using System.Collections.Immutable;
using System.Text.Json;
using Atoll.Api.Extensions;

namespace Atoll.Api.Services.Search.Indexing;

public static class PackageDataLoader
{
    public static async Task<SearchIndexData> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AUR package dump is not a JSON array.");

        var packages = doc.RootElement.EnumerateArray()
            .Where(element => element.TryGetProperty("Name", out var nameElement) &&
                              nameElement.ValueKind == JsonValueKind.String &&
                              !string.IsNullOrEmpty(nameElement.GetString()))
            .Select(element => element.DeserializeAurPackage())
            .ToList();

        return BuildFromPackages(packages);
    }

    public static SearchIndexData BuildFromPackages(IEnumerable<AurPackageMetadata> packages)
    {
        var byNamesBuilder = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        var byProvidesMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byWordsMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var package in packages)
        {
            if (string.IsNullOrEmpty(package.Name)) continue;

            byNamesBuilder[package.Name] = package;
            IndexProvides(byProvidesMap, package);
            IndexWords(byWordsMap, package);
        }

        var byProvides = byProvidesMap.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToImmutableHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var byWords = byWordsMap.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToImmutableHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        return new SearchIndexData(byNamesBuilder.ToImmutable(), byProvides, byWords);
    }

    private static void IndexProvides(Dictionary<string, HashSet<string>> byProvides, AurPackageMetadata package)
    {
        var packageName = package.Name;
        var provides = package.Provides;

        if (provides.Count == 0)
        {
            AddValue(byProvides, packageName, packageName);
            return;
        }

        foreach (var provided in provides) AddValue(byProvides, provided, packageName);
    }

    private static void IndexWords(Dictionary<string, HashSet<string>> byWords, AurPackageMetadata package)
    {
        var packageName = package.Name;
        var desc = package.Description;

        var nameTerms = packageName.Split(['-', '_'], StringSplitOptions.None);
        var descTerms = desc.Split(' ');

        foreach (var word in TokenCleaning.SplitAndClean(nameTerms.Concat(descTerms).Concat(package.Keywords)))
            AddValue(byWords, word, packageName);
    }

    private static void AddValue(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            map[key] = set;
        }

        set.Add(value);
    }
}