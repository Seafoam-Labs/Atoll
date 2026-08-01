namespace Atoll.Api.Services.Packages.Seed;

public static class BulkSeedPlan
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildPkgBaseTargets(
        IEnumerable<string> packageNames,
        Func<string, string> resolvePackageBase)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var name in packageNames)
        {
            if (string.IsNullOrEmpty(name)) continue;

            var pkgBase = resolvePackageBase(name);
            if (string.IsNullOrEmpty(pkgBase)) pkgBase = name;

            if (!result.TryGetValue(pkgBase, out var names))
            {
                names = [];
                result[pkgBase] = names;
            }

            names.Add(name);
        }

        return result.ToDictionary(kv => kv.Key, IReadOnlyList<string> (kv) => kv.Value, StringComparer.Ordinal);
    }

    public static IEnumerable<IReadOnlyList<T>> ChunkBy<T>(IReadOnlyList<T> source, int batchSize)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");

        return ChunkByIterator(source, batchSize);
    }

    private static IEnumerable<IReadOnlyList<T>> ChunkByIterator<T>(
        IReadOnlyList<T> source,
        int batchSize)
    {
        for (var i = 0; i < source.Count; i += batchSize)
        {
            var remaining = source.Count - i;
            var take = Math.Min(batchSize, remaining);
            var slice = new T[take];
            for (var j = 0; j < take; j++)
                slice[j] = source[i + j];
            yield return slice;
        }
    }
}