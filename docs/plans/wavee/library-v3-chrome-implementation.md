# Library V3 chrome sweep — search morph, filter pills, nav band, "Recents" = played

Plan file for `docs/plans/wavee/library-v3-chrome-implementation.md` (the implementer copies this there, per the
"plans with real code" rule). Decisions confirmed with Christos on 2026-09-03: **in-row search morph** (Spotify),
**composite pill kept but rebuilt on `ConcertUi.SegmentedPill`**, **Home band becomes fixed chrome above the header**.
Engine changes are allowed for this work (overrides sidebar iron rule 11); none turned out to be *required* — see §8.

## Context

Library V3 (`SidebarDesign.LibraryV3`) is the Spotify-style "Your Library" sidebar: header · toolbar (search + sort/view)
· filter-chip rail · the ONE virtualized `SidebarPane`. The content pipeline is sound; the **chrome** is what the user
called out, and every complaint traces to a concrete defect (all verified in source this session):

| Complaint | Root cause |
|---|---|
| Search "morph is buggy, slow, doesn't reset" | `LibraryV3Search.cs:83/:98` returns two **differently keyed** elements — there is no morph, only an unmount/mount cross-fade. Escape handling at `:103-117` is **dead code**: `EditableText.HandleKey` marks Escape handled (`EditableText.cs:684`) and `DoCancel` reverts to the *focus-time snapshot* then blurs to nothing (`:839-853`), so "clear" only works on a fresh focus and "close" is unreachable. **No blur handler** ever closes the field. `V3SearchOpen` is **persisted** (`AppSettings.cs:384`, `SidebarPreferences.cs:340-345`) despite the doc claiming session-only — a relaunch reopens the field. The `AutoSuggestBox` editor lane is 32 DIP inside a 28 DIP clipped field (`AutoSuggestBox.cs:524`), and its trailing magnifier is a dead submit button (no ✕ to clear). Every keystroke runs the full binder rebuild with no debounce (`SidebarProjectionBinder.cs:778, :917`). |
| "Sorting not visible" | The sort pill collapses to icon-only whenever the search is open (`LibraryV3Header.cs:170-174`), and the persisted open flag makes that the state after every relaunch. |
| Composite pills "don't go smooth", accent/text issues, chevron instead of ✕ | `LibraryV3Chips.FusedPill` (`:328-360`) hand-rolls `ConcertUi.SegmentedPill` with the exact token combination `ConcertUi.cs:915-919` documents as broken (`FillCardDefault` segment + `AccentTextPrimary` ink = accent-on-accent). On-accent ink is the theme-keyed `Tok.TextOnAccentPrimary` instead of the contrast-picked `Tok.OnAccent` (five sites). The selected pill inserts a check and shrinks its padding (`:299, :309-320`) so the label walks under the cursor through a 220 ms reflow — the label-shift `ConcertUi.cs:541-546` removed. Resting pills are `FillSubtleSecondary` with no border (near-invisible on Mica). Trailing glyph is hard-coded `ChevronDown` (`:358`) for an action that clears. No leading clear ✕. Rove index resets on every relayout; the rail never scrolls the selection into view. |
| "Shortcuts always showing" | `LibraryV3Document.Build:55` prepends the TopBar band unconditionally — unlike the pin band (`:62`) and Liked (`:78`) it ignores `Searching`/`Filter`/`Drilled`, and it is the only titled section V3 draws, so "Shortcuts" is the only header in the pane. |
| "Things are insanely misaligned" | Three left edges in one pane, from three renderer terms, none of them `Depth`: a bare-glyph row uses gap 12 and no art column (`SidebarEntityRow.cs:351-352, :372`); one folder anywhere in the rootlist flips every tree row into a 16-DIP chevron-cell layout the pin band never gets (`SidebarPaneSlot.cs:341`, `SidebarEntityRow.cs:530-552`); the Shortcuts band is 40 tall beside a 44 Liked row (`SidebarDisplayOptions.Links`). The chrome's leading glyphs sit at the content lane (14) while row art sits at 27. Full trace in W7. |
| Clicking a playlist moves it to the top under "Recents" | `SidebarSort.Recents` (`SidebarSort.cs:95-106`) sorts on `LastVisitedTicksUtc`, stamped from **`HistoryStore` = the navigation log** (`SidebarProjectionBinder.cs:319-331`, `SidebarRecency.cs:6-11`). `WaveeShell.Go` appends a visit on every click (`WaveeShell.cs:1797`); `HistoryVersion` is a binder trigger (`:768`) → the clicked row gets the newest stamp and lands at index 0. The uncommitted working tree already built the play-based index the Library page uses (`PlayLogStore.Recency`, `PlayRecency.cs`, fed by `PlaybackBridge.PushState:1397` and `RecentsPage.Adopt:2194`) and the staged CHANGELOG promises `"Recents" … now means recently *played*` — the sidebar was scoped out of that fix (`library-artist-jump-and-recents-implementation.md:277, :720`). |

Reference material: Spotify's own V3 (the user's screenshot) for behaviour; `C:\WAVEE\fastpotify` (Rust/egui) for
metrics and palette only — it has **no** morph, no sub-filters, no X chip, no sort control, so nothing behavioural is
transcribed from it. Its useful numbers: chip Inter 500/13, pad 12×7, radius = height/2, 6 DIP gap; selected chip =
`text` fill + `window` ink; search field 34 tall, magnifier at x+18, ✕ 15 px at right−17; nav rows 40 tall, icon 22,
Bold 15; header = icon 22 + Bold 15 title, 28×28 icon buttons 2 DIP apart.

## Target design

