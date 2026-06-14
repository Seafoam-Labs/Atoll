using System.Collections.Immutable;
using System.Text.Json;

namespace Atoll.Api.Services.Indexing;

public static class PackageDataLoader
{
    public static async Task<PackageIndexes> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AUR package dump is not a JSON array.");

        var byNamesBuilder = ImmutableDictionary.CreateBuilder<string, AurPackage>(StringComparer.Ordinal);
        var byProvidesMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byWordsMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var packageElement in doc.RootElement.EnumerateArray())
        {
            if (!packageElement.TryGetProperty("Name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String) continue;

            var name = nameElement.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            var package = packageElement.DeserializeAurPackage();
            byNamesBuilder[name] = package;
            IndexProvides(byProvidesMap, name, packageElement);
            IndexWords(byWordsMap, name, packageElement);
        }

        var byProvides = byProvidesMap.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToImmutableHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var byWords = byWordsMap.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToImmutableHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        return new PackageIndexes(byNamesBuilder.ToImmutable(), byProvides, byWords);
    }

    private static void IndexProvides(Dictionary<string, HashSet<string>> byProvides, string packageName,
        JsonElement package)
    {
        var provides = package.TryGetStringArray("Provides");

        if (provides.Length == 0)
        {
            AddValue(byProvides, packageName, packageName);
            return;
        }

        foreach (var provided in provides) AddValue(byProvides, provided, packageName);
    }

    private static void IndexWords(Dictionary<string, HashSet<string>> byWords, string packageName, JsonElement package)
    {
        var desc = package.TryGetProperty("Description", out var descriptionElement) &&
                   descriptionElement.ValueKind == JsonValueKind.String
            ? descriptionElement.GetString() ?? string.Empty
            : string.Empty;

        var nameTerms = packageName.Split(['-', '_'], StringSplitOptions.None);
        var descTerms = desc.Split(' ');
        var keywords = package.TryGetStringArray("Keywords");

        foreach (var word in TokenCleaning.SplitAndClean(nameTerms.Concat(descTerms).Concat(keywords)))
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