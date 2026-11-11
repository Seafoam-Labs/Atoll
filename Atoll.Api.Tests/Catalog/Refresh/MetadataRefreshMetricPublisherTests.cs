using System.Text.Json;
using Atoll.Api.Services.Catalog.Refresh;
using Atoll.Api.Services.Metrics;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Refresh;

public class MetadataRefreshMetricPublisherTests
{
    [Fact]
    public void Publish_WritesOneStandaloneEmfDocument()
    {
        var output = new StringWriter();
        var publisher = new MetadataRefreshMetricPublisher(output);
        var status = new RefreshStatusSnapshot(
            MetadataCollection: "aur-metadata",
            Interval: TimeSpan.FromMinutes(5),
            Attempts: 123,
            Successes: 117,
            Failures: 6,
            ConsecutiveFailures: 4,
            LastStartedUtc: new DateTimeOffset(2026, 11, 10, 13, 5, 0, TimeSpan.Zero),
            LastSucceededUtc: new DateTimeOffset(2026, 11, 10, 8, 18, 0, TimeSpan.Zero),
            LastFailedUtc: new DateTimeOffset(2026, 11, 10, 13, 5, 12, TimeSpan.Zero),
            LastLoadedFromCacheUtc: null);

        publisher.Publish(status, PackageIndexRefreshOutcome.Failed);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var aws = root.GetProperty("_aws");
        var directive = aws.GetProperty("CloudWatchMetrics")[0];

        Assert.Multiple(() =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.InRange(aws.GetProperty("Timestamp").GetInt64(), now - 60_000, now + 60_000);
            Assert.Equal("Atoll/MetadataRefresh", directive.GetProperty("Namespace").GetString());

            // One empty dimension set: the EMF schema requires at least one set, and an
            // empty set publishes the metric without dimensions.
            var dimensions = directive.GetProperty("Dimensions");
            Assert.Equal(1, dimensions.GetArrayLength());
            Assert.Equal(0, dimensions[0].GetArrayLength());

            var metric = directive.GetProperty("Metrics")[0];
            Assert.Equal("RefreshConsecutiveFailures", metric.GetProperty("Name").GetString());
            Assert.Equal("Count", metric.GetProperty("Unit").GetString());

            Assert.Equal(4, root.GetProperty("RefreshConsecutiveFailures").GetInt64());
            Assert.Equal("Failed", root.GetProperty("Outcome").GetString());
            Assert.Equal(123, root.GetProperty("Attempts").GetInt64());
            Assert.Equal(117, root.GetProperty("Successes").GetInt64());
            Assert.Equal(6, root.GetProperty("Failures").GetInt64());
        });
    }

    [Fact]
    public void Publish_OnSuccess_WritesZero()
    {
        var output = new StringWriter();
        var publisher = new MetadataRefreshMetricPublisher(output);
        var status = new RefreshStatusSnapshot(
            MetadataCollection: "aur-metadata",
            Interval: TimeSpan.FromMinutes(5),
            Attempts: 124,
            Successes: 118,
            Failures: 6,
            ConsecutiveFailures: 0,
            LastStartedUtc: DateTimeOffset.UtcNow,
            LastSucceededUtc: DateTimeOffset.UtcNow,
            LastFailedUtc: null,
            LastLoadedFromCacheUtc: null);

        publisher.Publish(status, PackageIndexRefreshOutcome.Refreshed);

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, document.RootElement.GetProperty("RefreshConsecutiveFailures").GetInt64());
    }
}