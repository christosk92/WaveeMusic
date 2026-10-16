// ── Themes/Classic/Structure/Rules/Tiers.Artist.cs — Classic's artist ladders and the artist page's tier records (themes plan 3.2, 3.6; TC6) ─────
//
// THE SUB-LADDERS (moved out of core: `Entities\Artist.UI.cs` `ArtistHeroLayout.TierFor` and `ArtistSections.TopBandWide`, `Entities\Artist.Reader.Shape.cs`
// `ReaderShape.Narrow` / `Medium`), written with the engine's `TierLadder` so their holds behave like the window tiers' and a crossing found after mount
// commits on the first frame that is not resized and animates (plan 3.3). Classic's `TierPolicy` names the hero's and the reader's
// (`ClassicTiers.Policy.Ladders`); the core hosts resolve them through `UseTier` by id (`Wavee.Theming.ArtistLadders`) and never see a number.
//
//   hero      measured on the page's own width. Index = `ArtistHeroTier`: Narrow below 360, Compact from 360, Medium from 600, Wide from 880, each
//             edge held 24 BOTH ways (production's band: a Wide hero holds down to 856 and a Medium one re-enters Wide only at 904).
//   reader    measured on the library artist reader's width. 0 narrow below 640 (the 88 cover, the stacked album head), 1 medium below the wide
//             band's lane (748: no cover spine, the stacked band), 2 wide; each narrowed at once and re-admitted 24 past its edge.
//   top band  measured inside the Top-tracks band's `Responsive.Of`: the chart beside its featured card from 760, held down to 736 once wide.
//
// THE RECORDS (boards C-Artist, C-Artist-Compact, C-Artist-Small, C-Artist-Huge, C-Artist-Huge4K, C-Breakpoints' "Artist hero" row). One page record
// per WINDOW tier (`ClassicArtistLayout`) and one hero form per (window tier, hero sub-tier) (`ClassicArtistHeroForm`); the core reads the hero's metrics
// through `ClassicArtistTierPolicy` (`ThemePolicies.ArtistTiers`), Classic's views read the forms and the records:
//
//   tier       hero                                                                        chart        shelves          discography
//   Compact    the photograph full (300), the copy on it over a vertical veil, the actions    1 x 5        a 164 strip,     each facet a 164 strip (152
//              a 40 row below it (C-Artist-Compact); Narrow stacks them in two rows                         the 3rd peeks    cards on 12); the page's drawer
//                                                                                                                            1 column of 40 rows, 44 tabs
//   Small      Medium: the copy at the bottom-left (20 up), 560 wide, one bio line            2 x 5        fit 150-200      the 180 floor; drawer 2
//              (C-Artist-Small, capped at 288 by the Short window); Compact sub-tier as Compact                              columns (C-Discography-Small)
//   Standard   the tuned design (P3b): Wide 440 / Medium 384 copy centred, Compact and Narrow 2 x 5        fit 150-200      the 180 floor; the drawer's
//              the photograph field over the identity column                                                                 columns follow the grid
//   Huge       Wide: 560 (0.24 of the hero's width up to 640), the copy at the bottom-left    3 x 5 at      10 columns       the 180 floor; drawer 4
//              (40 up), 1120 wide, the bio at 760, a More button; the 56 section row under    3 : 1        (fit 264)        columns (C-Discography-Huge)
//              the hero (C-Artist-Huge)
//   Huge 4K    Huge's past the Huge 4K rung (window 3200, `ClassicDetailTiers.HugeWideRung`): the chart 4 x 4 at 4 : 1, the next concert under the pick
//              (C-Artist-Huge4K)
//   Short      under 680 tall a hero caps at 288 (60 % of the 480 page, C-Breakpoints) and its copy sits at the bottom; under 480 tall it is the 126
//              strip (60 % of the 210 page): the name and Play · Follow only.
//
// THE PAGE GUTTER is the one gutter (TC1's content ladder, `ClassicTiers.PageOf`): the hero's metrics carry it. ONE ALIGNMENT SOURCE: where the page is
// wider than its measure (1600; Huge's 2400) the magazine is a capped, centred column, and the hero's copy, the band, the section row and the magazine all
// inset by `ClassicArtistLayout.ColumnInset` (the gutter plus half of what the cap leaves), so the copy lines up with Top tracks at 4K. Huge is designed,
// not scaled: the magazine fills to 2400 and the chart takes three columns (four at Huge 4K).
//
// Public, so `Wavee.Tests` pins the ladders and the records (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>How a hero form lays the photograph, the veil and the copy out.</summary>
public enum ClassicArtistHeroArm : byte
{
    /// <summary>The tuned Standard arm: the photograph fills the hero, a horizontal veil, the copy (and its actions) centred on the left.</summary>
    Centred,
    /// <summary>The photograph fills the hero, a horizontal veil, the copy (and its actions) at the hero's bottom-left (C-Artist-Small, C-Artist-Huge).</summary>
    Bottom,
    /// <summary>The tuned stacked arm: the photograph is a field at the top and the identity column (with its actions) sits below it, on the page.</summary>
    Field,
    /// <summary>The photograph is a band of its own with the copy on it over a vertical veil, and the actions are a row under it (C-Artist-Compact).</summary>
    Overlay,
}

