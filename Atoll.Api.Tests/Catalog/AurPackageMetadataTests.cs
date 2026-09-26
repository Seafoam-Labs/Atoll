using Atoll.Api.Services.Catalog;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

public class AurPackageMetadataTests
{
    [Fact]
    public void Equals_StructurallyIdenticalInstances_AreEqualAndShareAHashCode()
    {
        var left = Sample();
        var right = Sample();

        Assert.Multiple(() =>
        {
            // The list instances differ, so equality has to come from their contents.
            Assert.NotSame(left, right);
            Assert.Equal(left, right);
            Assert.Equal(left.GetHashCode(), right.GetHashCode());
        });
    }

    [Fact]
    public void Equals_DifferingCollectionContent_BreaksEquality()
    {
        Assert.NotEqual(Sample(), Sample() with { Depends = ["other"] });
    }

    [Fact]
    public void Equals_DifferingCollectionOrder_BreaksEquality()
    {
        Assert.NotEqual(Sample(), Sample() with { Keywords = ["b", "a"] });
    }

    private static AurPackageMetadata Sample()
    {
        return new AurPackageMetadata(
            7, "ghost-bin", 3, "ghost", "1.0-1",
            "sample metadata", "https://example.test", 12, 4.5, null,
            "alice", "bob", 1600000000, 1700000001,
            "/cgit/ghost.git",
            ["dep1"], ["make1"], ["opt1"],
            ["conflict1"], ["provided"], ["MIT"],
            ["a", "b"], ["bob"])
        {
            CheckDepends = ["check1"],
            Groups = ["group1"],
            Replaces = ["old-ghost"]
        };
    }
}
