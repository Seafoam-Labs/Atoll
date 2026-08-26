namespace Atoll.Api.Services.Sync.Refresh;

public sealed record PackageRefreshStatusSnapshot(
    bool Enabled,
    long CyclesAttempted,
    long CyclesSucceeded,
    long CyclesFailed,
    long CandidatePackages,
    long CandidatePackageBases,
    long PackagesUpdated,
    long PackagesUnchanged,
    long PackagesSkipped,
    long RefsSkipped,
    long RefsFailed,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastFinishedUtc);

public sealed class RefreshStatusStore(bool enabled)
{
    private long _candidatePackageBases;
    private long _candidatePackages;
    private long _cyclesAttempted;
    private long _cyclesFailed;
    private long _cyclesSucceeded;
    private long _lastFinishedTicks;
    private long _lastStartedTicks;
    private long _packagesSkipped;
    private long _packagesUnchanged;
    private long _packagesUpdated;
    private long _refsFailed;
    private long _refsSkipped;

    public PackageRefreshStatusSnapshot GetSnapshot()
    {
        return new PackageRefreshStatusSnapshot(
            enabled,
            Interlocked.Read(ref _cyclesAttempted),
            Interlocked.Read(ref _cyclesSucceeded),
            Interlocked.Read(ref _cyclesFailed),
            Interlocked.Read(ref _candidatePackages),
            Interlocked.Read(ref _candidatePackageBases),
            Interlocked.Read(ref _packagesUpdated),
            Interlocked.Read(ref _packagesUnchanged),
            Interlocked.Read(ref _packagesSkipped),
            Interlocked.Read(ref _refsSkipped),
            Interlocked.Read(ref _refsFailed),
            DecodeTimestamp(_lastStartedTicks),
            DecodeTimestamp(_lastFinishedTicks));
    }

    public void BeginCycle()
    {
        Interlocked.Increment(ref _cyclesAttempted);
        Interlocked.Exchange(ref _lastStartedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void EndCycle()
    {
        Interlocked.Increment(ref _cyclesSucceeded);
        Interlocked.Exchange(ref _lastFinishedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordCycleFailed()
    {
        Interlocked.Increment(ref _cyclesFailed);
        Interlocked.Exchange(ref _lastFinishedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void AddCandidatePackages(long count)
    {
        Interlocked.Add(ref _candidatePackages, count);
    }

    public void AddCandidatePackageBases(long count)
    {
        Interlocked.Add(ref _candidatePackageBases, count);
    }

    public void AddPackagesUpdated(long count)
    {
        Interlocked.Add(ref _packagesUpdated, count);
    }

    public void AddPackagesUnchanged(long count)
    {
        Interlocked.Add(ref _packagesUnchanged, count);
    }

    public void AddPackagesSkipped(long count)
    {
        Interlocked.Add(ref _packagesSkipped, count);
    }

    public void AddRefsSkipped(long count)
    {
        Interlocked.Add(ref _refsSkipped, count);
    }

    public void AddRefsFailed(long count)
    {
        Interlocked.Add(ref _refsFailed, count);
    }

    private static DateTimeOffset? DecodeTimestamp(long ticks)
    {
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}