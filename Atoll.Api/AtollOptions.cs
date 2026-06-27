using System.ComponentModel.DataAnnotations;

namespace Atoll.Api;

public sealed class AtollOptions
{
    public DataSourceOptions DataSource { get; init; } = new();
    public StorageOptions Storage { get; init; } = new();
}

public sealed class DataSourceOptions
{
    [Required] public string DataFile { get; init; } = "packages-meta-ext-v1.json";

    [Required] [Url] public string DataFileUrl { get; init; } = "https://aur.archlinux.org/packages-meta-ext-v1.json.gz";

    [Range(1, 670)] public int RefreshIntervalMinutes { get; init; } = 10;
}

public sealed class StorageOptions
{
    public StorageType Type { get; init; } = StorageType.Local;
    public LocalStorageOptions Local { get; init; } = new();
    public S3StorageOptions S3 { get; init; } = new();
}

public sealed class LocalStorageOptions
{
    [Required] public string DataPath { get; init; } = "/data/aur";
}

public sealed class S3StorageOptions
{
    [Required] public string Bucket { get; init; } = "aur";

    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public bool ForcePathStyle { get; init; }

    public bool UseHttp { get; init; }

    public string Region { get; init; } = "us-east-1";
}

public enum StorageType
{
    Local,
    S3
}