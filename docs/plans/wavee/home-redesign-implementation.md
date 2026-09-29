# Wavee Home redesign + app-wide button standardisation — technical plan

## Context

The owner approved a Home redesign mockup (canvas https://claude.ai/artifact/Qxb9bZUyCjqVsBv21gy8m8, source in
`C:\wavee\waveemusic\home-canvas-pub\project\*.dc.html`; `Components.dc.html` is the pixel reference, `FacetSwitch.dc.html`
the switch motion, design docs in the session scratchpad `06-facet-design.md` / `05-native-research.md`). It replaces the
current Home (hero band + module grids + underline facet strip) with a native-WinUI page: a facet **page-title pivot**
(All · Music · Podcasts · Audiobooks + Following), a daylist **card** with live countdown, Recently played grid + history
entry, paged GridView shelves with a two-cell **wide lead**, grouped "Because you like…" cards, Browse tiles, dedicated
Music/Podcasts/Audiobooks pages, and a facet switch that **keeps the old page, dims it and pins an indeterminate
ProgressBar** (shimmer only on first load). Separately the owner wants the app's **pill buttons removed everywhere** and
standardised on stock WinUI buttons (32px, 4px radius), with Play tinted by the **page palette accent** and a custom,
"beautiful yet WinUI" **Follow ToggleButton**. Both workstreams run **in parallel**. "From artists you follow" uses
**server sections only** (no new fetch).

Exploration found most plumbing exists: `header_image_url_desktop`, trackCount, episode duration/position/video flags,
homeChips/subChips and per-facet rows (`Entities.HomeFeed(facet)` → `wavee:home:<facet>`) are decoded; the daylist
countdown/rollover (`Entities/Daylist.cs`), Recents edge + page (`RouteKind.Recents`), `PagedShelf`, `Segmented`,
`ProgressBar.Indeterminate`, `InfoBar`, sticky scroll effects and `Skel.Region` all exist in the engine or the data
layer. **Owner decision:** the Home presentation layer is rebuilt from the ground up. It gets new files and types, and
the old Home UI and rules code is deleted rather than adapted. Only the data pipeline is kept, and the engine is
extended where needed.

First implementation step: copy this plan into `docs/plans/wavee/home-redesign-implementation.md` (repo convention:
real code, trees, wireframes, waves) and open GitHub issues for the three workstreams via the `github-triage` skill
(owner approves each `gh` call) so CHANGELOG bullets / commits can carry `(#n)` / `Fixes #n`.

---

## Workstream E — engine (`C:\wavee\fluent-gpu`, lands first, verified there)

Rule: `docs/guide/control-fidelity.md` §6 — extend via Parts / content slots / Style records / options, no knobs.

