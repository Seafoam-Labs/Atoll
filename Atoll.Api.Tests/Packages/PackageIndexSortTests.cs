using Atoll.Api.Services.Packages;
using Xunit;

namespace Atoll.Api.Tests.Packages;

public class PackageIndexSortTests
{
    // Same Enum.TryParse shape as By: numerics bind, comma-separated names OR into one value, and
    // the binder comma-joins repeated parameters, so a combination never fails to parse on its own.
    [Theory]
    [InlineData("name", PackageIndexSortBy.Name)]
    [InlineData("VOTES", PackageIndexSortBy.Votes)]
    [InlineData(" name", PackageIndexSortBy.Name)]
    [InlineData("0", PackageIndexSortBy.Name)]
    [InlineData("3", PackageIndexSortBy.Version)]
    [InlineData("8", (PackageIndexSortBy)8)]
    [InlineData("votes,popularity", PackageIndexSortBy.Version)]
    [InlineData("name,version", PackageIndexSortBy.Version)]
    public void TryParse_SortByNamesNumericsAndFlagCombinations_SetsSortBy(string input, PackageIndexSortBy expected)
    {
        var parsed = PackageIndexSortByQuery.TryParse(input, out var result);

        Assert.True(parsed);
        Assert.Equal(expected, result.SortBy);
    }

    [Theory]
    [InlineData("asc", PackageIndexSortOrder.Asc)]
    [InlineData("DESC", PackageIndexSortOrder.Desc)]
    [InlineData(" desc ", PackageIndexSortOrder.Desc)]
    [InlineData("0", PackageIndexSortOrder.Asc)]
    [InlineData("1", PackageIndexSortOrder.Desc)]
    [InlineData("asc,desc", PackageIndexSortOrder.Desc)]
    [InlineData("2", (PackageIndexSortOrder)2)]
    public void TryParse_SortOrderNamesNumericsAndFlagCombinations_SetsOrder(string input, PackageIndexSortOrder expected)
    {
        var parsed = PackageIndexSortOrderQuery.TryParse(input, out var result);

        Assert.True(parsed);
        Assert.Equal(expected, result.Order);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    public void TryParse_UnparseableSortAndOrderValues_ReturnsFalse(string? input)
    {
        Assert.False(PackageIndexSortByQuery.TryParse(input, out _));
        Assert.False(PackageIndexSortOrderQuery.TryParse(input, out _));
    }
}
