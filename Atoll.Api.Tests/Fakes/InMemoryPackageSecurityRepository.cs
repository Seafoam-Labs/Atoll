using Atoll.Api.Services.Security;

namespace Atoll.Api.Tests.Fakes;

internal sealed class InMemoryPackageSecurityRepository : IPackageSecurityRepository
{
    private readonly Dictionary<string, PackageSecurityScanDocument> _scans = new(StringComparer.Ordinal);

    public Task<PackageSecurityScanDocument?> GetAsync(string packageName, CancellationToken ct = default)
    {
        return Task.FromResult(_scans.GetValueOrDefault(packageName));
    }

    public Task<IReadOnlyCollection<string>> ListPackageNamesAsync(CancellationToken ct = default)
    {
        IReadOnlyCollection<string> result = _scans.Keys.ToList();
        return Task.FromResult(result);
    }

    public Task MarkPendingAsync(string packageName, string revisionId, CancellationToken ct = default)
    {
        _scans[packageName] = new PackageSecurityScanDocument
        {
            Id = packageName,
            RevisionId = revisionId,
            Status = SecurityStatus.Pending
        };
        return Task.CompletedTask;
    }

    public Task EnsurePendingAsync(string packageName, string revisionId, CancellationToken ct = default)
    {
        if (!_scans.ContainsKey(packageName))
            return MarkPendingAsync(packageName, revisionId, ct);

        return Task.CompletedTask;
    }

    public Task<PackageSecurityScanDocument?> TryClaimPendingScanAsync(
        string owner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var pending = _scans.Values.FirstOrDefault(scan =>
            scan.Status == SecurityStatus.Pending &&
            (scan.LeaseUntil is null || scan.LeaseUntil < DateTimeOffset.UtcNow));
        if (pending is null)
            return Task.FromResult<PackageSecurityScanDocument?>(null);

        var claim = pending with { LeaseOwner = owner, LeaseUntil = DateTimeOffset.UtcNow.Add(leaseDuration) };
        _scans[claim.Id] = claim;
        return Task.FromResult<PackageSecurityScanDocument?>(claim);
    }

    public Task CompleteScanAsync(
        string packageName,
        string revisionId,
        string owner,
        ScanResult result,
        CancellationToken ct = default)
    {
        if (_scans.TryGetValue(packageName, out var scan) &&
            scan.RevisionId == revisionId && scan.LeaseOwner == owner)
            _scans[packageName] = scan with
            {
                Status = result.Status,
                Findings = result.Findings.ToList(),
                ScannedAt = DateTimeOffset.UtcNow,
                LeaseUntil = null,
                LeaseOwner = null
            };

        return Task.CompletedTask;
    }

    public Task MarkScanErrorAsync(
        string packageName,
        string revisionId,
        string owner,
        CancellationToken ct = default)
    {
        if (_scans.TryGetValue(packageName, out var scan) &&
            scan.RevisionId == revisionId && scan.LeaseOwner == owner)
            _scans[packageName] = scan with
            {
                Status = SecurityStatus.Error,
                Findings = [],
                ScannedAt = DateTimeOffset.UtcNow,
                LeaseUntil = null,
                LeaseOwner = null
            };

        return Task.CompletedTask;
    }

    public Task ReleaseScanClaimAsync(string packageName, string owner, CancellationToken ct = default)
    {
        if (_scans.TryGetValue(packageName, out var scan) && scan.LeaseOwner == owner)
            _scans[packageName] = scan with { LeaseUntil = null, LeaseOwner = null };

        return Task.CompletedTask;
    }
}