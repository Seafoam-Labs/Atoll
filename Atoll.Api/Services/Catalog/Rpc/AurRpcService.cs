using System.Text.Json.Serialization;
using Atoll.Api.Services.Catalog.Indexing;

namespace Atoll.Api.Services.Catalog.Rpc;

public sealed class AurRpcService(PackageIndexStore store)
{
    public const int MaxResults = 5000;

    public IReadOnlyList<AurPackageMetadata> Info(IEnumerable<string> names)
    {
        var snapshot = store.Current;
        var results = new List<AurPackageMetadata>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (seen.Add(name) && snapshot.ByNames.TryGetValue(name, out var package))
                results.Add(package);
        }

        return results;
    }

    public IReadOnlyList<AurPackageMetadata> Search(string query, string by)
    {
        IEnumerable<AurPackageMetadata> matches = store.Current.ByNames.Values;

        matches = by switch
        {
            "name" => matches.Where(package => Contains(package.Name, query)),
            "name-desc" => matches.Where(package =>
                Contains(package.Name, query) || Contains(package.Description, query)),
            "maintainer" => string.IsNullOrEmpty(query)
                ? matches.Where(package => package.Maintainer is null)
                : matches.Where(package => EqualsValue(package.Maintainer, query)),
            "comaintainers" => matches.Where(package => ContainsValue(package.CoMaintainers, query)),
            "depends" => matches.Where(package => ContainsDependency(package.Depends, query)),
            "makedepends" => matches.Where(package => ContainsDependency(package.MakeDepends, query)),
            "optdepends" => matches.Where(package => ContainsDependency(package.OptDepends, query)),
            "checkdepends" => matches.Where(package => ContainsDependency(package.CheckDepends, query)),
            "provides" => matches.Where(package => ContainsDependency(package.Provides, query)),
            "conflicts" => matches.Where(package => ContainsDependency(package.Conflicts, query)),
            "replaces" => matches.Where(package => ContainsDependency(package.Replaces, query)),
            "groups" => matches.Where(package => ContainsValue(package.Groups, query)),
            "submitter" => matches.Where(package => EqualsValue(package.Submitter, query)),
            _ => throw new ArgumentOutOfRangeException(nameof(by), by, null)
        };

        return
        [
            .. matches
                .OrderBy(package => package.Name, StringComparer.Ordinal)
                .Take(MaxResults + 1)
        ];
    }

    public IReadOnlyList<string> Suggest(string prefix, bool packageBases)
    {
        var values = store.Current.ByNames.Values
            .Where(package => (packageBases ? package.PackageBase : package.Name)
                .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(package => packageBases ? package.PackageBase : package.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(20)
            .ToArray();

        return values;
    }

    public IReadOnlyList<string> ResolvePackageNames(string nameOrPackageBase)
    {
        var snapshot = store.Current;
        var names = snapshot.ByNames.Values
            .Where(package => string.Equals(package.PackageBase, nameOrPackageBase, StringComparison.Ordinal))
            .Select(package => package.Name)
            .Where(name => !string.Equals(name, nameOrPackageBase, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Prepend(nameOrPackageBase)
            .ToArray();

        return names;
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static bool EqualsValue(string? value, string query) =>
        string.Equals(value, query, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsValue(IEnumerable<string> values, string query) =>
        values.Any(value => EqualsValue(value, query));

    private static bool ContainsDependency(IEnumerable<string> values, string query) =>
        values.Any(value => EqualsValue(DependencyName(value), query));

    private static string DependencyName(string value)
    {
        var descriptionIndex = value.IndexOf(':');
        var end = descriptionIndex >= 0 ? descriptionIndex : value.Length;

        foreach (var separator in new[] { '<', '>', '=' })
        {
            var index = value.IndexOf(separator);
            if (index >= 0 && index < end)
                end = index;
        }

        return value[..end].Trim();
    }
}

public sealed class AurRpcResponse
{
    [JsonPropertyName("version")] public int Version { get; init; } = 5;
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("resultcount")] public int ResultCount => Results.Count;
    [JsonPropertyName("results")] public IReadOnlyList<AurRpcPackage> Results { get; init; } = [];
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    public static AurRpcResponse Success(string type, IEnumerable<AurPackageMetadata> packages) => new()
    {
        Type = type,
        Results = [.. packages.Select(AurRpcPackage.FromMetadata)]
    };

    public static AurRpcResponse Failure(string error) => new()
    {
        Type = "error",
        Error = error
    };
}

public sealed class AurRpcPackage
{
    [JsonPropertyName("ID")] public long Id { get; init; }
    [JsonPropertyName("Name")] public required string Name { get; init; }
    [JsonPropertyName("PackageBaseID")] public long PackageBaseId { get; init; }
    [JsonPropertyName("PackageBase")] public required string PackageBase { get; init; }
    [JsonPropertyName("Version")] public required string Version { get; init; }
    [JsonPropertyName("Description")] public required string Description { get; init; }
    [JsonPropertyName("URL")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }
    [JsonPropertyName("NumVotes")] public long NumVotes { get; init; }
    [JsonPropertyName("Popularity")] public double Popularity { get; init; }
    [JsonPropertyName("OutOfDate")] public long? OutOfDate { get; init; }
    [JsonPropertyName("Maintainer")] public string? Maintainer { get; init; }
    [JsonPropertyName("Submitter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Submitter { get; init; }
    [JsonPropertyName("FirstSubmitted")] public long FirstSubmitted { get; init; }
    [JsonPropertyName("LastModified")] public long LastModified { get; init; }
    [JsonPropertyName("URLPath")] public required string UrlPath { get; init; }

    [JsonPropertyName("Depends")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Depends { get; init; }
    [JsonPropertyName("MakeDepends")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? MakeDepends { get; init; }
    [JsonPropertyName("OptDepends")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? OptDepends { get; init; }
    [JsonPropertyName("CheckDepends")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? CheckDepends { get; init; }
    [JsonPropertyName("Conflicts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Conflicts { get; init; }
    [JsonPropertyName("Provides")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Provides { get; init; }
    [JsonPropertyName("Replaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Replaces { get; init; }
    [JsonPropertyName("Groups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Groups { get; init; }
    [JsonPropertyName("License")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? License { get; init; }
    [JsonPropertyName("Keywords")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Keywords { get; init; }
    [JsonPropertyName("CoMaintainers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? CoMaintainers { get; init; }

    public static AurRpcPackage FromMetadata(AurPackageMetadata package) => new()
    {
        Id = package.Id,
        Name = package.Name,
        PackageBaseId = package.PackageBaseId,
        PackageBase = package.PackageBase,
        Version = package.Version,
        Description = package.Description,
        Url = package.Url,
        NumVotes = package.NumVotes,
        Popularity = package.Popularity,
        OutOfDate = package.OutOfDate,
        Maintainer = package.Maintainer,
        Submitter = package.Submitter,
        FirstSubmitted = package.FirstSubmitted,
        LastModified = package.LastModified,
        UrlPath = $"/{Uri.EscapeDataString(package.PackageBase)}.git",
        Depends = Optional(package.Depends),
        MakeDepends = Optional(package.MakeDepends),
        OptDepends = Optional(package.OptDepends),
        CheckDepends = Optional(package.CheckDepends),
        Conflicts = Optional(package.Conflicts),
        Provides = Optional(package.Provides),
        Replaces = Optional(package.Replaces),
        Groups = Optional(package.Groups),
        License = Optional(package.License),
        Keywords = Optional(package.Keywords),
        CoMaintainers = Optional(package.CoMaintainers)
    };

    private static IReadOnlyList<string>? Optional(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : values;
}
