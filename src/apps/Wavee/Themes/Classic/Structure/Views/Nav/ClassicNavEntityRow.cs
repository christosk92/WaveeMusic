// ── Themes/Classic/Structure/Views/Nav/ClassicNavEntityRow.cs — Classic's one entity row (themes plan 6.1, T5f) ─────────────────
//
// Moved from `Shell/Sidebar.UI.Rows.cs` (`Sidebar.EntityRow`, `RowSpec`, `RowDropCue`) by T5f, the last sidebar lane: the row's
// ladder (the icon column, the label gap, the trailing pad, the folder chevron column, the indent) is the structure's, and core no
// longer carries a copy of it. Everything that draws a plan row, a rail tile, a flyout row or the Library head's Home and Search
// rows builds from this one primitive, so the layouts cannot drift apart. Numbers are `Rules\SidebarGeometry.cs`.
//
// Everything here is either a PURE STATIC over a value (a Component per row would cost a mount per slot in a 10k virtualized list)
// or a small Component that owns exactly the hooks a static cannot. Props freeze at mount, so every changing input is a Func probe,
// never a value.

using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

// Classic's rows and Library V3's list rows all come out of ClassicEntityRow.Create, so the two layouts cannot drift apart.
// It owns the neutral NavigationViewItem backplate ramp (`NavRow`: the pill is the
// only "you are here"), the WinUI row ladder (40-px icon column, label at pane 48, 31-px depth indent), the slot
// order, the one key handler and the drag source.

/// <summary>A whole-row drop cue the structure paints on a row that is rebuilt per open (the rail folder flyout): <c>Active</c>
/// says when it is armed, the rest is what it paints while armed (read live, so a theme token swap lands).</summary>
internal readonly record struct ClassicRowDropCue(Func<bool> Active, Func<ColorF> Fill, Func<ColorF> Border, float BorderWidth);

/// <summary>Everything <see cref="ClassicEntityRow.Create"/> needs: a mutable struct filled with an object initializer and
/// passed by <c>in</c>, so a row costs no allocation beyond its elements. Always <c>new ClassicRowSpec { … }</c>, never
/// <c>default</c> — the defaults below apply only through the constructor.</summary>
internal struct ClassicRowSpec
{
    public ClassicRowSpec()
    {
        Key = ""; Label = ""; Subtitle = null; Selected = false; Enabled = true; Depth = 0;
        Shape = SidebarRowShape.EntityTwoLine; Tile = false; LabelTooltip = false; Height = float.NaN; Ink = null;
        ArtSize = float.NaN; Leading = null; Glyph = null; DisclosureChevron = null; Pinned = false; OnUnpin = null; Trailing = null;
        Playing = false; PlayingAnimated = false; Track = false; Overflow = false; OnClick = null; OnRealized = null;
        MenuOverlay = null; Menu = null; Drag = null; DropTarget = null; DropCue = null; Animate = null;
        Caption = null; CheckLane = null; MultiSelected = false; ChecksVisible = null; Focusable = false;
        OnRename = null; OnMove = null; OnActivate = null; OnEscape = null;
    }

    // ── identity ──
    /// <summary>REQUIRED. The reconciler key and the pill-registration key: stable per item, never an index.</summary>
    public string Key;
    /// <summary>REQUIRED. The title — also the accessible name (the engine has no separate automation-name channel).</summary>
    public string Label;
    /// <summary>Second line (count / kind · creator). Drawn only in an <see cref="SidebarRowShape.EntityTwoLine"/> row.</summary>
    public string? Subtitle;

    // ── state ──
    /// <summary>The open ROUTE is this row: the selected leg of the neutral NavRow ramp (the pill is drawn by the slot).</summary>
    public bool Selected;
    /// <summary>False dims to 0.3 and drops the ramp (missing entity / unavailable action retention).</summary>
    public bool Enabled;
    /// <summary>Nesting depth: 31 DIP per level, capped at 3.</summary>
    public int Depth;
    /// <summary>The section's ONE row shape (height, art size, subtitle line).</summary>
    public SidebarRowShape Shape;
    /// <summary>A compact-rail tile (P2): 40 wide, icon only, the label as tooltip.</summary>
    public bool Tile;
    /// <summary>The full label as a tooltip (a truncated title, or a rail tile).</summary>
    public bool LabelTooltip;
    /// <summary>The label's (and so the glyph's — one ink per row) colour when not TextPrimary: a pin that is unavailable,
    /// or still unnamed (design Q9, D9). Null = TextPrimary.</summary>
    public ColorF? Ink;
    /// <summary>PIN the height (NaN ⇒ derive). A section pins it so a Reorderable pitch and the extent table see ONE
    /// height per section.</summary>
    public float Height;
    /// <summary>Art-slot edge. NaN ⇒ <c>SidebarGeometry.ArtOf(Shape)</c>.</summary>
    public float ArtSize;

