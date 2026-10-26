// ── Themes/WinUi/Structure/Views/Discovery/Browse/WinUiBrowseCells.cs — WinUI's Browse cells (themes plan 6.2; P6 W5) ────────────────────
//
// Boards W-Browse (-Compact / -Small / -Huge), W-BrowseCategory: the directory's destinations are COLOUR TILES (the category's own colour is the fill, the ink
// the on-media white), in three densities:
//
//   Top     a 112-high tile (200 at Huge): a 40 black-wash icon box over the 20/28 semibold word, both on the tile's foot
//   ForYou  a 56-high card chip: the category's 16 dot, then the word at 14/20 semibold (44 at Compact, where the chips are one scrolling strip)
//   Tile    a 88-high tile (120 at Huge) with its word at 16/20 semibold on the foot: Genres, Mood & activity and the long tail ("Podcasts & audiobooks")
//
// Shared by the directory's bands, a category page's tile blocks and the Search page's genre links. EVERY CELL IS A LINK: Role Hyperlink, Focusable, Hand, a focus
// margin, keyed by its destination's own uri (no width in a key: the only width in a key is the grid's column count), a hover plate in the stock subtle fill. No
// menu, no drag: a browse category is a link, full stop. The numbers are `Rules\WinUiBrowseRules.cs`, the column ladders `Rules\Tiers.Discovery.Browse.cs`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's Browse cells.</summary>
public static class WinUiBrowseCells
{
    /// <summary>Top: the colour tile with the icon box over its word.</summary>
    internal static Element Top(BrowseTileModel m, WinUiBrowseLayout layout)
    {
        float gap = layout.Tier == WidthTier.Huge ? WinUiBrowseRules.TopDiscGapHuge : WinUiBrowseRules.TopDiscGap;
        return Plate(in m, layout.TopH, layout.TopPad, new BoxEl
        {
            Direction = 1, Gap = gap, MinWidth = 0f, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Width = layout.TopDisc, Height = layout.TopDisc, Shrink = 0f, Corners = CornerRadius4.All(Radii.Control),
                    Fill = WinUiBrowseRules.DiscFill, HitTestVisible = false,
                },
                R.Type.ModuleHeader(m.Title) with
                {
                    Color = R.OnMedia.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        });
    }

