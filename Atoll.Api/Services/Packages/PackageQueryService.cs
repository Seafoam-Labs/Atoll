using System.Text.Json;

namespace Atoll.Api.Services.Packages;

public sealed class PackageQueryService(PackageIndexStore store)
{
    private long _requestCount;

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public IReadOnlyList<JsonElement> FindByNames(HashSet<string> names)
    {
        var snapshot = store.Current;
        Interlocked.Increment(ref _requestCount);

        return names
            .Select(name => snapshot.ByNames.GetValueOrDefault(name))
            .Where(package => package.ValueKind != JsonValueKind.Undefined)
            .ToArray();
    }

    public IReadOnlyList<JsonElement> FindByProvides(HashSet<string> names)
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
            .Where(package => package.ValueKind != JsonValueKind.Undefined)
            .ToArray();
    }

    public IReadOnlyList<JsonElement> FindByWords(HashSet<string> words)
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
            .Where(package => package.ValueKind != JsonValueKind.Undefined)
            .OrderByDescending(GetNumVotes)
            .Take(50)
            .ToArray();
    }

    private static long GetNumVotes(JsonElement package)
    {
        if (!package.TryGetProperty("NumVotes", out var votes)) return 0;

        return votes.ValueKind switch
        {
            JsonValueKind.Number when votes.TryGetInt64(out var n) => n,
            JsonValueKind.Number when votes.TryGetDouble(out var d) => (long)d,
            _ => 0
        };
    }
}