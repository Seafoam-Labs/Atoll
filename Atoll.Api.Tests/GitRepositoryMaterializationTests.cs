using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Git;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests;

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

    private static (MongoPackageService service, string reposRoot) CreateService()
    {
        var repo = new InMemoryPackageRepository();
        var reposRoot = CreateTempReposRoot();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = reposRoot }
        });
        return (new MongoPackageService(repo, options), reposRoot);
    }

    [SetUp]
    public async Task SetUp()
    {
        Assume.That(await GitIsAvailable(), "git binary is required for these tests");
    }

    [Test]
    public async Task EnsureGitRepositoryAsync_creates_bare_repo_with_main_branch()
    {
        var (service, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await service.EnsureGitRepositoryAsync("shelly");

            var gitDir = service.GetRepositoryPath("shelly")!;
            Assert.That(Directory.Exists(gitDir), Is.True);
            Assert.That((await File.ReadAllTextAsync(Path.Combine(gitDir, "HEAD"))).Trim(),
                Is.EqualTo("ref: refs/heads/main"));

            string[] args = ["rev-parse", "refs/heads/main"];
            var refSha = (await GitClient.ExecuteAsync(gitDir, args, input: null, env: null, CancellationToken.None)).Trim();
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
        var (service, reposRoot) = CreateService();
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
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
        var (service, reposRoot) = CreateService();
        var cloneDir = Path.Combine(Path.GetTempPath(),
            $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            await service.EnsureGitRepositoryAsync("shelly");
            var gitDir = service.GetRepositoryPath("shelly")!;

            string[] args = ["clone", "--quiet", gitDir, cloneDir];
            await GitClient.ExecuteAsync(Directory.GetCurrentDirectory(), args, input: null, env: null, CancellationToken.None);

            foreach (var (name, content) in SampleFiles)
            {
                var fullPath = Path.Combine(cloneDir, name);
                Assert.That(File.Exists(fullPath), Is.True, $"missing {name}");
                Assert.That(await File.ReadAllTextAsync(fullPath), Is.EqualTo(content));
            }

            string[] args1 = ["rev-list", "--count", "HEAD"];
            var logCount = (await GitClient.ExecuteAsync(cloneDir, args1, input: null, env: null, CancellationToken.None)).Trim();
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
        var (service, reposRoot) = CreateService();
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
        var service = new MongoPackageService(repo, options);

        await service.SeedFilesAsync("shelly", SampleFiles);

        Assert.DoesNotThrowAsync(async () =>
            await service.EnsureGitRepositoryAsync("shelly"));
        Assert.That(service.GetRepositoryPath("shelly"), Is.Null);
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
            // best-effort cleanup
        }
    }
}