using System.Net;
using Atoll.Api.Services.Packages;
using Atoll.Api.Tests.Support;
using NUnit.Framework;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Ui;

public class StatusPageTests
{
    private SecurityTestFactory _factory = null!;
    private HttpClient _client = null!;

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

    [Test]
    public async Task StatusPageRendersOverviewStatsAndWorkerCards()
    {
        await _factory.Repository.InsertSeedAsync(Doc("shelly-bin"), SeedRevision("shelly-bin"));
        await _factory.SecurityRepository.MarkPendingAsync("shelly-bin", "rev-1", true);
        await _factory.SeedExclusions.RecordDocumentTooLargeAsync("huge-base", ["huge-base"], 20_000_000);

        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
            Assert.That(body, Does.Contain("Index packages"));
            Assert.That(body, Does.Contain(">3</dd>"));
            Assert.That(body, Does.Contain("Seeded packages"));
            Assert.That(body, Does.Contain(">1</dd>"));
            Assert.That(body, Does.Contain("Pending scans"));
            Assert.That(body, Does.Contain("Excluded package bases"));
            Assert.That(body, Does.Contain("huge-base"));
            Assert.That(body, Does.Contain("Seeding - direct"));
            Assert.That(body, Does.Contain("Cycles started"));
            Assert.That(body, Does.Contain("Package refresh"));
            Assert.That(body, Does.Contain("disabled"));
            Assert.That(body, Does.Contain("Security scanner"));
            Assert.That(body, Does.Contain("cumulative"));
            Assert.That(body, Does.Contain("href=\"/metrics\""));
            Assert.That(body, Does.Contain("Data assembled"));
            Assert.That(body, Does.Contain("Never"));
        });
    }

    [Test]
    public async Task StatusPageShowsBypassedBannerWhenSecurityDisabled()
    {
        await _factory.DisposeAsync();
        _client.Dispose();
        _factory = new SecurityTestFactory { SecurityEnabled = false };
        _client = _factory.CreateClient();

        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("Content checks bypassed by configuration"));
            // The enabled-mode explanation of the cumulative counters must not render.
            Assert.That(body, Does.Not.Contain("cumulative counters since the process started"));
        });
    }

    [Test]
    public async Task StatusPageShowsNotLoadedYetWithEmptyIndex()
    {
        await _factory.DisposeAsync();
        _client.Dispose();
        _factory = new SecurityTestFactory { LoadSampleIndex = false };
        _client = _factory.CreateClient();

        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("Index packages"));
            Assert.That(body, Does.Contain(">0</dd>"));
            Assert.That(body, Does.Contain("Not loaded yet"));
        });
    }

    [Test]
    public async Task StatusPageHidesGrafanaLinkWhenUnconfigured()
    {
        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Does.Not.Contain("Grafana"));
    }

    [Test]
    public async Task StatusPageOmitsOpenApiLinkOutsideDevelopment()
    {
        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        // The Testing environment is not Development, so no OpenAPI link may appear.
        Assert.That(body, Does.Not.Contain("/openapi/v1.json"));
    }
}
