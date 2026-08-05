using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Atoll.Api.Services.Packages.Git;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Security;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace Atoll.Api.Services.Packages;

public sealed class MongoPackageService(
    IPackageRepository repo,
    PackageIndexStore indexStore,
    IOptions<AtollOptions> options,
    IPackageSecurityRepository securityRepository,
    ILogger<MongoPackageService> logger) : IPackageService
{
    private const int MongoMaxDocumentSizeBytes = 16 * 1024 * 1024;

    // Per-file BSON overhead: hash string, size field, element names, and framing.
    private const int FileEntryOverheadBytes = 160;

    // Revision-document-level overhead: identifiers, timestamps, and revision metadata.
    private const int DocumentOverheadBytes = 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks = new();
    private readonly AtollOptions _options = options.Value;

    public Task<IReadOnlyList<string>> ListAsync()
    {
        return repo.ListAsync();
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

    public Task DeleteAsync(string packageName)
    {
        return repo.DeleteAsync(packageName);
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
        var root = _options.Git.RepositoriesPath;
        return string.IsNullOrWhiteSpace(root)
            ? null
            : Path.GetFullPath(Path.Combine(root, packageName + ".git"));
    }

    public async Task EnsureGitRepositoryAsync(string packageName, CancellationToken ct = default)
    {
        var path = GetRepositoryPath(packageName);
        if (path is null)
            return;

        var doc = await repo.GetHeadAsync(packageName, ct);
        if (doc is null)
            return;

        var marker = Path.Combine(path, ".atoll-head");
        var headMarker = doc.HeadRevisionId;

        if (IsUpToDate(path, marker, headMarker))
            return;

        var lockObj = RepoLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync(ct);
        try
        {
            if (IsUpToDate(path, marker, headMarker))
                return;

            Directory.CreateDirectory(path);

            if (!File.Exists(Path.Combine(path, "HEAD")))
            {
                string[] arguments = ["init", "--bare", "--quiet"];
                await GitClient.ExecuteAsync(path, arguments, null, null, ct);
            }

            var parent = string.Empty;
            var complete = true;
            foreach (var revision in doc.Revisions.OrderBy(r => r.CreatedAt))
            {
                var content = await repo.GetRevisionAsync(packageName, revision.RevisionId, ct);
                if (content is null)
                {
                    logger.LogError(
                        "Revision content for {PackageName} revision {RevisionId} is missing; materializing the remaining history.",
                        packageName, revision.RevisionId);
                    complete = false;
                    continue;
                }

                var tree = await WriteTreeAsync(path, content.Files, ct);
                parent = await WriteCommitAsync(path, tree, parent, revision, ct);
            }

            if (!string.IsNullOrEmpty(parent))
            {
                string[] arguments = ["update-ref", "refs/heads/main", parent];
                await GitClient.ExecuteAsync(path, arguments, null, null, ct);
            }

            string[] arguments1 = ["symbolic-ref", "HEAD", "refs/heads/main"];
            await GitClient.ExecuteAsync(path, arguments1, null, null, ct);

            if (complete)
                await File.WriteAllTextAsync(marker, headMarker, ct);
        }
        finally
        {
            lockObj.Release();
        }
    }

    public async Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
    {
        var packageFiles = BuildAndValidatePackageFiles(files);
        var revisionId = ComputeRevisionId(packageName, packageFiles);
        var now = DateTimeOffset.UtcNow;

        var revision = new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(packageName, revisionId),
            PackageName = packageName,
            RevisionId = revisionId,
            CreatedAt = now,
            Author = "aur",
            Message = "seed from AUR",
            Files = packageFiles
        };

        ThrowIfRevisionDocumentTooLarge(packageName, revision);

        var doc = new PackageDocument
        {
            Id = packageName,
            PackageName = packageName,
            CreatedAt = now,
            UpdatedAt = now,
            HeadRevisionId = revisionId,
            Revisions =
            [
                new PackageRevisionDocument
                {
                    RevisionId = revisionId,
                    CreatedAt = now,
                    Author = "aur",
                    Message = "seed from AUR"
                }
            ]
        };

        await repo.InsertSeedAsync(doc, revision);
        await securityRepository.MarkPendingAsync(packageName, revisionId, true);
    }

    public async Task<bool> AppendRevisionFromUpstreamAsync(
        string packageName,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct = default)
    {
        var packageFiles = BuildAndValidatePackageFiles(files);
        var revisionId = ComputeRevisionId(packageName, packageFiles);

        var current = await repo.GetHeadAsync(packageName, ct);
        if (current is null)
            throw new KeyNotFoundException($"Package '{packageName}' not found.");

        if (revisionId == current.HeadRevisionId)
            return false;

        var now = DateTimeOffset.UtcNow;
        var revision = new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(packageName, revisionId),
            PackageName = packageName,
            RevisionId = revisionId,
            CreatedAt = now,
            Author = "aur",
            Message = "refresh from AUR",
            Files = packageFiles
        };

        ThrowIfRevisionDocumentTooLarge(packageName, revision);

        await repo.AppendRevisionAsync(packageName, revision, _options.Mongo.MaxRevisions, ct);

        await securityRepository.MarkPendingAsync(packageName, revisionId, true, ct);
        await securityRepository.PromoteHeadAsync(packageName, revisionId, ct);

        return true;
    }

    internal string ResolvePackageBase(string packageName)
    {
        if (indexStore.Current.ByNames.TryGetValue(packageName, out var metadata)
            && !string.IsNullOrEmpty(metadata.PackageBase))
            return metadata.PackageBase;

        return packageName;
    }

    private static bool IsUpToDate(string path, string marker, string headMarker)
    {
        return Directory.Exists(path)
               && File.Exists(Path.Combine(path, "HEAD"))
               && File.Exists(marker)
               && File.ReadAllText(marker) == headMarker;
    }

    private static async Task<string> WriteTreeAsync(
        string repoPath,
        IReadOnlyDictionary<string, PackageFile> files,
        CancellationToken ct)
    {
        using var tempIndex = new TempFile();
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex.Path };

        await GitClient.ExecuteAsync(repoPath, ["read-tree", "--empty"], null, env, ct);

        foreach (var (name, file) in files)
        {
            var blob = (await GitClient.ExecuteAsync(repoPath, ["hash-object", "--stdin", "-w"], file.Content, env, ct)).Trim();

            var mode = IsExecutable(name, file.Content) ? "100755" : "100644";

            await GitClient.ExecuteAsync(repoPath, ["update-index", "--add", "--cacheinfo", mode, blob, name], null, env, ct);
        }

        return (await GitClient.ExecuteAsync(repoPath, ["write-tree"], null, env, ct)).Trim();
    }

    private static bool IsExecutable(string name, string? content)
    {
        if (name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrEmpty(content) && content.StartsWith("#!", StringComparison.Ordinal);
    }

    private static async Task<string> WriteCommitAsync(
        string repoPath,
        string treeSha,
        string parent,
        PackageRevisionDocument revision,
        CancellationToken ct)
    {
        using var messageFile = new TempFile();
        await File.WriteAllTextAsync(messageFile.Path, revision.Message, ct);

        string[] args = string.IsNullOrEmpty(parent)
            ? ["commit-tree", treeSha, "-F", messageFile.Path]
            : ["commit-tree", treeSha, "-p", parent, "-F", messageFile.Path];

        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = SanitizeIdent(revision.Author),
            ["GIT_AUTHOR_EMAIL"] = $"{SanitizeIdent(revision.Author)}@atoll.local",
            ["GIT_COMMITTER_NAME"] = "atoll",
            ["GIT_COMMITTER_EMAIL"] = "atoll@local"
        };

        if (revision.CreatedAt != default)
        {
            var unix = revision.CreatedAt.ToUnixTimeSeconds().ToString();
            env["GIT_AUTHOR_DATE"] = unix;
            env["GIT_COMMITTER_DATE"] = unix;
        }

        return (await GitClient.ExecuteAsync(repoPath, args, null, env, ct)).Trim();
    }

    private static string SanitizeIdent(string value)
    {
        var sanitized = value.Trim()
            .Where(c => c is not ('<' or '>' or '\n' or '\r'))
            .ToString();

        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
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

    private static void ThrowIfRevisionDocumentTooLarge(string packageName, PackageRevisionContentDocument revision)
    {
        if (EstimateSerializedSizeBound(revision.Files) <= MongoMaxDocumentSizeBytes)
            return;

        var serializedSizeBytes = revision.ToBson().LongLength;
        if (serializedSizeBytes > MongoMaxDocumentSizeBytes)
            throw new PackageDocumentTooLargeException(packageName, serializedSizeBytes, MongoMaxDocumentSizeBytes);
    }

    private static long EstimateSerializedSizeBound(IReadOnlyDictionary<string, PackageFile> files)
    {
        long contentBytes = 0;
        foreach (var (name, file) in files)
            contentBytes += file.Size + Encoding.UTF8.GetByteCount(name) + FileEntryOverheadBytes;

        return contentBytes + DocumentOverheadBytes;
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

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}