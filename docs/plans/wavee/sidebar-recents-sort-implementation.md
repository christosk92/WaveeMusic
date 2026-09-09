# Sidebar "Recents" hides a just-created playlist — implementation plan

**Status:** planned, not started. **Scope:** `src/apps/Wavee/Backend/Playlists/RootlistTreeBuilder.cs`,
`src/apps/Wavee.Core/Library/Sidebar.cs` (`PlaylistLeaf`), `src/apps/Wavee/Features/Sidebar/Data/{SidebarProjection.cs,
SidebarSort.cs, SidebarLibraryEntry.cs}`, `Wavee.Tests/SidebarSortTests.cs` (+ `SidebarProjectionTests.cs`). Engine-free
end to end — every touched file is already source-included by `Wavee.Tests`. Independent of the playlist-sync plan.

---

## 0. RCA — verified 2026-09-05

The rootlist → sidebar pipeline IS live (a create shows up in the tree on the next rebuild); the row simply lands where
the default sort puts a never-played row: below every playlist the user has ever played.

| Fact | Where (current) |
|---|---|
| Default sort is `Recents`. | `Features/Sidebar/SidebarDesign.cs:28` (`SidebarV3Sort { Recents = 0, … }`), `Data/SidebarBinderPipeline.cs:87` (`SidebarV3Query.Sort = SidebarV3Sort.Recents`), `Modes/LibraryV3/LibraryV3Metrics.cs:105` (`NormalizeSort` fallback), `Platform/AppSettings.cs:402` (`sidebar.v3.sort` default 0). **Path corrections:** `SidebarDesign.cs` is NOT under `Data/`; `LibraryV3Metrics.cs` is under `Modes/LibraryV3/`. |
| `Recents` partitions: played block first (by `LastPlayedMs` desc), never-played block second (by `SortStamp` desc), then name, then id. | `Data/SidebarSort.cs:104-115`. |
| A playlist row's `AddedAtMs` is always 0 and its `SortStamp` is always the local first-seen proxy. | `Data/SidebarProjection.cs:152-158` — comment "Playlists have NO add timestamp anywhere"; `Added(addedAt, p.Uri)` returns 0 because `GetLibraryAddedAtAsync` only fills albums/artists/shows. |
| `GetLibraryAddedAtAsync` excludes playlists "by construction". | `Backend/Library/StoreLibrarySource.cs:318-338` (**path correction:** `Backend/Library/`, not `Backend/Store/`). |
| The premise is stale: the rootlist DOES carry a per-row server ADD timestamp. | `Backend/Store.cs:26` `RootlistEntry(…, long AddedAtMs = 0)`; persisted (`Persistence/ColdStore.cs:39`, `rootlist.added_at`); captured on the GET from `ItemAttributes.timestamp` (`Backend/Playlists/PlaylistFetcher.cs:91-94`); a create stamps `nowMs` on its optimistic row (`PlaylistMutationSource.cs:55, 73-74` → `RootlistOps.ApplyLocally` → `EntriesFromUris(uris, stamps)` at `RootlistOps.cs:469-473, 502`). |
| **The exact drop point.** `RootlistTreeBuilder` flattens `RootlistEntry` → `RootlistMarker(Kind, Uri, GroupName)` and the leaf is `new PlaylistLeaf(resolve(uri))`; neither carries the stamp, and `PlaylistSummary` (`Wavee.Core/Library/Sidebar.cs:9-16`) has no field for it. | `Backend/Playlists/RootlistTreeBuilder.cs:15` (the record), `:21`, `:30` (the two copies), `:64` (the leaf). |

Consequence: a freshly created playlist has `LastPlayedMs = 0` → never-played block; `SortStamp = firstSeen.Stamp(id) = now`
→ first *within* that block; but the whole block sits under every played row (`SidebarSort.cs:106-107`). In a library with
dozens of played playlists it is off-screen until the user plays from it once (`PlayLogRevision` bumps and it re-sorts).

