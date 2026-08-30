# Blank TITLE + `0:00` duration on album and playlist rows — research handoff

Self-contained paste for Fable. **Do not implement.** Diagnose why track **Title** and **DurationMs** are missing on detail-page rows while other fields (sometimes all of them) are present. Date: 2026-08-27.

| Field | Value |
|---|---|
| Symptom | Detail track table: TITLE column empty; duration column `0:00` on every visible row |
| Surfaces | Album page (Sia — *1000 Forms Of Fear*) **and** playlist pages (High School Musical OST rows). Not Liked-only. Not one release. |
| Density | Both captures had **Cozy** selected. Treat layout as a *secondary* hypothesis; duration `0:00` is painted text, not a zero-width clip. |
| What still works | Playlist capture: Artist, Album, Date added (`Jul 30`), Plays (hundreds of millions), BPM · Camelot. Player bar can still show a real clock on a *different* playing track. Album capture: header (cover, `ALBUM · 2014`, title, artist link), About tiles (12 Songs, July 7 2014, label). Album Plays column was `—` (unknown), not a count. |
| Not the cause (until proven) | “The page never got a tracklist” — both pages have the right *row count*. “Facts-rail / LikedFacts” — album is `BadgeStyle.TypeYear` (ReleasePanel, not the liked bento). “Duration formatter” — `TrackExpandedFacts.TrackTime(0)` is `"0:00"` by design; the question is why `DurationMs` is 0. |
| Working tree | Playlist-facts-rail work in flight (Year on TrackV4, `LeanAlbumRef.date = 6`, `TrackHydrationCensus`, duration-cell dash *started* as a symptom patch). **Do not treat that dash as the fix.** Do not edit PlayPlay-private paths. |

---

## 0. What “repair” actually is (and why it is a smell)

There is no general “fix the row” daemon. **Repair** is one named step on the **album** ladder only:

`AlbumHydration.ContinueAsync` (`src/apps/Wavee/Backend/Hydration/AlbumHydration.cs`)

```
if album not yet Open:
    collect disc rows where TrackUnnamed
    EnsureManyAsync(those track uris, Identity)   // ← “TrackV4 repair”
    RebuildTracklists()                           // copy store tracks back onto Album.Tracks
    if still not Open: getAlbum fallback
then Rich traits / Full getAlbum …
```

Comments in that file state the intended world:

> AlbumV4’s disc rows are **gid-only** for tracks the album entity did not carry names for. Without this an album opens with a list of blank rows.

So the product currently **accepts** an AlbumV4 tracklist that has the right *count* and the wrong *identity* (empty `name` / `duration = 0`), then hopes a **second** TrackV4 POST fills the shared Track plane, then **rebuilds** the album’s denormalized `Tracks` list from those store rows.

That is why the word exists. It is not a Spotify API named Repair. It is Wavee compensating for a thin nested message.

**Playlist has no equivalent step on `PlaylistHydration`.** Membership is uris + `added_at` / `added_by`. Filling Title/Duration is supposed to be `PlaylistFetcher`’s hydrate delegate at **Identity** (`PlaylistFetcher.cs` header comment: *hands membership uris to a hydrate delegate (the facade at Identity)*).

---

## 1. How a row reaches the table

```
Album page
  GetAlbumAsync(Rich) → AlbumHydration
    step 0: AlbumV4 → ProjectAlbum upserts Album + nested Track stubs
    Open:   TrackV4 “repair” (Identity) + RebuildTracklists
    Rich:   traits (RowBundle|PlayCount|Publishing) + RebuildTracklists again
    Full:   getAlbum envelope (label / versions / MoreBy) — DetailTrailing, below the fold
  DetailPage.MapAlbum reads album.Tracks VERBATIM   // denormalized copy

Playlist page
  GetPlaylistAsync(Open) → PlaylistHydration (header + LibrarySync membership; traits on pump)
  PlaylistFetcher.HydrateMembershipAsync → façade Identity on member uris
  StoreLibrarySource.JoinMembership: GetTrack(itemUri) inner join + stamp AddedAt/AddedBy
  DetailPage maps that list
```

`JoinMembership` (`StoreLibrarySource.cs` ~579–592): if `GetTrack` is null the row is **omitted**. Visible playlist rows **are** store Track records. Empty TITLE + `0:00` means the **store Track** has `Title == ""` (or title==uri, which would *show* the uri, not a blank) and `DurationMs <= 0`.

