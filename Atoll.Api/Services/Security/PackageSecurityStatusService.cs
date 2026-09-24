using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Security.Persistence;

namespace Atoll.Api.Services.Security;

/// <summary>
///     Scan-status read model and rescan queueing for a package's revisions. An unscanned
///     revision reports as <see cref="SecurityStatus.Pending"/> rather than absent.
/// </summary>
public sealed class PackageSecurityStatusService(
    IPackageRepository packages,
    IPackageSecurityRepository security,
    IPackageSecurityScanner scanner)
{
    public async Task<PackageSecurityHistoryResponse?> GetHistoryAsync(
        string packageName,
        CancellationToken ct = default)
    {
        var package = await packages.GetHeadAsync(packageName, ct);
        if (package is null)
            return null;

        var scans = await security.ListForPackageAsync(packageName, ct);

        return new PackageSecurityHistoryResponse(
            packageName,
            package.HeadRevisionId,
            [
                .. scans
                    .OrderByDescending(s => s.IsHead)
                    .ThenByDescending(s => s.ScannedAt)
                    .Select(s => new PackageSecurityRevisionItem(
                        s.RevisionId,
                        s.Status.ToString(),
                        s.IsHead,
                        s.ScannedAt,
                        s.Findings.Count
                    ))
            ]);
    }

    public async Task<PackageSecurityRevisionResponse?> GetRevisionAsync(
        string packageName,
        string revisionId,
        CancellationToken ct = default)
    {
        var package = await packages.GetHeadAsync(packageName, ct);
        if (package is null)
            return null;

        var scan = await security.GetAsync(packageName, revisionId, ct);
        if (scan is null &&
            !string.Equals(revisionId, package.HeadRevisionId, StringComparison.Ordinal) &&
            package.Revisions.TrueForAll(r => !string.Equals(r.RevisionId, revisionId, StringComparison.Ordinal)))
            return null;

        return new PackageSecurityRevisionResponse(
            packageName,
            revisionId,
            (scan?.Status ?? SecurityStatus.Pending).ToString(),
            string.Equals(revisionId, package.HeadRevisionId, StringComparison.Ordinal),
            scan?.ScannedAt,
            scan?.Findings.Count ?? 0);
    }

    /// <summary>
    ///     Requeue a revision for scanning, defaulting to the head when none is given.
    ///     Returns the queued revision id, or <c>null</c> if the package or revision is unknown.
    /// </summary>
    public async Task<string?> QueueRescanAsync(
        string packageName,
        string? revisionId = null,
        CancellationToken ct = default)
    {
        var package = await packages.GetHeadAsync(packageName, ct);
        if (package is null)
            return null;

        var revision = string.IsNullOrEmpty(revisionId) ? package.HeadRevisionId : revisionId;
        if (package.Revisions.TrueForAll(r => !string.Equals(r.RevisionId, revision, StringComparison.Ordinal)))
            return null;

        var isHead = string.Equals(revision, package.HeadRevisionId, StringComparison.Ordinal);
        await security.MarkPendingAsync(packageName, revision, isHead, scanner.PolicyVersion, ct);

        return revision;
    }
}
