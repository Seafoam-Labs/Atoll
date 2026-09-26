using Atoll.Api.Services.Packages;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Packages;

[Trait("Category", "RequiresMongo")]
public sealed class MongoPackageRepositoryTests : IAsyncLifetime
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
    public async Task InsertSeedAsync_SameIdTwice_ThrowsPackageConflictException()
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
    public async Task InsertSeedAsync_NewSeed_StampsCurrentSchemaVersionOnBothDocuments()
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
    public async Task AppendRevisionAsync_BeyondMaxRevisions_CapsEmbeddedHistory()
    {
        const int maxRevisions = 5;

        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        for (var i = 1; i <= 10; i++)
            await AppendAsync("shelly", NewRevisionContent("shelly", $"rev-{i}", $"commit {i}"), maxRevisions);

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
    public async Task AppendRevisionAsync_UnknownPackage_ThrowsKeyNotFoundException()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await AppendAsync(
            "missing",
            NewRevisionContent("missing", "rev-1", "commit 1")));
    }

    [Fact]
    public async Task GetRevisionAsync_AppendedRevision_ReturnsStoredContent()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        await AppendAsync(
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
    public async Task GetHistoryAsync_AfterMultipleAppends_ReturnsNewestFirst()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        for (var i = 1; i <= 3; i++)
            await AppendAsync("shelly", NewRevisionContent("shelly", $"rev-{i}", $"commit {i}"));

        var history = await _repo.GetHistoryAsync("shelly", CancellationToken.None);

        Assert.Equal(["rev-3", "rev-2", "rev-1", "rev-0"], history.Select(v => v.Sha), StringComparer.Ordinal);
    }

    [Fact]
    public async Task DeleteAsync_ExistingPackage_RemovesDocument()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        Assert.True(await _repo.ExistsAsync("shelly", CancellationToken.None));

        await _repo.DeleteAsync("shelly", CancellationToken.None);

        Assert.False(await _repo.ExistsAsync("shelly", CancellationToken.None));
    }

    [Fact]
    public async Task ListIndexPageAsync_ConsecutivePages_ReturnOrderedProjections()
    {
        foreach (var name in new[] { "c-carrot", "a-apple", "e-egg", "b-banana", "d-date" })
        {
            var (doc, revision) = NewSeed("pkg/" + name, name);
            await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        }

        await AppendAsync("a-apple", NewRevisionContent("a-apple", "rev-a2", "second", PkgbuildFiles("a-apple", "rev-a2")));

        var total = await _repo.CountAsync(CancellationToken.None);
        var firstPage = await _repo.ListIndexPageAsync(0, 2, CancellationToken.None);
        var secondPage = await _repo.ListIndexPageAsync(2, 2, CancellationToken.None);
        var lastRow = await _repo.ListIndexPageAsync(4, 2, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(5, total);
            Assert.Equal(["a-apple", "b-banana"], firstPage.Select(p => p.Name), StringComparer.Ordinal);
            Assert.Equal(["c-carrot", "d-date"], secondPage.Select(p => p.Name), StringComparer.Ordinal);
            Assert.Equal(["e-egg"], lastRow.Select(p => p.Name), StringComparer.Ordinal);

            var apple = firstPage.Single(p => string.Equals(p.Name, "a-apple", StringComparison.Ordinal));
            Assert.Equal("rev-a2", apple.HeadRevisionId);
            Assert.Equal(2, apple.RevisionCount);
            Assert.True(apple.CreatedAt > DateTimeOffset.MinValue);
            Assert.True(apple.UpdatedAt > DateTimeOffset.MinValue);
        });
    }

    [Fact]
    public async Task ListIndexPageAsync_OffsetPage_MatchesInMemoryFake()
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
    public async Task ListIndexEntriesAsync_UnorderedNames_DropsUnknownAndMatchesFake()
    {
        var fake = new InMemoryPackageRepository();

        foreach (var name in new[] { "c-carrot", "a-apple", "e-egg" })
        {
            var (doc, revision) = NewSeed("pkg/" + name, name);
            await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
            await fake.InsertSeedAsync(doc, revision, CancellationToken.None);
        }

        await AppendAsync("a-apple", NewRevisionContent("a-apple", "rev-a2", "second", PkgbuildFiles("a-apple", "rev-a2")));
        await fake.AppendRevisionAsync("a-apple", NewRevisionContent("a-apple", "rev-a2", "second", PkgbuildFiles("a-apple", "rev-a2")), 10, TestContext.Current.CancellationToken);

        var names = new[] { "e-egg", "missing", "a-apple", "c-carrot" };
        var fromMongo = await _repo.ListIndexEntriesAsync(names, CancellationToken.None);
        var fromFake = await fake.ListIndexEntriesAsync(names, CancellationToken.None);
        var empty = await _repo.ListIndexEntriesAsync([], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(
                fromMongo.OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => (p.Name, p.HeadRevisionId, p.RevisionCount)),
                fromFake.OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => (p.Name, p.HeadRevisionId, p.RevisionCount)));
            Assert.Equal(3, fromMongo.Count);

            var apple = fromMongo.Single(p => string.Equals(p.Name, "a-apple", StringComparison.Ordinal));
            Assert.Equal("rev-a2", apple.HeadRevisionId);
            Assert.Equal(2, apple.RevisionCount);
            Assert.True(apple.CreatedAt > DateTimeOffset.MinValue);
            Assert.True(apple.UpdatedAt > DateTimeOffset.MinValue);

            Assert.Empty(empty);
        });
    }

    [Fact]
    public async Task ListAsync_QueryShape_IsIndexCoveredWithoutDocumentFetches()
    {
        for (var i = 0; i < 25; i++)
        {
            var (doc, revision) = NewSeed($"pkg/pkg-{i:D2}", $"pkg-{i:D2}");
            await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        }

        // Exercises the query shape used by MongoPackageRepository.ListAsync.
        var explain = await _client.GetDatabase(_database).RunCommandAsync<BsonDocument>(new BsonDocumentCommand<BsonDocument>(new BsonDocument
            {
                {
                    "explain", new BsonDocument
                    {
                        { "find", Packages.CollectionNamespace.CollectionName },
                        { "filter", new BsonDocument() },
                        { "sort", new BsonDocument("packageName", 1) },
                        { "projection", new BsonDocument { { "_id", 0 }, { "packageName", 1 } } },
                        { "limit", 0 }
                    }
                },
                { "verbosity", "executionStats" }
            }), cancellationToken: TestContext.Current.CancellationToken);

        var stages = BsonValues.Named(explain, "stage").Select(value => value.AsString).ToArray();
        var docsExamined = BsonValues.Named(explain, "totalDocsExamined").Sum(value => value.ToInt64());

        Assert.Multiple(() =>
        {
            Assert.Contains("PROJECTION_COVERED", stages, StringComparer.Ordinal);
            Assert.Equal(0, docsExamined);
        });
    }

    [Fact]
    public async Task ListAsync_SeededNames_ReturnsPackageNameOrder()
    {
        foreach (var name in new[] { "c-carrot", "a-apple", "b-banana" })
        {
            var (doc, revision) = NewSeed("pkg/" + name, name);
            await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        }

        var names = await _repo.ListAsync(CancellationToken.None);

        Assert.Equal(["a-apple", "b-banana", "c-carrot"], names, StringComparer.Ordinal);
    }

    [Fact]
    public async Task AppendRevisionAsync_BeyondMaxRevisions_EvictsRevisionDocuments()
    {
        const int maxRevisions = 5;

        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        for (var i = 1; i <= 10; i++)
            await AppendAsync(
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
                head.Revisions.Select(r => r.RevisionId), StringComparer.Ordinal);
            Assert.Equal(maxRevisions, revisionDocCount);
            Assert.All(retained, item => Assert.NotNull(item));
            Assert.All(evicted, item => Assert.Null(item));
        });
    }

    [Fact]
    public async Task AppendRevisionAsync_ReappearingContentHash_IsNotEvicted()
    {
        const int maxRevisions = 2;

        var (doc, revision) = NewSeed("pkg/shelly", "shelly", "rev-a");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        await AppendAsync("shelly", NewRevisionContent("shelly", "rev-b", "commit b"), maxRevisions);
        await AppendAsync("shelly", NewRevisionContent("shelly", "rev-c", "commit c"), maxRevisions);
        // rev-a is now evicted; append rev-b again - content hashes legitimately reappear, so
        // the freshly upserted document must not be deleted by the eviction sweep.
        await AppendAsync("shelly", NewRevisionContent("shelly", "rev-b", "commit b"), maxRevisions);

        var revB = await _repo.GetRevisionAsync("shelly", "rev-b", CancellationToken.None);
        var revC = await _repo.GetRevisionAsync("shelly", "rev-c", CancellationToken.None);
        var revA = await _repo.GetRevisionAsync("shelly", "rev-a", CancellationToken.None);
        var history = await _repo.GetHistoryAsync("shelly", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.NotNull(revB);
            Assert.NotNull(revC);
            Assert.Null(revA);
            Assert.Equal(["rev-b", "rev-c"], history.Select(v => v.Sha), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task TrimExcessRevisionsAsync_OverCap_TrimsAndEvictsRevisionDocuments()
    {
        const int maxRevisions = 5;

        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        for (var i = 1; i <= 6; i++)
            await AppendAsync(
                "shelly",
                NewRevisionContent("shelly", $"rev-{i}", $"commit {i}", PkgbuildFiles("shelly", $"rev-{i}")));

        var (otherDoc, otherRevision) = NewSeed("pkg/other", "other");
        await _repo.InsertSeedAsync(otherDoc, otherRevision, CancellationToken.None);

        var trimmed = await _repo.TrimExcessRevisionsAsync(maxRevisions, CancellationToken.None);

        var head = await _repo.GetHeadAsync("shelly", CancellationToken.None);
        var untouched = await _repo.GetHeadAsync("other", CancellationToken.None);
        var revisionDocCount = await CountRevisionDocsAsync("shelly");
        var evictedFirst = await _repo.GetRevisionAsync("shelly", "rev-0", CancellationToken.None);
        var evictedSecond = await _repo.GetRevisionAsync("shelly", "rev-1", CancellationToken.None);
        var retainedOldest = await _repo.GetRevisionAsync("shelly", "rev-2", CancellationToken.None);
        var secondPass = await _repo.TrimExcessRevisionsAsync(maxRevisions, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, trimmed);
            Assert.Equal(0, secondPass);
            Assert.NotNull(head);
            Assert.Equal(
                ["rev-6", "rev-5", "rev-4", "rev-3", "rev-2"],
                head!.Revisions.Select(r => r.RevisionId), StringComparer.Ordinal);
            Assert.Equal("rev-6", head.HeadRevisionId);
            Assert.Equal(maxRevisions, revisionDocCount);
            Assert.Null(evictedFirst);
            Assert.Null(evictedSecond);
            Assert.NotNull(retainedOldest);
            Assert.Equal(1, untouched!.Revisions.Count);
        });
    }

    [Fact]
    public async Task TrimExcessRevisionsAsync_UnderCap_IsNoOp()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        await AppendAsync("shelly", NewRevisionContent("shelly", "rev-1", "commit 1"));

        var trimmed = await _repo.TrimExcessRevisionsAsync(5, CancellationToken.None);

        var head = await _repo.GetHeadAsync("shelly", CancellationToken.None);
        var revisionDocCount = await CountRevisionDocsAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.Equal(0, trimmed);
            Assert.NotNull(head);
            Assert.Equal(2, head!.Revisions.Count);
            Assert.Equal(2, revisionDocCount);
        });
    }

    [Fact]
    public async Task DeleteAsync_ExistingPackage_CascadesToRevisionDocuments()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        await AppendAsync(
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
    public async Task InsertSeedAsync_ConflictingSeed_LeavesNoOrphanRevisionDocument()
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

    private Task AppendAsync(string packageName, PackageRevisionContentDocument revision, int maxRevisions = 10)
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

    private IMongoCollection<PackageDocument> Packages =>
        _client.GetDatabase(_database).GetCollection<PackageDocument>("packages");

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
            Files = files ?? new Dictionary<string, PackageFile>(StringComparer.Ordinal)
        };
    }

    private static Dictionary<string, PackageFile> PkgbuildFiles(string packageName, string revisionId)
    {
        var content = $"pkgname={packageName}\n# {revisionId}\n";
        return new Dictionary<string, PackageFile>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = new() { Content = content, Size = content.Length, Hash = revisionId }
        };
    }
}