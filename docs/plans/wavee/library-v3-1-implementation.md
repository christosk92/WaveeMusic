# Library V3.1 — implementation plan

Status: in progress (branch `feat/library-v3-1`, 2026-09-06). Issue: #104. Mockup: `library-v3-1-mica.html`.

Design source: `docs/plans/wavee/library-v3-1-mica.html` (published artifact, approved 2026-09-06).

## Context

Library V3 today says "Albums" twice two rows apart with two meanings (a page in the destination word rail, a
filter in the chips), truncates its own title to "Your L…" (the title is `Shrink=1` behind an inline search host
and three icons), renders Home as a 40-DIP Cozy row above the library's own title, gives every row a pin glyph at
rest plus a hover "…" (≈44 DIP of label lost), drifts its subtitle grammar ("50 songs" / "Album · X" / "Artist"),
and buries Sort/View in the "…" menu. V3.1 replaces the mode outright (no legacy path): one taxonomy (the chips),
a header that follows a Priority+ ladder where the title never yields, a 32-DIP lens row carrying the count, the
page link for Albums/Artists/Podcasts and the Sort/View controls, Liked Songs as a system row at the top of the
pinned band, a Compact shortcut band, and a one-trailing-slot row anatomy that only V3 opts into.

Decisions taken with the user (2026-09-06):
- **Row anatomy is V3-only, through a `SidebarPaneConfig` seam** (`RowStyle`), never a Design branch and never a
  persisted display flag. Classic/Curated rows stay byte-identical.
- **Local files is retired from every navigation surface** (route, Classic row, templates, pins, customizer,
  destinations, probe, loc). The `LocalSource` uri owner and `wavee:local:file:` playback stay; only the "Local
  Files" page/collection surface goes.
- **Title = collapse toggle**; the "‹" chevron leaves the header; a "Collapse" row returns to the "…" menu.
- **Issue**: Claude drafts a new GitHub issue (github-triage skill) and asks before `gh issue create`; the
  CHANGELOG bullet ends with ` (#n)` and the commit body carries `Fixes #n`.

Rules that bind every workstream: `.claude/skills/wavee-sidebar/SKILL.md` iron rules (one renderer, config = the
only mode seam, one height per section, one inset owner, no engine edits), `pitfalls.md` (props freeze at mount,
never write a signal from Render, a zero-size flex child still collects Gap, Reorderable slot fill), `testing.md`
(tests never read source; decisions live in source-included pure files). Agents do not build or test — the
orchestrator does (`dotnet build Wavee.slnx` Debug + Release, the filtered test sweep, the screenshot probes).

## Target chrome stack and row anatomy

```
 PanePad.Top 8
 ┌ LibraryV3NavBand ───────────────────────────────────┐  TopBar rows, Compact 32 (was Cozy 40); rule below
 │ ▌⌂ Home                                              │
 ├ LibraryV3Header 44 ─────────────────────────────────┤  title = collapse toggle, Shrink 0, never ellipsized
 │ [⫶ Your Library]        (search) [🔍] [+] […]        │  ladder: ≥320 inline field · 240–319 icons · <240 "+" folds
 ├ LibraryV3Chips 36 (was 40) ─────────────────────────┤  Playlists · Albums · Artists · Podcasts (order changed)
 ├ LibraryV3LensRow 32 (NEW) ──────────────────────────┤  "All · 212" | "All albums › 312" | "Playlists · By you · 31"
 │ All albums ›  312                    Recents ▾  ▤    │  right: sort text button + view glyph (hidden <200)
 │ (breadcrumb 32 replaces this row while drilled)      │
 ├ scroll surface (ONE ItemsView, the plan) ───────────┤
 │ v3.system   StaticLinks  ♥ Liked Songs / Playlist · 1,204 songs      (Cozy 44, Artwork, Subtitles)
 │ v3.pins     Pinned       user pins … + PinEnd gutter (24, dashed hairline)
 │ v3.rule     Divider      (only when there is no pin band; 16)
 │ v3.library  PlaylistTree | EntityList (unchanged)
 └──────────────────────────────────────────────────────┘
```

Row anatomy at `RowStyle.Slot` (V3 only):

```
 ┌4┐3┐6┐───32 art───┐6┐ title (Shrink 1, ellipsis)                       ┐ Trailing? ┐ slot 28 ┐8┐
 │ │▌│ │  cover     │ │ 📌 Playlist · Spotify   (pin mark leads subtitle)│ (badge/+)  │ …/♪/›   │ │
 slot content at rest: folder chevron > equalizer (playing) > empty; hover/focus: "…" (never on folders)
```

## Workstreams (parallel subagents on disjoint files; only the orchestrator builds/tests/launches)

