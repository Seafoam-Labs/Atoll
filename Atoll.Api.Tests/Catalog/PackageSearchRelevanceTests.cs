using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

public class PackageSearchRelevanceTests
{
    [Fact]
    public void SingleTermOrdersByTheTierLadder()
    {
        var snapshot = Index(
            Pkg("vim"),
            Pkg("editor-x", provides: ["vim"]),
            Pkg("vim-extra"),
            Pkg("foo-vim-bar"),
            Pkg("foo-vimtool"),
            Pkg("editor-plus", description: "vim editor"));

        var hits = PackageSearchEngine.Rank(snapshot, "vim");

        Assert.Equal(
            ["vim", "editor-x", "vim-extra", "foo-vim-bar", "foo-vimtool", "editor-plus"],
            Names(hits));
        Assert.Equal(
            [
                SearchTier.ExactName, SearchTier.ExactProvides, SearchTier.NamePrefix, SearchTier.NameToken,
                SearchTier.NameTokenPrefix, SearchTier.WordPosting
            ],
            [.. hits.Select(hit => hit.Tier)]);
    }

    [Fact]
    public void BareInfixNamesAreNotCandidates()
    {
        // A substring that is neither a name prefix nor a cleaned name token resolves nothing: the
        // infix tier was dropped when retrieval moved onto the index structures.
        var snapshot = Index(Pkg("neovim-git"), Pkg("crust-git"), Pkg("vim"), Pkg("rust"));

        Assert.Multiple(() =>
        {
            Assert.Equal(["vim"], Names(PackageSearchEngine.Rank(snapshot, "vim")));
            Assert.Equal(["rust"], Names(PackageSearchEngine.Rank(snapshot, "rust")));
        });
    }

    [Fact]
    public void EveryCaseVariantOfANameResolvesAsExact()
    {
        // Case variants compare equal in the sorted-name order, so the exact run has to be walked in
        // full rather than stopped at the binary search's landing slot.
        var snapshot = Index(Pkg("Vim"), Pkg("vim"), Pkg("VIM"));

        var hits = PackageSearchEngine.Rank(snapshot, "vim");

        Assert.Equal(["VIM", "Vim", "vim"], Names(hits));
        Assert.All(hits, hit => Assert.Equal(SearchTier.ExactName, hit.Tier));
    }

    [Fact]
    public void NameMatchingDoesNotFoldBeyondOrdinalIgnoreCase()
    {
        // OrdinalIgnoreCase folds a dotted i to I but keeps dotless ı and dotted İ apart. The sorted
        // run is located with the same comparison the walk re-tests, so the index cannot widen or
        // narrow the served set relative to the predicate it replaced.
        var snapshot = Index(Pkg("İstanbul-theme"), Pkg("istanbul-theme"), Pkg("ıspanak-theme"));

        Assert.Multiple(() =>
        {
            Assert.Equal(["istanbul-theme"], Names(PackageSearchEngine.Rank(snapshot, "istanbul")));
            Assert.Equal(["istanbul-theme"], Names(PackageSearchEngine.Rank(snapshot, "I")));
            Assert.Equal(["ıspanak-theme"], Names(PackageSearchEngine.Rank(snapshot, "ıspanak")));
        });
    }

    [Fact]
    public void MultiTermRanksFullCoverageAbovePartialThenByName()
    {
        var snapshot = Index(
            Pkg("vim-editor"),
            Pkg("vim-only"),
            Pkg("editor-only"));

        var hits = PackageSearchEngine.Rank(snapshot, "vim editor");

        Assert.Equal(["vim-editor", "editor-only", "vim-only"], Names(hits));
        Assert.Multiple(() =>
        {
            Assert.Equal(2, hits[0].MatchedTermCount);
            Assert.Equal(1, hits[1].MatchedTermCount);
            Assert.Equal(1, hits[2].MatchedTermCount);
        });
    }

    [Theory]
    [InlineData("neovim-git")]
    [InlineData("yay-git")]
    [InlineData("paru-git")]
    [InlineData("visual-studio-code-bin")]
    public void ExactCompoundNameRanksFirstAndStaysOneTerm(string name)
    {
        // The segment resolves through its parts too, so the wider family becomes a candidate; the
        // exact name still wins on the raw-segment tier, and coverage stays one bit because the
        // compound is one term rather than two.
        var snapshot = Index(
            Pkg("neovim-git", votes: 263), Pkg("neovim-gitsigns"), Pkg("neovim-gitsigns-git"),
            Pkg("yay", votes: 900), Pkg("yay-git", votes: 12),
            Pkg("paru", votes: 300), Pkg("paru-git", votes: 40),
            Pkg("visual-studio-code-bin", votes: 800), Pkg("code-marketplace", votes: 60));

        var hits = PackageSearchEngine.Rank(snapshot, name);

        Assert.Multiple(() =>
        {
            Assert.Equal(name, hits[0].Package.Name);
            Assert.Equal(SearchTier.ExactName, hits[0].Tier);
            Assert.Equal(1, hits[0].MatchedTermCount);
        });
    }