Album rows are **not** a live join. They are `Album.Tracks`. Store entities can be named while the denormalized list is still blank if `RebuildTracklists` did not run after the write that mattered, or a later `UpsertAlbum` replaced `Tracks` (`StoreEntityMerge.Album`: `Tracks = Has(incoming.Tracks) ? incoming : current`).

---

## 2. The playlist screenshot is the load-bearing clue

Traits are **not** identity. `TraitSet.RowBundle` = Video | AudioAttributes | Descriptors | VisualIdentity. `PlayCount` is kind 185. None of those write `Title` or `DurationMs`.

| Field on playlist row | Source (intended) | Playlist capture |
|---|---|---|
| Title | TrackV4 `name = 2` / getTrack | **empty** |
| DurationMs | TrackV4 `duration = 7` | **0 → cell `0:00`** |
| Artists | TrackV4 `artist = 4` | named |
| Album | TrackV4 `album = 3` | named |
| AddedAt | playlist4 membership | `Jul 30` |
| PlayCount | kind 185 | millions |
| Tempo / Camelot | kind 222 (RowBundle) | present |

So this is **not** “hydration never ran.” Facets landed. **The two TrackV4 identity scalars did not** (or they were written and then lost on the object the table reads).

Album capture could be the same shape with fewer columns: the album table does not show Artist/Album, and Plays was still `—` (185 not joined onto `Album.Tracks`, or never landed). About’s Label tile is getAlbum Full (kind 183 does not carry label — `PublishingProjector`).

`HydrationLevels.Of(Track)`: empty title → **None**, not Identity. Open needs named artists + named album + usable image + `DurationMs > 0`. A playlist row with album+artists+bpm+plays and empty title is **None** with a lot of jewellery.

`TrackUnnamed` = `TitleMissing` OR artists-need-names. Empty title keeps the album at **Identity** even with 12 rows, so album repair *should* still be eligible (`LevelOf < Open`).

---

## 3. Ranked hypotheses (prove or kill; do not shotgun-fix)

### H1 — TrackV4 projection writes empty `t.Name` / `t.Duration` while album+artists parse

`ExtendedMetadataSource.ProjectTrack` (`ExtendedMetadataSource.cs` ~237–257):

```
UpsertTrack(new Track(id, uri, t.Name, artists, album, t.Duration, … Year: year))
```

Same `LeanTrack` as AlbumV4 nested discs. `name = 2`, `album = 3`, `artist = 4`, `duration = 7`.

Recent change: `LeanAlbumRef.date = 6` (`lean_metadata.proto`) so TrackV4 can persist `Track.Year`. Nested parse of album.date should not steal track name/duration. **Still verify** against a real payload: `InvalidProtocolBufferException` is swallowed per entity (`ExtendedMetadataSource` ~232) → that uri never lands from TrackV4 at all.

**Kill if:** a captured TrackV4 for one of the blank rows has `name` + `duration` and `ProjectTrack` on those bytes produces a named Track.

### H2 — Identity was asked, fell short, ledger sealed Partial, later asks skip

`SpotifyProviderHydrator.EnsureManyAsync` (~107–119): if `TryPeek` is **Partial** (exhausted), the uri is skipped **even when `LevelOf` is still None**.

Album repair asks **Identity**, not Open. `PlayableHydration.ContinueAsync`: Identity is “step 0 and nothing else” — **no getTrack**. getTrack only runs at Open+ (`RepairAsync`, cap 8 per batch).

If TrackV4 returns a row that is still `TitleMissing`, Identity is not reached → Partial seal → **the blank is sticky for the exhausted TTL**.

**Kill if:** logs show `hydration.ensure` fetched TrackV4 for those uris *after* first paint, or ledger has no Partial for them.

### H3 — Album denormalized `Tracks` clobbered after a good repair (album-only)

`FetchEnvelopeAsync` upserts each envelope track (merge keeps a known Title/Duration on the **store** entity if incoming is empty) then `UpsertAlbum(album)` with the envelope’s **own** `Tracks` list. Merge **replaces** `Album.Tracks` whenever incoming has a non-empty list.

UI reads `MapAlbum` → `a.Tracks`. Store can be healthy; the table still blank.

Playlist **cannot** be this: `JoinMembership` reads `GetTrack`.

**Kill if:** `GetTrack(uri).Title` is empty in the same session as the blank playlist rows.

### H4 — Thin seed + traits, TrackV4 never applied

