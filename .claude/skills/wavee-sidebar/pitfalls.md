# Wavee sidebar — pitfalls and known issues

Every entry below was re-verified against the code after the unification landed. Where the build's own working
notes were wrong, the correction is called out — **trust this file over the handoff notes**.

---

## Localization

### `sidebar.pin.pinTo`, not `sidebar.pin.pin`

The generated table nests a dotted key into static classes, PascalCasing each segment
(`src/FluentGpu.SourceGen/Localization/LocalizationKeysGenerator.cs` → `Identifiers.ToPascal`). A leaf whose
PascalCase name equals its **enclosing group's class name** would emit `public const string Pin` inside
`static class Pin` — a C# error (a member may not have the same name as its enclosing type). The key was therefore
renamed: the live key is `sidebar.pin.pinTo` and the member is `Strings.Sidebar.Pin.PinTo`
(`Actions/PinActions.cs:28`, `:75`; `Actions/Extensibility/BuiltInExtensionTable.cs:247`). **Before naming a key,
check it does not collide with its own group.** There is no comment in the tree recording this — the evidence is
the key spelling itself.

### `_Self` is the generator's *prefix*-collision escape, and the customizer does not use it

When a dotted key is also a **prefix** of another key (`sidebar.customizer.undo` next to
`sidebar.customizer.undo.addSection`), the intermediate node must be both a class and a const, so the generator
emits the const as `_Self` (`LocalizationKeysGenerator.cs:199-207`). That rule genuinely fires for
`sidebar.customizer.undo`.

But `Strings.Sidebar.Customizer.Undo._Self` is **not** what the code uses, and neither is
`Strings.Sidebar.Customizer.Undo.AddSection` — there are zero `Strings.Sidebar.Customizer.*` references in
`src/apps`. The undo *button* label is a hand-written literal const, `CzLoc.Undo = "sidebar.customizer.undo"`
(`Curated/SidebarCustomizerPage.cs:841`), and the *command* labels are literal consts in
`SidebarUndoLabels` (`Wavee.Core/Sidebar/SidebarLayoutCommands.cs:15-37`) because Wavee.Core cannot see the
generated table. That is deliberate and documented at `SidebarCustomizerPage.cs:817-820`. Follow the landed
pattern; do not "fix" it to the generated members.

### Loc file mechanics

- Three files, and only three: `src/apps/Wavee/assets/loc/{en-US.json, nl.json, ko-KR.json}`.
- `nl` and `ko-KR` are **partial overrides** — 591 keys each against en-US's 1953 (recount 2026-08-26) — resolved by the engine's
  per-key fallback chain (active → parent → default → the key itself, `FluentGpu.Engine/Localization/Localization.cs:212-224`).
  A key missing from `nl`/`ko` is legal; a key missing from **en-US** renders visibly as `[key]`.
- The files are **CRLF, UTF-8 without BOM**. Keep it that way — an editor that "helpfully" adds a BOM or converts
  to LF produces a diff nobody wants to review.
- The shape is nested objects, but **literal dotted keys inside a nested object are also legal** (e.g.
  `"undo.addSection"` sits directly inside the `customizer` object). The generator flattens, then splits on `.`,
  so both spellings produce the same member path.
- Old keys survive in stale `bin/` output. Grep `src/apps/Wavee/assets/loc/`, not the whole tree, when deciding
  whether a key still exists.

---

## Components and reactivity

### Props freeze at mount — that is why the config is all delegates

`SidebarPaneConfig` is built once in `UseMemo(…, DepKey.Empty)` and frozen into the pane's ctor. A **value**
member would pin frame 1's state forever. `Document`, `Input`, `ModeEpoch`, `Head`, `RailFooter` are providers the
pane invokes inside *its* render — which is also the only reason the signals they read subscribe the pane. The mode
components refresh their `_prefs`/`_lib` fields every render and the frozen delegates read those fields, so the
config always sees live services (service instances are reference-stable).

The same trap bites the customizer's controls: a frozen `ComboBox`-style control cannot be told a new value, so
where one is unavoidable the landed workaround is a **remount key** derived from the value. Prefer the controlled
pattern below.

### Controlled controls derive from the document each render **and** fold `RejectEpoch`

Every property-panel control is *controlled against the document*: the row reads
`prefs.LayoutVersion.Value` **and** `page.RejectEpoch.Value` (`CzRow.Subject`,
`Curated/SidebarCustomizerControls.cs:221-240`), then mirrors the document into its own signal from a
**layout effect** keyed on `CzRow.Epoch(page)` — never from render.

