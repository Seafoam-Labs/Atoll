using System.Text;
using Atoll.Api.Services.Git;

namespace Atoll.Api.Services.Sync.Direct;

public sealed class AurGitPackageSource : IAurPackageSource
{
    public async Task<IReadOnlyDictionary<string, string>> FetchFilesAsync(
        string packageBase, CancellationToken ct = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"atoll-{packageBase}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempPath);
            await GitClient.CloneAsync($"https://aur.archlinux.org/{packageBase}.git", tempPath, ct);
            return await ReadFilesAsync(tempPath);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
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
}