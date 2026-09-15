using Atoll.Api.Components;
using Xunit;

namespace Atoll.Api.Tests.Ui;

public class FileViewerTests
{
    [Theory]
    [InlineData("PKGBUILD", "pkgbuild")]
    [InlineData("post-install.install", "bash")]
    [InlineData(".SRCINFO", "ini")]
    [InlineData("Makefile", "makefile")]
    [InlineData("src/main.py", "python")]
    [InlineData("fixes.patch", "diff")]
    [InlineData("org.example.desktop", "ini")]
    [InlineData("20-atool.hook", "ini")]
    [InlineData("99-udev.rules", "ini")]
    [InlineData("Pipfile", "ini")]
    [InlineData(".editorconfig", "ini")]
    [InlineData("LICENSE", "plaintext")]
    [InlineData(".gitignore", "plaintext")]
    [InlineData("README.txt", "plaintext")]
    [InlineData("nested/dir/Dockerfile", null)]
    [InlineData("image.bin", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MapLanguagePinsFilesCommonInAurPackages(string? path, string? expected)
    {
        Assert.Equal(expected, FileViewer.MapLanguage(path));
    }
}
