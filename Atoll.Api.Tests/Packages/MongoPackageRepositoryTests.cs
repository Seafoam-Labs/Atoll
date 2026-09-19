using Atoll.Api.Services.Packages;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Packages;

[Trait("Category", "RequiresMongo")]
public class MongoPackageRepositoryTests : IAsyncLifetime
{
    private const string RevisionCollection = "package-revisions";

    private readonly IMongoClient _client;
    private readonly string _database;
    private readonly MongoPackageRepository _repo;

    public MongoPackageRepositoryTests()
    {
        Assert.SkipUnless(MongoFixture.IsAvailable, $"Mongo unavailable: {MongoFixture.UnavailableReason}");

        _client = MongoRepositoryFactory.CreateClient();
        _database = MongoRepositoryFactory.NewDatabaseName();
        _repo = MongoRepositoryFactory.CreatePackageRepository(_client, _database);
    }

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await MongoRepositoryFactory.DropDatabaseAsync(_client, _database);
    }

    [Fact]
    public async Task InsertSeedAsync_same_id_twice_throws_PackageConflictException()
    {
        var (firstDoc, firstRevision) = NewSeed("pkg/shelly", "shelly");
        // Different content (and therefore a different revision id) so the second seed gets
        // past the revision-doc insert and reaches the package-doc conflict.
        var (secondDoc, secondRevision) = NewSeed("pkg/shelly", "shelly", "rev-0b");

        await _repo.InsertSeedAsync(firstDoc, firstRevision, CancellationToken.None);

        await Assert.ThrowsAsync<PackageConflictException>(async () =>
            await _repo.InsertSeedAsync(secondDoc, secondRevision, CancellationToken.None));
    }

    [Fact]
    public async Task InsertSeedAsync_stamps_current_schema_version_on_package_and_revision_documents()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        var head = await _repo.GetHeadAsync("shelly", CancellationToken.None);
        var storedRevision = await _repo.GetRevisionAsync("shelly", "rev-0", CancellationToken.None);

        Assert.NotNull(head);
        Assert.NotNull(storedRevision);
        Assert.Multiple(() =>
        {
            Assert.Equal(PackageSchema.CurrentVersion, head!.SchemaVersion);
            Assert.Equal(PackageSchema.CurrentVersion, storedRevision!.SchemaVersion);
        });
    }

    [Fact]
    public async Task AppendRevisionAsync_caps_revisions_to_maxRevisions()
    {
        const int maxRevisions = 5;

        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        for (var i = 1; i <= 10; i++)
            await Append("shelly", NewRevisionContent("shelly", $"rev-{i}", $"commit {i}"), maxRevisions);

        var head = await _repo.GetHeadAsync("shelly", CancellationToken.None);

        Assert.NotNull(head);
        Assert.Multiple(() =>
        {
            Assert.Equal(maxRevisions, head!.Revisions.Count);
            // Newest revision is pushed at position 0.
            Assert.Equal("rev-10", head.Revisions[0].RevisionId);
            Assert.Equal("rev-10", head.HeadRevisionId);
        });
    }

    [Fact]
    public async Task AppendRevisionAsync_unknown_package_throws_KeyNotFoundException()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await Append(
            "missing",
            NewRevisionContent("missing", "rev-1", "commit 1")));
    }

    [Fact]
    public async Task GetRevisionAsync_returns_expected_revision()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        await Append(
            "shelly",
            NewRevisionContent("shelly", "rev-a", "commit a", PkgbuildFiles("shelly", "rev-a")));

        var stored = await _repo.GetRevisionAsync("shelly", "rev-a", CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Multiple(() =>
        {
            Assert.Equal(PackageSchema.RevisionDocumentId("shelly", "rev-a"), stored!.Id);
            Assert.Equal("rev-a", stored.RevisionId);
            Assert.Equal("commit a", stored.Message);
            Assert.Contains("PKGBUILD", stored.Files);
        });
    }

    [Fact]
    public async Task GetHistoryAsync_returns_newest_first_after_multiple_appends()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        for (var i = 1; i <= 3; i++)
            await Append("shelly", NewRevisionContent("shelly", $"rev-{i}", $"commit {i}"));

        var history = await _repo.GetHistoryAsync("shelly", CancellationToken.None);

        Assert.Equal(["rev-3", "rev-2", "rev-1", "rev-0"], history.Select(v => v.Sha));
    }

    [Fact]
    public async Task DeleteAsync_removes_package()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        Assert.True(await _repo.ExistsAsync("shelly", CancellationToken.None));

        await _repo.DeleteAsync("shelly", CancellationToken.None);

        Assert.False(await _repo.ExistsAsync("shelly", CancellationToken.None));
    }

    [Fact]
    public async Task ListIndexPageAsync_returns_ordered_windows_with_projection_fields()
    {
        foreach (var name in new[] { "c-carrot", "a-apple", "e-egg", "b-banana", "d-date" })
        {
            var (doc, revision) = NewSeed("pkg/" + name, name);
            await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        }

        await Append("a-apple", NewRevisionContent("a-apple", "rev-a2", "second", PkgbuildFiles("a-apple", "rev-a2")));

        var total = await _repo.CountAsync(CancellationToken.None);
        var firstPage = await _repo.ListIndexPageAsync(0, 2, CancellationToken.None);
        var secondPage = await _repo.ListIndexPageAsync(2, 2, CancellationToken.None);
        var lastRow = await _repo.ListIndexPageAsync(4, 2, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(5, total);
            Assert.Equal(["a-apple", "b-banana"], firstPage.Select(p => p.Name));
            Assert.Equal(["c-carrot", "d-date"], secondPage.Select(p => p.Name));
            Assert.Equal(["e-egg"], lastRow.Select(p => p.Name));

            var apple = firstPage.Single(p => p.Name == "a-apple");
            Assert.Equal("rev-a2", apple.HeadRevisionId);
            Assert.Equal(2, apple.RevisionCount);
            Assert.True(apple.CreatedAt > DateTimeOffset.MinValue);
            Assert.True(apple.UpdatedAt > DateTimeOffset.MinValue);
            Assert.Null(apple.UpstreamPackageBase);
        });
    }

    [Fact]
    public async Task ListIndexPageAsync_matches_in_memory_fake_paging()
    {
        var fake = new InMemoryPackageRepository();

        foreach (var name in new[] { "c-carrot", "a-apple", "e-egg", "b-banana", "d-date" })
        {
            var (doc, revision) = NewSeed("pkg/" + name, name);
            await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
            await fake.InsertSeedAsync(doc, revision, CancellationToken.None);
        }

        var fromMongo = await _repo.ListIndexPageAsync(1, 3, CancellationToken.None);
        var fromFake = await fake.ListIndexPageAsync(1, 3, CancellationToken.None);

        Assert.Equal(
            fromMongo.Select(p => (p.Name, p.HeadRevisionId, p.RevisionCount)),
            fromFake.Select(p => (p.Name, p.HeadRevisionId, p.RevisionCount)));
    }

    [Fact]
    public async Task ListIndexEntriesAsync_is_order_independent_and_drops_unknown_names()
    {
        var fake = new InMemoryPackageRepository();

        foreach (var name in new[] { "c-carrot", "a-apple", "e-egg" })
        {
            var (doc, revision) = NewSeed("pkg/" + name, name);
            await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
            await fake.InsertSeedAsync(doc, revision, CancellationToken.None);
        }

        await Append("a-apple", NewRevisionContent("a-apple", "rev-a2", "second", PkgbuildFiles("a-apple", "rev-a2")));
        await fake.AppendRevisionAsync(
            "a-apple",
            NewRevisionContent("a-apple", "rev-a2", "second", PkgbuildFiles("a-apple", "rev-a2")),
            10);

        var names = new[] { "e-egg", "missing", "a-apple", "c-carrot" };
        var fromMongo = await _repo.ListIndexEntriesAsync(names, CancellationToken.None);
        var fromFake = await fake.ListIndexEntriesAsync(names, CancellationToken.None);
        var empty = await _repo.ListIndexEntriesAsync([], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(
                fromMongo.OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => (p.Name, p.HeadRevisionId, p.RevisionCount, p.UpstreamPackageBase)),
                fromFake.OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => (p.Name, p.HeadRevisionId, p.RevisionCount, p.UpstreamPackageBase)));
            Assert.Equal(3, fromMongo.Count);

            var apple = fromMongo.Single(p => p.Name == "a-apple");
            Assert.Equal("rev-a2", apple.HeadRevisionId);
            Assert.Equal(2, apple.RevisionCount);
            Assert.True(apple.CreatedAt > DateTimeOffset.MinValue);
            Assert.True(apple.UpdatedAt > DateTimeOffset.MinValue);
            Assert.Null(apple.UpstreamPackageBase);

            Assert.Empty(empty);
        });
    }

    [Fact]
    public async Task AppendRevisionAsync_evicts_revision_documents_beyond_maxRevisions()
    {
        const int maxRevisions = 5;

        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        for (var i = 1; i <= 10; i++)
            await Append(
                "shelly",
                NewRevisionContent("shelly", $"rev-{i}", $"commit {i}", PkgbuildFiles("shelly", $"rev-{i}")),
                maxRevisions);

        var head = await _repo.GetHeadAsync("shelly", CancellationToken.None);
        Assert.NotNull(head);

        var retained = new List<PackageRevisionContentDocument?>();
        for (var i = 6; i <= 10; i++)
            retained.Add(await _repo.GetRevisionAsync("shelly", $"rev-{i}", CancellationToken.None));

        var evicted = new List<PackageRevisionContentDocument?>();
        for (var i = 0; i <= 5; i++)
            evicted.Add(await _repo.GetRevisionAsync("shelly", $"rev-{i}", CancellationToken.None));

        var revisionDocCount = await CountRevisionDocsAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.Equal(maxRevisions, head!.Revisions.Count);
            Assert.Equal(
                ["rev-10", "rev-9", "rev-8", "rev-7", "rev-6"],
                head.Revisions.Select(r => r.RevisionId));
            Assert.Equal(maxRevisions, revisionDocCount);
            Assert.All(retained, item => Assert.NotNull(item));
            Assert.All(evicted, item => Assert.Null(item));
        });
    }

    [Fact]
    public async Task AppendRevisionAsync_never_deletes_reappearing_content_hash()
    {
        const int maxRevisions = 2;

        var (doc, revision) = NewSeed("pkg/shelly", "shelly", "rev-a");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        await Append("shelly", NewRevisionContent("shelly", "rev-b", "commit b"), maxRevisions);
        await Append("shelly", NewRevisionContent("shelly", "rev-c", "commit c"), maxRevisions);
        // rev-a is now evicted; append rev-b again - content hashes legitimately reappear, so
        // the freshly upserted document must not be deleted by the eviction sweep.
        await Append("shelly", NewRevisionContent("shelly", "rev-b", "commit b"), maxRevisions);

        var revB = await _repo.GetRevisionAsync("shelly", "rev-b", CancellationToken.None);
        var revC = await _repo.GetRevisionAsync("shelly", "rev-c", CancellationToken.None);
        var revA = await _repo.GetRevisionAsync("shelly", "rev-a", CancellationToken.None);
        var history = await _repo.GetHistoryAsync("shelly", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.NotNull(revB);
            Assert.NotNull(revC);
            Assert.Null(revA);
            Assert.Equal(["rev-b", "rev-c"], history.Select(v => v.Sha));
        });
    }

    [Fact]
    public async Task DeleteAsync_cascades_to_revision_documents()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        await Append(
            "shelly",
            NewRevisionContent("shelly", "rev-1", "commit 1", PkgbuildFiles("shelly", "rev-1")));

        await _repo.DeleteAsync("shelly", CancellationToken.None);

        var rev0 = await _repo.GetRevisionAsync("shelly", "rev-0", CancellationToken.None);
        var rev1 = await _repo.GetRevisionAsync("shelly", "rev-1", CancellationToken.None);
        var remainingRevisionDocs = await CountRevisionDocsAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.Null(rev0);
            Assert.Null(rev1);
            Assert.Equal(0, remainingRevisionDocs);
        });
    }

    [Fact]
    public async Task InsertSeedAsync_conflict_leaves_no_orphan_revision_document()
    {
        var (firstDoc, firstRevision) = NewSeed("pkg/shelly", "shelly");
        // Same package name but different content: the second revision document inserts before
        // the package-doc conflict, so its orphaned document must be cleaned up.
        var (secondDoc, secondRevision) = NewSeed("pkg/shelly", "shelly", "rev-0b");

        await _repo.InsertSeedAsync(firstDoc, firstRevision, CancellationToken.None);

        await Assert.ThrowsAsync<PackageConflictException>(async () =>
            await _repo.InsertSeedAsync(secondDoc, secondRevision, CancellationToken.None));

        var remainingRevisionDocs = await CountRevisionDocsAsync("shelly");
        var secondRevisionDoc = await _repo.GetRevisionAsync("shelly", "rev-0b", CancellationToken.None);
        var firstRevisionDoc = await _repo.GetRevisionAsync("shelly", "rev-0", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, remainingRevisionDocs);
            Assert.Null(secondRevisionDoc);
            Assert.NotNull(firstRevisionDoc);
        });
    }

    private Task Append(string packageName, PackageRevisionContentDocument revision, int maxRevisions = 10)
    {
        return _repo.AppendRevisionAsync(packageName, revision, maxRevisions, CancellationToken.None);
    }

    private Task<long> CountRevisionDocsAsync(string packageName)
    {
        return _client.GetDatabase(_database)
            .GetCollection<BsonDocument>(RevisionCollection)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("packageName", packageName),
                cancellationToken: CancellationToken.None);
    }

    private static (PackageDocument Doc, PackageRevisionContentDocument Revision) NewSeed(
        string id,
        string packageName,
        string revisionId = "rev-0")
    {
        var now = DateTimeOffset.UtcNow;
        var revision = NewRevisionContent(
            packageName,
            revisionId,
            "seed from AUR",
            PkgbuildFiles(packageName, revisionId));

        var doc = new PackageDocument
        {
            Id = id,
            PackageName = packageName,
            CreatedAt = now,
            UpdatedAt = now,
            HeadRevisionId = revisionId,
            Revisions =
            [
                new PackageRevisionDocument
                {
                    RevisionId = revision.RevisionId,
                    CreatedAt = revision.CreatedAt,
                    Author = revision.Author,
                    Message = revision.Message
                }
            ]
        };

        return (doc, revision);
    }

    private static PackageRevisionContentDocument NewRevisionContent(
        string packageName,
        string revisionId,
        string message,
        Dictionary<string, PackageFile>? files = null)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(packageName, revisionId),
            PackageName = packageName,
            RevisionId = revisionId,
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = message,
            Files = files ?? new Dictionary<string, PackageFile>()
        };
    }

    private static Dictionary<string, PackageFile> PkgbuildFiles(string packageName, string revisionId)
    {
        var content = $"pkgname={packageName}\n# {revisionId}\n";
        return new Dictionary<string, PackageFile>
        {
            ["PKGBUILD"] = new() { Content = content, Size = content.Length, Hash = revisionId }
        };
    }
}