```
 ┌──────────────────────────────────────────┐
 │  ⌂  Home                                 │  NAV BAND (fixed chrome) — prefs.TopBar items, 40-DIP rows, no title,
 │──────────────────────────────────────────│  never scrolls, never filtered. Divider (content lane).
 │  ▤  Your Library              +   …   ‹  │  HEADER 44 — IconButton ControlSize.Small (28) for … and ‹
 │  [🔍]                    Recents ⇅ │ ≡   │  TOOLBAR 36 — search HOST (28 wide, closed) · spacer · sort/view pill
 │  (Playlists)(Podcasts)(Albums)(Artists)  │  CHIP RAIL 40 — 28-DIP bordered pills, 6 gap, horizontal ScrollView
 │──────────────────────────────────────────│  rule (content lane)
 │  ♡  Liked Songs                          │  ← the ONE SidebarPane (pins · liked · library) unchanged
 │  ▣  Dalkom Cafe        50 songs          │
 └──────────────────────────────────────────┘

 search OPEN                                       filter selected                 qualifier picked
 [🔍 Search in Your Library        ✕]  ⇅          (✕)[Playlists](By you)(By Spotify)(Mixed)   (✕)[✓ Playlists │ By you ✕]
  ▲ same node as the 28-DIP button —               ▲ round ✕ enters (fade+scale),  siblings FLIP; other kinds exit
    width 28 → row width, 170 ms Reflow;             selected = AccentDefault + Tok.OnAccent, SAME width as resting
    sort pill drops to icon-only while open           qualifier picked = ConcertUi.SegmentedPill (Sidebar register)
```

Component tree of the head (`SidebarPaneConfig.Head` → `LibraryV3Chrome`):

```
LibraryV3Chrome (Direction=1, Padding (0,8,0,0))
 ├─ LibraryV3NavBand      Key "v3-nav"      NEW  — rows from prefs.TopBar (+ LayoutVersion read), divider below
 ├─ LibraryV3Header       Key "v3-header"        — glyph · title · SidebarCreateButton · IconButton(…) · IconButton(‹)
 ├─ LibraryV3Toolbar      Key "v3-toolbar"       — [LibraryV3Search host] [spacer Grow=1, always mounted] [V3SortViewTrigger]
 ├─ LibraryV3Chips        Key "v3-chips"         — ScrollView(horizontal) ▸ slots from LibraryV3ChipStrip (pure)
 ├─ Divider               Key "v3-chrome-rule"
 ├─ Breadcrumb (drilled)  Key "v3-breadcrumb"    — unchanged
 └─ ErrorBanner / EmptyBand                      — unchanged
```

## Workstreams (parallel subagents on disjoint files; only the orchestrator builds/tests/launches)

### W1 — Search: one keyed host, real morph, real reset

Files: `Modes/LibraryV3/LibraryV3Search.cs` (rewrite), `Modes/LibraryV3/LibraryV3Header.cs` (toolbar), new pure
`Modes/LibraryV3/LibraryV3SearchRules.cs`, `Modes/LibraryV3/LibraryV3Session.cs`, `SidebarPreferences.cs`,
`Platform/AppSettings.cs`, `Wavee.Tests/TestAppSettingsShim.cs`, `SidebarProjectionBinder.cs` (debounce).

**State.** `V3SearchOpen` leaves `SidebarPreferences`/`IAppSettings` entirely (delete `SidebarKeys.V3SearchOpen` at
`AppSettings.cs:384`, the ctor read `:96`, the save/restore at `:194/:220`, `SetV3SearchOpen` `:340-345`, the shim
mirror `TestAppSettingsShim.cs:106`). It becomes `LibraryV3Session.SearchOpen : Signal<bool>` (ephemeral, like
everything else in the session). `prefs.V3Search` (the text) stays where it is — the binder reads it.

```csharp
// LibraryV3Session — replaces prefs.SetV3SearchOpen
public Signal<bool> SearchOpen { get; } = new(false);
public void OpenSearch()  => SearchOpen.SetIfChanged(true);
public void CloseSearch() { SearchOpen.SetIfChanged(false); Prefs?.V3Search.SetIfChanged(""); }
```

**Pure rules** (engine-free, test-included; the "extract the decision" pattern):

```csharp
// LibraryV3SearchRules.cs — System only
static class LibraryV3SearchRules
{
    public const float ClosedWidth = 28f;
    public const float SortIconOnlyWidth = 28f;           // the trigger's icon-only box
    public const float Gap = 4f;                          // toolbar Gap
    public enum EscapeAction : byte { None, Clear, Close }
    /// One Escape = clear the query (the filter is what you want gone first); a second closes the field.
    public static EscapeAction OnEscape(string text) => text.Length > 0 ? EscapeAction.Clear : EscapeAction.Close;
    /// Focus left the editor: an EMPTY field closes (nothing to keep visible); a query stays open — Spotify keeps it.
    public static bool ClosesOnBlur(string text) => text.Length == 0;
    /// The open host's width: the toolbar lane (pane − the toolbar's OWN padding, i.e. W7's LeadInset on the left and
    /// ContentLaneEnd on the right) minus the icon-only sort pill and one gap. Never below ClosedWidth.
    public static float OpenWidth(float paneWidth, float toolbarPadH)
        => MathF.Max(ClosedWidth, paneWidth - toolbarPadH - SortIconOnlyWidth - Gap);
}
```

**The component** — copy `DetailTracks.SearchHost` (`Features/Detail/DetailTracks.cs:2188-2309`), not the old file:

