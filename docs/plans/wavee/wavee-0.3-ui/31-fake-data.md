# The fake-data seed (`--fake`): the fixture contract behind the Wave 5 gate — 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee.Core/Fakes/FakeData.cs` (633) · `Fakes/FakeSource.cs` (79) ·
> `Fakes/FakeSpotifySession.cs` (34 — the whole file; its members are `:8-15`, `:19-22`, `:33`) ·
> `Sources/FakePodcastSource.cs` (21 — `:10`, `:13`, `:17-20`) · `Sources/LocalSource.cs` (85) ·
> `Sources/UserPlaylistSource.cs` (145) · `Sources/AggregateCatalog.cs` (303) · `Spotify/SpotifyExport.cs` (127) ·
> `Spotify/SpotifyExportSource.cs` (186) · `Spotify/SpotifyExportMapper.cs` (2 025, of which `Hash` / `SynthCount` /
> `MapArtist` are load-bearing here) · `src/apps/Wavee/App/Services.cs:584-634` (`CreateFake`) + the **thirteen** `Null*`
> seats (`:320-325`, `:358-364`) · `src/apps/Wavee/Program.cs:439-440` (the flag) ·
> `src/apps/Wavee/App/FakeAppUpdateService.cs` (138 — the update simulator; developer mode, not `--fake`) ·
> `src/apps/Wavee/Features/Sidebar/Shared/SidebarMiniature.cs:128, :134-137, :156-162, :263-266, :272, :292, :328`
> (the one surface that indexes the seed by hand) · `src/apps/Wavee/Features/Home/HomePage.cs:90` +
> `FakeData.cs:587-610` (the skeleton seed) · `src/apps/Wavee/Features/Detail/ArtistPage.cs:184, :199` (the seed on a
> **production** path) · `src/apps/Wavee/Features/Diagnostics/WaveeNavProbe.cs:34-43` + `:44-48` (the 13-route list the
> Wave 5 gate cites).
> **Assets**: `src/apps/Wavee/assets/covers/cover00..15.jpg` + `liked-songs-300.png` (321 844 B) and
> `src/apps/Wavee/assets/spotify/{home.json, playlists.json, icedamericano.json, artist-maroon5.json}` (1 579 823 B),
> both copied by `Wavee.csproj:196` (`<Content Include="assets\**\*" …>`).
> **Code in scope ≈ 1 613 lines + 1.9 MB of fixture assets.**
> | 0.3 target: **`Entities/Entities.Fake.cs`** — `Entities.SeedFake()`, the file plan §5 names in four words and does
> not file; plus `Platform/Platform.cs` (args + seed clock), one line in `App.cs` (§3.5), and the seed's own test file
> `src/apps/Wavee.Tests/EntitiesFakeTests.cs`
> | **Wave 5, owner Q** ("Queue.UI.cs + the fake data seed"). This chapter is that clause's specification.

After Wave 0 every 0.2.9 path above lives under `src/apps/_old/…` at the same relative spelling.

---

This chapter exists because of one sentence in the plan. Wave 5's gate reads *"`dotnet run -- --fake` opens every
route from the nav probe list"* (plan §5, Wave 5), and §8.3 repeats it as a definition-of-done item. **Opening is not
rendering.** Thirteen `Null*Service` seats (`Services.cs:320-325`, `:358-364`), `UnsupportedPlaybackPlayer` and
`NoLyricsProvider` mean a 0.3 tree that
compiles and routes passes that gate with a majority of its surfaces showing an empty state — which is also what the
0.2.9 build does today on seven of them. Chapter 29 §2 W17 measured the shape of the problem; this chapter turns it
into the seed's contract: **per surface, what must be in `SeedFake()` for the LOADED state to render, which
wireframed states are reachable, which are not, and why.**

It supersedes chapter 29 §7.1 wherever the two disagree; §11 lists every disagreement and the `file:line` that
settles it. Chapter 29 keeps the OS-surface, ambient-cadence and first-run material; the fixture inventory moves
here, and ch 29's own index row ("the `--fake` fixture inventory | **29**") should read **31**.

---

## 0. The non-negotiables

1. **`--fake` is not a data source; it is a SECOND source stack behind the same façade.** `CreateFake`
   (`Services.cs:584-634`) builds a `SourceRegistry` of seven sources and hands the UI the identical
   `AggregateCatalog` + `HydrationRouter` the real backend gets (`:623`, `:629`). Nothing downstream of the façade
   knows which backend it has. In 0.3 the equivalent is: **`SeedFake()` writes staging columns and edges through the
   same commit path `Spotify.Decode` writes through, and then does nothing else.** An `if (fake)` branch anywhere in
   `Entities/`, `Shell/` or a page is a defect.
2. **The seed owns identity, not rendering.** Every fixture is addressed by a uri whose provider byte the one parser
   decides (`EntityUri.Parse`, plan §4.1) — `spotify:album:al{i}`, `spotify:artist:ar{n}`, `spotify:playlist:pl{i}`,
   `spotify:track:tr{i}`, `spotify:collection:tracks`, `wavee:local:track:{i}`, `wavee:show:{i}`,
   `wavee:episode:{s}:{i}`, `wavee:concert:{h}:{k}`, `wavee:playlist:{n}`. A surface never asks "am I fake".
3. **Index-addressable, total, and pure.** Every generator is a function of one `int`, and `Wrap(i, n)` makes it
   total for *any* int including the negative products a uri hash can produce (`FakeData.cs:13`, `:26`, `:52-56`).
   Chapter 26 discovered why this is not a nicety: `SidebarMiniature` indexes eight fixed playlist slots and one
   artist slot by hand (`:128`, `:134-137`, `:156-162`, `:272`, `:292`, `:328`), so a reshuffling seed makes a modal
   dialog flicker between opens. **`SeedFake.Playlist(7)` must be the same playlist in every process, on every
   launch, forever.**
4. **0.2.9 breaks rule 3 in exactly two places, and both must be fixed, not ported.** `FakeData.cs:147` seeds every
   artist facet from `Math.Abs(s.Artist.GetHashCode())` — .NET Core randomises `string.GetHashCode()` per process, so
   monthly listeners, followers, verified, world rank, and the *presence* of concerts / merch / playlists / videos /
   cities / gallery / pinned item all change between two launches of the same binary. And seven date fields are
   anchored on `DateTimeOffset.Now` (`:176`, `:208`, `:213`, `:328`, `:331`, `:410`, `:417`), so they change between
   two launches on different days. §7.3 specifies the replacements.
5. **A seeded surface renders its LOADED state or the gate does not count it.** The Wave 5 gate's unit is not "the
   route opened" but "the route reached its Ready arm with rows". §2 W14 is the matrix; §10 is the procedure.
6. **A state that cannot be faked belongs to the live checklist, not to Wave 5.** A live Connect transfer, a dealer
   push, a real device roster, real search ranking and a real DRM video licence are unreachable by construction
   (§7.4). Plan §8.4 already owns them; the Wave 5 gate must not pretend to.
7. **The seed is not offline for artwork.** `FakeData.Cover(i)` resolves a bundled local jpg (`:24-29`), but the
   bundled Home document carries **≈ 306 remote image urls across ten CDN hosts** — counted from
   `assets/spotify/home.json`: `i.scdn.co` **166**, `pickasso.spotifycdn.com` **65**, `image-cdn-fa.spotifycdn.com`
   **32**, `image-cdn-ak.spotifycdn.com` **18**, `seed-mix-image.spotifycdn.com` **10**, `daylist.spotifycdn.com`
   **8**, `misc.scdn.co` **3**, `lexicon-assets.spotifycdn.com` **2**, `newjams-images.scdn.co` **1**,
   `shareables.spotify.com` **1** (plus 20 `open.spotify.com` share links, which are not images). `playlists.json`
   carries **≈ 63 more** across nine hosts (`i.scdn.co` 37, `pickasso` 11, `misc.scdn.co` 3, `daylist` 3,
   `mosaic.scdn.co` 3, `image-cdn-ak` 2, `seed-mix-image` 2, `image-cdn-fa` 1, `misc.spotifycdn.com` 1). Home art in
   `--fake` needs the network; every synthesized surface does not. A screenshot procedure that does not say which
   condition it ran under is not a parity item. **Chapter 29 and this chapter's own first pass said "166 + 3 + 1 =
   170"; that counted one host family and missed six.**
8. **Two fixtures must agree with each other or the page lies.** `FakeData.Playlist`'s own comment states the rule
   (`:311-313`): the header a card or sidebar row advertises for a uri and the header the detail load returns for
   that uri must match. **0.2.9 violates it** — see §0.10a — and 0.3 must not.
9. **Blank-shaped seeds are fixtures too.** `FakeData.HomeSeed` (`:587-610`) is not demo content: it is the tree the
   Home skeleton is *derived* from (`HomePage.cs:90`, `Skel.Region`), and its module order and counts are the
   silhouette. A seed that disagrees with the loaded layout turns the reveal into a jump cut (ch 10 §2 W1).
   `SeedFake` owns the blank shape and the loaded shape, and they are two halves of one fixture.
10. **Known 0.2.9 defects this chapter records rather than ports.** (a) `spotify:playlist:pl{i}` is owned by
    `SpotifyExportSource` (registered first, `Services.cs:615`), which has no header or card for it, so the detail
    page renders the generic `"Playlist"` / owner `"Spotify"` / **no cover** arm (`SpotifyExportSource.cs:33`) —
    `FakeData.Playlist(i)` is never served for it (§2 W7). (b) Neither fake source overrides `GetDiscographyAsync`,
    so both fall through to the interface default that slices the artist's **6** in-memory albums
    (`ICatalogSource.cs:51-60`); `FakeData.Discography` (`:112-128`), which synthesizes 380-698 singles per facet,
    has **no caller** and the artist grid never virtualizes (§2 W6). (c) `LibraryStats()` reports 7 podcasts (`:477`)
    while `ShowSeed` has 8 (`:374-379`). (d) `ContextTracks("spotify:collection:tracks")` returns `LikedSongs(161)`
    (`:466`) while the Liked page renders `LikedSongs(export.LikedCount = 166)` (`SpotifyExportSource.cs:140`) — the
    page and its own play context disagree by five rows. (e) **The export serves 21 playlists, not 22.**
    `LoadLibrary` skips `format == "listen-later"` as well as the `Folder`
    (`SpotifyExport.cs:70`, `:73`), and `playlists.json`'s 22 `Playlist` items include one — "Your Episodes"
    (`spotify:playlist:37i9dQZF1FgnTBfUlzkeKt`). So `_summaries` / `_headers` hold **21**, and every "22 exported
    playlists" claim below, in ch 29 and in the plan is off by one. (f) **Six fixtures exist twice under two
    different uris.** The synthetic `PlaylistSeed` names (`:481-490`) were copied from the export, so
    `playlists.json` *also* carries "Dalkom Cafe" (`spotify:playlist:37i9dQZF1DX5g856aiKiDS`, owner **Spotify**,
    format `editorial`), "Iced Americano", "우울해", "My Playlist #6", "Nostalgia 2000s Mix" and "Henry Moodie Mix".
    `AggregateCatalog.GetPlaylistTreeAsync` **concatenates** every source's tree (`AggregateCatalog.cs:119-124`), and
    `SpotifyExportSource` does not override it, so the interface default flattens its 21 summaries
    (`ICatalogSource.cs:88-89`) — the `--fake` sidebar shows **21 export leaves followed by the 5 synthetic nodes**,
    with six names appearing twice, each opening a different page (§2 W13). (g) **Home renders "Jump back in"
    twice**, and one of them is never localized. `SpotifyHomeComposer.Compose` prepends a synthetic `QuickGrid`
    module built from the library summaries, titled `t.JumpBackIn` (`SpotifyHomeComposer.cs:40-46`, `QuickPicks = 9`
    at `:28`), while `home.json` section 4 is the server's own "Jump back in". `SpotifyExport.LoadHome` calls
    `Compose(home, _summaries)` with **no titles record** (`SpotifyExport.cs:123`), so the synthetic row takes
    `HomeModuleTitles.Default`'s hard-coded English (`SpotifyHomeComposer.cs:8-21`) instead of the loc-resolved
    `HomeModuleCopy.Titles` the live path passes — switching language in `--fake` moves one of the two rows and not
    the other.
11. **Five fixtures are built and unreachable.** `FakeData.Lyrics` (`:543-567`, no caller anywhere),
    `DefaultQueue()` (`:569-576`, no caller), `SearchSeed` (`:611`, no caller), `Discography` (`:112-128`, no caller)
    and `UserPlaylists()` (`:519-521`, referenced only from a doc comment). Chapter 29 found two; there are five.
    Four of them are exactly the fixtures the dark surfaces need.
12. **The seed must be tested, not just written.** 0.2.9 has tests for `FakeSource`'s *ownership*
    (`HydrationRouterTests.cs:267-290`) and for the aggregate's merge (`AggregateCatalogSearchTests.cs:16`, `:36`),
    and **none** for the seed's contents. `Entities.SeedFake` is a pure core function over the table set; per plan
    D17 it gets `Wavee.Tests/EntitiesFakeTests.cs` pinning counts, indices, determinism and the cross-surface
    agreement rule (§8).
13. **Nothing in the seed may read the wall clock at read time.** Time enters once, as `SeedFake(long now0)` (§7.3),
    and every dated fixture is an offset from that one value. A generator that calls `DateTimeOffset.Now` per call
    cannot be screenshot-diffed and cannot be unit-tested.
14. **The seed is not the only state `--fake` launches with.** `CreateFake` takes the **real** settings store
    (`AppDataSettings.ForUnpackaged("Wavee","Wavee")`, `Services.cs:593`), and the liked seed is applied **only on a
    first run**: `savedSeed` is the first 166 liked uris *when the stored `SavedLibrary` key is empty*, and the
    persisted newline-joined set otherwise (`Services.cs:600-604`). Theme, sidebar-layout document, home-layout
    document, developer mode and the saved/followed set all survive a relaunch. So "two launches are pixel-identical"
    is a claim about `SeedFake` **plus a known store state**; §7.2 rule E and §10 item 40 now say so, and 0.2.9's
    procedure never did.
15. **`--fake` is selected by an argv flag, but one of its fixtures is selected by an environment variable.**
    `WAVEE_FAKE_CHALLENGE` (`WaveeApp.cs:276`, `:323`, `:343`, `:388`, via `Diag.EnvFlag`) is the only way to reach
    the pairing-card takeover. CLAUDE.md's working rules forbid environment-variable switches for behaviour or
    verification, so in 0.3 it becomes `Platform.Args.FakeChallenge` (DATA GAP 4), not a second `EnvFlag`.

---

## 1. Anatomy

### 1.1 The 0.2.9 composition, as it actually resolves

Node names are the types; `file:line` is where the node is constructed; the role is what it decides for the UI.

```
Program.Main                                                      Program.cs:439-440
└─ Services.UseRealBackend = Array.IndexOf(args, "--fake") < 0    ← the ONE flag read; a static bool
   └─ Services.CreateFake(settings, locale)                       Services.cs:584   (the fake composition root)
      ├─ FakeSpotifySession                                       :586  → LoggedOut → Authenticating → (250 ms)
      │                                                                   → Authenticated (FakeSpotifySession.cs:18-23);
      │                                                                   user "Wavee Listener", IsPremium: true,
      │                                                                   Email listener@wavee.app, AvatarUrl NULL (:21)
      ├─ UnsupportedPlaybackPlayer                                 :589  → every play intent is REJECTED → a toast
      ├─ NoConnectDevices                                          :590  → the device roster is empty, forever
      ├─ AppDataSettings.ForUnpackaged("Wavee","Wavee")            :593  → the REAL settings store (local state)
      ├─ SpotifyExport.Load()                                      :595  → reads 4 json from assets/spotify
      │  ├─ LoadLibrary(playlists.json)                            SpotifyExport.cs:57-81
      │  │     24 libraryV3 items = 22 Playlist + 1 PseudoPlaylist(count 166) + 1 Folder(skipped, :70)
      │  │     …of the 22 Playlists, ONE is format "listen-later" ("Your Episodes") and is skipped too (:73)
      │  │     → _summaries[21], _headers[21], LikedCount = 166      ← TWENTY-ONE, not 22 (§0.10e)
      │  ├─ LoadIced(icedamericano.json)                           :83-104  → ONE playlist with REAL tracks
      │  ├─ LoadHome(home.json)                                    :107-126 → _cards[] + Home = Compose(home,
      │  │                                                           _summaries) at :123 — NO titles record, so the
      │  │                                                           synthetic module names are Default's English (§0.10g)
      │  │     31 sections / 107 items: 20 Baseline, 8 Generic, 1 Shorts, 1 Spotlight, 1 RecentlyPlayed;
      │  │     greeting "Good morning"; homeChips = 3 (Music / Podcasts / Audiobooks, each with a
      │  │     "Following" subChip) → HomeContribution.Chips is POPULATED offline (SpotifyHomeComposer.cs:126, :241)
      │  └─ LoadArtists(artist-*.json)                             :40-55   → _artists[1]:
      │                                                                        spotify:artist:04gDigrS5kc9YWfZHwBETP
      ├─ LocalMutationSource(savedSeed)                            :601-604 → seeded from the first
      │                                                                        min(166, 300) = 166 liked uris
      ├─ UserPlaylistSource                                        :607  → EMPTY at boot; session-only, not persisted
      ├─ SourceRegistry  [ORDER IS ROUTING]                        :613-622
      │   1. SpotifyExportSource(export)   Owns spotify:*             Catalog | Home | Search
      │   2. LocalSource                   Owns local: wavee:local:*  Catalog | Search | LocalDecode
      │   3. userPlaylists                 Owns wavee:playlist:*      Catalog | Mutations
      │   4. FakeSource                    Owns fake:* + bare ids     Catalog | FALLBACK   ← NOT spotify:*
      │   5. FakePodcastSource             Owns wavee:show/episode:*  Podcasts
      │   6. mutations                                                 Mutations
      │   7. session                                                   Session
      ├─ AggregateCatalog(registry)                                :623  → first OWNING source wins
      │                                                                   (AggregateCatalog.cs:36-41 and the same
      │                                                                   shape at :44-49, :52-58, :62-69, :80-86);
      │                                                                   the Fallback capability answers only a uri
      │                                                                   nobody owns (:88-90).
      │                                                                   COLLECTIONS are the other rule: the
      │                                                                   playlist TREE is CONCATENATED across every
      │                                                                   catalog source (:119-124) — §0.10f
      ├─ new Services(..., new NoLyricsProvider(), ..., new InMemoryActivityStore(), ...)   :624
      └─ svc.Hydrator = new HydrationRouter(registry)              :629

   … and, from the shared Services ctor, the THIRTEEN seats that are Null on BOTH backends until go-live
      (0.2.9 and ch 29 both say "twelve"; the list below is 13):
      NullUserTopService :320 · NullPlaylistPopcountService :321 · NullPreReleaseService :322 ·
      NullTrackCreditsService :323 · NullContentFilterService :324 · NullFriendActivityService :325 ·
      NullSpotifyNotificationsService :358 · NullWhatsNewService :359 · NullConcertService :360 ·
      SwitchableBrowseService(Null) :361 · SwitchableTrackExpansionService(Null) :362 ·
      SwitchableRecentsService(Null) :363 · SwitchableHomeSectionService(Null) :364
```

**The consequence the plan's gate depends on and nobody wrote down**: routing is by `Owns`, in registry order, and
`SpotifyExportSource.Owns` is `EntityUri.Parse(uri).Provider == EntityProviders.Spotify` (`SpotifyExportSource.cs:20`)
— i.e. **every** `spotify:*` uri. `FakeSource`'s single-item reads (`GetPlaylistAsync`, `GetAlbumAsync`,
`GetArtistAsync`, `:21-34`) are therefore **dead for every spotify uri**; `FakeData` reaches the screen only where
`SpotifyExportSource` *itself* calls it — `AlbumFor` (`:47`), `GetArtistAsync` (`:69`), `GetLikedSongsAsync` (`:140`),
`SearchAsync` (`:150`), `SynthPlaylistTracks` (`:164`), `SynthAlbumTracks` (`:175`). `FakeSource` contributes only its
two *collections* (13 albums `:65-67`, 12 artists `:69-73`), its stats (`:78`) and — the one nobody notices — its
**playlist TREE** (`FakeSource.cs:53-54`), which is concatenated after the export's 21 flattened leaves rather than
replacing them. This is why §2 W7's defect exists, and why §2 W13's sidebar is 26 top-level rows, not 5.

### 1.2 The same thing in 0.3 terms

```
App.Main                                                          plan §3.5
├─ Platform.Boot()            settings, log, update policy        (unchanged by --fake)
├─ Entities.Boot(Platform.Scope)
│   └─ if (Platform.Args.Fake) Entities.SeedFake(Platform.Clock.SeedEpoch)   ← ONE call, ONE place
│        writes STAGING columns + edges, commits through the ordinary path,
│        bumps every touched table's `Changed` signal exactly once
├─ Spotify.Boot()             if fake: Session.Phase := Authenticated, Me := the seeded user row
├─ Playback.Boot()            if fake: the host loop runs; the audio pump is the silent sink (§7.4, GAP 6)
├─ Modules.Boot()
└─ Shell.Run()
```

`SeedFake` has one input (a 64-bit epoch-seconds value) and one output (the current `Scope`'s tables and edges are
populated and published). It is CORE by plan G7: no engine types, no I/O except the bundled asset **paths** it
interns as `StringId`s, no `DateTime.Now`, no `Random`, no `string.GetHashCode`.

