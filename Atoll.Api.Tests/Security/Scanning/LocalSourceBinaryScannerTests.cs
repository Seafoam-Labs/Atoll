using System.Text;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Scanning;
using Xunit;

namespace Atoll.Api.Tests.Security.Scanning;

public class LocalSourceBinaryScannerTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static SecurityFinding? Scan(byte[] raw, string path)
    {
        return LocalSourceBinaryScanner.Scan(raw, PackageText.DecodeForScanning(raw), path);
    }

    private static SecurityFinding? Scan(string text, string path)
    {
        return Scan(Encoding.UTF8.GetBytes(text), path);
    }

    private static byte[] Utf8(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    private static byte[] Elf(string payload = "ELF payload")
    {
        return [0x7F, 0x45, 0x4C, 0x46, .. Utf8(payload)];
    }

    [Theory]
    [InlineData("icon.png")]
    [InlineData("archive/name.ico")]
    public void Scan_RawPngMagic_MediumRegardlessOfPath(string path)
    {
        // The upstream bytes, not the U+FFFD-shadowed text the old decoder produced.
        byte[] png = [.. PngMagic, .. Utf8("chunkdata")];

        var finding = Scan(png, path);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Equal("local-binary", finding.RuleId);
    }

    [Fact]
    public void Scan_RawJpegWithJfifAndExifMarkers_Medium()
    {
        byte[] jfif = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. Utf8("JFIF"), 0x00];
        byte[] exif = [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, .. Utf8("Exif"), 0x00];

        Assert.Equal(FindingSeverity.Medium, Scan(jfif, "photo.jpg")!.Severity);
        Assert.Equal(FindingSeverity.Medium, Scan(exif, "photo.jpeg")!.Severity);
    }

    [Theory]
    // GIF87a
    [InlineData("GIF87a")]
    // GIF89a
    [InlineData("GIF89a")]
    // PDF
    [InlineData("%PDF")]
    // OpenType font
    [InlineData("OTTO")]
    // WOFF font
    [InlineData("wOFF")]
    // WOFF2 font
    [InlineData("wOF2")]
    // FastTracker 2 module
    [InlineData("Extended Module: ")]
    // Impulse Tracker module
    [InlineData("IMPM")]
    // Allegro packed datafile
    [InlineData("slh!")]
    public void Scan_KnownAsciiMagicWithBinaryTail_Medium(string magic)
    {
        byte[] raw = [.. Utf8(magic), 0x00, 0x00, .. Utf8("binarydata")];

        var finding = Scan(raw, "file.bin");

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
    }

    [Fact]
    public void Scan_TrueTypeAndIcoMagicWithNulBytes_Medium()
    {
        byte[] ttf = [0x00, 0x01, 0x00, 0x00, 0x00, 0x0C];
        byte[] ico = [0x00, 0x00, 0x01, 0x00, 0x01, 0x00];

        Assert.Equal(FindingSeverity.Medium, Scan(ttf, "font.ttf")!.Severity);
        Assert.Equal(FindingSeverity.Medium, Scan(ico, "icon.ico")!.Severity);
    }

    [Fact]
    public void Scan_RawBmpMagic_Medium()
    {
        byte[] bmp = [0x42, 0x4D, 0x36, 0x04, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x00, 0x28, 0x00];

        Assert.Equal(FindingSeverity.Medium, Scan(bmp, "image.bmp")!.Severity);
    }

    [Fact]
    public void Scan_RawWebpMagic_Medium()
    {
        byte[] webp = [.. Utf8("RIFF"), 0x24, 0x00, 0x00, 0x00, .. Utf8("WEBP")];

        Assert.Equal(FindingSeverity.Medium, Scan(webp, "image.webp")!.Severity);
    }

    [Fact]
    public void Scan_RiffWithoutWebpSignature_FallsBackToBinaryData()
    {
        byte[] riff = [.. Utf8("RIFF"), 0x24, 0x00, 0x00, 0x00, .. Utf8("WAVE")];

        var finding = Scan(riff, "sound.wav");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary data", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_S3mSignatureAtHeaderOffset_Medium()
    {
        // S3M modules carry the "SCRM" signature at byte offset 44, after the 28-byte title
        // and the header fields.
        var header = new byte[48];
        header[16] = 0x10;
        header[44] = (byte)'S';
        header[45] = (byte)'C';
        header[46] = (byte)'R';
        header[47] = (byte)'M';

        var finding = Scan(header, "bgm.s3m");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
    }

    [Fact]
    public void Scan_S3mSignatureOffHeaderOffset_FallsBackToBinaryData()
    {
        // Raw bytes are authoritative, so only the exact offset counts.
        var header = new byte[48];
        header[16] = 0x10;
        header[43] = (byte)'S';
        header[44] = (byte)'C';
        header[45] = (byte)'R';
        header[46] = (byte)'M';

        var finding = Scan(header, "bgm.s3m");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary data", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_ScrmSignatureBeyondHeaderWindow_FallsBackToMedium()
    {
        // Only the S3M header position counts; a match deeper in a binary does not clear it,
        // but unrecognized binary data is non-blocking anyway.
        var raw = new byte[68];
        Utf8("SCRM").CopyTo(raw, 64);

        var finding = Scan(raw, "data.bin");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary data", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_PngMagicNamedExe_MagicWinsOverExtension()
    {
        // Content-based detection: a real PNG named .exe is still inert media.
        byte[] png = [.. PngMagic, .. Utf8("data")];

        var finding = Scan(png, "installer.exe");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
    }

    [Fact]
    public void Scan_ElfWithMediaExtension_StaysCritical()
    {
        var finding = Scan(Elf(), "picture.png");

        Assert.Equal(FindingSeverity.Critical, finding!.Severity);
        Assert.Contains("ELF executable", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("libqt5im-nimf.so")]
    [InlineData("libre2.so.5")]
    [InlineData("libgconf-2.so.4.1.5")]
    [InlineData("libpcre.so.3.13.2")]
    // path with directory
    [InlineData("subdir/libsteam_api.so")]
    public void Scan_ElfNamedAsSharedLibrary_Medium(string path)
    {
        // A shared library is loaded by other programs as package content - the same trust
        // class as binaries inside a vendored archive - so it is kept for review only.
        var finding = Scan(Elf(), path);

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("shared library", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tool.so.txt")]
    [InlineData("libfoo.so.5a")]
    public void Scan_ElfWithNonLibrarySoSuffix_StaysCritical(string path)
    {
        // Only the linker naming convention counts; anything else keeps blocking severity.
        var finding = Scan(Elf(), path);

        Assert.Equal(FindingSeverity.Critical, finding!.Severity);
    }

    [Fact]
    public void Scan_WindowsExecutableMagic_Critical()
    {
        byte[] exe = [0x4D, 0x5A, 0x90, 0x00, 0x03];

        var finding = Scan(exe, "icon.png");

        Assert.Equal(FindingSeverity.Critical, finding!.Severity);
        Assert.Contains("Windows executable", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_TextStartingWithMz_NotExecutable()
    {
        // "MZ" is ASCII, so it only counts as a PE header when the content is binary.
        Assert.Null(Scan("MZ initials in a comment\n", "notes.txt"));
    }

    [Fact]
    public void Scan_KnownArchiveMagics_Medium()
    {
        // Archives cannot execute on their own and are versioned in the repository, so they
        // are retained for review without blocking - unlike a recognized executable format.
        byte[] gzip = [0x1F, 0x8B, 0x08, 0x00];
        byte[] zip = [0x50, 0x4B, 0x03, 0x04, 0x00];
        byte[] zstd = [0x28, 0xB5, 0x2F, 0xFD, 0x04, 0x00, 0x91, 0x22];
        byte[] xz = [0xFD, .. Utf8("7zXZ"), 0x00];
        byte[] bzip2 = [.. Utf8("BZh"), 0x31, 0x00, .. Utf8("data")];
        byte[] sevenZip = [.. Utf8("7z"), 0xBC, 0xAF, 0x27, 0x1C, 0x00, .. Utf8("data")];
        byte[] rar = [.. Utf8("Rar!"), 0x1A, 0x07, 0x00, .. Utf8("data")];

        Assert.Equal(FindingSeverity.Medium, Scan(gzip, "image.svgz")!.Severity);
        Assert.Equal(FindingSeverity.Medium, Scan(zip, "files.zip")!.Severity);
        Assert.Equal(FindingSeverity.Medium, Scan(zstd, "files.tar.zst")!.Severity);
        Assert.Equal(FindingSeverity.Medium, Scan(xz, "files.tar.xz")!.Severity);
        Assert.Equal(FindingSeverity.Medium, Scan(bzip2, "files.tar.bz2")!.Severity);
        Assert.Equal(FindingSeverity.Medium, Scan(sevenZip, "files.7z")!.Severity);
        Assert.Equal(FindingSeverity.Medium, Scan(rar, "files.rar")!.Severity);
    }

    [Fact]
    public void Scan_TarMagicAtHeaderOffset_Medium()
    {
        // POSIX tar carries "ustar" at byte offset 257, after the first header block.
        var header = new byte[512];
        Utf8("ustar").CopyTo(header, 257);

        var finding = Scan(header, "files.tar");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary archive", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_GzipMagicNamedBin_ArchiveWinsOverExtension()
    {
        byte[] gzip = [0x1F, 0x8B, 0x08, 0x00];

        var finding = Scan(gzip, "payload.bin");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary archive", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A valid UTF-8 text file that happens to spell an inert-media magic is text, not media:
    // magic matching only runs after the text view classifies the file as binary.
    [InlineData("GIF89a is a header name in this note\n")]
    [InlineData("BMW owners write notes here\n")]
    [InlineData("%PDF appears in this documentation\n")]
    [InlineData("RIFFxxxxWEBP is not a real image\n")]
    [InlineData("ustar is mentioned in this prose\n")]
    public void Scan_AsciiMagicInValidText_NotClassifiedAsBinary(string text)
    {
        Assert.Null(Scan(text, "notes.txt"));
    }

    [Fact]
    public void Scan_TarMagicAtHeaderOffsetInValidText_NotArchive()
    {
        // The binary gate keeps a prose file with 257 leading characters and the word
        // "ustar" at the tar signature offset from being classified as an archive.
        var text = new string('a', 257) + "ustar and more prose\n";

        Assert.Null(Scan(text, "notes.txt"));
    }

    [Fact]
    public void Scan_BomMarkedUtf16Text_UsesBomAwareScanView()
    {
        // The scan-only view preserves the old StreamReader behavior: a BOM-marked UTF-16
        // file decodes to clean text, so it is neither binary nor inert media.
        byte[] raw = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("pkgname=foo\npkgver=1.0\n")];

        Assert.Null(Scan(raw, "PKGBUILD"));
    }

    [Fact]
    public void Scan_Utf8BomText_NotBinary()
    {
        byte[] raw = [0xEF, 0xBB, 0xBF, .. Utf8("pkgname=foo\n")];

        Assert.Null(Scan(raw, "PKGBUILD"));
    }

    // ===== Certificates and signatures: extension-based Medium =====

    [Theory]
    [InlineData("package.sig")]
    [InlineData("package.asc")]
    [InlineData("key.gpg")]
    [InlineData("cert.cer")]
    // case-insensitive extension
    [InlineData("cert.CRT")]
    [InlineData("chain.pem")]
    public void Scan_CertificateAndSignatureExtensions_Medium(string path)
    {
        byte[] raw = [0x89, 0x02, 0x1D, 0x04, .. Utf8("data")];

        var finding = Scan(raw, path);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
    }

    [Fact]
    public void Scan_ElfContentWithSignatureExtension_StaysCritical()
    {
        var finding = Scan(Elf(), "package.sig");

        Assert.Equal(FindingSeverity.Critical, finding!.Severity);
    }

    [Fact]
    public void Scan_OnlyReplacementCharacters_Medium()
    {
        // Legacy Latin-1/mojibake: undecodable bytes, but no NUL or control characters.
        byte[] legacy = [0x50, 0x61, 0x82, 0x6B, 0x0A];

        var finding = Scan(legacy, "PKGBUILD");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("unrecognized encoding", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_ControlCharacters_MediumBinaryData()
    {
        var finding = Scan("abc\0def", "data.bin");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary data", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // C0 start of heading
    [InlineData("\u0001")]
    // C0 unit separator
    [InlineData("\u001F")]
    // DEL
    [InlineData("\u007F")]
    // C1 NEL
    [InlineData("\u0085")]
    // C1 CSI
    [InlineData("\u009B")]
    public void Scan_NonWhitespaceControls_MediumBinaryData(string control)
    {
        var finding = Scan("abc" + control + "def", "data.bin");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary data", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_PlainText_NoFinding()
    {
        Assert.Null(Scan("pkgname=foo\npkgver=1.0\n", "PKGBUILD"));
    }

    [Fact]
    public void Scan_WhitespaceControlCharacters_NotBinary()
    {
        Assert.Null(Scan("line1\nline2\r\n\tindented\v\f", "script.sh"));
    }

    [Fact]
    public void Scan_AnyFinding_UsesPathAsSnippetAndFile()
    {
        var finding = Scan("abc\0def", "subdir/data.bin");

        Assert.Equal("subdir/data.bin", finding!.File);
        Assert.Equal("subdir/data.bin", finding.Snippet);
    }
}