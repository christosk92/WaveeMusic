# Theming: Zune

> **Scope.** The Zune theme (P5, Developer-only): what each lane built and how to extend it. The core is `theming.md`;
> the plan is `docs\themes\plan\technical-plan.md` (6.3, 9). Each lane writes only
> under its own heading below.

## Z0: the skeleton

The composition, the structure's skeleton, the tiers and the policies every family lane builds on.

**Files.** `Themes\Zune\ZuneTheme.cs` (the composition: id `zune`, `Zune.Structure` x `Zune.Style`, `ThemeAvailability.Developer`),
`Structure\ZuneStructure.cs` (options, registrars, capabilities), `Structure\ZuneOptions.cs` (the option enums, `ZuneParameters`, the
tracked reads), `Structure\Rules\Tiers.cs` (`ZuneTiers`), `Rules\ZunePolicies.cs`, `Rules\ZuneMotion.cs`. Installed in `App.cs` next to
Classic.

**Developer only.** With Developer mode off, `ThemeCatalog.Resolve("zune")` answers Classic; the stored `theme.id` is kept, so Zune comes
back when Developer mode does.

**Registering a family.** Each lane implements its own registrar in `Structure\ZuneStructure.<Family>.cs`:

```csharp
public sealed partial class ZuneStructure
{
    static partial void RegisterFrame(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ShellFrame, ZuneFrame.Template, "zune.shell.frame");
    }
}
```

The registrars are `RegisterFrame` (Z2a), `RegisterBand` (Z2b), `RegisterLists` (Z3), `RegisterPlayer` (Z4a), `RegisterStage` (Z4b),
`RegisterStageMore` (Z4c), `RegisterDiscovery` (Z5), `RegisterDetail` (Z6a1), `RegisterArtist` (Z6a2), `RegisterPlaylistLiked` (Z6b1),
`RegisterShowEpisodeConcert` (Z6b2), `RegisterCollection` (Z7), `RegisterOverlays` (Z8a) and `RegisterScreens` (Z8b).
`ZuneStructure.RegisterOwn` runs them all, then `CommonBlocks.Register` and Nothing (`CommonNothing`) for `Shell.NarrowDrawer` and
`Shell.Masthead`. Template ids start with `zune.`.

**The stand-in (until Z9).** A key nobody registers renders nothing, and a missing style key throws, so a half-built Zune would be a blank
window. `ZuneTheme.Create()` therefore gives the structure Classic's registration as a stand-in, and the structure registers it IN PLACE of
its own: picking Zune in Developer mode shows Classic's views under Zune's style, policies, tiers and capabilities. The engine cannot
merge two registrations key by key yet (`ViewRegistryBuilder.Add` throws on a duplicate; a built registry cannot be read back). Once it
offers `ViewRegistryBuilder.AddMissing(ViewRegistry)`, `ZuneStructure.Register` becomes "Zune's own views, then Classic's for every key
Zune lacks", and a family's views show in the app as soon as its lane lands. `new ZuneStructure()` (no stand-in) is Zune's own registry:
what the Zune registry tests read. Z9 removes the stand-in.

