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
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}
