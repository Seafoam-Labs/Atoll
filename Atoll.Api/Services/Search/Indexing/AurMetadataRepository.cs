using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Atoll.Api.Services.Search.Indexing;

public sealed class AurMetadataRepository : IAurMetadataRepository
{
    private readonly IMongoCollection<AurPackageMetadataDocument> _packages;
    private readonly IMongoCollection<BatchPointer> _pointer;

    public AurMetadataRepository(IMongoClient client, IOptions<AtollOptions> options)
    {
        var o = options.Value;
        var db = client.GetDatabase(o.Mongo.Database);
        var aurMetadata = o.Mongo.Collections.AurMetadata;
        _packages = db.GetCollection<AurPackageMetadataDocument>(aurMetadata);
        _pointer = db.GetCollection<BatchPointer>($"{aurMetadata}.pointer");

        var index = new CreateIndexModel<AurPackageMetadataDocument>(
            Builders<AurPackageMetadataDocument>.IndexKeys
                .Ascending(x => x.BatchId)
                .Ascending(x => x.AurId),
            new CreateIndexOptions { Unique = true });

        _packages.Indexes.CreateOne(index);
    }

    public async Task SaveAsync(IEnumerable<AurPackageMetadata> packages, CancellationToken ct)
    {
        var batchId = Guid.NewGuid().ToString("N");

        var docs = packages.Select(p => p.ToDocument(batchId)).ToList();

        if (docs.Count == 0)
        {
            await SwapPointerAsync(batchId, ct);
            return;
        }

        await _packages.InsertManyAsync(docs, cancellationToken: ct);
        await SwapPointerAsync(batchId, ct);
    }

    public async Task<IReadOnlyList<AurPackageMetadata>> LoadAsync(CancellationToken ct)
    {
        var pointer = await _pointer
            .Find(Builders<BatchPointer>.Filter.Empty)
            .FirstOrDefaultAsync(ct);

        if (pointer is null) return [];

        var docs = await _packages
            .Find(Builders<AurPackageMetadataDocument>.Filter.Eq(x => x.BatchId, pointer.ActiveBatchId))
            .ToListAsync(ct);

        return docs
            .Select(x => x.ToMetadata())
            .ToList();
    }

    public async Task<bool> ExistsAsync(CancellationToken ct)
    {
        var pointer = await _pointer
            .Find(Builders<BatchPointer>.Filter.Empty)
            .FirstOrDefaultAsync(ct);

        return pointer is not null;
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        var pointer = await _pointer
            .Find(Builders<BatchPointer>.Filter.Empty)
            .FirstOrDefaultAsync(ct);

        if (pointer is null) return 0;

        return await _packages.CountDocumentsAsync(
            Builders<AurPackageMetadataDocument>.Filter.Eq(x => x.BatchId, pointer.ActiveBatchId),
            cancellationToken: ct);
    }

    public async Task DeleteAsync(CancellationToken ct)
    {
        await _packages.DeleteManyAsync(Builders<AurPackageMetadataDocument>.Filter.Empty, ct);
        await _pointer.DeleteManyAsync(Builders<BatchPointer>.Filter.Empty, ct);
    }

    private async Task SwapPointerAsync(string batchId, CancellationToken ct)
    {
        var previous = await _pointer.FindOneAndReplaceAsync(
            Builders<BatchPointer>.Filter.Empty,
            new BatchPointer { ActiveBatchId = batchId },
            new FindOneAndReplaceOptions<BatchPointer, BatchPointer>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.Before
            },
            ct);

        if (previous?.ActiveBatchId is not null)
            await _packages.DeleteManyAsync(
                Builders<AurPackageMetadataDocument>.Filter.Eq(x => x.BatchId, previous.ActiveBatchId),
                ct);
    }
}