    /// <summary>For you: the card chip with the category's dot and its word.</summary>
    internal static Element Chip(BrowseTileModel m, WinUiBrowseLayout layout) => new BoxEl
    {
        Key = m.Uri, Layout = WinUiDiscoveryMotion.Shove,
        Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
        FocusVisualMargin = Design.FocusInsetBordered,
        OnClick = m.Open, MinWidth = 0f, Height = layout.ForYouH,
        Direction = 0, Gap = WinUiBrowseRules.ChipGap, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WinUiBrowseRules.ChipPad, 0f, WinUiBrowseRules.ChipPad, 0f),
        Corners = Radii.CardAll, Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new BoxEl { Width = WinUiBrowseRules.ChipDot, Height = WinUiBrowseRules.ChipDot, Shrink = 0f, Corners = Radii.PillAll, Fill = Accent(in m), HitTestVisible = false },
            BodyStrong(m.Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
        ],
    };

    /// <summary>A destination tile: the colour fill, the word on its foot (16/20 semibold).</summary>
    internal static Element Tile(BrowseTileModel m, WinUiBrowseLayout layout)
        => Plate(in m, layout.TileH, layout.TilePad, R.Type.TrackTitle(m.Title) with
        {
            Size = WinUiBrowseRules.TileTitleSize, LineHeight = WinUiBrowseRules.TileTitleLine, Weight = 600, Color = R.OnMedia.Ink,
            MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        });

    /// <summary>The colour plate: fill = the category colour, r8, the content on its foot (a column justified End), the stock hover wash over the colour (the
    /// fill's own overlay), the link's focus ring.</summary>
    static Element Plate(in BrowseTileModel m, float height, float pad, Element content) => new BoxEl
    {
        Key = m.Uri, Layout = WinUiDiscoveryMotion.Shove,
        Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
        FocusVisualMargin = Design.FocusInsetBordered,
        OnClick = m.Open, MinWidth = 0f, Height = height,
        Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Stretch,
        Padding = Edges4.All(pad), ClipToBounds = true,
        Corners = Radii.CardAll, Fill = Accent(in m), HoverOpacity = 0.9f,
        Children = [content],
    };

    /// <summary>ONE accent resolution for every cell: a colour ⇒ raw; null or 0 ⇒ the semantic accent (never black).</summary>
    static ColorF Accent(in BrowseTileModel m) => m.Color is { } c && c != 0u ? ColorScience.ToColor(c) : Tok.AccentDefault;

    /// <summary>The tile grid (Genres, Mood &amp; activity, the long tail; a category page's blocks; Search's genre links): the engine's own auto-fill at the tier's
    /// floor and cap, ONE key at every count so a width change moves the same cells (each keyed by its destination) into their new tracks (the cells FLIP).</summary>
    internal static Element TileGrid(IReadOnlyList<BrowseTileModel> items, WinUiBrowseLayout layout, string key = TileGridKey)
    {
        var cells = new Element[items.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = Tile(items[i], layout);
        return Cells(layout.Tiles, layout.TileGap, layout.TileGap, layout.TileH, cells) with { Key = key };
    }

    /// <summary>The tile grid's default key.</summary>
    internal const string TileGridKey = "browse-tile-grid";

    /// <summary>An auto-fill grid at a band's floor and cap (a fixed band is its count): <paramref name="rowH"/> the cells' height, 0 for the cells' own.</summary>
    internal static GridEl Cells(WinUiGridBand band, float colGap, float rowGap, float rowH, Element[] cells) => new()
    {
        MinColWidth = MathF.Max(1f, band.MinCol), MaxColumns = band.MaxCols, ColGap = colGap, RowGap = rowGap,
        RowHeight = rowH > 0f ? rowH : float.NaN, Children = cells,
    };

    /// <summary>A grid / related block (0.2.9 <c>BrowsePage.CategoryBlock</c>): a LABEL header over the tile grid; an untitled block is the bare grid with no
    /// header row and no header gap.</summary>
    internal static Element CategoryBlock(string key, string? title, IReadOnlyList<BrowseTileModel> tiles, WinUiBrowseLayout layout)
    {
        Element grid = TileGrid(tiles, layout);
        if (string.IsNullOrWhiteSpace(title)) return grid with { Key = key };
        return new BoxEl
        {
            Key = key, Direction = 1, Gap = layout.HeaderGap, MinWidth = 0f,
            Children = [BandHeading(title), grid],
        };
    }

    /// <summary>A band heading: the Subtitle rung 20/28 semibold (W-Browse: `h2`).</summary>
    internal static TextEl BandHeading(string title)
        => R.Type.ModuleHeader(title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>"Explore all categories": the accent link that returns to the directory. On every mode, including the empty and error arms.</summary>
    internal static Element ExploreAll(Action? open) => new BoxEl
    {
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        AlignSelf = FlexAlign.Start, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
        FocusVisualMargin = Design.FocusInsetBordered,
        OnClick = open,
        Children = [Body(Loc.Get(Strings.Browse.ExploreAll)) with { Color = Tok.AccentTextPrimary }],
    };

    /// <summary>The outlined cell that closes For you at Huge (W-Browse-Huge): the same link as a 56-high bordered cell.</summary>
    internal static Element ExploreCell(Action? open) => new BoxEl
    {
        Key = "browse-explore-cell", Layout = WinUiDiscoveryMotion.Shove,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, FocusVisualMargin = Design.FocusInsetBordered,
        OnClick = open, MinWidth = 0f, Height = WinUiBrowseRules.ExploreH,
        Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
        Corners = Radii.CardAll, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, HoverFill = Tok.FillSubtleSecondary,
        Children = [Body(Loc.Get(Strings.Browse.ExploreAll))],
    };
}
