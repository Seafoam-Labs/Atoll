using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Atoll.Api.Services.Packages.Persistence;

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

    [BsonElement("files")] public Dictionary<string, PackageFile> Files { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Listing row for the package index endpoint: the mirror-side columns projected in MongoDB plus catalog fields
/// joined from the in-memory index at read time. Never carries the embedded revisions array.
/// </summary>
public sealed record PackageIndexEntry(
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string HeadRevisionId,
    int RevisionCount)
{
    // Catalog presentation fields joined from the in-memory index at read time; null when the package
    // is absent from the current dump (pruned upstream, or the index has not loaded yet). An empty array
    // means the dump carries none for that key, which is distinct from a null array.
    public string? Description { get; init; }
    public string? Version { get; init; }
    public long? NumVotes { get; init; }
    public double? Popularity { get; init; }
    public long? OutOfDate { get; init; }
    public string? Url { get; init; }
    public string? Maintainer { get; init; }
    public string? PackageBase { get; init; }
    public long? FirstSubmitted { get; init; }
    public long? LastModified { get; init; }
    public IReadOnlyList<string>? License { get; init; }
    public IReadOnlyList<string>? Depends { get; init; }
    public IReadOnlyList<string>? MakeDepends { get; init; }
    public IReadOnlyList<string>? OptDepends { get; init; }
    public IReadOnlyList<string>? Provides { get; init; }
}

public sealed class PackageSyncState
{
    [BsonElement("packageName")] public string PackageName { get; init; } = string.Empty;

    [BsonElement("lastSyncedUpstreamHead")]
    [BsonIgnoreIfNull]
    public string? LastSyncedUpstreamHead { get; init; }

    [BsonElement("lastSyncSucceededAt")]
    [BsonIgnoreIfNull]
    public DateTimeOffset? LastSyncSucceededAt { get; init; }
}