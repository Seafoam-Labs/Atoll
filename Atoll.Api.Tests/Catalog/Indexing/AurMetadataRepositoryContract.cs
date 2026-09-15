using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Persistence;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Indexing;

public abstract class AurMetadataRepositoryContract
{
    private protected abstract IAurMetadataRepository CreateRepository();

    [Fact]
    public async Task EmptyRepository_HasNoData_AndZeroCount()
    {
        var repo = CreateRepository();

        var exists = await repo.ExistsAsync(CancellationToken.None);
        var count = await repo.CountAsync(CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.False(exists);
            Assert.Equal(0, count);
            Assert.Empty(loaded);
        });
    }

    [Fact]
    public async Task SaveAsync_Then_LoadAsync_RoundTripsPackages()
    {
        var repo = CreateRepository();

        var packages = SamplePackages().ToList();
        await repo.SaveAsync(packages, CancellationToken.None);

        var loaded = await repo.LoadAsync(CancellationToken.None);
        var count = await repo.CountAsync(CancellationToken.None);
        var exists = await repo.ExistsAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.True(exists);
            Assert.Equal(packages.Count, count);
            Assert.Equal(packages.Count, loaded.Count);
            Assert.Equivalent(packages.Select(p => p.Name).Order(), loaded.Select(p => p.Name).Order(), strict: true);
        });
    }

    [Fact]
    public async Task SaveAsync_ReplacesPreviousBatch_AndRemovesOldDocuments()
    {
        var repo = CreateRepository();

        var firstBatch = SamplePackages("v1").ToList();
        await repo.SaveAsync(firstBatch, CancellationToken.None);
        Assert.Equal(firstBatch.Count, await repo.CountAsync(CancellationToken.None));

        var secondBatch = SamplePackages("v2").ToList();
        await repo.SaveAsync(secondBatch, CancellationToken.None);

        var loaded = await repo.LoadAsync(CancellationToken.None);
        var loadedNames = loaded.Select(p => p.Name).ToHashSet();
        var activeCount = await repo.CountAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(secondBatch.Count, activeCount);
            Assert.False(loadedNames.Overlaps(firstBatch.Select(p => p.Name)));
            foreach (var expected in secondBatch.Select(p => p.Name))
                Assert.True(loadedNames.Contains(expected), $"Missing {expected}");
        });
    }

    [Fact]
    public async Task SaveAsync_PreservesAllFields()
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

        await repo.SaveAsync([original], CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        Assert.Single(loaded);
        var pkg = loaded[0];

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

        await repo.SaveAsync([.. SamplePackages()], CancellationToken.None);
        Assert.True(await repo.ExistsAsync(CancellationToken.None));

        await repo.DeleteAsync(CancellationToken.None);

        var exists = await repo.ExistsAsync(CancellationToken.None);
        var count = await repo.CountAsync(CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.False(exists);
            Assert.Equal(0, count);
            Assert.Empty(loaded);
        });
    }

    [Fact]
    public async Task SaveAsync_EmptyInput_SwapsPointerToEmptyBatch()
    {
        var repo = CreateRepository();

        await repo.SaveAsync([.. SamplePackages()], CancellationToken.None);
        Assert.True(await repo.CountAsync(CancellationToken.None) > 0);

        await repo.SaveAsync([], CancellationToken.None);

        var existsAfterEmpty = await repo.ExistsAsync(CancellationToken.None);
        var countAfterEmpty = await repo.CountAsync(CancellationToken.None);
        var loadedAfterEmpty = await repo.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.True(existsAfterEmpty);
            Assert.Equal(0, countAfterEmpty);
            Assert.Empty(loadedAfterEmpty);
        });
    }

    private static IEnumerable<AurPackageMetadata> SamplePackages(string prefix = "")
    {
        var p = string.IsNullOrEmpty(prefix) ? "" : prefix + "-";
        yield return NewMeta(1001, $"{p}shelly-bin", $"{p}shelly");
        yield return NewMeta(1002, $"{p}portable-kit", $"{p}portable");
        yield return NewMeta(1003, $"{p}portable-pro", $"{p}portable-pro");
    }

    private static AurPackageMetadata NewMeta(long id, string name, string packageBase)
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