    // ── slots ──
    /// <summary>The leading visual (build it with <see cref="Sidebar.Cover"/>). Null falls back to <see cref="Glyph"/>.</summary>
    public Element? Leading;
    /// <summary>A 16-DIP glyph centred in an art-wide column when <see cref="Leading"/> is null; in a text row (covers off) it leads the label
    /// at the text lead, and a text row without one starts with its label.</summary>
    public string? Glyph;
    /// <summary>A folder's <see cref="Chevron.Disclosure"/> — the FIRST trailing element, never in the leading lane.</summary>
    public Element? DisclosureChevron;
    /// <summary>A pinned library entry (#85): the 24-px Unpin button (S5) before the equalizer and the count. Never on a track.</summary>
    public bool Pinned;
    /// <summary>What the Unpin button does (S5). Null draws the same 24 box as a quiet mark (no pin store to drop it from).</summary>
    public Action? OnUnpin;
    /// <summary>Trailing content (a count badge, a "+").</summary>
    public Element? Trailing;
    /// <summary>This row's context is the playing one: the accent equalizer (<see cref="Controls.EqualizerH"/>).</summary>
    public bool Playing;
    /// <summary>Animate the equalizer (playing) vs hold it low (paused on this row).</summary>
    public bool PlayingAnimated;
    /// <summary>A TRACK row: the cover gains a hover scrim + ▶, and activation plays.</summary>
    public bool Track;
    /// <summary>Render the hover-revealed 26-DIP "…" (needs <see cref="MenuOverlay"/> + <see cref="Menu"/>).</summary>
    public bool Overflow;

    // ── wiring ──
    /// <summary>Activation. Null ⇒ non-interactive.</summary>
    public Action? OnClick;
    /// <summary>The caller's realize handler — chained, never replaced, by the context-menu attach.</summary>
    public Action<NodeHandle>? OnRealized;
    /// <summary>The overlay the context menu opens through.</summary>
    public IOverlayService? MenuOverlay;
    /// <summary>The menu factory, invoked AT OPEN TIME (never at render time).</summary>
    public Func<ContextMenuModel?>? Menu;
    /// <summary>The resource this row lifts. The row builds the drag source itself (click-primary). Leave null inside a
    /// <c>Reorderable</c> band — the band installs its own source.</summary>
    public DragPayload? Drag;
    /// <summary>Optional drop destination.</summary>
    public DropTargetSpec? DropTarget;
    /// <summary>WHOLE-ROW cue for surfaces rebuilt per open (the rail folder flyout): when it is armed and what it paints
    /// (the structure's). A plan row does NOT use this — its plate is the slot's own always-mounted DropPlate, because a
    /// thunk here is wired at mount and would answer for the row this slot first mounted with.</summary>
    public ClassicRowDropCue? DropCue;
    /// <summary>Layout transition. Null inside a Reorderable (it owns position — one transform owner per node).</summary>
    public LayoutTransition? Animate;
    /// <summary>A third text line (11 px tertiary). Nothing sets it today; the shape is free.</summary>
    public string? Caption;
    /// <summary>The multi-select lane (<c>SelectorVisualsBound.BoundCheckLane</c>), placed as a sibling before the icon column.</summary>
    public Element? CheckLane;
    /// <summary>In the tree MULTI-selection: the quiet FillSubtleSecondary plate (never the accent one).</summary>
    public bool MultiSelected;
    /// <summary>Is the check lane up? Read at PRESS/KEY time to synthesize WinUI's multi-select tap.</summary>
    public Func<bool>? ChecksVisible;
    /// <summary>Make the row a focus stop. False inside a Reorderable (its wrapper is the stop).</summary>
    public bool Focusable;
    /// <summary>F2. Also makes the row a focus stop.</summary>
    public Action? OnRename;
    /// <summary>Alt+↑ (-1) / Alt+↓ (+1) within its own list. Also makes the row a focus stop.</summary>
    public Action<int>? OnMove;
    /// <summary>Activation WITH modifiers (a multi-selectable tree row) — replaces <see cref="OnClick"/>.</summary>
    public Action<KeyModifiers>? OnActivate;
    /// <summary>Escape: clear the selection and leave check mode.</summary>
    public Action? OnEscape;
}