/// <summary>The type rung of the hero's name.</summary>
public enum ClassicArtistName : byte
{
    /// <summary>84 / 96 (<c>ArtistDisplay</c>).</summary>
    Display,
    /// <summary>48 / 60 (<c>ArtistTitle</c>).</summary>
    Title,
    /// <summary>32 / 40 (<c>ArtistCompactTitle</c>).</summary>
    Compact,
}

/// <summary>Which of the hero's actions show, and how.</summary>
public enum ClassicHeroActions : byte
{
    /// <summary>Play · Shuffle · Follow · Radio in one row.</summary>
    Row,
    /// <summary>[Play, Follow] over [Shuffle, Radio] (a hero too narrow for one row).</summary>
    TwoRows,
    /// <summary>Play · Follow only (the Short strip: the band beside it carries the same words).</summary>
    Primary,
}

/// <summary>What the artist hero lays out in one (window tier, hero sub-tier).</summary>
public sealed record ClassicArtistHeroForm
{
    /// <summary>The hero's sub-tier.</summary>
    public required ArtistHeroTier Tier { get; init; }

    /// <summary>How the photograph, the veil and the copy are laid out.</summary>
    public required ClassicArtistHeroArm Arm { get; init; }

    /// <summary>The whole pinned hero's height (the collapse runs from it to the 56 band).</summary>
    public required float Height { get; init; }

    /// <summary>The photograph's extent: the whole hero on the photo arms, the field or band on the stacked ones.</summary>
    public required float Photo { get; init; }

    /// <summary>The copy column's widest.</summary>
    public required float CopyMax { get; init; }

    /// <summary>The air under the copy on the Bottom and Overlay arms.</summary>
    public required float CopyBottom { get; init; }

    /// <summary>The gap between the copy's lines.</summary>
    public required float CopyGap { get; init; }

    /// <summary>The biography lead's lines (0 hides it).</summary>
    public required int BioLines { get; init; }

    /// <summary>The biography lead's widest (NaN: the copy column's).</summary>
    public required float BioMax { get; init; }

    /// <summary>The Verified caption and the meta line show (false: the Short strip's name alone).</summary>
    public required bool Details { get; init; }

    /// <summary>The name's type rung.</summary>
    public required ClassicArtistName Name { get; init; }

    /// <summary>Which actions show, and how.</summary>
    public required ClassicHeroActions Actions { get; init; }

    /// <summary>The actions' edge: 40 at Compact (the brief's Compact rule), the control's 32 elsewhere.</summary>
    public required float Target { get; init; }

    /// <summary>The action row ends in a More button (the artist's menu; C-Artist-Huge, C-Artist-Huge4K).</summary>
    public bool More { get; init; }

    /// <summary>The photograph sits apart from the copy (no type on it, the field arm) or the copy is a band of its own on it (the overlay arm): the
    /// veil is vertical or absent.</summary>
    public bool Stacked => Arm is ClassicArtistHeroArm.Field or ClassicArtistHeroArm.Overlay;

