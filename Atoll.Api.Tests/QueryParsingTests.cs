using NUnit.Framework;

namespace Atoll.Api.Tests;

public class CommaSeparatedQueryParameterTests
{
    [Test]
    public void NamesAreSplitByComma()
    {
        var parsed = CommaSeparatedQueryParameter.TryParse("shelly,portable,portable", out var result);

        Assert.That(parsed, Is.True);
        Assert.That(result.Parts.Length, Is.EqualTo(3));
        Assert.That(result.Parts[0], Is.EqualTo("shelly"));
        Assert.That(result.Parts[1], Is.EqualTo("portable"));
        Assert.That(result.Parts[2], Is.EqualTo("portable"));
    }

    [Test]
    public void EmptySourceProducesNoParts()
    {
        _ = CommaSeparatedQueryParameter.TryParse("", out var result);

        Assert.That(result.Parts, Is.Empty);
    }
}