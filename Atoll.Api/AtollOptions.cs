namespace Atoll.Api;

public sealed class AtollOptions
{
    public string DataFile { get; init; } = "packages-meta-ext-v1.json";
    public string DataFileUrl { get; init; } = "https://aur.archlinux.org/packages-meta-ext-v1.json.gz";
    public int RefreshIntervalMinutes { get; init; } = 10;

    public string S3Bucket { get; init; } = "aur";
    public string DataPath { get; init; } = "/data/aur";
}