using Atoll.Api.Services.Catalog;
using NUnit.Framework;

namespace Atoll.Api.Tests.Catalog;

public class PackageQueryTests
{
    [TestCase("Name", By.Name)]
    [TestCase("Provides", By.Provides)]
    [TestCase("Words", By.Words)]
    [TestCase("name", By.Name)]
    [TestCase("PROVIDES", By.Provides)]
    [TestCase("words", By.Words)]
    public void ValidValueParsesSuccessfully(string input, By expected)
    {
        var parsed = ByQuery.TryParse(input, out var result);

        Assert.That(parsed, Is.True);
        Assert.That(result.By, Is.EqualTo(expected));
    }

    [Test]
    public void InvalidValueReturnsFalse()
    {
        var parsed = ByQuery.TryParse("Invalid", out var result);

        Assert.That(parsed, Is.False);
        Assert.That(result.By, Is.EqualTo(default(By)));
    }

    [Test]
    public void NullReturnsFalse()
    {
        var parsed = ByQuery.TryParse(null, out var result);

        Assert.That(parsed, Is.False);
        Assert.That(result.By, Is.EqualTo(default(By)));
    }

    [Test]
    public void EmptyStringReturnsFalse()
    {
        var parsed = ByQuery.TryParse(string.Empty, out var result);

        Assert.That(parsed, Is.False);
        Assert.That(result.By, Is.EqualTo(default(By)));
    }

    [Test]
    public void WhitespaceReturnsFalse()
    {
        var parsed = ByQuery.TryParse("   ", out var result);

        Assert.That(parsed, Is.False);
        Assert.That(result.By, Is.EqualTo(default(By)));
    }

    [Test]
    public void NamesAreSplitByComma()
    {
        var parsed = SearchQuery.TryParse("shelly,portable,portable", out var result);

        Assert.That(parsed, Is.True);
        Assert.That(result.Query.Length, Is.EqualTo(3));
        Assert.That(result.Query[0], Is.EqualTo("shelly"));
        Assert.That(result.Query[1], Is.EqualTo("portable"));
        Assert.That(result.Query[2], Is.EqualTo("portable"));
    }

    [Test]
    public void EmptySourceProducesNoParts()
    {
        _ = SearchQuery.TryParse("", out var result);

        Assert.That(result.Query, Is.Empty);
    }
}