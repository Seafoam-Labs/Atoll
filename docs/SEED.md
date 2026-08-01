# Package seeding

This document describes Atoll's package-seeding modes. It is intended for maintainers changing a seed worker or its Git transport, and for operators diagnosing a failed sync.

Atoll seeds missing packages listed in the metadata index into the package repository. Select the strategy with `Atoll:Seed:Mode`:

- **`Direct`** - clones each missing **pkgname** directly from AUR. This is the default mode and does not need a mirror cache.
- **`Bulk`** - discovers and batch-fetches **pkgbase** branches from the GitHub AUR mirror into a persistent bare cache, then seeds each mapped pkgname from the extracted tree.

The modes are mutually exclusive: startup registers exactly one seed worker, so Direct and Bulk never race to seed the same package. The application-level implementation is in:

- `Atoll.Api/Services/Packages/Seed/DirectSeedWorker.cs`
- `Atoll.Api/Services/Packages/Seed/PackageBulkSeedWorker.cs`
- `Atoll.Api/Services/Packages/Seed/AurMirror.cs`
- `Atoll.Api/Services/Packages/Seed/BulkSeedPlan.cs`

## Direct mode

`DirectSeedWorker` is the simplest and default strategy. On each cycle it:

1. Reads the current metadata index and waits 15 seconds if it is empty.
2. Lists packages already in the package repository and selects missing pkgnames.
3. Calls the existing `IPackageService.SeedFromAurAsync` path once for each missing pkgname, which retrieves it from `aur.archlinux.org`.
4. Waits for `Atoll:Seed:Direct:SeedDelayMs` after every attempt, including a failed attempt or a conflict caused by another request seeding the package first.
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

`SeedDelayMs` defaults to `1000` ms and is validated in configuration to **100–60,000** ms; the worker also clamps lower runtime values to 100 ms. Increase it when directly cloning a large number of packages to reduce AUR request pressure. Direct mode has no bulk cache, mirror-branch discovery, pkgbase grouping, or bulk-specific metrics. Use the worker logs to review cycle candidate, seeded, conflict, and failure counts.

## Bulk mode

### Principle

Atoll stores and seeds documents by AUR **pkgname**, but AUR Git repositories and GitHub mirror branches are named by **pkgbase**. A split package can therefore have several pkgnames backed by one Git tree:

```text
pkgname:  foo-cli ─┐
                   ├─ pkgbase: foo ─ Git branch: refs/heads/foo
pkgname:  libfoo ──┘
```

For each seed cycle, the worker:

1. Finds metadata-indexed pkgnames that do not yet exist in the package repository.
2. Resolves each pkgname to its pkgbase and groups the pkgnames by pkgbase. If metadata has no pkgbase, it uses the pkgname as the fallback.
3. Excludes pkgbases previously recorded as too large for MongoDB's 16 MiB BSON-document limit.
4. Lists the mirror's branches once and intersects them with the target pkgbases.
5. Fetches the remaining pkgbase branches in batches into one persistent bare cache, at depth one.
6. Archives each fetched tree once, then calls `SeedFilesAsync` for every pkgname mapped to that pkgbase.

This replaces one network clone per pkgname with one batched request per group of pkgbases. It retains the existing seed validation and persistence path. In particular, each split pkgname is still seeded separately and receives its normal pkgname-specific revision identity.

### Git transport contract

The configured default mirror is `https://github.com/archlinux/aur`. The following are required assumptions, not incidental implementation details:

| Contract                                                                | Why it matters                                                                                                                                                    |
| ----------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| A mirror branch is `refs/heads/<pkgbase>`.                              | The worker must map pkgname to pkgbase before discovery and fetching.                                                                                             |
| Branch discovery uses `git ls-remote --heads`.                          | Official packages and other non-AUR index entries have no mirror branch and must not enter the bulk fetch list.                                                   |
| Fetch uses explicit refspecs, such as `+refs/heads/foo:refs/atoll/foo`. | Git protocol v2 can limit the advertised refs to requested prefixes; the cache keeps fetched trees addressable by pkgbase without creating normal local branches. |
| Fetch is `--depth=1 --no-tags`.                                         | Seeding only needs the current tree, not history or tags.                                                                                                         |
| File extraction is `git archive --format=tar refs/atoll/<pkgbase>`.     | The seeder reads a tree, not a checkout, and excludes Git-internal paths defensively.                                                                             |
| A failed multi-ref fetch is treated as an unsuccessful batch.           | A ref can disappear after discovery. The application bisects the batch until it can skip only unreachable refs and continue with the rest.                        |

The cache is a bare repository at `Atoll:Seed:Bulk:CachePath` (default `./data/aur-mirror`). Its fetched refs live in `refs/atoll/`; it is not a clone of every mirror branch.

### Plain-Git verification

Run the following on a machine with Git and network access. It does not require Atoll, MongoDB, or any application credentials. Use an empty disposable directory for `CACHE`; do not initialize a working repository there.

```sh
CACHE=/tmp/atoll-aur-mirror-check
MIRROR=https://github.com/archlinux/aur

git init --bare --quiet "$CACHE"
git -C "$CACHE" remote add origin "$MIRROR"
```

