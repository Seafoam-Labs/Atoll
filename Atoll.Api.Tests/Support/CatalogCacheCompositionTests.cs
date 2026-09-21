using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atoll.Api.Tests.Support;

// Write-path catalog invalidation and the ranker's cached name list both depend on DI handing the
// singleton PackageService the HybridCache the catalog reads from. That constructor parameter is
// optional, so a dropped registration would silently fall back to the uncached path rather than
// fail at startup, and per-test construction cannot catch it: only the composed host can.
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

        await packages.SeedFilesAsync("shelly-bin", new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=shelly-bin\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly-bin\n"
        });

        Assert.Equal(["shelly-bin"], (await SearchSeededAsync(catalog, ct)).Rows.Select(row => row.Package.Name));
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

    private static Task<CatalogResult> SearchSeededAsync(PackageCatalogService catalog, CancellationToken ct) =>
        catalog.SearchAsync(
            null,
            CatalogSeededFilter.Seeded,
            CatalogSecurityFilter.Any,
            CatalogSearchMode.Name,
            CatalogSort.NameAsc,
            ct: ct);
}
