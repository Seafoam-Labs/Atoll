# Architecture Overview

Atoll is a self-hosted Arch Linux AUR mirror API. It downloads AUR package metadata, stores package files with revision
history, provides fast in-memory search, and exposes each package as a cloneable Git repository over HTTP.

## Context & Goals

- **Problem:** Provide a private, searchable AUR package registry with version history and Git-compatible read access to
  PKGBUILD files.
- **Scope:** Mirrors AUR metadata and package files read-only _from AUR's perspective_ - Atoll never writes upstream.
  Locally it exposes unauthenticated seed and delete endpoints, so it must not be reachable by untrusted clients. Git
  push (`git-receive-pack`) and authentication are currently out of scope.
- **Success criteria:** Search queries served from memory in < 10 ms end-to-end; metadata index stays in sync with AUR
  within the configured refresh interval (default 10 minutes). Seeded content is updated only when the optional
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
  [ASP.NET Core Minimal API / Blazor]  (:8080 container / :5290 dev)
           │
     ┌─────┼──────────────────────────┬──────────────────────────┐
     │     │                          │                          │
     ▼     ▼                          ▼                          ▼
[PackageSearchService]        [IPackageService /         [PackageDetailsService /
(in-memory index)              IGitTransferService]       StatusDashboardService]
     │                        (MongoDB; git subprocess)  (UI view models)
     ▼                                │                          │
[PackageIndexStore]                   ▼                          │
     ▲                            [MongoDB]                      │
     │ rebuild on refresh         (packages + aur-metadata       │
[PackageIndexWorker (bg)]          collections) ─────────────────┘
     │ fetches metadata every N minutes
     ▼
[AUR packages-meta-ext-v1.json.gz]

[DirectSeedWorker (bg)]
     │  clones missing packages from aur.archlinux.org, delay between seeds
     ▼
