# Library — the navigator jump on select (#E) and "Recents" = recently played (#F)

Investigated 2026-09-02 from `C:\Users\ChristosKarapasias\Videos\buggedartist.mp4` (4.9 s, Library › Artists, sort pill
"Recents", 1928×1246 @ 150 %). Every claim below was read in source; the one link that could not be *observed* is
flagged as such and the plan adds the always-on log line that confirms it on the first Debug run. Style precedent:
`logs-page-implementation.md`, `onboarding-v3-implementation.md`.

Rules baked in: no environment switches (an always-on `Info` log line is the diagnostic); no legacy paths (the
navigator's `ItemsView.List` preset and the hand-rolled `Filtered`/`FilterSortAlbums` sorts are **replaced**, not
wrapped; the persisted play-log gains additive fields, no migration code); derived facts live on the model (the
recency index is a `PlayLogStore` fact, the order is a pure `LibraryNavOrder` decision, the UI only renders both —
memory note "Derived facts live on the model"); every comment says WHY; no source-text tests.

## Context

### What the recording shows (frames at 2 fps + 30 fps crops of the artist column)

| t | frame | what is on screen |
|---|---|---|
| 0.0 s | f001 | Artists page, sort pill **Recents ▴**, list view. LAUV is selected (accent bar) and the list is scrolled so LAUV / In Love With a Ghost are the last two visible rows. Cursor on "In Love With a Ghost". |
| +1 frame (33 ms) | tile1 r1c2 | The click lands: the right panes switch to In Love With a Ghost (Half Step Princess), and the **left list is at offset 0** (Hans Zimmer, Phil Collins, Savage Garden … at the top). No accent bar visible — the selected row is now off-screen. The cursor has not moved; the hover plate is under it on "vaultboy". |
| 0.5 → 1.0 s | tile1 r1–r3 | The list stays parked at the top for ~18 frames (0.6 s). Hover follows the mouse; nothing else changes. |
| ~1.0 s | tile1 r4 c4–c6 | An **animated** scroll runs downward over ≥3 frames until "In Love With a Ghost" sits at the bottom edge of the viewport with the accent bar — i.e. exactly the pre-click offset (the clicked row was the bottom-most visible row before the click, so "minimal bring-into-view" and "the old offset" coincide). |
| 2.0 s / 3.0 s | f005–f010, tile3 | Same three-beat sequence for Urban Zakapa and for Jukjae: jump to top on the click → ~0.6 s parked → glide back until the selected row is at the bottom edge. The discography pane meanwhile shows cold cover tiles (f007) — a normal artist switch. |

So: not a skeleton flash, not a lost selection, not a re-sort of the visible rows (the row order is identical before
and after in all three clicks). It is a **scroll-offset reset to 0 followed by a programmatic bring-into-view of
the selected row**. The order shown under "Recents" is the saved-set order (added-date descending), which is not
"recently played" at all — see #F.

### Root cause #E — the navigator remounts on selection and has no scroll memory

1. The navigator is a **frozen-template** `ItemsView` whose wrapper is keyed on the item **order hash**
   (`src/apps/Wavee/Features/Library/LibraryPage.cs:475` `string key = "nav:" + view + ":" + size + ":" + NavHash(shown);`,
   `:862` `NavHash` folds every `RouteKey` in sequence). Any republish of `store.Artists` that yields a different
   sequence changes the key → the reconciler mounts a fresh list and removes the old one (`..\fluent-gpu\src\FluentGpu.Engine\Reconciler\Reconciler.cs:3355-3379`).
2. The list is built with the `ItemsView.List(...)` preset (`LibraryPage.cs:489-492`), which exposes **no
   `ScrollOptions`** — the preset never sets `ScrollKey` (`..\fluent-gpu\src\FluentGpu.Controls\ItemsViewPresets.cs:242-263`),
   so a remount has nothing to restore: `ApplyScrollKey(isMount, newKey: null)` returns before the scope walk
   (`Reconciler.cs:2165-2172`) and the fresh viewport starts at 0. The engine's own guidance for this shape is
   "re-key the wrapper so a set change remounts it (**scrollKey preserves the offset**; the DetailTracks idiom)"
   (`..\fluent-gpu\src\FluentGpu.Controls\ItemsView.cs:806-808`).
3. Selecting an artist **re-publishes the whole artists collection**: `Select` (`LibraryPage.cs:348-352`) →
   `_selectedKey` → `LoadArtist` (`:411-415`) → `StoreLibrarySource.GetArtistAsync`
   (`src/apps/Wavee/Backend/Library/StoreLibrarySource.cs:212-218`) → `EnsureAsync(uri, Open)` and/or a cold→hot
   promotion (`src/apps/Wavee/Backend/Persistence/CachedStore.cs:272,323-331`) → `Store.UpsertArtist` →
   `Bump(a.Uri)` (`src/apps/Wavee/Backend/Store.cs:719-727`) → `StoreLibrarySource.OnStoreChange` maps an artist uri
   to `CollectionKind.Artists` (`:842-869`) → `LibraryStore.OnCollectionsChanged` → `Refresh(Artists, GetArtistsAsync)`
   (`src/apps/Wavee/App/LibraryStore.cs:139-154`) → `Artists.SetReady(newList)` → the page re-renders with a new
   `shown`.
4. That list is `JoinSet("artists", _store.GetArtist)` (`StoreLibrarySource.cs:690-704`): an **inner join** over the
   saved set ("skip not-yet-hydrated") sorted by `List<T>.Sort` (introsort, unstable) on `AddedAtMs`, where unknown
   stamps are `0` and tie. Membership is therefore "saved ∩ resolvable from the hot/cold tiers", which changes as the
   background collection ask (`:337`) and every artist open hydrate more followed artists (related/appears-on
   artists land through `ArtistHydration`), and as the residency backstop/shed evicts (`Store.cs:627`,
   `Services.cs:290,419`). Every such change is a different `NavHash` → step 1 → offset 0. **This is the inferred
   link**: the chain is real code, but which write changed the sequence on each of the three clicks was not
   observed. Part E adds `library.nav.remount` (old/new count, which key part changed) so the first run states it.
5. The glide back is `SyncNav` (`LibraryPage.cs:519-541`): the effect is keyed on `NavHash(shown)` so it re-runs
   after the republish; the selected key's index moved → `SyncSelect` (`:511-517`) → `_navCtl.StartBringItemIntoView(idx)`
   → minimal scroll → the row lands on the bottom edge (`ItemsView.cs:975-980`). The ~0.6 s is the hydration round
   trip before the republish lands.

