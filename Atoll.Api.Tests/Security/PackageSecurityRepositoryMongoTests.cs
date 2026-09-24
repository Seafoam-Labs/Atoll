using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Support;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Atoll.Api.Tests.Security;

[Trait("Category", "RequiresMongo")]
public sealed class PackageSecurityRepositoryMongoTests : PackageSecurityRepositoryContract, IAsyncLifetime
{
    /// <summary>The index set <see cref="MongoPackageSecurityRepository" /> leaves behind, including the primary key.</summary>
    private static readonly string[] EnsuredIndexNames =
    [
        "_id_",
        "status_1_requiredPolicyVersion_1_leaseUntil_1",
        "packageName_1_isHead_1",
        "isHead_1_status_1_packageName_1"
    ];

    private readonly IMongoClient _client;
    private readonly string _database;
    private readonly MongoPackageSecurityRepository _repo;

    public PackageSecurityRepositoryMongoTests()
    {
        Assert.SkipUnless(MongoFixture.IsAvailable, $"Mongo unavailable: {MongoFixture.UnavailableReason}");

        _client = MongoRepositoryFactory.CreateClient();
        _database = MongoRepositoryFactory.NewDatabaseName();
        _repo = MongoRepositoryFactory.CreatePackageSecurityRepository(_client, _database);
    }

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await MongoRepositoryFactory.DropDatabaseAsync(_client, _database);
    }

    private IMongoCollection<PackageSecurityScanDocument> Scans =>
        _client.GetDatabase(_database).GetCollection<PackageSecurityScanDocument>("package-security-scans");

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
        var ct = TestContext.Current.CancellationToken;
        using var cursor = await Scans.Indexes.ListAsync(ct);
        var indexes = await cursor.ToListAsync(ct);
        return indexes.Select(index => index["name"].AsString).ToArray();
    }

    [Fact]
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
            await Scans.Indexes.CreateOneAsync(new CreateIndexModel<PackageSecurityScanDocument>(superseded), cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal) { "packageName_1", "isHead_1_status_1" },
            new HashSet<string>(await IndexNamesAsync(), StringComparer.Ordinal));

        NewRepository();

        Assert.Equivalent(EnsuredIndexNames, await IndexNamesAsync(), strict: true);
    }

    [Fact]
    public async Task Constructor_is_idempotent_when_the_superseded_indexes_are_already_absent()
    {
        NewRepository();

        // A second startup (or a fresh database) finds nothing to drop and must converge on the
        // same index set rather than fail on the missing indexes.
        NewRepository();

        Assert.Equivalent(EnsuredIndexNames, await IndexNamesAsync(), strict: true);
    }

    [Fact]
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
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Exercises the query shape used by MongoPackageSecurityRepository.ListHeadStatusesAsync.
        var explain = await _client.GetDatabase(_database).RunCommandAsync<BsonDocument>(new BsonDocumentCommand<BsonDocument>(new BsonDocument
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
        }, cancellationToken: TestContext.Current.CancellationToken);

        var claim = await _repo.TryClaimPendingScanAsync("v2-worker", TimeSpan.FromMinutes(1), workerPolicyVersion: 2, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(claim);

        await _repo.ReleaseScanClaimAsync("legacy-pending", "rev-1", "v2-worker", TestContext.Current.CancellationToken);

        var requeued = await _repo.RequeueOutdatedAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal(1, requeued);

        var scan = await _repo.GetAsync("legacy-pending", "rev-1", TestContext.Current.CancellationToken);
        Assert.NotNull(scan);
        Assert.Multiple(() =>
        {
            Assert.Equal(3, scan!.RequiredPolicyVersion);
            Assert.Equal(SecurityStatus.Pending, scan.Status);
        });
    }

    [Fact]
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
        }, cancellationToken: TestContext.Current.CancellationToken);

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
        }, cancellationToken: TestContext.Current.CancellationToken);

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
        }, cancellationToken: TestContext.Current.CancellationToken);

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
        }, cancellationToken: TestContext.Current.CancellationToken);

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
        }, cancellationToken: TestContext.Current.CancellationToken);

        // 6. Document currently Pending with an outdated requirement
        await _repo.MarkPendingAsync("already-pending", "rev-1", true, requiredPolicyVersion: 1, ct: TestContext.Current.CancellationToken);

        // Requeue outdated scans with current policy version 2
        var requeuedCount = await _repo.RequeueOutdatedAsync(2, TestContext.Current.CancellationToken);
        Assert.Equal(4, requeuedCount);

        // Verify legacy-verified is now Pending, unversioned, timestamps cleared
        var doc1 = await _repo.GetAsync("legacy-verified", "rev-1", TestContext.Current.CancellationToken);
        Assert.NotNull(doc1);
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Pending, doc1!.Status);
            Assert.Null(doc1.PolicyVersion);
            Assert.Equal(2, doc1.RequiredPolicyVersion);
            Assert.Null(doc1.ScannedAt);
            Assert.Empty(doc1.Findings);
            Assert.True(doc1.IsHead);
        });

        // Verify legacy-flagged is now Pending, findings cleared
        var doc2 = await _repo.GetAsync("legacy-flagged", "rev-1", TestContext.Current.CancellationToken);
        Assert.NotNull(doc2);
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Pending, doc2!.Status);
            Assert.Null(doc2.PolicyVersion);
            Assert.Empty(doc2.Findings);
        });

        // Verify v1-error is now Pending, IsHead preserved
        var doc3 = await _repo.GetAsync("v1-error", "rev-1", TestContext.Current.CancellationToken);
        Assert.NotNull(doc3);
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Pending, doc3!.Status);
            Assert.Null(doc3.PolicyVersion);
            Assert.False(doc3.IsHead);
        });

        // Verify the pending requirement was raised in place
        var doc6 = await _repo.GetAsync("already-pending", "rev-1", TestContext.Current.CancellationToken);
        Assert.NotNull(doc6);
        Assert.Equal(2, doc6!.RequiredPolicyVersion);

        // Verify v2-verified is unchanged
        var doc4 = await _repo.GetAsync("v2-verified", "rev-1", TestContext.Current.CancellationToken);
        Assert.NotNull(doc4);
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Verified, doc4!.Status);
            Assert.Equal(2, doc4.PolicyVersion);
        });

        // Verify a newer result is not downgraded by an older worker
        var doc5 = await _repo.GetAsync("v3-verified", "rev-1", TestContext.Current.CancellationToken);
        Assert.NotNull(doc5);
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Verified, doc5!.Status);
            Assert.Equal(3, doc5.PolicyVersion);
        });

        // Idempotency: Running RequeueOutdatedAsync again returns 0
        var secondRequeue = await _repo.RequeueOutdatedAsync(2, TestContext.Current.CancellationToken);
        Assert.Equal(0, secondRequeue);
    }
}
