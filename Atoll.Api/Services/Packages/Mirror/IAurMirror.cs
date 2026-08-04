namespace Atoll.Api.Services.Packages.Mirror;

public sealed record BulkFetchResult(
    IReadOnlyList<string> Succeeded,
    IReadOnlyList<string> Failed);

public interface IAurMirror
{
    Task EnsureInitializedAsync(CancellationToken ct = default);

    Task<IReadOnlySet<string>> ListBranchesAsync(CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string>> ListBranchHeadsAsync(CancellationToken ct = default);

    Task<BulkFetchResult> FetchAsync(IReadOnlyList<string> pkgBases, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string>> ReadFilesAsync(string pkgBase, CancellationToken ct = default);
}