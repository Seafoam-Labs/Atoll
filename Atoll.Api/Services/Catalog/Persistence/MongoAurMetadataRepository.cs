using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Atoll.Api.Services.Catalog.Persistence;

public sealed class MongoAurMetadataRepository : IAurMetadataRepository
{
    /// <summary>
    ///     Keeps one bulk command near 1.5 MB, well under the 16 MB message limit and the 100k
    ///     operations MongoDB allows per command.
    /// </summary>
    private const int BulkChunkSize = 1000;

    private const string LegacyBatchIndexName = "batch_1_aur_id_1";

    // A rolling deploy overlaps a draining task that may still write legacy ObjectId-keyed documents
    // into this collection; deserializing one of those into the string _id below throws out of the
    // index worker and stops the host. Once the legacy layout is gone the filter matches everything.
    private static readonly FilterDefinition<AurPackageMetadataDocument> NameKeyed =
        new BsonDocumentFilterDefinition<AurPackageMetadataDocument>(
            new BsonDocument("_id", new BsonDocument("$type", "string")));

    private readonly IMongoDatabase _database;
    private readonly string _collectionName;
    private readonly IMongoCollection<AurPackageMetadataDocument> _packages;

    public MongoAurMetadataRepository(IMongoClient client, IOptions<AtollOptions> options)
    {
        var o = options.Value;
        _database = client.GetDatabase(o.Mongo.Database);
        _collectionName = o.Mongo.Collections.AurMetadata;
        _packages = _database.GetCollection<AurPackageMetadataDocument>(_collectionName);

        DropLegacyBatchLayout();
    }

    public async Task<IReadOnlyList<AurPackageMetadata>> LoadAsync(CancellationToken ct)
    {
        var docs = await _packages
            .Find(NameKeyed)
            .ToListAsync(ct);

        return
        [
            .. docs
                .Select(x => x.ToMetadata())
        ];
    }

    public async Task SyncAsync(AurMetadataDelta delta, CancellationToken ct)
    {
        if (delta.IsEmpty) return;

        var bulkOptions = new BulkWriteOptions { IsOrdered = false };

        foreach (var chunk in delta.Upserts.Chunk(BulkChunkSize))
        {
            // The replacement carries _id explicitly: an upsert whose filter names _id but whose
            // replacement document does not has historically inserted a null one.
            var models = chunk
                .Select(p =>
                {
                    var document = p.ToDocument();
                    return (WriteModel<AurPackageMetadataDocument>)new ReplaceOneModel<AurPackageMetadataDocument>(
                        Builders<AurPackageMetadataDocument>.Filter.Eq(x => x.Id, document.Id),
                        document)
                    {
                        IsUpsert = true
                    };
                })
                .ToList();

            await _packages.BulkWriteAsync(models, bulkOptions, ct);
        }

        foreach (var chunk in delta.Removals.Chunk(BulkChunkSize))
        {
            await _packages.DeleteManyAsync(
                Builders<AurPackageMetadataDocument>.Filter.In(x => x.Id, chunk), ct);
        }
    }

    public Task<long> CountAsync(CancellationToken ct)
    {
        return _packages.CountDocumentsAsync(
            Builders<AurPackageMetadataDocument>.Filter.Empty,
            cancellationToken: ct);
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        return _packages.DeleteManyAsync(
            Builders<AurPackageMetadataDocument>.Filter.Empty, ct);
    }

    /// <summary>
    ///     Discards the superseded batch-rotation layout, which cannot be converted in place because
    ///     _id is immutable. The first refresh cycle re-syncs the whole dump into the name-keyed one.
    /// </summary>
    private void DropLegacyBatchLayout()
    {
        // Gated on the live index list rather than suppressing IndexNotFound because DocumentDB
        // reports a missing index as a generic command error without MongoDB's codeName, which would
        // crash startup on a fresh cluster. Listing still surfaces authorization failures.
        using var indexCursor = _packages.Indexes.List();
        var indexNames = indexCursor
            .ToList()
            .Select(ix => ix["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);

        if (!indexNames.Contains(LegacyBatchIndexName)) return;

        _database.DropCollection(_collectionName);
        _database.DropCollection($"{_collectionName}.pointer");
    }
}
