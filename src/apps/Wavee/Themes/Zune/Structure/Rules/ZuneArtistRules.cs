// ── Themes/Zune/Structure/Rules/ZuneArtistRules.cs — the numbers and tier records of Zune's artist family (themes plan 6.3; P5 Z6a2) ─────
//
// Boards Z-Artist (Standard, the target), Z-Artist-Compact / -Small / -Huge / -Huge4K for the other tiers (ZT6 tunes them). WVTH003 allows
// literals only here, in `Rules\**`; WVTH013 keeps every width decision in the tier record (`ZuneArtistTiers`, by CONTENT tier).
//
//   THE HERO (Z-Artist): no photograph of its own (the artist's header is the material's BLEED, published to the band through the page chrome)
//   and no veil: the cover's blend wash tints it. 340 tall at Standard, the "verified artist" eyebrow 36 down, the name a PANORAMA word (120/130
//   at the light 200; 64 compact below 640; 150 at Huge) standing 8 left of the gutter so its glyph edge lines up, the caption under it
//   ("#3 in the world" in the accent · monthly listeners · followers) 16 up from the foot. THE ACTIONS follow the board: Standard leaves them to
//   the band's row 2; Compact puts a 40 row under the hero, Small stands them at the hero's bottom right, Huge in a row under the caption.
//   THE BODY: 24 under the hero, Popular (the chart, its columns by tier) beside a rail (300; Huge 360) holding the upcoming release, the
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

/// <summary>Where the hero's actions (play · shuffle · follow · radio · more) stand in a tier.</summary>
public enum ZuneArtistActions : byte
{
    /// <summary>The band's row 2 carries them (Standard).</summary>
    Band,
    /// <summary>A row under the hero's caption (Huge).</summary>
    HeroRow,
    /// <summary>At the hero's bottom right (Small).</summary>
    HeroCorner,
    /// <summary>A 40 row under the hero (Compact).</summary>
    BelowHero,
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

    /// <summary>Where the actions stand.</summary>
    public required ZuneArtistActions Actions { get; init; }

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
    /// discography link.</summary>
    public const string PagePrefix = "zune-artist-page:", ScrollPrefix = "zune-artist-scroll:", BodyPrefix = "zune-artist-body:",
        HeroKey = "zune-artist-hero", NameKey = "zune-artist-name", EyebrowKey = "zune-artist-eyebrow", CaptionKey = "zune-artist-caption",
        ActionsKey = "zune-artist-actions", PopularKey = "zune-artist-popular", RailKey = "zune-artist-rail",
        SectionPrefix = "zune-artist-sec:", AnchorPrefix = "zune-artist-anchor:", FacetHeadPrefix = "zune-artist-facet-head:",
        DiscographyLinkPrefix = "zune-artist-disco-link:", AboutKey = "zune-artist-about", WashKey = "zune-artist-wash:",
        LightboxKey = "zune-artist-lightbox", DiscoPageKey = "zune-discography-page", DiscoHeadKey = "zune-discography-head";

    // ── the hero ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The name stands this far left of the gutter, so the glyph edge of the light panorama lines up with the eyebrow.</summary>
    public const float NameNudge = 8f;

    /// <summary>The gap between the name and the caption under it.</summary>
    public const float NameCaptionGap = 14f;

    /// <summary>The gap between the caption and the actions row (Huge).</summary>
    public const float ActionsTop = 14f;

    /// <summary>A hero action's gaps: between actions, and between a ring and its word.</summary>
    public const float ActionsGap = 22f, ActionLabelGap = 10f;

    /// <summary>The follow pill: its border, its horizontal padding and its height at the 36 ring (the band's own is 28).</summary>
    public const float PillBorder = 2f, PillPadX = 16f;

    /// <summary>The corner actions' bottom and right inset (Small).</summary>
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

    /// <summary>The widest the Huge page grows (decision 2: pages cap at 2400, 4K included).</summary>
    public const float HugePageMax = 2400f;

    /// <summary>The standalone and inline grid's reveal inset (the drawer opens with its card this far under the page's top; the band is outside
    /// the page's viewport).</summary>
    public const float GridRevealInset = 24f;

    /// <summary>The facet heading pins nowhere (no pinned header in Zune): the pin the core is told.</summary>
    public const float PinTop = 0f;

    // ── the shelves ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A shelf's card and the gap between cards; a music-video card's width.</summary>
    public const float ShelfCardW = 154f, ShelfGap = 16f, VideoCardW = 240f;

