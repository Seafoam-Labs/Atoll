using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Persistence;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Persistence;

public abstract class AurMetadataRepositoryContract
{
    private protected abstract IAurMetadataRepository CreateRepository();

    [Fact]
    public async Task EmptyRepository_HasNoData_AndZeroCount()
    {
        var repo = CreateRepository();

        var count = await repo.CountAsync(CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(0, count);
            Assert.Empty(loaded);
        });
    }

    [Fact]
    public async Task SyncAsync_Then_LoadAsync_RoundTripsPackages()
    {
        var repo = CreateRepository();

        var packages = SamplePackages().ToList();
        await repo.SyncAsync(FullSync(packages), CancellationToken.None);

        var loaded = await repo.LoadAsync(CancellationToken.None);
        var count = await repo.CountAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(packages.Count, count);
            Assert.Equal(packages.Count, loaded.Count);
            Assert.Equivalent(
                packages.Select(p => p.Name).Order(StringComparer.Ordinal),
                loaded.Select(p => p.Name).Order(StringComparer.Ordinal),
                strict: true);
        });
    }

    [Fact]
    public async Task SyncAsync_UpsertsChanged_InsertsNew_RemovesVanished_KeepsUntouched()
    {
        var repo = CreateRepository();

        var unchanged = NewMeta(1001, "shelly-bin", "shelly");
        var changed = NewMeta(1002, "portable-kit", "portable");
        var vanished = NewMeta(1003, "portable-pro", "portable-pro");
        await repo.SyncAsync(FullSync([unchanged, changed, vanished]), CancellationToken.None);

        var next = new[]
        {
            unchanged,
            changed with { Version = "1.1-1", NumVotes = 7 },
            NewMeta(1004, "portable-next", "portable-next")
        };
        var delta = AurMetadataDelta.Compute(
            ToDictionary([unchanged, changed, vanished]), next);

        await repo.SyncAsync(delta, CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);
        var count = await repo.CountAsync(CancellationToken.None);
        var byName = loaded.ToDictionary(p => p.Name, StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.Equal(3, count);
            Assert.False(byName.ContainsKey(vanished.Name));
            Assert.Equal(unchanged, byName[unchanged.Name]);
            Assert.Equal("1.1-1", byName[changed.Name].Version);
            Assert.Equal(7, byName[changed.Name].NumVotes);
            Assert.True(byName.ContainsKey("portable-next"));
        });
    }

    [Fact]
    public async Task SyncAsync_EmptyDelta_ChangesNothing()
    {
        var repo = CreateRepository();

        var packages = SamplePackages().ToList();
        await repo.SyncAsync(FullSync(packages), CancellationToken.None);
        var before = await repo.LoadAsync(CancellationToken.None);

        await repo.SyncAsync(AurMetadataDelta.Compute(ToDictionary(packages), packages), CancellationToken.None);
        var after = await repo.LoadAsync(CancellationToken.None);
        var count = await repo.CountAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(packages.Count, count);
            Assert.Equivalent(before, after, strict: true);
        });
    }

    [Fact]
    public async Task SyncAsync_RepeatedWithSameSnapshot_KeepsOneDocumentPerName()
    {
        var repo = CreateRepository();

        var packages = SamplePackages().ToList();
        await repo.SyncAsync(FullSync(packages), CancellationToken.None);
        await repo.SyncAsync(AurMetadataDelta.Compute(ToDictionary(packages), packages), CancellationToken.None);
        await repo.SyncAsync(FullSync(packages), CancellationToken.None);

        var loaded = await repo.LoadAsync(CancellationToken.None);
        var count = await repo.CountAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(packages.Count, count);
            Assert.Equal(packages.Count, loaded.Count);
            Assert.Equal(
                loaded.Count,
                loaded.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        });
    }

    [Fact]
    public async Task SyncAsync_PreservesAllFields()
    {
        var repo = CreateRepository();

        var original = new AurPackageMetadata(
            4242,
            "ghost-bin",
            99,
            "ghost",
            "9.9.9-9",
            "A test package",
            "https://example.test",
            42,
            1.5,
            1700000000,
            "alice",
            "bob",
            1600000000,
            1700000001,
            "/cgit/ghost.git",
            ["dep1", "dep2"],
            ["make1"],
            ["opt1"],
            ["conflict1"],
            ["provided"],
            ["MIT"],
            ["kw1", "kw2"],
            ["bob", "alice"])
        {
            CheckDepends = ["check1"],
            Groups = ["group1"],
            Replaces = ["old-ghost"]
        };

        await repo.SyncAsync(FullSync([original]), CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        var pkg = Assert.Single(loaded);

        Assert.Multiple(() =>
        {
            Assert.Equal(original.Id, pkg.Id);
            Assert.Equal(original.Name, pkg.Name);
            Assert.Equal(original.PackageBaseId, pkg.PackageBaseId);
            Assert.Equal(original.PackageBase, pkg.PackageBase);
            Assert.Equal(original.Version, pkg.Version);
            Assert.Equal(original.Description, pkg.Description);
            Assert.Equal(original.Url, pkg.Url);
            Assert.Equal(original.NumVotes, pkg.NumVotes);
            Assert.Equal(original.Popularity, pkg.Popularity);
            Assert.Equal(original.OutOfDate, pkg.OutOfDate);
            Assert.Equal(original.Maintainer, pkg.Maintainer);
            Assert.Equal(original.Submitter, pkg.Submitter);
            Assert.Equal(original.FirstSubmitted, pkg.FirstSubmitted);
            Assert.Equal(original.LastModified, pkg.LastModified);
            Assert.Equal(original.UrlPath, pkg.UrlPath);
            Assert.Equivalent(original.Depends, pkg.Depends, strict: true);
            Assert.Equivalent(original.MakeDepends, pkg.MakeDepends, strict: true);
            Assert.Equivalent(original.OptDepends, pkg.OptDepends, strict: true);
            Assert.Equivalent(original.Conflicts, pkg.Conflicts, strict: true);
            Assert.Equivalent(original.Provides, pkg.Provides, strict: true);
            Assert.Equivalent(original.License, pkg.License, strict: true);
            Assert.Equivalent(original.Keywords, pkg.Keywords, strict: true);
            Assert.Equivalent(original.CoMaintainers, pkg.CoMaintainers, strict: true);
            Assert.Equivalent(original.CheckDepends, pkg.CheckDepends, strict: true);
            Assert.Equivalent(original.Groups, pkg.Groups, strict: true);
            Assert.Equivalent(original.Replaces, pkg.Replaces, strict: true);
        });
    }

    [Fact]
    public async Task DeleteAsync_RemovesEverything()
    {
        var repo = CreateRepository();

        await repo.SyncAsync(FullSync(SamplePackages()), CancellationToken.None);
        Assert.True(await repo.CountAsync(CancellationToken.None) > 0);

        await repo.DeleteAsync(CancellationToken.None);

        var count = await repo.CountAsync(CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(0, count);
            Assert.Empty(loaded);
        });
    }

    private protected static AurMetadataDelta FullSync(IEnumerable<AurPackageMetadata> packages)
    {
        return AurMetadataDelta.Compute(new Dictionary<string, AurPackageMetadata>(StringComparer.Ordinal), packages);
    }

    private protected static Dictionary<string, AurPackageMetadata> ToDictionary(IEnumerable<AurPackageMetadata> packages)
    {
        return packages.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    private protected static IEnumerable<AurPackageMetadata> SamplePackages()
    {
        yield return NewMeta(1001, "shelly-bin", "shelly");
        yield return NewMeta(1002, "portable-kit", "portable");
        yield return NewMeta(1003, "portable-pro", "portable-pro");
    }

    private protected static AurPackageMetadata NewMeta(long id, string name, string packageBase)
    {
        return new AurPackageMetadata(
            id, name, id, packageBase, "1.0-1",
            "sample metadata", null, 0, 0, null,
            null, null, 0, 0,
            "", [], [], [],
            [], [], [],
            ["sample"], []);
    }
}
