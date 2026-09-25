# 2026-09-25: prose-only stop list (token cleaning)

The 27-term "Indexing noise" block is gone from `TokenCleaning.IgnoredTerms` (`git`, `bin`, `lib`,
`api`, `json`, `http`, `org`, `php`, `svn`, ...). It was written as description noise, but
`PackageIndexBuilder.IndexWords` runs the same cleaner over name tokens, and relevance made the
result query-facing, so those terms were unreachable through the word (P5) and name-token (P3/P4)
tiers on both sides. The stop list now holds prose only.

Outcome in one line: 37.6% of the corpus becomes reachable through the token tiers, the obvious
answer to `neovim git` and `shelly bin` moves to position 1, every pre-existing `ByWords` key keeps
its posting set *and* its enumeration order, and the isolated 10/s gate passes at 14.48 to 83.32 ms
p95 against a 300 ms threshold. The cost is that `git` becomes the broadest term in the corpus
(31,169 postings against `python`'s 11,258): relevance for it goes 0.24 to 10.08 ms in process, and
legacy `by=words&query=git` goes from an immediate `[]` to 50 rows at 16.79 to 17.59 ms in process
and 22.9 ms served.

## Comparability

- **Corpus:** one `packages-meta-ext-v1.json.gz` download (119,975 entries), fed to both variants, so
  the A/B is byte-identical rather than two reads of a moving collection. This deviates from the usual
  "read the live `atoll-mongo-1` collection" recipe deliberately: the diff below compares *enumeration
  order*, which needs one input.
- **Both variants in one process.** `ImmutableHashSet` enumeration order depends on the per-process
  string hash seed, so a two-process order diff would report noise. The baseline index is built by
  loading HEAD's `Atoll.Api.dll` into a separate `AssemblyLoadContext` and calling its
  `PackageIndexBuilder.Parse` on the same JSON.
- **In-process probe:** Release, 16-core host, 3 warmup plus 20 timed iterations, median; run twice per
  variant. Counts are deterministic and identical across the two runs; the millisecond columns vary by
  a few percent, and both samples are quoted where they differ.
- **Served:** isolated benchmark stack (`benchmark/docker-compose.yaml`), API 1 CPU / 2 GB, MongoDB
  1 CPU / 1 GB, security off, `Atoll__Seed__Mode=Off`, refresh disabled, image built from the working
  tree. Startup loaded 119,961 names from the Mongo cache, then the unconditional refresh landed
  **119,976**, one document more than the probe's dump.
- k6 v2.2.0, .NET 10.0.12.

## Index diff: the compatibility proof

Same process, same dump bytes, HEAD assembly against the working tree:

| Measure | HEAD | After |
| --- | --- | --- |
| `ByWords` keys | 95,216 | 95,243 (+27) |
| `ByWords` postings, total | 953,954 | 1,008,809 (+54,855) |
| Name-token vocabulary | 69,169 | 69,195 (+26) |
| Name-token postings | 221,545 | 266,966 (+45,421) |
| Largest posting | `python` 11,258 | **`git` 31,169** |
| `BuildFromPackages` median | 2477.4 / 2456.6 ms | 2527.3 / 2543.0 ms |

The assertions that matter:

- **Removed keys: 0.**
- **Pre-existing keys whose posting enumeration sequence differs: 0.** This is the served `by=words`
  order, which `MatchWords` inherits by copying the first matched posting before intersecting.
- **Pre-existing key enumeration order preserved: true.**
- The 27 added keys are exactly the 27 deleted terms, nothing else: `git` 31,169, `bin` 13,792,
  `api` 2,164, `http` 889, `sql` 690, `json` 619, `org` 590, `html` 574, `php` 557, `net` 521,
  `com` 453, `xml` 433, `https` 428, `etc` 400, `log` 372, `lib` 342, `svn` 205, `url` 187,
  `css` 178, `env` 78, `www` 73, `src` 52, `dir` 51, `var` 18, `cfg` 11, `tmp` 7, `err` 2.

Build cost rises about 2% (50 to 86 ms on a 2.5 s build), consistently in both samples. It lands on
the refresh worker, off the request path.

## Corpus coverage

Names carrying at least one deleted-block name token: **45,084 of 119,975 (37.6%)**.

| Name token | Names carrying it |
| --- | --- |
| `git` | 30,210 |
| `bin` | 13,721 |
| `php` | 204 |
| `svn` | 181 |
| `api` | 147 |
| `net` | 117 |
| `json` | 114 |
| `http` | 109 |

`git` and `bin` are 96.7% of the 45,421 name postings gained; the other 25 terms are rounding.

## Rank cost, in process (live corpus, `Rank` at the 50-row cap)

| Query | Candidates | top-50 median | Catalog unlimited median | Position of the obvious answer |
| --- | --- | --- | --- | --- |
| `git` | 645 to **31,229** | 0.24 to **10.08 ms** | 0.44 to 24.57/25.23 ms | `neovim-git`: absent to 562 |
| `bin` | 255 to **14,015** | 0.10 to 1.93/2.01 ms | 0.15 to 7.66/8.74 ms | `shelly-bin`: absent to 355 |
| `neovim git` | 1,047 to 31,355 | 0.42 to 9.65/9.77 ms | 0.79 to 24.40/26.49 ms | `neovim-git`: **9 to 1** |
| `shelly bin` | 266 to 14,024 | 0.10 to 2.06/2.40 ms | 0.15 to 7.44/7.75 ms | `shelly-bin`: **3 to 1** |
| `firefox git` | 1,187 to 31,717 | 0.38 to 7.84/8.07 ms | 0.90 to 24.85/26.76 ms | `firefox-syncstorage-git`: **149 to 1** |
| `api` | 62 to 2,178 | 0.01/0.03 to 0.21/0.23 ms | 0.01 to 0.62/0.66 ms | |
| `php` | 755 to 898 | 0.06/0.12 to 0.05/0.06 ms | 0.11 to 0.12 ms | |
| `lib` | 4,499 to 4,753 | 0.27/0.35 to 0.26 ms | 1.11 to 1.15/1.18 ms | |
| `python` | 11,349 (unchanged) | 2.34/2.52 to 1.64/1.69 ms | 4.54 to 4.30/4.65 ms | |
| `vim` | 946 (unchanged) | 0.13/0.21 to 0.07/0.08 ms | 0.20/0.22 to 0.15/0.17 ms | |

Two rows are the point of the change. `neovim git` used to give `neovim-git` coverage 1 while
`neovim-gitsigns` got coverage 2, because `git` was a P4 prefix of the token `gitsigns`: two zero-vote
packages outranked the 263-vote exact family. `firefox git` used to lead with
`firefox-extension-refined-github-bin` (0 votes) for the same reason, and now leads with
`firefox-syncstorage-git` (10 votes).

`python` and `vim` are untouched in candidate count; their millisecond columns move inside run-to-run
noise, and the HEAD sample's 10.66 ms unlimited `python` was the outlier (4.54 ms on the repeat).

## Legacy `by=words`: the surface the plan did not measure

`MatchWords` short-circuits to `[]` when any term is missing, so a stop-listed word cost nothing. It
now hydrates and vote-sorts the whole posting before the 50-row take.

| Word | Posting | Rows | Median (in process) | Served median | Top row |
| --- | --- | --- | --- | --- | --- |
| `git` | 0 to **31,169** | 0 to **50** | 0.00 to **16.79 / 17.59 ms** | **22.9 ms** | `aur-auto-vote-git` |
| `bin` | 0 to 13,792 | 0 to 50 | 0.00 to 3.57 / 3.76 ms | 10.3 ms | `visual-studio-code-bin` |
| `api` | 0 to 2,164 | 0 to 50 | 0.00 to 0.51 / 0.53 ms | 2.4 ms | `postman-bin` |
| `python` | 11,258 (unchanged) | 50 | 3.15 / 3.37 to 2.80 / 3.12 ms | 9.2 ms | `pycharm` |
| `vim` | 902 (unchanged) | 50 | 0.25 / 0.27 to 0.21 ms | 1.3 ms | `neovim-git` |
| `the` | 0 (prose, still stopped) | 0 | 0.00 ms | 0.6 ms | none |

This is the accepted `by=words` behavior change: `?by=words&query=git` goes from `[]` to 50 rows.
Membership and ordering for every pre-existing key are unchanged, per the diff above.

## Served uncontended (1-CPU container, 12 sequential requests per query)

| By | Query | Rows | min | median | p95 | max | First |
| --- | --- | --- | --- | --- | --- | --- | --- |
| relevance | `git` | 50 | 14.9 | **17.7** | 44.1 | 62.9 | `git-git` |
| relevance | `bin` | 50 | 6.2 | 6.8 | 8.7 | 10.4 | `bin` |
| relevance | `neovim git` | 50 | 14.4 | 16.7 | 19.6 | 523.0 | **`neovim-git`** |
| relevance | `shelly bin` | 50 | 6.5 | 7.7 | 9.8 | 10.7 | **`shelly-bin`** |
| relevance | `firefox git` | 50 | 14.7 | 16.8 | 18.7 | 28.8 | **`firefox-syncstorage-git`** |
| relevance | `python` | 50 | 5.2 | 6.4 | 9.2 | 10.5 | `python-pyalsaaudio` |
| relevance | `vim` | 50 | 1.3 | 1.4 | 2.0 | 2.5 | `neovim-symlinks` |
| relevance | `lib` | 50 | 1.5 | 1.5 | 3.7 | 4.8 | `libreoffice-extension-languagetool` |
| relevance | `the` | 50 | 1.1 | 1.3 | 1.8 | 2.2 | `thermald-git` (P2 on the raw segment) |
| relevance | `brwose` | 0 | 0.5 | 0.6 | 0.7 | 1.4 | none |
| relevance | `xz` | 24 | 0.8 | 0.9 | 1.0 | 1.4 | `xzoom` |
| relevance | 256-char term | 0 | 0.5 | 0.5 | 0.8 | 2.6 | none |
| words | `git` | 50 | 21.1 | **22.9** | 26.0 | 32.8 | `aur-auto-vote-git` |
| words | `bin` | 50 | 8.6 | 10.3 | 14.5 | 15.3 | `visual-studio-code-bin` |
| words | `api` | 50 | 1.8 | 2.4 | 3.3 | 4.5 | `postman-bin` |
| words | `python` | 50 | 7.4 | 9.2 | 12.5 | 12.9 | `pycharm` |
| words | `vim` | 50 | 1.3 | 1.3 | 2.0 | 2.5 | `neovim-git` |
| words | `the` | 0 | 0.5 | 0.6 | 0.7 | 0.8 | none |
| name | `neovim-git` | 1 | 0.6 | 0.6 | 1.1 | 1.7 | `neovim-git` |
| name | `git` | 0 | 0.5 | 0.6 | 0.6 | 0.6 | none (exact only) |

The previous worst served query was `python` at 5.0 ms median (`2026-09-25-relevance-index.md`).
`git` is now 17.7 ms on relevance and 22.9 ms on words, which makes it the most expensive single term
the corpus can serve. `by=name` and the no-hit paths are unchanged at 0.5 to 0.6 ms.

**Catalog match count for `git`: 645 (13 pages) to 31,229 (625 pages)**, confirmed served on the UI.
The catalog ranks the whole membership without a limit, so it pays the unlimited column above
(24.57 to 26.49 ms in process) per query.

## RPC is untouched

`/rpc/v5/search/git` and `/rpc?v=5&type=search&arg=git` both return aurweb's overflow sentinel
(`"Too many package results."`, past 5,000), and `/rpc/v5/search/neovim-git` returns 6. Identical
before and after: `AurRpcService.Search` scans `All(snapshot)` with `Contains` and never reads
`ByWords` or `RelevanceIndex`. Note this corrects an assumption in the plan that helpers searching
`git` get the `-git` packages; they get the overflow error, exactly as aurweb does.

## In-process perf probe (120k shape-matched synthetic corpus), HEAD against after

| Measure | HEAD | After |
| --- | --- | --- |
| Name-token vocabulary / postings | 113 / 183,390 | 116 / 209,970 |
| `BuildFromPackages` median | 907.0 ms | 914.8 ms |
| `RelevanceIndex.Build` alone | 58.9 ms | 59.5 ms |
| Retained per generation | 44.0 MB | 45.5 MB |
| `collapsed tiers (git)` candidates | 50 | 50 |
| adversarial `l` / `li` / `lib` candidates | 6,864 | 13,170 |
| adversarial `lib` median | 1.64 ms | 5.05 ms |
| unique-query pool candidates/call, median | 19,663, 5.15 ms | 22,404, 6.95 ms |
| adversarial compound 8x6 | 102,927, 191.49 ms | 102,927, 181.50 ms |

Every asserted candidate count held, including `GitCount = 50`: the synthetic `git-NNNN` family earns
P2, P3 and P5 on the same 50 names, so the tier changes and the membership does not. That scenario was
renamed from `stop word (git)` to `collapsed tiers (git)`. The synthetic corpus widens on `lib`
(a filler syllable) rather than `git`, which is the same effect the live corpus shows on `git`.

## k6

`k6 inspect loadtest.js` still lists six scenarios (`search`, `rpc`, `catalog`, `catalog_sorted`,
`ui`, `git_fetch`) and no relevance thresholds, so the default profile and the recorded baseline are
unchanged. The only behavioral script edit is the `RELEVANCE_QUERIES` default gaining `neovim git`;
the rest is comments and the `benchmark/README.md` row that documents the pool.

### Run 1 and 2: isolated (`RELEVANCE_RATE=10`, `GIT_RATE=0`, `VUS=1`, `UI_VUS=1`, `SORT_RATE=1`)

```sh
VUS=1 UI_VUS=1 GIT_RATE=0 SORT_RATE=1 RELEVANCE_RATE=10 RELEVANCE_POOL=<pool> \
  WARMUP=10s HOLD=30s COOLDOWN=5s GIT_DURATION=45s k6 run loadtest.js
```

| Pool | relevance med | relevance p95 | relevance max | Total reqs | Failed | Dropped | API CPU peak | API mem peak |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `default` (now 11 queries, incl. `neovim git`) | 14.87 ms | **83.32 ms** | 370.89 ms | 14,626 (325/s) | 0.00% | 0 | 100.8% | 1339 MiB |
| `adversarial` | 9.63 ms | **14.48 ms** | 122.24 ms | 19,248 (428/s) | 0.00% | 0 | 100.6% | 1267 MiB |

Every threshold passes in both runs (`catalog` 143.83 / 103.75 ms, `catalog_sorted` 73.21 / 22.78 ms,
`rpc` 55.52 / 56.14 ms, `search` 42.59 / 19.50 ms, `ui` 68.32 / 47.00 ms), zero failed requests, zero
dropped iterations. The `default` pool is the slower one because it now contains the two broadest
queries in the corpus (`git`, `neovim git`); the recorded 2026-09-25 run of the same pool was 57.59 ms
median / 63.42 ms p95 over ten queries without them. CPU averages are not quoted: the sampler for
these two runs outlived the 45 s window, so only its peak is meaningful.

### Run 3 and 4: full profile, same day, relevance off then on

```sh
k6 run loadtest.js                                        # off (RELEVANCE_RATE=0 default)
RELEVANCE_RATE=10 RELEVANCE_POOL=adversarial k6 run loadtest.js
# both at VUS=25 UI_VUS=5 GIT_RATE=2 SORT_RATE=10, WARMUP=20s HOLD=1m COOLDOWN=15s
```

| Scenario | Off p95 | On p95 | Threshold | Off | On | Flipped |
| --- | --- | --- | --- | --- | --- | --- |
| catalog_sorted | 509.97 ms | 529.28 ms | 600 ms | **pass** | **pass** | no |
| ui | 827.61 ms | 796.50 ms | 1 s | **pass** | **pass** | no |
| catalog | 708.73 ms | 701.48 ms | 600 ms | fail | fail | no |
| git_fetch | 5.86 s | 5.17 s | 2 s | fail | fail | no |
| rpc | 441.07 ms | 477.12 ms | 300 ms | fail | fail | no |
| search | 400.01 ms | 402.47 ms | 300 ms | fail | fail | no |
| relevance | n/a | 385.72 ms | 300 ms | n/a | fail | n/a |

Totals: off 27,359 requests (287/s), 19 dropped iterations, peak 89 VUs, API CPU avg 96.2% / peak
126.2%, mem peak 1087 MiB. On 27,291 requests (286/s), 13 dropped iterations, peak 94 VUs, API CPU
avg 96.2% / peak 114.0%, mem peak 1227 MiB. `http_req_failed` 0.00% in both, and every functional
check passes at 100.00%.

**The gate criterion passes: enabling relevance flips nothing.** The same four scenarios fail off and
on, as they did in the recorded 2026-09-25 pair, and relevance's own 300 ms threshold fails at
385.72 ms against that run's 404.36 ms. That is queueing on a node that has been over capacity for
every expensive scenario since the 2026-09-19 rebaseline, not rank cost: the same queries served
uncontended on this stack cost 0.5 to 17.7 ms.

Both arms of this pair run the changed image, so the pair measures relevance's marginal cost, not the
stop-list change's. The change's effect on the mixed profile is only visible against the recorded
2026-09-25 pair (`search` 469.90 off / 394.51 on then, 400.01 / 402.47 now), which is a cross-run
comparison and inside that scenario's run-to-run band. `TERMS` already contained `git` and `bin`, so
the `search` scenario was exercising the widened `by=words` path in all four runs.

## Exposure noted, not fixed

A broad name token is now the most expensive thing a single query can ask for, on two surfaces:
relevance ranks 31,229 candidates (10.08 ms capped, 24.57 ms unlimited in the catalog), and legacy
`by=words` hydrates and vote-sorts a 31,169-name posting (16.79 ms) before taking 50. At 23 ms served
on a 1-CPU node, about 43 requests per second for `git` alone saturate the core, and there is no
in-app rate limiting (ADR: gate at the reverse proxy).

This is the same class of exposure `python` already had (11,349 candidates, 4.01 to 5.0 ms served) and
the plan put out of scope: broad-token cost is a property of the corpus, not of token cleaning. The
triggers for result caching or admission control are unchanged and still in `search2.md`; nothing here
meets them, but the term that meets them first is now `git` rather than `python`.

## Verdict

1. The catalog resolves a pasted hyphenated or camelCase name through its parts (previous change) and
   now also resolves the name tokens that make multi-term queries work: `neovim git`, `shelly bin` and
   `firefox git` all put the obvious answer first, pinned by the probe above and by the goldens in
   `PackageSearchRelevanceTests`.
2. No golden changed except the three test files that asserted the stop block directly
   (`TokenCleaningTests`, `RelevanceQueryTests`, `PackageSearchRelevancePerfTests`). No production
   golden moved: `PackageSearchRelevanceTests`, `PackageSearchEngineTests`,
   `PackageSearchServiceTests`, `PackageIndexBuilderTests` and the endpoint tests are untouched and
   pass.
3. The `ByWords` corpus diff shows zero removed keys, zero posting-sequence differences and preserved
   key order; the k6 isolated and full-profile runs pass their criteria and are recorded above; the
   catalog match count for `git` is 645 (13 pages) to 31,229 (625 pages).
4. Test tiers, re-checked totals: fast 1079 passed / 0 skipped, `RequiresGit` 27 passed / 0 skipped,
   `RequiresMongo` 40 passed / 0 skipped.
5. `search.md`'s tier table and multi-term section record the prose-only stop list and the
   coverage-first mechanism behind `neovim git`.
