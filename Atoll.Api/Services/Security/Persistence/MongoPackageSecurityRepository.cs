using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Atoll.Api.Services.Security.Persistence;

public sealed class MongoPackageSecurityRepository : IPackageSecurityRepository
{
    private readonly IMongoCollection<PackageSecurityScanDocument> _scans;

    public MongoPackageSecurityRepository(IMongoClient client, IOptions<AtollOptions> options)
    {
        var mongo = options.Value.Mongo;
        _scans = client.GetDatabase(mongo.Database)
            .GetCollection<PackageSecurityScanDocument>(mongo.Collections.PackageSecurityScans);

        EnsureIndexes();
    }

    public async Task<PackageSecurityScanDocument?> GetAsync(
        string packageName,
        string revisionId,
        CancellationToken ct = default)
    {
        return await _scans
            .Find(x => x.Id == PackageSecurityScanDocument.ComposeId(packageName, revisionId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PackageSecurityScanDocument?> GetHeadAsync(string packageName, CancellationToken ct = default)
    {
        return await _scans
            .Find(x => x.PackageName == packageName && x.IsHead)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyCollection<PackageSecurityScanDocument>> ListForPackageAsync(
        string packageName,
        CancellationToken ct = default)
    {
        return await _scans
            .Find(x => x.PackageName == packageName)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RevisionScanStatus>> ListStatusesForPackageAsync(
        string packageName,
        CancellationToken ct = default)
    {
        var projected = await _scans
            .Find(x => x.PackageName == packageName)
            .Project(x => new { x.RevisionId, x.Status })
            .ToListAsync(ct);

        return [.. projected.Select(x => new RevisionScanStatus(x.RevisionId, x.Status))];
    }

    public async Task<IReadOnlyCollection<string>> ListPackageNamesAsync(CancellationToken ct = default)
    {
        var cursor = await _scans.DistinctAsync(x => x.PackageName, Builders<PackageSecurityScanDocument>.Filter.Empty, null, ct);
        return await cursor.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HeadScanStatus>> ListHeadStatusesAsync(CancellationToken ct = default)
    {
        var projected = await _scans
            .Find(x => x.IsHead)
            .Project(x => new { x.PackageName, x.Status })
            .ToListAsync(ct);

        return [.. projected.Select(x => new HeadScanStatus(x.PackageName, x.Status))];
    }

    public async Task<HeadScanStatusCounts> CountHeadStatusesAsync(CancellationToken ct = default)
    {
        var counts = await _scans
            .Aggregate()
            .Match(x => x.IsHead)
            .Group(x => x.Status, g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        long CountOf(SecurityStatus status) => counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;

        return new HeadScanStatusCounts(
            CountOf(SecurityStatus.Verified),
            CountOf(SecurityStatus.Flagged),
            CountOf(SecurityStatus.Pending),
            CountOf(SecurityStatus.Error));
    }

    public async Task<long> CountPendingAsync(CancellationToken ct = default)
    {
        return await _scans.CountDocumentsAsync(x => x.Status == SecurityStatus.Pending, cancellationToken: ct);
    }

    public async Task<long> RequeueOutdatedAsync(int currentPolicyVersion, CancellationToken ct = default)
    {
        var filter = Builders<PackageSecurityScanDocument>.Filter;
        var outdatedResult = filter.And(
            filter.Ne(x => x.Status, SecurityStatus.Pending),
            filter.Or(
                filter.Eq(x => x.PolicyVersion, null),
                filter.Lt(x => x.PolicyVersion, currentPolicyVersion)));
        long modified = 0;

        // Completed results whose outcome predates the current policy: requeue and raise the
        // requirement to the current version. Max semantics: a requirement already at or above
        // the current version is kept untouched so an older reconciler cannot lower newer work.
        var raiseRequirement = await _scans.UpdateManyAsync(
            filter.And(outdatedResult, filter.Or(
                filter.Eq(x => x.RequiredPolicyVersion, null),
                filter.Lt(x => x.RequiredPolicyVersion, currentPolicyVersion))),
            PendingResetUpdate().Max(x => x.RequiredPolicyVersion, currentPolicyVersion),
            cancellationToken: ct);
        modified += raiseRequirement.ModifiedCount;

        var keepRequirement = await _scans.UpdateManyAsync(
            filter.And(outdatedResult, filter.Gte(x => x.RequiredPolicyVersion, currentPolicyVersion)),
            PendingResetUpdate(),
            cancellationToken: ct);
        modified += keepRequirement.ModifiedCount;

        // Pending documents whose requirement predates the current policy: raise the requirement
        // and clear the lease, fencing a lower-version worker that claimed before the requirement rose.
        var raisePending = await _scans.UpdateManyAsync(
            filter.And(
                filter.Eq(x => x.Status, SecurityStatus.Pending),
                filter.Or(
                    filter.Eq(x => x.RequiredPolicyVersion, null),
                    filter.Lt(x => x.RequiredPolicyVersion, currentPolicyVersion))),
            Builders<PackageSecurityScanDocument>.Update
                .Max(x => x.RequiredPolicyVersion, currentPolicyVersion)
                .Unset(x => x.LeaseUntil)
                .Unset(x => x.LeaseOwner),
            cancellationToken: ct);
        modified += raisePending.ModifiedCount;

        return modified;
    }

    public async Task MarkPendingAsync(
        string packageName,
        string revisionId,
        bool isHead,
        int requiredPolicyVersion,
        CancellationToken ct = default)
    {
        // $max gives monotonic semantics in a single atomic update: an existing requirement at or
        // above the requested version is kept, everything else is raised. The upsert never raises
        // a duplicate-key conflict because it only fires when no document with this id exists.
        var update = PendingResetUpdate()
            .SetOnInsert(x => x.PackageName, packageName)
            .SetOnInsert(x => x.RevisionId, revisionId)
            .Set(x => x.IsHead, isHead)
            .Max(x => x.RequiredPolicyVersion, requiredPolicyVersion);

        await _scans.UpdateOneAsync(
            x => x.Id == PackageSecurityScanDocument.ComposeId(packageName, revisionId),
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    public async Task EnsurePendingAsync(
        string packageName,
        string revisionId,
        bool isHead,
        int requiredPolicyVersion,
        CancellationToken ct = default)
    {
        var update = Builders<PackageSecurityScanDocument>.Update
            .SetOnInsert(x => x.PackageName, packageName)
            .SetOnInsert(x => x.RevisionId, revisionId)
            .SetOnInsert(x => x.IsHead, isHead)
            .SetOnInsert(x => x.Status, SecurityStatus.Pending)
            .SetOnInsert(x => x.Findings, [])
            .SetOnInsert(x => x.RequiredPolicyVersion, requiredPolicyVersion);

        await _scans.UpdateOneAsync(
            x => x.Id == PackageSecurityScanDocument.ComposeId(packageName, revisionId),
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    public async Task<PackageSecurityScanDocument?> TryClaimPendingScanAsync(
        string owner,
        TimeSpan leaseDuration,
        int workerPolicyVersion,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var filter = Builders<PackageSecurityScanDocument>.Filter;
        var update = Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.LeaseUntil, now.Add(leaseDuration))
            .Set(x => x.LeaseOwner, owner);

        return await _scans.FindOneAndUpdateAsync(
            filter.And(
                filter.Eq(x => x.Status, SecurityStatus.Pending),
                filter.Or(
                    filter.Eq(x => x.LeaseUntil, null),
                    filter.Lt(x => x.LeaseUntil, now)),
                filter.Or(
                    filter.Eq(x => x.RequiredPolicyVersion, null),
                    filter.Lte(x => x.RequiredPolicyVersion, workerPolicyVersion))),
            update,
            new FindOneAndUpdateOptions<PackageSecurityScanDocument> { ReturnDocument = ReturnDocument.After },
            ct);
    }

    public async Task<bool> CompleteScanAsync(
        string packageName,
        string revisionId,
        string owner,
        ScanResult result,
        int policyVersion,
        CancellationToken ct = default)
    {
        var update = Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.Status, result.Status)
            .Set(x => x.Findings, [.. result.Findings])
            .Set(x => x.PolicyVersion, policyVersion)
            .Set(x => x.ScannedAt, DateTimeOffset.UtcNow)
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);

        return await UpdateClaimedScanAsync(packageName, revisionId, owner, policyVersion, update, ct);
    }

    public async Task<bool> MarkScanErrorAsync(
        string packageName,
        string revisionId,
        string owner,
        int policyVersion,
        CancellationToken ct = default)
    {
        var update = Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.Status, SecurityStatus.Error)
            .Set(x => x.Findings, [])
            .Set(x => x.PolicyVersion, policyVersion)
            .Set(x => x.ScannedAt, DateTimeOffset.UtcNow)
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);

        return await UpdateClaimedScanAsync(packageName, revisionId, owner, policyVersion, update, ct);
    }

    /// <summary>
    ///     Persists a terminal scan state only while the caller still owns a pending claim whose
    ///     required policy version it satisfies. Reconciliation can raise the requirement while a
    ///     scan is running, so claiming alone cannot fence the write.
    /// </summary>
    private async Task<bool> UpdateClaimedScanAsync(
        string packageName,
        string revisionId,
        string owner,
        int policyVersion,
        UpdateDefinition<PackageSecurityScanDocument> update,
        CancellationToken ct)
    {
        var filter = Builders<PackageSecurityScanDocument>.Filter;
        var result = await _scans.UpdateOneAsync(
            filter.And(
                filter.Eq(x => x.Id, PackageSecurityScanDocument.ComposeId(packageName, revisionId)),
                filter.Eq(x => x.Status, SecurityStatus.Pending),
                filter.Eq(x => x.LeaseOwner, owner),
                filter.Or(
                    filter.Eq(x => x.RequiredPolicyVersion, null),
                    filter.Lte(x => x.RequiredPolicyVersion, policyVersion))),
            update,
            cancellationToken: ct);
        return result.ModifiedCount > 0;
    }

    public async Task ReleaseScanClaimAsync(
        string packageName,
        string revisionId,
        string owner,
        CancellationToken ct = default)
    {
        var update = Builders<PackageSecurityScanDocument>.Update
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);

        await _scans.UpdateOneAsync(
            x => x.Id == PackageSecurityScanDocument.ComposeId(packageName, revisionId)
                 && x.LeaseOwner == owner,
            update,
            cancellationToken: ct);
    }

    public async Task PromoteHeadAsync(
        string packageName,
        string newHeadRevisionId,
        CancellationToken ct = default)
    {
        var demote = Builders<PackageSecurityScanDocument>.Update.Set(x => x.IsHead, false);
        await _scans.UpdateManyAsync(
            x => x.PackageName == packageName
                 && x.IsHead
                 && x.Id != PackageSecurityScanDocument.ComposeId(packageName, newHeadRevisionId),
            demote,
            cancellationToken: ct);

        var promote = Builders<PackageSecurityScanDocument>.Update.Set(x => x.IsHead, true);
        await _scans.UpdateOneAsync(
            x => x.Id == PackageSecurityScanDocument.ComposeId(packageName, newHeadRevisionId),
            promote,
            cancellationToken: ct);
    }

    public async Task DeleteAsync(string packageName, string revisionId, CancellationToken ct = default)
    {
        await _scans.DeleteOneAsync(
            x => x.Id == PackageSecurityScanDocument.ComposeId(packageName, revisionId),
            ct);
    }

    public async Task DeletePackageAsync(string packageName, CancellationToken ct = default)
    {
        await _scans.DeleteManyAsync(x => x.PackageName == packageName, ct);
    }

    /// <summary>Resets result and lease fields, moving a document back to the pending queue.</summary>
    private static UpdateDefinition<PackageSecurityScanDocument> PendingResetUpdate()
    {
        return Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.Status, SecurityStatus.Pending)
            .Set(x => x.Findings, [])
            .Unset(x => x.PolicyVersion)
            .Unset(x => x.ScannedAt)
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);
    }

    private void EnsureIndexes()
    {
        var keys = Builders<PackageSecurityScanDocument>.IndexKeys;
        _scans.Indexes.CreateMany(
        [
            // Serves the policy-aware pending claim: workers scan (status, requiredPolicyVersion,
            // leaseUntil) and only claim work their policy version satisfies.
            new CreateIndexModel<PackageSecurityScanDocument>(
                keys.Ascending(x => x.Status).Ascending(x => x.RequiredPolicyVersion).Ascending(x => x.LeaseUntil)),
            new CreateIndexModel<PackageSecurityScanDocument>(
                keys.Ascending(x => x.PackageName).Ascending(x => x.IsHead)),
            // Serves both head-status reads off the index alone: the package catalog's
            // (packageName, status) projection and the status dashboard's aggregation. Keeping
            // PackageName last means neither has to fetch head documents carrying findings.
            new CreateIndexModel<PackageSecurityScanDocument>(
                keys.Ascending(x => x.IsHead).Ascending(x => x.Status).Ascending(x => x.PackageName))
        ]);

        // CreateMany only adds, so every superseded shape needs a targeted drop. Drops are gated
        // on the live index list rather than suppressing IndexNotFound because DocumentDB reports
        // a missing index as a generic command error without MongoDB's codeName, which would
        // crash startup on a fresh cluster. Listing still surfaces authorization failures.
        var existing = _scans.Indexes.List()
            .ToList()
            .Select(ix => ix["name"].AsString)
            .ToHashSet();
        foreach (var superseded in new[]
                 {
                     // Pre-policy-aware claim index.
                     "status_1_leaseUntil_1",
                     // Strict prefix duplicate of (packageName, isHead).
                     "packageName_1",
                     // Too narrow to cover the head-status projection.
                     "isHead_1_status_1"
                 })
        {
            if (existing.Contains(superseded))
            {
                _scans.Indexes.DropOne(superseded);
            }
        }
    }
}