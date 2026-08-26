using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Fakes;

internal sealed class InMemorySeedExclusionRepository : ISeedExclusionRepository
{
    private readonly Dictionary<string, SeedExclusionDocument> _exclusions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<IReadOnlySet<string>> ListDocumentTooLargePackageBasesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlySet<string> result = new HashSet<string>(
                _exclusions.Values
                    .Where(x => x.Reason == SeedExclusionReasons.DocumentTooLarge)
                    .Select(x => x.PackageBase),
                StringComparer.Ordinal);

            return Task.FromResult(result);
        }
    }

    public Task RecordDocumentTooLargeAsync(
        string packageBase,
        IReadOnlyList<string> packageNames,
        long serializedSizeBytes,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_exclusions.TryGetValue(packageBase, out var existing))
                _exclusions[packageBase] = new SeedExclusionDocument
                {
                    Id = packageBase,
                    PackageBase = packageBase,
                    PackageNames = [.. packageNames],
                    Reason = SeedExclusionReasons.DocumentTooLarge,
                    SerializedSizeBytes = serializedSizeBytes,
                    FirstSeenUtc = existing.FirstSeenUtc,
                    LastSeenUtc = now
                };
            else
                _exclusions[packageBase] = new SeedExclusionDocument
                {
                    Id = packageBase,
                    PackageBase = packageBase,
                    PackageNames = [.. packageNames],
                    Reason = SeedExclusionReasons.DocumentTooLarge,
                    SerializedSizeBytes = serializedSizeBytes,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                };

            return Task.CompletedTask;
        }
    }
}