using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Atoll.Api.Services.Packages.Seed;

public interface ISeedExclusionRepository
{
    Task<IReadOnlySet<string>> ListDocumentTooLargePackageBasesAsync(CancellationToken ct = default);

    Task RecordDocumentTooLargeAsync(
        string packageBase,
        IReadOnlyList<string> packageNames,
        long serializedSizeBytes,
        CancellationToken ct = default);
}

public sealed class MongoSeedExclusionRepository : ISeedExclusionRepository
{
    private readonly IMongoCollection<SeedExclusionDocument> _exclusions;

    public MongoSeedExclusionRepository(IMongoClient client, IOptions<AtollOptions> options)
    {
        var mongo = options.Value.Mongo;
        _exclusions = client
            .GetDatabase(mongo.Database)
            .GetCollection<SeedExclusionDocument>(mongo.Collections.SeedExclusions);
    }

    public async Task<IReadOnlySet<string>> ListDocumentTooLargePackageBasesAsync(CancellationToken ct = default)
    {
        var bases = await _exclusions
            .Find(x => x.Reason == SeedExclusionReasons.DocumentTooLarge)
            .Project(x => x.PackageBase)
            .ToListAsync(ct);

        return new HashSet<string>(bases, StringComparer.Ordinal);
    }

    public Task RecordDocumentTooLargeAsync(
        string packageBase,
        IReadOnlyList<string> packageNames,
        long serializedSizeBytes,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var update = Builders<SeedExclusionDocument>.Update
            .SetOnInsert(x => x.Id, packageBase)
            .SetOnInsert(x => x.PackageBase, packageBase)
            .SetOnInsert(x => x.FirstSeenUtc, now)
            .Set(x => x.PackageNames, [.. packageNames])
            .Set(x => x.Reason, SeedExclusionReasons.DocumentTooLarge)
            .Set(x => x.SerializedSizeBytes, serializedSizeBytes)
            .Set(x => x.LastSeenUtc, now);

        return _exclusions.UpdateOneAsync(
            x => x.Id == packageBase,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }
}

public static class SeedExclusionReasons
{
    public const string DocumentTooLarge = "mongo-document-too-large";
}