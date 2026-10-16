// ── Themes/Classic/Structure/Views/Pages/ClassicBrowseCells.cs — Classic's Browse cells (themes plan 6.1, T10a2) ───────────────────────────
//
// From `Entities\Browse.UI.cs` §1 and its category-page blocks: the five Browse cell densities (Word / Name / Link / Bar / Peek), the link grid, the star
// grid, the category block (a LABEL header over the link grid) and the "Explore all categories" link. Shared by the directory's bands, a category
// page's tile blocks and the Search page's genre links.
//
// ── FIVE DENSITIES, ONE PER BAND, CHEAPEST TO MOST EXPRESSIVE ────────────────────────────────────────────────────────
//
// Density is what the directory spends to say "here is how much this destination is". Top's four entry points need
// nothing but their own name on the stock link chip at 600 (Word); For you is one step down, a pip beside a name (Name); Genres runs
// ~25 deep, so it is a padded pip + secondary text link (Link); a mood IS its colour, so Mood & activity earns a card
// plate with the colour as a corner wash + tick (Bar); the long tail earns the same card weight plus a hanging cover
// (Peek). 0.2.9 `BrowseTiles.cs` verbatim, ported onto 0.3's tokens: `Design.Type` / `Design.Motion` / `Design.Palette`
// / `Controls.Artwork` replace `WaveeType` / `WaveeMotion` / `WaveePalette` / `Surfaces.Artwork`.
//
// THE CATEGORY COLOUR IS A WASH + A TICK, NEVER THE PLATE (ch 13 §0.11, §4.2). The Bar/Peek plates are the house card
// plate at the SAME elevation on hover; a full-bleed colour field forces on-media white ink and breaks the light theme.
// A 0 / absent colour is the semantic accent, never pure black.
//
// EVERY CELL IS A LINK: Role Hyperlink, Focusable, Hand, a 2-DIP focus margin, keyed by its destination's own uri. No
// menu, no drag — a browse category is a link, full stop (§6.3). The cell keys carry NO width (0.2.9's
// "browse-peek:<uri>:<cardW>" remounted every peek cell on a resize — ch 13 §9 ReuseGuard trap): the only width in a
// key is the column count of the grid that holds them. The numbers are `Rules\ClassicBrowseRules.cs`, the column ladders `Rules\Tiers.Browse.cs`.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Browse cells.</summary>
public static class ClassicBrowseCells
{
    /// <summary>Top: the stock link chip (<see cref="Controls.LinkChip"/>, 32/r4, D7), the only one at 600 — primary without being set
    /// larger than the band heading that names it.</summary>
    internal static Element Word(BrowseTileModel m, ClassicBrowseLayout layout)
        => Controls.LinkChip(m.Uri, m.Title, m.Open, strong: true, minHeight: layout.TopChipH, cornerRadius: layout.TopChipRadius, padX: layout.TopChipPadX);

    /// <summary>For you: the same chip with the identity pip its detail page carries.</summary>
    internal static Element Name(BrowseTileModel m, ClassicBrowseLayout layout)
        => Controls.LinkChip(m.Uri, m.Title, m.Open, Pip(in m), minHeight: layout.ForYouChipH);

    /// <summary>Genres — and Search's genre results (ch 13 §0.15): the pip beside Body secondary, one rung below Name
    /// because a genre column runs ~25 deep. The padded hit target and its hover fill are what make one row easy to click
    /// without catching its neighbour; a search genre passes <c>Color: null</c> ⇒ an accent pip.</summary>
    internal static Element Link(BrowseTileModel m, ClassicBrowseLayout layout) => new BoxEl
    {
        Key = m.Uri, MinHeight = layout.LinkMinH,
        Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
        FocusVisualMargin = Design.FocusInsetBordered,
        OnClick = m.Open, MinWidth = 0f,
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, Spacing.XS),
        Corners = CornerRadius4.All(Radii.Control),
        HoverFill = Tok.FillControlSecondary,
        Children =
        [
            Pip(in m),
            Body(m.Title).Secondary() with
            {
                HoverColor = Tok.AccentTextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        ],
    };

