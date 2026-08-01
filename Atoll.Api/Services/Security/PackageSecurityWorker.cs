using Atoll.Api.Services.Packages;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Security;

public sealed class PackageSecurityWorker(
    IPackageRepository packageRepository,
    IPackageSecurityRepository securityRepo,
    IPackageSecurityScanner scanner,
    IOptions<AtollOptions> options,
    ILogger<PackageSecurityWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly SecurityOptions _security = options.Value.Security;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_security.Enabled)
        {
            logger.LogInformation("Package security scanning is disabled.");
            return;
        }

        await EnsureExistingPackagesArePendingAsync(stoppingToken);

        var pollInterval = TimeSpan.FromMilliseconds(_security.PollIntervalMs);
        logger.LogInformation(
            "Package security scanning started (poll {PollIntervalMs} ms, concurrency {Concurrency}).",
            _security.PollIntervalMs, _security.ScannerConcurrency);

        var workers = Enumerable.Range(0, _security.ScannerConcurrency)
            .Select(_ => PollLoopAsync(pollInterval, stoppingToken));
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
                var package = await packageRepository.GetHeadFilesAsync(packageName, token);
                if (package is not null)
                    await securityRepo.EnsurePendingAsync(packageName, package.HeadRevisionId, token);
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

    private async Task<bool> ClaimAndScanOneAsync(CancellationToken ct)
    {
        var claim = await securityRepo.TryClaimPendingScanAsync(_owner, LeaseDuration, ct);
        if (claim is null)
            return false;

        try
        {
            var package = await packageRepository.GetHeadFilesAsync(claim.Id, ct);
            if (package is null)
            {
                await securityRepo.ReleaseScanClaimAsync(claim.Id, _owner, ct);
                return true;
            }

            // A refresh can replace the head after this job was claimed. Keep
            // the newer revision pending; it must never inherit this result.
            if (package.HeadRevisionId != claim.RevisionId)
            {
                await securityRepo.MarkPendingAsync(claim.Id, package.HeadRevisionId, ct);
                return true;
            }

            var files = package.Files.ToDictionary(kv => kv.Key, kv => kv.Value.Content, StringComparer.Ordinal);
            var result = scanner.Scan(files);
            await securityRepo.CompleteScanAsync(claim.Id, claim.RevisionId, _owner, result, ct);

            if (result.Status == SecurityStatus.Flagged)
                logger.LogWarning(
                    "Security scan flagged {PackageName} revision {RevisionId}: {FindingCount} findings.",
                    claim.Id, claim.RevisionId, result.Findings.Count);
            else
                logger.LogDebug(
                    "Security scan for {PackageName} revision {RevisionId} -> {Status} ({FindingCount} findings).",
                    claim.Id, claim.RevisionId, result.Status, result.Findings.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ReleaseClaimQuietlyAsync(claim.Id);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Security scan failed for {PackageName}; marking as Error.", claim.Id);
            await MarkScanErrorQuietlyAsync(claim);
        }

        return true;
    }

    private async Task ReleaseClaimQuietlyAsync(string packageName)
    {
        try
        {
            await securityRepo.ReleaseScanClaimAsync(packageName, _owner, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to release security scan claim for {PackageName}.", packageName);
        }
    }

    private async Task MarkScanErrorQuietlyAsync(PackageSecurityScanDocument claim)
    {
        try
        {
            await securityRepo.MarkScanErrorAsync(claim.Id, claim.RevisionId, _owner, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to mark {PackageName} as security Error.", claim.Id);
        }
    }
}