    /// <summary>The metrics the core latches for this form at the page gutter <paramref name="gutter"/>.</summary>
    public ArtistHeroMetrics Metrics(float gutter) => new(Tier, Height, gutter, Stacked ? ArtistHeroVeilAxis.Vertical : ArtistHeroVeilAxis.Horizontal, CopyMax)
    {
        PhotoHeight = Photo < Height ? Photo : 0f,
    };
}

/// <summary>What Classic's artist page lays out in one window tier.</summary>
public sealed record ClassicArtistLayout
{
    /// <summary>The window tier.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The most columns the Top-tracks chart takes (its 264 card floor and its row count still decide below that).</summary>
    public required int ChartColumns { get; init; }

    /// <summary>The rows of one chart column (five; Huge 4K's four: C-Artist-Huge4K's 4 x 4).</summary>
    public required int ChartRows { get; init; }

    /// <summary>The chart's share against the featured card beside it (2 : 1, Huge 3 : 1).</summary>
    public required float ChartShare { get; init; }

    /// <summary>The shelves' fit (appears on, videos, playlists, concerts, merch, gallery, "Fans also like").</summary>
    public required ClassicShelfBand Shelf { get; init; }

    /// <summary>The discography grid's column floor and gap.</summary>
    public required float DiscoMinColumn { get; init; }

    /// <summary>The discography grid's gap.</summary>
    public required float DiscoGap { get; init; }

    /// <summary>The page's hit targets: 40 at Compact, the control's 32 elsewhere (the band's pivot words are this tall too).</summary>
    public required float Target { get; init; }

    /// <summary>The 56 section row (the name, the pivot, Play · Follow as words) sits under the hero, in the flow (C-Artist-Huge).</summary>
    public bool SectionRow { get; init; }

    /// <summary>The albums, singles and compilations are each a strip at the shelves' fit (<see cref="Shelf"/>) instead of the inline grid with its
    /// drawer (C-Artist-Compact: 152 cards on 12, the third peeking).</summary>
    public bool DiscoStrip { get; init; }

    /// <summary>The album drawer's track columns (0: the grid's own rule, two from a five-column grid).</summary>
    public required int DrawerColumns { get; init; }

    /// <summary>The album drawer's row pitch (the 40 target at Compact, C-Discography-Compact; the tuned 32 elsewhere).</summary>
    public required float DrawerPitch { get; init; }

    /// <summary>The album drawer's head row (C-Discography-Compact's 48 with its 40 Play and open buttons; the tuned 36 elsewhere).</summary>
    public required float DrawerHead { get; init; }

    /// <summary>The standalone discography page's release-type tabs: C-Discography-Compact's 44 (NaN: the stock SelectorBar item).</summary>
    public float DiscoTab { get; init; } = float.NaN;

    /// <summary>The artist's next concert sits under the pick, beside the chart (C-Artist-Huge4K).</summary>
    public bool UpcomingBesidePick { get; init; }

    /// <summary>The widest the magazine grows: the page measure (<c>R.Space.PageMaxW</c>, 1600) up to Standard, Huge's own 2400 fill rule.</summary>
    public float PageMaxWidth => Tier >= WidthTier.Huge ? ClassicArtistTiers.HugePageMax : R.Space.PageMaxW;

    /// <summary>THE ONE ALIGNMENT SOURCE: the inset of the page's column at a <paramref name="width"/>-wide page, the page <paramref name="gutter"/> plus half
    /// of what the measure leaves either side. The hero's copy, the band, the section row and the magazine all inset by it, so they line up at every width.</summary>
    public float ColumnInset(float width, float gutter) => gutter + MathF.Max(0f, (width - PageMaxWidth) * 0.5f);

    /// <summary>The album drawer's geometry the core's verdict sizes (the head's allowance is the head row plus the panel's padding).</summary>
    public Album.DrawerShape Drawer => new(DrawerColumns, DrawerPitch, DrawerHead + ClassicArtistTiers.DrawerHeadPad);
}

