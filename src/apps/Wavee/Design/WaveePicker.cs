using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

// Wavee's ONE preview-card radio. Four pickers had grown drifted copies of the same four things — the accent/neutral
// ink pair, the card shell, the selected-label treatment, and the wrapping strip: Settings → row density, Settings →
// track page layout, Settings → palette, and the sidebar design chooser (which is ALSO the fresh-install dialog). The
// differences that actually matter are the wireframe drawn inside a card and its footprint, so those are the
// parameters and everything else lives here.
//
// FOUR PIECES, NOT ONE RECORD. A palette swatch is a 30-DIP circle, not a card — forcing it through a card shell would
// be worse than the duplication it removes. So each picker composes only what it genuinely shares: all four take
// Strip + Label, three take Ink, two take Tile.
//
// WHY Strip IS THE POINT. It delegates group behaviour to FluentGpu.Controls.RadioButtons instead of leaving every
// card its own tab stop, which closes the gap SidebarDesignPicker documented as an engine follow-up ("Element has no
// radio-GROUP role and no arrow-key group traversal"): ONE tab stop per picker that lands on the current value,
// Up/Down/Left/Right roving, selection following focus (Ctrl+arrow to move without applying), Space to select. The two
// things RadioButtons was missing for this — a glyph-less item and a wrapping container — are now
// RadioButton.Style.ShowGlyph and RadioButtons.PartGrid/PartColumn.
static class WaveePicker
{
    /// <summary>The accent/neutral ink pair every miniature tints itself with: <c>Block</c> for the solid shapes
    /// (covers, tiles, pills), <c>Faint</c> for the skeleton bars behind them. The selected card tints its WHOLE
    /// wireframe, so the choice reads from across the page and not only from the border.</summary>
    public readonly record struct Ink(ColorF Block, ColorF Faint)
    {
        public static Ink For(bool on) => on
            ? new(Tok.AccentDefault, Tok.AccentDefault with { A = 0.45f })
            : new(Tok.AccentDefault with { A = 0.58f }, Tok.AccentDefault with { A = 0.22f });
    }

    /// <summary>A card footprint. <paramref name="Inset"/> is the RESTING padding: a selected card spends 1 DIP of it
    /// on the 1→2 border growth, so the border draws INWARD and the wireframe never shifts by a pixel.
    /// <paramref name="Height"/> may be <see cref="float.NaN"/> for a content-sized card; <paramref name="Gap"/> is
    /// the spacing between the card's own stacked children.</summary>
    public readonly record struct Shell(float Width, float Height, float Inset, float Gap);

    /// <summary>The settings wireframe tile — row density, track page layout.</summary>
    public static readonly Shell Tile = new(116f, 84f, 8f, 4f);
    /// <summary>The sidebar design card as it appears in Settings, where it shares a page column.</summary>
    public static readonly Shell PaneCompact = new(200f, float.NaN, 10f, 7f);
    /// <summary>The sidebar design card at full size — the fresh-install chooser.</summary>
    public static readonly Shell Pane = new(224f, float.NaN, 10f, 7f);
    /// <summary>The Liked Songs cover-style thumbnail: a 76-DIP square miniature inside the shared card shell (92
    /// wide, 8 of resting inset on each side). Content-sized in height — the miniature IS the card's content, and the
    /// label sits under the card rather than in it (<see cref="Titled"/>).</summary>
    public static readonly Shell CoverMini = new(92f, float.NaN, 8f, 5f);
    /// <summary>A wide horizontal row card — the setup wizard's sidebar-design chooser
    /// (<c>SidebarDesignPicker.Rows</c>), where the picker reads as a vertical LIST of full-width rows rather than a
    /// strip of square cards.</summary>
    public static readonly Shell WideRow = new(480f, 100f, 8f, 0f);

    /// <summary>The card shell: fill, radius, the accent border that grows inward on selection, the subtle
    /// hover/press scale. Deliberately carries NO <c>Role</c>/<c>Focusable</c>/<c>OnClick</c> — inside
    /// <see cref="Strip"/> the RadioButtons item root owns all three, and a second radio role here would announce the
    /// card twice. <c>ClipToBounds</c> is on: a miniature that outgrows its card must be cut, not painted over its
    /// neighbours (the failure mode that produced the overlapping Sidebar header).</summary>
    public static BoxEl Card(bool on, in Shell s, params Element[] body) => new()
    {
        Width = s.Width,
        Height = s.Height,
        Shrink = 0f,
        Direction = 1,
        Gap = s.Gap,
        Padding = Edges4.All(on ? s.Inset - 1f : s.Inset),
        ClipToBounds = true,
        Corners = CornerRadius4.All(Radii.Card),
        Fill = on ? Tok.AccentSubtle : Tok.FillCardDefault,
        HoverFill = on ? WaveeColors.SelectedHover : Tok.FillCardSecondary,
        PressedFill = on ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
        BorderWidth = on ? 2f : 1f,
        BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
        HoverScale = WaveeMotion.ScaleSubtle.Hover,
        PressScale = WaveeMotion.ScaleSubtle.Press,
        Cursor = CursorId.Hand,
        Children = body,
    };

