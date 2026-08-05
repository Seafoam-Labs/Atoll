# Package seeding and refresh

This document describes how Atoll gets package content from AUR into MongoDB and keeps it current:

- **Seeding** imports packages listed in the metadata index that are missing from the package repository.
- **Periodic refresh** re-syncs already-seeded packages when upstream changes.

It is intended for maintainers changing a seed or refresh worker or their shared Git transport, and for operators
diagnosing a failed sync. System context lives in [Architecture Overview](ARCHITECTURE.md); the security gating applied
to refreshed revisions is documented in [Package security scanning](SECURITY.md).

The application-level implementation is in:

- `Atoll.Api/Services/Packages/Seed/DirectSeedWorker.cs`
- `Atoll.Api/Services/Packages/Seed/PackageBulkSeedWorker.cs`
- `Atoll.Api/Services/Packages/Seed/BulkSeedPlan.cs`
- `Atoll.Api/Services/Packages/Refresh/PackageRefreshWorker.cs`
- `Atoll.Api/Services/Packages/Refresh/RefreshPlan.cs`
- `Atoll.Api/Services/Packages/Mirror/AurMirror.cs`

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

## Seeding

Atoll seeds missing packages listed in the metadata index into the package repository. Select the strategy with
`Atoll:Seed:Mode`:

- **`Direct`** - clones each missing **pkgname** directly from AUR. This is the default mode and does not need a mirror
  cache.
- **`Bulk`** - discovers and batch-fetches **pkgbase** branches from the GitHub AUR mirror into a persistent bare cache,
  then seeds each mapped pkgname from the extracted tree.
- **`Off`** - does not register an automated seed worker. Metadata indexing and explicit `POST /packages/{name}/seed`
  requests remain available.

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
3. Calls the existing `IPackageService.SeedFromAurAsync` path once for each missing pkgname, which retrieves it from
   `aur.archlinux.org`.
4. Waits for `Atoll:Seed:Direct:SeedDelayMs` after every attempt, including a failed attempt or a conflict caused by
   another request seeding the package first.
5. Waits five minutes before retrying when all indexed packages are present or when a cycle seeds no packages.

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

For each seed cycle, the worker:

1. Finds metadata-indexed pkgnames that do not yet exist in the package repository.
2. Resolves each pkgname to its pkgbase and groups the pkgnames by pkgbase. If metadata has no pkgbase, it uses the
   pkgname as the fallback.
3. Excludes pkgbases previously recorded as too large for MongoDB's 16 MiB BSON-document limit.
4. Lists the mirror's branches once and intersects them with the target pkgbases.
5. Fetches the remaining pkgbase branches in batches into one persistent bare cache, at depth one. Fetching runs
   ahead as a pipeline stage, so later batches download while earlier ones are still being processed.
6. Archives each fetched tree once, then calls `SeedFilesAsync` for every pkgname mapped to that pkgbase. Within a
   batch, archive extraction and seeding run with bounded parallelism (`Parallelism`); this is safe because
   `git archive` only reads the bare cache and each seeded package document is disjoint.

This replaces one network clone per pkgname with one batched request per group of pkgbases. It retains the existing seed
validation and persistence path. In particular, each split pkgname is still seeded separately and receives its normal
pkgname-specific revision identity. When a cycle seeds no packages, the worker waits five minutes before checking again.

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
mapped pkgname is seeded through the existing direct-AUR path instead. It does not replace bisection for fetch failures
among branches that were advertised by the mirror.

`GET /metrics` exposes `bulkSeed` status, including batch attempts/successes/failures, skipped and failed refs,
seeded/skipped/excluded packages, and cycle timestamps. Each cycle-complete log line also reports the total cycle time
with the fetch and seed phase durations, which overlap because of pipelining. Use logs and metrics together to
distinguish these outcomes:

| Symptom                              | Expected evidence                                                                                                            |
| ------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------- |
| Target absent from mirror            | `refsSkipped` increases; direct fallback is used only if enabled.                                                            |
| Ref changed after discovery          | `refsFailed` increases after bisection; other refs in the original batch continue.                                           |
| Tree cannot be read                  | A `git archive`-related warning; mapped pkgnames are skipped for that cycle.                                                 |
| Revision snapshot exceeds BSON limit | `packagesExcluded` increases and the pkgbase is persisted in `seed-exclusions`, preventing repeated fetches in later cycles. |
| Nothing to seed                      | The worker waits five minutes before the next check.                                                                         |

## Periodic refresh

