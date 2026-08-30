using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Support;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security;

[Category("RequiresMongo")]
public class PackageSecurityRepositoryMongoTests : PackageSecurityRepositoryContract
{
    /// <summary>The index set <see cref="MongoPackageSecurityRepository" /> leaves behind, including the primary key.</summary>
    private static readonly string[] EnsuredIndexNames =
    [
        "_id_",
        "status_1_requiredPolicyVersion_1_leaseUntil_1",
        "packageName_1_isHead_1",
        "isHead_1_status_1_packageName_1"
    ];

    private IMongoClient _client = null!;
    private string _database = null!;
    private MongoPackageSecurityRepository _repo = null!;

    private IMongoCollection<PackageSecurityScanDocument> Scans =>
        _client.GetDatabase(_database).GetCollection<PackageSecurityScanDocument>("package-security-scans");

    [SetUp]
    public void SetUp()
    {
        Assume.That(MongoFixture.IsAvailable, Is.True, $"Mongo unavailable: {MongoFixture.UnavailableReason}");

        _client = MongoRepositoryFactory.CreateClient();
        _database = MongoRepositoryFactory.NewDatabaseName();
        _repo = MongoRepositoryFactory.CreatePackageSecurityRepository(_client, _database);
    }

    [TearDown]
    public async Task TearDown()
    {
        await MongoRepositoryFactory.DropDatabaseAsync(_client, _database);
    }

    private protected override IPackageSecurityRepository CreateRepository()
    {
        return NewRepository();
    }

    private MongoPackageSecurityRepository NewRepository()
    {
        return MongoRepositoryFactory.CreatePackageSecurityRepository(_client, _database);
    }

    private async Task<string[]> IndexNamesAsync()
    {
        var indexes = await Scans.Indexes.List().ToListAsync();
        return indexes.Select(index => index["name"].AsString).ToArray();
    }

    [Test]
    public async Task Constructor_drops_superseded_indexes_left_by_an_older_deployment()
    {
        // Recreate the shapes earlier releases ensured: the pre-policy-aware claim index, the
        // packageName duplicate of the compound head index, and the narrow head-status index.
        var keys = Builders<PackageSecurityScanDocument>.IndexKeys;
        foreach (var superseded in new[]
                 {
                     keys.Ascending(x => x.Status).Ascending(x => x.LeaseUntil),
                     keys.Ascending(x => x.PackageName),
                     keys.Ascending(x => x.IsHead).Ascending(x => x.Status)
                 })
        {
            await Scans.Indexes.CreateOneAsync(new CreateIndexModel<PackageSecurityScanDocument>(superseded));
        }

        Assert.That(await IndexNamesAsync(), Is.SupersetOf(new[] { "packageName_1", "isHead_1_status_1" }));

        NewRepository();

        Assert.That(await IndexNamesAsync(), Is.EquivalentTo(EnsuredIndexNames));
    }

    [Test]
    public async Task Constructor_is_idempotent_when_the_superseded_indexes_are_already_absent()
    {
        NewRepository();

        // A second startup (or a fresh database) finds nothing to drop and must converge on the
        // same index set rather than fail on the missing indexes.
        NewRepository();

        Assert.That(await IndexNamesAsync(), Is.EquivalentTo(EnsuredIndexNames));
    }

    [Test]
    public async Task Head_status_query_shape_is_covered_by_the_index_without_fetching_documents()
    {
        for (var i = 0; i < 25; i++)
        {
            await Scans.InsertOneAsync(new PackageSecurityScanDocument
            {
                Id = PackageSecurityScanDocument.ComposeId($"pkg-{i}", "rev-1"),
                PackageName = $"pkg-{i}",
                RevisionId = "rev-1",
                IsHead = i % 2 == 0,
                Status = SecurityStatus.Verified,
                Findings = [new SecurityFinding("rule", FindingSeverity.High, "payload", "", "PKGBUILD")]
            });
        }

        // Exercises the query shape used by MongoPackageSecurityRepository.ListHeadStatusesAsync.
        var explain = await _client.GetDatabase(_database).RunCommandAsync<BsonDocument>(
            new BsonDocumentCommand<BsonDocument>(new BsonDocument
            {
                {
                    "explain", new BsonDocument
                    {
                        { "find", Scans.CollectionNamespace.CollectionName },
                        { "filter", new BsonDocument("isHead", true) },
                        { "projection", new BsonDocument { { "_id", 0 }, { "packageName", 1 }, { "status", 1 } } },
                        { "limit", 0 }
                    }
                },
                { "verbosity", "executionStats" }
            }));

        var stages = ValuesNamed(explain, "stage").Select(value => value.AsString).ToArray();
        var docsExamined = ValuesNamed(explain, "totalDocsExamined").Sum(value => value.ToInt64());

        Assert.Multiple(() =>
        {
            Assert.That(stages, Does.Contain("PROJECTION_COVERED"), $"winning plan was {string.Join(" → ", stages)}");
            Assert.That(docsExamined, Is.Zero, "head-status reads must not touch the documents behind the index");
        });
    }

    private static IEnumerable<BsonValue> ValuesNamed(BsonValue node, string name)
    {
        switch (node)
        {
            case BsonDocument document:
            {
                foreach (var element in document)
                {
                    if (element.Name == name) yield return element.Value;
                    foreach (var nested in ValuesNamed(element.Value, name)) yield return nested;
                }

                break;
            }
            case BsonArray array:
            {
                foreach (var item in array)
                foreach (var nested in ValuesNamed(item, name)) yield return nested;

                break;
            }
        }
    }

