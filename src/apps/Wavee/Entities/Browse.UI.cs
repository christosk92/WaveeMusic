// ── Entities/Browse.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the five Browse cell densities (Word / Name / Link / Bar / Peek), the link grid, and the directory body + bands
//
// Role: UI
// Owner: P (stream P3)
// Wave: 5
// Budget: 650 lines
// Spec: ch 13 §0.10-14, §1.3, W15-W17, §3 (Browse cells), §4.2, §5 (browse rows), §6.3 — the browse half
//
// ── FIVE DENSITIES, ONE PER BAND, CHEAPEST TO MOST EXPRESSIVE ────────────────────────────────────────────────────────
//
// Density is what the directory spends to say "here is how much this destination is". Top's four entry points need
// nothing but their own name on a tall pill (Word); For you is one step down, a pip beside a name (Name); Genres runs
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
// key is the column count of the grid that holds them.
//
// ── THE DIRECTORY BODY IS EAGER ──────────────────────────────────────────────────────────────────────────────────────
//
// It mounts exactly once per page mount with a taxonomy-fixed band count — the two conditions `Design.Entrance` needs —
// so each band cascades in 40 ms behind the last, starting at index 1. The same `DirectoryBody` renders the loaded page
// and (over `BrowseDirectorySeeds`, `.Skeletonized(true)`) the loading one, which is what keeps the band and wrap counts
// identical across the swap (§0 W16, parity 53).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ══ 1. THE CELLS ═════════════════════════════════════════════════════════════════════════════════════════════════════

public static partial class BrowseTiles
{
    /// <summary>Open a category page (0.2.9 <c>go(BrowseRoutes.Page(uri), title)</c>): no origin — the route family
    /// composes the <c>Browse › X</c> trail itself. UI thread (the route interns).</summary>
    public static readonly Action<string, string> OpenCategory = static (uri, title) => Shell.GoTo(PageRoute(uri, title));

    /// <summary>Open a client feature: only a resolvable one navigates; anything else declines and logs, rather than
    /// navigating to a key no page renders (0.2.9 <c>BrowseRoutes.FeatureRoute</c> + <c>browse.feature.unsupported</c>).</summary>
    public static readonly Action<string> OpenFeature = static uri =>
    {
        var route = FeatureRoute(uri);
        if (route.IsNone) Log.Warn("nav", "browse.feature.unsupported: " + uri);
        else Shell.GoTo(route);
    };

    /// <summary>The plate the two pill densities share (0.2.9 <c>ContentFilterChips.Chip</c>'s grammar, verbatim):
    /// FillControlDefault → Secondary on hover, a stroke that goes accent on hover, the subtle scale tier. Shrink 0 on a
    /// WRAPPING row: the row breaks to a new line rather than ellipsising every pill.</summary>
    static Element Chip(in BrowseTileModel m, float height, Element? lead, TextEl label)
    {
        var text = label with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        return new BoxEl
        {
            Key = m.Uri,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = m.Open,
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = height, Shrink = 0f, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
            Corners = CornerRadius4.All(999f),
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, HoverBorderColor = Tok.AccentDefault,
            HoverScale = Design.Motion.ScaleSubtle.Hover, PressScale = Design.Motion.ScaleSubtle.Press,
            HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
            Children = lead is null ? [text] : [lead, text],
        };
    }

    /// <summary>Top: the tallest pill (36), the only one at BodyStrong — primary without being set larger than the band
    /// heading that names it.</summary>
    public static Element Word(BrowseTileModel m) => Chip(in m, BrowseLayout.WordChipH, null, BodyStrong(m.Title));

    /// <summary>For you: the same plate one rung down (32), with the identity pip its detail page carries.</summary>
    public static Element Name(BrowseTileModel m) => Chip(in m, BrowseLayout.NameChipH, Pip(in m), Body(m.Title));

