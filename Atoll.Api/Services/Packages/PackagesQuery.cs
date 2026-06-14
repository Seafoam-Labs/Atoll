namespace Atoll.Api.Services.Packages;

/// <summary>
///     The type of query to perform for packages.
///     1. Name: search packages by 'name'
///     2. Prov: search packages by 'provides'
///     3. Desc: search packages by 'words' (Name + Description + Keywords)
/// </summary>
public enum QueryType
{
    Name,
    Prov,
    Desc
}

public readonly record struct QueryValues(string[] Values)
{
    public static bool TryParse(string source, out QueryValues result)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            result = new QueryValues([]);
            return true;
        }

        result = new QueryValues(source.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return true;
    }
}