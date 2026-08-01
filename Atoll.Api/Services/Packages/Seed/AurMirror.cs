using System.Formats.Tar;
using System.Text;
using CliWrap;
using CliWrap.Exceptions;

namespace Atoll.Api.Services.Packages.Seed;

public class AurMirror : IAurMirror
{
    private const string RemoteName = "origin";
    private const string LocalRefNamespace = "refs/atoll/";

    private static readonly IReadOnlyDictionary<string, string?> NoPromptEnv = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_ASKPASS"] = "/bin/true"
    };

    private readonly string _cachePath;
    private readonly ILogger<AurMirror> _logger;

    private readonly string _mirrorUrl;

    public AurMirror(string mirrorUrl, string cachePath, ILogger<AurMirror> logger)
    {
        if (string.IsNullOrWhiteSpace(mirrorUrl))
            throw new ArgumentException("Mirror URL is required.", nameof(mirrorUrl));

        _mirrorUrl = mirrorUrl;
        _cachePath = Path.GetFullPath(cachePath);
        _logger = logger;
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_cachePath);

        var headPath = Path.Combine(_cachePath, "HEAD");
        if (!File.Exists(headPath))
            await RunGitAsync(["init", "--bare", "--quiet", _cachePath], Directory.GetCurrentDirectory(), ct);

        var remotes = await RunGitAsync(["remote"], _cachePath, ct);
        if (ContainsRemote(remotes, RemoteName))
            await RunGitAsync(["remote", "set-url", RemoteName, _mirrorUrl], _cachePath, ct);
        else
            await RunGitAsync(["remote", "add", RemoteName, _mirrorUrl], _cachePath, ct);
    }

    public async Task<IReadOnlySet<string>> ListBranchesAsync(CancellationToken ct = default)
    {
        // protocol v2 keeps the advertisement compact; --heads limits to branch refs.
        var output = await RunGitAsync(["-c", "protocol.version=2", "ls-remote", "--heads", RemoteName],
            _cachePath, ct);

        var branches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: "<sha>\trefs/heads/<pkgbase>"
            var tab = line.IndexOf('\t');
            if (tab < 0 || tab + 1 >= line.Length) continue;

            var refName = line.AsSpan(tab + 1);
            const string prefix = "refs/heads/";
            if (!refName.StartsWith(prefix)) continue;

            var pkgBase = refName[prefix.Length..].ToString();
            if (!string.IsNullOrEmpty(pkgBase))
                branches.Add(pkgBase);
        }

        return branches;
    }

    public async Task<BulkFetchResult> FetchAsync(IReadOnlyList<string> pkgBases, CancellationToken ct = default)
    {
        if (pkgBases.Count == 0) return new BulkFetchResult([], []);

        try
        {
            await FetchBatchCoreAsync(pkgBases, ct);
            return new BulkFetchResult([..pkgBases], []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Batch of {Count} refs failed atomically; bisecting to isolate bad refs.",
                pkgBases.Count);
            return await BisectAsync(pkgBases, ct);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadFilesAsync(string pkgBase, CancellationToken ct = default)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        using var buffer = new MemoryStream();
        var archiveCmd = Cli.Wrap("git")
            .WithWorkingDirectory(_cachePath)
            .WithArguments(["archive", "--format=tar", LocalRefNamespace + pkgBase])
            .WithEnvironmentVariables(NoPromptEnv)
            .WithStandardOutputPipe(PipeTarget.ToStream(buffer))
            .WithValidation(CommandResultValidation.ZeroExitCode);

        try
        {
            await archiveCmd.ExecuteAsync(ct);
        }
        catch (CommandExecutionException ex)
        {
            throw new InvalidOperationException($"git archive for pkgbase '{pkgBase}' failed: {ex.Message}", ex);
        }

        buffer.Position = 0;
        await using var reader = new TarReader(buffer);
        while (await reader.GetNextEntryAsync(false, ct) is { } entry)
        {
            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile) continue;

            var name = entry.Name;
            if (string.IsNullOrEmpty(name)) continue;

            // Skip Git-internal files from paths; branches should hold only working files but be defensive.
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("/.git", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("/.git/", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = entry.DataStream;
            if (data is null) continue;

            using var streamReader = new StreamReader(data, leaveOpen: false);
            files[name] = await streamReader.ReadToEndAsync(ct);
        }

        return files;
    }

    private async Task<BulkFetchResult> BisectAsync(IReadOnlyList<string> pkgBases, CancellationToken ct)
    {
        if (pkgBases.Count == 1)
        {
            _logger.LogWarning("Ref refs/heads/{PkgBase} could not be fetched; skipping.", pkgBases[0]);
            return new BulkFetchResult([], pkgBases);
        }

        var mid = pkgBases.Count / 2;
        var left = pkgBases.Take(mid).ToArray();
        var right = pkgBases.Skip(mid).ToArray();

        var leftResult = await FetchAsync(left, ct);
        var rightResult = await FetchAsync(right, ct);

        return new BulkFetchResult(
            [..leftResult.Succeeded, ..rightResult.Succeeded],
            [..leftResult.Failed, ..rightResult.Failed]);
    }

    private Task<string> FetchBatchAsync(IReadOnlyList<string> pkgBases, CancellationToken ct)
    {
        // Explicit refspecs do two jobs: (1) protocol v2 emits ref-prefix filters so the server
        // doesn't re-advertise all ~95k refs each request, and (2) fetched branches are written
        // under refs/atoll/<pkgbase> so they stay addressable by name for file extraction.
        var args = new List<string>(7 + pkgBases.Count)
        {
            "-c", "protocol.version=2", "fetch", "--depth=1", "--no-tags", "--quiet", RemoteName
        };
        args.AddRange(pkgBases.Select(pkgBase => $"+refs/heads/{pkgBase}:{LocalRefNamespace}{pkgBase}"));

        return RunGitAsync([.. args], _cachePath, ct);
    }

    protected virtual Task FetchBatchCoreAsync(IReadOnlyList<string> pkgBases, CancellationToken ct)
    {
        return FetchBatchAsync(pkgBases, ct);
    }

    private static async Task<string> RunGitAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();

        var cmd = Cli.Wrap("git")
            .WithWorkingDirectory(workingDirectory)
            .WithArguments(arguments)
            .WithEnvironmentVariables(NoPromptEnv)
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(error));

        try
        {
            await cmd.ExecuteAsync(ct);
            return output.ToString();
        }
        catch (CommandExecutionException ex)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.ToString().Trim()}", ex);
        }
    }

    private static bool ContainsRemote(string remoteOutput, string remote)
    {
        return remoteOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Trim().Equals(remote, StringComparison.Ordinal));
    }
}