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

    public async Task<PackageSecurityScanDocument?> GetAsync(string packageName, CancellationToken ct = default)
    {
        return await _scans.Find(x => x.Id == packageName).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyCollection<string>> ListPackageNamesAsync(CancellationToken ct = default)
    {
        return await _scans
            .Find(Builders<PackageSecurityScanDocument>.Filter.Empty)
            .Project(x => x.Id)
            .ToListAsync(ct);
    }

    public async Task MarkPendingAsync(string packageName, string revisionId, CancellationToken ct = default)
    {
        var update = Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.RevisionId, revisionId)
            .Set(x => x.Status, SecurityStatus.Pending)
            .Set(x => x.Findings, [])
            .Unset(x => x.ScannedAt)
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);

        await _scans.UpdateOneAsync(
            x => x.Id == packageName,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    public async Task EnsurePendingAsync(string packageName, string revisionId, CancellationToken ct = default)
    {
        var update = Builders<PackageSecurityScanDocument>.Update
            .SetOnInsert(x => x.RevisionId, revisionId)
            .SetOnInsert(x => x.Status, SecurityStatus.Pending)
            .SetOnInsert(x => x.Findings, []);

        await _scans.UpdateOneAsync(
            x => x.Id == packageName,
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
            Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.Id, packageName),
            Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.RevisionId, revisionId),
            Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.LeaseOwner, owner));
        var update = Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.Status, result.Status)
            .Set(x => x.Findings, result.Findings.ToList())
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
            Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.Id, packageName),
            Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.RevisionId, revisionId),
            Builders<PackageSecurityScanDocument>.Filter.Eq(x => x.LeaseOwner, owner));
        var update = Builders<PackageSecurityScanDocument>.Update
            .Set(x => x.Status, SecurityStatus.Error)
            .Set(x => x.Findings, [])
            .Set(x => x.ScannedAt, DateTimeOffset.UtcNow)
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);

        await _scans.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task ReleaseScanClaimAsync(string packageName, string owner, CancellationToken ct = default)
    {
        var update = Builders<PackageSecurityScanDocument>.Update
            .Unset(x => x.LeaseUntil)
            .Unset(x => x.LeaseOwner);

        await _scans.UpdateOneAsync(
            x => x.Id == packageName && x.LeaseOwner == owner,
            update,
            cancellationToken: ct);
    }

    private void EnsureIndexes()
    {
        var keys = Builders<PackageSecurityScanDocument>.IndexKeys;
        _scans.Indexes.CreateOne(
            new CreateIndexModel<PackageSecurityScanDocument>(
                keys.Ascending(x => x.Status).Ascending(x => x.LeaseUntil)));
    }
}