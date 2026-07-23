using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Tests.Fakes;
using NUnit.Framework;

namespace Atoll.Api.Tests;

public class AurMetadataRepositoryTests
{
    private static IAurMetadataRepository CreateRepository()
    {
        return new InMemoryAurMetadataRepository();
    }

    [Test]
    public async Task EmptyRepository_HasNoData_AndZeroCount()
    {
        var repo = CreateRepository();

        var exists = await repo.ExistsAsync(CancellationToken.None);
        var count = await repo.CountAsync(CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exists, Is.False);
            Assert.That(count, Is.EqualTo(0));
            Assert.That(loaded, Is.Empty);
        });
    }

    [Test]
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
            Assert.That(exists, Is.True);
            Assert.That(count, Is.EqualTo(packages.Count));
            Assert.That(loaded, Has.Count.EqualTo(packages.Count));
            Assert.That(loaded.Select(p => p.Name).Order(), Is.EquivalentTo(packages.Select(p => p.Name).Order()));
        });
    }

    [Test]
    public async Task SaveAsync_ReplacesPreviousBatch_AndRemovesOldDocuments()
    {
        var repo = CreateRepository();

        var firstBatch = SamplePackages("v1").ToList();
        await repo.SaveAsync(firstBatch, CancellationToken.None);
        Assert.That(await repo.CountAsync(CancellationToken.None), Is.EqualTo(firstBatch.Count));

        // Second batch with different packages — old ones must disappear from the active batch.
        var secondBatch = SamplePackages("v2").ToList();
        await repo.SaveAsync(secondBatch, CancellationToken.None);

        var loaded = await repo.LoadAsync(CancellationToken.None);
        var loadedNames = loaded.Select(p => p.Name).ToHashSet();
        var activeCount = await repo.CountAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(activeCount, Is.EqualTo(secondBatch.Count));
            Assert.That(loadedNames.Overlaps(firstBatch.Select(p => p.Name)), Is.False);
            foreach (var expected in secondBatch.Select(p => p.Name))
                Assert.That(loadedNames.Contains(expected), Is.True, $"Missing {expected}");
        });
    }

    [Test]
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
            ["bob", "alice"]);

        await repo.SaveAsync([original], CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        Assert.That(loaded, Has.Count.EqualTo(1));
        var pkg = loaded[0];

        Assert.Multiple(() =>
        {
            Assert.That(pkg.Id, Is.EqualTo(original.Id));
            Assert.That(pkg.Name, Is.EqualTo(original.Name));
            Assert.That(pkg.PackageBaseId, Is.EqualTo(original.PackageBaseId));
            Assert.That(pkg.PackageBase, Is.EqualTo(original.PackageBase));
            Assert.That(pkg.Version, Is.EqualTo(original.Version));
            Assert.That(pkg.Description, Is.EqualTo(original.Description));
            Assert.That(pkg.Url, Is.EqualTo(original.Url));
            Assert.That(pkg.NumVotes, Is.EqualTo(original.NumVotes));
            Assert.That(pkg.Popularity, Is.EqualTo(original.Popularity));
            Assert.That(pkg.OutOfDate, Is.EqualTo(original.OutOfDate));
            Assert.That(pkg.Maintainer, Is.EqualTo(original.Maintainer));
            Assert.That(pkg.Submitter, Is.EqualTo(original.Submitter));
            Assert.That(pkg.FirstSubmitted, Is.EqualTo(original.FirstSubmitted));
            Assert.That(pkg.LastModified, Is.EqualTo(original.LastModified));
            Assert.That(pkg.UrlPath, Is.EqualTo(original.UrlPath));
            Assert.That(pkg.Depends, Is.EquivalentTo(original.Depends));
            Assert.That(pkg.MakeDepends, Is.EquivalentTo(original.MakeDepends));
            Assert.That(pkg.OptDepends, Is.EquivalentTo(original.OptDepends));
            Assert.That(pkg.Conflicts, Is.EquivalentTo(original.Conflicts));
            Assert.That(pkg.Provides, Is.EquivalentTo(original.Provides));
            Assert.That(pkg.License, Is.EquivalentTo(original.License));
            Assert.That(pkg.Keywords, Is.EquivalentTo(original.Keywords));
            Assert.That(pkg.CoMaintainers, Is.EquivalentTo(original.CoMaintainers));
        });
    }

    [Test]
    public async Task DeleteAsync_RemovesEverything()
    {
        var repo = CreateRepository();

        await repo.SaveAsync(SamplePackages().ToList(), CancellationToken.None);
        Assert.That(await repo.ExistsAsync(CancellationToken.None), Is.True);

        await repo.DeleteAsync(CancellationToken.None);

        var exists = await repo.ExistsAsync(CancellationToken.None);
        var count = await repo.CountAsync(CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exists, Is.False);
            Assert.That(count, Is.EqualTo(0));
            Assert.That(loaded, Is.Empty);
        });
    }

    [Test]
    public async Task SaveAsync_EmptyInput_SwapsPointerToEmptyBatch()
    {
        var repo = CreateRepository();

        await repo.SaveAsync(SamplePackages().ToList(), CancellationToken.None);
        Assert.That(await repo.CountAsync(CancellationToken.None), Is.GreaterThan(0));

        await repo.SaveAsync([], CancellationToken.None);

        var existsAfterEmpty = await repo.ExistsAsync(CancellationToken.None);
        var countAfterEmpty = await repo.CountAsync(CancellationToken.None);
        var loadedAfterEmpty = await repo.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(existsAfterEmpty, Is.True);
            Assert.That(countAfterEmpty, Is.EqualTo(0));
            Assert.That(loadedAfterEmpty, Is.Empty);
        });
    }

    private static IEnumerable<AurPackageMetadata> SamplePackages(string prefix = "")
    {
        var p = string.IsNullOrEmpty(prefix) ? "" : prefix + "-";
        yield return new AurPackageMetadata(
            Random.Shared.NextInt64(),
            $"{p}shelly-bin",
            1, $"{p}shelly", "1.0-1",
            "Shelly: A Modern Arch Package Manager (prebuilt binary)",
            null, 10, 0.5, null,
            null, null, 0, 0,
            "", [], [], [],
            [], ["shelly"], [],
            ["helper", "AUR"], []);

        yield return new AurPackageMetadata(
            Random.Shared.NextInt64(),
            $"{p}portable-kit",
            2, $"{p}portable", "1.0-1",
            "Handheld gaming toolkit 1337 i3",
            null, 5, 0.2, null,
            null, null, 0, 0,
            "", [], [], [],
            [], [], [],
            ["handheld"], []);

        yield return new AurPackageMetadata(
            Random.Shared.NextInt64(),
            $"{p}portable-pro",
            3, $"{p}portable-pro", "1.0-1",
            "Handheld gaming emulator",
            null, 20, 0.7, null,
            null, null, 0, 0,
            "", [], [], [],
            [], ["portable"], [],
            ["emulator", "fast"], []);
    }
}