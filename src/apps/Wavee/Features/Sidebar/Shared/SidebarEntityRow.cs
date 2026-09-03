using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// F.1.4 — THE ONE sidebar entity row. Classic's pinned rows, Library V3's list rows and Curated's entity rows all come
// out of Create(in SidebarRowSpec) so the three designs cannot drift apart visually.
//
// WHAT IT OWNS (and why nothing else may re-implement it):
//
//  • The 4-STATE SELECTION-AWARE HOVER/PRESS RAMP — the stock NavigationViewItem backplate ladder
//    (NavigationView.cs:1174-1176 / NavigationView_themeresources): rest Transparent · Selected=Secondary; hover
//    Secondary · SelectedPointerOver=Tertiary; pressed Tertiary · SelectedPressed=Secondary. A selected row must DARKEN
//    on hover, never flatten into its own rest fill. This is copied verbatim from the landed WaveeSidebar rows; the whole
//    reason this factory exists is that the ramp is written ONCE.
//  • The height ladder by density, the 3-DIP selection gutter (the pill's reserve — see SidebarSelectionPill), the depth
//    indent, and the slot order.
//  • OnRealized CHAINING for the selection pill: a row both registers its node for the pill AND can carry a caller's own
//    realize handler; ContextMenu.Attach chains onto whatever this leaves behind (it never clobbers).
//
// PURE STATIC by design: a Component per row would cost a mount per slot in a virtualized 10k list. Nothing here
// allocates beyond the returned records and the children array.

/// <summary>Row-height + metric rules, split out so a virtualizing surface can size a slot WITHOUT building the row.
///
/// <para>The height / indent ARITHMETIC is not here: it lives in <see cref="SidebarRowGeometry"/> (engine-free, and
/// therefore the only version a test can reach — this file is engine-bound and is deliberately not source-included by
/// Wavee.Tests). These members forward, so there is still ONE ladder; only the art ladder, which needs the engine-side
/// <see cref="SidebarCover"/> sizes, is owned here.</para></summary>
static class SidebarRowMetrics
{
    /// <inheritdoc cref="SidebarRowGeometry.ClassicHeight"/>
    public const float ClassicHeight = SidebarRowGeometry.ClassicHeight;

    /// <inheritdoc cref="SidebarRowGeometry.HeightFor(SidebarDensity, bool)"/>
    public static float HeightFor(SidebarDensity density, bool hasSubtitle)
        => SidebarRowGeometry.HeightFor(density, hasSubtitle);

    /// <summary>Art size by density — the art slot shrinks with the row so the 3-DIP gutter and the text baseline stay put.
    /// Delegates to <see cref="SidebarRowGeometry.ArtFor"/> (the engine-free ladder a test can reach); <see cref="SidebarCover.S20"/>/
    /// <see cref="SidebarCover.S32"/>/<see cref="SidebarCover.S40"/> are asserted equal to it rather than restated.</summary>
    public static float ArtFor(SidebarDensity density) => SidebarRowGeometry.ArtFor(density);

    /// <inheritdoc cref="SidebarRowGeometry.IndentFor"/>
    public static float IndentFor(int depth) => SidebarRowGeometry.IndentFor(depth);

    /// <inheritdoc cref="SidebarRowGeometry.SubtitleVisible"/>
    public static bool SubtitleVisible(SidebarDensity density, string? subtitle)
        => SidebarRowGeometry.SubtitleVisible(density, subtitle);
}

/// <summary>
/// Everything <see cref="SidebarEntityRow.Create"/> needs. A mutable struct with public fields (the
/// <c>InteractionRecipe</c> precedent): a caller fills the slots it cares about with an object initializer and passes it
/// by <c>in</c>, so a row costs no allocation beyond its elements.
///
/// <para><b>Struct-default caveat</b> (same as <c>InteractionRecipe</c>): the field defaults below apply only through the
/// parameterless constructor — always write <c>new SidebarRowSpec { … }</c>, never <c>default</c>.</para>
/// </summary>
struct SidebarRowSpec
{
    // EVERY field is assigned here, explicitly: the non-nullable string fields would otherwise trip CS8618 and the rest
    // would rely on C# 11's auto-default — and this file builds under TreatWarningsAsErrors.
    public SidebarRowSpec()
    {
        Key = "";
        Label = "";
        Subtitle = null;
        Selected = false;
        Enabled = true;
        Depth = 0;
        TreeNode = false;
        TreeDepth = 0;
        TreeContinuationMask = 0;
        Density = SidebarDensity.Cozy;
        Gap = float.NaN;
        Height = float.NaN;
        ArtSize = float.NaN;
        Leading = null;
        Glyph = null;
        DisclosureChevron = null;
        Pinned = false;
        Trailing = null;
        Playing = false;
        PlayingAnimated = false;
        Track = false;
        Overflow = false;
        OnClick = null;
        OnRealized = null;
        MenuOverlay = null;
        Menu = null;
        Drag = null;
        DropActive = null;
        DropTarget = null;
        CheckLane = null;
        MultiSelected = false;
        ChecksVisible = null;
        Animate = null;
        Caption = null;
        Focusable = false;
        OnRename = null;
        OnMove = null;
        OnActivate = null;
        OnEscape = null;
    }

