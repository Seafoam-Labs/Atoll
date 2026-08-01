namespace Atoll.Api.Services.Packages;

public sealed class PackageDocumentTooLargeException(
    string packageName,
    long serializedSizeBytes,
    long maxDocumentSizeBytes)
    : Exception(
        $"Package '{packageName}' serializes to {serializedSizeBytes} bytes, which exceeds MongoDB's {maxDocumentSizeBytes}-byte document limit.")
{
    public string PackageName { get; } = packageName;

    public long SerializedSizeBytes { get; } = serializedSizeBytes;

    public long MaxDocumentSizeBytes { get; } = maxDocumentSizeBytes;
}