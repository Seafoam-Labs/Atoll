# Caching overview

MongoDB is the only authoritative state. Everything documented here is derived and rebuildable, in memory or on
disk, and the deployment is single-instance. This is the canonical inventory: consult it before adding a cache or
changing an invalidation path.

The shared HybridCache TTLs come from `Atoll:Caching` (`RankTtlSeconds` 600, `SnapshotTtlSeconds` 600,
`DashboardTtlSeconds` 5) and are read once when a consuming service is constructed, so changing them needs a
restart. Rank and snapshot are a backstop, not the freshness mechanism: they sit well above the 5-minute index
swap cadence, `PackageIndexWorker` warms the seeded snapshot, the default catalog sorts, and the ranked name
list at startup, and writes invalidate by tag, so a warm entry is not meant to expire into a request. Their
`[Range]` attributes are enforced at startup: `AtollOptions` marks each nested section with
`[ValidateObjectMembers]`, which is what makes `ValidateDataAnnotations` descend into it.

## Inventory

| # | Cache | Location | Contents | Refresh / invalidation | Max staleness |
| --- | --- | --- | --- | --- | --- |
| 1 | Search index snapshot | `PackageIndexStore` (`Services/Catalog/Indexing/`) | Whole AUR catalog as `SearchIndexData` (`ByNames`, `ByProvides`, `ByWords`) | Startup prime from the `aur-metadata` collection, then an atomic swap every `Atoll:DataSource:RefreshIntervalMinutes` (default 5) | Refresh interval; indefinite while dump downloads fail (the last good snapshot is kept) |
| 2 | Ranked name views | `PackageIndexRanker` (`Services/Packages/`) | Seeded name list plus one sorted name array per (sort, order) | HybridCache keys `atoll.rank.names` and `atoll.rank.sorted/{sort}/{order}` under the `catalog` tag only: seed/delete drop it; `Atoll:Caching:RankTtlSeconds` backstop | One TTL (600 s); about two when a write races an in-flight store; an index swap is not seen until expiry or a write |
| 3 | Catalog sorted views | `PackageCatalogService._sortedViews` (UI) | `AurPackageMetadata[]` pre-sorted for each catalog sort, keyed on the index instance | Rebuilt per index generation; dropped structurally when the replaced `SearchIndexData` becomes unreachable | Same as #1 |
| 4 | Catalog seeded snapshot | `PackageCatalogService` (UI) | `FrozenSet` of seeded names plus a `FrozenDictionary` of head scan statuses | HybridCache key `atoll.ui.seeded-snapshot` under both `catalog` and `head-status`: seed/delete drop `catalog`; a queued head rescan and a completed or errored head scan drop `head-status` | Immediate for seeds, deletes, rescans, and scan completions; for a refresh-promoted head, up to one TTL (600 s) of stale badge display, accepted because access gating reads live statuses; a build racing a tag removal serves its pre-write value for up to one TTL |
| 5 | Status dashboard model | `StatusDashboardService` (UI) | One assembled `StatusDashboardModel` | HybridCache key `atoll.ui.status-dashboard`, no tags, `Atoll:Caching:DashboardTtlSeconds` TTL, single-flight | One TTL |
| 6 | Git bare repos | `GitRepositoryCache` under `Atoll:Git:RepositoriesPath` (`data/repos/`) | Materialized cloneable history per package | The `.atoll-head` marker is recomputed from MongoDB on every Git request; a mismatch rebuilds lazily under a per-path lock | None (checked per request); changes cost a rebuild inside request latency |
| 7 | AUR mirror | `AurMirror` under `Atoll:Seed:Bulk:CachePath` / `Atoll:Refresh:CachePath` (`data/aur-mirror/`) | Bare shallow mirror, refs under `refs/atoll/<pkgbase>` | Shared by bulk seeding and refresh; a pkgbase is fetched again when its upstream SHA moves or the `MaxStalenessHours` (24 h) sweep fires | Refresh cadence, up to 24 h |
| 8 | AUR metadata dump copy | Mongo `aur-metadata` collection | Persisted dump for warm starts and `304` cycles | Rewritten on every successful download | Same as #1 |
| 9 | HTTP validators | `PackageIndexUpdater._etag` / `_lastModified` | Conditional-request headers for the dump | Updated per full download; deliberately withheld while a suspicious shrink awaits confirmation | Refresh interval |
| 10 | Process-local counters | `SecurityScanStatusStore`, `DirectSeedStatusStore`, `BulkSeedStatusStore`, `RefreshStatusStore`, updater status | Worker progress gauges for `/metrics` and `/status` | Written by the workers; never reloaded from Mongo | Reset on restart |

