# Architecture Overview

Atoll is a self-hosted Arch Linux AUR mirror API. It downloads AUR package metadata, stores package files with revision history, provides fast in-memory search, and exposes each package as a cloneable Git repository over HTTP.

## Context & Goals

- **Problem:** Provide a private, searchable AUR package registry with version history and Git-compatible read access to PKGBUILD files.
- **Scope:** Mirrors AUR metadata and package files read-only _from AUR's perspective_ - Atoll never writes upstream. Locally it exposes unauthenticated seed and delete endpoints, so it must not be reachable by untrusted clients. Git push (`git-receive-pack`) and authentication are currently out of scope.
- **Success criteria:** Search queries served from memory in < 10 ms end-to-end; metadata index stays in sync with AUR within the configured refresh interval (default 10 minutes). Note: sync applies to _metadata_ only - seeded package files are frozen at seed time until periodic re-sync (TODO #1) lands.

## Architecture Principles

- **MongoDB is the only authoritative state** - the in-memory index and the bare repos under `data/repos/` are both rebuildable caches. The index is rebuilt from MongoDB on startup. Note: the index and seed workers assume a **single instance**; running multiple replicas duplicates AUR fetches and races on seeding until a distributed lock is added.
- **Domain logic lives in services, not endpoints** - `Endpoints.cs` only routes and maps; all business rules live in `Services/`.

## Tech Stack

| Layer            | Technology                                           | Rationale                                                                 |
| ---------------- | ---------------------------------------------------- | ------------------------------------------------------------------------- |
| Runtime          | .NET 10                                              | Current LTS runtime with built-in async primitives                        |
| Framework        | ASP.NET Core Minimal API                             | Low-overhead routing; no controller boilerplate needed                    |
| Database         | MongoDB 8 (via MongoDB.Driver)                       | Flexible document model suits package files + embedded revision history   |
| In-memory index  | ImmutableDictionary (ByNames / ByWords / ByProvides) | Fast reads with a consistent per-request snapshot, no external cache tier |
| Git subprocess   | CliWrap + system `git`                               | Reuses the `git upload-pack` implementation                               |
| Containerization | Docker / Docker Compose                              | Single `compose.yaml` spins up API + MongoDB                              |
| Cloud infra      | Terraform (`terraform/`)                             | Cloud infrastructure definitions                                          |

## Architecture

```txt
[Git client / HTTP client]
           │
           ▼
  [ASP.NET Core Minimal API]  (:8080 container / :5290 dev)
           │
     ┌─────┴──────────────────────────┐
     │                                │
     ▼                                ▼
[PackageSearchService]        [IPackageService / IGitTransferService]
(in-memory index)             (MongoDB-backed; git subprocess)
     │                                │
     ▼                                ▼
[PackageIndexStore]           [MongoDB]
     ▲                        (packages + aur-metadata collections)
     │ rebuild on refresh
[PackageIndexWorker (background)]
     │ fetches metadata every N minutes
     ▼
[AUR packages-meta-ext-v1.json.gz]

[DirectSeedWorker (background)]
     │  clones missing packages from aur.archlinux.org, delay between seeds
     ▼
[MongoDB packages collection]
```

- **PackageIndexWorker** periodically downloads the AUR dump, persists it to MongoDB, and atomically swaps the in-memory `PackageIndexStore` snapshot. On startup it first rebuilds the index from the cached MongoDB metadata (if any) so search is available before the first download.
- **DirectSeedWorker** continuously polls the index for packages not yet in MongoDB and clones them, pausing `Seed:Direct:SeedDelayMs` between packages (default 1 s; 10 s in the container image) and backing off when the index is empty or fully seeded. This is the default `Seed:Mode=Direct` seeder.
- **PackageBulkSeedWorker** (`Seed:Mode=Bulk`) is the mutually-exclusive alternative. It batch-fetches pkgbase branches from the read-only GitHub AUR mirror and seeds their files through the existing `SeedFilesAsync` path. See [Bulk Seeding](SEED.md).
- **No seed worker** (`Seed:Mode=Off`) disables automated package seeding while the metadata index worker and manual `POST /packages/{name}/seed` requests remain available.
- **PackageSearchService** serves all search queries from the immutable in-memory snapshot with no database round-trips.
- **GitTransferService** shells out to `git upload-pack` to serve clone/fetch requests. Bare repositories under `data/repos/` are materialized lazily from MongoDB documents by `MongoPackageService.EnsureGitRepositoryAsync` (commits are synthesized from stored revisions; a `.atoll-head` marker tracks the last materialized revision). Because commits are synthesized rather than imported, **the SHAs served over Git do not match upstream AUR commit SHAs** - the SHAs returned by `/packages/{name}/versions` are the authoritative namespace.
- **Package name semantics:** `{name}` in all `/packages/{name}` routes is the AUR **pkgname**. Seeding resolves the **pkgbase** from the in-memory AUR metadata index (`MongoPackageService.ResolvePackageBase`) before cloning `aur.archlinux.org/{pkgbase}.git`, since AUR Git URLs are keyed by pkgbase - not pkgname. For split packages where `pkgname != pkgbase` this mapping is required; for non-split packages or when the index is unavailable (cold start, stale snapshot) it falls back to the pkgname, which is correct for non-split packages. Bulk fetching applies the same pkgname → pkgbase mapping before matching mirror branches.

Request paths of note (everything else is standard Minimal API routing with `GlobalExceptionHandler` converting unhandled exceptions to RFC 9457 `ProblemDetails`):

- **Search:** `PackageSearchService` reads the current immutable `PackageIndexStore` snapshot and returns results with no I/O.
- **Package CRUD:** `MongoPackageService` delegates to `MongoPackageRepository`; seeding clones the AUR Git repo to a temp directory, reads the files, and persists them to MongoDB.
- **Git Smart HTTP:** `GitTransferService` ensures the on-disk bare repository exists and is current (materializing from MongoDB on first use or after a new revision), then pipes stdin/stdout to `git upload-pack`.

## Bulk Seeding

Bulk seeding is the opt-in, mutually exclusive alternative to direct AUR cloning (`Atoll:Seed:Mode=Bulk`). It groups missing **pkgnames** by **pkgbase**, fetches each mirror branch once, and fans the extracted files back out through the normal `SeedFilesAsync` path. This preserves split-package semantics while reducing network requests.

The Git transport contract, plain-Git verification steps, failure recovery, configuration, metrics, and cache lifecycle are documented in [Bulk package seeding](SEED.md).

## State & Storage

- **MongoDB (authoritative):** `packages` stores package documents with embedded files and revision history; `aur-metadata` stores the full AUR package dump as typed documents; `seed-exclusions` records pkgbases that cannot currently fit in a MongoDB document; `package-security-scans` stores the current security state and latest findings for each package (keyed by package name; compound-indexed on `(status, leaseUntil)` for the scan work queue). The `packages` collection is indexed on `packageName` at startup so the head/exists/history/revision/delete lookups (which filter on `packageName`, not `_id`) are index-served; the index is non-unique to tolerate pre-existing data even though `packageName` is effectively unique in production. The security-state schema and lifecycle are documented in [Package security scanning](SECURITY.md).
- **In-memory index (cache):** `PackageIndexStore` - immutable snapshot of `ByNames`, `ByWords`, `ByProvides` dictionaries; rebuilt from MongoDB on startup and after each AUR refresh.
- **On-disk Git repos (cache):** bare repositories under `data/repos/` (configurable via `Atoll:Git:RepositoriesPath`), one per seeded package, materialized lazily from MongoDB.
- **Limits:** `MaxRevisions` (default 10) caps embedded history per package; `MaxFileBytes` (default 5 MB) rejects oversized individual files at seed time. Every initial package document is BSON-size checked before insertion against MongoDB's fixed 16 MiB limit. Oversized bulk-seed documents are recorded as pkgbase exclusions instead of being retried indefinitely. This is a containment measure, not a way to store large packages.
- **Disk growth:** unbounded. Seeding all ~116k AUR packages lazily materializes up to ~116k bare repos on disk with no eviction, TTL, or sizing guidance (inode pressure is a real concern at that count). Automated seeding is on by default; use `Seed:Mode=Off` when that behavior is not desired and plan capacity accordingly.

## API

- **Base URLs:** `http://localhost:8080` (container), `http://localhost:5290` (dev)
- **Style:** REST, JSON · **Authentication:** none (open read/write; trusted networks only) · **Versioning:** none · **Error format:** RFC 9457 `ProblemDetails`
- **OpenAPI:** `/openapi/v1.json` in Development mode

### Search parameters

`/search` accepts two query parameters:

- `query` - a comma-separated list of values (e.g. `?query=linux,zen`). An omitted or empty query deliberately returns `200` with no results rather than `400`.
- `by` - `name` (exact match), `words` (token intersection over Name/Description/Keywords, ordered by votes, capped at 50), or `provides` (currently no cap or defined ordering - asymmetry worth resolving). Defaults to `name`.

### Endpoints

| Method   | Path                                       | Description                                               |
| -------- | ------------------------------------------ | --------------------------------------------------------- |
| GET/HEAD | `/health`                                  | Liveness only - does not check MongoDB or index readiness |
| GET      | `/metrics`                                 | Service metrics (see Operations)                          |
| GET      | `/search?query=…&by=name\|words\|provides` | In-memory package search (comma-separated values)         |
| GET      | `/packages`                                | List all seeded package names                             |
| POST     | `/packages/{name}/seed`                    | Clone from AUR and persist (409 if exists)                |
| GET      | `/packages/{name}`                         | Get head revision files                                   |
| GET      | `/packages/{name}/versions`                | Get revision history                                      |
| GET      | `/packages/{name}/versions/{sha}`          | Get specific revision files                               |
| DELETE   | `/packages/{name}`                         | Delete package (MongoDB document only - see note below)   |
| GET      | `/packages/{name}/security`                | Get effective security status + latest scan summary       |
| POST | `/packages/{name}/security/rescan` | Mark the head revision for re-scan |
| GET | `/packages/{name}.git/info/refs?service=git-upload-pack` | Git ref advertisement |
| POST | `/packages/{name}.git/git-upload-pack` | Git pack negotiation and transfer |

> **Known issue:** `DELETE` removes the MongoDB document but leaves the bare repo on disk, so `git clone` keeps serving the deleted package's content indefinitely. Fix by either deleting the directory or having `GitTransferService` verify the MongoDB document exists first.

### Security scanning

Seeded AUR content is user-submitted and may execute arbitrary shell at build/install time, so Atoll runs deterministic static analysis on stored files and gates read access to package content and Git on a per-package security status. Search and the package list remain ungated (they serve public AUR metadata).

Each package has one security-state document in `package-security-scans` (`Pending` / `Verified` / `Flagged` / `Error`), keyed by package name and tied to its current revision. The persisted `Pending` state is the durable work queue: `PackageSecurityWorker` runs `ScannerConcurrency` poll loops, atomically leases a pending document (`leaseUntil`/`leaseOwner`, 5-minute lease), scans the head revision, and writes the result back guarded by `(id, revisionId, leaseOwner)`. A new seed or rescan re-marks the state `Pending`; expired leases are reclaimable after a restart.

`PkgBuildSecurityScanner` matches a rule set against the `PKGBUILD` and script-like companion files, after de-obfuscating each line (quote-splitting and backslash escapes) and detecting hidden/invisible characters used for homograph spoofing. Rules that match only after de-obfuscation are escalated to `Critical`. `Critical` and `High` findings flag a package; `Medium` findings are retained without blocking.

`PackageSecurityAccess.CheckAsync` is enforced in `Endpoints.cs` on `GET /packages/{name}`, `GET /packages/{name}/versions/{sha}`, and both Git Smart HTTP routes. Blocked requests return `403 Forbidden` with an RFC 9457 problem-details body carrying a non-sensitive `reason` code (`security_status_pending` / `security_status_flagged` / `security_scan_error`). Version history (`/versions`) and the status endpoint (`/packages/{name}/security`) are not gated — they expose metadata and the scan summary, not content. When security is disabled (`Atoll:Security:Enabled=false`) everything is served regardless of status.

The threat model, full rule table, pipeline internals, decision table, configuration (`Enabled` / `ScannerConcurrency` / `PollIntervalMs`), manual verification steps, and known limitations are documented in [Package security scanning](SECURITY.md).

## Key Decisions (ADRs)

| Decision                                  | Rationale                                                    | Trade-offs                                                                                                             | Status                                                |
| ----------------------------------------- | ------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------- |
| In-memory search index (no Elasticsearch) | Fast reads; AUR metadata fits comfortably in RAM             | Index must be rebuilt on restart; no ranked full-text scoring                                                          | Active                                                |
| MongoDB for package storage               | Flexible schema; embedded revisions avoid joins              | 16 MB document cap requires conservative revision and file-size limits (currently unenforced per-document - see above) | Active                                                |
| Shell out to `git upload-pack`            | Reuses the complete and reliable Git transfer implementation | Requires `git` installed in the container; subprocess overhead per request                                             | Active                                                |
| Atomic `PackageIndexStore` snapshot swap  | Lock-free reads; consistent view per request                 | Full index rebuild on each refresh; 2× peak memory while both snapshots are live                                       | Active - full-rebuild trade-off superseded by TODO #3 |
| No authentication                         | Keeps the API simple for trusted private deployments         | Unauthenticated callers can seed **and delete** packages; must sit behind a reverse proxy / firewall if exposed        | Active                                                |

Security notes not covered by the ADRs: options are validated on startup via Data Annotations (`[Required]`, `[Range]`, `[Url]`); raw stack traces are never returned to clients; `git-receive-pack` (push) is explicitly rejected with `403 Forbidden`.

## Operations

- **Deployment:** `Atoll.Api/Dockerfile` builds the image (installs the `git` CLI); `compose.yaml` orchestrates API + MongoDB 8 with a health check on Mongo. Named volumes `atoll-data` (Git repos) and `mongo-data` persist state. Single-instance only (see Principles).
- **Configuration:** `appsettings.json` + Data Annotation validation for local dev; 12-factor environment variables for containers (see `compose.yaml` for an example).
- **Logging:** ASP.NET Core structured console logging; workers log seeding progress, refresh status, and errors. `Activity.Current?.Id` is captured in error logs for correlation.
- **Metrics:** `GET /metrics` returns uptime, total search request count, index sizes (ByNames / ByWords / ByProvides), and AUR refresh statistics (attempts, successes, failures, last timestamps). Alerting is not configured; intended for the infrastructure layer.
- **Health:** `/health` is liveness only. There is no readiness signal - the search index may be empty on first requests after a cold start, and `/health` does not verify MongoDB connectivity.

## Follow-up TODOs

### 1. Periodic refresh and sync of packages from AUR upstream

Continuously re-sync seeded packages so the latest version is always available, instead of seeding once and freezing. Open questions that need research before implementation:

- **Change detection** - cheap ways to discover updated packages (e.g. `git ls-remote` on the GitHub mirror vs. stored head SHAs, AUR RPC `info` calls, or the `LastModified` field in the metadata dump).
- **Sync rate** - how often to poll without abusing upstream rate limits; likely tiered (popular/recently-updated packages more frequently).
- **Update path** - integrate with the bulk-fetch worker so refreshes are also batched; append new revisions to the embedded history respecting `MaxRevisions`, and refresh materialized bare repos when the head changes.

### 2. Security scanning of PKGBUILD and package scripts

**Implemented** (see [Security scanning](#security-scanning) above and [Package security scanning](SECURITY.md)). Remaining follow-ups: richer rule coverage, source-host allow/deny lists, manual override state (`ForceVerified` / `ForceBlocked`), and security metrics under `/metrics`.

### 3. Incremental index updates

Full-rebuild-on-refresh costs redundant CPU and doubles peak memory while both snapshots are live. Replace it with a diff-based update that touches only changed entries - e.g. `ImmutableDictionary.Builder` plus one atomic swap, which preserves the per-request consistent view from the snapshot ADR. (A `ConcurrentDictionary` is an alternative - `ImmutableDictionary` already provides lock-free concurrent reads, so the real gain would be incremental mutation - but it gives up cross-map snapshot consistency and needs its own concurrency story for the `ByWords` collection values.) Pairs with TODO #1.

### 4. Normalize package revision content

`PackageDocument` currently embeds identical file content in both the head and its initial revision, and embeds every retained revision in the same MongoDB document. MongoDB's 16 MiB BSON limit therefore remains a structural storage constraint; the bulk-seed exclusion mechanism only prevents futile retries.

Move revision file content to a separate collection keyed by `(packageName, revisionId)` and retain only package and revision metadata plus `HeadRevisionId` in `packages`. The head should reference its revision rather than duplicate its file map. Store files that can individually exceed a practical BSON-document budget in GridFS or chunked file documents. This migration must update package reads, history reads, Git materialization, deletion, and the refresh/append-revision path atomically enough to avoid serving a revision whose content is absent.

## References

- AUR package metadata dump: `https://aur.archlinux.org/packages-meta-ext-v1.json.gz`
- AUR RPC interface: `https://aur.archlinux.org/rpc`
- Git Smart HTTP protocol: `https://git-scm.com/docs/http-protocol`
- Local setup and quickstart: see `README.md`
