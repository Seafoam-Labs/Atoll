using Atoll.Api.Services.Sync.Direct;
using Xunit;

namespace Atoll.Api.Tests.Sync.Direct;

public class DirectSeedStatusStoreTests
{
    [Fact]
    public void DisabledSnapshotKeepsEnabledFalseAndZeroCounters()
    {
        var store = new DirectSeedStatusStore(enabled: false);
        var snapshot = store.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.False(snapshot.Enabled);
            Assert.Equal(0, snapshot.CyclesStarted);
            Assert.Equal(0, snapshot.CyclesCompleted);
            Assert.Equal(0, snapshot.Candidates);
            Assert.Equal(0, snapshot.Seeded);
            Assert.Equal(0, snapshot.AlreadyPresent);
            Assert.Equal(0, snapshot.Failed);
            Assert.Null(snapshot.LastStartedUtc);
            Assert.Null(snapshot.LastFinishedUtc);
        });
    }

    [Fact]
    public void CycleRecordingUpdatesCountersAndTimestamps()
    {
        var store = new DirectSeedStatusStore(enabled: true);

        store.BeginCycle(7);
        var started = store.GetSnapshot();

        store.RecordSeeded();
        store.RecordSeeded();
        store.RecordAlreadyPresent();
        store.RecordFailed();
        store.EndCycle();
        var finished = store.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.True(started.Enabled);
            Assert.Equal(1, started.CyclesStarted);
            Assert.NotNull(started.LastStartedUtc);

            Assert.Equal(1, finished.CyclesStarted);
            Assert.Equal(1, finished.CyclesCompleted);
            Assert.Equal(7, finished.Candidates);
            Assert.Equal(2, finished.Seeded);
            Assert.Equal(1, finished.AlreadyPresent);
            Assert.Equal(1, finished.Failed);
            Assert.NotNull(finished.LastFinishedUtc);
            Assert.True(finished.LastFinishedUtc!.Value >= started.LastStartedUtc!.Value);
        });
    }

    [Fact]
    public async Task ConcurrentCounterUpdatesAreNotLost()
    {
        var store = new DirectSeedStatusStore(enabled: true);
        store.BeginCycle(1000);

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
                store.RecordSeeded();
        }, TestContext.Current.CancellationToken));

        await Task.WhenAll(tasks);
        store.EndCycle();

        var snapshot = store.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.Equal(1000, snapshot.Candidates);
            Assert.Equal(4000, snapshot.Seeded);
            Assert.Equal(1, snapshot.CyclesCompleted);
        });
    }
}
