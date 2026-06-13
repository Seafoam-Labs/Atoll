namespace Atoll.Api.Services.Refresh;

public sealed record RefreshStatusSnapshot(
    string DataFile,
    TimeSpan Interval,
    long Attempts,
    long Successes,
    long Failures,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastSucceededUtc,
    DateTimeOffset? LastFailedUtc);