    // ── identity ─────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>REQUIRED. The reconciler key AND the key the caller registers this row's node under for the selection
    /// pill. Must be stable per item (an entry/pin id or a route key) — never an index.</summary>
    public string Key;

    /// <summary>REQUIRED. The row's title. This is also its accessible name (the engine has no separate automation-name
    /// channel; the text under the cursor IS the announced name).</summary>
    public string Label;

    /// <summary>The second line (track count / kind · creator / item count). Ignored at <see cref="SidebarDensity.Compact"/>.</summary>
    public string? Subtitle;

    // ── state ────────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Drives the 4-state ramp (rest/hover/pressed all shift when selected). Compute it from the live route.</summary>
    public bool Selected;

    /// <summary>False dims the row and drops its ramp — the missing-entity / unavailable-extension retention state.</summary>
    public bool Enabled;

    /// <summary>Nesting depth (rootlist folders / Curated groups). 12 DIP per level, clamped at 4.</summary>
    public int Depth;

    /// <summary>True for a PlaylistTree row. Tree rows reserve one stable disclosure column so sibling artwork aligns
    /// whether the row is a folder or a leaf.</summary>
    public bool TreeNode;

    /// <summary>Depth inside the playlist tree itself (separate from <see cref="Depth"/>, which may also include a
    /// containing CustomGroup). Each level is drawn as a connector cell instead of anonymous left padding.</summary>
    public int TreeDepth;

    /// <summary>One bit per tree level: a set bit means that level has a later sibling and its vertical connector must
    /// continue through this row. The current level always draws an elbow and stops at its midpoint when its bit is clear.</summary>
    public byte TreeContinuationMask;

    /// <summary>Row height + art size ladder.</summary>
    public SidebarDensity Density;

    /// <summary>Main-axis gap. NaN ⇒ 10 with an art slot, 12 with a bare glyph (the landed Classic metrics).</summary>
    public float Gap;

    /// <summary>PIN the row height instead of deriving it from <see cref="Density"/> + subtitle presence. NaN ⇒ derive.
    /// Set it whenever a section must be UNIFORM regardless of which rows happen to carry a subtitle — a
    /// <c>Reorderable</c>'s slot pitch and a virtualizing host's extent both assume one height per section, and a mixed
    /// 40/44 list silently breaks their geometry. (Classic's pinned section pins it to
    /// <see cref="SidebarRowMetrics.ClassicHeight"/> for exactly that reason.)</summary>
    public float Height;

    /// <summary>Art-slot edge. NaN ⇒ <see cref="SidebarRowMetrics.ArtFor"/>.</summary>
    public float ArtSize;

    // ── slots ────────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The leading visual — build it with <see cref="SidebarCover"/>. Null falls back to <see cref="Glyph"/>.</summary>
    public Element? Leading;

    /// <summary>A bare 16-DIP glyph in the leading column when <see cref="Leading"/> is null (the shortcut-row shape).
    /// It tints with selection exactly like Classic's library rows.</summary>
    public string? Glyph;

    /// <summary>A folder row's disclosure chevron. W7 moved it OUT of the leading cluster (it used to sit before the
    /// leading visual, reserving a fixed cell every tree row paid for even when it was not a folder) and INTO the
    /// TRAILING cluster instead — the FIRST element there, ahead of the now-playing equalizer and <see cref="Trailing"/>.
    /// Build it with <c>SidebarChevron.Disclosure(open)</c> (the rotating glyph), not a hand-rolled swap.</summary>
    public Element? DisclosureChevron;

    /// <summary>Issue #85 (H1) — this row is a PINNED library entry (<c>SidebarLibraryEntry.IsPinned</c>). Renders a
    /// quiet 12-DIP pin glyph in the trailing cluster, beside the count badge — a per-row marker rather than a
    /// section header, so it keeps working when a pin is filtered into the middle of a lens (Library V3 renders no
    /// section headers at all — see <c>LibraryV3Document.cs</c> — and the pinned band itself, <c>v3.pins</c>, floats
    /// pins to the top of every sort mode but has never actually MARKED the rows it floats). Never true for a
    /// <see cref="Track"/> row (a track cannot be pinned).</summary>
    public bool Pinned;

    /// <summary>Trailing content (a count badge, a "new" dot, a state glyph). Placed before the overflow affordance.</summary>
    public Element? Trailing;

    /// <summary>Show the now-playing equalizer in the trailing column (this row's context is the one playing).</summary>
    public bool Playing;

    /// <summary>Animate the equalizer (playing) vs hold it low (paused-on-this-row). Ignored unless <see cref="Playing"/>.</summary>
    public bool PlayingAnimated;

    /// <summary>A TRACK row: the leading art gains a hover-revealed scrim + play glyph, and activation PLAYS rather than
    /// navigates. Tracks are never pinnable; callers may still supply a drag payload for playlist deposit/reorder.</summary>
    public bool Track;

    /// <summary>Render the hover-revealed 26-DIP "…" that re-enters the context-request funnel
    /// (<c>ClickRequestsContext</c>), so the trailing button and right-click open the SAME menu. Requires
    /// <see cref="MenuOverlay"/> + <see cref="Menu"/>, else it is omitted rather than rendered dead.</summary>
    public bool Overflow;

    // ── wiring ───────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Activation (navigate, or play for a <see cref="Track"/> row). Null ⇒ a non-interactive row.</summary>
    public Action? OnClick;

