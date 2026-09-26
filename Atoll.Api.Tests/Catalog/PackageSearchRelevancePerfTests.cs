using System.Diagnostics;
using System.Text.Json;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

/// <summary>
/// In-process cost probe for <see cref="PackageSearchEngine.Rank"/> at the measured corpus scale
/// (119,808 docs; rounded to 120,000 here). Shape-matched: the candidate-set size that drives sort
/// and serialization cost is reproduced from measured live-corpus counts by injecting four
/// deterministic name families per term, then asserted, so a generator edit cannot silently
/// invalidate the comparison.
///
/// Log-only: output goes to <see cref="ITestOutputHelper"/>, which <c>dotnet test</c> hides. Read the
/// numbers by running the MTP executable directly:
/// <c>./Atoll.Api.Tests/bin/Release/net10.0/Atoll.Api.Tests -showLiveOutput -noLogo -class "Atoll.Api.Tests.Catalog.PackageSearchRelevancePerfTests"</c>.
/// Run it that way for the allocation columns too: <see cref="GC.GetTotalAllocatedBytes"/> is
/// process-wide, so classes the runner executes in parallel inflate them.
/// Assertions are shape/sanity checks, not timing gates, so this stays in the normal fast tier.
/// </summary>
public class PackageSearchRelevancePerfTests
{
    private const int PackageCount = 120_000;
    private const int WarmupIterations = 5;
    private const int Iterations = 50;
    private const int PoolIterations = 500;
    private const int BuildIterations = 3;

    /// <summary>The REST response cap; the catalog ranks without one because it pages the membership.</summary>
    private const int ResponseCap = 50;

    // Evidence-table family sizes (live atoll-mongo-1, 119,808 docs, measured 2026-09-24). Each term
    // gets four buckets whose names are built so the three measured counts land exactly on the table:
    //   prefix    "<term>-NNNN"        -> name starts with term              (P2; name-contains; token)
    //   interior  "extra-<term>-NNNN"  -> token == term                      (P3; name-contains; token)
    //   infix     "a<term>NNNN"        -> contains term, no delimited token  (name-contains only)
    //   descOnly  "meta-NNNNNN"        -> description carries the token      (P5; NOT name-contains)
    // So prefix = P; contains = prefix+interior+infix; token = prefix+interior+descOnly.
    //
    // The infix family is kept as deliberate noise: since the infix tier was dropped, those names
    // contain the term but resolve no tier, so they stay in the corpus and out of every candidate
    // count below (AssertInfixDropped pins that). "vim" is also a substring of every "neovim" name,
    // which folds into vim's name-contains count but no longer into its candidates.
    private const int VimPrefix = 650, VimInterior = 100, VimInfix = 12, VimDesc = 151;
    private const int RustPrefix = 213, RustInterior = 100, RustInfix = 32, RustDesc = 2096;
    private const int NeovimPrefix = 283, NeovimInterior = 5, NeovimInfix = 4, NeovimDesc = 116;
    private const int BrowserPrefix = 22, BrowserInterior = 200, BrowserInfix = 43, BrowserDesc = 943;

    private const int VimContains = VimPrefix + VimInterior + VimInfix + NeovimPrefix + NeovimInterior + NeovimInfix;
    private const int RustContains = RustPrefix + RustInterior + RustInfix;
    private const int NeovimContains = NeovimPrefix + NeovimInterior + NeovimInfix;
    private const int BrowserContains = BrowserPrefix + BrowserInterior + BrowserInfix;

    private const int VimCandidates = VimPrefix + VimInterior + VimDesc;
    private const int RustCandidates = RustPrefix + RustInterior + RustDesc;
    private const int NeovimCandidates = NeovimPrefix + NeovimInterior + NeovimDesc;
    private const int BrowserCandidates = BrowserPrefix + BrowserInterior + BrowserDesc;

    private const int QtCount = 50;     // "qt6-NNN" (P2) + "lib-qt6-NNN" (P4); "qt" is sub-minimum -> no posting
    private const int GitCount = 50;    // "git-NNNN": one family earning P2, P3 and P5 on the same 50 names
    private const int TieCount = 5_000; // "tie-NNNNN", equal votes -> exercises the ordinal tiebreak at sort scale

    // Inputs the old full-name scan made uniformly expensive: cost was the corpus size, not the
    // result size. Post-index each has to stay proportional to what it actually matches. The no-hit
    // and tie-heavy cases already run as asserted scenarios above.
    private static readonly string[] AdversarialQueries =
    [
        "a",
        "l",
        "li",
        "lib",
        "xz",
        new string('z', RelevanceQueryParser.MaxQueryLength)
    ];

