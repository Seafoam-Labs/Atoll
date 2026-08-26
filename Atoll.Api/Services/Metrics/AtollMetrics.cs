using System.Diagnostics;
using System.Diagnostics.Metrics;
using Atoll.Api.Services.Sync.Bulk;
using Atoll.Api.Services.Sync.Refresh;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Refresh;
using Atoll.Api.Services.Security;

namespace Atoll.Api.Services.Metrics;

public sealed class AtollMetrics : IDisposable
{
    public const string MeterName = "Atoll.Api";

    private readonly Meter _meter;

    public AtollMetrics(
        PackageSearchService searchService,
        PackageIndexStore indexStore,
        PackageIndexUpdater indexUpdater,
        BulkSeedStatusStore bulkSeedStatus,
        RefreshStatusStore packageRefreshStatus,
        SecurityScanStatusStore securityScanStatus)
    {
        var meter = new Meter(MeterName, "1.0.0");

        var uptime = Stopwatch.StartNew();
        meter.CreateObservableGauge(
            "process.uptime",
            () => uptime.Elapsed.TotalSeconds,
            "s",
            "Uptime of the process in seconds.");

        meter.CreateObservableCounter(
            "atoll.search.requests.total",
            () => searchService.RequestCount,
            "{request}",
            "Total number of search requests served.");

        meter.CreateObservableGauge(
            "atoll.index.size",
            () =>
            {
                var snapshot = indexStore.Current;
                return new[]
                {
                    new Measurement<long>(snapshot.ByNames.Count, new KeyValuePair<string, object?>("index", "names")),
                    new Measurement<long>(snapshot.ByProvides.Count, new KeyValuePair<string, object?>("index", "provides")),
                    new Measurement<long>(snapshot.ByWords.Count, new KeyValuePair<string, object?>("index", "words"))
                };
            },
            "{entry}",
            "Number of entries in each search index.");

        RegisterSnapshotCounters(meter, "atoll.refresh", indexUpdater.GetStatus,
            ("attempts.total", "Total metadata refresh attempts.", s => s.Attempts),
            ("successes.total", "Total successful metadata refreshes.", s => s.Successes),
            ("failures.total", "Total failed metadata refreshes.", s => s.Failures));

        RegisterTimestamp(meter, "atoll.refresh.last_started_timestamp",
            "Unix time of the last started metadata refresh.",
            () => indexUpdater.GetStatus().LastStartedUtc);
        RegisterTimestamp(meter, "atoll.refresh.last_succeeded_timestamp",
            "Unix time of the last successful metadata refresh.",
            () => indexUpdater.GetStatus().LastSucceededUtc);
        RegisterTimestamp(meter, "atoll.refresh.last_failed_timestamp",
            "Unix time of the last failed metadata refresh.",
            () => indexUpdater.GetStatus().LastFailedUtc);

        RegisterSnapshotCounters(meter, "atoll.bulkseed", bulkSeedStatus.GetSnapshot,
            ("batches.attempted.total", "Total bulk seed batches attempted.", s => s.BatchesAttempted),
            ("batches.succeeded.total", "Total bulk seed batches succeeded.", s => s.BatchesSucceeded),
            ("batches.failed.total", "Total bulk seed batches failed.", s => s.BatchesFailed),
            ("refs.skipped.total", "Total mirror refs skipped during bulk seeding.", s => s.RefsSkipped),
            ("refs.failed.total", "Total mirror refs that failed during bulk seeding.", s => s.RefsFailed),
            ("packages.seeded.total", "Total packages seeded.", s => s.PackagesSeeded),
            ("packages.skipped.total", "Total packages skipped during bulk seeding.", s => s.PackagesSkipped),
            ("packages.excluded.total", "Total packages excluded from bulk seeding.", s => s.PackagesExcluded));

        RegisterTimestamp(meter, "atoll.bulkseed.last_started_timestamp",
            "Unix time of the last started bulk seed cycle.",
            () => bulkSeedStatus.GetSnapshot().LastStartedUtc);
        RegisterTimestamp(meter, "atoll.bulkseed.last_finished_timestamp",
            "Unix time of the last finished bulk seed cycle.",
            () => bulkSeedStatus.GetSnapshot().LastFinishedUtc);

        RegisterSnapshotCounters(meter, "atoll.packagerefresh", packageRefreshStatus.GetSnapshot,
            ("cycles.attempted.total", "Total package refresh cycles attempted.", s => s.CyclesAttempted),
            ("cycles.succeeded.total", "Total package refresh cycles succeeded.", s => s.CyclesSucceeded),
            ("cycles.failed.total", "Total package refresh cycles failed.", s => s.CyclesFailed),
            ("packages.updated.total", "Total packages updated by package refresh.", s => s.PackagesUpdated),
            ("packages.unchanged.total", "Total packages found unchanged by package refresh.", s => s.PackagesUnchanged),
            ("packages.skipped.total", "Total packages skipped by package refresh.", s => s.PackagesSkipped),
            ("refs.skipped.total", "Total mirror refs skipped during package refresh.", s => s.RefsSkipped),
            ("refs.failed.total", "Total mirror refs that failed during package refresh.", s => s.RefsFailed));

        meter.CreateObservableGauge(
            "atoll.packagerefresh.candidate_packages",
            () => packageRefreshStatus.GetSnapshot().CandidatePackages,
            "{package}",
            "Candidate packages discovered by package refresh.");

        meter.CreateObservableGauge(
            "atoll.packagerefresh.candidate_package_bases",
            () => packageRefreshStatus.GetSnapshot().CandidatePackageBases,
            "{pkgbase}",
            "Candidate package bases discovered by package refresh.");

        RegisterTimestamp(meter, "atoll.packagerefresh.last_started_timestamp",
            "Unix time of the last started package refresh cycle.",
            () => packageRefreshStatus.GetSnapshot().LastStartedUtc);
        RegisterTimestamp(meter, "atoll.packagerefresh.last_finished_timestamp",
            "Unix time of the last finished package refresh cycle.",
            () => packageRefreshStatus.GetSnapshot().LastFinishedUtc);

        RegisterSnapshotCounters(meter, "atoll.securityscan", securityScanStatus.GetSnapshot,
            ("completed.total", "Total security scans completed.", s => s.ScansCompleted),
            ("verified.total", "Total security scans that verified a clean result.", s => s.ScansVerified),
            ("flagged.total", "Total security scans that flagged findings.", s => s.ScansFlagged),
            ("errored.total", "Total security scans that errored.", s => s.ScansErrored),
            ("dropped.total", "Total security scans dropped.", s => s.ScansDropped));

        meter.CreateObservableGauge(
            "atoll.securityscan.pending",
            () => securityScanStatus.GetSnapshot().PendingScans,
            "{scan}",
            "Security scans currently pending.");

        RegisterTimestamp(meter, "atoll.securityscan.last_finished_timestamp",
            "Unix time of the last finished security scan.",
            () => securityScanStatus.GetSnapshot().LastScanFinishedUtc);

        _meter = meter;
    }

    public void Dispose()
    {
        _meter.Dispose();
    }

    private static void RegisterSnapshotCounters<TSnapshot>(
        Meter meter,
        string prefix,
        Func<TSnapshot> getSnapshot,
        params (string Suffix, string Description, Func<TSnapshot, long> Select)[] counters)
    {
        foreach (var (suffix, description, select) in counters)
            meter.CreateObservableCounter(
                $"{prefix}.{suffix}",
                () => select(getSnapshot()),
                description: description);
    }

    private static void RegisterTimestamp(Meter meter, string name, string description, Func<DateTimeOffset?> getValue)
    {
        meter.CreateObservableGauge(
            name,
            () => getValue() is { } timestamp
                ? new[] { new Measurement<long>(timestamp.ToUnixTimeSeconds()) }
                : Array.Empty<Measurement<long>>(),
            description: description);
    }
}