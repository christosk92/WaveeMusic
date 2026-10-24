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

using FluentGpu.Animation;
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

    /// <summary>A hero photograph with faces (`Palette.Subject`, lane B10) places them centred across and this share of the way down the band above the
    /// copy (the Verified line, or the name): its middle. Without faces the card keeps its centred crop.</summary>
    public const float PhotoFacesAtX = 0.5f, PhotoFacesAboveCopy = 0.5f;

    /// <inheritdoc cref="VeilClear"/>
    public const float ShimmerLineW = 240f;

    /// <summary>The Verified seal (14 round, its check 9) and the gap to its words; the lines' gap in the copy; the actions' gap and their air under the card's baseline.</summary>
    public const float SealSize = 14f, SealGlyph = 9f, SealGap = 6f, CopyGap = 2f, ActionsGap = 8f, ActionsBottom = 8f;

    /// <summary>The Display face's natural line box per em: the name's box is the taller of it and its line height (what the scrim's bands measure).</summary>
    public const float NameNaturalLine = 1.33f;

    /// <summary>The name's size (Display 68) and line (80), its auto-fit floor; the meta line's size and line; the Verified words'.</summary>
    public const float NameSize = 68f, NameLine = 80f, NameFloor = 40f, MetaSize = 14f, MetaLine = 20f, VerifiedSize = 12f, VerifiedLine = 16f;

    /// <summary>A hero action's plate: 70 % of the base with a 12 % hairline (the boards: rgba(32,32,32,0.7), rgba(255,255,255,0.12)).</summary>
    public const float PlateAlpha = 0.7f, PlateStrokeAlpha = 0.12f;

    /// <summary>The plate itself: the board's rgba(32,32,32,0.7) in light AND dark. Everything on the photograph is on-media (light ink on a dark plate),
    /// whatever the theme kind; hover and press step down the on-media scrim ramp (`Controls.OnMediaPlatePalette`).</summary>
    public static ColorF PlateFill => ColorF.FromRgba(32, 32, 32) with { A = PlateAlpha };

    /// <summary>The most the adaptive scrim (`Controls.ScrimBoost`) darkens a bright photograph over the 62 % veil: 0.6 keeps the foot under 85 % black and the
    /// photograph's top clear, so it stays a photograph.</summary>
    public const float BoostCeiling = 0.6f;

    /// <summary>The Compact actions row under the card: 12 under it; the Play word's padding.</summary>
    public const float ActionsRowTop = 12f, PlayPad = 16f;

    /// <summary>The air between the hero card and the pivot.</summary>
    public const float HeroToPivot = 16f;

    // ── the pivot ──

    /// <summary>The pivot's height (the SelectorBar's 40 items) and its hairline: the sticky bar is 41 tall, which is the clearance a section parks under.</summary>
    public const float PivotItemHeight = 40f, PivotHairline = 1f, PivotGap = 4f;
    /// <summary>The pill's place in a pivot item (W-Artist: 3 tall, 4 above the 40's foot, under a word centred in the 40). The SelectorBar pads its items 4 down
    /// inside the 40 row, so the lift is the board's 4 plus that 4; the gap parts the pill from the word's line box.</summary>
    public const float PivotPillLift = 8f, PivotPillGap = 4f;

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

    /// <summary>How far Popular's clip reaches above the column and past its end (padding in, margin out: nothing moves): the chart head's 28 chevrons'
    /// share of the 40 touch floor (`TouchTargetPolicy.CompactFloor`), so the clip does not cut their hit slop at a Compact window.</summary>
    public const float PopularHitRoom = 6f;
}

/// <summary>How the artist overview restacks when its column count changes (two columns <-> one: a window resize across the 1100 page edge, a pane
/// docking at Standard). WinUI's reposition, a fade-through: the side column (the latest release, Discography, the upcoming card, the pick) fades out
/// where it stands, Popular holds until it is gone and then resizes, the overview's height eases (the sections under it ride it), and the side column
/// fades in at its new slot once Popular has made room. Nothing travels across other content, so no card ever passes over another card or text.</summary>
public static class WinUiArtistMotion
{
    /// <summary>The restack's resize: the discovery grids' 250 ms column FLIP.</summary>
    public const float RestackMs = WinUiDiscoveryMotion.ReflowMs;

    /// <summary>The side column's fade-out where it stands (WinUI's 167 ms exit), and its fade-in at the new slot (250 ms).</summary>
    public const float FadeOutMs = 167f, FadeInMs = 250f;

    /// <summary>The share of <see cref="RestackMs"/> Popular has run before the side column starts fading in: Popular's eased edge is then inside the
    /// column gap, so the incoming column never lies under its rows.</summary>
    public const float FadeInLead = 0.6f;

    /// <summary>The fade-in's start: the fade-out, then most of Popular's resize.</summary>
    public const float FadeInDelayMs = FadeOutMs + RestackMs * FadeInLead;

