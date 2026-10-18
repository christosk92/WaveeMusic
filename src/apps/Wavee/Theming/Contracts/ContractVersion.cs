// ── Theming/Contracts/ContractVersion.cs — the version of the contract between core and a theme ───────────────────────
//
// A theme is built against ONE contract version (`ThemeManifest.ContractMajor/ContractMinor`). The contract is the set of
// published view keys and style keys with their props records, the verbs and state enums those props carry, and the
// role vocabulary (`Wavee.Theming.Roles.WaveeStyleRoles`). Version 1.0 is the key list of themes plan section 6.0.
//
// 1.1 (tuned Classic CT4): `LyricsState.Offline` and the optional `LyricsLinesStyle.Offline` picture; `PlayerBarSlots.VideoSplit`
// (the video split button's own level bit; it used to ride `Queue`).
//
// 1.2 (tuned Classic CT1c): the omnibar model's `SuggestionsOpen`, `FieldExpanded`, `FieldSettled` and `ReportFieldWidth` (the field
// eases to its focused width and the title bar re-pushes its regions once it settles). Members nothing used any more were removed
// before any external theme existed: `ShellTitleBarProps.ToggleTheme`, `SearchSuggestionsProps.AllowNarrow`, `ShellSearchProps.Avail`,
// and `ShellSearchProps.FieldWidth` takes no measurement (`Func<float>`).
//
// 1.3 (tuned Classic CT3a): `SidebarRowShape.EntityText` (the covers-off text row), the optional `NavPanePolicy.ShowCovers`, and
// the nav row host's `UnpinAction` (the row's Unpin button).
//
// 1.4 (tuned Classic CT5c): the track row lanes lose the film lane — `Track.ColumnSet.Video`,
// `TrailingColumns.Video` and `CellKey.Video` are gone (a video is the glyph after the title in both row styles) — and a row
// template reads `Track.HasVersions()` for the versions chevron, `Track.DurationLabel` / `Track.WithheldReason` for the
// duration lane. `GridOptions.HeartRevealOnHover` / `EagerRowOptions.HeartRevealOnHover` are removed: every heart lane reveals.
// Removed before any external theme existed, as 1.2 did.
//
// 1.5 (tuned Classic CT5b): the optional `DetailRailProps.Shown` (the list's shown / total count for the rail's "(filtered)" line) and
// `DetailFrameProps.Sheet` on the two-column arm too (the insights sheet over the list on every facts page). The list bar's command kit
// (`ITrackCommandKit`, internal) takes `Bar(left, view, search, tail)` and lost `Separator`, `Trail`, `SortCaret`, `DensityLabel` and
// `DensityPanel` (Row size, Select and the sort caret left the bar); `TrackRowSkin.PadX` went with the Classic row divider's inset (the
// header rule, the plates and the dividers share one left edge, V10). No external theme exists yet.
//
// 1.6 (tuned Classic CT6b): the optional `CoverVeilProps.Hold` (where a horizontal veil's hold ends: the artist hero's copy edge).
// 1.7 (tuned Classic CT9): the optional `ThemePolicies.Artwork` (`ArtworkColorPolicy`: whether the page accent and the now-playing colours
// follow the artwork) and the optional `WashRoles.Rich` (`WashStrength`: a stronger wash level's hero, shelf, tone-plane and tint pairs). The
// wash reads take the level (`R.Wash.HeroAlphaAt(rich)` and its siblings, `ShellTintFromScheme(scheme, rich)`); the level-less reads
// `HeroAlpha`, `ShelfAlpha`, `PageTonePlaneAlpha`, `ShellTintAlpha` and `ShellTintFromScheme(scheme)` are removed before any external theme existed.
// 1.8 (tuned Classic CT8): the `Overlay.OfflineVacancy` key and its `OfflineVacancyProps` (the offline voice of a page with nothing
// saved: a page-scale error vacancy speaks it while the session is offline). The offline lane (`ContentStateKind.OfflineLane`) now
// stands in the flow above the page instead of floating over it.
// 1.9 (tuned Classic CT6a): a page with no rail (`Detail.Config.Rail` false, Liked Songs, D16) — the optional `DetailFrameProps.Head`
// (`DetailHeadProps`: the title and meta over the list), `LikedChipBarProps.Inline` (the chips ride the page bar, D8) and
// `TrackRowSkin.Quiet` (a quiet list's full-width plate). The command kit (internal) gains `PageLead` and `Bar`'s optional `middle`; the
// chrome record gains its `GroupHeader` (the date groups' head).
// 1.10 (tuned Classic X2 follow-up): the optional `LibraryAlbumPaneProps.Columns` (the lanes the library pane's embedded table will lay out for
// the row style; its counted shimmer and header shim stand on them). `Track.EmbeddedColumns` / `EmbeddedTracks` take the style.
// 1.11 (P3t TC9, overlays at every tier): the optional `ThemePolicies.Overlays` (`OverlayPolicy`: per width band the dialog card's fit, the toast lane's dock, the
// vacancy's type, the standalone selection bar and whether context menus open as a bottom sheet) and the `Overlay.ContextSheet` key with its `ContextSheetProps`.
//
// 1.12 (Classic frame tiers TC1): `ChromeRowState.TabCount` / `.TabMaxWidth` (the Compact title bar's tab-count button and the tier's
// tab cap), the optional `ChromePolicy.Compact` / `.Huge` budgets with `ChromeBudget.TabsAsCount`, and `ctx.TierReader()` (the window
// tiers for a bound channel of a template that renders once).
//
// 1.13 (Classic stage tiers TC3): `StagePolicy.Aspect` (`AspectThresholds`) is three engine `TierLadder`s (width, height, ratio) instead of
// seven floats, and `StagePolicy` gains the optional per-window-tier `StageTierMetrics` (`Metrics`, `Compact`, `Small`, `Huge`: the transport
// card's height and gutter, the lyrics pane's type range) with `MetricsFor`. The float members are removed before any external theme existed.
// 1.14 (Classic detail tiers TC5): the optional `ThemePolicies.DetailTiers` (`DetailTierPolicy`: per window band the track table's Compact
// form and lane caps, the docked insights pane, the rail's resting width, the hero action row, and the rail title's cap per height rung), the
// detail sub-ladder ids `DetailLadders` a structure's `TierPolicy.Ladders` names, the optional `Track.ColumnSet.Compact` (the Compact row
// form), the optional `DetailInsightsProps.Docked` (the insights pane as a column beside the list), the optional `ContextSheetProps.Content` (a card body in the sheet: the filter card at Compact), `Track.RowMetrics.ShowDuration` and the internal kit's `ITrackCommandKit.CommandEdge`.
// The page-mode and table ladders' numbers left the core (`Detail.Breakpoints.ModeFor` / `TierFor` and their nominal forms are gone).
// 1.15 (Classic player bar and panels per tier, TC2): `PanelPlacement`, `PanelView.Placement` / `.UnmountWhenClosed`, the optional `PanelProps.ShowMode`, the
// frame's optional second docked pane (`ShellFrameProps.PanelPlacement`, `SecondPanel`, `SecondPanelOpen`, `SecondPanelWidth`,
// `SecondGapWidth`, `SecondReservedWidth`), the policies' per-window-tier rules (`PanelPolicy.Tiers` / `PanelTierRule`,
// `PlayerBarPolicy.Windows` / `PlayerBarWindowRule`, `ChromeBudget.TrailingInDrawer`), the mini player bar's `PlayerBarProps.ProgressLine`
// / `OpenNowPlaying` and `SeekRailProps.Hairline`, `ChromeRowState.TabsHidden`, the drawer's account row (`NarrowDrawerProps.ShowAccount`,
// `.Account`, `DrawerAccountProps`) and `ProfileChipProps.Drawer`. `PanelPolicy.InlineMin` (the old fit test's content floor) is removed:
// docking follows the window tier. Removed before any external theme existed, as 1.2 did.
// 1.16 (Classic collection tiers TC7): `LibraryPageProps.SetPageWidth` and `.Kinds` (`LibraryKindChip`) replace `IsCollapsed` / `SetCollapsed`: the structure's
// layout ladder (`LibraryLadders.Layout`, a named sub-ladder of its `TierPolicy`) over the page's measured width decides the one-column drill-in, resolved by the
// core host through `UseTier`. The two members are removed before any external theme existed, as 1.2 did.
// 1.17 (Classic screens TC8): the optional `DialogFit.FormWidth` and `DialogFit.CommandHeight` (a structure's card width and 40-DIP command row for the report and crash
// form dialogs), `DialogFit.FormCardWidthFor` / `FormBodyWidth`, and `OverlayFit.ShowForm`, the door both form dialogs open through.
// 1.18 (Classic artist and people pages per tier, TC6): the optional `ThemePolicies.ArtistTiers` (`ArtistTierPolicy`: the artist and profile heroes' metrics per
// sub-tier and window tier, the Top-tracks chart's columns, the profile list's narrowest card), the sub-ladder ids `ArtistLadders` (the hero, the library artist
// reader, the profile list) a structure's `TierPolicy.Ladders` names, the optional `ArtistHeroMetrics.PhotoHeight`, `Controls.FollowToggle.Size` and the sized
// `Controls.PlayButton` / `PrimaryButton`. The hero ladders and heights left the core (`ArtistHeroLayout.TierFor` / `For` and their constants,
// `ProfileHeroLayout.For` / `Make`, `ArtistSections.TopBandWide`, `ReaderShape.Narrow` / `Medium` / `MediumBelow`, `ProfileGridFit.ShowsStrip` / `MinCell`);
// removed before any external theme existed, as 1.2 did.
// 1.19 (compact control sizes): the engine's stock controls reach the 40-DIP target, so the structure sets their own knobs: `DialogFit.CommandSize`,
// `.CommandMinWidth` and `.UniformCommands` (every dialog's command buttons per band, through the engine's `ContentDialog.CommandSize` /
// `CommandMinWidth` / `UniformCommandWidths`) replace `DialogFit.CommandHeight` and the hand-built form command row; `OverlayLayout.MenuSize` (the anchored menus' rows); the optional `ToastDock.Lane` (the
// engine's `ToastLaneStyle`: the lane's side margin, card widths and action size, provided as `Toast.Lane`). `DialogFit.CommandHeight` is removed
// before any external theme existed, as 1.2 did.
// 1.20 (the Compact overlays fix): the optional `PanelProps.Transport` (`PanelTransportProps`, `PanelTransportFacts`: the transport the now-playing sheet
// carries, since the sheet covers the player bar).
// 1.21 (the Huge detail and library layouts): the optional `DetailFrameProps.Aside` (`DetailAsideProps`: a page's right aside column at a tier whose
// record docks one), the core slot `Detail.FrameSlots.Aside` and the `Detail.AsideDocked` context, the `Album.Aside` key (`AlbumAsideProps`), the optional
// `DetailTierLayout.AsideDocked` / `.InsightsRestOpen` and `DetailTierPolicy.HugeWide` / `.HugeWideRung` (the Huge 4K record), `AboutLayout.Aside`,
// the optional `NavPanePolicy.HugeWidth` (the sidebar's width at Huge while the user never resized it), `ThemePolicies.LibraryTiers` with
// `LibraryTierPolicy` (the navigator's default width per tier while the user never dragged it), and `ThemeState.IsCustomized`.
// 1.22 (the remaining artist and stage items): the optional `ArtistTierPolicy.ChartRows` (the chart's rows per column per tier), `DiscographyMetrics.Drawer`
// (`Album.DrawerShape`: the album drawer's columns, pitch and head per tier) with `Album.DrawerVerdict.Pitch`; the stage's `StageTierMetrics.GalleryToFoot` /
// `.UpNextW` / `.ArtBarsH` / `.IdentityCard` / `.GalleryOnDemand`, `StagePolicy.HugeWide` / `.HugeWideRung` / `.IsHugeWide`, `StageLayers.UpNext`,
// `QueueStagePaneProps.Aside` and `PanelProps.VideoTitle` (`PanelVideoTitleProps`, `PanelVideoTitleFacts`). `ArtistChartSkeletonArgs.For` takes the tier's
// columns and rows, and `DrawerVerdict` gains its positional `Pitch`; changed before any external theme existed, as 1.2 did.
// 1.23 (the facts a view is drawn in): the style builders the core's components call outside any view context take the tiers of the facts
// that component is drawn in (its root's `ThemeFactsContext`, never the process's last window): `DetailHeroStyle.Skeleton` gains a `TierFacts`
// argument and `DiscographyMetricsTemplate` an `in TierFacts` one (changed before any external theme existed, as 1.2 did), and
// `ITrackCommandKit.In(in TierFacts)` (a default member: a kit with no tier rules is itself) hands the table a kit drawn in its tiers.
// `TierContext.DrawnIn` is the component-side reader.
// 1.24 (the remaining board items on the detail pages, Home, Search, Settings, the library and the now-playing sheet): the optional `PanelTransportProps.Source`
// (`PanelSourceFacts`: the sheet's "Playing from" header), `.Device` (`PanelDeviceFacts`) and `.ToggleDevicePicker` (its Devices door); the unavailable-songs bar
// (`TrackUnavailableBarArgs`, `TrackUnavailableBarTemplate`, the optional `TrackChromeTemplates.UnavailableBar` / `.UnavailableBarExtent`, over the core's
// `Track.UnavailableTally`, `FilterModel.HiddenByPlayable` / `.OffersHide`: "Hide them" turns on `FilterFlags.PlayableOnly`); the optional `DetailTableTier.ListIndex`
// with `Track.ColumnSet.IndexHidden` / `Track.Lanes.IndexHidden` (a row form with no "#" lane), `PlaylistRecsProps.Lead` (`PlaylistRecsLead`: an empty
// playlist's search lead), `VerticalItemRole.Recommendations` and the optional `recommendations` argument of `Detail.VerticalLayout.ItemCount` / `RowSlots` /
// `ItemRole` (the vertical arm's Recommended songs), the internal kit's `ITrackCommandKit.SortCommand`; the optional `FacetRowProps.ToggleFriends`,
// `SearchBodyModel.ArtistRail` and `SearchPageProps.SelectArtists`; and `LibraryTierPolicy.SmallNavWidth`, `.SmallReaderNavWidth`, `.SmallCardCellMin` and
// `CardCellMinFor` (the Small navigator and its card floor; `NavWidthFor` answers at Small too). `LibraryShowPaneProps.Primary` takes the button's `ControlSize`
// (the 40 command row at Compact): a changed signature, made before any external theme existed, as 1.2 did.
// 1.25 (the last Classic gaps): the optional `PanelTransportProps.DevicePickerRequest` (a request, such as a toast's "Choose device", that the
// now-playing sheet answers by opening its Devices door's picker), and the library grid's per-tier gaps (`LibraryGridGaps`, `LibraryTierPolicy.CompactGridGaps` /
// `.SmallGridGaps` / `.StandardGridGaps` / `.HugeGridGaps` and `GridGapsFor`; null keeps the core's 8 / 8).
// 1.26 (P5 round A, the seams Zune needs): the role vocabulary: twelve `TypeRoles` members (Panorama, PanoramaCompact, Hero, Pivot, SubPivot,
// Numeral, NumeralLarge, NowPlayingDisplay, Wordmark, LyricsLine, Heading, QuickplayHead) with `PanoramaSize`, `WithPanorama` and `WithBigNumerals`,
// `ShapeRole` / `ShapeRoles` with `WaveeStyleRoles.Shape` and `R.Shape` (a non-colour role), and `StyleMetrics.DockedPane` / `.DockedPane4K` /
// `.PaneItem` / `.CardGap` (positional constructors changed before any external theme existed, as 1.2 did); page chrome row 2: `PageChrome.Views` /
// `.Actions` / `.Backdrop` / `.CurrentView`, `PageView`, `PageAction`, `PageActionKind`, `PageBackdrop`, `PageChromeRow` and the `PageChromeSink`
// context; the band: `NavPaneProps.Band`, `BandProps`, `BandDestination`, `BandPin`, `PageScroll` and `PageScrollDirection`.
// 1.27 (P5 round B): `Track.RowState.OnThisPc` (optional, false by default: the track's audio is fully cached here with its key saved; a
// display fact, no offline playback), which every row contract carrying the row state passes through; Browse's bands answer row 2.
// 1.28 (P5 round D): the `Home.Quickplay` key (`HomeQuickplayProps`: the band's pins, history and new releases at the top of Home;
// Classic registers Nothing for it).
// 1.29 (P5 rounds E-F): docked panes per tier: `PanelArrangement`, `DockedPane`, `PaneSide`, `PanelModeSet`, `PanelTierRule.Panes` /
// `.Arrangement` / `.DocksThree` and `MaxDocked` 1-3, `PanelPolicy.ArrangementAt` / `.PaneAt`, `PanelView.Beside` / `.Linked`,
// `ShellFrameProps.DockedPanes` with `DockedPaneProps` (`RailWiring.QueuePane` removed: the core's hard-wired queue pane becomes the
// tier's arrangement; Classic's rule docks exactly as before); `ChromePolicy.Small` and `OverlayPolicy.Small` (null keeps Standard's).
// 1.30 (P5 round G): `PageChromeRow.Title` (a detail page's entity name folds into `PageChrome.Title`), `ShellTitleBarProps.OnSearchRoute`
// (the Search route is on screen), `ArtistTierPolicy.BandClearance` (where a pivot click parks a section; Classic keeps 56).
// 1.31 (Zune fidelity): `ArtistTierPolicy.ChartRowShape` / `.ChartBalanced` with `ArtistChartRowShape`, `ArtistChartStyle.HeaderWith`
// (`ArtistChartHeaderArgs`), the chart row / cell / skeleton args' optional row-shape and column fields (defaults keep Classic's).
// 1.32 (Zune fidelity): `DetailTableTier.HoverActions` (no heart or "..." lane: both stand in the time lane on row hover, `ColumnSet.HoverActions`) and
// `DetailTableTier.OneLineTrim` (an album's one-line rows stand this far under the density's row).
// 1.33 (Zune fidelity): `LibraryTierPolicy.GroupAlbumsByArtist`, `LibraryPageProps.Groups` (`LibraryGroupsProps`, `LibraryGroupSet`,
// `LibraryGroupRow`) and `LibraryPageProps.Count` (the library's albums grouped by artist; Classic publishes no groups).
// 1.34 (Zune fidelity): `IArtistReaderHost.ReadReleases` / `.AlbumPane` with `ArtistReaderRelease`, `LibraryTierPolicy.GroupPaneFrom`,
// `LibraryGroupsProps.Play` / `.Shuffle` / `.Open`, `LibraryGroupSet.SelectedArt` / `.SelectedSongs`.
// 1.35 (Zune fidelity): `SettingsPageProps.OpenSection` / `.OpenTab` / `.CloseSection` (the settings route carries the open section).
//
// RULES (themes plan 1.2, 5.3). A MINOR bump only ADDS: a new key, a new optional member of a props record, a new role.
// A theme built against 1.N keeps working on any core that is 1.M with M >= N. A MAJOR bump may remove or change a
// member and is a deliberate, owner-visible event. `ThemeCatalog` refuses to install a composition whose major differs
// from `Major` or whose minor is newer than `Minor` (an older core cannot honour a newer theme's keys).

namespace Wavee.Theming.Contracts;

/// <summary>The contract version this core implements.</summary>
public static class ContractVersion
{
    /// <summary>Breaking changes to the published keys, props records or verbs.</summary>
    public const int Major = 1;

    /// <summary>Additive changes to the published keys, props records or verbs.</summary>
    public const int Minor = 35;

    /// <summary>True when a theme built against <paramref name="major"/>.<paramref name="minor"/> can run on this core.</summary>
    public static bool IsCompatible(int major, int minor) => major == Major && minor >= 0 && minor <= Minor;

    /// <summary>The version as text (<c>1.0</c>), for logs and the feedback line.</summary>
    public static string Text => Major + "." + Minor;
}