```csharp
sealed class LibraryV3Search : Component
{
    // Reflow, never Reveal: the host must PUSH the spacer/sort pill through real layout (Reveal snaps neighbours on frame 1).
    static readonly LayoutTransition HostMorph = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(WaveeMotion.Fast /*167*/, Easing.SmoothOut), Size: SizeMode.Reflow, Axes: SizeAxes.Width);
    // The two LAYERS of the query region cross-fade under DISTINCT keys (so Enter/Exit legs run, not a morph).
    static readonly LayoutTransition LayerSwap = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(WaveeMotion.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(WaveeMotion.Faster, Easing.FluentAccelerate));

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var hooks = UseContext(InputHooks.Current);
        var post = UsePost();
        var hostNode = UseRef<NodeHandle>(default);
        bool open = _session.SearchOpen.Value;
        // Quantized so a seam drag re-renders this component only when the integer width moves.
        var openWidth = UseComputed(() => MathF.Round(LibraryV3SearchRules.OpenWidth(_session.Width.Value,
            SidebarPaneMetrics.LeadInset + SidebarPaneMetrics.ContentLaneEnd)));   // the toolbar's padding (W7)
        // ONE memoised parts map (mutating TemplateParts bumps its epoch — never per render). The field LAYER mounts on
        // every open, so PartRoot.OnRealized fires per open: focus the editor AFTER commit (UsePost), through
        // FirstFocusableIn so we never focus chrome (.claude/skills/wavee/focus-pitfalls.md).
        var parts = UseMemo(() => { var p = new TemplateParts();
            p[EditableText.PartRoot] = b => b with { OnRealized = h => post(() => {
                var ed = hooks.FirstFocusableIn?.Invoke(h) ?? h; hooks.FocusNode?.Invoke(ed, true); }) };
            return p; }, DepKey.Empty);
        if (prefs is not { } p) return new BoxEl();
        string text = p.V3Search.Value;
        bool hasQuery = text.Length > 0;

        // `Animate` lives on BoxEl, not on a component embed: each LAYER is a keyed BoxEl wrapping its content
        // (DetailTracks.cs:2198-2209). The query region is Grow-based so it tracks the host's INTERPOLATED width.
        Element layer = open
            ? new BoxEl { Key = "search:field", Animate = LayerSwap, Direction = 1, Grow = 1f, MinWidth = 0f, Height = 28f,
                Children = [Embed.Comp(() => new EditableText {
                  Text = p.V3Search, Placeholder = Loc.Get(Strings.Sidebar.V3.SearchPlaceholder),
                  Width = float.NaN, Height = 28f, FontSize = 13f, Chromeless = true, Parts = parts,
                  LeftAffix = Icon(Icons.Search, 14f, Tok.TextSecondary) with { HitTestVisible = false },
                  ShowDeleteButton = true,                       // the WinUI inline ✕ (clears, keeps focus)
                  PreviewKeyDown = e => {                         // runs BEFORE the editor's own Escape (revert+blur);
                      if (e.KeyCode != Keys.Escape) return false; // returning true also pre-empts the dispatcher's global Escape blur
                      switch (LibraryV3SearchRules.OnEscape(p.V3Search.Peek())) {
                          case LibraryV3SearchRules.EscapeAction.Clear: p.V3Search.SetIfChanged(""); return true;
                          default: _session.CloseSearch(); var b = hostNode.Value; if (!b.IsNull) hooks.FocusNode?.Invoke(b, true); return true; }
                  },
                  OnFocusChanged = gained => { if (!gained && LibraryV3SearchRules.ClosesOnBlur(p.V3Search.Peek())) _session.CloseSearch(); },
                })] }
            : new BoxEl { Key = "search:icon", Animate = LayerSwap, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                          HitTestVisible = false, Children = [Icon(Icons.Search, 14f, Tok.TextSecondary)] };

        return ToolTip.Wrap(new BoxEl {
            Key = "v3-search", ZStack = true, Shrink = 0f, ClipToBounds = true,
            Width = open ? openWidth.Value : LibraryV3SearchRules.ClosedWidth, Height = 28f,
            Animate = HostMorph,
            // Corners + border mounted ALWAYS (neither is an animatable channel); only the COLOURS cross-fade.
            Corners = Radii.ControlAll, BorderWidth = 1f,
            Fill = open ? Tok.FillControlDefault : ColorF.Transparent,
            BorderColor = open ? Tok.StrokeControlDefault : ColorF.Transparent,
            HoverFill = open ? Tok.FillControlDefault : Tok.FillSubtleSecondary,
            BrushTransitionMs = WaveeMotion.Fast,
            Role = AutomationRole.Button, Focusable = !open, Cursor = open ? null : CursorId.Hand,   // Cursor is CursorId?
            OnRealized = h => hostNode.Value = h,
            OnClick = open ? null : _session.OpenSearch,
            Children = [layer],
        }, Loc.Get(Strings.Sidebar.V3.SearchTooltip));
    }
}
```

Rules honoured: no bound `Width` on the reflow node (limitation "bound dimension + reflow unsupported"); the query
region is `Grow`-based so it tracks the interpolated width every tick; `TemplateParts` memoised; no signal writes in
render (all in handlers). The **drill reset on query** (`ResetDrill` when text appears) moves from a layout effect in
the search component to `LibraryV3Chrome` (it already reads `state.Searching`) — one effect, one owner.

