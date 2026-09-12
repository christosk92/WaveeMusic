# Index — the Wavee 0.3 visual fidelity contract

> **What this is.** The 32 chapters in `docs/plans/wavee/wavee-0.3-ui/` (`00-design-system.md` … `31-fake-data.md`,
> **56,135 lines** as of 2026-09-12, excluding this index and the merged sweep's tombstone) specify, surface by
> surface, what 0.2.9 looks like and what 0.3 must reproduce. This file is the map: one row per user-visible surface,
> so a dropped surface is visible **before** Wave 5 opens rather than in a side-by-side at the end.
> **Status: all 32 chapters are complete.** Every one carries §0-§11, a §9 line estimate and a §10 parity checklist
> (00, 15 and 29 also carry a §12), so **nothing in this index reads `UNVERIFIED` any more**.
> `29-cross-cutting.md` landed at **3,801 lines** — 28 wireframes, 123 parity items, 48 audit entries, the longest
> checklist of the set; `30-appearance-preferences.md` was re-cut onto the standard twelve sections and gained §3,
> §4, a §5 motion table and a §11 audit log (its thirteen per-preference state blocks now carry W-numbers, so its §2
> holds 29 wireframes rather than 16). `90-coverage-gaps-1.md` was **merged into `28-setup-whatsnew-feedback.md` on
> 2026-09-12** and is now a 5-line tombstone — not a memo, and still not a chapter (§7).
> **Consistency pass, 2026-09-12 (post-arbitration), as of 2026-09-12.** `wavee-0.3-implementation.md` was rewritten
> to settle 18 arbitrations (its §9.4) and carry the final 137-file tree (§2; **136 after §9.6 Q7, 2026-09-12**) and wave assignments (§5); this index
> was written before that rewrite, so its tables still described as PROPOSED, UNOWNED or UNRESOLVED a long list of
> homes the plan has since settled. **This pass reconciles the whole index against that settled plan**: every row in
> §2.1-§2.8 now carries the plan's settled file, owner and wave (dropping PROPOSED / "not in §2" / "UNOWNED" /
> "UNRESOLVED" marks wherever the plan's tree, §5 waves or §9.4 arbitrations gave the surface a home — the shared
> detail frame and the track surface into Wave 4.5, history/recents/discography/the sidebar customizer/the action
> platform/the drag layer/the context-menu grammar/the Home partials/the Liked partials/the track filter-sort-density
> and credits rows among them); §5 and §6 are rewritten as settled records rather than open lists; §3 and §4 are kept
> as the 2026-09-11 historical count the plan cites. The chapter line count above (56,135) was recounted by `wc -l`
> after every chapter's own correction and agrees with `wavee-0.3-implementation.md` §9.2. A handful of homes the
> plan itself leaves open for Christos (§9.6) are kept marked open here too — see §5's closing rows and §9.6 there.
> **Answers, 2026-09-12 (later the same day).** Christos decided all nine of §9.6's questions; the "137-file tree"
> cited above is the pre-answers figure — Q7 deletes the API console and its four helpers outright, taking the tree
> to **136 files / 170,015 lines** (§6 row 17, §8). Every row this pass touches is marked inline with the date; see
> §2.1, §4, §5, §6 row 17 and §8. This pass also adds real prose to twelve chapters (their own §11 audit logs, plus
> Q7's spillover into a few more), so the **56,135** chapter-lines-on-disk figure above is now **56,264** (§8).

---

## 1. How to use this

A **Wave 4/5/6 owner reads the chapters for the files they own, end to end, before writing a line** — not the plan's
§4 code sketches, which sit an altitude above the surface and are wrong in the specific ways each chapter's §9
records. Start at the surface table (§2): find every row whose "0.3 file(s)" column names a file you own, then read
those chapters' §0 (non-negotiables), §2 (wireframes), §3-§6 (tokens, colour, motion, interaction) and §9 (re-author
notes) before you open an editor, and §8 (pure rules to port verbatim) before you re-derive anything. Then check §3
of this index: your file's honest line estimate is on average **2.4× the plan §2 budget you were handed**, and the
plan's own rule — "a file that passes its budget by 30% gets a named partial, not a second type" — means you name
the partials on day one rather than discovering the overrun mid-wave. **A page is not done when it renders. It is
done when its chapter's §10 parity checklist passes item by item against the kept 0.2.9 Release build**
(`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`), side by side, at the
window sizes each item names — **2,650 items across 32 checklists**, each one a decision somebody already paid for.
Where a chapter and the plan disagree, the chapter wins on *what the surface looks like* and the plan wins on *where
the code lives* — settled for every surface and every contradiction as of 2026-09-12: §5 below is now the closed
record of who owns what was unowned, and §6 is the closed record of how each cross-chapter disagreement resolved.
An owner who finds a chapter still describing its own file, owner or wave as open should read that chapter's header
as stale and follow §2/§5/§6 here (and `wavee-0.3-implementation.md` §9.4) instead — not re-litigate it.

The wireframe and parity counts in the table are **per chapter**, not per surface: a chapter's 17-34 wireframes and
56-123 parity items cover every row that cites it. Where a surface has no number of its own, that is said rather
than split by guesswork.

---

## 2. THE TABLE — every user-visible surface

Route keys are the real ones, from `src/apps/Wavee/Features/Shell/ShellRoutes.cs` (`s_exact` :26-44, `s_prefixes`
:48-60, plus `ConcertRoutes.TryParse` :22-43) and `ContentHost.PageFor` (`ContentHost.cs:188-311`). "0.2.9 source
lines" is the chapter's own header count. **PROPOSED** marks a file a chapter invents because plan §2's tree has
none. Every row cites the chapter it came from.

### 2.1 Routed pages

| Surface | Route key | Chapter | 0.2.9 source lines | 0.3 file(s) | Wave | Owner | Wireframes | Parity items |
|---|---|---|--:|---|---|---|--:|--:|
| Home landing (feed, facets, hero, wash, readiness) | `home` | 10 | 3,544 | `Entities/Home.cs`, `Home.UI.cs`, `Home.Page.cs` | 5 | P | 20 | 81 |
| Home cards, module shells, Fold tile, timeline, artist podium | `home` | 11 | 3,175 | **settled (A15):** `Home.UI.cs` + `+Home.Cards.UI.cs` + `+Home.Artists.UI.cs` (named partials, in §2 now) + `Home.cs` CORE | 5 | P | 34 | 85 |
| Home section drill-in | `home-section:<uri>` | 12 | 628 + 119 + 104 + 42 | `Home.Page.cs` (`Home.SectionPage`) | 5 | P | 21 | 90 |
| Browse section drill-in (same page class) | `browse-section:<uri>` | 12 | (shared with the row above) | `Home.Page.cs` | 5 | P | 21 | 90 |
| Home customizer (full page) | `home-customize` | 12 | 310 + 157 + 244 + 271 (Core) | **settled (A15):** `Entities/Home.Customizer.cs` (300: the page + its model, reducer and commands) + `Home.Host.cs` (400: `home-layout.json` off the UI thread) | 5 | P | 21 | 90 |
| Browse directory | `browse` | 13 | 363 + 67 + 54 + 96 (of 4,502) | `Entities/Browse.UI.cs` + **settled** `Browse.Page.cs` (700: `BrowsePage` + `BrowseDirectoryPage` + `BrowsePageHost`) | 5 | P | 22 | 103 |
| Browse category page | `browse:<uri>` | 13 | 730 + 60 + 64 | `Browse.UI.cs` + **settled** `Browse.Page.cs` (above) | 5 | P | 22 | 103 |
| Search (hero, chips, genre tiles, facet grid, rows) | `search` | 13 | 1,151 + 156 + 180 + 165 + 138 + 28 + 11 | `Entities/Search.cs`, `Search.UI.cs`, `Search.Page.cs` | 5 | P | 22 | 103 |
| Library — Albums | `albums` | 15 | 1,809 (shared with the two rows below) | **settled (A3):** `User.UI.cs`, `User.Page.Library.cs` | 5 | O | 24 | 92 |
| Library — Artists | `artists` | 15 | (shared) | **settled (A3):** `User.UI.cs`, `User.Page.Library.cs` | 5 | O | 24 | 92 |
| Library — Podcasts | `podcasts` | 15 | (shared) | **settled (A3):** `User.UI.cs`, `User.Page.Library.cs` | 5 | O | 24 | 92 |
| Liked Songs (cover treatments, facts bento, filter chips) | `liked` | 07 (+03 frame, +04 table) | 5,121 | **settled (A3):** `User.UI.cs`, `User.Page.Liked.cs`, `User.Liked.cs`, `User.Facts.cs`, `User.Facts.UI.cs`, `User.Cover.cs` | 5 | O | 24 | 96 |
| Local files | `local` | 15 (entry point) + 06 (body) + 03 (frame) | 85 + 165 + 115, plus the frame | **settled (plan §9.5):** `Entities/Playlist.Page.cs` — its `wavee:local:all` `DetailKind.Playlist` arm | 5 | O | 24 | 92 |
| Navigation history | `history` | 16 | 616 (of which `HistoryStore` 146) | **settled (plan §9.5):** a `Shell.History` section in `Shell.cs` + `history.json`/`play-log.json`/`play-recency.json` in `Shell.Host.cs` + the page in `+Shell.History.UI.cs` | 4 | I | 25 | 83 |
| Recently played | `recents` | 16 | 2,499 + 687 | **settled — in §2:** `Entities/Recents.cs`, `Recents.UI.cs`, `Recents.Page.cs` | 5 | P | 25 | 83 |
| Settings — General / Appearance / Playback / Notifications / Storage / About / Logs | `settings` | 27 (+30 for what each preference *does*) | ≈7,515 | `Screens/Settings.cs`, `Settings.UI.cs`, `Settings.Host.cs` | 6 | R | 29 | 79 |
| ~~API console (developer mode only)~~ | ~~`api-console`~~ | 27 | 329 (+ ≈1,400 helper lines) | **DELETED, 2026-09-12 (plan §9.6 Q7): no 0.3 file, no route, no owner, no wave.** Row kept struck through as the record of what 0.2.9 painted; not removed. | — | — | 29 | 79 |
| Playback runtime diagnostics | `playback-diagnostics` | 27 | 454 | `Screens/Diagnostics.UI.cs` | 6 | S | 29 | 79 |
| Connect diagnostics | `connect-diagnostics` — **renderable but NOT in `ShellRoutes.s_exact`** (ch. 27 §9.6); **fix decided, plan §9.6 Q6, 2026-09-12 — its GitHub issue is drafted, awaiting Christos's approval, no number yet** | 27 | 196 | `Screens/Diagnostics.UI.cs` (`Diagnostics.ConnectPage`) | 6 | S | 29 | 79 |
| What's new / release notes | `whatsnew` (keyed by arg) | 28 | 2,156 + 1,882 (Core) + 543 (store) | `Screens/ReleaseNotes.cs`, `ReleaseNotes.UI.cs`, `ReleaseNotes.Host.cs` | 6 | R | 26 | 90 |
| Sidebar customizer (full page) | `sidebar-customize` | 26 | 4,247 (`Curated/**`) | **settled (A4):** `Shell/Sidebar.Customizer.UI.cs` — sequenced **last** in Wave 4, over the live `Sidebar.Doc.cs` document | 4 | J | 18 | 99 |
| Album | `album:<uri>` | 05 (+03, 04, 01) | ≈2,700 album-owned of the detail set | `Entities/Album.cs`, `Album.UI.cs`, `Album.Page.cs` | 5 | M | 20 | 74 |
| Prerelease (resolves into the album surface) | `prerelease:<uri>` | 05 | 43 + 127 + the kind-138 branches | **settled (plan §9.5):** `Album.Page.cs` — no page class of its own; the whole prerelease surface (route, kind-138 resolve, countdown card, pre-save heart swap, greyed pending rows, "N of M songs" tile) folds in here | 5 | M | 20 | 74 |
| Playlist (incl. radio / mix / blend) | `pl:<uri>` | 06 (+03, 04) | ≈3,000 playlist-owned | `Entities/Playlist.UI.cs`, `Playlist.Page.cs` | 5 | O | 28 | 72 |
| Artist | `artist:<uri>` | 08 | 4,215 artist-owned | `Entities/Artist.cs`, `Artist.UI.cs`, `Artist.Page.cs` | 5 | N | 29 | 93 |
| Discography (facet page, virtual grid, album drawer, era bands) | `disco:<uri>` | 08 | 174 + 218 + 48 + 879 (drawer) | **settled (plan §9.5):** `Entities/Artist.Discography.cs` (~1,300) — `disco:` is a real route, in the route table | 5 | N | 29 | 93 |
| Show / podcast | `show:<uri>` | 09 (+03 frame) | 260 (`EpisodeList`) + the show branches | `Entities/Show.UI.cs`, `Show.Page.cs`, `Episode.UI.cs` | 5 | M | 21 | 83 |
| Module page / watch page | `module:<uri>` | 09 | 776 + 402 + 292 + 312 (Sdk) | `Platform/Modules.UI.cs` (+ `Modules.cs` CORE) | 6 | T | 21 | 83 |
| Concerts hub | `concerts` | 17 | 536 (of 4,735) | `Entities/Concert.Page.cs` | 5 | N | 23 | 71 |
| Artist concert schedule | `artist-concerts:<id>` | 17 | 321 | `Concert.Page.cs` | 5 | N | 23 | 71 |
| Concert detail | `concert:<id>` | 17 | 387 + 97 | `Concert.Page.cs` | 5 | N | 23 | 71 |
| Not-found page (any unclaimed key) | *fall-through* | 18 (W21) | ≈20 in `ContentHost.cs:293-311` | `Shell/Shell.UI.cs` | 4 | I | 26 | 89 |

