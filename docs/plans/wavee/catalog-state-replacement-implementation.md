# Catalog state replacement — implementation

Status: implementation in progress. User approved replacement on 2026-09-06.
This supersedes the hydration facade architecture. No compatibility facade, dual
metadata owner, environment switch, or runtime legacy reader is part of the result.

## Ownership and flow

```text
 Detail / Home / Sidebar / Queue
             |
    query snapshots + demand leases
             |
         QueryService ---- ResourceCoordinator ---- provider transports
             |                                          |
       pure dependency joins                       typed observations
             |                                          |
 Catalog facts + Effective library replica + Queue occurrences
             ^                                          |
             +--------- DataCommitQueue <---------------+
                             |
                     one SQLite writer
                             |
                  persist, then publish once
```

Catalog owns metadata and ordered catalog relations. LibraryReplicaCoordinator
owns confirmed playlist/rootlist/collection membership and pending local intents.
Playback owns occurrences, order, cursor and authority. Queries join these owners;
rendering and query recomputation never call transport. LibrarySync remains the
authoritative protocol loop, with all rootlist writers routed through it.

## Shared contracts

Core remains BCL-only and AOT-safe. The implemented public contracts live in
`Wavee.Core/Catalog/Queries.cs` and `Resources.cs`.

```csharp
public abstract record QuerySpec<T>(CatalogScope Scope);
public readonly record struct RowWindow(string Collection, int First, int LastExclusive);
public enum QueryPriority { Prefetch, Visible, Playback }
public sealed record QueryDemand(bool Active, QueryPriority Priority,
    IReadOnlyList<RowWindow> Windows, IReadOnlyList<FacetKind> OptionalFacets)
{
    public IReadOnlyList<FacetKind> GlobalFacets { get; init; } = [];
}
public sealed record QueryStatus(bool HasPrimaryData, bool IsRefreshing, bool IsOffline);
public sealed record QuerySnapshot<T>(long Revision, long OrderRevision, T Value,
    QueryStatus Status, IReadOnlyList<FacetProblem> Problems)
{
    public ResourceError? Failure { get; init; }
    public IReadOnlyDictionary<ResourceKey, ResourceSnapshot> Resources { get; init; }
        = new Dictionary<ResourceKey, ResourceSnapshot>();
}
public interface IQueryHandle<T> : IDisposable
{
    QuerySnapshot<T> Current { get; }
    IObservable<QuerySnapshot<T>> Changes { get; }
    void SetDemand(QueryDemand demand);
    ValueTask<RefreshResult> RefreshAsync(CancellationToken ct = default);
}
public interface IQueryService
{
    IQueryHandle<T> Acquire<T>(QuerySpec<T> query);
    Task<QuerySnapshot<T>> ReadOnceAsync<T>(QuerySpec<T> query, QueryDemand? demand = null,
        CancellationToken cancellationToken = default);
}
```

Acquire is network-passive, permits asynchronous local reads, and replay subscribes.
Equivalent specs share state but have independent demand leases. Refresh targets
primary resources and the current demanded window/facets. Cancellation detaches a
caller, never another subscriber. Recompute discovers dependencies; a separate
demand planner schedules resources for active leases only.

Resource keys include provider, logical storage account, actual provider account,
locale, market, catalogue, tier, explicit-filter context, subject, facet and typed
arguments. Unknown imported context is represented explicitly. Request stamps
include session epoch, resource generation and request ID. Invalidation increments
generation; late responses cannot clear it. Subject supports entities and documents
(exact Home facet, search query/facet/page). Arguments use a versioned deterministic
codec, never runtime hashes or arbitrary serialized objects.

Knowledge: Unknown / Present / Absent / Unsupported. Activity: Idle / Queued /
Fetching / Backoff / Offline. Value, staleness and latest error are independent.
Present includes zero, false and empty collections. Only authoritative absence is
negative cacheable. 403, timeout, cancellation and unsupported are not absence.

Typed facet patches own fields. Explicit field presence distinguishes omission and
clear. Inline Home/search/cluster seeds only fill unknown fields and never establish
authoritative freshness. Artist/album names join references. Runtime playback
title/duration overrides stay in projection. No whole Track read-modify-upsert.

Catalog relations persist page order, occurrence identity (duplicate URIs allowed),
snapshot identity, source revision, total, coverage and relationship-only context.
Never mix pages across snapshots. Keep last good set during refresh and atomically
activate a validated replacement. Initial partial sets expose unknown positions.
Playlist membership belongs exclusively to the replica.

## Scheduling and commit

