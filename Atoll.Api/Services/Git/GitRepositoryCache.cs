using System.Collections.Concurrent;
using System.Text;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Microsoft.Extensions.Options;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Services.Git;

/// <summary>
///     The rebuildable bare-repository cache: path resolution, per-repository locking,
///     up-to-date marker calculation, deterministic history materialization, and deletion.
///     It reads package revisions and security statuses because both determine cloneable
///     history, and never mutates authoritative package data.
/// </summary>
public sealed class GitRepositoryCache(
    IPackageRepository repo,
    IPackageSecurityRepository securityRepository,
    IOptions<AtollOptions> options,
    ILogger<GitRepositoryCache> logger) : IGitRepositoryCache
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks = new();
    private readonly AtollOptions _options = options.Value;

    public string? GetRepositoryPath(string packageName)
    {
        var root = _options.Git.RepositoriesPath;
        return string.IsNullOrWhiteSpace(root)
            ? null
            : Path.GetFullPath(Path.Combine(root, packageName + ".git"));
    }


    public async Task EnsureRepositoryAsync(string packageName, CancellationToken ct = default)
    {
        var path = GetRepositoryPath(packageName);
        if (path is null)
            return;

        var doc = await repo.GetHeadAsync(packageName, ct);
        if (doc is null)
            return;

        // The materialized Git history is a function of the retained revisions *and* their scan
        // statuses: a rescan that flips a revision Verified <-> Flagged must change what a clone
        // can check out, so both feed the up-to-date marker below.
        var securityEnabled = _options.Security.Enabled;
        Dictionary<string, SecurityStatus>? statuses = null;
        if (securityEnabled)
            statuses = (await securityRepository.ListStatusesForPackageAsync(packageName, ct))
                .ToDictionary(s => s.RevisionId, s => s.Status, StringComparer.Ordinal);

        var marker = Path.Combine(path, ".atoll-head");
        var headMarker = ComputeHistoryMarker(doc, securityEnabled, statuses);

        if (IsUpToDate(path, marker, headMarker))
            return;

        var lockObj = RepoLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync(ct);
        try
        {
            // Re-read under the lock: a concurrent DeleteAsync can remove the package between
            // the initial read above and the lock acquisition, and materializing from the
            // stale document would resurrect a repository nothing ever cleans up.
            doc = await repo.GetHeadAsync(packageName, ct);
            if (doc is null)
                return;

            if (securityEnabled)
                statuses = (await securityRepository.ListStatusesForPackageAsync(packageName, ct))
                    .ToDictionary(s => s.RevisionId, s => s.Status, StringComparer.Ordinal);

            headMarker = ComputeHistoryMarker(doc, securityEnabled, statuses);
            if (IsUpToDate(path, marker, headMarker))
                return;

            Directory.CreateDirectory(path);

            if (!File.Exists(Path.Combine(path, "HEAD")))
            {
                string[] arguments = ["init", "--bare", "--quiet"];
                await GitClient.ExecuteAsync(path, arguments, null, null, ct);
            }

            var parent = string.Empty;
            var complete = true;
            foreach (var revision in OrderedRevisions(doc))
            {
                SecurityStatus? status = null;
                if (statuses is not null && statuses.TryGetValue(revision.RevisionId, out var scanStatus))
                    status = scanStatus;

                // Non-verified revisions are excluded from the cloneable history; gaps in the
                // commit chain are fine because Atoll synthesizes its own SHAs.
                if (!IsRevisionServable(securityEnabled, status))
                    continue;

                var content = await repo.GetRevisionAsync(packageName, revision.RevisionId, ct);
                if (content is null)
                {
                    logger.LogError(
                        "Revision content for {PackageName} revision {RevisionId} is missing; materializing the remaining history.",
                        packageName, revision.RevisionId);
                    complete = false;
                    continue;
                }

                var tree = await WriteTreeAsync(path, content.Files, ct);
                parent = await WriteCommitAsync(path, tree, parent, revision, ct);
            }

            if (!string.IsNullOrEmpty(parent))
            {
                string[] arguments = ["update-ref", "refs/heads/main", parent];
                await GitClient.ExecuteAsync(path, arguments, null, null, ct);
            }

            string[] arguments1 = ["symbolic-ref", "HEAD", "refs/heads/main"];
            await GitClient.ExecuteAsync(path, arguments1, null, null, ct);

            if (complete)
                await File.WriteAllTextAsync(marker, headMarker, ct);
        }
        finally
        {
            lockObj.Release();
        }
    }


    public async Task DeleteAsync(
        string packageName,
        Func<CancellationToken, Task> deletePackageAsync,
        CancellationToken ct = default)
    {
        var path = GetRepositoryPath(packageName);
        SemaphoreSlim? lockObj = null;
        if (path is not null)
        {
            lockObj = RepoLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await lockObj.WaitAsync(ct);
        }

        try
        {
            // Derived state goes first, the authoritative package document last: if a step
            // fails partway, the package stays visible to delete/reconcile retries and the
            // leftovers are reclaimed. Deleting the package document first would orphan any
            // state that survives a failure, since every cleanup path keys off it. Everything
            // runs under the repository lock, so concurrent materialization cannot
            // resurrect the directory between the cleanup steps.
            await securityRepository.DeletePackageAsync(packageName, ct);

            if (path is not null && Directory.Exists(path))
                Directory.Delete(path, true);

            await deletePackageAsync(ct);
        }
        finally
        {
            lockObj?.Release();
        }
    }

    // The marker and the commit loop must enumerate revisions in the same order, or the
    // up-to-date check would validate against a history different from the one materialized.
    private static IOrderedEnumerable<PackageRevisionDocument> OrderedRevisions(PackageDocument doc)
    {
        return doc.Revisions.OrderBy(r => r.CreatedAt);
    }

    private static bool IsUpToDate(string path, string marker, string headMarker)
    {
        return Directory.Exists(path)
               && File.Exists(Path.Combine(path, "HEAD"))
               && File.Exists(marker)
               && File.ReadAllText(marker) == headMarker;
    }

    /// <summary>
    ///     A revision enters the synthesized Git history only when its scan is verified, or when
    ///     security is disabled entirely. Pending, flagged, error, and never-scanned revisions
    ///     stay reachable through the REST history API but are excluded from the cloneable history.
    /// </summary>
    internal static bool IsRevisionServable(bool securityEnabled, SecurityStatus? status)
    {
        return !securityEnabled || status == SecurityStatus.Verified;
    }

    /// <summary>
    ///     Marker content for the materialized history: the head revision id plus every retained
    ///     revision id in materialization order, each suffixed with its scan status when security
    ///     is enabled. Any status flip (rescan), history change, or security toggle changes the
    ///     marker, which lazily rebuilds the repository on the next Git request.
    /// </summary>
    internal static string ComputeHistoryMarker(
        PackageDocument doc,
        bool securityEnabled,
        IReadOnlyDictionary<string, SecurityStatus>? statuses)
    {
        var builder = new StringBuilder();
        builder.Append(doc.HeadRevisionId);
        foreach (var revisionId in OrderedRevisions(doc).Select(r => r.RevisionId))
        {
            builder.Append('\n').Append(revisionId);
            if (securityEnabled && statuses is not null && statuses.TryGetValue(revisionId, out var status))
                builder.Append(':').Append(status);
        }

        return builder.ToString();
    }

    private static async Task<string> WriteTreeAsync(
        string repoPath,
        IReadOnlyDictionary<string, PackageFile> files,
        CancellationToken ct)
    {
        using var tempIndex = new TempFile();
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex.Path };

        await GitClient.ExecuteAsync(repoPath, ["read-tree", "--empty"], null, env, ct);

        foreach (var (name, file) in files)
        {
            var blob = (await GitClient.ExecuteAsync(repoPath, ["hash-object", "--stdin", "-w"], file.Content, env, ct)).Trim();

            var mode = IsExecutable(name, file.Content) ? "100755" : "100644";

            await GitClient.ExecuteAsync(repoPath, ["update-index", "--add", "--cacheinfo", mode, blob, name], null, env, ct);
        }

        return (await GitClient.ExecuteAsync(repoPath, ["write-tree"], null, env, ct)).Trim();
    }

    private static bool IsExecutable(string name, string? content)
    {
        if (name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrEmpty(content) && content.StartsWith("#!", StringComparison.Ordinal);
    }

    private static async Task<string> WriteCommitAsync(
        string repoPath,
        string treeSha,
        string parent,
        PackageRevisionDocument revision,
        CancellationToken ct)
    {
        using var messageFile = new TempFile();
        await File.WriteAllTextAsync(messageFile.Path, revision.Message, ct);

        string[] args = string.IsNullOrEmpty(parent)
            ? ["commit-tree", treeSha, "-F", messageFile.Path]
            : ["commit-tree", treeSha, "-p", parent, "-F", messageFile.Path];

        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = SanitizeIdent(revision.Author),
            ["GIT_AUTHOR_EMAIL"] = $"{SanitizeIdent(revision.Author)}@atoll.local",
            ["GIT_COMMITTER_NAME"] = "atoll",
            ["GIT_COMMITTER_EMAIL"] = "atoll@local"
        };

        if (revision.CreatedAt != default)
        {
            var unix = revision.CreatedAt.ToUnixTimeSeconds().ToString();
            env["GIT_AUTHOR_DATE"] = unix;
            env["GIT_COMMITTER_DATE"] = unix;
        }

        return (await GitClient.ExecuteAsync(repoPath, args, null, env, ct)).Trim();
    }

    private static string SanitizeIdent(string value)
    {
        var sanitized = value.Trim()
            .Where(c => c is not ('<' or '>' or '\n' or '\r'))
            .ToString();

        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}