`RejectEpoch` exists because a **rejected** command does not bump `LayoutVersion`. Without the fold, the row never
re-rendered after a rejection, its mirror effect never re-ran, and the control kept showing the value the user
picked while the document still held the old one. With the fold, the control **snaps back** to the truth. (The
build notes described this as "avoiding a snap-back"; the code's intent is the opposite — the snap-back is the
correct behaviour and the fold is what makes it happen. `SidebarCustomizerPage.cs:560-570` bumps it, `:584-589`
clears it.)

### Never write a signal from `Render`

The plan is published to the bound slots as a **plain field** (`SidebarPane.Plan`), not a signal. The only signal
writes in the pane are: `_rowCount` from a `UseLayoutEffect`; a single `_countSeeded` write *before the list
exists* (provably not a backwards write — nothing has read it yet); and `_dispVersion` from `Choreograph`, which
runs inside the plan memo and is read only by the `ItemsView` child that renders after it.

`Entries.Version` (`SidebarPreferences.cs`, `SidebarEntries.Publish`) now folds TWO gates, not one: the published,
collapsed-folder-filtered rows, AND the binder's full flattened projection (`SidebarProjectionBinder`'s `_all`). A
hydration that only touches a playlist inside a collapsed folder never shows up in the published rows, but the
planner reads the full projection — without the second gate the pane would keep drawing that row's stale content.

### Bind wiring is MOUNT-ONLY — a per-row bound thunk must read `_scope.Index.Value`, never a captured index

The reconciler registers a node's bound `Prop<T>` thunks when the node **mounts** and never again: `Update` rewrites
props, not bindings. In a recycling `ItemsView` that is a trap with teeth, because a slot's children are paired by
`(Key, type)` — a child with a CONSTANT key (`"drop-line"`, `"drop-plate"`) is **Updated** across a recycle while the
entity row beside it, keyed by the row's own key, genuinely remounts.

So a thunk that captured its plan `index` (or the row `height`, or `enabled && selected`) keeps answering for the row
the slot was FIRST mounted with. Two shipped defects, one mechanism:

- **Two insertion lines after an auto-scrolled drag**, and no line reachable for "before the first row" — the armed
  row's caret plus a recycled slot's ghost, and the slot that had inherited index 0's binding.
- **A stale route plate.** `SidebarEntityRow.Create` bound `Fill`/`BorderColor` off `SidebarRowSpec.DropCue` folded
  with `enabled && selected`; `RefreshSelection` bumps the two flipped rows' epochs → same `Key` → Update → the new
  thunk is ignored, so the resting plate stayed on the OLD route until the row scrolled out.

**The rule.** Every per-row bound thunk opens with `int i = _scope.Index.Value; _ = _o.SubscribeRowEpoch(i);` and asks
the pane from `i` (the `PillState` discipline). Reading `Index` subscribes the binding to the recycle write; reading
the row epoch covers a same-index re-plan. Anything that is not a live read — a height, a colour that folds selection —
goes back to a **static** value the reconciler re-asserts every render. `DropCue` is deleted; the `Into` plate is the
slot's own always-mounted `DropPlate()` under the row. Rule: every `Prop.Of(` in
`InsertionLine`/`DropPlate` reads `_scope.Index.Value`, and the entity row keeps its fills static and owns no tree
drop cue (the source-scan tests that used to pin this were removed on 2026-08-22; tests never read source).

### V3's search-open flag is session-only — it is not a `SidebarKeys` setting

`LibraryV3Session.SearchOpen : Signal<bool>` is ephemeral, created fresh per mode mount. It is **not**
`prefs.V3SearchOpen`, `AppSettings.SidebarKeys`, or anything else `IAppSettings` persists — that persisted flag
existed once (`SidebarKeys.V3SearchOpen`) and was deleted specifically because it made a relaunch reopen the search
field with a stale, empty caret. The query TEXT (`prefs.V3Search`) is likewise session-only — it lives on
`SidebarPreferences` as a plain `Signal<string>`, never written to `IAppSettings` or the JSON layout document, and
is cleared on `CloseSearch()`/`ClearAllFilters()`. Do not reach for `AppSettings.cs`/`SidebarKeys` when wiring a new
V3 chrome toggle that should reset on relaunch — a `LibraryV3Session` field is the pattern.

### `SidebarSort.Recents` reads PLAYS, not visits — `HistoryStore` feeds a different feed entirely