- Two metadata transport workers; current playback > visible > prefetch.
- Existing 300-URI / 4-MiB extended-metadata chunk limits; 4,096 queued resource keys.
- Drop prefetch before active demand; no unbounded per-row task fanout.
- One injected TimeProvider deadline scheduler; parked views remove demand.
- Timeout/5xx retry delays 2, 10, 30 seconds, then stop until explicit refresh,
  reconnect or relevant invalidation. Existing HTTP 429 middleware is sole retry
  owner for 429; no outer retry multiplication.
- Identity 1h, album detail 10m, artist overview 12h; extension offline TTL clamped
  60 seconds–24h (default 6h), genuine absence 24h unless facet-specific policy.
- Finite per-resource provider recipes replace the global hydration ladder.
- Raw ETags optimize transport, never veto invalidation. 304 requires matching
  bytes; otherwise do one unconditional fetch. Empty successful bytes survive.
- DataCommitQueue is one async serialized owner. No network in it, no monitor
  across await, no UI-thread SQLite. Decode outside; validate stamps and reduce
  against current state inside; transaction then immutable publication.
- Retained changes publish after durability. Memory-only facts use same ordering
  owner. No speculative dirty write-behind. Queue bounds: 1,024 commits / 16 MiB.
- Precise changed resource/relationship/aggregate keys replace URI-less bulk.
- Large row projections use structural sharing and reverse URI dependencies;
  immutable 256-row chunks avoid whole-10k-list metadata copies.

## Persistence v12 (schema reset)

New tables: catalog_scope, catalog_resource, catalog_relation_item, catalog_search,
extension_cache, replica_state, replica_recovery. Outbox adds intent state and
authenticated owner. Existing playlist/rootlist/collection tables become confirmed
baseline storage. Shows move from playlist membership to catalog episode relations.
User video overrides remain user-owned; cached video associations become a facet.

```sql
CREATE TABLE catalog_resource (
 scope_id INTEGER NOT NULL, subject TEXT NOT NULL, facet INTEGER NOT NULL,
 arguments TEXT NOT NULL, state INTEGER NOT NULL CHECK(state IN (1,2)),
 payload_version INTEGER NOT NULL, fmt INTEGER NOT NULL, payload BLOB,
 size INTEGER NOT NULL, revision INTEGER NOT NULL, fetched_at INTEGER NOT NULL,
 expires_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, last_access INTEGER NOT NULL,
 PRIMARY KEY(scope_id,subject,facet,arguments),
 FOREIGN KEY(scope_id) REFERENCES catalog_scope(scope_id),
 CHECK(state<>2 OR payload IS NULL)
) WITHOUT ROWID;
CREATE TABLE catalog_relation_item (
 scope_id INTEGER NOT NULL, parent_subject TEXT NOT NULL, facet INTEGER NOT NULL,
 arguments TEXT NOT NULL, ordinal INTEGER NOT NULL, occurrence_key TEXT NOT NULL,
 child_uri TEXT NOT NULL, context_version INTEGER NOT NULL, context_payload BLOB,
 size INTEGER NOT NULL,
 PRIMARY KEY(scope_id,parent_subject,facet,arguments,ordinal),
 FOREIGN KEY(scope_id,parent_subject,facet,arguments)
 REFERENCES catalog_resource(scope_id,subject,facet,arguments) ON DELETE CASCADE
) WITHOUT ROWID;
```

Use existing compression/framing and source-generated JSON. Persist present and
authoritative absence, not errors/tasks/epochs. Search is derived in the same commit.
The sole writer also owns replica transactions, touches, GC and barriers; a separate
read-only WAL connection serves asynchronous cold reads. Late cold reads cannot
replace newer accepted resources.

Reset, not migration: a `library.db` whose `meta.schema_version` is not 12 has every
catalog and replica table (and `outbox`/`dead_letter`) dropped and recreated empty.
`video_override`, `recent_surfaces`, `activity_log` and the `cache_budget_bytes` meta
key survive untouched. Nothing is converted — the catalog cache is re-fetchable and
the library replicas re-sync from Spotify on the first launch after the reset. A file
whose schema version is newer than 12 throws. The decision and reasoning (the v11
migration ran ~1,100 lines and 69 seconds on a real install to convert data that a
fresh sync replaces anyway) live in
`docs/plans/wavee/catalog-schema-reset-and-fetch-throughput-implementation.md`.

Retention: facts 30d, artist overview 7d, relations 14d, grace 15m, newest 50 recent
surfaces, day-granularity touches, GC batches <=1,000, current user cache budget.
Bound pin closure; one pinned artist must not pin every album tracklist recursively.

