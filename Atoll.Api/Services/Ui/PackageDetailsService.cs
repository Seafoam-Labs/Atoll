using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
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
        : string.Equals(SelectedRevisionId, Head.HeadRevisionId, StringComparison.Ordinal)
            ? HeadScan
            : Scans.FirstOrDefault(scan => string.Equals(scan.RevisionId, SelectedRevisionId, StringComparison.Ordinal));
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

public enum DiffChangeKind
{
    Added,
    Modified,
    Removed,
    Binary,
    TooLarge
}

public sealed record DiffFileView(
    string Path,
    DiffChangeKind Kind,
    string? UnifiedDiff,
    long OldSize,
    long NewSize);

/// <summary>
///     Everything the diff tab needs for one request. Content comes from Mongo - the same stored revision
///     documents the Files tab reads - so a diff stays open for flagged revisions and never depends on the
///     rebuildable bare-repo cache or its synthesized commit SHAs.
/// </summary>
public sealed record PackageDiffView(
    string PackageName,
    string FromRevisionId,
    string ToRevisionId,
    bool FromFellBack,
    bool ToFellBack,
    SecurityAccessResult Access,
    IReadOnlyList<DiffFileView> Files,
    int ChangedFileCount,
    bool Truncated,
    bool Identical,
    bool DiffUnavailable);

