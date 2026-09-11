# Spotify pin sync (`ylpin`) — implementation plan

**Status:** M1 (read/hydrate) and M2 (write-back + convergence) implemented and tested; M3 (polish) partly done — the
doc updates below landed, header hydration for pinned non-followed playlists did not (see §4). **Scope:**
`src/apps/Wavee` (Backend + App + Features/Sidebar), `Wavee.Core` (one enum member), `Wavee.Tests`. No engine
changes. **Issue:** file one before starting (`github-triage`); every CHANGELOG bullet / commit body references it.

**Deviation from §1.5:** `SidebarPinSync`'s ctor takes the `SidebarPinStore` (`svc.Sidebar.Pins`) directly rather
than the full `SidebarPreferences`, and gains its own `Activate(Action<Action> post)` method (the same pattern
`SidebarPreferences.Activate` uses) instead of taking `post` as a ctor arg — `SidebarPreferences` itself is
engine-bound (FluentGpu.Hooks/Signals + the whole Curated/undo/document graph) and is NOT source-included into
Wavee.Tests, so depending on it directly would have made `SidebarPinSyncTests` impossible without pulling in the
engine. `SidebarPinStore` already is source-included and already carries everything the bridge needs
(`ApplyRemote`, `OnLocalPinChanged`), so nothing about the design changes — only which type it holds a reference to.
`SidebarPreferences.ApplyRemotePins` was accordingly never added (the plan's proposed one-line forwarder); the
bridge calls `pins.ApplyRemote` directly.

**§5 (Liked Songs wire uri) status:** NOT captured — a `--spotify-sync` run against a live account with Liked Songs
pinned needs a human with credentials. `PinSyncRules.TryWireUri`'s `"liked"` arm ships with the plan's best-supported
guess (`spotify:user:{username}:collection`) and a `TODO(pin-sync-liked-uri)` comment; `SpotifyLibrarySync.Sets` now
includes `"pins"` and logs the first 5 pin uris (§1 step 8) so the next `--spotify-sync` run confirms or corrects it.

Wavee's sidebar pins are purely local presentation state (`SidebarPinStore` → `sidebar-layout.json`). Spotify keeps
the same concept in the collection2v2 service as the wire set **`ylpin`** ("Your Library pins"). Today nothing reads
it (startup hydration, dealer push) and nothing writes it (pin/unpin). This plan wires both directions through the
plumbing that already exists for the other five collection sets, in three ordered milestones.

---

## 0. RCA — verified against the tree on 2026-09-05

All line numbers below were re-checked; the earlier investigation's references hold except where marked **(moved)**.

| Fact | Where |
|---|---|
| Both dealer forms (`hm://collection/ylpin/<user>` protobuf and `…/json`) reach `DealerRouter.OnCollection` and are enqueued as `SyncKind.CollectionPush` with wire set `"ylpin"` (`CollectionSetFromTopic` takes the first path segment). | `src/apps/Wavee/Backend/Realtime/DealerRouter.cs:48`, `:214-230` |
| `LibrarySync.CollectionPushAsync` — a parseable `PubSubUpdate` with items direct-applies; otherwise `LogicalSetsForWireSet(wireSet).Count == 0` → `LogUnknownWireSetOnce` → **drop**. | `src/apps/Wavee/Backend/Sync/LibrarySync.cs:757-778`, log line `:852-856` |
| `CollectionSets.WireSets = { "collection", "artist", "show", "listenlater" }` — `ylpin` absent → never hydrated at boot (`InitialHydrateAsync` `:363`), reconnect (`:1000`) or reconcile (`:1053`). `LogicalSetsForWireSet` has no `ylpin` arm. | `src/apps/Wavee/Backend/Collections/CollectionSets.cs:15`, `:56-63` |
| Pin/unpin path: `PinActions.Pin` → `SidebarPreferences.Pin` → `SidebarPinStore.Pin` → `Bump()` → `OnChanged` (= `SidebarPreferences.Commit`, JSON write). No network call anywhere. | `src/apps/Wavee/Actions/PinActions.cs:99-111`; `Features/Sidebar/SidebarPreferences.cs:102`, `:422`; `Features/Sidebar/SidebarPinStore.cs:63-71`, `:160-164` |
| The "pin is local presentation state with no server side" decision is documented in `PinActions.cs:17-21` and deferred in `docs/plans/wavee/library-sync-implementation-plan.md:911`. | — |
| Existing write plumbing: `SetReplayStrategy.Replay` POSTs `/collection/v2/write` with the vendor media type, body from `CollectionWriteMapper.BuildWrite(account, setId, uri, saved, nowSeconds, cuid)`, which maps `setId` through `CollectionSets.WireSet`. Accepted cuids go into `CollectionEchoRing`. | `src/apps/Wavee/Backend/Mutation.cs:69-82`; `Backend/Collections/CollectionWriteMapper.cs:12-20`; `Backend/Collections/CollectionEchoRing.cs` |
| `KindForLogicalSet` is a private switch in `LibrarySync` (not in `CollectionSets`). | `LibrarySync.cs:825-833` |
| Tests asserting the CURRENT (wrong) behaviour: `LibrarySyncTests.CollectionPush_WireSetTranslation_CollectionFetchesBoth_UnknownFetchesNothing` uses `"ylpin"` as its "unknown" set; `CollectionFetcherTests.FetchWireSet_UnknownWireSet_Throws` does too. | `src/apps/Wavee.Tests/LibrarySyncTests.cs:672-692` (`:686-691` is the ylpin half); `Wavee.Tests/CollectionFetcherTests.cs:111-117` |

### 0.1 Wire format — what the write body needs (verified from the retired app in git history)

The question "does the write body need `uri`-form items or the 16-byte identifiers seen in the protobuf push" is
answered by the retired WinUI-era code, still in this repository's history (parent of `1a3b96bd`,
`src/Wavee/Core/Library/Spotify/SpotifyLibraryService.cs`):