### 2.2 Shared page machinery — no route of its own, on screen for many rows above

| Surface | Route key | Chapter | 0.2.9 source lines | 0.3 file(s) | Wave | Owner | Wireframes | Parity items |
|---|---|---|--:|---|---|---|--:|--:|
| Shared detail frame (hero, context band, rail, trailing, skeleton, reveal) | `album:` `pl:` `liked` `local` `show:` `prerelease:` | 03 | ≈6,400 | **settled (A1):** `Entities/Detail.cs` (CORE) + `Detail.UI.cs` (UI) — `ContextBand`/`ContextBandLayout` live here, not in `Shell.UI.cs` or `Platform/Design.cs` | **4.5** — plan §5 now names this wave | M | 27 | 67 |
| Detail track table (chrome, command bar, header, tiers, list arms, recs, filters) | `album:` `pl:` `liked` `local` | 04 (+01) | 6,137 primary | **settled (A2):** `Entities/Track.Table.cs` (2,600) + `Track.UI.cs` (row, 2,600) | 4.5 | M (O *configures* via `TableProfile`; O never edits it) | 20 | 72 |
| Track row, expanded drawer, equalizer, heart, menus, drag chip | every list surface | 01 | ≈5,400 | **settled (A2/A6):** `Track.cs`, `Track.UI.cs` (Wave 4.5), `Track.Drawer.cs` (Wave 5), `Platform/Controls.cs`, `Platform/Drag.cs` (both Wave 4) | 4.5 / 5 (M) / 4 (L) | M + L | 31 | 82 |
| Media cards, shelves, chips, stat tiles, countdowns, face piles, rich text | every page | 02 | 4,398 | `Platform/Controls.cs` + `+Controls.Art.cs` (A16 named partial) + `Card()` factories in each `Entities/X.UI.cs` | 4 + 5 | L + M/N/O/P | 29 | 82 |
| Design system (tokens, ramp, colour, cover palette, materials, motion, CTAs, zoom) | every surface | 00 | 4,276 | `Platform/Design.cs`, `Platform/Controls.cs` (A16 envelope for `Controls.*`) | 4 | L | 18 | 75 |

### 2.3 Shell chrome, sidebar and the player bar

| Surface | Route key | Chapter | 0.2.9 source lines | 0.3 file(s) | Wave | Owner | Wireframes | Parity items |
|---|---|---|--:|---|---|---|--:|--:|
| Window frame, merged chrome row, masthead band, tab strip, drill trail | all | 18 | 5,424 | `Shell/Shell.cs`, `Shell.UI.cs`, `Shell.Host.cs` | 4 | I | 26 | 89 |
| Omnibar + suggestion popup | all | 18 (chrome) + 13 (`OmnibarSuggestQuery`) | 617 (toolbar) + 138 | **settled:** `+Shell.Masthead.UI.cs` (the omnibar itself, I) + `Entities/Search.cs` (the suggestion contract, P) — plan §5 Wave 4 names this a cross-owner contract explicitly | 4 + 5 | I + P | 26 / 22 | 89 / 103 |
| Narrow drawer (viewport ≤ 720) | all | 18 (W6) | inside 5,424 | **settled — in §2:** `Shell.UI.cs` | 4 | I | 26 | 89 |
| Back / forward history flyout | all | 18 (W16) | inside 5,424 | `Shell.UI.cs` | 4 | I | 26 | 89 |
| Sidebar pane — Classic / Library V3 / Wavee Curated | all | 25 | 16,126 (+6,311 shared pure rules) | `Shell/Sidebar.cs`, `Sidebar.UI.cs`, `Sidebar.Host.cs` (A4) | 4 | J | 25 | 82 |
| Sidebar rail (collapsed), folder flyout, tree drag cues, multi-select | all | 25 | inside 16,126 | `Sidebar.UI.cs` | 4 | J | 25 | 82 |
| "Move to folder…" destination picker (`ContentDialog`) | all | 25 (W24) | 189 | `Sidebar.UI.cs` | 4 | J | 25 | 82 |
| Sidebar section options popover (`SidebarEditSession` + property panel) | pane + `sidebar-customize` | 26 | 164 + 981 | `Sidebar.UI.cs` | 4 | J | 18 | 99 |
| Sidebar data pipeline — projection / binder / planner / sources | pane | 26 | 7,161 (`Data/**`, incl. `Sources/` 850) | `Sidebar.cs` | 4 | J | 18 | 99 |
| `sidebar-layout.json` document, reducer, wire, store, migrations | — | 26 (+25) | 1,491 | **settled (A4):** `Sidebar.Doc.cs` (the document + reducer + wire + migrations + defaults) + `Sidebar.Host.cs` (the file I/O and debounce half) | 4 | J | 18 | 99 |
| Action + extension platform (registry, descriptor, targeting, icons) | every menu | 26 §9.3.9 + 29 | 946 + 227 + 87 = 1,260 | **settled (A7):** `Platform/Actions.cs` (CORE: descriptor, targeting, the one action table) + `Platform/Actions.UI.cs` (menus, icons) — landed before J's picker; `PinRowRule` goes the other way, into `Sidebar.cs` | 4 | I | 18 / 28 | 99 / 123 |
| Player bar + seek bar + responsive tiers | all | 20 | 2,230 | **settled:** `+Shell.PlayerBar.UI.cs` (named partial, in §2 now) + `Shell.cs` CORE (the `Shell.Ui` rail-state section) | 4 | I | 23 | 85 |
| Device picker menu | player bar | 20 | 88 + 162 | **settled:** `+Shell.PlayerBar.UI.cs` — reads the device roster from `Spotify.Connect`'s cluster, not a new store | 4 | I | 23 | 85 |
| Format split button | player bar + track drawer | 20 + 01 | 129 | `Platform/Controls.cs` / `Track.Drawer.cs` | 4 / 5 | L / M | 23 | 85 |

### 2.4 Right rail, now-playing, queue, stage, decks, lyrics, video

