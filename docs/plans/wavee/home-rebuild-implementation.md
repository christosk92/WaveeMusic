# Home rebuild — third pass, matched to the canvas (implementation plan)

Date 2026-09-26. Supersedes the presentation parts of `home-redesign-implementation.md` and
`home-redesign-remediation.md`; their data pipeline (planner, facet state machine, `HomeLayout` service, D1 header
images, D2 previews, fake latency in the fetch host) is kept.

Prototype: the Design canvas "Wavee Home Redesign" (`https://claude.ai/artifact/Qxb9bZUyCjqVsBv21gy8m8`). A local copy
of its boards is at `docs/plans/wavee/home-redesign/canvas-v3/*.dc.html` (Main = All dark full page, CSS lines 11-430
hold every metric; Light; Music; Podcasts; States = loading / first-run / audiobooks; Components; FacetSwitch; Motion).

Owner decisions for this pass: **keep the virtualized zone list** (fix it); **all boards in scope**; templates fill
their cell, shelves self-fit (`minCardW`/`maxCardW`), grids are `GridEl.MinColWidth`, no width math; loading via
`SkelRegionEl`; no band-aids; pure-rule tests only; no env switches; stock Fluent controls; on-screen verification.
Open owner calls resolved by default here: Because-you-like = a fixed grid capped at 6 clusters (canvas); All's
"From artists you follow" = prefetch `music-following-chip` when All opens (+1 request).

---

## 1. Defects → root cause → fix

| # | On screen | Root cause | Fix |
|---|---|---|---|
| D1 | "All MusicPodcastsAudiobooks" squashed in a boxed pill (compact band) | `Facet.UI.cs:226-233` sets `ItemMinWidth = 0` on `Segmented.DefaultStyle`; the engine item root has no padding (`Segmented.cs:271-296`, no padding field on `Segmented.Style :26-50`). | Band = stock `Segmented.DefaultStyle` at 32 with new `Style.ItemPadding` (E19, `(12,0,12,0)`). |
| D2 | Pivot words at rest wrong | `Facet.UI.cs:111-211` bends `Segmented` via parts; item root `Grow = 1` (`Segmented.cs:275`), `PressScale .96` (`:287`), pill slot; label line-height fights the 40-px item. | Pivot built from `BoxEl`+`TextEl` (`FacetWord`), no Segmented (§5.3). |
| D3 | Black opaque "Recents" band | `Zones.UI.cs:98-101` routes RecentGrid through `ChapterHeader` `.Sticky(44)` (`:180`) with `Fill = Tok.FillSolidBase` when stuck (`:173`) — opaque canvas colour. Title `Strings.Home.Recents` (`Screen.UI.cs:219`). | Recently played = non-sticky `ZoneHeader`, title `Strings.Sidebar.Section.RecentlyPlayed` + subtitle + "Listening history ›". Chapter bands (Made for you onward) stick with content-surface acrylic .94. |
| D4 | Rows paint between band and pinned header | `Screen.UI.cs:325` puts padding (32,24,32,24) on the dim host that contains the ItemsView: viewport starts 24 below the region so `.Sticky(44)` pins at 68 while the band covers 0–44. `FacetCompactBand` root has no fill (`Facet.UI.cs:274-285`). | ItemsView fills the region edge-to-edge; padding moves into rows; band root = acrylic + 1-px divider. |
| D5 | Recents 2 columns of tall rows | `Zones.UI.cs:357` `Ui.AutoGrid(300,16,64)`; `AutoGrid` forces RowGap==ColGap (`Factories.cs:154-155`). | `new GridEl { MinColWidth 260, ColGap 12, RowGap 8, RowHeight 64 }`. |
| D6 | No daylist card | `ZonePlanner.cs:81` classifies Daylist only for `SectionKind.HomeSpotlight`; `:306-311` builds only from it. Live All feed has no spotlight; the daylist card sits inside a Made-for-you band (`:52` `IsPersonalFormat` includes "daylist"; `Home.Host.cs ScanDaylist ~:745-775`). | `DaylistSource.Find(sections)` pure: spotlight → any usable `Format=="daylist"` card (hoisted, removed from its shelf by `PageDedupe`) → none ⇒ no card. Decoder already stages daylist window/header for any daylist card (`Spotify.Decode.Home.cs:623-670`). |
| D7 | Shelves 4-up, 28-px headers | `Zones.UI.cs:393` `176/236`; `TitleColumn` (`:188-201`) stacks title over subtitle. | Densities 6-up `180/206`, 8-up `136/152`, 16:9 `400/428` (+ backplate 12); `ChapterBand` §5.4. |
| D8 | Loading = one grey bar | `Screen.UI.cs:164` ShimmerSource → `SkeletonDeriver.Derive` reaches ItemsView's `ComponentEl` (no SkeletonProxy) → default arm `Bar(160, 14)` (`SkeletonDeriver.cs:161-165`). | `ShimmerSource: () => new BoxEl { SkeletonOverride = HomeSkeleton.Build(seed) }` (`SkeletonDeriver.cs:27` returns overrides verbatim); `SkeletonStyle(PulseMs 2000, PulseMin .6)`. |
| D9 | No progress bar / InfoBar on switch | `Facet.BusyBar`/`FailureBar` (`Facet.UI.cs:379-411`) never mounted. | Row 0 = `FacetPivotRow`: pivot + toggle + 3-px `ProgressBar.Indeterminate(NaN)` + `Flow.Show(failed, InfoBar)`; band carries its own bar. |
| D10 | No zone stagger | `ListOptions.Entrance` only honoured by `CreateBound` (`ItemsView.cs:678-679,:740`), ignored by `Create` (`:588-640`); `EnterExit` has no delay (`LayoutTransition.cs:81-82`). | E20 `EnterExit.DelayMs`; rows realized ≤ 400 ms after facet mount and index < 6 get `Enter(Dy 8, Opacity 0, DelayMs i*30)` via pure `ZoneEnter.Of(index, msSinceMount)`. |

## 2. Engine API (verified) and changes

Use as-is: `PagedShelf.Create<T>(items, cardAt, …, minCardW, maxCardW, gap, …, edgeFade, parts, keyOf, measured, cardWidthAgnostic, snap, controller, leadSpan, lift, onInvoke)` (`PagedShelf.cs:198-260`; `ShelfController.GoTo/Prev/Next :150-156`; `ShelfLift.None`). `PipsPager.Controlled` (`PipsPager.cs:78-86`; stock pips accepted). `ProgressBar.Indeterminate(float.NaN, parts)` (`ProgressBar.cs:75-84`; set `PartTrack` opacity 1 for the 1-px track). `InfoBar.Create(severity, title, message, onClose, isOpen, isClosable, isIconVisible, actionButton, …)` (`InfoBar.cs:159-174`). `HyperlinkButton.Create(text, onClick, Style?)` (`:92`; style Padding (8,4,8,4) → 28 tall). `Button.Create(label, onClick, ButtonAppearance, ControlSize, glyph, …)` (Medium = 32). `IconButton.Create(glyph, onClick, IReadSignal<bool> isEnabled, Style)` (`Style.Size` 28/32). `ToggleButton.Controlled`. `Segmented.Create` (`DefaultStyle` = canvas band look). `GridEl { MinColWidth, ColGap, RowGap, RowHeight }` (`Element.cs:503-507`). `ItemsView.Create` + `RepeatLayout.Extents` (KeyOf inert; Entrance ignored). `.Sticky(top, scope, engaged)` (`ScrollEffectSpec.cs:42-43`, clamp to scope end `ScrollEffectEval.cs:24-28`, pinned paints above siblings). `Flow.Show`. `SkelRegionEl` + `SkeletonStyle` + `Element.SkeletonOverride`. `BoxEl.AspectRatio`, `ImageEl.FocusY/DecodePx`, `Controls.Cover(url, aspect, corner, decodePx)` (`Platform/Controls.cs:288`). Image priority: virtual rows outside the visible band request Overscan (`Reconciler.cs:2000-2020`). `BoxEl.BorderDashOn/Off` (`:127-128`). `BoxEl.Acrylic : AcrylicSpec?` (`:153`; `AcrylicSpec(Tint, TintOpacity, BlurSigma, NoiseOpacity, LuminosityOpacity, Fallback)` `Foundation/Effects.cs:158-166`; slice cap 16). Hover-only children: `Opacity 0, HoverOpacity 1, HoverScopeTransparent`. 2-line clamp: `TextEl { MaxLines 2, Wrap, Trim, Height }`. Display face: `TextEl.FontFamily = "Segoe UI Variable Display"`. Motion: `Motion.ControlFaster 83 / ControlFast 167 / ControlNormal 250`; 333 = `HomeTok.MotionSlow`. `UseScrollThreshold`. `HitTestVisible : Prop<bool>`. No `FlexAlign.Baseline` → title row `AlignItems End` + subtitle `Margin.Bottom 2`.

Tokens (`Dsl/Tokens.cs:80-213`): `FillCardDefault`, `StrokeCardDefault`, `FillSubtleSecondary/Tertiary`, `FillControlDefault/Secondary/Tertiary`, `FillControlAltSecondary`, `StrokeControlDefault/Secondary/StrongDefault`, `StrokeDividerDefault`, `TextPrimary/Secondary/Tertiary/Disabled`, `AccentDefault/Secondary/Tertiary/Subtle`, `AccentTextPrimary`, `OnAccent`, `ControlElevationBorder`. Content-layer colour: `Design.Colors.ContentSurface` (`Platform/Design.cs:281`).

Engine changes (in `..\fluent-gpu`, default byte-identical, each with a VerticalSlice gate + guide note):

| Id | Change | Files |
|---|---|---|
| E19 | `Segmented.Style.ItemPadding : Edges4` (default `default`) applied to the item root's `Padding`. | `FluentGpu.Controls/Segmented.cs`; gate `gate.segmented.item-padding` |
| E20 | `EnterExit.DelayMs : float = 0` honoured where Presence seeds enter (same place `Element.Stagger` bakes `index*Stagger`). | `Foundation/LayoutTransition.cs`, `Reconciler/Reconciler.Presence.cs`; gate `gate.presence.enter-delay` |

## 3. Spec → implementation map

Type ramp: 12/16; 14/20 (400/600); 20/28 Display 600; 28/36 Display 400 (sel 600); 40/52 Display 600. Nothing < 12.

Colours (canvas → Tok): text → `Text*`; card-fill → `FillCardDefault`; card-stroke → `StrokeCardDefault`; ctl-fill/hover/press → `FillControlDefault/Secondary/Tertiary`; subtle hover/press → `FillSubtleSecondary/Tertiary`; divider → `StrokeDividerDefault`; accent → `AccentDefault/Secondary/Tertiary`, `OnAccent`; accent-text → `AccentTextPrimary`; accent 16 %/12 % tints → `AccentSubtle` / `AccentDefault with {A=.12f}`; chap-bg → acrylic of `ContentSurface` at .94 blur 16; band → same at .92; skeleton → `FillSubtleSecondary`; daypart seg track/done/fill → `StrokeControlDefault`/`StrokeControlStrongDefault`/`AccentDefault`; history dashed → `StrokeControlSecondary` (dash 3/3); light theme only: 1-px `StrokeCardDefault` on covers/lead/row art.

