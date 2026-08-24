using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Ui;

public class PackageDetailsServiceTests
{
    private PackageIndexStore _store = null!;
    private InMemoryPackageRepository _repository = null!;
    private InMemoryPackageSecurityRepository _securityRepository = null!;
    private PackageDetailsService _service = null!;

    private const string Name = "shelly-bin";

    [SetUp]
    public async Task SetUp()
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

    [Test]
    public async Task GetRevisionsAsyncOrdersNewestFirstMarksHeadAndJoinsScanStatuses()
    {
        await SeedRevisionAsync("rev-1", "old head", SecurityStatus.Flagged, files: Files("pkgname=old\n"));
        await SeedRevisionAsync("rev-2", "sync from upstream", SecurityStatus.Verified, files: Files("pkgname=new\n"));

        var result = await _service.GetRevisionsAsync(Name);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Rows.Select(row => row.Sha), Is.EqualTo(["rev-2", "rev-1"]));
        Assert.That(result.TotalRevisions, Is.EqualTo(2));
        Assert.That(result.IsTruncated, Is.False);
        Assert.That(result.HeadRevisionId, Is.EqualTo("rev-2"));
        Assert.That(result.Rows[0].IsHead, Is.True);
        Assert.That(result.Rows[0].Message, Is.EqualTo("sync from upstream"));
        Assert.That(result.Rows[0].Status, Is.EqualTo(SecurityStatus.Verified));
        Assert.That(result.Rows[1].IsHead, Is.False);
        Assert.That(result.Rows[1].Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public async Task GetRevisionsAsyncMarksUnscannedRevisionsWithNullStatus()
    {
        await SeedRevisionAsync("rev-1", "seed");

        var result = await _service.GetRevisionsAsync(Name);

        Assert.That(result!.Rows.Single().Status, Is.Null);
    }

    [Test]
    public async Task GetRevisionsAsyncTruncatesBeyondRenderCap()
    {
        await InsertDocAsync();
        // The seed doc already holds one revision; append more to land above the cap.
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < PackageDetailsService.RevisionRenderCap + 4; i++)
            await AppendAsync($"rev-{i:000}", start.AddMinutes(i));

        var result = await _service.GetRevisionsAsync(Name);

        Assert.That(result!.TotalRevisions, Is.EqualTo(PackageDetailsService.RevisionRenderCap + 5));
        Assert.That(result.Rows, Has.Count.EqualTo(PackageDetailsService.RevisionRenderCap));
        Assert.That(result.IsTruncated, Is.True);
        // Newest first: the last appended revision leads the rendered page.
        Assert.That(result.Rows[0].Sha, Is.EqualTo($"rev-{PackageDetailsService.RevisionRenderCap + 3:000}"));
    }

    [Test]
    public async Task GetRevisionsAsyncReturnsNullForUnknownPackageAndEmptyForUnseeded()
    {
        Assert.That(await _service.GetRevisionsAsync("no-such-package"), Is.Null);

        var unseeded = await _service.GetRevisionsAsync("portable-kit");
        Assert.That(unseeded!.Rows, Is.Empty);
        Assert.That(unseeded.TotalRevisions, Is.EqualTo(0));
    }

    [Test]
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

        var view = await _service.GetFilesAsync(Name, null, null);

