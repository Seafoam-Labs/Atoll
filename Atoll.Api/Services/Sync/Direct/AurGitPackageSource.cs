using Atoll.Api.Services.Git;

namespace Atoll.Api.Services.Sync.Direct;

public sealed class AurGitPackageSource : IAurPackageSource
{
    public async Task<IReadOnlyDictionary<string, byte[]>> FetchFilesAsync(
        string packageBase, CancellationToken ct = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"atoll-{packageBase}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempPath);
            await GitClient.CloneAsync($"https://aur.archlinux.org/{packageBase}.git", tempPath, ct);
            return await ReadFilesAsync(tempPath, ct);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }

    internal static async Task<Dictionary<string, byte[]>> ReadFilesAsync(string workDir, CancellationToken ct)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workDir, path).Replace('\\', '/');

            if (relative.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
                continue;

            files[relative] = await File.ReadAllBytesAsync(path, ct);
        }

        return files;
    }
}