namespace Atoll.Api.Services.Security.Scanning;

using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;

internal static partial class LocalSourceBinaryScanner
{
    // Certificate/signature data has no reliable magic bytes, so these inert kinds are
    // recognized by extension. ELF still takes precedence and stays Critical regardless.
    private static readonly string[] CertificateExtensions = [".sig", ".asc", ".gpg", ".cer", ".crt", ".pem"];

    // Raw signatures. The scanner receives the original bytes, so the values are the
    // formats' actual magic rather than the U+FFFD-shadowed shapes a text decode leaves.
    private static ReadOnlySpan<byte> ElfMagic => [0x7F, 0x45, 0x4C, 0x46];
    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> IconMagic => [0x00, 0x00, 0x01, 0x00];
    private static ReadOnlySpan<byte> TrueTypeMagic => [0x00, 0x01, 0x00, 0x00];
    private static ReadOnlySpan<byte> GzipMagic => [0x1F, 0x8B];
    private static ReadOnlySpan<byte> SevenZipMagic => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static ReadOnlySpan<byte> XzMagic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private static ReadOnlySpan<byte> ZstdMagic => [0x28, 0xB5, 0x2F, 0xFD];

    // Shared libraries are named by the linker convention (*.so, *.so.N[.N...]). e_type
    // cannot distinguish them - PIE executables are ET_DYN like libraries - so the file
    // name is the signal.
    [GeneratedRegex(@"\.so(?:\.\d+)*$", RegexOptions.Compiled | RegexOptions.IgnoreCase, 250)]
    private static partial Regex SharedLibraryName { get; }

    /// <summary>
    ///     Classifies a local source file from two views of the same content: the raw bytes
    ///     for magic matching, and a scan-only decoded text for binary classification. The text
    ///     must come from <c>PackageText.DecodeForScanning</c>, so the classifier and the shell
    ///     rules in <see cref="PkgBuildSecurityScanner" /> agree on one view.
    /// </summary>
    public static SecurityFinding? Scan(ReadOnlySpan<byte> raw, string text, string path)
    {
        var hasBinaryCharacters = HasBinaryCharacters(text);
        var isElf = raw.StartsWith(ElfMagic);
        // "MZ" is ASCII and can open an ordinary text file, so it only counts as a PE
        // header when the decoded content is binary anyway.
        var isWindowsExecutable = hasBinaryCharacters && raw.StartsWith("MZ"u8);
        if (!isElf && !isWindowsExecutable && !hasBinaryCharacters)
            return null;

        if (isElf)
        {
            // A shared library is loaded by other programs as package content, the same
            // trust class as binaries inside a vendored archive, so it does not block.
            if (SharedLibraryName.IsMatch(path))
                return CreateFinding(SecurityFindingRules.LocalBinarySharedLibrary, path,
                    string.Format(CultureInfo.InvariantCulture, SecurityFindingRules.LocalBinarySharedLibrary.Description, path));

            return CreateFinding(SecurityFindingRules.LocalBinary, path,
                string.Format(CultureInfo.InvariantCulture, SecurityFindingRules.LocalBinary.Description, path, "an ELF executable"));
        }

        if (isWindowsExecutable)
            return CreateFinding(SecurityFindingRules.LocalBinary, path,
                string.Format(CultureInfo.InvariantCulture, SecurityFindingRules.LocalBinary.Description, path, "a Windows executable"));

        // Magic matching runs only after the text view classified the file as binary: an
        // all-ASCII PDF, GIF header, or tar header spelled in prose is text, not inert media.
        if (IsArchive(raw))
            return CreateFinding(SecurityFindingRules.LocalBinaryArchive, path,
                string.Format(CultureInfo.InvariantCulture, SecurityFindingRules.LocalBinaryArchive.Description, path));

        if (IsInertMedia(raw) || IsCertificateOrSignature(path))
            return CreateFinding(SecurityFindingRules.LocalBinaryInertMedia, path,
                string.Format(CultureInfo.InvariantCulture, SecurityFindingRules.LocalBinaryInertMedia.Description, path));

        if (!HasControlCharacters(text))
            return CreateFinding(SecurityFindingRules.LocalBinaryUnencodableText, path,
                string.Format(CultureInfo.InvariantCulture, SecurityFindingRules.LocalBinaryUnencodableText.Description, path));

        return CreateFinding(SecurityFindingRules.LocalBinaryData, path,
            string.Format(CultureInfo.InvariantCulture, SecurityFindingRules.LocalBinaryData.Description, path));
    }