`SidebarSort.Recents` sorts on `SidebarLibraryEntry.LastPlayedMs`, stamped from `PlayLogStore.Recency` (local plays
+ server history) by `SidebarProjection.Build`'s `lastPlayed` parameter. `SidebarRecency`/`LastVisitedTicksUtc` —
built from `HistoryStore`, the NAVIGATION log — feeds only the "recently opened" FEED
(`SidebarSourceMap.Visited`, a JumpBackIn-style shelf). Clicking a row to open it therefore no longer reorders the
sidebar's "Recents" sort; only playing something does. Do not relabel the visited feed "recently played", and do
not sort a list by `LastVisitedTicksUtc` expecting played semantics — the two recency facts have deliberately
separate inputs and separate consumers (`SidebarRecency.cs`'s own file-header comment states this in full).

### A bound row is a frozen child — `SubscribeRowEpoch(index)` is load-bearing

Re-planning in `SidebarPane` does **not** re-render a realized slot. Each realized slot reads
`_o.SubscribeRowEpoch(index)` in its render prologue, which subscribes it to ONE row's epoch. `SidebarPane.PublishStage`
bumps `_planVersion` and the per-row epochs (`BumpChangedRowEpochs` for a diffed publish, `BumpAllRowEpochs` for a
wholesale one), and the pane's own plan re-runs off `PlanDep` (`LayoutVersion`, `Entries.Version`, `PinsVersion`,
`FolderVersion`, `ModeEpoch`, the edit fold, search). Delete the row-epoch read and realized rows keep drawing the
previous plan's content after a library refresh, a customizer edit, a section toggle or a keystroke.

---

## Struct defaults and polarity

`SidebarSourceState` is ordered so `default` is `Ready` (`Data/SidebarRowPlanner.cs:67-69`) — a
`default(SidebarProjectionInput)` must plan real (empty) content, not a screenful of skeletons. The consequence:
a mount with **no binder at all** (a probe / headless mount) would honestly claim "library loaded, and it's empty".
`SidebarPane.Input()` compensates explicitly by forcing `LibraryState`/`TreeState`/`RecentsState`/
`NewReleasesState`/`ConcertsState` to `Pending` when `Prefs?.Binder is null` (`Pane/SidebarPane.cs:384-399`).

The same discipline once forced an **inverted flag name**: `SidebarProjectionInput.SuppressTreeCreateRow` rather than
a positive `TreeCreateRow`, because a positional default of `true` is silently lost on `default(T)`. Both that flag and
the `CreateAction` row it suppressed are now **deleted** (the create affordance is the section header's "+", chrome the
renderer owns per `SidebarPaneConfig.HeaderCreate`) — but the rule that produced the name still binds: when you add a
bool to a POD input, make **false the landed behaviour**.

---

## C# / engine call-shape traps

