using Atoll.Api.Extensions;
using Atoll.Api.Services.Packages.Mirror;
using Atoll.Api.Services.Packages.Refresh;
using Atoll.Api.Services.Packages.Seed;
using Atoll.Api.Services.Search.Refresh;
using Atoll.Api.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

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
            });
        }
    }

    [TestCase("Off", false)]
    [TestCase("Direct", false)]
    [TestCase("Bulk", false)]
    [TestCase("Off", true)]
    [TestCase("Direct", true)]
    [TestCase("Bulk", true)]
    public void Host_registers_expected_workers_and_shared_mirror_per_configuration(
        string seedMode,
        bool refreshEnabled)
    {
        using var factory = new HostingProbeFactory(new Dictionary<string, string?>
        {
            ["Atoll:Seed:Mode"] = seedMode,
            ["Atoll:Refresh:Enabled"] = refreshEnabled ? "true" : "false"
        });

        _ = factory.Services;

        var hosted = factory.HostedServiceTypes;
        Assert.Multiple(() =>
        {
            Assert.That(hosted, Does.Contain(typeof(PackageSecurityWorker)));
            Assert.That(hosted, Does.Contain(typeof(PackageIndexWorker)));

            Assert.That(hosted, seedMode == "Off"
                ? Does.Not.Contain(typeof(DirectSeedWorker))
                : Does.Contain(seedMode == "Bulk" ? typeof(PackageBulkSeedWorker) : typeof(DirectSeedWorker)));
            Assert.That(hosted, refreshEnabled
                ? Does.Contain(typeof(PackageRefreshWorker))
                : Does.Not.Contain(typeof(PackageRefreshWorker)));
        });

        var expectMirror = seedMode == "Bulk" || refreshEnabled;
        Assert.That(factory.MirrorRegistrations, Is.EqualTo(expectMirror ? 1 : 0),
            "bulk seed and refresh must share exactly one mirror registration");

        var bulkEnabled = factory.Services.GetRequiredService<BulkSeedStatusStore>().GetSnapshot().Enabled;
        var directEnabled = factory.Services.GetRequiredService<DirectSeedStatusStore>().GetSnapshot().Enabled;
        var refreshSnapshotEnabled = factory.Services.GetRequiredService<RefreshStatusStore>().GetSnapshot().Enabled;
        Assert.Multiple(() =>
        {
            Assert.That(bulkEnabled, Is.EqualTo(seedMode == "Bulk"));
            Assert.That(directEnabled, Is.EqualTo(seedMode == "Direct"));
            Assert.That(refreshSnapshotEnabled, Is.EqualTo(refreshEnabled));
        });

        if (expectMirror)
            Assert.That(factory.Services.GetRequiredService<IAurMirror>(), Is.Not.Null);
    }

    [Test]
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
            Assert.That(hosted, Does.Contain(typeof(DirectSeedWorker)));
            Assert.That(hosted, Does.Not.Contain(typeof(PackageBulkSeedWorker)));
            Assert.That(hosted, Does.Not.Contain(typeof(PackageRefreshWorker)));
            Assert.That(services.Count(d => d.ServiceType == typeof(IAurMirror)), Is.Zero);
        });

        using var provider = services.BuildServiceProvider();
        Assert.Multiple(() =>
        {
            Assert.That(provider.GetRequiredService<DirectSeedStatusStore>().GetSnapshot().Enabled, Is.True);
            Assert.That(provider.GetRequiredService<BulkSeedStatusStore>().GetSnapshot().Enabled, Is.False);
            Assert.That(provider.GetRequiredService<RefreshStatusStore>().GetSnapshot().Enabled, Is.False);
        });
    }

    [Test]
    public void AddSecurityServices_without_security_section_defaults_to_enabled()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddSecurityServices(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.That(provider.GetRequiredService<SecurityScanStatusStore>().GetSnapshot().Enabled, Is.True);
    }
}
