using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Support;

// Write-path catalog invalidation and the ranker's cached name list both depend on DI handing the
// singleton PackageService the HybridCache the catalog reads from. The constructor parameter is
// required, so a dropped registration now fails at startup; this is the end-to-end check that the
// composed host shares one cache instance between the writers and the readers.
public class CatalogCacheCompositionTests
{
    [Fact]
    public async Task Seeding_through_the_host_package_service_refreshes_the_catalog_snapshot()
    {
        await using var factory = new SecurityTestFactory();
        var catalog = factory.Services.GetRequiredService<PackageCatalogService>();
        var packages = factory.Services.GetRequiredService<IPackageService>();
        var ct = TestContext.Current.CancellationToken;

        // Warms the snapshot, so only an invalidation can make the seeded row appear.
        Assert.Empty((await SearchSeededAsync(catalog, ct)).Rows);

        await packages.SeedFilesAsync("shelly-bin", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = "pkgname=shelly-bin\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly-bin\n"
        });

        Assert.Equal(["shelly-bin"], (await SearchSeededAsync(catalog, ct)).Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_rest_rescan_through_the_host_refreshes_the_catalog_snapshot()
    {
        await using var factory = new SecurityTestFactory();
        var catalog = factory.Services.GetRequiredService<PackageCatalogService>();
        var packages = factory.Services.GetRequiredService<IPackageService>();
        var ct = TestContext.Current.CancellationToken;

        await packages.SeedFilesAsync("shelly-bin", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = "pkgname=shelly-bin\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly-bin\n"
        });
        await factory.SecurityRepository.MarkHeadVerifiedAsync("shelly-bin");

        // Warms the snapshot, so only an invalidation can flip the reported head status.
        Assert.Equal(SecurityStatus.Verified, (await SearchSeededAsync(catalog, ct)).Rows.Single().Head!.Status);

        // No worker runs under this factory, so nothing scans the queued revision back.
        var response = await factory.CreateClient()
            .PostAsync("/v1/packages/shelly-bin/security/rescan", null, ct);
        response.EnsureSuccessStatusCode();

        Assert.Equal(SecurityStatus.Pending, (await SearchSeededAsync(catalog, ct)).Rows.Single().Head!.Status);
    }

    [Fact]
    public async Task Sorted_page_through_the_host_package_service_lists_the_names_once()
    {
        await using var factory = new SecurityTestFactory();
        var packages = factory.Services.GetRequiredService<IPackageService>();
        var ct = TestContext.Current.CancellationToken;
        var listsBefore = factory.Repository.ListCalls;

        await packages.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, ct);
        await packages.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, ct);

        Assert.Equal(1, factory.Repository.ListCalls - listsBefore);
    }

    [Fact]
    public void The_host_raises_the_payload_cap_above_the_stock_default()
    {
        // The ranker stores full-corpus name arrays (~2.5 MB at 119k names). The stock 1 MB cap does
        // not refuse the store but logs one Error per store, so the host must raise it.
        using var factory = new SecurityTestFactory();
        var options = factory.Services.GetRequiredService<IOptions<HybridCacheOptions>>().Value;

        Assert.True(options.MaximumPayloadBytes > 1024 * 1024);
    }

    private static Task<CatalogResult> SearchSeededAsync(PackageCatalogService catalog, CancellationToken ct) =>
        catalog.SearchAsync(
            null,
            CatalogSeededFilter.Seeded,
            CatalogSecurityFilter.Any,
            CatalogSearchMode.Name,
            CatalogSort.NameAsc,
            ct: ct);
}
