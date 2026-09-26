using Atoll.Api.Services.Packages.Persistence;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Packages;

/// <summary>
///     Brings stored revision history down to <c>Atoll:Mongo:MaxRevisions</c> once per start. Append-time
///     trimming only bounds packages that change, so a lowered cap needs this sweep to take effect.
/// </summary>
public sealed class PackageRevisionCompactionWorker(
    IPackageRepository repository,
    IOptions<AtollOptions> options,
    ILogger<PackageRevisionCompactionWorker> logger)
    : BackgroundService
{
    private readonly int _maxRevisions = options.Value.Mongo.MaxRevisions;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var trimmed = await CompactAsync(stoppingToken);
            if (trimmed > 0)
            {
                logger.LogInformation(
                    "Trimmed revision history to {MaxRevisions} for {PackageCount} package(s).",
                    _maxRevisions, trimmed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Revision compaction pass stopped by shutdown.");
        }
        catch (Exception ex)
        {
            // Best effort: maintenance must not take the app down, and the next start retries.
            logger.LogWarning(ex,
                "Revision compaction pass failed; packages above {MaxRevisions} revisions stay untrimmed until the next start.",
                _maxRevisions);
        }
    }

    internal Task<long> CompactAsync(CancellationToken ct)
    {
        return repository.TrimExcessRevisionsAsync(_maxRevisions, ct);
    }
}