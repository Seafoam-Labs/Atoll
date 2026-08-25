using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Search.Refresh;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Search.Refresh;

public class UpstreamPackageReconcilerTests
{
    private static readonly IReadOnlyDictionary<string, string> Files =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=demo\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = demo\n"
        };

    [Test]
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
        var service = new MongoPackageService(
            repository,
            new PackageIndexStore(),
            options,
            security,
            NullLogger<MongoPackageService>.Instance);
        var reconciler = new UpstreamPackageReconciler(
            service,
            options,
            NullLogger<UpstreamPackageReconciler>.Instance);

        try
        {
            await service.SeedFilesAsync("kept", Files);
            await service.SeedFilesAsync("removed", Files);
            var removedRepo = service.GetRepositoryPath("removed")!;
            Directory.CreateDirectory(removedRepo);
            await File.WriteAllTextAsync(Path.Combine(removedRepo, "HEAD"), "stale");

            var deleted = await reconciler.ReconcileAsync(["kept"], 0, CancellationToken.None);
            var keptExists = await repository.ExistsAsync("kept");
            var removedExists = await repository.ExistsAsync("removed");
            var removedScans = await security.ListForPackageAsync("removed");

            Assert.Multiple(() =>
            {
                Assert.That(deleted, Is.EqualTo(1));
                Assert.That(keptExists, Is.True);
                Assert.That(removedExists, Is.False);
                Assert.That(removedScans, Is.Empty);
                Assert.That(Directory.Exists(removedRepo), Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(reposRoot))
                Directory.Delete(reposRoot, true);
        }
    }

    [Test]
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
        var service = new MongoPackageService(
            repository,
            new PackageIndexStore(),
            options,
            security,
            NullLogger<MongoPackageService>.Instance);
        var reconciler = new UpstreamPackageReconciler(
            service,
            options,
            NullLogger<UpstreamPackageReconciler>.Instance);
        await service.SeedFilesAsync("kept", Files);
        await service.SeedFilesAsync("removed", Files);

        var deleted = await reconciler.ReconcileAsync(["kept"], 0, CancellationToken.None);
        var keptExists = await repository.ExistsAsync("kept");
        var removedExists = await repository.ExistsAsync("removed");

        Assert.Multiple(() =>
        {
            Assert.That(deleted, Is.Zero);
            Assert.That(keptExists, Is.True);
            Assert.That(removedExists, Is.True);
        });
    }

    [Test]
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
        var service = new MongoPackageService(
            repository,
            new PackageIndexStore(),
            options,
            security,
            NullLogger<MongoPackageService>.Instance);
        var reconciler = new UpstreamPackageReconciler(
            service,
            options,
            NullLogger<UpstreamPackageReconciler>.Instance);
        await service.SeedFilesAsync("removed", Files);

        var firstDeleted = await reconciler.ReconcileAsync(["kept"], 100, CancellationToken.None);
        var secondDeleted = await reconciler.ReconcileAsync(["kept"], 1, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstDeleted, Is.Zero);
            Assert.That(secondDeleted, Is.EqualTo(1));
        });
    }
}