The mirror's fetch contract and cleanup guidance are in [SYNC.md](SYNC.md#cache-lifecycle). Not caches, often
mistaken for them: `AurGitPackageSource` clones into a temp directory per direct seed and deletes it;
`GitRepositoryCache.RepoLocks` is lock bookkeeping (grows once per repository path, never evicted);
`FileBrowser.razor`'s tree dictionaries are per-circuit render state.

## Invalidation rules

The keyed caches (#2, #4, #5) share one pattern: `GetOrCreateAsync` with keys from `AtollCacheKeys`, TTLs from
`Atoll:Caching`, and tag removals through `HybridCache.RemoveByTagAsync` directly. There is no wrapper method.

- **Rule of thumb:** writes that change seeded names drop `catalog`; writes that change a head's scan status drop
  `head-status`, guarded on `isHead` (a non-head revision can change neither). The catalog snapshot carries both
  tags; the ranked name views carry only `catalog`, so status writes never re-rank warm pages.
- **Drop sites:** `PackageService.SeedFilesAsync` and `DeleteAsync` drop `catalog`;
  `PackageSecurityStatusService.QueueRescanAsync` (the UI rescan button and REST alike) and `PackageSecurityWorker`
  after a completed or errored head scan drop `head-status`.
- **Deliberately not invalidated:** `AppendRevisionFromUpstreamAsync` promotes heads without a tag drop, because
  refresh appends in long bursts and the scan following each append drops `head-status` anyway, so those heads ride
  the snapshot TTL (up to 600 s; the badge lags, access gating does not). Scan completions that store nothing (the
  revision is gone, or the claim turned out stale) do not invalidate either. `RequeueOutdatedAsync` and the startup
  backfill flip many statuses while the cache is cold.
- **Last-wins merge:** a head promotion leaves two `isHead` documents for one package until the old head is demoted,
  so the snapshot's head-status merge is deliberately last-wins instead of a duplicate-key throw.

Accepted behaviors to know before changing this:

- A tag removal cannot cancel an in-flight factory: a build racing the removal stores its pre-write value and serves
  it for up to one TTL.
- `head-status` drops are unthrottled, so a scan sweep with sustained catalog reads rebuilds the snapshot at the
  head-completion rate (two collection reads per rebuild). A time-floor guard is the known follow-up if load shows
  it.
- On the ranked paths a store racing a tag removal can serve stale membership for about two TTLs, since a sorted
  entry can be built late in the window from stale cached names.

## Where stale data can appear

Ranked by how likely you are to notice.

1. **Search index is second after Mongo.** A package seeded into MongoDB before the next index refresh is cloneable
   and has REST history, yet is invisible to search, RPC info, and the package details page, because those gate on
   `ByNames`. This Mongo-first, index-second ordering is the sharpest edge in the system. A failed dump download
   keeps the previous snapshot indefinitely (logged, intentional). Upstream deletions are pruned only after two
   consistent snapshots confirm the shrink and only when `Atoll:DataSource:PruneDeletedPackages` is enabled (default
   false), so removed-upstream packages linger on purpose.
2. **Ranked pages on `/v1/packages`.** Totals and page counts describe the cached name list, not live Mongo; a
   package deleted between the ranking build and the page fetch shortens the page instead of re-ranking. The default
   `name asc` path pages Mongo directly and is always fresh on membership; only its enrichment comes from the index
   snapshot. Different sorts can disagree slightly on freshness within a TTL.
3. **Mirror content.** Seeded file content reflects the mirror at seed/refresh time. A branch whose upstream SHA has
   not moved skips fetching; `MaxStalenessHours` (24 h) is the only force-refresh, so already-seeded packages lag
   upstream until the refresh worker runs (`Atoll:Refresh:Enabled`).
4. **Git bare repos (correctness-safe, latency-exposed).** The marker is recomputed per request, so a rescan that
   flips Verified to Flagged, a revision trim, or a security toggle triggers a lazy rebuild on the next clone or
   advertise. Caveats: missing revision content leaves the marker unwritten, so every request re-attempts
   materialization; the marker lives inside the repo directory, so hand edits to `data/repos/` are trusted as up to
   date.
5. **Smaller windows.** The dashboard's 5 s TTL covers already-approximate sources (the page stamps its assembly
   time). After a restart the index comes from the `aur-metadata` copy, so search reflects the last successful
   download, not startup time. Worker counters reset on restart; `/metrics` gauges start from zero.