Design consequence: the fix must (a) make the sequence a **deterministic function of the set** so a same-set
republish never changes the key, and (b) give the navigator a **`ScrollKey`** so every *legitimate* remount (a real
set change, a view/size change, a recency reorder after a play) restores the offset **before the first realized
window** (`Reconciler.cs:4512` "seed BEFORE RealizeWindow"). (b) alone already removes the symptom regardless of
which store write fires; (a) removes the wasted remounts. `SyncNav` stays as is — minimal bring-into-view is a
no-op for a visible row.

### Root cause #F — "Recents" sorts by the saved-set order, not by plays

- `LibraryPage.Filtered` (`LibraryPage.cs:384-399`): sort `0` (**Recents**, the persisted default —
  `src/apps/Wavee/Platform/AppSettings.cs:339` `Sort(k) => new(..., 0)`) applies **no comparator** — it is the
  cached store order, which `JoinSet` documents as "ADD order, newest first" (`StoreLibrarySource.cs:688-689`). Sort
  `1` (**Recently added**) is `Array.Reverse(arr)` of that — i.e. *oldest* added first. The direction chevron
  (`_desc`) is ignored for both (`:397` `if (desc && cmp is not null)`). Three bugs in one switch.
- The discography column has the same shape: `LibraryArtistPane.FilterSortAlbums` (`:1295-1309`) sort `0` = "as
  returned by the API (≈ release-date desc)".
- The play log exists and is the app's one "recently PLAYED" fact (`src/apps/Wavee/App/PlayLogStore.cs:13-23`): a
  200-entry ring of `PlayLogEntry(TrackUri, ContextUri, ContextKind, PlayedAtMs, ContextTitle)` (`:37`), appended at
  every real track boundary by `PlaybackBridge.PushState` (`src/apps/Wavee/App/PlaybackBridge.cs:1396-1400`), read
  by the sidebar's JumpBackIn/Played (`SidebarProjectionBinder.cs:558-570` → `SidebarSourceMap.Played`
  `:93-135`) and the taskbar jump list (`JumpListBridge.cs:159-162`), always via `RecentContexts` (`:141-160`,
  context-first, deduped). It carries **no album/artist ids** although the writer has them (`played` is a
  `Wavee.Core.Track` with `Artists: IReadOnlyList<ArtistRef>` and `Album: AlbumRef`,
  `src/apps/Wavee.Core/Domain/Models.cs:308-312`), and nothing derives a per-entity "last played" from it.
- The other recents surfaces are server-side: `RecentsPage` reads `svc.Recents` (`IRecentsSource`,
  `src/apps/Wavee.Core/Library/RecentsList.cs:91-99`; rows carry `Uri`, `ContextUri`, `PlayedAtMs`, `Members`
  `:64-78`) and Home's "Recents" module is the `HomeRecentlyPlayedSectionData` feed section
  (`src/apps/Wavee.Core/Spotify/SpotifyHomeComposer.cs:91-104`). Neither is queryable per artist/album today.
- `library.db` stores no per-entity last-played: `recent_surfaces(uri, kind, last_opened)` is "recently OPENED"
  (`SqliteColdStore.cs:449-450`, a GC pin reason), and `collection_items.added_at` is the add stamp (`:105-107`).
  The `LibraryStore.AddedAt` side-channel (`LibraryStore.cs:46-53`) is the precedent for a uri→stamp map published
  beside the collections.

