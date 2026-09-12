# Wavee 0.3 — the structure refactor, implementation plan

Status: DRAFT for approval, 2026-09-11. Design record (goals G1-G7, decisions D1-D23, research, estimates):
`C:\Users\ChristosKarapasias\Documents\relationships_wavee.md` — this plan implements it and does not re-argue it.
Base: `release/0.2.9-perf` after Christos's pending commit (D1). Worktree `C:\WAVEE\wavee-0.3`, branch
`feat/0.3-structure`. `feat/catalog-state` is abandoned (D2).

Revision, 2026-09-12: this revision folds in the **UI fidelity contract** — the 32 chapters in
`docs/plans/wavee/wavee-0.3-ui/` (56,051 lines, 119 surfaces, 792 wireframes, 2,650 parity items), written from the
0.2.9 code before Wave 0 moves it. §2, §4.11, §5, §6, §7 and §8 are rewritten against them; §9 is new and carries the
arbitrations. The base commit and the branch are unchanged.

This is a rebuild in place, not an incremental migration: CLAUDE.md says "no legacy paths, replace outright,
breaking is acceptable", and the fold to one assembly (D3) breaks every namespace anyway. The app does not run
between Wave 0 and the end of Wave 5. Every wave is unit-test green on its own core files before the next starts;
the old tree stays in the worktree, excluded from compilation, until Wave 6 deletes it. Rollback is the branch.

---

## 1. Scope

| In | Out |
|---|---|
| Fold `Wavee.Core` into `Wavee.csproj`; new 8-folder tree (D18); every file `<Type>.<Concern>.cs` (D13) | `Wavee.Sdk`, `modules/*`, `vendor/NVorbis` (unchanged, D3) |
| Entities as handles over slab columns, edges as CSR tables, engine `StringId` text, one table set per scope (D5-D10, D13, Q13/Q14) | Visual redesign of any page. The designs are written down: the 0.2.9 look is reproduced surface by surface against the **UI fidelity contract** (§9, `docs/plans/wavee/wavee-0.3-ui/`), and each chapter's §10 parity checklist — 2,650 items across 32 checklists — is the proof it was reproduced, not a claim that it was |
| One graph: no hydration, no store/merge, no query layer; `known` bitmask + authority per column group (D16) | `src/apps/Wavee.PlayPlay` (private junction; the release script asserts it as before) |
| Playback as a pure reducer + host loop (D14); Connect in `Spotify.Connect.cs` (D19); video kept (D15) | Engine work beyond one addition (`StringTable.Intern(ReadOnlySpan<byte>)`, §3.4) |
| Functional core / imperative shell in every folder (G7); tests on core only (D17) | New features (0.3 ships the 0.2.9 feature set; CHANGELOG lists structure + perf) |
| Performance rules P1-P16 (D20); concurrency C1-C10 (D21-D23) | **Windows high contrast / contrast themes.** Decision, 2026-09-12 (P2): unsupported in 0.2.9 — verified, the only `highContrast` strings under `src/apps/Wavee` are keys in Spotify's cover-colour grading payload (`SpotifyLive/CoverColorFiller.cs:18,76,81`, `Design/WaveePalette.cs:140`), and no code reads a Windows contrast theme — and out of scope for 0.3. Stated here as a decision, not left as a silence |

Definition of done (§8): Debug + Release build clean, `Wavee.Tests` green, `--fake` renders every route's loaded
state, live login plays a track and follows a Connect transfer, perf tour numbers beat 0.2.9 on working set and cold
reveal, every chapter's parity checklist recorded green against the golden 0.2.9 build, the release rehearsal
(`wavee-release.ps1 -DryRun`) passes.

---

## 2. The tree (final, chapter-derived)

Every line budget below is the number the UI fidelity contract's chapters wrote for that file (§9). `plan` cites the
pre-count estimate this section used to carry and that no chapter contradicts. `DERIVED` marks a split the
orchestrator made from a chapter total — the total is the chapter's, the split is not. `UNVERIFIED` marks a number
no chapter states. A `+` prefix marks a **named partial declared on day one**: Wave 0 creates it empty, so no owner
invents a file mid-wave against a frozen skeleton.

### `App.cs`

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|--:|---|
| `App.cs` | `Main`, window, composition order, GC latency mode, `Glyphs` before `FluentAppHarness.Run` (ch 00 §9.5) | — | 0 | 400 | plan |

### `Entities/` — 57 files, **59,050**

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|--:|---|
| `Entities.cs` | kinds, uri parse, columns/slabs, `StringId`, scope sets, signals, factories; `AllocRun` + `Authority.Seed` | A | 1 | 1,240 | plan 1,200 + ch 31 §9.4 (40) |
| `Edges.cs` | CSR edge tables + payload structs + reverse index; `Insert`/`Remove`/`Settle` (ch 07), `ReplaceRun` (ch 31); **`MerchTable` (`Name`, `Price`, `ImageId`, `ShopUrl`) + `EdgeTable<NoEdge> AlbumMerch`** (§9.6 Q4, 2026-09-12: added, ch 05 D8) | B | 1 | 980 | plan 800 + ch 07/31 (DERIVED) + 80 UNVERIFIED for the merch table/edge (ch 05 D8 gives a shape, not a line count) |
| `Store.cs` | sqlite schema, `Ensure(span)`, write-behind, GC | C | 1 | 1,200 | plan |
| `Fetch.cs` | `wanted & ~known & ~inflight` planner, batches, epoch, provider switch | C | 1 | 700 | plan |
| `Palette.cs` | **CORE (A5)** — the per-image graded schemes as columns, TTL, key identity, `Watch`, `Ensure`. Lands in Wave 1 because five chapters' grounds depend on it | A | 1 | 400 | ch 07 §9 |
| `Palette.Host.cs` | **SHELL (A5)** — the debounced pump, the filler, the tone plane's mount | L | 4 | 250 | DERIVED (ch 00 §9.5: the pump + filler half of `CoverColorPlane` 556, plus the tone plane's mount) |
| `Detail.cs` | **CORE (A1)** — breakpoints, vertical-layout arithmetic, rail policy, notice rules, reveal ramp, header merge, **`ContextBandLayout`**, the per-kind config table | M | 4.5 | 900 | ch 03 §9 |
| `Detail.UI.cs` | **UI (A1)** — `Frame`, `Hero`, `Rail`, `CompactRail`, **`ContextBand`**, `Skeleton`, `NoticeBar`, `TonePlane`; static functions over handles | M | 4.5 | 2,600 | ch 03 §9 |
| `Track.cs` | **CORE (A2)** — handle, columns, field groups, and every pure rule of the surface: lane table, tier + relief ladders, density/art ladders, sort cycle, filter model, reorder rules, membership diff, reveal ramp, list state, expanded facts, the shared number formats | M | 4.5 | 1,200 | ch 01 §9 |
| `Track.UI.cs` | **(A2)** the ROW and nothing else: grid + 11 lanes, the `#` state machine, both skins, the metadata line, the cells, `ArtCard`, the 14 call-site variants, the track menu composition | M | 4.5 | 2,600 | ch 01 §9 |
| `Track.Table.cs` | **(A2)** the detail table: chrome, command bar, header, tiers, list arms, choreography, drag, selection, recs, filters, the drawer's mount. Owner O configures it through `TableProfile` and never edits it | M | 4.5 | 2,600 | ch 01 §9 / ch 04 §9 |
| `Track.Drawer.cs` | **(A2)** the expanded-row drawer body: facts strip, versions, gutter rail, waveform, format ladder, credits cache | M | 5 | 700 | ch 01 §9 |
| `Album.cs` | columns for identity / release / publishing / prerelease, the flags, five ported pure rules | M | 5 | 400 | ch 05 §9 |
| `Album.UI.cs` | face pile, cover, eyebrow, stat tiles, trailing rows, versions menu, the artist-page drawer panel | M | 5 | 800 | ch 05 §9 |
| `Album.Page.cs` | the two arms, the release panel, the trailing block (ch 03's +700 sits inside this row, not beside it) **and the whole `prerelease:` surface** — the route, the kind-138 resolve, the countdown card, the pre-save heart swap, the greyed pending rows, the "N of M songs" tile | M | 5 | 1,500 | ch 05 §9 |
| `Artist.cs` | columns, flags, the handle, the ported pure rules, commit-time derivations | N | 5 | 550 | ch 08 §9 |
| `Artist.UI.cs` | hero, chart, pick, banners, shelves, lightbox, the two layout rules. Ch 08 §9 asks that it be split with a named partial (`+Artist.UI.Chart.cs`) rather than run to 2k — owner N names it on day one of Wave 5 (§8 G4), inside this 1,500 | N | 5 | 1,500 | ch 08 §9 |
| `Artist.Page.cs` | composition, sections, readiness, band wiring | N | 5 | 2,200 | ch 08 §9 |
| `Artist.Discography.cs` | the `disco:` facet page, virtual grid, album drawer, verdict, era bands — a deep-linkable route, and in the route table (§4.11) | N | 5 | 1,300 | ch 08 §9 |
| `Playlist.cs` | columns + the seven ported rule sets + the notice column; the local-file display-title rules (`LocalPlayables`, 165) | O | 5 | 900 | ch 06 §9 (+ ch 15, DERIVED) |
| `Playlist.UI.cs` | cover/title/description editors, owner block, collaborator pile, invite + access flyout, picker, chips, insertion preview | O | 5 | 1,400 | ch 06 §9 |
| `Playlist.Page.cs` | page, rail/hero configuration, list configuration, insertion/drop/reorder, recs, tune, menus — **and the `local` route's `DetailKind.Playlist` arm** (`wavee:local:all`) | O | 5 | 2,300 | ch 06 §9 (mid of 2,100-2,500) |
| `Show.cs` | show columns + rules | M | 5 | 180 | ch 09 §9.5 |
| `Show.UI.cs` | show cells and rows | M | 5 | 320 | ch 09 §9.5 |
| `Show.Page.cs` | the show arm of the shared frame (episodes instead of tracks) | M | 5 | 700 | ch 09 §9.5 |
| `Episode.cs` | episode columns + `Episode.Rules` | M | 5 | 200 | ch 09 §9.5 |
| `Episode.UI.cs` | the episode row and its variants | M | 5 | 400 | ch 09 §9.5 |
| `User.cs` | **CORE (A3)** — the library edges over the account slot, the search matcher, the library pure rules | O | 5 | 800 | ch 07 §9's settled `User.*` plan (ch 15's content) |
| `User.UI.cs` | **(A3)** library rows, the count pill, the sort/filter flyout | O | 5 | 600 | ch 15 §9 |
| `User.Page.Library.cs` | **(A3)** the `albums` / `artists` / `podcasts` master-detail page | O | 5 | 1,300 | ch 15 §9 |
| `User.Page.Liked.cs` | **(A3)** the `liked` page's own composition of the shared detail frame: the rail/hero bento hosts, the lens header's placement, the chip bar, the list footer arm for `PageHero` | O | 5 | 700 | ch 07 §9's settled `User.*` plan |
| `User.Liked.cs` | **CORE (A3)** — `LikedCoverRules` + `ContentFilterTags` + the liked uri identity | O | 5 | 500 | ch 07 §9 |
| `User.Facts.cs` | **CORE (A3)** — `LikedFactsRules`, verbatim | O | 5 | 1,000 | ch 07 §9 |
| `User.Facts.UI.cs` | **(A3)** the facts bento: panel, week / years / tempo / artists / blend / rediscover cards, pills, since-line, lens header | O | 5 | 1,800 | ch 07 §9 |
| `User.Cover.cs` | **(A3)** the nine cover treatments, the four palette leaves, the heart geometry, the cover component, the picker + flyout | O | 5 | 1,700 | ch 07 §9 |
| `Home.cs` | **CORE (A15)** — `Home.Layout`, the gate, both projections, both estimators, the hero geometry, the wash, the artist-row / timeline / play rules | P | 5 | 1,800 | ch 10 §9 |
| `Home.UI.cs` | **(A15)** row shell, greeting, facet chips, hero band, module shells, the Fold tile, the timeline | P | 5 | 1,600 | ch 10 §9's settled seven-file Home table (ch 10 600 + ch 11 1,000) |
| `+Home.Cards.UI.cs` | **(A15)** the card skins | P | 5 | 1,250 | ch 11 §9.6 |
| `+Home.Artists.UI.cs` | **(A15)** the artist podium + Mixview | P | 5 | 650 | ch 11 §9.6 |
| `Home.Page.cs` | **(A15)** the landing page, the extent table, **and `Home.SectionPage`** — one class serving `home-section:` and `browse-section:`, switching on the PREFIX and never on the section's own uri | P | 5 | 2,230 | ch 10 §9's settled seven-file Home table (ch 10 1,750 + ch 12 480) |
| `Home.Customizer.cs` | **(A15)** the `home-customize` page + its model, reducer and commands | P | 5 | 300 | ch 10 §9's settled seven-file Home table (ch 12's 300) |
| `Home.Host.cs` | **(A15) SHELL** — `home-layout.json` off the UI thread. There is no other SHELL file for Home | P | 5 | 400 | ch 10 §9's settled seven-file Home table (ch 12's 400: the store, its faults, the `.bak` recovery) |
| `Search.cs` | `OmnibarSuggestQuery`, `SearchChipSkeletonPolicy`, the facet / fallback / column rules | P | 5 | 450 | ch 13 §9.8 |
| `Search.UI.cs` | rows, grids, chrome, hero, genre tiles, facet grid, the popup rows | P | 5 | 1,100 | ch 13 §9.8 |
| `Search.Page.cs` | page shell, chip bar, shimmers, dispatch | P | 5 | 550 | ch 13 §9.8 |
| `Browse.cs` | `BrowseTaxonomy`, `BrowseDirectorySeeds`, `BrowsePageLayout`, `BrowseLayout`, `BrowseMastheadMetrics` | P | 5 | 500 | ch 13 §9.8 |
| `Browse.UI.cs` | `BrowseTiles`, the directory body and bands | P | 5 | 650 | ch 13 §9.8 |
| `Browse.Page.cs` | `BrowsePage` + `BrowseDirectoryPage` + `BrowsePageHost` — 857 lines of 0.2.9 with no home before this revision | P | 5 | 700 | ch 13 §9.8 |
| `Concert.cs` | the 807 ported pure rules + columns / handle / fields | N | 5 | 1,050 | ch 17 §9 |
| `Concert.UI.cs` | tiles, date blocks, pills, split hero, flyout panels, picker | N | 5 | 900 | ch 17 §9 |
| `Concert.Page.cs` | hub, filter bar, artist schedule, month board, detail, shy pill + ticker + preloader | N | 5 | 1,900 | ch 17 §9 |
| `Queue.cs` | **CORE** — `QueueSlots` + `QueueMovePlan` + `QueueOrder` + the bucket split | Q | 5 | 250 | ch 21 §9.5 |
| `Queue.UI.cs` | the rail panel + the stage pane, one shared row builder, two skins | Q | 5 | 900 | ch 21 §9.5 |
| `Recents.cs` | **CORE** — the whole `RecentsView` rule set (19 classes, §6) | P | 5 | 650 | ch 16 §9.4 |
| `Recents.UI.cs` | rows, headers, cells, drawer | P | 5 | 550 | ch 16 §9.4 |
| `Recents.Page.cs` | shell, semantic zoom, rail, sticky band, accent, calendar surface | P | 5 | 1,150 | ch 16 §9.4 |
| `Entities.Fake.cs` | `Entities.SeedFake()` — SEED-CORE 600 + SEED-SURFACES 500; ch 31 is its specification | Q | 5 | 1,100 | ch 31 §9.4 |

**Two notes on the `User.*` rows.** There is **no profile page and no profile route in 0.2.9**, so
`User.Page.Profile.cs` is deleted from the file set and `RouteKind.User` from §4.11 (A3). And the eight rows are ch 07
§9's *settled `User.*` file plan* (2026-09-12) verbatim — the same eight names A3 arbitrates, with ch 07's own numbers
for the rows it marks ◆ and ch 15's for the rest — summing **8,400**, not the two chapters' headline sum of 6,950
(ch 07 4,900 + ch 15 2,050). Ch 07's per-file table deliberately runs above its own headline and says so; the
per-file table is the number of record.

**Two notes on the entity CORE rows.** (1) Thirteen of the CORE files in the table above — `Track.cs`, `Album.cs`, `Artist.cs`,
`Episode.cs`, `Show.cs`, `Concert.cs`, `Playlist.cs`, `User.cs`, `Queue.cs`, `Home.cs`, `Search.cs`, `Browse.cs`,
`Recents.cs` — are opened in **Wave 1** by owners A and B for their *columns, handles and field groups only*, and
finished in Wave 4.5/5 by the owner named in the row, who adds the ported pure rules and the commit-time
derivations. The Owner/Wave columns name the owner of the **rule set and the budget**; §5's Wave 1 tables name the
owner of the columns. This is the one place in the tree where a file has two owners in two waves, and it is
deliberate: the columns must exist before Wave 2 can decode into them. (2) The line budget in each such row is the
whole file, columns included.

**Two notes from §9.6, both dated 2026-09-12.** **Q4 (album merch, ADDED):** merch is not an entity — "Featured on"
and "Similar albums" are ordinary `EdgeTable<NoEdge>` rows — but it needs its own small `MerchTable` plus
`EdgeTable<NoEdge> AlbumMerch`, both now in `Edges.cs`'s row above, owner B, Wave 1, +80 lines (UNVERIFIED: ch 05
D8 gives the shape, not a count). The album trailing band (`Album.UI.cs`, `Album.Page.cs`) keeps its merch row and
parity items — nothing there changes; only `Edges.cs` grows. **Q5 (friend activity, CONFIRMED, not merely
proposed):** the split this tree already shows — the friends **edge and columns** land in Wave 1 with the other
edges (owner B, inside `Edges.cs`, folded into its existing budget; ch 21 gap G6), the **panel** lands in Wave 4
with the rail (owner K, inside `Rail.UI.cs`) — is confirmed as written. There is no `Entities/Friends.cs`.

### `Spotify/` — 8 files, **9,420** (+ `Protos/`)

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|--:|---|
| `Spotify.cs` | CORE: `Session` struct, Shannon, DH, hashcash, PKCE, base62, dealer frame, request fold | D | 2 | 1,400 | plan |
| `Spotify.Session.cs` | SHELL: AP socket, login5, tokens, dealer loop, audio keys | D | 2 | 1,600 | plan |
| `Spotify.Api.cs` | SHELL: one function per request | F | 2 | 1,800 | plan |
| `Spotify.Decode.cs` | CORE: wire → staging columns; `RecentsList.Group` (ch 16, +150); the bundled-export **offline fixture path** (ch 31, +70) | E | 2 | 1,600 | plan + ch 16/31 (DERIVED split) |
| `+Spotify.Decode.Pathfinder.cs` | CORE: the `Utf8JsonReader` pathfinder folds. Named on day one — the plan's own "if > 2k" now holds | E | 2 | 820 | DERIVED (plan 2,200 + 220) |
| `Spotify.Audio.cs` | SHELL: fileId/format/key/CDN + AES-CTR stream | F | 2 | 1,200 | plan |
| `Spotify.Connect.cs` | SHELL: spirc glue | F | 2 | 400 | plan |
| `Spotify.Telemetry.cs` | SHELL: Gabo, Herodotus | F | 2 | 600 | plan |
| `Protos/` | 42 `.proto` (generated, moved from `SpotifyLive/Protos`) | D | 0 | — | plan |

No chapter of the contract covers `Spotify/`; these are the plan's own numbers plus the two additions the chapters
ask for by name.

### `Playback/` — 5 files, **6,780**

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|--:|---|
| `Playback.cs` | CORE: `State`, `Input`, `Effects`, `Step`, the ownership fold; `SmtcTimelineCoalescer` (ch 14, +67); `TimeFormat` / `LiveRail` / `LiveEdgeState` (ch 20, +120) | G | 3 | 1,790 | plan + ch 14 §9 + ch 20 §9 |
| `Playback.Host.cs` | SHELL: `Post` loop, signals, `Pending`, ticker | G | 3 | 800 | plan |
| `Playback.Audio.cs` | SHELL: pump over `FluentGpu.Media`, `AudioSource` ×5, gapless/crossfade; `SilentSink` for `--fake` (ch 31, +50) | H | 3 | 2,250 | plan + ch 31 §9.4 |
| `Playback.Video.cs` | SHELL: **the decode/host only** (A13). Not one pixel of UI — the four video surfaces are owner K's `Shell/Video.*` | H | 3 | 1,100 | plan |
| `Playback.Os.cs` | SHELL: SMTC bridge, taskbar button + thumbnail toolbar, jump list, app-icon resolver, media keys, power. **Ch 14 is its contract** — 29 wireframes, 58 parity items | H | 3 | 840 | ch 14 §9 (which retracts its own 830; §2 previously said 1,100) |

