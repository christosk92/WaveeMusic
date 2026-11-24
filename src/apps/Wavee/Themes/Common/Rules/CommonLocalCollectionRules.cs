// ── Themes/Common/Rules/CommonLocalCollectionRules.cs — the numbers of the "On this PC" page WinUI and Classic share (#240 P2) ─────────
//
// Data, not decisions: `CommonLocalCollection*.cs` read them (WVTH003 allows literals only in `Rules\**`). Boards: #240 board 18 "On this PC:
// a collection with a home" (1440 x 1916), 19 "Albums: a cover wall" and 20 "Folders" (1440 x 960). The owner's rule: Classic uses the WinUI
// layout, so both structures register this page; each paints it with its own tokens and type ramp. The page gutter is the theme's own
// (WinUI `WinUiTiers.InsetOf`, Classic `ClassicTiers.PageOf().Gutter`), handed in at registration.

using FluentGpu.Animation;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;
using L = Wavee.Themes.Common.CommonLocalCollectionRules;

namespace Wavee.Themes.Common;

/// <summary>Node keys and fixed geometry of the shared collection page.</summary>
internal static class CommonLocalCollectionRules
{
    public const string PageKey = "lc-page", HeroKey = "lc-hero", PivotKey = "lc-pivot", BodyKey = "lc-body", ScrollKey = "lc-scroll", HeadKey = "lc-head";

    /// <summary>The pivot switch's clock (ms): WinUI's incoming 300 on its decelerate, the head's cross-fade and the body's slide.</summary>
    public const float HeadInMs = 300f;
    public const string ShelfKey = "lc-shelf", WallKey = "lc-wall", ArtistsKey = "lc-artists", FoldersKey = "lc-folders", InsightsKey = "lc-insights";

    /// <summary>The hero's mosaic: rows and columns of covers, its strength over the page ground, the fade into the ground.</summary>
    public const int MosaicColumns = 14, MosaicRows = 3;
    public const float MosaicOpacity = 0.6f, CalmOpacity = 0.55f;
    /// <summary>A mosaic tile's edge, one size at every tier (a tier crossing never changes the hero's height through it).</summary>
    public const float MosaicTile = 100f;
    /// <summary>Each mosaic row's share of <see cref="MosaicOpacity"/>, top down: the art fades into the page ground before the pivot.</summary>
    public static ReadOnlySpan<float> MosaicRowFade => [1f, 0.5f, 0.16f];

    /// <summary>The head, one form on every pivot (the library page's head, W-Library): the title, the meta line <see cref="MetaGap"/> under it, the
    /// folders and the state line <see cref="StatusTop"/> under that, the pivot <see cref="HeadBottom"/> under the head (the library's title-to-pivot
    /// 16); the actions <see cref="ActionGap"/> apart, beside the title (under the words at Compact, <see cref="ActionsUnderGap"/> down). The air
    /// above the title steps with the tier (<see cref="CommonLocalCollectionLayout.HeadTop"/>).</summary>
    public const float MetaGap = 4f, StatusTop = 12f, HeadBottom = 16f, ActionGap = 8f, ActionsUnderGap = 16f;
    /// <summary>The gap between a folder head's parts and the padding of the Folders browser's head (boards 19 and 20).</summary>
    public const float ThumbGap = 16f, CompactTop = 16f;

    /// <summary>A folder chip: height, side padding, glyph, the gap between chips and between glyph and words; the status dot.</summary>
    public const float ChipH = 28f, ChipPadX = 10f, ChipGlyph = 12f, ChipGap = 8f, ChipInner = 6f, ChipBorder = 1f, Dot = 8f, StatusGap = 12f;

    /// <summary>The pivot's first word stands on the page gutter, as the library's kinds do (the stock item pads its word 12 in).</summary>
    public const float PivotLead = -12f;

    /// <summary>The pivot (a SelectorBar): item height and gap, the hairline under it. The item keeps the stock SelectorBar padding (12,10,12,7):
    /// 10 + the 20 label + 7 + the 3 pill = the 40 item, so the pill sits 7 under the label as WinUI's does (a zero vertical padding stacked
    /// the pill flush under the text, "too tight").</summary>
    public const float PivotItemH = 40f, PivotGap = 4f, PivotHairline = 1f;

    /// <summary>A Home section: the gap above it, the head row's height and its gap to the cards, a group label's gap.</summary>
    /// <summary>"Your files and Spotify"'s offer under the pivot (board 28: 20 under the pivot's hairline).</summary>
    public const float LinkOfferTop = 20f;
    public const float SectionTop = 28f, HeadGap = 12f, GroupLabelGap = 8f, GroupGap = 16f, PageBottom = 48f, SeeAllGlyph = 12f;

