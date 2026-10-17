// ── Wavee.Tests/Theming/ZuneArtistRulesTests.cs — Zune's artist, discography, lightbox, profile and concert tier rules (P5 ZT6) ──────────────
//
// The pure rules of `Rules\ZuneArtistRules.cs`, `Rules\ZuneProfileRules.cs` and `Rules\ZuneShowRules.cs` (the concert records) against the boards (Z-Artist and
// its four tiers, Z-Profile and its three, Z-Concert and its three), the plan (decision 2: pages cap at 2400) and the brief (Compact targets are 40, the Short
// rung gives height back, a popup never outgrows a 300 window):
//
//   · each family's record per content tier, Huge 4K reconciled with the 2400 cap (what the board keeps and what the cap gives up);
//   · the Short rung (windows 330-699 tall) is a pure fold of the content tier's record and leaves the Regular answers alone;
//   · the band clearance the artist and profile pages read (Zune 16 outside the viewport, Classic and the shell before tiers 56);
//   · the popups' fit: a flyout is its natural width or the window less its margins.

using FluentGpu.Foundation;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZuneArtistRulesTests
{
    static readonly WidthTier[] s_tiers = [WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K];

    /// <summary>The facts of a Zune window <paramref name="width"/> x <paramref name="height"/> (the content is the window: nothing docked).</summary>
    static TierFacts Window(float width, float height) => WindowTiers.Resolve(ZuneTiers.Policy, width, height, width);

    // ── the artist: the records ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_tier_stands_on_the_frames_gutter_and_the_system_column()
    {
        foreach (var tier in s_tiers)
        {
            var lay = ZuneArtistTiers.For(tier);
            Assert.Equal(tier, lay.Tier);
            Assert.Equal(ZuneTiers.Frame(tier).Gutter, lay.Gutter);
            Assert.Equal(ZuneFrameRules.PageCap + 2f * lay.Gutter, lay.PageMax);
        }
    }

    [Fact]
    public void Huge_4K_keeps_the_boards_hero_rail_and_gaps_on_the_capped_column()
    {
        var huge = ZuneArtistTiers.For(WidthTier.Huge);
        var k = ZuneArtistTiers.For(WidthTier.Huge4K);
        // Z-Artist-Huge4K: the 520 hero (the 150 name, a 56 eyebrow, a 24 foot), the 440 rail 64 from the chart, 44 between sections, the 32 body top.
        Assert.Equal(520f, k.HeroH);
        Assert.Equal(56f, k.EyebrowTop);
        Assert.Equal(24f, k.CaptionBottom);
        Assert.Equal(150f, k.NameSize);
        Assert.Equal(440f, k.RailW);
        Assert.Equal(64f, k.RailGap);
        Assert.Equal(44f, k.ZoneGap);
        Assert.Equal(32f, k.BodyTop);
        Assert.Equal(88f, k.PickArt);
        // The page stops at the cap, the chart is the column less the rail (the board's 2632 less 440 is the cap's 2400 less 440: no extra cap), the
        // cells are 200 wide with a 20 gap (eleven across 2400, the board's count), the shelf cards the board's 220 (Home's).
        Assert.Equal(ZuneFrameRules.PageCap + 2f * k.Gutter, k.PageMax);
        Assert.Equal(0f, k.ChartMaxW);
        Assert.Equal(200f, k.DiscoMinColumn);
        Assert.Equal(20f, k.DiscoGap);
        Assert.Equal(11, (int)((ZuneFrameRules.PageCap + k.DiscoGap) / (k.DiscoMinColumn + k.DiscoGap)));
        Assert.Equal(220f, k.ShelfCardW);
        // Huge keeps the audited 1200 chart cap and the 360 rail.
        Assert.Equal(1200f, huge.ChartMaxW);
        Assert.Equal(360f, huge.RailW);
        Assert.Equal(400f, huge.HeroH);
        Assert.Equal(200f, huge.DiscoMinColumn);
        Assert.Equal(16f, huge.DiscoGap);
        Assert.Equal(180f, huge.ShelfCardW);
    }

    [Fact]
    public void A_bare_link_word_is_forty_at_compact_only_and_every_target_edge_is_at_least_forty_there()
    {
        foreach (var tier in s_tiers)
            Assert.Equal(tier == WidthTier.Compact ? 40f : 0f, ZuneArtistTiers.For(tier).LinkTarget);
        var c = ZuneArtistTiers.For(WidthTier.Compact);
        Assert.Equal(40f, c.Target);
        Assert.True(c.LightboxThumb >= 40f, "the filmstrip's thumb is a target");
        Assert.True(c.LightboxChromeH >= c.Target, "the lightbox's top row holds a 40 ring");
    }

    [Fact]
    public void The_lightbox_scales_with_the_window_and_the_shelf_cards_with_the_content()
    {
        float thumb = 0f, chrome = 0f, card = 0f;
        foreach (var tier in s_tiers)
        {
            var lay = ZuneArtistTiers.For(tier);
            Assert.True(lay.LightboxThumb >= thumb && lay.LightboxChromeH >= chrome && lay.ShelfCardW >= card, $"{tier}: the sizes never shrink as the window grows");
            Assert.True(lay.VideoCardW >= lay.ShelfCardW, $"{tier}: a video card is at least a cover");
            (thumb, chrome, card) = (lay.LightboxThumb, lay.LightboxChromeH, lay.ShelfCardW);
        }
        // The window's tier, not the content's: a docked panel narrowing the page leaves the overlay's sizes alone.
        var facts = new TierFacts(WidthTier.Huge, HeightTier.Regular, WidthTier.Small, 800f, 2560f, 1440f, false, 0UL, ZuneTiers.Policy);
        Assert.Same(ZuneArtistTiers.For(WidthTier.Huge), ZuneArtistTiers.OfWindow(in facts));
        Assert.Same(ZuneArtistTiers.For(WidthTier.Small), ZuneArtistTiers.Of(in facts));
        Assert.Same(ZuneArtistTiers.For(WidthTier.Standard), ZuneArtistTiers.OfWindow(default));
    }

    // ── the Short rung ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(300f)]
    [InlineData(800f)]
    [InlineData(1440f)]
    [InlineData(2560f)]
    [InlineData(3840f)]
    public void A_short_window_folds_the_artist_hero_and_keeps_its_copy(float width)
    {
        var regular = ZuneArtistTiers.Of(Window(width, 960f));
        var shortLay = ZuneArtistTiers.Of(Window(width, 330f));
        Assert.Equal(regular.Tier, shortLay.Tier);
        Assert.True(shortLay.HeroH <= regular.HeroH, "the hero gives height back");
        Assert.Equal(ZuneArtistTiers.ShortHeroH, shortLay.HeroH);
        Assert.True(shortLay.CompactName, "the 64 voice");
        Assert.Equal(0f, shortLay.NameSize);
        Assert.True(shortLay.BodyTop <= regular.BodyTop && shortLay.ZoneGap <= regular.ZoneGap);
        // What is not height is the tier's own: the actions, the columns, the rail, the page.
        Assert.Equal(regular.Actions, shortLay.Actions);
        Assert.Equal(regular.Secondary, shortLay.Secondary);
        Assert.Equal(regular.ChartColumns, shortLay.ChartColumns);
        Assert.Equal(regular.RailW, shortLay.RailW);
        Assert.Equal(regular.PageMax, shortLay.PageMax);
        Assert.Equal(regular.Target, shortLay.Target);
    }

    [Fact]
    public void A_regular_window_and_a_view_with_no_frame_read_the_content_tiers_record()
    {
        Assert.Same(ZuneArtistTiers.For(WidthTier.Standard), ZuneArtistTiers.Of(Window(1440f, 960f)));
        Assert.Same(ZuneArtistTiers.For(WidthTier.Standard), ZuneArtistTiers.Of(default));
        Assert.Same(ZuneArtistTiers.For(WidthTier.Huge4K), ZuneArtistTiers.Of(Window(3840f, 2160f)));
        // The 700 line: 699 folds, 700 does not.
        Assert.Equal(ZuneArtistTiers.ShortHeroH, ZuneArtistTiers.Of(Window(1440f, 699f)).HeroH);
        Assert.Same(ZuneArtistTiers.For(WidthTier.Standard), ZuneArtistTiers.Of(Window(1440f, 700f)));
    }

    // ── the band clearance ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pivot_click_clears_zunes_band_by_its_air_and_classics_by_the_sticky_56()
    {
        var facts = Window(1440f, 960f);
        Assert.Equal(ZuneArtistRules.SectionAir, ZuneArtistTiers.Policy.BandClearance(in facts));
        Assert.True(ZuneArtistRules.SectionAir < Detail.BandLayout.Height, "the band is outside the viewport: far less than Classic's 56");
        // The shell before tiers and Classic keep the sticky band's height: nothing changed for them.
        Assert.Equal(56f, Detail.BandLayout.Height);
        Assert.Equal(Detail.BandLayout.Height, ArtistTierPolicy.Default.BandClearance(default));
        Assert.Equal(Detail.BandLayout.Height, Wavee.Themes.Classic.ClassicArtistTiers.Policy.BandClearance(default));
    }

    // ── the people lists ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_policy_gives_the_people_list_the_profile_records_narrowest_card()
    {
        foreach (var width in new[] { 300f, 800f, 1440f, 2560f, 3840f })
        {
            var facts = Window(width, 960f);
            Assert.Equal(ZuneProfileTiers.Of(in facts).ListMinCell, ZuneArtistTiers.Policy.ProfileListMinCell(in facts));
        }
        // Two cards share a 300 window's 268 between the gutters (gap 16); the wide tiers grow with the shelf cards.
        Assert.True(2f * ZuneProfileTiers.For(WidthTier.Compact).ListMinCell + 16f <= 268f);
        Assert.Equal(220f, ZuneProfileTiers.For(WidthTier.Huge4K).ListMinCell);
    }

    // ── the profile ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_profile_cards_and_links_follow_the_tier()
    {
        foreach (var tier in s_tiers)
        {
            var rec = ZuneProfileTiers.For(tier);
            Assert.Equal(tier == WidthTier.Compact ? 40f : 0f, rec.LinkTarget);
            Assert.True(rec.CardW >= ZuneProfileRules.CardW);
        }
        Assert.Equal(200f, ZuneProfileTiers.For(WidthTier.Huge).CardW);
        Assert.Equal(220f, ZuneProfileTiers.For(WidthTier.Huge4K).CardW);
        Assert.Equal(154f, ZuneProfileTiers.For(WidthTier.Standard).CardW);
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(800f)]
    [InlineData(1440f)]
    [InlineData(3840f)]
    public void A_short_window_folds_the_profile_hero(float width)
    {
        var regular = ZuneProfileTiers.Of(Window(width, 960f));
        var shortRec = ZuneProfileTiers.Of(Window(width, 330f));
        Assert.Equal(regular.Tier, shortRec.Tier);
        Assert.True(shortRec.Avatar <= ZuneProfileRules.ShortAvatar && shortRec.Avatar <= regular.Avatar);
        Assert.Equal(ZuneProfileName.Title, shortRec.Name);
        Assert.Equal(ZuneProfileRules.ShortNameLine, shortRec.NameLine);
        Assert.True(shortRec.SectionGap <= ZuneProfileRules.ShortSectionGap);
        // The columns, the rows, the targets and the verbs' home are the tier's own.
        Assert.Equal(regular.TopCols, shortRec.TopCols);
        Assert.Equal(regular.TopRowH, shortRec.TopRowH);
        Assert.Equal(regular.Target, shortRec.Target);
        Assert.Equal(regular.ActionsH, shortRec.ActionsH);
        Assert.Equal(regular.OwnVerbsInHero, shortRec.OwnVerbsInHero);
        Assert.True(shortRec.NameLine >= 0f);
    }

    // ── the concerts ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_concert_records_hold_the_boards_numbers_and_the_compact_link_is_forty()
    {
        Assert.Equal(40f, ZuneConcertTiers.Compact.LinkTarget);
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(0f, ZuneConcertTiers.For(tier).LinkTarget);
        Assert.Equal(480f, ZuneConcertTiers.Huge4K.TicketsW);
        Assert.Equal(40f, ZuneConcertTiers.Huge4K.Target);
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(800f)]
    [InlineData(1440f)]
    [InlineData(2560f)]
    [InlineData(3840f)]
    public void A_short_window_folds_the_concert_pages_voices_and_gaps_and_keeps_their_composition(float width)
    {
        var regular = ZuneConcertTiers.Of(Window(width, 960f));
        var shortRec = ZuneConcertTiers.Of(Window(width, 330f));
        Assert.Equal(regular.Tier, shortRec.Tier);
        Assert.True(shortRec.TitleSize <= ZuneConcertTiers.Small.TitleSize && shortRec.TitleLine <= ZuneConcertTiers.Small.TitleLine);
        Assert.True(shortRec.NumeralSize <= ZuneConcertTiers.ShortNumeral);
        Assert.True(shortRec.SectionGap <= ZuneConcertTiers.ShortGap && shortRec.ZoneGap <= ZuneConcertTiers.ShortGap);
        Assert.True(shortRec.SchedulePhotoH <= ZuneConcertTiers.ShortPhotoH);
        Assert.True(shortRec.FactsSize <= ZuneConcertTiers.Standard.FactsSize);
        // The composition is the tier's own: the slab's width and bleed, the lineup, the related columns, the targets.
        Assert.Equal(regular.TicketsW, shortRec.TicketsW);
        Assert.Equal(regular.SlabBleed, shortRec.SlabBleed);
        Assert.Equal(regular.LineupRows, shortRec.LineupRows);
        Assert.Equal(regular.RelatedColumns, shortRec.RelatedColumns);
        Assert.Equal(regular.Target, shortRec.Target);
        Assert.Equal(regular.HubPill, shortRec.HubPill);
        Assert.Equal(regular.LinkTarget, shortRec.LinkTarget);
    }

    [Fact]
    public void A_regular_window_reads_the_concert_tiers_record_alone()
    {
        Assert.Same(ZuneConcertTiers.Standard, ZuneConcertTiers.Of(Window(1440f, 960f)));
        Assert.Same(ZuneConcertTiers.Standard, ZuneConcertTiers.Of(default));
        Assert.Same(ZuneConcertTiers.Huge4K, ZuneConcertTiers.Of(Window(3840f, 2160f)));
        Assert.NotSame(ZuneConcertTiers.Standard, ZuneConcertTiers.Of(Window(1440f, 699f)));
        Assert.Same(ZuneConcertTiers.Standard, ZuneConcertTiers.Of(Window(1440f, 700f)));
    }

    // ── the popups ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_flyout_is_its_natural_width_or_the_window_less_its_margins()
    {
        // 300 wide: neither flyout nor the picker fits at its natural width, so each takes the window less 12 each side.
        float room = 300f - 2f * ZuneConcertRules.FlyoutMarginX;
        Assert.Equal(room, ZuneConcertRules.FlyoutWidthIn(ZuneConcertRules.WhenFlyoutW, 300f));
        Assert.Equal(room, ZuneConcertRules.FlyoutWidthIn(ZuneConcertRules.PickerW, 300f));
        Assert.Equal(ZuneConcertRules.WhereFlyoutW, ZuneConcertRules.FlyoutWidthIn(ZuneConcertRules.WhereFlyoutW, 300f));
        // A wide window leaves the natural widths alone.
        Assert.Equal(ZuneConcertRules.WhenFlyoutW, ZuneConcertRules.FlyoutWidthIn(ZuneConcertRules.WhenFlyoutW, 1440f));
        Assert.Equal(ZuneConcertRules.PickerW, ZuneConcertRules.FlyoutWidthIn(ZuneConcertRules.PickerW, 1440f));
        // Never below one pixel, whatever the window.
        Assert.Equal(1f, ZuneConcertRules.FlyoutWidthIn(ZuneConcertRules.WhenFlyoutW, 0f));
    }

    [Fact]
    public void A_flyout_never_outgrows_a_short_window_and_the_picker_keeps_its_results_a_row_tall_at_least()
    {
        // The month leaf's 400 cap and the picker's 248 results shrink in a 330 window; a tall window leaves them alone.
        Assert.Equal(330f - 2f * ZuneConcertRules.FlyoutMarginY, ZuneConcertRules.FlyoutHeightIn(ZuneConcertRules.MonthMaxH, 330f));
        Assert.Equal(ZuneConcertRules.MonthMaxH, ZuneConcertRules.FlyoutHeightIn(ZuneConcertRules.MonthMaxH, 960f));
        Assert.Equal(ZuneConcertRules.PickerResultsMaxH, ZuneConcertRules.PickerResultsIn(960f));
        float short330 = ZuneConcertRules.PickerResultsIn(330f);
        Assert.True(short330 < ZuneConcertRules.PickerResultsMaxH && short330 >= ZuneConcertRules.PickerRowH);
        Assert.Equal(ZuneConcertRules.PickerRowH, ZuneConcertRules.PickerResultsIn(0f));
    }

    [Fact]
    public void The_calendar_shares_the_flyout_and_its_days_are_the_tiers_hit_edge()
    {
        // The natural flyout: the days are the 38 they always were.
        Assert.Equal(ZuneConcertRules.CalendarCellW, ZuneConcertRules.CalendarCellWIn(ZuneConcertRules.WhenFlyoutW));
        // A 300 window's flyout: seven days and six gaps fit between the padding.
        float flyout = ZuneConcertRules.FlyoutWidthIn(ZuneConcertRules.WhenFlyoutW, 300f);
        float cell = ZuneConcertRules.CalendarCellWIn(flyout);
        Assert.True(cell < ZuneConcertRules.CalendarCellW);
        Assert.True(7f * cell + 6f * ZuneConcertRules.CalendarGap + 2f * ZuneConcertRules.FlyoutPad <= flyout + 0.001f);
        // 40 at Compact, the board's 32 elsewhere (a target of 36 is below the cell's own 32 only at Small and up, which is no Compact rule).
        Assert.Equal(40f, ZuneConcertRules.CalendarCellHIn(ZuneConcertTiers.Compact.Target));
        Assert.Equal(40f, ZuneConcertRules.BackEdge(ZuneConcertTiers.Compact.Target));
        Assert.Equal(ZuneConcertRules.CalendarCellH, ZuneConcertRules.CalendarCellHIn(0f));
        Assert.Equal(ZuneConcertRules.PickerFieldW, ZuneConcertRules.PickerFieldIn(ZuneConcertRules.PickerW));
        Assert.True(ZuneConcertRules.PickerFieldIn(flyout) < ZuneConcertRules.PickerFieldW);
    }
}
