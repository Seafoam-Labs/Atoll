# 2026-09-08 — local Docker benchmark (production-shaped), 2 GB API RAM

Rerun of `2026-09-08-docker-local.md` with a single variable changed: the API
container memory limit **1 GB → 2 GB** in `benchmark/docker-compose.yaml`; CPU
limits unchanged (1 core per container). Question: do the 1 GB run's OOM
findings recur, and what do the profiles look like with headroom?

**Comparability:** same host, same `benchmark-atoll` image (built from
`3ab1a4d` for the recorded run and reused here — identical binary), same
six-name `PACKAGES` pool, thresholds, and stage shapes, same cold-start order
(fresh volumes → restore → API start → cold smoke → warm smoke → default →
idle anchors). Limits verified via `docker inspect` (API 2 GiB / 1 core,
MongoDB 1 GiB / 1 core). Two caveats:

- The recorded run used the pre-rotation draft of `loadtest.js`: RPC batches
  always 2 args (now sampled from {1, 2, 5}), setup seed `409`s counted in
  `http_req_failed` (now excluded via `expectedStatuses(201, 409)`), and a
  smaller search-term pool (now the fixed 22-term `TERMS`). Absolute p95s
  across the two docs are indicative; the failure-mode comparison is exact.
- The corpus is a fresh clone of the live `atoll` DB (dump 410,544,125 bytes).
  Pre-startup counts matched the recorded run exactly (118,516 packages /
  130,933 revisions / 131,154 scans / 118,992 catalog); the startup refresh
  then reconciled upstream churn (catalog → 119,082; 6 upstream-deleted
  packages pruned, none in the pool) → 118,510 / 130,926 / 131,147. All scans
  at policy v4 (no startup re-queue); 136 non-Verified scans, pool heads
  `Verified` (`VERIFY_SECURITY=true` setup checks passed in every run).

## Startup (1 GB finding: dump-parse OOM)

Resolved by headroom. Listening and `/health` 200 within ~2 s; the index built
from the restored `aur-metadata` (118,992 entries) at 16:46:17; the
`packages-meta-ext-v1` dump fetch began 16:46:21 and
`Package index refreshed with 119082 packages` completed 16:46:40 (~19 s).
RSS during/after the parse: **~1.19–1.21 GiB** — i.e. the parse working set
alone exceeds the old 1 GB limit, which is why that run OOM'd
(`AurMetadataClient.ParsePackagesAsync`). No re-queue: all scans already at
policy v4.

## Results (p95)

### Smoke cold — `VUS=5 UI_VUS=2 GIT_RATE=1 SORT_RATE=0.1 HOLD=30s GIT_DURATION=65s`

| Scenario | p95 | avg | Threshold | Pass |
| --- | --- | --- | --- | --- |
| search | 199 ms | 50 ms | 300 ms | ✓ |
| rpc | 403 ms | 73 ms | 300 ms | ✗ |
| catalog | 408 ms | 211 ms | 600 ms | ✓ |
| catalog_sorted | **11.95 s** | 7.60 s (min 1.66 s) | 3 s | **✗** |
| ui | 395 ms | 167 ms | 1 s | ✓ |
| git_fetch | **6.48 s** | 1.82 s (max 11.5 s) | 2 s | **✗** |

`http_req_failed` 0.00% (0/9,091), checks 100% (20,363).

### Smoke warm — same profile, second run

| Scenario | p95 | avg | Threshold | Pass |
| --- | --- | --- | --- | --- |
| search | 194 ms | 40 ms | 300 ms | ✓ |
| rpc | 234 ms | 50 ms | 300 ms | ✓ |
| catalog | 398 ms | 173 ms | 600 ms | ✓ |
| catalog_sorted | **6.22 s** | 3.96 s (min 0.77 s) | 3 s | **✗** |
| ui | 296 ms | 90 ms | 1 s | ✓ |
| git_fetch | **8.98 s** | 1.76 s (max 12.19 s) | 2 s | **✗** |

`http_req_failed` 0.00% (0/12,315), checks 100%. Warm-vs-cold improvement
matches the recorded run's pattern (sorted avg 7.60 s → 3.96 s).

### Default profile — `VUS=25 GIT_RATE=2 SORT_RATE=1 UI_VUS=5` (stages 20 s/1 m/15 s)

| Scenario | p95 | avg | max | Threshold | Pass |
| --- | --- | --- | --- | --- | --- |
| search | 897 ms | 289 ms | 3.2 s | 300 ms | ✗ |
| rpc | 1.99 s | 418 ms | 5.19 s | 300 ms | ✗ |
| catalog | 1.59 s | 810 ms | 13.32 s | 600 ms | ✗ |
| catalog_sorted | **30.95 s** | 17.92 s | 34.26 s | 3 s | ✗ |
| ui | 2.5 s | 1.16 s | 20.5 s | 1 s | ✗ |
| git_fetch | 18.51 s | 5.72 s | 23.89 s | 2 s | ✗ |

Aggregate: 13,123 requests, `http_req_failed` **0.00%**, checks **100%**,
`dropped_iterations` 165, 642 MB received. **k6 exit 99 — every latency
threshold crossed; zero failed requests.** In the 1 GB run the same profile
produced a 500 on `catalog_sorted` (`System.OutOfMemoryException` in
`PackageService.GetIndexPageAsync` → `EnrichWithCatalog`) — it did **not**
recur here; full-catalog sorts served 200 for the entire run at 1 req/s.

