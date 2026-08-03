using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Atoll.Api.Services.Security;

public sealed record PackageSecurityScanDocument
{
    /// <summary>
    ///     Composite key "{packageName}:{revisionId}". Scan state is kept per revision so a
    ///     flagged revision never taints the package's other revisions.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; init; } = string.Empty;

    [BsonElement("packageName")] public string PackageName { get; init; } = string.Empty;

    [BsonElement("revisionId")] public string RevisionId { get; init; } = string.Empty;

    /// <summary>Denormalized so the gate can find the head scan without a second read.</summary>
    [BsonElement("isHead")]
    public bool IsHead { get; init; }

    [BsonElement("status")]
    [BsonRepresentation(BsonType.String)]
    public SecurityStatus Status { get; init; } = SecurityStatus.Pending;

    [BsonElement("findings")] public List<SecurityFinding> Findings { get; init; } = [];

    [BsonElement("scannedAt")] public DateTimeOffset? ScannedAt { get; init; }

    [BsonElement("leaseUntil")] public DateTimeOffset? LeaseUntil { get; init; }

    [BsonElement("leaseOwner")] public string? LeaseOwner { get; init; }

    public static string ComposeId(string packageName, string revisionId)
    {
        return packageName + ":" + revisionId;
    }
}

public sealed record SecurityFinding(
    string RuleId,
    [property: BsonRepresentation(BsonType.String)]
    FindingSeverity Severity,
    string Message,
    string Snippet,
    string File);