using System.Net;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Endpoints;

public class MutationsEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SecurityTestFactory _factory;

    public MutationsEndpointsTests()
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

    private async Task SeedPackageAsync(string name = "pkg")
    {
        await _factory.Repository.InsertSeedAsync(Doc(name), SeedRevision(name));
    }

    [Fact]
    public async Task Mutations_disabled_rejects_seed_with_403()
    {
        await using var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();

        var response = await client.PostAsync("/v1/packages/no-such-package/seed", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mutations_disabled_rejects_rescan_with_403()
    {
        await using var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();

        // The mutation gate takes precedence over package lookup.
        var response = await client.PostAsync("/v1/packages/no-such-package/security/rescan", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mutations_disabled_rejects_delete_with_403()
    {
        await using var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();

        await disabled.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg"));

        var response = await client.DeleteAsync("/v1/packages/pkg");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The gate runs before repo.DeleteAsync, so the package is not removed.
        var repo = await disabled.Repository.GetHeadAsync("pkg");
        Assert.NotNull(repo);
    }

    [Fact]
    public async Task Mutations_enabled_rescan_queues_head_pending()
    {
        await SeedPackageAsync();

        var response = await _client.PostAsync("/v1/packages/pkg/security/rescan", null);

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("/v1/packages/pkg/security?revision=rev-1", response.Headers.Location?.OriginalString);
        });

        var scan = await _factory.SecurityRepository.GetHeadAsync("pkg");
        Assert.NotNull(scan);
        Assert.Equal(SecurityStatus.Pending, scan!.Status);
        Assert.Equal("rev-1", scan.RevisionId);
    }
}