namespace Atoll.Api.Services.Packages.Seed;

public sealed record BulkSeedStatusSnapshot(
    bool Enabled,
    long BatchesAttempted,
    long BatchesSucceeded,
    long BatchesFailed,
    long RefsSkipped,
    long RefsFailed,
    long PackagesSeeded,
    long PackagesSkipped,
    long PackagesExcluded,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastFinishedUtc);

public sealed class BulkSeedStatusStore(bool enabled)
{
    private long _batchesAttempted;
    private long _batchesFailed;
    private long _batchesSucceeded;
    private DateTimeOffset? _lastFinishedUtc;
    private DateTimeOffset? _lastStartedUtc;
    private long _packagesExcluded;
    private long _packagesSeeded;
    private long _packagesSkipped;
    private long _refsFailed;
    private long _refsSkipped;

    public BulkSeedStatusSnapshot GetSnapshot()
    {
        return new BulkSeedStatusSnapshot(
            enabled,
            Interlocked.Read(ref _batchesAttempted),
            Interlocked.Read(ref _batchesSucceeded),
            Interlocked.Read(ref _batchesFailed),
            Interlocked.Read(ref _refsSkipped),
            Interlocked.Read(ref _refsFailed),
            Interlocked.Read(ref _packagesSeeded),
            Interlocked.Read(ref _packagesSkipped),
            Interlocked.Read(ref _packagesExcluded),
            _lastStartedUtc,
            _lastFinishedUtc);
    }

    public void BeginCycle()
    {
        _lastStartedUtc = DateTimeOffset.UtcNow;
    }

    public void EndCycle()
    {
        _lastFinishedUtc = DateTimeOffset.UtcNow;
    }

    public void RecordBatchAttempted()
    {
        Interlocked.Increment(ref _batchesAttempted);
    }

    public void RecordBatchSucceeded()
    {
        Interlocked.Increment(ref _batchesSucceeded);
    }

    public void RecordBatchFailed()
    {
        Interlocked.Increment(ref _batchesFailed);
    }

    public void AddRefsSkipped(long count)
    {
        Interlocked.Add(ref _refsSkipped, count);
    }

    public void AddRefsFailed(long count)
    {
        Interlocked.Add(ref _refsFailed, count);
    }

    public void AddPackagesSeeded(long count)
    {
        Interlocked.Add(ref _packagesSeeded, count);
    }

    public void AddPackagesSkipped(long count)
    {
        Interlocked.Add(ref _packagesSkipped, count);
    }

    public void AddPackagesExcluded(long count)
    {
        Interlocked.Add(ref _packagesExcluded, count);
    }
}