```csharp
private const string YlPinSet = "ylpin";
…
var item = new CollectionItem { Uri = uri, AddedAt = (int)addedAt, IsRemoved = false };
await _spClient.WriteCollectionAsync(username, YlPinSet, new[] { item }, ct);      // POST /collection/v2/write
…
await SyncCollectionAsync(YlPinSet, SpotifyLibraryItemType.YlPin, "pins", null, ct); // /paging + /delta, same as every set
```

So: **the write is an ordinary `WriteRequest { set = "ylpin", items = [CollectionItem { uri, added_at(seconds),
is_removed }] }`** — string `spotify:` URIs, exactly what `CollectionWriteMapper.BuildWrite` already emits. The
`/paging` and `/delta` responses for `ylpin` are ordinary `CollectionItem { uri, added_at, is_removed }` too (the
retired app fed them through the same `SyncCollectionAsync` as every other set and then ran a mixed-type metadata
fetch because a pin set mixes playlists/albums/artists/shows).

The **dealer push payload for `ylpin` is opaque** in practice. The retired `LibraryChangeManager` (same commit,
`src/Wavee/Connect/LibraryChangeManager.cs:60-89`) never decoded it; for `set == ylpin` it emitted a bare "changed"
event on BOTH the empty/binary form and the `/json` form, and the orchestrator re-ran the token-gated delta
(`docs/plans/wavee/library-sync-fix-proposal.md:522`). This plan does the same: **`ylpin` pushes never
direct-apply; they always take the settle + delta-fetch path** (§2.3). That also dodges the risk of a
`PubSubUpdate` that "parses" with a binary blob in `uri` being folded into the store.

**One open fact — the Liked Songs pin.** The `/json` push describes it as `{"type":"collection"}` with no
identifier; the URI the `/paging` response carries for it is not in any capture we hold. §5 makes confirming it a
concrete first task (the `--spotify-sync` CLI prints the set). Until then `PinSyncRules` accepts any spelling
`EntityUri.IsLikedCollection` recognises (`spotify:collection:tracks`, `spotify:user:<u>:collection`,
`spotify:user:<u>:collection:tracks`) on the read side and writes `spotify:user:{username}:collection` — the one
form Spotify's own clients use for the user-namespaced collection.

---

## 1. Design

### 1.1 The shape in one picture

```
                 ┌───────────────────────── server truth ─────────────────────────┐
  Spotify client │  collection2v2  set="ylpin"   (membership + added_at only)      │
                 └───────┬──────────────────────────────────────▲─────────────────┘
        dealer push      │ /paging, /delta                       │ /collection/v2/write
   hm://collection/ylpin │ (CollectionFetcher)                   │ (SetReplayStrategy, cuid → CollectionEchoRing)
                         ▼                                       │
             IStore saved set  "pins"   ◄── SetSaved(Pending) ── MutationEngine.Save("pins", uri, saved)
             (SqliteColdStore collection_items, token "ylpin")          ▲
                         │ Changes (Kind = CollectionKind.Pins)         │ SetPinnedAsync
                         ▼                                              │
             ┌──────────────────────── SidebarPinSync (App, UI-thread via post) ────────────────────────┐
             │  remote → local:  store.SavedItems("pins") ─PinSyncRules.TryPinId─► pins.ApplyRemote(…)  │
             │  local  → remote: pins.OnLocalPinChanged ─PinSyncRules.TryWireUri─► mutations.SetPinned │
             └──────────────────────────────────────────────────────────────────────────────────────────┘
                         ▲                                              │
                         │ ApplyRemote (NO OnLocalPinChanged)           │ Pin/Insert/Unpin (user intent)
                         ▼                                              ▼
                     SidebarPinStore  (order = local, membership ⊇ server for syncable kinds)
                         │ OnChanged → SidebarPreferences.Commit → sidebar-layout.json (unchanged)
```

