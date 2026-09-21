using Microsoft.Extensions.Caching.Hybrid;
using Xunit;

namespace Atoll.Api.Tests.Support;

// Contracts the HybridCache-backed services (ranker, catalog snapshot, dashboard) lean on,
// graduated from the step 0 spike of cache.md. Built on the real DefaultHybridCache: mocking the
// abstract class would not pin stampede coalescing.
public class HybridCacheSemanticsTests
{
    private static readonly HybridCacheEntryOptions LongLived = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
    };

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // Measured: a removal that lands while a factory is in flight does not evict that factory's late
    // store. A value generated before a write can therefore serve for one full TTL after a racing
    // write; the next invalidation or expiry heals it (accepted delta 2 in cache.md).
    [Fact]
    public async Task RemoveByTagAsync_does_not_evict_the_late_store_of_an_in_flight_factory()
    {
        var cache = TestHybridCache.New();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshCalls = 0;

        async ValueTask<string> InFlight(CancellationToken _)
        {
            started.SetResult();
            await release.Task;
            return "in-flight";
        }

        ValueTask<string> Fresh(CancellationToken _)
        {
            freshCalls++;
            return new ValueTask<string>("fresh");
        }

        var read1 = cache.GetOrCreateAsync("rank.names", InFlight, LongLived, ["catalog"], Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cache.RemoveByTagAsync("catalog", Ct);
        release.SetResult();
        var first = await read1.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = await cache.GetOrCreateAsync("rank.names", Fresh, LongLived, ["catalog"], Ct);

        Assert.Multiple(() =>
        {
            Assert.Equal("in-flight", first);
            Assert.Equal("in-flight", second);
            Assert.Equal(0, freshCalls);
        });
    }

    // Measured: an already-cancelled caller token throws before the factory runs, so a cached read
    // needs no ThrowIfCancellationRequested prologue of its own.
    [Fact]
    public async Task Precancelled_token_throws_without_invoking_the_factory()
    {
        var cache = TestHybridCache.New();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var calls = 0;

        ValueTask<string> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<string>("never served");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.GetOrCreateAsync("rank.names", Factory, LongLived, cancellationToken: cancelled.Token).AsTask());

        Assert.Equal(0, calls);
    }

    // Measured: the factory token cancels only when every waiter is gone, so a caller aborting its
    // request cannot kill a shared rebuild; the remaining waiter gets the value and it is cached.
    [Fact]
    public async Task Factory_token_cancels_only_when_all_waiters_cancel()
    {
        var cache = TestHybridCache.New();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var factorySawCancellation = false;

        async ValueTask<string> Factory(CancellationToken ct)
        {
            calls++;
            started.SetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(static s => ((TaskCompletionSource)s!).SetResult(), cancelled))
            {
                var winner = await Task.WhenAny(release.Task, cancelled.Task);
                factorySawCancellation = winner == cancelled.Task;
            }

            release.TrySetResult();
            return "shared";
        }

        using var aborting = new CancellationTokenSource();
        var waiterA = cache.GetOrCreateAsync("rank.names", Factory, LongLived, cancellationToken: aborting.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiterB = cache.GetOrCreateAsync("rank.names", Factory, LongLived, cancellationToken: Ct).AsTask();
        aborting.Cancel();

        // Await the abort before releasing the factory. The aborted join cancels on its own token, so
        // this pins that it leaves while the rebuild is still blocked; releasing first races the
        // shared completion, which can win and serve the value to the aborted wait instead.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => waiterA.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        release.TrySetResult();
        var b = await waiterB.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var third = await cache.GetOrCreateAsync("rank.names", Factory, LongLived, cancellationToken: Ct);

        Assert.Multiple(() =>
        {
            Assert.False(factorySawCancellation);
            Assert.Equal("shared", b);
            Assert.Equal("shared", third);
            Assert.Equal(1, calls);
        });
    }
}