Writers of `new Track(...)` besides `ProjectTrack` / `ProjectAlbum`: `SpotifyExportMapper` (getAlbum/getTrack), `PlaylistExtenderClient`, cluster/connect paths (`ContextResolver` / `LiveContextResolver` seed `Title = uri`, which would **show** the uri, not a blank), optimistic `DetailPage` pending tracks (`Title = ""`).

A uri-only seed with later 185/222 would match playlist jewellery **if** album+artist names also came from somewhere that is not TrackV4. RowBundle does not write those. So H4 needs a writer that sets Artists+Album without Title+Duration.

**Kill if:** no such writer exists, or store rows never appear before the TrackV4 POST.

### H5 — UI: TITLE column width / Cozy / frozen props (secondary)

Both captures: Cozy. A squeezed title cell could look blank **if titles exist**. It does **not** explain painted `0:00` unless `DurationMs` is 0.

`TrackRow.EagerRowHost` uses re-pushed `UseProps` (`Embed.Comp(props, factory)`). Parent must re-render on store change. Playlist `JoinMembership` allocates a new list each compose — should refresh.

**Kill if:** debugger / a one-line log on `Track.Title` at `MapAlbum` / `JoinMembership` is empty. Then it is data, not layout.

### H6 — Persistence restore of thin columns without Title/DurationMs

Pinned tracks persist full JSON (`CachedStore.PersistTrack`). Albums **strip `Tracks`**. A restored album is Identity until the ladder re-runs. Restored **track** blobs should still have Title. Worth checking a blank uri’s cold row if the session is a restart.

---

## 4. What “Open” vs “Identity” means for a playable

From `HydrationLevels.Of(Track)` (`Wavee.Core/Hydration/HydrationLevel.cs`):

| Rung | Predicate |
|---|---|
| None | null or `TitleMissing` (empty **or** `title == uri`) |
| Identity | has a real title, but not Open |
| Open ≡ Rich | named artist[0], named album, usable image, `DurationMs > 0` |
| Full | + `Availability` (getTrack / TrackV4 files) |

Album Open = named tracklist (no `TrackUnnamed` row) + `Hydration >= Tracks`.

Tests that pin the *intended* album repair: `AlbumHydrationTests.Open_UnnamedRows_OneIdentityRepairThenTheTracklistIsRebuilt`, `HydrationWasteTests.AlbumWithUnnamedDiscRows_RepairsInOneBatchedPost_NotOnePerRow`.

---

## 5. Evidence to gather (one session)

Logs: `%LOCALAPPDATA%\Wavee\logs\wavee-*.log`

```
hydration.album.repair          Debug   unnamed disc-row batch (count only)
hydration.album.repair.fail     Warning
hydration.album.envelope        Info    getAlbum landed (why=fallback|full, tracks=N)
hydration.ensure                Debug   asked/fresh/fetched/reached per batch
hydration.tracks.gaps           Info    NEW census after a Track-kind EnsureMany / trait page
                                        (n, rungs, gaps title=/duration=/playcount=, sample uris)
hydration.playable.envelope     Warning getTrack repair (Open+ only)
```

For one blank playlist row uri and one blank album disc uri:

1. `HydrationLevels.Of(GetTrack(uri))` — expect **None** if Title empty.
2. `GetTrack(uri)` vs `GetAlbum(album).Tracks[i]` Title/DurationMs/PlayCount (album only).
3. Whether a TrackV4 POST for that uri appears in the session (API debug / Fiddler / `hydration.ensure` fetched>0).
4. Ledger: Partial vs Reached for `(uri, Identity)`.
5. Census line: `title=thin_seed` vs duration `open_predicate` vs playcount `trait_*`.

Do not launch the app from the agent unless the human already has it open. The human reproduces.

---

## 6. Code map (read these, don’t restyle them)

