namespace Atoll.Api.Services.Query;

public static class QueryParsing
{
    public static HashSet<string> ParseNames(string? rawNames)
    {
        if (string.IsNullOrWhiteSpace(rawNames)) return [];

        return rawNames
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}