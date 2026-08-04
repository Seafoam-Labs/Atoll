namespace Atoll.Api.Services.Metrics;

public sealed class Metrics
{
    public long UptimeSeconds { get; set; }
    public long RequestCount { get; set; }
    public IndexSizes IndexSizes { get; set; } = new();
    public RefreshStatus Refresh { get; set; } = new();
    public BulkSeedStatus? BulkSeed { get; set; }
    public PackageRefreshStatus? PackageRefresh { get; set; }
    public SecurityScanStatus? SecurityScan { get; set; }
}

public sealed class IndexSizes
{
    public long ByNames { get; set; }
    public long ByProvides { get; set; }
    public long ByWords { get; set; }
}

public sealed class RefreshStatus
{
    public string? MetadataCollection { get; set; }
    public long IntervalSeconds { get; set; }
    public long Attempts { get; set; }
    public long Successes { get; set; }
    public long Failures { get; set; }
    public DateTimeOffset? LastStartedUtc { get; set; }
    public DateTimeOffset? LastSucceededUtc { get; set; }
    public DateTimeOffset? LastFailedUtc { get; set; }
}

public sealed class BulkSeedStatus
{
    public bool Enabled { get; set; }
    public long BatchesAttempted { get; set; }
    public long BatchesSucceeded { get; set; }
    public long BatchesFailed { get; set; }
    public long RefsSkipped { get; set; }
    public long RefsFailed { get; set; }
    public long PackagesSeeded { get; set; }
    public long PackagesSkipped { get; set; }
    public long PackagesExcluded { get; set; }
    public DateTimeOffset? LastStartedUtc { get; set; }
    public DateTimeOffset? LastFinishedUtc { get; set; }
}

public sealed class PackageRefreshStatus
{
    public bool Enabled { get; set; }
    public long CyclesAttempted { get; set; }
    public long CyclesSucceeded { get; set; }
    public long CyclesFailed { get; set; }
    public long CandidatePackages { get; set; }
    public long CandidatePackageBases { get; set; }
    public long PackagesUpdated { get; set; }
    public long PackagesUnchanged { get; set; }
    public long PackagesSkipped { get; set; }
    public long RefsSkipped { get; set; }
    public long RefsFailed { get; set; }
    public DateTimeOffset? LastStartedUtc { get; set; }
    public DateTimeOffset? LastFinishedUtc { get; set; }
}

public sealed class SecurityScanStatus
{
    public bool Enabled { get; set; }
    public long ScansCompleted { get; set; }
    public long ScansVerified { get; set; }
    public long ScansFlagged { get; set; }
    public long ScansErrored { get; set; }
    public long ScansDropped { get; set; }
    public long PendingScans { get; set; }
    public DateTimeOffset? LastScanFinishedUtc { get; set; }
}