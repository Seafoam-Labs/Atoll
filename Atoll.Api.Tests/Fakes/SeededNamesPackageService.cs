using Atoll.Api.Services.Packages;

namespace Atoll.Api.Tests.Fakes;

internal sealed class SeededNamesPackageService(IReadOnlyList<string> seededNames) : IPackageService
{
    /// <summary>Counts calls so tests can pin snapshot caching.</summary>
    internal int ListCalls { get; private set; }

    public Task<IReadOnlyList<string>> ListAsync()
    {
        ListCalls++;
        return Task.FromResult(seededNames);
    }

    public Task<int> CountAsync()
    {
        return Task.FromResult(seededNames.Count);
    }

    public Task<PackageIndexResponse> GetIndexPageAsync(
        int page,
        int limit,
        PackageIndexSortBy sortBy = PackageIndexSortBy.Name,
        PackageIndexSortOrder? order = null,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
        => throw new NotSupportedException();

    public Task DeleteAsync(string packageName, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
        => throw new NotSupportedException();

    public Task<bool> AppendRevisionFromUpstreamAsync(
        string packageName, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
        => throw new NotSupportedException();

}
