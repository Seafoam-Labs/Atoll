using Atoll.Api.Services.Packages;
using Atoll.Api.Tests.Support;
using MongoDB.Driver;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages;

[Category("RequiresMongo")]
public class MongoPackageRepositoryTests
{
    private IMongoClient _client = null!;
    private string _database = null!;
    private MongoPackageRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        Assume.That(MongoFixture.IsAvailable, Is.True, $"Mongo unavailable: {MongoFixture.UnavailableReason}");

        _client = MongoRepositoryFactory.CreateClient();
        _database = MongoRepositoryFactory.NewDatabaseName();
        _repo = MongoRepositoryFactory.CreatePackageRepository(_client, _database);
    }

    [TearDown]
    public async Task TearDown()
    {
        await MongoRepositoryFactory.DropDatabaseAsync(_client, _database);
    }

    [Test]
    public async Task InsertSeedAsync_same_id_twice_throws_PackageConflictException()
    {
        var first = NewSeedDoc("pkg/shelly", "shelly");
        var second = NewSeedDoc("pkg/shelly", "shelly");

        await _repo.InsertSeedAsync(first);

        Assert.ThrowsAsync<PackageConflictException>(async () => await _repo.InsertSeedAsync(second));
    }

    [Test]
    public async Task AppendRevisionAsync_caps_revisions_to_maxRevisions()
    {
        const int maxRevisions = 5;

        await _repo.InsertSeedAsync(NewSeedDoc("pkg/shelly", "shelly"));

        for (var i = 1; i <= 10; i++)
            await _repo.AppendRevisionAsync(
                "shelly",
                NewRevision($"rev-{i}", $"commit {i}"),
                new Dictionary<string, PackageFile>(),
                maxRevisions,
                CancellationToken.None);

        var head = await _repo.GetHeadAsync("shelly", CancellationToken.None);

        Assert.That(head, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(head!.Revisions, Has.Count.EqualTo(maxRevisions));
            // Newest revision is pushed at position 0.
            Assert.That(head.Revisions[0].RevisionId, Is.EqualTo("rev-10"));
            Assert.That(head.HeadRevisionId, Is.EqualTo("rev-10"));
        });
    }

    [Test]
    public async Task AppendRevisionAsync_unknown_package_throws_KeyNotFoundException()
    {
        Assert.ThrowsAsync<KeyNotFoundException>(async () => await _repo.AppendRevisionAsync(
            "missing",
            NewRevision("rev-1", "commit 1"),
            new Dictionary<string, PackageFile>(),
            10,
            CancellationToken.None));
    }

    [Test]
    public async Task GetRevisionAsync_returns_expected_revision()
    {
        await _repo.InsertSeedAsync(NewSeedDoc("pkg/shelly", "shelly"));
        await _repo.AppendRevisionAsync(
            "shelly",
            NewRevision("rev-a", "commit a"),
            new Dictionary<string, PackageFile>(),
            10,
            CancellationToken.None);

        var revision = await _repo.GetRevisionAsync("shelly", "rev-a", CancellationToken.None);

        Assert.That(revision, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(revision!.RevisionId, Is.EqualTo("rev-a"));
            Assert.That(revision.Message, Is.EqualTo("commit a"));
        });
    }

    [Test]
    public async Task GetHistoryAsync_returns_newest_first_after_multiple_appends()
    {
        await _repo.InsertSeedAsync(NewSeedDoc("pkg/shelly", "shelly"));

        for (var i = 1; i <= 3; i++)
            await _repo.AppendRevisionAsync(
                "shelly",
                NewRevision($"rev-{i}", $"commit {i}"),
                new Dictionary<string, PackageFile>(),
                10,
                CancellationToken.None);

        var history = await _repo.GetHistoryAsync("shelly", CancellationToken.None);

        Assert.That(history.Select(v => v.Sha), Is.EqualTo(["rev-3", "rev-2", "rev-1", "rev-0"]));
    }

    [Test]
    public async Task DeleteAsync_removes_package()
    {
        await _repo.InsertSeedAsync(NewSeedDoc("pkg/shelly", "shelly"));
        Assert.That(await _repo.ExistsAsync("shelly", CancellationToken.None), Is.True);

        await _repo.DeleteAsync("shelly", CancellationToken.None);

        Assert.That(await _repo.ExistsAsync("shelly", CancellationToken.None), Is.False);
    }

    private static PackageDocument NewSeedDoc(string id, string packageName)
    {
        var now = DateTimeOffset.UtcNow;
        return new PackageDocument
        {
            Id = id,
            PackageName = packageName,
            CreatedAt = now,
            UpdatedAt = now,
            HeadRevisionId = "rev-0",
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = $"pkgname={packageName}\n", Size = 10, Hash = "h" }
            },
            Revisions =
            [
                NewRevision("rev-0", "seed from AUR")
            ]
        };
    }

    private static PackageRevisionDocument NewRevision(string revisionId, string message)
    {
        return new PackageRevisionDocument
        {
            RevisionId = revisionId,
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = message,
            Files = new Dictionary<string, PackageFile>()
        };
    }
}