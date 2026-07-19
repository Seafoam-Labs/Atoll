using Atoll.Api.Services.Packages;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests;

public class MongoPackageServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> SampleFiles =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        };

    private static MongoPackageService CreateService(InMemoryPackageRepository repo)
    {
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        });
        return new MongoPackageService(repo, options);
    }

    [Test]
    public async Task SeedFilesAsync_then_GetAsync_returns_files()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var files = await service.GetAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.That(files.Files.Keys, Is.EquivalentTo(SampleFiles.Keys));
            Assert.That(files.Files["PKGBUILD"], Is.EqualTo(SampleFiles["PKGBUILD"]));
            Assert.That(files.Files[".SRCINFO"], Is.EqualTo(SampleFiles[".SRCINFO"]));
        });
    }

    [Test]
    public async Task SeedFilesAsync_then_GetHistoryAsync_returns_one_revision()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var history = await service.GetHistoryAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.That(history, Has.Count.EqualTo(1));
            Assert.That(history[0].Sha, Has.Length.EqualTo(64));
            Assert.That(history[0].Author, Is.EqualTo("aur"));
            Assert.That(history[0].Message, Is.EqualTo("seed from AUR"));
        });
    }

    [Test]
    public async Task SeedFilesAsync_then_GetAsync_by_revision_sha_returns_files()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var history = await service.GetHistoryAsync("shelly");
        var sha = history[0].Sha;

        var byRevision = await service.GetAsync("shelly", sha);

        Assert.Multiple(() =>
        {
            Assert.That(byRevision.Files.Keys, Is.EquivalentTo(SampleFiles.Keys));
            Assert.That(byRevision.Files["PKGBUILD"], Is.EqualTo(SampleFiles["PKGBUILD"]));
        });
    }

    [Test]
    public void SeedFilesAsync_existing_package_returns_conflict()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        Assert.DoesNotThrowAsync(async () => await service.SeedFilesAsync("shelly", SampleFiles));

        var ex = Assert.ThrowsAsync<PackageConflictException>(async () => await service.SeedFilesAsync("shelly", SampleFiles))!;

        Assert.That(ex.PackageName, Is.EqualTo("shelly"));
    }

    [Test]
    public async Task SeedFilesAsync_oversized_file_throws()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 1_024, MaxRevisions = 10 }
        });
        var service = new MongoPackageService(repo, options);

        var big = new Dictionary<string, string>
        {
            ["big.bin"] = new('x', 2_048)
        };

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await service.SeedFilesAsync("big-pkg", big))!;

        Assert.That(ex.Message, Does.Contain("big.bin"));
        Assert.That(await repo.ExistsAsync("big-pkg"), Is.False);
    }

    [Test]
    public async Task DeleteAsync_then_GetAsync_throws_not_found()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        await service.DeleteAsync("shelly");

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("shelly"));
    }

    [Test]
    public async Task GetAsync_unknown_package_throws_not_found()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("missing"));
    }

    [Test]
    public async Task GetAsync_unknown_revision_throws_not_found()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("shelly", "deadbeef"));
    }

    [Test]
    public async Task ListAsync_returns_seeded_package_names()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        await service.SeedFilesAsync("other", SampleFiles);

        var names = await service.ListAsync();

        Assert.That(names, Is.EquivalentTo(["shelly", "other"]));
    }

    [Test]
    public async Task SeedFilesAsync_same_content_produces_same_revision_sha()
    {
        // Determinism check: identical content must produce identical revision ids.
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var firstHistory = await service.GetHistoryAsync("shelly");

        var repo2 = new InMemoryPackageRepository();
        var service2 = CreateService(repo2);
        await service2.SeedFilesAsync("shelly", SampleFiles);
        var secondHistory = await service2.GetHistoryAsync("shelly");

        Assert.That(firstHistory[0].Sha, Is.EqualTo(secondHistory[0].Sha));
    }
}