### `Shell/` — 27 files, **56,200**

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|--:|---|
| `Shell.cs` | CORE: nav, routes, deep links, responsive layout, page-nav recipes, tint ownership, the first-run composite; **`WaveeTipsCore` (A10)**; the **`Shell.History`** section and the play log (ch 16); the in-app update card's arms (ch 14 → ch 19); the player bar's tier + picker rules and the **`Shell.Ui` rail-state section** the bar's four toggles write — `RailOpen`, `Mode`, `RailWidth`, `DockedVideoHeight(+Pinned)`, `ActiveStagePlayable`, `RailFits`, `ImmersiveLyrics`, `Toggle`, `CanFitRail` (`App/ShellUi.cs` 91; ch 20 asks for a home and §2 had none) (ch 20, +230) | I | 4 | 2,100 | ch 18 §9 (1,150 pure) + ch 20 + ch 16 + ch 14 + ch 29 |
| `Shell.UI.cs` | window frame, merged chrome row, tab strip, drill trail, narrow drawer, back/forward flyout, not-found page, network chrome, file-drop target + cue (`LocalFileActions`), **the notification panel and its rows (A9)** | I | 4 | 2,600 | ch 18 §9 (frame UI 3,250, less `+Shell.Masthead.UI.cs`'s 950) + ch 19 §9.4 (300 of its `Shell.UI.cs` +2,000 share — the panel and its rows; the other 1,700 is `+Shell.Overlays.UI.cs`) |
| `+Shell.Masthead.UI.cs` | masthead band, material layer, omnibar + suggestion popup (a cross-owner contract with P's `Search.cs`) | I | 4 | 950 | ch 18 §4 ("each is a named surface, not optional chrome") |
| `+Shell.PlayerBar.UI.cs` | the player bar, seek bar, responsive tiers, the device picker menu **and the device roster it reads** (ch 20 asks for a home; the roster is a read of `Spotify.Connect`'s cluster, not a new store), the four toggles — which write `Shell.Ui`'s rail state, above | I | 4 | 2,000 | ch 20 §9 (1,900-2,100) |
| `+Shell.Overlays.UI.cs` | profile chip + menu + `Play ▸` cascade, logout confirm, play-a-link dialog, the playback-runtime banner and its gate, the digital-signature dialog, teaching tips, the in-app toast decision sites, **and the mount of `+Screens/Setup.UI.Runtime.cs`** (the setup card's body is not here — A18) | I | 4 | 1,700 | ch 19 §9.4 (1,700 of its `Shell.UI.cs` +2,000 share; the note under the table restated it 1,500 → 2,000 when the panel landed) |
| `Shell.Palette.cs` | the **command palette** (Ctrl+K): table + filter + card. §2 previously said 2,400 for 492 lines of 0.2.9 | I | 4 | 600 | ch 19 §9.4 |
| `+Shell.History.UI.cs` | the `history` page | I | 4 | 400 | ch 16 §9.4 |
| `Shell.Host.cs` | SHELL: window, activation, session snapshot, `history.json` / `play-log.json` / `play-recency.json` (ch 16), the `WaveeTips` host (A10), the file-drop hook | I | 4 | 1,000 | plan 800 + ch 16 + ch 29 |
| `Sidebar.cs` | **CORE (A4)** — projection / binder / planner / sources / geometry / drop / selection / edit and every pure rule of §6; **`PinRowRule` lands here (A7)** | J | 4 | 5,000 | ch 26 §9.4 (9,000 less the 4,000 that leaves for `Sidebar.Doc.cs`) |
| `Sidebar.Doc.cs` | **(A4)** the `sidebar-layout.json` document + reducer + wire + migrations + defaults | J | 4 | 4,000 | ch 25 §9 |
| `Sidebar.UI.cs` | **(A4)** the pane and the three designs (Classic / Library V3 / Wavee Curated), the collapsed rail, folder flyout, tree drag cues, multi-select, the "Move to folder…" picker, the section options popover | J | 4 | 7,500 | ch 26 §9.4 |
| `Sidebar.Customizer.UI.cs` | **(A4)** the full-page `sidebar-customize` destination (4,247 lines of `Curated/**`). **Sequenced LAST in the wave** | J | 4 | 4,500 | ch 26 §9.4 |
| `Sidebar.Host.cs` | **(A4) SHELL** — the file I/O and debounce for `sidebar-layout.json`, the store/binder half, the four extension data-source hosts | J | 4 | 2,500 | ch 26 §9.4 (which restates §2's old 800 as ~2,500 by name) |
| `Rail.cs` | CORE: `RailVideoCoupling`, `NpvPlayerCatalog`, `NpvPlayerPrefs`, `NpvDiagnostics` | K | 4 | 200 | ch 21 §9.5 |
| `Rail.UI.cs` | rail frame (docked + floating), header, the docked video cap's body, NPV panel, hero tile, header row, lyrics peek, friends panel, artwork context menu | K | 4 | 1,400 | ch 21 §9.5 |
| `+Rail.Styles.UI.cs` | player-style flyout, thumbnails, art menu, swatches | K | 4 | 600 | ch 21 §9.5 |
| `Stage.cs` | **CORE (A12)** — `StageLayout`, `StageArm`, `StageInk`, `StagePane` | K | 4 | 500 | ch 21 §9.5 (which restates its own earlier 400: it omitted `StageInk`; 578 lines of 0.2.9, so 500 is a floor) |
| `Stage.UI.cs` | **(A12)** `StageChrome`, `StageIdentity`, **all of** `StagePanes` — the pane switcher, the pane box, the stage queue pane's skin and the lyrics pane's FRAME. Owns the ~500 lines chapters 21 and 22 both counted (their bodies stay in `Lyrics.UI.cs`) | K | 4 | 1,150 | ch 21 §9.5 |
| `Deck.cs` | CORE: the 13 model classes ported verbatim + a pure `Fold` + the catalog | K | 4 | 1,500 | ch 23 §9 (1,450-1,600) |
| `Deck.UI.cs` | host, clock, signals, art, gesture, dispatch, the record family | K | 4 | 1,500 | ch 23 §9 (1,450-1,600) |
| `+Deck.Faces.cs` | the eight non-record faces (turntable, Zune, cassette, reel, CD, MD, iPod, VU, Winamp, WMP, canvas) | K | 4 | 2,400 | ch 23 §9 (2,300-2,500) |
| `Lyrics.cs` | CORE: `Fx`, `BlurPolicy`, `SyncGate`, `RowShape`, `MediaClock`, `PeekClock`, `Emphasis`, `Cascade`, `Prefs` | K | 4 | 3,700 | ch 22 §9 |
| `Lyrics.UI.cs` | the view, the row, the frame driver, ticker/stepper, the NPV peek — **and the lyrics pane inside the stage (A12)**; `Lyrics.Stage.UI.cs` is dropped | K | 4 | 3,500 | ch 22 §9, restated under A12 (2,600 + the dropped `Lyrics.Stage.UI.cs`'s 1,400, less the ~500 already inside `Stage.UI.cs`'s 1,150) |
| `Lyrics.Host.cs` | SHELL: the fetch aggregator, sources, rerank, disk cache, upgrades, the per-track diagnostics store | K | 4 | 2,300 | ch 22 §9 |
| `Video.cs` | **CORE (A13)** — the nine pure rule classes and the placement state machine (`PlacementCore` + `PlacementState`, the home ch 20 and ch 24 both ask for) | K | 4 | 900 | ch 24 §9 |
| `Video.UI.cs` | **(A13)** the docked cap surface, in-window PiP (8 resize zones), the fullscreen surface, the watch stage, the placement menu | K | 4 | 1,450 | ch 24 §9 |
| `Video.Host.cs` | **(A13) SHELL** — the pop-out window (own HWND, borderless fullscreen), the `IDetachedVideoWindow` seam | K | 4 | 250 | ch 24 §9 |

### `Screens/` — 22 files, **19,000**

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|--:|---|
| `Settings.cs` | CORE: `SettingsCatalog`, `SettingsGlyphs`, the tab model | R | 6 | 700 | ch 27 §9.3 |
| `Settings.UI.cs` | the page shell, tabs, General, Notifications, the Logs mount | R | 6 | 1,200 | DERIVED (ch 27 §9.3's 3,400, across its own four named partials) |
| `+Settings.UI.Appearance.cs` | the Appearance tab: three expanders, four collapsed picker groups, six writers, the preview cards | R | 6 | 700 | DERIVED (ch 27 §9.3 / ch 30 §9.4) |
| `+Settings.UI.Playback.cs` | the Playback tab: equalizer, crossfade, the video-overrides card | R | 6 | 800 | DERIVED (ch 27 §9.3) |
| `+Settings.UI.Storage.cs` | the Storage tab: the hue set, bar segments, row accents | R | 6 | 350 | DERIVED (ch 27 §9.3) |
| `+Settings.UI.About.cs` | the About tab: receipts, the GPU line, the crash-reports card mount | R | 6 | 350 | DERIVED (ch 27 §9.3) |
| `+Settings.UI.Video.cs` | the video-override manager flyout **body** — written by K in Wave 4, mounted by R's Playback tab in Wave 6 | K | 4 | 280 | ch 24 §9 |
| `Settings.Host.cs` | SHELL: the settings store | R | 6 | 700 | ch 27 §9.3 |
| `Diagnostics.cs` | CORE: `LogView`, `LogCapturePolicy`, `BuildReport`, `GpuSummary` / `ClassifySharedIgpu` / `FormatBytes` / `FormatUptime`, `Diagnostics.StageRects` | S | 6 | 600 | ch 27 §9.3 |
| `Diagnostics.UI.cs` | **(A14)** the two diagnostics pages (runtime, Connect — ~~the API console page~~ **DELETED, §9.6 Q7, 2026-09-12**), the logs panel + log view, the FPS overlay, **and the lyrics inspector dialog (≈600, moved here by ch 22 (d))** | S | 6 | 2,300 | ch 27 §9.3 (1,700) + ch 22 §9 (d) (+600) — the 1,700 figure predates Q7 and is not reduced here; ch 27 owns the precise re-split |
| `Diagnostics.Host.cs` | SHELL: `WaveeLogSessions`, rolled-file discovery, export, `CrashReportFiles.List` | S | 6 | 500 | ch 27 §9.3 |
| `Diagnostics.Probe.cs` | the CLI probe arms: `--perf-bench`, `--startup-bench`, `--crash-probe`, **`--lyrics-advance-probe`** (ch 22 (a) — the env-var switch is deleted), `--qr-dump` (ch 28 §1.5), `NotificationSimulator` (ch 14, +195), the process receipts | S | 6 | 800 | ch 27 §9.3 (400) + ch 14 §9 (195) + ch 28 (93) + ch 22 (DERIVED) |
| `Setup.cs` | CORE: `SetupGating`, `SetupCommands`, `SetupEntryPoint`, `SetupLayout`, both presentations, `SetupBootstrap`, `QrPlate`, `Qr`, `WaveeLottieRecolor`, **`SetupSession`/`SetupSession.MarkerEpoch`** (the process-static marker epoch the app root subscribes to — §9.6 Q8, 2026-09-12: orchestrator's call, no chapter names a home) | R | 6 | 1,150 | ch 28 §9.5 + §9.6 Q8 (DERIVED: 950 + 200 for `SetupSession`, 324 lines of 0.2.9 ported and folded) |
| `Setup.UI.cs` | the wizard (Terms / Sign in / Local playback), `QrGrid`, `LoginView` | R | 6 | 1,250 | ch 28 §9.5 |
| `+Setup.UI.Runtime.cs` | **(A18)** the 10-phase runtime provisioning card BODY (`PlaybackRuntimeSetupCard` 1,030), rendered by the shell's banner in Wave 4 **and** by the wizard's Local-playback page in Wave 6 — **written by I in Wave 4, mounted by R in Wave 6** (the `Settings.UI.Video.cs` precedent, inverted) | I | 4 | 1,100 | ch 19 §9.4 (which budgets it 1,100 under `Screens/Setup.UI.cs`) |
| `Setup.Host.cs` | SHELL: the runtime provisioning half — §2 had no file for it | R | 6 | ≈450 UNVERIFIED | ch 19 §9.5 (proposes the file, states no number) |
| `ReleaseNotes.cs` | CORE: the parsers, links, validation | R | 6 | 1,000 | DERIVED split of ch 28 §9.5's 1,850 |
| `+ReleaseNotes.Model.cs` | CORE: the document + index records. **The named partial ch 28 §9.3.6 asks for**, so `Wavee.ReleaseTool`'s cherry-pick has a name to include (§3.3). **`AppUpdateToasts` is NOT in either file (A8)** — it is `Platform/Notify.cs` | R | 6 | 850 | DERIVED split of ch 28 §9.5's 1,850 (2,000 less `AppUpdateToasts`' 150) |
| `ReleaseNotes.UI.cs` | the What's-new page, the highlight viewer, the after-update dialog | R | 6 | 1,950 | ch 28 §9.5 |
| `ReleaseNotes.Host.cs` | SHELL: `ReleaseNotesStore` | R | 6 | 520 | ch 28 §9.5 |
| `Feedback.cs` | CORE: `ReportKinds`, `ReportBundle`, `IssueFormUrl`, `ReportRedactor`, `ReportKindIndex`, `ReportIdentity` | R | 6 | 650 | ch 28 §9.5 |
| `Feedback.UI.cs` | the report dialog (Bug / Crash), composer, chrome, the crash-reports card | R | 6 | 800 | ch 28 §9.5 |

**Two notes on `Screens/`, both from §9.6, both dated 2026-09-12.** (1) **Q7 deletes `+Diagnostics.Api.cs`
outright** — the four `ApiDebug*` helpers (~1,400 lines) and the console they served (`ApiConsolePage`, 329 lines
of 0.2.9) have no home in 0.3; the row that used to carry them is gone from the table above, not struck, because
this is the plan's own tree (ch 27 keeps its parity items and wireframes struck-through, per Q7's rule that a
chapter's record is never silently deleted). This takes the folder from 23 files/20,200 lines to **22 files/19,000
lines**. (2) **Q8 folds `SetupSession`/`SetupSession.MarkerEpoch`** (324 lines of 0.2.9, `Features/Setup/SetupSession.cs`
today) into `Setup.cs`, the orchestrator's call under the plan's own rule that the plan decides where code lives —
no chapter named a home for it. `Setup.cs` moves 950 → **1,150**. Net effect on the folder: 20,200 − 1,400 + 200 =
**19,000**.

### `Platform/` — 16 files, **19,165**

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|--:|---|
| `Platform.cs` | CORE: the settings key table + epochs, boot, credentials, `NetworkPolicy`, `ZoomAutoPolicy` (+ `MigrateMode` in `Main`), `AppLocaleBootstrap`, the ambient power policy, `--fake` args + `Clock.SeedEpoch`, the `WaveeLog` seam, **`PlaybackRuntimeSetupModel.Phase`'s enum** (ch 28 §11-21: it must land ONCE, here, not in both the wizard and the shell card) | S | 6 | 1,100 | DERIVED (ch 29 §9.10 share 400 + ch 30 §9.4 plumbing + ch 27 §9.5) |
| `Platform.Host.cs` | SHELL: the Win32 seams, the detached-window owner, the zoom/display bridges | S | 6 | 700 | DERIVED (the plan's `Platform/` bucket) |
| `Prefs.cs` | the four cross-surface preference epochs: `AppearancePrefs`, `LyricsPrefs`, `DetailHeroPrefs`, `NpvPlayerPrefs` | L | 4 | ≈250 UNVERIFIED | ch 00 §9.5 (proposes the file, states no number) |
| `Design.cs` | tokens, type ramp, colour, materials, motion, wash geometry, page-nav motion, reveal ramp, frame time, `MorphKeys`, image decode scale, the four cover-leaf components, `WaveeAccentCtx`, the focus insets (`FocusInsetBordered 2f` / `FocusInsetRow 1f`), the ambient cadence block. **Not `ContextBandLayout` (A1) and not the palette plane (A5)** | L | 4 | 3,000 | ch 00 §9.4 (2,600-3,000) + ch 29 §9.10 (200) |
| `Controls.cs` | **(A16)** the envelope for the whole file family is ch 00's 4,500-6,000, and chapters 02's and 01's shares sit INSIDE it — do not double count. This file: `Surfaces`, `SearchHighlight`, `Equalizer`, `SaveButton`/`PreSaveButton`/`FollowButton`, `SelectionBar`, `RowSwipe`, `ExplicitBadge`, `MoreButton`, `ExpandChevron`, the three dialog helpers, the vacancy grammar, `Notify.Say` | L | 4 | 2,000 | DERIVED split of ch 00 §9.4's 5,250 midpoint |
| `+Controls.Cta.cs` | **(A16)** `WaveeCta` and the CTA ladders | L | 4 | 1,000 | DERIVED |
| `+Controls.Art.cs` | **(A16)** `MediaCard`, `PagedShelf`, chips, stat tiles, countdowns, face piles, rich text, the shimmer | L | 4 | 1,400 | DERIVED |
| `+Controls.Picker.cs` | **(A16)** `WaveePicker`, `WaveeEqualizerCurve` | L | 4 | 850 | DERIVED |
| `Drag.cs` | **(A6)** the payload record, the chip resolver, every drop rule, the insertion preview. **Shipped in the FIRST week of Wave 4** — owners I (tab spring-load) and J (the five sidebar drop cues) consume its rule tables | L | 4 | 700 | ch 01 §9 / ch 29 §9.10 |
| `Actions.cs` | **CORE (A7)** — the descriptor, the targeting matrix, the seven reasons, the one action table, the thirteen first-party descriptors, the extension registry | I | 4 | 950 | ch 29 §9.10 (`Actions.cs` 700 + `Actions.Table.cs` 250) |
| `Actions.UI.cs` | **(A7)** the menu vocabulary, `ActionIcons`, the action row, the picker, the reason caption. **Landed before owner J's picker** | I | 4 | 1,200 | ch 29 §9.10 (`Actions.Menus.cs` 900 + `Actions.UI.cs` 300) |
| `Notify.cs` | **CORE (A9)** — `NotifyLevel`/`NotifyTopic`/`QuietHours`, `NotificationPolicy`, `NotificationPrefs`, the escalation plan, merge / filter / read-state, the notification table and its decode, **and `AppUpdateToasts` (A8)**; `ToastEscalator`'s decision half (A11) | I | 4 | 1,000 | ch 19 §9.4's `~1,000 CORE`: ch 14 §9 (445: policy, prefs, `AppUpdateToasts`, the escalation plan) + ch 19 §9.4 (555: merge, read-ids, models, `SpotifyNotifications`, `WhatsNew`, the bridge's read-state half) |
| `Notify.Host.cs` | **SHELL (A9)** — WinRT toasts, the Action Center, both schedulers (`DaylistNotifier`, `ReleaseNotifier`), AUMID + the activator, the activation → deep-link hop; `ToastEscalator`'s WinRT half (A11) | I | 4 | 715 | ch 14 §9 (715) — ch 19 §9.4 states in terms that it adds nothing to this file and it "must not re-count it" |
| `Modules.cs` | CORE: the pure `WatchPageModel` + `PlayableLinks` | T | 6 | 400 | ch 09 §9.5 |
| `Modules.UI.cs` | the module page and the watch page | T | 6 | 1,400 | ch 09 §9.5 |
| `Modules.Host.cs` | SHELL: the Sdk host, module items → tables, module playables → `Playback.Open` | T | 6 | 2,500 | ch 09 §9.3 |

### `assets/`

Fonts, `loc/*.json`, deck media, the 16 fixture covers + `liked-songs-300.png` + `assets/spotify/*.json` (1.9 MB,
moved as-is, decoded by `Spotify.Decode.cs`'s offline fixture path — ch 31 §9.5(2)), `assets/lottie/*` and
`assets/whatsnew/*` (ch 28). None of it is re-authored.

### Totals

| Folder | Files | Lines |
|---|--:|--:|
| `App.cs` | 1 | 400 |
| `Entities/` | 57 | 59,050 |
| `Spotify/` | 8 | 9,420 |
| `Playback/` | 5 | 6,780 |
| `Shell/` | 27 | 56,200 |
| `Screens/` | 22 | 19,000 |
| `Platform/` | 16 | 19,165 |
| **Total** | **136** | **170,015** |

**Why these numbers and not the old ones.** §2 used to end "~86 files, ~83-88k lines". That was a **pre-count
estimate**, written before chapters 03 (the shared detail frame), 04 (the detail table), 16 (recents and history),
19 (the shell overlays), 24 (the video surfaces) and 30 (the appearance plumbing) existed as surfaces at all, and it
did not survive its own arithmetic: its `Shell/` header said "13 files ~21,600" against per-file rows summing
**26,800**, and its `Entities/` header said "40 files ~23,000" against 36 files summing **24,000** (A17; index §6
row 17). Every subtotal above is recomputed from its own rows.

Against the contract, the honest de-duplicated figure for the surfaces the chapters map is **≈141,000-143,000**
against the **64,900** this section used to budget for those same surfaces — ≈2.2×. The table above reaches
**170,015** (§9.6 answers, 2026-09-12: 171,135 less `+Diagnostics.Api.cs`'s 1,400 (Q7, deleted), plus 200 for
`SetupSession` folding into `Setup.cs` (Q8), plus 80 for the merch table/edge in `Edges.cs` (Q4)) because it also
carries `Spotify/` (9,420), the non-UI half of `Playback/` (5,940 — the folder's 6,780
less `Playback.Os.cs`, which is ch 14's), the entity infrastructure no chapter covers (`Entities.cs` + `Edges.cs` +
`Store.cs` + `Fetch.cs`, 4,120 — up 80 from Q4's merch addition) and the module host, diagnostics helpers and fake seed the index's per-chapter column
leaves out. Subtract the first three and the UI surface is **≈150,500** — the contract's figure plus the plumbing it
demands by name. The **≈7,300** above the contract's own top-of-range (down from ≈8,700 now that Q7 deletes
`Diagnostics.Api.cs`'s 1,400 outright rather than merely counting it) is the `Modules.Host.cs` 2,500 only ch 09 §9.3
names, `Platform.cs`/`Platform.Host.cs` (1,800) and the rounding the chapters' own ranges carry.

**Partials are named on day one.** Wave 0 creates every `+`-marked file empty. The plan's own rule — a file that
passes its budget by 30 % gets a named partial, not a second type — becomes a rename rather than a budget if it is
applied at +62 % across the whole tree (ch 29 §9.4), so the split is made here, once, in advance, and each Wave 4/5/6
owner adds any further partial they need on the first day of their wave (§8 G4), never mid-wave.

**One consequence for `Wavee.ReleaseTool` (§3.3).** `Screens/ReleaseNotes.cs` is a two-file partial set now that
`AppUpdateToasts` has moved out (A8) and ch 28 §9.3.6's named partial is in the tree above as
`+ReleaseNotes.Model.cs`; the tool's `<Compile Include>` cherry-picks must name **both** or the tool will not build.
Fix the include list in Wave 0, with those two names, not in Wave 6.

Namespaces: one, `Wavee`, for everything (the engine's own `Features/` convention already did this for 538 files).
Types are `static partial class` (subsystems: `Entities`, `Spotify`, `Playback`, `Shell`, `Sidebar`, ...) or
`readonly partial struct` (entity handles: `Track`, `Album`, ...). Pages are `sealed partial class XPage : Component`
inside the `X.Page.cs` partial file of the handle's type (a nested type keeps the folder rule honest).

---

## 3. Project and build changes (Wave 0)

### 3.1 `Wavee.csproj`
- Remove `<ProjectReference Include="..\Wavee.Core\Wavee.Core.csproj" />`; delete `Wavee.Core.csproj`.
- Compile globs: `Entities\**;Spotify\**;Playback\**;Shell\**;Screens\**;Platform\**;App.cs`. The OLD tree
  (`Actions, App, Backend, Components, Design, Diagnostics, Features, Platform(old), SpotifyLive` and the moved
  `Wavee.Core` sources under `_old\`) is moved to `src/apps/_old/` on Wave 0 and is NOT in any glob; it is
  reference material for the porting subagents and is deleted in Wave 6.
- Keep: `RuntimeHostConfigurationOption` block (ConserveMemory=5, ArrayPool caps), PublishAot, the module
  item transform, NVorbis reference, protobuf generation (`Spotify\Protos\*.proto`).
- Add trial knob (P16b), measured in Wave 6: `<RuntimeHostConfigurationOption Include="System.GC.RegionSize" Value="1048576" />`.

### 3.2 `Wavee.Tests.csproj`
- Replace the ~30 `<Compile Include="..\Wavee\...">` cherry-picks and the `Wavee.Core` reference with
  `<ProjectReference Include="..\Wavee\Wavee.csproj" />` (D3, D17). Tests may construct any core type; they never
  start the engine loop. `FluentGpu.WindowsApi` reference stays for the P/Invoke shims some tests use.
- Old test files move to `src/apps/_old/Wavee.Tests/` (not compiled) and are ported per wave (§8).

### 3.3 `Wavee.ReleaseTool.csproj`
It referenced `Wavee.Core` for `ReleaseNotes` parsing only. It gets **two** cherry-picks —
`<Compile Include="..\Wavee\Screens\ReleaseNotes.cs" />` and
`<Compile Include="..\Wavee\Screens\ReleaseNotes.Model.cs" />` — because §2 splits that CORE file into a named
partial pair on day one (ch 28 §9.3.6). Both are pure and engine-free by G7; the tool must not load the engine, so
the include list is exhaustive by name and a third partial added later is a build break in Wave 0, not Wave 6.

### 3.4 Engine (one change, `..\fluent-gpu`, verified there)
`FluentGpu.Foundation.StringTable`: add `public StringId Intern(ReadOnlySpan<byte> utf8)` — probe the existing map
through an `AlternateLookup<ReadOnlySpan<byte>>`-style path (decode to a `stackalloc char[]` for ≤256 bytes, else a
pooled buffer) so a wire title is interned without a temporary `string` unless it is genuinely new. Gate: the
engine's VerticalSlice text suite + a new unit test (same id for the same bytes, no allocation on a hit).

### 3.5 `App.cs`
```csharp
static class App
{
    static void Main()
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;        // P16a, once
        Glyphs.Register();               // wavee-icons.otf PUA + bundled SegoeFluentIcons.ttf — BEFORE the harness runs, or every Fluent-Icons glyph is tofu on Win10 (ch 00 §9.5)
        Platform.Boot();                 // log, credentials, settings, update policy (shell); calls ZoomAutoPolicy.MigrateMode(settings) at settings load, before anything reads appearance.zoom.mode (ch 00 §9.5)
        Entities.Boot(Platform.Scope);   // table set for the last scope, Store thread, Fetch
        Spotify.Boot();                  // session static, signals created once; login later
        Playback.Boot();                 // state, host loop, audio pump, os bridges
        Modules.Boot();
        Shell.Run();                     // window + engine loop; never returns until exit
    }
}
```

---

## 4. The code

Real C# for the load-bearing types. Names are final; bodies marked `…` are the parts a subagent writes by porting
the 0.2.9 logic named in the comment. Engine APIs used are the ones verified on 2026-09-11: `Signal<T>(initial)`,
`.Value`/`.Peek()`, `StringTable.Intern/Resolve/AddRef/Release`, `StringId`, `AppHost.Post(Action)` (drained in
one Batch), `Element` records (`BoxEl`, `TextEl(Prop<string>)`, `ImageEl`), `Component`, `UseSignal/UseEffect`,
`ItemsView.CreateBound<T>(BoundItemsSource<T>, Func<BoundItemScope<T>, Element>, …)`.

### 4.1 `Entities/Entities.cs` — kinds, uris, columns, slabs, scope sets, signals, factories

```csharp
namespace Wavee;

public enum EntityKind : byte { Unknown, Track, Episode, Album, Artist, Playlist, Show, User, Collection, Concert }

/// Parsed once at the wire; never a string comparison downstream. spotify:track:<id> | wavee:local:file:<b64> | wavee:module:<id>:<b64>
public readonly record struct EntityUri(EntityKind Kind, StringId Full, byte Provider /* 0 spotify, 1 local, 2 module, 3 fake */)
{
    public static EntityUri Parse(ReadOnlySpan<byte> utf8) { … /* port EntityUri.KindOf/IdOf from _old/Wavee.Core/Hydration/EntityUri.cs, byte-wise */ }
    public static EntityUri Parse(ReadOnlySpan<char> s) { … }
}

/// One column: a slab that grows x2 and never shrinks (P5). T is unmanaged so the GC sees ONE object per column.
public struct Column<T> where T : unmanaged
{
    T[] _a;
    public Column(int cap) => _a = new T[cap];
    public ref T this[int slot] => ref _a[slot];
    public Span<T> Span => _a;                       // for SIMD scans (P15)
    public void EnsureCapacity(int n) { if (n > _a.Length) Array.Resize(ref _a, Math.Max(n, _a.Length * 2)); }
}

/// Per-kind table skeleton. Bookkeeping columns are the same for every kind (P2 group 3).
public abstract class Table
{
    public int Count;                                 // slots handed out (incl. free)
    public readonly Stack<int> Free = new();           // free-list (P5)
    public Column<uint>  Version;                      // bumps on every write (D8)
    public Column<uint>  Known;                        // bit per column group / field (P3)
    public Column<byte>  Authority;                    // per row: highest authority that wrote identity (D16); per-group variants live in the kind file
    public Column<int>   FetchedAt, Touched;           // seconds since app epoch (P7)
    public Column<uint>  Inflight;                     // fetch epoch that owns this row's request, 0 = none (C7)
    public Column<StringId> Uri;
    public readonly Dictionary<StringId, int> ByUri = new();   // StringId -> slot; the uri string exists once (P6)
    public readonly Signal<uint> Changed = new(0);     // ONE signal per table (D8); value = publication counter

    public int Alloc(StringId uri)
    {
        int slot = Free.Count > 0 ? Free.Pop() : Count++;
        EnsureCapacity(Count); Uri[slot] = uri; Version[slot] = 1; Known[slot] = 0; ByUri[uri] = slot; return slot;
    }
    public void FreeSlot(int slot) { ByUri.Remove(Uri[slot]); Version[slot]++; Known[slot] = 0; Free.Push(slot); }
    protected abstract void EnsureCapacity(int n);
}

/// The table SET for one scope (D9). Switching scope replaces the whole object.
public sealed class Scope
{
    public readonly CatalogScope Key;                  // provider, account, locale, market, tier, explicit (kept as-is from 0.2.9)
    public readonly TrackTable Tracks = new(); public readonly AlbumTable Albums = new(); public readonly ArtistTable Artists = new();
    public readonly PlaylistTable Playlists = new(); public readonly ShowTable Shows = new(); public readonly EpisodeTable Episodes = new();
    public readonly UserTable Users = new(); public readonly ConcertTable Concerts = new();
    public readonly Edges Edges = new();
    public uint Epoch;                                 // bumps on switch; late answers for an older epoch are dropped (C7)
}

public static partial class Entities
{
    public static StringTable Strings => …;            // the ENGINE table (5.6); one interner for app + paint path
    public static Scope Current { get; private set; } = null!;
    public static uint Publication;                    // bumped once per UI drain that changed anything (C3)

    public static void Boot(CatalogScope key) { Current = new Scope(key); Store.Boot(); Fetch.Boot(); Store.Warm(Current); }
    public static void Switch(CatalogScope key)        // D9: locale/market/account changed
    { var old = Current; Current = new Scope(key) { Epoch = old.Epoch + 1 }; Store.Warm(Current); /* old set is garbage: ONE free */ }

    // Factories (D10): the only way to a handle. Allocates an empty row if unseen; never touches sqlite or the network.
    public static Track Track(EntityUri uri) => new(Slot(Current.Tracks, uri));
    public static Album Album(EntityUri uri) => new(Slot(Current.Albums, uri));
    … // Artist, Playlist, Show, Episode, User, Concert
    static int Slot(Table t, EntityUri uri) => t.ByUri.TryGetValue(uri.Full, out int s) ? s : t.Alloc(uri.Full);

    /// Batch API (P4): every page asks for what it wants for a SPAN of handles; the planner decides sqlite vs network.
    public static void Ensure(ReadOnlySpan<Track> rows, TrackFields wanted, FetchPriority p = FetchPriority.Visible)
        => Fetch.Plan(Current, rows, (uint)wanted, p);

    /// Called by Store/Fetch commits ON THE UI THREAD (C1): copy staged columns, bump versions, mark the table changed.
    internal static void Commit(in Staging s) { … /* per kind: for each staged row: if (s.Authority >= tbl.Authority[slot] || group not known) write group, Known |= bits, Version++ */ }
    internal static void Publish() { Publication++; foreach (var t in Current.All) if (t.Dirty) { t.Dirty = false; t.Changed.Value = Publication; } }
}
```

### 4.2 `Entities/Track.cs` — the columns, the handle, the field groups

Field derivation from the 30-field 0.2.9 record (`_old/Wavee.Core/Domain/Models.cs:285`):

| 0.2.9 field | 0.3 | Group |
|---|---|---|
| Id, Uri | `Uri` (StringId), id is a suffix of the string | bookkeeping |
| Title, Artists (ArtistRef[]), Album (AlbumRef), DurationMs, IsExplicit, Image | `Title`, edge `TrackArtists`, `Album` (slot), `DurationMs` (int), flag `Explicit`, `Image` (StringId) | **hot** |
| PlayCount, Year, Availability, AvailableAt, CanonicalUri, Isrc, Source, Origin | `PlayCount` (uint), `Year` (ushort), flag `Playable`/`Unavailable` + `AvailableAt` (int), `Canonical` (slot), `Isrc` (StringId), flags `Local`, `Podcast` | cold |
| TempoBpm, MusicalKey, CamelotCode, CamelotColor, Tags | `Tempo` (ushort ×10), `Key` (byte), `Camelot` (byte), `CamelotColor` (uint), edge `TrackTags` | cold |
| AddedAt, AddedBy, ContextUid, Chart | **not on Track**: `PlaylistTrackEdge` fields (D10) | edge |
| (video presence) | `VideoCounterpart` (slot, 0 = none) + flag `HasVideo` | cold |

```csharp
namespace Wavee;

[Flags] public enum TrackFields : uint
{
    Title = 1 << 0, Artists = 1 << 1, Album = 1 << 2, Duration = 1 << 3, Explicit = 1 << 4, Image = 1 << 5,
    Identity = Title | Artists | Album | Duration | Explicit | Image,          // hot group: one wire shape fills it
    PlayCount = 1 << 8, Year = 1 << 9, Availability = 1 << 10, Isrc = 1 << 11, Canonical = 1 << 12,
    Audio = 1 << 13 /* tempo, key, camelot */, Tags = 1 << 14, Video = 1 << 15, Publishing = 1 << 16,
    Row = Identity | PlayCount | Availability,                                  // what a list row paints
    All = 0x1FFFF,
}
public enum Authority : byte { None = 0, Thin = 1 /* search, playlist item, cluster */, Full = 2 /* TrackV4, getTrack */, Local = 3 /* user edit, local file */ }

public sealed class TrackTable : Table
{
    // hot
    public Column<StringId> Title, Image; public Column<int> Album, DurationMs; public Column<uint> Flags;
    // cold
    public Column<uint> PlayCount, CamelotColor; public Column<ushort> Year, Tempo; public Column<byte> Key, Camelot;
    public Column<int> AvailableAt, Canonical, VideoCounterpart; public Column<StringId> Isrc;
    public Column<byte> IdentityAuthority, ExtrasAuthority;    // D16: authority per column GROUP
    protected override void EnsureCapacity(int n) { … /* every column */ }
}

public readonly partial struct Track(int Slot) : IEquatable<Track>
{
    static TrackTable T => Entities.Current.Tracks;
    public int Slot { get; } = Slot;
    public bool IsValid => Slot > 0 && Slot < T.Count;
    public uint Version => T.Version[Slot];                    // pages compare this across frames (P5 cost 1)
    public bool Knows(TrackFields f) => (T.Known[Slot] & (uint)f) == (uint)f;

    public EntityUri Uri => new(EntityKind.Track, T.Uri[Slot], 0);
    public string Title => Entities.Strings.Resolve(T.Title[Slot]);          // paint path resolves StringId -> string once per glyph run (engine caches)
    public StringId TitleId => T.Title[Slot];
    public Album Album => new(T.Album[Slot]);
    public int DurationMs => T.DurationMs[Slot];
    public bool IsExplicit => (T.Flags[Slot] & TrackFlags.Explicit) != 0;
    public bool IsPlayable => (T.Flags[Slot] & TrackFlags.Unavailable) == 0;
    public ReadOnlySpan<int> ArtistSlots => Entities.Current.Edges.TrackArtists.Targets(Slot);   // CSR, zero alloc
    public ReadOnlySpan<StringId> Tags => Entities.Current.Edges.TrackTags.Payload(Slot);

    public bool Equals(Track o) => o.Slot == Slot; public override int GetHashCode() => Slot;
}
```

### 4.3 `Entities/Edges.cs` — CSR relationship tables with typed edge payloads

```csharp
namespace Wavee;

/// parent slot -> ordered targets, with an unmanaged payload per edge. CSR: Offsets[parent]..Offsets[parent+1] index Targets/Payload.
/// A parent's list is rewritten whole (that is how every relation arrives: a page of a revision), never spliced in place;
/// rewrites reuse the parent's old range when the new one fits, else append (compaction in the store GC tick).
public sealed class EdgeTable<TEdge> where TEdge : unmanaged
{
    public Column<int> Start, Length;      // per parent slot
    public Column<int> Targets;            // child slots, contiguous per parent
    public Column<TEdge> Payload;          // parallel to Targets
    public Column<uint> Version;           // per parent list (bound lists compare it)
    public Column<byte> State;             // 0 unknown, 1 partial (paged), 2 complete  (D7 answer: "partial" IS first-class)
    public Column<int>  Total;             // server-reported total when partial
    int _tail;
    public ReadOnlySpan<int>   Targets(int parent) => Targets.Span.Slice(Start[parent], Length[parent]);
    public ReadOnlySpan<TEdge> Payload(int parent) => Payload.Span.Slice(Start[parent], Length[parent]);
    public void Replace(int parent, ReadOnlySpan<int> targets, ReadOnlySpan<TEdge> payload, byte state, int total) { … }
    public void ReplacePage(int parent, int offset, ReadOnlySpan<int> targets, ReadOnlySpan<TEdge> payload, int total) { … /* grows the list to offset+n, state=partial until Length==Total */ }
    // reverse index for membership questions (liked? in playlist X?) — built lazily per parent kind, invalidated by Replace
    public bool Contains(int parent, int target) { … }
}

public readonly record struct NoEdge;
public readonly record struct AlbumTrackEdge(byte Disc, ushort Number);
public readonly record struct PlaylistTrackEdge(StringId ItemId, int AddedAt, int AddedBy /* user slot */, byte ChartStatus, ushort ChartPos, ushort ChartPrev, byte Flags /* 1 = pending add, 2 = pending remove (C6) */);
public readonly record struct LibraryEdge(int AddedAt, byte Flags /* pending bits (C6) */);
public readonly record struct RootlistEdge(ushort Position, byte Depth, byte Kind /* 0 item 1 folder-start 2 folder-end */, StringId FolderName, int AddedAt);
public readonly record struct QueueEdge(ulong ItemId, byte Provider /* context/queue/autoplay */, byte Bucket /* NowPlaying/UserQueue/NextUp/History */);
public readonly record struct DiscographyEdge(byte Kind /* album/single/compilation/appears-on */);
public readonly record struct FriendEdge(int UserSlot, long TimestampMs, int TrackSlot, int AlbumSlot, int ArtistSlot, int ContextSlot);  // §9.6 Q4/Q5, 2026-09-12

/// Merch is not an entity (§9.6 Q4, 2026-09-12, ch 05 D8): a small side table + a NoEdge edge off the album, not a kind of its own.
public struct Merch { public StringId Name, ImageId, ShopUrl; public uint PriceMinorUnits; }
public sealed class MerchTable { public Column<Merch> Row = new(0); public int Count; public int Alloc() => Count++; }

public sealed class Edges
{
    public readonly EdgeTable<NoEdge>            TrackArtists = new(), AlbumArtists = new(), ArtistRelated = new(), ShowEpisodes = new();
    public readonly EdgeTable<StringId>          TrackTags = new();                    // payload IS the tag id; Targets unused
    public readonly EdgeTable<AlbumTrackEdge>    AlbumTracks = new();
    public readonly EdgeTable<DiscographyEdge>   ArtistReleases = new(), ArtistAppearsOn = new();
    public readonly EdgeTable<NoEdge>            ArtistPopular = new();
    public readonly MerchTable                   Merch = new();                        // §9.6 Q4: the small side table merch rows point into
    public readonly EdgeTable<NoEdge>            AlbumMerch = new();                    // parent = album slot, target = a Merch row's slot
    public readonly EdgeTable<PlaylistTrackEdge> PlaylistTracks = new();
    // the LIBRARY (G6): parent = the user slot
    public readonly EdgeTable<LibraryEdge>       Liked = new(), SavedAlbums = new(), FollowedArtists = new(), SavedShows = new(), Pins = new();
    public readonly EdgeTable<RootlistEdge>      Rootlist = new();
    public readonly EdgeTable<QueueEdge>         Queue = new();                        // parent = the playback session slot (1)
    public readonly EdgeTable<FriendEdge>        Friends = new();                      // §9.6 Q5 CONFIRMED: parent = a synthetic feed subject, per ch 21 gap G6
    public readonly EdgeTable<NoEdge>            HomeSection = new(), SearchResult = new();  // synthetic subjects (Home.cs/Search.cs own the parent slots)
}
```

### 4.4 `Entities/Store.cs` — sqlite as columns (SHELL: its own thread, C9/D22)

```sql
-- one file: %LOCALAPPDATA%\Wavee\library.db (packaged: LocalCache). Schema v3; a v2 file is deleted, not migrated (cache).
CREATE TABLE scope(scope_id INTEGER PRIMARY KEY, provider TEXT, account TEXT, locale TEXT, market TEXT, tier INT, explicit INT, UNIQUE(provider,account,locale,market,tier,explicit));
CREATE TABLE track(scope_id INT, uri TEXT, title TEXT, image TEXT, album_uri TEXT, duration_ms INT, flags INT,
  play_count INT, year INT, available_at INT, canonical_uri TEXT, isrc TEXT, tempo INT, key INT, camelot INT, camelot_color INT, video_uri TEXT,
  known INT, identity_auth INT, extras_auth INT, fetched_at INT, touched INT, PRIMARY KEY(scope_id,uri)) WITHOUT ROWID;
CREATE INDEX ix_track_title ON track(scope_id, title COLLATE NOCASE);          -- P11: real indexed search
CREATE INDEX ix_track_gc    ON track(touched);
-- album, artist, playlist, show, episode, user, concert: same shape, own columns
CREATE TABLE edge(scope_id INT, kind INT, parent TEXT, ordinal INT, child TEXT, payload BLOB, PRIMARY KEY(scope_id,kind,parent,ordinal)) WITHOUT ROWID;
CREATE INDEX ix_edge_child ON edge(scope_id, kind, child);                     -- membership questions offline
CREATE TABLE edge_state(scope_id INT, kind INT, parent TEXT, state INT, total INT, version INT, fetched_at INT, PRIMARY KEY(scope_id,kind,parent)) WITHOUT ROWID;
CREATE TABLE intent(id INTEGER PRIMARY KEY, kind INT, payload BLOB, created_at INT, state INT);   -- C6/D22: synchronous journal
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);                            -- cache_bytes (trigger-maintained), budget, schema
```
```csharp
public static partial class Store
{
    static Thread _thread; static readonly BlockingCollection<Action> _q = new(boundedCapacity: 4096);   // C8
    public static void Boot() { … /* open, EnsureSchema, one write connection + one read connection, start _thread */ }
    /// Cold read for rows the planner found unknown: ONE query per table per drain (P4). Answers arrive as a Staging via Post.
    public static void Read(Scope s, TrackTable t, ReadOnlySpan<int> slots, uint epoch) { … /* SELECT ... WHERE scope_id=? AND uri IN (...) -> Staging -> AppHost.Post(() => { if (s.Epoch==epoch) Entities.Commit(st); Fetch.Continue(...); }) */ }
    /// Write-behind (D22): a committed Staging is enqueued; the thread UPSERTs in one transaction per batch.
    internal static void WriteBehind(Staging s) => _q.Add(() => Upsert(s));
    /// Synchronous journal for user intents (D22) — the ONLY blocking call, from the intent's own shell, never the UI thread.
    public static long Journal(byte kind, ReadOnlySpan<byte> payload) { … }
    /// GC tick (R2): TTL filter, LRU rank, byte budget; bounded batches; deferred while the queue is busy (port CatalogSweepSchedule as a pure function in Store.cs core section).
    static void Sweep() { … }
}
```

### 4.5 `Entities/Fetch.cs` — the planner (CORE) and the request runner (SHELL)

```csharp
public static partial class Fetch
{
    public enum FetchPriority : byte { Prefetch, Visible, Playback }
    // CORE, pure: which rows need which groups, deduped by Inflight (C7). Returns request batches by provider and shape.
    public static void Plan(Scope s, ReadOnlySpan<Track> rows, uint wanted, FetchPriority p)
    {
        var t = s.Tracks; Span<int> unknownOnDisk = stackalloc int[Math.Min(rows.Length, 512)]; int n = 0;
        foreach (var r in rows)
        {
            uint missing = wanted & ~t.Known[r.Slot];
            if (missing == 0 || t.Inflight[r.Slot] == s.Epoch) continue;        // known or already asked this epoch
            t.Inflight[r.Slot] = s.Epoch; t.Touched[r.Slot] = Clock.Now;
            if ((t.Known[r.Slot] & KnownOnDiskProbe) == 0) unknownOnDisk[n++] = r.Slot; else _net.Add(r.Slot, missing, p);
        }
        if (n > 0) Store.Read(s, t, unknownOnDisk[..n], s.Epoch);               // disk first; Continue() re-plans the leftovers for the network
        Kick();
    }
    // SHELL: batches by provider; spotify: 300 uris per extended-metadata POST, pathfinder per subject; local/module: their own openers.
    static void Kick() { … /* group _net by (provider, shape); 4 in flight; backoff per ResourcePolicy (ported, pure) */ }
}
```

### 4.6 `Spotify/Spotify.Decode.cs` — wire → staging (CORE, pure, P14 day one)

```csharp
public static partial class Spotify
{
    public static partial class Decode
    {
        // extended-metadata kinds (ported from _old/Wavee/Backend/Metadata/ExtendedMetadataSource.cs + Projectors/*):
        public static void TrackV4(ReadOnlySpan<byte> proto, ref Staging s) { … /* Title, ArtistUris, AlbumUri, Duration, Explicit, Image, Isrc, Year, Canonical, earliest_live -> Staging.Tracks (authority Full) */ }
        public static void AlbumV4(ReadOnlySpan<byte> proto, ref Staging s) { … /* album identity + disc rows -> AlbumTracks edge page (state complete) + thin track rows */ }
        public static void ArtistV4(…); public static void ShowV4(…); public static void EpisodeV4(…); public static void ListMetadataV2(…);
        public static void AudioAttributes(ReadOnlySpan<byte> k222, ref Staging s); public static void Descriptors(ReadOnlySpan<byte> k6, ref Staging s);
        public static void PlayCount(ReadOnlySpan<byte> k185, …); public static void Publishing(ReadOnlySpan<byte> k183, …); public static void VideoAssociations(ReadOnlySpan<byte> k99, …); public static void VisualIdentity(ReadOnlySpan<byte> k179, …);
        // pathfinder (ported from _old/Wavee.Core/Spotify/SpotifyExportMapper.cs): Utf8JsonReader, never JsonElement (no DOM allocation)
        public static void GetAlbum(ref Utf8JsonReader r, ref Staging s) { … /* albumUnion: album Full + OtherVersions + ArtistsDetailed + tracks page */ }
        public static void GetTrack(ref Utf8JsonReader r, ref Staging s); public static void ArtistOverview(ref Utf8JsonReader r, ref Staging s); public static void Search(ref Utf8JsonReader r, ref Staging s); public static void Home(ref Utf8JsonReader r, ref Staging s);
        // playlist4 / collection (ported from PlaylistWireMapper, CollectionWireMapper)
        public static void PlaylistRevision(ReadOnlySpan<byte> selectedListContent, ref Staging s) { … /* PlaylistTrackEdge page: itemId, addedAt, addedBy, chart */ }
        public static void CollectionPage(ReadOnlySpan<byte> proto, byte set, ref Staging s);
        // connect (5.10.1): cluster -> delta value the Playback core understands; PutState encode from a Playback snapshot
        public static ClusterDelta Cluster(ReadOnlySpan<byte> proto); public static int PutState(in Playback.Snapshot snap, Span<byte> into);
        // text: bytes -> StringId directly (P14)
        static StringId Str(ReadOnlySpan<byte> utf8) => Entities.Strings.Intern(utf8);
    }
}

/// Staging: columns to write, built on the receiving thread (D23), committed on the UI thread. Pooled; cleared after commit.
public struct Staging
{
    public StagedRows<TrackRow> Tracks; public StagedRows<AlbumRow> Albums; … ; public StagedEdges Edges; public byte Authority; public uint Epoch;
}
```

### 4.7 `Playback/Playback.cs` — the reducer (CORE)

```csharp
public static partial class Playback
{
    public enum Phase : byte { Idle, Loading, Playing, Paused, Ended }
    public enum Owner : byte { Nobody, Us, Foreign }
    public struct State
    {
        public Track Current; public QueueCursor Cursor;             // cursor = (bucket, index) into Edges.Queue
        public Phase Phase; public Owner Owner; public int ForeignDeviceSlot;
        public int PosMs; public long PosQpc;                          // position = PosMs + (now - PosQpc) while Playing
        public float Volume; public bool Shuffle; public byte Repeat;
        public long Fence;                                             // server_timestamp_ms fence (C5)
        public uint Epoch;                                             // bumps whenever a shell must abandon in-flight work (C4)
        public uint LoadEpoch, TransferEpoch, PublishSeq;
    }
    public enum InputKind : byte { Play, Pause, Resume, Seek, Next, Prev, SetVolume, SetShuffle, SetRepeat, Transfer, Cluster, RemoteCommand, AudioSignal, Ended, DeviceLost, Suspend, Resume_, Tick }
    public readonly record struct Input(InputKind Kind, Track Track = default, QueueCursor Cursor = default, int IntArg = 0, long LongArg = 0, uint Epoch = 0, ClusterDelta Cluster = default, RemoteCommand Remote = default);
    /// Fixed effect SLOTS (C3): the last write wins inside a drain; Execute reads them once.
    public struct Effects
    {
        public bool Load; public Track LoadTrack; public uint LoadEpoch; public int LoadFromMs;
        public bool Start, Stop, Seek; public int SeekMs;
        public bool PrepareNext; public Track NextTrack;
        public bool PublishState; public byte PublishReason;
        public bool SendRemote; public int RemoteDeviceSlot; public RemoteCommand RemoteCmd;
        public bool Snapshot;
        public void Clear() => this = default;
    }

    public static void Step(ref State s, in Input i, ref Effects fx)
    {
        switch (i.Kind)
        {
            case InputKind.Play:
                s.Current = i.Track; s.Cursor = i.Cursor; s.Phase = Phase.Loading; s.PosMs = 0; s.Owner = Owner.Us;
                s.Epoch++; s.LoadEpoch = s.Epoch;
                fx.Load = true; fx.LoadTrack = i.Track; fx.LoadEpoch = s.Epoch; fx.LoadFromMs = 0; fx.PublishState = true; fx.PublishReason = PublishReason.PlayerStateChanged;
                break;
            case InputKind.Next:                      // ten clicks = ten Steps, one Load effect (the last) — C3/C4
                if (!Queue.TryAdvance(ref s.Cursor, out var next)) { s.Phase = Phase.Ended; fx.Stop = true; break; }
                s.Current = next; s.Phase = Phase.Loading; s.PosMs = 0; s.Epoch++; s.LoadEpoch = s.Epoch;
                fx.Load = true; fx.LoadTrack = next; fx.LoadEpoch = s.Epoch; fx.PrepareNext = Queue.TryPeek(s.Cursor, out fx.NextTrack); fx.PublishState = true;
                break;
            case InputKind.AudioSignal:               // from the pump: (Epoch, IntArg = PumpSignal.Started/Position/Buffering...)
                if (i.Epoch != s.LoadEpoch) break;    // stale (C4)
                … /* Started -> Phase.Playing + PosQpc; Position -> PosMs; Ended -> Input.Next inline */
                break;
            case InputKind.Cluster:                   // ownership fold with fence (ported from _old/Wavee/Backend/PlaybackOwnership.cs, pure)
                var verdict = Ownership.Fold(s.Owner, s.Fence, in i.Cluster, out int foreignSlot);
                if (verdict == Owner.Foreign && s.Owner == Owner.Us) { s.Owner = Owner.Foreign; s.ForeignDeviceSlot = foreignSlot; s.Epoch++; fx.Stop = true; }
                s.Fence = Math.Max(s.Fence, i.Cluster.ServerTimestampMs);
                … /* mirror remote track/position into State so the bar renders it */
                break;
            case InputKind.Transfer:
                s.Epoch++; s.TransferEpoch = s.Epoch; fx.SendRemote = true; fx.RemoteDeviceSlot = i.IntArg; fx.RemoteCmd = RemoteCommand.Transfer(s);
                break;
            … // Pause/Resume/Seek/SetVolume/Shuffle/Repeat/RemoteCommand/DeviceLost/Suspend/Tick
        }
    }
}
```

### 4.8 `Playback/Playback.Host.cs` — the loop (SHELL)

```csharp
public static partial class Playback
{
    static State _s; static Effects _fx; static readonly Queue<Input> _inbox = new();
    public static readonly Signal<Track> Current = new(default);
    public static readonly Signal<int> PositionMs = new(0);
    public static readonly Signal<Phase> PhaseSignal = new(Phase.Idle);
    public static readonly Signal<float> Volume = new(1f);
    public static class Pending { public static readonly Signal<bool> Load = new(false), Transfer = new(false); }   // D21: per-button decision reads these

    /// Thread-safe. The engine drains posts inside one Batch at frame start (C1/C3).
    public static void Post(in Input i) { var copy = i; Shell.Host.Post(() => { Step(ref _s, in copy, ref _fx); Execute(); Publish(); }); }

    static void Execute()
    {
        if (_fx.Load) { Pending.Load.Value = true; Audio.Load(_fx.LoadTrack, _fx.LoadEpoch, _fx.LoadFromMs); }   // cancels the previous epoch inside Audio
        if (_fx.Stop) Audio.Stop();
        if (_fx.Seek) Audio.Seek(_fx.SeekMs);
        if (_fx.PrepareNext) Audio.Prepare(_fx.NextTrack);
        if (_fx.PublishState) Spotify.Connect.PublishState(in _s, _fx.PublishReason);        // Spotify knows Playback; not the reverse (D19)
        if (_fx.SendRemote) { Pending.Transfer.Value = true; Spotify.Connect.Send(_fx.RemoteDeviceSlot, _fx.RemoteCmd, _s.TransferEpoch); }
        if (_fx.Snapshot) Store.SnapshotSession(in _s);
        _fx.Clear();
    }
    static void Publish() { Current.Value = _s.Current; PhaseSignal.Value = _s.Phase; PositionMs.Value = _s.PosMs; Volume.Value = _s.Volume; if (_s.Phase == Phase.Playing) Pending.Load.Value = false; }
    // 1 s position ticker (P10 named timer): Post(Input.Tick) only while Playing
}
```

### 4.9 `Playback/Playback.Audio.cs` — sources and the pump (SHELL)

```csharp
public static partial class Playback
{
    public abstract class AudioSource : IDisposable { public abstract long Length { get; } public abstract int Read(long offset, Span<byte> into); public abstract void Dispose(); }
    // the routing table IS this switch (5.10): no provider registry
    static AudioSource Open(Track t, CancellationToken ct) => t.Uri.Provider switch
    {
        0 => Spotify.Audio.Open(t, ct),               // fileId ladder, key, CDN; AES-CTR stream (ported from _old/Wavee/SpotifyLive/Audio)
        1 => new FileSource(Platform.LocalPath(t)),
        2 => Modules.Host.Open(t.Uri, ct),
        _ => throw new NotSupportedException(),
    };
    public static class Audio
    {
        static CancellationTokenSource? _loadCts; static uint _epoch;
        public static void Load(Track t, uint epoch, int fromMs)
        { _loadCts?.Cancel(); _loadCts = new(); _epoch = epoch; var ct = _loadCts.Token; _pump.Enqueue(() => LoadCore(t, epoch, fromMs, ct)); }   // serialized pump (kept from FluentMediaAudioHost)
        static async Task LoadCore(Track t, uint epoch, int fromMs, CancellationToken ct)
        { var src = Open(t, ct); … /* decode session over FluentGpu.Media PcmAudioPlayer; on first frame: Post(Input.AudioSignal Started, epoch) */ }
        … // Stop, Seek, Prepare(gapless), 200 ms ticker (P10), device-format reload, xrun drain, crossfade commit
    }
}
```

### 4.10 `Spotify/Spotify.Connect.cs` (SHELL) — the glue (D19)

```csharp
public static partial class Spotify
{
    public static class Connect
    {
        internal static void OnDealer(ReadOnlySpan<byte> frame)          // dealer receive thread
        {
            var msg = Spotify.DealerFrame.Parse(frame);                   // core, pure
            if (msg.IsClusterUpdate) { var delta = Decode.Cluster(msg.Payload); Playback.Post(new(Playback.InputKind.Cluster, Cluster: delta)); }
            else if (msg.IsRequest)  { var cmd = Decode.ConnectCommand(msg.Payload); Playback.Post(new(Playback.InputKind.RemoteCommand, Remote: cmd)); Api.Reply(msg.Key, ok: true); }
        }
        internal static void PublishState(in Playback.State s, byte reason) { … /* debounce 50 ms, seq++, Decode.PutState(snapshot) -> Api.PutState; response cluster -> Post(Input.Cluster) so the fence lands (C5) */ }
        internal static void Send(int deviceSlot, RemoteCommand cmd, uint epoch) { … /* Api.PlayerCommand; on completion Post(Input.AudioSignal/TransferDone, epoch) */ }
    }
}
```

### 4.11 `Shell/Shell.cs` — routes and navigation (CORE)

The enum below is the **real** route set, read off `ShellRoutes.s_exact` (16 names in 0.2.9; **15 in 0.3** — `api-console`
is deleted, §9.6 Q7, 2026-09-12), `ShellRoutes.s_prefixes`
(10 prefix families), `ConcertRoutes.TryParse` (3) and `ContentHost.PageFor` (which renders one more key than
`ShellRoutes` registers). One kind per renderable destination, no kind for anything that is not one.

```csharp
public static partial class Shell
{
    /// One kind per destination ContentHost.PageFor can render. 28 registered routes + connect-diagnostics + NotFound
    /// (29 in 0.2.9, less ApiConsole — deleted, §9.6 Q7).
    public enum RouteKind : byte
    {
        // s_exact (15) — ApiConsole DELETED, §9.6 Q7, 2026-09-12: the console and its four ApiDebug* helpers are cut
        Home, Browse, Search, LibraryAlbums, LibraryArtists, LibraryPodcasts, Liked, Local,
        History, Recents, Settings, PlaybackDiagnostics, WhatsNew, SidebarCustomize, HomeCustomize,
        // s_prefixes (10) — "<prefix><entity uri>"; a bare prefix addresses nothing and is NOT a route
        Album, Playlist, Artist, Show, Prerelease, Discography, Module, BrowseCategory, HomeSection, BrowseSection,
        // ConcertRoutes (3)
        Concerts, ArtistConcerts, Concert,
        // renderable by PageFor, NOT in s_exact — see the note below
        ConnectDiagnostics,
        // the fall-through ContentHost paints when nothing above claims the key
        NotFound,
    }

    public readonly record struct Route(RouteKind Kind, EntityUri Subject = default, StringId Arg = default, int Tab = 0);
    public struct Nav { public Route Current; public readonly Stack<Route> Back, Forward; public int Tab; }
    public static void Go(ref Nav n, in Route r) { if (r == n.Current) return; n.Back.Push(n.Current); n.Forward.Clear(); n.Current = r; }
    public static bool BackStep(ref Nav n) { … } public static bool ForwardStep(ref Nav n) { … }
    public static Route DeepLink(ReadOnlySpan<char> uriOrUrl) { … /* wavee:// and open.spotify.com/... -> Route; ported from ShellRoutes/DeepLinks */ }

    /// ONE table, keyed by kind (ch 27 §9.6): (page, isKnown, title, glyph, developerOnly). ShellRoutes.IsKnown,
    /// ShellNav.Dest and ContentHost.PageFor are three READS of this table, never three lists that must stay in step.
    public static ref readonly RouteRow Row(RouteKind k) => ref s_routes[(int)k];
}
```

Dropped from the earlier sketch's enum, one line each:

- **`Episode`** — an episode is a row inside `show:` and a subject of the player. There is no `episode:` prefix in
  `s_prefixes` and no arm in `PageFor`.
- **`Queue`** — the queue is a rail arm and a stage pane (`Queue.UI.cs`), never a destination. No route key.
- **`User`** — there is no profile page and no profile route in 0.2.9; `s_exact` carries `albums`/`artists`/
  `podcasts`/`liked` and nothing else user-shaped (A3, which also deletes `User.Page.Profile.cs`).
- **`Setup`** — the wizard is pre-shell first-run chrome (`Screens/Setup.*`), mounted before the content host exists.
- **`ApiConsole`** — DELETED, §9.6 Q7, 2026-09-12 (not merely dropped from an earlier sketch: it was a real 0.2.9
  route, `api-console`, removed by decision). The API console and the four `ApiDebug*` helpers behind it
  (~1,400 lines) are cut outright; there is no `+Diagnostics.Api.cs` file and no route for it in 0.3.

Added because the sketch missed them: `Local`, `History`, `Recents`, `PlaybackDiagnostics`,
`SidebarCustomize`, `HomeCustomize`, `Prerelease`, `Discography`, `BrowseCategory`, `HomeSection`, `BrowseSection`,
`ArtistConcerts`, `Concert`, `NotFound`, and the three library kinds split out of one `Library`. `WhatsNew` is keyed
by its **Arg** as well as its kind (`whatsnew` for 0.2.1 is a different keep-alive slot from `whatsnew` for 0.3.0);
so are `Module`, `SidebarCustomize` and both section kinds.

**`ConnectDiagnostics` is a 0.2.9 defect this table exposes, not a port.** `ContentHost.PageFor` renders
`ConnectDiagnosticsPage.Route`, but the key is absent from `ShellRoutes.s_exact`, so `IsKnown` is false: the deep
link is refused, its history row is inert and its tab is labelled "Your Library" (ch 27 §9.6). On a healthy install
the page has exactly one door — an error `InfoBar` in Settings ▸ Playback. Giving it its own kind in the table above
fixes it by construction. **Per CLAUDE.md ("every fix references its issue") the fix needs a GitHub issue first, and
no issue exists today.** That is an action for Christos (§9.6, item 6); do not file one from this plan.

**One route key is an alias, not a kind.** `NavRouteNormalizer.LegacyRecentsRoute` =
`"home-section:spotify:list:recents:main"` is matched by the `home-section:` prefix, so `IsKnown` accepts it, but
`ContentHost.PageFor` rewrites it to `recents` before any arm sees it (`ContentHost.cs:192-194`) and
`PublishesShellMaterial` lists it beside `recents` (`:185`). It is carried by persisted history documents and old
Home layout documents, so **`Shell.DeepLink` and the history/tab restore path must normalise it to
`RouteKind.Recents`** — it gets no kind of its own, and `NavRouteNormalizerTests` ports with the rule (§6.1 ch 18).

### 4.12 `Entities/Track.UI.cs` — the one row (UI, pure over handles)

```csharp
public readonly partial struct Track
{
    /// The row every list uses. The EDGE decides the optional columns (D10): album pages pass an AlbumTrackEdge (number), playlists pass added-at/by, the queue passes nothing.
    public static Element Row(in BoundItemScope<Track> item, RowStyle style)
        => new BoxEl
        {
            Height = 56, Layout = Row.Horizontal, Padding = Design.RowPad,
            Children =
            [
                style.ShowNumber ? item.Text(t => style.NumberOf(t)) : null,
                new ImageEl { Source = item.Image(t => t.Knows(TrackFields.Image) ? t.ImageId : Design.PlaceholderCover), Size = 40, Radius = 4 },
                new BoxEl { Layout = Row.Vertical, Children =
                [
                    new TextEl(item.Text(t => t.TitleId)) { Style = Design.RowTitle, Fill = item.Signal(t => Playback.Current.Value == t ? Design.Accent : Design.Fg) },
                    new TextEl(item.Text(t => t.ArtistLineId)) { Style = Design.RowSub },     // ArtistLineId: a StringId column computed at commit (P11), never a per-frame concat
                ]},
                style.ShowAddedAt ? item.Text(t => style.AddedAtOf(t)) : null,
                item.Duration(t => t.DurationMs),
                item.ShowWhen(t => Playback.Current.Value == t, () => Controls.Equalizer(Playback.PhaseSignal)),
            ],
            OnPointerDown = item.Command(t => Playback.Post(new(Playback.InputKind.Play, t, style.CursorOf(t)))),
            ContextMenu = item.Menu(t => Shell.ActionsFor(EntityKind.Track, style.Context(t))),
        };
}
```

### 4.13 `Entities/Album.Page.cs` — component tree and wireframe

```
AlbumPage(Album a)                          ┌───────────────────────────────────────────────┐
├─ Hero(a)                                  │ ◀ back      [cover 232]  ALBUM                 │
│   ├─ Cover(a)                             │              Album title (StringId)            │
│   ├─ Title/Artists faces/Year·N tracks    │              ● artist  · 2024 · 12 songs, 41 m │
│   └─ Actions: Play, Save/Unsave, More     │              [▶ Play] [♡] [⋯]                  │
├─ TrackList                                ├───────────────────────────────────────────────┤
│   └─ ItemsView.CreateBound(a.TracksSource,│  #  Title / Artist                 ▶  Plays  ⏱ │
│        item => Track.Row(item, AlbumStyle))│  1  Track one ................  ⏸  1,203,455 3:41 │
├─ About(a)          (cold group: shows when│  2  Track two ................            3:12 │
│   a.Knows(Publishing))                    │  … virtualized; rows bound by slot version    │
├─ MoreByArtist(a)   (edge ArtistReleases)  ├───────────────────────────────────────────────┤
└─ OtherVersions(a)  (edge AlbumVersions)   │  Label · ℗ 2024 · Copyright line              │
                                            │  More by artist  [card][card][card] ▸         │
                                            └───────────────────────────────────────────────┘
```
```csharp
public readonly partial struct Album
{
    public sealed class Page : Component
    {
        readonly Album _a;
        public Page(Album a) => _a = a;
        public override Element Render()
        {
            var version = UseSignal(Entities.Current.Albums.Changed);       // re-render only when the ALBUM table published (D8); rows have their own binding
            UseEffect(() =>
            {   // demand the whole model (CLAUDE.md: pages never manage fetch windows): identity + full detail + the complete track list
                Entities.Ensure(_a, AlbumFields.All);
                Entities.EnsureEdges(_a, EdgeKind.AlbumTracks);
                Entities.EnsureRows(_a.TrackSlots, TrackFields.Row);          // one 300-uri batch per page, not per visible range
            });
            return new BoxEl { Layout = Row.Vertical, Children = [ Hero(_a), TrackList(_a), About(_a), MoreBy(_a), Versions(_a) ] };
        }
    }
}
```

### 4.14 `Entities/User.cs` — the library IS edges (G6)

```csharp
public readonly partial struct User(int Slot)
{
    public static User Me => Entities.Current.Me;                             // the account's own slot, allocated at Boot
    public ReadOnlySpan<int> LikedTrackSlots => E.Liked.Targets(Slot);        // parent = user
    public bool Likes(Track t) => E.Liked.Contains(Slot, t.Slot);              // reverse index, no bool column anywhere (P3)
    public ReadOnlySpan<RootlistEdge> Rootlist => E.Rootlist.Payload(Slot);
    public ReadOnlySpan<int> RootlistSlots => E.Rootlist.Targets(Slot);
    /// Optimistic write (C6): flip the edge NOW with the pending bit, journal, PUT; confirmation clears the bit, rejection reverts.
    public void Like(Track t) { E.Liked.Insert(Slot, t.Slot, new LibraryEdge(Clock.Now, Flags: 1), at: 0); Store.Journal(Intent.Like, t.Uri); Spotify.Api.CollectionAdd("collection", t.Uri, onDone: ok => Shell.Host.Post(() => E.Liked.Settle(Slot, t.Slot, ok))); }
}
```

### 4.15 Tests (D17) — one example per core file, the shape every test follows

```csharp
public class PlaybackStepTests
{
    [Fact] public void Ten_next_clicks_produce_one_load_with_the_last_epoch()
    {
        var s = TestState.PlayingQueueOf(12); var fx = new Playback.Effects();
        for (int i = 0; i < 10; i++) Playback.Step(ref s, new(Playback.InputKind.Next), ref fx);
        Assert.True(fx.Load); Assert.Equal(s.LoadEpoch, fx.LoadEpoch); Assert.Equal(11, s.Cursor.Index);
    }
    [Fact] public void Stale_audio_signal_is_ignored() { … }
    [Fact] public void Cluster_older_than_fence_cannot_revoke_ownership() { … }
}
public class DecodeTests
{
    [Fact] public void TrackV4_fills_identity_group_with_full_authority()
    {
        var st = new Staging(); Spotify.Decode.TrackV4(Fixtures.TrackV4Bytes, ref st);
        var row = st.Tracks[0]; Assert.Equal((uint)TrackFields.Identity, row.Known & (uint)TrackFields.Identity); Assert.Equal(Authority.Full, row.Authority);
    }
}
public class EdgesTests { [Fact] public void ReplacePage_marks_partial_until_total_reached() { … } }
public class FetchPlanTests { [Fact] public void Second_plan_in_same_epoch_asks_nothing() { … } }
public class NavTests { [Fact] public void Go_pushes_back_and_clears_forward() { … } }
```

---

## 5. Waves

Rules for every wave: subagents work on DISJOINT files listed under "owner"; only the orchestrator builds, tests and
launches; a wave closes when its gate is green in Debug AND Release. Line budgets are the §2 targets; a file that
passes its budget by 30% gets a named partial, not a second type — and every owner **names their partials on the
first day of their wave** (§2, §8 G4), never mid-wave. Every Wave 4/5/6 owner **reads the chapters for the files
they own, end to end, before writing a line** (§9). The "depends on" column is binding: a shared file is never
scheduled after a consumer of it.

### Wave 0 — golden captures, worktree, fold, skeleton (2 days, orchestrator only)
- **First, before anything moves the tree: the golden captures (§8 G1).** Drive the kept 0.2.9 Release build
  (`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`) with
  `ops/release/tools/Drive-WaveeWindow.ps1` — it uses `PrintWindow`; the harness `--screenshot` returns black under
  Mica — and capture every route at the width tiers each chapter names, in the states its wireframes name. Store
  under `docs/plans/wavee/wavee-0.3-ui/golden/`. This baseline **cannot be taken after Wave 0**.
- Create `C:\WAVEE\wavee-0.3` on `feat/0.3-structure` from the committed 0.2.9 HEAD.
- `git mv` the old tree to `src/apps/_old/` (Wavee/{Actions,App,Backend,Components,Design,Diagnostics,Features,Platform,SpotifyLive}, Wavee.Core/*, Wavee.Tests/*). Protos and assets `git mv` to their new homes. §3 project changes.
- Create **every file of §2** — all 136 (Q7, 2026-09-12, deletes `+Diagnostics.Api.cs`; was 137), including every `+`-marked named partial — as an empty partial with its
  header comment (CORE/SHELL/UI, owner, wave, budget, the chapters that specify it) so the tree compiles.
- Fix `Wavee.ReleaseTool`'s `<Compile Include>` list for `Screens/ReleaseNotes.cs`'s partials (§2).
- Engine PR: `StringTable.Intern(ReadOnlySpan<byte>)` (§3.4).
- Gate: golden captures stored and spot-checked against three chapters' §2 wireframes; `dotnet build Wavee.slnx`
  Debug + Release clean; `Wavee.Tests` compiles with zero tests.

### Wave 1 — Entities core (3 subagents)
| Owner | Files | Depends on | Ports from |
|---|---|---|---|
| A | `Entities.cs`, `Palette.cs` (CORE), `Track.cs`†, `Album.cs`, `Artist.cs`, `Episode.cs`, `Show.cs`, `Concert.cs` | — | Models.cs records → columns; EntityUri; `CoverColorPlane`'s table half |
| B | `Edges.cs`, `Playlist.cs`, `User.cs`, `Queue.cs`, `Home.cs`, `Search.cs`, `Browse.cs`, `Recents.cs`, the **friends edge and columns** (ch 21 gap G6) | — | PlaylistMember/RootlistEntry/SavedItem → edge structs; HomeFeed/SearchResults/RecentsList → synthetic parents |
| C | `Store.cs`, `Fetch.cs` | — | SqliteColdStore schema → columns; EntityCacheGc → Sweep; hydration ledger → Plan |

† **Columns, handles and field groups only, for every entity CORE file in both rows** — `Track.cs`, `Album.cs`,
`Artist.cs`, `Episode.cs`, `Show.cs`, `Concert.cs` (A) and `Playlist.cs`, `User.cs`, `Queue.cs`, `Home.cs`,
`Search.cs`, `Browse.cs`, `Recents.cs` (B). Each file's **ported pure rules and its whole §2 line budget** belong to
the owner §2 names for it (M in Wave 4.5 for `Track.cs`; M/N/O/P/Q in Wave 5 for the rest), who opens the same file
again in their wave. This is the one deliberate two-owner/two-wave arrangement in the tree: Wave 2 cannot decode
into columns that do not exist yet, and Wave 1 cannot write rules against chapters nobody has read yet. See the
"two notes on the entity CORE rows" under §2's `Entities/` table.

- `Palette.cs` is in Wave 1 and not Wave 4 because chapters 00, 03, 07, 08, 12 and 21 all read the graded plane as
  ground: without it five Liked treatments lose their base and Rainbow degrades to input order (A5).
- **`Platform/Platform.cs`'s boot seam is orchestrator-owned from Wave 0.** §3.5's `Main` calls `Platform.Boot()`
  before anything else, Wave 2 reads stored credentials through it and Wave 4 reads the settings key table and the
  four preference epochs — but §2 puts `Platform.cs` in Wave 6 with owner S. Wave 0 therefore lands the settings
  store, the credential slot, `Glyphs`, `ZoomAutoPolicy.MigrateMode` and the `WaveeLog` seam as an
  orchestrator-owned first cut inside `Platform.cs`; owner S completes the file (network policy, ambient power,
  `--fake` args, `Clock.SeedEpoch`) in Wave 6. Same pattern as `Entities.Fake.cs` (Wave 5's owner, Wave 4's need).
- Tests: tables alloc/free/version; Knows/authority merge; CSR replace/page/contains/insert/settle; Plan dedupe; Store round-trip against a temp db; palette key identity and TTL; SIMD scan == scalar scan on random data (P15).
- Gate: tests green; a micro-benchmark (BenchmarkDotNet is NOT added; a `Wavee.Tests` fact with a Stopwatch and a generous bound) shows 10k tracks resident in < 1.5 MB managed and zero allocations in a 1k-row `Knows` sweep.

### Wave 2 — Spotify (3 subagents)
| Owner | Files | Depends on | Ports from |
|---|---|---|---|
| D | `Spotify.cs`, `Spotify.Session.cs` | Wave 1 | ApConnection, Shannon, Handshake, login5, DealerFrameParser, dealer loop, audio-key |
| E | `Spotify.Decode.cs`, `+Spotify.Decode.Pathfinder.cs` | Wave 1 (A, B) | ExtendedMetadataSource, all Projectors, SpotifyExportMapper, PlaylistWireMapper, CollectionWireMapper, cluster/PutState mappers, `RecentsList.Group` (ch 16), the bundled-export offline fixture path (ch 31) |
| F | `Spotify.Api.cs`, `Spotify.Audio.cs`, `Spotify.Telemetry.cs`, `Spotify.Connect.cs` | D | ~20 Spotify*Service files → functions; SpotifyLive/Audio stream + keys; Gabo/Herodotus; LiveConnect + DeviceStatePublisher |
- Tests: Decode against the existing captured fixtures (`_old/Wavee.Tests/**/Fixtures`), Shannon/handshake vectors, request builders return the request (pure), dealer frame parse.
- Gate: tests green; a console smoke (`dotnet run -- --login-smoke`, a `Diagnostics.Probe` entry) logs in with the stored credential and decodes one `getAlbum` into a Staging with the expected row counts. (Network; run by the orchestrator only.)

### Wave 3 — Playback (2 subagents)
| Owner | Files | Depends on | Ports from |
|---|---|---|---|
| G | `Playback.cs`, `Playback.Host.cs` | Wave 2 | PlaybackSession, PlaybackOwnership, MediaSwitchLogic, SeekGate, PlayIntentGate, GaplessJoinClock, DeviceRecoveryPlan, PlaybackController verbs; `SmtcTimelineCoalescer`, `TimeFormat`/`LiveRail`/`LiveEdgeState` |
| H | `Playback.Audio.cs`, `Playback.Video.cs`, `Playback.Os.cs` | G | FluentMediaAudioHost + byte sources, the video decode host + VideoLoadPump, SMTC/taskbar/thumbnail-toolbar/jump-list/power bridges. **Ch 14 is `Playback.Os.cs`'s contract** |
- **The jump list compiles against stores from Waves 1 and 4.** Wave 3 ships it behind the existing
  `AttachHistory` / `AttachLibrary` late-binding seam, with the category empty until owner I's history log (Wave 4)
  and owner B's library edges attach. Ch 14 W14-W16 are then re-checked in Wave 5, not Wave 3.
- `Playback.Video.cs` is the decode/host only (A13). Owner K writes every video **surface** in Wave 4.
- Tests: Step transitions (the 0.2.9 PlaybackSession/ownership tests ported to Step), stale-epoch drops, effect-slot semantics, `SmtcTimelineCoalescerTests`, `ToastCoalescingTests`.
- Gate: tests green; the login smoke plays 10 s of a track through the real pump (orchestrator); the SMTC card, the taskbar overlay and the thumbnail toolbar match ch 14's W1-W13 against the golden captures (owner H's whole HWND-line surface; W14-W16, the jump list, are re-checked in Wave 5 per the note above).

### Wave 4 — shell, sidebar, rail/stage/deck/lyrics/video, platform (4 subagents)
| Owner | Files | Depends on | Ports from |
|---|---|---|---|
| I | `Shell.cs`, `Shell.UI.cs`, `+Shell.Masthead.UI.cs`, `+Shell.PlayerBar.UI.cs`, `+Shell.Overlays.UI.cs`, `Shell.Palette.cs`, `+Shell.History.UI.cs`, `Shell.Host.cs`, **`Platform/Actions.cs` + `Actions.UI.cs` (A7)**, **`Platform/Notify.cs` + `Notify.Host.cs` (A9)**, **`+Screens/Setup.UI.Runtime.cs` (A18)** | L's `Drag.cs` (week 1), then L's `Design.cs` + `Controls.*`; P's `Search.cs` for the omnibar suggestion contract (the omnibar itself is I's) | WaveeShell, ShellNav/ShellRoutes/ContentHost, PlayerBar, WaveeCommands + `Actions/*` → one table, `HistoryStore` + the play log, `NotificationCenterBridge`'s merge/filter/read-state half + `AppUpdateToasts` + `ToastEscalator` + `DaylistNotifier` + `ReleaseNotifier`, `WaveeTips`/`WaveeTipsCore` |
| J | `Sidebar.cs`, `Sidebar.Doc.cs`, `Sidebar.UI.cs`, `Sidebar.Customizer.UI.cs`, `Sidebar.Host.cs` | I's `Actions.*`; L's `Drag.cs`, `Design.cs` and `Controls.*` | the sidebar platform (reducer/templates/planner/projection/pane/customizer/persistence); data sources become edge reads |
| K | `Rail.cs`, `Rail.UI.cs`, `+Rail.Styles.UI.cs`, `Stage.cs`, `Stage.UI.cs`, `Deck.cs`, `Deck.UI.cs`, `+Deck.Faces.cs`, `Lyrics.cs`, `Lyrics.UI.cs`, `Lyrics.Host.cs`, `Video.cs`, `Video.UI.cs`, `Video.Host.cs`, `+Screens/Settings.UI.Video.cs` | L's `Design.cs`/`Controls.*`/`Palette.Host.cs`; H's `Playback.Video.cs` (Wave 3) | RightRail, NowPlayingPanel, PlayerStyleFlyout, Deck/*, LyricsView + Backend/Lyrics, StageChrome/Identity/Panes/Layout/Arm/Ink, the four video surfaces + `PlacementCore` + the override manager body, the friends panel |
| L | `Design.cs`, `Controls.cs`, `+Controls.Cta.cs`, `+Controls.Art.cs`, `+Controls.Picker.cs`, **`Drag.cs`**, `Prefs.cs`, `Entities/Palette.Host.cs` | A's `Palette.cs` (Wave 1) | Design/*, Components/* minus entity rows/cards, `Features/DragDrop/*`, the four preference epochs, `CoverColorPlane`'s pump |

Sequencing inside Wave 4, which is not optional:
1. **L ships `Drag.cs` in the first week** (A6). I's tab spring-load and J's five sidebar drop cues consume its rule
   tables; written second, they are written twice.
2. **I ships `Actions.cs` + `Actions.UI.cs` before J's "Move to folder…" picker** (A7). The descriptor shape is
   frozen when it lands; `PinRowRule` goes the other way, into `Sidebar.cs`.
3. **J sequences `Sidebar.Customizer.UI.cs` last** — it is a routed destination over the live document, so it needs
   the document, the reducer and the pane to be real first.
4. **K's stage and video land after L's `Design.cs`/`Controls.*`**, and `Settings.UI.Video.cs` is written here even
   though owner R mounts it in Wave 6.
5. **Notifications are Wave 4, not Wave 6** (A9): the in-app panel is shell chrome and shares the model with the
   Action Center half, so both halves ship together under one owner.
6. **I writes `+Screens/Setup.UI.Runtime.cs` in this wave** (A18) even though owner R's wizard mounts it in Wave 6 —
   the mirror of point 4's `Settings.UI.Video.cs`. Without it the Wave-4 shell gate has a banner with nothing behind
   its button, and Wave 6 would write the provisioning card a second time.

- Tests: Nav reducer, deep links, the route table (`ShellRoutesTests` extended to the new `RouteKind`), the action
  table + targeting matrix, the sidebar reducer/planner/projection suite (37 files, ports nearly 1:1), the deck's
  10 model test files, `Lyrics.ResolveLine` (ONE copy) and the lyric parsers, `PlacementCore`, the notification
  policy/merge/escalation set, `AppUpdateToasts`.
- Gate: tests green; `--fake` shows the shell frame with an empty content host, a working sidebar in all three
  designs, the player bar, the right rail, the stage, one deck face, the lyrics rail arm and the notification panel
  rendering seeded rows. Parity (§8 G3) recorded for chapters 14 (W17-W28, the toast/notification frames owner I
  ships this wave — W1-W16 are owner H's Wave 3 surface and already gated there), 18, 19, 20, 21, 22, 23, 24, 25, 26
  and the Wave-4 half of 00, 02 and 29, side by side against the golden captures.

### Wave 4.5 — the shared detail frame and the track surface (1 subagent, opens when Wave 4 closes)
A slot the plan did not have. Wave 5 hands `Album.*`/`Show.*` to M, `Playlist.*`/`User.*` to O and `Artist.*` to N,
and all five pages compose **one** frame and **one** table: without this slot Wave 5 opens with five owners sharing
two unwritten files, which breaks §5's own "DISJOINT files" rule on day one (ch 03 §9, index §5).

| Owner | Files | Depends on |
|---|---|---|
| M | `Entities/Detail.cs` (A1), `Entities/Detail.UI.cs` (A1), `Entities/Track.cs` (A2), `Entities/Track.UI.cs` (A2), `Entities/Track.Table.cs` (A2) | all of Wave 4 — the frame mounts `Controls.*`, `Drag.cs`, `Actions.*` and `Palette.Host.cs` |

- Chapters to read end to end first: **03** (frame), **04** (table), **01** (row and the reconciled four-file plan),
  and **30** for every preference these files read.
- Tests: the six pure detail classes ported verbatim with their existing test files (§6), the track rule set, the
  table's command-bar layout and filter model.
- **Gate: the fake album renders through the real frame in both layout arms** (ch 03 §10 item 67), and chapters 03,
  04 and 01's parity checklists pass item by item against the golden captures at the widths they name.
  **Wave 5 does not open until this gate is green.**

### Wave 5 — entity UI and pages (5 subagents; the app runs at the end)
| Owner | Files | Depends on |
|---|---|---|
| M | `Track.Drawer.cs`, `Album.cs`, `Album.UI.cs`, `Album.Page.cs` (incl. the whole `prerelease:` surface), `Show.cs`, `Show.UI.cs`, `Show.Page.cs`, `Episode.cs`, `Episode.UI.cs` | Wave 4.5 (M's own) |
| N | `Artist.cs`, `Artist.UI.cs`, `Artist.Page.cs`, `Artist.Discography.cs`, `Concert.cs`, `Concert.UI.cs`, `Concert.Page.cs` | Wave 4.5; `Detail.UI.cs`'s `ContextBand` |
| O | `Playlist.cs`, `Playlist.UI.cs`, `Playlist.Page.cs` (incl. the `local` arm), `User.cs`, `User.UI.cs`, `User.Page.Library.cs`, `User.Page.Liked.cs`, `User.Liked.cs`, `User.Facts.cs`, `User.Facts.UI.cs`, `User.Cover.cs` | Wave 4.5; **O configures `Track.Table.cs` through `TableProfile` and never edits it** (A2) |
| P | `Home.cs`, `Home.UI.cs`, `+Home.Cards.UI.cs`, `+Home.Artists.UI.cs`, `Home.Page.cs`, `Home.Customizer.cs`, `Home.Host.cs`, `Search.cs`, `Search.UI.cs`, `Search.Page.cs`, `Browse.cs`, `Browse.UI.cs`, `Browse.Page.cs`, `Recents.cs`, `Recents.UI.cs`, `Recents.Page.cs` | I's `Shell.Masthead.UI.cs` (the omnibar contract) |
| Q | `Queue.cs`, `Queue.UI.cs`, `Entities.Fake.cs` (ch 31 is its specification) | K's `Stage.UI.cs` (the queue's stage skin); everyone else consumes the seed |

- `Entities.Fake.cs` is Q's, but the seed is **needed in Wave 4** for that wave's own gate (ch 29 §9.6(b)): Q writes
  a first cut of SEED-CORE at the start of Wave 4 as an orchestrator-owned file, and takes ownership in Wave 5.
- **Gate (G2, stage 1): `dotnet run -- --fake` makes every route whose page ships in Waves 1-5 RENDER ITS LOADED
  STATE from `Entities.SeedFake()`** — not "opens", not a skeleton — plus the surface half (a PLAYING bar over the
  silent sink, the rail/NPV/deck, the queue panel, the lyrics rail arm, the friends panel, the notification centre,
  the device picker). The probe list is the §4.11 route table **less** `settings`, `whatsnew`,
  `playback-diagnostics`, `connect-diagnostics` and `module:` (no `api-console` — Q7, 2026-09-12, deleted, never
  probed at all), whose pages are Wave 6 (`Screens/*`,
  `Platform/Modules.*`) and are probed at the Wave 6 gate instead; it explicitly includes `home-customize`,
  `home-section:`, `browse-section:`, `disco:`, `prerelease:`, `local`, `history`, `recents` and
  `sidebar-customize`. Row binding proven by the `ReuseGuard` (no
  remounts on a table publish); no allocation on a scroll frame (engine `FG_FPS_LOG` alloc counter = 0 in phases
  6-13). Parity (G3) recorded for chapters 01, 03, 04, 05, 06, 07, 08, 09, 10, 11, 12, 13, 15, 16, 17, 21 (queue)
  and 31.

### Wave 6 — screens, platform, modules, live, delete, ship (3 subagents + orchestrator)
| Owner | Files | Depends on |
|---|---|---|
| R | `Settings.*` (incl. its four named tab partials; mounts K's `Settings.UI.Video.cs`), `Setup.*` (the wizard's Local-playback page mounts I's `+Setup.UI.Runtime.cs`, A18 — R does not rewrite it), `ReleaseNotes.*`, `Feedback.*` | I's `Notify.cs` (Settings ▸ Notifications reads its prefs); I's `+Setup.UI.Runtime.cs` (Wave 4) |
| S | `Diagnostics.*` (incl. the lyrics inspector, A14, and the row inspector for handles), `Platform.cs`, `Platform.Host.cs` | K's `Lyrics.Host.cs` (the inspector's report store) |
| T | `Modules.cs`, `Modules.UI.cs`, `Modules.Host.cs` (Sdk unchanged; module items → tables; module playables → `Playback.Open`) | Wave 5 |
- **The Wave 6 gate re-runs the whole G2 probe list**, this time including `settings`, `whatsnew`,
  `playback-diagnostics`, `connect-diagnostics` and `module:` + the watch page, whose pages did not exist at Wave 5
  (no `api-console` — Q7, 2026-09-12, deleted, not merely deferred).
- Orchestrator: live login end to end; Connect transfer both directions; `ops/perf-tour` run vs the 0.2.9 baseline; `local-update-e2e.ps1` both scenarios; RegionSize trial kept or reverted on the numbers; `git rm -r src/apps/_old`; CHANGELOG (structure, perf, every fixed issue with `(#n)`); `wavee-release.ps1 -DryRun`.
- Gate: parity (G3) recorded for chapters 14, 27, 28, 30 and the remainder of 29 — **all 32 checklists, 2,650 items,
  green and logged in each chapter's audit log.**

---

## 6. Migration of the old test suite (535 files, 111k lines)

| Category (today) | Count (approx.) | 0.3 |
|---|--:|---|
| Pure-decision tests of files that survive as core sections (Nav, sidebar reducer/planner, lyrics parse/rerank, release notes, update policy, setup gating, ownership fold, seek/intent gates, sweep schedule) | ~150 files | Port: same assertions, call the section function on the new type |
| Tests of hydration / store merge / query / catalog plumbing | ~180 files | Delete; replaced by Wave 1 table/edge/plan/store tests (~40 files) |
| Tests of interfaces, Switchable/Null/fakes, bridges, wiring | ~90 files | Delete (the subject is gone) |
| Wire mapper tests with captured fixtures | ~50 files | Port to Decode tests; fixtures kept |
| Audio/playback controller tests with IAudioHost fakes | ~40 files | Port the assertions to Step tests; the fake host dies |
| Sdk/module tests | 23 files | Unchanged |
Target: ~180 test files, ~3k tests, all against core files. The 6.6k count is not a goal; coverage of the core is.

### 6.1 The pure rules the contract names, and the tests that already pin them (G6)

Each chapter's §8 is a list of rules to port **verbatim** — the decisions may not be re-derived, only their input
types change from records to handles. This table is the destination for every one of them and the test file that
already holds it; a `—` is a rule the chapter found untested, and the owner adds the test with the port. Wave 4/5/6
owners read their chapter's §8 **before** they re-derive anything.

| Ch | Pure rules | 0.3 CORE home | Existing tests to port |
|--:|---|---|---|
| 00 | motion ladders, entrance stagger, hover gate, wash geometry, merged rung, reveal ramp, zoom policy, cover palette | `Platform/Design.cs`, `Platform/Platform.cs`, `Entities/Palette.cs` | `MotionSystemTests`, `EntranceStaggerTests`, `HoverMotionGateTests`, `ShellWashGeometryTests`, `ShellMergedRungTests`, `DetailRevealRampTests`, `DetailPageToneTests`, `ZoomAutoPolicyTests`, `CoverColorPlaneTests`, `ContentHostPageTransitionTests`, `StageLayoutTests` |
| 01 | lane table, tier/relief ladders, row-style rules, expanded facts, membership diff, reorder rules, equalizer motion, drag rules + chip | `Entities/Track.cs`, `Platform/Drag.cs`, `Platform/Controls.cs` | `TrackRowStyleRulesTests`, `TrackExpandedFactsTests`, `MembershipDiffTests`, `PlaylistReorderRulesTests`, `EqualizerMotionPolicyTests`, `NowPlayingOverlayMatchTests`, `WaveeDragRulesTests`, `WaveeDragChipModelTests`, `MoveRowsConventionTests`, `PlaylistDepositTargetsTests`, `RootlistRefusalTests`, `SidebarDropCueTests`, `ArtistPopularLayoutTests`, `DetailLayoutBreakpointTests`, the 10-file `Actions/` suite (`MenuGrammarTests`, `SelectionSemanticsTests`, `ActionRulesTests`, `SpotifyLinkTests`, `VideoOverrideUxTests`, …) |
| 02 | card chrome rules, chip parse, chart-title match, filter tags, entity uri | `Platform/Controls.cs`, `Entities/Entities.cs`, `Entities/User.Liked.cs` | `ChartTitleMatchTests`, `ContentFilterParserTests`, `ContentFilterTagsTests`, `EntityUriTests`, `ExportMapperTextTests`, `DeepLinkParseTests` |
| 03 | `DetailVerticalLayout`, `DetailLayoutBreakpoints`, `DetailRailPolicy`, `DetailRevealRamp`, `DetailHeaderMergeRules`, `PlaylistPageNoticeRules`, `ContextBandLayout`, skeleton geometry | `Entities/Detail.cs` | `DetailVerticalLayoutTests`, `DetailLayoutBreakpointTests`, `DetailRailPolicyTests`, `DetailRevealRampTests`, `Actions/DetailHeaderMergeRulesTests`, `Actions/PlaylistPageNoticeRulesTests`, `ContextBandLayoutTests`, `DetailSkeletonGeometryTests`, `DetailVerticalFooterTests`, `DetailCoverStabilityTests`, `DetailLiveRefreshTests`, `DetailOwnerIdsTests`, `AlbumReleaseFactsRulesTests` |
| 04 | `DetailTrackTableRules`, `DetailTrackCommandBarLayout`, `TrackFilterModel`, `PlaylistListState`, tempo filter | `Entities/Track.cs` | `DetailTrackCommandBarLayoutTests`, `TrackFilterModelTests`, `PlaylistListStateTests`, `TempoFilterTests`, `ContentFilterTagsTests` |
| 05 | `AlbumReleaseFactsRules`, `AlbumDrawerVerdict`, `PreReleaseDerivation` | `Entities/Album.cs` | `AlbumReleaseFactsRulesTests`, `AlbumDrawerVerdictTests`, `PreReleaseModelTests` — **plus four album rules ch 05 found untested** |
| 06 | insertion geometry, deposit targets, edit error kinds, notice rules | `Entities/Playlist.cs`, `Entities/Detail.cs` | `PlaylistInsertionGeometryTests`, `PlaylistDepositTargetsTests`, `Actions/PlaylistEditErrorKindsTests`, `Actions/PlaylistPageNoticeRulesTests` |
| 07 | `LikedCoverRules`, `LikedFactsRules`, `ContentFilterTags` | `Entities/User.Liked.cs`, `Entities/User.Facts.cs` | `LikedCoverRulesTests`, `LikedFactsRulesTests`, `ContentFilterTagsTests`, `TrackFilterModelTests` |
| 08 | popular layout + tracks, hero layout, era bands, discography pagination, drawer verdict | `Entities/Artist.cs`, `Entities/Artist.Discography.cs` | `ArtistPopularLayoutTests`, `ArtistPopularTracksTests`, `ArtistHeroLayoutTests`, `DiscographyEraBandsTests`, `DiscographyPaginationTests`, `AlbumDrawerVerdictTests`, `ContextBandLayoutTests` |
| 09 | `Episode.Rules`, show/episode paging, module page gate, `WatchPageModel`, `ModulePages` | `Entities/Episode.cs`, `Platform/Modules.cs` | `EpisodeRulesTests`, `ShowEpisodePagingTests`, `ModulePageGateTests`, `Modules/ModulePagesTests`, `Modules/WatchPageModelTests`, `DockedVideoHostingTests` |
| 10-12 | `Home.Layout`, both projections, both estimators, hero/artist-row/timeline/play rules, section paging, masthead metrics | `Entities/Home.cs`, `Entities/Browse.cs` | `HomeLayoutTests`, `HomeLandingProjectionTests`, `HomeFacetProjectionTests`, `HomeFacetStripTests`, `HomeHeroLayoutTests`, `HomeArtistRowLayoutTests`, `HomeTimelineMergeTests`, `HomeCardPlayRoutingTests`, `HomeBrowseCardsTests`, `HomeWashSourceTests`, `HomeWashLocaleTests`, `HomeSectionPagingTests`, `BrowseSectionPagingWalkTests`, `BrowseMastheadMetricsTests`, `ShellMastheadRegistryTests`, `ShellTintOwnershipTests`, `DrillTrailTests` |
| 10-12 | `HomeFeedReadiness` + `HomeRevealGate`, `HomeLandingProjection`, `HomeFacetStrip`, `HomeFacetProjection`, `HomeHeroLayout`, `HomeWashSource`, `HomeModuleLayout`, `HomeTimelineMerge`, `HomeCardPlayRouting`, `HomeArtistRowLayout`, `HomeCards`' three number/duration formats, `BrowseSectionWalk` + `ChartTitleMatch`, `ChartSections`, `HomeSectionPaging`, `HomeSectionRoutes`/`BrowseSectionRoutes`, `HomeLayoutDoc`/`Reducer`/`Commands`/`Wire`/`Modules`, `HomeLayoutStore`'s fault classification, `DrillTrail`, `ShellMastheadRegistry`, `BrowseMastheadMetrics`, `HomeCardNav.OpenBrowseSection`'s one-card rule, `NearTailWatch` | `Entities/Home.cs`, `Entities/Home.Customizer.cs`, `Entities/Home.Host.cs`, `Shell/Shell.cs` (`DrillTrail`, `ShellMastheadRegistry`), `Entities/Browse.cs` (`BrowseMastheadMetrics`, `ChartSections`) | `HomeFeedReadinessTests`, `HomeLandingProjectionTests`, `HomeFacetStripTests`, `HomeFacetProjectionTests`, `HomeHeroLayoutTests`, `HomeWashSourceTests`, `HomeTimelineMergeTests`, `HomeCardPlayRoutingTests`, `HomeArtistRowLayoutTests`, `HomeBrowseCardsTests`, `ChartTitleMatchTests`, `BrowseSectionPagingWalkTests`, `HomeSectionPagingTests`, `HomeLayoutTests`, `BrowseTaxonomyTests`, `BrowseMastheadMetricsTests`, `BrowsePageLayoutTests`, `DrillTrailTests`, `ShellMastheadRegistryTests`, `HomeWashLocaleTests` — **plus `HomeModuleLayout`'s geometry ladder and `HomeLayoutStore`'s fault/`.bak` classification, which ch 10 §8 and ch 12 §8 mark untested. Add both with the port** |
| 13 | `OmnibarSuggestQuery`, `SearchChipSkeletonPolicy`, `BrowseTaxonomy`, `BrowsePageLayout`, `NavRouteNormalizer` | `Entities/Search.cs`, `Entities/Browse.cs` | `OmnibarSuggestQueryTests`, `SearchChipSkeletonPolicyTests`, `BrowseTaxonomyTests`, `BrowsePageLayoutTests`, `BrowseMastheadMetricsTests`, `NavRouteNormalizerTests`, `ShellNavDestTests`, `DrillTrailTests` |
| 14 | `SmtcTimelineCoalescer`, `NotificationPolicy`, `AppUpdateToasts`, toast coalescing, the escalation plan | `Playback/Playback.cs`, `Platform/Notify.cs` | `SmtcTimelineCoalescerTests`, `NotificationAggregationTests`, `ToastCoalescingTests`, `AppUpdateToastsTests`, `SimulatedNotificationsTests` |
| 15 | library breakpoints, nav order, search matcher, selection commit, recency | `Entities/User.cs` | `LibraryLayoutBreakpointTests`, `LibraryNavOrderTests`, `LibrarySearchTests`, `LibrarySelectionCommitTests`, `RecentsRecencyTests` |
| 16 | the 19 `RecentsView` classes (content types, filter, sections, relabel, day buckets, density, calendar, morph first-occurrence, summary, owner subtitle, accent source row) | `Entities/Recents.cs` | `RecentsViewTests` (one file, ~60 facts — the chapter cites each by line) |
| 17 | the 807 lines of concert rules | `Entities/Concert.cs` | `ConcertHubModelTests`, `ConcertSchedulePageTests`, `ConcertRouteTests` |
| 18 | `ShellResponsiveLayout`, merged chrome, drill trail, `ShellRoutes`, `ShellNav.Dest`, tab workspace, splitter math, session snapshot | `Shell/Shell.cs` | `ShellResponsiveLayoutTests`, `MergedChromeLayoutTests`, `ShellMergedRungTests`, `DrillTrailTests`, `ShellRoutesTests`, `ShellNavDestTests`, `NavRouteNormalizerTests`, `TabWorkspaceTests`, `WorkspaceTabsPersistenceTests`, `SplitterMathTests`, `SessionSnapshotTests`, `ContentHostPageTransitionTests`, `ShellTintOwnershipTests`, `ShellWashGeometryTests`, `DeepLinkParseTests`, `ZoomAutoPolicyTests` |
| 19 | notification merge/read-ids/policy/prefs, `AppUpdateToasts`, `WaveeTipsCore`, `SetupCommands`, `PlayLinkActions` | `Platform/Notify.cs`, `Shell/Shell.cs` | `NotificationAggregationTests`, `NotificationPolicyTests`, `AppUpdateToastsTests`, `ToastCoalescingTests`, `WaveeTipsCoreTests`, `SetupCommandsTests`, `Actions/PlayLinkActionsTests` |
| 20 | `TimeFormat`, `LiveRail`, `LiveEdgeState`, `SeekGate`, device picker items, responsive tiers, volume taper, `PlayableLinks` | `Playback/Playback.cs`, `Shell/Shell.cs` | `TimeFormatTests`, `Backend/LiveRailTests`, `Backend/LiveEdgeStateTests`, `Backend/NowPlayingLiveWindowTests`, `SeekGateTests`, `DevicePickerItemsTests`, `PlayerBarResponsiveLayoutTests`, `Audio/VolumeTaperTests`, `Actions/PlayableLinksTests`, `PlacementCoreTests` |
| 21 | `QueueMovePlan`, `QueueOrder`, `RailVideoCoupling`, `NpvPlayerCatalog`, `NpvPlayerPrefs`, `StageLayout`, `NpvLyricsPeek` | `Entities/Queue.cs`, `Shell/Rail.cs`, `Shell/Stage.cs` | `QueueMovePlanTests`, `QueueOrderTests`, `RailVideoCouplingTests`, `NpvPlayerCatalogTests`, `NpvPlayerPrefsTests`, `StageLayoutTests`, `Lyrics/NpvLyricsPeekTests`, `DockedVideoHostingTests`, `ShellResponsiveLayoutTests` |
| 22 | `Fx`, `BlurPolicy`, `SyncGate`, `RowShape`, `MediaClock`, `PeekClock`, `Emphasis`/`ResolveLine`/`AdvancePastInterlude`, `Cascade`, `Prefs`, `IsRicherLyrics` | `Shell/Lyrics.cs` | `LyricsBlurPolicyTests`, `LyricsSyncGateTests`, `LyricsRowShapeTests`, `LyricsMediaClockTests`, `Lyrics/NpvLyricsPeekTests`, `StageLayoutTests` — **and eight rule sets ch 22 marks UNVERIFIED (no test today): `LyricsFx`, `PackEmphasis`/`OpacityOf`, `ResolveLine`/`SungOutMs`/`AdvancePastInterlude`, `ComputeSplit`/`HeldSyllableGlow`, the cascade math, `SoftnessOfLine`, `LyricsPrefs`, `IsRicherLyrics`. Add them with the port** |
| 23 | the 13 deck model classes | `Shell/Deck.cs` | `SpinIntegratorTests`, `TonearmGeometryTests`, `TonearmMachineTests`, `TapeMachineTests`, `DiscMachineTests`, `LevelSynthTests`, `MeterBallisticsTests`, `PositionInterpolatorTests`, `DeckBoundaryRulesTests`, `Player/Deck/DeckEaseTests` |
| 24 | `PlacementCore`, detached fullscreen rule, video-override mutation, aspect persistence, stage input, docked hosting | `Shell/Video.cs` | `PlacementCoreTests`, `DetachedFullscreenRuleTests`, `VideoOverrideMutationCoreTests`, `VideoAspectPersistenceTests`, `VideoStageInputTests`, `DockedVideoHostingTests`, `RailVideoCouplingTests` |
| 25-26 | the whole sidebar rule set: geometry, extents, planner, resolve, diff, pill, drop resolver/decision/nav, reorder clamp, tree selection, nav layout/preview, edit plan, folder flyout, stage hold, sort, search, pin id/sync, sources, document/reducer/wire/migrations | `Shell/Sidebar.cs`, `Shell/Sidebar.Doc.cs` | the 37-file suite: `SidebarRowGeometryTests`, `SidebarRowExtentsTests`, `SidebarRowPlannerTests`, `SidebarRailPlannerTests`, `SidebarRowDiffTests`, `SidebarPaneInvariantTests`, `RootlistSlotResolverTests`, `SidebarDropCueTests`, `RootlistRefusalTests`, `RootlistDropScenarioTests`, `RootlistSlotToOpTests`, `RootlistFolderPickerTests`, `SidebarDragClampTests`, `SidebarTreeSelectionTests`, `SidebarNavLayoutTests`, `SidebarNavExtrasTests`, `SidebarNavPreviewTests`, `SidebarEditPlanTests`, `SidebarFolderFlyoutNavTests`, `SidebarDropFreezeTests`, `SidebarSortTests`, `SidebarTemplateTests`, `SidebarProjectionTests`, `SidebarProjectionBinderTests`, `SidebarDataSourceTests`, `SidebarChurnTests`, `SidebarBootstrapTests`, `SidebarBuiltInDocumentTests`, `SidebarLayoutJsonTests`, `SidebarLayoutReducerTests`, `SidebarLayoutStoreTests`, `SidebarLayoutV2MigrationTests`, `SidebarPinKindWireTests`, `SidebarPinStoreTests`, `PinSyncRulesTests`, `SidebarShortcutsSectionTests`, `SidebarDesignGatingTests`, `FolderActionsTests` |
| 27 | `LogView`, `LogCapturePolicy`, `SettingsCatalog`, equalizer settings, metered status line, `ShutdownUpdatePolicy`, `WaveeLogSessions` | `Screens/Diagnostics.cs`, `Screens/Settings.cs` | `LogViewTests`, `LogCapturePolicyTests`, `SettingsCatalogTests`, `EqualizerSettingsTests`, `MeteredStatusLineTests`, `ShutdownUpdatePolicyTests`, `WaveeLogSessionsTests`, `NotificationPolicyTests`, `Actions/VideoOverrideUxTests` — **plus `BuildReport`, `GpuSummary`/`ClassifySharedIgpu`/`FormatBytes`/`FormatUptime`, untested today** |
| 28 | `SetupGating`, `SetupCommands`, `SetupLayout`, both setup presentations, `Qr`/`QrGrid`, `WaveeLottieRecolor`, the whole `ReleaseNotes` set, the whole `Feedback` set | `Screens/Setup.cs`, `Screens/ReleaseNotes.cs`, `Screens/Feedback.cs` | `SetupGatingTests`, `SetupCommandsTests`, `SetupLayoutTests`, `SetupRuntimePresentationTests`, `SetupSignInPresentationTests`, `SetupSignInProjectionTests`, `QrTests`, `QrGridTests`, `WaveeLottieRecolorTests`, `ReleaseNotes/*` (11 files), `ReleaseNotesJsonTests`, `ReleaseNotesStoreTests`, `Feedback/*` (7 files), `WaveeTipsCoreTests`, `AppUpdateToastsTests` |
| 29 | `PageNavMotion.SlotKey`/`RecipeFor`, `ShellTintOwnership`, `ShellWashGeometry`, `NetworkPolicy`, `ZoomAutoPolicy`, `AppLocaleBootstrap`, `WaveeActionTargets`, `WaveeActionDescriptor.Resolve`, `PinRowRule`, the four drag rule sets, `WaveeDragChipModel`, `WaveeResourceDragPayload` | `Shell/Shell.cs`, `Platform/Design.cs`, `Platform/Platform.cs`, `Platform/Actions.cs`, `Platform/Drag.cs`, `Shell/Sidebar.cs` | `WaveeDragRulesTests`, `WaveeDragChipModelTests`, `ZoomAutoPolicyTests`, the `Actions/` targeting tests — **and four rules with no test today that ch 29 says to add one for: `ShellWashGeometry.Resolve`, `NetworkPolicy.EffectiveQuality`, `AppLocaleBootstrap.SpotifyLanguage`, `ShellTintOwnership.Resolve`** |
| 30 | `AppearancePrefs`/`LyricsPrefs`/`DetailHeroPrefs`/`NpvPlayerPrefs` epochs, `SidebarDesign` gating, `DetailRailPolicy`, `DetailLayoutBreakpoints`, `ZoomAutoPolicy` | `Platform/Prefs.cs`, `Platform/Platform.cs` (the rest is a cross-cut view of files other chapters own) | `SidebarDesignGatingTests`, `DetailRailPolicyTests`, `DetailLayoutBreakpointTests`, `ZoomAutoPolicyTests`, `LyricsBlurPolicyTests`, `PlayerBarResponsiveLayoutTests`, `TrackRowStyleRulesTests`, `TimeFormatTests` |
| 31 | seed determinism (the pinned `Clock.SeedEpoch`), `AllocRun`, `ReplaceRun`, the fixture inventory | `Entities/Entities.Fake.cs` | **new**: `Wavee.Tests/EntitiesFakeTests.cs` (≈350 lines) — without a pinned seed clock no screenshot parity item is reproducible across two launches (ch 31 W15, W16) |

CLAUDE.md's "no source-text tests" rule is unchanged: every row above is a test that calls a function, never one
that reads production source.

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| **The honest budget is 2.2× the old one** (§2: ≈141,000-143,000 de-duplicated UI lines against the 64,900 §2 used to allocate for the same surfaces; +62 % per folder by ch 29 §9.4) | It is now **counted, not discovered mid-wave**. Every file carries a chapter-derived number, every over-30 % file is a named partial created empty in Wave 0, and the folder subtotals are recomputed from their own rows. The failure mode this replaces is real: at +62 % the "30 % over budget gets a partial" rule fires on nearly every file, "which is not a budget — it is a rename" (ch 29 §9.4), and a partial invented in Wave 5 is a new file against a frozen skeleton |
| **A chapter drifts from the arbitration** — two chapters still name different homes for one component | The seventeen contradictions the index found are settled once, in §9.4's arbitration table (A1-A17; A18 settles an eighteenth this review found), and the plan wins on *where the code lives* while the chapter wins on *what the surface looks like* (index §1). Re-verified 2026-09-12: the four chapters this row used to name as stale have all taken the edit — ch 04 §1.2 and §9 now put the drawer in `Track.Drawer.cs`, ch 30 §8 now files `ContextBandLayout` under `Entities/Detail.cs`, ch 21 §9.5 restated `Stage.cs` to 500 and ch 22 dropped `Lyrics.Stage.UI.cs`. The residual risk is the reverse: a chapter's **header** line or a §1 prose row that still quotes a pre-arbitration file or figure while its §9 has moved (ch 24's header says `Video.UI.cs` ~1,700 where its own line budget says 1,450 + the 280 override body). **An owner follows §9's table here, then the chapter's own §9 line budget, and never a chapter header**, and reports any third answer to the orchestrator rather than choosing |
| **The app is dark for Waves 1-4.5** | Each wave has a gate that runs code for real (db round-trip, login smoke, 10 s playback, shell frame, the fake album through the real frame); no wave merges without it. **What makes the blackout survivable is the golden captures** (§8 G1): the comparison baseline is taken from the kept 0.2.9 Release build before Wave 0 moves a file, so a surface re-authored four waves later is still checked against the thing it is supposed to look like rather than against somebody's memory of it. It cannot be taken afterwards |
| **Wave 4 is lopsided.** By §2's own rows the four buckets are J **23,500** (sidebar), K **21,630** (rail, stage, decks, lyrics, video), I **16,315** (shell + `Actions.*` + `Notify.*` + the setup card) and L **9,450** (design, controls, drag, prefs, palette host). Ch 18's "owner I's whole Wave-4 bucket 10,000-11,000" predates A7, A9 and A18, which added ~5,000 to I (Actions 2,150 + Notify 1,715 + the setup card 1,100) | Ch 21 proposed a sixth Wave-4 owner and the arbitration kept the stage and the video surfaces with K (A12, A13) because they share `Rail.UI.cs`'s mount points and the deck/lyrics clocks. If K's wave slips, the split point is `Deck.*` (5,400) — ten model test files, no dependency on the rail beyond the NPV mount. If I's slips, the split point is `Notify.*` + `Actions.*` (3,865) to a fifth owner, at the cost A7 and A9 name. **L is the small bucket and ships first (A6): if the wave has to be rebalanced, move work TO L, not away from it** |
| Stale handle after eviction/scope switch | `Version` compare on every bound row (engine slab does the same); scope switch replaces the whole set and bumps `Epoch`; a page holding an old `Scope` re-resolves through `Entities.Current` |
| Edge rewrite races a page iterating the span | Single writer (C1): rewrites happen in the UI drain; a page never holds a span across a drain (rule documented in Edges.cs header; ReuseGuard-style debug check in Debug builds) |
| Decode fixtures drift from live shapes | Wave 2 login smoke decodes live payloads and asserts row counts; the probe archives payloads for new fixtures |
| Sidebar platform is the largest port (31,551 lines of 0.2.9 → 23,500 budgeted, ch 26 §9.4) | Owner J alone for the whole wave; reducer/planner tests port first and stay green throughout; the customizer page last. Ch 25 §9 lists, **in order**, what a hurried port sheds first — if a cut becomes necessary it is named there and the corresponding parity items are struck, not silently failed |
| Perf claims not met | Wave 5 alloc gate and Wave 6 perf tour are pass/fail, not advisory; RegionSize is reverted if it does not help |
| PlayPlay junction | Untouched; `Spotify.Audio.cs` calls the same `Wavee.PlayPlay` entry points from their new home; the release script's junction assert is unchanged |

---

## 8. Definition of done

1. `dotnet build Wavee.slnx` and `-c Release` clean (`TreatWarningsAsErrors`).
2. `dotnet test src/apps/Wavee.Tests` green; every core file has a test file of the same name.
3. `dotnet run --project src/apps/Wavee -- --fake` renders every route's loaded state; ReuseGuard silent.
4. Live: login, home, search, album, artist, playlist, liked, queue, play, seek, next, Connect transfer to and from another device, lyrics, video toggle.
5. Perf tour vs 0.2.9 baseline: working set lower, cold album/artist reveal not slower, zero managed allocations on a scroll frame, no GC gen2 during a 5-minute listen.
6. `ops/release/tests/local-update-e2e.ps1` both scenarios; `wavee-release.ps1 -DryRun -SkipTests` clean; CHANGELOG issue refs gate passes.
7. `src/apps/_old` deleted; `Wavee.Core.csproj` gone. (The old "the file count under `src/apps/Wavee` is under 100" clause is **deleted** — P1. The tree is 136 files by §2's own rows (137 before Q7 deleted `+Diagnostics.Api.cs`, 2026-09-12), and a cap that forces a partial to be merged back is a cap that deletes the partial the budget needed.)

### The fidelity gates (G1-G6)

**G1 — golden captures, BEFORE Wave 0 moves the tree.** Capture every route from the kept 0.2.9 Release build
(`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`) with
`ops/release/tools/Drive-WaveeWindow.ps1`, which uses `PrintWindow` — **the harness `--screenshot` returns black
under Mica** — at the width tiers each chapter names and in the states its wireframes name. Store under
`docs/plans/wavee/wavee-0.3-ui/golden/`. This is the comparison baseline for the whole rebuild and **it cannot be
taken after Wave 0**.

**G2 — "renders its loaded state", not "opens".** Every route in §4.11's table paints real content from
`Entities.SeedFake()` (ch 31's fixture inventory), against a pinned seed clock so two launches agree. Ch 31's own
score of 0.2.9 is the thing being beaten: 31 route keys "open" today, but only sixteen rows render loaded, seven are
empty and one is absent, and **zero of the eleven non-routed surfaces render a loaded state at all** — the queue's
and the lyrics' fixtures are already written and have no caller. The probe list explicitly includes
`home-customize`, `home-section:`, `browse-section:`, `disco:`, `prerelease:`, `local`, `history`, `recents`,
`sidebar-customize`, `playback-diagnostics` and `connect-diagnostics` (no `api-console` — Q7, 2026-09-12, deleted),
plus the surface half: a
PLAYING player bar (the silent sink), the rail/NPV/deck, the queue panel, the lyrics rail arm, the friends panel,
the notification centre, the device picker and the four OS surfaces.

**G2 runs in two stages, because §2 does not build every page by Wave 5.** At the **Wave 5** gate the probe covers
every route whose page ships in Waves 1-5 — everything above except the five whose pages are `Screens/` or
`Platform/Modules.*` in Wave 6. At the **Wave 6** gate the *whole* list runs again and must include `settings`,
`whatsnew`, `playback-diagnostics`, `connect-diagnostics` (owner S/R) and `module:` + the watch page
(owner T). Nothing is dropped by the split; a route named at Wave 5 that only its Wave-6 page can render is a gate
that cannot pass, and this plan says which stage owns each one instead of leaving it to the run.

**G3 — a surface is not done when it renders.** It is done when its chapter's §10 parity checklist passes item by
item, side by side against the golden build, at the widths and in the states each item names, and the owner records
the pass in that chapter's §11 audit log. **2,650 items across 32 checklists.** A failed item is either fixed or
struck in the chapter with a reason — never left unchecked.

**G4 — read before writing.** Every Wave 4/5/6 owner reads the chapters for the files they own, end to end, before
writing a line: §0 (non-negotiables), §2 (wireframes), §3-§6 (tokens, colour, motion, interaction), §9 (re-author
notes) and §8 (the pure rules to port verbatim). They name any partial beyond §2's on the first day of the wave.

**G5 — Windows high contrast is out of scope** (P2, §1), stated as a decision. No parity item asks for it and no
file budgets it.

**G6 — every chapter's §8 pure-rules list ports verbatim with its existing tests**, per §6.1. A rule the chapter
marks untested gets its test written with the port, not after.

---

## 9. The UI fidelity contract

### 9.1 What it is

Thirty-two chapters, `docs/plans/wavee/wavee-0.3-ui/00-design-system.md` … `31-fake-data.md`, **56,051 lines**,
written 2026-09-11/12 **from the 0.2.9 source, before Wave 0 moves it**. Each chapter carries the same twelve
sections: §0 non-negotiables, §1 anatomy, §2 wireframes, §3 tokens, §4 colour and material, §5 motion, §6
interaction, §7 data and readiness in 0.3 terms, §8 pure rules to port verbatim, §9 re-author notes, §10 a parity
checklist, §11 an audit log. They specify, surface by surface, what 0.2.9 looks like and therefore what 0.3 must
reproduce. `90-coverage-gaps-1.md` is not a chapter: it was a critic sweep, merged into chapter 28 on 2026-09-12 and
kept as a five-line tombstone so the number never reads as a lost chapter.

**`docs/plans/wavee/wavee-0.3-ui/00-index.md` is its map** — one row per user-visible surface, with the route key,
the chapter, the 0.2.9 line count, the 0.3 file, the wave and the owner, so a dropped surface is visible before Wave
5 opens rather than in a side-by-side at the end.

### 9.2 The totals

| | |
|---|--:|
| Chapters | **32** (00-31), all complete |
| Chapter lines on disk | **56,264** (`wc -l` over the 32 chapter files, 2026-09-12 — 56,135 after the arbitration edits, **+129 from this pass's §9.6 answers and its verification sweep**, landed in chapters 05, 07, 14, 18, 19, 21, 22, 25, 26, 27, 28, 29, 31; `00-index.md` now states the same number) |
| User-visible surfaces mapped | **119** |
| Routes in `ShellRoutes` (16 exact + 10 prefix families + 3 concert) | **29** in 0.2.9 (+1 renderable but unregistered: `connect-diagnostics`); **28 in 0.3** — `api-console` DELETED, §9.6 Q7, 2026-09-12 |
| Wireframes | **792** |
| Parity items | **2,650** across 32 checklists (largest 123, ch 29; smallest 56, ch 31) |
| Honest line estimate, de-duplicated | **≈141,000-143,000** |
| What §2 used to budget for the same surfaces | **64,900** — ≈2.2× under |
| Files the chapters proposed that §2 lacked | **46** (index §4) — 43 folded into §2 above with an owner and a wave (the album **merch table** joined this group 2026-09-12, §9.6 Q4: added, inside `Edges.cs`, not a new file); 2 explicitly rejected (`User.Page.Profile.cs`, A3; `Entities/Notification.*`, A9 — and ch 19 §9.4 withdraws it itself); `Lyrics.Stage.UI.cs` dropped by A12. Two of the 46 entries are not files at all (the `EdgeTable` `Insert`/`Remove` pair, folded into `Edges.cs`; and the `Wavee.Tests` rows, folded into §6.1) |
| Surfaces with no owner or no wave slot | **20** (index §5) — all assigned in §9.5, whose table has **23** rows: Recents and History split to two owners and two waves, OS surfaces is a re-budget rather than an unowned surface, and the runtime provisioning card is A18's |
| Contradictions between chapters | **17** (index §6) — all arbitrated in §9.4, plus A18, an eighteenth this plan's own review found |

### 9.3 How an owner uses it

**Read your chapters end to end before writing a line** (§8 G4) — not §4's code sketches, which sit an altitude
above the surface and are wrong in the specific ways each chapter's §9 records. Start at the index's surface table
(§2 there): find every row whose "0.3 file(s)" column names a file you own, then read those chapters' §0, §2, §3-§6
and §9 before you open an editor, and §8 before you re-derive anything.

**Where a chapter and this plan disagree: the chapter wins on *what the surface looks like*; the plan wins on *where
the code lives*.** The arbitration table below is the plan speaking. Every chapter the index flagged as
pre-arbitration has since taken its edit (re-verified 2026-09-12: ch 04's drawer row, ch 30's `ContextBandLayout`
row, ch 21's `Stage.cs` 500, ch 22's dropped `Lyrics.Stage.UI.cs`), so a third answer today is most likely a
chapter's **header block** quoting a figure its own §9 has since moved — ch 24's header says `Video.UI.cs` ~1,700
where its §9 line budget says 1,450 plus a 280 override body. Read §9's line-budget table, not the header; follow the
arbitration table for the file name; tell the orchestrator; do not choose.

**A page is not done when it renders.** It is done when its chapter's §10 checklist passes item by item against the
golden 0.2.9 build (§8 G1, G3), at the widths and in the states each item names, with the pass recorded in that
chapter's §11 audit log.

### 9.4 Arbitrations (A1-A18)

Policy decisions P1 and P2 were approved by Christos on 2026-09-12; the arbitrations are the orchestrator's, taken
from the chapters' own reasoning.

| # | Decision | Why |
|---|---|---|
| **P1** | Budgets raised to the honest chapter-derived numbers; the 46 proposed files folded into §2 with owners; the "under 100 files" DoD clause deleted; partials named on day one | A budget 2.2× under is not a budget; the cut it forces lands on whoever ports last |
| **P2** | Windows high contrast out of scope for 0.3, stated in §1 as a decision | Unsupported in 0.2.9 — verified, no contrast-theme code under `src/apps/Wavee` — so a silence would read as an omission |
| **A1** | Shared detail frame → `Entities/Detail.cs` (CORE) + `Detail.UI.cs` (UI); `ContextBand` + `ContextBandLayout` live there, not in `Shell.UI.cs` and not in `Platform/Design.cs`. Owner M, a new **Wave 4.5** slot | The band's geometry *is* detail-frame arithmetic (`ClipFadeBand` = `DetailVerticalLayout.StickyFadeBand`, `Height 56` = `CompactIdentityHeight`). Ch 30 is wrong; a `Design.cs` home imports the frame's vertical layout into the design system |
| **A2** | Track surface → ch 01's reconciled four files: `Track.cs` CORE 1,200, `Track.UI.cs` (row) 2,600, `Track.Table.cs` (table) 2,600, `Track.Drawer.cs` (drawer) 700 = **7,100**, all owner M. The often-quoted **8,700** is the whole track *surface*, which also carries `Platform/Controls.cs`'s 900 share and `Platform/Drag.cs`'s 700 — **owner L's, in Wave 4, not M's** (chs 01 and 04 §9 both spell the 7,100 / 8,700 split out). Owner O configures the table through `TableProfile` | Ch 01 §9 declares itself the single authority and supersedes ch 04; the drawer's only call site is the table, not the row. Splitting the table by page would produce two implementations behind one design |
| **A3** | User → pages split by page, helpers by concern: `User.cs`, `User.UI.cs`, `User.Page.Library.cs`, `User.Page.Liked.cs`, `User.Liked.cs`, `User.Facts.cs`, `User.Facts.UI.cs`, `User.Cover.cs`. **No `User.Page.Profile.cs` and no `RouteKind.User`** | Chs 07 and 15 propose two incompatible schemes; both are right about their own half. There is no profile page and no profile route in 0.2.9, so the third arm is a file for a surface that does not exist |
| **A4** | Sidebar → ch 26's whole-platform total (23,000-25,000) and file set, plus ch 25's `Sidebar.Doc.cs`, at ch 26 §9.4's own post-arbitration per-file split: `Sidebar.cs` **5,000** · `Sidebar.Doc.cs` **4,000** · `Sidebar.UI.cs` **7,500** · `Sidebar.Customizer.UI.cs` **4,500** · `Sidebar.Host.cs` **2,500** = **23,500** | Ch 26 counted the platform last and counted the customizer; ch 25's document file is the better home for the reducer + wire than either of ch 26's two files |
| **A5** | Cover palette → `Entities/Palette.cs` (CORE, Wave 1, owner A) + `Entities/Palette.Host.cs` (SHELL, Wave 4, owner L). **Not** `Platform/Design.cs` | Six chapters read the graded plane as ground; it is a per-image table with a TTL, which is an entity concern, not a token. It lands in Wave 1 because five chapters' grounds depend on it |
| **A6** | Drag → `Platform/Drag.cs`, owner L, shipped in the **first week** of Wave 4 | It is used by rows, cards, the sidebar, the playlist page and the tab strip — not the sidebar's and not the shell's. Owners I and J consume its rule tables; written second it is written twice |
| **A7** | Actions and extensions → `Platform/Actions.cs` (CORE: descriptor, targeting, the one action table) + `Platform/Actions.UI.cs` (menus, icons), owner **I**, landed before owner J's picker. `PinRowRule` goes the other way, into `Sidebar.cs` | Plan §5 left it between I and J and both consume it; ch 01 already assumes I builds the table. Getting it wrong costs a second descriptor type |
| **A8** | `AppUpdateToasts` → `Platform/Notify.cs` (CORE). Not `Screens/ReleaseNotes.cs`, not `Platform.cs` | A 12-fact pure decision table with its own test file can live once. It is a notification decision, so it lives with the notification decisions |
| **A9** | Notifications → ONE stack: `Platform/Notify.cs` (CORE: policy, prefs, escalation, merge/filter/read-state, `AppUpdateToasts`) + `Platform/Notify.Host.cs` (SHELL: WinRT toasts, Action Center, both schedulers, AUMID, the activator). Owner **I**, **Wave 4** — not Wave 6. The panel and its rows stay in `Shell/Shell.UI.cs`, and **`Entities/Notification.cs` + `Notification.UI.cs` are not created** — ch 19 §9.4 withdraws its own proposal in terms ("a notification is not an entity") | The in-app panel is shell chrome and shares the model with the OS half; splitting them across two waves and two owners is how the two halves disagree about read state |
| **A10** | `WaveeTips` / `WaveeTipsCore` → `Shell/Shell.cs` CORE + `Shell/Shell.Host.cs` SHELL (ch 19); ch 28 drops its claim | A dual claim with no defer; the tips are shell overlays, and ch 19 specifies them |
| **A11** | `ToastEscalator` → decision half in `Platform/Notify.cs`, WinRT half in `Platform/Notify.Host.cs` | Chs 14 and 19 already agree on the split and disagree only on the two file names. One rename settles it |
| **A12** | Stage → `Shell/Stage.cs` + `Shell/Stage.UI.cs` (owner K, Wave 4) own `StageChrome`, `StageIdentity`, `StagePanes`, `StageLayout`, `StageArm`, `StageInk`. Ch 22 drops `Lyrics.Stage.UI.cs`; the lyrics pane inside the stage lives in `Lyrics.UI.cs`. Both chapters have now restated their numbers, so the contested ~500 lines are counted **once**: ch 21 §9.5 moved `Stage.cs` 400 → **500** (its earlier figure omitted `StageInk`) and ch 22 §9/§11-48 moved `Lyrics.UI.cs` 2,600 → **3,500** and dropped `Lyrics.Stage.UI.cs` | Ch 22's audit reconciled the ownership before either chapter restated its number, so ~500 lines were briefly counted twice or by nobody. Verified 2026-09-12: both restatements are in the chapters |
| **A13** | Video → `Shell/Video.cs` (CORE) + `Video.UI.cs` + `Video.Host.cs`, owner K, Wave 4. `Playback/Playback.Video.cs` stays the decode/host only, owner H, Wave 3. The rail's video-panel body is K's, inside `Rail.UI.cs` | Ch 21 asked for exactly this sentence and the plan did not say it: `Playback.Video.cs` is not one pixel of UI, and four surfaces had no file at all |
| **A14** | `Diagnostics.UI.cs` → 2,300, including the lyrics inspector (≈600), which moves out of the lyrics files per ch 22 and gains a row in ch 27's table | A cross-chapter edit that was decided in ch 22 and never applied in ch 27 |
| **A15** | Home → ch 11's five files plus ch 12's two: `Home.cs`, `Home.UI.cs`, `Home.Cards.UI.cs`, `Home.Artists.UI.cs`, `Home.Page.cs`, `Home.Customizer.cs`, `Home.Host.cs`. Owner P | Three chapters, one owner, three file sets summing to roughly the same total. Take the finest split — it is the only one with a SHELL file, and Home has none today |
| **A16** | `Platform/Controls.cs` → named partials on day one: `Controls.cs`, `Controls.Cta.cs`, `Controls.Art.cs`, `Controls.Picker.cs`. Ch 00's 4,500-6,000 is the **envelope**; chapters 02's and 01's shares sit INSIDE it | The three chapters' shares cannot all fit inside ch 01's proposed ~1,200 file budget; ch 00 counted the whole file and is the envelope |
| **A17** | §2's own arithmetic is corrected and every subtotal recomputed from its rows: the old `Shell/` header said 21,600 against rows summing 26,800; `Entities/` said "40 files ~23,000" against 36 files summing 24,000 | A tree whose totals argue with its own rows cannot carry a budget conversation. Ch 22 spotted half of it; the `Entities/` half was recorded nowhere but the index |
| **A18** | The 10-phase runtime provisioning card (`PlaybackRuntimeSetupCard` 1,030, ch 19's source) → a named partial **`Screens/Setup.UI.Runtime.cs`** (1,100), **written by owner I in Wave 4 and mounted by owner R's wizard in Wave 6**. `+Shell.Overlays.UI.cs` keeps the banner, the gate and the mount, not the body | Ch 19 §9.4 budgets the card 1,100 under `Screens/Setup.UI.cs` because the shell's banner and the wizard's Local-playback page render the *same* provisioning UI; the earlier §2 listed the card among `Shell.Overlays.UI.cs`'s contents and budgeted **nothing** for it. Putting the body in `Screens/` and the writing in Wave 4 satisfies both: one implementation, and it exists for the Wave-4 shell gate. This is the `+Screens/Settings.UI.Video.cs` precedent, inverted |

### 9.5 The surfaces that now have owners

Every row the index listed as unowned or unslotted (its §5, **20 rows**), plus three the plan splits, re-budgets or found itself — **23 rows** here: Recents and History are split to two owners and two waves, OS surfaces is a re-budget rather than an unowned surface, and the runtime provisioning card is A18's.

| Surface | 0.3 home | Owner | Wave |
|---|---|---|---|
| Shared detail frame | `Entities/Detail.cs` + `Detail.UI.cs` | M | **4.5** |
| `Track.Table.cs` + `Track.Drawer.cs` | `Entities/Track.*` (four files) | M | 4.5 / 5 |
| Recents | `Entities/Recents.cs` + `Recents.UI.cs` + `Recents.Page.cs` | P | 5 |
| History | a `Shell.History` section in `Shell.cs`, `history.json` / `play-log.json` / `play-recency.json` in `Shell.Host.cs`, the page in `+Shell.History.UI.cs` | I | 4 |
| Video surfaces, placement machine, override flyout body | `Shell/Video.{cs,UI.cs,Host.cs}` + `+Screens/Settings.UI.Video.cs` | K | 4 |
| Immersive stage | `Shell/Stage.cs` + `Stage.UI.cs` | K | 4 |
| Toast + notification stack | `Platform/Notify.cs` + `Notify.Host.cs`; the panel in `Shell.UI.cs` | I | 4 |
| Action + extension platform | `Platform/Actions.cs` + `Actions.UI.cs`; `PinRowRule` → `Sidebar.cs` | I | 4 |
| `local` route | the `DetailKind.Playlist` arm of `Playlist.Page.cs` | O | 5 |
| Discography page | `Entities/Artist.Discography.cs` (~1,300); `disco:` is in the route table | N | 5 |
| Home customizer + `home-layout.json` + the section drill page | `Home.Customizer.cs`, `Home.Host.cs`, `Home.Page.cs` (`Home.SectionPage`); `home-customize`, `home-section:`, `browse-section:` added to the Wave 5 probe list | P | 5 |
| Browse page | `Entities/Browse.Page.cs` (857 lines of 0.2.9 with no home) | P | 5 |
| Sidebar customizer + `sidebar-layout.json` persistence | `Sidebar.Customizer.UI.cs` (sequenced **last**) + `Sidebar.Doc.cs` + `Sidebar.Host.cs` | J | 4 |
| Cover palette plane | `Entities/Palette.cs` (CORE) + `Palette.Host.cs` (SHELL) | A / L | 1 / 4 |
| Drag layer | `Platform/Drag.cs`, first week of the wave | L | 4 |
| Prerelease surface | `Album.Page.cs`: the route, the kind-138 resolve, the countdown card, the pre-save heart swap, the greyed pending rows, the "N of M songs" tile | M | 5 |
| `Entities.SeedFake()` | `Entities/Entities.Fake.cs` (≈1,100), ch 31 as its specification | Q | 5 |
| OS surfaces | `Playback/Playback.Os.cs` keeps SMTC, taskbar, jump list and the app icon, budget raised to ch 14's 840; the jump list ships behind the `AttachHistory` / `AttachLibrary` late-binding seam with the category empty until Waves 1 and 4 attach | H | 3 |
| Appearance-preferences plumbing | `Platform/Prefs.cs` (the four epochs) + `Platform/Platform.cs` (the store — whose **boot half is orchestrator-owned from Wave 0**, because `Main` and Waves 2-4 call it; §5 Wave 1) | L / S | 4 / 6 |
| Runtime provisioning card (A18, **CONFIRMED §9.6 Q9, 2026-09-12**) | `+Screens/Setup.UI.Runtime.cs` (1,100), the body; the banner, gate and mount in `+Shell.Overlays.UI.cs`; mounted again by R's wizard in Wave 6 | I | 4 |
| Friends | the friends **edge and columns** (`EdgeTable<FriendEdge> Friends` in `Edges.cs`, §4.3) land with the other edges; the **panel** lands with the rail. Ch 21 records it as data gap G6; **CONFIRMED, §9.6 Q5, 2026-09-12** — no `Entities/Friends.cs` | B / K | 1 / 4 |
| `connect-diagnostics` | its own `RouteKind` in §4.11's table — **the fix is decided (§9.6 Q6, 2026-09-12); the GitHub issue is drafted, awaiting Christos's approval, no number yet** | I | 4 |
| Lyrics inspector | `Screens/Diagnostics.UI.cs` (+≈600), A14 | S | 6 |

### 9.6 Answered, 2026-09-12 — Christos's decisions on the nine open questions

The nine questions below were open at the previous revision. Christos answered all nine on 2026-09-12. Each entry
keeps the original question text as the record, then adds the **answer** and the **consequence**: where else in
this plan, the index and the named chapters the decision had to land. §9.6's own title changes from "Open
questions" to this answered record for the same reason G3 never lets a parity item sit unchecked: a decision
recorded here and not applied everywhere it touches is the exact failure this pass exists to prevent.

1. **The lyrics env-var probes versus CLAUDE.md's ban on env-var switches.** 0.2.9 carries `WAVEE_LYRICS_DEBUG`
   (a behaviour switch) and `WAVEE_LYRICS_ADVANCE_PROBE` / `WAVEE_LYRICS_OPEN` / `WAVEE_LIVE_LYRICS_SCROLL_PROBE`
   (verification switches), and CLAUDE.md bans both forms. Ch 22 (a) proposed deleting the debug pill outright — the
   inspector already shows a superset of it behind the persisted `diag.developerMode` — and turning the probes into
   **`Screens/Diagnostics.Probe.cs` CLI entries** (`--lyrics-advance-probe`, with `--probe-out` / `--probe-*-frames`
   replacing the `EnvInt` knobs), beside the `--perf-bench` / `--startup-bench` / `--crash-probe` arms the app
   already has. They cannot be in-app buttons: they drive the frame loop themselves and refuse `--fake`.
   **ANSWER: KEPT, contrary to ch 22's proposal.** The pill survives, re-homed on the persisted `diag.developerMode`
   flag; only the `WAVEE_LYRICS_DEBUG` env var is deleted (CLAUDE.md bans env-var switches, not the surface). The
   three verification env vars still become `Diagnostics.Probe.cs` CLI entries exactly as ch 22 (a) describes.
   **CONSEQUENCE:** ch 22 KEEPS its W21 wireframe and its budget for the pill inside `Lyrics.cs`/`Lyrics.UI.cs`
   (no line-count change — nothing was ever subtracted for a deletion that didn't happen); its §9 (a) paragraph is
   restated as "kept, gate changed" rather than deleted; W23 now hosts only the `FG_STAGE_RECTS` retirement, not the
   pill; §0 item 15, the §1.1 caption, both §3 debug token rows and parity item 89 are corrected to match. This
   plan's §2 already counted the pill inside the lyrics files — that was correct all along and needed no change.
2. **The concert / social toast's launch argument** (ch 14 DATA GAP 1). `ToastEscalator` builds
   `"wavee://open?route=" + Escape(s.ActionUri)`, but `ActionUri` is a `spotify:concert:<id>` or an
   `https://concerts.spotify.com/...` — not a route key — so `GoDeepLinkOpen` does not compose and `IsKnown` rejects
   it: the click almost certainly lands nowhere. The in-app panel row does it correctly, through
   `RichText.RouteForUri` first. **UNVERIFIED at runtime** (`--fake` cannot produce a live gander row; the
   notification simulator can). Port the line verbatim, or port it *through* `RouteForUri`?
   **ANSWER: FIXED, not ported verbatim.** Route it through `RichText.RouteForUri` the way the in-app panel row
   already does, so the click resolves. This is a deliberate divergence from 0.2.9. **CONSEQUENCE:** needs a
   GitHub issue and a `(#n)` CHANGELOG bullet — **neither is filed yet**; ch 14's parity item 29 and DATA GAP 1 are
   restated as "differs from 0.2.9, deliberately — (#n)" so a side-by-side reviewer does not file it as a
   regression.
3. **`ReleaseNotifier` never calls `Silent()`** (ch 14 DATA GAP 4). `DaylistNotifier` honours `policy.Sound`; the
   release-drop toast is the one Wavee toast that always makes a sound regardless of the preference. Match 0.2.9, or
   fix it? **ANSWER: FIXED.** `ReleaseNotifier` gains the `policy.Sound` check `DaylistNotifier` and `ToastEscalator`
   already have. **CONSEQUENCE:** same treatment as Q2 — issue, `(#n)` CHANGELOG bullet (neither filed yet), and ch
   14's parity item 31 restated as a deliberate divergence.
4. **The album merch table** (ch 05 D8). Merch is not an entity: "Featured on" and "Similar albums" are ordinary
   `EdgeTable<NoEdge>` rows, but merch needs a small `MerchTable` (`Name`, `Price`, `ImageId`, `ShopUrl`) plus
   `EdgeTable<NoEdge> AlbumMerch`. §2 had no row for it and §4.3 no table. **ANSWER: ADDED.** Both land in Wave 1
   with the other tables and edges (owner B, inside `Edges.cs`); ch 05 D8 is the specification. **CONSEQUENCE:**
   `Edges.cs`'s §2 row grows 900 → 980 (+80, UNVERIFIED — ch 05 D8 gives a shape, not a count); §4.3's code sample
   gains `Merch`/`MerchTable` and `EdgeTable<NoEdge> AlbumMerch`; `Entities/`'s subtotal moves 58,970 → 59,050. The
   album trailing band keeps its merch row and its parity items unchanged.
5. **The friends file.** Ch 21 records friend activity as data gap G6 and proposes a shape — a synthetic parent,
   `EdgeTable<FriendEdge> Friends` with `FriendEdge(UserSlot, TimestampMs, TrackSlot, AlbumSlot, ArtistSlot,
   ContextSlot)` plus a `Signal<FriendFeedState>` on `Spotify.Connect` — but proposes no file. This plan already
   puts the edge and the columns in Wave 1 with the other edges (owner B) and the panel in Wave 4 with the rail
   (owner K). **ANSWER: SPLIT CONFIRMED**, exactly as the plan already has it. **CONSEQUENCE:** no
   `Entities/Friends.cs`; §2's "two notes" now say CONFIRMED rather than proposed; §4.3's code sample gains
   `EdgeTable<FriendEdge> Friends`; the index and ch 21 are marked confirmed rather than open. No arithmetic moves —
   the edge was always folded into `Edges.cs`'s existing budget.
6. **`connect-diagnostics` needs a GitHub issue before the fix.** The page is renderable by `PageFor` but absent
   from `ShellRoutes.s_exact`, so its deep link is refused and its history row is inert (ch 27 §9.6). §4.11's table
   fixes it by construction — but CLAUDE.md's "every fix references its issue" means the issue comes first, and no
   issue exists today. **ANSWER: the orchestrator drafts it for Christos's approval; nothing has been filed.**
   **CONSEQUENCE:** recorded everywhere the fix is cited as "issue drafted, awaiting approval — no number yet",
   with the `(#n)` placeholder left in place (§4.11's note, §9.5's `connect-diagnostics` row, ch 27 §9.6 and its
   route table). This is still an action for Christos to approve; nothing in this plan files it.
7. **The API-console debug helpers: keep or delete?** `ApiDebugBodyBuilder` / `ApiDebugExecutor` / `ApiDebugProto` /
   `ApiDebugProtoDecomposer`, ~1,400 developer-only lines behind `DeveloperMode.Enabled`. Ch 27 §9.5 offered deletion
   "as a live alternative the plan never makes". §2 carried them as `+Diagnostics.Api.cs` (1,400) so the number was
   not silently lost — but that is 1,400 lines of Wave-6 port for a surface with one door, and ch 27's own §9.6 says
   the API console's route is developer-gated in `PageFor`. **ANSWER: DELETED.** The four helpers and the console
   they serve (`ApiConsolePage`, 329 lines — it has no function without them) are both dropped. **CONSEQUENCE, all
   applied:** `+Diagnostics.Api.cs` removed from §2 (`Screens/` 23 files/20,200 → **22 files/19,000**, the tree
   137 → **136** files); `ApiConsole` removed from §4.11's `RouteKind` enum (`s_exact` 16 → 15 names, "29
   registered routes" → 28) and from the "added because the sketch missed them" list, replaced with an explicit
   "dropped" entry; the `api-console` route removed from the G2 probe list in §8 and from Wave 5/6's gates in §5
   (it is no longer deferred to Wave 6 — it does not exist at all); `Diagnostics.UI.cs`'s description in §2 no
   longer names the API console page (budget unchanged at 2,300 — ch 27 owns the precise re-split). In chapter 27,
   the API-console parity items (72, 72a), the W25 wireframe, the §9.5 unowned-types row and the §9.6 route table's
   `ApiConsole` row are all STRUCK with this reason and this date, never deleted. Every other citation of
   `api-console`/`ApiConsole` found in this pass is listed in the return below.
8. **`SetupSession` / `SetupSession.MarkerEpoch` has no named home** (ch 29 §9.11 item 5) — a 324-line
   process-static signal the app root subscribes to, in `Features/Setup/SetupSession.cs`, and it is **not** inside
   ch 28 §9.5's "the rest of `Features/Setup` (1,032)". `Screens/Setup.cs` (CORE) is the obvious home and would take
   it to ≈1,150, but no chapter says so. **ANSWER: the orchestrator's call**, under this plan's own rule that the
   plan decides where code lives: `Screens/Setup.cs`. **CONSEQUENCE:** `Setup.cs`'s §2 row grows 950 → **1,150**
   and gains `SetupSession`/`SetupSession.MarkerEpoch` in its description; `Screens/`'s subtotal absorbs the +200;
   chapters 28 and 29 — each of which said it had no home for this signal — now record the home.
9. **Confirm A18** (the runtime provisioning card's file and wave). Ch 19 §9.4 budgets `PlaybackRuntimeSetupCard`'s
   1,100 lines under `Screens/Setup.UI.cs`; the pre-review §2 listed the card among `+Shell.Overlays.UI.cs`'s
   contents and budgeted **zero** for it. A18 splits the difference — the body is `+Screens/Setup.UI.Runtime.cs`,
   written by I in Wave 4 because the Wave-4 shell gate needs it, mounted by R's wizard in Wave 6.
   **ANSWER: CONFIRMED as written** — the alternative is two implementations of the same ten-phase card.
   **CONSEQUENCE:** §9.5's runtime-provisioning-card row now reads CONFIRMED; chapters 19 and 28 are marked
   confirmed rather than open.

**Arithmetic this record moves** (§2's Totals table, recomputed from its own rows): `Entities/` 58,970 → **59,050**
(Q4, +80); `Screens/` 20,200 → **19,000** (Q7 −1,400, Q8 +200); every other folder unchanged. File count 137 →
**136** (Q7 removes one file; Q8 adds no file, it folds into an existing one). Grand total 171,135 → **170,015**.
