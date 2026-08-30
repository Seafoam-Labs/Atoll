namespace Atoll.Api.Services.Security;

public sealed record SecurityScanStatusSnapshot(
    bool Enabled,
    long ScansCompleted,
    long ScansVerified,
    long ScansFlagged,
    long ScansErrored,
    long ScansDropped,
    long PendingScans,
    DateTimeOffset? LastScanFinishedUtc);

public sealed class SecurityScanStatusStore(bool enabled)
{
    private long _lastScanFinishedTicks;
    private long _pendingScans;
    private long _scansCompleted;
    private long _scansDropped;
    private long _scansErrored;
    private long _scansFlagged;
    private long _scansVerified;

    public SecurityScanStatusSnapshot GetSnapshot()
    {
        return new SecurityScanStatusSnapshot(
            enabled,
            Interlocked.Read(ref _scansCompleted),
            Interlocked.Read(ref _scansVerified),
            Interlocked.Read(ref _scansFlagged),
            Interlocked.Read(ref _scansErrored),
            Interlocked.Read(ref _scansDropped),
            Interlocked.Read(ref _pendingScans),
            DecodeTimestamp(_lastScanFinishedTicks));
    }

    public void RecordScanCompleted(SecurityStatus status)
    {
        Interlocked.Increment(ref _scansCompleted);
        if (status == SecurityStatus.Verified)
            Interlocked.Increment(ref _scansVerified);
        else if (status == SecurityStatus.Flagged)
            Interlocked.Increment(ref _scansFlagged);

        Interlocked.Exchange(ref _lastScanFinishedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordScanErrored()
    {
        Interlocked.Increment(ref _scansCompleted);
        Interlocked.Increment(ref _scansErrored);
        Interlocked.Exchange(ref _lastScanFinishedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordScanDropped()
    {
        Interlocked.Increment(ref _scansDropped);
    }

    public void UpdatePendingScans(long count)
    {
        Interlocked.Exchange(ref _pendingScans, count);
    }

    private static DateTimeOffset? DecodeTimestamp(long ticks)
    {
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}