using System.Globalization;
using System.Text;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Rpc;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Packages.Git;

[Trait("Category", "RequiresGit")]
public sealed class GitTransferServiceTests : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, string> SampleFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        };

    private static async Task<bool> GitIsAvailableAsync()
    {
        var (exitCode, _) = await GitClient.TryExecuteAsync(["--version"], CancellationToken.None);
        return exitCode == 0;
    }

    private static (GitTransferService git, PackageService packages, GitRepositoryCache cache, IPackageSecurityRepository security, string reposRoot)
        CreateServices()
    {
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var reposRoot = Path.Combine(Path.GetTempPath(), $"atoll-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reposRoot);
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = reposRoot }
        });
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var packages = new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache, TestHybridCache.New(), new PackageIndexStore());
        var git = new GitTransferService(repo, cache, new AurRpcService(new PackageSearchEngine(new PackageIndexStore())));
        return (git, packages, cache, security, reposRoot);
    }

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(await GitIsAvailableAsync(), "git binary is required for these tests");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AdvertiseRefsAsync_UnknownPackage_ReturnsNotFound()
    {
        var (git, _, _, _, reposRoot) = CreateServices();
        try
        {
            using var output = new MemoryStream();
            var result = await git.AdvertiseRefsAsync("missing", output, CancellationToken.None);
            Assert.IsType<GitTransferResult.NotFound>(result, exactMatch: false);
            Assert.Equal(0, output.Length);
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task UploadPackAsync_UnknownPackage_ReturnsNotFound()
    {
        var (git, _, _, _, reposRoot) = CreateServices();
        try
        {
            using var input = new MemoryStream();
            using var output = new MemoryStream();
            var result = await git.UploadPackAsync("missing", input, output, CancellationToken.None);
            Assert.IsType<GitTransferResult.NotFound>(result, exactMatch: false);
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task AdvertiseRefsAsync_SeededPackage_WritesPktLinePreludeAndRefs()
    {
        var (git, packages, cache, security, reposRoot) = CreateServices();
        try
        {
            await packages.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");

            using var output = new MemoryStream();
            var result = await git.AdvertiseRefsAsync("shelly", output, CancellationToken.None);

            Assert.IsType<GitTransferResult.Ok>(result, exactMatch: false);

            output.Position = 0;
            using var reader = new StreamReader(output, leaveOpen: false);
            var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

            Assert.StartsWith("001e# service=git-upload-pack\n", body, StringComparison.Ordinal);
            Assert.Contains("HEAD", body, StringComparison.Ordinal);
            Assert.Contains("refs/heads/main", body, StringComparison.Ordinal);
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task EnsureRepositoryAsync_SeededPackage_ClonesWithExpectedFiles()
    {
        var (git, packages, cache, security, reposRoot) = CreateServices();
        var cloneDir = Path.Combine(Path.GetTempPath(), $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            await packages.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");

            using var advOutput = new MemoryStream();
            await git.AdvertiseRefsAsync("shelly", advOutput, CancellationToken.None);

            await cache.EnsureRepositoryAsync("shelly", TestContext.Current.CancellationToken);
            var gitDir = cache.GetRepositoryPath("shelly")!;
            string[] args = ["clone", "--quiet", gitDir, cloneDir];
            await GitClient.ExecuteAsync(Directory.GetCurrentDirectory(), args, null, null, CancellationToken.None);

            foreach (var (name, content) in SampleFiles)
            {
                var fullPath = Path.Combine(cloneDir, name);
                Assert.True(File.Exists(fullPath), $"missing {name}");
                Assert.Equal(content, await File.ReadAllTextAsync(fullPath, TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            TryCleanup(reposRoot);
            TryCleanup(cloneDir);
        }
    }

    [Fact]
    public async Task UploadPackAsync_WantRequest_ReturnsOkWithBody()
    {
        var (git, packages, cache, security, reposRoot) = CreateServices();
        try
        {
            await packages.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");

            using var adv = new MemoryStream();
            await git.AdvertiseRefsAsync("shelly", adv, CancellationToken.None);
            adv.Position = 0;
            var advText = await new StreamReader(adv).ReadToEndAsync(TestContext.Current.CancellationToken);
            var sha = ExtractHeadSha(advText);
            Assert.NotNull(sha);

            var requestBody = EncodePacketLine($"want {sha}\n") + "0000" + EncodePacketLine("done\n");
            using var input = new MemoryStream(Encoding.ASCII.GetBytes(requestBody));
            using var output = new MemoryStream();

            var result = await git.UploadPackAsync("shelly", input, output, CancellationToken.None);
            Assert.IsType<GitTransferResult.Ok>(result, exactMatch: false);
            Assert.True(output.Length > 0, "expected upload-pack response body");
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task UploadPackAsync_UnknownWant_ReturnsProtocolErrPacket()
    {
        var (git, packages, cache, security, reposRoot) = CreateServices();
        try
        {
            await packages.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");

            const string bogus = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
            var requestBody = EncodePacketLine($"want {bogus}\n") + "0000" + EncodePacketLine("done\n");
            using var input = new MemoryStream(Encoding.ASCII.GetBytes(requestBody));
            using var output = new MemoryStream();

            var result = await git.UploadPackAsync("shelly", input, output, CancellationToken.None);

            Assert.IsType<GitTransferResult.Ok>(result, exactMatch: false);
            Assert.Equal(
                EncodePacketLine($"ERR upload-pack: not our ref {bogus}"),
                Encoding.ASCII.GetString(output.ToArray()));
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Fact]
    public async Task UploadPackAsync_FlushOnlyBody_ReturnsEmptyOk()
    {
        var (git, packages, cache, security, reposRoot) = CreateServices();
        try
        {
            await packages.SeedFilesAsync("shelly", SampleFiles);
            await security.MarkHeadVerifiedAsync("shelly");

            using var input = new MemoryStream(Encoding.ASCII.GetBytes("0000"));
            using var output = new MemoryStream();

            var result = await git.UploadPackAsync("shelly", input, output, CancellationToken.None);

            Assert.IsType<GitTransferResult.Ok>(result, exactMatch: false);
            Assert.Equal(0, output.Length);
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    private static string? ExtractHeadSha(string advertisement)
    {
        const string needle = " refs/heads/main";
        var idx = advertisement.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 1) return null;

        var shaStart = idx - 40;
        if (shaStart < 0) return null;

        var sha = advertisement.Substring(shaStart, 40);
        return sha.All(Uri.IsHexDigit) ? sha : null;
    }

    private static string EncodePacketLine(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line);
        var length = (bytes.Length + 4).ToString("x4", CultureInfo.InvariantCulture);
        return length + line;
    }

    private static void TryCleanup(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // ignore
        }
    }
}