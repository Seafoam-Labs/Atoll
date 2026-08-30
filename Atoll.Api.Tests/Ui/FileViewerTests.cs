using Atoll.Api.Components;
using NUnit.Framework;

namespace Atoll.Api.Tests.Ui;

public class FileViewerTests
{
    [TestCase("PKGBUILD", "pkgbuild")]
    [TestCase("post-install.install", "bash")]
    [TestCase(".SRCINFO", "ini")]
    [TestCase("Makefile", "makefile")]
    [TestCase("src/main.py", "python")]
    [TestCase("fixes.patch", "diff")]
    [TestCase("org.example.desktop", "ini")]
    [TestCase("20-atool.hook", "ini")]
    [TestCase("99-udev.rules", "ini")]
    [TestCase("Pipfile", "ini")]
    [TestCase(".editorconfig", "ini")]
    [TestCase("LICENSE", "plaintext")]
    [TestCase(".gitignore", "plaintext")]
    [TestCase("README.txt", "plaintext")]
    [TestCase("nested/dir/Dockerfile", null)]
    [TestCase("image.bin", null)]
    [TestCase("", null)]
    [TestCase(null, null)]
    public void MapLanguagePinsFilesCommonInAurPackages(string? path, string? expected)
    {
        Assert.That(FileViewer.MapLanguage(path), Is.EqualTo(expected));
    }
}
