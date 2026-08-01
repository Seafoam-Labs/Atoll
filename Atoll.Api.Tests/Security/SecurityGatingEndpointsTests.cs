using System.Net;
using System.Net.Http.Headers;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security;

public class SecurityGatingEndpointsTests
{
    private HttpClient _client = null!;
    private SecurityTestFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new SecurityTestFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static PackageDocument Doc(string name)
    {
        return new PackageDocument
        {
            Id = name,
            PackageName = name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadRevisionId = "rev-1",
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 12, Hash = "h" }
            },
            Revisions =
            [
                new PackageRevisionDocument
                {
                    RevisionId = "rev-1",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Author = "test",
                    Message = "seed",
                    Files = new Dictionary<string, PackageFile>
                    {
                        ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 12, Hash = "h" }
                    }
                }
            ]
        };
    }

    private async Task SeedAsync(SecurityStatus status)
    {
        await _factory.Repository.InsertSeedAsync(Doc("pkg"));
        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-1");
        if (status != SecurityStatus.Pending)
        {
            await _factory.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1));
            await _factory.SecurityRepository.CompleteScanAsync("pkg", "rev-1", "test", new ScanResult(status, []));
        }
    }

    [Test]
    public async Task Verified_package_files_are_served()
    {
        await SeedAsync(SecurityStatus.Verified);

        var response = await _client.GetAsync("/packages/pkg");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Pending_package_files_are_blocked_with_403_and_reason()
    {
        await SeedAsync(SecurityStatus.Pending);

        var response = await _client.GetAsync("/packages/pkg");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("security_status_pending"));
    }

    [Test]
    public async Task Flagged_package_files_are_blocked_with_403()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/packages/pkg");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("security_status_flagged"));
    }

    [Test]
    public async Task Flagged_revision_read_is_blocked()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/packages/pkg/versions/rev-1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Version_history_remains_visible_when_blocked()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/packages/pkg/versions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Search_remains_ungated()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/search?query=portable-kit");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Package_list_remains_ungated()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/packages");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Git_info_refs_is_blocked_for_pending_package()
    {
        await SeedAsync(SecurityStatus.Pending);

        var response = await _client.GetAsync("/packages/pkg.git/info/refs?service=git-upload-pack");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Git_upload_pack_is_blocked_for_pending_package()
    {
        await SeedAsync(SecurityStatus.Pending);

        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");

        var response = await _client.PostAsync("/packages/pkg.git/git-upload-pack", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Security_status_endpoint_reports_status()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/packages/pkg/security");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Flagged"));
    }
}