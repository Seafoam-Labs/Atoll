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
        if (scan is null && revisionId != package.HeadRevisionId && package.Revisions.All(r => r.RevisionId != revisionId))
            return null;

        return new PackageSecurityRevisionResponse(
            packageName,
            revisionId,
            (scan?.Status ?? SecurityStatus.Pending).ToString(),
            revisionId == package.HeadRevisionId,
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
        if (package.Revisions.All(r => r.RevisionId != revision))
            return null;

        await security.MarkPendingAsync(packageName, revision, revision == package.HeadRevisionId,
            scanner.PolicyVersion, ct);

        return revision;
    }
}
