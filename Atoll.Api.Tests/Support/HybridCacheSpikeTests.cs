using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Atoll.Api.Tests.Support;

// Spike (cache.md step 0) pinning the measured configuration constraints of HybridCache 10.10.0 in
// an L1-only setup: MaximumPayloadBytes behavior and L1 reference semantics. Graduated: the racing
// and cancellation contracts live in HybridCacheSemanticsTests.
[Trait("Category", "Spike")]
public class HybridCacheSpikeTests
{
    private static readonly HybridCacheEntryOptions LongLived = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
    };

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HybridCache NewCache(Action<HybridCacheOptions>? configure = null, LogCapture? logs = null)
    {
        var services = new ServiceCollection();
        if (logs is not null) services.AddLogging(builder => builder.AddProvider(logs));
        if (configure is null) services.AddHybridCache();
        else services.AddHybridCache(configure);
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private static string[] BigNames()
    {
        var names = new string[10_000];
        for (var i = 0; i < names.Length; i++)
            names[i] = $"pkg-{i:D5}-{new string('x', 300)}";
        return names;
    }

    // Measured: the cap does not refuse the store when there is no L2 (the oversized entry keeps
    // serving from L1), but every store logs exactly one Error, so the cap must still be raised for
    // the ~119k-name rank arrays.
    [Fact]
    public async Task DefaultMaximumPayloadBytes_logs_an_error_per_oversized_store()
    {
        var logs = new LogCapture();
        var cache = NewCache(logs: logs);
        var payload = BigNames();
        var calls = 0;

        ValueTask<string[]> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<string[]>(payload);
        }

        var first = await cache.GetOrCreateAsync("spike.payload-cap", Factory, LongLived, cancellationToken: Ct);
        var second = await cache.GetOrCreateAsync("spike.payload-cap", Factory, LongLived, cancellationToken: Ct);
        var third = await cache.GetOrCreateAsync("spike.payload-cap-b", Factory, LongLived, cancellationToken: Ct);

        Assert.Multiple(() =>
        {
            Assert.Equal(2, calls);
            Assert.True(ReferenceEquals(first, second));
            Assert.True(ReferenceEquals(first, third));
            Assert.Equal(
                2,
                logs.Messages.Count(m => m.Contains("MaximumPayloadBytes", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task RaisedMaximumPayloadBytes_stores_the_large_entry_quietly()
    {
        var logs = new LogCapture();
        var cache = NewCache(o => o.MaximumPayloadBytes = 16 * 1024 * 1024, logs);
        var payload = BigNames();
        var calls = 0;

        ValueTask<string[]> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<string[]>(payload);
        }

        await cache.GetOrCreateAsync("spike.raised-cap", Factory, LongLived, cancellationToken: Ct);
        await cache.GetOrCreateAsync("spike.raised-cap", Factory, LongLived, cancellationToken: Ct);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, calls);
            Assert.Empty(logs.Messages);
        });
    }

    // Measured: the payload size check runs even for an [ImmutableObject(true)] value, so the
    // raised cap in AddCachingServices is needed for the marked rank arrays too. Warm reads stay
    // reference-shared; the serialization is paid once per cold store (measured ~7.5 ms for a
    // 2.5 MB / 119k-name array, ~0.04 ms per warm read).
    [Fact]
    public async Task ImmutableObject_payloads_are_still_measured_against_the_default_cap()
    {
        var logs = new LogCapture();
        var cache = NewCache(logs: logs);
        var payload = new ImmutableWrappingRecord(BigNames());
        var calls = 0;

        ValueTask<ImmutableWrappingRecord> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<ImmutableWrappingRecord>(payload);
        }

        var first = await cache.GetOrCreateAsync("spike.marked-cap", Factory, LongLived, cancellationToken: Ct);
        var second = await cache.GetOrCreateAsync("spike.marked-cap", Factory, LongLived, cancellationToken: Ct);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, calls);
            Assert.True(ReferenceEquals(first, second));
            Assert.True(ReferenceEquals(first.Names, second.Names));
            Assert.Single(logs.Messages, m => m.Contains("MaximumPayloadBytes", StringComparison.Ordinal));
        });
    }

    // Measured: every unmarked shape is deep-copied on an L1 hit (serialization round-trip), so the
    // step 4 rank payload must be a record marked [ImmutableObject(true)] to keep warm reads free.
    [Fact]
    public async Task L1_hits_clone_unmarked_values_and_share_ImmutableObject_marked_ones()
    {
        var cache = TestHybridCache.New();
        string[] names = ["alpha", "beta"];

        var (_, _, arrSame) = await ReadTwiceAsync(cache, "spike.clone.array", names);
        var (_, _, plainSame) = await ReadTwiceAsync(cache, "spike.clone.plain", new PlainRecord("v"));
        var (_, _, wrapSame) = await ReadTwiceAsync(cache, "spike.clone.wrapping", new WrappingRecord(names));
        var (imm1, imm2, immSame) = await ReadTwiceAsync(cache, "spike.clone.immutable", new ImmutableWrappingRecord(names));

        Assert.Multiple(() =>
        {
            Assert.False(arrSame);
            Assert.False(plainSame);
            Assert.False(wrapSame);
            Assert.True(immSame);
            Assert.True(ReferenceEquals(imm1.Names, imm2.Names));
        });
    }

    private async Task<(T First, T Second, bool Same)> ReadTwiceAsync<T>(HybridCache cache, string key, T value)
    {
        ValueTask<T> Factory(CancellationToken _) => new(value);
        var first = await cache.GetOrCreateAsync(key, Factory, LongLived, cancellationToken: Ct);
        var second = await cache.GetOrCreateAsync(key, Factory, LongLived, cancellationToken: Ct);
        return (first, second, ReferenceEquals(first, second));
    }

    private sealed record PlainRecord(string Value);

    private sealed record WrappingRecord(string[] Names);

    [ImmutableObject(true)]
    private sealed record ImmutableWrappingRecord(string[] Names);

    // Measured: dots, dashes, and slashes pass key validation, so the AtollCacheKeys vocabulary from
    // cache.md step 1 is usable verbatim.
    [Fact]
    public async Task Keys_may_contain_dots_dashes_and_slashes()
    {
        var cache = TestHybridCache.New();

        var value = await cache.GetOrCreateAsync(
            "atoll.rank.sorted/downloads/0", static _ => new ValueTask<string>("ok"), LongLived,
            ["atoll.ui.seeded-snapshot"], Ct);

        Assert.Equal("ok", value);
    }

    private sealed class LogCapture : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => this;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                _messages.Enqueue($"{logLevel}#{eventId.Id}: {formatter(state, exception)}");
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => this;

        public void Dispose()
        {
        }
    }
}