    // Eight compound segments at 232 characters: the widest legal query, and each segment truncates
    // to MaxPostingsPerTerm, so this walks 8 x 6 vocabulary keys plus 8 name-prefix runs.
    private static readonly string[] CompoundHeads =
        ["python", "kernel", "crypto", "graph", "media", "shell", "text", "data"];

    private static readonly string CompoundAdversarialQuery =
        string.Join(' ', CompoundHeads.Select(head => $"{head}-core-gtk-util-web-tool"));

    private static readonly string[] Syllables =
        ["core", "lib", "gtk", "python", "kernel", "net", "data", "util", "graph", "media", "text", "crypto", "web", "ai", "shell", "tool"];

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private readonly ITestOutputHelper _output;
    private readonly PackageIndexStore _store;
    private readonly SearchIndexData _index;
    private readonly List<AurPackageMetadata> _packages;

    public PackageSearchRelevancePerfTests(ITestOutputHelper output)
    {
        _output = output;
        _packages = BuildCorpus();
        _index = PackageIndexBuilder.BuildFromPackages(_packages);
        _store = new PackageIndexStore();
        _store.Replace(_index);
    }

    [Fact]
    public void Rank_RealisticCorpusShape_ReproducesMeasuredCandidateCounts()
    {
        Assert.Equal(PackageCount, _index.ByNames.Count);
        _output.WriteLine($"index={_index.ByNames.Count:N0} packages, iterations={Iterations}, .NET {Environment.Version}");

        // Shape guard: the evidence terms must reproduce the measured prefix/contains/token counts.
        AssertShape("vim", VimPrefix, VimContains, VimPrefix + VimInterior + VimDesc);
        AssertShape("rust", RustPrefix, RustContains, RustPrefix + RustInterior + RustDesc);
        AssertShape("neovim", NeovimPrefix, NeovimContains, NeovimPrefix + NeovimInterior + NeovimDesc);
        AssertShape("browser", BrowserPrefix, BrowserContains, BrowserPrefix + BrowserInterior + BrowserDesc);
        AssertShape("brwose", 0, 0, 0);
        AssertInfixDropped();

        RunIndexBuildScenario();

        RunScenario("exact name", "yay", 1);
        RunScenario("broad prefix", "vim", VimCandidates);
        RunScenario("name-vs-metadata (rust)", "rust", RustCandidates);
        RunScenario("name-vs-metadata (browser)", "browser", BrowserCandidates);
        RunScenario("interior token (neovim)", "neovim", NeovimCandidates);
        RunScenario("no hit", "brwose", 0);
        RunScenario("short term (qt)", "qt", QtCount * 2);
        RunScenario("collapsed tiers (git)", "git", GitCount);
        RunScenario("tie-heavy", "tie", TieCount);
        RunScenario("max terms, mixed coverage", "vim rust neovim browser qt git yay tie", null);
        RunScenario("max terms, no coverage", "zzzqqq zzzwww zzzrrr zzzttt zzzuuu zzzvvv zzzxxx zzzzzz", 0);

        _output.WriteLine("-- adversarial pool (bounded cost regardless of hit count) --");
        foreach (var query in AdversarialQueries)
            RunScenario(Describe(query), query, null);

        RunCompoundAdversarialScenario();
        RunUniqueQueryScenario();
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

    /// <summary>
    /// The dropped infix tier: names that contain a term without being a prefix or a cleaned token
    /// must not surface as candidates.
    /// </summary>
    private void AssertInfixDropped()
    {
        foreach (var term in new[] { "vim", "rust", "neovim", "browser" })
        {
            var names = PackageSearchEngine.Rank(_index, term).Select(hit => hit.Package.Name).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain($"a{term}0000", names);
        }

        // "neovim-0000" contains "vim" but no longer contributes to its candidate set.
        Assert.DoesNotContain(
            "neovim-0000",
            PackageSearchEngine.Rank(_index, "vim").Select(hit => hit.Package.Name),
            StringComparer.Ordinal);
    }

    private void RunScenario(string label, string query, int? expectedCandidates)
    {
        // Both shapes are served: the catalog ranks the whole membership for paging and totals, REST
        // only ever returns the capped page.
        var full = Measure(query, null, out var hits);
        var capped = Measure(query, ResponseCap, out var cappedHits);
        var rows = cappedHits.Select(hit => hit.Package).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rows, WebOptions).Length;

        _output.WriteLine(
            $"{label,-28} candidates={hits.Length,6}  rows={rows.Length,3}  bytes={bytes,7}  " +
            $"median={full.MedianMs,7:F2}  p95={full.P95Ms,7:F2}  min={full.MinMs,7:F2} ms  " +
            $"alloc={full.AllocatedBytesPerCall / 1024.0,9:F1} KB/call  " +
            $"| top{ResponseCap,3}: median={capped.MedianMs,6:F2}  p95={capped.P95Ms,6:F2} ms  " +
            $"alloc={capped.AllocatedBytesPerCall / 1024.0,8:F1} KB/call");

        if (expectedCandidates is { } expected) Assert.Equal(expected, hits.Length);
    }

