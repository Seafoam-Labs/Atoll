using System.Diagnostics;
using System.Text;
using Atoll.Api.Services.Sync.Mirror;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atoll.Api.Tests.Sync.Mirror;

public class AurMirrorGitIntegrationTests : IDisposable
{
    private readonly string _cache;
    private readonly string _scratch;
    private readonly string _upstream;

    public AurMirrorGitIntegrationTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), $"atoll-mirror-it-{Guid.NewGuid():N}");
        _upstream = Path.Combine(_scratch, "upstream.git");
        _cache = Path.Combine(_scratch, "mirror");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_scratch)) return;
        try
        {
            Directory.Delete(_scratch, true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public async Task ListBranches_fetch_and_read_files_round_trip()
    {
        CreateUpstreamWithBranch(
            "alpha",
            ("PKGBUILD", "pkgname=alpha\npkgver=1.0\n"),
            (".SRCINFO", "pkgbase = alpha\n"));

        var mirror = new AurMirror(_upstream, _cache, NullLogger<AurMirror>.Instance);

        await mirror.EnsureInitializedAsync(CancellationToken.None);

        var branches = await mirror.ListBranchesAsync(CancellationToken.None);
        Assert.Contains("alpha", branches);

        var result = await mirror.FetchAsync(["alpha"], CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "alpha" }, result.Succeeded, strict: true);
            Assert.Empty(result.Failed);
        });

        var files = await mirror.ReadFilesAsync("alpha", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "PKGBUILD", ".SRCINFO" }, files.Keys, strict: true);
            Assert.Contains("pkgname=alpha", files["PKGBUILD"]);
            Assert.Contains("pkgbase", files[".SRCINFO"]);
        });
    }

    [Fact]
    public async Task FetchAsync_reports_missing_ref_as_failed_without_throwing()
    {
        CreateUpstreamWithBranch(
            "real",
            ("PKGBUILD", "pkgname=real\n"));

        var mirror = new AurMirror(_upstream, _cache, NullLogger<AurMirror>.Instance);
        await mirror.EnsureInitializedAsync(CancellationToken.None);

        var result = await mirror.FetchAsync(["real", "ghost"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "real" }, result.Succeeded, strict: true);
            Assert.Equivalent(new[] { "ghost" }, result.Failed, strict: true);
        });
    }

    private void CreateUpstreamWithBranch(string branch, params (string Name, string Content)[] files)
    {
        RunGit(_scratch, ["init", "--bare", "--quiet", _upstream]);

        var work = Path.Combine(_scratch, $"work-{branch}");
        Directory.CreateDirectory(work);
        RunGit(work, ["init", "--quiet"]);
        RunGit(work, ["config", "user.email", "it@atoll.local"]);
        RunGit(work, ["config", "user.name", "it"]);

        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(work, name), content);

        RunGit(work, ["add", "-A"]);
        RunGit(work, ["commit", "--quiet", "-m", branch]);

        RunGit(work, ["branch", "-M", branch]);
        RunGit(work, ["remote", "add", "origin", _upstream]);
        RunGit(work, ["push", "--quiet", "origin", branch]);
    }

    private static void RunGit(string workingDirectory, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("could not start git");
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => stderr.Append(e.Data);
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {proc.ExitCode}): {stderr}");
    }
}