    /// <summary>A music-video card's width is the shelf's; the shelf's edge fade.</summary>
    public const float ShelfEdgeFade = 36f;

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
        float nameSize, float nameLine, float nameTracking, ZuneArtistActions actions, float target, int chartCols, bool railBeside, float railW,
        float railGap, float latestArt, float pickArt, int drawerCols, float drawerPitch, float drawerHead, bool aboutBeside, float pageMax) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, ZoneGap = zoneGap, BodyTop = bodyTop, HeroH = heroH, EyebrowTop = eyebrowTop,
        CaptionBottom = captionBottom, CompactName = compactName, NameSize = nameSize, NameLine = nameLine, NameTracking = nameTracking,
        Actions = actions, Target = target, ChartColumns = chartCols, ChartRows = ArtistSections.ChartMaxRows, RailBeside = railBeside,
        RailW = railW, RailGap = railGap, LatestArt = latestArt, PickArt = pickArt, DiscoMinColumn = ZuneArtistRules.ShelfCardW,
        DiscoGap = ZuneArtistRules.ShelfGap, DrawerColumns = drawerCols, DrawerPitch = drawerPitch, DrawerHead = drawerHead,
        AboutBeside = aboutBeside, PageMax = pageMax,
    };

    private static readonly ZuneArtistLayout[] s_records =
    [
        // Compact (Z-Artist-Compact): a 220 hero with the 64 compact name, a 40 actions row under it, one chart column, the rail under the chart,
        // the drawer one column of 40 rows.
        Make(WidthTier.Compact, zoneGap: 22f, bodyTop: 8f, heroH: 220f, eyebrowTop: 16f, captionBottom: 10f, compactName: true, nameSize: 0f,
            nameLine: 0f, nameTracking: 0f, ZuneArtistActions.BelowHero, target: 40f, chartCols: 1, railBeside: false, railW: 0f, railGap: 22f,
            latestArt: 96f, pickArt: 64f, drawerCols: 1, drawerPitch: 40f, drawerHead: 48f, aboutBeside: false, pageMax: 9999f),
        // Small (Z-Artist-Small): a 140 hero with the compact name and the actions at its bottom right, one chart column beside a 220 rail.
        Make(WidthTier.Small, zoneGap: 28f, bodyTop: 12f, heroH: 140f, eyebrowTop: 12f, captionBottom: 8f, compactName: true, nameSize: 0f,
            nameLine: 0f, nameTracking: 0f, ZuneArtistActions.HeroCorner, target: 36f, chartCols: 1, railBeside: true, railW: 220f, railGap: 32f,
            latestArt: 120f, pickArt: 48f, drawerCols: 2, drawerPitch: 40f, drawerHead: 48f, aboutBeside: false, pageMax: 9999f),
        // Standard (Z-Artist): the tuned page. The actions are the band's row 2.
        Make(WidthTier.Standard, zoneGap: 28f, bodyTop: 24f, heroH: 340f, eyebrowTop: 36f, captionBottom: 16f, compactName: false, nameSize: 0f,
            nameLine: 0f, nameTracking: 0f, ZuneArtistActions.Band, target: 36f, chartCols: 2, railBeside: true, railW: 300f, railGap: 48f,
            latestArt: 96f, pickArt: 64f, drawerCols: 0, drawerPitch: 40f, drawerHead: 48f, aboutBeside: true, pageMax: 1600f),
        // Huge (Z-Artist-Huge): a 400 hero with the 150 name and the actions in a row under the caption, a 360 rail, the 2400 fill.
        Make(WidthTier.Huge, zoneGap: 28f, bodyTop: 28f, heroH: 400f, eyebrowTop: 40f, captionBottom: 12f, compactName: false, nameSize: 150f,
            nameLine: 160f, nameTracking: -20f, ZuneArtistActions.HeroRow, target: 36f, chartCols: 2, railBeside: true, railW: 360f, railGap: 48f,
            latestArt: 128f, pickArt: 72f, drawerCols: 4, drawerPitch: 40f, drawerHead: 48f, aboutBeside: true, pageMax: ZuneArtistRules.HugePageMax),
        // Huge 4K: Huge's (pages cap at 2400, decision 2); ZT6 tunes the board's 520 hero and the 440 rail against it.
        Make(WidthTier.Huge4K, zoneGap: 28f, bodyTop: 28f, heroH: 400f, eyebrowTop: 40f, captionBottom: 12f, compactName: false, nameSize: 150f,
            nameLine: 160f, nameTracking: -20f, ZuneArtistActions.HeroRow, target: 36f, chartCols: 2, railBeside: true, railW: 360f, railGap: 48f,
            latestArt: 128f, pickArt: 72f, drawerCols: 4, drawerPitch: 40f, drawerHead: 48f, aboutBeside: true, pageMax: ZuneArtistRules.HugePageMax),
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneArtistLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneArtistLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);

    /// <summary>The policy Zune publishes for the core's artist hosts.</summary>
    public static ArtistTierPolicy Policy { get; } = new ZuneArtistTierPolicy();
}

/// <summary>Zune's answers to the core's artist page questions (the records above).</summary>
public sealed class ZuneArtistTierPolicy : ArtistTierPolicy
{
    /// <inheritdoc />
    public override ArtistHeroMetrics Hero(ArtistHeroTier tier, in TierFacts facts, float width) => ZuneArtistTiers.Of(in facts).Metrics(tier);

    /// <inheritdoc />
    public override int ChartMaxColumns(in TierFacts facts) => ZuneArtistTiers.Of(in facts).ChartColumns;

    /// <inheritdoc />
    public override int ChartRows(in TierFacts facts) => ZuneArtistTiers.Of(in facts).ChartRows;
}
