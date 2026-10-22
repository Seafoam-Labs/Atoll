using System.Globalization;
using System.Text;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Packages.Git;

[Trait("Category", "RequiresGit")]
public sealed class GitRepositoryMaterializationTests : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, byte[]> SampleFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        }.AsByteFiles();

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static async Task<bool> GitIsAvailableAsync()
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
        return (new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache, TestHybridCache.New(), new PackageIndexStore()), cache, security, reposRoot);
    }

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(await GitIsAvailableAsync(), "git binary is required for these tests");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EnsureRepositoryAsync_CreatesBareRepoWithMainBranch()
    {
        var (service, cache, security, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);

            var gitDir = cache.GetRepositoryPath("shelly")!;
            Assert.True(Directory.Exists(gitDir));
            Assert.Equal("ref: refs/heads/main",
                (await File.ReadAllTextAsync(Path.Combine(gitDir, "HEAD"), TestContext.Current.CancellationToken)).Trim());

            string[] args = ["rev-parse", "refs/heads/main"];
            var refSha = (await GitClient.ExecuteAsync(gitDir, args, null, null, CancellationToken.None)).Trim();
            Assert.Equal(40, refSha.Length);
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_UnchangedHead_DoesNotRewriteMarker()
    {
        var (service, cache, security, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);
            var gitDir = cache.GetRepositoryPath("shelly")!;
            var marker = Path.Combine(gitDir, ".atoll-head");
            var firstMarkerWrite = File.GetLastWriteTimeUtc(marker);

            await Task.Delay(50, TestContext.Current.CancellationToken);
            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);

            var secondMarkerWrite = File.GetLastWriteTimeUtc(marker);
            Assert.Equal(firstMarkerWrite, secondMarkerWrite);
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_SeededPackage_ProducesCloneableRepoWithExpectedFiles()
    {
        var (service, cache, security, reposRoot) = CreateService();
        var cloneDir = Path.Combine(Path.GetTempPath(),
            $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);
            var gitDir = cache.GetRepositoryPath("shelly")!;

            string[] args = ["clone", "--quiet", gitDir, cloneDir];
            await GitClient.ExecuteAsync(Directory.GetCurrentDirectory(), args, null, null, CancellationToken.None);

            foreach (var (name, content) in SampleFiles)
            {
                var fullPath = Path.Combine(cloneDir, name);
                Assert.True(File.Exists(fullPath), $"missing {name}");
                Assert.Equal(content, await File.ReadAllBytesAsync(fullPath, TestContext.Current.CancellationToken));
            }

            string[] args1 = ["rev-list", "--count", "HEAD"];
            var logCount = (await GitClient.ExecuteAsync(cloneDir, args1, null, null, CancellationToken.None)).Trim();
            Assert.Equal("1", logCount);
        }
        finally
        {
            TryCleanup(reposRoot);
            TryCleanup(cloneDir);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_BinaryFile_ClonesByteExact()
    {
        var (service, cache, security, reposRoot) = CreateService();
        var cloneDir = Path.Combine(Path.GetTempPath(), $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0xFF];
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = Encoding.UTF8.GetBytes("pkgname=shelly\n"),
                ["icon64.png"] = png
            };

            await service.SeedFilesAsync("shelly", files);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);

            var gitDir = cache.GetRepositoryPath("shelly")!;
            string[] args = ["clone", "--quiet", gitDir, cloneDir];
            await GitClient.ExecuteAsync(Directory.GetCurrentDirectory(), args, null, null, CancellationToken.None);

            var cloned = await File.ReadAllBytesAsync(Path.Combine(cloneDir, "icon64.png"), TestContext.Current.CancellationToken);
            var blobSize = (await GitClient.ExecuteAsync(gitDir, ["cat-file", "-s", "main:icon64.png"], null, null,
                CancellationToken.None)).Trim();

            Assert.Multiple(() =>
            {
                Assert.Equal(png, cloned);
                Assert.Equal(png.Length.ToString(CultureInfo.InvariantCulture), blobSize);
            });
        }
        finally
        {
            TryCleanup(reposRoot);
            TryCleanup(cloneDir);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_ExecBit_RequiresRawShebang()
    {
        var (service, cache, security, reposRoot) = CreateService();
        try
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["script"] = Encoding.UTF8.GetBytes("#!/bin/sh\n"),
                ["bom-script"] = Encoding.UTF8.GetBytes("\uFEFF#!/bin/sh\n"),
                ["helper.sh"] = Encoding.UTF8.GetBytes("echo hi\n")
            };

            await service.SeedFilesAsync("shelly", files);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);

            var gitDir = cache.GetRepositoryPath("shelly")!;
            var tree = await GitClient.ExecuteAsync(gitDir, ["ls-tree", "main"], null, null, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.Contains("100755 blob", Line(tree, "script"), StringComparison.Ordinal);
                Assert.Contains("100644 blob", Line(tree, "bom-script"), StringComparison.Ordinal);
                Assert.Contains("100755 blob", Line(tree, "helper.sh"), StringComparison.Ordinal);
            });
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    private static string Line(string multiLine, string name)
    {
        return multiLine.Split('\n')
            .Single(line => line.EndsWith("\t" + name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureRepositoryAsync_UnknownPackage_ReturnsSilently()
    {
        var (_, cache, _, reposRoot) = CreateService();
        try
        {
            await cache.EnsureRepositoryAsync("does-not-exist", TestContext.Current.CancellationToken);
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_SeededPackage_PacksObjectStore()
    {
        var (service, cache, security, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);

            var gitDir = cache.GetRepositoryPath("shelly")!;
            var packDir = Path.Combine(gitDir, "objects", "pack");

            Assert.Multiple(() =>
            {
                Assert.Single(Directory.GetFiles(packDir, "*.pack"));
                Assert.Single(Directory.GetFiles(packDir, "*.bitmap"));
                Assert.Empty(LooseObjectDirectories(gitDir));
            });
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_AfterRevisionAppend_KeepsSinglePackfile()
    {
        var (service, cache, security, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);

            // A revision append changes the marker, so the next request re-materializes the
            // whole chain over an already-packed object store.
            var revision2 = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = "pkgname=shelly\npkgver=2.0\n",
                [".SRCINFO"] = "pkgname = shelly\n"
            }.AsByteFiles();
            Assert.True(await service.AppendRevisionFromUpstreamAsync("shelly", revision2, TestContext.Current.CancellationToken));
            await security.MarkHeadVerifiedAsync("shelly");

            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);

            var gitDir = cache.GetRepositoryPath("shelly")!;
            var packDir = Path.Combine(gitDir, "objects", "pack");
            var commits = (await GitClient.ExecuteAsync(gitDir, ["rev-list", "--count", "main"], null, null,
                CancellationToken.None)).Trim();

            Assert.Multiple(() =>
            {
                Assert.Equal("2", commits);
                Assert.Single(Directory.GetFiles(packDir, "*.pack"));
                Assert.Single(Directory.GetFiles(packDir, "*.bitmap"));
                Assert.Empty(LooseObjectDirectories(gitDir));
            });
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_UnsetRepositoriesPath_ReturnsSilently()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = "" }
        });
        var security = new InMemoryPackageSecurityRepository();
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var service = new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache, TestHybridCache.New(), new PackageIndexStore());

        await service.SeedFilesAsync("shelly", SampleFiles);

        await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);
        Assert.Null(cache.GetRepositoryPath("shelly"));
    }

    [Fact]
    public async Task EnsureRepositoryAsync_FlaggedHeadRevision_ExcludesUntilRescanned()
    {
        var (service, cache, security, reposRoot) = CreateService();
        var cloneDir = Path.Combine(Path.GetTempPath(), $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            // Revision 1: clean and verified.
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");

            // Revision 2 (new head): scan completes Flagged.
            var revision2 = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = "pkgname=shelly\npkgver=2.0\nsource=(\"https://example.com/install.sh\")\n",
                [".SRCINFO"] = "pkgname = shelly\n"
            }.AsByteFiles();
            Assert.True(await service.AppendRevisionFromUpstreamAsync("shelly", revision2, TestContext.Current.CancellationToken));
            var flaggedRevision = await security.CompleteScanAsync("shelly", SecurityStatus.Flagged,
                new SecurityFinding("network-download", FindingSeverity.Critical, "test", "curl | sh", "PKGBUILD"));

            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);
            var (commits, pkgbuild) = await CloneAndInspectAsync(cache, "shelly", cloneDir);
            Assert.Multiple(() =>
            {
                Assert.Equal(1, commits);
                Assert.Contains("pkgver=1.0", pkgbuild, StringComparison.Ordinal);
            });

            // Rescan the flagged head to Verified; the marker must change and the lazy
            // rebuild must restore the revision to the cloneable history.
            await security.MarkPendingAsync("shelly", flaggedRevision, true, PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);
            await security.MarkHeadVerifiedAsync("shelly");

            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);
            TryCleanup(cloneDir);
            (commits, pkgbuild) = await CloneAndInspectAsync(cache, "shelly", cloneDir);
            Assert.Multiple(() =>
            {
                Assert.Equal(2, commits);
                Assert.Contains("pkgver=2.0", pkgbuild, StringComparison.Ordinal);
            });
        }
        finally
        {
            TryCleanup(reposRoot);
            TryCleanup(cloneDir);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_FlaggedAncestor_ExcludesFromHistory()
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
            var revision2 = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = "pkgname=shelly\npkgver=2.0\n",
                [".SRCINFO"] = "pkgname = shelly\n"
            }.AsByteFiles();
            Assert.True(await service.AppendRevisionFromUpstreamAsync("shelly", revision2, TestContext.Current.CancellationToken));
            await security.MarkHeadVerifiedAsync("shelly");

            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);
            var (commits, pkgbuild) = await CloneAndInspectAsync(cache, "shelly", cloneDir);
            Assert.Multiple(() =>
            {
                Assert.Equal(1, commits);
                Assert.Contains("pkgver=2.0", pkgbuild, StringComparison.Ordinal);
            });
        }
        finally
        {
            TryCleanup(reposRoot);
            TryCleanup(cloneDir);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_RacingDelete_DoesNotResurrectRepository()
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
                    cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken),
                    cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken),
                    service.DeleteAsync("shelly", TestContext.Current.CancellationToken));

                Assert.False(Directory.Exists(repoDir),
                    $"iteration {i}: the deleted repository must not be resurrected");
                Assert.False(await service.ExistsAsync("shelly", TestContext.Current.CancellationToken),
                    $"iteration {i}: the package document must be gone");
            }
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    /// <summary>
    ///     Loose objects live in two-hex-character fanout directories under <c>objects/</c>; a packed
    ///     object store has none.
    /// </summary>
    private static string[] LooseObjectDirectories(string gitDir)
    {
        return Directory.GetDirectories(Path.Combine(gitDir, "objects"))
            .Where(dir =>
            {
                var name = Path.GetFileName(dir);
                return name.Length == 2 && name.All(char.IsAsciiHexDigit);
            })
            .ToArray();
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
        return (int.Parse(count, CultureInfo.InvariantCulture), pkgbuild);
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

    [Fact]
    public async Task EnsureRepositoryAsync_MissingRevisionContent_MaterializesRemainingHistoryWithoutMarker()
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
        try
        {
            await repo.InsertSeedAsync(new PackageDocument
                {
                    Id = "pkg",
                    PackageName = "pkg",
                    CreatedAt = T0,
                    UpdatedAt = T0,
                    HeadRevisionId = "rev-1",
                    Revisions = [new PackageRevisionDocument { RevisionId = "rev-1", CreatedAt = T0 }]
                }, new PackageRevisionContentDocument
                {
                    Id = PackageSchema.RevisionDocumentId("pkg", "rev-1"),
                    PackageName = "pkg",
                    RevisionId = "rev-1",
                    CreatedAt = T0,
                    Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
                    {
                        ["PKGBUILD"] = PackageFileFactory.FromText("pkgname=pkg\npkgver=1.0\n", hash: "unused", size: 0)
                    }
                }, TestContext.Current.CancellationToken);
            await repo.AppendRevisionAsync("pkg", new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId("pkg", "rev-2"),
                PackageName = "pkg",
                RevisionId = "rev-2",
                CreatedAt = T1,
                Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
                {
                    ["PKGBUILD"] = PackageFileFactory.FromText("pkgname=pkg\npkgver=2.0\n", hash: "unused", size: 0)
                }
            }, 10, TestContext.Current.CancellationToken);

            await security.MarkPendingAsync("pkg", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);
            await security.CompleteScanAsync("pkg", SecurityStatus.Verified);
            await security.MarkPendingAsync("pkg", "rev-2", true, PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);
            await security.CompleteScanAsync("pkg", SecurityStatus.Verified);

            await cache.EnsureRepositoryAsync("pkg", TestContext.Current.CancellationToken);

            var gitDir = cache.GetRepositoryPath("pkg")!;
            var ct = CancellationToken.None;
            var count = (await GitClient.ExecuteAsync(gitDir, ["rev-list", "--count", "main"], null, null, ct)).Trim();
            var pkgbuild = await GitClient.ExecuteAsync(gitDir, ["show", "main:PKGBUILD"], null, null, ct);

            Assert.Multiple(() =>
            {
                Assert.Equal("1", count);
                Assert.Equal("pkgname=pkg\npkgver=2.0\n", pkgbuild);
                Assert.False(File.Exists(Path.Combine(gitDir, ".atoll-head")),
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

        public Task<IReadOnlyList<PackageIndexEntry>> ListIndexPageAsync(int skip, int take, CancellationToken ct = default)
        {
            return inner.ListIndexPageAsync(skip, take, ct);
        }

        public Task<IReadOnlyList<PackageIndexEntry>> ListIndexEntriesAsync(
            IReadOnlyCollection<string> names, CancellationToken ct = default)
        {
            return inner.ListIndexEntriesAsync(names, ct);
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
            return string.Equals(revisionId, hiddenRevisionId, StringComparison.Ordinal)
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

        public Task<long> TrimExcessRevisionsAsync(int maxRevisions, CancellationToken ct = default)
        {
            return inner.TrimExcessRevisionsAsync(maxRevisions, ct);
        }

        public Task<IReadOnlyList<PackageSyncState>> ListSyncStatesAsync(CancellationToken ct = default)
        {
            return inner.ListSyncStatesAsync(ct);
        }

        public Task UpdateSyncStateAsync(
            IReadOnlyCollection<string> packageNames, string? upstreamHead, bool succeeded, string? errorMessage,
            CancellationToken ct = default)
        {
            return inner.UpdateSyncStateAsync(packageNames, upstreamHead, succeeded, errorMessage, ct);
        }

        public Task DeleteAsync(string packageName, CancellationToken ct = default)
        {
            return inner.DeleteAsync(packageName, ct);
        }
    }
}