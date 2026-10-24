// ── Themes/Zune/Structure/Rules/ZuneArtistRules.cs — the numbers and tier records of Zune's artist family (themes plan 6.3; P5 Z6a2) ─────
//
// Boards Z-Artist (Standard, the target), Z-Artist-Compact / -Small / -Huge / -Huge4K for the other tiers (ZT6 tunes them). WVTH003 allows
// literals only here, in `Rules\**`; WVTH013 keeps every width decision in the tier record (`ZuneArtistTiers`, by CONTENT tier).
//
//   THE HERO (Z-Artist): no photograph of its own (the artist's header is the material's BLEED, published to the band through the page chrome)
//   and no veil: the cover's blend wash tints it. 340 tall at Standard, the "verified artist" eyebrow 36 down, the name a PANORAMA word (120/130
//   at the light 200; 64 compact below 640; 150 at Huge) standing 8 left of the gutter so its glyph edge lines up, the caption under it
//   ("#3 in the world" in the accent · monthly listeners · followers) 16 up from the foot. The hero HUGS its copy above its record height, so a
//   long caption pushes the body down instead of running under it. THE ACTIONS have one home per verb (audit A4, an owner directive over the
//   boards): from Small up play, shuffle and follow live ONLY in the band's row 2; the hero keeps the verbs the band does not carry (radio,
//   "...") as unlabelled rings, at the bottom right at Small and in a row under the caption at Huge; Compact (no band actions) puts all of them
//   in a 40 row under the hero. THE PAGE is the system page column (`ZuneFrameRules.PageColumn`: the gutter, 2400 cap, centred, audit A1).
//   THE BODY: 24 under the hero, Popular (the chart, its columns by tier, capped at 1200 at Huge) beside a rail (300; Huge 360) holding the upcoming release, the
//   latest release (a 96 art row) and the artist pick (a 64 art row), 48 apart; then the sections 28 apart: the discography facets (a heading
//   row with "discography" at its end over the grid), the shelves of 154 covers 16 apart, the tour, "about", the gallery, fans also like.
//
// Public, so `Wavee.Tests` pins them (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Where the hero's primary actions (play · shuffle · follow) stand in a tier.</summary>
public enum ZuneArtistActions : byte
{
    /// <summary>The band's row 2 carries them (Small and up: one home per verb, audit A4).</summary>
    Band,
    /// <summary>A 40 row under the hero, with radio and "..." (Compact: the band carries no actions).</summary>
    BelowHero,
    /// <summary>The hero's row under the caption, beside the secondary rings where the tier has that row: a Short window folds the band's row 2 at
    /// rest, so play, shuffle and follow come back to the page (V4: the follow state is visible at rest; Z-Artist-Small).</summary>
    Hero,
}

/// <summary>Where the hero's secondary rings (radio · "...", the verbs the band does not carry) stand in a tier.</summary>
public enum ZuneArtistSecondary : byte
{
    /// <summary>Nowhere in the hero (Compact: the row under the hero carries them; Standard: the tuned board draws none).</summary>
    None,
    /// <summary>At the hero's bottom right (Small).</summary>
    Corner,
    /// <summary>A row under the caption (Huge and Huge 4K).</summary>
    Row,
}

