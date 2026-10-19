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
