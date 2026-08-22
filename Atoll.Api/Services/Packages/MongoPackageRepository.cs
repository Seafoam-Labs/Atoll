using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Atoll.Api.Services.Packages;

public sealed class MongoPackageRepository : IPackageRepository
{
    private readonly IMongoCollection<PackageDocument> _packages;
    private readonly IMongoCollection<PackageRevisionContentDocument> _revisions;

    public MongoPackageRepository(IMongoClient client, IOptions<AtollOptions> options)
    {
        var o = options.Value.Mongo;
        var db = client.GetDatabase(o.Database);
        _packages = db.GetCollection<PackageDocument>(o.Collections.Packages);
        _revisions = db.GetCollection<PackageRevisionContentDocument>(o.Collections.PackageRevisions);

        EnsureIndexes();
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        return await _packages
            .Find(Builders<PackageDocument>.Filter.Empty)
            .Project(p => p.PackageName)
            .ToListAsync(ct);
    }

    public Task<long> CountAsync(CancellationToken ct = default)
    {
        // An empty-filter count is answered from an index-only COUNT_SCAN instead of
        // streaming and deserializing every PackageName.
        return _packages.CountDocumentsAsync(
            Builders<PackageDocument>.Filter.Empty,
            cancellationToken: ct);
    }

    public async Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
    {
        var count = await _packages.CountDocumentsAsync(
            Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName),
            new CountOptions { Limit = 1 },
            ct);
        return count > 0;
    }

    public async Task<PackageDocument?> GetHeadAsync(string packageName, CancellationToken ct = default)
    {
        return await _packages
            .Find(Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<string?> GetHeadRevisionIdAsync(string packageName, CancellationToken ct = default)
    {
        return await _packages
            .Find(Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName))
            .Project(p => p.HeadRevisionId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PackageRevisionContentDocument?> GetRevisionAsync(
        string packageName,
        string revisionId,
        CancellationToken ct = default)
    {
        var id = PackageSchema.RevisionDocumentId(packageName, revisionId);
        return await _revisions
            .Find(Builders<PackageRevisionContentDocument>.Filter.Eq(r => r.Id, id))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(
        string packageName,
        CancellationToken ct = default)
    {
        var doc = await _packages
            .Find(Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName))
            .FirstOrDefaultAsync(ct);

        if (doc is null)
            return [];

        return
        [
            .. doc.Revisions
                .Select(r => new PackageVersion(r.RevisionId, r.CreatedAt, r.Message, r.Author))
        ];
    }

    public async Task InsertSeedAsync(PackageDocument doc, PackageRevisionContentDocument revision, CancellationToken ct = default)
    {
        try
        {
            await _revisions.InsertOneAsync(revision, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new PackageConflictException(doc.PackageName);
        }

        try
        {
            await _packages.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            await _revisions.DeleteOneAsync(
                Builders<PackageRevisionContentDocument>.Filter.Eq(r => r.Id, revision.Id), ct);
            throw new PackageConflictException(doc.PackageName);
        }
    }

    public async Task AppendRevisionAsync(
        string packageName,
        PackageRevisionContentDocument revision,
        int maxRevisions,
        CancellationToken ct = default)
    {
        var doc = await _packages
            .Find(Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName))
            .FirstOrDefaultAsync(ct);

        if (doc is null)
            throw new KeyNotFoundException($"Package '{packageName}' not found.");

        // Upsert: revision ids are content hashes, so identical content can legitimately
        // reappear and its document may already exist.
        await _revisions.ReplaceOneAsync(
            Builders<PackageRevisionContentDocument>.Filter.Eq(r => r.Id, revision.Id),
            revision,
            new ReplaceOptions { IsUpsert = true },
            ct);

        var metadata = new PackageRevisionDocument
        {
            RevisionId = revision.RevisionId,
            CreatedAt = revision.CreatedAt,
            Author = revision.Author,
            Message = revision.Message
        };

        // Push at position 0 (newest first) and slice to keep the last maxRevisions.
        var update = Builders<PackageDocument>.Update
            .Set(p => p.HeadRevisionId, revision.RevisionId)
            .Set(p => p.UpdatedAt, DateTimeOffset.UtcNow)
            .PushEach(p => p.Revisions, [metadata], position: 0, slice: maxRevisions);

        var result = await _packages.UpdateOneAsync(
            Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName),
            update,
            cancellationToken: ct);

        if (result.MatchedCount == 0)
            throw new KeyNotFoundException($"Package '{packageName}' not found.");

        await DeleteEvictedRevisionDocsAsync(packageName, doc.Revisions, revision.RevisionId, maxRevisions, ct);
    }

    public async Task<IReadOnlyList<PackageSyncState>> ListSyncStatesAsync(CancellationToken ct = default)
    {
        return await _packages
            .Find(Builders<PackageDocument>.Filter.Empty)
            .Project(p => new PackageSyncState
            {
                PackageName = p.PackageName,
                UpstreamPackageBase = p.UpstreamPackageBase,
                LastSyncedUpstreamHead = p.LastSyncedUpstreamHead,
                LastSyncSucceededAt = p.LastSyncSucceededAt
            })
            .ToListAsync(ct);
    }

    public Task UpdateSyncStateAsync(
        IReadOnlyCollection<string> packageNames,
        string? upstreamHead,
        bool succeeded,
        string? error,
        CancellationToken ct = default)
    {
        if (packageNames.Count == 0) return Task.CompletedTask;

        var now = DateTimeOffset.UtcNow;
        const int maxErrorLength = 500;
        string? truncatedError;
        if (succeeded)
            truncatedError = null;
        else
            truncatedError = string.IsNullOrEmpty(error)
                ? null
                : error[..Math.Min(error.Length, maxErrorLength)];

        var update = succeeded
            ? Builders<PackageDocument>.Update
                .Set(p => p.LastSyncedUpstreamHead, upstreamHead)
                .Set(p => p.LastSyncSucceededAt, now)
                .Set(p => p.LastSyncAttemptAt, now)
                .Set(p => p.LastSyncError, null)
            : Builders<PackageDocument>.Update
                .Set(p => p.LastSyncAttemptAt, now)
                .Set(p => p.LastSyncError, truncatedError);

        return _packages.UpdateManyAsync(
            Builders<PackageDocument>.Filter.In(p => p.PackageName, [.. packageNames]),
            update,
            cancellationToken: ct);
    }

    public async Task DeleteAsync(string packageName, CancellationToken ct = default)
    {
        await _packages.DeleteOneAsync(
            Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName),
            ct);

        await _revisions.DeleteManyAsync(
            Builders<PackageRevisionContentDocument>.Filter.Eq(r => r.PackageName, packageName),
            ct);
    }

    private async Task DeleteEvictedRevisionDocsAsync(
        string packageName,
        IReadOnlyList<PackageRevisionDocument> previousRevisions,
        string appendedRevisionId,
        int maxRevisions,
        CancellationToken ct)
    {
        var retained = new HashSet<string>(StringComparer.Ordinal) { appendedRevisionId };
        foreach (var revision in previousRevisions.Take(Math.Max(0, maxRevisions - 1)))
            retained.Add(revision.RevisionId);

        var evictedDocIds = previousRevisions
            .Select(r => r.RevisionId)
            .Where(revisionId => !retained.Contains(revisionId))
            .Distinct()
            .Select(revisionId => PackageSchema.RevisionDocumentId(packageName, revisionId))
            .ToList();

        if (evictedDocIds.Count == 0) return;

        await _revisions.DeleteManyAsync(
            Builders<PackageRevisionContentDocument>.Filter.In(r => r.Id, evictedDocIds),
            ct);
    }

    private void EnsureIndexes()
    {
        _packages.Indexes.CreateOne(
            new CreateIndexModel<PackageDocument>(
                Builders<PackageDocument>.IndexKeys.Ascending(p => p.PackageName)));

        _revisions.Indexes.CreateOne(
            new CreateIndexModel<PackageRevisionContentDocument>(
                Builders<PackageRevisionContentDocument>.IndexKeys.Ascending(r => r.PackageName)));
    }
}