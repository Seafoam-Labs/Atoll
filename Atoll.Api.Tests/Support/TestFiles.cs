using System.Security.Cryptography;
using System.Text;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Support;

internal static class TestFiles
{
    /// <summary>
    ///     Converts a UTF-8 text fixture into the byte map the ingestion and service APIs take.
    ///     Binary fixtures pass byte arrays directly.
    /// </summary>
    internal static IReadOnlyDictionary<string, byte[]> AsByteFiles(
        this IReadOnlyDictionary<string, string> files)
    {
        var result = new Dictionary<string, byte[]>(files.Count, StringComparer.Ordinal);
        foreach (var (name, content) in files)
            result[name] = Encoding.UTF8.GetBytes(content);

        return result;
    }

    internal static IReadOnlyDictionary<string, byte[]> Text(
        params (string Name, string Content)[] files)
    {
        var result = new Dictionary<string, byte[]>(files.Length, StringComparer.Ordinal);
        foreach (var (name, content) in files)
            result[name] = Encoding.UTF8.GetBytes(content);

        return result;
    }
}

internal static class PackageFileFactory
{
    /// <summary>
    ///     Builds a stored file fixture. The default hash is the real SHA-256 of the content so
    ///     fixtures that compare or skip by hash behave like production documents; pass
    ///     <paramref name="hash" /> explicitly only when a test needs a synthetic identity.
    /// </summary>
    internal static PackageFile FromText(string content, string? hash = null, long? size = null)
    {
        return FromBytes(Encoding.UTF8.GetBytes(content), hash, size);
    }

    internal static PackageFile FromBytes(byte[] content, string? hash = null, long? size = null)
    {
        return new PackageFile
        {
            ContentBytes = content,
            Size = size ?? content.Length,
            Hash = hash ?? ContentHash(content)
        };
    }

    private static string ContentHash(byte[] content)
    {
        return $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";
    }
}