## Replica and playback

Fetchers return explicit Snapshot/Delta/Unchanged plus expected/head, rows/ops and
catalog observations. No store write or metadata await. On adoption: validate head,
form confirmed candidate, rebase pending edit-ID order, persist once, publish effective
once. Wrong-base delta requests one full read. New rows never use a stale old head.

Effective = confirmed + awaiting-verification overlays + pending overlays. Header
edits overlay catalog fields. Every full/diff/dealer/tuning/ack/rootlist/collection
write crosses the coordinator. A captured mutation attempt holds exact sent ops,
base/head, intent ID, account and epoch. Ack removes only matching intent, preserves
new coalesced intent, rebases others. Terminal failure removes overlay from latest
baseline, not an old rollback snapshot.

Ambiguous response/timeout: durable AwaitingVerification, never blind resend, block
later same-aggregate sends. Verify at 1/2/4 seconds. Resolve only acknowledged-head /
valid ancestry or operation-specific effect proof (minted IDs, keyed absence/order,
attributes). Full GET alone is insufficient. Exhaustion => durable NeedsAttention;
explicit refresh/reconnect can run another bounded verification. Pending creates
return local URI after local durability, separately exposing network completion.

Rootlist semantic intents build wire positions immediately before send in LibrarySync;
409 bootstrap runs inside handler (no enqueue-and-await-self). Remove RootlistLane.
Preserve folder markers and create->follow->initial-tracks ordering. Collections keep
existing token ledger/shields; commit validated walk+sweep+token atomically without
metadata. Incomplete walks may add observations but never remove/advance token.

Playback internal snapshots hold QueueOccurrence(ItemId, Uid, EntityUri, Bucket,
Provider, Kind, ContextUri, WireMetadata). Catalog joins once in PlaybackQueueProjection
and shared QueueQuery. Preserve public QueueEntry.Track/IPlaybackState/Connect wire.
Metadata updates do not advance structural queue revision. Pause/volume cannot
reinstall old track copies. Runtime overrides only current occurrence. Delete bridge
metadata asks/caps/repair and NowPlaying private metadata dedup/merge.

## UI and wireframes

One QuerySignalBinding owns stable signal, subscription, generation and one pending
UI post. Park removes demand and delivery; activation replays current once. Routes
dispose old handles. Recycled actions read current item. Reuse BoundItems/ItemsView.

```text
 PLAYLIST -- cached while refreshing
 +--------------------------------------------------------------------+
 | [cached art] Late-night rotation                  Updating...       |
 |              248 tracks           [Play] [Shuffle]                  |
 |--------------------------------------------------------------------|
 | #  TITLE                       ALBUM                    PLAYS  TIME |
 | 1  Known song                  Known album             82,140  3:42 |
 | 2  Another song                Another album                -  4:01 |
 | 3  [title loading..........]   [album loading.......]       -    -- |
 +--------------------------------------------------------------------+
 HOME -- expiry updates same retained card/shelf
 +--------------------------------------------------------------------+
 | Home                    [All] [Music] [Podcasts]                    |
 | [art] confidence baddie saturday night                              |
 |       Updating your daylist... [Play]                               |
 | Recently played                                     < page 2/4 >   |
 | [A]       [B]       [C focused / popup remains open]       [D]       |
 +--------------------------------------------------------------------+
                      accepted header / edition
 +--------------------------------------------------------------------+
 | Home                    [All] [Music] [Podcasts]                    |
 | [art] k-ballad korean ost sunday night                              |
 |       Updated window (or updating until known) [Play]               |
 | Recently played                                     < page 2/4 >   |
 | [A]       [B]       [C focused / popup remains open]       [D]       |
 +--------------------------------------------------------------------+
```

Playlist first demand 50 rows then actual realized range (already overscanned). Map
detail header/footer positions. Optional columns demand actual facets. Global sort/
filter requests only necessary fields across membership; unknown is not a filter
miss. Keep cached primary content; authoritative empty distinct from offline unknown.

Home documents retain section occurrence IDs/facet/window and entity references.
Daylist shared policy: exact deadline, active 60s revision cadence, 5s probe coalescing,
expired window remains stale with real title, unchanged-title revision still refreshes;
header updates every dependent surface even when Home requery fails. One affected-facet
requery per edition. Lagging expired header retry <=once/5min; resident changes apply
independent of that gate. No page timers/seen cache/identity epoch/overlay pass.