    /// <summary>Mood &amp; activity: the house card plate (HomeFoldTile's own fill / stroke / elevation) with the category
    /// colour demoted to a right-edge radial wash plus the left tick. Hover swaps the fill at the SAME elevation — never a
    /// lift. Ink is <c>Tok.TextPrimary</c>: the plate is a theme surface, not on-media art.</summary>
    internal static Element Bar(BrowseTileModel m)
    {
        ColorF seed = Accent(in m);
        return new BoxEl
        {
            Key = m.Uri,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = m.Open, MinWidth = 0f,
            ZStack = true, Height = ClassicBrowseRules.BarHeight, ClipToBounds = true,
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Children =
            [
                CornerWash(seed, ClassicBrowseRules.BarHeight, ClassicBrowseRules.BarWashCenterY, ClassicBrowseRules.BarWashRadiusX, ClassicBrowseRules.BarWashRadiusY),
                Tick(seed, ClassicBrowseRules.BarHeight),
                new BoxEl
                {
                    HitTestVisible = false, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    Padding = Edges4.All(Spacing.S),
                    Children =
                    [
                        R.Type.CardTitle(m.Title) with
                        {
                            Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>More: the same plate as Bar under a tilted hanging cover — the long tail earns card weight rather than
    /// being demoted for being unsorted. The cover is appended ONLY when the category has artwork (0.2.9
    /// <c>BrowseTiles.cs:166</c>): a coverless card is plate + wash + tick + title, and nothing animates on it.</summary>
    internal static Element Peek(BrowseTileModel m, float cardW)
    {
        ColorF seed = Accent(in m);
        var copy = new BoxEl
        {
            HitTestVisible = false, Direction = 1, Justify = FlexJustify.Start, MinWidth = 0f,
            Padding = Edges4.All(Spacing.M),
            MaxWidth = cardW > 0f ? cardW * ClassicBrowseRules.PeekCopyFrac : float.NaN,
            Children =
            [
                R.Type.ModuleHeader(m.Title) with
                {
                    Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };

        Element wash = CornerWash(seed, ClassicBrowseRules.MoreHeight, ClassicBrowseRules.MoreWashCenterY, ClassicBrowseRules.MoreWashRadiusX, ClassicBrowseRules.MoreWashRadiusY);
        Element[] children = m.Artwork is { Length: > 0 }
            ? [wash, Tick(seed, ClassicBrowseRules.MoreHeight), copy, PeekArt(m.Artwork)]
            : [wash, Tick(seed, ClassicBrowseRules.MoreHeight), copy];

        return new BoxEl
        {
            Key = m.Uri,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = m.Open,
            ZStack = true, Height = ClassicBrowseRules.MoreHeight, MinWidth = 0f, ClipToBounds = true,
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Children = children,
        };
    }

    /// <summary>The corner wash (HomeFoldTile's radial technique): a RAW, un-muted colour anchored at the right edge,
    /// transparent by stop 0.72; alpha rides the NODE's Opacity 0.20 / HoverOpacity 0.34 pair — a plain decoration on the
    /// house ~83 ms hover cross-fade, not a gesture pose.</summary>
    static Element CornerWash(ColorF seed, float height, float centerY, float radiusX, float radiusY) => new BoxEl
    {
        HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
        Height = height, Opacity = ClassicBrowseRules.WashOpacity, HoverOpacity = ClassicBrowseRules.WashHoverOpacity,
        Gradient = new GradientSpec(GradientShape.Radial, 0f,
            [new GradientStop(0f, seed with { A = 1f }), new GradientStop(ClassicBrowseRules.WashFadeStop, seed with { A = 0f })])
        {
            RadialCenter = new Point2(1.0f, centerY),
            RadialRadius = new Point2(radiusX, radiusY),
        },
    };

    /// <summary>The hanging cover: corner-aligned bottom-right (AlignSelf vertical, JustifySelf horizontal), hung past both
    /// edges by <c>Peek × 0.3</c> = 24 DIP and clipped by the root; on hover it slides (−6, +2) and rotates +3° as DELTAS on
    /// its rest pose over <c>MotionTok.ControlNormal</c> — the token owns the reduced-motion policy.</summary>
    static Element PeekArt(string url) => new BoxEl
    {
        Width = ClassicBrowseRules.Peek, Height = ClassicBrowseRules.Peek,
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
        Margin = new Edges4(0f, 0f, -ClassicBrowseRules.PeekHang, -ClassicBrowseRules.PeekHang),
        Rotation = ClassicBrowseRules.PeekRestRotation,
        WhileHover = new MotionTarget
        {
            OffsetX = ClassicBrowseRules.PeekHoverX, OffsetY = ClassicBrowseRules.PeekHoverY, Rotation = ClassicBrowseRules.PeekHoverRotation,
        },
        Transition = MotionTok.ControlNormal,
        HitTestVisible = false, Shadow = Elevation.Card, ClipToBounds = true,
        Corners = CornerRadius4.All(Radii.Control),
        Children = [Controls.Artwork(url, ClassicBrowseRules.Peek, ClassicBrowseRules.Peek, Radii.Control, decodePx: ClassicBrowseRules.PeekDecodePx)],
    };

    /// <summary>The 8-DIP identity pip — a rounded SQUARE (a disc beside a name reads as a bullet).</summary>
    static Element Pip(in BrowseTileModel m) => new BoxEl
    {
        Width = ClassicBrowseRules.Pip, Height = ClassicBrowseRules.Pip, Corners = CornerRadius4.All(Spacing.XXS),
        Fill = Accent(in m), HitTestVisible = false, Shrink = 0f,
    };

    /// <summary>The full-height 3-DIP left hairline on Bar / Peek, in the raw colour.</summary>
    static Element Tick(ColorF seed, float height) => new BoxEl
    {
        Width = ClassicBrowseRules.TickW, Height = height,
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Start,
        Corners = CornerRadius4.All(Spacing.XXS), Fill = seed,
        HitTestVisible = false, Shrink = 0f,
    };

    /// <summary>ONE accent resolution for every cell: a colour ⇒ raw; null or 0 ⇒ the semantic accent (never black).</summary>
    static ColorF Accent(in BrowseTileModel m) => m.Color is { } c && c != 0u ? ColorScience.ToColor(c) : Tok.AccentDefault;

    /// <summary>The Genres grid — the SAME grid a category page's grid/related block renders: the tier's fixed columns (2 / 2 / 3 / 6), colGap 12, the tier's
    /// row gap, keyed by its column count (the one width a key may carry). No width is read: the count is the record's.</summary>
    internal static Element LinkGrid(IReadOnlyList<BrowseTileModel> items, ClassicBrowseLayout layout)
    {
        int cols = layout.GenreColumns();
        var cells = new Element[items.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = Link(items[i], layout);
        return StarGrid(cols, Spacing.M, layout.LinkRowGap, cells) with { Key = LinkGridKey(cols) };
    }

    static readonly string[] s_linkKeys = ["browse-link-grid:1", "browse-link-grid:2", "browse-link-grid:3"];
    static string LinkGridKey(int cols) => cols is >= 1 and <= 3 ? s_linkKeys[cols - 1] : "browse-link-grid:" + cols.ToString(CultureInfo.InvariantCulture);

    /// <summary>A star grid, not a wrapped flex row (a row of Grow cells reports a one-line measure). AlignSelf Stretch
    /// backfills a 1-column band; zero cells is a bare box.</summary>
    public static Element StarGrid(int columns, float colGap, float rowGap, IReadOnlyList<Element> cells)
    {
        if (cells.Count == 0) return new BoxEl();
        var tracks = new TrackSize[Math.Max(1, columns)];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = TrackSize.Star();
        var children = new Element[cells.Count];
        for (int i = 0; i < children.Length; i++) children[i] = cells[i];
        return Ui.Grid(tracks, colGap, rowGap, float.NaN, children) with { AlignSelf = FlexAlign.Stretch };
    }

    /// <summary>A grid / related block (0.2.9 <c>BrowsePage.CategoryBlock</c>): a LABEL header — <c>DrillHeader(title,
    /// null)</c>, never a chevron, never a click — over the Responsive link grid; an untitled block is the bare grid with
    /// no header row and no header gap (parity 101).</summary>
    internal static Element CategoryBlock(string key, string? title, IReadOnlyList<BrowseTileModel> tiles, ClassicBrowseLayout layout)
    {
        Element grid = LinkGrid(tiles, layout);
        if (string.IsNullOrWhiteSpace(title)) return grid with { Key = key };
        return new BoxEl
        {
            Key = key, Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children = [HomeModules.DrillHeader(title, null), grid],
        };
    }

    /// <summary>"Explore all categories" (0.2.9 <c>BrowsePage.ExploreAll</c>): the Caption rung in accent ink, a button
    /// that returns to the directory. On every mode, including the empty and error arms (parity 79).</summary>
    internal static Element ExploreAll(Action? open) => new BoxEl
    {
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        AlignSelf = FlexAlign.Start, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
        FocusVisualMargin = Design.FocusInsetBordered,
        OnClick = open,
        Children = [R.Type.TrackMeta(Loc.Get(Strings.Browse.ExploreAll)) with { Color = Tok.AccentTextPrimary }],
    };
}