/// <summary>Classic's artist ladders and the artist page's tier records.</summary>
public static class ClassicArtistTiers
{
    // ── the hero ladder ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hero sub-tiers' edges: Compact from 360, Medium from 600, Wide from 880.</summary>
    public const float HeroCompactFrom = 360f, HeroMediumFrom = 600f, HeroWideFrom = 880f;

    /// <summary>The hero edges' hold, both ways (production's band).</summary>
    public const float HeroHold = 24f;

    /// <summary>The hero sub-tier ladder (index = <see cref="ArtistHeroTier"/>).</summary>
    public static TierLadder Hero { get; } = TierLadder.Of(
        new TierEdge(HeroCompactFrom, NarrowHold: HeroHold, WidenHold: HeroHold),
        new TierEdge(HeroMediumFrom, NarrowHold: HeroHold, WidenHold: HeroHold),
        new TierEdge(HeroWideFrom, NarrowHold: HeroHold, WidenHold: HeroHold));

    // ── the reader ladder ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The reader's narrow edge: under it the block cover drops 120 → 88 and the album head stacks (the library's own 640; at 640 the inline
    /// head's title keeps <see cref="Artist.ReaderShape.TitleFloorW"/> with room to spare).</summary>
    public const float ReaderNarrowBelow = 640f;

    /// <summary>The reader's medium edge, DERIVED: the wide band's lane (<see cref="Artist.ReaderShape.WideBandLaneW"/>, 748).</summary>
    public const float ReaderMediumBelow = Artist.ReaderShape.WideBandLaneW;

    /// <summary>Both reader edges' hold on widening: an arm entered under its edge holds until the width clears it by this much.</summary>
    public const float ReaderHold = 24f;

    /// <summary>The reader's arm ladder (0 narrow, 1 medium, 2 wide).</summary>
    public static TierLadder Reader { get; } = TierLadder.Of(
        new TierEdge(ReaderNarrowBelow, NarrowHold: 0f, WidenHold: ReaderHold),
        new TierEdge(ReaderMediumBelow, NarrowHold: 0f, WidenHold: ReaderHold));

    // ── the Top-tracks band ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The chart sits beside its featured card from this band width, and holds down by <see cref="TopBandHold"/> once it does.</summary>
    public const float TopBandWideFrom = 760f, TopBandHold = 24f;

    /// <summary>The band's two arms (0 stacked, 1 beside).</summary>
    public static TierLadder TopBand { get; } = TierLadder.Of(new TierEdge(TopBandWideFrom, NarrowHold: TopBandHold, WidenHold: 0f));

    /// <summary>The band's latched arm at <paramref name="width"/>: beside from 760, held to 736 once beside.</summary>
    public static bool TopBandWide(float width, bool wasWide) => TopBand.Resolve(width, wasWide ? 1 : 0) == 1;

    /// <summary>The two ladders Classic's policy names.</summary>
    public static ImmutableArray<NamedLadder> Ladders { get; } = ImmutableArray.Create(
        new NamedLadder(ArtistLadders.Hero, Hero),
        new NamedLadder(ArtistLadders.Reader, Reader));

    /// <summary>The hero sub-tier of a <paramref name="width"/>-wide page, held against <paramref name="previous"/> (-1: a fresh resolve).</summary>
    public static ArtistHeroTier HeroTierFor(float width, int previous = -1) => ArtistLadders.HeroOf(Hero.Resolve(width, previous));

    // ── the hero forms ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The control's edge and the Compact one.</summary>
    public const float Target = 32f, CompactTarget = 40f;

    /// <summary>The overlay arm's photograph band and the step from it to the action row (C-Artist-Compact).</summary>
    public const float OverlayPhoto = 300f, OverlayActionsGap = 16f;

    /// <summary>The two-row action stack's row gap.</summary>
    public const float ActionRowGap = 8f;

    /// <summary>The Huge hero: its height's floor and ceiling and its share of the hero's width between them (C-Artist-Huge 560 at 1544, C-Artist-Huge4K
    /// 640 at 2664).</summary>
    public const float HugeHeroMin = 560f, HugeHeroMax = 640f, HugeHeroShare = 0.2403f;

