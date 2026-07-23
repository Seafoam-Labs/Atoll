using System.ComponentModel.DataAnnotations;

namespace Atoll.Api;

public sealed class AtollOptions
{
    public DataSourceOptions DataSource { get; init; } = new();
    public MongoOptions Mongo { get; init; } = new();
    public GitOptions Git { get; init; } = new();
    public SeedOptions Seed { get; init; } = new();
}

public sealed class SeedOptions
{
    [Range(100, 60_000)] public int SeedDelayMs { get; init; } = 1000;
}

public sealed class GitOptions
{
    public string RepositoriesPath { get; init; } = "./data/repos";
}

public sealed class MongoOptions
{
    [Required] public string ConnectionString { get; init; } = "mongodb://localhost:27017";

    [Required] public string Database { get; init; } = "atoll";

    [Required] public string PackagesCollection { get; init; } = "packages";

    [Range(1, 200)] public int MaxRevisions { get; init; } = 10;

    [Range(1_024, 10_485_760)] public int MaxFileBytes { get; init; } = 5_242_880;
}

public sealed class DataSourceOptions
{
    [Required] public string DataFile { get; init; } = "packages-meta-ext-v1.json";

    [Required] [Url] public string DataFileUrl { get; init; } = "https://aur.archlinux.org/packages-meta-ext-v1.json.gz";

    [Range(1, 670)] public int RefreshIntervalMinutes { get; init; } = 10;
}