| # | Change | Files | API |
|---|---|---|---|
| E1 | **AccentSet from any colour** (palette Play) | `Engine/Dsl/Tokens.cs`, `Controls/Button.cs` | `readonly record struct AccentSet(Fill, FillSecondary, FillTertiary, Subtle, Text, Ink, InkSecondary){ static AccentSet From(ColorF base); }` sharing one private shade fn with `Tok.Accent*`; `Button.ButtonPalette.ForAccent(ColorF base)` (fill 1.0/.90/.80, luminance-picked ink, `Tok.AccentControlElevationBorder`) |
| E2 | **ToggleButton glyph + controlled + checked label + pop** (Follow, Home "Following", filter chips) | `Controls/ToggleButton.cs`, `Engine/Hooks/MotionRecipes.cs` | `Create(..., string? glyph=null, string? checkedGlyph=null, string? checkedLabel=null)`; `static BoxEl Controlled(string label, bool isChecked, Action<bool> onToggle, string? glyph=null, string? checkedGlyph=null, Style? style=null, bool isEnabled=true, TemplateParts? parts=null, ControlSize size=Medium)`; `PartIcon`/`PartGlyph`; Style adds `OffGlyphForeground, OnGlyphForeground, GlyphSize=16, GlyphGap=8, ContentReflow (LayoutTransition), LabelSwap, CheckedPopScale (0=off), CheckedPopMs=250`; `AnimEngine.Pulse(node, peak, ms)` (no-op under reduced motion). Pop fires only on a **user-initiated** false→true (click sets `UseRef userFlip`, `UseLayoutEffect` consumes) — never on data load. Label trimmed `MaxLines=1`. No-glyph tree byte-identical. |
| E3 | **Segmented presets** (facet pivot + compact band) | `Controls/Segmented.cs` | `PartLabel`, `PartSelectionPillSlot` (caller hides the 3px pill row), `SegmentedOptions.WrapFocus`, `SegmentedOptions.ItemRole` (Tab for the facet tablist). Keeps focus-not-selection keyboard model. |
| E4 | **PagedShelf external controller** (pager lives in the app's sticky chapter header) | `Controls/PagedShelf.cs` | `sealed class ShelfController { IReadSignal<int> Page, PageCount; IReadSignal<bool> CanPrev, CanNext; Signal<int> PageState; GoTo/Prev/Next }`; `PagedShelf.Create(..., ShelfController? controller=null)`; `ShelfPagerContext.PageSignal`. Document the headerless 12px LiftClearance (app offsets via `PartRoot` Margin.Top=-12). |
| E5 | **ProgressBar stretch** | `Controls/ProgressBar.cs` | `Indeterminate(float width = 240, ...)`; `width: float.NaN` ⇒ stretch via `UseMeasuredWidth(quantum 4)`, re-arms sweep on resize; visible 1px track via `PartTrack` app-side. |
| E6 | **UseScrollThreshold** (compact band 64/56 hysteresis) | `Engine/Hooks/ScrollHooks.cs`, `RenderContext.cs`, `Scroll/Runtime/ScrollObservation.cs` | `protected IReadSignal<bool> UseScrollThreshold(double enterAt, double exitAt, ScrollHandle? handle=null)`; pure `ScrollThreshold.Next(prev, offset, enter, exit)`; writes only on flip, zero alloc. |
| E7 | **PagedShelf lead span** (wide lead item) | `Engine/Scene/VirtualLayout.cs` (`FillRowVirtualLayout`), `Controls/PagedShelf.cs` | `FillRowVirtualLayout.SetLeadSpan(int)`/`LeadSpan`, `EffectiveLeadSpan = Rows==1 ? min(span, perPage) : 1`; `PagedShelf.Create(..., int leadSpan = 1)` as a **live** prop (depends on data); `CellsOf(n)=n+s-1`, `FirstItemOfPage(p)= p==0?0:p·cols-(s-1)`; page stride unchanged (1332) so pages land on card edges; span-aware `ItemRect` ⇒ XY keyboard nav free. |
| E8 | Dashed 1px stroke (history item) + icons | `Dsl/Element.cs` (`BoxEl.BorderDash`) or fallback solid 50%; `Icons.PersonCheck/History/Video/HeartFill` if missing | minor |

Order: E1→E2 (both teams) · E3,E4,E5,E6 (Home) · E7 after E4 (same file) · E8 anytime.
No engine work: dim-and-block (app: `Opacity` bind + `Transition = MotionTokenDef.Eased(167f,…)` + `HitTestVisible=!busy`
on a wrapper around a stable child), two-tone focus ring (automatic; add gate `gate.focus.ring.shelf-cell`).
Known gap logged, not built: UIA names/busy/pressed (UIA is placeholder).

Engine tests/gates: `Engine.Tests/AccentSetTests.cs`, `ScrollThresholdTests.cs`, `VirtualLayoutLeadSpanTests.cs`;
VerticalSlice gates `gate.button.palette.for-accent`, `gate.toggle.{glyph.slot,checked-label.swap,pop.user-only,reflow}`,
`gate.segmented.{wrap,role.tab,pillslot.part,select-not-follow-focus}`, `gate.shelf.controller.{goto,settle-sync,pagecount-refit,stable-actions}`,
`gate.shelf.lead.{pages,keyboard,live,alloc}`, `gate.progress.stretch`, `gate.hooks.scroll-threshold`. Gallery samples in
`BasicInputPages.cs` (Play palette, Follow toggle), Segmented page (pivot + band on one signal), `StatusLayoutPages.cs`,
`ScrollDemo.cs`, `CollectionsMenusPages.cs` (lead shelf + external sticky header). Docs: virtualization.md,
component-props-contract.md, scroll.md, motion-recipes.md, then `docs\design\check-canon.ps1`.

---

## Workstream B — button standardisation (app, parallel with Home)

### Grammar (component sheet row 1)
| Role | Control | Geometry |
|---|---|---|
| Primary (one per surface: Play/Resume) | `Controls.PrimaryButton/PlayButton` → `Button.Create(Accent, glyph, palette: Button.ButtonPalette.ForAccent(pageAccent))` | 32, r4, 14 semibold (PartLabel), min-w 120 (PartRoot) |
| Secondary (Shuffle, Open page, Load more) | `Button.Create(label, cb, Standard, glyph:)` | 32, r4 |
| Icon-only (like, more, radio, row play) | `Controls.IconAction` (stock `IconButton` Subtle 32) + `Controls.Named` tooltip; drop HoverScale/PressScale/hand cursor | 32×32 r4 |
| Link (See all, social, +N genres) | `HyperlinkButton.Create` | — |
| Follow / Pre-save | **`Controls.FollowToggle`** (new, below) | 32 r4 |
| Filter / mode toggles | `ToggleButton.Controlled` (stock checked = accent) | 32 / 24 small |
| Media exceptions (stay round) | `Controls.PlayFab`, `Controls.CoverActionFab`, video poster play | circle |

No hover lift anywhere; no hand cursor on buttons.

### FollowToggle (the "beautiful yet WinUI" toggle) — `Platform/Controls.cs` replacing `FollowButton` (:809)
A = page accent (`Design.AccentCtx.Fill`, else `Tok.AccentDefault`).
| State | Fill | 1px stroke | Glyph | Label |
|---|---|---|---|---|
| Off rest/hover/pressed | stock Standard FillControlDefault/Secondary/Tertiary | ControlElevationBorder | `Icons.Heart` TextPrimary | "Follow" |
| On rest | A @ .14 dark / .10 light | A @ .50 / .60 | `Icons.HeartFill` in HeartInk | "Following" |
| On hover | A @ .20 / .15 | same | same | same |
| On pressed | A @ .10 / .07 | A @ .35 / .42 | same | TextSecondary |
| Disabled | FillControlDisabled | StrokeControlDefault | TextDisabled | TextDisabled |
HeartInk = A if contrast ≥ 3:1 over the on-fill composite, else `Tok.AccentTextPrimary`. Motion: user toggle-on → heart
spring pop 1→1.18→1 (~250ms, E2 `CheckedPopScale=1.18`), label cross-fade (E2 `LabelSwap`), root width reflow 260ms
FluentDecelerate (E2 `ContentReflow`); reduced motion snaps. No "Unfollow" hover text, no press scale.
```csharp
public sealed class FollowToggle : Component {
  public required string Uri { get; init; } public string? Name { get; init; } public FollowVerb Verb { get; init; }
  public override Element Render() {
    var page = UseContext(Design.AccentCtx.Slot); var lib = Library; if (lib is null) return new BoxEl();
    bool on = lib.IsSaved(Uri); ColorF a = page is { } p ? p.Value.Fill : Tok.AccentDefault;
    return ToggleButton.Controlled(Label(false), on, _ => lib.ToggleSaved(Uri, Name), glyph: Icons.Heart,
        checkedGlyph: Icons.HeartFill, style: ButtonRules.FollowStyle(a, Tok.IsLight)) with { Shrink = 0f };
  }
  public static Element SkeletonShape() => ToggleButton.Controlled(Loc.Get(Strings.Artist.Follow), false, static _ => { }, glyph: Icons.Heart);
}
```
`PreSaveButton` → `FollowToggle{Verb=PreSave}`; `SaveButton` plate → `Radii.ControlAll`, box 32.
New pure `Platform/ButtonRules.cs`: `FollowTint(ColorF a, bool light) → (Rest, Hover, Pressed, Stroke, PressedStroke, HeartInk)`,
`FollowStyle`, `PrimaryWidthNominal` + `Wavee.Tests/ButtonRulesTests.cs`.

### `Platform/Controls.Cta.cs` rewrite
Add `ButtonHeight=32`, `PrimaryMinWidth=120`, `PrimaryButton(label, onClick, ColorF accent, glyph?, wrap=false)`,
`PlayButton(ColorF|Func<ColorF> accent, onClick, label?, glyph?)` (bound fill cross-fades when the palette lands),
`AccentIconButton(accent, glyph, onClick)` (compact reader band), `OnMediaPalette` (scrim button over photos),
`AccentToggleStyle(fill)`. Palette via E1 `ButtonPalette.ForAccent` (single source; no app copy of tier math).
**Delete at the end:** `Controls.Play/Accent/Pill/IconPill/PillHeight/CtaPalette`, `FollowButton`, `Show.UI.PrimaryPill`
(wrap via PartLabel `MaxLines=2`), `Detail.UI.PlayPill` (renamed to `PlayButton`).

### Migration groups (disjoint files → parallel subagents)
- **A Platform core (orchestrator lands first):** `Controls.Cta.cs`, `Controls.cs` (FollowToggle, PreSave, SaveButton),
  `ButtonRules.cs` (new), `Controls.Art.cs` (Chip → `ToggleButton.Controlled`), tests `ButtonRulesTests`, `ControlsTests`, `DesignTests:255`.
- **B Artist:** `Artist.UI.cs:552-561` hero → [PlayButton][Shuffle Standard][FollowToggle][radio IconAction]; `:776`;
  ArtistHeroLayout heights (252→248, 300→292); `Artist.Page.cs:1756,1633,1510` (LinkPills→HyperlinkButton);
  `Artist.Reader.cs:1191,1199,1233,1922`; `Artist.Discography.cs:1250`; `ArtistHeroLayoutTests`.
- **C Detail/Album/Show/Episode/Concert:** `Detail.UI.cs` (PlayPill→PlayButton; callers Detail.UI:1325,1677, Detail.UI.Hero:422,
  Episode.Page:115, Show.UI:500; Fab/Satellite/More→IconAction; skeleton shapes 32/r4/120), `Album.UI.cs:1102,1030`,
  `Album.Page.cs:1262`, `Show.UI.cs`/`Show.Page.cs:1027-1036` (primary wrap, ghosts→Standard, LoadMore), `Episode.Discussion.cs:105`
  (ReactionPill restyled Standard r4), `Concert.UI.cs:337-351` (FilterToken→ToggleButton.Controlled, MoreToken→HyperlinkButton),
  `DetailSkeletonGeometryTests` (36→32; CTA row cases 44→36, 92→80).
- **D Search/Shell/Queue/Modules/User:** `Search.UI.cs:351,441,443,506`, `Shell/Rail.UI.cs:645`, `Shell/Video.UI.cs:99`
  (→PlayFab 64), `Queue.UI.cs:713` (→ToggleButton.Controlled accent style), `Modules.UI.cs:506-544`, `User.Cover.cs:1034`
  (→Button Standard + OnMediaPalette).
- **S Sidebar (wavee-sidebar skill):** `Sidebar.UI.Rows.cs:1334` Chip → `ToggleButton.Controlled(size: Small)`, `Sidebar.cs:533`
  ChipHeight 26→24, `Sidebar.UI.LibraryV3.cs:1456` documented exception restyled r4.
- Constants: `Detail.VerticalLayout.ActionRowHeight` 40→36, `RailLayout.PillHeight`→`ButtonHeight` 32, `FabSize` 40→32,
  `SatelliteSize` 36→32, `Skeleton.PlayPillWidth` 104→120. Skeleton shapes use the real controls (parity).
- Out of scope: non-interactive badges (ReleaseNotes chips, Diagnostics `Ui.Pill`, History stat pills), `ToolbarPillHeight` (name only).

---

## Workstream H — Home rebuilt from the ground up (app, parallel with B)

**Owner decision: the whole Home presentation layer is rebuilt from scratch.** New files, new types, designed from the
canvas and the component sheet. The old UI and rules code is **not** adapted, and is read only to learn the data
contracts. The only thing kept is the **data pipeline**; every old presentation file is deleted when the new screen goes live.

### Keep (data pipeline only)
- `Spotify/Spotify.Decode.Home.cs`: extended with new facts only.
- From `Entities/Home.cs`, only the storage and access layer: `HomeTable`/`SectionTable`, `SectionPaging`, the
  `Home`/`Section` handles, the `HomeCard` live read handle, `Entities.HomeFeed(facet)`, `Home.EnsureFeed`,
  `LiveAttemptConcluded`, `Inflight`, `FetchedAt`.
- Fetch plumbing in `Home.Host.cs`: `Feeds.Install/Refresh/RearmDaylist`.
- `Daylist.cs` rollover and edition timing rules.
- The Recents edge and Episode/Show entities.
- `HomeBrowseCards.ChartDeck` as a data source.
- `FoldDeck`/`HomeFoldTile` move to Browse-owned files unchanged, because Browse renders them and they are not Home UI.

### Rebuilt from scratch (new folder `src/apps/Wavee/Home/`, namespace `Wavee.HomeUi`)
There are no references to `HomeComposer`, `HomeFeedView`, `HomeGroup`, `HomeGroupKind`, `HomeRevealGate`,
`HomeFeedReadiness`, `HomeRow`, `HomeLandingProjection`, `HomeFacetProjection`, `HomeFacetStrip`,
`HomeModuleLayout`, `HomeHeroLayout`, `HomeWashSource`/`HomeWashBinder`, `HomeCardNav`, `HomeLayoutStore`/reducer,
`HomeCustomizerPage`, `HomeSectionPageView`, or any old module shell or card skin. The new code reads sections straight
from the tables:
```csharp
namespace Wavee.HomeUi;
public enum ZoneKind : byte { Daylist, RecentGrid, CoverShelf, WideTiles, ReleaseList, ClusterCards, MixedCovers, RadioShelf,
  BrowseTiles, EpisodeLead, ContinueEpisodes, ShowGrid, VideoTiles, EpisodeRows, PodcastGroups, EmptyFacet }
public readonly record struct SectionInput(int Slot, string Uri, string? Title, string? Subtitle, SectionKind Kind,
  IReadOnlyList<HomeCard> Cards, int TotalCount);                       // read directly off SectionTable (SectionReader)
public sealed record Zone(ZoneKind Kind, string Key, string? Title, string? Subtitle, IReadOnlyList<HomeCard> Items,
  HomeCard? Lead = null, IReadOnlyList<Cluster>? Clusters = null, int SectionSlot = -1, int TotalCount = 0);
public sealed record Cluster(string Over, string Name, IReadOnlyList<HomeCard> Rows, IReadOnlyList<HomeCard>? FooterShows = null,
  int FooterSectionSlot = -1, int FooterTotal = 0);
public sealed record ScreenModel(string Facet, IReadOnlyList<Zone> Zones, string Greeting, IReadOnlyList<FacetWord> Words);
```
Type names below use the new namespace. In the rules and wiring text that follows, `Home*` names map to their new
counterparts:
- `ZonePlanner` = the zone projection
- `SectionRoles` = the role rules
- `ClusterFold`, `ReleaseDetect`, `PodcastPlanner`, `EpisodeCaption`
- `FacetSwitch`, `FacetCache`, `FacetPivot`, `FacetRoute`
- `ZoneMetrics`, `GridFit`, `ShelfPaging`
- `ScreenReveal`: the new first-load reveal policy (hold until the chrome settles, force release at 8s), written fresh
- `CardNav`: open and play routing for a `HomeCard`
- `WashPick`: picks the shell wash from the daylist card or the first cover
- `LayoutFile`: `home-layout.json` v2 store, reducer and wire
- `CustomizeScreen`
- `SectionScreen`: the "See all" drill page, including the charts walk bar, rebuilt in the new style
Zone order — **All:** Daylist · RecentGrid · CoverShelf "Made for you" (lead = Discover Weekly header image) · WideTiles
New Music Friday (+ ReleaseList when detected) · ClusterCards · MixedCovers Jump back in · RadioShelf (lead) · BrowseTiles (+charts).
**Music:** CoverShelf per server section (Made For X lead DW) · ClusterCards · RadioShelf · BrowseTiles; no daylist/recents.
**Podcasts:** EpisodeLead · ContinueEpisodes · ShowGrid Your shows · VideoTiles · EpisodeRows · PodcastGroups · ShowGrid(s).
**Audiobooks:** CoverShelf for any sections else EmptyFacet.

### New files (all new; Role/Owner/Wave headers, Wave 6; folder `src/apps/Wavee/Home/`)
| File | Role | Content |
|---|---|---|
| `Home/Model.cs` | CORE | `ZoneKind`, `Zone`, `Cluster`, `ScreenModel`, `SectionInput`, `SectionReader.Read(Home h)` (table → inputs, memoized per section version) |
| `Home/ZonePlanner.cs` | CORE | `SectionRoles.Of`, `ZonePlanner.Plan(inputs, titles, layout, nowMs)`, `ClusterFold`, `ReleaseDetect`, `RadioMerge`, `PageDedupe` |
| `Home/PodcastPlanner.cs` | CORE | `PodcastPlanner.Plan` (06 §4.4 rules 1–12), `PodcastPhrases`, `EpisodeCaption` |
| `Home/Facets.cs` | CORE | `FacetWord`, `FacetPivot`, `FacetSwitchState`/`FacetPhase`, `FacetSwitch`, `FacetCache` (TTL 600 000 ms, prefetch 150 ms), `FacetRoute`, `ZoneEnter` |
| `Home/Metrics.cs` | CORE | `ZoneMetrics` (every constant commented with its sheet row), `GridFit`, `ShelfPaging` |
| `Home/Reveal.cs` | CORE | `ScreenReveal` (first-load hold/force policy), `WashPick`, `CardNav` rules (open/play routing) |
| `Home/Time.cs` | CORE | `Daypart` (OfHour, TryFromTitle, Elapsed, FormatCountdown into Span<char>), `RecentsWeek`/`WeekSummary`, `WhenCaption` |
| `Home/LayoutFile.cs` | CORE | v2 `home-layout.json` model, reducer, commands, wire, store (fresh; v1 files → defaults) |
| `Home/Items.UI.cs` | UI | item templates: `GridItem`, `WideLead`, `WideTile`, `RowItem`, `HistoryItem`, `ListRow`, `NavTile`, `EpisodeRow`, `DaylistCard`, `DaylistTimer` (component, 1s `UseInterval`, pooled char[8]), `NowPlayingMark` |
| `Home/Zones.UI.cs` | UI | `ChapterHeader` (title 20/28 + sub + See all + divider + chevrons + `PipsPager` on the E4 controller, `.Sticky(44, scope: zoneKey)`), All/Music zone views |
| `Home/Podcasts.UI.cs` | UI | podcast zone views |
| `Home/Facet.UI.cs` | UI | pivot (`Segmented` page-title preset, E3), `CompactBand` (E6, Segmented band preset), `FollowingToggle` (E2 subtle), busy `ProgressBar.Indeterminate(float.NaN)` (E5), failure `InfoBar` + Retry, empty-facet starter |
| `Home/Screen.UI.cs` | UI | `HomeScreen : Component` (per-tab facet state, reveal, switch, scroll, dim/enter, band, history arg) + `ZoneListLayout : IMeasuredVirtualLayout` (one per facet) + `WashBinder` |
| `Home/SectionScreen.UI.cs` | UI | "See all"/charts drill page, new style (`PagedShelf`-free `ItemsView.Grid` + walk `ProgressBar`) |
| `Home/Customize.UI.cs` | UI | `CustomizeScreen` (ToggleSwitch visibility + Reorderable), entry: "Customize Home" link at page end + palette command |
| `Spotify/Spotify.Decode.Home.cs`, `Entities/Home.cs` (data part) | CORE | new card facts: `ReleasedAtS`, `PlayedState`, `VideoThumb`, `ShowName/ShowUri`, flags Explicit/Unplayable + `HomeCard` members |
| `Entities/Entities.Fake.Home.cs` | CORE | mockup-mirroring dataset: All/Music/Podcasts/Audiobooks, releases section, per-facet latency/failure table (data, not env switch) |
| `Shell/Shell.cs` (+Host) | UI | `RouteKind.Home` → `HomeScreen`, `section` routes → `SectionScreen`, `home-customize` → `CustomizeScreen`; route-arg signal for facet history |

**Go-live and deletion (same wave, no dual paths):**
- Routes switch to the new screens.
- Delete `Entities/Home.Page.cs`, `Home.UI.cs`, `Home.Cards.UI.cs`, `Home.Artists.UI.cs` and `Home.Customizer.cs`.
- Delete the presentation half of `Home.Rules.cs`: projections, strip, hero/module/artist layouts, timeline merge,
  layout doc/reducer/store/wire, `HomeLandingRules`.
- Delete the presentation half of `Home.cs`: `HomeComposer`, `HomeFeedView`, `HomeGroup`, `HomeGroupKind`,
  `HomeSectionView`, `HomeChip` view, `HomeRevealGate`, `HomeFeedReadiness`, `HomeModuleTitles`, `SelectedFacet`.
- Delete `Home.Host.cs`'s `HomeLayoutStore`, `HomePreferences`, `MarkRevealed`/`HasRevealed` and `EnsureTopContent`.
- Delete every old Home test file for deleted code. The `HomeDecodeTests` and table/paging tests stay.

### Zone → controls → metrics (sheet, 1316 content width; `HomeZoneMetrics`)
Page pad 24/32, zone gap 40; facet row 40 + 20 below (+56 InfoBar when failed); band 44 (show >64, hide <56); chapter header 52+8.
| Zone | Controls | Metrics |
|---|---|---|
| Daylist | card `BoxEl` r8 FillCardDefault; `Controls.PlayButton(daylist accent)`, `Button.Create Standard` Shuffle, `Controls.IconAction` heart/more, `HyperlinkButton` tags; `ImageEl` inset r4 FocusY .4; `DaylistTimer` ring (`ProgressRing` determinate 16) + 5-seg daypart bar + labels | grid [1fr,540] gap 32, pad 12/12/12/32, h 320; <980 art 0.41W; <700 stacked |
| RecentGrid | `Ui.Grid` 4/3/2 cols gap 8; `RowItem` 64/48 art (round artists, playing = accent stroke + eq); `HistoryItem` last cell; header `HyperlinkButton` "Listening history ›" → `Shell.GoTo(RouteKind.Recents)` | rows·64+gaps |
| CoverShelf / MixedCovers / RadioShelf / ShowGrid | `PagedShelf` (`pager: None`, E4 controller, `leadSpan` E7 when lead has header image, `snap: Page`, gap 16, `perPageOverride = HomeGridFit.Columns`) | 6-up 206 (lead 428×206), 8-up 150.5 (lead 317×150); page stride 1332 |
| WideTiles | `PagedShelf` 3/page `fixedCardW=(W−32)/3` 16:9 | 428×241 + text |
| ReleaseList | `Ui.Grid` 2 cols, `ListRow` 64 (type badge, date col 88, hover play) | ≤6 rows |
| ClusterCards / PodcastGroups | `Ui.Grid` 3 cols gap 16, card r8, header over+name+divider, rows 56/40 art; podcast footer 6×28 thumbs + "See all N" | |
| BrowseTiles | `Ui.Grid` 4 cols `NavTile` (SettingsCard look 68/48 art/chevron) + 3 chart tiles (existing `HomeBrowseCards.ChartDeck`) | |
| EpisodeLead / ContinueEpisodes / VideoTiles / EpisodeRows | grids with `EpisodeRow` 76/56 art (E badge 14, video glyph 12, 2px progress hairline `BoxEl`, hover 32 round play), 16:9 `ImageEl` tiles 317×178 | |
| EmptyFacet | 480 block, 160 below row, `Button.Create Standard` | |
Hover = fill change only; covers r4, cards r8; type 12/14/20/28/40.

### Pure rules (tests in `Wavee.Tests`, no source-text tests)
- `HomeSectionRoleRules.Of`: Recents → Daylist → Personal (≥50% daily-mix/DW/daylist formats; "Soundtrack your…" mixes join as page 2) → WideEditorial (≥2/3 header images, Release Radar first) → Releases → Radio (≥50% radio formats; merge Stations→Popular→top-mix leftovers) → Cluster (baseline/"More like/For fans of") → History (first mixed section = Jump back in) → Browse (editorial/Shorts) → Generic → Drop (UnknownType/unplayable). `HomePageDedupe` top-down by card key.
- `HomeReleaseDetect.IsReleases` (owner: server sections only): generic section, ≥3 cards, ≥75% albums, ≥60% released ≤42 days (or future = "Upcoming"); title phrases only break ties.
- `HomeClusterFold`: group by entity; untitled + singletons → "More for you"; ≤4 rows/card.
- `HomePodcastProjection` (06 §4.4): lead = newest with video thumb; in-progress removed elsewhere; videos topped to 4; dedupe uri + (title, date); groups <3 fold into Episodes you might like; footer thumbs don't consume uris; all-empty → EmptyFacet.
- `HomeFacetSwitch` state machine (Idle/Loading/Refreshing/FadingOut/Failed): Fresh → instant (83 out/250 in); Stale → instant + background refresh (bar, no dim; swap only if at top, <2s, not interacted); Missing → Loading (dim .6 + bar); Fail → revert + InfoBar. `HomeFacetCache.Decide/ShouldPrefetch` (sub-chips never prefetched). `HomeFacetPivot.Words/Resolve/Target/Enabled`.
- `HomeZoneMetrics/HomeGridFit/HomeShelfPaging` (206/150.5/428/1332; tiers 6/5/4/3 & 8/6/5/4 at 1100/900/700), `DaylistDaypart`, `RecentsWeekSummary`, `RecentsWhen`, `HomeEpisodeCaption`.
- (Rule names in this list use the old `Home*` prefix for readability; they are implemented fresh under `Wavee.HomeUi`
  with the new names listed in "Rebuilt from scratch", e.g. `HomeSectionRoleRules` → `SectionRoles`,
  `HomeFacetSwitch` → `FacetSwitch`. Test files follow the new names under `Wavee.Tests/HomeUi/`.)
- Test files: `HomeZoneProjectionTests`, `HomeSectionRoleTests`, `HomeClusterFoldTests`, `HomePageDedupeTests`, `HomeReleaseDetectTests`, `HomePodcastProjectionTests`, `HomeEpisodeCaptionTests`, `HomeFacetSwitchTests`, `HomeFacetCacheTests`, `HomeFacetPivotTests`, `HomeZoneMetricsTests`, `DaylistDaypartTests`, `RecentsWeekSummaryTests`, `RecentsWhenTests`; update `HomeLayoutTests`, `HomeLayoutStoreTests`, `EntitiesFakeHomeTests`, `HomeDecodeTests`.

### HomeScreen wiring (new component, written fresh)
- **Per-tab state:** `_facet Signal<string>`, `_followingOn Dictionary<string,bool>`, `_switch Signal<FacetSwitchState>`,
  `_scroll ScrollHandle`, `_layouts[facet]`. These reach the pivot and band through `Ctx.Provide(FacetCtx.Slot, …)`, per
  the props-freeze rule. There is no app-global selected-facet static.
- **Data loop:** a `UseSignalEffect` reads the facet's `Home` row (`Entities.HomeFeed(facet)`: `Changed`, `Inflight`,
  `FetchedAt`, `LiveAttemptConcluded`), builds `SectionReader.Read`, then `ZonePlanner`/`PodcastPlanner`, and offers the
  result to `ScreenReveal`, a fresh first-load policy: hold until the page structure settles, force-release at 8 s,
  never show a provisional empty page.
