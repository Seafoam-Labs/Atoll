using System.Globalization;
using System.Text.Json;
using Atoll.Api.Services.Catalog.Refresh;

namespace Atoll.Api.Services.Metrics;

/// <summary>
///     Writes one CloudWatch embedded-metric-format event per refresh cycle to stdout.
///     The awslogs driver ships it to CloudWatch Logs, which extracts the metric without
///     PutMetricData or an ECS task role. Do not route this through ILogger: EMF requires
///     the log event to be the JSON document with no prefix.
/// </summary>
public sealed class MetadataRefreshMetricPublisher(TextWriter output)
{
    public const string MetricNamespace = "Atoll/MetadataRefresh";
    public const string MetricName = "RefreshConsecutiveFailures";

    public void Publish(RefreshStatusSnapshot status, PackageIndexRefreshOutcome outcome)
    {
        var payload = new
        {
            _aws = new
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CloudWatchMetrics = new[]
                {
                    new
                    {
                        Namespace = MetricNamespace,
                        // One empty dimension set, matching the EMF schema and AWS's
                        // Powertools serializer for a metric without dimensions.
                        Dimensions = new[] { Array.Empty<string>() },
                        Metrics = new[]
                        {
                            new { Name = MetricName, Unit = "Count" }
                        }
                    }
                }
            },
            // Keep this property name in sync with MetricName; EMF references it by name.
            RefreshConsecutiveFailures = status.ConsecutiveFailures,
            Outcome = outcome.ToString(),
            status.Attempts,
            status.Successes,
            status.Failures,
            LastSucceededUtc = status.LastSucceededUtc?.ToString("O", CultureInfo.InvariantCulture),
            LastFailedUtc = status.LastFailedUtc?.ToString("O", CultureInfo.InvariantCulture)
        };

        output.WriteLine(JsonSerializer.Serialize(payload));
        output.Flush();
    }
}