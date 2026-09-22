using Atoll.Api.Services.Security;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Refresh;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Refresh;

public class UpstreamPackageReconcilerTests
{
    private static readonly IReadOnlyDictionary<string, string> Files =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = "pkgname=demo\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = demo\n"
        };

    [Fact]
    public async Task ReconcileAsync_deletes_local_packages_absent_from_upstream_and_all_derived_state()
    {
        var repository = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var reposRoot = Path.Combine(Path.GetTempPath(), $"atoll-reconcile-{Guid.NewGuid():N}");
        var options = Options.Create(new AtollOptions
        {
            DataSource = new DataSourceOptions { PruneDeletedPackages = true },
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = reposRoot }
        });
        var cache = new GitRepositoryCache(repository, security, options, NullLogger<GitRepositoryCache>.Instance);
        var service = new PackageService(repository, options, security, new PkgBuildSecurityScanner(), cache, TestHybridCache.New(), new PackageIndexStore());
        var reconciler = new UpstreamPackageReconciler(
            service,
            options,
            NullLogger<UpstreamPackageReconciler>.Instance);

        try
        {
            await service.SeedFilesAsync("kept", Files);
            await service.SeedFilesAsync("removed", Files);
            var removedRepo = cache.GetRepositoryPath("removed")!;
            Directory.CreateDirectory(removedRepo);
            await File.WriteAllTextAsync(Path.Combine(removedRepo, "HEAD"), "stale", TestContext.Current.CancellationToken);

            var deleted = await reconciler.ReconcileAsync(["kept"], 0, CancellationToken.None);
            var keptExists = await repository.ExistsAsync("kept", TestContext.Current.CancellationToken);
            var removedExists = await repository.ExistsAsync("removed", TestContext.Current.CancellationToken);
            var removedScans = await security.ListForPackageAsync("removed", TestContext.Current.CancellationToken);

            Assert.Multiple(() =>
            {
                Assert.Equal(1, deleted);
                Assert.True(keptExists);
                Assert.False(removedExists);
                Assert.Empty(removedScans);
                Assert.False(Directory.Exists(removedRepo));
            });
        }
        finally
        {
            if (Directory.Exists(reposRoot))
                Directory.Delete(reposRoot, true);
        }
    }

    [Fact]
    public async Task ReconcileAsync_deletes_nothing_when_pruning_is_disabled()
    {
        var repository = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var options = Options.Create(new AtollOptions
        {
            DataSource = new DataSourceOptions { PruneDeletedPackages = false },
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = string.Empty }
        });
        var service = new PackageService(repository, options, security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repository, security, options, NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), new PackageIndexStore());
        var reconciler = new UpstreamPackageReconciler(
            service,
            options,
            NullLogger<UpstreamPackageReconciler>.Instance);
        await service.SeedFilesAsync("kept", Files);
        await service.SeedFilesAsync("removed", Files);

        var deleted = await reconciler.ReconcileAsync(["kept"], 0, CancellationToken.None);
        var keptExists = await repository.ExistsAsync("kept", TestContext.Current.CancellationToken);
        var removedExists = await repository.ExistsAsync("removed", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(0, deleted);
            Assert.True(keptExists);
            Assert.True(removedExists);
        });
    }

    [Fact]
    public async Task ReconcileAsync_defers_pruning_once_when_snapshot_shrinks_abruptly()
    {
        var repository = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var options = Options.Create(new AtollOptions
        {
            DataSource = new DataSourceOptions { PruneDeletedPackages = true },
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = string.Empty }
        });
        var service = new PackageService(repository, options, security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repository, security, options, NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), new PackageIndexStore());
        var reconciler = new UpstreamPackageReconciler(
            service,
            options,
            NullLogger<UpstreamPackageReconciler>.Instance);
        await service.SeedFilesAsync("removed", Files);

        var firstDeleted = await reconciler.ReconcileAsync(["kept"], 100, CancellationToken.None);
        var secondDeleted = await reconciler.ReconcileAsync(["kept"], 1, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(0, firstDeleted);
            Assert.Equal(1, secondDeleted);
        });
    }
}
