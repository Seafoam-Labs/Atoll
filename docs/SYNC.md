# Package seeding and refresh

This document describes how Atoll gets package content from AUR into MongoDB and keeps it current:

- **Seeding** imports packages listed in the metadata index that are missing from the package repository.
- **Periodic refresh** re-syncs already-seeded packages when upstream changes.
- **Upstream reconciliation** removes seeded package names that disappear from a successfully parsed full AUR snapshot.

It is intended for maintainers changing a seed or refresh worker or their shared Git transport, and for operators
diagnosing a failed sync. System context lives in [Architecture Overview](ARCHITECTURE.md); the security gating applied
to refreshed revisions is documented in [Package security scanning](SECURITY.md).

The application-level implementation is in:

- `Atoll.Api/Services/Sync/Direct/DirectSeedWorker.cs`
- `Atoll.Api/Services/Sync/Bulk/PackageBulkSeedWorker.cs`
- `Atoll.Api/Services/Sync/Bulk/BulkSeedPlan.cs`
- `Atoll.Api/Services/Sync/Refresh/PackageRefreshWorker.cs`
- `Atoll.Api/Services/Sync/Refresh/RefreshPlan.cs`
- `Atoll.Api/Services/Sync/Mirror/AurMirror.cs`
- `Atoll.Api/Services/Catalog/Refresh/PackageIndexUpdater.cs`
- `Atoll.Api/Services/Catalog/Refresh/UpstreamPackageReconciler.cs`

## pkgname and pkgbase

Atoll stores and seeds documents by AUR **pkgname**, but AUR Git repositories and GitHub mirror branches are named by
**pkgbase**. A split package can therefore have several pkgnames backed by one Git tree:

```text
pkgname:  foo-cli ─┐
                   ├─ pkgbase: foo ─ Git branch: refs/heads/foo
pkgname:  libfoo ──┘
```

Both bulk seeding and refresh resolve pkgname to pkgbase before discovery and fetching, then fan the fetched tree back
out to each member pkgname individually.

## Metadata polling and authoritative reconciliation

### Why the metadata archive is the source of truth

AUR exposes four change-detection surfaces; Atoll's sync design uses two of them as authorities and rejects the rest:

| Surface | What it reports | Role in Atoll |
| --- | --- | --- |
| Metadata archive (`packages-meta-ext-v1.json.gz`, produced every ~5 minutes) | The complete package set with version and dependency metadata; supports `ETag`/`Last-Modified` conditional requests | Authoritative for **additions and removals**: the index rebuilds from it, and reconciliation only prunes against a successfully parsed complete snapshot |
| AUR Git (`aur.archlinux.org/{pkgbase}.git`, via the GitHub mirror with a direct-AUR fallback) | The PKGBUILD commit tree of each package | Authoritative for **content updates**: Git HEAD/content is a stronger update signal than metadata `Version`/`LastModified` |
| RPC (`/rpc/v5/info`) | Metadata for individually named packages (~200 names per request, 4,000 requests/day per IP). A name missing from a response no longer exists — but may have been renamed or merged, and a transient failure is indistinguishable from absence | Not used: the archive carries the same data without the rate limit, so a failed RPC never drives a deletion decision |
| RSS (`/rss`) | Newest *submitted* packages only, in a bounded recent window | Not usable as a sync source: it reports neither updates nor removals |

