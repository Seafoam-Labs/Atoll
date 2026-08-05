using Atoll.Api.Services.Packages;
using Atoll.Api.Tests.Support;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages;

[Category("RequiresMongo")]
public class MongoPackageRepositoryTests
{
    private const string RevisionCollection = "package-revisions";

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
        var (firstDoc, firstRevision) = NewSeed("pkg/shelly", "shelly");
        // Different content (and therefore a different revision id) so the second seed gets
        // past the revision-doc insert and reaches the package-doc conflict.
        var (secondDoc, secondRevision) = NewSeed("pkg/shelly", "shelly", "rev-0b");

        await _repo.InsertSeedAsync(firstDoc, firstRevision, CancellationToken.None);

        Assert.ThrowsAsync<PackageConflictException>(async () =>
            await _repo.InsertSeedAsync(secondDoc, secondRevision, CancellationToken.None));
    }

    [Test]
    public async Task InsertSeedAsync_stamps_current_schema_version_on_package_and_revision_documents()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        var head = await _repo.GetHeadAsync("shelly", CancellationToken.None);
        var storedRevision = await _repo.GetRevisionAsync("shelly", "rev-0", CancellationToken.None);

        Assert.That(head, Is.Not.Null);
        Assert.That(storedRevision, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(head!.SchemaVersion, Is.EqualTo(PackageSchema.CurrentVersion));
            Assert.That(storedRevision!.SchemaVersion, Is.EqualTo(PackageSchema.CurrentVersion));
        });
    }

    [Test]
    public async Task AppendRevisionAsync_caps_revisions_to_maxRevisions()
    {
        const int maxRevisions = 5;

        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        for (var i = 1; i <= 10; i++)
            await Append("shelly", NewRevisionContent("shelly", $"rev-{i}", $"commit {i}"), maxRevisions);

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
        Assert.ThrowsAsync<KeyNotFoundException>(async () => await Append(
            "missing",
            NewRevisionContent("missing", "rev-1", "commit 1")));
    }

    [Test]
    public async Task GetRevisionAsync_returns_expected_revision()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        await Append(
            "shelly",
            NewRevisionContent("shelly", "rev-a", "commit a", PkgbuildFiles("shelly", "rev-a")));

        var stored = await _repo.GetRevisionAsync("shelly", "rev-a", CancellationToken.None);

        Assert.That(stored, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Id, Is.EqualTo(PackageSchema.RevisionDocumentId("shelly", "rev-a")));
            Assert.That(stored.RevisionId, Is.EqualTo("rev-a"));
            Assert.That(stored.Message, Is.EqualTo("commit a"));
            Assert.That(stored.Files, Does.ContainKey("PKGBUILD"));
        });
    }

    [Test]
    public async Task GetHistoryAsync_returns_newest_first_after_multiple_appends()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        for (var i = 1; i <= 3; i++)
            await Append("shelly", NewRevisionContent("shelly", $"rev-{i}", $"commit {i}"));

        var history = await _repo.GetHistoryAsync("shelly", CancellationToken.None);

        Assert.That(history.Select(v => v.Sha), Is.EqualTo(["rev-3", "rev-2", "rev-1", "rev-0"]));
    }

    [Test]
    public async Task DeleteAsync_removes_package()
    {
        var (doc, revision) = NewSeed("pkg/shelly", "shelly");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);
        Assert.That(await _repo.ExistsAsync("shelly", CancellationToken.None), Is.True);

        await _repo.DeleteAsync("shelly", CancellationToken.None);

        Assert.That(await _repo.ExistsAsync("shelly", CancellationToken.None), Is.False);
    }

    [Test]
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
        Assert.That(head, Is.Not.Null);

        var retained = new List<PackageRevisionContentDocument?>();
        for (var i = 6; i <= 10; i++)
            retained.Add(await _repo.GetRevisionAsync("shelly", $"rev-{i}", CancellationToken.None));

        var evicted = new List<PackageRevisionContentDocument?>();
        for (var i = 0; i <= 5; i++)
            evicted.Add(await _repo.GetRevisionAsync("shelly", $"rev-{i}", CancellationToken.None));

        var revisionDocCount = await CountRevisionDocsAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.That(head!.Revisions, Has.Count.EqualTo(maxRevisions));
            Assert.That(
                head.Revisions.Select(r => r.RevisionId),
                Is.EqualTo(["rev-10", "rev-9", "rev-8", "rev-7", "rev-6"]));
            Assert.That(revisionDocCount, Is.EqualTo(maxRevisions));
            Assert.That(retained, Has.All.Not.Null, "retained revisions should still have documents");
            Assert.That(evicted, Has.All.Null, "evicted revisions should have been deleted");
        });
    }

    [Test]
    public async Task AppendRevisionAsync_never_deletes_reappearing_content_hash()
    {
        const int maxRevisions = 2;

        var (doc, revision) = NewSeed("pkg/shelly", "shelly", "rev-a");
        await _repo.InsertSeedAsync(doc, revision, CancellationToken.None);

        await Append("shelly", NewRevisionContent("shelly", "rev-b", "commit b"), maxRevisions);
        await Append("shelly", NewRevisionContent("shelly", "rev-c", "commit c"), maxRevisions);
        // rev-a is now evicted; append rev-b again — content hashes legitimately reappear, so
        // the freshly upserted document must not be deleted by the eviction sweep.
        await Append("shelly", NewRevisionContent("shelly", "rev-b", "commit b"), maxRevisions);

        var revB = await _repo.GetRevisionAsync("shelly", "rev-b", CancellationToken.None);
        var revC = await _repo.GetRevisionAsync("shelly", "rev-c", CancellationToken.None);
        var revA = await _repo.GetRevisionAsync("shelly", "rev-a", CancellationToken.None);
        var history = await _repo.GetHistoryAsync("shelly", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(revB, Is.Not.Null, "the re-appended revision document must survive");
            Assert.That(revC, Is.Not.Null);
            Assert.That(revA, Is.Null);
            Assert.That(history.Select(v => v.Sha), Is.EqualTo(["rev-b", "rev-c"]));
        });
    }

    [Test]
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
            Assert.That(rev0, Is.Null);
            Assert.That(rev1, Is.Null);
            Assert.That(remainingRevisionDocs, Is.Zero);
        });
    }

    [Test]
    public async Task InsertSeedAsync_conflict_leaves_no_orphan_revision_document()
    {
        var (firstDoc, firstRevision) = NewSeed("pkg/shelly", "shelly");
        // Same package name but different content: the second revision document inserts before
        // the package-doc conflict, so its orphaned document must be cleaned up.
        var (secondDoc, secondRevision) = NewSeed("pkg/shelly", "shelly", "rev-0b");

        await _repo.InsertSeedAsync(firstDoc, firstRevision, CancellationToken.None);

        Assert.ThrowsAsync<PackageConflictException>(async () =>
            await _repo.InsertSeedAsync(secondDoc, secondRevision, CancellationToken.None));

        var remainingRevisionDocs = await CountRevisionDocsAsync("shelly");
        var secondRevisionDoc = await _repo.GetRevisionAsync("shelly", "rev-0b", CancellationToken.None);
        var firstRevisionDoc = await _repo.GetRevisionAsync("shelly", "rev-0", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(remainingRevisionDocs, Is.EqualTo(1));
            Assert.That(secondRevisionDoc, Is.Null, "the conflicting seed's revision document must be deleted");
            Assert.That(firstRevisionDoc, Is.Not.Null, "the original seed's revision document must remain");
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