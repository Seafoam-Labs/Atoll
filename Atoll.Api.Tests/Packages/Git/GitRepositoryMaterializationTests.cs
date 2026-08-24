using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Git;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

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

    private static (MongoPackageService service, IPackageSecurityRepository security, string reposRoot) CreateService()
    {
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var reposRoot = CreateTempReposRoot();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = reposRoot }
        });
        return (new MongoPackageService(
            repo,
            new PackageIndexStore(),
            options,
            security,
            NullLogger<MongoPackageService>.Instance), security, reposRoot);
    }

    [SetUp]
    public async Task SetUp()
    {
        Assume.That(await GitIsAvailable(), "git binary is required for these tests");
    }

    [Test]
    public async Task EnsureGitRepositoryAsync_creates_bare_repo_with_main_branch()
    {
        var (service, security, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await service.EnsureGitRepositoryAsync("shelly");

            var gitDir = service.GetRepositoryPath("shelly")!;
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
    public async Task EnsureGitRepositoryAsync_is_idempotent_when_head_unchanged()
    {
        var (service, security, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await service.EnsureGitRepositoryAsync("shelly");
            var gitDir = service.GetRepositoryPath("shelly")!;
            var marker = Path.Combine(gitDir, ".atoll-head");
            var firstMarkerWrite = File.GetLastWriteTimeUtc(marker);

            await Task.Delay(50);
            await service.EnsureGitRepositoryAsync("shelly");

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
    public async Task EnsureGitRepositoryAsync_produces_cloneable_repo_with_expected_files()
    {
        var (service, security, reposRoot) = CreateService();
        var cloneDir = Path.Combine(Path.GetTempPath(),
            $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");
            await service.EnsureGitRepositoryAsync("shelly");
            var gitDir = service.GetRepositoryPath("shelly")!;

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
    public async Task EnsureGitRepositoryAsync_returns_silently_for_unknown_package()
    {
        var (service, _, reposRoot) = CreateService();
        try
        {
            Assert.DoesNotThrowAsync(async () =>
                await service.EnsureGitRepositoryAsync("does-not-exist"));
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Test]
    public async Task EnsureGitRepositoryAsync_returns_silently_when_no_path_configured()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = "" }
        });
        var service = new MongoPackageService(
            repo,
            new PackageIndexStore(),
            options,
            new InMemoryPackageSecurityRepository(),
            NullLogger<MongoPackageService>.Instance);

        await service.SeedFilesAsync("shelly", SampleFiles);

        Assert.DoesNotThrowAsync(async () =>
            await service.EnsureGitRepositoryAsync("shelly"));
        Assert.That(service.GetRepositoryPath("shelly"), Is.Null);
    }

    [Test]
    public async Task Flagged_revision_is_excluded_from_git_history_until_rescanned()
    {
        var (service, security, reposRoot) = CreateService();
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

            await service.EnsureGitRepositoryAsync("shelly");
            var (commits, pkgbuild) = await CloneAndInspectAsync(service, "shelly", cloneDir);
            Assert.Multiple(() =>
            {
                Assert.That(commits, Is.EqualTo(1), "the flagged head revision must not be cloneable");
                Assert.That(pkgbuild, Does.Contain("pkgver=1.0"), "clone must fall back to the last verified revision");
            });

            // Rescan the flagged head to Verified; the marker must change and the lazy
            // rebuild must restore the revision to the cloneable history.
            await security.MarkPendingAsync("shelly", flaggedRevision, true);
            await security.MarkHeadVerifiedAsync("shelly");

            await service.EnsureGitRepositoryAsync("shelly");
            TryCleanup(cloneDir);
            (commits, pkgbuild) = await CloneAndInspectAsync(service, "shelly", cloneDir);
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
        var (service, security, reposRoot) = CreateService();
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

            await service.EnsureGitRepositoryAsync("shelly");
            var (commits, pkgbuild) = await CloneAndInspectAsync(service, "shelly", cloneDir);
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

    private static async Task<(int Commits, string Pkgbuild)> CloneAndInspectAsync(
        MongoPackageService service, string packageName, string cloneDir)
    {
        var gitDir = service.GetRepositoryPath(packageName)!;
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
}