# Sidebar customizer page and the visual outcomes of the sidebar data pipeline - 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Features/Sidebar/Curated/**` 6 files 4,247 lines (`SidebarCustomizerPage.cs` 1,042, `SidebarPropertyPanel.cs` 981, `SidebarItemPickers.cs` 600, `SidebarCustomizerLayout.cs` 561, `SidebarCustomizerControls.cs` 555, `SidebarCustomizerPalette.cs` 508) | `src/apps/Wavee/Features/Sidebar/Persistence/**` 4 files 1,491 lines (`SidebarLayoutDoc.cs` 907, `SidebarLayoutStore.cs` 485, `SidebarLayoutMigrations.cs` 53, `SidebarLayoutDefaults.cs` 46) | `src/apps/Wavee/Features/Sidebar/Data/**` 33 files 7,161 lines (incl. `Sources/` 4 files 850) | plus `SidebarEditSession.cs` 164, `SidebarLayoutMenu.cs` 153, `SidebarIcons.cs` 74, `Shared/SidebarMiniature.cs` 343, `SidebarPreferences.cs` 923 (editor half), `Wavee.Core/Sidebar/**` 2,526 | **the action platform this page BINDS** (no chapter named it before this pass): `src/apps/Wavee/Actions/Extensibility/**` 6 files 946 lines (`BuiltInExtensionTable.cs` 303, `WaveeActionTargeting.cs` 251, `WaveeRegistryTable.cs` 173, `WaveeExtensionRegistry.cs` 148, `IWaveeExtension.cs` 39, `PinRowRule.cs` 32) + `Actions/WaveeActionDescriptor.cs` 227 + `Actions/ActionIcons.cs` 87 = **1,260** | **total in scope ≈ 18,300 lines** | 0.3 target — **one five-file set for the whole sidebar platform** (arbitration 2026-09-12, A4; ch. 25's header carries the same list): `Shell/Sidebar.cs` (CORE: planner, geometry, diff/resolve, the pipeline's pure rules) · `Shell/Sidebar.Doc.cs` (CORE: the layout document + reducer + the `sidebar-layout.json` wire and migrations) · `Shell/Sidebar.UI.cs` (pane, rows, rail, the three designs — ch. 25) · `Shell/Sidebar.Customizer.UI.cs` (this chapter's page) · `Shell/Sidebar.Host.cs` (store/binder shell + the four sources) | Wave 4 owner J — **the customizer is sequenced LAST in the wave** (§9.3.7) — **except** `Actions/Extensibility/**`, whose ownership §5 of the plan leaves between owner I ("WaveeCommands + Actions/* → one table") and owner J ("the sidebar platform"); this chapter's recommendation is **I**, see §9.3.9

Cross-references (do not re-specify here): `00-design-system.md` (Spacing/Radii/Tok/Motion/`WaveeType`), `02-cards-and-controls.md` (small controls), `18-shell-frame.md` (the route, tabs, KeepAlive, page transitions), `19-shell-overlays.md` (`ContentDialog`, `MenuFlyout`, `InfoBar`, toasts), `25-sidebar.md` (the pane renderer, section cards, rows, rail, the three designs), `27-settings-and-diagnostics.md` (the Settings row that links here).

Doc drift, one line: `docs/plans/wavee/wavee-sidebar-extension-platform.md` still describes the customizer as a four-region page with a docked inspector column, an outline and a bottom sheet; the CODE deleted all four in Phase 3 (`SidebarCustomizerLayout.cs:25-30` records the deletions by name) and the page is now ONE scrolling column at every width with the property panel re-hosted on the pane's per-section popover. The code wins.

---

## 0. The non-negotiables

1. **One scrolling column at every width, left-aligned, capped at 720 DIP.** There is no tier, no breakpoint, no reflow, no second pane. `SidebarCustomizerPage.cs:50` `ColumnMaxWidth = 720f`; `:383-398` is the whole body. The old four-tier ladder hid the eyebrow, the saved dot, Reset and the preview from anyone under ~1600 DIP — that failure must not return.
2. **The live docked sidebar IS the canvas and IS the preview.** This page mounts no copy of the layout and has no apply step and no dirty state: it reads `UseContext(SidebarPreferences.Slot)` and every edit lands in the docked pane in the SAME frame (`SidebarCustomizerPage.cs:36-38`). A user must never see the page and the sidebar disagree.
3. **Every rejection says something, inline, for 4 s — ON THIS PAGE.** `RejectLocKey` covers all 13 `SidebarRejectReason` members plus 5 shortcut-band overrides (`SidebarCustomizerPage.cs:439-460`); the strip is an `InfoBarSeverity.Informational` bar that auto-dismisses on `UseTimeout(ClearReject, 4000f, DepKey.From(rejectEpoch))` (`:170`). A click that does nothing and explains nothing is a regression.
   **The honest 0.2.9 caveat**: the OTHER host of the same controls — `SidebarEditSession` (the pane's per-section options popover) — has no message surface at all. Its `Apply` only bumps `_rejectEpoch` so the control snaps back (`SidebarEditSession.cs:155-163`); there is no `RejectLocKey`, no strip, no toast. So a rejection provoked from the popover is *visible* (the switch returns) but *mute*. 0.3 should give the popover the same sentence (an `InfoBar` at the popover's foot, or the shell toast) rather than inherit the silence.
4. **A rejected edit visibly snaps the control back.** Every `Cz*` row is CONTROLLED: it reads the document during render and mirrors into its own signal from a LAYOUT effect whose dep key folds `CzRow.Epoch = LayoutVersion*397 + RejectEpoch` (`SidebarCustomizerControls.cs:247-248`). Without the reject half the switch keeps the position the user dragged it to while the document says otherwise.
5. **Destinations render FIRST in the palette and are labelled by `ShellNav.Dest`, never by a second string.** `SidebarPalette.Groups` puts `Destinations` at index 0 (`SidebarCustomizerLayout.cs:249-255`); `LabelOf`/`GlyphOf` resolve a route row through `ShellNav.Dest(route)` (`SidebarCustomizerPalette.cs:135-142`). Typing "home" must answer with **Home**, not with "Links — Shortcuts to pages like Home or Search".
6. **One palette click = one undoable command that produces a WORKING row.** A bare Links row opens the destination picker first (`AddLinksSection`, `SidebarCustomizerPage.cs:543-552`); Liked Songs and every destination are pre-seeded `AddSection(StaticLinks, Item:…)`. The only honest two-command exception is "Recently played" (`:605-613`), and it is documented as such.
7. **The append rule is visible, never invisible state.** While a `StaticLinks` card is the canvas subject, the Destinations group header grows a trailing `sidebar.customizer.appendsTo` caption in `WaveeAccent.Decor` naming the section (`SidebarCustomizerPalette.cs:144-165`).
8. **The section budget is always on screen.** `{used}/{max}` at 11f/600, `Tok.TextTertiary`, flipping to `Tok.SystemFillCritical` at 40/40 (`SidebarCustomizerPalette.cs:98-102`, cap `SidebarLayoutReducer.cs:14` `MaxSections = 40`).
9. **Nothing vanishes into an invisible elsewhere.** The Hidden sections group walks top-level AND children and offers one Show button per hidden section, including a section of a kind this build does not understand (`SidebarCustomizerPage.cs:816-857`).
10. **"Saved locally" is a 6-DIP success dot + 11f tertiary label, and it goes silent on a fault** — the error `InfoBar` owns failures, and two voices telling one story is how a UI starts lying (`SidebarCustomizerPage.cs:302-321`).
11. **The design segmented control makes the silent force-switch to Curated visible AND reversible.** The menu row switches design before navigating (`SidebarLayoutMenu.cs:68-73`); the page's `Segmented` shows Custom selected and switching back goes through `prefs.SwitchDesign`, never a raw `Design.Value` write (`SidebarCustomizerPage.cs:793-798`).
12. **The template confirmation shows a real miniature of the target document**, not a sentence about it: a 220-DIP tall card holding a 258-DIP quarter-scale pane using the live `SidebarRowGeometry` ladder and the fake catalog's real covers, beside a synthetic workspace (`SidebarMiniature.Template`, `Shared/SidebarMiniature.cs:62-96`).
13. **ONE enum treatment per value, IN THE PROPERTY SURFACE.** Short choices → `Segmented` with its 24×3 accent pill suppressed via `PartSelectionPill`; long choices → a real `ComboBox`. `SelectorBar` is BANNED in the property panel (`SidebarCustomizerControls.cs:285-340`, budgets at `:316,319`). The screenshot that showed Density wearing a plate AND a blue underline must not recur.
    **Scope, exactly**: the rule is `CzRow.Choice`'s, and `CzRow.Choice` is used by the property panel, `CzQueryBlock`, `CzConfigRow` and the action picker's mode row. The PAGE's own design segmented is NOT one of them — `SidebarPresetBlock` calls the bare `Segmented.Create(designs, index, SwitchDesign)` (`SidebarCustomizerPage.cs:775`), i.e. `Segmented.DefaultStyle`: **h 34, font 14, item min-w 52, and the 24×3 accent pill VISIBLE under the selected segment**. Two treatments therefore ship side by side in 0.2.9 (page = plate + pill, panel = plate only). Port the fact, and decide it on purpose — do not "tidy" the page control into the compact one without deciding, and do not assume the compact metrics apply to it.
