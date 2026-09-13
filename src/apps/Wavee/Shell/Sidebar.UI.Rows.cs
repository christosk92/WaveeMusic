// ── Shell/Sidebar.UI.Rows.cs ───────────────────────────────────────────────────────────────────────────────────────
// the sidebar row primitives: the one entity row, covers, the rotating chevron, the selection pill, quiet counts,
// skeletons, the section header, the pin drop zone + the "+" create button, the customize-canvas section card, the
// EntityList inline controls + search head, and the pane's text / loc / glyph tables
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 1700 lines
// Spec: ch 25 §0, §2 (W1, W1b, W2, W4, W9, W10, W17, W18, W19), §3, §4, §5, §6, §9
//
// NAMED PARTIAL of Sidebar.UI.cs (J1): the row / cover / chrome primitives every plan row, rail tile and mode builds from.
//
// Everything here is either a PURE STATIC over a value (a Component per row would cost a mount per slot in a 10k
// virtualized list) or a small Component that owns exactly the hooks a static cannot: a node ref + an animation seed
// (Chevron, SelectionPill), a drag-state subscription (PinDropZone), an anchor + overlay handle (CreateButton, the sort
// trigger, the card's options button). Props freeze at mount, so every changing input is a Func probe, never a value.

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
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    // ══ 1. THE ONE ENTITY ROW ════════════════════════════════════════════════════════════════════════════════════════
    //
    // Classic's pinned rows, Library V3's list rows and Curated's entity rows all come out of EntityRow.Create, so the
    // three designs cannot drift apart. It owns the 4-state selection-aware ramp (a selected row DARKENS on hover — the
    // NavigationViewItem backplate ladder), the height ladder, the 3-DIP pill gutter, the depth indent, the slot order,
    // the one key handler and the drag source.

    /// <summary>Everything <see cref="EntityRow.Create"/> needs: a mutable struct filled with an object initializer and
    /// passed by <c>in</c>, so a row costs no allocation beyond its elements. Always <c>new RowSpec { … }</c>, never
    /// <c>default</c> — the defaults below apply only through the constructor.</summary>
    internal struct RowSpec
    {
        public RowSpec()
        {
            Key = ""; Label = ""; Subtitle = null; Selected = false; Enabled = true; Depth = 0; TreeNode = false;
            TreeDepth = 0; TreeContinuationMask = 0; Density = SidebarDensity.Cozy; Gap = float.NaN; Height = float.NaN;
            ArtSize = float.NaN; Leading = null; Glyph = null; DisclosureChevron = null; Pinned = false; Trailing = null;
            Playing = false; PlayingAnimated = false; Track = false; Overflow = false; OnClick = null; OnRealized = null;
            MenuOverlay = null; Menu = null; Drag = null; DropTarget = null; DropActive = null; Animate = null;
            Caption = null; CheckLane = null; MultiSelected = false; ChecksVisible = null; Focusable = false;
            OnRename = null; OnMove = null; OnActivate = null; OnEscape = null;
        }

        // ── identity ──
        /// <summary>REQUIRED. The reconciler key and the pill-registration key: stable per item, never an index.</summary>
        public string Key;
        /// <summary>REQUIRED. The title — also the accessible name (the engine has no separate automation-name channel).</summary>
        public string Label;
        /// <summary>Second line (count / kind · creator). Ignored at Compact.</summary>
        public string? Subtitle;

        // ── state ──
        /// <summary>The open ROUTE is this row: drives the accent plate of the 4-state ramp.</summary>
        public bool Selected;
        /// <summary>False dims to 0.55 and drops the ramp (missing entity / unavailable action retention).</summary>
        public bool Enabled;
        /// <summary>Nesting depth: 12 DIP per level, clamped at 4.</summary>
        public int Depth;
        /// <summary>A PlaylistTree row: its depth is drawn as connector cells instead of anonymous padding.</summary>
        public bool TreeNode;
        /// <summary>Depth inside the tree itself (separate from <see cref="Depth"/>).</summary>
        public int TreeDepth;
        /// <summary>One bit per tree level: set ⇒ that level has a later sibling and its vertical guide continues.</summary>
        public byte TreeContinuationMask;
        /// <summary>Row height + art size ladder.</summary>
        public SidebarDensity Density;
        /// <summary>Main-axis gap. NaN ⇒ <c>SidebarRowGeometry.LeadingGap</c> (6) for every row shape.</summary>
        public float Gap;
        /// <summary>PIN the height (NaN ⇒ derive). A section pins it so a Reorderable pitch and the extent table see ONE
        /// height per section.</summary>
        public float Height;
        /// <summary>Art-slot edge. NaN ⇒ <c>SidebarRowGeometry.ArtFor(Density)</c>.</summary>
        public float ArtSize;

        // ── slots ──
        /// <summary>The leading visual (build it with <see cref="Cover"/>). Null falls back to <see cref="Glyph"/>.</summary>
        public Element? Leading;
        /// <summary>A 16-DIP glyph centred in an art-wide column when <see cref="Leading"/> is null.</summary>
        public string? Glyph;
        /// <summary>A folder's <see cref="Chevron.Disclosure"/> — the FIRST trailing element, never in the leading lane.</summary>
        public Element? DisclosureChevron;
        /// <summary>A pinned library entry (#85): a quiet 12-DIP pin glyph beside the count. Never on a track.</summary>
        public bool Pinned;
        /// <summary>Trailing content (a count badge, a "+").</summary>
        public Element? Trailing;
        /// <summary>This row's context is the playing one: the 12-DIP accent equalizer.</summary>
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
        /// <summary>WHOLE-ROW cue for surfaces rebuilt per open (the rail folder flyout). A plan row does NOT use this —
        /// its plate is the slot's own always-mounted DropPlate, because a thunk here is wired at mount and would answer
        /// for the row this slot first mounted with.</summary>
        public Func<bool>? DropActive;
        /// <summary>Layout transition. Null inside a Reorderable (it owns position — one transform owner per node).</summary>
        public LayoutTransition? Animate;
        /// <summary>A third text line (11 px tertiary). Nothing sets it today; the shape is free.</summary>
        public string? Caption;
        /// <summary>The multi-select lane (<c>SelectorVisualsBound.BoundCheckLane</c>), placed inside the leading cluster.</summary>
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

    internal static class EntityRow
    {
        /// <summary>The hover "…" box — also the trailing cluster's reserve, so a chevron or a count never sits under it.</summary>
        const float OverflowWidth = 26f;

        /// <summary>The height <paramref name="spec"/> renders at, for a host that sizes a slot before building the row.</summary>
        public static float HeightOf(in RowSpec spec)
            => float.IsNaN(spec.Height)
                ? SidebarRowGeometry.HeightFor(spec.Density, SidebarRowGeometry.SubtitleVisible(spec.Density, spec.Subtitle))
                : spec.Height;

        /// <summary>The 3-DIP reserve the selection pill sits in; exists so row content never shifts as selection moves.</summary>
        public static Element SelGutter() => new BoxEl { Width = SidebarRowGeometry.SelGutterWidth, Shrink = 0f };

        /// <summary>Build the row. Returns a <see cref="BoxEl"/> so a caller can still <c>with</c> layout fields (Grow for a
        /// Reorderable wrapper); the fill ramp must never be overridden downstream.</summary>
        public static BoxEl Create(in RowSpec spec)
        {
            bool selected = spec.Selected;
            bool enabled = spec.Enabled;
            bool hasSubtitle = SidebarRowGeometry.SubtitleVisible(spec.Density, spec.Subtitle);
            float height = float.IsNaN(spec.Height) ? SidebarRowGeometry.HeightFor(spec.Density, hasSubtitle) : spec.Height;
            float art = float.IsNaN(spec.ArtSize) ? SidebarRowGeometry.ArtFor(spec.Density) : spec.ArtSize;
            bool bareGlyph = spec.Leading is null && spec.Glyph is { Length: > 0 };
            float gap = float.IsNaN(spec.Gap) ? SidebarRowGeometry.LeadingGap : spec.Gap;
            // Copies: an `in` parameter cannot be captured by the bound thunks / handlers below.
            var plateOn = spec.DropActive;
            var activate = spec.OnActivate;
            var checksVisible = spec.ChecksVisible;
            // TWO selections, two skins: the open ROUTE takes the accent plate (+ the pill), a tree MULTI-selection the
            // quiet one. Re-asserted as a value on every reconcile, so it re-enters the brush cross-fade for free.
            ColorF rest = !enabled ? ColorF.Transparent
                : selected ? global::Wavee.Design.Colors.SelectedRest
                : spec.MultiSelected ? Tok.FillSubtleSecondary
                : ColorF.Transparent;
            ColorF hoverFill = !enabled ? ColorF.Transparent
                : selected ? global::Wavee.Design.Colors.SelectedHover : Tok.FillSubtleSecondary;

            // ── leading column: a glyph row builds the SAME art-wide column an art row does, so labels align (W7) ──
            Element leading;
            if (spec.Leading is { } given) leading = given;
            else if (bareGlyph)
                leading = new BoxEl
                {
                    Width = art, Height = art, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Icon(spec.Glyph!, 16f, selected ? Tok.TextPrimary : Tok.TextSecondary)],
                };
            else leading = new BoxEl { Width = art, Height = art, Shrink = 0f };
            if (spec.Track && spec.Leading is not null) leading = TrackArt(leading, art, spec.Density);

            // ── text column: Shrink + MinWidth 0 on both arms, or a long title runs under the trailing cluster ──
            Element text;
            if (!hasSubtitle && spec.Caption is null)
            {
                text = Body(spec.Label) with
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                };
            }
            else
            {
                bool hasCaption = spec.Caption is { Length: > 0 };
                var stack = new Element[1 + (hasSubtitle ? 1 : 0) + (hasCaption ? 1 : 0)];
                int n = 0;
                stack[n++] = Body(spec.Label) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 };
                if (hasSubtitle)
                    stack[n++] = Caption(spec.Subtitle!).Secondary() with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 };
                if (hasCaption)
                    stack[n] = new TextEl(spec.Caption!)
                    {
                        Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    };
                text = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 1f, Children = stack };
            }

            // ── trailing cluster: chevron · equalizer · pin · trailing, ONE grouped element ──
            bool overflow = spec.Overflow && enabled && spec.MenuOverlay is not null && spec.Menu is not null;
            Element? trailingCluster = null;
            int trailingCount = (spec.DisclosureChevron is null ? 0 : 1) + (spec.Playing ? 1 : 0)
                              + (spec.Pinned ? 1 : 0) + (spec.Trailing is null ? 0 : 1);
            if (trailingCount > 0)
            {
                var parts = new Element[trailingCount];
                int t = 0;
                if (spec.DisclosureChevron is { } chevron) parts[t++] = chevron;
                if (spec.Playing) parts[t++] = Controls.Equalizer(spec.PlayingAnimated, Tok.AccentDefault, 12f);
                if (spec.Pinned) parts[t++] = Icon(Icons.Pin, 12f, Tok.TextTertiary);
                if (spec.Trailing is { } trailingContent) parts[t] = trailingContent;
                trailingCluster = new BoxEl
                {
                    Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = gap,
                    // Reserve the overlay's width only when the row actually carries the "…".
                    Padding = overflow ? new Edges4(0f, 0f, OverflowWidth, 0f) : default,
                    Children = parts,
                };
            }

            Element leadingCluster = spec.TreeNode
                ? TreeLeading(leading, spec.TreeDepth, spec.TreeContinuationMask, height)
                : StandardLeading(leading, gap);
            // The check lane rides INSIDE the leading cluster in a gapless box: as a gapped sibling its hidden Flow.Show
            // boundary still cost one row gap, pushing every rootlist row right of the album/artist rows beside it.
            if (spec.CheckLane is { } checkLane)
                leadingCluster = new BoxEl
                {
                    Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [checkLane, leadingCluster],
                };

            var kids = new Element[trailingCluster is null ? 2 : 3];
            kids[0] = leadingCluster;
            kids[1] = text;
            if (trailingCluster is not null) kids[2] = trailingCluster;

            // The "…" is a ZSTACK OVERLAY, never a flex sibling: it costs the title ZERO width, so a title never ellipsizes
            // early and hovering never re-trims it. A long title may run under it — accepted: the row is translucent over
            // Mica, there is no opaque tone to fade into, and a hover-conditional width would relayout on every enter.
            Element[] rowChildren = overflow
                ? [new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = gap, Children = kids }, OverflowButton()]
                : kids;

            var row = new BoxEl
            {
                Key = spec.Key,
                Animate = spec.Animate,
                OnRealized = spec.OnRealized,
                ZStack = overflow,
                Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = gap,
                Padding = new Edges4(SidebarRowGeometry.IndentFor(spec.Depth), 0f, SidebarRowGeometry.RowInsetRight, 0f),
                Corners = CornerRadius4.All(4f),
                // THE 4-STATE SELECTION-AWARE RAMP, written once for every row in every design. An armed whole-row drop
                // reads as a lit accent PLATE (bound — this runs while a drag is live, inside the 0-alloc region).
                Fill = plateOn is null ? rest : Prop.Of(() => plateOn() ? Tok.AccentDefault with { A = 0.18f } : rest),
                HoverFill = hoverFill,
                PressedFill = !enabled ? ColorF.Transparent
                    : selected ? global::Wavee.Design.Colors.SelectedPressed : Tok.FillSubtleTertiary,
                BorderWidth = plateOn is null ? 0f : 1f,
                BorderColor = plateOn is null
                    ? ColorF.Transparent
                    : Prop.Of(() => plateOn() ? Tok.AccentDefault : ColorF.Transparent),
                Opacity = enabled ? 1f : 0.55f,
                IsEnabled = enabled,
                // A row with OnActivate wires the POINTER-RELEASED path: OnClick throws the modifiers away, and Ctrl/Shift
                // ARE the gesture. A double click always activates plainly (WinUI's DoubleTap rule); a single tap while
                // the check lane is up gets Ctrl synthesized.
                OnClick = enabled && activate is null ? spec.OnClick : null,
                OnPointerReleased = enabled && activate is not null
                    ? args => activate!(args.ClickCount >= 2
                        ? KeyModifiers.None
                        : SelectorVisualsBound.MultiSelectMods(checksVisible?.Invoke() ?? false, args.Mods))
                    : null,
                Focusable = spec.Focusable
                            || (enabled && (spec.OnRename is not null || spec.OnMove is not null || activate is not null)),
                OnKeyDown = enabled && (spec.OnRename is not null || spec.OnMove is not null
                                        || activate is not null || spec.OnEscape is not null)
                    ? KeyHandler(spec.OnRename, spec.OnMove, activate, spec.OnEscape)
                    : null,
                // The RESOURCE drag, click-primary: navigating is the constant intent, so a click landed while the mouse
                // is still travelling is not eaten by a promotion. A row in a reorderable band carries no Drag at all.
                Draggable = enabled && spec.Drag is { } payload ? Drag.Source(() => payload, clickPrimary: true) : null,
                DropTarget = spec.DropTarget,
                Children = rowChildren,
            };

            // Right-click / Menu key / long-press — the attach CHAINS onto OnRealized, never clobbering the pill capture.
            if (spec.MenuOverlay is { } svc && spec.Menu is { } factory) row = row.WithContextMenu(svc, factory);
            return row;
        }

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

        static Element StandardLeading(Element leading, float gap) => new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = gap, AlignItems = FlexAlign.Center,
            Children = [SelGutter(), leading],
        };

        /// <summary>A tree row: the gutter and depth's connector cells butted together (SidebarRowGeometry.TreeContentX is
        /// exactly that sum — a gap there would put the caret one gap short of what the row draws), then one LeadingGap.
        /// Identical to <see cref="StandardLeading"/> at depth 0.</summary>
        static Element TreeLeading(Element leading, int depth, byte continuationMask, float height)
        {
            int levels = Math.Clamp(depth, 0, SidebarRowGeometry.MaxIndentDepth);
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

        /// <summary>One 12-DIP cell per level: a 1-DIP guide (half height on the current level when it has no later
        /// sibling) and, on the current level, an 8-DIP elbow at mid height.</summary>
        static Element TreeGuides(int depth, byte continuationMask, float height)
        {
            var cells = new Element[depth];
            for (int level = 1; level <= depth; level++)
            {
                bool current = level == depth;
                bool continues = (continuationMask & (1 << (level - 1))) != 0;
                bool guide = current || continues;
                var marks = new Element[(guide ? 1 : 0) + (current ? 1 : 0)];
                int m = 0;
                if (guide)
                    marks[m++] = new BoxEl
                    {
                        Width = 1f, Height = current && !continues ? height / 2f : height, Shrink = 0f,
                        AlignSelf = FlexAlign.Start, Margin = new Edges4(Spacing.XS, 0f, 0f, 0f),
                        Fill = Tok.StrokeDividerDefault,
                    };
                if (current)
                    marks[m] = new BoxEl
                    {
                        Width = Spacing.S, Height = 1f, Shrink = 0f, AlignSelf = FlexAlign.Start,
                        Margin = new Edges4(Spacing.XS, height / 2f, 0f, 0f), Fill = Tok.StrokeDividerDefault,
                    };
                cells[level - 1] = new BoxEl
                {
                    Width = SidebarRowGeometry.TreeGuideStep, Height = height, Shrink = 0f, ZStack = true,
                    HitTestPassThrough = true, Children = marks,
                };
            }
            return new BoxEl
            {
                Direction = 0, Width = depth * SidebarRowGeometry.TreeGuideStep, Height = height, Shrink = 0f,
                HitTestPassThrough = true, Children = cells,
            };
        }

        /// <summary>The hover "…" parked flush against the row's trailing padding (JustifySelf End) inside the row's
        /// ZStack. ClickRequestsContext re-enters the context-request funnel, so the button and a right-click open the SAME
        /// menu, anchored at the button.</summary>
        static Element OverflowButton() => new BoxEl
        {
            Width = OverflowWidth, Height = OverflowWidth, JustifySelf = FlexAlign.End, AlignSelf = FlexAlign.Center,
            Opacity = 0f, HoverOpacity = 1f,
            Children =
            [
                new BoxEl
                {
                    Width = OverflowWidth, Height = OverflowWidth, AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Center, Corners = CornerRadius4.All(13f),
                    HoverFill = Tok.FillSubtleTertiary,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand,
                    ClickRequestsContext = true,
                    Children = [Icon(Icons.More, 14f, Tok.TextSecondary)],
                },
            ],
        };

        /// <summary>A track row's cover with a hover scrim + play glyph. The reveal is HoverOpacity on a DESCENDANT, which
        /// the engine resolves against the ROW's hover. A scrim is dark in BOTH themes, so the glyph is literal white
        /// (never TextOnAccentPrimary, which is black in dark).</summary>
        static Element TrackArt(Element cover, float art, SidebarDensity density) => ZStack(
            cover,
            new BoxEl
            {
                Width = art, Height = art, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(Cover.Radius(art, circular: false)),
                Fill = new ColorF(0f, 0f, 0f, 0.55f),
                Opacity = 0f, HoverOpacity = 1f, HitTestVisible = false,
                Children = [Icon(Icons.Play, density == SidebarDensity.Compact ? 10f : 14f, ColorF.FromRgba(0xFF, 0xFF, 0xFF))],
            }) with { Width = art, Height = art, Shrink = 0f };
    }

    // ══ 2. COVERS ════════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // The ONE leading-visual factory for every row, tile and card: the radius ladder, circular-for-artists, the folder /
    // route glyph tiles, the mosaic hand-off and the six canonical sizes. The image pipeline itself (decode, placeholder
    // tint, shimmer) is Controls.Artwork's — a sidebar cover and a grid card share one decode cache.

    internal static class Cover
    {
        /// <summary>The six canonical sidebar art sizes: caption 20 · compact 28 · cozy 32 · comfortable / rail 40 ·
        /// compact grid 48 · grid / spotlight 64.</summary>
        public const float S20 = 20f, S28 = 28f, S32 = 32f, S40 = 40f, S48 = 48f, S64 = 64f;

        /// <summary>4 (≤28) → 6 (≤40) → 8; circular is always half the size.</summary>
        public static float Radius(float size, bool circular)
            => circular ? size * 0.5f : size <= 28f ? 4f : size <= 40f ? 6f : 8f;

        /// <summary>The glyph inside a tile: 16 from 28 up, else 12.</summary>
        public static float GlyphSize(float size) => size >= 28f ? 16f : 12f;

        /// <summary>Bucketed decode: without it a thumb decodes at its LAYOUT size (blurry on any >1x display), and the
        /// buckets make a 36-DIP rail tile and a 32-DIP row share one cache entry.</summary>
        static int DecodeBucket(float size) => size <= 32f ? 64 : size <= 64f ? 128 : 256;

        /// <summary>The art slot for a projected entry — the ONE call a surface makes: liked collection → its cover ·
        /// folder → folder tile · app route → the route's own glyph tile · artist / Circular → circle · else cover art.</summary>
        public static Element ForEntry(in SidebarLibraryEntry e, float size)
        {
            if (IsLiked(e.Id, e.Uri)) return Liked(size);
            return e.Kind switch
            {
                SidebarEntryKind.Folder => Folder(size),
                SidebarEntryKind.AppRoute => RouteGlyph(e.Id, size),
                _ => Art(e.Cover, e.MosaicTiles, e.Id, size, e.Circular || e.Kind == SidebarEntryKind.Artist),
            };
        }

        /// <summary>The two spellings of the liked collection: the "liked" ROUTE pin id, or the collection uri.</summary>
        static bool IsLiked(string? id, string? uri)
            => string.Equals(id, "liked", StringComparison.Ordinal)
               || (uri is { Length: > 0 } && EntityUri.IsLikedCollection(uri));

        /// <summary>Liked Songs at sidebar scale: a 2×2 mosaic of the newest likes' covers, one cover when fewer than four
        /// distinct ones exist, a heart tile when the library has none yet. The one place the "no component per art slot"
        /// rule bends — a sidebar shows Liked at most twice — and it is wrapped in the same hard-sized clipped box every
        /// other arm returns, or a Liked row measures taller than its section (one height per section).</summary>
        public static Element Liked(float size)
        {
            float r = Radius(size, false);
            return new BoxEl
            {
                Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(r), ClipToBounds = true,
                // Keyed by geometry: the component freezes its size at mount.
                Children = [Embed.Comp(() => new LikedArt(size, r)) with { Key = "liked-cover:" + FormatCache.Int((int)size) }],
            };
        }

        /// <summary>Cover art: the image when there is one; for a cover-less entry a 2×2 mosaic of ≥4 tiles, else its first
        /// tile; else the neutral placeholder tile. <paramref name="seedKey"/> is the entity's stable id — never an index —
        /// so a re-sorted list keeps each row's identity (0.2.9's seed was likewise inert: the tint comes from the url's
        /// graded colour, and a url-less slot takes the opaque neutral tile).</summary>
        public static Element Art(StringId cover, IReadOnlyList<StringId>? mosaicTiles, string seedKey, float size,
                                  bool circular = false)
        {
            float radius = Radius(size, circular);
            string? url = Controls.ArtUrl(cover);
            if (url is null && mosaicTiles is { Count: > 0 } tiles)
            {
                if (tiles.Count >= 4
                    && Controls.ArtUrl(tiles[0]) is { } t0 && Controls.ArtUrl(tiles[1]) is { } t1
                    && Controls.ArtUrl(tiles[2]) is { } t2 && Controls.ArtUrl(tiles[3]) is { } t3)
                    return Controls.Mosaic(new[] { t0, t1, t2, t3 }, size, size, radius);
                url = Controls.ArtUrl(tiles[0]);
            }
            return Controls.Artwork(url, size, size, radius, decodePx: DecodeBucket(size));
        }

        /// <summary>A hand-placed item's retained art (<c>FallbackImageUrl</c>), or the placeholder tile when it has none.</summary>
        public static Element ArtUrl(string? url, string seedKey, float size, bool circular = false)
            => Controls.Artwork(url is { Length: > 0 } ? url : null, size, size, Radius(size, circular),
                                decodePx: DecodeBucket(size));

        /// <summary>A neutral tile carrying a glyph — what a folder / route / unavailable entity wears, so the leading
        /// column is always the same width whatever it holds.</summary>
        public static Element Glyph(string glyph, float size, bool circular = false, ColorF? color = null)
            => new BoxEl
            {
                Width = size, Height = size, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(Radius(size, circular)),
                Fill = Tok.FillSubtleSecondary,
                Children = [Icon(glyph, GlyphSize(size), color ?? Tok.TextSecondary)],
            };

        /// <summary>A playlist folder; <paramref name="expanded"/> swaps to the open-folder mark (reads as open even where
        /// the chevron is clipped away).</summary>
        public static Element Folder(float size, bool expanded = false)
            => Glyph(expanded ? Icons.FolderOpen : Icons.Folder, size);

        /// <summary>An app-route tile wearing the route's own glyph (a pinned Liked shows a heart, not a square).</summary>
        public static Element RouteGlyph(string routeKey, float size)
            => Glyph(Shell.Dest(Shell.Parse(routeKey)).Glyph, size);

        /// <summary>The stable 31-hash seed for a key (the same hash every landed sidebar row used).</summary>
        public static int SeedFrom(string key)
        {
            int h = 17;
            foreach (char c in key) h = h * 31 + c;
            return h & 0x7fffffff;
        }

        /// <summary>The liked-collection composition. Subscribes to the liked relation and the track table, so a new like
        /// or a landed cover re-composes it.</summary>
        sealed class LikedArt : Component
        {
            readonly float _size, _radius;

            public LikedArt(float size, float radius) { _size = size; _radius = radius; }

            public override Element Render()
            {
                var scope = Entities.Current;
                if (scope is null) return Glyph(Icons.Heart, _size);
                _ = scope.Edges.Liked.Changed.Value;
                _ = scope.Tracks.Changed.Value;

                var slots = User.Me.LikedTrackSlots;
                string? a = null, b = null, c = null, d = null;
                int found = 0;
                // Newest first; distinct covers only (four likes off one album are one cover, not a mosaic).
                for (int i = 0; i < slots.Length && found < 4 && i < 64; i++)
                {
                    string? url = Controls.ArtUrl(new Track(slots[i]).ImageId);
                    if (url is null || url == a || url == b || url == c) continue;
                    switch (found++)
                    {
                        case 0: a = url; break;
                        case 1: b = url; break;
                        case 2: c = url; break;
                        default: d = url; break;
                    }
                }
                if (found == 4) return Controls.Mosaic(new[] { a!, b!, c!, d! }, _size, _size, _radius);
                if (found > 0) return Controls.Artwork(a, _size, _size, _radius, decodePx: DecodeBucket(_size));
                return Glyph(Icons.Heart, _size);
            }
        }
    }

    // ══ 3. THE CHEVRON AND THE SELECTION PILL ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE one disclosure chevron: ONE glyph whose Rotation rides <c>MotionTokenId.DisclosureChevron</c> (167 ms,
    /// cubic-bezier(.167,.167,0,1)) through <c>SeedValue</c>, so the token owns the dynamics AND the reduced-motion policy.
    /// Never a glyph swap — a swap teleports while the section beside it animates.
    ///
    /// <para>A Component because the rotation needs a node ref and an edge-triggered effect — hooks a recycling slot must
    /// not grow conditionally. <c>open</c> is a Func invoked inside THIS render, so its signal reads subscribe this 10-DIP
    /// component, never the slot.</para>
    ///
    /// <para>A first mount SEEDS the resting angle with no motion. <c>identity</c> (optional) extends that to a RECYCLE:
    /// when the probe's value changes (the slot's index, a section id hash) the new angle is seeded, never animated — a
    /// row scrolled onto a different section must not visibly spin.</para>
    /// </summary>
    internal sealed class Chevron : Component
    {
        readonly Func<bool> _open;
        readonly Func<int>? _identity;
        readonly string _glyph;
        readonly float _size, _openDeg;

        Chevron(Func<bool> open, Func<int>? identity, string glyph, float size, float openDeg)
        {
            _open = open; _identity = identity; _glyph = glyph; _size = size; _openDeg = openDeg;
        }

        /// <summary>A SECTION header's chevron: ChevronDown at rest, rotated 180° when the section is open.</summary>
        public static Element Section(Func<bool> open, float size = 10f, Func<int>? identity = null)
            => Embed.Comp(() => new Chevron(open, identity, Icons.ChevronDown, size, 180f));

        /// <summary>A DISCLOSURE chevron (a folder row / tree row): ChevronRight at rest, rotated 90° when expanded.</summary>
        public static Element Disclosure(Func<bool> open, float size = 10f, Func<int>? identity = null)
            => Embed.Comp(() => new Chevron(open, identity, Icons.ChevronRight, size, 90f));

        public override Element Render()
        {
            bool open = _open();
            float target = open ? _openDeg : 0f;
            int identity = _identity?.Invoke() ?? 0;

            var node = UseRef<NodeHandle>(default);
            var seeded = UseRef(false);
            var seededIdentity = UseRef(0);

            UseEffect(() =>
            {
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null || node.Value.IsNull || !scene.IsLive(node.Value)) return;
                if (!seeded.Value || seededIdentity.Value != identity)
                {
                    seeded.Value = true;
                    seededIdentity.Value = identity;
                    anim.SeedValue(node.Value, AnimChannel.Rotation, target, MotionTokenId.DisclosureChevron, from: target);
                    return;
                }
                // A mid-flight toggle retargets from the LIVE angle instead of restarting.
                anim.SeedValue(node.Value, AnimChannel.Rotation, target, MotionTokenId.DisclosureChevron);
            }, DepKey.From(target, identity));

            return new BoxEl
            {
                Width = _size, Height = _size, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                OnRealized = h => node.Value = h,
                Children = [Icon(_glyph, _size, Tok.TextTertiary)],
            };
        }
    }

    /// <summary>
    /// One NavigationViewItem SelectionIndicator (3×16, radius 1.5, accent) permanently owned by every selectable realized
    /// row. The pane runs the paired 600 ms route flight on the TRANSFORM; a recycle is never navigation, so it snaps.
    ///
    /// <para><b>Opacity is BOUND, never authored</b> (#22/#23 — two pills lit at once): one thunk for the node's whole life
    /// reads the slot's LIVE <see cref="SidebarPillState"/>, so anything that wrote the node's opacity (a force-completed
    /// flight, a registration pointing at a recycled slot) is re-derived on the row's own epoch and can never stick.</para>
    /// </summary>
    internal sealed class SelectionPill : Component
    {
        public const float PillW = 3f, PillH = 16f;

        readonly PaneView _owner;
        readonly Func<SidebarPillState> _state;
        readonly Prop<float> _opacity;
        NodeHandle _self;
        string? _route;

        public SelectionPill(PaneView owner, Func<SidebarPillState> state)
        {
            _owner = owner;
            _state = state;
            _opacity = Prop.Of(() => _state().Opacity);
        }

        public override Element Render()
        {
            var state = _state();
            bool selected = state.Selected;
            bool recycled = _route is not null && !string.Equals(_route, state.Route, StringComparison.Ordinal);
            int routeHash = state.Route is null ? 0 : StringComparer.Ordinal.GetHashCode(state.Route);
            UseLayoutEffect(() =>
            {
                _route = state.Route;
                var anim = Context.Anim;
                if (anim is null || _self.IsNull || state.Route is not { Length: > 0 } route) return;
                _owner.RegisterSelectionPill(route, _self);
                // A recycled node may inherit an interrupted transform from the route previously bound to the slot; snap
                // to the same visibility the bound channel reads, so the snap can never disagree with it.
                if (recycled) NavigationSelectionMotion.SnapVertical(anim, _self, selected);
            }, DepKey.From(routeHash));

            return new BoxEl
            {
                Width = PillW,
                Height = PillH,
                Margin = new Edges4(state.Indent, state.Top, 0f, 0f),
                Corners = CornerRadius4.All(PillW * 0.5f),
                Fill = Tok.AccentDefault,
                Opacity = _opacity,
                // NavigationSelectionMotion expresses WinUI's CenterPoint swap as painted-top coordinates: a top-edge
                // origin is part of the primitive's geometry contract.
                TransformOriginY = 0f,
                HitTestVisible = false,
                OnRealized = h => _self = h,
            };
        }
    }

    // ══ 4. COUNTS, SKELETONS, THE SECTION HEADER ═════════════════════════════════════════════════════════════════════

    /// <summary>THE one count renderer. A library shortcut's count is ambient, not a notification: an 11-DIP tertiary
    /// number, never WinUI's accent InfoBadge pill (five of those read as five alerts).</summary>
    internal static class Counts
    {
        /// <summary>The pending plate (20×12) — narrower and shorter than a badge, so it reads "a number is coming".</summary>
        public const float PlateW = 20f, PlateH = 12f;

        /// <summary>The number, or the pending plate while <paramref name="count"/> is unknown.</summary>
        public static Element Badge(int? count) => count is { } n ? Number(n) : Pending();

        public static Element Number(int count) => new TextEl(FormatCache.Int(count))
        {
            Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f,
        };

        public static Element Pending() => new BoxEl
        {
            Width = PlateW, Height = PlateH, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
        };
    }

    /// <summary>The explicit shimmer shapes (a streaming list has no seed rows to derive a skeleton from). Sized by the
    /// same ladders the real row uses, so the shimmer→content swap never changes a section's height.</summary>
    internal static class Skeletons
    {
        /// <summary>The rail's tile box (= the Rail file's <c>Box</c>, 40).</summary>
        const float RailTileBox = 40f;

        /// <summary>One pending row: pad (9,0,8,0), gap 10, a cover-ladder tile, bars 140×12 (+ 80×10 at gap 4), r4.
        /// Uniform by design (<paramref name="index"/> is position-independent).</summary>
        public static Element Row(int index, SidebarDensity density, bool subtitle, float heightOverride = float.NaN,
                                  float artOverride = float.NaN)
        {
            float height = float.IsNaN(heightOverride) ? SidebarRowGeometry.HeightFor(density, subtitle) : heightOverride;
            float art = float.IsNaN(artOverride) ? SidebarRowGeometry.ArtFor(density) : artOverride;
            Element text = subtitle
                ? new BoxEl { Direction = 1, Grow = 1f, Gap = 4f, Children = [Bar(140f, 12f), Bar(80f, 10f)] }
                : new BoxEl { Direction = 1, Grow = 1f, Children = [Bar(140f, 12f)] };
            return new BoxEl
            {
                Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = 10f,
                Padding = new Edges4(9f, 0f, 8f, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Width = art, Height = art, Corners = CornerRadius4.All(Cover.Radius(art, false)),
                        Fill = Tok.FillSubtleSecondary,
                    },
                    text,
                ],
            };
        }

        /// <summary>One pending rail tile: the rail's own 40-DIP box, r8.</summary>
        public static Element RailTile() => new BoxEl
        {
            Width = RailTileBox, Height = RailTileBox, Corners = CornerRadius4.All(8f), Fill = Tok.FillSubtleSecondary,
        };

        /// <summary><paramref name="count"/> pending rail tiles at the rail's 6-DIP gap, centred.</summary>
        public static Element RailStack(int count)
        {
            var kids = new Element[count < 0 ? 0 : count];
            for (int i = 0; i < kids.Length; i++) kids[i] = RailTile();
            return new BoxEl { Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center, Children = kids };
        }

        static Element Bar(float w, float h) => new BoxEl
        {
            Width = w, Height = h, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary,
        };
    }

    /// <summary>The 28-DIP section header band, the bare heading and the explicit divider. (0.2.9's Section / Rule /
    /// RevealWrapper had no call sites — the virtualized pane plans rows, it never wraps a body in a clip.)</summary>
    internal static class SectionHeader
    {
        public const float Height = SidebarRowGeometry.HeaderHeight;

        /// <summary>Title (12/600 secondary, ellipsised — #84: the one row family that used to overflow) · spacer ·
        /// optional action · the chevron. <paramref name="onToggle"/> gets the NEW state; null ⇒ not collapsible (no
        /// chevron, no click, no hover plate). A clickable action consumes its own clicks.</summary>
        public static Element Header(string title, bool open, Action<bool>? onToggle, Element? action = null,
                                     Element? chevron = null)
        {
            bool collapsible = onToggle is not null;
            var kids = new Element[2 + (action is null ? 0 : 1) + (collapsible ? 1 : 0)];
            int k = 0;
            // Shrink 1 is required: the spacer is a zero-basis Grow box, so the title is the only child that can give
            // width back once the row overflows.
            kids[k++] = new TextEl(title)
            {
                Size = 12f, Weight = 600, Color = Tok.TextSecondary,
                Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            kids[k++] = new BoxEl { Grow = 1f };
            if (action is { } a) kids[k++] = a;
            if (collapsible) kids[k] = chevron ?? Icon(open ? Icons.ChevronUp : Icons.ChevronDown, 10f, Tok.TextTertiary);

            Action? click = null;
            if (onToggle is { } toggle) click = () => toggle(!open);
            return new BoxEl
            {
                Direction = 0, Height = SidebarRowGeometry.HeaderHeight, AlignItems = FlexAlign.Center, Gap = 4f,
                // THE ROW INSET, never a literal 8: the header is a sibling of the rows it labels (one lane).
                Padding = PaneMetrics.RowInset, Corners = CornerRadius4.All(4f),
                HoverFill = collapsible ? Tok.FillSubtleSecondary : ColorF.Transparent,
                Role = collapsible ? AutomationRole.Button : AutomationRole.None,
                OnClick = click,
                Children = kids,
            };
        }

        /// <summary>A non-collapsible heading (<c>SidebarSectionKind.Header</c>): same type, no chevron, no plate.</summary>
        public static Element Label(string title) => Header(title, open: true, onToggle: null);

        /// <summary>An explicit Divider section: a 16-DIP band spanning exactly the row content box (one inset owner),
        /// its hairline centred 8 DIP below the previous row.</summary>
        public static Element ExplicitDivider() => new BoxEl
        {
            Direction = 0, Height = SidebarRowGeometry.DividerHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = PaneMetrics.RowInset,
            Children = [new BoxEl { Grow = 1f, Height = 1f, Fill = Tok.StrokeDividerDefault }],
        };
    }

    // ══ 5. THE PIN DROP ZONE AND THE "+" ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The ZERO-PIN state (W4). At rest a real 56-DIP card — a solid StrokeCardDefault hairline, the pin mark, and two
    /// lines naming the gesture AND its alternative, so a user who never drags still learns how to pin. The dashed accent
    /// border, the AccentSubtle fill and the 72-DIP growth are reserved for a live COMPATIBLE drag, where a louder target
    /// is actually useful. A track drag fails <c>CanPin</c>, so no pin affordance appears.
    ///
    /// <para>Its own Component so <c>UseDragState</c> (a re-render per drag CONTENT edge) is scoped to this card.</para>
    /// </summary>
    internal sealed class PinDropZone : Component
    {
        /// <summary>The resting height a virtualizing host seeds; the growth is a measured reflow on top of it.</summary>
        public const float RestHeight = SidebarRowGeometry.PinDropZoneRestHeight;
        public const float ActiveHeight = 72f;

        static readonly LayoutTransition Resize = new(
            TransitionChannels.Size, MotionTok.ContentResize.ToDynamics(),
            Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing);

        readonly Action<object?, int> _accept;

        public PinDropZone(Action<object?, int> accept) => _accept = accept;

        public override Element Render()
        {
            var drag = UseDragState();
            var over = UseSignal(false);
            // The accept test IS the pin-eligibility test; the caption names WHAT gets pinned (the half the chip covers).
            var spec = UseMemo(() => Drop.Target<DragPayload>(
                Drag.Resource,
                accepts: static p => p.CanPin,
                caption: static p => Drag.Pin(p.Name),
                onEnter: (_, _) => over.Value = true,
                onOver: (_, _) => over.Value = true,
                onLeave: _ => over.Value = false,
                onDrop: (p, _) => { over.Value = false; _accept(p, 0); },
                visualPolicy: DropTargetVisualPolicy.Spotlight), DepKey.Empty);

            bool compatible = drag.Active
                && string.Equals(drag.Kind, Drag.Resource, StringComparison.Ordinal)
                && Drag.Unwrap(drag.Payload) is { CanPin: true };
            bool active = compatible || over.Value;

            return new BoxEl
            {
                Key = "pins-empty",
                Height = active ? ActiveHeight : RestHeight,
                // The pane owns the 8-DIP horizontal inset; the card carries only its trailing gap. It is the CARD family:
                // its plate starts at PanePad like every row fill, its content padded inside.
                Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Corners = Radii.ControlAll,
                DropTarget = spec,
                Fill = active ? Tok.AccentSubtle : ColorF.Transparent,
                BorderColor = active ? Tok.AccentDefault : Tok.StrokeCardDefault,
                BorderWidth = 1f,
                BorderDashOn = active ? Spacing.XS : 0f,
                BorderDashOff = active ? Spacing.XXS : 0f,
                Transition = MotionTok.ControlFaster,
                Layout = Resize,
                Children =
                [
                    Icon(Icons.Pin, 16f, active ? Tok.AccentTextPrimary : Tok.TextTertiary),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.XXS,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Sidebar.DropToPin))
                            {
                                Size = 12f, Weight = (ushort)(active ? 600 : 400),
                                Color = active ? Tok.AccentTextPrimary : Tok.TextSecondary,
                                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            },
                            // ONE ellipsised line: a nudge, not a paragraph.
                            new TextEl(Loc.Get(Strings.Sidebar.Pin.EmptyHint))
                            {
                                Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            },
                        ],
                    },
                ],
            };
        }
    }

    /// <summary>
    /// THE "+" create affordance for every surface that offers it (the PlaylistTree header, a folder row, the rail
    /// footer, V3's header), so "what does + do" cannot drift per design.
    ///
    /// <para>With a <c>menu</c> it opens a flyout built AT OPEN TIME (labels resolve then, never at render); a factory
    /// that answers nothing keeps the one direct verb — an affordance that opens nothing reads as broken. It is also a
    /// DROP destination whose spec and <c>dropActive</c> probe are the CALLER's (only the pane knows what a drop there
    /// means); the cue is a BOUND accent plate because it runs while a drag is live. <c>revealOpacity</c> is the folder
    /// row's reveal: hover flags freeze mid-drag, so the bound base opacity is what shows the "+" during a gesture.</para>
    ///
    /// <para><c>BlocksDragArm</c> stops the drag-arm walk here, so pressing "+" on a draggable row neither lifts that row
    /// nor toggles the folder under it.</para>
    /// </summary>
    internal sealed class CreateButton : Component
    {
        readonly Action _onPlaylist;
        readonly Func<ContextMenuModel?>? _menu;
        readonly DropTargetSpec? _drop;
        readonly Func<bool>? _dropActive;
        readonly Func<float>? _revealOpacity;
        readonly float _box, _glyph;

        public CreateButton(Action onPlaylist, Func<ContextMenuModel?>? menu = null, DropTargetSpec? drop = null,
                            Func<bool>? dropActive = null, Func<float>? revealOpacity = null, float box = 24f,
                            float glyph = 14f)
        {
            _onPlaylist = onPlaylist; _menu = menu; _drop = drop; _dropActive = dropActive;
            _revealOpacity = revealOpacity; _box = box; _glyph = glyph;
        }

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);

            void Activate()
            {
                if (_menu is null || svc is null) { _onPlaylist(); return; }
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                if (_menu() is not { } model || model.Rows.Count == 0) { _onPlaylist(); return; }
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(model.Rows, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss,
                                     Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            var box = new BoxEl
            {
                Width = _box, Height = _box, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(4f),
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                BlocksDragArm = true,
                OnRealized = h => anchor.Value = h,
                OnClick = Activate,
                DropTarget = _drop,
                Children = [Icon(Icons.Add, _glyph, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle);

            // AFTER Interactive, which rewrites Fill/BorderColor wholesale — the drag cue has to land on top of it.
            if (_dropActive is { } cue)
                box = box with
                {
                    Fill = Prop.Of(() => cue() ? Tok.AccentDefault with { A = 0.18f } : ColorF.Transparent),
                    BorderColor = Prop.Of(() => cue() ? Tok.AccentDefault : ColorF.Transparent),
                    BorderWidth = 1f,
                };
            if (_revealOpacity is { } reveal) box = box with { Opacity = Prop.Of(reveal), HoverOpacity = 1f };

            return ToolTip.Wrap(box, Loc.Get(_menu is null
                ? Strings.Sidebar.CreatePlaylistTooltip
                : Strings.Sidebar.CreateTooltip));
        }
    }

    // ══ 6. THE SECTION CARD (the customize canvas, W9 / W10) ═════════════════════════════════════════════════════════
    //
    // What a whole section looks like while the pane IS the customize canvas: ONE uniform 44-DIP card — grip · kind tile
    // · title · count or "Hidden" · eye · "…" · chevron. A uniform pitch is what lets the section band be an ordinary
    // Reorderable run inside the one virtualized plan list. Every affordance mutates through the pane's command methods,
    // so every edit is reduced, undoable, autosaved and visible in the same frame.

    internal static class EditCard
    {
        /// <summary>The plate's vertical inset: PADDING on the slot root, never a Margin, so the measured extent and the
        /// Reorderable pitch are the same number.</summary>
        const float PlateInsetY = 2f;

        /// <summary>A hidden section stays IN the canvas, dimmed; dimming + the tag is the difference from "gone".</summary>
        const float HiddenOpacity = 0.55f;

        /// <summary>One card. <paramref name="chevron"/> is built by the SLOT: a hook-owning component whose probe must
        /// capture the recycling slot, never a section id.</summary>
        public static Element Build(PaneView owner, SidebarSectionSpec section, int planIndex, Element chevron)
        {
            string id = section.Id;
            bool pinned = SidebarEditPlan.IsPinnedCard(id);
            bool hidden = section.Hidden;
            bool open = owner.EditShowsBody(section);
            // A Divider / Header card reveals nothing: no chevron, no click — but a same-width spacer keeps every card in
            // the one recycle pool the same shape.
            bool expandable = SidebarEditPlan.HasBody(section.Kind);
            // Inside the armed band, Reorderable.Item's wrapper is the focus stop (and the Space/arrow lift keys).
            bool inBand = owner.TryEditSectionBand(planIndex, out _);
            // HIDDEN wins the trailing slot over the count; a projected section shows no count rather than a guessed one.
            int count = hidden ? -1 : SidebarEditPlan.CardCount(section);
            bool badge = hidden || count >= 0;

            var kids = new Element[3 + (badge ? 1 : 0) + (pinned ? 0 : 2) + 1];
            int k = 0;

            // The GRIP is a pure mark: the drag source is the whole card. The pinned Shortcuts card cannot move, so none.
            kids[k++] = new BoxEl
            {
                Width = 12f, Height = PaneMetrics.EditCardHeight - PlateInsetY * 2f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                Children = [pinned ? Blank(12f) : Icon(Icons.GripperBar, 12f, Tok.TextTertiary)],
            };
            kids[k++] = new BoxEl
            {
                Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
                Fill = open ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                Children = [Icon(RowGlyphs.ForSectionKind(section.Kind), 13f,
                                 open ? Tok.AccentTextPrimary : Tok.TextSecondary)],
            };
            kids[k++] = new TextEl(PaneText.TitleOf(section))
            {
                Size = 13f, Weight = 600,
                Color = hidden ? Tok.TextTertiary : Tok.TextPrimary,
                Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            if (hidden) kids[k++] = HiddenTag();
            else if (count >= 0) kids[k++] = Counts.Number(count);

            // The eye and the "…" are absent on the pinned Shortcuts card: the sentinel is not in `Sections`, so hide /
            // options addressed at it are rejections — an affordance that silently rejects is worse than none. One eye
            // glyph for both states; the STATE is the tint plus the card's dimming.
            if (!pinned)
            {
                kids[k++] = Affordance(Icons.RevealPassword, Loc.Get(hidden ? PaneLoc.EditShow : PaneLoc.EditHide),
                    () => owner.SetSectionHidden(id, !hidden),
                    hidden ? Tok.AccentTextPrimary : Tok.TextSecondary);
                // Keyed by section id, so a recycle onto another section remounts it instead of keeping a frozen subject.
                kids[k++] = Embed.Comp(() => new OptionsButton(owner, id)) with { Key = "sec-opt:" + id };
            }
            kids[k] = expandable ? chevron : Blank(10f);

            Action? activate = null;
            if (expandable) activate = () => owner.ToggleEditExpanded(id);

            var plate = new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                Padding = new Edges4(4f, 0f, 4f, 0f),
                Corners = Radii.ControlAll,
                // The EXPANDED card wears the selected plate: it is the one whose rows are on screen.
                Fill = open ? global::Wavee.Design.Colors.SelectedRest : Tok.FillCardDefault,
                HoverFill = open ? global::Wavee.Design.Colors.SelectedHover : Tok.FillCardSecondary,
                PressedFill = open ? global::Wavee.Design.Colors.SelectedPressed : Tok.FillSubtleTertiary,
                BorderWidth = 1f,
                BorderColor = open ? Tok.AccentSubtle : Tok.StrokeCardDefault,
                BrushTransitionMs = global::Wavee.Design.Motion.Faster,
                Opacity = hidden ? HiddenOpacity : 1f,
                Role = expandable ? AutomationRole.Button : AutomationRole.None,
                Cursor = expandable ? CursorId.Hand : CursorId.Arrow,
                Focusable = !inBand,
                OnClick = activate,
                DropTarget = PaletteDrop(owner, id),
                Children = kids,
            };
            // Right-click is where the NON-drag ways to move a section live (drag is one way, never the only one).
            if (!pinned) plate = plate.WithContextMenu(owner.MenuOverlay, () => owner.EditCardMenu(id));

            return new BoxEl
            {
                Key = id,
                Direction = 1, Height = PaneMetrics.EditCardHeight, Shrink = 0f,
                Padding = new Edges4(SidebarRowGeometry.RowInsetLeft, PlateInsetY, SidebarRowGeometry.RowInsetRight,
                                     PlateInsetY),
                Children = [plate],
            };
        }

        /// <summary>
        /// The palette→canvas drop: each CARD is its own target and a drop inserts the new section immediately above it.
        /// The band's Reorderable cannot host it — the pane mounts no list wrapper, so its foreign seams have no target
        /// and its slot math would measure from the wrong origin. A card-to-card drag is a ReorderPayload whose Item is
        /// null, so it never unwraps here. Every delegate runs per frame while a drag is live, so none allocates: a count
        /// comparison and two constant-key lookups.
        /// </summary>
        static DropTargetSpec PaletteDrop(PaneView owner, string sectionId) =>
            Drop.Target<SidebarSectionDropPayload>(
                SidebarEditPlan.SectionDragKind,
                accepts: _ => owner.CanAcceptPaletteDrop,
                onDrop: (payload, _) => owner.AddSectionFromPalette(sectionId, payload),
                caption: _ => Loc.Get(PaneLoc.EditDropHere),
                // The ONE refusal (the section cap) is a reason the user can act on — never an invisible refusal.
                refusalCaption: _ => Loc.Get(PaneLoc.EditDropFull));

        /// <summary>A 24-DIP card affordance. Non-focusable: the card's (or the band wrapper's) stop is the row's, and every
        /// command here is also in the context menu.</summary>
        static Element Affordance(string glyph, string tip, Action onClick, ColorF tint)
        {
            var box = new BoxEl
            {
                Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Cursor = CursorId.Hand, Focusable = false, Role = AutomationRole.Button,
                OnClick = onClick,
                Children = [Icon(glyph, 12f, tint)],
            }.Interactive(Interaction.Subtle);
            return ToolTip.Wrap(box, tip);
        }

        static Element Blank(float size) => new BoxEl { Width = size, Height = 0f, Shrink = 0f };

        /// <summary>The "Hidden" pill (10/600 on FillSubtleSecondary, r Full) — the customizer's own string.</summary>
        static Element HiddenTag() => new BoxEl
        {
            Shrink = 0f, Padding = new Edges4(6f, 1f, 6f, 2f), Corners = CornerRadius4.All(Radii.Full),
            Fill = Tok.FillSubtleSecondary, HitTestVisible = false,
            Children =
            [
                new TextEl(Loc.Get(PaneLoc.EditHidden)) { Size = 10f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 },
            ],
        };

        /// <summary>The card's "…": OPTIONS LIVE ON THE OBJECT. The pane owns the 320×520 popover (anchored
        /// RightEdgeAlignedTop, so it never covers the rows the options are about); this component owns only what a
        /// static builder cannot — the anchor node — and hands the pane a probe for it.</summary>
        sealed class OptionsButton : Component
        {
            readonly PaneView _owner;
            readonly string _sectionId;
            Func<NodeHandle>? _anchorProbe;

            public OptionsButton(PaneView owner, string sectionId) { _owner = owner; _sectionId = sectionId; }

            public override Element Render()
            {
                var anchor = UseRef<NodeHandle>(default);
                var probe = _anchorProbe ??= () => anchor.Value;
                return ToolTip.Wrap(new BoxEl
                {
                    Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnRealized = h => anchor.Value = h,
                    OnClick = () => _owner.OpenSectionOptions(_sectionId, probe),
                    Children = [Icon(Icons.More, 14f, Tok.TextSecondary)],
                }.Interactive(Interaction.Subtle), Loc.Get(PaneLoc.EditOptions));
            }
        }
    }

    // ══ 7. INLINE CONTROLS (W1b) AND THE SEARCH HEAD ═════════════════════════════════════════════════════════════════
    //
    // An EntityList section's filter chips + sort/view trigger, rendered as HEADER chrome (never a virtualized row).
    // Every edit rewrites THIS SECTION'S PERSISTED SPEC through SetQuery / SetDisplayOption → reducer → undo pre-image →
    // autosave — which is exactly why this is not V3's mode-global flyout.

    internal static class InlineControls
    {
        /// <summary>The kind chips. "On" means the chip's kind is the WHOLE filter; tapping the active chip clears back to
        /// everything, so no chip can blank its own section.</summary>
        public static Element Chips(PaneView owner, SidebarSectionSpec section)
        {
            var q = section.Query ?? SidebarEntityQuery.Default;
            return new BoxEl
            {
                // No horizontal inset of its own: the pane owns the edge (a second one made a fifth left edge).
                Direction = 0, Wrap = true, Gap = 4f, Padding = new Edges4(0f, 0f, 0f, 2f),
                Children =
                [
                    Chip(owner, section.Id, q, SidebarEntityKinds.Playlists, PaneLoc.FilterPlaylists),
                    Chip(owner, section.Id, q, SidebarEntityKinds.Albums, PaneLoc.FilterAlbums),
                    Chip(owner, section.Id, q, SidebarEntityKinds.Artists, PaneLoc.FilterArtists),
                    Chip(owner, section.Id, q, SidebarEntityKinds.Shows, PaneLoc.FilterPodcasts),
                ],
            };
        }

        static Element Chip(PaneView owner, string sectionId, SidebarEntityQuery q, SidebarEntityKinds kind,
                            string labelKey)
        {
            bool on = q.Kinds == kind;
            return new BoxEl
            {
                Key = labelKey,
                Height = SidebarRowGeometry.ChipHeight, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(10f, 0f, 10f, 0f), Corners = Radii.PillAll,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                OnClick = () => owner.Dispatch(new SetQuery(sectionId, q with { Kinds = on ? SidebarEntityKinds.All : kind })),
                Children =
                [
                    new TextEl(Loc.Get(labelKey))
                    {
                        Size = 12f, Weight = (ushort)(on ? 600 : 400),
                        Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary, MaxLines = 1,
                    },
                ],
            }.Interactive(Interaction.Subtle) with
            {
                // AFTER Interactive (it rewrites Fill wholesale): the ON chip is the accent pill (ch 25 W1b), and it keeps
                // the accent ramp through hover/press rather than flattening to the subtle veil.
                Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary,
                HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
                PressedFill = on ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
            };
        }

        /// <summary>The header's 24-DIP sort/view trigger. Keyed by section id: a recycled header slot remounts it.</summary>
        public static Element SortTrigger(PaneView owner, SidebarSectionSpec section)
            => Embed.Comp(() => new SortButton(owner, section.Id)) with { Key = "sec-sort:" + section.Id };

        /// <summary>The flyout rows, built AT OPEN TIME from the LIVE document (labels resolve then; a reopen after an edit
        /// shows the new state).</summary>
        public static IReadOnlyList<MenuFlyoutItem> Rows(PaneView owner, string sectionId)
        {
            var section = Sidebar.Layout.Find(sectionId) ?? owner.SectionOf(sectionId);
            if (section is null) return Array.Empty<MenuFlyoutItem>();
            var q = section.Query ?? SidebarEntityQuery.Default;

            var rows = new List<MenuFlyoutItem>(10)
            {
                Sort(owner, sectionId, q, SidebarSortMode.Recents, PaneLoc.SortRecents),
                Sort(owner, sectionId, q, SidebarSortMode.RecentlyAdded, PaneLoc.SortRecentlyAdded),
                Sort(owner, sectionId, q, SidebarSortMode.Alphabetical, PaneLoc.SortAlphabetical),
                Sort(owner, sectionId, q, SidebarSortMode.Creator, PaneLoc.SortCreator),
            };
            // Custom order only means something for a playlists-ONLY query — elsewhere it would silently do nothing.
            if (q.Kinds == SidebarEntityKinds.Playlists)
                rows.Add(Sort(owner, sectionId, q, SidebarSortMode.CustomOrder, PaneLoc.SortCustom));
            // "Reversed" = not this sort's NATURAL direction: recency is naturally newest-first, collation A→Z.
            bool recency = q.Sort is SidebarSortMode.Recents or SidebarSortMode.RecentlyAdded;
            bool reversed = recency ? !q.Descending : q.Descending;
            rows.Add(MenuFlyoutItem.Toggle(Loc.Get(PaneLoc.SortReversed), reversed,
                () => owner.Dispatch(new SetQuery(sectionId, q with { Descending = !q.Descending }))));
            rows.Add(MenuFlyoutItem.Separator);

            bool grid = section.Opts.Presentation == SidebarPresentation.Grid;
            rows.Add(MenuFlyoutItem.RadioItem(Loc.Get(PaneLoc.ViewList), !grid,
                () => owner.Dispatch(new SetDisplayOption(sectionId, SidebarDisplayField.Presentation,
                    (int)SidebarPresentation.List)), Icons.ViewList));
            rows.Add(MenuFlyoutItem.RadioItem(Loc.Get(PaneLoc.ViewGrid), grid,
                () => owner.Dispatch(new SetDisplayOption(sectionId, SidebarDisplayField.Presentation,
                    (int)SidebarPresentation.Grid)), Icons.ViewGrid));
            return rows;
        }

        static MenuFlyoutItem Sort(PaneView owner, string sectionId, SidebarEntityQuery q, SidebarSortMode mode,
                                   string labelKey)
            => MenuFlyoutItem.RadioItem(Loc.Get(labelKey), q.Sort == mode,
                () => owner.Dispatch(new SetQuery(sectionId, q with { Sort = mode })));

        /// <summary>The sort/view icon button: an anchor node + an open handle; the section's spec is re-read at open.</summary>
        sealed class SortButton : Component
        {
            readonly PaneView _owner;
            readonly string _sectionId;

            public SortButton(PaneView owner, string sectionId) { _owner = owner; _sectionId = sectionId; }

            public override Element Render()
            {
                var anchor = UseRef<NodeHandle>(default);
                var handle = UseRef<OverlayHandle?>(null);

                void Toggle()
                {
                    if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                    var items = Rows(_owner, _sectionId);
                    if (items.Count == 0) return;
                    handle.Value = _owner.MenuOverlay.Open(
                        () => anchor.Value,
                        () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                        FlyoutPlacement.BottomEdgeAlignedRight,
                        new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss,
                                         Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
                    handle.Value.ClosedAction = () => handle.Value = null;
                }

                return ToolTip.Wrap(new BoxEl
                {
                    Width = 24f, Height = 24f, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnRealized = h => anchor.Value = h,
                    OnClick = Toggle,
                    Children = [Icon(Icons.Sort, 14f, Tok.TextSecondary)],
                }.Interactive(Interaction.Subtle), Loc.Get(PaneLoc.SortLabel));
            }
        }
    }

    /// <summary>The Curated pane's library-only search: fixed chrome ABOVE the scroll surface, so it carries the pane's
    /// horizontal inset itself (band pad 8,8,8,4; field 32 tall, 13 px, width max(120, paneW − 16)). It writes the pane's
    /// OWN session signal, never V3's mode-global search. The width is a bound signal, so a seam drag does not re-render
    /// this component per frame.</summary>
    internal sealed class SearchHead : Component
    {
        readonly Signal<string> _text;
        readonly Signal<float> _paneWidth;

        public SearchHead(Signal<string> text, Signal<float> paneWidth) { _text = text; _paneWidth = paneWidth; }

        public override Element Render()
        {
            var width = UseComputed(() => MathF.Max(120f, _paneWidth.Value - PaneMetrics.PaneInsetH));
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Padding = new Edges4(8f, 8f, 8f, 4f),
                Children =
                [
                    Embed.Comp(() => new EditableText
                    {
                        Text = _text,
                        Placeholder = Loc.Get(PaneLoc.SearchPlaceholder),
                        WidthSignal = width,
                        Height = 32f,
                        FontSize = 13f,
                        ShowDeleteButton = true,
                        LeftAffix = Icon(Icons.Search, 14f, Tok.TextSecondary),
                    }),
                ],
            };
        }
    }

    // ══ 8. THE PANE'S TEXT, LOC AND GLYPH TABLES ═════════════════════════════════════════════════════════════════════

    /// <summary>The renderer's display rules: section titles, per-kind subtitles, the item join, icon fallbacks and the
    /// "never render a blank row" degradations — split out so the row builders stay about LAYOUT.</summary>
    internal static class PaneText
    {
        /// <summary>The user's rename wins, then the template's key, then the kind's default (JumpBackIn follows its
        /// recents source).</summary>
        public static string TitleOf(SidebarSectionSpec section)
        {
            if (section.Title is { Length: > 0 } title) return title;
            if (section.TitleLocKey is { Length: > 0 } key) return Loc.Get(key);
            var fallback = SidebarSectionKinds.DefaultTitleLocKey(section.Kind, section.Opts.Recents);
            return fallback is null ? "" : Loc.Get(fallback);
        }

        /// <summary>The hand-placed item a row was projected from (the one join rule, shared with the selection sweep).</summary>
        public static SidebarItemSpec? ItemOf(SidebarSectionSpec section, string key) => SidebarRowResolve.ItemOf(section, key);

        /// <summary>Playlist → "N songs" · album → "Album · first artist" · artist → "Artist" · show → "Podcast ·
        /// publisher" · folder → "N items" · track → its artist · route → a concert's venue (its Creator).</summary>
        public static string? SubtitleOf(in SidebarLibraryEntry e) => e.Kind switch
        {
            SidebarEntryKind.Playlist => Strings.Sidebar.SongCount(e.TrackCount),
            // A LIBRARY album bills its first artist in FirstArtistName; a FEED album carries its one creator in Creator.
            SidebarEntryKind.Album => ArtistOf(in e) is { Length: > 0 } artist
                ? Loc.Get(Strings.Sidebar.V3.Kind.Album) + " · " + artist
                : Loc.Get(Strings.Sidebar.V3.Kind.Album),
            SidebarEntryKind.Artist => Loc.Get(Strings.Sidebar.V3.Kind.Artist),
            SidebarEntryKind.Show => e.Publisher.Length > 0
                ? Loc.Get(Strings.Sidebar.V3.Kind.Show) + " · " + e.Publisher
                : Loc.Get(Strings.Sidebar.V3.Kind.Show),
            SidebarEntryKind.Folder => Strings.Sidebar.V3.ItemCount(e.ChildCount),
            SidebarEntryKind.Track => e.Creator.Length > 0 ? e.Creator : null,
            SidebarEntryKind.AppRoute => e.Creator.Length > 0 ? e.Creator : null,
            _ => null,
        };

        static string ArtistOf(in SidebarLibraryEntry e) => e.FirstArtistName.Length > 0 ? e.FirstArtistName : e.Creator;

        /// <summary>A release's compact age ("3d" / "2w" / "4mo"): digits + a unit letter (no loc key exists for it).</summary>
        public static string? AgeBadge(long epochMs)
        {
            if (epochMs <= 0) return null;
            long days = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - epochMs) / 86_400_000L;
            if (days < 0) return null;                       // a future stamp is not an age
            if (days < 1) return "1d";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (days < 7) return days.ToString(inv) + "d";
            if (days < 60) return (days / 7).ToString(inv) + "w";
            return (days / 30).ToString(inv) + "mo";
        }

        /// <summary>An event's day over its month in the art slot; the month comes from the culture's abbreviated names.</summary>
        public static Element DateBlock(long epochMs, float size)
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime();
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            return new BoxEl
            {
                Width = size, Height = size, Shrink = 0f,
                Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(Cover.Radius(size, circular: false)),
                Fill = Tok.FillSubtleSecondary,
                Children =
                [
                    new TextEl(when.Day.ToString(culture))
                    {
                        Size = size >= 32f ? 14f : 11f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                    },
                    new TextEl(when.ToString("MMM", culture)) { Size = size >= 32f ? 9f : 8f, Color = Tok.TextTertiary, MaxLines = 1 },
                ],
            };
        }

        /// <summary>The last resort when nothing names a row: the uri's / key's final segment. Never blank, never a crash —
        /// a hand-edited document must render something the user can right-click and remove.</summary>
        public static string ShortUri(string? uri)
        {
            if (string.IsNullOrEmpty(uri)) return "—";
            int at = uri.LastIndexOf(':');
            return at >= 0 && at + 1 < uri.Length ? uri[(at + 1)..] : uri;
        }

        /// <summary>An item's glyph, preferring its authored override. Null-item tolerant (a projected row has no item).</summary>
        public static string Glyph(SidebarItemSpec? item, string fallback)
            => item is null ? fallback : RowGlyphs.For(item, fallback);

        /// <summary>The natural mark for a PROJECTED row's family (the projection's own enum).</summary>
        public static string EntryGlyph(SidebarEntryKind kind) => kind switch
        {
            SidebarEntryKind.Playlist => Icons.MusicNote,
            SidebarEntryKind.Album => Icons.Album,
            SidebarEntryKind.Artist => Icons.Contact,
            SidebarEntryKind.Show => Icons.RadioTower,
            SidebarEntryKind.Folder => Icons.Folder,
            SidebarEntryKind.AppRoute => Icons.Home,
            _ => Icons.MusicNote,
        };

        /// <summary>The EMPTY copy for a section that resolved to zero rows, per kind (never a borrowed debug string).</summary>
        public static string EmptyText(SidebarSectionKind kind) => kind switch
        {
            SidebarSectionKind.JumpBackIn => Loc.Get(PaneLoc.SectionEmptyRecents),
            SidebarSectionKind.NewReleases => Loc.Get(PaneLoc.NewReleasesEmpty),
            SidebarSectionKind.Concerts => Loc.Get(PaneLoc.ConcertsEmpty),
            _ => Loc.Get(PaneLoc.SectionEmpty),
        };
    }

    /// <summary>The pane renderer's loc KEYS as literals, in one place. Every key exists in <c>assets/loc/en-US.json</c>;
    /// a typo renders loudly as <c>[key]</c>.</summary>
    internal static class PaneLoc
    {
        public const string ExtensionManage = "sidebar.extension.manage";
        public const string ExtensionMissing = "sidebar.action.unavailable.missing";
        public const string ExtensionNotNow = "sidebar.action.unavailable.notNow";
        public const string ConcertsPrompt = "sidebar.concerts.locationPrompt";
        public const string ConcertsEmpty = "sidebar.concerts.empty";
        public const string NewReleasesEmpty = "sidebar.newReleases.empty";
        public const string MissingEntity = "sidebar.customizer.missingEntity";
        public const string RemoveItem = "sidebar.customizer.undo.removeItem";
        /// <summary>The row menu's Remove — the customizer item list's own word, so the two cannot disagree.</summary>
        public const string ItemRemove = "sidebar.customizer.itemRemove";
        public const string PaneEmpty = "sidebar.customizer.empty";
        public const string PaneEmptySub = "sidebar.customizer.emptySub";
        public const string SectionEmpty = "sidebar.section.empty";
        public const string SectionEmptyRecents = "sidebar.section.emptyRecents";
        public const string LibraryEmpty = "sidebar.v3.empty.library";
        public const string SearchEmpty = "sidebar.v3.empty.search";
        public const string SearchPlaceholder = "sidebar.v3.searchPlaceholder";
        public const string SortLabel = "sidebar.option.sort";
        public const string SortRecents = "sidebar.option.sortRecents";
        public const string SortRecentlyAdded = "sidebar.option.sortRecentlyAdded";
        public const string SortAlphabetical = "sidebar.option.sortAlphabetical";
        public const string SortCreator = "sidebar.option.sortCreator";
        public const string SortCustom = "sidebar.v3.sort.custom";
        public const string SortReversed = "sidebar.v3.sort.reversed";
        public const string ViewList = "sidebar.option.presentationList";
        public const string ViewGrid = "sidebar.option.presentationGrid";
        public const string FilterPlaylists = "sidebar.v3.filter.playlists";
        public const string FilterPodcasts = "sidebar.v3.filter.podcasts";
        public const string FilterAlbums = "sidebar.v3.filter.albums";
        public const string FilterArtists = "sidebar.v3.filter.artists";

        // ── the customize canvas ──
        /// <summary>The options popover's title — the docked inspector's own string (the same surface, re-hosted).</summary>
        public const string EditOptions = "sidebar.customizer.properties";
        public const string EditHide = "sidebar.customizer.undo.hideSection";
        public const string EditShow = "sidebar.customizer.undo.showSection";
        public const string EditHidden = "sidebar.customizer.hidden";
        public const string EditMoveUp = "sidebar.customizer.moveUp";
        public const string EditMoveDown = "sidebar.customizer.moveDown";
        public const string EditRemove = "sidebar.customizer.undo.removeSection";
        public const string EditDuplicate = "sidebar.customizer.undo.duplicateSection";
        public const string EditDuplicateSuffix = "sidebar.customizer.duplicateSuffix";
        /// <summary>The palette→canvas cues: CONSTANT for the whole gesture, so the caption resolvers stay 0-alloc.</summary>
        public const string EditDropHere = "sidebar.customizer.dropHere";
        public const string EditDropFull = "sidebar.customizer.dropFull";

        /// <summary>REUSED "{index} of {count}" — a second key for one sentence is how translations drift.</summary>
        public const string ReorderPosition = "sidebar.pin.position";
        public const string ReorderGrabbed = "sidebar.customizer.reorderGrabbed";
        public const string ReorderMoved = "sidebar.customizer.reorderMoved";
        public const string ReorderDropped = "sidebar.customizer.reorderDropped";
        public const string ReorderCancelled = "sidebar.customizer.reorderCancelled";
    }

    /// <summary>An action descriptor's <see cref="IconRef"/> as a leading element — the app-side twin of the engine's
    /// internal IconRef render path (a registered themed name wins over the glyph; a glyph keeps its font override).</summary>
    internal static class PaneIcon
    {
        /// <param name="art">The section's art edge: the 16-DIP mark is CENTRED in an art-wide column, so an action row's
        /// label lands at the same x as every other row in its section (W7).</param>
        public static Element? Leading(string? iconOverride, IconRef icon, bool enabled, float art)
        {
            var color = enabled ? Tok.TextSecondary : Tok.TextTertiary;
            Element mark;
            // An authored override is the user's explicit choice and beats the descriptor's own mark.
            if (iconOverride is { Length: > 0 } name && RowGlyphs.IsAllowed(name))
                mark = Icon(RowGlyphs.Glyph(name, Icons.MusicNote), 16f, color);
            else if (icon.ThemedName is { Length: > 0 } themed && ThemedIconRegistry.Has(themed))
                mark = ThemedIcon.Create(themed, 16f);
            else if (icon.Glyph is { Length: > 0 } glyph)
                mark = Icon(glyph, 16f, color, icon.Font);
            else
                mark = Icon(Icons.MusicNote, 16f, color);
            return new BoxEl
            {
                Width = art, Height = art, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [mark],
            };
        }
    }

    /// <summary>
    /// The glyph whitelist for hand-placed items. A layout document is a hand-editable JSON file, so an IconOverride is a
    /// NAME, never a codepoint — this map is the only thing that turns a name into a glyph, so no document can inject an
    /// arbitrary codepoint. The NAME list is CORE (<c>SidebarIconNames</c>, the reducer validates against it); this owns
    /// name → glyph, and an unknown name degrades to the row's natural mark, never a blank box.
    /// </summary>
    internal static class RowGlyphs
    {
        /// <summary>Ordered, stable — the icon-picker order.</summary>
        public static string[] Allowed => SidebarIconNames.Allowed;

        public static bool IsAllowed(string? name) => SidebarIconNames.IsAllowed(name);

        public static string Glyph(string? name, string fallback) => name switch
        {
            "MusicNote" => Icons.MusicNote,
            "Heart" => Icons.Heart,
            "Album" => Icons.Album,
            "Contact" => Icons.Contact,
            "RadioTower" => Icons.RadioTower,
            "Folder" => Icons.Folder,
            "FolderOpen" => Icons.FolderOpen,
            "Home" => Icons.Home,
            "Search" => Icons.Search,
            "Clock" => Icons.Clock,
            "Star" => Icons.Star,
            "FavoriteStar" => Icons.FavoriteStar,
            "Tag" => Icons.Tag,
            "Headphones" => Icons.Headphones,
            "Microphone" => Icons.Microphone,
            "Movie" => Icons.Movie,
            "Picture" => Icons.Picture,
            "Queue" => Icons.Queue,
            "Shuffle" => Icons.Shuffle,
            "Link" => Icons.Link,
            "Grid" => Icons.Grid,
            "List" => Icons.List,
            "Pin" => Icons.Pin,
            "Settings" => Icons.Settings,
            "Code" => Icons.Code,
            "Globe" => Icons.Globe,
            "Device" => Icons.Device,
            "Friends" => Icons.Friends,
            "Equalizer" => Icons.Equalizer,
            "Download" => Icons.Download,
            _ => fallback,
        };

        /// <summary>An item's glyph: its override, else <paramref name="fallback"/> (a route item passes its route glyph).</summary>
        public static string For(SidebarItemSpec item, string fallback) => Glyph(item.IconOverride, fallback);

        /// <summary>The natural placeholder mark for an entity family (the persisted enum).</summary>
        public static string ForEntityKind(SidebarEntityKind kind) => kind switch
        {
            SidebarEntityKind.Playlist => Icons.MusicNote,
            SidebarEntityKind.Album => Icons.Album,
            SidebarEntityKind.Artist => Icons.Contact,
            SidebarEntityKind.Show => Icons.RadioTower,
            SidebarEntityKind.PlaylistFolder => Icons.Folder,
            SidebarEntityKind.Track => Icons.MusicNote,
            _ => Icons.MusicNote,
        };

        /// <summary>A section KIND's mark — the one table the section card, the customizer outline and its inspector
        /// header read, so a renamed section is still identifiable at a glance (0.2.9's <c>CzGlyphs.ForKind</c>).</summary>
        public static string ForSectionKind(SidebarSectionKind kind) => kind switch
        {
            SidebarSectionKind.Pinned => Icons.Pin,
            SidebarSectionKind.JumpBackIn => Icons.Clock,
            SidebarSectionKind.CollectionShortcuts => Icons.Heart,
            SidebarSectionKind.PlaylistTree => Icons.Folder,
            SidebarSectionKind.EntityList => Icons.Filter,
            SidebarSectionKind.StaticLinks => Icons.Link,
            SidebarSectionKind.CustomGroup => Icons.Grid,
            SidebarSectionKind.Header => Icons.Font,
            SidebarSectionKind.Divider => Icons.Remove,
            SidebarSectionKind.EntityEmbed => Icons.FavoriteStar,
            SidebarSectionKind.NewReleases => Icons.Album,
            SidebarSectionKind.Concerts => Icons.Calendar,
            SidebarSectionKind.Extension => Icons.Code,
            _ => Icons.MusicNote,
        };
    }
}