    /// <summary>A card's words under its cover (two lines), the gap between cover and words; the pill's inset, padding and height.</summary>
    public const float CardTextH = 44f, CardTextGap = 8f, PillInset = 8f, PillPadX = 6f, PillH = 20f, PillText = 11f, NewInset = 8f;
    /// <summary>A card's play button (on the cover's corner, shown on hover).</summary>
    public const float PlayButton = 40f, PlayGlyph = 16f, PlayInset = 10f;

    /// <summary>A decade tile: its height, the covers' size and overlap, the shuffle glyph.</summary>
    public const float DecadeH = 56f, DecadeCover = 36f, DecadeOverlap = 14f, DecadeGap = 10f, DecadePad = 10f;
    public const int DecadeCovers = 3;

    /// <summary>The Albums wall's sort row: its height and the chips' gap (Zune's wall).</summary>
    public const float SortRowH = 52f, SortGap = 8f;

    /// <summary>Albums' and Artists' toolbar (the library's: the sort, the filter, the view pair): the air above and below it, the gap between its
    /// controls, the filter's width beside the sort (it takes its own line at Compact).</summary>
    public const float ToolbarTop = 16f, ToolbarBottom = 12f, ToolbarGap = 8f, FilterW = 280f;

    /// <summary>The grid | list pair where a structure registers no library controls (W-Library's: two 40-wide cells on one control plate).</summary>
    public const float ViewCellW = 40f, ViewGlyph = 16f, ViewStroke = 1f;

    /// <summary>A list row (the library's: the 40 art, the title over its meta, a 56 plate) and a compact one (the 32 art, the title alone).</summary>
    public const float RowArt = 40f, RowH = 56f, CompactRowArt = 32f, CompactRowH = 48f;

    /// <summary>The grid's least cell per size code (small, medium, large), as a share of the tier's wall cell.</summary>
    public static ReadOnlySpan<float> GridScale => [0.75f, 1f, 1.35f];

    /// <summary>A song row in the Folders browser.</summary>
    public const float SongRowH = 40f, SongNumberW = 36f, SongQualityW = 96f, SongTimeW = 56f, SongColGap = 16f;

    /// <summary>An open folder's (or album's) header under the breadcrumb: the gap between its cover and its words, the air under it, the gap
    /// between the title and its meta line.</summary>
    public const float DetailGap = 20f, DetailBottom = 24f, DetailMetaGap = 4f;

    /// <summary>A folder tile's glyph when it has no picture.</summary>
    public const float FolderGlyph = 32f, MusicGlyph = 28f;

    /// <summary>Insights: card padding and gap, the format bar's height, a stat row's height.</summary>
    public const float InsightPad = 16f, InsightGap = 16f, ShareBarH = 12f, StatRowH = 32f, InsightColumnMin = 320f;

