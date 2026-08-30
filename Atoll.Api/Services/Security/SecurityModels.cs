using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

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

public sealed record HeadScanStatus(string PackageName, SecurityStatus Status);

public sealed record RevisionScanStatus(string RevisionId, SecurityStatus Status);

/// <summary>Current head-revision scan status counts, queried live from storage (not a cumulative counter).</summary>
public sealed record HeadScanStatusCounts(long Verified, long Flagged, long Pending, long Error)
{
    public long Total => Verified + Flagged + Pending + Error;
}

public sealed record SecurityFinding(
    string RuleId,
    [property: BsonRepresentation(BsonType.String)]
    FindingSeverity Severity,
    string Message,
    string Snippet,
    string File);

public sealed record PackageSecurityHistoryResponse(
    string PackageName,
    string HeadRevisionId,
    IReadOnlyList<PackageSecurityRevisionItem> Revisions);

public sealed record PackageSecurityRevisionItem(
    string RevisionId,
    string Status,
    bool IsHead,
    DateTimeOffset? ScannedAt,
    int FindingCount);

public sealed record PackageSecurityRevisionResponse(
    string PackageName,
    string RevisionId,
    string Status,
    bool IsHead,
    DateTimeOffset? ScannedAt,
    int FindingCount);