**Options** (stored as `theme.zune.structure.<id>`): `background` {ArtworkWash, Mix, None} (Live), `panoramaFit` {Bleed, Fit}
(Transaction), `bandPins` (Live). **Structure parameters** (Hidden: never shown or stored, a composition sets them; each has a WVTH011
matrix line): `collapseBand` (true), `pivotOverflow` {EdgeFadeScroll, MoreMenu}, `seekTimes` {InFace, AtSliderEnds}, `panelMode`
{InlineWhenRoom, AlwaysOverlay} (the enum is `PanelPlacement`, apart from the contract's `PanelMode`) and `maxDockedPanes` (1-3, default 3).
A view reads them through `ZuneOptions` (tracked); a policy hook gets them as `ZuneParameters`.

**Capabilities** (`ZunePolicies.Capabilities`): Tabs, NavTree and ForwardButton off, MaxBandPins 6, HistoryMenu and FriendsPanel on.

**Tiers** (`ZuneTiers`, board `Z-Sizes-Breakpoints`): window and content edges 640, 1008, 1920 and 3200, each committing at the line on
widening and held 24 on narrowing (Compact 300-639, Small 640-1007, Standard 1008-1919, Huge 1920-3199, Huge 4K from 3200; narrowing
commits below 616 / 984 / 1896 / 3176). Height: Short below 700, held 24. ShortFloor 126 (330 less the 48 title bar, the 84 band and the
72 bar). Rungs `pane.second` (1920) and `pane.third` (3200) on the window axis. A family adds its own ladders and rungs in
`Rules\Tiers.<Family>.cs` through the partial hooks `Ladders<Family>` / `Rungs<Family>` (Frame, Band, Lists, Player, Stage, Discovery,
Detail, Artist, Collection, Overlays, Screens); they run inside `ZuneTiers.Policy`'s initializer, so keep their numbers in their own class.

| window tier | gutter | title bar | band (collapsed) | play bar |
|---|---|---|---|---|
| Compact | 16 | 48 | 92 (52) | 64 (mini) |
| Small | 24 | 48 | 84 (52) | 72 |
| Standard | 48 | 48 | 84 (52) | 72 |
| Huge | 48 | 48 | 84 (52) | 72 |
| Huge 4K | 64 | 56 | 84 (52) | 88 |

`ZuneTiers.FrameOf(tiers)` is the record; the page reserves 72 for the bar at every tier (`ZuneTiers.BarReserveH`).

**Policies** (`ZunePolicies.For(ZuneParameters)`, one bundle per value set): no nav pane at any tier (NarrowMode and TinyMode `None`,
not resizable; the Wide band's pane never mounts because NavTree is off), `ZuneTiers.Policy`, and every other policy at the core's
Standard value (Classic's numbers) passed through a family hook: `BandPolicy`, `FramePolicy`, `PanelPolicyFor`, `PlayerPolicy`,
`StagePolicyFor`, `ListsPolicy`, `DetailPolicyFor`, `ArtistPolicy`, `CollectionPolicy`, `ArtworkPolicy`, `OverlaysPolicy`. A lane
implements its hook in `Rules\ZunePolicies.<Family>.cs`.

**Motion** (`ZuneMotion`): `PivotSlide` (250), `TitleScale` (200, the larger line height reserved), `ColumnFade` (120) then
`ColumnCollapse` (200), `GridCrossFade` (160) and `SwapFade`.

**Loc.** `theme.zune.name`, `.description` and `.options.*` are Z0's; each lane writes only inside its own object:
`theme.zune.{frame,band,lists,player,stage,discovery,detail,artist,collection,overlays,screens,states}`. nl and ko-KR fall back to English.

**Tests.** `Wavee.Tests\Theming\ZuneCatalogTests.cs` and `ZuneTierPolicyTests.cs`; `Wavee.ShellTests\ZuneHost.cs` (mount the app under
Zune) and `ZuneTierLayoutTests.cs` (the harness: Zune's size table and edges, the frame's assertions). A lane adds its page fixtures in
`ZuneTierLayoutTests.<Family>.cs`:

```csharp
public sealed partial class ZuneTierLayoutTests
{
    static readonly TierPage Album = new("album", new Shell.Route(Shell.RouteKind.Album, ...), "detail:");
    [Theory, MemberData(nameof(PageSizes))]
    public void The_album_page_holds_at_every_tier(string size) => AssertPage(Album, size, (shell, tiers) => { /* its record */ });
}
```

## Z1: the Zune style

_Not built yet._

## ZC1: the Browse band route (core)

_Not built yet._

## ZC2: "on this PC" (core)

_Not built yet._

## Z2a: the frame

_Not built yet._

## Z2b: the band and the material

_Not built yet._

## Z3: the lists

_Not built yet._

## Z4a: the player

_Not built yet._

## Z4b: the stage frame, Card and LargeArt

_Not built yet._

## Z4c: the rest of the stage

_Not built yet._

## Z5: discovery

_Not built yet._

## Z6a1: the detail frame and the album

_Not built yet._

## Z6a2: the artist page

_Not built yet._

## Z6b1: the playlist and Liked Songs

_Not built yet._

## Z6b2: the show, the episode and the concerts

_Not built yet._

## Z7: the collection

_Not built yet._

## Z8a: the overlays and the dialogs

_Not built yet._

## Z8b: the screens

Settings, the theme picker and its options, the setup wizard, the release notes, and the report and crash dialogs. All fifteen keys are Zune's own
(`Structure\ZuneStructure.Screens.cs`); Zune's registrations win over Classic's stand-in key by key.

**Files.** `Structure\Views\Screens\ZuneSettings*.cs` (the page and its seven tabs), `ZuneSetup*.cs`, `ZuneReleaseNotes*.cs`, `ZuneFeedback.cs`,
`ZuneCrash.cs`; numbers and tier records in `Structure\Rules\ZuneSettingsRules.cs` (settings) and `Rules\ZuneSetupRules.cs` (setup, release notes
and the two dialogs); loc `theme.zune.screens`; tests `Wavee.ShellTests\ZuneScreensMountedTests.cs`.

**Keys.** `Pages.Settings`, `Settings.ThemePicker`, `Settings.ThemeOptions` (page and wizard variants), `Setup.Wizard / Terms / SignIn / Theme /
LocalPlayback / SignInDoor / RuntimeCard`, `Pages.WhatsNew`, `ReleaseNotes.Viewer`, `ReleaseNotes.AfterUpdatePlate`, `Dialog.Feedback`,
`Dialog.Crash`. Template ids: `zune.screens.settings[.theme-picker|.theme-options]`, `zune.setup.*`, `zune.release-notes.*`, `zune.dialog.*`.

**Settings.** A panorama title ("settings"), a lowercase section list (the seven core tabs: the page state and every deep link name a tab), and the
open tab's column. The column is the document drawn: every `SettingsDoc` section of the tab is a group (a 26/300 heading over label | control rows), so a
new document row shows up with its words and its writer; the controls that need labels or ranges (combos, chips, sliders) are built per row in
`ZuneSettings.<Tab>.cs`. Controls are the engine's, drawn by `ZuneControls`; the stateful ones (`ZuneKit.ChoiceCell`, `SliderCell`) keep one signal for
their life and re-read the document on `Settings.PageEpoch`. Appearance mounts the picker and the options through the registry (`Slots.View`), and
beside the column (or under it, by flex wrap) a live preview built from the roles. Node identity: the list, lane, groups and rows are the same nodes in
every tier; a tier changes their direction, wrap and widths only.

**Theme options.** `ZuneSettings.Theme.cs` draws the core's `SettingsOptionsView` by row kind: toggles as switches, short selects as chips (a combo
past four or when the structure asks for one), the accent as a row of square swatches, sliders, confirmed actions, "reset" beside the heading. The
wizard variant has no reset and always stacks.

**Setup.** A flat plate on the dialog ground with an accent column (the step as a 150 numeral at 35 % ink-on-accent, the page's name, the four steps);
the footer is the step label over a 2 DIP line, back and the secondary as words, the primary as the round accent button with its word. Compact is a sheet
with a 120 accent strip and a stacked footer. The core model (`Setup.WizardModel`, `Commands.ResolveFor`) decides everything.

**Release notes.** A panorama title; the main column (since line, hero, highlight panels, the changelog groups) beside the release rail at Standard and up,
a picker under the title below it. The viewer and the after-update plate are built from Zune's parts (no board draws them, owner Q18).

**Dialogs.** The report and crash bodies keep the core chrome; the body width is the active structure's overlay fit (`DialogFit.FormBodyWidth`), frozen
at open. Compact folds the preview behind a 40 row, Huge is a two-pane card.

**Numbers.** `ZuneSettingsTiers.For/Of` (content tier), `ZuneSetupTiers`, `ZuneReleaseNotesTiers` (page: content tier; overlays: window tier),
`ZuneDialogTiers`. The sizes the lane covers are Standard; the others are sane variations ZT8 tunes against the -Compact, -Small and -Huge boards.

**Conflicts with the boards (the plan and the owner win).** The board lists twelve sections; the core page is a seven-tab cell, so account, devices and
keyboard have no document rows and are not built, and audio, lyrics, feedback & help are groups of Playback, Appearance, About and Privacy. No downloads
(Q29 / NQ4): the core Storage cache settings stay. The board's "add a theme" tile and community-themes line are not built (nothing installs a theme
from a folder). Compact and Small draw the section list as a wrapping row above the column, not a drill-in. The Standard preview column wraps under the
options when the window is too narrow for both.

## Z9: the states, "on this PC", the light pass and the completeness test

_Not built yet._

## ZT1: frame tiers

_Not built yet._

## ZT2: player and panel tiers

_Not built yet._

## ZT3: stage tiers

_Not built yet._

## ZT4: discovery tiers

_Not built yet._

## ZT5: detail tiers

_Not built yet._

## ZT6: artist and people tiers

_Not built yet._

## ZT7: collection tiers

_Not built yet._

## ZT8: screens, overlays, dialogs and states tiers

_Not built yet._