| Element | Canvas | Implementation |
|---|---|---|
| Page | padding 24 32 0 32; zone gap 40; footer 32 (48 non-All) | list fills region; row 0 padding (32,24,32,17); ZoneRow padding-bottom 40; x-padding 32 in bodies/headers |
| Facet row | 40 tall, mb −20; words Display 28/36 400/600, sec/primary, box 40, pad 0 8, radius 4, gap 24, first ml −8; subtle hover; colour 83 ms; reflow 260 | `FacetPivot` HStack of `FacetWord` |
| Following toggle | 32 (28 in band), pad 0 12 0 10, gap 8, r4, glyph 16/14; on = subtle fill + accent-text glyph; fade 167 | keep `FollowingToggle` |
| Progress bar | 3 px, full width, 1-px track, fade 83; top 40 or band bottom | `ProgressBar.Indeterminate(NaN)` wrapper opacity bound to `sw.Bar` |
| InfoBar | 48, r8, Error, "Couldn't load X" + Check your connection…, Retry + close; enter 167 from −8 | `InfoBar.Create(Error, …)` under `Flow.Show(failed)` |
| Compact band | overlay 44, pad 0 36 0 32, acrylic .92, 1-px divider (transparent while busy), band-in 167 from −8, show 64 / hide 56; Segmented 32/30, item pad 0 12, 14 px, sel 600 | `FacetBand` |
| Daylist card | fill+stroke r8, h 320, pad 12 12 12 32, cols 1fr/gap 32/540; text pad 20 0 12 space-between; eyebrow 14 sec; title 40/52 mt4; tags 14 tertiary links (sec, hover primary+underline) mt6; meta 12 tertiary mt4; actions mt20 gap 8: Accent Play min 120, Standard Shuffle, Subtle ♡, Subtle ⋯ (32); daypart: ring 16 + "Next daylist in **hh:mm:ss**" 14 + "· friday afternoon arrives at 13:04" sec; bar mt10 5 seg 4px r2 gap4; labels mt6 12 tertiary, current primary 600; image r4 focus 50/40 %; enter 333 +8px once | `Daylist.Card`, `DaypartTimeline` |
| Recently played header | baseline row gap 12, h 28, mb 12; 20 title + 14 sec sub; right link "Listening history ›" | `Chapter.ZoneHeader` (not sticky) |
| Recents grid | 4×2, gaps 8 rows × 12 cols; 8th = history; items enter 250 stagger 30 | `GridEl{260,12,8,64}` + `Stagger .03` |
| Row item | 64, pad 0 8, gap 12, r4, fill+stroke; art 48 r4 (round artist); title 14/600 1 line; caption 12 sec "Album · Tristam **· yesterday**" (ago tertiary); hover ctl-hover/press; hover-only 32 play; playing: stroke accent .5 + 10-px equalizer | `Cards.RowItem` |
| History item | transparent, dashed stroke; hover subtle fill; plate 48 accent .12 + history glyph accent-text; "Your listening history"; 7 bars 3w gap 2 max 16, today accent, "142 plays this week" 12 sec; chevron tertiary | `Cards.HistoryItem` |
| Chapter band | sticky 44, h 52, full-bleed pad 0 32, mb 8, acrylic when stuck; left title 20 + sub 14 gap 12; right gap 8: See all link · 1×16 divider (m 0 4) · ‹ pips › (28, disabled at ends) | `Chapter.ChapterBand` |
| Square item | cover 1:1 r4 (round artist/station); hover backplate −6 r8 subtle; play FAB 40 accent bottom-left 8/8 shadow 0 2 4 .26, fade 167 + 4 px rise; title mt8 14/600; caption 12 sec 2 lines fixed 32 (1 line 16) | `Cards.SquareItem` — card root IS the backplate (padding 6, r8); shelf gap 16−12=4 |
| Wide lead | 2 cells + gap, image height = square height, r4; caption 2 lines + tertiary meta; hover zoom 2 % 333 | `Cards.WideLead`, `AspectRatio 428/206`, `leadSpan 2` |
| 16:9 tile | 16:9 r4; title; 1-line caption; meta 12 tertiary; zoom | `Cards.WideTile` |
| Sub-header | m 24 0 8; 14/600 + 12 tertiary | `Chapter.SubHeader` |
| Release list | 2-col gap 4×24; row 64 pad 0 8 r4; art 48; title; badge 18 pad 0 6 r4 (subtle / new = accent .16 + accent-text) + caption 12; when col 88 tertiary; hover subtle; hover-only play | `Cards.ListRow`; `GridEl{480,24,4,64}` |
| Cluster card | 3-col gap 16; r8 fill+stroke pad 12 8 8; header pad 0 8 8 8 mb4 divider: "More like" 12 tertiary over name 14/600, trailing ⋯; ≤3 rows 56, art 40, pad 0 8 r4, hover subtle | `Cards.ClusterCard`; `GridEl{MinCol 400, 16,16}`, cap 6 |
| Browse tiles | 4-col 8×12; tile 68 pad 0 12 0 10 r4 fill+stroke; art 48 or glyph plate; title + 1-line caption; chevron; hover ctl-hover; Charts sub-header + 3 glyph tiles | `Cards.NavTile`; `GridEl{260,12,8,68}`; charts `GridEl{360,12,12,68}` |
| Starter | max 480 centred, mt 140, glyph tertiary mb16, 20/600, 14 sec m 8 0 20, Standard button | `Facet.EmptyFacet` |
| Episode row | 76, pad 0 8, gap 12; art 56 r4 + 32 accent play at 12/12 hover-only; title 14/600 2 lines; caption 12 sec "show · date · [video] 27 min"; E badge; hover-only ⋯; progress hairline 2px mt6 | `Cards.EpisodeRow` |
| Podcasts lead + rows | 428 / 1fr gap 16 | flex Wrap: lead Basis 428 MinWidth 320; rows Basis 480 Grow 1 |
| Continue listening | 3-col gap 16, card-filled 76 rows with hairline | `GridEl{380,16,16,76}` |
| Video tiles | 4-col gap 16, 16:9, 2-line title | `GridEl{280,16}` |
| Shows | 8-up squares | 8-up PagedShelf |
| Skeleton (States ①) | hero 300 (1fr/520, pad 12 12 12 32, fill+stroke): bars 220×12, 78%×36, 360×12, 160×10, buttons 120/100/32/32×32, bottom 320×12 + 5-seg; header 180×20 m 32 0 12; 4×2 row items (art 48 round/square + 2 lines); header 140×20; lead(2 cells)+4 squares + text lines; breathe 2 s 1→.6 | `HomeSkeleton.Build` authored geometry |
| First run (States ②) | no personal zones; generic shelves with 20 + 14 "why" header | falls out of the planner |

## 4. Page architecture

```
HomeScreen : Component                                   (Home/HomeScreen.cs)  Ctx: FacetCtx, HomeLayout
└─ BoxEl Grow=1 Direction=1 ZStack
   ├─ Palette.ShellTint
   ├─ SkelRegionEl
   │   Pending  → ShimmerSource: BoxEl{ SkeletonOverride = HomeSkeleton.Build(seed) }
   │   Failed   → FirstLoadFailed InfoBar
   │   Content  → BoxEl Key="facet:"+facet  Enter/Exit opacity 83
   │       └─ dim host (NO padding)  Opacity=Prop(.6|1)  HitTestVisible=Prop(!Dim)
   │           └─ ItemsView.Create(n+2, RowAt, RepeatLayout.Extents(ZoneRows.EstimateExtent, 320), ListOptions{Grow 1, None, RepaintBoundary, Scroll})
   │               ├─ [0] FacetPivotRow   padding (32,24,32,17): pivot ‖ toggle · 3-px bar · Flow.Show(failed → InfoBar)
   │               ├─ [1..n] ZoneRow  Key="zone:"+key  Enter=ZoneEnter.Of(..)  padding-bottom 40  ScrollScope=zone.Key
   │               │     ├─ ZoneHeader (Recently played) | ChapterBand.Sticky(44, scope, engaged) | none (Daylist)
   │               │     └─ body
   │               └─ [n+1] tail: CustomizeLink (All) / footer pad
   └─ Flow.Show(bandShown, FacetBand)   44, acrylic .92, divider, enter −8/167
```

All at rest (1440×900, content 1316):
```
 24  All   Music   Podcasts   Audiobooks                         [Following]
 84  ┌ Good morning, Chris · your daylist ──────────────┐ ┌ header image ┐
     │ scream teen pop friday morning   (40/52)         │ │  540 × 296   │
     │ tags · meta · [▶ Play][⇄ Shuffle][♡][⋯]          │ │              │
     │ ◔ Next daylist in 01:34:57 · … ▬▬▬▬▬ labels      │ └──────────────┘
 404 └──────────────────────────────────────────────────┘
 444 Recently played  Pick up where you left off              Listening history ›
 484 [◯ row][▢ row][◯ row][▢ row]
 556 [◯ row][▢ row][▢ row][⌛ Your listening history]
 668 Made for you  Daily Mixes refreshed…            See all │ ‹ ● ○ ›
 728 [ lead 428×206 ][206][206][206][206][206]
```
Scrolled: 44-px band on top (Segmented + Following), chapter band pinned at 44 with acrylic, one pinned at a time.

Virtualized list fixes: (1) ItemsView is the direct child of the dim host, no padding above it; (2) padding lives in rows; chapter bands span full width and pad themselves; (3) only chapters sticky; `ScrollScope = zone.Key` on the ZoneRow root ends the pin; (4) band is a ZStack overlay painted last; (5) `ZoneRows.EstimateExtent` from HomeTok: row0 24+40+3+17 (+56 failed); Daylist 360; Recents 28+12+2·64+8+40; CoverShelf 52+8+206+12+8+20+32+40; WideTiles 52+8+241+12+8+20+16+16+40; Cluster 52+8+2·(28+4+3·56+20)+16+40; Browse 52+8+2·68+8+52+68+40; tail 32/48; (6) entrance via ZoneEnter + E20; (7) row keys on the returned element.

## 5. Files

Kept: `Model.cs`, `ZonePlanner.cs` (edits §6), `PodcastPlanner.cs`, `Facets.cs`, `Facet.Rules.cs`, `Time.cs` (+additions), `EpisodeCaption.cs`, `LayoutFile.cs`, `Items.Rules.cs`, `SectionScreen.Rules.cs`, `Zones.Rules.cs` (+`ZoneRows`, `ZoneEnter`), `Reveal.cs`.

New/rewritten (old deleted): `HomeTok.cs` (← Metrics.cs), `HomeScreen.cs` (← Screen.UI.cs), `FacetPivot.UI.cs` (← Facet.UI.cs), `Chapter.UI.cs`, `Zones.UI.cs`, `Cards.UI.cs` (← Items.UI.cs), `Daylist.UI.cs`, `Podcasts.UI.cs`, `Skeleton.UI.cs`, `DaylistSource.cs`, `RecentsCells.cs`.

### 5.1 HomeTok.cs
```csharp
public static class HomeTok
{
    public const float T12 = 12f, L16 = 16f, T14 = 14f, L20 = 20f, T20 = 20f, L28 = 28f, T28 = 28f, L36 = 36f, T40 = 40f, L52 = 52f;
    public const ushort Regular = 400, Semibold = 600;
    public const string DisplayFace = "Segoe UI Variable Display";
    public const float CardRadius = 8f, CoverRadius = 4f;
    public const float FacetRowH = 40f, FacetWordPadX = 8f, FacetWordGap = 24f, FacetRowBelow = 20f;
    public const float BandH = 44f, BandShowAt = 64f, BandHideAt = 56f, BarH = 3f, InfoBarH = 48f, InfoBarSlot = 56f;
    public const float ChapterH = 52f, ChapterBelow = 8f, ZoneHeaderH = 28f, ZoneHeaderBelow = 12f, TitleSubGap = 12f, SubBaselineFix = 2f;
    public const float PagerBtn = 28f, LinkH = 28f, ButtonH = 32f, DividerH = 16f;
    public const float RowItemH = 64f, RowArt = 48f, ListRowH = 64f, ClusterRowH = 56f, ClusterArt = 40f, NavTileH = 68f, EpRowH = 76f, EpArt = 56f;
    public const float PagePadX = 32f, PagePadTop = 24f, ZoneGap = 40f, FooterPadAll = 32f, FooterPadOther = 48f;
    public const float ShelfGap = 16f, Backplate = 6f, BackplateRadius = 8f;
    public const float RecentsColGap = 12f, RecentsRowGap = 8f, ListColGap = 24f, ListRowGap = 4f, GroupGap = 16f, TilesColGap = 12f, TilesRowGap = 8f;
    public const float SubHeaderAbove = 24f, SubHeaderBelow = 8f;
    public const float PlayFab = 40f, EpPlay = 32f, FabRise = 4f, HistoryBarW = 3f, HistoryBarGap = 2f, HistoryBarMaxH = 16f;
    public const float DaylistPadL = 32f, DaylistPad = 12f, DaylistGap = 32f, DaylistArtBasis = 540f, DaylistArtAspect = 540f / 296f, DaylistTextBasis = 420f;
    public const float DaypartSegH = 4f, DaypartSegGap = 4f, DaypartRing = 16f;
    public const float StarterMaxW = 480f, StarterAbove = 140f;
    // shelf density: fit targets (6×206 / 8×150.5 / 3×428 at 1316) + 2·Backplate because the card root carries the backplate
    public const float SixMin = 180f + 2 * Backplate, SixMax = 206f + 2 * Backplate;
    public const float EightMin = 136f + 2 * Backplate, EightMax = 152f + 2 * Backplate;
    public const float WideMin = 400f + 2 * Backplate, WideMax = 428f + 2 * Backplate;
    public const float ShelfCellGap = ShelfGap - 2 * Backplate;
    public const float RecentsMinCol = 260f, ListMinCol = 480f, ClusterMinCol = 400f, TilesMinCol = 260f, ChartsMinCol = 360f;
    public const float ContinueMinCol = 380f, VideoMinCol = 280f, EpisodeRowsMinCol = 560f;
    public const int RecentsMax = 7, ClusterMax = 6, ClusterRows = 3, PodcastGroupRows = 4, ReleaseRowsMax = 6;
    public const float MotionXFast = 83f, MotionFast = 167f, MotionNormal = 250f, MotionSlow = 333f;
    public const float ZoneStaggerMs = 30f, ZoneEnterWindowMs = 400f, FacetReflowMs = 260f, BreatheMs = 2000f, BreatheMin = 0.6f;
    public const int ZoneStaggerCount = 6;
    public const float LeadAspect = 428f / 206f, WideAspect = 16f / 9f;
    // colours: theme-live thunks (never cached)
    public static ColorF LightCoverStroke => Tok.Theme == ThemeKind.Light ? Tok.StrokeCardDefault : ColorF.Transparent;
    public static ColorF AccentTint => Tok.AccentDefault with { A = 0.12f };
    public static ColorF PlayingStroke => Tok.AccentDefault with { A = 0.5f };
    public static AcrylicSpec ChapterAcrylic => Band(0.94f);
    public static AcrylicSpec BandAcrylic => Band(0.92f);
    static AcrylicSpec Band(float tint) { var c = Design.Colors.ContentSurface; return new AcrylicSpec(c, tint, 16f, 0f, 0.96f, c); }
    // interaction recipes: CardFillHover (card fill → ctl hover → ctl press), SubtleHoverOnly (transparent → subtle)
}
```
Text helpers in the same file: `HomeText.Title14`, `Caption12(maxLines, fixedHeight)`, `Chapter20`, `Pivot28`, `Hero40`, `Meta12Tertiary`. (Exact Tok/Theme/recipe API names: verify against the engine when writing.)