14. **One right-hand column width.** Every dropdown / number box / text box in the property surface is `CzRow.ComboWidth = 264f` (`SidebarCustomizerControls.cs:343`); the slider is 272 (`:478`). The right edge must not stair-step.
15. **The pipeline's readiness ladder is honest.** `Pending` → skeleton rows (3), `Ready`+0 rows → the section's empty behaviour, `Error`/`Missing`/`Disabled`/`Incompatible` → exactly one actionable `PromptRow`, a failed source that HAD rows → its last-good snapshot replayed as `Cached` rather than a blanked section (`SidebarBinderPipeline.cs:303-312`, `SidebarRowPlanner.cs:199`).
16. **A bound action row is VISIBLE-BUT-DISABLED with a sentence, never absent.** This is the extension platform's
    own rule (`WaveeActionTargeting.cs:36-38`, `WaveeExtensionRegistry.cs:18-21`) and it reaches three surfaces this
    chapter owns: the picker's `ReasonRow`, the property panel's item-row "why inert" line, and the pane's action row
    (which `ToolTip.Wrap`s the whole row in the reason — `SidebarPaneSlot.cs:735-762`). A row whose extension was removed
    still renders, with "This action is no longer available"; it never vanishes, because a vanishing row makes the user's
    own sidebar look broken. There are exactly **7** reasons and each has its own sentence (§1.1's reason table) — "it
    didn't work" is not one of them.
17. **The descriptor is the ONLY lookup path for a bound row, and the registry NEVER unregisters.** `TryGetAction` /
    `TryGetSource` over an append-only, insertion-ordered, key-unique table; duplicates are refused **first-wins** and
    recorded, never dropped silently (`WaveeRegistryTable.cs:121-157`). Nothing is removed even when an extension is
    disabled — that is what keeps every stored binding resolvable to a descriptor, which is what makes rule 16 possible
    at all. A 0.3 registry that "cleans up" removed contributions breaks the disabled-with-a-reason row.
18. **`BuiltInExtensionTable`'s exclusions are a DESIGN DECISION, not an oversight.** `AddToPlaylist` /
    `AddToDefaultPlaylist` / `RemoveFromThisPlaylist` / `RemoveFromQueue` / `SelectAll` (they need a live selection or a
    playlist host with resolved row ids — none survives a restart), `ViewCredits` (needs a resolved `Track` with a
    primary-artist uri), `Rename` / `TogglePlaylistPublic` / `InviteCollaborators` / `DeletePlaylist` (owner-only
    management; a one-click sidebar shortcut is the wrong affordance, delete especially) and the `Video ▸` verbs (they
    open file pickers over a service that may not exist) are deliberately NOT bindable — `BuiltInExtensionTable.cs:16-30`
    is the only place that record exists, and **no test asserts it** (the file is engine-bound and is not source-included
    by `Wavee.Tests`). A re-author who "completes the table" has silently changed the product. Port the comment with the
    table.

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
ContentHost route "sidebar-customize"                      Features/Shell/ContentHost.cs:250-252
│  Key = "page:sidebar-customize:" + (Arg ?? "")           — a KeepAlive destination (MaxEntries **3**,
│  inner Key = "sidebar-customizer:" + (Arg ?? "")           ContentHost.cs:106 — live page + 2-deep back
│                                                            stack; 8 was reverted for scene-node growth)
└─ SidebarCustomizerPage(r.Arg) : Component, ISidebarEditHost   Curated/SidebarCustomizerPage.cs:43
   │  ctx: SidebarPreferences.Slot, ActionServices.Slot, Overlay.Service,      :129-136
   │       LibraryStore.Slot, WaveeExtensionRegistry.Slot, HistoryStore.{NavCtx,BackCtx,Slot}
   │  signals: Selected, SelectedItem, PaletteQuery, RejectEpoch, _bannerEpoch  :58-78
   │  root BoxEl Key="customizer" Direction=1 Grow=1 ClipToBounds               :172-177
   ├─ HeaderBar()                     h 64, pad 8/0/16/0, gap 8                 :187-250
   │  ├─ BackButton()                 plain BoxEl wrapper (load-bearing) +      :259-268
   │  │                               ToolTip.Wrap(IconButton Icons.Back, Small)
   │  ├─ title lane  Grow=1 Basis=0 Shrink=1 MinWidth=0 Justify=Center          :204-222
   │  │  ├─ WaveeType.Eyebrow(template name)  Color=WaveeAccent.Decor, 1 line   :211-215
   │  │  └─ TextEl(CzLoc.Title) 16f/600 Tok.TextPrimary, 1 line, ellipsis       :216-220
   │  ├─ SavedIndicator()   [healthy only]  6×6 circle SystemFillSuccess + 11f  :305-321
   │  └─ command cluster    Direction=0 Shrink=0 Gap=4 AlignItems=Center        :227-241
   │     ├─ ToolTip(IconButton Icons.Undo, Small, isEnabled=CanUndo)            :232-233
   │     ├─ ToolTip(IconButton Icons.Redo, Small, isEnabled=CanRedo)            :234-235
   │     ├─ Button "Reset to template"  Subtle · Small                          :236-237
   │     └─ Button "Done"               Accent · Small                          :238-239
   ├─ Divider()                       1 DIP Tok.StrokeDividerDefault      Dsl/Factories.cs:177
   ├─ Banners()                       Direction=1 Shrink=0 Gap=4 pad 16/8/16/0  :325-378
   │  ├─ InfoBar Warning  "corrupt"   + [Copy path][Start fresh]                :332-351
   │  ├─ InfoBar Error    "saveFault" + SafeDetail in parens                    :355-366
   │  └─ InfoBar Informational  reject key, onClose=ClearReject                 :368-369
   └─ Body()  ScrollView Key="customizer-column" AutoEdgeFade                   :383-398
      └─ column BoxEl Direction=1 Gap=16 MaxWidth=720 pad 16/12/16/20           :384-386
         ├─ Embed.Comp SidebarPresetBlock(this)                                 :747-801
         │  │  outer BoxEl Direction=1 Shrink=0 Gap=12 (group ↔ template list)  :782-784
         │  ├─ CzRow.Group("Sidebar design")                                     :787
         │  │  ├─ CzRow.Wide(designsSub, Segmented[Classic|Library|Custom])     :774-775
         │  │  │    STOCK Segmented: h34 / 14f / item min-w 52 / pill VISIBLE
         │  │  └─ CzRow.Prop("Show section contents", ToggleSwitch)             :778-779
         │  └─ Embed.Comp SidebarTemplateList(page)                              :449-508
         │     └─ CzRow.Group("Start from a template", 5 radio cards)            :469
         ├─ Embed.Comp SidebarCustomizerPalette(this)              Palette.cs:31-387
         │  ├─ Head(used, full, picking)  gap 8, margin 4/0/4/0                  :89-104
         │  │  ├─ CzRow.GroupLabel("Add a section")  |  BackRow (pick mode)      :96-97
         │  │  └─ TextEl "{used}/{max}" 11f/600                                  :98-102
         │  ├─ SearchBox()  TextBox over PaletteQuery, h 30, placeholder         :382-386
         │  └─ card BoxEl  Key "palette-card:{sections|contributions}"           :72-83
         │     │  Animate = PageSlideForward|PageSlideBack, Corners=ControlAll
         │     │  Fill=FillCardSecondary, Border 1 StrokeCardDefault, pad 4
         │     ├─ GroupHeader(group[, appendsTo caption])                        :144-165
         │     ├─ Row(glyph, label, sub, onClick, drag) × N                      :322-359
         │     │  ├─ 24×24 glyph plate  FillSubtleSecondary, Icon 13f            :348-353
         │     │  ├─ lines  13f/600 + 11f tertiary (wrap, 2 lines)               :324-335
         │     │  └─ AddChip()  24×24 FullAll, Opacity .45→1 on ROW hover        :365-372
         │     └─ [empty arms]  Note() 12f tert, wrap 3, margin 4/8/4/8          :316-320
         │        ├─ sections list, query with no hit   → "Nothing matches …"    :129
         │        ├─ pick mode, query with no hit       → the SAME line          :298
         │        └─ pick mode, registry has 0 sources  → sidebar.extension.manage :273
         ├─ Embed.Comp SidebarHiddenSections(this)                  Page.cs:816-857
         │  └─ CzRow.Group("Hidden sections", caption=HiddenSectionsSub)          :834
         │     └─ CzRow.Prop(title, "Hidden", Button "Show", icon=kind glyph)    :847-856
         └─ Embed.Comp SidebarAdvancedBlock(this)                   Page.cs:867-911
            └─ CzRow.Group("Advanced")                                           :881
               ├─ CzRow.Prop("Reset to template", resetBody, Button)            :883-885
               └─ CzRow.Wide("Layout file", sub, path 11f + [Show][Copy])       :886-908

MODALS (opened from the page, hosted by Overlay.Service)             Curated/SidebarItemPickers.cs
├─ SidebarPickers.OpenItem → ContentDialog DialogW=480, PrimaryText=""           :32-53
│  └─ SidebarItemPickerBody(page, pick, entitiesOnly, kindFilter)                :102-239
│     ├─ SelectorBar["Navigation","Library"]  (hidden when entitiesOnly)         :130-132
│     ├─ TextBox query, w 432, h 32                                              :133-136
│     └─ ScrollView h 320 → PickerRow h 40 × N                                   :146-149, 212-232
├─ SidebarPickers.OpenAction → ContentDialog, ALL three built-ins ""             :70-89
│  ├─ SidebarActionPickerBody(model)                                             :358-551
│  │  ├─ ScrollView h 260 → ActionRow h 48 × N                                   :402-405, 411-480
│  │  ├─ Divider + ModeRow (CzRow.Wide + CzRow.Choice)                           :482-496
│  │  ├─ TargetRow (FixedEntity → item picker · FixedTrack → now playing)        :498-525
│  │  └─ ReasonRow (Icons.StatusWarning 12f + 11f tertiary)                      :529-550
│  └─ SidebarActionPickerFooter(model)  [Cancel Standard][Add Accent]            :559-600
└─ ShowTemplateConfirmation → ContentDialog DialogW=480                Page.cs:707-732
   └─ body 13f/secondary wrap 3 lines + SidebarMiniature.Template(target)        :720-729

THE RE-HOSTED PROPERTY SURFACE (this page's controls, mounted by the PANE's "…" popover)
SidebarPropertyPanel(ISidebarEditHost, scrollKey)                    Curated/SidebarPropertyPanel.cs:36
├─ SubjectHeader  h 52, pad 12/0/8/0                                             :127-155
│  ├─ Icon CzGlyphs.ForKind 16f + title 13f/600 + kind 10f tertiary
│  └─ CzMenuButton(Icons.More) → [Duplicate][—][Remove section]                  :152, 166-173
├─ Divider()
└─ ScrollView AutoEdgeFade, gap 12, pad 0/4/0/24                                 :95-102
   ├─ CzRow.Group("General")   CzTitleRow (absent for Divider) · CzHiddenRow      :157-164
   ├─ CzRow.Group("Content", caption="{n} items")   [only when non-empty]         :85
   │  ├─ CzQueryBlock        (SupportsLibraryQuery: EntityList / PlaylistTree)    :440-568
   │  ├─ [Extension only]  read-only "Extension" row whose SUB is the source id   :246
   │  │  ├─ ref not well-formed → Note "Pick a contribution first."               :239
   │  │  ├─ source unregistered → Note "Manage extension"                         :252
   │  │  ├─ doc schema > build schema → Note "Manage extension" (change nothing)  :259
   │  │  └─ CzConfigRow × schema fields                                           :573-770
   │  ├─ CzItemRow × items    (AcceptsItems kinds)                                :775-981
   │  ├─ [Pinned, 0 items] EmptyRow(Icons.Pin, sidebar.pin.emptyHint) and RETURN  :290-294
   │  └─ [not Pinned] [Add…][Action shortcut] buttons row, pad 12/8/12/12         :300-319
   │        Add…  enabled = EntityEmbed || items < ItemCapacity(kind)             :306
   │        Action shortcut  ABSENT for EntityEmbed; else enabled = !full         :308-312
   ├─ CzRow.Group("Appearance")  [only when non-empty]  Density · Presentation
   │                             · GridColumns (only while Presentation==Grid)
   │                             · Artwork · Subtitles · CountBadges
   │                             · InlineControls · PlayButton                    :182-211
   ├─ CzRow.Group("Behavior")    [only when non-empty]  RecentsSource · MaxItems
   │                             · EmptyBehavior · ShowInRail · CzCollapsedRow
   │        CollapsedByDefault is SKIPPED — an add-time seed no renderer reads;
   │        CzCollapsedRow edits the LIVE spec.Collapsed instead                  :191-198, 224-230
   └─ CzRow.Danger("Remove section", Icons.Delete)  pad 8/12/8/24                :88-93
        A Divider therefore shows exactly TWO groups: General (Hidden only) + the danger button.
```

**Config field kind → control family** (`CzConfigRow`, `SidebarPropertyPanel.cs:611-675`) — the whole of what an
extension can ask the customizer to draw:

| `SidebarConfigFieldKind` | row form | control | notes |
|---|---|---|---|
| `Bool` | `CzRow.Prop` | `ToggleSwitch` | default from `DefaultJson == "true"` (`:724`) |
| `Int` | `CzRow.Wide` | `NumberBox.CreateWithSpinners`, w 264 | `min = Field.Min`, `max = Field.Max > Min ? Max : 500`; commit clamps (`:619-626`) |
| `Enum` | `CzRow.Wide` | `CzRow.Choice` (Segmented ≤4 short labels, else ComboBox) | **empty `EnumValues` falls through to `String`** (`:632`); labels via `EnumLabel` — 13 known first-party words reuse catalog keys, anything else shows the RAW value (`:753-769`) |
| `EntityUri` | `CzRow.Prop` | `[ Add… ]` Standard·Small → entity-only item picker | sub line = resolved name → raw uri → `sidebar.source.artistTopTracks.unset`; a field key containing "artist" filters the picker to `SidebarEntryKind.Artist` (`:644-658`) |
| `UriList` | `CzRow.Prop` | `CzMenuButton(Icons.More)` | sub = "{n} items" or nothing at 0; menu = `Add…` + separator + up to **20** uris, click removes (`:660-666, 678-710`) |
| `String` (and the default arm) | `CzRow.Wide` | `TextBox` w 264 h 32, commit on Enter/blur | `:668-674` |

**The action platform (no visual node of its own; it decides what the action picker lists and what a bound row draws).**
`Actions/Extensibility/**` is the half of this surface that no chapter drew a tree for, and every one of its decisions
is visible: the glyph on a picker row's 28-DIP plate, the caption under it, whether the commit button enables, whether a
bound row in the docked sidebar is live or greyed, and which sentence the grey one carries.

```
WaveeExtensionRegistry (app root, reference-stable, Context Slot)  Actions/Extensibility/WaveeExtensionRegistry.cs:34
|  Build(services) ONCE per shell (Features/Shell/WaveeShell.cs:576) - actions FIRST, then the data sources
|  (RegisterSidebarSources, :577), which is why the picker's 13 actions and W3's 9 sources are each in their
|  own registration order
+- _actions  : WaveeRegistryTable<WaveeActionDescriptor>    <- the action picker's ONE source       :41,65
+- _sources  : WaveeRegistryTable<ISidebarDataSource>       <- W3's contribution list               :42,68
+- Diagnostics : IReadOnlyList<WaveeRegistryDiagnostic>     <- **NO UI IN 0.2.9** (see DATA GAPS)   :76-87
+- Resolve / Execute(services, binding) - the ONE path bound UI takes                             :138-147
   |  an unresolvable key => ActionMissing, never a silent no-op                                  :141
   v
WaveeRegistryTable<T>  - append-only, insertion-ordered, key-unique    Extensibility/WaveeRegistryTable.cs:112
|  Add() outcomes: Registered / RejectedNull / RejectedInvalidKey / RejectedDuplicate (FIRST WINS)  :18-28,139-157
|  nothing is ever REMOVED - that is what keeps a stored binding resolvable (s0.17)                 :121-124
+- WaveeExtensionKey - the namespaced `publisher.contribution[.sub]` vocabulary                      :41
      IsValid: 2+ segments, ASCII-letter start, [A-Za-z0-9_-] body, <=128 chars (the key is
               PERSISTED inside sidebar-layout.json, so it is length-bounded on purpose)          :51-74
      Compose(providerId, actionId): tolerates a document that already stored the qualified form,
               "" when either half is missing => reported as missing, never mis-matched           :85-93
   v
WaveeActionDescriptor  - ONE bindable action                         Actions/WaveeActionDescriptor.cs:28
|  VISUAL fields:  LabelLocKey -> Label()  .  IconKey -> Icon(isChecked) via ActionIcons.Resolve  .  AcceptedTargets
|                  (which IS the picker's "Acts on" list AND the row's 11f caption)  .  IsChecked (non-null => the
|                  row is a TOGGLE and Icon() picks the filled variant)  .  Destructive (red glyph while unselected)
|  SAFETY fields:  RequiresConfirmation + ConfirmTitle/Body/PrimaryLocKey -> SettingsShared.Confirm        :63-73
|  NON-visual:     ArgumentSchema (opaque in M1) . RequiredPermissions (recorded, UNENFORCED until M3) . LegacyId
|  Resolve(services, binding, peek) - target matrix, THEN the confirm-surface refusal, THEN IsEnabled   :103-116
|  Execute(...)  - refuses rather than running unconfirmed when Overlay is null                        :123-145
+- ToMenuItem(...) - the disabled reason rides in the **AcceleratorText column**, the one place a menu
                     row can carry a short explanation without a second line                           :150-168
   v
WaveeActionTargets  - the pure target matrix (engine-free; source-included by Wavee.Tests)
                                                                Extensibility/WaveeActionTargeting.cs:135
   Resolve(mode, targetKey, accepted, host) checks `accepted` FIRST, so narrowing a descriptor's set
   can never widen what an old binding does                                                        :182-186
   WaveeActionHostState snapshots {nowPlayingTrackUri, nowPlayingContextUri, activeRouteKey,
   fixedTargetName, nowPlayingTrackName, activeRouteName} - a plain struct, so the resolver never
   touches a signal; the names are what user-facing activity/history shows                          :62-89
```

**The reason vocabulary — 7 members, 7 sentences** (`WaveeActionTargeting.cs:39-58,140-158`; strings from
`assets/loc/en-US.json` `sidebar.action.unavailable.*`, lines 1888-1897). These are the ONLY words a disabled bound row,
a picker `ReasonRow` or a property-panel "why inert" line can say:

| `WaveeActionUnavailable` | loc key | en-US | raised by |
|---|---|---|---|
| `None` | - (null) | - | available |
| `ModeNotSupported` | `...unavailable.mode` | "This shortcut can't use that target" | a binding naming a mode the descriptor does not accept — a document from a newer build, or an extension that narrowed its set (`:185-186`) |
| `MissingTargetKey` | `...unavailable.noTarget` | "No item chosen" | `FixedEntity`/`FixedTrack` with no `TargetKey` (`:195,213`) |
| `NoNowPlaying` | `...unavailable.noNowPlaying` | "Nothing is playing" | a `NowPlaying` binding while nothing plays (`:219-220`) |
| `NoActiveRoute` | `...unavailable.noRoute` | "No page is open" | an `ActiveRoute` binding with no route provider (`:226-227`) |
| `ActionMissing` | `...unavailable.missing` | "This action is no longer available" | no descriptor for the key — extension removed or disabled (`WaveeExtensionRegistry.cs:141`) |
| `HostUnavailable` | `...unavailable.host` | "Not available right now" | a `RequiresConfirmation` action with **no overlay to confirm in** — the deliberate refusal, never a silent unconfirmed run (`WaveeActionDescriptor.cs:109-110`) |
| `NotApplicable` | `...unavailable.notNow` | "Not available right now" | the descriptor's own `IsEnabled` said no (`:112-113`) |

The last two share a sentence on purpose; keep both enum members, because only `HostUnavailable` is the safety refusal.

**The 13 first-party descriptors — the whole action picker, in registration order** (`BuiltInExtensionTable.cs:61-273`).
The "accepted targets" column is *literally* the row's 11f subtitle: `TargetSummary` joins the mode labels with " · " in
the fixed order None → FixedEntity → FixedTrack → NowPlaying → ActiveRoute (`SidebarItemPickers.cs:294-304,326-338`).

| # | key (persisted — **never rename**) | label (en-US) | `IconKey` → glyph fallback | accepted targets = the row's subtitle | notes |
|---|---|---|---|---|---|
| 1 | `wavee.play` | Play | `play` → `Icons.Play` (themed "Play") | A chosen item · A chosen track | one branch on the RESOLVED mode: track => `PlayTrackAsync`, else `PlayAsync` (`:73-74`) |
| 2 | `wavee.playNext` | Play next | `play-next` → `WaveeIcons.PlayNext` **U+E900, `WaveeIcons.Font`** | A chosen track | the tofu row (`ActionIcons.cs:52`) |
| 3 | `wavee.addToQueue` | **Play after** | `queue` → `WaveeIcons.PlayAfter` **U+E901, `WaveeIcons.Font`** | A chosen track | the other tofu row (`ActionIcons.cs:53`). NB the label is *Play after*, not "Add to queue" - the key is the only place that word survives |
| 4 | `wavee.toggleLike` | Save to Liked Songs | `like` → `Icons.Heart` / `HeartFill` when checked | A chosen track · Now Playing | **TOGGLE** (`IsChecked`) |
| 5 | `wavee.save` | Save to Your Library | `save` → `Icons.Add` / `Icons.Check` when checked | A chosen item | **TOGGLE**. 4 and 5 are two rows for one saved-set concept on purpose: they used to share `Strings.Menu.Save` and rendered as two identical "Save" rows with the same heart (round-2 defect 6b). Dropping either makes its target kind unbindable (`:106-114`) |
| 6 | `wavee.open` | Open | `open` → `Icons.OpenInNewWindow` | A chosen item · Now Playing | routes through `t.RouteKey`, so an entity with no route is a silent no-op (`:156-158`) |
| 7 | `wavee.goToAlbum` | Go to album | `album` → `Icons.Album` | Now Playing | NowPlaying ONLY — a persisted track uri cannot name its album without a metadata round-trip (`:161-164`) |
| 8 | `wavee.goToArtist` | Go to artist | `artist` → `Icons.Contact` | Now Playing | same reason |
| 9 | `wavee.copyLink` | Copy link | `link` → `Icons.Link` | A chosen item · A chosen track · Now Playing | the only 3-mode row; on success raises a Success toast, on a clipboard throw a `PlaylistEditErrors.Toast` (`:203-211`) |
| 10 | `wavee.songRadio` | Go to song radio | `radio` → `Icons.RadioTower` | A chosen track · Now Playing | |
| 11 | `wavee.artistRadio` | Go to artist radio | `radio` → `Icons.RadioTower` | A chosen item | 10 and 11 share a glyph and are told apart ONLY by their caption — which is why the caption is a caption, not decoration |
| 12 | `wavee.pinToSidebar` | Pin to sidebar | `pin` → `Icons.Pin` | A chosen item · The open page | `IsEnabled` = a pin store exists **and** `PinBindable(binding)`, which is how "a track is never pinnable" reaches the picker as a greyed row (`:250,276-279`) |
| 13 | `wavee.unpinFromSidebar` | Unpin from sidebar | `unpin` → `Icons.UnPin` (E77A) | A chosen item · The open page | 12/13 are an **absolute-state PAIR, never a toggle** — the `Menus.AccessItem` precedent ("a mis-checked toggle would invert the user's intent") and Spotify's own menu. `PinRowRule.Decide(hasStore, pinId, isPinned)` is the menu-side half of the same rule: no store **or** an unpinnable target => NO row at all rather than a dead one (`PinRowRule.cs:9-31`) |

Four facts a re-author will otherwise get wrong:

- **No first-party descriptor accepts `None`.** "Nothing (a global action)" is a real mode label
  (`sidebar.customizer.targetNone`, en-US line 1845) that **never appears** in 0.2.9's picker, because not one of the 13
  sets `WaveeActionTargetModes.None`. Do not draw it in a mock, and do not "fix" the table by adding a global verb
  without deciding to.
- **No first-party descriptor sets `Destructive` or `RequiresConfirmation`.** Both paths are fully built and entirely
  unexercised: the red-glyph arm (`SidebarItemPickers.cs:463-466`) and the confirmation gate
  (`WaveeActionDescriptor.cs:109-110,129-144`) never fire in 0.2.9. They exist "for the day one of them is bound anyway"
  (`BuiltInExtensionTable.cs:23-25`) and must survive the port — a 0.3 that deletes them as dead code removes the only
  thing standing between a bound `DeletePlaylist` and a one-click destroy.
- **The picker renders the GLYPH FALLBACK, not the themed icon.** `ActionRow` calls
  `Icon(icon.Glyph ?? Icons.More, 14f, ..., icon.Font)` (`SidebarItemPickers.cs:461-465`) — the `IconRef.ThemedName` arm
  is not taken at all, so every row shows its Segoe-or-WaveeIcons fallback. The **pane** resolves the same `IconRef`
  through a different ladder: icon override → `ThemedIcon` if registered → glyph + its own font → `Icons.MusicNote`,
  at 16f (`Pane/SidebarPaneText.cs:226-236`). The same bound action therefore wears a *layered themed* mark in the
  sidebar and a *flat glyph* in the picker. Port the divergence knowingly or unify it on purpose — do not discover it.
- **A toggle descriptor's picker row is always drawn UNCHECKED.** `a.Icon()` takes the default `isChecked: false`
  (`SidebarItemPickers.cs:414`), so rows 4 and 5 show the outline Heart and the Add plus even when the target is already
  saved. Only the *bound* surfaces call `Checked(...)` / `Icon(on)` (`WaveeActionDescriptor.cs:118-119,162-166`).

**The pipeline (no visual node of its own; it decides what the pane draws).**

```
SidebarPreferences (app root, reference-stable)                     SidebarPreferences.cs
├─ Layout : SidebarCustomLayout            + LayoutVersion : Signal<int>       :466
├─ Edit   : SidebarEditSession             Expanded · ShowContents · OptionsSection
├─ Pins   : SidebarPinStore                + PinsVersion
├─ Entries: SidebarEntries (cell)          + Version, PinCount, QualifiersAvailable
└─ Binder : SidebarProjectionBinder  ← the ONE driver                          :450
   ├─ triggers fold  SidebarBinderTriggers.Fold()        Data/SidebarBinderPipeline.cs:49-69
   ├─ SidebarProjection.Build(...)                       Data/SidebarProjection.cs:46
   │    ├─ SidebarFirstSeen.Stamp(id)  (playlist "added at" proxy)  Data/SidebarFirstSeen.cs:66
   │    └─ SidebarRecency (navigation)                   Data/SidebarRecency.cs:36
   ├─ SidebarBinderPipeline.Shape(...)  filter → sort → PinsFirst  :137-170
   │    ├─ SidebarSearch.Matches                         Data/SidebarSearch.cs:37
   │    ├─ SidebarSort.Apply / .Effective                Data/SidebarSort.cs:62-72
   │    └─ SidebarProjection.PinsFirst                   Data/SidebarProjection.cs:225
   ├─ ResolvePins → SidebarBinderPipeline.ResolveUnlistedPin  :185-208  (+ async hydration)
   ├─ SidebarBinderPipeline.ResolveExtensions → SidebarExtensionSlices  :221-247
   │    └─ per source: EnsureFresh → Fill → State/NeedsPrompt → cache Store/TryReplay
   ├─ SidebarEntriesShadow.Publish → version bump ONLY on a real change  Data/SidebarEntriesShadow.cs:45
   └─ CurrentInput : SidebarProjectionInput                            :221
        ↓
SidebarRowPlanner.Build / BuildRail / BuildEdit          Data/SidebarRowPlanner.cs:203/217/275
        ↓ SidebarRowPlan(Rows, Entries, Revision)
SidebarPane  ── per-row epochs from SidebarRowDiff.Diff   Data/SidebarRowDiff.cs:24
             ── extents from SidebarRowExtents.HeightOf   Data/SidebarRowExtents.cs:45
             ── selection from SidebarRowResolve.Sweep/Flipped  Data/SidebarRowResolve.cs:102/121
             ── pill from SidebarPillState.For            Data/SidebarPillState.cs:46
             ── drop cue from RootlistSlotResolver.Resolve + SidebarDropCue  RootlistSlotResolver.cs:163/275
             ── mid-drag publishes parked in SidebarStageHold  Data/SidebarStageHold.cs:30

SidebarLayoutStore  ── Commit(snapshot) coalesced on the pool, atomic File.Replace + .bak
                       Persistence/SidebarLayoutStore.cs:292-386
                    ── WriteCompleted → PersistenceHealth : Signal<SidebarWriteResult>
```

### 1.2 The same tree in 0.3 terms

Props freeze at mount, so the column below states how each child receives change. Across the five-file set (A4): `Sidebar.Customizer.UI.cs` holds this page's `Element`s and `Sidebar.UI.cs` the pane's; `Sidebar.cs` holds the CORE sections that shape rows (palette table, display table, planner, projection, sort/search, geometry, edit plan); `Sidebar.Doc.cs` holds the layout document, the reducer, the templates and the wire; `Sidebar.Host.cs` holds the store, the binder and the data sources. Where a row below says `Sidebar.UI.cs` for a control that belongs to THIS page, read `Sidebar.Customizer.UI.cs`.

| 0.3 node | file / form | inputs | change reaches it via |
|---|---|---|---|
| `Sidebar.CustomizerPage : Component` | `Shell/Sidebar.UI.cs` | ctor `(string? focusArg)`; contexts as today | it is the reference-stable holder; its own `Signal`s |
| `Sidebar.Customizer.Header(page)` | static fn in `Sidebar.UI.cs` | `page` | re-read of `Sidebar.LayoutVersion.Value` inside the page render |
| `Sidebar.Customizer.Banners(page)` | static fn | `page` | `_bannerEpoch` + `PersistenceHealth` signal |
| `Sidebar.PresetBlock : Component` | `Sidebar.UI.cs` | `page` | reads `Design`/`Edit.ShowContents` itself; mirrors into `UseSignal` from `UseLayoutEffect` |
| `Sidebar.TemplateList : Component` | `Sidebar.UI.cs` | `page` | reads `LayoutVersion` itself |
| `Sidebar.Palette : Component` | `Sidebar.UI.cs` | `page` | reads `page.PaletteQuery`, `LayoutVersion`, its own `_pickContribution` |
| `Sidebar.HiddenSections : Component` | `Sidebar.UI.cs` | `page` | reads `LayoutVersion` + `RejectEpoch` |
| `Sidebar.AdvancedBlock : Component` | `Sidebar.UI.cs` | `page` | reads `LayoutVersion` |
| `Sidebar.PropertyPanel : Component` | `Sidebar.UI.cs` | `ISidebarEditHost`, `scrollKey` | reads `LayoutVersion` + `RejectEpoch`; subject from `host.Selected.Value` |
| `Sidebar.Cz*Row : Component` (6 kinds) | `Sidebar.UI.cs` | `(host, sectionId[, field])` frozen | **`Key` remount** per `"opt:{field}:{sectionId}"`; live value via `CzRow.Subject` + layout-effect mirror on `DepKey.From(value, CzRow.Epoch(host))` |
| `Sidebar.CzNumberSpinner` | `Sidebar.UI.cs` | `(host, sectionId, field, min, max, authoritative)` | **`Key` remount** `"number:{sec}:{field}:{value}"` — `NumberBox` affixes freeze at mount |
| `Sidebar.CzConfigRow` | `Sidebar.UI.cs` | `SidebarConfigField` frozen | **`Key` remount** `"cfg:{sec}:{fieldKey}"` |
| `Sidebar.ItemPickerBody` / `ActionPickerBody` / `ActionPickerFooter` | `Sidebar.UI.cs` | shared `ActionPickerModel` (plain holder, no hooks) | signals on the model, read by BOTH body and footer |
| `Sidebar.Miniature.Template(layout)` | static fn, `Sidebar.UI.cs` | `SidebarCustomLayout` value | rebuilt per dialog open (a dialog is one-shot) |
| `Sidebar.Palette` table, `Sidebar.DisplayValues`, `Sidebar.NumberEdit`, `Sidebar.QueryPanelShape`, `Sidebar.ConfigJson` | CORE section of `Sidebar.cs` | pure | n/a |
| `Sidebar.Store` (layout json), `Sidebar.Binder`, `Sidebar.Sources.*` | `Sidebar.Host.cs` | shell | `Signal<int>` versions + `Signal<SidebarWriteResult>` |

**The one structural change 0.3 forces**: the palette's Destinations group and the item picker's Navigation tab today enumerate `SidebarPinId.PinnableRoutes` and resolve labels through `ShellNav.Dest`. In 0.3 those become reads of `Shell.Routes` (plan §4.11) — same table, new owner. The library tab of the item picker stops reading `prefs.Entries.Current` and becomes a scan over `User.Me` edges (§7).

---

## 2. Wireframes

Scale ≈ 8 DIP per monospace character. The content column is `720 − 16 − 16 = 688` DIP ≈ 86 chars. The column is **left-aligned** — `Body()` sets `MaxWidth` with no `Justify`/`AlignSelf` centring (`SidebarCustomizerPage.cs:384-386`), so at any window wider than 720 the column hugs the content host's left edge and the remainder is empty page ground.

### W1 — fully loaded, healthy, nothing selected @ 1280 window (content host ≈ 976 DIP)

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ h=64  pad L8 R16  gap 8  align center                                                                             │
│ ┌──┐  ┌────────────────────────────────────────────────┐                    ● Saved locally  ┌──┐┌──┐┌─────┐┌────┐│
│ │ ← │  │ WAVEE CURATED            ← Eyebrow 12/16/600   │                    6px  11f tert   │↶ ││↷ ││Reset││Done││
│ └──┘  │ Customize sidebar        ← 16f/600 TextPrimary │                                     └──┘└──┘└─────┘└────┘│
│ 24×24 └────────────────────────────────────────────────┘                      Small: h≥24, 12f, r4             Accent│
├───────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ 1 DIP StrokeDividerDefault
│                                                                                                                   │
│ ←16→┌──────────────────────────────────────────────────────────────────────────────────────┐  ← MaxWidth 720      │
│ ↑12 │ SIDEBAR DESIGN                                        ← GroupLabel 12/16/600 tert    │                      │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │  card r4             │
│     │ │ Customizing edits the Custom design, so opening this page   ← Wide: label above  │ │  FillCardSecondary   │
│     │ │ switched to it. Pick another to switch back…       13f, 2 lines max               │ │  1px StrokeCardDef   │
│     │ │  ┌──────────────┬──────────────┬──────────────┐   STOCK Segmented h34/14f/min52  │ │  pad 12/10/12/10     │
│     │ │  │   Classic    │   Library    │  ▣ Custom    │   accent pill 24×3 VISIBLE under │ │  MinHeight 44        │
│     │ │  └──────────────┴───────────────────▁▁▁▁──────┘   the selected segment (:775)    │ │                      │
│     │ ├──────────────────────────────────────────────────────────────────────────────────┤ │                      │
│     │ │ Show section contents                                              (  ●)  Toggle  │ │  Prop row           │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│ ↕16 │ START FROM A TEMPLATE                                                                 │                      │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │                      │
│     │ │ ◉  Wavee Curated                                                              ✓  │ │ active: Fill=       │
│     │ │    Pinned, Jump back in, library shortcuts and folder-aware playlists.            │ │ FillSubtleSecondary │
│     │ │ ▦  Classic-inspired                                                               │ │ pad 12/8/12/8, gap 8│
│     │ │    The familiar Wavee sidebar, now editable.                                      │ │ 13f/600 + 11f tert  │
│     │ │ ▦  Library-inspired                                                               │ │ radio glyph 14f     │
│     │ │    One filterable library list with pins on top.                                  │ │ inactive tail 16w   │
│     │ │ ▦  Minimal   · Three shortcuts and a compact playlist list.                        │ │                      │
│     │ │ ▦  Blank     · Start from nothing and add only what you want.                      │ │                      │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│ ↕16 │ ADD A SECTION                                                            12/40       │  gap 4 between head, │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │  box and card        │
│     │ │ Search sections                                                           h 30    │ │                      │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │ card r4, pad 4       │
│     │ │  PAGES                                            ← group header, margin 4/8/4/2 │ │                      │
│     │ │  ┌──┐ Home                                                                  ┌─┐  │ │ row pad 4/6/4/6      │
│     │ │  │⌂ │ Add a shortcut to this page                                           │+│  │ │ gap 8, r4            │
│     │ │  └──┘                                                                       └─┘  │ │ 24×24 plate / chip   │
│     │ │  ┌──┐ Search                                                                ┌─┐  │ │ chip Opacity .45     │
│     │ │  │⌕ │ Add a shortcut to this page                                           │+│  │ │                      │
│     │ │  └──┘                                                                       └─┘  │ │                      │
│     │ │  … Albums · Artists · Liked Songs · Podcasts · Local files · History · Recents    │ │ 9 pinnable +         │
│     │ │  … Settings · Concerts                     (API console only in developer mode)  │ │ 3 extra = 12 rows    │
│     │ │  NAVIGATION                                                                       │ │                      │
│     │ │  ┌──┐ Pinned            The items you pinned, in your order              ┌─┐      │ │                      │
│     │ │  │📌│                                                                    │+│      │ │                      │
│     │ │  └──┘                                                                    └─┘      │ │                      │
│     │ │  … Library shortcuts · Liked Songs · Links                                        │ │                      │
│     │ │  LIBRARY      Playlists · Library list · Spotlight                                 │ │                      │
│     │ │  PLAYBACK     Recently played · Queue · Now Playing                                │ │                      │
│     │ │  DYNAMIC      Jump back in · Artist top tracks · New releases · Concerts near you  │ │                      │
│     │ │  LAYOUT       Group · Heading · Divider                                            │ │                      │
│     │ │  ACTIONS      Action shortcut                                                      │ │                      │
│     │ │  EXTENSIONS   Extension                                                            │ │                      │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│ ↕16 │ HIDDEN SECTIONS                                       Hiding never removes anything  │  group caption 11f   │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │                      │
│     │ │ Nothing is hidden.                                              (enabled=false,   │ │ Opacity .4           │
│     │ └──────────────────────────────────────────────────── Opacity 0.4, IsEnabled=false)─┘ │                      │
│ ↕16 │ ADVANCED                                                                              │                      │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │                      │
│     │ │ Reset to template                                                     [ Reset ]  │ │ Standard · Small     │
│     │ │ This restores "Wavee Curated" and discards your changes. You can undo it.         │ │ sub 11f tertiary     │
│     │ ├──────────────────────────────────────────────────────────────────────────────────┤ │                      │
│     │ │ Layout file                                                                       │ │ Wide row             │
│     │ │ Your sidebar is saved here. Editing it by hand is unsupported…                    │ │                      │
│     │ │ C:\Users\…\AppData\Local\Wavee\WaveeMusic\sidebar-layout.json    11f tert, 2 lines │ │ gap 8 below label    │
│     │ │ [ Show in Explorer ]  [ Copy path ]                        Standard · Subtle       │ │                      │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│ ↓20 └──────────────────────────────────────────────────────────────────────────────────────┘                      │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W2 — the same page @ 640 window (content host ≈ 640 DIP) — no breakpoint, no hysteresis

```
┌─────────────────────────────────────────────────────────────────────┐
│ ┌──┐ WAVEE CURATED                      ● Saved… ┌──┐┌──┐┌────┐┌───┐│  title lane ellipsizes,
│ │ ← │ Customize sidebar                           │↶ ││↷ ││Rese││Don││  cluster holds width
│ └──┘                                              └──┘└──┘└────┘└───┘│  (Grow=1 Basis=0 Shrink=1
├─────────────────────────────────────────────────────────────────────┤   MinWidth=0 on the lane only)
│ ←16→┌──────────────────────────────────────────────────────────┐    │
│     │ SIDEBAR DESIGN                                            │    │ column = min(720, host)
│     │ ┌──────────────────────────────────────────────────────┐ │    │      − 32 padding
│     │ │ Customizing edits the Custom design, so opening this  │ │    │ = 608 content lane
│     │ │ page switched to it. Pick another to switch back…     │ │    │
│     │ │ ┌────────────┬────────────┬────────────┐              │ │    │ stock ItemMinWidth 52
│     │ │ │  Classic   │  Library   │ ▣ Custom   │              │ │    │ × 3 = 156 + padding, so
│     │ │ └────────────┴──────────────────▁▁▁────┘              │ │    │ it never clips even here
│     │ └──────────────────────────────────────────────────────┘ │    │
│     …  IDENTICAL ORDER AND ROW GEOMETRY, ONLY NARROWER          │    │ ComboWidth stays 264;
└─────────────────────────────────────────────────────────────────────┘ the label column absorbs
                                                                        the difference (Grow=1
   THE ONLY WIDTH-DEPENDENT DECISION ON THIS PAGE: CzRow.Choice picks    Basis=0 MinWidth=0)
   Segmented vs ComboBox from the RESOLVED LABELS, not from the width —
   ≤4 choices AND every label ≤12 chars → Segmented, else ComboBox.
   (SidebarCustomizerControls.cs:316-340)
```

### W3 — palette in contribution-pick mode (after clicking "Extension")

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│  ┌─ BackRow h 28, r4, Interaction.Subtle ─────────────────┐                  12/40   │  head unchanged:
│  │ ‹  Add a section          12f/600 TextSecondary        │                          │  the count stays
│  └────────────────────────────────────────────────────────┘                          │
│ ┌──────────────────────────────────────────────────────────────────────────────────┐ │  SEARCH BOX IS
│ │ Search sections                                                            h 30  │ │  UNCONDITIONAL
│ └──────────────────────────────────────────────────────────────────────────────────┘ │  (defect 5)
│ ┌──────────────────────────────────────────────────────────────────────────────────┐ │
│ │  Key = "palette-card:contributions"  Animate = MotionRecipes.PageSlideForward     │ │  enters from +8 DIP
│ │  ┌──┐ library                This extension declares no name yet (wavee.library) │ │  X, opacity 0→1,
│ │  │</>│                                                                     │+│   │ │  250 ms SmoothOut
│ │  └──┘                                                                      └─┘   │ │
│ │  ┌──┐ history.visited        …no name yet (wavee.history.visited)          ┌─┐   │ │  REGISTRATION ORDER,
│ │  ┌──┐ history.played         …no name yet (wavee.history.played)           ┌─┐   │ │  not palette order:
│ │  ┌──┐ playlistTree           …no name yet (wavee.playlistTree)             ┌─┐   │ │  WaveeBuiltInData-
│ │  ┌──┐ Artist top tracks      The most-played tracks of one artist          ┌─┐   │ │  Sources.RegisterAll
│ │  ┌──┐ newReleases            …no name yet (wavee.newReleases)              ┌─┐   │ │  :39-47 → registry
│ │  ┌──┐ concerts               …no name yet (wavee.concerts)                 ┌─┐   │ │  .Sources (insertion
│ │  ┌──┐ Queue                  What's playing next                           ┌─┐   │ │  order)
│ │  │≡ │                                                                      │+│   │ │
│ │  └──┘                                                                      └─┘   │ │  Only 3 of the 9 are
│ │  ┌──┐ Now Playing            The current track at a glance                 ┌─┐   │ │  NAMED (queue, now-
│ └──────────────────────────────────────────────────────────────────────────────────┘ │  Playing, artist top
└──────────────────────────────────────────────────────────────────────────────────────┘  tracks are the only
  Clicking ‹ sets _forward=false → the card re-mounts under Key "palette-card:sections"    palette entries that
  with Animate = PageSlideBack (enters from −8 DIP X).   Palette.cs:76, 301-312            carry a Contribution-
                                                                                          Id); the other SIX
  A RAW ID APPEARS AT MOST ONCE PER ROW — as the subtitle, never also as the title: an     show the contribution
  unnamed source's TITLE is SidebarContributions.ContributionOf(id) (the half after        half as the title and
  "wavee."), its SUBTITLE is Loc.Format(contributionUnnamed, id).   Palette.cs:284-296     the full id in the sub.

  EMPTY ARMS of this mode (neither is drawn above):
   • registry.Sources is null/0  → one Note "Manage extension" (sidebar.extension.manage)  Palette.cs:271-275
   • the query matches nothing   → the SAME "Nothing matches "{q}"" line as W4             Palette.cs:298
```

### W4 — palette search with no match · W5 — the section budget full

```
W4                                                            W5
┌──────────────────────────────────────────────┐   ┌──────────────────────────────────────────────┐
│ ADD A SECTION                        12/40   │   │ ADD A SECTION                        40/40   │
│ ┌──────────────────────────────────────────┐ │   │                                       ▲      │
│ │ zzz                                      │ │   │                       Tok.SystemFillCritical │
│ └──────────────────────────────────────────┘ │   │ ┌──────────────────────────────────────────┐ │
│ ┌──────────────────────────────────────────┐ │   │ │ (rows unchanged and still clickable)      │ │
│ │ Nothing matches "zzz"                    │ │   │ └──────────────────────────────────────────┘ │
│ │ 12f tert, wrap 3, margin 4/8/4/8         │ │   │ a click now returns SectionCapReached and    │
│ └──────────────────────────────────────────┘ │   │ raises the inline strip (W9); the palette    │
└──────────────────────────────────────────────┘   │ does NOT disable its rows.                   │
 Palette.cs:129, 314-320                            └──────────────────────────────────────────────┘
                                                     Palette.cs:57-58, 98-102
```

### W6 — Destinations header while a `StaticLinks` card is the canvas subject

```
│  PAGES              adds to "Quick links"                                          │
│  ▲                  ▲                                                              │
│  GroupLabel 12/16   TextEl 11f  Color = WaveeAccent.Decor (= Tok.AccentTextPrimary)│
│  600 tert, Shrink=0 Grow=1 Shrink=1 MinWidth=0 MaxLines=1 CharacterEllipsis        │
│  margin 4/8/4/2                                                                    │
└────────────────────────────────────────────────────────────────────────────────────┘
   The subject is prefs.Edit.OptionsSection ?? prefs.Edit.Expanded, filtered to
   Kind == StaticLinks and never the Shortcuts sentinel.   Page.cs:517-528
   In this state every Destinations row loses its DragSource (SidebarPalette.AppendsToSelection
   is true, so the click appends and a drag would mean something else).  Palette.cs:217
```

### W7 / W8 / W9 — the three banner states (all three can stack, in this order)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐ ← pad 16/8/16/0
│ ⚠  Your saved sidebar layout couldn't be read                                            ✕  │   gap 4 between bars
│    We kept the unreadable file at The sidebar layout contains invalid data (JsonException).  │
│    and loaded Wavee Curated instead. Your next change replaces it.                           │  InfoBarSeverity.Warning
│                                          [ Copy path ]  [ Start fresh ]   Subtle · Standard  │  Page.cs:332-351
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⛔ Your sidebar layout can't be saved right now                                           ✕  │  InfoBarSeverity.Error
│    The layout grew past its size budget. Shrink a section's settings to resume saving —      │  Page.cs:355-366
│    nothing has been lost.  (Document is 2200104 B, over the 2097152 B budget.)               │  SafeDetail in parens
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⓘ  Your sidebar is full. Remove a section before adding another.                         ✕  │  Informational
└──────────────────────────────────────────────────────────────────────────────────────────────┘  auto-dismiss 4000 ms
  When all three are absent the whole node is BoxEl{Key="banners", Height=0, Shrink=0}  Page.cs:371
  While a fault is up the header's "● Saved locally" is ABSENT (persistence.Success false). :225
  — a LOAD fault seeds PersistenceHealth non-Success at boot, so the dot never appears at all
    on a faulted launch.   SidebarPreferences.cs:647-665

  ★ W7 IS A DEFECT AS DRAWN, and 0.3 must fix it rather than port it. The bar interpolates
    `prefs.FaultDetail` into the {path} slot (Page.cs:334,338) — but FaultDetail is the STORE'S
    DIAGNOSTIC SENTENCE, never a path: "The sidebar layout contains invalid data (JsonException)."
    / "…could not be read (IOException)." / "Layout version 4 is newer than supported version 3."
    / "The sidebar layout has an invalid version (0)."   SidebarLayoutStore.cs:192-224
    So the sentence reads as above, and [ Copy path ] copies THAT STRING, not a path
    (Page.cs:345 → CopyPath(path) → Clipboard.SetText). The path the user needs is one call away
    (`SidebarLayoutStore.DefaultPath()`, which the Advanced block already prints). In 0.3: put the
    PATH in {path} and carry the diagnostic as a second, parenthetical clause — the same shape the
    save-fault bar already uses for SafeDetail.

  ★ ONE BAR, THREE FAULT KINDS. `SidebarLoadFault` is {None, Corrupt, TooNew, Unreadable}
    (SidebarLayoutStore.cs:15) and the page shows this bar for ANY non-None value, with the same
    "couldn't be read" wording — including TooNew, where the file is perfectly readable and owned
    by a NEWER build that this one must not touch (Store.cs:161-170). "Start fresh" on a TooNew
    document moves a newer build's file aside, which is a real (if rare) data event. 0.3 should
    either give TooNew its own sentence or drop its Start-fresh button.
  ★ A .bak RECOVERY SHOWS NO BAR AT ALL: a malformed primary with a good backup loads the backup,
    keeps writes ENABLED, logs `sidebar.layout.recovered`, and returns Fault.None — the user sees
    the healthy header with the saved dot.   SidebarLayoutStore.cs:176-184
```

### W10 — header states, side by side

```
healthy           ● Saved locally   [↶][↷][ Reset ][ Done ]     dot 6×6 Radii.Circle(6) SystemFillSuccess
                                                                 label 11f Tok.TextTertiary, margin R4
faulted                             [↶][↷][ Reset ][ Done ]     indicator gone; the InfoBar speaks
nothing to undo                     [↶̶][↷̶][ Reset ][ Done ]     IconButton isEnabled:false
after "Add section"  tooltip on ↶ = "Undo: Add section"          Loc.Format(CzLoc.UndoOf, action)
after undo           tooltip on ↷ = "Redo: Add section"          SidebarPreferences.cs:566-571
```

### W11 — template / reset confirmation (`ContentDialog`, `DialogWidth = 480`)

```
                    ┌──────────────────────────────────────────────────────┐
                    │  ← ContentDialogPadding 24, OverlayCornerRadius 8    │
                    │  Apply "Minimal"?                     dialog title   │
                    │                                                      │
                    │  This replaces your current sections. You can undo   │  13f Tok.TextSecondary
                    │  it.                                    wrap, ≤3 ln  │  MinWidth = BodyW 432
                    │  ┌────────────────────────────────────────────────┐  │
                    │  │ ┌──────────┬──────────────────────────────────┐│  │  SidebarMiniature.Template
                    │  │ │ pane 258 │  workspace (fake hero + 3 cards) ││  │  h 220, r8,
                    │  │ │ FillCard │  pad 16, gap 16                  ││  │  Fill FillCardDefault,
                    │  │ │ Secondary│  ┌──┐ ████████                   ││  │  1px StrokeCardDefault
                    │  │ │ 1px card │  │64│ ██████████████             ││  │
                    │  │ │ stroke   │  └──┘ ███████████                ││  │  pane: pad 8/12/8/12,
                    │  │ │ pad 8/12 │                                  ││  │  gap 2, ClipToBounds,
                    │  │ │ SHORTCUTS│  ┌──┐ ┌──┐ ┌──┐                  ││  │  ≤14 rows (:100)
                    │  │ │ ⌂ Home   │  │64│ │64│ │64│                  ││  │
                    │  │ │ ⌕ Search │  └──┘ └──┘ └──┘                  ││  │  rows use the REAL
                    │  │ │ ────────  │  ██    ██    ██                  ││  │  SidebarRowGeometry
                    │  │ │ PLAYLISTS│                                  ││  │  .HeightFor(density,
                    │  │ │ ▾ 📁 …    │                                  ││  │   subtitles) ladder
                    │  │ │   ▣ …     │                                  ││  │  and SidebarCover art
                    │  │ └──────────┴──────────────────────────────────┘│  │  from FakeData
                    │  └────────────────────────────────────────────────┘  │
                    │                                  [ Cancel ] [ Apply ]│  DefaultBtn = Primary
                    └──────────────────────────────────────────────────────┘
  SKIPPED ENTIRELY when SidebarLayoutCompare.EqualTemplateSectionsIgnoringIds(current,
  Build(current.TemplateId)) — a pristine document has nothing to lose.   Page.cs:672-686
  Dialog buttons: PrimaryText = "Apply" / "Reset", CloseText = Cancel, DefaultBtn = Primary.  :714-717

  THE MINIATURE'S OWN THREE BRANCHES (Shared/SidebarMiniature.cs), none of which W11 draws:
   1. BLANK TEMPLATE (0 rows) — the pane column is NOT empty: it centres Icons.Add 20f
      Tok.TextTertiary over the template's own description at 12f tertiary, wrap ≤2, gap 8.
      This is the ONLY arm where the pane shows no rows at all.                        :66-79
   2. A GRID SECTION (Opts.Presentation == Grid) draws TWO 48-DIP cells side by side (gap 4,
      pad 4, r4, FillSubtleSecondary, S40 art + an 11f 2-line label) INSTEAD of rows, and
      returns — no title rows below it.                                              :123-131, 270-288
   3. A HIDDEN section is skipped outright (`if (section.Hidden) continue;`), so the miniature
      shows the layout as the SIDEBAR will render it, not as the editor lists it.        :103
  Per-kind sampling, verbatim: Pinned → FakeData.Playlist(1) + Artist(3) (circular);
  PlaylistTree → a folder header row ("Playlists", chevron 10f, S28 folder, count "2") then
  Playlist(5) at depth 1 with a 12-DIP tree guide; Shortcuts/StaticLinks → the first ≤3 items
  through ShellNav.Dest + SidebarIcons.For, counts from FakeData.LibraryStats(); everything
  else → Playlist(index + 6).  Workspace: hero Playlist(8), cards Playlist(10/12/14).  :132-165, 290-342
```

### W12 / W13 — the item picker (`ContentDialog`, 480 wide, body 432)

```
┌──────────────────────────────────────────────────────┐   ┌──────────────────────────────────────────────────────┐
│  Add…                                                │   │  Add…                                                │
│  ┌──────────────────┬──────────────────┐             │   │  ┌──────────────────┬──────────────────┐             │
│  │   Navigation     │     Library      │ SelectorBar │   │  │   Navigation     │     Library      │             │
│  └══════════════════┴──────────────────┘             │   │  └──────────────────┴══════════════════┘             │
│  ┌────────────────────────────────────────────────┐  │   │  ┌────────────────────────────────────────────────┐  │
│  │ Search in Your Library                   h 32  │  │   │  │ zzz                                       h 32 │  │
│  └────────────────────────────────────────────────┘  │   │  └────────────────────────────────────────────────┘  │
│  ┌─ ScrollView h 320, AutoEdgeFade ───────────────┐  │   │  ┌────────────────────────────────────────────────┐  │
│  │ ⌂  Home                              row h 40 │  │   │  │ No results for "zzz"      12f tert, wrap ≤3    │  │
│  │    home            ← 11f tert SUB    gap 2    │  │   │  │ margin 8/8/8/0                                 │  │
│  │ ⌕  Search                            pad 8/0  │  │   │  │ (empty library instead → "Nothing in Your      │  │
│  │    search                            icon 14f │  │   │  │  Library yet")                                 │  │
│  │ ▣  Albums / albums                   13f/11f  │  │   │  └────────────────────────────────────────────────┘  │
│  │ ☺  Artists / artists                          │  │   │                                                      │
│  │ ♡  Liked Songs / liked                        │  │   │  Library rows carry a subtitle = e.Creator.          │
│  │ 📻 podcasts · 💾 local · 🕐 history            │  │   │  Folders, tracks and app routes are EXCLUDED;        │
│  │ 🕐 recents · ⚙ settings · 📅 concerts          │  │   │  shown is capped at 200.  Pickers.cs:184-199         │
│  │ (API console only in developer mode)          │  │   │                                          [ Cancel ]  │
│  └────────────────────────────────────────────────┘  │   └──────────────────────────────────────────────────────┘
│                                          [ Cancel ]  │
└──────────────────────────────────────────────────────┘
  PrimaryText = "" (NOT null — null falls back to a localized "OK").  Pickers.cs:41-44
  DefaultButton = DefaultBtn.Close, so Enter and Esc both cancel.     Pickers.cs:45
  A row CLICK commits and closes; there is no OK.

  ★ EVERY NAVIGATION ROW CARRIES ITS RAW ROUTE KEY AS AN 11f TERTIARY SECOND LINE — PickerRow is
    called as PickerRow(glyph, title, routeKey, …) (Pickers.cs:171), and that same key is half of
    the search predicate (Matches(q, title, routeKey), :170), which is why typing "liked" finds
    Liked Songs. The palette's Destinations group does NOT do this: there the subtitle is the one
    shared "Add a shortcut to this page". Two lists over the same 12 routes, two subtitle rules —
    port both deliberately, or make the picker say the same sentence the palette does.
  ★ The no-match line interpolates the NORMALIZED query (trimmed + lower-cased by
    SidebarPalette.NormalizeQuery), so typing "ZZZ" renders `No results for "zzz"` (Pickers.cs:125,198).
    The PALETTE's empty line interpolates the RAW text and keeps the user's casing (Palette.cs:314).
```

### W14 — the action picker (body 260-tall list + a live footer)

```
┌────────────────────────────────────────────────────────────────┐
│  Action shortcut                                               │
│  ┌─ ScrollView h 260, gap 2, AutoEdgeFade ────────────────────┐│  13 ROWS, REGISTRATION ORDER
│  │ ┃ ┌────┐ Play                                     ✓      │  │  (the §1.1 table)
│  │ ┃ │ ▶  │ A chosen item · A chosen track                  │  │  selected row:
│  │ ┃ └────┘          11f tert, 1 line                       │  │   3×20 accent bar r1.5
│  │   ┌────┐ Play next                                       │  │   28×28 plate AccentSubtle
│  │   │ ⬚  │ A chosen track                    h 48          │  │   glyph AccentTextPrimary
│  │   └────┘  ▲ U+E900, app-local WaveeIcons face            │  │   Fill  SelectedRest
│  │   ┌────┐ Play after   ← NOT "Add to queue"               │  │   Hover SelectedHover
│  │   │ ⬚  │ A chosen track   ▲ U+E901, same face            │  │   Press SelectedPressed
│  │   └────┘                                                 │  │   BrushTransitionMs 83
│  │   ┌────┐ Save to Liked Songs                             │  │  unselected: 14-wide spacer
│  │   │ ♡  │ A chosen track · Now Playing                    │  │
│  │   └────┘  ▲ ALWAYS the UNCHECKED variant here            │  │  a destructive row WOULD tint
│  │   ┌────┐ Save to Your Library                            │  │  its glyph critical — but NO
│  │   │ +  │ A chosen item                                   │  │  0.2.9 row is destructive
│  │   └────┘  ▲ rows 4/5 differ ONLY by label +              │  │  (§1.1), so the arm never
│  │            caption (round-2 defect 6b)                   │  │  fires
│  │     … Open · Go to album · Go to artist                  │  │
│  │     … Copy link · Go to song radio                       │  │  10/11 share Icons.RadioTower;
│  │     … Go to artist radio                                 │  │  the CAPTION is the only thing
│  │     … Pin to sidebar · Unpin from sidebar                │  │  telling them apart
│  └──────────────────────────────────────────────────────────┘  │  12/13 = an absolute-state PAIR
│  ────────────────────────────────────────────────  Divider     │
│  Acts on                                                       │
│  ┌───────────────────────────────────────────────────────────┐ │  CzRow.Wide + CzRow.Choice
│  │ A chosen item                                      ▾ 264 │  │  → always ComboBox here
│  └───────────────────────────────────────────────────────────┘ │  (labels are sentences)
│  A chosen item                            [ Add… ]  Standard   │  CzRow.Prop; label = the
│                                                                │  resolved target name
│  ⚠ Nothing is playing.                                         │  ReasonRow — only when Ready()
│    glyph 12f AND text 11f, BOTH Tok.TextTertiary, ≤2 lines     │  and the binding won't resolve.
│    The sentence is ALWAYS one of the 7 in §1.1's reason        │  (:529-550)
│    table — never free text, never a fabricated hint.           │
│                                                                │
│                          [  Cancel  ] [    Add    ]   96×32    │  Footer owns both buttons;
└────────────────────────────────────────────────────────────────┘  Add disabled until Ready()
  All three ContentDialog built-in buttons are suppressed with "" (Pickers.cs:80-83), because
  IsPrimaryButtonEnabled is read once at card-build time and cannot follow live state.

  STATES THIS BODY HAS BEYOND THE ONE DRAWN:
   • registry.Actions null/0     → the 260-tall list holds ONE Note "Manage extension".  :379-380
   • nothing selected yet        → NO divider, NO "Acts on", NO target row, NO reason row: the whole
                                    tail is built only `if (descriptor is not null)`.     :388-395
   • the action accepts ≤1 mode  → ModeRow returns BoxEl{Height=0} — no "Acts on" at all.  :484
   • mode needs no target        → TargetRow is not built (NeedsTarget = FixedEntity|FixedTrack). :393
   • a destructive action        → its 28×28 plate glyph is Tok.SystemFillCritical while UNSELECTED,
                                    and Tok.AccentTextPrimary once selected.               :463-466
   • re-binding an existing row  → the model opens with that action selected and its stored mode
                                    and target pre-filled (`existing`).                   :260-266
   • picking a DIFFERENT action  → Choose() resets the mode to the descriptor's FIRST accepted mode
                                    and CLEARS the target, so a stale target can never commit.  :278-290
   • an action needing CONFIRMATION → unreachable in 0.2.9 (nothing sets RequiresConfirmation). Once one is
                                    bound, Resolve() returns HostUnavailable while ActionServices.Overlay is
                                    null, so the ReasonRow reads "Not available right now" instead of letting
                                    a binding commit that would later run UNCONFIRMED.  Descriptor.cs:109-110
   • a DESTRUCTIVE action        → likewise unreachable in 0.2.9 (nothing sets Destructive). Keep both arms.

  WHAT THE PICKER DOES **NOT** OFFER, and why (all §1.1):
   • the mode "Nothing (a global action)" — no first-party descriptor accepts WaveeActionTargetModes.None,
     so the label exists in the catalog (sidebar.customizer.targetNone) and never renders.
   • AddToPlaylist / RemoveFromThisPlaylist / RemoveFromQueue / SelectAll / ViewCredits / Rename /
     TogglePlaylistPublic / InviteCollaborators / DeletePlaylist / the Video ▸ verbs — deliberately NOT
     registered (BuiltInExtensionTable.cs:16-30). Their absence IS the design, not a missing row.
   • anything from AppActions.All — bound UI resolves ONLY through the registry (WaveeExtensionRegistry.cs:15-17);
     a picker enumerating AppActions would offer verbs no stored binding can honestly re-target after a restart.
```

### W15 — the property surface, fully populated (`EntityList`), as the pane's options popover

```
┌──────────────────────────────────────────────┐  the panel is width-agnostic; its host gives it
│ ▣  Library list                         ⋯   │  ~320 DIP, which is what ComboWidth 264 and
│    Library list        h 52, pad 12/0/8/0   │  SliderLength 272 are sized for (320 − 2 border
│    13f/600 + 10f tert                       │  − 16 inset − 24 row padding = 278)
├──────────────────────────────────────────────┤
│ GENERAL                                      │  ScrollView gap 12, pad 0/4/0/24, AutoEdgeFade
│ ┌──────────────────────────────────────────┐ │
│ │ Rename section                            │ │  CzRow.Wide: label 13f + sub 11f, then control
│ │ Enter to rename · Esc to cancel            │ │  TextBox w 264 h 32, MaxLength 60,
│ │ ┌──────────────────────────────────────┐  │ │  Placeholder = the effective title,
│ │ │ My mixes                             │  │ │  CommitOnLostFocus = true
│ │ └──────────────────────────────────────┘  │ │
│ ├──────────────────────────────────────────┤ │
│ │ Hidden                            (  ●)   │ │  CzRow.Prop
│ │ Keep it in the editor but remove it       │ │
│ │ from the live sidebar.                    │ │
│ └──────────────────────────────────────────┘ │
│ CONTENT                            3 items   │  ← the count rides the GROUP LABEL, never a row
│ ┌──────────────────────────────────────────┐ │
│ │ ☑ Playlists  ☐ Albums                     │ │  CheckBox stack, gap 2, pad 12/8/12/8
│ │ ☐ Artists    ☐ Podcasts                   │ │
│ │ Sort                                      │ │  5 choices → always a ComboBox; the 5th
│ │ [ Recents                          ▾ 264 ]│ │  ("Custom order") is itemEnabled ONLY for
│ │ Reverse order                     (●  )   │ │  PlaylistTree or a playlists-ONLY query.
│ │ Playlists by                              │ │  Key "sort:custom"|"sort:nocustom" — the
│ │ [ Anyone                           ▾ 264 ]│ │  itemEnabled array FREEZES at mount.
│ │        ▲ Reverse order is DISABLED (0.4   │ │  shown only while QualifiersAvailable
│ │          opacity, IsEnabled=false) while  │ │  (PropertyPanel.cs:508-533; the qualifier
│ │          Sort == Custom order (:533)      │ │   set is 4 labels but "By Spotify" busts
│ │                                           │ │   the 12-char budget → ComboBox)
│ │ ┌────┐ Jazz essentials            ⋯  🗑   │ │  item row: pad 12/10/12/10, gap 6
│ │ │ ♫  │ 24×24 plate, glyph 13f              │ │  title 13f, reason 11f tert when inert
│ │ └────┘ [ Use the original name    h 30 ]  │ │  label TextBox w 264 h 30
│ │ [   Add…   ][  Action shortcut  ]  Grow=1 │ │  Standard + Subtle, pad 12/8/12/12
│ └──────────────────────────────────────────┘ │
│ APPEARANCE                                   │
│ ┌──────────────────────────────────────────┐ │
│ │ Density                                   │ │  3 choices, longest "Comfortable" = 11 ≤ 12
│ │ ┌────────┬────────┬──────────────┐        │ │  → Segmented h30/12f, pill suppressed
│ │ │Compact │  Cozy  │ Comfortable  │        │ │
│ │ └────────┴────────┴──────────────┘        │ │
│ │ Layout    [ List ][ Grid ]                │ │  2 choices → Segmented
│ │ Show artwork                      (●  )   │ │
│ │ Show subtitles                    (●  )   │ │
│ │ Show counts                       (  ●)   │ │
│ │ Sort and view options             (  ●)   │ │  InlineControls, EntityList only
│ └──────────────────────────────────────────┘ │
│ BEHAVIOR                                     │
│ ┌──────────────────────────────────────────┐ │
│ │ Maximum items                       All   │ │  CzRow.Ranged: header + live caption 12f/600
│ │ ├──●───────────────────────────────┤ 272  │ │  Slider 0…500, Step 1, LargeChange 10,
│ │ When empty                                │ │  thumb tooltip converts 0 → "All"
│ │ [ Recommended                      ▾ 264 ]│ │  4 choices but "Show a compact hint" = 21 > 12
│ │ Show in collapsed rail            (●  )   │ │  → ComboBox
│ │ Contributes an icon or artwork to the     │ │
│ │ collapsed rail.                           │ │
│ │ Collapse section                  (  ●)   │ │  LIVE state, SetSectionCollapsed
│ └──────────────────────────────────────────┘ │
│ [ 🗑 Remove section ]     pad 8/12/8/24       │  CzRow.Danger: SystemFillCritical ink on
└──────────────────────────────────────────────┘  SystemFillCriticalBackground, Subtle geometry
```

### W16 — property surface, no subject

```
┌──────────────────────────────────────────────┐
│                                              │  Grow=1, centred both axes,
│                    ⚙  20f Tok.TextTertiary   │  pad 16/20/16/20, gap 8
│                                              │
│      Select a section to edit its options.   │  12f Tok.TextTertiary, wrap ≤3
│                                              │
└──────────────────────────────────────────────┘  PropertyPanel.cs:111-123
```

### W17 — palette row: rest / hover / pressed / focused

```
rest      ┌──┐ New releases                                              ┌─┐    plate FillSubtleSecondary
          │▣ │ Fresh music from the artists you follow                   │+│    chip Opacity 0.45
          └──┘                                                           └─┘    row Fill transparent

hover     ┌──┐ New releases                                              ┏━┓    row Fill → FillSubtleSecondary
          │▣ │ Fresh music from the artists you follow                   ┃+┃    chip Opacity 0.45 → 1.0 over
          └──┘                                                           ┗━┛    83 ms (HoverOpacity/HoverDurationMs)
          ▲ Interaction.ListRow StateBrush(transparent, FillSubtleSecondary, FillSubtleTertiary, transparent)

pressed   ┌──┐ New releases                                              ┏━┓    row Fill → FillSubtleTertiary
                                                                                 chip stays lit

focused   ╔══════════════════════════════════════════════════════════════════╗   engine focus visual on the
          ║ same as rest                                                     ║   row node (Focusable = true,
          ╚══════════════════════════════════════════════════════════════════╝   Role = AutomationRole.Button)
  The chip is HitTestVisible = false on purpose: the whole row adds, and the "+" only says so.
  A HoverFill on the chip would never be reached — the engine cascades a container's hover to a
  descendant ONLY for an opacity/scale reveal.   Palette.cs:361-372
```

### W18 — a palette chip dragged onto the canvas

```
   COMPANION PAGE (content host)                    DOCKED PANE (the canvas, 280 DIP)
   ┌──────────────────────────────────┐             ┌──────────────────────────────┐
   │  DYNAMIC                          │             │ ┌──────────────────────────┐ │
   │  ┌──┐ New releases          ┌─┐  │             │ │ ⠿ 📌 Pinned          👁 ⋯ │ │  each SectionCard is
   │  │▣ │ …                     │+│  │  drag ───▶  │ ├──────────────────────────┤ │  its OWN drop target
   │  └──┘                       └─┘  │             │ │▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔│ │  "Add here"
   │       ▲ DragSource armed at       │             │ │ ⠿ 🕐 Jump back in    👁 ⋯ │ │  → insert BEFORE this
   │         2× the normal threshold   │             │ ├──────────────────────────┤ │     card
   │         (Drag.ClickPrimary        │             │ │ ⠿ 📁 Playlists       👁 ⋯ │ │
   │          ThresholdMultiplier = 2) │             │ └──────────────────────────┘ │
   └──────────────────────────────────┘             └──────────────────────────────┘
   chip caption = the already-localized entry label, composed ONCE at promotion (never per move)
   accept caption  "Add here"        sidebar.customizer.dropHere   Pane/SidebarPaneText.cs:206
   refusal caption "Your sidebar is full — remove a section first"  …dropFull                :207
   payload = SidebarSectionDropPayload(Kind, Label, Item?, Extension?)   Data/SidebarEditPlan.cs:55-59
   drop → SidebarEditPlan.ToAddSection(document, beforeSectionId, payload)                   :221-242

   CLICK-ONLY rows (SidebarPalette.CanDrag false): Links (opens a picker), Action shortcut
   (opens a picker), Extension (switches the palette's mode), Recently played (TWO commands).
   Those rows carry no DragSource at all rather than a drag that lies.   CustomizerLayout.cs:284-285
```

### W19 — Hidden sections, populated

```
│ HIDDEN SECTIONS                                       Hiding never removes anything  │
│ ┌──────────────────────────────────────────────────────────────────────────────────┐ │
│ │ 📅  Concerts near you                                                  [ Show ]  │ │  icon 16f TextSecondary
│ │     Hidden                                                      Standard · Small │ │  title 13f / sub 11f
│ ├──────────────────────────────────────────────────────────────────────────────────┤ │
│ │ ♫   Unrecognized section                                               [ Show ]  │ │  a kind this build does
│ │     Hidden                                                                       │ │  not know: Icons.MusicNote
│ └──────────────────────────────────────────────────────────────────────────────────┘ │  + CzLoc.UnknownSection
  The list recurses exactly once (depth-1 by construction) so a hidden child of a
  CustomGroup is reachable too.   Page.cs:837-845
```

### W20 — inline reject strip timing (the one motion-visible transient)

```
t=0     click "Pinned" at 40/40  →  reducer returns SectionCapReached
t=0     _rejectKey set, RejectEpoch++ → Banners() re-renders, InfoBar enters
        (engine InfoBar enter; the page adds no transition of its own)
t=0     every controlled Cz* row in the re-hosted panel re-runs its mirror effect
        (dep key carries CzRow.Epoch) and SNAPS BACK to the document value
t=4000  UseTimeout fires ClearReject → _rejectKey=null, RejectEpoch++ → bar leaves
        A NEW rejection before t=4000 re-arms the timer (DepKey.From(rejectEpoch)).
```

### W21 — the pipeline's visual outcomes in the pane, as a ladder (owner: `25-sidebar.md` draws them; this chapter owns WHEN)

```
section state                     what the planner emits                        height (SidebarRowExtents)
──────────────────────────────────────────────────────────────────────────────────────────────────────────
slice.NeedsPrompt (ANY state)  →  PromptRow  ← TESTED FIRST, so a Pending source  48 (Concerts) / 56 (else)
                                  that needs a prompt never shows a skeleton      (Planner.cs:380)
source State == Pending        →  Skeleton × 3                                   3 × HeightFor(section.Opts)
Ready, rows > 0                →  the rows                                        HeightFor(section.Opts) each
Ready, 0 rows, Pinned          →  Empty (which IS the pin drop zone)               56 (72 while a drag is live)
Ready, 0 rows, HideBody        →  Empty                                            0
Ready, 0 rows, ActionCard      →  Empty                                            HeightFor(section.Opts)
Ready, 0 rows, default/compact →  Empty (the quiet hint)                           32
NeedsPrompt (Concerts, no loc) →  PromptRow                                        48
Missing/Disabled/Incompatible  →  PromptRow "Manage extension"                     56 (carries a reason line)
Error + a last-good snapshot   →  the snapshot rows, Availability = Cached         as Ready
──────────────────────────────────────────────────────────────────────────────────────────────────────────
 SidebarRowPlanner.cs:199 (SkeletonRows=3), :380-382 (the three-arm empty branch) · SidebarRowExtents.cs:66-91
 (kind→extent), :105-117 (EmptyHeight) · SidebarBinderPipeline.cs:252-312 (Resolve + the Cached replay)
 Geometry LINE refs (not values): SidebarRowGeometry.cs EmptyHintHeight :165, PinDropZoneRestHeight :169,
 PromptHeight :191. The VALUES are 32 / 56 (→72 mid-drag, SidebarPinDropZone.ActiveHeight :39) / 48|56.
 PromptHeight's argument is `section.Kind != Concerts` — the ladder claims the taller 56 for every
 NON-Concerts prompt, because only a contribution prompt can carry a reason line (Extents.cs:84-87).
```

---

### W22 — what a BOUND action row looks like once the picker has committed (the descriptor's visible outcome)

The picker's whole job is to produce a `SidebarActionBinding`. This is what that binding then draws, in the two hosts
this chapter's claims reach. Both are `25-sidebar.md`'s to render, but the STATES are the descriptor's, so they belong
beside the picker that created them.

```
IN THE DOCKED PANE (SidebarPaneSlot.ActionRow, :703-762) — height = the SECTION's row height (iron rule 4)

  available                      ┌─────────────────────────────────────┐
                                 │ [icon 16f]  Play                    │   label   = LabelOverride, else
                                 └─────────────────────────────────────┘             descriptor.Label()
                                   ▲ leading ladder: IconOverride →              icon    = descriptor.Icon()
                                     ThemedIcon (if registered) → glyph +        Enabled = resolution.Available
                                     icon.Font → Icons.MusicNote                 OnClick = registry.Execute
                                     Tok.TextSecondary   SidebarPaneText.cs:226-236

  unavailable (THE RULE)         ┌─────────────────────────────────────┐
                                 │ [icon 16f]  Play                    │  ← ToolTip.Wrap(row, reason, grow: 1f)
                                 └─────────────────────────────────────┘    :757-762
                                   ▲ Tok.TextTertiary (enabled: false)      tooltip = one of the 7 sentences
                                   THE ROW STILL RENDERS. It never           `grow: 1f` is LOAD-BEARING: the wrapper
                                   vanishes — a vanishing row makes the      is a flex ROW, so without it the disabled
                                   user's own sidebar look broken.           arm shrinks to its own label while the
                                                                             enabled arm fills the pane — one row kind
                                                                             at two widths depending on availability

  no binding at all              label falls through to "Manage extension"; reason = "This action is no longer
  (a hand-edited document)       available"  (ExtensionMissing = sidebar.action.unavailable.missing)          :718-721

  no registry / no service bag   reason = "Not available right now"  (ExtensionNotNow = ...unavailable.notNow)
  yet (a cold shell frame)       — the DEFAULT, assigned before any lookup, so the row is disabled-with-a-
                                 sentence and never blank                                                     :713-715

IN THE PROPERTY PANEL's item list (CzItemRow, SidebarPropertyPanel.cs:775-981) — the SAME binding, drawn differently

  ┌──────────────────────────────────────────────────────────┐
  │ [24×24 plate]  Play                             ⋯   🗑   │   title  = LabelOverride, else descriptor.Label(),
  │   glyph 13f    This action is no longer available        │            else the RAW binding.ActionKey
  └──────────────────────────────────────────────────────────┘            (= ProviderId + '.' + ActionId)   :932-942
      ▲ ALWAYS Icons.RefineSparkle for an Action item unless the          reason = Registry.Resolve(...).ReasonLocKey,
        user set an IconOverride — the EDITOR does not use the                     11f, ≤2 lines, row Opacity 0.7
        descriptor's own glyph (:951-961). The PANE does. Two hosts,      :963-972
        one binding, two marks: a deliberate divergence to port, or
        to unify ON PURPOSE (see §1.1's last bullet).
```

Three parity-relevant consequences:

1. **The reason text is host-dependent in FORM, never in WORDS.** The pane shows it as a tooltip on the whole row, the
   property panel as a second line inside the row, the picker as a `ReasonRow` under the target, and
   `ToMenuItem` as the menu row's **accelerator column** (`WaveeActionDescriptor.cs:150-168`). Four presentations, one
   7-sentence vocabulary.
2. **An unresolvable binding still round-trips.** The binding stores `ProviderId` and `ActionId` as two halves precisely
   so a currently-missing extension survives a save (`SidebarItemPickers.cs:339-352`), and `WaveeExtensionKey.Compose`
   is the exact inverse (`WaveeRegistryTable.cs:85-93`). Re-installing the extension makes the row live again with no
   edit.
3. **`LabelOverride` beats the descriptor everywhere.** A renamed shortcut keeps its name even when the action behind it
   disappears — which is why the "This action is no longer available" line is a *second* line and not a replacement
   title.

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source file:line |
|---|---|---|---|---|---|---|---|
| page root | Grow 1, Direction 1, ClipToBounds | — | — | — | inherits the content host's ground | none | `Curated/SidebarCustomizerPage.cs:172-177` |
| header bar | h **64** | pad 8/0/16/0, gap 8 | — | — | — | none | `:245-249` |
| back button wrapper | Shrink 0 | — | — | — | — | — | `:259-268` |
| back / undo / redo | `ControlSize.Small` → min-h 24, icon 14 | pad 7/2/7/3 | 4 | — | `IconButton` default | — | `:232-235`, `ControlSize.cs:41` |
| Reset button | Small | — | 4 | 12f | `ButtonAppearance.Subtle` | — | `:236` |
| Done button | Small | — | 4 | 12f | `ButtonAppearance.Accent` | — | `:238` |
| eyebrow (template) | 12 / 16 / 600, tracking 30 | — | — | `WaveeType.Eyebrow` | `WaveeAccent.Decor` = `Tok.AccentTextPrimary` | — | `:211-215`, `Design/WaveeType.cs:38,54`, `WaveeTokens.cs:50` |
| page title | 16f / 600, 1 line, char-ellipsis | — | — | — | `Tok.TextPrimary` | — | `:216-220` |
| saved dot | 6 × 6 | margin R 4, gap 4 | `Radii.Circle(6)` = 3 | — | `Tok.SystemFillSuccess` | — | `:311-315` |
| saved label | 11f, 1 line | — | — | — | `Tok.TextTertiary` | — | `:316-319` |
| divider | h 1, stretch | — | — | — | `Tok.StrokeDividerDefault` | — | `Dsl/Factories.cs:177-179` |
| banner stack | Shrink 0 | pad 16/8/16/0, gap 4 | — | — | — | `InfoBar` (engine) | `:372-377` |
| empty banner node | h **0** | — | — | — | — | — | `:371` |
| content column | MaxWidth **720**, left-aligned | pad 16/12/16/20, gap 16 | — | — | — | `ScrollView` + `AutoEdgeFade` | `:383-398` |
| group label | 12 / 16 / 600, tracking 30, 1 line | margin 4/8/4/0 | — | `WaveeType.Eyebrow` | `Tok.TextTertiary` | — | `Curated/SidebarCustomizerControls.cs:103-106,120` |
| group caption (trailing) | 11f, 1 line, Shrink 0 | — | — | — | `Tok.TextTertiary` | — | `:124-128` |
| group card | Direction 1, Shrink 0, ClipToBounds | — | `Radii.ControlAll` = 4 | — | `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault` | none | `:132-139` |
| property row (`Prop`) | MinHeight **44** | pad 12/10/12/10, gap 12 | — | — | Opacity 0.4 + `IsEnabled=false` when disabled | — | `:208-221` |
| property row (`Wide`) | MinHeight 44, AlignItems Stretch | pad 12/10/12/10, inner gap 6 | — | — | — | — | `:165-179,208-221` |
| row label | 13f, ≤2 lines, wrap + ellipsis | inner gap 1 | — | — | `Tok.TextPrimary` | — | `:186-192` |
| row sublabel | 11f, ≤2 lines, wrap + ellipsis | — | — | — | `Tok.TextTertiary` | — | `:194-199` |
| row leading icon | 16f | — | — | — | `Tok.TextSecondary` | — | `:150` |
| row chevron (clickable rows) | 12f | — | — | — | `Tok.TextTertiary` | — | `:158` |
| `Ranged` row | Shrink 0 | pad 12/8/12/8, gap 4 | — | label 13f 1 line / caption 12f 600 | `Tok.TextPrimary` / `Tok.TextSecondary` | — | `:259-283` |
| Segmented (compact) — `CzRow.Choice` ONLY | h **30**, item min-w **40**, font 12 | style pad 2 | outer `Radii.Control` 4, item `Radii.Control-1` = 3 | — | `Segmented.DefaultStyle` colours | selection **pill suppressed** (`Fill` transparent, `Width` 0); the 3-DIP pill slot stays, so suppressing costs no relayout | `:300-313`, `Segmented.cs:62-76,203-220` |
| Segmented (**stock**) — the PAGE's design row | h **34**, item min-w **52**, font 14, icon 16, icon gap 8 | style pad 2 | 4 / 4 | — | `Segmented.DefaultStyle` | pill **VISIBLE**: 24 × 3, `CornerRadius4.All(1.5)`, `Tok.AccentDefault`, `Animate = PillTransition` | `SidebarCustomizerPage.cs:775`, `Segmented.cs:25-33,203-211` |
| group outer stack (`CzRow.Group`) | Direction 1, Shrink 0 | gap **4** (label row ↔ card) | — | — | — | — | `Curated/SidebarCustomizerControls.cs:112-114` |
| group head row | Shrink 0, AlignItems Center | gap 8, margin 4/8/4/0 | — | — | — | — | `:117-120` |
| preset block stack | Direction 1, Shrink 0 | gap **12** (design group ↔ template list) | — | — | — | — | `SidebarCustomizerPage.cs:782-784` |
| ~~`CzRow.Header`~~ | 11f / 600, margin 2/12/0/4 | — | — | — | `Tok.TextTertiary` | **DEAD — no call site in the app**; do NOT port it | `SidebarCustomizerControls.cs:250-254` |
| ComboBox / NumberBox / TextBox | w **264** (`CzRow.ComboWidth`), TextBox h 30 or 32 | — | 4 | — | control defaults | — | `:343`, `PropertyPanel.cs:372-379`, `:838-844` |
| Slider | length **272**, Min 0 Max 500, Step 1, LargeChange 10 | — | — | thumb tooltip 0→"All" | control defaults | — | `:468-478` |
| Danger button | Small | — | 4 | 12f | fg `Tok.SystemFillCritical`, bg `Tok.SystemFillCriticalBackground` (pressed ×0.75 alpha), border transparent, disabled fg `Tok.TextDisabled` | `BackgroundSizing.InnerBorderEdge` | `:351-368` |
| `CzMenuButton` | 24 × 24, glyph 14f | — | 4 | — | `Interaction.Subtle`, glyph `Tok.TextSecondary` | flyout `PopupChrome.Popup`, `FlyoutPlacement.BottomEdgeAlignedRight` | `:43-78` |
| palette head | gap 8 | margin 4/0/4/0 | — | — | — | — | `Curated/SidebarCustomizerPalette.cs:89-104` |
| section budget | 11f / 600, 1 line, Shrink 0 | — | — | — | `Tok.SystemFillCritical` when full, else `Tok.TextTertiary` | — | `:98-102` |
| palette search box | h **30** | — | 4 | placeholder `sidebar.customizer.paletteSearch` | control defaults | — | `:382-386` |
| palette card | Shrink 0, ClipToBounds | pad 4 | 4 | — | `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault` | `Animate = PageSlideForward\|Back` | `:72-83` |
| palette group header | 1 line | margin 4/8/4/2 | — | `CzRow.GroupLabel` | `Tok.TextTertiary` | — | `:374-377` |
| appends-to caption | 11f, 1 line, ellipsis | — | — | — | `WaveeAccent.Decor` | — | `:158-161` |
| palette row | Shrink 0, gap 8 | pad 4/6/4/6 | 4 | title 13f/600 1 line, sub 11f ≤2 wrap | `Interaction.ListRow` | — | `:322-359` |
| palette row glyph plate | 24 × 24, icon 13f | — | 4 | — | `Tok.FillSubtleSecondary`, icon `Tok.TextSecondary` | HitTestVisible false | `:348-353` |
| palette add chip | 24 × 24, icon 12f (`Icons.Add`) | — | `Radii.FullAll` | — | `Tok.FillSubtleSecondary`, icon `Tok.TextSecondary` | Opacity 0.45 → HoverOpacity 1 @ 83 ms | `:365-372` |
| palette back row | h **28**, gap 4 | — | 4 | 12f / 600 | `Interaction.Subtle`, `Tok.TextSecondary`, chevron 12f | — | `:301-312` |
| palette note / empty line | 12f, wrap ≤3 | margin 4/8/4/8 | — | — | `Tok.TextTertiary` | — | `:316-320` |
| template card | Shrink 0, gap 8, glyph 14f, tail spacer 16 | pad 12/8/12/8 | — | 13f/600 + 11f ≤2 wrap | active `Tok.FillSubtleSecondary` / hover `FillSubtleTertiary` / pressed `FillSubtleSecondary`; inactive transparent / `FillSubtleSecondary` / `FillSubtleTertiary` | `Role = RadioButton` | `:472-507` |
| template active marks | `Icons.RadioBullet` `Tok.AccentDefault` + `Icons.Accept` 14f `Tok.AccentTextPrimary` (margin R 2) | — | — | — | — | — | `:484,503-505` |
| property subject header | h **52**, gap 8 | pad 12/0/8/0 | — | title 13f/600, kind 10f | `Tok.TextPrimary` / `Tok.TextTertiary`, glyph 16f `Tok.TextSecondary` | — | `Curated/SidebarPropertyPanel.cs:127-155` |
| property body scroller | gap 12 | pad 0/4/0/24 | — | — | — | `AutoEdgeFade`, `ScrollKey` per host | `:95-102` |
| empty-state row (Pinned) | MinHeight 44, plate 28 × 28, glyph 14f | pad 12/12/12/12, gap 12 | 4 | hint 12f wrap ≤3 | `Tok.FillSubtleSecondary`, `Tok.TextTertiary` | HitTestVisible false | `:324-343` |
| item row (normal) | Shrink 0, gap 6 / 8, plate 24 × 24 glyph 13f | pad 12/10/12/10 | 4 | title 13f, reason 11f ≤2 wrap | Opacity 0.7 when inert | — | `:808-846` |
| item row (compact / shortcut band) | **320 × 52**, gap 8, gripper 12f, plate 24 × 24, label box w 148 h 32 | pad 8 | 4 | — | `Tok.FillSubtleSecondary` → hover `FillSubtleTertiary` | — | `:777-778,849-877` |
| item add buttons | Grow 1 each, gap 8 | pad 12/8/12/12 | — | — | `Standard` + `Subtle`, Small | — | `:300-319` |
| picker dialog | **480** wide; body **432** | `ContentDialogPadding` 24 | `OverlayCornerRadius` 8 | — | dialog defaults | `Elevation.Flyout` (engine) | `Curated/SidebarItemPickers.cs:58,61`, `ContentDialog.cs:18` |
| picker list | h **320**, gap 2 | — | — | — | — | `AutoEdgeFade`, `ScrollKey "customizer.picker"` | `:146-149` |
| picker row | h **40**, gap 8, glyph 14f | pad 8/0/8/0 | 4 | title 13f 1 line, sub 11f 1 line | `Interaction.ListRow`, `Tok.TextPrimary` / `Tok.TextTertiary` | — | `:212-232` |
| action list | h **260**, gap 2 | — | — | — | — | `AutoEdgeFade`, `ScrollKey "customizer.actionpicker"` | `:402-405` |
| action row | h **48**, gap 8 | pad 2/0/8/0 | 4 | title 13f/600, sub 11f 1 line | selected `WaveeColors.SelectedRest` / `SelectedHover` / `SelectedPressed`; else transparent / `FillSubtleSecondary` / `FillSubtleTertiary` | `BrushTransitionMs = Motion.ControlFaster` (83) | `:430-479`, `Design/WaveeTokens.cs:272-274` |
| action selection bar | 3 × 20 | — | 1.5 | — | `Tok.AccentDefault`, Opacity 1 / 0 | HitTestVisible false | `:446-450` |
| action icon plate | 28 × 28, glyph 14f | — | 4 | — | selected `Tok.AccentSubtle` + `Tok.AccentTextPrimary`; destructive `Tok.SystemFillCritical`; else `Tok.FillSubtleSecondary` + `Tok.TextSecondary` | glyph uses the ref's **own font family** (`icon.Font`) | `:452-468` |
| action footer buttons | min-w **96**, h **32** | gap 8, Justify End | 4 | — | `Button.Standard` + `Button.Accent` (TabIndex 1) | — | `:561,575-591` |
| reason row | glyph 12f `Icons.StatusWarning`, text 11f ≤2 wrap | pad 4/0/4/0, gap 8 | — | — | `Tok.TextTertiary` | — | `:529-550` |
| action row glyph resolution | plate 28 × 28, glyph **14f** | — | 4 | — | see the plate row above | **`IconRef.Glyph` + `IconRef.Font` ONLY — the `ThemedName` arm is never taken in the picker**; `a.Icon()` is called with the default `isChecked: false`, so a toggle's row is always the unchecked mark | `Curated/SidebarItemPickers.cs:414,461-465` |
| bound row glyph resolution (the PANE, for comparison) | mark **16f**, centred in an `ArtSize(section)` box | — | — | — | `Tok.TextSecondary` enabled / `Tok.TextTertiary` disabled | ladder: `IconOverride` → `ThemedIcon` if `ThemedIconRegistry.Has` → glyph + `icon.Font` → `Icons.MusicNote` | `Pane/SidebarPaneText.cs:226-236` |
| bound row disabled affordance (the PANE) | the WHOLE row | — | — | — | — | `ToolTip.Wrap(row, reason, grow: 1f)` — the `grow: 1f` is load-bearing (without it the disabled arm renders narrower than the enabled arm) | `Pane/SidebarPaneSlot.cs:757-762` |
| property-panel item row, ACTION target | plate 24 × 24, glyph 13f | — | 4 | — | — | glyph is **always `Icons.RefineSparkle`** unless the user set an `IconOverride` — the editor does NOT use the descriptor's own mark | `Curated/SidebarPropertyPanel.cs:951-961` |
| miniature frame | h **220**, ClipToBounds | — | `Radii.CardAll` = 8 | — | `Tok.FillCardDefault`, border 1 `Tok.StrokeCardDefault` | — | `Shared/SidebarMiniature.cs:89-95` |
| miniature pane | w **258**, gap 2, ≤14 rows | pad 8/12/8/12 | — | section title = Eyebrow 12/16/600 tert, margin 4/2/4/2 | `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault` | — | `:81-88,100,167-172` |
| miniature row | height = `SidebarRowGeometry.HeightFor(density, subtitles)`, art `SidebarCover.S20/S28`, gap 8 | pad 8/0/8/0 | 4 | title 12f, sub 10f, count 10f | `Tok.FillSubtleSecondary` on the sampled playlist row | — | `:174-220` |
| miniature workspace | hero `SidebarCover.S64`, 3 cards of S64 | pad 16, gap 16 | — | card label 10f | `Tok.FillSubtleTertiary` / `Tok.StrokeDividerDefault` bars | — | `:290-342` |

### The glyph tables this surface owns — and the one it borrows

Every mark on this page resolves through one of these. They are app-side (they touch `Icons.*`), so the pure
palette/model tables carry glyph **names** and these turn a name into a codepoint. None of them may fall through to
a blank box — each has an explicit neutral default.

| table | input → output | entries | default | file:line |
|---|---|---|---|---|
| `CzGlyphs.ForKind(SidebarSectionKind)` | the mark on a palette row's plate, the hidden-list row, the property panel's subject header, the miniature's generic row | Pinned→`Pin`, JumpBackIn→`Clock`, CollectionShortcuts→`Heart`, PlaylistTree→`Folder`, EntityList→`Filter`, StaticLinks→`Link`, CustomGroup→`Grid`, Header→`Font`, Divider→`Remove`, EntityEmbed→`FavoriteStar`, NewReleases→`Album`, Concerts→`Calendar`, Extension→`Code` (13) | `Icons.MusicNote` — which is also what an UNKNOWN kind from a newer build wears in the Hidden list | `Curated/SidebarCustomizerPalette.cs:417-433` |
| `CzGlyphs.ForName(string?)` | a palette entry's `IconName` (the engine-free table's only glyph vocabulary) | Pin, Heart, Link, Folder, Filter, FavoriteStar, Headphones, Queue, Play, Clock, Contact, Album, Calendar, Grid, Font, Remove, RefineSparkle, Code (18) | `Icons.MusicNote` | `:394-415` |
| `SidebarIcons.Glyph(name, fallback)` | an ITEM's `IconOverride`, and the icon-picker's 30 radio rows | the 30 `SidebarIconNames.Allowed` names, in that order | the caller's fallback (a route's own `ShellNav.Dest` glyph, else `Icons.MusicNote`) — a hand-edited document degrades, never blanks | `Features/Sidebar/SidebarIcons.cs:23-56`, `Wavee.Core/Sidebar/SidebarLayoutModel.cs:493-498` |

**The borrowed one: `ActionIcons.Resolve(key, isChecked)`** (`Actions/ActionIcons.cs:46-86`) — 28 semantic keys →
`IconRef`, and the ONE table a `WaveeActionDescriptor` may name (`IconKey` is a key, never a raw glyph —
`WaveeActionDescriptor.cs:37-39`). It is owned by the action table (`Shell.cs`, Wave 4 owner I), not by the sidebar, but
every mark in the action picker and on a bound sidebar row comes out of it, so its shape is a constraint on this chapter:

| what it returns | keys | why it matters here |
|---|---|---|
| `IconRef.Themed(name, fallback)` — a layered vector name **plus** a Segoe glyph fallback | `play`, `like`/`heart`, `save` (unchecked), `add`, `link`, `delete`, `open`, `rename`, `folder` | the picker takes the **fallback** branch only (§1.1), the pane takes the themed branch when `ThemedIconRegistry.Has(name)` |
| a themed name whose fallback lives in the **app-local `WaveeIcons` face** | `play-next` (U+E900), `queue` (U+E901) | `IconRef.Font` MUST travel with `IconRef.Glyph`; dropping it renders □ (round-2 defect 6a) |
| a stateful pair | `like`/`heart` → `Heart` / `HeartFill`; `save` → `Add` / `Icons.Check` | the filled variant appears only where `isChecked` is passed — never in the picker |
| a plain Segoe glyph | `album`, `artist`, `remove`, `people`, `globe`, `credits`, `share`, `copy-uri`, `open-web`, `radio`, `video`, `replace`, `locate`, `reveal-folder`, `pin`, `unpin` | `pin`→`Icons.Pin`, `unpin`→`Icons.UnPin` (E77A) |
| `Icons.More` | anything unknown | the neutral default — a third-party `IconKey` this build does not know degrades to "…", never to a blank box |

`CzGlyphs.ForName` and `SidebarIcons.Allowed` are **deliberately different vocabularies** (18 vs 30, overlapping in
11 names): the first is what the palette's own table may say, the second is what a user's document may say. Do not
merge them in 0.3 — the second is validated by the reducer and is a persisted contract.

An item with no override resolves: Route → `ShellNav.Dest(key).Glyph`; Action → `Icons.RefineSparkle`;
Entity → `SidebarIcons.ForEntityKind` (Playlist→MusicNote, Album→Album, Artist→Contact, Show→RadioTower,
PlaylistFolder→Folder, Track→MusicNote) (`SidebarPropertyPanel.cs:952-961`, `SidebarIcons.cs:64-73`).

### Pipeline constants that decide geometry (all in `Features/Sidebar/Data/`)

| constant | value | what it decides | file:line |
|---|---|---|---|
| `SidebarRowGeometry.ClassicHeight` | 44 | the one edit-card height (`SectionCard`) | `SidebarRowGeometry.cs:20`, `SidebarRowExtents.cs:90` |
| `HeightFor(density, hasSubtitle)` | 32 / 40 / 44 / 44 / 48 | every item-shaped row | `SidebarRowGeometry.cs:57-62` |
| `ArtFor(density)` | 20 / 32 / 40 | the row art column | `:120-125` |
| `PaneEdge` / `RowInsetLeft` / `RowInsetRight` | 8 / 4 / 8 | the content lane | `:34,37,40` |
| `ContentLane` / `ContentLaneEnd` | **12** / **16** | where pane content begins / ends | `:44,49` |
| `SelGutterWidth` / `LeadingGap` / `LeadingLaneWidth` | 3 / 6 / **9** | art column origin (`ArtX(0)` = 21) | `:94,106,111,114` |
| `IndentStep` / `MaxIndentDepth` / `TreeGuideStep` | 12 / 4 / 12 | nesting | `:72,76,129` |
| `TreeContentX(depth)` | 19, 31, 43, … | the drop caret's x | `:139-143` |
| `HeaderHeight` / `SectionGap` / `HeaderBodyGap` | 28 / 8 / 2 | section rhythm | `:152,156,158` |
| `DividerHeight` / `EmptyHintHeight` / `TreeEndHeight` | 16 / 32 / 24 | chrome bands | `:161,164,195` |
| `PinDropZoneRestHeight` | 56 (→72 during a drag) | Pinned's empty state | `:168` |
| `ChipHeight` / `ChipStripHeight` / `ChipStripGap` | 26 / 28 / 4 | the EntityList header's inline chips | `:174-178` |
| `CardHeightFor(density)` | 56 / 72 / 88 | the EntityEmbed hero | `:183-188` |
| `PromptHeight(hasReason)` | 48 / 56 | degraded-state rows | `:191` |
| `RootlistSlotResolver.EdgeFraction/MinEdge/MaxEdge` | 0.30 / 10 / 16 | the drop edge band = `clamp(0.30·h, 10, 16)`, capped at h/2 | `RootlistSlotResolver.cs:118-138` |
| `DepthHysteresis` | **4 DIP** | stops the caret flickering between depths | `:125` |
| `SidebarDropCue.LineThickness/LineCorner/DotSize` | 2 / 1 / 6 | the insertion caret | `:275-280` |
| `SidebarRowPlanner.RailTileCap` / `RailPinnedCap` / `RailJumpBackInCap` / `RailEntityListCap` | 40 / 8 / 4 / 20 | rail tile budget | `SidebarRowPlanner.cs:181,195-197` |
| `SectionRowCap` / `DynamicSectionRowCap` / `SkeletonRows` | 5,000 / 20,000 / 3 | row guards + the skeleton count | `:184,193,199` |
| `SidebarContributionCache.PerSourceCap` | 200 | the stale-replay ceiling | `SidebarBinderPipeline.cs:414` |
| `SidebarFirstSeen.Cap` | 2,000 | the added-at proxy map | `SidebarFirstSeen.cs:21` |
| `SidebarProjectionBinder.RecencyCap` | 40 | the "recently opened" feed | `SidebarProjectionBinder.cs:49` |
| `SidebarConcertsSource.RefreshMinutes` / `SnapshotCap` | 30 / 20 | concert freshness + bound | `Data/Sources/SidebarFeedSources.cs:116,119` |
| `SidebarLayoutStore.MaxDocumentBytes` / `MaxSectionConfigBytes` | 2 MiB / 64 KiB | which fault banner appears | `Persistence/SidebarLayoutStore.cs:83,87` |
| `SidebarLayoutReducer.MaxSections/MaxItemsPerSection/MaxTitleLength/MaxTopBarItems/MaxUrisPerSet` | 40 / 500 / 60 / 6 / 500 | the budget chip, `MaxLength`, cap rejections | `Wavee.Core/Sidebar/SidebarLayoutReducer.cs:14-26` |
| `SidebarUndo.Capacity` | 50 | undo depth (oldest overwritten silently) | `Wavee.Core/Sidebar/SidebarUndo.cs:18` |

---

## 4. Colour & material

This surface has **no cover palette, no wash, no gradient, no scrim, no Mica/acrylic of its own** — it is a form, and the canvas is the sidebar. Every colour is a semantic `Tok.*` read, so light/dark is the token layer's job (`00-design-system.md`). The five derivations that DO exist:

1. **Eyebrow accent.** `WaveeAccent.Decor => Tok.AccentTextPrimary` (`Design/WaveeTokens.cs:50`). Applied to (a) the header's active-template eyebrow (`Page.cs:214`) and (b) the Destinations "adds to …" caption (`Palette.cs:160`). Both are *decorative accent ink on the page ground*, never on an accent plate — so they follow the theme's accent-text ramp and never need a contrast pass.

2. **Selection ramp for the action picker.** `WaveeColors.SelectedRest => Tok.AccentSubtle`; `SelectedHover => ColorContrast.Over(Tok.FillSubtleSecondary, Tok.AccentSubtle)`; `SelectedPressed => ColorContrast.Over(Tok.FillSubtleTertiary, Tok.AccentSubtle)` (`Design/WaveeTokens.cs:272-274`). The three are set EXPLICITLY on the row (`Pickers.cs:436-438`) because `.Interactive(...)` would overwrite all three from its recipe and erase the selected plate. Transition: `BrushTransitionMs = Motion.ControlFaster` = **83 ms**.

3. **Template-card ramp.** Same rule, different table: active `FillSubtleSecondary` / hover `FillSubtleTertiary` / pressed `FillSubtleSecondary`; inactive transparent / `FillSubtleSecondary` / `FillSubtleTertiary` (`Palette.cs:478-480`). The active card deliberately gets a *quieter* pressed state than its rest state so a press does not read as a deselect.

4. **The danger pairing.** The engine has no `ButtonAppearance.Danger`; `CzRow.Danger` folds the app's ONE red pairing — `Tok.SystemFillCritical` ink on `Tok.SystemFillCriticalBackground` (the destructive swipe plate in `Components/RowSwipe.cs`) — into the stock `Subtle` geometry through `Button`'s public palette seam: `Background = StateBrush(transparent, wash, wash×0.75α, transparent)`, `Foreground = StateBrush(ink, ink, ink@0.85α, Tok.TextDisabled)`, `Border = Flat(transparent)`, `Sizing = InnerBorderEdge` (`SidebarCustomizerControls.cs:351-368`). Red is a WARNING; the confirmation and the undo step are the safety.

5. **Health colour.** `Tok.SystemFillSuccess` for the 6-DIP saved dot; `Tok.SystemFillCritical` for the 40/40 budget count. Both are *state ink*, never a fill.

**Miniature.** The only surface here that carries imagery: it uses the bundled fake catalog's real cover art through `SidebarCover.Art(cover, null, seed, size, circular)` and the live `SidebarRowGeometry` height ladder, on `Tok.FillCardDefault` (frame) over `Tok.FillCardSecondary` (pane), separated by `Tok.StrokeCardDefault` hairlines and `Tok.StrokeDividerDefault` for a template's `Divider` sections (`Shared/SidebarMiniature.cs:89-95,111`). It is a *diagram of the real document*, never a data-loading skeleton — so it never shows a shimmer and never waits on the network.

**Transitions.** The palette card is the only node with a declared `Animate`; everything else changes colour through the engine's default brush transition (83 ms `FluentStandard`) baked by `AnimBake` from `Interaction.*` recipes.

---

## 5. Motion

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | file:line |
|---|---|---|---|---|---|---|---|---|
| enter contribution-pick mode | palette card (`Key` changes) | X + Opacity | +8 DIP, 0 → 0, 1 | **250 ms** (`Expressive.Fast`) | `Easing.SmoothOut` | none | engine `LayoutTransition`; opacity channel kept | `Palette.cs:76`, `MotionRecipes.cs:193-197` |
| leave pick mode (BackRow) | palette card | X + Opacity | −8 DIP, 0 → 0, 1 | 250 ms | `Easing.SmoothOut` | none | as above | `Palette.cs:76,306`, `MotionRecipes.cs:199-203` |
| either mode switch | the OUTGOING card (same node, new `Key`) | X + Opacity | 0, 1 → ∓8 DIP, 0 | 250 ms | `Easing.SmoothOut` | none | engine | `MotionRecipes.cs:197,203` (`Exit: EnterExit(Dx: ∓DistBase, Opacity: 0)`) |
| design switch (the PAGE's segmented) | the 24×3 accent pill | X (travel between segments) | old segment → new | `Segmented.PillTransition` (control default) | control default | none | engine | `Segmented.cs:210` — visible here because this control does NOT suppress its pill (§0.13) |
| hover / press a compact item row (shortcut band) | row `Fill` | brush | `FillSubtleSecondary` → `FillSubtleTertiary` | 83 ms (engine default) | `FluentStandard` | none | KeepFade | `PropertyPanel.cs:854` |
| pointer enters a palette row | the "+" chip | Opacity | 0.45 → 1 | **83 ms** (`Motion.ControlFaster`) | engine `FluentStandard` | none | `ReducedMotionPolicy.KeepFade` — the fade survives | `Palette.cs:370` |
| pointer enters / presses a palette row | row `Fill` | brush | transparent → `FillSubtleSecondary` → `FillSubtleTertiary` | 83 ms | `FluentStandard` | none | KeepFade | `Palette.cs:358` (`Interaction.ListRow`), `Reconciler.cs:4812-4813` |
| pointer enters / presses a template card | card `Fill` | brush | see §4.3 | 83 ms (engine default) | `FluentStandard` | none | KeepFade | `Palette.cs:478-480` |
| pointer enters / presses an action row | row `Fill` | brush | see §4.2 | **83 ms** explicit (`BrushTransitionMs`) | `FluentStandard` | none | KeepFade | `Pickers.cs:439` |
| select an action row | selection bar | Opacity | 0 → 1 | 83 ms (engine default) | `FluentStandard` | none | KeepFade | `Pickers.cs:449` |
| rejection dispatched | inline `InfoBar` | enter | engine `InfoBar` enter | engine | engine | none | engine | `Page.cs:368-369` |
| 4 s after a rejection | inline `InfoBar` | exit | engine | engine | engine | **delay 4000 ms** (`UseTimeout`) | timer is not motion; unaffected | `Page.cs:170` |
| accepted edit anywhere | every `Cz*` control | its own value | user value → document value | engine control transition | control default | none | value snap, no motion | `SidebarCustomizerControls.cs:391,426,460` |
| design switch from elsewhere | `Segmented` index | selection plate | old → new segment | `Segmented` default | control default | none | control default | `Page.cs:756-760` |
| palette chip drag promotion | chip | engine drag chip | — | engine | engine | armed at **2×** the normal threshold (`Drag.ClickPrimaryThresholdMultiplier = 2`) | engine | `Palette.cs:221-225`, `DragDropFacade.cs:31` |
| drop cue over a section card | card plate | `Fill`/`Border` | via `SidebarDropCue.DrawsPlate` | engine drop-target visual | engine | none | engine | `Pane/SidebarPaneEditCard.cs:189-199` |
| page enter / leave | whole page | shell page transition | — | — | — | — | — | owned by `18-shell-frame.md` |

**Clock.** Nothing in this chapter samples time itself: every transition is an engine `LayoutTransition` / `InteractionAnim`, which run on `FrameTime.NowQpc`. The one wall-clock in scope is the reject auto-dismiss, a `UseTimeout` (a scheduler, not an animation). **The one `Environment.TickCount64` in the whole pipeline** is `SidebarConcertsSource.EnsureFresh`'s 30-minute refresh gate (`Data/Sources/SidebarFeedSources.cs:159-160`) — correct there, because it measures a *network freshness window*, not an animation, and it must survive the app being idle. Port it as-is; do not "fix" it to frame time.

**Motion this surface deliberately does NOT have** (and must not gain): no reveal choreography or stagger on the column (it is a form, not a feed), no morph between the palette and the canvas, no scroll-linked header compaction (the header is fixed at 64 and never shrinks), no skeleton on the page itself (it renders from an in-memory document that is always present).

---

## 6. Interaction

### 6.1 The page

| gesture | target | outcome |
|---|---|---|
| click | Back (`←`) | `GoBack()`: `Prefs.Flush()`, then `HistoryStore.BackCtx` (the shell's real Back). Headless fallback: newest non-customizer visit in `HistoryStore.Entries`, else `Go("home")`. `Page.cs:274-293` |
| click | Done | **literally the same method** as Back (`Done() => GoBack()`, `:300`). Two exits that can never disagree about where "out" is. |
| click | Undo / Redo | `Prefs.Undo()/Redo()` then `ClearReject()`. Enabled from `CanUndo`/`CanRedo`. `:471-481` |
| hover | Undo / Redo | tooltip = `"Undo: {action}"` from the step's own loc key, or the bare "Undo" when there is none. `:192-197` |
| hover | Back | tooltip `auth.back` ("Back") — reused, not a fourth spelling of one word. `:989-990` |
| click | Reset | `ConfirmReset()` — skips the dialog when the document already equals a fresh build of its own template. `:688-705` |
| click | banner `Copy path` | `Acts.Clipboard.SetText(path)` (no-op on an empty path). `:490-494` |
| click | banner `Start fresh` | `Prefs.DiscardCorruptDocument()` → the store moves the file to `*.corrupt`, deletes `.bak` and `.tmp`, unblocks writes. `:483-488`, `SidebarLayoutStore.cs:446-474` |
| click | banner ✕ | `_corruptDismissed = true` / `_dismissedPersistenceFault = fault` + `BumpBanner()`. A *different* fault re-shows. `:339,366` |
| keyboard | the whole page | pure engine focus order: Back → title lane (not focusable) → Undo → Redo → Reset → Done → the column's focusables in document order. Every interactive node sets `Focusable = true` and an `AutomationRole`. **There is no accelerator anywhere on this page** — no Ctrl+Z/Ctrl+Y (Undo/Redo are buttons only), no Esc-to-leave, no `/` to focus the palette search. That is a real gap, not a simplification: a page whose whole job is edit-and-undo offering no Ctrl+Z is the first thing a keyboard user tries. 0.3 should add Ctrl+Z / Ctrl+Shift+Z (or Ctrl+Y) scoped to this route, and Esc → `GoBack()`. |
| keyboard | the CANVAS, while section drag is armed | **Space · arrows · Space · Esc** — the engine `Reorderable`'s built-in keyboard lift, which is the non-mouse half of section reordering and is announced through `sidebar.customizer.reorderGrabbed` / `…Moved` / `…Dropped` / `…Cancelled` + the reused `sidebar.pin.position` caption (`Pane/SidebarPaneText.cs:210-215`, `SidebarPaneEditCard.cs:200-203`). Owned by `25-sidebar.md`; named here because it is the only keyboard path to the gesture this chapter's palette drag shares. |
| keyboard | any `TextBox` here (rename, item label, palette search, config String) | `Enter` commits (`OnCommit`), `Esc` reverts (`OnCancel`) and suppresses the following blur-commit. The palette search box has NO `OnCommit` — it is a live filter (`Palette.cs:382-386`). |

### 6.2 The preset block

- **Segmented (Classic / Library / Custom)** — `Role` from `Segmented`; controlled against the service (`UseLayoutEffect` mirrors `SidebarDesignGating.IndexOf(prefs.Design.Value)`), so a design switch from the pane's quick menu or Settings moves it too. `onChange` → `prefs.SwitchDesign(SidebarDesignGating.FromIndex(v))`, **never** a raw `Design.Value` write (that would drop the per-mode remembered pane + view state). `Page.cs:754-798`
- **Show section contents** — `ToggleSwitch` over `prefs.Edit.ShowContents`. ON reveals every visible section's body on the canvas and **disarms section drag** (the card run is no longer contiguous). The rule has TWO terms, not one: `SectionsReorderable = !ShowContents && ExpandedSection is null` — so tapping any single card to expand it disarms the band too, with this switch still OFF. `Data/SidebarEditPlan.cs:100-101`. The escape hatch in both states is the card's own context menu / "…" → Move up · Move down (`SidebarPaneEditCard.cs:213-222`), which is why disarming is honest rather than a dead end.
- **Template cards** — `Role = AutomationRole.RadioButton`; the document's own `TemplateId` is the checked one. Click → `ApplyTemplate(id)`.

### 6.3 The palette

| gesture | outcome |
|---|---|
| type in the search box | `PaletteQuery` (session-only, never persisted). Filters BOTH the section list and the contribution list. Token-wise: every whitespace token must appear in the label or the description (`"top art"` finds "Artist top tracks"). `SidebarPalette.Matches`, `CustomizerLayout.cs:313-331` |
| click a **destination** row | `AddDestination(routeKey)`: append into the selected `StaticLinks` section via `SidebarItemCommands.Add`, else one `AddSection(StaticLinks, Item: route)`. No `IconOverride` is seeded. `Page.cs:565-581` |
| click **Links** | opens the destination picker FIRST; cancelling adds nothing. `Page.cs:543-552` |
| click **Liked Songs** | one `AddSection(StaticLinks, Item: route "liked", IconOverride "Heart")`. `Page.cs:630-639` |
| click **Action shortcut** | opens the action picker, then one `AddSection(StaticLinks, Item: bound action)`. `Page.cs:617-628` |
| click **Recently played** | `AddSection(JumpBackIn)` **then** `SetDisplayOption(RecentsSource, Played)` — the one honest two-undo-step gesture. `Page.cs:605-613` |
| click **Queue / Now Playing / Artist top tracks** | `AddContribution(id)` with the schema's `DefaultJson` seeds. `Page.cs:586-600` |
| click **Extension** | switches the palette into contribution-pick mode (`_forward = true`). `Palette.cs:203-206` |
| click **any other row** | `AddSectionOfKind(kind)`, appended at `TopLevelCount`. `Page.cs:531-536` |
| **after any accepted add** | `PaletteQuery` is cleared and the new section becomes this host's subject. It deliberately does NOT expand the new card on the canvas — the page owns `ShowContents`, the canvas owns `Expanded`. `Page.cs:650-655` |
| drag a droppable row | `Drag.Source(SidebarEditPlan.SectionDragKind, …, thresholdMultiplier: 2)`. The `DragSource` sits on the CLICK-OWNING node so `DragController.TryArm`'s walk-up has nothing to disambiguate. `Palette.cs:217-227` |
| click ‹ in pick mode | `_forward = false; _pickContribution.Value = false` → the card slides back. `Palette.cs:306` |

**Tooltips** — the palette has none; every row's meaning is its own 11f subtitle.

### 6.4 The property surface

| gesture | outcome |
|---|---|
| `Enter` in the rename box | `RenameSection(id, text)` — one rename, one undo step. Not per keystroke. |
| `Esc` in the rename box | reverts to the document value (`OnCancel`), and does NOT commit on the following blur. |
| blur the rename box | **commits** (`CommitOnLostFocus = true`) — load-bearing in the pane's light-dismiss popover, where the click that closes it is the blur. `PropertyPanel.cs:371-379` |
| `⋯` on the subject header | `MenuFlyout`, built at OPEN time (never at render — resolving labels in a render subscribes the row to the culture epoch): **1.** `Duplicate section` · **2.** separator · **3.** `Remove section` (`Icons.Delete`). `:166-173` |
| `⋯` on an item row | built at open time: (action items only) `Action shortcut` + separator; then a disabled `Icon` header; then **30 radio items** in `SidebarIconNames.Allowed` order — MusicNote, Heart, Album, Contact, RadioTower, Folder, FolderOpen, Home, Search, Clock, Star, FavoriteStar, Tag, Headphones, Microphone, Movie, Picture, Queue, Shuffle, Link, Grid, List, Pin, Settings, Code, Globe, Device, Friends, Equalizer, Download. Clicking the checked one CLEARS the override. `:903-929`, `Wavee.Core/Sidebar/SidebarLayoutModel.cs:493-498` |
| `⋯` on a `UriList` config row | `Add…` (entity picker) + separator + up to 20 current uris; clicking one REMOVES it. `:678-710` |
| 🗑 on an item row | `SidebarItemCommands.Remove(sectionId, itemId)`; tooltip `sidebar.customizer.itemRemove` ("Remove"). `:833-835,980` |
| `Remove section` | `RemoveSection(id)`; on success clears the host's subject. `:175-178` |
| slider drag (`Maximum items`) | live thumb tooltip; `0` reads as the WORD **"All"** in both the caption and the thumb (a spinner structurally cannot). `SidebarCustomizerControls.cs:463-482` |
| number box (`Columns`, extension `Int`) | `SidebarNumberEdit.Normalize(value, min, max)` = `Clamp(Round(v), min, max)`; a rejected edit snaps the local signal back to the authoritative value. `:551-553`, `CustomizerLayout.cs:46-50` |

### 6.5 The two menus that BRACKET this page (owned elsewhere, asserted here)

Both are `25-sidebar.md`'s to draw, but this chapter's non-negotiable 11, §6.2 and parity items 27/39 all depend on
their exact contents, so the rows and their order are recorded here too.

| menu | rows, in order | file:line |
|---|---|---|
| **the quick layout menu** (the pane's `SplitView` button, and the pane's background context menu) — the ONE route into this page | **1.** ◉ Classic · **2.** ◉ Library V3 · **3.** ◉ Curated (radio, checked = `prefs.Design`) · **4.** — · **5.** **Customize sidebar** (glyph `ActionIcons.Rename`; calls `prefs.SwitchDesign(Curated)` *then* `go("sidebar-customize")` — the silent force-switch §0.11 makes visible) · **6.** — · **7.** Reset width (enabled only while `prefs.WidthUserSet`) | `SidebarLayoutMenu.cs:56-79`; every label resolved at OPEN time, never at render |
| **the canvas card menu** ("…" on a `SectionCard`, and right-click on it) | **1.** Move up (enabled `Index > 0`) · **2.** Move down (enabled `Index < siblings-1`) · **3.** — · **4.** Hide **or** Show (one row, the label flips on `spec.Hidden`) · **5.** Duplicate section · **6.** — · **7.** Remove section (`Icons.Delete`) | `Pane/SidebarPaneEditCard.cs:213-227` |

The pinned **Shortcuts** sentinel card carries NO grip, NO eye and NO "…" at all — every one of those commands is an
`UnknownSection` rejection against it, and an affordance that silently rejects is worse than one that is absent
(`SidebarEditPlan.cs:103-110`). Its ITEMS stay fully editable through the expanded card.

**Accessibility names.** Every clickable node declares an `AutomationRole`: `Button` for palette rows, the back row, `CzMenuButton` and clickable `Prop` rows; `RadioButton` for template cards and action rows. The property panel's a11y reuses catalog keys rather than inventing (e.g. `InlineControls` labels as `sidebar.a11y.sortView`, "Sort and view options" — `CustomizerLayout.cs:425`).

**Drag-and-drop, complete** (page side): **source** = a palette row whose `SidebarPalette.CanDrag(add)` is true AND that is not currently in append-to-selection mode; **payload** = `SidebarSectionDropPayload`; **kind** = `"wavee.sidebar.section"` (`SidebarEditPlan.cs:68` — ONE owner, because a drag kind typed twice is a drop that silently accepts nothing); **target** = every `SectionCard` on the canvas, each its own `Drop.Target` (`Pane/SidebarPaneEditCard.cs:189-199`); **convention** = insert BEFORE the card you aimed at; a drop on the pinned Shortcuts head resolves to index 0; no card under the pointer appends. Captions: accept "Add here", refusal "Your sidebar is full — remove a section first".

### 6.6 The action platform (what a bound row does when it is clicked, and what happens when it can't be)

`Actions/Extensibility/**` is the only part of this surface whose *gestures* live outside the page, so they are recorded
here rather than inferred from the picker.

| gesture / event | outcome |
|---|---|
| click a **live** bound row in the pane | `registry.Execute(services, in binding)` — the ONE path. It re-resolves with `peek: true` (a snapshot, not a render subscription) and refuses if the target went away between paint and click, so a click can never run against a target the row did not render (`WaveeExtensionRegistry.cs:144-147`, `WaveeActionDescriptor.cs:123-133`) |
| click a **disabled** bound row | nothing — `OnClick` is only assigned on the enabled arm (`Pane/SidebarPaneSlot.cs:733`). The explanation is already on the row as a tooltip; there is no toast, no flash, no shake |
| hover a disabled bound row | the tooltip is the reason sentence, one of the 7 |
| invoke an action with `RequiresConfirmation` | routes through `SettingsShared.Confirm(overlay, title, body, primary, run)` — the `ContainerActions.DeletePlaylist` precedent (`19-shell-overlays.md` owns the dialog). Copy falls back title→`LabelLocKey`, body→title→label, primary→label (`WaveeActionDescriptor.cs:139-143`). **Unexercised in 0.2.9** |
| invoke it with **no overlay service** | REFUSED, with `HostUnavailable`. It does *not* degrade into an unconfirmed run — and because `Resolve` applies the same test, the row was already disabled before the click, so the refusal is never a surprise (`:109-110,136`) |
| a duplicate key registers at startup | the **first** registration wins, the second is dropped and recorded as `RejectedDuplicate`. Because `BuiltInExtensionTable` runs first, no third-party contribution can ever shadow a first-party action or data source (`WaveeRegistryTable.cs:135-157`, `WaveeExtensionRegistry.cs:23-26`) |
| an invalid / null key registers | `RejectedInvalidKey` / `RejectedNull`, also recorded. **Never a toast** — a registration fault is a developer or publisher fact, not a user interruption (`WaveeRegistryTable.cs:30-33`) |
| the user wants to SEE those refusals | **there is nowhere.** `Diagnostics` has no consumer anywhere in 0.2.9 (a repo-wide search for `.Diagnostics` finds no UI read). A contribution that silently failed to register is invisible, which is the exact failure the diagnostics list was built to prevent. 0.3 direction: one read-only list on the diagnostics page (`27-settings-and-diagnostics.md`), not a toast |

**Threading, asserted once**: registration is UI-thread, at startup, unsynchronized, and then read-only from the render
path — `register-then-read`, the same discipline as `SidebarPinStore` (`WaveeExtensionRegistry.cs:28-32`,
`WaveeRegistryTable.cs:13-15`, `IWaveeExtension.cs:16-17`). A 0.3 that makes the registry mutable at runtime (an
extension enabled from Settings, say) must NOT do it by unregistering — filter at the consumption site, or §0.17 breaks.

---

## 7. Data & readiness in 0.3 terms

### 7.1 The customizer page

| visual element | 0.2.9 data source | 0.3 read | readiness predicate |
|---|---|---|---|
| header eyebrow (template name) | `prefs.Layout.TemplateId` → `SidebarTemplates.NameLocKey` | `Sidebar.Layout.TemplateId` (in-memory document, loaded at Boot) | **always ready** — the document is loaded synchronously or defaulted; never a skeleton |
| Undo/Redo enablement + labels | `prefs.CanUndo/CanRedo/UndoLabel/RedoLabel` | `Sidebar.Undo` ring in `Sidebar.cs` | always ready |
| "Saved locally" | `prefs.PersistenceHealth.Value.Success` | `Sidebar.PersistenceHealth : Signal<WriteResult>` in `Sidebar.Host.cs` | always ready (`Healthy` is the seed) |
| corrupt banner | `prefs.Fault`, `prefs.FaultDetail` | same, from the layout store load | resolved at Boot |
| save-fault banner | `PersistenceHealth.Fault ∈ {ConfigTooLarge, DocumentTooLarge, IoFailure}` | same | edge-driven |
| design segmented index | `prefs.Design.Value` | `Sidebar.Design : Signal<SidebarDesign>` | always ready |
| "Show section contents" | `prefs.Edit.ShowContents` | `Sidebar.Edit.ShowContents` | always ready |
| template cards (5) | `SidebarTemplates.All` + loc keys | `Sidebar.Templates.All` (CORE table) | static |
| palette section rows (**19**) | `SidebarPalette.Sections` (static table — 4 Navigation, 3 Library, 3 Playback, 4 Dynamic feeds, 3 Layout, 1 Actions, 1 Extensions; the file's own "eighteen rows" comment is stale) | `Sidebar.Palette.Sections` (CORE) | static |
| palette **destination** rows (12 in 0.2.9; **11 in 0.3**, plan §9.6 Q7, 2026-09-12) | `SidebarPinId.PinnableRoutes` (9: home, search, albums, artists, liked, podcasts, local, history, recents — `SidebarPinId.cs:47-48`) ∪ `ExtraDestinationRoutes` (3: settings, api-console, concerts — `CustomizerLayout.cs:195`), labelled by `ShellNav.Dest(route)` | `Sidebar.Palette.Destinations` built from `Shell.Routes` (plan §4.11); labels via `Shell.Dest(route)` — ~~`api-console`~~ is not among them, it is DELETED | static in 0.2.9; the API-console row was gated on `DeveloperMode.Enabled.Peek()` inside `Filter` — **11 rows in a normal session, 12 in developer mode** (`CustomizerLayout.cs:346-350`, `App/DeveloperMode.cs:61,66,75`). **In 0.3 there is no developer-mode row here at all: 11 rows always** — `ExtraDestinationRoutes` becomes 2 (settings, concerts), not 3 |
| palette rows, TOTAL | `SidebarPalette.All` = 12 destinations + 19 sections = **31**; `Filter` offers **30** with developer mode off | same | static |
| palette contribution rows (pick mode) | `Registry.Sources` (`WaveeExtensionRegistry`) | `Sidebar.Sources` table in `Sidebar.Host.cs` | static after registration (Boot) |
| section budget `{used}/{max}` | `prefs.Layout.SectionCount` | `Sidebar.Layout.SectionCount` | always ready |
| appends-to caption | `prefs.Edit.OptionsSection ?? Expanded` → `Layout.Find(id)` | same, on `Sidebar.Edit` | always ready |
| hidden-section rows | walk of `Layout.Sections` + `ChildList` | same | always ready |
| layout file path | `SidebarLayoutStore.DefaultPath()` | `Sidebar.Store.DefaultPath()` | static composition of `%LOCALAPPDATA%` |
| **template confirmation miniature** | `SidebarTemplates.Build(id)` + `FakeData.Playlist/Artist/LibraryStats` covers | same; `FakeData` becomes `Entities.SeedFake()` (plan Wave 5, owner Q) | **static** — it must NOT read the live library, or the dialog would gate on the network |

**Nothing on this page is ever a skeleton.** The document is in memory; the whole page is a projection of it. That is a property worth preserving explicitly in 0.3: if a rebuild makes any part of this page wait on a fetch, that is a regression.

### 7.2 The property surface (the parts that DO read live data)

| visual element | 0.2.9 data source | 0.3 read | readiness predicate |
|---|---|---|---|
| item row title for an **Entity** item | `prefs.Entries.Current` linear scan on `e.Uri == item.Key`, else `item.FallbackTitle`, else "Unavailable" | `Entities.Track/Album/Artist/Playlist/Show(uri)` handle → `Knows(Identity)` ? `.Title` : `item.FallbackTitle` | show the fallback until `Identity` is Known; **never** "Unavailable" while a fetch is in flight |
| item row title for a **Route** item | `ShellNav.Dest(item.Key).Title` | `Shell.Dest(key).Title` | static |
| item row title for an **Action** item | `item.LabelOverride` → `Registry.TryGetAction(binding).Label()` → the RAW `binding.ActionKey` (`ProviderId + '.' + ActionId`) — `SidebarPropertyPanel.cs:932-942`, `Wavee.Core/Sidebar/SidebarLayoutModel.cs:176` | `Shell.Actions` table (plan §4.11, owner I) | static. **Never "Unavailable"** for an action — that arm is the Entity branch's |
| item row GLYPH for an **Action** item | `IconOverride` → else **always `Icons.RefineSparkle`** (`:951-961`) — the editor deliberately does not draw the descriptor's own mark, while the pane does (W22) | one decision, made on purpose | static |
| item row "why inert" line | `item.Action is null` → `CzLoc.RejectExtensionRefMissing`; else `Registry.Resolve(acts, binding).ReasonLocKey` (one of the 7 in §1.1), else `CzLoc.MissingEntity` — `:963-972` | same table | static; `Acts`/`Registry` null ⇒ **no line at all**, not a fabricated one |
| action picker row (label, caption, plate glyph) | `Registry.Actions` in registration order — 13 first-party descriptors (§1.1) | `Shell.Actions` | static after Boot; the list is a boot fact, never a fetch |
| action picker commit enablement | `model.Ready()` = a descriptor is chosen AND (`!NeedsTarget()` OR a `TargetKey` is set) — `SidebarItemPickers.cs:274` | same | live, read by the dialog FOOTER over a shared model |
| registry refusals (`Diagnostics`) | `WaveeExtensionRegistry.Diagnostics`, actions table then sources table | **no consumer in 0.2.9** — see §6.6 | n/a |
| `EntityUri` config row's shown name | `prefs.Entries.Current` scan | handle `.Knows(Identity) ? .Title : uri` | fallback = the raw uri, never a fabricated title |
| `CzQueryBlock` qualifier rail visibility | `prefs.Entries.QualifiersAvailable` | `Sidebar.QualifiersAvailable` — still `PopCount(flavorMask & 0b1110) >= 2` over the rootlist projection | requires the rootlist edge to be **complete**; while `Rootlist.State != complete` the rail must stay hidden rather than flicker in |
| item picker's Library tab | `prefs.Entries.Current` (max 200 shown) | `User.Me` edges: `Rootlist` (playlists+folders), `SavedAlbums`, `FollowedArtists`, `SavedShows` — filtered by `kindFilter`, excluding folders/tracks/routes | show the tab only when the four edge lists are `state == complete`; otherwise the engine's list skeleton |

### 7.3 The pipeline, file by file — what each one decides visually, and what becomes an edge read in 0.3

| 0.2.9 file | visual outcome it decides | 0.3 shape |
|---|---|---|
| `Data/SidebarProjection.cs` | the entry list every design renders: emission ORDER (rootlist order, then albums/artists/shows newest-saved-first), the folder 2×2 mosaic tiles (first ≤4 child covers), `Circular` for artists, the joined-artist string ("A, B, C…" capped at 3), `PinsFirst`'s leading pin band and the `IsPinned` stamp | **deleted as a copy step.** The "projection" IS `User.Me`'s edges: `Rootlist` (`RootlistEdge{Position, Depth, Kind, FolderName, AddedAt}`), `SavedAlbums`, `FollowedArtists`, `SavedShows`, `Pins`. `SidebarLibraryEntry` becomes a **view struct over a handle + an edge payload**, not a materialised record |
| `Data/SidebarLibraryEntry.cs` | the row's display vocabulary: `Name`, `Creator`, `Cover`, `ChildCount`, `Depth`, `Circular`, `Flavor`, `RouteKey` (null for folders and tracks), `IsPlayable`, `Missing` | every positional member maps to a handle property or an edge payload field (§7.4 gaps below); `RouteKey` becomes `Shell.RouteOf(handle)` |
| `Data/SidebarSort.cs` | the five row orders and their tie-breaks. **Every comparator ends in an ordinal `Id` compare** because `List<T>.Sort` is unstable and two equal keys would reshuffle between rebuilds — visible as row flicker under the FLIP transitions | port **verbatim** as `Sidebar.Sort` over `(slot, edge)` pairs; `NameComparer` stays the once-per-culture `StringComparer.Create(CurrentUICulture, ignoreCase: true)` with **no article stripping** |
| `Data/SidebarSearch.cs` | which rows survive a keystroke. Diacritics-insensitive, allocation-free, with a hand-folded Latin-1 fallback because `InvariantGlobalization=true` kills `IgnoreNonSpace` | port verbatim; it already operates on `(name, creator, query)` strings, which `StringTable.Resolve` still supplies |
| `Data/SidebarBinderPipeline.cs` | the ORDER of shaping — filter (kinds → qualifier → search) → sort → pins-first — which is what makes pins lead in *every* sort mode; the stale-qualifier and stale-custom-sort fallbacks; the contribution slice + availability verdict; the `Cached` replay | `Sidebar.Shape` (CORE) over edge spans; `ResolveExtensions` stays, reading `Sidebar.Sources` |
| `Data/SidebarEntriesShadow.cs` | **whether the sidebar re-plans at all.** An exact shadow compare (meta + count + elementwise, with a by-VALUE `MosaicTiles` compare) suppresses the version bump for a byte-identical rebuild — 2 storms per track boundary and 3 per navigation before it landed | **superseded** by the plan's per-table `Version`/`Changed` publication counter (§4.1). Keep the *property*: a publish that changed nothing must not bump `Changed` |
| `Data/SidebarRowPlanner.cs` (1,379 lines) | the whole flat row list: which kinds emit which `SidebarRowKind`, the skeleton count, the grid strip slicing, the tree flattening + expansion filter, the rail tile budgets, `BuildEdit`'s card-per-section canvas | port to `Sidebar.Plan` / `Sidebar.PlanRail` / `Sidebar.PlanEdit` (CORE). Inputs become spans over edges instead of `IReadOnlyList<SidebarLibraryEntry>` |
| `Data/SidebarRowDiff.cs` | **which realized rows re-render.** A row addresses its entry by INDEX, so both the row record and the entry behind it are compared; a plan publish bumps only the indices whose content can have moved | port; in 0.3 the entry half of the compare becomes a `Version` compare on the handle slot (cheaper and exact) |
| `Data/SidebarRowExtents.cs` | **the scroll geometry before anything is realized.** Without it every unrealized row claimed 44 and expanding a folder made rows above AND below shuffle for a frame or two as the extent corrected | port verbatim (it is already pure over the plan + display options) |
| `Data/SidebarRowGeometry.cs` | the one height/indent/art/lane ladder every row, band and chrome strip consumes; `ContentYOf` (prefix sum) for drop placement and bring-into-view; `DirectionOf` for the selection indicator's travel side; `TryFolderDescendantRange` / `TrySectionBodyRange` for disclosure choreography; `GridFallbackColumns` for the narrow-pane strip; `ShowsPinGlyph` | port **verbatim** — this is the single most load-bearing numeric file in the sidebar |
| `Data/SidebarRowResolve.cs` | **which row draws selected**, kind by kind, and the `Sweep`/`Flipped` symmetric difference that bumps only the rows that flipped on a navigation | port; `EntrySelects` becomes `Shell.RouteOf(handle) == route` |
| `Data/SidebarPillState.cs` | the left accent pill's **opacity is a bound read of this state**, never a mount-time literal. The `For(liveRoute, rowSelectsRoute)` conjunction is what makes a recycled slot dark for the one frame its index moved but its snapshot did not (the "two pills at once" defect #22/#23) | port verbatim |
| `Data/SidebarFirstSeen.cs` | **"Recently added" order for playlists**, which have no server add-date. First observation, capped at 2,000 with oldest-evicted; on a first run everything ties and `SourceOrder` (Spotify's own newest-first rootlist order) decides | keep. The plan's `RootlistEdge.AddedAt` is the natural home, but it is *still a local stamp* — do not let it masquerade as a server field |
| `Data/SidebarRecency.cs` | the **"recently opened"** feed only. Explicitly NOT what the Recents SORT uses (that is `LastPlayedMs` from the play log), so clicking a row to open it does not reorder the list | keep the split; two recency facts, two consumers |
| `Data/SidebarStageHold.cs` | **the mid-drag freeze.** A re-projection arriving mid-gesture re-keys the rows under the pointer and the drop lands where the user did not aim; the newest stage is parked and applied on session end (drop, cancel and Escape alike), last-writer-wins | port verbatim |
| `Data/SidebarReorderClamp.cs` | the displacement offset for a CLAMPED reorder (the engine's own `ReorderList.OffsetFor` cannot see an app-side clamp): rows between `from` and `to` shift by ±`extent`, everything else and the lifted row stay put | port verbatim |
| `Data/RootlistSlotResolver.cs` | the whole drop cue: the edge band `clamp(0.30·h, 10, 16)` capped at `h/2`, the depth channel from pointer X with **4 DIP hysteresis**, the `DepthRange` a row's After slot may address, the refusal table, and the invariant **line ⟺ Before/After/EndOfList, plate ⟺ Into, never both, never neither-while-armed** | port verbatim |
| `Data/RootlistDropDecision.cs` | where a drop LANDS and whether it may; the drag chip's "Move out of {anchor}" vs the toast's "{destination}" (two different questions that one wrong field used to answer) | port; legality still delegates to the rootlist marker stream |
| `Data/RootlistTreeNav.cs` | the non-mouse half: which of Move up / Move down / Move to folder appear at all (absent, never present-and-dead) | port; `RootlistSiblingRun` is a scan over `Rootlist` edge payloads |
| `Data/SidebarTreeSelection.cs` | multi-selection keyed by ROW ID (never index, because the tree re-flows constantly); WinUI `ExtendedSelector` semantics ported rule for rule; every mutator returns "did anything change" so only flipped rows re-skin | port verbatim |
| `Data/SidebarFolderFlyoutNav.cs` | the collapsed rail's folder drill-in: children by `ParentFolderId` (**not** `FolderId`, which means two different things by kind), the `ChildCount` that must equal the listed rows, cycle refusal, and the `PageKey = depth + ":" + folderId` that makes the slide read correctly | port; `ParentFolderId` becomes the rootlist edge's folder ancestry |
| `Data/SidebarNavLayout.cs` | which of Move up / Move down / Remove a row's context menu offers | port verbatim |
| `Data/SidebarNavPreview.cs` | **frame-one detail headers.** Opening a playlist/album from the sidebar stashes the row's own display cache so the detail page paints a title/cover/owner/count immediately instead of a header-less skeleton that reshapes | **obsolete by construction** in 0.3: the handle is already the shared row, so the detail page reads the same slot. Keep the *outcome* (no header-less first frame) as a parity item |
| `Data/SidebarPinId.cs` | the pin identity scheme — the pin id **is** the nav route key, which is why a pinned row renders through `ShellNav.Dest` with no extra plumbing, why the recency join is an identity lookup, and why a pin survives a library refresh. Also the closed route recogniser (typos and third-party uris are refused, never rendered as the "Your Library" fallback) | port; `Pins` becomes an edge on `User.Me` with the *id scheme kept* (it is what a pinned row's label and glyph resolve through) |
| `Data/PinSyncRules.cs` | which pins mirror Spotify's `ylpin` set and how a pin id ↔ wire uri maps (Liked = bare `spotify:collection`) | port verbatim |
| `Data/SidebarDestination.cs` | the canonical routable destination shared by tabs, page chrome and the sidebar; Search canonicalises so a query never mints one pin per query | port into `Shell.cs` |
| `Data/SidebarEditPlan.cs` | the canvas: which sections reveal bodies, whether section drag is armed, the card's honest count (`-1` = "no count worth claiming" for a projected section), the band-slot → `MoveSection` bridge and the palette-drop → `AddSection` bridge | port verbatim |
| `Data/SidebarSourceMap.cs` | the feed row shapes: `Offline → Ready` (an offline feed is EMPTY, not broken, and must render its empty caption rather than a permanent skeleton); a played context the projection does not know is emitted with an **empty `Name`**, which is the surface's "render dimmed from the uri" signal | port; the "unknown context" row becomes a handle whose `Identity` is not Known |
| `Data/SidebarDataSource.cs` | the contribution contract the customizer GENERATES property controls from: `SidebarConfigFieldKind` (String/Int/Bool/EntityUri/Enum/UriList) → the control family; `SidebarContributionAvailability` → the placeholder reason | port; the schema stays the customizer's only input for extension rows |
| `Data/Sources/*.cs` (9 sources) | the actual rows of Queue (excl. the now-playing head, deduped by uri, default max 5), Now Playing (exactly 1, empty while nothing plays), New releases (default 4), Concerts (default 3, radius 100 km, 30-min TTL, **"Set your location" prompt** as the one actionable degraded state), Artist top tracks, Library, Playlist tree, History visited/played | port to `Sidebar.Sources` in `Sidebar.Host.cs`; each `Fill` becomes an edge/handle read instead of a record copy |
| `Persistence/SidebarLayoutStore.cs` | **which banner appears and whether the saved dot shows.** Atomic `File.Replace` with one rotated `.bak`; a corrupt file is preserved, never rewritten; over-budget snapshots are dropped WHOLE (never truncated) and classified; a good `.bak` is a full recovery with writes still enabled | port verbatim into `Sidebar.Host.cs` |
| `Persistence/SidebarLayoutDoc.cs` | **forward compatibility, which is a visual property**: an unrecognised section kind round-trips untouched at its original index (renders as nothing, still listed in Hidden sections, still counted by the budget) and unknown MEMBERS anywhere survive via `[JsonExtensionData]` re-attached by owning id | port verbatim |
| `Persistence/SidebarLayoutMigrations.cs` | nothing visual today (v1→v2 is IDENTITY) but owns the contract for every future arm: in memory, before anything reads it, total, preserving, one version at a time | port verbatim |
| `Persistence/SidebarLayoutDefaults.cs` | the fresh-install AND corrupt-fallback document (Wavee Curated) | port verbatim |

### DATA GAPS

Everything this surface shows that the plan's data model (§4.1-4.3, §4.14) does not yet hold:

| element | 0.2.9 source | what 0.3 needs | proposed column / edge |
|---|---|---|---|
| **Folder rows in the sidebar tree** (name, direct child count, 2×2 mosaic of the first ≤4 child covers) | `SidebarProjection.Walk` over `PlaylistFolder` + `FolderTiles` (`SidebarProjection.cs:276-287`) | folders are not an `EntityKind` in §4.1 and `RootlistEdge` carries only `FolderName`/`Depth`/`Kind` | add `RootlistEdge.FolderId : StringId` and a derived per-folder `ChildCount : ushort`; mosaic tiles are a **computed-at-commit** `StringId[4]` beside the folder marker (a per-frame scan of the CSR would be P11's exact anti-pattern) |
| **Playlist flavor** (By you / By Spotify / Mixed / unknown) — gates the qualifier chips and the `Playlists by` dropdown | `SidebarProjection.FlavorOf(in PlaylistSummary)` from `IsOwner` / `OwnerName == "Spotify"` / `CanEdit` (`:204-209`) | `Playlist` has no owner-name, `IsOwner` or `CanEdit` column in the plan | `PlaylistTable`: `Owner : int` (user slot), flags `Owned`, `Collaborative`; `Flavor` stays **derived** (`Sidebar.FlavorOf(Playlist)`), never a stored column, so the conservative "the data does not say ⇒ None" rule survives |
| **`QualifiersAvailable`** | `PopCount(flavorMask & 0b1110) >= 2` folded during the projection pass (`:214`) | no place to accumulate a mask | `Sidebar.QualifiersAvailable` computed once per rootlist publication and cached beside `Sidebar.LayoutVersion` — **a derived fact on the model**, never probed by the UI |
| **Pin display cache** (`Name`, `Uri`, `AddedAtMs`) for an editorial pin the library does not contain | `SidebarPin` record + the binder's async `RequestPinHydration` overlay (`SidebarProjectionBinder.cs:478-521`) | `Pins` is an `EdgeTable<LibraryEdge>` with only `AddedAt` + flags | either (a) a pin whose target handle is not `Knows(Identity)` is `Ensure`d at `FetchPriority.Visible` on the first paint — which is the honest 0.3 answer and removes the whole hydration side-cache — or (b) `LibraryEdge.CachedName : StringId`. **Prefer (a)**; the "blank cover + 0 songs forever" defect was caused by (b) with no refresh |
| **First-seen stamps** (the playlist added-at proxy) | `SidebarFirstSeen` map, persisted in `sidebar-layout.json` `v3.firstSeen` | nothing | keep as a side map in `Sidebar.Host.cs`, persisted where it is now; `RootlistEdge.AddedAt` is the natural slot but must be documented as LOCAL |
| **`LastPlayedMs`** (what the Recents SORT reads) | `PlayLogStore.Recency` stamped onto each entry by the projection (`:269-270`) | no play-log column | `TrackTable`/`AlbumTable`/… `LastPlayedAt : int` (seconds since app epoch), written by the playback host at context start |
| **`LastVisitedTicksUtc`** (what the "recently opened" FEED reads) | `SidebarRecency` built from `HistoryStore` | nothing | `Shell.History` stays a side store; the sidebar joins by route key (which is still the entity uri/pin id) |
| **Contribution config schemas** (the customizer's generated property rows) | `ISidebarDataSource.ConfigSchema` — 7 fields for `wavee.library`, 1-2 each for queue/newReleases/concerts/artistTopTracks | not in the plan at all | keep `Sidebar.ConfigSchema` as-is in `Sidebar.Host.cs`; it is a UI-generation contract, not entity data |
| **Extension config blobs** | `SidebarExtensionRef.Config : JsonElement`, 64 KiB cap, round-tripped verbatim | not in the plan | stays in `sidebar-layout.json`, untouched |
| **Action descriptors** — the FULL shape, not just the picker's four visible fields | `WaveeActionDescriptor` (`Actions/WaveeActionDescriptor.cs:28-89`) + the 13-row `BuiltInExtensionTable` | plan §4.11 says "WaveeCommands + Actions/* → one table" and stops there | `Shell.Actions : ActionDescriptor[]` must carry **all twelve** members, not the four that are obviously visual: `{ Key (persisted — never renamed), LabelLocKey, IconKey (→ IconRef with glyph **and font**), AcceptedTargets (which IS the picker's "Acts on" list and the row caption), ArgumentSchema, IsEnabled, IsChecked (non-null ⇒ the row is a toggle), Destructive, RequiresConfirmation + ConfirmTitle/Body/PrimaryLocKey, RequiredPermissions, LegacyId, Run(services, binding, resolution) }` plus `Resolve/Execute/Checked/ToMenuItem`. **The font matters**: `wavee.playNext` (U+E900) and `wavee.addToQueue` (U+E901) live in the app-local `WaveeIcons` face and rendered as □ when only `.Glyph` was passed. **The confirmation trio matters more**: drop it and a bound destructive verb runs with no dialog |
| **The unavailability vocabulary** (what a disabled bound row, a picker `ReasonRow`, an inert item row and a menu accelerator all say) | `WaveeActionUnavailable` — 7 members, 7 loc keys (`Extensibility/WaveeActionTargeting.cs:39-58,140-158`) | nothing in the plan; §4.11 has no notion of *why* a command is unavailable | `Shell.Actions` resolution must return `(Available, ReasonLocKey)`, never a bare bool — a bool collapses 7 sentences into one grey row. The enum, its 7 keys and the `HostUnavailable`/`NotApplicable` split (same sentence, different meaning: only the first is the safety refusal) port verbatim |
| **The contribution registry's bookkeeping** (what makes a stored binding always resolvable) | `WaveeRegistryTable<T>` + `WaveeExtensionKey` (`Extensibility/WaveeRegistryTable.cs`) | the plan has one action table but no registration POLICY | port the four outcomes (`Registered`/`RejectedNull`/`RejectedInvalidKey`/`RejectedDuplicate`), **first-wins**, insertion order (which IS the picker's order), key validity (2+ segments, ≤128 chars — the key is persisted in `sidebar-layout.json`), `Compose`'s already-qualified tolerance, and the rule that **nothing is ever unregistered**. A disabled extension is filtered at the consumption site |
| **Registration diagnostics** | `WaveeExtensionRegistry.Diagnostics` — built, **never read by any UI** | not in the plan | keep the list; give it its one read-only home on the diagnostics page (`27-settings-and-diagnostics.md`). Never a toast — a registration fault is a developer fact. This is a 0.3 *addition*, not parity |
| **The trusted-SDK seam** (`IWaveeExtension` / `IWaveeExtensionRegistrar`) | `Extensibility/IWaveeExtension.cs` — two interfaces, 39 lines, and the shape `BuiltInExtensionTable.RegisterAll` is hand-written against today and **source-generated in M4** | not in the plan | port the interfaces unchanged even though 0.2.9 has exactly one implementation-by-delegate. They are the reason first-party is literally the extension `"wavee"` rather than a privileged path, and the reason M3's sandboxed host can replay a manifest onto the SAME registrar. Collapsing them into a static table is the one change that cannot be undone later |
| **Fake covers for the miniature** | `FakeData.Playlist(n)`, `FakeData.Artist(n)`, `FakeData.LibraryStats()` | plan Wave 5 owner Q replaces `Wavee.Core/Fakes` with `Entities.SeedFake()` | `Entities.SeedFake()` must expose **deterministic, index-addressable** samples (`SeedFake.Playlist(1)`, `.Artist(3)`) — the miniature indexes Playlist **1, 2, 5, 7, 8, 10, 12, 14** and `index + 6`, plus Artist **3** and `LibraryStats()` for the shortcut counts (`SidebarMiniature.cs:134-137,156-162,272,292,320`), and a random seed would make the confirmation dialog flicker between opens. **2 and 7 are the grid-strip cells** (`PreviewGridCell(2)`, `PreviewGridCell(7)` at `:128`) — omit them and a Grid-presentation template's miniature loses its covers |

---

## 8. Pure rules to port verbatim

These are the engine-free classes that encode the look. They are **ported, never re-derived**. "CORE section" = the pure region of the named 0.3 file.

| name | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `SidebarPalette` | `Curated/SidebarCustomizerLayout.cs:115-356` | the whole palette table (**19** section entries + 12 generated destinations = 31 rows), render order (Destinations FIRST), the token-wise filter, `CanDrag`, `AppendsToSelection`, `EntryForContribution`, `NormalizeQuery`, `GroupLocKey`, the developer-mode route gate | `Wavee.Tests/SidebarCustomizerLayoutTests.cs:24-279` — **12 facts** (7 `Palette_*`, 3 `Destinations_*`, `CanDrag_*`, `TheBareLinksRow_*`) | `Shell/Sidebar.cs` — CORE "palette" |
| `SidebarDisplayValues` | same file `:362-451` | the property panel's ROW ORDER (13 fields), the int projection (`Read`, the exact inverse of the reducer's `WithField`), `IsFlag` (7 flag fields), label loc keys, and the CHOICE LISTS whose index order **is** the enum value order (Density 3 · Presentation 2 · RecentsSource 2 · EmptyBehavior 4) | `SidebarCustomizerLayoutTests.cs:289-415` — **5 facts**, incl. `DisplayValues_EveryFieldRoundTripsEveryChoiceThePanelCanOffer` and `DisplayValues_OrderCoversEveryFieldTheOptionTableCanAllow` | `Sidebar.cs` — CORE "display options" |
| `SidebarQueryPanelShape` | same file `:34-42` | which query controls a kind owns (`PlaylistTree` → qualifier only; `EntityList` → kinds + qualifier; everything else → neither) | `SidebarCustomizerLayoutTests.cs:304-316` (1 fact) | `Sidebar.cs` — CORE |
| `SidebarNumberEdit` | same file `:46-50` | `Clamp(Round(v), min, max)` — used for dispatch AND for rejection snap-back | `SidebarCustomizerLayoutTests.cs:317-350` (1 fact) | `Sidebar.cs` — CORE |
| `SidebarConfigJson` | same file `:459-561` | the opaque-config rewriter + `Defaults(schema)`: every untouched member copies through verbatim; a null/empty `UriList` REMOVES the member; a non-object degrades to just the edited member; an unparseable `DefaultJson` is skipped; never throws | `SidebarCustomizerLayoutTests.cs:426-506` — **6 facts** (2 `ConfigDefaults_*`, 4 `ConfigWrite_*`) | `Sidebar.cs` — CORE "extension config" |
| `WaveeExtensionKey` | `Actions/Extensibility/WaveeRegistryTable.cs:41-105` | the namespaced-key vocabulary every contribution and every stored binding share: `IsValid` (2+ segments, ASCII-letter start, `[A-Za-z0-9_-]` body, ≤128 chars), `Compose` (tolerates an already-qualified `ActionId`; "" when a half is missing), `PublisherOf`, `IsFirstParty` | `Wavee.Tests/WaveeExtensionRegistryTests.cs:43-95` (**8** facts, incl. the overlong-key and double-prefix cases) | `Shell.cs` — CORE "actions" |
| `WaveeRegistryTable<T>` | `Actions/Extensibility/WaveeRegistryTable.cs:112-172` | the registration POLICY that makes §0.17 true: append-only, insertion-ordered (**which IS the action picker's row order and W3's contribution order**), key-unique, **first-wins** on a duplicate, four `WaveeRegisterOutcome`s, a `WaveeRegistryDiagnostic` per refusal and none on success | `WaveeExtensionRegistryTests.cs:96-169` (**6** facts, incl. `DuplicateKeyIsRejectedAndTheFirstRegistrationWins` and `EnumerationPreservesRegistrationOrderForThePicker`) | `Shell.cs` — CORE "actions" |
| `WaveeActionTargets` + `WaveeActionTargetModes` + `WaveeActionUnavailable` + `WaveeActionHostState` + `WaveeActionTargetResolution` | `Actions/Extensibility/WaveeActionTargeting.cs` (251 lines) | the **whole** visible-but-disabled contract: which target modes a descriptor may offer, what a binding resolves to, and the 7 reasons with their 7 loc keys. `accepted` is checked FIRST, so narrowing a descriptor can never widen an old binding; an unknown future mode maps to `Nothing` and is refused rather than treated as `None`; a `FixedEntity` key is normalized through BOTH `SidebarPinId.FromUri` and `.FromRoute`, and an unrecognized scheme still passes through as a bare uri | `WaveeExtensionRegistryTests.cs:205-381` (**16** facts, incl. `AModeTheDescriptorDoesNotAcceptIsRefusedEvenWhenTheStateWouldSatisfyIt`, `AFutureModeValueIsRefusedRatherThanTreatedAsNone`, `EveryUnavailableReasonCarriesAnExplanationAndAvailableCarriesNone`) | `Shell.cs` — CORE "actions" |
| `PinRowRule` / `PinRowKind` | `Actions/Extensibility/PinRowRule.cs` (32 lines) | which pin row a menu inserts — an **absolute-state pair, never a toggle** (the `Menus.AccessItem` precedent). No store (the feature's kill switch) **or** an unpinnable target ⇒ `None`: the menu OMITS the row rather than showing a dead one | `WaveeExtensionRegistryTests.cs:382-443` (**7** facts, incl. the track/episode arms and a round-trip through the real store) | `Sidebar.cs` — CORE (beside `SidebarPinId`) |
| `DeveloperMode.ShowsRoute` | `App/DeveloperMode.cs:61-75` | the ONE developer-route gate BOTH this surface's route lists ride — the palette's Destinations group (`Filter`) and the item picker's Navigation tab (`AppendRoute`). One predicate, so the two offers can never disagree about the API console | `SidebarCustomizerLayoutTests.cs:112-146` (exercised) | `Shell.cs` — CORE |
| `SidebarEditPlan` | `Data/SidebarEditPlan.cs:62-253` | `ShowsBody`, `HasBody`, `SectionsReorderable`, `IsPinnedCard`, `CardCount`, `Fold`, `ToMoveSection`, `ToAddSection`, `SectionIdAt`, and the `SectionDragKind` literal | `Wavee.Tests/SidebarEditPlanTests.cs` (532 lines) | `Sidebar.cs` — CORE "edit" |
| `SidebarRowGeometry` | `Data/SidebarRowGeometry.cs` | the entire height / indent / lane / art ladder + `ContentYOf`, `IndexOfRoute`, `DirectionOf`, `FolderHeaderIndexOf`, `TryFolderDescendantRange`, `TrySectionBodyRange`, `ShowsPinGlyph`, `GridFallbackColumns` | `SidebarRowGeometryTests.cs` (281) | `Sidebar.cs` — CORE "geometry" |
| `SidebarRowExtents` | `Data/SidebarRowExtents.cs` | the analytic extent of every planned row + `BandTop`'s previous-row rule | `SidebarRowExtentsTests.cs` (170) | `Sidebar.cs` — CORE "geometry" |
| `SidebarRowPlanner` | `Data/SidebarRowPlanner.cs` | the flat plan, the rail plan, the EDIT plan | `SidebarRowPlannerTests.cs`, `SidebarRailPlannerTests.cs`, `SidebarShortcutsSectionTests.cs` | `Sidebar.cs` — CORE "planner" |
| `SidebarRowDiff` | `Data/SidebarRowDiff.cs` | which realized rows re-render | `SidebarRowDiffTests.cs` (109) | `Sidebar.cs` — CORE |
| `SidebarRowResolve` | `Data/SidebarRowResolve.cs` | per-kind selection + `Sweep`/`Flipped` | (exercised through `SidebarRowPlannerTests`) | `Sidebar.cs` — CORE |
| `SidebarPillState` | `Data/SidebarPillState.cs` | the accent pill's lit rule and its derived opacity | (exercised through the pane invariant tests) | `Sidebar.cs` — CORE |
| `SidebarSort` | `Data/SidebarSort.cs` | five total orders + `Effective` + `SupportsDirection` | `SidebarSortTests.cs` (257) | `Sidebar.cs` — CORE |
| `SidebarSearch` | `Data/SidebarSearch.cs` | the diacritics-folding matcher + its invariant-mode fallback | (exercised through `SidebarProjectionTests`) | `Sidebar.cs` — CORE |
| `SidebarProjection` | `Data/SidebarProjection.cs` | emission order, `FlavorOf`, `QualifiersAvailable`, `PinsFirst` | `SidebarProjectionTests.cs` (641) | `Sidebar.cs` — CORE (re-expressed over edges) |
| `SidebarBinderPipeline` | `Data/SidebarBinderPipeline.cs` | the shaping ORDER, `ResolveUnlistedPin`, `ResolveExtensions`/`Resolve`, `SidebarDataSourceTable`, `SidebarExtensionSlices`, `SidebarContributionCache` | `SidebarProjectionBinderTests.cs` (761) | `Sidebar.cs` CORE + `Sidebar.Host.cs` SHELL |
| `SidebarEntriesShadow` | `Data/SidebarEntriesShadow.cs` | the publish gate (exact compare, incl. by-value tile compare) | `SidebarChurnTests.cs` | superseded by table `Version`; keep the property |
| `SidebarFirstSeen` | `Data/SidebarFirstSeen.cs` | the added-at proxy, cap + eviction + prune | `SidebarProjectionTests.cs` | `Sidebar.Host.cs` |
| `SidebarRecency` | `Data/SidebarRecency.cs` | navigation recency (feed only) | `SidebarProjectionTests.cs` | `Sidebar.Host.cs` |
| `SidebarStageHold<T>` | `Data/SidebarStageHold.cs` | the mid-drag parking bay | `SidebarDropFreezeTests.cs` (100) | `Sidebar.cs` — CORE |
| `SidebarReorderClamp` | `Data/SidebarReorderClamp.cs` | the clamped displacement hint | `SidebarDragClampTests.cs` | `Sidebar.cs` — CORE |
| `RootlistSlotResolver` + `SidebarDropCue` | `Data/RootlistSlotResolver.cs` | the drop zone bands, depth channel, hysteresis, refusal table, and the line/plate invariant | `RootlistSlotResolverTests.cs`, `RootlistRefusalTests.cs`, `SidebarDropCueTests.cs` | `Sidebar.cs` — CORE "drop" |
| `RootlistSlotMapper` / `RootlistDropDecision` | `Data/RootlistDropDecision.cs` | cue → destination, and the anchor/destination name split | `RootlistDropScenarioTests.cs`, `RootlistSlotToOpTests.cs` | `Sidebar.cs` — CORE "drop" |
| `RootlistTreeNav` | `Data/RootlistTreeNav.cs` | sibling runs, which move verbs exist | `SidebarNavExtrasTests.cs`, `FolderActionsTests.cs`, `RootlistFolderPickerTests.cs` | `Sidebar.cs` — CORE |
| `SidebarTreeSelection` | `Data/SidebarTreeSelection.cs` | the WinUI extended-selection semantics, keyed by id | `SidebarTreeSelectionTests.cs` | `Sidebar.cs` — CORE |
| `SidebarFolderTree` / `SidebarFolderFlyoutNav` | `Data/SidebarFolderFlyoutNav.cs` | the rail flyout's children, count, cycle refusal, page key | `SidebarFolderFlyoutNavTests.cs` (239) | `Sidebar.cs` — CORE |
| `SidebarNavLayout` | `Data/SidebarNavLayout.cs` | which move/remove verbs a row's menu offers | `SidebarNavLayoutTests.cs` (62) | `Sidebar.cs` — CORE |
| `SidebarNavPreview` | `Data/SidebarNavPreview.cs` | the frame-one detail header stash | `SidebarNavPreviewTests.cs` (120) | obsolete; keep the outcome |
| `SidebarPinId` | `Data/SidebarPinId.cs` | the pin↔route identity scheme and the closed route recogniser | `SidebarPinStoreTests.cs`, `SidebarPinKindWireTests.cs` | `Sidebar.cs` — CORE (or `Shell.cs`) |
| `PinSyncRules` | `Data/PinSyncRules.cs` | which pins sync and their wire spelling | `PinSyncRulesTests.cs` (97) | `Sidebar.cs` — CORE |
| `SidebarSourceMap` | `Data/SidebarSourceMap.cs` | feed row shapes + `FromFeedState` (Offline → Ready) | `SidebarDataSourceTests.cs` (412) | `Sidebar.cs` — CORE |
| `SidebarDataSource*` contract | `Data/SidebarDataSource.cs` | the schema → control-family mapping and the availability vocabulary | `SidebarDataSourceTests.cs` | `Sidebar.cs` — CORE contract |
| `SidebarLayoutStore` | `Persistence/SidebarLayoutStore.cs` | the fault classification the banners read; the atomic write + `.bak` policy; the two budgets | `SidebarLayoutStoreTests.cs` (546) | `Sidebar.Host.cs` — SHELL (its pure classifier stays testable) |
| `SidebarLayoutWire` / the DTOs | `Persistence/SidebarLayoutDoc.cs` | round-trip fidelity for unknown kinds and unknown members | `SidebarLayoutJsonTests.cs` (913) | **`Sidebar.Doc.cs`** — CORE "wire" (A4) |
| `SidebarLayoutMigrations` | `Persistence/SidebarLayoutMigrations.cs` | the version ladder contract | `SidebarLayoutV2MigrationTests.cs` (394) | **`Sidebar.Doc.cs`** — CORE (A4) |
| `SidebarLayoutDefaults` | `Persistence/SidebarLayoutDefaults.cs` | fresh-install + corrupt-fallback documents | `SidebarBootstrapTests.cs`, `SidebarBuiltInDocumentTests.cs` | **`Sidebar.Doc.cs`** — CORE (A4) |
| `SidebarDesignGating` | `Features/Sidebar/SidebarDesignGating.cs` | chooser gate, marker burn, `CanCustomize`/`OffersCustomize` (Curated only), index↔enum coercion, card title/subtitle keys | `SidebarDesignGatingTests.cs` | `Sidebar.cs` — CORE |
| `SidebarLayoutReducer` + model + templates + undo + compare + item commands | `Wavee.Core/Sidebar/*` (2,526 lines) | every cap, every rejection reason, the per-kind option table `AllowsDisplayField` (**which the property panel RENDERS from**), `AcceptsItems`, `ItemCapacity`, `SupportsLibraryQuery`, `EffectiveQuery`, `EmptyBehaviorFor`, `IsKnown`, the 5 templates, the 30-name icon whitelist, the 50-step undo ring | `SidebarLayoutReducerTests.cs` (~2k lines — the plan says this ports "nearly 1:1"), `SidebarTemplateTests.cs` | **`Sidebar.Doc.cs`** — CORE "reducer" (A4: document + reducer + wire are one file, ~4,000 lines with the persistence rows above) |

**The one table in this chapter's scope with NO test at all**: `BuiltInExtensionTable.cs` (303 lines, the 13 descriptors
and the exclusion list) is engine-bound — its descriptors carry `ActionServices`-typed delegates — so `Wavee.Tests`
source-includes `WaveeRegistryTable.cs`, `WaveeActionTargeting.cs` and `PinRowRule.cs` but **not** it
(`Wavee.Tests.csproj:322-324`). Nothing asserts a label, an `IconKey`, an accepted-mode set, or the deliberate
exclusions; the only record is the file's own doc comment (`:16-30`). That is the single highest-risk file in the port:
a re-author reading only the tests sees a registry with no contents. §0.18 exists for this reason — and 0.3 should add
the table test that 0.2.9 never had (a descriptor's `Key`/`AcceptedTargets`/`IconKey` are pure data; only `Run` is not).

---

## 9. Re-author notes

### 9.1 What must not be simplified

- **The re-hosting seam.** `ISidebarEditHost` (`SidebarEditSession.cs:34-61`) exists so `SidebarPropertyPanel` + the six `Cz*` rows + both pickers are mounted by TWO hosts — this page and the pane's per-section options popover — without a fork. Collapsing it back to "the panel takes the page" duplicates the controlled-input + rejection contract, which is the "same artifact defined twice" failure the whole sidebar architecture exists to prevent.
- **`RejectEpoch` in the mirror dep key.** Drop it and every rejected edit leaves a control lying about the saved state. It is `LayoutVersion * 397 + RejectEpoch` (`SidebarCustomizerControls.cs:248`) precisely so BOTH answers re-run the mirror.
- **`CzRow` instead of `SettingsCard`.** `SettingsCard`'s header lane has no line cap; in a 320-DIP column "Show in collapsed rail" plus its 11f sublabel wrapped to three lines and jammed the `ToggleSwitch`. The two-column contract (label `Grow=1 MinWidth=0 MaxLines 2 ellipsis`, control `Shrink=0` right-aligned, 10 DIP pad, 44 floor) IS the fix.
- **The ONE enum treatment.** `CzRow.Choice` decides from RESOLVED LABELS (≤4 choices AND every label ≤12 chars), so a long localization demotes itself to a dropdown instead of clipping. `SelectorBar` stays banned in the property panel.
- **Suppressing the Segmented pill.** `SegmentedCore` paints both a selected plate AND a 24×3 accent pill; two indicators for one value is the defect. Suppress through the public `PartSelectionPill` seam — no engine edit, and the 3-DIP slot stays so suppressing costs no relayout.
- **`PrimaryText = ""` not `null`** on the two pickers. `ContentDialog` reads `PrimaryText != ""` and falls back to a localized "OK"; `null` ships a stray dead OK.
- **The action picker's footer.** `IsPrimaryButtonEnabled` is read once at card-build time, so the commit button MUST live in `ContentDialog.Footer` over a shared model to disable itself until an action (and, when needed, a target) is chosen.
- **Icon fonts in the action picker.** Pass `icon.Font` through `Ui.Icon(..., family:)`. Two first-party actions live in the app-local `WaveeIcons` face and render as □ otherwise.
- **`BuiltInExtensionTable`'s exclusion list.** The comment at `BuiltInExtensionTable.cs:16-30` is the ONLY record of
  which verbs are deliberately unbindable and why (§0.18). No test guards it. Port the comment WITH the table, and add
  the table test 0.2.9 never had — a descriptor's `Key` / `LabelLocKey` / `IconKey` / `AcceptedTargets` are pure data.
- **`Destructive` and `RequiresConfirmation`.** Zero call sites in 0.2.9 and therefore the first things a re-author
  deletes. They are the only thing standing between a future bound `DeletePlaylist` and a one-click destroy, and the
  refusal-when-there-is-no-overlay (`WaveeActionDescriptor.cs:109-110,136`) is the half that actually enforces it.
- **`(Available, ReasonLocKey)`, never a bare bool.** Collapsing the 7-member `WaveeActionUnavailable` into "enabled" is
  the same class of loss as dropping `RejectEpoch` from the mirror dep key: the row still looks right and stops being
  honest. The four presentations (row tooltip · property-panel second line · picker `ReasonRow` · menu accelerator
  column) all read from that one enum.
- **`IWaveeExtension` / `IWaveeExtensionRegistrar`.** Two interfaces and one implementation-by-delegate looks like
  ceremony and is not: `BuiltInExtensionTable.RegisterAll` is written against the exact call shape the M4 source
  generator emits, and M3's sandboxed host replays a manifest onto the SAME registrar. Flatten them into a static table
  and the sandbox seam has to be re-cut later (`IWaveeExtension.cs:5-17`).
- **The miniature's realism.** It uses the live geometry ladder and real cover art. A box-and-line abstraction would break the one promise the confirmation makes.
- **`GoBack` is the ONE exit.** Done and Back are the same method. Do not re-introduce `Go("home")` for Done.

### 9.2 Traps

| trap | how 0.2.9 handles it |
|---|---|
| **Props freeze at mount** | every sub-component takes THIS page (a reference-stable holder of signals + delegates) as its single ctor arg and re-reads `LayoutVersion` itself, so a document edit re-renders it without the page rebuilding its children (`Page.cs:40-42`) |
| **`ComboBox.Create` freezes `Items`/`ItemEnabled`/`Width`** | `CzQueryBlock` puts the gate in the mount KEY (`"sort:custom"` / `"sort:nocustom"`), so ticking "Albums" remounts the combo the instant Custom order becomes illegal (`PropertyPanel.cs:503-511`) |
| **`NumberBox` affixes freeze** | `CzNumberRow` keys the inner `CzNumberSpinner` by the authoritative document value (`"number:{sec}:{field}:{value}"`), so both arrows are born from the real value and remount after every accepted change (`SidebarCustomizerControls.cs:510-518`) |
| **`ToolTip.Wrap` sets `AlignSelf = Start`** | which opts out of the header row's `AlignItems = Center` and pins the back arrow to the TOP of the 64-DIP header. The extra plain `BoxEl` wrapper is **load-bearing** (`Page.cs:253-268`) |
| **`.Interactive(...)` overwrites all three fills** | selection-aware rows (template cards, action rows) style their own four-state ramp explicitly instead (`Palette.cs:476-480`, `Pickers.cs:435-438`) |
| **Hover cascades only for opacity/scale reveals** | the palette's "+" chip uses `Opacity`/`HoverOpacity`; a `HoverFill` there would never be reached (`Palette.cs:361-372`) |
| **Resolving loc in a render subscribes to the culture epoch** | every `MenuFlyout` is built at OPEN time, never at render (`CzMenuButton`, `SidebarLayoutMenu.cs:18-24`) |
| **`Flow.KeepAlive` means no unmount** | the page's enter/leave effect resets ergonomics but is ALLOWED to be missed; what arms the canvas is derived from the active ROUTE in `CuratedSidebar.ReadEditSession`, not from a flag someone has to clear (`Page.cs:145-156`, `SidebarEditSession.cs:71-78`). NB the ring is **3 entries**, not the 8 both of those comments still claim (`ContentHost.cs:106` — 8 was reverted: scene nodes 494 → 15 668 on a route tour). A shorter ring makes the "cleanup may never run" argument *stronger*, not weaker: the page is evicted sooner, and its cleanup then runs at an arbitrary later moment. |
| **A rejected command must not bump `LayoutVersion`** | `SidebarPreferences.Dispatch` returns early on `!Changed`: no undo push, no version bump, no commit (`SidebarPreferences.cs`) |
| **Static field initializer order** | `ExtraDestinationRoutes` is declared ABOVE `Destinations` because C# runs static initializers in TEXTUAL order; below it the Destinations group would ship empty (`CustomizerLayout.cs:192-194`) |
| **Zero-allocation scroll frames vs per-row richness** | reconciled OUTSIDE this page: the planner allocates no string (a row's `Key` is always an existing string), row/entry storage is a caller-owned `SidebarPlanBuffers`, `SidebarRowDiff` bumps only the rows whose content moved, `SidebarRowResolve.Flipped` bumps only the rows whose selection flipped, and `SidebarEntriesShadow` suppresses the publish entirely when nothing changed. The *page* is not on a scroll-frame budget (it is a form with ~40 rows); the *canvas it edits* is, and every one of those five mechanisms must survive the port |

### 9.3 Where the plan is wrong or too thin for this surface

1. **§2's tree has no home for the customizer PAGE.** `Shell/Sidebar.UI.cs` is budgeted 5,000 lines for "the sidebar"; the customizer is a routed content-host destination, not sidebar chrome, and it is 4,247 lines in `Curated/` alone before the pickers' engine-bound halves. **Settled** (arbitration 2026-09-12, A4): `Shell/Sidebar.Customizer.UI.cs` (~4,500) is a named file in the five-file set from day one — not a partial invented when `Sidebar.UI.cs` overruns — and it is **sequenced last** in owner J's wave (§9.3.7). §2 of the plan still has to gain the row.
2. **§2's Sidebar budget is off by ~3×.** 0.2.9 is `Features/Sidebar` 29,025 + `Wavee.Core/Sidebar` 2,526 = **31,551 lines**; §7's risk table even says "the largest port (31k lines)". §2 budgets 4,500 + 5,000 + 800 = **10,300**. Honest estimate after removing the hydration/projection copy step, the `Entries` cell, the shadow gate and the pin hydration side-cache (≈4,000 lines of the 31.5k), and after the fold removes ~15% of ceremony: **~23,000-25,000 lines**, and the arbitration of 2026-09-12 (A4) fixes how they are split — `Sidebar.cs` **~5,000** · `Sidebar.Doc.cs` **~4,000** · `Sidebar.UI.cs` **~7,500** · `Sidebar.Customizer.UI.cs` **~4,500** · `Sidebar.Host.cs` **~2,500** = **~23,500**. The only change from this chapter's earlier list is that the document + reducer + wire come OUT of `Sidebar.cs` (9,000 − 4,000 = 5,000) into ch. 25's `Sidebar.Doc.cs`, whose ~4,000 is grounded in `Persistence/**` 1,491 + `Wavee.Core/Sidebar/**` 2,526 = 4,017. Five named files up front, no partial invented mid-wave.
3. **§4.12's `RowStyle` has no analogue for a SECTION.** The sidebar's rows are not one shape parameterised by an edge — they are 14 `SidebarRowKind`s at a *per-section uniform height* (iron rule 4), with the kind table (`AllowsDisplayField`) deciding which options even exist. `Track.Row(item, style)` cannot serve the sidebar; the planner + `SidebarPaneSlot` must stay their own thing.
4. **§4.13's "pages demand their whole model on mount" needs an exemption note for the sidebar.** The sidebar is not a page: it is always mounted and its model is the user's whole library. `Ensure(User.Me, Rootlist | SavedAlbums | FollowedArtists | SavedShows)` once at Boot is the right shape — but the *extension sources* (Concerts with a 30-minute TTL, New releases via the What's New feed) are genuinely incremental and must keep their own `EnsureFresh` kick on the rebuild path. The plan does not describe that seam.
5. **§4.14 makes the library edges but not the ROOTLIST TREE shape.** `RootlistEdge` carries `Position, Depth, Kind (item/folder-start/folder-end), FolderName, AddedAt` — enough to reconstruct a tree, but every consumer in this chapter needs `ParentFolderId` (the containment definition that makes the rail flyout's rows and its "N items" agree) and a folder's own group id. Add both, or the folder flyout, the drop mapper, the outdent gesture and `FolderHeaderIndexOf` all have to re-derive them per frame.
6. **§4.1's `Known` bitmask has no per-EDGE readiness that the sidebar's chip gate can read.** `EdgeTable.State` (0 unknown / 1 partial / 2 complete) exists, which is right — but §4.13's page example never mentions gating UI on it. The qualifier chips MUST stay hidden while `Rootlist.State != 2`, or they flicker in mid-load. State that on the chip gate, not in each surface.
7. **§5 Wave 4 gives owner J "the sidebar platform" including the customizer, in one wave, alongside three other owners.** The reducer + planner tests (`SidebarLayoutReducerTests` ~2k lines, `SidebarRowPlannerTests`, `SidebarCustomizerLayoutTests`, `SidebarEditPlanTests` — ~6,100 test lines in this chapter's scope alone) port first and must stay green throughout, which the plan says; it does not say that the **customizer UI is the last thing in the wave** and can slip to Wave 5 without blocking the `--fake` gate (the gate is "a working sidebar", not "a working customizer"). **Now decided** (arbitration 2026-09-12, A4): `Sidebar.Customizer.UI.cs` is sequenced **LAST** in owner J's wave, and both this chapter's header and ch. 25's say so. §5 of the plan still has to record the sequencing.
8. **Missing from §2 entirely**: the layout **persistence** file (`sidebar-layout.json` store + wire + migrations, 1,491 lines) has no named home. It is not `Entities/Store.cs` (that is the entity cache, deleted and rebuilt on a schema change); this document is USER DATA and must never be deleted on a version bump. **Named** (arbitration 2026-09-12, A4): **`Shell/Sidebar.Doc.cs`** — the document, the reducer and the wire are one concern and one file (~4,000 with `Wavee.Core/Sidebar/**`'s reducer set), with the file I/O and the debounce staying in `Shell/Sidebar.Host.cs`. The plan's one-line "800 lines" for `Sidebar.Host.cs` is wrong either way; §9.4 restates it as ~2,500.

9. **§5 leaves `Actions/Extensibility/**` un-owned, and BOTH Wave 4 owners consume it.** Owner I takes "WaveeCommands +
   `Actions/*` → one table"; owner J takes "the sidebar platform". `Actions/Extensibility/**` (946 lines) +
   `WaveeActionDescriptor.cs` (227) sit exactly between them: the descriptor shape and the registry belong to I's one
   action table, but the only consumers in 0.2.9 are J's — the customizer's action picker, the property panel's item
   rows, and the pane's bound `ActionRow`. **Recommendation: assign it to I**, with two conditions, because the cost of
   getting this wrong is a second descriptor type:
   - I ships `Shell.Actions` (the descriptor + `WaveeActionTargets` + the registry table + the 7-reason vocabulary)
     **before** J's customizer UI lands — it is a dependency of the picker, not a peer of it.
   - the descriptor's shape is frozen by §7's DATA GAPS row (all twelve members, not the four visible ones). J must not
     be able to "extend" it from the sidebar side; a bindable field the sidebar needs is a change to I's table.
   `PinRowRule` is the one file that should go the other way: it is pin semantics, it lives beside `SidebarPinId`, and
   `25-sidebar.md` already owns the menu that shows its row. Either way, **say it in §5** — today the file set is named
   by no chapter and no owner, which is how 1,200 lines get ported twice or not at all.

### 9.4 Line budget

| | lines |
|---|---|
| 0.2.9, this chapter's scope | `Curated/` 4,247 + `Persistence/` 1,491 + `Data/` 7,161 + `SidebarEditSession` 164 + `SidebarLayoutMenu` 153 + `SidebarIcons` 74 + `SidebarMiniature` 343 = **13,633**, plus the action platform this surface BINDS — `Actions/Extensibility/**` 946 + `WaveeActionDescriptor` 227 + `ActionIcons` 87 = **1,260** (shared with `01-track-row.md` and `19-shell-overlays.md`; see §9.3.9 for who ports it) = **14,893** (plus the editor half of `SidebarPreferences.cs` and the `Wavee.Core/Sidebar` reducer set, shared with `25-sidebar.md`) |
| plan §2 target | not itemised; this chapter's share of `Sidebar.cs` + `Sidebar.UI.cs` + `Sidebar.Host.cs` (10,300 total for the WHOLE sidebar platform) |
| honest estimate | **~10,500**: customizer page + palette + preset/hidden/advanced blocks ~1,900; controls + property panel + pickers ~2,400; miniature ~350; pure customizer tables (`Palette`, `DisplayValues`, `ConfigJson`, `NumberEdit`, `QueryPanelShape`) ~600; persistence (store + wire + migrations + defaults) ~1,450; pipeline pure rules (geometry, extents, diff, resolve, pill, sort, search, edit plan, drop resolver/decision/nav/selection/flyout, first-seen, recency, stage hold, clamp, pin id, sync rules, source map, source contract) ~2,900; sources ~900. **The action platform is NOT in this number** — its ~1,100 ported lines land in `Shell.cs`'s action table (§9.3.9), with only `PinRowRule`'s ~30 coming to `Sidebar.cs` |
| the same estimate mapped onto the one five-file set (A4), so chs. 25 and 26 count each line once | `Sidebar.Customizer.UI.cs` **~4,500** (all of it: 1,900 + 2,400 + the miniature 350, minus the fold) · `Sidebar.Doc.cs` **~4,000** (persistence ~1,450 + the document/reducer/wire from `Wavee.Core/Sidebar/**` ~2,550, shared with ch. 25 and counted **here**) · ~**3,500 of `Sidebar.cs`** (pipeline pure rules ~2,900 + the customizer tables ~600) · ~**900 of `Sidebar.Host.cs`** (the four extension sources) = **~12,900** of the platform's ~23,500. Ch. 25 carries the other **~10,600** (`Sidebar.UI.cs` 7,500 + the planner/geometry half of `Sidebar.cs` ~1,500 + the store/binder half of `Sidebar.Host.cs` ~1,600) and §9's budget there states it the same way |

---

## 10. Parity checklist

Verify against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`
Route in every case: open the sidebar quick menu (the `SplitView` glyph on the pane's first section header) → **Customize sidebar**, or deep-link `sidebar-customize`. Unless stated, window = **1280 × 860**, design = Custom (Wavee Curated).

1. **Column width.** Static capture @ 1280. The content column's card edges sit at x = 16 and x = 704 measured from the content host's left edge (720 cap − 16 padding each side). It is LEFT-aligned; the space to its right is empty page ground.
2. **Column width @ 2560.** Same capture at a maximised window on a 2560-DIP monitor. The column does NOT grow past 720 and does NOT centre.
3. **No breakpoint.** Resize continuously 520 → 1600. The block ORDER (Designs → Templates → Add a section → Hidden sections → Advanced) never changes, nothing is hidden, nothing reflows into a second column.
4. **Header height.** Static capture. The header band is 64 DIP; the divider beneath it is 1 DIP.
5. **Eyebrow.** The header's first line reads the ACTIVE TEMPLATE ("WAVEE CURATED") in accent-text ink, above "Customize sidebar" at 16f semibold.
6. **Back arrow centring.** Static capture: the `←` is vertically centred in the 64-DIP header, NOT pinned to its top (the `ToolTip` `AlignSelf` trap).
7. **Saved dot.** Healthy state: a 6-DIP green dot + "Saved locally" sits between the title lane and the Undo button, right-margin 4.
8. **Undo tooltip.** Add a section, hover Undo → "Undo: Add section". Hover Redo before any undo → the bare "Redo".
9. **Undo/Redo enablement.** On a freshly-opened page both are disabled; after one add, Undo is enabled and Redo is not.
10. **Done == Back.** Click Done from a page reached via the quick menu; then repeat with Back. Both return to the SAME destination (whatever was open before), never Home.
11. **Design segmented.** The control shows **Custom** selected on arrival even when the menu was opened from Classic; the row's label is the consequence sentence, not a second "Sidebar design".
12. **Design switch reversibility.** Click Classic. The docked sidebar switches to Classic in the same frame, the document is untouched, and clicking Custom restores the edited sidebar exactly.
13. **Show section contents.** Toggle ON → every visible section on the canvas reveals its rows AND section drag disarms (grip handles stop lifting; the card "…" menu's Move up/down still work).
14. **Template list.** Exactly 5 cards, in order: Wavee Curated, Classic-inspired, Library-inspired, Minimal, Blank. The active one wears a filled plate, a radio bullet in accent, and a trailing ✓ (an inactive card has a 16-DIP blank tail).
15. **Template confirmation skip.** On a pristine Curated document, click "Classic-inspired" → NO dialog; the sections change immediately and one Undo restores them.
16. **Template confirmation dialog.** Edit anything first, then click "Minimal". A 480-wide dialog appears with a 220-tall miniature showing a 258-DIP quarter-scale sidebar beside a workspace mock. Rows use real cover art.
17. **Miniature determinism.** Open and cancel the same confirmation three times: the miniature shows the SAME playlists and covers each time.
18. **Palette order.** The first group in the palette card is **PAGES**, above NAVIGATION. Frame-order capture: it is there on the very first paint, not after a tick.
19. **Typing "home".** Type `home` in the palette search: the first result is the **Home** page row (subtitle "Add a shortcut to this page"), not "Links".
20. **Token-wise search.** Type `top art` → "Artist top tracks" matches.
21. **Query cleared after an add.** Type `pinn`, click the Pinned row, observe the search box is empty and the full palette is back.
22. **Query cleared on leave/enter.** Type `zzz`, press Done, re-open the customizer → the box is empty.
23. **Query survives the mode switch.** Type `queue`, click the **Extension** row → the pick list is still filtered by `queue` and the search box still shows it.
24. **Empty search line.** Type `zzz` → "Nothing matches "zzz"" (NOT "No results for …", which is the library list's line).
25. **Section budget.** Read `{used}/{max}` in the palette head; add sections until 40/40 and confirm the number turns red and a click then raises the inline "Your sidebar is full…" bar.
26. **Reject strip auto-dismiss.** Frame recording: the informational bar disappears ~4 s after it appeared; a second rejection before then re-arms the 4 s.
27. **Every rejection speaks.** Provoke `DuplicateItem` (add the same destination twice into one Links section) and `KindNotDuplicable` (duplicate a Pinned section from the canvas card menu). Both produce a bar with text, never a silent no-op.
28. **Appends-to caption.** Expand a Links card on the canvas, then look at the palette's PAGES header: it carries "adds to "<name>"" in accent ink, and the destination rows lose their drag affordance.
29. **Bare Links opens a picker.** Click the **Links** row: the item picker dialog opens FIRST. Cancel it → no section was added (the canvas is unchanged and Undo is not newly enabled).
30. **Destination = one undo step.** Click **Albums** in PAGES → one new Links section containing Albums; ONE Undo removes the whole thing.
31. **Recently played = two steps.** Click **Recently played** → the section appears configured to the play log; it takes TWO Undos to remove (documented deviation).
32. **Palette row hover.** Hover capture: the row plate lights to `FillSubtleSecondary` and the trailing "+" chip brightens from 45% to 100% over ~83 ms. The chip does not respond to its own hover.
33. **Palette drag.** Drag the **Divider** row onto the canvas: a chip appears only after ~2× the normal drag distance; hovering a section card shows "Add here"; dropping inserts the divider ABOVE that card.
34. **Drag refusal caption.** At 40/40, drag a row over a card: the caption reads "Your sidebar is full — remove a section first".
35. **Click-only rows carry no drag.** Press and drag **Links**, **Action shortcut**, **Extension** and **Recently played** by 40 DIP each: no chip is produced.
36. **Contribution pick slide.** Click **Extension** → the card slides in from the right over ~250 ms; click ‹ → it slides back from the left. Frame recording.
37. **Contribution names.** In pick mode, `wavee.queue` shows as "Queue / What's playing next" — the raw id appears at most once, as the subtitle of a source with no name.
38. **Hidden sections empty state.** With nothing hidden: one dimmed row reading "Nothing is hidden." at 40% opacity.
39. **Hidden sections recovery.** Hide a section from the canvas card's eye, then confirm it is listed here with its kind glyph and a Show button; click Show and confirm the section returns to the live sidebar in the same frame.
40. **Hidden child sections.** Hide a section nested inside a Group; confirm it is still listed here.
41. **Advanced path.** The Layout file row shows the real `%LOCALAPPDATA%\Wavee\WaveeMusic\sidebar-layout.json` path in 11f tertiary, wrapping to ≤2 lines, with [Show in Explorer] and [Copy path].
42. **Property panel row rhythm.** Open a section's "…" options popover on the canvas. Every row has the same 10-DIP vertical padding and a 44-DIP floor; the right-hand column edge is identical for every dropdown, number box and text box (264 wide).
43. **Segmented vs ComboBox.** In the same popover: **Density** renders as a 3-segment control (30 tall) with NO accent underline; **When empty** renders as a ComboBox (because "Show a compact hint" busts the 12-char budget).
44. **No SelectorBar in the panel.** Nothing in the property popover is a tab strip.
45. **Max items slider.** Drag it to 0 → both the header caption and the thumb tooltip read the WORD "All".
46. **Rejection snap-back.** With the layout at the section cap, try to change an option that the reducer refuses; the control visibly returns to the document value rather than staying where you put it.
47. **Live collapse.** Toggle "Collapse section" in the popover → the section collapses in the docked sidebar in the same frame (the old "Start collapsed" row that changed nothing must be absent).
48. **Grid columns row.** Set Layout = Grid → a **Columns** number box (2-4) appears; set Layout = List → it disappears.
49. **Rename commits on blur.** Type a new title in the popover's rename box and click outside the popover (which dismisses it): the rename is committed, not dropped.
50. **Item icon menu.** Open an item's "…" → a disabled "Icon" header followed by 30 radio items in the whitelist order, starting MusicNote and ending Download; clicking the currently-checked one clears the override.
51. **Item picker geometry.** Open Add… from an item list: a 480-wide dialog, a 2-tab `SelectorBar`, a 432-wide search box at 32 tall, and a 320-tall scroll list of 40-DIP rows. Only one button: Cancel.
52. **Item picker commits on click.** Clicking a row adds the item and closes the dialog — there is no OK to press.
53. **Entity-only picker.** From a Spotlight section's Add…, the `SelectorBar` is absent and only library entities are listed (no routes, no folders, no tracks).
54. **Action picker footer.** Open **Action shortcut**: Cancel (Standard) then Add (Accent) in the footer, each ≥96 × 32. Add is disabled until an action is picked.
55. **Action row selection.** Click an action: a 3 × 20 accent bar appears at its left, its icon plate turns accent-subtle, and a ✓ appears at its right — the action's own icon is NOT replaced by a radio bullet.
56. **Action icon fonts.** Scroll to "Play next" / "Add to queue": both show real glyphs, not □.
57. **Mode row.** Pick an action accepting more than one target mode → an "Acts on" ComboBox appears; picking "A chosen item" reveals a target row with its own Add… button.
58. **Fixed-track target.** With nothing playing, the FixedTrack target row's Add… is disabled; start playback and it enables and captures the current track's uri.
59. **Corrupt banner.** Corrupt `sidebar-layout.json` (replace it with `{`) and relaunch → a Warning `InfoBar` naming the preserved path, with [Copy path] and [Start fresh]; the "Saved locally" dot is absent; the sidebar shows Wavee Curated.
60. **Save-fault banner.** Force a document over 2 MiB → an Error `InfoBar` whose subtitle carries the byte counts in parentheses; the dot is absent; dismissing it and provoking a DIFFERENT fault re-shows it.
61. **Pipeline: skeleton, not blank.** Launch `--fake` with a section whose source is pending; capture frame 1 of the sidebar: exactly 3 skeleton rows at the section's own row height, never 44 for all of them.
62. **Pipeline: extents before realize.** Expand a rootlist folder inside a long tree; frame recording: rows above and below the folder do NOT shuffle as the inserted band realizes.
63. **Pipeline: no churn.** Play a track and let it change; frame recording of the sidebar: no rows re-render on the track boundary unless their content actually changed.
64. **Pipeline: one pill.** Navigate quickly between two sidebar rows; frame recording: exactly one accent pill is lit at any moment, and it travels toward the new selection rather than cross-fading in place.
65. **Pipeline: mid-drag freeze.** Start dragging a playlist in the tree and force a library refresh (fake backend tick); the rows under the pointer do not re-key and the drop lands where the caret pointed.
66. **Pipeline: drop cue invariant.** Hover the top 30% of a playlist row (line), the centre of a folder row (plate), and the bottom 30% (line at the resolved depth). Never both, never an armed row with neither.
67. **Pipeline: depth hysteresis.** Hold the pointer at a depth boundary in the bottom band of the last child of a folder and jitter ±3 DIP horizontally: the caret does NOT flicker between depths.
68. **Pipeline: Concerts prompt.** With no location set, the Concerts section shows exactly one 56-DIP "Set your location" prompt row, not an empty caption and not a permanent skeleton.
69. **Pipeline: offline is empty, not broken.** Run `--fake` offline: the New releases section shows its empty caption, never a skeleton that never resolves.
70. **Pipeline: unknown section round-trips.** Hand-add a section of kind `"futureThing"` to `sidebar-layout.json`, relaunch, make one edit, then re-read the file: the unknown section is still there at its original index with every member intact, and it is listed in the customizer's Hidden sections group if hidden.

### Added by the fidelity audit (the states and values the first pass missed)

71. **The design segmented is the STOCK control.** Static capture of the "Sidebar design" row: the segmented bar is **34 DIP tall at 14f**, and the selected segment carries the **24 × 3 accent pill** under its label. Compare against the Density control in a section's options popover, which is 30/12f with NO pill. Both must look exactly as they do in 0.2.9 — this is the one place the app deliberately ships two segmented treatments, and a 0.3 port that silently unifies them has changed the page.
72. **Palette row count.** With developer mode OFF, count the palette's rows at an empty query: **30** (11 PAGES + 19 sections). Turn developer mode on: **31**, the new row being API console under PAGES.
73. **Contribution pick order.** Click **Extension**. The list is in REGISTRATION order and holds **9** rows: library, history.visited, history.played, playlistTree, Artist top tracks, newReleases, concerts, Queue, Now Playing. Exactly three wear localized names; the other six show the contribution half of their id as the title and the full id inside the "no name yet" subtitle.
74. **Pick mode with no extensions.** Launch a build with an empty registry (or fake one) and enter pick mode: one dimmed 12f note reading "Manage extension", NOT an empty card.
75. **Pick mode empty search.** In pick mode type `zzzz`: the SAME "Nothing matches "zzzz"" line the section list shows, and the search box is still present (the mode never drops it).
76. **Corrupt banner shows a PATH.** (0.3 FIX, not 0.2.9 parity — 0.2.9 prints the store's diagnostic sentence in the `{path}` slot.) Corrupt the file, relaunch: the banner names `%LOCALAPPDATA%\Wavee\WaveeMusic\sidebar-layout.json`, `[ Copy path ]` puts THAT on the clipboard, and the parser's reason rides as a parenthetical clause.
77. **A newer document is not called corrupt.** (0.3 FIX.) Hand-set `"version": 99`: 0.2.9 shows the same "couldn't be read" warning with a Start-fresh button. 0.3 must say a newer Wavee owns the file — and must not offer to move it aside by default.
78. **A .bak recovery is silent.** Corrupt the primary but leave a good `.bak`: the sidebar comes back with the backup's sections, the header shows "● Saved locally", and NO banner appears.
79. **Reverse order disables at Custom order.** In an EntityList popover set Sort = Custom order (only offered for a playlists-only query): the "Reverse order" switch dims to 40 % and stops responding.
80. **The Add buttons disable at the item cap.** Fill a Links section to `ItemCapacity`: both [Add…] and [Action shortcut] go disabled. From an **EntityEmbed** section: there is no [Action shortcut] button at all, and [Add…] stays enabled forever (a second pick retargets the spotlight).
81. **A Divider's options popover.** Open "…" on a Divider card: exactly two groups — GENERAL holding only the Hidden switch (no rename row), then the red Remove section button. No Content, no Appearance, no Behavior.
82. **Extension section, three degraded notes.** (a) Point a section at an unregistered contribution id → one "Manage extension" note under CONTENT, with the read-only source-id row above it. (b) Set its `schemaVersion` past the build's → the same note, and nothing in the document changes. (c) Blank its extension ref → "Pick a contribution first."
83. **Blank template miniature.** Click **Blank** on an edited document: the confirmation's miniature pane is NOT empty — it centres a "+" glyph over the template's own description; the workspace half is unchanged.
84. **Grid template miniature.** Apply a template with a Grid-presentation section and re-open a confirmation: that section draws two 48-DIP cover cells side by side instead of rows.
85. **Hidden sections are absent from the miniature.** Hide a section, then open any template confirmation: the miniature's CURRENT-document side never shows it (the miniature renders what the sidebar renders, not what the editor lists).
86. **Navigation picker subtitles.** Open Add… → Navigation: every row has a second 11f line carrying its raw route key (`home`, `liked`, `local`). Typing `local` matches the Local files row through that key.
87. **The popover's rejections are mute.** Provoke a rejection from a section's "…" options popover (e.g. an option the reducer refuses): the control snaps back and **nothing says why** — 0.2.9 has no message surface there. Track this as a 0.3 improvement, not a parity item.
88. **Undo has no accelerator.** Press Ctrl+Z on the customizer page: nothing happens in 0.2.9. 0.3 is expected to bind it; verify the bound version undoes exactly one step and matches the Undo button's tooltip.
89. **Keyboard section reorder.** With the canvas armed (contents OFF, no card expanded), focus a section card, press Space, Down, Space: the section moves one slot and the screen reader hears "Picked up X. 2 of 7." → "Moved X. 3 of 7."; Esc after a Space restores the original slot.

### Added by the completeness audit (the action platform, which no chapter had named)

90. **Action picker row count and order.** Open **Action shortcut** and scroll the 260-tall list: exactly **13** rows, in
    this order — Play, Play next, Play after, Save to Liked Songs, Save to Your Library, Open, Go to album, Go to artist,
    Copy link, Go to song radio, Go to artist radio, Pin to sidebar, Unpin from sidebar. Registration order, first-party
    first. A 14th row means someone "completed" `BuiltInExtensionTable` — check §0.18 before accepting it.
91. **The third row reads "Play after", not "Add to queue".** Only its registry KEY (`wavee.addToQueue`) carries the
    queue word. Same check for row 4/5: "Save to Liked Songs" and "Save to Your Library", never two rows both reading
    "Save".
92. **Every row's subtitle is its accepted target set, joined with " · ".** Spot-check three: Play → "A chosen item · A
    chosen track"; Go to album → "Now Playing" **only**; Copy link → all three. No row's subtitle reads "Nothing (a
    global action)" — that mode exists in the catalog and no first-party descriptor accepts it.
93. **A toggle's picker row is drawn unchecked.** With a track already in Liked Songs, open the picker: row 4 still shows
    the outline heart, not the filled one. (The filled variant is a *bound-surface* state, not a picker state.)
94. **The four reason surfaces, one vocabulary.** Bind an action to **Now Playing** with nothing playing, then read the
    same sentence — "Nothing is playing" — in all four places: (a) the picker's `ReasonRow` while the dialog is open,
    (b) the property panel's item row second line, (c) the tooltip of the row in the docked sidebar, (d) that row's
    overflow menu item, where it rides in the **accelerator column**. Then start playback and confirm all four clear.
95. **A bound row never vanishes.** Hand-edit `sidebar-layout.json` to point an action item at `"providerId": "nobody",
    "actionId": "nothing"`, relaunch: the row is still there, disabled, tooltip "This action is no longer available",
    and its `LabelOverride` (if any) is still its title. Remove the override and the title falls back to the raw
    `nobody.nothing`. Re-adding a matching descriptor makes it live again with no edit.
96. **The disabled row is the same width as the enabled one.** Static capture of a section holding one live and one
    unavailable action row: both fill the pane's content lane. (The `ToolTip.Wrap(..., grow: 1f)` regression makes the
    disabled one shrink to its label.)
97. **The two hosts' glyphs differ on purpose.** Bind **Play next** and compare: the docked sidebar draws the layered
    themed "PlayNext" mark at 16f; the action picker draws the U+E900 `WaveeIcons` fallback at 14f on a 28-DIP plate; the
    property panel's item row draws `Icons.RefineSparkle` at 13f on a 24-DIP plate. Three marks, one binding. Record
    whichever 0.3 chooses — but neither may be □ (parity item 56).
98. **First-wins registration.** Not observable in the shipping app (no second registrar exists); assert it in the ported
    tests instead — `WaveeExtensionRegistryTests.DuplicateKeyIsRejectedAndTheFirstRegistrationWins` and
    `EnumerationPreservesRegistrationOrderForThePicker` must both stay green, because the picker's row order and the
    "no third party can shadow a first-party action" guarantee are the same mechanism.
99. **Registration refusals are invisible — confirm that is still true, then fix it.** 0.2.9 has no UI reading
    `WaveeExtensionRegistry.Diagnostics`. This is a 0.3 *addition*, not a parity item: one read-only list on the
    diagnostics page, never a toast.

---

## 11. Audit log

Adversarial re-read of every 0.2.9 source in scope against every number, state and claim in §0-§10. Each line is one
correction made in place. "wrong" = the chapter stated something the code contradicts; "missing" = a state, element or
rule the code has and the chapter did not; "unverified" = a claim I could not confirm and have marked as such;
"overclaim" = true of one host/case and written as if universal.

| # | § | kind | correction |
|---|---|---|---|
| 1 | §1.1, §9.2 | **wrong** | `KeepAlive` `MaxEntries` is **3**, not 8 (`ContentHost.cs:106`; the 8 was reverted for scene-node growth). Both the tree and the trap repeated a stale figure that `SidebarEditSession.cs`'s own comment still carries. |
| 2 | §0.13, §2/W1, §2/W2, §3 | **wrong** | The PAGE's design segmented is **stock** `Segmented.DefaultStyle` (h 34, 14f, item min-w 52, accent pill VISIBLE), not the compact pill-suppressed one — `SidebarCustomizerPage.cs:775` passes no options. Only `CzRow.Choice` suppresses the pill. W1, W2, §0.13 and the §3 table now carry both treatments as separate rows. |
| 3 | §7.1, §8 | **wrong** | `SidebarPalette.Sections` holds **19** entries (4+3+3+4+3+1+1), not 18; `All` is 31 rows and `Filter` offers 30 with developer mode off. The "18" came from the source file's own stale comment; W1's wireframe already enumerated 19. |
| 4 | §2/W7 | **wrong** | The corrupt banner does **not** print a path: `prefs.FaultDetail` is the store's diagnostic SENTENCE (`SidebarLayoutStore.cs:192-224`), and it is interpolated into `{path}` — so the bar reads "…kept the unreadable file at The sidebar layout contains invalid data (JsonException). and loaded…" and `[Copy path]` copies that string. Redrawn as it really renders, flagged as a 0.3 fix, parity item 76 added. |
| 5 | §2/W3 | **wrong** | The contribution pick list is in **registration** order and holds **9** rows (library, history.visited, history.played, playlistTree, artistTopTracks, newReleases, concerts, queue, nowPlaying — `WaveeBuiltInDataSources.cs:39-47`), of which only 3 are named by the palette table. The old wireframe showed 4 rows headed by Queue. |
| 6 | §2/W12 | **missing** | Every Navigation picker row carries its **raw route key** as an 11f tertiary subtitle (`Pickers.cs:171`) — and that key is half of the search predicate. The palette's Destinations rows use a different subtitle rule; both are now stated. |
| 7 | §2/W12-13 | **missing** | Item picker: `DefaultButton = DefaultBtn.Close` (Enter and Esc both cancel, `:45`); the no-match line interpolates the NORMALIZED (lower-cased) query while the palette's interpolates the raw one. |
| 8 | §0.3 | **overclaim** | "Every rejection says something, inline" is true of the PAGE host only. `SidebarEditSession.Apply` (`:155-163`) bumps the epoch and nothing else — a rejection provoked from the pane's options popover snaps the control back **mutely**. Recorded, with a 0.3 direction and parity item 87. |
| 9 | §2/W21 | **wrong** | Ordering: `slice.NeedsPrompt` is tested **before** `State == Pending` (`SidebarRowPlanner.cs:380-382`), so a pending source that needs a prompt never shows skeletons. Also clarified that W21's trailing "164 / 168 / 191" are line numbers, not heights (32 / 56 / 48 or 56), and that `PromptHeight`'s reason flag is `Kind != Concerts`. |
| 10 | §8 | **wrong** | Test-fact counts: `SidebarPalette` has **12** facts (`:24-279`), not 11; `SidebarDisplayValues` **5** (`:289-415`), not 6; `SidebarConfigJson` **6** (`:426-506`), not 5. Every other test file name and line count in §8 was verified correct against `Wavee.Tests`. |
| 11 | §7 DATA GAPS | **wrong** | The miniature also indexes `FakeData.Playlist(2)` and `(7)` — the grid-strip cells (`SidebarMiniature.cs:128`). The gap row listed 1, 3, 5, 8, 10, 12, 14 only, so a `SeedFake` built to that list would break a Grid template's confirmation. |
| 12 | §1.1 | **missing** | The property panel's group cards are **conditional** — Content / Appearance / Behavior are added only when non-empty (`PropertyPanel.cs:85-87`), so a Divider shows exactly General + the danger button. Tree and parity item 81 added. |
| 13 | §1.1 | **missing** | The Extension block's four states: the read-only source-id row, plus three degraded notes (ref not well-formed → "Pick a contribution first."; source unregistered → "Manage extension"; document schema newer than the build → "Manage extension", change nothing) — `PropertyPanel.cs:234-271`. Parity item 82. |
| 14 | §1.1 | **missing** | A whole table for `SidebarConfigFieldKind` → control family (Bool/Int/Enum/EntityUri/UriList/String), including the Enum→String fall-through on empty `EnumValues`, the `Max > Min ? Max : 500` int ceiling, the "artist" key heuristic on the EntityUri picker, and `EnumLabel`'s 13 reused catalog words. |
| 15 | §1.1, §2/W15 | **missing** | Item-block affordance states: the Pinned 0-item `EmptyRow` short-circuits before the buttons; [Add…] is enabled as `EntityEmbed \|\| items < ItemCapacity`; [Action shortcut] is **absent entirely** for EntityEmbed and disabled at the cap (`:298-312`). Parity item 80. |
| 16 | §2/W15 | **missing** | "Reverse order" is disabled while Sort == Custom order (`:533`); the Sort combo is always a 5-choice ComboBox whose 5th item is `itemEnabled` only for PlaylistTree or a playlists-only query. Parity item 79. |
| 17 | §2/W11 | **missing** | Three miniature branches were undrawn: the **Blank** template's centred "+"-over-description empty state (`:66-79`), the **Grid** presentation's two 48-DIP cells (`:123-131`), and the fact that hidden sections are skipped (`:103`). Parity items 83-85. |
| 18 | §2/W3, §1.1 | **missing** | The palette's two other empty arms: an empty registry in pick mode → the "Manage extension" note (`Palette.cs:271-275`), and a no-match query in pick mode → the same "Nothing matches" line (`:298`). Parity items 74-75. |
| 19 | §2/W14 | **missing** | Seven action-picker states: empty registry note, no-selection (the whole tail is unbuilt), ≤1 accepted mode (ModeRow is a 0-height box), no target needed, the destructive glyph tint while unselected, re-binding pre-fill, and `Choose()`'s mode+target reset. |
| 20 | §2/W7 | **missing** | `SidebarLoadFault` has **four** members and ONE banner covers three of them (Corrupt / TooNew / Unreadable) with the same "couldn't be read" wording, while a `.bak` recovery shows **no banner at all** and keeps writes enabled (`SidebarLayoutStore.cs:155-184`). Parity items 77-78. |
| 21 | §3 | **missing** | The three glyph tables this surface owns (`CzGlyphs.ForKind` 13 kinds, `CzGlyphs.ForName` 18 names, `SidebarIcons.Glyph` 30 names + `ForEntityKind`), their neutral defaults, and why the 18- and 30-name vocabularies must stay separate. Nothing in the chapter had recorded a single glyph mapping. |
| 22 | §3 | **missing** | Token rows for the group card's own 4-DIP outer gap, the group head row, and the preset block's 12-DIP stack gap — every other gap on the page was tabulated. |
| 23 | §3 | **missing** | `CzRow.Header` (`SidebarCustomizerControls.cs:250-254`) is **dead code** — no call site anywhere in the app. Marked so nobody ports it. |
| 24 | §5 | **missing** | Four motion rows: the palette card's EXIT half (∓8 DIP, opacity → 0, same 250 ms), the design segmented's own `PillTransition` (visible precisely because that control does not suppress its pill), and the compact shortcut-band row's hover fill. |
| 25 | §6.1 | **missing** | The page has **no keyboard accelerators at all** — no Ctrl+Z, no Esc-to-leave, no focus-the-search key. Recorded as a gap with a 0.3 direction; parity item 88. Also added the engine `Reorderable` keyboard lift (Space · arrows · Space · Esc) and its four announcement keys, and the Enter/Esc/blur contract shared by every TextBox here. |
| 26 | §6.5 (new) | **missing** | The two context menus this chapter's claims depend on but never listed: the quick layout menu (7 rows — the ONE route into this page, and where the silent Curated force-switch happens) and the canvas card menu (7 rows, Move up/Move down/—/Hide\|Show/Duplicate/—/Remove), plus the Shortcuts sentinel's deliberate absence of all three affordances. |
| 27 | §6.2 | **missing** | `SectionsReorderable` has **two** terms — `!ShowContents && ExpandedSection is null` (`SidebarEditPlan.cs:100-101`) — so expanding any single card disarms section drag even with the switch off. |
| 28 | §7.1 | **missing** | The Destinations group is **11 rows in a normal session, 12 in developer mode**; the row sets of both PinnableRoutes (9) and ExtraDestinationRoutes (3) are now named explicitly, with the gate's file:line. |
| 29 | §8 | **missing** | `DeveloperMode.ShowsRoute` added as a pure rule to port: it is the single predicate the palette's Destinations filter AND the item picker's Navigation tab both ride, which is what stops the two offers disagreeing about the API console. |
| 30 | §3, §5, §7.3 | verified | Every other number was checked against source and is CORRECT: `Spacing` XXS/XS/S/M/L/XL/XXL = 2/4/8/12/16/20/24 and `Radii.Control`/`Card` = 4/8 (so every padding triplet in §3 resolves); `ControlSize.Small` = pad 7/2/7/3, min-h 24, 12f, icon 14; `Expressive.Fast` 250 + `DistBase` 8 + `Easing.SmoothOut` for both palette recipes; `Motion.ControlFaster` 83; `Drag.ClickPrimaryThresholdMultiplier` 2; the pill 24×3 r1.5; every geometry constant (44 / 32-40-44-44-48 / 20-32-40 / 8-4-8 / 12-16 / 3-6-9 / 12-4-12 / 28-8-2 / 16-32-24 / 56→72 / 26-28-4 / 56-72-88 / 48-56); the drop bands 0.30·h clamped 10-16 with 4-DIP hysteresis and the 2/1/6 caret; every cap (40 / 8 / 4 / 20 / 5 000 / 20 000 / 3 / 200 / 2 000 / 40 / 30 min / 20 / 2 MiB / 64 KiB / 40 / 500 / 60 / 6 / 500 / 50); every source default (queue 5, new releases 4, concerts 3 + 100 km, artist top tracks 5, library's 7 fields, recents 6); the 30-name icon whitelist verbatim and in order; `WaveeType.Eyebrow` 12/16/600 + tracking 30; `WaveeAccent.Decor`; the three `WaveeColors.Selected*` derivations; all 13 + 5 reject arms; and all 37 test files named in §8 with their stated line counts. |
| 31 | §2 W2 | unverified | The claim that the column "hugs the content host's left edge" at every width above 720 follows from `Body()` setting only `MaxWidth`, but the enclosing `ScrollView`'s content alignment was not read. Parity items 1-2 already measure it; treat them as the authority rather than the prose. |
| 32 | §9.4 | unverified | The 0.2.9 line totals in the header and §9.4 were re-counted and match exactly (Curated 4 247, Persistence 1 491, Data 7 161 incl. Sources 850, miniature 343, edit session 164, menu 153, icons 74). The 0.3 *estimates* in §9.3-9.4 remain the author's judgement and were not re-derived. |
| 33 | header, §1.1, §3, §7, §8, §9.3 | **missing** | critic-fix: ~1,200 lines of the action/extension platform were named by NO chapter — `Actions/Extensibility/{BuiltInExtensionTable.cs 303, WaveeActionTargeting.cs 251, WaveeRegistryTable.cs 173, WaveeExtensionRegistry.cs 148, IWaveeExtension.cs 39, PinRowRule.cs 32}` + `Actions/WaveeActionDescriptor.cs 227` + `Actions/ActionIcons.cs 87`. 25/26/27 between them mentioned only `WaveeExtensionRegistry` and `WaveeActionDescriptor`, as bare names. Verified against 0.2.9 and added: the header's source line and totals (13,633 → 14,893 in scope; ≈17,000 → ≈18,300 overall), a full §1.1 anatomy block (registry → table → descriptor → target matrix with file:line), §3's borrowed `ActionIcons.Resolve` table, §7's expanded DATA GAPS rows, §8's four pure-rule rows, and §9.3.9 on ownership. |
| 34 | §1.1 (new) | **missing** | critic-fix: the **7-member reason vocabulary** (`WaveeActionUnavailable` → loc key → en-US sentence → who raises it). This is what a disabled bound row, the picker's `ReasonRow`, the property panel's "why inert" line and a menu accelerator all say, and the chapter had recorded none of it (`WaveeActionTargeting.cs:39-58,140-158`; `assets/loc/en-US.json:1888-1897`). Noted that `HostUnavailable` and `NotApplicable` deliberately share a sentence and must stay separate members — only the first is the confirmation-surface refusal. |
| 35 | §1.1 (new) | **missing** | critic-fix: a full table of the **13 first-party descriptors** — key, en-US label, `IconKey` → glyph fallback, accepted-target set (which *is* the row's subtitle) and the per-row reasons, from `BuiltInExtensionTable.cs:61-273`. Three of the four rows the old W14 drew were fabricated; see line 36. |
| 36 | §2/W14 | **wrong** | critic-fix: W14's three drawn rows were invented. Row 1's subtitle read "Nothing (a global action) · A chosen item" — `wavee.play` accepts `FixedEntity\|FixedTrack`, and **no first-party descriptor accepts `None` at all**, so that label never renders. Row 2 was titled "Add to queue"; the label is `Strings.Detail.PlayAfter` = **"Play after"** (`BuiltInExtensionTable.cs:95`), and its subtitle is "A chosen track" alone. Row 3 was "Save to library / A chosen item"; the real pair is "Save to Liked Songs" (`A chosen track · Now Playing`) and "Save to Your Library" (`A chosen item`). The box is redrawn with the real 13 rows in registration order. |
| 37 | §2/W14, §3 | **wrong** | critic-fix: W14's `ReasonRow` line claimed "⚠ Sign in to use this action." in "12f warn" ink. There is no such sentence — the row can only say one of the 7 — and BOTH the 12f `Icons.StatusWarning` and the 11f text are `Tok.TextTertiary`, not a warning colour (`SidebarItemPickers.cs:537-547`; §3's own token row was already correct, so the two contradicted each other). |
| 38 | §2/W14 | **missing** | critic-fix: two more picker states and a "what it does NOT offer" block — the `RequiresConfirmation` arm (resolves `HostUnavailable` with no overlay rather than committing a binding that would later run unconfirmed) and the `Destructive` arm, **both entirely unexercised in 0.2.9** because no first-party descriptor sets either flag. Recorded with §0.18 and §9.1 so a re-author does not delete them as dead code. |
| 39 | §2/W22 (new) | **missing** | critic-fix: the chapter drew the picker but never what the picker PRODUCES. W22 adds the bound row's four states in the pane (available · unavailable-with-tooltip · no binding · no registry yet, `SidebarPaneSlot.cs:703-762`) and the same binding as the property panel draws it (`SidebarPropertyPanel.cs:775-981`), including the `ToolTip.Wrap(..., grow: 1f)` width trap and the raw-`ActionKey` title fallback. |
| 40 | §1.1, §3, §2/W22 | **missing** | critic-fix: **three hosts, one binding, three different marks.** The picker takes `IconRef.Glyph` + `.Font` only and never the `ThemedName` arm (14f on a 28-DIP plate, `SidebarItemPickers.cs:461-465`); the pane resolves override → `ThemedIcon` if registered → glyph+font → `MusicNote` at 16f (`Pane/SidebarPaneText.cs:226-236`); the property panel ignores the descriptor entirely and draws `Icons.RefineSparkle` at 13f (`SidebarPropertyPanel.cs:951-961`). Also: `a.Icon()` in the picker takes the default `isChecked: false`, so a toggle row is always the unchecked variant. None of this was recorded. |
| 41 | §0 (16-18, new) | **missing** | critic-fix: three non-negotiables the platform's own headers assert and the chapter did not — (16) a bound row is visible-but-disabled with a sentence, never absent; (17) the registry is append-only, first-wins and NEVER unregisters, which is what makes 16 possible; (18) `BuiltInExtensionTable`'s exclusion list (AddToPlaylist, ViewCredits, Rename/Delete, the Video verbs, …) is a design decision recorded in exactly one comment and guarded by no test. |
| 42 | §6.6 (new) | **missing** | critic-fix: the platform's interaction contract — what a live click runs (`Execute` re-resolves with `peek: true`), that a disabled row's click does nothing and says nothing beyond its tooltip, the confirmation route and its no-overlay refusal, and the registration outcomes. Plus the threading assertion (register-then-read, UI thread, no lock) and the warning that a 0.3 "disable an extension" feature must filter at the consumption site rather than unregister. |
| 43 | §6.6, §7.2, §7 DATA GAPS | **missing** | critic-fix: `WaveeExtensionRegistry.Diagnostics` is built on every refusal and **read by nothing** — a repo-wide search for a UI consumer finds none. A contribution that silently failed to register is invisible, which is the exact failure the list exists to prevent. Recorded as a 0.3 addition (one read-only list on the diagnostics page, never a toast), not a parity item. |
| 44 | §8 | **missing** | critic-fix: four engine-free classes added as pure rules to port — `WaveeExtensionKey`, `WaveeRegistryTable<T>`, `WaveeActionTargets` (+ its four supporting types) and `PinRowRule` — with their real test ranges and counts, re-counted from `Wavee.Tests/WaveeExtensionRegistryTests.cs` (443 lines, 40 `[Fact]`/`[Theory]` declarations: 8 + 6 + 16 + 7 across the four regions). All four are source-included by `Wavee.Tests.csproj:322-324`. |
| 45 | §8 | **missing** | critic-fix: `BuiltInExtensionTable.cs` is the one file in this chapter's scope with **no test at all** — it is engine-bound and is NOT source-included, so nothing asserts a label, an icon key, an accepted-mode set or the exclusions. Flagged as the highest-risk file in the port, with the 0.3 direction (the descriptor's data half is pure and testable; only `Run` is not). |
| 46 | §9.1, §9.3.9, header | **missing** | critic-fix: §5 of the plan leaves `Actions/Extensibility/**` between Wave 4 owners I ("WaveeCommands + Actions/* → one table") and J ("the sidebar platform"), and BOTH waves consume it. Added §9.3 item 9 recommending **I**, with two sequencing conditions (I ships the table before J's picker; the descriptor shape is frozen by §7's gaps row) and `PinRowRule` going to `Sidebar.cs` instead. The header line now says so too. The plan file itself is out of this chapter's edit scope — §5 still needs the one-line assignment. |
| 47 | §10 | **missing** | critic-fix: parity items 90-99 — the picker's 13 rows and their order, the "Play after" label, the subtitle-is-the-target-set rule, the unchecked toggle row, the four reason surfaces saying one sentence, the never-vanishing bound row, the equal-width disabled row, the three-host glyph divergence, the first-wins test assertion, and the diagnostics gap. |
| 48 | §1.1, §2/W3, §11 | verified | The registration ORDER claim W3 already made is confirmed at its source: `WaveeShell.cs:576-578` builds the registry (actions first, via `BuiltInExtensionTable.RegisterAll`) and only then registers the sidebar sources, and `WaveeRegistryTable.Items` preserves insertion order (`:121-124`). So the picker's 13 actions and W3's 9 contributions are each in their own registration order, and `Diagnostics` concatenates actions-then-sources (`WaveeExtensionRegistry.cs:76-87`). Fixed in passing: audit row 9 carried a bare 48-pipe-56, whose unescaped pipe split that row into six cells and broke this table's rendering. |

**arbitration 2026-09-12:** one sidebar file set and one platform total (A4). Chs. 25 and 26 gave different file
names, different per-file numbers and a ~5,000-line gap; the settled answer is **this chapter's total and file set
plus ch. 25's `Sidebar.Doc.cs`** — `Shell/Sidebar.cs` (CORE) · `Shell/Sidebar.Doc.cs` (document + reducer + wire) ·
`Shell/Sidebar.UI.cs` · `Shell/Sidebar.Customizer.UI.cs` · `Shell/Sidebar.Host.cs`, **owner J, Wave 4, the
customizer sequenced LAST**. Rewritten to it: the header target line, §1's file legend, §8's four persistence /
reducer destinations (`SidebarLayoutWire`, `SidebarLayoutMigrations`, `SidebarLayoutDefaults` and the
`Wavee.Core/Sidebar/*` reducer set now say `Sidebar.Doc.cs`, not `Sidebar.cs`), §9.3.1, §9.3.2, §9.3.7, §9.3.8 and
§9.4. The band **23,000–25,000** is unchanged and is now itemised **5,000 / 4,000 / 7,500 / 4,500 / 2,500 =
23,500**: the only movement from §9.3.2's original list is the document + reducer + wire leaving `Sidebar.cs`
(9,000 − 4,000 = 5,000) for `Sidebar.Doc.cs`, whose ~4,000 is grounded in `Persistence/**` 1,491 +
`Wavee.Core/Sidebar/**` 2,526 = 4,017 (both re-counted against 0.2.9 for this pass). §9.4 gains a row mapping this
chapter's ~10,500 onto the set as **~12,900** of the 23,500 — including the `Sidebar.Doc.cs` lines it shares with
ch. 25 and counts here — against ch. 25's **~10,600**, stated identically there, so no line is counted twice.
§9.3.9's recommendation on `Actions/Extensibility/**` (owner I, `PinRowRule` to `Sidebar.cs`) is untouched by this
arbitration. No wireframe, token, motion or 0.2.9 citation changed.

**answers 2026-09-12: Q7 (plan §9.6) reaches this chapter once.** The customizer palette's destination row count moves from 12 (0.2.9: 11 normal + the developer-mode `api-console` row) to **11 always** in 0.3 — `ExtraDestinationRoutes` becomes `settings` + `concerts`, and the developer-mode filter arm has nothing left to gate. Struck in the §7.1 table with the reason and date, not removed. No wireframe, token or motion row changed.
