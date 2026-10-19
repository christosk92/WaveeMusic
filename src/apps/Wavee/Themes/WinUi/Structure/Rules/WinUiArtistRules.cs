// ── Themes/WinUi/Structure/Rules/WinUiArtistRules.cs — the numbers of WinUI's artist page (themes plan 6.2; P6 W6a) ─────────────────────────────────
//
// Boards W-Artist (C / S / Std / H / 4K). What the artist views read instead of a literal (WVTH003 allows literals only in `Rules\**`):
//
//   THE HERO is a r8 CARD inside the page's inset, 200 / 220 / 260 / 420 tall (Compact / Small / Standard / Huge): the photograph (flat neutral without one) under a
//   180-tall veil (clear to 62 % black), the copy at the bottom-left 28 in and 24 up: the Verified line (a 14 accent seal and Caption 12 in Text 1), the name in
//   Display 68 (auto-fitting down to 40), the meta (Body 14: "20,577,457 monthly listeners · #112 in the world") and, right-aligned on the same baseline, the actions:
//   Play (accent, glyph and word), Follow, Shuffle, Artist radio and "..." on a 70 % dark plate with a hairline. At Compact the actions are a row UNDER the card.
//   THE PIVOT is a SelectorBar (Overview · Albums · Singles & EPs · Compilations · Appears on · About), 40 tall, STICKY under the card; its 41 (the hairline) is the
//   clearance a pivot click parks a section under, so the spy lights exactly the section on screen (`ArtistTierPolicy.BandClearance`).
//   THE OVERVIEW is two columns 36 apart, Popular (1.2) | the latest release card over the Discography preview (1), stacking below 1100; three at Huge, Popular |
//   Discography | the latest release over About and Fans.
//
// Public, so `Wavee.Tests` pins the numbers.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The geometry of the artist hero card in one window tier.</summary>
public sealed record WinUiArtistHeroForm(float Height, float Veil, float InsetX, float Bottom, float NameSize, float NameLine, float MetaSize, float MetaLine, bool RankInMeta);

/// <summary>The artist page's numbers.</summary>
public static class WinUiArtistRules
{
    // ── the hero card ──

    /// <summary>The card's height per window tier: 200 / 220 / 260 / 420.</summary>
    public static float HeroHeightOf(WidthTier tier) => HeroOf(tier).Height;

    /// <summary>The hero card's geometry in a window tier (W-Artist-Compact / -Small / W-Artist / -Huge): the height, the veil's height, the copy's inset and its air under it, the
    /// name's size and line (Display 68/80; 28/36 at Compact and Small), the meta's (Body 14/20; Caption 12/16 at Compact, where it says the listeners alone).</summary>
    public static WinUiArtistHeroForm HeroOf(WidthTier tier) => tier switch
    {
        WidthTier.Compact => new(200f, 140f, 16f, 16f, 28f, 36f, 12f, 16f, RankInMeta: false),
        WidthTier.Small => new(220f, 150f, 20f, 20f, 28f, 36f, 14f, 20f, RankInMeta: true),
        WidthTier.Standard => new(260f, 180f, 28f, 24f, 68f, 80f, 14f, 20f, RankInMeta: true),
        _ => new(420f, 180f, 28f, 24f, 68f, 80f, 14f, 20f, RankInMeta: true),
    };

    /// <summary>The card's corner radius, the copy's inset from its left edge and its air under the copy.</summary>
    public const float HeroRadius = 8f, CopyInsetX = 28f, CopyBottom = 24f;

    /// <summary>The veil's height and its bottom opacity (the boards: clear to rgba(0,0,0,0.62) over the last 180).</summary>
    public const float VeilHeight = 180f, VeilBottomAlpha = 0.62f;

    /// <summary>The veil's colours: clear at its top, 62 % black at the card's foot; the plate's hairline (white at 12 %); the photograph's decode edge; the skeleton's line bar.</summary>
    public static ColorF VeilClear => ColorF.FromRgba(0, 0, 0, 0);