internal static class ClassicEntityRow
{
    /// <summary>The height <paramref name="spec"/> renders at, for a host that sizes a slot before building the row.</summary>
    public static float HeightOf(in ClassicRowSpec spec)
        => float.IsNaN(spec.Height) ? SidebarGeometry.HeightOf(spec.Shape) : spec.Height;

    /// <summary>WinUI's NavigationViewItem backplate ramp verbatim (TR:9-32 / 75-98): neutral fills, the pill is the only
    /// "you are here". Rest / hover / pressed / disabled, brush cross-fade 83 ms (ControlFaster).</summary>
    public static InteractionRecipe NavRow(bool selected) => new()
    {
        Fill = new StateBrush(
            selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
            selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            Tok.FillSubtleTransparent),
        BrushMs = 83f,
    };

    /// <summary>Build the row. Returns a <see cref="BoxEl"/> so a caller can still <c>with</c> layout fields (Grow for a
    /// Reorderable wrapper); the fill ramp must never be overridden downstream.</summary>
    public static BoxEl Create(in ClassicRowSpec spec)
    {
        bool selected = spec.Selected;
        bool enabled = spec.Enabled;
        bool hasSubtitle = !spec.Tile && SidebarGeometry.SubtitleVisible(spec.Shape, spec.Subtitle);
        bool hasCaption = spec.Caption is { Length: > 0 };
        float height = float.IsNaN(spec.Height) ? SidebarGeometry.HeightOf(spec.Shape) : spec.Height;
        float art = float.IsNaN(spec.ArtSize) ? SidebarGeometry.ArtOf(spec.Shape) : spec.ArtSize;
        var activate = spec.OnActivate;
        var checksVisible = spec.ChecksVisible;
        // Icon colour = label colour in every state (NVX:425-427): TextPrimary, a disabled row fades as a whole.
        var ink = spec.Ink ?? Tok.TextPrimary;

        // ── the icon column: the glyph (16) or art centred in the 32 art slot at slot x 10 (a rail tile: centred in 40) ──
        Element visual = spec.Leading
            ?? (spec.Glyph is { Length: > 0 } g ? Icon(g, SidebarGeometry.GlyphSize, ink) : new BoxEl { Width = art, Height = art });
        if (spec.Track && spec.Leading is not null) visual = TrackArt(visual, art, spec.Shape);
        // A text row (S0/S4) has no art column: the folder glyph (16) at the text lead and the 8 gap, or the label right at the lead.
        bool textRow = !spec.Tile && SidebarGeometry.IsText(spec.Shape);
        bool textGlyph = textRow && spec.Leading is null && spec.Glyph is { Length: > 0 };
        Element iconColumn = textRow
            ? new BoxEl
            {
                Width = SidebarGeometry.LabelX(spec.Shape, textGlyph) - (textGlyph ? SidebarGeometry.LabelGap : 0f), Height = height, Shrink = 0f,
                Padding = new Edges4(SidebarGeometry.TextLead, 0f, 0f, 0f), AlignItems = FlexAlign.Center,
                Children = textGlyph ? [visual] : [],
            }
            : new BoxEl
            {
                Width = spec.Tile ? SidebarGeometry.TileWidth : SidebarGeometry.IconColumn, Height = height, Shrink = 0f,
                Padding = new Edges4(spec.Tile ? 0f : SidebarGeometry.LeadInset, 0f, 0f, 0f),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [visual],
            };
        if (spec.Tile)
            return Finish(in spec, new BoxEl
            {
                Key = spec.Key, Animate = spec.Animate, OnRealized = spec.OnRealized,
                Width = SidebarGeometry.TileWidth, Height = height, Shrink = 0f,
                Margin = new Edges4(0f, SidebarGeometry.RowMarginY, 0f, SidebarGeometry.RowMarginY),
                Corners = Radii.ControlAll, AlignItems = FlexAlign.Center,
                Children = [iconColumn],
            }, enabled, selected, activate, checksVisible);

        // ── the label column ──
        Element text;
        if (!hasSubtitle && !hasCaption)
        {
            text = Body(spec.Label) with
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                Wrap = TextWrap.NoWrap, Color = ink,
            };
        }
        else
        {
            var lines = new List<Element>(3)
            {
                Body(spec.Label) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1, Wrap = TextWrap.NoWrap, Color = ink, LineHeight = SidebarGeometry.TwoLineLabelHeight },
            };
            if (hasSubtitle)
                lines.Add(Caption(spec.Subtitle!).Secondary() with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1, Wrap = TextWrap.NoWrap });
            if (hasCaption)
                lines.Add(R.Type.MicroMeta(spec.Caption!) with
                {
                    Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                });
            text = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center,
                Children = [.. lines],
            };
        }

        // ── trailing: Unpin button · equalizer · count slot, ending at W − 18; the chevron column replaces the 14 pad ──
        bool overflow = spec.Overflow && enabled && spec.MenuOverlay is not null && spec.Menu is not null;
        // The count slot: the row's trailing content, or (S2/S5) an empty slot the "…" lands in when the row has none but carries the
        // Unpin button or the equalizer.
        Element? slot = spec.Trailing
            ?? (SidebarGeometry.ReservesSlot(overflow, false, spec.Pinned, spec.Playing)
                ? new BoxEl { Width = SidebarGeometry.CountSlot, Height = SidebarGeometry.CountSlot, Shrink = 0f }
                : null);
        int trailingCount = (spec.Playing ? 1 : 0) + (spec.Pinned ? 1 : 0) + (slot is null ? 0 : 1);
        var kids = new List<Element>(6);
        // The check lane is a gapless sibling BEFORE the icon column: while the checks show it pushes the whole row
        // right (WinUI's multi-select content offset) instead of crushing the 40-px column.
        if (spec.CheckLane is { } lane) kids.Add(lane);
        kids.Add(iconColumn);
        if (!textRow || textGlyph) kids.Add(new BoxEl { Width = SidebarGeometry.LabelGap, Shrink = 0f });
        kids.Add(text);
        if (trailingCount > 0)
        {
            var parts = new Element[trailingCount];
            int t = 0;
            if (spec.Pinned) parts[t++] = UnpinButton(spec.OnUnpin, enabled);
            if (spec.Playing) parts[t++] = Controls.Equalizer(spec.PlayingAnimated, Tok.AccentDefault, Controls.EqualizerH);
            // With a hover "…" the count slot hands its place over (S2): it fades out as the button fades in over it, so the count
            // ends at W − 18 at rest instead of leaving a button-wide hole beside it. The Unpin button and the equalizer stay.
            if (slot is not null)
                parts[t] = overflow ? new BoxEl { Shrink = 0f, HoverOpacity = 0f, Children = [slot.Value] } : slot.Value;
            kids.Add(new BoxEl
            {
                Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = SidebarGeometry.TrailingGap,
                Margin = new Edges4(SidebarGeometry.TrailingGap, 0f, 0f, 0f), Children = parts,
            });
        }
        bool chevron = spec.DisclosureChevron is not null;
        if (spec.DisclosureChevron is { } chev)
            kids.Add(new BoxEl
            {
                Width = SidebarGeometry.ChevronColumn, Height = height, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [chev],
            });

        // The "…" is a ZSTACK OVERLAY, never a flex sibling: on a row with no trailing content it costs the title ZERO
        // width, so a title never ellipsizes early and hovering never re-trims it. A long title may run under it —
        // accepted: the row is translucent over Mica, there is no opaque tone to fade into, and a hover-conditional
        // width would relayout on every enter.
        Element[] rowChildren = overflow
            ? [new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Children = [.. kids] },
               OverflowButton(chevron ? SidebarGeometry.ChevronColumn : 0f)]
            : [.. kids];

        var row = new BoxEl
        {
            Key = spec.Key,
            Animate = spec.Animate,
            OnRealized = spec.OnRealized,
            ZStack = overflow,
            Direction = 0, Height = height, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, SidebarGeometry.RowMarginY, 0f, SidebarGeometry.RowMarginY),
            Padding = new Edges4(SidebarGeometry.IndentFor(spec.Depth), 0f, chevron ? 0f : SidebarGeometry.TrailingPad, 0f),
            Corners = Radii.ControlAll,
            Children = rowChildren,
        };
        return Finish(in spec, row, enabled, selected, activate, checksVisible);
    }

    /// <summary>The wiring every row shape shares: the neutral ramp (the multi-selection's quiet plate is the selected
    /// rest fill without a pill), disabled 0.3 (TR:501), the click / modifier-aware activation, the keys, the drag
    /// source, the drop target, and the context menu.</summary>
    static BoxEl Finish(in ClassicRowSpec spec, BoxEl row, bool enabled, bool selected, Action<KeyModifiers>? activate,
                        Func<bool>? checksVisible)
    {
        var own = SurfaceRules.Ownership(inSlot: false, hasClick: enabled && (activate is not null || spec.OnClick is not null));
        row = (row with
        {
            Opacity = enabled ? 1f : SidebarGeometry.DisabledOpacity,
            IsEnabled = enabled,
            Role = AutomationRole.NavigationItem,
            Cursor = SurfaceRules.Cursor(in own),
            OnClick = enabled && activate is null ? spec.OnClick : null,
            OnPointerReleased = enabled && activate is not null
                ? args => activate!(args.ClickCount >= 2
                    ? KeyModifiers.None
                    : SelectorVisualsBound.MultiSelectMods(checksVisible?.Invoke() ?? false, args.Mods))
                : null,
            Focusable = spec.Focusable || (enabled && (spec.OnRename is not null || spec.OnMove is not null || activate is not null)),
            OnKeyDown = enabled && (spec.OnRename is not null || spec.OnMove is not null || activate is not null || spec.OnEscape is not null)
                ? KeyHandler(spec.OnRename, spec.OnMove, activate, spec.OnEscape)
                : null,
            Draggable = enabled && spec.Drag is { } payload ? Drag.Source(() => payload, clickPrimary: true) : null,
            DropTarget = spec.DropTarget,
        }).Interactive(NavRow(selected || spec.MultiSelected), isEnabled: enabled);
        // The folder flyout's whole-row cue paints OVER the ramp, so it lands after Interactive (which writes the brushes).
        if (spec.DropCue is { } cue)
        {
            ColorF rest = NavRow(selected || spec.MultiSelected).Fill.Resting(enabled);
            row = row with
            {
                Fill = Prop.Of(() => cue.Active() ? cue.Fill() : rest),
                BorderWidth = cue.BorderWidth,
                BorderColor = Prop.Of(() => cue.Active() ? cue.Border() : ColorF.Transparent),
            };
        }
        if (spec.MenuOverlay is { } svc && spec.Menu is { } factory) row = row.WithContextMenu(svc, factory);
        return row;
    }

    /// <summary>S8: <paramref name="row"/> wearing its full <paramref name="label"/> as a tooltip when the label would truncate in a
    /// lane <paramref name="laneWidth"/> wide (a pane, a flyout panel) at <paramref name="depth"/> with <paramref name="trailing"/>
    /// DIP of trailing content (<see cref="SidebarGeometry.TrailingWidth"/>); the row itself otherwise. The plan rows decide this
    /// in their slot (<c>ClassicNavRow.Tipped</c>); the flyouts' rows, built outside a slot, use this.</summary>
    public static Element TipIfCut(Element row, string label, float laneWidth, int depth, float trailing)
        => SidebarLabelFit.Overflows(label, SidebarGeometry.LabelWidth(laneWidth, depth, trailing))
            ? ToolTip.Wrap(row, label, grow: 1f)
            : row;

    /// <summary>Names a TRACK row's activation ("Play track"): a tooltip is the only non-visual name channel.
    /// <c>grow: 1f</c> because ToolTip.Wrap's wrapper is a flex ROW — without it the wrapped row's plate shrank to
    /// its own title, visibly narrower than the rows around it.</summary>
    public static Element WithPlayTrackHint(Element row)
        => ToolTip.Wrap(row, Loc.Get(Strings.Sidebar.Item.PlayTrack), grow: 1f);

    /// <summary>F2 rename · Alt+↑/↓ move · Enter activate · Space toggle into the selection · Escape clear. ONE handler:
    /// InputDispatcher routes keys from the focused node upward and a row can only have one. Every arm is inert when
    /// its command is absent.</summary>
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
            case Keys.Enter when activate is not null:
                e.Handled = true; activate(KeyModifiers.None); return;
            case Keys.Space when activate is not null && !e.IsRepeat:
                e.Handled = true; activate(e.Mods | KeyModifiers.Ctrl); return;
            case Keys.Escape when escape is not null:
                e.Handled = true; escape(); return;
        }
        // EXACTLY Alt: Alt+Shift+↑ belongs to whatever claims it next.
        if (move is null || e.Mods != KeyModifiers.Alt) return;
        if (e.KeyCode == Keys.Up) { e.Handled = true; move(-1); }
        else if (e.KeyCode == Keys.Down) { e.Handled = true; move(1); }
    };

    /// <summary>S5: the pin as a real 24-px "Unpin" button (board `C-Sidebar` `s.pin`: 24 box, r4, the 12 pin glyph, tertiary) with its
    /// tooltip; it drops the pin on click and never arms the row's drag. Without <paramref name="unpin"/> (no pin store) the same box is a
    /// quiet mark, so the row's geometry does not change with the store.</summary>
    static Element UnpinButton(Action? unpin, bool enabled)
    {
        Element glyph = Icon(Icons.Pin, SidebarGeometry.PinGlyph, Tok.TextTertiary);
        if (unpin is null || !enabled)
            return new BoxEl
            {
                Width = SidebarGeometry.PinButton, Height = SidebarGeometry.PinButton, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [glyph],
            };
        var button = new BoxEl
        {
            Width = SidebarGeometry.PinButton, Height = SidebarGeometry.PinButton, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, BlocksDragArm = true,
            OnClick = unpin,
            Children = [glyph],
        }.Interactive(Interaction.Subtle);
        return ToolTip.Wrap(button, Loc.Get(Strings.Sidebar.Pin.UnpinShort));
    }

    /// <summary>The hover "…" parked against the row's trailing edge (JustifySelf End) inside the row's ZStack,
    /// <paramref name="trailingInset"/> short of it on a folder so it clears the chevron column. ClickRequestsContext
    /// re-enters the context-request funnel, so the button and a right-click open the SAME menu, anchored at the
    /// button.</summary>
    static Element OverflowButton(float trailingInset) => new BoxEl
    {
        Width = SidebarGeometry.OverflowButton, Height = SidebarGeometry.OverflowButton, JustifySelf = FlexAlign.End, AlignSelf = FlexAlign.Center,
        Margin = new Edges4(0f, 0f, trailingInset, 0f),
        Opacity = 0f, HoverOpacity = 1f,
        Children =
        [
            new BoxEl
            {
                Width = SidebarGeometry.OverflowButton, Height = SidebarGeometry.OverflowButton, AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center, Corners = CornerRadius4.All(SidebarGeometry.OverflowButtonCorner),
                HoverFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                ClickRequestsContext = true,
                Children = [Icon(Icons.More, SidebarGeometry.OverflowGlyph, Tok.TextSecondary)],
            },
        ],
    };

    /// <summary>A track row's cover with a hover scrim + play glyph. The reveal is HoverOpacity on a DESCENDANT, which
    /// the engine resolves against the ROW's hover. A scrim is dark in BOTH themes, so the glyph is literal white
    /// (never TextOnAccentPrimary, which is black in dark).</summary>
    static Element TrackArt(Element cover, float art, SidebarRowShape shape) => ZStack(
        cover,
        new BoxEl
        {
            Width = art, Height = art, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Sidebar.Cover.Radius(art, circular: false)),
            Fill = SidebarGeometry.TrackScrim,
            Opacity = 0f, HoverOpacity = 1f, HitTestVisible = false,
            Children = [Icon(Icons.Play, shape == SidebarRowShape.EntityOneLine ? SidebarGeometry.TrackPlayGlyphCompact : SidebarGeometry.TrackPlayGlyph, SidebarGeometry.TrackPlayInk)],
        }) with { Width = art, Height = art, Shrink = 0f };
}