    [Test]
    public async Task Legacy_pending_work_without_requirement_is_claimable_and_backfilled()
    {
        var collection = _client.GetDatabase(_database).GetCollection<PackageSecurityScanDocument>("package-security-scans");
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("legacy-pending", "rev-1"),
            PackageName = "legacy-pending",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Pending,
            Findings = []
        });

        var claim = await _repo.TryClaimPendingScanAsync("v2-worker", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.That(claim, Is.Not.Null, "a missing requirement is treated as unconstrained");

        await _repo.ReleaseScanClaimAsync("legacy-pending", "rev-1", "v2-worker");

        var requeued = await _repo.RequeueOutdatedAsync(3);
        Assert.That(requeued, Is.EqualTo(1));

        var scan = await _repo.GetAsync("legacy-pending", "rev-1");
        Assert.That(scan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(scan!.RequiredPolicyVersion, Is.EqualTo(3), "reconciliation backfills the requirement");
            Assert.That(scan.Status, Is.EqualTo(SecurityStatus.Pending));
        });
    }

    [Test]
    public async Task RequeueOutdatedAsync_requeues_unversioned_and_older_versions_only()
    {
        // 1. Legacy unversioned verified document (directly inserted into Mongo)
        var collection = _client.GetDatabase(_database).GetCollection<PackageSecurityScanDocument>("package-security-scans");
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("legacy-verified", "rev-1"),
            PackageName = "legacy-verified",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Verified,
            PolicyVersion = null,
            ScannedAt = DateTimeOffset.UtcNow.AddDays(-5),
            Findings = []
        });

        // 2. Legacy unversioned flagged document with findings
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("legacy-flagged", "rev-1"),
            PackageName = "legacy-flagged",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Flagged,
            PolicyVersion = null,
            ScannedAt = DateTimeOffset.UtcNow.AddDays(-5),
            Findings = [new SecurityFinding("rule-old", FindingSeverity.High, "old issue", "", "PKGBUILD")]
        });

        // 3. Document scanned under older policy version (e.g. 1)
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("v1-error", "rev-1"),
            PackageName = "v1-error",
            RevisionId = "rev-1",
            IsHead = false,
            Status = SecurityStatus.Error,
            PolicyVersion = 1,
            ScannedAt = DateTimeOffset.UtcNow.AddDays(-2),
            Findings = []
        });

        // 4. Document scanned under current policy version (e.g. 2)
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("v2-verified", "rev-1"),
            PackageName = "v2-verified",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Verified,
            PolicyVersion = 2,
            ScannedAt = DateTimeOffset.UtcNow,
            Findings = []
        });

        // 5. Document produced by a newer worker during a rolling deployment
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("v3-verified", "rev-1"),
            PackageName = "v3-verified",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Verified,
            PolicyVersion = 3,
            ScannedAt = DateTimeOffset.UtcNow,
            Findings = []
        });

        // 6. Document currently Pending with an outdated requirement
        await _repo.MarkPendingAsync("already-pending", "rev-1", true, requiredPolicyVersion: 1);

        // Requeue outdated scans with current policy version 2
        var requeuedCount = await _repo.RequeueOutdatedAsync(2);
        Assert.That(requeuedCount, Is.EqualTo(4), "three completed outcomes plus one pending requirement are updated");

        // Verify legacy-verified is now Pending, unversioned, timestamps cleared
        var doc1 = await _repo.GetAsync("legacy-verified", "rev-1");
        Assert.That(doc1, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc1!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(doc1.PolicyVersion, Is.Null);
            Assert.That(doc1.RequiredPolicyVersion, Is.EqualTo(2), "requeued work now requires the current policy");
            Assert.That(doc1.ScannedAt, Is.Null);
            Assert.That(doc1.Findings, Is.Empty);
            Assert.That(doc1.IsHead, Is.True);
        });

        // Verify legacy-flagged is now Pending, findings cleared
        var doc2 = await _repo.GetAsync("legacy-flagged", "rev-1");
        Assert.That(doc2, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc2!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(doc2.PolicyVersion, Is.Null);
            Assert.That(doc2.Findings, Is.Empty);
        });

        // Verify v1-error is now Pending, IsHead preserved
        var doc3 = await _repo.GetAsync("v1-error", "rev-1");
        Assert.That(doc3, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc3!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(doc3.PolicyVersion, Is.Null);
            Assert.That(doc3.IsHead, Is.False);
        });

        // Verify the pending requirement was raised in place
        var doc6 = await _repo.GetAsync("already-pending", "rev-1");
        Assert.That(doc6, Is.Not.Null);
        Assert.That(doc6!.RequiredPolicyVersion, Is.EqualTo(2));

        // Verify v2-verified is unchanged
        var doc4 = await _repo.GetAsync("v2-verified", "rev-1");
        Assert.That(doc4, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc4!.Status, Is.EqualTo(SecurityStatus.Verified));
            Assert.That(doc4.PolicyVersion, Is.EqualTo(2));
        });

        // Verify a newer result is not downgraded by an older worker
        var doc5 = await _repo.GetAsync("v3-verified", "rev-1");
        Assert.That(doc5, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc5!.Status, Is.EqualTo(SecurityStatus.Verified));
            Assert.That(doc5.PolicyVersion, Is.EqualTo(3));
        });

        // Idempotency: Running RequeueOutdatedAsync again returns 0
        var secondRequeue = await _repo.RequeueOutdatedAsync(2);
        Assert.That(secondRequeue, Is.EqualTo(0));
    }
}