    /// <summary>The selected-label treatment: the current choice goes semibold and primary, the rest stay regular and
    /// secondary — so the selection survives a colour-blind read of the accent border.</summary>
    public static TextEl Label(string text, bool on, float size = 12f) => new(text)
    {
        Size = size,
        LineHeight = size + 4f,
        Weight = (ushort)(on ? 600 : 400),
        Color = on ? Tok.TextPrimary : Tok.TextSecondary,
        MaxLines = 1,
        Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The one row-density miniature used by Settings and fresh setup. Its proportions come from the real
    /// <see cref="TrackRow"/> geometry, compressed by one scale factor; the two surfaces therefore cannot drift into
    /// different explanations of Compact / Default / Cozy / Comfortable. The bars stay native so they inherit the
    /// live theme and the card's selected-state ink instead of embedding a screenshot.</summary>
    public static Element DensityRows(int density, bool on)
    {
        const float PreviewScale = 0.25f;
        var ink = Ink.For(on);
        float rowHeight = TrackRow.RowHeightFor(density) * PreviewScale;
        float artworkEdge = TrackRow.ThumbSize * PreviewScale;

        Element Bar(float grow, bool strong = false) => new BoxEl
        {
            Grow = grow,
            Basis = 0f,
            MinWidth = 0f,
            Height = Spacing.XXS,
            Corners = Radii.PillAll,
            Fill = strong ? ink.Block : ink.Faint,
        };

        Element Row() => new BoxEl
        {
            Height = rowHeight,
            Shrink = 0f,
            Direction = 0,
            Gap = Spacing.XS,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
            Children =
            [
                new BoxEl
                {
                    Width = artworkEdge,
                    Height = artworkEdge,
                    Shrink = 0f,
                    Corners = Radii.ControlAll,
                    Fill = ink.Block,
                },
                Bar(1f, strong: true),
                new BoxEl
                {
                    Width = Spacing.XXL,
                    Height = Spacing.XXS,
                    Shrink = 0f,
                    Corners = Radii.PillAll,
                    Fill = ink.Faint,
                },
                new BoxEl
                {
                    Width = Spacing.L,
                    Height = Spacing.XXS,
                    Shrink = 0f,
                    Corners = Radii.PillAll,
                    Fill = ink.Faint,
                },
            ],
        };

        return new BoxEl
        {
            Direction = 1,
            Gap = Spacing.XXS,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            MinHeight = 0f,
            AlignSelf = FlexAlign.Stretch,
            Justify = FlexJustify.Center,
            Children = [Row(), Row(), Row()],
        };
    }

    /// <summary>A tinted bar segment for a wireframe row — <paramref name="strong"/> picks the identity ink
    /// (<see cref="Ink.Block"/>) over the skeleton ink (<see cref="Ink.Faint"/>), <paramref name="height"/> the bar's
    /// thickness (and, via <see cref="Radii.Circle"/>, its pill radius). Promoted out of
    /// <c>SettingsPage.General.TrackListStyleCards</c> so <see cref="ModernRow"/>/<see cref="ClassicRow"/> can share it
    /// with any other row-wireframe miniature (the setup wizard's Appearance stage) instead of a second hand-copy.</summary>
    public static Element Bar(float grow, Ink ink, bool strong = false, float height = Spacing.XXS) => new BoxEl
    {
        Grow = grow, Basis = 0f, MinWidth = 0f, Height = height,
        Corners = Radii.Circle(height), Fill = strong ? ink.Block : ink.Faint,
    };

    /// <summary>The "Modern" track-row wireframe: a square art tile beside a title+subtitle bar pair (or, with
    /// <paramref name="twoLine"/> false, a single bar) — the art-led stacked-row grammar every Modern-style track list
    /// uses. <paramref name="art"/> is the tile's edge; <paramref name="height"/> the row's own height (both default
    /// to the Settings-tab miniature's original 20/16 so existing callers are pixel-identical).</summary>
    public static Element ModernRow(Ink ink, float height = Spacing.XL, float art = Spacing.L, bool twoLine = true) => new BoxEl
    {
        Height = height, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = Radii.ControlAll, Fill = ink.Faint,
        Children =
        [
            new BoxEl { Width = art, Height = art, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block },
            twoLine
                ? new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children = [Bar(1f, ink, strong: true), Bar(0.65f, ink)],
                }
                : Bar(1f, ink, strong: true),
        ],
    };