### 5.2 HomeScreen.cs
Keep from `Screen.UI.cs`: Props, PageFor, per-tab fields, route-arg effect, Demand, Compute, Verdict, LocalizedTitles (RecentlyPlayed → `Strings.Sidebar.Section.RecentlyPlayed`), SelectFacet*, RevertRoute, RememberFollowing, Prefetch, EmptyFacetFor, FirstLoadFailed, BuildWash, _lastShape. Replace Render tree, FacetContent, RowAt, Seed:
```csharp
new SkelRegionEl(
    Pending: () => verdict.Value == LoadState.Pending,
    Failed:  () => verdict.Value == LoadState.Failed,
    Content: () => FacetContent(model.Value ?? Seed(facetSig.Value), tab, scope),
    ShimmerSource: () => new BoxEl { SkeletonOverride = HomeSkeleton.Build(Seed(facetSig.Value)) },
    OnFailed: () => FirstLoadFailed(tab),
    Reveal: SkelReveal.Soft,
    Style: SkeletonStyle.Default with { PulseMs = HomeTok.BreatheMs, PulseMin = HomeTok.BreatheMin },
    Group: this, SmoothResize: false),
Flow.Show(() => bandShown.Value, Embed.Comp(() => new FacetBand())),

Element FacetContent(ScreenModel m, int tab, string scope)   // dim host has NO padding (D4)
{ … ItemsView.Create(n + 2, i => RowAt(m, i, tab, mountedAt), _zoneLayout, new ListOptions { Grow = 1f, SelectionMode = None, RepaintBoundary = true, Scroll = … }) … }

Element RowAt(ScreenModel m, int i, int tab, long mountedAt)
{
    if (i == 0) return Embed.Comp(() => new FacetPivotRow()) with { Key = "facet-row" };
    if (i > m.Zones.Count) return tail;                        // CustomizeLink (All) / footer pad 32|48
    var zone = m.Zones[i - 1];
    if (zone.Kind == ZoneKind.EmptyFacet) return EmptyFacetFor(m.Facet, tab) with { Key = "zone:" + zone.Key };
    long sinceMs = now - mountedAt;
    return Embed.Comp(new ZoneProps(zone, m.Facet), static () => new ZoneRow())
        with { Key = "zone:" + zone.Key, Enter = ZoneEnter.Of(i - 1, sinceMs) };
}
```
`_extentOf = i => ZoneRows.EstimateExtent(_model, i, failed: _switch.Peek().Phase == FacetPhase.Failed)`. When All opens, also prefetch `music-following-chip` (§6.3).

### 5.3 FacetPivot.UI.cs
`FacetPivotRow` (component): padding (32,24,32,17); a 40-tall SpaceBetween row of [HStack gap 24 role TabList with `LayoutTransition(Position|Size, 260 ms)` of `FacetWord`s, `FollowingToggle`]; `BusyBar(ctx.Switch)`; `Flow.Show(failed, FailureBar(ctx))`. Hover/focus index → 150-ms dwell prefetch (`FacetCache.PrefetchHoverMs`). Roving Left/Right/Home/End moves focus; Enter/Space select; selection never follows focus.
```csharp
static BoxEl FacetWord(string label, bool selected, bool first, Action onSelect, …) => new()
{
    Height = 40, Padding = (8,0,8,0), Margin = first ? (-8,0,0,0) : default, Corners = 4,
    AlignItems = Center, Justify = Center, Role = Tab, Focusable = true, TabStop = selected, OnClick = onSelect,
    BrushTransitionMs = 83,
    Children = [ new TextEl(label) { FontFamily = DisplayFace, Size = 28, LineHeight = 36,
        Weight = selected ? 600 : 400, Color = selected ? TextPrimary : TextSecondary, HoverColor = TextPrimary, Wrap = NoWrap } ],
}.Interactive(SubtleHoverOnly);
```
`FacetBand`: 44, padding (32,0,36,0), SpaceBetween, `Acrylic = BandAcrylic`, Enter(Dy −8, Opacity 0)/Exit(Opacity 0) 167; `Segmented.DefaultStyle with { Height 32, Padding All(1), ItemMinWidth 0, SelectedFontWeight 600, ItemPadding (12,0,12,0) }` (E19); `FollowingToggle` 28; bottom divider transparent while busy, `BusyBar` at the bottom edge. Selected-index sync in an effect (never a render write).
`BusyBar`: 3-px box, opacity bound to `sw.Value.Bar`, 83 ms, `HitTestVisible = false`, child `ProgressBar.Indeterminate(float.NaN, parts: track visible)`.
`FailureBar`: `InfoBar.Create(Error, Strings.Home.CouldntLoad(label), CheckConnection, onClose: ctx.Dismiss, actionButton: Button(Retry → ctx.Select(failed)))`, margin-top 8, Enter(Dy −8, Opacity 0) 167. `FacetContext` gains `Dismiss` (→ `FacetSwitch.Dismiss`).
`FollowingToggle`, `EmptyFacet` carried over with HomeTok numbers.

### 5.4 Chapter.UI.cs
```csharp
static TextEl Title(string t) => new(t) { FontFamily = DisplayFace, Size = 20, LineHeight = 28, Weight = 600, Color = TextPrimary, MaxLines = 1, Trim = CharacterEllipsis };
static TextEl Sub(string s)   => new(s) { Size = 14, LineHeight = 20, Color = TextSecondary, MaxLines = 1, Trim = CharacterEllipsis, Margin = (0,0,0,2) };
static BoxEl TitleRow(title, sub) => HStack AlignItems End, Gap 12, Grow 1, Shrink 1, MinWidth 0;
public static Element ZoneHeader(title, sub, linkLabel, onLink)   // Recently played: 28 tall, mb 12, x-pad 32, not sticky
public static Element ChapterBand(Zone zone, Action? seeAll, ShelfController? pager, Signal<bool> stuck, string seeAllLabel)
    // 52 tall, full width, x-pad 32, mb 8; right: link (28) · divider 1×16 (m 0 4) · chevron(28, isEnabled CanPrev) · ShelfPips · chevron
    // .Sticky(44, scope: zone.Key, engaged: stuck)
public static Element SubHeader(title, cap)   // m (0,24,0,8): 14/600 + 12 tertiary
```
Acrylic is a plain value: make the band a tiny component that reads `stuck.Value` and sets `Acrylic = ChapterAcrylic` only while pinned (re-renders one node per pin edge); transparent at rest.

### 5.5 Zones.UI.cs
```csharp
public sealed record ZoneProps(Zone Zone, string Facet);
public sealed class ZoneRow : Component
{
    readonly ShelfController _controller = new();
    readonly Signal<bool> _stuck = new(false);
    // root: Direction 1, Grow 1, Shrink 1, MinWidth 0, ScrollScope = zone.Key, Padding bottom 40 (ReleaseList row: bottom 40 kept, previous WideTiles row gap collapses)
    // Daylist      → Pad(Daylist.Card(zone)) with Enter(Dy 8, Opacity 0) 333
    // RecentGrid   → ZoneHeader(RecentlyPlayed, RecentsSub, ListeningHistory, GoRecents) + Pad(RecentGrid)
    // CoverShelf/MixedCovers/RadioShelf/ShowGrid → ChapterBand + ShelfPad(CoverShelf)
    // WideTiles    → ChapterBand + ShelfPad(WideTiles)
    // ReleaseList  → Pad(SubHeader("From artists you follow", "Released this week")) + Pad(ReleaseList)   (no band; sub-block of New Music Friday)
    // ClusterCards/PodcastGroups → ChapterBand(no pager) + Pad(ClusterGrid)
    // BrowseTiles  → ChapterBand + Pad(BrowseTiles) + Pad(ChartsBlock)
    // else         → Podcasts.Body(zone, this, _controller)
}
// Pad = x-padding 32; ShelfPad = x-padding 32 − 6 so covers align with the chapter title
```
Bodies: `CoverShelf` = `PagedShelf.Create(items (lead merged first), cardAt: lead ? Cards.WideLead : Cards.SquareItem, pager: None, controller, lift: None, minCardW/maxCardW Six|Eight, gap ShelfCellGap, snap Page, leadSpan lead?2:1, edgeFade 0, cardWidthAgnostic, measured, keyOf, onInvoke)`; `WideTiles` = same with Wide densities and `Cards.WideTile`; `ReleaseList` = `GridEl{480,24,4,64}` ≤ 6 `ListRow`; `ClusterGrid` = `GridEl{400,16,16}` ≤ 6 `ClusterCard`; `BrowseTiles` = `GridEl{260,12,8,68}`; `ChartsBlock` = SubHeader(Charts, Updated daily) + `GridEl{360,12,12,68}` of 3 chart tiles. `RecentGrid` keeps its Recents subscription + `WeekSummary`, builds cells via `RecentsCells.Of`, `GridEl{260,12,8,64, Stagger .03}` with cells `Enter(Dy 8, Opacity 0)` 250. Shelf cards: no Role/Focusable/OnClick on the root (E9, onInvoke).

### 5.6 Cards.UI.cs (no template takes a width)
- `Cover(url, aspect, round, zoom, onPlay, fab)`: `AlignSelf Stretch, AspectRatio, ClipToBounds, ZStack`, r4 or full, 1-px `LightCoverStroke`, fill `FillCardDefault`; zoom = inner box `HoverScale 1.02, HoverDurationMs 333, HoverScopeTransparent` (probe; fallback no zoom).
- `PlayFab(onPlay, 40, round)`: accent round, bottom-left 8/8 (centred for round), shadow, `Opacity 0/HoverOpacity 1` 167, rise 4 (`WhileHover OffsetY` — probe; fallback fade only).
- `Item(cover, title, caption, captionLines, meta, twoLineTitle)`: root padding 6, r8, subtle hover/press (the backplate), text block mt 8: Title14 (1 or 2 lines fixed 40), Caption12 (1 line 16 / 2 lines 32), Meta12Tertiary.
- `SquareItem(card, oneLine)`: round for artists/stations; `WideLead(card)`: header image, `LeadAspect`, zoom, caption 2 + meta; `WideTile(card, twoLineTitle)`: 16:9, zoom, 1-line caption + meta.
- `RowItem(RecentsCell)`: 64, gap 12, pad 0 8, r4, border bound to playing (PlayingStroke / StrokeCardDefault), `CardFillHover`; art 48 (round); title 14/600; caption row = equalizer (Visible bound to playing) + spans (secondary + tertiary "· when"); hover-only 32 subtle play.
- `HistoryItem(week)`: dashed `StrokeControlSecondary` 3/3, hover subtle fill, plate 48 `AccentTint` + history glyph `AccentTextPrimary`, "Your listening history", 7 bars + "{n} plays this week", chevron.
- `ListRow(card, typeLabel, isNew, when)`: 64, badge 18 (new = AccentSubtle + AccentTextPrimary; else FillSubtleSecondary + TextSecondary), when column 88, hover-only play.
- `NavTile(title, caption, imageUrl?, glyph)`: 68, `CardFillHover`, 48 art or `FillControlDefault` plate + 20 glyph, chevron.
- `ClusterCard(cluster, rows, footer?)`: pad (8,12,8,8), header pad (8,0,8,8) + divider + mb 4, rows 56/40; podcasts footer strip (6×28 thumbs, divider, See all N).
- `EpisodeRow(card, cardStyle)` and `EpisodeLeadCard`: from `Items.UI.cs:529-633` re-skinned to 76/56, 32 accent play inside the art, 2-line title (1 for Continue), caption with E badge + video glyph, hover-only ⋯, fill-fraction hairline.
Keep the bound NowPlaying idiom (no page re-render on track change).

