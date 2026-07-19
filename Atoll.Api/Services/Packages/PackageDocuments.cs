using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Atoll.Api.Services.Packages;

/// <summary>
///     MongoDB document for a single package. One document per package name.
/// </summary>
/// <remarks>
///     Revisions are embedded and capped (see <see cref="MongoOptions.MaxRevisions" />).
///     Keep <c>MaxFileBytes</c> and <c>MaxRevisions</c> conservative to stay under
///     MongoDB's 16 MB document size limit.
/// </remarks>
public sealed class PackageDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; init; } = string.Empty;

    [BsonElement("packageName")] public string PackageName { get; init; } = string.Empty;

    [BsonElement("createdAt")] public DateTimeOffset CreatedAt { get; init; }

    [BsonElement("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }

    [BsonElement("headRevisionId")] public string HeadRevisionId { get; init; } = string.Empty;

    [BsonElement("files")] public Dictionary<string, PackageFile> Files { get; init; } = new();

    [BsonElement("revisions")] public List<PackageRevisionDocument> Revisions { get; init; } = [];
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

    [BsonElement("files")] public Dictionary<string, PackageFile> Files { get; init; } = new();
}