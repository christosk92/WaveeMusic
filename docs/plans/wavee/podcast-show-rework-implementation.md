# Podcast rework — the show reader and the episode page (Wavee 0.3)

## Implementation update - 2026-09-19, supplied vc3/vc4 evidence

This update supersedes the old endpoint/unit/capture-gate assumptions below. Implementation and integrated validation are recorded in [the implementation status](podcast-20260919-implementation-status.md). The owner approved the full podcast plan, all 17 comparison findings, real pitch-preserving speed, and one integrated final gate. No further user captures are needed.

Evidence: `verycomplex3.saz` #420/#430/#446 establish canonical saved-episode IDs; #422 establishes paginated listen-later discovery; #594 establishes replies; #605/#624 establish the web episode-detail contract. `verycomplex4.saz` #312/#313 establish compound explicit completion; #410 establishes repeated revisions grouped by URI and timestamp tie-breaking; #414/#420/#432 establish 1.5/1.9/1 playback settings. Source-confirmed recommendation/reaction contracts come from the already extracted official UI. The Spotify notification preference remains explicitly unavailable.

Implementation owners: progress/model/telemetry; playback/Connect/audio resolution; engine PCM time stretching; orchestrator catalog/list/API/UI integration. Files are disjoint. Only the orchestrator builds/tests/launches, after assembly.

```csharp
// Zero is a valid explicit position, not a sentinel for resume.
Playback.PlayEpisode(id, context,
    new Playback.EpisodeStart(Playback.EpisodeStartKind.Position, chapter.StartMs));
// Show membership is playlist4, with canonical item IDs and a coherent disk baseline.
Entities.EnsureEdge(FetchEdge.ShowEpisodes, show.Slot);
```

```text
Show reader                       Episode reader
+ artwork / publisher / rating    + artwork / owning show / facts
+ follow / visit primary action   + resume / beginning / save / completion
+ find / filter / sort            + description / media
+ grouped episode rows            + chapters / transcript / comments
+ related shows                   + related episodes
```

Final gates: Wavee Debug + Release, Wavee.Tests, Pester release tests, engine Debug + Release + VerticalSlice/canon as applicable, ARM64 publish, fake UI and normal diagnostic smoke. Raw captures and credentials never enter fixtures. Track every remaining item and gate outcome in the session as-built report; an implementation claim requires passing evidence.

---


