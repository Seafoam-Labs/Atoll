using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Packages.Git;

[Category("RequiresGit")]
public class GitRepositoryMaterializationTests
{
    private static readonly IReadOnlyDictionary<string, string> SampleFiles =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        };

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static async Task<bool> GitIsAvailable()
    {
        var (exitCode, _) = await GitClient.TryExecuteAsync(["--version"], CancellationToken.None);
        return exitCode == 0;
    }

    private static string CreateTempReposRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"atoll-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static (PackageService service, GitRepositoryCache cache, IPackageSecurityRepository security, string reposRoot)
        CreateService()
    {
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var reposRoot = CreateTempReposRoot();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = reposRoot }
        });
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        return (new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache), cache, security, reposRoot);
    }

    [SetUp]
    public async Task SetUp()
    {
        Assume.That(await GitIsAvailable(), "git binary is required for these tests");
    }

    [Test]
    public async Task EnsureRepositoryAsync_creates_bare_repo_with_main_branch()
    {
        var (service, cache, security, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly");

            var gitDir = cache.GetRepositoryPath("shelly")!;
            Assert.That(Directory.Exists(gitDir), Is.True);
            Assert.That((await File.ReadAllTextAsync(Path.Combine(gitDir, "HEAD"))).Trim(),
                Is.EqualTo("ref: refs/heads/main"));

            string[] args = ["rev-parse", "refs/heads/main"];
            var refSha = (await GitClient.ExecuteAsync(gitDir, args, null, null, CancellationToken.None)).Trim();
            Assert.That(refSha, Has.Length.EqualTo(40));
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Test]
    public async Task EnsureRepositoryAsync_is_idempotent_when_head_unchanged()
    {
        var (service, cache, security, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly");
            var gitDir = cache.GetRepositoryPath("shelly")!;
            var marker = Path.Combine(gitDir, ".atoll-head");
            var firstMarkerWrite = File.GetLastWriteTimeUtc(marker);

            await Task.Delay(50);
            await cache.EnsureRepositoryAsync("shelly");

            var secondMarkerWrite = File.GetLastWriteTimeUtc(marker);
            Assert.That(secondMarkerWrite, Is.EqualTo(firstMarkerWrite),
                "marker file should not be rewritten when head revision is unchanged");
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Test]
    public async Task EnsureRepositoryAsync_produces_cloneable_repo_with_expected_files()
    {
        var (service, cache, security, reposRoot) = CreateService();
        var cloneDir = Path.Combine(Path.GetTempPath(),
            $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly");
            var gitDir = cache.GetRepositoryPath("shelly")!;

            string[] args = ["clone", "--quiet", gitDir, cloneDir];
            await GitClient.ExecuteAsync(Directory.GetCurrentDirectory(), args, null, null, CancellationToken.None);

            foreach (var (name, content) in SampleFiles)
            {
                var fullPath = Path.Combine(cloneDir, name);
                Assert.That(File.Exists(fullPath), Is.True, $"missing {name}");
                Assert.That(await File.ReadAllTextAsync(fullPath), Is.EqualTo(content));
            }

            string[] args1 = ["rev-list", "--count", "HEAD"];
            var logCount = (await GitClient.ExecuteAsync(cloneDir, args1, null, null, CancellationToken.None)).Trim();
            Assert.That(logCount, Is.EqualTo("1"));
        }
        finally
        {
            TryCleanup(reposRoot);
            TryCleanup(cloneDir);
        }
    }

    [Test]
    public async Task EnsureRepositoryAsync_returns_silently_for_unknown_package()
    {
        var (_, cache, _, reposRoot) = CreateService();
        try
        {
            Assert.DoesNotThrowAsync(async () =>
                await cache.EnsureRepositoryAsync("does-not-exist"));
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Test]
    public async Task EnsureRepositoryAsync_returns_silently_when_no_path_configured()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = "" }
        });
        var security = new InMemoryPackageSecurityRepository();
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var service = new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache);

        await service.SeedFilesAsync("shelly", SampleFiles);

        Assert.DoesNotThrowAsync(async () =>
            await cache.EnsureRepositoryAsync("shelly"));
        Assert.That(cache.GetRepositoryPath("shelly"), Is.Null);
    }

    [Test]
    public async Task Flagged_revision_is_excluded_from_git_history_until_rescanned()
    {
        var (service, cache, security, reposRoot) = CreateService();
        var cloneDir = Path.Combine(Path.GetTempPath(), $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            // Revision 1: clean and verified.
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");

            // Revision 2 (new head): scan completes Flagged.
            var revision2 = new Dictionary<string, string>
            {
                ["PKGBUILD"] = "pkgname=shelly\npkgver=2.0\nsource=(\"https://example.com/install.sh\")\n",
                [".SRCINFO"] = "pkgname = shelly\n"
            };
            Assert.That(await service.AppendRevisionFromUpstreamAsync("shelly", revision2), Is.True);
            var flaggedRevision = await security.CompleteScanAsync("shelly", SecurityStatus.Flagged,
                new SecurityFinding("network-download", FindingSeverity.Critical, "test", "curl | sh", "PKGBUILD"));

            await cache.EnsureRepositoryAsync("shelly");
            var (commits, pkgbuild) = await CloneAndInspectAsync(cache, "shelly", cloneDir);
            Assert.Multiple(() =>
            {
                Assert.That(commits, Is.EqualTo(1), "the flagged head revision must not be cloneable");
                Assert.That(pkgbuild, Does.Contain("pkgver=1.0"), "clone must fall back to the last verified revision");
            });

            // Rescan the flagged head to Verified; the marker must change and the lazy
            // rebuild must restore the revision to the cloneable history.
            await security.MarkPendingAsync("shelly", flaggedRevision, true, PkgBuildSecurityScanner.CurrentPolicyVersion);
            await security.MarkHeadVerifiedAsync("shelly");

            await cache.EnsureRepositoryAsync("shelly");
            TryCleanup(cloneDir);
            (commits, pkgbuild) = await CloneAndInspectAsync(cache, "shelly", cloneDir);
            Assert.Multiple(() =>
            {
                Assert.That(commits, Is.EqualTo(2), "a rescan to Verified must restore the revision to history");
                Assert.That(pkgbuild, Does.Contain("pkgver=2.0"));
            });
        }
        finally
        {
            TryCleanup(reposRoot);
            TryCleanup(cloneDir);
        }
    }

    [Test]
    public async Task Flagged_ancestor_is_excluded_when_head_is_verified()
    {
        var (service, cache, security, reposRoot) = CreateService();
        var cloneDir = Path.Combine(Path.GetTempPath(), $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            // Revision 1: scan completes Flagged.
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.CompleteScanAsync("shelly", SecurityStatus.Flagged,
                new SecurityFinding("network-download", FindingSeverity.Critical, "test", "curl | sh", "PKGBUILD"));

            // Revision 2 (new head): clean and verified.
            var revision2 = new Dictionary<string, string>
            {
                ["PKGBUILD"] = "pkgname=shelly\npkgver=2.0\n",
                [".SRCINFO"] = "pkgname = shelly\n"
            };
            Assert.That(await service.AppendRevisionFromUpstreamAsync("shelly", revision2), Is.True);
            await security.MarkHeadVerifiedAsync("shelly");

            await cache.EnsureRepositoryAsync("shelly");
            var (commits, pkgbuild) = await CloneAndInspectAsync(cache, "shelly", cloneDir);
            Assert.Multiple(() =>
            {
                Assert.That(commits, Is.EqualTo(1),
                    "a flagged ancestor must not be cloneable even when the head verifies");
                Assert.That(pkgbuild, Does.Contain("pkgver=2.0"));
            });
        }
        finally
        {
            TryCleanup(reposRoot);
            TryCleanup(cloneDir);
        }
    }

    [Test]
    public async Task Concurrent_materialize_and_delete_never_resurrects_the_repository()
    {
        var (service, cache, security, reposRoot) = CreateService();
        try
        {
            var repoDir = cache.GetRepositoryPath("shelly")!;
            for (var i = 0; i < 10; i++)
            {
                await service.SeedFilesAsync("shelly", SampleFiles);
                await security.MarkHeadVerifiedAsync("shelly");

                // Materializers race a full delete. Deletion and materialization share one
                // per-repository lock and the delete removes the authoritative document
                // while holding it, so a late materializer must re-read a missing head and
                // bail instead of rebuilding the directory after the delete committed.
                await Task.WhenAll(
                    cache.EnsureRepositoryAsync("shelly"),
                    cache.EnsureRepositoryAsync("shelly"),
                    service.DeleteAsync("shelly"));

                Assert.That(Directory.Exists(repoDir), Is.False,
                    $"iteration {i}: the deleted repository must not be resurrected");
                Assert.That(await service.ExistsAsync("shelly"), Is.False,
                    $"iteration {i}: the package document must be gone");
            }
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    private static async Task<(int Commits, string Pkgbuild)> CloneAndInspectAsync(
        GitRepositoryCache cache, string packageName, string cloneDir)
    {
        var gitDir = cache.GetRepositoryPath(packageName)!;
        string[] args = ["clone", "--quiet", gitDir, cloneDir];
        await GitClient.ExecuteAsync(Directory.GetCurrentDirectory(), args, null, null, CancellationToken.None);

        var count = (await GitClient.ExecuteAsync(cloneDir, ["rev-list", "--count", "HEAD"], null, null,
            CancellationToken.None)).Trim();
        var pkgbuild = await GitClient.ExecuteAsync(cloneDir, ["show", "HEAD:PKGBUILD"], null, null,
            CancellationToken.None);
        return (int.Parse(count), pkgbuild);
    }

    private static void TryCleanup(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // ignore
        }
    }

    [Test]
    public async Task Missing_revision_content_materializes_remaining_history_without_marker()
    {
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var reposRoot = CreateTempReposRoot();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = reposRoot }
        });
        var hidingRepo = new RevisionHidingRepository(repo, "rev-1");
        var cache = new GitRepositoryCache(hidingRepo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var service = new PackageService(hidingRepo, options, security, new PkgBuildSecurityScanner(), cache);
        try
        {
            await repo.InsertSeedAsync(
                new PackageDocument
                {
                    Id = "pkg",
                    PackageName = "pkg",
                    CreatedAt = T0,
                    UpdatedAt = T0,
                    HeadRevisionId = "rev-1",
                    Revisions = [new PackageRevisionDocument { RevisionId = "rev-1", CreatedAt = T0 }]
                },
                new PackageRevisionContentDocument
                {
                    Id = PackageSchema.RevisionDocumentId("pkg", "rev-1"),
                    PackageName = "pkg",
                    RevisionId = "rev-1",
                    CreatedAt = T0,
                    Files = new Dictionary<string, PackageFile>
                    {
                        ["PKGBUILD"] = new() { Content = "pkgname=pkg\npkgver=1.0\n", Size = 0, Hash = "unused" }
                    }
                });
            await repo.AppendRevisionAsync("pkg", new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId("pkg", "rev-2"),
                PackageName = "pkg",
                RevisionId = "rev-2",
                CreatedAt = T1,
                Files = new Dictionary<string, PackageFile>
                {
                    ["PKGBUILD"] = new() { Content = "pkgname=pkg\npkgver=2.0\n", Size = 0, Hash = "unused" }
                }
            }, 10);

            await security.MarkPendingAsync("pkg", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
            await security.CompleteScanAsync("pkg", SecurityStatus.Verified);
            await security.MarkPendingAsync("pkg", "rev-2", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
            await security.CompleteScanAsync("pkg", SecurityStatus.Verified);

            await cache.EnsureRepositoryAsync("pkg");

            var gitDir = cache.GetRepositoryPath("pkg")!;
            var ct = CancellationToken.None;
            var count = (await GitClient.ExecuteAsync(gitDir, ["rev-list", "--count", "main"], null, null, ct)).Trim();
            var pkgbuild = await GitClient.ExecuteAsync(gitDir, ["show", "main:PKGBUILD"], null, null, ct);

            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo("1"),
                    "the revision whose content document is missing must be skipped");
                Assert.That(pkgbuild, Is.EqualTo("pkgname=pkg\npkgver=2.0\n"),
                    "the remaining revision becomes a parentless head commit");
                Assert.That(File.Exists(Path.Combine(gitDir, ".atoll-head")), Is.False,
                    "an incomplete materialization must not write the marker, so the next request retries");
            });
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    /// <summary>
    ///     Simulates a lost revision content document: the head document still lists the
    ///     revision, but GetRevisionAsync cannot serve it.
    /// </summary>
    private sealed class RevisionHidingRepository(IPackageRepository inner, string hiddenRevisionId) : IPackageRepository
    {
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
            return revisionId == hiddenRevisionId
                ? Task.FromResult<PackageRevisionContentDocument?>(null)
                : inner.GetRevisionAsync(packageName, revisionId, ct);
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
            return inner.DeleteAsync(packageName, ct);
        }
    }
}