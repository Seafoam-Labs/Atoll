using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Git;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Atoll.Api.Tests;

[Category("RequiresGit")]
public class GitSmartHttpEndpointsTests
{
    private static readonly IReadOnlyDictionary<string, string> SampleFiles =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        };

    private HttpClient _client = null!;

    private GitTestFactory _factory = null!;
    private MongoPackageService _packages = null!;

    private static bool GitIsAvailable()
    {
        var (exitCode, _) = GitClient.TryExecuteAsync(["--version"], CancellationToken.None).Result;
        return exitCode == 0;
    }

    [SetUp]
    public void SetUp()
    {
        Assume.That(GitIsAvailable(), "git binary is required for these tests");
        _factory = new GitTestFactory();
        _client = _factory.CreateClient();
        _packages = (MongoPackageService)_factory.Services.GetRequiredService<IPackageService>();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task InfoRefs_unknown_package_returns_404()
    {
        var response = await _client.GetAsync("/packages/missing.git/info/refs?service=git-upload-pack");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task InfoRefs_rejects_non_upload_pack_service_with_403()
    {
        await _packages.SeedFilesAsync("shelly", SampleFiles);

        var response = await _client.GetAsync("/packages/shelly.git/info/refs?service=git-receive-pack");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task InfoRefs_returns_advertisement_with_expected_headers()
    {
        await _packages.SeedFilesAsync("shelly", SampleFiles);

        var response = await _client.GetAsync("/packages/shelly.git/info/refs?service=git-upload-pack");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo("application/x-git-upload-pack-advertisement"));
        Assert.That(response.Headers.CacheControl?.NoCache, Is.True,
            "Cache-Control: no-cache expected");

        var body = await response.Content.ReadAsByteArrayAsync();
        var text = Encoding.ASCII.GetString(body);
        Assert.That(text, Does.StartWith("001e# service=git-upload-pack\n"),
            "expected pkt-line service prelude");
        Assert.That(text, Does.Contain("refs/heads/main"));
    }

    [Test]
    public async Task UploadPack_unknown_package_returns_404()
    {
        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");

        var response = await _client.PostAsync("/packages/missing.git/git-upload-pack", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UploadPack_stateless_request_returns_result_content_type()
    {
        await _packages.SeedFilesAsync("shelly", SampleFiles);

        var adv = await _client.GetAsync("/packages/shelly.git/info/refs?service=git-upload-pack");
        var advBody = Encoding.ASCII.GetString(await adv.Content.ReadAsByteArrayAsync());
        var sha = ExtractHeadSha(advBody);
        Assert.That(sha, Is.Not.Null, "could not extract advertised HEAD sha");

        var requestBody = EncodePacketLine($"want {sha}\n") + "0000" + EncodePacketLine("done\n");
        using var content = new ByteArrayContent(Encoding.ASCII.GetBytes(requestBody));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");

        var response = await _client.PostAsync("/packages/shelly.git/git-upload-pack", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/x-git-upload-pack-result"));
        Assert.That(response.Headers.CacheControl?.NoCache, Is.True);

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.That(body.Length, Is.GreaterThan(0));
    }

    private static string EncodePacketLine(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line);
        var length = (bytes.Length + 4).ToString("x4");
        return length + line;
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
}