PagedShelf replaces count+captured-list with reactive bound items/live props; retain
pager, scroll, focus, callbacks and same item nodes. Keys are identity, not metadata.
Measured shelves always use measured virtual body; remeasure content revision and
width. Remove old overload after all callers migrate. Preserve sidebar planner,
unlisted pins fix and explicit user labels. Queue demand actual show-more slices.

## Startup and missing-metadata integration corrections

The first implementation missed consumer demand ownership for the sidebar and
saved Albums/Artists/Podcasts master lists. The root projections intentionally
requested membership only, but these consumers still assumed membership implied
loaded metadata. Detail-page queries happened to fill individual identities,
masking the omission whenever metadata was warm. Passing backend and projection
tests did not establish that mounted consumers issued the required demand.

The acceptance baseline must therefore begin with membership present and child
metadata absent. An ordinary screen must populate without opening its detail page.
Consumer tests also cover delayed membership, reordered display indices, filtering,
hidden/parked state and multiple simultaneous owners. No background repair loop is
an acceptable substitute for these contracts.

| Consumer | Query ownership | Required data |
| --- | --- | --- |
| Root LibraryStore | Passive metadata projection; library replica demand | Membership, counts, current accepted facts |
| Sidebar pane | Retained SidebarLibraryQuery; collection-specific windows | Visible names/artwork; only fields and kinds needed by current sort/filter |
| Saved master list | Retained SavedAlbums/Artists/ShowsQuery | Display-to-membership mapped viewport; shallow fields for sort/filter |
| Add/Move playlist picker | Retained PlaylistTargetsQuery | Rootlist and candidate playlist headers including edit rights and searchable names |
| Search cards | Retained SearchQuery | Visible card identity dependencies by entity kind, independent of inline seeds |
| Artist release shelves | Retained ArtistReleasesQuery | Thin paged release membership; visible release identities and global sorting fields |
| Now-playing next-up | Retained QueueQuery | The actual displayed next-up occurrences |

The Add/Move actions open the searchable picker directly. The previous synchronous
submenu could only display whatever happened to be cached. The picker retains
known targets while showing loading or retry for unresolved permissions. Unknown
membership or permissions cannot become a false empty result.

The shared scheduler processes resource demand in bounded 256-key waves. Removed
resource waiters detach when a viewport changes; resources required by another
handle retain their waiter. A fully parked node releases replica demand too.
Capacity deferral is retried after coordinator capacity becomes available, while
offline deferral waits for reconnect. Query-level failures cover replica reads
that have no associated catalog resource; they cannot disappear into an empty
per-resource error list.

## Implementation checklist / lanes

- [ ] Core contracts, normalized catalog and finite scheduler (catalog agent)
- [ ] Shared query service, source migration and composition roots (orchestrator)
- [ ] v12 persistence (schema reset) and sole writer (orchestrator, then catalog agent)
- [ ] Replica/fetcher/rootlist/outbox changes (sync agent)
- [ ] Playback occurrence/projection migration (sync agent after replica)
- [ ] Reactive shelves and mounted gates (UI agent)
- [ ] Query UI/Daylist/sidebar migration (UI agent after shelf)
- [ ] Delete all obsolete hydration/store repair/source facade paths
- [ ] Update architecture/skills and full verification

Only orchestrator builds/tests/launches. No subagent git state changes. Private fenced
paths remain unread/unedited. Preserve other current changes. No issue/PR publication
is implicit in implementation.

## Verification

Behavioral tests: patch lost-update/default/clear, failure outcome end-to-end, per-facet
TTL, force race/epoch fence, shared waiter cancellation, retry bound, valid empty vs
404 vs403, cold-load race, atomic persistence failure, migration resume/order/partial/
pending/account, no whole-cache replay, membership blocked metadata independence,
atomic pending overlays, wrong-base delta, ack/coalescing, uncertain intent verification,
rootlist races, duplicate queue URI then pause/volume/reorder, precise query fanout,
park/replay/range reorder, mounted shelf node/pager/focus/popup/recycling/measurement,
daylist same-URI/revision/expiry/lag/failure. No source-text tests.

```powershell
dotnet build Wavee.slnx
dotnet build Wavee.slnx -c Release
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj
Invoke-Pester -Path ops/release/tests
# sibling engine
dotnet build src/FluentGpu.slnx
dotnet build src/FluentGpu.slnx -c Release
dotnet run --project src/FluentGpu.VerticalSlice
powershell -File docs/design/check-canon.ps1
```

Baseline research: 435 selected tests passed before implementation. Full gates and
live smoke remain required; never report this baseline as implementation acceptance.