| Surface | Route key | Chapter | 0.2.9 source lines | 0.3 file(s) | Wave | Owner | Wireframes | Parity items |
|---|---|---|--:|---|---|---|--:|--:|
| Right rail frame — docked and floating | all | 21 | 458 | `Shell/Rail.UI.cs` | 4 | K | 17 | 92 |
| Now-playing view panel (hero tile, header row, meta) | rail | 21 | 566 + 112 | `Rail.UI.cs` | 4 | K | 17 | 92 |
| Player-style flyout + thumbnails + swatches | rail | 21 | 232 + 256 + 90 + 48 | **settled — in §2:** `+Rail.Styles.UI.cs` + `Shell/Rail.cs` CORE | 4 | K | 17 | 92 |
| Queue panel (rail arm + stage pane) | rail / stage | 21 | 936 + 149 + 80 | `Entities/Queue.UI.cs` (+ `Queue.cs` CORE) | 5 | Q | 17 | 92 |
| Friends panel | rail | 21 | 228 + 64 | **CONFIRMED, plan §9.6 Q5, 2026-09-12:** `Rail.UI.cs` (the panel, K, Wave 4); the friend edge + columns land with `Edges.cs` (`EdgeTable<FriendEdge> Friends`, B, Wave 1). No `Entities/Friends.cs` | 4 (panel) / 1 (edge) | K (UI) / B (edge) | 17 | 92 |
| Video rail panel | rail | 21 + 24 | 156 | `Rail.UI.cs` (body) / **settled (A13)** `Shell/Video.UI.cs` (1,450, surface) | 4 | K (both body and surface) | 17 / 27 | 92 / 75 |
| Artwork context menu | rail hero | 21 (W10) | inside 566 | `Rail.UI.cs` | 4 | K | 17 | 92 |
| Immersive stage — chrome, identity, panes, arm, ink | overlay | 21 (+22 for the lyrics pane) | 2,261 | **settled (A12)** `Shell/Stage.cs` (500) + `Shell/Stage.UI.cs` (1,150) | 4 | K — single owner, not split | 17 | 92 |
| Deck faces — 12 presets (record, turntable, Zune, cassette, reel, CD, MD, iPod, VU, Winamp, WMP, canvas) | rail NPV | 23 | 5,394 | **settled — in §2:** `Shell/Deck.cs`, `Deck.UI.cs`, `+Deck.Faces.cs` | 4 | K | 28 | 68 |
| Lyrics — rail arm (wipe, cascade, latch, resync pill, interlude) | rail | 22 | 3,160 + 177 | `Shell/Lyrics.cs`, `Lyrics.UI.cs`, `Lyrics.Host.cs` | 4 | K — ch. 22's ask for a 2nd owner on the backend half is not adopted; the plan keeps K as sole owner of all three files | 22 | 91 |
| Lyrics — immersive stage arm | overlay | 22 | 550 | **settled (A12): dropped.** `Shell/Lyrics.Stage.UI.cs` is not created; the immersive surface and the stage's lyrics column move into `Lyrics.UI.cs` (3,500) | 4 | K | 22 | 91 |
| NPV lyrics peek | rail ▸ Details | 22 (W20) | 177 | `Lyrics.UI.cs` | 4 | K | 22 | 91 |
| Lyrics inspector dialog (developer mode) | dialog | 22 (W17-W19b) + 27 | 564 | **settled (A14):** `Screens/Diagnostics.UI.cs` (+≈600) — ch. 27's table now carries the row | 6 | S | 22 | 91 |
| Docked video cap (Cap + PageStage faces) | rail / watch page | 24 | 521 | **settled (A13)** `Shell/Video.UI.cs` (1,450); `Playback/Playback.Video.cs` (owner H) stays decode/host only | 4 | K | 27 | 75 |
| In-window PiP (8 resize zones, drag, poster) | overlay | 24 | 538 | **settled (A13)** `Shell/Video.UI.cs` | 4 | K | 27 | 75 |
| Pop-out video window (own HWND, borderless fullscreen) | OS window | 24 | 259 | **settled (A13)** `Shell/Video.Host.cs` (250) | 4 | K | 27 | 75 |
| Video fullscreen surface (main window) | overlay | 24 | 336 | **settled (A13)** `Shell/Video.UI.cs` | 4 | K | 27 | 75 |
| Video placement menu + placement state machine | player-bar chevron | 24 | 77 + 543 (`PlacementCore`) + 203 (host) | **settled (A13)** `Shell/Video.cs` (900, CORE: `PlacementCore` + `PlacementState`) + `Video.UI.cs` (the menu) | 4 | K | 27 | 75 |
| Video override manager flyout | Settings ▸ Playback | 24 (body) + 27 (card) | 295 | **settled (A18-precedent)** named partial `Screens/+Settings.UI.Video.cs` (280, body) mounted by `Settings.UI.cs`'s Playback tab (card) | 4 (body) / 6 (mount) | K (body, Wave 4) / R (mounts it, Wave 6) | 27 / 29 | 75 / 79 |

### 2.5 Shell overlays, dialogs, toasts, menus

| Surface | Route key | Chapter | 0.2.9 source lines | 0.3 file(s) | Wave | Owner | Wireframes | Parity items |
|---|---|---|--:|---|---|---|--:|--:|
| Command palette (Ctrl+K) | overlay | 19 | 200 + 292 | `Shell/Shell.Palette.cs` | 4 | I | 32 | 87 |
| Profile chip + profile menu + `Play ▸` cascade | chrome | 19 (+18 W17) | 341 | **settled — in §2:** `+Shell.Overlays.UI.cs` | 4 | I | 32 | 87 |
| Logout confirm (modal) | modal | 19 (W8) | inside 341 | **settled — in §2:** `+Shell.Overlays.UI.cs` | 4 | I | 32 | 87 |
| Notification panel (bell) — feed, rows, filters, read state | flyout | 19 (+14 for the OS half) | 640 + 377 (bridge, split with ch. 14) | **settled (A9):** the panel and its rows stay in `Shell/Shell.UI.cs`; the feed/policy/escalation fold is `Platform/Notify.cs` (CORE) + `Platform/Notify.Host.cs` (SHELL) — `Entities/Notification.cs` + `Notification.UI.cs` is withdrawn, a notification is not an entity | 4 | I | 32 | 87 |
| Play-a-link dialog | modal | 19 | 251 + 146 | **settled — in §2:** `+Shell.Overlays.UI.cs` | 4 | I | 32 | 87 |
| In-app toasts (107 `Toast.Show` sites across 38 files) | overlay | 19 + 29 | ≈600 lines of app-side decision code | **settled:** the decision sites live in `+Shell.Overlays.UI.cs`; ch. 29 §6.4 routes every site through one `Notify.Say(key, severity, …)` in `Platform/Controls.cs`, so the inventory is generated, not grepped | 4 | I | 32 / 28 | 87 / 123 |
| Teaching tips (`WaveeTips` / `WaveeTipsCore`) | overlay | 19 (+28 claims them too) | 188 + 100 | **settled (A10):** `Shell.cs` CORE + `Shell.Host.cs` SHELL — ch. 28 drops its claim | 4 | I | 32 | 87 |
| Playback-runtime banner (`InfoBar`, horizontal + vertical arms) | banner | 19 | 121 | **settled — in §2:** `+Shell.Overlays.UI.cs` | 4 | I | 32 | 87 |
| Playback-runtime setup card (10 phases + Advanced) | dialog | 19 (+28 wizard host) | 1,030 + 20 | **settled (A18):** the banner, gate and mount stay in `+Shell.Overlays.UI.cs`; the card body is the named partial `+Screens/Setup.UI.Runtime.cs` (1,100), written by I in Wave 4 and mounted again by R's wizard in Wave 6; the provisioning SHELL half is `Screens/Setup.Host.cs` (≈450, UNVERIFIED — ch 19 §9.5 proposes the file but states no number) | 4 (body, banner) / 6 (mount, host) | I (body, banner) / R (mount, host) | 32 | 87 |
| Digital-signature details dialog | dialog | 19 (W33) | inside 1,030 | **settled — in §2:** `+Shell.Overlays.UI.cs` | 4 | I | 32 | 87 |
| Context menus — the whole grammar (`Menus.cs`, icons, rules, targeting) | every surface | 01 + 29 (+26) | 1,197 + 87 + 109 + 79 | **settled (A7):** `Platform/Actions.cs` (CORE) + `Platform/Actions.UI.cs` (the menu vocabulary, `ActionIcons`, the action row, the picker, the reason caption) | 4 | I | 31 / 28 | 82 / 123 |
| Drag chip, insertion preview, spring-load, drop rules, resource payload | every surface | 01 + 29 (+06, 02) | 938 | **settled (A6):** `Platform/Drag.cs` — shipped in the first week of Wave 4 | 4 | L | 31 / 28 | 82 / 123 |
| Selection command bar | detail table + artist album drawer | 01 + 04 | 394 | `Platform/Controls.cs` | 4 | L | 31 | 82 |
| Track filter flyout / sort menu / density panel | detail table | 04 | 472 + 290 | **settled (A2):** `Track.Table.cs` | 4.5 | M | 20 | 72 |
| Track credits dialog | row menu | 01 | 123 | **settled (A2):** `Track.Drawer.cs` | 5 | M | 31 | 82 |
| File-drop target + cue (whole window) | shell | 29 + 15 | `WaveeShell.cs:1351-1431` + 115 (`LocalFileActions`) | **settled:** `Shell.UI.cs` (`LocalFileActions`) / `Shell.Host.cs` (the file-drop hook); `LocalPlayables` (the local-file display-title rules) is settled separately, inside `Entities/Playlist.cs` (O, Wave 5) | 4 | I | 28 | 123 |
| Focus ring + keyboard visuals (the dual ring, both themes, the roving tab index) | every surface | 29 | engine + ≈20 app sites (37 `FocusVisualMargin` sites censused) | `Platform/Design.cs` — two named insets, `FocusInsetBordered = 2f` / `FocusInsetRow = 1f` | 4 | L | 28 | 123 |
| Dialog width ladder (320 / 460 / 480 / 548) + the two raw plates + the destructive-confirm shape | modal | 29 (W22, W23) | engine `ContentDialog` + **11 app call sites, 4 widths**; `SettingsShared.Confirm` is the one destructive helper | `Platform/Controls.cs` (the three dialog helpers) — `SetupDialog`/`AfterUpdateDialog` stay raw overlays (28) | 4 | L | 28 | 123 |
| Empty / skeleton / error / offline vocabulary | every surface | 29 + 02 | 120 + 29 (`OfflineBanner`, **zero call sites**) + ≈90 notice rules + engine `Loadable` | `Platform/Controls.cs` | 4 | L | 28 / 29 | 123 / 82 |
| Shared-element morph (`MorphKeys`) | navigation | 29 + 00 | 17 + call sites | `Platform/Design.cs` | 4 | L | 28 / 18 | 123 / 75 |
| Page-transition pair matrix, material layer, wash geometry | navigation | 29 + 18 + 00 | 122 + 133 + 59 + 55 | `Shell.cs` / `Shell.UI.cs` + `Design.cs` | 4 | I + L | 28 / 26 | 123 / 89 |
| Network state chrome (offline chip, banner, metered line) | chrome | 29 (+20) | ≈150 + 73 + 30 | `Platform/Platform.cs` (the policy) + one `NetworkChrome` section in `Shell.UI.cs` (the chrome; today spread over three files) | 4 (chrome) / 6 (policy) | I (chrome) / S (policy) | 28 | 123 |

### 2.6 Setup, feedback, diagnostics surfaces

