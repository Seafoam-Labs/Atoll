using System.Net;
using System.Net.Http.Headers;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Security;

public sealed class SecurityGatingEndpointsTests : IDisposable
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
            Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 12, Hash = "h" }
            }
        };
    }

    private async Task SeedAsync(SecurityStatus status)
    {
        await _factory.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg"), TestContext.Current.CancellationToken);
        await _factory.SecurityRepository.ScanRevisionAsync("pkg", "rev-1", status);
    }

    [Fact]
    public async Task GetV1PackagesName_VerifiedHead_ServesContent()
    {
        await SeedAsync(SecurityStatus.Verified);

        var response = await _client.GetAsync("/v1/packages/pkg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetV1PackagesName_PendingHead_Returns403WithReason()
    {
        await SeedAsync(SecurityStatus.Pending);

        var response = await _client.GetAsync("/v1/packages/pkg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("security_status_pending", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetV1PackagesName_FlaggedHead_Returns403()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages/pkg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("security_status_flagged", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetV1PackagesNameVersionsSha_FlaggedRevision_Returns403()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages/pkg/versions/rev-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetV1PackagesNameVersions_FlaggedHead_RemainsVisible()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages/pkg/versions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetV1Search_FlaggedPackage_RemainsUngated()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/search?query=portable-kit", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetV1Packages_FlaggedPackage_RemainsUngated()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InfoRefs_PendingPackage_Returns403()
    {
        await SeedAsync(SecurityStatus.Pending);

        var response = await _client.GetAsync("/packages/pkg.git/info/refs?service=git-upload-pack", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UploadPack_PendingPackage_Returns403()
    {
        await SeedAsync(SecurityStatus.Pending);

        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");

        var response = await _client.PostAsync("/packages/pkg.git/git-upload-pack", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetV1PackagesNameVersionsSha_MixedRevisionStatuses_BlocksFlaggedAndServesVerified()
    {
        var rev2 = new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId("pkg", "rev-2"),
            PackageName = "pkg",
            RevisionId = "rev-2",
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = "update",
            Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
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
    public async Task GetV1PackagesNameSecurity_FlaggedHead_ReportsFlaggedStatus()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var response = await _client.GetAsync("/v1/packages/pkg/security", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Flagged", body, StringComparison.Ordinal);
    }
}