/// <summary>What Zune's artist family lays out in one CONTENT tier.</summary>
public sealed record ZuneArtistLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions { get; init; } = ImmutableArray<TierRegion>.Empty;

    /// <summary>The page's gutter (the frame's, so the hero's copy and the band share an x).</summary>
    public required float Gutter { get; init; }

    /// <summary>The gap between sections, and between the hero and the first.</summary>
    public required float ZoneGap { get; init; }

    /// <summary>The body's top under the hero.</summary>
    public required float BodyTop { get; init; }

    /// <summary>The hero's height.</summary>
    public required float HeroH { get; init; }

    /// <summary>The hero's eyebrow's top inset and the caption's bottom inset.</summary>
    public required float EyebrowTop { get; init; }

    /// <inheritdoc cref="EyebrowTop"/>
    public required float CaptionBottom { get; init; }

    /// <summary>The name is the compact panorama (64) rather than the panorama.</summary>
    public required bool CompactName { get; init; }

    /// <summary>The name's size override (0: the role's own) with its line height and tracking.</summary>
    public required float NameSize { get; init; }

    /// <inheritdoc cref="NameSize"/>
    public required float NameLine { get; init; }

    /// <inheritdoc cref="NameSize"/>
    public required float NameTracking { get; init; }

    /// <summary>The name lines the hero holds whatever the name (audit B7, V11): the loading skeleton does not know the name, so the hero reserves
    /// the lines a name of this tier commonly takes, in the skeleton and on the loaded page alike, and the name landing moves nothing. Compact's
    /// 64 name wraps most two-word names at a phone's width (two lines); the wider tiers hold one. A name past the reserve still grows the hero.</summary>
    public int NameReserveLines { get; init; } = 1;

    /// <summary>Where the primary actions stand.</summary>
    public required ZuneArtistActions Actions { get; init; }

    /// <summary>Where the secondary rings stand.</summary>
    public required ZuneArtistSecondary Secondary { get; init; }

    /// <summary>The chart column's cap beside the rail (0: none). At Huge an uncapped chart spread "title" and "time" 1700 apart (audit B, Artist 2560).</summary>
    public required float ChartMaxW { get; init; }

    /// <summary>The 36 ring (Standard and up) or the 40 Compact target.</summary>
    public required float Target { get; init; }

    /// <summary>The chart's columns and rows per column.</summary>
    public required int ChartColumns { get; init; }

    /// <inheritdoc cref="ChartColumns"/>
    public required int ChartRows { get; init; }

    /// <summary>The rail stands beside the chart (its width), or under it.</summary>
    public required bool RailBeside { get; init; }

    /// <inheritdoc cref="RailBeside"/>
    public required float RailW { get; init; }

    /// <summary>The gap between the chart and the rail.</summary>
    public required float RailGap { get; init; }

    /// <summary>The gap between the rail's items (the board: the 10 under a heading plus the pick heading's own top margin, 22 / 16 / 26 / 32).</summary>
    public float RailStackGap { get; init; } = 22f;

    /// <summary>The chart row's geometry in the tier (the boards: Standard 60 / 48 / 52 lane, Compact and Huge 56 / 44, Small 52 / 40 / 44, 4K 68 / 52 / 60), and the air between the
    /// chart's two columns (48; 64 at 4K).</summary>
    public float ChartRowH { get; init; } = 60f;

    /// <inheritdoc cref="ChartRowH"/>
    public float ChartArt { get; init; } = 48f;

    /// <inheritdoc cref="ChartRowH"/>
    public float ChartLead { get; init; } = 52f;

    /// <inheritdoc cref="ChartRowH"/>
    public float ChartColumnGap { get; init; } = 48f;

    /// <summary>The latest release stands as a card (the cover over its words) rather than a row: Small's 220 rail.</summary>
    public bool LatestStacked { get; init; }

    /// <summary>The latest release's art and the pick's art.</summary>
    public required float LatestArt { get; init; }

    /// <inheritdoc cref="LatestArt"/>
    public required float PickArt { get; init; }

    /// <summary>The discography grid's narrowest cell and its gap.</summary>
    public required float DiscoMinColumn { get; init; }

    /// <inheritdoc cref="DiscoMinColumn"/>
    public required float DiscoGap { get; init; }

    /// <summary>The album drawer's track columns (0: the grid's rule), row pitch and head row.</summary>
    public required int DrawerColumns { get; init; }

    /// <inheritdoc cref="DrawerColumns"/>
    public required float DrawerPitch { get; init; }

    /// <inheritdoc cref="DrawerColumns"/>
    public required float DrawerHead { get; init; }

    /// <summary>"About" stands in two columns (the biography beside the facts), or stacked.</summary>
    public required bool AboutBeside { get; init; }

    /// <summary>The widest the page's column grows.</summary>
    public required float PageMax { get; init; }

    /// <summary>A bare link word's minimum hit edge (the "discography" and "see all" words, the external links, the crumb): 40 at Compact, 0 elsewhere
    /// (the word is its own target).</summary>
    public required float LinkTarget { get; init; }

    /// <summary>A shelf card's width (and the discography grid's narrowest cell is <see cref="DiscoMinColumn"/>), and a music-video card's.</summary>
    public required float ShelfCardW { get; init; }

    /// <inheritdoc cref="ShelfCardW"/>
    public required float VideoCardW { get; init; }

    /// <summary>The lightbox's filmstrip thumb and its top chrome's height, by the WINDOW tier the overlay covers.</summary>
    public required float LightboxThumb { get; init; }

    /// <inheritdoc cref="LightboxThumb"/>
    public required float LightboxChromeH { get; init; }

    /// <summary>The width of the page's column: the page (capped at the measure), less both gutters.</summary>
    public float ColumnWidth(float width) => MathF.Max(1f, MathF.Min(width, PageMax) - Gutter - Gutter);

    /// <summary>The biography text's measure: the column, or its share beside the facts.</summary>
    public float AboutTextWidth(float width)
        => AboutBeside ? MathF.Max(1f, (ColumnWidth(width) - ZuneArtistRules.AboutGap) * ZuneArtistRules.BioShare) : ColumnWidth(width);

    /// <summary>The album drawer's geometry the core's verdict sizes.</summary>
    public Album.DrawerShape Drawer => new(DrawerColumns, DrawerPitch, DrawerHead + ZuneArtistRules.DrawerHeadPad);

    /// <summary>The metrics the core latches for this tier (the hero's height and gutter; no veil, no copy cap).</summary>
    public ArtistHeroMetrics Metrics(ArtistHeroTier sub) => new(sub, HeroH, Gutter, ArtistHeroVeilAxis.Horizontal, ZuneArtistRules.CopyMax);
}

