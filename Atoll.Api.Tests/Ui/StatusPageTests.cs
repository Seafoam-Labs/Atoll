using Atoll.Api.Services.Security;
using System.Net;
using Atoll.Api.Tests.Support;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Ui;

public class StatusPageTests : IDisposable
{
    private SecurityTestFactory _factory = null!;
    private HttpClient _client = null!;

    public StatusPageTests()
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

    [Fact]
    public async Task StatusPageRendersOverviewStatsAndWorkerCards()
    {
        await _factory.Repository.InsertSeedAsync(Doc("shelly-bin"), SeedRevision("shelly-bin"));
        await _factory.SecurityRepository.MarkPendingAsync("shelly-bin", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SeedExclusions.RecordDocumentTooLargeAsync("huge-base", ["huge-base"], 20_000_000);

        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Index packages", body);
            Assert.Contains(">3</dd>", body);
            Assert.Contains("Seeded packages", body);
            Assert.Contains(">1</dd>", body);
            Assert.Contains("Pending scans", body);
            Assert.Contains("Excluded package bases", body);
            Assert.Contains("huge-base", body);
            Assert.Contains("Seeding - direct", body);
            Assert.Contains("Cycles started", body);
            Assert.Contains("Package refresh", body);
            Assert.Contains("disabled", body);
            Assert.Contains("Security scanner", body);
            Assert.Contains("cumulative", body);
            Assert.Contains("href=\"/metrics\"", body);
            Assert.Contains("Data assembled", body);
            Assert.Contains("Never", body);
        });
    }

    [Fact]
    public async Task StatusPageShowsBypassedBannerWhenSecurityDisabled()
    {
        await _factory.DisposeAsync();
        _client.Dispose();
        _factory = new SecurityTestFactory { SecurityEnabled = false };
        _client = _factory.CreateClient();

        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Content checks bypassed by configuration", body);
            // The enabled-mode explanation of the cumulative counters must not render.
            Assert.DoesNotContain("cumulative counters since the process started", body);
        });
    }

    [Fact]
    public async Task StatusPageShowsNotLoadedYetWithEmptyIndex()
    {
        await _factory.DisposeAsync();
        _client.Dispose();
        _factory = new SecurityTestFactory { LoadSampleIndex = false };
        _client = _factory.CreateClient();

        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Index packages", body);
            Assert.Contains(">0</dd>", body);
            Assert.Contains("Not loaded yet", body);
        });
    }

    [Fact]
    public async Task StatusPageHidesGrafanaLinkWhenUnconfigured()
    {
        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Grafana", body);
    }

    [Fact]
    public async Task StatusPageShowsOpenApiLink()
    {
        var response = await _client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("/scalar", body);
    }
}