Two rules make it safe:

1. **The store's `"pins"` set is the mirror of the server; `SidebarPinStore` stays the UI's list.** Order, folders,
   app routes and `wavee:` playlists live only in `SidebarPinStore`. The mirror contributes membership for the
   syncable kinds and nothing else.
2. **Only user intent writes.** `SidebarPinStore` grows a second event, `OnLocalPinChanged`, raised by `Pin`,
   `Insert` and `Unpin` only. `ApplyRemote`, `LoadFrom`, `Move` and `Touch` never raise it. That — plus the echo
   ring, the pending-op shield and idempotent apply — is the whole anti-feedback-loop story (§1.6).

### 1.2 Which pins sync (`PinSyncRules`, engine-free, `Features/Sidebar/Data/PinSyncRules.cs`)

| Pin id | Kind | Syncs? | Wire uri |
|---|---|---|---|
| `pl:spotify:playlist:X` | Playlist | yes | `spotify:playlist:X` |
| `album:spotify:album:X` / `artist:spotify:artist:X` / `show:spotify:show:X` | Album/Artist/Show | yes | the uri after the prefix |
| `liked` (route pin, `SidebarPinId.LikedSongsUri`) | AppRoute | yes | `spotify:user:{username}:collection` (§0.1) |
| `pl:wavee:playlist:X` | Playlist (session-local provider) | **no** | — |
| `folder:<id>` | Folder | **no** | — |
| `home`, `search`, `albums`, `prerelease:…`, `browse:…`, `module:…`, every other route | AppRoute | **no** | — |

```csharp
namespace Wavee;

/// <summary>The ONE rule for which sidebar pins mirror Spotify's ylpin set, and how a pin id and a wire uri map onto
/// each other. Engine-free (System + Wavee.Core), source-included by Wavee.Tests. Everything that is NOT a Spotify
/// playlist/album/artist/show or the Liked Songs route stays a local-only pin — folders, app routes, wavee: playlists.</summary>
public static class PinSyncRules
{
    /// <summary>pin id → the collection2v2 item uri, or null when this pin is local-only.</summary>
    public static string? TryWireUri(string? pinId, string username)
    {
        if (string.IsNullOrEmpty(pinId)) return null;
        if (string.Equals(pinId, "liked", StringComparison.Ordinal))
            return username.Length > 0 ? "spotify:user:" + username + ":collection" : null;
        switch (SidebarPinId.KindOf(pinId))
        {
            case SidebarEntryKind.Playlist:
            case SidebarEntryKind.Album:
            case SidebarEntryKind.Artist:
            case SidebarEntryKind.Show:
                string uri = SidebarPinId.UriOf(pinId);
                return uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri : null;
            default:
                return null;   // folders, app routes
        }
    }

    /// <summary>wire uri → the canonical pin id, or null when the server sent something this client cannot pin
    /// (a track, an episode, an unknown scheme, a binary blob).</summary>
    public static string? TryPinId(string? wireUri)
    {
        if (string.IsNullOrEmpty(wireUri) || !wireUri.StartsWith("spotify:", StringComparison.Ordinal)) return null;
        return SidebarPinId.FromUri(wireUri);   // collapses every Liked spelling onto "liked"; refuses tracks/episodes
    }

    public static bool IsSyncable(string? pinId, string username) => TryWireUri(pinId, username) is not null;
}
```

`SidebarPinId.FromUri` (`Features/Sidebar/Data/SidebarPinId.cs:115-128`) already does the kind screening, so the
read side is one call.

### 1.3 Backend: `ylpin` as a wire set with ONE logical set `"pins"`

`src/apps/Wavee/Backend/Collections/CollectionSets.cs` — the only mapping owner:

```csharp
public static readonly string[] WireSets = { "collection", "artist", "show", "listenlater", "ylpin" };

public static string WireSet(string setId) => setId switch
{
    …existing rows…
    "pins" => "ylpin",          // Your-Library pins: playlists/albums/artists/shows + the Liked Songs collection, mixed
    _ => setId,
};

// UriPrefix("pins") stays null (mixed kinds) — membership is filtered by AcceptsUri instead.

public static IReadOnlyList<string> LogicalSetsForWireSet(string wireSet) => wireSet switch
{
    …existing rows…
    "ylpin" => Pins,
    _ => Array.Empty<string>(),
};
static readonly string[] Pins = { "pins" };

/// <summary>Whether an item off the wire may be folded into a logical set at all. Every prefix-filtered set says yes
/// to whatever its prefix admits; the prefix-less "pins" set — the one place a mixed, partly-opaque set can leak a
/// non-uri into the store — admits only pinnable Spotify entity uris and the Liked Songs collection.</summary>
public static bool AcceptsUri(string setId, string uri)
{
    if (setId != "pins") return true;
    if (!uri.StartsWith("spotify:", StringComparison.Ordinal)) return false;
    if (EntityUri.IsLikedCollection(uri)) return true;
    return EntityUri.KindOf(uri) is EntityKind.Playlist or EntityKind.Album or EntityKind.Artist or EntityKind.Show;
}

/// <summary>Does a dealer PubSubUpdate for this wire set direct-apply (zero round-trip) or always re-fetch? ylpin
/// pushes are opaque in practice (the retired client never decoded one), so they always take the delta path.</summary>
public static bool PushDirectApplies(string wireSet) => wireSet != "ylpin";
```

