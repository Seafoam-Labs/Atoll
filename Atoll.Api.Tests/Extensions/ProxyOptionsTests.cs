using System.Net;
using Atoll.Api.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Extensions;

public class ProxyOptionsTests
{
    private static ForwardedHeadersOptions BuildForwardedHeaders(IDictionary<string, string?> configuration)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(configuration).Build();

        services.AddAtollOptions(config);
        services.AddAtollInfrastructure();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }

    [Test]
    public void Without_proxy_configuration_framework_loopback_defaults_apply()
    {
        var forwarded = BuildForwardedHeaders(new Dictionary<string, string?>());

        Assert.Multiple(() =>
        {
            Assert.That(forwarded.ForwardedHeaders,
                Is.EqualTo(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto));
            Assert.That(forwarded.ForwardedProtoHeaderName, Is.EqualTo("X-Forwarded-Proto"));
            Assert.That(forwarded.ForwardLimit, Is.EqualTo(1));
            Assert.That(forwarded.KnownIPNetworks, Has.Count.EqualTo(1));
            Assert.That(forwarded.KnownIPNetworks[0].BaseAddress, Is.EqualTo(IPAddress.Parse("127.0.0.0")));
            Assert.That(forwarded.KnownIPNetworks[0].PrefixLength, Is.EqualTo(8));
            Assert.That(forwarded.KnownProxies, Is.EqualTo(new[] { IPAddress.IPv6Loopback }));
        });
    }

    [Test]
    public void Configured_networks_proxies_and_headers_replace_the_defaults()
    {
        var forwarded = BuildForwardedHeaders(new Dictionary<string, string?>
        {
            ["Atoll:Proxy:KnownNetworks"] = "172.31.0.0/16,10.0.0.0/8",
            ["Atoll:Proxy:KnownProxies"] = "192.0.2.10",
            ["Atoll:Proxy:ForwardedProtoHeaderName"] = "CloudFront-Forwarded-Proto",
            ["Atoll:Proxy:ForwardLimit"] = "2"
        });

        Assert.Multiple(() =>
        {
            Assert.That(
                forwarded.KnownIPNetworks.Select(network => (network.BaseAddress.ToString(), network.PrefixLength)),
                Is.EqualTo(new[] { ("172.31.0.0", 16), ("10.0.0.0", 8) }));
            Assert.That(forwarded.KnownProxies, Is.EqualTo(new[] { IPAddress.Parse("192.0.2.10") }));
            Assert.That(forwarded.ForwardedProtoHeaderName, Is.EqualTo("CloudFront-Forwarded-Proto"));
            Assert.That(forwarded.ForwardLimit, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Two_hop_chain_restores_original_scheme_and_client_ip()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Atoll:Proxy:KnownNetworks"] = "172.31.0.0/16",
            ["Atoll:Proxy:ForwardedProtoHeaderName"] = "CloudFront-Forwarded-Proto",
            ["Atoll:Proxy:ForwardLimit"] = "2"
        }).Build();

        services.AddAtollOptions(config);
        services.AddAtollInfrastructure();

        await using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseForwardedHeaders();
        app.Run(_ => Task.CompletedTask);

        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.31.0.50");
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.24, 172.31.0.20";
        context.Request.Headers["CloudFront-Forwarded-Proto"] = "https";

        await app.Build()(context);

        Assert.Multiple(() =>
        {
            Assert.That(context.Request.Scheme, Is.EqualTo("https"));
            Assert.That(context.Connection.RemoteIpAddress, Is.EqualTo(IPAddress.Parse("198.51.100.24")));
        });
    }

    [TestCase("172.31.0.0/33")]
    [TestCase("172.31.0.0")]
    [TestCase("172.31.0.1/16")]
    [TestCase("not-a-network/16")]
    [TestCase("172.31.0.0/")]
    [TestCase("/16")]
    public void Invalid_networks_fail_options_validation(string knownNetworks)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Atoll:Proxy:KnownNetworks"] = knownNetworks
        }).Build();

        services.AddAtollOptions(config);
        using var provider = services.BuildServiceProvider();

        Assert.That(() => provider.GetRequiredService<IOptions<ProxyOptions>>().Value,
            Throws.TypeOf<OptionsValidationException>());
    }

    [Test]
    public void Invalid_forward_limit_fails_options_validation()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Atoll:Proxy:ForwardLimit"] = "0"
        }).Build();

        services.AddAtollOptions(config);
        using var provider = services.BuildServiceProvider();

        Assert.That(() => provider.GetRequiredService<IOptions<ProxyOptions>>().Value,
            Throws.TypeOf<OptionsValidationException>());
    }
}