    /// <summary>The cap a Short window (480-679 tall) puts on a hero: 60 % of its 480 page (C-Breakpoints: "Heroes cap at 60 % of the content height
    /// (480 → 288)").</summary>
    public const float ShortHeroCap = 288f;

    /// <summary>The Short strip's height under 480 tall: 60 % of the 210 page.</summary>
    public static float StripHeight => ClassicTiers.ShortFloorH * ClassicHomeTiers.ShortHeroFrac;

    /// <summary>The widest a Huge artist page's magazine grows (C-Breakpoints: "Huge lifts grids and shelves to 2400").</summary>
    public const float HugePageMax = 2400f;

    /// <summary>The section row under a Huge hero (C-Artist-Huge: 56 between its two hairlines; the band's own height).</summary>
    public const float SectionRowHeight = 56f;

    /// <summary>The overlay arm's wrapped meta: the gap between its lines and between its runs (C-Artist-Compact: <c>gap: 4px 16px</c>).</summary>
    public const float OverlayMetaRowGap = 4f, OverlayMetaColumnGap = 16f;

    /// <summary>The album drawer's head rows (the tuned 36, C-Discography-Compact's 48) and what the panel's padding adds to the head's allowance in the
    /// verdict (the core's 40 over the tuned 36).</summary>
    public const float DrawerHeadRow = 36f, DrawerHeadRowCompact = 48f, DrawerHeadPad = 4f;

    /// <summary>The album drawer's row pitches: the tuned 32 and the Compact 40 target.</summary>
    public const float DrawerPitch = 32f, DrawerPitchCompact = 40f;

    /// <summary>The standalone discography page's Compact tabs (C-Discography-Compact).</summary>
    public const float DiscoTabCompact = 44f;

    // The tuned Standard forms (every number is the one the core's hero held before tiers).
    private static readonly ClassicArtistHeroForm s_wide = new()
    {
        Tier = ArtistHeroTier.Wide, Arm = ClassicArtistHeroArm.Centred, Height = 440f, Photo = 440f, CopyMax = 1120f, CopyBottom = 0f, CopyGap = 8f,
        BioLines = 2, BioMax = float.NaN, Details = true, Name = ClassicArtistName.Display, Actions = ClassicHeroActions.Row, Target = Target,
    };

    private static readonly ClassicArtistHeroForm s_medium = s_wide with
    {
        Tier = ArtistHeroTier.Medium, Height = 384f, Photo = 384f, CopyMax = 760f, Name = ClassicArtistName.Title,
    };

    // The stacked identity band's WORST-CASE anatomy: verified caption + a 2-line title + a 2-line bio + meta (one row at Compact, three stacked rows at
    // Narrow) + gaps + 12 / 20 padding + actions (one 32 row at Compact, two at Narrow).
    private static readonly ClassicArtistHeroForm s_compact = s_wide with
    {
        Tier = ArtistHeroTier.Compact, Arm = ClassicArtistHeroArm.Field, Height = 200f + 248f, Photo = 200f, CopyMax = 640f, Name = ClassicArtistName.Compact,
    };

    private static readonly ClassicArtistHeroForm s_narrow = s_compact with
    {
        Tier = ArtistHeroTier.Narrow, Height = 176f + 292f, Photo = 176f, CopyMax = 520f, Actions = ClassicHeroActions.TwoRows,
    };

    // C-Artist-Compact: the photograph full, the copy on it 14 up, the actions a 40 row 16 under it; no biography lead on the board.
    private static readonly ClassicArtistHeroForm s_overlay = new()
    {
        Tier = ArtistHeroTier.Compact, Arm = ClassicArtistHeroArm.Overlay, Height = OverlayPhoto + OverlayActionsGap + CompactTarget, Photo = OverlayPhoto,
        CopyMax = 640f, CopyBottom = 14f, CopyGap = 4f, BioLines = 0, BioMax = float.NaN, Details = true, Name = ClassicArtistName.Compact,
        Actions = ClassicHeroActions.Row, Target = CompactTarget,
    };

