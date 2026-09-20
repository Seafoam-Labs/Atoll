using Atoll.Api.Services.Metrics;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

public class MetricsScopeIsolationTests
{
    [Fact]
    public async Task EachTestingHostScrapesOnlyItsOwnAtollMeterScope()
    {
        // Guards against the parallel-runner flake: WebApplicationFactory hosts
        // share one process and .NET metric listeners match meters by name
        // process-wide, so every host must expose its own Atoll meter scope.
        using var other = new ApiTestFactory();
        using var host = new ApiTestFactory();

        var hostScope = host.Services.GetRequiredService<AtollMetrics>().ScopeName;
        var otherScope = other.Services.GetRequiredService<AtollMetrics>().ScopeName;
        Assert.NotEqual(hostScope, otherScope);

        var client = host.CreateClient();
        await client.GetAsync("/v1/search?query=portable-kit", TestContext.Current.CancellationToken);
        var body = await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken);

        var atollLines = body.Split('\n')
            .Where(static line => line.StartsWith("atoll_", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(atollLines);
        Assert.All(atollLines, line => Assert.Contains($"otel_scope_name=\"{hostScope}\"", line, StringComparison.Ordinal));
    }
}
