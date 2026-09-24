namespace Atoll.Api.Services.Catalog;

public enum By
{
    Name,
    Provides,
    Words,

    // 3 is deliberately left undefined: the binder comma-joins repeated ?by= values and
    // Enum.TryParse ORs comma-delimited names, so "provides,words" parses to (By)3. Keeping 3
    // undefined leaves that combination rejected by the endpoint's throw arm (400) rather than
    // silently aliasing a real mode. Relevance therefore takes the next free value, 4.
    Relevance = 4
}

public readonly record struct ByQuery(By By)
{
    public static bool TryParse(string? s, out ByQuery result)
    {
        if (Enum.TryParse<By>(s, true, out var value))
        {
            result = new ByQuery(value);
            return true;
        }

        result = default;
        return false;
    }
}

public readonly record struct SearchQuery(string[] Query, string Raw)
{
    public static bool TryParse(string source, out SearchQuery result)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            result = new SearchQuery([], source);
            return true;
        }

        result = new SearchQuery(
            source.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            source);
        return true;
    }
}