**How live data reaches each node under props-freeze.** Component props freeze at mount (CLAUDE.md), so the seed must
not be handed to a page as a prop — and it is not: `SeedFake` writes the same columns `Spotify.Decode` writes, every
page binds through its handle plus the table's one `Signal<uint> Changed` (plan §4.1, D8), and a page mounted before
the seed commits re-renders on the publication exactly as it would for a live decode. The three nodes that need
naming:

| node | 0.3 input | how it updates after mount |
|---|---|---|
| any page (`X.Page.cs`) | an `EntityUri` from the route | the table's `Changed` signal + a `Version[slot]` compare; the seed's commit is one publication |
| `SidebarMiniature` (ch 26 W11's confirmation dialog) | **no signal at all** — `Entities.SeedFake.Sample(kind, index)`, a pure static over the seed's own arrays | never: it is a static preview of a *template*, and reading the live tables here is the defect ch 26 §7.1 forbids |
| the Home skeleton (`HomePage.cs:90` today) | the blank-shaped seed document | it does not update; the shimmer is derived from it once and replaced by the Ready arm |

---

## 2. Wireframes

Scale is stated per frame. These exist so a reviewer can hold a `--fake` screenshot beside a drawing and see whether
the seed did its job; they are not new designs — every geometry constant belongs to the owning chapter, and each
frame names it.

### W1 — Home, seeded, top of page @ A ≈ 1 088 (1 char ≈ 8 DIP)

Source: `assets/spotify/home.json` through `SpotifyHomeComposer.Compose` (`SpotifyExport.cs:123`). Geometry: ch 10 W3.
**Order is composed, not documentary.** The first row is the SYNTHETIC `QuickGrid` (9 of the 21 library summaries,
`SpotifyHomeComposer.cs:40-46`, `QuickPicks = 9` at `:28`); the Spotlight hero is then *inserted at index 1*
(`:122-124`), and only then does the document's own order run. `home.json`'s own first section is a
`HomeShortsSectionData` (10 items of 64) that the frame below never drew.

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐  A ≈ 1088
│  ░░ hero wash (3 legs, from the first 3 modules' art) ░░                                      │
│  [ Music ] [ Podcasts ] [ Audiobooks ]   ← homeChips: 3, each with a "Following" subChip
│  Jump back in  (synthetic QuickGrid: 9 of the 21 library summaries)         Show all ›
│  [card][card][card][card][card][card][card][card][card]
│  ┌────────────────────────┐  Good morning, Wavee Listener                       [⋯]          │  greeting = the
│  │                        │  NEW RELEASE FROM KIMMUSEUM                                       │  server's own
│  │   384 × 384 cover      │  ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓                                │  transformedLabel
│  │   (i.scdn.co — NEEDS   │  ┃  <spotlight card title>       ┃  48/60/700, ≤ 2 lines          │  (home.json)
│  │    NETWORK; offline    │  ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛                                │
│  │    it is the neutral   │  ( ▶ Play )  ( ♡ )  ( ⋯ )                                         │  NO countdown row:
│  │    placeholder tile)   │                                                                   │  the hero is a
│  └────────────────────────┘                                                                   │  release, not a
│                                                                                               │  daylist (§7.3)
│  Made For Christos                                                                            │  9 of 9 ⇒ no
│  [card][card][card][card][card][card][card][card][card]                                       │  "Show all"
│  Recents                                                                                      │  the 1 RecentlyPlayed
│  [card][card][card][card][card][card]                                                         │  section; its wrapper
│  Jump back in   ← the SERVER's row: the SECOND with this title (§0.10g)     Show all ›        │  totalCount is 1, so
│  [tile][tile][tile][tile]                                                                     │  RecentsTotal reads the
│  Recommended Stations                                                       Show all ›        │  wrapped list (:97-102)
│  Soundtrack your Saturday morning                                           Show all ›        │  9 of 14 · 9 of 20
│  Geniet van de zaterdag ☕  ← a DUTCH, emoji-bearing SERVER title: the        Show all ›        │  9 of 10 · 10 of 13
│                              long-label / i18n fixture no chapter had cited                   │
│  Your top mixes                                                             Show all ›        │  9 of 13
│  More of what you like                                                      Show all ›        │  10 of 25
│  Popular radio                                                              Show all ›        │  10 of 50
│  … 20 single-card baseline sections, each 1 of 1 (indices 11-30) …                             │
│  ┌───────── Charts (Fold deck) ─────────┐                                                     │  ▒▒ FAILED ▒▒
│  │  ⚠  Couldn't load   [ Retry ]        │                                                     │  NullBrowseService
│  └──────────────────────────────────────┘                                                     │  + an AUTHENTICATED
└──────────────────────────────────────────────────────────────────────────────────────────────┘  fake session
```

Counts are ch 10 §10's, re-derived here from the file itself: 31 sections, 107 section items, greeting
`"Good morning"`, `homeChips` **3** (Music / Podcasts / Audiobooks, each carrying one "Following" subChip).

**31 sections are not 31 module rows.** `Compose` emits one synthetic QuickGrid, one Hero (the first Spotlight; a
second Spotlight would be an ordinary group, `:73-75`), one group per Baseline and per RecentlyPlayed section, and
for every Generic / Shorts section whatever `EmitSectionGroups` decides (`:164-199`): a single `Topic` when more
than half the cards carry an editorial format, otherwise **one group per card-kind bucket** plus a `SectionEntry`
when no bucket is dominant. The row count is therefore a function of the document, not equal to 31 — §10 item 5 no
longer asserts "31 module rows".

**The daylist card is already in the fixture.** `home.json` and `playlists.json` each carry exactly one playlist
with `"format": "daylist"` ("daylist", `spotify:playlist:37i9dQZF1EP6YuccBxUcC1`, cover
`daylist.spotifycdn.com/playlist-covers-mix/en/morning_*.jpg`), and `ModuleForFormat` maps
`"daylist" => HomeGroupKind.Hero` (`SpotifyHomeComposer.cs:230-232`). What is missing is not the card but
`ExpiresAt` / `CreatedAt` — the fields `DetailHeaderMergeRules.IsRollingIdentity(loaded.ExpiresAtMs,
preview?.ExpiresAtMs)` reads (`DetailPage.cs:124`) and ch 11's flip clock counts down against. §2 W14, §5 and
§7.1 item 7 are corrected to **◑ (card present, clock absent)**, not ✖.

### W2 — Home, cold skeleton, derived from `HomeSeed` @ A ≈ 1 088

The bones are the SAME tree rendered against blank cards (`HomePage.cs:90`, `FakeData.cs:587-610`). Fourteen groups
in this order, at these counts — these are the fixture, not decoration:

```
 group (FakeData.cs)            count   why the count is load-bearing
 ───────────────────────────────────────────────────────────────────────────────────────────────
 Hero          :591               1     HeroBlank() carries Format "daylist", TrackCount 50,
                                        Seeds ["focus","indie","chill"], OwnerName "Wavee" (:613-617)
                                        → the ONLY seed entry with a non-null Meta; it fixes the hero's
                                        minimum height (tags + meta + action lanes)
 WeeklyPair    :592               2     Formats "discover-weekly" / "release-radar" (:626-632)
 QuickGrid     :593               8     HomeCardKind.Playlist
 Recents       :594               8     HomeCardKind.Album
 MixBand       :595               6
 ChipCards     :596               6
 RadioDial     :597              12
 QueueList     :598               6     HomeCardKind.Episode
 RatedShelf    :599               6     HomeCardKind.Audiobook
 Featured      :600               4
 PodcastShelf  :601               6     Uri wavee:skeleton:section:podcasts, TotalCount 6
 Topic         :602               7     Uri …:topic,  TotalCount 20
 SectionEntry  :603               7     Uri …:mixed,  TotalCount 20
 DiscoverFeed  :604              12
 + Sections ledger (3 entries)  :605-610  topic / mixed / podcasts — the podcasts uri is consumed by
                                        PodcastShelf, leaving TWO Fold-shaped tiles (ch 10 audit item 3)
 Chips: ABSENT — the ctor stops at `Sections:` (:605), so the cold first row carries NO chip bones
```

### W3 — Album page, seeded, two-column @ page 1 040 (1 char ≈ 8 DIP)

Route `album:spotify:album:al2` → `SpotifyExportSource.AlbumFor` (`:43-57`) → no card for `al2` →
`FakeData.Album(2)` (`:103-110`). Geometry: ch 05 W1.

```
┌─────────────┬────────────────────────────────────────────────────────────────────────┐
│  rail 280   │  #  TITLE                        PLAYS       BPM·KEY        ⏱          │
│ ┌─────────┐ │  1  Dalkom Cafe              ★  67,750,000   175 · 9B       4:38        │  ← PlayCount
│ │ cover   │ │  2  Nostalgia 2000s Mix          37,750,000    93 · 10B      2:45        │    descending
│ │ 300×300 │ │  3  Strobe                       27,750,000   126 · 11B      3:22        │    (:96) ⇒ track 1
│ │ cover02 │ │  …                                                                      │    is the ★ hit
│ │ (LOCAL  │ │  12 Weird Fishes                 12,750,000   128 · 8B       3:55        │
│ │  jpg)   │ │                                                                          │
│ └─────────┘ │  ── About this release ────────  EMPTY: no Label/©/courtesy in the seed  │
│ ALBUM · 2016│  ── Other versions ───────────── ABSENT: the Album record has none        │
│ 우울해      │  ── More by Christos ─────────── ABSENT                                   │
│ Christos    │  ── About the artist ─────────── present (the enrichment service is a     │
│ 12 songs ·  │                                   catalog adapter — Services.cs:319)   │
│ 43 min      │                                                                           │
│ (▶)(♡)(⋯)   │                                                                           │
└─────────────┴────────────────────────────────────────────────────────────────────────┘
 year  = 2014 + (2 % 11) = 2016              (:109)      cover index = Wrap(2, 16) = 02   (:26)
 title = Seed[Wrap(2, 16)].Title = "우울해"   (:105)      tracks = AlbumTracks(12, i·10 = 20) (:108)

 EVERY ROW ABOVE IS EVALUATED AT OFFSET 20, NOT 0. The first pass of this chapter read the i = 0,1,2 column and
 printed it as al2's. The generators, for i = 20 … 31 and k = 0 … 11:
   title  Seed[Wrap(i,16)].Title  ⇒ 20→"Dalkom Cafe", 21→"Nostalgia 2000s Mix", 22→"Strobe", 31→"Weird Fishes"
          — the ALBUM is "우울해" and its first track is not; title and album name are independent (:41, :105).
          The track's own AlbumRef is al{i} / the track title (:41), so in a fake Liked/search list the ALBUM
          column always repeats the TITLE column. That is a fixture property, not a rendering bug.
   plays  60_000_000/(k+1) + ((20·131 % 37) + 1)·250_000 = 60_000_000/(k+1) + 7_750_000        (:96)
          ⇒ 67.75 M / 37.75 M / 27.75 M … 12.75 M. The +7.75 M floor is a function of the OFFSET, not of k, so
          no fake album's last row ever falls to a small number — ch 01's "thin play count" arm is unreachable.
   dur    138 + (i·37 % 150) s ⇒ 278 / 165 / 202 … 235 s;  Σ(i = 20..31) = 2 628 000 ms = 43 min 48 s
   bpm    (96 | 128 | 172 by i%3) + (i%7) − 3     key  slot = 1 + Wrap(i,12), ring = i%5 < 3 ? "B" : "A",
                                                        name = CamelotKeys[slot−1] (:56-57, :59, :65)
   artist kind Album ⇒ various = false ⇒ every row is ArtistRef(2) = "Christos"                  (:98, :108)
```

### W4 — The four `AlbumKind`s, by index (the seed's whole release-shape vocabulary)

`AlbumShape(i)` (`FakeData.cs:80-88`) is a 6-cycle on `i % 6`:

```
 i%6   kind          tracks   reachable at              what it proves on the page
 ──────────────────────────────────────────────────────────────────────────────────────────────
  0    Single         1       album:spotify:album:al0   eyebrow "SINGLE"; selection mode is None
                                                        (ch 05 W17); no disc grouping
  1    EP             5       …al1                      eyebrow "EP"
  2    Album         12       …al2                      the default frame (W3)
  3    Single         2       …al3                      a 2-track single — the "is it an EP" boundary
  4    Compilation   18       …al4                      VARIOUS ARTISTS: each track keeps its own
                                                        artist (:98) ⇒ the Album lane is populated
  5    Album         10       …al5
```

Because artist `i`'s six albums are `Album(i·6 + k)` for `k = 0..5` and `(i·6 + k) % 6 = k`, **every fake artist owns
exactly one album of each shape** — which is also why W6's facet split is 2 / 3 / 1.

### W5 — Artist page, seeded @ W = 1 160 (1 char ≈ 16 DIP)

Route `artist:spotify:artist:ar3` → `SpotifyExportSource.GetArtistAsync` (`:67-82`) → not in `_artists` → no card →
`FakeData.Artist(3)` (`:142-165`). Geometry: ch 08 W1.

```
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ ████ header photo = Cover(3, 640) = cover03.jpg (LOCAL) ██████████████████████████████████ │  hero 440
│   ✔ Christos                                                                                │
│   <N> monthly listeners · #<R> in the world              ← BOTH VALUES CHANGE PER LAUNCH    │  ⚠ §0.4
│   "Christos is an artist whose sound moves between …"    (:289-292)                         │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│  Popular                                     │  Latest release                              │  chart 712 / rail 356
│  1 ♪ <title>   <plays>                       │  [72] <newest-by-year album>   (:156-158)    │  TopTracksOf(a, 5)
│  2 ♪ …                                       │  Artist pick     (present iff h%3 != 1)      │  (:294-306): title-
│  3 4 5                                       │                                              │  deduped, play-count
├──────────────────────────────────────────────┴──────────────────────────────────────────────┤  descending, 5 rows
│  Discography   [ Albums | Singles | Compilations ]                    ← SEE W6              │
│  Appears on        ABSENT  (Artist.AppearsOn is null, :163)                                 │
│  Fans also like    the cached-artists pool fallback (Extras.Related is null, :193)           │
│  Music videos      present iff h%5 != 2   ·  Playlists   present iff h%2 == 0                │  ⚠ all six gates
│  Upcoming concerts present iff h%4 != 0   ·  Merch       present iff h%3 != 1                │  are per-launch
│  Top cities        present iff h%2 == 1   ·  Gallery     present iff h%3 != 2                │  random today
│  Tour banner       present iff concerts present  (TourBannerFor, :170-180)                   │  (:182-195)
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

Two fixture facts the frame above does not show. **`PopularReleases` is `albums.Take(4)`** (`:159`) — the first
four of the six, i.e. Single / EP / Album / Single, *not* the four most popular — so the "Popular releases" rail is
release-order, never play-order. And **`LatestRelease` is the newest-by-`Year`, ties broken by ordinal name**
(`:156-158`): for `ar3` that is `Album(21)` — year 2024, `AlbumShape(21) = Single(2)`, title "Nostalgia 2000s Mix"
— so the Latest-release banner on a fake artist is usually a **2-track single**, and ch 08's album-shaped banner
arm is never exercised offline.

The one exception: `artist:spotify:artist:04gDigrS5kc9YWfZHwBETP` resolves to the **real** exported `artistUnion`
(`SpotifyExport.cs:40-55`, `SpotifyExportMapper.MapArtist:1584-1650`) with real discography item lists and real
`totalCount`s, with monthly / followers / rank / videos / cities backfilled from the synthetic artist
(`SpotifyExportSource.cs:72-79`, `MergeExtras:85-94`). It is the seed's only magazine-grade artist page.

### W6 — Artist discography, as `--fake` ACTUALLY serves it (the defect ch 08 depends on)

```
 what ch 08 §7 requires            what --fake delivers today                     why
 ─────────────────────────────────────────────────────────────────────────────────────────────────────
 hundreds per facet, so the        Albums 2 · Singles 3 · Compilations 1          neither FakeSource nor
 grid genuinely virtualizes        (the artist's SIX in-memory albums,            SpotifyExportSource
                                   FakeData.cs:145-146, sliced by kind            overrides GetDiscographyAsync
                                   through AggregateCatalog.KindMatches)          → ICatalogSource.cs:51-60,
                                                                                  the overview-slice default
 FakeData.Discography(:112-128)    Albums       60 + (seed·7)%40   →  60.. 97     NEVER CALLED. Zero call
 would deliver                     Singles     380 + (seed·53)%320 → 380..698     sites in the whole tree.
                                   Compilations 110 + (seed·17)%120 → 110..229
                                   ↑ `seed` is Wrap(IndexFromUri, 16) (:116), so only SIXTEEN values are
                                   reachable and the modulus never attains its ceiling: the true ranges are
                                   60..97 and 380..698, not 60..99 / 380..699. Every facet total is one of 16
                                   fixed numbers per kind — which is exactly the determinism the 0.3 seed wants,
                                   and worth pinning as a literal in EntitiesFakeTests (§8 assertion 5).

 ┌── Albums (2) ───────────────────────┐   ┌── what it must look like (W6b) ──────────────────────────┐
 │ [card][card]                        │   │ Albums 87                                    Show all ›   │
 │  no era bands (a 2-item grouping)   │   │ [card][card][card][card][card][card]                      │
 │  no pager, no virtualization        │   │ 2024 ──────────────────────────────  ← era band           │
 └─────────────────────────────────────┘   │ [card][card][card]…  (scroll → page 2 of 60)              │
                                           └───────────────────────────────────────────────────────────┘
 Route disco:0:spotify:artist:ar3 renders the same 2/3/1 — so ch 08 W22's discography PAGE is
 structurally present and substantively empty. The ARTIST PAGE does not even route through
 GetDiscographyAsync: ArtistPage slices `a.TopAlbums` itself (`ArtistPage.cs:202-203`, the same
 `Kind == Album` / `Kind is Single or EP` predicate KindMatches uses), so the 2/3/1 split appears twice,
 from two independent code paths. 0.3 must not reproduce that: ch 08 gap 8's three per-facet edges are the
 one place the split lives.
```

### W7 — Playlist opened from the sidebar: the header the seed promises vs the header it serves

```
 the sidebar row (SidebarProjection over FakeData.PlaylistTree, :501-517)
 ┌────────────────────────────────────────┐
 │ [cover08] Dalkom Cafe                  │   name/owner/count/cover from PlaylistSummary(4)  (:492-496)
 │           50 songs                     │   uri spotify:playlist:pl4 · cover = Cover(4+100, 300)
 │                                        │   ⇒ Wrap(104,16) = 8 ⇒ cover08.jpg, NOT cover04 (:495, :26)
 └────────────────────────────────────────┘
                    │  click → route pl:spotify:playlist:pl4
                    ▼
 AggregateCatalog.GetPlaylistAsync → first OWNING source = SpotifyExportSource (Services.cs:615)
   _x.TryGetFullPlaylist("…pl4") → miss          (the export's uris are real base62)
   _x.TryGetHeader("…pl4")       → miss
   _x.TryGetCard("…pl4")         → miss
   ⇒ SpotifyExportSource.cs:33   new Playlist(id, uri, "Playlist", null, "Spotify", null, SynthCount(uri), …)

 ┌─────────────┬──────────────────────────────────────────────────────────────────────┐
 │  rail 280   │  #  TITLE                                    ADDED       ⏱           │
 │ ┌─────────┐ │  1  …                                        —           …           │  ← SynthPlaylistTracks
 │ │ ▓▓▓▓▓▓▓ │ │  …                                                                    │    (:164) =
 │ │  NO     │ │  n  …                                        —           …           │    Tracks(n, Hash(uri)%800)
 │ │ COVER   │ │                                                                       │    n = 12 + Hash(uri)%40
 │ └─────────┘ │                                                                       │
 │ Playlist    │   ← the sidebar said "Dalkom Cafe"                                     │  DetailHeaderMergeRules
 │ Playlist    │   ← the generic title wins, because a NON-ROLLING container prefers    │  .ResolveTitle
 │ Spotify     │      the LOADED title (DetailHeaderMergeRules.cs:30-31)                │  (rolling = false)
 │ n songs     │   ← neither 50 nor the sidebar's badge                                 │
 └─────────────┴──────────────────────────────────────────────────────────────────────┘
 FakeData.Playlist(4) — with its name, its Spotify/user curated split, its AddedAt stamps and its
 collaborative people (:308-336) — is NEVER SERVED on this path.
```

The **21 exported** playlists (`playlists.json`, §0.10e) do not have this problem: `TryGetHeader` hits, and the Iced
Americano uri (`icedamericano.json`) serves REAL tracks through `TryGetFullPlaylist` (`SpotifyExportSource.cs:28`).
So `--fake` has exactly one fully-real playlist and **20** real headers with synthesized bodies.

**And the sidebar shows "Dalkom Cafe" TWICE.** The export's own library contains a *different* "Dalkom Cafe"
(`spotify:playlist:37i9dQZF1DX5g856aiKiDS`, owner **Spotify**, format `editorial`, a real `i.scdn.co` cover), and
so it does for "Iced Americano", "우울해", "My Playlist #6", "Nostalgia 2000s Mix" and "Henry Moodie Mix" — the
synthetic `PlaylistSeed` names were copied from it (§0.10f). One of the two rows opens a correct magazine header,
the other opens the generic arm drawn above. A reviewer holding a screenshot beside this frame must check the URI,
not the name.

### W8 — Liked Songs, seeded @ page ≥ 820, mode 0 (1 char ≈ 8 DIP)

```
┌─────────────┬──────────────────────────────────────────────────────────────────────┐
│  rail 240   │  #  TITLE                    ALBUM          DATE ADDED     ⏱         │
│ ┌─────────┐ │  1  Breathe                  Breathe            —          3:58      │  ← 166 rows
│ │ LENS    │ │  2  Lebanese Blonde          Lebanese Blonde    —          4:35      │    (the export's
│ │ cover   │ │  …                                                                    │    LikedCount)
│ │ 304     │ │ 166 …                                                                 │
│ │ canvas  │ │                                                                        │  ⚠ NO "date added":
│ │ over the│ │                                                                        │    LikedSongs() =
│ │ first N │ │                                                                        │    Tracks(166, 1000)
│ │ covers  │ │                                                                        │    (:339), and Track()
│ └─────────┘ │                                                                        │    sets no AddedAt
│ Liked Songs │                                                                        │    (:58-60)
│ 166 songs · │  chips: ABSENT (NullContentFilterService, Services.cs:324) — the        │
│ <duration>  │         DERIVED fallback works: every track carries Tags[0] (:60)       │
│ facts panel │  years / tempo / blend cards POPULATE (Year :60, TempoBpm :51,          │
│             │  CamelotColor :59 on every row)                                        │
└─────────────┴──────────────────────────────────────────────────────────────────────┘
 distinct cover tiles = 16 (CoverCount, :12) — enough for every LikedCoverRules.MinTiles style
 every row reads SAVED: LocalMutationSource is seeded with the first 166 liked uris (Services.cs:601-603) — but
   ONLY on a first run: with a non-empty SavedLibrary key the persisted set is loaded instead (:600-603, §0.14)
 rows are LikedSongs(166) = Tracks(166, 1000) ⇒ track indices 1000…1165, so row 1 is Seed[1000 % 16 = 8] =
   "Breathe" / Télépopmusik, row 2 Seed[9] = "Lebanese Blonde", row 3 Seed[10] = "Undo" (:339, :40, :14-22).
   The first pass of this chapter printed Seed[0] / Seed[1] here, which is the OFFSET-0 list, not Liked's.
 ALBUM column == TITLE column on every row: Track(i)'s AlbumRef takes the track's own title (:41)
```

### W9 — Search "a" @ pane 960 (1 char ≈ 8 DIP)

```
 AggregateCatalog.SearchAsync concatenates SpotifyExportSource (:142-155) + LocalSource (:71-79)
 ┌──────────────────────────────────────────────────────────────────────────────────────┐
 │ [All]  ← the STATIC fallback superset filtered by HasAny; no server ChipOrder          │
 │                                                                                        │
 │ Top result                    Songs                                                    │
 │ ┌───────────────┐             1 ♪ <Seed title>   …                                     │  8 tracks,
 │ │  <first hit>  │             …                                                        │  offset q.Length·3
 │ └───────────────┘             8 ♪ …                                                    │  (:536)
 │                                                                                        │
 │ Artists   [○][○][○]                 2 artists (:538)    ← + name-matched EXPORT         │
 │ Albums    [▢][▢][▢]                 3 albums  (:537)      artists prepended (:152-153)  │
 │ Playlists [▢][▢]                    2 fake (:539) OR the name-matched export rows (:146)│
 │ Local     <matching local titles>   LocalSource.SearchAsync                             │
 │                                                                                        │
 │ Shows · Episodes · Audiobooks · Profiles · Genres · Related searches  ── ALL ABSENT ──  │
 └──────────────────────────────────────────────────────────────────────────────────────┘
 Results are a function of query LENGTH, not content (:536-539): "a", "b" and "z" return the SAME
 tracks/albums/artists. Only the export name-match arm reacts to the actual characters — and for the letter
 "a" that arm is not a corner case: it matches the ONE exported artist ("Maroon 5", so Artists renders 1 real +
 2 synthetic = 3, :152-153) and most of the 21 exported playlist names, so `matches.Count > 0` and the
 Playlists shelf shows EXPORT rows, never Playlist(1)/Playlist(2) (:146, :154).
```

### W10 — Library ▸ Albums / Artists / Podcasts @ content 1 140

```
 Albums    13 rows   FakeSource.GetAlbumsAsync  → FakeData.Album(20..32)      FakeSource.cs:65-67
 Artists   12 rows   FakeSource.GetArtistsAsync → FakeData.Artist(30..41)     FakeSource.cs:69-73
 Podcasts   8 rows   FakePodcastSource.GetShowsAsync → FakeData.Shows()       FakePodcastSource.cs:13
                     ⚠ the sidebar badge says 7 (LibraryStats().Podcasts, FakeData.cs:477)
 Liked    166 songs  stats summed by the aggregate: FakeSource (13,12,0,7) + SpotifyExportSource
                     (0,0,166,0)                                             FakeSource.cs:78,
                                                                             SpotifyExportSource.cs:160
 Selection → the detail pane renders the same album/artist pages as W3/W5, so the three-column arm is
 fully exercised. Full-text library search: UNVERIFIED on the fake backend (no StoreLibrarySource).
```

### W11 — Local Files (route `local`) @ content 1 140

```
 14 rows, titles from LocalSeed (:344-351): "Sunset Boulevard", "Paper Planes", … "Morning Pages"
 uri wavee:local:track:{i} · duration 150 000 + (i·41 % 140)·1000 ms         (:363)
 Availability.Playable stated explicitly (:368) so a playable-only filter does not hide the page (:364-365)
 header "Local Files" / "Music imported from this computer." / "On this device", NO cover  (LocalSource.cs:27-31)
 capabilities CanView, CanEditItems, CanEditMetadata, IsOwner, Known: true                  (LocalSource.cs:30)
```

### W12 — Show page (route `show:wavee:show:3`) @ page 1 040

```
 FakePodcastSource.GetShowAsync (:17-20) → FakeData.Show(uri) (:397-402), idx = Wrap(IndexFromUri, 8)
 ┌─────────────┬──────────────────────────────────────────────────────────────────────┐
 │ cover 300   │  #11 · Tape Loops            <date>             43 min                │
 │ Podcast     │  #10 · The Quiet Release     <date>  ▓▓▓▓░░░░   56 min  ← IN PROGRESS │  i == 1 ⇒ prog = dur/3
 │ Coffee &    │  #9  · Edge Cases            <date>             69 min                │  (:414)
 │   Code      │  …                                                                     │
 │ Brewed Bytes│                                                                        │
 │ (▶)(♡)(⋯)   │  (no "Load more" pill — TotalEpisodes == PagedThrough == n,             │
 │             │   FakePodcastSource.cs:19)                                              │
 └─────────────┴──────────────────────────────────────────────────────────────────────┘
 n = 8 + (idx % 5) ⇒ 8..12 episodes  (:408)     duration = (22 + (idx·7 + i·13) % 50) min  (:413)
 for idx = 3: n = 11, and the three durations above are 22+21 = 43, 22+34 = 56, 22+47 = 69 min — the first pass
 printed 44 / 38 / 51, which no (idx, i) pair produces. Episode title = "#{n−i} · EpTitles[(idx·5 + i) % 12]"
 (:415), so show 3 opens on #11 Tape Loops / #10 The Quiet Release / #9 Edge Cases (verified against :380-384).
 cover = Cover(800 + idx, 300) ⇒ Wrap(803, 16) = 3 ⇒ cover03.jpg on the show AND every episode (:392, :401, :417)
```

### W13 — Sidebar tree and the miniature's hand-indexed fixtures

**The real sidebar is 26 top-level rows, not 5.** `AggregateCatalog.GetPlaylistTreeAsync` CONCATENATES every
catalog source's tree in registry order (`AggregateCatalog.cs:119-124`); `SpotifyExportSource` does not override it,
so the interface default flattens its 21 summaries into 21 leaves (`ICatalogSource.cs:88-89`) — real names, real
`i.scdn.co` covers, `SynthCount` counts, **no folders** (the export's one `Folder`, "New Folder", is dropped at
`SpotifyExport.cs:70`). `FakeSource.GetPlaylistTreeAsync` (`FakeSource.cs:53-54`) then appends the 5 synthetic
nodes below. Six names therefore appear twice (§0.10f). The frame below is the SYNTHETIC TAIL only.

```
 THE SYNTHETIC TAIL (FakeData.PlaylistTree, :501-517)  THE MINIATURE (SidebarMiniature.cs)
 ├─ mellow pop wistful saturday…  Spotify  50         Pinned band      → Playlist(1) + Artist(3)   :134-135
 ├─ My Playlist #6                Christos  4         PlaylistTree     → folder + Playlist(5)      :156
 ├─ ▾ Cafe & chill            (folder id "cafe")      Grid strip       → Playlist(2), Playlist(7)  :128
 │   ├─ 우울해                    Christos 22         other sections   → Playlist(index + 6)       :162
 │   ├─ ▾ Late night     (folder id "latenight")      Workspace hero   → Playlist(8)               :292
 │   │   └─ Dalkom Cafe           Christos 50         Workspace cards  → Playlist(10), (12), (14)  :328
 │   └─ Iced Americano            Christos 15         Shortcut badges  → LibraryStats()            :263-266
 ├─ Nostalgia 2000s Mix           Spotify  30                          = (13, 12, 161, 7)          :477
 └─ Henry Moodie Mix              Spotify  50
   ↑ A FOLDER INSIDE A FOLDER — the recursion the tree exists to exercise (:498-500)
   … and the ONLY folders in the whole --fake sidebar: the export contributes none.
```

The miniature's badge reads **161** liked while the live sidebar badge reads **166** (W10). Both are "correct" — the
miniature previews a *template*, not this account — but 0.3 must keep them separate deliberately, not by accident:
§7.2 rule D.

The nine hand-indexed call sites, re-read at HEAD: grid strip `PreviewGridCell(2)` / `(7)` (`:128`, the seed read
at `:272`) · Pinned band `Playlist(1)` + `Artist(3)` (`:134-135`) · PlaylistTree `Playlist(5)` (`:156`) · every
other section `Playlist(index + 6)` (`:162`) · Workspace hero `Playlist(8)` (`:292`) · workspace cards
`PreviewWorkspaceCard(10)` / `(12)` / `(14)` (the call sites are at `:299`; the seed read at `:328`) · shortcut
badges `LibraryStats()` (`:263-266`).

### W14 — The Wave 5 gate's real shape: every route, its `--fake` state, and what the seed must add

Supersedes ch 29 §2 W17. `████` renders its LOADED state · `▓▓░░` partial · `▒▒▒▒` a real failure state ·
`░░░░` an empty state · `────` the surface does not mount.

```
 ROUTE                      0.2.9 --fake   source / reason                            0.3 seed must add
 ───────────────────────────────────────────────────────────────────────────────────────────────────────────────
 home                       ████           assets/spotify/home.json (31 sections,     ExpiresAt/CreatedAt on the
                                           3 homeChips, 1 daylist-format card)       daylist card that already
                                                                                      exists (§2 W1); ONE title
                                                                                      for "Jump back in" (§0.10g)
 home-customize             ████           the live home-layout document              —
 home-section:<uri>         ░░░░           NullHomeSectionService (Services.cs:364)   a pageable section + cursor
 browse                     ░░░░           NullBrowseService (:361)                   a directory, 5 bands
 browse:<uri>               ░░░░           NullBrowseService                          1 category page per
                                                                                      BrowsePageLayout mode (4)
 browse-section:<uri>       ░░░░           NullHomeSectionService                     as home-section
 search                     ▓▓░░           SpotifyExportSource.SearchAsync :142-155:  shows, episodes, audiobooks,
                                           tracks + albums + artists + playlists ONLY profiles, genres, chipOrder,
                                                                                      a top hit, suggestions
 albums / artists /
   podcasts                 ████           FakeSource 13/12 + FakePodcastSource 8     fix the 7-vs-8 stat (§0.10c)
 liked                      ████           166 rows via the export's PseudoPlaylist   AddedAt per row
   └ content-filter chips   ░░░░           NullContentFilterService (:324)            a curated chip set
 local                      ████           LocalSource + LocalTracks() (14)           —
 album:<uri>                ████           FakeData.Album(i), 4 kinds via AlbumShape   release facts; other
                                                                                      versions; a PRERELEASE
 prerelease:<uri>           ░░░░           no prerelease anywhere in the seed         one upcoming album + its
                                                                                      countdown
 pl:<uri>                   ▓▓░░           21 export headers + Iced (real tracks);    make FakeData.Playlist(i)
                                           spotify:playlist:pl{i} → the GENERIC arm   reachable for pl{i} (§0.10a)
 artist:<uri>               ████           FakeData.Artist(i) (+ 1 real export)       determinism (§0.4)
 disco:<n>:<uri>            ▓▓░░           2/3/1 albums, not hundreds (§0.10b)        wire Discography() in
 show:<uri>                 ████           8 shows × 8-12 episodes                    one show with a load-more
 module:<uri>               ────           Services.Modules is NULL on the fake       a module with a page + a
                                           backend                                    watch page (ch 09, ch 24)
 concerts / concert:<uri> /
   artist-concerts:<uri>    ░░░░           NullConcertService (:360)                  a hub feed, an artist
                                                                                      schedule, a detail + offers
 recents                    ░░░░           NullRecentsService (:363) → Empty          a grouped snapshot with
                                                                                      members + instants
 history                    ████           the shell's own log — navigate 10-12 first —
 whatsnew                   ████           the bundled release-notes document         —
 settings                   ████           the real settings store                    —
 sidebar-customize          ████           the live preference document + the         keep the fixtures index-
                                           miniature                                   addressable (§7.2)
 api-console (DELETED, §9.6 Q7, 2026-09-12) — struck, no 0.3 route or seed needed
 playback-diagnostics       ████           honest "no playback session" arms          —
 connect-diagnostics        ████           renderable, but ShellRoutes.IsKnown omits  — (ch 18's defect, not the
                                           it (ch 18 §7)                               seed's)
 (unknown route)            ████           the not-found page                         —
 ───── SURFACES, not routes ────────────────────────────────────────────────────────────────────────────────────
 player bar                 ░░░░ RESTING   UnsupportedPlaybackPlayer (:589)           a PLAYING state (§7.4 note)
 right rail / NPV / deck    ░░░░ RESTING   nothing plays ⇒ no art, no clock, no motion ditto
 queue panel                ░░░░ EMPTY     DefaultQueue() exists (:569-576), NO CALLER wire it
 lyrics (rail + immersive)  ──── ABSENT    NoLyricsProvider (:624). Lyrics() builds a
                                           40-line WORD-SYNCED doc (:543-567), NO CALLER wire it
 video (4 surfaces)         ──── ABSENT    no modules, no overrides, no source        a module video + an override
 friends panel              ░░░░           NullFriendActivityService (:325)           a friends feed
 notifications centre       ░░░░           NullSpotifyNotificationsService (:358)     ≥ 4 rows, ≥ 2 unread
 device picker              ░░░░           NoConnectDevices (:590)                    a static roster (§7.4 D)
 track credits              ░░░░           NullTrackCreditsService (:323)             a credit list
 top tracks / popcount /
   pre-save rows            ░░░░           NullUserTopService / NullPlaylistPopcount /
                                           NullPreRelease (:320-322)                  ranked lists + a save count
 OS surfaces (all four)     ░░░░ IDLE      nothing plays ⇒ Clear* everywhere          follows from "something plays"
 ───────────────────────────────────────────────────────────────────────────────────────────────────────────────
 SCORE, 0.2.9. The ROUTE half of this table is 27 rows covering 31 route keys (the albums/artists/podcasts
 row is three routes; the concerts row is three). Sixteen rows render LOADED · 3 PARTIAL · 7 EMPTY · 1 ABSENT —
 and inside the LOADED `home` row the Charts deck is a real FAILED state (the fail-loud arm, ch 10 W12).
 The SURFACE half is 11 rows, and ZERO of them render a loaded state — two (the queue's and the lyrics')
 have a fixture already written and no caller.
 "--fake opens every route" is satisfied by all 31 route keys. That is the whole problem.
```

### W15 — The two determinism failures, drawn (what changes between two launches, today)

```
 LAUNCH A                                     LAUNCH B (same binary, same day)
 ┌──────────────────────────────┐             ┌──────────────────────────────┐
 │ ✔ Christos                   │             │   Christos                   │  verified = h%5 != 0
 │ 14,930,000 monthly listeners │             │ 3,610,000 monthly listeners  │  h = Math.Abs(
 │ #318 in the world            │             │ (no world rank)              │    "Christos".GetHashCode())
 │                              │             │                              │  FakeData.cs:147
 │ Upcoming concerts  [7 dates] │             │ (section absent)             │  h%4 != 0
 │ Merch              [5 items] │             │ Merch            [3 items]   │  h%3 != 1
 │ Top cities         [5]       │             │ (section absent)             │  h%2 == 1
 │ Gallery            [8]       │             │ Gallery          [4]         │  h%3 != 2
 └──────────────────────────────┘             └──────────────────────────────┘
 .NET Core randomises string.GetHashCode() per PROCESS. Ch 29's parity item 89 ("relaunch twice and diff a
 screenshot of Home, an album and the sidebar — pixel-identical") passes only because it does not name an
 artist page. It cannot pass for one. (The displayed numbers above are illustrative of the FORM; the exact
 values are unknowable by construction, which is the defect.)

 LAUNCH on 2026-09-12                          the SAME launch on 2026-09-13
 Liked / a playlist "Added"  Sep 12, Sep 10…    Sep 13, Sep 11…      now.AddDays(-(t·2 + i%5))  :331
 Show episode dates          Sep 12, Sep 5…     Sep 13, Sep 6…       now.AddDays(-(i·7+idx))    :417
 Artist concert dates        Sep 16 … Dec 4     Sep 17 … Dec 5       now.AddDays(4+k·9+h%5)     :213
 Tour banner eyebrow         "ON TOUR NOW"      "UPCOMING TOUR"      next.Date within 7 days    :176
```

### W16 — The states that need a clock, and what the seed must pin them to

```
 state                        owner ch   input the UI reads           the seed's fixed offset from now0
 ──────────────────────────────────────────────────────────────────────────────────────────────────────────
 daylist hero countdown       10, 11     Playlist.ExpiresAt           now0 + 4 h 37 m  → a 4-hour flip clock
                                         Playlist.CreatedAt           now0 − 3 h 23 m    with all four digit
                                                                                         pairs non-zero.
                                                                                         The CARD exists already
                                                                                         (home.json format
                                                                                         "daylist"); only these two
                                                                                         fields are missing.
 prerelease album countdown   05, 02     Album.ReleaseAt              now0 + 9 d 4 h   → the days+hours arm
                                         AlbumFlags.PreRelease
 a second prerelease (hours)  02 W24     Album.ReleaseAt              now0 + 5 h 12 m  → the hours+minutes arm
 not-yet-out TRACK rows       01 W6      Track.AvailableAt            now0 + 9 d 4 h     (the waterfall album's
                                                                                         rows 4..12)
 chart playlist caption       06 W26     ChartUpdatedAt               now0 − 2 h       → "Updated 2 hours ago"
                                         ChartNewEntries = 7
 chart row deltas             01 W5      PlaylistTrackEdge            fixed per index: NEW at 1, ▲ at 2-4,
                                         .ChartStatus/Pos/Prev        = at 5-8, ▼ at 9-12 (a full vocabulary)
 liked "added" spread         07         LibraryEdge.AddedAt          now0 − (i·2 d + (i%5) d), i over 166
                                                                      rows → spans ~332 days, so the week
                                                                      spark, the "since", the rediscover and
                                                                      the save-decade cards all have evidence
 show episode dates           09         Episode.PublishedAt          now0 − (i·7 d + showIdx d)
 one episode in progress      09 W7      Episode.ResumeMs             duration / 3, on episode index 1
 concert dates                17         Concert.Date                 now0 + (4 + k·9 + idx%5) d, k = 0..n-1
 tour banner "ON TOUR NOW"    08 W20     derived (TourBannerFor)      a SECOND artist whose first concert is
                                                                      now0 + 2 d, so both eyebrow arms exist
 recents day buckets          16 W3      RecentsEdge.PlayedAtMs       now0 − {40 m, 5 h, 26 h, 3 d, 9 d, 40 d}
                                                                      → Today / Yesterday / This week /
                                                                        This month / Earlier, all five
 history timestamps           16 W17     the shell's own log          self-seeding; nothing to fake
 ──────────────────────────────────────────────────────────────────────────────────────────────────────────
 now0 = SeedFake's ONE argument. Every value above is now0 ± a constant. Nothing calls the clock twice.
```

### W17 — The seed's own shape in 0.3 (columns and edges, not records)

```
 Entities.SeedFake(long now0)                                   Entities/Entities.Fake.cs   CORE
 │
 ├─ 1. INTERN the fixed strings once     Strings.Intern(utf8)   16 titles + 16 artists + 14 local titles +
 │                                                              8 shows + 12 episode titles + 10 venues +
 │                                                              6 descriptors + 12 camelot keys + the
 │                                                              17 asset paths              ≈ 130 StringIds
 ├─ 2. ALLOC rows, kind by kind, in slot order
 │      Tracks ~1 400 · Albums ~120 · Artists ~40 · Playlists ~40 · Shows 8 · Episodes ~80 ·
 │      Users 4 (me + Christos + Alex + Mia) · Concerts ~40
 │      → every slot index is a pure function of the fixture index, so Sample(kind, i) is O(1) (§7.2 rule C)
 ├─ 3. FILL columns with straight loops (P15-friendly): Title, Image, Duration, Year, Tempo, Camelot,
 │      CamelotColor, PlayCount, Flags(Explicit|Unavailable|HasVideo), AvailableAt, …
 ├─ 4. BUILD edges as whole CSR runs (never one Add per row):
 │      AlbumTracks · PlaylistTracks (+ AddedAt/AddedBy/Chart payload) · Liked (+ AddedAt) ·
 │      TrackArtists · AlbumArtists · ArtistAlbums/Singles/Compilations · ArtistPopular ·
 │      ShowEpisodes · Rootlist (+ Position/Depth/Kind/FolderName) · SavedAlbums · FollowedArtists ·
 │      SavedShows · Pins · HomeSection + SectionCards · SearchResult · Queue · Recents ·
 │      ArtistConcerts · ConcertOffers · ConcertLineup · PlaylistCollaborators
 ├─ 5. SET the Known bitmask per row to the field groups the fixture actually filled
 │      (a seeded row that claims Knows(Publishing) with no label is the same lie as a live one)
 ├─ 6. SET Authority := Authority.Seed (a NEW rung below Wire) so a later live decode wins cleanly if the
 │      two ever coexist — and so the diagnostics row inspector can say where a value came from
 └─ 7. ONE publication: bump every touched table's `Changed` signal exactly once, at the end.
        (Seven bumps during the seed = seven layout passes before the first frame.)
```

---

## 3. Tokens

**This chapter introduces no colour, type, spacing or radius token.** It owns *fixture constants*, and every one of
them is a number a parity item can measure. The prescribed table is filled for the one thing the seed does own
geometrically — the bundled cover assets and the sizes each surface asks for:

| element | size | padding/gap | radius | type style | colour token | material/elevation | file:line |
|---|---|---|---|---|---|---|---|
| `cover00..15.jpg` (the whole art vocabulary) | 16 files; 321 844 B incl. `liked-songs-300.png`. Source pixel resolution **600 × 600 JPEG, all sixteen** (read from the SOF headers; `liked-songs-300.png` is 300 × 300). So the artist header's `Cover(i, 640)` request (`:162`) asks for **more pixels than the file has** — every fake artist hero is a 6.7 % upscale, and ch 08's hero sharpness cannot be judged from a `--fake` screenshot | n/a | n/a | n/a | n/a | decoded by the engine `ImageCache`; the palette plane grades them | `FakeData.cs:12` (`CoverCount = 16`), `:24-29` |
| track thumb request | `Cover(i, 64)` → 64 × 64 | owner: ch 01 | owner: ch 01 | n/a | n/a | n/a | `FakeData.cs:58` |
| album / artist / show / playlist cover request | `Cover(i, 300)` → 300 × 300 | owner: ch 03 | owner: ch 03 | n/a | n/a | n/a | `:109`, `:160`, `:392`, `:495` |
| artist header photo request | `Cover(i, 640)` → 640 × 640 | owner: ch 08 | n/a | n/a | n/a | n/a | `:162` |
| artist gallery image request | `Cover(i + k + 3, 480)` | owner: ch 08 W21 | n/a | n/a | n/a | n/a | `:277` |
| merch image request | `Cover(i + k + 5, 300)` | owner: ch 08 | n/a | n/a | n/a | n/a | `:225` |
| liked stock cover | `assets/covers/liked-songs-300.png` | owner: ch 07 W13 | n/a | n/a | n/a | `ImageTransition.None` (ch 07 §7 G8) | ch 07 §7 G8 |
| Home card art | remote `https://i.scdn.co/image/<id>` — 166 urls | owner: ch 11 | owner: ch 11 | n/a | n/a | **network-dependent** | `assets/spotify/home.json` |

### 3.1 Fixture constants (the table §10 measures against)

| constant | value | formula | file:line |
|---|---|---|---|
| `CoverCount` | 16 | `n = ((i % 16) + 16) % 16` | `FakeData.cs:12`, `:13`, `:26` |
| `Seed` (title, artist) pairs | 16 | index = `Wrap(i, 16)` | `:14-22` |
| track duration | 138 000 … 287 000 ms (2:18 … 4:47) | `138_000 + (i·37 % 150)·1000` | `:42` |
| explicit every | 6th track | `i % 6 == 0` | `:58` |
| tempo | three humps: 93-99 / 125-131 / 169-175 bpm | `(96 \| 128 \| 172 by i%3) + (i%7) − 3` | `:50-51` |
| Camelot slot | 1 … 12; ring `"B"` when `i%5 < 3` else `"A"` | `1 + Wrap(i, 12)` | `:56-57` |
| Camelot colours | 12 opaque ARGB | table | `:59`, `:66-70` |
| descriptors (`Tags[0]`) | 6 | `Descriptors[Wrap(i, 6)]` | `:60`, `:63` |
| track year | 2008 … 2024 (17 values) | `2008 + i % 17` | `:60` |
| album year | 2014 … 2024 (11 values) | `2014 + i % 11` | `:109` |
| album track offset | `i · 10` | — | `:108` |
| album play counts | descending from ~60 M | `60_000_000/(i+1) + ((offset·131 % 37)+1)·250_000` | `:96` |
| albums per artist | 6, one of each `AlbumShape` | `Album(i·6 + k)`, k = 0..5 | `:145-146` |
| monthly listeners | 850 000 … **29 990 000** (31·940 000 + 850 000; the first pass said 30 010 000) | `850_000 + (h%32)·940_000` — **h is per-process random** | `:148` |
| followers | — | `monthly/2 + (h%11)·130_000` | `:149` |
| world rank | 0 (none) or 1 … 500 | `h%7==0 ? 0 : 1 + h%500` | `:151` |
| top tracks shown | 5 | title-deduped, play-count descending | `:296`, `:304` |
| concerts per artist | 3 … 10 | `3 + h%8`; festival at `k == 2` | `:207`, `:213` |
| merch items | 3 … 6; prices `$19 + k·5` | `3 + h%4` | `:221`, `:224` |
| gallery images | 4 … 8 | `4 + h%5` | `:275` |
| top cities | 5 | `monthly/30/(k+1)` listeners | `:255-257` |
| external links | 3 (Instagram, Twitter, Wikipedia) | slug = letters+digits, lowercased | `:261-271` |
| discography totals (**unreached**) | Albums 60-**97** · Singles 380-**698** · Comps 110-229 (`seed` is one of 16 values, `:116`) | `:119-121` | `:112-128` |
| `LikedSongs` default | 5 000 | `Tracks(count, offset 1000)` | `:339` |
| liked rows actually rendered | **166** | `max(1, export.LikedCount)` | `SpotifyExportSource.cs:140`; `playlists.json` PseudoPlaylist `count` |
| liked rows in the play CONTEXT | **161** | `LikedSongs(161)` | `FakeData.cs:466` |
| `LibraryStats` | Albums 13 · Artists 12 · Liked 161 · Podcasts 7 | literal | `:477` |
| `PopularReleases` on an artist | 4 — `albums.Take(4)`, i.e. release order, NOT play order | — | `:159` |
| `LatestRelease` on an artist | newest by `Year`, ordinal-name tiebreak — for `ar3` a **2-track Single** from 2024 | — | `:156-158` |
| update simulator cadence | Checking 600 ms → Available 1 500 ms → Downloading 0-100 by `ProgressStep` → Installing 900 ms → Completed | — | `FakeAppUpdateService.cs:30-33`, `:70-115` |
| saved-albums collection | 13 (`Album(20..32)`) | — | `FakeSource.cs:65-67` |
| followed-artists collection | 12 (`Artist(30..41)`) | — | `FakeSource.cs:69-73` |
| `Library()` list (no caller in any page) | 1 + 8 + 6 + 4 = 19 items | — | `:523-531` |
| local tracks | 14 | `LocalSeed.Length` | `:344-351` |
| local duration | 150 000 … **280 000** ms (i runs 0–13, so `i·41 % 140` attains 130, never 139) | `150_000 + (i·41 % 140)·1000` | `:363` |
| shows | 8 | `ShowSeed.Length` | `:374-379` |
| episodes per show | 8 … 12 | `8 + showIdx % 5` | `:408` |
| episode duration | 22 … 71 min | `(22 + (showIdx·7 + i·13) % 50)·60_000` | `:413` |
| episode in progress | exactly 1, at ⅓ | `i == 1 → dur/3` | `:414` |
| named playlist seeds | 7; counts 50, 4, 22, 15, 50, 30, 50 | — | `:481-490` |
| sidebar tree depth | 3 (`cafe` → `latenight` → leaf) | — | `:506-514` |
| `SynthCount` (a playlist with no data) | 12 … 51 | `12 + Hash(uri) % 40` | `SpotifyExportMapper.cs:1027` |
| `Hash` | deterministic, cross-process | `h = 17; foreach c: h = h·31 + c; h & 0x7fffffff` | `SpotifyExportMapper.cs:1024` |
| synth album tracks | 6 … 13 | `6 + Hash(uri) % 8` | `SpotifyExportSource.cs:168` |
| search results per query | 8 tracks, 3 albums, 2 artists, 2 playlists | offsets are `query.Length`-derived | `FakeData.cs:536-539` |
| lyric document | 40 lines over 12 phrases, word-synced | line every 3 600 ms; word every 450 ms, 400 ms long | `:543-567` |
| default queue | 1 now-playing + 3 user-queue + 8 next-up (last 3 autoplay) | — | `:569-576` |
| Home skeleton | 14 groups + a 3-entry `Sections` ledger | — | `:587-610` |
| export library items | 24 = 22 Playlist + 1 PseudoPlaylist + 1 Folder; **21 accepted** (the Folder at `:70` and the one `listen-later` playlist, "Your Episodes", at `:73` are skipped) | — | `SpotifyExport.cs:63-78`, `playlists.json` |
| export Home | 31 sections / 107 items; 20 Baseline, 8 Generic, 1 Shorts, 1 Spotlight, 1 RecentlyPlayed | — | `SpotifyExport.cs:114-123`, `home.json` |
| export Home chips | **3** (Music / Podcasts / Audiobooks), each with one "Following" subChip | — | `home.json` `data.home.homeChips`, `SpotifyHomeComposer.cs:126`, `:241-248` |
| synthetic QuickGrid | **9** cards, the first 9 of the 21 library summaries, titled `HomeModuleTitles.Default.JumpBackIn` — **not localized in `--fake`** | `QuickPicks = 9` | `SpotifyHomeComposer.cs:28`, `:40-46`; `SpotifyExport.cs:123` |
| home card formats present | `inspiredby-mix` 25 · `editorial` 17 · `format-shows-shuffle` 12 · `descripto` 10 · `topic-mix` 9 · `daily-mix` 6 · `artist-mix-reader` 4 · `artistsets` 3 · `discover-weekly` 1 · `release-radar` 1 · **`daylist` 1** | the `ModuleForFormat` vocabulary, `SpotifyHomeComposer.cs:230-238` | `home.json` |
| export artists | 1 (`spotify:artist:04gDigrS5kc9YWfZHwBETP`, from `artist-maroon5.json`, 396 966 B) | the `artist-*.json` glob | `SpotifyExport.cs:43` |
| remote image urls in the fake Home doc | **≈ 306 across ten hosts** — `i.scdn.co` 166 · `pickasso.spotifycdn.com` 65 · `image-cdn-fa` 32 · `image-cdn-ak` 18 · `seed-mix-image` 10 · `daylist.spotifycdn.com` 8 · `misc.scdn.co` 3 · `lexicon-assets` 2 · `newjams-images.scdn.co` 1 · `shareables.spotify.com` 1 | — | `home.json` (§0.7) |
| remote image urls in `playlists.json` | **≈ 63 across nine hosts**, incl. 3 `mosaic.scdn.co` — a SERVER-composed mosaic cover, not the client's 4-tile `MosaicTiles` (nothing in the mapper writes that field) | — | `playlists.json` (§7.1 item 1) |
| fake session connect delay | 250 ms (LoggedOut → Authenticating → Authenticated) | — | `FakeSpotifySession.cs:19-22` |
| fake user | `u_wavee` / "Wavee Listener" / `IsPremium: true` / `listener@wavee.app`; **AvatarUrl null** | — | `FakeSpotifySession.cs:21` |
| export track-page stream delay | 120 ms per 25-track page | — | `SpotifyExportSource.cs:101-105` |

---

## 4. Colour & material

The seed decides **three** things about colour, and no more.

1. **Whether the cover-palette plane has anything to grade.** Every synthesized surface points at one of 16 local
   jpgs (`FakeData.cs:24-29`), so the palette plane (ch 00 GAP-1 / ch 03 / ch 08 gap 1) has real pixels on the first
   frame and every accent, wash, page tone and shell tint resolves offline. **Home does not**: its cards carry
   `i.scdn.co` urls, so offline the Home wash legs stay null and the page keeps its neutral ground (ch 10 §7: "a leg
   with no grading contributes **no layer**, never a grey one"). This is the difference between an offline Home
   screenshot and a networked one, and a parity item that does not say which is not a parity item.
2. **Whether a payload accent exists before an image decodes.** `HomeCardMeta.Accent`
   (`extractedColors.colorDark`, **114 occurrences** in `home.json`) rides the exported Home document, so the
   `--fake` Home has tier-1 colour with no network; the synthesized surfaces have **none** — only tier-2 grading. In 0.3 (`HomeCardEdge.Accent`, ch 11
   gap 1; `Column<uint> Accent`, ch 06/10) the seed must write an accent for the synthesized entities too, or the
   cold-start colour path is only ever exercised on Home.
3. **The one colour the seed states outright**: `CamelotColors` (`:66-70`) — twelve opaque ARGB values, one per
   Camelot slot, carried on every track as `CamelotColor` (`:59`). They feed the BPM·Key swatch (ch 01 §3) and the
   Liked tempo card (ch 07 W21). They are the wire's own hue family and must be ported value-for-value.

Material is untouched: the seed writes no surface, elevation or blur. Every material decision belongs to ch 00 and
ch 18, and `--fake` exercises them exactly as the live build does, because the shell composition is identical.

**Light and dark.** Nothing in the seed is theme-dependent. The 16 covers are photographic and grade to both halves;
the Camelot colours are opaque and are re-inked per theme by `WaveePalette.DataDotInk` (ch 01 gap 9), not by the
seed.

---

## 5. Motion

The seed animates nothing. It decides whether a motion *has inputs*. Every duration and easing below belongs to the
chapter named in the row; this table exists so a re-author can see which motion families are dark under `--fake`
today and which the new fixtures light up.

| trigger | target | property | from → to | duration | easing | delay/stagger | reduced-motion | file:line |
|---|---|---|---|---|---|---|---|---|
| the seed commits (one publication) | every mounted page | skeleton → content | shimmer → real | ch 03's reveal ramp | ch 03 | ch 00 W12's stagger | ch 00 W15 | `Entities.Fake.cs` step 7 (§2 W17) — the reason step 7 is ONE bump |
| Home cold mount | the derived shimmer | opacity + blur | bones → cards | ch 10 W2 | ch 10 | 40 ms per row, 500 ms total (ch 10 §10 item 3) | ch 10 | `HomePage.cs:90` + `FakeData.cs:587-610` |
| daylist `ExpiresAt` ticking | the hero's flip clock | digit flip | per second | ch 11 W1 | ch 11 | — | ch 11 | **NO INPUT TODAY** — the seed has no `ExpiresAtMs` (ch 30 §7.3 item 5) |
| prerelease `ReleaseAt` ticking | the countdown card | digits | per second / minute | ch 02 W24 | ch 02 | — | ch 02 | **NO INPUT TODAY** — no prerelease in the seed |
| playback position | the deck machines, the seek bar, the lyric wipe | every property ch 20 / 22 / 23 own | — | — | — | — | — | **NO INPUT TODAY** — `UnsupportedPlaybackPlayer` (`Services.cs:589`) |
| queue reorder drag | the queue rows | translate | ch 21 W5 | ch 21 | ch 21 | — | ch 21 | **NO INPUT TODAY** — `DefaultQueue()` has no caller (`:569-576`) |
| a track boundary | the deck's change-record choreography (5.6 s) | ch 23 W5 | — | — | — | — | — | **NO INPUT TODAY** |
| an artist / Liked page mount | the ambient cover bands, the marquee (ch 07 W8) | translate | — | ch 07 | ch 07 | — | ch 07 | works: the covers are local |
| a template preview | the sidebar miniature | **none (static)** | — | — | — | — | — | `SidebarMiniature.cs` — it must not animate; ch 26 W20 is the only transient on that page |
| **the fake session connecting** | the shell's auth arm / the sign-in takeover | phase | LoggedOut → Authenticating → Authenticated | **250 ms**, once, at boot | n/a (a step, not a ramp) | — | ch 18 / ch 28 | `FakeSpotifySession.cs:19-22` — the ONE timed transition `--fake` has today, and the reason a cold screenshot taken before ~250 ms catches the signed-out shell |
| **a track page landing** | the detail table's rows | rows appended | 25 at a time | **120 ms per page** | n/a | 120 ms × ⌈n/25⌉ (166 liked ⇒ 7 pages ⇒ ≈ 840 ms) | ch 04 | `SpotifyExportSource.cs:101-108` — a REAL progressive-landing cadence, so ch 04 W5-W9's partial arms are reachable offline on any playlist/album page (§7.4 I is corrected) |
| Settings ▸ Developer ▸ Simulate update | the update notification row + the sticky toast | state + progress | Checking → Available → Downloading 0-100 → Installing → Completed | 600 / 1 500 / step / 900 ms | n/a | — | ch 19 / ch 27 | `FakeAppUpdateService.cs:30-33`, `:70-115` — developer mode, not `--fake` |

**The rule this table is really stating**: six of the nine motion families in the app have no input under `--fake`
today. The three that DO have one are the three timed things the fake stack owns outright — the 250 ms auth flip,
the 120 ms track-page cadence and the update simulator's four delays — and all three are wall-clock `Task.Delay`s,
not frame-clock ramps. In 0.3 the first two become `Platform.Clock`-driven so a screenshot harness can pin them
(`animations-sample-frame-time` governs the ramps; these are the *inputs* to them). Four of them (daylist, prerelease, queue, lyrics) become reachable with fixtures the seed can simply hold;
two (playback position, track boundary) need the silent sink of §7.4. None of them needs a network.

---

## 6. Interaction

What a gesture actually does in `--fake`, so §10 can say "click this and expect that".

| gesture | 0.2.9 `--fake` outcome | file:line |
|---|---|---|
| Play (any CTA, row, card, keyboard) | **rejected**; `OnPlayIntentRejected` fires the standard "choose a remote device" toast; the player bar stays at "Nothing playing" | `Services.cs:589`, `:631` |
| ♥ / Save / Follow | **works and persists**: `LocalMutationSource` writes the newline-joined uri set into the real settings store | `Services.cs:601-604` |
| Create playlist, add to playlist, reorder | **works, session-only**: `UserPlaylistSource` mints `wavee:playlist:{n}`, holds tracks in memory, is never persisted | `UserPlaylistSource.cs:33-44` |
| Open an empty owned playlist (ch 06 W10) | reachable **only** by creating one in-session | as above |
| Navigate | fully real (routes, tabs, back/forward, drill trail, keep-alive, history) | `ShellRoutes.cs`, `ContentHost.cs:190-300` |
| Search typing | results change with query **LENGTH**, plus a name-match arm over the 21 export playlists and the 1 export artist. For most single letters the name-match arm wins outright, so the Playlists shelf shows export rows and never `Playlist(q.Length)` | `FakeData.cs:536-539`, `SpotifyExportSource.cs:146-154` |
| Sidebar customize / Home customize | fully real — they edit the live preference documents | ch 12, ch 26 |
| Settings, Diagnostics, Logs, About | fully real; the module list and playback-runtime cards show their honest "no host / no session" arms | ch 27 §7 |
| Device picker | opens **empty** | `Services.cs:590` |
| Lyrics toggle | the rail mounts and reports "no lyrics" | `Services.cs:624` |
| Drag a track onto a playlist | the drag language is fully exercised (the chip, the insertion gap, the refusal captions) because the rootlist and membership are resident | ch 01 W21-W22 |
| Right-click any row or card | the full menu grammar, minus the arms gated on a Null seat | ch 01 W19-W20 |
| `WAVEE_FAKE_CHALLENGE=1` | seeds a canned pairing challenge (`WZY5-Q6TX`, expiry `UtcNow + 872 s`) and forces the login takeover — deterministic login screenshots. **An environment-variable switch, which CLAUDE.md's working rules forbid**, and its expiry is a live `DateTimeOffset.UtcNow` read (§0.15, §7.3): in 0.3 it is `Platform.Args.FakeChallenge` with `now0 + 872 s` | `WaveeApp.cs:276-283`, and the gate at `:323`, `:343`, `:388` |
| Settings ▸ Developer ▸ Simulate update | walks Checking → Available → Downloading 0-100 → Installing → Completed through the real notification path (600 / 1 500 / step / 900 ms) | `FakeAppUpdateService.cs:52-60` (`Start`), `:70-115` (the walk), `SettingsPage.General.cs:151` |

**Two things `--fake` is not.** It is not a *logged-out* mode (`FakeSpotifySession` reports `Authenticated`
(`:87`, `:101`), which is why the Charts row fails loud instead of rendering empty — ch 10 §7), and it is not an
*offline* mode (Home art is remote). A 0.3 seed that wants either state must offer it as its own arm, not as a side
effect.

---

## 7. Data & readiness in 0.3 terms

### 7.1 The per-surface fixture inventory

This is the Wave 5 gate's real content. Read each row as: *for this surface's LOADED wireframes to render, `SeedFake`
must contain the middle column.* "today" is `--fake` on the kept 0.2.9 build — **✔** the fixture exists **and reaches
the surface**; **◑** it reaches it partially; **✖** it does not exist or does not reach it.

| # | surface (chapter) | what `SeedFake` must contain for the LOADED state | today |
|---|---|---|---|
| 1 | Design system: cover palette (00 W6-W9, W14) | ≥ 16 distinct local images so every placeholder, tint, wash and scheme has pixels on frame 1; at least one entity with **no** image (ch 03 W26) and one with a 4-tile mosaic | ◑ 16 covers (`:12`), all **600 × 600** — so `Cover(i, 640)` upscales (§3); the only cover-less entity is the generic-playlist accident; **no client-side 4-tile mosaic anywhere** (nothing in `SpotifyExportMapper` writes `MosaicTiles`), though 3 exported playlists carry a server-composed `mosaic.scdn.co` cover url — a network-dependent single image, not the tile fixture ch 02 W1 needs |
| 2 | Track row / table (01 W1-W12, 04 W1-W4) | per row: title, artists (≥ 2 on some), album, 64-px art, explicit on 1-in-6, duration, play count, tempo in three humps, a Camelot slot + its colour, one descriptor tag, a year over 17 values | ✔ `:50-60`, `:96` |
| 3 | Track row: chart row (01 W5), not-yet-out (01 W6) | ≥ 1 playlist with `ChartStatus/Pos/Prev` on every row covering NEW / ▲ / = / ▼; ≥ 1 album with `AvailableAt > now0` on its tail rows | ✖ nothing writes `Chart`; nothing writes `AvailableAt` |
| 4 | Track drawer (01 W14-W15) | alternate versions, audio formats, a waveform, and a **reserved pending** music-video row | ✖ `SwitchableTrackExpansionService(Null)` (`Services.cs:362`) |
| 5 | Track credits modal (01 W27) | ≥ 6 credits across ≥ 3 role groups, ≥ 1 linkable | ✖ `NullTrackCreditsService` (`:323`) |
| 6 | Cards & controls: mosaic (02 W1), face pile (02 W22), stat tiles (02 W23) | a cover-less playlist with ≥ 4 member album covers; ≥ 6 distinct artists across the liked span; album facts | ✖ mosaic · ✔ faces · ◑ tiles |
| 7 | Cards: prerelease countdown (02 W24), daylist flip (02 W25) | an album with `PreRelease` + `ReleaseAt`; a playlist with `ExpiresAt` / `CreatedAt` | ✖ prerelease · **◑ daylist**: the CARD exists (`home.json` + `playlists.json` each carry one `"format": "daylist"`, mapped to `HomeGroupKind.Hero` at `SpotifyHomeComposer.cs:232`) and only `ExpiresAt` / `CreatedAt` are missing (ch 30 §7.3 item 5) |
| 8 | Cards: content-filter chips (02 W21) | a curated chip set on the user row **plus** one chip whose evidence is missing (it must render DISABLED, not hidden) | ✖ `NullContentFilterService` (`:324`) |
| 9 | Detail frame (03 W1-W8, W21-W25) | one album, one playlist, Liked, one show, one local collection — each with enough rows to scroll and a complete meta line | ✔ except the playlist header defect (§0.10a) |
| 10 | Detail frame: notice strip (03 W15) | a playlist with `DeletedByOwner`, one with access revoked, one thin album (rows known but unnamed) | ✖ nothing writes a notice |
| 11 | Detail frame: no cover / ungradeable (03 W26) | one entity whose `Image` is known-and-empty (distinct from unknown) | ◑ the generic playlist arm has no cover — by accident, not by design |
| 12 | Detail table: date-added + added-by lanes (04 W1) | one playlist with `AddedAt` on every row, one **collaborative** with ≥ 2 distinct `AddedBy` users, one curated with neither | ✔ the fixture exists (`:319-333`) · ✖ it is unreachable (§0.10a) |
| 13 | Detail table: membership not landed (04 W9) | one parent whose track edge is left at `State = 0` deliberately | ✖ every seeded edge is complete |
| 14 | Detail table: recommendations (04 W19) | ≥ 20 rows on `PlaylistRecs` for one owned playlist | ✖ no extender on the fake backend |
| 15 | Album (05 W1-W6, W20) | all four `AlbumKind`s; descending play counts so row 1 is the ★; ≥ 12 rows on one | ✔ `:80-88`, `:96` |
| 16 | Album: "About this release" (05 W11) | label, copyright, courtesy, an ISO release date + precision, disc count — **the whole record or none** | ✖ the `Album` record carries none |
| 17 | Album: other versions (05 W15), more-by, featured-on, similar, merch (05 §7 D4-D8) | ≥ 3 versions for one album; ≥ 6 items per trailing shelf | ✖ all |
| 18 | Album: prerelease (05 W13) | one upcoming album, a resolved `spotify:prerelease:` uri, and per-row `AvailableAt` | ✖ |
| 19 | Playlist (06 W1-W6) | an owner playlist, an editorial playlist, one with a long description, two non-Latin titles, a 4-track one and a 50-track one | ◑ the synthetic fixture exists (`:481-490`) · the DETAIL page never sees it. The **export** half covers more than this row credited: "우울해" (non-Latin), "Summer 2016 vibes 🌊🏖️🌅" owned by "#Catherine🌞" (emoji in BOTH title and owner — the grapheme-cluster / long-label fixture), "daylist", 8 editorial rows and 4 user-owned rows, all with real headers |
| 20 | Playlist: inline edit (06 W14-W16) | one playlist whose `Caps` say `CanEditMetadata` **and** one that says it cannot | ✖ the generic arm's caps are `default` |
| 21 | Playlist: collaborators (06 W25) | ≥ 3 users on `PlaylistCollaborators`, with names and avatars | ✖ the `AddedBy` people exist as strings (`:327`), not as user rows |
| 22 | Playlist: tune (06 W24), chart rail (06 W26) | a `PlaylistTuning` edge with ≥ 3 options; a chart playlist with `ChartNewEntries` + `ChartUpdatedAt` | ✖ both |
| 23 | Playlist: notice strip (06 W13), write-failure toasts (06 W22b) | the three notice kinds; one mutation made to fail | ✖ |
| 24 | Liked (07 W1-W13) | ≥ 160 tracks, ≥ 16 distinct album covers (all nine cover styles need `MinTiles`), a real `AddedAt` spread | ◑ 166 rows ✔ and 16 covers ✔ · **`AddedAt` is absent on every liked row** (`:339` → `Tracks()`, which sets none) · and the ALBUM column repeats the TITLE column on every row, because `Track(i)`'s `AlbumRef` takes the track's own title (`:41`) — ch 07 W3's album lane is structurally present and informationally empty |
| 25 | Liked: facts panel (07 W16-W17, W21) | year coverage ≥ 60 %, tempo coverage ≥ 60 %, `Tags[0]` on every row, ≥ 6 ranked artists | ✔ `:50-60` |
| 26 | Liked: empty (07 W19) and 10 000+ (07 W20) | a second scope, or a documented way to clear / raise the liked edge without the UI | ✖ |
| 27 | Artist (08 W1-W4, W8-W13, W18-W21) | name, header photo, avatar, bio, verified, monthly, followers, world rank, a pick, a latest release, ≥ 5 popular tracks with play counts, gallery, links, cities, videos, playlists, merch, concerts | ✔ present — ✖ **non-deterministic** (§0.4) |
| 28 | Artist: discography (08 W14-W17, W22) | ≥ 300 releases in ≥ 1 facet, with independent per-facet totals, so the grid virtualizes and the era bands group | ✖ 2 / 3 / 1 (§0.10b) |
| 29 | Artist: album drawer (08 W15-W16) | the expanded album's tracks resolvable **synchronously** (a warm peek) | ✔ `TryPeekAlbum` always returns true (`FakeSource.cs:28-32`, `SpotifyExportSource.cs:61-65`) |
| 30 | Artist: no image (08 W4b), error (08 W23) | one artist with no header and no avatar; one uri that resolves to a failure | ✖ (the fake stack never fails) |
| 31 | Show / episode (09 W1-W9) | 8 shows, 8-12 episodes each, exactly one in progress at ⅓, `Total == PagedThrough` so no load-more pill, a publisher, a blurb | ✔ `FakePodcastSource.cs:130-133` |
| 32 | Show: load-more (09 W9) | one show with `Total > PagedThrough` | ✖ by construction today |
| 33 | Module page (09 W10-W12, W17, W19) + watch page (09 W13-W15, W18) | one installed module with an `entity` page, one with a `custom` page, one minimal document, one failing document, one watch page with a poster | ✖ `Services.Modules` is null on the fake backend |
| 34 | Home (10 W3-W7; 11 W1-W33) | a document whose sections cover **every** module kind at the count that module shows; a daylist hero with tags + meta + a live `ExpiresAt`; a header-photo hero; a weekly pair; ≥ 10 podium artists; **chips** | ◑ 31 sections ✔ · all eleven `ModuleForFormat` formats present ✔ (§3.1) · daylist **card** ✔ / its clock ✖ · header-photo hero **✔ — `header_image_url_desktop` occurs 14× in `home.json`** (the UNVERIFIED in ch 11 W4 is now settled) · chips ✔ (3, `homeChips`) · podium ✖ (`NullUserTopService`) · and the synthetic QuickGrid duplicates the server's "Jump back in" title (§0.10g) |
| 35 | Home skeleton (10 W1) | a blank-content document of the **same shape and counts** as the loaded one | ✔ `:587-610` |
| 36 | Home: empty feed (10 W10), failed load (10 W11) | a facet that returns zero sections; a facet that fails | ✖ empty · ✔ failed (the Charts row, by accident) |
| 37 | Home: charts (10 W12; 11 W24-W26) | five chart sections with ≥ 3 cards each | ✖ `SwitchableBrowseService(Null)` (`:361`) |
| 38 | Home: timeline (11 W23) | ≥ 4 notification rows, ≥ 2 unread, ≥ 1 concert announcement | ✖ `NullSpotifyNotificationsService` (`:358`) |
| 39 | Home section page (12 W1-W12) | a section whose `Total` exceeds the landed page, with a real cursor, plus a chart section for the filter arm | ✖ `SwitchableHomeSectionService(Null)` (`:364`) |
| 40 | Home customizer (12 W13-W19) | nothing — it projects the live document | ✔ |
| 41 | Search (13 W1-W14) | top hits across **five** kinds, a chip order with per-facet totals, genres, related searches, suggestion rows, a lyrics-match hit, an access-label hit | ◑ 4 kinds; no chips, no genres, no suggestions, no top-hit chrome. The only content-sensitive arm is the name match over the 21 export playlists + 1 export artist (`SpotifyExportSource.cs:146-154`); `LocalSource.SearchAsync` (`:71-81`) contributes matching local TRACKS only — no albums, no artists |
| 42 | Browse (13 W15-W22) | a directory with all five bands populated **and one category page in each of the four `BrowsePageLayout` modes** | ✖ `SwitchableBrowseService(Null)` |
| 43 | Library (15 W1-W13) | 13 albums, 12 artists, 8 shows, 166 liked, each with title + cover + subtitle; a nested playlist tree | ✔ — ✖ the podcast STAT is 7 (§0.10c), and the tree is 21 flat export leaves + 5 synthetic nodes with six duplicated names (§0.10f, §2 W13) |
| 44 | Library: full-text search (15 W11-W13, W26) | a corpus with ≥ 1 artist-level, ≥ 1 album-level and ≥ 1 track-level match, and ≥ 1 hit with no highlight span | **UNVERIFIED** — no `StoreLibrarySource` on the fake backend |
| 45 | Recents (16 W1-W16) | a grouped snapshot: ≥ 6 rows spanning all five day buckets, ≥ 2 groups with members and their own instants, ≥ 1 "Saved" row, `ChildCount ≠ ChildUris.Count` on ≥ 1 row, a revision | ✖ `SwitchableRecentsService(Null)` (`:363`) |
| 46 | History (16 W17-W21) | nothing — it self-seeds from navigation (≥ 10 routes, ≥ 1 retired) | ✔ |
| 47 | Concerts (17 W1-W22) | a saved place and an inferred place, ≥ 8 concepts, a feed with ≥ 2 sections + a pagination key + a count, an artist schedule spanning ≥ 3 months, one detail with ≥ 2 offers and a lineup including a URI-less billing name | ✖ `NullConcertService` (`:360`) — but `SynthConcerts` (`:205-216`) already builds the artist half |
| 48 | Shell frame (18 W1-W24) | a signed-in user with a display name, an avatar and a tier; ≥ 12 routes visited (the tab strip, the drill trail, the history flyout) | ◑ name + premium tier + email ✔ (`FakeSpotifySession.cs:21`) · **avatar is null**, so ch 18 W17's profile chip and ch 19 W4-W6's menu never render their portrait arm offline |
| 49 | Shell overlays: command palette (19 W1-W3) | nothing — static tables plus the route index | ✔ |
| 50 | Shell overlays: notifications (19 W14-W18) | ≥ 4 rows across ≥ 3 categories, ≥ 2 unread, one update row mid-download, one activity card | ✖ (the update arm is reachable through the developer simulator, `FakeAppUpdateService.cs:52`) |
| 51 | Shell overlays: toasts, tips, banners (19 W19-W23) | reachable today — a play intent raises the standard toast (`Services.cs:631`) | ✔ |
| 52 | Shell overlays: setup dialog (19 W24-W33), sign-in (28 W3-W7) | `WAVEE_FAKE_CHALLENGE` for the pairing card; a runtime catalog for the nine provisioner states | ◑ challenge ✔ (`WaveeApp.cs:276-283`) · runtime states ✖ |
| 53 | Player bar (20 W1-W23) | **something playing** — a current track, a duration, a position that advances, a context, a queue | ✖ the single largest gap |
| 54 | Player bar: remote / live / DVR (20 W10-W13) | a foreign owner plus a device roster; a live source with a DVR window | ✖ (§7.4 D: the ROSTER is fakeable, the TRANSFER is not) |
| 55 | Right rail / NPV (21 W1-W2) | a playing track with art, artists and album; an artist About block; top cities; credits; merch | ✖ |
| 56 | Queue (21 W3-W5, W12) | 1 now-playing + 3 user-queue + 8 next-up, the last three autoplay, ≥ 1 removable | ✔ `DefaultQueue()` `:569-576` — **✖ no caller** |
| 57 | Friends (21 W6) | ≥ 4 friends, ≥ 1 offline, ≥ 1 with a stale timestamp | ✖ `NullFriendActivityService` (`:325`) |
| 58 | Stage (21 W11-W15) | as 53, plus a stream-format string | ✖ |
| 59 | Lyrics (22 W1-W16, W20, W22) | a 40-line **word-synced** document that overflows the ~11-line viewport; a line-synced one; an unsynced one; one with a translation; one with a ≥ 5 s interlude; and a track with none | ◑ the word-synced doc exists (`:543-567`) — **✖ no caller** (`NoLyricsProvider`, `:624`); the other five do not exist |
| 60 | Lyrics inspector (22 W17-W19b) | ≥ 2 providers with traces, raw payloads and parsed candidates | ✖ |
| 61 | Decks (23 W1-W28) | as 53, plus an album boundary, a duration, level frames, a stream format and a bitrate | ✖ |
| 62 | Video (24 W1-W25) | a resolved source with a natural size, a poster, a LIVE arm, plus ≥ 2 override roster rows (one with a missing file) | ✖ |
| 63 | Sidebar (25 W1-W22) | a folder inside a folder; ≥ 5 pinned rows including one editorial pin the library does not contain; per-kind counts; a playlist flavour mix (owned + Spotify + editable) so the V3 qualifiers appear | ◑ tree ✔ (`:501-517`) · pins and flavours ✖ |
| 64 | Sidebar: pending / offline feeds (25 W15; 26 §10 items 61, 69) | one data source deliberately left `Pending` and one left `Offline` | ✖ |
| 65 | Sidebar customizer (26 W1-W21) | **index-addressable** `Playlist(1, 2, 5, 7, 8, 10, 12, 14)`, `Playlist(index + 6)`, `Artist(3)`, `LibraryStats()` — 2 and 7 are the grid-strip cells | ✔ today; §7.2 makes it a rule |
| 66 | Settings + diagnostics (27 W1-W29) | the real store, the real process, the real log ring — plus a crash-report file and ≥ 2 past log sessions to make W22 and W27 reachable | ✔ except the two file fixtures |
| 67 | Setup / What's new / Feedback (28 W1-W26) | the canned challenge; the bundled release-notes document; a highlight poster on disk | ◑ |
| 68 | Appearance preferences (30 W1-W16) | every kind × every mode: album (4 kinds), playlist, Liked, local, show, **prerelease** — each openable at every breakpoint | ◑ — prerelease ✖ (ch 30 §7.3 item 5) |
| 69 | Cross-cutting OS surfaces (29 W1-W5) | **something playing**, ≥ 6 distinct recent contexts (the jump list), and a notification feed with ≥ 4 unread (the burst summary) | ✖ |

### 7.2 The determinism rule

Chapter 26 discovered it on one dialog; it is general, and it has four parts. `SeedFake` is a **pure function of its
one argument**, and the four rules below are what make that sentence true rather than aspirational.

**Rule A — no randomness of any kind.** No `Random`, no `Guid.NewGuid`, no dictionary or set iteration order, and —
the one 0.2.9 actually violates — **no `string.GetHashCode()`**. .NET Core randomises string hashing per process, so
`FakeData.cs:147`'s `Math.Abs(s.Artist.GetHashCode())` reseeds every artist facet on every launch (§2 W15). Replace
it with the deterministic hash the export side already uses:
`Hash(s) { h = 17; foreach c: h = h·31 + c; return h & 0x7fffffff; }` (`SpotifyExportMapper.cs:1024`). This is also a
latent crash: `Math.Abs(int.MinValue)` throws, and a randomised hash can return it.

**Rule B — total for any `int`.** `Wrap(i, n) = ((i % n) + n) % n` (`FakeData.cs:13`) is applied at every array index,
because a uri-derived index can be huge and the `i·6` / `i·10` offsets can overflow to negative — the file documents
the exact crash this fixed on the artist page's pending-shape path (`:52-55`). Port the discipline, not just the
data: in 0.3 the index comes from the uri's interned `StringId`, and every table lookup wraps.

**Rule C — index-addressable and O(1).** `SeedFake.Playlist(7)` must name the same playlist forever, and resolving it
must not scan. The slot of fixture `i` of kind `K` is `SlotOf(K, i) = K.Base + i`, fixed at seed time, so
`Sample(kind, index)` is an array read. `SidebarMiniature`'s nine hard-coded indices (§2 W13) are the reason;
`Entities.SeedFake.Sample` is the API they call.

**Rule D — a sample is not a read of the live tables.** The miniature previews a *template*, not this account
(ch 26 §7.1: "it must NOT read the live document"). `Sample` therefore reads the seed's own arrays and works on the
**real** backend too, where the tables hold a real account. That is why the miniature's liked badge (161) and the
sidebar's (166) legitimately differ, and why the 0.3 seed must expose `Sample` even in a build that never calls
`SeedFake()`.

**Rule E — the seed is not the only state a launch carries.** `--fake` opens the **real** settings store
(`Services.cs:593`), and three documents plus one mutation set survive a relaunch: the theme/appearance preferences,
`sidebar-layout.json` (`SidebarPreferences`, `Services.cs:331`), the home-layout document (`:332`) and the
newline-joined `SavedLibrary` key the liked/saved outbox writes (`:600-604`). The liked SEED is applied **only when
that key is empty**. So a determinism claim has two halves: `SeedFake(now0)` is pure, **and** the store is at a known
state. In 0.3 that means `Platform.Args.Fake` selects a **scoped store root** (or `--fake --reset-state` wipes it),
and §10 item 40 runs from that known state — otherwise "two launches are pixel-identical" is only true for the
second and third launch, never the first. 0.2.9's own E2E harness already wipes `%LOCALAPPDATA%\Wavee` for exactly
this reason (CLAUDE.md, "Commands").

**What "deterministic" is checkable as** (§10 items 40-44): launch twice, screenshot Home, an album, an **artist**, a
playlist, Liked, the sidebar and the customizer's template confirmation; the pairs are pixel-identical. Then move the
clock forward a day and repeat: the pairs are still identical, because §7.3 removed the wall clock.

### 7.3 The states that need a clock, and how the seed fakes time

`SeedFake(long now0)` takes one timestamp. Everything dated is `now0 ± a constant`, and `now0` itself is
`Platform.Clock.SeedEpoch` — which is **not** `DateTimeOffset.Now`:

```csharp
// Platform/Platform.cs (CORE)
// The seed's time origin. Two rules:
//  (1) a screenshot taken on any day must be identical, so the DEFAULT is a FIXED instant;
//  (2) a human demoing the app wants "3 days ago" to mean 3 days ago, so a launch may opt into the real clock.
public static long SeedEpoch => Args.FakeLiveClock ? Clock.UnixSeconds : FixedSeedEpoch;
public const  long FixedSeedEpoch = 1_788_000_000;   // a stamped instant, never "now"
```

The `--fake` default is the fixed instant (screenshots and the Wave 5 gate); `--fake --live-clock` opts into the real
one (demos). §2 W16 lists every fixture and its offset. Three consequences worth stating outright:

1. **A countdown still ticks.** `ExpiresAt = now0 + 4 h 37 m` is a *fixed target*, so on the fixed clock the daylist
   hero's countdown reads the same value in every **frame-1** screenshot and still decrements while the app runs,
   because the countdown reads the live frame clock against that fixed target (ch 11; motion samples
   `FrameTime.NowQpc`, per the `animations-sample-frame-time` rule). A screenshot at t = 0 and one at t = 90 s differ
   by 90 s — which is correct, and which is why §10 item 41 compares *frame 1* of two launches, not two arbitrary
   frames.
2. **Every derived "is it soon" verdict becomes stable.** `TourBannerFor`'s eyebrow ladder (`FakeData.cs:170-180`)
   picks ON TOUR NOW / UPCOMING TOUR / UPCOMING DATES / UPCOMING SHOW from `(next.Date − now).TotalDays <= 7`. With
   `now0` fixed and the concert offsets fixed, the seed can guarantee **both** arms exist: artist A's first concert at
   `now0 + 2 d` (ON TOUR NOW) and artist B's at `now0 + 21 d` (UPCOMING TOUR).
3. **`AvailableAt` and `ReleaseAt` are one mechanism.** A prerelease album is `ReleaseAt = now0 + 9 d 4 h` with
   `AlbumFlags.PreRelease`, and its rows 4..12 carry the same `AvailableAt` — which is what gives ch 05's countdown
   derivation (`PreReleaseDerivation.UpcomingAt`) evidence to work on, and what makes ch 01 W6's not-yet-out dimming
   reachable. One constant lights three surfaces.

**What the 0.2.9 seed does instead, and must stop doing** — seven wall-clock reads, each producing a value that
drifts between days: `FakeData.cs:176` (the tour "soon" test), `:208` + `:213` (concert dates), `:328` + `:331`
(playlist `AddedAt`), `:410` + `:417` (episode `PublishedAt`). Under the 0.3 rule all seven become `now0` offsets.

### 7.4 The states that cannot be faked — the live checklist, not the Wave 5 gate

Each of these needs a peer that is not this process. Faking them would mean faking the protocol, which tests the fake
and not the app. They belong to plan §8.4 ("Live: … Connect transfer to and from another device …"), and the Wave 5
gate must not claim them.

| # | state | why a seed cannot produce it | where it belongs |
|---|---|---|---|
| A | **Audio actually decoding** (the deck level meters, the stream format, the bitrate, gapless, crossfade) | needs a CDN body, an audio key and the PlayPlay runtime | live checklist — **but see the note below** |
| B | **A Connect transfer** (either direction), `is_active` changing owner, the put-state verdict, a foreign device pausing us | ownership is decided by the cluster plus the put-state response (`connect-ownership-single-authority`), and there is no second device | live checklist |
| C | **A dealer push** (a playlist mutated elsewhere landing mid-scroll, a rootlist reorder arriving, a liked delta) | a real hm:// socket with a real account | live checklist |
| D | **A real device roster with a real active id** | the roster is the cluster's | live checklist — though a *static* roster IS fakeable and lights ch 20 W16's picker geometry (§7.1 item 54) |
| E | **Real search ranking** (`chipOrder`, `topResultsV2`, the lyrics-match and access-label chrome) | the ranking is the server's; a fake can produce the SHAPE but never the semantics | the shape is in scope for the seed (item 41); the semantics are live-only |
| F | **A DRM video licence or a live broadcast** | PlayReady plus a module broadcast source | live checklist |
| G | **A real app update** (download, signature trust, restart) | a signed release on a feed | the developer simulator (`FakeAppUpdateService.cs`) covers the UI states; the real path is `ops/release/tests/local-update-e2e.ps1` |
| H | **A network failure mid-page** (ch 03 W16, ch 08 W23, ch 10 W11, ch 13 W11, ch 15 W24) | the fake stack is complete-at-construction and cannot fail | needs a deliberate `SeedFake` failure arm — DATA GAP 9 |
| I | **A cold cache / a genuinely slow load** (ch 03 W9-W10, ch 10 W2's reveal, every page-level skeleton) | the seed commits before the first frame | needs a deliberate delay arm — DATA GAP 9. **Partly false as written**: `SpotifyExportSource.StreamTracksAsync` awaits **120 ms per 25-track page** (`:101-108`), so a playlist or album TRACK TABLE really does land progressively — 166 liked rows over 7 pages ≈ 840 ms — and ch 04 W5-W9's partial-table arms ARE reachable offline today. What is unreachable is a cold *page* (the header, the shelves, the Home reveal) and any failure |

**The one that looks live and is not.** "Something playing" (§7.1 items 53-61, 69, and every OS surface) is *not* in
this table. It needs no network: `Playback.Host` can be opened on a seeded context with a **silent sink** that
advances `PositionMs` from the frame clock. That is what lights the player bar, the rail, the NPV, the deck machines,
the lyric wipe, the queue, the stage, SMTC, the taskbar and the jump list — ten surfaces, one fixture. It is the
highest-value single item in this chapter, and 0.2.9 forecloses it with `UnsupportedPlaybackPlayer`
(`Services.cs:589`).

### 7.5 The seed's own shape, in 0.3 terms

`SeedFake` writes the same columns and edges the decoder writes. Sketch — names are the plan's and the sibling
chapters'; bodies marked `…` are what owner Q ports:

```csharp
// Entities/Entities.Fake.cs — CORE. No engine types, no I/O, no clock, no Random, no string.GetHashCode.
public static partial class Entities
{
    /// The demo catalog, as columns. Called once, from Entities.Boot, only when Platform.Args.Fake.
    /// Every value below is a pure function of (its fixture index, now0).
    public static void SeedFake(long now0)
    {
        var s = Current;                                   // the Scope: the table set for this launch

        // ── 1. intern the closed vocabularies once ─────────────────────────────────────────────────
        Span<StringId> titles = stackalloc StringId[SeedTitles.Length];
        for (int i = 0; i < SeedTitles.Length; i++)
            titles[i] = Strings.Intern(SeedTitles[i]);     // the §3.4 ReadOnlySpan<byte> overload

        // ── 2. rows, in slot order, so SlotOf(kind, i) == Base + i ────────────────────────────────
        int trackBase = s.Tracks.AllocRun(TrackCount);     // ONE EnsureCapacity, one run of slots (GAP 3)
        for (int i = 0; i < TrackCount; i++)
        {
            int slot = trackBase + i;
            s.Tracks.Title[slot]        = titles[Wrap(i, SeedTitles.Length)];      // FakeData.cs:40
            s.Tracks.Image[slot]        = CoverId(i);                              // assets/covers/coverNN.jpg
            s.Tracks.DurationMs[slot]   = 138_000 + (i * 37 % 150) * 1000;         // :42
            s.Tracks.Year[slot]         = (ushort)(2008 + i % 17);                 // :60
            s.Tracks.Tempo[slot]        = (ushort)(TempoBpm(i) * 10);              // :50-51, ×10 per plan §4.2
            s.Tracks.Camelot[slot]      = (byte)(1 + Wrap(i, 12));                 // :56
            s.Tracks.CamelotColor[slot] = CamelotColors[Wrap(i, 12)];              // :66-70, value-for-value
            s.Tracks.Flags[slot]        = i % 6 == 0 ? TrackFlags.Explicit : 0;    // :58
            s.Tracks.AvailableAt[slot]  = 0;                     // overwritten for the prerelease rows, §7.3
            s.Tracks.Known[slot]        = TrackFields.Row | TrackFields.Audio | TrackFields.Tags
                                        | TrackFields.Year | TrackFields.PlayCount | TrackFields.Availability;
            s.Tracks.Authority[slot]    = Authority.Seed;                          // below Wire (GAP 2)
            s.Tracks.Version[slot]      = 1;
            s.Tracks.ByUri[UriOf(EntityKind.Track, i)] = slot;
        }
        …

        // ── 3. edges as whole CSR runs, never a per-row Add ────────────────────────────────────────
        s.Edges.AlbumTracks.ReplaceRun(albumSlot, trackSlots, discAndNumber);
        s.Edges.PlaylistTracks.ReplaceRun(plSlot, trackSlots, stamps);   // AddedAt / AddedBy / Chart
        s.Edges.Liked.ReplaceRun(meSlot, likedSlots, likedStamps);       // AddedAt = now0 − (i·2 d + (i%5) d)
        …

        // ── 4. the clock-anchored fixtures (§2 W16) ───────────────────────────────────────────────
        s.Playlists.ExpiresAt[daylistSlot] = (int)(now0 + 4 * 3600 + 37 * 60);
        s.Playlists.CreatedAt[daylistSlot] = (int)(now0 - 3 * 3600 - 23 * 60);
        s.Albums.ReleaseAt[prereleaseSlot] = (int)(now0 + 9 * 86400 + 4 * 3600);
        s.Albums.Flags[prereleaseSlot]    |= AlbumFlags.PreRelease;
        …

        // ── 5. ONE publication ────────────────────────────────────────────────────────────────────
        s.PublishAll();                                    // bumps each touched table's Changed exactly once
    }

    /// A STATIC sample of the seed, valid on EVERY backend. The sidebar miniature's only data source.
    /// Index-addressable and O(1) by construction (§7.2 rule C); never reads Entities.Current (rule D).
    public static FakeSample Sample(EntityKind kind, int index) => …;

    public readonly record struct FakeSample(StringId Title, StringId Image, int Count, bool Circular);
}
```

Three properties this shape has that the 0.2.9 record graph cannot:

* **It costs one publication.** 0.2.9's fake catalog is rebuilt per read (`FakeData.Album(i)` allocates a fresh record
  graph on every `GetAlbumAsync`), which is why the demo backend's working set moves under navigation. The 0.3 seed
  allocates once, into slabs.
* **`Known` is stated, not implied.** A 0.2.9 fake record is "complete" because every field is non-null; a 0.3 seeded
  row is complete only where it sets the bit. That makes the *absence* of a fixture a first-class, testable thing —
  §7.1 item 13 ("one parent whose track edge is left at `State = 0` deliberately") becomes a one-line seed decision
  instead of an impossibility.
* **`Authority.Seed` makes the origin visible.** The diagnostics row inspector (plan §5.11 cost 3) can say "this value
  came from the seed", which is the answer to "why does this page look right in `--fake` and wrong live".

### DATA GAPS

Everything this chapter needs that the plan's §2 tree, §4 model or §5 wave table does not hold.

| # | element | 0.2.9 source | proposed 0.3 home |
|---|---|---|---|
| 1 | **The seed file itself.** Plan §5 gives owner Q the clause "+ the fake data seed" and names no file, no size and no contents; §2's tree has no entry for it. | `Wavee.Core/Fakes/*` + `Sources/{Local,FakePodcast,UserPlaylist}Source.cs` + `Spotify/SpotifyExport*.cs` | **`Entities/Entities.Fake.cs`**, ≈ 1 100 lines (§9.4), CORE, with `Wavee.Tests/EntitiesFakeTests.cs` |
| 2 | **`Authority.Seed`.** Plan §4.1 defines `Column<byte> Authority` and D16 an authority ladder, but no rung for a synthetic write. Without one a seeded row and a wire row are indistinguishable, and a live decode cannot cleanly overwrite. | none | one enum value below `Wire`, in `Entities.cs` |
| 3 | **`Table.AllocRun(int n)` and `EdgeTable.ReplaceRun`.** The plan has `Alloc(StringId)` (one slot) and `ReplacePage` (one page). A seed that allocates ~1 700 rows one at a time does ~1 700 `EnsureCapacity` probes and ~30 array resizes before the first frame. | none | two methods in `Entities.cs` / `Edges.cs` — also wanted by the decoder's bulk commit |
| 4 | **`Platform.Args`.** Plan §3.5's `Main` never parses argv. `--fake` is one bool, but `--screenshot`, `--width`, `--height`, `--frames` and the probe flags all ride the same list today. | `Program.cs:439-440` + the probe gate | `Platform/Platform.cs` CORE: a parsed `Args` record, read once |
| 5 | **`Platform.Clock.SeedEpoch`.** §7.3's fixed instant. Nothing in the plan owns a clock at all — yet `Table.FetchedAt` / `Touched` are already "seconds since app epoch" (P7), so the app epoch exists implicitly and must be named. | none | `Platform/Platform.cs` CORE, with `FixedSeedEpoch` a stamped const |
| 6 | **A silent audio sink.** §7.4's note: the highest-value fixture in the chapter needs `Playback.Audio` to expose a sink that consumes nothing while `Playback.Host`'s ticker advances position from the frame clock. Plan §4.9 names five `AudioSource`s and no sink seam. | `UnsupportedPlaybackPlayer` does the opposite (it refuses) | `Playback/Playback.Audio.cs` (Wave 3, owner H): a `SilentSink` selected when `Platform.Args.Fake` |
| 7 | **A home for a seeded lyrics document.** Ch 22 gap 1 proposes `Lyrics { Dictionary<int, LyricsDoc> }` as a side table; the seed must be able to write into it, so that table must exist before Wave 5. | `FakeData.Lyrics` (`:543-567`) — unreachable | `Shell/Lyrics.cs` (Wave 4, owner K) lands the table; owner Q fills it |
| 8 | **Tables for the nine Null seats.** Notifications, recents, concerts, browse, home-sections, friends, credits, top-tracks, popcount and content-filter chips have **no table in the plan at all** (chs 11, 13, 16, 17, 19, 21 each raise the same gap). The seed cannot fill a table that does not exist. | the twelve `Null*` seats (`Services.cs:320-325`, `:358-364`) | each owning chapter's proposal; **the seed's dependency on them must be scheduled before Wave 5, not during it** (§9.3) |
| 9 | **A deliberate incompleteness arm.** §7.4 H/I: `--fake` cannot today produce a skeleton, a partial edge, a failure or a slow load, because every fake source is complete-at-construction (`FakeSource.cs:25-27`). Six chapters' loading and error wireframes are unreachable. | the 120 ms per-page delay at `SpotifyExportSource.cs:105` is the only thing resembling one | `SeedFake` accepts a shape: `SeedFake(now0, SeedShape.Complete \| SeedShape.ColdOpen \| SeedShape.Failing)`, selected by a flag, so a re-author can screenshot a skeleton with no network |
| 10 | **Bundled asset identity.** The seed interns 17 file paths, and the covers must be present beside the exe. `Wavee.csproj:196` copies `assets\**\*`; plan §2 says "assets/ … moved as-is". Make it explicit that `assets/covers` and `assets/spotify` are **fixtures**, not decoration, and that removing them silently empties the demo. | `Wavee.csproj:193-196` | a comment in `Entities.Fake.cs`'s header + a test that every `cover{NN}.jpg` resolves |
| 11 | **`Entities.SeedFake.Sample`.** Ch 26's DATA GAP row asks for it by name. It must be callable on the REAL backend (§7.2 rule D), which means it cannot read `Entities.Current`. | `FakeData.Playlist/Artist/LibraryStats`, called directly from `SidebarMiniature.cs` | a static over the seed's own arrays, in the same file |
| 12 | **A `Track.Chart` writer.** Ch 01 W5 and ch 06 W26 both render chart deltas; `PlaylistTrackEdge.ChartStatus/Pos/Prev` exists in plan §4.3 and **nothing in 0.2.9 writes it** (ch 06 §7 says so explicitly). The seed would be its first and only writer until the live chart decode lands. | none | `SeedFake` writes it; ch 06's note "keep the fields, note the gap the other way" is resolved here |

---

## 8. Pure rules to port verbatim

Every one of these is already engine-free and is a decision, not a rendering. Input types change from records to
handles and indices; the arithmetic may not.

| name | 0.2.9 file | decides | tests under `src/apps/Wavee.Tests` | 0.3 destination |
|---|---|---|---|---|
| `Wrap(i, count)` | `FakeData.cs:13` | totality for any `int`, including the negatives a uri hash and the `i·6` / `i·10` offsets produce (`:52-55`) | **none** | `Entities/Entities.Fake.cs` CORE |
| `IndexFromUri(uri)` | `FakeData.cs:436-445` | the inverse of every generator: the trailing digit run of a uri. The one function that makes `Track` / `Album` / `Playlist` / `Show` round-trip through a route | **none** | `Entities.Fake.cs` — in 0.3 it reads the interned `StringId`'s bytes, not a `string` |
| `AlbumShape(i)` | `FakeData.cs:80-88` | the 6-cycle that gives the catalogue all four `AlbumKind`s with plausible track counts (1 / 5 / 12 / 2 / 18 / 10) | **none** | `Entities.Fake.cs` |
| the `AlbumTracks` play-count curve | `FakeData.cs:96` | descending plays, so row 1 is the visible ★ hit on every album (ch 04 W2, ch 05 W1) | **none** | `Entities.Fake.cs` |
| `TopTracksOf(a, count)` | `FakeData.cs:294-306` | the artist's Popular list **and** the artist play context — one ordered set, title-deduped, play-count descending, so the page and playback agree (`:294-295`) | **none** | `Entities/Artist.cs` CORE (it is a ranking rule, not a fixture) |
| `TourBannerFor(name, concerts)` | `FakeData.cs:170-180` | the tour banner's four eyebrow arms (1 date → UPCOMING SHOW, 2-3 → UPCOMING DATES, ≥ 4 with the next within 7 days → ON TOUR NOW, else UPCOMING TOUR) — and it is **already used on the live path** (`SpotifyExportMapper` calls it for exported artists) | **none** | `Entities/Artist.cs` CORE; ch 08 gap 15 wants it computed at commit into `TourEyebrow/Headline/Subline` |
| `ContextTracks(contextUri)` | `FakeData.cs:460-473` | which ordered list a context uri plays — the rule that makes "play from row N" play the track row N shows | **none** | superseded by edges in 0.3 (`Edges.AlbumTracks` *is* the context); port only the **agreement rule**, and fix the 161 / 166 split (§0.10d) |
| `Hash(s)` | `SpotifyExportMapper.cs:1024` | a deterministic, cross-process string hash: `h = 17; h = h·31 + c; h & 0x7fffffff` | **none** | `Entities.Fake.cs` — and it **replaces** `string.GetHashCode()` at `FakeData.cs:147` (§7.2 rule A) |
| `SynthCount(uri)` | `SpotifyExportMapper.cs:1027` | a stable 12-51 track count for a playlist with no track data | **none** | `Entities.Fake.cs` |
| `SidebarTree.Flatten(nodes)` | called at `FakeData.cs:521` | the tree ↔ flat-list agreement: `UserPlaylists()` delegates to the one flatten helper "so it can never drift from the tree's nesting rules" (`:519-520`) | ported by chs 25/26 (the sidebar reducer suite) | `Shell/Sidebar.cs` CORE (ch 25) — the seed calls it, it does not own it |
| `AggregateCatalog.KindMatches(ak, dk)` | `AggregateCatalog.cs:94-99` | Singles ⇒ `Single` **or** `EP`; the one filter shared by the offline slice and the live facet, so the counts match | **none** | `Entities/Artist.cs` CORE — 0.3's three per-facet edges (ch 08 gap 8) must use it at seed and decode time |
| `DetailHeaderMergeRules.{IsRollingIdentity, ResolveTitle, ResolveIncomingCover}` | `DetailHeaderMergeRules.cs:26-27`, `:32`, `:39` (called from `DetailPage.cs:124-125`, `:141`) | why a fake playlist's sidebar title loses to the loaded generic one (§2 W7): the preview wins only for a rolling identity, and `--fake` has no `ExpiresAtMs` anywhere, so `IsRollingIdentity` is **always false** offline — the rolling arm is unreachable until the seed writes a daylist clock | `Wavee.Tests/Actions/DetailHeaderMergeRulesTests.cs` | `Entities/Playlist.cs` CORE (ch 06) — unchanged; the seed must make the two inputs agree instead |
| `SpotifyHomeComposer.{ModuleForFormat, ModuleFor, EmitSectionGroups, IsEditorialFormat, RecentsTotal}` | `SpotifyHomeComposer.cs:164-199`, `:216-238` | the whole card→module classification the `--fake` Home is made of: `"daylist" ⇒ Hero`, `daily-mix ⇒ MixBand`, `discover-weekly`/`release-radar` ⇒ `WeeklyPair`, `topic-mix`/`artist-mix-reader` ⇒ `ChipCards`, `inspiredby-mix` ⇒ `RadioDial`, the four editorial formats ⇒ `Featured`; the >½-editorial ⇒ one `Topic` rule; the dominant-bucket rule that decides whether a `SectionEntry` is also emitted; and `RecentsTotal`, which exists because the RecentlyPlayed wrapper's own `totalCount` is 1 (`:97-102`) | **none** | `Spotify/Spotify.Decode.Home.cs` CORE (ch 10/11) — it is the decoder's rule, not the seed's, but the seed's Home fixture is only meaningful through it |
| `SpotifyHomeComposer.Compose`'s QuickGrid prepend | `SpotifyHomeComposer.cs:28`, `:40-46`, `:122-124` | that Home's first row is SYNTHETIC (9 library summaries) and the Spotlight hero is inserted *after* it — the order no wireframe had drawn (§2 W1), plus the `HomeModuleTitles` seam that `--fake` fails to pass through (§0.10g) | **none** | ch 10's landing projection; in 0.3 the titles record must be loc-resolved on BOTH paths |

**The test file this chapter creates.** `Wavee.Tests/EntitiesFakeTests.cs`, per plan D17 (one test file per core
file). Its assertions are the fixture inventory:

1. `SeedFake(now0)` into two fresh `Scope`s ⇒ every column of every table is byte-identical.
2. `SeedFake(now0)` vs `SeedFake(now0 + 86_400)` ⇒ identical **except** the dated columns, which differ by exactly
   86 400. This is what proves there is no second clock read.
3. `Sample(kind, i)` is stable across calls and equals the seeded row for every index the miniature uses
   (playlists 1, 2, 5, 7, 8, 10, 12, 14; artist 3), and for `index + 6` over the section range.
4. `Wrap(i, n) ∈ [0, n)` for `int.MinValue`, `-1`, `0` and `int.MaxValue`.
5. Every count in §3.1: 16 covers, 4 album kinds at 1 / 5 / 12 / 2 / 18 / 10, 166 liked, 8 shows, 8-12 episodes with
   exactly one in progress, 14 local, 7 named playlists, a folder inside a folder, ≥ 300 in one discography facet.
6. **The agreement rule** (§0.8): for every uri that appears both as a card/row and as a detail subject, the title,
   owner, cover and count are equal. This is the test that would have caught §0.10a and §0.10d.
7. Every `Known` bit the seed sets has a non-default value behind it (no row claims `Knows(Publishing)` with an empty
   label).
8. `Hash("Christos")` equals a **literal** — the algorithm is closed-form (`SpotifyExportMapper.cs:1024`), so this
   fails immediately if anyone swaps it back to `GetHashCode()`.
9. **Cross-source name/uri collisions are deliberate or absent.** For every fixture name that exists twice under two
   uris (§0.10f), either the seed stops minting the duplicate, or the test asserts the pair exists on purpose and
   the two rows carry different owners — so a reviewer reading a screenshot can tell which is which.
10. **The bundled asset inventory is pinned**: 16 covers at 600 × 600, `liked-songs-300.png` at 300 × 300, the four
    json files present, `playlists.json` ⇒ 21 accepted playlists + `LikedCount` 166, `home.json` ⇒ 31 sections /
    107 items / 3 chips / exactly one `"format": "daylist"`. These are the numbers every count in §3.1 descends
    from, and a silently re-captured export would otherwise move them without failing anything.

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **Do not collapse `SeedFake` into "a few sample entities".** §7.1 has 69 rows because the app has 69 distinct
   loaded states worth seeing offline. A seed with one album, one playlist and one artist passes the Wave 5 gate as
   written and tells a re-author nothing.
2. **Do not let the seed touch the UI.** No `if (fake)` in a page, a component, a shelf or a row. The seed writes
   columns; the UI cannot tell. 0.2.9 holds this line, and it is worth naming, because the shortest path to "make
   `--fake` show a playing track" is a UI branch.
3. **Do not port `FakeData.cs:147`.** `string.GetHashCode()` is the single defect that makes every artist page
   unscreenshottable (§2 W15). Replace with `Hash` (`SpotifyExportMapper.cs:1024`).
4. **Do not port the seven `DateTimeOffset.Now` reads.** §7.3.
5. **Do not "fix" the miniature by pointing it at the live tables.** Ch 26 §7.1 forbids it; §7.2 rule D says why it
   also has to work on the real backend.
6. **Do not drop the blank-shaped Home seed.** It is not duplicate data — it is the skeleton's silhouette
   (`HomePage.cs:90`), and its counts must track the loaded document's; the source says so at `:583-586`.
7. **Do not delete `FakeData.Lyrics`, `DefaultQueue`, `Discography` or `SearchSeed` as dead code.** They are four of
   the fixtures §7.1 asks for, already written and already correct. The bug is the missing wiring.
8. **Do not treat the bundled export as disposable.** `home.json` is the only document in the repo with a real
   31-section, 107-card, five-typename Home shape. Re-deriving it by hand would take longer than the rest of the seed
   and be less faithful. Keep it; in 0.3 it is decoded by `Spotify.Decode.Home` from a file instead of a socket,
   which also makes the decoder testable without a network.
9. **Do not let "the app is dark for Waves 1-4" (plan §7) hide this.** The seed is what makes Wave 5's "the app runs
   at the end" a true statement. If it lands last inside Wave 5, the four other Wave-5 owners have nothing to look at
   while they build.

### 9.2 Where plan sections 2, 4 and 5 are wrong or thin

| plan site | what it says | what is wrong |
|---|---|---|
| §2 (the tree) | `Entities/` lists 40 files; none of them is the seed | The seed is a file. It needs a row, a line budget and a CORE/SHELL marker. Ch 29 proposed `Entities/Entities.Fake.cs`; this chapter files it. |
| §5, Wave 5, owner Q | "Queue.UI.cs + the fake data seed (`Entities.SeedFake()` replaces Wavee.Core/Fakes)" | (a) The seed does not replace `Wavee.Core/Fakes` alone — it replaces `Fakes/*` **and** `Sources/{Local,FakePodcast,UserPlaylist}Source.cs` **and** the seeding half of `Spotify/SpotifyExport*.cs`: 1 613 lines, not 733. (b) It is scheduled **last**, in the very wave whose gate depends on it. (c) It is paired with `Queue.UI.cs`, which ch 21 §9 already argues belongs to Wave 4 owner K. Owner Q's clause is, in practice, the whole of this chapter. |
| §5, Wave 5 gate | "`dotnet run -- --fake` opens every route from the nav probe list" | Two problems. **The gate is unfalsifiable**: §2 W14 shows 28 destinations all "opening", 7 of them empty. **The list is wrong**: `WaveeNavProbe`'s `HeavyRoutes` (`:34-43` — 8 entries: `home`, `liked`, `pl:…pl0`…`pl5`) and `CheapRoutes` (`:44-48` — `albums`, `artists`, `podcasts`, `local`, `browse`) are 13 routes chosen for *frame-cost* measurement, not coverage, and six of the eight heavy ones are the SAME route with a different id. They omit search, concerts, recents, history, whatsnew, settings, show, artist, disco, module, prerelease and every section route — though they DO include `browse` and `local`, which an earlier draft of this row denied. Worse for this chapter: five of the eight heavy entries are `spotify:playlist:pl{i}`, precisely the uris §0.10a shows render the GENERIC arm, so the probe`s own comment “a heavy real playlist (40 tracks + cover)” (`:33`) describes a page that does not exist. §10 item 1 is the list the gate needs. |
| §5, Wave 4 gate | "`--fake` shows the shell frame with an empty content host and a working sidebar" | A "working sidebar" needs the rootlist edge, the pin set, per-kind counts and a nested folder — i.e. it needs part of the seed **in Wave 4**. Either the gate softens to "an empty sidebar rendering its skeleton rows", or a minimal `SeedFake` lands with Wave 4's owner J. §9.3. |
| §4 | defines `Authority` per column group (D16) and names no rung for synthetic data | DATA GAP 2. |
| §4.1 | `Table.Alloc(StringId)` allocates one slot | DATA GAP 3: a seed needs a run allocator, and so does the bulk decode commit. |
| §3.5 (`Main`) | `Platform.Boot(); Entities.Boot(Platform.Scope); …` — argv is never parsed | DATA GAP 4. The seed's call site is inside `Entities.Boot`, gated on a parsed flag that does not exist yet. |
| §8.3 | "`dotnet run … --fake` opens every route in the nav probe list; ReuseGuard silent" | The same defect as the Wave 5 gate, in the definition of done. Replace with "§10 of `31-fake-data.md` passes". |
| §8.4 | the live list | Correct, and it is where §7.4's states belong. Add D (a real device roster), E (search semantics) and H/I explicitly. |

### 9.3 The scheduling correction this chapter forces

The seed is not a Wave-5 leaf. It has a hard dependency on tables Waves 1 and 4 create, and Waves 4 and 5 have gates
that depend on *it*. The honest order:

```
 Wave 0   Entities/Entities.Fake.cs exists as an empty partial with its header, as every §2 file does.
 Wave 1   Owners A/B land the tables and edges; AllocRun/ReplaceRun (GAP 3) and Authority.Seed (GAP 2) ride along.
          Platform.Args + Platform.Clock.SeedEpoch (GAPS 4, 5) must land HERE, not in Wave 5: the seed's call site
          and its one argument are both Platform's, and WAVEE_FAKE_CHALLENGE's replacement rides the same record.
          → owner Q lands SEED-CORE: tracks, albums, artists, playlists, shows, episodes, the local collection,
            the rootlist tree, the liked edge.  ≈ 600 lines.  Gate: EntitiesFakeTests green.
            This also gives Waves 2-4 a populated table set to develop against — which plan §7 calls "the app is
            dark for Waves 1-4" and treats as unavoidable. It is not.
 Wave 4   Owner J's sidebar gate uses SEED-CORE (rootlist + pins + counts) instead of "an empty content host".
          Owner K lands the Lyrics side table (GAP 7)   → owner Q fills it with the 40-line document.
          Owner H lands the SilentSink (GAP 6)          → owner Q seeds a playing context.
 Wave 5   Owner Q lands SEED-SURFACES: the home document + its skeleton, search results, the queue, recents,
          notifications, concerts, browse, the prerelease and daylist clock fixtures, the chart deltas.
          ≈ 500 lines.  Gate: §10 of this chapter, not "opens every route".
```

### 9.4 Line budget

| | lines |
|---|---|
| 0.2.9, everything this chapter replaces | **1 613** — `FakeData.cs` 633 · `FakeSource.cs` 79 · `FakeSpotifySession.cs` 34 · `FakePodcastSource.cs` 21 · `LocalSource.cs` 85 · `UserPlaylistSource.cs` 145 · `SpotifyExport.cs` 127 · `SpotifyExportSource.cs` 186 · the seeding half of `SpotifyExportMapper.cs` (`Hash`, `SynthCount`, `MapArtist`, `MapPlaylistHeader`, `MapTrack`, `PickImage`, `CardFromEntity`) ≈ 250 · `Services.CreateFake` 51 · `Program.cs` 2 |
| plan §2 target | **0** — there is no entry for the seed in the tree, and owner Q's clause carries no number |
| chapter 29's estimate | 850 |
| **honest estimate** | **≈ 1 350** — `Entities/Entities.Fake.cs` **1 100** (SEED-CORE 600 + SEED-SURFACES 500) · `Platform/Platform.cs` share **60** (`Args`, `Clock.SeedEpoch`, `FixedSeedEpoch`) · `Entities/Entities.cs` share **40** (`AllocRun`, `Authority.Seed`) · `Edges.cs` share **30** (`ReplaceRun`) · `Playback/Playback.Audio.cs` share **50** (`SilentSink`) · `Spotify/Spotify.Decode.cs` share **70** (decode the bundled `home.json` from a file — it replaces `SpotifyExport.cs` plus the mapper's seeding half and is *not* fake-specific: it is the decoder's own offline fixture path) |
| plus | `Wavee.Tests/EntitiesFakeTests.cs` ≈ **350** |
| assets | unchanged: 1.9 MB, moved as-is |

Chapter 29's 850 is low by roughly 35 % because it counted only `FakeData.cs`'s successor — not the four source
classes, the export loader, the `Sample` API, the clock plumbing or the run allocators.

### 9.5 Two decisions the owner must make explicitly

1. **Does `--fake` get a playing track?** §7.4's note says it can, with a silent sink, and that it lights ten
   surfaces. The cost is `Playback.Audio`'s sink seam (GAP 6) plus a decision about what the OS surfaces do — SMTC
   will publish a real "now playing" card for a track that makes no sound. **Recommendation: yes**, and let the OS
   surfaces publish; ch 29's parity items 1-19 are otherwise unreachable offline.
2. **Does the bundled Spotify export survive?** It is 1.5 MB of captured production payloads and the only faithful
   Home document in the repo. Keeping it means `Spotify.Decode` gains a file-fed path — which its own tests want
   anyway (plan §5, Wave 2). Dropping it means hand-authoring 31 sections. **Recommendation: keep**, and promote the
   file-fed decode from "the fake path" to "the decoder's offline fixture path".

---

## 10. Parity checklist

Side by side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
Unless stated, a **maximized 1 440 × 900 window with the sidebar at its default width**. Items marked **[new]** do not
pass on 0.2.9 and are the fixtures this chapter adds; items marked **[live]** are §7.4 states that belong to plan §8.4,
not to the Wave 5 gate. Every item is binary.

**The route sweep — this replaces "the nav probe list"**

1. Launch `--fake` and open, in order: `home`, `search` (type "a"), `browse`, `albums`, `artists`, `podcasts`,
   `liked`, `local`, `recents`, `history`, `concerts`, `whatsnew`, `settings`, `sidebar-customize`, `home-customize`,
   `album:spotify:album:al2`, `pl:spotify:playlist:pl4`, `artist:spotify:artist:ar3`, `disco:0:spotify:artist:ar3`,
   `show:wavee:show:3`, `home-section:wavee:skeleton:section:topic`, `browse:spotify:page:pop`,
   `concert:wavee:concert:1:0`, `artist-concerts:spotify:artist:ar3`, `module:anything`, `nonsense-route`. **26
   destinations.** Record each as LOADED / PARTIAL / FAILED / EMPTY / ABSENT against §2 W14. The 0.2.9 column is the
   expected result; any 0.3 regression against it is a defect.
2. **[new]** Every one of the 26 reads LOADED, except `nonsense-route` (the not-found page, which *is* its loaded
   state); `module:anything` becomes LOADED once a demo module is seeded.
3. ReuseGuard is silent across the whole sweep (no remount on a table publish).
4. The engine allocation counter is 0 on a scroll frame of the three heaviest pages (`liked`, `home`, an artist).

**The seed's own contents**

5. `home` shows, IN THIS ORDER: a 3-chip row (Music / Podcasts / Audiobooks); a **synthetic "Jump back in"
   QuickGrid of 9 library cards**; the KIMMUSEUM spotlight hero under the greeting "Good morning, Wavee Listener";
   then the document's own sections, ending in 20 single-card baseline rows. The row count is what `Compose`
   produces from 31 sections (§2 W1), **not** 31 — count the sections ledger, not the rows, if you want 31.
   **[new]** Exactly ONE row is titled "Jump back in" (§0.10g), and switching the app language moves it.
6. The cold first frame of `home` shows the 14-group shimmer of §2 W2, in that order, with **no chip bones** and
   **two** Fold-shaped bones.
7. Open `album:…al0`, `al1`, `al2`, `al4`: the eyebrows read SINGLE / EP / ALBUM / COMPILATION and the track counts
   are 1 / 5 / 12 / 18.
8. On `album:…al2`, row 1 carries the ★ and the largest play count; play counts descend monotonically.
9. On `album:…al4` (the compilation) the Album lane is populated per row and the artists differ between rows.
10. **[new]** The prerelease album shows the countdown card with a days+hours value, and its tail rows are dimmed as
    not-yet-out.
11. `liked` shows **166** rows. **[new]** Every row carries a date in the Added column, and the dates span ≳ 330 days.
12. `liked`: cycle all nine cover styles; each renders its art arm rather than falling back to Stock — i.e. ≥ 16
    distinct tiles are available.
13. `liked`'s facts panel populates the years, tempo and blend cards (never the "not enough data" arm).
14. **[new]** `liked`'s chip rail shows a curated chip set including one DISABLED chip.
15. `local` shows 14 rows titled "Sunset Boulevard" … "Morning Pages", header "Local Files" / "On this device", and
    no cover. Durations span 2:30 … 4:40 (150 000 … 280 000 ms), never 4:49.
15a. `album:…al2` row 1 reads **Dalkom Cafe · 67,750,000 · 175 · 9B · 4:38** and the header reads **12 songs · 43
    min** (§2 W3). Any pass that reproduces the old 60,000,000 / 96 · 1B / 2:18 / 38 min row is reading offset 0.
15b. `liked` row 1 reads **Breathe** (Télépopmusik) and row 2 **Lebanese Blonde** — the offset-1000 list — and on
    every row the ALBUM column repeats the TITLE column.
15c. The sidebar's playlist tree shows **21 flat export leaves followed by 5 synthetic nodes**, six names appear
    twice under two uris, and only the synthetic tail has folders (§0.10f, §2 W13). **[new]** In 0.3 either the
    duplicates are gone or both rows carry distinguishing owners.
16. `show:wavee:show:3` shows **11** episodes headed "#11 · Tape Loops" (43 min), "#10 · The Quiet Release"
    (56 min, the ⅓ progress bar), "#9 · Edge Cases" (69 min), and **no** load-more pill.
17. **[new]** A second show shows a load-more pill (`Total > PagedThrough`).
18. `artist:…ar3` shows monthly listeners, followers, a world rank, a bio, a Popular list of 5 with play counts, and a
    Latest-release banner.
19. **[new]** `artist:…ar3` shows all eight optional sections (pick, upcoming, videos, playlists, concerts, merch,
    cities, gallery). Today their presence is a per-launch coin flip (§2 W15).
20. **[new]** `disco:0:spotify:artist:ar3` shows ≥ 300 cards in the Singles facet, with era bands and a working
    pager. 0.2.9 shows **3**.
21. `artist:spotify:artist:04gDigrS5kc9YWfZHwBETP` renders the full magazine page from the real export, with real
    discography item lists.
21a. On `artist:…ar3`, "Latest release" is a **2-track Single** ("Nostalgia 2000s Mix", 2024) and "Popular
    releases" holds 4 cards in RELEASE order, not play order (`FakeData.cs:156-159`). **[new]** 0.3 seeds an
    album-shaped latest release too, so ch 08's banner has both arms.
21b. The artist hero photo is a 600 × 600 jpg requested at 640 (`Cover(i, 640)`, `:162`): it upscales. **[new]**
    The 0.3 seed either ships a 640-or-larger asset or asks for 600.
22. Open a playlist from the **sidebar tree** (e.g. "Dalkom Cafe"). **[new]** The detail header shows the same title,
    owner, cover and count the sidebar row showed. 0.2.9 shows "Playlist" / "Spotify" / no cover (§2 W7).
23. **[new]** One seeded playlist is collaborative: the Added-by lane appears with ≥ 2 distinct avatars.
24. Library ▸ Albums = 13, Artists = 12, Podcasts = **8**. **[new]** The sidebar's podcast badge also reads 8 (0.2.9
    reads 7).
25. The sidebar tree contains a folder inside a folder ("Cafe & chill" → "Late night").
26. **[new]** The sidebar shows ≥ 5 pinned rows, one of them an editorial pin the library does not contain (the
    display-cache path).
27. `search` for "a": results cover **tracks, albums, artists, playlists** and **[new]** shows, episodes, audiobooks,
    profiles, genres, a top-result hero with its chrome, a chip row with per-facet totals, and related searches.
    0.2.9 shows the first four only — with **3** artists (1 exported "Maroon 5" + 2 synthetic) and export playlist
    rows rather than `Playlist(1)`/`Playlist(2)`, because the name-match arm wins for almost every single letter.
27a. Type "a", then "ab", then "abc": the track/album/artist rows CHANGE with each keystroke even though no result
    is more relevant — they are a function of `query.Length` (`FakeData.cs:536-539`). **[new]** 0.3's seeded
    search index answers by content, so "zzz" returns nothing rather than three albums.
28. **[new]** `browse` shows five populated bands, and one category page in each of the four `BrowsePageLayout` modes
    is reachable.
29. **[new]** `recents` shows rows across all five day buckets, ≥ 2 expandable groups, and ≥ 1 "Saved" row.
30. `history`, after the sweep in item 1, shows ≥ 20 rows grouped by bucket, and the Most-visited sort collapses them.
31. **[new]** `concerts` shows a hub feed with ≥ 2 sections, a count ticker, a genre strip and a working date flyout;
    `artist-concerts:…ar3` shows a month board; `concert:…` shows a detail with ≥ 2 offers and a lineup containing a
    URI-less billing name.
32. **[new]** `home-section:…` shows a grid with a working "Show all" and an append tail.
33. **[new]** `module:…` opens a seeded module page, and its watch page shows a poster.

**Playback and its dependents**

34. **[new]** Press Play on any row: the player bar leaves "Nothing playing", the position advances, the deck machine
    runs, the queue panel shows 1 + 3 + 8 rows with the last three marked autoplay, and SMTC / the taskbar / the jump
    list publish. 0.2.9 shows a "choose a remote device" toast and nothing else.
35. **[new]** With something playing, the lyrics rail shows the 40-line word-synced document, the wipe tracks the
    position, and scrolling away offers the resync pill.
36. **[new]** The right rail's NPV shows About-the-artist, top cities, credits and merch.
37. **[new]** The device picker shows a static roster with one entry marked active.
38. **[live]** A Connect transfer in either direction. Not verifiable under `--fake`; plan §8.4.
39. **[live]** A dealer push mutating an open playlist. Plan §8.4.

**Determinism (§7.2)**

39a. **[new]** Before item 40, put the settings store at a known state — delete `%LOCALAPPDATA%\Wavee` (unpackaged)
    or the package's `LocalCache` — and record that you did. `--fake` reads the REAL store (`Services.cs:593`):
    theme, `sidebar-layout.json`, the home-layout document and the `SavedLibrary` set all persist, and the 166-uri
    liked seed is applied **only** when `SavedLibrary` is empty (`:600-604`). Without this step item 40 compares
    launch 2 with launch 3 and silently passes (§0.14, §7.2 rule E).
40. Launch `--fake` twice. Screenshot frame 1 of `home` (with the network disconnected, so the CDN art is the
    placeholder in both), `album:…al2`, `pl:…pl4`, `liked`, `local`, `show:wavee:show:3` and the sidebar. Each pair is
    pixel-identical.
41. Repeat for `artist:…ar3` and `disco:0:…ar3`. **[new]** — the pairs differ on 0.2.9 (§2 W15).
42. Open Sidebar customize ▸ preview a Grid-presentation template ▸ confirm ▸ cancel ▸ repeat. The miniature is
    identical both times, and its grid-strip cells carry covers (indices 2 and 7).
43. **[new]** Move the system date forward by one day and repeat item 40. Each pair is still pixel-identical; on 0.2.9
    every date shifts.
44. **[new]** Run `--fake --live-clock`: the dated fixtures now read relative to today, and everything else is
    unchanged.

**The seed's boundaries**

45. Settings ▸ Playback shows the honest "no playback session" and "This build has no playback-module host (the fake
    backend)." arms — a sentence, never an empty card, never a skeleton.
46. Settings ▸ Storage census and Metadata stats complete (they read the real filesystem and the real store).
47. `WAVEE_FAKE_CHALLENGE=1` forces the login takeover with the canned pairing card (`WZY5-Q6TX`). **[new]** In 0.3
    the same state is reached by `--fake --challenge` and the expiry is `now0 + 872 s`, not `UtcNow + 872 s`
    (§0.15) — so two launches produce the same countdown, and no behaviour hangs off an environment variable.
48. Settings ▸ Developer ▸ Simulate update walks Checking → Available → Downloading → Installing → Completed, with a
    sticky progress toast and a notification row.
49. ♥ a track, restart `--fake`: it is still liked (the settings-store outbox).
50. Create a playlist in-session: it appears in the sidebar, opens as an **empty owned** playlist with the
    recommendations arm, and is gone after a restart (session-only by design).
51. **[new]** Launch `--fake --seed-shape=cold`: every page shows its skeleton arm and holds it (DATA GAP 9).
52. **[new]** Launch `--fake --seed-shape=failing`: `album:`, `artist:`, `home` and `search` each show their error
    state with a working Retry.
53. Delete `assets/covers` from the publish folder and launch: every synthesized surface shows the neutral
    placeholder tile and **nothing crashes** (the seed interns paths; it does not read files).
54. With the network disconnected, `home` renders every module with placeholder art and no wash legs — the page is
    complete, only uncoloured. With the network connected, the same page shows real CDN art and three wash legs.
    The disconnected run must not stall: `home.json` alone references **≈ 306 image urls across ten hosts** (§0.7),
    so every one of them has to fail fast, not hold a decode slot.
55. Open a 40+ row playlist and watch the table land: rows arrive in pages of 25 about 120 ms apart
    (`SpotifyExportSource.cs:101-108`). Ch 04 W5-W9's partial-table arms are therefore verifiable offline TODAY —
    the only progressive state `--fake` has (§7.4 I). **[new]** 0.3 keeps a comparable cadence behind
    `--seed-shape=cold` rather than losing it to an instant commit.
56. **[new]** Switch the app language and reopen `home`: every module title changes, including the synthetic
    QuickGrid row (0.2.9 leaves that one in English, §0.10g).

---

## 11. Audit log

First pass, **2026-09-12**. Written from the 0.2.9 sources at HEAD `7cef3a0e`, read back at `file:line`, plus
sections 2 and 7 of all 30 sibling chapters in `docs/plans/wavee/wavee-0.3-ui/`. Four facts were re-derived from the
bundled assets rather than trusted from a sibling chapter (`home.json`'s 31 sections / 107 items / greeting;
`playlists.json`'s 24 items and `LikedCount = 166`; the 170 remote image urls; the cover and export byte counts).
**This chapter was written without launching either build** (the task forbade it), so §10 is a procedure derived from
code, not an executed run — see item 12.

| # | kind | section | note |
|---|---|---|---|
| 1 | **wrong → corrected** | §0.10a, §2 W7 | Ch 29 §7.1 and W17 record `pl:…` as LOADED from "`FakeData.Playlist(i)` + export `playlists.json`". `FakeData.Playlist` is **never served** for a `spotify:playlist:pl{i}` uri: `SpotifyExportSource.Owns` claims every `spotify:*` (`:20`), it is registered first (`Services.cs:615`), and `AggregateCatalog` takes the first owner (`AggregateCatalog.cs:35-38`). The page gets the generic `"Playlist"` / `"Spotify"` / no-cover arm (`SpotifyExportSource.cs:33`), and `DetailHeaderMergeRules.ResolveTitle` gives the loaded title precedence for a non-rolling container (`:30-31`). Recorded as a defect plus parity 22. |
| 2 | **wrong → corrected** | §0.10b, §2 W6 | Ch 29 §7.1 records the artist discography as "✔ `Discography` `:114-130`" — hundreds per facet. Neither fake source overrides `GetDiscographyAsync`, so both take the interface default (`ICatalogSource.cs:51-60`), which slices the artist's **six** in-memory albums: Albums 2 / Singles 3 / Compilations 1. `FakeData.Discography` has **zero** call sites. Ch 08 §7's virtualization requirement is therefore unexercised offline. Parity 20. |
| 3 | **missing → added** | §0.11 | Ch 29 found **two** unreachable fixtures (`Lyrics`, `DefaultQueue`). There are **five**: add `SearchSeed` (`:611`), `Discography` (`:112-128`) and `UserPlaylists()` (`:519-521`). Four of the five are fixtures §7.1 asks for. |
| 4 | **missing → added** | §0.4, §7.2 rule A, §2 W15 | **The determinism defect ch 26's rule exists to prevent is already live.** `FakeData.cs:147` seeds every artist facet from `Math.Abs(s.Artist.GetHashCode())`; .NET Core randomises string hashing per process, so monthly listeners, followers, verified, world rank and the presence of six optional sections change between launches. Ch 29's parity item 89 ("relaunch twice … pixel-identical") passes only because it omits artist pages. It is also a latent `OverflowException` (`Math.Abs(int.MinValue)`). Parity 41. |
| 5 | **missing → added** | §7.3, §2 W16 | **Seven wall-clock reads** (`:176`, `:208`, `:213`, `:328`, `:331`, `:410`, `:417`) make every dated fixture drift between days. Ch 30 §7.3 item 5 noticed the *absence* of two clock fixtures; nobody noticed that the seven that exist are unpinnable. `SeedFake(now0)` plus `Platform.Clock.SeedEpoch` is the fix. Parity 43-44. |
| 6 | **wrong → corrected** | §3.1, §2 W8 | Ch 29 W17 records the liked stats as "13/12/**161**/7". Two different numbers are live: the Liked **page** renders `max(1, export.LikedCount)` = **166** (`SpotifyExportSource.cs:140`; `playlists.json`'s PseudoPlaylist `count: 166`) and the sidebar badge sums to 166, while `LibraryStats()`'s literal 161 (`:477`) reaches only the customizer miniature and `ContextTracks` (`:466`). So the Liked page and its own play context disagree by five rows. Recorded as defect §0.10d plus the §8 agreement test. |
| 7 | **missing → added** | §0.10c, parity 24 | `LibraryStats().Podcasts` is **7** (`:477`) while `ShowSeed` has **8** (`:374-379`): the sidebar badge and the Podcasts page disagree by one. |
| 8 | **missing → added** | §0.7, §4, parity 54 | **`--fake` is not offline for artwork.** `home.json` carries 166 `i.scdn.co` + 3 `misc.scdn.co` + 1 `newjams-images.scdn.co` urls and `playlists.json` 37 more; only the synthesized surfaces use the 16 bundled jpgs. No chapter's parity procedure says which condition its Home screenshot was taken under, which makes every Home colour item ambiguous. |
| 9 | **missing → added** | §7.4 note, DATA GAP 6, parity 34-37 | **"Something playing" is not a live-only state.** Ch 29 §7.1 files it under "✖ the largest single gap" without saying whether it is fakeable. It is: a silent sink in `Playback.Audio` plus a seeded context lights ten surfaces (player bar, rail, NPV, deck, lyrics, queue, stage, SMTC, taskbar, jump list) with no network. This is the highest-value single item in the chapter and it needs a Wave-3 seam (GAP 6) — i.e. a decision before Wave 5. |
| 10 | **missing → added** | §7.4 H/I, DATA GAP 9, parity 51-52 | **`--fake` cannot produce a skeleton, a partial edge or a failure.** Every fake source is complete-at-construction (`FakeSource.cs:25-27`, `SpotifyExportSource.cs:40-42`), so ch 03 W9-W10, ch 04 W5-W9, ch 08 W23, ch 10 W11, ch 13 W11 and ch 15 W24 are unreachable offline — six chapters' loading and error wireframes. Proposed `SeedShape` arms. |
| 11 | **missing → added** | §9.2, §9.3 | **The seed's schedule is wrong in the plan.** It is assigned to Wave 5 owner Q as a parenthetical, in the same wave whose gate depends on it, paired with a file ch 21 §9 wants moved to Wave 4. Splitting it into SEED-CORE (Wave 1) and SEED-SURFACES (Wave 5) also removes plan §7's "the app is dark for Waves 1-4": a populated table set exists from Wave 1 onward. |
| 12 | **procedure, not a run** | §10 | §10 was derived from code only. Two items in particular need an executed run to settle: **24** (whether Library ▸ Podcasts really lists 8 while the badge says 7 — the two code paths are independent) and **44** (whether `LibrarySearch`'s corpus is reachable at all on the fake backend, marked UNVERIFIED in §7.1 item 44). |
| 13 | **index correction** | header | Ch 29's index row — `| the `--fake` fixture inventory | **29** |` in its §9 index table — should read **31**. Ch 29 §7.1 remains a correct summary; where the two differ, items 1, 2, 3, 6 and 7 above are the `file:line` that settles it. |
| 14 | **UNVERIFIED → two of three SETTLED** | §3, §7.1 items 34, 44 | The first pass marked three facts UNVERIFIED. Two are now measured from the assets themselves: the covers are **600 × 600 JPEG** (all sixteen, read from the SOF headers; `liked-songs-300.png` is 300 × 300), which makes the artist hero's `Cover(i, 640)` an upscale; and `home.json` contains **14** `header_image_url_desktop` values, so ch 11 W4's header-photo hero IS reachable offline. The third — whether the fake backend reaches `LibrarySearchIndex` — stays UNVERIFIED and still needs a run. |
| 15 | **missing → added** | §3.1, §7.1 item 48 | `FakeSpotifySession` supplies a display name and a premium tier but **`AvatarUrl` is null** (`FakeSpotifySession.cs:21` — the first pass cited `:100` in a 34-line file, see row 16), so ch 18 W17's profile chip and ch 19 W4-W6's profile menu never render their portrait arm offline. No chapter had noticed. |

**Second pass, adversarial audit, 2026-09-12.** Re-read every `file:line` in the header blockquote against HEAD
`7cef3a0e`, plus `SpotifyHomeComposer.cs`, `ICatalogSource.cs`, `AggregateCatalog.cs`, `DetailHeaderMergeRules.cs`,
`WaveeNavProbe.cs` and `WaveeApp.cs`, and re-derived every asset number by parsing `home.json` / `playlists.json` and
the JPEG/PNG headers directly. Sixteen corrections; the chapter above carries all of them.

| # | kind | section | note |
|---|---|---|---|
| 16 | **wrong → corrected** | header, §1.1, §2 W10, W12, §3.1 | **Two source files were cited at line numbers they do not have.** `FakeSpotifySession.cs` is **34 lines** and was cited at `:87`, `:96-103`, `:99`, `:100`, `:101`; the real members are `Status`/`Owns`/`Capabilities` at `:8-15`, the 250 ms connect + user at `:19-22`, `Set` at `:33`. `FakePodcastSource.cs` is **21 lines** and was cited at `:126`, `:130-133`, `:132`; the real members are `Owns` `:10`, `GetShowsAsync` `:13`, `GetShowAsync` `:17-20` (the `Total == Paged` rewrite is `:19`). Every occurrence is repaired. |
| 17 | **wrong → corrected** | §0.10e, §1.1, §2 W7, W14, §3.1, §6, §7.1 item 41 | **"22 exported playlists" is 21.** `LoadLibrary` skips the `Folder` (`SpotifyExport.cs:70`) *and* any `format == "listen-later"` playlist (`:73`); `playlists.json`'s 22 `Playlist` items include "Your Episodes" (`spotify:playlist:37i9dQZF1FgnTBfUlzkeKt`, `listen-later`). `_summaries` / `_headers` hold 21, so "22 real headers with synthesized bodies" is 20 once Iced Americano is removed. |
| 18 | **missing → added** | §0.10f, §2 W7, W13, §7.1 items 19, 43 | **The `--fake` sidebar is 26 top-level rows, and six names appear twice.** `AggregateCatalog.GetPlaylistTreeAsync` CONCATENATES every catalog source's tree (`:119-124`); `SpotifyExportSource` does not override it, so the interface default flattens its 21 summaries (`ICatalogSource.cs:88-89`) and `FakeSource`'s synthetic tree (`FakeSource.cs:53-54`) is appended below. The synthetic `PlaylistSeed` names were copied from the export, so "Dalkom Cafe", "Iced Americano", "우울해", "My Playlist #6", "Nostalgia 2000s Mix" and "Henry Moodie Mix" each exist twice under two different uris — one opening a correct header, the other the generic arm of §0.10a. W13 drew only the 5 synthetic nodes and called it "the tree". |
| 19 | **missing → added** | §0.10g, §2 W1, §8, §10 items 5, 56 | **Home renders "Jump back in" twice, and one of the two is never localized.** `Compose` prepends a synthetic `QuickGrid` of 9 library summaries titled `t.JumpBackIn` (`SpotifyHomeComposer.cs:28`, `:40-46`) and inserts the Spotlight hero at index 1 (`:122-124`); `home.json` section 4 is the server's own "Jump back in". `SpotifyExport.LoadHome` calls `Compose(home, _summaries)` with no titles record (`:123`), so the synthetic row takes `HomeModuleTitles.Default`'s hard-coded English rather than the loc-resolved `HomeModuleCopy.Titles` the live path passes. W1 drew neither the QuickGrid nor the order. |
| 20 | **wrong → corrected** | §2 W3 | **The album wireframe's entire track table was computed at offset 0 instead of 20.** `al2` is `AlbumTracks(12, 20)`, so row 1 is "Dalkom Cafe" · 67 750 000 plays · 175 bpm · 9B · 4:38 and the header reads 12 songs · **43 min** (Σ = 2 628 000 ms) — not "우울해" · 60 000 000 · 96 · 1B · 2:18 · 38 min. The `+7 750 000` floor is a function of the offset (`:96`), so no fake album's last row is ever small. |
| 21 | **wrong → corrected** | §2 W8 | **The Liked wireframe's rows were the offset-0 list too.** `LikedSongs(166) = Tracks(166, 1000)` (`:339`), so row 1 is Seed[1000 % 16 = 8] = "Breathe" (3:58) and row 2 "Lebanese Blonde" (4:35), not "LAST GOODBYE" / "mellow pop wistful…". Also recorded: `Track(i)`'s `AlbumRef` takes the track's own title (`:41`), so the ALBUM column repeats the TITLE column on every synthesized row. |
| 22 | **wrong → corrected** | §2 W12 | **Show 3's episode durations were invented.** `(22 + (showIdx·7 + i·13) % 50)` for `showIdx = 3` gives **43 / 56 / 69** min, not 44 / 38 / 51; no `(idx, i)` pair produces the printed numbers. `n = 11` and the three titles (#11 Tape Loops, #10 The Quiet Release, #9 Edge Cases) were correct. |
| 23 | **wrong → corrected** | §2 W7 | `PlaylistSummary(4)`'s cover is `Cover(4 + 100, 300)` (`:495`) ⇒ `Wrap(104, 16) = 8` ⇒ **cover08.jpg**, not cover04. |
| 24 | **wrong → corrected** | §0.7, §3.1, §4, §10 item 54 | **The remote-url count was one host family, not the document.** `home.json` carries ≈ **306** image urls across **ten** hosts (`i.scdn.co` 166, `pickasso.spotifycdn.com` 65, `image-cdn-fa` 32, `image-cdn-ak` 18, `seed-mix-image` 10, `daylist.spotifycdn.com` 8, `misc.scdn.co` 3, `lexicon-assets` 2, `newjams-images` 1, `shareables.spotify.com` 1), and `playlists.json` ≈ **63** across nine — not "166 + 3 + 1" and "37". |
| 25 | **wrong → corrected** | §2 W1, W14, W16, §5, §7.1 item 7 | **The daylist card already exists in the fixture.** `home.json` and `playlists.json` each carry one playlist with `"format": "daylist"`, and `ModuleForFormat` maps it to `HomeGroupKind.Hero` (`SpotifyHomeComposer.cs:232`). The gap is `ExpiresAt` / `CreatedAt` — the fields `DetailHeaderMergeRules.IsRollingIdentity` reads (`DetailPage.cs:124`) and ch 11's flip clock counts against — so the row is ◑, not ✖. A consequence worth stating: with no `ExpiresAtMs` anywhere in `--fake`, `IsRollingIdentity` is **always false** offline and the rolling-header arm is unreachable. |
| 26 | **wrong → corrected** | §3.1, §2 W6 | **Four formula ranges over-claimed their ceiling.** Monthly listeners top out at **29 990 000** (31·940 000 + 850 000), not 30 010 000; local durations at **280 000 ms**, not 289 000 (only 14 local tracks exist, so `i·41 % 140` attains 130); discography Albums at **97** and Singles at **698**, not 99 / 699, because `seed` is one of sixteen values (`:116`). |
| 27 | **missing → added** | §0.14, §7.2 rule E, §10 item 39a | **Determinism has a second half nobody wrote down.** `CreateFake` opens the REAL settings store (`Services.cs:593`); the theme, `sidebar-layout.json`, the home-layout document and the `SavedLibrary` set all survive a relaunch, and the 166-uri liked seed is applied **only** when `SavedLibrary` is empty (`:600-604`). "Two launches are pixel-identical" is a claim about `SeedFake` **plus a known store state**, and the procedure never said to establish one. |
| 28 | **missing → added** | §0.15, §6, §10 item 47 | **One `--fake` fixture is selected by an environment variable**, which CLAUDE.md's working rules forbid: `WAVEE_FAKE_CHALLENGE` (`WaveeApp.cs:276`, `:323`, `:343`, `:388`). Its expiry is also a live `DateTimeOffset.UtcNow` read — an eighth wall-clock read the §7.3 list missed. Both become `Platform.Args` + `now0` in 0.3. |
| 29 | **missing → added** | §5, §7.4 I, §10 item 55 | **`--fake` has three timed behaviours and §5 listed none of them.** The 250 ms auth flip (`FakeSpotifySession.cs:19-22`), the **120 ms per 25-track page** in `StreamTracksAsync` (`SpotifyExportSource.cs:101-108`) and the update simulator's 600 / 1 500 / step / 900 ms walk (`FakeAppUpdateService.cs:30-33`, `:70-115`). The second one partly falsifies §7.4 I: ch 04 W5-W9's partial-table arms ARE reachable offline today — 166 liked rows land over ≈ 840 ms. |
| 30 | **missing → added** | §1.1 | The Null-seat list is **thirteen** entries, not twelve — the chapter said "twelve" while printing thirteen names (`Services.cs:320-325` is six, `:358-364` is seven). |
| 31 | **missing → added** | §2 W1, §7.1 items 19, 34, 41, §8 | **Four fixtures the chapter had no row for.** (a) `homeChips`: `home.json` carries 3 (Music / Podcasts / Audiobooks, each with a "Following" subChip) and `Compose` publishes them (`:126`, `:241-248`) — Home's chip rail is populated offline. (b) `header_image_url_desktop` occurs 14×. (c) The export's i18n fixtures: a Dutch, emoji-bearing section title ("Geniet van de zaterdag ☕"), a playlist "Summer 2016 vibes" with three emoji, owned by "#Catherine" with one more — the grapheme-cluster and long-label evidence chs 00/01/06 ask for. (d) `SpotifyHomeComposer`'s classification rules (`ModuleForFormat`, `EmitSectionGroups`, `RecentsTotal`) are pure, untested, and the reason a fixture becomes a module row; they belong in §8. |
| 32 | **wrong → corrected** | §9.2, §2 W6, §3 | Three smaller corrections. The nav-probe row claimed the list "omits browse" — `CheapRoutes` (`:44-48`) includes **`browse` and `local`**; the real lists are `:34-43` and `:44-48`, six of the eight heavy entries are the same route with a different id, and five of them are the `pl{i}` uris §0.10a shows render the generic arm. The 2/3/1 discography split reaches the screen through **two** independent paths — `ICatalogSource`'s default and `ArtistPage.cs:202-203`'s own `Where` — so fixing one would not fix the page. And the covers being 600 × 600 makes `Cover(i, 640)` (`:162`) an upscale on every fake artist hero. |

**answers 2026-09-12: Q7 (plan §9.6) reaches this chapter once.** `api-console` is struck from the §2 readiness list — the API console and its four `ApiDebug*` helpers are DELETED, so `--fake` needs no route and no seed for it; the line is kept struck with the reason and date. No fixture, wireframe or parity item changed.