    /// <summary>The caller's realize handler — typically <c>h =&gt; rowNodes[key] = h</c> for the selection pill. It is
    /// CHAINED, never replaced, by anything this factory or <c>ContextMenu.Attach</c> adds afterwards.</summary>
    public Action<NodeHandle>? OnRealized;

    /// <summary>The overlay service the context menu opens through (<c>UseContext(Overlay.Service)</c>).</summary>
    public IOverlayService? MenuOverlay;

    /// <summary>The context-menu factory, invoked AT OPEN TIME (never at render time) — e.g.
    /// <c>() =&gt; Menus.SidebarEntry(acts, in entry, toggleFolder, expanded)</c>.</summary>
    public Func<ContextMenuModel?>? Menu;

    /// <summary>Makes the row a typed Wavee resource drag source. Leave null for rows already wrapped by
    /// <c>Reorderable.Item</c> (which installs its own source).</summary>
    public WaveeResourceDragPayload? Drag;

    /// <summary>Optional resource destination (playlist deposit and/or pinned-band insertion).</summary>
    public DropTargetSpec? DropTarget;

    /// <summary>Cold compatibility cue for a live resource drag, for the WHOLE-ROW surfaces whose only outcome is
    /// "this row takes it" — the rail folder flyout's rows (<c>SidebarRailFolderFlyout</c>), which are rebuilt on every
    /// open and never recycled.
    ///
    /// <para><b>A PLAN ROW DOES NOT USE THIS.</b> Its <c>Into</c> plate is drawn by the slot's own always-mounted
    /// <c>SidebarPaneSlot.DropPlate()</c>, UNDER the row, because the reconciler wires bound thunks at MOUNT ONLY: a
    /// per-row thunk built here captures <c>enabled &amp;&amp; selected</c> — and, in the retired <c>DropCue</c> form,
    /// the row's plan index — as VALUES, so a recycled slot kept answering for the row it was first mounted with. That
    /// is the stale-cue defect, and deleting <c>DropCue</c> is what removed the shape that carried it.</para></summary>
    public Func<bool>? DropActive;

    /// <summary>The row's layout transition. Leave null when a <c>Reorderable</c> wraps the row — <c>Reorderable.Item</c>
    /// applies <c>LayoutTransition.Slide</c> FLIP itself, and an authored offset hint plus a position track is a
    /// documented stomp (see <c>ReorderList</c>'s remarks).</summary>
    public LayoutTransition? Animate;

    /// <summary>An extra caption line under the subtitle (the keyboard-reorder position announcement, "3 of 12").</summary>
    public string? Caption;

    /// <summary>The MULTI-SELECT check lane (<c>SelectorVisualsBound.BoundCheckLane</c>), rendered as the row's FIRST
    /// child. Bound throughout — visibility and checked state are both thunks — so a selection change re-skins the lane
    /// without re-rendering the row. Null on every row that is not a multi-selectable tree row.
    /// <para>Rule 4 survives it: the lane changes the row's WIDTH allocation, never its height.</para></summary>
    public Element? CheckLane;

    /// <summary>This row is in the tree MULTI-SELECTION (not the open route). Draws the quiet
    /// <c>Tok.FillSubtleSecondary</c> plate; the accent <c>SelectedRest</c> plate and the pill stay ROUTE-only, so the
    /// two selections can never be mistaken for one another. A static value, re-rendered on the row's own epoch.</summary>
    public bool MultiSelected;

    /// <summary>Is the check lane currently visible? Read at PRESS/KEY time to synthesize WinUI's multi-select tap
    /// (<c>SelectorVisualsBound.MultiSelectMods</c>: while the lane is up, a plain tap TOGGLES into the selection
    /// instead of replacing it). A probe, never a value — the lane appears and disappears under a mounted row.</summary>
    public Func<bool>? ChecksVisible;

    /// <summary>Make the row itself a focus stop. Leave false when a <c>Reorderable</c> wraps it — its wrapper is the
    /// focus stop and the keyboard-lift key handler, and two stops per row would double the tab order.</summary>
    public bool Focusable;

    /// <summary><b>F2</b> on this row. Supplying it also makes the row a focus stop — a key handler on a node nothing
    /// can focus is dead code, and <c>InputDispatcher</c> routes keys from the focused node upward, never into children.
    /// Set it only where the verb is REAL (a playlist whose metadata this user may edit, a rootlist folder); a row that
    /// cannot be renamed stays exactly as unfocusable as it was, so the tab order grows only by rows that earned it.
    /// Leave null when a <c>Reorderable</c> wraps the row: that wrapper owns both the focus stop and the key handler.</summary>
    public Action? OnRename;

    /// <summary><b>Alt+↑ / Alt+↓</b> on this row: move it one position within its own list (<c>-1</c> up, <c>+1</c>
    /// down). Supplying it also makes the row a focus stop, for the same reason <see cref="OnRename"/> does.
    ///
    /// <para>ALT, not a bare arrow: bare arrows are the pane's roving navigation, and the detail track list already took
    /// Alt+↑/↓ for exactly this gesture (<c>DetailTracks</c>) — one chord, one meaning, across the app. Set it only for
    /// a ROOTLIST TREE row, and never for a <c>Reorderable</c>-wrapped one: that wrapper owns the focus stop and the
    /// keyboard-lift handler, and two of each per row is a documented stomp. The command itself decides the ends of the
    /// run, so a move that cannot happen is a silent no-op rather than a wrap-around.</para></summary>
    public Action<int>? OnMove;

