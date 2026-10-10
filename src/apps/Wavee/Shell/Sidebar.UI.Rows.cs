// ── Shell/Sidebar.UI.Rows.cs ───────────────────────────────────────────────────────────────────────────────────────
// the sidebar row primitives: the one entity row, covers, the rotating chevron, the selection pill, quiet counts,
// skeletons, the section header, the "+" create button, and the pane's text / loc / glyph tables
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
// (Chevron, SelectionPill), an anchor + overlay handle (CreateButton, the sort
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
    // Classic's rows and Library V3's list rows all come out of EntityRow.Create, so the two layouts cannot drift apart.
    // It owns the neutral NavigationViewItem backplate ramp (`NavRow`: the pill is the
    // only "you are here"), the WinUI row ladder (40-px icon column, label at pane 48, 31-px depth indent), the slot
    // order, the one key handler and the drag source.

    /// <summary>Everything <see cref="EntityRow.Create"/> needs: a mutable struct filled with an object initializer and
    /// passed by <c>in</c>, so a row costs no allocation beyond its elements. Always <c>new RowSpec { … }</c>, never
    /// <c>default</c> — the defaults below apply only through the constructor.</summary>
    internal struct RowSpec
    {
        public RowSpec()
        {
            Key = ""; Label = ""; Subtitle = null; Selected = false; Enabled = true; Depth = 0;
            Shape = SidebarRowShape.EntityTwoLine; Tile = false; LabelTooltip = false; Height = float.NaN; Ink = null;
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
        /// <summary>Art-slot edge. NaN ⇒ <c>SidebarRowGeometry.ArtOf(Shape)</c>.</summary>
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
        /// <summary>WHOLE-ROW cue for surfaces rebuilt per open (the rail folder flyout). A plan row does NOT use this —
        /// its plate is the slot's own always-mounted DropPlate, because a thunk here is wired at mount and would answer
        /// for the row this slot first mounted with.</summary>
        public Func<bool>? DropActive;
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

    internal static class EntityRow
    {
        /// <summary>The hover "…" box. It overlays the row and costs the label nothing: a trailing count or pin mark fades out
        /// under it on hover.</summary>
        const float OverflowWidth = 26f;

        /// <summary>The height <paramref name="spec"/> renders at, for a host that sizes a slot before building the row.</summary>
        public static float HeightOf(in RowSpec spec)
            => float.IsNaN(spec.Height) ? SidebarRowGeometry.HeightOf(spec.Shape) : spec.Height;

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
        public static BoxEl Create(in RowSpec spec)
        {
            bool selected = spec.Selected;
            bool enabled = spec.Enabled;
            bool hasSubtitle = !spec.Tile && SidebarRowGeometry.SubtitleVisible(spec.Shape, spec.Subtitle);
            bool hasCaption = spec.Caption is { Length: > 0 };
            float height = float.IsNaN(spec.Height) ? SidebarRowGeometry.HeightOf(spec.Shape) : spec.Height;
            float art = float.IsNaN(spec.ArtSize) ? SidebarRowGeometry.ArtOf(spec.Shape) : spec.ArtSize;
            var activate = spec.OnActivate;
            var checksVisible = spec.ChecksVisible;
            // Icon colour = label colour in every state (NVX:425-427): TextPrimary, a disabled row fades as a whole.
            var ink = spec.Ink ?? Tok.TextPrimary;

            // ── the icon column: the glyph (16) or art centred in the 32 art slot at slot x 10 (a rail tile: centred in 40) ──
            Element visual = spec.Leading
                ?? (spec.Glyph is { Length: > 0 } g ? Icon(g, SidebarRowGeometry.GlyphSize, ink) : new BoxEl { Width = art, Height = art });
            if (spec.Track && spec.Leading is not null) visual = TrackArt(visual, art, spec.Shape);
            Element iconColumn = new BoxEl
            {
                Width = spec.Tile ? SidebarRowGeometry.TileWidth : SidebarRowGeometry.IconColumn, Height = height, Shrink = 0f,
                Padding = new Edges4(spec.Tile ? 0f : SidebarRowGeometry.LeadInset, 0f, 0f, 0f),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [visual],
            };
            if (spec.Tile)
                return Finish(in spec, new BoxEl
                {
                    Key = spec.Key, Animate = spec.Animate, OnRealized = spec.OnRealized,
                    Width = SidebarRowGeometry.TileWidth, Height = height, Shrink = 0f,
                    Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY),
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
                    Body(spec.Label) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1, Wrap = TextWrap.NoWrap, Color = ink, LineHeight = 18f },
                };
                if (hasSubtitle)
                    lines.Add(Caption(spec.Subtitle!).Secondary() with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1, Wrap = TextWrap.NoWrap });
                if (hasCaption)
                    lines.Add(global::Wavee.Design.Type.MicroMeta(spec.Caption!) with
                    {
                        Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    });
                text = new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center,
                    Children = [.. lines],
                };
            }

            // ── trailing: equalizer · pin mark · count/badge, ending at W − 18; the chevron column replaces the 14 pad ──
            bool overflow = spec.Overflow && enabled && spec.MenuOverlay is not null && spec.Menu is not null;
            int trailingCount = (spec.Playing ? 1 : 0) + (spec.Pinned ? 1 : 0) + (spec.Trailing is null ? 0 : 1);
            var kids = new List<Element>(6);
            // The check lane is a gapless sibling BEFORE the icon column: while the checks show it pushes the whole row
            // right (WinUI's multi-select content offset) instead of crushing the 40-px column.
            if (spec.CheckLane is { } lane) kids.Add(lane);
            kids.Add(iconColumn);
            kids.Add(new BoxEl { Width = SidebarRowGeometry.LabelGap, Shrink = 0f });
            kids.Add(text);
            if (trailingCount > 0)
            {
                var parts = new Element[trailingCount];
                int t = 0;
                if (spec.Playing) parts[t++] = Controls.Equalizer(spec.PlayingAnimated, Tok.AccentDefault, Controls.EqualizerH);
                if (spec.Pinned) parts[t++] = Icon(Icons.Pin, 12f, Tok.TextTertiary);
                if (spec.Trailing is { } trailingContent) parts[t] = trailingContent;
                // With a hover "…" the cluster hands its place over: it fades out as the button fades in over it, so the
                // count ends at W − 18 at rest instead of leaving a button-wide hole beside it.
                kids.Add(new BoxEl
                {
                    Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = SidebarRowGeometry.TrailingGap,
                    Margin = new Edges4(SidebarRowGeometry.TrailingGap, 0f, 0f, 0f), Children = parts,
                    HoverOpacity = overflow ? 0f : float.NaN,
                });
            }
            bool chevron = spec.DisclosureChevron is not null;
            if (spec.DisclosureChevron is { } chev)
                kids.Add(new BoxEl
                {
                    Width = SidebarRowGeometry.ChevronColumn, Height = height, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [chev],
                });

            // The "…" is a ZSTACK OVERLAY, never a flex sibling: on a row with no trailing content it costs the title ZERO
            // width, so a title never ellipsizes early and hovering never re-trims it. A long title may run under it —
            // accepted: the row is translucent over Mica, there is no opaque tone to fade into, and a hover-conditional
            // width would relayout on every enter.
            Element[] rowChildren = overflow
                ? [new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Children = [.. kids] },
                   OverflowButton(chevron ? SidebarRowGeometry.ChevronColumn : 0f)]
                : [.. kids];

            var row = new BoxEl
            {
                Key = spec.Key,
                Animate = spec.Animate,
                OnRealized = spec.OnRealized,
                ZStack = overflow,
                Direction = 0, Height = height, AlignItems = FlexAlign.Center,
                Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY),
                Padding = new Edges4(SidebarRowGeometry.IndentFor(spec.Depth), 0f, chevron ? 0f : SidebarRowGeometry.TrailingPad, 0f),
                Corners = Radii.ControlAll,
                Children = rowChildren,
            };
            return Finish(in spec, row, enabled, selected, activate, checksVisible);
        }

        /// <summary>The wiring every row shape shares: the neutral ramp (the multi-selection's quiet plate is the selected
        /// rest fill without a pill), disabled 0.3 (TR:501), the click / modifier-aware activation, the keys, the drag
        /// source, the drop target, and the context menu.</summary>
        static BoxEl Finish(in RowSpec spec, BoxEl row, bool enabled, bool selected, Action<KeyModifiers>? activate,
                            Func<bool>? checksVisible)
        {
            var own = SurfaceRules.Ownership(inSlot: false, hasClick: enabled && (activate is not null || spec.OnClick is not null));
            row = (row with
            {
                Opacity = enabled ? 1f : 0.3f,
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
            if (spec.DropActive is { } plateOn)
            {
                ColorF rest = NavRow(selected || spec.MultiSelected).Fill.Resting(enabled);
                row = row with
                {
                    Fill = Prop.Of(() => plateOn() ? Tok.AccentDefault with { A = 0.18f } : rest),
                    BorderWidth = 1f,
                    BorderColor = Prop.Of(() => plateOn() ? Tok.AccentDefault : ColorF.Transparent),
                };
            }
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

        /// <summary>The hover "…" parked against the row's trailing edge (JustifySelf End) inside the row's ZStack,
        /// <paramref name="trailingInset"/> short of it on a folder so it clears the chevron column. ClickRequestsContext
        /// re-enters the context-request funnel, so the button and a right-click open the SAME menu, anchored at the
        /// button.</summary>
        static Element OverflowButton(float trailingInset) => new BoxEl
        {
            Width = OverflowWidth, Height = OverflowWidth, JustifySelf = FlexAlign.End, AlignSelf = FlexAlign.Center,
            Margin = new Edges4(0f, 0f, trailingInset, 0f),
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
        static Element TrackArt(Element cover, float art, SidebarRowShape shape) => ZStack(
            cover,
            new BoxEl
            {
                Width = art, Height = art, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(Cover.Radius(art, circular: false)),
                Fill = new ColorF(0f, 0f, 0f, 0.55f),
                Opacity = 0f, HoverOpacity = 1f, HitTestVisible = false,
                Children = [Icon(Icons.Play, shape == SidebarRowShape.EntityOneLine ? 10f : 14f, ColorF.FromRgba(0xFF, 0xFF, 0xFF))],
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

        /// <summary>The liked-collection composition. Subscribes to the liked relation and the track table — inside a
        /// value gate (W2-A2): the memo's value is the first four distinct liked cover ids, so the mosaic is
        /// re-composed when a new like or a landed cover CHANGES those four, not on every publication of the track
        /// table while a page's covers stream in.</summary>
        sealed class LikedArt : Component
        {
            readonly float _size, _radius;
            readonly Func<LikedStamp> _stamp;
            readonly string[] _tiles = new string[4];

            public LikedArt(float size, float radius)
            {
                _size = size;
                _radius = radius;
                _stamp = Stamp;
            }

            /// <summary>Newest first; distinct covers only (four likes off one album are one cover, not a mosaic).
            /// Identity by interned image id: equal text is one id, so this is the url comparison without resolving.</summary>
            readonly record struct LikedStamp(uint Epoch, int Found, StringId A, StringId B, StringId C, StringId D);

            LikedStamp Stamp()
            {
                uint epoch = Entities.ScopeEpoch.Value;          // a boot / scope switch is the wake before any table exists
                var scope = Entities.Current;
                if (scope is null) return new LikedStamp(epoch, 0, default, default, default, default);
                _ = scope.Edges.Liked.Changed.Value;
                _ = scope.Tracks.Changed.Value;

                var slots = User.Me.LikedTrackSlots;
                StringId a = default, b = default, c = default, d = default;
                int found = 0;
                for (int i = 0; i < slots.Length && found < 4 && i < 64; i++)
                {
                    var id = new Track(slots[i]).ImageId;
                    if (id.IsEmpty || id == a || id == b || id == c) continue;
                    switch (found++)
                    {
                        case 0: a = id; break;
                        case 1: b = id; break;
                        case 2: c = id; break;
                        default: d = id; break;
                    }
                }
                return new LikedStamp(epoch, found, a, b, c, d);
            }

            public override Element Render()
            {
                var s = UseComputed(_stamp).Value;
                if (s.Found == 0) return Glyph(Icons.Heart, _size);
                string? first = Controls.ArtUrl(s.A);
                if (s.Found < 4 || first is null) return Controls.Artwork(first, _size, _size, _radius, decodePx: DecodeBucket(_size));
                _tiles[0] = first;
                _tiles[1] = Controls.ArtUrl(s.B) ?? first;
                _tiles[2] = Controls.ArtUrl(s.C) ?? first;
                _tiles[3] = Controls.ArtUrl(s.D) ?? first;
                return Controls.Mosaic(_tiles, _size, _size, _radius);
            }
        }
    }

    // ══ 3. THE CHEVRON AND THE SELECTION PILL ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE one disclosure chevron: ONE glyph whose Rotation rides <c>MotionTokenId.Reveal</c> — the SAME spring as the band
    /// it discloses, so the glyph and the rows land together — through <c>SeedValue</c>, so the token owns the dynamics AND
    /// the reduced-motion policy. Never a glyph swap — a swap teleports while the section beside it animates.
    ///
    /// <para>A Component because the rotation needs a node ref and an edge-triggered effect — hooks a recycling slot must
    /// not grow conditionally. <c>open</c> and <c>identity</c> are read inside the effect, never in Render, so their signal
    /// reads subscribe the effect alone: a recycle or an open flip re-runs the seeding and renders nothing.</para>
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
        readonly bool _accent;

        Chevron(Func<bool> open, Func<int>? identity, string glyph, float size, float openDeg, bool accent)
        {
            _open = open; _identity = identity; _glyph = glyph; _size = size; _openDeg = openDeg; _accent = accent;
        }

        /// <summary>A SECTION header's chevron: ChevronDown at rest, rotated 180° when the section is open.</summary>
        public static Element Section(Func<bool> open, float size = 10f, Func<int>? identity = null)
            => Embed.Comp(() => new Chevron(open, identity, Icons.ChevronDown, size, 180f, accent: false));

        /// <summary>A DISCLOSURE chevron (a folder row / tree row): ChevronRight at rest, rotated 90° when expanded.
        /// <paramref name="accentWhenOpen"/> inks it with the accent while open (the track table's expand cell).</summary>
        public static Element Disclosure(Func<bool> open, float size = 10f, Func<int>? identity = null, bool accentWhenOpen = false)
            => Embed.Comp(() => new Chevron(open, identity, Icons.ChevronRight, size, 90f, accentWhenOpen));

        public override Element Render()
        {
            var node = UseRef<NodeHandle>(default);
            var seeded = UseRef(false);
            var seededIdentity = UseRef(0);
            var last = UseRef(0f);

            // The item is read HERE, in an auto-tracked effect, never in Render: an open flip, a recycle onto another
            // item or any other change behind `open` re-runs this body and renders nothing (a bound row's recycle stays
            // a rebind).
            UseEffect(() =>
            {
                float target = _open() ? _openDeg : 0f;
                int identity = _identity?.Invoke() ?? 0;
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null || node.Value.IsNull || !scene.IsLive(node.Value)) return;
                if (!seeded.Value || seededIdentity.Value != identity)
                {
                    seeded.Value = true;
                    seededIdentity.Value = identity;
                    last.Value = target;
                    anim.SeedValue(node.Value, AnimChannel.Rotation, target, MotionTokenId.Reveal, from: target);
                    return;
                }
                if (last.Value == target) return;   // a change that moved neither the angle nor the item
                last.Value = target;
                // A mid-flight toggle retargets from the LIVE angle instead of restarting.
                anim.SeedValue(node.Value, AnimChannel.Rotation, target, MotionTokenId.Reveal);
            });

            return new BoxEl
            {
                Width = _size, Height = _size, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                OnRealized = h => node.Value = h,
                Children = [Icon(_glyph, _size) with { Color = _accent ? Prop.Of(() => _open() ? Tok.AccentTextPrimary : Tok.TextSecondary) : (Prop<ColorF>)Tok.TextTertiary }],
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
        public const float PillW = SidebarRowGeometry.PillW, PillH = SidebarRowGeometry.PillH;

        readonly PaneView _owner;
        readonly Func<SidebarPillState> _state;
        readonly Prop<float> _opacity;
        readonly SidebarPillLane _lane;
        NodeHandle _self;
        string? _route;

        public SelectionPill(PaneView owner, Func<SidebarPillState> state, SidebarPillLane lane = SidebarPillLane.List)
        {
            _owner = owner;
            _state = state;
            _lane = lane;
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
                _owner.RegisterSelectionPill(route, _self, _lane);
                // A recycled node may inherit an interrupted transform from the route previously bound to the slot; snap
                // to the same visibility the bound channel reads, so the snap can never disagree with it.
                if (recycled) NavigationSelectionMotion.SnapVertical(anim, _self, selected);
            }, DepKey.From(routeHash));

            return new BoxEl
            {
                Width = PillW,
                Height = PillH,
                Margin = new Edges4(state.Indent, state.Top, 0f, 0f),
                Corners = CornerRadius4.All(SidebarRowGeometry.PillRadius),
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

        public static Element Number(int count) => global::Wavee.Design.Type.MicroMeta(FormatCache.Int(count)) with
        {
            Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f,
        };

        public static Element Pending() => new BoxEl
        {
            Width = PlateW, Height = PlateH, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
        };

        /// <summary>A library shortcut's LIVE count as its own 11-DIP component (W3-A2): the relation's publish signal
        /// is subscribed inside a <c>UseComputed</c> whose value is <see cref="ShortcutCount.Stamp"/> — scope epoch,
        /// edge state, total — so a membership answer that left the number alone resolves the memo EQUAL and renders
        /// nothing; only a moved count (a like, a save, a page landing) re-renders this badge, and never its row. Keyed by
        /// kind: the route row it sits in is keyed by its route, so a slot recycling onto another shortcut remounts the
        /// row and this with it — the kind captured in the factory can never go stale.</summary>
        public static Element Live(LibraryEdgeKind kind)
            => Embed.Comp(() => new CountHost(kind)) with { Key = ShortcutCount.KeyOf(kind) };

        sealed class CountHost : Component
        {
            readonly LibraryEdgeKind _kind;
            readonly Func<ShortcutCount.Stamp> _stamp;

            public CountHost(LibraryEdgeKind kind)
            {
                _kind = kind;
                _stamp = Stamp;
            }

            /// <summary>The gate's compute: the scope epoch FIRST (G-179 — the welcome switch re-points every table read
            /// below), then the relation's publish counter (the wake), folded into what the badge paints.</summary>
            ShortcutCount.Stamp Stamp()
            {
                uint epoch = Entities.ScopeEpoch.Value;
                var relation = User.Relation(_kind);
                _ = relation.Changed.Value;
                var me = User.Me;
                return new ShortcutCount.Stamp(epoch, me.State(_kind), relation.Total(me.Slot));
            }

            public override Element Render() => Badge(ShortcutCount.Shown(UseComputed(_stamp).Value));
        }
    }

    /// <summary>The PURE half of the library-shortcut count badge (W3-A2): which relation a shortcut route counts, what
    /// the badge shows for a relation state, and the stamp the live badge gates its render on. Public so the rules are
    /// pinned by facts (<c>SidebarWiringTests</c>) without an engine.</summary>
    public static class ShortcutCount
    {
        /// <summary>Exactly what the badge paints: an UNKNOWN state is the pending plate whatever the total says, any other
        /// state is the total. The epoch is part of the value so a scope switch that lands on the same number still
        /// re-points the memo at the new scope's signals (its read of <c>ScopeEpoch</c> is the wake; the equality is what
        /// decides the render).</summary>
        public readonly record struct Stamp(uint Epoch, EdgeState State, int Total);

        /// <summary>The relation a shortcut route counts, or null for a route that carries no count (Local files, Home…).</summary>
        public static LibraryEdgeKind? KindOf(string routeKey) => routeKey switch
        {
            "albums" => LibraryEdgeKind.SavedAlbums,
            "artists" => LibraryEdgeKind.FollowedArtists,
            "liked" => LibraryEdgeKind.Liked,
            "podcasts" => LibraryEdgeKind.SavedShows,
            _ => null,
        };

        /// <summary>The number the badge shows, or null for the pending plate: the server TOTAL while a relation pages in
        /// (so the number never climbs), nothing while nobody has answered.</summary>
        public static int? Shown(EdgeState state, int total) => state == EdgeState.Unknown ? null : total;

        /// <inheritdoc cref="Shown(EdgeState, int)"/>
        public static int? Shown(in Stamp stamp) => Shown(stamp.State, stamp.Total);

        /// <summary>The live badge's reconciler key — one literal per kind, so a route row's trailing slot never allocates
        /// a key string per render.</summary>
        public static string KeyOf(LibraryEdgeKind kind) => kind switch
        {
            LibraryEdgeKind.SavedAlbums => "count:albums",
            LibraryEdgeKind.FollowedArtists => "count:artists",
            LibraryEdgeKind.Liked => "count:liked",
            LibraryEdgeKind.SavedShows => "count:podcasts",
            _ => "count:other",
        };
    }

    /// <summary>The explicit shimmer shapes (a streaming list has no seed rows to derive a skeleton from). Sized by the
    /// same ladders the real row uses, so the shimmer→content swap never changes a section's height. (The rail's pending
    /// tiles are not here: they are the media surface's own seed face — <c>Rail.SeedStack</c>.)</summary>
    internal static class Skeletons
    {
        /// <summary>One pending row in the row's own ladder: the row's icon column (the art centred in its 32 slot at slot
        /// x 10), the label at slot x 50, bars 140×12 (+ 80×10 at gap 4 in a two-line row), r4. Uniform by design
        /// (<paramref name="index"/> is position-independent).</summary>
        public static Element Row(int index, SidebarRowShape shape, float heightOverride = float.NaN,
                                  float artOverride = float.NaN)
        {
            bool subtitle = shape == SidebarRowShape.EntityTwoLine;
            float height = float.IsNaN(heightOverride) ? SidebarRowGeometry.HeightOf(shape) : heightOverride;
            float art = float.IsNaN(artOverride) ? SidebarRowGeometry.ArtOf(shape) : artOverride;
            Element text = subtitle
                ? new BoxEl { Direction = 1, Grow = 1f, Gap = 4f, Children = [Bar(140f, 12f), Bar(80f, 10f)] }
                : new BoxEl { Direction = 1, Grow = 1f, Children = [Bar(140f, 12f)] };
            return new BoxEl
            {
                Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = SidebarRowGeometry.LabelGap,
                Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY),
                Padding = new Edges4(0f, 0f, SidebarRowGeometry.TrailingPad, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Width = SidebarRowGeometry.IconColumn, Height = height, Shrink = 0f,
                        Padding = new Edges4(SidebarRowGeometry.LeadInset, 0f, 0f, 0f),
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = art, Height = art, Corners = CornerRadius4.All(Cover.Radius(art, false)),
                                Fill = Tok.FillSubtleSecondary,
                            },
                        ],
                    },
                    text,
                ],
            };
        }

        static Element Bar(float w, float h) => new BoxEl
        {
            Width = w, Height = h, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary,
        };
    }

    /// <summary>The 40-px section header (design V.6): the title at slot x 10 (pane 14, the covers' left edge), 14 SemiBold TextSecondary, brightening
    /// to TextPrimary on hover (no plate); then the ⋯ (revealed on hover, permanent after a touch), the "+" (always, Playlists /
    /// Your Library), and the chevron (24×24, always visible, ending at pane W − 4). A click anywhere collapses; the slot root
    /// is the roving focus stop (ToggleButton). <paramref name="onToggle"/> null ⇒ a heading with no chevron.</summary>
    internal static class SectionHeader
    {
        public const float Height = SidebarRowGeometry.HeaderHeight;

        public static Element Header(string title, bool open, Action<bool>? onToggle, Element? create, Element? more,
                                     Element? chevron)
        {
            var kids = new List<Element>(5)
            {
                new TextEl(title)
                {
                    Size = 14f, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary,
                    Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap,
                },
                new BoxEl { Grow = 1f },
            };
            // The hover-revealed ⋯ goes BEFORE the "+": at rest an invisible ⋯ between them read as a hole beside the chevron.
            if (more is not null) kids.Add(more);
            if (create is not null) kids.Add(create);
            if (onToggle is not null) kids.Add(chevron ?? Icon(open ? Icons.ChevronUp : Icons.ChevronDown, 12f, Tok.TextSecondary));
            Action? click = null;
            if (onToggle is { } toggle) click = () => toggle(!open);
            return new BoxEl
            {
                Direction = 0, Height = SidebarRowGeometry.HeaderHeight, AlignItems = FlexAlign.Center, Gap = 0f,
                Padding = new Edges4(SidebarRowGeometry.HeaderTextX, 0f, 0f, 0f),
                Role = onToggle is null ? AutomationRole.None : AutomationRole.ToggleButton,
                Cursor = onToggle is null ? CursorId.Arrow : CursorId.Hand,
                OnClick = click,
                Children = [.. kids],
            };
        }

        /// <summary>A header's 24×24 inline button (⋯ / +): consumes its own click, hover plate FillSubtleSecondary.
        /// <paramref name="reveal"/> = hover-revealed (the ⋯ off touch). The result is an OUTER, handler-less reveal box
        /// (Opacity / HoverOpacity follow the header's hover) around the INNER interactive button: the plate belongs to the
        /// inner node alone, so hovering the header text reveals the glyph at its rest look and only hovering the button
        /// paints the plate (one node carrying both the reveal and <c>Interactive</c> let the container hover drive the
        /// fill). A <see cref="BoxEl"/> so the caller can attach a context menu to the outer box (a context request walks up
        /// from the button); <paramref name="requestsContext"/> makes the click re-enter that funnel, anchored at the
        /// button. <paramref name="onRealized"/> / <paramref name="focusable"/> apply to the INNER button, the real focus
        /// stop and anchor. The caller wraps the result in <c>ToolTip.Wrap</c> for its tooltip and accessible name.</summary>
        public static BoxEl InlineButton(string glyph, Action? onClick, bool reveal, Action<NodeHandle>? onRealized = null,
                                         bool focusable = false, bool requestsContext = false)
            => new BoxEl
            {
                Width = SidebarRowGeometry.HeaderButton, Height = SidebarRowGeometry.HeaderButton, Shrink = 0f,
                Opacity = reveal ? 0f : 1f, HoverOpacity = 1f,
                Children =
                [
                    new BoxEl
                    {
                        Width = SidebarRowGeometry.HeaderButton, Height = SidebarRowGeometry.HeaderButton,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                        Role = AutomationRole.Button, Cursor = CursorId.Hand,
                        OnClick = requestsContext ? null : onClick, ClickRequestsContext = requestsContext,
                        Focusable = focusable, OnRealized = onRealized,
                        BlocksDragArm = true,
                        Children = [Icon(glyph, 12f, Tok.TextSecondary)],
                    }.Interactive(Interaction.Subtle),
                ],
            };

        /// <summary>The 8-px separator (TR:223, TR:247): a 1-px StrokeDividerDefault rule at y 3, bleeding over the list's
        /// 4-px inset so it spans the full pane width.</summary>
        public static Element Separator() => new BoxEl
        {
            Direction = 1, Height = SidebarRowGeometry.SeparatorHeight, Shrink = 0f,
            Margin = new Edges4(-SidebarRowGeometry.PaneEdge, 0f, -SidebarRowGeometry.PaneEdge, 0f),
            Padding = new Edges4(0f, SidebarRowGeometry.SeparatorLineTop, 0f, 0f),
            HitTestVisible = false,
            Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }],
        };
    }

    // ══ 5. THE "+" ═════════════════════════════════════════════════════════════════════════════

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
            // The reveal sits on an OUTER handler-less box (as in SectionHeader.InlineButton): on the interactive button
            // itself the container hover would drive its plate, so hovering the row alone painted the "+" plate.
            Element shown = box;
            if (_revealOpacity is { } reveal)
                shown = new BoxEl
                {
                    Width = _box, Height = _box, Shrink = 0f,
                    Opacity = Prop.Of(reveal), HoverOpacity = 1f,
                    Children = [box],
                };

            return ToolTip.Wrap(shown, Loc.Get(_menu is null
                ? Strings.Sidebar.CreatePlaylistTooltip
                : Strings.Sidebar.CreateTooltip));
        }
    }

    // ══ 8. THE PANE'S TEXT, LOC AND GLYPH TABLES ═════════════════════════════════════════════════════════════════════

    /// <summary>The renderer's display rules: section titles, per-kind subtitles, icon fallbacks and the
    /// "never render a blank row" degradations — split out so the row builders stay about LAYOUT.
    /// <para>Public, not internal: this assembly has no <c>InternalsVisibleTo</c> (see <c>Playlist.UI.cs</c>'s own
    /// note on the same pattern), so <c>Wavee.Tests</c> can only pin <see cref="SubtitleOf"/>'s bug A1 contract — an
    /// unknown COUNT renders no subtitle, never "0 songs" — by calling the real method.</para></summary>
    public static class PaneText
    {
        /// <summary>The section's title: its kind's loc key (a section has no rename).</summary>
        public static string TitleOf(SidebarSection section)
            => SidebarCatalogue.TitleKeyOf(section.Kind) is { } key ? Loc.Get(key) : "";

        /// <summary>The subtitle text: the grammar is <see cref="SidebarSubtitleRules.Of"/> (data), the words are the loc table's.
        /// Playlist → "N songs" (or "N items" with an episode, "N episodes" for Your Episodes) · album → "Album · first artist" · artist → "Artist" · show →
        /// "Podcast · publisher" · folder → "N items" · track → its artist · route → a concert's venue (its Creator). An
        /// UNKNOWN count is null, never "0 songs" (bug A1).</summary>
        public static string? SubtitleOf(in SidebarLibraryEntry e)
        {
            var s = SidebarSubtitleRules.Of(in e);
            return s.Kind switch
            {
                SidebarSubtitleKind.Songs => Strings.Sidebar.SongCount(s.Count),
                SidebarSubtitleKind.Items => Strings.Sidebar.V3.ItemCount(s.Count),
                SidebarSubtitleKind.Episodes => Strings.Podcast.EpisodeCount(s.Count),
                SidebarSubtitleKind.Album => s.Detail.Length > 0
                    ? Loc.Get(Strings.Sidebar.V3.Kind.Album) + " · " + s.Detail
                    : Loc.Get(Strings.Sidebar.V3.Kind.Album),
                SidebarSubtitleKind.Podcast => s.Detail.Length > 0
                    ? Loc.Get(Strings.Sidebar.V3.Kind.Show) + " · " + s.Detail
                    : Loc.Get(Strings.Sidebar.V3.Kind.Show),
                SidebarSubtitleKind.Artist => Loc.Get(Strings.Sidebar.V3.Kind.Artist),
                SidebarSubtitleKind.Text => s.Detail,
                _ => null,
            };
        }

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

        /// <summary>The last resort when nothing names a row: the uri's / key's final segment. Never blank, never a crash —
        /// a hand-edited document must render something the user can right-click and remove.</summary>
        public static string ShortUri(string? uri)
        {
            if (string.IsNullOrEmpty(uri)) return "—";
            int at = uri.LastIndexOf(':');
            return at >= 0 && at + 1 < uri.Length ? uri[(at + 1)..] : uri;
        }

        /// <summary>The EMPTY copy for a section that resolved to zero rows, per kind (never a borrowed debug string).</summary>
        public static string EmptyText(SidebarSectionKind kind) => kind switch
        {
            SidebarSectionKind.NewReleases => Loc.Get(PaneLoc.NewReleasesEmpty),
            _ => Loc.Get(PaneLoc.SectionEmpty),
        };
    }

    /// <summary>The pane renderer's loc KEYS as literals, in one place. Every key exists in <c>assets/loc/en-US.json</c>;
    /// a typo renders loudly as <c>[key]</c>.</summary>
    internal static class PaneLoc
    {
        public const string NewReleasesEmpty = "sidebar.newReleases.empty";
        public const string SectionEmpty = "sidebar.section.empty";
        public const string LibraryEmpty = "sidebar.v3.empty.library";
        public const string SearchEmpty = "sidebar.v3.empty.search";
        /// <summary>A section header's ⋯ button (its tooltip / accessible name).</summary>
        public const string SectionOptions = "sidebar.header.options";
    }

    /// <summary>The compact-rail tile glyphs (design V.9).</summary>
    internal static class PaneIcon
    {
        /// <summary>A collapsed section's compact-rail tile glyph (design V.9), by the section's kind.</summary>
        public static string SectionGlyph(SidebarSectionKind kind) => kind switch
        {
            SidebarSectionKind.Pinned => Icons.Pin,
            SidebarSectionKind.Collections => Icons.Library,
            SidebarSectionKind.Playlists => Icons.MusicNote,
            SidebarSectionKind.Recent => Icons.History,
            SidebarSectionKind.NewReleases => Icons.Album,
            _ => Icons.List,
        };
    }
}