- **`SelectFacet(target)`:**
  1. `FacetCache.Decide(...)`.
  2. `_switch = FacetSwitch.Select(...)`.
  3. `_facet = target`.
  4. Push the history arg (`FacetRoute.ArgOf`).
  5. If Missing: `Home.EnsureFeed`. If Stale: `Entities.Refresh(Homes, slot, HomeFields.All, Visible)`.
- **When the document lands:** `FacetSwitch.Landed` → FadingOut → an 83 ms `UseTimeout` → publish the new
  `ScreenModel` and call `OutDone`.
- **On failure:** `FacetSwitch.Fail` reverts `_facet` and the history arg, and shows an **InfoBar** with Retry
  (no `Notify.Say`).
- **Skeleton:** `Skel.Region` shaped exactly like the new layout, used on the first load only.
- Dim: zone rows `Opacity` bound (.6 dim / 0 fading / 1) with 167ms/83ms `Transition`, `HitTestVisible = !dim`; row 0 never dims.
- Enter: `HomeZoneEnter.DelayMs` ⇒ `Enter = EnterExit(Opacity 0, Y 8)` 250ms decelerate, 30ms × index (≤5).
- Scroll: one `VirtualListEl`, `ScrollKey = scope + "home:" + facet` (verify per-key restore; engine fix if needed),
  compact band = `UseScrollThreshold(64, 56)`; only one of pivot/segmented in the a11y tree; chapter headers `.Sticky(44)`.