    /// <summary>
    ///     Recognizes inert media formats by their raw magic bytes. Archives are recognized
    ///     separately by <see cref="IsArchive" />, and both are only consulted once the text
    ///     view has established that the file is binary.
    /// </summary>
    private static bool IsInertMedia(ReadOnlySpan<byte> raw)
    {
        if (raw.StartsWith(PngMagic) ||
            raw.StartsWith("GIF87a"u8) ||
            raw.StartsWith("GIF89a"u8) ||
            raw.StartsWith("%PDF"u8) ||
            raw.StartsWith("OTTO"u8) ||
            raw.StartsWith("wOFF"u8) ||
            raw.StartsWith("wOF2"u8) ||
            raw.StartsWith(IconMagic) ||
            raw.StartsWith(TrueTypeMagic) ||
            raw.StartsWith(JpegMagic))
            return true;

        // Tracker music modules and Allegro packed datafiles: audio/game asset containers
        // that cannot execute on their own.
        if (raw.StartsWith("Extended Module: "u8) ||
            raw.StartsWith("IMPM"u8) ||
            raw.StartsWith("slh!"u8))
            return true;

        // BMP: "BM" plus the four reserved header bytes.
        if (raw.StartsWith("BM"u8) &&
            raw.Length >= 14 &&
            raw[6] == 0 && raw[7] == 0 && raw[8] == 0 && raw[9] == 0)
            return true;

        // WebP: "RIFF", a four-byte size, then "WEBP".
        if (raw.StartsWith("RIFF"u8) && raw.Length >= 12 && raw[8..].StartsWith("WEBP"u8))
            return true;

        // S3M tracker module: "SCRM" signature at byte offset 44.
        return raw.Length >= 48 && raw[44..].StartsWith("SCRM"u8);
    }

    /// <summary>
    ///     Recognizes archive formats by their raw magic bytes so they can be downgraded to a
    ///     non-blocking finding: an archive cannot execute on its own and is versioned in the
    ///     repository, making it more auditable than an equivalent remote source URL.
    /// </summary>
    private static bool IsArchive(ReadOnlySpan<byte> raw)
    {
        if (raw.StartsWith(GzipMagic) ||
            raw.StartsWith("BZh"u8) || // bzip2
            raw.StartsWith("PK\x03\x04"u8) || // zip local header
            raw.StartsWith("PK\x05\x06"u8) || // zip end-of-central-directory
            raw.StartsWith("Rar!\x1A\x07"u8) || // RAR
            raw.StartsWith(SevenZipMagic) ||
            raw.StartsWith(XzMagic) ||
            raw.StartsWith(ZstdMagic))
            return true;

        // tar: "ustar" at byte offset 257, after the first 512-byte header block.
        return raw.Length >= 262 && raw[257..].StartsWith("ustar"u8);
    }

    private static bool IsCertificateOrSignature(string path)
    {
        return CertificateExtensions.Contains(
            Path.GetExtension(Path.GetFileName(path)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static SecurityFinding CreateFinding(SecurityFindingRule rule, string path, string message)
    {
        return new SecurityFinding(rule.Id, rule.Severity, message, path, path);
    }

    // Cc minus the five whitespace controls, which pass through: [0x00..0x08] +
    // [0x0E..0x1F] + [0x7F..0x9F]. The binary set adds U+FFFD, where the scan-only decode
    // lands undecodable bytes.
    private static readonly char[] ControlCharacterValues =
    [
        .. Enumerable.Range(0x00, 0x09).Select(i => (char)i),
        .. Enumerable.Range(0x0E, 0x20 - 0x0E).Select(i => (char)i),
        .. Enumerable.Range(0x7F, 0xA0 - 0x7F).Select(i => (char)i),
    ];

    private static readonly SearchValues<char> ControlCharacters =
        SearchValues.Create(ControlCharacterValues);

    private static readonly SearchValues<char> BinaryCharacters =
        SearchValues.Create([.. ControlCharacterValues, '\uFFFD']);

    private static bool HasBinaryCharacters(string text)
    {
        return text.AsSpan().ContainsAny(BinaryCharacters);
    }

    /// <summary>True when the content contains NUL or control characters (U+FFFD alone does not count).</summary>
    private static bool HasControlCharacters(string text)
    {
        return text.AsSpan().ContainsAny(ControlCharacters);
    }
}