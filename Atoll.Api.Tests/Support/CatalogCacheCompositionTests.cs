using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atoll.Api.Tests.Support;

// Write-path catalog invalidation depends on DI handing the singleton PackageService the same
// HybridCache the catalog reads from. That constructor parameter is optional, so a dropped
// registration would silently stop invalidating rather than fail at startup, and per-test
// construction cannot catch it: only the composed host can.
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

    private static Task<CatalogResult> SearchSeededAsync(PackageCatalogService catalog, CancellationToken ct) =>
        catalog.SearchAsync(
            null,
            CatalogSeededFilter.Seeded,
            CatalogSecurityFilter.Any,
            CatalogSearchMode.Name,
            CatalogSort.NameAsc,
            ct: ct);
}