    /// <summary>A layout change of the side column outside a restack (a pane docking a frame before the column count follows it) holds this long before
    /// it glides, so the frames between the width change and the restack show the column where it stood, not a step toward a slot it never takes.</summary>
    public const float HoldMs = 64f;

    /// <summary>The overview's height: it eases through REAL layout (a reflow, which also clips the overview to it), so every section under it is laid
    /// out at the eased height's foot each tick. A flow reveal (layout once, the presented extent eased at paint time) landed at once whenever the
    /// change lay wholly below the view (the restack grows the overview past the window's foot), so the body's layout stepped ~455 DIP in one frame.</summary>
    public static readonly LayoutTransition OverviewHeight = new(TransitionChannels.Size,
        TransitionDynamics.Tween(RestackMs, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>The Popular column: it resizes (full width stacked, its weight's share beside the side column), re-laying the chart at the eased width every
    /// tick, after the side column has faded out of the way (it never widens under the outgoing column).</summary>
    public static readonly LayoutTransition Column = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(RestackMs, Easing.SmoothOut), SizeMode.Relayout, DelayMs: FadeOutMs);

    /// <summary>Popular's resize once a restack that WIDENS it has held it at its old width for <see cref="FadeOutMs"/> (the view lays it out at that
    /// width, then releases it): <see cref="Column"/> without the delay, which the hold has already spent.</summary>
    public static readonly LayoutTransition ColumnReleased = Column with { DelayMs = 0f };

    /// <summary>The restack's cross-fade, a DECLARED swap family: the overview's arrangements (one column, two, three at Huge) are different trees of
    /// the same blocks (the latest release over Discography beside Popular; Discography its own column with the release, upcoming and the pick in a
    /// third), so a restack fades the old column out where it stands and the new one in at its slot, and nothing travels across Popular.</summary>
    public const string RestackFamily = "WinUi.Artist.Restack";

    /// <summary>The side column in one arrangement: a restack replaces it (keyed by the arrangement), so the outgoing one fades out where it stands and
    /// the incoming one fades in at its slot once Popular has resized. Inside one arrangement its layout changes hold <see cref="HoldMs"/>, then glide.</summary>
    public static readonly LayoutTransition Side = new(TransitionChannels.Position | TransitionChannels.Size | TransitionChannels.Opacity,
        TransitionDynamics.Tween(FadeInMs, Easing.SmoothOut), SizeMode.Reveal,
        Enter: SwapFamilies.Declare(RestackFamily, new EnterExit(Opacity: 0f, Active: true, DelayMs: FadeInDelayMs)),
        Exit: SwapFamilies.Declare(RestackFamily, new EnterExit(Opacity: 0f, Active: true)),
        ExitDynamics: TransitionDynamics.Tween(FadeOutMs, Easing.Linear), DelayMs: HoldMs, ExitDelayMs: 0f);

    /// <summary>The hero card's height across a tier crossing (200 / 220 / 260 / 420): it eases through real layout, so the pivot and the page under the
    /// card ride it; the photograph, the veil and the copy stretch with the card.</summary>
    public static readonly LayoutTransition HeroHeight = new(TransitionChannels.Size, TransitionDynamics.Tween(WinUiDiscoveryMotion.ReflowMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>A section's height across a tier crossing (a discography grid re-fitting its columns, a shelf re-sizing its cards): it eases through real layout,
    /// so the sections under it ride it.</summary>
    public static readonly LayoutTransition SectionHeight = new(TransitionChannels.Size, TransitionDynamics.Tween(RestackMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>The hero's actions between their two presentations, a DECLARED swap family: on-media plated controls inside the card (32, words at Huge)
    /// and the Compact page row under it (the 40 targets, unplated, "..." at the row's end). The row's slot eases its height.</summary>
    public static readonly EnterExit ActionsSwap = SwapFamilies.Declare("WinUi.Artist.Actions", new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The actions' cross-fade timing.</summary>
    public static MotionTokenDef ActionsSwapMotion => MotionTok.ControlFast;

    /// <summary>The card's plated actions coming and going with the Compact form: the cross-fade, and their width eased through real layout (clipped: a wipe
    /// from their end as they leave, an opening as they come).</summary>
    public static readonly LayoutTransition CardActions = new(TransitionChannels.Size | TransitionChannels.Opacity,
        TransitionDynamics.Tween(FadeOutMs, Easing.SmoothOut), Size: SizeMode.Reflow, Axes: SizeAxes.Width,
        Enter: ActionsSwap, Exit: ActionsSwap);

    /// <summary>A block inside the side column (the latest release, the upcoming card, the pick, the discography preview): it moves and resizes with the
    /// column inside one arrangement, the size laid out at once and revealed.</summary>
    public static readonly LayoutTransition Block = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(RestackMs, Easing.SmoothOut), SizeMode.Reveal);
}
