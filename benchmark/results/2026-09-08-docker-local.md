# 2026-09-08 — local Docker benchmark (production-shaped)

First run of `loadtest.js` against a resource-capped stack using a **clone** of
the production-equivalent corpus. k6 v2.2.0 (host) → `http://localhost:8080`.

**Comparability:** measured with the pre-rotation workload — a fixed 6-name
`PACKAGES` pool, RPC always 2 args, search terms drawn from those names plus
`editor`/`font`, and setup's expected seed `409`s counted in `http_req_failed`
(its 7 failures = 6 conflicts + 1 OOM). So the name-keyed p95s are cache-hot and
term-cold optimistic, and `git_fetch` repeatedly hit one size-tail repository.
Do not read these against runs after that change.

## Corpus and stack

- Live `atoll` DB (`atoll-mongo-1`, read-only source) cloned via
  `mongodump --gzip` (392 MB, 33 s) → `mongorestore --drop` (39 s) into
  disposable volume `atoll-benchmark-mongo`; verified counts identical:
  **118,516 packages / 130,933 revisions / 131,154 scans**, indexes intact.
- Limits in effect: API **1 CPU / 1 GB**, MongoDB **1 CPU / 1 GB**
  (`docker stats --no-stream` confirmed saturation, see below).
- `ATOLL_SECURITY_ENABLED=true`, `ATOLL_SEED_MODE=Off`, refresh disabled,
  `VERIFY_SECURITY=true`. Head scans were `Verified` @ policy v4, matching the
  image's `CurrentPolicyVersion`, so no startup re-queue occurred.
- `PACKAGES=yay,paru,visual-studio-code-bin,slack-desktop,xpipe-ptb,duckstation-gpl`
  — small (1–3 revs) plus large/deep reps: `xpipe-ptb` (10 revs, 4.4 MB),
  `duckstation-gpl` (11 MB). Bare repos materialize on first Git fetch
  (only 2 existed on the source disk); `/app/data` started empty.

## Results (p95)

### Smoke — `VUS=5 UI_VUS=2 GIT_RATE=1 SORT_RATE=0.1 HOLD=30s GIT_DURATION=65s`

| Scenario | p95 | avg | Threshold | Pass |
| --- | --- | --- | --- | --- |
| search | 97 ms | 15 ms | 300 ms | ✓ |
| rpc | 217 ms | 43 ms | 300 ms | ✓ |
| catalog | 399 ms | 156 ms | 600 ms | ✓ |
| catalog_sorted | **5.19 s** | 3.68 s (min 1.15 s) | 3 s | **✗** |
| ui | 204 ms | 69 ms | 1 s | ✓ |
| git_fetch | **4.66 s** | 1.23 s (max 10.19 s) | 2 s | **✗** |

`http_req_failed` 0.02% (6/23,119). All checks passed. Second smoke on warm
caches (repos materialized, Mongo warmed) versus first cold run: sort avg
6.97 s → 3.68 s, git avg 2.37 s → 1.23 s.

### Default profile — `VUS=25 GIT_RATE=2 SORT_RATE=1 UI_VUS=5` (stages 20 s/1 m/15 s)

| Scenario | p95 | avg | max | Threshold | Pass |
| --- | --- | --- | --- | --- | --- |
| search | 1.1 s | 330 ms | 2.28 s | 300 ms | ✗ |
| rpc | 2.1 s | 514 ms | 8.48 s | 300 ms | ✗ |
| catalog | 2.1 s | 1.02 s | 17.79 s | 600 ms | ✗ |
| catalog_sorted | **34.95 s** | 19.19 s | 37.11 s | 3 s | ✗ (+1 request **500**) |
| ui | 3.42 s | 1.66 s | 25.9 s | 1 s | ✗ |
| git_fetch | 22.78 s | 6.88 s | 27.39 s | 2 s | ✗ |

Aggregate: 11,089 requests, `http_req_failed` **0.06%**, checks 99.99%
(2 failures = the OOM'd sorted request), `dropped_iterations` 179 (arrival-rate
VU ceilings), 474 MB received. **k6 exit 99 — every latency threshold crossed;
failure rate stayed ~zero, latency degraded instead.** No container
OOM-kill/restart.

### Idle anchors (isolated, nothing else running)

| Request | Cost |
| --- | --- |
| `GET /v1/packages?page=150&limit=50` (unsorted) | 29–36 ms |
| `GET /v1/packages?sortBy=votes` (full-catalog sort) | 0.79–1.15 s (≈25–35× a page) |
| Git fetch `duckstation-gpl` (5,160,259-byte pack) | 1.87 s / 2.00 s — **at/past the 2 s threshold at zero load** |

