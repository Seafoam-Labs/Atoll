using System.Security.Cryptography;
using System.Text;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Services.Packages;

internal sealed record PackageSnapshot(
    string RevisionId,
    DateTimeOffset CreatedAt,
    PackageRevisionContentDocument Content,
    PackageRevisionDocument Metadata);

/// <summary>
///     Builds the deterministic per-revision snapshot: validated file entries, the derived
///     revision id, and the persisted content/metadata documents. Revision ids hash the
///     package name with each file name and hash in ordinal order, so the insertion order of
///     the incoming files never affects the revision identity.
/// </summary>
internal static class PackageSnapshotFactory
{
    public static PackageSnapshot Create(
        string packageName,
        IReadOnlyDictionary<string, string> files,
        int maxFileBytes,
        string author,
        string message)
    {
        var packageFiles = BuildAndValidatePackageFiles(files, maxFileBytes);
        var revisionId = ComputeRevisionId(packageName, packageFiles);
        var createdAt = DateTimeOffset.UtcNow;

        return new PackageSnapshot(
            revisionId,
            createdAt,
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId(packageName, revisionId),
                PackageName = packageName,
                RevisionId = revisionId,
                CreatedAt = createdAt,
                Author = author,
                Message = message,
                Files = packageFiles
            },
            new PackageRevisionDocument
            {
                RevisionId = revisionId,
                CreatedAt = createdAt,
                Author = author,
                Message = message
            });
    }

    private static Dictionary<string, PackageFile> BuildAndValidatePackageFiles(
        IReadOnlyDictionary<string, string> files,
        int maxFileBytes)
    {
        var result = new Dictionary<string, PackageFile>(files.Count);

        foreach (var (name, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            if (bytes.Length > maxFileBytes)
                throw new InvalidOperationException(
                    $"File '{name}' is {bytes.Length} bytes which exceeds the per-file limit of {maxFileBytes} bytes.");

            var hash = SHA256.HashData(bytes);
            result[name] = new PackageFile
            {
                Content = content,
                Size = bytes.Length,
                Hash = $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"
            };
        }

        return result;
    }

    private static string ComputeRevisionId(
        string packageName,
        IReadOnlyDictionary<string, PackageFile> files)
    {
        var builder = new StringBuilder();
        builder.Append(packageName);

        foreach (var (name, file) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            builder.Append('\0').Append(name).Append('\0').Append(file.Hash);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}