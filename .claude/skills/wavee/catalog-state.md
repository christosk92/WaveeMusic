# Wavee catalog state and live queries

Read this before adding a catalog read, changing a view subscription, or introducing a provider. The current design
is [architecture.md](../../../docs/plans/wavee/architecture.md); the approved implementation contract is
[catalog-state-replacement-implementation.md](../../../docs/plans/wavee/catalog-state-replacement-implementation.md).
An older `library.db` schema is reset (dropped and rebuilt empty), never converted — see that doc's "Persistence
v12 (schema reset)" section and
[catalog-schema-reset-and-fetch-throughput-implementation.md](../../../docs/plans/wavee/catalog-schema-reset-and-fetch-throughput-implementation.md).
The hydration facade, completeness levels, trait ladders, mutable entity store, and per-surface repair loops are
superseded. Do not restore their interfaces under another name.

## Ownership

| State | Owner | Consumer |
|---|---|---|
| Identity, header, optional facts, ordered catalog relations, Home/search documents | `CatalogRepository`, keyed by full `ResourceKey` | Pure `CatalogReadView` and typed query definitions |
| Playlist/rootlist/collection confirmed state and pending operations | `LibraryReplicaCoordinator` | Effective replica snapshots joined by queries |
| Durable transaction ordering and atomic publication | `DataCommitQueue` | Catalog, replicas, curation persistence |
| Requests, in-flight coalescing, priority, retries and freshness | `ResourceCoordinator` + `ResourcePolicy` | Explicit active query demand or finite backend reads |
| Current immutable joined result and exact dependencies | `QueryService` node | `IQueryHandle<T>` leases |
| UI dispatch, active/parked lifetime and visible range | `QuerySignalBinding<T>` and view | Stable signals and bound controls |
| Selection, editing draft, focus, drag geometry, scroll and pager | View/engine control | Local interaction only |

The residual `IStore` is an effective replica projection. It is not a catalog read/write port or a second cache.
`CatalogCacheMaintenance` manages normalized cold/resident cache data; video attachments have their own durable
curation port. Clearing metadata must preserve user curation and replica state.

## Read and demand are different operations

`Acquire(QuerySpec<T>)` creates or shares passive observation. It may load explicitly consumed facts from the
local cache; it does not start network requests. `Current` is always an immutable snapshot and `Changes` replays
it once, then publishes ordered revisions. Disposing the last lease releases query work.

`SetDemand` is the network permission from a view's actual needs. A page demands its **whole model** — never a
viewport window. `Facets` are the columns/sort/filter fields applied to every row. `QueryDemand.Initial` is an
active Visible demand with no extra facets (identities + header). `QueryDemand.None` permits no network work.
The query/catalog layer fetches required keys in extended-metadata batches of 300 subjects per POST, with up to
`ResourceCoordinator.MaxInFlight` (4) batches in flight. Only rendering (`ItemsView`) is virtualized. Search
server paging (`QueryPageWindow`) is the named exception.

```csharp
var binding = new QuerySignalBinding<Playlist>(
    svc.Queries.Acquire(new PlaylistDetailQuery(svc.CatalogScope, playlistUri)),
    post,
    snapshot => RenderPublishedPlaylist(snapshot));
binding.SetDemand(new QueryDemand(true, QueryPriority.Visible, renderedFacets));
binding.SetActive(active.Peek());

// KeepAlive park / resume; unmount disposes the binding.
binding.SetActive(false);
binding.SetActive(true);
binding.Dispose();
```

`SetActive(false)` submits `None`, suppresses UI delivery, and invalidates queued posts. Activation restores the
saved demand and delivers the latest canonical snapshot once. Unmount disposes both subscription and handle.
A scope/account change disposes the old binding and acquires a new typed spec; never reuse the pre-login scope.
`Services.CatalogScope` reads the UI-posted scope signal. Use `UsePost`, not direct signal writes from backend threads.

`ReadOnceAsync(spec, demand, ct)` is the finite backend/service path. It waits for local preparation, cold reads,
and the bounded dependency closure. `RefreshAsync` explicitly revalidates that closure; failures remain typed
problems alongside the last good data. A query publication is never permission to start an ad-hoc HTTP call.

## Adding a query or facet

1. Add a concrete record in `Wavee.Core/Catalog/Queries.cs`. Reuse a domain DTO where it expresses the view.
   No string query language, reflection mapper, arbitrary property bag, or generic UI data framework.
2. Implement a definition in `Backend/Queries`. `Read(QueryReadContext)` and `Requirements(value, demand)` are pure:
   no HTTP, SQLite, tasks, timers, signal writes, or synchronous waits. Register every consumed catalog/replica
   dependency even when a memoized joined row is reused. Snapshot collections and indexers must not read live stores.
3. Add a `FacetKind` and a closed `CatalogValue` only when the data has distinct authority/freshness. The provider
   returns a patch for owned fields plus optional inline child seeds. `FieldChange` distinguishes omitted fields
   from explicit null/empty values. Zero duration/count and an empty descriptor array can be real answers.
