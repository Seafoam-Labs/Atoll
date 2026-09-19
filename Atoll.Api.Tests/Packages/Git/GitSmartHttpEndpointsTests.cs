using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atoll.Api.Tests.Packages.Git;

[Trait("Category", "RequiresGit")]
public class GitSmartHttpEndpointsTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> SampleFiles =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        };

    private readonly HttpClient _client;

    private readonly GitTestFactory _factory;
    private readonly PackageService _packages;

    private static bool GitIsAvailable()
    {
        var (exitCode, _) = GitClient.TryExecuteAsync(["--version"], CancellationToken.None).Result;
        return exitCode == 0;
    }

    public GitSmartHttpEndpointsTests()
    {
        Assert.SkipUnless(GitIsAvailable(), "git binary is required for these tests");
        _factory = new GitTestFactory();
        _client = _factory.CreateClient();
        _packages = (PackageService)_factory.Services.GetRequiredService<IPackageService>();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task InfoRefs_unknown_package_returns_404()
    {
        var response = await _client.GetAsync("/packages/missing.git/info/refs?service=git-upload-pack");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InfoRefs_rejects_non_upload_pack_service_with_403()
    {
        await _packages.SeedFilesAsync("shelly", SampleFiles);

        var response = await _client.GetAsync("/packages/shelly.git/info/refs?service=git-receive-pack");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InfoRefs_returns_advertisement_with_expected_headers()
    {
        await _packages.SeedFilesAsync("shelly", SampleFiles);

        var response = await _client.GetAsync("/packages/shelly.git/info/refs?service=git-upload-pack");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-git-upload-pack-advertisement",
            response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache,
            "Cache-Control: no-cache expected");

        var body = await response.Content.ReadAsByteArrayAsync();
        var text = Encoding.ASCII.GetString(body);
        Assert.StartsWith("001e# service=git-upload-pack\n", text);
        Assert.Contains("refs/heads/main", text);
    }

    [Fact]
    public async Task RootInfoRefs_resolves_a_split_package_base_to_a_seeded_package()
    {
        await _packages.SeedFilesAsync("shelly-bin", SampleFiles);

        var response = await _client.GetAsync("/shelly.git/info/refs?service=git-upload-pack");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("refs/heads/main", body);
    }

    [Fact]
    public async Task UploadPack_unknown_package_returns_404()
    {
        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");

        var response = await _client.PostAsync("/packages/missing.git/git-upload-pack", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UploadPack_stateless_request_returns_result_content_type()
    {
        await _packages.SeedFilesAsync("shelly", SampleFiles);

        var adv = await _client.GetAsync("/packages/shelly.git/info/refs?service=git-upload-pack");
        var advBody = Encoding.ASCII.GetString(await adv.Content.ReadAsByteArrayAsync());
        var sha = ExtractHeadSha(advBody);
        Assert.NotNull(sha);

        var requestBody = EncodePacketLine($"want {sha}\n") + "0000" + EncodePacketLine("done\n");
        using var content = new ByteArrayContent(Encoding.ASCII.GetBytes(requestBody));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");

        var response = await _client.PostAsync("/packages/shelly.git/git-upload-pack", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-git-upload-pack-result", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.True(body.Length > 0);
    }

    [Fact]
    public async Task UploadPack_unknown_want_returns_protocol_err_packet()
    {
        await _packages.SeedFilesAsync("shelly", SampleFiles);

        const string bogus = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        var requestBody = EncodePacketLine($"want {bogus}\n") + "0000" + EncodePacketLine("done\n");
        using var content = new ByteArrayContent(Encoding.ASCII.GetBytes(requestBody));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");

        var response = await _client.PostAsync("/packages/shelly.git/git-upload-pack", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-git-upload-pack-result", response.Content.Headers.ContentType?.MediaType);

        var body = Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(EncodePacketLine($"ERR upload-pack: not our ref {bogus}"), body);
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