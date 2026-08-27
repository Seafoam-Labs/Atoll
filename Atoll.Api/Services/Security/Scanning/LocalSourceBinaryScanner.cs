namespace Atoll.Api.Services.Security.Scanning;

internal static class LocalSourceBinaryScanner
{
    // Certificate/signature data has no reliable magic bytes, so these inert kinds are
    // recognized by extension. ELF still takes precedence and stays Critical regardless.
    private static readonly string[] CertificateExtensions = [".sig", ".asc", ".gpg", ".cer", ".crt", ".pem"];

    public static SecurityFinding? Scan(string content, string path)
    {
        var isElf = content.StartsWith("\u007fELF", StringComparison.Ordinal);
        if (!isElf && !HasBinaryCharacters(content))
            return null;

        if (isElf)
            return CreateFinding(SecurityFindingRules.LocalBinary, path,
                string.Format(SecurityFindingRules.LocalBinary.Description, path, "an ELF executable"));

        if (IsInertMedia(content) || IsCertificateOrSignature(path))
            return CreateFinding(SecurityFindingRules.LocalBinaryInertMedia, path,
                string.Format(SecurityFindingRules.LocalBinaryInertMedia.Description, path));

        if (!HasControlCharacters(content))
            return CreateFinding(SecurityFindingRules.LocalBinaryUnencodableText, path,
                string.Format(SecurityFindingRules.LocalBinaryUnencodableText.Description, path));

        return CreateFinding(SecurityFindingRules.LocalBinary, path,
            string.Format(SecurityFindingRules.LocalBinary.Description, path, "a binary"));
    }

    /// <summary>
    ///     Recognizes inert media formats by their magic bytes. Content reaches the scanner
    ///     UTF-8-decoded, so signatures are anchored on the parts that survive decoding:
    ///     undecodable lead bytes arrive as U+FFFD, low bytes (NUL, control, ASCII) verbatim.
    ///     Archives are deliberately absent - they can carry executables.
    /// </summary>
    private static bool IsInertMedia(string content)
    {
        if (content.Length < 4)
            return false;

        if (content.StartsWith("\uFFFDPNG\r\n\u001a\n", StringComparison.Ordinal) ||
            content.StartsWith("GIF87a", StringComparison.Ordinal) ||
            content.StartsWith("GIF89a", StringComparison.Ordinal) ||
            content.StartsWith("%PDF", StringComparison.Ordinal) ||
            content.StartsWith("OTTO", StringComparison.Ordinal) ||
            content.StartsWith("wOFF", StringComparison.Ordinal) ||
            content.StartsWith("wOF2", StringComparison.Ordinal) ||
            content.StartsWith("\0\0\u0001\0", StringComparison.Ordinal) ||
            content.StartsWith("\0\u0001\0\0", StringComparison.Ordinal))
            return true;

        // Tracker music modules and Allegro packed datafiles: audio/game asset containers
        // that cannot execute on their own. All three magics are pure ASCII.
        if (content.StartsWith("Extended Module: ", StringComparison.Ordinal) ||
            content.StartsWith("IMPM", StringComparison.Ordinal) ||
            content.StartsWith("slh!", StringComparison.Ordinal))
            return true;

        // JPEG: FF D8 FF decodes to three replacements; JFIF/Exif marker at offset 6.
        if (content.Length >= 10 &&
            content.StartsWith("\uFFFD\uFFFD\uFFFD", StringComparison.Ordinal) &&
            (content.AsSpan(6).StartsWith("JFIF") || content.AsSpan(6).StartsWith("Exif")))
            return true;

        // BMP: "BM" plus the reserved NUL pair near the header. The offset is loose because
        // the size field can contain multi-byte-valid sequences that merge during decoding.
        if (content.StartsWith("BM", StringComparison.Ordinal) &&
            content.Length >= 14 &&
            content.AsSpan(0, 14).Contains('\0'))
            return true;

        // WebP: "RIFF" ... "WEBP". The 4 size bytes decode to 1-4 characters, shifting the
        // "WEBP" offset, so it is located in a window instead of at a fixed position.
        if (content.StartsWith("RIFF", StringComparison.Ordinal))
        {
            var webp = content.IndexOf("WEBP", StringComparison.Ordinal);
            if (webp is >= 4 and <= 12)
                return true;
        }

        // S3M tracker module: "SCRM" signature at byte offset 44. Multi-byte sequences can
        // only merge during decoding, shifting the offset earlier, so the window is bounded.
        var s3m = content.IndexOf("SCRM", StringComparison.Ordinal);
        if (s3m is >= 0 and <= 44)
            return true;

        return false;
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

    private static bool HasBinaryCharacters(string content)
    {
        foreach (var character in content)
        {
            if (character is '\uFFFD' or '\0')
                return true;

            if (IsControlCharacter(character))
                return true;
        }

        return false;
    }

    /// <summary>True when the content contains NUL or control characters (U+FFFD alone does not count).</summary>
    private static bool HasControlCharacters(string content)
    {
        return content.Any(IsControlCharacter);
    }

    private static bool IsControlCharacter(char c)
    {
        return char.IsControl(c) && c is not ('\n' or '\r' or '\t' or '\v' or '\f');
    }
}