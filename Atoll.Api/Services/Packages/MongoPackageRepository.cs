using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Atoll.Api.Services.Packages;

public sealed class MongoPackageRepository : IPackageRepository
{
    private readonly IMongoCollection<PackageDocument> _packages;

    public MongoPackageRepository(IMongoClient client, IOptions<AtollOptions> options)
    {
        var o = options.Value.Mongo;
        var db = client.GetDatabase(o.Database);
        _packages = db.GetCollection<PackageDocument>(o.Collections.Packages);

        EnsureIndexes();
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        return await _packages
            .Find(Builders<PackageDocument>.Filter.Empty)
            .Project(p => p.PackageName)
            .ToListAsync(ct);
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

    public async Task<PackageHeadFiles?> GetHeadFilesAsync(string packageName, CancellationToken ct = default)
    {
        return await _packages
            .Find(Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName))
            .Project(p => new PackageHeadFiles
            {
                HeadRevisionId = p.HeadRevisionId,
                Files = p.Files
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PackageRevisionDocument?> GetRevisionAsync(
        string packageName,
        string revisionId,
        CancellationToken ct = default)
    {
        var doc = await _packages
            .Find(Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName))
            .FirstOrDefaultAsync(ct);

        return doc?.Revisions.FirstOrDefault(r => r.RevisionId == revisionId);
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

        return doc.Revisions
            .Select(r => new PackageVersion(r.RevisionId, r.CreatedAt, r.Message, r.Author))
            .ToList();
    }

    public async Task InsertSeedAsync(PackageDocument doc, CancellationToken ct = default)
    {
        try
        {
            await _packages.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new PackageConflictException(doc.PackageName);
        }
    }

    public async Task AppendRevisionAsync(
        string packageName,
        PackageRevisionDocument revision,
        Dictionary<string, PackageFile> headFiles,
        int maxRevisions,
        CancellationToken ct = default)
    {
        // Push at position 0 (newest first) and slice to keep the last maxRevisions.
        var update = Builders<PackageDocument>.Update
            .Set(p => p.Files, headFiles)
            .Set(p => p.HeadRevisionId, revision.RevisionId)
            .Set(p => p.UpdatedAt, DateTimeOffset.UtcNow)
            .PushEach(p => p.Revisions, [revision], position: 0, slice: maxRevisions);

        var result = await _packages.UpdateOneAsync(
            Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName),
            update,
            cancellationToken: ct);

        if (result.MatchedCount == 0)
            throw new KeyNotFoundException($"Package '{packageName}' not found.");
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
            Builders<PackageDocument>.Filter.In(p => p.PackageName, packageNames.ToList()),
            update,
            cancellationToken: ct);
    }

    public async Task DeleteAsync(string packageName, CancellationToken ct = default)
    {
        await _packages.DeleteOneAsync(
            Builders<PackageDocument>.Filter.Eq(p => p.PackageName, packageName),
            ct);
    }

    private void EnsureIndexes()
    {
        _packages.Indexes.CreateOne(
            new CreateIndexModel<PackageDocument>(
                Builders<PackageDocument>.IndexKeys.Ascending(p => p.PackageName)));
    }
}