Apply the filter where items are folded — two places, both already switch on the set:

- `CollectionFetcher.ApplyItems` (`Backend/Collections/CollectionFetcher.cs:243-253`): add
  `if (!CollectionSets.AcceptsUri(setId, it.Uri)) continue;` next to the prefix test.
- `CollectionDeltaApplier.Apply` (called from `FetchWireSetAsync :105`) — same guard, or filter in
  `CollectionFetcher.ForLogicalSet` (`:321-329`) so the delta applier stays generic. Pick one; `ForLogicalSet` is the
  smaller change (it already filters by prefix per logical set).
- `CollectionDrift.Compare` (reconcile, `:137`) compares `ledger.UrisFor(prefix)` against `SavedItems`; with the filter
  applied on the walk side (`CollectionSnapshotLedger.UrisFor` → add an `accepts` predicate or post-filter in
  `ReconcileWireSetAsync`) the drift check does not report the filtered-out blobs as "missing". Verify at
  implementation which of the two is smaller.

`LibrarySync.cs`:

```csharp
// :825 — KindForLogicalSet
"pins" => CollectionKind.Pins,

// :837 — ShouldDirectApply gains the wire-set policy. It is called from Enqueue (:157) with only the payload, so
// thread the set through: ShouldDirectApply(cmd.Uri, cmd.Payload) / ShouldDirectApply(wireSet, payload).
bool ShouldDirectApply(string wireSet, byte[]? payload)
{
    if (!CollectionSets.PushDirectApplies(wireSet)) return false;   // ylpin: settle + delta, never a fold
    …unchanged…
}
```

`CollectionPushAsync :766-772` — the echo check still runs before the fetch (a parseable echo of our own ylpin write
is dropped for free); the `upd.Items.Count > 0` direct-apply branch must also honour `PushDirectApplies` so a
payload that happens to parse cannot fold. Simplest: `if (CollectionSets.PushDirectApplies(wireSet) && upd.Items.Count > 0) { DirectApply…; return; }`.

`Wavee.Core/Sources/CollectionEvents.cs:5`: `public enum CollectionKind { Albums, Artists, Shows, Playlists, Liked, Pins }`.
`CollectionKind` is switched on in `Features/Detail/DetailPage.cs`, `Backend/Library/StoreLibrarySource.cs`,
`App/LibraryStore.cs` and `Backend/Store.cs` — grep `CollectionKind.` and make every `switch` either ignore `Pins`
explicitly or already have a default arm (`TreatWarningsAsErrors` turns a non-exhaustive switch expression into a
build break).

`EngineMutationSource` (`Backend/Seam.cs`): `AllSets` (`:91`) **must not** gain `"pins"` — it is the union behind
`Saved`/hearts, and a pinned album is not a saved album. Add the one new entry point:

```csharp
/// <summary>Pin / unpin in Spotify's ylpin set. Same shape as SetSavedAsync minus the set inference (a pin is a
/// pin whatever the entity kind), same drain routing.</summary>
public async Task SetPinnedAsync(string wireUri, bool pinned, CancellationToken ct = default)
{
    _mut.Save("pins", wireUri, pinned);                                   // optimistic Pending row + durable outbox op
    if (ScheduleDrain is { } viaLoop) { viaLoop(); return; }
    await _mut.Drain(_transport, _ctx(), ct).ConfigureAwait(false);
}
```

`MutationEngine.Save` (`Mutation.cs:565-575`) coalesces per `(set, uri)` and `SetReplayStrategy` already builds the
right body — nothing else changes on the write path. `HasPending("pins", uri)` (`:559`) is the shield the fetcher
already consults through its `hasPending` delegate.

### 1.4 `SidebarPinStore` — the remote-apply path

`src/apps/Wavee/Features/Sidebar/SidebarPinStore.cs`:

