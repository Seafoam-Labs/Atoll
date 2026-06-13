namespace Atoll.Api.Services.Query;

public sealed record PackagesQuery
{
    public CommaSeparatedQueryParameter? Names { get; init; }

    public string? By { get; init; }
}

public readonly record struct CommaSeparatedQueryParameter(string[] Parts)
{
    public static bool TryParse(string source, out CommaSeparatedQueryParameter result)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            result = new CommaSeparatedQueryParameter([]);
            return true;
        }

        result = new CommaSeparatedQueryParameter(source.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return true;
    }
}