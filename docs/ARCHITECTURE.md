# Architecture Overview

Atoll is a self-hosted Arch Linux AUR mirror API. It downloads AUR package metadata, stores package files with revision
history, provides fast in-memory search, and exposes each package as a cloneable Git repository over HTTP.

## Context & Goals

- **Problem:** Provide a private, searchable AUR package registry with version history and Git-compatible read access to
  PKGBUILD files.
- **Scope:** Mirrors AUR metadata and package files read-only _from AUR's perspective_ - Atoll never writes upstream.
  Local mutation endpoints are unauthenticated: trusted deployments may enable them, while publicly reachable instances
  must set `Atoll:Mutations:Enabled=false`. Git push (`git-receive-pack`) and authentication are currently out of scope.
- **Success criteria:** Search queries served from memory in < 10 ms end-to-end; metadata index stays in sync with AUR
  within the configured refresh interval (default 5 minutes). Seeded content is updated only when the optional
  package-refresh worker is enabled.

## Architecture Principles

- **MongoDB is the only authoritative state** - the in-memory index and the bare repos under `data/repos/` are both
  rebuildable caches. The index is rebuilt from MongoDB on startup. Note: the index and seed workers assume a
  **single instance**; running multiple replicas duplicates AUR fetches and races on seeding until a distributed lock is
  added.
- **Domain logic lives in services, not endpoints** - `Endpoints.cs` only routes and maps; all business rules live in
  `Services/`.

## Tech Stack

| Layer | Technology | Rationale |
| --- | --- | --- |
| Runtime | .NET 10 | Current LTS runtime with built-in async primitives |
| Framework | ASP.NET Core Minimal API | Low-overhead routing; no controller boilerplate needed |
| Web UI | Blazor (Interactive Server + SSR) + Tailwind CSS v4 | Built-in web interface for catalog search, file inspection, and status dashboard |
| Database | MongoDB 8 (via MongoDB.Driver) | Flexible document model suits package metadata + per-revision content documents |
| In-memory index | ImmutableDictionary (ByNames / ByWords / ByProvides) | Fast reads with a consistent per-request snapshot, no external cache tier |
| Git subprocess | CliWrap + system `git` | Reuses the `git upload-pack` implementation |
| Containerization | Docker / Docker Compose | Single `compose.yaml` spins up API + MongoDB |
| Cloud infra | Terraform (`terraform/`) | Cloud infrastructure definitions |

## Architecture

```txt
                      [Git client / HTTP client / Web browser]
                                         │
                                         ▼
                      [ASP.NET Core Minimal API / Blazor]
                         (:8080 container / :5290 dev)
                                         │
     ┌────────────────────────┬──────────┴───────────┬────────────────────────┐
     │                        │                      │                        │
     ▼                        ▼                      ▼                        ▼
[PackageSearchService]   [IPackageService]  [GitTransferService]    [PackageDetailsService /
(in-memory index)        (MongoDB repos)    (git upload-pack)        StatusDashboardService]
     │                        │                      │                        │
     ▼                        │                      ▼                        │
[PackageIndexStore]           │              [Bare Git Repos]                 │
     ▲                        │              (data/repos/ cache)              │
     │                        │                      │                        │
     │                        └──────────┬───────────┘                        │
     │                                   │                                    │
     │                                   ▼                                    │
     │                              [MongoDB]                                 │
     │                        (authoritative state) ──────────────────────────┘
     │                                   ▲
     │                                   │ writes & leases
     ├───────────────────────┬───────────┴───────────┬────────────────────────┐
     │                       │                       │                        │
[PackageIndexWorker]   [SeedWorker]        [RefreshWorker]          [SecurityWorker]
(polls AUR metadata)   (Direct / Bulk)     (re-syncs upstream)      (static analysis)
     │                       │                       │
     ▼                       ▼                       ▼
 [AUR Metadata]        [AUR / Mirror]      [GitHub AUR Mirror]
```

- **PackageIndexWorker:** Periodically downloads the AUR metadata archive (`packages-meta-ext-v1.json.gz`) with
  conditional `ETag`/`Last-Modified` headers, persists new snapshots to MongoDB, and atomically swaps the in-memory
  `PackageIndexStore` snapshot. On startup, it primes the index from MongoDB so search is immediately available. When
  `DataSource:PruneDeletedPackages` is enabled, it prunes seeded packages that disappeared upstream (>10% drops require
  confirmation by a second snapshot).