```csharp
/// <summary>Raised for a USER-INTENT membership change only (Pin / Insert / Unpin). Never by ApplyRemote, LoadFrom,
/// Move or Touch — that asymmetry is what keeps a server-originated change from echoing back as a write.</summary>
public Action<SidebarPin, bool>? OnLocalPinChanged;

public bool Pin(SidebarPin pin)
{
    …unchanged…
    Bump();
    OnLocalPinChanged?.Invoke(stored, true);
    return true;
}
public bool Insert(SidebarPin pin, int index) { …; Bump(); OnLocalPinChanged?.Invoke(stored, true); return true; }
public int Unpin(string? pinId)
{
    …
    var removed = _items[at];
    …
    Bump();
    OnLocalPinChanged?.Invoke(removed, false);
    return at;
}

/// <summary>Converge the SYNCABLE pins onto the server's membership without touching order, local-only pins, or the
/// local-change event. <paramref name="serverPins"/> is the server set already mapped to pin ids (+ display cache);
/// <paramref name="isSyncable"/> decides which local pins are eligible for removal at all. Adds are APPENDED in the
/// given order (the caller sorts by added_at ascending, so a batch of remote pins lands oldest-first); removals only
/// happen when <paramref name="removeMissing"/> is true — the caller gates that on "the server set has actually
/// converged once" (see SidebarPinSync). Returns true when anything changed; commits (OnChanged) exactly once.</summary>
public bool ApplyRemote(IReadOnlyList<SidebarPin> serverPins, Func<string, bool> isSyncable, bool removeMissing)
{
    bool changed = false;
    var keep = new HashSet<string>(serverPins.Count, StringComparer.Ordinal);
    for (int i = 0; i < serverPins.Count; i++)
    {
        var p = Canonicalize(serverPins[i]);
        if (string.IsNullOrEmpty(p.Id)) continue;
        keep.Add(p.Id);
        if (IndexOf(p.Id) >= 0) continue;
        _index[p.Id] = _items.Count;
        _items.Add(p);
        changed = true;
    }
    if (removeMissing)
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var id = _items[i].Id;
            if (!isSyncable(id) || keep.Contains(id)) continue;
            _items.RemoveAt(i);
            _index.Remove(id);
            Reindex(i);
            changed = true;
        }
    if (changed) Bump();          // version + OnChanged (persist) — deliberately NOT OnLocalPinChanged
    return changed;
}
```

`SidebarPreferences` exposes it (`Features/Sidebar/SidebarPreferences.cs` next to `:422-430`):
`public bool ApplyRemotePins(IReadOnlyList<SidebarPin> server, Func<string, bool> isSyncable, bool removeMissing) => Pins.ApplyRemote(server, isSyncable, removeMissing);`

### 1.5 `SidebarPinSync` — the bridge (`src/apps/Wavee/App/SidebarPinSync.cs`)

Owned by `Services`, created in the shared ctor next to `Sidebar` (`App/Services.cs:325`), wired to the store and the
mutation source right after `mutations` exists (`:670`), and given the UI `post` in the same place
`SidebarPreferences.Activate` gets it.

```csharp
namespace Wavee;

/// <summary>Two-way bridge between the store's "pins" set (the ylpin mirror) and the sidebar's pin list.
/// UI-thread affine on the SidebarPinStore side (every mutation goes through <c>post</c>); store subscriptions fire
/// on the sync loop. Engine-free apart from the store/mutation seams, so <c>SidebarPinSyncTests</c> drives it with
/// an InMemoryStore and a recording mutation source.</summary>
public sealed class SidebarPinSync : IDisposable
{
    readonly IStore _store;
    readonly SidebarPreferences _prefs;
    readonly IPinMutations _mutations;               // SetPinnedAsync(uri, pinned) — EngineMutationSource implements it
    readonly Func<string> _username;
    readonly Func<bool> _converged;                  // () => cold.GetCollectionRevision("ylpin") is not null
    readonly Func<string, bool> _hasPending;         // (uri) => mutEngine.HasPending("pins", uri)
    readonly Action<Action> _post;
    readonly IDisposable _sub;
    bool _migrated;                                  // §1.7 — one-time push of pre-existing local pins

    public SidebarPinSync(IStore store, SidebarPreferences prefs, IPinMutations mutations, Func<string> username,
                          Func<bool> converged, Func<string, bool> hasPending, Action<Action> post, bool migrated)
    {
        …assign…
        _prefs.Pins.OnLocalPinChanged = OnLocalPinChanged;
        _sub = store.Changes.Subscribe(Observers.From<StoreChange>(OnStoreChange));
    }

    // remote → local. Bulk (a page walk finished) or a pins-kind bump both re-read the whole mirror: it is at most a
    // few dozen rows and the apply is idempotent, so a diff would only add a second source of truth.
    void OnStoreChange(StoreChange c)
    {
        if (!c.IsBulk && c.Kind != CollectionKind.Pins) return;
        var items = _store.SavedItems("pins");
        var server = new List<SidebarPin>(items.Count);
        foreach (var it in items.OrderBy(i => i.AddedAtMs))          // oldest first → appended in pin order
        {
            if (PinSyncRules.TryPinId(it.Uri) is not { } id) continue;
            server.Add(new SidebarPin(id, SidebarPinId.KindOf(id), SidebarPinId.UriOf(id), "", it.AddedAtMs));
        }
        bool removeMissing = _converged() && _migrated;
        string user = _username();
        _post(() =>
        {
            _prefs.ApplyRemotePins(server,
                isSyncable: id => PinSyncRules.IsSyncable(id, user) && !_hasPending(PinSyncRules.TryWireUri(id, user)!),
                removeMissing);
            if (!_migrated && _converged()) MigrateLocalPins(server, user);
        });
    }

    // local → remote. Only syncable kinds; everything else is a silent local pin as before.
    void OnLocalPinChanged(SidebarPin pin, bool pinned)
    {
        if (PinSyncRules.TryWireUri(pin.Id, _username()) is not { } uri) return;
        _ = _mutations.SetPinnedAsync(uri, pinned);                // outbox: durable, replays after a restart/offline
    }

    public void Dispose() { _sub.Dispose(); _prefs.Pins.OnLocalPinChanged = null; }
}
```