The opt-in `PackageRefreshWorker` (`Atoll:Refresh:Enabled`, default `false`) continuously re-synces seeded packages so
the latest upstream version is available instead of freezing at first seed. It is independent of the seed mode and can
run alongside either `DirectSeedWorker` or `PackageBulkSeedWorker`; when active it shares the same `IAurMirror`
singleton (GitHub mirror cache) as bulk seeding — see [Cache lifecycle](#cache-lifecycle).

Change detection is **content-based via upstream HEAD SHA**, not AUR metadata timestamps.

### How it works

Cycles run every minute; a failed cycle backs off one minute before retrying. Each cycle:

1. Skips when the metadata index is empty or when no packages are seeded yet.
2. Reads the sync state of all seeded packages (a lean projection of `packages`), resolves each package's **pkgbase**
   from the index, falling back to the stored `upstreamPackageBase`, then the pkgname, and groups members by pkgbase.
3. Issues one `git ls-remote --heads` against the mirror to build a branch→SHA map. pkgbases with no mirror branch are
   counted as `refsSkipped` and left untouched.
4. Selects candidates: pkgbases where any member was never synced, where the stored `lastSyncedUpstreamHead` differs
   from the current branch head, or where the last successful sync is older than `MaxStalenessHours` (a safety sweep so
   nothing waits forever even when its SHA has not moved). Candidates are processed least-recently-synced first.
5. Splits candidates by whether the upstream SHA actually moved. Candidates whose SHA is unchanged for **every** member
   (staleness-only) skip the fetch entirely — the worker just advances their watermarks. Only genuine SHA movers are
   batch-fetched, capped at `MaxPackagesPerRun`, reusing the bulk-seed batching/bisection. As in bulk seeding, fetching
   runs ahead of application as a pipeline stage.
6. For each fetched pkgbase, archives the tree once and applies it to each member pkgname with bounded parallelism
   (`Parallelism`). If the deterministic revision ID matches the current head, the package is recorded unchanged (but
   the watermark still advances so the pkgbase is not refetched); otherwise a new revision is appended via
   `AppendRevisionFromUpstreamAsync`, which writes the snapshot as its own document in `package-revisions` before
   updating the package document's head/metadata (write ordering keeps readers from ever observing a head without
   content) and deletes revision documents evicted by the `MaxRevisions` cap.
7. Advances the watermarks of all members of successful pkgbases; fetch or application failures record the error on the
   affected members.

New head revisions are marked `Pending` for security scanning and the previous head's scan is demoted
(`PromoteHead
Async`), so refreshed heads are conservatively **blocked from being served until scanned**, exactly like a
fresh seed. On-disk bare repos are not touched during sync — `EnsureGitRepositoryAsync` observes the new
`headRevisionId` and re-materializes lazily on the next request, keeping MongoDB authoritative.

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

`GET /metrics` exposes a `packageRefresh` block with cycle counters (`cyclesAttempted`/`cyclesSucceeded`/
`cyclesFailed`), candidate counts (`candidatePackages`/`candidatePackageBases`), outcomes (`packagesUpdated`/
`packagesUnchanged`/`packagesSkipped`), ref counts (`refsSkipped`/`refsFailed`), and cycle start/finish timestamps.
Each cycle-complete log line also reports the total cycle time with the fetch and apply phase durations, which overlap
because of pipelining. Use logs and metrics together to distinguish these outcomes:

| Symptom                                        | Expected evidence                                                                |
| ---------------------------------------------- | -------------------------------------------------------------------------------- |
| pkgbase no longer on the mirror                | `refsSkipped` increases; member watermarks stay untouched.                       |
| Branch disappeared between discovery and fetch | `refsFailed` increases after bisection; affected members record `lastSyncError`. |
| Cycle capped by `MaxPackagesPerRun`            | Log line about deferral; remaining candidates are picked up in later cycles.     |
| Staleness sweep found no SHA movement          | `packagesUnchanged` increases without any fetch batches.                         |
| Tree cannot be read                            | A `git archive`-related warning; affected members record `lastSyncError`.        |

## Mirror transport

Bulk seeding and refresh share the `AurMirror` Git transport: one persistent bare cache, protocol v2 with explicit
refspecs, depth-one fetches, and `git archive` extraction.

### Shared fetch controls

Both workers use the same controls with the same defaults and validation:

| Option         | Default and range     | Effect                                                                                                                                                                                                                               |
| -------------- | --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `BatchSize`    | `1000`; 10–10,000     | Pkgbases requested by each `git fetch` invocation.                                                                                                                                                                                   |
| `BatchDelayMs` | `1000` ms; 100–60,000 | Delay between fetch batches. Runtime values below 100 ms are clamped to 100 ms.                                                                                                                                                      |
| `Parallelism`  | `4`; 1–128            | Pkgbases from a fetched batch archived and applied concurrently. Higher values trade CPU and MongoDB write pressure for shorter cycles; fetching and application already overlap, so this does not increase mirror request pressure. |

The controls live under `Atoll:Seed:Bulk` for bulk seeding and `Atoll:Refresh` for refresh. When bulk seeding is active,
its mirror settings configure the shared transport; see [Cache lifecycle](#cache-lifecycle).

### Git transport contract

The configured default mirror is `https://github.com/archlinux/aur`. The following are required assumptions, not
incidental implementation details:

| Contract                                                                | Why it matters                                                                                                                                                    |
| ----------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| A mirror branch is `refs/heads/<pkgbase>`.                              | Both workers must map pkgname to pkgbase before discovery and fetching.                                                                                           |
| Branch discovery uses `git ls-remote --heads`.                          | Official packages and other non-AUR index entries have no mirror branch and must not enter fetch lists.                                                           |
| Fetch uses explicit refspecs, such as `+refs/heads/foo:refs/atoll/foo`. | Git protocol v2 can limit the advertised refs to requested prefixes; the cache keeps fetched trees addressable by pkgbase without creating normal local branches. |
| Fetch is `--depth=1 --no-tags`.                                         | Seeding and refresh only need the current tree, not history or tags.                                                                                              |
| File extraction is `git archive --format=tar refs/atoll/<pkgbase>`.     | Workers read a tree, not a checkout, and exclude Git-internal paths defensively.                                                                                  |
| A failed multi-ref fetch is treated as an unsuccessful batch.           | A ref can disappear after discovery. The application bisects the batch until it can skip only unreachable refs and continue with the rest.                        |

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
