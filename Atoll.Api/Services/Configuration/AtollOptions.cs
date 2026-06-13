namespace Atoll.Api.Services.Configuration;

public sealed class AtollOptions
{
    public const string DataFileDefault = "packages-meta-ext-v1.json";
    public const string DataFileUrlDefault = "https://aur.archlinux.org/packages-meta-ext-v1.json.gz";

    public int Port { get; init; } = 8080;
    public string? Key { get; init; }
    public string? Cert { get; init; }
    public string DataFile { get; init; } = DataFileDefault;
    public string DataFileUrl { get; init; } = DataFileUrlDefault;
    public int RefreshIntervalHours { get; init; } = 1;
}