Display names: a remote-added pin arrives with `Name = ""`. The pinned row already renders
`SidebarPaneText.ShortUri(entry.Uri)` as its fallback and `SidebarPreferences.TouchPin` refreshes the cache from live
library data on the next projection, so albums/artists/shows/followed playlists name themselves once the fetcher's
hydrate delegate has run (it hydrates every added uri — `CollectionFetcher.HydrateAsync :303`). A pinned playlist the
user does **not** follow is never in the rootlist and the extended-metadata catalogue does not serve playlists, so
it would keep its fallback title. Milestone 3 (§4) closes that with a header fetch through the hydration façade
(`svc.Hydrator` at `HydrationLevel.Header`); confirm the exact façade API at implementation time — this plan does not
pin it.

### 1.6 Why there is no feedback loop

| Path | What stops the echo |
|---|---|
| Local pin → write → dealer echo (parseable `PubSubUpdate` with our cuid) | `CollectionEchoRing.Contains(cuid)` → `EchoDropped`, nothing reaches the store (`LibrarySync.cs:769`). |
| Local pin → write → dealer push (opaque) → delta fetch returns our own item | `ApplyItems` skips `(pins, uri)` while the op is pending (`_hasPending`); after the drain the row is already `Confirmed` — `SetSaved` with the same state is a no-op bump; `ApplyRemote` finds the id already pinned → no change, no `OnLocalPinChanged`. |
| Remote pin → store → `ApplyRemote` | `ApplyRemote` never raises `OnLocalPinChanged`, so no write is enqueued. |
| Remote unpin → store → `ApplyRemote` removes | same — and the removal is gated on `converged && migrated`, so an empty mirror (first boot, offline) can never wipe the user's list. |
| Undo toast (`InsertPin` after an unpin) | `Insert` raises `OnLocalPinChanged(pin, true)` → one write; `MutationEngine.Save` coalesces per `(set, uri)` so a fast unpin/undo pair collapses to the final state. |
| Reorder (`Move`) | no event, no write — order is local by design. |

### 1.7 Pre-existing local pins (the upgrade path)

On the first run after this ships, the user's JSON holds pins the server does not. Deleting them would be data loss,
so the first converged walk **migrates instead of sweeping**: `MigrateLocalPins` enqueues `SetPinnedAsync(uri, true)`
for every syncable local pin absent from `server`, then sets `_migrated = true` and persists it as a settings key
(`SidebarKeys.PinsMigratedToServer`, alongside the other `SidebarKeys` in `SidebarPreferences`). Removals from the
server are honoured only after that flag is set. A fresh install has nothing to migrate and sets the flag on first
convergence.

---

## 2. Milestones (ordered — ship each on its own)

### M1 — read + hydrate (Spotify → Wavee)

1. `Wavee.Core/Sources/CollectionEvents.cs`: `CollectionKind.Pins`; fix every switch (§1.3).
2. `CollectionSets.cs`: `WireSets` + `"pins"`/`"ylpin"` rows, `Pins` array, `AcceptsUri`, `PushDirectApplies`.
3. `CollectionFetcher.cs` (`ForLogicalSet`/`ApplyItems`) + reconcile filter: apply `AcceptsUri`.
4. `LibrarySync.cs`: `KindForLogicalSet("pins")`; `ShouldDirectApply(wireSet, payload)`; guard the direct-apply branch.
5. `Features/Sidebar/Data/PinSyncRules.cs` (new, engine-free) — `TryPinId` only is needed for M1; add `TryWireUri` now anyway.
6. `SidebarPinStore.ApplyRemote` + `SidebarPreferences.ApplyRemotePins`.
7. `App/SidebarPinSync.cs` (remote→local half; `OnLocalPinChanged` wired but `SetPinnedAsync` not called yet), created in `Services` (§1.5); `converged` = the cold store's `GetCollectionRevision("ylpin") is not null`; `migrated` read from settings (M1 never removes: leave `_migrated = false` until M2 lands the write).
8. `SpotifyLive/SpotifyLibrarySync.cs:23` `Sets` + `"pins"` so `--spotify-sync` prints the pin count, **and log the first 5 pin uris** — this is the capture step for §0.1's Liked Songs question.
9. Tests (§3, M1 rows). Debug + Release build, `Wavee.Tests` green.

