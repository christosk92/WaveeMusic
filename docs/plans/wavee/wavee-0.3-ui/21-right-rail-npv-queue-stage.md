# Right rail: now-playing panel, queue, friends, stage, player styles — 0.3 visual fidelity contract

> 0.2.9 sources (5,912 lines): `Features/Player/RightRail.cs` 458 · `NowPlayingPanel.cs` 566 · `NpvHeaderRow.cs` 112 ·
> `NpvLyricsPeek.cs` 177 · `NpvThumbnails.cs` 256 · `NpvPlayerCatalog.cs` 90 · `NpvPlayerPrefs.cs` 48 ·
> `NpvDiagnostics.cs` 16 · `PlayerStyleFlyout.cs` 232 · `QueuePanel.cs` 936 · `QueueMovePlan.cs` 149 ·
> `QueueOrder.cs` 80 · `FriendsPanel.cs` 228 · `VideoRailPanel.cs` 156 · `StageChrome.cs` 363 · `StageIdentity.cs` 706 ·
> `StageLayout.cs` 353 · `StagePanes.cs` 614 · `App/FriendsBridge.cs` 64 · `App/RailVideoCoupling.cs` 83 ·
> `Design/StageArm.cs` 143 · `Design/StageInk.cs` 82.
> Also consulted (owned elsewhere): `App/ShellUi.cs` 91 · `App/DockedVideoHosting.cs` 168 ·
> `Features/Shell/ShellResponsiveLayout.cs:123-165` · `Features/Shell/WaveeShell.cs:1140-1312` ·
> `Backend/Lyrics/LyricsPeekClock.cs` 45 · `Features/Player/ImmersiveLyricsSurface.cs` 550 (the stage's HOST).
> 0.3 target: `Shell/Rail.UI.cs`, `Shell/Rail.Styles.UI.cs`, `Shell/Rail.cs` (CORE), `Entities/Queue.UI.cs`,
> `Entities/Queue.cs` (CORE), **`Shell/Stage.cs` (CORE) + `Shell/Stage.UI.cs`** — the stage pair settled by arbitration
> 2026-09-12 (A12) and now in the plan's tree with owner **K** — plus `Shell/Deck.UI.cs` (deck faces, chapter 23).
> | Wave 4 owner K + Wave 5 owner Q.
> **Cross-references** (do not re-specify): `00-design-system.md` (tokens, ramp, cover palette, materials, motion curves,
> CTAs) · `01-track-row.md` (`TrackRow.ArtCard`, `TrackRow.Heart`, `TrackRow.ArtistLinks`, `RowSwipe`, the drag chip) ·
> `20-player-bar.md` (`SeekBar`, `TimeText`, `PlayerBarContent.*`, `DevicePickerMenu`, the rail-toggle buttons) ·
> `22-lyrics.md` (`LyricsView`, `LyricsPrefs`, `ImmersiveLyricsSurface`'s backdrop/scrim host, the lyrics inspector) ·
> `23-deck-faces.md` (the twelve decks themselves; this chapter owns only the slot, the header and the picker) ·
> `24-video-surfaces.md` (`DockedVideoSurface`, `PlacementCore`, `VideoUpgradeGate`) · `19-shell-overlays.md` (`Popup`,
> `MenuFlyout`, `ContextMenuModel`, `Toast`) · `18-shell-frame.md` (the rail's reservation spacer, splitter and overlay).

---

## 0. The non-negotiables

Checkable claims. If any one of these is false in 0.3, the surface is not a port.

1. **The rail is ONE rung with the page, not a card on it.** Docked, `RightRail`'s own surface paints
   `ColorF.Transparent` and the shell's reservation-band underlay paints the single `WaveeColors.FileArea` coat
   (`RightRail.cs:96`, `WaveeShell.cs:1197`); the band's ONE hairline is a 1-DIP `Tok.StrokeCardDefault` on the
   **left + top only**, drawn topmost so panel content cannot cover it (`RightRail.cs:104-110`). There is **no shadow
   docked**. Floating (`!RailFits`) flips to: the rail paints its own `FileArea`, a uniform 1-DIP ring, and
   `Elevation.Flyout` (`RightRail.cs:150-154`). Three coats in one 340-DIP strip is the regression this shape exists to
   prevent.
2. **Open and close are ONE translate track, 300 ms, no opacity.** `AnimChannel.TranslateX` from the *presented*
   `LocalTransform.Dx` to `open ? 0 : railWidth`, `EasingSpec.CubicBezier(0, 0.35, 0.15, 1)` (`RightRail.cs:73-78`).
   The fully-laid-out subtree is retained through the shell's fixed clip; the reservation spacer SNAPS in the commit
   frame. A fading full-height panel is the "ghost rail" defect — never add an opacity track.
3. **The Details hero is PINNED and never scrolls.** The 324-square art tile (or the deck, or the docked video card)
   sits `Shrink = 0f` above the scroller (`RightRail.cs:169`, `NowPlayingPanel.cs:486-505`). A composited video cannot
   live inside `ScrollView { AutoEdgeFade = true }` — `DrawOp.DrawVideo` is a DestOut erase against the back buffer and
   renders BLACK inside an offscreen layer (`NowPlayingPanel.cs:24-29`). Do not re-nest it.
4. **The video is the same shape and the same width in every rail body.** One card (`RightRail.DockedCap`,
   `RightRail.cs:218-257`), full-bleed at the rail width, height fitted to the content's own aspect
   (`ShellResponsiveLayout.FitDockedVideoHeight`), reached from BOTH arms. The old fixed-square Details inset — a 16:9
   stream in fat letterbox bars the moment you switched to Details — is a deleted defect, not a fallback.
5. **The queue's current track is a pinned bordered CARD, never a row in the list** (`QueuePanel.cs:361-425`), and
   there is **no history section** at all: `Queue → Next up → Autoplay`, forward-looking only (`QueuePanel.cs:18-30`,
   `:124-132`).
6. **Every queue slot is a plain keyed child of one column.** No virtualization, no bound-slot recycling: rows carry
   `Enter (Dy 6, Opacity 0)`, `Exit (Dy −4, Opacity 0)` and `Layout = LayoutTransition.Slide`, so a consumed row exits,
   the rest slide up and the card cross-fades — motion a recycled list physically cannot express (`QueuePanel.cs:693-716`).
7. **ONE `Reorderable` spans the whole upcoming list, headers included.** `QueueSlots` flattens headers, realized rows
   and "Show more" rows into slots with authored extents (44 / 36 / 54 / 46 DIP), and `QueueMovePlan.For` decides
   section-locality. A lone queued track has exactly one legal slot and every other drop is a **told refusal**
   (`Strings.Drag.CantMoveAcrossSections`), never a silent landing on a playlist (`QueueMovePlan.cs:93-132`).
8. **The stage is ONE tree with ONE reflow flag.** `StageLayout.Wide` (threshold 600 DIP, promotion hysteresis 40)
   decides the row direction, the art size, every transport box and the folded-control set. A demotion/fold is
   immediate; a promotion/unfold needs reserve. Dragging the window edge flips each rung exactly once per crossing
   (`StageLayout.cs:317-346`).
9. **The stage's scrim is TWO full-bleed paint layers, and neither has a locatable edge.** One continuous vertical
   gradient (0.76 → 0.46 plateau → 0.70) plus one left-anchored column shade (0.26 held to 352, feathered to exactly
   0 across 260 DIP). No region brings its own boxed veil — not the caption band, not the identity column, not the
   pivot band (`StageChrome.cs:85-108`).
10. **The stage flips with the theme, from one source.** Every colour is mixed from `StageInk.Veil` and inked from
    `StageInk.Ink`; the dark arm is `WaveeOnMedia` **verbatim** (test-pinned) and the light arm mirrors its alphas over
    a light ground. No renderer names a theme (`StageArm.cs`, `StageInk.cs`, `StageLayoutTests`).
11. **The stage's exit outranks the chrome beside it.** A 44-DIP `ExitFab` whose plate is made of INK (`GlassPlate`,
    Ink @ 0.14) with `Elevation.Card`, next to a 40-DIP `ScrimFab` whose plate is scrim. They are deliberately NOT a
    matched pair (`StageChrome.cs:186-226`).
12. **Both stage panes stay mounted; the switch is a 250 ms opacity cross-fade** with `HitTestVisible` following the
    active one (`StagePanes.cs:57-74`). Conditional mounting would tear down `LyricsView`'s measured document and the
    queue's reorder lane on every flip.
13. **The NPV lyrics peek is a two-row reel with a 3-DIP accent spine**, the sung line at full opacity over a
    0.38-opacity next line, each slot 56 DIP, full-row slide with **no blur**, ticked at 100 ms
    (`NpvLyricsPeek.cs:18-23`, `:147-176`). Each slot's text is `MaxLines 2, Wrap` inside its 56 (`:159-166`). Three
    slot states, not one: **pre-roll** (before the first line — active `−1` ⇒ an EMPTY 56-DIP box, peek = line 0 at
    0.38), **running** (n / n+1), **last line** (active held, peek `−1` ⇒ an empty box). `LyricsPeekClock.ActiveAndPeek`
    owns all three and `LeadMs = 140` (`LyricsPeekClock.cs:12`, `:35-45`).
14. **The player-style flyout STAYS OPEN across picks** — every pick writes a preference, bumps `NpvPlayerPrefs.Epoch`
    and re-renders the body while the deck changes behind it. Only Escape / light-dismiss closes it
    (`PlayerStyleFlyout.cs:17-19`, `NpvHeaderRow.cs:103-107`).
15. **The friends row flips live.** A friend whose activity is ≤ 120 s old shows a 12-DIP accent presence dot cut into
    the avatar and a live equalizer instead of a relative time; a 30 s frame-clock tick advances the window without a
    push (`FriendsPanel.cs:23-25`, `:42`, `:85`, `:135-138`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
WaveeShell rail overlay                                   Features/Shell/WaveeShell.cs:1258-1312
│  Width = RailWidth (constant on the projected path)      :1273-1275
│  inner host: IsolateLayout, Opacity ← _rightRailFade     :1292-1307   ← the LAYOUT FIREWALL
└── RightRail : Component                                  RightRail.cs:18        role: rail frame + header + body swap
    │   reads ShellUi (RailOpen/RailWidth/Mode/RailFits/ImmersiveLyrics/ActiveStagePlayable),
    │         Services (settings), PlaybackBridge (video placement)      :24-34
    │   railBody = RailVideoCoupling.BodyModeFor(mode, stageHosts)       :51    ← a RENDER-time substitution
    ├── surface   BoxEl  Fill bound Transparent|FileArea, Corners(8,0,0,0), Clip   :89-97
    ├── edge      BoxEl  Margin(0,0,−1,−1), Border 1 StrokeCardDefault, topmost    :104-110
    │
    ├── [Details arm]  :146-176   ZStack(surface, column, edge)
    │   └── column  Direction 1
    │       ├── PinnedHero(...)                                          :298-310  role: the ONE pinned slot
    │       │    ├── DockedCap(ui, true, …)   when video docked here     :305      → Ch.24 DockedVideoSurface
    │       │    ├── NowPlayingHeroTile + ArtVideoToggle  (dockable)     :309
    │       │    └── NowPlayingHeroTile                    (otherwise)   :301
    │       └── NowPlayingPanel (scrolls)                                :140
    │
    └── [every other arm]  :178-203  ZStack(surface, column, edge)
        └── column  Direction 1
            ├── DockedCap(ui, dockedVideo, …)                            :199  mounts unconditionally; its OWN gate hides it
            │    ├── DockedVideoSurface                                  :230  → Ch.24
            │    └── Splitter (vertical, 16-DIP strip, HitTestPassThrough):246-252
            ├── header  BoxEl  Height 44, Gap 4, Padding(12,0,8,0)       :121-128
            │    ├── TitleText(railBody)        RailHeader 20/28/600     :405-409
            │    ├── [Lyrics]  secondary-line toggle (cond.)             :395-397  → Ch.22 LyricsPrefs
            │    ├── [Lyrics]  LyricsInspectorButton (developer mode)    :382      → Ch.22
            │    ├── [Lyrics]  expand → ImmersiveLyrics = true           :383-384
            │    ├── [Video]   pop-out (BackToWindow) + fullscreen       :268-277  → Ch.24
            │    └── CloseButton  32-square, ChromeClose 16              :449-457
            └── body  (Grow 1, Clip)
                 ├── RailMode.Lyrics  → LyricsView(visible: …)           :135-136  → Ch.22
                 ├── RailMode.Queue   → QueuePanel                       :137
                 ├── RailMode.Friends → FriendsPanel                     :138
                 ├── RailMode.Video   → VideoRailPanel                   :139
                 └── default(Details) → NowPlayingPanel                  :140

NowPlayingHeroTile : Component                             NowPlayingPanel.cs:506   role: pinned hero, Cover|Player
│   Padding(8,8,8,0), Gap 8, Shrink 0                                      :556-563
│   side = round((RailWidth − 16)/4)·4     (340 ⇒ 324)                     :541-542
├── NpvHeaderRow : Component                               NpvHeaderRow.cs:23        role: eyebrow · selector · gear
│    ├── WaveeType.Eyebrow("Now playing")  TextTertiary, Grow 1             :87-91
│    ├── SelectorBar.Create(["Cover", <short>], presentation, CompactBar)   :92-94
│    └── Popup.Create(gear, PlayerStyleFlyout, StyleFlyoutOpen)             :103-107
└── heroBox (Key-swapped, owns the context menu)                            :546-554
     ├── Key "cover"              → NowPlayingPanel.HeroArt(track)          :548 / :126-142
     └── Key "<deckSlug>@<side>"  → NpvDeck.Create(track, preset, side)     :547 → Ch.23
     └── .WithContextMenu(NpvArtMenu.Model)                                 :554 / PlayerStyleFlyout.cs:214-232

NowPlayingPanel : Component                                NowPlayingPanel.cs:35     role: the scrolled Details body
└── ScrollView { Grow 1, AutoEdgeFade }                                     :103
    └── column
        ├── HeroMeta(track, lib, go)   Padding(8,12,8,16), Gap 12           :146-198
        │    ├── title   Ui.Subtitle 20/28/600, Wrap, MaxLines 3            :185-188
        │    ├── artists SpanTextEl 14/20 TextSecondary (links)             :212-227
        │    ├── album   SpanTextEl 12/16 TextTertiary (link)               :156-159
        │    ├── SaveButton(uri, 16, 36, title)  Key "save:"+uri            :192      → Ch.01
        │    └── NpvLyricsPeek  Key "npv-lyrics:"+track.Id                  :195
        └── sections  Gap 16, Padding(12,0,12,12)                           :95-100
             ├── AboutArtist(about, go)                                     :229-303
             ├── TopCities(cities)         max 5                            :305-337
             ├── Credits(credits, sources) grouped by RoleGroup             :339-376
             ├── Merch(merch)              max 4                            :378-410
             ├── NextUpSection(next)       max 5 · TrackRow.ArtCard(Rail)   :412-437
             └── LoadingSection()          3 shimmer bars                   :467-477

NpvLyricsPeek : Component                                  NpvLyricsPeek.cs:17
├── spine  Width 3, Corners 1.5, Fill AccentDefault                          :86-91
└── reel   Height 112, Clip
     ├── Slot(active, faded:false)   Opacity 1                              :98 / :147-176
     └── Slot(peek,   faded:true)    Opacity 0.38                           :99

PlayerStyleFlyout : Component                              PlayerStyleFlyout.cs:26
│   Width 340, Gap 12, Padding 12                                            :66-70
├── "Player style"  14/20/600                                                :58
├── GroupRow(Media)    label + 4 × ThumbCard(74.5)                           :80-91
├── GroupRow(Devices)                                                        :60
├── GroupRow(Software)                                                       :61
├── "Options"                                                                :62
└── OptionRow × preset.Options   label(60) + Segmented(28) | Swatches(30/22)  :132-195

QueuePanel : Component                                     QueuePanel.cs:31
│   root Direction 1, Padding(14,4,14,0)                                     :190-206
├── Pills  Gap 8, Padding(0,4,0,10)   Shuffle · Repeat · Autoplay(∞)          :815-860
└── ScrollEl { AutoEdgeFade, ScrollKey "queuepanel" }                         :197-204
    └── body  Padding(0,0,0,14)
        ├── PlayingFrom(source, href)        MinHeight 28                     :339-357
        ├── NowPlayingCard(track)            MinHeight 64, Key "np:"+uri      :361-425
        ├── _reorder.List( Upcoming(slots) ) Key "lane:upcoming"              :171-174
        │    ├── SectionHeader × 3           Height 36 | 54                   :428-469
        │    ├── QueueRow      × n           MinHeight 44                     :564-740
        │    └── ShowMore      × ≤3          Height 40                        :545-562
        └── EmptyState.Compact("Nothing playing")                             :176

FriendsPanel : Component                                   FriendsPanel.cs:21
└── Shell  Padding(12,4,12,0)                                                 :222-227
     ├── rows    ScrollEl { AutoEdgeFade, ScrollKey "friendspanel" }          :72-80
     │    └── Row × n   Key "fr:"+UserUri, MinHeight 56                       :83-142
     │         ├── AvatarWithPresence  40 + 12-dot                            :145-172
     │         ├── name/track/context  14-20 / 12-16 / 12-16                  :95-101
     │         └── equalizer(14) | RelTime                                    :130-139
     ├── SkeletonView   5 × SkeletonRow                                       :186-213
     └── Message(...)   EmptyState.Compact                                    :218-219

VideoRailPanel : Component                                 VideoRailPanel.cs:38
└── ScrollView { Gap 8, Padding(12,8,12,12), AutoEdgeFade }                    :89-94
     ├── NoVideoPlaceholder (when !CurrentTrackHasVideo)  16:9                 :113-133
     ├── title NowPlayingTitle 20/28/600 MaxLines 2                            :67-70
     ├── meta  TrackMeta "Artist · Album"                                      :71-73
     ├── Rule  Height 1 StrokeCardDefault                                      :109
     ├── Eyebrow("Up next") TextTertiary                                       :77
     └── Row × ≤5  TrackRow.ArtCard(Rail, art 40)                              :135-155

── THE STAGE (hosted by ImmersiveLyricsSurface, Ch.22 owns the backdrop) ────────────────────────
ImmersiveLyricsSurface                                     ImmersiveLyricsSurface.cs:46
│   stage = UseSignal(StageLayout.Seed(vpW, ColumnAvailH(vpH)))                 :122-131
├── spacer  Height 48 (TitleBar.ExpandedHeight)                                :208
├── body    ZStack, Fill StageInk.Floor                                        :209-231
│    ├── Backdrop: floor → σ80 baked-blur cover @1.30 overscale → Scrim() → ColumnShade()   :420-498
│    ├── Shield  (childless hit layer)                                         :219 / :254+
│    └── content
│         ├── TopBar  Height 88 | 56, Padding(16,16,16,12)                     :355-382
│         │    ├── [Lyrics pane] ScrimFab(Globe, 40, glyph 16)                 :388-389
│         │    └── ExitFab(ChevronDown, 44, Elevation.Card)                     :401-402
│         └── StageBody  Direction = Wide ? row : column, Gap = Wide ? 56 : 0   :303-348
│              ├── wrapper Key "stage:identity" Width = Wide ? 352 : NaN        :328-338
│              │    └── StageIdentity(stage)                 StageIdentity.cs:36
│              │         ├── [WIDE] column  Grow 1, Justify Center, Pad(24,28,24,28)   :215-242
│              │         │    ├── art        L.ArtSize², Corners 8, Elevation.Dialog    :169-177
│              │         │    ├── identityRow  title/meta/heart/"…"  Entrance rung 0     :182-186
│              │         │    ├── seek block   SeekBar + times + quality  rung 1         :188-192
│              │         │    ├── transport    32·40·56·40·32, Gap 6      rung 2         :194-198
│              │         │    ├── volume       glyph 32 + Slider(264×20)  rung 3         :200-206
│              │         │    └── device line  MinHeight 24               rung 4         :207-213
│              │         └── [COMPACT] header  Pad(16,8,16,8)                            :248-296
│              │              └── art 64 · identity · prev 32 · play 40 · next 32 · "…"  :252-270
│              │              └── seek block (never folds)                                :286-293
│              └── wrapper Key "stage:panes"  Grow 1                                      :339-345
│                   └── StagePanes                          StagePanes.cs:35
│                        ├── pane:lyrics  Opacity 1|0, Transition ControlNormal           :57-64
│                        │    └── LyricsColumn: Pad(0,0,48,72), MaxWidth 700 → LyricsView(large, onMedia)  :96-123
│                        ├── pane:queue   Opacity 0|1, Gradient PaneShade()                :65-73
│                        │    └── StageQueuePane                       StagePanes.cs:160
│                        │         ├── Header "Playing next" + "from {ctx}" + rule 20×2     :302-337
│                        │         ├── AutoplayRow ∞  MinHeight 48                          :341-376
│                        │         └── _reorder.List( Upcoming(slots) )  rows 56            :254-260
│                        └── Pivot  Height 72, End/End, Gap 16, Pad(24,0,24,16)             :127-140
└── spacer  Height 72 (WaveeSize.PlayerBarH)                                    :232
```

### 1.2 The same tree in 0.3 terms

`Component` props freeze at mount. Every row below says how a data change reaches the node: **S** = a `Signal`/`Func`
read inside `Render`, **C** = `Ctx.Provide`/`UseContext`, **K** = a `Key` remount, **B** = a bound `Prop.Of` thunk
(re-paints without re-rendering).

| 0.3 node | file | shape | inputs | change path |
|---|---|---|---|---|
| `Rail.Frame()` | `Shell/Rail.UI.cs` | static `Element` | `Shell.RailOpen`, `Shell.RailWidth`, `Shell.RailMode`, `Shell.RailFits` (all `Signal`) | S for mode/width/open; **B** for the surface `Fill` and the edge `BorderColor` so a dock/float flip repaints without re-rendering the subtree |
| `Rail.Header(mode)` | `Shell/Rail.UI.cs` | static | `mode`, `Lyrics.SecondaryAvailable`, `Lyrics.PrefsEpoch`, `DeveloperMode.Enabled` | S — all four are signal reads inside the header builder, exactly as `RightRail.LyricsHeaderKids` does today |
| `Rail.DockedCap()` | `Shell/Rail.UI.cs` | static | `Shell.DockedVideoHeight` (`FloatSignal`, shared with the splitter), `Shell.RailWidth` | the height is the **same signal instance** the splitter writes — a fresh `Prop.Of` thunk per render left `LayoutInput.Height` NaN and collapsed the ZStack to the 16-DIP strip (`RightRail.cs:206-210`) |
| `Rail.NowPlayingBody` | `Shell/Rail.UI.cs` | `sealed class : Component` | none (reads `Playback.Current`, `Entities.Current.Tracks.Changed`) | S |
| `Rail.HeroTile` | `Shell/Rail.UI.cs` | `Component` | none | S for track + `Shell.RailWidth`; **K** on `"cover"` vs `"<deckSlug>@<side>"` — a preset change must remount (a Cassette may not inherit a Record's mounted deck state) while an option flip inside one preset restyles in place off `Npv.Epoch` |
| `Rail.HeaderRow` | `Shell/Rail.UI.cs` | `Component` | none | S on `Npv.Epoch`; the `SelectorBar`'s controlled index is a component-owned `UseSignal` re-synced by `UseEffect(epoch)` — **never written during Render** (`NpvHeaderRow.cs:64-66`) |
| `Rail.LyricsPeek` | `Shell/Rail.UI.cs` | `Component` | none | S on the lyrics document + a 100 ms `UseInterval`; **K** per track id from the parent (`"npv-lyrics:"+id`) |
| `Rail.StyleFlyout` | `Shell/Rail.Styles.UI.cs` | `Component` | none | S on `Npv.Epoch`; a per-render mirror `Signal<int>` per Segmented row (the option SET changes with the preset, so a hook-per-option would be a conditional hook) |
| `Npv.Thumbnails.For(id, size)` | `Shell/Rail.Styles.UI.cs` | static pure | `presetId`, `size` | none — 12 static plates, no signal, no ticker |
| `Rail.Friends` | `Shell/Rail.UI.cs` | `Component` | none | S on the feed signals + a 30 s `UseInterval` bumping `NowTick`; mount/unmount drives `SetActive` |
| `Rail.VideoBody` | `Shell/Rail.UI.cs` | `Component` | none | S on `Playback.CurrentTrackHasVideo` and the queue edge |
| `Queue.RailPanel` | `Entities/Queue.UI.cs` | `Component` | none | S on `Entities.Current.Edges.Queue.Version[session]`; `display` is a component-owned optimistic mirror written by ONE `UseSignalEffect` (`QueuePanel.cs:85-91`) |
| `Queue.StagePane` | `Entities/Queue.UI.cs` | `Component` | none | same, with `headers: false` |
| `Queue.Row(...)` | `Entities/Queue.UI.cs` | static | slot, section, flags | **K** = `RowKey(entry) + ":art=" + show + ":classic=" + classic` — identity survives reorder/remove |
| `Stage.Layout` | `Shell/Stage.cs` (**new, CORE**) | `readonly record struct` | `width`, `columnAvailH`, `previous` | pure; the host holds it in `UseSignal` and only writes on `!next.Equals(prev)` |
| `Stage.Identity` | `Shell/Stage.UI.cs` (**new**) | `Component(IReadSignal<StageLayout>)` | the coarse band signal | **S** — the layout arrives as an `IReadSignal` ctor arg, NOT a frozen value. This is the one place the props-freeze rule bites hardest: a `StageLayout` value in the constructor would pin the stage to its mount-time shape |
| `Stage.Panes` | `Shell/Stage.UI.cs` | `Component` | none | S on `StagePane.Current` (a **static** session-lived `Signal<int>`) |
| `Stage.Chrome.*` | `Shell/Stage.UI.cs` | static builders | glyph, box, glyph size, accent, latched | none — pure element factories |
| `Stage.Ink` / `Stage.Arm` | `Shell/Stage.cs` | static + `readonly record struct` | `ThemeKind` | every rung resolves its token **at the point of consumption**; a `ColorF` frozen into a component ctor would survive a live re-theme (`StageArm.cs:14-18`) |

---

## 2. Wireframes

Scale: **1 monospace char ≈ 8 DIP** for every rail wireframe (rail 340 DIP ≈ 42 chars). The stage wireframes use
**1 char ≈ 16 DIP** and say so in their titles — a 1440-DIP stage is 180 chars at 8 DIP/char and unreadable.

### W1 — Rail, docked, Details / Cover, fully loaded @ 340

```
 ← 8 gap →┌─────────────────────────────────────────┐  ← 1-DIP StrokeCardDefault, LEFT+TOP only
          │ ▒▒▒▒ this corner only: r8 ▒▒▒           │     surface Fill = Transparent (docked)
          │ 8│                                    │8│     no shadow docked
          │  │Now playing  [▣ Cover│◉ Record]  ⚙  │ │ ← NpvHeaderRow  h36  gap 4
          │  │12/16/600 +30tr       pill 30       │ │    gear 32² r4 glyph16, tip "Player style"
          │  │↑ SENTENCE CASE. WaveeType.Eyebrow  │ │    (Loc player.nowPlaying = "Now playing";
          │  │  never .ToUpper()s a loc string.   │ │     NpvHeaderRow.cs:84-91, :75)
          │ 8│                                    │ │ ← Gap 8  (ArtTop = 8+36+8 = 52)
          │  ┌────────────────────────────────────┐ │
          │  │                          ┌────────┐│ │ ← ArtVideoToggle h24 r4
          │  │                          │ ▣ │ ▶  ││ │    Fill GlassHover; sel half @0.16
          │  │                          └────────┘│ │    Margin(0, 56, 12, 0)
          │  │            324 × 324               │ │
          │  │          cover art  r8             │ │ ← HeroArt: Cover fit, aspect 1,
          │  │       Placeholder = HeroWash       │ │    DecodePx 512, BlurHash
          │  │                                    │ │
          │  └────────────────────────────────────┘ │
          ├─ ─ ─ ─ ─ ─ ─ ─ pinned ends ─ ─ ─ ─ ─ ─ ─┤ ← everything below is inside ScrollView
          │ 8│ Pad top 12                        │8│    { Grow 1, AutoEdgeFade }
          │  │Track title wraps to three         │ │ ← Subtitle 20/28/600, MaxLines 3
          │  │lines maximum, then ellipsis…  ┌──┐│ │    SaveButton box 36, glyph 16
          │  │                               │ ♡ ││ │    Gap between column and button: 12
          │  │Artist One, Artist Two         └──┘│ │ ← 14/20  TextSecondary  (links)
          │  │Album Name                          │ │ ← 12/16  TextTertiary  (link)   gap 2
          │  │                                    │ │ ← Gap 12
          │  │▌ The line that is being sung now   │ │ ← spine w3 r1.5 AccentDefault
          │  │▌                                   │ │    active slot h56, Opacity 1
          │  │▌ and the next one, faded           │ │    peek slot   h56, Opacity 0.38
          │  │ Pad bottom 16                      │ │
          │ 12│ sections: Gap 16, Pad(12,0,12,12) │12    ← note: sections take 12, hero takes 8
          │  │About the artist    20/28/600       │ │
          │  ┌────────────────────────────────────┐ │ ← card r8, Fill FillCardSecondary,
          │  │▓▓▓▓▓▓▓ artist header 132 ▓▓▓▓▓▓▓▓▓▓│ │    1-DIP StrokeCardDefault
          │  │▓▓ EdgeFade bottom 56 + scrim ▓▓▓▓▓▓│ │    image aspect 2.6, decode 320
          │  ├────────────────────────────────────┤ │
          │  │ Artist Name              ✓         │ │ ← 18/24/600 + Check 12 AccentText
          │  │ ┌────────┐┌────────┐┌────────┐     │ │ ← Fact pills r4 Pad(8,4,8,4) Gap 8
          │  │ │12,345,6││  1,234 ││   #42  │     │ │    value 12/16/600, label 12/16 T3
          │  │ │monthly ││followers││# in the│     │ │    the rank LABEL is literally
          │  │ └────────┘└────────┘└─world──┘     │ │    Strings.Artist.WorldRank("").Trim()
          │  │ Bio text, 14/20 secondary, five    │ │    = "# in the world" (§9.3 #9)
          │  │ lines maximum then ellipsis…       │ │    each pill renders only when its
          │  │ [ Follow ]                         │ │    value > 0; the row Wraps
          │  └────────────────────────────────────┘ │ ← FollowButton, Key "follow:"+artistUri
          │     no header image ⇒ the 132 band is   │    (NowPlayingPanel.cs:287)
          │     a 72-DIP FillSubtleSecondary strip  │    :261-268
          │     holding a 56-DIP PersonPicture      │
          │  │Listened to most in 20/28/600       │ │ ← Loc artist.listenedMostIn, verbatim
          │  │ Los Angeles, US            412,203 │ │ ← 12/16/600 T1  ·  12/16 T3
          │  │ ████████████████████░░░░░░░░░░░░░░ │ │ ← bar h4 r2; fill 260·frac AccentDefault
          │  │ London, UK                 298,110 │ │    frac = clamp(n/max, 0.08, 1)
          │  │ ████████████████░░░░░░░░░░░░░░░░░░ │ │    max 5 cities
          │  │Credits            20/28/600        │ │
          │  │PERFORMED BY        eyebrow 12/16/600│ │ ← group key = RoleGroup, FALLING BACK to
          │  │ Name (link, accent)      Vocals    │ │    Role when RoleGroup is blank (:342);
          │  │ Source: Label Ltd.                 │ │    server's own casing, never .ToUpper()
          │  │Next up             20/28/600       │ │ ← 12/16 T3, MaxLines 2
          │  │ [▣] Track  · Artist                │ │ ← TrackRow.ArtCard(Rail) art 40, gap 2
          │  │ [▣] Track  · Artist                │ │    max 5, no duration, no explicit badge,
          └──┴────────────────────────────────────┴─┘    ColumnSet Video:true (the video glyph
                                                          column IS on), each row in a r4 wrapper
                                                          with HoverFill FillSubtleSecondary
                                                          (:37, :420-434)
```

**The pending arm.** While `NowPlayingInfo` is loading, the About block is `LoadingSection()` — and it is wrapped in
the SAME `Section(Loc.Get(Strings.Detail.AboutTheArtist))` header, so the reader sees "About the artist" over three
shimmer bars, not three bars floating alone (`NowPlayingPanel.cs:467`). On failure the section is absent entirely.

### W2 — Rail, docked, Details / **Player** deck @ 340

Identical to W1 above the fold except the hero slot, which is `NpvDeck.Create(track, preset, 324)` keyed
`"<slug>@324"`. The SelectorBar's second item takes the preset's short label (`Loc.Get(preset.ShortLabelKey)`,
`NpvHeaderRow.cs:71`), so the pill reads `[▣ Cover│◉ Cassette]` etc. The `ArtVideoToggle` is unchanged — it offsets by
`ArtTop` and still lands on the deck's top-right corner. **Ch.23 owns the deck's own drawing.**

### W3 — Rail, docked, **Queue**, loaded @ 340

```
          ┌─────────────────────────────────────────┐
          │Queue                                ✕  │ ← header h44, Pad(12,0,8,0)
          │20/28/600, NoWrap + ellipsis   32² r4    │    title = Loc player.queue here; per mode:
          ├─────────────────────────────────────────┤    Lyrics / Queue / Friend Activity / Video /
          │                                         │    Now playing (RightRail.Title, :436-443)
          │                                         │    (no separator line — this is a gap)
          │14│ Pad top 4                        │14│
          │ ┌──────┐ ┌──────┐ ┌──────────┐        │ ← Pills row Gap 8, Pad(0,4,0,10)
          │ │⤨ Shuf│ │⟳ Repe│ │ ∞ Autopla│        │    each h32 r16 Pad(12,0,12,0) gap 6
          │ └──────┘ └──────┘ └──────────┘        │    glyph 12 / label 12/16/600
          │  off: FillCardSecondary   on: accent   │    on → TextOnAccentPrimary
          │                                        │
          │ Playing from Daily Mix 3            ›  │ ← h28 r4 Pad(8,2,8,8) hover RowHover
          │ 12/16  T2 + T1@700              chev12 │
          │ ┌────────────────────────────────────┐ │ ← NowPlayingCard r8 FillCardDefault
          │ │ ┌────┐              Track title    │ │    1-DIP StrokeCardDefault, Pad 10
          │ │ │ ▣▶ │   gap 16     Artist links  ♡│ │    MinHeight 64, Margin bottom 10
          │ │ │ 44 │              14/20/600 ACC  │ │    art 44 r4 + centred 28 FAB
          │ │ └────┘              12/16 T2   30w │ │
          │ └────────────────────────────────────┘ │
          │ NEXT IN QUEUE   3            Clear     │ ← header slot h36  Pad(8,12,8,4)
          │ eyebrow T3      12/16/600 T3  pill r4  │    eyebrow row MinHeight 20
          │ ♡ [▣]  Song title             ⋯   ✕   │ ← row slot h44  Pad(8,0,4,0) gap 8
          │ 26  34  14/20/600 T1         32²  32² │    heart slot 26 · art 34 r4
          │        12/16 T2 artist links           │    ⋯ glyph Icons.More 14, Opacity 0 →
          │                                        │      HoverOpacity 1, HoverFill RowPressed
          │                                        │    ✕ glyph ChromeClose 12, HoverFill
          │                                        │      RowPressed — **ALWAYS PAINTED**, it
          │                                        │      carries NO hover-opacity on the RAIL
          │                                        │      (:661 vs :677-689). The STAGE's ✕ is
          │                                        │      the hover-revealed one (W12).
          │                                        │    both TextTertiary → TextPrimary
          │ ♡ [▣]  Song title                     │
          │ ♡ [▣]  Song title                     │
          │ NEXT UP        12                      │ ← no Clear on Next up
          │ ♡ [▣]  Song title                     │
          │      …                                 │
          │ ┌────────────────────────────────────┐ │ ← ShowMore h40 r4 FillCardSecondary
          │ │  ⌄  Show next 100  ·  63 more      │ │    Margin(0,4,0,2), Gap 8
          │ └────────────────────────────────────┘ │    12/16/600 T1 + 12/16 T3
          │ AUTOPLAY       8                       │ ← header slot h54 (has a hint line)
          │ Similar music will keep playing        │ ← 12/16 T3
          │ ♡ [▣]  Song title            Opacity   │ ← autoplay rows: whole row Opacity 0.72
          │ ♡ [▣]  Song title              0.72    │
          │14│ Pad bottom 14                   │14│
          └─────────────────────────────────────────┘
```

**Row variants the wireframe above does not show.**

| variant | what changes | source |
|---|---|---|
| **thin row** (`HydrationLevels.TitleMissing`) | the title/artist column is replaced by two bars, 120×14 over 80×11, `Gap 4`, `FillSubtleSecondary`. The heart, the art, the ⋯ and the ✕ all stay | `QueuePanel.cs:629-636` |
| **classic row style** (Settings ▸ Appearance, `TrackRowStyle == 1`) | no artwork, `Radii.None`, a 1-DIP `StrokeDividerDefault` bottom hairline, title+artists folded into ONE 14/20 span run, the heart lane keeps its 26 slot at `RowExtent`, and — the one place the queue shows one — `TrackRow.ClassicExplicitBadge` pinned AFTER the ellipsized identity. Its thin-row form is a single 160×14 bar | `:745-805`, `:790`, `:756` |
| **explicit** | the MODERN queue row has **no explicit badge at all**; only the classic run carries the word-mark | `:790` |
| **artwork hidden** (Settings ▸ Appearance, independent of classic) | `showTrackArtwork = !classic && !artworkHidden` drops the 34-square and its FAB; the key gains `":art=False"` so the row remounts | `:141-145`, `:695` |
| **viewer** (a remote Connect device is active) | no ✕ (a 32-wide spacer stands in), no Move up/down, no Clear pill, and the row owns its own drag source instead of the `Reorderable` | `:140`, `:677-689`, `:497`, `:699-701` |

### W4 — Rail, Queue, **empty** @ 340

Nothing playing **and** no user queue **and** no context continuation → the pills row still renders, then
`EmptyState.Compact(Loc.Get(Strings.Player.NothingPlaying))` — a `Ui.Subtitle` (20/28/600) headline, centred,
`Gap 4`, `Padding 24` (`QueuePanel.cs:175-176`, `EmptyState.cs:43-45`, `:68-72`). With `slots.Count == 0` the whole
body is still wrapped in `_reorder.List(...)` keyed `"lane:empty"` so a foreign drop has a target: item count 0 ⇒ every
position resolves to slot 0 ⇒ the play-next insert (`QueuePanel.cs:185-188`).

### W5 — Rail, Queue, **row hover + drag in progress** @ 340

```
          │ NEXT IN QUEUE   4            Clear     │
          │ ♡ [▣]  Song title             ⋯   ✕   │ ← hover: Fill WaveeColors.RowHover
          │                                        │    ⋯ only: Opacity 0 → 1 (HoverOpacity).
          │                                        │    The ✕ was already there (see W3).
          │                                        │    press: RowPressed + PressScale 0.98
          │ ╭────────────────────────────────────╮ │ ← LIFTED row: stays in place at
          │ ┆ ♡ [▣]  Song being dragged          ┆ │    Drag.SourceDimOpacity = 0.40
          │ ╰────────────────────────────────────╯ │    (DragLift.Stationary — no lift-off)
          │ ♡ [▣]  Song title       ← FLIP glides  │
          │ ♡ [▣]  Song title          neighbours  │
          │ NEXT UP        12       ← headers glide too (they are slots)
```
The drag **chip** follows the pointer (Ch.01 owns its visuals). Dropping outside the list commits nothing
(`RequireDropOnList = true`, `QueuePanel.cs:69`). Dropping into another section commits nothing and shows an
Informational toast, `Strings.Drag.CantMoveAcrossSections` = "Can't move across sections" (`QueuePanel.cs:281-284`).
A foreign payload arriving over the lane shows `Strings.Drag.AddToQueue`; a refusal shows `Strings.Drag.ReorderHint`
(another queue surface's row), `Strings.Drag.CantAddArtist`, or `Strings.Drag.NothingToAdd` (`QueuePanel.cs:233-242`).

### W6 — Rail, **Friends** @ 340, rows / skeleton / offline

```
 rows                                    skeleton                        offline / empty / error
 ┌──────────────────────────────┐        ┌──────────────────────────┐    ┌──────────────────────┐
 │Friend Activity           ✕   │        │Friend Activity       ✕   │    │Friend Activity   ✕  │
 ├──────────────────────────────┤        ├──────────────────────────┤    ├──────────────────────┤
 │12│ Pad top 4            │12  │        │12│ Pad top 4        │12  │    │                     │
 │ ⬤  Ada Lovelace     ▌▌▌ │     │ h56   │ ⬤  ▬▬▬▬▬▬▬▬▬▬          │    │  No friend activity │
 │ 40  Song • Artist    eq14│     │ gap12 │ 40  ▬▬▬▬▬▬▬▬▬▬▬▬▬       │    │        yet          │
 │  ⬤  Daily Mix 3          │     │ pad   │  (40 circle + 120×12    │    │  20/28/600 centred  │
 │ 12 accent dot, 2-ring    │     │ (8,4, │   + 160×12, r4,         │    │                     │
 │ ⬤  Grace Hopper    3 hr │     │  8,4) │   FillSubtleSecondary)  │    │  When friends you   │
 │     Song • Artist   12/16│     │       │   × 5 rows              │    │  follow listen…     │
 │     Album Name      /600 │     │       │                         │    │  12/16 secondary    │
 └──────────────────────────┘             └─────────────────────────┘    │   [ Retry ]  (error)│
                                                                          └─────────────────────┘
```
Row ink: name 14/20/600 `TextPrimary`; `"Track  •  Artist"` 12/16 `TextSecondary`; context (ContextName ?? AlbumName)
12/16 `TextTertiary`; trailing slot **Width 44** carrying either `WaveeEqualizer.Of(true, () => Tok.AccentDefault, 14f)`
(live) or the relative time 12/16/600 `TextTertiary`. **No zebra.** Hover/press plates only on rows that navigate
(`FriendsPanel.cs:111-118`).

### W7 — Rail, **Video** mode, video docked @ 340

```
          ┌─────────────────────────────────────────┐
          │█████████████████████████████████████████│ ← DockedCap: full-bleed, NO inset
          │██  DockedVideoSurface (Ch.24)        ██│    Fill Tok.MediaLetterbox (#000 opaque)
          │██  height = FitDockedVideoHeight      ██│    at 340: 16:9 floor = 191.25
          │█████████████████████████████████████████│    splitter strip 16 DIP over the bottom,
          │─ ─ ─ ─ ─ splitter 16, pass-through ─ ─ ─│    HitTestPassThrough (chrome keeps working)
          ├─────────────────────────────────────────┤    Min = 9/16·railW, Max = 560
          │Video          ⧉    ⛶    ✕              │ ← header h44: pop-out · fullscreen · close
          │20/28/600     32²  32²  32²              │    glyphs 16, TextSecondary → TextPrimary
          ├─────────────────────────────────────────┤
          │12│ Pad(12,8,12,12)  Gap 8           │12│
          │  Track title, two lines max            │ ← NowPlayingTitle 20/28/600
          │  Artist · Album                        │ ← TrackMeta 12/16 secondary
          │  ────────────────────────────────────  │ ← Rule h1 StrokeCardDefault
          │  UP NEXT                               │ ← Eyebrow 12/16/600 +30tr T3 (player.videoUpNext)
          │  [▣] Song  · Artist                    │ ← ArtCard(Rail) art 40, Gap 2, max 5,
          └─────────────────────────────────────────┘    Key "vrp:"+ItemId, ColumnSet Video:true
```
**Nothing upcoming**: the rows are replaced by `EmptyState.Compact(Loc.Get(Strings.Player.QueueEmpty))` =
"Nothing up next" — the rule, the eyebrow and the track meta above it all keep rendering
(`VideoRailPanel.cs:79-80`). **Nothing playing**: the title and meta lines are simply absent; the rule and the
eyebrow still render. **Bridge not attached**: `RightRail` hands back a bare title + close header
(`RightRail.cs:263-265`).
**No video for this track** (B10): the card's own gate collapses it to nothing and the body's first child becomes a
16:9 letterboxed placeholder — cover art at `Opacity 0.4` under the centred label
`Strings.Player.NoVideoForThisSong` = "No video for this song" at 12/600 `Tok.TextOnAccentPrimary`, corners 8, fill
`Tok.MediaLetterbox` (`VideoRailPanel.cs:113-133`). The rail does **not** close.

### W7b — the rail's **width ladder**: 200 · 340 · 500 (every frame above is ONE rung of it)

W1–W7 are all drawn at the 340 default. The rail is not a fixed strip: it is a live-resizable seam, clamped
`RailMinW 200 … RailMaxW 500` around `RailDefaultW 340` (`ShellResponsiveLayout.cs:126-127`), dragged from its LEFT
edge and committed to `WaveeSettings.ShellRailWidth` (`WaveeShell.cs:453-458`). **18 W12b owns the seam's detent**
(no `FadeStart`/`Resist`/`ForcePush`/`ReExpand` passed ⇒ engine defaults: resist below 200, the panel fades to 0.35,
the rail CLOSES at raw ≤ 156 and re-opens past 210) — do not re-specify it here.

**There is no width branch anywhere in the rail.** No `if (railW < …)`, no breakpoint, no per-width layout: every
number below is a *derivation*, an *ellipsis* or a *clip*. That is exactly why the ladder needs a table — a port that
invents a breakpoint is wrong, and a port that drops one derivation only shows it at 200 or at 500.

| derived quantity | @ 200 (min) | @ 340 (default) | @ 500 (max) | rule | source |
|---|---|---|---|---|---|
| hero art / deck side | **184** | 324 | **484** | `round((railW − 2·Spacing.S)/4)·4` | `NowPlayingPanel.cs:541-542` |
| pinned Details block | **236** | 376 | **536** | `ArtTop (8+36+8) + side` | `NowPlayingPanel.cs:556-563`, `NpvHeaderRow.cs:27` |
| deck remount cadence | — | — | — | the hero key is `"<slug>@"+(int)side`, so a drag re-keys **once per 4-DIP quantum** — ~75 remounts across a full 200→500 sweep, and that quantisation IS the budget (a per-DIP key would cost 4×) | `NowPlayingPanel.cs:541-547` |
| NPV title column | **136** | 276 | **436** | `railW − 16 (hero-meta S insets) − 12 (Gap M) − 36 (SaveButton box)` | `NowPlayingPanel.cs:163-192` |
| docked cap, fitted 16:9 | **120** | 191.25 | 281.25 | `clamp(railW·ratio, DockedVideoFitMinH 120, 560)` — at 200 the 16:9 fit (112.5) is clamped UP, so a 16:9 stream sits in 3.75-DIP bars top and bottom. **200 is the one width where the fit and the splitter floor disagree** | `ShellResponsiveLayout.cs:137-158` |
| cap splitter floor | 112.5 | 191.25 | 281.25 | `DockedVideoNaturalH = railW·9/16` — the DRAG floor only, a different number from the fit's 120 | `ShellResponsiveLayout.cs:130-131`, `RightRail.cs:246-252` |
| a **pinned** cap height | unchanged | unchanged | unchanged | `CommitRailDrag` writes the WIDTH only; nothing re-clamps `DockedVideoHeight` on a rail resize, so a 560 cap pinned at rail 500 is still 560 at rail 200 (a 200×560 box around a 112.5-tall picture) | `WaveeShell.cs:453-458` vs `ShellResponsiveLayout.ClampDockedVideoHeight` |
| Video-body placeholder | 99 | 177.75 | 267.75 | `(railW − 24)·9/16` — the 16:9 no-video poster sits inside the body's `Pad(12,8,12,12)`, **not** full-bleed like the cap | `VideoRailPanel.cs:89-94`, `:113-119` |
| queue row identity lane | **4** | 144 | 304 | `railW − 196` = `railW − 28 (root Pad 14/14) − 12 (row Pad 8/4) − 124 (heart 26 + art 34 + ⋯ 32 + ✕ 32) − 32 (4 × Gap S)` | `QueuePanel.cs:190-206`, `:606-690` |
| …classic / artwork-hidden | 46 | 186 | 346 | `railW − 154` (the 34 art and one Gap S come back) | `QueuePanel.cs:591-605`, `:745-805` |
| friends row text lane | **52** | 192 | 352 | `railW − 148` = `railW − 24 (shell Pad) − 16 (row Pad) − 40 (avatar) − 44 (trailing slot) − 24 (2 × Gap M)` | `FriendsPanel.cs:104-142`, `:222-227` |
| player-style flyout | **340** | 340 | 340 | a `const`, anchored `BottomEdgeAlignedRight` off a 32-DIP gear: at rail 200 the flyout is **140 DIP wider than the rail** and overhangs the page to its left. It does not narrow, and its 74.5 thumb cards do not reflow | `PlayerStyleFlyout.cs:30`, `NpvHeaderRow.cs:103-107` |
| `RailFits` | recomputed **live from `RailWidth`** | — | — | widening can flip the rail dock→float **mid-drag**: at vpW 1060 with a 240 sidebar every width above 340 floats (W8's formula) | `WaveeShell.cs:696-701`, `ShellUi.cs:88-90` |

**Three lanes absorb every width change, and nothing else does.** `Shrink` defaults to **0** engine-wide (Yoga-style),
so no control in the rail squashes: the flexible lanes are the NPV header's eyebrow (`Grow 1, Basis 0, MinWidth 0,
NoWrap, CharacterEllipsis` — `NpvHeaderRow.cs:84-91`), the queue / friends identity columns (the same four properties —
`QueuePanel.cs:627`, `FriendsPanel.cs:125`) and the NPV title column (`NowPlayingPanel.cs:180`). Everything else is
fixed and clipped. Three consequences a 340-only port never sees:

- the queue's **pills row** neither wraps nor shrinks (`Gap 8`, three pills of ≈ 250 DIP against `railW − 28`), so
  below ≈ 280 the Autoplay pill is cut by the panel root's own `ClipToBounds` (`QueuePanel.cs:190-193`, `:815-860`);
- the **thin-row skeleton**'s bars are fixed 120×14 / 80×11 with `Shrink 0`, so in a 4-DIP lane they overflow rather
  than compress (`QueuePanel.cs:629-636`) — the classic thin row's single 160×14 bar likewise (`:756`);
- the NPV header's `SelectorBar` + gear are `Shrink 0` too, so the eyebrow ellipsizes to nothing FIRST and only then
  does the row overrun the tile's 8-DIP inset into the rail surface's `ClipToBounds` (`RightRail.cs:89-97`).

```
 W7b-a  Details @ 200 (min)           W7b-b  Queue @ 200 (min)          W7b-c  Friends @ 200
 ┌───────────────────────┐            ┌───────────────────────┐         ┌───────────────────────┐
 │Now p…[▣│◉ Cass]  ⚙   │ h36        │Queue              ✕  │ h44     │Friend Activi…     ✕  │
 │ ↑ eyebrow ellipsizes  │            ├───────────────────────┤         ├───────────────────────┤
 │   FIRST; the pill and │            │┌────┐┌────┐┌───────┐┆│ ← the   │ ⬤ Ada Lovel…   ▌▌▌ │
 │   the gear never      │            ││⤨ Sh││⟳ Re││ ∞ Auto┆│  ∞ pill │ 40  Song • Ar…  eq14│
 │   squash              │            │└────┘└────┘└───────┘┆│  is cut │  ⬤ Daily Mi…        │
 │┌─────────────────────┐│            │ Playing from Daily…› │  by the │ ⬤ Grace Hop…  3 hr │
 ││                     ││            │┌─────────────────────┐│ root's │     Song • Ar…  12/16│
 ││     184 × 184       ││ ← the      ││┌──┐  Track title   ││ clip    │     Album Na…   /600 │
 ││   cover or deck     ││   ONLY     │││▣▶│  Artist link  ♡││        └───────────────────────┘
 ││   r8                ││   number   ││└──┘  14/20/600 ACC ││        text lane = 52 DIP: every
 │└─────────────────────┘│   that     │└─────────────────────┘│        line ellipsizes; the 40
 │ Title wraps inside a  │   scales   │NEXT IN QUEUE 3  Clear│        avatar and the 44 trailing
 │ 136-wide column, then │   with the │ ♡ [▣] T…    ⋯    ✕  │        slot do not move
 │ ellipsi… ┌──┐         │   rail     │ ↑ identity lane = 4  │
 │          │ ♡ │        │            │   DIP: the title and │
 │ Artist…  └──┘         │            │   the artist line    │
 │ Album…                │            │   ellipsize to       │
 │▌ sung line…           │            │   nothing; heart/art/│
 │▌ next line, faded     │            │   ⋯/✕ keep their     │
 │ About the artist      │            │   full lanes         │
 └───────────────────────┘            └───────────────────────┘
      ⚙ opens a 340-wide flyout over a 200-wide rail (it overhangs LEFT, onto the page)
```

**@ 500 (max) nothing new appears** — every flexible lane simply grows (art 484, queue identity 304, friends text
352), the About / Credits / Next-up sections keep their `Pad(12,0,12,12)` and `Gap 16`, and the flyout stays 340
while the hero under it is 484 wide. The only qualitative changes at the top of the ladder are the two in the table:
the dock→float flip once `sidebarW + 500 + 480 > viewportW`, and a docked cap that can finally reach its 560 ceiling
(`railW·ratio > 560` needs a portrait stream — a 9:16 clip at rail 500 wants 889 and is clamped).

### W8 — Rail, **floating** (`!RailFits`) @ any width

```
   page content continues underneath ──────────────►
                       ╔═════════════════════════════╗ ← uniform 1-DIP StrokeCardDefault ring
                       ║ surface Fill = FileArea     ║    over WaveeColors.FloatingChrome
                       ║ Shadow = Elevation.Flyout   ║    (blur 16, y 8, #00000042 dark /
                       ║   (the ONLY case with one)  ║     #00000024 light)
                       ║ Corners still (8,0,0,0)     ║    NO reservation spacer — the page is
                       ╚═════════════════════════════╝    not resized, the panel overlays it
```
`RailFits` = `sidebarW + railW + 480 ≤ viewportW` (`ShellResponsiveLayout.cs:164-165`, `ShellUi.cs:88-90`). At the
340 default with a 240 sidebar that is `viewportW ≥ 1060`; compact sidebar (56) ⇒ `viewportW ≥ 876`.

### W9 — Player-style flyout, open (anchored bottom-right of the gear) — 340 wide

```
 ┌──────────────────────────────────────────┐ ← Popup, PopupChrome.Popup, FocusTrap,
 │ 12│                                  │12 │    LightDismiss, BottomEdgeAlignedRight
 │  Player style       14/20/600            │
 │  Media             12/16 TextSecondary   │ ← Gap 12 between blocks; Gap 6 label→cards
 │  ┌──────┐┌──────┐┌──────┐┌──────┐        │ ← 4 cards, ThumbSize 74.5, gap 6
 │  │ ▓▓▓▓ ││ ▓▓▓▓ ││ ▓▓▓▓ ││ ▓▓▓▓ │        │    card: Pad 4, Gap 4, r6
 │  │ mini ││ mini ││ mini ││ mini │        │    mini-art 66.5² r4
 │  │Record││Casset││ Reel ││  CD  │        │    label 11/14, MaxLines 2
 │  └══════┘└──────┘└──────┘└──────┘        │    SELECTED: 2-DIP AccentDefault ring +
 │   ↑ 2px accent ring, label TextPrimary   │    TextPrimary label (others 1-DIP
 │  Devices                                 │    StrokeCardDefault + TextSecondary)
 │  ┌──────┐┌──────┐┌──────┐┌──────┐        │
 │  │Turnta││ iPod ││Winamp││ Hi-fi│        │
 │  └──────┘└──────┘└──────┘└──────┘        │
 │  Software                                │
 │  ┌──────┐┌──────┐┌──────┐┌──────┐        │
 │  │ Zune ││Visual││Canvas││Pictur│        │
 │  └──────┘└──────┘└──────┘└──────┘        │
 │  Options            12/16 TextSecondary  │
 │  Finish     ⬤ ⬤ ⬤ ⬤ ⬤                  │ ← swatch row: Gap 8, outer 30 circle
 │  (60 min)   30² each, inner 22           │    (2-DIP accent ring when selected),
 │  Size       ┌─────────┬─────────┐        │    inner 22 circle + 1-DIP StrokeCardDefault
 │             │ 12″ LP  │7″ single│        │ ← Segmented: h28, font 12, ItemMinWidth 56,
 │             └─────────┴─────────┘        │    r5 outer / r4 item
 │  Speed      ┌─────────┬─────────┐        │    OptionRow: Gap 10, Justify SpaceBetween,
 │             │  33⅓    │   45    │        │    Wrap = true (a 4-choice row wraps under
 │             └─────────┴─────────┘        │    its label rather than squashing)
 │  Sleeve     ┌─────────┬─────────┐        │
 │             │  Shown  │ Hidden  │        │
 │             └─────────┴─────────┘        │
 └──────────────────────────────────────────┘
```
Every one of the twelve presets carries at least one option (`NpvPlayerCatalog.cs:32-81`), so the "Options" label is
never left standing over nothing — there is no empty arm to design. Option counts run 1 (Reel, CD) to 4 (Record,
Turntable). A `Wrap = true` row lets a 4-choice Segmented drop under its 60-DIP label rather than squashing.

### W10 — The artwork context menu (right-click the hero, either presentation)

```
 ┌──────────────────────────────┐
 │ ◉  Cover                     │ ← RadioItem, Icons.Picture, checked when presentation == 0
 │ ○  Record                    │ ← RadioItem, Icons.Album, label = preset.ShortLabelKey
 │ ──────────────────────────── │ ← MenuFlyoutItem.Separator
 │ ⚙  Player style…             │ ← opens the SAME popup the gear owns (StyleFlyoutOpen = true)
 └──────────────────────────────┘
```

### W11 — Stage, **WIDE**, lyrics pane @ 1440 × 900 (1 char ≈ 16 DIP)

```
 ┌──────────────────────────────────────────────────────────────────────────────────────────┐
 │  (48 DIP window caption strip — reserved, pass-through)                                   │
 ├──────────────────────────────────────────────────────────────────────────────────────────┤
 │ scrim 0.76 at y=0 …………… feathers to the 0.46 plateau at 22% of the body ………              │
 │                                                                         ┌──┐  ┌───┐       │ ← TopBar h88
 │   column shade: 0.26 held to x=352, feathered to EXACTLY 0 at x=612     │🌐│  │ ⌄ │       │   Pad(16,16,16,12)
 │                                                                         └──┘  └───┘       │   ScrimFab 40 · ExitFab 44
 │  ┌────────────────────┐              ← Gap 56 →                                           │
 │  │                    │   Lyric line one, left-anchored, max 700 wide                      │
 │  │                    │                                                                    │
 │  │   cover 300 × 300  │   Lyric line two — the sung one                                    │
 │  │   r8               │                                                                    │
 │  │   Elevation.Dialog │   Lyric line three (blurred by distance — Ch.22)                   │
 │  │                    │                                                                    │
 │  └────────────────────┘                                                                    │
 │   ↕ 18                                                                                     │
 │   Track Title              22/28/650 Display face, Ink                                     │
 │   Artist — Album  ♡  ⋯     14/20/400 InkSecondary (underlines on hover) · 32² · 32²        │
 │   ↕ 18                                                                                     │
 │   ▬▬▬▬▬▬●───────────────   SeekBar (Ch.20), full 304 wide                                  │
 │   0:47        FLAC    −2:13   12 InkTertiary  ·  11.5/16/600 InkTertiary  ·  12            │
 │   ↕ 8                                                                                      │
 │      ⤨    ◀◀   ( ▶ )   ▶▶   ⟳      32 · 40 · 56 · 40 · 32, Gap 6, centred                  │
 │   ↕ 18                                                                                     │
 │   🔊 ▬▬▬▬▬▬▬▬●─────────    glyph 32² + Slider length 264, thickness 20                     │
 │   ↕ 8                                                                                      │
 │   🎧 Headphones (Realtek)   MinHeight 24, 12/16/400 InkTertiary → Ink on hover              │
 │                                                                                            │
 │  ←24→ column content 304 ←24→                                    scrim deepens from 62%    │
 │                                                          ┌────────┬────────┐               │ ← Pivot h72
 │                                                          │ Lyrics │ Queue  │               │   End/End Gap 16
 │                                                          │ ══════ │        │               │   Pad(24,0,24,16)
 │                                                          └────────┴────────┘               │   underline 2 accent
 ├──────────────────────────────────────────────────────────────────────────────────────────┤
 │  (72 DIP player bar — reserved, pass-through)                                              │
 └──────────────────────────────────────────────────────────────────────────────────────────┘
   |← 352 column box →|← 56 gap →|← pane region (Grow 1), reading column max 700, 48 trailing gutter →|
```
The 🌐 `ScrimFab` is **conditional twice over**: it renders only when `LyricsPrefs.Available != 0` (the document on
screen actually carries a translation or a romanization) AND the LYRICS pane is the one up — a translation toggle over
a queue is chrome for a pane you cannot see (`ImmersiveLyricsSurface.cs:364-369`). The ⌄ `ExitFab` is unconditional.
With nothing playing the title reads `player.nothingPlaying`, `canTransport` is false (prev / next / shuffle / repeat
take `InkTertiary`, lose their cursor and their hover scale) and `primaryEnabled` is false (the play disc drops to the
`ScrimRest` plate with an `InkTertiary` glyph). A playback **error** kills `canTransport` alone — the play disc stays
live (`StageIdentity.cs:89-90`, `StageChrome.cs:129-143`, `:263-276`).

### W12 — Stage, WIDE, **queue pane** @ 1440 × 900 (1 char ≈ 16 DIP)

```
 │  ┌────────────────────┐        ▒▒ PaneShade: 0 on the pane's LEFT, 0.24 at the window edge ▒▒
 │  │   cover 300        │  ←56→  Playing next    from Daily Mix 3                              │ ← 20/28/600 Ink
 │  └────────────────────┘        ▬▬▬▬                                                          │   + 12/16 InkTertiary
 │   Track Title                  ↑ SectionRule 20 × 2, accent          Pad(24,24,24,0)          │
 │   Artist — Album  ♡  ⋯                                                                        │
 │   ▬▬▬●────────────             ┌──────────────────────────────────────────────┐              │
 │   0:47   FLAC   −2:13          │ ∞  Autoplay                                  │ h48, r4      │
 │    ⤨  ◀◀ (▶) ▶▶ ⟳              │    Similar music will keep playing           │ glass ramp   │
 │   🔊 ▬▬▬▬●──────                └──────────────────────────────────────────────┘              │
 │   🎧 Headphones                 ⠿ [▣]  Song title                    3:41  ✕ │ h56 rows      │
 │                                 24 38   14/20/600 Ink                 44   32│ Gap 12        │
 │                                     12/16 InkSecondary                        │ Pad(8,0,8,0) │
 │                                 ⠿ [▣]  Song title                    4:02  ✕ │              │
 │                                 ⠿ [▣]  Song title (autoplay, Opacity 0.68)   │              │
 │                                 ┌──────────────────────────────────────────┐ │              │
 │                                 │            ⌄  ·  63                      │ h40 r4 glass   │
 │                                 └──────────────────────────────────────────┘ │              │
 │                                                          ┌────────┬────────┐ │              │
 │                                                          │ Lyrics │ Queue  │ │              │
 │                                                          │        │ ══════ │ │              │
```
The asymmetries against the rail's queue — all of them, because "the rail's row with a grip" is the wrong mental model:

| | rail queue row | stage queue row |
|---|---|---|
| section captions | three headers (36 / 54) | **none** — `QueueSlots.Build(…, headers: false)` (`StagePanes.cs:240`) |
| "Show more" | `⌄ Show next 100 · 63 more` | `⌄ · 63` only (`:536-551`) |
| page counters | three, one per section (`QueuePanel.cs:47-49`) | **one**, shared (`StagePanes.cs:171`, `:235-239`) |
| heart lane | 26-DIP slot, always painted | **absent** |
| ⋯ overflow | 32², hover-revealed | **absent** — right-click / Menu key only |
| ✕ | 32², **always painted** | 32² `StageChrome.Glyph`, `Opacity 0 → HoverOpacity 1` (`:512-521`) |
| grip | none | 24-DIP `Icons.GripperBar` 14 `InkTertiary`, `HitTestVisible = false`, `HoverOpacity` only when the list owns the drag (`gripped`) — a **viewer never sees it** (`:404`, `:476-481`) |
| duration | none | 44-DIP end-aligned 12/16 `InkTertiary` column (`:504-511`) |
| swipe (touch) | like / remove via `RowSwipe` | **none** |
| classic row style | supported (hairline, one span run, explicit word-mark) | **not supported** — one skin |
| thin-row skeleton | two bars when the title is missing | **none** — the stage paints the raw title |
| dim (autoplay) | `Opacity 0.72` | `Opacity 0.68` |
| hover plate | `WaveeColors.RowHover` | `StageInk.GlassHover` (Ink @ 0.10) |
| row key | `RowKey + ":art=" + show + ":classic=" + classic` | `RowKey + ":art=" + show` where `RowKey` is `"si"/"se"`-prefixed (`:452`, `:578`) |

**Nothing upcoming**: the pane shows no `EmptyState` at all — a `Padding top 24` box holding
`Loc.Get(Strings.Player.QueueEmpty)` = "Nothing up next" at 14/20 `StageInk.InkTertiary` (`StagePanes.cs:261-269`).
The ∞ Autoplay row above it keeps rendering, and the whole body is still wrapped in `_reorder.List` keyed
`"stagelane:empty"` so a foreign drop has a target.

### W13 — Stage, **COMPACT** @ 560 × 900 (1 char ≈ 16 DIP)

```
 ┌───────────────────────────────────────┐
 │  (48 caption)                          │
 ├───────────────────────────────────────┤
 │                          ┌──┐  ┌───┐   │ ← TopBar h56 (CompactTopBandH), same Pad
 │                          │🌐│  │ ⌄ │   │
 │  Pad(16,8,16,8)                        │
 │ ┌────┐  Track Title       ♡   ◀◀ ▶ ▶▶ ⋯│ ← art 64 r4 Elevation.Card, decode 192
 │ │ 64 │  16/22/650                      │   identity Grow 1; heart 32²
 │ └────┘  Artist — Album   32²  32 40 32 │   prev/next 32 glyph15, play 40 glyph17
 │                                     32²│   overflow "…" 32² (ALWAYS present here)
 │ ↕ 4                                    │
 │ ▬▬▬▬▬▬●──────────────────────────────  │ ← the seek NEVER folds
 │ 0:47            FLAC            −2:13  │
 ├───────────────────────────────────────┤
 │                                        │
 │   Lyric line (full-width pane)          │ ← the pane takes the whole surface; Gap 0
 │   Lyric line                            │
 │                                        │
 │                     ┌────────┬────────┐│
 │                     │ Lyrics │ Queue  ││ ← pivot unchanged: h72, End/End
 │                     │ ══════ │        ││
 ├───────────────────────────────────────┤
 │  (72 player bar)                       │
 └───────────────────────────────────────┘
   The "…" carries, in this order (only the folded ones):
     ☑ Shuffle        (Toggle, Icons.Shuffle)
     ☑ Repeat         (Toggle, Icons.RepeatOne | RepeatAll)
       Mute / Unmute  (Icons.Volume | Icons.Mute)
     ───────────────  (separator, only if anything precedes)
       …the whole two-section DevicePickerMenu item list
   Built at OPEN time (`StageIdentity.cs:665-699`), anchored BottomEdgeAlignedRight, FocusTrap + LightDismiss,
   ConstrainToRootBounds = false. If every folded control happens to be absent the button opens NOTHING — an
   `items.Count == 0` early return, not an empty menu (`:691`). Tooltip: `player.nowPlaying`.
```

### W14 — Stage, wide, **height-folded** (the vertical ladder) @ 1440 × 680 and × 640

```
 vpH 900 → availH 692 → art 300, nothing folded          (chrome 320)
 vpH 700 → availH 492 → art 172, nothing folded          (chrome 320; 492−320 = 172 ≥ 168)
 vpH 680 → availH 472 → DEVICE LINE FOLDS, art 184       (chrome 288)
 vpH 640 → availH 432 → + VOLUME ROW FOLDS,  art 192     (chrome 238)
 vpH 614 → availH 406 = WideEnterH  ← the exact demotion point
 vpH 613 → COMPACT
   availH(vpH) = vpH − 48 (caption) − 72 (player bar) − 88 (TopBandH)  =  vpH − 208
   art = floor((availH − chrome) / 4) · 4, clamped to [168, 300]
 Unfolding a rung needs 24 DIP of reserve (FoldHysteresisH); folding takes none.
```

### W15 — Stage identity row, hover / focus states

```
   Track Title                                       ← title never reacts
   Artist — Album                    ♡      ⋯        ← rest
   ───────────                                          hover the meta link only: Ink + Underline
                                    ▓▓     ▓▓        ← hover a button: GlassHover (Ink @ 0.10)
                                                        + WhileHover scale 1.07, 83 ms
                                    ██                ← press: GlassPressed (Ink @ 0.16), scale 0.92
                                   [♥]                ← saved: Icons.HeartFill in StageChrome accent
```
The identity region's hit ownership is a **childless full-bleed shield** beneath the content
(`StageIdentity.ContextShield`, `:154-155`). Without it, hovering the gap between two transport buttons lit the heart
and pressing anywhere lit every button's plate at once (`StageIdentity.cs:111-142`). **Contract for anything added
later: no stage container with several interactive descendants may own a pointer/click/press handler.**

### W16 — Stage, lyrics pane, document **absent** / **pending** (1 char ≈ 16 DIP)

The stage is **not** gated on lyrics. Its one entry point is the rail's Lyrics-header expand button, and that button
is returned by BOTH arms of `RightRail.LyricsHeaderKids` — including the `available == 0` arm (`RightRail.cs:382-390`)
— so a track with no lyrics at all still opens a full stage. This is the arm a port loses by treating the stage as
"the lyrics screen": everything except the reading column is unchanged, and the queue pane one pivot-click away is
the same surface as W12.

```
 ┌──────────────────────────────────────────────────────────────────────────────────────────┐
 │  (48 DIP caption strip)                                                                   │
 ├──────────────────────────────────────────────────────────────────────────────────────────┤
 │  same backdrop: floor → σ80 baked-blur cover @1.30 → Scrim() → ColumnShade()               │
 │                                                                               ┌───┐       │ ← NO 🌐:
 │   the scrim and the column shade are unchanged — nothing about them reads      │ ⌄ │       │   Available
 │   the document                                                                └───┘       │   is 0
 │  ┌────────────────────┐              ← Gap 56 →                                           │
 │  │                    │                                                                    │
 │  │   cover 300 × 300  │                                                                    │
 │  │   r8, Dialog       │                  No lyrics available                               │ ← centred in
 │  │                    │                  14/20, LyricsInk.Media secondary                  │   BOTH axes of
 │  └────────────────────┘                  Pad(24,0,24,0), Wrap                              │   the pane box
 │   Track Title                                                                              │
 │   Artist — Album  ♡  ⋯     the whole identity column is UNCHANGED — art, title, seek,      │
 │   ▬▬▬●──────────────       transport, volume and the device line all still render and      │
 │      ⤨  ◀◀ ( ▶ ) ▶▶ ⟳      still work; `canTransport` / `primaryEnabled` are about the      │
 │   🔊 ▬▬▬▬●─────            TRACK, never about the document                                  │
 │   🎧 Headphones                                          ┌────────┬────────┐               │ ← Pivot
 │                                                          │ Lyrics │ Queue  │               │   unchanged,
 │                                                          │ ══════ │        │               │   both links
 │                                                          └────────┴────────┘               │   live
 ├──────────────────────────────────────────────────────────────────────────────────────────┤
 │  (72 DIP player bar)                                                                       │
 └──────────────────────────────────────────────────────────────────────────────────────────┘
```

**The three arms of the reading column, and they are one `Skel.Region` — not three branches**
(`LyricsView.cs:857-876`):

| arm | what fills the 700-wide column | source |
|---|---|---|
| pending | `LyricsShimmer(large: true, ink: Media)` bars — `SkeletonStyle(ink.Skeleton, RowGap **18** (large; the rail's is 14), BarRadius 6, TextRatio 0.86)`, `reveal: SkelReveal.FadeOnly`, `smoothResize: false` | `LyricsView.cs:868-876` |
| ready | the lyrics document (Ch.22) | `:870` |
| **failed · empty · `Lines.Count == 0`** | ONE centred line — `Message("No lyrics available")`: `Grow 1, MinHeight 0`, column, `AlignItems Center`, `Justify Center`, `Pad(24,0,24,0)`, 14/20 `Wrap`, `_ink.Secondary` (= the media ink on the stage, the theme ink in the rail) | `:871-874`, `:2522-2527` |

Three things this frame pins that the W11 frame does not:

1. **The 🌐 `ScrimFab` is gone, and it is gone for the right reason.** `ClearDocument` retires
   `HasTranslation`/`HasRomanization` and republishes `LyricsPrefs.Available = 0` *before* its early-out, so the
   capability never lingers from the previous track (`LyricsView.cs:1195-1202`, `:1150-1155`). The ⌄ `ExitFab` stays —
   it is unconditional (`ImmersiveLyricsSurface.cs:364-372`).
2. **Nothing collapses.** The pane box keeps `Pad(0,0,48,72)` and its 700 `MaxWidth`, the identity column keeps its
   352 wrapper, and the entrance cascade still runs its five rungs — the surface's shape is identical to W11 with one
   sentence where the lines were (`StagePanes.cs:96-123`).
3. **The string is hardcoded English** — `"No lyrics available"` appears as a literal three times in
   `LyricsView.cs:870-874`. Ch.22 owns the fix; it is recorded here because this frame is where a reader meets it,
   and a port that re-types the frame must not re-type the literal.

**Unsynced document** (a plain text block, no timed lines) is a *fourth* arm and is **not** this frame: it renders
real content through `UnsyncedLyricsContent`, and it is the reason `LyricsPrefs.Available` is gated on a TIMED
document — so an unsynced stage also shows no 🌐 while showing plenty of text (`LyricsView.cs:1145-1155`, Ch.22).

---

## 3. Tokens

| element | size (DIP) | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| rail width | 340 default, 200–500 clamp | — | — | — | — | a LIVE seam, not a constant: every width-derived number in the rail is tabulated per rung in **W7b**, and the seam's detent is 18 W12b | `ShellResponsiveLayout.cs:126-127`, `WaveeShell.cs:453-458` |
| rail surface | fill | — | `(8,0,0,0)` | — | `Transparent` docked · `WaveeColors.FileArea` floating | none docked · `Elevation.Flyout` floating | `RightRail.cs:87`, `:96`, `:154` |
| rail edge | 1 docked · **0 floating** | `Margin(0,0,−1,−1)` | `(8,0,0,0)` | — | `Tok.StrokeCardDefault`, left+top only | topmost, `HitTestVisible = false` | `RightRail.cs:104-110` |
| rail ring (floating only) | 1 | — | `(8,0,0,0)` | — | `Tok.StrokeCardDefault` | on the ARM's ROOT box, not on `edge` — `edge` goes to `BorderWidth 0` when floating so the two never double-draw | `RightRail.cs:150-154`, `:182-184` |
| rail hit gate | — | — | — | — | — | `HitTestVisible = baseline \|\| open`: a CLOSED rail is fully laid out, translated off-clip and **unhittable** | `RightRail.cs:148`, `:180` |
| rail reservation gap | 8 | — | — | — | none (bare Mica) | — | `WaveeShell.cs:1147-1151` |
| rail splitter strip | 16 | — | — | — | — | leading polarity, `InvertCollapsed` | `WaveeShell.cs:1240-1257`, `Splitter.StripW = 16` |
| rail header | h 44 (`WaveeSize.NavItemH`) | `Pad(12,0,8,0)`, `Gap 4` | — | `WaveeType.RailHeader` = `Ui.Subtitle` 20/28/600 | `Tok.TextPrimary` | — | `RightRail.cs:121-128`, `:405-409` |
| header glyph button | 32² (`WaveeCta.IconButtonSize`) | centred | 4 (`Radii.Control`) | glyph 16 (`RightRail.HeaderGlyph`) | `Tok.TextSecondary` → `Tok.TextPrimary`; active `Tok.AccentTextPrimary` | `Interaction.Subtle` (transparent → `FillSubtleSecondary` → `FillSubtleTertiary`, 83 ms) | `RightRail.cs:418-434`, `:447` |
| docked cap | full-bleed, h = `DockedVideoHeight` | — | — | — | `Tok.MediaLetterbox` (#000 α1) | — | `RightRail.cs:233-238` |
| cap height floor/ceiling | `railW·9/16` … 560 (the SPLITTER's floor) · the FIT's own floor is **120** | — | — | — | — | the two floors disagree at rail 200 only (112.5 vs 120) — W7b | `ShellResponsiveLayout.cs:130-138`, `:137-158` |
| hero tile | `Pad(8,8,8,0)`, `Gap 8` | — | — | — | — | — | `NowPlayingPanel.cs:556-563` |
| hero art | 324 at rail 340 (`railW − 16`, quantised to 4) — **184** at rail 200, **484** at rail 500 (W7b) | — | 8 (`Radii.Card`) | — | `Placeholder = HeroWashColor(url)` (bound) | `DecodePx 512`, `ImageFit.Cover`, aspect 1, BlurHash | `NowPlayingPanel.cs:126-142`, `:541-542` |
| NPV header row | h 36 | `Gap 4` | — | `WaveeType.Eyebrow` 12/16/600 + 30/1000 tracking | `Tok.TextTertiary` | — | `NpvHeaderRow.cs:27`, `:79-91` |
| SelectorBar (compact) | pill 30, bar 33 | item `Pad(12,3,12,2)`, `Gap 7` | stock | 13 | stock | shape-guarded `TemplateParts` | `NpvHeaderRow.cs:36-49` |
| Art\|Video toggle | h 24, halves 24² | `Margin(0, 56, 12, 0)` | 4 | glyph 11 | `WaveeOnMedia.GlassHover` plate; selected half `Tok.OnMediaPrimary @ 0.16`; glyph `OnMediaPrimary` / `OnMediaSecondary` | — | `RightRail.cs:328-359` |
| NPV hero meta | — | `Pad(8,12,8,16)`, `Gap 12`; inner `Gap 4` / `Gap 2` | — | title `Ui.Subtitle` 20/28/600 MaxLines 3 · artist 14/20 · album 12/16 | `TextPrimary` / `TextSecondary` / `TextTertiary` | — | `NowPlayingPanel.cs:146-198` |
| SaveButton (NPV) | box 36, glyph 16 | — | — | — | — | `Key "save:"+uri` | `NowPlayingPanel.cs:192` |
| lyrics peek | h 112 (2 × 56), spine 3 | `Gap 8` | spine 1.5 | `WaveeType.NpvLyric` = 20/28 Display face, **Weight 350**, `CharSpacing −6` | spine `Tok.AccentDefault`; text `Tok.TextPrimary`; peek slot `Opacity 0.38` | — | `NpvLyricsPeek.cs:18-23`, `:76-103`, `:147-176` |
| peek unsynced note | h 56, spine 3 × 28 | `Gap 8` | 1.5 | 12 | `Tok.TextTertiary` both | — | `NpvLyricsPeek.cs:109-131` |
| NPV section header | — | `Gap 8` under it | — | `WaveeType.RailHeader` 20/28/600 MaxLines 1 | `TextPrimary` | — | `NowPlayingPanel.cs:444-452` |
| About-artist card | header band 132 (or 72 fallback) | body `Pad 12`, `Gap 12` | 8 | name 18/24/600 | `Tok.FillCardSecondary`, 1-DIP `StrokeCardDefault`, hover `FillCardDefault` | `EdgeFade(Bottom, 56)` + a 3-stop scrim | `NowPlayingPanel.cs:229-303` |
| fact pill | — | `Pad(8,4,8,4)`, `Gap 2` | 4 | value 12/16/600, label 12/16 | `Tok.FillSubtleSecondary`; `TextPrimary` / `TextTertiary` | — | `NowPlayingPanel.cs:454-465` |
| top-city bar | h 4, fill `260 · frac` | `Gap 4` / row `Gap 8` | 2 | city 12/16/600, count 12/16 | track `FillSubtleSecondary`, fill `Tok.AccentDefault` | — | `NowPlayingPanel.cs:305-337` |
| credit row | — | `Gap 8`; section `Gap 8` | — | name 14/20/600, role 12/16 | linkable `Tok.AccentTextPrimary` else `TextPrimary`; role `TextTertiary` | — | `NowPlayingPanel.cs:357-376` |
| merch row | thumb 56² | `Pad 8`, `Gap 12` | 4 | name 14/20/600 MaxLines 2, price 12/16/600 | `FillCardSecondary` + 1-DIP stroke, hover `FillCardDefault`; price `AccentTextPrimary` | — | `NowPlayingPanel.cs:385-410` |
| NPV loading card | bars 18×180, 12×240, 12×210 | `Pad 12`, `Gap 8` | card 8, bars 4 | — | `FillCardSecondary` card, `FillSubtleSecondary` bars | — | `NowPlayingPanel.cs:467-477` |
| queue root | — | `Pad(14,4,14,0)`; body `Pad(0,0,0,14)` | — | — | — | `ScrollKey "queuepanel"`, `AutoEdgeFade` | `QueuePanel.cs:190-206` |
| queue pill | h 32 | `Pad(12,0,12,0)`, `Gap 6`; row `Gap 8`, `Pad(0,4,0,10)` | 16 (`Radii.Pill`) | glyph 12 / label 12/16/600 (∞ at 14/20/600) | off `Tok.FillCardSecondary` → `FillSubtleSecondary` → `FillSubtleTertiary`; on `accent` → `accent@0.88` → `accent@0.78`; ink on `Tok.TextOnAccentPrimary` | — | `QueuePanel.cs:815-860` |
| "Playing from" | MinHeight 28 | `Pad(8,2,8,8)`, `Gap 8` | 4 | 12/16, source at **Weight 700** | `TextSecondary` + `TextPrimary`; hover `WaveeColors.RowHover` | — | `QueuePanel.cs:339-357` |
| now-playing card | MinHeight 64, art 44 | `Pad 10`, `Gap 16`; `Margin bottom 10` | 8 (art 4) | title 14/20/600, artists 12/16 | `Tok.FillCardDefault` + 1-DIP `StrokeCardDefault`; title `AccentTextPrimary` | overlay FAB 28, `decodePx 96` | `QueuePanel.cs:361-425` |
| now-playing card (classic) | MinHeight 44 | `Pad(8,0,4,0)`, `Gap 8` | 0 | one 14/20 span run | transparent + a 1-DIP `Tok.StrokeDividerDefault` bottom hairline | — | `QueuePanel.cs:378-424`, `:799-805` |
| queue section header | h 36 (54 with hint) | `Pad(8,12,8,4)`, `Gap 2`; row MinHeight 20, `Gap 8` | — | `WaveeType.Eyebrow`; count 12/16/600; hint 12/16 | all `Tok.TextTertiary` | `BlocksDragArm`, `Layout = Slide` | `QueuePanel.cs:40-42`, `:428-469` |
| "Clear" pill | — | `Pad(8,2,8,2)` | 4 | 12/16/600 | `TextSecondary` → `TextPrimary`; hover `RowHover`, press `RowPressed` | — | `QueuePanel.cs:439-446` |
| queue row | MinHeight 44 (`RowExtent`), art 34 (`QueueArt`), heart slot 26, ⋯/✕ 32² | `Pad(8,0,4,0)`, `Gap 8` | 4 (0 classic) | title 14/20/600, artists 12/16 | transparent → `WaveeColors.RowHover` → `RowPressed`; now-playing title `AccentTextPrimary`; autoplay row `Opacity 0.72` | `PressScale 0.98` (`ScaleSubtle.Press`), `decodePx 72`, overlay FAB 26; heart slot + ⋯ + ✕ all `BlocksDragArm` | `QueuePanel.cs:33-34`, `:564-740` |
| queue row ⋯ | 32², glyph `Icons.More` **14** | centred | 4 (0 classic) | — | `TextTertiary` → `TextPrimary`; `HoverFill = WaveeColors.RowPressed` | wrapper `Opacity 0`, `HoverOpacity 1`; `ClickRequestsContext`; rendered only when a menu is attachable | `QueuePanel.cs:658-675` |
| queue row ✕ | 32², glyph `ChromeClose` **12** | centred | 4 (0 classic) | — | `TextTertiary` → `TextPrimary`; `HoverFill = WaveeColors.RowPressed` | **no hover-opacity — always painted**; replaced by a 32-wide spacer for a viewer or a row with no `ItemId` | `QueuePanel.cs:677-689` |
| queue classic hairline | 1 | — | — | — | `Tok.StrokeDividerDefault` (bound) | `AlignSelf End`, `JustifySelf Stretch`, `HitTestVisible = false`, `Key "classic-hairline"` | `QueuePanel.cs:799-805` |
| queue classic identity | — | row `Gap 8` | — | one 14/20 span run (title 600 + `"  ·  "` + artist links) | now-playing: whole run `AccentTextPrimary`; else title `TextPrimary`, rest `TextSecondary` | `TrackRow.ClassicExplicitBadge` pinned after the ellipsis; thin form = one 160×14 bar | `QueuePanel.cs:745-797` |
| queue thin-row skeleton | bars 120×14 + 80×11 | `Gap 4` | 4 | — | `Tok.FillSubtleSecondary` | — | `QueuePanel.cs:629-636` |
| "Show more" (rail) | h 40, extent 46 | `Margin(0,4,0,2)`, `Gap 8` | 4 | chevron 12; "Show next N" 12/16/600; "· N more" 12/16 | `Tok.FillCardSecondary`, hover `RowHover`, press `RowPressed`; `TextPrimary` / `TextTertiary` | `BlocksDragArm`, `Layout = Slide` | `QueuePanel.cs:43-44`, `:545-562` |
| friends row | MinHeight 56 (`WaveeSize.TrackRowH`), avatar 40, trailing 44 | `Pad(8,4,8,4)`, `Gap 12` | 4 | name 14/20/600, track 12/16, context 12/16, time 12/16/600 | `TextPrimary`/`TextSecondary`/`TextTertiary`; hover `RowHover` only when navigable | — | `FriendsPanel.cs:103-142` |
| presence dot | 12, ring 2 | bottom-right of the 40 avatar | circle | — | `Tok.AccentDefault` fill, `WaveeColors.FloatingPane` ring | — | `FriendsPanel.cs:160-167` |
| friends skeleton | avatar 40, bars 120×12 + 160×12 | `Pad(8,4,8,4)`, `Gap 12` / `Gap 4` | avatar circle, bars 4 | — | `Tok.FillSubtleSecondary` | 5 rows | `FriendsPanel.cs:197-213` |
| friends shell | — | `Pad(12,4,12,0)`; list `Pad(0,0,0,12)` | — | — | — | `ScrollKey "friendspanel"`, `AutoEdgeFade` | `FriendsPanel.cs:72-80`, `:222-227` |
| video-rail body | — | `Pad(12,8,12,12)`, `Gap 8` | — | title 20/28/600, meta 12/16, eyebrow 12/16/600 | `TextPrimary` / secondary / `TextTertiary` | `AutoEdgeFade` | `VideoRailPanel.cs:89-94` |
| no-video placeholder | aspect 16:9 | `Pad 8` | 8 | label 12/600 | `Tok.MediaLetterbox`; art at `Opacity 0.4`; label `Tok.TextOnAccentPrimary` | — | `VideoRailPanel.cs:113-133` |
| style flyout | w 340 | `Pad 12`, `Gap 12` | stock popup | title 14/20/600, labels 12/16 | `TextPrimary` / `TextSecondary` | `PopupChrome.Popup`, `FocusTrap`, `LightDismiss` | `PlayerStyleFlyout.cs:30`, `:66-70` |
| thumb card | 74.5 (`(340 − 24 − 18)/4`), art 66.5 | `Pad 4`, `Gap 4`; row `Gap 6` | 6 (art 4) | label 11/14 MaxLines 2 | `Tok.FillControlAltSecondary` → `…AltTertiary` → `…AltQuaternary`; ring `AccentDefault` 2 selected / `StrokeCardDefault` 1 | `Interaction.Card` press geometry, all three stroke legs re-stated | `PlayerStyleFlyout.cs:93-128` |
| swatch dot | outer 30, inner 22 | `Gap 8` | circle | — | inner = the catalog swatch or bound `HeroWashColor`; inner 1-DIP `StrokeCardDefault`; selected 2-DIP `AccentDefault` | `FocusVisualMargin −2` | `PlayerStyleFlyout.cs:160-195` |
| option Segmented | h 28, ItemMinWidth 56 | row `Gap 10`, label MinWidth 60 | 5 outer / 4 item | 12 | stock | — | `PlayerStyleFlyout.cs:37-40`, `:132-147` |
| mini-art plate | `size` square | — | 4 | — | `Tok.FillCardSecondary` or a literal gradient; every layer a literal hex | `HitTestVisible = false` throughout | `NpvThumbnails.cs:180-192` |
| **stage** top band | 88 wide / 56 compact | `Pad(16,16,16,12)`, `Gap 8` | — | — | no veil of its own | — | `StageChrome.cs:69-75`, `ImmersiveLyricsSurface.cs:372-381` |
| stage exit FAB | 44, glyph 18 | centred | circle | — | `StageInk.GlassPlate` 0.14 → 0.22 → 0.28; 1-DIP `StageInk.Stroke`; glyph `StageInk.Ink` | `Elevation.Card` (blur 8 y 2 `#00000033` dark / blur 4 y 2 `#0000001A` light) | `StageChrome.cs:200-226` |
| stage scrim FAB | 40, glyph 16 | centred | circle | — | `StageInk.ScrimRest/Hover/Pressed` (0.55 / 190 / 220 α over the veil); 1-DIP `Stroke`; latched glyph = accent | — | `StageChrome.cs:157-183` |
| stage glyph button | caller's box, caller's glyph (wide prev/next **17**, compact 15, wide "…" 16, mute 15) | centred | 4 | — | `GlassRest` (transparent) → `GlassHover` (Ink @ 0.10) → `GlassPressed` (Ink @ 0.16); ink `InkSecondary` → `Ink`; **disabled: `InkTertiary` both, plate frozen at `GlassRest`, no cursor, no scale** | `BrushTransitionMs 83`, `ScaleEmphatic` 1.07 / 0.92 | `StageChrome.cs:123-146`, `StageIdentity.cs:262-266`, `:381-386` |
| stage art | `L.ArtSize` square (300 wide / 64 compact) | wide `Margin bottom 18` | 8 wide / 4 compact | — | `Surfaces.Artwork` seeded off the uri | `Elevation.Dialog` wide / `Elevation.Card` compact; `decodePx` **512** wide / **192** compact; **not on the entrance cascade** | `StageIdentity.cs:169-177`, `:254-260` |
| stage satellite | 32, glyph 14 | centred | 4 | — | latched rest = `GlassHover`, latched hover = `GlassPressed`; latched glyph = accent | same | `StageChrome.cs:231-252` |
| stage play | 56 wide / 40 compact, glyph 22 / 17 | centred | circle | — | `StageInk.ButtonFill` (dark: `OnMediaPrimary`; light: `Ink`), hover/press `Darken/Lighten` 0.08 / 0.157; glyph `ButtonInk`. **Disabled: the plate falls back to `ScrimRest` and the glyph to `InkTertiary`** — it does not simply grey | same | `StageChrome.cs:258-278`, `StageArm.cs:103-114` |
| stage heart | 32, glyph 16 | centred | 4 | — | saved = accent, else `InkSecondary` → `Ink` | `BlocksDragArm`; `Key "sh:on"/"sh:off"` | `StageChrome.cs:282-312` |
| stage title | — | — | — | 22/28 **Weight 650**, `"Segoe UI Variable Display"` (compact 16/22) | `StageInk.Ink` | — | `StageIdentity.cs:43-47`, `:310-317` |
| stage meta link | — | — | — | 14/20/400 | `InkSecondary` → `Ink`, `Underline` on hover | — | `StageIdentity.cs:518-524` |
| stage quality badge | — | `Margin(8,0,8,0)` | — | **11.5**/16/600 | `InkTertiary` | renders nothing when no format or a remote device is active | `StageIdentity.cs:557-565` |
| stage device line | MinHeight 24, glyph 12 | `Pad(4,0,8,0)`, `Gap 8` | 4 | 12/16/400 | glass ramp; `InkTertiary` local / `InkSecondary` remote → `Ink` | picker `TopEdgeAlignedLeft`, `FocusTrap`, `LightDismiss`, `ConstrainToRootBounds = false`; glyph map Headphones/Headset → `Icons.Headphones`, Hdmi → `Icons.TvMonitor`, else `Icons.Speakers`, remote → `Icons.Devices`; remote LABEL is `player.playingOn` = "Playing on {device}", local is the endpoint name or `player.systemDefault` | `StageIdentity.cs:588-646` |
| stage volume | glyph 32 (icon 15), track **264** × 20 | `Gap 8` | stock | — | `RailFill` Ink@0.26, `ValueFill` Ink, thumb Ink, `ThumbBorder` = `StageInk.Stroke` | tooltip `"{0-100}%"` | `StageIdentity.cs:64`, `:404-452` |
| stage pivot link | — | `Pad(8,4,8,2)`; row `Gap 16` | 4 | 14/20/600 (`WaveeCta.TextAction*` rungs) | active `Ink`, inactive `InkTertiary`, hover `Ink`; underline `2` high, `4` gap, accent or transparent | `Role = Tab`, underline `BrushTransitionMs 167` | `StageChrome.cs:326-352` |
| stage section rule | 20 × 2 | — | — | — | accent | — | `StageChrome.cs:356-362` |
| stage queue row | MinHeight 56, art 38, grip 24, time 44, ✕ 32 | `Pad(8,0,8,0)`, `Gap 12` | 4 | title 14/20/600, artists 12/16, time 12/16 | glass ramp; `Ink` / `InkSecondary` / `InkTertiary`; autoplay `Opacity 0.68` | `PressScale 0.98`, `decodePx 96`, FAB 26 | `StagePanes.cs:162-165`, `:450-523` |
| stage autoplay row | MinHeight 48, ∞ slot 22 | `Pad(8,0,8,0)`, `Gap 12`, inner `Gap 2` | 4 | ∞ 17/22/600; label 13/18/600; hint 12/16 | ∞ on = accent, off = `InkTertiary`; label on = `Ink`, off = `InkSecondary`; hint `InkTertiary` | `Role = CheckBox` | `StagePanes.cs:341-376` |
| stage queue header | — | `Pad(0,0,0,12)`, `Gap 8` | — | "Playing next" 20/28/600; "from X" 12/16 | `Ink` / `InkTertiary` | — | `StagePanes.cs:302-337` |
| stage queue root | — | `Pad(24,24,24,0)`; body `Pad(0,0,0,72)` | — | — | — | `ScrollKey "stagequeue"`, `AutoEdgeFade` | `StagePanes.cs:271-297` |
| lyrics reading column | max 700 | `Pad(0,0,48,72)` | — | — | — | left-anchored, never centred | `StagePanes.cs:96-123`, `ImmersiveLyricsSurface.cs:53`, `:57` |
| lyrics column, **no document** (W16) | fills the column (`Grow 1, MinHeight 0`) | `Pad(24,0,24,0)` | — | 14/20, `Wrap` | `_ink.Secondary` — `LyricsInk.Media` on the stage, the theme ink in the rail | centred on BOTH axes; hardcoded `"No lyrics available"` (Ch.22 owns the string) | `LyricsView.cs:870-874`, `:2522-2527` |
| lyrics column, **pending** (W16) | shimmer bars | `RowGap` **18** large / 14 rail | 6 | `TextRatio 0.86` | `_ink.Skeleton` = `StageInk.SkeletonBar` on media, `Tok.FillSubtleSecondary` in the rail — ONE expression, no branch at the call site | `SkelReveal.FadeOnly`, `smoothResize: false` | `LyricsView.cs:868-876`, `LyricsInk.cs:69` |
| stage queue ✕ | 32², glyph 12 | centred | 4 | — | `StageChrome.Glyph` ramp | wrapper `Opacity 0`, `HoverOpacity 1`, `BlocksDragArm`; a 32-wide spacer for a viewer | `StagePanes.cs:512-521` |
| stage queue grip | 24 slot, glyph `Icons.GripperBar` 14 | — | — | — | `StageInk.InkTertiary` | `Opacity 0`, `HoverOpacity` = 1 **only when the list owns the drag**; `HitTestVisible = false` | `StagePanes.cs:476-481` |
| stage queue "Show more" | h 40, extent 46 | `Margin(0,4,0,2)`, `Gap 8` | 4 | chevron 12 `InkSecondary`; `"·  {n}"` 12/16 `InkTertiary` | glass ramp | `BlocksDragArm`, `Layout = Slide`, `Key "stagemore:"+tag` | `StagePanes.cs:536-551` |
| stage queue empty | — | `Pad top 24` | — | 14/20 | `StageInk.InkTertiary` | plain text, **not** `EmptyState` — `player.queueEmpty` = "Nothing up next" | `StagePanes.cs:261-269` |
| stage skeleton bar | — | — | — | — | `StageInk.SkeletonBar` = `Ink @ 0.12` (the lyrics shimmer's rung, in the stage's polarity) | — | `StageArm.cs:139-141` |
| video-rail empty "Up next" | — | `Gap 4`, `Pad 24` | — | 20/28/600 | `EmptyState.Compact(player.queueEmpty)` | the rule + eyebrow above it keep rendering | `VideoRailPanel.cs:79-80` |

---

## 4. Colour & material

### 4.1 The rail's material ladder — one coat per rung

| state | who paints what |
|---|---|
| docked, open | shell spacer's underlay → `WaveeColors.FileArea` (stock `LayerFillColorDefault`, translucent) over live Mica, corners `(8,0,0,0)`, `Margin = WaveeShell.StrokeOverhang`, **no stroke** (`WaveeShell.cs:1189-1201`). `RightRail`'s own surface → `ColorF.Transparent` (`RightRail.cs:96`). The ONE hairline is `RightRail.edge`. |
| docked, closing | `RailOpen` flips false, the spacer snaps to 0 in the commit frame and its coat vanishes instantly — so the rail's own `Fill` bind includes `RailOpen` and takes `FileArea` back for the 300 ms slide-out. Without it the slide is raw text over the expanding page (`RightRail.cs:92-96`). |
| floating | overlay backing → `WaveeColors.FloatingChrome` (= `ShellGround`); rail surface → `FileArea` on top; uniform ring + `Elevation.Flyout` (`WaveeShell.cs:1284-1291`, `RightRail.cs:150-154`). Deliberately **not** `FloatingPane`: pane-then-surface was a double coat that made the floating rail one rung darker than docked. |

### 4.2 The hero wash — one function, three surfaces

```
url → SpotifyLive.CoverColorPlane.Current.Watch(url)          (subscribe INSIDE the Prop.Of thunk)
    → Surfaces.SchemeFor(url)          follows the ACTIVE theme       Surfaces.cs:141-142
    → WaveePalette.Accent(scheme)      most-saturated of 4 roles      WaveePalette.cs:151-166
    → WaveePalette.Lift(·, 210)        raise max channel to 210/255   WaveePalette.cs:25-33
    → ColorF.Lerp(Tok.FillCardSecondary, accent, dark ? 0.18 : 0.10)  NowPlayingPanel.cs:203-210
```
Applied at **three** places and they must never disagree: the cover `ImageEl.Placeholder` (`NowPlayingPanel.cs:134`),
the deck faces' `DeckArt` (Ch.23), and the flyout's "Album colour" swatch (`PlayerStyleFlyout.cs:173`). All three take
it as a **bound** `Prop.Of` so a late grading repaints without re-rendering the panel or the flyout. No grading yet ⇒
`Tok.AccentDefault`.

### 4.3 The queue's chrome accent

`Surfaces.ChromeSchemeFor(coverUrl)` — the **opposite** theme's grading on purpose — then `WaveePalette.ChromeAccent`
(= `Lift(Accent(s))`, then `Vivid(...)` unless HSV saturation ≤ `NeutralS = 0.08`, in which case `Tok.AccentDefault`)
(`QueuePanel.cs:114-116`, `Surfaces.cs:149-156`, `WaveePalette.cs:126-131`). This is the **filled** role: the pills are
accent plates carrying `Tok.TextOnAccentPrimary` ink, and the raw grading role can be deliberately dark enough to make
an enabled pill read disabled. Hover/press are the same colour at `A = 0.88` / `0.78` — an alpha step, not a second hue.

### 4.4 The stage's two-arm ink

`StageInk.Live = StageArm.For(Tok.Theme)` is the **only** theme branch on the whole surface (`StageInk.cs:17`).

| rung | dark arm | light arm |
|---|---|---|
| `Veil` (what every scrim alpha mixes from, and the opaque floor) | `Tok.MediaStage` `#0A0A0A` | `WaveePalette.PageToneNeutralLight` `#F5F5F5` |
| `Ink` / `InkSecondary` / `InkTertiary` | `WaveeOnMedia.Ink` = white 1.0 / 0.80 / 0.60 | `Tok.MediaStage` at the **same** alphas |
| `GlassRest/Hover/Pressed` | transparent / white @ 0.10 / white @ 0.16 | transparent / `Ink` @ 0.10 / `Ink` @ 0.16 |
| `GlassPlate/Hover/Pressed` | white @ 0.14 / 0.22 / 0.28 | `Ink` @ the same |
| `ScrimRest/Hover/Pressed` | black @ 0.55 / 190/255 / 220/255 | `Veil` @ the same alphas |
| `Stroke` | white @ 58/255 | `Ink` @ 58/255 |
| `ButtonFill` (play) | `OnMediaPrimary` (white), `Darken 0.08 / 0.157` | `Ink` (near-black), `Lighten 0.08 / 0.157` |
| `ButtonInk` (the glyph on it) | `Tok.MediaStage` | `Veil` |
| `Accent` | `WaveePalette.ChromeAccent(scheme)` | `WaveePalette.TextInk(chrome, Light, AccentGround)` where `AccentGround = ColorContrast.Over(Veil @ 0.46, Tok.OnMediaPrimary)` |
| `SkeletonBar` (the lyrics shimmer) | `Ink @ 0.12` | `Ink @ 0.12` — mirrored ground, same alpha (`StageArm.cs:139-141`) |
| `ArtStandIn(url)` (backdrop placeholder) | `Surfaces.PlaceholderFor(url, light: false)` | `…(url, light: true)` — the cover TINT survives, only the neutral flips (`StageInk.cs:79`) |
| `IsDark` | true | false — the ONE polarity read renderers may take, and only for the lyrics bloom's direction/weight (`StageInk.cs:21`) |

The dark arm is `WaveeOnMedia` **verbatim**, test-pinned so "dark theme is byte-identical to what shipped" is
executable (`StageArm.cs:20-25`, `StageLayoutTests.TheStageInkDarkArm_IsWaveeOnMediaVerbatim`). The light arm reads
every alpha off its dark twin rather than restating it.

### 4.5 The stage's scrim system — exact stops

```
Scrim()          GradientDown                                    StageChrome.cs:85-89
  0.00  Veil @ 0.76     ScrimTopA
  0.22  Veil @ 0.46     ScrimTopStop  →  ScrimBaseA       ┐ the two interior stops carry the SAME value:
  0.62  Veil @ 0.46     ScrimBottomStop → ScrimBaseA      ┘ that is what makes the middle a PLATEAU
  1.00  Veil @ 0.70     ScrimBottomA
  On a 780-DIP body the top feather is 172 DIP long and the bottom one 296 — never a boxed band.

ColumnShade()    GradientRight, layer width 612 = 352 + 260      StageChrome.cs:95-100
  0.0000  Veil @ 0.26          ColumnShadeA
  0.5752  Veil @ 0.26          = 352/612, the designed column's real edge
  0.8088  Veil @ 0.0884        = hold + 0.55·(1−hold); alpha = 0.26 · 0.34   ← a CURVE, not a Mach band
  1.0000  Veil @ 0.00          exactly zero — the edge-invisibility rule
  Width is BOUND (0 in the compact shape) so the flip re-solves without re-rendering the surface.

PaneShade()      GradientRight, the queue pane only             StageChrome.cs:106-108
  0.00  Veil @ 0.00
  0.22  Veil @ 0.096          = PaneShadeA · 0.4
  1.00  Veil @ 0.24           deep end = the WINDOW edge (nothing outside to contrast with)
  Mounted and cross-faded WITH the pane, because the queue's hover glass needs a floor while it is up.
```
Contrast, at the plateau, against each arm's worst cover (the comment block these numbers are asserted against —
`StageLayout.cs:186-213`):

| rung | dark arm, near-white cover | light arm, near-black cover |
|---|---|---|
| primary ink | 3.57 : 1 | 4.29 : 1 |
| secondary | 2.89 : 1 | 3.55 : 1 |
| under the column shade | 5.70 : 1 | 6.70 : 1 |

The light arm clears a **higher** ratio at every rung, so one alpha ladder is correct for both. The sanctioned
addition, if tuning ever disagrees, is a `ScrimBaseLightA` beside `ScrimBaseA` — **alphas may live in `StageLayout`,
colours may not**.

### 4.6 The backdrop (host-owned, Ch.22 co-owns)

Floor `StageInk.Floor` → cover at `BakedBlurSpec(σ 80 DIP, resolution scale 0.5)` (baked once per art change, not a
per-frame Gaussian) inside a **1.30× paint scale about the box centre** (a `Transform`, never a `Width` — declaring the
overscale as geometry once climbed the ZStacks and arranged a 1534×952 root inside a 1180×760 window)
(`ImmersiveLyricsSurface.cs:62-67`, `:441-478`). The cover fades in over 220 ms (`ImageTransition.Fade(220f)`).

### 4.7 Other derivations

- **Docked cap letterbox**: `Tok.MediaLetterbox` = opaque `#000000`. The cap's bars are black, never a theme fill.
- **About-artist scrim**: `GradientDown` over the header band, `0 → rgba(0,0,0,0)`, `0.55 → 0.12`, `1.0 → 0.62`, plus
  `EdgeFade(EdgeMask.Bottom, 56)` on the band itself (`NowPlayingPanel.cs:241-259`).
- **Friends presence ring**: 2-DIP `WaveeColors.FloatingPane` (= `ContentSurface`, the opaque content rung) so the dot
  reads as a hole in the avatar whether the rail is docked or floating (`FriendsPanel.cs:162-167`).
- **Light/dark for the rail**: none of the rail's own colours branch on theme; every one is a `Tok.*` /
  `WaveeColors.*` token that re-resolves through `Tok.Epoch` + `Reconciler.RethemeAll()`.

---

## 5. Motion

Every animation below samples the engine present clock (`FrameTime` / `AnimScheduler`); the two intervals
(`UseInterval` at 100 ms and 30 s) are frame-clock driven and auto-pause while the window is parked/minimised.
**The one exception is documented at the end of this table.**

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| rail open | rail host node | `TranslateX` | presented `Dx` → 0 | 300 ms | `CubicBezier(0, 0.35, 0.15, 1)` | none | engine policy on the anim channel | `RightRail.cs:76-77` |
| rail close | rail host node | `TranslateX` | presented `Dx` → `railWidth` | 300 ms | same | none | same | same |
| first mount | rail host node | `TranslateX` | `x → x` | **1 ms**, `Easing.Linear` | — | — | — | `RightRail.cs:63-67` (seeds closed off-canvas with no startup fly-in) |
| rail open (shell side) | reservation spacer | `Width` | 0 ↔ `RailWidth` | **snap** (`RailSpacerAnim = null`) | — | — | — | `WaveeShell.cs:210-212`, `:1169-1170` |
| rail open (shell side) | content card | FLIP | absorbs the reserved shift | `SidebarReveal` | — | — | — | `WaveeShell.cs:1152-1157` |
| rail splitter drag | `RailWidth` | width | live | none (direct) | — | — | — | `WaveeShell.cs:1248-1255` |
| rail splitter drag | hero tile (Cover **or** deck) | **remount** | old side → new side | once per **4-DIP** quantum of `railW` — the key is `"<slug>@"+(int)side`, so the drag re-keys ~75 times over a full 200→500 sweep and each remount is a cold deck | — | — | — | `NowPlayingPanel.cs:541-547` (W7b) |
| cap splitter drag | `DockedVideoHeight` | height | live, clamped `[9/16·railW, 560]` | none | — | — | — | `RightRail.cs:246-252` |
| peek line advance | active slot inner box | `Dy` + `Opacity` | enter `+56, 0 → 0, 1`; exit `0, 1 → −56, 0` | `MotionTok.ControlFast` = **150 ms** | `Easing.FluentStandard` | none | `ReducedMotionPolicy.KeepFade` (the fade survives, the slide does not) | `NpvLyricsPeek.cs:24-25`, `:155-156` |
| peek clock | `_pair` signal | index pair | — | `UseInterval` **100 ms** | — | — | interval unaffected | `NpvLyricsPeek.cs:23`, `:63` |
| queue row insert | row | `Dy` + `Opacity` | `+6, 0 → 0, 1` | engine default enter | — | none | engine policy | `QueuePanel.cs:712` |
| queue row remove | row | `Dy` + `Opacity` | `0, 1 → −4, 0` | engine default exit | — | none | engine policy | `QueuePanel.cs:713` |
| queue row shift | row / header / more-row | FLIP | old rect → new rect | `LayoutTransition.Slide` | — | none | engine policy | `QueuePanel.cs:714`, `:463`, `:555` |
| track change | now-playing card | remount + `Dy`/`Opacity` | `+6, 0 → 0, 1` | engine default enter | — | none | engine policy | `QueuePanel.cs:414`, `:421` — the `Key` is `"np:"+uri+":classic="+classic`, so a track change **cross-fades the card** |
| queue row hover | row plate | `Fill` | transparent → `WaveeColors.RowHover` | brush fade (engine default) | — | none | — | `QueuePanel.cs:706` |
| queue row press | row | `Scale` | 1 → **0.98** (`ScaleSubtle.Press`) | interaction | — | — | reduced-motion safe via the tier | `QueuePanel.cs:708` |
| queue row hover | **⋯ only** | `Opacity` | 0 → 1 (`HoverOpacity`) | engine hover fade | — | — | — | `QueuePanel.cs:661` |
| — | the rail's ✕ | — | **no opacity track** — it is painted at rest and only its plate/ink react to hover | — | — | — | — | `QueuePanel.cs:677-689` |
| peek seed | `_pair` | index pair | — | one `UseLayoutEffect` on `show`, so the first line is right without waiting a tick | — | — | — | `NpvLyricsPeek.cs:64` |
| mid-drag | neighbours (rows AND headers) | FLIP via the live projection | — | reorder default | — | — | — | `QueuePanel.cs:471-476`, `:485-486` |
| drag lift | source row | `Opacity` | 1 → **0.40** (`Drag.SourceDimOpacity`), `DragLift.Stationary` | — | — | — | — | `QueuePanel.cs:65` |
| friends push | row | `Dy` + `Opacity` | enter `+6, 0`; exit `−4, 0`; `Layout = Slide` | engine defaults | — | none | engine policy | `FriendsPanel.cs:119-121` |
| friends live | equalizer | bar `ScaleY` | `WaveeEqualizer` ticker, height 14 | — | — | — | `EqualizerMotionPolicy` | `FriendsPanel.cs:136` |
| friends tick | `NowTick` | long | +1 | `UseInterval` **30 s** | — | — | auto-pauses while parked | `FriendsPanel.cs:42` |
| stage button hover | any `StageChrome` box | `Scale` + `Fill` | 1 → **1.07**, glass rest → hover | **83 ms** (`WaveeMotion.Faster`) | `MotionTokenId.ControlFaster` / `Easing.FluentStandard` | none | tier-safe | `StageChrome.cs:132-133`, `:171`, `:214` |
| stage button press | same | `Scale` + `Fill` | 1 → **0.92** | 83 ms | same | none | tier-safe | same |
| stage pane switch | `pane:lyrics` / `pane:queue` | `Opacity` | 1 ↔ 0 | `MotionTok.ControlNormal` = **250 ms** | `Easing.FluentStandard` | none | `KeepFade` (the token's own policy — never a branch) | `StagePanes.cs:61-70` |
| stage pivot switch | underline | `Fill` | transparent ↔ accent | **167 ms** (`WaveeMotion.Fast`) | brush fade | none | — | `StageChrome.cs:347` |
| stage open (wide) | the 5 column rows | `Opacity` (+ `Dy 8`, `Blur 2` on enter) | 0 → 1 | `Expressive.Slow` = **400 ms** | `Easing.SmoothOut` | **0 / 40 / 80 / 120 / 160 ms** (`WaveeEntrance.Row(i)`, `StaggerMs 40`, cap 8) | `DelayMs` returns 0 under reduced motion | `StageIdentity.cs:184-213`, `WaveeMotion.cs:102-127` |
| stage open (compact) | header row, seek block | same | 0 → 1 | 400 ms | `SmoothOut` | 0 / 40 ms | same | `StageIdentity.cs:284`, `:290` |
| **the cover is NOT on the cascade** | `stage:art` | — | — | — | — | — | — | `StageIdentity.cs:163-166` — a 300-square that rises and fades reads as the whole surface arriving late |
| stage surface enter / exit | the whole surface | `Scale` + `Opacity` | `1.03 → 1` / `1 → 1.02` | host-owned | — | — | reduced motion: **fade only, no scale** | `ImmersiveLyricsSurface.cs:97-105` |
| stage backdrop drift | the drift carrier node | `LocalTransform` | `±4%` translate, `±2%` scale, two sinusoids at **37 s / 53 s** | continuous, `UseInterval` 33 ms | sinusoidal | — | vetoed by `Motion.ReducedMotion` AND by `WaveeSettings.LyricsAnimatedBackdrop` — OFF means **no ticker at all** | `ImmersiveLyricsSurface.cs:73-80`, `:139`, `:165` |
| stage backdrop cover swap | the blurred image | `Opacity` | fade | **220 ms** | `ImageTransition.Fade` | — | — | `ImmersiveLyricsSurface.cs:425` |
| stage heart toggle | glyph box | remount by `Key` `"sh:on"/"sh:off"` | — | — | — | — | — | `StageChrome.cs:300` |
| stage queue row | same set as the rail's | — | — | — | — | — | — | `StagePanes.cs:465-471` |
| stage grip reveal | grip box | `Opacity` | 0 → 1 on ROW hover (0 → 0 for a viewer: `HoverOpacity = gripped ? 1 : 0`) | engine hover fade | — | — | — | `StagePanes.cs:404`, `:476-481` |
| stage queue ✕ reveal | ✕ wrapper | `Opacity` | 0 → 1 on ROW hover | engine hover fade | — | — | — | `StagePanes.cs:512-521` |
| stage entrance cascade cap | rungs past index 8 | `DelayMs` | all share index 8's 320 ms | — | — | `StaggerCap = 8` ⇒ the whole cascade is bounded at 360 ms | 0 under reduced motion | `WaveeMotion.cs:105-122` |

**The one clock exception.** `NpvLyricsPeek.Tick` reads `_b.PositionMs.Peek()` — the playback bridge's position
signal, which is itself advanced from timestamped `PositionSamples` and not from `Environment.TickCount64`
(`NpvLyricsPeek.cs:143`). `ImmersiveLyricsSurface.DriftTick` uses `Stopwatch.GetTimestamp()` explicitly, with the
comment "never TickCount64, which quantises to ~15.6 ms" (`:508`, and the comment on the `_driftOriginQpc` field at
`:91`). Neither reads `TickCount64`. **Port both exactly; do not "simplify" either to a wall clock.**

One genuine wall-clock read remains, and it is correct: `FriendsPanel` takes
`DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` once per render to age the feed's server timestamps
(`FriendsPanel.cs:49`). That is a DATE comparison against a server epoch, not an animation clock.

---

## 6. Interaction

### 6.1 Rail frame

| gesture | result |
|---|---|
| player-bar Lyrics / Queue / Now-playing buttons | `ShellUi.Toggle(mode)` — clicking the already-showing mode **closes** the rail (`ShellUi.cs:80-85`, `PlayerBar.cs:459`, `:597`, `:603`) |
| the toolbar's friends button | `MergedChromeRow.ToggleFriends()` → `Toggle(RailMode.Friends)` (`MergedChromeRow.cs:192`) |
| header ✕ | `RailOpen = false` for **every** mode, video included. The rail's close is what demotes a docked video to Floating (`RailVideoCoupling.OnRailClosed`), so ✕ never means "stop the video" (`RightRail.cs:118`, `:259-262`) |
| drag the left seam | rail splitter, `Min 200 / Max 500`, leading polarity; commit persists `WaveeSettings.ShellRailWidth` (`WaveeShell.cs:453-458`, `:1248-1255`). The detent below the minimum (resist → fade → close at raw ≤ 156, re-open past 210) is 18 W12b; every width-derived number between the two stops is **W7b** |
| drag the seam WIDER than the window can reserve | `RailFits` is recomputed from the live `RailWidth`, so the rail flips docked→floating **mid-drag** — at vpW 1060 with a 240 sidebar that is any width above 340 (`WaveeShell.cs:696-701`). Nothing closes and nothing re-flows the page: the spacer drops to 0 and the same panel is now the overlay of W8 |
| drag the cap's bottom 16 DIP | vertical splitter over the docked video; commit sets `DockedVideoHeightPinned = true` and persists `ShellDockedVideoHeight`. `HitTestPassThrough` so the video's own chrome keeps working (`RightRail.cs:218-256`) |
| tooltips | pop-out `Strings.Player.VideoMiniPlayer` = "Play in a mini player"; fullscreen `Strings.Player.VideoFullScreen` = "Full screen"; expand lyrics `Strings.Player.ExpandLyrics` = "Expand lyrics"; the secondary-line toggle `LyricsPrefs.Tooltip(mode)` (Ch.22). The ✕ has **no** tooltip (a close glyph is universal) |
| announcements | both video header buttons call `Announcer.Say(...)` with the same string as the tooltip (`RightRail.cs:270`, `:275`) |

### 6.2 Now-playing panel

- **Artist names** and **album name** in the hero meta are `SpanTextEl` links → `go("artist:"+uri, name)` /
  `go("album:"+uri, name)`. Without a nav context they degrade to plain `TextEl` (`NowPlayingPanel.cs:150-161`,
  `:212-227`).
- **The whole About-artist card** is clickable → the artist page; `HoverFill` = `Tok.FillCardDefault`, `Cursor.Hand`.
  With no nav context it is inert **and** keeps `FillCardSecondary` on hover (`NowPlayingPanel.cs:294-296`).
- **A linkable credit name** navigates to the artist; non-linkable credits render in `TextPrimary` and do nothing
  (`NowPlayingPanel.cs:357-364`).
- **A merch row** opens `item.ShopUrl` through `InputHooks.Current.Default.OpenUri` — an **external** browser hand-off.
  With no `ShopUrl` the row is inert: no `OnClick`, no `Cursor.Hand` — but it keeps its `HoverFill`. Its price line
  falls back to `Strings.Artist.Buy` = "Buy" when the price string is empty (`NowPlayingPanel.cs:385-406`).
- **The SaveButton** is absent entirely when there is no `LibraryBridge` (`:192`).
- **"Next up" rows** are `TrackRow.ArtCard(kind: Rail)` — Ch.01 owns their menus and hover.
- **The lyrics peek** is `Role = Button`, `Focusable`, `Cursor.Hand` → `ui.Toggle(RailMode.Lyrics)`. It is the same
  target in both the reel and the unsynced-note form (`NpvLyricsPeek.cs:80-83`, `:112-116`, `:133`).

### 6.3 Cover | Player

| gesture | result |
|---|---|
| click a SelectorBar item | the controlled index moves in the **same frame as the press** (the bar writes it before `OnChange`), then `NpvPlayerPrefs.SetPresentation(settings, i, "header")` persists + bumps `Epoch` (`NpvHeaderRow.cs:60-66`, `:92-94`) |
| click the gear | `StyleFlyoutOpen = !Peek()`; the gear lights `Tok.AccentTextPrimary` while the flyout is up **wherever it was opened from**. It is a `RightRail.HeaderButton`, so it carries the tooltip `player.playerStyle` = "Player style" (`NpvHeaderRow.cs:68-77`) |
| Escape / light-dismiss the flyout | closes, restores focus to the gear, logs `npv flyout.close` (`NpvHeaderRow.cs:104`, `NpvDiagnostics.cs:14`) |
| right-click the hero (cover **or** deck) | `NpvArtMenu.Model` — the wrapper owns the menu for both occupants, so the target is identical in either presentation (`NowPlayingPanel.cs:552-554`) |
| pick a thumbnail card | `SetStyle(settings, id, "flyout")` — and picking a player **also shows it**: a style chosen while the cover was up flips presentation to Player (`NpvPlayerPrefs.cs:34-41`) |
| pick a swatch / segment | `SetChoice(...)`; the flyout stays open and the deck restyles in place (no remount — only the **preset** is in the `Key`) |
| keyboard | thumbnail cards are `Role = Button, Focusable`, `FocusVisualMargin −2`; swatches are `Role = RadioButton, Focusable`, same focus inset (`PlayerStyleFlyout.cs:100-101`, `:181-182`) |
| swatch tooltip | `Loc.Get(choice.LabelKey)` — e.g. `player.choice.albumColour` = "Album colour" (`PlayerStyleFlyout.cs:192`) |
| Art\|Video tooltips | each half names the action **it** performs, unconditionally: `player.switchToAudio` = "Switch to audio", `player.switchToVideo` = "Switch to video" (`RightRail.cs:337-340`) |

**Art menu, exact order** (`PlayerStyleFlyout.cs:216-231`):
1. `Icons.Picture` — `player.presentationCover` = "Cover" — **RadioItem**, checked when presentation == Cover
2. `Icons.Album` — `Loc.Get(preset.ShortLabelKey)` — **RadioItem**, checked when presentation == Player
3. separator
4. `Icons.Settings` — `player.playerStyleEllipsis` = "Player style…" → `StyleFlyoutOpen = true`

Radio, not toggle: the two are one mutually exclusive choice (WinUI E915).

### 6.4 Queue

| gesture | result |
|---|---|
| click a row | `SkipToQueueItemAsync(entry.ItemId)` — a **cursor move** inside the live session, never a rebuild. `PlayTrackAsync` only when `ItemId.IsNone` (`QueuePanel.cs:891-899`) |
| click the row's cover FAB | same target (`NowPlayingOverlay.Create(..., () => PlayQueueEntry(b, entry), 26f, cover: true, 34f, centered: true)`) |
| click the now-playing card's overlay | play/pause toggle (`NowPlayingOverlay` with a no-op `onPlay`, `QueuePanel.cs:374`) |
| click ✕ | `RemoveQueueItemAsync` + optimistic `QueueOrder.Remove`. Hidden (a 32-wide spacer) for a viewer or a row with no `ItemId` (`QueuePanel.cs:578-582`, `:677-689`) |
| click ⋯ | `ClickRequestsContext = true` — re-enters the context funnel so the row's own menu opens **byte-identically** to a right-click, anchored at the button (`QueuePanel.cs:671`) |
| right-click / Menu key | `Menus.QueueEntry` (below) |
| swipe LEFT (touch) | remove (destructive) — `TrackActions.RemoveFromQueue`, reusing the same `Remove()` closure |
| swipe RIGHT (touch) | like — `TrackActions.ToggleLike`. One `SwipeGroup` per panel; scrolling closes any open swipe (`QueuePanel.cs:202`, `:731-738`) |
| drag a row | `Drag.Source(WaveeDragKinds.Resource, ForQueueRow(entry))` when the `Reorderable` is not owning it (a viewer); otherwise the list owns the source. Either way the payload is the **queue row** — `CanCopyTracks` is false, so no playlist surface may take it as a copy (`QueuePanel.cs:697-701`, `:317-319`) |
| drop inside its own section | `PlaybackSession.MoveItem` via `MoveQueueItemAsync` + optimistic `QueueOrder.Move` |
| drop in another section | refused with an Informational toast (`drag.cantMoveAcrossSections`) |
| drop outside the list | cancel — `RequireDropOnList = true` |
| drop a foreign payload | inserted into the **user queue** at `QueueMovePlan.InsertIndex(slots, slot)`; success toast `Strings.Detail.AddedToQueue(n)` or `AddedFirstToQueue(n)` when the batch cap trimmed it (`QueuePanel.cs:315-336`) |
| click "Clear" | `ClearQueueAsync()` + drop every `UserQueue` row from the display mirror. Rendered only when `!viewer` (`QueuePanel.cs:496`, `:809-813`) |
| click "Show next N" | `pages.Value += 1` — one more 100-row page realized. The underlying queue is never truncated (`QueuePanel.cs:503-509`) |
| click a pill | shuffle / repeat cycle (Ch.20's `PlayerBarContent.ToggleShuffle` / `CycleRepeat`); autoplay writes `WaveeSettings.AutoplayEnabled` and bumps `PlaybackPrefs` — the same seam the Settings toggle and the stage's ∞ row drive (`QueuePanel.cs:104-109`, `:821-826`) |
| click "Playing from X" | `go(RichText.RouteForUri(ctxUri), source)`; inert (and un-hovered) when the uri has no route (`QueuePanel.cs:345-347`) |
| keyboard | rows are `Focusable`, `Role = Button`; the reorderable supports a **keyboard lift** which `ReleaseStrandedLift` deliberately leaves alone (`QueuePanel.cs:253-258`) |
| scroll while a swipe is open | `OnScrollGeometryChanged` closes the whole `SwipeGroup` — one group per panel (`QueuePanel.cs:202`) |
| settings that change what a row draws | `WaveeSettings.TrackRowStyle == 1` (Classic) and `AppearancePrefs.TrackArtworkHidden` are **independent**; Classic suppresses artwork on its own, so `showTrackArtwork = !classic && !artworkHidden`. Both are Epoch-live and both appear in the row's `Key`, so a flip remounts rather than re-binds (`QueuePanel.cs:141-145`, `:695`) |

**`Menus.QueueEntry`, exact composition** (`Actions/Menus.cs:1077-1108`):
- header: cover thumb + track title + `"{artists} · {section}"`, where section ∈ `"Next in queue"` / `"Autoplay"` /
  `"Next up"` (derived from `entry.Provider` — note these three are **hardcoded English**, see §9)
- primary command strip, in order: **Play now** (`Icons.Play`, `menu.playNow`) · Play next · Add to queue · Like
- rows: `TrackRows(showGoToAlbum: true, extras)` = Add to playlist ▸ · Go to album · Go to artist(s) · Share ▸ ·
  View credits · Go to song radio, with the queue extras spliced in: **Move up** (`Icons.ChevronUp`, `menu.moveUp`) and
  **Move down** (`Icons.ChevronDown`, `menu.moveDown`), each present only when the move is legal
  (`canMove && index > 0` / `canMove && index + 1 < count`), then the destructive **Remove from queue**
  (`menu.removeFromQueue`), added by `TrackRows` itself for a `QueueEntry` target with a remove closure.

### 6.5 Friends

- A row navigates to `RichText.RouteForUri(ContextUri) ?? RouteForUri(AlbumUri) ?? RouteForUri(ArtistUri)`, labelled
  `ContextName ?? AlbumName ?? ArtistName`. A row with **no** route is inert: `Role = None`, `Cursor.Arrow`, no hover
  plate, no press scale, not focusable (`FriendsPanel.cs:87-118`).
- No context menu in this cut. No selection. No drag.
- The error state's only control is `Button.Standard(Loc.Get(Strings.Friends.Retry), fb.Refresh)` — a stock Standard
  button, **never** an accent one (`FriendsPanel.cs:60-61`, `EmptyState.cs:57-62`).

### 6.6 Stage

| gesture | result |
|---|---|
| Escape | closes the surface. The root re-parks focus on itself whenever focus would otherwise go null, because an unhandled Escape clears focus and disarms the keyboard exit (`ImmersiveLyricsSurface.cs:185-205`) |
| click the ⌄ disc | closes. Tooltip `player.closeLyricsHint` = "Close lyrics (Esc)" — **the tooltip is what teaches the keyboard half** |
| right-click / Menu key anywhere on the identity region | `Menus.NowPlaying(track)` — the same menu the player bar's cluster raises; the factory `Peek`s at open time so it never serves a render-time capture (`StageIdentity.cs:104-106`) |
| click the wide "…" | `ClickRequestsContext = true` → the same menu via the ancestor's `WithContextMenu` |
| click the compact "…" | the **folded-controls** menu, built at open time (W13) |
| click "Artist — Album" | `PlayableLinks.RouteFor(track, LinkSlot.Artist)` resolved **at invoke time** (this node outlives every track change). Hover underlines only this word, never the row (`StageIdentity.cs:496-524`) |
| click the device line | `DevicePickerMenu` at `TopEdgeAlignedLeft`, `FocusTrap`, `LightDismiss`, `ConstrainToRootBounds = false` |
| click a pivot link | `StagePane.Current = Lyrics | Queue`. `Role = Tab` |
| drag the volume thumb | `SetVolumeAsync`; thumb tooltip `"{0-100}%"` |
| stage queue row | identical verbs to the rail's, plus a **drag grip** (`Icons.GripperBar`, 14, `InkTertiary`) revealed on row hover — an affordance, not a control: `HitTestVisible = false`, the whole row is the drag source (`StagePanes.cs:474-481`) |
| stage ∞ row | `Role = CheckBox`, toggles `AutoplayEnabled` + `PlaybackPrefs.Bump()` |
| transport tooltips | `player.shuffle` / `player.previous` / `player.next` / `player.repeat`; the mute glyph takes `player.mute` / `player.unmute`; the compact "…" takes `player.nowPlaying` |
| accessibility names | every `StageChrome` button sets `Role = AutomationRole.Button` + `AllowFocusOnInteraction = false` (so a click does not steal the focus ring); the pivot links are `Role = Tab`; the ∞ row is `Role = CheckBox` |

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 source | 0.3 read | readiness predicate |
|---|---|---|---|
| rail open / mode / width / fits / immersive | `ShellUi` signals (`ShellUi.cs`) | `Shell.RailOpen/RailMode/RailWidth/RailFits/Immersive` (`Shell/Shell.cs` CORE + `Shell.Host.cs`) | always ready (chrome state) |
| rail title | `RailMode` → `Loc` | same | always |
| hero cover | `b.CurrentTrack.Value.Image.Url` + `BlurHash` | `Playback.Current` → `Track.Image` (`StringId`), `TrackFields.Image` | `t.Knows(TrackFields.Image)`; until then the **wash** placeholder paints (never a grey hole) |
| hero title / artists / album | `Track.Title`, `Artists`, `Album` | `Track.Title`, `Edges.TrackArtists.Targets(slot)`, `Track.Album` | `t.Knows(TrackFields.Identity)` — **the whole hero block is skeleton until Identity is complete**, never title-then-artist |
| save heart | `LibraryBridge.IsSaved(uri)` | `User.Me.Likes(t)` (`Edges.Liked.Contains`, plan §4.14) | the Liked edge's `State == complete` for `User.Me`; a pending bit renders the optimistic state |
| lyrics peek | `svc.Lyrics.GetLyricsAsync(trackId)` → `LyricsDocument` | `Lyrics.For(track)` (`Shell/Lyrics.cs`, Ch.22) | `LyricsPeekClock.ShouldShow(doc)` — line- or syllable-synced with ≥ 1 line; otherwise the peek renders **nothing at all** (not an empty box) |
| peek suppressed by video | `LyricsSyncGate.SyncSuppressed(b.VideoActive())` | `Playback.VideoActive` | renders the one-line note, and **stops the 100 ms clock** (a render-time branch over a live ticker would keep waking the panel) |
| About-the-artist block | `svc.AlbumEnrichment.GetNowPlayingInfoAsync(artistUri, trackUri)` → `NowPlayingInfo.About : Artist` | **DATA GAP** (below) | whole-section: `LoadingSection()` (3 shimmer bars) while pending, **nothing** on failure |
| top cities | `Artist.Extras.TopCities : IReadOnlyList<TopCity>` | **DATA GAP** | section renders only when `Count > 0` |
| credits | `svc.TrackCredits.GetAsync(trackUri)` → `TrackCredits`, falling back to `NowPlayingInfo.Track.Credits` (capped at 10) | **DATA GAP** | section renders only when `Credits.Count > 0`; the **uncapped** drawer wins when present |
| merch | `TrackNpvInfo.Merch` ?? `Artist.Extras.Merch` | **DATA GAP** | `Count > 0` |
| "Next up" (NPV) | `b.Queue.Value.Where(Bucket is UserQueue or NextUp).Take(5)` | `Edges.Queue.Payload(session)` filtered on `QueueEdge.Bucket` | `Count > 0` |
| queue buckets | `QueueEntry.Bucket` + `Provider`, minus the currently playing uri | `QueueEdge.Bucket` / `QueueEdge.Provider`; targets are track slots | the edge's `State` must be ≥ partial; rows whose track lacks `TrackFields.Identity` render the **thin-row skeleton** (two bars), never a bare `spotify:track:…` uri |
| queue row identity | `HydrationLevels.TitleMissing(t.Title, t.Uri)` | `!t.Knows(TrackFields.Title)` — one bit, no string probe | see above |
| queue row stable key | `QueueItemId` else `EntryId` | `QueueEdge.ItemId` (ulong) else the CSR index | — |
| "Playing from X" | `b.CurrentContext` → `GetPlaylistAsync/GetAlbumAsync/GetArtistAsync(HydrationLevel.Identity)` | `Playback.ContextUri` → `Entities.Playlist/Album/Artist(uri).Name` | `handle.Knows(<Kind>Fields.Title)`; before that the crumb **is not rendered** (a "Playing from …" with no name is a lie) — except `Collection`, answered synchronously as `player.likedSongs` |
| shuffle / repeat / autoplay | `b.IsShuffle`, `b.Repeat`, `WaveeSettings.AutoplayEnabled` + `PlaybackPrefs.Epoch` | `Playback.Shuffle`, `Playback.Repeat`, `Settings.AutoplayEnabled` | always |
| pill accent | `Surfaces.ChromeSchemeFor(cover)` → `ChromeAccent` | **DATA GAP** (cover palette) | falls back to `Tok.AccentDefault` — never a grey pill |
| viewer (remote device) | `PlayerBarContent.RemoteDevice(b) is not null` | `Playback.ActiveDeviceId` + the device roster | always; drives `removable` and whether the local reorder path is offered |
| friends feed | `FriendsBridge.Items/State/Error/NowTick` | **RESOLVED, plan §9.6 Q5, 2026-09-12** — `Edges.Friends` (`EdgeTable<FriendEdge>`, `Edges.cs`, Wave 1) | `State` picks the surface; **rows win whenever `Count > 0`**, even during a refresh or a transient error |
| video placement / availability | `b.VideoSurface`, `b.VideoPlacementNow()`, `PlacementCore.Allows` | `Playback.Video.*` (Ch.24) | `PlacementCore.Resolve(state)`; `DockedVideoHosting.HostFor` decides rail vs page stage |
| "no video for this song" | `b.CurrentTrackHasVideo` | `Track.VideoCounterpart != 0` / `TrackFlags.HasVideo` (plan §4.2 has both) | `t.Knows(TrackFields.Video)` — **do not** render the placeholder while Video is unknown; that would flash "no video" on every track change |
| stage title / meta | `Track.Title`, `Artists`, `Album.Name` | `TrackFields.Identity` | falls back to `player.nothingPlaying` when the title is absent **or equals the uri** (`StageIdentity.cs:302`) |
| stage quality badge | `b.StreamFormat` (published by `NowPlayingProjection`) | `Playback.StreamFormat` | renders **nothing** when unpublished or when a remote device is active — "silence is the correct answer, not a fallback" |
| stage device line | `PlayerBarContent.RemoteDevice` + `LocalOutputs.SelectedOutputId`/`Devices` | `Playback.Os` device roster | always renders (falls back to `player.systemDefault`) |
| stage accent | `StageInk.Accent(track)` over the cover scheme | **DATA GAP** | `Tok.AccentDefault` when ungraded |
| stage backdrop | cover url + `BlurHash` | `Track.Image` | `StageInk.ArtStandIn(url)` (a tinted placeholder) until decoded — the stage must never let the page show through. **No cover at all** ⇒ a solid `ArtStandIn(null)` box, never a bare `Floor` (`ImmersiveLyricsSurface.cs:430-434`) |
| row density / artwork | `WaveeSettings.TrackRowStyle` + `AppearancePrefs.TrackArtworkHidden` (both Epoch-live) | `Settings.TrackRowStyle` / `Settings.TrackArtworkHidden` | always; **two independent branches**, and both belong in the row `Key` so a flip remounts. They reach the rail queue, the stage queue, the NPV "Next up" and the video body — four surfaces, one pair of settings |
| stage secondary-line toggle | `LyricsPrefs.Available` + `LyricsPrefs.Epoch` | Ch.22 `Lyrics.SecondaryAvailable` / `PrefsEpoch` | the 🌐 FAB is composed only when `Available != 0` **and** the lyrics pane is active — never composed-and-hidden (`ImmersiveLyricsSurface.cs:364`) |
| stage transport enablement | `track != null`, `b.Error`, `b.IsLoading` | `Playback.Current`, `Playback.Error`, `Playback.Loading` | `canTransport = track != null && err == null` gates prev/next/shuffle/repeat; `primaryEnabled = track != null && !loading` gates the play disc. They are **different predicates** — an error leaves play live (`StageIdentity.cs:89-90`) |

### DATA GAPS

Everything this surface shows that the plan's data model does not yet hold.

| # | element | 0.2.9 source | proposed column / edge |
|---|---|---|---|
| G1 | **Cover palette** — the hero wash, the queue's chrome accent, the stage's accent, the deck finishes, the flyout's "Album colour" swatch | `SpotifyLive.CoverColorPlane` (a decoded `getDynamicColorsByUris` grading, 5 roles × light/dark, cached on disk; ~9,316 entries observed) | `TrackTable`/`AlbumTable`: `Column<uint> PaletteLight0..4`, `PaletteDark0..4` (10 uints per row = 40 B) **or** a side table `PaletteTable` keyed by image `StringId` with `TrackFields.Palette`. The image id is the natural key, because two tracks sharing a cover share a grading. Add `Palette = 1 << 17` to `TrackFields`. |
| G2 | **Artist "About"** — bio, monthly listeners, followers, world rank, verified, header image | `NowPlayingInfo.About : Artist` from `AlbumEnrichment.GetNowPlayingInfoAsync` (Pathfinder, TTL-cached, not persisted) | `ArtistTable`: `Column<StringId> Bio, HeaderImage`; `Column<uint> MonthlyListeners, Followers`; `Column<ushort> WorldRank`; `TrackFlags`-style `Verified` bit. New group `ArtistFields.About`. |
| G3 | **Top cities** | `Artist.Extras.TopCities : IReadOnlyList<TopCity>` | `EdgeTable<TopCityEdge>` `ArtistTopCities` with `TopCityEdge(StringId City, StringId Country, uint Listeners)`; parent = artist slot. Targets unused (the payload IS the row), the `TrackTags` precedent. |
| G4 | **Merch** | `TrackNpvInfo.Merch` / `Artist.Extras.Merch` (`MerchItem(Name, Price, Description, Image, ShopUrl)`) | `EdgeTable<MerchEdge>` `ArtistMerch` with `MerchEdge(StringId Name, StringId Price, StringId Image, StringId ShopUrl)`. |
| G5 | **Track credits** — name, role, role group, artist uri, linkable + the source/label line | `TrackCredits` (kind-186 drawer) with `TrackNpvInfo.Credits` as the 10-cap fallback | `EdgeTable<CreditEdge>` `TrackCredits` with `CreditEdge(StringId Name, StringId Role, StringId RoleGroup, int ArtistSlot, byte Flags /*1 = linkable*/)`; plus `Column<StringId> CreditSources` on `TrackTable` (a pre-joined `", "` line — P11's "computed at commit, never a per-frame concat"). Add `TrackFields.Credits`. |
| G6 | **Friend activity** — the whole feed | `IFriendActivityService` (presence seed + dealer deltas) → `FriendsBridge` signals. No persistence. | A synthetic parent (the `Friends.cs` precedent of `HomeSection`): `EdgeTable<FriendEdge> Friends` with `FriendEdge(int UserSlot, long TimestampMs, int TrackSlot, int AlbumSlot, int ArtistSlot, int ContextSlot)`, plus `Signal<FriendFeedState>` on `Spotify.Connect`. The five uris become real handles, which is strictly better than 0.2.9's string soup: `RichText.RouteForUri` can go away. **SPLIT CONFIRMED, plan §9.6 Q5, 2026-09-12** — this chapter proposed the shape but no file; the plan puts the edge and columns above in Wave 1 with the other edges (`Edges.cs`, owner B) and the panel (this chapter's `FriendsPanel` → `Rail.UI.cs`) in Wave 4 with the rail (owner K). No `Entities/Friends.cs`. |
| G7 | **Lyrics document** | `svc.Lyrics.GetLyricsAsync(trackId)` | Ch.22's problem, but this chapter depends on it: `LyricsPeekClock.ShouldShow(doc)` gates the whole peek. |
| G8 | **Stream format badge** | `PlaybackBridge.StreamFormat` (a `string?` published by `NowPlayingProjection`, cleared on remote) | `Playback.State.StreamFormat : StringId` + the clear-on-remote fold. Plan §4.7 does not mention it. |
| G9 | **Docked video cap height + pinned flag** | `ShellUi.DockedVideoHeight` (`FloatSignal`), `DockedVideoHeightPinned`, persisted as `ShellDockedVideoHeight` | `Shell.DockedVideoHeight` / `.Pinned` + the same setting key. Purely chrome; no entity column. |
| G10 | **Player-style prefs** | `WaveeSettings.NpvPresentation`, `NpvPlayerStyle`, and the runtime-built `npv.player.{preset}.{option}` int keys | unchanged — HKCU ints through `Settings`, **slugs are persisted and must never be renamed** (`NpvPlayerCatalog.cs:8-9`). |

Two rules the port must not relax, both already CLAUDE.md policy:
- **Pages demand their whole model on mount.** The queue asks for `TrackFields.Row` over the WHOLE queue edge in one
  batch, not per visible window; the NPV panel asks for `ArtistFields.About | TrackFields.Credits` on the track change.
- **Derived facts live on the model.** "Is this row the now-playing one", "does this section have rows", "is the feed
  live" are model reads, not UI probes. The one thing 0.2.9 does probe and 0.3 must not is
  `HydrationLevels.TitleMissing(title, uri)` — a string comparison standing in for a known-bit. In 0.3 that is
  `!t.Knows(TrackFields.Title)`.

---

## 8. Pure rules to port verbatim

| class | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `StageLayout` (`readonly record struct`) | `Features/Player/StageLayout.cs` (353) | The whole stage allocator: wide⇄compact at 600 with 40 reserve; the height ladder (device line → volume row → demote) with 24 reserve; art as the residual, quantised to 4, clamped `[168, 300]`; `ColumnChromeH`; the fold sets; `Shows`/`Richness`; **and the whole scrim alpha ladder** (`ScrimBaseA` … `PaneShadeFeatherStop`) | `Wavee.Tests/StageLayoutTests.cs` (585 lines, 29 facts — sweeps, hysteresis, `NarrowingNeverAdds`, `GrowingEitherAxis_NeverTakesSomethingAway`, the scrim plateau, the shade's feather-to-zero, both ink arms) | **CORE section of `Shell/Stage.cs`** (new file — see §9) |
| `StageArm` (`readonly record struct`) | `Design/StageArm.cs` (143) | The stage's polarity: which ground/ink/glass/scrim/button/accent each theme takes; `StageArm.For(theme)` is a **pure** entry point so tests drive both arms without mutating global theme state | same file (`TheStageInkDarkArm_IsWaveeOnMediaVerbatim`, `TheStageInkLightArm_MirrorsTheDarkOne`, `TheLightArm_IsNoWorseThanTheShippedDarkOne`, `TheStageGlass_IsAtLeastAsAudibleAsTheAppsLightRowHover`) | **CORE section of `Shell/Stage.cs`** |
| `QueueSlots` + `QueueSlot` + `QueueSection` + `QueueSlotKind` | `Features/Player/QueueMovePlan.cs:8-71` | Flattening the three sections into reorder slots: `Realized(count, pages, pageSize)`; a section with no rows contributes **nothing, header included**; autoplay listed only while the toggle is on; `headers: false` is the stage's continuous list | `Wavee.Tests/QueueMovePlanTests.cs` (5 `Build_*` facts) | **CORE section of `Entities/Queue.cs`** |
| `QueueMovePlan` | `Features/Player/QueueMovePlan.cs:93-149` | A flat `(from, to)` → a section-local move, a refusal, or a no-op. The legal window is `start ≤ to ≤ end − 1`, the shared boundary claimed for the row's **own** section; a single-row section has exactly one legal slot and every other drop is a refusal. `InsertIndex` counts the queue rows above a boundary | same file (**10** facts; 15 in the file, 5 of them `Build_*`) | **CORE section of `Entities/Queue.cs`** |
| `QueueOrder` | `Features/Player/QueueOrder.cs` (80) | The optimistic reorder as pure data: `Same` (item id, else entry id), `Positions` (a section is a **subsequence**, so positions only advance; empty when a row moved under us), `Move` (remove + insert at the post-removal index — **not** a swap), `Remove` | `Wavee.Tests/QueueOrderTests.cs` (9 facts, incl. `Move_MatchesTheSessionOpItMirrors`) | **CORE section of `Entities/Queue.cs`** |
| `RailVideoCoupling` + `RailMode` | `App/RailVideoCoupling.cs` (83) | `ModeOnDock` / `OnRailClosed` / `ReDockOnRailOpen` / `CloseRailOnVideoLeft` / **`BodyModeFor`** — the render-time substitution that makes the rail yield to a watch page's stage | `Wavee.Tests/RailVideoCouplingTests.cs` (9 facts) | **CORE section of `Shell/Rail.cs`** (or `Shell/Shell.cs`; it must be engine-free either way) |
| `DockedVideoHosting` + `DockedVideoHost` + `DockedVideoFace` | `App/DockedVideoHosting.cs` (168) | `PageStageHosts` / `HostFor` / `ShouldMount` / `DockedHostAvailable` — the ≤1-mounted-surface invariant as a VALUE | `Wavee.Tests/DockedVideoHostingTests.cs` (298) | **CORE of `Playback/Playback.Video.cs`** (Ch.24 owns it; this chapter only reads it) |
| `NpvPlayerCatalog` | `Features/Player/NpvPlayerCatalog.cs` (90) | The one table behind the flyout, the Settings rows, the art menu and the tests: 12 presets × 3 groups × 4, their persisted slugs and ids, their option defs and choice lists, the swatch encoding, `FromCover = 0`, `ById` fallback | `Wavee.Tests/NpvPlayerCatalogTests.cs` (13 facts, incl. `Ids_AreContiguousAndEqualTheirIndex`, `Slugs_AreUniqueLowercaseAsciiAndPinned`, `EveryLabelKey_StartsWithPlayerDot`) | **CORE section of `Shell/Rail.cs`** (engine-free: `using System;` only) |
| `NpvPlayerPrefs` | `Features/Player/NpvPlayerPrefs.cs` (48) | Clamps + the write path: every writer clamps, persists, logs and **`Bump()`s once**; `SetStyle` also flips presentation to Player; `NextStyle` wraps | `Wavee.Tests/NpvPlayerPrefsTests.cs` (13 facts) | **CORE section of `Shell/Rail.cs`** |
| `LyricsPeekClock` | `Backend/Lyrics/LyricsPeekClock.cs` (45) | `ShouldShow` (line- or syllable-synced, ≥1 line), `ResolveLine` (binary search), `ActiveAndPeek` with `LeadMs = 140` and the pre-roll / last-line rules | `Wavee.Tests/Lyrics/NpvLyricsPeekTests.cs` (7 facts) | **CORE of `Shell/Lyrics.cs`** (Ch.22 owns the file; this chapter consumes it) |
| `ShellResponsiveLayout` rail block | `Features/Shell/ShellResponsiveLayout.cs:123-168` | `RailMinW/MaxW/DefaultW` = 200 / 500 / 340, `ClampRailWidth`, `DockedVideoNaturalH` (= `railW·9/16`, the SPLITTER's floor), `DockedVideoMaxH` = 560, **`DockedVideoFitMinH` = 120** (the FIT's own floor — a separate number, easy to miss), `ClampDockedVideoHeight`, `FitDockedVideoHeight` (**deliberately not** routed through the clamp — flooring a content fit at 16:9 forces every 2.35:1 stream 32% taller than its own aspect; it clamps to `[120, 560]` instead and answers 16:9 while the player has not reported a size), `CanFitRail` | `Wavee.Tests/ShellResponsiveLayoutTests.cs` (199) | **CORE of `Shell/Shell.cs`** (Ch.18 owns it) |

These eleven are **ported, never re-derived.** Every one of them has an executable test file that should port
assertion-for-assertion (plan §6: "Port: same assertions, call the section function on the new type").

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **The two `RightRail` arms are mutually exclusive returns, not a flag.** `Render` returns from the Details arm or
   the other one (`RightRail.cs:144-203`). That is what makes "only one docked face is ever mounted" a structural fact
   rather than a coincidence. Do not merge them into one tree with a conditional child.
2. **`DockedCap` mounts UNCONDITIONALLY in the non-Details arm.** It is `DockedVideoSurface`'s own gate that makes it
   vanish with no reflow; the wrapper's bound `Height` collapses to 0. Wrapping the mount in an `if` reintroduces the
   reflow this shape removed (`RightRail.cs:190-199`).
3. **The cap's `Height` must be the *same signal instance* the splitter writes.** A fresh `Prop.Of` thunk per render
   left `LayoutInput.Height` NaN and the ZStack collapsed to the 16-DIP strip (`RightRail.cs:206-210`).
4. **The rail's `Fill` bind includes `RailOpen`.** Drop that term and closing the rail is raw text over the expanding
   page for 300 ms (`RightRail.cs:92-96`).
5. **The hero tile is not inside the scroller, and the `ArtVideoToggle` offset is DERIVED.** `ArtTop = Spacing.S +
   NpvHeaderRow.Height + Spacing.S`. Never a literal — change the strip's height and the toggle must follow it down
   (`NowPlayingPanel.cs:512`, `RightRail.cs:332`).
6. **The deck `side` is quantised to 4 DIP because it is part of the remount `Key`.** `NpvDeck.Create` freezes `side`
   at mount, so only a re-key re-squares a mounted deck; a per-DIP key would remount four times as often during a rail
   drag (`NowPlayingPanel.cs:538-547`).
7. **The `SelectorBar`'s mirror signal is never written during Render.** The bar writes it on click (so the pill moves
   in the press frame) and a `UseEffect(epoch)` re-syncs it when some other surface moved the pref
   (`NpvHeaderRow.cs:60-66`).
8. **The peek's suppression is stopping the clock, not branching the render.** A render-time branch over a still-ticking
   signal keeps waking the panel to compute a line it must not use (`NpvLyricsPeek.cs:61-63`).
9. **`QueuePanel` `Peek`s the queue and writes `display` from ONE effect.** Reading `.Value` at render scope bought a
   live component→queue dependency and made one push re-render the panel **twice** — the render census caught it as
   `QueuePanel × 2 ⇒ SwipeControlCore × 100` (`QueuePanel.cs:81-91`).
10. **Section headers and "Show more" rows carry an explicit `Height`.** They are reorder slots; the extent the slot
    math samples must be the extent the layout arranges, or every boundary below a header lands one header-height off
    (`QueuePanel.cs:36-44`).
11. **`ReleaseStrandedLift` runs every render.** A server push that re-keys the lifted row frees its node mid-gesture;
    the engine keeps the drag alive on the chip but can no longer deliver the node's `OnDragCompleted`, so the
    `Reorderable` stays lifted forever and paints a row where the server never put it (`QueuePanel.cs:244-258`).
12. **`Upcoming` renders through the LIVE PROJECTION** (`reorder.ItemAt(i)`), with an `(uint)item >= count` guard. That
    is what makes headers glide out of the way mid-drag (`QueuePanel.cs:485-486`).
13. **The `Reorderable` item wrapper must be `Direction = 1`.** A row-direction wrapper sizes the row to its content
    and collapses the hover plate to the text (`QueuePanel.cs:517-521`).
14. **The stage's `ContextShield` must stay childless.** It is the answer to hit *ownership* and to the hover cascade,
    not just to the press one — the engine fixed the press half, the hit half is still the app's job
    (`StageIdentity.cs:111-155`).
15. **Every wide-column wrapper is `Direction = 1` on purpose.** A `BoxEl` defaults to a row, and a row's single child
    takes its intrinsic main-axis size — which is what made the seek bar a ~120-DIP stub and collapsed "0:15  3:20"
    into "0:15-3:20" (`StageIdentity.cs:178-181`).
16. **The identity column's horizontal participation lives on the HOST wrapper, not in the component.**
    `MirrorParticipation` copies the component's `Grow = 1` onto its anchor, and in a ROW band that reads as "and half
    the free width" — the grow leak that clipped lyrics mid-word (`ImmersiveLyricsSurface.cs:316-338`).
17. **The reading column is MEASURED, never predicted.** The deleted formula
    (`viewportW − LayoutWidth − ColumnGutter`) was a second private copy of the band's arithmetic; the column now grows
    into whatever the pane gives it, capped by `MaxWidth`, left-anchored (`StagePanes.cs:87-104`).
18. **`Slider.Create` takes a track LENGTH, not a stretch.** `VolumeTrackW = ColumnContentW − IconButtonSize −
    Spacing.S = 264`. A NaN propagates into every `Width` in the template — the whole of the "volume is a tiny dash"
    report (`StageIdentity.cs:61-64`, `:410-421`).
19. **`FitDockedVideoHeight` is deliberately NOT routed through `ClampDockedVideoHeight`** (see §8).
20. **The style flyout allocates a fresh mirror `Signal<int>` per Segmented row, per render, on purpose.** The option
    SET changes with the preset, so a hook-per-option would be a **conditional hook**
    (`PlayerStyleFlyout.cs:20-24`).
21. **Both always-on diagnostics port.** `queue.panel.rows` (`QueuePanel.PanelDump`, `:134-138`, `:903-927`) is an
    edge-triggered dump of what the panel ACTUALLY shows per section, with row keys, so a bad queue can be split from a
    bad render by diffing it against `queue.snapshot` / `bridge.ui.push-state`. The `npv` category
    (`NpvDiagnostics`: `presentation.set` / `style.set` / `option.set` / `flyout.close`, each carrying a `source` of
    `header` / `flyout` / `artMenu` / `settings` / `palette`) is how a pref written from five surfaces is attributed.
    CLAUDE.md's "always-on logs, no env switches" makes these part of the port, not optional instrumentation.
22. **`NpvPlayerPrefs` clamps on READ as well as write.** `Presentation` / `Style` / `Choice` all re-read the store
    behind an `Epoch` subscription and clamp before returning, so a hand-edited registry value or a renamed preset can
    never crash a render — it degrades to `Record` / `Cover` / choice 0 (`NpvPlayerPrefs.cs:16-26`).

### 9.2 Traps

- **Props freeze at mount.** `StageIdentity` takes `IReadSignal<StageLayout>`, not `StageLayout`. `NpvDeck.Create`
  takes a frozen `side` and a frozen `preset` — which is exactly why both are in the `Key`. `SaveButton` takes a frozen
  `uri` — which is why it carries `Key = "save:" + uri`.
- **`Key` remounts you must keep** — the complete list, grepped rather than remembered:
  - *NPV*: `"cover"` / `"<slug>@<side>"` (hero), `"save:"+uri`, `"follow:"+artistUri`, `"npv-lyrics:"+track.Id`,
    `"l:"+index` (peek slots).
  - *Rail queue*: `"qp:ctx"` (the "Playing from" crumb), `"np:"+uri+":classic="+classic` (the card cross-fade),
    `"upcoming"` (the column), `RowKey(entry)+":art="+show+":classic="+classic` where `RowKey` is `"i"+ItemId` else
    `"e"+EntryId`, `"hdr:"+title`, `"more:"+tag`, `"classic-hairline"`, `"lane:upcoming"` / `"lane:empty"`.
  - *Video body*: `"vrp:"+ItemId`.
  - *Friends*: `"fr:"+UserUri`.
  - *Stage queue*: `"stageupcoming"`, `"stage:autoplay"`, `RowKey(entry)+":art="+show` where `RowKey` is `"si"`/`"se"`-
    prefixed (deliberately a DIFFERENT prefix from the rail's, so the two surfaces' keys cannot alias),
    `"stagemore:"+tag`, `"stagelane:upcoming"` / `"stagelane:empty"`.
  - *Stage chrome*: `"sh:on"` / `"sh:off"` (heart), `"stage:shield"` (the surface's hit shield),
    `"stage:context-shield"` (the identity's), `"pane:lyrics"` / `"pane:queue"`.
  - *Stage identity*: `"stage:art"` / `"stage:identity-row"` / `"stage:seek"` / `"stage:transport"` /
    `"stage:volume"` / `"stage:device"` (these **six** must survive the wide⇄compact flip, or the entrance cascade
    replays on every resize), plus `"stage:header-row"` / `"stage:prev"` / `"stage:play"` / `"stage:next"` /
    `"stage:overflow"` (compact), `"tp:shuffle"` / `"tp:prev"` / `"tp:play"` / `"tp:next"` / `"tp:repeat"` (wide
    transport), and the host's `"stage:identity"` / `"stage:identity-comp"` / `"stage:panes"` wrappers.
- **`ReuseGuard`**: the queue's rows are eagerly keyed, not bound-recycled, so `ReuseGuard` should be silent by
  construction. If 0.3 moves the queue onto `ItemsView.CreateBound`, every per-row closure in `QueueRow` (`Remove`,
  `Move`, `like`, the menu factory, the swipe `ActionContext`) becomes a stale-capture hazard — that is precisely the
  "frozen-bind / wrong-track corruption" the 0.2.9 rework deleted (`QueuePanel.cs:26-30`).
- **Zero-allocation scroll frames vs per-row richness — how 0.2.9 reconciled them.** The queue is **not** virtualized
  and **is** rich (swipe, menu, drag, heart, overlay FAB). The reconciliation is **visual pagination**: 100 realized
  rows per section per revealed page, with an explicit `Show next 100 · N more` affordance. A 375-row FIXTURE-C queue
  therefore mounts 100 rows, not 375, and the keyed reconciler patches only what changed on a push. Port the pagination
  before porting the rows; a 375-row eager mount with a `SwipeControl` each will not hold 60 fps.
- **The `_pages` asymmetry**: the rail keeps **three** page counters (one per section) reset on a context change; the
  stage keeps **one** shared by all three. Keep both — they produce visibly different "Show more" behaviour and the
  stage's single counter is why its more-row shows only `⌄ · N`.
- **Two intervals, two cadences, both frame-clock**: 100 ms (peek) and 30 s (friends). Both `enabled:`-gated so an
  invisible panel costs nothing. The peek's gate is `show && !syncOff`; the friends' gate is `fb is not null`, with
  mount/unmount driving `fb.SetActive`.
- **`StagePane.Current` is a `static` signal**, deliberately: "last pane wins" for the session, not a setting and not
  per-track state (`StageChrome.cs:12-23`).

### 9.3 Where the plan is wrong or too thin for this surface

1. **§2 had no file for the stage, and that was the largest omission in this chapter — now settled.**
   `StageChrome + StageIdentity + StageLayout + StagePanes + StageArm + StageInk` = **2,261 lines** of 0.2.9
   (363 + 706 + 353 + 614 + 143 + 82, re-counted 2026-09-12), and §2's only candidate was `Shell/Lyrics.UI.cs` at
   2,000 — a budget `LyricsView` alone exceeds. §4 never mentioned the stage.
   **Settled (arbitration 2026-09-12, A12): `Shell/Stage.cs` (CORE, ~500 — `StageLayout` + `StageArm` + `StageInk` +
   `StagePane`) and `Shell/Stage.UI.cs` (~1,150 — chrome + identity + panes), Wave 4 owner K**, who already owns
   `Rail.UI.cs` and `Lyrics.*`. All six 0.2.9 stage files land in that pair and nowhere else: **chapter 22 drops its
   proposed `Shell/Lyrics.Stage.UI.cs`**, and the lyrics pane *inside* the stage is `Lyrics.View` mounted from
   `Lyrics.UI.cs` — this chapter owns the pane FRAME (the switcher, the pane box, the scrim and shade ladders), chapter
   22 owns what is drawn in it. The ~500 lines both chapters previously claimed for "panes" are counted **here**, once.
2. **§2's `Rail.UI.cs` at 1,800 lines is asked to hold six distinct surfaces.** The rail frame, the NPV panel, the
   hero tile, the header row, the lyrics peek, the style flyout + 12 mini-arts, the friends panel and the video body
   are **2,403 lines** in 0.2.9 before any stage code. `NpvThumbnails.cs` alone is 256 lines of irreducible literal
   geometry (12 transliterated CSS compositions; there is nothing to factor out). **Propose a named partial per §5's
   own rule ("a file that passes its budget by 30% gets a named partial"): `Shell/Rail.Styles.UI.cs` for the flyout,
   the thumbnails, the art menu and the swatch decoder.**
3. **§2's `Entities/Queue.UI.cs` at 600 lines is the single most optimistic number in the tree.** `QueuePanel.cs` is
   936 lines and `StageQueuePane` (inside `StagePanes.cs`) is another ~420 — 1,356 for the two queue surfaces, plus
   229 lines of pure rules that belong in `Queue.cs`. Sharing one row builder between the rail and the stage is the
   real saving available (0.2.9 already shares `CommitMove`, `MoveInSection`, `InsertAtSlot`, `SectionRows`, `Tag` and
   `ReleaseStrandedLift` as `QueuePanel` statics), but the two skins are genuinely different (rail: heart lane, ✕,
   hover ⋯, swipe, classic hairline variant; stage: grip, duration column, glass ramp, no swipe). **Honest: 900 UI +
   250 CORE.**
4. **§4.12's `Track.Row` signature does not cover a queue row.** `RowStyle` has `ShowNumber` / `ShowAddedAt` /
   `NumberOf` / `AddedAtOf` / `CursorOf` / `Context` — none of which a queue row uses, and it is missing everything one
   does: the heart lane, the remove verb, the drag payload, the swipe actions, the dim flag, the thin-row skeleton and
   the bucket the menu header names. A queue row is **not** `Track.Row(item, QueueStyle)`; it is its own builder over
   a `(Track, QueueEdge)` pair. Say so in `Queue.UI.cs`'s header.
5. **§4.3's `QueueEdge(ulong ItemId, byte Provider, byte Bucket)` is exactly right and should be kept** — it is the
   0.2.9 `QueueEntry` minus the redundant `EntryId`. But note that `RowKey` falls back to `EntryId` when `ItemId` is
   none (the `--fake` seed and a degenerate snapshot), so the 0.3 key must fall back to something stable too; the CSR
   index is not stable across a rewrite. **Propose: mint a synthetic `ItemId` at commit for rows that arrive without
   one, rather than carrying a second id.**
6. **§4.13's `UseEffect(() => Entities.Ensure(...))` pattern has no analogue for the rail**, which is not a page and
   never mounts on a route. The rail's demand is keyed on the **playing track**, not on navigation: the NPV panel must
   `Ensure(track, TrackFields.Credits | ArtistFields.About)` on every track change, and the queue must
   `EnsureRows(queueEdge.Targets(session), TrackFields.Row)` on every queue publication. Write that down in
   `Rail.UI.cs` / `Queue.UI.cs`, or the rail will be the one surface still doing per-render fetches.
7. **§5 Wave 4 gives owner K five files and the largest single visual surface in the app** (rail + decks + lyrics +
   stage). The stage alone is 2,261 lines with a 585-line test file. **Propose splitting the stage to a sixth owner, or
   moving `Deck.*` (Ch.23) off K.**
8. **No plan section covers the cover palette**, and five things in this chapter alone read it (§7 G1). It is not a
   nice-to-have: without it every accent pill, the hero wash, the vinyl finish and the stage's four accent jobs fall to
   `Tok.AccentDefault` and the app loses its per-album identity. It needs a Wave 1 column.
9. **Localisation drift to fix while porting** (all three are live in 0.2.9):
   - `QueuePanel.ShowMore` renders `$"Show next {nextPage}"` and `$"·  {remaining} more"` as **hardcoded English**,
     while `player.showNext` = "Show next {n}" and `player.moreCount` = "{n} more" exist in the catalogue
     (`QueuePanel.cs:559-560`).
   - `Menus.QueueEntry`'s header section label is hardcoded `"Next in queue"` / `"Autoplay"` / `"Next up"`, while
     `player.queueSectionNext` / `queueSectionAutoplay` / `queueSectionNextUp` exist (`Menus.cs:1098-1103`).
   - `NowPlayingPanel.AboutArtist` labels the world-rank fact with `Strings.Artist.WorldRank("").Trim()` — a format
     string with an empty argument, trimmed. It works, but a dedicated key is the honest fix
     (`NowPlayingPanel.cs:237`).

### 9.4 Drift between the design docs and the shipped code (the code wins)

- **`queue-rework-proposal.md` §10.1 specifies a History bucket** above the anchor with a scroll-to-anchor on mount and
  prepend-stable anchoring. **Shipped: there is no history section at all** — the queue is forward-looking only, and
  the design's whole anchor-index / scroll-pinning apparatus is deleted with it (`QueuePanel.cs:18-30`).
- **§10 says "no drag UI this branch; context-menu Move up/down is NOT added either".** **Shipped: both.** One
  `Reorderable` over the whole upcoming list plus `menu.moveUp` / `menu.moveDown`.
- **§10.2.4 says "virtualization stays on".** **Shipped: explicitly non-virtualized**, with visual pagination at 100
  rows per page — because plain keyed rows are what let the reconciler animate every change natively
  (`QueuePanel.cs:25-30`).
- **`npv-lyrics-peek-prototype.html:177` gives the spine `opacity: .85`.** **Shipped: full-opacity
  `Tok.AccentDefault`** (`NpvLyricsPeek.cs:90`).
- **`npv-player-styles-implementation.md:341-346` describes the compact Segmented as "height 26, font 12".**
  **Shipped: `Height = 28f`** (`PlayerStyleFlyout.cs:38`), and the header row's compact `SelectorBar` targets a 30-DIP
  pill inside a 33-DIP bar (`NpvHeaderRow.cs:34-38`).
- **The same doc's target tree says the pinned block grows "332 → 376 DIP".** At the 340 default it is
  `8 + 36 + 8 + 324 = 376` — correct, and the derivation is `ArtTop + side`.

### 9.5 Line budget

| | lines |
|---|---|
| 0.2.9, this surface | **5,912** (`RightRail` 458 · `NowPlayingPanel` 566 · `NpvHeaderRow` 112 · `NpvLyricsPeek` 177 · `NpvThumbnails` 256 · `NpvPlayerCatalog` 90 · `NpvPlayerPrefs` 48 · `NpvDiagnostics` 16 · `PlayerStyleFlyout` 232 · `QueuePanel` 936 · `QueueMovePlan` 149 · `QueueOrder` 80 · `FriendsPanel` 228 · `VideoRailPanel` 156 · `StageChrome` 363 · `StageIdentity` 706 · `StageLayout` 353 · `StagePanes` 614 · `FriendsBridge` 64 · `RailVideoCoupling` 83 · `StageArm` 143 · `StageInk` 82) |
| former plan §2 target | **2,400** — `Shell/Rail.UI.cs` 1,800 + `Entities/Queue.UI.cs` 600. **Nothing at all for the stage.** |
| honest estimate | **5,000** (the seven named files below; the stage pair is now in the tree, A12) |

Honest breakdown, with named files:

| file | lines | contents |
|---|---|---|
| `Shell/Rail.UI.cs` | 1,400 | rail frame + header + docked cap + NPV panel + hero tile + header row + lyrics peek + friends + video body |
| `Shell/Rail.Styles.UI.cs` (**new partial**) | 600 | `PlayerStyleFlyout` + `NpvThumbnails` (256 irreducible) + `NpvArtMenu` + `NpvSwatch` |
| `Shell/Rail.cs` (CORE) | 200 | `RailVideoCoupling` + `NpvPlayerCatalog` + `NpvPlayerPrefs` + `NpvDiagnostics` |
| `Entities/Queue.UI.cs` | 900 | the rail panel + the stage pane, one shared row builder, two skins |
| `Entities/Queue.cs` (CORE) | 250 | `QueueSlots` + `QueueMovePlan` + `QueueOrder` + the bucket split |
| `Shell/Stage.UI.cs` (**settled, A12**) | 1,150 | `StageChrome` + `StageIdentity` + **all of** `StagePanes` — the pane switcher, the pane box, `StageQueuePane`'s skin, and the lyrics pane's FRAME (its body is `Lyrics.View`, counted in chapter 22's `Lyrics.UI.cs`) |
| `Shell/Stage.cs` (CORE, **settled, A12**) | 500 | `StageLayout` + `StageArm` + **`StageInk`** + `StagePane`. 578 lines of 0.2.9 (353 + 143 + 82) that §9.5 itself calls irreducible design data, so 500 is a floor, not a target — the earlier 400 omitted `StageInk`, which §1.2 already homed here |
| total | **5,000** | |

Where the ~900 lines of saving come from (5,912 − 5,000), concretely: no `Loadable`/`LoadState` plumbing (`UseResource` × 4 becomes
`Knows` checks), no `bridge is null` guard on every read, no `HydrationLevels` string probes, one shared queue-row
builder instead of two, `FriendsBridge`'s signal-copying dissolved into the entity tables, and `ImmediateContextName`
/ `ResolveContextNameAsync` (duplicated verbatim in `QueuePanel` **and** `StagePanes`, ~40 lines each) collapsing to
one `handle.Name` read. Nothing is saved on the thumbnails, the scrim ladder, the stage's geometry table or the
option catalogue — those are irreducible design data.

**Counted exactly once (arbitration 2026-09-12).** This chapter and chapter 22 both used to bill the stage's panes.
The line is drawn at the pane frame: `Shell/Stage.UI.cs` (1,150, here) carries `StagePanes` whole — the switcher, the
pane box, the queue pane's skin and the lyrics pane's frame; `Shell/Lyrics.UI.cs` (chapter 22) carries the immersive
surface and the lyrics column that mounts inside that frame. Chapter 22's `Shell/Lyrics.Stage.UI.cs` is **dropped**,
its 1,400 redistributed as ≈900 into `Lyrics.UI.cs` and ≈500 recognised as already inside the 1,150 above. Video is
drawn the same way (A13): `Shell/Video.{cs,UI.cs,Host.cs}` is chapter 24's, owner K, Wave 4; `Playback/Playback.Video.cs`
stays the decode/host only (owner H, Wave 3); and **the rail's video panel body stays in `Rail.UI.cs`**, inside the
1,400 above — chapter 24 bills it as a mount site at ~0 net, not as a second copy.

### 9.6 Files missing from the §2 tree

| missing | why it matters |
|---|---|
| ~~`Shell/Stage.cs` + `Shell/Stage.UI.cs`~~ — **settled 2026-09-12 (A12), now in the tree, owner K, Wave 4** | the entire immersive stage — 2,261 lines, a 585-line test file, and the app's most visually distinctive surface |
| `Shell/Rail.Styles.UI.cs` | 642 lines of player-style catalogue, flyout and mini-art that `Rail.UI.cs` cannot absorb inside 1,800 |
| a home for `FriendsPanel`'s data | §2 has no `Friends.cs`/`Social.cs`, no friends edge in §4.3, and no mention in any wave's port list (G6) |
| a home for the cover palette | §4.1/§4.2 have no palette column; five features in this chapter read it (G1) |
| ~~`VideoRailPanel`'s body~~ — **settled 2026-09-12 (A13)** | it stays folded into `Rail.UI.cs`, owner **K**, Wave 4. `Playback/Playback.Video.cs` is the decode/host only (owner **H**, Wave 3) and the four video SURFACES are `Shell/Video.{cs,UI.cs,Host.cs}` (owner **K**, Wave 4, chapter 24). Three files, one seam, no ambiguity |

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`, launched with `--fake`.
"Static capture" = a screenshot at the stated window size; "hover capture" = a screenshot with the pointer parked on
the named element; "frame recording" = a screen recording, frame-diffed.

**`--fake` caveats, verify these first so the rest of the list is readable:**
- The friends service is `NullFriendActivityService` ⇒ the Friends panel **always** shows the offline state
  (`Services.cs:324`). Items 33-36 must be checked against a live session, not `--fake`.
- Every fake queue entry carries `QueueItemId.None` (`FakeData.cs:573-574`) ⇒ no ✕, no Move up/down, and a drag lifts
  and FLIPs but commits nothing. Items 21-23 and 26 must be checked live.
- `--fake` has no Spotify colour gradings ⇒ every accent falls back to `Tok.AccentDefault`. Items 14, 18 and 45 need a
  live session to see a real cover accent; in `--fake` verify only that the fallback is the system accent and not grey.

### Rail frame

1. `--fake`, 1400×900, rail closed → open via the player bar's Now-playing button. **Frame recording**: the panel
   slides in over ~300 ms with **no opacity change**; the page re-tiles in the commit frame (no second easing track).
2. Same recording, reversed within 100 ms of the open: the close **continues from the visible position**, it does not
   jump back to fully-open first.
3. 1400×900, rail open, Details. **Static capture**: exactly ONE hairline along the rail's left edge and ONE along its
   top; the top-left corner is rounded at 8 and every other corner is square.
4. Same capture: no drop shadow on the docked rail.
5. Narrow the window to 1000×900 (below `CanFitRail` at a 240 sidebar). **Static capture**: the page does **not**
   resize; the rail overlays it with a **uniform** ring and a flyout shadow, and its own `FileArea` coat.
6. Drag the rail's left seam. **Frame recording**: width tracks the pointer between 200 and 500 with no snap; release
   and reopen the app — the width persists.
7. 1400×900, Details rail, header strip. **Static capture at 2×**: the gear is a 32-DIP square with a 16-DIP glyph
   (not 12), and the eyebrow reads "Now playing" in **sentence case**, not caps.

### Now-playing panel

8. Details rail @ 340, a playing track. **Static capture**: the cover is 324 square, 8 DIP inside the rail on all
   three visible sides, with no bottom padding of its own.
9. Scroll the Details body to the bottom. **Frame recording**: the cover and the header strip **do not move**.
10. Same scroll: the scroller's top and bottom edges fade (`AutoEdgeFade`).
11. A track with a long title. **Static capture**: the title wraps to at most 3 lines at 20/28/600 then ellipsizes;
    the artist line is 14/20 secondary and the album line 12/16 tertiary, 2 DIP apart.
12. Hover the artist name, then the album name. **Hover captures**: each is an independent link (cursor changes over
    the word only).
13. A track whose artist has a bio. **Static capture**: the About card's header band is 132 tall with a bottom edge
    fade, the card has a 1-DIP stroke and `FillCardSecondary` fill, and up to three fact pills wrap at 8-DIP gaps.
14. Hover the About card. **Hover capture**: the whole card lightens to `FillCardDefault` (one plate, not the
    individual rows).
15. A track with top cities. **Static capture**: at most 5 rows; the longest bar is 260 DIP and the shortest is at
    least `0.08 × 260 = 20.8` DIP (nothing collapses to zero).
16. A track with credits. **Static capture**: role-group eyebrows keep the server's own casing (not upper-cased), and
    the source line reads `Source: {label}` on at most 2 lines.
17. A track with a lyrics document. **Frame recording** across a line boundary: the active line slides **up and out**
    by 56 DIP while the next rises into its slot and brightens from 0.38 to 1, over ~150 ms, with **no blur**.
18. Same: the 3-DIP spine is full-height, rounded at 1.5, in the accent colour.
19. Play a **video** track with lyrics. **Static capture**: the reel is replaced by a single 56-DIP row with a
    half-height tertiary spine and the text "Synced lyrics aren't available while a video is playing".
20. Nothing playing. **Static capture**: the pinned slot is **empty** (no square, no header strip) and the body reads
    "Nothing playing" at 20/28/600, centred.

### Queue

21. Queue rail @ 340, a playing track from a playlist. **Static capture**: three pills at the top (32 tall, 16 radius),
    then "Playing from {name} ›", then a bordered 64-tall now-playing card with 10-DIP padding and a 44-DIP cover.
22. **Static capture**: the section headers read "Next in queue N Clear", "Next up N", "Autoplay N" + the hint line,
    all as eyebrows in tertiary ink. The Autoplay header is visibly taller (54 vs 36).
23. **Static capture**: the currently playing track appears **only** in the card, never as a row.
24. **Static capture**: scroll to the Autoplay rows — the whole row (art included) is at 0.72 opacity.
25. Hover a queue row. **Hover capture**: the row plate lights and the **⋯ alone** fades in from 0. The ✕ and the
    heart were **already painted at rest** — if the ✕ fades in, an opacity track has been copied across from the
    stage's row, which is the one that does hover-reveal it.
26. **Live session**: drag a row two slots down inside "Next in queue". **Frame recording**: the source row stays in
    place at 0.40 opacity, the neighbours FLIP to make room, and on release the row lands exactly where the insertion
    line was.
27. **Live session**: drag a "Next in queue" row down into "Next up". **Frame recording**: on release the row snaps
    back and an Informational toast reads "Can't move across sections".
28. **Live session**: drag a row and release it over the page. Nothing changes, no toast.
29. Drag an album from the page onto the queue lane. **Frame recording**: the drop caption reads "Add to queue"; after
    the drop a success toast names the song count.
30. Drag an **artist** onto the lane. The caption reads "Can't add an artist".
31. A queue with > 100 rows in one section (live, or a 375-row set_queue). **Static capture**: exactly 100 rows then a
    40-DIP "⌄ Show next 100 · N more" row; clicking it reveals 100 more and the count updates.
32. Toggle the Autoplay pill. **Frame recording**: the pill fills with the accent, the ∞ and label flip to on-accent
    ink, and the Autoplay section appears/disappears with its rows sliding.
33. Right-click a queue row. **Static capture**: the menu header reads `{artists} · Next in queue` (or Autoplay / Next
    up), the command strip is Play now · Play next · Add to queue · Like, and the rows include Move up / Move down
    where legal and Remove from queue at the end.
34. Turn on Classic row style (Settings ▸ Appearance). **Static capture**: queue rows lose their artwork and corner
    radius, gain a 1-DIP bottom hairline, and title+artists fold into one 14/20 span run.
34a. Classic, an **explicit** track in the queue. **Static capture**: the word-mark badge sits after the ellipsized
    identity run — and it is the ONLY place the rail queue shows an explicit badge; the modern row has none at all.
34b. Turn on "Hide track artwork" with Classic OFF. **Static capture**: the 34-square and its FAB are gone from every
    queue row, from the NPV "Next up" list and from the Video body's rows; the heart lane, ⋯ and ✕ are unchanged. The
    two settings are independent — verify both, and verify the rows REMOUNT (the key carries `:art=`) rather than
    re-binding.
34c. A queue whose rows have arrived without metadata (a `set_queue` that outran the identity pass). **Static
    capture**: the thin row is two bars — 120×14 over 80×11 at `FillSubtleSecondary` — with the heart, the art, the ⋯
    and the ✕ all still in place. Never a bare `spotify:track:…` uri. In Classic the same row is ONE 160×14 bar.

### Friends

35. **Live session**, Friends rail @ 340, at least one friend listening now. **Static capture**: a 40-DIP avatar with
    a 12-DIP accent dot ringed 2 DIP in the pane colour, and an equalizer (14 tall) in the 44-DIP trailing slot.
36. **Live session**: a friend who stopped > 2 minutes ago. **Static capture**: no dot, and the trailing slot reads a
    relative time ("3 hr") at 12/16/600 tertiary.
37. **Live session**, first open. **Frame recording**: 5 skeleton rows (40 circle + a 120×12 and a 160×12 bar) before
    the real rows land, and the real rows enter with a 6-DIP rise.
38. `--fake`, Friends rail. **Static capture**: "Sign in to see what your friends are playing." at 20/28/600, centred,
    with no Retry button.
39. **Live session**, force an error. **Static capture**: "Couldn't load friend activity." plus a **Standard** (not
    accent) Retry button.
40. Hover a friend row with a context. **Hover capture**: the row plate lights and the cursor is a hand. Hover a row
    **without** a route: no plate, arrow cursor.

### Player styles

41. Details rail, click "Player" on the SelectorBar. **Frame recording**: the pill moves in the press frame; the hero
    swaps to the current deck in the same square.
42. Click the gear. **Static capture**: a 340-wide flyout with three labelled rows of four 74.5-DIP cards, the
    selected card carrying a 2-DIP accent ring and a `TextPrimary` label.
43. Click a different thumbnail. **Frame recording**: the deck behind the flyout changes and **the flyout stays open**.
44. Same flyout: the option rows below change to that preset's options (e.g. Record → Finish/Size/Speed/Sleeve;
    Winamp → Skin/Analyser).
45. Record preset, Finish row. **Static capture**: five 30-DIP circles with 22-DIP inner dots; the "Album colour" one
    paints the **same** wash the cover placeholder uses. Hover it — the tooltip reads "Album colour".
46. Press Escape. The flyout closes and focus returns to the gear (the gear's accent tint clears).
47. Right-click the hero (in either presentation). **Static capture**: Cover / ‹Player› as radio items, a separator,
    then "Player style…".
48. Pick a style from the flyout while the **cover** is showing. The presentation flips to Player automatically.
49. Change the rail width by 40 DIP with a deck showing. **Frame recording**: the deck re-squares in 4-DIP steps
    (at most one remount per 4 DIP), never per pixel.
50. **Static capture at 2×** of all twelve thumbnails: each is a 4-radius plate; Record shows a tilted pale sleeve
    behind a black disc with a pale label; CD shows a 5-stop diagonal rainbow with the plate through its hole; WMP's
    analyser bars take the **theme accent** while every other mini uses literal colours.

### Video in the rail

51. Dock a music video into the rail (Details body). **Static capture**: the pinned slot is the **full-bleed** card at
    the rail's width, fitted to the video's aspect — not a square inset.
52. Switch to Queue, Lyrics and Friends. **Static captures**: the card is the same width, the same height and the same
    shape in all four bodies, sitting above the header in three of them and in the hero slot in Details.
53. Drag the card's bottom edge. **Frame recording**: the height grows from the 16:9 floor toward 560 and the body
    below shrinks; release, change track, and the height stays pinned for that source.
54. A track with **no** video, Video rail mode. **Static capture**: the rail stays open, the card is replaced by a
    16:9 letterboxed placeholder reading "No video for this song", and Up next keeps rendering underneath.
55. An un-docked video-capable track, Details rail. **Hover capture** of the cover's top-right corner: a 24-DIP
    two-half Art|Video toggle sitting XS inside the art's corner (not straddling the header strip's corner).
56. Close the rail while a video is docked. The video demotes to the floating mini player; re-open the rail and it
    re-docks.

### Stage

57. Open the immersive stage at 1440×900. **Static capture**: a 352-DIP identity column, 56 DIP of air, then the
    reading column; the column shade has **no findable right edge** (sample pixels at x = 600, 610, 620 — all three
    within one 8-bit step of the plateau).
58. Same capture: the scrim is darkest at the very top and the very bottom with a flat middle; there is **no** boxed
    band under the caption cluster or under the pivot row.
59. Open the stage. **Frame recording**: the cover appears immediately (no rise/fade) while the identity row, seek
    block, transport, volume row and device line fade in at 40 ms intervals.
60. 1440×900. **Static capture at 2×**: the transport is 32 · 40 · 56 · 40 · 32 with 6-DIP gaps, centred in 304 DIP;
    the volume rail is 264 wide (not a thumb-sized dash).
61. Hover the ⌄ exit disc. **Hover capture**: a 44-DIP disc made of INK with a card shadow, visibly larger and lighter
    than the 40-DIP 🌐 disc beside it.
62. Hover the **gap** between two transport buttons. **Hover capture**: nothing lights — not the heart, not the
    buttons.
63. Press and hold anywhere on the identity column's background. **Hover capture**: no button's plate lights.
64. Right-click anywhere on the identity column (art, title, a gap). The now-playing menu opens.
65. Click "Queue" in the pivot. **Frame recording**: a 250 ms opacity cross-fade — the lyrics do **not** unmount and
    remount (scroll position and the sung-line state survive a switch back).
66. Same recording: the underline moves by colour (transparent ↔ accent over 167 ms), it does not fly between items.
67. Queue pane up. **Static capture**: the pane's left edge shows **no** shade boundary; the shade comes up out of zero
    and deepens toward the window edge.
68. Queue pane. **Static capture**: rows are 56 tall with a 38-DIP cover, a 44-DIP end-aligned duration column whose
    colons line up, and **no section captions** between Queue / Next up / Autoplay.
69. Hover a stage queue row. **Hover capture**: the grip glyph fades in on the left, the ✕ fades in on the right, and
    the row takes the glass hover (Ink @ 0.10), not the app's row-hover plate.
70. Drag the window from 1440 wide down to 500 and back up slowly. **Frame recording**: the wide⇄compact flip happens
    exactly **once** in each direction (demotion at 600, promotion at 640), with no thrash on the boundary.
71. 560×900. **Static capture**: a 64-DIP cover in a header row, a 40-DIP play button, 32-DIP prev/next, a "…"
    button, and a full-width seek block beneath.
72. 560×900, click "…". **Static capture**: Shuffle (toggle), Repeat (toggle), Mute/Unmute, a separator, then the
    whole device list — every folded control is reachable.
73. 1440×640. **Static capture**: the volume row and the device line are gone, the cover is 192 square, and the "…"
    does **not** appear (the wide shape has no overflow).
74. 1440×613. **Static capture**: the stage is in its compact shape.
75. Switch the app to **light** theme with the stage open. **Static capture**: the ground is pale, the ink is near-black
    at the same three alphas, the play button is a **dark** disc with a pale glyph, and no region is a dark patch on a
    light ground.
76. Light theme, a near-black cover. **Static capture**: the title is still legible on the plateau (the
    contrast claim in §4.5 — measure it).
77. Play a FLAC/Vorbis stream locally. **Static capture**: the quality badge sits centred between the two times at
    11.5/16/600 tertiary. Transfer to another device: the badge **disappears** (it does not show a stale value).
78. Turn off Settings ▸ animated backdrop. **Frame recording** for 60 s: the backdrop is completely still (no ticker),
    and the cover is still correctly centred after a window resize.
79. Enable OS reduced motion. **Frame recording**: the stage still fades in but does not scale; the entrance cascade
    has zero delays; the pane cross-fade still fades.
80. Open the stage with nothing playing. **Static capture**: the title reads "Nothing playing", the transport is
    disabled (tertiary ink, no hand cursor, no hover scale), the play disc falls back to the **scrim** plate with a
    tertiary glyph (not the ink disc greyed), and the queue pane reads "Nothing up next" at 14/20 tertiary — plain
    text, not an `EmptyState`, with the ∞ Autoplay row still above it.
81. A track with **no** lyrics document, Details rail. **Static capture**: the peek renders **nothing at all** — the
    hero meta's bottom padding meets the sections directly. There is no empty 112-DIP box and no placeholder line.
82. Scrub to before the first lyric line. **Static capture**: the reel's TOP slot is empty and the first line sits in
    the BOTTOM slot at 0.38. Scrub past the last line: the last line holds in the top slot and the bottom slot is
    empty. (Both are `LyricsPeekClock.ActiveAndPeek` arms, and both are easy to lose to an "always two lines" port.)
83. **Live session**: put playback on another Connect device, then open the stage's queue pane. **Static capture**:
    no drag grip on any row (the list no longer owns the drag), no ✕, and Move up / Move down absent from the row
    menu. The rail's queue in the same state: no ✕, no Clear pill.
84. A track whose artist has **no header image**. **Static capture**: the About card's band is a 72-DIP
    `FillSubtleSecondary` strip holding a 56-DIP `PersonPicture`, not a 132-DIP image band with an edge fade.
85. A merch item with no shop URL. **Hover capture**: the row still lightens but the cursor stays an arrow and the
    click does nothing; a merch item with no price reads "Buy" in accent ink.
86. 560×900, fold every control, then click "…" on a build where nothing is folded. Nothing opens (the
    `items.Count == 0` early return) — verify no empty menu frame flashes.

### Rail width ladder (W7b)

Every item above was captured at the 340 default; these six are the ladder itself. Drag the rail's LEFT seam — do
not resize the window — so only `RailWidth` changes.

87. Drag the seam to the **200 minimum**, Details / Cover. **Static capture**: the art is **184²** (not 200, not
    192 — `round((200−16)/4)·4`), the pinned block is 236 tall, the header eyebrow has ellipsized ("Now p…") while
    the Cover|<preset> pill and the 32-DIP gear are at full size, and the title column is 136 wide with the 36-DIP
    SaveButton still beside it. Switch to a **deck** preset at the same width: the deck is 184² and re-squares as
    you drag, one remount per 4 DIP.
88. Same 200, **Queue**. **Static capture**: the identity lane is **4 DIP** — title and artist ellipsize to nothing
    while the heart (26), the art (34), the ⋯ (32) and the ✕ (32) all keep their full lanes and the row is still
    44 tall. The pills row does **not** wrap: the Autoplay pill is cut by the panel root's clip. A **thin** row at
    this width overflows its 120/80 bars rather than compressing them.
89. Same 200, **Friends** (live session), then open the **player-style flyout**. **Static capture**: the friends
    text lane is 52 DIP with all three lines ellipsized and the 44-DIP trailing slot unmoved; the flyout is still
    **340 wide** — 140 wider than the rail — hanging left over the page, its four 74.5 thumb cards unreflowed.
90. Rail at 340 in a **1060-wide** window with a 240 sidebar (the rail is docked), then drag the seam **wider**.
    **Frame recording**: the rail flips to the floating presentation (own `FileArea` coat, uniform ring,
    `Elevation.Flyout`, no reservation spacer) the moment the width passes 340 — mid-drag, without the window
    changing — and flips back on the way in.
91. Dock a **16:9** video, then drag the rail to 200. **Static capture**: the cap is **120** tall, not 112.5 — the
    content fit clamps UP to `DockedVideoFitMinH`, so the picture sits in 3.75-DIP bars top and bottom (the one
    width where the fit and the splitter's 16:9 floor disagree). Now drag the cap to 560 at rail 500 and narrow the
    rail back to 200: the cap **stays 560** (a rail resize never re-clamps a pinned height).
92. A track with **no lyrics at all**, rail in Lyrics mode. **Static capture**: the header still offers the expand
    button; expand it. The stage opens fully — backdrop, identity column, transport, pivot — with one centred
    "No lyrics available" at 14/20 in the media secondary ink, **no 🌐 fab**, and the Queue pivot still switching
    to a normal queue pane. Skip to a track that HAS a translation: the 🌐 appears; skip back: it goes away again
    (the capability is retired by `ClearDocument`, never left over).

---

## 11. Audit log

Adversarial re-read of every assigned 0.2.9 source against this chapter, 2026-09-12. Each line is one correction made
in place. `wrong` = the chapter asserted something the code contradicts; `missing` = a state / element / rule the code
has and the chapter did not; `unverified` = a claim the chapter made that this pass could not check from the assigned
sources; `overclaim` = a claim stated harder than the code supports.

| # | kind | section | correction |
|---|---|---|---|
| 1 | **wrong** | §2 W3, W5 · §5 · §10 #25 | The rail queue row's **✕ is not hover-revealed**. Only the ⋯ wrapper carries `Opacity 0 / HoverOpacity 1` (`QueuePanel.cs:661`); the ✕ at `:677-689` is painted at rest. The STAGE's ✕ is the hover-revealed one (`StagePanes.cs:512-521`). Fixed in both wireframes, the motion table, the token table and parity #25. |
| 2 | **wrong** | §2 W1 | The eyebrow was drawn as "NOW PLAYING". `WaveeType.Eyebrow` never upper-cases a loc string and the code comment forbids a call-site `.ToUpper()` (`NpvHeaderRow.cs:84-91`); the string is `player.nowPlaying` = "Now playing". The wireframe contradicted the chapter's own parity item 7. |
| 3 | **wrong** | §2 W3 | The Queue body's header read "Now playing". `RightRail.Title` gives `player.queue` = "Queue" there (`:436-443`); added the full per-mode title list. |
| 4 | **wrong** | §2 W1 | Section title is "Listened **to** most in" (`artist.listenedMostIn`), and the world-rank fact's LABEL renders literally as "# in the world" (`Strings.Artist.WorldRank("").Trim()`), not "World rank". |
| 5 | **wrong** | §3 | The "rail edge" row credited the floating uniform ring to the `edge` element. Floating sets `edge.BorderWidth = 0` (`RightRail.cs:107`); the ring and the `Elevation.Flyout` live on the arm's ROOT box (`:150-154`, `:182-184`). Split into two rows. |
| 6 | **wrong** | §8 | `QueueMovePlan` has **10** facts, not 11 (`QueueMovePlanTests.cs`: 15 total, 5 of them `Build_*`). |
| 7 | **wrong** | §9.2 | "these five" enumerated six stage keys. Corrected to six and the list completed. |
| 8 | **wrong** | §5 | The `Stopwatch.GetTimestamp` citation was `:509`/`:60`; the call is `ImmersiveLyricsSurface.cs:508` and the "never TickCount64" comment is on the `_driftOriginQpc` field at `:91`. |
| 9 | missing | §0 #13 · §10 | The peek's **three** slot states: pre-roll (active `−1` ⇒ empty top slot, peek = line 0 at 0.38), running, last-line (peek `−1` ⇒ empty bottom slot) — `LyricsPeekClock.cs:35-45`. Plus `LeadMs = 140` and the slot text's `MaxLines 2, Wrap`. Two parity items added. |
| 10 | missing | §2 W3 · §3 | A variants table for the rail queue row: thin-row skeleton, Classic (hairline, one span run, **the only explicit badge in the queue**, one-bar thin form), artwork-hidden, viewer. Four token rows added (⋯, ✕, classic hairline, classic identity). |
| 11 | missing | §2 W12 | The rail↔stage queue asymmetry was two bullets; it is **thirteen** differences. Replaced with a full table (heart, ⋯, ✕ reveal, grip + its viewer gate, duration column, swipe, classic, thin-row, dim alpha, hover plate, page counters, captions, key prefix). |
| 12 | missing | §2 W12 · §3 | The stage queue pane's **empty** state — a `Pad top 24` box with `player.queueEmpty` = "Nothing up next" at 14/20 `InkTertiary`, NOT an `EmptyState`, with the ∞ row still above it and the `"stagelane:empty"` lane still wrapped (`StagePanes.cs:261-269`). |
| 13 | missing | §2 W7 · §3 | The Video body's empty "Up next" → `EmptyState.Compact(player.queueEmpty)`, plus its nothing-playing and null-bridge arms (`VideoRailPanel.cs:79-80`, `RightRail.cs:263-265`). |
| 14 | missing | §2 W11 · §7 | The stage top bar's 🌐 is conditional **twice**: `LyricsPrefs.Available != 0` AND the lyrics pane active (`ImmersiveLyricsSurface.cs:364`). The chapter drew it unconditionally. |
| 15 | missing | §2 W11 · §3 · §7 | The stage's **disabled** arms, and that they are two different predicates: `canTransport` (track + no error) gates prev/next/satellites; `primaryEnabled` (track + not loading) gates play, whose disabled plate falls back to `ScrimRest` + `InkTertiary` rather than greying (`StageIdentity.cs:89-90`, `StageChrome.cs:263-276`). |
| 16 | missing | §3 | Glyph sizes the table never named: wide prev/next **17** (compact 15), wide "…" 16, mute 15; and a `stage art` row (300/64, `Elevation.Dialog`/`Card`, `decodePx` 512/192, off the cascade). |
| 17 | missing | §3 · §6.6 | The device line's glyph map (Headphones/Headset / Hdmi / else / remote) and the remote LABEL `player.playingOn` = "Playing on {device}" — the chapter said only "falls back to `player.systemDefault`". |
| 18 | missing | §2 W13 · §6.6 | The compact "…" opens **nothing** when `items.Count == 0` (`StageIdentity.cs:691`), is built at open time, and takes the `player.nowPlaying` tooltip. |
| 19 | missing | §4.4 | Three `StageArm` rungs were absent: `SkeletonBar` (`Ink @ 0.12`), `ArtStandIn` and `IsDark` — the last being the one sanctioned polarity read. |
| 20 | missing | §2 W1 · §6.2 | The About card's **no-header-image fallback** (72-DIP band + 56 `PersonPicture`), the `FollowButton` and its `"follow:"+uri` key, `LoadingSection` rendering UNDER the "About the artist" header, the credits group-key fallback to `Role`, the merch "Buy" price fallback and its inert-with-hover arm, and the SaveButton's absence without a `LibraryBridge`. |
| 21 | missing | §2 W1 | The NPV "Next up" rows carry `ColumnSet(Video: true)` — the video glyph column IS on — and sit in an `r4` wrapper with `HoverFill = FillSubtleSecondary` (`NowPlayingPanel.cs:37`, `:420-434`). |
| 22 | missing | §3 · §7 · §10 | "Hide track artwork" (`AppearancePrefs.TrackArtworkHidden`) as a settings branch **independent of Classic**, reaching four surfaces and appearing in the row key. The chapter covered Classic only. |
| 23 | missing | §9.2 | The Key list was ~40% complete. Rewritten by grep: added `"qp:ctx"`, `"upcoming"`, `"classic-hairline"`, `"follow:"`, `"vrp:"`, `"stageupcoming"`, `"stagemore:"`, `"stage:autoplay"`, the `"si"/"se"` stage row prefix, `"stage:shield"`, `"stage:context-shield"`, `"stage:header-row"`, `"stage:prev/play/next/overflow"`, the five `"tp:*"` transport keys and the host's `"stage:identity"/"-comp"/"stage:panes"`. |
| 24 | missing | §3 | The rail's `HitTestVisible = baseline \|\| open` — a closed rail is laid out, translated off-clip and unhittable (`RightRail.cs:148`, `:180`). |
| 25 | missing | §8 | `ShellResponsiveLayout.DockedVideoFitMinH = 120` — the content fit's own floor, a different number from the splitter's 16:9 floor, and the one a port is most likely to collapse into the clamp. |
| 26 | missing | §9.1 | Two always-on diagnostics that CLAUDE.md makes part of the port: `queue.panel.rows` (`PanelDump`) and the `npv` category with its five `source` values. Plus `NpvPlayerPrefs` clamping on read as well as write. |
| 27 | missing | §5 | The peek's `UseLayoutEffect(show)` seed, the stage ✕/grip reveals as their own rows, and the entrance cascade's `StaggerCap = 8` ⇒ **320 ms** ceiling (`8 × StaggerMs 40`; the "360 ms" in `WaveeMotion.cs:105-106` is the source-comment drift `00-design-system.md §9.6` records, and `EntranceStaggerTests.cs:52` pins 320). |
| 28 | missing | §5 | `FriendsPanel.cs:49` reads `DateTimeOffset.UtcNow` once per render. Noted as the one *correct* wall-clock read (a server-epoch age, not an animation clock) so a future reader does not "fix" it into the frame clock. |
| 29 | missing | §2 W9 | Every preset carries ≥1 option (1–4), so the "Options" label never stands alone — there is no empty arm to design. |
| 30 | missing | §6.3 · §6.4 | The gear's `player.playerStyle` tooltip; scroll-closes-swipe (`OnScrollGeometryChanged`); the classic/artwork settings pair as an interaction row. |
| 31 | **critic-fix: missing** | §2 (new **W7b**), §3, §5, §6.1, §10 | **Every rail frame was drawn at one width.** W1–W7 are all 340, yet the rail is a live seam clamped `RailMinW 200 … RailMaxW 500` around `RailDefaultW 340` (`ShellResponsiveLayout.cs:126-127`), committed by `CommitRailDrag` (`WaveeShell.cs:453-458`), with 18 W12b's detent below the minimum — and the chapter had **no** rung table and no frame at either end. Verified against 0.2.9 and added as **W7b — the rail's width ladder**: a per-rung table (hero/deck side `round((railW−16)/4)·4` = 184 / 324 / 484, `NowPlayingPanel.cs:541-542`; pinned block 236 / 376 / 536; the deck's 4-DIP remount quantum; NPV title column `railW − 64`; the fitted cap 120 / 191.25 / 281.25 with the fit clamping UP at 200 against the splitter's 112.5 floor, `ShellResponsiveLayout.cs:130-158`; a **pinned** cap height that a rail resize never re-clamps; the video body's `(railW−24)·9/16` poster; queue identity lane `railW − 196` = **4 DIP at 200** and `railW − 154` classic / artwork-hidden, `QueuePanel.cs:606-690`; friends text lane `railW − 148`, `FriendsPanel.cs:104-142`; the player-style flyout's **fixed 340** overhanging a 200 rail, `PlayerStyleFlyout.cs:30`; `RailFits` recomputed live so a widening drag flips dock→float, `WaveeShell.cs:696-701`), three min-width frames (Details / Queue / Friends @ 200) and the "@ 500 nothing new appears" note. Also recorded the mechanism the table rests on: **`Shrink` defaults to 0 engine-wide**, so only four lanes flex (`NpvHeaderRow.cs:84-91`, `QueuePanel.cs:627`, `FriendsPanel.cs:125`, `NowPlayingPanel.cs:180`) and everything else is clipped — which is why the pills row's Autoplay pill is cut below ≈ 280 and the thin row's 120/80 bars overflow a 4-DIP lane. Three §3 rows updated (rail width, cap floors, hero art), one §5 motion row (the 4-DIP deck re-key), two §6.1 rows (the detent pointer, the mid-drag float flip), parity **87–91**. |
| 32 | **critic-fix: missing** | §2 (new **W16**), §3, §10 | **No stage frame for a track without lyrics.** The stage is not gated on the document: the rail's expand button is returned by BOTH arms of `LyricsHeaderKids`, the `available == 0` arm included (`RightRail.cs:382-390`), so "no lyrics" still opens a full stage. Added **W16 — Stage, lyrics pane, document absent / pending**: the single `Skel.Region`'s three arms (`LyricsView.cs:857-876`) — shimmer (`RowGap 18` large, `BarRadius 6`, `TextRatio 0.86`, `SkelReveal.FadeOnly`, `smoothResize: false`), the document, and `Message("No lyrics available")` centred on both axes at 14/20 in `_ink.Secondary` with `Pad(24,0,24,0)` (`:2522-2527`); the 🌐 `ScrimFab` absent because `ClearDocument` retires `LyricsPrefs.Available` to 0 *before* its early-out (`:1195-1202`) rather than letting the previous track's capability linger; nothing else collapsing (pane box, 700 column, 352 identity wrapper, entrance cascade, both pivot links); the hardcoded English literal (Ch.22 owns the fix); and the unsynced document as a distinct FOURTH arm. Two §3 rows added (no-document message, pending shimmer — `_ink.Skeleton` is `StageInk.SkeletonBar` on media and `Tok.FillSubtleSecondary` in the rail, one expression, `LyricsInk.cs:69`). Parity **92**. |
| 33 | **critic-fix: rejected** | §2 stage frames | "No stage frame **with video**" — there is none to draw. The immersive stage has **no video path at all**: `ImmersiveLyricsSurface`, `StageChrome`, `StageIdentity` and `StagePanes` contain zero references to video, and `DockedVideoHost.PageStage` names the module **watch page's** in-page 16:9 surface, not this stage (`DockedVideoHosting.cs:26`, `:36-44`, `:96-108`). The two docked hosts are the rail's card and that page stage; Ch.24 owns both. No frame added. |
| 34 | **critic-fix: rejected** | §2 stage frames | "No stage frame **per deck face**" — decks never reach the stage. `NpvDeck` has exactly one call site, the rail hero (`NowPlayingPanel.cs:546-547`); `StageIdentity`'s art is always `Surfaces.Artwork(track.Image, …)` at `L.ArtSize` (`:169-177`, `:252-260`), and neither `StageIdentity` nor `ImmersiveLyricsSurface` reads `NpvPlayerPrefs.Presentation`. The stage is presentation-blind by construction; W2 stays the only deck frame this chapter owes (Ch.23 owns the twelve faces). No frame added. |
| 35 | **critic-fix: rejected** | §9.5 | "Split the §2 allocation explicitly — `Rail.Styles.UI.cs`, `Rail.cs`, `Queue.cs`, `Stage.UI.cs`, `Stage.cs` have no lines" — they do. §9.5's honest-breakdown table already carries all seven files with per-file counts (1,400 / 600 / 200 / 900 / 250 / 1,150 / 500 = 5,000 after arbitration 2026-09-12; 400 / 4,900 before it), §9.3 #1-#3 argue each split, and §9.6 lists what the plan's own §2 tree is missing. The **2,400** figure is quoted as the PLAN's number being corrected, not as this chapter's allocation. No change. |
| 36 | **arbitration** | header · §9.3 #1 · §9.5 · §9.6 | arbitration 2026-09-12: **A12 gives the stage to `Shell/Stage.cs` (CORE) + `Shell/Stage.UI.cs`, owner K, Wave 4** — this chapter's own proposal, now settled and in the plan's tree, owning all six 0.2.9 stage files (`StageChrome` 363 · `StageIdentity` 706 · `StagePanes` 614 · `StageLayout` 353 · `StageArm` 143 · `StageInk` 82 = 2,261, re-counted). `StageInk` is named in `Stage.cs`'s contents (it was already homed there by §1.2 but omitted from the §9.5 row), so `Stage.cs` moves **400 → 500** and the honest total **4,900 → 5,000**. Chapter 22 drops `Shell/Lyrics.Stage.UI.cs`; the contested ≈500 pane lines are counted once, **here**, and the lyrics pane's body is `Lyrics.View` from `Lyrics.UI.cs`. **A13** settles the video split the §9.6 row asked for: `Shell/Video.{cs,UI.cs,Host.cs}` owner K Wave 4 (chapter 24), `Playback/Playback.Video.cs` the decode/host only (owner H, Wave 3), and the rail's video panel body stays in `Rail.UI.cs`. |

**Verified and left alone** (checked with file:line, found correct — recorded so the next auditor does not re-do it):
the whole §4.5 scrim ladder (0.76 / 0.46 plateau / 0.70, stops 0.22 & 0.62, hold 352/612, mid `+0.55·(1−hold)` at
`×0.34`, feather to exactly 0, `PaneShade` 0 → 0.096 → 0.24); the entire W14 height ladder recomputed from
`ColumnChromeH` (320 / 288 / 238 chrome, `WideEnterH` = 406, arts 300 / 172 / 184 / 192, demote at vpH 613); the
600 + 40 width hysteresis and 24-DIP fold reserve; `ColumnContentW` 304 and `VolumeTrackW` 264; every `Spacing` /
`Radii` / `MotionTok` / `WaveeMotion` / `Expressive` / `Elevation` / `WaveeOnMedia` value the chapter names
(`ControlFast` 150, `ControlNormal` 250, `Faster` 83, `Fast` 167, `Slow` 400, `DistBase` 8, `BlurSmall` 2,
`ScaleSubtle` 1.02/0.98, `ScaleEmphatic` 1.07/0.92, `Stroke` 58/255, glass 0.10/0.16, plate 0.14/0.22/0.28, scrim
190/220, `Elevation.Card` 8/2/#33 dark & 4/2/#1A light, `Flyout` 16/8/#42 & #24); the flyout's derived 74.5 / 66.5
and every option-row metric; `ThumbSize`, `CompactSeg` 28/12/56/5/4, swatch 30/22; the queue's authored extents
44 / 36 / 54 / 46; `QueueMovePlan`'s boundary rule; `QueueOrder`'s remove-then-insert; `RailVideoCoupling.BodyModeFor`;
`CanFitRail` = `sidebar + rail + 480` with `CompactRailW` = 56; `RowSwipe`'s leading=right / trailing=left mapping;
`SaveButton(uri, glyph 16, box 36)`; `EmptyState.Compact`'s `Gap 4 / Pad 24` and Standard-never-Accent button; the
`--fake` caveats (`Services.cs:325` `NullFriendActivityService`, `FakeData.cs:572-574` all-`QueueItemId.None`); and
every loc string the chapter quotes, checked against `assets/loc/en-US.json`.

**Residual, not fixed here** (out of this chapter's ownership):
- `TrackRow.ArtCard(kind: Rail)`'s own internals, `NowPlayingOverlay`, `TrackRow.Heart` and
  `TrackRow.ClassicExplicitBadge` are Ch.01's; this chapter now names where they appear but still does not specify
  them.
- `SeekBar` / `TimeText` metrics inside the stage's seek block (Ch.20). The chapter asserts "12" for the times; that
  number comes from `TimeText`, which this pass did not open.
- `NpvThumbnails`' twelve compositions are named only as "irreducible literal geometry"; parity #50 spot-checks three
  of them. A full per-preset spec belongs in Ch.23 with the decks.
- `LyricsView`'s behaviour on the stage (blur-by-distance, the wipe, click-to-seek) is Ch.22's; this chapter fixes
  only the column that holds it.

**token-reconcile (2026-09-12):** §11 item 27 stated the entrance cascade's ceiling as **360 ms**, repeating the stale `WaveeMotion.cs:105-106` source comment. The arithmetic is `StaggerCap 8 × StaggerMs 40` = **320 ms**, `EntranceStaggerTests.cs:52` pins 320, and `00-design-system.md §9.6` already records the comment as a drift the 0.3 port fixes. Corrected in place. The rest of the chapter's token citations re-verified: `WaveeSize.NavItemH` 44 / `PlayerBarH` 72 / `TrackRowH` 56, `WaveeColors.FileArea` / `FloatingChrome` / `FloatingPane` / `ContentSurface`, `Tok.MediaStage` `#0A0A0A`, `ScaleSubtle` 1.02/0.98 and `ScaleEmphatic` 1.07/0.92, `WaveeMotion.Faster/Fast/Standard` 83/167/250, and `Radii.Control` 4 / `Card` 8 / `Pill` 16.

**token-reconcile (2026-09-12):** second pass. `Tok.MediaLetterbox` and `Tok.FillControlAltSecondary`, both named here but missing from the first build of `00-design-system.md §12.1`, are now indexed there. No value in this chapter changed.

**answers 2026-09-12: Q5 (plan §9.6) confirms this chapter's own G6 data gap, exactly as it proposed.** The friends
edge and columns (`EdgeTable<FriendEdge> Friends`) land in Wave 1 with the other edges (`Edges.cs`, owner B); the
panel (`FriendsPanel` → `Rail.UI.cs`) stays in Wave 4 with the rail (owner K). No `Entities/Friends.cs`. G6's table
row and the friends-feed row in §7's data table are both marked resolved rather than open.