    private static readonly ClassicArtistHeroForm s_overlayNarrow = s_overlay with
    {
        Tier = ArtistHeroTier.Narrow, Height = OverlayPhoto + OverlayActionsGap + 2f * CompactTarget + ActionRowGap, Actions = ClassicHeroActions.TwoRows,
    };

    // C-Artist-Small: the Medium hero with the copy at the bottom-left, 560 wide, one biography line.
    private static readonly ClassicArtistHeroForm s_smallMedium = s_medium with
    {
        Arm = ClassicArtistHeroArm.Bottom, CopyMax = 560f, CopyBottom = 20f, CopyGap = 6f, BioLines = 1,
    };

    private static readonly ClassicArtistHeroForm s_smallWide = s_wide with
    {
        Arm = ClassicArtistHeroArm.Bottom, CopyMax = 760f, CopyBottom = 28f,
    };

    // The Compact sub-tier inside a Small window: the overlay arm at the control's 32.
    private static readonly ClassicArtistHeroForm s_smallCompact = s_overlay with
    {
        Height = OverlayPhoto + OverlayActionsGap + Target, Target = Target,
    };

    // C-Artist-Huge (and -Huge4K): the copy 40 up, 1120 wide, the bio at 760, the lines 10 apart; the height follows the width between 560 and 640.
    private static readonly ClassicArtistHeroForm s_huge = s_wide with
    {
        Arm = ClassicArtistHeroArm.Bottom, Height = HugeHeroMin, Photo = HugeHeroMin, CopyBottom = 40f, CopyGap = 10f, BioMax = 760f, More = true,
    };

    /// <summary>The hero's form for sub-tier <paramref name="tier"/> of a <paramref name="width"/>-wide page in the tiers <paramref name="facts"/>; the Standard
    /// forms where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicArtistHeroForm HeroForm(ArtistHeroTier tier, in TierFacts facts, float width)
    {
        var window = facts.Policy is null ? WidthTier.Standard : (WidthTier)Math.Min((int)facts.Window, (int)WidthTier.Huge);
        var form = window switch
        {
            WidthTier.Compact => tier >= ArtistHeroTier.Compact ? s_overlay : s_overlayNarrow,
            WidthTier.Small => tier switch
            {
                ArtistHeroTier.Wide => s_smallWide,
                ArtistHeroTier.Medium => s_smallMedium,
                _ => s_smallCompact,
            },
            WidthTier.Huge when tier == ArtistHeroTier.Wide => HugeAt(width),
            _ => StandardForm(tier),
        };
        if (facts.Policy is null || facts.Height != HeightTier.Short) return form;
        bool strip = facts.Policy.IndexOfRung(ClassicTiers.HeroFullRung) >= 0 && !facts.Rung(ClassicTiers.HeroFullRung);
        return strip ? Strip(form, window) : Capped(form);
    }

    /// <summary>The tuned Standard form of a sub-tier.</summary>
    public static ClassicArtistHeroForm StandardForm(ArtistHeroTier tier) => tier switch
    {
        ArtistHeroTier.Wide => s_wide,
        ArtistHeroTier.Medium => s_medium,
        ArtistHeroTier.Compact => s_compact,
        _ => s_narrow,
    };

    /// <summary>The Huge hero at a <paramref name="width"/>-wide page: its share of the width between 560 and 640 (a fill rule inside the tier).</summary>
    private static ClassicArtistHeroForm HugeAt(float width)
    {
        float h = MathF.Round(Math.Clamp(width * HugeHeroShare, HugeHeroMin, HugeHeroMax));
        return h == s_huge.Height ? s_huge : s_huge with { Height = h, Photo = h };
    }

