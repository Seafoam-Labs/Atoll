using System.Text;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Scanning;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security.Scanning;

public class LocalSourceBinaryScannerTests
{
    private const string PngMagic = "\uFFFDPNG\r\n\u001a\n";

    private static string Bytes(params byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }

    [TestCase("icon.png")]
    [TestCase("archive/name.ico")]
    public void Png_magic_is_medium_regardless_of_path(string path)
    {
        var finding = LocalSourceBinaryScanner.Scan(PngMagic + "chunkdata", path);

        Assert.That(finding, Is.Not.Null);
        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.RuleId, Is.EqualTo("local-binary"));
    }

    [Test]
    public void Jpeg_with_jfif_and_exif_markers_is_medium()
    {
        var jfif = Bytes(0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00);
        var exif = Bytes(0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00);

        Assert.That(LocalSourceBinaryScanner.Scan(jfif, "photo.jpg")!.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(LocalSourceBinaryScanner.Scan(exif, "photo.jpeg")!.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [TestCase("GIF87a", Description = "GIF87a")]
    [TestCase("GIF89a", Description = "GIF89a")]
    [TestCase("%PDF", Description = "PDF")]
    [TestCase("OTTO", Description = "OpenType font")]
    [TestCase("wOFF", Description = "WOFF font")]
    [TestCase("wOF2", Description = "WOFF2 font")]
    [TestCase("Extended Module: ", Description = "FastTracker 2 module")]
    [TestCase("IMPM", Description = "Impulse Tracker module")]
    [TestCase("slh!", Description = "Allegro packed datafile")]
    public void Ascii_magic_formats_are_medium(string magic)
    {
        var finding = LocalSourceBinaryScanner.Scan(magic + "\0\0binarydata", "file.bin");

        Assert.That(finding, Is.Not.Null);
        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [Test]
    public void TrueType_and_ico_magic_with_nul_bytes_is_medium()
    {
        var ttf = Bytes(0x00, 0x01, 0x00, 0x00, 0x00, 0x0C);
        var ico = Bytes(0x00, 0x00, 0x01, 0x00, 0x01, 0x00);

        Assert.That(LocalSourceBinaryScanner.Scan(ttf, "font.ttf")!.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(LocalSourceBinaryScanner.Scan(ico, "icon.ico")!.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [Test]
    public void Bmp_magic_is_medium()
    {
        var bmp = Bytes(0x42, 0x4D, 0x36, 0x04, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x00, 0x28, 0x00);

        Assert.That(LocalSourceBinaryScanner.Scan(bmp, "image.bmp")!.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [Test]
    public void Webp_magic_is_medium_despite_variable_size_field_decoding()
    {
        var plainSize = "RIFF" + Bytes(0x24, 0x00, 0x00, 0x00) + "WEBP";
        var mergedSize = "RIFF" + Bytes(0x80) + "WEBP";

        Assert.That(LocalSourceBinaryScanner.Scan(plainSize, "image.webp")!.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(LocalSourceBinaryScanner.Scan(mergedSize, "image.webp")!.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [Test]
    public void S3m_signature_at_header_offset_is_medium()
    {
        // S3M modules carry the "SCRM" signature at byte offset 44, after the 28-byte title
        // and the header fields.
        var header = new byte[48];
        header[16] = 0x10;
        header[44] = (byte)'S';
        header[45] = (byte)'C';
        header[46] = (byte)'R';
        header[47] = (byte)'M';

        var finding = LocalSourceBinaryScanner.Scan(Bytes(header), "bgm.s3m");

        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [Test]
    public void S3m_signature_shifted_by_multibyte_decoding_is_medium()
    {
        // A valid two-byte sequence before the signature decodes to one character, so the
        // decoded offset lands below byte offset 44.
        var header = new byte[48];
        header[16] = 0x10;
        header[42] = 0xC3;
        header[43] = 0xA9;
        header[44] = (byte)'S';
        header[45] = (byte)'C';
        header[46] = (byte)'R';
        header[47] = (byte)'M';

        var finding = LocalSourceBinaryScanner.Scan(Bytes(header), "bgm.s3m");

        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [Test]
    public void Scrm_signature_beyond_the_header_window_stays_critical()
    {
        // Only the S3M header position counts; a match deeper in a binary does not clear it.
        var finding = LocalSourceBinaryScanner.Scan(new string('\0', 64) + "SCRM", "data.bin");

        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    [Test]
    public void Magic_bytes_win_over_a_suspicious_extension()
    {
        // Content-based detection: a real PNG named .exe is still inert media.
        var finding = LocalSourceBinaryScanner.Scan(PngMagic + "data", "installer.exe");

        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [Test]
    public void Elf_stays_critical_even_with_a_media_extension()
    {
        var elf = Bytes([0x7F, .. Encoding.UTF8.GetBytes("ELF payload")]);

        var finding = LocalSourceBinaryScanner.Scan(elf, "picture.png");

        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(finding.Message, Does.Contain("ELF executable"));
    }

    [Test]
    public void Executable_renamed_to_media_extension_stays_critical()
    {
        // No magic match: renaming does not defeat the check.
        var exe = "MZ" + Bytes(0x90, 0x00, 0x03);

        var finding = LocalSourceBinaryScanner.Scan(exe, "icon.png");

        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    [Test]
    public void Archive_magics_are_not_downgraded()
    {
        // Compressed streams carry NUL/control bytes after the magic, which keeps them
        // Critical even when the magic itself decodes without control characters.
        var gzip = Bytes(0x1F, 0x8B, 0x08, 0x00);
        var zip = Bytes((byte)'P', (byte)'K', 0x03, 0x04, 0x00);
        var zstd = Bytes(0x28, 0xB5, 0x2F, 0xFD, 0x04, 0x00, 0x91, 0x22);
        var xz = Bytes([0xFD, .. Encoding.UTF8.GetBytes("7zXZ"), 0x00]);

        // gzip would also match a renamed .svgz - it must stay blocking.
        Assert.That(LocalSourceBinaryScanner.Scan(gzip, "image.svgz")!.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(LocalSourceBinaryScanner.Scan(zip, "files.zip")!.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(LocalSourceBinaryScanner.Scan(zstd, "files.tar.zst")!.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(LocalSourceBinaryScanner.Scan(xz, "files.tar.xz")!.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    // ===== Certificates and signatures: extension-based Medium =====

    [TestCase("package.sig")]
    [TestCase("package.asc")]
    [TestCase("key.gpg")]
    [TestCase("cert.cer")]
    [TestCase("cert.CRT", Description = "case-insensitive extension")]
    [TestCase("chain.pem")]
    public void Certificate_and_signature_extensions_are_medium(string path)
    {
        var finding = LocalSourceBinaryScanner.Scan(Bytes(0x89, 0x02, 0x1D, 0x04) + "data", path);

        Assert.That(finding, Is.Not.Null);
        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [Test]
    public void Elf_content_with_signature_extension_stays_critical()
    {
        var elf = Bytes([0x7F, .. Encoding.UTF8.GetBytes("ELF payload")]);

        var finding = LocalSourceBinaryScanner.Scan(elf, "package.sig");

        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    [Test]
    public void Content_with_only_replacement_characters_is_medium()
    {
        // Legacy Latin-1/mojibake: undecodable bytes, but no NUL or control characters.
        var legacy = Bytes(0x50, 0x61, 0x82, 0x6B, 0x0A);

        var finding = LocalSourceBinaryScanner.Scan(legacy, "PKGBUILD");

        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("unrecognized encoding"));
    }

    [Test]
    public void Content_with_control_characters_stays_critical()
    {
        var finding = LocalSourceBinaryScanner.Scan("abc\0def", "data.bin");

        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(finding.Message, Does.Contain("a binary"));
    }

    [Test]
    public void Plain_text_has_no_finding()
    {
        Assert.That(LocalSourceBinaryScanner.Scan("pkgname=foo\npkgver=1.0\n", "PKGBUILD"), Is.Null);
    }

    [Test]
    public void Whitespace_control_characters_do_not_trigger_binary_detection()
    {
        Assert.That(LocalSourceBinaryScanner.Scan("line1\nline2\r\n\tindented\v\f", "script.sh"), Is.Null);
    }

    [Test]
    public void Finding_carries_path_as_snippet_and_file()
    {
        var finding = LocalSourceBinaryScanner.Scan("abc\0def", "subdir/data.bin");

        Assert.That(finding!.File, Is.EqualTo("subdir/data.bin"));
        Assert.That(finding.Snippet, Is.EqualTo("subdir/data.bin"));
    }
}