6. **Memory-retention nuance.** The catalog sorted views' `ConditionalWeakTable` entries drop only when the old
   `SearchIndexData` instance becomes unreachable. A long-lived reference to an old index pins its sorted views with
   it. Stale-serving is impossible (keys are the index instance), but a leak shows as memory, not wrong data.

## Structures mapped to meaning

- `ImmutableDictionary` / `ImmutableHashSet` (`SearchIndexData`): the immutable search index snapshot.
- `FrozenSet` / `FrozenDictionary` (`SeededSnapshot`): point-in-time seeded names and head scan statuses.
- `ConcurrentDictionary`: per-sort catalog views (inside the weak table), git per-path locks.
- `ConditionalWeakTable<SearchIndexData, ...>`: the catalog sorted views' generation mechanism. A generation is an
  index instance; swapping the store drops entries structurally instead of tracking versions.
- `HybridCache` (`AtollCacheKeys`): the ranked names, the seeded snapshot, and the dashboard model. One library
  pattern, with tag-removal invalidation and `[ImmutableObject(true)]` payload records so warm L1 reads are
  reference-shared instead of serialization-cloned.

## Verified non-cached paths

Per-request reads with no TTL anywhere: security gating (`PackageSecurityAccess` via `PackageSecurityFilter`, also
enforced in the service layer for Blazor circuits), security history and rescan validation, package
files/history/details pages, Git serving's Mongo existence checks, RPC and search lookups (a fresh snapshot
reference per request), and metrics gauges.

## Extending

- Add keys and tags only through `AtollCacheKeys`; keys are derived consts/enums, never user input.
- Tag invalidation is logical: an invalidated entry keeps occupying cache until natural expiry, and the next read
  treats it as a miss and reruns the factory, so freshness on the invalidating instance is immediate.
- Cached payloads that should be shared by reference must be sealed records marked `[ImmutableObject(true)]`;
  otherwise every warm L1 read pays a serialization clone, inner collections included.
- L2 is not wired. `AddHybridCache` raises `MaximumPayloadBytes` to 16 MB because an oversized L1-only entry still
  stores and serves but logs one Error per store. If L2 is ever added: `FrozenSet`/`FrozenDictionary` cannot
  round-trip through System.Text.Json, so those payloads need a custom serializer or `Immutable*` types, and
  invalidation reaches only the local server plus L2 (no backplane for other servers' L1).
- `Atoll.Api.Tests/Support/HybridCacheSemanticsTests.cs` pins the library behaviors the design leans on (payload-cap
  logging, `[ImmutableObject]` reference-sharing, tag removal racing an in-flight store, factory-token semantics)
  against a real DI-built `DefaultHybridCache`; do not mock `HybridCache`. Per-test instances come from
  `TestHybridCache.New()`.

**Why the remaining caches stay custom.** HybridCache models keyed payloads with TTLs and tags, which fits exactly
caches #2, #4, and #5 and has collapsed their bespoke gate/epoch/TTL plumbing into one pattern. The snapshot-swap
caches (#1, #3) work by substituting one immutable `SearchIndexData` instance and keying derived views on that
identity, which the library does not model: as entries they would need explicit keys and invalidation on every
swap, and with an L2 they would serialize the full catalog per node past the payload cap. The disk, Git, HTTP, and
process entries (#6 to #10) are not keyed payloads at all; each has its own owner and lifetime. And HybridCache
does not make a cache multi-instance: invalidation and factory coalescing are per process, with no cross-server
backplane, so scale-out still depends on the single-instance workers and local-disk constraints.