    /// <summary>A form under the Short cap (480-679 tall): a photo arm caps its height and sets its copy at the bottom, a field becomes the overlay band.</summary>
    private static ClassicArtistHeroForm Capped(ClassicArtistHeroForm f)
    {
        if (f.Height <= ShortHeroCap) return f;
        if (!f.Stacked)
            return f with
            {
                Arm = ClassicArtistHeroArm.Bottom, Height = ShortHeroCap, Photo = ShortHeroCap, CopyBottom = 20f, CopyGap = 6f, BioLines = 1,
                Name = f.Name == ClassicArtistName.Display ? ClassicArtistName.Title : f.Name,
            };
        float actions = f.Actions == ClassicHeroActions.TwoRows ? 2f * f.Target + ActionRowGap : f.Target;
        return f with
        {
            Arm = ClassicArtistHeroArm.Overlay, Height = ShortHeroCap, Photo = ShortHeroCap - OverlayActionsGap - actions,
            CopyBottom = 12f, CopyGap = 4f, BioLines = 0,
        };
    }

    /// <summary>The Short strip under 480 tall: 126 tall, the name and Play · Follow on the photograph.</summary>
    private static ClassicArtistHeroForm Strip(ClassicArtistHeroForm f, WidthTier window) => f with
    {
        Arm = ClassicArtistHeroArm.Bottom, Height = StripHeight, Photo = StripHeight, CopyBottom = 12f, CopyGap = 8f, BioLines = 0, Details = false,
        Name = ClassicArtistName.Compact, Actions = ClassicHeroActions.Primary, Target = window == WidthTier.Compact ? CompactTarget : Target,
    };

    /// <summary>The hero's metrics for sub-tier <paramref name="tier"/>: the form's, at the page gutter of the content tier on screen.</summary>
    public static ArtistHeroMetrics HeroMetrics(ArtistHeroTier tier, in TierFacts facts, float width)
        => HeroForm(tier, in facts, width).Metrics(ClassicTiers.PageOf(in facts).Gutter);

    /// <summary>The form the view lays out for <paramref name="m"/> (the metrics the core latched) in the tiers on screen.</summary>
    public static ClassicArtistHeroForm FormOf(in ArtistHeroMetrics m, in TierFacts facts, float width) => HeroForm(m.Tier, in facts, width);