- **Seed workers (Direct / Bulk):** `Seed:Mode` controls automated seeding of missing packages. Direct mode clones
  individual packages from AUR; Bulk mode batch-fetches branches from the GitHub AUR mirror into a local cache.
- **PackageRefreshWorker:** Periodically checks already-seeded packages against upstream Git HEADs, appending new
  revisions only when content changes. Shares the mirror cache with Bulk seeding.
- **PackageSecurityWorker:** Scans newly seeded or refreshed revisions using deterministic static analysis, gating
  content and Git access until verified.
- **PackageSearchService:** Serves all search queries in-memory from `PackageIndexStore` snapshots with zero database
  I/O.
- **Blazor Web UI:** Razor components under `Components/` provide a fast, responsive UI:
  - `PackageCatalogService`: Powers `/` with cached pre-sorted views, live filtering, and server-side pagination.
  - `PackageDetailsService`: Assembles package metadata, relations, file trees, and security verdicts.
  - `StatusDashboardService`: Aggregates worker statuses, sync metrics, and exclusions for `/status`.
- **GitTransferService:** Serves Git clone/fetch requests via `git upload-pack`. Bare repositories under `data/repos/`
  are lazily materialized from MongoDB on demand. Commits are synthesized deterministically from stored revisions, so
  **commit SHAs served over Git match Atoll revision IDs, not upstream AUR commit SHAs**.
- **Package name semantics:** `{name}` in routes is always the AUR **pkgname**. When interacting with Git mirrors or
  AUR, Atoll maps `pkgname` to its parent **pkgbase** using the in-memory index
  (`MongoPackageService.ResolvePackageBase`), correctly handling split packages.

Request paths of note (everything else is standard Minimal API routing with `GlobalExceptionHandler` converting
unhandled exceptions to RFC 9457 `ProblemDetails`):

- **Web UI:** Blazor routes (`/`, `/package/{name}`, `/package/{name}/files`, `/package/{name}/revisions`, `/status`)
  map to Razor components rendering interactive and static SSR pages directly backed by `PackageCatalogService`,
  `PackageDetailsService`, and `StatusDashboardService`.
- **Search:** `PackageSearchService` reads the current immutable `PackageIndexStore` snapshot and returns results with
  no I/O.
- **Package CRUD:** `MongoPackageService` delegates to `MongoPackageRepository`; seeding clones the AUR Git repo to a
  temp directory, reads the files, and persists them to MongoDB.
- **Git Smart HTTP:** `GitTransferService` ensures the on-disk bare repository exists and is current (materializing from
  MongoDB on first use or after a new revision), then pipes stdin/stdout to `git upload-pack`.

## State & Storage

- **MongoDB (authoritative):**
  - `packages` — Root package documents. Stores metadata, embedded revision headers (ID, author, timestamp, message),
    `headRevisionId` pointer, and refresh sync watermarks (`upstreamPackageBase`, `lastSyncedUpstreamHead`,
    `lastSyncAttemptAt`, `lastSyncSucceededAt`, `lastSyncError`). Indexed on `packageName` at startup.
  - `package-revisions` — Normalized snapshot file content (one document per retained revision, keyed by
    `{packageName}:{revisionId}`). History length does not bloat root package documents; snapshots are capped by
    MongoDB's 16 MiB BSON limit.
  - `package-security-scans` — Security state and findings per revision (keyed by `{packageName}:{revisionId}`).
    Indexed on `(status, leaseUntil)` for the scanner work queue and `(packageName, isHead)` for head lookups.
  - `seed-exclusions` — Records pkgbases whose revision content exceeds the 16 MiB document limit, preventing endless
    retries during seed and refresh.
  - `aur-metadata` — Raw AUR metadata dump snapshots.
- **In-memory index (cache):** `PackageIndexStore` maintains an immutable snapshot of `ByNames`, `ByWords`, and
  `ByProvides` lookup tables. Rebuilt on startup from MongoDB and swapped atomically on metadata refresh.
- **On-disk Git repos (cache):** Bare repositories under `data/repos/` (configurable via `Atoll:Git:RepositoriesPath`),
  lazily materialized from `package-revisions`. Re-materialized whenever the head revision or security status changes.