[MongoDB packages collection]
```

- **PackageIndexWorker** periodically downloads the AUR dump, persists it to MongoDB, and atomically swaps the in-memory
  `PackageIndexStore` snapshot. On startup it first rebuilds the index from the cached MongoDB metadata (if any) so
  search is available before the first download.
- **Seed worker:** `Seed:Mode` selects the direct, bulk, or disabled automated seeding strategy. Direct and bulk are
  mutually exclusive; manual seeding remains available when automated seeding is off.
- **PackageRefreshWorker:** the opt-in refresh worker updates already-seeded packages from upstream and appends a
  revision only when its content changes. It shares bulk seeding's mirror cache when both are enabled.
  Seeding and refresh mechanics, configuration, cache lifecycle, metrics, and operational verification are documented
  in [Package seeding and refresh](SYNC.md).
- **PackageSearchService** serves all search queries from the immutable in-memory snapshot with no database round-trips.
- **Blazor Web UI:** Razor components under `Components/` provide a rich web UI. `PackageCatalogService` powers the
  interactive package catalog with top-K max-heap selection and snapshot caching; `PackageDetailsService` assembles
  package metadata, relations, file views, and security verdicts; `StatusDashboardService` consolidates worker status,
  sync metrics, and exclusions for the status dashboard.
- **GitTransferService** shells out to `git upload-pack` to serve clone/fetch requests. Bare repositories under
  `data/repos/` are materialized lazily from MongoDB documents by `MongoPackageService.EnsureGitRepositoryAsync`
  (commits are synthesized from stored revisions; a `.atoll-head` marker tracks the last materialized revision). Because
  commits are synthesized rather than imported, **the SHAs served over Git do not match upstream AUR commit SHAs** - the
  SHAs returned by `/packages/{name}/versions` are the authoritative namespace.
- **Package name semantics:** `{name}` in all `/packages/{name}` routes is the AUR **pkgname**. Seeding resolves the
  **pkgbase** from the in-memory AUR metadata index (`MongoPackageService.ResolvePackageBase`) before cloning
  `aur.archlinux.org/{pkgbase}.git`, since AUR Git URLs are keyed by pkgbase - not pkgname. For split packages where
  `pkgname != pkgbase` this mapping is required; for non-split packages or when the index is unavailable (cold start,
  stale snapshot) it falls back to the pkgname, which is correct for non-split packages. Bulk fetching applies the same
  pkgname → pkgbase mapping before matching mirror branches.

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

- **MongoDB (authoritative):** `packages` stores metadata-only package documents: revision **metadata** (id,
  timestamp, author, message) stays embedded, but revision file content lives in `package-revisions` — one document
  per retained revision, keyed by `{packageName}:{revisionId}` (same composite-string convention as
  `package-security-scans`) and indexed on `packageName` for cascade deletes. The head is served by reading its
  revision document via `headRevisionId`; history length no longer affects package-document size. `packages` also
  carries the refresh sync watermarks (`upstreamPackageBase`, `lastSyncedUpstreamHead`, `lastSyncAttemptAt`,
  `lastSyncSucceededAt`, `lastSyncError`); `aur-metadata` stores the full AUR package dump as typed documents;
  `seed-exclusions` records pkgbases whose revision snapshot cannot fit in a MongoDB document (consulted by both
  seeding and refresh); `package-security-scans` stores the security state and latest findings for each retained
  package revision (keyed by `{packageName}:{revisionId}`; compound-indexed on `(status, leaseUntil)` for the scan
  work queue and on `(packageName, isHead)` for head lookups). The `packages` collection is indexed on `packageName`
  at startup so the head/exists/history/revision/delete lookups (which filter on `packageName`, not `_id`) are
  index-served; the index is non-unique to tolerate pre-existing data even though `packageName` is effectively unique
  in production. The security-state schema and lifecycle are documented in [Package security scanning](SECURITY.md).
- **In-memory index (cache):** `PackageIndexStore` - immutable snapshot of `ByNames`, `ByWords`, `ByProvides`
  dictionaries; rebuilt from MongoDB on startup and after each AUR refresh.
- **On-disk Git repos (cache):** bare repositories under `data/repos/` (configurable via `Atoll:Git:RepositoriesPath`),
  one per seeded package, materialized lazily from MongoDB.
- **Limits:** `MaxRevisions` (default 10) caps retained history per package; revision file content is stored in
  `package-revisions` (one document per revision), so history length no longer affects package-document size and the
  16 MiB BSON limit applies per revision snapshot instead. `MaxFileBytes` (default 5 MB) rejects oversized individual
  files at seed time. Every revision snapshot is BSON-size checked before insertion against MongoDB's fixed 16 MiB
  limit. Oversized snapshots are recorded as pkgbase exclusions (consulted by both seeding and refresh) instead of
  being retried indefinitely. This is a containment measure, not a way to store large packages.
- **Disk growth:** unbounded. Seeding all ~116k AUR packages lazily materializes up to ~116k bare repos on disk with no
  eviction, TTL, or sizing guidance (inode pressure is a real concern at that count). Automated seeding is on by
  default; use `Seed:Mode=Off` when that behavior is not desired and plan capacity accordingly.

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
| POST | `/packages/{name}/seed` | Clone from AUR and persist (409 if exists) |
| GET | `/packages/{name}` | Get head revision files |
| GET | `/packages/{name}/versions` | Get revision history |
| GET | `/packages/{name}/versions/{sha}` | Get specific revision files |
| DELETE | `/packages/{name}` | Delete package (MongoDB document only - see note below) |
| GET | `/packages/{name}/security` | Get per-revision security status (`?revision={sha}` for one revision) |
| POST | `/packages/{name}/security/rescan` | Mark a revision for re-scan (`?revision={sha}`, defaults to head) |
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

> **Known issue:** `DELETE` removes the MongoDB document but leaves the bare repo on disk, so `git clone` keeps serving
> the deleted package's content indefinitely. Fix by either deleting the directory or having `GitTransferService` verify
> the MongoDB document exists first.

### Security scanning

Seeded AUR content is untrusted. Atoll uses deterministic static analysis and a durable per-revision scan state to gate
content and Git access; search, package lists, history, and security status remain available as metadata. New and
refreshed revisions are blocked until they verify. The full threat model, scan rules, worker pipeline, access decisions,
configuration, and limitations are documented in [Package security scanning](SECURITY.md).

## Key Decisions (ADRs)

| Decision | Rationale | Trade-offs | Status |
| --- | --- | --- | --- |
| In-memory search index (no Elasticsearch) | Fast reads; AUR metadata fits comfortably in RAM | Index must be rebuilt on restart; no ranked full-text scoring | Active |
| MongoDB for package storage | Flexible schema; embedded revision metadata avoids joins | Revision file content is normalized into `package-revisions`, so the 16 MiB cap applies per snapshot; content reads take an extra indexed find; appends write two documents without transactions (write ordering keeps readers consistent) | Active |
| Shell out to `git upload-pack` | Reuses the complete and reliable Git transfer implementation | Requires `git` installed in the container; subprocess overhead per request | Active |
| Atomic `PackageIndexStore` snapshot swap | Lock-free reads; consistent view per request | Full index rebuild on each refresh; 2× peak memory while both snapshots are live. Incremental updates evaluated and rejected: rebuild cost is negligible at ~116k packages per 10-minute cycle. | Active |
| `PackageCatalogService` top-K selection + default-view cache | The Blazor catalog page (`/`) enumerates all ~85k indexed packages per request; a bounded max-heap keeps only the best `RenderCap` (500) rows instead of fully sorting and discarding the rest, and the common empty-query/no-filter view is cached per sort (keyed by index/snapshot instance, so it self-invalidates on refresh or `InvalidateSnapshot`) | Cuts the default page load from ~85 ms / 7.6 MB to a cache hit; does not remove the ~20–25 ms floor from enumerating and substring-scanning the full index on every call (cold/filtered queries) | Active |
| No authentication | Keeps the API simple for trusted private deployments | Unauthenticated callers can seed **and delete** packages; must sit behind a reverse proxy / firewall if exposed | Active |

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
  document limit;
- `PackageCatalogService` still enumerates and substring-scans the entire in-memory index on every uncached call
  (~20–25 ms at ~85k packages, even for a single-hit query) - a real substring/prefix index would be needed to
  remove this floor. See `tailwind.md` (2026-08-22 status note) for the measurement harness and what was already
  fixed (top-K selection, default-view cache).

## References

- AUR package metadata dump: `https://aur.archlinux.org/packages-meta-ext-v1.json.gz`
- AUR RPC interface: `https://aur.archlinux.org/rpc`
- Git Smart HTTP protocol: `https://git-scm.com/docs/http-protocol`
- Local setup and quickstart: see `README.md`
- Package seeding and refresh: see `docs/SYNC.md`