    /// <summary>Genres — and Search's genre results (ch 13 §0.15): the pip beside Body secondary, one rung below Name
    /// because a genre column runs ~25 deep. The padded hit target and its hover fill are what make one row easy to click
    /// without catching its neighbour; a search genre passes <c>Color: null</c> ⇒ an accent pip.</summary>
    public static Element Link(BrowseTileModel m) => new BoxEl
    {
        Key = m.Uri,
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
    public static Element Bar(BrowseTileModel m)
    {
        ColorF seed = Accent(in m);
        return new BoxEl
        {
            Key = m.Uri,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = m.Open, MinWidth = 0f,
            ZStack = true, Height = BrowseLayout.BarHeight, ClipToBounds = true,
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Children =
            [
                CornerWash(seed, BrowseLayout.BarHeight, centerY: 0.45f, radiusX: 0.65f, radiusY: 1.40f),
                Tick(seed, BrowseLayout.BarHeight),
                new BoxEl
                {
                    HitTestVisible = false, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    Padding = Edges4.All(Spacing.S),
                    Children =
                    [
                        Design.Type.CardTitle(m.Title) with
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
    public static Element Peek(BrowseTileModel m, float cardW)
    {
        ColorF seed = Accent(in m);
        var copy = new BoxEl
        {
            HitTestVisible = false, Direction = 1, Justify = FlexJustify.Start, MinWidth = 0f,
            Padding = Edges4.All(Spacing.M),
            MaxWidth = cardW > 0f ? cardW * BrowseLayout.PeekCopyFrac : float.NaN,
            Children =
            [
                Design.Type.ModuleHeader(m.Title) with
                {
                    Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };

        Element[] children = m.Artwork is { Length: > 0 }
            ? [CornerWash(seed, BrowseLayout.MoreHeight, 0.28f, 0.70f, 1.30f), Tick(seed, BrowseLayout.MoreHeight), copy, PeekArt(m.Artwork)]
            : [CornerWash(seed, BrowseLayout.MoreHeight, 0.28f, 0.70f, 1.30f), Tick(seed, BrowseLayout.MoreHeight), copy];

        return new BoxEl
        {
            Key = m.Uri,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = m.Open,
            ZStack = true, Height = BrowseLayout.MoreHeight, MinWidth = 0f, ClipToBounds = true,
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
        Height = height, Opacity = 0.20f, HoverOpacity = 0.34f,
        Gradient = new GradientSpec(GradientShape.Radial, 0f,
            [new GradientStop(0f, seed with { A = 1f }), new GradientStop(0.72f, seed with { A = 0f })])
        {
            RadialCenter = new Point2(1.0f, centerY),
            RadialRadius = new Point2(radiusX, radiusY),
        },
    };

    /// <summary>The hanging cover: corner-aligned bottom-right (AlignSelf vertical, JustifySelf horizontal), hung past both
    /// edges by <c>Peek × 0.3</c> = 24 DIP and clipped by the root; on hover it slides (−6, +2) and rotates +3° as DELTAS on
    /// its rest pose over <c>MotionTok.ControlNormal</c> — the token owns the reduced-motion policy.</summary>
    static Element PeekArt(string url)
    {
        const float hang = BrowseLayout.Peek * 0.3f;
        return new BoxEl
        {
            Width = BrowseLayout.Peek, Height = BrowseLayout.Peek,
            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
            Margin = new Edges4(0f, 0f, -hang, -hang),
            Rotation = -8f,
            WhileHover = new MotionTarget { OffsetX = -6f, OffsetY = 2f, Rotation = 3f },
            Transition = MotionTok.ControlNormal,
            HitTestVisible = false, Shadow = Elevation.Card, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Control),
            Children = [Controls.Artwork(url, BrowseLayout.Peek, BrowseLayout.Peek, Radii.Control, decodePx: 128)],
        };
    }

    /// <summary>The 8-DIP identity pip — a rounded SQUARE (a disc beside a name reads as a bullet).</summary>
    static Element Pip(in BrowseTileModel m) => new BoxEl
    {
        Width = BrowseLayout.Pip, Height = BrowseLayout.Pip, Corners = CornerRadius4.All(Spacing.XXS),
        Fill = Accent(in m), HitTestVisible = false, Shrink = 0f,
    };

    /// <summary>The full-height 3-DIP left hairline on Bar / Peek, in the raw colour.</summary>
    static Element Tick(ColorF seed, float height) => new BoxEl
    {
        Width = BrowseLayout.TickW, Height = height,
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Start,
        Corners = CornerRadius4.All(Spacing.XXS), Fill = seed,
        HitTestVisible = false, Shrink = 0f,
    };

    /// <summary>ONE accent resolution for every cell: a colour ⇒ raw; null or 0 ⇒ the semantic accent (never black).</summary>
    static ColorF Accent(in BrowseTileModel m) => m.Color is { } c && c != 0u ? Design.Palette.ToColor(c) : Tok.AccentDefault;

    /// <summary>The Genres grid — the SAME grid a category page's grid/related block renders: 3 / 2 / 1 columns at the
    /// 720 / 380 bands, colGap 12, rowGap 8, keyed by its column count (the one width a key may carry).</summary>
    public static Element LinkGrid(IReadOnlyList<BrowseTileModel> items, float width)
    {
        int cols = BrowseLayout.LinkColumns(width > 0f ? width : BrowseLayout.DirectoryFallbackWidth);
        var cells = new Element[items.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = Link(items[i]);
        return BrowseLayout.StarGrid(cols, Spacing.M, Spacing.S, cells) with { Key = LinkGridKey(cols) };
    }

    static readonly string[] s_linkKeys = ["browse-link-grid:1", "browse-link-grid:2", "browse-link-grid:3"];
    static string LinkGridKey(int cols) => cols is >= 1 and <= 3 ? s_linkKeys[cols - 1] : "browse-link-grid:" + cols.ToString(CultureInfo.InvariantCulture);

    /// <summary>A category → its tile model for a LIVE surface (both handlers wired) or the inert loading one.</summary>
    public static BrowseTileModel ModelOf(BrowseCategory c, bool live)
        => ToModel(c, live ? OpenCategory : null, live ? OpenFeature : null);
}

// ══ 2. THE DIRECTORY BODY AND ITS BANDS ═════════════════════════════════════════════════════════════════════════════

public readonly partial struct Browse
{
    /// <summary>The directory's category list projected off its rows (the rules' <see cref="BrowseCategory"/> value): the
    /// directory relation's targets in wire order, each tile's title, colour, artwork url and client-feature bit. A tile
    /// nobody has titled yet is skipped (it would render as an empty pill). UI thread; allocates — the page memoizes it
    /// per publication.</summary>
    public static BrowseCategory[] CategoriesOf(ReadOnlySpan<int> tileSlots)
    {
        var list = new List<BrowseCategory>(tileSlots.Length);
        for (int i = 0; i < tileSlots.Length; i++)
        {
            if (CategoryAt(tileSlots[i]) is { } c) list.Add(c);
        }
        return list.ToArray();
    }

    /// <summary>One browse node as a category, or null when it has no identity to render.</summary>
    public static BrowseCategory? CategoryAt(int slot)
    {
        var b = new Browse(slot);
        if (!b.IsValid || b.TitleId.IsEmpty) return null;
        var id = b.Id;
        if (id.Form == EntityForm.None) return null;
        return new BrowseCategory(id.Text, Entities.Strings.Resolve(b.TitleId), b.Color == 0u ? (uint?)null : b.Color,
                                  Controls.ArtUrl(b.ImageId), b.IsClientFeature);
    }

    /// <summary>The directory body (0.2.9 <c>BrowseDirectory.Body</c>): the bands in <see cref="BrowseTaxonomy.BandOrder"/>
    /// — Top, Charts, For you, Genres, Mood &amp; activity, More — each keyed by its band, Gap 16, gutterless (the page
    /// owns the frame). Charts is chrome, not a tile band: its bucket is SWALLOWED (the two chart categories never render
    /// as tiles) and <paramref name="chartsBandAt"/> is injected in its slot, always. <paramref name="live"/> false is the
    /// loading directory: every cell still hovers and does nothing.</summary>
    public static Element DirectoryBody(IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> groups,
                                        bool live, Func<int, Element> chartsBandAt)
    {
        var children = new List<Element>(groups.Count + 2);
        int band = 1;   // the first band's OWN entrance delay (0.2.9: the masthead used to hold rung 0)
        for (int b = 0; b < BrowseTaxonomy.BandOrder.Count; b++)
        {
            var g = BrowseTaxonomy.BandOrder[b];
            if (g == BrowseGroup.Charts) { children.Add(chartsBandAt(band++) with { Key = "browse-band:charts" }); continue; }
            if (ItemsOf(groups, g) is { Count: > 0 } items) children.Add(BandOf(g, items, live, band++));
        }
        return new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, Children = children.ToArray() };
    }

    static IReadOnlyList<BrowseCategory>? ItemsOf(IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> groups, BrowseGroup g)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Group == g) return groups[i].Items;
        return null;
    }

    /// <summary>The state a band's responsive grid is gated on: the memoized item list BY REFERENCE plus the live bit, so a
    /// page re-render that changed nothing (the under-band clip edge) rebuilds no grid.</summary>
    sealed record BandState(IReadOnlyList<BrowseCategory> Items, bool Live);

    static readonly string[] s_bandKeys =
        ["browse-band:top", "browse-band:foryou", "browse-band:genres", "browse-band:mood", "browse-band:charts", "browse-band:more"];

    /// <summary>One band: an eyebrow label over the density its destinations earn, cascading in at
    /// <paramref name="index"/> (the only thing the index is for).</summary>
    static Element BandOf(BrowseGroup group, IReadOnlyList<BrowseCategory> items, bool live, int index)
    {
        var state = new BandState(items, live);
        Element body = group switch
        {
            BrowseGroup.Top => WrapRow(items, live, word: true),
            BrowseGroup.ForYou => WrapRow(items, live, word: false),
            BrowseGroup.Genres => Responsive.Of(state, static (st, w) => LinkGridOf(st, w), fallback: BrowseLayout.DirectoryFallbackWidth),
            BrowseGroup.MoodActivity => Responsive.Of(state, static (st, w) => BarGrid(st, w), fallback: BrowseLayout.DirectoryFallbackWidth),
            _ => Responsive.Of(state, static (st, w) => MoreGrid(st, w), fallback: BrowseLayout.DirectoryFallbackWidth),
        };
        return new BoxEl
        {
            Key = s_bandKeys[(int)group],
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Animate = Design.Entrance.Row(index),
            Children = [BandLabel(GroupLabel(group)), body],
        };
    }

    /// <summary>Top / For you: a wrapping row of pills at their own natural width — no grid, no column count.</summary>
    static Element WrapRow(IReadOnlyList<BrowseCategory> items, bool live, bool word)
    {
        var cells = new Element[items.Count];
        for (int i = 0; i < cells.Length; i++)
        {
            var m = BrowseTiles.ModelOf(items[i], live);
            cells[i] = word ? BrowseTiles.Word(m) : BrowseTiles.Name(m);
        }
        return new BoxEl { Direction = 0, Gap = BrowseLayout.ChipGap, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = cells };
    }

    static Element LinkGridOf(BandState st, float width)
    {
        var tiles = new BrowseTileModel[st.Items.Count];
        for (int i = 0; i < tiles.Length; i++) tiles[i] = BrowseTiles.ModelOf(st.Items[i], st.Live);
        return BrowseTiles.LinkGrid(tiles, width);
    }

    static Element BarGrid(BandState st, float width)
    {
        int cols = BrowseLayout.BarColumns(width > 0f ? width : BrowseLayout.DirectoryFallbackWidth);
        var cells = new Element[st.Items.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = BrowseTiles.Bar(BrowseTiles.ModelOf(st.Items[i], st.Live));
        return BrowseLayout.StarGrid(cols, Spacing.XS, Spacing.XS, cells) with { Key = "browse-mood-grid:" + cols.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>The More grid; the cell width handed to Peek is <c>width / cols</c> with the gaps NOT subtracted
    /// (0.2.9 <c>BrowseDirectory.cs:312</c>, kept — it only bounds the copy column).</summary>
    static Element MoreGrid(BandState st, float width)
    {
        float w = width > 0f ? width : BrowseLayout.DirectoryFallbackWidth;
        int cols = BrowseLayout.MoreColumns(w);
        float cellW = w / cols;
        var cells = new Element[st.Items.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = BrowseTiles.Peek(BrowseTiles.ModelOf(st.Items[i], st.Live), cellW);
        return BrowseLayout.StarGrid(cols, Spacing.M, Spacing.M, cells) with { Key = "browse-more-grid:" + cols.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>A band heading NAMES the row; it is not a peer of its destinations — the eyebrow rung (Caption 600 +
    /// tracking, secondary), sentence case, never caps-transformed (a localized string).</summary>
    static Element BandLabel(string label)
        => Design.Type.Eyebrow(label) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>The localized band heading (membership is uri-keyed and culture-independent; only the label translates).
    /// Never asked for Charts — that band carries the Fold deck's own header.</summary>
    static string GroupLabel(BrowseGroup g) => g switch
    {
        BrowseGroup.Top => Loc.Get(Strings.Browse.Top),
        BrowseGroup.ForYou => Loc.Get(Strings.Browse.ForYou),
        BrowseGroup.Genres => Loc.Get(Strings.Browse.Genres),
        BrowseGroup.MoodActivity => Loc.Get(Strings.Browse.MoodActivity),
        _ => Loc.Get(Strings.Browse.More),
    };

    /// <summary>The loading directory (0.2.9 <c>BrowseDirectory.Skeleton</c>): the SAME body over the seeds — 4 / 10 / 25 /
    /// 14 / 3 — with the Charts band shimmering off <see cref="HomeBrowseCards.ChartDeckSeed"/>, the whole tree
    /// <c>.Skeletonized(true)</c>. Built once per theme read by the page and handed to its region.</summary>
    public static Element DirectorySkeleton()
        => DirectoryBody(BrowseTaxonomy.Grouped(BrowseDirectorySeeds.Categories), live: false, s_seedChartsBand).Skeletonized(true);

    static readonly Action<HomeSectionView> s_noopSection = static _ => { };

    static readonly Func<int, Element> s_seedChartsBand = static index => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Animate = Design.Entrance.Row(index),
        Children = [HomeModules.FoldDeck(HomeBrowseCards.ChartDeckSeed, Loc.Get(Strings.Browse.Charts), s_noopSection)],
    };

    // ── the category page's shared blocks (Browse.Page.cs composes them) ─────────────────────────────────────────────

    /// <summary>A grid / related block (0.2.9 <c>BrowsePage.CategoryBlock</c>): a LABEL header — <c>DrillHeader(title,
    /// null)</c>, never a chevron, never a click — over the Responsive link grid; an untitled block is the bare grid with
    /// no header row and no header gap (parity 101).</summary>
    public static Element CategoryBlock(string? title, IReadOnlyList<BrowseTileModel> tiles)
    {
        Element grid = Responsive.Of(tiles, static (items, w) => BrowseTiles.LinkGrid(items, w), fallback: BrowseLayout.DirectoryFallbackWidth);
        if (string.IsNullOrWhiteSpace(title)) return grid;
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children = [HomeModules.DrillHeader(title, null), grid],
        };
    }

    /// <summary>"Explore all categories" (0.2.9 <c>BrowsePage.ExploreAll</c>): the Caption rung in accent ink, a button
    /// that returns to the directory. On every mode, including the empty and error arms (parity 79).</summary>
    public static Element ExploreAll(Action? open) => new BoxEl
    {
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        AlignSelf = FlexAlign.Start, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
        FocusVisualMargin = Design.FocusInsetBordered,
        OnClick = open,
        Children = [Design.Type.TrackMeta(Loc.Get(Strings.Browse.ExploreAll)) with { Color = Tok.AccentTextPrimary }],
    };

    /// <summary>The Explore-all verb: the directory, no origin.</summary>
    public static readonly Action GoDirectory = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse));
}
