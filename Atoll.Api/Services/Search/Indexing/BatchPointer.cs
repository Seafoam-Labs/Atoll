using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Atoll.Api.Services.Search.Indexing;

[BsonIgnoreExtraElements]
public sealed class BatchPointer
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = null!;

    [BsonElement("activeBatchId")] public string ActiveBatchId { get; set; } = null!;
}