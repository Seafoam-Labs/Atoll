using System.Diagnostics;
using System.Text;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Sync.Mirror;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atoll.Api.Tests.Sync.Mirror;

public sealed class AurMirrorGitIntegrationTests : IDisposable
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
    public async Task FetchAsync_InitializedMirrorWithBranch_ListsFetchesAndReadsFiles()
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
            Assert.Contains("pkgname=alpha", Encoding.UTF8.GetString(files["PKGBUILD"]), StringComparison.Ordinal);
            Assert.Contains("pkgbase", Encoding.UTF8.GetString(files[".SRCINFO"]), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FetchAsync_MissingRef_ReportsFailedWithoutThrowing()
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

    [Fact]
    public async Task ReadFilesAsync_BinaryBomUtf16AndEmptyFiles_PreserveExactBytesAcrossPaths()
    {
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xFE, 0x80, 0x42
        ];
        byte[] utf8Bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("pkgname=bom\n")];
        byte[] utf16Le = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("pkgname=utf16\n")];
        byte[] empty = [];

        CreateUpstreamWithBranch(
            "binary",
            ("icon64.png", png),
            ("bom.txt", utf8Bom),
            ("utf16le.txt", utf16Le),
            ("empty.txt", empty));

        var mirror = new AurMirror(_upstream, _cache, NullLogger<AurMirror>.Instance);
        await mirror.EnsureInitializedAsync(CancellationToken.None);
        await mirror.FetchAsync(["binary"], CancellationToken.None);
        var mirrored = await mirror.ReadFilesAsync("binary", CancellationToken.None);

        // The direct path reads the same tree from a checkout; both paths must agree byte-for-byte.
        var checkout = Path.Combine(_scratch, "checkout");
        Directory.CreateDirectory(checkout);
        foreach (var (name, content) in new[]
                 {
                     ("icon64.png", png),
                     ("bom.txt", utf8Bom),
                     ("utf16le.txt", utf16Le),
                     ("empty.txt", empty)
                 })
            await File.WriteAllBytesAsync(Path.Combine(checkout, name), content, CancellationToken.None);

        var direct = await AurGitPackageSource.ReadFilesAsync(checkout, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(png, mirrored["icon64.png"]);
            Assert.Equal(utf8Bom, mirrored["bom.txt"]);
            Assert.Equal(utf16Le, mirrored["utf16le.txt"]);
            Assert.Empty(mirrored["empty.txt"]);
            foreach (var name in new[] { "icon64.png", "bom.txt", "utf16le.txt", "empty.txt" })
                Assert.Equal(mirrored[name], direct[name]);
        });
    }

    private void CreateUpstreamWithBranch(string branch, params (string Name, string Content)[] files)
    {
        CreateUpstreamWithBranch(
            branch,
            [.. files.Select(file => (file.Name, Encoding.UTF8.GetBytes(file.Content)))]);
    }

    private void CreateUpstreamWithBranch(string branch, params (string Name, byte[] Content)[] files)
    {
        RunGit(_scratch, ["init", "--bare", "--quiet", _upstream]);

        var work = Path.Combine(_scratch, $"work-{branch}");
        Directory.CreateDirectory(work);
        RunGit(work, ["init", "--quiet"]);
        RunGit(work, ["config", "user.email", "it@atoll.local"]);
        RunGit(work, ["config", "user.name", "it"]);

        foreach (var (name, content) in files)
            File.WriteAllBytes(Path.Combine(work, name), content);

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