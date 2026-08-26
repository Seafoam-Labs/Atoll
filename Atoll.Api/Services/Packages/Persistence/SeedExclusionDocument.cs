using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Atoll.Api.Services.Packages.Persistence;

public sealed class SeedExclusionDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; init; } = string.Empty;

    [BsonElement("packageBase")] public string PackageBase { get; init; } = string.Empty;

    [BsonElement("packageNames")] public List<string> PackageNames { get; init; } = [];

    [BsonElement("reason")] public string Reason { get; init; } = string.Empty;

    [BsonElement("serializedSizeBytes")] public long SerializedSizeBytes { get; init; }

    [BsonElement("firstSeenUtc")] public DateTimeOffset FirstSeenUtc { get; init; }

    [BsonElement("lastSeenUtc")] public DateTimeOffset LastSeenUtc { get; init; }
}