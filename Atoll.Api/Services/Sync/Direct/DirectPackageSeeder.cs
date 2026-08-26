using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Catalog.Indexing;

namespace Atoll.Api.Services.Sync.Direct;

public sealed class DirectPackageSeeder(
    IPackageRepository repo,
    PackageIndexStore indexStore,
    IAurPackageSource source,
    IPackageService packageService)
{
    public async Task SeedAsync(string packageName, CancellationToken ct = default)
    {
        if (await repo.ExistsAsync(packageName, ct))
            throw new PackageConflictException(packageName);

        var packageBase = ResolvePackageBase(packageName);
        var files = await source.FetchFilesAsync(packageBase, ct);
        await packageService.SeedFilesAsync(packageName, files);
    }

    internal string ResolvePackageBase(string packageName)
    {
        if (indexStore.Current.ByNames.TryGetValue(packageName, out var metadata)
            && !string.IsNullOrEmpty(metadata.PackageBase))
            return metadata.PackageBase;

        return packageName;
    }
}