### W0 — orchestrator prelude (sequential, before the fan-out)
1. Write `docs/plans/wavee/library-v3-1-implementation.md` (this plan + the code/wireframes, the repo's plan format).
2. Draft the GitHub issue via the `github-triage` skill (title "Library V3.1: one taxonomy, a title that never
   truncates, one trailing slot; retire Local files"; labels `type: feature`, `area: sidebar`; milestone
   `0.2.x Breaker`) and **ask the user before `gh issue create`**. Filed as **#104** (2026-09-06).
3. Loc keys (one owner; all three files `src/apps/Wavee/assets/loc/{en-US,nl,ko-KR}.json`, CRLF, UTF-8 no BOM;
   check no leaf collides with its group name — `pitfalls.md` "sidebar.pin.pinTo"):
   - `sidebar.v3.lens.all` = "All"; `sidebar.v3.lens.playlists` = "All playlists"; `.albums` = "All albums";
     `.artists` = "All artists"; `.podcasts` = "All podcasts"; `sidebar.v3.lens.matches` =
     `{count, plural, one {# match} other {# matches}}`; `sidebar.v3.lens.openPage` = "Open {page}" (tooltip).
   - `sidebar.v3.newPlaylist`/`newFolder` are NOT needed — reuse `Strings.Detail.NewPlaylist` and
     `Strings.Sidebar.CreateFolder` (already used by `LibraryV3Header.CreateMenu`).
   - Retire `nav.localFiles` from all three files (W7 removes its last reader).
4. Branch: new branch off `main` (current checkout is on `fix/audio-disk-cache-tests`).

### W1 — header ladder (title never truncates, title = collapse toggle)
Files: `Modes/LibraryV3/LibraryV3HeaderRules.cs` (NEW, engine-free), `LibraryV3SearchRules.cs`,
`LibraryV3Search.cs`, `LibraryV3Header.cs`, `src/apps/Wavee.Tests/LibraryV3HeaderRulesTests.cs` (NEW),
`LibraryV3SearchRulesTests.cs`, `Wavee.Tests.csproj` (add the include beside `LibraryV3SearchRules.cs:291`).

```csharp
// LibraryV3HeaderRules.cs — the ONE rule for the header row's shape (Priority+: the title is the last to yield)
static class LibraryV3HeaderRules
{
    public const float InlineSearchWidth = 320f;   // ≥: the closed field sits inline and grows
    public const float CreateFoldWidth   = 240f;   // <: "+" folds into "…" (New playlist · New folder)
    public readonly record struct Shape(bool InlineSearch, bool SearchTakesRow, bool ShowsCreate);
    public static Shape Resolve(float paneWidth, bool searchOpen, bool hasText)
    {
        bool inline = paneWidth >= InlineSearchWidth;
        bool takesRow = !inline && (searchOpen || hasText);     // the field covers the title; Esc/✕ restores it
        return new(inline, takesRow, paneWidth >= CreateFoldWidth);
    }
}
```
Width budget at the 180 floor: header lane 180 − 21 − 16 = 143; two 28-DIP icons + two 4-DIP gaps = 64; the title
button ("Your Library" 15/600 ≈ 84 + 10 padding) = 94 → 158 > 143 is why the title is **text-only** (the engine's
`Icons` table has no library glyph — `MusicNote` is the closest — and the chrome sweep already deleted the old
decorative one). The ladder therefore has two rungs, and at 180 the row is [title 94][spacer][🔍][…] = 150 with the
spacer at 0; if measured text exceeds that, `Shrink = 0` on the title pushes the icons, never ellipsizes — verify at
180 in the eyeball pass and lower `CreateFoldWidth` is not the lever (it already folded); the fallback is 14-DIP
title type below 200, decided in the same rules file.
- `LibraryV3SearchRules.InlineWidth` 420 → delegates to `LibraryV3HeaderRules.InlineSearchWidth`; `Resolve`
  keeps its signature (`Search.cs:73,151,163`, `Header.cs:71` callers) but `Inline`/`Expanded` come from the
  ladder. Delete `OpenWidth`, `TitleReserve`, `TrailingControlsWidth` and the three `OpenWidth_*` tests: the open
  narrow field now takes the row (`Grow = 1`), the title is hidden via `Flow`, so no width arithmetic remains.
- `LibraryV3Header.Render`: title becomes a button —
  `new BoxEl { Direction=0, Gap=8, Height=28, Padding=(4,0,6,0), Corners=Radii.ControlAll, Role=Button,
  Focusable=!InDrawer, Cursor=Hand, OnClick=_session.Collapse, Shrink=0 (never 1), Children=[glyph?, TextEl 15/600] }
  .Interactive(Interaction.Subtle)` wrapped in `ToolTip.Wrap(…, Loc.Get(Strings.Sidebar.V3.Collapse))`; in the
  drawer it is a plain `TextEl` (no rail to collapse into). Text-only: no leading glyph (see the width budget
  above; `pitfalls.md:176`'s "mangled U+E71C literal" entry is STALE — that glyph was already deleted — W6 retires it).
  Hidden when `shape.SearchTakesRow` (`Flow.Show`, never `Width=0`). Spacer `Grow = inline ? 0 : 1` stays.
  `[+]` only when `shape.ShowsCreate`; the "‹" chevron and its `Key="v3-collapse"` go.
- `BuildOverflow` order: Sidebar layout ▸ · sep · (New playlist · New folder — only when `!ShowsCreate`, resolved
  at open time from `_session.Width.Peek()`) · Clear filters · Collapse (`_session.Collapse`, not in drawer) · sep ·
  API console (dev). **Sort by / View as submenus are removed** (they live on the lens row, W2).
- `LibraryV3Search`: `Shrink = 0` in every shape (it never squeezes the title); open narrow shape: host
  `Grow = 1, Width = NaN`; keep `HostMorph` for the 32↔row morph; Escape ladder/blur rules unchanged.
- Tests: `Resolve_*` theories for 180/200/239/240/319/320/460 × searchOpen; the old `Resolve_NarrowPane_*` updated
  to 320; `Escape_*`/`Blur_*` untouched.

### W2 — lens row, metrics, chip order
Files: `Modes/LibraryV3/LibraryV3LensRules.cs` (NEW, engine-free), `LibraryV3LensRow.cs` (NEW component),
`LibraryV3Chrome.cs`, `LibraryV3Metrics.cs`, `LibraryV3ChipStrip.cs`, `src/apps/Wavee.Tests/LibraryV3LensRulesTests.cs`
(NEW), `LibraryV3ChipStripTests.cs`, `Wavee.Tests.csproj` (include).

```csharp
// LibraryV3LensRules.cs — what the row under the chips says and which controls it carries
static class LibraryV3LensRules
{
    public const float ViewToggleWidth = 200f;                       // below: the view glyph is dropped, sort text stays
    public readonly record struct Shape(bool Visible, int Filter, int Qualifier, bool Searching,
                                        string? PageRoute, bool ShowsView);
    public static Shape Resolve(int filter, int qualifier, bool searching, bool drilled, float paneWidth) =>
        new(Visible: !drilled, filter, qualifier, searching,
            PageRoute: searching ? null : LibraryV3ChipStrip.RouteFor(filter),   // Albums/Artists/Podcasts only
            ShowsView: paneWidth >= ViewToggleWidth);
}
```
- `LibraryV3LensRow : Component` (mounted by `LibraryV3Chrome` after the chips; `Key = "v3-lens"`; height
  `LibraryV3Metrics.LensRowHeight = 32`; padding `SidebarPaneMetrics.LeadBandInset`; reads `_session.ReadState()`
  for filter/qualifier/searching/drilled, `prefs.Entries.Version.Value` + `_session.View.Count + pinBand` for
  the count — the exact expression `LibraryV3Chrome.cs:105-109` already computes; move it into a
  `LibraryV3Session.VisibleRowCount()` helper so the chrome and the lens row cannot disagree).
  Left: label = filter==All ? `lens.all` : `lens.{facet}`; qualifier ≠ Any appends ` · ` + `LibraryV3Labels.Qualifier`;
  when `PageRoute != null` the label is a link (`HomeModules.ModuleHeader` idiom: `BoxEl { Role=Hyperlink,
  Focusable, Cursor=Hand, OnClick=() => _session.Go(route, LibraryV3Labels.Filter(filter)), Children=[TextEl 12/600
  TextPrimary, Icon(Icons.ChevronRight, 10f, Tok.TextTertiary)] }.Interactive(Interaction.Subtle)`, tooltip
  `Strings.Sidebar.V3.Lens.OpenPage(ShellNav.Dest(route).Title)`); otherwise a plain 12/600 `TextSecondary` TextEl.
  Then `SidebarCounts.Number(count)` — or `Strings.Sidebar.V3.Lens.Matches(count)` as 11/tertiary text when searching.
  Right: sort text button `[LibraryV3Labels.Sort(sort) + ChevronDown 10]` (12/600 TextSecondary, 24 tall, Subtle
  hover) opening `MenuFlyout.Create(V3SortViewMenu.SortRows(prefs), …, minWidth: 200)` at
  `FlyoutPlacement.BottomEdgeAlignedRight` (build rows at OPEN time — `Loc` subscribes the culture epoch); view
  glyph `IconButton.Create(LibraryV3Labels.ViewGlyph(view), …, size: Small)` opening `ViewRows`. Both carry
  `Strings.Library.SortBy` / `ViewAs` tooltips and the existing `sidebar.a11y.sortView` group label.
- `LibraryV3Chrome.Render` bands: nav · header · chips · (drilled ? breadcrumb : lens row) · banner/empty. Delete the
  `"v3-chrome-rule"` divider (the lens row is the separation).
- `LibraryV3Metrics`: `NavRowHeight` 40 → 32; `ChipRailHeight` 40 → 36; add `LensRowHeight = 32f`; delete every
  `Destination*` constant (7 of them) and the stale `DestinationLabelW` mention.
- `LibraryV3ChipStrip` facet order → `Playlists, Albums, Artists, Podcasts` (+ test).

### W3 — nav band + rail
Files: `Modes/LibraryV3/LibraryV3NavBand.cs`, `Modes/LibraryV3Sidebar.cs`, `Pane/SidebarPaneRail.cs` (liked tile only).
- Delete `DestinationRail`, `RailChevrons`, `RailChevron`, `ScrollRailBy`, `DestinationCount`, `_railViewport`,
  `_railCanScrollLeft/Right` and the `kids.Add(DestinationRail(route))` call; the file's header comment loses the
  word-rail paragraphs. Nothing outside the file references them.
- The four row builders: `Density = SidebarDensity.Compact, Height = LibraryV3Metrics.NavRowHeight` (32);
  `ActionRow` leading `SidebarRowMetrics.ArtFor(SidebarDensity.Compact)` (20); `TrackRow`/`EntityRow` art edge
  `SidebarRowMetrics.ArtFor(Compact)` instead of the literal `32f`. Rows are chrome, so `SidebarShortcutsSection.From`
  (Classic/Curated, Cozy 44) is untouched.
- `LibraryV3Sidebar`: `RowStyle = SidebarRowStyle.Slot` in the config (W4 defines the member); `BuildRailHead`
  drops the five destination tiles (the `v3.system` section is `ShowInRail: true`, so the planner's `BuildRail`
  draws Liked; user pins and the library follow as today). Rail polish: a route tile whose key
  `SidebarCover.IsLiked(key, null)` draws `SidebarRailItem.Art(key, SidebarCover.Liked(SidebarRailItem.ArtEdge), …)`
  instead of the heart glyph (`SidebarPaneRail.Build`'s route arm; verify where `SidebarRailItem.Icon` is chosen).

### W4 — one trailing slot, pin in subtitle, "Kind · detail", system route rows (V3 opt-in via `RowStyle`)
Files: `Shared/SidebarEntityRow.cs`, `Pane/SidebarPaneSlot.cs`, `Pane/SidebarPaneText.cs`, `Pane/SidebarPaneConfig.cs`,
`Pane/SidebarPane.cs` (**`RowPlayUri` only**, ~`:1446-1455`), `Data/SidebarRowGeometry.cs`,
`Data/SidebarSubtitleRules.cs` (NEW, auto-included), tests `SidebarSubtitleRulesTests.cs` (NEW), `SidebarRowGeometryTests.cs`.

**The seam.** `Pane/SidebarPaneConfig.cs`: `public SidebarRowStyle RowStyle { get; init; } = SidebarRowStyle.Cluster;`
+ `public enum SidebarRowStyle : byte { Cluster = 0, Slot = 1 }` (doc: Cluster = the landed anatomy — trailing
cluster, ZStack "…", pin glyph in the cluster, today's `SubtitleOf` strings — **byte-identical**; Slot = V3.1). A new
`SidebarRowSpec.Style` field (default Cluster) is stamped by every `SidebarPaneSlot` builder from `_o.Config.RowStyle`
(verify the slot owner exposes `Config`; otherwise mirror it as an internal `SidebarPane.RowStyle` beside `Store`).
Every V3.1 arm below is `Style == Slot ? new : old`, with the old code moved verbatim into a `Cluster…` helper so the
Cluster path can be deleted later in one cut. `LibraryV3NavBand` rows stay at Cluster (no menu, no pin, nothing
trailing — the label keeps the full lane).

**`Data/SidebarRowGeometry.cs`** (after `RowInsetRight`, `:39`; the pin block replaces `:297-301`):
```csharp
public const float TrailingSlotWidth = 28f;                                   // the ONE trailing slot
public const float TrailingLaneWidth = LeadingGap + TrailingSlotWidth + RowInsetRight;   // 6 + 28 + 8 = 42; right edge = PaneEdge+RowInsetRight = 16 from the pane edge, clear of the 12-DIP overlay scrollbar
public static SidebarTrailingContent TrailingAtRest(bool hasChevron, bool playing)
    => hasChevron ? SidebarTrailingContent.Chevron : playing ? SidebarTrailingContent.Equalizer : SidebarTrailingContent.Empty;
public static bool HoverShowsOverflow(bool hasChevron, bool showsOverflow) => showsOverflow && !hasChevron;   // a folder keeps its chevron; its menu is right-click / Menu key
public const float PinMarkSize = 10f, PinMarkSubtitleGap = 3f, PinMarkTitleGap = 4f;
public static SidebarPinMark PinMarkPlacement(bool pinned, bool subtitleVisible)
    => !pinned ? SidebarPinMark.None : subtitleVisible ? SidebarPinMark.BeforeSubtitle : SidebarPinMark.AfterTitle;
public static bool ShowsPinGlyph(bool isPinned, bool isTrack) => isPinned && !isTrack;   // unchanged
public enum SidebarTrailingContent : byte { Empty = 0, Chevron = 1, Equalizer = 2 }
public enum SidebarPinMark : byte { None = 0, BeforeSubtitle = 1, AfterTitle = 2 }
```

**`Data/SidebarSubtitleRules.cs`** (engine-free: `System` + `Wavee.Core.Sidebar` + Data types only — never `Loc`/`Icons`/`Tok`):
```csharp
public enum SidebarSubtitleKind : byte { None, Playlist, Album, Artist, Show, Folder }
public enum SidebarSubtitleDetail : byte { None, Text, SongCount, ItemCount }
public readonly record struct SidebarSubtitleShape(SidebarSubtitleKind Kind, SidebarSubtitleDetail Detail, string Text, int Count)
{   // Empty / IsEmpty / Of(kind) / Of(kind, text) (empty text ⇒ bare kind) / Songs(kind, n) / Items(kind, n)
}
public static class SidebarSubtitleRules
{
    public const string LikedRouteKey = "liked";
    public static SidebarSubtitleShape For(in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.Playlist => ShowsOwner(in e) ? Of(Playlist, e.OwnerName) : Songs(Playlist, e.TrackCount),
        SidebarEntryKind.Album    => Of(Album, e.FirstArtistName.Length > 0 ? e.FirstArtistName : e.Creator),
        SidebarEntryKind.Artist   => Of(Artist),
        SidebarEntryKind.Show     => Of(Show, e.Publisher),
        SidebarEntryKind.Folder   => Items(Folder, e.ChildCount),
        SidebarEntryKind.Track    => Of(None, e.Creator),
        SidebarEntryKind.AppRoute => e.Id == LikedRouteKey ? Of(Playlist) : Of(None, e.Creator),   // a concert's venue rides Creator
        _ => Empty,
    };
    public static bool ShowsOwner(in SidebarLibraryEntry e)   // someone else's playlist leads with its owner ("Playlist · Spotify")
        => e.Kind == SidebarEntryKind.Playlist && !e.IsOwner && e.Flavor != SidebarPlaylistFlavor.ByYou && e.OwnerName.Length > 0;
    public static SidebarSubtitleShape ForRoute(string? routeKey, int? likedSongs)   // the system row
        => routeKey == LikedRouteKey ? (likedSongs is { } n ? Songs(Playlist, n) : Of(Playlist)) : Empty;
}
```
(Verify `SidebarLibraryEntry` carries `OwnerName`/`IsOwner` as the design agent found; if only `Creator` exists, use it.)

**`Pane/SidebarPaneText.cs`**: keep today's `SubtitleOf(in e)` verbatim (Cluster) and add
`SubtitleOf(in e, SidebarRowStyle style) => style == Slot ? Format(SidebarSubtitleRules.For(in e)) : SubtitleOf(in e)`;
`RouteSubtitle(string routeKey, LibraryStore? store) => Format(SidebarSubtitleRules.ForRoute(routeKey, LikedSongsCount(store)))`
where `LikedSongsCount` mirrors `SidebarPaneSlot.CountBadge`'s read (`Stats.State == Ready && Stats.Value.Value is { } s
? s.LikedSongs : null` — reading the signals subscribes the calling slot, exactly like the badge); `Format` maps kind →
`Strings.Sidebar.V3.Kind.*`, detail → text / `Strings.Sidebar.SongCount(n)` / `Strings.Sidebar.V3.ItemCount(n)`, joined
with `" · "`. `SidebarRailFolderFlyout.cs:212` and `Menus.cs:819` keep the old overload.

**`Shared/SidebarEntityRow.cs`** at Slot (Cluster code moved verbatim into `ClusterTrailing`/the old text column):
- Text column (`:395-414`): `pinMark = PinMarkPlacement(spec.Pinned, hasSubtitle)`; title = `Body(label) with {Shrink=1,
  MinWidth=0, Ellipsis}` and, for `AfterTitle`, a `BoxEl{Direction=0, Gap=PinMarkTitleGap, Shrink=1, MinWidth=0,
  [title, PinMark()]}`; for `BeforeSubtitle`, the subtitle line is `BoxEl{Direction=0, Gap=PinMarkSubtitleGap,
  [PinMark(), Caption(sub).Secondary() with {Grow=1, Shrink=1, MinWidth=0}]}`. `PinMark() => Icon(Icons.Pin,
  PinMarkSize, Tok.AccentTextPrimary) with { Shrink = 0f }` — mounted only when pinned (Gap pitfall).
- Children (`:416-492`): `[leadingCluster][text][Trailing?][TrailingSlot(chevron, playing, animated,
  HoverShowsOverflow(hasChevron, ShowsOverflow(in spec)))]`; delete `ZStack = overflow` (`:504`) on this path; the row
  `Padding` right stays `RowInsetRight` (8) so the slot sits inside the row's own padding.
- `TrailingSlot`: a 28×28 `ZStack` box, `Shrink=0`; rest layer = chevron | `WaveeEqualizer.Of(animated,
  Tok.AccentDefault, 12f)` | nothing, in a `BoxEl{HitTestVisible=false, HoverOpacity = hoverOverflow ? 0f : NaN}` (the
  equalizer yields to the "…" on row hover; the chevron never yields); hover layer = `OverflowButton()` when
  `hoverOverflow`. `OverflowButton` = OUTER reveal box `{Opacity=0, HoverOpacity=1}` (lit by ROW hover through the
  reveal cascade) around an INNER 26-DIP circle `{HoverFill=FillSubtleTertiary, Role=Button, Cursor=Hand,
  ClickRequestsContext=true, BlocksDragArm=true, Icon(Icons.More,14,TextSecondary)}` — two boxes on purpose (one box
  carrying both would light the circle on row hover). `ClickRequestsContext` implies the tab stop, so Tab + Enter/Space
  still opens the menu keyboard-anchored (as today). Delete the Cluster-only `OverflowWidth`/overlay when Cluster is
  retired, not now.
- Spec docs to rewrite: `DisclosureChevron` (`:173-177`), `Pinned` (`:179-185`), `Trailing` (`:187-188`), `Overflow`
  (`:200-203`), the file header (`:14-30`). `HeightOf`, `ShowsOverflow`, `SelGutter`, `StandardLeading`, `TreeLeading`,
  `TreeGuides`, `TrackArt` untouched.

**`Pane/SidebarPaneSlot.cs`** (Slot only): `FolderRow` (`:510`) `Subtitle = Opts.Subtitles ? SubtitleOf(in snapshot, style) : null`
(keeps `DisclosureChevron` `:525` + `FolderTrailing` `:526`); `EntryRow`/`Card`/`GridCell` pass the style into
`SubtitleOf`; `GridCell` label (`:953-957`) gets `[PinMark][label Grow/Shrink/MinWidth 0]` when
`ShowsPinGlyph(entry.IsPinned, entry.IsTrack)`; `RouteRow` (`:575-611`) adds
```csharp
float art = SidebarPaneMetrics.ArtSize(section);
string glyph = SidebarIcons.For(item, dest.Glyph);
Element? leading = slot && section.Opts.Artwork
    ? (string.Equals(key, SidebarSubtitleRules.LikedRouteKey, StringComparison.Ordinal) ? SidebarCover.Liked(art) : SidebarCover.Glyph(glyph, art))
    : null;                                                     // SidebarCover.IsLiked is private — the id form of the same test
string? subtitle = slot && section.Opts.Subtitles ? SidebarPaneText.RouteSubtitle(key, _o.Store) : null;
var (playing, animated) = slot ? _o.RowPlayState(index) : default;
// spec: Subtitle, ArtSize = art, Leading = leading, Glyph = leading is null ? glyph : null, Playing, PlayingAnimated, Style — everything else as today
```
`SidebarCover.Liked(float)` / `Glyph(string, float, bool, ColorF?)` are public (`SidebarCover.cs:79`, `:102`); `_o.Store`
already backs `CountBadge` (`:1281`). Height stays `RowHeight(section)` (one height per section).

**`Pane/SidebarPane.cs` `RowPlayUri`** (`:1446-1455`, after the Track check): `return item is { Target: Route } &&
SidebarPinId.FromRoute(item.Key) is { } pin ? SidebarPinId.UriOf(pin) : "";` — `UriOf("liked")` is the collection uri,
`""` for every other route, so only the Liked row can light (`NowPlayingMatch.RelatesToPlaying` matches the context uri).

**Tests.** `SidebarRowGeometryTests`: `TrailingSlot_Is28_AndSitsInsideTheRowsOwnTrailingPadding` (28; lane 42;
`PaneEdge + RowInsetRight >= 12`), `TrailingAtRest_ChevronThenEqualizerThenEmpty` (4 cases),
`HoverShowsOverflow_OnlyOnAMenuRowWithoutAChevron` (4 cases), `PinMarkPlacement_LeadsTheSubtitleElseFollowsTheTitle`
(4 cases), `PinMark_IsTenDipWithAThreeAndAFourGap`, `PinMark_FollowsTheTitleAtCompact_WhateverTheSubtitleSays`
(uses `SubtitleVisible(Compact, …)`). NEW `SidebarSubtitleRulesTests` (fixture = `SidebarRowPlannerTests.Entry`'s
shape): my own playlist → song count; a Spotify playlist → owner; someone else's with unknown flavor → owner; a
collaborative one → owner; no owner name → song count; `IsOwner` beats a stale flavor; `ShowsOwner` false for
non-playlists; album → first billed artist then creator then bare kind; artist bare; show publisher-or-bare; folder
item count; track creator-or-empty; `ForRoute("liked", 123)` → songs, `("liked", null)` → bare Playlist,
`("albums"/"home"/""/null)` → empty. No existing test breaks (nothing in Wavee.Tests references `OverflowWidth`,
`SubtitleOf`, `Trailing` or `RouteRow`); comment refreshes at `SidebarRowGeometryTests.cs:25-29`, `:271`.

**Risks (from the design pass).** Drop cues (`Indicator`/`DropCueOverlay` ZStack, `:1292-1305`) are unaffected by
removing the row's own `ZStack = overflow`. V3 rows now pay 34 DIP on the right that overlay-only rows paid 0 for —
intended (one trailing edge); `MinWidth = 0` keeps titles eliding at the 180 floor. `TreeLeading`/`TreeContentX`
untouched. Recycle pools are keyed by row kind and the slot has no bound thunks, so a same-key re-render just updates
the static layers; a slot mounting under an already-hovered row is seeded faded by `TrySeedHoverFromContainer`. Hover
flags freeze during a drag (`:1193-1197`) — unchanged. Keyboard: a Tab-focused "…" is reachable but not visible
(opacity multiplies the focus ring; `WhileFocus` never fires) — same as today's overlay; an engine `FocusOpacity` twin
of `HoverOpacity` is a hand-off, not part of this work. `Card` and the rail folder flyout's non-folder rows call
`SubtitleOf` and are Cluster unless passed the style — V3's pane owner passes Slot everywhere through `_o.Config`.

### W5 — the document: Liked as a system row
Files: `Modes/LibraryV3/LibraryV3Document.cs`, `LibraryV3Session.cs`, `src/apps/Wavee.Tests/LibraryV3DocumentTests.cs`,
`SidebarShortcutsSectionTests.cs`, `SidebarRailPlannerTests.cs` (one case).
- Delete `ChromeCarriesDestinations`; rename `LikedId`/`LikedItemId` → `SystemId = "v3.system"`,
  `SystemLikedItemId = "v3.system.liked"`. Section (placed BEFORE `PinsId`), emitted when `state.LikedVisible`
  (unchanged rule: `!LikedPinned && !Searching && !Drilled && Filter is All or Playlists`) and
  `!SidebarShortcutsSection.ContainsRoute(topBar, LikedRouteKey)` (the existing dedupe obligation):
  ```csharp
  sections.Add(new SidebarSectionSpec(SystemId, SidebarSectionKind.StaticLinks, Title: null, TitleLocKey: null,
      Hidden: false, Collapsed: false,
      Display: new SidebarDisplayOptions(Density: SidebarDensity.Cozy, Presentation: SidebarPresentation.List,
          Artwork: true, Subtitles: true, CountBadges: false, CollapsedByDefault: false, ShowInRail: true),
      Items: [new SidebarItemSpec(SystemLikedItemId, SidebarItemTarget.Route, LikedRouteKey, IconOverride: "Heart")]));
  ```
  When the pin band is NOT visible, follow it with `new SidebarSectionSpec("v3.rule.system", SidebarSectionKind.Divider, …)`
  so the system row never runs straight into the library (with pins, `PinEnd` closes the whole band).
  In grid views the system section stays a List section (one row; `PinEnd` is list-only anyway).
- `LibraryV3DocState`: no new field needed for Liked; `LikedPinned` already reads `IsPinned("liked")`.
- Tests: section order/ids per state (idle, Playlists, Albums, searching, drilled, liked pinned, topBar carrying
  liked, pins present vs absent → divider), display options, `SidebarShortcutsSectionTests.LibraryV3_NoLongerEmitsItsOwnLikedRow…`
  rewritten to the system-section rule, `SidebarRailPlannerTests`: the system section contributes one tile.

### W6 — docs, skill, changelog
Files: `CHANGELOG.md` (`## [0.2.9] - unreleased` → `### Changed`, one bolded-lead bullet ending ` (#n)`),
`docs/guide/sidebar-extension-platform.md` (`:48` table row, `:70-105` the V3 exception paragraphs → the system
section + `RailHead` for TopBar only), `.claude/skills/wavee-sidebar/{architecture.md (:207,:211,:230,:689),
where-to-change-what.md (:85 nav-band row, the search-host row, + rows for the lens row / header ladder / RowStyle),
pitfalls.md (:176 glyph fixed, :370-371), testing.md (inventory: 3 new test classes; the two removed OpenWidth tests)}`,
`SKILL.md` (three-mode table: V3 = chrome nav band + header + chips + lens row). Retire the word-rail paragraphs
and `library-v3-destinations-*.html` are left in place as history (no deletion).

### W7 — retire Local files from every navigation surface
Files: `Features/Shell/ShellNav.cs:59`, `ShellRoutes.cs:35`, `ContentHost.cs:156`, `Features/Detail/DetailPage.cs:78`,
`Features/Shell/HistoryPage.cs:33`, `Features/Diagnostics/WaveeNavProbe.cs:48`, `Backend/Persistence/RecentSurfaceRoute.cs`
(comment only), `Data/SidebarPinId.cs:48` (`PinnableRoutes` minus "local"), `SidebarPinStore.LoadFrom` (skip an
AppRoute id that `SidebarPinId.IsPinnableRoute` no longer accepts — a deliberate retirement prune),
`Pane/SidebarBuiltInDocuments.cs:97`, `Wavee.Core/Sidebar/SidebarTemplates.cs:76,:97`,
`Wavee.Core/Sidebar/SidebarShortcutsSection.cs:96` (four destinations), `Wavee.Core/Sources/LocalSource.cs`
(delete the synthetic "Local Files" playlist `GetPlaylistAsync` arm for `wavee:local:all`; keep the source as the
`local:`/`wavee:local:*` uri owner), `Persistence/SidebarLayoutMigrations.cs` + `SidebarLayoutStore.CurrentVersion`
2 → 3 with `MigrateV2ToV3`: drop every `Route` item whose key is `"local"` from every section (children included)
and from `topBar` (in place, extension-data carry preserved — the ladder's documented shape).
Tests to update: `ShellNavDestTests:143` (arm removed → default), `DrillTrailTests:26`, `LibrarySearchColdTests:80`,
`LibraryV3DocumentTests:206`, `SidebarTemplateTests:91,:122`, `SidebarBuiltInDocumentTests:115`,
`SidebarLayoutReducerTests:1448` (use "history" for the sixth key), `SidebarCustomizerLayoutTests` (Destinations
set), `SidebarLayoutV2MigrationTests` (`VersionAboveTwo` → above three; add the v2→v3 prune cases),
`SidebarPinStoreTests` (a persisted "local" pin is dropped on load). The customizer's Destinations group derives
from `PinnableRoutes`, so it updates itself.

## Verification (orchestrator)
1. `dotnet build Wavee.slnx` and `dotnet build Wavee.slnx -c Release` (TreatWarningsAsErrors; close the running app
   first — it locks the apphost).
2. `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj --filter "FullyQualifiedName~Sidebar|FullyQualifiedName~Rootlist|FullyQualifiedName~PlayLog|FullyQualifiedName~ShellNav|FullyQualifiedName~WaveeExtension|FullyQualifiedName~LibraryV3"`
   with a hard timeout (a full unfiltered run hangs); then the whole suite once at the end
   (`dotnet run --project src/apps/Wavee.Tests -p:WaveeSkipPrivateSources=true --no-build`). Baseline: 8
   pre-existing unrelated failures on main + the 6 audio-cache failures of #95 — do not attribute them to this work.
3. Launch `dotnet run --project src/apps/Wavee -- --fake` and, in an authenticated session, the probes
   `WAVEE_SIDEBAR_V3_SHOT=1` (8 PNGs across views/filters) and `WAVEE_SIDEBAR_MODE_SHOT=1` (expanded + rail per
   design). Eyeball checklist: title intact at 180/240/320/460 with and without search open; "+" folds below 240;
   no word rail; Home at 32; chips order; lens row text, link and count in All/Albums/Playlists·By you/search/
   drilled (breadcrumb replaces it)/empty-by-filter; Liked Songs system row with the dynamic cover, "Playlist ·
   N songs", selection pill on the Liked page, pin verb moves it into the pin band and back; one trailing slot
   (rest empty, hover "…", equalizer on the playing row, chevron on folders); pin mark leading pinned subtitles
   and grid labels; Classic and Curated rows unchanged side by side; rail order expand · + · Home · rule · Liked ·
   pins · library; no `[key]` text anywhere; no "Local files" anywhere (Classic, templates, customizer palette,
   pin picker, deep link `local` refused).
4. Release gate rehearsal: `powershell -File ops/release/wavee-release.ps1 -DryRun -SkipTests` (the `issue refs`
   check must see ` (#n)` in the CHANGELOG bullet and `Fixes #n` in the commit body).

## Accepted limits
- Liked Songs when it IS a real pin renders through the binder's unlisted-pin resolver (`ResolveUnlistedPin`) with
  `SubtitleOf(AppRoute "liked", Slot)` → "Playlist" (no count); the count shows only on the system row.
- A Tab-focused "…" is reachable (Enter/Space opens the menu) but invisible, exactly as today's overlay; an engine
  focus-reveal is a separate hand-off.
- `nl`/`ko-KR` show the new lens strings in English until translated (legal per-key fallback).
