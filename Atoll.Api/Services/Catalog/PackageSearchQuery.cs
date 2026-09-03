namespace Atoll.Api.Services.Catalog;

public enum By
{
    Name,
    Provides,
    Words
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

public readonly record struct SearchQuery(string[] Query)
{
    public static bool TryParse(string source, out SearchQuery result)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            result = new SearchQuery([]);
            return true;
        }

        result = new SearchQuery(source.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return true;
    }
}