    /// <summary>
    /// Build cost lands on the refresh worker, off the request path, but it has to fit the 2 GB /
    /// 1-CPU stack while the outgoing generation is still serving.
    /// </summary>
    private void RunIndexBuildScenario()
    {
        var samples = new double[BuildIterations];
        long allocated = 0;

        for (var i = 0; i < BuildIterations; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            var built = PackageIndexBuilder.BuildFromPackages(_packages);
            stopwatch.Stop();

            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
            allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Assert.Equal(PackageCount, built.Relevance.Count);
        }

        Array.Sort(samples);

        // Retained size of one whole generation, measured against a forced collection so the
        // transient build garbage is not counted.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var retainedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var retained = PackageIndexBuilder.BuildFromPackages(_packages);
        var retainedBytes = GC.GetTotalMemory(forceFullCollection: true) - retainedBefore;
        GC.KeepAlive(retained);

        var relevance = _index.Relevance;
        var structureBytes =
            (long)relevance.PackagesById.Length * 8 +
            (long)relevance.SortedNames.Length * 8 +
            (long)relevance.SortedIds.Length * 4 +
            (long)relevance.SortedNameTokens.Length * 8 +
            (long)relevance.TokenOffsets.Length * 4 +
            (long)relevance.TokenPostings.Length * 4;

        _output.WriteLine(
            $"{"index build",-28} names={relevance.Count,6:N0}  tokens={relevance.SortedNameTokens.Length,6:N0}  " +
            $"postings={relevance.TokenPostings.Length,7:N0}  median={samples[BuildIterations / 2],7:F1} ms  " +
            $"alloc={allocated / 1024.0 / 1024.0,7:F1} MB/build");
        _output.WriteLine(
            $"{"index footprint",-28} relevance arrays={structureBytes / 1024.0 / 1024.0,6:F1} MB " +
            $"(idsByName {relevance.IdsByName.Count:N0} entries on top)  " +
            $"retained generation={retainedBytes / 1024.0 / 1024.0,6:F1} MB  " +
            $"relevance structures alone={MeasureRelevanceBuild(relevance),6:F1} ms");
    }

    /// <summary>
    /// What the relevance structures add on top of the name/provides/word maps the builder already
    /// produced. Tokenization is excluded because the word map pays for it either way.
    /// </summary>
    private static double MeasureRelevanceBuild(RelevanceIndex relevance)
    {
        var packagesById = relevance.PackagesById;
        var nameTokensById = new string[packagesById.Length][];
        var idsByName = new Dictionary<string, int>(packagesById.Length, StringComparer.Ordinal);

        for (var id = 0; id < packagesById.Length; id++)
        {
            nameTokensById[id] =
                [.. TokenCleaning.SplitAndClean(packagesById[id].Name.Split(['-', '_'], StringSplitOptions.None))];
            idsByName[packagesById[id].Name] = id;
        }

        var samples = new double[BuildIterations];
        for (var i = 0; i < BuildIterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var built = RelevanceIndex.Build(packagesById, nameTokensById, idsByName);
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
            GC.KeepAlive(built);
        }

        Array.Sort(samples);
        return samples[BuildIterations / 2];
    }

    /// <summary>
    /// The widest legal query: eight compound segments, each truncated to the per-term posting cap.
    /// The shape is asserted so a syllable edit cannot silently narrow the walk it is measuring.
    /// </summary>
    private void RunCompoundAdversarialScenario()
    {
        var terms = RelevanceQueryParser.Parse(CompoundAdversarialQuery).Terms;

        Assert.Multiple(() =>
        {
            Assert.Equal(RelevanceQueryParser.MaxTerms, terms.Length);
            Assert.All(terms, term => Assert.Equal(RelevanceQueryParser.MaxPostingsPerTerm, term.Postings.Length));
        });

        RunScenario("adversarial compound 8x6", CompoundAdversarialQuery, null);
    }

