namespace Atoll.Api.Services.Security;

public enum SecurityStatus
{
    Pending,
    Verified,
    Flagged,
    Error
}

public enum FindingSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

public sealed record ScanResult(SecurityStatus Status, IReadOnlyList<SecurityFinding> Findings);

public sealed record HeadScanStatus(
    string PackageName,
    SecurityStatus Status,
    int FindingCount,
    DateTimeOffset? ScannedAt);

/// <summary>Current head-revision scan status counts, queried live from storage (not a cumulative counter).</summary>
public sealed record HeadScanStatusCounts(long Verified, long Flagged, long Pending, long Error)
{
    public long Total => Verified + Flagged + Pending + Error;
}