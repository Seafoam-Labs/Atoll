using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Atoll.Api.Services.Security;

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

    public async Task MarkPendingAsync(
        string packageName,
        string revisionId,
        bool isHead,
        CancellationToken ct = default)
    {
        var update = Builders<PackageSecurityScanDocument>.Update
            .SetOnInsert(x => x.PackageName, packageName)
            .SetOnInsert(x => x.RevisionId, revisionId)
            .Set(x => x.IsHead, isHead)
            .Set(x => x.Status, SecurityStatus.Pending)
            .Set(x => x.Findings, [])
            .Unset(x => x.ScannedAt)
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);

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
        CancellationToken ct = default)
    {
        var update = Builders<PackageSecurityScanDocument>.Update
            .SetOnInsert(x => x.PackageName, packageName)
            .SetOnInsert(x => x.RevisionId, revisionId)
            .SetOnInsert(x => x.IsHead, isHead)
            .SetOnInsert(x => x.Status, SecurityStatus.Pending)
            .SetOnInsert(x => x.Findings, []);

        await _scans.UpdateOneAsync(
            x => x.Id == PackageSecurityScanDocument.ComposeId(packageName, revisionId),
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    public async Task<PackageSecurityScanDocument?> TryClaimPendingScanAsync(
        string owner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var filter = Builders<PackageSecurityScanDocument>.Filter.And(
            Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.Status, SecurityStatus.Pending),
            Builders<PackageSecurityScanDocument>.Filter.Or(
                Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.LeaseUntil, null),
                Builders<PackageSecurityScanDocument>.Filter.Lt(x => x.LeaseUntil, now)));
        var update = Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.LeaseUntil, now.Add(leaseDuration))
            .Set(x => x.LeaseOwner, owner);

        return await _scans.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<PackageSecurityScanDocument> { ReturnDocument = ReturnDocument.After },
            ct);
    }

    public async Task CompleteScanAsync(
        string packageName,
        string revisionId,
        string owner,
        ScanResult result,
        CancellationToken ct = default)
    {
        var filter = Builders<PackageSecurityScanDocument>.Filter.And(
            Builders<PackageSecurityScanDocument>.Filter.Eq(
                x => x.Id, PackageSecurityScanDocument.ComposeId(packageName, revisionId)),
            Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.LeaseOwner, owner));
        var update = Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.Status, result.Status)
            .Set(x => x.Findings, [.. result.Findings])
            .Set(x => x.ScannedAt, DateTimeOffset.UtcNow)
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);

        await _scans.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task MarkScanErrorAsync(
        string packageName,
        string revisionId,
        string owner,
        CancellationToken ct = default)
    {
        var filter = Builders<PackageSecurityScanDocument>.Filter.And(
            Builders<PackageSecurityScanDocument>.Filter.Eq(
                x => x.Id, PackageSecurityScanDocument.ComposeId(packageName, revisionId)),
            Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.LeaseOwner, owner));
        var update = Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.Status, SecurityStatus.Error)
            .Set(x => x.Findings, [])
            .Set(x => x.ScannedAt, DateTimeOffset.UtcNow)
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);

        await _scans.UpdateOneAsync(filter, update, cancellationToken: ct);
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

    private void EnsureIndexes()
    {
        var keys = Builders<PackageSecurityScanDocument>.IndexKeys;
        _scans.Indexes.CreateMany(
        [
            new CreateIndexModel<PackageSecurityScanDocument>(
                keys.Ascending(x => x.Status).Ascending(x => x.LeaseUntil)),
            new CreateIndexModel<PackageSecurityScanDocument>(
                keys.Ascending(x => x.PackageName).Ascending(x => x.IsHead)),
            new CreateIndexModel<PackageSecurityScanDocument>(
                keys.Ascending(x => x.PackageName)),
            // Covers the status dashboard's head-status aggregation (match on IsHead,
            // group by Status) without touching non-head revision scans.
            new CreateIndexModel<PackageSecurityScanDocument>(
                keys.Ascending(x => x.IsHead).Ascending(x => x.Status))
        ]);
    }
}