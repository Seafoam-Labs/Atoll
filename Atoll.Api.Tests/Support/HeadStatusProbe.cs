using Atoll.Api.Services.Caching;
using Microsoft.Extensions.Caching.Hybrid;

namespace Atoll.Api.Tests.Support;

/// <summary>
///     Counts rebuilds of one <c>head-status</c>-tagged entry, so a test can assert that the write
///     under test did or did not drop the tag. The 5 minute TTL keeps expiry out of the count.
/// </summary>
internal sealed class HeadStatusProbe(HybridCache cache)
{
    private static readonly HybridCacheEntryOptions LongLived = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
    };

    /// <summary>How many times the entry has rebuilt.</summary>
    internal int Runs { get; private set; }

    internal ValueTask<string> ReadAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync("probe.head-status", BuildAsync, LongLived, [AtollCacheKeys.TagHeadStatus], ct);

    private ValueTask<string> BuildAsync(CancellationToken ct)
    {
        Runs++;
        return new ValueTask<string>("probe");
    }
}