| Trap | Detail |
|---|---|
| never name a local `from` before a `with` | `from … with` trips the query-expression parser. `SidebarLayoutReducer.DoMoveItem` uses `src`/`to` instead (`Wavee.Core/Sidebar/SidebarLayoutReducer.cs:320`, with the comment). |
| `explicit in` needs an lvalue | `SidebarPaneReorderCommit.Default(prefs, in ctx)` and `descriptor.Execute(services, in binding)` require a local — you cannot pass a `new …(…)` expression by `in`. The descriptor copies its `in` parameter to a local `var b = binding;` before capturing it in a lambda. |
| `TextEl.Weight` is `ushort` | `FluentGpu.Engine/Dsl/Element.cs:509`. A literal (`Weight = 600`) is fine; a **ternary** needs a cast: `Weight = (ushort)(on ? 600 : 400)` — six sidebar call sites do exactly that. |
| `BoxEl.Direction` is `byte` | `Element.cs:81` (0 = row, 1 = column). Ternary ⇒ `Direction = (byte)(vertical ? 1 : 0)` (`Curated/SidebarCustomizerControls.cs:204`). |
| options records are **nested** | `TextBox.TextBoxOptions` (`FluentGpu.Controls/TextBox.cs:27`) and `NumberBox.NumberBoxOptions` (`NumberBox.cs:139`). There is no top-level `TextBoxOptions`. |
| `ContentDialog.PrimaryText`: `null` shows a stray OK | `null` = the localized default label (shown); `""` = **explicitly hidden** (`FluentGpu.Controls/ContentDialog.cs:38-40`, `:205`). A dismiss-only dialog must set `PrimaryText = ""` — `Curated/SidebarItemPickers.cs:43` and `:81` do. |
| `Segmented` paints a plate **and** a pill | Two indicators for one value. Suppress the pill through the control's public `Segmented.PartSelectionPill` seam — the landed `SegmentedNoPill` template at `Curated/SidebarCustomizerControls.cs:287-295` styles it to `Transparent` + `Width = 0` (no engine edit, and the 3-DIP slot stays put so suppressing it costs no relayout). `SelectorBar` is **banned** in the property panel. |
| `IconRef.Font` must be forwarded, or you get tofu | An `IconRef` may name the app-local `WaveeIcons` face (`wavee.playNext` U+E900, `wavee.addToQueue` U+E901). Reading only `.Glyph` resolves those codepoints against Segoe Fluent and renders □. Pass the family through: `Icon(icon.Glyph ?? Icons.More, 14f, …, icon.Font)` (`Curated/SidebarItemPickers.cs:453-463`; also `Pane/SidebarPaneText.cs:207`). |
| artwork needs an explicit `decodePx` | Without the hint an image decodes at its **layout** size with no DPI multiply — visibly blurry on any >1× display. `Shared/SidebarCover.cs:81-85` is the **single** owner of the bucketed ladder (`size <= 32 → 64`, `<= 64 → 128`, else `256`), which also makes the 36-DIP rail tile and the 32-DIP row share one cache entry. `SidebarPaneRail.cs` has no `decodePx` of its own — it delegates to `SidebarCover.Art`. Do not add a second ladder. |
| a private-use glyph codepoint MUST be a `\uXXXX` escape, never a literal character | `Modes/LibraryV3/LibraryV3Header.cs`'s header glyph is meant to be U+E71C (Segoe MDL2 "Filter"/library mark), and its own comment already says so ("as an ESCAPE, never a literal ... was silently dropped once already"). **As of this audit the literal in the file is still corrupted**: the `Icon(` call's glyph argument is the three-character sequence `i-circumflex` + `oe` + `oe` (U+00EE, U+0153, U+0153 — verified byte-for-byte), which is U+E71C's UTF-8 bytes (`EE 9C 9C`) mis-decoded as Windows-1252 — exactly the failure the comment warns about, and apparently not yet fixed. A private-use character is invisible in most editors/terminals, so a diff or grep pass can carry a mangled one through unnoticed; the durable fix is the literal escape text `\uE71C`, never a pasted glyph, with the font named explicitly (`Icon`'s `family` param — reading only the codepoint against the wrong face is the OTHER way this renders as tofu). This is a `.cs` file, so a docs-only pass cannot correct it — flag it for a code fix. |

---

## Geometry and virtualization

### One uniform row height per section

`SidebarPaneMetrics.RowHeight(section)` depends on the section's density + **subtitle intent**, never on whether a
given row happens to carry a subtitle. Two consumers demand it: the `RepeatLayout.VariableList` extent, and
`Reorderable`'s slot pitch — `Reorderable.SlotFromPosition` applies a midpoint rule over
`ItemExtent + Spacing` in content space (`FluentGpu.Controls/Reorderable.cs:438-457`). A mixed 40/44 band silently
breaks both.

Nuance: `Reorderable` *does* support variable extents via `ExtentOf` — and since
the C3 landing the WHOLE geometry family reads the sampled prefix sums, cross-list insertion included
(`ReorderList.SlotAtOffset`/`BoundaryOffset`; gates `e5dragdrop.reorder.varextent[.samelist]`). An earlier revision
of this file claimed the cross-list math still assumed a uniform pitch — it does not. The pane's bands stay
uniform-per-section because the VIRTUALIZING host's extent and the section rhythm want it, not because `Reorderable`
requires it. What *is* still true: the pane sets `ShowInsertionLine = false` because it never wraps a
`Reorderable.List(...)` at all (each band is a run inside ONE virtualized plan list), and the built-in line is
positioned inside that wrapper relative to item 0.

### `Reorderable.Item` does not stretch its child — the wrapped row must fill the slot itself

`Reorderable.Item` wraps content in a `BoxEl` that leaves `Direction` at its default **0 = ROW**, so the row sits on
that wrapper's MAIN axis and — with no `Grow` — arranges at its own **measured content width**. Unwrapped rows fill
(the bound slot's `Embed.Comp` anchor is a plain scene node, whose `LayoutInput.Default` is a COLUMN with
`AlignItems = Stretch`), so only the REORDERABLE bands are affected: their hover/selected fill plate rendered at the
title's width, which reads as "hover and selected are different widths" even though `Fill`/`HoverFill`/`PressedFill`
all sit on the SAME `BoxEl` (`Shared/SidebarEntityRow.cs:301-341`) and cannot differ by state.

