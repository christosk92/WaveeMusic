# Media cards, shelves and small controls - 0.3 visual fidelity contract

> 0.2.9 sources: `Components/MediaCard.cs` (1570), `Components/Rail.cs` (112), `Components/FacePiles.cs` (153),
> `Components/ContentFilterChips.cs` (107), `Components/EmptyState.cs` (73), `Components/ErrorState.cs` (44),
> `Components/OfflineBanner.cs` (29), `Components/StatTile.cs` (57), `Components/RichText.cs` (246),
> `Components/NavPreview.cs` (103), `Components/FlipCountdown.cs` (172), `Components/PreReleaseCountdown.cs` (127),
> `Components/LikedSongsArtwork.cs` (85), `Actions/ContainerActions.cs` (364), `Design/WaveeCta.cs` (243),
> `Design/Surfaces.cs` (505), `Design/SearchHighlight.cs` (81), `Design/WaveeMotion.cs` (190),
> `Design/WaveeOnMedia.cs` (137) = **4,398 lines of primary surface** + call sites in
> `Features/{Home,Search,Browse,Detail,Recents,Modules}` | 0.3 target: `Card()` factories in each
> `Entities/X.UI.cs` + `Platform/Controls.cs` | Wave 4 owner L + Wave 5 owners M/N/O/P

Cross-references (do not re-specify here): `00-design-system.md` (tokens, type ramp, cover palette, motion curves,
CTAs), `01-track-row.md` (equalizer, save button, menus, drag chip), `03-detail-frame.md` (rail, trailing stack),
`10-home.md` / `11-home-cards-and-modules.md` (Home's own authored skins — `HomeCards.*`, which call
`MediaCard.ApplyCardPhysics` but are otherwise their own surface), `13-search-and-browse.md`,
`08-artist-and-discography.md`, `16-recents-and-history.md`, `07-liked-songs.md` (the liked cover *treatments*; this
chapter owns only the app-wide *funnel* into them).

Design-intent doc: `docs/plans/wavee/media-card-concepts.html`. **Drift (code wins):** the prototype's card is
radius 16 with a 12-radius cover, lifts `translateY(-5px)`, zooms art `scale(1.035)` over `.55s`, and floats a
44-DIP **white** play button that enters from `translateY(8px) scale(.92)`; the shipped card is radius 8 / cover
radius 8, lifts `-4`, zooms `1.04` over 300 ms, and its 44-DIP FAB is **accent-filled** and reveals by opacity only
(no travel, no entry scale). The prototype's 16 px/690 card title and its over-art `cover-title`/`cover-type` are
not shipped at all — the shipped title is the ramp's BodyStrong 14/20/600 under the cover.

---

## 0. The non-negotiables

1. **One hover plate, revealed — not a permanently drawn box.** Every rectangular media card rests *borderless and
   unfilled*; the plate (fill + 1 px stroke + `Elevation.Card`) fades in from `Opacity 0 -> 1` over 83 ms on hover
   (`MediaCard.cs:83-98`). A grid of cards at rest is a grid of covers and labels floating on the page, not a wall of
   boxes. This is the single most visible property of the surface.
2. **Card lift is -4 DIP, press is scale 0.99 + -1 DIP, and it is identical on every surface.**
   `MediaCard.ApplyCardPhysics` (`MediaCard.cs:68-74`) is the only place it is authored; Home's authored skins call
   it too. Two surfaces with different card physics is a regression.
3. **The shadow band never changes on hover.** `Elevation.Card` on *both* legs (`MediaCard.cs:93`) — a hovered card
   must not claim the flyout/tooltip blur band. The lift and the plate reveal carry the state change.
4. **The artwork zooms 1.04 inside the card's own rounded clip; the card root does not scale.**
   (`MediaCard.cs:1150-1156`.) A root `HoverScale` pushed outermost shelf cards past the viewport's exact-bounds clip
   and squared their corners.
5. **Shape is the type tell.** Artist = circle (`Radii.Full`, or `PersonPicture` initials when there is no photo);
   album/playlist/show = `Radii.Card` 8; video = 16:9 at `Radii.Control` 4. Home's Recents rail deliberately ships no
   subtitle explaining this (`HomeModules.cs:181-183`) — the shape explains itself.
6. **Play FAB bottom-right, "…" top-left-of-nothing / top-**right**, equalizer bottom-left.** Three fixed corners,
   8 DIP inset (`MediaCard.FabInset`), never rearranged per surface. The FAB reveals on hover; the equalizer shows
   whenever the card *relates* to what is playing, hover or not.
7. **A card with no play route renders no FAB.** `onPlay: null` -> no button at all (`MediaCard.cs:1345-1348`). A
   cursor-hand button that does nothing shipped once on the Home Recents rail and is called out in the code.
   **Caveat (0.2.9 signature gap):** only `MediaCard.Shelf` takes `Action? onPlay` (`:201`). `GridCard` (`:224`),
   `Row` (`:956`), `VideoCard` (`:882`) and `QuickPick` (`:921`) all take a **non-nullable** `Action`, so those four
   surfaces can never express "no play route" and always paint a FAB. In 0.3 every factory reads
   `Playback.CanPlay(handle)` — the nullability must be uniform, not shelf-only.
8. **A loading cover is that album's colour, never a grey hole.** `Surfaces.Shimmer` + `CoverColorPlane`: the tile
   under the image carries the cover's own graded tint at `TintStrength 0.55` and *breathes* 1.0<->0.5 over 1 s until
   the image resolves (`Surfaces.cs:66-71, 198-230, 453-497`).
9. **Text never contributes its intrinsic width to the parent measure.** Every clamped label carries either an
   explicit `Width = inner` (column context) or `Grow=1, Basis=0, MinWidth=0` (row context) — `MediaCard.cs:17-20`.
   Breaking this is what made text bleed out of cards and pushed grids past the viewport edge.
10. **Hover does not arm from a stationary pointer.** Every card owns a `HoverMotionGate`; the first
    `OnPointerMoveWithin` sample is a baseline, never a hover (`WaveeMotion.cs:141-159`). Back-navigating under a
    resting mouse must not pop the card that lands there.
11. **The empty/error grammar is display-face headline + one caption + at most one *quiet* action.** No glyph, ever;
    `Button.Standard`, never `Button.Accent` (`EmptyState.cs:8-29`). An error is an empty state with a reason.
12. **Shelf paging is chevrons in the header, an offset-driven edge fade on the strip, and 12 DIP of halo gutter on
    all four sides of the card row** (`PagedShelf.cs:257-272`). The fade *is* the overflow affordance; the gutters are
    what keep the first/last card's lift halo from being shaved by the viewport clip.
