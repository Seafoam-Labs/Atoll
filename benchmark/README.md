# Atoll benchmark suite

k6 load test and a resource-capped Docker stack for measuring Atoll's public
request-serving capacity. Requires [k6](https://k6.io/) and Docker.

Everything after `setup()` is read-only. Safe against the local stack; read
[Corpora](#corpora) and [Remote AWS runs](#remote-aws-runs) before pointing it
at anything shared.

## Quick start

```sh
cd benchmark
docker compose up --build -d   # same image as the root compose, deliberately isolated (see below)
docker stats --no-stream       # API and MongoDB are each capped at 1 CPU / 1 GB
k6 run loadtest.js             # setup() waits for /health, seeds the corpus, then loads
```

`setup()` polls `/health` for up to 5 minutes (a cold stack downloads and parses
the AUR metadata dump before serving), seeds every name in `PACKAGES` through
`POST /v1/packages/{name}/seed` — `409 Conflict` counts as already present, so
reruns stay cheap — and fails before load if a seed is not queryable through the
AUR RPC.

The local stack disables refresh, automated seeding, and security scanning so
serving paths are measured in isolation; the header comment of
`docker-compose.yaml` lists each deviation and its rationale. Recorded runs are
in [`results/`](results/), newest first.

## Workload

Six scenarios cover the contracts real AUR clients and browser users exercise;
keeping them separate stops a slow path from hiding in the aggregate.

| Scenario | Executor | Path | Threshold |
| --- | --- | --- | --- |
| `search` | ramping-vus, 25 VUs | `GET /v1/search?query=&by=name/words/provides` (in-memory index); each mode queries its own pool — `PACKAGES` for exact names, `TERMS` for words, `PROVIDES` for provided tokens | p95 < 300 ms |
| `rpc` | ramping-vus, 25 VUs | yay-style `POST /rpc` form `v=5&type=multiinfo` with a batched 1–200 `arg[]` list, plus 10% `GET /rpc/v5/suggest` | p95 < 300 ms |
| `catalog` | ramping-vus, 12 VUs | `GET /v1/packages?page=` (Mongo), plus sampled `/versions` and detail reads | p95 < 600 ms |
| `catalog_sorted` | constant-arrival-rate, 1 request/s | `GET /v1/packages?sortBy=votes/popularity/version` | p95 < 3 s |
| `ui` | ramping-vus, 5 VUs | `GET /` and `GET /package/{name}` (server-rendered Blazor) | p95 < 1 s |
| `git_fetch` | constant-arrival-rate, 2 fetches/s | Full Git Smart HTTP fetch: `info/refs` advertisement → one-shot `want`/`done` → `git-upload-pack` | p95 < 2 s |

The two rate-driven scenarios are capped instead of VU-driven because each is
expensive per request: Git fetching shells out to `git upload-pack` server-side,
and non-default sorting reads and enriches the whole seeded catalog before
returning one page — the suite's clearest capacity and denial-of-service
boundary. Overall failures must stay below 1% with at least 99% passing checks
per scenario; the default profile runs about 95 seconds, with the rate-driven
scenarios bounded to match.

## Corpora

**Isolated (default).** MongoDB starts empty and `setup()` stores only the names
in `PACKAGES`; security is off, so every content read is allowed. Good for a fast
request-contract and small-data regression check, and for nothing else: it cannot
establish full-AUR-catalog capacity, sort cost, or security-gate behavior.

**Production-shaped.** Restore copies of the production-equivalent `/data/db`
and `/app/data` into the stack's own volumes, `atoll-benchmark-mongo` and
`atoll-benchmark-data` — delete volumes left by isolated runs first, or name
other disposable copies with `MONGO_DATA_VOLUME`/`ATOLL_DATA_VOLUME`. `PACKAGES`
must name packages that are already seeded there with `Verified` head scans.
Never point this stack at a live production database or volume, and keep
`ATOLL_SEED_MODE=Off`.

```sh
ATOLL_SECURITY_ENABLED=true docker compose up --build -d

# Refuse to measure pending, flagged, or absent heads, on a rotating request mix.
PACKAGES="$(KEEP=duckstation-gpl,xpipe-ptb ./sample-packages.sh 80)" \
  VERIFY_SECURITY=true k6 run loadtest.js
```

`sample-packages.sh` draws a fresh set of servable names from the stack's Mongo
so the name-keyed scenarios (rpc, detail, versions, ui, git) exercise a rotating
working set rather than the same cache-hot documents, and `KEEP=` pins curated
size-tail reps. A rotated run pays two expected costs: bare-repo materialization
inside `git_fetch` on each name's first fetch, and much higher `data_received` —
the detail read returns a package's whole file contents, not metadata.

## Tunables (environment variables)

| Variable | Default | Meaning |
| --- | --- | --- |
| `TARGET` | `http://localhost:8080` | Base URL of the instance under test |
| `PACKAGES` | `yay,paru,visual-studio-code-bin,slack-desktop,xpipe-ptb,duckstation-gpl` | Request mix for the name-keyed scenarios; spans median documents and the size/depth tail. Rotate it (see above). Every name must be seedable — `setup()` aborts on one that is not |
| `TERMS` | 22 validated words, hot to unmatched | Queries for `by=words` and RPC suggestions |
| `PROVIDES` | 9 validated provided tokens | Queries for `by=provides` |
| `VUS` | `25` | Peak VUs per VU-driven scenario |
| `UI_VUS` | `5` | Peak VUs requesting server-rendered HTML |
| `GIT_RATE` | `2` | Git fetches per second |
| `SORT_RATE` | `1` | Full-catalog sorts per second; increase slowly |
| `WARMUP` / `HOLD` / `COOLDOWN` | `20s` / `1m` / `15s` | Ramp-up, steady-state, ramp-down stage durations |
| `GIT_DURATION` | `95s` | Duration of the rate-driven scenarios; keep aligned with the three stages |
| `VERIFY_SECURITY` | `false` | Require every selected package's head scan to be `Verified` before load |

## Run progression

Start low, raise one dimension per run, and retain the k6 summary next to
`docker stats --no-stream` and application/Mongo metrics. Stop at the first
threshold failure, growing request duration, container memory growth, or Mongo
connection exhaustion.

```sh
# Contract and basic serving check.
VUS=5 UI_VUS=2 GIT_RATE=1 SORT_RATE=0.1 HOLD=30s GIT_DURATION=65s k6 run loadtest.js

# Expected small-node operating load.
k6 run loadtest.js

# Controlled capacity step: increase one dimension, then compare p95 and errors.
VUS=40 UI_VUS=8 GIT_RATE=3 SORT_RATE=1 HOLD=2m GIT_DURATION=155s k6 run loadtest.js
```

Measure `catalog_sorted` on its own before allowing higher rates, since one
public request scales with the number of seeded packages. This suite detects
that limit; bounding or redesigning the endpoint is the production-readiness
decision to make from the measurement.

### Excluded from the workload

`POST /v1/packages/{name}/seed`, `DELETE /v1/packages/{name}`, and
`POST /v1/packages/{name}/security/rescan` stay out of the concurrent workload:
they mutate package data, materialize or remove Git repositories, and queue
scanner work. Exercise them as short one-request checks in a disposable
environment, then soak separately with production-equivalent scanner concurrency
and refresh enabled — mixing that with serving measurement makes a regression
indistinguishable from intended background work.

## Remote (AWS) runs

```sh
# `terraform output application_url` from the repository root
TARGET=https://atoll.example.com VUS=10 GIT_RATE=1 k6 run loadtest.js
```

### The WAF gate

The web ACL (`terraform/waf.tf`) is attached to the CloudFront distribution, so
it inspects every remote request, and a CloudFront-associated rule aggregates by
**real viewer IP** — all k6 VUs share one budget. The default
`waf_rate_limit = 250` requests per 5 minutes is ≈ 0.83 req/s, so a load test
trips it in the first second and the source IP then takes 403s (counted as
failures) until the rolling count decays: up to ~5 minutes of dead time after
the run stops. Pick one:

- **Raise it for the session:**
  `terraform apply -var "waf_rate_limit=$(( EXPECTED_RPS * 300 * 2 ))"` — size at
  ~2× peak rate × window so NAT neighbours and bursts don't trip it. Changing a
  rate-based rule's settings resets its counters, so the new limit applies
  cleanly; re-apply the default afterwards.
- **Exempt the load generator:** give the rule a scope-down statement excluding
  your test IP — WAF only aggregates, counts, and limits matching requests.
- **Bypass the edge:** run an EC2/VPC runner against the internal ALB
  (temporarily allow its subnet in a security group that currently admits only
  the CloudFront origin prefix list). That measures the app stack without WAF or
  the CDN leg — and note that reaching the CloudFront URL from inside the VPC
  does *not* dodge the WAF, which is evaluated at the edge for every origin.

### Other remote notes

- Remote p95s include TLS termination and the viewer→edge hop, so they are
  inflated versus the local stack; `origin_read_timeout = 60` in `cloudfront.tf`
  is the hard ceiling for slow origin responses, long Git fetches included.
- The distribution uses the managed `CachingDisabled` policy, so every request
  reaches the origin — no caching masks serving cost.
- WAF and CloudFront bill per processed request (a default-profile run is cents),
  but the load lands on the live ECS task serving real clients, and anything
  `setup()` seeds keeps being served to them afterwards.
- Validate on an isolated pre-production deployment, starting at the first
  progression command. Make a direct production target only a final, low-rate
  confirmation, after the WAF treatment is agreed and with monitoring and a stop
  condition in place.