Fix it **app-side, per call site** — never in the engine (iron rule 10; the wrapper is shared with `TabView`). The
pattern is `content with { Grow = 1f, Shrink = 1f, MinWidth = 0f }` (`MinWidth = 0` keeps a long title ELIDING instead
of pushing the row past the pane). `Pane/SidebarPaneSlot.cs` is now the ONLY owner of the pattern and carries it at
**both** of its wrap sites — the item band and the edit-mode section-CARD band. (Historically the fix existed twice
more, in the customizer outline's "FILL THE COLUMN (round-3 defect 1)" and as an explicit `Width` on the top-bar strip;
Phase 3 deleted both files. If you go looking for those precedents, that is why they are gone.) Pinned by
`SidebarPaneInvariantTests.ReorderBandRows_FillTheSlot`, which asserts both sites.

### A zero-size flex child still collects the row's `Gap`

Setting `Width = 0f`/`Height = 0f` on a flex child hides its paint, but it is still a flex PARTICIPANT — the row's
own `Gap` still lands after it, so a hidden sibling pushes every visible child one gap to the right. Two shipped
instances, one mechanism:

- **The multi-select check lane**, when it rode as a flex SIBLING of the row's leading cluster inside the gapped
  row: `Flow.Show`'s hidden state still counted, so the row's 10-DIP `Gap` after it pushed every rootlist row (the
  only rows that carry the lane) one gap right of the album/artist rows beside them. Fixed by folding the lane
  INSIDE the leading cluster, in its own gapless wrapper box (`Shared/SidebarEntityRow.Create`'s
  `spec.CheckLane` handling).
- **The chip rail's a11y group label** (`Modes/LibraryV3/LibraryV3Chips.GroupLabel`), when it rode as a flex sibling
  of the chips inside the rail's gapped row: even at `Width = 0f, Height = 0f` it still collected the rail's 6-DIP
  `Gap`, pushing the first chip 6 DIP off the rows' art column. Fixed by mounting the label OUTSIDE the gapped chip
  row entirely (`LibraryV3Chips.Rail`'s two-child `BoxEl` — the label, then a separate gapped box holding only the
  chips).

**The rule.** A node that must add nothing to a gapped row's layout — a bound-but-hidden lane, a zero-size a11y
label — goes either inside a gapless wrapper folded into one visible sibling, or entirely outside the gapped
container. Zero size on the node itself is not enough.

### `SidebarProjectionInput` ALIASES the binder's buffers

`Prefs.Binder.CurrentInput`'s lists point at the binder's reusable rebuild buffers, so a plan built from it is
valid exactly until the next rebuild — which is the `UseMemo` lifetime it is built for. Two consequences:

- **Never** cache a plan or an entry list across rebuilds.
- The expanded pane and the 56-DIP rail must own **separate `SidebarPlanBuffers`** (`_paneBuffers`,
  `_railBuffers`), because a plan aliases its buffers too. Sharing one would have the rail overwrite the pane's plan.

### A flat sort destroys folder adjacency

`SidebarSort.Apply` sorts the whole list by one comparator, so a nested playlist can land **above** the folder that
contains it. V3 re-groups afterwards: `LibraryV3View.Build(published, skip, tree, treeRevision, drillFolderId,
group)` (`Modes/LibraryV3/LibraryV3View.cs:76-118` + `EnsureParentMap`/`BuildBuckets`/`EmitLevel`) orders folders
among their siblings by the active sort and each folder's children by the same sort *within* the folder, rewriting
`Depth`/`SourceOrder`. Its tree input is the binder's — `input.PlaylistTree`, i.e. `Binder.CurrentInput.PlaylistTree`,
threaded through `SidebarPane.Input()` (`Modes/LibraryV3Sidebar.cs:211`) — and the parent map is memoised on
`Binder.Revision`. **Any new tree consumer needs this pass**; it is the precedent to copy, not to re-derive.

---

## Persistence

- **Persisted enums are append-only.** `SidebarSectionKind`, `SidebarItemTarget`, `SidebarActionTargetMode`,
  `SidebarDensity`, `SidebarPresentation`, `SidebarSortMode`, `SidebarPlaylistQualifier`, `SidebarRecentsSource`,
  `SidebarEmptyBehavior`, `SidebarDisplayField`, `SidebarRejectReason`, `SidebarEntityKind`. Never renumber, never
  reuse a value, and never rename a **wire string** in `SidebarLayoutWire`.
