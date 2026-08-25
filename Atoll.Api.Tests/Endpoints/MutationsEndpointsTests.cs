using System.Net;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Endpoints;

public class MutationsEndpointsTests
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

    private async Task SeedPackageAsync(string name = "pkg")
    {
        await _factory.Repository.InsertSeedAsync(Doc(name), SeedRevision(name));
    }

    [Test]
    public async Task Mutations_disabled_rejects_seed_with_403()
    {
        var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();

        try
        {
            var response = await client.PostAsync("/packages/no-such-package/seed", null);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        finally
        {
            client.Dispose();
            disabled.Dispose();
        }
    }

    [Test]
    public async Task Mutations_disabled_rejects_rescan_with_403()
    {
        var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();
        try
        {
            // The mutation gate takes precedence over package lookup.
            var response = await client.PostAsync("/packages/no-such-package/security/rescan", null);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        finally
        {
            client.Dispose();
            disabled.Dispose();
        }
    }

    [Test]
    public async Task Mutations_disabled_rejects_delete_with_403()
    {
        var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();
        try
        {
            await disabled.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg"));

            var response = await client.DeleteAsync("/packages/pkg");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            // The gate runs before repo.DeleteAsync, so the package is not removed.
            var repo = await disabled.Repository.GetHeadAsync("pkg");
            Assert.That(repo, Is.Not.Null);
        }
        finally
        {
            client.Dispose();
            disabled.Dispose();
        }
    }

    [Test]
    public async Task Mutations_enabled_rescan_queues_head_pending()
    {
        await SeedPackageAsync();

        var response = await _client.PostAsync("/packages/pkg/security/rescan", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        var scan = await _factory.SecurityRepository.GetHeadAsync("pkg");
        Assert.That(scan, Is.Not.Null);
        Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(scan.RevisionId, Is.EqualTo("rev-1"));
    }
}