### Idle anchors (isolated, nothing else running)

| Request | 1 GB run | 2 GB run |
| --- | --- | --- |
| `GET /v1/packages?page=150&limit=50` | 29–36 ms | 30–32 ms |
| `GET /v1/packages?sortBy=votes` | 0.79–1.15 s | 0.66–0.77 s |
| Git clone `duckstation-gpl` (full pack) | 1.87 s / 2.00 s | 1.89 s / 2.03 s |

Unsorted paging is unchanged, the full-catalog sort is ~25% faster, and the
large-package pack generation cost is **identical** — it is CPU-bound
(served pack ≈ 5–6 MB; client stores it loose, < `transfer.unpackLimit`
objects).

## Resources during default run

- `benchmark-atoll-1`: 85–116% of its 1-core cap from ramp-onward (pinned,
  same as the 1 GB run); RSS 1.20 → **1.59 GiB peak** / 2 GiB (1 GB run:
  916 MB at its cap), settling ~1.41 GiB; PIDs 25 → 84 (`git upload-pack`
  spawns).
- `benchmark-mongo-1`: 1–63% CPU, 449–505 MB / 1 GiB — unchanged, not
  memory-bound.
- No container OOM-kill or restart; **zero `OutOfMemoryException`** in the API
  logs across all three runs. The only exceptions are the expected
  `PackageConflictException` seed 409s (18 = 6 packages × 3 setup rounds),
  still logged at `fail:` level with stacks.

## Findings

1. **Memory was the failure axis; CPU is the latency axis.** With 2 GB both
   OOM modes from the 1 GB run are gone: the startup dump path completes
   (previously fatal) and the full-catalog sort no longer 500s under load
   (34,529 requests across the three runs, 0 failures, 100% checks). But the
   default profile still crosses all six latency thresholds with the core
   pinned at ~100% — averages improved only 10–30% (e.g. sorted 19.19 s →
   17.92 s, git 6.88 s → 5.72 s, ui 1.66 s → 1.16 s), a GC-pressure relief,
   not a capacity change. The `catalog_sorted` redesign/bounding work and the
   git pack-regeneration cost remain the capacity items; RAM does not buy
   capacity here.
2. **1 GB remains below the cold-start floor.** Post-parse RSS alone is
   ~1.19–1.21 GiB, and default-load peak reached 1.59 GiB. 2 GB covers both
   with headroom; the stream-parse alternative (see 1 GB run, finding 3)
   stays relevant only if provisioning 2 GB is unacceptable.
3. **Git serving is unchanged** — per-negotiation full-pack regeneration
   costs the same idle seconds as at 1 GB (1.89/2.03 s for the corpus's
   largest package), and `git_fetch` still misses its 2 s threshold at any
   load because one big repo draw lands in ~1 of 6 requests.
4. Minor findings from the 1 GB run are unchanged where re-exercised: seed
   `409 Conflict`s still log at `fail:` with stacks (log noise, not errors —
   they are excluded from `http_req_failed` by the current script). The
   unknown-`want`-SHA git error path was not exercised this run.

## Raw k6 summary — default profile (excerpt)

```text
  █ THRESHOLDS
    checks{scenario:*}                ✓ 'rate>0.99' rate=100.00%  (all six)
    http_req_duration{scenario:catalog_sorted}  ✗ 'p(95)<3000' p(95)=30.95s
    http_req_duration{scenario:catalog}         ✗ 'p(95)<600'  p(95)=1.59s
    http_req_duration{scenario:git_fetch}       ✗ 'p(95)<2000' p(95)=18.51s
    http_req_duration{scenario:rpc}             ✗ 'p(95)<300'  p(95)=1.99s
    http_req_duration{scenario:search}          ✗ 'p(95)<300'  p(95)=897.09ms
    http_req_duration{scenario:ui}              ✗ 'p(95)<1000' p(95)=2.5s
    http_req_failed                             ✓ 'rate<0.01'  rate=0.00%

    http_req_duration...............: avg=518.17ms min=66.83µs med=208.32ms p(90)=910.89ms p(95)=1.45s max=34.26s
      { scenario:catalog_sorted }...: avg=17.92s   min=810.59ms med=20.83s  p(90)=28.53s  p(95)=30.95s max=34.26s
      { scenario:catalog }..........: avg=810.26ms med=590.44ms p(90)=1.29s  p(95)=1.59s  max=13.32s
      { scenario:git_fetch }........: avg=5.72s    med=4.98s    p(90)=8.39s  p(95)=18.51s max=23.89s
      { scenario:rpc }..............: avg=417.89ms med=201.26ms p(90)=908.55ms p(95)=1.99s max=5.19s
      { scenario:search }...........: avg=288.75ms med=202.4ms  p(90)=604.77ms p(95)=897.09ms max=3.2s
      { scenario:ui }...............: avg=1.16s    med=700.71ms p(90)=2.09s  p(95)=2.5s   max=20.5s
    http_req_failed.................: 0.00%  0 out of 13123
    dropped_iterations..............: 165
    iterations......................: 12061  117.63/s
    data_received...................: 642 MB 6.3 MB/s
```