- **Unknown-kind round-trip policy:** an unrecognized `kind` string is preserved as an opaque section blob at its
  original index and re-emitted on the next save; it renders as nothing. Unknown *members* anywhere in the tree
  ride `[JsonExtensionData]` and are re-attached on write, matched by owning id. This only works if
  `SidebarPreferences` keeps threading `SidebarWireCarry` (`ReadCurated` on load, `WriteCurated(_layout, _carry)`
  on every snapshot). An unknown kind nested inside a `CustomGroup` is hoisted to a top-level opaque blob.
- A **missing** `version` is treated as *malformed*, not as v1.
- Over-budget is a **fault**, never a truncation. `SaveFault` does not latch — `Commit()` no-ops while over
  budget, so in-memory state legitimately runs ahead of disk.
- Canonical-JSON equality (`SidebarJson.Canonical`) is **property-order sensitive** by design: a reorder is a real
  change. `GetRawText()` is deliberately not used — it returns the original source span, so a config read back out
  of the indented document would never compare equal to the one that wrote it and every load would look like an
  edit.

---

## Tests

The tests project **source-includes app files one by one** rather than referencing `Wavee`, and deliberately has no
`FluentGpu.Engine`/`FluentGpu.Controls` project reference. So:

- **Included:** `Features/Sidebar/{Data\*.cs (one level — NOT Data\Sources\), Persistence\*.cs, SidebarDesign.cs,
  SidebarDesignGating.cs, SidebarPaneInvariant.cs, SidebarPinStore.cs}`, exactly four hand-picked files from
  otherwise-excluded folders (`Pane\SidebarBuiltInDocuments.cs`, `Modes\LibraryV3\LibraryV3Document.cs`,
  `Modes\LibraryV3\LibraryV3View.cs`, `Curated\SidebarCustomizerLayout.cs`), `Features/Shell/{ShellNav.cs,
  ShellResponsiveLayout.cs}`, `App/{SidebarBootstrap.cs, PlayLogStore.cs}` (via `Link=`), and four
  `Actions/` files.
- **Deliberately NOT included:** the rest of `Pane/`, `Shared/*`, `Modes/CuratedSidebar.cs`,
  `Modes/LibraryV3Sidebar.cs`, the rest of `Modes/LibraryV3/`, the rest of `Curated/`, `SidebarHost.cs`,
  `SidebarPreferences.cs`, `SidebarProjectionBinder.cs`, `SidebarIcons.cs`, `SidebarLayoutMenu.cs`,
  `SidebarOnboardingChrome.cs`, `SidebarDesignPicker.cs`, `SidebarResize.cs`.

**If you put decision logic in a non-included file, it becomes untestable.** That is the whole reason for the
pure/impure split (`SidebarBinderPipeline` under `SidebarProjectionBinder`, `SidebarPaneState` under
`SidebarPreferences`, `SidebarSourceMap` under the adapters, `SidebarCustomizerLayout` under the customizer). Add a
new include only when you must, and add the comment naming the test class that needs it — that is the file's
convention.