Design consequence: the cheapest correct derivation is an **in-memory `uri → lastPlayedMs` index maintained by the
play-log writer** (the writer already holds the track's album + billed artists), persisted beside the ring so its
horizon is not the 200-play cap, and **fed from the server recents pipeline too** when a snapshot lands (the
Recents page's own `Adopt`), so plays from other devices count. Both library panes and the discography sort through
one pure `LibraryNavOrder` over that map. No SQL, no new table, no join at read time.

## Part E — the navigator never jumps

### E.0 Decisions
- **`ScrollKey` on every library list/grid**: navigator `"lib:nav:" + kind`, discography `"lib:disco:" + artistUri`.
  Scope is the KeepAlive slot (`Reconciler.cs:2091-2098`), so two tabs keep independent offsets for free.
- **`ItemsView.List(...)` preset → `ItemsView.Create(...)` with `Selector = SelectorVisual.AccentPill`** — the preset
  is only "AccentPill chrome + drag wiring + ItemClick" (`ItemsViewPresets.cs:172-181`), none of which the navigator
  uses beyond the chrome, and it is the only way to pass `ScrollOptions`. The grid arm already uses `Create`.
- **Remount key = view + size + `OrderKey` + `FactsKey`** (pure, from `LibraryNavOrder`), never the selection.
  `FactsKey` (uri/title/subtitle/cover url in order) is new: today a cover or name that lands after mount is never
  shown until an unrelated remount, because the template is frozen; with `ScrollKey` a facts remount is invisible, so
  the list can be allowed to be *correct*.
- **Deterministic total order** (Part F's `LibraryNavOrder`) — the final tie-break is the source index, so the same
  set always yields the same sequence whatever `List.Sort` did upstream.
- **Always-on diagnostic** `library.nav.remount` (`Info`, category `ui`): fields `kind`, `reason` (`view|order|facts`),
  `before`, `after` (row counts). One line per remount; that is the confirmation of root-cause step 4.
- **`LibraryStore` / `StoreLibrarySource` untouched** in this workstream (the per-write whole-collection refresh and
  the residency-dependent inner join are real but separate — see Follow-ups).

### E.1 Component tree (artists, wide layout — unchanged shape, changed leaves)
```
LibraryPage.Render
└─ BoxEl{Dir=1,Grow}  OnBoundsChanged→_collapsed
   └─ inner BoxEl{Dir=0,Grow}
      ├─ LeftColumn = NavPanel{Width=_leftW}
      │  ├─ Toolbar(title) : PageHero · LibrarySortView(_sort,_desc,_view,_size) · AutoSuggestBox(_filter)
      │  └─ ListBody(nav)  BoxEl{Key="nav:"+view+":"+size+":"+nav.OrderKey+":"+nav.FactsKey, Grow, Dir=1}
      │       └─ ItemsView.Create(nav.Items.Length, i => NavRowContent|NavCardContent(nav.Items[i], compact),
      │                            grid ? RepeatLayout.GridFit(w, 8) : RepeatLayout.Stack(compact ? 40 : 60),
      │                            new ListOptions{ Single, Selection=_navSel, Controller=_navCtl, Grow=1,
      │                                             Selector = grid ? Border : AccentPill,
      │                                             OnChange=() => OnNavSel(nav.Items),
      │                                             Scroll = _navScroll /* ScrollKey "lib:nav:"+kind */ })
      ├─ Grip(_leftW)
      └─ ArtistColumns: artistPane[LibraryArtistPane] · Grip(_midW) · tracksPane (unchanged)
LibraryArtistPane.Body(shown, artistUri)  BoxEl{Key="disco:"+view+":"+size+":"+OrderKey(facts)}
   └─ ItemsView.Create(..., Scroll = new ScrollOptions{ ScrollKey = "lib:disco:" + artistUri })
```

### E.2 Code

**`LibraryPage` — the shape computed once per render (replaces `NavHash` + the `shown` array):**
```csharp
// One shape per render: the rows in display order plus the two identities the remount key is built from. Computed
// ONCE so the wrapper key, the SyncNav effect key and the remount diagnostic all see the same sequence.
readonly record struct NavShape(NavItem[] Items, string OrderKey, string FactsKey);

readonly record struct NavItem(Image? Cover, string Title, string Subtitle, string Uri, bool Circular, string RouteKey, int Year)
{
    public LibraryNavFacts Facts => new(Uri, Title, Subtitle, Year, Cover?.Url);
}

NavShape Shape(NavItem[] items, IReadOnlyDictionary<string, long> recency)
{
    string q = _filter.Value.Trim(); var sort = (LibraryNavSort)_sort.Value; bool desc = _desc.Value;   // subscribe
    var arr = q.Length == 0 ? items : items.Where(it => it.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
    var facts = new LibraryNavFacts[arr.Length];
    for (int i = 0; i < arr.Length; i++) facts[i] = arr[i].Facts;
    // The ORDER is a model decision (LibraryNavOrder), never a UI one: the page hands over the source-ordered facts
    // and the recency map and renders whatever comes back.
    var order = LibraryNavOrder.Order(facts, sort, desc, recency);
    var sorted = new NavItem[arr.Length];
    var sortedFacts = new LibraryNavFacts[arr.Length];
    for (int i = 0; i < order.Length; i++) { sorted[i] = arr[order[i]]; sortedFacts[i] = facts[order[i]]; }
    return new NavShape(sorted, LibraryNavOrder.OrderKey(sortedFacts), LibraryNavOrder.FactsKey(sortedFacts));
}
```
In `Render` (`LibraryPage.cs:127`): `var shown = Filtered(Project(store));` becomes
```csharp
int playRev = svc.PlayLog.Version.Value;                       // subscribe — a play re-orders "Recents" in place
var nav = Shape(Project(store), svc.PlayLog.Recency);
var shown = nav.Items;
```
`:156` → `UseEffect(() => SyncNav(shown, fullSearch), nav.OrderKey + "|" + _selectedKey.Value + "|" + fullSearch);`
plus, right after it (unconditional — hooks are never branched on this page):
```csharp
// The remount diagnostic: which part of the navigator's identity changed. This is the line that names root-cause #E
// on a real library (a same-set republish must NOT show up here any more).
UseEffect(() => NoteNavKey(nav, _view.Value, _size.Value), nav.OrderKey + "|" + nav.FactsKey + "|" + _view.Value + "|" + _size.Value);
```
```csharp
string? _lastOrderKey, _lastFactsKey; int _lastView = -1, _lastSize = -1, _lastCount;
void NoteNavKey(NavShape nav, int view, int size)
{
    if (_lastOrderKey is not null)
    {
        string reason = view != _lastView || size != _lastSize ? "view" : nav.OrderKey != _lastOrderKey ? "order" : "facts";
        WaveeLog.Instance.Info("ui", "library.nav.remount", "Library navigator remounted",
            WaveeLogField.Of("kind", _kind), WaveeLogField.Of("reason", reason),
            WaveeLogField.Of("before", _lastCount), WaveeLogField.Of("after", nav.Items.Length));
    }
    _lastOrderKey = nav.OrderKey; _lastFactsKey = nav.FactsKey; _lastView = view; _lastSize = size; _lastCount = nav.Items.Length;
}
```
`Filtered` and `NavHash` are deleted. `Project` is unchanged.

**`ListBody` (replaces `:464-494`):**
```csharp
// ScrollMemory identity for the navigator: one per kind, scoped by the KeepAlive slot (so a second tab keeps its own
// offset). This is what makes a remount land at the saved row BEFORE the first realized window — the fix for #E.
readonly ScrollOptions _navScroll;   // ctor: _navScroll = new ScrollOptions { ScrollKey = "lib:nav:" + kind };

Element ListBody(NavShape nav)
{
    var shown = nav.Items;
    int view = _view.Value; int size = _size.Value;   // subscribe
    if (shown.Length == 0)
        return _filter.Peek().Length > 0
            ? EmptyState.Compact(Loc.Get(Strings.Library.NoMatch))
            : new BoxEl { Padding = new Edges4(Spacing.M, Spacing.XL, Spacing.M, Spacing.XL), Children = [Caption("…").Secondary()] };

    bool grid = view >= 2; bool compact = view == 0 || view == 2;
    // Remount ONLY when the frozen ItemsView would lie: a different row set/order (the template indexes `shown` by
    // position), different row facts (a cover/name that landed after mount), or a different view/size (row extent +
    // template shape). NEVER on selection and never on a same-set republish — the keys are deterministic functions of
    // the rows (LibraryNavOrder), and _navScroll restores the offset across every remount that does happen.
    string key = "nav:" + view + ":" + size + ":" + nav.OrderKey + ":" + nav.FactsKey;
    var options = new ListOptions
    {
        SelectionMode = ItemsSelectionMode.Single, Selection = _navSel, Controller = _navCtl, Grow = 1f,
        // AccentPill IS the ListView chrome the List preset wore (SelectorVisuals.AccentPill); Border is the grid's.
        Selector = grid ? SelectorVisual.Border : SelectorVisual.AccentPill,
        OnChange = () => OnNavSel(shown),
        Scroll = _navScroll,
    };
    var layout = grid
        ? RepeatLayout.GridFit((compact ? 88f : 116f) + size * (compact ? 16f : 24f), 8f)
        : RepeatLayout.Stack(compact ? 40f : 60f);
    Func<int, Element> template = grid ? i => NavCardContent(shown[i], compact) : i => NavRowContent(shown[i], compact);
    return new BoxEl
    {
        Key = key, Grow = 1f, Direction = 1,
        Padding = grid ? new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f) : new Edges4(0f, 0f, 0f, 0f),
        Children = [ItemsView.Create(shown.Length, template, layout, options)],
    };
}
```
`OnNavSelIdx` is unchanged; `OnNavSel` already exists (`:496`). Callers: `LeftColumn(...)` and `CollapsedLayout(...)`
take `NavShape nav` instead of `NavItem[] shown` and pass `nav` to `ListBody`, `nav.Items` everywhere else.

**`LibraryArtistPane.Body` (`:1314-1344`)** — same treatment: signature `Body(Album[] albums, LibraryNavFacts[] facts, string artistUri)`,
key `"disco:" + view + ":" + size + ":" + LibraryNavOrder.OrderKey(facts)`, both arms get
`Scroll = new ScrollOptions { ScrollKey = "lib:disco:" + artistUri }` (list arm moves from `ItemsView.List` to
`ItemsView.Create(..., RepeatLayout.Stack(compact ? 44f : 60f), new ListOptions { ..., Selector = SelectorVisual.AccentPill, OnChange = () => Pick(_discoSel.FirstSelectedIndex), ... })`).
`FilterSortAlbums` is replaced in Part F.3 (it returns the facts too).

### E.3 Tests
`LibraryNavOrder` is covered in F.5; the #E-specific facts are:
- `OrderKey_SameSequence_SameKey_DifferentOrder_DifferentKey`
- `FactsKey_ChangesWhenTitleOrCoverChanges_NotWhenSelectionWould` (facts do not include selection — there is no such input)
- `Order_IsDeterministic_ForTiedStamps` — build 50 rows with identical `lastPlayed`, feed them in two different
  *source* orders (a permutation), assert both outputs are the same sequence **relative to source index**
  (i.e. `Order` returns `0..n-1` for equal stamps regardless of how the upstream join permuted the ties — the exact
  property the remount key needs).

## Part F — "Recents" = recently played

### F.0 Decisions
- **One index, one owner**: `PlayLogStore.Recency` (`IReadOnlyDictionary<string,long>`, uri → last played unix ms),
  maintained by `PlayRecency` (pure). Stamped uris per play: track, context (album/playlist/artist/show/collection),
  album, every billed artist. Max-merge (a stamp never goes backwards). Cap 4096 with hysteresis (trim to 3840 — one
  trim per 256 new uris, never per append).
- **Writer carries the facts**: `PlaybackBridge` passes `played.Album.Uri` and the billed artist uris into
  `Append`. Read-time joins through the store are avoided on purpose: they would not work on the fake backend
  (`RealStore` null), would need the entity resident, and would make an ordering depend on cache residency (the very
  defect behind #E).
- **Persistence**: `play-log.json` keeps its array shape with two additive DTO fields (`album`, `artists`); the index
  is written as `play-recency.json` beside it (`{ "spotify:artist:…": 1788…, … }`), same debounced write-then-rename
  path. On load the file is read, then the ring is folded in (max-merge), so a missing or older file self-heals and a
  pre-change ring still contributes track + context stamps. No migration branch exists or is needed.
- **Server history feeds the same index** (reuse of the Recents pipeline): when `RecentsPage.Adopt` installs a
  snapshot, `RecentsRecency.Stamps(rows)` (pure, `Wavee.Core`) is merged in — Single rows' uris, Group rows' context
  uris and every collapsed member uri. After the page's identity hydration completes, track rows are resolved to
  their album/artists through the store (`RecentsRecency.TrackStamps`) so cross-device track plays reach the
  artist/album panes too. The sidebar/jump-list readers (`RecentContexts`) are untouched — the ring's semantics do
  not change.
- **Sort semantics** (`LibraryNavOrder`, shared by the artists pane, the albums pane, the podcasts pane and the
  discography): Recents = played block newest-first, then the never-played block in source order (the sidebar rule,
  `SidebarSort.Recents` `:95-106`: the never block never floats above a played row; `desc` flips inside the
  blocks). Recently added = source order (added-desc, fixing today's reversed list). Alphabetical / Creator =
  case-insensitive title / subtitle. Release date = year desc, unknown years sink. `desc` now flips **every** sort.
  Labels stay ("Recents", "Recently added" — `en-US.json:689-690`).
- **Fake backend**: `UnsupportedPlaybackPlayer` never plays and `NullRecentsService` never answers, so on `--fake`
  the played block is empty and "Recents" degrades to today's order. The behaviour is verified on the real session
  (see Verification).

### F.1 Data flow
```
PlaybackBridge.PushState (track boundary)            RecentsPage.Adopt(snapshot)         RecentsPage.HydrateAsync → _post
   │ Append(track, ctx, title, album, artists[])         │ MergeRecency(RecentsRecency.Stamps(rows))   │ MergeRecency(RecentsRecency.TrackStamps(rows, uris, store.GetTrack))
   ▼                                                     ▼                                             ▼
PlayLogStore ── ring (200) ──► play-log.json        PlayLogStore.Recency (PlayRecency, cap 4096) ──► play-recency.json
   │ Version++                                           ▲ folded from the ring at load
   ├─► SidebarProjectionBinder / JumpListBridge (RecentContexts — unchanged)
   └─► LibraryPage.Render: svc.PlayLog.Version (subscribe) → Shape(Project(store), svc.PlayLog.Recency)
            └─► LibraryNavOrder.Order(facts, sort, desc, recency)  ──► navigator rows / discography rows
```

### F.2 Code — the index and the writer

**NEW `src/apps/Wavee/App/PlayRecency.cs`** (System only):
```csharp
using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>uri → last-played unix ms over EVERY uri a play touches: the track, the context it was played from, its
/// album and each billed artist. This is the one "recently played" fact the library sorts on; it is derived here, at
/// the writer, never joined at read time (a read-time join would need the entity resident and would silently fail on
/// the fake backend). Max-merge: a stamp never moves backwards, so a server history older than a local play cannot
/// demote an artist you just listened to.</summary>
public sealed class PlayRecency
{
    /// <summary>Hard cap on distinct uris. 4 096 covers a few thousand artists/albums/tracks — far beyond any library
    /// pane — and keeps the sidecar file a few tens of KB.</summary>
    public const int Cap = 4096;
    /// <summary>Trim target once the cap is crossed: the oldest 256 stamps go in one pass, so a trim costs one sort
    /// per 256 NEW uris rather than one per append.</summary>
    public const int TrimTo = Cap - 256;

    readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, long> Map => _last;
    public int Count => _last.Count;
    public long Of(string uri) => _last.TryGetValue(uri, out var ms) ? ms : 0;

    /// <summary>Stamp one uri. Returns true when the map changed (first sighting, or a NEWER play).</summary>
    public bool Stamp(string? uri, long atMs)
    {
        if (string.IsNullOrEmpty(uri) || atMs <= 0) return false;
        if (_last.TryGetValue(uri!, out var cur) && cur >= atMs) return false;
        _last[uri!] = atMs;
        if (_last.Count > Cap) Trim();
        return true;
    }

    /// <summary>Stamp everything one play names. A bare track play (no context) stamps the track alone; the album
    /// and artists are whatever the writer knew — rows persisted before those fields existed carry neither.</summary>
    public bool Stamp(in PlayLogEntry e)
    {
        bool changed = Stamp(e.TrackUri, e.PlayedAtMs);
        if (e.ContextKind != PlayContextKind.None) changed |= Stamp(e.ContextUri, e.PlayedAtMs);
        changed |= Stamp(e.AlbumUri, e.PlayedAtMs);
        if (e.ArtistUris is { } artists)
            for (int i = 0; i < artists.Count; i++) changed |= Stamp(artists[i], e.PlayedAtMs);
        return changed;
    }

    public bool Merge(IEnumerable<KeyValuePair<string, long>> stamps)
    {
        bool changed = false;
        foreach (var kv in stamps) changed |= Stamp(kv.Key, kv.Value);
        return changed;
    }

    public void Clear() => _last.Clear();

    /// <summary>Snapshot for the pool writer (the PlayLogStore.Snapshot contract: taken on the caller's thread).</summary>
    public KeyValuePair<string, long>[] Snapshot()
    {
        var arr = new KeyValuePair<string, long>[_last.Count];
        int i = 0;
        foreach (var kv in _last) arr[i++] = kv;
        return arr;
    }

    void Trim()
    {
        // Oldest-first drop down to TrimTo. Allocates once per trim (rare by construction — see TrimTo).
        var all = Snapshot();
        Array.Sort(all, static (a, b) => a.Value.CompareTo(b.Value));
        int drop = all.Length - TrimTo;
        for (int i = 0; i < drop; i++) _last.Remove(all[i].Key);
    }
}
```

**`src/apps/Wavee/App/PlayLogStore.cs` edits** (the class stays; these are the deltas):
```csharp
// :37 — the entry carries what the writer knew about the track. Both optional: a bare/legacy row has neither.
public readonly record struct PlayLogEntry(string TrackUri, string ContextUri, PlayContextKind ContextKind, long PlayedAtMs,
                                           string? ContextTitle = null, string? AlbumUri = null, IReadOnlyList<string>? ArtistUris = null)

// fields
readonly PlayRecency _recency = new();
string? _recencyPath;   // play-recency.json beside play-log.json — derived in Init, never a second Init argument

/// <summary>uri → last-played unix ms (track, context, album, artists). The library panes' "Recents" fact. Bumps
/// <see cref="Version"/> with the ring, so one subscription covers both.</summary>
public IReadOnlyDictionary<string, long> Recency => _recency.Map;

public void Init(string playLogFilePath)
{
    _path = playLogFilePath;
    _recencyPath = Path.Combine(Path.GetDirectoryName(playLogFilePath) ?? "", "play-recency.json");
}

// LoadFromDisk: after the ring is read (and trimmed) — read the sidecar, THEN fold the ring in. Order matters: the
// ring is the fresher source for the last 200 plays, and max-merge makes the fold idempotent.
LoadRecencyFile();
for (int i = 0; i < _entries.Count; i++) _recency.Stamp(_entries[i]);

// Append gains the two facts; the idempotence gate is unchanged (same (track, context) within 1 s).
public bool Append(string? trackUri, string? contextUri, long atMs = 0, string? contextTitle = null,
                   string? albumUri = null, IReadOnlyList<string>? artistUris = null)
{
    …
    var entry = new PlayLogEntry(trackUri!, context, ClassifyContext(context), atMs, title,
                                 string.IsNullOrEmpty(albumUri) ? null : albumUri, artistUris is { Count: > 0 } ? artistUris : null);
    _entries.Add(entry);
    TrimToCap();
    _recency.Stamp(entry);
    _revision.Value++;
    ScheduleSave();
    return true;
}

/// <summary>Fold externally-known plays in (the server recents snapshot). Only a NEWER stamp changes anything; an
/// unchanged map bumps nothing, so a revalidation that returned the same history costs no re-render.</summary>
public bool MergeRecency(IEnumerable<KeyValuePair<string, long>> stamps)
{
    if (!_recency.Merge(stamps)) return false;
    _revision.Value++;
    ScheduleSave();
    return true;
}

// Clear(): also _recency.Clear() and delete the sidecar. SaveNow()/SaveAndWait(): snapshot BOTH (ring + recency) on
// the caller's thread and write both files in the same pool task (two write-then-rename moves; a crash between them
// is healed by the load-time fold). Snapshot(): the DTO gains Album/Artists.
internal readonly record struct PlayLogEntryDto(string Track, string? Context, byte Kind, long AtMs, string? Title = null,
                                                string? Album = null, string[]? Artists = null);
[JsonSerializable(typeof(PlayLogEntryDto[]))]
[JsonSerializable(typeof(Dictionary<string, long>))]   // the sidecar shape
internal sealed partial class PlayLogJsonCtx : JsonSerializerContext { }
```
`LoadRecencyFile` mirrors `LoadFromDisk`'s try/catch: an unreadable sidecar is moved to `.corrupt` and logged once
(`sidebar.play_log.load_failed` with `file=recency`), and the ring fold rebuilds what it can.

**`src/apps/Wavee/App/PlaybackBridge.cs:1400`** (one statement; the file has foreign uncommitted edits — touch only this line):
```csharp
_playLog?.Append(played.Uri, s.ContextUri, contextTitle: JumpListBridge.FromTrack(played, s.ContextUri),
                 albumUri: played.Album.Uri, artistUris: PlayLogStore.ArtistUris(played.Artists));
```
with, in `PlayLogStore`:
```csharp
/// <summary>The billed-artist uris of a track as a compact array (null when there are none) — a track boundary
/// allocation, not a per-push one.</summary>
public static string[]? ArtistUris(IReadOnlyList<ArtistRef> artists)
{
    if (artists.Count == 0) return null;
    var uris = new string[artists.Count];
    for (int i = 0; i < uris.Length; i++) uris[i] = artists[i].Uri;
    return uris;
}
```

### F.3 Code — the order

**NEW `src/apps/Wavee/Features/Library/LibraryNavOrder.cs`** (System only — `Compile Include` in `Wavee.Tests.csproj`
next to `LibrarySelectionCommit.cs`, `:344`):
```csharp
using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>The sort keys the library pickers offer (LibrarySortView rows 0..4). The int codes are PERSISTED
/// (LibraryStateKeys.Sort / AlbumSort) — never renumber.</summary>
public enum LibraryNavSort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, ReleaseDate = 4 }

/// <summary>What an order needs from a row. The navigator's NavItem and the discography's Album both project to this,
/// so one comparator set serves the artists, albums and podcasts panes and the discography column.</summary>
public readonly record struct LibraryNavFacts(string Uri, string Title, string Subtitle, int Year, string? CoverUrl);

/// <summary>The pure ordering behind every library list. Rows arrive in SOURCE order — the saved set newest-added-first
/// (StoreLibrarySource.JoinSet) for the panes, the API's release order for a discography — and that order is the
/// tie-break of last resort, so the result is a total order: the same set in the same source order always yields the
/// same sequence, which is what lets the page key its (frozen-template) ItemsView on <see cref="OrderKey"/> without
/// remounting on a same-set republish (#E).</summary>
public static class LibraryNavOrder
{
    static readonly StringComparer Name = StringComparer.OrdinalIgnoreCase;

    /// <summary>The permutation (indices into <paramref name="rows"/>) for <paramref name="sort"/>.
    /// <list type="bullet">
    /// <item><b>Recents</b> — played rows newest-first, then the never-played block in source order. The block split is
    /// applied BEFORE <paramref name="desc"/> (the SidebarSort.Recents rule): a never-played row can never float above a
    /// played one; <paramref name="desc"/> reverses inside each block.</item>
    /// <item><b>RecentlyAdded</b> — the source order IS added-desc; <paramref name="desc"/> reverses it.</item>
    /// <item><b>Alphabetical</b> / <b>Creator</b> — case-insensitive title / subtitle, then title, then uri.</item>
    /// <item><b>ReleaseDate</b> — year desc; unknown (0) years sink as a block.</item>
    /// </list></summary>
    public static int[] Order(LibraryNavFacts[] rows, LibraryNavSort sort, bool desc, IReadOnlyDictionary<string, long> lastPlayed)
    {
        var idx = new int[rows.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        if (rows.Length < 2) return idx;
        int sign = desc ? -1 : 1;
        Comparison<int> cmp = sort switch
        {
            LibraryNavSort.Recents => (a, b) =>
            {
                long pa = StampOf(lastPlayed, rows[a].Uri), pb = StampOf(lastPlayed, rows[b].Uri);
                bool ha = pa > 0, hb = pb > 0;
                if (ha != hb) return ha ? -1 : 1;                       // block split — direction-proof
                int c = ha ? pb.CompareTo(pa) : a.CompareTo(b);         // newest play first · never played: source order
                return sign * (c != 0 ? c : ByTitle(rows, a, b));
            },
            LibraryNavSort.Alphabetical => (a, b) => sign * ByTitle(rows, a, b),
            LibraryNavSort.Creator => (a, b) =>
            {
                int c = Name.Compare(rows[a].Subtitle, rows[b].Subtitle);
                return sign * (c != 0 ? c : ByTitle(rows, a, b));
            },
            LibraryNavSort.ReleaseDate => (a, b) =>
            {
                bool ya = rows[a].Year > 0, yb = rows[b].Year > 0;
                if (ya != yb) return ya ? -1 : 1;                       // unknown years sink as a block
                int c = rows[b].Year.CompareTo(rows[a].Year);
                return sign * (c != 0 ? c : ByTitle(rows, a, b));
            },
            _ => (a, b) => sign * a.CompareTo(b),                       // RecentlyAdded
        };
        Array.Sort(idx, cmp);   // unstable sort, total comparator (index is the last tie-break) → deterministic
        return idx;
    }

    static long StampOf(IReadOnlyDictionary<string, long> map, string uri) => map.TryGetValue(uri, out var ms) ? ms : 0;

    static int ByTitle(LibraryNavFacts[] rows, int a, int b)
    {
        int c = Name.Compare(rows[a].Title, rows[b].Title);
        if (c == 0) c = string.CompareOrdinal(rows[a].Uri, rows[b].Uri);
        return c != 0 ? c : a.CompareTo(b);
    }

    /// <summary>Identity of the SEQUENCE (uris in order) — the remount key part that says "the frozen template's
    /// index→row mapping is stale". FNV-1a, not string.GetHashCode: stable across runs so a test can pin it.</summary>
    public static string OrderKey(LibraryNavFacts[] rows)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < rows.Length; i++) h = Fnv(h, rows[i].Uri);
        return rows.Length + ":" + h.ToString("x16");
    }

    /// <summary>Identity of what the rows DISPLAY (uri, title, subtitle, cover) — the remount key part that says "a
    /// fact landed after mount". Selection is deliberately not an input: selecting must never remount.</summary>
    public static string FactsKey(LibraryNavFacts[] rows)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            h = Fnv(Fnv(Fnv(Fnv(h, r.Uri), r.Title), r.Subtitle), r.CoverUrl ?? "");
        }
        return h.ToString("x16");
    }

    static ulong Fnv(ulong h, string s)
    {
        for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 1099511628211UL; }
        h ^= 0x1F; h *= 1099511628211UL;   // field separator so ("ab","c") ≠ ("a","bc")
        return h;
    }
}
```

**`LibraryPage`** — see E.2 (`Shape` replaces `Filtered`; the page subscribes to `svc.PlayLog.Version` and hands
`svc.PlayLog.Recency` in). Nothing else in the page knows about plays.

**`LibraryArtistPane`** — `Render` adds `var svc = UseContext(Services.Slot);` and `int playRev = svc?.PlayLog.Version.Value ?? 0;`
(unconditional, before the early return, like the existing hooks), then:
```csharp
// Filter (title contains) + the shared library order over the artist's releases. Source order = the API's
// (≈ release-date desc), which is what "Recents" falls back to for releases you have never played.
static (Album[] Rows, LibraryNavFacts[] Facts) FilterSortAlbums(IReadOnlyList<Album> albums, string filter, int sort, bool desc,
                                                                IReadOnlyDictionary<string, long> recency)
{
    string q = filter.Trim();
    var arr = (q.Length == 0 ? albums : albums.Where(al => al.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToArray();
    var facts = new LibraryNavFacts[arr.Length];
    for (int i = 0; i < arr.Length; i++) facts[i] = new(arr[i].Uri, arr[i].Name, "", arr[i].Year, arr[i].Cover?.Url);
    var order = LibraryNavOrder.Order(facts, (LibraryNavSort)sort, desc, recency);
    var rows = new Album[arr.Length]; var sorted = new LibraryNavFacts[arr.Length];
    for (int i = 0; i < order.Length; i++) { rows[i] = arr[order[i]]; sorted[i] = facts[order[i]]; }
    return (rows, sorted);
}
```
`SyncDisco`'s effect key becomes `_albumKey.Value + "|" + LibraryNavOrder.OrderKey(facts)`.

### F.4 Code — the server history seed (Recents pipeline reuse)

**NEW `src/apps/Wavee.Core/Library/RecentsRecency.cs`** (pure):
```csharp
namespace Wavee.Core;

/// <summary>What a grouped recents page says about WHEN each entity was last played, in the shape PlayLogStore.MergeRecency
/// takes. A Single row is its own uri; a Group row is its context (the header uri) plus every collapsed member — the
/// members are the only complete account of which plays the card stands for (RecentsList.Group). Max-merge downstream
/// makes duplicates and out-of-order rows harmless.</summary>
public static class RecentsRecency
{
    public static List<KeyValuePair<string, long>> Stamps(IReadOnlyList<RecentsRow> rows)
    {
        var into = new List<KeyValuePair<string, long>>(rows.Count * 2);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.PlayedAtMs <= 0) continue;
            if (r.Uri.Length > 0) into.Add(new(r.Uri, r.PlayedAtMs));
            if (r.ContextUri is { Length: > 0 } ctx && ctx != r.Uri) into.Add(new(ctx, r.PlayedAtMs));
            if (r.Members is { Count: > 0 } members)
                for (int m = 0; m < members.Count; m++)
                    if (members[m].Uri.Length > 0 && members[m].PlayedAtMs > 0) into.Add(new(members[m].Uri, members[m].PlayedAtMs));
        }
        return into;
    }

    /// <summary>After identity hydration: the album + billed artists of every TRACK uri in <paramref name="uris"/>,
    /// stamped with that track's play time (looked up from the rows/members). <paramref name="resolve"/> is the
    /// store read (IStore.GetTrack); a track the store cannot name contributes nothing.</summary>
    public static List<KeyValuePair<string, long>> TrackStamps(IReadOnlyList<RecentsRow> rows, IReadOnlyList<string> uris,
                                                               Func<string, Track?> resolve)
    {
        var playedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (RecentsList.EntityKindOf(r.Uri) == RecentsEntityKind.Track) Newest(playedAt, r.Uri, r.PlayedAtMs);
            if (r.Members is { } members)
                for (int m = 0; m < members.Count; m++) Newest(playedAt, members[m].Uri, members[m].PlayedAtMs);
        }
        var into = new List<KeyValuePair<string, long>>();
        for (int i = 0; i < uris.Count; i++)
        {
            if (!playedAt.TryGetValue(uris[i], out long at) || resolve(uris[i]) is not { } t) continue;
            if (t.Album.Uri.Length > 0) into.Add(new(t.Album.Uri, at));
            for (int a = 0; a < t.Artists.Count; a++) into.Add(new(t.Artists[a].Uri, at));
        }
        return into;
    }

    static void Newest(Dictionary<string, long> map, string uri, long at)
    {
        if (uri.Length == 0 || at <= 0) return;
        if (!map.TryGetValue(uri, out long cur) || at > cur) map[uri] = at;
    }
}
```
**`RecentsPage`** — two one-line hooks, both already on the UI thread (`_post`): in `Adopt(snapshot)`
(`RecentsPage.cs:2183`) `_svc?.PlayLog.MergeRecency(RecentsRecency.Stamps(snapshot.Rows));` and in the
`HydrateAsync` completion block (`:2404-2409`) `if (_store is { } st) _svc?.PlayLog.MergeRecency(RecentsRecency.TrackStamps(_shape.Rows, uris, st.GetTrack));`.
The page already holds `_svc` (`:199`) and `_store` (`:200`).

### F.5 Tests (all `Wavee.Tests`, xunit, no engine)
- NEW `LibraryNavOrderTests.cs`: `Recents_PlayedNewestFirst_ThenNeverPlayedInSourceOrder`,
  `Recents_Desc_ReversesInsideBlocks_NeverPlayedStaysBelow`, `Recents_TieOnStamp_BreaksByTitleThenUri`,
  `RecentlyAdded_IsSourceOrder_DescReverses`, `Alphabetical_IsCaseInsensitive_TieBreaksByUri`,
  `Creator_BySubtitleThenTitle`, `ReleaseDate_NewestFirst_UnknownYearSinks`, `Order_SingleRow_And_Empty`, plus the
  three #E cases from E.3.
- NEW `PlayRecencyTests.cs`: `Stamp_FirstSighting_ReturnsTrue_OlderStamp_ReturnsFalse`,
  `Stamp_Entry_TouchesTrackContextAlbumArtists`, `Stamp_Entry_BareTrack_SkipsContext`,
  `Trim_DropsOldestDownToTrimTo_OncePerBatch`, `Merge_ReportsChangeOnlyWhenNewer`.
- EDIT `PlayLogStoreTests.cs`: `Append_RecordsAlbumAndArtists`, `Recency_ContainsTrackContextAlbumArtists_AfterAppend`,
  `Recency_RoundTripsThroughSidecarFile` (SaveAndWait → new store → same map), `Recency_SurvivesRingCap`
  (append 201 plays; the first play's artist keeps its stamp), `LoadFromDisk_RowsWithoutAlbumOrArtists_StillStampTrackAndContext`
  (write a JSON fixture with only `track/context/kind/atMs`), `MergeRecency_NewerBumpsVersion_OlderDoesNot`,
  `Clear_DropsRecencyAndSidecar`, `ArtistUris_NullForNoArtists`.
- NEW `RecentsRecencyTests.cs` (Core types only): `Stamps_SingleRow_ContextRow_Members`, `Stamps_SkipsZeroTimes`,
  `TrackStamps_ResolvesAlbumAndArtists_NewestMemberTimeWins`, `TrackStamps_UnresolvedTrackContributesNothing`.

## CHANGELOG (`## [0.2.7] - unreleased` › Fixed)
- Selecting an artist (or album/podcast) in Your Library no longer throws the list back to the top and then scrolls
  it back: the navigator keeps its scroll position across every refresh, and a refresh that changes nothing no longer
  rebuilds the list at all. (#E)
- "Recents" in Your Library › Artists / Albums / Podcasts — and in an artist's discography column — now means
  recently *played*: what you listened to most recently comes first (including plays from other devices, via your
  listening history), everything you have not played keeps its old order below. "Recently added" now really is
  newest-added first (it listed the oldest first), and the direction chevron works for every sort. (#F)

Commit bodies: `Fixes #E` / `Fixes #F` (issue numbers to be created — the release gate matches bullet ↔ commit).

## Sequencing (Sonnet subagents on disjoint files; the orchestrator builds/tests/launches)

| # | Who | Files (exclusive) | Depends on | Notes |
|---|---|---|---|---|
| 0 | orchestrator | `src/apps/Wavee.Tests/Wavee.Tests.csproj` | — | Add `Compile Include` for `..\Wavee\Features\Library\LibraryNavOrder.cs` and `..\Wavee\App\PlayRecency.cs` (`App\PlayLogStore.cs` is already `:288`; Core is referenced). Do this FIRST so A/B never touch the csproj. |
| A | Sonnet | NEW `Features/Library/LibraryNavOrder.cs`, NEW `Wavee.Tests/LibraryNavOrderTests.cs` | 0 | Pure; the API above is the contract C codes against. |
| B | Sonnet | NEW `App/PlayRecency.cs`, `App/PlayLogStore.cs`, `Wavee.Tests/PlayRecencyTests.cs`, `Wavee.Tests/PlayLogStoreTests.cs`, `App/PlaybackBridge.cs` (**line ~1400 only** — the file carries foreign uncommitted edits; do not reformat) | 0 | `Append`/`MergeRecency`/`Recency`/`ArtistUris` signatures above are the contract C and D code against. |
| C | Sonnet | `Features/Library/LibraryPage.cs` | A, B (compiles against their signatures; can start in parallel) | E.2 + F.3: `Shape`, `ListBody` → `ItemsView.Create` + `ScrollKey`, `NoteNavKey`, `LibraryArtistPane.Body`/`FilterSortAlbums`. Delete `Filtered`, `NavHash`. Verify the AccentPill chrome is pixel-identical to the preset's (it is the same `SelectorVisuals.AccentPill`). |
| D | Sonnet | NEW `Wavee.Core/Library/RecentsRecency.cs`, `Features/Recents/RecentsPage.cs` (the two hooks), NEW `Wavee.Tests/RecentsRecencyTests.cs` | B | Pure helper + two call sites. |
| E | orchestrator | `CHANGELOG.md` | all | Bullets above; then the gates below. |

## Verification
1. `dotnet build Wavee.slnx` and `dotnet build Wavee.slnx -c Release` clean (`TreatWarningsAsErrors`); the engine
   is untouched (no `..\fluent-gpu` gates needed).
2. `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` green — baseline + the new `LibraryNavOrderTests`,
   `PlayRecencyTests`, `RecentsRecencyTests` and the extended `PlayLogStoreTests`.
3. **#E, real session** (the user's Debug instance is the same checkout — coordinate; a second `Wavee.exe` hands off
   and exits): open Library › Artists, scroll so a row sits at the bottom edge, then
   `powershell -File ops/release/tools/Drive-WaveeWindow.ps1 -Out before.png`,
   `-Click <x>,<y>` on that row (client DIP coords from `-Info`), `-Out after.png`. Expect: identical row positions in
   both captures, the accent bar on the clicked row, the right panes switched. Then grep the live log for
   `library.nav.remount` — a click must log **nothing**; a play (next track) may log one `reason=order` line, and the
   list must not move on it either (ScrollMemory restore).
4. **#F, real session**: with the sort pill on Recents ▴, the first rows must be the artists of the newest
   `play-log.json` entries (read-only cross-check of `%LOCALAPPDATA%\Wavee\WaveeMusic\play-log.json` /
   `play-recency.json`); open the Recents page once and confirm a cross-device artist moves up; flip the chevron and
   confirm the played block reverses but stays above the never-played block; Albums pane the same with album uris;
   pick an artist and confirm the discography's Recents puts a played release first.
5. **`--fake`** (`dotnet run --project src/apps/Wavee -- --fake`): no plays exist, so Recents must equal today's
   order and the list must not jump on selection (`Drive-WaveeWindow.ps1 -Click` on an artist row, capture
   before/after). To see the played block in fake mode, seed `play-log.json` with `spotify:artist:` uris from the
   export **only after backing up the user's `play-log.json` + `play-recency.json`**, and restore them afterwards —
   `CreateFake` reads the real `%LOCALAPPDATA%\Wavee\WaveeMusic` path (`Services.cs:396`).
6. Sidebar JumpBackIn (Played) and the taskbar jump list still list contexts exactly as before (`RecentContexts` is
   untouched).

## Follow-ups (out of scope here, worth their own issues)
- `StoreLibrarySource.OnStoreChange` re-publishes a whole collection on **every** entity write of that kind
  (`:842-869`), including cold→hot promotions of unrelated entities; `LibraryStore.Refresh` could coalesce per frame
  and skip a value whose uri sequence and facts are unchanged.
- `JoinSet` is an inner join over cache residency (`:690-696`): a followed artist whose entity was shed disappears
  from the Artists pane until something re-hydrates it. Liked Songs already solved this with an outer join and
  placeholder rows (`LikedMembershipJoin`); the artist/album/show sets should follow.