4. Register the provider through its declared source capabilities. `EntityUri.Parse` is the URI vocabulary;
   do not duplicate provider/kind prefix rules or mix two providers' ownership.
5. Verify behavior using real catalog/replica/query fixtures with fake transport/clock: overlapping demand,
   cancellation, cold/offline data, stale epoch, explicit absence, and unchanged occurrence order on metadata updates.

Finite asynchronous local search preparation uses `IAsyncQueryDefinition<T>` and returns a new immutable pure
definition. The node owns cancellation and generation fencing. Cold promotion/activity recomputes joins without
re-running SQL search preparation; durable relevant changes may invalidate preparation.

## Knowledge and presentation

`Knowledge.Unknown`, `Present`, `Absent`, and `Unsupported` are independent of request activity and error. Missing
bytes are unknown; only an explicit provider absence is negative-cacheable. A request failure never clears the
last good value. Inline seeds fill unknown fields and do not promote themselves into fresh authoritative answers.

`QueryStatus.HasPrimaryData` decides initial readiness, not `Count > 0`, whether all child names arrived, or whether
optional columns succeeded. Known empty membership is ready; unknown membership is pending. Playlist headers can
paint before rows using `Playlist.MembershipLoaded`. `Resources` exposes the exact snapshot knowledge for field
availability. `Problems` carries failures/absence/unsupported facts; it is not a list of every unknown field.
`QueryPresentationRules.InitialFailure` surfaces terminal initial failure/offline-without-cache while preserving
primary data on optional failure. `QuerySignalBinding.Failure` exposes an unexpected terminated observable on the
UI thread and retains its previous snapshot.

## Lists, documents and generated playlists

Occurrence identity is separate from entity identity: a playlist or queue may contain the same URI more than once.
Keep occurrence keys and order revisions stable when only title, artwork, contributor, play count, or availability
changes. `RelationProjection` combines a contiguous compatible relation snapshot and retains the previous one while
replacement pages arrive. Numeric next cursors equal to the offset normalize to offset-only keys; opaque cursors
remain meaningful. HomeSection has a local first-page generation barrier because its wire response supplies no
shared snapshot token: old cached/in-flight tails must be revalidated before replacement publication.

Home is an ordered source document, not a mutable overlay map. Its cards join current identity/header facts by URI.
A generated playlist may keep its URI and title while its `Edition` or `NextUpdateAt` changes. `ActiveCatalogDemand`
shares generated-playlist deadlines and revision probes across active leases; it revalidates the header without
refreshing or rebuilding Home. `DaylistNotifier` observes accepted authoritative header facts. Toast schedule
reconciliation is an external-system responsibility, not an application metadata repair loop.

## FluentGpu consumption

Read the sibling engine's `component-props-contract.md`. A plain factory field freezes at mount; explicit re-pushed
props are live. `PagedShelf.Create(items, (item, index, width) => ..., keyOf: (item, index) => occurrenceKey)` accepts
an immutable snapshot and projects it once into its retained `BoundItemsSource<T>`. Build actions from the passed
current item. Do not index an old captured list. Metadata updates keep the shelf, pager, focus, popup and viewport;
only an actual component identity change calls for a new key. `Responsive` also re-pushes its builder.

For direct virtualized rows use stable `BoundItems`/`ItemsView.CreateBound` sources. Updating source contents is a
source revision, not a reason to remount the list. Layout configuration documented as mount-only still requires
an intentional configuration boundary; live props do not make every constructor argument dynamic.

## Commit and cache discipline

`DataCommitQueue` stages/reduces serially, persists, then publishes all owner state atomically. Pure query reads
share its short publication gate. Notifications are deferred until the outermost publication ends, including
same-thread reentrancy. Never await or invoke network work under that gate. Observer exceptions cannot turn a
successful durable commit into an apparent failure.

Durable membership and pending intents are one transaction. Inline mutation metadata enters the catalog in that
same transaction with explicit provenance. UI editors retain drafts only; they do not repair shared playlist
models or the store after a command. Await `PlaylistCreated.Staged` before navigating to its optimistic playlist.

Catalog acceptance is fenced by account/context/epoch/request generation. Cold rows from a different account are
not a current baseline. Deep playlist baselines load lazily through `EnsurePlaylistCachedAsync`, not at bootstrap.
Current demand keys are retention pins; ordinary inactive metadata remains evictable. Use the normalized cache
maintenance API rather than resurrecting `CachedStore` or raw SQLite writes from UI.

## Verification

Only the orchestrator builds/tests/launches. Add behavioral tests, never source-text tests. Request counts must
follow whole-model demand batched at 300 subjects per POST (up to 4 in flight), not a viewport window. Backend
tests establish state/transport behavior; retained UI props/pager/focus/measurement require mounted engine gates.
Do not claim those UI properties from an engine-free projection test.
