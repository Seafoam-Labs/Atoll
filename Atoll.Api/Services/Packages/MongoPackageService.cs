using System.Security.Cryptography;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Options;
using static CliWrap.CommandResultValidation;

namespace Atoll.Api.Services.Packages;

public sealed class MongoPackageService(
    IPackageRepository repo,
    IOptions<AtollOptions> options) : IPackageService
{
    private readonly AtollOptions _options = options.Value;

    public Task<IReadOnlyList<string>> ListAsync()
    {
        return repo.ListAsync();
    }

    public Task<bool> ExistsAsync(string packageName)
    {
        return repo.ExistsAsync(packageName);
    }

    public async Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
    {
        if (string.IsNullOrEmpty(commitSha))
        {
            var doc = await repo.GetHeadAsync(packageName)
                      ?? throw new KeyNotFoundException($"Package '{packageName}' not found.");
            return ToPackageFiles(doc.Files);
        }

        var rev = await repo.GetRevisionAsync(packageName, commitSha)
                  ?? throw new KeyNotFoundException(
                      $"Revision '{commitSha}' not found for package '{packageName}'.");

        return ToPackageFiles(rev.Files);
    }

    public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
    {
        return repo.GetHistoryAsync(packageName);
    }

    public Task DeleteAsync(string packageName)
    {
        return repo.DeleteAsync(packageName);
    }

    public async Task SeedFromAurAsync(string packageName)
    {
        if (await repo.ExistsAsync(packageName))
            throw new PackageConflictException(packageName);

        var tempPath = Path.Combine(Path.GetTempPath(), $"atoll-{packageName}-{Guid.NewGuid():N}");
        Dictionary<string, string> files;
        try
        {
            Directory.CreateDirectory(tempPath);
            await CloneAurAsync(packageName, tempPath);
            files = await ReadFilesAsync(tempPath);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }

        await SeedFilesAsync(packageName, files);
    }

    public Task SyncFromStorageAsync(string packageName)
    {
        return Task.CompletedTask;
    }

    public Task SyncToStorageAsync(string packageName)
    {
        return Task.CompletedTask;
    }

    public string? GetRepositoryPath(string packageName)
    {
        return null;
    }

    internal async Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
    {
        var packageFiles = BuildAndValidatePackageFiles(files);
        var revisionId = ComputeRevisionId(packageName, packageFiles);
        var now = DateTimeOffset.UtcNow;

        var revision = new PackageRevisionDocument
        {
            RevisionId = revisionId,
            CreatedAt = now,
            Author = "aur",
            Message = "seed from AUR",
            Files = packageFiles
        };

        var doc = new PackageDocument
        {
            Id = packageName,
            PackageName = packageName,
            CreatedAt = now,
            UpdatedAt = now,
            HeadRevisionId = revisionId,
            Files = packageFiles,
            Revisions = [revision]
        };

        await repo.InsertSeedAsync(doc);
    }

    private Dictionary<string, PackageFile> BuildAndValidatePackageFiles(
        IReadOnlyDictionary<string, string> files)
    {
        var maxFileBytes = _options.Mongo.MaxFileBytes;
        var result = new Dictionary<string, PackageFile>(files.Count);

        foreach (var (name, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            if (bytes.Length > maxFileBytes)
                throw new InvalidOperationException(
                    $"File '{name}' is {bytes.Length} bytes which exceeds the per-file limit of {maxFileBytes} bytes.");

            var hash = SHA256.HashData(bytes);
            result[name] = new PackageFile
            {
                Content = content,
                Size = bytes.Length,
                Hash = $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"
            };
        }

        return result;
    }

    private static string ComputeRevisionId(
        string packageName,
        IReadOnlyDictionary<string, PackageFile> files)
    {
        var builder = new StringBuilder();
        builder.Append(packageName);

        foreach (var (name, file) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            builder.Append('\0').Append(name).Append('\0').Append(file.Hash);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task CloneAurAsync(string packageName, string workDir)
    {
        await Cli.Wrap("git")
            .WithArguments(["clone", $"https://aur.archlinux.org/{packageName}.git", workDir])
            .WithValidation(ZeroExitCode)
            .ExecuteAsync();
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