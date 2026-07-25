# Architecture Overview

Atoll is a self-hosted Arch Linux AUR mirror API. It downloads AUR package metadata, stores package files with revision history, provides fast in-memory search, and exposes each package as a cloneable Git repository over HTTP.

## Context & Goals

- **Problem:** Provide a private, searchable AUR package registry with version history and Git-compatible read access to PKGBUILD files.
- **Scope:** Mirrors AUR metadata and package files read-only _from AUR's perspective_ - Atoll never writes upstream. Locally it exposes unauthenticated seed and delete endpoints, so it must not be reachable by untrusted clients. Git push (`git-receive-pack`) and authentication are currently out of scope.
- **Success criteria:** Search queries served from memory in < 10 ms end-to-end; metadata index stays in sync with AUR within the configured refresh interval (default 10 minutes). Note: sync applies to _metadata_ only - seeded package files are frozen at seed time until periodic re-sync (TODO #2) lands.

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

[PackageSeedWorker (background)]
     │  clones missing packages from aur.archlinux.org, delay between seeds
     ▼
[MongoDB packages collection]
```

- **PackageIndexWorker** periodically downloads the AUR dump, persists it to MongoDB, and atomically swaps the in-memory `PackageIndexStore` snapshot. On startup it first rebuilds the index from the cached MongoDB metadata (if any) so search is available before the first download.
- **PackageSeedWorker** continuously polls the index for packages not yet in MongoDB and clones them, pausing `SeedDelayMs` between packages (default 1 s; 10 s in the container image) and backing off when the index is empty or fully seeded.
- **PackageSearchService** serves all search queries from the immutable in-memory snapshot with no database round-trips.
- **GitTransferService** shells out to `git upload-pack` to serve clone/fetch requests. Bare repositories under `data/repos/` are materialized lazily from MongoDB documents by `MongoPackageService.EnsureGitRepositoryAsync` (commits are synthesized from stored revisions; a `.atoll-head` marker tracks the last materialized revision). Because commits are synthesized rather than imported, **the SHAs served over Git do not match upstream AUR commit SHAs** - the SHAs returned by `/packages/{name}/versions` are the authoritative namespace.
- **Package name semantics:** `{name}` in all `/packages/{name}` routes is the AUR **pkgname**. Seeding clones `aur.archlinux.org/{name}.git`, which is keyed by **pkgbase** - for split packages where `pkgname != pkgbase` this clone fails. The `PackageBase` field is already captured in the stored AUR metadata, but the pkgname → pkgbase mapping is not yet used by the seed path (known bug; also affects the branch pre-filtering planned in TODO #1, which must map names to pkgbases before matching against `git ls-remote --heads` output).

Request paths of note (everything else is standard Minimal API routing with `GlobalExceptionHandler` converting unhandled exceptions to RFC 9457 `ProblemDetails`):

- **Search:** `PackageSearchService` reads the current immutable `PackageIndexStore` snapshot and returns results with no I/O.
- **Package CRUD:** `MongoPackageService` delegates to `MongoPackageRepository`; seeding clones the AUR Git repo to a temp directory, reads the files, and persists them to MongoDB.
- **Git Smart HTTP:** `GitTransferService` ensures the on-disk bare repository exists and is current (materializing from MongoDB on first use or after a new revision), then pipes stdin/stdout to `git upload-pack`.

## State & Storage

- **MongoDB (authoritative):** `packages` collection stores package documents with embedded files and revision history; `aur-metadata` collection stores the full AUR package dump as typed documents. A unique index on package name is assumed but not yet explicitly created (gap).
- **In-memory index (cache):** `PackageIndexStore` - immutable snapshot of `ByNames`, `ByWords`, `ByProvides` dictionaries; rebuilt from MongoDB on startup and after each AUR refresh.
- **On-disk Git repos (cache):** bare repositories under `data/repos/` (configurable via `Atoll:Git:RepositoriesPath`), one per seeded package, materialized lazily from MongoDB.
- **Limits:** `MaxRevisions` (default 10) caps embedded history per package; `MaxFileBytes` (default 5 MB) rejects oversized individual files at seed time. **Known gap:** these limits are per-file/per-revision, not per-document - 10 revisions × 5 MB files can exceed MongoDB's 16 MB BSON document cap. There is currently no enforced per-document byte budget; an oversized write surfaces as an unhandled MongoDB error → `ProblemDetails` 500. GridFS is the alternative if large files must be supported.
- **Disk growth:** unbounded. Seeding all ~116k AUR packages lazily materializes up to ~116k bare repos on disk with no eviction, TTL, or sizing guidance (inode pressure is a real concern at that count). The seed worker is on by default; plan capacity accordingly.

## API

- **Base URLs:** `http://localhost:8080` (container), `http://localhost:5290` (dev)
- **Style:** REST, JSON · **Authentication:** none (open read/write; trusted networks only) · **Versioning:** none · **Error format:** RFC 9457 `ProblemDetails`
- **OpenAPI:** `/openapi/v1.json` in Development mode

### Search parameters

`/search` accepts two query parameters:

- `query` - a comma-separated list of values (e.g. `?query=linux,zen`). An omitted or empty query deliberately returns `200` with no results rather than `400`.
- `by` - `name` (exact match), `words` (token intersection over Name/Description/Keywords, ordered by votes, capped at 50), or `provides` (currently no cap or defined ordering - asymmetry worth resolving). Defaults to `name`.

### Endpoints

| Method   | Path                                                     | Description                                               |
| -------- | -------------------------------------------------------- | --------------------------------------------------------- |
| GET/HEAD | `/health`                                                | Liveness only - does not check MongoDB or index readiness |
| GET      | `/metrics`                                               | Service metrics (see Operations)                          |
| GET      | `/search?query=…&by=name\|words\|provides`               | In-memory package search (comma-separated values)         |
| GET      | `/packages`                                              | List all seeded package names                             |
| POST     | `/packages/{name}/seed`                                  | Clone from AUR and persist (409 if exists)                |
| GET      | `/packages/{name}`                                       | Get head revision files                                   |
| GET      | `/packages/{name}/versions`                              | Get revision history                                      |
| GET      | `/packages/{name}/versions/{sha}`                        | Get specific revision files                               |
| DELETE   | `/packages/{name}`                                       | Delete package (MongoDB document only - see note below)   |
| GET      | `/packages/{name}.git/info/refs?service=git-upload-pack` | Git ref advertisement                                     |
| POST     | `/packages/{name}.git/git-upload-pack`                   | Git pack negotiation and transfer                         |

> **Known issue:** `DELETE` removes the MongoDB document but leaves the bare repo on disk, so `git clone` keeps serving the deleted package's content indefinitely. Fix by either deleting the directory or having `GitTransferService` verify the MongoDB document exists first.

## Key Decisions (ADRs)

| Decision                                  | Rationale                                                    | Trade-offs                                                                                                             | Status                                                |
| ----------------------------------------- | ------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------- |
| In-memory search index (no Elasticsearch) | Fast reads; AUR metadata fits comfortably in RAM             | Index must be rebuilt on restart; no ranked full-text scoring                                                          | Active                                                |
| MongoDB for package storage               | Flexible schema; embedded revisions avoid joins              | 16 MB document cap requires conservative revision and file-size limits (currently unenforced per-document - see above) | Active                                                |
| Shell out to `git upload-pack`            | Reuses the complete and reliable Git transfer implementation | Requires `git` installed in the container; subprocess overhead per request                                             | Active                                                |
| Atomic `PackageIndexStore` snapshot swap  | Lock-free reads; consistent view per request                 | Full index rebuild on each refresh; 2× peak memory while both snapshots are live                                       | Active - full-rebuild trade-off superseded by TODO #4 |
| No authentication                         | Keeps the API simple for trusted private deployments         | Unauthenticated callers can seed **and delete** packages; must sit behind a reverse proxy / firewall if exposed        | Active                                                |

Security notes not covered by the ADRs: options are validated on startup via Data Annotations (`[Required]`, `[Range]`, `[Url]`); raw stack traces are never returned to clients; `git-receive-pack` (push) is explicitly rejected with `403 Forbidden`.

## Operations

- **Deployment:** `Atoll.Api/Dockerfile` builds the image (installs the `git` CLI); `compose.yaml` orchestrates API + MongoDB 8 with a health check on Mongo. Named volumes `atoll-data` (Git repos) and `mongo-data` persist state. Single-instance only (see Principles).
- **Configuration:** `appsettings.json` + Data Annotation validation for local dev; 12-factor environment variables for containers (see `compose.yaml` for an example).
- **Logging:** ASP.NET Core structured console logging; workers log seeding progress, refresh status, and errors. `Activity.Current?.Id` is captured in error logs for correlation.
- **Metrics:** `GET /metrics` returns uptime, total search request count, index sizes (ByNames / ByWords / ByProvides), and AUR refresh statistics (attempts, successes, failures, last timestamps). Alerting is not configured; intended for the infrastructure layer.
- **Health:** `/health` is liveness only. There is no readiness signal - the search index may be empty on first requests after a cold start, and `/health` does not verify MongoDB connectivity.

## Follow-up TODOs

### 1. Bulk fetching of AUR packages

Replace the current one-by-one seeding (`git clone` per package) with bulk batch fetching from the GitHub AUR mirror (`https://github.com/archlinux/aur`, one branch per **pkgbase** - not per pkgname, so the pkgname → pkgbase mapping must be applied before branch pre-filtering). Verified feasibility findings are in `wip/git-fetch.md`:

- Batch-fetch 500–1,000 branches per `git fetch --depth=1` request (~160 batches, ~10 min at 1–2 s between batches, ~3 GB local cache). Each `git fetch` re-advertises all ~95k refs (~10 MB) unless protocol v2 `ref-prefix` filtering is forced - that's ~1.6 GB of pure ref-advertisement traffic across 160 batches, so the filtering matters.
- Pre-filter requested branches against a cached `git ls-remote --heads` result, since `git fetch` fails atomically if any ref is missing.
- Feed fetched files into the existing `SeedFilesAsync` path; rate-limit per batch instead of per package.
- ETA context: the "~44 h for a full sync" figure for the current seeder assumes `SeedDelayMs = 1000`; the container image ships `SeedDelayMs = 10000`, i.e. ~116,000 × 10 s ≈ **13 days** for a full sync at shipped defaults.
- Caveats: the mirror is marked experimental upstream, may lag behind AUR, and needs a configurable storage path for the local cache.

### 2. Periodic refresh and sync of packages from AUR upstream

Continuously re-sync seeded packages so the latest version is always available, instead of seeding once and freezing. Open questions that need research before implementation:

- **Change detection** - cheap ways to discover updated packages (e.g. `git ls-remote` on the GitHub mirror vs. stored head SHAs, AUR RPC `info` calls, or the `LastModified` field in the metadata dump).
- **Sync rate** - how often to poll without abusing upstream rate limits; likely tiered (popular/recently-updated packages more frequently).
- **Update path** - integrate with the bulk-fetch worker (TODO #1) so refreshes are also batched; append new revisions to the embedded history respecting `MaxRevisions`, and refresh materialized bare repos when the head changes.

### 3. Security scanning of PKGBUILD and package scripts

Scan PKGBUILD files and any accompanying scripts (`*.install`, hooks, etc.) before a package becomes accessible, since AUR content is user-submitted and executes arbitrary shell at build time. Requirements:

- **Verification status** - add a per-package status field (e.g. `Pending` / `Verified` / `Flagged`) persisted in MongoDB on seeded package documents. **Scoping note:** search is served from the AUR metadata dump (all ~116k packages) while verification status applies only to seeded packages (a small subset) - gating search on it would empty the index. The workable split: gate _file and Git access_ on verification status, and leave _metadata search_ ungated (it is public AUR metadata), or carry a status field into the index snapshot (interacts with TODO #4).
- **Scanning rules** - detect dangerous patterns (e.g. `curl | sh`, base64-obfuscated payloads, writes outside `$pkgdir`, unexpected network fetches, suspicious `source` URLs); evaluate existing rule sets (OWASP/CWE-style) before writing custom ones.
- **Pipeline integration** - run scans at seed/refresh time (TODO #2) in the background worker, recording scan results and timestamps alongside each revision.

### 4. Incremental index updates

Full-rebuild-on-refresh costs redundant CPU and doubles peak memory while both snapshots are live. Replace it with a diff-based update that touches only changed entries - e.g. `ImmutableDictionary.Builder` plus one atomic swap, which preserves the per-request consistent view from the snapshot ADR. (A `ConcurrentDictionary` is an alternative - `ImmutableDictionary` already provides lock-free concurrent reads, so the real gain would be incremental mutation - but it gives up cross-map snapshot consistency and needs its own concurrency story for the `ByWords` collection values.) Pairs with TODO #2. **Supersedes** the "Atomic snapshot swap" ADR's full-rebuild trade-off.

## References

- AUR package metadata dump: `https://aur.archlinux.org/packages-meta-ext-v1.json.gz`
- AUR RPC interface: `https://aur.archlinux.org/rpc`
- Git Smart HTTP protocol: `https://git-scm.com/docs/http-protocol`
- Local setup and quickstart: see `README.md`
