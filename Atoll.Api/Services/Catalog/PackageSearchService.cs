namespace Atoll.Api.Services.Catalog;

public sealed class PackageSearchService(PackageSearchEngine engine)
{
    private long _requestCount;

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public AurPackageMetadata[] FindByNames(IReadOnlySet<string> names)
    {
        var snapshot = engine.Capture();
        Interlocked.Increment(ref _requestCount);

        return engine.Hydrate(snapshot, names);
    }

    public AurPackageMetadata[] FindByProvides(IReadOnlySet<string> names)
    {
        var snapshot = engine.Capture();
        Interlocked.Increment(ref _requestCount);

        return engine.Hydrate(snapshot, engine.MatchProvides(snapshot, names));
    }

    public AurPackageMetadata[] FindByWords(IReadOnlySet<string> words)
    {
        var snapshot = engine.Capture();
        Interlocked.Increment(ref _requestCount);

        var matchingNames = engine.MatchWords(snapshot, words);
        if (matchingNames is null) return [];

        return
        [
            .. engine.Hydrate(snapshot, matchingNames)
                .OrderByDescending(package => package.NumVotes)
                .Take(50)
        ];
    }

    public AurPackageMetadata[] FindByRelevance(string rawQuery)
    {
        var snapshot = engine.Capture();
        Interlocked.Increment(ref _requestCount);

        // The cap is a rank bound, not a post-sort Take: selection stops at the best 50 candidates.
        return [.. PackageSearchEngine.Rank(snapshot, rawQuery, 50).Select(hit => hit.Package)];
    }
}
