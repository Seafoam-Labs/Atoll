using System.Net;
using Atoll.Api.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

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

    [Fact]
    public void AddAtollInfrastructure_UnconfiguredProxy_KeepsFrameworkLoopbackDefaults()
    {
        var forwarded = BuildForwardedHeaders(new Dictionary<string, string?>(StringComparer.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, forwarded.ForwardedHeaders);
            Assert.Equal("X-Forwarded-Proto", forwarded.ForwardedProtoHeaderName);
            Assert.Equal(1, forwarded.ForwardLimit);
            var network = Assert.Single(forwarded.KnownIPNetworks);
            Assert.Equal(IPAddress.Parse("127.0.0.0"), network.BaseAddress);
            Assert.Equal(8, network.PrefixLength);
            Assert.Equal(new[] { IPAddress.IPv6Loopback }, forwarded.KnownProxies);
        });
    }

    [Fact]
    public void AddAtollInfrastructure_ConfiguredNetworksProxiesAndHeaders_ReplaceDefaults()
    {
        var forwarded = BuildForwardedHeaders(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Atoll:Proxy:KnownNetworks"] = "172.31.0.0/16,10.0.0.0/8",
            ["Atoll:Proxy:KnownProxies"] = "192.0.2.10",
            ["Atoll:Proxy:ForwardedProtoHeaderName"] = "CloudFront-Forwarded-Proto",
            ["Atoll:Proxy:ForwardLimit"] = "2"
        });

        Assert.Multiple(() =>
        {
            Assert.Equal(
                new[] { ("172.31.0.0", 16), ("10.0.0.0", 8) },
                forwarded.KnownIPNetworks.Select(network => (network.BaseAddress.ToString(), network.PrefixLength)));
            Assert.Equal(new[] { IPAddress.Parse("192.0.2.10") }, forwarded.KnownProxies);
            Assert.Equal("CloudFront-Forwarded-Proto", forwarded.ForwardedProtoHeaderName);
            Assert.Equal(2, forwarded.ForwardLimit);
        });
    }

    [Fact]
    public async Task UseForwardedHeaders_TwoHopChain_RestoresSchemeAndClientIp()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
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
            Assert.Equal("https", context.Request.Scheme);
            Assert.Equal(IPAddress.Parse("198.51.100.24"), context.Connection.RemoteIpAddress);
        });
    }

    [Theory]
    [InlineData("172.31.0.0/33")]
    [InlineData("172.31.0.0")]
    [InlineData("172.31.0.1/16")]
    [InlineData("not-a-network/16")]
    [InlineData("172.31.0.0/")]
    [InlineData("/16")]
    public void KnownNetworks_InvalidCidr_FailsOptionsValidation(string knownNetworks)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Atoll:Proxy:KnownNetworks"] = knownNetworks
        }).Build();

        services.AddAtollOptions(config);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ProxyOptions>>().Value);
    }

    [Fact]
    public void ForwardLimit_Zero_FailsOptionsValidation()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Atoll:Proxy:ForwardLimit"] = "0"
        }).Build();

        services.AddAtollOptions(config);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ProxyOptions>>().Value);
    }
}