13. **Countdowns tick off the frame clock, not the wall clock.** `FlipCountdown` samples `DateTimeOffset.UtcNow` once
    and adds `FrameTime.NowMs` deltas (`FlipCountdown.cs:52-78`). A second wall-clock poll per tick is the stuck-timer
    bug (S3 #19) and must not come back.
14. **Liked Songs is decided by the URI alone.** `LikedSongsArtwork.For(uri, ...)` returns the user's chosen
    treatment for every liked spelling and deliberately ignores the provider's cover (`LikedSongsArtwork.cs:63-72`) —
    Spotify's recents feed serves a CDN copy of the very stock PNG the treatment replaces.
15. **A card subtitle is HTML.** Playlist/album blurbs carry `<a href="spotify:...">` and `<b>`; `RichText` parses
    them into one shaped paragraph with accent, individually-clickable links (`RichText.cs:19-31, 65-75`). Rendering
    the raw tag at the user is a shipped-and-fixed defect.

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
MediaCard (static factories)                                    Components/MediaCard.cs:21
├── ApplyCardPhysics(BoxEl)                                      :68   THE card motion contract (lift/press/elevate)
├── CardShell(content, onClick, plateFill?, persistent?)          :76   ZStack[ hover plate, content ], radius 8
│    └── plate BoxEl                                             :83   fill+1px stroke+Elevation.Card, Opacity 0->1
├── MoreCorner(show, persistent?)                                :108  top-right 30-circle "…" on media scrim
├── MoreInline(show, onDark, size=36)                            :135  inline "…" circle — ONLY live caller is SearchHero
│                                                                      (`SearchHero.cs:72`, onDark=false). The onDark:true
│                                                                      arm has one call site, EditorialCardCore (:802) = DEAD.
├── KindChip(HomeCardKind)                        DEAD (only :802) :152 capsule on media scrim, Eyebrow + on-media ink
├── RowChip(text)                                                :1065 capsule on FillSubtleSecondary, tertiary ink
├── ArtworkOrLiked(cover,uri,w,h,r,morphKey,decodePx,diag)       :172  liked funnel -> Surfaces.Artwork
├── PlayFab(onClick, glyph, size=44)                             :1077 accent circle, glyph 0.42*size, BlocksDragArm
├── CoverActionFab(onClick, glyph, tooltip, size)                :1091 -> CoverActionFabCore (scrim circle + tooltip)
├── FabGlyph(glyph, size, color)                                 :1094 TextEl in Theme.IconFont, square box
├── LazyOverlay(hovered,uri,onPlay,fab,cover,inner,onNav,center) :35   -> LazyNowPlayingOverlay
├── Shelf(...)            -> ShelfCard : Component               :200 / :1107
│    ├── gutter BoxEl  Padding(0,4,0,2)                          :1205
│    └── CardShell
│         └── content  Direction=1 Gap=8 Padding(8,8,8,12)       :1169
│              ├── coverStack  ZStack, HoverScale 1.04           :1145
│              │    ├── face: PersonPicture | LikedSongsArtwork  :1123-1143
│              │    │         | Surfaces.Mosaic | ZStack(Shimmer, Image@256)
│              │    ├── LazyOverlay(fab 44, cover, inner)        :1164
│              │    └── MoreCorner(menu != null)                 :1165
│              └── labels  Direction=1 Gap=2 Align=(circ?Ctr:Start)  :1180
│                   ├── WaveeType.TrackTitle  Width=inner, 1 line     :1187
│                   └── RichText.Of(sub, 12, ..., inner, 2 lines)     :1190
├── ShelfHeight(cardW) => cardW + 72                             :212  THE virtualized shelf's cross extent
├── GridCard(...)                                                :223  width-agnostic twin of Shelf
│    └── CardShell(content) with { Draggable, move/exit handlers }:282
│         └── content Direction=1 Gap=8 Padding(8,8,8,12)        :250
│              ├── coverStack ZStack ClipToBounds=!circular      :231
│              │    ├── LikedSongsArtwork.Fill | Surfaces.ArtworkFill(r)  :242
│              │    ├── LazyOverlay(fab 44, cover, inner=0, onNavigate)   :243
│              │    └── MoreCorner(menu != null)                          :244
│              └── labels Direction=1 Gap=2                      :258
│                   ├── SearchHighlight.Row | WaveeType.TrackTitle (titleLines)  :265-273
│                   └── WaveeType.TrackMeta (1 line; omitted when empty)          :274
├── Row(...)                                                     :955  the horizontal media row
│    ├── coverStack 48 (or 84 large), ZStack ClipToBounds        :973
│    │    ├── ArtworkOrLiked(morphKey)                           :981
│    │    └── LazyOverlay(fab 30|44, cover, art, centered:true)  :982
│    ├── leadingArtwork?(coverStack)  (Recents "Saved" frame)    :985
│    ├── text column Grow=1 Basis=0 Gap=2 (large: 8)             :1005
│    │    ├── Eyebrow? (eyebrowColor)                            :987
│    │    ├── PageHero (large) | TrackTitle                      :990
│    │    ├── RichText.OfRow(subtitle, 12, onSubtitleNav)        :995
│    │    ├── meta?  Caption/600 TextPrimary  | metaContent      :997
│    │    └── detail? Caption TextTertiary, 2 lines wrap         :1000
│    ├── RowChip(typeChip)?                                      :1010
│    ├── trailing?  (FollowButton | SaveButton | Recents cluster) :1011
│    └── belowArt arm: Direction=1, MinHeight 72, second block    :1012-1037
├── VideoCard(...)                                               :881  16:9 thumb + title + duration
├── ArtistPick(pinned, ...)                                      :306  the tone panel
│    ├── wash gradient (accent .16 -> .05@.55 -> 0@.85)          :317
│    ├── head: PersonPicture 32 + BodyStrong + Caption           :325
│    ├── quote: WaveeType.PickQuote, 4 lines                     :354
│    ├── photo? 150 tall (vertical) / 300 wide (horizontal)      :370
│    └── PickFootRow: 44 cover + title + kind(+accent date) + CTA :425
│         └── trailing = WaveeCta.Play(accent) | PreSaveButton keyed on uri :393
├── QuickPick(...)                        DEAD (no call site)    :921
├── EditorialCard / EditorialCardCore     DEAD (no call site)    :489 / :512
└── WideEditorialDestination(...)         DEAD pass-through to ConcertUi :44

LazyNowPlayingOverlay : Component                                :1294  eager FAB + lazy equalizer
NowPlayingOverlay     : Component                                :1374  the reactive relate/own state model
 ├── FabReveal(toggle, playingHere, fab, cover, inner, nav, ctr) :1407  3 layouts: centred-on-art / cover-corner / inline
 ├── Toggle(bridge, uri, onPlay)                                 :1390  pause only when this card OWNS playback
 ├── Persistent / Light arm            DEAD (only :694 = Editorial) :1482 48-dia always-on FAB, glyph 0.38*size,
 │                                                                       WaveeOnMedia.LightButton ramp, ToolTip.Wrap
 └── EqPill(pauseOnHover)                                        :1505  scrim pill + WaveeEqualizer.Of(…, 14)
CoverActionFabCore : Component                                   :1209  ToolTip.WrapStable over a scrim circle
CardLibraryAction  : Component                                   :1240  40-circle heart/follow (EditorialCard only: DEAD)

Rail : Component                          DEAD (no call site)    Components/Rail.cs:11
FacePiles (static)                                               Components/FacePiles.cs:21
 ├── SlotsIn(width) / VisibleFaces(width, total)                  :40 / :46   PURE
 ├── Strip(faces, maxVisible, overflow, onOverflow, tip)          :73
 ├── AvatarFrame(face, index, first)                              :88
 └── OverflowFrame(n, first, onClick, tip)                        :120
ContentFilterChips (static)                                      Components/ContentFilterChips.cs:24
 ├── Derive(tracks) -> ContentFilterChipSet                       :29
 ├── Build(chips, selected, select, allLabel, scrollKey)          :47   horizontal ScrollView + AutoEdgeFade
 └── Chip(label, selected, available, onClick)                    :79
EmptyState.Build / .Compact / .Default / .Centered               EmptyState.cs:34/:43/:66/:68
ErrorState.Build / .Compact                                      ErrorState.cs:17/:33  -> EmptyState + a log line
OfflineBanner.Build                                              OfflineBanner.cs:12
StatTile.Create(key, value, caption, wrapValue, layout, trail)   StatTile.cs:21
RichText.Of / .OfFlex / .OfRow / .Expandable / .ExpandableFlex   RichText.cs:21/:36/:65/:50/:56
 ├── Parse(s, linkColor, onNavUri)                                :107
 ├── RouteForUri(uri)                                             :80   PURE
 └── ExpandableRichText : Component                               :157
FlipCountdown : Component                                        FlipCountdown.cs:26
PreReleaseCountdown : Component                                  PreReleaseCountdown.cs:27
LikedSongsArtwork.Cover/.Dynamic/.Fitted/.For/.Fill              LikedSongsArtwork.cs:21/:37/:48/:71/:81
NavPreviewStore / DetailPreview / DetailNav                      NavPreview.cs:15/:26/:88
SearchHighlight.Row(text, start, len, size, weight, colour, max) Design/SearchHighlight.cs:18

The SHELF is not Rail.cs. It is the engine's PagedShelf (FluentGpu.Controls/PagedShelf.cs:88), configured per call.
```

### 1.2 The 0.3 tree

| 0.2.9 node | 0.3 home | Kind | Inputs (how data reaches it) |
|---|---|---|---|
| `MediaCard.ApplyCardPhysics` | `Platform/Controls.cs` — `Controls.CardPhysics(BoxEl)` | static fn | none (pure `with`) |
| `MediaCard.CardShell` | `Platform/Controls.cs` — `Controls.CardShell(Element content, Action onClick)` | static fn | `content` Element, `onClick` |
| `MoreCorner` / `MoreInline` / `KindChip` / `RowChip` | `Platform/Controls.cs` | static fns | `bool show`, `MenuModel?` |
| `PlayFab` / `CoverActionFab` / `FabGlyph` | `Platform/Controls.cs` | static fns + one `Component` for the tooltip arm | `Action`, glyph `StringId`, size |
| `LazyNowPlayingOverlay` / `NowPlayingOverlay` / `FabReveal` / `Toggle` | `Platform/Controls.cs` — `Controls.NowPlayingOverlay` | `Component` with a **props record** | `Props(IReadSignal<bool> Hovered, EntityUri Uri, Action? OnPlay, float Fab, bool Cover, float Inner, Action? OnNav, bool Centered)`; playback identity read through `Playback.Current` / `Playback.IsPlaying` signals, **never** a frozen bool |
| `ShelfCard : Component` | `Platform/Controls.cs` — `Controls.ShelfCard` | `Component` + props record | `Props(EntityHandleRef Handle, string? SubtitleOverride, float CardW, bool Circular, Action OnClick, Action? OnPlay, MenuModel? Menu, DragSource? Drag)` — **props re-pushed per bind**, `_hovered` mount-stable field |
| `MediaCard.Shelf(...)` per kind | `Entities/Album.UI.cs`, `Artist.UI.cs`, `Playlist.UI.cs`, `Show.UI.cs` — `public static Element Card(this Album a, float cardW, CardStyle s)` | static adapter | reads the handle's columns *at build time*; re-binds through the shelf's `BoundItemScope<T>` |
| `MediaCard.GridCard(...)` | same files — `Card(..., CardStyle.Grid)` | static adapter | same |
| `MediaCard.Row(...)` | same files — `ListRow(this Album a, RowStyle s)`; the *shared* body in `Platform/Controls.cs` | static adapter + shared fn | same |
| `MediaCard.VideoCard` | `Entities/Track.UI.cs` (a music video stands for its track) | static adapter | `Track` handle + `TrackFields.Video` |
| `MediaCard.ArtistPick` / `PickFootRow` | `Entities/Artist.UI.cs` | static fn | `Artist` handle + the pinned-item columns (see DATA GAPS) |
| `MediaCard.ShelfHeight` | `Platform/Controls.cs` — **CORE section**, `Controls.ShelfHeight(float) => cardW + 72f` | pure | — |
| `FacePiles` | `Platform/Controls.cs` (**CORE section** for `SlotsIn`/`VisibleFaces`) | static | `ReadOnlySpan<Face>` built from `Edges.TrackArtists` / a ranked list |
| `ContentFilterChips` | `Platform/Controls.cs` + `Entities/User.UI.cs` (liked) | static | `ContentFilterChipSet`, selected `StringId?`, `Action<StringId?>` |
| `EmptyState` / `ErrorState` / `OfflineBanner` | `Platform/Controls.cs` | static fns | strings (loc keys) + optional `Action` |
| `StatTile` | `Platform/Controls.cs` | static fn | `key`, `value`, `caption` strings |
| `RichText` | `Platform/Controls.cs` (**CORE section** for `Parse` + `RouteForUri`) | static + one `Component` for the expandable arm | `string? html` — or a pre-parsed `StringId` span list; see DATA GAPS |
| `FlipCountdown` | `Platform/Controls.cs` | `Component`, **Key = `ExpiresAtMs`** | `long ExpiresAtMs`, `Func<ColorF> Accent` |
| `PreReleaseCountdown` | `Platform/Controls.cs` | `Component`, **Key = `ReleaseAt`** | `DateTimeOffset ReleaseAt`, `Func<ColorF> Accent`, `bool Bare` |
| `LikedSongsArtwork.For/.Fill` | `Entities/User.UI.cs` (the funnel) — treatments in `07-liked-songs.md` | static | `EntityUri` + slot geometry |
| `SearchHighlight.Row` | `Platform/Controls.cs` | static | text + match span |
| `NavPreviewStore` / `DetailPreview` / `DetailNav` | **DELETED.** A 0.3 handle *is* the shared model — a card click navigates to the same `Album`/`Playlist` slot the card painted, so there is nothing to stash. | — | — |

**Props freeze at mount — what must be a Signal / Func / Key in 0.3:**

| Value | Channel | Why |
|---|---|---|
| `hovered` for the play FAB / equalizer | `Signal<bool>` owned by the card `Component` (mount-stable field) | a fresh `Signal` per factory call defeats the overlay's props equality gate (`MediaCard.cs:203-207`) |
| uri / title / onPlay / onClick / cardW / circular / menu / drag on a **virtualized** card | **props record, re-pushed** (`UseProps<Props>()`) | virtualized parents reuse `ComponentEl` slots; a ctor-captured uri keeps pointing at the slot's FIRST row (`MediaCard.cs:29-34`) |
| playback relation (`relates`, `playingHere`) | `UseComputed` over `Playback.HasActiveContext` then `Playback.Identity` — read the **coarse bool first and bail** | an idle overlay must not join the hot identity fan-out (`MediaCard.cs:1319-1335`) |
| accent for `FlipCountdown` / `PreReleaseCountdown` / `ArtistPick` | `Func<ColorF>` read **inside** `Render` | the palette lands after mount; a value would freeze at the semantic default |
| `ExpiresAtMs` / `ReleaseAt` | **`Key`** on the embed | the countdown's anchor is frozen at mount; a new window must remount |
| `DragSource` | frozen ctor field is **correct** | a `DragSource` is gesture-COLD config: a kind + a payload *factory* that runs once at promotion (`MediaCard.cs:485-488`) |
| liked cover geometry (`size`, `radius`, `morphKey`) | **`Key`** = `"liked-cover:{size}:{radius}:{morph}"` | the treatment freezes its canvas at mount (`LikedSongsArtwork.cs:37-41`) |
| `RichText.Expandable` body | **`Key`** = `"rich-expand:{ctx}:{html}:{width}:{lines}"` | `RichText.cs:53` |

---

## 2. Wireframes

Scale is stated per wireframe. Card-level drawings: **1 char ≈ 8 DIP** horizontally, **1 row ≈ 16 DIP**.
Shelf-level drawings: **1 char ≈ 16 DIP**.

### W1 - Shelf card, resting, square (album/playlist) @ cardW 173.3 (shelf 1100)

```
scale: 1 char = 8 DIP
        <------------------- 173.3 -------------------->
   ^    ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·      4  gutter top   (Spacing.XS)
   |    ┌───────────────────────────────────────────────┐   card root: radius 8, NO fill, NO stroke, NO shadow
   |    │                                               │   ^ 8 pad
   |    │   ┌───────────────────────────────────────┐   │
   |    │   │                                       │   │   cover 157.3 x 157.3, radius 8, ClipToBounds
   |    │   │              c o v e r                │   │   decode 256 px square, ImageFit.Cover
 245.3  │   │          (Shimmer tile beneath)       │   │   shimmer = cover tint @0.55 over neutral, breathing
   |    │   │                                       │   │
   |    │   └───────────────────────────────────────┘   │
   |    │                                               │   8  content gap (Spacing.S)
   |    │   Album or playlist title…                    │   BodyStrong 14/20/600, Width=157.3, 1 line, ellipsis
   |    │                                               │   2  (Spacing.XXS)
   |    │   Artist · 2024 second line wraps to two       │   Caption 12/16, Width=157.3, 2 lines, ellipsis
   |    │   lines and then ellipsises…                  │
   |    │                                               │   12 pad bottom (Spacing.M)
   |    └───────────────────────────────────────────────┘
   v    ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·      2  gutter bottom (Spacing.XXS)

Height = 4 + 8 + (cardW-16) + 8 + 20 + 2 + 32 + 12 + 2  =  cardW + 72   (MediaCard.cs:212)
```

### W2 - Shelf card, hovered @ cardW 173.3

```
scale: 1 char = 8 DIP                                          all deltas vs W1:
        ╔═══════════════════════════════════════════════╗   plate: Opacity 0 -> 1 over 83 ms (FluentStandard)
        ║ ┌───────────────────────────────────────────┐ ║     Fill  Tok.FillCardDefault  (dark #0DFFFFFF / light #B3FFFFFF)
        ║ │                                           │ ║     Stroke 1 px Tok.StrokeCardDefault (#19000000 / #0F000000)
        ║ │            c o v e r   1.04x              │ ║     Shadow Elevation.Card  (dark 0/2/8 #33000000, light 0/2/4 #1A000000)
        ║ │                         ╭──╮  (…)  30 dia │ ║   whole card: OffsetY -4, HoverElevatePaint = true
        ║ │                         ╰──╯              │ ║   cover: HoverScale 1.04 over 300 ms FluentDecelerate
        ║ │   ╭────╮                          ╭─────╮ │ ║   "…" corner: Opacity 0->1, 167 ms FluentDecelerate
        ║ │   │▮▮▮ │  eq pill (only if relates)│  ▶  │ │ ║   play FAB 44 dia: Opacity 0->1, 167 ms FluentDecelerate
        ║ │   ╰────╯                          ╰─────╯ │ ║     Fill Tok.AccentDefault, glyph E768 @ 18.5, on-accent ink
        ║ └───────────────────────────────────────────┘ ║   FAB inset 8 from bottom + right; eq inset 8 from bottom + left
        ║   Album or playlist title…                    ║   "…" inset 8 from top + right
        ║   Artist · 2024 …                             ║
        ╚═══════════════════════════════════════════════╝
```

### W3 - Shelf card, circular (artist) @ cardW 173.3

```
scale: 1 char = 8 DIP
        ┌───────────────────────────────────────────────┐
        │            ╭─────────────────────╮            │  cover is a CIRCLE: r = inner/2 = 78.65
        │          ╭─┘                     └─╮          │  ClipToBounds is FALSE on the stack (MediaCard.cs:1150)
        │         │        p o r t r a i t    │         │  so the FAB / "…" are not shorn by the avatar circle
        │         │   (PersonPicture initials │         │  no photo -> PersonPicture initials + contact fallback
        │         │    when there is no URL)  │   ╭───╮ │  play FAB still sits at the RECTANGLE's bottom-right
        │          ╰─╮                     ╭─╯   │ ▶ │ │
        │            ╰─────────────────────╯     ╰───╯ │
        │                 Artist name                   │  labels are CENTRED (AlignItems.Center) when circular
        │                    Artist                     │
        └───────────────────────────────────────────────┘
```

### W4 - Virtualized shelf, fully loaded @ viewport 1100 (Home / Browse / Search)

```
scale: 1 char = 16 DIP
   ┌─ header row ─ Direction 0, AlignItems Center, Gap 8 ────────────────────────────────────────┐
   │ Made for you                                                        ( ‹ 32 )  ( › 32 )      │  header: caller-supplied
   └──────────────────────────────────────────────────────────────────────────────────────────────┘  chevrons 32x32 circle
                                                                    ^ 12 header gap MINUS 12 LiftClearance = 0 on-screen gap
   ┌─ PartViewport ─ ClipToBounds, widened 2x12 by a negative margin, AutoEdgeFade band 24 ──────┐
   ┆ 12 │ ┌──────────┐ 12 ┌──────────┐ 12 ┌──────────┐ 12 ┌──────────┐ 12 ┌──────────┐ 12 ┌────  ┆  6 cards @ 173.3
   ┆    │ │  cover   │    │  cover   │    │  cover   │    │  cover   │    │  cover   │    │      ┆  gap 12 (Spacing.M)
   ┆ fade │          │    │          │    │          │    │          │    │          │    │ fade ┆  lead/trail inset 12
   ┆ 24  │ └──────────┘    └──────────┘    └──────────┘    └──────────┘    └──────────┘    └───  ┆
   ┆     │  Title          Title          Title          Title          Title          Titl     ┆
   ┆     │  Subtitle       Subtitle       Subtitle       Subtitle       Subtitle       Subt     ┆
   └──────────────────────────────────────────────────────────────────────────────────────────────┘
     ^12 LiftClearance (top pad, inside the clip)          ^12 ShadowClearance (bottom pad, inside the clip)
   viewport height = ShelfHeight(173.3) + 12 + 12 = 269.3

Fit (FillRowVirtualLayout.Fit, VirtualLayout.cs:485):
   perPage = floor((W + 12) / (148 + 12));  cardW = (W - (perPage-1)*12) / perPage
   while cardW > 188: perPage++ and recompute;  cardW = min(cardW, 188)
   W=320 -> 2 @ 154.0   W=420 -> 3 @ 132.0   W=480 -> 3 @ 152.0   W=600 -> 4 @ 141.0
   W=800 -> 5 @ 150.4   W=968 -> 6 @ 151.3   W=1100 -> 6 @ 173.3  W=1240 -> 7 @ 166.9   W=1400 -> 8 @ 164.5
There is no hysteresis on this fit; it is a pure function of the measured width and re-fits live.
```

### W5 - Shelf, skeleton / loading (SkeletonProxy)

```
scale: 1 char = 16 DIP
   Made for you                                                        ( ‹ )  ( › )
   ┌──────────┐ 12 ┌──────────┐ 12 ┌──────────┐ 12 ┌──────────┐ 12 ┌──────────┐ 12 ┌──────────┐
   │▒▒▒▒▒▒▒▒▒▒│    │▒▒▒▒▒▒▒▒▒▒│    │▒▒▒▒▒▒▒▒▒▒│    │▒▒▒▒▒▒▒▒▒▒│    │▒▒▒▒▒▒▒▒▒▒│    │▒▒▒▒▒▒▒▒▒▒│
   │▒ cover ▒▒│    │          │    │          │    │          │    │          │    │          │
   └──────────┘    └──────────┘    └──────────┘    └──────────┘    └──────────┘    └──────────┘
   ▒▒▒▒▒▒▒▒▒         ▒▒▒▒▒▒▒▒        ▒▒▒▒▒▒▒▒        ▒▒▒▒▒▒▒▒        ▒▒▒▒▒▒▒▒        ▒▒▒▒▒▒▒▒
   ▒▒▒▒▒▒            ▒▒▒▒▒           ▒▒▒▒▒           ▒▒▒▒▒           ▒▒▒▒▒           ▒▒▒▒▒

 - PagedShelf derives up to 6 REAL cards fitted to the MEASURED slot (PagedShelf.cs:151-176). Never one
   sentinel-wide card: that reflows into the real columns the moment data lands.
 - Inside a card, the hover-only affordances are Skeletonized(false) and contribute nothing (MediaCard.cs:40, :132, :149).
 - Surfaces.Shimmer is also Skeletonized(false) so the derived cover square comes from the paired Image (Surfaces.cs:228).
 - BrowsePage builds its own fixed shimmer card at ShelfCardMin x ShelfCardHeight(ShelfCardMin) = 148 x 220
   (BrowsePage.cs:303-317): cover square FillSubtleSecondary radius 8, then a CardTitle and a TrackMeta ghost line.
```

### W6 - Grid card (Home "see all" / Browse category / Search facet) @ cell 151.3, 1 title line

```
scale: 1 char = 8 DIP
        <------------------- 151.3 (cell) ------------->
        ┌───────────────────────────────────────────┐       card root: radius 8, resting plate invisible
        │                                           │  8 pad
        │  ┌─────────────────────────────────────┐  │
        │  │                                     │  │  cover fills the cell: Surfaces.ArtworkFill,
        │  │        c o v e r  (aspect 1)        │  │  CSS aspect-ratio 1, decodePx 256, radius 8
        │  │                                     │  │  (circular -> Radii.Full, and the stack is NOT clipped)
        │  └─────────────────────────────────────┘  │
        │                                           │  8 gap
        │  Card title on one line…                  │  BodyStrong 14/20/600, MinWidth 0, NoWrap, ellipsis
        │  Subtitle                                 │  2 gap; Caption 12/16, 1 line  (omitted when empty)
        │                                           │  12 pad bottom
        └───────────────────────────────────────────┘

Cell reserve (AspectGridVirtualLayout ExtraHeight) = HomeModuleLayout.GridCardChromeFor(titleLines, hasSubtitle)
    = 14 (GridLabelOverhead) + titleLines*20 + (hasSubtitle ? 18 : 0)                 HomeModules.cs:507-536
    1 line + subtitle = 52   |  2 lines + subtitle = 72  |  2 lines, no subtitle (Charts) = 54
Column count = FillRowVirtualLayout.Fit(width, 148, 188, 12).PerPage — the FIT decides COLUMNS ONLY; the card
then fills the arranged cell (HomeModules.cs:428-437). Never a separately-fitted cardW: that shear ellipsised
titles into "Netherla…".

A GRID CARD NEVER MOSAICS. `Surfaces.ArtworkFill` collapses a cover-less playlist's `MosaicTiles` to
`tiles[0]` — one album cover, not the 2x2 (Surfaces.cs:295-296) — because the fill cell has no known width to
compose a mosaic at. A SHELF card paints the full 2x2 (MediaCard.cs:1134-1135) and `Surfaces.Artwork` (rows,
the pick foot) mosaics at >= 4 tiles and falls back to `tiles[0]` at 1-3 (Surfaces.cs:241-245). So the SAME
cover-less playlist reads as a quad on Home's shelf and as a single cover in its "see all" grid. That is shipped
0.2.9 behaviour, not a bug — but in 0.3 the GAP-2 edge makes the mosaic cheap at any width, so state the intent.

The grid card does **not** `Grow` (MediaCard.cs:247-249): a grid row may reserve trailing space as its vertical
gutter, and growing into it creates a dead footer and eats the gap before the next row. The SHELF card's shell
carries `Grow = 1` (:1199) for the opposite reason — a measured shelf stretches every card to the tallest.
Its `hovered` signal is a **fresh `Signal` per factory call** (:228), not a mount-stable field like ShelfCard's —
which is exactly the defect the props table below names. Fix it in 0.3: `Controls.GridCard` is a `Component` too.
Drag is null for `Track`/`Episode` kinds (HomeModules.cs:444-448); every other kind is a resource drag source.
```

### W7 - Grid card, 2 title lines with a search-highlight pill (Charts)

```
scale: 1 char = 8 DIP
        ┌───────────────────────────────────────────┐
        │  ┌─────────────────────────────────────┐  │
        │  │            c o v e r                │  │
        │  └─────────────────────────────────────┘  │
        │  Songs from the ▛▀▀▀▀▀▀▀▀▛              │  SearchHighlight.Row: the matched run sits in a
        │  ▛Netherla▟nds 2024                      │  Radii.Control 4 pill, Padding 3/1/3/1,
        │                                           │  Fill Tok.AccentSelectedTextBackground,
        └───────────────────────────────────────────┘  ink Tok.TextOnAccentSelectedText.
 The run row WRAPS (flex wrap between runs), capped at maxLines * LineBoxFor(14) = 2 * 20 = 40 DIP
 (SearchHighlight.cs:60-68). Charts BLANK the subtitle, so the reserve is 54, not 72.
```

### W8 - Grid card, discography drawer OWNER (expanded)

```
scale: 1 char = 8 DIP
        ╔═══════════════════════════════════════════╗   BorderWidth 2 (not 1), BorderColor = the page accent,
        ║  ┌─────────────────────────────────────┐  ║   Fill forced to Tok.FillCardDefault — the plate is no
        ║  │            c o v e r          ╭──╮  │  ║   longer hover-gated while this card owns the drawer
        ║  │                               │ ⧉│  │  ║   (ArtistPage.AlbumExpand.cs:542-547)
        ║  │                               ╰──╯  │  ║   TWO FABs, STACKED VERTICALLY — CoverActionFab 36
        ║  │                                8    │  ║   ("Go to album", Icons.OpenInNewWindow E8A7) ABOVE
        ║  │                               ╭───╮ │  ║   PlayFab 44, Gap 8, both right-aligned and pinned
        ║  │                               │ ▶ │ │  ║   to the cover's BOTTOM-right.
        ║  │                               ╰───╯ │  ║   FabReveal's cover arm is Direction=1 (COLUMN),
        ║  └─────────────────────────────────────┘  ║   Justify=End, AlignItems=End (MediaCard.cs:1430-1436).
        ║  Album name                               ║   Height forced to cardW + 50 (CardChrome) so every
        ║  12 Mar 2024 · 12 songs                   ║   cell is uniform and the drawer's hug spacing is exact
        ╚═══════════════════════════════════════════╝   grid: minCol 180, gap 16, rowExtra 50 + 20
                     ▲ caret 16 x 8 at the drawer's top edge (see 08-artist-and-discography.md)

 The whole two-FAB column shares ONE hover-revealed wrapper (Opacity 0 -> 1, 167 ms FluentDecelerate), so both
 buttons fade together. The card's own `onClick` TOGGLES the drawer; navigating to the album is the ⧉ FAB's job
 (ArtistPage.AlbumExpand.cs:535-538) — the only card in the app whose click does not open its target.
 `accent:` is passed here (`AlbumExpand.cs:539`) and **GridCard ignores it** — see §4.5.
```

### W9 - Media row, plain (`plated: false`) @ 64 DIP - Search results, Recents

```
scale: 1 char = 8 DIP                                      width = the list column
   ┌──────────────────────────────────────────────────────────────────────────────────────────┐  Height 64, radius 4
   │ 8 ┌────────┐ 12  Title of the thing                                       [chip] [save]  │  rest Fill TRANSPARENT
   │   │  48    │     Song · Artist name, Artist two                                          │  hover FillSubtleSecondary
   │   │ cover  │                                                                             │  press FillSubtleTertiary
   │   └────────┘                                                                          8  │  no border, no shadow
   └──────────────────────────────────────────────────────────────────────────────────────────┘
     art 48 (WaveeSize.Thumb48), radius 4 (Radii.Control) or 24 when circular
     text column: Grow 1, Basis 0, MinWidth 0, Gap 2
        title    BodyStrong 14/20/600, NoWrap, 1 line, ellipsis
        subtitle RichText.OfRow 12/16 TextSecondary; <a> runs -> Tok.AccentTextPrimary, clickable, own hit test
     typeChip (RowChip): capsule, Padding 8/4/8/4, Fill FillSubtleSecondary, Eyebrow 12/16/600 tracking 30, TextTertiary
     trailing: FollowButton | SaveButton (32 circle, HoverFill FillSubtleSecondary, HoverScale 1.07, glyph 16) | caller
     eyebrow (optional, above the title): WaveeType.Eyebrow, 1 line, ellipsis. THREE colours, not one —
        factory default `Tok.TextSecondary` (MediaCard.cs:989); Search passes `Tok.AccentTextPrimary` for a
        "Lyrics match" and `WaveeColors.PremiumText` for an AccessLabel ("Included in Premium") (SearchPage.cs:775-776).
```

### W9b - Media row, `showArtwork: false` (the Appearance setting)

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────────────────────────────────────────────────┐  Height 64, radius 4
   │ 8  Title of the thing                                                     [chip] [save]  │  NO cover, NO overlay,
   │    Song · Artist name, Artist two                                                        │  NO play FAB at all
   └──────────────────────────────────────────────────────────────────────────────────────────┘  (the leading child is
                                                                                                  simply not added)
   Driven by `WaveeSettings.HideTrackArtwork` — Settings ▸ Appearance ▸ Lists ▸ "hideTrackArtwork"
   (SettingsCatalog.cs:77), read through `AppearancePrefs.TrackArtworkHidden(settings)` (AppearancePrefs.cs:17).
   Search applies it to TRACK rows only: `showArtwork: !isTrack || !hideTrackArtwork` (SearchPage.cs:784, :660).
   An artist/album/playlist row keeps its cover regardless. The row's Gap 12 collapses with the art, so the title
   column starts at the row's own 8 padding.
   **0.3:** this is a live settings signal, not a mount-frozen bool — `Settings.HideTrackArtwork` read inside Render,
   or the row never responds to the toggle without a page remount.
```

### W10 - Media row, plated (`plated: true`, the default)

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────────────────────────────────────────────────┐  Height 64, radius 8
   │ ┌────────┐   Title                                                                       │  Fill FillCardSecondary
   │ │  48    │   Subtitle                                                                    │  hover FillCardDefault
   │ └────────┘                                                                               │  press FillSubtleTertiary
   └──────────────────────────────────────────────────────────────────────────────────────────┘  border 1 StrokeCardDefault
   Used by: DetailTrailing "Featured on" / "More by" / "Similar albums" (DetailTrailing.cs:462, :472).
   Search and Recents pass plated:false (W9).
```

### W11 - Media row, hovered (art overlay, `centered: true`)

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────────────────────────────────────────────────┐
   │ ┌────────┐   Title                                                                       │  the ROW is the
   │ │▒▒╭──╮▒▒│   Subtitle                                                                    │  interactive ancestor;
   │ │▒▒│ ▶│▒▒│   <- a full-cover veil (WaveeOnMedia.CoverScrim, black @ 110/255) fades in     │  the FAB resolves off
   │ │▒▒╰──╯▒▒│      with a CENTRED 30-dia accent FAB on top (44 when large)                   │  ROW hover
   │ └────────┘                                                                               │
   └──────────────────────────────────────────────────────────────────────────────────────────┘
   At rest and RELATED to playback: the equalizer pill sits CENTRED in the same 48 box and is
   hidden on hover (HoverOpacity 0) so the FAB takes over (MediaCard.cs:1515-1528).
```

### W12 - Media row, `large: true` (Search "Top result" hero, `plated: false`)

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────────────────────────────────────────────────────┐  Height 112
   │ 16 ┌──────────────┐ 16  Artist Name                                     [ Follow ]        16 │  Padding 16/12/16/12
   │    │              │     Artist                                                               │  Gap 16 (Spacing.L)
   │    │   84 x 84    │                                                                          │  art 84, radius 8
   │    │  radius 8    │     title  = WaveeType.PageHero  (Ui.Title 28/36/600), 1 line, ellipsis   │  (circular -> 42)
   │    │  (42 circ)   │     text column Gap 8 (not 2)                                             │  fab 44 (not 30)
   │    └──────────────┘                                                                          │  meta + detail
   └──────────────────────────────────────────────────────────────────────────────────────────────┘  suppressed
   plated + large press fill is FillCardDefault, not FillSubtleTertiary (MediaCard.cs:1053).
   `large` suppresses `meta` and `detail` IN THE FACTORY (`hasMeta`/`hasDetail` are `!large`-gated, MediaCard.cs:970-971).
   The EYEBROW is **not** factory-suppressed — the top-result CALL SITE passes `eyebrow: large ? null : …`
   (SearchPage.cs:774). A 0.3 `ListRow(..., RowStyle.Hero)` that forwards an eyebrow will render one.
```

### W13 - Media row, audiobook (`detailBelowArt: true`)

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────────────────────────────────────────────────┐  Direction 1
   │ ┌────────┐   Title                                            [Audiobook]                │  Height NaN
   │ │  48    │   Author · Narrator                                                           │  MinHeight 72
   │ └────────┘                                                                               │  Padding all 8
   │                                                                                          │  Gap 8
   │   12 h 41 m                                                                              │  meta: Caption/600 TextPrimary
   │   A two-line blurb about the book that ellipsises at the second line…                    │  detail: Caption TextSecondary,
   └──────────────────────────────────────────────────────────────────────────────────────────┘  2 lines, wrap
   SearchHitsGrid reserves AudiobookRowH = 128 for a page containing one (SearchPage.cs:952-958):
   PagedShelf grids reserve ONE row height for every cell and measure nothing.
   The belowArt arm's press fill is ALWAYS `Tok.FillSubtleTertiary` (MediaCard.cs:1025) — it does NOT take the
   plain row's plated/large ladder, so an audiobook row inside a plated list presses differently from its siblings.
   The below block is a second column, Gap 2 (Spacing.XXS), inside the row's own Gap 8 (:1034).
```

### W14 - Media row with an inline detail (non-audiobook, `detail` set)

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────────────────────────────────────────────────┐  Height NaN,
   │ ┌────────┐   Title                                                                       │  MinHeight 64,
   │ │  48    │   Subtitle                                                                    │  Padding all 8
   │ │        │   Detail line one, wrapping to a second line and then ellipsising…            │  detail: Caption
   │ └────────┘                                                                               │  TextTertiary, 2 lines
   └──────────────────────────────────────────────────────────────────────────────────────────┘
```

### W15 - Video card (16:9) @ cardW 173.3

```
scale: 1 char = 8 DIP
        ┌───────────────────────────────────────────────┐  Direction 1, Gap 8, ClipToBounds,
        │  ┌─────────────────────────────────────────┐  │  Padding 8/8/8/12, radius 8
        │  │   thumb 157.3 x 88.5  (16:9, inner*9/16) │  │  Fill FillCardDefault at REST (unlike Shelf/Grid),
        │  │        radius 4, decode 480 px          │  │  HoverFill FillControlSecondary,
        │  │                          ╭──╮      ╭───╮│  │  border 1 StrokeCardDefault, Shadow Elevation.Card,
        │  │                          │…│      │ ▶ ││  │  HoverScale 1.02 / PressScale 0.98 (ScaleSubtle)
        │  └─────────────────────────────────────────┘  │  — this card scales its ROOT, the only one that does
        │  Video title on one line…                     │  BodyStrong 14/20/600, Width = inner
        │  3:41                                         │  Caption 12/16, Width = inner (omitted when empty)
        └───────────────────────────────────────────────┘  height = ar + 72 ≈ 160.5 (measured shelf, so exact)

 inner = max(**64**, cardW - 16) — a HIGHER floor than the shelf card's max(48, cardW-16) (MediaCard.cs:887).
 The "…" is `MoreCorner` (top-right, inset 8) and the FAB is the overlay's bottom-right column; they are separate
 siblings in the thumb's ZStack, NOT one row (:902-911). The card root carries `ClipToBounds = true` (:891) — the
 one card root that does, which is why its 1.02 root scale never shows square slivers.
 Its third line is a FREE meta string (WatchPageView.cs:389), not necessarily a duration.
```

### W16 - Artist Pick tone panel, VERTICAL arm (`horizontal: false`)

```
scale: 1 char = 8 DIP
   ┌────────────────────────────────────────────────────────┐  ZStack, ClipToBounds, radius 8, Fill FillCardDefault,
   │▓▓▓▓▓▓▓▓▓▓ accent @ .16 ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│  border 1 StrokeCardDefault, Shadow Elevation.Card
   │▒▒ ( 32 )  Artist Name                                  │  head: Padding 16/16/16/0, Gap 12, PersonPicture 32
   │▒           Artist pick                                 │  BodyStrong + Caption(TextSecondary)
   │·                                                       │  wash: accent .16 -> .05 @55% -> 0 @85%
   │   "I made this one in a hotel room in                  │  quote: Padding 16/8/16/16
   │    Reykjavik and never fixed the                       │  WaveeType.PickQuote = Ui.Title 28/36 in
   │    take. It is the only honest                         │  Segoe UI Variable Display, Weight 400, tracking -12
   │    thing I did that year."                             │  Wrap, MaxLines 4, ellipsis
   │                                                        │
   │ ┌────────────────────────────────────────────────────┐ │  photo band ONLY when a real wide image exists
   │ │            wide photograph, 150 tall               │ │  (pin campaign art, else the artist header banner)
   │ │            ImageFit.Cover, aspect 1.6, decode 640  │ │  NO scrim, NO pills, NO FAB, NO acrylic — a FIELD
   │ └────────────────────────────────────────────────────┘ │
   │                                                        │
   │  ┌────┐  Record title                     ╭──────────╮ │  foot row: Padding 16/12/16/12, Gap 12
   │  │ 44 │  Album · Releases 4 Oct           │  ▶ Play  │ │  44 cover radius 4 decode 96
   │  └────┘                                   ╰──────────╯ │  date run: Caption, Weight 600, colour = ACCENT
   └────────────────────────────────────────────────────────┘  trailing: WaveeCta.Play(accent) capsule (36 tall)
                                                                or PreSaveButton, keyed on the target uri
```

### W17 - Artist Pick tone panel, HORIZONTAL arm (`horizontal: true`)

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────┬───────────────────────────────┐
   │▓▒ ( 32 ) Artist Name                         │                               │  copy column:
   │▒          Artist pick                        │      photograph 300 wide      │  Grow 1, Basis 0, MinWidth 0
   │                                              │      AlignSelf = Stretch      │
   │  "…the quote grows to eat the leftover       │      (full panel height)      │  quote: Grow 1 so the foot
   │   height so the foot row sits on the          │                               │  rows sit on the bottom
   │   bottom…"                                    │                               │
   │  ┌────┐ Record title          ╭──────────╮   │                               │
   │  │ 44 │ Album                 │  ▶ Play  │   │                               │
   │  └────┘                       ╰──────────╯   │                               │
   └──────────────────────────────────────────────┴───────────────────────────────┘
```

### W18 - Empty state, PAGE scale

```
scale: 1 char = 8 DIP                                  Centered: Direction 1, Grow 1, AlignItems Center,
   ┌──────────────────────────────────────────────┐    Justify Center, Gap 4 (Spacing.XS), Padding all 24
   │                                              │
   │                                              │
   │            Nothing here yet                  │    WaveeType.PageHero = Ui.Title 28/36/600, Wrap
   │   When there's something to show, it'll      │    WaveeType.TrackMeta = Caption 12/16 secondary, Wrap
   │              appear here.                    │
   │                                              │    16 (Spacing.L) spacer box
   │              [  Browse  ]                    │    Button.Standard — NEVER Button.Accent
   │                                              │
   └──────────────────────────────────────────────┘
   loc: common.emptyTitle / common.emptySubtitle
   NO GLYPH. The `glyph` parameter was removed so a rogue copy is a compile error (EmptyState.cs:24-29).
```

### W19 - Empty state, RAIL scale (`Compact`) - narrower than ~340 DIP

```
scale: 1 char = 8 DIP
   ┌────────────────────────────────┐    identical grammar; the headline drops one rung to
   │        Nothing queued          │    Ui.Subtitle 20/28/600. 28/36 wraps to three ragged
   │  Play something and it'll      │    lines at 240 DIP, which is a paragraph, not big type.
   │       show up here.            │
   │         [  Browse  ]           │
   └────────────────────────────────┘
```

### W20 - Error state (page scale) and the offline banner

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────┐   ErrorState = EmptyState with a reason.
   │          Something went wrong.               │   loc: common.errorTitle / common.errorSubtitle / common.retry
   │   Check your connection and try again.       │   No critical colour, no critical glyph, no accent Retry.
   │              [  Retry  ]                     │   Every call logs Warning "ui" "Surface error shown: …".
   └──────────────────────────────────────────────┘

   ┌──────────────────────────────────────────────────────────────────────────────────────┐
   │ 16 (i) 12 You're offline — showing saved content.                    [ Retry ]  16   │  Direction 0,
   └──────────────────────────────────────────────────────────────────────────────────────┘  AlignItems Center,
   Height = content + 8 top + 8 bottom; radius 4 (Radii.Control)                              Gap 12,
   Fill Tok.SystemFillCautionBackground (light #FFF4CE / dark #433519)                        Padding 16/8/16/8
   Icon F136 @ 16 in Tok.SystemFillCaution (light #9D5D00 / dark #FCE100)
   Text: WaveeType.TrackMeta (Caption 12/16 secondary). loc: common.offline / common.offlineBanner
```

### W21 - Content-filter chip rail (Liked Songs) @ any width

```
scale: 1 char = 8 DIP                              horizontal ScrollView, SuppressScrollBar, AutoEdgeFade
   ┌────────────────────────────────────────────────────────────────────────────────────────┐
   │ (  All  ) 8 (  K-Pop  ) 8 ( Chill ) 8 ( Energetic ) 8 ( Acoustic ) 8 ( Sad ) …  ▒fade▒ │
   └────────────────────────────────────────────────────────────────────────────────────────┘
   Rail height 40 (chip 32 + room for the focus visual, which draws outside the chip box)
   Bottom margin 8; VerticalExtent contributed to stacked chrome = 40 + 8 = 48
   Chip: Height 32, Shrink 0, Padding 12/0/12/0, Corners 999 (capsule)
     unselected:  Fill FillControlDefault, border 1 StrokeControlDefault, label 13/400 TextPrimary
     hover:       Fill FillControlSecondary, HoverBorderColor = Tok.AccentDefault
     selected:    Fill AccentDefault, border transparent, label 13/600 TextOnAccentPrimary,
                  hover -> AccentSecondary
     unavailable: no hover change at all, label TextDisabled, IsEnabled false, Cursor Arrow, no focus stop
   HoverScale/PressScale = ScaleSubtle (1.02 / 0.98), gated on availability; 167 ms FluentDecelerate
   Selection is EXCLUSIVE (All + at most one). Re-tapping the active chip clears it.
   ZERO chips -> `Build` returns **null** (ContentFilterChips.cs:50) and the caller omits the whole row; there is
     no empty rail and no "All"-only rail. `Derive(tracks)` marks everything evidenced by construction (:29-33),
     so the disabled state only ever appears on the CURATED set.
   `Role = AutomationRole.Button` is set on EVERY chip including unavailable ones (:81); only `Focusable`,
     `IsEnabled`, `Cursor` and the scale/border channels are availability-gated.
   The rail's `AutoEdgeFade` uses the ENGINE DEFAULT band (36), not the shelf's 24 — the two fade bands in this
     chapter differ (ContentFilterChips.cs:73 vs HomeModuleLayout.ShelfEdgeFade).
   The label is `MaxLines = 1` with NO `Trim`, and `Shrink = 0` — a long concept name overflows its capsule rather
     than ellipsising. Worth fixing in 0.3 (`Trim = CharacterEllipsis`) but it is what 0.2.9 ships.
```

### W22 - Face pile (Liked facts panel)

```
scale: 1 char = 4 DIP (this one is small)
      <-32-><-20-><-20-><-20->
      ╭────╮
      │╭──╮│╭────╮                        each frame: 32 outer (28 avatar + 2 ring on both sides),
      ││28││││28 ││╭────╮                 Corners = 16, Fill = Tok.FillSolidBase (the RING), Padding all 2
      │╰──╯│╰────╯│╭──╮ │╭────╮           siblings after the first carry Margin.Left = -12
      ╰────╯      ││28│ ││+7  │           Step = Outer - Overlap = 20 -> width of n frames = 32 + (n-1)*20
                  │╰──╯ ││    │           overflow frame: inner 28 circle Fill FillCardDefault,
                  ╰─────╯╰────╯           label "+N" at 10 px / 700 / TextSecondary
   selected face: ring Fill swaps to Tok.AccentDefault; hover -> Tok.AccentSecondary
   unselected + clickable: hover ring -> Tok.AccentSubtle
   inert face: Role None, Cursor Arrow, HoverFill Transparent, HoverScale/PressScale 1 — but `FocusVisualMargin`
     is set UNCONDITIONALLY on an avatar frame (FacePiles.cs:103); only the OVERFLOW frame gates it on `live` (:132).
     Harmless (an unfocusable node draws no ring) but do not "restore" it as a live-only channel in 0.3.
   live face: Role Button, Focusable, FocusVisualMargin 1 on all sides, HoverScale 1.04 / Press 0.96 (ScaleStandard),
     83 ms (MotionTok.ControlFaster). An avatar frame has NO PressedFill; the overflow frame has both
     (HoverFill FillSubtleSecondary, PressedFill FillSubtleTertiary, :133-134).
   tooltip: ToolTip.Wrap(..., showDelayMs: LikedLens.TipDelayMs) — the constant is 0f
     (LikedFactsPanel.cs:1348), shared with every tooltip in the facts panel. Keyed "face:{i}" / "face:more",
     and the Key is re-applied to the TOOLTIP WRAPPER as well (:116, :150) so the wrapper reconciles in place.
   EMPTY list -> `Strip` returns an empty BoxEl, NOT a ring (:76). "An empty ring is not a face pile."
   `maxVisible` defaults to 4 (`FacePiles.MaxVisible`, :36); the Liked facts panel passes its own count (5+).
   `overflow` may be supplied by the caller when the count comes from somewhere the list cannot see
     (ArtistFacePile's track-only contributors); it defaults to `faces.Count - visible`.
   The negative margin lives on the FRAME, under the tooltip wrapper (:112-114) — a wrapper Margin would need a
     second geometry to keep in step.
   SlotsIn(w) = w < 32 ? 1 : 1 + floor((w - 32) / 20)
   VisibleFaces(w, total) = total <= 0 ? 0 : total <= slots ? total : max(1, min(slots-1, total-1))
   The pile's ONLY 0.2.9 consumer is LikedFactsPanel (:606-673); ArtistFacePile and CollaboratorFacePile keep
     their own hand-rolled copies of the same constants on purpose (FacePiles.cs:10-20).
```

### W23 - Stat tile (album facts bento / pre-release unit tiles)

```
scale: 1 char = 8 DIP
   ┌──────────────┐  ┌──────────────┐   Direction 1, Gap 1, Grow 1, Basis 0, MinWidth 0, Shrink 1,
   │ 2,401,556    │  │ 41 m 12 s    │   ClipToBounds, Padding 12/8/12/8, Corners 4 (Radii.Control),
   │ plays        │  │ length       │   Fill FillCardSecondary, border 1 StrokeCardDefault
   └──────────────┘  └──────────────┘   value:   18 px / 800 / TextPrimary, MinWidth 0, 1 line, ellipsis
                                        caption: 11 px / 400 / TextSecondary, 1 line, ellipsis
   The PARENT owns the width (grid column / equal flex share); the tile never measures to its text.
   A value swap cross-fades in place: the value box is a ZStack with MinWidth 0 and its keyed child
   ("v:" + value) carries MotionRecipes.TextSwap, so a swap can never change measurement.
   Enter = DetailRail.FadeUp (opacity only); Layout = DetailRail.Shove (Position only, 250 ms SmoothOut) —
   `layout` is an OVERRIDABLE parameter (`layout ?? DetailRail.Shove`, StatTile.cs:49), so a host with its own
   reflow may substitute one; `Shove` is the default, not a constant.
   Key = "fact:" + key (:49) — the TILE is keyed by unit/name, its VALUE BOX by "v:" + value (:39). Two keys,
   two jobs: the tile must not remount on a tick, the value must.
   Two more arms the wireframe does not draw:
     · `wrapValue: true` -> the value run becomes `MaxLines 2, Wrap` instead of `NoWrap` + ellipsis (:26-27),
       for a tile whose value is a phrase rather than a number.
     · `trailing:` appends a THIRD child under the caption (:54) — a sparkline, a delta chip. The tile still
       never measures to it (`MinWidth 0`, `ClipToBounds`).
```

### W24 - Pre-release countdown card (album rail / artist page)

```
scale: 1 char = 8 DIP
   ┌────────────────────────────────────────────────────────┐  Direction 0, AlignItems Center, Gap 12,
   │  ◜◝  Coming soon                                       │  Padding all 12, Corners 8,
   │ ( ⟳ ) ┌──────────┐ 4 ┌──────────┐                      │  Fill FillCardDefault, border 1 StrokeCardDefault
   │  ◟◞   │ 12       │   │ 04       │                      │  ProgressRing.Indeterminate(34, accent, !released)
   │       │ DAYS     │   │ HRS      │                      │  eyebrow: WaveeType.Eyebrow, colour = ACCENT
   │       └──────────┘   └──────────┘                      │  tiles WRAP: 2x2 in a narrow rail,
   │       ┌──────────┐ 4 ┌──────────┐                      │  one row when the card is wide
   │       │ 33       │   │ 07       │                      │  each tile = StatTile (W23), keyed by UNIT
   │       │ MIN      │   │ SEC      │                      │  so the 1 Hz tick cross-fades the value in place
   │       └──────────┘   └──────────┘                      │  days: natural width; h/m/s zero-padded D2
   └────────────────────────────────────────────────────────┘
   Released -> "Out now" (15 px / 700 / TextPrimary) and the ring's own Inactive state fades it out.
   Bare:true (tone panels) returns ONLY the tiles / "Out now": no plate, no ring, no eyebrow.
   The plate carries NO Shadow (PreReleaseCountdown.cs:65-71) — unlike every other card in this chapter.
   The eyebrow and the tile block sit in a Grow=1 column with Gap 4 (Spacing.XS, :78); the ring is its sibling
   at the card's Gap 12.
   Once `released` the 1 Hz UseInterval is disabled (`enabled: !released`, :59), so a shipped album's card
   costs nothing per frame.
   loc: detail.preReleaseEyebrow "Coming soon" / detail.preReleaseOut "Out now" /
        detail.preReleaseUnitDays "DAYS" / …Hours "HRS" / …Minutes "MIN" / …Seconds "SEC"  (verified in en-US.json)
```

### W25 - Flip countdown (daylist), hero and compact

```
scale: 1 char = 4 DIP
   HERO (cellH 28, cellW 13, colon 6/8, size 20):
   ┌──┐┌──┐ : ┌──┐┌──┐ : ┌──┐┌──┐   8   Next update at 17:00
   │ 0││2│   │ 5││9│   │ 0││1│           Caption, TextTertiary, 1 line, ellipsis, MinWidth 0
   └──┘└──┘   └──┘└──┘   └──┘└──┘        loc: home.nextUpdateAt
   COMPACT (cellH 20, cellW 10, colon 6, size 14) — the narrow detail rail.

   Numerals: FontFamily "Segoe UI Variable Display", Weight 300, LineHeight = cellH.
   Colour = WaveePalette.TextInk(Accent()) while live; Tok.TextTertiary once expired (dimmed 00:00:00).
   Each cell is a fixed-size ClipToBounds window over ONE keyed child (the interned numeral string):
   a numeral change is a KEYED REMOUNT — the old digit exits up (Dy = -round(h*0.35)), the new one
   rises from below (Dy = +round(h*0.35)), both at Opacity 0, on MotionTok.ControlFast.
   Hours clamp at 99 (two fixed cells) rather than reflowing.
   HERO colon width 8, COMPACT colon width 6 (FlipCountdown.cs:93) — the "6/8" above reads in that order.
   Digit strip Gap 0 (the cells butt); the strip-to-caption Gap is 8 (Spacing.S, :104).
   `ExpiresAtMs <= 0` -> renders an EMPTY BoxEl (:84) — no dimmed zeros, nothing. The hooks above the guard
     always run, so the order stays stable. The dimmed 00:00:00 is the EXPIRED-but-positive case only.
   `BottomMargin` is a caller-owned bottom margin (:37, :105) — the Home hero reserves its pulse row with it.
   HeroRowHeight 28 / CompactRowHeight 20 are restated as literals in `HomeHeroLayout.PulseBlock` and
     `DetailVerticalLayout.PulseRowHeight` (:39-44). THREE sources; changing one means changing all three.
```

### W26 - Card context menu (right-click / Menu key / "…" click), album card

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────┐  ContextMenuModel = header + command strip + rows
   │ ┌────┐  Album name                           │  header: 38 x 38 art, radius 6 (19 when circular),
   │ │ 38 │  Artist name (tags stripped to text)  │  decode 76; subtitle falls back to the KIND label
   │ └────┘                                       │  ("Album" / "Artist" / "Playlist" / "Podcast" / "Song")
   ├──────────────────────────────────────────────┤
   │  [ ▶ Play ] [ Play next ] [ Play after ]     │  the labelled command strip (AppBarCommand)
   │  [ ♡ Save ]                                  │  strip order: Play, Play next, Play after, Save
   ├──────────────────────────────────────────────┤  (queue pair only when the kind HAS a track set;
   │  Add to playlist                           ▸ │   Liked Songs drops Save — it cannot be un-saved)
   │  Open                                        │
   │  Pin / Unpin                                 │  rows, in grammar order: state -> collection ->
   │  Go to artist            (album only)        │  navigation -> Share -> surface extras
   │  Share                                     ▸ │
   │  Go to artist radio      (artist only)       │
   └──────────────────────────────────────────────┘
   ARTIST menus add a "Follow / Following" ROW above Add-to-playlist, and omit the queue pair AND
   Add-to-playlist (an artist has no resolvable track set — ContainerTracks' locked decision).
   SHOW cards get Play · Open · Pin · Share only (Menus.cs:617). TRACK uris get the thin track shape
   (`TrackUriCard`, :654). EPISODE and every unrecognised scheme get NO MENU AT ALL (Menus.cs:544-571).

   Rows that can be ABSENT, not disabled:
     · Add to playlist — absent whenever `ContainerTracks.ResolverFor` is null (Menus.cs:286-289): an artist,
       a show. "The row is ABSENT rather than a disabled promise."
     · Pin / Unpin — absent for a uri `SidebarPinId` cannot pin (Menus.cs:597). Decided in ONE place, never per menu.
     · Save — absent on Liked Songs (:583), which cannot be un-saved.
   Every liked spelling (`spotify:user:<u>:collection` included) resolves to the PLAYLIST arm (:558-563); before
   that ladder a recents-built liked card fell through to `TargetKind.None` and got no menu at all.
   Header art for Liked Songs comes from `LikedSongsArtwork.For(uri, 38, 38, 6)` (Menus.cs:1152) — 38 is below
   the 140 treatment floor, so what paints is the flat 2x2 of the newest likes (or the stock PNG), never a
   provider cover. Header title and subtitle both run through `PlainHeaderText` = strip tags THEN decode entities
   (:1162-1163), so "AC&amp;DC" reads "AC&DC" and an `<a>` in a blurb never reaches the header.
```

### W26b - Expandable rich description ("… More" / "Less")

```
scale: 1 char = 8 DIP
   ┌──────────────────────────────────────────────────────────────┐
   │ A playlist blurb that runs past its line budget and is cut …  │  SpanTextEl, Wrap, Trim CharacterEllipsis
   │ at the last line, where the engine's native OverflowSuffix    │  MaxLines = maxLines while collapsed
   │ reserves the run:                                   … More   │  OverflowSuffix = [ "… " + common.more ]
   └──────────────────────────────────────────────────────────────┘    Weight 600, Color = linkColor, own OnClick

   expanded:  MaxLines = 0 (uncapped) and a " " + common.less span is APPENDED to the parsed run list
              (RichText.cs:183-202), same 600 / linkColor / own OnClick.
   loc: common.more = "More", common.less = "Less"
   The suffix is reserved on the FINAL LINE ONLY, and only when the body actually overflows — that is the engine's
   OverflowSuffix contract, not an app-side measure. Do not re-implement it as a second TextEl in 0.3.
   Key: "rich-expand:{ctx}:{html}:{(int)width}:{lines}" (:53) / "rich-expand-flex:{ctx}:{html}:{lines}" (:59).
   `Of` / `OfFlex` / `OfRow` have NO suffix: they ellipsise silently.
```

### W27 - Drag in progress from a card

```
   The card itself is the drag source (never the shelf's gutter wrapper — lifting that would drag a
   rectangle of empty margin, MediaCard.cs:194-198). Nested affordances set BlocksDragArm = true so the
   arm walk stops there: PlayFab, CoverActionFab, MoreCorner, MoreInline, CardLibraryAction, SaveButton.
   The chip and the drop cues belong to 01-track-row.md; what THIS surface owns is only where the
   source is attached and which children refuse to be handles.
```

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| Shelf card gutter | Grow 1 | `0,4,0,2` | — | — | — | — | `MediaCard.cs:1205` |
| Card root (`CardShell`) | Grow 1 | — | `Radii.Card` 8 | — | none at rest | none at rest | `MediaCard.cs:77-101` |
| Hover plate | Grow 1 | — | 8 | — | `Tok.FillCardDefault` (dark `#0DFFFFFF`, light `#B3FFFFFF`); stroke 1 px `Tok.StrokeCardDefault` (dark `#19000000`, light `#0F000000`) | `Elevation.Card` — dark blur 8 / y 2 / `#33000000`, light blur 4 / y 2 / `#1A000000` | `MediaCard.cs:83-98`, `Elevation.cs:18-21`, `PaletteBuilder.cs:304-305, 316, 390-391, 427` |
| Card content box | Grow 1 | pad `8,8,8,12`, gap 8 | — | — | — | — | `MediaCard.cs:1169-1176`, `:250-253` |
| Shelf cover | `inner = max(48, cardW-16)` square | — | 8 (square) / `inner/2` (circular) | — | — | — | `MediaCard.cs:1120-1121` |
| Grid cover | fills the cell, aspect 1 | — | 8 / `Radii.Full` | — | — | — | `MediaCard.cs:230-246`, `Surfaces.cs:292-303` |
| Card title | — | — | — | `WaveeType.TrackTitle` = `Ui.BodyStrong` **14 / 20 / 600** | `Tok.TextPrimary` | — | `WaveeType.cs:23`, `:28` |
| Card subtitle (shelf) | width = `inner`, 2 lines | — | — | `SpanTextEl` size 12, LineHeight 16 | `Tok.TextSecondary`; links `Tok.AccentTextPrimary` | — | `RichText.cs:26-30` |
| Card subtitle (grid/row) | 1 line | — | — | `WaveeType.TrackMeta` = `Ui.Caption` secondary **12 / 16 / 400** | `Tok.TextSecondary` | — | `WaveeType.cs:31` |
| Play FAB | 44 (`MediaCard.FabSize`); 30 on a plain row, 44 on `large` | inset 8 (`FabInset`) from bottom+right | circle | glyph `0.42 * size` = 18.5 (E768/E769) | `Tok.AccentDefault` / `AccentSecondary` / `AccentTertiary`; glyph `Tok.TextOnAccentPrimary` | none (the card carries it) | `MediaCard.cs:26-27, 1077-1089` |
| Cover action FAB ("go to album") | `max(34, fab-8)` = 36 | gap 8 before the play FAB | circle | glyph `0.40 * size` = 14.4 (E8A7) | `WaveeOnMedia.ScrimRest` `#8C000000` / hover `#BE000000` / press `#DC000000`; stroke 1 px `#3AFFFFFF`; glyph `Tok.OnMediaPrimary` | `Elevation.Card` | `MediaCard.cs:1221-1234`, `WaveeOnMedia.cs:37-56` |
| "…" corner | 30 | inset 8 from top+right | circle | glyph 13 (E712) | same on-media ladder as above | `Elevation.Card` | `MediaCard.cs:108-133` |
| "…" inline | 36 (`size`) | — | circle | glyph 15 | `onDark:false` -> transparent rest, `Tok.FillSubtleSecondary` hover, `Tok.FillSubtleTertiary` press, glyph `Tok.TextSecondary` | none | `MediaCard.cs:135-150` |
| Kind chip (on media) **DEAD** | — | `8,4,8,4` | `Radii.Full` (capsule) | `WaveeType.Eyebrow` = Caption 12/16/**600**, `CharSpacing 30` | `WaveeOnMedia.ScrimRest` + 1 px `WaveeOnMedia.Stroke`; ink `Tok.OnMediaPrimary`; `HitTestVisible = false` | — | `MediaCard.cs:161-169`; only caller `:802` (EditorialCardCore) |
| Row chip (on page) | — | `8,4,8,4` | `Radii.Full` | Eyebrow 12/16/600 tracking 30 | `Tok.FillSubtleSecondary`; ink `Tok.TextTertiary` | — | `MediaCard.cs:1065-1071` |
| Equalizer pill | bar height 14 | pad all 4 | `Radii.Control` 4 | — | `WaveeOnMedia.ScrimRest`; bars = page accent ink (`WaveeAccentCtx`) or `Tok.AccentTextPrimary` | — | `MediaCard.cs:1505-1513` |
| Row (plain) | Height 64 | pad `8,0,8,0`, gap 12 | 4 | — | transparent / `FillSubtleSecondary` / `FillSubtleTertiary` | none | `MediaCard.cs:1039-1062` |
| Row (plated) | Height 64 | same | 8 | — | `FillCardSecondary` / `FillCardDefault` / `FillSubtleTertiary`; 1 px `StrokeCardDefault` | none | same |
| Row (large) | Height 112 | pad `16,12,16,12`, gap 16 | as above | title `WaveeType.PageHero` = `Ui.Title` **28 / 36 / 600** | as above; press fill `FillCardDefault` | none | `MediaCard.cs:1043-1053` |
| Row art | 48 (`WaveeSize.Thumb48`) / 84 (`large`) | — | 4 / 8 / `art/2` circular | — | — | — | `MediaCard.cs:965-968` |
| Cover hover veil (centred FAB) | fills the art | — | inherits the art clip | — | `WaveeOnMedia.CoverScrim` black @ 110/255 | — | `MediaCard.cs:1415-1421`, `WaveeOnMedia.cs:50` |
| Video card | `inner` wide, `inner*9/16` tall | pad `8,8,8,12`, gap 8 | 8 (card) / 4 (thumb) | title BodyStrong, meta Caption | `FillCardDefault` at rest / `FillControlSecondary` hover; 1 px `StrokeCardDefault` | `Elevation.Card` | `MediaCard.cs:887-916` |
| Artist Pick panel | — | head `16,16,16,0`; quote `16,8,16,16`; foot `16,12,16,12` | 8 | quote `WaveeType.PickQuote` = Title 28/36 **display face, 400, tracking -12** | `FillCardDefault` + 1 px `StrokeCardDefault` + accent wash | `Elevation.Card` | `MediaCard.cs:306-421`, `WaveeType.cs:198-203` |
| Pick photo band | 150 tall / 300 wide | — | 0 (the panel clips) | — | image, `ImageFit.Cover`, aspect 1.6, decode 640 | none | `MediaCard.cs:370-388` |
| Pick foot cover | 44 | — | 4 | — | decode 96 | — | `MediaCard.cs:438-443` |
| Filter chip | Height 32; rail 40 | pad `12,0,12,0`, gap 8 | 999 | 13 px / 400 (600 selected) | see W21 | — | `ContentFilterChips.cs:79-106` |
| Face frame | 32 outer / 28 avatar | ring 2, overlap -12 | circle | "+N" 10 / 700 | ring `Tok.FillSolidBase`; selected `Tok.AccentDefault` | — | `FacePiles.cs:24-33, 94-110` |
| Stat tile | Grow 1, Basis 0 | pad `12,8,12,8`, gap 1 | 4 | value 18 / 800; caption 11 / 400 | `FillCardSecondary` + 1 px `StrokeCardDefault` | — | `StatTile.cs:23-55` |
| Empty state | — | pad all 24, gap 4; 16 spacer before the action | — | `PageHero` 28/36/600 (page) or `Ui.Subtitle` 20/28/600 (compact); caption = `TrackMeta` | `Tok.TextPrimary` / `Tok.TextSecondary`; action = `Button.Standard` | — | `EmptyState.cs:47-72` |
| Offline banner | — | pad `16,8,16,8`, gap 12 | 4 | `TrackMeta` | `Tok.SystemFillCautionBackground`; icon `Tok.SystemFillCaution` @ 16 | — | `OfflineBanner.cs:21-27` |
| Shelf chevron (engine) | 32 x 32 | — | 16 (circle) | glyph 13 (E76B / E76C) | `Tok.FillControlDefault` / `FillControlSecondary`; `Opacity 0.35` disabled; glyph `Tok.TextSecondary` | — | `PagedShelf.cs:1402-1409` |
| Shelf geometry | min 148 / max 188, gap 12, edge fade 24 | lift 12 / shadow 12 / halo 12 | — | — | — | — | `HomeModules.cs:504-506`, `PagedShelf.cs:257-272` |
| Rail chevron (DEAD) | 32 x 32 (`WaveeCta.IconButtonSize` = `WaveeSize.ControlH` 32) | — | 4 | glyph 16 | `FillSubtleTransparent` / `FillSubtleSecondary` / `FillSubtleTertiary`; rest `Opacity 0.7`, disabled 0; `HoverScale/PressScale = ScaleStandard` gated on `enabled` | — | `Rail.cs:95-111` |
| Rail viewport (DEAD) | Grow 1, caller height | — | — | — | `EdgeFadeSpec(mask, **36**)` — the engine default, not the shelf's 24; mask from `(canPrev, canNext)` | `ClipToBounds` | `Rail.cs:46-51` |
| Expandable rich suffix | inline run | — | — | size of the host paragraph, **Weight 600** | `linkColor` (the caller's `Tok.AccentTextPrimary`) | — | `RichText.cs:184, :200` |
| Rich subtitle bold run | inline run | — | — | **Weight 700** | host colour | — | `RichText.cs:119` |
| Rich subtitle unroutable link | inline run | — | — | as above | `linkColor`, **no `OnClick`** — styled but inert (an `spotify:episode:` href) | — | `RichText.cs:123` |
| Persistent FAB (DEAD) | 48 at its one call site | — | circle | glyph **0.38 × size** | `Light` arm: `WaveeOnMedia.LightButton`/`…Hover`/`…Pressed`, ink `Tok.MediaStage`; else the accent ladder | `Elevation.Card` | `MediaCard.cs:1482-1499`, `WaveeOnMedia.cs:129-136` |
| Card library action (DEAD) | 40 | — | circle | glyph **17** (`Icons.Heart` EB51 / `HeartFill` EB52) | on-dark: the scrim ladder; else transparent / `FillSubtleSecondary` / `FillSubtleTertiary` | — | `MediaCard.cs:1257-1271` |
| BrowsePage shelf skeleton card | 148 × `ShelfCardHeight(148)` = **220** | gap 4 (`Spacing.XS`) | cover 8 | `CardTitle` + `TrackMeta` ghosts | cover square `Tok.FillSubtleSecondary` | — | `BrowsePage.cs:303-318` |

---

## 4. Colour & material

**1. The cover-tinted placeholder (the single most important colour derivation on this surface).**
`url -> SpotifyLive.CoverColorPlane.TryGetTint(url, isLightTheme, out argb)` ->
`ColorF.Lerp(neutral, ToColor(argb), 0.55f)` (`Surfaces.cs:66-92`), where `neutral` is the **opaque**
`#2A2A2A` (dark) / `#F2F2F2` (light) (`Surfaces.cs:57-61`) — never a translucent card brush, or the desktop reads
through a still-loading cover.
*Applied to:* the `CoverShimmer` tile behind every shelf card cover; `Surfaces.ArtworkFill`'s `Placeholder`; every
`Mosaic` quadrant; every row/sidebar thumb under 80 DIP (`ShimmerMinEdge`).
*Transition:* the fill is a **paint-only bind** (`Prop.Of` reading `CoverColorPlane.Watch(url)`), so a landed
grading repaints exactly that tile — never a component re-render, never the global epoch fan-out
(`Surfaces.cs:94-111, 490-504`). A miss *enqueues that image for grading*: rendering the art IS the request.

**2. Shimmer breathe.** `AnimChannel.Opacity` keyframes `[0:1, 0.5:0.5, 1:1]` over 1000 ms, looping, **only while
the image is loading**; on the loading->settled edge the loop is replaced by a finite flat track so the frame loop
can quiesce (`Surfaces.cs:455-488`). On a weak/UMA GPU (`GpuProfile.IsWeak`) the breathe is **off** and the tile is
held flat so decodes/uploads coalesce.

**3. The on-media ladder (theme-INVARIANT).** Everything painted on artwork:
scrim plate rest `Tok.MediaScrim` black @ 0.55, hover `#BE000000`, pressed `#DC000000`; hairline
`#3AFFFFFF`; ink `Tok.OnMediaPrimary` white 1.0 / `OnMediaSecondary` 0.80 / `OnMediaTertiary` 0.60; full-cover veil
`CoverScrim` black @ 110/255 (`WaveeOnMedia.cs:31-68`). These stay dark and this ink stays white in light mode —
`theming.md`'s leaf-value rule.

**4. The Artist Pick wash.** `accentColor` (from a `Func<ColorF>` read at build) ->
`GradientDown(0: a@0.16, 0.55: a@0.05, 0.85: a@0)` (`MediaCard.cs:317-323`). 0.16 is the engine's
`Tok.AccentSubtle` alpha rung, deliberately not a new ladder. The date caption in the foot row paints the **raw**
accent as ink (`MediaCard.cs:459-462`); the Play capsule takes `WaveeCta.Palette(accentColor)`, whose ramp is
`fill / fill@0.90 / fill@0.80` with WCAG-picked ink (`WaveeCta.cs:225-235`).

**5. Grid-card accent is DELIBERATELY NOT APPLIED.** `GridCard` accepts `ColorF? accent` and then passes
`CardShell(content, onClick)` with **no** `plateFill` — "Keep the hover surface neutral. The cover already carries
the release palette; tinting the whole plate muddies saturated artwork colours" (`MediaCard.cs:280-283`). The only
accent a card ever wears is the discography drawer owner's 2 px border (W8). Do not "fix" this in 0.3.

**6. Countdown ink.** `WaveePalette.TextInk(Accent())` — the contrast-graded *text* grade of the chrome accent, never
the raw fill, because the digits sit on a wash mixed from the same hue (`FlipCountdown.cs:88-90`). Expired ->
`Tok.TextTertiary`.

**7. Light vs dark, concretely.**
`FillCardDefault` light `#B3FFFFFF` (white @ 70 %) vs dark `#0DFFFFFF` (white @ 5 %) — so the resting card is
*invisible* in both and the hover plate is a large step in light, a small one in dark; that is why the -4 lift and
the `Elevation.Card` halo (light blur 4 @ 10 %, dark blur 8 @ 20 %) carry the state in dark.
`StrokeCardDefault` light `#0F000000` vs dark `#19000000` — both black-alpha, the stroke gets *stronger* in dark.
`SystemFillCautionBackground` light `#FFF4CE` / dark `#433519`.
The on-media ladder and the placeholder neutrals are the only theme-branching this surface does by hand.

**8. Kind-chip / eq-pill / "…" all share ONE plate.** Before convergence MediaCard hand-rolled 36 `FromRgba`
literals and three near-duplicate scrim ladders (185/225/245, 132/190/220, 120/184/218). One ladder now
(`WaveeOnMedia.cs:10-23`). Re-introducing a second is a regression.

---

## 5. Motion

Every value below is a *value read during Render*, never a hook-order branch. `Motion.ReducedMotion` collapses
`ScaleTier.Hover/Press` to `1f` (`WaveeMotion.cs:179-189`), which makes the recorder's
`abs(scale-1) > 0.0008` test fail and skips the transform entirely.

| trigger | target | property | from -> to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| card hover | card root | `OffsetY` | 0 -> **-4** | `MotionTok.ControlNormal` 250 ms | `Easing.FluentStandard` | — | tier is not consulted for `WhileHover`; `EditorialCardCore` nulls it explicitly, `ApplyCardPhysics` does not | `MediaCard.cs:70-73`, `MotionTok.cs:166` |
| card press | card root | `Scale`, `OffsetY` | 1 -> **0.99**, 0 -> **-1** | 250 ms | FluentStandard | — | as above | `MediaCard.cs:71` |
| card hover | hover plate | `Opacity` | **0 -> 1** | `MotionTok.ControlFaster` **83 ms** | FluentStandard | — | `KeepFade` — fade survives | `MediaCard.cs:94-96` |
| card hover | cover stack | `Scale` | 1 -> **1.04** (`ScaleStandard`) | `MotionTok.StandardEnter` **300 ms** | `Easing.FluentDecelerate` | — | -> 1.0 | `MediaCard.cs:1155-1156` |
| card hover | play-FAB wrapper | `Opacity` | **0 -> 1** | `WaveeMotion.Fast` **167 ms** | FluentDecelerate | — | fade survives | `MediaCard.cs:1418, 1434, 1440` |
| card hover | "…" corner wrapper | `Opacity` | **0 -> 1** | 167 ms | FluentDecelerate | — | fade survives | `MediaCard.cs:113` |
| FAB hover / press | the FAB circle | `Fill` | `AccentDefault -> AccentSecondary -> AccentTertiary` | 83 ms brush ramp — **engine default**, not authored (`PlayFab` sets no `HoverDurationMs`; `Reconciler.cs:4812` falls back to `InteractionAnim.ControlFasterMs`) | FluentStandard | — | unaffected | `MediaCard.cs:1081` |
| FAB hover / press | the FAB circle | `Scale` | **none, deliberately** — scaling the rounded plate past its retained paint bounds cut out the lower-right sector (the "Pac-Man" wedge). Colour + the card's own press carry it | — | — | — | n/a | `MediaCard.cs:1082-1084` |
| "…" / cover-FAB hover / press | the circle | `Scale` | 1 -> **1.07** / **0.92** (`ScaleEmphatic`) | engine interact fade | — | — | -> 1.0 | `MediaCard.cs:125`, `WaveeMotion.cs:48` |
| VideoCard hover / press | card root | `Scale` | 1 -> **1.02** / **0.98** (`ScaleSubtle`) | engine interact fade | — | — | -> 1.0 | `MediaCard.cs:897` |
| row hover / press | row | `Fill` | see W9/W10 ramps | 83 ms — **engine default** (the row sets no duration) | FluentStandard | — | unaffected | `MediaCard.cs:1051-1053` |
| row press | row | `Scale` | **none.** `ScaleSubtle` is explicitly banned on a near-full-width row: 2 % moves each edge several DIP in opposite directions and visibly blurs the title mid-scale (S3 #14). A wide row's press acknowledgement is `PressedFill` alone | — | — | — | n/a | `WaveeMotion.cs:33-38` |
| row hover | centred FAB veil + FAB | `Opacity` | 0 -> 1 | 167 ms | FluentDecelerate | — | fade survives | `MediaCard.cs:1418` |
| row hover | equalizer pill (centred / inline) | `Opacity` | 1 -> **0** (`HoverOpacity 0`) | engine hover fade | — | — | fade survives | `MediaCard.cs:1524, 1559` |
| playback edge | equalizer bars | `Transform.ScaleY` | per-bar, pixel-quantised | `UseInterval` ~15 Hz, ONE ticker for 3 bars | — | phase-staggered | policy in `EqualizerMotionPolicy` (see `01-track-row.md`) | `Components/Equalizer.cs:23-35` |
| shelf pager click | strip offset | scroll offset | page stride | `ItemsViewController.StartBringItemIntoView` glide | engine | — | engine policy | `PagedShelf.cs` |
| shelf settle (`ShelfSnap.Page`, Search only) | strip offset | scroll offset | fractional -> page boundary | engine glide, armed after **180 ms** grace | — | commit threshold **0.25 page** | engine policy | `PagedShelf.cs:305-325` |
| shelf overflow | viewport edges | `AutoEdgeFade` | offset-driven, live per frame | — | — | band **24** DIP (Wavee) / 36 (engine default) | n/a (not motion) | `HomeModules.cs:506`, `PagedShelf.cs:1206` |
| chip hover / press | chip | `Scale` | 1 -> 1.02 / 0.98, **gated on availability** | 167 ms | FluentDecelerate | — | -> 1.0 | `ContentFilterChips.cs:94-96` |
| chip hover | chip border | `BorderColor` | `StrokeControlDefault -> AccentDefault` | 83 ms brush ramp | FluentStandard | — | unaffected | `ContentFilterChips.cs:93` |
| face hover / press | face frame | `Scale` | 1 -> 1.04 / 0.96 | `MotionTok.ControlFaster` 83 ms | FluentStandard | — | -> 1.0 | `FacePiles.cs:105-107` |
| stat-tile value change | value box child | keyed remount | `MotionRecipes.TextSwap`: Dy +4 -> 0, Opacity 0 -> 1, Blur 2 -> 0 | **150 ms** | `Easing.EaseInOut` | — | `KeepFade` | `StatTile.cs:38-42`, `MotionRecipes.cs:229-233` |
| stat-tile insert | tile | `Enter = DetailRail.FadeUp` (Opacity only) | 0 -> 1 | `Expressive.Fast` **250 ms** | `Easing.SmoothOut` | — | fade survives | `StatTile.cs:49`, `DetailRail.cs:52-57` |
| stat-tile shove | tile | `Layout = DetailRail.Shove` (Position only) | old origin -> new | 250 ms | SmoothOut | — | travel snaps | `DetailRail.cs:56-57` |
| daylist tick (1 Hz) | one digit cell | keyed remount | out: `Dy = -round(h*0.35)`, `Opacity 0`; in: `Dy = +round(h*0.35)`, `Opacity 0` | `MotionTok.ControlFast` **150 ms** | FluentStandard | — | `KeepFade` -> crossfade, no slide | `FlipCountdown.cs:135-155`, `MotionTok.cs:165` |
| pre-release tick (1 Hz) | unit tile value | `TextSwap` (see above) | — | 150 ms | EaseInOut | — | KeepFade | `PreReleaseCountdown.cs:126` |
| pre-release ring | `ProgressRing.Indeterminate(34)` | GPU looping keyframe track | — | engine | — | — | engine policy | `PreReleaseCountdown.cs:75` |
| shelf entrance | eager stacks only | `WaveeEntrance.Row(i)`: Dy 8 + Opacity 0 + Blur 2 | — | `Expressive.Slow` **400 ms** | SmoothOut | **40 ms/item, capped at index 8** (360 ms total) | `DelayMs` returns 0 | `WaveeMotion.cs:102-127` |
| Artist Pick / PreSave | panel | `ApplyCardPhysics` | as row 1-2 | 250 ms | FluentStandard | — | as above | `MediaCard.cs:414` |
| Rail page change (DEAD) | strip | `OffsetX` | `page*3*(cardW+gap)` | `UseAnimatedValue` **320 ms** | `Easing.SmoothOut` | — | engine policy | `Rail.cs:32-33` |
| Rail chevron reveal (DEAD) | chevron | `Opacity` | **0.7 -> 1** (disabled: 0 -> 0) | 167 ms | FluentDecelerate | — | fade survives | `Rail.cs:101-102` |
| Rail chevron hover / press (DEAD) | chevron | `Scale` | 1 -> **1.04 / 0.96** (`ScaleStandard`), gated on `enabled` | engine interact fade 83 ms | — | — | -> 1.0 | `Rail.cs:103` |
| face hover (overflow "+N" frame) | frame | `Fill` | `FillSolidBase -> FillSubtleSecondary -> FillSubtleTertiary`, gated on `live` | 83 ms | FluentStandard | — | unaffected | `FacePiles.cs:133-134` |
| avatar-frame hover (ring) | frame | `Fill` | selected `AccentDefault -> AccentSecondary`; unselected+clickable `FillSolidBase -> AccentSubtle`; inert `-> Transparent` | 83 ms | FluentStandard | — | unaffected | `FacePiles.cs:98, :104` |
| grid/video/row hover arm | `hovered` signal | bool | false -> true on the **second** `OnPointerMoveWithin` > 0.5 DIP away | — | — | — | gate is motion-independent | `MediaCard.cs:285-286, :898-899, :1029-1030, :1059-1060, :1201-1202` |

**Clock discipline.** `FlipCountdown` samples the wall clock **once** and adds `FrameTime.NowMs` deltas thereafter
(`FlipCountdown.cs:52-78`) — a QPC-derived monotonic clock, the `LyricsMediaClock` anchor pattern applied to a 1 Hz
display. `PreReleaseCountdown` is the documented exception: it re-polls `DateTimeOffset.UtcNow.UtcTicks` each tick
(`PreReleaseCountdown.cs:59`). Its two mount sites are singular and it counts days, so the drift is invisible —
but in 0.3 port it onto the `FlipCountdown` anchor pattern anyway, since the cost is one field.
`Environment.TickCount64` appears nowhere in this surface and must not.

---

## 6. Interaction

**Hover.** Every card owns a `HoverMotionGate` (a struct field on a `Component`, or a local captured by a static
factory's closures). `OnPointerMoveWithin` feeds the gate; only when a *second, different* sample lands (> 0.5 DIP
on either axis) does `hovered.Value = true`. `OnPointerExit` clears it. Once armed it stays armed for the
component's lifetime. This exists because the engine re-fires `OnPointerMoveWithin` at the same on-screen point
whenever new content lands under a resting cursor (`InputDispatcher.RefreshHoverAfterLayoutMove/AfterScroll`) —
without the gate, back-navigating pops every card under the mouse.
The *engine-serviced* channels (`HoverOpacity`, `HoverScale`, `HoverFill`, `WhileHover`) need no signal at all and
resolve off the nearest interactive ancestor — which is the card root, so the cover zoom and the FAB reveal fire
together. **The hover scope must never be hoisted above one card**: `AnimScheduler.SetHoverDescendants` recurses
through non-interactive wrappers, so a hover boundary on a shelf root would pop every card in the strip
(`Rail.cs:56-70` records the whole investigation).

**Click.** The card root carries `OnClick`. Nested affordances win by being hit first:
`PlayFab` (play/pause), `CoverActionFab` (navigate), `MoreCorner`/`MoreInline` (`ClickRequestsContext = true`),
`CardLibraryAction`, `SaveButton`. All of them set `BlocksDragArm = true`.

**Play/pause semantics — two different predicates, and they must stay different.**
- *Relates* (`NowPlayingMatch.RelatesToPlaying`): this card is the playing context, the playing item, or the playing
  item's album/artist. **Relating only ever REVEALS the equalizer.**
- *Owns* (`NowPlayingMatch.OwnsPlayback`): this card **is** what is playing. Only an owning card's FAB shows the
  Pause glyph and only an owning card's click toggles pause/resume; everything else plays the card
  (`MediaCard.cs:1387-1401`). An artist card of the playing track relates but never owns, so its glyph stays ▶.
- The toggle is **optimistic**: `b.IsPlaying.Value = !p` first, then `PauseAsync`/`ResumeAsync`.

**Double-click.** None on this surface — a single click opens.

**Right-click / Menu key / long-press.** `BoxEl.WithMenu(MenuAttach?)` -> `WithContextMenu(overlay, factory)`
(`ActionServices.cs:73-81`). The model is built **lazily at open**, never per render. The "…" button re-enters the
same funnel via `ClickRequestsContext`, so the walk finds the card root's `OnContextRequested` and anchors the
flyout at the button. Contents: see **W26**. Menu *rows* and their order are `Menus.ContainerRows`
(`Menus.cs:586-603`): Follow (artist only) -> Add to playlist ▸ -> Open -> Pin/Unpin -> Go to artist (album only)
-> Share ▸ -> Go to artist radio (artist only). Icons come from `ActionIcons.*`. Every row's enablement is a
`static c => …` predicate on `ActionContext`, never a capture.

**Keyboard.** The card root is not focusable in 0.2.9 — only its nested buttons are (`CoverActionFabCore` sets
`Focusable = true`; `FacePiles` live faces and the filter chips are focusable; `Rail`'s chevrons were focusable
only when enabled, with `AllowFocusOnInteraction = false`). **This is a real gap** — see §9.

**Drag and drop.** Source = the card root itself (`Draggable = drag`), kind `WaveeDragKinds.Resource`, payload
`WaveeResourceDragPayload.ForEntity(kind, uri, name, cover, acts)` built by a factory that runs **once at
promotion** (so the frozen ctor field is correct). Track rows use `ForTrack(t)`. Targets/cues/chip: `01-track-row.md`
and `25-sidebar.md`. Refusals carry a caption (`drag.cantAddArtist`, `drag.nothingToAdd`, …). An **episode** uri
refuses a drag outright (no by-uri episode read exists).

**Tooltips.**
- `CoverActionFab` -> `ToolTip.WrapStable(factory, tooltip)`. The only live caller passes the literal
  **`"Go to album"`** (`MediaCard.cs:1425`) — a hardcoded English string, and **the only unlocalised user-facing
  string on this whole surface**. VERIFIED: `menu.goToAlbum` = "Go to album" already exists in
  `assets/loc/en-US.json`, so 0.3's port is a one-line `Loc.Get(Strings.Menu.GoToAlbum)`, not a new key.
- `CardLibraryAction` -> `artist.following` / `artist.follow` for artists+playlists, `detail.edit.saved` /
  `detail.edit.save` otherwise. (DEAD in 0.2.9 — only `EditorialCardCore` mounts it.)
- Persistent `NowPlayingOverlay` FAB -> `home.pause` / `home.play` (DEAD in 0.2.9 — its one mount site is
  `EditorialCardCore`, `MediaCard.cs:694`).
- `SearchHero`'s "…" -> `common.more` (`SearchHero.cs:72` — the only live `MoreInline` call site, `onDark: false`).
- Face pile -> `detail.likedFacts.artistTip` / `…ArtistTipAdded` / `…moreArtists`, at
  `showDelayMs: LikedLens.TipDelayMs` = **0f** (`LikedFactsPanel.cs:1348`) — instant, and the same constant every
  tooltip in the facts panel uses. Port the CONSTANT, not the literal.
- `MoreCorner` and `PlayFab` carry **no tooltip at all** — the hover-revealed corner "…" and the card play button
  are unlabelled in 0.2.9. Pair the a11y fix above with a tooltip in 0.3.

**Inline edit.** None on this surface (playlist inline edit: `06-playlist.md`).

**Selection.** None on cards. The filter chips are an *exclusive* selection (All + at most one, re-tap clears).

**Focus visuals.** Engine default focus ring; `FacePiles` widens it with `FocusVisualMargin = 1` all round,
`ContentFilterChips` with `2` all round (the rail is 40 tall to make room for it), `WaveeCta` keeps the stock
`-3` inset from `Button`.

**Accessibility names.** `Role = AutomationRole.Button` is set on: `MoreCorner` (`:126`), `MoreInline` (`:146`),
`CoverActionFabCore` (`:1231`, the only one that is also `Focusable = true`), `CardLibraryAction` (`:1267`, DEAD),
the **persistent** `NowPlayingOverlay` FAB (`:1495`, DEAD), the whole `MediaCard.Row` (`:1028, :1058`),
`HomeCards.Card`/`Row`, live `FacePiles` frames (`:100, :129`), and filter chips (`:81`, set even when unavailable).
`Rail`'s chevrons set it too (`Rail.cs:104`, DEAD).

**Three real gaps, corrected from the earlier draft of this section:**
- **`MediaCard.PlayFab` carries NO `Role` and no accessible name** (`MediaCard.cs:1077-1089`) — only `Cursor.Hand`
  and `BlocksDragArm`. The app's single most-clicked card affordance is invisible to a screen reader. It is also
  not `Focusable`, so it cannot be reached by keyboard at all.
- **`PagedShelf`'s pager chevrons carry no `Role`, no `Cursor` and no `Focusable`** (`PagedShelf.cs:1402-1409`) —
  the LIVE shelf pager is less accessible than the dead `Rail`'s.
- **Cards themselves carry no `Role` and no accessible name**, and no card root is focusable.
All three must be fixed in 0.3, not ported. See §9.

A `FacePiles` face that is *not* an affordance takes `AutomationRole.None` and `Cursor.Arrow`.
A merch row with no shop link likewise takes `None` — "an entry with no link is a plain listing, not a dead button".

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 source | 0.3 read | readiness predicate (skeleton until true) |
|---|---|---|---|
| Card cover | `Image?` (`Album.Cover` / `HomeCard.Image` / `Artist.Image`) | `h.ImageId` (`StringId`) | `h.Knows(XFields.Image)` |
| Card cover, playlist mosaic | `Image.MosaicTiles`: **shelf** card mosaics at >= 4 (`MediaCard.cs:1134`); `Surfaces.Artwork` (rows, pick foot) mosaics at >= 4 and falls back to `tiles[0]` at 1-3 (`Surfaces.cs:241-245`); **grid** card never mosaics — `ArtworkFill` always collapses to `tiles[0]` (`Surfaces.cs:295-296`) | **GAP-2** — see below | `Edges.PlaylistCoverTiles.Total(p) >= 4` |
| Card title | `.Name` / `.Title` | `h.TitleId` | `h.Knows(XFields.Title)` |
| Shelf subtitle (rich) | `HomeCard.Subtitle` HTML, `PlaylistRef.Subtitle` | `h.SubtitleId` + a parsed span table | `h.Knows(XFields.Description)`; see GAP-3 |
| Grid/row subtitle (plain) | `SpotifyExportMapper.ToPlainText(sub)` | `h.SubtitlePlainId` — computed **at commit**, never per frame (P11) | same |
| Album card subtitle "date · N tracks" | `AlbumExpand.AlbumMeta(album)` | `a.ReleaseDate` + `Edges.AlbumTracks.Total(a.Slot)` | `a.Knows(AlbumFields.ReleaseDate)` **and** `Edges.AlbumTracks.State[a] != 0` |
| Row subtitle "Song • {artists}" | `SearchAllList.Names(t.Artists)` | `t.ArtistLineId` (commit-computed `StringId`) | `t.Knows(TrackFields.Artists)` |
| Artist card shape (circular) | `HomeCardKind.Artist` / `SearchHit.RoundImage` | `h.Uri.Kind == EntityKind.Artist` | always known (the uri is parsed at the wire) |
| Play FAB present | `onPlay != null` at the call site — **but only `MediaCard.Shelf` takes `Action?`**; `GridCard`/`Row`/`VideoCard`/`QuickPick` take a non-nullable `Action` and always paint one | `Playback.CanPlay(handle)` — a pure predicate on kind + `Flags.Unavailable`, honoured by **every** factory | identity known |
| Row artwork present | `showArtwork` at the call site, from `AppearancePrefs.TrackArtworkHidden(settings)` (`WaveeSettings.HideTrackArtwork`) | `Settings.HideTrackArtwork` read **inside Render** (a live signal, not a mount-frozen bool) | live — a settings toggle must not need a remount |
| Row eyebrow colour | `WaveeColors.PremiumText` for an `AccessLabel`, `Tok.AccentTextPrimary` for a lyrics match, else `Tok.TextSecondary` | the `SearchHitEdge.Flags` bit decides (GAP-7) | result-edge payload |
| Play FAB glyph (▶ / ⏸) | `NowPlayingMatch.OwnsPlayback` + `bridge.IsPlaying` | `Playback.Owns(handle)` + `Playback.IsPlaying` signals | live — never gated |
| Equalizer visible | `NowPlayingMatch.RelatesToPlaying` | `Playback.Relates(handle)` (context / item / item's album / item's artist) | live |
| "…" menu present | `menu != null` at the call site | `Shell.ActionsFor(kind, target) is not null` | live |
| Card menu kind + rows | `EntityUri.KindOf(uri)` | `h.Uri.Kind` | parsed at the wire |
| Save / Follow state in the menu | `LibraryBridge.IsSaved(uri)` | `User.Me.Likes(t)` / `E.SavedAlbums.Contains(Me, a)` / `E.FollowedArtists.Contains(Me, ar)` | `E.<set>.State[Me] == 2` (complete) — **a half-loaded library must not render "Save" on a saved album** |
| Cover tint / shimmer | `CoverColorPlane.TryGetTint(url, light)` | **GAP-1** | — |
| Liked Songs treatment | `LikedSongsArtwork.For(uri, …)` -> `LikedCoverArt` over `LibraryStore.Liked` | `User.Me` + `E.Liked.Targets(Me)` + each track's `ImageId` | `E.Liked.State[Me] != 0` **and** >= `LikedCoverRules.MinTiles(style)` distinct tiles; else `LikedCoverSite.Stock` |
| Artist Pick quote / eyebrow / target | `PinnedItem` (`ArtistExtras`) | **GAP-4** | — |
| Pick release date | `PinnedItem.ReleaseAt` | `a.ReleaseAt` (int, seconds) | `a.Knows(AlbumFields.ReleaseDate)` |
| Pre-release countdown | `DetailModel.PreReleaseEnd` / `ArtistExtras.PreRelease` | `a.ReleaseAt` + `AlbumFlags.PreRelease` | `a.Knows(AlbumFields.Availability)` |
| Daylist countdown | `PlaylistSummary.DaylistExpiresAtMs` | `p.ExpiresAt` (int) | `p.Knows(PlaylistFields.Format)` |
| Filter chips (curated) | `content-filter/v1/liked-songs` | **GAP-5** | — |
| Filter chips (derived) | `ContentFilterTags.Derive(tracks)` over `Track.Tags` | `Edges.TrackTags.Payload(slot)` over the liked span | `E.Liked.State[Me] == 2` **and** the tag edge state per row |
| Face pile portraits | `Hydrator.EnsureManyAsync(uris, Identity, Revalidate)` | `Entities.EnsureRows(artistSlots, ArtistFields.Identity)` in the page's mount effect | `ar.Knows(ArtistFields.Image)` per face; initials render meanwhile (never a skeleton) |
| Stat tile values | per-surface | per-surface handle columns | the owning chapter's predicate |
| Search highlight span | `ChartTitleMatch.TryFind(title, query)` | same pure fn over `Strings.Resolve(h.TitleId)` | title known |
| Video card duration | `MusicVideo.DurationMs` | `t.DurationMs` on the video counterpart | `t.Knows(TrackFields.Video)` |

**The readiness rule for this surface.** A card is **all or nothing**: it renders when
`h.Knows(XFields.Title | XFields.Image)`, and until then its slot is the shelf's `SkeletonProxy` card (W5). Never a
title with a grey square, never a square with no title — that flicker is what the owner calls a regression. The
shelf's `cardHeight` delegate is `Controls.ShelfHeight`, a pure function of the fitted width, so the strip's extent
is identical whether the cards are skeletons or real: **a shelf never changes height when its data lands.**
Pages demand their whole model on mount (`Entities.Ensure` + `EnsureEdges` + one `EnsureRows` batch); a card factory
never fetches and a shelf never reports a visible range for fetching purposes.

### DATA GAPS

| gap | what the surface shows | 0.2.9 source | proposed 0.3 column / edge |
|---|---|---|---|
| **GAP-1 · cover palette** | the tinted placeholder under every loading cover; the grid/mosaic/thumb tiles; `NavPreview`'s click-time accent seed; the Artist Pick wash; the countdown ink | `SpotifyLive.CoverColorPlane` — an **image-keyed** plane (light + dark scheme each) fed by extension kind 179 (visual identity) and by a local grader; `WaveePalette.Accent/Lift/TextInk/ChromeFromPayload` | **Not an entity column** — it is keyed by *image url*, and several entities share one rendition. Add a 9th table: `ImageTable { Column<StringId> Url; Column<uint> TintLight, TintDark; Column<uint> AccentLight, AccentDark; Column<byte> Known; Signal<uint> Changed; Dictionary<StringId,int> ByUrl; }` in `Entities/Entities.cs`, with `Entities.Image(StringId url)` as the factory and a **paint-only** `Prop.Of` bind (never a component re-render) exactly as `CoverShimmer.PlaceholderFill` does today. `Spotify.Decode` fills it from kind 179; `Store.cs` persists it. The plan's §4.1-4.3 has no image table and no per-image column at all. |
| **GAP-2 · playlist cover mosaic** | a cover-less playlist paints a 2x2 of its four newest album covers (`Surfaces.Mosaic`), and the Liked treatments consume up to 16 tiles | `Image.MosaicTiles` (`IReadOnlyList<string>`) carried on the domain `Image` record | `EdgeTable<NoEdge> PlaylistCoverTiles` on `Edges` (parent = playlist slot, targets = album slots, ordered newest-first, capped at `LikedCoverRules.MaxTiles` 16), plus `Playlist.CoverTileSlots`. A `StringId[]` column would duplicate urls the album table already holds. |
| **GAP-3 · rich subtitles** | every shelf-card subtitle and every row subtitle can be an HTML fragment whose `<a href="spotify:…">` runs are individually clickable accent links | raw HTML string in `HomeCard.Subtitle` / `PlaylistRef.Subtitle`, parsed **per render** by `RichText.Parse` | Parse at **commit**, not at paint (P11). Add `Column<StringId> SubtitleId` (plain text, for grids/rows/menus) **and** an `EdgeTable<SpanEdge> SubtitleSpans` (`SpanEdge(int Start, int Length, byte Weight, int TargetSlot)`; `TargetSlot` 0 = plain run). `Controls.RichText` then shapes from the span table with zero parsing on the paint path. Keep `RichText.Parse`/`RouteForUri` as the CORE functions the decoder calls. |
| **GAP-4 · the artist's pinned item** | the whole Artist Pick panel: `Comment` (the artist speaking), `Eyebrow`, `Title`, `Subtitle`, `Cover`, `BackgroundImage` (authored campaign art), `TargetUri`, `Uri`, `IsUpcoming`, `ReleaseAt` | `PinnedItem` on `ArtistExtras`, from the artist Pathfinder `preRelease`/`pinnedItem` shapes | `ArtistTable` columns `Pin*`: `Column<StringId> PinComment, PinEyebrow, PinBackground; Column<int> PinTarget (slot); Column<uint> PinFlags (IsUpcoming)`, guarded by `ArtistFields.Pin`. The target's title/subtitle/cover come from the target handle itself, so only the **artist-authored** parts are new columns. |
| **GAP-5 · curated content-filter chips** | the Liked Songs chip bar's primary source — Spotify's own descriptor concepts, which routinely name concepts whose rows are not enriched yet (shown *disabled*, not dropped) | `content-filter/v1/liked-songs` -> `ContentFilterChipSet(Titles, EvidencedCount)` | `EdgeTable<StringId> LikedFilterChips` on `Edges` (parent = the user slot; payload = the concept's presentation-name `StringId`; `Total` = the curated count). Availability is then `Edges.TrackTags` membership over the liked span, computed by the *model*, never probed by the chip. |
| **GAP-6 · music-video counterpart facts** | `VideoCard`'s title + duration + thumbnail for the artist page's 16:9 shelf | `MusicVideo { Thumbnail, Title, DurationMs, TrackUri }` | already covered: `Track.VideoCounterpart` (slot) + `TrackFields.Video`. The **thumbnail** is a second image per track — add `Column<StringId> VideoImage` beside it rather than reusing `Image`. |
| **GAP-7 · `AccessLabel` / `MatchedLyrics` / `Detail` / `Meta` on a row** | the search row's eyebrow ("Lyrics match", "Included in Premium"), its audiobook blurb and its duration meta | `SearchTopHit` fields, per-query | Not entity state — these are **result-edge payloads**. Extend the plan's `EdgeTable<NoEdge> SearchResult` to `EdgeTable<SearchHitEdge>` with `SearchHitEdge(byte Kind, byte Flags /*MatchedTitle, MatchedLyrics, RoundImage*/, StringId AccessLabel, StringId Detail, StringId Meta, ushort MatchStart, ushort MatchLen)`. The plan's §4.3 lists `SearchResult` with `NoEdge`, which cannot carry any of this. |
| **GAP-8 · "is this card's context playing"** | the equalizer and the ▶/⏸ glyph on every visible card | `PlaybackBridge.HasActiveContext` / `.Identity` / `.IsPlaying` + the two `NowPlayingMatch` predicates | `Playback.Host` must publish the same **three** signals with the same coarse-first shape: a cheap `HasActiveContext` bool that an idle overlay can subscribe to *alone*, and only then the hot `Identity`. `Playback.cs` §4.7 in the plan has a reducer but names no such coarse signal; without it every card joins the identity fan-out. |

---

## 8. Pure rules to port verbatim

| name | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `MediaCard.ShelfHeight(cardW)` | `Components/MediaCard.cs:212` | the virtualized shelf's cross extent: `cardW + 72` (6 gutter + 20 plate pad + cover + 8 gap + 20 title + 2 + 32 subtitle) | none — **add one** | CORE section of `Platform/Controls.cs` |
| `HomeModuleLayout.GridCardChromeFor(titleLines, hasSubtitle)` + `GridCardChrome`/`GridTitleLineH`/`GridSubtitleBlockH`/`GridLabelOverhead` | `Features/Home/HomeModules.cs:507-536` | the grid cell's extra height above the square cover | none directly — **add one** | CORE section of `Platform/Controls.cs` (shared by Home, Browse, Search) |
| `HomeModuleLayout.ShelfCardMin/Max/EdgeFade/GridGap` | `HomeModules.cs:504-509` | 148 / 188 / 24 / 12 — the shelf and grid fit inputs | — | same |
| `FillRowVirtualLayout.Fit(main, min, max, gap, override, fixed, maxCols)` | **engine** `FluentGpu.Engine/Scene/VirtualLayout.cs:485` | columns + fitted card width | engine suite | unchanged (engine) |
| `FacePiles.SlotsIn(width)` / `VisibleFaces(width, total)` | `Components/FacePiles.cs:40, 46` | how many overlapping portraits fit and when one slot becomes "+N" | **none — the geometry constants are pinned only by comment.** Add `FacePilesTests` | CORE section of `Platform/Controls.cs` |
| `ContentFilterTags.Derive(tracks)` | `Wavee.Core` (engine-free) | which descriptor concepts earn a chip (a >= 3-carrier floor; a 2-carrier tag earns nothing) | `Wavee.Tests/ContentFilterTagsTests.cs` | CORE section of `Entities/User.cs` |
| `ContentFilterParser` | `Wavee.Core` | the curated `content-filter/v1` payload -> titles | `Wavee.Tests/ContentFilterParserTests.cs` | `Spotify/Spotify.Decode.cs` |
| `NowPlayingMatch.RelatesToPlaying` / `.OwnsPlayback` | `Components/NowPlayingMatch.cs` | the loose (reveal) vs strict (pause) relation — **the two must stay distinct** | `Wavee.Tests/NowPlayingOverlayMatchTests.cs` | CORE section of `Playback/Playback.cs` |
| `HoverMotionGate` | `Design/WaveeMotion.cs:141-159` | whether a pointer sample is real motion (0.5 DIP threshold, one-shot latch) | `Wavee.Tests/HoverMotionGateTests.cs` | CORE section of `Platform/Controls.cs` |
| `ScaleTier` + the three tiers | `Design/WaveeMotion.cs:30-48, 163-190` | 1.02/0.98, 1.04/0.96, 1.07/0.92, and the reduced-motion collapse to 1 | `Wavee.Tests/MotionSystemTests.cs` | `Platform/Design.cs` (see `00-design-system.md`) |
| `WaveeEntrance.DelayMs(index)` / `.Row(index)` | `Design/WaveeMotion.cs:102-127` | the 40 ms/item stagger capped at index 8 | `Wavee.Tests/EntranceStaggerTests.cs` | `Platform/Design.cs` |
| `RichText.RouteForUri(uri)` | `Components/RichText.cs:80-105` | entity uri -> app route key (`pl:` / `album:` / `prerelease:` / `artist:` / `show:` / `liked` / module) | via `Wavee.Tests/EntityUriTests.cs` + `DeepLinkParseTests.cs` | CORE section of `Shell/Shell.cs` |
| `RichText.Parse` / `.Decode` / `.ExtractHref` | `RichText.cs:107-155, 206-245` | HTML fragment -> spans; entity decode; unknown tags dropped, text kept | `Wavee.Tests/ExportMapperTextTests.cs` (the plain-text twin) — **add a span test** | CORE section of `Platform/Controls.cs`, **called from the decoder** (GAP-3) |
| `SearchHighlight.LineBoxFor(size)` | `Design/SearchHighlight.cs:75-80` | 14 -> 20, 12 -> 16, else `ceil(size*1.43)` | none — **add one** | CORE section of `Platform/Controls.cs` |
| `ChartTitleMatch.TryFind` | `Features/Browse` | the highlighted run in a chart card's title | `Wavee.Tests/ChartTitleMatchTests.cs` | CORE section of `Entities/Search.cs` |
| `LikedCoverRules.*` (`IsLikedCollection`, `Canonical`, `IsSquare`, `FitSide`, `Site`, `MinTiles`, `Tiles`, `FillCells`, `WallCellIndex`, `RainbowOrder`, `Effective`) | `Features/Detail/LikedCoverRules.cs` | the whole honesty ladder, incl. the 140-DIP treatment floor and the `MosaicCells` 4 fallback | `Wavee.Tests/LikedCoverRulesTests.cs` | CORE section of `Entities/User.cs` |
| `PreReleaseCountdown.Breakdown(TimeSpan)` | `Components/PreReleaseCountdown.cs:120-121` | per-unit remainders clamped at 0 | none — **add one** | CORE section of `Platform/Controls.cs` |
| `ImageDecodeScale.For(dipEdge, scale)` | `Design/Surfaces.cs:36-43` | the device-pixel decode budget: bucket to 8, clamp to [8, 2048] | via `Wavee.Tests/ZoomAutoPolicyTests.cs` neighbours — **add one** | CORE section of `Platform/Controls.cs` |
| `Menus.Card` kind ladder | `Actions/Menus.cs:534-548` | uri kind -> which menu shape (track / show / container / none) | `Wavee.Tests/EntityUriTests.cs` for the parse half | CORE section of `Shell/Shell.cs` (the one action table, Wave 4 owner I) |
| `ContainerTracks.CanResolve` | `Actions/ContainerActions.cs:298` | which kinds get the queue verbs + Add-to-playlist (album/playlist yes; artist/show/episode no) | `Wavee.Tests/WaveeDragRulesTests.cs` (same resolver) | CORE section of `Shell/Shell.cs` |
| `HomeModuleLayout.ShelfExtent(width)` | `Features/Home/HomeModules.cs:637-641` | the whole shelf's row height for the estimator: `32 (chevron header) + ShelfHeight(Fit(width).CardW) + 2 × 12`. The renderer and the estimator MUST return the same number — §9.11 | none — **add one** | CORE section of `Platform/Controls.cs` |
| `ContentFilterChips.VerticalExtent` | `Components/ContentFilterChips.cs:40` | `RailHeight 40 + Spacing.S 8 = 48` — what the chip rail contributes to stacked detail chrome | none — **add one** | CORE section of `Platform/Controls.cs` |
| `FacePiles.Outer` / `.Step` / `.MaxVisible` | `Components/FacePiles.cs:24-36` | 32 / 20 / 4 — the constants `SlotsIn`/`VisibleFaces` are derived from, and which `ArtistFacePile` + `CollaboratorFacePile` restate by hand | none — **add one**, pinning all three piles | same as `SlotsIn` |
| `LikedLens.TipDelayMs` | `Features/Detail/LikedFactsPanel.cs:1348` | `0f` — the instant-tooltip rung the whole facts surface (pile, sparkline, lens rows) shares | none | CORE section of `Platform/Controls.cs` |
| `ImageDecodeScale.Ceiling` | `Design/Surfaces.cs:25` | `2048` — the decode clamp's upper bound (the chapter's `[8, 2048]` above) | via `ZoomAutoPolicyTests` neighbours | with `ImageDecodeScale.For` |

---

## 9. Re-author notes

### What must not be simplified

1. **The hover plate is not a `HoverFill`.** It is a *separate, non-hit-testable sibling* under the content, carrying
   the fill, the stroke **and the shadow**, cross-faded 0->1. Collapsing it into `HoverFill` on the card root loses
   the stroke and the halo, and a parent clip would then shave the halo (`SceneRecorder`'s shadow-before-own-clip
   contract). **No `ClipToBounds` on a card root**: the plate carries the shadow and every child self-clips.
2. **`HoverElevatePaint = true`.** Without it a later sibling card overpaints the hovered card's lift halo. This is
   the design's `z-index: 2` and it changes neither layout nor hit-testing.
3. **The zoom container must not push its own rounded clip.** The RHI clamps Image/Gradient primitives to the
   *topmost* rounded clip only; a clip here would ride the scale out past the card and show square slivers at the
   corners (`MediaCard.cs:706-711`).
4. **Circular cards do not clip the overlay layer.** `ClipToBounds = !circular` on the cover stack — the FAB sits
   inside the cover's *rectangle* but outside the avatar circle (`MediaCard.cs:1147-1150`, `:233-235`).
5. **`LazyNowPlayingOverlay` is not an optimisation to drop.** It mounts the FAB *eagerly* (one hover-faded box +
   glyph per card) and the full equalizer subtree *only* for a card that relates to playback. The FAB was once
   mount-gated on the hover signal and (a) never armed for a card paged in under a stationary pointer, (b) unmounted
   the very node being clicked when the exit edge fired before release. Both failure modes are recorded in the file
   header (`MediaCard.cs:1285-1293`).
6. **The playback relation is a `UseComputed`, not a signal a `UseSignalEffect` writes.** The write-then-read cycle
   had an ordering hazard that doubled overlay mounts on card-heavy pages (`MediaCard.cs:1308-1318`).
7. **One shape on both legs of the overlay** — `[equalizer slot, FAB]`, with an empty `BoxEl` standing in — so the
   reconciler keeps the FAB's node while the equalizer mounts/unmounts beside it. A press in flight must never lose
   its target to a playback edge.
8. **`Grow = 1` + ZStack fill, never a captured width**, for the cover overlay: the card re-fits wider after mount
   and a captured width leaves the FAB floating mid-cover (`MediaCard.cs:1530-1537`).
9. **The 0.2.9 shelf card's `Grow = 1`** on the shell is what makes a measured shelf stretch every card to the
   tallest card's height — uniform panels, exact, no reserved worst case (`MediaCard.cs:1171-1175, 1195-1199`).
10. **The card's `RichText` subtitle is capped at 2 lines with an explicit `Width`,** and the *grid* title's
    highlight arm gets the **same** line budget as the plain title, so a filter narrowing the grid does not reflow a
    card from two lines to one (`MediaCard.cs:262-273`).
11. **`ShelfHeight` and `GridCardChromeFor` must stay a pure function the *renderer* and the *estimator* both call.**
    An estimate that disagrees with the rendered height makes the measured virtual list re-pin its scroll anchor
    mid-scroll, which reads as the feed jumping under the cursor.
12. **Three defects to FIX, not port** (they are the only places this chapter says "do not reproduce 0.2.9"):
    (a) `PlayFab` has no `Role`, no name, no focus stop (`MediaCard.cs:1077-1089`); (b) `PagedShelf`'s pager
    chevrons have none either (`PagedShelf.cs:1402-1409`); (c) the card ROOT is not focusable anywhere, so a
    keyboard user cannot reach a card at all. Everything else in this chapter is "port verbatim"; these three are
    not. Add `Role = Button`, `Focusable = true`, an accessible name from the handle's title, and a tooltip
    (`home.play` / `home.pause`, `common.more`, `menu.goToAlbum`).
13. **Every clamped label carries an explicit `Width` or `Grow+Basis=0`, and a `Trim`.** The one 0.2.9 exception is
    the filter chip's label (`MaxLines = 1`, no `Trim`, `Shrink = 0`, `ContentFilterChips.cs:100-104`), which
    overflows its capsule on a long concept name. Give it `Trim = CharacterEllipsis` in 0.3.
14. **Empty/error/offline copy is verbatim.** `common.emptyTitle` = "Nothing here yet";
    `common.emptySubtitle` = "When there's something to show, it'll appear here."; `common.errorTitle` = "Something
    went wrong."; `common.errorSubtitle` = "Check your connection and try again."; `common.retry` = "Retry";
    `common.offline` = "You're offline — showing saved content."; `common.offlineBanner` = "You're offline. Cached
    content still works; playback and edits resume when you're back." **No glyph, no accent action, no critical
    colour.**

### Traps

- **Props freeze at mount.** Every value that identifies or invokes a card (`uri`, `onPlay`, `onClick`, `cardW`,
  `menu`) must ride the **props channel** on a virtualized card, because virtualized parents reuse `ComponentEl`
  slots. A ctor-captured uri keeps pointing at the slot's *first* row — the visible-row/played-row split on Recents.
  A `DragSource` is the documented exception ("config, not data").
- **`Key` remounts.** `FlipCountdown` (on `ExpiresAtMs`), `PreReleaseCountdown` (on `ReleaseAt`), `PreSaveButton`
  (on the target uri), `LikedCoverArt` (on size:radius:morph), `CoverShimmer` (on `url:decodeWxH`),
  `RichText.Expandable` (on the whole content signature), `FlipCountdown`'s digit cells (on the interned numeral),
  `StatTile`'s value box (on `"v:" + value`), `FacePiles` frames (on **index**, never name — two credits can share a
  display name). Each key is load-bearing; dropping one produces a component frozen on stale data.
- **`ReuseGuard`.** `PagedShelf` ignores `CardAt`/`KeyOf`/`CustomPager`/`OnVisibleRange` in its props equality gate
  and reports a DEBUG note when a delegate's *Method* changes. The contract that buys this: **what a card renders
  must be a function of its item.** State the card *paints* ("saved", "playing") belongs IN the item or on a signal
  the card reads — never captured by the closure. In 0.3 the "item" is a handle and its table `Version`, which makes
  this contract free.
- **Zero-allocation scroll frames vs per-row richness — how 0.2.9 reconciled them.** (a) The shelf's *data* signal
  is written only when the items actually moved, so an equal-but-rebuilt props push re-renders no card; chrome rides
  a *separate* signal so a rebuilt header never rebuilds a card. (b) Each overlay subscribes to a **coarse**
  `(active, playing, playingHere)` triple whose setter suppresses on equality, so an unrelated track skip re-runs one
  cheap effect per card and schedules no render. (c) `CoverShimmer` **latches** `settled` and stops calling
  `UseImage`, unsubscribing from the global image epoch — a loaded cover never re-renders on another image's status
  change. (d) Palette tints are **paint-only** `Prop.Of` binds, so a landed grading marks one tile `PaintDirty`.
  (e) Interned numerals in `FlipCountdown` — a keyed remount per second must not also mint strings.
  **All five must survive the port**, or the Wave 5 alloc gate fails.
- **The pager hover scope.** See §6. Scope hover to the header row, never to the shelf.
- **`Skeletonized(false)`** on every hover-only affordance (FAB overlay `:40`, "…" `:132, :149`, `CoverShimmer`
  `Surfaces.cs:229`, the Pick's trailing CTA `:468`) — a hover-only affordance is not skeleton content, and an
  opaque shimmer component inside a `Skel.Region` otherwise maps to the deriver's default bar (a stray stripe
  across the cover).
- **Only `ShelfCard` is a `Component` in 0.2.9.** `GridCard` (`:228`), `VideoCard` (`:885`), `Row` (`:963`) and
  `QuickPick` (`:923`) are static factories that mint a **fresh `Signal<bool>` and a fresh `HoverMotionGate` on
  every call**. Inside a virtualized grid or list that is the exact defect the props table names for the shelf:
  every parent re-render hands `LazyNowPlayingOverlay` a new props record whose `Hovered` can never compare
  reference-equal, so the overlay's equality gate never fires and the gate's "has real input happened" latch is
  reset on every rebuild. In 0.3 **all five are `Component`s with a mount-stable field**; do not carry the static
  factory shape over "because it is simpler".
- **Two decode budgets, deliberately different.** Shelf covers decode at a fixed **256** square
  (`MediaCard.ShelfDecodePx`, `:25`) "stable across responsive card widths, avoids resize-time redecodes"; grid
  covers at `ArtworkFill`'s **256** default; the video thumb at **480**; the Pick foot cover at **96**; the menu
  header at **76**; a peek row at **64**. A `Surfaces.Shimmer` tile MUST be handed the same `decodeW/decodeH` as
  the `Image` it sits under or the load-state read forks a second decode (`Surfaces.cs:204-206`).

### Where plan §2 / §4.12 / §4.13 are wrong or too thin

1. **§2 puts `Design.cs` + `Controls.cs` at ~2 files inside a 7-file, 8,600-line `Platform/` folder shared with
   `Platform.cs`, `Platform.Host.cs`, `Modules.*`.** Wave 4 owner L is given "Design/*, Components/* minus entity
   rows/cards". `Design/` is **2,980** lines and `Components/` is **7,600**; even after moving `TrackRow` (1,136),
   `MediaCard`'s entity adapters and `ConcertUi` (1,187) elsewhere, `Controls.cs` lands at ~4,500-5,500 lines on its
   own. **Settled 2026-09-12:** the file's budget is `00-design-system.md` §9.4's **envelope of 4,500-6,000 for the
   whole file**, and it is split on day one into exactly four named partials — **`Controls.cs`, `Controls.Cta.cs`,
   `Controls.Art.cs`, `Controls.Picker.cs`** (the §5 rule allows this at +30 %). This chapter's earlier proposal of
   `Controls.Cards.cs` + `Controls.States.cs` is dropped in favour of that naming: the card/cover/shelf plate files
   into `Controls.Art.cs`, the accent CTAs into `Controls.Cta.cs`, the preview-card strips into `Controls.Picker.cs`,
   and the empty/error/offline states, chips, stat tiles, face piles, rich text and both countdowns into
   `Controls.cs`. This chapter's line budget below is a **share of that envelope, not an addition to it**.
2. **§2's split of "entity rows/cards" into the five `X.UI.cs` files is a drift hazard for exactly this surface.**
   `MediaCard.cs`'s own header says why: "Every rectangular media card composes THIS instead of hand-rolling its own
   plate, so the hover grammar can't drift between surfaces." If `Album.UI.cs`, `Artist.UI.cs`, `Playlist.UI.cs`,
   `Show.UI.cs` and `Search.UI.cs` each get a card, five Wave-5 subagents will produce five hover physics.
   **Required correction:** the card *shell* (`CardPhysics`, `CardShell`, `MoreCorner`, `MoreInline`, `PlayFab`,
   `CoverActionFab`, `FabGlyph`, `NowPlayingOverlay`, `ShelfCard`, `RowChip`, `ShelfHeight`,
   `GridCardChromeFor`) lands in `Platform/Controls.cs` in **Wave 4** (owner L), and each `X.UI.cs` contributes only
   a `Card(handle, cardW, style)` adapter that fills in cover/title/subtitle/menu/drag. Owners M/N/O/P must be told
   this explicitly or they will each re-derive the plate. (`KindChip` is **not** on that list — it is dead in 0.2.9;
   port it only if a 0.3 surface actually asks for an on-media kind capsule.)
3. **§4.12's `Track.Row` sketch has no hover state at all** — no `WhileHover`, no `HoverFill`, no hover-revealed
   affordance, no `HoverMotionGate`. It also uses `Height = 56` where this surface's media row is **64** (plain),
   **112** (large), auto with `MinHeight 64` (detail) and auto with `MinHeight 72` (audiobook). Four row heights,
   not one.
4. **§4.13's `AlbumPage` sketch shows "More by artist [card][card][card] ▸"** with no shelf contract at all: no
   `cardHeight` delegate, no min/max fit, no edge fade, no halo clearance, no pager. Wire it to
   `PagedShelf.Create(..., cardHeight: Controls.ShelfHeight, minCardW: 148, maxCardW: 188, gap: 12, edgeFade: 24)`
   or the album page's shelf will not match Home's.
5. **§4.1-4.3 hold no image/palette table** (GAP-1) and no cover-tile edge (GAP-2), and type `SearchResult` as
   `EdgeTable<NoEdge>` (GAP-7). All three are load-bearing for *this* surface, not nice-to-haves: without GAP-1 every
   loading cover is a grey hole, which is the single biggest visual regression available.
6. **§4.7's playback reducer names no coarse `HasActiveContext` signal** (GAP-8). Without it, every visible card
   subscribes to the hot identity signal and a track skip re-renders every overlay on screen.
7. **Missing from the §2 tree entirely:** `NavPreviewStore`/`DetailNav` (correctly — handles replace it, say so),
   the drag payload/rules (`Features/DragDrop/*`, ~900 lines — it is not in `Shell/` or `Platform/` in §2),
   `SearchHighlight`, `ImageDecodeScale`, `ContentFilterChips`, `FacePiles`, `StatTile`, `RichText`, and both
   countdowns. They all belong in `Platform/Controls.cs`; the budget in §2 does not account for them.

### Line budget

| | lines |
|---|---|
| 0.2.9, this surface's primary files | **4,398** |
| 0.2.9 dead code inside them (`QuickPick` 29, `EditorialCard`+`Core` 346, `WideEditorialDestination` 5, `CardLibraryAction` 44, `Rail` 112, `AccentCardFill/HoverFill` 10, `CardShell`'s unused `plateFill`/`persistent` arms, `GridCard`'s unused `accent`, **`KindChip` 19** — only caller is `EditorialCardCore` `:802`, **`MoreInline`'s `onDark: true` arm** — same, **`NowPlayingOverlay`'s `Persistent`/`Light` arm 18** (`:1482-1499`) — reached only from the dead `:694`, **`MoreCorner`'s `persistent` arm**) | **~600 — do not port** |
| Plan §2 target for `Platform/Design.cs` + `Controls.cs` (implied share of the 7-file/8,600-line folder) | ~2,500 combined |
| **Honest estimate — this surface's SHARE of `Platform/Controls.cs` (with `01-track-row.md`'s primitives excluded)** | **3,200 - 3,800** — a share, not a file budget: the file's envelope is `00-design-system.md` §9.4's **4,500 - 6,000**, inside which this 3,200 - 3,800 sits beside 01's 900 and 00's own ≈ 1,100 (arbitration 2026-09-12). Do not add these three together and call the sum a new number for the file |
| Plus the per-entity `Card`/`ListRow` adapters across `Album/Artist/Playlist/Show/Track/Search.UI.cs` | **~600** (100 each) — these are in the `X.UI.cs` files, outside the `Controls.cs` envelope |
| **Total for this surface in 0.3** | **~3,800 - 4,400** (3,200 - 3,800 inside `Controls.cs` + ~600 in the entity files) |

Net: roughly the 0.2.9 line count minus the dead code, plus what the span-table subtitle path (GAP-3) costs and
minus what the handle model saves (no `Image?`/`Album`/`PlaylistSummary` marshalling, no `NavPreview`, no
`ArtworkOrLiked` null ladder). **The plan's implied ~2,500 for all of Design + Controls is about half of what this
one surface needs.** Flag it before Wave 4 starts.

---

## 10. Parity checklist

Verify side by side against the kept 0.2.9 Release build
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`, launched with `--fake`.
Unless stated, window width **1280** (content column ≈ 968 DIP, 6 shelf cards @ 151.3) and **dark** theme.
"Static capture" = a screenshot; "hover capture" = a screenshot with the pointer parked; "frame recording" = a
short screen recording, frame-differenced.

1. **Home, resting shelf.** Route `home`, static capture. A shelf card at rest shows **no fill, no border, no
   shadow** — only the cover and two text lines on the page ground.
2. **Home, hovered shelf card.** Hover capture on the third card. The plate (fill + 1 px stroke + halo) is visible
   on **that card only**; its neighbours are unchanged.
3. **Lift distance.** Frame recording of a hover-in. The card translates **-4 DIP** and nothing else moves.
4. **No shadow-band change on hover.** Compare the halo of the hovered card against a *flyout* opened on the same
   page — the card's halo must be visibly smaller (blur 8 vs 16).
5. **Cover zoom.** Frame recording of a hover-in over 300 ms: the cover scales to **1.04** *inside* the card's
   rounded corners. No square slivers at the card corners at any frame.
6. **Card press.** Frame recording of a press-and-hold: scale 0.99 and an additional -1 DIP, both released on
   pointer-up.
7. **FAB reveal.** Hover capture: a **44 DIP accent circle** at the cover's bottom-right, inset **8** from both
   edges, with the ▶ glyph at 18.5.
8. **FAB fade only.** Frame recording: the FAB fades in over ~167 ms with **no travel and no entry scale** (the
   prototype's `translateY(8px) scale(.92)` is deliberately absent).
9. **"…" corner.** Hover capture: a **30 DIP** dark-scrim circle with a 1 px white hairline at the cover's
   **top-right**, inset 8. Hover it: it scales to 1.07 and its plate darkens.
10. **"…" opens the card menu anchored at the button**, not at the pointer.
11. **Card menu, album.** Right-click an album card. Header: 38 DIP art (radius 6) + title + artist. Strip:
    Play · Play next · Play after · Save. Rows: Add to playlist ▸, Open, Pin, **Go to artist**, Share ▸.
12. **Card menu, artist.** Right-click an artist card. Rows begin with **Follow/Following**; there is **no**
    Play next / Play after and **no** Add to playlist; the last row is **Go to artist radio**.
13. **Card menu, show.** Right-click a podcast card: exactly Play · Open · Pin · Share ▸.
14. **Card menu, episode.** Right-click an episode card (Home podcast module): **no menu appears at all.**
15. **Artist card shape.** Home Recents rail, static capture: artist cards are **circles**, their labels are
    **centred**, and their play FAB still sits at the *rectangle's* bottom-right, unclipped.
16. **Artist card with no photo** (`--fake` seeds one): shows PersonPicture **initials**, not a grey square.
17. **Liked Songs card.** Home "Jump back in" / Recents: the Liked tile shows the user's chosen treatment, **not**
    the purple stock heart, at every size ≥ 140 DIP; below 140 it shows a flat 2x2 mosaic.
18. **Cover placeholder is tinted.** Throttle or cold-start, static capture during load: the tile under each cover
    carries that cover's own colour, not a uniform grey.
19. **Shimmer breathes then settles.** Frame recording during load: opacity pulses 1.0<->0.5 on a 1 s cycle and
    **stops** once the image lands (no residual per-frame work).
20. **Shelf fit.** Resize the window and capture at content widths **600 / 800 / 968 / 1100 / 1400**: card counts
    **4 / 5 / 6 / 6 / 8** at widths 141.0 / 150.4 / 151.3 / 173.3 / 164.5.
21. **Shelf height never changes with data.** Frame recording of a cold Home load: the strip's height is
    `cardW + 96` from the first skeleton frame to the last real one — no vertical jump.
22. **Shelf skeleton.** Static capture during load: up to **6 real-shaped** shimmer cards fitted to the measured
    slot — never one wide sentinel card.
23. **Edge fade.** Scroll a shelf to a non-page-aligned rest: both edges carry a **24 DIP** soft fade; at page 0 the
    left fade is absent.
24. **Halo gutter.** Hover the **first** card of a shelf: its lift halo is soft at the left edge, not hard-clipped.
25. **Pager chevrons.** Static capture: two **32 DIP circles** in the header at `FillControlDefault`; the disabled
    one sits at **35 % opacity** (it does not disappear).
26. **Pager glide.** Frame recording of a next-page click: the strip glides (not jumps) and lands on a boundary.
27. **Search shelf snaps.** Route `search` -> a query with album results. Two-finger pan a shelf and lift mid-page:
    it settles onto a page boundary ~180 ms later. (Home shelves do **not** snap.)
28. **Grid card.** Route `home-section:` any "see all". Static capture: cover fills the cell, one title line, one
    subtitle line, cell reserve 52 above the square.
29. **Charts grid.** A chart section: **two** title lines, **no** subtitle, reserve 54.
30. **Search highlight.** Type a query that matches a chart title: the matched run sits in a 4-radius accent pill
    with on-accent ink, and the row **wraps between runs** rather than ellipsising the tail.
31. **Discography drawer owner.** Route `artist:` -> expand an album. The owning card has a **2 px accent border**
    and an always-on `FillCardDefault` plate; every other card is unchanged.
32. **Two FABs on a discography card.** Hover an expandable album card: a **36 DIP** scrim circle with the
    open-in-new glyph sits **directly ABOVE** the 44 accent play FAB, gap 8, both right-aligned and pinned to the
    cover's bottom-right — a vertical column, not a row. Both fade in together on one wrapper.
32b. **Clicking an expandable album card does NOT open the album** — it toggles the drawer. Only the ⧉ FAB
    navigates. (The only card in the app whose click is not "open".)
33. **Media row, plain.** Route `search` -> results. Static capture: 64 DIP tall, transparent at rest, 48 cover at
    radius 4, no border.
34. **Media row hover.** Hover capture: a dark veil over the 48 cover with a **centred 30 DIP** accent FAB; the row
    fill goes to `FillSubtleSecondary`.
35. **Top result hero.** Same route, first row: **112 DIP** tall, **84** art, title at 28/36/600, gap 16.
36. **Audiobook row.** Search "Audiobooks" facet: art + title/subtitle on row 1, duration + a 2-line blurb below,
    min height 72.
37. **Row subtitle links.** Hover an artist name inside a search row's subtitle: it is accent-coloured and
    clickable on its own; clicking it navigates to the artist, not to the row's target.
38. **Row type chip.** Recents: a capsule at `FillSubtleSecondary` with 12/16/600 tertiary text, right of the text
    column.
39. **Plated row.** Route `album:` -> the trailing column's "More by" section: rows are plated (radius 8,
    `FillCardSecondary`, 1 px stroke), unlike search's.
40. **Video card.** Route `artist:` with music videos: 16:9 thumb at radius 4 inside a card that **is** filled at
    rest and scales 1.02 on hover (the only card that scales its root).
41. **Now-playing equalizer on a card.** Start playback, then find the playing album's card on Home: a scrim pill
    with three animating bars sits at the cover's **bottom-left**, visible **without hovering**.
42. **Related-but-not-owning card keeps ▶.** With a track playing, hover its **artist** card: the equalizer shows,
    the FAB glyph is **▶** (not ⏸), and clicking it starts the artist.
43. **Owning card shows ⏸.** Hover the playing album's own card: the glyph is **⏸**; clicking pauses.
44. **Equalizer hides on hover for rows and inline slots.** Hover the playing track's search row: the centred
    equalizer fades out as the FAB fades in.
45. **No FAB where there is no play route.** Any shelf whose cards pass `onPlay: null` renders no button — verify
    no cursor-hand circle appears on hover.
46. **Hover does not arm from a resting pointer.** Park the pointer over where a card will appear, navigate away and
    back (Alt+Left, Alt+Right). Frame recording: the card that lands under the cursor does **not** pop.
47. **Empty state.** Route `library:local` with nothing installed (or any empty facet): display-face headline, one
    caption, **no glyph**, and the action (if any) is a **standard**, not accent, button.
48. **Compact empty state.** Open the queue rail with nothing queued: the same grammar at the 20/28/600 rung.
49. **Error state.** Force a failed section: "Something went wrong." / "Check your connection and try again." /
    [Retry] — no red, no icon — and one `Warning ui "Surface error shown…"` line in `%LOCALAPPDATA%\Wavee\logs`.
50. **Offline banner.** Disconnect: a caution-background bar, radius 4, 16 DIP info glyph in the caution fill, the
    message at Caption-secondary, Retry at the right.
51. **Filter chips.** Route `liked`: a single non-wrapping 40 DIP rail; "All" selected shows an accent fill with
    600-weight on-accent ink; an unevidenced chip is present, disabled, at `TextDisabled`, with no hover response.
52. **Chip exclusivity.** Tap a chip, then another: the first clears. Tap the active chip: it clears to "All".
53. **Chip hover border.** Hover an available unselected chip: the border goes to the accent.
54. **Face pile.** Liked facts panel: 28 DIP portraits in 2 DIP rings, each overlapped 12; the last slot is a "+N"
    frame when anyone would clip; the selected face's ring is accent.
55. **Face pile tooltip is instant** (no delay) and names the artist + count.
56. **Stat tile.** Album rail facts: 18/800 value over an 11 px caption on `FillCardSecondary` with a 1 px stroke;
    a long value **ellipsises inside** the tile.
57. **Stat tile value swap.** Frame recording while a value changes: it cross-fades **in place** (the tile does not
    resize).
58. **Pre-release card.** An announced-unreleased album: a 34 DIP indeterminate ring, "Coming soon" in the accent,
    and four unit tiles that wrap 2x2 in the narrow rail and to one row when wide. Hours/minutes/seconds are
    zero-padded; days are not.
59. **Daylist flip countdown.** Home hero with a daylist: HH:MM:SS in fixed cells, Segoe UI Variable Display at
    weight 300; frame recording of a second tick shows the old digit exit **upward** while the new one rises in,
    inside the cell's clip, with nothing reflowing.
60. **Countdown does not stall.** Record 60 s of the daylist strip: every second advances (the S3 #19 stuck-timer
    regression).
61. **Artist Pick, vertical.** Route `artist:` at a narrow content width: accent wash at the top, 32 avatar + name +
    "Artist pick", a 4-line 28/36 display-face quote, then (if art exists) a 150 DIP photo band with **no scrim,
    pills or FABs on it**, then the 44-cover foot row with the accent Play capsule.
62. **Artist Pick, horizontal.** Widen past the featured breakpoint: the photo becomes a **300 DIP right column**
    stretched to the full panel height and the quote grows so the foot row stays on the bottom.
63. **Artist Pick with no wide art:** no photo band at all, and **no blurred square stand-in** of the cover.
64. **Pre-save pick.** An upcoming pinned release: the trailing control is the Pre-save button (not Play) and the
    kind line carries an accent "Releases {date}".
65. **Drag from a card.** Drag an album card onto a sidebar playlist: the drag arms from the **card**, not from its
    gutter, and pressing the play FAB or the "…" never starts a drag.
66. **Reduced motion.** Enable Windows "Show animations in Windows" = Off, relaunch, hover a card: **no scale on
    anything** (card, cover, FAB, chip, face), the plate still cross-fades, and the countdown digits crossfade
    instead of sliding.
67. **Light theme.** Switch to light and repeat items 1, 2, 5, 9, 18, 41: the resting card is still invisible, the
    hover plate is `#B3FFFFFF`, the on-media scrim and ink are **unchanged** (still dark plate / white ink), and the
    placeholder neutral is `#F2F2F2`.
68. **Narrow window.** Resize to ~900 px wide and capture Home: shelves refit live (no remount flash), cards stay
    within [132, 188], and no text bleeds past a card edge anywhere on the page.
69. **Row artwork off.** Settings ▸ Appearance ▸ Lists ▸ turn "hideTrackArtwork" ON, then route `search`: TRACK
    rows lose their 48 cover **and their play FAB entirely**; artist / album / playlist rows keep theirs. Toggle it
    back with the page still open — the rows must change **without a navigation** (0.3: a live settings signal).
70. **Row eyebrow, two colours.** Search a lyric phrase: the "Lyrics match" eyebrow is `AccentTextPrimary`. Find a
    Premium-gated result: its "Included in Premium" eyebrow is `WaveeColors.PremiumText`, not the accent.
71. **Cover-less playlist, grid vs shelf.** Find a playlist with no cover. On Home's SHELF it paints a 2x2 mosaic
    of four album covers; in that section's "see all" GRID it paints a SINGLE cover (the first tile). That is 0.2.9
    behaviour; if 0.3 mosaics both, note it as a deliberate improvement, not a silent drift.
72. **Grid card, no drag.** Drag a Track or Episode card in a `home-section:` grid onto a sidebar playlist: the
    drag never arms (those two kinds pass `drag: null`). An Album/Artist/Playlist card in the same grid does arm.
73. **Expandable description.** Open a playlist with a long blurb: the collapsed body ends "… More" on its last
    line only, at 600 weight in the accent; clicking it uncaps the paragraph and appends a " Less" run. Neither is
    a separate button beneath the text.
74. **Chip rail absent, not empty.** Route `liked` on an account with no descriptor concepts at all: the 48 DIP
    of chip-rail chrome is **gone** (no "All"-only rail, no empty strip) and the tracklist starts higher.
75. **Face pile with nobody.** A Liked facts panel with zero resolvable artists renders **nothing** in the pile
    slot — not an empty ring, not a "+0" frame.
76. **Countdown with no window.** A daylist whose `ExpiresAtMs` is 0/absent renders **no strip at all** — not a
    dimmed 00:00:00. (The dimmed zeros are the *expired-but-known* case.)
77. **Pre-release card has no shadow.** Compare it against the album-facts stat tiles directly below it on the
    rail: the countdown plate sits flat on the page; every `MediaCard` plate casts `Elevation.Card`.
78. **Stat tile with a phrase.** A tile whose value wraps (`wrapValue`) takes two lines and the tile grows; a tile
    with a `trailing` child stacks it under the caption. Neither changes the tile's width.
79. **Audiobook row press.** Press-and-hold an audiobook row inside a PLATED list: its press fill is
    `FillSubtleTertiary` while its plated siblings press to `FillCardDefault`. (0.2.9 inconsistency — decide in
    0.3 whether to unify; do not port it by accident.)
80. **Menu rows that vanish.** Right-click a card whose uri cannot be pinned: there is **no** Pin row (not a
    greyed one). Right-click an artist: **no** Add-to-playlist row (not a greyed one).
81. **Liked Songs menu header.** Right-click the Liked tile anywhere: the flyout header's 38 DIP art is the flat
    2x2 of the newest likes, never the purple stock heart and never a provider cover.
82. **Keyboard reach (the a11y gate).** Tab through a shelf. In 0.2.9 nothing on a card is reachable — not the
    card, not the play FAB, not the "…" corner, not the pager chevrons; only a `CoverActionFab` is. In 0.3 every
    one of those must take focus, announce a name, and activate on Space/Enter. **This item is expected to FAIL
    against 0.2.9 — it is the one place 0.3 must be better, not identical.**

---

## 11. Audit log

Adversarial re-read of the 0.2.9 sources against this chapter (2026-09-12). Every line below is a change made to
this file, with the evidence that forced it. Paths are relative to `src/apps/Wavee/`.

### wrong (a stated value or shape did not match the code)

1. **W8 — the two discography FABs are a COLUMN, not a row.** `NowPlayingOverlay.FabReveal`'s cover arm is
   `Direction = 1, Justify = End, AlignItems = End, Gap = Spacing.S` (`Components/MediaCard.cs:1430-1436`), so the
   36 `CoverActionFab` sits **above** the 44 `PlayFab`, both right-aligned at the cover's bottom-right. The
   wireframe drew them side by side; parity item 32 was reworded to match.
2. **W15 — the video thumb was labelled "(9:16)".** It is 16:9: `ar = inner * 9f / 16f` on a thumb `inner` wide
   (`MediaCard.cs:888`). Also corrected: `inner = max(64, cardW-16)` there, not the shelf card's 48 floor
   (`:887`).
3. **§6 accessibility — `MediaCard.PlayFab` carries no `Role` and no name.** `:1077-1089` sets only `Cursor.Hand`
   and `BlocksDragArm`; it is not `Focusable`. The section claimed `Role = Button` on "the card root's nested
   FABs". Corrected, and `PagedShelf`'s pager chevrons (`fluent-gpu/.../PagedShelf.cs:1402-1409`, no `Role`, no
   `Cursor`, no `Focusable`) added as a second instance. Promoted to a numbered "fix, do not port" rule in §9 and
   to parity item 82.
4. **W12 — the eyebrow is not factory-suppressed on `large`.** `MediaCard.Row` gates `meta` and `detail` on
   `!large` (`:970-971`) but renders any `eyebrow` it is given (`:987`); it is the top-result CALL SITE that passes
   `large ? null : ...` (`Features/Search/SearchPage.cs:774`). A 0.3 `RowStyle.Hero` that forwards an eyebrow will
   render one.
5. **W22 — the face-pile tooltip delay is a named constant, and `FocusVisualMargin` is unconditional.**
   `showDelayMs: LikedLens.TipDelayMs` (`Components/FacePiles.cs:116, :150`), the constant being `0f` at
   `Features/Detail/LikedFactsPanel.cs:1348`. And `AvatarFrame` sets `FocusVisualMargin` on every face
   (`FacePiles.cs:103`) — only `OverflowFrame` gates it on `live` (`:132`), so "inert face: every channel at rest"
   was an overstatement.
6. **`KindChip`, `MoreInline(onDark: true)` and `NowPlayingOverlay`'s `Persistent`/`Light` arm are DEAD.** Their
   only call sites are `MediaCard.cs:802` and `:694`, both inside `EditorialCardCore`, which the chapter already
   lists as dead. §1's tree, §3's token table, §9.2's Wave-4 port list and §9's line budget all treated them as
   live. Corrected in all four places; the dead-code estimate moved 550 to ~600.
7. **§0 #7 — only `MediaCard.Shelf` can express "no play route".** `Shelf` takes `Action? onPlay` (`:201`);
   `GridCard` (`:224`), `Row` (`:956`), `VideoCard` (`:882`) and `QuickPick` (`:921`) take a non-nullable
   `Action`. The non-negotiable now carries the caveat and §7's readiness row says `Playback.CanPlay` must be
   honoured by every factory, not just the shelf.
8. **§5 — the 83 ms row/FAB brush ramps are the ENGINE DEFAULT, not authored.** Neither `PlayFab` nor
   `MediaCard.Row` sets `HoverDurationMs`; the reconciler falls back to `InteractionAnim.ControlFasterMs`
   (`fluent-gpu/.../Reconciler/Reconciler.cs:4812`). Annotated so a 0.3 port does not "restore" a value that was
   never written.
9. **`Menus` line references were off by ~10.** `Menus.Card` is `Actions/Menus.cs:544-571` (the chapter said
   534-548); `ContainerStrip` `:573-585`; `ContainerRows` `:587-604`; `ShowCard` `:617`; `Header` `:1146-1156`.

### missing (a state, element or rule the chapter did not cover)

10. **`showArtwork: false` — a media row with no artwork at all.** Driven by `WaveeSettings.HideTrackArtwork`
    (Settings > Appearance > Lists, `App/SettingsCatalog.cs:77`), read via `Design/AppearancePrefs.cs:17` and
    applied to TRACK rows only (`SearchPage.cs:660, :784`). New wireframe **W9b**, a §7 readiness row, and parity
    item 69. This is the chapter's only settings-driven visual branch and it was absent entirely.
11. **The row eyebrow has three colours**, not one: `WaveeColors.PremiumText` (an AccessLabel),
    `Tok.AccentTextPrimary` (a lyrics match), `Tok.TextSecondary` (the factory default). `SearchPage.cs:775-776`,
    `MediaCard.cs:989`. Added to W9, §7 and parity item 70.
12. **Grid cards never mosaic.** `Surfaces.ArtworkFill` collapses `MosaicTiles` to `tiles[0]`
    (`Design/Surfaces.cs:295-296`) while the shelf card paints the 2x2 (`MediaCard.cs:1134-1135`) and
    `Surfaces.Artwork` mosaics at >= 4 / falls back to the first tile at 1-3 (`Surfaces.cs:241-245`). The same
    cover-less playlist therefore reads differently on two surfaces. Added to W6, §7 and parity item 71.
13. **`RichText.Expandable`'s "... More" / "Less" affordance** — the native `OverflowSuffix` run (600 weight, link
    colour, own click), `MaxLines = 0` when expanded, loc `common.more` / `common.less`
    (`Components/RichText.cs:183-202`). New wireframe **W26b**, two §3 rows, parity item 73.
14. **`StatTile`'s `wrapValue` and `trailing` arms** (`Components/StatTile.cs:26-27, :54`) and the fact that
    `layout` is overridable (`:49`). Added to W23 and parity item 78.
15. **`ContentFilterChips.Build` returns null on an empty set** (`:50`) so the caller omits the row; `Derive`
    marks everything evidenced by construction (`:29-33`); the rail's edge fade is the engine default 36, not the
    shelf's 24 (`:73`); the chip label has no `Trim` and `Shrink = 0`, so a long concept overflows. W21 + §9.13 +
    parity item 74.
16. **`FacePiles.Strip` renders nothing for an empty list** (`:76`), `MaxVisible` defaults to 4 (`:36`), the
    overflow frame has its own hover/press fills (`:133-134`), and the caller may supply `overflow` explicitly.
    W22 + parity item 75.
17. **`FlipCountdown` renders an empty box when `ExpiresAtMs <= 0`** (`:84`) and carries a caller-owned
    `BottomMargin` (`:37, :105`); hero colon width is 8 and compact 6 (`:93`); `HeroRowHeight`/`CompactRowHeight`
    are restated as literals in two layout estimators (`:39-44`). W25 + parity item 76.
18. **The pre-release plate carries no `Shadow`** (`Components/PreReleaseCountdown.cs:65-71`) — the only card-like
    plate in this chapter that does not — the eyebrow/tiles column has `Gap 4` and `Grow 1` (`:78`), and the tick
    stops once released (`:59`). W24 + parity item 77.
19. **The card menu's Liked Songs header uses the user's treatment** (`Menus.cs:1152`), and both header strings run
    through strip-then-decode (`:1162-1163`). W26 + parity item 81.
20. **Menu rows that are ABSENT rather than disabled**: Add-to-playlist when no track set resolves
    (`Menus.cs:286-289`), Pin for a non-pinnable uri (`:597`), Save on Liked Songs (`:583`). W26 + parity item 80.
21. **Grid-card drag is null for Track/Episode kinds** (`Features/Home/HomeModules.cs:444-448`). W6 + parity 72.
22. **`GridCard` deliberately does not `Grow`** (`MediaCard.cs:247-249`), the mirror of the shelf card's
    `Grow = 1` (`:1199`). Added to W6 beside §9.9.
23. **The audiobook row's press fill is always `FillSubtleTertiary`** (`MediaCard.cs:1025`), not the plated/large
    ladder — an inconsistency inside 0.2.9 itself. W13 + parity item 79.
24. **Only `ShelfCard` is a `Component`**; `GridCard`/`VideoCard`/`Row`/`QuickPick` mint a fresh `Signal<bool>`
    and `HoverMotionGate` per factory call (`:228, :885, :963, :923`) — the exact defect §1.2's props table names
    for the shelf. New §9 trap.
25. **Six different decode budgets on this one surface** (256 shelf / 256 grid / 480 video / 96 pick foot / 76 menu
    header / 64 peek row) and the rule that a `Shimmer` tile must share the paired `Image`'s decode target
    (`Surfaces.cs:204-206`). New §9 trap.
26. **§5 gained five rows**: the FAB's deliberate *absence* of a press scale (the "Pac-Man wedge",
    `MediaCard.cs:1082-1084`); the row's deliberate absence of a press scale (`Design/WaveeMotion.cs:33-38`); the
    `Rail` chevron's `ScaleStandard` (`Rail.cs:103`); the avatar-ring and overflow-frame fill ramps
    (`FacePiles.cs:98, :104, :133-134`); the hover-arm gate itself as a discrete transition.
27. **§8 gained six pure rules**: `HomeModuleLayout.ShelfExtent` (which §9.11's estimator rule depends on),
    `ContentFilterChips.VerticalExtent`, `FacePiles.Outer/.Step/.MaxVisible`, `LikedLens.TipDelayMs`,
    `ImageDecodeScale.Ceiling`.
28. **§3 gained eight token rows**: the `Rail` viewport's 36 edge fade, the expandable suffix run, the rich bold
    run (700) and the unroutable-link run (styled but inert, `RichText.cs:119, :123`), the dead persistent FAB
    (glyph 0.38 x size, the `WaveeOnMedia.LightButton` ramp), the dead `CardLibraryAction` (glyph 17,
    `Icons.Heart` EB51 / `HeartFill` EB52), and BrowsePage's fixed 148 x 220 skeleton card.

### unverified to verified (no change of substance, but the claim now has evidence)

29. **`menu.goToAlbum` already exists** in `assets/loc/en-US.json` with the value "Go to album". §6's "UNVERIFIED
    as a loc key" is resolved: the 0.3 port is `Loc.Get(Strings.Menu.GoToAlbum)`, not a new key. It is also
    confirmed to be the **only** unlocalised user-facing string on this surface.
30. **Every number in §2/§3/§5 the audit could reach was re-derived and holds.** Checked against source:
    `Spacing.XXS/XS/S/M/L/XXL` = 2/4/8/12/16/24 and `Radii.Control/Card/Full` = 4/8/999
    (`fluent-gpu/.../Dsl/Spacing.cs`, `Radii.cs`); `Elevation.Card` dark 8/2/#33 and light 4/2/#1A, `CardHover`
    dark blur 16, `Flyout` blur 16 (`Elevation.cs:18-24`); `MotionTok` ControlFaster 83 / ControlFast 150 /
    ControlNormal 250 / StandardEnter 300 with their easings (`Animation/MotionTok.cs:164-168`);
    `Expressive.Slow` 400, `DistBase` 8, `BlurSmall` 2, `Fast` 250 (`Dsl/Expressive.cs:17-36`);
    `WaveeMotion.Faster/Fast/Standard` 83/167/250 and the three scale tiers 1.02/0.98, 1.04/0.96, 1.07/0.92
    (`Design/WaveeMotion.cs:39-59`); the on-media ladder 140/190/220 scrim, 58 hairline, 110 veil
    (`Design/WaveeOnMedia.cs:37-56`); `TintStrength` 0.55 and the `#2A2A2A`/`#F2F2F2` neutrals
    (`Design/Surfaces.cs:57-66`); `ShimmerMinEdge` 80 (`:203`); the breathe keyframes `[0:1, .5:.5, 1:1]` over
    1000 ms and the weak-GPU flat hold (`Surfaces.cs:455-488`); `ImageDecodeScale` bucket 8 / ceiling 2048
    (`:25-43`); `PagedShelf`'s ShadowClearance / LiftClearance / HaloBleed 12, `SnapGraceMs` 180,
    `CommitFraction` 0.25, the 6-card skeleton clamp and the 32x32 / radius 16 / glyph 13 / 0.35-disabled chevron
    (`PagedShelf.cs:256-322, :164, :1402-1409`); `FillRowVirtualLayout.Fit` re-run at all nine widths in W4 —
    every pair matches (`Scene/VirtualLayout.cs:483-500`); `HomeModuleLayout` 148/188/24/12 and
    `GridCardChromeFor` 52/72/54 (`HomeModules.cs:504-539`); `AlbumExpand` MinCol 180 / Gap 16 / CardChrome 50 /
    RowGap 20 (`ArtistPage.AlbumExpand.cs:418-425`); every glyph code (E712, E768, E769, E8A7, E76B, E76C, F136)
    against `fluent-gpu/src/FluentGpu.Controls/glyphs.json`; and every `common.*` / `detail.preRelease*` /
    `home.nextUpdateAt` / `artist.artistPick` string against `assets/loc/en-US.json`. §9's verbatim copy block is
    character-for-character correct.
31. **Every `MediaCard.cs` line reference in §0, §1 and §1.2 was checked and is accurate** (`:35, :68, :76, :83,
    :108, :135, :152, :172, :200, :212, :223, :306, :425, :489, :512, :881, :921, :955, :1065, :1077, :1091,
    :1094, :1107, :1145, :1169, :1205, :1209, :1240, :1294, :1374, :1390, :1407, :1505`), as were the tests §8
    names — `HoverMotionGateTests`, `MotionSystemTests`, `EntranceStaggerTests`, `ContentFilterTagsTests`,
    `ContentFilterParserTests`, `NowPlayingOverlayMatchTests`, `LikedCoverRulesTests`, `ChartTitleMatchTests`,
    `ExportMapperTextTests`, `EntityUriTests`, `DeepLinkParseTests`, `WaveeDragRulesTests`,
    `DesignTokenConvergenceTests` all exist. The four §8 entries marked "none — add one" are correct: there is no
    `FacePilesTests`, no `ShelfHeight` test, no `SearchHighlight` test and no `Breakdown` test.

### residual risk (not resolved by this audit)

32. `Design/WaveeCta.cs`, `Design/Surfaces.cs` and `Design/WaveeType.cs` were read only where this chapter cites
    them; their full surfaces belong to `00-design-system.md` and were not re-derived here.
33. `Features/Library/LibraryPage.cs` and `Features/Home/HomeCards.cs` call sites were not exhaustively walked —
    Home's authored skins are `11-home-cards-and-modules.md`'s surface and only their `ApplyCardPhysics` call is
    this chapter's business.
34. Nothing in this audit was verified visually. Every claim is source-derived; the §10 checklist is still the
    only thing that can confirm the *rendered* result.

### arbitration (a settled cross-chapter contradiction)

35. **arbitration 2026-09-12: `Platform/Controls.cs` has ONE budget and four named partials, and this chapter's
    number is a share of it.** Three chapters sized the file and gave three answers — `00-design-system.md`
    4,500-6,000, this chapter 3,200-3,800 for its own material (and ~4,500-5,500 in §9 item 1),
    `01-track-row.md` a 900 share "of a ~1,200 file budget", which cannot hold 900 + 3,200. The decision:
    **00's 4,500-6,000 is the envelope for the whole file**; this chapter's 3,200-3,800 and 01's 900 sit inside
    it rather than adding to it; and the day-one partials are fixed at `Controls.cs`, `Controls.Cta.cs`,
    `Controls.Art.cs`, `Controls.Picker.cs`, so this chapter's proposed `Controls.Cards.cs` / `Controls.States.cs`
    naming is dropped (the card plate goes to `Controls.Art.cs`, the states and the small controls to
    `Controls.cs`). Changed here: §9 item 1 and the two affected rows of the line budget. The 0.2.9 counts the
    argument rests on were re-checked against source this pass and both hold — `Design/` **2,980** lines,
    `Components/` **7,600**.
