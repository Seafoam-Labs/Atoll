using Atoll.Api.Services.Security;

namespace Atoll.Api.Tests.Fakes;

internal sealed class InMemoryPackageSecurityRepository : IPackageSecurityRepository
{
    // Bulk seeding and refresh mark scans pending concurrently, so every access is guarded.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PackageSecurityScanDocument> _scans = new(StringComparer.Ordinal);

    public Task<PackageSecurityScanDocument?> GetAsync(
        string packageName,
        string revisionId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_scans.GetValueOrDefault(PackageSecurityScanDocument.ComposeId(packageName, revisionId)));
        }
    }

    public Task<PackageSecurityScanDocument?> GetHeadAsync(string packageName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_scans.Values.FirstOrDefault(s => s.PackageName == packageName && s.IsHead));
        }
    }

    public Task<IReadOnlyCollection<PackageSecurityScanDocument>> ListForPackageAsync(
        string packageName,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyCollection<PackageSecurityScanDocument> result =
                [.. _scans.Values.Where(s => s.PackageName == packageName)];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyCollection<string>> ListPackageNamesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyCollection<string> result = [.. _scans.Values.Select(s => s.PackageName).Distinct()];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<HeadScanStatus>> ListHeadStatusesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<HeadScanStatus> result =
            [
                .. _scans.Values
                    .Where(s => s.IsHead)
                    .Select(s => new HeadScanStatus(s.PackageName, s.Status, s.Findings.Count, s.ScannedAt))
            ];
            return Task.FromResult(result);
        }
    }

    public Task<HeadScanStatusCounts> CountHeadStatusesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var heads = _scans.Values.Where(s => s.IsHead).ToList();
            return Task.FromResult(new HeadScanStatusCounts(
                heads.Count(s => s.Status == SecurityStatus.Verified),
                heads.Count(s => s.Status == SecurityStatus.Flagged),
                heads.Count(s => s.Status == SecurityStatus.Pending),
                heads.Count(s => s.Status == SecurityStatus.Error)));
        }
    }

    public Task<long> CountPendingAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult((long)_scans.Values.Count(s => s.Status == SecurityStatus.Pending));
        }
    }

    public Task MarkPendingAsync(
        string packageName,
        string revisionId,
        bool isHead,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            _scans[PackageSecurityScanDocument.ComposeId(packageName, revisionId)] = new PackageSecurityScanDocument
            {
                Id = PackageSecurityScanDocument.ComposeId(packageName, revisionId),
                PackageName = packageName,
                RevisionId = revisionId,
                IsHead = isHead,
                Status = SecurityStatus.Pending
            };
            return Task.CompletedTask;
        }
    }

    public Task EnsurePendingAsync(
        string packageName,
        string revisionId,
        bool isHead,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_scans.ContainsKey(PackageSecurityScanDocument.ComposeId(packageName, revisionId)))
                _scans[PackageSecurityScanDocument.ComposeId(packageName, revisionId)] = new PackageSecurityScanDocument
                {
                    Id = PackageSecurityScanDocument.ComposeId(packageName, revisionId),
                    PackageName = packageName,
                    RevisionId = revisionId,
                    IsHead = isHead,
                    Status = SecurityStatus.Pending
                };

            return Task.CompletedTask;
        }
    }

    public Task<PackageSecurityScanDocument?> TryClaimPendingScanAsync(
        string owner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        lock (_gate)
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
    }

    public Task CompleteScanAsync(
        string packageName,
        string revisionId,
        string owner,
        ScanResult result,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var id = PackageSecurityScanDocument.ComposeId(packageName, revisionId);
            if (_scans.TryGetValue(id, out var scan) && scan.LeaseOwner == owner)
                _scans[id] = scan with
                {
                    Status = result.Status,
                    Findings = [.. result.Findings],
                    ScannedAt = DateTimeOffset.UtcNow,
                    LeaseUntil = null,
                    LeaseOwner = null
                };

            return Task.CompletedTask;
        }
    }

    public Task MarkScanErrorAsync(
        string packageName,
        string revisionId,
        string owner,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var id = PackageSecurityScanDocument.ComposeId(packageName, revisionId);
            if (_scans.TryGetValue(id, out var scan) && scan.LeaseOwner == owner)
                _scans[id] = scan with
                {
                    Status = SecurityStatus.Error,
                    Findings = [],
                    ScannedAt = DateTimeOffset.UtcNow,
                    LeaseUntil = null,
                    LeaseOwner = null
                };

            return Task.CompletedTask;
        }
    }

    public Task ReleaseScanClaimAsync(
        string packageName,
        string revisionId,
        string owner,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var id = PackageSecurityScanDocument.ComposeId(packageName, revisionId);
            if (_scans.TryGetValue(id, out var scan) && scan.LeaseOwner == owner)
                _scans[id] = scan with { LeaseUntil = null, LeaseOwner = null };

            return Task.CompletedTask;
        }
    }

    public Task PromoteHeadAsync(string packageName, string newHeadRevisionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var (id, scan) in _scans.ToList())
            {
                if (scan.PackageName != packageName)
                    continue;

                var isHead = scan.RevisionId == newHeadRevisionId;
                if (scan.IsHead != isHead)
                    _scans[id] = scan with { IsHead = isHead };
            }

            return Task.CompletedTask;
        }
    }

    public Task DeleteAsync(string packageName, string revisionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _scans.Remove(PackageSecurityScanDocument.ComposeId(packageName, revisionId));
            return Task.CompletedTask;
        }
    }
}