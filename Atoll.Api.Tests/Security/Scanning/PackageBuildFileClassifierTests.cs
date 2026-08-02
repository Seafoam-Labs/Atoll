using Atoll.Api.Services.Security.Scanning;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security.Scanning;

public class PackageBuildFileClassifierTests
{
    [TestCase("PKGBUILD", true)]
    [TestCase("pkgbuild", true, Description = "case-insensitive match")]
    [TestCase("PKGBuild", true)]
    [TestCase("dir/PKGBUILD", true, Description = "basename match regardless of directory")]
    [TestCase("/tmp/build/PKGBUILD", true)]
    [TestCase("PKGBUILD.txt", false, Description = "basename must be exactly PKGBUILD")]
    [TestCase("foo.PKGBUILD", false)]
    [TestCase("PKGBUILDS", false)]
    [TestCase("SRCINFO", false)]
    [TestCase("", false)]
    public void IsPkgbuild_recognises_only_files_named_pkgbuild(string path, bool expected)
    {
        Assert.That(PackageBuildFileClassifier.IsPkgbuild(path), Is.EqualTo(expected));
    }

    [TestCase("PKGBUILD", true, Description = "PKGBUILD is always scannable")]
    [TestCase("package.install", true)]
    [TestCase("data.bin", false)]
    [TestCase("README.md", false)]
    [TestCase("source.tar.gz", false, Description = "extension is .gz, not a script")]
    [TestCase("archive.tar.bz2", false)]
    public void IsScannable_filters_by_filename_and_extension(string path, bool expected)
    {
        Assert.That(PackageBuildFileClassifier.IsScannable(path), Is.EqualTo(expected));
    }

    [TestCase(".sh")]
    [TestCase(".bash")]
    [TestCase(".install")]
    [TestCase(".hook")]
    [TestCase(".py")]
    [TestCase(".pl")]
    [TestCase(".rb")]
    [TestCase(".service")]
    [TestCase(".csh")]
    [TestCase(".zsh")]
    public void IsScannable_accepts_all_known_script_extensions(string extension)
    {
        Assert.That(PackageBuildFileClassifier.IsScannable($"script{extension}"), Is.True);
    }

    [TestCase("script.SH", Description = "uppercase extension")]
    [TestCase("scripts/build.PY", Description = "uppercase + nested directory")]
    public void IsScannable_is_case_insensitive(string path)
    {
        Assert.That(PackageBuildFileClassifier.IsScannable(path), Is.True);
    }

    [Test]
    public void IsScannable_returns_false_for_files_without_extension()
    {
        Assert.That(PackageBuildFileClassifier.IsScannable("plainfile"), Is.False);
    }
}