### 5.7 Daylist.UI.cs
`Daylist.Card(zone)`: Wrap row gap 32, padding (32,12,12,12), r8, fill+stroke. Text column Grow 1 Basis 420 MinWidth 0, padding (0,20,0,12), space-between: eyebrow (`Strings.Home.HeroEyebrow` + YourDaylist) 14 sec; title 40/52 Display 600 max 2 lines; tags (links → `Shell.GoTo(Search, arg: tag)`); meta 12 tertiary "Playlist · Spotify · {n} songs"; actions mt 20: Accent Play (min 120), Standard Shuffle, IconAction heart (saved state bound), MoreButton. Bottom: `DaypartTimeline`. Art box Basis 540 Shrink 1 MinWidth 320 MaxWidth 540 AspectRatio 540/296 r4 clip, image FocusY .4, header image preferred.
`DaypartTimeline` (component): 1-Hz `UseInterval` writes `countdown` (Signal<string>), `elapsed` (float), `part` (Daypart); ring 16 + span text "Next daylist in **{t}** · {next} arrives at {time}" bound; 5 segments Grow 1 h 4 r2 gap 4 (done `StrokeControlStrongDefault`, else `StrokeControlDefault`; current contains accent fill fraction — bind `Grow` if bindable, else measured width); labels 12 tertiary, current primary 600 (re-render on the daypart edge only).

### 5.8 Podcasts.UI.cs
`Podcasts.Body(zone, row, controller)` with ChapterBand for every zone: EpisodeLead = Wrap row lead (Basis 428, MinWidth 320) + rows column (Basis 480 Grow 1 gap 4); ContinueEpisodes = `GridEl{380,16,16,76}` card-style rows; VideoTiles = `GridEl{280,16}` WideTile(twoLineTitle); EpisodeRows = `GridEl{560, col 16, row 4, 76}`; PodcastGroups = ClusterGrid 4 rows + footer; ShowGrid = 8-up CoverShelf.

### 5.9 Skeleton.UI.cs
`HomeSkeleton.Build(ScreenModel seed)` authored from States ① (see §3 row). All pieces are plain `BoxEl` bars (`FillSubtleSecondary`, r4), card surfaces `FillCardDefault` + stroke; no text; root `HitTestVisible = false`. Podcasts seed: 16:9 lead + 4 rows + 3 continue cards + 8 squares. The 6-col lead row: Wrap row with Basis fractions (or GridEl span if supported).

### 5.10 SectionScreen.UI.cs, Customize.UI.cs
Swap to `Cards`/`HomeTok`; `RepeatLayout.GridFit(HomeTok.SixMin, HomeTok.ShelfCellGap)`; structure unchanged.

## 6. Data work
1. `Home/DaylistSource.cs` (pure) — `Find(sections, roles)`: spotlight-role card → first usable card with `Format == "daylist"` (hydrated) → null. `ZonePlanner.PlanAll` calls it before `PageDedupe` so the hoisted card leaves its shelf.
2. `Home/RecentsCells.cs` (pure) — `RecentsCells.Of(items, whenByUri, liked, nowMs, tz, culture, loc)` → ≤ 7 `RecentsCell(Card, Round, TypeLabel, DetailLabel, WhenLabel, IsPlaying)`; planner caps Recents at 7 (`ZonePlanner.cs:319`). Liked count from the Liked table (`User.Page.Liked.cs:175`); "playing on {device}" from the Connect device signal (fallback `Strings.Home.When.PlayingNow`).
3. Followed-artist releases on All: planner input `following: IReadOnlyList<SectionInput>?` from the `music-following-chip` document (prefetched when All opens); block omitted until known.
4. Charts tiles: 3 tiles from `ChartSections` (`Browse.Rules.cs:35-45`) opening `Shell.RouteKind.BrowseSection`.
5. Loc keys (en-US / nl / ko-KR, under `home`): `recentsSub`, `yourListeningHistory`, `playsThisWeek({n})`, `updatedDaily`, `browseAll`, `releasedThisWeek`, `daylistMeta({n})`, `daylist.arrivesAt({when},{time})`, `daypart.early` = "Early morning", `zoneSub.{madeForYou,newMusic,becauseYouLike,jumpBackIn,radio,browse,newEpisodes}`, `chartsCaption.{top50Country,top50Global,viral50Country}`, `playingOn({device})`. Reuse existing keys where they exist.
6. `Time.cs`: `DaypartRules.Next`, `DaypartRules.Countdown(remainingMs)`, `DaylistNext.Caption(expiresAtMs, tz, culture, loc)`.

## 7. Deletions
- `Home/Screen.UI.cs`, `Facet.UI.cs`, `Items.UI.cs`, `Metrics.cs`, old `Zones.UI.cs`/`Podcasts.UI.cs` bodies.
- Superseded Wave-5 UI: `Entities/Home.UI.cs`, `Home.Cards.UI.cs`, `Home.Artists.UI.cs`, `Home.Customizer.cs`, `Home.Page.cs`, presentation halves of `Home.Rules.cs` and `Home.cs` (`HomeComposer`, `HomeFeedView`, `HomeGroup*`), `Home.Host.cs` `LandingPageFor/SectionPageFor/CustomizerPageFor`. **First move** the types still used by Browse/Search (`Browse.Page.cs`, `Browse.UI.cs`, `Search.UI.cs`): `HomeCardNav`, `HomeBrowseCards`, `HomeModuleLayout`, `HomeCards` shelf helpers, `HomeModules.{FoldDeck,DrillHeader,SectionGrid}`, `HomeFoldTile` → `Entities/Browse.Cards.cs` / `Browse.Modules.cs`, unchanged.
- Tests over deleted types (check each for a data-rule subject first): `HomeArtistRowLayoutTests`, `HomeHeroLayoutTests`, `HomeFacetStripTests`, `HomeLandingProjectionTests`, `HomeLandingRulesTests`, `HomeLayoutStoreTests`, `HomeLayoutTests`, `HomeComposerTests` (rewrite `HomeDecodeTests` `ModuleFor` asserts against `SectionRoles.Of`). Keep Browse-related tests.
- Rewrite `.claude/skills/wavee/home-layout.md`.

## 8. Tests (pure)
New: `DaylistSourceTests`, `RecentsCellsTests`, `ZoneEnterTests`, `ZoneExtentTests`, `DaylistNextTests`. Changed: `ZonePlannerTests` (daylist hoist, recents cap 7, following block), `HomeDecodeTests`. Engine: `gate.segmented.item-padding`, `gate.presence.enter-delay`.

## 9. Waves
| Wave | Work | Gate |
|---|---|---|
| 0 | snapshot branch `snapshot/home-remediation-2026-09-26`; `HomeTok.cs`; loc keys; engine E19 + E20 | engine build D+R, VerticalSlice, Engine.Tests D+R; app build D+R |
| 1 | A Cards · B Chapter+Zones · C Daylist+Time+tests · D FacetPivot · E Podcasts · F Skeleton · G DaylistSource+RecentsCells+ZonePlanner+Zones.Rules+tests · H Browse type move | build D+R, tests D+R (with Wave 2 in the same change) |
| 2 | HomeScreen.cs, deletions, SectionScreen/Customize swap, skill doc | build D+R, tests D+R, grep gates, then §10 |
| 3 | fix pass from captures; live account run | owner review |

UI brief rules: templates fill their cell; no width params; HomeTok only; hover = fill only; text ≥ 12; shelf cards never Focusable/OnClick on root; live values are bound Props; attach the canvas excerpt.

## 10. On-screen verification
`dotnet run --project src/apps/Wavee -- --fake`, sandbox-free, 1440×900, dark and light, stop by PID. Compare with boards: (1) All at rest vs Main top; (2) scrolled 616 vs Motion frame 2 (band, pinned chapter, no slit); (3) chapter hand-off; (4) page turn; (5) Music switch (dim, bar, stagger); (6) Music→Following failure InfoBar; (7) Podcasts vs Podcasts board, Audiobooks starter; (8) first-ever load skeleton vs States ①, first run vs States ②; (9) hover states vs Components; (10) reflow 1000×700 and 1920×1080; (11) diagnostics: image requests ≤ viewport+overscan, ReuseGuard silent, no page re-render on track change; (12) live account: daylist card, recents captions, lead + 16:9 tiles, one cluster card per baseline band, following rows.

## 11. Probe items (verify while implementing; fallbacks listed)
HoverScale through a transparent hover scope (fallback: no zoom); `WhileHover` OffsetY semantics (fallback: fade only); `Grow` bindability for the daypart fill (fallback: measured width); FAB shadow API name; GridEl column span (fallback: Wrap + Basis); dashed history border stays dashed on hover (accepted); stock PipsPager dots (accepted); Connect device-name signal location.

---

## Appendix C — cross-file contract for the Wave-1 agents (namespace `Wavee.HomeUi`)

Every agent codes against these signatures exactly. Add a helper you need to YOUR file (private) rather than to
another agent's. `HomeTok` (`Home/HomeTok.cs`) already exists; do not edit it — report a missing constant instead and
use a local `const` in your file meanwhile. Old files (`Items.UI.cs`, `Metrics.cs`/`ZoneMetrics`, `Screen.UI.cs`,
`Facet.UI.cs`) are deleted by the orchestrator in Wave 2; do NOT reference `ZoneMetrics` or `Items.*` in new code.
Read old code only to learn data access (HomeCard columns, Recents, Playback, Library, Shell routes).

```csharp
// Home/HomeText.cs  (orchestrator, Wave 0) — text helpers
public static class HomeText {
    TextEl Title14(string text, int maxLines = 1, float? fixedHeight = null);            // 14/20 600 primary, ellipsis
    TextEl Caption12(string text, int maxLines = 1, float? fixedHeight = null, ColorF? color = null); // 12/16 secondary
    TextEl Meta12Tertiary(string text);                                                   // 12/16 tertiary 1 line
    TextEl Chapter20(string text);                                                        // Display 20/28 600
    TextEl Body14(string text, ColorF? color = null);                                     // 14/20 400 secondary default
}

// Home/Cards.UI.cs  (agent A)
public static class Cards {
    Element SquareItem(HomeCard card, bool oneLineCaption);          // shelf cell; root = backplate; no Focusable/OnClick (E9)
    Element WideLead(HomeCard card);                                  // 2-cell lead
    Element WideTile(HomeCard card, bool twoLineTitle);               // 16:9 (shelf cell or grid cell)
    Element RowItem(RecentsCell cell);                                // Recently played cell (focusable, opens)
    Element HistoryItem(RecentsWeek week, Action onOpen);             // 8th Recents cell
    Element ListRow(HomeCard card, string typeLabel, bool isNew, string whenLabel);
    Element NavTile(string title, string? caption, string? imageUrl, string? glyph, Action onOpen);
    Element ClusterCard(ZoneCluster cluster, int maxRows, bool podcastFooter);
    Element EpisodeRow(HomeCard episode, bool cardStyle);             // Podcasts rows / Continue listening (cardStyle)
    Element EpisodeLeadCard(HomeCard episode);                        // Podcasts 16:9 lead
    // card actions (moved here from Zones/Items): 
    void Open(HomeCard card);  void Play(HomeCard card);
    IReadSignal<bool> NowPlaying(HomeCard card);                     // bound, per card (no page re-render)
}

// Home/Chapter.UI.cs  (agent B)
public static class Chapter {
    Element ZoneHeader(string title, string? subtitle, string linkLabel, Action onLink);            // not sticky
    Element ChapterBand(Zone zone, Action? seeAll, ShelfController? pager, Signal<bool> stuck, string seeAllLabel); // sticky 44
    Element SubHeader(string title, string? caption);
}

// Home/Zones.UI.cs  (agent B)
public sealed record ZoneProps(Zone Zone, string Facet);
public sealed class ZoneRow : Component { internal Element Band(...); internal static BoxEl Pad(Element); internal static BoxEl ShelfPad(Element); }
public static class Zones { Action? SeeAll(Zone zone); void GoRecents(); /* bodies */ }

// Home/Daylist.UI.cs  (agent C)
public static class Daylist { Element Card(Zone zone); }            // zone.Kind == Daylist, zone.Items[0] = the daylist card
public sealed class DaypartTimeline : Component { ... }

// Home/Podcasts.UI.cs  (agent E)
public static class Podcasts { Element[] Body(Zone zone, ZoneRow row, ShelfController controller); }

// Home/FacetPivot.UI.cs  (agent D) — replaces Facet.UI.cs (keep the public names FacetCtx, FacetContext, FollowingToggle,
//  static class Facet { EmptyFacet(...) } so HomeScreen/others still compile)
public sealed class FacetPivotRow : Component;   public sealed class FacetBand : Component;

// Home/Skeleton.UI.cs  (agent F)
public static class HomeSkeleton { Element Build(ScreenModel seed); }

// Home/RecentsCells.cs, Home/DaylistSource.cs, Zones.Rules.cs additions  (agent G)
public readonly record struct RecentsCell(HomeCard Card, bool Round, string Title, string Detail, string When);
public static class RecentsCells { IReadOnlyList<RecentsCell> Of(IReadOnlyList<HomeCard> items, long nowMs, /* whatever pure inputs */ ...); }
public static class DaylistSource { (HomeCard Card, int SectionSlot)? Find(IReadOnlyList<SectionInput> sections, ...); }
public static class ZoneRows  { float EstimateExtent(ScreenModel? model, int i, bool failed); }
public static class ZoneEnter { EnterExit? Of(int zoneIndex, long msSinceFacetMount); }
```

