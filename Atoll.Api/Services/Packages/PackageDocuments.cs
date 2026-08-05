using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Atoll.Api.Services.Packages;

public static class PackageSchema
{
    public const int CurrentVersion = 2;

    public static string RevisionDocumentId(string packageName, string revisionId)
    {
        return $"{packageName}:{revisionId}";
    }
}

public sealed class PackageDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; init; } = string.Empty;

    [BsonElement("packageName")] public string PackageName { get; init; } = string.Empty;

    [BsonElement("createdAt")] public DateTimeOffset CreatedAt { get; init; }

    [BsonElement("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }

    [BsonElement("headRevisionId")] public string HeadRevisionId { get; init; } = string.Empty;

    [BsonElement("schemaVersion")] public int SchemaVersion { get; init; } = PackageSchema.CurrentVersion;

    [BsonElement("revisions")] public List<PackageRevisionDocument> Revisions { get; init; } = [];

    [BsonElement("upstreamPackageBase")]
    [BsonIgnoreIfNull]
    public string? UpstreamPackageBase { get; init; }

    [BsonElement("lastSyncedUpstreamHead")]
    [BsonIgnoreIfNull]
    public string? LastSyncedUpstreamHead { get; init; }

    [BsonElement("lastSyncAttemptAt")]
    [BsonIgnoreIfNull]
    public DateTimeOffset? LastSyncAttemptAt { get; init; }

    [BsonElement("lastSyncSucceededAt")]
    [BsonIgnoreIfNull]
    public DateTimeOffset? LastSyncSucceededAt { get; init; }

    [BsonElement("lastSyncError")]
    [BsonIgnoreIfNull]
    public string? LastSyncError { get; init; }
}

public sealed class PackageFile
{
    [BsonElement("content")] public string Content { get; init; } = string.Empty;

    [BsonElement("size")] public long Size { get; init; }

    [BsonElement("hash")] public string Hash { get; init; } = string.Empty;
}

public sealed class PackageRevisionDocument
{
    [BsonElement("revisionId")] public string RevisionId { get; init; } = string.Empty;

    [BsonElement("createdAt")] public DateTimeOffset CreatedAt { get; init; }

    [BsonElement("author")] public string Author { get; init; } = string.Empty;

    [BsonElement("message")] public string Message { get; init; } = string.Empty;
}

public sealed class PackageRevisionContentDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; init; } = string.Empty;

    [BsonElement("packageName")] public string PackageName { get; init; } = string.Empty;

    [BsonElement("revisionId")] public string RevisionId { get; init; } = string.Empty;

    [BsonElement("createdAt")] public DateTimeOffset CreatedAt { get; init; }

    [BsonElement("author")] public string Author { get; init; } = string.Empty;

    [BsonElement("message")] public string Message { get; init; } = string.Empty;

    [BsonElement("schemaVersion")] public int SchemaVersion { get; init; } = PackageSchema.CurrentVersion;

    [BsonElement("files")] public Dictionary<string, PackageFile> Files { get; init; } = new();
}

public sealed class PackageSyncState
{
    [BsonElement("packageName")] public string PackageName { get; init; } = string.Empty;

    [BsonElement("upstreamPackageBase")]
    [BsonIgnoreIfNull]
    public string? UpstreamPackageBase { get; init; }

    [BsonElement("lastSyncedUpstreamHead")]
    [BsonIgnoreIfNull]
    public string? LastSyncedUpstreamHead { get; init; }

    [BsonElement("lastSyncSucceededAt")]
    [BsonIgnoreIfNull]
    public DateTimeOffset? LastSyncSucceededAt { get; init; }
}