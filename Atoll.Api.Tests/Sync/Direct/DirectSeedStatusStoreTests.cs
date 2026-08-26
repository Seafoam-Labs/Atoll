using Atoll.Api.Services.Sync.Direct;
using NUnit.Framework;

namespace Atoll.Api.Tests.Sync.Direct;

public class DirectSeedStatusStoreTests
{
    [Test]
    public void DisabledSnapshotKeepsEnabledFalseAndZeroCounters()
    {
        var store = new DirectSeedStatusStore(enabled: false);
        var snapshot = store.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Enabled, Is.False);
            Assert.That(snapshot.CyclesStarted, Is.Zero);
            Assert.That(snapshot.CyclesCompleted, Is.Zero);
            Assert.That(snapshot.Candidates, Is.Zero);
            Assert.That(snapshot.Seeded, Is.Zero);
            Assert.That(snapshot.AlreadyPresent, Is.Zero);
            Assert.That(snapshot.Failed, Is.Zero);
            Assert.That(snapshot.LastStartedUtc, Is.Null);
            Assert.That(snapshot.LastFinishedUtc, Is.Null);
        });
    }

    [Test]
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
            Assert.That(started.Enabled, Is.True);
            Assert.That(started.CyclesStarted, Is.EqualTo(1));
            Assert.That(started.LastStartedUtc, Is.Not.Null);

            Assert.That(finished.CyclesStarted, Is.EqualTo(1));
            Assert.That(finished.CyclesCompleted, Is.EqualTo(1));
            Assert.That(finished.Candidates, Is.EqualTo(7));
            Assert.That(finished.Seeded, Is.EqualTo(2));
            Assert.That(finished.AlreadyPresent, Is.EqualTo(1));
            Assert.That(finished.Failed, Is.EqualTo(1));
            Assert.That(finished.LastFinishedUtc, Is.Not.Null);
            Assert.That(finished.LastFinishedUtc!.Value, Is.GreaterThanOrEqualTo(started.LastStartedUtc!.Value));
        });
    }

    [Test]
    public async Task ConcurrentCounterUpdatesAreNotLost()
    {
        var store = new DirectSeedStatusStore(enabled: true);
        store.BeginCycle(1000);

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
                store.RecordSeeded();
        }));

        await Task.WhenAll(tasks);
        store.EndCycle();

        var snapshot = store.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Candidates, Is.EqualTo(1000));
            Assert.That(snapshot.Seeded, Is.EqualTo(4000));
            Assert.That(snapshot.CyclesCompleted, Is.EqualTo(1));
        });
    }
}
