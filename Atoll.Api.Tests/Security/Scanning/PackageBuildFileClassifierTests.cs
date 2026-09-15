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
    public void IsPkgbuild_recognises_only_files_named_pkgbuild(string path, bool expected)
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
    public void IsScannable_filters_by_filename_and_extension(string path, bool expected)
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
    public void IsScannable_accepts_all_known_script_extensions(string extension)
    {
        Assert.True(PackageBuildFileClassifier.IsScannable($"script{extension}"));
    }

    [Theory]
    // uppercase extension
    [InlineData("script.SH")]
    // uppercase + nested directory
    [InlineData("scripts/build.PY")]
    public void IsScannable_is_case_insensitive(string path)
    {
        Assert.True(PackageBuildFileClassifier.IsScannable(path));
    }

    [Fact]
    public void IsScannable_returns_false_for_files_without_extension()
    {
        Assert.False(PackageBuildFileClassifier.IsScannable("plainfile"));
    }
}