/// <summary>The constants and node keys of Zune's artist family.</summary>
public static class ZuneArtistRules
{
    // ── the node keys the views carry (the tests find them) ──────────────────────────────────────────────────────────────

    /// <summary>The page's column (`zune-artist-page:<route>`), its scroller, the body child, the hero, its name, its caption, its actions, the
    /// popular band, the rail, a section holder (`zune-artist-sec:<key>`), an anchor holder (`zune-artist-anchor:<key>`), a facet heading, the
    /// discography link, and the easing air (`zune-artist-air:top` / `:foot` in the hero, `:body` over the magazine).</summary>
    public const string PagePrefix = "zune-artist-page:", ScrollPrefix = "zune-artist-scroll:", BodyPrefix = "zune-artist-body:",
        HeroKey = "zune-artist-hero", NameKey = "zune-artist-name", EyebrowKey = "zune-artist-eyebrow", CaptionKey = "zune-artist-caption",
        ActionsKey = "zune-artist-actions", PopularKey = "zune-artist-popular", RailKey = "zune-artist-rail",
        SectionPrefix = "zune-artist-sec:", AnchorPrefix = "zune-artist-anchor:", FacetHeadPrefix = "zune-artist-facet-head:",
        DiscographyLinkPrefix = "zune-artist-disco-link:", AboutKey = "zune-artist-about", WashKey = "zune-artist-wash:",
        LightboxKey = "zune-artist-lightbox", DiscoPageKey = "zune-discography-page", DiscoHeadKey = "zune-discography-head",
        AirPrefix = "zune-artist-air:";

    // ── the hero ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The name stands this far left of the gutter, so the glyph edge of the light panorama lines up with the eyebrow.</summary>
    /// <summary>The artist photo's focal point, 0..1 from the top: Spotify's header images and avatars put the face in the upper third, so the shell's bleed (the one
    /// photo behind the title bar, the band and the hero) crops around 28 % down instead of the middle, where only the nose and mouth were left under the chrome.</summary>
    public const float PhotoFocusY = 0.28f;

    public const float NameNudge = 8f;

    /// <summary>The gap between the name and the caption under it.</summary>
    public const float NameCaptionGap = 14f;

    /// <summary>The lines the hero's name may take: it wraps by whole words and the hero grows to hug it (the name never shrinks to fit).</summary>
    public const int NameLines = 2;

    /// <summary>The gap between the caption and the secondary row (Huge).</summary>
    public const float ActionsTop = 14f;

    /// <summary>A hero action's gaps: between actions, and between a ring and its word.</summary>
    public const float ActionsGap = 22f, ActionLabelGap = 10f;

    /// <summary>The follow pill: its border, its horizontal padding and its height at the 36 ring (the band's own is 28).</summary>
    public const float PillBorder = 2f, PillPadX = 16f;

    /// <summary>The corner rings' bottom inset (Small; the right inset is the gutter).</summary>
    public const float CornerInsetY = 30f;

