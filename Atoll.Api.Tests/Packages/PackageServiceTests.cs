using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Packages;

public class PackageServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> SampleFiles =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        };

    private static PackageService CreateService(
        InMemoryPackageRepository repo,
        IPackageSecurityRepository? securityRepository = null)
    {
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        });
        return new PackageService(repo, options, securityRepository ?? new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, securityRepository ?? new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance));
    }

    [Test]
    public async Task SeedFilesAsync_then_GetAsync_returns_files()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var files = await service.GetAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.That(files.Files.Keys, Is.EquivalentTo(SampleFiles.Keys));
            Assert.That(files.Files["PKGBUILD"], Is.EqualTo(SampleFiles["PKGBUILD"]));
            Assert.That(files.Files[".SRCINFO"], Is.EqualTo(SampleFiles[".SRCINFO"]));
        });
    }

    [Test]
    public async Task SeedFilesAsync_then_GetHistoryAsync_returns_one_revision()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var history = await service.GetHistoryAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.That(history, Has.Count.EqualTo(1));
            Assert.That(history[0].Sha, Has.Length.EqualTo(64));
            Assert.That(history[0].Author, Is.EqualTo("aur"));
            Assert.That(history[0].Message, Is.EqualTo("seed from AUR"));
        });
    }

    [Test]
    public async Task SeedFilesAsync_then_GetAsync_by_revision_sha_returns_files()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var history = await service.GetHistoryAsync("shelly");
        var sha = history[0].Sha;

        var byRevision = await service.GetAsync("shelly", sha);

        Assert.Multiple(() =>
        {
            Assert.That(byRevision.Files.Keys, Is.EquivalentTo(SampleFiles.Keys));
            Assert.That(byRevision.Files["PKGBUILD"], Is.EqualTo(SampleFiles["PKGBUILD"]));
        });
    }

    [Test]
    public void SeedFilesAsync_existing_package_returns_conflict()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        Assert.DoesNotThrowAsync(async () => await service.SeedFilesAsync("shelly", SampleFiles));

        var ex = Assert.ThrowsAsync<PackageConflictException>(async () => await service.SeedFilesAsync("shelly", SampleFiles))!;

        Assert.That(ex.PackageName, Is.EqualTo("shelly"));
    }

    [Test]
    public async Task SeedFilesAsync_oversized_file_throws()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 1_024, MaxRevisions = 10 }
        });
        var service = new PackageService(repo, options, new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance));

        var big = new Dictionary<string, string>
        {
            ["big.bin"] = new('x', 2_048)
        };

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await service.SeedFilesAsync("big-pkg", big))!;

        Assert.That(ex.Message, Does.Contain("big.bin"));
        Assert.That(await repo.ExistsAsync("big-pkg"), Is.False);
    }

    [Test]
    public async Task SeedFilesAsync_document_larger_than_mongo_limit_throws_typed_exception_before_insert()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 10_485_760, MaxRevisions = 10 }
        });
        var service = new PackageService(repo, options, new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance));
        var files = new Dictionary<string, string>
        {
            ["large-1.txt"] = new('x', 9_000_000),
            ["large-2.txt"] = new('x', 9_000_000)
        };

        var ex = Assert.ThrowsAsync<PackageDocumentTooLargeException>(async () => await service.SeedFilesAsync("too-large", files))!;
        var packageExists = await repo.ExistsAsync("too-large");

        Assert.Multiple(() =>
        {
            Assert.That(ex.PackageName, Is.EqualTo("too-large"));
            Assert.That(ex.SerializedSizeBytes, Is.GreaterThan(ex.MaxDocumentSizeBytes));
            Assert.That(packageExists, Is.False);
        });
    }

    [Test]
    public async Task AppendRevisionFromUpstreamAsync_oversized_snapshot_throws_before_write()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 10_485_760, MaxRevisions = 10 }
        });
        var service = new PackageService(repo, options, new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance));
        await service.SeedFilesAsync("pkg", SampleFiles);
        var history = await service.GetHistoryAsync("pkg");
        var originalHead = history[0].Sha;
        var oversizedFiles = new Dictionary<string, string>
        {
            ["large-1.txt"] = new('x', 9_000_000),
            ["large-2.txt"] = new('x', 9_000_000)
        };

        Assert.ThrowsAsync<PackageDocumentTooLargeException>(async () =>
            await service.AppendRevisionFromUpstreamAsync("pkg", oversizedFiles));
        var afterHistory = await service.GetHistoryAsync("pkg");
        var packageExists = await repo.ExistsAsync("pkg");

        Assert.Multiple(() =>
        {
            Assert.That(afterHistory, Has.Count.EqualTo(1));
            Assert.That(afterHistory[0].Sha, Is.EqualTo(originalHead));
            Assert.That(packageExists, Is.True);
        });
    }

    [Test]
    public async Task DeleteAsync_then_GetAsync_throws_not_found()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        await service.DeleteAsync("shelly");

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("shelly"));
    }

    [Test]
    public async Task GetAsync_unknown_package_throws_not_found()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("missing"));
    }

    [Test]
    public async Task GetAsync_unknown_revision_throws_not_found()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("shelly", "deadbeef"));
    }

    [Test]
    public async Task ListAsync_returns_seeded_package_names()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        await service.SeedFilesAsync("other", SampleFiles);

        var names = await service.ListAsync();

        Assert.That(names, Is.EquivalentTo(["shelly", "other"]));
    }

    [Test]
    public async Task SeedFilesAsync_same_content_produces_same_revision_sha()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var firstHistory = await service.GetHistoryAsync("shelly");

        var repo2 = new InMemoryPackageRepository();
        var service2 = CreateService(repo2);
        await service2.SeedFilesAsync("shelly", SampleFiles);
        var secondHistory = await service2.GetHistoryAsync("shelly");

        Assert.That(firstHistory[0].Sha, Is.EqualTo(secondHistory[0].Sha));
    }

    [Test]
    public async Task SeedFilesAsync_estimate_above_limit_but_exact_bson_under_limit_is_accepted()
    {
        // Two files totalling 16,776,000 content bytes: the conservative estimate
        // (content + 160/file + 1024) exceeds the 16 MiB BSON limit, but the exact
        // ToBson() measurement stays under it, so the exact second pass must admit it.
        const int perFile = 8_388_000;
        var estimated = 2L * perFile + 2 * (160 + "large-1.txt".Length) + 1024;
        Assert.That(estimated, Is.GreaterThan(16 * 1024 * 1024),
            "precondition: the conservative estimate must exceed the BSON limit");

        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 10_485_760, MaxRevisions = 10 }
        });
        var service = new PackageService(repo, options, new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance));
        var files = new Dictionary<string, string>
        {
            ["large-1.txt"] = new('x', perFile),
            ["large-2.txt"] = new('x', perFile)
        };

        await service.SeedFilesAsync("boundary", files);

        var persisted = await service.GetAsync("boundary");
        var packageExists = await repo.ExistsAsync("boundary");
        Assert.Multiple(() =>
        {
            Assert.That(packageExists, Is.True);
            Assert.That(persisted.Files["large-1.txt"], Has.Length.EqualTo(perFile));
        });
    }

    [Test]
    public async Task DeleteAsync_failure_after_derived_cleanup_leaves_package_deletable_again()
    {
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var reposRoot = Path.Combine(Path.GetTempPath(), $"atoll-delete-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reposRoot);
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = reposRoot }
        });
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var service = new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache);
        var failingOnce = new ThrowOnceOnDeleteRepository(repo);
        var retryService = new PackageService(failingOnce, options, security, new PkgBuildSecurityScanner(), cache);

        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            Assert.That(await security.CountPendingAsync(), Is.EqualTo(1));
            var repoDir = cache.GetRepositoryPath("shelly")!;
            Directory.CreateDirectory(repoDir);
            await File.WriteAllTextAsync(Path.Combine(repoDir, "HEAD"), "marker-for-cleanup");

            Assert.ThrowsAsync<InvalidOperationException>(async () => await retryService.DeleteAsync("shelly"));

            var remainingScans = await security.ListForPackageAsync("shelly");
            var packageStillExists = await repo.ExistsAsync("shelly");
            Assert.Multiple(() =>
            {
                // Derived state is removed before the authoritative document, so the failed
                // delete leaves no orphaned scan records or on-disk cache...
                Assert.That(remainingScans, Is.Empty);
                Assert.That(Directory.Exists(repoDir), Is.False);
                // ...while the package document survives, keeping the delete retryable.
                Assert.That(packageStillExists, Is.True);
            });

            await retryService.DeleteAsync("shelly");

            Assert.That(await repo.ExistsAsync("shelly"), Is.False);
        }
        finally
        {
            try
            {
                if (Directory.Exists(reposRoot))
                    Directory.Delete(reposRoot, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private sealed class ThrowOnceOnDeleteRepository(IPackageRepository inner) : IPackageRepository
    {
        public bool HasThrown { get; private set; }

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
        {
            return inner.ListAsync(ct);
        }

        public Task<long> CountAsync(CancellationToken ct = default)
        {
            return inner.CountAsync(ct);
        }

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
        {
            return inner.ExistsAsync(packageName, ct);
        }

        public Task<PackageDocument?> GetHeadAsync(string packageName, CancellationToken ct = default)
        {
            return inner.GetHeadAsync(packageName, ct);
        }

        public Task<string?> GetHeadRevisionIdAsync(string packageName, CancellationToken ct = default)
        {
            return inner.GetHeadRevisionIdAsync(packageName, ct);
        }

        public Task<PackageRevisionContentDocument?> GetRevisionAsync(
            string packageName, string revisionId, CancellationToken ct = default)
        {
            return inner.GetRevisionAsync(packageName, revisionId, ct);
        }

        public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName, CancellationToken ct = default)
        {
            return inner.GetHistoryAsync(packageName, ct);
        }

        public Task InsertSeedAsync(
            PackageDocument doc, PackageRevisionContentDocument revision, CancellationToken ct = default)
        {
            return inner.InsertSeedAsync(doc, revision, ct);
        }

        public Task AppendRevisionAsync(
            string packageName, PackageRevisionContentDocument revision, int maxRevisions,
            CancellationToken ct = default)
        {
            return inner.AppendRevisionAsync(packageName, revision, maxRevisions, ct);
        }

        public Task<IReadOnlyList<PackageSyncState>> ListSyncStatesAsync(CancellationToken ct = default)
        {
            return inner.ListSyncStatesAsync(ct);
        }

        public Task UpdateSyncStateAsync(
            IReadOnlyCollection<string> packageNames, string? upstreamHead, bool succeeded, string? error,
            CancellationToken ct = default)
        {
            return inner.UpdateSyncStateAsync(packageNames, upstreamHead, succeeded, error, ct);
        }

        public Task DeleteAsync(string packageName, CancellationToken ct = default)
        {
            if (!HasThrown)
            {
                HasThrown = true;
                throw new InvalidOperationException("simulated delete failure");
            }

            return inner.DeleteAsync(packageName, ct);
        }
    }
}