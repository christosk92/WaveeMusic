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
    public const string PageKey = "lc-page", HeroKey = "lc-hero", PivotKey = "lc-pivot", BodyKey = "lc-body", ScrollKey = "lc-scroll";
    public const string ShelfKey = "lc-shelf", WallKey = "lc-wall", ArtistsKey = "lc-artists", FoldersKey = "lc-folders", InsightsKey = "lc-insights";

    /// <summary>The hero's mosaic: rows and columns of covers, its strength over the page ground, the fade into the ground.</summary>
    public const int MosaicColumns = 14, MosaicRows = 3;
    public const float MosaicOpacity = 0.6f, CalmOpacity = 0.55f;
    /// <summary>A mosaic tile's edge, one size at every tier (a tier crossing never changes the hero's height through it).</summary>
    public const float MosaicTile = 100f;
    /// <summary>Each mosaic row's share of <see cref="MosaicOpacity"/>, top down: the art fades into the page ground before the pivot.</summary>
    public static ReadOnlySpan<float> MosaicRowFade => [1f, 0.5f, 0.16f];

    /// <summary>The hero's vertical rhythm (board 18: title at 50, stats 8 under, chips 16 under, actions 20 under, the pivot 40 under).</summary>
    public const float HeroTop = 48f, HeroBottom = 36f, TitleGap = 8f, ChipsGap = 16f, ActionsGap = 20f, ActionGap = 8f;
    /// <summary>The compact head (boards 19 and 20): the mosaic thumb, its gap to the words, the padding above and below.</summary>
    public const float ThumbSize = 60f, ThumbGap = 16f, CompactTop = 16f, CompactBottom = 16f;

    /// <summary>The stats line's counts (board 18: "**6,016** songs · **512** albums"): semibold over the line's own weight.</summary>
    public const ushort StatsNumberWeight = 600;

    /// <summary>A folder chip: height, side padding, glyph, the gap between chips and between glyph and words; the status dot.</summary>
    public const float ChipH = 28f, ChipPadX = 10f, ChipGlyph = 12f, ChipGap = 8f, ChipInner = 6f, ChipBorder = 1f, Dot = 8f, StatusGap = 12f;

    /// <summary>The pivot (a SelectorBar): item height, padding and gap, the hairline under it.</summary>
    public const float PivotItemH = 40f, PivotGap = 4f, PivotHairline = 1f;
    public static readonly Edges4 PivotItemPad = new(12f, 0f, 12f, 0f);

    /// <summary>A Home section: the gap above it, the head row's height and its gap to the cards, a group label's gap.</summary>
    public const float SectionTop = 28f, HeadGap = 12f, GroupLabelGap = 8f, GroupGap = 16f, PageBottom = 48f, SeeAllGlyph = 12f;

    /// <summary>A card's words under its cover (two lines), the gap between cover and words; the pill's inset, padding and height.</summary>
    public const float CardTextH = 44f, CardTextGap = 8f, PillInset = 8f, PillPadX = 6f, PillH = 20f, PillText = 11f, NewInset = 8f;
    /// <summary>A card's play button (on the cover's corner, shown on hover).</summary>
    public const float PlayButton = 40f, PlayGlyph = 16f, PlayInset = 10f;

    /// <summary>A decade tile: its height, the covers' size and overlap, the shuffle glyph.</summary>
    public const float DecadeH = 56f, DecadeCover = 36f, DecadeOverlap = 14f, DecadeGap = 10f, DecadePad = 10f;
    public const int DecadeCovers = 3;

    /// <summary>The Albums wall's sort row: its height and the chips' gap.</summary>
    public const float SortRowH = 52f, SortGap = 8f;

    /// <summary>A song row in the Folders browser.</summary>
    public const float SongRowH = 40f, SongNumberW = 36f, SongQualityW = 96f, SongTimeW = 56f, SongColGap = 16f;

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

    static CommonLocalCollectionLayout Std(WidthTier t) => new()
    {
        Tier = t, Card = 164f, CardGap = 16f, WallMin = 168f, WallGap = 20f, ArtistMin = 140f, DecadeMin = 240f,
        FileColumn = true, ActionsBeside = true, ChipH = L.ChipH,
    };

    static readonly CommonLocalCollectionLayout[] s_records =
    [
        Std(WidthTier.Compact) with { Card = 136f, CardGap = 12f, WallMin = 140f, WallGap = 12f, ArtistMin = 104f, DecadeMin = 170f, FileColumn = false, ActionsBeside = false, ChipH = CommonTiers.CompactTarget },
        Std(WidthTier.Small) with { Card = 148f, CardGap = 14f, WallMin = 150f, ArtistMin = 120f, WallGap = 16f, DecadeMin = 220f, FileColumn = false },
        Std(WidthTier.Standard),                                                                     // boards 18-20
        Std(WidthTier.Huge) with { Card = 196f, CardGap = 20f, WallMin = 200f, WallGap = 24f, ArtistMin = 180f, DecadeMin = 300f },
    ];

    public static CommonLocalCollectionLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];
}