    /// <summary>ACTIVATION WITH MODIFIERS — the multi-select gesture seam, set only by a PlaylistTree row that is not
    /// inside a <c>Reorderable</c> band. When present it REPLACES <see cref="OnClick"/>: the row wires
    /// <c>OnPointerReleased</c> (the detail track-row shape, <c>DetailTracks</c>) so Ctrl/Shift reach the handler, and
    /// Enter/Space reach it through the row's one key handler.
    ///
    /// <para>The row applies WinUI's tap rule before calling: a DOUBLE click always activates plainly
    /// (<c>KeyModifiers.None</c> — navigate, or toggle the folder), and a single tap while <see cref="ChecksVisible"/>
    /// is up gets Ctrl synthesized, i.e. toggles into the selection.</para></summary>
    public Action<KeyModifiers>? OnActivate;

    /// <summary>ESCAPE on a focused row: clear the selection and leave check mode. Its own member rather than a
    /// modifier on <see cref="OnActivate"/> — "cancel" is not an activation, and encoding it as one is how one chord
    /// ends up meaning two things.</summary>
    public Action? OnEscape;
}

static class SidebarEntityRow
{
    /// <summary>The Classic 44-DIP row height, re-exported so call sites do not have to know about
    /// <see cref="SidebarRowMetrics"/> to size a <c>Reorderable</c>.</summary>
    public const float ClassicHeight = SidebarRowMetrics.ClassicHeight;

    /// <inheritdoc cref="SidebarRowMetrics.HeightFor"/>
    public static float HeightFor(SidebarDensity density, bool hasSubtitle)
        => SidebarRowMetrics.HeightFor(density, hasSubtitle);

    /// <summary>The row's keyboard verbs: <b>F2</b> = rename, <b>Alt+↑/↓</b> = move one position and — on a
    /// multi-selectable tree row — <b>Enter</b> = activate, <b>Space</b> = toggle into the selection, <b>Escape</b> =
    /// clear it. ONE handler, because <c>InputDispatcher</c> routes keys from the focused node upward and a row can only
    /// have one. Built outside <see cref="Create"/>'s object initializer because an <c>in</c> parameter cannot be
    /// captured by a lambda.</summary>
    static Action<KeyEventArgs> KeyHandler(Action? rename, Action<int>? move, Action<KeyModifiers>? activate,
                                           Action? escape) => e =>
    {
        if (rename is not null && e.KeyCode == Keys.F2 && e.Mods == KeyModifiers.None)
        {
            e.Handled = true;
            rename();
            return;
        }
        switch (e.KeyCode)
        {
            // Enter INVOKES (WinUI never multi-select-synthesizes the EnterKey trigger); Space TOGGLES into the
            // selection; Escape cancels it. Each arm is inert when its command is absent, so a row that only renames
            // keeps exactly the behaviour it had.
            case Keys.Enter when activate is not null:
                e.Handled = true; activate(KeyModifiers.None); return;
            case Keys.Space when activate is not null && !e.IsRepeat:
                e.Handled = true; activate(e.Mods | KeyModifiers.Ctrl); return;
            case Keys.Escape when escape is not null:
                e.Handled = true; escape(); return;
        }
        // Exactly Alt — Alt+Shift+↑ belongs to whatever claims it next, and swallowing it here would make this row the
        // reason that chord does nothing.
        if (move is null || e.Mods != KeyModifiers.Alt) return;
        if (e.KeyCode == Keys.Up) { e.Handled = true; move(-1); }
        else if (e.KeyCode == Keys.Down) { e.Handled = true; move(1); }
    };

    /// <summary>The height <paramref name="spec"/> will render at — for a virtualizing host that must size the slot
    /// before (or without) building the row.</summary>
    public static float HeightOf(in SidebarRowSpec spec)
        => float.IsNaN(spec.Height)
            ? SidebarRowMetrics.HeightFor(spec.Density, SidebarRowMetrics.SubtitleVisible(spec.Density, spec.Subtitle))
            : spec.Height;

