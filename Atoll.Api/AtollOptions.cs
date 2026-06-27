namespace Atoll.Api;

public sealed class AtollOptions
{
    public string DataFile { get; init; } = "packages-meta-ext-v1.json";
    public string DataFileUrl { get; init; } = "https://aur.archlinux.org/packages-meta-ext-v1.json.gz";
    public int RefreshIntervalMinutes { get; init; } = 10;

    public string DataPath { get; init; } = "/data/aur";
    public StorageType StorageType { get; set; } = StorageType.Local;

    public string S3Bucket { get; init; } = "aur";
    public string S3AccessKey { get; init; } = "";
    public string S3SecretKey { get; init; } = "";
    public string S3Endpoint { get; init; } = "";
    public bool S3ForcePathStyle { get; init; } = false;
    public bool S3UseHttp { get; init; } = true;
    public string S3Region { get; init; } = "us-east-1";
}

public enum StorageType
{
    Local,
    S3
}