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