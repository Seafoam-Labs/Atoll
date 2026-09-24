using System.Diagnostics;
using System.Text.Json;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

/// <summary>
/// In-process cost probe for <see cref="PackageSearchEngine.Rank"/> at the measured corpus scale
/// (119,808 docs; rounded to 120,000 here). Shape-matched: the candidate-set size that drives sort
/// and serialization cost is reproduced from the search.md Evidence table by injecting four
/// deterministic name families per term, then asserted, so a generator edit cannot silently
/// invalidate the comparison.
///
/// Log-only: output goes to <see cref="ITestOutputHelper"/>, which <c>dotnet test</c> hides. Read the
/// numbers by running the MTP executable directly:
/// <c>./Atoll.Api.Tests/bin/Release/net10.0/Atoll.Api.Tests -showLiveOutput -noLogo -class "Atoll.Api.Tests.Catalog.PackageSearchRelevancePerfTests"</c>.
/// Assertions are shape/sanity checks, not timing gates, so this stays in the normal fast tier.
/// </summary>
public class PackageSearchRelevancePerfTests
{
    private const int PackageCount = 120_000;
    private const int WarmupIterations = 5;
    private const int Iterations = 50;

    // Evidence-table family sizes (live atoll-mongo-1, 119,808 docs, measured 2026-09-24). Each term
    // gets four buckets whose names are built so the three measured counts land exactly on the table:
    //   prefix    "<term>-NNNN"        -> name starts with term              (P2; name-contains; token)
    //   interior  "extra-<term>-NNNN"  -> token == term                      (P3; name-contains; token)
    //   infix     "a<term>NNNN"        -> contains term, no delimited token  (P6; name-contains only)
    //   descOnly  "meta-NNNNNN"        -> description carries the token      (P5; NOT name-contains)
    // So prefix = P; contains = prefix+interior+infix; token = prefix+interior+descOnly; and the
    // relevance candidate set = contains + descOnly (descOnly names are the word-posting hits whose
    // name does not contain the term). "vim" is also a substring of every "neovim" name, so neovim's
    // 292 name-contains rows fold into vim's contains and candidate counts (but not its prefix/token).
    private const int VimPrefix = 650, VimInterior = 100, VimInfix = 12, VimDesc = 151;
    private const int RustPrefix = 213, RustInterior = 100, RustInfix = 32, RustDesc = 2096;
    private const int NeovimPrefix = 283, NeovimInterior = 5, NeovimInfix = 4, NeovimDesc = 116;
    private const int BrowserPrefix = 22, BrowserInterior = 200, BrowserInfix = 43, BrowserDesc = 943;

    private const int VimContains = VimPrefix + VimInterior + VimInfix + NeovimPrefix + NeovimInterior + NeovimInfix;
    private const int RustContains = RustPrefix + RustInterior + RustInfix;
    private const int NeovimContains = NeovimPrefix + NeovimInterior + NeovimInfix;
    private const int BrowserContains = BrowserPrefix + BrowserInterior + BrowserInfix;

    private const int VimCandidates = VimContains + VimDesc;
    private const int RustCandidates = RustContains + RustDesc;
    private const int NeovimCandidates = NeovimContains + NeovimDesc;
    private const int BrowserCandidates = BrowserContains + BrowserDesc;

    private const int QtCount = 50;     // "qt6-NNN" (P2) + "lib-qt6-NNN" (P4); "qt" is sub-minimum -> no posting
    private const int GitCount = 50;    // "git-NNNN" (P2 only); "git" is a stop word -> no token, no P5/P6
    private const int TieCount = 5_000; // "tie-NNNNN", equal votes -> exercises the ordinal tiebreak at sort scale

    private static readonly string[] Syllables =
        ["core", "lib", "gtk", "python", "kernel", "net", "data", "util", "graph", "media", "text", "crypto", "web", "ai", "shell", "tool"];

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private readonly ITestOutputHelper _output;
    private readonly PackageIndexStore _store;
    private readonly SearchIndexData _index;

    public PackageSearchRelevancePerfTests(ITestOutputHelper output)
    {
        _output = output;
        _index = BuildCorpus();
        _store = new PackageIndexStore();
        _store.Replace(_index);
    }

