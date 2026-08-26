using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Seed;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages.Seed;

public class DirectPackageSeederTests
{
    private static readonly IReadOnlyDictionary<string, string> BaseFiles =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=demo\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = demo\n"
        };

    private sealed class FakeAurPackageSource : IAurPackageSource
    {
        public List<string> FetchedBases { get; } = [];

        public Task<IReadOnlyDictionary<string, string>> FetchFilesAsync(
            string packageBase, CancellationToken ct = default)
        {
            FetchedBases.Add(packageBase);
            return Task.FromResult(BaseFiles);
        }
    }

    private static AurPackageMetadata SampleMetadata(string name, string packageBase)
    {
        return new AurPackageMetadata(
            0, name, 0, packageBase,
            "1.0", "sample", null,
            0, 0, null,
            null, null,
            0, 0, "",
            [], [], [],
            [], [], [],
            [], []);
    }

    private static (DirectPackageSeeder Seeder, FakeAurPackageSource Source, InMemoryPackageRepository Repo) CreateSeeder(
        PackageIndexStore? store = null)
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        });
        var security = new InMemoryPackageSecurityRepository();
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var service = new PackageService(repo, options, security, cache);
        var source = new FakeAurPackageSource();
        var seeder = new DirectPackageSeeder(repo, store ?? new PackageIndexStore(), source, service);
        return (seeder, source, repo);
    }

    [Test]
    public async Task SeedAsync_fetches_resolved_pkgbase_and_persists_files()
    {
        // Split packages have pkgname != pkgbase; the clone source must see the base.
        var store = new PackageIndexStore();
        store.Replace(PackageDataLoader.BuildFromPackages([
            SampleMetadata("libfoo", "foo"),
            SampleMetadata("libfoo-devel", "foo")
        ]));
        var (seeder, source, repo) = CreateSeeder(store);

        await seeder.SeedAsync("libfoo");

        var persisted = await repo.GetRevisionAsync("libfoo", (await repo.GetHeadAsync("libfoo"))!.HeadRevisionId);
        Assert.Multiple(() =>
        {
            Assert.That(source.FetchedBases, Is.EqualTo(["foo"]));
            Assert.That(persisted!.Files.Keys, Is.EquivalentTo(BaseFiles.Keys));
        });
    }

    [Test]
    public async Task SeedAsync_throws_conflict_without_fetching_when_package_exists()
    {
        var (seeder, source, repo) = CreateSeeder();

        await seeder.SeedAsync("shelly");
        Assert.ThrowsAsync<PackageConflictException>(async () => await seeder.SeedAsync("shelly"));
        var packageCount = await repo.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(source.FetchedBases, Is.EqualTo(["shelly"]), "the first seed fetches once");
            Assert.That(packageCount, Is.EqualTo(1), "the conflicting seed must not fetch or persist");
        });
    }

    [Test]
    public void ResolvePackageBase_split_package_returns_pkgbase_not_pkgname()
    {
        // Split packages (e.g. "libfoo" / "libfoo-devel" under base "foo") have
        // pkgname != pkgbase; AUR Git URLs are keyed by pkgbase.
        var store = new PackageIndexStore();
        store.Replace(PackageDataLoader.BuildFromPackages([
            SampleMetadata("libfoo", "foo"),
            SampleMetadata("libfoo-devel", "foo")
        ]));

        var (seeder, _, _) = CreateSeeder(store);

        Assert.Multiple(() =>
        {
            Assert.That(seeder.ResolvePackageBase("libfoo"), Is.EqualTo("foo"));
            Assert.That(seeder.ResolvePackageBase("libfoo-devel"), Is.EqualTo("foo"));
        });
    }

    [Test]
    public void ResolvePackageBase_non_split_package_returns_pkgname()
    {
        var store = new PackageIndexStore();
        store.Replace(PackageDataLoader.BuildFromPackages([
            SampleMetadata("shelly", "shelly")
        ]));

        var (seeder, _, _) = CreateSeeder(store);

        Assert.That(seeder.ResolvePackageBase("shelly"), Is.EqualTo("shelly"));
    }

    [Test]
    public void ResolvePackageBase_unknown_package_falls_back_to_pkgname()
    {
        // Cold start or stale index: fall back to pkgname so non-split packages
        // can still be seeded. Split packages will fail at clone time, which is
        // the pre-fix behavior and surfaces the missing index entry in logs.
        var (seeder, _, _) = CreateSeeder(new PackageIndexStore());

        Assert.That(seeder.ResolvePackageBase("anything"), Is.EqualTo("anything"));
    }
}
