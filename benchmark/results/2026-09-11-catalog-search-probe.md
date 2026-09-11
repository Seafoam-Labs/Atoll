# 2026-09-11 — in-process catalog search probe (first recorded baseline)

First recorded run of `PackageCatalogServicePerfTests`
(`Atoll.Api.Tests/Ui/PackageCatalogServicePerfTests.cs`) — the steady-state
`PackageCatalogService.SearchAsync` probe behind the catalog page the k6 `ui`
scenario drives (README, [In-process cost probes](../README.md#in-process-cost-probes)).
This is the regression baseline the probe was missing: rerun under the same
conditions and compare before attributing a `ui` regression to the search path.

**Comparability:** measured on the host, not in the Docker stack. Commit
`1043052` (clean tree), .NET SDK 10.0.112 / runtime 10.0.12, serial NUnit
testhost (no parallelizable fixtures in the test project), deterministic
synthetic corpus (seed 12345): 85,000 packages, 1,000 seeded. Host: AMD Ryzen 7
PRO 8840U (16 threads, max ~5.1 GHz), 54 GB RAM, CachyOS, kernel 7.2.4.
Caveats that bound what the numbers mean:

- The documented command runs **Debug, workstation GC** (plain-SDK testhost);
  the API itself is Web SDK, which defaults to server GC. Allocations transfer
  exactly between configurations; latencies are indicative, not
  production-absolute.
- Release vs Debug matters for the scan scenarios (−35–40%); the fast paths and
  the seeded filter are identical in both. Release is the better cross-commit
  baseline.
- Scale is 85k synthetic vs ≈119k live catalog entries (the 2026-09-08 k6
  docs' corpus), ~71%; the full-scan scenarios scale roughly with index size.

## Results

Debug (default `dotnet test`; two back-to-back runs to show noise):

| Scenario | matches | median r1 / r2 | min r1 / r2 | alloc |
| --- | --- | --- | --- | --- |
| default page load (empty q, NameAsc) | 85,000 | 0.01 / 0.01 ms | 0.00 / 0.00 ms | 4 KB/call |
| page navigation (empty q, page 7) | 85,000 | 0.01 / 0.00 ms | 0.00 / 0.00 ms | 4 KB/call |
| sort toggle (empty q, VotesDesc) | 85,000 | 0.00 / 0.00 ms | 0.00 / 0.00 ms | 4 KB/call |
| seeded filter (empty q, Seeded, VotesDesc) | 1,000 | 25.99 / 27.04 ms | 24.10 / 24.88 ms | 4 KB/call |
| broad query (q=lib-, NameAsc) | 10,523 | 20.48 / 19.33 ms | 19.62 / 17.59 ms | 4 KB/call |
| narrow query (single hit, NameAsc) | 1 | 19.92 / 19.61 ms | 18.85 / 18.05 ms | 1 KB/call |

Release (`dotnet test -c Release`):

| Scenario | matches | median | min | alloc |
| --- | --- | --- | --- | --- |
| default page load (empty q, NameAsc) | 85,000 | 0.01 ms | 0.00 ms | 3 KB/call |
| page navigation (empty q, page 7) | 85,000 | 0.00 ms | 0.00 ms | 3 KB/call |
| sort toggle (empty q, VotesDesc) | 85,000 | 0.01 ms | 0.00 ms | 3 KB/call |
| seeded filter (empty q, Seeded, VotesDesc) | 1,000 | 26.15 ms | 23.29 ms | 3 KB/call |
| broad query (q=lib-, NameAsc) | 10,523 | 12.23 ms | 9.96 ms | 3 KB/call |
| narrow query (single hit, NameAsc) | 1 | 12.69 ms | 10.66 ms | 1 KB/call |

## Reading the numbers

- The three empty-query scenarios hit `CollectPage`'s unfiltered slice fast
  path: microseconds and 3–4 KB per call. Pagination and sort toggling are
  effectively free once a sort view exists. Excluded by design (warmup absorbs
  them): the one-time per-sort materialization — an `Array.Sort` over the whole
  index that a first-ever click on a new sort pays — and the 30 s
  seeded/head snapshot refresh.
- The seeded filter is the outlier: ~26 ms in **both** Debug and Release, while
  the query scans drop ~35–40% under Release. The cost is not JIT-sensitive
  arithmetic but 85k probes through `MatchesRowFilters`, each a `Contains` on
  the snapshot's `ImmutableHashSet<string>` (~300 ns/probe: randomized string
  hashing plus hash-trie traversal through interface dispatch). The Debug/Release
  tie is expected for that mix, and marks the number as algorithm-bound: the
  obvious lever, if this path ever matters at live scale, would be a frozen
  collection for the snapshot — not applied here.
- Both query scenarios are full-index scans by construction (`BuildPredicate`
  delegates run per package and total-match counting never short-circuits), so
  their cost scales with index size and predicate cost, not with match count.
- Allocations are the most stable regression signal (deterministic,
  GC-configuration-independent): 3–4 KB/call everywhere, 1 KB for the
  single-hit page (one row instead of 50).

## Regression guidance

- Compare like with like: Debug↔Debug or Release↔Release, same host. Back-to-back
  Debug medians here moved ≤5%, min-to-median spread reached ~20%. Treat deltas
  under ~15–20% as noise; use allocations as the tiebreaker (they should be
  nearly exact).
- The probe covers the in-process leg only: a k6 `ui` p95 change with flat
  probe numbers points at HTTP/serialization/Blazor rendering; both rising
  points at the search path.

## Probe soundness review (same session)

Sound for its stated purpose (log-only steady-state probe, sanity assertions);
the notes below qualify how its output should be read. None block baseline use.
(A mislocated seeded-filter scenario comment and a dead `Stopwatch` in
`RunScenarioAsync` were cleaned up right after this review; neither affects
measurement, and the numbers above predate nothing but that cleanup.)

1. **Steady-state framing is correct.** Warmups absorb snapshot population and
   per-sort view materialization; all 6 scenarios × 25 calls finish well inside
   the 30 s snapshot TTL, so no measured call includes a refresh. Scenario
   order dependencies (page navigation reuses the warm NameAsc view) are
   documented in the test and hold because NUnit runs serially by default.
2. **Measurement APIs check out** (verified against current Microsoft Learn
   docs): `Stopwatch` (QPC-backed) for per-call latency;
   `GC.GetTotalAllocatedBytes(precise: true)` deltas for per-call allocation —
   a process-wide managed-allocation counter (native allocations excluded)
   whose precision cost lands outside the timed region.
3. **Assertions pin behavior, not timing** — totals, rendered rows, page
   counts all matched construction exactly (85,000 / 1,000 / 10,523 / 1;
   broad-query total is the only uncounted one, deliberately). Safe to keep in
   the normal test run.
4. **Testhost ≠ production environment** (finding 1 in "Comparability"):
   workstation GC and Debug by default. Fine for relative tracking; quote
   Release numbers when comparing across commits.
5. **Scale headroom:** 85k synthetic vs ≈119k live catalog (scan scenarios
   ~1.4× costlier at live scale), and 1k seeded vs ≈118.5k live seeded. The
   snapshot build itself (Mongo read + set/dict construction every 30 s) is out
   of scope by design; at live scale it is dominated by the read, which k6's
   `catalog` scenario covers.

## Raw probe output — Release

```text
index=85,000 packages, seeded=1,000, page size=50, iterations=20, .NET 10.0.12
default page load (empty q, NameAsc)         matches= 85000  rows= 50  median=    0.01 ms  min=   0.00 ms  alloc=        3 KB/call
page navigation (empty q, NameAsc, page 7)   matches= 85000  rows= 50  median=    0.00 ms  min=   0.00 ms  alloc=        3 KB/call
sort toggle (empty q, VotesDesc)             matches= 85000  rows= 50  median=    0.01 ms  min=   0.00 ms  alloc=        3 KB/call
seeded filter (empty q, Seeded, VotesDesc)   matches=  1000  rows= 50  median=   26.15 ms  min=  23.29 ms  alloc=        3 KB/call
broad query (q=lib-, NameAsc)                matches= 10523  rows= 50  median=   12.23 ms  min=   9.96 ms  alloc=        3 KB/call
narrow query (single hit, NameAsc)           matches=     1  rows=  1  median=   12.69 ms  min=  10.66 ms  alloc=        1 KB/call
```
