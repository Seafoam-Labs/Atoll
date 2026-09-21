using Atoll.Api.Extensions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Atoll.Api.Tests.Support;

// Fresh DI-built DefaultHybridCache per call, never a mock: the real instance is what pins stampede
// coalescing. Built through the production registration so the host's payload cap cannot drift away
// from what tests store. L1 is per-instance, so tests never bleed into each other; production is a
// singleton, so bleed is impossible there.
public static class TestHybridCache
{
    public static HybridCache New()
    {
        var services = new ServiceCollection();
        services.AddCachingServices();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}
