using Atoll.Api.Services.Catalog;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

public class PackageQueryTests
{
    [Theory]
    [InlineData("Name", By.Name)]
    [InlineData("Provides", By.Provides)]
    [InlineData("Words", By.Words)]
    [InlineData("name", By.Name)]
    [InlineData("PROVIDES", By.Provides)]
    [InlineData("words", By.Words)]
    public void ValidValueParsesSuccessfully(string input, By expected)
    {
        var parsed = ByQuery.TryParse(input, out var result);

        Assert.True(parsed);
        Assert.Equal(expected, result.By);
    }

    [Fact]
    public void InvalidValueReturnsFalse()
    {
        var parsed = ByQuery.TryParse("Invalid", out var result);

        Assert.False(parsed);
        Assert.Equal(default(By), result.By);
    }

    [Fact]
    public void NullReturnsFalse()
    {
        var parsed = ByQuery.TryParse(null, out var result);

        Assert.False(parsed);
        Assert.Equal(default(By), result.By);
    }

    [Fact]
    public void EmptyStringReturnsFalse()
    {
        var parsed = ByQuery.TryParse(string.Empty, out var result);

        Assert.False(parsed);
        Assert.Equal(default(By), result.By);
    }

    [Fact]
    public void WhitespaceReturnsFalse()
    {
        var parsed = ByQuery.TryParse("   ", out var result);

        Assert.False(parsed);
        Assert.Equal(default(By), result.By);
    }

    [Fact]
    public void NamesAreSplitByComma()
    {
        var parsed = SearchQuery.TryParse("shelly,portable,portable", out var result);

        Assert.True(parsed);
        Assert.Equal(3, result.Query.Length);
        Assert.Equal("shelly", result.Query[0]);
        Assert.Equal("portable", result.Query[1]);
        Assert.Equal("portable", result.Query[2]);
    }

    [Fact]
    public void EmptySourceProducesNoParts()
    {
        _ = SearchQuery.TryParse("", out var result);

        Assert.Empty(result.Query);
    }
}