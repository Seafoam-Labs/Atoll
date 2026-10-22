using System.Text;
using System.Text.Unicode;

namespace Atoll.Api.Services.Packages;

/// <summary>
///     Text-facing decoding for stored package bytes. <see cref="TryDecodeUtf8" /> is strict on
///     purpose: a file is text only when its whole byte array is valid UTF-8, which is exactly the
///     condition under which a JSON string round-trips losslessly. <see cref="DecodeForScanning" />
///     is the one deliberate lossy path, used only for pattern matching. Storage, hashing, and
///     serving never use a lossy fallback.
/// </summary>
internal static class PackageText
{
    public static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        if (!Utf8.IsValid(bytes))
        {
            text = string.Empty;
            return false;
        }

        text = Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>
    ///     Decodes bytes for pattern matching only. This deliberately matches the old
    ///     <see cref="StreamReader" /> ingestion semantics: a leading BOM is stripped, UTF-16/32
    ///     are decoded when a BOM marks them, and invalid UTF-8 falls back to U+FFFD so
    ///     shell/PKGBUILD rules keep scanning legacy encodings. The result is never stored,
    ///     served, or hashed.
    /// </summary>
    public static string DecodeForScanning(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
