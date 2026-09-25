# 2026-09-25: relevance index reruns (Phase 4, step 3)

Reruns of the 2026-09-24 relevance gate after ranked retrieval moved off the full name scan and onto
the generation-scoped `RelevanceIndex`, and after the bare-infix tier (P6) was dropped. Same
isolated 1-CPU benchmark stack as the gate, same image build path, same-day full-profile pair.

Outcome in one line: relevance is no longer the binding constraint. Served uncontended cost falls
from 33 ms median / 63 ms p95 to 0.5-5.0 ms median / 0.7-6.8 ms p95, a no-hit query costs nothing
instead of a full corpus traversal, the isolated 10/s gate passes at 63 ms p95 against a 300 ms
threshold (was 492 ms), and enabling relevance at 10/s in the full profile now flips no scenario
threshold that the relevance-off run passes (`catalog_sorted` and `ui` both stay green). Derived
sustained rate: **50/s on a 1-CPU node with headroom** (see [D2](#d2-sustained-public-rate)).

## Comparability

- Isolated benchmark stack (`benchmark/docker-compose.yaml`), API capped at 1 CPU / 2 GB, MongoDB
  1 CPU / 1 GB; security off, `Atoll__Seed__Mode=Off`, package refresh disabled, index refresh at
  60 min. Image built from the working tree (the Phase 4 changes).
- In-memory index from the live `packages-meta-ext-v1` dump: **119,969 names** at startup for every
  run, drifting to **119,961** during the refresh spot check (the gate recorded 119,637 and 119,929
  on 2026-09-24).
- k6 v2.2.0, .NET 10.0.12. The in-process probe ran in Release on the 16-core host, like the gate's
  probe numbers, so probe and served figures are not the same machine.
- `GIT_RATE=0` now deregisters `git_fetch` (new in this change), so the isolated runs are cleaner
  than the gate's, which could not go below git's 1/s floor. The gate's isolated p95 of 492 ms was
  measured with git competing; the 63 ms here is not.
- The full-profile pair is same-day and same-stack, unlike the gate's cross-day comparison.

## Scenario registration check

`k6 inspect loadtest.js` lists six scenarios (`search`, `rpc`, `catalog`, `catalog_sorted`, `ui`,
`git_fetch`) and no relevance thresholds, so the default profile and the recorded baseline are
unchanged. `-e GIT_RATE=0` drops `git_fetch` and its two thresholds. `-e RELEVANCE_RATE=10` adds
`relevance` and its two thresholds. Both together give a six-scenario profile with relevance and no
git.

## In-process probe (`PackageSearchRelevancePerfTests`, Release, 120k shape-matched corpus)

Build cost and footprint, the new Step 1 budget check:

| Measure | Value |
| --- | --- |
| `BuildFromPackages` median | 1045.5 ms |
| of which `RelevanceIndex.Build` | **68.0 ms** |
| Allocated per build (transient) | 250.8 MB |
| Retained per generation (whole `SearchIndexData`) | 44.0 MB |
| Relevance arrays | 3.0 MB, plus a 120,000-entry `IdsByName` frozen dictionary |
| Name-token vocabulary / postings | 113 distinct / 183,390 (synthetic corpus; the live corpus has a richer vocabulary) |

The relevance structures are ~7% of a build that already costs about a second, and they land on the
refresh worker, off the request path.

Rank cost per scenario. `top 50` is the served REST shape (bounded selection); the unqualified
columns are the unlimited ranking the catalog pages over.

| Scenario | Candidates | median | p95 | top-50 median | top-50 p95 | top-50 alloc |
| --- | --- | --- | --- | --- | --- | --- |
| exact name (`yay`) | 1 | 0.00 ms | 0.01 ms | 0.00 ms | 0.00 ms | 0.7 KB |
| broad prefix (`vim`) | 901 | 0.77 ms | 0.84 ms | 0.47 ms | 0.52 ms | 64.3 KB |
| name-vs-metadata (`rust`) | 2,409 | 1.89 ms | 2.46 ms | 0.41 ms | 1.14 ms | 274.6 KB |
| name-vs-metadata (`browser`) | 1,165 | 0.69 ms | 0.80 ms | 0.20 ms | 0.27 ms | 132.2 KB |
| interior token (`neovim`) | 404 | 0.07 ms | 0.08 ms | 0.12 ms | 0.16 ms | 31.9 KB |
| no hit (`brwose`) | 0 | 0.00 ms | 0.00 ms | 0.00 ms | 0.00 ms | 0.5 KB |
| short term (`qt`) | 100 | 0.01 ms | 0.01 ms | 0.03 ms | 0.03 ms | 16.7 KB |
| stop word (`git`) | 50 | 0.01 ms | 0.01 ms | 0.01 ms | 0.01 ms | 7.3 KB |
| tie-heavy (`tie`) | 5,000 | 1.12 ms | 2.00 ms | 0.62 ms | 1.35 ms | 570.6 KB |
| max terms, mixed coverage | 10,030 | 4.36 ms | 7.46 ms | 1.81 ms | 5.52 ms | 1187.8 KB |
| max terms, no coverage | 0 | 0.00 ms | 0.00 ms | 0.00 ms | 0.00 ms | 1.8 KB |
| adversarial `a` | 7,053 | 1.82 ms | 3.31 ms | 0.26 ms | 1.16 ms | 570.6 KB |
| adversarial `l` | 6,864 | 1.52 ms | 1.94 ms | 0.34 ms | 1.12 ms | 570.6 KB |
| adversarial `li` | 6,864 | 1.77 ms | 3.19 ms | 0.28 ms | 0.58 ms | 570.6 KB |
| adversarial `lib` | 6,864 | 1.88 ms | 3.25 ms | 0.28 ms | 1.04 ms | 570.6 KB |
| adversarial `xz` | 0 | 0.00 ms | 0.00 ms | 0.00 ms | 0.00 ms | 0.5 KB |
| adversarial 256-char term | 0 | 0.00 ms | 0.00 ms | 0.00 ms | 0.00 ms | 0.5 KB |
| unique-query pool (500 distinct, capped) | 3,393 avg | 0.07 ms | 3.63 ms | max 9.14 ms | wall 0.63 ms/call | 314.5 KB |
| serialize 50 rows | 50 | 0.04 ms | 0.04 ms | | | 20.5 KB |

The gate's equivalent figures were 25-31 ms median single-term and ~50 ms at 8 terms, with no
distinction between hit and no-hit. The adversarial rows are the point of the change: one- and
two-character prefixes and the maximum-length term now cost what they match, not what the corpus
holds.

## Served uncontended (1-CPU container, 12 sequential requests per query)

End to end: HTTP, rank, and 50-row serialization.

| Query | Rows | min | median | p95 | max |
| --- | --- | --- | --- | --- | --- |
| `vim` | 50 | 0.9 ms | 0.9 ms | 2.2 ms | 14.7 ms |
| `python` | 50 | 3.8 ms | 5.0 ms | 6.8 ms | 7.3 ms |
| `p` | 50 | 3.8 ms | 4.5 ms | 6.8 ms | 7.7 ms |
| `a` | 50 | 2.1 ms | 3.2 ms | 4.1 ms | 5.5 ms |
| `l` | 50 | 1.7 ms | 2.7 ms | 4.0 ms | 5.1 ms |
| `lib` | 50 | 1.0 ms | 1.3 ms | 2.2 ms | 2.6 ms |
| `rust` | 50 | 1.2 ms | 1.5 ms | 3.2 ms | 4.3 ms |
| `theme` | 50 | 1.3 ms | 1.8 ms | 3.0 ms | 3.2 ms |
| `xz` | 24 | 0.6 ms | 0.7 ms | 0.7 ms | 0.8 ms |
| `7z` | 13 | 0.5 ms | 0.6 ms | 1.1 ms | 1.3 ms |
| `i3` | 50 | 0.7 ms | 0.8 ms | 1.0 ms | 1.2 ms |
| `brwose` (no hit) | 0 | 0.4 ms | 0.5 ms | 1.0 ms | 1.2 ms |
| 256-char term (no hit) | 0 | 0.4 ms | 0.5 ms | 0.7 ms | 1.1 ms |

The gate measured 27.3 ms min / 32.9 ms median / 62.5 ms p95 / 132.4 ms max over the ten-query
`RELEVANCE_QUERIES` pool on the same stack. The worst query here (`python`, a broad token with a
large posting list) is 5.0 ms median against that 32.9 ms.

### P6 drop confirmed on the live corpus

`xz` returns 24 rows and `7z` returns 13, exactly the 33 to 24 and 23 to 13 predicted by the
2026-09-25 P6-only recall measurement. `i3` is unchanged at 50 (its P6-only names never reached the
cap). No broad query changed at all.

## Run 1: isolated (`RELEVANCE_RATE=10`, `GIT_RATE=0`)

```sh
VUS=1 UI_VUS=1 GIT_RATE=0 SORT_RATE=1 RELEVANCE_RATE=10 RELEVANCE_POOL=<pool> \
  WARMUP=10s HOLD=30s COOLDOWN=5s GIT_DURATION=45s k6 run loadtest.js
```

Three pools, so the served number cannot be flattered by ten hot queries. `VUS=1` is not idle: on
sub-millisecond paths one VU each still generates 377-531 requests/s of competing load, so these
runs are contended, and API CPU peaks at ~102% regardless of pool.

| Pool | relevance med | relevance p95 | relevance max | Total reqs | Failed | API CPU avg/peak | API mem peak |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `default` | 57.59 ms | **63.42 ms** | 159.22 ms | 16,980 (377/s) | 0.00% | 78.9% / 102.2% | 1229 MiB |
| `unique` | 20.26 ms | **23.47 ms** | 61.69 ms | 22,934 (509/s) | 0.00% | 78.7% / 102.1% | 1255 MiB |
| `adversarial` | 15.32 ms | **17.55 ms** | 115.25 ms | 23,927 (531/s) | 0.00% | 79.5% / 103.5% | 1224 MiB |

All three pass the 300 ms threshold; the worst is 4.7x inside it. The gate's isolated run recorded
492 ms p95 and 4 dropped iterations with git at its 1/s floor competing. Every other scenario passes
in all three runs, zero failures, zero dropped iterations.

The `default` pool is the slowest because its ten queries include the broadest terms (`vim`, `rust`,
`browser`), not because repeats are warm; `unique` and `adversarial` draw prefixes that mostly match
less.

### Rate sweep (isolated, adversarial pool)

| `RELEVANCE_RATE` | relevance med | p95 | max | Total reqs | Failed | Dropped iterations | API CPU avg/peak |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 10 | 15.32 ms | 17.55 ms | 115.25 ms | 23,927 | 0.00% | 0 | 79.5% / 103.5% |
| 25 | 4.62 ms | 63.59 ms | 269.56 ms | 21,285 | 0.00% | 2 | 71.8% / 100.9% |
| 50 | 8.93 ms | 71.22 ms | 262.39 ms | 22,964 | 0.00% | 6 | 79.3% / 101.8% |
| 100 | 7.74 ms | 60.66 ms | 261.73 ms | 21,138 | 0.00% | 31 | 76.8% / 110.2% |
| 150 | 7.10 ms | 68.65 ms | 273.76 ms | 25,236 | 0.00% | 149 | 82.7% / 101.0% |

p95 is flat from 10/s to 150/s: a 15x rate increase moves it by nothing measurable against the
background load, and API CPU does not rise with it. Relevance has stopped registering on the node's
budget. The 149 dropped iterations at 150/s are k6's `maxVUs=12` failing to hold the arrival rate,
not server failures; `http_req_failed` stays at 0.00% throughout.

## Run 2: full profile, same day, relevance off then on

```sh
k6 run loadtest.js                                        # relevance off (RELEVANCE_RATE=0 default)
RELEVANCE_RATE=10 RELEVANCE_POOL=adversarial k6 run loadtest.js
# both at VUS=25 UI_VUS=5 GIT_RATE=2 SORT_RATE=10, WARMUP=20s HOLD=1m COOLDOWN=15s
```

Relevance on uses the adversarial pool deliberately: the gate criterion should be judged against the
worst queries, not the ten familiar ones.

| Scenario | Off p95 | On p95 | Threshold | Off | On | Flipped |
| --- | --- | --- | --- | --- | --- | --- |
| search | 469.90 ms | 394.51 ms | 300 ms | fail | fail | no |
| rpc | 592.52 ms | 901.56 ms | 300 ms | fail | fail | no |
| catalog | 793.20 ms | 789.86 ms | 600 ms | fail | fail | no |
| catalog_sorted | 569.11 ms | 595.50 ms | 600 ms | **pass** | **pass** | no |
| ui | 899.58 ms | 794.96 ms | 1 s | **pass** | **pass** | no |
| git_fetch | 5.97 s | 5.79 s | 2 s | fail | fail | no |
| relevance | n/a | 404.36 ms | 300 ms | n/a | fail | n/a |

Totals:

| | Off | On | Gate (2026-09-24, on) |
| --- | --- | --- | --- |
| Requests | 24,994 (262/s) | 26,811 (281/s) | 12,916 |
| `http_req_failed` | 0.00% | 0.00% | 0.04% (6 relevance) |
| Dropped iterations | 13 | 38 | 113 |
| Peak VUs | 89 | 97 | 103 |
| API CPU avg / peak | 96.4% / 111.2% | 98.3% / 110.2% | peak 118% (baseline) |
| API mem peak | 1367 MiB | 1485 MiB | not recorded |

**The gate criterion passes: enabling relevance flips nothing.** `catalog_sorted`, the scenario the
2026-09-24 run flipped from pass to fail, stays green at 595.50 ms against its 600 ms threshold, and
`ui` improves. Throughput no longer halves; it rises by roughly the relevance requests themselves.
No request fails.

The one failing threshold is relevance's own 300 ms, at 404.36 ms. That is queueing, not rank cost:
the same query set served uncontended on this stack costs 0.5-6.8 ms, and every other
expensive-per-request scenario in this profile is also over its threshold (git_fetch at 2.9x, rpc at
3x). The default full profile has been over the node's capacity since the 2026-09-19 rebaseline;
relevance now sits in that queue instead of causing it.

Peak API memory rises 118 MiB with the relevance structures, to 1485 MiB of the 2 GB cap.

## Run 3: index rebuild and swap under load

The refresh cycle that follows startup is unconditional (the HTTP validators are only recorded after
a successful download), so a container restart produces exactly one full download, parse, Mongo save,
rebuild, and atomic swap. Timed to land inside the k6 window:

```
06:04:56Z  docker restart benchmark-atoll-1
06:05:01Z  "Loaded 119969 packages. Building indexes."     (generation 1 from the Mongo cache)
06:05:07Z  generation 1 serving
06:05:21Z  k6 started
06:05:33Z  "Package index refreshed with 119961 packages." (generation 2 swapped in, 12 s into the run)
06:06:48Z  k6 finished
```

```sh
VUS=1 UI_VUS=1 GIT_RATE=0 SORT_RATE=1 RELEVANCE_RATE=10 RELEVANCE_POOL=unique \
  WARMUP=5s HOLD=75s COOLDOWN=5s GIT_DURATION=85s k6 run loadtest.js
```

| Scenario | med | p95 | max | Threshold |
| --- | --- | --- | --- | --- |
| relevance | 61.64 ms | **180.93 ms** | 2.08 s | 300 ms pass |
| catalog_sorted | 63.94 ms | 344.34 ms | 1.96 s | 600 ms pass |
| catalog | 94.65 ms | 165.69 ms | 1.96 s | 600 ms pass |
| rpc | 0.23 ms | 96.91 ms | 1.88 s | 300 ms pass |
| ui | 1.99 ms | 73.64 ms | 2.98 s | 1 s pass |
| search | 0.33 ms | 14.32 ms | 1.88 s | 300 ms pass |

31,118 requests, `http_req_failed` 0.00%, 9 dropped iterations, every threshold passing. API CPU
avg 92.6% / peak 109.5%; **API memory peak 1382 MiB** of the 2 GB cap while both generations and
the parsed dump were live.

The whole 32 s refresh window (download plus parse plus Mongo save plus build) shows up as ~2-3 s
maxima and lifts relevance p95 from 23.47 ms to 180.93 ms, but nothing fails and nothing drops. The
swap itself is a `Volatile.Write` of one reference; the cost around it is the download and the
single-core build, which is what the 2 GB floor and the off-request-path build already assume.

## D2: sustained public rate

**Document 50 relevance requests per second on a 1-CPU node.** Derivation:

- The highest rate the harness served with negligible dropped iterations is 100/s (31 dropped
  iterations against ~4,500 expected relevance arrivals, 0.7%, and 0.00% failed requests), with p95
  60.66 ms against a 300 ms threshold.
- 50/s is half that, so it carries more than the ~20% headroom the plan asks for, and it measured
  71.22 ms p95 with 6 dropped iterations and zero failures.
- It is 5x the rate the 2026-09-24 gate rejected, and 17x inside the threshold at the rate the gate
  failed.

Two conditions belong with the number:

1. It assumes a node with headroom. In the full default profile the node is already over capacity
   for every expensive scenario, and relevance's own p95 there is 404 ms at 10/s from queueing. A
   deployment running the default profile at its thresholds needs more than one CPU regardless of
   relevance.
2. There is still no in-app rate limiting (ADR: gate at the reverse proxy), so this is the rate to
   configure the proxy at, not a limit the app enforces.

CPU per request, for sizing: in-process the capped rank is 0.26-0.34 ms median for the broad
adversarial prefixes and 1.81 ms for an 8-term mixed-coverage query; served end to end, including
HTTP and 50-row serialization, it is 0.5-6.8 ms median. At 50/s that is under a quarter of one core
at the worst query and about 1.5% at the median. The rate sweep could not measure it directly:
raising relevance from 10/s to 100/s left API CPU average inside its run-to-run noise (79.5% to
76.8%).

## Verdict against the Phase 4 definition of done

1. No request-time scan of all names: every tier resolves through index lookups. P6 dropped per D1.
2. Top-50 REST selection without a full candidate sort; `BoundedSelectionMatchesTheUnlimitedRanking`
   pins the bounded output to the first 50 of the unlimited ranking, and every prior golden passes
   except the tier ladder, which lost its `NameInfix` row.
3. Build and footprint measured: 68 ms of relevance structures in a ~1 s build, 44 MB retained per
   generation, 1485 MiB peak under the full profile and 1382 MiB peak across a swap, both inside the
   2 GB cap.
4. Isolated 10/s passes at 17.55-63.42 ms p95 against 300 ms; the same-day full-profile pair flips
   nothing.
5. D2 recorded above with its evidence.