        Assert.That(view!.Access.Allowed, Is.True);
        Assert.That(view.Entries.Select(entry => entry.Path),
            Is.EqualTo(["sub/deep/notes.txt", "sub/hook.sh", ".SRCINFO", "PKGBUILD", "zzz.txt"]));
        Assert.That(view.IsHead, Is.True);
        Assert.That(view.SelectedPath, Is.Null);
        Assert.That(view.EntriesTruncated, Is.False);
    }

    [Test]
    public async Task GetFilesAsyncReturnsSelectedFileContent()
    {
        await SeedRevisionAsync("rev-1", "seed", SecurityStatus.Verified, files: Files("line one\nline two\n"));

        var view = await _service.GetFilesAsync(Name, null, "PKGBUILD");

        Assert.That(view!.SelectedPath, Is.EqualTo("PKGBUILD"));
        Assert.That(view.Content, Is.EqualTo("line one\nline two\n"));
        Assert.That(view.IsBinary, Is.False);
        Assert.That(view.IsTruncated, Is.False);
        Assert.That(view.FileNotFound, Is.False);
    }

    [Test]
    public async Task GetFilesAsyncMarksMissingPathAsFileNotFound()
    {
        await SeedRevisionAsync("rev-1", "seed", SecurityStatus.Verified);

        var view = await _service.GetFilesAsync(Name, null, "not-there.txt");

        Assert.That(view!.FileNotFound, Is.True);
        Assert.That(view.Content, Is.Null);
    }

    [Test]
    public async Task GetFilesAsyncServesFlaggedRevisionsForUiInspection()
    {
        await SeedRevisionAsync("rev-1", "old", SecurityStatus.Flagged, files: Files("pkgname=old\n"));
        await SeedRevisionAsync("rev-2", "new", SecurityStatus.Verified, files: Files("pkgname=new\n"));

        var blocked = await _service.GetFilesAsync(Name, "rev-1", "PKGBUILD");

        Assert.That(blocked!.Access.Allowed, Is.False);
        Assert.That(blocked.Access.ReasonCode, Is.EqualTo(SecurityAccessReasonCodes.Flagged));
        Assert.That(blocked.Entries.Select(entry => entry.Path), Is.EqualTo(["PKGBUILD"]));
        Assert.That(blocked.Content, Is.EqualTo("pkgname=old\n"));

        var allowed = await _service.GetFilesAsync(Name, "rev-2", "PKGBUILD");
        Assert.That(allowed!.Access.Allowed, Is.True);
        Assert.That(allowed.Content, Is.EqualTo("pkgname=new\n"));
        Assert.That(allowed.IsHead, Is.True);
    }

    [Test]
    public async Task GetFilesAsyncFallsBackToHeadForUnknownRevision()
    {
        await SeedRevisionAsync("rev-1", "old", SecurityStatus.Verified, files: Files("pkgname=old\n"));
        await SeedRevisionAsync("rev-2", "new", SecurityStatus.Verified, files: Files("pkgname=new\n"));

        var fellBack = await _service.GetFilesAsync(Name, "garbage", "PKGBUILD");
        var pinned = await _service.GetFilesAsync(Name, "rev-1", "PKGBUILD");

        Assert.That(fellBack!.RevisionFellBack, Is.True);
        Assert.That(fellBack.RevisionId, Is.EqualTo("rev-2"));
        Assert.That(fellBack.Content, Is.EqualTo("pkgname=new\n"));

        Assert.That(pinned!.RevisionFellBack, Is.False);
        Assert.That(pinned.RevisionId, Is.EqualTo("rev-1"));
        Assert.That(pinned.IsHead, Is.False);
        Assert.That(pinned.Content, Is.EqualTo("pkgname=old\n"));
    }

    [Test]
    public async Task GetFilesAsyncDetectsBinaryFiles()
    {
        await SeedRevisionAsync("rev-1", "seed", SecurityStatus.Verified, files: new Dictionary<string, PackageFile>
        {
            ["blob.bin"] = new() { Content = "abc\0def", Size = 7, Hash = "h" }
        });

        var view = await _service.GetFilesAsync(Name, null, "blob.bin");

        Assert.That(view!.IsBinary, Is.True);
        Assert.That(view.Content, Is.Null);
    }

    [Test]
    public async Task GetFilesAsyncTruncatesLargeContent()
    {
        var large = new string('a', PackageDetailsService.ContentRenderChars + 1000);
        await SeedRevisionAsync("rev-1", "seed", SecurityStatus.Verified,
            files: new Dictionary<string, PackageFile> { ["big.txt"] = new() { Content = large, Size = large.Length, Hash = "h" } });

        var view = await _service.GetFilesAsync(Name, null, "big.txt");

        Assert.That(view!.IsTruncated, Is.True);
        Assert.That(view.Content, Has.Length.EqualTo(PackageDetailsService.ContentRenderChars));
        Assert.That(view.ContentBytes, Is.EqualTo(large.Length));
    }

    [Test]
    public async Task GetFilesAsyncReturnsNullForUnknownPackageAndEmptyForUnseeded()
    {
        Assert.That(await _service.GetFilesAsync("no-such-package", null, null), Is.Null);

        var unseeded = await _service.GetFilesAsync("portable-kit", null, null);
        Assert.That(unseeded!.Entries, Is.Empty);
        Assert.That(unseeded.Access.Allowed, Is.True);
    }

    [Test]
    public async Task GetAsyncResolvesRevisionPinWithHeadFallback()
    {
        await SeedRevisionAsync("rev-1", "old", SecurityStatus.Flagged, files: Files("pkgname=old\n"));
        await SeedRevisionAsync("rev-2", "new", SecurityStatus.Verified, files: Files("pkgname=new\n"));

        var head = await _service.GetAsync(Name);
        var pinned = await _service.GetAsync(Name, "rev-1");
        var garbage = await _service.GetAsync(Name, "garbage");

        Assert.That(head!.SelectedRevisionId, Is.EqualTo("rev-2"));
        Assert.That(head.SelectedIsHead, Is.True);
        Assert.That(head.RevisionFellBack, Is.False);
        Assert.That(head.SelectedScan!.Status, Is.EqualTo(SecurityStatus.Verified));

        Assert.That(pinned!.SelectedRevisionId, Is.EqualTo("rev-1"));
        Assert.That(pinned.SelectedIsHead, Is.False);
        Assert.That(pinned.RevisionFellBack, Is.False);
        Assert.That(pinned.SelectedScan!.Status, Is.EqualTo(SecurityStatus.Flagged));

        Assert.That(garbage!.RevisionFellBack, Is.True);
        Assert.That(garbage.SelectedRevisionId, Is.EqualTo("rev-2"));
    }

    [Test]
    public async Task GetAsyncReturnsNullForUnknownPackage()
    {
        Assert.That(await _service.GetAsync("no-such-package"), Is.Null);
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

        await _securityRepository.MarkPendingAsync(Name, sha, isHead: true);
        var claim = await _securityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1));
        await _securityRepository.CompleteScanAsync(
            Name, sha, claim!.LeaseOwner!, new ScanResult(status.Value, []));
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
