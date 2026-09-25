using Atoll.Api.Extensions;
using Atoll.Api.Services.Sync.Bulk;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Sync.Mirror;
using Atoll.Api.Services.Sync.Refresh;
using Atoll.Api.Services.Catalog.Refresh;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Atoll.Api.Tests.Extensions;

public class ServiceCollectionExtensionsTests
{
    private sealed class HostingProbeFactory(IDictionary<string, string?> configurationOverrides)
        : WebApplicationFactory<Program>
    {
        public List<Type> HostedServiceTypes { get; } = [];

        public int MirrorRegistrations { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseConfiguration(new ConfigurationBuilder()
                .AddInMemoryCollection(configurationOverrides)
                .Build());

            builder.ConfigureServices(services =>
            {
                HostedServiceTypes.AddRange(services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .Select(d => d.ImplementationType)
                    .Where(t => t is not null)
                    .Select(t => t!));

                MirrorRegistrations = services.Count(d => d.ServiceType == typeof(IAurMirror));

                // Stop the workers from running; this probe only observes registrations.
                services.RemoveAll<IHostedService>();
                services.UseInMemoryRepositories();
            });
        }
    }

    [Theory]
    [InlineData("Off", false)]
    [InlineData("Direct", false)]
    [InlineData("Bulk", false)]
    [InlineData("Off", true)]
    [InlineData("Direct", true)]
    [InlineData("Bulk", true)]
    public void Host_registers_expected_workers_and_shared_mirror_per_configuration(
        string seedMode,
        bool refreshEnabled)
    {
        using var factory = new HostingProbeFactory(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Atoll:Seed:Mode"] = seedMode,
            ["Atoll:Refresh:Enabled"] = refreshEnabled ? "true" : "false"
        });

        _ = factory.Services;

        var hosted = factory.HostedServiceTypes;
        Assert.Multiple(() =>
        {
            Assert.Contains(typeof(PackageSecurityWorker), hosted);
            Assert.Contains(typeof(PackageIndexWorker), hosted);

            if (string.Equals(seedMode, "Off", StringComparison.Ordinal))
                Assert.DoesNotContain(typeof(DirectSeedWorker), hosted);
            else
                Assert.Contains(string.Equals(seedMode, "Bulk", StringComparison.Ordinal) ? typeof(PackageBulkSeedWorker) : typeof(DirectSeedWorker), hosted);

            if (refreshEnabled)
                Assert.Contains(typeof(PackageRefreshWorker), hosted);
            else
                Assert.DoesNotContain(typeof(PackageRefreshWorker), hosted);
        });

        var expectMirror = string.Equals(seedMode, "Bulk", StringComparison.Ordinal) || refreshEnabled;
        Assert.Equal(expectMirror ? 1 : 0, factory.MirrorRegistrations);

        var bulkEnabled = factory.Services.GetRequiredService<BulkSeedStatusStore>().GetSnapshot().Enabled;
        var directEnabled = factory.Services.GetRequiredService<DirectSeedStatusStore>().GetSnapshot().Enabled;
        var refreshSnapshotEnabled = factory.Services.GetRequiredService<RefreshStatusStore>().GetSnapshot().Enabled;
        Assert.Multiple(() =>
        {
            Assert.Equal(string.Equals(seedMode, "Bulk", StringComparison.Ordinal), bulkEnabled);
            Assert.Equal(string.Equals(seedMode, "Direct", StringComparison.Ordinal), directEnabled);
            Assert.Equal(refreshEnabled, refreshSnapshotEnabled);
        });

        if (expectMirror)
            Assert.NotNull(factory.Services.GetRequiredService<IAurMirror>());
    }

    [Fact]
    public void AddSyncServices_without_seed_or_refresh_sections_defaults_to_direct_without_mirror()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddSyncServices(configuration);

        var hosted = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.Contains(typeof(DirectSeedWorker), hosted);
            Assert.DoesNotContain(typeof(PackageBulkSeedWorker), hosted);
            Assert.DoesNotContain(typeof(PackageRefreshWorker), hosted);
            Assert.Equal(0, services.Count(d => d.ServiceType == typeof(IAurMirror)));
        });

        using var provider = services.BuildServiceProvider();
        Assert.Multiple(() =>
        {
            Assert.True(provider.GetRequiredService<DirectSeedStatusStore>().GetSnapshot().Enabled);
            Assert.False(provider.GetRequiredService<BulkSeedStatusStore>().GetSnapshot().Enabled);
            Assert.False(provider.GetRequiredService<RefreshStatusStore>().GetSnapshot().Enabled);
        });
    }

    [Fact]
    public void AddSecurityServices_without_security_section_defaults_to_enabled()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddSecurityServices(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<SecurityScanStatusStore>().GetSnapshot().Enabled);
    }
}