| Piece | Path |
|---|---|
| Album ladder + RebuildTracklists + getAlbum write | `src/apps/Wavee/Backend/Hydration/AlbumHydration.cs` |
| Track/Episode ladder, Identity = V4 only, getTrack at Open | `src/apps/Wavee/Backend/Hydration/PlayableHydration.cs` |
| Playlist ladder (no member TrackV4) | `src/apps/Wavee/Backend/Hydration/PlaylistHydration.cs` |
| Membership → Identity hydrate | `src/apps/Wavee/Backend/Playlists/PlaylistFetcher.cs` (`HydrateAsync`) |
| Join + `GetAlbumAsync` | `src/apps/Wavee/Backend/Library/StoreLibrarySource.cs` |
| TrackV4 / AlbumV4 project | `src/apps/Wavee/Backend/Metadata/ExtendedMetadataSource.cs` `ProjectTrack` / `ProjectAlbum` |
| Lean schema | `src/apps/Wavee/SpotifyLive/Protos/lean_metadata.proto` (`LeanTrack`, `LeanAlbumRef.date = 6`) |
| Merge (keep nonzero Duration/Title/Year; Album.Tracks replace) | `src/apps/Wavee/Backend/Store.cs` `StoreEntityMerge` |
| Ledger skip on Partial | `src/apps/Wavee/Backend/Hydration/SpotifyProviderHydrator.cs` ~107–119, Publish Partial ~224–231 |
| Rung predicates | `src/apps/Wavee.Core/Hydration/HydrationLevel.cs` |
| Census (just added, Info) | `src/apps/Wavee/Backend/Hydration/TrackHydrationCensus.cs` |
| Table duration cell | `src/apps/Wavee/Components/TrackRow.cs` ~275–297 (`PlayCount <= 0` already dashes; `DurationMs == 0` still `TrackTime` → `0:00` unless the in-flight `DurationCell` lands) |
| MapAlbum | `src/apps/Wavee/Features/Detail/DetailPage.cs` `MapAlbum` |
| getAlbum mapper | `PathfinderEnvelopeFetch.AlbumAsync` → `SpotifyExportMapper.AlbumFromUnion` |

Out of scope: `src/apps/.native/**`, `src/apps/Wavee.PlayPlay/**`, `private-runtimes/**`.

---

## 7. Research questions (answer in order)

1. **Is Title empty in the store** at the moment the table paints, or only in the element tree? (H5 vs everything else.)
2. **Same TrackV4 message:** do `name` and `duration` exist on the wire for a blank row that already has album+artists in the store? (H1)
3. **Did Identity EnsureMany run** for those uris this session, and what did `LevelOf` become after it? (H2)
4. **Album only:** after getAlbum Full, does `GetTrack` have titles while `album.Tracks` does not? (H3)
5. **Who first `UpsertTrack`s** a row with `Title==""` and `DurationMs==0` but named Artists/Album? Grep is not enough — trace one uri from membership/AlbumV4 to the first write.
6. **Why Identity rather than Open** on album repair? Open is what buys getTrack. If TrackV4 is structurally nameless, Identity cannot succeed and Partial seals the blank. Is that the design, or a ladder bug?
7. **Why is this intermittent on playlists?** (“sometimes”) — pump drop, Partial TTL, first paint before `HydrateMembershipAsync`, Cozy-only, specific playlist formats (editorial vs user), restart vs warm session.

---

## 8. Honesty vs root cause

`TrackRow` already dashes unknown **plays** (`PlayCount <= 0` → `—`) and comments that formatting 0 duration as `0:00` “reads as a real, dismal track.” The expanded facts strip **omits** duration when `DurationMs == 0`. The table does not. A dash (or `leaf.Pending`) is display honesty. **It will not put Chandelier in the TITLE column.**

Success for this research: one sentence of the form *“writer W produces Title='' DurationMs=0; reader R paints that list; Identity/Open does not re-fetch because S.”*

---

## 9. Constraints for Fable

- Research only unless the human asks to implement.
- No `FG_*` kill switches. No source-text tests. Props freeze at mount — if you propose a UI fix, say how data reaches the row (`UseProps` / signal / Key).
- Verify with `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` only if you write tests; do not launch Wavee.
- Do not weaken `StoreEntityMerge` “keep known Title/Duration” without a failing test that shows incoming empty wiping a name.

---

## 10. Resolved as (2026-08-27 — verification pass)

Every code claim above was checked against the working tree and the live logs (`wavee-2026082{6,7}.log`, session
`eaace2ed`). Corrections first, then what the blank rows actually are, then the fix.

### 10.1 Corrections to the text above

- **§3 H1 / §6 — `LeanAlbumRef.date = 6` is not a suspect.** Field numbers and types match `metadata.proto` `Album.date = 6`
  (`Date{sint32…}`) exactly, and `LeanAlbumGroup` reuses the same shape over a real `Album`. More decisively, the run
  that produced both captures used `bin/Debug/net10.0/Wavee.exe` built 08-26 12:11 — **before** the proto edit
  (08-27 15:20), the census (15:29) and the `DurationCell` change (15:40). None of the working-tree work was in the
  observed binary. `0:00` is simply the old `TrackTime` path.