If checking the configured cache rather than a disposable one, use the configured `CachePath` and run `git -C "$CACHE" remote set-url origin "$MIRROR"` instead of reinitializing it.

#### 1. Verify branch discovery and pkgbase naming

List mirror branches using the same protocol mode and `--heads` filter as the application:

```sh
git -C "$CACHE" -c protocol.version=2 ls-remote --heads origin
```

Each line has this form:

```text
<commit-sha>    refs/heads/<pkgbase>
```

Choose one printed `<pkgbase>` and substitute it in the following commands. A pkgname that differs from its pkgbase is a useful split-package test case: the branch name must be the pkgbase, never the individual split pkgname.

To make a deterministic targeted check without scanning the output manually:

```sh
git -C "$CACHE" -c protocol.version=2 ls-remote --heads origin "refs/heads/<pkgbase>"
```

It must print exactly the requested branch for a mirror-resident pkgbase. No output means that the base is not available on the mirror; bulk mode will either count its mapped pkgnames as skipped or, when configured, use the direct-AUR fallback.

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

This is equivalent to one worker batch; its configured size is `Atoll:Seed:Bulk:BatchSize` (default `1000`, constrained to 10–10,000). Batches are spaced by `Atoll:Seed:Bulk:BatchDelayMs` (default `1000`, constrained to 100–60,000; values below 100 are clamped to 100 at runtime).

#### 3. Verify archive extraction

The worker streams this archive into a TAR reader. Inspect it directly:

```sh
git -C "$CACHE" archive --format=tar "refs/atoll/<pkgbase>" | tar -tvf -
```

To inspect a specific file without a working checkout:

```sh
git -C "$CACHE" archive --format=tar "refs/atoll/<pkgbase>" PKGBUILD | tar -xOf -
```

The archive must contain the source tree expected from the AUR package, including files such as `PKGBUILD` when present. It must be readable from the `refs/atoll/<pkgbase>` ref created above.

#### 4. Optional: compare mirror tree with direct AUR

This checks that the mirror supplies the same current tree as direct AUR for a selected base. Use a temporary directory and replace `<pkgbase>`.

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

No `diff` output and a zero exit status means the two archived trees match. A mismatch can be legitimate when the experimental mirror lags AUR; it is operational evidence to investigate, not a reason to change the mapping or refspec contract.

#### 5. Verify missing-ref handling and batch recovery

The worker prevents known missing refs from reaching `fetch` by intersecting targets with `ls-remote --heads`. Confirm why this matters by combining a verified base with a deliberately impossible one:

```sh
git -C "$CACHE" -c protocol.version=2 fetch --depth=1 --no-tags origin \
  "+refs/heads/<pkgbase>:refs/atoll/<pkgbase>" \
  "+refs/heads/atoll-verification-ref-does-not-exist:refs/atoll/atoll-verification-ref-does-not-exist"
```

This command must fail because the second remote ref does not exist. In production, `AurMirror.FetchAsync` responds by splitting the failed list in half recursively, fetching valid halves, and reporting only single unreachable refs as failed. This also handles a real branch deletion or rename that occurs between discovery and fetch.

Do not use a missing ref as a normal control path: discovery is the normal protection, while bisection is recovery for a race or unexpected Git failure.

### Bulk configuration and observability

Bulk mode is active only when `Atoll:Seed:Mode` is `Bulk`. It is mutually exclusive with Direct mode, so the two workers do not race to seed the same missing package.

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
        "AurFallbackForNotOnMirror": false
      }
    }
  }
}
```

`AurFallbackForNotOnMirror` applies only when a target pkgbase is absent from the mirror branch list. When enabled, each mapped pkgname is seeded through the existing direct-AUR path instead. It does not replace bisection for fetch failures among branches that were advertised by the mirror.

`GET /metrics` exposes `bulkSeed` status, including batch attempts/successes/failures, skipped and failed refs, seeded/skipped/excluded packages, and cycle timestamps. Use it with application logs to distinguish these outcomes:

| Symptom                             | Expected evidence                                                                                                            |
| ----------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| Target absent from mirror           | `refsSkipped` increases; direct fallback is used only if enabled.                                                            |
| Ref changed after discovery         | `refsFailed` increases after bisection; other refs in the original batch continue.                                           |
| Tree cannot be read                 | A `git archive`-related warning; mapped pkgnames are skipped for that cycle.                                                 |
| Package document exceeds BSON limit | `packagesExcluded` increases and the pkgbase is persisted in `seed-exclusions`, preventing repeated fetches in later cycles. |
| Nothing to seed                     | The worker waits five minutes before the next check.                                                                         |

### Cache lifecycle

The cache persists Git objects and fetched `refs/atoll/*` refs across cycles. It is intentionally retained so subsequent cycles do not repeatedly bootstrap a repository, but it has no automatic pruning or size limit. A full sync has historically been estimated at roughly 3 GB; actual growth depends on the mirror and how many refs change. Monitor the configured cache path and reclaim space deliberately according to the deployment's retention policy.

The mirror is upstream-experimental and can lag or change. Before changing code in response to a mirror incident, run the plain-Git checks above and record whether the failure is in branch discovery, fetch, archive extraction, or tree parity with direct AUR.