```
   rootlist (IStore.Rootlist())          RootlistTreeBuilder             PlaylistLeaf / PlaylistSummary        SidebarProjection
   RootlistEntry{Uri, AddedAtMs=T} ──►  RootlistMarker{Kind,Uri,Group} ──► PlaylistLeaf(PlaylistSummary{…})  ──► AddedAtMs = 0
                            ▲                    ▲  DROPPED HERE (:15/:21/:30)                                    SortStamp = firstSeen(now)
                 captured on GET (:91)                                                                             LastPlayedMs = 0
                 stamped on create (:55)                                                                           → never-played block
```

---

## 1. Decision: what "Recents" should mean

Spotify's own Library "Recents" is recent **activity**: playing, but also creating or saving. A playlist you created a
minute ago is at the top there. Wavee's `Recents` today is "recently PLAYED, never-played sink" — a deliberate F.7.5
decision (`SidebarSort.cs:91-103`) that was correct when playlists had no add stamp at all (a first-seen proxy is not an
activity). Now that the real stamp exists, the rule that matches both Spotify and the user's expectation is:

> **Activity recency** = `max(LastPlayedMs, AddedAtMs)`. Rows with any activity stamp form the first block, newest
> activity first; rows with neither (no play, no server add stamp) form the second block by `SortStamp` (the first-seen
> proxy), as today. `desc` still reverses within each block only.

Applied to **all** row kinds, not just owned playlists — albums/artists/shows already carry a real `AddedAtMs` from the
saved sets and suffer the identical "saved last week, never played, sits under something played in March" problem. The
ownership asymmetry the RCA floated (owned → `max`, followed → `LastPlayedMs` only) is **rejected**: following a playlist
is the user's own deliberate action at a known instant, exactly like saving an album; treating it as "no activity" would
keep the bug for followed playlists and add a branch nobody can explain. `IsOwner` is available on the row
(`SidebarLibraryEntry.cs:159`, set at `SidebarProjection.cs:164`) if product later wants it — one extra `&&`.

**Where this departs from the RCA's instinct** ("recently-played should very likely still outrank a merely-just-created
one"): under `max()` a playlist created *now* ranks above one played *yesterday*. That is the intended outcome — the user
just created it and is about to fill it; it is the single most recent thing they did in the library, and it is what
Spotify shows. What the RCA was right to protect is the other direction: a playlist played **more recently than** another
was **created** must stay above it (played yesterday beats created last week), and `max()` guarantees that. If product
wants play to win ties-in-spirit (e.g. "created today loses to played today"), the only honest lever is a discount on
the add stamp — `max(LastPlayedMs, AddedAtMs - AddedDiscountMs)` — flagged here as a knob, **not** implemented: no value
is defensible without a user in front of it.

### Precise ordering (Recents, natural direction — `desc:false` at the comparator, which the planner maps from the V3
"reversed" flag: `SidebarRowPlanner.cs:1224-1226`, `LibraryV3Document.cs:184-195`)

Given, with `T` = now:

