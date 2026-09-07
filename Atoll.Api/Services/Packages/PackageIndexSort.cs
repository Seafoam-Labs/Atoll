namespace Atoll.Api.Services.Packages;

public enum PackageIndexSortBy
{
    Name,
    Votes,
    Popularity,
    Version
}

public enum PackageIndexSortOrder
{
    Asc,
    Desc
}

public readonly record struct PackageIndexSortByQuery(PackageIndexSortBy SortBy)
{
    public static bool TryParse(string? s, out PackageIndexSortByQuery result)
    {
        if (Enum.TryParse<PackageIndexSortBy>(s, true, out var value))
        {
            result = new PackageIndexSortByQuery(value);
            return true;
        }

        result = default;
        return false;
    }
}

public readonly record struct PackageIndexSortOrderQuery(PackageIndexSortOrder Order)
{
    public static bool TryParse(string? s, out PackageIndexSortOrderQuery result)
    {
        if (Enum.TryParse<PackageIndexSortOrder>(s, true, out var value))
        {
            result = new PackageIndexSortOrderQuery(value);
            return true;
        }

        result = default;
        return false;
    }
}
