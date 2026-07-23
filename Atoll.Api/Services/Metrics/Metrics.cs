namespace Atoll.Api.Services.Metrics;

/// <summary>
///     Represents the Atoll Search API metrics.
/// </summary>
public sealed class Metrics
{
    public long UptimeSeconds { get; set; }
    public long RequestCount { get; set; }
    public IndexSizes IndexSizes { get; set; } = new();
    public RefreshStatus Refresh { get; set; } = new();
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