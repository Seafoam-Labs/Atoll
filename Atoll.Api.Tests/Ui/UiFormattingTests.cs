using Atoll.Api.Components;
using Xunit;

namespace Atoll.Api.Tests.Ui;

public class UiFormattingTests
{
    [Fact]
    public void PackageMetaDescriptionFallsBackToTheSiteBlurbWithoutAnAurDescription()
    {
        Assert.Multiple(() =>
        {
            Assert.Equal(UiFormatting.SiteMetaDescription("Atoll"), UiFormatting.PackageMetaDescription("Atoll", null));
            Assert.Equal(UiFormatting.SiteMetaDescription("Atoll"), UiFormatting.PackageMetaDescription("Atoll", "  "));
            Assert.Equal("A prebuilt binary.", UiFormatting.PackageMetaDescription("Atoll", " A prebuilt binary. "));
        });
    }
}
