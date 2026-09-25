using System.Collections.Immutable;
using System.Text.Json;
using Atoll.Api.Extensions;

namespace Atoll.Api.Services.Catalog.Indexing;

public static class PackageIndexBuilder
{
    private static readonly char[] NameSeparators = ['-', '_'];

    public static async Task<SearchIndexData> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseDump(doc.RootElement);
    }

    /// <summary>
    /// Builds the index from dump text, for callers that hold the JSON and cannot await. The file
    /// based <see cref="LoadAsync" /> stays the production entry point.
    /// </summary>
    internal static SearchIndexData Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseDump(doc.RootElement);
    }

    private static SearchIndexData ParseDump(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AUR package dump is not a JSON array.");

        using var array = root.EnumerateArray();
        var packages = array
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
        var packagesById = new List<AurPackageMetadata>();
        var nameTokensById = new List<string[]>();
        var idsByName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var package in packages)
        {
            if (string.IsNullOrEmpty(package.Name)) continue;

            byNamesBuilder[package.Name] = package;
            IndexProvides(byProvidesMap, package);
            var nameTokens = IndexWords(byWordsMap, package);

            // ByNames keeps the last package for a repeated name, so the id space mirrors it: a
            // repeat replaces its slot rather than adding a second id for one name.
            if (idsByName.TryGetValue(package.Name, out var id))
            {
                packagesById[id] = package;
                nameTokensById[id] = nameTokens;
            }
            else
            {
                idsByName[package.Name] = packagesById.Count;
                packagesById.Add(package);
                nameTokensById.Add(nameTokens);
            }
        }

        var byProvides = byProvidesMap.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToImmutableHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var byWords = byWordsMap.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToImmutableHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var relevance = RelevanceIndex.Build([.. packagesById], [.. nameTokensById], idsByName);

        return new SearchIndexData(byNamesBuilder.ToImmutable(), byProvides, byWords, relevance);
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

    private static string[] IndexWords(Dictionary<string, HashSet<string>> byWords, AurPackageMetadata package)
    {
        var packageName = package.Name;
        var descTerms = package.Description.Split(' ');

        // Cleaned name tokens are materialized once and feed both the word map and the relevance
        // name-token postings, so interior-token membership cannot drift between them. Cleaning the
        // name first keeps ByWords' insertion order identical to the single concatenated pass this
        // replaces: a token repeated by the description or keywords is already in the set, so the
        // second AddValue cannot move it.
        string[] nameTokens = [.. TokenCleaning.SplitAndClean(packageName.Split(NameSeparators, StringSplitOptions.None))];

        foreach (var word in nameTokens) AddValue(byWords, word, packageName);
        foreach (var word in TokenCleaning.SplitAndClean(descTerms.Concat(package.Keywords)))
            AddValue(byWords, word, packageName);

        return nameTokens;
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