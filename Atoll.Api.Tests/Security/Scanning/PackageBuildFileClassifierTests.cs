using Atoll.Api.Services.Security.Scanning;
using Xunit;

namespace Atoll.Api.Tests.Security.Scanning;

public class PackageBuildFileClassifierTests
{
    [Theory]
    [InlineData("PKGBUILD", true)]
    // case-insensitive match
    [InlineData("pkgbuild", true)]
    [InlineData("PKGBuild", true)]
    // basename match regardless of directory
    [InlineData("dir/PKGBUILD", true)]
    [InlineData("/tmp/build/PKGBUILD", true)]
    // basename must be exactly PKGBUILD
    [InlineData("PKGBUILD.txt", false)]
    [InlineData("foo.PKGBUILD", false)]
    [InlineData("PKGBUILDS", false)]
    [InlineData("SRCINFO", false)]
    [InlineData("", false)]
    public void IsPkgbuild_OnlyExactPkgbuildBasenameMatches(string path, bool expected)
    {
        Assert.Equal(expected, PackageBuildFileClassifier.IsPkgbuild(path));
    }

    [Theory]
    // PKGBUILD is always scannable
    [InlineData("PKGBUILD", true)]
    [InlineData("package.install", true)]
    [InlineData("data.bin", false)]
    [InlineData("README.md", false)]
    // extension is .gz, not a script
    [InlineData("source.tar.gz", false)]
    [InlineData("archive.tar.bz2", false)]
    public void IsScannable_FilenameAndExtension_FiltersNonScripts(string path, bool expected)
    {
        Assert.Equal(expected, PackageBuildFileClassifier.IsScannable(path));
    }

    [Theory]
    [InlineData(".sh")]
    [InlineData(".bash")]
    [InlineData(".install")]
    [InlineData(".hook")]
    [InlineData(".py")]
    [InlineData(".pl")]
    [InlineData(".rb")]
    [InlineData(".service")]
    [InlineData(".csh")]
    [InlineData(".zsh")]
    public void IsScannable_KnownScriptExtensions_AllAccepted(string extension)
    {
        Assert.True(PackageBuildFileClassifier.IsScannable($"script{extension}"));
    }

    [Theory]
    // uppercase extension
    [InlineData("script.SH")]
    // uppercase + nested directory
    [InlineData("scripts/build.PY")]
    public void IsScannable_UppercaseExtensions_Accepted(string path)
    {
        Assert.True(PackageBuildFileClassifier.IsScannable(path));
    }

    [Fact]
    public void IsScannable_FileWithoutExtension_ReturnsFalse()
    {
        Assert.False(PackageBuildFileClassifier.IsScannable("plainfile"));
    }
}