### Resources during default run

- `benchmark-atoll-1`: 98–102% of its 1-core cap from ramp-onward (pinned);
  RSS 811 MB → 916 MB / 1 GB; PIDs 41 → 78 (`git upload-pack` spawns).
- `benchmark-mongo-1`: 45–82% CPU, ~463–515 MB / 1 GB.
- Mongo pool healthy under load: 29 connections current, 175 total created.

## Findings

1. **`catalog_sorted` is the capacity wall.** One public request materializes
   all 118k entries. At 1 req/s on 1 CPU it alone saturates the core, and under
   the default profile a `GET /v1/packages?sortBy=…` threw
   `System.OutOfMemoryException` in `PackageService.GetIndexPageAsync →
   EnrichWithCatalog` (`Atoll.Api/Services/Packages/PackageService.cs:64/:96`)
   and returned 500. Confirms the README's request-amplification hypothesis;
   the endpoint needs bounding/redesign before higher `SORT_RATE` is sane.
2. **Git serving re-generates the full pack per negotiation.** `upload-pack`
   on a 5.2 MB pack costs ~2 s of the capped core even idle, so the 2 s
   `git_fetch` threshold is unreachable for large packages at any concurrency;
   small repos (yay ~0.08–0.84 s) meet it.
3. **1 GB is below the cold-start floor for the AUR dump path.** At startup,
   `AurMetadataClient.ParsePackagesAsync` (`JsonDocument.ParseAsync` of the
   full dump, `Services/Catalog/Refresh/AurMetadataClient.cs:59`) threw
   `OutOfMemoryException`; `PackageIndexUpdater` logged "Unable to fetch and
   store new package data" and search still served (index came from the
   restored `aur-metadata`). Either raise the memory limit or stream-parse.
4. Minor (log-noise/behavior, not serving bugs): expected seed `409 Conflict`s
   are logged at `fail:` level with full stacks; an unknown `want` SHA produces
   an unhandled `InvalidOperationException` after "response has already
   started" instead of a clean git-protocol error.

**Run progression stop condition reached:** escalating to `VUS=40` would
measure queueing behind the same pinned core, not additional capacity.

## Raw k6 summary — default profile (excerpt)

```text
  █ THRESHOLDS
    checks{scenario:catalog_sorted}   ✗ 'rate>0.99' rate=96.55%
    checks{scenario:catalog}          ✓ 'rate>0.99' rate=100.00%
    checks{scenario:git_fetch}        ✓ 'rate>0.99' rate=100.00%
    checks{scenario:rpc}              ✓ 'rate>0.99' rate=100.00%
    checks{scenario:search}           ✓ 'rate>0.99' rate=100.00%
    checks{scenario:ui}               ✓ 'rate>0.99' rate=100.00%
    http_req_duration{scenario:catalog_sorted}  ✗ 'p(95)<3000' p(95)=34.95s
    http_req_duration{scenario:catalog}         ✗ 'p(95)<600'  p(95)=2.1s
    http_req_duration{scenario:git_fetch}       ✗ 'p(95)<2000' p(95)=22.78s
    http_req_duration{scenario:rpc}             ✗ 'p(95)<300'  p(95)=2.1s
    http_req_duration{scenario:search}          ✗ 'p(95)<300'  p(95)=1.1s
    http_req_duration{scenario:ui}              ✗ 'p(95)<1000' p(95)=3.42s
    http_req_failed                             ✓ 'rate<0.01'  rate=0.06%

    http_req_duration...............: avg=622.08ms min=68.82µs med=206.65ms p(90)=1.19s p(95)=1.77s max=37.11s
      { scenario:catalog_sorted }...: avg=19.19s   min=1.27s   med=21.75s   p(90)=31.94s p(95)=34.95s max=37.11s
      { scenario:catalog }..........: avg=1.02s    med=684.44ms p(90)=1.7s  p(95)=2.1s   max=17.79s
      { scenario:git_fetch }........: avg=6.88s    med=5.49s   p(90)=13.8s  p(95)=22.78s max=27.39s
      { scenario:rpc }..............: avg=514.38ms med=201.87ms p(90)=1.19s p(95)=2.1s   max=8.48s
      { scenario:search }...........: avg=330.48ms med=200.52ms p(90)=892.62ms p(95)=1.1s max=2.28s
      { scenario:ui }...............: avg=1.66s    med=937.56ms p(90)=2.54s p(95)=3.42s  max=25.9s
    http_req_failed.................: 0.06%  7 out of 11089
    dropped_iterations..............: 179
    iterations......................: 10222  97.66/s
    data_received...................: 474 MB 4.5 MB/s
```