    /// <summary>Build the row. Returns a <see cref="BoxEl"/> (not <c>Element</c>) so a caller can still <c>with</c> extra
    /// fields on it — but everything in the F.1.4 contract is already applied, and the fill ramp in particular must never
    /// be overridden downstream.</summary>
    public static BoxEl Create(in SidebarRowSpec spec)
    {
        bool selected = spec.Selected;
        bool enabled = spec.Enabled;
        bool hasSubtitle = SidebarRowMetrics.SubtitleVisible(spec.Density, spec.Subtitle);
        float height = float.IsNaN(spec.Height) ? SidebarRowMetrics.HeightFor(spec.Density, hasSubtitle) : spec.Height;
        float art = float.IsNaN(spec.ArtSize) ? SidebarRowMetrics.ArtFor(spec.Density) : spec.ArtSize;
        bool bareGlyph = spec.Leading is null && spec.Glyph is { Length: > 0 };
        // W7: ONE gap for every row shape (no more bareGlyph ⇒ 12 special case) — SidebarRowGeometry.LeadingGap is the
        // same 10 StandardLeading and TreeLeading already used, so a glyph row's label lands exactly where an art row's
        // does at the same density (both now build an ArtFor(density)-wide leading column; see the leading column below).
        float gap = float.IsNaN(spec.Gap) ? SidebarRowGeometry.LeadingGap : spec.Gap;
        // A copy: an `in` parameter cannot be captured by the bound paint thunk. This is the WHOLE-ROW cue only (the
        // rail folder flyout's rows). A PLAN ROW's `Into` plate is the slot's own `DropPlate()` under the row, because a
        // thunk built here is wired at MOUNT and would answer for the row this slot first mounted with.
        var plateOn = spec.DropActive;
        var activate = spec.OnActivate;
        var checksVisible = spec.ChecksVisible;
        // The row's RESTING plate, as a VALUE, re-asserted on every reconcile (and therefore re-entering the 83 ms
        // BrushTransition cross-fade for free): the open ROUTE takes the accent plate, a row in the tree MULTI-SELECTION
        // takes the quiet one. Two selections, two skins, and the pill stays route-only.
        ColorF rest = !enabled ? ColorF.Transparent
            : selected ? WaveeColors.SelectedRest
            : spec.MultiSelected ? Tok.FillSubtleSecondary
            : ColorF.Transparent;
        // The row's HOVERED plate, as a value.
        ColorF hoverFill = !enabled ? ColorF.Transparent : selected ? WaveeColors.SelectedHover : Tok.FillSubtleSecondary;

        // ── leading column ──────────────────────────────────────────────────────────────────────────────────────────
        // W7: the bare-glyph arm gets the SAME ArtFor(density)-wide box the art arm builds below, with its 16-DIP icon
        // CENTRED inside it — a glyph row's column IS the art column now, not a narrower bare icon, so its label lands
        // where an art row's label lands at the same density (both add ArtX(0) + ArtFor(density) + LeadingGap).
        Element leading;
        if (spec.Leading is { } given) leading = given;
        else if (bareGlyph) leading = new BoxEl
        {
            Width = art, Height = art, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Icon(spec.Glyph!, 16f, selected ? Tok.TextPrimary : Tok.TextSecondary)],
        };
        else leading = new BoxEl { Width = art, Height = art, Shrink = 0f };   // keeps the leading column's width stable
        if (spec.Track && spec.Leading is not null)
            leading = TrackArt(leading, art, spec.Density);

