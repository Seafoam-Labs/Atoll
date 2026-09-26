using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Packages;

public class PackageServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> SampleFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        };

    private static PackageService CreateService(
        InMemoryPackageRepository repo,
        IPackageSecurityRepository? securityRepository = null,
        PackageIndexStore? indexStore = null,
        HybridCache? cache = null,
        int rankTtlSeconds = 30)
    {
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Caching = new CachingOptions { RankTtlSeconds = rankTtlSeconds }
        });
        return new PackageService(repo, options, securityRepository ?? new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, securityRepository ?? new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance), cache ?? TestHybridCache.New(), indexStore ?? new PackageIndexStore());
    }

    private static async Task<SearchIndexData> LoadIndexAsync(string packagesJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"atoll-sort-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, packagesJson, TestContext.Current.CancellationToken);
        try
        {
            return await PackageIndexBuilder.LoadAsync(path, CancellationToken.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<PackageIndexStore> CreateIndexStoreAsync(string packagesJson)
    {
        var store = new PackageIndexStore();
        store.Replace(await LoadIndexAsync(packagesJson));
        return store;
    }

    private static AurPackageMetadata Meta(string name, int numVotes, double popularity)
    {
        return new AurPackageMetadata(0, name, 0, name, "1.0", "d", null, numVotes, popularity, null, null, null, 0, 0, "",
            [], [], [], [], [], [], [], []);
    }

    [Fact]
    public async Task GetAsync_AfterSeed_ReturnsFiles()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var files = await service.GetAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.Equivalent(SampleFiles.Keys, files.Files.Keys, strict: true);
            Assert.Equal(SampleFiles["PKGBUILD"], files.Files["PKGBUILD"]);
            Assert.Equal(SampleFiles[".SRCINFO"], files.Files[".SRCINFO"]);
        });
    }

    [Fact]
    public async Task GetHistoryAsync_AfterSeed_ReturnsOneRevision()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var history = await service.GetHistoryAsync("shelly");

        Assert.Multiple(() =>
        {
            var revision = Assert.Single(history);
            Assert.Equal(64, revision.Sha.Length);
            Assert.Equal("aur", revision.Author);
            Assert.Equal("seed from AUR", revision.Message);
        });
    }

    [Fact]
    public async Task GetAsync_SeededRevisionSha_ReturnsFiles()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var history = await service.GetHistoryAsync("shelly");
        var sha = history[0].Sha;

        var byRevision = await service.GetAsync("shelly", sha);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(SampleFiles.Keys, byRevision.Files.Keys, strict: true);
            Assert.Equal(SampleFiles["PKGBUILD"], byRevision.Files["PKGBUILD"]);
        });
    }

    [Fact]
    public async Task SeedFilesAsync_ExistingPackage_ThrowsPackageConflict()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);

        var ex = await Assert.ThrowsAsync<PackageConflictException>(async () => await service.SeedFilesAsync("shelly", SampleFiles));

        Assert.Equal("shelly", ex.PackageName);
    }

    [Fact]
    public async Task SeedFilesAsync_OversizedFile_Throws()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 1_024, MaxRevisions = 10 }
        });
        var service = new PackageService(repo, options, new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), new PackageIndexStore());

        var big = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["big.bin"] = new('x', 2_048)
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.SeedFilesAsync("big-pkg", big));

        Assert.Contains("big.bin", ex.Message, StringComparison.Ordinal);
        Assert.False(await repo.ExistsAsync("big-pkg", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedFilesAsync_DocumentOverMongoLimit_ThrowsTypedExceptionBeforeInsert()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 10_485_760, MaxRevisions = 10 }
        });
        var service = new PackageService(repo, options, new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), new PackageIndexStore());
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["large-1.txt"] = new('x', 9_000_000),
            ["large-2.txt"] = new('x', 9_000_000)
        };

        var ex = await Assert.ThrowsAsync<PackageDocumentTooLargeException>(async () => await service.SeedFilesAsync("too-large", files));
        var packageExists = await repo.ExistsAsync("too-large", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal("too-large", ex.PackageName);
            Assert.True(ex.SerializedSizeBytes > ex.MaxDocumentSizeBytes);
            Assert.False(packageExists);
        });
    }

    [Fact]
    public async Task AppendRevisionFromUpstreamAsync_OversizedSnapshot_ThrowsBeforeWrite()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 10_485_760, MaxRevisions = 10 }
        });
        var service = new PackageService(repo, options, new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), new PackageIndexStore());
        await service.SeedFilesAsync("pkg", SampleFiles);
        var history = await service.GetHistoryAsync("pkg");
        var originalHead = history[0].Sha;
        var oversizedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["large-1.txt"] = new('x', 9_000_000),
            ["large-2.txt"] = new('x', 9_000_000)
        };

        await Assert.ThrowsAsync<PackageDocumentTooLargeException>(async () =>
            await service.AppendRevisionFromUpstreamAsync("pkg", oversizedFiles, TestContext.Current.CancellationToken));
        var afterHistory = await service.GetHistoryAsync("pkg");
        var packageExists = await repo.ExistsAsync("pkg", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            var revision = Assert.Single(afterHistory);
            Assert.Equal(originalHead, revision.Sha);
            Assert.True(packageExists);
        });
    }

    [Fact]
    public async Task GetAsync_AfterDelete_ThrowsNotFound()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        await service.DeleteAsync("shelly", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("shelly"));
    }

    [Fact]
    public async Task GetAsync_UnknownPackage_ThrowsNotFound()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("missing"));
    }

    [Fact]
    public async Task GetAsync_UnknownRevision_ThrowsNotFound()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("shelly", "deadbeef"));
    }

    [Fact]
    public async Task ListAsync_SeededPackages_ReturnsNames()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        await service.SeedFilesAsync("other", SampleFiles);

        var names = await service.ListAsync();

        Assert.Equivalent(new[] { "shelly", "other" }, names, strict: true);
    }

    [Fact]
    public async Task GetIndexPageAsync_VotesAscending_SortsAcrossPages()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo, indexStore: await CreateIndexStoreAsync(
            """
            [
              { "Name": "v-a", "NumVotes": 5 },
              { "Name": "v-b", "NumVotes": 50 },
              { "Name": "v-c", "NumVotes": 50 }
            ]
            """));

        foreach (var name in new[] { "v-a", "v-b", "v-c", "v-missing" })
            await service.SeedFilesAsync(name, SampleFiles);

        var firstPage = await service.GetIndexPageAsync(1, 2, PackageIndexSortBy.Votes, ct: TestContext.Current.CancellationToken);
        var secondPage = await service.GetIndexPageAsync(2, 2, PackageIndexSortBy.Votes, ct: TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            // Ascending is the uniform default; packages absent from the catalog rank as zero votes.
            Assert.Equal(new[] { "v-missing", "v-a" }, firstPage.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "v-b", "v-c" }, secondPage.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(4, secondPage.TotalItems);
            Assert.Equal(2, secondPage.TotalPages);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_VotesDescending_SortsWithNameTieBreak()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo, indexStore: await CreateIndexStoreAsync(
            """
            [
              { "Name": "v-a", "NumVotes": 5 },
              { "Name": "v-b", "NumVotes": 50 },
              { "Name": "v-c", "NumVotes": 50 }
            ]
            """));

        foreach (var name in new[] { "v-a", "v-b", "v-c", "v-missing" })
            await service.SeedFilesAsync(name, SampleFiles);

        var firstPage = await service.GetIndexPageAsync(1, 2, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);
        var secondPage = await service.GetIndexPageAsync(2, 2, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            // Equal votes tie-break on name; packages absent from the catalog rank as zero votes.
            Assert.Equal(new[] { "v-b", "v-c" }, firstPage.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "v-a", "v-missing" }, secondPage.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Null(secondPage.Items[1].NumVotes);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_PopularityDescending_SortsAcrossPages()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo, indexStore: await CreateIndexStoreAsync(
            """
            [
              { "Name": "p-a", "Popularity": 2.5 },
              { "Name": "p-b", "Popularity": 10.5 },
              { "Name": "p-c", "Popularity": 2.5 }
            ]
            """));

        foreach (var name in new[] { "p-a", "p-b", "p-c" })
            await service.SeedFilesAsync(name, SampleFiles);

        var firstPage = await service.GetIndexPageAsync(1, 2, PackageIndexSortBy.Popularity, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);
        var secondPage = await service.GetIndexPageAsync(2, 2, PackageIndexSortBy.Popularity, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(new[] { "p-b", "p-a" }, firstPage.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(10.5, firstPage.Items[0].Popularity);
            Assert.Equal(new[] { "p-c" }, secondPage.Items.Select(item => item.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_VersionDescending_ComparesOrdinallyWithNullsLast()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo, indexStore: await CreateIndexStoreAsync(
            """
            [
              { "Name": "ver-a", "Version": "2.0.0-1" },
              { "Name": "ver-b", "Version": "10.0.0-1" },
              { "Name": "ver-c", "Version": "2.0.0-1" }
            ]
            """));

        foreach (var name in new[] { "ver-a", "ver-b", "ver-c", "ver-missing" })
            await service.SeedFilesAsync(name, SampleFiles);

        var firstPage = await service.GetIndexPageAsync(1, 2, PackageIndexSortBy.Version, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);
        var secondPage = await service.GetIndexPageAsync(2, 2, PackageIndexSortBy.Version, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            // Version strings compare ordinally, so "2.0.0-1" outranks "10.0.0-1"; a package
            // absent from the catalog has no version and sorts last.
            Assert.Equal(new[] { "ver-a", "ver-c" }, firstPage.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "ver-b", "ver-missing" }, secondPage.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Null(secondPage.Items[1].Version);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_NameDescending_SortsAcrossPages()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        foreach (var name in new[] { "n-a", "n-b", "n-c" })
            await service.SeedFilesAsync(name, SampleFiles);

        var firstPage = await service.GetIndexPageAsync(1, 2, PackageIndexSortBy.Name, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);
        var secondPage = await service.GetIndexPageAsync(2, 2, PackageIndexSortBy.Name, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(new[] { "n-c", "n-b" }, firstPage.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "n-a" }, secondPage.Items.Select(item => item.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_VersionAscending_SortsCatalogAbsentFirst()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo, indexStore: await CreateIndexStoreAsync(
            """
            [
              { "Name": "ver-a", "Version": "2.0.0-1" },
              { "Name": "ver-b", "Version": "10.0.0-1" },
              { "Name": "ver-c", "Version": "2.0.0-1" }
            ]
            """));

        foreach (var name in new[] { "ver-a", "ver-b", "ver-c", "ver-missing" })
            await service.SeedFilesAsync(name, SampleFiles);

        var page = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Version, ct: TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            // Absent from the catalog ranks as a null version, which sorts first ascending.
            Assert.Equal(new[] { "ver-missing", "ver-b", "ver-a", "ver-c" },
                page.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Null(page.Items[0].Version);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_CatalogRows_JoinsDetailFieldsAndLeavesAbsentNull()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo, indexStore: await CreateIndexStoreAsync(
            """
            [
              {
                "Name": "j-shelly",
                "PackageBase": "shelly",
                "License": ["MIT"],
                "Depends": ["pacman>=6"],
                "Provides": ["shelly"],
                "FirstSubmitted": 1700000000,
                "LastModified": 1720000000
              }
            ]
            """));

        await service.SeedFilesAsync("j-shelly", SampleFiles);
        await service.SeedFilesAsync("j-absent", SampleFiles);

        var page = await service.GetIndexPageAsync(1, 10, ct: TestContext.Current.CancellationToken);
        var joined = page.Items.Single(item => string.Equals(item.Name, "j-shelly", StringComparison.Ordinal));
        var absent = page.Items.Single(item => string.Equals(item.Name, "j-absent", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.Equal("shelly", joined.PackageBase);
            Assert.Equal(1700000000, joined.FirstSubmitted);
            Assert.Equal(1720000000, joined.LastModified);
            Assert.Equivalent(new[] { "MIT" }, joined.License, strict: true);
            Assert.Equivalent(new[] { "pacman>=6" }, joined.Depends, strict: true);
            Assert.Equivalent(new[] { "shelly" }, joined.Provides, strict: true);

            // The dump omits these keys for a package it does carry, so the row reports a known
            // absence as an empty array rather than the missing-from-dump null.
            Assert.Equivalent(Array.Empty<string>(), joined.MakeDepends, strict: true);
            Assert.Equivalent(Array.Empty<string>(), joined.OptDepends, strict: true);

            Assert.Null(absent.PackageBase);
            Assert.Null(absent.FirstSubmitted);
            Assert.Null(absent.LastModified);
            Assert.Null(absent.License);
            Assert.Null(absent.Depends);
            Assert.Null(absent.MakeDepends);
            Assert.Null(absent.OptDepends);
            Assert.Null(absent.Provides);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_CachedRankings_MatchDirectSortsOfSameCorpus()
    {
        // One cached corpus, three sort keys covering both key components: each page must equal a
        // plain LINQ sort of the same names and keys, so a mis-keyed or wrongly unwrapped entry
        // cannot pass.
        var corpus = new (string Name, int NumVotes, double Popularity)[]
        {
            ("o-a", 7, 0.5),
            ("o-b", 3, 0.1),
            ("o-c", 7, 9.9),
            ("o-d", 0, 0.2)
        };

        var store = new PackageIndexStore();
        store.Replace(PackageIndexBuilder.BuildFromPackages(
            [.. corpus.Select(entry => Meta(entry.Name, entry.NumVotes, entry.Popularity))]));

        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo, indexStore: store);
        foreach (var entry in corpus)
            await service.SeedFilesAsync(entry.Name, SampleFiles);

        var ct = TestContext.Current.CancellationToken;
        var votesDesc = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, ct);
        var votesAsc = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Asc, ct);
        var popularity = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Popularity, PackageIndexSortOrder.Desc, ct);

        Assert.Multiple(() =>
        {
            Assert.Equal(
                corpus.OrderByDescending(entry => entry.NumVotes).ThenBy(entry => entry.Name, StringComparer.Ordinal).Select(entry => entry.Name),
                votesDesc.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(
                corpus.OrderBy(entry => entry.NumVotes).ThenBy(entry => entry.Name, StringComparer.Ordinal).Select(entry => entry.Name),
                votesAsc.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(
                corpus.OrderByDescending(entry => entry.Popularity).ThenBy(entry => entry.Name, StringComparer.Ordinal).Select(entry => entry.Name),
                popularity.Items.Select(item => item.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_IndexSwapWithoutWrite_ServesPreviousRanking()
    {
        // An index swap alone does not drop the cached ranking; the worker's swap warm is what
        // rebuilds it (see the prewarm Theory below). A seeded-set write removes the catalog tag,
        // the healing path this test takes.
        var repo = new InMemoryPackageRepository();
        var store = await CreateIndexStoreAsync(
            """
            [
              { "Name": "s-a", "NumVotes": 1 },
              { "Name": "s-b", "NumVotes": 9 }
            ]
            """);
        var service = CreateService(repo, indexStore: store);

        foreach (var name in new[] { "s-a", "s-b" })
            await service.SeedFilesAsync(name, SampleFiles);

        var before = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        store.Replace(await LoadIndexAsync(
            """
            [
              { "Name": "s-a", "NumVotes": 9 },
              { "Name": "s-b", "NumVotes": 1 }
            ]
            """));

        var stale = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        await service.SeedFilesAsync("s-c", SampleFiles);
        var healed = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(new[] { "s-b", "s-a" }, before.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "s-b", "s-a" }, stale.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "s-a", "s-b", "s-c" }, healed.Items.Select(item => item.Name), StringComparer.Ordinal);
        });
    }

    private const string FirstGeneration =
        """
        [
          { "Name": "x-a", "NumVotes": 1, "Popularity": 1.0, "Version": "1.0" },
          { "Name": "x-b", "NumVotes": 2, "Popularity": 2.0, "Version": "2.0" },
          { "Name": "x-c", "NumVotes": 3, "Popularity": 3.0, "Version": "3.0" }
        ]
        """;

    private const string SecondGeneration =
        """
        [
          { "Name": "x-a", "NumVotes": 3, "Popularity": 3.0, "Version": "3.0" },
          { "Name": "x-b", "NumVotes": 2, "Popularity": 2.0, "Version": "2.0" },
          { "Name": "x-c", "NumVotes": 1, "Popularity": 1.0, "Version": "1.0" }
        ]
        """;

    [Theory]
    [InlineData(PackageIndexSortBy.Votes, PackageIndexSortOrder.Asc)]
    [InlineData(PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc)]
    [InlineData(PackageIndexSortBy.Popularity, PackageIndexSortOrder.Asc)]
    [InlineData(PackageIndexSortBy.Popularity, PackageIndexSortOrder.Desc)]
    [InlineData(PackageIndexSortBy.Version, PackageIndexSortOrder.Asc)]
    [InlineData(PackageIndexSortBy.Version, PackageIndexSortOrder.Desc)]
    public async Task PrewarmAsync_AfterIndexSwap_RebuildsEveryKeyedRanking(
        PackageIndexSortBy sortBy, PackageIndexSortOrder order)
    {
        // Each request before the swap builds the pair's cached array from the first generation; the
        // swap plus Prewarm must replace it, or the same request would still serve the old ordering.
        // The name sorts are absent because a swap cannot change the Mongo-backed names, so their
        // rebuild has no observable effect.
        var repo = new InMemoryPackageRepository();
        var store = await CreateIndexStoreAsync(FirstGeneration);
        var service = CreateService(repo, indexStore: store);

        foreach (var name in new[] { "x-a", "x-b", "x-c" })
            await service.SeedFilesAsync(name, SampleFiles);

        var ct = TestContext.Current.CancellationToken;
        var before = await service.GetIndexPageAsync(1, 10, sortBy, order, ct);

        store.Replace(await LoadIndexAsync(SecondGeneration));
        await service.PrewarmAsync(ct);
        var after = await service.GetIndexPageAsync(1, 10, sortBy, order, ct);

        var ascending = order is PackageIndexSortOrder.Asc;
        var expectedBefore = ascending ? new[] { "x-a", "x-b", "x-c" } : new[] { "x-c", "x-b", "x-a" };
        var expectedAfter = ascending ? new[] { "x-c", "x-b", "x-a" } : new[] { "x-a", "x-b", "x-c" };
        Assert.Multiple(() =>
        {
            Assert.Equal(expectedBefore, before.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(expectedAfter, after.Items.Select(item => item.Name), StringComparer.Ordinal);

            // The swap warm re-ranks; the seeded names come from Mongo and are not rescanned.
            Assert.Equal(1, repo.ListCalls);
        });
    }

    [Fact]
    public async Task SeedFilesAsync_AfterSwapWarm_EvictsRankedViews()
    {
        // The warm stores the sorted arrays with SetAsync; the catalog tag must cover those stores
        // exactly as it covers factory-built ones, or a seed would leave them stale for a TTL.
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);
        await service.SeedFilesAsync("g-a", SampleFiles);

        var ct = TestContext.Current.CancellationToken;
        await service.PrewarmAsync(ct);
        var warmed = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, ct);

        await service.SeedFilesAsync("g-b", SampleFiles);
        var afterWrite = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, ct);

        Assert.Multiple(() =>
        {
            Assert.Equal(new[] { "g-a" }, warmed.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "g-a", "g-b" }, afterWrite.Items.Select(item => item.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task PrewarmAsync_WarmsWithinTtl_RearmsNameList()
    {
        // Every warm lands well inside the 2 s TTL, so filling alone would still let the entry expire
        // on its own clock and hand the repository scan to the next request. The re-store carries it.
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo, rankTtlSeconds: 2);
        await service.SeedFilesAsync("r-a", SampleFiles);

        var ct = TestContext.Current.CancellationToken;
        await service.PrewarmAsync(ct);
        await Task.Delay(1200, ct);
        await service.PrewarmAsync(ct);
        await Task.Delay(1200, ct);
        await service.PrewarmAsync(ct);

        Assert.Equal(1, repo.ListCalls);
    }

    [Fact]
    public async Task GetIndexPageAsync_SeedThenDelete_TracksMutations()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("m-a", SampleFiles);
        var initial = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        await service.SeedFilesAsync("m-b", SampleFiles);
        var afterSeed = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        await service.DeleteAsync("m-a", TestContext.Current.CancellationToken);
        var afterDelete = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(new[] { "m-a" }, initial.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "m-a", "m-b" }, afterSeed.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(2, afterSeed.TotalItems);
            Assert.Equal(new[] { "m-b" }, afterDelete.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(1, afterDelete.TotalItems);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_RankingBuiltAcrossSeed_ServesStaleUntilNextWrite()
    {
        // A tag removal landing while a factory is in flight does not evict that factory's later
        // store, so a ranking built across a seed serves stale names until the next write or TTL.
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);
        await service.SeedFilesAsync("r-a", SampleFiles);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.AfterListAsync = () =>
        {
            entered.SetResult();
            return release.Task;
        };

        var inFlight = service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);
        await entered.Task;

        // Seed once the ranking read has snapshotted the names, then let that read return them.
        await service.SeedFilesAsync("r-b", SampleFiles);
        repo.AfterListAsync = null;
        release.SetResult();
        var stale = await inFlight;
        var staleNext = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        await service.SeedFilesAsync("r-c", SampleFiles);
        var healed = await service.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(new[] { "r-a" }, stale.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "r-a" }, staleNext.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(new[] { "r-a", "r-b", "r-c" }, healed.Items.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(3, healed.TotalItems);
        });
    }

    [Fact]
    public async Task GetIndexPageAsync_TiedSort_PagesDeterministically()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo, indexStore: await CreateIndexStoreAsync(
            """
            [
              { "Name": "t-a", "NumVotes": 5 },
              { "Name": "t-b", "NumVotes": 5 },
              { "Name": "t-c", "NumVotes": 5 },
              { "Name": "t-d", "NumVotes": 5 },
              { "Name": "t-e", "NumVotes": 5 },
              { "Name": "t-f", "NumVotes": 5 },
              { "Name": "t-g", "NumVotes": 5 }
            ]
            """));

        foreach (var name in new[] { "t-d", "t-a", "t-g", "t-b", "t-f", "t-c", "t-e" })
            await service.SeedFilesAsync(name, SampleFiles);

        var names = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            var response = await service.GetIndexPageAsync(page, 2, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);
            Assert.Equal(4, response.TotalPages);
            names.AddRange(response.Items.Select(item => item.Name));
        }

        // Every page is a slice of the same tied-key ranking: name ascending on the tie-break.
        Assert.Equal(new[] { "t-a", "t-b", "t-c", "t-d", "t-e", "t-f", "t-g" }, names, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SeedFilesAsync_SameContentAcrossInstances_ProducesSameRevisionSha()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var firstHistory = await service.GetHistoryAsync("shelly");

        var repo2 = new InMemoryPackageRepository();
        var service2 = CreateService(repo2);
        await service2.SeedFilesAsync("shelly", SampleFiles);
        var secondHistory = await service2.GetHistoryAsync("shelly");

        Assert.Equal(secondHistory[0].Sha, firstHistory[0].Sha);
    }

    [Fact]
    public async Task SeedFilesAsync_EstimateOverLimitButExactBsonUnder_Accepts()
    {
        // Two files totalling 16,776,000 content bytes: the conservative estimate
        // (content + 160/file + 1024) exceeds the 16 MiB BSON limit, but the exact
        // ToBson() measurement stays under it, so the exact second pass must admit it.
        const int perFile = 8_388_000;
        var estimated = 2L * perFile + 2 * (160 + "large-1.txt".Length) + 1024;
        Assert.True(estimated > 16 * 1024 * 1024,
            "precondition: the conservative estimate must exceed the BSON limit");

        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 10_485_760, MaxRevisions = 10 }
        });
        var service = new PackageService(repo, options, new InMemoryPackageSecurityRepository(), new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, new InMemoryPackageSecurityRepository(), options, NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), new PackageIndexStore());
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["large-1.txt"] = new('x', perFile),
            ["large-2.txt"] = new('x', perFile)
        };

        await service.SeedFilesAsync("boundary", files);

        var persisted = await service.GetAsync("boundary");
        var packageExists = await repo.ExistsAsync("boundary", TestContext.Current.CancellationToken);
        Assert.Multiple(() =>
        {
            Assert.True(packageExists);
            Assert.Equal(perFile, persisted.Files["large-1.txt"].Length);
        });
    }

    [Fact]
    public async Task DeleteAsync_FailureAfterDerivedCleanup_LeavesPackageDeletable()
    {
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var reposRoot = Path.Combine(Path.GetTempPath(), $"atoll-delete-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reposRoot);
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
            Git = new GitOptions { RepositoriesPath = reposRoot }
        });
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var store = new PackageIndexStore();
        var service = new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache, TestHybridCache.New(), store);
        var failingOnce = new ThrowOnceOnDeleteRepository(repo);
        var retryService = new PackageService(failingOnce, options, security, new PkgBuildSecurityScanner(), cache, TestHybridCache.New(), store);

        try
        {
            await service.SeedFilesAsync("shelly", SampleFiles);
            Assert.Equal(1, await security.CountPendingAsync(TestContext.Current.CancellationToken));
            var repoDir = cache.GetRepositoryPath("shelly")!;
            Directory.CreateDirectory(repoDir);
            await File.WriteAllTextAsync(Path.Combine(repoDir, "HEAD"), "marker-for-cleanup", TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await retryService.DeleteAsync("shelly", TestContext.Current.CancellationToken));

            var remainingScans = await security.ListForPackageAsync("shelly", TestContext.Current.CancellationToken);
            var packageStillExists = await repo.ExistsAsync("shelly", TestContext.Current.CancellationToken);
            Assert.Multiple(() =>
            {
                // Derived state is removed before the authoritative document, so the failed
                // delete leaves no orphaned scan records or on-disk cache...
                Assert.Empty(remainingScans);
                Assert.False(Directory.Exists(repoDir));
                // ...while the package document survives, keeping the delete retryable.
                Assert.True(packageStillExists);
            });

            await retryService.DeleteAsync("shelly", TestContext.Current.CancellationToken);

            Assert.False(await repo.ExistsAsync("shelly", TestContext.Current.CancellationToken));
        }
        finally
        {
            try
            {
                if (Directory.Exists(reposRoot))
                    Directory.Delete(reposRoot, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private sealed class ThrowOnceOnDeleteRepository(IPackageRepository inner) : IPackageRepository
    {
        public bool HasThrown { get; private set; }

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
        {
            return inner.ListAsync(ct);
        }

        public Task<long> CountAsync(CancellationToken ct = default)
        {
            return inner.CountAsync(ct);
        }

        public Task<IReadOnlyList<PackageIndexEntry>> ListIndexPageAsync(int skip, int take, CancellationToken ct = default)
        {
            return inner.ListIndexPageAsync(skip, take, ct);
        }

        public Task<IReadOnlyList<PackageIndexEntry>> ListIndexEntriesAsync(
            IReadOnlyCollection<string> names, CancellationToken ct = default)
        {
            return inner.ListIndexEntriesAsync(names, ct);
        }

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
        {
            return inner.ExistsAsync(packageName, ct);
        }

        public Task<PackageDocument?> GetHeadAsync(string packageName, CancellationToken ct = default)
        {
            return inner.GetHeadAsync(packageName, ct);
        }

        public Task<string?> GetHeadRevisionIdAsync(string packageName, CancellationToken ct = default)
        {
            return inner.GetHeadRevisionIdAsync(packageName, ct);
        }

        public Task<PackageRevisionContentDocument?> GetRevisionAsync(
            string packageName, string revisionId, CancellationToken ct = default)
        {
            return inner.GetRevisionAsync(packageName, revisionId, ct);
        }

        public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName, CancellationToken ct = default)
        {
            return inner.GetHistoryAsync(packageName, ct);
        }

        public Task InsertSeedAsync(
            PackageDocument doc, PackageRevisionContentDocument revision, CancellationToken ct = default)
        {
            return inner.InsertSeedAsync(doc, revision, ct);
        }

        public Task AppendRevisionAsync(
            string packageName, PackageRevisionContentDocument revision, int maxRevisions,
            CancellationToken ct = default)
        {
            return inner.AppendRevisionAsync(packageName, revision, maxRevisions, ct);
        }

        public Task<long> TrimExcessRevisionsAsync(int maxRevisions, CancellationToken ct = default)
        {
            return inner.TrimExcessRevisionsAsync(maxRevisions, ct);
        }

        public Task<IReadOnlyList<PackageSyncState>> ListSyncStatesAsync(CancellationToken ct = default)
        {
            return inner.ListSyncStatesAsync(ct);
        }

        public Task UpdateSyncStateAsync(
            IReadOnlyCollection<string> packageNames, string? upstreamHead, bool succeeded, string? errorMessage,
            CancellationToken ct = default)
        {
            return inner.UpdateSyncStateAsync(packageNames, upstreamHead, succeeded, errorMessage, ct);
        }

        public Task DeleteAsync(string packageName, CancellationToken ct = default)
        {
            if (!HasThrown)
            {
                HasThrown = true;
                throw new InvalidOperationException("simulated delete failure");
            }

            return inner.DeleteAsync(packageName, ct);
        }
    }
}