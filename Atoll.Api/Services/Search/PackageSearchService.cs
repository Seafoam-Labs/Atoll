using Atoll.Api.Services.Search.Indexing;

namespace Atoll.Api.Services.Search;

public sealed class PackageSearchService(PackageIndexStore store)
{
    private long _requestCount;

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public AurPackageMetadata[] FindByNames(HashSet<string> names)
    {
        var snapshot = store.Current;
        Interlocked.Increment(ref _requestCount);

        return names
            .Select(name => snapshot.ByNames.GetValueOrDefault(name))
            .Where(package => package is not null)
            .Cast<AurPackageMetadata>()
            .ToArray();
    }

    public AurPackageMetadata[] FindByProvides(HashSet<string> names)
    {
        var snapshot = store.Current;
        Interlocked.Increment(ref _requestCount);

        var matchingNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (!snapshot.ByProvides.TryGetValue(name, out var packageNames)) continue;

            matchingNames.UnionWith(packageNames);
        }

        return matchingNames
            .Select(name => snapshot.ByNames.GetValueOrDefault(name))
            .Where(package => package is not null)
            .Cast<AurPackageMetadata>()
            .ToArray();
    }

    public AurPackageMetadata[] FindByWords(HashSet<string> words)
    {
        var snapshot = store.Current;
        Interlocked.Increment(ref _requestCount);

        if (words.Count == 0) return [];

        HashSet<string>? intersection = null;

        foreach (var word in words)
        {
            if (!snapshot.ByWords.TryGetValue(word, out var packageNames)) return [];

            if (intersection is null)
            {
                intersection = [.. packageNames];
                continue;
            }

            intersection.IntersectWith(packageNames);
            if (intersection.Count == 0) return [];
        }

        if (intersection is null) return [];

        return intersection
            .Select(name => snapshot.ByNames.GetValueOrDefault(name))
            .Where(package => package is not null)
            .Cast<AurPackageMetadata>()
            .OrderByDescending(package => package.NumVotes)
            .Take(50)
            .ToArray();
    }
}