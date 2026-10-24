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
// 1.36 (Zune fidelity): `LibraryTierPolicy.PodcastsEpisodesFirst` / `.PlaylistsMasterDetail`, `LibraryPageProps.Episodes` (`LibraryEpisodesProps`).
// 1.37 (Zune fidelity): `ConcertDetailView.ShareUrl` / `.PlayLineup` / `.OnSale` (the event's share, play-the-lineup and on-sale verbs).
// 1.38 (Zune fidelity): the stage context's `ToggleSecondLine` / `HasSecondLine` / `SecondLineOn` / `ResyncLyrics` (the lyrics bar's
// translate and resync words).
// 1.39 (Zune fidelity): `ViewKeys.StateVacancy` with `VacancyProps` (the shared empty / error / offline picture; Classic registers
// `Controls.VacancyDefault`, today's look).
// 1.40 (Zune fidelity): `LibrarySavedEpisodesProps.Feed` / `.PlayEpisode` / `.OpenEpisode` with `LibraryEpisodesFeed` and
// `LibraryEpisodeRow` (the podcasts library's own episodes feed; Classic keeps its table).
// 1.41 (Zune fidelity): `VideoFullscreenProps.Artists` / `.SwitchToAudio` (the fullscreen video bar's title line and audio switch).
// 1.42 (Zune fidelity): `DialogFit.TitleSize` / `.CloseMark` (a theme's dialog title size and close mark).
// 1.43 (Zune fidelity): `TimeLabelProps.Widest` (the widest the label prints for the track: a theme reserves it), `QueueStagePaneProps.Player` (the shuffle and repeat verbs)
// and `QueueRowText.Album` (the Up next page's album column).
// 1.44 (Zune fidelity): `TrackChromeTemplates.HidesChromeOnEmptyLead` and `Detail.ListCount.EmptyLead` (an empty list showing its
// recommendations lead drops its heading, toolbar and column header where the theme asks).
// 1.45 (Zune fidelity): `PipBounds.OverlaysContent` (the floating video card floats over the page instead of reserving its height).
// 1.46 (Zune fidelity): `QueueProps.History` with `QueueHistoryRow` and `IQueueVerbs.PlayHistory` (the tracks played before the current one, newest first: the Up next
// page's "played before this"), `ConcertDetailView.Interested` / `.ToggleInterested` (the concert page's local "interested" mark).
// 1.47 (Zune fidelity round 2): `TrackRowSkin.Dimmed` (the row's dim state, so a skin dims the check lane with the row).
// 1.48 (Settings sections): `SettingsPageProps.Area` / `.AreaCell` / `.OpenArea` (the page's twelve sections, `Settings.Area`, each a list of document sections),
// 1.49 (Zune panel wash): `PanelProps.CoverUrl` (the playing track's cover url, so a theme tints the panel's surface from it).
// 1.50 (Zune cover wash): `PageBackdrop.TallWash` (a cover page asks for the tall single wash the boards draw behind its hero; Home and the rest keep the band-height one).
// 1.51 (P6 wave 1): `ControlRole`/`ControlRoleTable`/`CommandButtonStyle.LabelMode` and `WaveeStyleRoles.Controls` (WR); `ShellCapabilities.HiddenTabStrip`/`CyclesTabs`, `SettingsGrouping`/`ThemePolicies.Settings` and `SettingsPageProps.Grouping/Group/OpenGroup`, `LyricsLinesStyle.StageDepthBlur` and `BlurPolicy.ResolveFor(setting, onStage, styleBlur)`, `StageTierMetrics.Layouts`, `DockedPane.Tabs` and `PanelProps.ShowPaneTab` (WK).
// 1.52 (P6 wave 2): `NavPanePolicy.WideMode` and `NavRowKit.Footer` (W2b); `DetailTableTier.HeartTrailing`/`KeepDuration`/`VersionsLane`/`DurationWidth` and `Track.ColumnSet.DurationKept`/`DurationWidth` (W3).
// 1.53 (P6 wave 3 + fixes): `HomePageProps.Greeting` (W5); `DetailTierLayout.PageInsetX`/`BandHeight`/`AsideInHero`/`FansInBand`/`BandOf`, `AlbumAsideProps.Artists`/`AlbumTitle`, `ArtistTierPolicy.SplitDetailTabs` (W6a); `DialogFit.ReportWidth`/`PlateRadius` and the `report` parameter on `FormCardWidthFor`/`FormBodyWidth`/`OverlayFit.ShowForm` (W8a); `PanelTierRule.DockContentFloor`/`DockHold`/`PlacementAt` (W4a); `SurfaceShape.Flush`, `SurfaceGeometry.ShelfHeight(.., flush)`/`PlateInsetX`, `SurfaceParts.InnerOf(.., flush)`, `HomeCards.ShelfCell(.., flush)`; `ITrackCommandKit.FindCommand`/`SelectCommand`, `NavRowKit.AdoptiveRoute` and `SidebarPillAnchor.Adopted`, `Controls.ArtistAboutCard(.., follow:)`; `LyricsHeaderFacts.TrackTitle`/`SyncLine`, `LyricsLinesStyle.Rail`/`LyricsRailForm`, `ILyricsHost.OnRail`; `LyricsLinesStyle.StageRows`/`LyricsStageRows`; `PanelPolicy.DockGap`/`PaneGap` (the gaps the frame lays beside the docked panes, which the content-width estimate counts).
// `Settings.PartsOf` / `AreaSlugs` / `AreaFromSlug` / `HomeArea` / `FirstTab`, the document tabs `Tab.Account` / `.Devices` / `.Keyboard` / `.Feedback` (with their rows and
// `Settings.Shortcuts`), and the document section "Playback" of the Playback tab. `SettingsPageProps.Tab` / `.TabCell` / `.OpenTab` stay as superseded adapters over the area.
// 1.54 (audit C0): `VideoPipProps.ToDocked` / `ToSeparateWindow` (the mini player's own placement buttons, W-Video: "Dock in rail" where the rail fits, else "Play in a separate window"); `ShowPrimaryArgs.Empty` (a show with no episodes draws its play verbs disabled).
// 1.55 (audit B6): `ShowReaderStyle.NarrowToolbarGlyph` (the toolbar's glyph edge under the reader's narrow arm, summed by `ShowToolbarLayout.For`'s new `narrowGlyph`: Classic's 40 Compact targets).
// 1.56 (audit B4): `VideoFullscreenProps.Transport` (the fullscreen surface owns the transport: the theme draws the on-media strip in its chrome).
// 1.57 (audit B3): `VideoPopOutProps.Strip` (the pop-out's on-media strip, stacked over its poster); `StageTransportProps.Next` and
// `StageNextFacts` (the track up next, the boards' "next · …"); `StageIdentityProps.Marquee` (a one-line stage title scrolls under the
// marquee preference).
// 1.59 (audit B7): `PageChromeRow.ViewsPending` / `PageChrome.ViewsPending` (a page whose views are still loading: a band keeps row 2's place) and
// `PageAction.Reserve` (a word action's other word: a theme holds the wider of the two, so follow / following never changes the action's width);
// `ConcertDetailProps.Failed` / `.Retry` (a concert row whose ask failed is the error vacancy with Retry, never the skeleton forever);
// `LibraryPageProps.CountKnown` and `LibraryGroupSet.Known` (no count beside a loading library); `NotificationsProps.Loading` (a feed still loading
// draws its skeleton, not "Loading...").
// 1.62 (audit B5a): `BandProps.Section` (the destination a detail page lives under, from its trail: the band lights "library" over an album);
// `PageCrumb.Word` (a crumb that is the core's word, not a name: a theme may re-case it); `StyleKeys.LibraryPlaylistHead` with `LibraryPlaylistHeadStyle` /
// `LibraryPlaylistHeadArgs` (the library playlists reader's head as a theme draws it); `AlbumTrailStackProps.Name` (the name a stack's title carries);
// `BrowseCategoryProps.Color` / `.Parent` (a category page's own colour and the page it was opened from).
// 1.63 (audit B3bc): `SurfaceStackBodyTemplate` takes the shelf card's live width (`IReadSignal<float>? liveWidth`): a shelf body binds its
// sizes to it, so a card width change re-lays the card out instead of re-rendering it; the stack's label block is width-free.
// `StageArtistNameProps.Pane` (the Artist layout's "about the artist" block reads the Artist pane's facts and verbs).
// 1.66 (audit B11): `PlayerCredits.Line(separator)` (the credits line as plain words: what the bar's credits marquee restarts on and shows as its
// tooltip). `StageIdentityProps.Marquee` stays as it is: the marquee control behind it now rests at the start and passes a bounded number of times.
// 1.68 (audit B5b): `DevicePickerEntry.Word` (the picker's "System default" is the core's word, not a device's name: a theme may re-case it);
// `DevicePickerEntry.IsChecked` is set on a this-computer output too (the one playing), where it was always false; `StageDeviceFacts.Word` (the same
// word on the stage's device button).
// 1.69 (audit B9): the one-tree detail page. `DetailForm` (Hero | Columns) and `DetailColumnsProps` (the columns form's rail, head, aside and sheet), carried
// by `DetailFrameProps.Form` / `.Columns` and `DetailHeroProps.Parts.Form` / `.Columns` (the hero draws both forms; the form is a live prop, never a key);
// `DetailHeroStyle.ListInsets` (`DetailInsetQuery` -> `DetailListInsets`: the width the hero's side columns take beside the rows) and `.InsetMotion`; the
// hero style's `Skeleton` takes the form and the columns (a theme built against an older minor supplies the hero form only and keeps the two-arm frame).
// A show's or an episode's vertical arm also carries `DetailFrameProps.TwoColumn` (the rail's props) and `DetailRailProps.Stacked`, so a structure can draw
// one identity at every width (the fixed header and the column as one arrangement); the `ShowHeader` props stay for one that does not.
// 1.72 (audit B7 r2): `DetailHeroStyle.Skeleton` is optional. Null: the hero shimmers its own identity in place while the page's header is unread, so the
// hero list mounts at once (no loading band) and the header's landing eases the hero's height, the rows riding it. A style that hands a band keeps it.
// 1.73 (audit B8): `DevicePickerEntry.Chosen` (the this-computer output the user chose, also while playback is elsewhere: Z-Overlays draws it as a
// filled dim dot and the unused outputs as hollow rings); `StyleMetrics.RowPadXFloor` (the least gutter a track row pads to at any relief tier:
// Zune's Compact rows keep the board's 16).
// 1.75 (audit Z-OWNER): `ShowReaderStyle.YearsInMargin` (the show reader's year strip stands in the frame's outer margin: the reader reserves no strip
// column and, where it ends on the column edge, pads no trailing gutter, so its rows run to that edge; `ShowReaderRules.HeadColumns` and `ShowToolbarLayout.For`
// take the flag); `ShowToolbarArgs.Trailing` (whether the toolbar pads that gutter); `Show.ReaderOwnsEdge` (the reader's trailing edge is the window's or its
// pane's, the vertical arm's and a Compact drill-in's: a margin strip then stands inside the reader's own gutter).
// 1.76 (audit B8 r2): `DetailTableTier.PageMenu` (`DetailPageMenu`: where the page's "…" lives at a tier; one "…" per page at Compact, carrying
// the other's items: Classic the list bar's, Zune and WinUI the hero's).
// 1.77 (audit Z-OWNER jump list): `ShowJumpList` (the months the reader's view holds and the verb that brings one's header to the list's top) and
// `ShowGroupArgs.Jump` / `.MonthKey`, so a structure can open a jump list (Windows Phone's semantic zoom) from a month header.
// 1.79 (audit Z-LIB): the library's third column steps aside on the page's own width. `LibraryTierPolicy.PaneFromLayout` (the layout ladder's index from which the
// pane stands beside the middle column; default 1, every split) and `LibraryPageProps.PaneBeside`; under it the host opens a picked grouped album's page.
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
    public const int Minor = 79;

    /// <summary>True when a theme built against <paramref name="major"/>.<paramref name="minor"/> can run on this core.</summary>
    public static bool IsCompatible(int major, int minor) => major == Major && minor >= 0 && minor <= Minor;

    /// <summary>The version as text (<c>1.0</c>), for logs and the feedback line.</summary>
    public static string Text => Major + "." + Minor;
}