| Row | LastPlayedMs | AddedAtMs | SortStamp (first-seen) | Activity = max(played, added) |
|---|---|---|---|---|
| **B** "Road trip" — created just now, never played, owned | 0 | T | T | **T** |
| **A** "Gym" — played yesterday, added a year ago | T−1d | T−365d | T−365d | **T−1d** |
| **D** "Lo-fi beats" — followed 3 days ago (someone else's), never played | 0 | T−3d | T−3d | **T−3d** |
| **C** "Summer 2024" — played 60 days ago | T−60d | T−400d | T−400d | **T−60d** |
| **F** album "X" — saved 10 days ago, played 5 days ago | T−5d | T−10d | T−10d | **T−5d** |
| **E** "Old empty" — never played, no server stamp (adopted before timestamps) | 0 | 0 | T−100d | none |
| **G** "Older empty" — never played, no server stamp | 0 | 0 | T−200d | none |

Expected order: **B, A, D, F, C, E, G**. Block 1 = {B, A, D, F, C} by activity desc; block 2 = {E, G} by SortStamp desc.
(D outranks F: Activity(D) = T−3d is more recent than Activity(F) = T−5d — an earlier draft of this table had these
two swapped, which is an arithmetic mistake, not a different intended rule.)
`desc:true` → **C, F, D, A, B, G, E** (each block reversed independently; block 2 never floats above block 1).

Ties inside a block: activity equal → `NameComparer` → ordinal `Id` (a strict total order — the
`EveryComparator_IsAntisymmetric_OverAMixedList` sweep at `SidebarSortTests.cs:231-256` must stay green).

---

## 2. Changes

### 2.1 Carry the stamp through the tree — `Backend/Playlists/RootlistTreeBuilder.cs` + `Wavee.Core/Library/Sidebar.cs`

`PlaylistLeaf` gains the row's server add stamp. `PlaylistSummary` is a catalog read-model shared with Home/detail
previews and stays untouched; the stamp is a *rootlist row* fact, so it rides the leaf, which is the rootlist's node.

```csharp
// Wavee.Core/Library/Sidebar.cs  (was: public sealed record PlaylistLeaf(PlaylistSummary Playlist) : PlaylistNode;)
/// <param name="AddedAtMs">The rootlist row's server ADD timestamp (playlist4 ItemAttributes.timestamp, unix ms) — when
/// the user created or followed this playlist. 0 = not captured (a row adopted before the rootlist carried timestamps),
/// in which case the sidebar falls back to its local first-seen proxy.</param>
public sealed record PlaylistLeaf(PlaylistSummary Playlist, long AddedAtMs = 0) : PlaylistNode;
```

```csharp
// RootlistTreeBuilder.cs :15 — the marker carries the stamp
public readonly record struct RootlistMarker(int Kind, string Uri, string? GroupName, long AddedAtMs = 0);

// :21 and :30 — both adapters copy it
for (int i = 0; i < entries.Count; i++)
    markers[i] = new RootlistMarker(entries[i].Kind, entries[i].Uri, entries[i].GroupName, entries[i].AddedAtMs);

// :64 — the leaf keeps it
var leaf = new PlaylistLeaf(resolve(e.Uri), e.AddedAtMs);
```

`ColdRootlistEntry` (`Persistence/ColdStore.cs:39`) already has `AddedAtMs`, so the cold overload at `:19-24` copies the
same field. No other `PlaylistLeaf` constructor call exists outside tests (`grep "new PlaylistLeaf("` — verify; test
helpers that build trees get the default `0`).

### 2.2 Feed it into the row — `Features/Sidebar/Data/SidebarProjection.cs:145-168`

```csharp
case PlaylistLeaf leaf:
{
    if (!wantPlaylists) break;
    var p = leaf.Playlist;
    string id = SidebarPinId.PlaylistPrefix + p.Uri;
    var flavor = FlavorOf(p);
    flavorMask |= (byte)(1 << (int)flavor);
    // The rootlist row's server ADD stamp (created / followed at) — the rootlist IS a timestamped marker stream
    // (ItemAttributes.timestamp, captured on the GET and stamped on a local create). 0 only for rows adopted before
    // timestamps were captured, which fall back to the first-seen proxy exactly as before.
    long added = leaf.AddedAtMs > 0 ? leaf.AddedAtMs : Added(addedAt, p.Uri);
    into.Add(new SidebarLibraryEntry(
        id, SidebarEntryKind.Playlist, p.Uri, p.Name ?? "", p.OwnerName ?? "",
        p.Cover, p.Cover is null ? p.MosaicTiles : null, p.TrackCount, added,
        SortStamp: added > 0 ? added : firstSeen.Stamp(id),
        LastVisitedTicksUtc: recency.LastVisitedTicks(id),
        SourceOrder: order++, Depth: depth, Circular: false, Flavor: flavor)
    {
        FolderId = folderId, FolderName = folderName,
        ParentFolderId = folderId, ParentFolderName = folderName,
        IsOwner = p.IsOwner, CanEdit = p.CanEdit, FirstArtistName = "",
        LastPlayedMs = LastPlayed(lastPlayed, p.Uri),
    });
    break;
}
```

Side effect, intended: **RecentlyAdded** (`SidebarSort.cs:117-128`) now sorts playlists by their real server stamp
instead of the first-seen proxy. Update its "HONEST LIMIT" doc comment (`:118-120`) — the limit is now only "rows adopted
before timestamps were captured tie on the proxy". `StoreLibrarySource.GetLibraryAddedAtAsync`'s comment (`:318-321`)
"Playlists are absent by construction: the rootlist … has no per-item date" is also stale — reword to "playlists get
their stamp from the rootlist row itself (RootlistTreeBuilder → PlaylistLeaf.AddedAtMs), not from this map".

### 2.3 The comparator — `Features/Sidebar/Data/SidebarSort.cs:91-115` and a row helper

```csharp
// SidebarLibraryEntry.cs — beside LastPlayedMs (:133)
/// <summary>The row's most recent user ACTIVITY: the later of its last play (<see cref="LastPlayedMs"/>) and its server
/// add stamp (<see cref="AddedAtMs"/> — created / followed / saved at). 0 = neither is known. This is what
/// <see cref="SidebarSort.Recents"/> sorts on.</summary>
public long ActivityMs => LastPlayedMs > AddedAtMs ? LastPlayedMs : AddedAtMs;
```

```csharp
/// <summary>Recents: most recent ACTIVITY — the later of last PLAYED (<c>PlayLogStore.Recency</c>,
/// <see cref="SidebarLibraryEntry.LastPlayedMs"/>) and ADDED (<see cref="SidebarLibraryEntry.AddedAtMs"/>: created,
/// followed or saved, from the rootlist row / saved-set stamp). Spotify's own "Recents" reads the same way, and it is
/// what makes a playlist created a moment ago land at the top instead of under a year of plays.
/// An activity-block-first, no-activity-block-second partition: everything with either stamp leads, newest activity
/// first; everything with neither (no play, no server add stamp — rows adopted before timestamps were captured) sinks
/// AS A BLOCK, ordered by SortStamp desc (the local first-seen proxy) then Name. The block split is applied BEFORE
/// (and is never affected by) <paramref name="desc"/>; `desc` reverses the ordering WITHIN each block independently.
///
/// <para>Clicking a row to open it does not move it (<see cref="SidebarLibraryEntry.LastVisitedTicksUtc"/> feeds only
/// the "recently opened" feed). HONEST LIMIT on the play half: a playlist or show is stamped only when playback
/// STARTED FROM that context (<c>PlayRecency.Stamp(in PlayLogEntry)</c>, <c>App/PlayRecency.cs:38-46</c>).</para></summary>
public static int Recents(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
{
    long aa = a.ActivityMs, ba = b.ActivityMs;
    bool ap = aa > 0, bp = ba > 0;
    if (ap != bp) return ap ? -1 : 1;

    int c = ap
        ? ba.CompareTo(aa)
        : b.SortStamp.CompareTo(a.SortStamp);
    if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
    if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
    return desc ? -c : c;
}
```

No change to `For`/`Apply`/`Effective` (`:38-77`), to the cached delegates, or to any other comparator. `SidebarRowPlanner`'s
`EntryOrder` calls the same `Recents` — nothing to touch there.

### 2.4 Is a rebuild triggered when the stamp lands?

Yes, already: the create writes the rootlist (`PlaylistMutationSource.cs:73-76`, `store.Bump("rootlist", …)`), the library
store republishes `PlaylistTree`, and the binder rebuilds (`SidebarProjectionBinder.cs:322, 351-357`). The stamp is on the
optimistic row from the first frame, so the new playlist sorts to the top of Recents **before** the server has even acked
the create. When the server's rootlist GET later replaces the rows, `ItemAttributes.timestamp` for that row is the same
instant we sent (the ADD carries `nowMs`, `PlaylistMutationSource.cs:74`) — no visible re-sort.

---

## 3. Tests — `Wavee.Tests/SidebarSortTests.cs`

The `Pl(...)` factory (`:16-21`) hard-codes `AddedAtMs: 0` and has no ownership parameter. Extend it (default keeps every
existing call site unchanged):

```csharp
static SidebarLibraryEntry Pl(string id, string name, string creator = "Owner",
                              long sortStamp = 0, long visited = 0, int order = 0, long played = 0,
                              long added = 0, bool owner = false) =>
    new("pl:spotify:playlist:" + id, SidebarEntryKind.Playlist, "spotify:playlist:" + id, name, creator,
        null, null, ChildCount: 0, AddedAtMs: added, SortStamp: sortStamp > 0 ? sortStamp : added, LastVisitedTicksUtc: visited,
        SourceOrder: order, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
    { LastPlayedMs = played, IsOwner = owner };

static SidebarLibraryEntry Album(string id, string name, long added = 0, long played = 0, int order = 0) =>
    new("album:spotify:album:" + id, SidebarEntryKind.Album, "spotify:album:" + id, name, "",
        null, null, ChildCount: 0, AddedAtMs: added, SortStamp: added, LastVisitedTicksUtc: 0,
        SourceOrder: order, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
    { LastPlayedMs = played };
```

New cases (the table in §1, `T = 1_000_000_000_000L`, one day = `86_400_000L`):

```csharp
// The bug: a playlist created a moment ago, never played, must not sink under a year of plays. It is the user's most
// recent library ACTIVITY, so it leads — and a play more recent than another row's creation still wins over it.
[Fact]
public void Recents_ActivityIsTheLaterOfPlayedAndAdded()
{
    const long T = 1_000_000_000_000L, Day = 86_400_000L;
    var list = new List<SidebarLibraryEntry>
    {
        Pl("c", "Summer 2024", played: T - 60 * Day, added: T - 400 * Day),
        Pl("e", "Old empty", sortStamp: T - 100 * Day),                          // no play, no server stamp
        Pl("a", "Gym", played: T - 1 * Day, added: T - 365 * Day),
        Pl("g", "Older empty", sortStamp: T - 200 * Day),
        Pl("d", "Lo-fi beats", creator: "Someone", added: T - 3 * Day),        // followed, never played
        Album("f", "X", added: T - 10 * Day, played: T - 5 * Day),
        Pl("b", "Road trip", added: T, owner: true),                            // created just now
    };
    Assert.Equal(new[] { "Road trip", "Gym", "X", "Lo-fi beats", "Summer 2024", "Old empty", "Older empty" },
                 Names(Sorted(list, SidebarV3Sort.Recents)));
    Assert.Equal(new[] { "Summer 2024", "Lo-fi beats", "X", "Gym", "Road trip", "Older empty", "Old empty" },
                 Names(Sorted(list, SidebarV3Sort.Recents, desc: true)));
}

// The invariant the bug violated, stated on its own so a future retune cannot quietly reintroduce it.
[Fact]
public void Recents_CreatedNowNeverPlayed_OutranksPlayedLongAgo()
{
    const long T = 1_000_000_000_000L, Day = 86_400_000L;
    var list = new List<SidebarLibraryEntry> { Pl("old", "Played in March", played: T - 180 * Day), Pl("new", "Created now", added: T) };
    Assert.Equal(new[] { "Created now", "Played in March" }, Names(Sorted(list, SidebarV3Sort.Recents)));
}

// Play still wins when it IS the more recent activity: created last week, vs played yesterday.
[Fact]
public void Recents_PlayedYesterday_OutranksCreatedLastWeek()
{
    const long T = 1_000_000_000_000L, Day = 86_400_000L;
    var list = new List<SidebarLibraryEntry> { Pl("n", "Created last week", added: T - 7 * Day), Pl("p", "Played yesterday", played: T - Day, added: T - 365 * Day) };
    Assert.Equal(new[] { "Played yesterday", "Created last week" }, Names(Sorted(list, SidebarV3Sort.Recents)));
}

// Ownership is NOT a factor (decision §1): a followed playlist's follow instant is the user's activity too.
[Fact]
public void Recents_FollowedRecently_RanksByItsFollowStamp_RegardlessOfOwnership()
{
    const long T = 1_000_000_000_000L, Day = 86_400_000L;
    var list = new List<SidebarLibraryEntry>
    {
        Pl("mine", "Mine, played a month ago", played: T - 30 * Day, owner: true),
        Pl("theirs", "Theirs, followed yesterday", creator: "Someone", added: T - Day, owner: false),
    };
    Assert.Equal(new[] { "Theirs, followed yesterday", "Mine, played a month ago" }, Names(Sorted(list, SidebarV3Sort.Recents)));
}

// Rows with NEITHER stamp still sink as a block, by the first-seen proxy — the pre-existing contract, unchanged.
[Fact]
public void Recents_NoActivityRows_StillSinkAsABlock_ByFirstSeen()
{
    var list = new List<SidebarLibraryEntry>
    {
        Pl("n1", "NeverOld", sortStamp: 10), Pl("v1", "Played", played: 5), Pl("n2", "NeverNew", sortStamp: 99),
        Pl("a1", "AddedOnly", added: 3),
    };
    Assert.Equal(new[] { "Played", "AddedOnly", "NeverNew", "NeverOld" }, Names(Sorted(list, SidebarV3Sort.Recents)));
}
```

Existing tests that keep passing unchanged: `Recents_OrdersByLastPlayedDescending` (`:42-52`),
`Recents_NeverPlayedSinkAsABlock_OrderedBySortStampThenName` (`:54-64` — its rows have `AddedAtMs: 0`, so nothing moves),
`Recents_Descending_ReversesEachBlockIndependently` (`:66-78`), `Recents_AVisitDoesNotReorder` (`:80-96`), the
antisymmetry sweep (`:231-256`).

Projection-level pin — `Wavee.Tests/SidebarProjectionTests.cs` (it already drives `SidebarProjection.Build` over a
`PlaylistLeaf` tree and applies `SidebarSort` at `:537`): one new case builds a tree from
`RootlistTreeBuilder.Build(RootlistTreeBuilder.EntriesFromUris(uris, stamps), resolve)` with a stamp on one uri and asserts
the projected row's `AddedAtMs == stamp` and `SortStamp == stamp`, and that a row with stamp 0 still gets `SortStamp ==
firstSeen.Stamp(id)`. A second case pins `RootlistTreeBuilder` directly: `Build(entries).OfType<PlaylistLeaf>().Single().AddedAtMs`
equals the entry's stamp, for both the live and the cold overloads.

---

## 4. Gates and files

`dotnet build Wavee.slnx` Debug + Release clean; `Wavee.Tests` green; `dotnet run --project src/apps/Wavee -- --fake`
then create a playlist from the sidebar and confirm it appears at the top of Recents without playing it (the FakeData
rootlist path uses the same `RootlistOps.ApplyLocally` → stamps flow).

| File | Change |
|---|---|
| `Wavee.Core/Library/Sidebar.cs` | `PlaylistLeaf(PlaylistSummary Playlist, long AddedAtMs = 0)` |
| `Backend/Playlists/RootlistTreeBuilder.cs` | `RootlistMarker` + both adapters + the leaf carry `AddedAtMs` (`:15, :21, :30, :64`) |
| `Features/Sidebar/Data/SidebarProjection.cs` | `:152-158` read `leaf.AddedAtMs` first |
| `Features/Sidebar/Data/SidebarLibraryEntry.cs` | `ActivityMs` computed member |
| `Features/Sidebar/Data/SidebarSort.cs` | `Recents` on `ActivityMs`; doc comments on `Recents` and `RecentlyAdded` |
| `Backend/Library/StoreLibrarySource.cs` | `:318-321` stale comment |
| `Wavee.Tests/SidebarSortTests.cs`, `SidebarProjectionTests.cs` (+ `RootlistTreeTests.cs`) | §3 |
