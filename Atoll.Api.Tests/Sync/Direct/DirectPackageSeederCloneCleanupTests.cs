using Atoll.Api.Services.Security;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Sync.Direct;

/// <summary>
///     Characterizes the temporary-directory lifecycle of the direct AUR clone: the clone target
///     under the system temp path must be removed on failure as well as success. The failed-clone
///     path is exercised with a package name that cannot exist upstream.
/// </summary>
[Category("RequiresGit")]
public class DirectPackageSeederCloneCleanupTests
{
    [SetUp]
    public async Task SetUp()
    {
        var (exitCode, _) = await GitClient.TryExecuteAsync(["--version"], CancellationToken.None);
        Assume.That(exitCode == 0, "git binary is required for these tests");
    }

    [Test]
    public async Task Failed_clone_cleans_up_its_temporary_directory()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        });
        var security = new InMemoryPackageSecurityRepository();
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var service = new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache);
        var seeder = new DirectPackageSeeder(repo, new PackageIndexStore(), new AurGitPackageSource(), service);

        // A space in the package name makes the clone URL malformed, which git rejects
        // client-side before any network access. The temp directory is named
        // atoll-{packageName}-{guid}; the unique probe makes any leftover attributable to this run.
        var probe = $"cleanup probe {Guid.NewGuid():N}";
        var pattern = "atoll-cleanup probe *";
        var before = Directory.EnumerateDirectories(Path.GetTempPath(), pattern).ToHashSet();

        Assert.ThrowsAsync<InvalidOperationException>(async () => await seeder.SeedAsync(probe));

        var after = Directory.EnumerateDirectories(Path.GetTempPath(), pattern).ToHashSet();
        var packagePersisted = await repo.ExistsAsync(probe);
        Assert.Multiple(() =>
        {
            Assert.That(after.Except(before), Is.Empty,
                "the failed clone's temporary directory must be deleted");
            Assert.That(packagePersisted, Is.False,
                "a failed seed must not persist the package");
        });
    }
}