    /// <summary>The page gutter's spacers ease to a new width (a window-tier crossing changes it): through real layout, so what follows rides it.</summary>
    public const float GutterMs = 250f;
    public static readonly LayoutTransition GutterEase = new(TransitionChannels.Size, TransitionDynamics.Tween(GutterMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>A wall card that changes place or size (the column count changed): position and size tween over the gutter's time, the size revealed.</summary>
    public static readonly LayoutTransition CardReflow = new(TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(GutterMs, Easing.SmoothOut), SizeMode.Reveal);

    /// <summary>A box whose size steps on both axes: a card's cover (and the card) at a tier crossing, a chip whose words change (a folder's "· scanning"
    /// leaving it) or whose height steps at Compact. It eases through real layout, so what stands beside and under it rides it frame by frame.</summary>
    public static readonly LayoutTransition SizeReflow = new(TransitionChannels.Size, TransitionDynamics.Tween(GutterMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width | SizeAxes.Height);

    /// <summary>A box whose height steps with the tier (a chip's 40 hit target at Compact and 28 above; the decade grid taking another row count): it
    /// eases through real layout, so what follows rides it.</summary>
    public static readonly LayoutTransition HeightReflow = new(TransitionChannels.Size, TransitionDynamics.Tween(GutterMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>A Home shelf under "Edit home": moved, its outer box glides to the new place over the gutter's time; shown or hidden, its inner box
    /// reveals or folds its height (the drawer recipe) and the shelves below ride it.</summary>
    public static readonly LayoutTransition ShelfReveal = Design.Reveal.Drawer;
    public static readonly LayoutTransition ShelfMove = new(TransitionChannels.Position, TransitionDynamics.Tween(GutterMs, Easing.SmoothOut));

    /// <summary>"Edit home": the popup card's width and padding, a shelf row's height (a 40 target), the gap between rows and words.</summary>
    public const float EditW = 320f, EditPad = 16f, EditRowH = 40f, EditGap = 4f;

    /// <summary>The empty and loading states' widest column.</summary>
    public const float EmptyMaxW = 560f;

    /// <summary>A no-art tile: its colour from the name (a stable FNV-1a hash picks the hue; the saturation and value keep it calm, the second stop
    /// a darker, shifted hue), the initial's ink, and the tile size from which the initial takes the page-hero voice.</summary>
    public static (ColorF Top, ColorF Bottom) TintOf(string seed)
    {
        uint h = 2166136261;
        foreach (char c in seed) h = (h ^ c) * 16777619;
        float hue = h % 360u;
        return (ColorF.FromHsv(hue, 0.42f, 0.62f), ColorF.FromHsv((hue + 28f) % 360f, 0.55f, 0.36f));
    }

    public static ColorF InitialInk => ColorF.FromRgba(255, 255, 255, 0xD0);
    public const float InitialBigFrom = 96f;

    /// <summary>The image decode budget of a card cover and of a mosaic tile.</summary>
    public const float CardDecode = 256f, MosaicDecode = 160f;
}

/// <summary>The page's numbers per content tier (boards 18-20 at Standard; Compact 400, Small 800, Huge 2560 scale the cards).</summary>
internal sealed record CommonLocalCollectionLayout
{
    public required WidthTier Tier { get; init; }
    /// <summary>A shelf card's width and the gap between cards.</summary>
    public required float Card { get; init; }
    public required float CardGap { get; init; }
    /// <summary>The wall's least cell width (columns fill the row) and its gaps.</summary>
    public required float WallMin { get; init; }
    public required float WallGap { get; init; }
    /// <summary>An artist cell's least width (the round portraits run smaller than the album covers).</summary>
    public required float ArtistMin { get; init; }
    /// <summary>A decade tile's least width.</summary>
    public required float DecadeMin { get; init; }
    /// <summary>The Folders song table shows the File column (wide tiers only).</summary>
    public required bool FileColumn { get; init; }
    /// <summary>The compact head docks the actions beside the title (false: under it).</summary>
    public required bool ActionsBeside { get; init; }
    /// <summary>A clickable chip's (folder, sort, "See all") height: the board's 28, the 40 hit target at Compact.</summary>
    public required float ChipH { get; init; }
    /// <summary>A control's height in the head and the toolbar (the buttons, the sort, the filter, the view pair): the stock 32, the 40 target at Compact.</summary>
    public required float ControlH { get; init; }
    /// <summary>The air above the title: the library head's (16 / 20 / 24 / 32 across the tiers).</summary>
    public required float HeadTop { get; init; }
    /// <summary>The cover of an open folder's (or album's) header: the picture its tile's cover flies into. One size at every tier, as the hero's
    /// mosaic tile is: the header is a fixed row of the folder's list, whose extent a tier crossing would otherwise step (the rows under it jumped).</summary>
    public required float DetailCover { get; init; }

    static CommonLocalCollectionLayout Std(WidthTier t) => new()
    {
        Tier = t, Card = 164f, CardGap = 16f, WallMin = 168f, WallGap = 20f, ArtistMin = 140f, DecadeMin = 240f,
        FileColumn = true, ActionsBeside = true, ChipH = L.ChipH, DetailCover = 144f, ControlH = 32f, HeadTop = 24f,
    };

    static readonly CommonLocalCollectionLayout[] s_records =
    [
        Std(WidthTier.Compact) with { Card = 136f, CardGap = 12f, WallMin = 140f, WallGap = 12f, ArtistMin = 104f, DecadeMin = 170f, FileColumn = false, ActionsBeside = false, ChipH = CommonTiers.CompactTarget, ControlH = CommonTiers.CompactTarget, HeadTop = 16f },
        Std(WidthTier.Small) with { Card = 148f, CardGap = 14f, WallMin = 150f, ArtistMin = 120f, WallGap = 16f, DecadeMin = 220f, FileColumn = false, HeadTop = 20f },
        Std(WidthTier.Standard),                                                                     // boards 18-20
        Std(WidthTier.Huge) with { Card = 196f, CardGap = 20f, WallMin = 200f, WallGap = 24f, ArtistMin = 180f, DecadeMin = 300f, HeadTop = 32f },
    ];

    public static CommonLocalCollectionLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];
}
