using System.Net;
using System.Net.Http.Headers;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Security;

public class SecurityGatingEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SecurityTestFactory _factory;

    public SecurityGatingEndpointsTests()
    {
        _factory = new SecurityTestFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
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
            await _factory.SecurityRepository.CompleteScanAsync("pkg", status);
    }

    [Fact]
    public async Task Verified_package_files_are_served()
    {
        await SeedAsync(SecurityStatus.Verified);

        var response = await _client.GetAsync("/v1/packages/pkg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Pending_package_files_are_blocked_with_403_and_reason()
    {
        await SeedAsync(SecurityStatus.Pending);

        var response = await _client.GetAsync("/v1/packages/pkg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("security_status_pending", body);
    }

    [Fact]
    public async Task Flagged_package_files_are_blocked_with_403()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages/pkg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("security_status_flagged", body);
    }

    [Fact]
    public async Task Flagged_revision_read_is_blocked()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages/pkg/versions/rev-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Version_history_remains_visible_when_blocked()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages/pkg/versions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Search_remains_ungated()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/search?query=portable-kit", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Package_list_remains_ungated()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Git_info_refs_is_blocked_for_pending_package()
    {
        await SeedAsync(SecurityStatus.Pending);

        var response = await _client.GetAsync("/packages/pkg.git/info/refs?service=git-upload-pack", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Git_upload_pack_is_blocked_for_pending_package()
    {
        await SeedAsync(SecurityStatus.Pending);

        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");

        var response = await _client.PostAsync("/packages/pkg.git/git-upload-pack", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
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

        await _factory.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg"), TestContext.Current.CancellationToken);
        await _factory.Repository.AppendRevisionAsync("pkg", rev2, 10, TestContext.Current.CancellationToken);
        Assert.Equal("rev-2", await _factory.Repository.GetHeadRevisionIdAsync("pkg", TestContext.Current.CancellationToken));

        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-1", false, PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);
        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-2", true, PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);
        await _factory.SecurityRepository.CompleteScanAsync("pkg", SecurityStatus.Verified);
        await _factory.SecurityRepository.CompleteScanAsync("pkg", SecurityStatus.Flagged);

        var flagged = await _client.GetAsync("/v1/packages/pkg/versions/rev-2", TestContext.Current.CancellationToken);
        var clean = await _client.GetAsync("/v1/packages/pkg/versions/rev-1", TestContext.Current.CancellationToken);
        var head = await _client.GetAsync("/v1/packages/pkg", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.Forbidden, flagged.StatusCode);
            Assert.Equal(HttpStatusCode.OK, clean.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, head.StatusCode);
        });
    }

    [Fact]
    public async Task Security_status_endpoint_reports_status()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages/pkg/security", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Flagged", body);
    }
}