- **Limits & containment:**
  - `MaxRevisions` (default 10) caps retained revision history per package.
  - `MaxFileBytes` (default 5 MB) rejects oversized files at seed time.
  - 16 MiB BSON limit applies per revision document. Oversized packages are recorded in `seed-exclusions`.
  - Disk growth for bare repos is unbounded (~116k bare repos if full AUR is seeded). Use `Seed:Mode=Off` or monitor
    storage/inodes.

## API

- **Base URLs:** `http://localhost:8080` (container), `http://localhost:5290` (dev)
- **Style:** REST, JSON · **Authentication:** none (open read/write; trusted networks only) · **Versioning:** none ·
  **Error format:** RFC 9457 `ProblemDetails`
- **OpenAPI:** `/openapi/v1.json` in Development mode

### Search parameters

`/search` accepts two query parameters:

- `query` - a comma-separated list of values (e.g. `?query=linux,zen`). An omitted or empty query deliberately returns
  `200` with no results rather than `400`.
- `by` - `name` (exact match), `words` (token intersection over Name/Description/Keywords, ordered by votes, capped at
  50), or `provides` (currently no cap or defined ordering - asymmetry worth resolving). Defaults to `name`.

### Endpoints

| Method | Path | Description |
| --- | --- | --- |
| GET/HEAD | `/health` | Liveness only - does not check MongoDB or index readiness |
| GET | `/metrics` | OpenTelemetry metrics in Prometheus format (see Operations) |
| GET | `/search?query=…&by=name\|words\|provides` | In-memory package search (comma-separated values) |
| GET | `/packages` | List all seeded package names |
| POST | `/packages/{name}/seed` | Clone from AUR and persist (409 if exists). `403` when `Atoll:Mutations:Enabled=false` |
| GET | `/packages/{name}` | Get head revision files |
| GET | `/packages/{name}/versions` | Get revision history |
| GET | `/packages/{name}/versions/{sha}` | Get specific revision files |
| DELETE | `/packages/{name}` | Delete package data, security scans, and materialized Git repo. `403` when `Atoll:Mutations:Enabled=false` |
| GET | `/packages/{name}/security` | Get per-revision security status (`?revision={sha}` for one revision) |
| POST | `/packages/{name}/security/rescan` | Mark a revision for re-scan (`?revision={sha}`, defaults to head). `403` when `Atoll:Mutations:Enabled=false` |
| GET | `/packages/{name}.git/info/refs?service=git-upload-pack` | Git ref advertisement |
| POST | `/packages/{name}.git/git-upload-pack` | Git pack negotiation and transfer |

### Web UI routes

| Path | Render Mode | Description |
| --- | --- | --- |
| `/` | Interactive Server | Package catalog search, live filtering (all/seeded/unseeded), and sorting |
| `/package/{name}` | Static SSR | Package details, metadata, relationships, clone block, security banner |
| `/package/{name}/files` | Static SSR | PKGBUILD and source file viewer across revisions |
| `/package/{name}/revisions` | Static SSR | Revision history list and static security analysis findings |
| `/status` | Static SSR | Operational dashboard: index sync, workers, security scans, exclusions |

`DELETE` removes the package and revision documents, security scan records, and the materialized bare Git repository.
`GitTransferService` also verifies that the MongoDB package still exists before serving an on-disk repository.

### Security scanning

Seeded AUR content is untrusted. Atoll uses deterministic static analysis and a durable per-revision scan state to gate
content and Git access; search, package lists, history, and security status remain available as metadata. New and
refreshed revisions are blocked until they verify. The full threat model, scan rules, worker pipeline, access decisions,
configuration, and limitations are documented in [Package security scanning](SECURITY.md).

## Key Decisions (ADRs)

