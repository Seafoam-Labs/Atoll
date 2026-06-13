using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atoll.Api.Services.Serialization;

[JsonSerializable(typeof(IReadOnlyList<JsonElement>))]
[JsonSerializable(typeof(MetricsResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AppJsonContext : JsonSerializerContext;

/// <summary>
///     Represents the response from the /metrics endpoint.
/// </summary>
public sealed class MetricsResponse
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
    public string? DataFile { get; set; }
    public long IntervalSeconds { get; set; }
    public long Attempts { get; set; }
    public long Successes { get; set; }
    public long Failures { get; set; }
    public DateTimeOffset? LastStartedUtc { get; set; }
    public DateTimeOffset? LastSucceededUtc { get; set; }
    public DateTimeOffset? LastFailedUtc { get; set; }
}