    /// <inheritdoc cref="VeilClear"/>
    public static ColorF VeilEdge => ColorF.FromRgba(0, 0, 0, 0x9E);

    /// <inheritdoc cref="VeilClear"/>
    public static ColorF PlateStroke => ColorF.FromRgba(255, 255, 255, 0x1F);

    /// <inheritdoc cref="VeilClear"/>
    public const int PhotoDecode = 1280;

    /// <inheritdoc cref="VeilClear"/>
    public const float ShimmerLineW = 240f;

    /// <summary>The Verified seal (14 round, its check 9) and the gap to its words; the lines' gap in the copy; the actions' gap and their air under the card's baseline.</summary>
    public const float SealSize = 14f, SealGlyph = 9f, SealGap = 6f, CopyGap = 2f, ActionsGap = 8f, ActionsBottom = 8f;

    /// <summary>The name's size (Display 68) and line (80), its auto-fit floor; the meta line's size and line; the Verified words'.</summary>
    public const float NameSize = 68f, NameLine = 80f, NameFloor = 40f, MetaSize = 14f, MetaLine = 20f, VerifiedSize = 12f, VerifiedLine = 16f;

    /// <summary>A hero action's plate: 70 % of the base with a 12 % hairline (the boards: rgba(32,32,32,0.7), rgba(255,255,255,0.12)).</summary>
    public const float PlateAlpha = 0.7f, PlateStrokeAlpha = 0.12f;

    /// <summary>The Compact actions row under the card: 12 under it; the Play word's padding.</summary>
    public const float ActionsRowTop = 12f, PlayPad = 16f;

    /// <summary>The air between the hero card and the pivot.</summary>
    public const float HeroToPivot = 16f;

    // ── the pivot ──

    /// <summary>The pivot's height (the SelectorBar's 40 items) and its hairline: the sticky bar is 41 tall, which is the clearance a section parks under.</summary>
    public const float PivotItemHeight = 40f, PivotHairline = 1f, PivotGap = 4f;

    /// <summary>A tab's padding (the board: 0 12 in a 40 item).</summary>
    public static readonly Edges4 PivotItemPadding = new(12f, 0f, 12f, 0f);

    /// <summary>The clearance of a pivot click and the spy's band edge: the bar and its hairline.</summary>
    public const float PivotClearance = PivotItemHeight + PivotHairline;

    // ── the overview ──

    /// <summary>The page width from which the overview is two columns (and below which it stacks), the gap between columns, their weights, and the vertical gap
    /// between the overview's blocks and between sections.</summary>
    public const float TwoColumnFrom = 1100f, ColumnGap = 36f, HugeColumnGap = 48f, ColumnHold = 24f, PopularGrow = 1.2f, OtherGrow = 1f, BlockGap = 12f, SectionGap = 32f;

    /// <summary>The air under the pivot before the overview, and the dock's extra under the last section.</summary>
    public const float OverviewTop = 16f, DockExtra = 24f;

    /// <summary>The latest release card: r8 on the card fill, 12 padding and gap, a 64 cover (r4), the eyebrow 12, the title 14 semibold, the sub 12; Play at 32.</summary>
    public const float LatestPad = 12f, LatestGap = 12f, LatestCoverEdge = 64f, LatestCoverRadius = 4f, LatestEyebrow = 12f, LatestEyebrowLine = 16f, LatestTitle = 14f,
        LatestTitleLine = 20f, LatestSub = 12f, LatestSubLine = 16f, GroupTitle = 12f, GroupTitleLine = 16f;

    /// <summary>The latest cover's decode bucket.</summary>
    public const int LatestDecode = 192;

    /// <summary>The discography preview: three covers 16 apart, the title 14/20 semibold 6 under, the sub 12/16; the combos 32 tall.</summary>
    public const float PreviewColumns = 3f, PreviewGap = 16f, PreviewTitleGap = 6f, PreviewCardRadius = 8f, ComboHeight = 32f;

