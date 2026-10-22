using System.Text;
using System.Text.Unicode;

namespace Atoll.Api.Services.Packages;

/// <summary>
///     Text-facing decoding for stored package bytes. Strict on purpose: a file is text only when its
///     whole byte array is valid UTF-8, which is exactly the condition under which a JSON string
///     round-trips losslessly. Storage, hashing, and serving never use a lossy fallback.
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
}
