namespace Atoll.Api.Services.Search.Refresh;

public sealed record RefreshStatusSnapshot(
    string MetadataCollection,
    TimeSpan Interval,
    long Attempts,
    long Successes,
    long Failures,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastSucceededUtc,
    DateTimeOffset? LastFailedUtc);