using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Options;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Ui;

/// <summary>
///     GetDiffAsync classification and cap behavior with a stub differ, so the whole file is git-free.
///     Output shape of the real CLI is pinned separately in Packages/Git/GitTextDifferTests.
/// </summary>
public sealed class PackageDetailsServiceDiffTests : IAsyncLifetime
{
    private const string Name = "shelly-bin";

    private PackageIndexStore _store = null!;
    private InMemoryPackageRepository _repository = null!;
    private InMemoryPackageSecurityRepository _securityRepository = null!;
    private FakeTextDiffer _differ = null!;
    private PackageDetailsService _service = null!;

    public ValueTask InitializeAsync()
    {
        _store = new PackageIndexStore();
        _store.Replace(TestData.LoadSampleIndexes());
        _repository = new InMemoryPackageRepository();
        _securityRepository = new InMemoryPackageSecurityRepository();
        _differ = new FakeTextDiffer();
        _service = new PackageDetailsService(
            _store,
            _repository,
            _securityRepository,
            new PackageSecurityAccess(_repository, _securityRepository, Options.Create(new AtollOptions())),
            _differ);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ReturnsNullForUnknownPackageAndEmptyViewForUnseeded()
    {
        Assert.Null(await _service.GetDiffAsync("no-such-package", null, null, TestContext.Current.CancellationToken));

        var unseeded = await _service.GetDiffAsync("portable-kit", null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(unseeded);
        Assert.Empty(unseeded!.Files);
        Assert.Equal(string.Empty, unseeded.ToRevisionId);
        Assert.Empty(_differ.Calls);
    }

    [Fact]
    public async Task DefaultsToComparingAgainstTheParentRevision()
    {
        await SeedAsync("rev-1", Files("pkgname=one\n"));
        await AppendAsync("rev-2", Files("pkgname=two\n"));
        await AppendAsync("rev-3", Files("pkgname=three\n"));

        var diff = await _service.GetDiffAsync(Name, null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        Assert.Equal("rev-2", diff!.FromRevisionId);
        Assert.Equal("rev-3", diff.ToRevisionId);
        Assert.False(diff.FromFellBack);
        Assert.False(diff.ToFellBack);
    }

    [Fact]
    public async Task OldestRevisionHasNoParentSoEveryFileReadsAsAdded()
    {
        await SeedAsync("rev-1", Files("pkgname=one\n"));
        await AppendAsync("rev-2", Files("pkgname=two\n"));

        var diff = await _service.GetDiffAsync(Name, null, "rev-1", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        // rev-1 is the oldest, so there is no base: an empty base, not a bogus diff against the newer head.
        Assert.Equal(string.Empty, diff!.FromRevisionId);
        Assert.False(diff.Identical);
        var file = Assert.Single(diff.Files);
        Assert.Equal(DiffChangeKind.Added, file.Kind);
        Assert.Equal(0, file.OldSize);
    }

    [Fact]
    public async Task SameRevisionShortCircuitsWithoutCallingTheDiffer()
    {
        await SeedAsync("rev-1", Files("pkgname=one\n"));

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-1", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        Assert.True(diff!.Identical);
        Assert.Empty(diff.Files);
        Assert.Equal(0, diff.ChangedFileCount);
        Assert.Empty(_differ.Calls);
    }

    [Fact]
    public async Task ClassifiesAddedRemovedAndModifiedAndSkipsUnchanged()
    {
        await SeedAsync("rev-1", new Dictionary<string, PackageFile>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = File("pkgname=old\n"),
            ["gone.txt"] = File("bye\n"),
            ["same.txt"] = File("identical\n")
        });
        await AppendAsync("rev-2",
            new Dictionary<string, PackageFile>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = File("pkgname=new\n"),
                ["same.txt"] = File("identical\n"),
                ["added.txt"] = File("hello\n")
            });

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        var byPath = diff!.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        Assert.Equal(3, diff.ChangedFileCount);
        Assert.Equal(DiffChangeKind.Modified, byPath["PKGBUILD"].Kind);
        Assert.Equal(DiffChangeKind.Added, byPath["added.txt"].Kind);
        Assert.Equal(DiffChangeKind.Removed, byPath["gone.txt"].Kind);
        Assert.False(byPath.ContainsKey("same.txt"));
        // A null side reports size 0 rather than a placeholder.
        Assert.Equal(0, byPath["gone.txt"].NewSize);
        Assert.Equal(0, byPath["added.txt"].OldSize);
        // Every chunk comes back keyed by the caller's path.
        Assert.All(diff.Files, file => Assert.NotNull(file.UnifiedDiff));
    }

    [Fact]
    public async Task WhitespaceOnlyChangeIsStillAChange()
    {
        await SeedAsync("rev-1", Files("pkgname=test\n"));
        await AppendAsync("rev-2", Files("pkgname=test \n"));

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        // No -w/-b: a trailing-space edit in a PKGBUILD is exactly the change a reviewer needs to see.
        Assert.Equal(DiffChangeKind.Modified, Assert.Single(diff!.Files).Kind);
    }

    [Fact]
    public async Task BinarySideIsClassifiedAndNeverHandedToTheDiffer()
    {
        // A NUL in the leading probe is what LooksBinary keys on; written as an escape so it stays visible.
        const string binaryContent = "\0PNG fake payload";

        await SeedAsync("rev-1", Files("pkgname=old\n"));
        await AppendAsync("rev-2",
            new Dictionary<string, PackageFile>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = File("pkgname=new\n"),
                ["logo.png"] = File(binaryContent)
            });

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        var binary = Assert.Single(diff!.Files, f => string.Equals(f.Path, "logo.png", StringComparison.Ordinal));
        Assert.Equal(DiffChangeKind.Binary, binary.Kind);
        Assert.Null(binary.UnifiedDiff);
        Assert.DoesNotContain(_differ.Calls, entry => string.Equals(entry.Path, "logo.png", StringComparison.Ordinal));
        // Binary wins over "added", so the size delta is what preserves the direction of the change.
        Assert.Equal(0, binary.OldSize);
        Assert.Equal(binaryContent.Length, binary.NewSize);
    }

    [Fact]
    public async Task OversizedSideIsTooLargeAndSkipsTheDiffer()
    {
        var huge = new string('x', PackageDetailsService.DiffFileRenderChars + 1);
        await SeedAsync("rev-1", new Dictionary<string, PackageFile>(StringComparer.Ordinal)
        {
            ["big.txt"] = File(huge)
        });
        await AppendAsync("rev-2",
            new Dictionary<string, PackageFile>(StringComparer.Ordinal) { ["big.txt"] = File(huge + "y") });

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        var file = Assert.Single(diff!.Files);
        Assert.Equal(DiffChangeKind.TooLarge, file.Kind);
        Assert.Null(file.UnifiedDiff);
        Assert.Empty(_differ.Calls);
        // Per-file TooLarge is self-describing on the page, so it is not a list-level truncation.
        Assert.False(diff.Truncated);
        // Sizes still report, so the page can say what was skipped.
        Assert.Equal(huge.Length, file.OldSize);
    }

    [Fact]
    public async Task TotalCharacterCapSetsTruncatedAndStopsFeedingTheDiffer()
    {
        // Each side stays under the per-file cap, so only the aggregate cap can bite.
        var chunk = new string('y', 100_000);
        var files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
        {
            ["a.txt"] = File(chunk),
            ["b.txt"] = File(chunk),
            ["c.txt"] = File(chunk)
        };
        await SeedAsync("rev-1", files);
        await AppendAsync("rev-2", new Dictionary<string, PackageFile>(StringComparer.Ordinal)
        {
            ["a.txt"] = File(chunk + "1"),
            ["b.txt"] = File(chunk + "2"),
            ["c.txt"] = File(chunk + "3")
        });

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        Assert.True(diff!.Truncated);
        Assert.Equal(3, diff.ChangedFileCount);
        // The first file fits under ContentRenderChars; every later one would push past it.
        Assert.Single(_differ.Calls);
        Assert.Equal(3, diff.Files.Count);
        Assert.Contains(diff.Files, f => f.UnifiedDiff is null);
    }

    [Fact]
    public async Task ChangedFileCapTruncatesTheRenderedList()
    {
        var old = new Dictionary<string, PackageFile>(StringComparer.Ordinal);
        var changed = new Dictionary<string, PackageFile>(StringComparer.Ordinal);
        for (var i = 0; i < PackageDetailsService.TreeRenderCap + 5; i++)
        {
            old[$"f{i:000}.txt"] = File("a\n");
            changed[$"f{i:000}.txt"] = File("b\n");
        }

        await SeedAsync("rev-1", old);
        await AppendAsync("rev-2", changed);

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        Assert.Equal(PackageDetailsService.TreeRenderCap, diff!.Files.Count);
        Assert.Equal(PackageDetailsService.TreeRenderCap + 5, diff.ChangedFileCount);
        Assert.True(diff.Truncated);
    }

    [Fact]
    public async Task UnknownFromAndToFallBackWithoutThrowing()
    {
        await SeedAsync("rev-1", Files("pkgname=one\n"));
        await AppendAsync("rev-2", Files("pkgname=two\n"));

        var badTo = await _service.GetDiffAsync(Name, null, "garbage", TestContext.Current.CancellationToken);
        var badFrom = await _service.GetDiffAsync(Name, "garbage", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(badTo);
        Assert.NotNull(badFrom);
        // An unknown target falls back to head, and the default base is then head's parent.
        Assert.True(badTo!.ToFellBack);
        Assert.Equal("rev-2", badTo.ToRevisionId);
        Assert.Equal("rev-1", badTo.FromRevisionId);
        Assert.False(badTo.Identical);
        // An unknown base falls back to head, which is also the target here - so it is the same-revision case.
        Assert.True(badFrom!.FromFellBack);
        Assert.Equal("rev-2", badFrom.FromRevisionId);
        Assert.True(badFrom.Identical);
    }

    [Fact]
    public async Task DifferFailureDegradesInsteadOfThrowing()
    {
        await SeedAsync("rev-1", Files("pkgname=one\n"));
        await AppendAsync("rev-2", Files("pkgname=two\n"));
        _differ.Throw = true;

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        Assert.True(diff!.DiffUnavailable);
        // The classified list still renders: paths, kinds, and sizes need no git.
        Assert.Equal(DiffChangeKind.Modified, Assert.Single(diff.Files).Kind);
        Assert.All(diff.Files, file => Assert.Null(file.UnifiedDiff));
    }

    [Fact]
    public async Task MissingContentDocumentIsTreatedAsAnEmptySide()
    {
        await SeedAsync("rev-1", Files("pkgname=one\n"));
        await _repository.AppendRevisionAsync(
            Name,
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId(Name, "rev-2"),
                PackageName = Name,
                RevisionId = "rev-2",
                CreatedAt = DateTimeOffset.UtcNow,
                Author = "test",
                Message = "appended",
                Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
            },
            maxRevisions: 10, ct: TestContext.Current.CancellationToken);
        await _securityRepository.PromoteHeadAsync(Name, "rev-2", TestContext.Current.CancellationToken);

        // rev-2 was never stored, so every file in rev-1 reads as removed rather than dereferencing null.
        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        var file = Assert.Single(diff!.Files);
        Assert.Equal(DiffChangeKind.Removed, file.Kind);
        Assert.Equal(0, file.NewSize);
    }

    [Fact]
    public async Task HostilePathIsClassifiedWithoutTouchingTheFilesystem()
    {
        const string traversal = "../../etc/passwd";
        const string spaced = "dir with spaces/file name.txt";
        const string absolute = "/etc/shadow";
        var old = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
        {
            [traversal] = File("old\n"),
            [spaced] = File("old\n"),
            [absolute] = File("same\n")
        };
        var changed = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
        {
            [traversal] = File("new\n"),
            [spaced] = File("new\n"),
            [absolute] = File("same\n")
        };

        await SeedAsync("rev-1", old);
        await AppendAsync("rev-2", changed);

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        var byPath = diff!.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        Assert.Equal(2, diff.ChangedFileCount);
        Assert.All(byPath.Values, file => Assert.Equal(DiffChangeKind.Modified, file.Kind));
        // The paths reach the differ verbatim; making a temp tree safe is the differ's job, not this layer's.
        Assert.Contains(_differ.Calls, entry => string.Equals(entry.Path, traversal, StringComparison.Ordinal));
        Assert.Contains(_differ.Calls, entry => string.Equals(entry.Path, spaced, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AccessCheckGatesTheBannerButNotTheDiff()
    {
        await SeedAsync("rev-1", Files("pkgname=one\n"), SecurityStatus.Flagged);
        await AppendAsync("rev-2", Files("pkgname=two\n"));
        await _securityRepository.ScanRevisionAsync(Name, "rev-2", SecurityStatus.Flagged,
            [new SecurityFinding("evil-curl", FindingSeverity.High, "pipes curl into sh", "", "PKGBUILD")]);

        var diff = await _service.GetDiffAsync(Name, "rev-1", "rev-2", TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        // Same posture as the Files tab: a flagged revision is still inspectable in the UI.
        Assert.False(diff!.Access.Allowed);
        Assert.Equal(1, diff.Files.Count);
    }

    private static PackageFile File(string content) =>
        new() { Content = content, Size = content.Length, Hash = "h" };

    private static Dictionary<string, PackageFile> Files(string pkgbuild) =>
        new(StringComparer.Ordinal) { ["PKGBUILD"] = File(pkgbuild) };

    private Task SeedAsync(
        string sha,
        Dictionary<string, PackageFile> files,
        SecurityStatus? status = null)
    {
        return SeedCoreAsync(sha, files, status);
    }

    private async Task SeedCoreAsync(
        string sha,
        Dictionary<string, PackageFile> files,
        SecurityStatus? status)
    {
        await _repository.InsertSeedAsync(
            new PackageDocument
            {
                Id = Name,
                PackageName = Name,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                HeadRevisionId = sha,
                Revisions =
                [
                    new PackageRevisionDocument
                    {
                        RevisionId = sha,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Author = "test",
                        Message = "seed"
                    }
                ]
            },
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId(Name, sha),
                PackageName = Name,
                RevisionId = sha,
                CreatedAt = DateTimeOffset.UtcNow,
                Author = "test",
                Message = "seed",
                Files = files
            },
            TestContext.Current.CancellationToken);

        if (status is not null)
            await _securityRepository.ScanRevisionAsync(Name, sha, status.Value);
    }

    private Task AppendAsync(string sha, Dictionary<string, PackageFile> files) => AppendCoreAsync(sha, files);

    private async Task AppendCoreAsync(string sha, Dictionary<string, PackageFile> files)
    {
        await _repository.AppendRevisionAsync(
            Name,
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId(Name, sha),
                PackageName = Name,
                RevisionId = sha,
                CreatedAt = DateTimeOffset.UtcNow,
                Author = "test",
                Message = "appended",
                Files = files
            },
            maxRevisions: 10, ct: TestContext.Current.CancellationToken);
    }
}
