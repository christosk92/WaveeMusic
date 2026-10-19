// ── Themes/WinUi/Structure/Rules/WinUiDetailRules.cs — the numbers of WinUI's detail frame, album and hero (themes plan 6.2; P6 W6a) ──────────────────
//
// What the detail views (`Views\Detail\WinUiDetail*.cs`, `WinUiAlbum*.cs`) read instead of writing a literal (WVTH003 allows literals only in `Rules\**`).
// Boards: W-Album and its -Compact / -Small / -Huge / -Huge4K twins, W-SkeletonAlbum (-Compact / -Huge), W-Shell-Short. The tier records are `Tiers.Detail.cs`.
//
//   THE HERO (tall). The cover at the tier's edge (168; 232 at Huge; 160 centred at Compact), r8 with the card shadow; the words bottom-aligned beside it
//   (Compact: stacked and centred under it): the eyebrow (Caption 12/16 in Text 2; 14/20 at Huge), the title (Title Large 40/52, Title 28/36 at Compact), the
//   billed artists as links in the accent beside their avatars (24 with a 2 ring, 28 at Huge, -8 overlap) and the meta, then the action row 12 under (14 at
//   Huge): the accent Play SplitButton, Shuffle and Save as bordered words (glyphs alone at Compact), Share and the "..." plain. Targets 32; 40 at Compact.
//   THE SHORT HERO (a window under 720 tall): the 104 cover, the title 28/36, no eyebrow; with the list bar under it the hero is 168 tall, so the sticky bar
//   takes over after 120 of scroll.
//   THE STICKY BAR: 48 tall (the band the table's chrome pins under and the rows clip at), a 2c2c2c layer with a hairline under it, a 32 cover, the title
//   Body Strong 14/20 over the byline Caption 12/16, and Play as an accent button, on the tier's inset.
//
// Public, so `Wavee.Tests` pins the numbers.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's detail pages.</summary>
public static class WinUiDetailRules
{

    // ── the arms' cross-fade, the dock's reveal, the title's lines, the decode buckets ───────────────────────────────────────

    /// <summary>An arm enters and leaves on its opacity alone (a theme swap's cross-fade), on the control's fast beat.</summary>
    public static readonly EnterExit ArmFade = new(Opacity: 0f, Active: true);

    /// <inheritdoc cref="ArmFade"/>
    public static MotionTokenDef ArmFadeMotion => MotionTok.ControlFast;

