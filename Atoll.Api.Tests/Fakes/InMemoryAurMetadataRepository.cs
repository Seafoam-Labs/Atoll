using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Persistence;

namespace Atoll.Api.Tests.Fakes;

internal sealed class InMemoryAurMetadataRepository : IAurMetadataRepository
{
    private readonly Dictionary<string, AurPackageMetadata> _byName = new(StringComparer.Ordinal);

    public Task SyncAsync(AurMetadataDelta delta, CancellationToken ct)
    {
        foreach (var package in delta.Upserts) _byName[package.Name] = package;
        foreach (var name in delta.Removals) _byName.Remove(name);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AurPackageMetadata>> LoadAsync(CancellationToken ct)
    {
        IReadOnlyList<AurPackageMetadata> result = [.. _byName.Values];
        return Task.FromResult(result);
    }

    public Task<long> CountAsync(CancellationToken ct)
    {
        return Task.FromResult((long)_byName.Count);
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        _byName.Clear();
        return Task.CompletedTask;
    }
}
