# 2026-09-24: relevance benchmark gate (Phase 2, step 7)

First benchmarks of the new `?by=relevance` ranked path, added in Phase 2 of the
search plan and still undocumented/unexposed (served, but the root README API
table, `endpoints.http`, and `docs/ARCHITECTURE.md` are untouched until Phase 3).
Two runs, both on the isolated 1-CPU benchmark stack: an **isolated** run for
relevance's own latency, and a **full-profile** run with relevance enabled to
measure interference against the 2026-09-19 `SORT_RATE=10` headline baseline.

Outcome in one line: relevance's own served cost is ~33 ms median / ~63 ms p95
(matching the in-process probe), but it is CPU-bound (a full ~120k-key linear
scan per request), so at `RELEVANCE_RATE=10` it saturates the single core. In
isolation alongside `git_fetch` at its 1/s floor the scenario p95 is 492 ms; in
the full profile it is 1.39 s, it pushes `catalog_sorted` over its threshold,
triples `git_fetch` p95, and halves total throughput. Phase 3 exposure at 10/s on
a 1-CPU node is not supported by these runs.

## Comparability

- Isolated benchmark stack (`benchmark/docker-compose.yaml`), API capped at
  1 CPU / 2 GB, MongoDB 1 CPU / 1 GB; security off, `Atoll__Seed__Mode=Off`,
  refresh disabled. `setup()` seeds the six-name `PACKAGES` pool (irrelevant to
  relevance, which ranks the in-memory index built from the AUR dump).
- Image built from the working tree (uncommitted Phase 2 steps 1-6).
- In-memory index from the live `packages-meta-ext-v1` dump: **119,637 names**
  for the isolated run, **119,929** for the full-profile run (the dump drifts
  daily; the baseline doc recorded 119,637).
- k6 v2.2.0, .NET 10. `k6 run` reads OS env into `__ENV`; `k6 inspect` does not
  (use `-e`), which is how the scenario-registration check below was done.
- The full-profile comparison is cross-day against the 2026-09-19 rebaseline and
  inherits its host/corpus caveats, but the stack config and 1-CPU cap match.

## Scenario registration check

`k6 inspect loadtest.js` (default `RELEVANCE_RATE=0`) lists six scenarios and no
relevance thresholds, so the default profile and the recorded baseline are
unchanged. `k6 inspect -e RELEVANCE_RATE=10` adds the `relevance`
constant-arrival-rate scenario plus its `p(95)<300` and `checks rate>0.99`
thresholds.

## Run 1: isolated (`RELEVANCE_RATE=10`, competing load minimized)

```sh
VUS=1 UI_VUS=1 GIT_RATE=1 SORT_RATE=1 RELEVANCE_RATE=10 \
  WARMUP=10s HOLD=30s COOLDOWN=5s GIT_DURATION=45s k6 run loadtest.js
```

`git_fetch` has a hard rate floor of 1/s (k6 rejects rate 0), so this is
low-interference, not clean isolation; git alone (med 502 ms, p95 1.11 s) eats
much of the single core.

| Scenario | avg | med | p90 | p95 | max | Threshold |
| --- | --- | --- | --- | --- | --- | --- |
| **relevance** | 212 ms | 194 ms | 393 ms | **492 ms** | 1.2 s | 300 ms **✗** |
| git_fetch | 597 ms | 502 ms | 892 ms | 1.11 s | 5 s | 2 s ✓ |
| catalog_sorted | 92 ms | 89 ms | 160 ms | 226 ms | 987 ms | 600 ms ✓ |
| catalog | 109 ms | 99 ms | 202 ms | 285 ms | 788 ms | 600 ms ✓ |
| search | 18 ms | 0.5 ms | 88 ms | 96 ms | 1.06 s | 300 ms ✓ |
| rpc | 31 ms | 0.3 ms | 95 ms | 210 ms | 692 ms | 300 ms ✓ |
| ui | 42 ms | 3.6 ms | 99 ms | 140 ms | 1.06 s | 1 s ✓ |

45 s, 4,763 requests, `http_req_failed` 0.00%, checks 100%, 4 dropped iterations.
Only the relevance threshold crossed.