**Toolbar** (`LibraryV3Toolbar`): children are always `[search, spacer(Grow=1), trigger]` — no keyed swap; the host's
explicit width does the work and the spacer shrinks to 0 while open. Padding is `(LeadInset, 0, ContentLaneEnd, 0)`
(W7), which is the same pair `OpenWidth` subtracts. `iconOnly` memo reads `_session.SearchOpen` instead of
`prefs.V3SearchOpen`. Because open is no longer persisted, the sort label is visible on launch (the "sorting not
visible" complaint). Keep `SortIconOnlyWidth = 280` for genuinely narrow panes.

**Debounce** (`SidebarProjectionBinder.SidebarBinderPump`, `:904-919`): the pump owns
`var search = UseDebouncedValue(_binder._prefs.V3Search, SearchDebounceMs /*90*/);` (`_prefs` is a binder field,
`:51`; `UseDebouncedValue(IReadSignal<T>, float)` is on `Component`, `Component.cs:85`) and hands it to the binder
**once, from its attach effect** (`_binder.AttachSearch(search)` → a `_effectiveSearch` field). `Rebuild` has no
render context, so it must `Peek` that field (`:356`), and `Read(...)`'s `SearchHash` (`:778`) reads its `Value` when
subscribing — the trigger and the rebuild see the same text, so a rebuild fired by another trigger mid-typing cannot
run ahead of the debounced tick. `SidebarPane._effectiveSearch` already derives the empty-state copy from the input's
search, so the "No results for …" line and the rows can never disagree. The chrome's `state.Searching` stays live
(hiding pins/Liked the instant a character lands is fine; rows follow ≤90 ms).

### W2 — Chips: pure slot model + shared pill primitive + ✕

Files: new pure `Modes/LibraryV3/LibraryV3ChipStrip.cs` (test-included), `Modes/LibraryV3/LibraryV3Chips.cs`
(rewrite), `Components/ConcertUi.cs` (one new `SegmentedPillStyle.Sidebar` register), loc (reuse
`sidebar.v3.clearFilters` for the ✕ tooltip).

**Pure model** — the `HomeFacetStrip` pattern (`Features/Home/HomeFacetStrip.cs`):

```csharp
enum V3ChipKind : byte { Clear, Facet, Fused, Option }
readonly record struct V3ChipSlot(V3ChipKind Kind, int Code, bool Selected, string Key,
                                  int SelectFilter, int SelectQualifier);   // what a tap writes (All/Any = clear)

static class LibraryV3ChipStrip
{
    public static readonly int[] Facets = [Playlists, Podcasts, Albums, Artists];
    public static readonly int[] Qualifiers = [ByYou, BySpotify, Mixed];
    /// idle:      F F F F
    /// filtered:  X [F*] (+ its options when the facet owns a sub-filter the data evidences)
    /// fused:     X [F*│Q]            — the fused pill and the loose facet share key "v3f{code}" (the morph)
    public static List<V3ChipSlot> Slots(int filter, int qualifier, bool qualifiersAvailable)
    {
        var slots = new List<V3ChipSlot>(8);
        if (filter == All) { foreach (var f in Facets) slots.Add(Facet(f, false)); return slots; }
        slots.Add(new(V3ChipKind.Clear, 0, false, "v3-clear", All, Any));
        bool owns = filter == Playlists && qualifiersAvailable;
        if (owns && qualifier != Any) slots.Add(new(V3ChipKind.Fused, filter, true, "v3f" + filter, filter, Any)); // tap = step back
        else {
            slots.Add(new(V3ChipKind.Facet, filter, true, "v3f" + filter, All, Any));                            // tap = clear
            if (owns) foreach (var q in Qualifiers) slots.Add(new(V3ChipKind.Option, q, false, "v3q" + q, filter, q));
        }
        return slots;
    }
    /// Roving focus survives relayout by CODE, not index: returns the index of `focusedKey` or 0.
    public static int FocusIndex(List<V3ChipSlot> slots, string? focusedKey) { … }
    static V3ChipSlot Facet(int f, bool on) => new(V3ChipKind.Facet, f, on, "v3f" + f, f, Any);
}
```

Spotify parity: with a kind selected the **other kinds leave** (Exit Dx −12 / opacity) and return when the ✕ clears;
qualifier options spill after the facet; the qualifier auto-correct effect (a qualifier that stops being evidenced is
cleared) stays as today. Persisted enums untouched.

**Rendering rules**

- Resting pill: `Tok.FillControlDefault` + 1 DIP `Tok.StrokeControlDefault`, 28 DIP, `Radii.FullAll`, padding
  `(12,0,12,0)`, text `Ui.Caption`-based alias at 13 pt (add `WaveeType.Chip(s)` = `Body(s)` at 13 if no alias fits),
  `BrushTransitionMs = WaveeMotion.Fast`, `Role = ToggleButton`. **Same padding, no check glyph when selected** —
  the width of a facet is identical in both states (the `ConcertUi.FilterToken` rule).
- Selected facet: `Fill = Tok.AccentDefault`, border `Tok.AccentDefault`, ink **`Tok.OnAccent`** (contrast-picked for
  the live OS accent). Every on-accent ink in V3 (`LibraryV3Chips`, `V3SortViewFlyout.cs:211`) switches to `Tok.OnAccent`.
- Fused: `ConcertUi.SegmentedPill("v3f" + code, SegmentedPillStyle.Sidebar, facet, qualifier, onClick: stepBack)
  with { Focusable = focusIdx == i, OnRealized = h => nodes[key] = h, Role = AutomationRole.RadioButton }` — the
  primitive hard-codes `Focusable = true` / `Role = Button` (`ConcertUi.cs:648`), and the rail is ONE tab stop with
  roving focus, so the caller must override those on the returned `BoxEl`. New register in `ConcertUi.cs` beside
  `Accent`/`Strip`:

```csharp
/// The sidebar register: the Accent capsule at the rail's 28-DIP scale. Segment is FillControlSolid (opaque in both
/// themes — the FillCardDefault-on-accent mistake is documented above), on-accent ink is Tok.OnAccent (live accent),
/// and the trailing glyph is an X because tapping CLEARS the qualifier (one step back) — no menu ever opens.
public static SegmentedPillStyle Sidebar => new(
    Height: 28f, SegmentHeight: 22f, Padding: new Edges4(3f, 3f, 9f, 3f), SegmentPadding: new Edges4(8f, 0f, 8f, 0f),
    Gap: 6f, SegmentGap: 4f, TextSize: 12f, CheckSize: 10f, TrailingSize: 9f,
    Fill: Tok.AccentDefault, HoverFill: Tok.AccentSecondary, PressedFill: Tok.AccentTertiary,
    SegmentFill: Tok.FillControlSolid, SegmentShadow: Elevation.Card, SegmentInk: Tok.AccentTextPrimary,
    ValueInk: Tok.OnAccent, TrailingInk: Tok.OnAccent, TrailingGlyph: Icons.Cancel);
```

- Clear ✕: keyed `"v3-clear"`, a 28×28 round `BoxEl` (`Radii.FullAll`, `FillControlDefault` + stroke,
  `Icon(Icons.Cancel, 12f, Tok.TextPrimary)`, `.Interactive(Interaction.Subtle)`, `Role = Button`, tooltip
  `Strings.Sidebar.V3.ClearFilters`) with `Animate = new(Position | Opacity, Tween(WaveeMotion.Fast, SmoothOut),
  Enter: new EnterExit(Sx: .6f, Sy: .6f, Opacity: 0, Active: true), Exit: same)`. Click → `_session.ClearAllFilters()`.
  Its mount is not a reflow row, so the siblings' Position FLIP is not suppressed; if it visibly snaps in practice,
  fall back to a reserved fixed slot animating only opacity/scale (the engine survey's mitigation).
- Facets and options carry `Animate = Position | Opacity` (FLIP + fade); option exit `Dx −56` toward the pill
  (the dock read); facets exiting on filter `Dx −12`. The **fused pill is the only `SizeMode.Reflow` participant**
  (inside `SegmentedPill`). Drop the hairline dividers between facets (Spotify has none; fewer moving parts).
- Rail: keep `ScrollView(horizontal)` + `AutoEdgeFade` + `SuppressScrollBar`; the inner content padding becomes
  `(LeadInset, 0, ContentLaneEnd, 0)` (W7) so the first chip shares the rows' art edge; add a memoised `ScrollController` as
  `Controller`, and a `UseLayoutEffect` keyed on `(filter, qualifier)` that calls `BringIntoView(selectedNode, 0f)`
  or `ScrollTo(0, Glide)` when clearing. `nodes` dictionary entries are removed via `OnRealized`… no — keep the dict
  but rebuild it per render from the slots actually emitted (clear + refill), so stale handles cannot leak.
- Roving focus stores the focused **slot key** (`Signal<string?>`), remapped through `FocusIndex` each render.
- The a11y group label box: `Width = 0f, Height = 0f, ClipToBounds = true` so it adds nothing to scroll extent.

### W3 — Nav band: Home above the header, not in the list

Files: `Wavee.Core/Sidebar/SidebarShortcutsSection.cs` (unchanged — Curated still uses it), `LibraryV3Document.cs`,
`LibraryV3Sidebar.cs`, new `Modes/LibraryV3/LibraryV3NavBand.cs`, `LibraryV3Chrome.cs`, `Pane/SidebarPaneConfig.cs`,
`Pane/SidebarPaneRail.cs`, `LibraryV3DocumentTests.cs`.

- `LibraryV3Document.Build` **stops prepending** the shortcut section (delete step 0 at `:53-55`). It keeps the
  `topBar` parameter for exactly one rule — `LikedVisible && !SidebarShortcutsSection.ContainsRoute(topBar, "liked")`
  — so a user who put Liked Songs in the band does not get it twice. Update `LibraryV3DocumentTests` accordingly
  (the band is no longer a section; the Liked dedupe still holds).
- `LibraryV3NavBand` (first child of `LibraryV3Chrome`): reads `prefs.TopBar` and `prefs.LayoutVersion.Value` (the
  subscription), renders one `SidebarEntityRow.Create(in spec)` per item at `SidebarRowMetrics.HeightFor(Comfortable,
  subtitles:false)` (= 44) — or 40 if a `Cozy` no-subtitle row reads better against the 44 header; pick 40 (Spotify/
  fastspotify nav rows are 40) and say so in `LibraryV3Metrics.NavRowHeight`. Spec construction **mirrors
  `SidebarPaneSlot.RouteItemRow/ActionItemRow/EntityItemRow/TrackItemRow`** (glyph via `SidebarIcons.For(item,
  ShellNav.Dest(key).Glyph)`, label = `LabelOverride ?? dest.Title`, `Selected = _session.Route.Value.Name == key`,
  `OnClick = () => _session.Go(key, null)`, `Focusable = true` (the spec defaults to false — without it the rows are
  unreachable by keyboard); actions resolve through `WaveeExtensionRegistry.TryGetAction` — never
  `AppActions.All`; entity/track items get cover + fallback title and navigate/play the way the pane rows do). No
  drag, no reorder, no context menu here — the band is edited in the customizer. Padding `SidebarPaneMetrics.BandInset`
  minus the row's own inset so glyphs land on the content lane; a `Divider()` with the chrome-rule margins closes it.
  An **empty** TopBar renders nothing (the user emptied it on purpose — same rule as `Renders`).
- Rail: re-add `SidebarPaneConfig.RailHead : Func<Element?>?` (prepended before the plan's tiles in
  `SidebarPaneRail.Build`, divider after) and have `LibraryV3Sidebar` supply Route/Action TopBar items as
  `SidebarRailItem.Icon(key, glyph, selected, onClick, tooltip)` tiles. The compact rail is a `UseMemo` keyed on
  `(planVersion, Tok.Epoch, culture, binder presence, selected route)` (`SidebarPane.cs:551-564`); once the band is no
  longer in the document a TopBar edit does not move the plan, so when `Config.RailHead != null` the pane folds
  `Prefs.LayoutVersion.Value` into that `DepKey` (the comment at `:547-550` records that the old `RailHead` needed
  exactly this). Update `pitfalls.md`/`SidebarPaneRail.cs:67-71` which record the old deletion: the seam is back for
  ONE mode whose band is chrome, and it is a config delegate (rule 1), not a design branch.

### W4 — "Recents" sorts by plays

Files: `Data/SidebarLibraryEntry.cs`, `Data/SidebarProjection.cs`, `Data/SidebarSort.cs`, `Data/SidebarRecency.cs`
(comment), `SidebarProjectionBinder.cs`, `Wavee.Tests/SidebarSortTests.cs`, `SidebarProjectionTests.cs`.

```csharp
// SidebarLibraryEntry — an init member (like IsPinned), so no positional ctor churn across the projection/tests
/// uri → last-played unix ms from PlayLogStore.Recency (local plays + the server history). 0 = never played.
public long LastPlayedMs { get; init; }

// SidebarProjection.Build — one more input, stamped by URI for playlist/album/artist/show (folders + routes stay 0)
IReadOnlyDictionary<string, long>? lastPlayed, …
… LastPlayedMs = lastPlayed is { } lp && lp.TryGetValue(p.Uri, out var ms) ? ms : 0,

// SidebarSort.Recents — the LibraryNavOrder rule: played block newest-first, never-played block by SortStamp desc,
// the block split is direction-proof, `desc` reverses inside each block.
public static int Recents(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
{
    bool ap = a.LastPlayedMs > 0, bp = b.LastPlayedMs > 0;
    if (ap != bp) return ap ? -1 : 1;
    int c = ap ? b.LastPlayedMs.CompareTo(a.LastPlayedMs) : b.SortStamp.CompareTo(a.SortStamp);
    if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
    if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
    return desc ? -c : c;
}
```

- `SidebarProjectionBinder.Rebuild` passes `_playLog?.Recency` to all three `Build` calls (`:339, :343, :361`).
  `PlayLogRevision` (`:769`) is already a trigger and `PlayLogStore.MergeRecency`/`Append` bump `Version`, so a play
  here or on another device re-sorts; a **navigation no longer changes the order** (HistoryVersion still rebuilds for
  the visited FEED — that is correct and cheap).
- `LastVisitedTicksUtc` stays for `SidebarSourceMap.Visited` (the "recently opened" feed). Rewrite the semantics note
  at `SidebarRecency.cs:6-11` and the `Recents` doc comment: the sort is PLAYED, the feed is OPENED. Honest limit to
  state in that comment: `PlayRecency.Stamp(in PlayLogEntry)` stamps the track, the CONTEXT uri, the album and the
  artists (`PlayRecency.cs:38-46`), so a playlist or show is stamped only when playback started from it; an episode
  played from the queue or a Home shelf does not stamp its show (no `ShowUri` on `PlayLogEntry`). Albums and artists
  are covered regardless of context.
- Tests: `SidebarSortTests` Recents cases move from `visited:` to `played:`; add `Recents_AVisitDoesNotReorder`
  (two entries, bump `LastVisitedTicksUtc` on the second, order unchanged) and `Recents_NeverPlayedSinkAsABlock`.
  `SidebarProjectionTests`: `Build` stamps `LastPlayedMs` from the map by uri, 0 when absent.

### W5 — Header/toolbar/flyout polish (small, same files as W1)

- `…` and `‹` header buttons → `IconButton.Create(glyph, onClick, size: ControlSize.Small)` (28; focus ring,
  Space/Enter, AutomationRole for free). `SidebarCreateButton` stays.
- `V3SortViewPanel`: view-cell ink `Tok.OnAccent`; the direction chevron and check in `SortRow` are chrome → 
  `Tok.TextSecondary`/`Tok.TextPrimary` (accent-budget rule, `WaveeTokens.cs:35-37`); the retry-banner "Retry" label
  keeps accent (it is the affordance).
- Raw `TextEl { Size = … }` in the chrome → `WaveeType`/`Ui` aliases (title `Ui.BodyStrong` at 15 → keep 15 via a
  `WaveeType.PaneTitle` alias if none exists).

### W7 — Alignment: one left edge, one art size per section, one height per section

Files: `Data/SidebarRowGeometry.cs`, `Shared/SidebarEntityRow.cs`, `Pane/SidebarPaneSlot.cs`, `Shared/SidebarCover.cs`,
`Pane/SidebarPaneMetrics.cs`, `Wavee.Core/Sidebar/SidebarLayoutModel.cs`, the V3 chrome bands (W1–W3 files), tests
`SidebarRowGeometryTests`, `RootlistSlotResolverTests`, `SidebarDropCueTests`, `SidebarPaneInvariantTests`.

**Root causes** (`x` = pane-relative DIP; `PanePad.Left 8 + IndentFor(0) 6 = ContentLane 14` for every row):

| Row family | Leading visual x | Label x | Why |
|---|---|---|---|
| glyph rows (Home, Liked) | 29 (16-DIP bare icon) | 57 | `SidebarEntityRow.cs:351-352, :372`: `bareGlyph ⇒ gap = 12` (art rows use 10) and the icon is drawn bare — **no art-width column** |
| pin band art rows (album *and* artist — circular only changes the corner radius, `SidebarCover.cs:41-42`) | 27 | 69 | `StandardLeading` = gutter 3 + gap 10 (`:516-526`) |
| tree depth-0 rows when the rootlist has ANY folder | 33 | 75 | `SidebarPaneSlot.cs:341` `treeNode = Kind == PlaylistTree && SectionHasFolder(section)` (a **section-wide** probe) ⇒ `TreeLeading` reserves `TreeChevronCell 16` for every row with no gap (`:530-552`) |
| pinned FOLDER row | 33 + 12·rootlistDepth | — | `FolderRow` sets `TreeNode = true` unconditionally (`SidebarPaneSlot.cs:499`) with `TreeDepth = entry.Depth` (`:457`) |
| Shortcuts band vs Liked row | — | — | `SidebarDisplayOptions.Links` is Cozy/no-subtitle = **40** tall (`SidebarLayoutModel.cs:278-289`) while `v3.liked` is Comfortable = **44** |
| chrome bands vs rows | 14 | — | `BandInset` puts the header glyph, the magnifier and the first chip at the content lane (14) while every row's art/glyph sits at 27 — a 13-DIP ragged edge between the head and the list |

`Depth`/`IndentFor` is **not** a differing term (every family is depth 0 = 6). Also dead: `SidebarCover.ForPin`
(`:84-90`, zero call sites) — a second art-sizing entry point waiting to drift.

**Fix (geometry constants first, then consumers):**

```csharp
// SidebarRowGeometry — the ONE leading lane, named once
public const float LeadingGap = 10f;                                   // gutter → leading visual, ALL row shapes
public const float LeadingLaneWidth = SelGutterWidth + LeadingGap;    // 13: IndentFor(depth) + this = the art/glyph x
/// The art column's left edge for a row at `depth` — the number the drop caret, the depth pick and the chrome share.
public static float ArtX(int depth) => PaneEdge + IndentFor(depth) + LeadingLaneWidth;   // 27 at depth 0
// Keep the ORIGINAL spelling (IndentFor(0) + d·TreeGuideStep), not IndentFor(depth): the two are equal only while
// IndentStep == TreeGuideStep, and the caret must not break silently if they ever diverge.
public static float TreeContentX(int depth) => IndentFor(0) + LeadingLaneWidth + depth * TreeGuideStep;   // was … + TreeChevronCell
```

- `SidebarEntityRow.Create`: the bare-glyph arm renders its 16-DIP icon **centred in an `ArtFor(density)`-wide box**
  (the box `:373` already builds for the no-leading case) and uses the same `LeadingGap` — a glyph row's column IS the
  art column, so its label lands where an art row's label lands in the same density (69 at Cozy).
- `TreeLeading` no longer reserves a chevron cell before the art. Layout = `[gutter 3][guides depth·12][gap 10][leading]`,
  identical to `StandardLeading` at depth 0. The folder's **disclosure chevron moves to the trailing column**: it is the
  FIRST element of the folder row's trailing cluster (before the count badge and the folder "+" that `FolderRow`
  already places there, `SidebarPaneSlot.cs:1142-1164`), using the existing rotating `SidebarChevron.Disclosure(open,
  size)` (`Shared/SidebarChevron.cs:46`, today wired at `SidebarPaneSlot.cs:510`). Because the hover "…" is a 26-DIP
  ZStack overlay parked at the row's right edge (`SidebarEntityRow.cs:615-631`), `Create` gives the trailing cluster a
  right padding equal to that overlay's width whenever `Overflow` is true — which also stops count badges from
  disappearing under the "…" on hover today. The folder mosaic tile stays the leading visual (Spotify's folder rows have
  no leading chevron either). `TreeChevronCell` is deleted. `SectionHasFolder` stops being a geometry switch: it may
  decide whether guides are drawn, never where the art starts.
- `FolderRow` uses the same `treeNode` test as `EntryRow` (`section.Kind == PlaylistTree`), so a pinned folder cannot
  out-indent its pinned siblings; its `TreeDepth` is the row's relative depth, never the rootlist depth.
- `InsertionLine` (`SidebarPaneSlot.cs:1366`) and `RootlistSlotResolver.PickDepth` already read `TreeContentX`, so the
  caret and the depth pick stay exact by construction; their tests' numbers change **deliberately** (25→19 at depth 0,
  the depth-1 boundary from 43 to `TreeContentX(1) + 6 = 37`).