| Surface | Route key | Chapter | 0.2.9 source lines | 0.3 file(s) | Wave | Owner | Wireframes | Parity items |
|---|---|---|--:|---|---|---|--:|--:|
| Setup wizard — Terms / Sign in / Local playback | pre-shell (first run) | 28 | 1,601 (`Features/Setup`) + 775 (`Features/Auth`) | `Screens/Setup.cs`, `Setup.UI.cs` | 6 | R | 26 | 90 |
| Sign-in QR plate + grid + `LoginView` | setup | 28 (§1.5 — the merged sweep) | 368 (`Qr`) + 75 + 216 + 23 | `Screens/Setup.cs`, `Setup.UI.cs` | 6 | R | 26 | 90 |
| `--qr-dump` artefacts (`qr.png` + the ASCII grid) | n/a — headless CLI | 28 (§1.5, §6.6, W27 — merged from `90-coverage-gaps-1.md`) | 93 (`Features/Auth/QrDump.cs`) + `Program.cs:232-238` + `CliRun.cs:15` | **settled — in §2:** `Screens/Diagnostics.Probe.cs` (the `--qr-dump` CLI arm) | 6 | S | 26 | 90 |
| Highlight viewer (What's new slides) | overlay | 28 (W20, W21) | inside 2,156 | `Screens/ReleaseNotes.UI.cs` | 6 | R | 26 | 90 |
| After-update dialog | modal | 28 (W22, W23) | inside 2,156 | `Screens/ReleaseNotes.UI.cs` | 6 | R | 26 | 90 |
| Report dialog (Bug / Crash) + composer + redactor | modal | 28 | 844 | **settled — in §2:** `Screens/Feedback.cs` (CORE) + `Screens/Feedback.UI.cs` (UI) | 6 | R | 26 | 90 |
| Crash reports card | Settings ▸ About | 28 + 27 | 59 | `Screens/Feedback.UI.cs`, mounted by `+Settings.UI.About.cs` — cross-owner | 6 | R | 26 / 29 | 90 / 79 |
| Logs panel + log view + capture policy | Settings ▸ Logs | 27 | 592 + 240 + 70 | **settled:** `Screens/Diagnostics.UI.cs` (the panel) + `Diagnostics.Host.cs` (`WaveeLogSessions`) + `Platform/Platform.cs` (the `WaveeLog` seam) | 6 | S | 29 | 79 |
| Equalizer curve editor | Settings ▸ Playback | 27 | 407 + 47 | **settled (A16):** `+Controls.Picker.cs` (`WaveeEqualizerCurve`) | 4 | L | 29 | 79 |
| FPS overlay | developer overlay | 27 | 45 | `Screens/Diagnostics.UI.cs` | 6 | S | 29 | 79 |

### 2.7 OS surfaces (painted by Explorer, the Shell and the Action Center — chapter 14)

| Surface | Route key | Chapter | 0.2.9 source lines | 0.3 file(s) | Wave | Owner | Wireframes | Parity items |
|---|---|---|--:|---|---|---|--:|--:|
| SMTC media overlay card (6 states, W1-W6: playing, paused, buffering, live, no track, first-of-queue) | n/a — OS | 14 | 229 + 67 (coalescer) | `Playback/Playback.Os.cs` (+67 in `Playback.cs`) | 3 | H | 29 | 58 |
| Taskbar button — overlay icon + progress (3 states; playing carries **no** overlay) | n/a — OS | 14 (W7-W9) | 248 + 27 (icon) | `Playback.Os.cs` | 3 | H | 29 | 58 |
| Taskbar thumbnail toolbar (3 buttons, re-added on an Explorer restart, degrades to tooltips) | n/a — OS | 14 (W10-W13) | inside 248 (`ApplyThumbs` :160-229) | `Playback.Os.cs` | 3 | H | 29 | 58 |
| Jump list | n/a — OS | 14 (W14-W16) | 314 | `Playback.Os.cs` | 3 | H | 29 | 58 |
| Windows toasts / Action Center — release, episode, concert, follower, burst summary, update ×3, pre-save hero, daylist | n/a — OS | 14 (W17-W25) | 255 + 129 + 234 + 96 + 153 + 78 | **settled (A9):** `Platform/Notify.cs` (CORE, 1,000 total once ch 19's merge/read-state half is folded in) + `Platform/Notify.Host.cs` (SHELL, 715) | 4 — moved from Wave 6 (A9: ships with the in-app panel, not after it) | I | 29 | 58 |
| Notification simulator ("Send event") | Settings ▸ Notifications | 14 + 27 | 193 | `Screens/Diagnostics.Probe.cs` | 6 | S | 29 / 29 | 58 / 79 |
| Cold-launch + redirected activation → deep link → wake window | n/a — OS | 14 | `Program.cs:483-488` | `Shell/Shell.cs` | 4 | I | 29 | 58 |
| In-app update card (the Action-Center half's in-window twin) | overlay | 14 (W26-W28) + 19 | `NotificationCenterBridge.cs:216-287` | `Shell.cs` (+135) — ch. 14 hands it to ch. 19 | 4 | I | 29 / 32 | 58 / 87 |

**Settled.** Ch. 14 §9 budgets `Playback/Playback.Os.cs` at **840** — 818 raw (229 + 248 + 314 + 27) plus its
`Loc.Get` wiring — and explicitly retracts the 830 its own first draft carried; ch. 29 §9.9.1's reconciliation to
**830** quotes that retracted figure. `wavee-0.3-implementation.md` §2 now carries the file at ch. 14's **840**
(previously 1,100), owner H, Wave 3 — the ten-line gap between 840 and ch. 29's 830 is not otherwise recorded.

### 2.8 Cross-cutting — not a surface, but listed so it cannot be dropped

| Surface | Route key | Chapter | 0.2.9 source lines | 0.3 file(s) | Wave | Owner | Wireframes | Parity items |
|---|---|---|--:|---|---|---|--:|--:|
| Appearance preferences — the 42-row preference matrix and everything it changes | every surface | 30 | ≈4,300 (plumbing ≈2,073 + pure rules ≈2,228) | **settled (plan §9.5):** `Platform/Prefs.cs` (the four epochs) + `Platform/Platform.cs` (the store) + `Platform/Design.cs` + every `*.UI.cs` / `*.Page.cs` read | 4-6 | L (Prefs/Design/Controls + epochs, Wave 4), S (Platform.cs store, Wave 6), R (Settings), M/O (detail), K (lyrics/NPV), I (player bar) | 29 | 75 |
| `--fake` seed (166 liked tracks, 16 covers, the export fixtures) | every route | 31 | ≈1,613 + 1.9 MB assets | **settled — in §2:** `Entities/Entities.Fake.cs` (≈1,100) + `Platform/Platform.cs` (args + seed clock) + one line in `App.cs` + `Wavee.Tests/EntitiesFakeTests.cs`; a first cut of SEED-CORE is needed in Wave 4 for that wave's gate (orchestrator-owned), Q takes ownership in Wave 5 | 5 | Q | 17 | 56 |
| Bundled fixture assets — 16 covers + `liked-songs-300.png` + the four `assets/spotify/*.json` | every route | 31 | 321,844 B + 1,579,823 B, copied by `Wavee.csproj:196` | **settled:** the same assets, decoded by `Spotify/Spotify.Decode.cs`'s offline fixture path (owner E, Wave 2, +70) rather than by `SpotifyExport.cs` | 5 (assets) / 2 (decode) | Q (assets) / E (decode) | 17 | 56 |
| `SidebarMiniature`'s hand-indexed fixtures (the customizer preview) | `sidebar-customize` ▸ preview | 31 (W13) | 9 call sites in `SidebarMiniature.cs` (:128, :134-137, :156-162, :263-266, :272, :292, :328) | `Entities/Entities.Fake.cs` (`SeedFake.Sample`), read by `Shell/Sidebar.Customizer.UI.cs` — a **static** preview of a template, never the live tables (ch. 26 §7.1) | 4 + 5 | J (UI) + Q (seed) | 17 | 56 |
| Seed determinism — the per-process hash facts and the `DateTime.Now`-relative dates | every `--fake` screenshot | 31 (W15, W16) | `FakeData.cs:147` (`GetHashCode` fan-out), `:331`, `:417` (relative dates) | a pinned seed clock in `Platform/Platform.cs` (`Clock.SeedEpoch`, `FixedSeedEpoch`) — **without it no screenshot parity item is reproducible across two launches**. `Platform.cs`'s boot half is orchestrator-owned from Wave 0 (`Main` and Waves 2-4 call it); owner S completes the file in Wave 6 (plan §5 Wave 1 note) | 0 (boot) / 6 (S completes) | orchestrator / S | 17 | 56 |
| Zoom + large-display scaling ladder | every surface | 29 + 30 | 114 + call sites | `Platform/Platform.cs` | 6 | S | 28 / 29 | 123 / 75 |
| Ambient cadence — plugged / battery / unfocused (every `loop: true` row) | every surface | 29 (W25, §5.4) | 140 (`App/AmbientPowerPolicy.cs`) | `Platform/Design.cs` (the cadence block `00-design-system.md` §5 lacks) + `Platform/Platform.cs` (the policy) | 4 (cadence block) / 6 (policy) | L (cadence block) / S (policy) | 28 | 123 |
| Localisation, long strings, the four fixed picker widths | every surface | 29 | 44 + `assets/loc/*.json` | `Platform/Platform.cs` — read by every owner throughout, but the file itself is owner S's | 6 | S | 28 | 123 |
| First-run composite (gating, window, wizard hand-off) | first launch | 29 + 28 | 201 + `WaveeApp.cs:315-418` | `Shell/Shell.cs` + `Screens/Setup.cs` | 4 + 6 | I + R | 28 / 26 | 123 / 90 |

**Row count: 119 surfaces across 32 chapters.** Seven rows are new since the first pass: the `--qr-dump` artefacts
(§2.6, from the merged sweep), the taskbar thumbnail toolbar split out of the taskbar button (§2.7), the dialog
width ladder (§2.5) and the ambient cadence (§2.8) now that chapter 29 draws both, and chapter 31's three fixture
rows — the bundled assets, the sidebar miniature's hand-indexed sample and the seed's determinism.

---

## 3. Honest line estimate vs plan §2 budget — the per-chapter totals

**This section is the 2026-09-11 record.** It is kept as-is because `wavee-0.3-implementation.md` §2 quotes this
table's own numbers by name (the honest de-duplicated ≈141,000-143,000 against the old 64,900 budget, ≈2.2×) — the
plan's **§2 is now the live tree** (owners, waves and the raised, honest per-file budgets); this section is the
before-picture that justified raising it (P1), not a second source of truth for where a file lands.

Each row is the chapter's own §9 figure. Ranges are reduced to their midpoint for the sum; the range is kept in the
cell. "Plan §2 budget" is the budget **that chapter cites** for its own files.

| Chapter | Honest line estimate (its §9) | Plan §2 budget | Delta |
|---|--:|--:|--:|
| 00 design system | 8,050 (`Design.cs` 2,600-3,000 + `Controls.cs` 4,500-6,000) | 2,400 (implied share of `Platform/` 8,600 ÷ 7 files) | **+5,650** |
| 01 track row | 4,350 | 1,800 (`Track.cs` 300 + `Track.UI.cs` 1,500) | **+2,550** |
| 02 cards and controls | 4,100 (3,800-4,400) | 2,500 (`Design.cs` + `Controls.cs` implied) | **+1,600** |
| 03 detail frame | 4,200 (3,500 frame + 700 album trailing) | **0 — no file, no owner, no wave slot** | **+4,200** |
| 04 detail track table | 5,100 | 1,800 (**the same §2 line as ch. 01**) | **+3,300** |
| 05 album | 2,800 (2,600-3,000) | 2,000 | **+800** |
| 06 playlist | 4,600 (4,400-4,800) | 2,700 | **+1,900** |
| 07 liked songs | 4,900 (4,600-5,200) | 2,500 (`User.*`, **shared with 15 and the profile page**) | **+2,400** |
| 08 artist and discography | 5,550 | 3,200 | **+2,350** |
| 09 show / episode / module | 3,600 | 1,500 (+ `Modules.UI.cs` has no line of its own) | **+2,100** |
| 10 home | 2,800 (2,600-3,000; family 7,000-8,000) | 2,000 (all of Home) | **+800** |
| 11 home cards and modules | 2,900 | 2,000 (**the same §2 line as 10 and 12**) | **+900** |
| 12 home section + customizer | 1,450 (1,400-1,500) | 2,000 (**the same §2 line**) | **−550** |
| 13 search and browse | 3,950 | 2,200 (no `Browse.Page.cs`) | **+1,750** |
| 14 OS surfaces | 2,421 | 1,100 (`Playback.Os.cs`) | **+1,321** |
| 15 library | 2,050 (1,900-2,200; `User.*` total 3,400-3,900) | 2,500 (`User.*` total, **shared with 07**) | **−450** |
| 16 recents and history | 3,080 | **0 — absent from the tree** | **+3,080** |
| 17 concerts | 3,850 | 1,200 | **+2,650** |
| 18 shell frame | 4,400 (frame alone; owner I's whole bucket 10,000-11,000) | 4,800 (`Shell.cs`+`UI`+`Host`, **shared with 19, 20, 16-history**) | **−400** (owner I's bucket: **+5,700**) |
| 19 shell overlays | 4,600 (4,400-4,800) | **0 — "no line is allocated to notifications, toasts or tips at all"** | **+4,600** |
| 20 player bar | 2,350 (1,900-2,100 UI + 230 + 120 CORE) | 2,800 (all of `Shell.UI.cs`, **shared with 18 and 19**) | **−450** |
| 21 right rail / NPV / queue / stage | 4,700 (the named-file breakdown sums 4,900) | 2,400 (`Rail.UI.cs` 1,800 + `Queue.UI.cs` 600; **nothing for the stage**) | **+2,300** |
| 22 lyrics | 10,600 (≈10,000 + ≈600 inspector) | 5,100 | **+5,500** |
| 23 deck faces | 5,450 (5,200-5,700) | 2,400 | **+3,050** |
| 24 video surfaces | 3,200 | **0 — absent from the tree** | **+3,200** |
| 25 sidebar | 19,000 (18,000-20,000, incl. ch. 26's `Sidebar.Doc.cs` ≈4,000) | 10,300 | **+8,700** |
| 26 sidebar customizer + pipeline | 10,500 (whole platform re-stated at 23,000-25,000) | **0 itemised** — a share of the same 10,300 | **+10,500** |
| 27 settings and diagnostics | 6,800 | **0 stated per file** — `Screens/` is ~13,700 for 13 files | **+6,800** |
| 28 setup / what's new / feedback | 8,100 across 7 files | 3,700 implied | **+4,400** |
| 29 cross-cutting | 1,850 of NEW app lines (§9.10), **plus ~2,100 that ports into files other chapters already budget** — counted once, there, not here | **0 — "nothing at all"; §2 allots this chapter no file** | **+1,850** |
| 30 appearance preferences | 4,120 | **0 — the plan never names this plumbing as a deliverable** | **+4,120** |
| 31 fake data | 1,350 | **0 — no entry for the seed in the tree** | **+1,350** |
| **TOTAL (all 32 chapters; every one now has a §9)** | **156,771** | **64,900** | **+91,871** |

**What the total means, stated honestly.** The raw sum double-counts, because several chapters describe overlapping
files. The named overlaps, each with the chapter that records it:

- **01 + 04** both count `Track.UI.cs` and `Track.cs`. Ch. 01 §9's reconciled plan makes the whole track surface
  **8,700**, not 9,450 → **−750**.
- **25 + 26** both count the sidebar platform. Ch. 26's whole-platform figure is **23,000-25,000** (mid 24,000)
  against 19,000 + 10,500 = 29,500 → **−5,500**.
- **30** is a cross-cut *view* of files owned by 00, 01, 07, 18 and 27, not new code → **−4,120**.
- **02's** `Controls.cs` share (≈3,500) sits inside **00's** `Controls.cs` 4,500-6,000, which ch. 00 explicitly says
  covers "every non-entity component from `Components/`" → **−3,500**; **01's** `Controls.cs` share (900) likewise
  → **−900**.
- **03's** "+700 album trailing in `Album.Page.cs`" may already be inside **05's** 2,600-3,000 → **−700**
  (uncertain; the two chapters do not say).
- **21 vs 22** on the stage: ch. 21 budgets `Stage.cs` 400 + `Stage.UI.cs` 1,150; ch. 22 budgets
  `Lyrics.Stage.UI.cs` 1,400. Ch. 22's audit calls it reconciled (21 owns `StageLayout`/`StageIdentity`/
  `StageChrome`/`StageInk`, 22 owns the lyrics pane) but no number was restated. Possible overlap ≈500 — **flagged,
  not subtracted**.
- **10 + 11 + 12** are self-consistent: their 7,150 sits inside ch. 10's own Home-family figure of 7,000-8,000.
- **29** is already de-duplicated in its own cell: its §9.10 splits ≈3,950 into **1,850 of new app lines** and ~2,100
  that lands in files other chapters budget — `Platform/Drag.cs` **700** (ch. 01's line) and
  `Entities/Entities.Fake.cs` **850** (ch. 31's 1,100) being the two largest. Only the 1,850 is summed above, so
  **nothing is subtracted here** — but note that ch. 29 and ch. 31 give that one seed file two different numbers
  (850 vs 1,100), which is a number to settle, not a double count.

**De-duplicated: ≈141,000-143,000 honest lines against ≈64,900 of plan §2 budget for the same surfaces — ≈2.2×.**
For scale, plan §2's whole-app footer is "~86 files, ~83-88k lines" *including* `Spotify/` (9,600) and the non-UI
half of `Playback/`, neither of which any chapter here touches.

**One chapter now sums this too, per folder rather than per chapter.** `29-cross-cutting.md` §9.4 is the same
reconciliation cut by tree folder — `Shell/` 21,600 → ≈38,500, `Platform/` and `Entities/` likewise, +62 % overall —
and it reaches the same verdict from the other direction: at +62 % the plan's "a file 30 % over budget gets a named
partial" rule produces a partial for nearly every file, "which is not a budget — it is a rename". Read §9.4 for the
per-folder numbers and this section for the per-chapter ones; where they differ, they differ only in how the same
sums are grouped.

**A second arithmetic finding, from adding the plan's own tree.** §2's `Shell/` subtotal says **13 files ~21,600**,
but its own per-file numbers sum to **26,800** (Shell 1,200+2,800+800+2,400 = 7,200 · Sidebar 4,500+5,000+800 =
10,300 · Rail/Deck 1,800+800+1,600 = 4,200 · Lyrics 1,800+2,000+1,300 = 5,100). `Entities/` says "40 files ~23,000"
against 36 files summing to **24,000**. Ch. 22 §9 spotted half of this and asked for the `Shell/` subtotal to move
to ~26,500; the `Entities/` discrepancy is not recorded anywhere. Fix both before any budget conversation, or the
tree's own totals argue with its own rows.

---

## 4. Files chapters propose that plan section 2 does not have

**This section is the 2026-09-11 record — the plan's own "46 files" figure (§9.2) is quoted from this catalog by its
original number, so the count stays fixed.** All 46 are now resolved (43 folded into the settled tree with an owner
and a wave — the album merch table joined this group 2026-09-12, plan §9.6 Q4: ADDED, inside `Edges.cs`;
`User.Page.Profile.cs` and `Entities/Notification.*` explicitly rejected — A3, A9 — `Lyrics.Stage.UI.cs`
dropped — A12); **`wavee-0.3-implementation.md` §2 is now the
live tree** for where each of these files actually lands, not this catalog.

Consolidated, with the chapter that proposes each. Where two chapters propose different homes for the same code,
both are listed here and the disagreement is in §6.

### Entities/

| Proposed file | What it holds | Proposed by |
|---|---|---|
| `Entities/Detail.cs` (CORE) | breakpoints, vertical-layout arithmetic, rail policy, notice rules, reveal ramp, header merge, `ContextBandLayout`, the per-kind config table | **03** (also 05 #21, 06, 08) |
| `Entities/Detail.UI.cs` (UI) | `Frame`, `Hero`, `Rail`, `CompactRail`, `Band`, `Skeleton`, `NoticeBar`, `TonePlane` — static functions over handles | **03** (also 08 §1.2) |
| `Entities/Track.Table.cs` | the detail table: chrome, command bar, header, tiers, list arms, choreography, drag, selection, recs, filters, the drawer's mount | **04**, adopted unchanged by **01** §9 |
| `Entities/Track.Drawer.cs` | the expanded-row drawer body: facts strip, versions, gutter rail, waveform, format ladder, credits cache | **01** (supersedes 04 §1.2) |
| `Entities/Artist.Discography.cs` | the `disco:` facet page, virtual grid, album drawer, verdict, era bands | **08** |
| `Entities/User.Liked.cs` (CORE) | `LikedCoverRules` + `ContentFilterTags` + the liked uri identity | **07** |
| `Entities/User.Facts.cs` (CORE) | `LikedFactsRules` verbatim | **07** |
| `Entities/User.Cover.cs` (UI) | nine cover treatments, four palette leaves, heart geometry, picker + flyout | **07** |
| `Entities/User.Facts.UI.cs` (UI) | the facts bento: panel, week/years/tempo/artists/blend/rediscover cards, pills, lens header | **07** |
| `Entities/User.Page.Library.cs` / `.Liked.cs` / `.Profile.cs` | the three `User.Page` arms as named partials at Wave 0 | **15** (a *different* partial scheme from 07 — see §6) |
| `Entities/Palette.cs` (+ `Palette.Host.cs`) | the per-image graded schemes as columns, TTL, key identity, `Watch`, `Ensure` | **00** §9.5, **07** (400 lines) — contested by **08**, which wants `Platform/Design.cs` |
| `Entities/Recents.cs`, `Recents.UI.cs`, `Recents.Page.cs` | the whole Recents feed — a synthetic-parent feed like Home | **16** |
| `Entities/Browse.Page.cs` | `BrowsePage` + `BrowseDirectoryPage` + `BrowsePageHost` (857 lines with no home) | **13**, **12** |
| `Entities/Home.Customizer.cs` **or** `Screens/HomeCustomize.UI.cs` | the Home customizer page | **12** |
| `Entities/Home.Host.cs` | `home-layout.json` off the UI thread — there is no SHELL file for Home at all | **12** |
| `Entities/Home.Cards.UI.cs`, `Home.Artists.UI.cs` | named partials for the card vocabulary and the artist row | **11** |
| `Entities/Notification.cs`, `Notification.UI.cs` | the notification feed + its rows | **19** (contested by 14's `Platform/Notify.*`) |
| `Entities/Entities.Fake.cs` | `Entities.SeedFake()` — the seed plan §5 names in four words and never files | **31**, **29** |
| An `Entities/*` line for a merch table | album merch (D8) — **ADDED, plan §9.6 Q4, 2026-09-12: `MerchTable` + `EdgeTable<NoEdge> AlbumMerch`, inside `Edges.cs`, owner B, Wave 1** | **05** |
| An `Insert`/`Remove` on `EdgeTable<TEdge>` | §4.3 offers only `Replace`/`ReplacePage`; `Like()` needs `at: 0` | **07** |

### Shell/

| Proposed file | What it holds | Proposed by |
|---|---|---|
| `Shell/Stage.cs` (CORE) + `Shell/Stage.UI.cs` | the whole immersive stage — 2,261 lines, a 585-line test file | **21** |
| `Shell/Rail.Styles.UI.cs` + `Shell/Rail.cs` (CORE) | player-style flyout, thumbnails, art menu, swatches; `RailVideoCoupling` + the NPV catalog/prefs/diagnostics | **21** |
| `Shell/Deck.Faces.cs` | the eight non-record deck faces (2,508 lines today) | **23** |
| `Shell/Lyrics.Stage.UI.cs` | the fourth lyrics partial — `Lyrics.cs` 3,700 · `Lyrics.UI.cs` 2,600 · `Lyrics.Stage.UI.cs` 1,400 · `Lyrics.Host.cs` 2,300 | **22** |
| `Shell/Video.cs` (CORE ~900), `Video.UI.cs` (UI ~1,700), `Video.Host.cs` (SHELL ~250) | the four video surfaces, the placement machine, the override manager body, the detached-window owner | **24** |
| `Shell/Sidebar.Customizer.UI.cs` | the customizer page (4,247 lines) — a routed destination, not sidebar chrome | **26** |
| `Shell/Sidebar.Doc.cs` | document + reducer + wire (a *different* answer to the same gap) | **25** |
| `Shell.PlayerBar.UI.cs` | a named partial for the player bar, on day one | **20** |
| A `Shell.History` section in `Shell.cs` + `history.json`/`play-log.json`/`play-recency.json` in `Shell.Host.cs` | the nav log and the play log | **16** |
| A home for `WaveeTips` / `WaveeTipsCore` | `Shell.cs` CORE + `Shell.Host.cs` SHELL | **19** (ch. 28 also lists them) |
| A home for the device roster and for `PlacementState`; a section of `Shell.cs` for `ShellUi` | the bar's four toggles write rail state nothing in the tree corresponds to | **20** |
| A masthead-band / material-layer / narrow-drawer / omnibar file | "each is a named surface, not optional chrome" | **18** |
| A home for `FriendsPanel`'s data | no `Friends.cs`/`Social.cs`, no friends edge, no wave port list — **CONFIRMED, plan §9.6 Q5, 2026-09-12: split into the edge (`Edges.cs`, B, Wave 1) and the panel (`Rail.UI.cs`, K, Wave 4), no `Friends.cs`** | **21** |

### Platform/ and Screens/

| Proposed file | What it holds | Proposed by |
|---|---|---|
| `Platform/Drag.cs` | payload record, chip resolver, all drop rules, insertion preview (938 lines) | **01**, **29** (ch. 06 proposes `Shell.Drag.cs`; ch. 02 says `Platform/Controls.cs`) |
| `Platform/Actions.{cs,UI.cs,Menus.cs,Table.cs}` | the action + extension platform | **29** (ch. 26 says `Shell.cs`'s action table; ch. 25 says `Platform/Extensions.cs`) |
| `Platform/Notify.cs` (CORE **445**) + `Platform/Notify.Host.cs` (SHELL **715**) | `NotificationPolicy`, `NotificationPrefs`, `AppUpdateToasts`, the escalation plan; `ToastEscalator`'s WinRT half, `DaylistNotifier`, `ReleaseNotifier`, AUMID + activator. (Ch. 14 §9 retracts its own first draft's ≈390: the three files `Notify.cs` absorbs are already 385, so `EscalationPlan` adds on top) | **14** |
| `Platform/Prefs.cs` (or a `Platform.Prefs` partial) | `AppearancePrefs` / `LyricsPrefs` / `DetailHeroPrefs` / `NpvPlayerPrefs` — four cross-surface epoch signals | **00** |
| A home for `WaveeAccentCtx` and for `Glyphs.cs` | the page-scoped accent context; the PUA glyph font, which must be set **before** `FluentAppHarness.Run` | **00** |
| `ZoomAutoPolicy.MigrateMode(settings)` in `Main` | one-shot, version-stamped; without it a user on 125 % wakes at 150 % | **00** |
| A home for `IDetachedVideoWindow` / `OpenDetachedWindow` and for the override roster | `Platform/Platform.cs` (seam) + `Platform.Host.cs` (Win32) | **24** |
| A home for the settings key table + epochs, `WaveeLog`/`WaveeLogSessions` | `Platform/Platform.cs` or `Screens/Diagnostics.Host.cs` — the plan names neither | **27**, **30** |
| `Screens/Feedback.cs` (CORE ≈650) | `ReportKinds`, `ReportBundle`, `IssueFormUrl`, `ReportRedactor`, `ReportKindIndex`, `ReportIdentity` | **28** |
| `Screens/Setup.Host.cs` | the runtime provisioning half is SHELL and has no file | **19** |
| A §2 line for `App/LocalPlayables.cs` (165) + `Actions/LocalFileActions.cs` (115) | the display title of every local file, and the whole file-drop policy — named by **no** chapter's source list before ch. 15 | **15** |
| A §2 line for `Features/Auth/*` (5 files), `assets/lottie/*`, `assets/whatsnew/*` | | **28** |
| `Wavee.Tests` files for `PlaylistPageNoticeRules` and four untested album rules; the 10-file `Wavee.Tests/Actions/` suite; the 10 deck test files scheduled in Wave 4 | | **05**, **01**, **23** |

---

## 5. Surfaces that now have an owner and a wave (closed 2026-09-12)

**This was the open list on 2026-09-11** — 20 rows with a chapter and a specification but no line in plan §2's tree
and no name in plan §5's wave tables. `wavee-0.3-implementation.md`'s rewrite (its §9.4 arbitrations and §9.5 table)
closed every row: 18 by arbitration (A1-A18), the rest by the plan simply naming the home its chapter already asked
for. The plan's own table (§9.5) carries **23** rows because it splits Recents and History to two owners and two
waves, re-budgets OS surfaces rather than leaving it unowned, and adds the runtime provisioning card (A18). Two
rows below were settled on a home but still carried an open confirmation for Christos; **both are now answered,
2026-09-12** — see the "Settled by" column and `wavee-0.3-implementation.md` §9.6.

| Surface | Chapter(s) | 0.3 home | Owner | Wave | Settled by |
|---|---|---|---|---|---|
| Shared detail frame | 03 | `Entities/Detail.cs` (CORE) + `Detail.UI.cs` (UI) | M | 4.5 | A1 |
| `Track.Table.cs` + `Track.Drawer.cs` | 01, 04 | `Entities/Track.*` — `Track.cs`, `Track.UI.cs`, `Track.Table.cs` (Wave 4.5); `Track.Drawer.cs` (Wave 5) | M | 4.5 / 5 | A2 |
| Recents | 16 | `Entities/Recents.cs` + `Recents.UI.cs` + `Recents.Page.cs` | P | 5 | plan §9.5 |
| History | 16 | a `Shell.History` section in `Shell.cs`, `history.json`/`play-log.json`/`play-recency.json` in `Shell.Host.cs`, the page in `+Shell.History.UI.cs` | I | 4 | plan §9.5 |
| Video surfaces (all four) + placement machine + override flyout body | 24 | `Shell/Video.{cs,UI.cs,Host.cs}` + `+Screens/Settings.UI.Video.cs` | K | 4 | A13 |
| Immersive stage | 21 | `Shell/Stage.cs` + `Stage.UI.cs` | K | 4 | A12 |
| Toast + notification stack | 14, 19 | `Platform/Notify.cs` + `Notify.Host.cs`; the panel stays in `Shell/Shell.UI.cs` | I | 4 | A9 |
| Action + extension platform | 26 §9.3.9, 29, 25 | `Platform/Actions.cs` + `Actions.UI.cs`; `PinRowRule` goes the other way, into `Sidebar.cs` | I | 4 | A7 |
| `local` route | 15 | the `DetailKind.Playlist` arm of `Playlist.Page.cs` (`wavee:local:all`) | O | 5 | plan §9.5 |
| Discography page | 08 | `Entities/Artist.Discography.cs` (~1,300); `disco:` is in the §4.11 route table | N | 5 | plan §9.5 |
| Home customizer + `home-layout.json` + the section drill page | 10, 12 | `Home.Customizer.cs`, `Home.Host.cs`, `Home.Page.cs` (`Home.SectionPage`); `home-customize`/`home-section:`/`browse-section:` added to the Wave 5 probe list | P | 5 | A15 / plan §9.5 |
| `Browse.Page.cs` | 13, 12 | `Entities/Browse.Page.cs` (857 lines of 0.2.9 with no home before this revision) | P | 5 | plan §9.5 |
| Sidebar customizer page + `sidebar-layout.json` persistence | 25, 26 | `Sidebar.Customizer.UI.cs` (sequenced **last**) + `Sidebar.Doc.cs` + `Sidebar.Host.cs` | J | 4 | A4 |
| Cover palette plane | 00, 03, 07, 08, 12, 21 | `Entities/Palette.cs` (CORE) + `Palette.Host.cs` (SHELL) — not `Platform/Design.cs` | A / L | 1 / 4 | A5 |
| Drag layer | 01, 02, 06, 29 | `Platform/Drag.cs`, first week of the wave | L | 4 | A6 |
| `FriendsPanel`'s data | 21 | the friends edge + columns land with the other edges (`Edges.cs`, `EdgeTable<FriendEdge> Friends`); the panel lands with the rail (`Rail.UI.cs`) | B / K | 1 / 4 | plan §9.5 — **CONFIRMED, §9.6 Q5, 2026-09-12** |
| Prerelease surface | 05 | `Album.Page.cs`: the route, the kind-138 resolve, the countdown card, the pre-save heart swap, the greyed pending rows, the "N of M songs" tile | M | 5 | plan §9.5 |
| `Entities.SeedFake()` | 31 | `Entities/Entities.Fake.cs` (≈1,100), ch. 31 as its specification | Q | 5 | plan §9.5 |
| `connect-diagnostics` route | 27 §9.6 | its own `RouteKind` in the §4.11 route table | I | 4 | plan §9.5 — **fix decided, §9.6 Q6, 2026-09-12: issue drafted, awaiting Christos's approval, no number yet** |
| Lyrics inspector's move to `Diagnostics.UI.cs` | 22 (d), 27 | `Screens/Diagnostics.UI.cs` (+≈600) | S | 6 | A14 |
| Appearance-preferences plumbing | 30 | `Platform/Prefs.cs` (the four epochs) + `Platform/Platform.cs` (the store; boot half orchestrator-owned from Wave 0) | L / S | 4 / 6 | plan §9.5 |
| OS surfaces | 14 | `Playback/Playback.Os.cs` keeps SMTC, taskbar, jump list and the app icon, budget raised to ch. 14's 840; the jump list ships behind the `AttachHistory`/`AttachLibrary` late-binding seam, empty until Waves 1 and 4 attach | H | 3 | plan §9.5 — a re-budget, not an unowned surface |
| Runtime provisioning card | 19 §9.4 | `+Screens/Setup.UI.Runtime.cs` (1,100), the body; the banner/gate/mount stay in `+Shell.Overlays.UI.cs`; mounted again by R's wizard in Wave 6 | I | 4 | A18 — **CONFIRMED, §9.6 Q9, 2026-09-12** |

---

## 6. Contradictions between chapters — all 17 settled 2026-09-12

Each row is two chapters that put the same component in different files, or gave it different numbers, as they stood
on 2026-09-11. `wavee-0.3-implementation.md` §9.4 arbitrated every one (A1-A17; A18 is an eighteenth the plan's own
review found and has no row here since no chapter contradiction produced it). The "Chapter A says" / "Chapter B
says" columns are kept as the record of the disagreement; "State" now names the decision and its arbitration.

| # | Component | Chapter A says | Chapter B says | State |
|---|---|---|---|---|
| 1 | **`ContextBand` / `ContextBandLayout`** | **03** (:151, :880) and **08** (:194, :1107) agree: UI half in `Entities/Detail.UI.cs`, CORE in `Entities/Detail.cs`, because `ClipFadeBand` *is* `DetailVerticalLayout.StickyFadeBand` and `Height 56` *is* `CompactIdentityHeight` | **30** (:2034, :2194) files `ContextBandLayout` under `Platform/Design.cs` | **SETTLED (A1).** `Entities/Detail.cs` (CORE) + `Detail.UI.cs` (UI). Ch. 30 is wrong: a `Design.cs` home would import the detail frame's vertical layout into the design system |
| 2 | **Track drawer** (`TrackFactsStrip` + `TrackVersionsPanel` + `FormatSplitButton`) | **01** §9: `Entities/Track.Drawer.cs` (700), because the drawer's only call site is the **table**, not the row | **04** §1.2 (:204) and §9: `Track.Drawer(...)` **inside `Track.UI.cs`**, at 1,300 for row+drawer together | **SETTLED (A2).** Ch. 01's reconciled four-file plan is the single authority: `Track.cs` 1,200 + `Track.UI.cs` 2,600 + `Track.Table.cs` 2,600 + `Track.Drawer.cs` 700 = 7,100, all owner M |
| 3 | **`User.Page` partial scheme** | **07**: `User.Liked.cs`, `User.Facts.cs`, `User.Cover.cs`, `User.Facts.UI.cs`, `Palette.cs`, `User.Page.cs` | **15**: `User.Page.Library.cs`, `User.Page.Liked.cs`, `User.Page.Profile.cs`, named at Wave 0 | **SETTLED (A3).** Pages split by page, helpers by concern: `User.cs`, `User.UI.cs`, `User.Page.Library.cs`, `User.Page.Liked.cs`, `User.Liked.cs`, `User.Facts.cs`, `User.Facts.UI.cs`, `User.Cover.cs`. **No** `User.Page.Profile.cs` and no `RouteKind.User` — there is no profile page or route in 0.2.9 |
| 4 | **Sidebar platform totals and file set** | **25**: 18,000-20,000 — `Sidebar.cs` ~6,500 · `Sidebar.UI.cs` ~9,000 · `Sidebar.Host.cs` ~2,500 + ch. 26's `Sidebar.Doc.cs` ~4,000 | **26**: 23,000-25,000 — `Sidebar.cs` ~9,000 · `Sidebar.UI.cs` ~7,500 · `Sidebar.Customizer.UI.cs` ~4,500 · `Sidebar.Host.cs` ~2,500 | **SETTLED (A4).** Ch. 26's whole-platform total and file set, plus ch. 25's `Sidebar.Doc.cs`, at ch. 26 §9.4's own post-arbitration split: `Sidebar.cs` 5,000 · `Sidebar.Doc.cs` 4,000 · `Sidebar.UI.cs` 7,500 · `Sidebar.Customizer.UI.cs` 4,500 · `Sidebar.Host.cs` 2,500 = 23,500 |
| 5 | **Cover palette plane** | **00** §9.5 and **07**: `Entities/Palette.cs` (CORE) + `Entities/Palette.Host.cs` (SHELL) | **08**: `Platform/Design.cs` (GAP #1) | **SETTLED (A5).** `Entities/Palette.cs` (CORE, Wave 1, owner A) + `Entities/Palette.Host.cs` (SHELL, Wave 4, owner L). Not `Platform/Design.cs` — it is a per-image table with a TTL, an entity concern |
| 6 | **Drag layer** | **01** §9 and **29**: `Platform/Drag.cs` | **06**: `Shell.Drag.cs` or a named partial of `Shell.UI.cs`; **02** §9 #7: "they all belong in `Platform/Controls.cs`" | **SETTLED (A6).** `Platform/Drag.cs`, owner L, shipped in the first week of Wave 4 — used by rows, cards, the sidebar, the playlist page and the tab strip, so it belongs to neither the sidebar's nor the shell's file |
| 7 | **Action + extension platform** | **29**: `Platform/Actions.{cs,UI.cs,Menus.cs,Table.cs}`, Wave 4 owner **L** | **26** §9.3.9: `Shell.cs`'s action table, Wave 4 owner **I**; **25**: `Platform/Extensions.cs` | **SETTLED (A7).** `Platform/Actions.cs` (CORE: descriptor, targeting, the one action table) + `Platform/Actions.UI.cs` (menus, icons), owner **I**, landed before J's picker. `PinRowRule` goes the other way, into `Sidebar.cs` |
| 8 | **`AppUpdateToasts`** (153) | **14**: `Platform/Notify.cs` (CORE), verbatim | **19** §8: the decision half in `Platform.cs` CORE, the latches in `Platform.Host.cs` SHELL; **28** §9.5: absorbed into `Screens/ReleaseNotes.cs` (CORE) | **SETTLED (A8).** `Platform/Notify.cs` (CORE). Not `Screens/ReleaseNotes.cs`, not `Platform.cs` — a notification decision lives with the notification decisions |
| 9 | **The notification feed** | **14**: `Platform/Notify.cs` + `Platform/Notify.Host.cs`, owner **S** (Wave 6) | **19**: `Entities/Notification.cs` + `Entities/Notification.UI.cs`, owner **I** (Wave 4); **11**: "(if the timeline survives) a `Notification` model file" | **SETTLED (A9).** ONE stack: `Platform/Notify.cs` + `Platform/Notify.Host.cs`, owner **I**, **Wave 4** — not Wave 6. The panel stays in `Shell/Shell.UI.cs`; `Entities/Notification.*` is **not created** (ch. 19 §9.4 withdraws its own proposal: "a notification is not an entity") |
| 10 | **`WaveeTips` / `WaveeTipsCore`** (288) | **19** §9.5: `Shell/Shell.cs` CORE + `Shell/Shell.Host.cs` SHELL | **28** §9.6 lists them among *its* files missing from the tree, and its header source list claims them | **SETTLED (A10).** `Shell/Shell.cs` CORE + `Shell/Shell.Host.cs` SHELL (ch. 19); ch. 28 drops its claim |
| 11 | **`ToastEscalator`** (255) | **14**: `Platform/Notify.Host.cs` (its WinRT half), owner S | **19** §8: `Platform.Host.cs` SHELL with the decision (`MaxPerRebuild`, the sentinel fold, `UpdateChanged`) in `Platform.cs` CORE | **SETTLED (A11).** Decision half in `Platform/Notify.cs` (CORE), WinRT half in `Platform/Notify.Host.cs` (SHELL) — the chapters already agreed on the split; this is the rename that settles the file names |
| 12 | **The stage** | **21**: `Shell/Stage.cs` (400) + `Shell/Stage.UI.cs` (1,150) — `StageChrome` + `StageIdentity` + `StagePanes` + `StageLayout` + `StageArm` + `StagePane` | **22**: `Shell/Lyrics.Stage.UI.cs` (1,400) | **SETTLED (A12).** `Shell/Stage.cs` + `Stage.UI.cs` (owner K, Wave 4) own `StageChrome`/`StageIdentity`/`StagePanes`/`StageLayout`/`StageArm`/`StageInk`. Ch. 22 drops `Lyrics.Stage.UI.cs`; the lyrics pane inside the stage lives in `Lyrics.UI.cs`. Both chapters restated their numbers: `Stage.cs` 400 → 500 (added `StageInk`), `Lyrics.UI.cs` 2,600 → 3,500 (absorbed the dropped file) |
| 13 | **`VideoRailPanel` / docked cap** | **21** folds the rail body into `Rail.UI.cs` (owner K, Wave 4) | **24** puts the surface in `Shell/Video.UI.cs` (proposed K) while plan §5 gives `Playback.Video.cs` to **H** in Wave 3 | **SETTLED (A13).** The rail body is K's, inside `Rail.UI.cs`; the surface is K's, in `Shell/Video.{cs,UI.cs,Host.cs}`; `Playback/Playback.Video.cs` stays the decode/host only, owner H, Wave 3 — not one pixel of UI. The plan now says so explicitly, as ch. 21 §9.6 asked |
| 14 | **`Diagnostics.UI.cs` budget** | **22** (d) instructs ch. 27 to move `Diagnostics.UI.cs` from 1,700 to 2,300 and add `Diagnostics.LyricsInspector` to its unowned-types table | **27** states no per-file budget at all (one figure, 6,800, for the whole surface) and its table has no lyrics-inspector row | **SETTLED (A14).** `Diagnostics.UI.cs` → 2,300, including the lyrics inspector (≈600); the row is now in ch. 27's table |
| 15 | **`Home` file set** | **10**: the family is `Home.cs` ≈1,800 · `Home.UI.cs` ≈3,500 · `Home.Page.cs` ≈2,500 — three files | **11**: `Home.Cards.UI.cs` ~1,250 · `Home.UI.cs` ~1,000 · `Home.Artists.UI.cs` ~650 · `Home.cs` ~450 — five files; **12** adds `Home.Customizer.cs` and `Home.Host.cs` | **SETTLED (A15).** Ch. 11's five files plus ch. 12's two, the finest split of the three — the only one with a SHELL file, and Home had none before: `Home.cs`, `Home.UI.cs`, `+Home.Cards.UI.cs`, `+Home.Artists.UI.cs`, `Home.Page.cs`, `Home.Customizer.cs`, `Home.Host.cs`. Owner P |
| 16 | **`Platform/Controls.cs`'s real size** | **00**: 4,500-6,000, "will pass its budget by far more than 30 %; it needs named partials on day one" (`Controls.Cta.cs`, `Controls.Art.cs`, `Controls.Picker.cs`) | **02**: 3,200-3,800 for *its* surface alone with 01's primitives excluded; **01** adds a 900 share against "a ~1,200-line budget" | **SETTLED (A16).** Named partials on day one: `Controls.cs`, `+Controls.Cta.cs`, `+Controls.Art.cs`, `+Controls.Picker.cs`. Ch. 00's 4,500-6,000 is the **envelope**; chapters 02's and 01's shares sit inside it, not beside it |
| 17 | **Plan §2's own `Shell/` subtotal** | the tree header: "13 files ~21,600" | its own per-file numbers sum to **26,800** (`Entities/` likewise: "40 files ~23,000" against 36 files summing to 24,000) | **SETTLED (A17).** Every subtotal in the plan's §2 is now recomputed from its own rows: `Shell/` is 27 files / 56,200, `Entities/` is 57 files / 58,970 → **59,050** after §9.6 Q4 (2026-09-12, the album merch table), `Screens/` is 23 files / 20,200 → **22 files / 19,000** after §9.6 Q7 deletes `+Diagnostics.Api.cs` and Q8 folds `SetupSession` into `Setup.cs`, and the whole tree is 137 files / 171,135 → **136 files / 170,015** |

---

## 7. Unused chapter numbers

Stated explicitly so a missing number never reads as a lost chapter.

- **00 through 31 are all present and accounted for** — 32 chapter files, no gaps in the sequence. There is no
  missing chapter between any two numbers in the table above.
- **`90-coverage-gaps-1.md` is not a chapter, and is now a tombstone.** It was a critic sweep of 0.2.9 files no
  chapter author opened, written in the chapters' own heading vocabulary so its blocks could be pasted into the named
  chapter. Sweep 1 (`Features/Auth/QrDump.cs`, 93 lines, with its call sites `Program.cs:232-238` and
  `CliRun.cs:15`) was **merged into `28-setup-whatsnew-feedback.md` on 2026-09-12**: it became §1.5 (the old §1.5
  "the 0.3 tree" renumbering to §1.6), **W27**, §3.5, a sentence in §4.2, a line in §5, §6.6, a paragraph in §7, a
  row in §8, a trap in §9.2, corrections to §9.3.1 and §9.5, **parity items 88-90**, and an audit-log entry in §11.
  The file is kept as a **5-line tombstone** so the number never reads as a lost chapter, and so a future sweep does
  not re-file work already merged. The `9x` range stays reserved for further sweeps.
- **Unused: 32 through 89, and 91 upward.** Nothing was ever numbered there, nothing was deleted from there, and a
  new chapter should take the next free number in the `2x`/`3x` run rather than the `9x` range.
- **Within-chapter wireframe numbering is not always dense** and that is not a gap either: `19-shell-overlays.md`
  runs W1-W34 with W10/W11/W12 under a single combined heading and no separate W13; `18-shell-frame.md` carries
  W10b and W12b; `08`, `24`, `22`, `26` and `28` likewise use `b` suffixes, `14-os-surfaces.md` carries W23b, and
  `29-cross-cutting.md` carries **W5B** (the five derived-colour surfaces its first pass missed). The wireframe
  counts in §2 count **headings in §2**, so a combined heading counts once — the distinct-wireframe count for 19 is
  34, for 18 it is 26. Chapter 30 is the one chapter whose §2 headings are not all wireframes: five of its 34
  headings are the `2.0`-`2.4` prose sub-sections, so its wireframe count is **29** (W1-W29).

---

## 8. Totals, in one place

| | |
|---|--:|
| Chapters | **32** (00-31), all complete, plus one merged-sweep tombstone |
| Chapter lines on disk | **56,264** (`wc -l`, recounted 2026-09-12 — 56,135 after this pass's consistency corrections, **+129 from the same day's later §9.6 answers pass and its verification sweep**; agrees with `wavee-0.3-implementation.md` §9.2) |
| Longest / shortest chapter | **3,801** (29) / **988** (20) |
| User-visible surfaces mapped | **119** |
| Routes in `ShellRoutes` (16 exact + 10 prefix families + 3 concert) | **29** (+1 renderable-but-unregistered: `connect-diagnostics`) |
| Wireframes (§2 frames, all 32 chapters) | **792** |
| Parity items (§10, all 32 checklists) | **2,650** |
| Largest / smallest parity checklist | **123** (29) / **56** (31) |
| Honest line estimate, raw column sum | **156,771** |
| Honest line estimate, de-duplicated | **≈141,000-143,000** |
| Plan §2 budget for the same surfaces | **64,900** |
| Delta | **+91,871 raw · ≈+76,000 de-duplicated (≈2.2×)** |
| Files chapters propose that plan §2 lacks (2026-09-11 record; all now resolved, §4) | **46** |
| Surfaces with no owner or no wave slot on 2026-09-11 (now settled — 23 rows in plan §9.5, §5) | **20** |
| Contradictions between chapters (all now settled — plan §9.4 A1-A17, §6) | **17** |
| Chapters still `UNVERIFIED` (no §9 estimate or no §10 checklist) | **0** |

**Answers, 2026-09-12.** None of the rows above move — they describe the chapters' own content, not the plan's
tree. What moves is `wavee-0.3-implementation.md` §2's tree, tracked in §6 row 17 of this index: **136 files /
170,015 lines** (was 137 / 171,135), `Entities/` **59,050** (Q4, +80), `Screens/` **22 files / 19,000** (Q7 −1,400,
Q8 +200).

---

## 9. Revision log

- **2026-09-11.** First pass. §2's rows carried PROPOSED files and "not in §2" notes wherever a chapter's file had
  no home in the plan's pre-arbitration tree; §5 listed 20 surfaces with no owner or wave slot; §6 listed 17
  cross-chapter contradictions as open; §3/§4 recorded the honest-line-estimate and proposed-file catalog that
  justified raising the plan's budget.
- **consistency 2026-09-12: reconciled with the settled plan** — `wavee-0.3-implementation.md` was rewritten to
  settle 18 arbitrations (A1-A18, its §9.4) and carry the final 137-file tree (§2; 136 after §9.6 Q7, 2026-09-12) and wave assignments (§5,
  including the new Wave 4.5). This pass updates every §2.1-§2.8 row to the plan's settled file/owner/wave, rewrites
  §5 as a closed record (20 rows → the plan's 23-row §9.5 table) and §6 as a settled record (all 17 rows resolved),
  keeps §3 and §4 as the 2026-09-11 historical counts the plan quotes by their original numbers, corrects the
  longest/shortest chapter line counts (3,787→3,801 for ch. 29; 986→988 for ch. 20 — both stale `wc -l` figures; the
  32-chapter total of 56,135 was already correct and unchanged), and leaves two homes open exactly as the plan
  itself leaves them open: the friends panel's data split (plan §9.6 Q5) and the `connect-diagnostics` route fix,
  which needs a GitHub issue filed first (plan §9.6 Q6). No chapter file, wireframe count, parity count or line
  estimate was changed — those are this index's own measurements, per the task that produced this pass.
- **answers 2026-09-12 (Christos): all nine of §9.6's open questions are decided.** `wavee-0.3-implementation.md`
  §9.6 is rewritten from "Open questions" to an answered record; every consequence is applied here too. §2.1's API
  console row is struck (Q7: DELETED — no route, no owner, no wave; the tree drops from 137 to **136** files, and
  every "137-file tree" reference above is now the pre-Q7 figure). §2.1's Connect diagnostics row and §5's matching
  row both now say the issue is drafted, awaiting Christos's approval (Q6). §5's `FriendsPanel` row and §4's merch
  row are both CONFIRMED/ADDED rather than open (Q5, Q4); §5's runtime-provisioning-card row is CONFIRMED (Q9). The
  two "still open" items this log named on 2026-09-12 earlier the same day — the friends split and the
  connect-diagnostics issue — are answered, not merely reconciled: friends is confirmed with no further action
  here, and connect-diagnostics still needs its GitHub issue filed, now recorded as drafted rather than absent.
  Chapter 22's lyrics debug pill (Q1) and chapter 14's two toast fixes (Q2, Q3) touch no row in this index — they
  are pill/toast behaviour inside surfaces already mapped, not new surfaces or new homes — so nothing here moves for
  them; see the named chapters and the plan's §9.6 for the record.