---

## Status 2026-09-26 — implemented, verified on screen (fake + live account)

Landed as planned, with these on-screen-driven deviations:
- **Failure InfoBar is an overlay** in `HomeScreen` (under the facet row), not part of list row 0: a realized
  `ItemsView` row is not re-measured when it grows, so the in-row InfoBar was clipped to a sliver.
- **Zone list keyed by facet** (`ItemsView … with { Key = "zones:"+facet }`): the facet root's key sits on the skeleton
  region's single child, which is diffed in place, so the list kept its mount-time props and never showed the new facet.
- **Sticky scope excludes the zone gap** (ZoneRow = outer gap box + inner `ScrollScope` box) so a pinned band releases
  where its content ends; the band's sticky rides a zone-level box so it paints above the shelf (PinsAboveSiblings).
- **Daylist card / skeleton hero never wrap**: text and art share the row by Grow from a 0 basis (1 : 1.2, art ≤ 540);
  the engine measures a grow child at its grown width and does not re-measure after shrink.
- **All facet ends on the canvas rhythm**: leftover generic server bands fold into Browse tiles (≤ 12) instead of one
  6-up shelf each; zone-subtitle fallbacks apply only to zones we title (never a server-named band).
- **Scope epoch**: `Compute`/`Verdict`/landing/demand read `Entities.ScopeEpoch` — a sign-in re-boots the tables and
  the page stayed on the skeleton forever on a live cold start.
- **Engine**: `SurfacePool.TargetCensus` read the render thread's retire list from the UI thread (crash
  "Collection was modified" on a facet switch) — now running totals (`LayerTargetCensus.WithRetiredTotal`).
- Fake host: a failed facet fetch marks Sections ASKED (and bumps without re-setting Known) so the failure path concludes.
- Shell: Home's route arg is a facet id — `Shell.Dest` no longer uses it as the tab title.

Open: the 5 pre-existing `gate.shelf.*` VerticalSlice failures (uncommitted PagedShelf work in the engine tree);
`AlbumReleaseFactsRulesTests.Length_IsSpelledOnce…` fails in the full suite (in-flight Album/Detail edits, not Home);
the skeleton was verified on a cold live start before its hero fix, not after (warm cache skips it).

---

# Fourth pass — width-aware forms (2026-09-26)


## Context
The rebuild transcribed the 1316-DIP board's pixels. On the owner's machine (1717×1150 px @150 %, right panel open →
inner content ≈ 756 DIP) every 1316-derived minimum breaks the design. Five root causes explain all reported defects:
1. **1316 minimums everywhere** (daylist art 320–540, `RecentsMinCol 260`, `ClusterMinCol 400`, `ListMinCol 480`, …) →
   grids collapse to 1–2 columns, the hero art out-grows its text.
2. **Template chosen by data, not by the cell** (`Zones.UI.cs:193-195` always mounts `WideLead`; the engine drops the
   span at ≤ 4 columns, `VirtualLayout.cs:436`) → letterboxed Discover Weekly in one square cell.
3. **Hover leaks at record time**: the page `ItemsView` wraps every zone row in an interactive `ItemContainer`
   (`ItemsView.cs:1714`, `ItemContainer.cs:191`) that gets `HoverWithin` for any pointer in the zone
   (`InputDispatcher.cs:3215`); `InheritedState.ForChild` passes that progress through the shelf slot roots
   (`SceneRecorder.cs:559-561`), so every card's `HoverFill` paints.
4. **Band material is an opaque neutral slab** (`HomeTok.cs:91-95`) over a wash-tinted layer.
5. **Skeleton bars use `Grow` inside columns** (`Skeleton.UI.cs:47-56`) → they grow vertically into 50-px slabs.

## Design intent (what each zone is, what flexes)
- **Page:** a Windows content page — cards on one layer, 40 between chapters, no two neighbouring templates alike,
  hover = fill change, accent only on Play/selection/progress/links/focus.
- **Facet pivot:** the page title as type (28/36); a compact 44-px band after scroll.
- **Daylist:** the one calm "play-now" card — eyebrow → name (biggest type) → tags as one secondary line → meta →
  Accent Play + Standard Shuffle + two subtle icons → countdown (+ daypart timeline when room). Art is a supporting
  inset. Flexes: art size, title 40/52 → 28/36, timeline shown only when wide.
- **Recently played:** a fixed 2-row grid of 64-px row items, dashed history item last; columns 2–4, cells = 2·cols−1.
- **Made for you / Radio:** paged big covers (never < ~150) with a 2-cell lead **only when ≥ 2 ordinary cells sit
  beside it**, else item 0 is a plain square with its square cover.
- **New Music Friday:** 16:9 header-image tiles + a 1–2-column release list.
- **Because you like:** grouped list cards, 1–3 columns. **Browse:** nav tiles 2–4 + 3 charts.
- **Chapter band:** transparent at rest, frosted page when pinned; title never truncates before the subtitle.
- **Skeleton:** the loaded page's geometry in thin (10–12 px) breathing bars, same column counts.
- **Podcasts:** 16:9 video lead beside rows only when both fit; otherwise the lead becomes row 1.

## Width behaviour (inner width; engine fitting + three content-driven form edges)
| | ~640 | ~756 (owner) | ~936 | 1316 | ~1536 |
|---|---|---|---|---|---|
| Daylist form | Narrow (260, square art 236, 28/36, countdown only) | **Compact** (260, art 300×236, 28/36, 1 tag line, countdown ≤2 lines, no segments) | Compact | **Wide** (320, 540×296, 40/52, full timeline) | Wide |
| Recents (MinCol 232, max 4) | 2 cols | 3 (5 + history) | 3 | 4 (7 + history) | 4 |
| Made for you (192..218) | 3, no lead | 4: lead + 2 | 5 | 6 | 7 |
| 16:9 tiles (412..440) | 2 | 2 | 3 | 3 | 4 |
| Release list (340, max 2) / Clusters (340, max 3) | 1/1 | 2/2 | 2/2 | 2/3 | 2/3 |
| Jump back in / Radio (148..164) | 4 | 5 | 6 | 8 | 10 |
| Browse (232, max 4) / Charts (200, max 3) | 2/3 | 3/3 | 3/3 | 4/3 | 4/3 |
| Podcast new episodes | rows only | rows only | lead beside rows | beside | beside |

## Engine changes (`C:\wavee\fluent-gpu`, default byte-identical, one VerticalSlice gate each)
- **E21** `PagedShelf.Create(…, leadCardAt)`: the lead template follows the *effective* span (`_effSpanSig`, read in
  `ShelfCardSlot.Render`). Gate `gate.shelf.lead-template-follows-span`.
- **E22** per-shelf `leadMinColumns` (`FillRowVirtualLayout.MinColsFor`, shared by `EffectiveLeadSpan` and
  `PageCountFor`); Home passes 4. Gate `gate.shelf.lead-min-columns`.
- **E23** `GridEl.MaxColumns` + `GridEl.AutoFillColumnCount(innerW, minCol, gap, max)` used by `FlexLayout.GridColCount`.
  Gate `gate.grid.auto-fill-max-columns`.
