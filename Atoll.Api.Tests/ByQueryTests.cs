using Atoll.Api.Services.Search;
using NUnit.Framework;

namespace Atoll.Api.Tests;

public class ByQueryTests
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
        Assert.That(result.Value, Is.EqualTo(expected));
    }

    [Test]
    public void InvalidValueReturnsFalse()
    {
        var parsed = ByQuery.TryParse("Invalid", out var result);

        Assert.That(parsed, Is.False);
        Assert.That(result.Value, Is.EqualTo(default(By)));
    }

    [Test]
    public void NullReturnsFalse()
    {
        var parsed = ByQuery.TryParse(null, out var result);

        Assert.That(parsed, Is.False);
        Assert.That(result.Value, Is.EqualTo(default(By)));
    }

    [Test]
    public void EmptyStringReturnsFalse()
    {
        var parsed = ByQuery.TryParse(string.Empty, out var result);

        Assert.That(parsed, Is.False);
        Assert.That(result.Value, Is.EqualTo(default(By)));
    }

    [Test]
    public void WhitespaceReturnsFalse()
    {
        var parsed = ByQuery.TryParse("   ", out var result);

        Assert.That(parsed, Is.False);
        Assert.That(result.Value, Is.EqualTo(default(By)));
    }
}