Written 2026-09-18 against the worktree `C:\WAVEE\wavee-0.3` @ `02f22cce` (+ the uncommitted 09-18 playback/Connect
fix wave). The visual target is the approved prototype `docs/plans/wavee/podcast-show-episode-mica.html` (published at
https://claude.ai/artifact/92Ly7eaziCbWB4r1x4sgYU) — it is the parity reference for this plan the way the 0.2.9
Release build is for the chapters. Style precedent: `library-rework-implementation.md`.

Every claim about the tree was read in source on 2026-09-18. Every wire shape is quoted from the retired WinUI app at
commit `0d0429a0` of `C:\wavee\WaveeMusic` (read with `git show`, never checked out), where each **read** path below ran
in production until June 2026.

Rules baked in: **no legacy paths** (the bordered episode card, the two `SelectorBar`s, the "disc is the row's only
affordance" rule of ch 09 items 20–21 and the per-load `LookUpResumePoint` round trip are *deleted*, not hidden);
**derived facts live on the model** (the visit, listen-next, the ledger, "new since", the month groups, completion are
pure rules in CORE with tests — the UI renders them); **no page-side fetch windows** (a page demands its whole model;
`Fetch` batches 300/POST); **no environment switches** (the wire check is a Diagnostics action); **no source-text
tests**; **props freeze at mount**; **every fix references its issue** (§13); **subagents never build, test, or touch
git** — the orchestrator runs one integrated final validation gate after all implementation waves (owner decision, 2026-09-19).
**Never run `--spotify-*` probes from an agent shell** (memory `spotify-probes-clear-credentials`): hash verification is
§6.0's in-app action, run by the user.

---

## 0. The decision, in one screen

| | 0.3 today | After this plan |
|---|---|---|
| Show rail | cover · "Podcast" · title · `Publisher · N episodes` · Play + ♥/share · 6-line blurb | + **★ rating** (rate flyout) · **topic words** · badges (exclusive/E/video) · cadence in the meta line · **played ledger** · a **visit-dependent primary** (Follow → Resume · 23 min left → Play latest) · trailer / bell / ⋯ |
| Show right column | 2 `SelectorBar`s · "Listen next" banner · bordered cards · load-more | **sticky word rail** (filter · find · sort) → a **visit head** (*start here* doors + about \| *continue* hero + up-next + *new since you were here* \| *all caught up*) → **episode reader** (month groups, big numerals, hairlines, hover actions, now-playing) → **more like this** shelf |
| Same page for everyone | yes | **`ShowVisit.Of` → New · Returning · CaughtUp** decides the head and the rail's primary |
| Episode | a card; a `spotify:episode:` link *plays* | **`episode:` route** — same two panes: identity rail │ reader tabs `about · chapters · transcript · comments` → next/previous doors → more from this show → you might also like. Deep link *opens* the page |
| Progress | device-local, written by nothing (the column is never written in this build) | **one account-wide herodotus `ListCurrentStates` hydrate** at login + a **local mirror** on pause/end + **mark played/unplayed** through the existing resume-point write queue |
| Filter / sort | two plain `Signal<int>`, forgotten on leave | persisted per show in one capped setting (`podcast.views`, 64 shows LRU) |
| Data sources | `ShowV4`, `EpisodeV4` (6 + 7 fields) | + 9 more proto fields (decode only) · 8 pathfinder ops · herodotus batch · transcript GET (§6) |
| Writes with no captured endpoint | — | rate · comment/reply/react · new-episode notifications: **drawn, disabled with a reason**, each its own wave gated on a capture (§8 wave P10) |

What does **not** change: `Detail.Frame` stays the one frame (rail geometry, breakpoints 820/660/560, the 96-DIP
collapsed strip, rail width persistence, the palette tone plane); `Edges.ShowEpisodes` paging and `Episode.Rules`'
paging math; the library's `LibraryShowPane`; `Playback.PlayContext(show, episode)` as the play verb.

---

## 1. Context

### 1.1 What 0.3 already has (read before writing a line)

- `Entities/Show.cs` (275) — `ShowFields {Title, Image, Publisher, Identity, About}`, `ShowTable` (4 `StringId` + `EpisodesAsked`), `ShowShape`.
- `Entities/Episode.cs` (410) — `EpisodeFields {…, About, Progress}`, `EpisodeTable` (`DurationMs, PublishedAt, ProgressMs, Show`, three authority bytes), `Episode.Rules` (`:319-409`: `Pct`, `InProgressFloor .01`, `PlayedCeiling .98`, `View`, `ResumePick`, paging).
- `Entities/Show.Page.cs` (487) — `PageHost` → `Detail.Frame(Config.Show)`; `EpisodeListHost` = one `ItemsView.CreateBound` over `ListItem(Kind, Row)` with a `ContentType` split (head / card / foot), `RepeatLayout.VariableList(124f)`, scroll key `"episodes:" + routeKey`.
- `Entities/Show.UI.cs` (178), `Entities/Episode.UI.cs` (244) — the toolbar, banner, card (`BoundRow` is the bind-API canon: `item.Text/Image/Color/Show/Value/Invoke`).
- `Entities/Detail.UI.cs` — `Identity` (`:56`), `FrameActions` (`:218`), `FrameSlots` (`:243`, **closed set of 12, presence-equality**), `RailColumn` (`:1019-1113`, **hard-coded 12 rows + a fixed `PlayPill + [Save][Share][More]`**), skeleton twin `RailSkeletonColumn` (`:1126`) + `Skeleton.RailPlanFor` (`Detail.cs:874`).
- `Entities/User.UI.cs:80-239` — the word rail from the library rework; the reusable `RailBar`/`RailWord` are **private**, `WordRail` is bound to `LibraryNavSort`.
- `Entities/Artist.Reader.cs:355-375, 791-804, 1305-1323` — the **sticky-plane canon**: `PersistentPrefixCount` + a RAW (non-component) `.Sticky(0f)` element + `ScrollOptions.ItemClipTopInset`/`ItemClipTopFadeBand`.
- `Spotify/Spotify.Decode.cs:838-896` — hand `ProtoReader` decoders; `Spotify.Decode.Pathfinder.cs` — forward-only `Utf8JsonReader` folds, `Export` dispatch on the `data.*` root (`:1105`), **no `podcastUnionV2`/`episodeUnionV2` arm**.
- `Spotify/Spotify.Api.cs:1105-1248` — `Query(Op, Hash, Web)`, `Vars`, `Pathfinder(...)` (logs `rejected (400) — the persisted hash is probably stale`); `Serves` (`:666`) **must list a new op or it silently seals**; `GetText` (`:1623`, the `App-Platform: Android` recipe).
- `Spotify/Spotify.Telemetry.cs:660-752` — the resume-point **write** queue (2 s flush) and `ResumeMs` (one `ListResumePointRevisions` per episode load); `Protos/herodotus.proto` already declares `ListCurrentStatesRequest/Response` — **generated, never called**.
- `Entities/Entities.Fake.Album.cs:343-378` — `StageShows`: 8 shows × 8–12 episodes, one at ⅓ progress.

### 1.2 The five defects this plan removes, traced

1. **Progress is dead on real data.** `Fetch.Routes.cs:207-211` seals `EpisodeFields.Progress`; the only writers are sqlite load and the fake seed. `Playback.Host.Context.cs:1113-1125` sends pause/end positions to herodotus and **never to `EpisodeTable.ProgressMs`**. The filter, the banner and the chip are inert for every real user.
2. **One page for every visitor.** Nothing derives "new / returning / caught up"; the rail's primary is always Play.
3. **No episode destination.** No `RouteKind.Episode`, no `DetailKind.Episode`, no `ActionTarget.ForEpisode`; `Shell.cs:616` turns an episode uri into a play.
4. **The frame cannot carry the rail the design needs** — no slot for rows, a fixed CTA.
5. **The toolbar scrolls away and vanishes under 540** (`EpisodeHead.Render`: `if (!m.Vertical) kids.Add(EpisodeToolbar…)`).

---

## 2. Wireframes (prototype parity; DIPs)

### W1 — Show, returning, wide (rail 280 · reader ≥ 300)

```
├──────────── 280 ────────────┤│├───────────────────────────── reader ─────────────────────────────┤
┌─────────────────────────────┐│┌ STICKY · 44 · FillLayer over mica ──────────────────────────────────┐
│ cover 256 r8 Elevation.Card ││ all 128  unplayed 41  in progress 3  played 84   ⌕ Find…  │ newest oldest
│ Podcast  [exclusive] [E]    │││ ──                                                        ──      2-DIP tone underline
│ the long signal   DetailHero││└──────────────────────────────────────────────────────────────────────┘
│ Northlight Audio   13/Second││ continue   up next from your progress · 3 in progress · 41 unplayed    ← Pivot 24/30/300 lowercase
│ ★ 4.8  12,431 ratings  →fly ││ ┌ hero r8 · tone-gradient ──────────────────────────────────────────┐
│ 128 episodes · weekly · 2023││ │ [96 cover ⁹]  Dead air                                  (▶ Resume) │
│ ▓▓▓▓▓▓▓▓▒░░░░  4-DIP 3-part ││ │               17 min  left of 31 min   ← 30/300 tone · 12.5 Second │
│ 84 played · 3 in progress ·…││ │               ▓▓▓▓▓▓▓░░░░░ 4-DIP                                    │
│ (▶ Resume · 17 min left) ♥  ││ └────────────────────────────────────────────────────────────────────┘
│ ▷trailer  🔔  ⤴  ⋯   36 FABs ││ [10 Tallinn, 1983 · 55 min] [11 The engineer's…] [12 Shortwave…]  mini 3-up
│ documentary history radio … ││ new since you were here 2                                mark all played
│ blurb 12.5/Second ≤5 lines  ││ ─ 14 [56] What the archivist kept  · 2-line blurb · NEW Sep 15 · 28 min ⌸
└─────────────────────────────┘│ episodes 128
                               │ september 2026                       ← group 18/300 lowercase TextTertiary
                               │ ─ 14 [56] title 14/600 … (hover: ⊕queue ✓mark ♥ ⋯ ▶36)
                               │ more like this   [148 card]×n  PagedShelf
```

### W2 — Show, new visitor: the head swaps, the rail's primary is **Follow**

```
│ (+ Follow)  (▶ Episode 1)   │ start here   a serial — best heard in order
│ ▷ 🔔 ⤴ ⋯                    │ ┌ begin here ¹ ─────┐ ┌ trailer ──────────┐ ┌ latest ¹⁴ ────────┐   doors: auto-fit min 230
│ topics…                     │ │ Numbers, then…    │ │ Hear what it…     │ │ What the archiv…  │   lead door = tone gradient
│ (no blurb — it moved right) │ └ (▶) 38 min ───────┘ └ (▶) 2 min ────────┘ └ (▶) Sep 15·28 min ┘   serial: ep1 leads; else latest
                              │ about        full-measure RichText ≤ 68ch · facts: episodes · new episodes · running since · listen
```
Caught up: the head is one line — `you're all caught up` (Pivot 24) + `New episodes usually land on Tuesdays.`

### W3 — Episode row states (replaces ch 09 W7)

```
REST        ─ num 30/300 Tertiary (w46, right) │ art 56 r6 │ title 14/600 ≤2 · chips │ blurb 12.5 ≤2 │ Sep 15 · 28 min ⌸
HOVER       whole row FillSubtleSecondary; action cluster fades in (queue · mark · ♥ · ⋯) + 36 tone disc
IN PROGRESS meta leads with "23 min left" 12/600 tone; 3-DIP rule ≤ 340 wide under the meta
PLAYED      text + numeral + art at 58 % · "✓ played" · no rule
NOW PLAYING 3-DIP tone bar at the row's left edge · title in tone · Controls.Equalizer before the title
NEW         "NEW" 10.5/700 tone, tracking +60, before the date (published after the show's last play)
ROW = LINK  the whole row is Focusable, Role=Link → Shell.GoTo(episode); the disc plays. (Reverses ch 09 items 20-21.)
NARROW <540 numeral hidden · art 48 · cluster collapses to the disc only, always visible
```

### W4 — Episode page, wide

```
│ cover 256 (episode art)  ⁹  ││ about   chapters   transcript   comments 286      ← BIG rail: Pivot 20/300, active 400 + underline (sticky)
│ ▣ The Long Signal  → show   ││ about: RichText 14/1.65 ≤68ch; "(12:34)" → tone link → Playback.SeekTo
│ Dead air   22/28/600        ││ chapters: 0:00 ●─ Cold open / subtitle / tag     rail 18: track 2 + dot 10; done=tone, live=halo+½ fill
│ Sep 1, 2026 · 31 min · 17 l…││ transcript: ⌕ search · [English|Suomi] · sections (12/600 caps + time) · 15.5/1.75 sentences; said=Primary,
│ [episode 9] [E] [🔒 subs…]  ││            saying = tone plate; click a sentence = seek
│ ▓▓▓▓▓░░░  14 min in · 17 left││ comments: composer (disabled: "posting isn't available yet") · pinned first · avatar 32 · reactions pill · lazy replies
│ (▶ Resume · 17 min left)    ││ next in the story   [next ¹⁰ door] [previous ⁸ door]
│ ♥ ⊕ ✓ ⤴ ⋯                   ││ more from this show    see all 128      4 reader rows
│ blurb ≤5                    ││ you might also like    PagedShelf of episode cards
```

---

## 3. Component trees and the owner split

### 3.1 Show

```
Show.Page → PageHost : Component                         keyed "show-page:" + routeKey
└─ Detail.Frame(FrameSpec { Identity = IdentityOf(show, facts), Config = Detail.Config.Show,
   │                        Actions = { CoverDrag, More = ShowMenu }, Slots = _slots })
   ├─ rail  (Detail.UI RailColumn — new insertion points, §5.4)
   │   ├─ rail:badges    ← Slots.Badges()        Controls.Chip × n
   │   ├─ rail:rating    ← Slots.Rating(width)   Show.RatingRow  → overlay.Open(RateFlyout)
   │   ├─ rail:ledger    ← Slots.Ledger(width)   Controls.LedgerBar + counts line   (visit ≠ New)
   │   ├─ rail:cta       ← Slots.Primary(accent) + Slots.Satellites()  (replaces the fixed PlayPill group when present)
   │   └─ rail:topics    ← Slots.Topics(width)   Controls.Words.Links
   └─ right: Slots.Episodes(vertical) → Show.Reader (ReaderHost : Component, IPropsHost)
       └─ ItemsView.CreateBound  PersistentPrefixCount = 1, ItemClipTopInset = ReaderRailH
           ├─ [0] RAW sticky element: ReaderRail (Controls.Words ×2 + FindBox)        ← never a component (Artist.Reader.cs:370)
           ├─ [1] VisitHead   (Embed.Comp) StartHere+About | Continue+UpNext+NewSince | CaughtUp
           ├─ [2] Header "episodes N"
           ├─ [3..] Group | Episode.ReaderRow (BoundRow)       from ShowReaderShape (pure)
           ├─ Foot   load-more pill
           └─ Similar  PagedShelf over Edges.ShowSimilar
```

### 3.2 Episode

```
Episode.Page → PageHost                                   keyed "episode-page:" + routeKey
└─ Detail.Frame(FrameSpec { Identity = IdentityOf(episode), Config = Detail.Config.Episode, Slots = … })
   ├─ rail: Attribution = show link row · Badges · Ledger (in-episode) · Primary (Play|Resume|Replay|Play preview) · Satellites (♥ ⊕ ✓ ⤴ ⋯)
   └─ right: Slots.Episodes → Episode.Reader (ScrollView, one column)
       ├─ RAW sticky BIG rail  Controls.Words.Big(tabs)      tabs filtered by what the model knows
       ├─ tab body: About | Chapters (Edges.EpisodeChapters) | Transcript (Transcript.Store) | Comments (Comments.Store)
       ├─ Doors(next, previous)     EpisodeNeighbours (pure)
       ├─ "more from this show"     4 × Episode.ReaderRow
       └─ "you might also like"     PagedShelf over Edges.EpisodeRecommended
```

### 3.3 Where the code lives

| File | Change | Owner |
|---|---|---|
| `Entities/Show.cs` | columns + groups `Facts`, `Rating`; `StagedShow`, commit arms, `ShowShape` v+1 | A |
| `Entities/Episode.cs` | columns `Number, Kind, Flags, PlayedAt`; `EpisodeFlags`; `Rules.Completed`; shape v+1 | B |
| **new:** `Entities/Show.Rules.cs` | CORE, pure: `ShowVisit`, `ListenNext`, `ShowLedger`, `ShowCadence`, `ShowReaderShape`, `EpisodeNeighbours`, `ShowViewPrefs` | R |
| `Spotify/Spotify.Decode.cs` (`:838-896` only) | `ShowV4` cases 68/74/75/83; `EpisodeV4` cases 65/70/72/87/97 | C |
| **new:** `Spotify/Spotify.Decode.Podcast.cs` | pathfinder folds: `PodcastUnion`, `EpisodeUnion`, `RecommendedShows`, `RecommendedEpisodes`, `Chapters` | D1 |
| **new:** `Spotify/Spotify.Api.Podcast.cs` | `PodcastQueries`, request builders, `*Answer` methods, `WireCheck` | D1 |
| `Spotify/Spotify.Api.cs` | `Serves` + `AnswerQuery` arms only | D1 |
| `Spotify/Spotify.Decode.Pathfinder.cs` | two `Export` arms (`podcastUnionV2`, `episodeUnionV2`, `seoRecommended*`) | D1 |
| **new:** `Entities/Show.Podcast.cs` | `Edges.ShowSimilar`, `EpisodeRecommended`, `ShowTopics`, `EpisodeChapters` + `ChapterTable` | D2 |
| `Entities/Edges.Staging.cs`, `Fetch.Routes.cs`, `Fetch.Edges.cs` | `Relation`/`FetchEdge`/route rows for the four edges; `s_show`/`s_episode` routes | D2 |
| `Spotify/Spotify.Telemetry.cs`, `Spotify.Library.cs`, `Playback/Playback.Host.Context.cs`, `Playback.Host.cs` (`:602` only) | `CurrentStates` hydrate, local mirror, delete `LookUpResumePoint` | T |
| `Entities/Detail.cs`, `Entities/Detail.UI.cs` | `DetailKind.Episode`, `Config.Episode`, 6 new `FrameSlots`, rail insertion points, skeleton twin | M |
| **new:** `Platform/Controls.Words.cs` | the generic word rail (promoted from `User.UI.cs`), `Big`, `Links`, count badges | O |
| `Entities/User.UI.cs` | `RailBar`/`RailWord` deleted; the three library rails call `Controls.Words` | O |
| **new:** `Platform/Controls.Podcast.cs` | `Chip`, `LedgerBar`, `StarRow`, `Door`, `FindBox` | O |
| `Shell/Shell.cs`, `Platform/Actions.cs` | `RouteKind.Episode` row, `For` arm, deep link → open; `ActionTarget.ForShow/ForEpisode` | S |
| `Entities/Show.Page.cs`, `Show.UI.cs` | rebuilt on §3.1; `EpisodeToolbar`, `ResumeBanner`, `EpisodeHead/Foot/Slot` **deleted** | P |
| `Entities/Episode.UI.cs` | `ReaderRow` replaces `Row`/`BoundRow`/`Card`; `IsNowPlaying`, `Invoke`; `PlayCircle`/`ProgressRule` kept | E |
| **new:** `Entities/Episode.Page.cs`, `Episode.Reader.cs` | the page + tabs | Q |
| **new:** `Shell/Transcript.cs`, `Transcript.Host.cs`, `Transcript.UI.cs` | model+parser (pure) · store (Lyrics.Store model) · reader | V |
| **new:** `Shell/Comments.cs`, `Comments.Host.cs`, `Comments.UI.cs` | records+rules (pure) · store (no disk cache) · thread UI | W |
| `Entities/Entities.Fake.Album.cs` (+ new `Entities.Fake.Podcast.cs`) | §9 | F |
| `Screens/Diagnostics*.cs` | the "Podcast wire check" action (§6.0) | D1 |
| `Platform/Platform.cs` | `Keys.PodcastViews` | R |
| `assets/loc/{en-US,ko-KR,nl}.json` | §5.12 | O |

Disjoint by construction inside a wave (§8). An agent that needs a file it does not own stops and reports.

---

## 4. Interaction contract

- **Row** click / Enter = open the episode page; **disc** = `Episode.Invoke` (play, or toggle pause when it is the now-playing row); right-click / ⋯ = `Episode.Menu` (play next · add to queue · mark played/unplayed · save to Your Episodes (wave P9) · go to show · share).
- **Rail primary** by visit: `New & !followed` → Follow (accent) + ghost `▶ Episode 1` (serial) / `▶ Latest`; `Returning` → `Resume · {left}` on `ListenNext.Resume`, else `Play latest`; `CaughtUp` → `Play latest`. Play always goes through `Playback.PlayContext(show.Id, episode.Id)`; from listen-next the context order is chronological, from the reader it is the visible order (0d0429a0 `ShowViewModel.cs:1121-1150`).
- **Filter/sort/find** write `ReaderHost` signals; filter+sort persist through `ShowViewPrefs` on change; find does not persist. `Ctrl+F` stays the omnibar — the find box is reached by Tab from the rail.
- **Topic** → `Shell.GoTo(Browse page for topic.Uri)`. **Rating** → overlay flyout (`User.UI.cs:258-272` pattern); stars disabled with `Strings.Podcast.Rate.Unavailable` until wave P10.
- **mark all played** (new-since header) → one `Telemetry.ResumePoint(uri, completed)` per fresh episode through the existing 2 s batch queue + the local mirror.
- **Timestamp link**: `Controls.RichText`'s `onNavRoute` receives `wavee-seek:<ms>`; `Episode.Reader` plays the episode if it is not current, then `Playback.SeekTo(ms)`.
- **Chapters**: click = play-if-needed + seek. Live fill only while `Playback.CurrentId == episode.Id` (else chapter 1 would look permanently lit — 0d0429a0 `EpisodePageViewModel.cs:767-810`).
- Keyboard: every rail word, door, row, disc, FAB and chapter is one tab stop with a visible focus rect; word rails are `Role = TabList/Tab` for the episode tabs, `Role = Toolbar` for filter/sort.

---

## 5. The code

### 5.0 Verified names (read in source 2026-09-18 — bind to these, not to guesses)

| Used as | Is | Where |
|---|---|---|
| `Embed.Comp(props, static () => new T()) with { Key = … }` | component mount; props frozen | `Show.Page.cs:52` |
| `Entities.Ensure(handle, fields)` / `Ensure(span, fields)` / `EnsureEdge(FetchEdge, slot)` | the only demand API | `Entities.cs:1947`, `Show.Page.cs:108-129` |
| `s.Shows.RowFor(uri, authority, known)` / `s.Episodes.RowFor` | staging row | `Decode.Pathfinder.cs:544-566` |
| `s.Run(Relation.X)` … `run.Add()` … `run.EndEvenIfEmpty(in parent)` | edge staging (empty is an answer) | `Edges.Staging.cs:313-320` |
| `FetchRoute.Metadata(ext, groups)` / `.Pathfinder(op, groups)` | route rows | `Fetch.Routes.cs:165-176` |
| `new Query(op, hash, web)` · `new Vars(query)` · `vars.W` · `vars.Finish()` · `Pathfinder(in query, body, ct)` | pathfinder call | `Spotify.Api.cs:1105-1248` |
| `Spotify.Api.Serves(in FetchRoute)` | **a new op not listed here seals silently** | `Spotify.Api.cs:666-683` |
| `Spotify.Api.GetText(url, ct)` — `App-Platform: Android`, no client token | transcript GET | `Spotify.Api.cs:1623` |
| `Rs.ListCurrentStatesRequest/Response`, `Rs.CurrentStateEntry` | generated, uncalled | `Protos/herodotus.proto` |
| **SETTLED 2026-09-19: a `google.protobuf.Duration {seconds = 1, nanos = 2}` in a `CurrentStateValue` oneof (2 Duration · 3/4 markers · 12 context), revisions repeated — see `as-built-20260919.md` "Herodotus resume-point fix"; both rows below were wrong.** 0.3 `ResumePoint { int64 position = 2; }` vs 0d0429a0 `uint32 position_seconds = 1` | **unit differs — T verifies against `ResumeMs`'s own scaling before mapping** | `herodotus.proto`, `Telemetry.cs:737-752` |
| `Authority.Local` > wire; `Authority.Seed` < every wire answer | progress authority ladder | `Episode.cs:70-73`, `Entities.cs:90` |
| `ListOptions.PersistentPrefixCount`, `ScrollOptions.ItemClipTopInset/ItemClipTopFadeBand`, `el.Sticky(0f)` | sticky plane; **the sticky node must be a RAW element of a bound list** | `Artist.Reader.cs:355-375, 791-804` |
| `Controls.Equalizer(playing, color, height, paused)` | now-playing glyph | `Controls.cs:530` |
| `Playback.CurrentId`, `IsPlaying`, `PositionMs`, `SeekTo(int)`, `TogglePlay()` | signals/verbs | `Playback.Host.cs:118-134, 962-963` |
| `overlay.Open(anchor, content, placement, PopupOptions)` | flyout | `User.UI.cs:258-272` |
| `PagedShelf.Create(items, cardAt, …)` · `Controls.ShelfCard(CardData, w)` | shelves | engine `PagedShelf.cs:114`, `Controls.Art.cs:394` |
| `Platform.Settings.Get/Set(SettingKey<T>)`; memory-only under `--fake` | prefs | `Platform.cs:40-49`, `Platform.Settings.cs:56` |
| `Shell.GoTo(Shell.For(uri, title))`; `s_routes` ordinal == `RouteKind` ordinal (pinned by `ShellRoutesTests`) | navigation | `Shell.cs:121-180, 386` |
| per-row fetch failure, in-page find, `ActionTarget.ForShow/ForEpisode`, `Controls.Chip`, a saved-episodes edge | **do not exist** | — |
| `FootprintGateTests` measures `TrackTable` + the `Table` base only | show/episode columns do not trip it; a base column would | `FootprintGateTests.cs` |
| every owned `StringId` must be released in `ReleaseText` | else `ScopeRetirementTests` fails | `Edges.cs:899-910` |

### 5.1 `Entities/Show.cs`, `Entities/Episode.cs` — columns (owners A, B)

```csharp
// ── Show.cs: ShowFields, after About ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>What <c>ShowV4</c> also carries: flags, consumption order, trailer. One group, one authority — one answer fills all.</summary>
    Facts = 1 << 9,
    /// <summary>The pathfinder-only facts (rating, palette tone, exclusive). Own group because own route (the <c>ArtistFields.Chart</c> precedent).</summary>
    Rating = 1 << 10,
    All = Identity | About | Facts | Rating,

[Flags] public enum ShowFlags : uint { None = 0, Explicit = 1, Video = 2, Mixed = 4, Exclusive = 8, MusicAndTalk = 16, CanRate = 32 }
public enum ConsumptionOrder : byte { Unknown = 0, Sequential = 1, Episodic = 2, Recent = 3 }

// ShowTable gains:  Column<uint> Flags; Column<byte> Order; Column<StringId> Trailer;      (FactsAuthority)
//                   Column<ushort> RatingX100; Column<int> RatingCount; Column<byte> MyRating; Column<uint> Tone; (RatingAuthority)
```
```csharp
// ── Episode.cs ────────────────────────────────────────────────────────────────────────────────────────────────────────
[Flags] public enum EpisodeFlags : uint { None = 0, Explicit = 1, Video = 2, Short = 4, Paywalled = 8, PreviewOnly = 16, HasTranscript = 32 }
public enum EpisodeKind : byte { Full = 0, Trailer = 1, Bonus = 2 }

// EpisodeTable gains (all under Identity — EpisodeV4 fills them with the identity, and the row paints them):
//   Column<ushort> Number; Column<byte> Kind; Column<uint> Flags;
// and under Progress:  Column<int> PlayedAt;   // unix s of the newest resume-point revision — feeds "new since"

public static partial class Rules
{
    /// <summary>ONE completion rule (the WinUI app had three: 90 s, 30 s, 0.995).</summary>
    public const int CompletedTailMs = 30_000;
    public static bool Completed(int progressMs, int durationMs)
        => durationMs > 0 && (Pct(progressMs, durationMs) >= PlayedCeiling || durationMs - progressMs <= CompletedTailMs);
    /// <summary>Herodotus says "completed" by OMITTING the resume point: store it as the full duration.</summary>
    public static int ProgressOf(bool hasResumePoint, long positionMs, int durationMs)
        => !hasResumePoint ? Math.Max(durationMs, 1) : (int)Math.Clamp(positionMs, 0, int.MaxValue);
}
```
Both shapes bump their schema version and add the columns to `Cols`/`Save`/`Load`; `Tone`/`Trailer` join `ReleaseText`.

### 5.2 `Entities/Show.Rules.cs` — the CORE rules (owner R; engine-free; every rule has a test, §10)

```csharp
namespace Wavee;

public enum ShowVisitKind : byte { New, Returning, CaughtUp }

public static class ShowVisit
{
    /// <summary>Which first screen the visitor needs. Progress beats follow state: someone 40 episodes in who never
    /// followed is still Returning. CaughtUp = progress exists and nothing resident is unplayed or in progress.</summary>
    public static ShowVisitKind Of(bool anyProgress, int unplayed, int inProgress)
        => !anyProgress ? ShowVisitKind.New : unplayed + inProgress == 0 ? ShowVisitKind.CaughtUp : ShowVisitKind.Returning;
}

/// <summary>Listen-next, ported from 0d0429a0 <c>ShowViewModel.UpdateListenNextEpisodes</c> and made order-aware
/// (the WinUI app fetched <c>consumptionOrderV2</c> and never read it).</summary>
public static class ListenNext
{
    public const float NearComplete = 0.90f;
    public const int UpNextMax = 3;

    /// <param name="pcts">resident episodes, NEWEST FIRST (the edge order).</param>
    /// <param name="playing">index of the now-playing episode of this show, or -1.</param>
    /// <returns>resume index (or -1) and up to <see cref="UpNextMax"/> indices written to <paramref name="upNext"/>.</returns>
    public static (int Resume, int Count) Pick(ReadOnlySpan<float> pcts, ConsumptionOrder order, int playing, Span<int> upNext)
    {
        int resume = playing >= 0 && !Episode.Rules.Played(pcts[playing]) ? playing : -1;
        if (resume < 0)
            for (int i = 0; i < pcts.Length; i++)
                if (Episode.Rules.InProgress(pcts[i]) && pcts[i] < NearComplete) { resume = i; break; }

        int n = 0;
        if (order == ConsumptionOrder.Sequential)
        {
            int anchor = pcts.Length;                                   // chronological = descending index
            for (int i = 0; i < pcts.Length; i++)
                if (Episode.Rules.Played(pcts[i]) || pcts[i] >= NearComplete) { anchor = i; break; }   // newest finished
            for (int i = anchor - 1; i >= 0 && n < upNext.Length && n < UpNextMax; i--)
                if (i != resume && !Episode.Rules.Played(pcts[i])) upNext[n++] = i;
            if (n == 0 && anchor == pcts.Length)                        // never listened: from episode 1 onward
                for (int i = pcts.Length - 1; i >= 0 && n < UpNextMax && n < upNext.Length; i--) if (i != resume) upNext[n++] = i;
        }
        else
            for (int i = 0; i < pcts.Length && n < upNext.Length && n < UpNextMax; i++)
                if (i != resume && !Episode.Rules.Played(pcts[i])) upNext[n++] = i;
        return (resume, n);
    }
}

public readonly record struct ShowLedger(int Played, int InProgress, int ToGo, int Fresh)
{
    /// <summary>Counts over the RESIDENT episodes, scaled to the show's total only for an EPISODIC show whose unloaded
    /// tail is older than everything resident and the newest resident finished one is played (then the tail is
    /// assumed played — the WinUI app's behaviour); a serial never extrapolates.</summary>
    public static ShowLedger Of(ReadOnlySpan<float> pcts, ReadOnlySpan<int> publishedAt, int lastPlayedAt, int total, ConsumptionOrder order);
}

public static class ShowCadence
{
    public enum Kind : byte { Unknown, Daily, Weekly, Fortnightly, Monthly }
    /// <summary>Median gap of the newest ≤ 8 publish dates; Unknown under 4 episodes. Also the modal weekday.</summary>
    public static (Kind Cadence, DayOfWeek? Day) Of(ReadOnlySpan<int> publishedAtNewestFirst);
}

/// <summary>The reader's item list: what the bound list realizes. Rebuilt when its inputs publish, never per frame.</summary>
public static class ShowReaderShape
{
    public enum ItemKind : byte { Rail, Head, Header, Group, Row, Foot, Similar }
    public readonly record struct Item(ItemKind Kind, int Slot, int GroupKey);      // GroupKey = year*12+month
    public const int Prefix = 1;                                                     // the sticky rail
    public static int Build(ReadOnlySpan<int> viewSlots, ReadOnlySpan<int> publishedAt, bool canLoadMore, bool hasSimilar, Span<Item> into);
}

public static class EpisodeNeighbours
{
    /// <summary>"Next" is number+1 for a serial, the newer one otherwise; both may be -1 at an end.</summary>
    public static (int Next, int Previous) Of(ReadOnlySpan<int> slotsNewestFirst, int self, ConsumptionOrder order);
}

/// <summary>Filter+sort per show in ONE setting: "id:status:order;…", newest first, capped — never a key per uri.</summary>
public static class ShowViewPrefs
{
    public const int Cap = 64;
    public static (int Status, int Order) Read(string blob, ReadOnlySpan<char> showId);
    public static string Write(string blob, ReadOnlySpan<char> showId, int status, int order);   // moves to front, trims to Cap
}
```
`Platform.Keys.PodcastViews = new SettingKey<string>("podcast.views", "")`.

### 5.3 Decode-only fields (owner C) — `Spotify.Decode.cs`, inside the existing `ShowV4` / `EpisodeV4` field loops

```csharp
// ShowV4  (metadata.proto Show)                      // EpisodeV4 (metadata.proto Episode)
case 68: if (r.Bool()) flags |= ShowFlags.Explicit; break;      case 65: row.Number = (ushort)Math.Clamp(r.Int32(), 0, ushort.MaxValue); break;
case 74: media = r.Int32(); break;   // 0 MIXED 1 AUDIO 2 VIDEO  case 70: if (r.Bool()) ef |= EpisodeFlags.Explicit; break;
case 75: row.Order = (byte)(r.Int32() + 1); break;              case 72: ef |= EpisodeFlags.Video; r.Skip(); break;   // repeated video file ⇒ has video
case 83: row.Trailer = s.AddText(r.Bytes()); break;             case 87: row.Kind = (byte)r.Int32(); break;           // FULL/TRAILER/BONUS
case 85: if (r.Bool()) flags |= ShowFlags.MusicAndTalk; break;  case 97: if (r.Bool()) ef |= EpisodeFlags.Short; break;
// after the loop: known |= ShowFields.Facts (always — an absent field is "false", a real answer); same for the episode's Identity.
```
`C` confirms each reader method name (`r.Bool/Int32/Bytes/Skip`) against `ProtoReader` (`Spotify.Decode.cs:1015-1050` in the data report) and the enum numbering against `Protos/metadata.proto` before writing; the proto enum for `consumption_order` starts at 1 in some builds — the test fixture decides.

### 5.4 `Detail.cs` / `Detail.UI.cs` — the seams (owner M)

```csharp
public enum DetailKind : byte { Album, Playlist, Liked, Show, Episode }

// Detail.Config
public static Config Episode => Show with { Kind = DetailKind.Episode, Heart = HeartMode.None, RailScope = RailScope.Show };

// FrameSlots — six new members, appended so the existing Mask bits keep their values
public Func<Element>? Badges { get; init; }                 // 4096   under the eyebrow
public Func<float, Element>? Rating { get; init; }          // 8192   under the attribution/publisher
public Func<float, Element>? Ledger { get; init; }          // 16384  under the meta line
public Func<Func<ColorF>, Element>? Primary { get; init; }  // 32768  REPLACES PlayPill when present
public Func<Element[]>? Satellites { get; init; }           // 65536  REPLACES the [Save][Share][More] group when present
public Func<float, Element>? Topics { get; init; }          // 131072 under the CTA
```
`RailColumn` insertion order becomes: cover → eyebrow+**badges** → title → attribution → **rating** → meta → **ledger** → cta(**primary**, **satellites**) → **topics** → desc. `Identity` gains nothing: a slot closes over the page's own snapshot, and because slot equality is *presence*, the page re-pushes a **new `FrameSlots` only when a slot appears or disappears**; the slot bodies read signals (`Prop`/`UseComputed`) so values change without a remount. `RailSkeletonColumn` + `Skeleton.RailPlanFor` gain `Badges/Rating/Ledger/Topics` rows and count `Satellites` so the skeleton does not reflow on reveal. `Detail.Text.Eyebrow`, `RailPolicy.ScopeFor/KeysFor` get the `Episode` arm (it shares the show's rail keys). `DetailConfigTests`, `DetailSkeletonGeometryTests` extended.

### 5.5 `Platform/Controls.Words.cs` — the generic word rail (owner O)

```csharp
public static partial class Controls
{
    public static class Words
    {
        public const float Size = 13.5f, Line = 18f, Gap = 14f, Height = 32f;          // = the library rail, moved
        public const float BigSize = 20f, BigLine = 26f, BigGap = 22f, BigHeight = 40f; // Display face, 300 → 400

        public readonly record struct Word(Prop<string> Label, Prop<string>? Count = null, bool Visible = true);

        /// <summary>A rail bound to one int signal. The active word is TWO STACKED RUNS cross-faded over 83 ms
        /// (TextEl.Weight is a plain ushort — engine Dsl/Element.cs:669 — so a bound weight is impossible).</summary>
        public static Element Rail(IReadOnlyList<Word> words, Signal<int> selected, Func<ColorF>? tone = null,
                                   bool big = false, AutomationRole role = AutomationRole.Toolbar, Action<int>? onReselect = null);

        /// <summary>Lowercase wrapping word links (the topic row): TextSecondary → tone + underline on hover.</summary>
        public static Element Links(IReadOnlyList<(string Text, Action Go)> words, float width, Func<ColorF> tone);
    }
}
```
`User.WordRail/ScopeRail/ReaderSortRail` keep their signatures and call `Words.Rail`; `RailBar`/`RailWord` are deleted from `User.UI.cs`. The library's pixel output must not move — `O` diffs W8 of the library plan by eye in `--fake`.

`Controls.Podcast.cs`: `Chip(string text, string? glyph = null, bool tone = false)` (11/600, h16, r3); `LedgerBar(Prop<float> played, Prop<float> progress, Func<ColorF> tone)` (4 DIP, 2 gap, three flex parts, min 2); `StarRow(int value, Action<int>? rate, float size)`; `Door(DoorData d)` (r8 card, `lead` = tone gradient, 64-px ghost numeral, label 11/700 caps tone, title 15/600 ≤2, why 12 ≤2, foot disc 32 + meta); `FindBox(Signal<string> query, string placeholder)` over `TextBox.Create`.

### 5.6 `Entities/Show.Page.cs` — the reader host (owner P)

```csharp
sealed class ReaderHost : Component, IPropsHost
{
    internal readonly Signal<int> Status, Order;                 // ctor-seeded from ShowViewPrefs (library idiom, User.Page.Library.cs:330)
    internal readonly Signal<string> Find = new("");
    readonly RepeatLayout _layout = RepeatLayout.VariableList(96f);
    Memo<ReaderSnap>? _snap;                                      // ONE coherent read: slots, pcts, view, visit, listen-next, ledger, items

    ListOptions<ShowReaderShape.Item> OptionsFor(string routeKey) => new()
    {
        SelectionMode = ItemsSelectionMode.None, Grow = 1f, ContentType = _contentType, KeyOf = _keyOf,
        PersistentPrefixCount = ShowReaderShape.Prefix,
        Scroll = new ScrollOptions
        {
            ScrollKey = "episodes:" + routeKey, AutoEdgeFade = false,
            ItemClipTopInset = ReaderRailH, ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
        },
    };

    // ItemAt hands slot 0 back as a RAW element — a sticky root returned from a component never pins (Artist.Reader.cs:370).
    Element RailElement() => new BoxEl { /* Words.Rail(filter) · spacer · FindBox · divider · Words.Rail(sort) */ }.Sticky(0f);
}
```
`Compute` extends today's (`Show.Page.cs:295-350`): after `Episode.Rules.View(...)` it applies the find filter (title/description `Contains`, ordinal-ignore-case, over resolved `StringId`s — cached per `Version`), then `ListenNext.Pick`, `ShowVisit.Of`, `ShowLedger.Of`, `ShowCadence.Of`, `ShowReaderShape.Build`. The rail stays in vertical mode (defect 5). `DemandShow` adds `Entities.EnsureEdge(FetchEdge.ShowSimilar, slot)` and `ShowTopics`; `DemandRows` is unchanged (it already asks `Progress`).

### 5.7 `Entities/Episode.UI.cs` — the row (owner E)

`ReaderRow(in BoundItemScope<RowItem> item, RowActions a)` — a `BoxEl` grid `[num 46 | art 56 | copy | cluster]`, `Focusable`, `Role = Link`, `HoverFill = Tok.FillSubtleSecondary`, top hairline `Tok.StrokeDividerDefault`. All values through `item.Text/Value/Show` selectors (static lambdas, zero-alloc). New statics: `IsNowPlaying(Episode e) => e.Slot > 0 && Playback.CurrentId.Value == e.Id`; `Invoke(Episode e, Action startDifferent)` (the `Track.Invoke` twin, `Track.UI.cs:90-106`); `LeftLabel(progressMs, durationMs)`; `TitleSansNumber(title, number)` (strip a leading `#123 - ` when `Number` is shown — 0d0429a0 `ShowEpisodeRow.xaml.cs:263-272`, pure, tested). `Episode.Menu(Episode e, MenuOptions o)` builds the context menu over `ActionTarget.ForEpisode`.

### 5.8 Resume points (owner T)

```csharp
// Spotify.Telemetry.cs, beside ResumeMs
const string CurrentStatesRoute = "/herodotus/spotify.resumption.v1.CurrentStateService/ListCurrentStates";

/// <summary>Every resume point the account has touched since <paramref name="sinceUnixMs"/>, in ONE call (0d0429a0
/// SpClient.cs:1408: limit 1021, a CEL filter on update_time). API THREAD. Stages Progress + PlayedAt at
/// Authority.Full — BELOW Authority.Local, so a stale server position never rewinds what this device just played.</summary>
public static int CurrentStates(long sinceUnixMs, Staging s, CancellationToken ct)
{
    var request = new Rs.ListCurrentStatesRequest
    {
        Limit = 1021,
        Filter = "cs.resume_point_revisions.exists(revision, revision.update_time > timestamp('"
               + DateTimeOffset.FromUnixTimeMilliseconds(sinceUnixMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "'))",
    };
    // PostEncoded(CurrentStatesRoute, request, the SAME HeaderSet ResumeMs uses, "application/x-protobuf") → Rs.ListCurrentStatesResponse
    // foreach state: uri must start "spotify:episode:"; row = s.Episodes.RowFor(uri, Authority.Full, (uint)EpisodeFields.Progress);
    //   row.ProgressMs = Episode.Rules.ProgressOf(value.ResumePoint is not null, PositionMsOf(value.ResumePoint), knownDurationOr0);
    //   row.PlayedAt = (int)(revision.UpdateTime ?? revision.CreateTime).Seconds;
}
```
A completed episode whose duration is not resident yet stores `ProgressMs = int.MaxValue`; `Rules.Pct` already clamps to 1. Driven from the login sync (`Spotify.Library.cs:187-203`, lookback 180 days first run, then since the last successful stamp — a `SettingKey<long>`), posted back with `Spotify.Post` → `Entities.Commit` + `Publish` (the `Spotify.Api.Concert.cs:16-21` host-door pattern). **Local mirror**: at `Playback.Host.Context.cs:1115` and `:1122`, beside each `Telemetry.ResumePoint(...)`, stage `{Id, ProgressMs, PlayedAt = unix, Known = Progress, Authority.Local}`; plus a 15 s mirror while an episode plays (rows repaint "min left" without a pause). **Mark played/unplayed** = `Entities.MarkEpisode(Episode e, bool played)`: the same local stage + `Telemetry.ResumePoint(uri, played ? Completed : 0)`. `LookUpResumePoint` (`Playback.Host.Context.cs:1166`, caller `Playback.Host.cs:602`) is **deleted**: the load reads `episode.ProgressMs` (already hydrated) and passes it as `fromMs` — through `ResumeStart.For` from the 09-18 playback fix, so a finished episode restarts at 0 instead of at its end.

### 5.9 Pathfinder (owner D1) — `Spotify.Api.Podcast.cs`, `Spotify.Decode.Podcast.cs`

```csharp
public static class PodcastQueries     // hashes from 0d0429a0 PathfinderOperations.cs (June 2026) — §6.0 verifies before P5 builds on them
{
    public static readonly Query ShowMetadata = new("queryShowMetadataV2", "aaad798a17a43c0f443c45d630a83df39d2ca1062a090c2e4fb045d6b00ab360", true);
    public static readonly Query SimilarShows = new("internalLinkRecommenderShow", "6c369ff272a666b31fef1629c169925a1bd80f372195396c82304142cacd89e8", true);
    public static readonly Query Episode = new("getEpisodeOrChapter", "3416929067571ac4b79db16716be3c6ea5f6265f7975a0ee94b1fc5ee1dc1e9d", true);
    public static readonly Query SimilarEpisodes = new("internalLinkRecommenderEpisode", "122f5c777aae5c0918baec11cd646b7034b8f213f260097b4d229ad947ec7f93", true);
    public static readonly Query Chapters = new("queryNpvEpisodeChapters", "367f0e93a0d219ae6f5874bcc460201db0a43467ae94f16298931a704ac62ea6", true);
    public static readonly Query Comments = new("getCommentsForEntity", "bba34fe5f2da3aaa25ab5c90eef1fe2036d325bf32e791ae462b637665185d83", true);
    public static readonly Query Replies = new("getReplies", "a2018b23184ee9c8f355f5bcb0584aa3afbacaed6912195a367aa1bb807359f6", true);
    public static readonly Query Reactions = new("getReactions", "0d209bf9507779887fe2b3032d1afd8f35de8425b01aead094698ff1abecda71", true);
}
```
Variables (exact keys): show `{uri, includeContentCapabilityTrait:true, includeEpisodeContentRatingsV2:false}` · similar shows `{uri}` · episode / similar episodes `{uri, includeEpisodeContentRatingsV2:false}` · chapters `{uri, offset:0, limit:50}` · comments `{uri, token}` · replies `{commentUri, pageToken}` · reactions `{uri, token, reactionUnicode}`.

Folds (paths exact, from the WinUI response classes):
- `data.podcastUnionV2` → show row at `Authority.Full`, groups `Identity|About|Facts|Rating`: `name`, `publisher.name`, `htmlDescription` (→ `Description`; the rail already renders HTML), `coverArt.sources[]` (largest), `rating.averageRating{average,showAverage,totalRatings}` (`RatingX100 = showAverage ? round(average*100) : 0`), `rating.canRate`, `rating.rating.rating`, `showTypes[]` ∋ `*EXCLUSIVE*`, `contentRatingV2.labels[]` ∋ `EXPLICIT`, `mediaType`, `consumptionOrderV2`, `trailerV2.data.uri`, `visualIdentity.squareCoverImage.extractedColorSet.highContrast.backgroundTintedBase{red,green,blue}` → `Tone` (**never `textBrightAccent` — Spotify returns brand green for most shows**; 0d0429a0 `ShowViewModel.cs:260-268`), `topics.items[]{title,uri}` → `Relation.ShowTopics` run (`EndEvenIfEmpty`). `saved` is **ignored** (the library edge is the authority).
- `data.seoRecommendedPodcast.items[].data{uri,name,publisher.name,coverArt}` → show identity rows + `Relation.ShowSimilar`.
- `data.episodeUnionV2` → episode row: `htmlDescription`, `restrictions.paywallContent`, `previewPlayback.audioPreview.cdnUrl` (→ `PreviewOnly` when `!playability.playable`), `transcripts.items[]` non-empty → `HasTranscript`, `podcastV2.data{uri,name,…}` → the show identity. Its `playedState` is **ignored** (herodotus is the authority).
- `data.seoRecommendedEpisode.items[].data` → episode identity rows + `Relation.EpisodeRecommended`.
- chapters: `data.episodeUnionV2.displaySegments.displaySegments.items[]{title,subtitle,seekStart.milliseconds,seekStop.milliseconds}` (doubly nested; blank titles dropped) → `ChapterTable` rows + `Relation.EpisodeChapters`.

Routes (`Fetch.Routes.cs`): `s_show = [Pathfinder(ShowMetadata, Identity|About|Facts|Rating, primary: Rating), Metadata(ShowV4, Identity|About|Facts)]` — most-capable first; a dead hash seals only `Rating` and `ShowV4` still serves the rest. `s_episode` likewise with `Episode.Detail` as its own group (`EpisodeFields.Detail = 1 << 9`: paywall/preview/transcript/html). Every new `PathfinderOp` is added to `Serves` **and** `AnswerQuery`.

### 5.10 Transcript and comments stores (owners V, W) — outside `Entities/`, on the `Lyrics.Store` model

```csharp
// Shell/Transcript.cs — CORE, pure
public sealed record TranscriptDoc(string Language, bool SyllableSynced, TranscriptSection[] Sections);
public sealed record TranscriptSection(int StartMs, string? Title, TranscriptSentence[] Sentences);
public readonly record struct TranscriptSentence(int StartMs, string Text);
public static class TranscriptParser { public static TranscriptDoc? Parse(ReadOnlySpan<byte> json); }     // Utf8JsonReader; "section"[]{startMs,title{title},text{sentence{startMs,text,highlight[]}}}
public static class TranscriptRules
{
    public static int SentenceAt(TranscriptDoc d, int positionMs);              // binary search → the "saying" sentence
    public static int Find(TranscriptDoc d, string query, Span<int> hits);      // in-page search, ordinal-ignore-case
}
```
`Transcript.Host`: keyed by the base62 episode id; `Ensure(id)` idempotent with one shared in-flight; `Doc(id)`; `Answered(id)` (shimmer vs "no transcript" — a **404 is an answer**); `Signal<uint> Changed`; LRU 8; `DiskCache` with `SchemaVersion` and a 24 h negative TTL. Transport: `Spotify.Api.GetText(SpclientBaseUrl + "/transcript-read-along/v2/episode/" + id + "?format=json&maxSentenceLength=500&excludeCC=true", ct)`.

`Comments`: records `CommentThread(TotalCount, NextToken, Comment[])`, `Comment(Uri, Text, Author, AvatarUrl, CreatedUnix, Pinned, ReplyCount, ReactionCount, TopReactions[≤3], MyReaction)`; rules `Order` (pinned first, then wire order), `Visible` (drops `isSensitive`), `AgeLabel`. Store keyed by episode uri; `Ensure(uri)`, `More(uri)`, `Replies(commentUri)`, `Reactions(commentUri, emoji?)`; **no disk cache** (stale at once). The three responses are **arrays of pages** (`data.comments[0]`, `data.commentReplies[0]`, `data.commentReactions[0]`). The composer, React and Reply render disabled with `Strings.Podcast.Comments.PostingUnavailable` until wave P10.

### 5.11 Routing and actions (owner S)

`RouteKind.Episode` appended **before `NotFound`'s neighbours exactly as `Concert` was** (enum + `s_routes` same ordinal; `ShellRoutesTests` pins it): `new(RouteKind.Episode, "episode:", true, Strings.Nav.Episode, Icons.RadioTower, false, true, false)`; `Shell.For`: `EntityKind.Episode => RouteKind.Episode`; the lazy group joins `Album.InstallPages()`; `TryParseSpotifyUri` (`Shell.cs:616`): `EntityKind.Episode` moves from the *play* arm to `route = "episode"` — a shared episode link opens its page, whose primary is one click from playing (decision D-2, §12). `ActionTarget.ForShow(uri, title)`, `ForEpisode(uri, title, showUri)`; `ActionId.MarkPlayed/MarkUnplayed/GoToShow` rows.

### 5.12 Loc keys (owner O) — `assets/loc/en-US.json` under `"podcast"`; `ko-KR`/`nl` get the same node

`startHere`, `beginHere`, `whereItBegan`, `latest`, `trailer`, `serialHint`, `episodicHint`, `about`, `continue`, `upNextFromProgress`, `startFromBeginning`, `newSince`, `markAllPlayed`, `caughtUp`, `caughtUpNext{day}`, `episodesCount{count}`, `left{time}`, `leftOf{left}{total}`, `played`, `new`, `ledger{played}{progress}{togo}`, `ledgerNew{count}`, `cadence.{daily,weekly,fortnightly,monthly}`, `since{when}`, `ratings{count}`, `yours{stars}`, `rate.title{name}`, `rate.unavailable`, `notify.unavailable`, `find`, `nothingMatches`, `moreLikeThis`, `alsoFollow{name}`, `tabs.{about,chapters,transcript,comments}`, `nextInStory`, `aroundThisEpisode`, `next`, `previous`, `newer`, `older`, `moreFromShow`, `seeAll{count}`, `alsoLike`, `chapter{n}`, `transcript.{search,follows,none}`, `comments.{count,add,postingUnavailable,pinned,replies{count},loadMore,none}`, `badge.{exclusive,video,bonus,trailer,subscribers,previewAvailable,episode{n}}`, `menu.{markPlayed,markUnplayed,goToShow}`, `progressUnavailable`; `nav.episode`. The literal `Loc.Get("podcast.empty")` at `Show.UI.cs:126` becomes `Strings.Podcast.Empty`.

---

## 6. Data and readiness rules (the contract every owner codes against)

### 6.0 The wire check — before wave P5 (owner D1 builds it in P1; **the user runs it**)

Diagnostics → **Podcast wire check**: two uri boxes (show, episode) and a Run button. It fires each of the 8 ops + `ListCurrentStates` + the transcript GET once, and lists `op · status · bytes · root key found?`; **Save captures** writes each body to `%LOCALAPPDATA%\Wavee\logs\podcast-wire\<op>.json`. Those files, scrubbed of account data, become `Wavee.Tests/Fixtures/podcast/*.json` — the decoder tests are written against captures, never against guessed shapes (the `ConcertDecodeTests` precedent). A 400 means a rotated hash: that op's wave waits for a fresh hash from a web-player capture; nothing else blocks.

### 6.1 Readiness

| Element | Shown when | Until then |
|---|---|---|
| rail identity | `ShowFields.Identity` | frame skeleton |
| badges E/video, numerals, doors' trailer | `ShowFields.Facts` / episode `Identity` (decode tier) | absent — never reserved |
| rating, exclusive, tone, topics | `ShowFields.Rating`, `Edges.ShowTopics` answered | absent; tone falls back to the cover-extracted palette (today's behaviour) |
| visit head | `Edges.ShowEpisodes` answered **and** the login hydrate has settled (one `Signal<bool> Telemetry.ProgressSettled`) | the head's skeleton (one hero-height bar) — **never flash New at a Returning listener** |
| "progress unavailable" | the hydrate failed | the ledger line reads `Strings.Podcast.ProgressUnavailable`; rows show no state rather than a false "unplayed" |
| similar / recommended shelves | edge answered and non-empty | absent (no empty header) |
| tabs | chapters: edge non-empty · transcript: `HasTranscript` · comments: thread `eligibilityStatus` allows | the word is absent from the rail |

### 6.2 Authority

`Seed < Thin < Full(wire) < Local(player)` for `Progress`/`PlayedAt`. `queryShowMetadataV2` and `ShowV4` both write `Identity|About|Facts` at `Full`; the later answer wins, and they agree. `saved` and `playedState` from pathfinder are never written.

---

## 7. Motion

Rail words: 83 ms ink cross-fade (`MotionTok.ControlFaster`), underline slides 167 ms. Row hover cluster: opacity 0→1, 120 ms. Doors and the hero: `translateY(-1)` + fill, 150 ms; discs `Design.Motion.ScaleEmphatic` (1.07 / 0.92). Visit head swap: `SkelReveal.FadeOnly`. Ledger segments animate `flex` over 250 ms on a mark-played. Chapter live fill follows `Playback.PositionMs` sampled on `FrameTime.NowQpc` (memory `animations-sample-frame-time`) — never `Environment.TickCount64`. All off under reduced motion.

---

## 8. Waves, owners, gates

Opus/Sonnet write, Fable verifies (memory `0.3 model split`). One Debug + one Release build + one test run per wave.

**Wave P1 — CORE, decode, seams, seed (A, B, R, C, O, F, D1-check in parallel; disjoint files).**
- A, B: §5.1. R: §5.2 + all §10 rule tests. C: §5.3 + `DecodeTests`. O: §5.5 (`Controls.Words`, `Controls.Podcast`, library rails re-pointed). F: §9. D1: the wire-check action (§6.0) + `PodcastQueries`.
- *Gate P1:* builds + tests green; library word rails pixel-identical in `--fake`; **the user runs the wire check** and drops captures.

**Wave P2 — frame + route + resume points (M, S, T).** §5.4, §5.11, §5.8.
- *Gate P2:* `--fake` show page unchanged visually (new slots unused); `episode:` route resolves to a placeholder `Vacancy`; live: pause an episode → the log shows the local mirror, a relaunch shows progress on a second machine's episodes (hydrate).

**Wave P3 — the show reader (P, E).** §5.6, §5.7, the rail slots wired, `ShowViewPrefs`.
- *Gate P3:* prototype parity at 1440 / 1120 / 760 / 480 for New / Returning / CaughtUp in `--fake` (the seed has all three, §9); sticky rail pins; now-playing row; filter+sort survive a relaunch (live) .

**Wave P4 — pathfinder show data (D1, D2).** §5.9 show half: rating, tone, exclusive, topics, similar shows. Blocked per-op on §6.0.
**Wave P5 — the episode page (Q, D1, D2).** `Episode.Page/Reader`, about tab with timestamp links, doors, more-from-show, recommended.
**Wave P6 — chapters + transcript (D2, V).** **Wave P7 — comments, read-only (W).**
**Wave P8 — Your Episodes (one owner, the eleven-file list in the data report §7.2):** `LibraryEdgeKind.SavedEpisodes` over the parked `ListenLaterSet = "listenlater"`; the ♥ on rows and the episode rail goes live; the library podcasts page gains *saved · latest · in progress* (prototype scene 3).
**Wave P9 — chapter amendments + issues (orchestrator).** §11.
**Wave P10 — writes, each gated on a capture the user supplies:** rate (`canRate` already read), post/reply/react (optimistic-settle per `Edges.cs:504-523`), new-episode notifications. Until then each control is disabled with its reason string. Downloads are out of scope (own project).

---

## 9. The fake seed (owner F) — `Entities.Fake.Podcast.cs`, called from `StageShows`

Show 0 = **serial, Returning** (`Order = Sequential`, 14 episodes numbered 14→1, episodes 1–8 played, 9 at 46 %, `PlayedAt` = 9 days ago, two episodes newer than that ⇒ *new since*; trailer uri; rating 4.8 / 12 431; six topics; tone amber). Show 1 = **episodic, New** (no progress; explicit on even episodes; rating 4.6). Show 2 = **video, exclusive, CaughtUp** (all played; one `Paywalled|PreviewOnly` episode). Show 3 keeps ch 09's degraded cards. `Edges.ShowSimilar` = the other shows; `Edges.EpisodeRecommended` = 4 cross-show episodes; 6 chapters on show 0 / episode 9; a bundled `assets/fake/transcript.json` + a 3-comment thread served by fake store backings (`Transcript.Host`/`Comments.Host` take their fetch as a delegate, the `Lyrics.Boot` idiom). `Telemetry.ProgressSettled` is true at once under `--fake`. `EntitiesFakeTests` extended.

---

## 10. Tests (pure; none reads production source)

`ShowVisitTests` (progress beats follow; caught-up needs progress) · `ListenNextTests` (serial walks forward from the newest finished; new serial seeds from episode 1; episodic takes newest unplayed; the playing episode wins resume; ≥ 0.90 is not a resume) · `ShowLedgerTests` (serial never extrapolates; episodic tail rule; fresh = published after last play) · `ShowCadenceTests` (weekly/fortnightly, < 4 ⇒ Unknown, modal weekday) · `ShowReaderShapeTests` (prefix is 1; month boundaries; foot/similar presence; oldest order reverses groups) · `EpisodeNeighboursTests` · `ShowViewPrefsTests` (round-trip, LRU move-to-front, cap 64, malformed blob ⇒ defaults) · `EpisodeRulesTests` += `Completed`, `ProgressOf`, `TitleSansNumber`, `LeftLabel` · `DecodeTests` += the nine proto fields · `PodcastDecodeTests` (captures: show, similar, episode, recommended, chapters) · `SpotifyTelemetryTests` += `CurrentStates` filter string + fold (completed = no resume point; `Full` never beats `Local`) · `TranscriptParserTests`, `TranscriptRulesTests` · `CommentsRulesTests` (pages are arrays; sensitive dropped; pinned first) · `DetailConfigTests`/`DetailSkeletonGeometryTests` += the six slots · `ShellRoutesTests` += Episode ordinal + the deep link opening · `FetchRoutesTests` += route order and "a dead pathfinder op seals only `Rating`" · `WordsRailTests` (selection/ reselect rule) · `ScopeRetirementTests` stays green (new `StringId`s released).

---

## 11. Chapter amendments (orchestrator, wave P9)

`wavee-0.3-ui/09-show-episode-module.md`: §0 (row = link; rail slots), W1–W9 replaced by §2 here, §6.1, §7 + DATA GAPS (closed), §8 (the new rules), §9.5 budgets (`Show.cs` 300 · `Show.Rules.cs` 420 · `Show.UI.cs` 380 · `Show.Page.cs` 900 · `Episode.cs` 330 · `Episode.UI.cs` 520 · `Episode.Page.cs` 420 · `Episode.Reader.cs` 700), §10 parity against the prototype, §11 audit entry. `00-index.md` §2.1: the `episode:<uri>` row; the shared-frame row notes the six slots. `03-detail-frame.md`: the slot table. `00-design-system.md`: `Controls.Words` (rail + Big), `Chip`, `LedgerBar`, `Door`.

---

## 12. Decisions taken (say so if any is wrong)

- **D-1** Progress beats follow state in `ShowVisit`. **D-2** An episode deep link opens the page instead of playing. **D-3** The whole row navigates; the disc plays (reverses ch 09 items 20–21). **D-4** Pathfinder `saved`/`playedState` are ignored — the library edge and herodotus are the authorities. **D-5** One completion rule: ≥ 0.98 or ≤ 30 s left. **D-6** Filter/sort persist in one capped blob, not a key per show. **D-7** Tone comes from `backgroundTintedBase`, never `textBrightAccent`. **D-8** `LookUpResumePoint` is deleted once the hydrate lands. **D-9** Comments are never disk-cached; transcripts are. **D-10** Writes without a captured endpoint ship disabled with a reason, never hidden.

## 13. Issues (filed with `gh` only after the user approves each call)

1. Podcast progress never reaches the episode table (hydrate + local mirror + mark played) — P2. 2. Show page: visit-aware reader, sticky word rail, episode reader rows — P1/P3. 3. Detail frame: rail slots for badges/rating/ledger/primary/satellites/topics — P2. 4. Episode page + `episode:` route + deep link opens — P2/P5. 5. Podcast decode: number/type/explicit/video/trailer/order — P1. 6. Pathfinder show metadata, topics, similar shows — P4. 7. Chapters + transcript reader — P6. 8. Comments (read) — P7. 9. Your Episodes library edge — P8. 10. Writes pending capture: rate · comment · notify — P10.

## 14. As built

P1 and P2 (2026-09-19): see [as-built-20260919.md](as-built-20260919.md) — sections P1-R, P1-A/B/C/F, P1-D1, P1-O, P2-S, P2-M, P2-T.
