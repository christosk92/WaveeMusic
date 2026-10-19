# Theming: WinUI

> **Scope.** The WinUI theme (P6, Developer-only): what each lane built and how to extend it. The core is `theming.md`;
> the plan is `docs\themes\plan\technical-plan.md` (6.2) and the lane plan `P6-plan.md`. The boards are
> `docs\themes\boards\winui\project\` (the tier source of truth is `W-Tiers`, the options `W-ThemeOptions`). Each lane writes only
> under its own heading below.

## W0: the skeleton

The composition, the structure's skeleton, the options, the tiers and the policies every family lane builds on.

**Files.** `Themes\WinUi\WinUiTheme.cs` (the composition: id `winui`, `WinUi.Structure` x `WinUi.Style`, `ThemeAvailability.Developer`),
`Structure\WinUiStructure.cs` (options, registrars, capabilities), `Structure\WinUiOptions.cs` (the option enum, `WinUiParameters`, the
tracked reads), `Structure\Rules\Tiers.cs` (`WinUiTiers`, `WinUiFrameLayout`), `Structure\Rules\WinUiPolicies.cs`,
`Structure\Rules\WinUiLintGuide.cs`. Installed in `App.cs` next to Classic and Zune (`App.ShippedThemeIds`, so `--inspect mount theme=winui`
works). The style (`Style\**`, `WinUiStyle.LayerId` / `WinUiStyle.Create()`) is W1's.

**Developer only.** With Developer mode off, `ThemeCatalog.Resolve("winui")` answers Classic; the stored `theme.id` is kept, so WinUI comes
back when Developer mode does.

**Registering a family.** Each lane implements its own registrar in `Structure\WinUiStructure.<Family>.cs`:

```csharp
public sealed partial class WinUiStructure
{
    static partial void RegisterFrame(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ShellFrame, WinUiFrame.Template, "winui.shell.frame");
    }
}
```

The registrars are `RegisterFrame` (W2a), `RegisterNav` (W2b), `RegisterLists` (W3), `RegisterPlayer` (W4a), `RegisterStage` (W4b),
`RegisterStageMore` (W4c), `RegisterDiscovery` (W5), `RegisterDetail` (W6a: album, artist, discography), `RegisterEntities` (W6b: playlist,
Liked, show, episode, concerts), `RegisterCollection` (W7), `RegisterOverlays` (W8a) and `RegisterScreens` (W8b). `WinUiStructure.RegisterOwn`
runs them all, then `CommonBlocks.Register` (W8b may hand it WinUI's own module page through the `ModulePage` partial) and Nothing for
`Home.Quickplay` (WinUI's pins are the nav pane's; W5 removes that line if it draws one). Template ids start with `winui.`.

**The stand-in (until W9).** `WinUiTheme.Create()` gives the structure Classic's registration as a stand-in: `Register` registers WinUI's
own views, then copies every key WinUI does not draw from Classic (`ViewRegistryBuilder.AddMissing`). Picking WinUI in Developer mode shows
WinUI's views where a family has landed and Classic's elsewhere, all under WinUI's style, policies, tiers and capabilities.
`new WinUiStructure()` is WinUI's own registry, what the registry tests read.

**`DeliberateStandIns`** (`Wavee.Tests\Theming\WinUiRegistryCompletenessTests.cs`) lists every view key WinUI leaves to Classic, each with
the lane that draws it. A family lane DELETES its lines in the PR that registers its keys (the test fails both ways: an unlisted stand-in,
and a listed key WinUI now draws). W9 empties it.

**Options** (W-ThemeOptions: exactly six, no hidden, opt-in or composition-only member; so no WVTH011 matrix line). The structure's three,
stored as `theme.winui.structure.<id>`:

| id | kind | default | apply | reaches |
|---|---|---|---|---|
| `navigationPane` | Select {Expanded, Compact, Automatic}, Combo | Automatic | Live (the pane's width animates, 167 ms) | `NavPanePolicy` (below) |
| `tabs` | Toggle | on | Live (the strip's row animates) | `ShellCapabilities.Tabs` |
| `artworkInRows` | Toggle | on | Transaction (row extents) | `ListPolicy.TrackArtwork` |

The style's three (`backdrop` {Mica, MicaAlt, Acrylic, None} Transaction, `rowDensity` {Standard, Compact} Transaction (not `density`: Classic's stand-in views read that cell typed `RowDensity`), `useWindowsAccent`
Live) are `WinUiStyle`'s (W1); their loc keys are already under `theme.winui.style`. A view reads the structure options through
`WinUiOptions` (tracked); a policy hook gets them as `WinUiParameters`.

**Capabilities** (`WinUiPolicies.CapabilitiesFor`): Tabs = the `tabs` option; NavTree, ForwardButton, HistoryMenu and FriendsPanel on;
MaxBandPins 0. Tabs off follows the board (owner Q1): the strip is hidden but the workspace is kept, Ctrl+T opens in the background and
Ctrl+Tab cycles (core seam WK (a)).

**Tiers** (`WinUiTiers`, board `W-Tiers`): window and content edges 641, 1008 and 1920 (NavigationView's CompactModeThresholdWidth and
ExpandedModeThresholdWidth, plus Huge), each committing at the line on widening and held 24 on narrowing (Compact < 641, Small 641-1007,
Standard 1008-1919, Huge from 1920; narrowing commits below 617 / 984 / 1896). **No Huge 4K edge** (P6 D3): 3840 is Huge with more
columns, and `WinUiTiers.Frame(Huge4K)` is Huge's record. Height: Short below 720, held 24. ShortFloor 170 (330 less the 40 + 48 title
rows and the 72 bar). A family adds its own ladders and rungs (its in-page content breakpoints: 560, 860, 900, 1100 ...) in
`Rules\Tiers.<Family>.cs` through the partial hooks `Ladders<Family>` / `Rungs<Family>` (Frame, Nav, Lists, Player, Stage, Discovery,
Detail, Entities, Collection, Overlays, Screens); they run inside `WinUiTiers.Policy`'s initializer, so keep their numbers in their own
class.

| window tier | content inset (x / top) | title bar, tabs on (off) | play bar | nav pane (Automatic) |
|---|---|---|---|---|
| Compact < 641 | 16 / 16 | 40 + 48 (48) | Mini 64 | minimal: the 280 overlay from the hamburger |
| Small 641-1007 | 24 / 20 | 40 + 48 (48) | 72 | the 48 rail; the hamburger opens the 280 overlay |
| Standard 1008-1919 | 36 / 24 | 40 + 48 (48) | 72 | the 280 pane docked (the user may collapse it to the rail) |
| Huge from 1920 | 48 / 32 | 40 + 48 (48) | 72 | the 280 pane docked |

`WinUiFrameLayout` carries the inset, the bar and the regions a resized frame may clip (the content card, and from Small the nav pane's
48 rail); `TitleBarH(tabs)` answers 88 or 48. Its keys `shell.titlebar`, `shell.content.card` and `shell.playerbar` are Classic's and
Zune's, so the tier harness reads the stand-in frame and WinUI's; `winui.page.title` is the key the lint's inset edge resolves.

**Policies** (`WinUiPolicies.For`, one bundle per `WinUiParameters`): the tiers; the nav pane (280, not resizable, the 48 rail; Automatic:
Small forces the rail and Compact the overlay; Expanded: Small forces nothing; Compact: the rail at Small, while holding it at Standard
and Huge needs W2b, because a band that forces nothing follows the user's collapse); the list policy's artwork from `artworkInRows`. Every
other policy starts at the core default and passes through its family's partial hook in `Rules\WinUiPolicies.<Family>.cs`: `NavPolicy`
(W2b), `FramePolicy` (W2a), `PanelPolicyFor` and `PlayerPolicy` (W4a), `StagePolicyFor` (W4b, W4c), `ListsPolicy` (W3), `DetailPolicyFor`
(W6a, W6b), `ArtistPolicy` (W6a), `CollectionPolicy` (W7), `ArtworkPolicy` (W1, W9), `OverlaysPolicy` (W8a).

**Owner answers the hooks implement.** Q2: no page cap at Huge or 4K (grids and tables fill; only reading text caps at 760, SettingsCards
at 1000 / 1200). Q4: the nav pane stays docked beside a docked side pane at Standard (280 + 340 + 480 fits from 1100); the board's rail
there is a user collapse.

**Lint.** `WinUiLintGuide` (registered by `WinUiTheme.Create()`) checks a page title keyed `winui.page.title` against the content card's
left edge plus the tier's inset. It is inert until a lane keys that node.

**Tests.** `Wavee.Tests\Theming\WinUiCatalogTests.cs` (manifest, resolution, options and apply modes, capabilities, policies, the ladder
and the frame records) and `WinUiRegistryCompletenessTests.cs` (above). `Wavee.ShellTests\WinUiTierLayoutTests.cs` is the harness every
lane extends: the app under WinUI (`WinUiHost`) at the boards' sizes and every edge (640/641, 617/616 from above, 1007/1008, 984/983,
1919/1920, 1896/1895, heights 719/720, 696/695), asserting the first-frame tier, WinUI's records and capabilities, the nav pane's width
per tier, the three regions and the ShortFloor. A lane adds its page fixtures in `WinUiTierLayoutTests.<Family>.cs`.

## W2a: the frame

The frame family (boards Shell-Top, W-Shell-Compact / Small / Huge / Short, W-Narrow, W-Flyouts, W-Tiers): `Structure\WinUiStructure.Frame.cs`
registers `Shell.Frame`, `Shell.ContentState`, `Shell.TitleBar`, `Shell.Search`, `Shell.SearchSuggestions`, `Shell.Masthead`, `Page.Head`,
`Shell.CommandPalette`, `Shell.HistoryMenu`, `Shell.FileDrop`, `Shell.DragChip` and `Shell.CoverScrim` (`Views\Frame`, `Views\TitleBar`,
`Views\Search`, `Views\Masthead`, `Views\Palette`, `Views\FrameBits`). Numbers: `Rules\WinUiFrameRules.cs`; the title bar's record per tier:
`Rules\Tiers.Frame.cs` (`WinUiTiers.TitleBar(tier)`); the allocator's budget: `Rules\WinUiPolicies.Frame.cs`.

**The frame** (`WinUiFrame`): the title bar's slot (`shell.titlebar`), then the body: the content region (the nav column, the content card
`shell.content.card`, the panel reservations, the seams and the narrow drawer's layer, so the drawer overlays the page below the title bar)
over the play bar, with the panel layer over both (dock, float 340, the Compact sheet with its scrim; Classic's second pane and the core's
`DockedPanes`). The content card is the content layer (`R.Layer.FileArea`) with r8 on its top left and a left+top hairline from Small up,
square and flush at Compact (`WinUiFrameLayout.SquareCard`). Page insets (16 / 24 / 36 / 48, top 16 / 20 / 24 / 32) are the pages' and the
masthead's (`WinUiTiers.InsetOf`), never the frame's padding: a scroller keeps its bar on the card's edge.

**Motion.** The nav column's width (`ShellFrameProps.SidebarColumnWidth`) eases over 167 ms THROUGH REAL LAYOUT (`NavColumnAnim`, a reflow on
the pane curve), so the card follows frame by frame on a mode change (the option, the tier, the hamburger) and on a 1440 -> 800 resize; a
Reveal would leave the card at its final x on the first frame. The title bar's slot eases 88 <-> 48 when tabs turn on or off (`TitleSlotAnim`,
a reflow anchored at its foot): the toolbar row stays glued to the page. Prove both with `trace` (keys `shell.nav.column`,
`shell.content.card`, `shell.titlebar`, `winui.titlebar.toolbar`).

**The title bar** (`WinUiTitleBar`, the engine's EP16 shell bar): tabs on, `Rows = 2` (the 40 Mica Alt strip with the captions over the 48
toolbar); tabs off (`ShellCapabilities.ShowsTabStrip` false: WinUI's tabs option now sets `HiddenTabStrip`, the workspace kept, Ctrl+Tab
cycling) one 48 bar ending in the captions, the Wavee mark after Forward from Small up. Per tier (`WinUiTitleBarLayout`): the strip's form
(Ladder 240 / 200 / 160 -> 110; Compact's 140 selected + 40 icon tabs), Forward from Small, the search field on the window's centre line
(520, 640 at Huge, Small's filling the centred span up to 520; the magnifier at Compact), pin and Friends from Standard (Small folds them
into a ⋯ menu), the bell's badge, the avatar (the name beside it at Huge comes from the core's chip: `ChromeBudget.NameEnterW` is 0 at Huge
only). Compact is the touch tier: the buttons are 40 x 40, the tabs and "+" fill the 40 row (the board's 36 grow to the owner's floor; the
Compact strip is its own instance, re-mounted on the 641 crossing). Every interactive child is a keyed `TitleBarItem`; self-updating ones are
components. From Small up the toolbar keeps one shape, so the 1008 and 1920 crossings never re-mount its buttons.

**Search** (`WinUiSearch`): the stock AutoSuggestBox at the title bar's width (`ShellSearchProps.FieldWidth` overridden from the record and the
bar's measured centre: a crowded row narrows the field, it never overlaps the clusters), no focus growth; the popup per W-Flyouts (recent
queries with the history glyph, an edge-to-edge divider, the entity rows, Retry on failure).

**Masthead / Page.Head**: the band on the tier's inset; the head by `PageHeadForm` (Title: Title Large keyed `winui.page.title`; Drill: the
BreadcrumbBar over the title; Hero: the BreadcrumbBar alone), heights within the pages' `BrowseMastheadMetrics` reserve.

**Tests.** `Wavee.ShellTests\WinUiTierLayoutTests.Frame.cs` (the two rows, the clusters and the search per tier, the card's place, tabs off and
back); the harness's Compact 40-DIP rule is live for the title bar (the identity chip, W8a's view, is excluded).

**Open (engine / other lanes).** The strip's tab widths snap on a ladder change (engine `TabStrip.Form`); the search's 520 <-> 640 change at
the 1920 edge snaps on widening and keeps its final x on narrowing (a reflow inside the bar's hugging centre island); the 1008 crossing moves
the bell by the ⋯ slot's 44; the avatar chip (32) is under 40 at Compact (W8a's `Overlay.ProfileChip`); the play bar's height (Mini 64) is W4a's.

## W2b: the nav pane

NavigationView's pane under WinUI: the 280 list, the 48 rail and the overlay. Files: `Themes\WinUi\Structure\WinUiStructure.Nav.cs` (`RegisterNav`: twelve keys),
`Views\Nav\*` (the kit `WinUiNavRows`, the row slot `WinUiNavRow`, the row primitive `WinUiEntityRow`, the heads, the Settings footer, the flyouts, the Edit outline),
`Views\Drawer\WinUiDrawer.cs` (the overlay), `Views\Material\WinUiMaterial.cs`, `Rules\WinUiNavGeometry.cs` (every number, from the board `Shell-Nav`),
`Rules\WinUiNavPlanner.cs`, `Rules\WinUiNavDrawerRules.cs`, `Rules\WinUiPolicies.Nav.cs`. A theme may not reference another theme (WVTH001), so the planner, the row and the
flyouts are derived copies of Classic's, retuned; Classic is untouched.

**The list.** Every row is one line: 36 tall in a 4,2 margin, a 16 glyph or a 16 r2 thumbnail at slot x 12, the label at slot x 44 (pane 48), folder indents of 28, the
3x16 pill at the row's own left edge. The layout document's row shape (Classic's density) never reaches WinUI. Home holds Home and Browse under no header; the
Collections section is titled "Library" (`theme.winui.nav.library`); section heads are static (no chevron: sections never collapse, a section collapsed under Classic still
shows its rows); a folder is the one disclosure (the chevron turns down to up). No counts, no pin marks, no hover "...": the context menu is the right-click. The rail has no
heads, only 1-px rules between the sections; its folder and section tiles open the flyouts. Edit, drag and drop, the reorder bands and the menus are the core pane's and work as
under Classic.

**The footer.** Settings is a fixed row under the scrolling list (`NavRowKit.Footer`, a core seam: null for Classic and Zune). It shows while the layout document's Settings section
is not hidden (the Outline's Show box lands live), takes the pill by the live route, and is a 40 x 36 tile in the rail.

**Modes.** From `navigationPane` and the window tier: Compact = minimal (the 280 overlay from the hamburger), Small = the 48 rail plus the overlay, Standard and Huge = docked 280
(the user may collapse it to the rail). `navigationPane = Compact` holds the rail at every tier: `NavPanePolicy.WideMode` (default `Expanded` = today's behaviour) says what the Wide
band forces. The pane is a fixed 280 (bounds [280, 280], content floor 0), so an Expanded pane at 641 docks at 280.

**The overlay.** `WinUiDrawer`: a light scrim (a click closes) under a 280 acrylic pane with the flyout shadow, sliding in on the pane spline from the toolbar down to the player, in the
frame's content region, so it never pushes the page. Focus is trapped while it is open; Escape closes (the core host). No account row: WinUI's toolbar carries the account.

**Touch.** At the Compact window tier the drawer's pane mounts the touch kit (`WinUiNavRows.TouchKit`): rows 40 tall and flush (the same 40 pitch), the head "+" and the folder "+"
40 wide, so every nav target clears 40 x 40.

**Material.** WinUI paints no wash: the material layer and the tone and blend leaves draw nothing, the veil is neutral, the tint binder publishes "no colour".

**Tests.** `Wavee.Tests\Theming\WinUiNavTests.cs` (planner, ladder, touch metrics, the seam, the registry) and `Wavee.ShellTests\WinUiTierLayoutTests.Nav.cs` (the footer, the rail's tiles, the
overlay never pushing the page, the Compact option holding the rail, the Compact targets).

## W3: the lists

The track table (rows and chrome), the eager row, the episode row, the media surface, the lyrics lines, the artist chart and the track drawer: the seven
list styles of `StyleKeys` (`Structure\WinUiStructure.Lists.cs`, pictures in `Structure\Lists\WinUi*.cs`, numbers in `Rules\WinUiListRules.cs` and the
`WinUi*Layout.cs` records). They are STYLES the core hosts build from, not views, so none is a `DeliberateStandIns` line.

**The rows.** Two skins, one per `artworkInRows`: ON the Modern two-line row (title over its artists, a cover, 48), OFF the Classic one-line 40 row with the
artist in a lane of its own (`WinUiListRules.ListsFor`). An album's numbered rows are one line with no cover (40: the two-line row less
`AlbumRowTrim`). The plate is a full-width r4 backplate: bare at rest, subtle fill 1 (6 %) on hover, fill 2 pressed, SELECTED = fill 1 plus the 3 x 16 accent
pill (the pill stays while the check lane is in), selected-and-hovered fill 3, the playing row the accent text and the accent bars, a withheld row at 45 % that
says "Unavailable" in the time lane, a drop target a 2-px accent line on the top edge. The cover is 40 in the 48 (W-Playlist, W-MultiSelect, W-Unavailable draw
40; W-RowStates' 32 is the specimen), so `WinUiDensityOption.Rows` publishes `ModernArt` 40 (compact density 32 in the 40).

**The lanes and the tiers.** The lanes are the core's; WinUI states the heart in its own lane before the time (`DetailTableTier.HeartTrailing`). The list
column's table ladder (`Rules\Tiers.Lists.cs`, `WinUiListTiers`): below 560 the table is the two-line row, 560-859 drops Plays, Date and Album, from 860 every
lane (Added-by never: no WinUI board draws it), each edge held 24 on widening. The window's own bands (`WinUiListRules.DetailTiers`, the record W6a extends):
Compact < 641 the Compact row form (56 tall, a 40 cover, no column header, title over "artists - album" or "artists - 598.9M plays" on an album, the time, the
heart and a 40 "..." on every row, `KeepDuration`), Small no versions chevron lane (`VersionsLane`), Huge the board's 72 rows with a 64 cover.

**Row size.** The "..." menu's "Row size" rows (`WinUiTrackChrome.CommandKit.DensityItems`) read the PUBLISHED rung and request `style.rowDensity` for the WinUI
composition (`ThemeHost.Apply(new OptionChange(WinUiTheme.Id, ...))`); Classic's stand-in sent Classic's `density` and was a no-op under WinUI.

**Chrome.** Header Caption 12 in the secondary ink (the sorted lane primary), "#" a word and a sort target; the bar reads Find, [play next / shuffle], then the
order, Filter, Select as flat Body 14 words with r4 hover plates; the selection mode is the card (white 5 %, r8), the accent tint at Compact; the page bar's
lead is empty (the hero owns Play and Shuffle: W6a); the unavailable-songs bar is the InfoBar card.

**Lyrics.** `LyricsLinesStyle.StageDepthBlur = true` (the stage's far lines blur); the wipe and the romanization line are the core's.

**Core seams added (contract 1.52, additive).** `DetailTableTier.HeartTrailing`, `KeepDuration`, `VersionsLane` and `DurationWidth` (the 72 / 80 time lane); `Track.ColumnSet.DurationKept` and `DurationWidth`.

**Tests.** `Wavee.Tests\Theming\WinUiListsTests.cs` (the ladder and its holds, the policies, the table records, the density, the styles) and
`Wavee.ShellTests\WinUiTierLayoutTests.Lists.cs` (the table mounted at Compact, Small, Standard: header, lanes, row heights, the Row size request).

## W8b: the screens

Settings, the theme picker and WinUI's six options, the setup wizard, the What's-new page and the report and crash dialogs' bodies (boards `W-Settings`
(-Compact, -Small, -Huge), `W-SettingsSections`, `W-ThemeOptions`, `W-Setup`, `W-ReleaseNotes`, `W-Feedback`, `W-Crash`).

**Files.** `Structure\WinUiStructure.Screens.cs` (`RegisterScreens`: 15 keys), `Structure\Views\Screens\WinUiSettings*.cs`, `WinUiSetup*.cs`,
`WinUiReleaseNotes*.cs`, `WinUiFeedback.cs`, `WinUiCrash.cs`; `Structure\Rules\Tiers.Screens.cs` (the `winui.screens.stack` rung and the screens' motion),
`Tiers.Screens.Settings.cs` / `.Setup.cs` / `.ReleaseNotes.cs` / `.Dialogs.cs` (the records), `WinUiSettingsRules.cs` and the other `WinUi*Rules.cs`, and
`WinUiSettingsGrouping.cs`. Loc is `theme.winui.screens`. WinUI cannot name Classic (WVTH001), so these are WinUI's own views, written from the shared document
(`SettingsDoc`) and the core hosts, in WinUI's row grammar.

**Settings.** The seven tabs are the grouping WinUI publishes (`WinUiPolicies` -> `ThemePolicies.Settings` = `WinUiSettingsGrouping.Seven`): General (account,
devices, keyboard and the window rows), Appearance (+ lyrics), Playback (+ audio), Notifications, Storage & cache, Privacy & diagnostics (+ feedback), About. The page
draws `props.Grouping.PartsOf(props.Group)`; a tab click calls `props.OpenGroup(i)`. Every old slug lands (`WinUiScreensTests` pins each). Title 28 / 36 at Compact and
Small, 40 / 52 above; inset = the frame's; the SettingsCards cap at 1000 (1200 at Huge) on the left edge; below 560 CONTENT width a card puts its control under the label
(`WinUiScreenRules.StackRung`, a content-axis rung). Rows carry no glyphs except the Theme, App mode and Zoom cards. Toggles show their On / Off word. The keyboard list
gets a Ctrl+Tab row where the structure cycles tabs (`ShellCaps.Tracked.TabCycleChord`).

**The Theme card.** One SettingsExpander (current theme name in its header): the picker (four fluid columns at Standard and Huge, one row at Small, a two-up grid at
Compact) and "WinUI theme options": exactly the six, flattened and ordered as the board (`WinUiScreenRules.OptionOrder`). Backdrop, Density and Artwork apply by
transaction; Navigation pane, Tabs and the Windows accent apply live (the schema declares the mode; the view only writes through the row). Navigation pane's Automatic
says what this window width gets. Huge draws a preview column beside the cards.

**Not built (board vs app).** "Browse community themes" (nothing installs a theme from a folder), the tab-count note under Tabs (the theme contract has no open-tab list),
the InfoBar dismiss mark on the since-banner, the Huge preview's full window mock and nav-pane forms, and the feedback / crash bodies' board-only fields (diagnostics
check box, report preview fold, Save as) which the core form does not have.

