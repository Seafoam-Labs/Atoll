using System.Globalization;

namespace Atoll.Api.Services.Packages;

public sealed class PackageDocumentTooLargeException(
    string packageName,
    long serializedSizeBytes,
    long maxDocumentSizeBytes)
    : Exception(
        $"Package '{packageName}' serializes to {serializedSizeBytes.ToString(CultureInfo.InvariantCulture)} bytes, " +
        $"which exceeds MongoDB's {maxDocumentSizeBytes.ToString(CultureInfo.InvariantCulture)}-byte document limit.")
{
    public string PackageName { get; } = packageName;

    public long SerializedSizeBytes { get; } = serializedSizeBytes;

    public long MaxDocumentSizeBytes { get; } = maxDocumentSizeBytes;
}