        // ── text column ─────────────────────────────────────────────────────────────────────────────────────────────
        Element text;
        if (!hasSubtitle && spec.Caption is null)
        {
            // Shrink + MinWidth 0, like the stacked arm below: a Grow-only TextEl keeps its intrinsic width and runs
            // under the trailing cluster instead of ellipsizing when the pane is narrower than the title.
            text = Body(spec.Label) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 };
        }
        else
        {
            int lines = 1 + (hasSubtitle ? 1 : 0) + (spec.Caption is { Length: > 0 } ? 1 : 0);
            var stack = new Element[lines];
            int n = 0;
            stack[n++] = Body(spec.Label) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 };
            if (hasSubtitle)
                stack[n++] = Caption(spec.Subtitle!).Secondary() with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 };
            if (spec.Caption is { Length: > 0 } cap)
                stack[n++] = new TextEl(cap) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
            text = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 1f, Children = stack };
        }

        // ── children ────────────────────────────────────────────────────────────────────────────────────────────────
        // The trailing "…" is NOT a flex sibling of the text any more (see OverflowButton()): reserving its 26-DIP
        // width + gap UNCONDITIONALLY — even though HoverOpacity keeps it invisible at rest — is exactly the bug this
        // shape fixes (a title ellipsized well before the pane's free space ran out). It is composed as a ZSTACK
        // OVERLAY on top of the row instead, so it costs the text lane ZERO width whether or not the row is hovered:
        // no reserved-vs-revealed toggle, and therefore no reflow/re-trim on hover either.
        bool overflow = ShowsOverflow(in spec);
        // W7: the folder disclosure chevron moved OUT of the leading cluster and into the TRAILING one — it is the
        // FIRST trailing element (ahead of the now-playing equalizer, ahead of Trailing's count badge / "+"), because a
        // folder is never `Playing` and the chevron reads as "this row has children", which belongs beside the other
        // row-state glyphs rather than mixed into the leading art column. Built as ONE grouped element (not up to three
        // loose flex children) so the overflow padding below applies once, to the whole cluster.
        Element? trailingCluster = null;
        {
            int trailingCount = (spec.DisclosureChevron is null ? 0 : 1) + (spec.Playing ? 1 : 0)
                              + (spec.Pinned ? 1 : 0) + (spec.Trailing is null ? 0 : 1);
            if (trailingCount > 0)
            {
                var parts = new Element[trailingCount];
                int t = 0;
                if (spec.DisclosureChevron is { } chevron) parts[t++] = chevron;
                if (spec.Playing) parts[t++] = WaveeEqualizer.Of(spec.PlayingAnimated, Tok.AccentDefault, 12f);
                // H1 (#85) — the pin marker sits right beside the count badge (SidebarPaneSlot.TrailingBadge / the
                // library-shortcut count), ahead of whichever one this row also carries.
                if (spec.Pinned) parts[t++] = Icon(Icons.Pin, 12f, Tok.TextTertiary);
                if (spec.Trailing is { } trailingContent) parts[t++] = trailingContent;
                trailingCluster = new BoxEl
                {
                    Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = gap,
                    // The hover-revealed "…" is a 26-DIP ZSTACK overlay parked at the row's trailing edge (see
                    // OverflowButton()); without this reserve a chevron or count badge sits UNDER it on hover. Only
                    // reserved when the row actually carries the affordance — most rows do not pay for it.
                    Padding = overflow ? new Edges4(0f, 0f, OverflowWidth, 0f) : default,
                    Children = parts,
                };
            }
        }
        int count = 2                                                   // leading cluster + text
                  + (trailingCluster is null ? 0 : 1);
        var kids = new Element[count];
        int k = 0;
        Element leadingCluster = spec.TreeNode
            ? TreeLeading(leading, spec.TreeDepth, spec.TreeContinuationMask, height)
            : StandardLeading(leading, gap);
        // The multi-select check lane (WinUI's inline lane that slides in from −28 px and pushes the row's content
        // right) rides INSIDE the leading cluster, in a gapless box — never as a sibling of it in the gapped row. As a
        // sibling, its `Flow.Show` boundary was still a flex child while HIDDEN, so the row's 10-DIP Gap after it
        // pushed every rootlist row (the only rows that carry the lane) one gap right of the album/artist rows beside
        // them. It is bound, so it still costs a mounted-but-hidden node and no re-render when the selection changes.
        if (spec.CheckLane is { } checkLane)
            leadingCluster = new BoxEl
            {
                Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children = [checkLane, leadingCluster],
            };
        kids[k++] = leadingCluster;
        kids[k++] = text;
        if (trailingCluster is not null) kids[k++] = trailingCluster;

        // Without the overflow affordance, the row stays the plain flex row it always was — no extra node, no ZStack
        // measure/arrange cost for the vast majority of non-menu rows (a folder end-cap, a disabled retention row, …).
        Element[] rowChildren;
        if (overflow)
        {
            var content = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = gap, Children = kids };
            // The button is a ZSTACK OVERLAY, never a flex sibling: it must cost the title NO width, or the title
            // ellipsizes early with visible free space beside it (the bug this replaced). A long title's last
            // glyphs can therefore sit under the hovered "…" — accepted deliberately: the row is translucent over
            // live Mica, so there is no opaque tone to fade or clip into (see the WaveeTokens.cs tombstone on why a
            // constant material over Mica reads as a slab), and a hover-conditional width would re-render and
            // relayout the row on every pointer enter/exit.
            rowChildren = [content, OverflowButton()];
        }
        else
        {
            rowChildren = kids;
        }

        var row = new BoxEl
        {
            Key = spec.Key,
            Animate = spec.Animate,
            OnRealized = spec.OnRealized,
            // ZStack only when the button exists: `content` then fills the row exactly like the plain flex children
            // did (its own AlignSelf/JustifySelf are unset ⇒ Auto ⇒ inherit this row's AlignItems/Justify, so the whole
            // content cluster shrinks-to-fit and centers vertically the same way each flex child used to individually
            // — see the file-level remarks above OverflowButton()). Padding stays HERE (not innermost), so the fill
            // ramp below still paints the row's FULL bleed and only the CONTENT is inset by it, exactly as before.
            ZStack = overflow,
            Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = gap,
            Padding = new Edges4(SidebarRowMetrics.IndentFor(spec.Depth), 0f, 8f, 0f),
            Corners = CornerRadius4.All(4f),
            // THE 4-STATE SELECTION-AWARE RAMP (F.1.4). Defined here, once, for every sidebar row in every design.
            // Selected takes the accent plate and its states only ever go UP — see WaveeColors.SelectedRest for the
            // inversion this replaces and why hovered-selected is composed over the plate rather than swapped under it.
            // An ARMED DROP reads as a lit PLATE, not a hairline. The spotlight scrim dims the rest of the app to 55%
            // black and cuts this row out of it, so the row is being presented as one of a handful of answers — a single
            // 1-DIP accent border was far too quiet to carry that, especially next to SidebarPinDropZone's dashed accent
            // card. Bound, never re-rendered: this runs while a drag is live, inside the 0-alloc frame region.
            Fill = plateOn is null
                ? rest
                : Prop.Of(() => plateOn() ? Tok.AccentDefault with { A = 0.18f } : rest),
            HoverFill = hoverFill,
            PressedFill = !enabled ? ColorF.Transparent : selected ? WaveeColors.SelectedPressed : Tok.FillSubtleTertiary,
            BorderWidth = plateOn is null ? 0f : 1f,
            BorderColor = plateOn is null ? ColorF.Transparent
                : Prop.Of(() => plateOn() ? Tok.AccentDefault : ColorF.Transparent),
            Opacity = enabled ? 1f : 0.55f,
            IsEnabled = enabled,
            // A row that carries `OnActivate` wires the POINTER-RELEASED path instead, because `OnClick` throws the
            // modifiers away and Ctrl/Shift ARE the gesture on a multi-selectable tree row (the detail track-row shape).
            OnClick = enabled && activate is null ? spec.OnClick : null,
            OnPointerReleased = enabled && activate is not null
                ? args => activate(args.ClickCount >= 2
                                       // A DOUBLE click always activates plainly — navigate, or toggle the folder —
                                       // even while the check lane is up, which is WinUI's DoubleTap rule.
                                       ? KeyModifiers.None
                                       : SelectorVisualsBound.MultiSelectMods(checksVisible?.Invoke() ?? false, args.Mods))
                : null,
            Focusable = spec.Focusable
                        || (enabled && (spec.OnRename is not null || spec.OnMove is not null || activate is not null)),
            // F2 renames, Alt+↑/↓ reorders, Enter/Space/Escape drive the selection — ONE handler, because
            // InputDispatcher routes keys from the focused node upward and a row can only have one. Every arm is a
            // no-op when its command is absent, so a row that can only be renamed keeps exactly the behaviour it had.
            OnKeyDown = enabled && (spec.OnRename is not null || spec.OnMove is not null
                                    || activate is not null || spec.OnEscape is not null)
                ? KeyHandler(spec.OnRename, spec.OnMove, activate, spec.OnEscape)
                : null,
            // Stationary lift at the standard 0.4 source dim. This is the RESOURCE drag only: a row inside a
            // reorderable band carries no Drag payload at all (Reorderable.Item installs its own source and position
            // track — a second one is a documented stomp), so the vacated-slot case never reaches this line.
            // ...and the CLICK-PRIMARY mouse drag box (×2, WinUI's list-item multiplier): navigating to the row is the
            // constant intent, dragging it out the exception, so a click landed while the pointer is still travelling
            // must not be eaten by a promotion. Reorder is unaffected — a row in a reorderable band has no spec.Drag.
            Draggable = enabled && spec.Drag is { } payload
                ? Drag.Source(WaveeDragKinds.Resource, () => payload,
                              thresholdMultiplier: Drag.ClickPrimaryThresholdMultiplier)
                : null,
            DropTarget = spec.DropTarget,
            Children = rowChildren,
        };

        // Right-click / Menu key / long-press. Attach CHAINS onto the row's existing OnRealized + OnContextRequested —
        // it never clobbers the pill's measurement capture.
        if (spec.MenuOverlay is { } svc && spec.Menu is { } factory) row = row.WithContextMenu(svc, factory);
        return row;
    }

    /// <summary>The 3-DIP reserve where the selection accent sits. The MOVING pill is the single overlay
    /// <see cref="SidebarSelectionPill"/>, so the reserve exists purely to keep row content from shifting as selection
    /// moves.</summary>
    public static Element SelGutter() => new BoxEl { Width = SidebarRowGeometry.SelGutterWidth, Shrink = 0f };

    /// <summary>The ordinary row's leading cluster: the selection gutter, then the leading visual, one <paramref name="gap"/>
    /// apart. No disclosure lane here any more (W7) — a folder's chevron is a TRAILING element now, so this is exactly
    /// what a depth-0 <see cref="TreeLeading"/> also builds.</summary>
    static Element StandardLeading(Element leading, float gap) => new BoxEl
    {
        Direction = 0, Shrink = 0f, Gap = gap, AlignItems = FlexAlign.Center,
        Children = [SelGutter(), leading],
    };

    /// <summary>A tree row: the selection gutter and depth's worth of connector cells (butted together, no gap between
    /// them — <see cref="SidebarRowGeometry.TreeContentX"/> is exactly this sum), then ONE <see cref="SidebarRowGeometry.LeadingGap"/>
    /// before the leading visual — identical to <see cref="StandardLeading"/> at depth 0. W7 removed the fixed 16-DIP
    /// disclosure cell this used to reserve ahead of the art on EVERY tree row (folder or leaf): a leaf row paid for a
    /// chevron it never drew the moment its section had any folder in it, and the reserve put every tree row 4 DIP right
    /// of every other row family. The folder's chevron now lives in the row's TRAILING cluster instead
    /// (<see cref="SidebarRowSpec.DisclosureChevron"/>, wired in <see cref="Create"/>).</summary>
    static Element TreeLeading(Element leading, int depth, byte continuationMask, float height)
    {
        int levels = Math.Clamp(depth, 0, 4);
        // Gutter and guides are ONE unit with no gap between them — SidebarRowGeometry.TreeContentX sums SelGutterWidth
        // and depth·TreeGuideStep back-to-back, so a gap here would put the caret and PickDepth's ladder one gap short
        // of what the row actually draws (F2/F3).
        Element lane = levels > 0
            ? new BoxEl
              {
                  Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
                  Children = [SelGutter(), TreeGuides(levels, continuationMask, height)],
              }
            : SelGutter();
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = SidebarRowGeometry.LeadingGap, AlignItems = FlexAlign.Center,
            Children = [lane, leading],
        };
    }

    static Element TreeGuides(int depth, byte continuationMask, float height)
    {
        var cells = new Element[depth];
        for (int level = 1; level <= depth; level++)
        {
            bool current = level == depth;
            bool continues = (continuationMask & (1 << (level - 1))) != 0;
            var marks = new List<Element>(2);
            if (current || continues)
                marks.Add(new BoxEl
                {
                    Width = 1f, Height = current && !continues ? height / 2f : height, Shrink = 0f,
                    AlignSelf = FlexAlign.Start, Margin = new Edges4(Spacing.XS, 0f, 0f, 0f),
                    Fill = Tok.StrokeDividerDefault,
                });
            if (current)
                marks.Add(new BoxEl
                {
                    Width = Spacing.S, Height = 1f, Shrink = 0f, AlignSelf = FlexAlign.Start,
                    Margin = new Edges4(Spacing.XS, height / 2f, 0f, 0f), Fill = Tok.StrokeDividerDefault,
                });
            cells[level - 1] = new BoxEl
            {
                Width = SidebarRowGeometry.TreeGuideStep, Height = height, Shrink = 0f, ZStack = true,
                HitTestPassThrough = true, Children = [.. marks],
            };
        }
        return new BoxEl
        {
            Direction = 0, Width = depth * SidebarRowGeometry.TreeGuideStep, Height = height, Shrink = 0f,
            HitTestPassThrough = true, Children = cells,
        };
    }

    /// <summary>Name the activation of a TRACK row ("Play track"). The engine exposes no automation-name channel, so the
    /// only place a non-visual name can live is a tooltip — call this on every <see cref="SidebarRowSpec.Track"/> row
    /// (<c>Create</c> cannot: it returns a <see cref="BoxEl"/>, and <c>ToolTip.Wrap</c> returns a component wrapper).
    /// One helper, one loc key, so the queue / now-playing / artist-top-tracks feeds all announce it identically.
    ///
    /// <para><c>grow: 1f</c> because the wrap is not free: <c>ToolTip.Wrap</c>'s service wrapper is a flex ROW, so an
    /// unwrapped sibling row filled the pane's column while a WRAPPED one shrank to its own title — the track row's
    /// hover/selected fill plate came out visibly narrower than the rows above and below it. The opt-in makes the wrap
    /// layout-transparent on that axis; it is the ToolTip twin of the <c>Grow = 1f</c> the reorder-band and outline
    /// call sites already carry for <c>Reorderable.Item</c>'s wrapper.</para></summary>
    public static Element WithPlayTrackHint(Element row)
        => ToolTip.Wrap(row, Loc.Get(Strings.Sidebar.Item.PlayTrack), grow: 1f);

    static bool ShowsOverflow(in SidebarRowSpec spec)
        => spec.Overflow && spec.Enabled && spec.MenuOverlay is not null && spec.Menu is not null;

    /// <summary>The hover overlay's box (26 DIP) — shared with the trailing cluster's own reserve in <c>Create</c>, so a
    /// chevron or a count badge can never sit under it whichever this changes to.</summary>
    const float OverflowWidth = 26f;

    /// <summary>The hover-revealed trailing "…", as a ZSTACK OVERLAY (a sized layer inside the row's outer
    /// <c>ZStack = true</c> — see <c>Create</c>) rather than a flex sibling of the text: <c>JustifySelf = End</c> +
    /// <c>AlignSelf = Center</c> park it flush against the row's own trailing 8-DIP padding, vertically centred,
    /// WITHOUT ever taking a share of the row's main-axis width. That is the fix — the old flex-sibling shape (with
    /// `Shrink = 0f`) reserved its 26-DIP width + gap from the text's measuring width EVEN AT REST, while
    /// <c>Opacity = 0f</c> only ever hid its paint, not its layout footprint, so a title was ellipsized well before
    /// the pane's free space actually ran out. An overlay costs the text lane nothing either way, so hovering never
    /// re-trims/reflows the title (a reserve-toggle would). <c>ClickRequestsContext</c> re-enters the context-request
    /// funnel, so the walk finds the row's own <c>OnContextRequested</c> (the <c>WithContextMenu</c> attach) and the
    /// button and the right-click open the same menu anchored at the button.</summary>
    static Element OverflowButton() => new BoxEl
    {
        Width = OverflowWidth, Height = OverflowWidth, JustifySelf = FlexAlign.End, AlignSelf = FlexAlign.Center,
        Opacity = 0f, HoverOpacity = 1f,
        Children =
        [
            new BoxEl
            {
                Width = OverflowWidth, Height = OverflowWidth, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(13f),
                HoverFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                ClickRequestsContext = true,
                Children = [Icon(Icons.More, 14f, Tok.TextSecondary)],
            },
        ],
    };

    /// <summary>A track row's art: the cover with a hover-revealed scrim + play glyph over it. The reveal rides
    /// <c>HoverOpacity</c> on a DESCENDANT, which the engine's hover propagation resolves against the ROW's hover
    /// (AnimScheduler.Hover: only reveal/scale affordances follow their container) — so hovering anywhere on the row
    /// lights it.</summary>
    static Element TrackArt(Element cover, float art, SidebarDensity density) => ZStack(
        cover,
        new BoxEl
        {
            Width = art, Height = art, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(SidebarCover.Radius(art, circular: false)),
            // A scrim is dark in BOTH themes, so the glyph over it is literal white (never Tok.TextOnAccentPrimary,
            // which is BLACK in the dark theme).
            Fill = new ColorF(0f, 0f, 0f, 0.55f),
            Opacity = 0f, HoverOpacity = 1f, HitTestVisible = false,
            Children = [Icon(Icons.Play, density == SidebarDensity.Compact ? 10f : 14f, ColorF.FromRgba(0xFF, 0xFF, 0xFF))],
        }) with { Width = art, Height = art, Shrink = 0f };
}
