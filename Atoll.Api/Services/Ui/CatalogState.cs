using Microsoft.AspNetCore.Components;

namespace Atoll.Api.Services.Ui;

/// <summary>
///     The catalog page's query-string state, the single source of truth for the page. Best match is
///     the default mode for every URL, the relevance sort exists only for a non-blank ranked query,
///     and a value equal to its effective default is omitted from the URL.
/// </summary>
public sealed record CatalogState(
    string Query,
    int Page,
    CatalogSearchMode Mode,
    CatalogSeededFilter Seeded,
    CatalogSecurityFilter Security,
    CatalogSort Sort)
{
    public static CatalogState Default { get; } = new(
        "", 1, CatalogSearchMode.Relevance, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSort.NameAsc);

    /// <summary>The mode a bare URL resolves to: Best match, with or without a query.</summary>
    public static CatalogSearchMode DefaultMode => CatalogSearchMode.Relevance;

    /// <summary>The sort a bare URL resolves to: the ranked order for a ranked query, else name order.</summary>
    public static CatalogSort DefaultSortFor(CatalogSearchMode mode, string query) =>
        mode == CatalogSearchMode.Relevance && !string.IsNullOrWhiteSpace(query)
            ? CatalogSort.Relevance
            : CatalogSort.NameAsc;

    public static CatalogState FromQuery(
        string? q,
        int? page,
        string? mode,
        string? seeded,
        string? security,
        string? sort)
    {
        var query = q ?? "";
        var resolvedMode = Parse(mode, DefaultMode);
        var parsedSort = Parse(sort, DefaultSortFor(resolvedMode, query));

        // A relevance sort cannot order a legacy mode or a blank query; those fall back to name order.
        var resolvedSort = parsedSort == CatalogSort.Relevance
            ? DefaultSortFor(resolvedMode, query)
            : parsedSort;

        return new CatalogState(
            query,
            Math.Max(1, page ?? 1),
            resolvedMode,
            Parse(seeded, CatalogSeededFilter.All),
            Parse(security, CatalogSecurityFilter.Any),
            resolvedSort);
    }

    public string ToUri(NavigationManager nav) => nav.GetUriWithQueryParameters(ToQueryParameters());

    /// <summary>
    ///     The URL query parameters for this state. A <see langword="null" /> value encodes removal
    ///     (NavigationManager drops a parameter whose nullable value is null), and every value equal to
    ///     its effective default is absent, so the bare catalog URL stays bare.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ToQueryParameters()
    {
        var sort = Sort == CatalogSort.Relevance ? DefaultSortFor(Mode, Query) : Sort;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["q"] = string.IsNullOrWhiteSpace(Query) ? null : Query,
            ["page"] = Page > 1 ? Page : null,
            ["mode"] = Mode == DefaultMode ? null : Kebab(Mode),
            ["seeded"] = Seeded == CatalogSeededFilter.All ? null : Kebab(Seeded),
            ["security"] = Security == CatalogSecurityFilter.Any ? null : Kebab(Security),
            ["sort"] = sort == DefaultSortFor(Mode, Query) ? null : Kebab(sort)
        };
    }

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum
    {
        var text = value?.Replace("-", "", StringComparison.Ordinal);
        return text is not null
               && Enum.TryParse<T>(text, ignoreCase: true, out var parsed)
               && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
    }

    // e.g. VotesDesc -> votes-desc
    private static string Kebab<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var chars = new char[name.Length * 2];
        var length = 0;
        foreach (var ch in name)
        {
            if (char.IsUpper(ch) && length > 0)
                chars[length++] = '-';
            chars[length++] = char.ToLowerInvariant(ch);
        }

        return new string(chars, 0, length);
    }
}