- Following: `HomeFacetFollowing` (E2 subtle style, glyph PersonCheck), shown only when the chip has subChips; on ⇒
  `SelectFacet(word.SubId)`; memory per facet word for the tab lifetime.
- Keyboard: pivot = one tab stop, arrows move focus (no selection), Enter/Space select; hover/focus 150ms ⇒ prefetch.
  Palette commands "Home: All/Music/Podcasts/Audiobooks", "Home: Following on/off".
- Freshness: `Homes.FetchedAt` is the clock (no new persistence).
- Play colour on Home: daylist card uses `HomeCard.Accent` → graded cover (`WashPick`) → system accent
  (differs from the mockup's system blue on purpose). Hover/row play buttons: system accent.

### Removals (no legacy paths — whole files, at go-live)
See "Go-live and deletion" above: every old Home presentation file and class (incl. the top-artist podium, timeline,
`Feeds.EnsureTopContent` + top artists/tracks fetch where only Home/Fake use them — grep first), and their tests.
Customizer: fresh `home-layout.json` **v2** (`LayoutFile`) with zone kinds (`daylist, recents, madeForYou, newMusic,
releases, becauseYouLike, jumpBackIn, radio, browse`); v1 files → defaults (accepted break); All facet only; rewrite
`.claude/skills/wavee/home-layout.md` for the new architecture (and the wavee skill's Home references).

---

## Waves & parallelism (subagents on disjoint files; only the orchestrator builds/tests/launches; new agents use sonnet)

| Wave | Engine (fluent-gpu) | Buttons (B) | Home (H) |
|---|---|---|---|
| 0 | plan doc + issues | | |
| 1 | E1, E2 (+gates) | — | A1 `Model.cs`+`ZonePlanner.cs` · A2 `PodcastPlanner.cs` · A3 `Facets.cs` · A4 `Metrics.cs`+`Time.cs` · A5 `Reveal.cs`+`LayoutFile.cs` · A6 decode facts + mockup fake dataset (all CORE + tests, parallel; new code compiles beside the old, not yet routed) |
| 2 | E3, E4, E5, E6, E8 | Group A Platform core (orchestrator) | Gate A: build D+R, tests D+R |
| 3 | E7 (after E4) | Groups B, C, D, S in parallel | B1 `Items.UI.cs` · B2 `Zones.UI.cs` · B3 `Podcasts.UI.cs` · B4 `Facet.UI.cs` · B5 `SectionScreen.UI.cs`+`Customize.UI.cs` (B1 publishes template signatures first; lead renders as a normal cell until E7) |
| 4 | engine gates D+R + VerticalSlice + canon | layout constants/tests | C1 `Screen.UI.cs` + Shell routing + route-arg history/deep link + strings + palette commands → **go-live: route switch + delete all old Home presentation files/tests in the same change**; FoldDeck/HomeFoldTile moved to Browse |
| 5 | | final deletion of pill helpers (after H is live) | parity pass vs canvas, skill docs rewrite, CHANGELOG `(#n)` bullets |

C1 (Wave 4) owns the facet history/deep-link work (Shell route-arg signal in `Shell.cs`/`Shell.Host.cs`, `FacetRoute`
rules, deep-link doc) — Group D (buttons) touches only `Shell/Rail.UI.cs`/`Video.UI.cs` (disjoint). Because the new Home
never calls the old pill helpers, Buttons group A and Home are fully independent.

## Design parity with the canvas

Goal: at 1440×900 with the **mockup fake dataset**, Home is indistinguishable from the canvas boards except for the
deliberate differences listed below.

- **Mockup fake dataset** (`Entities.Fake.Home.cs`, A6): mirrors the canvas content 1:1. Same items, order, covers (the
  canvas blob images come from the same Spotify image URLs), captions, counts, daylist title/tags/countdown window,
  Recents order and "when" values, and podcast episodes. Screenshots are therefore comparable item by item.
- **Parity tokens:** every metric in `HomeZoneMetrics` and every token/colour is copied from `Components.dc.html`
  (sizes, radii, gaps, type ramp, fills/strokes, motion durations), and each constant carries a comment naming its sheet row.
- **Parity check per wave** (orchestrator): capture All/Music/Podcasts/States/mid-switch/band screenshots and place them
  side by side with the matching canvas board. Diff checklist: spacing, sizes, type, colours, hover/pressed/focus states,
  motion timing. Any deviation is fixed or explicitly accepted by the owner.

**Deliberate differences (by design or owner choice):**
1. **Play button colour.** Play uses the page palette accent (owner decision), so the daylist Play takes the daylist's
   colour instead of the mockup's system blue.
2. **Mica.** Real Mica/wallpaper tint replaces the CSS gradient approximation.
3. **Engine controls are the real WinUI controls.** Segmented, PipsPager, ProgressBar, InfoBar, ToggleButton and
   HyperlinkButton are styled toward the sheet through parts. Hairline details (pip size, InfoBar icon, the
   ProgressBar's two-indicator curve) follow real WinUI where the CSS was an approximation.
4. **Live data varies.** With a real account, zones appear only when the server sends them:
   - "From artists you follow" shows only if a releases section exists.
   - Section titles and counts are the server's.
   - The mockup's illustrative numbers (1,284 liked songs, 142 plays, release dates) become real values.
5. **Other window sizes** follow the responsive tiers (the canvas shows 1440 only).
6. **Dashed history outline** falls back to a 50% solid stroke if E8's dash support slips.
7. **Features the mockup had no room to draw:** history/deep-link, keyboard focus and the customize link are added.

## Verification
1. Engine: `dotnet build src/FluentGpu.slnx` Debug + Release; `dotnet run --project src/FluentGpu.VerticalSlice` → "ALL CHECKS PASSED"; `dotnet test src/FluentGpu.Engine.Tests`; canon check; gallery screenshots vs `Components.dc.html` rows 1, 5–8, 12–15.
2. App: `dotnet build Wavee.slnx` and `-c Release` clean (TreatWarningsAsErrors); `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` **Debug and Release** (Release JIT catches the FreshSlot miscompile class).
3. `dotnet run --project src/apps/Wavee -- --fake`, sandbox-free launch per the capture memory, 1440×900 dark + light screenshots compared with the canvas boards: facet switch then Back/Forward walks facets; deep link `wavee://open?route=home&arg=podcasts-chip` opens Podcasts; All at rest; scrolled 400px (band + stuck chapter header); Music Following off/on; Podcasts; Audiobooks empty; mid-switch (fake latency: dim + bar); failure (InfoBar + revert); artist/album/show/episode/search heroes (Play palette, FollowToggle pop on click only, never on load; reduced motion snaps); queue/sidebar/concert toggles; video poster; User cover.
4. Runtime health: `scroll.burst` Smooth while scrolling All end to end; Diagnostics ▸ Tiles counters 0; ReuseGuard log silent; zero-alloc gates green; keyboard: focus ring on every control, pivot arrows don't select, Enter selects.
5. Stop launched instances by PID only (never by image name).

## Owner decisions (confirmed)
- Play = page palette accent (E1); Follow = custom-styled WinUI ToggleButton (FollowToggle spec above).
- "From artists you follow" = server sections only (`HomeReleaseDetect`).
- Buttons and Home run in parallel.
- **Delete** the top-artist podium (`Home.Artists.UI.cs`, `HomeArtistRowLayout`), the what's-new/concerts timeline
  (`HomeTimelineView`, `HomeTimelineMerge`), `Feeds.EnsureTopContent`/`TopContentState` and the `Edges.UserTopArtists/
  UserTopTracks` fetch where Home/Fake are the only users (verify with grep; keep anything another page uses).
- **Facet history + deep link in v1** (Wave 4, part of C1):
  - A facet switch pushes a history entry on the current tab: `Shell.GoTo(new Route(RouteKind.Home, arg: facetId), …)`
    with the same Home page instance retained (verify `Shell.cs` Route/arg + keep-alive: navigating Home→Home with a
    different arg must re-use the mounted `HomeLandingView`, not remount). Back/Forward walk facets.
  - `HomeLandingView` reads the tab's current route arg through a **signal** (new `Shell.RouteArg(tab)` read signal, or
    `UseContext` of the shell's route slot) instead of freezing it at mount (`LandingPageFor` today); arg changes drive
    `SelectFacet(arg)` (history-driven switches use the same keep-old-page/dim/bar rules; no new history push).
  - Deep link `wavee://open?route=home&arg=<chip-id>` (see `.claude/skills/wavee/deep-linking.md`); unknown chip id ⇒ All.
  - Pure rule `HomeFacetRoute.{ArgOf(facet), FacetOf(arg, words)}` + `HomeFacetRouteTests`; `ShellRoutesTests` case for
    `home` + arg round-trip. Launch/restore still opens All (default arg "").
- Customize entry: "Customize Home" HyperlinkButton at the end of the All page + palette command.

## Remaining assumptions (defaults)
- Q2 RecentGrid "when" captions + sparkline use the existing Recents edge fetch (one extra request on All).
- Q3 Daypart hours 4–7 early, 7–12 morning, 12–17 afternoon, 17–21 evening, 21–4 night; daylist title token wins.
- Q4 Podcast phrase matching is English; other locales degrade to generic shelves.
- Q5 Radio format tokens verified against fixtures before `RadioFormats` is final.

---

## Appendix A — Component tree (new `Wavee.HomeUi`)

```
HomeScreen (Home/Screen.UI.cs)                     per-tab: _facet, _followingOn, _switch, _scroll, _layouts[facet]
├─ WashBinder                                      shell wash ← WashPick(daylist card | first cover)
├─ Skel.Region(first load only)
│   └─ Overlay
│       ├─ VirtualListEl(ZoneListLayout[facet], ScrollKey = scope+"home:"+facet, Handle=_scroll)
│       │   ├─ [0] FacetRow            key "home:facet"   Segmented(page-title preset) · FollowingToggle · ProgressBar(NaN) · InfoBar
│       │   ├─ [1..n] ZoneRow(zone)    key "home:"+facet+":"+zone.Key   Opacity(dim) · HitTestVisible · Enter(stagger)
│       │   │   ├─ ChapterHeader       .Sticky(44, scope=zoneKey)  title · sub · See all · | · ‹ PipsPager ›  (ShelfController)
│       │   │   └─ zone body           DaylistCard | RecentGrid | PagedShelf(leadSpan) | Grid(ListRow/ClusterCard/NavTile/EpisodeRow…)
│       │   └─ [n+1] Tail              "Customize Home" HyperlinkButton (All only) + dock reserve
│       └─ CompactBand (UseScrollThreshold(64,56))   Segmented(band preset) · FollowingToggle(28) · ProgressBar/divider · InfoBar
SectionScreen (Home/SectionScreen.UI.cs)            "See all" + charts walk
CustomizeScreen (Home/Customize.UI.cs)              visibility ToggleSwitch + Reorderable over LayoutFile v2
```

## Appendix B — Wireframes (1440×900, content 1316 wide)

```
┌ title bar ─────────────────────────────────────────────────────────────────────────────────────────────┐
│ ≡ ‹ › ⌂ Home                    [ Search songs, artists, albums…        ⌕ ]            (◉) ☼  ─ □ ✕ │
├──┬──────────────────────────────────────────────────────────────────────────────────────────────────────┤
│♡ │  All   Music   Podcasts   Audiobooks                                              [👤✓ Following]   │  ← facet row (28/36; toggle only w/ subChips)
│◎ │ ┌ card r8 ─────────────────────────────────────────────┬──────────────────────────┐                  │
│☺ │ │ Good morning, Chris · your daylist                    │                          │                  │
│⌂ │ │ scream teen pop friday morning        (40/52)         │   header image inset r4  │                  │  ← Daylist card 320
│──│ │ scream · teen pop · belter · …   Playlist·Spotify·50  │                          │                  │
│▣ │ │ [▶ Play (palette)] [⤮ Shuffle] ♡ …                    │                          │                  │
│▣ │ │ ◔ Next daylist in 01:35:05 · friday afternoon 13:04   │                          │                  │
│  │ │ ▬▬ ▬▬▬▬░░ ░░░░ ░░░░ ░░░░  Early·Morning·After·Eve·Night│                          │                  │
│  │ └───────────────────────────────────────────────────────┴──────────────────────────┘                  │
│  │ Recently played  Pick up where you left off                           Listening history ›            │
│  │ [●art KKA ·playing][▣ Liked 2h][● Rich Brian 5h][▣ WLUWD yest]                                       │  ← 4×2 row items 64
│  │ [● Arash Wed][▣ Arcane Tue][▣ 90's NL Mon][┆⏲ Your listening history ▂▅▁█▃▆▂ 142 plays ›┆]         │
│  │ Made for you  Daily Mixes refreshed…           See all │ ‹ • ○ ›                  (sticky @44)       │
│  │ [ Discover Weekly  (2-cell wide lead) ][DM1][DM3][DM4][DM5]                                          │  ← PagedShelf leadSpan 2
├──┴──────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ▣ ☐ Playing on iPhone / She Looks So Perfect ♡     ⏮ (⏸) ⏭  0:19 ━━━━━───────── 3:22     ⤮ ↻ 🔊 ✎ ▣ …  │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────┘

Scrolled > 64:  ┌ [All|Music|Podcasts|Audiobooks]  (Segmented, 44px band)             [👤 Following] ┐
Mid-switch:     facet row → selected word bold at once; 3px indeterminate bar under it; page below at 60%, no input
Failure:        [✕ Couldn't load Podcasts  Check your connection and try again.   [Retry]  ✕]  (InfoBar), old page 100%
```

## Appendix C — References
- Mockup canvas: https://claude.ai/artifact/Qxb9bZUyCjqVsBv21gy8m8 (boards Main, Light, Podcasts, Music, States,
  Components = pixel reference, FacetSwitch, Motion). Local source: `home-canvas-pub/project/*.dc.html` (untracked).
- Design notes: `06-facet-design.md` (facet control, switch, per-facet layouts, podcast rules §4.4),
  `05-native-research.md` (native WinUI rules) — copied into `docs/plans/wavee/home-redesign/` in Wave 0.