    /// <summary>The "Classic" track-row wireframe: three aligned text-lane bars over a hairline — the three-lane grid
    /// + divider grammar every Classic-style track list uses.</summary>
    public static Element ClassicRow(Ink ink, float height = Spacing.XL) => new BoxEl
    {
        Height = height, Direction = 1,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Grow = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
                Children = [Bar(1f, ink, strong: true), Bar(0.75f, ink), Bar(0.75f, ink)],
            },
            new BoxEl { Height = 1f, Fill = ink.Faint },
        ],
    };

    /// <summary>A card (or swatch) over its label — the shape three of the four pickers want. Returns a
    /// <see cref="BoxEl"/> so a caller can <c>with</c>-adjust it (the palette column pins its own width).</summary>
    public static BoxEl Titled(Element card, string label, bool on, float gap = Spacing.S, float labelSize = 12f) => new()
    {
        Direction = 1,
        Gap = gap,
        AlignItems = FlexAlign.Center,
        Children = [card, Label(label, on, labelSize)],
    };

    // ── the strip ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The glyph-less radio: the CARD is the control, so the ring/dot column is not built at all and the item
    /// root shrinks to its content. The focus ring insets 2 DIP (WinUI's −7,−3 would draw it through the card's own
    /// border).</summary>
    static readonly RadioButton.Style s_bare = RadioButton.DefaultStyle with
    {
        ShowGlyph = false,
        MinWidth = 0f,
        MinHeight = 0f,
        ContentGap = 0f,
        FocusVisualMargin = Edges4.All(2f),
    };

    /// <summary>Container styling for the items grid. RadioButtons lays items out column-major and never wraps (WinUI's
    /// ColumnMajorUniformToLargestGridLayout has no wrap state); a strip of fixed-width preview cards has to drop to
    /// fewer columns on a narrow window instead of overflowing, so the grid wraps and the columns hold their width.</summary>
    static readonly TemplateParts s_strip = new()
    {
        [RadioButtons.PartGrid] = g => g with { Wrap = true, Gap = Spacing.M },
        [RadioButtons.PartColumn] = c => c with { Shrink = 0f },
    };

    /// <summary>Mount <paramref name="count"/> cards as ONE radio group. <paramref name="item"/>(index, isSelected)
    /// builds each card; <paramref name="onChange"/> is the single apply path and fires on click AND on a keyboard
    /// rove (selection follows focus — the WinUI RadioButtons contract), so it must be safe to call repeatedly.
    ///
    /// <para><paramref name="maxColumns"/> defaults to <paramref name="count"/> — ONE row, which is what every
    /// settings-page picker wants and what this method did unconditionally before. Pass a smaller number for a picker
    /// whose cards do not fit on a line (the Liked cover flyout's 3-wide grid). Note what that actually arranges:
    /// RadioButtons is COLUMN-MAJOR (WinUI's ColumnMajorUniformToLargestGridLayout), so the items fill the first
    /// column top-to-bottom before starting the second — and the keyboard follows the same geometry, Up/Down stepping
    /// ±1 in DATA order (i.e. down a column) while Left/Right jump column to column at the same row. The default of
    /// one-item-per-column is the degenerate case of exactly that, which is why the two consumers cannot
    /// disagree.</para></summary>
    /// <param name="parts">Container template-part overrides — defaults to <see cref="s_strip"/> (wrap + gap, no
    /// column shrink). A caller whose picker reads as a vertical LIST rather than a wrapped horizontal strip (the
    /// setup wizard's sidebar-design chooser, one column of full-width rows) passes its own.</param>
    public static Element Strip(int count, int selected, Func<int, bool, Element> item, Action<int> onChange,
                                int? maxColumns = null, TemplateParts? parts = null)
        => RadioButtons.Create(
            count,
            i => item(i, i == selected),
            // A FRESH signal per render carrying the live value — NOT a mirror kept in step by a write-during-render
            // (the BackwardsWriteGuard's exact tripwire). RadioButtons re-pushes its props, so the throwaway is
            // re-seeded from the caller's truth every render and discarded; the real write happens in onChange. The
            // same contract the SelectorBar/ComboBox settings rows already rely on.
            selectedIndex: new Signal<int>(selected),
            onChange: onChange,
            // Default: one item per column ⇒ a single horizontal strip, so Left/Right and Up/Down both move ±1 in
            // data order. A caller-supplied count is clamped the same way, so 0 or a negative can never reach the grid.
            maxColumns: Math.Max(1, maxColumns ?? count),
            style: s_bare,
            parts: parts ?? s_strip);
}