| Decision | Rationale | Trade-offs | Status |
| --- | --- | --- | --- |
| In-memory search index (no Elasticsearch) | Fast reads (< 10 ms); full AUR metadata fits easily in RAM (~100 MB). | Must rebuild on restart; no fuzzy scoring. | Active |
| Normalized `package-revisions` storage | Avoids 16 MiB document growth as revisions accumulate; keeps `packages` documents lean. | Content reads take an extra indexed query; two-document writes on append without distributed transactions. | Active |
| Subprocess execution for `git upload-pack` | Reuses complete and standard Git smart HTTP protocol implementation. | Requires `git` binary in container; small process-spawn overhead per Git fetch. | Active |
| Atomic snapshot swap in `PackageIndexStore` | Lock-free, zero-contention reads; consistent view per query. | Full index rebuild on refresh; temporary 2× peak memory during rebuild. | Active |
| Cached sorted views in `PackageCatalogService` | Fast UI pagination over 100k+ packages. Each `(generation, sort)` is pre-sorted once into an array reference. | First request per sort pays O(N log N). Substring queries still scan linearly (~15–25 ms). | Active |
| Response compression (Brotli + Gzip) | Reduces dynamic SSR and API payload sizes ~5× without external infrastructure. | Minor CPU overhead (mitigated by `Fastest` level). Disabled over HTTPS by default to prevent BREACH attacks. | Active |
| Open endpoints / Trusted network model | Keeps the API and Git clone surface simple and standard for self-hosted instances. | Anyone on the network can mutate data unless `Atoll:Mutations:Enabled=false` is set. | Active |

Security notes not covered by the ADRs: options are validated on startup via Data Annotations (`[Required]`, `[Range]`,
`[Url]`); raw stack traces are never returned to clients; `git-receive-pack` (push) is explicitly rejected with
`403 Forbidden`.

## Operations

- **Deployment:** `Atoll.Api/Dockerfile` builds the image (installs the `git` CLI); `compose.yaml` orchestrates API +
  MongoDB 8 with a health check on Mongo. Named volumes `atoll-data` (Git repos) and `mongo-data` persist state.
  Single-instance only (see Principles).
- **Configuration:** `appsettings.json` + Data Annotation validation for local dev; 12-factor environment variables for
  containers (see `compose.yaml` for an example).
- **Logging:** ASP.NET Core structured console logging; workers log seeding progress, refresh status, and errors.
  `Activity.Current?.Id` is captured in error logs for correlation.
- **Metrics:** `GET /metrics` serves OpenTelemetry metrics in Prometheus exposition format. Custom `atoll_*`
  instruments cover process uptime, search request count, index sizes (by names / provides / words), AUR refresh
  statistics (attempts, successes, failures, last timestamps), bulk-seed statistics, security-scan statistics
  (throughput, outcomes, backlog depth), and package-refresh statistics; ASP.NET Core request metrics, outbound
  HTTP client metrics (AUR / mirror fetches via `OpenTelemetry.Instrumentation.Http`), and built-in .NET runtime
  metrics are included. Alerting is not configured; intended for the infrastructure layer.
- **Telemetry export:** Metrics and application logs are also exported over OTLP via `UseOtlpExporter()`,
  configured through `OTEL_EXPORTER_OTLP_*` environment variables. `compose.yaml` bundles the
  `grafana/otel-lgtm` development stack (OTel Collector, Prometheus, Loki, Tempo, Grafana) with a provisioned
  Atoll dashboard (`observability/grafana/`).
- **Health:** `/health` is liveness only. There is no readiness signal - the search index may be empty on first requests
  after a cold start, and `/health` does not verify MongoDB connectivity.

## Follow-up work

- Add direct-AUR fallback and periodic full verification for pkgbases unavailable from the mirror.
- Expand scanner coverage, add source-host policy and manual overrides, and prevent Git from exposing flagged historical
  revisions.
- Evaluate content-addressed deduplication or GridFS/chunked storage for revision snapshots larger than MongoDB's 16 MiB
  document limit.
- `PackageCatalogService` substring queries and seeded/security filters still scan the full sorted view on every call
  (~15–25 ms at ~85k packages, even for a single-hit query) - a real substring/prefix index would be needed to remove
  this floor. Empty-query default views bypass the scan via the per-sort cached sorted views (2026-08-23; replaced the
  earlier top-K heap).

## References

- AUR package metadata dump: `https://aur.archlinux.org/packages-meta-ext-v1.json.gz`
- AUR RPC interface: `https://aur.archlinux.org/rpc`
- Git Smart HTTP protocol: `https://git-scm.com/docs/http-protocol`
- Local setup and quickstart: see [README](../README.md)
- Development setup and local tooling: see [DEVELOPMENT.md](DEVELOPMENT.md)
- Package seeding and refresh: see [SYNC.md](SYNC.md)
- Package security scanning: see [SECURITY.md](SECURITY.md)
- Deployment guide: see [DEPLOYMENT.md](DEPLOYMENT.md)