    /// <summary>The widest the hero's copy grows (the name bleeds or fits by the panorama option, never by this).</summary>
    public const float CopyMax = 9999f;

    /// <summary>The Compact actions row's height, its top and its gap.</summary>
    public const float BelowRowTop = 8f, BelowRowGap = 12f;

    // ── the body ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page's foot under the last section.</summary>
    public const float PageBottom = 32f;

    /// <summary>A heading row: the gap to its subtitle, and from the row to its body.</summary>
    public const float HeadInlineGap = 20f, HeadGap = 10f;

    /// <summary>The rail's gap between its items, and a rail item's heading to its row.</summary>
    public const float RailItemGap = 12f, RailHeadGap = 10f, RailRowGap = 14f;

    /// <summary>The chart's one-column floor under the rail (the chart takes the rest).</summary>
    public const float ChartMinW = 280f;

    /// <summary>The chart column's cap at Huge and Huge 4K.</summary>
    public const float HugeChartMaxW = 1200f;

    /// <summary>The standalone and inline grid's reveal inset (the drawer opens with its card this far under the page's top; the band is outside
    /// the page's viewport).</summary>
    public const float GridRevealInset = 24f;

    /// <summary>The facet heading pins nowhere (no pinned header in Zune): the pin the core is told.</summary>
    public const float PinTop = 0f;

    // ── the shelves ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Standard shelf card and the gap between cards (a tier's own card is <see cref="ZuneArtistLayout.ShelfCardW"/>).</summary>
    public const float ShelfCardW = 154f, ShelfGap = 16f;

    /// <summary>The shelf's edge fade.</summary>
    public const float ShelfEdgeFade = 36f;

    /// <summary>How far under the page viewport's top a pivot click parks a section and where the scroll spy's line starts: Zune's band sits OUTSIDE the
    /// viewport (it is the frame's), so only a little air keeps a heading off the band's hairline (Classic's sticky band is 56 inside it).</summary>
    public const float SectionAir = 16f;

    /// <summary>The merch card's gap and padding.</summary>
    public const float MerchGap = 8f, MerchPad = 8f;

    /// <summary>The concert stub: its height and its date column.</summary>
    public const float StubH = 64f, StubDateW = 48f;

    /// <summary>The gallery card's decode width.</summary>
    public const int GalleryDecode = 480;

    // ── about ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The biography text's size and line height, the text's share of the band beside the facts, and the gap between the columns.</summary>
    public const float BioSize = 14f, BioLine = 20f, BioShare = 0.62f, AboutGap = 48f, AboutInnerGap = 16f;
    /// <summary>The bio's lines before "read more" in the "about" section (the board draws a full paragraph).</summary>
    public const int BioLines = 4;

    /// <summary>A fact: its numeral's gap to its label, its basis and the gap between facts.</summary>
    public const float FactGap = 2f, FactBasis = 140f, FactsGap = 24f;

    /// <summary>One city's row, its bar and its gap.</summary>
    public const float CityRow = 34f, CityBar = 4f, CityGap = 4f;

    /// <summary>The smallest flex grow a city bar's empty or full half takes (a zero grow collapses the bar's parent).</summary>
    public const float MinGrow = 0.001f;

    // ── the banners ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The upcoming release's cover and its decode, the tour badge, its glyph and chevron.</summary>
    public const float UpcomingCover = 64f, TourBadge = 40f, TourGlyph = 17f, TourChevron = 16f, TourGap = 2f;

    /// <summary>The upcoming release's cover decode width.</summary>
    public const int UpcomingCoverDecode = 160;

    // ── the discography ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The discography grid's narrowest cell at Huge and Huge 4K (the boards' eight and eleven across), and Huge 4K's gap between cells.</summary>
    public const float HugeCell = 200f, Huge4KGap = 20f;

    /// <summary>The cell's chrome under its square cover (the shared surface's geometry), and the row gap folded into the grid.</summary>
    public const float CardChrome = 50f, RowGap = 20f;

    /// <summary>The card subtitle's disclosure cue and its gap after the meta.</summary>
    public const float SubtitleGap = 4f, SubtitleGlyph = 9f;

    /// <summary>The drawer panel's padding, the head's pieces (play ring, cover, open ring) and the caret.</summary>
    public const float PanelPadX = 12f, PanelPadY = 6f, PanelPadSum = 12f, HeadPlay = 32f, HeadCover = 28f;

