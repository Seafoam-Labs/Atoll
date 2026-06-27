using System.Globalization;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using static CliWrap.CommandResultValidation;

namespace Atoll.Api.Services.Aur;

///<remarks>not thread safe</remarks>
public sealed class GitPackageRepository : IPackageRepository
{
    private const string Repos = "repos";

    private readonly string _basePath;
    private readonly IBundleStorage _storage;

    public GitPackageRepository(IBundleStorage storage, IOptions<AtollOptions> options)
    {
        _storage = storage;
        _basePath = options.Value.Storage.Local.DataPath;
        Directory.CreateDirectory(Path.Combine(_basePath, Repos));
    }

    public async Task<IReadOnlyList<string>> ListAsync()
    {
        return await _storage.ListAsync();
    }

    public async Task<bool> ExistsAsync(string packageName)
    {
        if (Directory.Exists(RepoPath(packageName))) return true;
        return await _storage.ExistsAsync(packageName);
    }

    public async Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
    {
        await EnsureLocalRepoAsync(packageName);

        var sha = commitSha ?? "HEAD";
        var repoPath = RepoPath(packageName);
        var result = await RunGitAsync(["ls-tree", "-r", "--name-only", sha], repoPath);

        var files = new Dictionary<string, string>();
        foreach (var file in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var content = await RunGitAsync(["show", $"{sha}:{file}"], repoPath);
            files[file] = content.StandardOutput;
        }

        return new PackageFiles(files);
    }

    public async Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
    {
        await EnsureLocalRepoAsync(packageName);

        var result = await RunGitAsync(["log", "--pretty=format:%H%x00%aI%x00%an%x00%s"], RepoPath(packageName));

        return result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Chunk(4)
            .Select(parts => new PackageVersion(
                parts[0],
                DateTimeOffset.ParseExact(parts[1], "o", CultureInfo.InvariantCulture, DateTimeStyles.None),
                parts[3],
                parts[2]))
            .ToList();
    }

    public async Task DeleteAsync(string packageName)
    {
        DeleteDirectory(RepoPath(packageName));
        await _storage.DeleteAsync(packageName);
    }

    public async Task SeedFromAurAsync(string packageName)
    {
        if (await ExistsAsync(packageName)) return;

        var repoPath = RepoPath(packageName);
        try
        {
            DeleteDirectory(repoPath);
            await RunGitAsync(["clone", "--mirror", $"https://aur.archlinux.org/{packageName}.git", repoPath]);
            await CreateAndUploadBundleAsync(packageName);
        }
        catch
        {
            DeleteDirectory(repoPath);
            throw;
        }
    }

    public async Task SyncToStorageAsync(string packageName)
    {
        if (!Directory.Exists(RepoPath(packageName))) return;
        await CreateAndUploadBundleAsync(packageName);
    }

    public async Task SyncFromStorageAsync(string packageName)
    {
        var bundlePath = Path.Combine(_basePath, $"{packageName}.bundle");
        try
        {
            await _storage.DownloadAsync(packageName, bundlePath);

            DeleteDirectory(RepoPath(packageName));
            await RunGitAsync(["clone", "--mirror", bundlePath], RepoPath(packageName));
        }
        finally
        {
            File.Delete(bundlePath);
        }
    }

    private string RepoPath(string packageName)
    {
        return Path.Combine(_basePath, Repos, $"{packageName}.git");
    }

    private async Task EnsureLocalRepoAsync(string packageName)
    {
        if (Directory.Exists(RepoPath(packageName))) return;
        await SyncFromStorageAsync(packageName);
    }

    private async Task CreateAndUploadBundleAsync(string packageName)
    {
        var packageBundle = $"{packageName}.bundle";
        var fullBundlePath = Path.Combine(RepoPath(packageName), packageBundle);
        try
        {
            await RunGitAsync(["bundle", "create", packageBundle, "--all"], RepoPath(packageName));
            await _storage.UploadAsync(packageName, fullBundlePath);
        }
        finally
        {
            File.Delete(fullBundlePath);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static async Task<BufferedCommandResult> RunGitAsync(
        string[] args,
        string? workDir = null,
        bool throwOnError = true)
    {
        var cmd = Cli.Wrap("git")
            .WithArguments(args)
            .WithValidation(throwOnError ? ZeroExitCode : None)
            .WithWorkingDirectory(workDir ?? Directory.GetCurrentDirectory());

        return await cmd.ExecuteBufferedAsync();
    }
}