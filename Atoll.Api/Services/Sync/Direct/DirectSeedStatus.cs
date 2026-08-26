namespace Atoll.Api.Services.Sync.Direct;

public sealed record DirectSeedStatusSnapshot(
    bool Enabled,
    long CyclesStarted,
    long CyclesCompleted,
    long Candidates,
    long Seeded,
    long AlreadyPresent,
    long Failed,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastFinishedUtc);

/// <summary>
///     Counters for the direct seed worker. Direct is the default seed mode, so it gets the same
///     kind of status surface the bulk worker has had from the start; all members are safe to call
///     from the worker loop while the status dashboard reads a snapshot.
/// </summary>
public sealed class DirectSeedStatusStore(bool enabled)
{
    private long _alreadyPresent;
    private long _candidates;
    private long _cyclesCompleted;
    private long _cyclesStarted;
    private long _failed;
    private long _lastFinishedTicks;
    private long _lastStartedTicks;
    private long _seeded;

    public DirectSeedStatusSnapshot GetSnapshot()
    {
        return new DirectSeedStatusSnapshot(
            enabled,
            Interlocked.Read(ref _cyclesStarted),
            Interlocked.Read(ref _cyclesCompleted),
            Interlocked.Read(ref _candidates),
            Interlocked.Read(ref _seeded),
            Interlocked.Read(ref _alreadyPresent),
            Interlocked.Read(ref _failed),
            DecodeTimestamp(_lastStartedTicks),
            DecodeTimestamp(_lastFinishedTicks));
    }

    public void BeginCycle(long candidateCount)
    {
        Interlocked.Increment(ref _cyclesStarted);
        Interlocked.Add(ref _candidates, candidateCount);
        Interlocked.Exchange(ref _lastStartedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordSeeded()
    {
        Interlocked.Increment(ref _seeded);
    }

    public void RecordAlreadyPresent()
    {
        Interlocked.Increment(ref _alreadyPresent);
    }

    public void RecordFailed()
    {
        Interlocked.Increment(ref _failed);
    }

    public void EndCycle()
    {
        Interlocked.Increment(ref _cyclesCompleted);
        Interlocked.Exchange(ref _lastFinishedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private static DateTimeOffset? DecodeTimestamp(long ticks)
    {
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
