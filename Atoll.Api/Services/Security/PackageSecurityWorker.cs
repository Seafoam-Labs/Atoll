using Atoll.Api.Services.Security.Persistence;
using Microsoft.Extensions.Options;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Services.Security;

public sealed class PackageSecurityWorker(
    IPackageRepository packageRepository,
    IPackageSecurityRepository securityRepo,
    IPackageSecurityScanner scanner,
    SecurityScanStatusStore status,
    IOptions<AtollOptions> options,
    ILogger<PackageSecurityWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    // One gauge query per interval regardless of concurrency keeps the backlog metric cheap;
    // 30s trades metric freshness against query load.
    private static readonly TimeSpan PendingScanCountInterval = TimeSpan.FromSeconds(30);

    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly SecurityOptions _security = options.Value.Security;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_security.Enabled)
        {
            logger.LogInformation("Package security scanning is disabled.");
            return;
        }

        var requeued = await securityRepo.RequeueOutdatedAsync(scanner.PolicyVersion, stoppingToken);
        if (requeued > 0)
        {
            logger.LogInformation(
                "Requeued {Count} outdated security scans for policy version {Version}.",
                requeued, scanner.PolicyVersion);
        }

        await EnsureExistingPackagesArePendingAsync(stoppingToken);

        var pollInterval = TimeSpan.FromMilliseconds(_security.PollIntervalMs);
        logger.LogInformation(
            "Package security scanning started (poll {PollIntervalMs} ms, concurrency {Concurrency}).",
            _security.PollIntervalMs, _security.ScannerConcurrency);

        var workers = Enumerable.Range(0, _security.ScannerConcurrency)
            .Select(_ => PollLoopAsync(pollInterval, stoppingToken))
            .Append(TrackPendingScansLoopAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task EnsureExistingPackagesArePendingAsync(CancellationToken ct)
    {
        var packageNames = await packageRepository.ListAsync(ct);
        var scanned = await securityRepo.ListPackageNamesAsync(ct);
        var scannedNames = new HashSet<string>(scanned, StringComparer.Ordinal);

        var missing = packageNames.Where(name => !scannedNames.Contains(name)).ToList();
        if (missing.Count == 0) return;

        logger.LogInformation(
            "Backfilling {Count} package(s) without a security scan document.", missing.Count);

        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = _security.ScannerConcurrency, CancellationToken = ct },
            async (packageName, token) =>
            {
                var headRevisionId = await packageRepository.GetHeadRevisionIdAsync(packageName, token);
                if (!string.IsNullOrEmpty(headRevisionId))
                    await securityRepo.EnsurePendingAsync(packageName, headRevisionId, true, scanner.PolicyVersion, token);
            });
    }

    private async Task PollLoopAsync(TimeSpan pollInterval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var claimed = false;
            try
            {
                claimed = await ClaimAndScanOneAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Security scan poll iteration failed.");
            }

            if (!claimed)
                await Task.Delay(pollInterval, ct);
        }
    }

    private async Task TrackPendingScansLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pending = await securityRepo.CountPendingAsync(ct);
                status.UpdatePendingScans(pending);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not refresh the pending security scan count.");
            }

            await Task.Delay(PendingScanCountInterval, ct);
        }
    }

    private async Task<bool> ClaimAndScanOneAsync(CancellationToken ct)
    {
        var claim = await securityRepo.TryClaimPendingScanAsync(_owner, LeaseDuration, scanner.PolicyVersion, ct);
        if (claim is null)
            return false;

        try
        {
            var revision = await packageRepository.GetRevisionAsync(claim.PackageName, claim.RevisionId, ct);
            if (revision is null)
            {
                logger.LogDebug(
                    "Dropping security scan claim for {PackageName} revision {RevisionId}: revision content no longer retained.",
                    claim.PackageName, claim.RevisionId);
                await securityRepo.DeleteAsync(claim.PackageName, claim.RevisionId, ct);
                status.RecordScanDropped();
                return true;
            }

            var files = revision.Files.ToDictionary(kv => kv.Key, kv => kv.Value.Content, StringComparer.Ordinal);
            var result = scanner.Scan(files);
            var persisted = await securityRepo.CompleteScanAsync(
                claim.PackageName, claim.RevisionId, _owner, result, scanner.PolicyVersion, ct);
            if (!persisted)
            {
                LogStaleClaim(claim);
                return true;
            }

            status.RecordScanCompleted(result.Status);

            if (result.Status == SecurityStatus.Flagged)
                logger.LogDebug(
                    "Security scan flagged {PackageName} revision {RevisionId}: {FindingCount} findings.",
                    claim.PackageName, claim.RevisionId, result.Findings.Count);
            else
                logger.LogDebug(
                    "Security scan for {PackageName} revision {RevisionId} -> {Status} ({FindingCount} findings).",
                    claim.PackageName, claim.RevisionId, result.Status, result.Findings.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ReleaseClaimQuietlyAsync(claim);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Security scan failed for {PackageName} revision {RevisionId}; marking as Error.",
                claim.PackageName, claim.RevisionId);
            if (await TryMarkScanErrorAsync(claim))
                status.RecordScanErrored();
            else
                LogStaleClaim(claim);
        }

        return true;
    }

    private async Task ReleaseClaimQuietlyAsync(PackageSecurityScanDocument claim)
    {
        try
        {
            await securityRepo.ReleaseScanClaimAsync(claim.PackageName, claim.RevisionId, _owner, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to release security scan claim for {PackageName} revision {RevisionId}.",
                claim.PackageName, claim.RevisionId);
        }
    }

    private async Task<bool> TryMarkScanErrorAsync(PackageSecurityScanDocument claim)
    {
        try
        {
            return await securityRepo.MarkScanErrorAsync(
                claim.PackageName, claim.RevisionId, _owner, scanner.PolicyVersion, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to mark {PackageName} revision {RevisionId} as security Error.",
                claim.PackageName, claim.RevisionId);
            return false;
        }
    }

    private void LogStaleClaim(PackageSecurityScanDocument claim)
    {
        // A policy mismatch is normal during a rolling deployment, not a scan failure.
        logger.LogInformation(
            "Discarded security scan result for {PackageName} revision {RevisionId}: the claim became stale (lease lost or required policy version raised).",
            claim.PackageName, claim.RevisionId);
    }
}