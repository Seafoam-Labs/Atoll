using Atoll.Api.Services.Packages;

namespace Atoll.Api.Tests.Fakes;

internal sealed class InMemoryPackageRepository : IPackageRepository
{
    private readonly Dictionary<string, PackageDocument> _docs = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> result = _docs.Keys.ToList();
        return Task.FromResult(result);
    }

    public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
    {
        return Task.FromResult(_docs.ContainsKey(packageName));
    }

    public Task<PackageDocument?> GetHeadAsync(string packageName, CancellationToken ct = default)
    {
        return Task.FromResult(_docs.TryGetValue(packageName, out var doc) ? doc : null);
    }

    public Task<PackageHeadFiles?> GetHeadFilesAsync(string packageName, CancellationToken ct = default)
    {
        if (!_docs.TryGetValue(packageName, out var doc))
            return Task.FromResult<PackageHeadFiles?>(null);

        return Task.FromResult<PackageHeadFiles?>(new PackageHeadFiles
        {
            HeadRevisionId = doc.HeadRevisionId,
            Files = doc.Files
        });
    }

    public Task<PackageRevisionDocument?> GetRevisionAsync(
        string packageName,
        string revisionId,
        CancellationToken ct = default)
    {
        if (!_docs.TryGetValue(packageName, out var doc))
            return Task.FromResult<PackageRevisionDocument?>(null);

        var rev = doc.Revisions.FirstOrDefault(r => r.RevisionId == revisionId);
        return Task.FromResult(rev);
    }

    public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(
        string packageName,
        CancellationToken ct = default)
    {
        if (!_docs.TryGetValue(packageName, out var doc))
            return Task.FromResult<IReadOnlyList<PackageVersion>>([]);

        IReadOnlyList<PackageVersion> result = doc.Revisions
            .Select(r => new PackageVersion(r.RevisionId, r.CreatedAt, r.Message, r.Author))
            .ToList();

        return Task.FromResult(result);
    }

    public Task InsertSeedAsync(PackageDocument doc, CancellationToken ct = default)
    {
        return _docs.TryAdd(doc.PackageName, doc)
            ? Task.CompletedTask
            : throw new PackageConflictException(doc.PackageName);
    }

    public Task AppendRevisionAsync(
        string packageName,
        PackageRevisionDocument revision,
        Dictionary<string, PackageFile> headFiles,
        int maxRevisions,
        CancellationToken ct = default)
    {
        if (!_docs.TryGetValue(packageName, out var existing))
            throw new KeyNotFoundException($"Package '{packageName}' not found.");

        var updated = new PackageDocument
        {
            Id = existing.Id,
            PackageName = existing.PackageName,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadRevisionId = revision.RevisionId,
            Files = headFiles,
            Revisions = new List<PackageRevisionDocument> { revision }
                .Concat(existing.Revisions)
                .Take(maxRevisions)
                .ToList(),
            UpstreamPackageBase = existing.UpstreamPackageBase,
            LastSyncedUpstreamHead = existing.LastSyncedUpstreamHead,
            LastSyncAttemptAt = existing.LastSyncAttemptAt,
            LastSyncSucceededAt = existing.LastSyncSucceededAt,
            LastSyncError = existing.LastSyncError
        };

        _docs[packageName] = updated;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PackageSyncState>> ListSyncStatesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PackageSyncState> result = _docs.Values
            .Select(d => new PackageSyncState
            {
                PackageName = d.PackageName,
                UpstreamPackageBase = d.UpstreamPackageBase,
                LastSyncedUpstreamHead = d.LastSyncedUpstreamHead,
                LastSyncSucceededAt = d.LastSyncSucceededAt
            })
            .ToList();

        return Task.FromResult(result);
    }

    public Task UpdateSyncStateAsync(
        IReadOnlyCollection<string> packageNames,
        string? upstreamHead,
        bool succeeded,
        string? error,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var name in packageNames)
        {
            if (!_docs.TryGetValue(name, out var existing)) continue;

            _docs[name] = new PackageDocument
            {
                Id = existing.Id,
                PackageName = existing.PackageName,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = existing.UpdatedAt,
                HeadRevisionId = existing.HeadRevisionId,
                Files = existing.Files,
                Revisions = existing.Revisions,
                UpstreamPackageBase = existing.UpstreamPackageBase,
                LastSyncAttemptAt = now,
                LastSyncSucceededAt = succeeded ? now : existing.LastSyncSucceededAt,
                LastSyncedUpstreamHead = succeeded ? upstreamHead : existing.LastSyncedUpstreamHead,
                LastSyncError = succeeded ? null : error
            };
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string packageName, CancellationToken ct = default)
    {
        _docs.Remove(packageName);
        return Task.CompletedTask;
    }
}