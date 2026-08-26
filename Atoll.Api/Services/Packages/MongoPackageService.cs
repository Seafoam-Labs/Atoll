using System.Text;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Security;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Packages;

public sealed class MongoPackageService(
    IPackageRepository repo,
    PackageIndexStore indexStore,
    IOptions<AtollOptions> options,
    IPackageSecurityRepository securityRepository,
    IGitRepositoryCache gitCache) : IPackageService
{
    private readonly AtollOptions _options = options.Value;

    public Task<IReadOnlyList<string>> ListAsync()
    {
        return repo.ListAsync();
    }

    public async Task<int> CountAsync()
    {
        return (int)await repo.CountAsync();
    }

    public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
    {
        return repo.ExistsAsync(packageName, ct);
    }

    public async Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
    {
        string revisionId;
        if (string.IsNullOrEmpty(commitSha))
        {
            var doc = await repo.GetHeadAsync(packageName) ?? throw new KeyNotFoundException($"Package '{packageName}' not found.");
            revisionId = doc.HeadRevisionId;
        }
        else
        {
            revisionId = commitSha;
        }

        var revision = await repo.GetRevisionAsync(packageName, revisionId) ??
                       throw new KeyNotFoundException($"Revision '{revisionId}' not found for package '{packageName}'.");

        return ToPackageFiles(revision.Files);
    }

    public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
    {
        return repo.GetHistoryAsync(packageName);
    }

    public Task DeleteAsync(string packageName, CancellationToken ct = default)
    {
        // The cache removes derived state (scan records, on-disk repository) before calling
        // back to delete the authoritative package document, holding the repository lock
        // throughout so concurrent materialization cannot resurrect the directory.
        return gitCache.DeleteAsync(packageName, token => repo.DeleteAsync(packageName, token), ct);
    }

    public async Task SeedFromAurAsync(string packageName)
    {
        if (await repo.ExistsAsync(packageName))
            throw new PackageConflictException(packageName);

        var packageBase = ResolvePackageBase(packageName);

        var tempPath = Path.Combine(Path.GetTempPath(), $"atoll-{packageName}-{Guid.NewGuid():N}");
        Dictionary<string, string> files;
        try
        {
            Directory.CreateDirectory(tempPath);
            await GitClient.CloneAsync($"https://aur.archlinux.org/{packageBase}.git", tempPath);
            files = await ReadFilesAsync(tempPath);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }

        await SeedFilesAsync(packageName, files);
    }

    public async Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
    {
        var snapshot = PackageSnapshotFactory.Create(
            packageName, files, _options.Mongo.MaxFileBytes, "aur", "seed from AUR");
        PackageDocumentSizeValidator.Validate(packageName, snapshot.Content);

        var doc = new PackageDocument
        {
            Id = packageName,
            PackageName = packageName,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.CreatedAt,
            HeadRevisionId = snapshot.RevisionId,
            Revisions = [snapshot.Metadata]
        };

        await repo.InsertSeedAsync(doc, snapshot.Content);
        await securityRepository.MarkPendingAsync(packageName, snapshot.RevisionId, true);
    }

    public async Task<bool> AppendRevisionFromUpstreamAsync(
        string packageName,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct = default)
    {
        var snapshot = PackageSnapshotFactory.Create(
            packageName, files, _options.Mongo.MaxFileBytes, "aur", "refresh from AUR");

        var current = await repo.GetHeadAsync(packageName, ct);
        if (current is null)
            throw new KeyNotFoundException($"Package '{packageName}' not found.");

        if (snapshot.RevisionId == current.HeadRevisionId)
            return false;

        PackageDocumentSizeValidator.Validate(packageName, snapshot.Content);

        await repo.AppendRevisionAsync(packageName, snapshot.Content, _options.Mongo.MaxRevisions, ct);

        await securityRepository.MarkPendingAsync(packageName, snapshot.RevisionId, true, ct);
        await securityRepository.PromoteHeadAsync(packageName, snapshot.RevisionId, ct);

        return true;
    }

    internal string ResolvePackageBase(string packageName)
    {
        if (indexStore.Current.ByNames.TryGetValue(packageName, out var metadata)
            && !string.IsNullOrEmpty(metadata.PackageBase))
            return metadata.PackageBase;

        return packageName;
    }

    private static async Task<Dictionary<string, string>> ReadFilesAsync(string workDir)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workDir, path).Replace('\\', '/');

            if (relative.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
                continue;

            var bytes = await File.ReadAllBytesAsync(path);
            files[relative] = Encoding.UTF8.GetString(bytes);
        }

        return files;
    }

    private static PackageFiles ToPackageFiles(IReadOnlyDictionary<string, PackageFile> files)
    {
        return new PackageFiles(files.ToDictionary(kv => kv.Key, kv => kv.Value.Content));
    }
}