using System.Text;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Git;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests;

[Category("RequiresGit")]
public class GitTransferServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> SampleFiles =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        };

    private static async Task<bool> GitIsAvailable()
    {
        var (exitCode, _) = await GitClient.TryExecuteAsync(["--version"], CancellationToken.None);
        return exitCode == 0;
    }

    private static (GitTransferService git, MongoPackageService packages, string reposRoot) CreateServices()
    {
        var repo = new InMemoryPackageRepository();
        var reposRoot = Path.Combine(Path.GetTempPath(), $"atoll-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reposRoot);
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = reposRoot }
        });
        var packages = new MongoPackageService(repo, options);
        var git = new GitTransferService(packages);
        return (git, packages, reposRoot);
    }

    [SetUp]
    public async Task SetUp()
    {
        Assume.That(await GitIsAvailable(), "git binary is required for these tests");
    }

    [Test]
    public async Task AdvertiseRefsAsync_unknown_package_returns_NotFound()
    {
        var (git, _, reposRoot) = CreateServices();
        try
        {
            using var output = new MemoryStream();
            var result = await git.AdvertiseRefsAsync("missing", output, CancellationToken.None);
            Assert.That(result, Is.InstanceOf<GitTransferResult.NotFound>());
            Assert.That(output.Length, Is.Zero);
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Test]
    public async Task UploadPackAsync_unknown_package_returns_NotFound()
    {
        var (git, _, reposRoot) = CreateServices();
        try
        {
            using var input = new MemoryStream();
            using var output = new MemoryStream();
            var result = await git.UploadPackAsync("missing", input, output, CancellationToken.None);
            Assert.That(result, Is.InstanceOf<GitTransferResult.NotFound>());
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Test]
    public async Task AdvertiseRefsAsync_writes_pkt_line_prelude_and_refs()
    {
        var (git, packages, reposRoot) = CreateServices();
        try
        {
            await packages.SeedFilesAsync("shelly", SampleFiles);

            using var output = new MemoryStream();
            var result = await git.AdvertiseRefsAsync("shelly", output, CancellationToken.None);

            Assert.That(result, Is.InstanceOf<GitTransferResult.Ok>());

            output.Position = 0;
            using var reader = new StreamReader(output, leaveOpen: false);
            var body = await reader.ReadToEndAsync();

            Assert.That(body, Does.StartWith("001e# service=git-upload-pack\n"),
                "expected service=git-upload-pack pkt-line prelude");
            Assert.That(body, Does.Contain("HEAD"));
            Assert.That(body, Does.Contain("refs/heads/main"));
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    [Test]
    public async Task UploadPackAsync_serves_a_full_clone_to_a_local_client()
    {
        var (git, packages, reposRoot) = CreateServices();
        var cloneDir = Path.Combine(Path.GetTempPath(), $"atoll-clone-{Guid.NewGuid():N}");
        try
        {
            await packages.SeedFilesAsync("shelly", SampleFiles);

            using var advOutput = new MemoryStream();
            await git.AdvertiseRefsAsync("shelly", advOutput, CancellationToken.None);

            await packages.EnsureGitRepositoryAsync("shelly");
            var gitDir = packages.GetRepositoryPath("shelly")!;
            string[] args = ["clone", "--quiet", gitDir, cloneDir];
            await GitClient.ExecuteAsync(Directory.GetCurrentDirectory(), args, input: null, env: null, CancellationToken.None);

            foreach (var (name, content) in SampleFiles)
            {
                var fullPath = Path.Combine(cloneDir, name);
                Assert.That(File.Exists(fullPath), Is.True, $"missing {name}");
                Assert.That(await File.ReadAllTextAsync(fullPath), Is.EqualTo(content));
            }
        }
        finally
        {
            TryCleanup(reposRoot);
            TryCleanup(cloneDir);
        }
    }

    [Test]
    public async Task UploadPackAsync_stateless_rpc_responds_to_want_request()
    {
        var (git, packages, reposRoot) = CreateServices();
        try
        {
            await packages.SeedFilesAsync("shelly", SampleFiles);

            using var adv = new MemoryStream();
            await git.AdvertiseRefsAsync("shelly", adv, CancellationToken.None);
            adv.Position = 0;
            var advText = await new StreamReader(adv).ReadToEndAsync();
            var sha = ExtractHeadSha(advText);
            Assert.That(sha, Is.Not.Null, "could not extract advertised HEAD sha");

            var requestBody = EncodePacketLine($"want {sha}\n") + "0000" + EncodePacketLine("done\n");
            using var input = new MemoryStream(Encoding.ASCII.GetBytes(requestBody));
            using var output = new MemoryStream();

            var result = await git.UploadPackAsync("shelly", input, output, CancellationToken.None);
            Assert.That(result, Is.InstanceOf<GitTransferResult.Ok>());
            Assert.That(output.Length, Is.GreaterThan(0), "expected upload-pack response body");
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
        var length = (bytes.Length + 4).ToString("x4");
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
            // best-effort cleanup
        }
    }
}