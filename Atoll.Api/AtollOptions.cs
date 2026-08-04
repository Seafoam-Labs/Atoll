using System.ComponentModel.DataAnnotations;

namespace Atoll.Api;

public sealed class AtollOptions
{
    public DataSourceOptions DataSource { get; init; } = new();
    public MongoOptions Mongo { get; init; } = new();
    public GitOptions Git { get; init; } = new();
    public SeedOptions Seed { get; init; } = new();
    public RefreshOptions Refresh { get; init; } = new();
    public SecurityOptions Security { get; init; } = new();
}

public enum SeedMode
{
    Off,
    Direct,
    Bulk
}

public sealed class SeedOptions
{
    public SeedMode Mode { get; init; } = SeedMode.Direct;

    public DirectSeedOptions Direct { get; init; } = new();

    public BulkSeedOptions Bulk { get; init; } = new();
}

public sealed class DirectSeedOptions
{
    [Range(100, 60_000)] public int SeedDelayMs { get; init; } = 1000;
}

public sealed class BulkSeedOptions
{
    [Required] [Url] public string MirrorUrl { get; init; } = "https://github.com/archlinux/aur";

    [Required] public string CachePath { get; init; } = "./data/aur-mirror";

    [Range(10, 10_000)] public int BatchSize { get; init; } = 1000;

    [Range(100, 60_000)] public int BatchDelayMs { get; init; } = 1000;

    [Range(1, 128)] public int Parallelism { get; init; } = 4;

    public bool AurFallbackForNotOnMirror { get; init; } = false;
}

public sealed class GitOptions
{
    public string RepositoriesPath { get; init; } = "./data/repos";
}

public sealed class MongoOptions
{
    [Required] public string ConnectionString { get; init; } = "mongodb://localhost:27017";

    [Required] public string Database { get; init; } = "atoll";

    public MongoCollections Collections { get; init; } = new();

    [Range(1, 200)] public int MaxRevisions { get; init; } = 10;

    [Range(1_024, 10_485_760)] public int MaxFileBytes { get; init; } = 5_242_880;
}

public sealed class MongoCollections
{
    [Required] public string Packages { get; init; } = "packages";

    [Required] public string AurMetadata { get; init; } = "aur-metadata";

    [Required] public string SeedExclusions { get; init; } = "seed-exclusions";

    [Required] public string PackageSecurityScans { get; init; } = "package-security-scans";
}

public sealed class DataSourceOptions
{
    [Required] [Url] public string DataFileUrl { get; init; } = "https://aur.archlinux.org/packages-meta-ext-v1.json.gz";

    [Range(1, 670)] public int RefreshIntervalMinutes { get; init; } = 10;
}

public sealed class RefreshOptions
{
    public bool Enabled { get; init; } = false;

    [Range(10, 10_000)] public int BatchSize { get; init; } = 1000;

    [Range(100, 60_000)] public int BatchDelayMs { get; init; } = 1000;

    [Range(1, 128)] public int Parallelism { get; init; } = 4;

    [Range(1, 500_000)] public int MaxPackagesPerRun { get; init; } = 10_000;

    [Range(1, 720)] public int MaxStalenessHours { get; init; } = 24;

    [Required] [Url] public string MirrorUrl { get; init; } = "https://github.com/archlinux/aur";

    [Required] public string CachePath { get; init; } = "./data/aur-mirror";
}

public sealed class SecurityOptions
{
    public bool Enabled { get; init; } = true;

    [Range(1, 64)] public int ScannerConcurrency { get; init; } = 4;

    [Range(100, 300_000)] public int PollIntervalMs { get; init; } = 100;
}