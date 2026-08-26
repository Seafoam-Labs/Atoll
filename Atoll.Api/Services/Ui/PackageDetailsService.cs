using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Services.Ui;

public sealed record PackageDetails(
    AurPackageMetadata Metadata,
    PackageDocument? Head,
    SecurityAccessResult Access,
    PackageSecurityScanDocument? HeadScan,
    IReadOnlyList<PackageSecurityScanDocument> Scans,
    string SelectedRevisionId = "",
    bool SelectedIsHead = true,
    bool RevisionFellBack = false)
{
    public bool IsSeeded => Head is not null;

    /// <summary>Scan of the revision the page is pinned to - head unless ?rev= names another revision.</summary>
    public PackageSecurityScanDocument? SelectedScan => Head is null
        ? null
        : SelectedRevisionId == Head.HeadRevisionId
            ? HeadScan
            : Scans.FirstOrDefault(scan => scan.RevisionId == SelectedRevisionId);
}

public sealed record RevisionRow(
    string Sha,
    DateTimeOffset Date,
    string Author,
    string Message,
    SecurityStatus? Status,
    bool IsHead);

public sealed record RevisionListResult(
    IReadOnlyList<RevisionRow> Rows,
    int TotalRevisions,
    bool IsTruncated,
    string HeadRevisionId);

public sealed record PackageFileEntry(string Path, long Size);

/// <summary>
///     Everything the Files tab needs for one request. The whole revision (tree + file bodies) is a single
///     repository document, so one read serves both the picker and the viewer - there is no cheap way to
///     fetch the tree without the content, hence the combined view instead of separate tree/content calls.
/// </summary>
public sealed record PackageFilesView(
    string PackageName,
    string RevisionId,
    bool IsHead,
    bool RevisionFellBack,
    SecurityAccessResult Access,
    IReadOnlyList<PackageFileEntry> Entries,
    bool EntriesTruncated,
    int TotalEntries,
    string? SelectedPath,
    string? Content,
    long ContentBytes,
    bool IsBinary,
    bool IsTruncated,
    bool FileNotFound);

