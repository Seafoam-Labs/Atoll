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
                .ToList()
        };

        _docs[packageName] = updated;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string packageName, CancellationToken ct = default)
    {
        _docs.Remove(packageName);
        return Task.CompletedTask;
    }
}