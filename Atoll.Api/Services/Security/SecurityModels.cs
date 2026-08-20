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