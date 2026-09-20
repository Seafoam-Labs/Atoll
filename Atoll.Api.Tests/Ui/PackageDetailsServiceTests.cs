using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Options;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Ui;

public class PackageDetailsServiceTests : IAsyncLifetime
{
    private PackageIndexStore _store = null!;
    private InMemoryPackageRepository _repository = null!;
    private InMemoryPackageSecurityRepository _securityRepository = null!;
    private PackageDetailsService _service = null!;

    private const string Name = "shelly-bin";

    public async ValueTask InitializeAsync()
    {
        _store = new PackageIndexStore();
        _store.Replace(await TestData.LoadSampleIndexesAsync());
        _repository = new InMemoryPackageRepository();
        _securityRepository = new InMemoryPackageSecurityRepository();
        _service = new PackageDetailsService(
            _store,
            _repository,
            _securityRepository,
            new PackageSecurityAccess(_repository, _securityRepository, Options.Create(new AtollOptions())));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GetRevisionsAsyncOrdersNewestFirstMarksHeadAndJoinsScanStatuses()
    {
        await SeedRevisionAsync("rev-1", "old head", SecurityStatus.Flagged, files: Files("pkgname=old\n"));
        await SeedRevisionAsync("rev-2", "sync from upstream", SecurityStatus.Verified, files: Files("pkgname=new\n"));

        var result = await _service.GetRevisionsAsync(Name, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(["rev-2", "rev-1"], result!.Rows.Select(row => row.Sha));
        Assert.Equal(2, result.TotalRevisions);
        Assert.False(result.IsTruncated);
        Assert.Equal("rev-2", result.HeadRevisionId);
        Assert.True(result.Rows[0].IsHead);
        Assert.Equal("sync from upstream", result.Rows[0].Message);
        Assert.Equal(SecurityStatus.Verified, result.Rows[0].Status);
        Assert.False(result.Rows[1].IsHead);
        Assert.Equal(SecurityStatus.Flagged, result.Rows[1].Status);
    }

    [Fact]
    public async Task GetRevisionsAsyncMarksUnscannedRevisionsWithNullStatus()
    {
        await SeedRevisionAsync("rev-1", "seed");

        var result = await _service.GetRevisionsAsync(Name, TestContext.Current.CancellationToken);

        Assert.Null(result!.Rows.Single().Status);
    }

    [Fact]
    public async Task GetRevisionsAsyncTruncatesBeyondRenderCap()
    {
        await InsertDocAsync();
        // The seed doc already holds one revision; append more to land above the cap.
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < PackageDetailsService.RevisionRenderCap + 4; i++)
            await AppendAsync($"rev-{i:000}", start.AddMinutes(i));

        var result = await _service.GetRevisionsAsync(Name, TestContext.Current.CancellationToken);

        Assert.Equal(PackageDetailsService.RevisionRenderCap + 5, result!.TotalRevisions);
        Assert.Equal(PackageDetailsService.RevisionRenderCap, result.Rows.Count);
        Assert.True(result.IsTruncated);
        // Newest first: the last appended revision leads the rendered page.
        Assert.Equal($"rev-{PackageDetailsService.RevisionRenderCap + 3:000}", result.Rows[0].Sha);
    }

    [Fact]
    public async Task GetRevisionsAsyncReturnsNullForUnknownPackageAndEmptyForUnseeded()
    {
        Assert.Null(await _service.GetRevisionsAsync("no-such-package", TestContext.Current.CancellationToken));

        var unseeded = await _service.GetRevisionsAsync("portable-kit", TestContext.Current.CancellationToken);
        Assert.Empty(unseeded!.Rows);
        Assert.Equal(0, unseeded.TotalRevisions);
    }

    [Fact]
    public async Task GetFilesAsyncReturnsTreeEntriesSortedDirectoriesFirst()
    {
        await SeedRevisionAsync("rev-1", "seed", SecurityStatus.Verified, files: new Dictionary<string, PackageFile>
        {
            ["PKGBUILD"] = File("pkgname=test\n"),
            [".SRCINFO"] = File("pkgname = test\n"),
            ["sub/hook.sh"] = File("#!/bin/sh\n"),
            ["sub/deep/notes.txt"] = File("x\n"),
            ["zzz.txt"] = File("z\n")
        });

        var view = await _service.GetFilesAsync(Name, null, null, TestContext.Current.CancellationToken);

        Assert.True(view!.Access.Allowed);
        Assert.Equal(["sub/deep/notes.txt", "sub/hook.sh", ".SRCINFO", "PKGBUILD", "zzz.txt"],
            view.Entries.Select(entry => entry.Path));
        Assert.True(view.IsHead);
        Assert.Null(view.SelectedPath);
        Assert.False(view.EntriesTruncated);
    }

    [Fact]
    public async Task GetFilesAsyncReturnsSelectedFileContent()
    {
        await SeedRevisionAsync("rev-1", "seed", SecurityStatus.Verified, files: Files("line one\nline two\n"));

        var view = await _service.GetFilesAsync(Name, null, "PKGBUILD", TestContext.Current.CancellationToken);

        Assert.Equal("PKGBUILD", view!.SelectedPath);
        Assert.Equal("line one\nline two\n", view.Content);
        Assert.False(view.IsBinary);
        Assert.False(view.IsTruncated);
        Assert.False(view.FileNotFound);
    }

    [Fact]
    public async Task GetFilesAsyncMarksMissingPathAsFileNotFound()
    {
        await SeedRevisionAsync("rev-1", "seed", SecurityStatus.Verified);

        var view = await _service.GetFilesAsync(Name, null, "not-there.txt", TestContext.Current.CancellationToken);

        Assert.True(view!.FileNotFound);
        Assert.Null(view.Content);
    }

    [Fact]
    public async Task GetFilesAsyncServesFlaggedRevisionsForUiInspection()
    {
        await SeedRevisionAsync("rev-1", "old", SecurityStatus.Flagged, files: Files("pkgname=old\n"));
        await SeedRevisionAsync("rev-2", "new", SecurityStatus.Verified, files: Files("pkgname=new\n"));

        var blocked = await _service.GetFilesAsync(Name, "rev-1", "PKGBUILD", TestContext.Current.CancellationToken);

        Assert.False(blocked!.Access.Allowed);
        Assert.Equal(SecurityAccessReasonCodes.Flagged, blocked.Access.ReasonCode);
        Assert.Equal(["PKGBUILD"], blocked.Entries.Select(entry => entry.Path));
        Assert.Equal("pkgname=old\n", blocked.Content);

        var allowed = await _service.GetFilesAsync(Name, "rev-2", "PKGBUILD", TestContext.Current.CancellationToken);
        Assert.True(allowed!.Access.Allowed);
        Assert.Equal("pkgname=new\n", allowed.Content);
        Assert.True(allowed.IsHead);
    }

    [Fact]
    public async Task GetFilesAsyncFallsBackToHeadForUnknownRevision()
    {
        await SeedRevisionAsync("rev-1", "old", SecurityStatus.Verified, files: Files("pkgname=old\n"));
        await SeedRevisionAsync("rev-2", "new", SecurityStatus.Verified, files: Files("pkgname=new\n"));

        var fellBack = await _service.GetFilesAsync(Name, "garbage", "PKGBUILD", TestContext.Current.CancellationToken);
        var pinned = await _service.GetFilesAsync(Name, "rev-1", "PKGBUILD", TestContext.Current.CancellationToken);

        Assert.True(fellBack!.RevisionFellBack);
        Assert.Equal("rev-2", fellBack.RevisionId);
        Assert.Equal("pkgname=new\n", fellBack.Content);

        Assert.False(pinned!.RevisionFellBack);
        Assert.Equal("rev-1", pinned.RevisionId);
        Assert.False(pinned.IsHead);
        Assert.Equal("pkgname=old\n", pinned.Content);
    }

    [Fact]
    public async Task GetFilesAsyncDetectsBinaryFiles()
    {
        await SeedRevisionAsync("rev-1", "seed", SecurityStatus.Verified, files: new Dictionary<string, PackageFile>
        {
            ["blob.bin"] = new() { Content = "abc\0def", Size = 7, Hash = "h" }
        });

        var view = await _service.GetFilesAsync(Name, null, "blob.bin", TestContext.Current.CancellationToken);

        Assert.True(view!.IsBinary);
        Assert.Null(view.Content);
    }

    [Fact]
    public async Task GetFilesAsyncTruncatesLargeContent()
    {
        var large = new string('a', PackageDetailsService.ContentRenderChars + 1000);
        await SeedRevisionAsync("rev-1", "seed", SecurityStatus.Verified,
            files: new Dictionary<string, PackageFile> { ["big.txt"] = new() { Content = large, Size = large.Length, Hash = "h" } });

        var view = await _service.GetFilesAsync(Name, null, "big.txt", TestContext.Current.CancellationToken);

        Assert.True(view!.IsTruncated);
        Assert.Equal(PackageDetailsService.ContentRenderChars, view.Content!.Length);
        Assert.Equal(large.Length, view.ContentBytes);
    }

    [Fact]
    public async Task GetFilesAsyncReturnsNullForUnknownPackageAndEmptyForUnseeded()
    {
        Assert.Null(await _service.GetFilesAsync("no-such-package", null, null, TestContext.Current.CancellationToken));

        var unseeded = await _service.GetFilesAsync("portable-kit", null, null, TestContext.Current.CancellationToken);
        Assert.Empty(unseeded!.Entries);
        Assert.True(unseeded.Access.Allowed);
    }

    [Fact]
    public async Task GetAsyncResolvesRevisionPinWithHeadFallback()
    {
        await SeedRevisionAsync("rev-1", "old", SecurityStatus.Flagged, files: Files("pkgname=old\n"));
        await SeedRevisionAsync("rev-2", "new", SecurityStatus.Verified, files: Files("pkgname=new\n"));

        var head = await _service.GetAsync(Name, ct: TestContext.Current.CancellationToken);
        var pinned = await _service.GetAsync(Name, "rev-1", TestContext.Current.CancellationToken);
        var garbage = await _service.GetAsync(Name, "garbage", TestContext.Current.CancellationToken);

        Assert.Equal("rev-2", head!.SelectedRevisionId);
        Assert.True(head.SelectedIsHead);
        Assert.False(head.RevisionFellBack);
        Assert.Equal(SecurityStatus.Verified, head.SelectedScan!.Status);

        Assert.Equal("rev-1", pinned!.SelectedRevisionId);
        Assert.False(pinned.SelectedIsHead);
        Assert.False(pinned.RevisionFellBack);
        Assert.Equal(SecurityStatus.Flagged, pinned.SelectedScan!.Status);

        Assert.True(garbage!.RevisionFellBack);
        Assert.Equal("rev-2", garbage.SelectedRevisionId);
    }

    [Fact]
    public async Task GetAsyncReturnsNullForUnknownPackage()
    {
        Assert.Null(await _service.GetAsync("no-such-package", ct: TestContext.Current.CancellationToken));
    }

    private static Dictionary<string, PackageFile> Files(string pkgbuild)
    {
        return new Dictionary<string, PackageFile> { ["PKGBUILD"] = File(pkgbuild) };
    }

    private static PackageFile File(string content)
    {
        return new PackageFile { Content = content, Size = content.Length, Hash = "h" };
    }

    private async Task InsertDocAsync()
    {
        await _repository.InsertSeedAsync(
            new PackageDocument
            {
                Id = Name,
                PackageName = Name,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                HeadRevisionId = "rev-seed",
                Revisions =
                [
                    new PackageRevisionDocument
                    {
                        RevisionId = "rev-seed",
                        CreatedAt = DateTimeOffset.UtcNow,
                        Author = "test",
                        Message = "seed"
                    }
                ]
            },
            RevisionContent("rev-seed", "seed", DateTimeOffset.UtcNow, Files("pkgname=seed\n")));
    }

    /// <summary>Appends a revision on top of the seed document (mirrors the append-then-promote flow).</summary>
    private async Task AppendAsync(string sha, DateTimeOffset createdAt)
    {
        await _repository.AppendRevisionAsync(
            Name,
            RevisionContent(sha, "appended", createdAt, Files($"pkgname={sha}\n")),
            maxRevisions: 10_000);
        await _securityRepository.PromoteHeadAsync(Name, sha);
    }

    private async Task SeedRevisionAsync(
        string sha,
        string message,
        SecurityStatus? status = null,
        Dictionary<string, PackageFile>? files = null)
    {
        files ??= Files($"pkgname={sha}\n");

        var exists = await _repository.GetHeadAsync(Name);
        if (exists is null)
        {
            await InsertDocWithRevisionAsync(sha, message, files);
        }
        else
        {
            await _repository.AppendRevisionAsync(
                Name,
                RevisionContent(sha, message, DateTimeOffset.UtcNow, files),
                maxRevisions: 10);
            await _securityRepository.PromoteHeadAsync(Name, sha);
        }

        if (status is null)
            return;

        await _securityRepository.MarkPendingAsync(Name, sha, isHead: true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        var claim = await _securityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1), PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _securityRepository.CompleteScanAsync(
            Name, sha, claim!.LeaseOwner!, new ScanResult(status.Value, []),
            PkgBuildSecurityScanner.CurrentPolicyVersion);
    }

    private async Task InsertDocWithRevisionAsync(string sha, string message, Dictionary<string, PackageFile> files)
    {
        await _repository.InsertSeedAsync(
            new PackageDocument
            {
                Id = Name,
                PackageName = Name,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                HeadRevisionId = sha,
                Revisions = [RevisionEntry(sha, message, DateTimeOffset.UtcNow)]
            },
            RevisionContent(sha, message, DateTimeOffset.UtcNow, files));
    }

    private static PackageRevisionDocument RevisionEntry(string sha, string message, DateTimeOffset createdAt)
    {
        return new PackageRevisionDocument
        {
            RevisionId = sha,
            CreatedAt = createdAt,
            Author = "test",
            Message = message
        };
    }

    private static PackageRevisionContentDocument RevisionContent(
        string sha,
        string message,
        DateTimeOffset createdAt,
        Dictionary<string, PackageFile> files)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(Name, sha),
            PackageName = Name,
            RevisionId = sha,
            CreatedAt = createdAt,
            Author = "test",
            Message = message,
            Files = files
        };
    }
}
