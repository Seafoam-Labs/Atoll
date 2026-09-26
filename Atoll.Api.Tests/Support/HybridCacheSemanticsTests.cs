using Microsoft.Extensions.Caching.Hybrid;
using Xunit;

namespace Atoll.Api.Tests.Support;

// Contracts the HybridCache-backed services (ranker, catalog snapshot, dashboard) lean on. Built
// on the real DefaultHybridCache: mocking the abstract class would not pin stampede coalescing.
public class HybridCacheSemanticsTests
{
    private static readonly HybridCacheEntryOptions LongLived = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Measured: a removal that lands while a factory is in flight does not evict that factory's late
    // store. A value generated before a write can therefore serve for one full TTL after a racing
    // write; the next invalidation or expiry heals it.
    [Fact]
    public async Task RemoveByTagAsync_InFlightFactoryLateStore_IsNotEvicted()
    {
        var cache = TestHybridCache.New();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshCalls = 0;

        async ValueTask<string> InFlightAsync(CancellationToken _)
        {
            started.SetResult();
            await release.Task;
            return "in-flight";
        }

        ValueTask<string> FreshAsync(CancellationToken _)
        {
            freshCalls++;
            return new ValueTask<string>("fresh");
        }

        var read1 = cache.GetOrCreateAsync("rank.names", InFlightAsync, LongLived, ["catalog"], Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cache.RemoveByTagAsync("catalog", Ct);
        release.SetResult();
        var first = await read1.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = await cache.GetOrCreateAsync("rank.names", FreshAsync, LongLived, ["catalog"], Ct);

        Assert.Multiple(() =>
        {
            Assert.Equal("in-flight", first);
            Assert.Equal("in-flight", second);
            Assert.Equal(0, freshCalls);
        });
    }

    // Measured: expiration is absolute, so a periodic warm that only reads does not keep an entry
    // alive; the entry dies on its own clock and the next reader pays the factory. Storing the value
    // back is what moves the expiry, which is what the ranker's warm relies on.
    [Fact]
    public async Task GetOrCreateAsync_AbsoluteExpiration_OnlyReStoreExtendsIt()
    {
        var cache = TestHybridCache.New();
        var ttl = TimeSpan.FromMilliseconds(2000);
        var options = new HybridCacheEntryOptions { Expiration = ttl, LocalCacheExpiration = ttl };
        var hitBuilds = 0;
        var reStoredBuilds = 0;

        ValueTask<string> HitAsync(CancellationToken _)
        {
            hitBuilds++;
            return new ValueTask<string>("hit");
        }

        ValueTask<string> ReStoredAsync(CancellationToken _)
        {
            reStoredBuilds++;
            return new ValueTask<string>("re-stored");
        }

        // Read at 1200 ms is a hit and leaves the 2000 ms expiry alone, so the 2400 ms read rebuilds.
        await cache.GetOrCreateAsync("hit", HitAsync, options, ["catalog"], Ct);
        await Task.Delay(1200, Ct);
        await cache.GetOrCreateAsync("hit", HitAsync, options, ["catalog"], Ct);
        await Task.Delay(1200, Ct);
        await cache.GetOrCreateAsync("hit", HitAsync, options, ["catalog"], Ct);

        // Same shape, but the 1200 ms read stores the value back, moving the expiry to 3200 ms.
        await cache.GetOrCreateAsync("re-stored", ReStoredAsync, options, ["catalog"], Ct);
        await Task.Delay(1200, Ct);
        var value = await cache.GetOrCreateAsync("re-stored", ReStoredAsync, options, ["catalog"], Ct);
        await cache.SetAsync("re-stored", value, options, ["catalog"], Ct);
        await Task.Delay(1200, Ct);
        await cache.GetOrCreateAsync("re-stored", ReStoredAsync, options, ["catalog"], Ct);

        Assert.Multiple(() =>
        {
            Assert.Equal(2, hitBuilds);
            Assert.Equal(1, reStoredBuilds);
        });
    }

    // Measured: an already-cancelled caller token throws before the factory runs, so a cached read
    // needs no ThrowIfCancellationRequested prologue of its own.
    [Fact]
    public async Task GetOrCreateAsync_PrecancelledToken_ThrowsWithoutInvokingFactory()
    {
        var cache = TestHybridCache.New();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var calls = 0;

        ValueTask<string> FactoryAsync(CancellationToken _)
        {
            calls++;
            return new ValueTask<string>("never served");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.GetOrCreateAsync("rank.names", FactoryAsync, LongLived, cancellationToken: cancelled.Token).AsTask());

        Assert.Equal(0, calls);
    }

    // Measured: the factory token cancels only when every waiter is gone, so a caller aborting its
    // request cannot kill a shared rebuild; the remaining waiter gets the value and it is cached.
    [Fact]
    public async Task GetOrCreateAsync_WaiterAbortsWithAnotherWaiting_KeepsRebuildAlive()
    {
        var cache = TestHybridCache.New();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var factorySawCancellation = false;

        async ValueTask<string> FactoryAsync(CancellationToken ct)
        {
            calls++;
            started.SetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(static s => ((TaskCompletionSource)s!).SetResult(), cancelled))
            {
                var winner = await Task.WhenAny(release.Task, cancelled.Task);
                factorySawCancellation = winner == cancelled.Task;
            }

            release.TrySetResult();
            return "shared";
        }

        using var aborting = new CancellationTokenSource();
        var waiterA = cache.GetOrCreateAsync("rank.names", FactoryAsync, LongLived, cancellationToken: aborting.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiterB = cache.GetOrCreateAsync("rank.names", FactoryAsync, LongLived, cancellationToken: Ct).AsTask();
        await aborting.CancelAsync();

        // Await the abort before releasing the factory. The aborted join cancels on its own token, so
        // this pins that it leaves while the rebuild is still blocked; releasing first races the
        // shared completion, which can win and serve the value to the aborted wait instead.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => waiterA.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        release.TrySetResult();
        var b = await waiterB.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var third = await cache.GetOrCreateAsync("rank.names", FactoryAsync, LongLived, cancellationToken: Ct);

        Assert.Multiple(() =>
        {
            Assert.False(factorySawCancellation);
            Assert.Equal("shared", b);
            Assert.Equal("shared", third);
            Assert.Equal(1, calls);
        });
    }
}
