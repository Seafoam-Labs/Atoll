using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Atoll.Api.Tests.Support;

// Fresh DI-built DefaultHybridCache per call, never a mock: the real instance is what pins stampede
// coalescing. L1 is per-instance, so tests never bleed into each other; production is a singleton.
public static class TestHybridCache
{
    public static HybridCache New()
    {
        var services = new ServiceCollection();
        // Mirrors AddCachingServices: the rank arrays exceed the 1 MB default, whose store-anyway
        // behavior with an error log is a quirk, not something to lean on.
        services.AddHybridCache(options => options.MaximumPayloadBytes = 16 * 1024 * 1024);
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}
