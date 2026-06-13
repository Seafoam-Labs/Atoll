using NUnit.Framework;

namespace Atoll.Api.Tests;

public class SearchTermsTests
{
    [Test]
    public void NamesAreSplitByComma()
    {
        var parsed = SearchTerms.TryParse("shelly,portable,portable", out var result);

        Assert.That(parsed, Is.True);
        Assert.That(result.Values.Length, Is.EqualTo(3));
        Assert.That(result.Values[0], Is.EqualTo("shelly"));
        Assert.That(result.Values[1], Is.EqualTo("portable"));
        Assert.That(result.Values[2], Is.EqualTo("portable"));
    }

    [Test]
    public void EmptySourceProducesNoParts()
    {
        _ = SearchTerms.TryParse("", out var result);

        Assert.That(result.Values, Is.Empty);
    }
}