    /// <summary>At Huge the preview shows Albums and Singles &amp; EPs as two titled rows (the boards), three covers each.</summary>
    public const int HugePreviewRows = 2;

    // ── the shelves (appears on, videos, playlists, concerts, merch, gallery, fans) ──

    /// <summary>A shelf's cards: 150 to 200 wide 12 apart; a Compact strip's fixed 164 with the next card peeking; ten columns at most from Huge.</summary>
    public const float ShelfMin = 150f, ShelfMax = 200f, ShelfGap = 12f, CompactCard = 164f;

    /// <summary>The Huge shelf's column cap.</summary>
    public const int HugeShelfColumns = 10;

    // ── about and the concert stub ──

    /// <summary>The About card's image band, and the widest the About block grows (it never spans a Huge page).</summary>
    public const float AboutBand = 130f, AboutMax = 760f;

    /// <summary>A concert stub (a shelf card): the date lane 44 wide, the day 20/28, the venue 14/20, the pin 12, the city 12/16.</summary>
    public const float StubDateW = 44f, StubDaySize = 20f, StubDayLine = 28f, StubTextGap = 2f, StubVenueSize = 14f, StubVenueLine = 20f, StubPinGap = 4f,
        StubPinSize = 12f, StubCitySize = 12f, StubCityLine = 16f;

    // ── the skeleton ──

    /// <summary>The loading page's reserved bones: the overview's rows and the preview's cards.</summary>
    public const int ShimmerRows = 5, ShimmerCards = 3;

    // ── the pick card ──
    /// <summary>The rail arm's photograph band height; the column supplies the width.</summary>
    public const float PickPhotoHeight = 150f;

    /// <summary>The band arm: photography as a right column, stretched to the panel.</summary>
    public const float PickPhotoColumnW = 300f;

    /// <summary>The photo's width / height (its decode height derives from it).</summary>
    public const float PickPhotoAspect = 1.6f;

    /// <summary>The photo's decode width.</summary>
    public const int PickPhotoDecodeW = 640;

    /// <summary>The artist avatar in the pick's header.</summary>
    public const float PickAvatar = 32f;

    /// <summary>The record's cover in the pick's footer.</summary>
    public const float PickCover = 44f;

    // ── the magazine ──
    /// <summary>Added to the player dock's reserve under the last section.</summary>
    public const float MagazineDockExtra = 40f;

    /// <summary>The skeleton's grid placeholders.</summary>
    public const int ShimmerCells = 5;

    // ── the biography band ──
    /// <summary>The biography text's size.</summary>
    public const float BioFontSize = 14f;

    /// <summary>The share of the band's width the biography text takes at the wide mode.</summary>
    public const float BioWideShare = 0.62f;

    /// <summary>The card's horizontal padding and border the text measure gives up.</summary>
    public const float BioTextInset = 60f;

    /// <summary>A profile-facts tile's flex basis.</summary>
    public const float FactBasis = 140f;

    // ── the city bars ──
    /// <summary>One city's row height.</summary>
    public const float CityRow = 34f;

    /// <summary>The gap between a city's name line and its bar.</summary>
    public const float CityGap = 4f;

    /// <summary>The accent bar's height.</summary>
    public const float CityBar = 4f;

    /// <summary>The city name's size.</summary>
    public const float CityNameSize = 14f;

    // ── the banners ──
    /// <summary>The upcoming card's cover (the band arm) and its decode width.</summary>
    public const float UpcomingCover = 88f;
    public const int UpcomingCoverDecode = 192;

    /// <summary>The latest-release banner's cover.</summary>
    public const float LatestCover = 72f;

    /// <summary>The tour banner's accent circle, its glyph, its headline, the eyebrow-to-headline gap and its chevron.</summary>
    public const float TourBadge = 44f;
    public const float TourIcon = 18f;
    public const float TourHeadline = 16f;
    public const float TourGap = 2f;
    public const float TourChevron = 16f;
}