The ArchWiki recommends the archives over bulk RPC queries to reduce server load
(<https://wiki.archlinux.org/title/Aurweb_RPC_interface>), which is why Atoll polls the archive at its ~5-minute
production cadence with conditional requests rather than polling the RPC.

### Polling and reconciliation behavior

`PackageIndexWorker` polls `packages-meta-ext-v1.json.gz` at `Atoll:DataSource:RefreshIntervalMinutes` (default 5
minutes).

1. **Startup & Priming:** Startup rebuilds the in-memory index from cached MongoDB metadata (if present) so search is
   available immediately, then initiates an upstream check without waiting for the first interval.
2. **Conditional Requests:** Subsequent polls send `ETag` and `Last-Modified` validators. A `304 Not Modified` skips
   downloading, decompressing, and re-parsing the dump.
3. **Atomic Updates:** A modified dump must decompress and parse into a valid, non-empty JSON package array.
   `PackageIndexUpdater` writes the batch to MongoDB and atomically swaps the active pointer, ensuring readers never see
   a partial snapshot.
4. **Authoritative Pruning (`PruneDeletedPackages`):**
   When `Atoll:DataSource:PruneDeletedPackages=true`, `UpstreamPackageReconciler` compares MongoDB's seeded package set
   against the newly published snapshot:
   - Packages missing upstream are deleted along with their revision documents, security scans, and bare Git repos.
   - **Corruption Guard:** If an upstream snapshot shrinks by >10% compared to the current index, pruning is deferred
     until a second consecutive snapshot confirms the drop. The first snapshot's validators are withheld so the next
     poll downloads the archive again instead of accepting a `304`.
   - Failed, malformed, or empty responses never trigger pruning.
   - Pruning operates independently of `Atoll:Mutations:Enabled` (background sync continues even when public API
     mutations are disabled).

## Seeding

Atoll seeds missing packages listed in the metadata index into the package repository. Select the strategy with
`Atoll:Seed:Mode`:

- **`Direct`** - maps each missing **pkgname** to its **pkgbase** and clones that repository directly from AUR. This is
  the default mode and does not need a mirror cache.
- **`Bulk`** - discovers and batch-fetches **pkgbase** branches from the GitHub AUR mirror into a persistent bare cache,
  then seeds each mapped pkgname from the extracted tree.
- **`Off`** - does not register an automated seed worker. Metadata indexing and explicit `POST /packages/{name}/seed`
  requests remain available unless `Atoll:Mutations:Enabled=false`, which rejects manual seeding, rescans, and deletes
  (`403`) for publicly exposed instances.

The modes are mutually exclusive: startup registers one worker for Direct or Bulk, and no seed worker for Off. Periodic
refresh is independent of this choice and can run alongside any of it.

### Off mode

Use Off when Atoll should retain its metadata index and support only manually requested seeds:

```json
{
  "Atoll": {
    "Seed": {
      "Mode": "Off"
    }
  }
}
```

Off does not remove or alter packages already stored in MongoDB; it only prevents the application from automatically
finding and seeding missing packages. The Direct and Bulk configuration sections may remain present but are ignored.

### Direct mode

`DirectSeedWorker` is the simplest and default strategy. On each cycle it:

1. Reads the current metadata index and waits 15 seconds if it is empty.
2. Lists packages already in the package repository and selects missing pkgnames.
3. Runs `DirectPackageSeeder` once per missing pkgname: it rejects packages already present, maps the pkgname to
   its pkgbase via the index, fetches `aur.archlinux.org/{pkgbase}.git` through `AurGitPackageSource`, and delegates
   persistence to `IPackageService.SeedFilesAsync`. The source uses a unique temporary checkout, excludes `.git`, and
   removes the checkout after success, failure, or cancellation.
4. Waits for `Atoll:Seed:Direct:SeedDelayMs` after every attempt, including a failed attempt or a conflict caused by
   another request seeding the package first.
5. Checks again after one minute when all indexed packages are present, limiting the delay after a metadata refresh.
   A cycle that had candidates but seeded none waits five minutes before retrying failures.

Configure it as follows:

```json
{
  "Atoll": {
    "Seed": {
      "Mode": "Direct",
      "Direct": {
        "SeedDelayMs": 1000
      }
    }
  }
}
```

`SeedDelayMs` defaults to `1000` ms and is validated in configuration to **100–60,000** ms; the worker also clamps lower
runtime values to 100 ms. Increase it when directly cloning a large number of packages to reduce AUR request pressure.
Direct mode has no bulk cache, mirror-branch discovery, pkgbase grouping, or bulk-specific metrics. Use the worker logs
to review cycle candidate, seeded, conflict, and failure counts.

### Bulk mode

#### How it works

For each seed cycle, `PackageBulkSeedWorker`:

1. **Candidate Discovery:** Identifies indexed pkgnames missing from MongoDB.
2. **Pkgbase Grouping:** Maps each pkgname to its pkgbase (falling back to pkgname if unmapped) and groups candidates.
3. **Exclusion Check:** Filters out pkgbases previously flagged in `seed-exclusions` (e.g. exceeding the 16 MiB BSON
   limit).
4. **Mirror Intersection:** Queries `git ls-remote --heads` on the mirror and keeps only advertised branches.
5. **Pipelined Batch Fetching:** Fetches target pkgbase branches into a persistent bare cache (`--depth=1`). Fetching
   runs ahead as a pipeline stage while previous batches are extracted and persisted.
6. **Archive & Seeding:** Extracts each pkgbase tree via `git archive` and invokes `SeedFilesAsync` concurrently across
   members (`Parallelism`), assigning each split pkgname its deterministic revision snapshot in `package-revisions`.

This replaces one network clone per pkgname with one batched request per group of pkgbases. It retains the existing seed
validation and persistence path. In particular, each split pkgname is still seeded separately and receives its normal
pkgname-specific revision identity. When all indexed packages are present, the worker checks again after one minute;
failed/no-progress cycles retain their longer backoff.

#### Configuration and observability

Bulk mode is active only when `Atoll:Seed:Mode` is `Bulk`. It is mutually exclusive with Direct mode, so the two workers
do not race to seed the same missing package. Set `Atoll:Seed:Mode` to `Off` to register neither worker.

```json
{
  "Atoll": {
    "Seed": {
      "Mode": "Bulk",
      "Bulk": {
        "MirrorUrl": "https://github.com/archlinux/aur",
        "CachePath": "./data/aur-mirror",
        "BatchSize": 1000,
        "BatchDelayMs": 1000,
        "Parallelism": 4,
        "AurFallbackForNotOnMirror": false
      }
    }
  }
}
```

`BatchSize`, `BatchDelayMs`, and `Parallelism` are shared fetch controls; their defaults, limits, and resource
trade-offs are documented in [Mirror transport](#shared-fetch-controls).

`AurFallbackForNotOnMirror` applies only when a target pkgbase is absent from the mirror branch list. When enabled, each
mapped pkgname is seeded through `DirectPackageSeeder` (the direct-AUR path) instead. It does not replace bisection for
fetch failures among branches that were advertised by the mirror.

`GET /metrics` exposes `atoll_bulkseed_*` Prometheus metrics, including batch attempts/successes/failures, skipped and
failed refs, seeded/skipped/excluded packages, and cycle timestamps. Each cycle-complete log line also reports the
total cycle time with the fetch and seed phase durations, which overlap because of pipelining. Use logs and metrics
together to distinguish these outcomes:

| Symptom | Expected evidence |
| --- | --- |
| Target absent from mirror | `atoll_bulkseed_refs_skipped_total` increases; direct fallback is used only if enabled. |
| Ref changed after discovery | `atoll_bulkseed_refs_failed_total` increases after bisection; other refs in the original batch continue. |
| Tree cannot be read | A `git archive`-related warning; mapped pkgnames are skipped for that cycle. |
| Revision snapshot exceeds BSON limit | `atoll_bulkseed_packages_excluded_total` increases and the pkgbase is persisted in `seed-exclusions`, preventing repeated fetches in later cycles. |
| All indexed packages already seeded | The worker checks for newly indexed packages again in one minute. |

## Periodic refresh

The `PackageRefreshWorker` (`Atoll:Refresh:Enabled`, default `true`) continuously re-synces seeded packages so
the latest upstream version is available instead of freezing at first seed. It is independent of the seed mode and can
run alongside either `DirectSeedWorker` or `PackageBulkSeedWorker`; when active it shares the same `IAurMirror`
singleton (GitHub mirror cache) as bulk seeding — see [Cache lifecycle](#cache-lifecycle).

Change detection is **content-based via upstream HEAD SHA**, not AUR metadata timestamps.

### How it works

Refresh runs every minute (backing off on error). Each cycle executes the following stages:

1. **Guard Check:** Skips if the metadata index is empty or no packages are seeded.
2. **Candidate Grouping:** Projects sync state from `packages`, resolves `pkgname` → `pkgbase`, and groups members.
3. **Branch Discovery:** Runs `git ls-remote --heads` against the mirror to discover current remote SHAs.
4. **Candidate Selection:** Filters for pkgbases where:
   - Any member has never been synced, or
   - The remote HEAD SHA differs from `lastSyncedUpstreamHead`, or
   - The last sync exceeds `MaxStalenessHours` (staleness safety sweep).
   Candidates are ordered least-recently-synced first.
5. **Selective Batch Fetch:**
   - Pkgbases whose remote SHA has not moved (staleness sweep only) skip fetching entirely; their watermarks advance
     directly.
   - Pkgbases with actual SHA movement are batch-fetched into the mirror cache (bounded by `MaxPackagesPerRun`).
6. **Archive & Revision Append:**
   - Extracts the pkgbase tree via `git archive`.
   - Compares the deterministic revision ID with the current head. If unchanged, updates watermarks without creating a
     new revision.
   - If content changed, appends the revision via `AppendRevisionFromUpstreamAsync` (writing to `package-revisions`
     first, updating root metadata second, and trimming history to `MaxRevisions`).
7. **Watermark & State Updates:** Updates `lastSyncedUpstreamHead`, `lastSyncSucceededAt`, or `lastSyncError` across all
   member pkgnames.

New head revisions are marked `Pending` for security scanning and the previous head is demoted (`PromoteHeadAsync`),
conservatively **blocking content and Git access until verified**. On-disk bare repos are lazily re-materialized on the
next Git request when the updated `headRevisionId` is observed.

Each `packages` document carries lightweight refresh watermarks (`upstreamPackageBase`, `lastSyncedUpstreamHead`,
`lastSyncAttemptAt`, `lastSyncSucceededAt`, `lastSyncError`); these are nullable and omitted when unset, so they do not
change the public API response contracts.

If a revision snapshot exceeds MongoDB's 16 MiB document limit (checked before insert), the append fails
deterministically; the worker records the pkgbase in `seed-exclusions` (reason `mongo-document-too-large`) and skips
it — together with all other excluded pkgbases — in every subsequent cycle instead of re-fetching it forever. Clearing
the exclusion re-enables refresh for that pkgbase.

### Measuring document sizes (ops)

To size the storage profile of a deployment, rank packages by BSON size and count documents approaching the 16 MiB
cap:

```javascript
// Top 20 largest package documents.
db.packages.aggregate([
  { $project: { packageName: 1, sizeBytes: { $bsonSize: "$$ROOT" } } },
  { $sort: { sizeBytes: -1 } },
  { $limit: 20 },
]);

// Counts within 25% / 10% of the 16 MiB cap.
db.packages.aggregate([
  { $project: { sizeBytes: { $bsonSize: "$$ROOT" } } },
  {
    $facet: {
      within25pct: [
        { $match: { sizeBytes: { $gte: 0.75 * 16777216 } } },
        { $count: "n" },
      ],
      within10pct: [
        { $match: { sizeBytes: { $gte: 0.9 * 16777216 } } },
        { $count: "n" },
      ],
    },
  },
]);

// Same for per-revision snapshot documents.
db.getCollection("package-revisions").aggregate([
  { $project: { _id: 1, sizeBytes: { $bsonSize: "$$ROOT" } } },
  { $sort: { sizeBytes: -1 } },
  { $limit: 20 },
]);
```

### Configuration

```json
{
  "Atoll": {
    "Refresh": {
      "Enabled": true,
      "BatchSize": 1000,
      "BatchDelayMs": 1000,
      "Parallelism": 4,
      "MaxPackagesPerRun": 10000,
      "MaxStalenessHours": 24,
      "MirrorUrl": "https://github.com/archlinux/aur",
      "CachePath": "./data/aur-mirror"
    }
  }
}
```

- `Enabled` (default `true`) — registers the refresh worker.
- `BatchSize`, `BatchDelayMs`, and `Parallelism` — shared fetch controls; see
  [Mirror transport](#shared-fetch-controls).
- `MaxPackagesPerRun` (default `10000`, validated to **1–500,000**) — caps packages fetched per cycle. Genuine SHA
  movers are rare, so this mainly bounds bursts after large seeds or long outages; deferred pkgbases are picked up by
  later cycles.
- `MaxStalenessHours` (default `24`, validated to **1–720**) — safety-sweep threshold for re-checking pkgbases whose
  SHA has not moved.
- `MirrorUrl` / `CachePath` — mirror settings used **only when bulk seeding is not active**. Both workers share one
  `IAurMirror` singleton, and when bulk mode is active `Atoll:Seed:Bulk:MirrorUrl`/`CachePath` configure it while the
  `Atoll:Refresh` mirror settings are ignored. The defaults are identical; keep them aligned.

### Observability

`GET /metrics` exposes `atoll_packagerefresh_*` Prometheus metrics with cycle counters (`cycles.attempted.total` /
`cycles.succeeded.total` / `cycles.failed.total`), candidate gauges (`candidate_packages` / `candidate_package_bases`),
outcomes (`packages.updated.total` / `packages.unchanged.total` / `packages.skipped.total`), ref counts
(`refs.skipped.total` / `refs.failed.total`), and cycle start/finish timestamps. Each cycle-complete log line also
reports the total cycle time with the fetch and apply phase durations, which overlap because of pipelining. Use logs
and metrics together to distinguish these outcomes:

| Symptom | Expected evidence |
| --- | --- |
| pkgbase no longer on the mirror | `atoll_packagerefresh_refs_skipped_total` increases; member watermarks stay untouched. |
| Branch disappeared between discovery and fetch | `atoll_packagerefresh_refs_failed_total` increases after bisection; affected members record `lastSyncError`. |
| Cycle capped by `MaxPackagesPerRun` | Log line about deferral; remaining candidates are picked up in later cycles. |
| Staleness sweep found no SHA movement | `atoll_packagerefresh_packages_unchanged_total` increases without any fetch batches. |
| Tree cannot be read | A `git archive`-related warning; affected members record `lastSyncError`. |

## Mirror transport

Bulk seeding and refresh share the `AurMirror` Git transport: one persistent bare cache, protocol v2 with explicit
refspecs, depth-one fetches, and `git archive` extraction.

### Shared fetch controls

Both workers use the same controls with the same defaults and validation:

| Option | Default and range | Effect |
| --- | --- | --- |
| `BatchSize` | `1000`; 10–10,000 | Pkgbases requested by each `git fetch` invocation. |
| `BatchDelayMs` | `1000` ms; 100–60,000 | Delay between fetch batches. Runtime values below 100 ms are clamped to 100 ms. |
| `Parallelism` | `4`; 1–128 | Pkgbases from a fetched batch archived and applied concurrently. Higher values trade CPU and MongoDB write pressure for shorter cycles; fetching and application already overlap, so this does not increase mirror request pressure. |

The controls live under `Atoll:Seed:Bulk` for bulk seeding and `Atoll:Refresh` for refresh. When bulk seeding is active,
its mirror settings configure the shared transport; see [Cache lifecycle](#cache-lifecycle).

### Git transport contract

The configured default mirror is `https://github.com/archlinux/aur`. The following are required assumptions, not
incidental implementation details:

| Contract | Why it matters |
| --- | --- |
| A mirror branch is `refs/heads/<pkgbase>`. | Both workers must map pkgname to pkgbase before discovery and fetching. |
| Branch discovery uses `git ls-remote --heads`. | Official packages and other non-AUR index entries have no mirror branch and must not enter fetch lists. |
| Fetch uses explicit refspecs, such as `+refs/heads/foo:refs/atoll/foo`. | Git protocol v2 can limit the advertised refs to requested prefixes; the cache keeps fetched trees addressable by pkgbase without creating normal local branches. |
| Fetch is `--depth=1 --no-tags`. | Seeding and refresh only need the current tree, not history or tags. |
| File extraction is `git archive --format=tar refs/atoll/<pkgbase>`. | Workers read a tree, not a checkout, and exclude Git-internal paths defensively. |
| A failed multi-ref fetch is treated as an unsuccessful batch. | A ref can disappear after discovery. The application bisects the batch until it can skip only unreachable refs and continue with the rest. |

The cache is a bare repository at the configured `CachePath` (default `./data/aur-mirror`). Its fetched refs live in
`refs/atoll/`; it is not a clone of every mirror branch.

### Cache lifecycle

The cache persists Git objects and fetched `refs/atoll/*` refs across cycles and across both workers. It is
intentionally retained so subsequent cycles do not repeatedly bootstrap a repository, but it has no automatic pruning
or size limit. A full sync has historically been estimated at roughly 3 GB; actual growth depends on the mirror and how
many refs change. Monitor the configured cache path and reclaim space deliberately according to the deployment's
retention policy.

Both workers use a single `IAurMirror` singleton, registered when either bulk seeding or refresh is active. When bulk
mode is active, its `MirrorUrl`/`CachePath` configures the singleton and the `Atoll:Refresh` mirror settings are
ignored; this is deliberate so both workers share one cache.

The mirror is upstream-experimental and can lag or change. Before changing code in response to a mirror incident, run
the plain-Git checks below and record whether the failure is in branch discovery, fetch, archive extraction, or tree
parity with direct AUR.

### Plain-Git verification

Run the following on a machine with Git and network access. It does not require Atoll, MongoDB, or any application
credentials. Use an empty disposable directory for `CACHE`; do not initialize a working repository there.

```sh
CACHE=/tmp/atoll-aur-mirror-check
MIRROR=https://github.com/archlinux/aur

git init --bare --quiet "$CACHE"
git -C "$CACHE" remote add origin "$MIRROR"
```

If checking the configured cache rather than a disposable one, use the configured `CachePath` and run
`git -C "$CACHE" remote set-url origin "$MIRROR"` instead of reinitializing it.

#### 1. Verify branch discovery and pkgbase naming

List mirror branches using the same protocol mode and `--heads` filter as the application:

```sh
git -C "$CACHE" -c protocol.version=2 ls-remote --heads origin
```

Each line has this form:

```text
<commit-sha>    refs/heads/<pkgbase>
```

Choose one printed `<pkgbase>` and substitute it in the following commands. A pkgname that differs from its pkgbase is a
useful split-package test case: the branch name must be the pkgbase, never the individual split pkgname.

To make a deterministic targeted check without scanning the output manually:

```sh
git -C "$CACHE" -c protocol.version=2 ls-remote --heads origin "refs/heads/<pkgbase>"
```

It must print exactly the requested branch for a mirror-resident pkgbase. No output means that the base is not available
on the mirror; bulk seeding will either count its mapped pkgnames as skipped or, when configured, use the direct-AUR
fallback, and refresh will leave its members untouched.

#### 2. Verify the exact fetch and local ref namespace

Fetch a known mirror branch using the production refspec shape:

```sh
git -C "$CACHE" -c protocol.version=2 fetch --depth=1 --no-tags --quiet origin \
  "+refs/heads/<pkgbase>:refs/atoll/<pkgbase>"

git -C "$CACHE" show-ref --verify "refs/atoll/<pkgbase>"
git -C "$CACHE" rev-parse --is-shallow-repository
```

Expected results:

- `show-ref --verify` prints a SHA and `refs/atoll/<pkgbase>` and exits successfully.
- `rev-parse --is-shallow-repository` prints `true` for a newly created cache after this fetch.
- No `refs/heads/<pkgbase>` is required locally; `refs/atoll/<pkgbase>` is the deliberate cache namespace.

For a small manual batch, repeat the explicit refspec argument for each base in one `fetch` invocation:

```sh
git -C "$CACHE" -c protocol.version=2 fetch --depth=1 --no-tags --quiet origin \
  "+refs/heads/<pkgbase-a>:refs/atoll/<pkgbase-a>" \
  "+refs/heads/<pkgbase-b>:refs/atoll/<pkgbase-b>"
```

This is equivalent to one worker batch. See [Shared fetch controls](#shared-fetch-controls) for the corresponding
configuration and limits.

#### 3. Verify archive extraction

The workers stream this archive into a TAR reader. Inspect it directly:

```sh
git -C "$CACHE" archive --format=tar "refs/atoll/<pkgbase>" | tar -tvf -
```

To inspect a specific file without a working checkout:

```sh
git -C "$CACHE" archive --format=tar "refs/atoll/<pkgbase>" PKGBUILD | tar -xOf -
```

The archive must contain the source tree expected from the AUR package, including files such as `PKGBUILD` when present.
It must be readable from the `refs/atoll/<pkgbase>` ref created above.

#### 4. Optional: compare mirror tree with direct AUR

This checks that the mirror supplies the same current tree as direct AUR for a selected base. Use a temporary directory
and replace `<pkgbase>`.

```sh
DIRECT=/tmp/atoll-aur-direct-check
MIRROR_TREE=/tmp/atoll-aur-mirror-tree
DIRECT_TREE=/tmp/atoll-aur-direct-tree

rm -rf "$DIRECT" "$MIRROR_TREE" "$DIRECT_TREE"
git clone --depth=1 --quiet "https://aur.archlinux.org/<pkgbase>.git" "$DIRECT"
mkdir -p "$MIRROR_TREE" "$DIRECT_TREE"
git -C "$CACHE" archive --format=tar "refs/atoll/<pkgbase>" | tar -xf - -C "$MIRROR_TREE"
git -C "$DIRECT" archive --format=tar HEAD | tar -xf - -C "$DIRECT_TREE"
diff -ru "$MIRROR_TREE" "$DIRECT_TREE"
```

No `diff` output and a zero exit status means the two archived trees match. A mismatch can be legitimate when the
experimental mirror lags AUR; it is operational evidence to investigate, not a reason to change the mapping or refspec
contract.

#### 5. Verify missing-ref handling and batch recovery

The workers prevent known missing refs from reaching `fetch` by intersecting targets with `ls-remote --heads`. Confirm
why this matters by combining a verified base with a deliberately impossible one:

```sh
git -C "$CACHE" -c protocol.version=2 fetch --depth=1 --no-tags origin \
  "+refs/heads/<pkgbase>:refs/atoll/<pkgbase>" \
  "+refs/heads/atoll-verification-ref-does-not-exist:refs/atoll/atoll-verification-ref-does-not-exist"
```

This command must fail because the second remote ref does not exist. In production, `AurMirror.FetchAsync` responds by
splitting the failed list in half recursively, fetching valid halves, and reporting only single unreachable refs as
failed. This also handles a real branch deletion or rename that occurs between discovery and fetch.

Do not use a missing ref as a normal control path: discovery is the normal protection, while bisection is recovery for a
race or unexpected Git failure.
