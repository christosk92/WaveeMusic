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
