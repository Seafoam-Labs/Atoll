using NUnit.Framework;

namespace Atoll.Api.Tests;

public class QueryParsingTests
{
    [Test]
    public void NamesAreSplitByCommaAndDeduplicated()
    {
        var parsed = QueryParsing.ParseNames("shelly,portable,portable");

        Assert.That(parsed.Count, Is.EqualTo(2));
        Assert.That(parsed, Does.Contain("shelly"));
        Assert.That(parsed, Does.Contain("portable"));
    }
}