    /// <summary>The hero's height change on a crossing tweens (production's detail hero recipe: 280 ms SmoothOut, the size revealed, never scaled).</summary>
    public static readonly LayoutTransition HeroMotion = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(280f, Easing.SmoothOut), SizeMode.Reveal);

    /// <summary>What the hero's height change moves (the magazine under it) glides on the same tween.</summary>
    public static readonly LayoutTransition FollowMotion = new(TransitionChannels.Position, TransitionDynamics.Tween(280f, Easing.SmoothOut));

    /// <summary>An arm swap cross-fades (the shared tier cross-fade, ~160 ms).</summary>
    public static readonly EnterExit ArmFade = new(Opacity: 0f, Active: true);

    /// <summary>The arm cross-fade's timing.</summary>
    public static MotionTokenDef ArmFadeMotion => MotionTok.ControlFast;

    // ── the page records (per window tier) ──────────────────────────────────────────────────────────────────────────

    /// <summary>The shelves' standard fit: cards 150-200, gap 12, the shelf's pager.</summary>
    private static readonly ClassicShelfBand s_shelf = new(150f, 200f, 0, FixedCardW: 0f, Gap: 12f, Pager: true);

    /// <summary>Compact (C-Artist-Compact): one chart column, a 164 strip whose third card peeks (the shelves and each discography facet: 152 cards on 12),
    /// the discography page's two-column grid with its one-column drawer of 40 rows and its 44 tabs (C-Discography-Compact), 40 targets.</summary>
    public static ClassicArtistLayout Compact { get; } = new()
    {
        Tier = WidthTier.Compact, ChartColumns = 1, ChartRows = ArtistSections.ChartMaxRows, ChartShare = 2f,
        Shelf = new ClassicShelfBand(164f, 164f, 0, FixedCardW: 164f, Gap: 0f, Pager: false),
        DiscoMinColumn = 128f, DiscoGap = 12f, Target = CompactTarget, DiscoStrip = true,
        DrawerColumns = 1, DrawerPitch = DrawerPitchCompact, DrawerHead = DrawerHeadRowCompact, DiscoTab = DiscoTabCompact,
    };

    /// <summary>Small (C-Artist-Small; C-Discography-Small's two-column drawer).</summary>
    public static ClassicArtistLayout Small { get; } = new()
    {
        Tier = WidthTier.Small, ChartColumns = 2, ChartRows = ArtistSections.ChartMaxRows, ChartShare = 2f, Shelf = s_shelf,
        DiscoMinColumn = ClassicDiscographyRules.MinColumn, DiscoGap = ClassicDiscographyRules.Gap, Target = Target,
        DrawerColumns = 2, DrawerPitch = DrawerPitch, DrawerHead = DrawerHeadRow,
    };

    /// <summary>Standard: the tuned design (the drawer's columns follow the grid, two from five columns).</summary>
    public static ClassicArtistLayout Standard { get; } = Small with { Tier = WidthTier.Standard, DrawerColumns = 0 };

    /// <summary>Huge (C-Artist-Huge, C-Discography-Huge): three chart columns of five at 3 : 1 beside the pick, the 56 section row under the hero, ten-card
    /// shelves, the four-column drawer, the 2400 fill.</summary>
    public static ClassicArtistLayout Huge { get; } = Small with
    {
        Tier = WidthTier.Huge, ChartColumns = 3, ChartShare = 3f, Shelf = new ClassicShelfBand(150f, 9999f, 10, FixedCardW: 0f, Gap: 12f, Pager: true),
        SectionRow = true, DrawerColumns = 4,
    };

    /// <summary>Huge 4K (C-Artist-Huge4K), past the Huge 4K rung: four chart columns of four at 4 : 1, the artist's next concert under the pick.</summary>
    public static ClassicArtistLayout Huge4K { get; } = Huge with
    {
        ChartColumns = 4, ChartRows = 4, ChartShare = 4f, UpcomingBesidePick = true,
    };

    /// <summary>The record of window tier <paramref name="tier"/> (Huge's at every Huge width; <see cref="Of"/> reads Huge 4K past its rung).</summary>
    public static ClassicArtistLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Standard => Standard,
        _ => Huge,
    };

    /// <summary>The record of <paramref name="facts"/> (Huge 4K past the Huge 4K rung, the detail pages' `ClassicDetailTiers.HugeWideRung`); Standard's where
    /// no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicArtistLayout Of(in TierFacts facts)
        => facts.Policy is null ? Standard : ClassicDetailTiers.Policy.IsHugeWide(in facts) ? Huge4K : For(facts.Window);

    /// <summary>The record on screen now, read TRACKED (the running render re-runs on a crossing): for the templates the core's components call
    /// (the discography grid's metrics), which carry no view context.</summary>
    public static ClassicArtistLayout Now => Of(ThemeHost.Facts.Value.Tiers);

    /// <summary>The policy the structure publishes for the core hosts.</summary>
    public static ArtistTierPolicy Policy { get; } = new ClassicArtistTierPolicy();
}

/// <summary>Classic's answers to the core's artist and people page questions (the records above and <see cref="ClassicProfileTiers"/>).</summary>
public sealed class ClassicArtistTierPolicy : ArtistTierPolicy
{
    /// <inheritdoc/>
    public override ArtistHeroMetrics Hero(ArtistHeroTier tier, in TierFacts facts, float width) => ClassicArtistTiers.HeroMetrics(tier, in facts, width);

    /// <inheritdoc/>
    public override ProfileHeroMetrics ProfileHero(ArtistHeroTier tier, in TierFacts facts, float width) => ClassicProfileTiers.HeroMetrics(tier, in facts, width);

    /// <inheritdoc/>
    public override int ChartMaxColumns(in TierFacts facts) => ClassicArtistTiers.Of(in facts).ChartColumns;

    /// <inheritdoc/>
    public override int ChartRows(in TierFacts facts) => ClassicArtistTiers.Of(in facts).ChartRows;

    /// <inheritdoc/>
    public override float ProfileListMinCell(in TierFacts facts) => ClassicProfileTiers.Of(in facts).ListMinCell;
}
