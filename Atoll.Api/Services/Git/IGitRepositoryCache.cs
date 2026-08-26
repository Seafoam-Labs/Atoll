namespace Atoll.Api.Services.Git;

/// <summary>
///     Owns the rebuildable bare-repository cache for served packages: path resolution,
///     the per-repository lock, the up-to-date marker, materialization, and deletion.
/// </summary>
public interface IGitRepositoryCache
{
    /// <summary>Absolute path of the package's bare repository, or null when no cache root is configured.</summary>
    string? GetRepositoryPath(string packageName);

    /// <summary>Materializes the bare repository for the package when it is missing or stale.</summary>
    Task EnsureRepositoryAsync(string packageName, CancellationToken ct = default);

    /// <summary>
    ///     Deletes the package's derived state (scan records and the on-disk repository) and
    ///     then the authoritative package documents via <paramref name="deletePackageAsync" />,
    ///     all under the repository's materialization lock.
    /// </summary>
    Task DeleteAsync(string packageName, Func<CancellationToken, Task> deletePackageAsync, CancellationToken ct = default);
}