public sealed class PackageDetailsService(
    PackageIndexStore indexStore,
    IPackageRepository packageRepository,
    IPackageSecurityRepository securityRepository,
    IPackageSecurityAccess securityAccess,
    ITextDiffer differ)
{
    public const int RevisionRenderCap = 100;
    public const int TreeRenderCap = 500;
    public const int ContentRenderChars = 256 * 1024;

    /// <summary>
    ///     Per-side cap for a file handed to the differ, bounding the temp write and the git work one
    ///     pathological file can cause. Same number as wwwroot/js/file-highlighting.js's <c>charCap</c>, which
    ///     applies to the rendered diff - a modified file near this cap renders uncolored.
    /// </summary>
    public const int DiffFileRenderChars = 128 * 1024;

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
        if (!indexStore.Current.ByNames.ContainsKey(name))
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
        if (!indexStore.Current.ByNames.ContainsKey(name))
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

    /// <summary>
    ///     Classifies what changed between two stored revisions and renders a unified diff per changed file.
    /// </summary>
    /// <param name="from">
    ///     Optional <c>?from=</c> base. Empty compares against the revision immediately older than
    ///     <paramref name="to" />; unknown ids fall back to head with
    ///     <see cref="PackageDiffView.FromFellBack" /> set rather than failing the page.
    /// </param>
    public async Task<PackageDiffView?> GetDiffAsync(
        string name,
        string? from,
        string? to,
        CancellationToken ct = default)
    {
        if (!indexStore.Current.ByNames.ContainsKey(name))
            return null;

        var head = await packageRepository.GetHeadAsync(name, ct);
        if (head is null)
            return EmptyDiff(name);

        var (toId, toFellBack) = ResolveRevision(head, to);
        var (fromId, fromFellBack) = ResolveBase(head, from, toId);
        var access = await securityAccess.CheckAsync(name, toId, ct);
        // Like the Files tab, browsing stays open for flagged revisions; only REST content endpoints and Git
        // serving are gated by PackageSecurityFilter.

        // Strictly the same resolved revision: no content to compare, so the differ is never reached.
        if (fromId.Length != 0 && string.Equals(fromId, toId, StringComparison.Ordinal))
            return new PackageDiffView(
                name, fromId, toId, fromFellBack, toFellBack, access, [], 0, false, true, false);

        // A missing content document (pruned, or never stored) means that side contributes no files.
        var oldFiles = fromId.Length == 0
            ? new Dictionary<string, PackageFile>(StringComparer.Ordinal)
            : (await packageRepository.GetRevisionAsync(name, fromId, ct))?.Files
              ?? new Dictionary<string, PackageFile>(StringComparer.Ordinal);
        var newFiles = (await packageRepository.GetRevisionAsync(name, toId, ct))?.Files
            ?? new Dictionary<string, PackageFile>(StringComparer.Ordinal);

        var changedFileCount = 0;
        var changedTruncated = false;
        var totalChars = 0;
        var totalTruncated = false;
        var files = new List<DiffFileView>();
        var pending = new List<TextDiffEntry>();

        foreach (var path in oldFiles.Keys.Union(newFiles.Keys, StringComparer.Ordinal))
        {
            oldFiles.TryGetValue(path, out var oldFile);
            newFiles.TryGetValue(path, out var newFile);

            if (oldFile is not null && newFile is not null
                && string.Equals(oldFile.Content, newFile.Content, StringComparison.Ordinal))
                continue;

            changedFileCount++;
            if (files.Count >= TreeRenderCap)
            {
                changedTruncated = true;
                continue;
            }

            var kind = Classify(oldFile, newFile, out var tooLarge);
            var view = new DiffFileView(path, kind, null, oldFile?.Size ?? 0, newFile?.Size ?? 0);
            files.Add(view);

            if (tooLarge || kind is DiffChangeKind.Binary)
                continue;

            totalChars += (oldFile?.Content.Length ?? 0) + (newFile?.Content.Length ?? 0);
            if (totalChars > ContentRenderChars)
            {
                totalTruncated = true;
                continue;
            }

            pending.Add(new TextDiffEntry(path, oldFile?.Content, newFile?.Content));
        }

        IReadOnlyDictionary<string, string> chunks = new Dictionary<string, string>(StringComparer.Ordinal);
        var unavailable = false;
        if (pending.Count > 0)
        {
            try
            {
                chunks = await differ.DiffAsync(pending, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missing or failing git degrades the page: the classified file list still renders.
                unavailable = true;
            }
        }

        // Every view was built with a null diff, so one pass fills them in. A path the differ had no chunk for
        // (git found nothing to say) simply stays null and the page renders a summary line instead.
        files = [.. files.Select(file => file with { UnifiedDiff = chunks.GetValueOrDefault(file.Path) })];

        return new PackageDiffView(
            name, fromId, toId, fromFellBack, toFellBack, access, files,
            changedFileCount, changedTruncated || totalTruncated, false, unavailable);
    }

    private static PackageDiffView EmptyDiff(string name) =>
        new(name, string.Empty, string.Empty, false, false, SecurityAccessResult.Allow(),
            [], 0, false, false, false);

    private static (string RevisionId, bool FellBack) ResolveRevision(PackageDocument head, string? requested)
    {
        if (string.IsNullOrEmpty(requested) || string.Equals(requested, head.HeadRevisionId, StringComparison.Ordinal))
            return (head.HeadRevisionId, false);

        // Membership in the retained revision list matches the /security/rescan validation.
        return head.Revisions.Exists(revision => string.Equals(revision.RevisionId, requested, StringComparison.Ordinal))
            ? (requested, false)
            : (head.HeadRevisionId, true);
    }

    /// <summary>
    ///     Resolves the base side. An explicit <paramref name="requested" /> wins, else the revision immediately
    ///     older than the target. The oldest revision has no parent, so it resolves to an empty base - every file
    ///     then reads as added, which is the honest answer rather than a bogus diff against head.
    /// </summary>
    private static (string RevisionId, bool FellBack) ResolveBase(
        PackageDocument head,
        string? requested,
        string toId)
    {
        if (!string.IsNullOrEmpty(requested))
            return ResolveRevision(head, requested);

        var parentId = ParentRevisionId(head, toId);
        return parentId.Length == 0 ? (string.Empty, false) : (parentId, false);
    }

    /// <summary>
    ///     The revision immediately older than <paramref name="revisionId" />, in the same newest-first ordering
    ///     <see cref="GetRevisionsAsync" /> applies to history. Empty when the target is the oldest revision.
    /// </summary>
    private static string ParentRevisionId(PackageDocument head, string revisionId)
    {
        var ordered = head.Revisions.OrderByDescending(revision => revision.CreatedAt).ToList();
        var index = ordered.FindIndex(revision =>
            string.Equals(revision.RevisionId, revisionId, StringComparison.Ordinal));
        return index >= 0 && index + 1 < ordered.Count ? ordered[index + 1].RevisionId : string.Empty;
    }

    /// <summary>
    ///     Decides what the page shows for one changed file. Binary and oversized win over added/removed because
    ///     they describe whether a diff can be rendered at all; the page still shows the size delta, so the
    ///     direction of the change is not lost.
    /// </summary>
    private static DiffChangeKind Classify(PackageFile? oldFile, PackageFile? newFile, out bool tooLarge)
    {
        var oldText = oldFile?.Content;
        var newText = newFile?.Content;

        tooLarge = oldText is { Length: > DiffFileRenderChars }
            || newText is { Length: > DiffFileRenderChars };
        if (tooLarge)
            return DiffChangeKind.TooLarge;

        if (LooksBinary(oldText) || LooksBinary(newText))
            return DiffChangeKind.Binary;

        if (oldFile is null)
            return DiffChangeKind.Added;

        return newFile is null ? DiffChangeKind.Removed : DiffChangeKind.Modified;
    }

    /// <summary>
    ///     Binary heuristic: stored content is UTF-8–decoded, and NUL bytes survive that decoding while
    ///     invalid sequences collapse to U+FFFD - so NUL in the leading probe means binary payload.
    /// </summary>
    private static bool LooksBinary(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

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
