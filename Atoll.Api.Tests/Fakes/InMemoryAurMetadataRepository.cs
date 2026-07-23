using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;

namespace Atoll.Api.Tests.Fakes;

internal sealed class InMemoryAurMetadataRepository : IAurMetadataRepository
{
    private readonly Dictionary<string, List<AurPackageMetadata>> _batchesByPointer = new(StringComparer.Ordinal);
    private string? _activeBatchId;

    public Task SaveAsync(IEnumerable<AurPackageMetadata> packages, CancellationToken ct)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var materialized = packages.ToList();
        _batchesByPointer[batchId] = materialized;

        var previous = _activeBatchId;
        _activeBatchId = batchId;

        if (previous is not null) _batchesByPointer.Remove(previous);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AurPackageMetadata>> LoadAsync(CancellationToken ct)
    {
        IReadOnlyList<AurPackageMetadata> result =
            _activeBatchId is not null && _batchesByPointer.TryGetValue(_activeBatchId, out var batch)
                ? batch
                : Array.Empty<AurPackageMetadata>();

        return Task.FromResult(result);
    }

    public Task<bool> ExistsAsync(CancellationToken ct)
    {
        return Task.FromResult(_activeBatchId is not null);
    }

    public Task<long> CountAsync(CancellationToken ct)
    {
        var count = _activeBatchId is not null && _batchesByPointer.TryGetValue(_activeBatchId, out var batch)
            ? batch.Count
            : 0;

        return Task.FromResult((long)count);
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        _batchesByPointer.Clear();
        _activeBatchId = null;
        return Task.CompletedTask;
    }
}