    /// <summary>
    /// Distinct query per iteration, so nothing here can be repeat-query warmth. Measured on the
    /// served (capped) shape; this is the number a sustained public rate is derived from.
    /// </summary>
    private void RunUniqueQueryScenario()
    {
        var pool = BuildQueryPool(PoolIterations, seed: 98765);
        Assert.Equal(PoolIterations, pool.Length);

        var totalCandidates = 0;
        foreach (var query in pool) totalCandidates += PackageSearchEngine.Rank(_index, query).Length;

        var samples = new double[PoolIterations];
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < PoolIterations; i++)
        {
            var perQuery = Stopwatch.StartNew();
            PackageSearchEngine.Rank(_index, pool[i], ResponseCap);
            perQuery.Stop();
            samples[i] = perQuery.Elapsed.TotalMilliseconds;
        }

        stopwatch.Stop();
        var allocatedPerCall = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)PoolIterations;
        Array.Sort(samples);

        _output.WriteLine(
            $"{"unique-query pool",-28} queries={pool.Length,6}  candidates/call={totalCandidates / (double)pool.Length,6:F0}  " +
            $"median={samples[PoolIterations / 2],7:F2}  p95={samples[P95(PoolIterations)],7:F2}  " +
            $"max={samples[PoolIterations - 1],7:F2} ms  wall={stopwatch.Elapsed.TotalMilliseconds / PoolIterations,6:F2} ms/call  " +
            $"alloc={allocatedPerCall / 1024.0,9:F1} KB/call");
    }

    /// <summary>
    /// Prefixes of real corpus names, length 2 (broad) up to the whole name (exact), plus two-term
    /// refinements: the shape a client typing a name actually sends.
    /// </summary>
    private string[] BuildQueryPool(int size, int seed)
    {
        var rng = new Random(seed);
        var pool = new HashSet<string>(StringComparer.Ordinal);

        while (pool.Count < size)
        {
            var name = _packages[rng.Next(_packages.Count)].Name;

            if (rng.Next(5) == 0)
            {
                var other = _packages[rng.Next(_packages.Count)].Name;
                pool.Add($"{Prefix(name, rng)} {Prefix(other, rng)}");
                continue;
            }

            pool.Add(Prefix(name, rng));
        }

        return [.. pool];
    }

    private static string Prefix(string name, Random rng) => name[..rng.Next(Math.Min(2, name.Length), name.Length + 1)];

    private static string Describe(string query) =>
        query.Length > 8 ? $"adversarial {query.Length}-char term" : $"adversarial '{query}'";

    private void RunSerializationScenario()
    {
        const string label = "serialize 50 rows";
        var rows = PackageSearchEngine.Rank(_index, "vim", ResponseCap).Select(hit => hit.Package).ToArray();
        Assert.Equal(ResponseCap, rows.Length);

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
            $"median={samples[Iterations / 2],7:F2}  p95={samples[P95(Iterations)],7:F2}  min={samples[0],7:F2} ms  " +
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

    /// <summary>
    /// The REST cap is a rank bound, not a post-sort take, so the bounded selection has to agree with
    /// the first N of the unlimited ranking exactly.
    /// </summary>
    [Fact]
    public void Rank_ResponseCap_MatchesTheUnlimitedPrefix()
    {
        foreach (var query in new[] { "vim", "rust", "tie", "l", "vim rust neovim browser qt git yay tie" })
        {
            var unlimited = PackageSearchEngine.Rank(_index, query);
            var limit = Math.Min(ResponseCap, unlimited.Length);

            var expected = unlimited.Take(limit).Select(hit => hit.Package.Name).ToArray();
            var bounded = PackageSearchEngine.Rank(_index, query, ResponseCap).Select(hit => hit.Package.Name).ToArray();

            Assert.Equal(expected, bounded);
        }
    }

    private Measurement Measure(string query, int? limit, out PackageSearchHit[] hits)
    {
        hits = PackageSearchEngine.Rank(_index, query, limit);
        for (var i = 0; i < WarmupIterations; i++) PackageSearchEngine.Rank(_index, query, limit);

        var samples = new double[Iterations];
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < Iterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            PackageSearchEngine.Rank(_index, query, limit);
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var allocatedPerCall = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)Iterations;
        Array.Sort(samples);
        return new Measurement(samples[Iterations / 2], samples[0], samples[P95(Iterations)], allocatedPerCall);
    }

    private static int P95(int iterations) => (int)Math.Ceiling(0.95 * iterations) - 1;

    private static List<AurPackageMetadata> BuildCorpus()
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

        return packages;
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
