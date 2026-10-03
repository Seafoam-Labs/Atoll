namespace Atoll.Api.Services.Catalog.Refresh;

/// <summary>
///     <see cref="Failures" /> is cumulative since the process started; <see cref="ConsecutiveFailures" />
///     counts failed cycles since the last one that completed without throwing (a 304 cycle included).
/// </summary>
public sealed record RefreshStatusSnapshot(
    string MetadataCollection,
    TimeSpan Interval,
    long Attempts,
    long Successes,
    long Failures,
    long ConsecutiveFailures,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastSucceededUtc,
    DateTimeOffset? LastFailedUtc,
    DateTimeOffset? LastLoadedFromCacheUtc);