using Atoll.Api.Services.Catalog.Persistence;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atoll.Api.Tests.Support;

internal static class InMemoryRepositories
{
    /// <summary>
    ///     Swaps the Mongo-backed repositories for the in-memory fakes. Every <c>WebApplicationFactory</c> host
    ///     resolves <c>AtollMetrics</c> while mapping the Prometheus endpoint, and that transitively constructs
    ///     the repositories - each Mongo one creates its indexes in the constructor, so a host that skips this
    ///     needs a live mongod on localhost:27017 and blocks 30s per test when there is none.
    /// </summary>
    public static IServiceCollection UseInMemoryRepositories(this IServiceCollection services)
    {
        services.RemoveAll<IPackageRepository>();
        services.RemoveAll<IPackageSecurityRepository>();
        services.RemoveAll<ISeedExclusionRepository>();
        services.RemoveAll<IAurMetadataRepository>();

        services.AddSingleton<IPackageRepository>(new InMemoryPackageRepository());
        services.AddSingleton<IPackageSecurityRepository>(new InMemoryPackageSecurityRepository());
        services.AddSingleton<ISeedExclusionRepository>(new InMemorySeedExclusionRepository());
        services.AddSingleton<IAurMetadataRepository>(new InMemoryAurMetadataRepository());

        return services;
    }
}