- **E24** `SceneRecorder.InheritedState.ForChild`: an interactive, non-`HoverScopeTransparent` node without its own
  progress resets hover/press to 0 for its subtree (the cascade's own boundary rule). Gate `gate.record.hover-fill-scope`.

## App changes (`src/apps/Wavee/Home`)
- **`Forms.cs` (new):** `FormBox<TForm>` — `UseMeasuredWidth(8)` container query that rebuilds only on a form edge
  (pure `Classify(width, previous)` with 16-DIP hysteresis). Pure rules + tests: `DaylistForm` (Wide ≥ 1080,
  Compact ≥ 640, else Narrow), `RecentsPlan` (`Columns` via `GridEl.AutoFillColumnCount(w,232,12,4)`, `Cells = 2c−1`),
  `PodcastLeadForm` (Beside ≥ 844).
- **`HomeTok.cs`:** replace 1316 minimums with the table above (+ `MaxColumns` per grid, `LeadMinColumns 4`); daylist
  form geometry; band/chapter acrylic → `LayerAcrylic(tint .45, luminosity .78)` / band `(.40, .75)` so the page wash
  shows through; `HoverMs = 83`.
- **`Daylist.UI.cs`:** `Forms.Of(DaylistForm…)`; tags as one `SpanTextEl` (MaxLines 1, ellipsis, `OnSpanClick` → search);
  countdown `Wrap/MaxLines 2`; timeline only in Wide; stock `Button.Create(…, ButtonAppearance.Accent, Icons.Play)` min
  120; heart/more as stock subtle `IconButton` 32 (no scale/hand cursor).
- **`Zones.UI.cs`:** recents via `Forms.Of<int>`; `CoverShelf` → `cardAt = SquareItem`, `leadCardAt = WideLead`,
  `leadMinColumns = 4`; all grids get `MaxColumns`.
- **`Cards.UI.cs`:** interactive roots (`RowItem`, `HistoryItem`, `ListRow`, `NavTile`, `ClusterRow`, `EpisodeRow`) get
  `HoverDurationMs = 83`; the 250-ms entrance transition moves off interactive cell roots (listening-history lag).
- **`Chapter.UI.cs`:** title `Shrink = 0`; new acrylic. **`FacetPivot.UI.cs`:** band acrylic + hairline divider.
- **`Podcasts.UI.cs`:** lead via `Forms.Of(PodcastLeadForm…)`: Beside = `[lead Width 428][rows Grow 1]`, Rows = lead as
  first `EpisodeRow`; new grid minimums.
- **`Skeleton.UI.cs`:** percentage bars as `[bar Basis0 Grow w][spacer Grow 1−w]` with fixed heights; hero per
  `DaylistForm`; recents/shelf grids share `HomeTok` (shelf = `GridEl{SixMin, MaxColumns 6}`).
- **`HomeScreen.cs`:** `ListOptions.ContainerFactory` = plain non-interactive lane box (zone rows aren't buttons).
- **`Zones.Rules.cs`:** extent seeds for the compact daylist / 2-row recents. Tests: `DaylistFormTests`,
  `RecentsPlanTests`, `PodcastLeadFormTests`, `ZoneExtentTests` update. Plan doc gets a "fourth pass" section.

## Order
1. Engine E21–E24 → engine build D+R, VerticalSlice (new gates green; 5 known shelf failures pre-existing), Engine.Tests.
2. App: `Forms.cs` + `HomeTok.cs` first, then parallel on disjoint files (Daylist / Zones+Chapter / Cards / Podcasts /
   Skeleton / FacetPivot / HomeScreen) + tests.
3. Build D+R (`-m:1`), `Wavee.Tests` D+R.

## On-screen verification (fake, then live account), dark and light
Window **A** 1717×1150 px @150 % with the right panel (inner ≈ 756) and **B** 1440×900 @100 % (inner 1316):
1. Daylist A = compact calm card (no truncation, solid accent Play, one tag line); B = the Main board hero.
2. Recents A 3×2, B 4×2; hovering a row/card lights only that item; history hover immediate.
3. Made for you A = wide lead + 2 squares; B = lead + 4; hovering the band/See all lights no card.
4. New Music Friday, Because you like, Jump back in/Radio, Browse follow the table.
5. Scroll: frosted band with the wash showing through, pinned chapter band frosted, clean hand-off.
6. Podcasts A rows only, B lead beside rows; skeleton (cold load) = thin bars, same columns.
7. Resize sweep 700→1600: form edges flip once, no flicker, no image refetch.

---

# Fifth pass — stock controls (2026-09-27)


## Context
Four hand-built passes were rejected ("dogshit handrolled band aids"). Home (~330 KB in `src/apps/Wavee/Home`)
re-implements what the engine and `Platform/Controls*.cs` already ship: a hand pivot (`FacetPivot.UI.cs WordBox`),
hand chapter bands with pips/parallax (`Chapter.UI.cs`), a width-query framework (`Forms.cs`), private card templates
+ hover recipes (`Cards.UI.cs`, `HomeTok`), a hand skeleton (`Skeleton.UI.cs`), a 5-segment daypart timeline, a
duplicate card router, and a virtualized `ItemsView` of zones with custom lanes/extents. The app's native pages
(Artist, Album, Browse, Search, Show) are a plain `ScrollView` column of `PagedShelf`/`GridEl` sections built from
`Controls.ShelfCard`/`MediaRow`/`ModuleHeader`. Home is rebuilt from exactly those pieces; data pipeline kept.

## Page (component tree)
```
HomeScreen (data half kept: Props..Prefetch, Compute/Verdict/Demand/SelectFacet/Seed/BuildWash)
└─ ZStack [ Palette.ShellTint,
   ScrollView(column){ Key/ScrollKey per published facet, Handle }            (Artist.Page.cs:389 idiom)
     Column Gap=Design.Size.SectionGapWide MaxWidth=PageMaxW Padding=PageWide
     ├─ Design.Type.SurfaceDisplay(greeting)
     ├─ FacetRow: SelectorBar.Create(words, index, onChange) · ToggleButton.Controlled(Following)
     │            · ProgressBar.Indeterminate(NaN) (opacity bound) · Flow.Show(failed, InfoBar Error + Retry)
     └─ SkelRegionEl(Content = Zones.Column(model), ShimmerSource = Zones.Column(Seed), Reveal None,
                     OnFailed = Controls.Vacancy(Error, retry))
          └─ keyed facet column (dim Opacity/HitTest binds), zones with Design.Entrance.Row(i), "Customize Home" link ]
```
No ItemsView of zones, no parallax, no compact band, no overlay InfoBar, no FormBox.

**Sticky headers, the app's own no-background idiom** (Browse.Page.cs:24-29,165-173; Artist.Page.cs:529-536): headers
paint NOTHING; the content under them cuts itself at the header's lower edge with `.StickyClip(inset)` and a top
feather `EdgeFade = new EdgeFadeSpec(EdgeMask.Top, band) { WhileStuck = true }`, so it dissolves into the header and is
never guillotined, and never softens at rest. Applied twice:
- **Facet row** pins at the top of the page (`.Sticky(0)`), transparent; the facet column below is
  `.StickyClip(facetRowH)` with the WhileStuck feather (the Browse masthead pattern: a spacer above the clipped node).
- **Chapter headers** (`ModuleHeader`) `.Sticky(facetRowH, scope: zone.Key)` inside a zone box with
  `ScrollScope = zone.Key`; the zone's body is `.StickyClip(facetRowH + headerH)` with the same feather, so the next
  zone's header pushes the previous one out at its section end (stock sticky scope clamp). No acrylic anywhere.
Chapter headers therefore live OUTSIDE `PagedShelf` (passed as a sibling above the shelf, not its `header:` arg) so the
header and the clipped body are siblings in the zone scope; the shelf keeps its own built-in pager placement via
`pager:` on the external header row (`ExternalChapterHeader` gallery idiom, `CollectionsMenusPages.cs:430`) — verify and
adopt whichever the gallery shows.

## Zone → control
| Zone | Header | Body |
|---|---|---|
| Daylist | — | `Ui.Card(HStack(Controls.Artwork 160 square, text))`: Eyebrow, `Design.Type.DetailHero` title (≤2 lines), tags one `SpanTextEl` (links → search), TrackMeta meta, `Button` Accent Play + Standard Shuffle, `Controls.SaveButton`, `Controls.MoreButton` + `HomeCardNav.MenuOf`; small `DaylistClock` (1 Hz) → countdown text + `ProgressBar.Create(elapsed, 200)` |
| Recently played | `HomeModules.ModuleHeader(title, sub, open: Recents)` | `GridEl{MinCol 220, Max 4, RowH 64}` of `Controls.MediaRow` (Liked → `Sidebar.Cover.Liked`); cap 8; history tile + sparkline deleted |
| Cover shelves (Made for you, NMF, Jump back in, Radio, Shows, Videos) | `ModuleHeader(title, sub, See all)` as `PagedShelf` header | `PagedShelf.Create` with `HomeCards.ShelfCell` / `HomeCardNav` — verbatim `Browse.Page.cs:547-560` (148–188, Chevrons+Pips, page snap); no wide lead |
| Release list | `Ui.SectionHeader` | `GridEl{320, Max 2}` of `MediaRow` + `RowChip` type + date trailing |
| Because you like / podcast groups | `ModuleHeader` | `GridEl{300, Max 3}` of `Ui.Card(SectionHeader + ≤3 MediaRow(40))` |
| Browse + Charts | `ModuleHeader` | `GridEl{220, Max 4}` of stock `SettingsCard` (art icon, chevron, click); Charts: 3 `SettingsCard` glyph tiles |
| Episodes / Continue | `ModuleHeader` | `GridEl{320, Max 2/3}` of `MediaRow(56, TitleLines 2, meta w/ ExplicitBadge + ProgressBar)` |
| Empty facet / failure | — | `Controls.Vacancy` |

Width (inner ≈696 at the owner's window): recents 3 cols, shelves 4 cards, lists 2, clusters 2, tiles 3 — all from
stock fitting; 1316: 4 / 7 / 2 / 3 / 4.

## Small shared/engine changes (default byte-identical)
- **E25** `SettingsCard.Options.HeaderIconElement` (image icon) + `Style.HeaderIconSize` — `fluent-gpu/src/FluentGpu.Controls/SettingsCard.cs`, gate + gallery sample.
- **S1** `Controls.NowPlayingOverlay` `.Skeletonized(false)` (no stray skeleton bar in covers) — `Platform/Controls.Art.cs:199`.
- **S2** `Controls.MediaRow` honours `CardData.TitleLines` — `Platform/Controls.Art.cs:652`.

## Deletions
`Home/Forms.cs, HomeTok.cs, HomeText.cs, Cards.UI.cs, Chapter.UI.cs, Skeleton.UI.cs, Podcasts.UI.cs, FacetPivot.UI.cs`
(→ ~80-line `Facet.UI.cs`), `Daylist.UI.cs` rewritten small, `Zones.Rules.cs` ZoneRows/ZoneEnter, `Reveal.cs` CardNav,
`Time.cs` RecentsWeek/WeekSummary; HomeScreen list machinery (ZoneLane, extents, RowAt, Tail, overlay InfoBar).
Tests: delete FormsTests, ZoneExtentTests, ZoneEnterTests, WeekSummaryTests, CardNavTests; ZonePlannerTests cap 7→8.
`SectionScreen`/`Customize` swap HomeTok literals for `Spacing`/`Design.Type`. Skill doc `home-layout.md` + plan appendix.

## Order
0. E25 + S1 + S2; engine build D+R + VerticalSlice.
1. Parallel (one file each): HomeScreen.cs · Zones.UI.cs · Daylist.UI.cs · Facet.UI.cs · deletions/rules/tests · docs.
2. Build D+R, Wavee.Tests D+R, grep gates (no HomeTok/HomeText/Cards./Forms.Of/ItemsView in Home/).

## Verification (live account, fake too)
Window 1717×1150 @150 % with right panel, dark + light, and 1440×900: greeting + SelectorBar; daylist card with no
truncation; MediaRow grid 3-up; stock shelves 4-up with pips/chevrons like Browse; tiles as SettingsCards; facet
switch dims + stock bar; failure InfoBar pushes content; while scrolling the facet row and the current chapter header
stay pinned with NO background — content dissolves under them via StickyClip + feather, and the next header pushes the
previous out; hover lights only the item under the pointer; cold-load
skeleton derived from the real tree; resize 640→1600 without flicker.

---

# Sixth pass — prototype visuals on the shared controls (2026-09-27)

Foundation from the fifth pass stays. Every visual is a small, general extension of a shared control; no
Home-private template or hover recipe. All APIs below were verified against source by the planning pass.

## Engine (C:\wavee\fluent-gpu), default byte-identical, one gate each
- **E26** `Interaction.Tile` (src/FluentGpu.Controls/Interaction.cs, after `Control`):
  ```csharp
  public static InteractionRecipe Tile => new()
  {
      Fill = new StateBrush(Tok.FillCardDefault, Tok.FillControlSecondary, Tok.FillControlTertiary, Tok.FillCardDefault),
      Stroke = StateBrush.Flat(Tok.StrokeCardDefault), StrokeWidth = 1f,
  };
  ```
  Gate `gate.interaction.tile-ramp` (fills/stroke as above, no WhileHover).
- **E27** `SettingsCard.Create` gets a `SkeletonProxy` = the real card built at the measured width via the
  `ResponsiveBox` + `DeriveRenderedOutput` idiom PagedShelf uses. Gate `gate.settingscard.skeleton-proxy` (derives an
  icon bar + ≥ 2 text bars, not one 160 bar). Guide note.

## App shared
`Platform/Controls.Art.cs`:
1. `CardData` + `public float CoverAspect { get; init; } = 1f;` and `public string? Meta { get; init; }` (both in Equals/GetHashCode).
2. `ShelfHeight(float cardW) => ShelfHeight(cardW, 1f, 0);` + `ShelfHeight(cardW, coverAspect, extraLines)` = `CoverHeight(inner, aspect) + 88 + 16·extraLines` (inner = cardW − 2·Spacing.S); `CoverHeight(inner, aspect) => aspect != 1 ? Round(inner/aspect) : inner`; `const int WideDecodePx = 512`. Identity: `ShelfHeight(w) == w + 72` must hold.
3. `CardCover(art, width, height, circular, overlay, corner)` (was one edge) — update callers.
4. `ShelfCardHost`: cover `inner × CoverHeight(inner, d.CoverAspect)`, decode `WideDecodePx` when non-square. `GridCardHost`: `AspectRatio = d.CoverAspect`.
5. `Labels`: tertiary one-line `d.Meta` line when set.
6. `MediaRow(..., RowSkin skin = RowSkin.ListRow)` replacing `bool plated`; `enum RowSkin { Plain, ListRow, Tile, Outline }`. Plain → row; ListRow → `Interaction.ListRow`; Outline → `Interaction.Subtle` + dashed `StrokeControlSecondary` (BorderDashOn/Off 3/3); Tile → `Interaction.Tile with { Stroke = null }` + `BorderWidth 1`, `BorderColor = Prop.Of(() => RelatesNow(uri) ? AccentDefault@.5 : StrokeCardDefault)`. Update callers (`Entities/Album.Page.cs:1117` `plated:false` → `RowSkin.Plain`).
7. `public static bool RelatesNow(string uri)` — `NowPlaying` seam, coarse `HasActiveContext` gate then `RelatesTo(uri)`.
8. S3: `NowPlayingOverlayHost.BuildPlayFab` returns `new BoxEl()` when `OnPlay is null`.
`Platform/Controls.cs`: `Cover(url, aspect, corner, decodePx, float focusY = 0.5f)` (sets `ImageEl.FocusY`); `ArtworkFill(..., float aspect = 1f)`; `IconPlate(glyph, size, fill, ink, glyphSize = 20)`; `TileCardStyle` (SettingsCard.DefaultStyle with HeaderIconSize 48, Padding (10,8,12,8), icon margin 12, MinHeight 68) and `TileCardParts` (header + description one-line ellipsis).
`Platform/Design.cs` Size: `WideTileMin = 330f, WideTileMax = 440f, WideTileAspect = 16f/9f`.
`Entities/Browse.Cards.cs`: `ShelfItem(..., string? WideArt = null, int CaptionLines = 1, string? Meta = null)`; `ShelfItemOf(in c, second, circular, wideArt = null, captionLines = 1, meta = null)`; `ShelfCell(..., float coverAspect = 1f)` — second line MaxLines = CaptionLines; art = coverAspect != 1 ? WideArt ?? Art : Art; `CardData { CoverAspect, Meta }`.

## Home
- `Time.cs`: restore `RecentsWeek` + `WeekSummary` verbatim from commit a4842b9f (`git show a4842b9f:src/apps/Wavee/Home/Time.cs`), and `Wavee.Tests/HomeUi/WeekSummaryTests.cs` from the same commit.
- `RecentsCells.cs`: `RecentsPlan.Cells(int cols, int available) = clamp(2·max(1,cols) − 1, 0, available)`; `RecentsCells.NowCaption(string? device, localize)` → `Strings.Home.PlayingOn(d)` or `Strings.Home.When.PlayingNow`.
- `Zones.Rules.cs` ShelfLead: `SquareWidth(leadW, gap, span) = (leadW − gap·(span−1))/span`; `LeadAspect(leadW, gap, span, cardPad) = (leadW − 2·pad)/(SquareWidth − 2·pad)` (lead cover exactly as tall as the squares).
- `Items.Rules.cs`: `CardMeta.Of(owner, trackCount)` → "Spotify · 200 songs" / "200 songs" / "Spotify" / null; `DaylistArt.Of(header, image)` → (header, 540/296, 540) | (image, 1, 296) | (null, 1, 296).
- `ZonePlanner.cs`: `SectionRoles.IsRadioFormat` public.
- `Zones.UI.cs`: recents header tool `HyperlinkButton(Loc.Get(Strings.Home.ListeningHistory), GoRecents)`; shelf headers tools = See all link · 16-px vertical divider · pips ‹ › (`ShelfTools(controller, leadingDivider)`); RecentGrid body = `Responsive.Of(w => Grid(...), fallback: HomeModuleLayout.FallbackWidth)`, cols = `GridEl.AutoFillColumnCount(w, 220, Spacing.M, 4)`, `RecentsPlan.Cells(cols, cells.Count)` rows of `MediaRow(..., skin: RowSkin.Tile)` whose Subtitle is [equalizer box (`Visible = RelatesNow`, `.Skeletonized(false)`), detail (secondary), tertiary text bound to `RelatesNow ? " · " + NowCaption(RemoteDeviceName()) : " · " + when`] + history tile (`MediaRow`, `RowSkin.Outline`, `CoverOverride = IconPlate(Icons.Headphones, 48, AccentSubtle, AccentTextPrimary)`, subtitle = `SparkBars` 7-day strip (today accent) + `Strings.Home.PlaysThisWeek(n)`, trailing chevron, `OnPlay null`), WeekSummary computed per Recents version in the cache. `RemoteDeviceName()` = `DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, Playback.Devices.Rows)` → `rows[slot].Name` (idiom Shell/Shell.PlayerBar.cs:382-389). Shelf: WideTiles/VideoTiles → `ShelfItemOf(..., wideArt: HeaderImageUrl ?? ImageUrl, meta: CardMeta.Of(owner, trackCount))`, cardHeight `w => ShelfHeight(w, WideTileAspect, 1)`, min/max WideTileMin/Max, cell coverAspect 16/9; cover shelves with a header-image lead → `leadSpan: 2, leadCardAt: (item,i,leadW) => Cell(item, leadW, ShelfLead.LeadAspect(leadW, Spacing.M, 2, Spacing.S)), leadMinColumns: 4`, lead item captionLines 2 + meta "{n} songs"; circular = Artist || IsRadioFormat. Browse tiles: `Style = Controls.TileCardStyle, Parts = Controls.TileCardParts`, art `Controls.Artwork(url, 48, 48, Radii.Control)`; charts `HeaderIconElement = Controls.IconPlate(glyph, 48, FillControlDefault, TextSecondary)`.
- `Daylist.UI.cs`: text column left (`Grow 1, Basis 0, MinWidth 0`), art RIGHT: `(url, aspect, maxW) = DaylistArt.Of(card.HeaderImageUrl, card.ImageUrl)`; `new BoxEl { Grow 1.2, Shrink 1, Basis 0, MinWidth 220, MaxWidth maxW, AlignSelf Center, Children = [Controls.Cover(url, aspect, Radii.Control, 512, focusY: 0.4f)] }`; card padding (32,12,12,12).
- Tests (pure): RecentsPlan.Cells; ShelfLead SquareWidth/LeadAspect (342,12,2 → 165; 326/149); WeekSummary restored; NowCaption; CardMeta; DaylistArt; ShelfHeight identities (w+72; lead height == square height for w ∈ {148,165,188}; ShelfHeight(428,16/9,1) == 232+88+16).

---

# Seventh pass — the prototype's design on the shared controls (2026-09-29)

## Context
Owner: "everything is so ugly." Screenshots at his ~760-DIP Home: the cold load is a greeting plus a few thin bars;
the daylist art eats two thirds of the card; the Radio lead is a pill; section subtitles end in a bold "…"; hover
lands late. The prototype is canvas-v3 (`docs/plans/wavee/home-redesign/canvas-v3/Main.dc.html`, `States.dc.html`).
The fifth/sixth-pass foundation (stock + shared controls, data pipeline) stays; this pass fixes the root causes and
applies the prototype's type, proportions and hover recipe.

**Owner decisions:**
- The facet words are the page title. No greeting headline; the greeting lives only in the daylist eyebrow.
- The prototype's fill-only hover goes on the **shared** shelf card, app-wide.

**Standing rules:** sticky headers stay background-free (overrides the prototype's `--chap-bg` band). The prototype
canvases have **no responsive rules** (a fixed 1440 board), so no width math is copied from them; every narrow
behaviour is content-driven (daylist: text `MinWidth 340`, art `Basis 540` shrinks first, art hidden below 160 via
`DaylistForm.ShowArt`).

## Root causes
| Symptom | Cause |
|---|---|
| Cold load = greeting + thin bars | fluent-gpu `Hooks/SkeletonDeriver.cs:158-171` does not render components: a `ComponentEl` without `SkeletonProxy` becomes ONE 160×10 bar. `ShelfChapter` (`Zones.UI.cs:128`) and `RecentGrid` (`:214`) are bare `Embed.Comp`s, so PagedShelf's card proxy (`PagedShelf.cs:272-284`) is never reached. The seed (`HomeScreen.cs:445`) has no Daylist zone. |
| Daylist art ≈ 2/3, title wraps, buttons stack | Flex clamps base size to MinWidth before splitting free space (`FlexLayout.cs:812-835`). Art `MinWidth 220` + `Grow 1.2` vs text `Grow 1, MinWidth 0` → at 652 inner, art ≈ 445, text ≈ 187; Play/Shuffle wrap (`Wrap = true`) and the fixed 200-px bar overflows. |
| Radio lead is a pill | `Zones.UI.cs:246-251` passes `circular` to the 2-span lead; `ShelfCardHost` rounds with `inner/2` on a wide cover (`Controls.Art.cs:477`). |
| Bold "…" on subtitles | `ModuleHeader` is one baseline-paired `SpanTextEl` (`Design.cs:1240`); `GlyphRenderer.cs:1440` draws "…" at the paragraph BASE size/weight, not the truncated span's. |
| Hover late on Home | (1) The facet dim host's `OnPointerDown` (`HomeScreen.cs:435`) makes the whole zone column an interactive hover scope: every card→gap→card move flips it, re-walks `SetHoverDescendants` over the column and dirties span reuse for every wrapper (Browse has no such ancestor). (2) The shared card lift is a 250 ms tween + 1.04/250 ms cover zoom (`Controls.Art.cs:89-96, 145`); the prototype is fill-only at 83 ms. |

## Page
```
HomeScreen  ZStack[ShellTint, ScrollView]
└─ column  MaxWidth PageMaxW, Padding (PageWide, 24, PageWide, Dock.Reserve+32), Gap 40
   ├─ FacetRow .Sticky(0) — SelectorBar(Design.FacetTitleStyle): All · Music · Podcasts · Audiobooks  [Following]
   │                        + 3-px busy bar
   └─ content .StickyClip(FacetRowH) + WhileStuck feather
      └─ SkelRegionEl(seed = [Daylist, RecentGrid, CoverShelf+lead])
         └─ facet column (dim host: Opacity/HitTest binds only — NO pointer handlers)
            ├─ Daylist card
            ├─ Recently played (header + Tile grid + dashed history tile)
            └─ chapters: ModuleHeader(title · subtitle, no chevron)  [See all | ‹ pips ›] + body
```

Daylist at the owner's width (card ≈ 696, inner 650):
```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Good evening, Chris · your daylist               ┌───────────────────────┐  │
│  cutesy korean r&b                                │                       │  │
│  sunday afternoon            (40/52, ≤2 lines)    │     art, cover-crop   │  │
│  korean r&b · cutesy · coffee house …             │     278 wide here,    │  │
│  Playlist · Spotify · 50 songs                    │     540 max, full     │  │
│  [▶ Play] [⤮ Shuffle] ♡ …        (one row)        │     card height       │  │
│                                                   │                       │  │
│  ◔ Next daylist in 01:09:15 · monday night …      │                       │  │
│  ▬▬▬ ▬▬▬ ▬▬░ ─── ───                              │                       │  │
│  Early  Morning  Afternoon  Evening  Night        └───────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────┘
text: Grow 1, Basis 0, MinWidth 340, SpaceBetween · art: Basis 540, Shrink 1, MinWidth 0, stretch
```

## Engine (C:\wavee\fluent-gpu), default byte-identical, one VerticalSlice gate each
- **E28** `SelectorBarStyle? style` on `SelectorBar.Create` (`src/FluentGpu.Controls/SelectorBar.cs`): `LabelSize`,
  `LineHeight`, `FontFamily`, `CharSpacing`, `RestColor`, `SelectedColor`, `SelectedWeight`, `ShowPill`,
  `ItemPadding`, `HoverFill`/`PressedFill`, `ItemGap`. `null` = today's look. Gates
  `gate.selectorbar.style-default-identical`, `gate.selectorbar.style-title` (28-px label, selected 600, no pill, hover plate).
- **E29** ellipsis in the truncated span's style: `GlyphRenderer.cs:1440` uses the size/weight/colour of the span at
  the cut, not the paragraph base. Gate `gate.text.span-ellipsis-style`.

## App shared
- `Controls.Art.cs` hover recipe (app-wide): `CardPhysics` drops `WhileHover` lift, `WhilePressed`,
  `HoverElevatePaint` (keeps `Cursor`); `CardShell` plate `Tok.FillSubtleSecondary`, pressed subtle-press, no stroke,
  no shadow, 83 ms; `CardCover` no `HoverScale` on square covers, 1.02 on the 333 ms decelerate rung for non-square;
  play FAB stays 167 ms.
- `Controls.Art.cs`: a non-square cover is never circular — pure `CoverShape.IsCircular(circular, aspect)`, honoured
  by `ShelfCardHost`/`CardCover`.
- `Controls.cs`: `CoverFill(url, corner, decodePx, focusY)` — `ImageFit.Cover` filling its box, no aspect box.
- `Design.cs`: `Design.Type.HeroTitle` (TitleLarge 40/52, display face, 600); `Design.FacetTitleStyle` (E28: 28/36
  display, rest `TextSecondary`, selected `TextPrimary` 600, no pill, subtle hover plate, padding 0/8, gap 24,
  first item −8 lead).

## Home
- `HomeScreen.cs`: greeting headline + its `UseComputed` deleted; top padding 24. Dim host loses `OnPointerDown`
  (F35's "interacted" mark moves to the `ScrollView` if that is already the page's interactive ancestor, else
  `FacetSwitchState.Interacted`/`FacetSwitch.Interact` are deleted and `Landed` swaps on at-top + window only;
  `Facets.cs` + tests follow). `Seed` for All = `[Daylist, RecentGrid, CoverShelf]`, the shelf's blank lead
  carrying a header so the 2-span lead skeletons.
- `Facet.UI.cs`: `SelectorBar.Create(..., style: Design.FacetTitleStyle)`; `SelectorH` 40 → `FacetRowH` = 43.
- `Daylist.UI.cs`: card `MinHeight 320`, padding (32,12,12,12), gap 32, `AlignItems Stretch`. Text column
  `SpaceBetween`, padding (0,20,0,12): eyebrow `Ui.Body` secondary; `HeroTitle` ≤ 2 lines; tags 14/20 (links
  secondary, separators tertiary); meta 12/16 tertiary; actions ONE row (`Wrap = false`), margin-top 20.
  `DaylistClock`: stock `ProgressRing.Create(elapsed, 16)` + "Next daylist in **hh:mm:ss**" + secondary
  "· {next} arrives at HH:mm"; five stock determinate `ProgressBar`s (done 1 / current elapsed / future 0), equal
  grow, gap 4; label row 12/16 tertiary ellipsised, current label primary 600 never truncated (segments from
  `DaypartRules`). Art `Basis 540, Shrink 1, Grow 0, MinWidth 0`, stretch, `CoverFill(..., focusY 0.4)`.
  `DaylistForm.ShowArt(inner)` = `inner − 32 − 340 ≥ 160`, via `Responsive.Of`; otherwise text column alone.
  `DaylistCard` gets a `SkeletonProxy` (same tree, blank text).
- `Zones.UI.cs`: 2-span leads `circular: false`; headers `open: null` (no chevron; See all / Listening history
  carry the drill); tools See all · divider · ‹ pips ›; `ShelfChapter` and `RecentGrid` get `SkeletonProxy` = their
  own static tree builders (`Render` calls the same builders, so the skeleton stays derived from the real tree).

## Tests (pure)
`CoverShape.IsCircular`; `DaylistForm.ShowArt` (650 → true, art 278; 480 → false); `FacetSwitch.Landed` without
`Interacted` (or the Interact cases removed); Daypart segment fill mapping (done / current / future).

## Verification
Engine D+R + VerticalSlice (4 pre-existing `gate.shelf.*` failures noted); `Wavee.slnx` D+R
(`-m:1 -nodeReuse:false -p:UseSharedCompilation=false`); `Wavee.Tests` D+R (`AlbumReleaseFactsRulesTests` failure
pre-existing). On screen: side-folder build, `wavee://open?route=home`, `Drive-WaveeWindow.ps1 -Move "0,0,1717,1150"`
@150 % with the right panel, then 1440×900; dark + light; live + `--fake`.
1. Facet words read as the page title; no greeting headline.
2. Daylist: title ≤ 2 lines, actions one row, ring + timeline visible, art shrinks before the text, art gone when narrow.
3. Cold load shows card/tile/shelf skeletons, not thin bars.
4. Radio lead is a rectangle, round radios beside it.
5. Subtitles ellipsise in their own 12-px tertiary style.
6. Pager order ‹ pips ›.
7. Hover immediate and fill-only on Home, Browse category, Artist and Search shelves (owner confirms the feel;
   synthetic hover isn't capturable).
8. Walk the rest (Made for you lead, NMF 16:9, release list, Because-you-like, Jump back in, Browse tiles, Podcasts)
   and list remaining deltas before fixing. Then `ops/build/publish-wavee-aot.ps1 -Arch arm64`.

## On-screen follow-ups (2026-09-29, live account)
- **Measured widths, not screenshot maths.** Layout units ≠ screenshot px ÷ 1.5 on the owner's box: the facet row
  measures **632** at 1717×1150 @150 % with the right panel and **464** at 1440×900 (a temporary `facet.form` log,
  since removed). Recents therefore lays out 2 columns at his size (632 < 3·220 + 2·12); unchanged, owner's call.
- **Facet row form** (`FacetForm.IsTitle(w) = w ≥ 600`, `Facet.UI.cs` via `Responsive.Of` cross-stretched by the band
  column — a row-grown Responsive box measured wrong): 28-px title words at ≥ 600, `Design.FacetCompactStyle` (20/28,
  items 8 apart) below, same 40-tall item so every sticky inset is unchanged. Test `FacetFormTests`.
- **Dead lift clearance removed.** With the shared card fill-only, every shelf of shared cards still reserved
  `ShelfLift.Elevate`'s 12 + 12 DIP; `lift: ShelfLift.None` on Home, Browse category, Search playlists rail, Artist
  shelves and the podcast Related shelf.
- **Daylist:** a `Gap = Spacing.XL` floor between the action row and the clock (SpaceBetween had no slack once a
  two-line title filled the card); wide-cover hover zoom on `Design.Motion.Slow` (new 333 ms rung).
- **Still open (for the owner):** ≈ 85 layout units between a shelf's captions and the next header (prototype ≈ 52);
  the daylist art is a tall crop at his width because the two-line 40/52 title makes the card ≈ 370 tall.

## Traced issues (2026-09-29) — root causes; ALL FIXED 2026-09-29 (status below the table)
| # | Issue | Root cause (evidence) | Fix direction |
|---|---|---|---|
| T1 | "Home goes blank at the page end" | **Capture-only, not on screen.** PrintWindow(PW_RENDERFULLCONTENT) shows the page blank once the engine idles after a scroll; `CopyFromScreen` of the same moment shows it fully rendered. The engine draws everything into ONE composition swapchain and presents nothing at idle (`AppHost.cs:1276-1287`, byte-identical skip `:1343-1371`), so PrintWindow gets a stale DWM buffer. | Verification tooling: use a screen copy (or an alpha-flattened `CaptureBgra`), never PrintWindow, for idle frames. |
| T2 | Shelf → next header ≈ 85 vs prototype 52 | Every card reserves a 2nd subtitle line it never uses (`ShelfHeight` 88 = …+32 subtitle, `Controls.Art.cs:410-423`; `CaptionLines` default 1) = 16 dead; lead shelves also reserve a meta line on every square (`Zones.UI.cs` `ShelfHeight(w,1,1)`) = 16 dead; plate bottom pad 12 + gutter 2; header title sits 2 into its 32 box vs 12 in the prototype's 52 `.chap`. Side bug: the lead's 2-line caption + meta needs 72 but the budget gives 70 (Labels `Gap = 2` not counted). | Reserve caption lines from the shelf's actual max `CaptionLines`; reserve the meta line only for the lead cell; count the Labels gap. |
| T3 | Daylist art is a tall portrait crop | Card height = max(320, text column); at 632 units the 40/52 title wraps to 2 lines → ≈ 370 tall; the art column stretches to the card height at ≈ 216-238 wide. | Design call (smaller title rung below a width, or cap the art aspect). |
| T4 | Recents shows 2 columns at the owner's size | The row measures 632 layout units < 3·220 + 2·12 = 684 (`GridEl.AutoFillColumnCount`). | Design call (min column 200, or 3 rows at 2 columns). |
| T5 | Layout units ≈ 1.1 × (px ÷ 1.5) | The owner's app zoom is **Manual 110 %** (`HKCU\Software\Wavee\Wavee\Settings`: `appearance.zoom = 1.1`, `appearance.zoom.mode = Manual`), from a Ctrl+= / Ctrl+wheel step; engine scale = dpi × zoom (`Win32Platform.cs:607-613`). | Owner: Ctrl+0 / Zoom → Auto if unintended. The facet-row threshold (600) is in layout units and measured at this zoom. |
| T6 | Playback resumed ~4.4 s after a probe launch | Not auto-resume: session restore is paused and never claims (`Playback.Transitions.cs:1107-1128`). The log shows `connect.owner Nobody → Us (claimed) claim=Protected` on the UI thread = a local `Input.Resume` (Space / a play click / taskbar thumbnail / tray / jump-list resume). None of those paths logs its cause (SMTC does). | Add an always-on attribution line to the Space / click / thumbnail / deep-link resume paths. |
| T7 | Facet-row `Responsive` "measured too small" | Not a bug: it measured 632 correctly; the first threshold (660) was derived from screenshot pixels without the 1.1 zoom (T5). | — (threshold now 600, test pinned). |
| T8 | 4 VerticalSlice `gate.shelf.*` failures | `controller.goto` and `lift.elevate-unchanged`: stale gates — E9 gave every slot root `Role = Button` (`PagedShelf.cs:604`), so `Roles(Button)` now also returns cards (10, not 2) and `buttons[0]` is the slot root, not the card (Y 0, padding inside). `keyboard.page-follow`: arrow keys use a minimal `BringIntoView` (98 px) and the page rounds to 0 — the gate expects paging ItemsView never did. `keyboard.alloc` 4776 B: every arrow re-renders the whole ItemsView because `Render` subscribes to `current` (`ItemsView.cs:859`). | Fix gates 1 & 4 (pick the header's buttons; measure the card child). Page-follow: page the shelf on focus (`GoToPage`) or relax the gate. Alloc: `Peek` `current` in bound mode and move the two `cur` layout effects into tracked effects. |
| T9 | `AlbumReleaseFactsRulesTests.Length_IsSpelledOnce…` | Order-dependent test bug: expected uses `MetaLineYear(2, …)` (raw int) while the code passes `SongCount(2)`; both render `"[key]"` until another test loads the locale catalog (`Localization.cs:237`). Not the Album edits. | Test: `MetaLineYear(Strings.Detail.SongCount(2), …)` (the `DetailTextTests.cs:133` shape). |
| T10 | Artist pick photo stays a grey block | Decode is right (`pinnedItem.backgroundImageV2`, `Spotify.Decode.Artist.cs:297`; fallback header `Artist.UI.cs:695`). The band is reserved whenever a URL exists and shows only the flat placeholder with no failure/timeout path (`Artist.UI.cs:756-776`). The owner's session left ~9 images neither Ready nor Pending after that navigation (`images=133 imagesReady=124 imagesPending=0`, `canceled=8`) with no decode failure logged → a None/Canceled leftover on a mounted non-virtual node, which nothing restarts (`ImageCache.cs:929-956`); probable trigger: the top band's first build at the 900 fallback then a rail/band layout flip unmounting the card mid-decode (`Artist.Page.cs:734-753`). Not reproduced in 4 fresh launches. | Engine: sweep pinned `None/Canceled` leftovers in `Pump` (like `RetryPinnedExhausted`). App: reveal the photo band only when the image is Ready, collapse on failure. Diagnostics: put the URL in the `image decode failed` line. |
| T11 | Artist hero doesn't stretch on overscroll (dark band above) | The stretch exists and computes right (`.StretchFromTop()` on the photo, `Artist.UI.cs:410`; `ScrollEffectEval.cs:70-71,107-111`), but the hero root's `ClipToBounds` (needed for `Collapse(Leading)`, `Artist.UI.cs:470`) clips at its un-stretched top edge (`SceneRecorder.cs:2072-2073`). The engine bench/gate have no clipping ancestor, so they miss it. | Engine: let `Collapse(Leading)` pose its cut as a clip-rect channel open at the top (like `StickyClip`'s `ClipTop`), so the root needs no `ClipToBounds`; add a gate with the app's real structure. |

### Fix status (2026-09-29, all verified: engine D+R + VerticalSlice ALL CHECKS PASSED 1695, canon OK, Wavee.slnx D+R, Wavee.Tests D+R 0 failures)
- T1: `ops/release/tools/Drive-WaveeWindow.ps1 -Out` screen-copies (`-PrintWindow` keeps the old path).
- T2: `Controls.ShelfHeight(w, aspect, captionLines, metaLine)` is exact (default w+66, 1-line w+50); plate bottom 12→8, gutter 2→0; the lead's meta rides inline in its caption; Browse/Search shelves reserve 1 caption line.
- T3: `DaylistForm.TextWidth/UseHeroTitle` — the title steps to 28/36 below a 480 text column; the card stays ≈ 320.
- T4: `Zones.RecentsMinCol` 200 (632 → 3 columns).
- T5/T7: no code (owner zoom; not a bug).
- T6: every local play/pause logs `[playback] play/pause cause=<source> action=…` (required `cause` on `Playback.Pause/Resume/TogglePlay`).
- T8: shelf gates fixed; ItemsView bound mode no longer re-renders on a `current` move (0 B); PagedShelf pages on keyboard focus (`FollowFocusToPage`).
- T9: the Album test builds its expectation with `SongCount(2)`.
- T10: engine `ImageCache` restarts pinned Canceled leftovers (`RestartPinnedLeftovers`, 500 ms) and an idle host wakes for them (`WakeReasons.ImageLeftoverDue`); the pick card reveals its photo only when Ready (`PickPhoto`); `image decode failed` lines carry `src=`.
- T11: `Collapse(Leading)` cuts its own children via the `ClipBottom`/`CollapseCut` channel; the Artist hero root dropped `ClipToBounds`, so `StretchFromTop` fills a top overpan (gate `gate.scroll.stretch-under-collapse`; touchpad-only in the app — a wheel hard-clamps).