    /// <summary>The head cover's decode width, the "show all" word's weight, and one half (a centre, an apex).</summary>
    public const int HeadCoverDecode = 56;

    /// <inheritdoc cref="HeadCoverDecode"/>
    public const ushort ShowAllWeight = 600;

    /// <inheritdoc cref="HeadCoverDecode"/>
    public const float Half = 0.5f;

    /// <summary>The allowance the verdict adds to the head row (the panel's padding).</summary>
    public const float DrawerHeadPad = 12f;

    /// <summary>The caret under the opened card.</summary>
    public const float CaretW = 16f, CaretH = 8f, CaretOverlap = 1f;

    /// <summary>The drawer rows: # 28 · heart · title ★ · time 48 · … 40; the air a row keeps in its pitch.</summary>
    public const float RankCol = 28f, TimeCol = 48f, MoreCol = 40f, RowAir = 4f;

    /// <summary>The shimmer rows' bars.</summary>
    public const float ShimmerRank = 16f, ShimmerTitleMax = 240f, ShimmerTime = 30f, ShimmerBarH = 11f, ShimmerTrail = 40f;

    /// <summary>The check lane's lead and the single-selection pill.</summary>
    public const float CheckLead = 4f, PillW = 3f, PillH = 16f, PillLead = 2f;

    /// <summary>The standalone page's head: the gap between the trail and the title, the tabs' gap, and the page's foot.</summary>
    public const float DiscoTrailGap = 6f, DiscoHeadGap = 14f, DiscoTabGap = 20f;

    /// <summary>A release-type word: its rest and selected weights, and the line under the current one.</summary>
    public const ushort TabRestWeight = 400, TabSelectedWeight = 600;

    /// <inheritdoc cref="TabRestWeight"/>
    public const float TabLine = 2f;

    /// <summary>The cells slide over when the check lane arrives (the track table's own tween).</summary>
    public static readonly LayoutTransition CheckShift = new(
        TransitionChannels.Position, TransitionDynamics.Tween(333f, Easing.FluentDecelerate));

    // ── the lightbox ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The floating chrome hides after this much pointer stillness; the pager comes back this long after an unzoom spring starts.</summary>
    public const float IdleHideMs = 2600f, UnzoomSettleMs = 260f;

    /// <summary>The zoom ceiling, a click's step, the wheel's gain per DIP, the floor under which a wheel zoom springs home, the settled scale.</summary>
    public const float ZoomMax = 4f, ZoomClick = 2.2f, ZoomWheelK = 0.0011f, ZoomFloor = 1.02f, ZoomRest = 1.001f;

    /// <summary>A drag past this many pixels is a pan, not a click.</summary>
    public const float DragSlop = 4f;

    /// <summary>The pan / zoom spring.</summary>
    public const float SpringResponse = 0.32f, SpringDamping = 0.9f;

    /// <summary>The filmstrip's thumb, its padding, its idle opacity, its border, its decode.</summary>
    public const float Thumb = 56f, StripPad = 14f, ThumbIdleOpacity = 0.55f, ThumbBorder = 2f;

    /// <summary>A thumb's decode width.</summary>
    public const int ThumbDecode = 128;

    /// <summary>The top chrome row, the gap in its title stack, and the photo's reveal fade.</summary>
    public const float ChromeH = 72f, ChromeTitleGap = 2f, PhotoFadeMs = 140f;

    /// <summary>The modal's ground, black at 88 %, and the top chrome's scrim.</summary>
    public static ColorF Scrim => ColorF.FromRgba(0, 0, 0, 224);

    /// <summary>A pager page's ground, opaque black: the FlipView under the photo paints the theme's solid base (white in light mode), so the chrome
    /// and a letterboxed photo sat on white there instead of the viewer's black.</summary>
    public static ColorF PageGround => ColorF.FromRgba(0, 0, 0);

    /// <summary>Fully transparent (the photo's placeholder, an idle thumb's border).</summary>
    public static ColorF Clear => ColorF.FromRgba(0, 0, 0, 0);