public sealed class PackageDetailsService(
    PackageIndexStore indexStore,
    IPackageRepository packageRepository,
    IPackageSecurityRepository securityRepository,
    IPackageSecurityAccess securityAccess)
{
    public const int RevisionRenderCap = 100;
    public const int TreeRenderCap = 500;
    public const int ContentRenderChars = 256 * 1024;

    private const int BinaryProbeChars = 8192;

    /// <param name="requestedRevisionId">
    ///     Optional <c>?rev=</c> pin. Unknown or malformed ids fall back to head with
    ///     <see cref="PackageDetails.RevisionFellBack" /> set rather than failing the page.
    /// </param>
    public async Task<PackageDetails?> GetAsync(
        string name,
        string? requestedRevisionId = null,
        CancellationToken ct = default)
    {
        if (!indexStore.Current.ByNames.TryGetValue(name, out var metadata))
            return null;

        var head = await packageRepository.GetHeadAsync(name, ct);
        if (head is null)
            return new PackageDetails(metadata, null, SecurityAccessResult.Allow(), null, []);

        var headScan = await securityRepository.GetHeadAsync(name, ct);
        var scans = (await securityRepository.ListForPackageAsync(name, ct))
            .OrderByDescending(scan => scan.IsHead)
            .ThenByDescending(scan => scan.ScannedAt)
            .ToList();
        var access = await securityAccess.CheckAsync(name, null, ct);

        var (selected, fellBack) = ResolveRevision(head, requestedRevisionId);

        return new PackageDetails(
            metadata,
            head,
            access,
            headScan,
            scans,
            selected,
            string.Equals(selected, head.HeadRevisionId, StringComparison.Ordinal),
            fellBack);
    }

    public async Task<RevisionListResult?> GetRevisionsAsync(string name, CancellationToken ct = default)
    {
        if (!indexStore.Current.ByNames.TryGetValue(name, out _))
            return null;

        var head = await packageRepository.GetHeadAsync(name, ct);
        if (head is null)
            return new RevisionListResult([], 0, false, string.Empty);

        var history = await packageRepository.GetHistoryAsync(name, ct);

        var statuses = new Dictionary<string, SecurityStatus?>(StringComparer.Ordinal);
        foreach (var scan in await securityRepository.ListForPackageAsync(name, ct))
            statuses[scan.RevisionId] = scan.Status;

        var ordered = history
            .OrderByDescending(version => version.Date)
            .ToList();
        var truncated = ordered.Count > RevisionRenderCap;
        var rendered = truncated ? [.. ordered.Take(RevisionRenderCap)] : ordered;

        var rows = rendered.Select(version => new RevisionRow(
            version.Sha,
            version.Date,
            version.Author,
            version.Message,
            statuses.GetValueOrDefault(version.Sha),
            string.Equals(version.Sha, head.HeadRevisionId, StringComparison.Ordinal))).ToList();

        return new RevisionListResult(rows, ordered.Count, truncated, head.HeadRevisionId);
    }

    public async Task<PackageFilesView?> GetFilesAsync(
        string name,
        string? requestedRevisionId,
        string? path,
        CancellationToken ct = default)
    {
        if (!indexStore.Current.ByNames.TryGetValue(name, out _))
            return null;

        var head = await packageRepository.GetHeadAsync(name, ct);
        if (head is null)
            return new PackageFilesView(
                name, string.Empty, false, false, SecurityAccessResult.Allow(),
                [], false, 0, null, null, 0, false, false, false);

        var (revisionId, fellBack) = ResolveRevision(head, requestedRevisionId);
        var isHead = string.Equals(revisionId, head.HeadRevisionId, StringComparison.Ordinal);

        var access = await securityAccess.CheckAsync(name, revisionId, ct);
        // UI file browsing stays available for flagged revisions so users can inspect the offending
        // content; only the REST content endpoints and Git serving are gated by PackageSecurityFilter.

        var revision = await packageRepository.GetRevisionAsync(name, revisionId, ct);
        if (revision is null || revision.Files.Count == 0)
            return new PackageFilesView(
                name, revisionId, isHead, fellBack, access,
                [], false, 0, null, null, 0, false, false, false);

        var entries = revision.Files
            .Select(file => new PackageFileEntry(file.Key, file.Value.Size))
            .OrderBy(entry => entry.Path, FileTreePathComparer.Instance)
            .ToList();
        var entriesTruncated = entries.Count > TreeRenderCap;
        if (entriesTruncated)
            entries = [.. entries.Take(TreeRenderCap)];

        string? content = null;
        var contentBytes = 0L;
        var isBinary = false;
        var isTruncated = false;
        var fileNotFound = false;

        if (!string.IsNullOrEmpty(path))
        {
            if (revision.Files.TryGetValue(path, out var file))
            {
                contentBytes = file.Size;
                if (LooksBinary(file.Content))
                    isBinary = true;
                else if (file.Content.Length > ContentRenderChars)
                {
                    content = file.Content[..ContentRenderChars];
                    isTruncated = true;
                }
                else
                {
                    content = file.Content;
                }
            }
            else
            {
                fileNotFound = true;
            }
        }

        return new PackageFilesView(
            name, revisionId, isHead, fellBack, access,
            entries, entriesTruncated, revision.Files.Count,
            path, content, contentBytes, isBinary, isTruncated, fileNotFound);
    }

    private static (string RevisionId, bool FellBack) ResolveRevision(PackageDocument head, string? requested)
    {
        if (string.IsNullOrEmpty(requested) || requested == head.HeadRevisionId)
            return (head.HeadRevisionId, false);

        // Membership in the retained revision list matches the /security/rescan validation.
        return head.Revisions.Any(revision => revision.RevisionId == requested)
            ? (requested, false)
            : (head.HeadRevisionId, true);
    }

    /// <summary>
    ///     Binary heuristic: stored content is UTF-8–decoded, and NUL bytes survive that decoding while
    ///     invalid sequences collapse to U+FFFD - so NUL in the leading probe means binary payload.
    /// </summary>
    private static bool LooksBinary(string content)
    {
        var probe = content.AsSpan(0, Math.Min(content.Length, BinaryProbeChars));
        return probe.Contains('\0');
    }

    /// <summary>
    ///     Orders paths for tree rendering: at every level directories sort before files, then ordinal.
    ///     E.g. "sub/a", "sub/deep/b", "sub/b.txt", "top.txt".
    /// </summary>
    private sealed class FileTreePathComparer : IComparer<string>
    {
        public static readonly FileTreePathComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var xs = x!.Split('/');
            var ys = y!.Split('/');
            var shared = Math.Min(xs.Length, ys.Length);
            for (var i = 0; i < shared; i++)
            {
                var xIsDir = i < xs.Length - 1;
                var yIsDir = i < ys.Length - 1;
                if (xIsDir != yIsDir)
                    return xIsDir ? -1 : 1;

                var result = string.CompareOrdinal(xs[i], ys[i]);
                if (result != 0)
                    return result;
            }

            return xs.Length.CompareTo(ys.Length);
        }
    }
}
