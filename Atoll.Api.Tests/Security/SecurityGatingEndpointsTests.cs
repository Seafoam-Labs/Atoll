using System.Net;
using System.Net.Http.Headers;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using NUnit.Framework;
using Atoll.Api.Services.Packages.Persistence;

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
            Revisions =
            [
                new PackageRevisionDocument
                {
                    RevisionId = "rev-1",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Author = "test",
                    Message = "seed"
                }
            ]
        };
    }

    private static PackageRevisionContentDocument SeedRevision(string name)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(name, "rev-1"),
            PackageName = name,
            RevisionId = "rev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = "seed",
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 12, Hash = "h" }
            }
        };
    }

    private async Task SeedAsync(SecurityStatus status)
    {
        await _factory.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg"));
        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        if (status != SecurityStatus.Pending)
        {
            await _factory.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1), PkgBuildSecurityScanner.CurrentPolicyVersion);
            await _factory.SecurityRepository.CompleteScanAsync(
                "pkg", "rev-1", "test", new ScanResult(status, []),
                PkgBuildSecurityScanner.CurrentPolicyVersion);
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
    public async Task Flagged_revision_is_blocked_but_other_revisions_are_served()
    {
        var rev2 = new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId("pkg", "rev-2"),
            PackageName = "pkg",
            RevisionId = "rev-2",
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = "update",
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = "pkgname=test2\n", Size = 13, Hash = "h2" }
            }
        };

        await _factory.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg"));
        await _factory.Repository.AppendRevisionAsync("pkg", rev2, 10);
        Assert.That(await _factory.Repository.GetHeadRevisionIdAsync("pkg"), Is.EqualTo("rev-2"));

        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-1", false, PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-2", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1), PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SecurityRepository.CompleteScanAsync(
            "pkg", "rev-1", "test", new ScanResult(SecurityStatus.Verified, []),
            PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1), PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SecurityRepository.CompleteScanAsync(
            "pkg", "rev-2", "test", new ScanResult(SecurityStatus.Flagged, []),
            PkgBuildSecurityScanner.CurrentPolicyVersion);

        var flagged = await _client.GetAsync("/packages/pkg/versions/rev-2");
        var clean = await _client.GetAsync("/packages/pkg/versions/rev-1");
        var head = await _client.GetAsync("/packages/pkg");

        Assert.Multiple(() =>
        {
            Assert.That(flagged.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(clean.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
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