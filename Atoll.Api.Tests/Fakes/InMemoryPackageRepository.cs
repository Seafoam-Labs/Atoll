using Atoll.Api.Services.Packages;

namespace Atoll.Api.Tests.Fakes;

internal sealed class InMemoryPackageRepository : IPackageRepository
{
    private readonly Dictionary<string, PackageDocument> _docs = new(StringComparer.Ordinal);

    // Bulk seeding and refresh process pkgbases concurrently, so every access is guarded.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PackageRevisionContentDocument> _revisions = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<string> result = [.. _docs.Keys];
            return Task.FromResult(result);
        }
    }

    public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_docs.ContainsKey(packageName));
        }
    }

    public Task<PackageDocument?> GetHeadAsync(string packageName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_docs.TryGetValue(packageName, out var doc) ? doc : null);
        }
    }

    public Task<string?> GetHeadRevisionIdAsync(string packageName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_docs.TryGetValue(packageName, out var doc) ? doc.HeadRevisionId : null);
        }
    }

    public Task<PackageRevisionContentDocument?> GetRevisionAsync(
        string packageName,
        string revisionId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var id = PackageSchema.RevisionDocumentId(packageName, revisionId);
            return Task.FromResult(_revisions.TryGetValue(id, out var revision) ? revision : null);
        }
    }

    public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(
        string packageName,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(packageName, out var doc))
                return Task.FromResult<IReadOnlyList<PackageVersion>>([]);

            IReadOnlyList<PackageVersion> result =
            [
                .. doc.Revisions
                    .Select(r => new PackageVersion(r.RevisionId, r.CreatedAt, r.Message, r.Author))
            ];

            return Task.FromResult(result);
        }
    }

    public Task InsertSeedAsync(PackageDocument doc, PackageRevisionContentDocument revision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_docs.TryAdd(doc.PackageName, doc))
                throw new PackageConflictException(doc.PackageName);

            _revisions[revision.Id] = revision;
            return Task.CompletedTask;
        }
    }

    public Task AppendRevisionAsync(
        string packageName,
        PackageRevisionContentDocument revision,
        int maxRevisions,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(packageName, out var existing))
                throw new KeyNotFoundException($"Package '{packageName}' not found.");

            // Upsert: revision ids are content hashes, so identical content can legitimately
            // reappear and its document may already exist.
            _revisions[revision.Id] = revision;

            var revisions = new List<PackageRevisionDocument>
            {
                new()
                {
                    RevisionId = revision.RevisionId,
                    CreatedAt = revision.CreatedAt,
                    Author = revision.Author,
                    Message = revision.Message
                }
            };
            revisions.AddRange(existing.Revisions);
            var retained = revisions.Take(maxRevisions).ToList();

            // Mirrors MongoPackageRepository: delete revision documents that no longer appear
            // anywhere in the retained list. The freshly appended id is always retained.
            var retainedIds = retained.Select(r => r.RevisionId).ToHashSet(StringComparer.Ordinal);
            foreach (var old in existing.Revisions.Where(old => !retainedIds.Contains(old.RevisionId)))
                _revisions.Remove(PackageSchema.RevisionDocumentId(packageName, old.RevisionId));

            _docs[packageName] = new PackageDocument
            {
                Id = existing.Id,
                PackageName = existing.PackageName,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                HeadRevisionId = revision.RevisionId,
                Revisions = retained,
                UpstreamPackageBase = existing.UpstreamPackageBase,
                LastSyncedUpstreamHead = existing.LastSyncedUpstreamHead,
                LastSyncAttemptAt = existing.LastSyncAttemptAt,
                LastSyncSucceededAt = existing.LastSyncSucceededAt,
                LastSyncError = existing.LastSyncError
            };

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<PackageSyncState>> ListSyncStatesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<PackageSyncState> result =
            [
                .. _docs.Values
                    .Select(d => new PackageSyncState
                    {
                        PackageName = d.PackageName,
                        UpstreamPackageBase = d.UpstreamPackageBase,
                        LastSyncedUpstreamHead = d.LastSyncedUpstreamHead,
                        LastSyncSucceededAt = d.LastSyncSucceededAt
                    })
            ];

            return Task.FromResult(result);
        }
    }

    public Task UpdateSyncStateAsync(
        IReadOnlyCollection<string> packageNames,
        string? upstreamHead,
        bool succeeded,
        string? error,
        CancellationToken ct = default)
    {
        lock (_gate)
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
    }

    public Task DeleteAsync(string packageName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _docs.Remove(packageName);

            // Cascade: mirrors MongoPackageRepository.DeleteAsync.
            var prefix = packageName + ":";
            foreach (var id in _revisions.Keys.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _revisions.Remove(id);

            return Task.CompletedTask;
        }
    }
}