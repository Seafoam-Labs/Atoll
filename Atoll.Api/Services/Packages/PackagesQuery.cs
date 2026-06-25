namespace Atoll.Api.Services.Packages;

/// <summary>
///     The type of query to perform for packages.
/// </summary>
public enum By
{
    Name,
    Provides,
    Words
}

public readonly record struct ByQuery(By Value)
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

public readonly record struct ValuesQuery(string[] Values)
{
    public static bool TryParse(string source, out ValuesQuery result)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            result = new ValuesQuery([]);
            return true;
        }

        result = new ValuesQuery(source.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return true;
    }
}