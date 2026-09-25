# 2026-09-25: isolated-stack baseline (relevance search default)

Current numbers for the isolated benchmark stack at `6e8d710`, where `?by=relevance` is the
catalog's default query mode, ranked retrieval resolves through the generation-scoped
`RelevanceIndex`, and the stop list holds prose only. This is the file to compare a new run
against; it replaces three change-scoped docs (the 2026-09-24 relevance gate and the 2026-09-25
relevance-index and token-cleaning reruns), whose narrative now lives in the commit messages of
`31631f6..6e8d710`.

**Scope:** these runs measure the *isolated* corpus (README, [Corpora](../README.md#corpora)):
MongoDB holds the full `aur-metadata` dump but only the six-name `PACKAGES` pool is seeded, since
`Atoll__Seed__Mode=Off`. `catalog`, `catalog_sorted` and `git_fetch` therefore price contention and
the request contract over a six-package catalog, not sort or clone cost at production scale; the
production-shaped reference for those stays
[`2026-09-19-docker-local-2g-rebaseline.md`](2026-09-19-docker-local-2g-rebaseline.md). Never
compare across the two.

## Comparability

- Stack: `benchmark/docker-compose.yaml`, API 1 CPU / 2 GB, MongoDB 1 CPU / 1 GB; security off,
  seeding off, package refresh disabled. Image built from the working tree at `6e8d710`.
- Corpus: in-memory index **119,976 names** (119,961 from the Mongo cache at startup, then the
  unconditional refresh landed 119,976). The dump drifts daily, so candidate counts move with it.
- k6 v2.2.0, .NET 10.0.12. In-process probes ran in Release on the 16-core host; probe and served
  figures are different machines.
- `GIT_RATE=0` deregisters `git_fetch`, so the isolated runs below have no git floor competing.
- Two images contribute: the current `6e8d710`, and `29f93c8` one commit earlier (before the
  stop-list change), which supplies the second samples, the rate ladder and the refresh-swap run.
  Every earlier-image number is marked where it is quoted.

## Default profile, relevance off (`k6 run loadtest.js`)

`VUS=25 UI_VUS=5 GIT_RATE=2 SORT_RATE=10`, `WARMUP=20s HOLD=1m COOLDOWN=15s`.

| Scenario | p95 | Threshold | Verdict | Second sample |
| --- | --- | --- | --- | --- |
| search | 400.01 ms | 300 ms | fail | 469.90 ms |
| rpc | 441.07 ms | 300 ms | fail | 592.52 ms |
| catalog | 708.73 ms | 600 ms | fail | 793.20 ms |
| catalog_sorted | 509.97 ms | 600 ms | **pass** | 569.11 ms |
| ui | 827.61 ms | 1 s | **pass** | 899.58 ms |
| git_fetch | 5.86 s | 2 s | fail | 5.97 s |

27,359 requests (287/s), `http_req_failed` 0.00%, checks 100.00%, 19 dropped iterations, peak 89
VUs, API CPU avg 96.2% / peak 126.2%, API memory peak 1087 MiB. The second sample is the same
profile one commit earlier: 24,994 requests (262/s), 13 dropped, peak 89 VUs, CPU 96.4% / 111.2%,
memory peak 1367 MiB. Read the four failing scenarios as the node's standing saturation, not as a
regression signal: they fail in every recorded full-profile run of this stack, and `catalog_sorted`
straddles its 600 ms threshold run to run (510-569 ms with relevance off, 529-596 ms on).

## Same profile, relevance on at 10/s (adversarial pool)

```sh
RELEVANCE_RATE=10 RELEVANCE_POOL=adversarial k6 run loadtest.js
```

| Scenario | p95 | Threshold | Verdict | Flipped vs off |
| --- | --- | --- | --- | --- |
| search | 402.47 ms | 300 ms | fail | no |
| rpc | 477.12 ms | 300 ms | fail | no |
| catalog | 701.48 ms | 600 ms | fail | no |
| catalog_sorted | 529.28 ms | 600 ms | **pass** | no |
| ui | 796.50 ms | 1 s | **pass** | no |
| git_fetch | 5.17 s | 2 s | fail | no |
| relevance | 385.72 ms | 300 ms | fail | n/a |

27,291 requests (286/s), `http_req_failed` 0.00%, checks 100.00%, 13 dropped iterations, peak 94
VUs, API CPU avg 96.2% / peak 114.0%, API memory peak 1227 MiB. The earlier image's pair gave
relevance 404.36 ms p95 with memory peak 1485 MiB and 38 dropped iterations.

**The standing gate: enabling relevance at 10/s flips no scenario verdict.** Relevance's own 300 ms
threshold fails at 386-404 ms, and that is queueing, not rank cost: the same queries served
uncontended on this stack cost 0.5-17.7 ms (below). Any run that flips `catalog_sorted` or `ui`, or
that pushes relevance's p95 past ~1 s, is a regression against this baseline.

## Isolated relevance: `GIT_RATE=0`, `VUS=1 UI_VUS=1 SORT_RATE=1`, `RELEVANCE_RATE=10`

```sh
VUS=1 UI_VUS=1 GIT_RATE=0 SORT_RATE=1 RELEVANCE_RATE=10 RELEVANCE_POOL=<pool> \
  WARMUP=10s HOLD=30s COOLDOWN=5s GIT_DURATION=45s k6 run loadtest.js
```

| Pool | relevance med | p95 | max | Total reqs | Failed | Dropped | API CPU peak | API mem peak |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `default` (11 queries) | 14.87 ms | **83.32 ms** | 370.89 ms | 14,626 (325/s) | 0.00% | 0 | 100.8% | 1339 MiB |
| `adversarial` | 9.63 ms | **14.48 ms** | 122.24 ms | 19,248 (428/s) | 0.00% | 0 | 100.6% | 1267 MiB |
| `unique` | not re-measured on this image | | | | | | | |

Every threshold passes in both runs, zero failed requests, zero dropped iterations. `VUS=1` is not
idle: on sub-millisecond paths one VU per scenario still generates 325-428 requests/s of competing
load, so these are contended runs and CPU peaks near 100% regardless of pool. `default` is the
slowest pool because it contains the corpus's two broadest terms (`git`, `neovim git`); on the
earlier image, without them, the same pool measured 57.59 ms median / 63.42 ms p95 and `unique`
measured 20.26 / 23.47 ms.

### Sustained rate: 50/s documented, derived before the stop-list change

Rate ladder on the earlier image (`29f93c8`), isolated, adversarial pool:

| `RELEVANCE_RATE` | med | p95 | max | Dropped | Failed | API CPU peak |
| --- | --- | --- | --- | --- | --- | --- |
| 10 | 15.32 ms | 17.55 ms | 115.25 ms | 0 | 0.00% | 103.5% |
| 25 | 4.62 ms | 63.59 ms | 269.56 ms | 2 | 0.00% | 100.9% |
| 50 | 8.93 ms | 71.22 ms | 262.39 ms | 6 | 0.00% | 101.8% |
| 100 | 7.74 ms | 60.66 ms | 261.73 ms | 31 | 0.00% | 110.2% |
| 150 | 7.10 ms | 68.65 ms | 273.76 ms | 149 | 0.00% | 101.0% |

p95 is flat from 10/s to 150/s and CPU does not rise with the rate: a 15x increase moves nothing
measurable against the background load. The documented public rate is **50 relevance requests per
second on a 1-CPU node with headroom**, half the highest rate the harness served with negligible
drops, and 5x the rate an earlier full-scan implementation could not sustain. Two conditions:

1. It assumes a node with headroom. On the default full profile the node is already over capacity
   for every expensive scenario and relevance's own p95 is 386 ms at 10/s from queueing.
2. There is no in-app rate limiting (ADR: gate at the reverse proxy), so 50/s is what to configure
   the proxy at, not a limit the app enforces.

Re-derive the ladder before raising the documented rate: it was measured before `git` and `bin`
entered the postings, and `git` is now the most expensive single term the corpus can serve
(~23 ms served, so ~43 `git` requests/s saturate one core on their own).

### Refresh swap under load

One run timed so the unconditional post-startup refresh (download, parse, Mongo save, rebuild,
atomic swap; ~32 s) landed 12 s into the window, on the earlier image with the `unique` pool:
relevance med 61.64 ms / **p95 180.93 ms** / max 2.08 s, `catalog_sorted` p95 344.34 ms, every
threshold passing, 31,118 requests, 0.00% failed, 9 dropped, API CPU avg 92.6% / peak 109.5%,
**API memory peak 1382 MiB** of the 2 GB cap with both generations and the parsed dump live. The
swap itself is a `Volatile.Write` of one reference; the cost around it is the download and the
single-core build, which is why the build stays off the request path and the 2 GB floor exists.

## Served uncontended (1-CPU container, 12 sequential requests per query)

End to end: HTTP, rank, and 50-row serialization.

| By | Query | Rows | min | median | p95 | max | First |
| --- | --- | --- | --- | --- | --- | --- | --- |
| relevance | `git` | 50 | 14.9 | **17.7** | 44.1 | 62.9 | `git-git` |
| relevance | `bin` | 50 | 6.2 | 6.8 | 8.7 | 10.4 | `bin` |
| relevance | `neovim git` | 50 | 14.4 | 16.7 | 19.6 | 523.0 | `neovim-git` |
| relevance | `shelly bin` | 50 | 6.5 | 7.7 | 9.8 | 10.7 | `shelly-bin` |
| relevance | `firefox git` | 50 | 14.7 | 16.8 | 18.7 | 28.8 | `firefox-syncstorage-git` |
| relevance | `python` | 50 | 5.2 | 6.4 | 9.2 | 10.5 | `python-pyalsaaudio` |
| relevance | `vim` | 50 | 1.3 | 1.4 | 2.0 | 2.5 | `neovim-symlinks` |
| relevance | `lib` | 50 | 1.5 | 1.5 | 3.7 | 4.8 | `libreoffice-extension-languagetool` |
| relevance | `the` | 50 | 1.1 | 1.3 | 1.8 | 2.2 | `thermald-git` (prefix tier on the raw segment) |
| relevance | `brwose` (no hit) | 0 | 0.5 | 0.6 | 0.7 | 1.4 | none |
| relevance | `xz` | 24 | 0.8 | 0.9 | 1.0 | 1.4 | `xzoom` |
| relevance | 256-char term (no hit) | 0 | 0.5 | 0.5 | 0.8 | 2.6 | none |
| words | `git` | 50 | 21.1 | **22.9** | 26.0 | 32.8 | `aur-auto-vote-git` |
| words | `bin` | 50 | 8.6 | 10.3 | 14.5 | 15.3 | `visual-studio-code-bin` |
| words | `api` | 50 | 1.8 | 2.4 | 3.3 | 4.5 | `postman-bin` |
| words | `python` | 50 | 7.4 | 9.2 | 12.5 | 12.9 | `pycharm` |
| words | `vim` | 50 | 1.3 | 1.3 | 2.0 | 2.5 | `neovim-git` |
| words | `the` (prose, stopped) | 0 | 0.5 | 0.6 | 0.7 | 0.8 | none |
| name | `neovim-git` | 1 | 0.6 | 0.6 | 1.1 | 1.7 | `neovim-git` |
| name | `git` | 0 | 0.5 | 0.6 | 0.6 | 0.6 | none (exact only) |

A no-hit query costs the same as the empty response: there is no request-time scan of the corpus.
`xz` returns 24 rows rather than the 33 the removed bare-infix tier used to add, confirmed on the
live corpus; broad queries are unaffected by that removal.

## Index shape and build cost

Live corpus (119,975-entry dump), measured in one process so enumeration order is comparable:

| Measure | Value |
| --- | --- |
| `ByWords` keys / postings | 95,243 / 1,008,809 |
| Name-token vocabulary / postings | 69,195 / 266,966 |
| Largest postings | `git` 31,169, `bin` 13,792, `python` 11,258 |
| `BuildFromPackages` median | 2527.3 / 2543.0 ms |
| Names reachable through the token tiers | 45,084 of 119,975 (37.6%) carry a formerly stop-listed name token; `git` 30,210, `bin` 13,721, the other 25 terms are rounding |

Synthetic 120k shape-matched probe (`PackageSearchRelevancePerfTests`, Release, host CPU):
`BuildFromPackages` 914.8 ms of which `RelevanceIndex.Build` **59.5 ms**, retained per generation
**45.5 MB**, name-token vocabulary/postings 116 / 209,970. The relevance structures are a few
percent of a build that already costs about a second, and the build lands on the refresh worker.

Rank cost on the live corpus, `Rank` at the 50-row cap and unlimited (the catalog ranks the whole
membership to page it):

| Query | Candidates | top-50 median | unlimited median |
| --- | --- | --- | --- |
| `git` | 31,229 | 10.08 ms | 24.57 / 25.23 ms |
| `neovim git` | 31,355 | 9.65 / 9.77 ms | 24.40 / 26.49 ms |
| `bin` | 14,015 | 1.93 / 2.01 ms | 7.66 / 8.74 ms |
| `python` | 11,349 | 1.64 / 1.69 ms | 4.30 / 4.65 ms |
| `lib` | 4,753 | 0.26 ms | 1.15 / 1.18 ms |
| `vim` | 946 | 0.07 / 0.08 ms | 0.15 / 0.17 ms |

Catalog match count for `git`: **31,229 (625 pages)**. Worst cases on the synthetic probe: the
unique-query pool averages 22,404 candidates at 6.95 ms median per call, and an adversarial
8-term × 6-char compound reaches 102,927 candidates at 181.50 ms.

## Standing exposure

- A broad *name* token is the most expensive thing one query can ask for, on two surfaces:
  relevance ranks 31,229 candidates for `git`, and legacy `by=words` hydrates and vote-sorts a
  31,169-name posting before taking 50 (22.9 ms served). This is a corpus property, not a
  relevance-path defect, and `python` had the same shape at a third of the size.
- No result caching and no admission control exist. The triggers for adding either are a measured
  rank cost that grows with corpus size rather than candidate count, or a served rate that the
  reverse proxy cannot cap. Nothing measured here meets them; the term that meets them first is
  `git`.
- RPC is untouched by all of this: `AurRpcService.Search` scans the snapshot with `Contains` and
  reads neither `ByWords` nor `RelevanceIndex`, so `/rpc/v5/search/git` returns aurweb's overflow
  sentinel (`"Too many package results."`, past 5,000) exactly as upstream does.
