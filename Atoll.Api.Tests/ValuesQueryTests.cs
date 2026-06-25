using NUnit.Framework;

namespace Atoll.Api.Tests;

public class ValuesQueryTests
{
    [Test]
    public void NamesAreSplitByComma()
    {
        var parsed = ValuesQuery.TryParse("shelly,portable,portable", out var result);

        Assert.That(parsed, Is.True);
        Assert.That(result.Values.Length, Is.EqualTo(3));
        Assert.That(result.Values[0], Is.EqualTo("shelly"));
        Assert.That(result.Values[1], Is.EqualTo("portable"));
        Assert.That(result.Values[2], Is.EqualTo("portable"));
    }

    [Test]
    public void EmptySourceProducesNoParts()
    {
        _ = ValuesQuery.TryParse("", out var result);

        Assert.That(result.Values, Is.Empty);
    }
}