Shims that make the includes compile: `VirtualCollectionSignalShim.cs` (a trivial `Signal<T>`/`IReadSignal<T>`),
`TestAppSettingsShim.cs` (`IAppSettings`/`SettingKey<T>`/`WaveeSettings`/**`SidebarKeys` mirrored verbatim** from
`Platform/AppSettings.cs` + `MemoryAppSettings`), `Actions/ActionsTestShims.cs`, and an inline
`FluentGpu.Controls` `Icons`/`Route` shim region inside `ShellNavDestTests.cs`. Keep the `SidebarKeys` mirror in
sync by hand.

### Headless animation time does not follow `Thread.Sleep`

`AnimClock.Advance` treats a post-wait resume as idle and advances by the 1/60 quantum
(`AnimClock.DefaultDeltaMs = 1000f/60f` ≈ 16.7 ms), clamping a real wall delta to 1…40 ms otherwise
(`FluentGpu.Engine/Animation/AnimClock.cs:28`, `:39-55`). A `Sleep(110)` frame therefore buys ~16.7 ms of
*animation* time, not 110. Headless hosts additionally use `FixedFrameTimeSource` (16 ms/frame,
`Hosting/FrameTimeSource.cs:18-22`, selected at `AppHost.cs:1717`). **Frames, not sleeps, settle a track** — drive
settle loops by frame count.

---

## Accessibility — the real limits

Do not overclaim. Everything the sidebar exposes is `AutomationRole.Button` / `RadioButton` /
`NavigationItem`; `SidebarSectionHeader` uses `AutomationRole.None`. There is **no** tree/list/group semantics.

There **is** a live-region seam: the engine's `InputHooks.Announce` (`FluentGpu.Engine/Hooks/Context.cs`) is a
`(text, assertive)` delegate the Windows backend points at `UiaRaiseNotificationEvent`
(`FluentGpu.Windows/Uia/Win32Uia.cs`), and `FluentGpu.Input.Announcer` is the coalescing front door over it
(`Say` / `SayThrottled`, ~100 ms). What the sidebar does and does not do with it:

- **Section-card reorder IS announced** (the customize canvas). `Pane/SidebarPane.cs` sets
  `Reorderable.AnnounceText = SectionAnnounce` and `AnnounceAssertive = true` on the section band, composing one
  sentence per milestone — grab / move / drop / cancel — from `SidebarPaneLoc.Reorder{Grabbed,Moved,Dropped,Cancelled}`
  plus the position caption `SidebarPaneLoc.ReorderPosition` (= the loc key `sidebar.pin.position`). Composition is the
  app's because only the app can name the section and owns the locale; delivery is coalesced by the engine, so a held
  arrow key speaks once per ~100 ms rather than once per key repeat. That is what makes the keyboard lift (Space /
  arrows / Space, Escape cancels) usable at all — the displacement is otherwise purely visual.
- **The ITEM band is not announced yet.** Only the section-card band wires `AnnounceText`; a pin reorder or a
  Shortcuts-item reorder still has no spoken feedback.
- Section collapse/expand is not announced, and a virtualized pane exposes no set-size/position-in-set.
- `SidebarPreferences.UndoLabel`/`RedoLabel` are described as "for the tooltip + a11y announcement"; nothing
  announces them yet — today they are tooltips.

---

# KNOWN ISSUES

No sidebar issue remains in the previously verified five-item table. The fixes below are source-audited; the user owns
the build/runtime verification pass.

| # | Resolved issue | Current evidence |
|---|---|---|
| 1 | **Expanded pinned folders now emit their visible subtree.** The planner finds the pinned folder in the canonical flattened tree, rebases descendant depth, honors nested disclosure state, and shares the section cap. While descendants make top-level pins non-contiguous, pane reordering is deliberately disabled for that section; collapsing restores it. | `Data/SidebarRowPlanner.cs` (`PlanPinned` / `AppendPinnedFolderChildren`); `Pane/SidebarPane.cs` (`_pinnedSubtrees`); `SidebarRowPlannerTests.ExpandedPinnedFolder_EmitsItsVisibleSubtreeAtRelativeDepth`. |
| 2 | **Library V3 empty-search copy now uses the query that built the plan.** `BuildPane` records the normalized `SidebarProjectionInput.Search` after the mode transform, and `SidebarPaneSlot.LibraryEmptyText` reads that effective query. | `Pane/SidebarPane.cs` (`_effectiveSearch`, `BuildPane`, `SearchText`); `Pane/SidebarPaneSlot.cs` (`LibraryEmptyText`). |
| 3 | **The customizer uses the shell's real Back stack.** `HistoryStore.BackCtx` carries `WaveeShell.Back`; the persisted visit-log walk remains only as a standalone/headless fallback. | `Features/Shell/HistoryPage.cs` (`BackCtx`); `Features/Shell/WaveeShell.cs` (provider); `Curated/SidebarCustomizerPage.cs` (`GoBack`). |
| 4 | **Save and Like have distinct semantic icon keys.** Like keeps the stateful heart; Save uses the add/check pair, and both legacy app actions and bindable descriptors use the matching key. | `Actions/ActionIcons.cs`; `Actions/{TrackActions,ContainerActions}.cs`; `Actions/Extensibility/BuiltInExtensionTable.cs`. |
| 5 | **Bindable save/like activity keeps the target title.** Target host state and resolution now carry best-known fixed, now-playing, and active-route names; the adapters forward `t.Name` to `LibraryBridge.ToggleSaved`. | `Actions/Extensibility/WaveeActionTargeting.cs`; `Actions/WaveeActionDescriptor.cs`; `Actions/Extensibility/BuiltInExtensionTable.cs`; `WaveeExtensionRegistryTests`. |

## Deletion candidates (unmounted / unreferenced)

Verified reference counts across all of `src/apps`, including the tests.

| Candidate | Status |
|---|---|
| the `SidebarSelectionPill` **component** | **NOT a candidate — this row was wrong.** It is MOUNTED: `Pane/SidebarPaneSlot.cs:1131` (`Indicator`) does `ZStack(row, Embed.Comp(() => new SidebarSelectionPill(_o, …)))`, and `SidebarSelectionPill.PillH` is read at `:1130` and at `Pane/SidebarPane.cs:1303`, `:1305`. Deleting the class body breaks the build **and** the selection indicator. |
| `SidebarSectionHeader.Section` | **0** call sites (3 doc-comment mentions). `Rule()` and `RevealWrapper()` are called **only** by `Section`, so all three go together (and `Reveal` with them). `Label`, `Header`, `ExplicitDivider` and `Height` **are** live (`Pane/SidebarPaneSlot.cs`) — do not remove the file. |
| `SidebarLayoutMenu.HeaderButton` | **Deleted** (defect 15). `SidebarLayoutMenu.cs` records why in a comment; do not re-add it. |
| loc `sidebar.pin.showAll`, `sidebar.pin.showLess` | **Retired** — gone from all three locales. |
| loc `sidebar.pin.position` | **KEEP — still live, but through a NEW owner.** The old path (`CzLoc.Position` → the customizer outline) died with the outline; the key is now `SidebarPaneLoc.ReorderPosition` (`Pane/SidebarPaneText.cs`), the position clause inside every section-card reorder ANNOUNCEMENT (`SidebarPane.SectionAnnounce`). Retiring it would silently render `[sidebar.pin.position]` into a screen-reader sentence. |
| loc `sidebar.createPlaylist` | **Retired** — gone from all three locales. Every call site uses the sibling `sidebar.createPlaylistTooltip` (the plain-click "+") or `sidebar.createTooltip` (the "+ with a flyout"). |
| loc `sidebar.createFolder` | Already **gone** from all three source locales (it survives only in stale `bin/` output). |
| loc `sidebar.customizer.{outline, preview, previewExpanded, previewRail, previewDrawer, previewHint, addFirst, startFromTemplate, liftHint, visibleCount, topBar, topBarGlobal, topBarEmpty, curatedLayout, curatedInactive}` | **Retired** — gone from all three locales. They labelled the outline / preview columns and the standalone "Top bar" card, all deleted in Phase 3. `sidebar.customizer.empty`/`emptySub` are **KEPT**: they are live through `SidebarPaneLoc.PaneEmpty`. |
| `Shared/SidebarNavBandModel.cs` | **NOT a deletion candidate any more.** W3 gave it a production caller back: `Modes/LibraryV3/LibraryV3NavBand.cs` calls `RouteKeyOf`/`SelectsRoute` for its hand-placed entity tiles (Library V3's nav band left the document as a section entirely and became fixed chrome — see the `SidebarPaneConfig.RailHead` entry below). `KindOf`/`Shape` still have none. `SidebarNavBandTests` drives the model directly either way. Its own file header still describes a `SidebarNavBand` *component* that no longer exists — that part is accurate. |
| `SidebarPaneConfig.RailHead` | **NOT deleted — re-added (W3), for exactly one mode.** Phase 1 had removed it on the theory that the Shortcuts band would always be an ordinary document section; that stays true for Classic/Curated, which still pass `RailHead = null` and get their rail tiles from `ShowInRail`. Library V3 is the one exception: its nav band left the document (`LibraryV3NavBand.cs` is fixed chrome, not a section), so it has no section for the rail to draw from and supplies `RailHead` instead (`Modes/LibraryV3Sidebar.BuildRailHead`). `Pane/SidebarPaneRail.Build` prepends `Config.RailHead?.Invoke()` ahead of the plan's own tiles (a divider follows), and — because those tiles are no longer part of any plan — `SidebarPane`'s rail memo folds `Prefs.LayoutVersion.Value` into its own `DepKey` whenever `Config.RailHead` is non-null, so a TopBar edit still repaints the rail even though it moves no section. |
| `AppActions.All` | Declared at `Actions/AppAction.cs:96-108` with **zero** code references anywhere — every mention is a doc comment. Dead-but-retained. Do not add the first reference; the registry is the path. |
| loc `player.play` / `player.pause` in `nl`/`ko` | Dead override keys — not present in en-US and referenced nowhere. |