### Relevance's own latency (uncontended)

120 sequential single-threaded requests across the `RELEVANCE_QUERIES` pool, no
concurrent load, same 1-CPU container:

```
min=27.3ms  median=32.9ms  p90=61.3ms  p95=62.5ms  max=132.4ms
```

This matches the in-process probe `PackageSearchRelevancePerfTests` (Release,
host CPU, 120k shape-matched index): single-term median ~25-31 ms / p95 ~28-36 ms,
8-term mixed-coverage median ~50 ms / p95 ~58 ms. Every request pays the full
~120k `ByNames` scan regardless of hit count (the contains-prefilter gates the
tokenizer, not the scan), so a no-hit `brwose` costs the same as `vim`.

## Run 2: full profile with relevance (`k6 run` defaults + `RELEVANCE_RATE=10`)

```sh
RELEVANCE_RATE=10 k6 run loadtest.js
# VUS=25 UI_VUS=5 GIT_RATE=2 SORT_RATE=10, WARMUP=20s HOLD=1m COOLDOWN=15s
```

Compared against the 2026-09-19 headline baseline (`SORT_RATE=10 GIT_RATE=2`, no
relevance), which already crossed five of six latency thresholds from aggregate
CPU saturation (API CPU max 118%).

| Scenario | Baseline p95 | With relevance p95 | Threshold | Baseline | Now |
| --- | --- | --- | --- | --- | --- |
| search | 307 ms | **808 ms** | 300 ms | ✗ | ✗ |
| rpc | 626 ms | **899 ms** | 300 ms | ✗ | ✗ |
| catalog | 696 ms | **1.59 s** | 600 ms | ✗ | ✗ |
| catalog_sorted | 523 ms | **1.18 s** | 600 ms | ✓ | **✗** |
| ui | 1.00 s | **2.07 s** | 1 s | ✗ | ✗ |
| git_fetch | 4.75 s | **13.4 s** (max 20.79 s) | 2 s | ✗ | ✗ |
| relevance | n/a | **1.39 s** | 300 ms | n/a | ✗ |

Totals: 12,916 requests (baseline 28,544, i.e. throughput roughly **halved**),
`http_req_failed` 0.04% (6 of 12,916, all six were relevance requests; baseline
0.00%), checks: relevance 99.33% (900/906), all others 100%, `dropped_iterations`
113 (baseline 36), peak 103 VUs (baseline 91). Every latency threshold crossed,
including `catalog_sorted`, which the baseline passed.

## Interpretation and gate verdict

- Relevance per-request cost is small in absolute terms (~33 ms median / ~63 ms
  p95 served uncontended, ~25-60 ms in-process), but it is **CPU-bound**: a full
  linear scan of ~120k names on every request, independent of result size.
- At 10 ranks/s that is ~0.3 core of steady demand. The default full profile
  already pegs the single core (API CPU 118% at baseline), so the added demand
  cascades into queueing across every scenario: `catalog_sorted` flips from pass
  to fail, `git_fetch` p95 roughly triples, search/rpc/catalog/ui all inflate,
  and served throughput halves. The six failed requests are relevance iterations
  dropped under the saturation.
- This is a capacity ceiling on a 1-CPU node, not a relevance-path defect. The
  default profile (`RELEVANCE_RATE=0`) is untouched, so the recorded baseline
  stays valid; relevance only competes when explicitly enabled.

**Phase 3 gate: do not expose relevance at 10/s on a 1-CPU node.** Before
exposure, pick at least one of:

1. Cap the sustained relevance rate well below 10/s for single-CPU deployments
   (relevance is cheap alone; the problem is concurrency against an already-pegged
   core).
2. Give the node more than one CPU. Each rank is single-threaded, but concurrent
   requests need cores; the 1-CPU cap is the binding constraint here.
3. Land the Phase 4 index structures (sorted-name prefix or trigram map) to
   remove the unconditional full scan, which is what makes every query, including
   no-hit ones, cost a full traversal.

A re-run after any of these should compare like with like against this file and
the 2026-09-19 baseline, never against the 5-VU smokes.