    [Fact]
    public void RelevanceRankCostAtRealisticIndexScale()
    {
        Assert.Equal(PackageCount, _index.ByNames.Count);
        _output.WriteLine($"index={_index.ByNames.Count:N0} packages, iterations={Iterations}, .NET {Environment.Version}");

        // Shape guard: the evidence terms must reproduce the measured prefix/contains/token counts.
        AssertShape("vim", VimPrefix, VimContains, VimPrefix + VimInterior + VimDesc);
        AssertShape("rust", RustPrefix, RustContains, RustPrefix + RustInterior + RustDesc);
        AssertShape("neovim", NeovimPrefix, NeovimContains, NeovimPrefix + NeovimInterior + NeovimDesc);
        AssertShape("browser", BrowserPrefix, BrowserContains, BrowserPrefix + BrowserInterior + BrowserDesc);
        AssertShape("brwose", 0, 0, 0);

        RunScenario("exact name", "yay", 1);
        RunScenario("broad prefix", "vim", VimCandidates);
        RunScenario("name-vs-metadata (rust)", "rust", RustCandidates);
        RunScenario("name-vs-metadata (browser)", "browser", BrowserCandidates);
        RunScenario("interior token (neovim)", "neovim", NeovimCandidates);
        RunScenario("no hit", "brwose", 0);
        RunScenario("short term (qt)", "qt", QtCount * 2);
        RunScenario("stop word (git)", "git", GitCount);
        RunScenario("tie-heavy", "tie", TieCount);
        RunScenario("max terms, mixed coverage", "vim rust neovim browser qt git yay tie", null);
        RunScenario("max terms, no coverage", "zzzqqq zzzwww zzzrrr zzzttt zzzuuu zzzvvv zzzxxx zzzzzz", 0);

        RunSerializationScenario();
        RunGenerationSwapScenario();
    }

    private void AssertShape(string term, int expectedPrefix, int expectedContains, int expectedToken)
    {
        var prefix = _index.ByNames.Keys.Count(name => name.StartsWith(term, StringComparison.OrdinalIgnoreCase));
        var contains = _index.ByNames.Keys.Count(name => name.Contains(term, StringComparison.OrdinalIgnoreCase));
        var token = _index.ByWords.GetValueOrDefault(term)?.Count ?? 0;

        _output.WriteLine($"shape {term,-8} prefix={prefix,5}  contains={contains,5}  token={token,5}");
        Assert.Multiple(() =>
        {
            Assert.Equal(expectedPrefix, prefix);
            Assert.Equal(expectedContains, contains);
            Assert.Equal(expectedToken, token);
        });
    }

    private void RunScenario(string label, string query, int? expectedCandidates)
    {
        var measurement = Measure(query, out var hits);
        var rows = hits.Take(50).Select(hit => hit.Package).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rows, WebOptions).Length;

        _output.WriteLine(
            $"{label,-28} candidates={hits.Length,6}  rows={rows.Length,3}  bytes={bytes,7}  " +
            $"median={measurement.MedianMs,7:F2}  p95={measurement.P95Ms,7:F2}  min={measurement.MinMs,7:F2} ms  " +
            $"alloc={measurement.AllocatedBytesPerCall / 1024.0,9:F1} KB/call");