    [Fact]
    public void CompoundNameAbsentFromTheCorpusReachesItsTokenFamily()
    {
        var snapshot = Index(
            Pkg("arc-gtk-theme", votes: 100), Pkg("arc-gtk-theme-git", votes: 5), Pkg("gtk-theme-numix"));

        var hits = PackageSearchEngine.Rank(snapshot, "gtk-theme-arc");

        Assert.Equal(["arc-gtk-theme", "arc-gtk-theme-git", "gtk-theme-numix"], Names(hits));
        Assert.All(hits, hit => Assert.Equal(SearchTier.NameToken, hit.Tier));
    }

    [Fact]
    public void CompoundNamePrefixStillBeatsTheTokenFamily()
    {
        // "linux-zen-headers-bin" is a name-prefix hit on the raw segment, so it outranks the
        // token-only matches the parts bring in.
        var snapshot = Index(
            Pkg("linux-zen-git-headers", votes: 30),
            Pkg("linux-zen-headers-bin", votes: 10),
            Pkg("linux-headers", votes: 5));

        var hits = PackageSearchEngine.Rank(snapshot, "linux-zen-headers");

        Assert.Equal(["linux-zen-headers-bin", "linux-zen-git-headers", "linux-headers"], Names(hits));
    }

    [Fact]
    public void ProvidesStillReadsTheRawSegmentVerbatim()
    {
        var snapshot = Index(Pkg("libegl-provider", provides: ["libEGL.so"]));

        var hit = Assert.Single(PackageSearchEngine.Rank(snapshot, "libEGL.so"));

        Assert.Equal(SearchTier.ExactProvides, hit.Tier);
    }

    [Fact]
    public void SameTierOrdersByVotesDescendingThenNameAscending()
    {
        var snapshot = Index(
            Pkg("aaa-tool", votes: 5),
            Pkg("bbb-tool", votes: 5),
            Pkg("ccc-tool", votes: 50));

        var hits = PackageSearchEngine.Rank(snapshot, "tool");

        Assert.Equal(["ccc-tool", "aaa-tool", "bbb-tool"], Names(hits));
    }

    [Fact]
    public void NameMatchIsCaseInsensitive()
    {
        var snapshot = Index(Pkg("shelly-bin"));

        var hits = PackageSearchEngine.Rank(snapshot, "SHELLY");

        Assert.Equal(["shelly-bin"], Names(hits));
        Assert.Equal(SearchTier.NamePrefix, Assert.Single(hits).Tier);
    }

    [Fact]
    public void ProvidesMatchIsCaseSensitive()
    {
        var snapshot = Index(Pkg("provider", provides: ["SHELLY"]));

        Assert.Equal(["provider"], Names(PackageSearchEngine.Rank(snapshot, "SHELLY")));
        Assert.Empty(PackageSearchEngine.Rank(snapshot, "shelly"));
    }

    [Fact]
    public void ExactNameBeatsProvidesSelfNameFallback()
    {
        // A package with no provides gets a self-name provides entry; P0 must still win over P1.
        var snapshot = Index(Pkg("vim"));

        var hit = Assert.Single(PackageSearchEngine.Rank(snapshot, "vim"));

        Assert.Equal(SearchTier.ExactName, hit.Tier);
    }

    [Fact]
    public void NoMatchReturnsEmpty()
    {
        var snapshot = Index(Pkg("vim"), Pkg("neovim-git"));

        Assert.Empty(PackageSearchEngine.Rank(snapshot, "brwose"));
    }

    [Fact]
    public void EmptyQueryReturnsEmpty()
    {
        var snapshot = Index(Pkg("vim"));

        Assert.Empty(PackageSearchEngine.Rank(snapshot, "   "));
    }

    [Fact]
    public void ServiceCapsRankedResultsAtFifty()
    {
        // 60 name-prefix matches, identical tier/coverage/votes: the total order ends on ordinal name.
        var packages = Enumerable.Range(0, 60).Select(i => Pkg($"pkg-{i:00}")).ToArray();
        var store = new PackageIndexStore();
        store.Replace(Index(packages));
        var service = new PackageSearchService(new PackageSearchEngine(store));

        var results = service.FindByRelevance("pkg");

        Assert.Multiple(() =>
        {
            Assert.Equal(50, results.Length);
            Assert.Equal("pkg-00", results[0].Name);
            Assert.Equal("pkg-49", results[^1].Name);
        });
    }

    private static SearchIndexData Index(params AurPackageMetadata[] packages) =>
        PackageIndexBuilder.BuildFromPackages(packages);

    private static AurPackageMetadata Pkg(
        string name, long votes = 0, string[]? provides = null, string description = "") =>
        new(0, name, 0, name, "1.0-1", description, null, votes, 0, null, null, null, 0, 0, "",
            [], [], [], [], provides ?? [], [], [], []);

    private static string[] Names(PackageSearchHit[] hits) => [.. hits.Select(hit => hit.Package.Name)];
}