- **§6 `TrackRow` — stale.** The working tree already dashes `DurationMs == 0` through `DetailFormat.DurationCell`
  (`TrackRow.cs:293`, `:436` → `TrackExpandedFacts.cs:222`). `PlayCount <= 0 → —` is the *Plays* cell, not duration.
- **§5 census — `open_predicate` was the *image* reason**; duration had no reason field (fixed in Phase 5d below).
- **§3 H1 — the swallowed `InvalidProtocolBufferException` (`ExtendedMetadataSource.cs:232`) was unlogged**, but the uri is
  not added to `landed`, so the ledger does **not** seal it: a silent retry, not a permanent loss.
- **§0 — the hydrate delegate is wired in `LiveSessionHost.cs:587-591`, not `Services.cs`.**
- **§3 H4 — "optimistic pending tracks"** are `DetailPage.PendingSeed` skeleton rows with `DurationMs = 180_000`, never upserted.
- **§5 — logs are Info-only.** `hydration.ensure`, `hydration.album.repair`, `hydration.catalog.fetch` are Debug and were
  never captured; the evidence plan needs Debug logging and a rebuilt binary.

### 10.2 What the blank rows are

- **Only one writer** produces named artists + named album + `Title == ""` + `DurationMs == 0`: the **AlbumV4 nested disc
  stub** in `ExtendedMetadataSource.ProjectAlbum` (`:331-334`) — parent `albumRef` named, album artists as fallback,
  `t.Name`/`t.Duration` may be empty, `UpsertTrack` unconditional. The getAlbum envelope rows (`SpotifyExportMapper.AlbumFromUnion:84-88`)
  are a second candidate if the Pathfinder `tracksV2.items[].track` shape drifts (title, duration **and** playcount all
  read from it — which also explains Plays `—` on the album).
- **Album:** `Album.Tracks` is replaced wholesale by whichever writer lands last (`StoreEntityMerge.Album`, `Store.cs:189`);
  there was no `RebuildTracklists` after the Full rung, and the envelope pins `limit 50`, so a >50-track album was also
  **truncated to 50** on Full. The log shows Sia's album landing `why=full tracks=12` in the session — the table painted
  envelope rows verbatim.
- **Playlist:** `JoinMembership` paints any resident row. The member Identity ask is **one-shot** (cold `FetchPlaylistAsync`
  only; `/diff` hydrates added uris only; the ladder is traits-only; `DetailPage` never asks per row). Playlist members
  are pinned, so a stub row is **persisted and restored blank** (`boot.warm_rows=358` in the observed session). Traits
  (185/222) still decorate it — hence album/artist/plays/BPM with no title.
- Amplifiers: `Partial` seal (10 min), extended-metadata `Missing` cached 24 h, `Track.Artists` merge lacking the
  name-aware fold (`Store.cs:135`), `Playlist.Name` merge taking `""` verbatim (`:273`) + a bare `catch` on the header
  re-fetch (`PlaylistFetcher.cs:171`).

Success sentence: *writer `ProjectAlbum` (and possibly `AlbumFromUnion`) produces `Title='' DurationMs=0` rows with named
album/artists; `MapAlbum` paints the wholesale-replaced `Album.Tracks`, `JoinMembership` paints the persisted store stub;
Identity does not re-fetch because the playlist path asks exactly once per uri and the album ladder never re-joins after Full.*

### 10.3 Fix (approved plan, same date)

1. `StoreEntityMerge`: per-uri, never-shrink fold for `Album.Tracks`; name-aware fold for `Track.Artists`;
   `Playlist.Tracks`/`Collaborators`/`Show.Episodes` asserted read-model; `Playlist.Name` cannot be blanked.
2. `AlbumHydration`: envelope never seeds a shorter list over a longer one; `RebuildTracklists` after Full;
   `hydration.album.rows.thin` census.
3. `PlaylistHydration`: `LevelOf` scans members (as `CollectionHydration`/`ShowHydration` do); Open re-asks thin members at
   Identity; `RootlistOpenPlan` checks level not presence; bare catch logged.
4. `StoreLibrarySource`: background set ask for saved albums/artists/shows; `Artist.TopTracks` re-joined at read.
5. `CachedStore`: unnamed rows never persisted; `hydration.project.malformed` + `hydration.playable.envelope.ok` logs;
   census duration reason.

Follow-up not in scope: moving `Album.Tracks` onto the generic membership plane (`SetMembership` + `ComposeAlbum` join, as
shows do), which would delete `RebuildTracklists` and give restored albums an offline tracklist.