        if (expectedCandidates is { } expected) Assert.Equal(expected, hits.Length);
    }

    private void RunSerializationScenario()
    {
        const string label = "serialize 50 rows";
        var rows = PackageSearchEngine.Rank(_index, "vim").Take(50).Select(hit => hit.Package).ToArray();
        Assert.Equal(50, rows.Length);

        for (var i = 0; i < WarmupIterations; i++) JsonSerializer.SerializeToUtf8Bytes(rows, WebOptions);

        var samples = new double[Iterations];
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < Iterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            JsonSerializer.SerializeToUtf8Bytes(rows, WebOptions);
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var allocatedPerCall = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)Iterations;
        Array.Sort(samples);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rows, WebOptions).Length;

        _output.WriteLine(
            $"{label,-28} candidates={rows.Length,6}  rows={rows.Length,3}  bytes={bytes,7}  " +
            $"median={samples[Iterations / 2],7:F2}  p95={samples[P95Index],7:F2}  min={samples[0],7:F2} ms  " +
            $"alloc={allocatedPerCall / 1024.0,9:F1} KB/call");
    }

    private void RunGenerationSwapScenario()
    {
        var engine = new PackageSearchEngine(_store);
        var captured = engine.Capture();

        // A new generation lands mid-flight; ranking the captured snapshot must still answer the old
        // generation. Capture-once is what makes a request immune to a concurrent index rebuild.
        _store.Replace(PackageIndexBuilder.BuildFromPackages([Pkg("vim", 1), Pkg("brand-new", 2)]));

        var onCaptured = PackageSearchEngine.Rank(captured, "vim");
        var onCurrent = PackageSearchEngine.Rank(_store.Current, "vim");

        _output.WriteLine($"{"generation swap",-28} captured candidates={onCaptured.Length,6}  current candidates={onCurrent.Length,6}");
        Assert.Multiple(() =>
        {
            Assert.Equal(VimCandidates, onCaptured.Length);
            Assert.Equal(1, onCurrent.Length);
            Assert.Equal("vim", onCurrent[0].Package.Name);
        });
    }

    private Measurement Measure(string query, out PackageSearchHit[] hits)
    {
        hits = PackageSearchEngine.Rank(_index, query);
        for (var i = 0; i < WarmupIterations; i++) PackageSearchEngine.Rank(_index, query);

        var samples = new double[Iterations];
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < Iterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            PackageSearchEngine.Rank(_index, query);
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var allocatedPerCall = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)Iterations;
        Array.Sort(samples);
        return new Measurement(samples[Iterations / 2], samples[0], samples[P95Index], allocatedPerCall);
    }

    private static int P95Index => (int)Math.Ceiling(0.95 * Iterations) - 1;

    private static SearchIndexData BuildCorpus()
    {
        var rng = new Random(12345);
        var packages = new List<AurPackageMetadata>(PackageCount);
        var meta = 0;

        AddTermFamily(packages, ref meta, rng, "vim", VimPrefix, VimInterior, VimInfix, VimDesc);
        AddTermFamily(packages, ref meta, rng, "rust", RustPrefix, RustInterior, RustInfix, RustDesc);
        AddTermFamily(packages, ref meta, rng, "neovim", NeovimPrefix, NeovimInterior, NeovimInfix, NeovimDesc);
        AddTermFamily(packages, ref meta, rng, "browser", BrowserPrefix, BrowserInterior, BrowserInfix, BrowserDesc);

        packages.Add(Pkg("yay", rng.Next(0, 2_000)));

        for (var i = 0; i < QtCount; i++) packages.Add(Pkg($"qt6-{i:d3}", rng.Next(0, 2_000)));
        for (var i = 0; i < QtCount; i++) packages.Add(Pkg($"lib-qt6-{i:d3}", rng.Next(0, 2_000)));
        for (var i = 0; i < GitCount; i++) packages.Add(Pkg($"git-{i:d4}", rng.Next(0, 2_000)));
        for (var i = 0; i < TieCount; i++) packages.Add(Pkg($"tie-{i:d5}", 0));

        // Filler carries an empty description and syllable-only names, so it contributes no tracked
        // term to any name-contains or word-posting count above.
        for (var i = packages.Count; i < PackageCount; i++)
            packages.Add(Pkg($"{Syllables[rng.Next(Syllables.Length)]}-{Syllables[rng.Next(Syllables.Length)]}-{i:d5}", rng.Next(0, 2_000)));

        return PackageIndexBuilder.BuildFromPackages(packages);
    }

    private static void AddTermFamily(
        List<AurPackageMetadata> packages, ref int meta, Random rng,
        string term, int prefix, int interior, int infix, int descOnly)
    {
        for (var i = 0; i < prefix; i++) packages.Add(Pkg($"{term}-{i:d4}", rng.Next(0, 2_000)));
        for (var i = 0; i < interior; i++) packages.Add(Pkg($"extra-{term}-{i:d4}", rng.Next(0, 2_000)));
        for (var i = 0; i < infix; i++) packages.Add(Pkg($"a{term}{i:d4}", rng.Next(0, 2_000)));
        for (var i = 0; i < descOnly; i++)
            packages.Add(Pkg($"meta-{meta++:d6}", rng.Next(0, 2_000), $"a {term} utility"));
    }

    private static AurPackageMetadata Pkg(string name, long votes, string description = "") =>
        new(0, name, 0, name, "1.0-1", description, null, votes, 0, null, null, null, 0, 0, "",
            [], [], [], [], [], [], [], []);

    private sealed record Measurement(double MedianMs, double MinMs, double P95Ms, double AllocatedBytesPerCall);
}