Visible result: pins made on phone/desktop Spotify appear in Wavee's Pinned section at launch and live (250 ms settle
+ one `/delta`). Nothing is ever removed locally yet.

### M2 — write-back (Wavee → Spotify) + convergence

1. `EngineMutationSource.SetPinnedAsync` (+ the tiny `IPinMutations` interface it implements so the bridge is testable).
2. `SidebarPinStore.OnLocalPinChanged` raised from `Pin`/`Insert`/`Unpin`; `SidebarPinSync.OnLocalPinChanged` calls `SetPinnedAsync`.
3. `MigrateLocalPins` + `SidebarKeys.PinsMigratedToServer`; `removeMissing = converged && migrated`.
4. `PinActions.cs:17-21` doc comment: replace "a pin is local presentation state with no server side" with the new
   contract ("membership of Spotify-kind pins mirrors ylpin through SidebarPinSync; order/folders/routes stay local;
   undo is still the toast, never the activity log").
5. Tests (§3, M2 rows). Verify end-to-end with the real client: pin in Wavee → appears in Spotify desktop within a
   second; unpin in Spotify → disappears in Wavee; airplane-mode pin → replays on reconnect (outbox).

### M3 — polish

- Header hydration for pinned, non-followed playlists (§1.5 last paragraph).
- Confirm/adjust the Liked Songs wire uri from the M1 capture; if the server spells it differently, only
  `PinSyncRules.TryWireUri`'s `"liked"` arm changes.
- `docs/plans/wavee/library-sync-implementation-plan.md:911`: strike the `ylpin` non-goal; `docs/guide/sidebar-extension-platform.md` pins section: describe the sync.
- `CHANGELOG` bullet `(#n)`.

---

## 3. Tests (no source-text tests — every decision is in an engine-free class)

| Test | Change |
|---|---|
| `LibrarySyncTests.CollectionPush_WireSetTranslation_CollectionFetchesBoth_UnknownFetchesNothing` (`:672-692`) | Replace `"ylpin"` with `"artistban"` as the unknown set (the assertion "unknown → zero fetch" stays valid and still exercises `LogUnknownWireSetOnce`). |
| **new** `LibrarySyncTests.CollectionPush_Ylpin_NeverDirectApplies_FetchesPinsSet` | Enqueue a `CollectionPush("ylpin")` with (a) no payload, (b) a parseable `PubSubUpdate` carrying `spotify:playlist:pin1`. Both: `PushDirectApplied == 0`, `SetFetches == 1`, `Store.IsSaved("pins", "spotify:playlist:pin1")`. `HydrateResponder` (`:148-159`) gains `case "ylpin": p.Items.Add(new CollectionItem { Uri = "spotify:playlist:pin1", AddedAt = 1 }); p.Items.Add(new CollectionItem { Uri = "spotify:track:notapin", AddedAt = 2 });` — the track must NOT land in `"pins"` (`AcceptsUri`). |
| **new** `LibrarySyncTests.CollectionPush_YlpinEcho_IsDropped` | Record a cuid in `h.Echo`, push a parseable `PubSubUpdate { Set="ylpin", ClientUpdateId=cuid }` → `EchoDropped == 1`, `SetFetches == 0`. |
| Any test asserting `CollectionSets.WireSets.Length`, "4 wire sets", or `CollectionPosts == 4` after `InitialHydrate` | +1 (`ylpin` is walked at boot). Grep `WireSets` and `CollectionPosts` in `Wavee.Tests` when the build turns red. |
| `CollectionFetcherTests.FetchWireSet_UnknownWireSet_Throws` (`:111-117`) | Use `"artistban"`. |
| **new** `CollectionSetsTests` (or extend the existing mapping test) | `WireSet("pins") == "ylpin"`, `LogicalSetsForWireSet("ylpin") == ["pins"]`, `AcceptsUri("pins", …)` table (playlist/album/artist/show/liked spellings → true; track/episode/`wavee:`/blob → false), `PushDirectApplies("ylpin") == false`. |
| **new** `PinSyncRulesTests` | The §1.2 table, both directions; `TryPinId` of every `EntityUri.IsLikedCollection` spelling → `"liked"`; `TryWireUri("liked", "")` → null (no username yet ⇒ not syncable). |
| `SidebarPinStoreTests` (+) | `ApplyRemote`: appends missing in given order; keeps existing order; never raises `OnLocalPinChanged`; with `removeMissing:false` removes nothing; with `removeMissing:true` removes only ids `isSyncable` says yes to (a `folder:` and a `home` pin survive); bumps `Version` once and raises `OnChanged` once per call; returns false when nothing changed. `Pin`/`Insert`/`Unpin` raise `OnLocalPinChanged` with the right flag; `Move`/`Touch`/`LoadFrom` do not. |
| **new** `SidebarPinSyncTests` (InMemoryStore + a recording `IPinMutations` + synchronous `post`) | remote add → pinned, zero writes; remote add of a track uri → ignored; local `Pin` of `pl:spotify:playlist:x` → exactly one `SetPinnedAsync(uri, true)`; local pin of `folder:…` → zero writes; unpin/undo pair → two calls, final state true; `converged=false` + empty mirror → no removals; `converged=true, migrated=false` → local syncable pins pushed (`SetPinnedAsync(..., true)` per pin), still no removals; `migrated=true` → a missing syncable pin is removed while a pending one is kept. |

`Wavee.Tests` source-includes `Features/Sidebar/Data/*` and the pin store (see `SidebarPinStoreTests.cs:6-9`), so
`PinSyncRules` and `ApplyRemote` are tested against production code with no engine.

---

## 4. Threading and persistence notes

- `store.Changes` fires on the sync loop / drain thread. `SidebarPinSync` touches `SidebarPinStore` only inside
  `_post(...)` — the same UI dispatcher `SidebarPreferences.Activate` receives (`SidebarPreferences.cs:108-118`).
  Before `Activate` has run (very early boot) the bridge must queue, not drop: reuse the "pending until post is set"
  pattern `SidebarPreferences` already has for persistence health.
- `ApplyRemote` commits through `OnChanged` → `Commit` — one JSON write per convergence, coalesced like any pin mutation.
- The `"pins"` set persists in `collection_items` like every other set (`SqliteColdStore`), so a cold launch shows the
  last-known server pins before the network; `LoadFrom` (the JSON) and the mirror agree by construction after the first
  converged run.
- `SetPinnedAsync` rides the outbox: an offline pin is durable and replays on the next login — same guarantee as a like.

---

## 5. First task before any code: confirm the Liked Songs wire uri

Add `"pins"` to `SpotifyLibrarySync.Sets` and a `log.Info` of the first few `store.SavedItems("pins")` uris (M1 step 8),
run `dotnet run --project src/apps/Wavee -- --spotify-sync` against an account with Liked Songs pinned, and read the
uri off the log. Set `PinSyncRules.TryWireUri("liked", …)` to that exact spelling. Everything else in this plan is
independent of the answer.

---

## 6. Files touched (summary)

| File | M |
|---|---|
| `src/apps/Wavee.Core/Sources/CollectionEvents.cs` | 1 |
| `src/apps/Wavee/Backend/Collections/CollectionSets.cs` | 1 |
| `src/apps/Wavee/Backend/Collections/CollectionFetcher.cs` (`ForLogicalSet`/`ApplyItems`, reconcile filter) | 1 |
| `src/apps/Wavee/Backend/Sync/LibrarySync.cs` (`KindForLogicalSet`, `ShouldDirectApply`, `CollectionPushAsync`) | 1 |
| `src/apps/Wavee/Features/Sidebar/Data/PinSyncRules.cs` **new** | 1 |
| `src/apps/Wavee/Features/Sidebar/SidebarPinStore.cs` (`ApplyRemote`, `OnLocalPinChanged`) | 1/2 |
| `src/apps/Wavee/Features/Sidebar/SidebarPreferences.cs` (`ApplyRemotePins`, `SidebarKeys.PinsMigratedToServer`) | 1/2 |
| `src/apps/Wavee/App/SidebarPinSync.cs` **new**; `App/Services.cs` (construct + wire) | 1/2 |
| `src/apps/Wavee/Backend/Seam.cs` (`SetPinnedAsync`, `IPinMutations`) | 2 |
| `src/apps/Wavee/Actions/PinActions.cs` (doc comment only) | 2 |
| `src/apps/Wavee/SpotifyLive/SpotifyLibrarySync.cs` (`Sets` + uri log) | 1 |
| `src/apps/Wavee.Tests/{LibrarySyncTests,CollectionFetcherTests,SidebarPinStoreTests}.cs`; **new** `PinSyncRulesTests`, `SidebarPinSyncTests`, `CollectionSetsTests` | 1/2 |
| `docs/plans/wavee/library-sync-implementation-plan.md:911`, `docs/guide/sidebar-extension-platform.md`, `CHANGELOG.md` | 3 |
