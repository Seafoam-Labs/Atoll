using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

public class PackageTarballEndpointsTests : IDisposable
{
    private static readonly UnixFileMode RegularFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly UnixFileMode ExecutableFileMode = RegularFileMode
        | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private readonly HttpClient _client;
    private readonly SecurityTestFactory _factory;

    public PackageTarballEndpointsTests()
    {
        _factory = new SecurityTestFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed record TarEntryData(string Name, UnixFileMode Mode, TarEntryType EntryType, string Content);

    [Fact]
    public async Task Head_tarball_is_served_while_revision_is_pending()
    {
        await SeedAsync(SecurityStatus.Pending);

        var response = await _client.GetAsync("/v1/packages/pkg/tarball");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/gzip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("pkg-rev-1.tar.gz", response.Content.Headers.ContentDisposition?.FileName);

        var entries = await ReadTarballAsync(response);
        Assert.Single(entries);
        Assert.Multiple(() =>
        {
            Assert.Equal("pkg/PKGBUILD", entries[0].Name);
            Assert.Equal(TarEntryType.RegularFile, entries[0].EntryType);
            Assert.Equal("pkgname=test\n", entries[0].Content);
            Assert.Equal(RegularFileMode, entries[0].Mode);
        });
    }

    [Fact]
    public async Task Flagged_head_tarball_is_served_while_content_json_is_blocked()
    {
        await SeedAsync(SecurityStatus.Flagged);

        var tarball = await _client.GetAsync("/v1/packages/pkg/tarball");
        var contentJson = await _client.GetAsync("/v1/packages/pkg");

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.OK, tarball.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, contentJson.StatusCode);
        });
    }

    [Fact]
    public async Task Pinned_revision_tarball_serves_the_requested_revision()
    {
        await SeedTwoRevisionsAsync(verified: SecurityStatus.Verified, flagged: SecurityStatus.Flagged);

        var flagged = await _client.GetAsync("/v1/packages/pkg/tarball?rev=rev-2");
        var verified = await _client.GetAsync("/v1/packages/pkg/tarball?rev=rev-1");

        await Assert.MultipleAsync(async () =>
        {
            Assert.Equal(HttpStatusCode.OK, flagged.StatusCode);
            Assert.Equal("pkg-rev-2.tar.gz", flagged.Content.Headers.ContentDisposition?.FileName);
            Assert.Equal("pkgname=test2\n", (await ReadTarballAsync(flagged)).Single().Content);

            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
            Assert.Equal("pkgname=test\n", (await ReadTarballAsync(verified)).Single().Content);
        });
    }

    [Fact]
    public async Task Unknown_revision_returns_404_instead_of_falling_back_to_head()
    {
        await SeedAsync(SecurityStatus.Verified);

        var response = await _client.GetAsync("/v1/packages/pkg/tarball?rev=rev-9");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_package_returns_404()
    {
        var response = await _client.GetAsync("/v1/packages/missing/tarball");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Shell_scripts_get_the_same_exec_bits_as_clones()
    {
        await _factory.Repository.InsertSeedAsync(Doc("pkg"), SeedRevision("pkg", new Dictionary<string, PackageFile>
        {
            ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 13, Hash = "h" },
            ["helper.sh"] = new() { Content = "#!/bin/sh\necho hi\n", Size = 18, Hash = "h2" }
        }));
        await _factory.SecurityRepository.MarkPendingAsync("pkg", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion);

        var response = await _client.GetAsync("/v1/packages/pkg/tarball");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await ReadTarballAsync(response);
        Assert.Multiple(() =>
        {
            Assert.Equal(RegularFileMode, entries.Single(e => e.Name == "pkg/PKGBUILD").Mode);
            Assert.Equal(ExecutableFileMode, entries.Single(e => e.Name == "pkg/helper.sh").Mode);
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