    /// <summary>The docked slot easing its width open and closed (350 ms open, 120 ms close, the WinUI sheet spline), the column inside keeping its full width.</summary>
    public static readonly LayoutTransition DockReveal = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(350f, SheetSpline), SizeMode.Reveal, ExitDynamics: TransitionDynamics.Tween(120f, SheetSpline));

    /// <summary>The title wraps to at most this many lines.</summary>
    public const int TitleMaxLines = 2;

    /// <summary>The list bar's own inset inside the page inset (the table's bar surface pads 6).</summary>
    public const float BarInset = 6f;

    /// <summary>The cover's decode bucket for an edge, and the sticky bar's 32 cover.</summary>
    public static int CoverDecode(float edge) => edge <= 128f ? 256 : edge <= 288f ? 512 : 1024;

    /// <inheritdoc cref="CoverDecode"/>
    public const int BandCoverDecode = 256;

    /// <summary>The title bone's width: a share of the title's size.</summary>
    public static float SkelTitleW(WinUiHeroForm form) => form.TitleSize * 8f;

    // ── the insights sheet and the docked pane (the shared facts bento) ────────────────────────────────────────────────────

    /// <summary>The sheet's width, or this share of what it covers when that is narrower.</summary>
    public const float SheetWidth = 320f, SheetCoverFraction = 0.76f;

    /// <summary>The sheet's width over a region this wide; an unmeasured region answers <see cref="SheetWidth"/>.</summary>
    public static float SheetWidthFor(float coveredWidth)
        => !(coveredWidth > 0f) ? SheetWidth : MathF.Max(1f, MathF.Min(SheetWidth, MathF.Floor(coveredWidth * SheetCoverFraction)));

    /// <summary>The sheet's padding and the gap under its header; the bar toggle's edge and glyph; the close button's; the title's size and line; the cast's width.</summary>
    public const float SheetPad = 16f, SheetGap = 12f, SheetToggleEdge = 32f, SheetToggleGlyph = 15f, SheetCloseEdge = 32f, SheetCloseGlyph = 14f,
        SheetTitleSize = 16f, SheetTitleLine = 22f, SheetShadowW = 32f;

    /// <summary>The sheet's spline (0.1, 0.9, 0.2, 1.0).</summary>
    public static readonly EasingSpec SheetSpline = EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1.0f);

    /// <summary>The cast under the sheet's left edge: clear to 35 % black.</summary>
    public static ColorF SheetCastClear => ColorF.FromRgba(0, 0, 0, 0);

    /// <inheritdoc cref="SheetCastClear"/>
    public static ColorF SheetCastEdge => ColorF.FromRgba(0, 0, 0, 0x59);

    /// <summary>The docked insights pane (a column of the page): its width and the padding around the sheet's own.</summary>
    public const float DockedInsightsWidth = 360f;

    /// <inheritdoc cref="DockedInsightsWidth"/>
    public static readonly Edges4 DockPadding = new(4f, 8f, 4f, 8f);

    // ── the billed-artist line box ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The names' size, the "+N"'s size and line, and the trigger's side padding.</summary>
    public const float ArtistNamesSize = 14f, ArtistPlusSize = 12f, ArtistPlusLine = 16f, ArtistTriggerPadX = 6f;

    // ── the page inset (the frame's: 16 / 24 / 36 / 48, top 16 / 20 / 24 / 32) ──────────────────────────────────────────────

    /// <summary>The inset left and right of a window tier.</summary>
    public static float InsetOf(WidthTier tier) => tier switch { WidthTier.Compact => 16f, WidthTier.Small => 24f, WidthTier.Standard => 36f, _ => 48f };

    /// <summary>The inset at the top of a window tier.</summary>
    public static float InsetTopOf(WidthTier tier) => tier switch { WidthTier.Compact => 16f, WidthTier.Small => 20f, WidthTier.Standard => 24f, _ => 32f };

    /// <summary>The hit target of the hero's actions: 32, and the Compact 40.</summary>
    public const float Target = 32f, CompactTarget = 40f;

    /// <summary>The target of a window tier's buttons (32; the Compact 40).</summary>
    public static float TargetOf(WidthTier tier) => tier == WidthTier.Compact ? CompactTarget : Target;

    // ── the hero forms ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Compact (W-Album-Compact): a 160 cover centred, 12 under it, the words centred 2 apart, Title 28/36, the actions 12 under, 12 to the list.</summary>
    public static WinUiHeroForm CompactHero { get; } = new(Stacked: true, Cover: 160f, Gap: 12f, Eyebrow: true, EyebrowSize: 12f, EyebrowLine: 16f,
        TitleSize: 28f, TitleLine: 36f, InfoGap: 2f, Avatar: 0f, ActionTop: 12f, ListGap: 12f, TopInset: 16f);

    /// <summary>Small (W-Album-Small): the horizontal hero, a 168 cover, 24 gap, 20 over it.</summary>
    public static WinUiHeroForm SmallHero { get; } = new(Stacked: false, Cover: 168f, Gap: 24f, Eyebrow: true, EyebrowSize: 12f, EyebrowLine: 16f,
        TitleSize: 40f, TitleLine: 52f, InfoGap: 4f, Avatar: 24f, ActionTop: 12f, ListGap: 12f, TopInset: 20f);

    /// <summary>Standard (W-Album): the same, 24 over it and 20 to the list bar.</summary>
    public static WinUiHeroForm StandardHero { get; } = SmallHero with { ListGap = 20f, TopInset = 24f };

    /// <summary>Huge (W-Album-Huge): a 232 cover, 32 gap, the eyebrow 14/20, the words 6 apart, 28 avatars, the actions 14 under, 24 to the list, 32 over it.</summary>
    public static WinUiHeroForm HugeHero { get; } = new(Stacked: false, Cover: 232f, Gap: 32f, Eyebrow: true, EyebrowSize: 14f, EyebrowLine: 20f,
        TitleSize: 40f, TitleLine: 52f, InfoGap: 6f, Avatar: 28f, ActionTop: 14f, ListGap: 24f, TopInset: 32f);

    /// <summary>The short hero (W-Tiers "Small (height 600): short hero, cover 128, Title 28"; a window under 720 tall in every tier): the 128 cover, Title 28/36, no eyebrow,
    /// the air over it 8 and 8 to the list bar.</summary>
    public static WinUiHeroForm ShortHero { get; } = new(Stacked: false, Cover: 128f, Gap: 16f, Eyebrow: false, EyebrowSize: 12f, EyebrowLine: 16f,
        TitleSize: 28f, TitleLine: 36f, InfoGap: 2f, Avatar: 0f, ActionTop: 8f, ListGap: 8f, TopInset: 8f);

    /// <summary>The short hero at Compact (W-Tiers: "cover 128 left, Title 28"): the same, 12 between the cover and the words.</summary>
    public static WinUiHeroForm ShortHeroCompact { get; } = ShortHero with { Gap = 12f };

    /// <summary>The cover's corner radius (r8: W-Tokens' cover) and its shadow blur and alpha (the boards: 0 8 16 rgba(0,0,0,0.26)).</summary>
    public const float CoverRadius = 8f;

    /// <summary>The attribution's avatar ring (the card's colour round the portrait), and how far each overlaps the next.</summary>
    public const float AvatarRing = 2f, AvatarOverlap = 8f, AvatarGap = 8f;

    /// <summary>At most this many portraits stand in the pile (the board draws two); what leads the meta after the names.</summary>
    public const int AvatarMax = 3;

    /// <summary>What joins the names to the meta ("Edward Maya, Vika Jigulina · 15 songs · 57 min").</summary>
    public const string MetaLead = " · ";

    /// <summary>The attribution line (14/20) and the meta line it carries (12/16 stacked, in Text 3).</summary>
    public const float AttributionSize = 14f, AttributionLine = 20f, MetaSize = 12f, MetaLine = 16f;

    /// <summary>The attribution row's FIXED box: the ringed portrait where the form draws portraits (24 + 2 x 2 = 28; 32 at Huge), else the 20 line. The row is reserved
    /// at this height from the first frame (the skeleton, a meta still loading, the facts arriving one by one), so nothing in the hero moves when its words land.</summary>
    public static float AttributionRow(float avatar) => avatar > 0f ? MathF.Max(avatar + 2f * AvatarRing, AttributionLine) : AttributionLine;

    /// <summary>The Play SplitButton: the label half's padding (0 14: the board's, 0 20 at Compact), the 32 chevron half, the 1-px divider at 20 %.</summary>
    /// <summary>Segoe Fluent Icons' SOLID transport pair (the boards draw the filled triangle and the bars).</summary>
    public const string PlaySolid = "", PauseSolid = "";

    public const float PlayPad = 14f, PlayPadCompact = 20f, PlayGlyph = 16f, PlayGap = 8f, SplitChevronGlyph = 12f, SplitDividerInk = 0.2f;

    /// <summary>The action row's gap (8), and a bordered action's padding (0 12), glyph (16) and gap to its word (8).</summary>
    public const float ActionGap = 8f, ActionPad = 12f, ActionGlyph = 16f, ActionWordGap = 8f;

    /// <summary>The row of actions wraps at the words' measure; below this width the labelled Shuffle and Save fold to glyphs (a Small hero at its narrowest).</summary>
    public const float LabelledActionsMin = 480f;

    // ── the sticky bar ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The bar's height (the band the hero collapses into), its cover and gap, the title and byline, and the Play button's padding.</summary>
    public const float BandHeight = 48f, BandCover = 32f, BandCoverRadius = 4f, BandGap = 12f, BandTitleSize = 14f, BandTitleLine = 20f,
        BandBylineSize = 12f, BandBylineLine = 16f, BandPlayPad = 16f;

    /// <summary>The short hero's height (8 over, the 128 cover, 8, the 44 list bar): the bar takes over after this less the 48 of scroll.</summary>
    public const float ShortHeroHeight = 188f, ShortCollapse = ShortHeroHeight - BandHeight;

    // ── the notice strip ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The strip's air: left and right (the page inset is added), top, bottom.</summary>
    public const float NoticeTop = 8f, NoticeBottom = 4f;

    // ── the aside (W-Album-Huge, W-Album-Huge4K) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The aside column's width (440; 480 from 3200), the air between it and the list (48), its top air (4) and the gap between its sections (24).</summary>
    public const float AsideWidth = 440f, AsideWideWidth = 480f, AsideGap = 48f, AsideTop = 4f, AsideSectionGap = 24f, AsideTitleGap = 8f;

    /// <summary>A section's title in the aside and under the table: Subtitle 20/28.</summary>
    public const float SectionTitleGap = 12f, SectionGap = 24f;

    /// <summary>The fact card (About this album): r8, a 44-tall row 12 gap, 16 padding, the key at 12 in a 120 lane, the value 14.</summary>
    public const float FactRowMin = 44f, FactPadX = 16f, FactKeyWidth = 120f, FactKeySize = 12f, FactValueSize = 14f, FactGap = 12f, CardRadius = 8f;

    /// <summary>An artist row ("Artists on this album"): 64 tall, 12 padding and gap, a 44 portrait, Follow at 32.</summary>
    public const float ArtistRowHeight = 64f, ArtistRowPad = 12f, ArtistPortrait = 44f, ArtistRowGap = 12f, ArtistNameSize = 14f, ArtistNameLine = 20f,
        ArtistSubSize = 12f, ArtistSubLine = 16f, ArtistRowGapBetween = 8f;

    /// <summary>"More by": two columns of covers 16 apart, the title 14/20 semibold 6 under, the sub 12/16.</summary>
    public const float MoreColumns = 2f, MoreGap = 16f, MoreTitleGap = 6f;

    /// <summary>"Fans also like" under the list at Huge: seven columns 24 apart, a 56 portrait 12 left of the name.</summary>
    public const float FansPortrait = 56f, FansGap = 24f, FansNameGap = 12f, FansRowGap = 16f, FansItemMin = 130f;

    /// <summary>The most fans in a row.</summary>
    public const int FansColumns = 7;

    // ── the trailing band under the list (Compact, Small, Standard) ──────────────────────────────────────────────────────────

    /// <summary>The band's section: title Subtitle 20/28, 12 over its body, 24 between sections; "Show all N" in the accent on the title's line.</summary>
    public const float BandSectionGap = 24f, BandTitleGap = 12f;

    /// <summary>The shelf's cards: between 150 and 200 wide, 12 apart; a Compact strip's 140 with the next card peeking.</summary>
    public const float ShelfMinCard = 150f, ShelfMaxCard = 200f, ShelfGap = 12f;

    /// <summary>The stack's rows (a related album or playlist, a merch listing): the row 56 tall, a 40 cover, the 4 between rows.</summary>
    public const float StackRow = 56f, StackArt = 40f, StackRowGap = 4f, MerchRow = 56f, MerchPriceSize = 14f, PlaceholderRow = 56f;

    /// <summary>The video hero card (one video): a 200x112 thumb beside its words; the shelf's 16:9 cards 220 to 300 wide.</summary>
    public const float VideoThumbW = 200f, VideoThumbH = 112f, VideoFab = 40f, VideoCardMinW = 220f, VideoCardMaxW = 300f, VideoAspect = 16f / 9f;

    /// <summary>The flyout of every billed artist: the row 44 tall, its portrait 32, the name 14/20 semibold, the row radius 4.</summary>
    public const float FlyoutPortrait = 32f, FlyoutNameSize = 14f, FlyoutNameLine = 20f, FlyoutRowRadius = 4f, FlyoutListGap = 2f;

    // ── the skeleton (W-SkeletonAlbum) ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The bones: the corner radius, the eyebrow, the attribution row's avatar and the action row's four plates.</summary>
    public const float BoneRadius = 4f, SkelEyebrowW = 150f, SkelEyebrowH = 16f, SkelNameW = 272f, SkelNameH = 20f, SkelAvatar = 24f,
        SkelPlayW = 104f, SkelShuffleW = 88f, SkelSaveW = 88f, SkelMoreW = 32f, SkelToolbarPlate = 72f;
}
