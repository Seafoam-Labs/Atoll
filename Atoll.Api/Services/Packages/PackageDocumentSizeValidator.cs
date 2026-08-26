using System.Text;
using MongoDB.Bson;

namespace Atoll.Api.Services.Packages;

/// <summary>
///     Guards revision content documents against MongoDB's 16 MiB BSON limit. A conservative
///     estimate runs first so typical documents never serialize; only documents whose estimate
///     crosses the limit pay for the exact <c>ToBson()</c> measurement.
/// </summary>
internal static class PackageDocumentSizeValidator
{
    public const long MongoMaxDocumentSizeBytes = 16 * 1024 * 1024;

    // Per-file BSON overhead: hash string, size field, element names, and framing.
    private const int FileEntryOverheadBytes = 160;

    // Revision-document-level overhead: identifiers, timestamps, and revision metadata.
    private const int DocumentOverheadBytes = 1024;

    public static void Validate(string packageName, PackageRevisionContentDocument revision)
    {
        if (EstimateSerializedSizeBound(revision.Files) <= MongoMaxDocumentSizeBytes)
            return;

        var serializedSizeBytes = revision.ToBson().LongLength;
        if (serializedSizeBytes > MongoMaxDocumentSizeBytes)
            throw new PackageDocumentTooLargeException(packageName, serializedSizeBytes, MongoMaxDocumentSizeBytes);
    }

    private static long EstimateSerializedSizeBound(IReadOnlyDictionary<string, PackageFile> files)
    {
        long contentBytes = 0;
        foreach (var (name, file) in files)
            contentBytes += file.Size + Encoding.UTF8.GetByteCount(name) + FileEntryOverheadBytes;

        return contentBytes + DocumentOverheadBytes;
    }
}