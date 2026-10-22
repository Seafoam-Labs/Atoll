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
    internal static PackageFile FromText(string content, string hash = "h", long? size = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new PackageFile { ContentBytes = bytes, Size = size ?? bytes.Length, Hash = hash };
    }

    internal static PackageFile FromBytes(byte[] content, string hash = "h", long? size = null)
    {
        return new PackageFile { ContentBytes = content, Size = size ?? content.Length, Hash = hash };
    }
}