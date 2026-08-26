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
    ///     Acquires the repository's materialization lock and deletes its derived state. The returned
    ///     scope keeps the lock held so package deletion can remove authoritative data without a race.
    /// </summary>
    Task<IAsyncDisposable> BeginDeleteAsync(string packageName, CancellationToken ct = default);
}