- Glyph bands must be **44 tall with a 32-wide column**, or the label lane breaks again: `Comfortable` would make the
  glyph column `ArtFor(Comfortable) = 40` → label at 77 beside content rows at 69. So `SidebarDisplayOptions.Shortcuts`/
  `Links` AND `v3.liked` (`LibraryV3Document.cs:84-89`) become `Density: Cozy, Subtitles: true, Artwork: false` —
  `HeightFor(Cozy, true) = 44`, `ArtFor(Cozy) = 32`, label 69, and no subtitle is drawn because route rows never pass
  one (`AllowsDisplayField(StaticLinks, Subtitles)` is false, so the user cannot flip it). Update
  `LibraryV3DocumentTests.TheLikedShortcut_IsAGlyphRouteRow_AtAContentRowHeight`, `SidebarBuiltInDocumentTests`
  (Classic's glyph bands) and the `SidebarRowGeometryTests` Classic⇄Curated parity accordingly.
- Delete `SidebarCover.ForPin`. `SidebarPaneMetrics.ArtSize(section)` is the only art-size owner.
- **Chrome bands align to the art column**: add `SidebarPaneMetrics.LeadInset = BandInset.Left + LeadingLaneWidth`
  (27) and use it for every leading chrome visual — the header's library glyph box, the nav band (rows already land
  there), the closed search host (and therefore the open field's left edge, so the morph never moves its origin) and
  the chip rail's content padding. Trailing edges stay at `ContentLaneEnd` (16), which is where a row's overflow button
  already ends. Full-width rules/dividers keep `BandInset`.

Result: art/glyph at **27** and text at **27 + ArtFor(density) + 10** for every row of every section in every design;
one height per section; the head's leading glyphs share the rows' column. Pin `ArtX(0) == 27` and
`ArtX(0) == BandInset.Left + LeadingLaneWidth` in `SidebarPaneInvariantTests`, and add a cross-family test that a
glyph row and an art row at the same density produce the same label x (all terms are in engine-free files).

### W6 — Docs, skill, changelog, issues

- `docs/plans/wavee/library-v3-chrome-implementation.md` = this file (with the wireframes).
- `.claude/skills/wavee-sidebar/where-to-change-what.md`: add rows for the search host, the chip strip (pure model +
  pixels), the nav band, `RailHead`. `pitfalls.md`: V3 search open is session-only (not a setting); `RailHead` exists
  again for V3; Recents = played. `architecture.md`: the `Head` composition and `RailHead`.
- `CHANGELOG.md` `[0.2.7]` bullets (each ending with ` (#n)`): search rebuild; filter pills; nav band; "Recents" in
  the sidebar = recently played. **Issues**: before commit, look up/create the GitHub issues through the
  `github-triage` skill (every modifying `gh` call approved by the user first) so the release gate's issue-ref check
  passes; commits carry `Fixes #n`.
- Loc: no new keys needed (`sidebar.v3.clearFilters`, `searchPlaceholder`, `searchTooltip` exist); remove none.

## Engine changes

None are required. `SizeMode.Reflow` width tween on a keyed host, opacity Enter/Exit layers, `EditableText`
(`Chromeless`, `ShowDeleteButton`, `PreviewKeyDown`, `OnFocusChanged`, `WidthSignal`), `ScrollController.BringIntoView`,
`Tok.OnAccent`, `IconButton.Create(ControlSize.Small)` all exist. Optional follow-ups (separate engine PRs, not this
sweep): a `Chip`/`ChipRail` control in `FluentGpu.Controls` (Wavee hand-rolls chips four times), `FocusHandle`, and
a container-level `ChildLayout` FLIP flag. If W2's ✕ mount visibly snaps siblings, the engine-side fix is the
FLIP-vs-reflow arbitration in `AppHost.ApplyProjections` — otherwise use the reserved-slot fallback.

## Test inventory

| File | Adds |
|---|---|
| `Wavee.Tests.csproj` | `Compile Include` for `LibraryV3ChipStrip.cs`, `LibraryV3SearchRules.cs` (pure, System only) |
| `LibraryV3ChipStripTests.cs` (new) | idle = 4 facets; filtered = Clear + facet (+ options only under Playlists with qualifiers evidenced); fused shares the facet key; Clear slot writes (All, Any); fused slot writes (filter, Any); `FocusIndex` survives relayout |
| `LibraryV3SearchRulesTests.cs` (new) | Escape ladder; blur closes only when empty; `OpenWidth` floor |
| `LibraryV3DocumentTests.cs` | no shortcut section in the document; Liked dedupe still keyed on `topBar` |
| `SidebarSortTests.cs` / `SidebarProjectionTests.cs` | played-based Recents; a visit does not reorder; stamping by uri |
| `TestAppSettingsShim.cs` | drop the `V3SearchOpen` mirror |
| `SidebarRowGeometryTests.cs` / `SidebarPaneInvariantTests.cs` | `ArtX(0) == 27 == BandInset.Left + LeadingLaneWidth`; `TreeContentX` = 19/31/43/67; glyph row and art row at one density share a label x; `Links`/`Shortcuts`/`v3.liked` = Cozy + subtitle intent (44 tall, 32 column) |
| `RootlistSlotResolverTests.cs` / `SidebarDropCueTests.cs` | depth-pick boundary and line width follow the new `TreeContentX` (deliberate number changes, semantics unchanged) |

## Verification

1. `dotnet build Wavee.slnx` and `dotnet build Wavee.slnx -c Release` (TreatWarningsAsErrors).
2. `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` (baseline 6.6k+, plus the new suites).
3. `dotnet run --project src/apps/Wavee -- --fake`, sidebar design = Library V3:
   - Search: click 🔍 → the same node widens to the row (sort pill shrinks to icon), caret is in the field; type →
     rows filter, ✕ appears; Escape once → text cleared, still focused; Escape again → field collapses, focus on the
     magnifier; type then click a row → field stays open with the query; empty field + click elsewhere → collapses.
     Relaunch → field closed, "Recents" label visible.
   - Chips: select Playlists → round ✕ scales in on the left, other kinds slide out, By you/By Spotify/Mixed appear;
     pick By you → fused `[✓ Playlists │ By you ✕]`, legible in light/dark and with a pale OS accent; tap the fused
     pill → back to loose + options; tap ✕ → all four facets return; Left/Right/Home/End roving focus keeps its chip
     across the relayout; a selected chip beyond the viewport is glided into view.
   - Nav: Home sits above "Your Library", never scrolls, is highlighted on the Home route, unaffected by filters and
     search; the 56-DIP rail shows a Home tile; the customizer's Top bar edits (add/remove/rename) show up live.
   - Sort: under Recents, clicking playlists does not reorder; playing one moves it to the top; the never-played block
     keeps its added-date order; Recently added / Alphabetical / Creator unchanged.
   - Alignment (screenshot at 100 % and 150 %): the header glyph, the magnifier, the first chip, the nav-band glyph,
     Liked's heart, every pinned cover (album and circular artist) and every depth-0 playlist cover share ONE left
     edge; labels of glyph rows and art rows line up within a density; a rootlist with a folder does not shift the
     playlist band; a pinned folder sits flush with its pinned siblings; the folder chevron is in the trailing column
     and the drop caret starts exactly at the art edge of the row it targets. Check Classic and Curated too — the row
     primitive is shared.
4. No engine files touched ⇒ no VerticalSlice run owed. If the optional engine fallback is taken, run the engine's
   Debug + Release build and VerticalSlice in `..\fluent-gpu`.