    /// <summary>The top chrome's scrim: black at 158 fading to nothing.</summary>
    public static readonly GradientSpec TopScrim = FluentGpu.Dsl.Ui.GradientDown(
        new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 158)),
        new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 0)));

    // ── skeleton ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shimmer's cover cells in a discography row.</summary>
    public const int ShimmerCells = 5;

    /// <summary>The wash's tail under the hero (the blend wash fades out over it).</summary>
    public const float WashTail = 96f;
}

/// <summary>Zune's artist tier records (content tier; ZT6 tunes Compact, Small, Huge and Huge 4K against their boards).</summary>
public static class ZuneArtistTiers
{
    private static ZuneArtistLayout Make(WidthTier tier, float zoneGap, float bodyTop, float heroH, float eyebrowTop, float captionBottom, bool compactName,
        float nameSize, float nameLine, float nameTracking, ZuneArtistActions actions, ZuneArtistSecondary secondary, float target, int chartCols,
        float chartMaxW, bool railBeside, float railW, float railGap, float latestArt, float pickArt, int drawerCols, float drawerPitch, float drawerHead,
        bool aboutBeside)
    {
        float gutter = ZuneTiers.Frame(tier).Gutter;
        bool huge = tier >= WidthTier.Huge, k4 = tier == WidthTier.Huge4K;
        return new()
        {
            // A bare link word is 40 at Compact; a shelf card grows at Huge (the grid's cell does) and is the board's 220 at 4K (as Home's); the lightbox's
            // strip and chrome follow the window.
            LinkTarget = tier == WidthTier.Compact ? 40f : 0f,
            ShelfCardW = k4 ? 220f : huge ? 180f : ZuneArtistRules.ShelfCardW,
            VideoCardW = k4 ? 340f : huge ? 280f : 240f,
            LightboxThumb = k4 ? 88f : huge ? 72f : tier == WidthTier.Compact ? 48f : ZuneArtistRules.Thumb,
            LightboxChromeH = k4 ? 104f : huge ? 88f : tier == WidthTier.Compact ? 64f : ZuneArtistRules.ChromeH,
            Tier = tier, Gutter = gutter, ZoneGap = zoneGap, BodyTop = bodyTop, HeroH = heroH, EyebrowTop = eyebrowTop,
            CaptionBottom = captionBottom, CompactName = compactName, NameSize = nameSize, NameLine = nameLine, NameTracking = nameTracking,
            Actions = actions, Secondary = secondary, Target = target, ChartColumns = chartCols, ChartRows = ArtistSections.ChartMaxRows,
            ChartMaxW = chartMaxW, RailBeside = railBeside, RailW = railW, RailGap = railGap, LatestArt = latestArt, PickArt = pickArt,
            DiscoMinColumn = ZuneArtistRules.ShelfCardW, DiscoGap = ZuneArtistRules.ShelfGap, DrawerColumns = drawerCols, DrawerPitch = drawerPitch,
            DrawerHead = drawerHead, AboutBeside = aboutBeside,
            // The system page column at every tier (audit A1: Standard's 1600 measure left 800 dead at 2560): 2400 plus the gutters.
            PageMax = ZuneFrameRules.PageCap + 2f * gutter,
        };
    }

