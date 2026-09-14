using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Endpoints;

public class PackageTarballEndpointsTests
{
    private static readonly UnixFileMode RegularFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly UnixFileMode ExecutableFileMode = RegularFileMode
        | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

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

    private sealed record TarEntryData(string Name, UnixFileMode Mode, TarEntryType EntryType, string Content);

    [Test]
    public async Task Head_tarball_is_served_while_revision_is_pending()
    {
        await SeedAsync(SecurityStatus.Pending);

        var response = await _client.GetAsync("/v1/packages/pkg/tarball");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/gzip"));
        Assert.That(response.Content.Headers.ContentDisposition?.FileName, Is.EqualTo("pkg-rev-1.tar.gz"));

        var entries = await ReadTarballAsync(response);
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Name, Is.EqualTo("pkg/PKGBUILD"));
            Assert.That(entries[0].EntryType, Is.EqualTo(TarEntryType.RegularFile));
            Assert.That(entries[0].Content, Is.EqualTo("pkgname=test\n"));
            Assert.That(entries[0].Mode, Is.EqualTo(RegularFileMode));
        });
    }

    [Test]
    public async Task Flagged_head_tarball_is_served_while_content_json_is_blocked()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var tarball = await _client.GetAsync("/v1/packages/pkg/tarball");
        var contentJson = await _client.GetAsync("/v1/packages/pkg");

        Assert.Multiple(() =>
        {
            Assert.That(tarball.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(contentJson.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }

    [Test]
    public async Task Pinned_revision_tarball_serves_the_requested_revision()
    {
        await SeedTwoRevisionsAsync(verified: SecurityStatus.Verified, flagged: SecurityStatus.Flagged);

        var flagged = await _client.GetAsync("/v1/packages/pkg/tarball?rev=rev-2");
        var verified = await _client.GetAsync("/v1/packages/pkg/tarball?rev=rev-1");

        Assert.Multiple(async () =>
        {
            Assert.That(flagged.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(flagged.Content.Headers.ContentDisposition?.FileName, Is.EqualTo("pkg-rev-2.tar.gz"));
            Assert.That((await ReadTarballAsync(flagged)).Single().Content, Is.EqualTo("pkgname=test2\n"));

            Assert.That(verified.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await ReadTarballAsync(verified)).Single().Content, Is.EqualTo("pkgname=test\n"));
        });
    }

    [Test]
    public async Task Unknown_revision_returns_404_instead_of_falling_back_to_head()
    {
        await SeedAsync(SecurityStatus.Verified);

        var response = await _client.GetAsync("/v1/packages/pkg/tarball?rev=rev-9");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Unknown_package_returns_404()
    {
        var response = await _client.GetAsync("/v1/packages/missing/tarball");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Shell_scripts_get_the_same_exec_bits_as_clones()
    {
        await _factory.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg", new Dictionary<string, PackageFile>
        {
            ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 13, Hash = "h" },
            ["helper.sh"] = new() { Content = "#!/bin/sh\necho hi\n", Size = 18, Hash = "h2" }
        }));
        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion);

        var response = await _client.GetAsync("/v1/packages/pkg/tarball");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var entries = await ReadTarballAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(entries.Single(e => e.Name == "pkg/PKGBUILD").Mode, Is.EqualTo(RegularFileMode));
            Assert.That(entries.Single(e => e.Name == "pkg/helper.sh").Mode, Is.EqualTo(ExecutableFileMode));
        });
    }

    private async Task SeedAsync(SecurityStatus status)
    {
        await _factory.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg"));
        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        if (status != SecurityStatus.Pending)
            await _factory.SecurityRepository.CompleteScanAsync("pkg", status);
    }

    private async Task SeedTwoRevisionsAsync(SecurityStatus verified, SecurityStatus flagged)
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
                ["PKGBUILD"] = new() { Content = "pkgname=test2\n", Size = 14, Hash = "h2" }
            }
        };

        await _factory.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg"));
        await _factory.Repository.AppendRevisionAsync("pkg", rev2, 10);
        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-1", false, PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-2", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SecurityRepository.CompleteScanAsync("pkg", verified);
        await _factory.SecurityRepository.CompleteScanAsync("pkg", flagged);
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

    private static PackageRevisionContentDocument SeedRevision(
        string name,
        Dictionary<string, PackageFile>? files = null)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(name, "rev-1"),
            PackageName = name,
            RevisionId = "rev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = "seed",
            Files = files ?? new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 13, Hash = "h" }
            }
        };
    }

    private static async Task<List<TarEntryData>> ReadTarballAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        var entries = new List<TarEntryData>();
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.DataStream is not { } data) continue;
            using var reader = new StreamReader(data);
            entries.Add(new TarEntryData(entry.Name, entry.Mode, entry.EntryType, await reader.ReadToEndAsync()));
        }

        return entries;
    }
}
