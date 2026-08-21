using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Security;

namespace Atoll.Api.Services.Ui;

public sealed record PackageDetails(
    AurPackageMetadata Metadata,
    PackageDocument? Head,
    SecurityAccessResult Access,
    PackageSecurityScanDocument? HeadScan,
    IReadOnlyList<PackageSecurityScanDocument> Scans)
{
    public bool IsSeeded => Head is not null;
}

public sealed class PackageDetailsService(
    PackageIndexStore indexStore,
    IPackageRepository packageRepository,
    IPackageSecurityRepository securityRepository,
    IPackageSecurityAccess securityAccess)
{
    public async Task<PackageDetails?> GetAsync(string name, CancellationToken ct = default)
    {
        if (!indexStore.Current.ByNames.TryGetValue(name, out var metadata))
            return null;

        var head = await packageRepository.GetHeadAsync(name, ct);
        if (head is null)
            return new PackageDetails(metadata, null, SecurityAccessResult.Allow(), null, []);

        var headScan = await securityRepository.GetHeadAsync(name, ct);
        var scans = (await securityRepository.ListForPackageAsync(name, ct))
            .OrderByDescending(scan => scan.IsHead)
            .ThenByDescending(scan => scan.ScannedAt)
            .ToList();
        var access = await securityAccess.CheckAsync(name, null, ct);

        return new PackageDetails(metadata, head, access, headScan, scans);
    }
}
