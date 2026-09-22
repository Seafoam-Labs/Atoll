using Atoll.Api.Services.Security;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Sync.Direct;

/// <summary>
///     Characterizes the temporary-directory lifecycle of the direct AUR clone: the clone target
///     under the system temp path must be removed on failure as well as success. The failed-clone
///     path is exercised with a package name that cannot exist upstream.
/// </summary>
[Trait("Category", "RequiresGit")]
public sealed class DirectPackageSeederCloneCleanupTests : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        var (exitCode, _) = await GitClient.TryExecuteAsync(["--version"], CancellationToken.None);
        Assert.SkipUnless(exitCode == 0, "git binary is required for these tests");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Failed_clone_cleans_up_its_temporary_directory()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        });
        var security = new InMemoryPackageSecurityRepository();
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var store = new PackageIndexStore();
        var service = new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache, TestHybridCache.New(), store);
        var seeder = new DirectPackageSeeder(repo, store, new AurGitPackageSource(), service);

        // A space in the package name makes the clone URL malformed, which git rejects
        // client-side before any network access. The temp directory is named
        // atoll-{packageName}-{guid}; the unique probe makes any leftover attributable to this run.
        var probe = $"cleanup probe {Guid.NewGuid():N}";
        var pattern = "atoll-cleanup probe *";
        var before = Directory.EnumerateDirectories(Path.GetTempPath(), pattern).ToHashSet();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await seeder.SeedAsync(probe, TestContext.Current.CancellationToken));

        var after = Directory.EnumerateDirectories(Path.GetTempPath(), pattern).ToHashSet();
        var packagePersisted = await repo.ExistsAsync(probe, TestContext.Current.CancellationToken);
        Assert.Multiple(() =>
        {
            Assert.Empty(after.Except(before));
            Assert.False(packagePersisted, "a failed seed must not persist the package");
        });
    }
}