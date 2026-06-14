namespace Atoll.Api;

public sealed class AtollOptions
{
    private const string DataFileDefault = "packages-meta-ext-v1.json";
    private const string DataFileUrlDefault = "https://aur.archlinux.org/packages-meta-ext-v1.json.gz";

    public string DataFile { get; init; } = DataFileDefault;
    public string DataFileUrl { get; init; } = DataFileUrlDefault;
    public int RefreshIntervalMinutes { get; init; } = 10;
}