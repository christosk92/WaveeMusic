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