    private static readonly ZuneArtistLayout[] s_records =
    [
        // Compact (Z-Artist-Compact): a 220 hero with the 64 compact name, a 40 actions row under it, one chart column, the rail under the chart,
        // the drawer one column of 40 rows.
        Make(WidthTier.Compact, zoneGap: 22f, bodyTop: 8f, heroH: 220f, eyebrowTop: 16f, captionBottom: 10f, compactName: true, nameSize: 0f,
            nameLine: 0f, nameTracking: 0f, ZuneArtistActions.BelowHero, ZuneArtistSecondary.None, target: 40f, chartCols: 1, chartMaxW: 0f,
            railBeside: false, railW: 0f, railGap: 22f, latestArt: 96f, pickArt: 64f, drawerCols: 1, drawerPitch: 40f, drawerHead: 48f, aboutBeside: false) with { ChartRowH = 56f, ChartArt = 44f, ChartLead = 40f, NameReserveLines = ZuneArtistRules.NameLines },
        // Small (Z-Artist-Small): a 140 hero with the compact name, play · shuffle · follow in the band, radio and "..." at the hero's bottom right,
        // one chart column beside a 220 rail.
        Make(WidthTier.Small, zoneGap: 28f, bodyTop: 12f, heroH: 140f, eyebrowTop: 12f, captionBottom: 8f, compactName: true, nameSize: 0f,
            nameLine: 0f, nameTracking: 0f, ZuneArtistActions.Band, ZuneArtistSecondary.Corner, target: 36f, chartCols: 1, chartMaxW: 0f,
            railBeside: true, railW: 220f, railGap: 32f, latestArt: 120f, pickArt: 48f, drawerCols: 2, drawerPitch: 40f, drawerHead: 48f, aboutBeside: false) with { RailStackGap = 16f, LatestStacked = true, ChartRowH = 52f, ChartArt = 40f, ChartLead = 44f },
        // Standard (Z-Artist): the tuned page. The actions are the band's row 2.
        Make(WidthTier.Standard, zoneGap: 28f, bodyTop: 24f, heroH: 340f, eyebrowTop: 36f, captionBottom: 16f, compactName: false, nameSize: 0f,
            nameLine: 0f, nameTracking: 0f, ZuneArtistActions.Band, ZuneArtistSecondary.None, target: 36f, chartCols: 2, chartMaxW: 0f,
            railBeside: true, railW: 300f, railGap: 48f, latestArt: 96f, pickArt: 64f, drawerCols: 0, drawerPitch: 40f, drawerHead: 48f, aboutBeside: true),
        // Huge (Z-Artist-Huge): a 400 hero with the 150 name, play · shuffle · follow in the band, radio and "..." in a row under the caption, the
        // chart capped at 1200 beside a 360 rail, the 2400 page column, the grid's cells 200 wide (the board's eight across at 2560).
        Make(WidthTier.Huge, zoneGap: 28f, bodyTop: 28f, heroH: 400f, eyebrowTop: 40f, captionBottom: 12f, compactName: false, nameSize: 150f,
            nameLine: 160f, nameTracking: -20f, ZuneArtistActions.Band, ZuneArtistSecondary.Row, target: 36f, chartCols: 2,
            chartMaxW: ZuneArtistRules.HugeChartMaxW, railBeside: true, railW: 360f, railGap: 48f, latestArt: 128f, pickArt: 72f, drawerCols: 4,
            drawerPitch: 40f, drawerHead: 48f, aboutBeside: true) with { DiscoMinColumn = ZuneArtistRules.HugeCell, RailStackGap = 26f, ChartRowH = 56f, ChartArt = 44f },
        // Huge 4K (Z-Artist-Huge4K) on the 2400 page column (decision 2: the board fills 2632, the page stops at 2400). KEPT from the board: the 520 hero
        // (the 150 name, a 56 eyebrow, a 24 foot), the 440 rail with its 64 gap, the 44 section gap, the 32 body top, the 88 pick art, the cells 200 wide with
        // a 20 gap (eleven across at 2400, the board's count), the 220 shelf cards. GIVEN UP to the cap: the board's 2632 column (the chart is the column
        // less the rail: two chart columns of about 920, the board's 1000, so it carries no extra cap), and its third body column ("about" stays the
        // pivot's own "details" section; the pivot's anchors are the core's plan). The latest release stays a 160 row in the rail (the board stacks a 240).
        Make(WidthTier.Huge4K, zoneGap: 44f, bodyTop: 32f, heroH: 520f, eyebrowTop: 56f, captionBottom: 24f, compactName: false, nameSize: 150f,
            nameLine: 160f, nameTracking: -20f, ZuneArtistActions.Band, ZuneArtistSecondary.Row, target: 36f, chartCols: 2,
            chartMaxW: 0f, railBeside: true, railW: 440f, railGap: 64f, latestArt: 160f, pickArt: 88f, drawerCols: 4,
            drawerPitch: 40f, drawerHead: 48f, aboutBeside: true) with { DiscoMinColumn = ZuneArtistRules.HugeCell, DiscoGap = ZuneArtistRules.Huge4KGap, RailStackGap = 32f, ChartRowH = 68f, ChartArt = 52f, ChartLead = 60f, ChartColumnGap = 64f },
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneArtistLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The Short rung (windows 330-699 tall, Z-Shell-Short: the band folds to 52 and the player bar holds 72, so about 160 of height is left to
    /// the page): the hero's SPACE tightens. It keeps its copy AT THE TIER'S TYPE (the eyebrow, the name at the tier's step, the caption, Huge's secondary
    /// row, Compact's actions row; Zune's type is never made smaller to fit) and hugs it, with no spring, so the hero is as tall as its name and the page
    /// scrolls; the body starts closer and the sections sit closer. The record is a pure fold of the content tier's.</summary>
    static ZuneArtistLayout Fold(ZuneArtistLayout r) => r with
    {
        HeroH = ShortHeroH, EyebrowTop = ShortEyebrowTop, CaptionBottom = ShortCaptionBottom,
        BodyTop = ShortBodyTop, ZoneGap = MathF.Min(r.ZoneGap, ShortZoneGap),
    };

    /// <summary>The folded hero's floor, its eyebrow's top and its caption's foot, the body's top and the sections' gap.</summary>
    public const float ShortHeroH = 132f, ShortEyebrowTop = 8f, ShortCaptionBottom = 8f, ShortBodyTop = 8f, ShortZoneGap = 20f;

    static readonly ZuneArtistLayout[] s_short = [Fold(s_records[0]), Fold(s_records[1]), Fold(s_records[2]), Fold(s_records[3]), Fold(s_records[4])];

    /// <summary>The record for <paramref name="facts"/>: the content tier's, folded on a Short window; Standard's where no frame resolved the tiers (a
    /// view mounted alone).</summary>
    /// <summary>The record for <paramref name="facts"/>: its content tier's, on the FRAME's gutter (the window tier's: the band, the title bar's wordmark and every page share
    /// one left edge, so a docked panel that narrows the content never moves it).</summary>
    public static ZuneArtistLayout Of(in TierFacts facts)
    {
        var record = OfContent(in facts);
        // The band's verbs come back to the hero where a Short window folds its row 2 at rest.
        if (record.Actions == ZuneArtistActions.Band && ZuneBandTiers.FoldsRow2AtRest(in facts)) record = record with { Actions = ZuneArtistActions.Hero };
        float gutter = ZuneTiers.GutterOf(in facts);
        return facts.Policy is null || record.Gutter == gutter ? record : record with { Gutter = gutter };
    }

    private static ZuneArtistLayout OfContent(in TierFacts facts)
    {
        if (facts.Policy is null) return For(WidthTier.Standard);
        return facts.Height == HeightTier.Short ? s_short[Math.Clamp((int)facts.Content, 0, s_short.Length - 1)] : For(facts.Content);
    }

    /// <summary>The record of the WINDOW's tier, for an overlay that covers the window (the lightbox): its sizes follow the window, not the page
    /// beside a docked panel. Standard's where no frame resolved the tiers.</summary>
    public static ZuneArtistLayout OfWindow(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The policy Zune publishes for the core's artist hosts.</summary>
    public static ArtistTierPolicy Policy { get; } = new ZuneArtistTierPolicy();
}

/// <summary>Zune's answers to the core's artist page questions (the records above).</summary>
public sealed class ZuneArtistTierPolicy : ArtistTierPolicy
{
    /// <inheritdoc />
    public override ArtistHeroMetrics Hero(ArtistHeroTier tier, in TierFacts facts, float width) => ZuneArtistTiers.Of(in facts).Metrics(tier);

    /// <inheritdoc />
    public override float BandClearance(in TierFacts facts) => ZuneArtistRules.SectionAir;

    /// <inheritdoc />
    public override int ChartMaxColumns(in TierFacts facts) => ZuneArtistTiers.Of(in facts).ChartColumns;

    /// <inheritdoc />
    public override ArtistChartRowShape ChartRowShape(in TierFacts facts)
    {
        var lay = ZuneArtistTiers.Of(in facts);
        return new ArtistChartRowShape(lay.ChartRowH, lay.ChartArt, lay.ChartLead, lay.ChartColumns > 1 ? lay.ChartColumnGap : 0f);
    }

    /// <inheritdoc />
    public override bool ChartBalanced(in TierFacts facts) => ZuneArtistTiers.Of(in facts).ChartColumns > 1;

    /// <inheritdoc />
    public override int ChartRows(in TierFacts facts) => ZuneArtistTiers.Of(in facts).ChartRows;

    /// <inheritdoc />
    public override float ProfileListMinCell(in TierFacts facts) => ZuneProfileTiers.Of(in facts).ListMinCell;
}
