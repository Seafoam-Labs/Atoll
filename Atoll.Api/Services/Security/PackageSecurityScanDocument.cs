using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Atoll.Api.Services.Security;

public sealed record PackageSecurityScanDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; init; } = string.Empty;

    [BsonElement("revisionId")] public string RevisionId { get; init; } = string.Empty;

    [BsonElement("status")]
    [BsonRepresentation(BsonType.String)]
    public SecurityStatus Status { get; init; } = SecurityStatus.Pending;

    [BsonElement("findings")] public List<SecurityFinding> Findings { get; init; } = [];

    [BsonElement("scannedAt")] public DateTimeOffset? ScannedAt { get; init; }

    [BsonElement("leaseUntil")] public DateTimeOffset? LeaseUntil { get; init; }

    [BsonElement("leaseOwner")] public string? LeaseOwner { get; init; }
}

public sealed record SecurityFinding(
    string RuleId,
    [property: BsonRepresentation(BsonType.String)]
    FindingSeverity Severity,
    string Message,
    string Snippet,
    string File);