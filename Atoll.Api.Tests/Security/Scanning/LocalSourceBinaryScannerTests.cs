using System.Text;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Scanning;
using Xunit;

namespace Atoll.Api.Tests.Security.Scanning;

public class LocalSourceBinaryScannerTests
{
    private const string PngMagic = "\uFFFDPNG\r\n\u001a\n";

    private static string Bytes(params byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }

    [Theory]
    [InlineData("icon.png")]
    [InlineData("archive/name.ico")]
    public void Png_magic_is_medium_regardless_of_path(string path)
    {
        var finding = LocalSourceBinaryScanner.Scan(PngMagic + "chunkdata", path);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Equal("local-binary", finding.RuleId);
    }

    [Fact]
    public void Jpeg_with_jfif_and_exif_markers_is_medium()
    {
        var jfif = Bytes(0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00);
        var exif = Bytes(0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00);

        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(jfif, "photo.jpg")!.Severity);
        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(exif, "photo.jpeg")!.Severity);
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
    public void Ascii_magic_formats_are_medium(string magic)
    {
        var finding = LocalSourceBinaryScanner.Scan(magic + "\0\0binarydata", "file.bin");

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
    }

    [Fact]
    public void TrueType_and_ico_magic_with_nul_bytes_is_medium()
    {
        var ttf = Bytes(0x00, 0x01, 0x00, 0x00, 0x00, 0x0C);
        var ico = Bytes(0x00, 0x00, 0x01, 0x00, 0x01, 0x00);

        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(ttf, "font.ttf")!.Severity);
        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(ico, "icon.ico")!.Severity);
    }

    [Fact]
    public void Bmp_magic_is_medium()
    {
        var bmp = Bytes(0x42, 0x4D, 0x36, 0x04, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x00, 0x28, 0x00);

        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(bmp, "image.bmp")!.Severity);
    }

    [Fact]
    public void Webp_magic_is_medium_despite_variable_size_field_decoding()
    {
        var plainSize = "RIFF" + Bytes(0x24, 0x00, 0x00, 0x00) + "WEBP";
        var mergedSize = "RIFF" + Bytes(0x80) + "WEBP";

        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(plainSize, "image.webp")!.Severity);
        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(mergedSize, "image.webp")!.Severity);
    }

    [Fact]
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

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
    }

    [Fact]
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

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
    }

    [Fact]
    public void Scrm_signature_beyond_the_header_window_falls_back_to_medium()
    {
        // Only the S3M header position counts; a match deeper in a binary does not clear it,
        // but unrecognized binary data is non-blocking anyway.
        var finding = LocalSourceBinaryScanner.Scan(new string('\0', 64) + "SCRM", "data.bin");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary data", finding.Message);
    }

    [Fact]
    public void Magic_bytes_win_over_a_suspicious_extension()
    {
        // Content-based detection: a real PNG named .exe is still inert media.
        var finding = LocalSourceBinaryScanner.Scan(PngMagic + "data", "installer.exe");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
    }

    [Fact]
    public void Elf_stays_critical_even_with_a_media_extension()
    {
        var elf = Bytes([0x7F, .. Encoding.UTF8.GetBytes("ELF payload")]);

        var finding = LocalSourceBinaryScanner.Scan(elf, "picture.png");

        Assert.Equal(FindingSeverity.Critical, finding!.Severity);
        Assert.Contains("ELF executable", finding.Message);
    }

    [Theory]
    [InlineData("libqt5im-nimf.so")]
    [InlineData("libre2.so.5")]
    [InlineData("libgconf-2.so.4.1.5")]
    [InlineData("libpcre.so.3.13.2")]
    // path with directory
    [InlineData("subdir/libsteam_api.so")]
    public void Elf_named_as_a_shared_library_is_medium(string path)
    {
        // A shared library is loaded by other programs as package content - the same trust
        // class as binaries inside a vendored archive - so it is kept for review only.
        var elf = Bytes([0x7F, .. Encoding.UTF8.GetBytes("ELF payload")]);

        var finding = LocalSourceBinaryScanner.Scan(elf, path);

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("shared library", finding.Message);
    }

    [Theory]
    [InlineData("tool.so.txt")]
    [InlineData("libfoo.so.5a")]
    public void Elf_with_a_non_library_so_suffixed_name_stays_critical(string path)
    {
        // Only the linker naming convention counts; anything else keeps blocking severity.
        var elf = Bytes([0x7F, .. Encoding.UTF8.GetBytes("ELF payload")]);

        var finding = LocalSourceBinaryScanner.Scan(elf, path);

        Assert.Equal(FindingSeverity.Critical, finding!.Severity);
    }

    [Fact]
    public void Windows_executable_magic_is_critical()
    {
        var exe = "MZ" + Bytes(0x90, 0x00, 0x03);

        var finding = LocalSourceBinaryScanner.Scan(exe, "icon.png");

        Assert.Equal(FindingSeverity.Critical, finding!.Severity);
        Assert.Contains("Windows executable", finding.Message);
    }

    [Fact]
    public void Text_starting_with_mz_is_not_an_executable()
    {
        // "MZ" is ASCII, so it only counts as a PE header when the content is binary.
        Assert.Null(LocalSourceBinaryScanner.Scan("MZ initials in a comment\n", "notes.txt"));
    }

    [Fact]
    public void Archive_magics_are_medium_and_non_blocking()
    {
        // Archives cannot execute on their own and are versioned in the repository, so they
        // are retained for review without blocking - unlike a recognized executable format.
        var gzip = Bytes(0x1F, 0x8B, 0x08, 0x00);
        var zip = Bytes((byte)'P', (byte)'K', 0x03, 0x04, 0x00);
        var zstd = Bytes(0x28, 0xB5, 0x2F, 0xFD, 0x04, 0x00, 0x91, 0x22);
        var xz = Bytes([0xFD, .. Encoding.UTF8.GetBytes("7zXZ"), 0x00]);
        var bzip2 = Bytes((byte)'B', (byte)'Z', (byte)'h', 0x31) + "\0data";
        var sevenZip = "7z" + Bytes(0xBC, 0xAF, 0x27, 0x1C) + "\0data";
        var rar = "Rar!" + Bytes(0x1A, 0x07, 0x00) + "data";

        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(gzip, "image.svgz")!.Severity);
        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(zip, "files.zip")!.Severity);
        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(zstd, "files.tar.zst")!.Severity);
        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(xz, "files.tar.xz")!.Severity);
        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(bzip2, "files.tar.bz2")!.Severity);
        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(sevenZip, "files.7z")!.Severity);
        Assert.Equal(FindingSeverity.Medium, LocalSourceBinaryScanner.Scan(rar, "files.rar")!.Severity);
    }

    [Fact]
    public void Tar_magic_at_header_offset_is_medium()
    {
        // POSIX tar carries "ustar" at byte offset 257, after the first header block.
        var header = new byte[512];
        header[257] = (byte)'u';
        header[258] = (byte)'s';
        header[259] = (byte)'t';
        header[260] = (byte)'a';
        header[261] = (byte)'r';
        header[300] = 0x00;

        var finding = LocalSourceBinaryScanner.Scan(Bytes(header), "files.tar");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary archive", finding.Message);
    }

    [Fact]
    public void Archive_magic_wins_over_a_suspicious_extension()
    {
        var gzip = Bytes(0x1F, 0x8B, 0x08, 0x00);

        var finding = LocalSourceBinaryScanner.Scan(gzip, "payload.bin");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary archive", finding.Message);
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
    public void Certificate_and_signature_extensions_are_medium(string path)
    {
        var finding = LocalSourceBinaryScanner.Scan(Bytes(0x89, 0x02, 0x1D, 0x04) + "data", path);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
    }

    [Fact]
    public void Elf_content_with_signature_extension_stays_critical()
    {
        var elf = Bytes([0x7F, .. Encoding.UTF8.GetBytes("ELF payload")]);

        var finding = LocalSourceBinaryScanner.Scan(elf, "package.sig");

        Assert.Equal(FindingSeverity.Critical, finding!.Severity);
    }

    [Fact]
    public void Content_with_only_replacement_characters_is_medium()
    {
        // Legacy Latin-1/mojibake: undecodable bytes, but no NUL or control characters.
        var legacy = Bytes(0x50, 0x61, 0x82, 0x6B, 0x0A);

        var finding = LocalSourceBinaryScanner.Scan(legacy, "PKGBUILD");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("unrecognized encoding", finding.Message);
    }

    [Fact]
    public void Content_with_control_characters_is_medium_binary_data()
    {
        var finding = LocalSourceBinaryScanner.Scan("abc\0def", "data.bin");

        Assert.Equal(FindingSeverity.Medium, finding!.Severity);
        Assert.Contains("binary data", finding.Message);
    }

    [Fact]
    public void Plain_text_has_no_finding()
    {
        Assert.Null(LocalSourceBinaryScanner.Scan("pkgname=foo\npkgver=1.0\n", "PKGBUILD"));
    }

    [Fact]
    public void Whitespace_control_characters_do_not_trigger_binary_detection()
    {
        Assert.Null(LocalSourceBinaryScanner.Scan("line1\nline2\r\n\tindented\v\f", "script.sh"));
    }

    [Fact]
    public void Finding_carries_path_as_snippet_and_file()
    {
        var finding = LocalSourceBinaryScanner.Scan("abc\0def", "subdir/data.bin");

        Assert.Equal("subdir/data.bin", finding!.File);
        Assert.Equal("subdir/data.bin", finding.Snippet);
    }
}