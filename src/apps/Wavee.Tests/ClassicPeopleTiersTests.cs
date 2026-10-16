// ── Wavee.Tests/ClassicPeopleTiersTests.cs — the artist and people pages' ladders and tier records (Classic Rules/Tiers.Artist.cs, Tiers.Profile.cs,
// Tiers.Concert.cs; themes plan 3.5, TC6) ──
//
// Pure: the facts are `WindowTiers.Resolve` over Classic's policy at the board sizes (the window less the nav pane is the content region), and every
// assertion is on a record, never on pixels. The boards: C-Artist(-Compact/-Small/-Huge/-Huge4K), C-Profile(-Compact/-Small/-Huge),
// C-Concert(-Compact/-Small/-Huge), C-Breakpoints' "Artist hero" row (the 60 % Short cap).

using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ClassicPeopleTiersTests
{
    /// <summary>The tiers of a <paramref name="w"/> x <paramref name="h"/> window whose content region is <paramref name="content"/> wide (a fresh resolve).</summary>
    static TierFacts Facts(float w, float h, float content) => WindowTiers.Resolve(ClassicTiers.Policy, w, h, content);

    static ClassicArtistHeroForm Hero(float w, float h, float content)
    {
        var facts = Facts(w, h, content);
        return ClassicArtistTiers.HeroForm(ClassicArtistTiers.HeroTierFor(content), in facts, content);
    }

    // ── the artist hero per tier ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_the_photograph_is_full_with_the_copy_on_it_and_the_actions_a_40_row_under_it()
    {
        var f = Hero(400f, 860f, 400f);                                     // C-Artist-Compact
        Assert.Equal(ClassicArtistHeroArm.Overlay, f.Arm);
        Assert.Equal(300f, f.Photo);
        Assert.Equal(300f + 16f + 40f, f.Height);
        Assert.Equal(40f, f.Target);
        Assert.Equal(ClassicArtistName.Compact, f.Name);
        Assert.Equal(ClassicHeroActions.Row, f.Actions);
        // At the 300 floor the four actions do not fit one row: two rows, still 40 targets.
        var floor = Hero(300f, 860f, 300f);
        Assert.Equal(ClassicHeroActions.TwoRows, floor.Actions);
        Assert.Equal(300f + 16f + 40f + 8f + 40f, floor.Height);
        // The metrics carry the photograph's band and the page gutter.
        var m = f.Metrics(16f);
        Assert.Equal(300f, ArtistHeroLayout.PhotoHeightFor(m));
        Assert.True(m.Stacked);
    }

    [Fact]
    public void Small_the_Medium_hero_sets_its_copy_at_the_bottom_and_a_Short_window_caps_it_at_288()
    {
        var tall = Hero(800f, 900f, 752f);                                  // C-Artist-Small at a Regular height
        Assert.Equal(ArtistHeroTier.Medium, tall.Tier);
        Assert.Equal(ClassicArtistHeroArm.Bottom, tall.Arm);
        Assert.Equal(384f, tall.Height);
        Assert.Equal(560f, tall.CopyMax);
        Assert.Equal(1, tall.BioLines);
        Assert.Equal(ClassicArtistName.Title, tall.Name);

        var board = Hero(800f, 600f, 752f);                                 // the board's 800 x 600 is Short: 60 % of its 480 page
        Assert.Equal(288f, board.Height);
        Assert.Equal(ClassicArtistHeroArm.Bottom, board.Arm);
    }

    [Fact]
    public void Short_under_480_tall_the_hero_is_the_126_strip_with_Play_and_Follow()
    {
        var strip = Hero(800f, 330f, 752f);
        Assert.Equal(126f, strip.Height, 3);
        Assert.False(strip.Details);
        Assert.Equal(0, strip.BioLines);
        Assert.Equal(ClassicHeroActions.Primary, strip.Actions);
        Assert.Equal(40f, Hero(300f, 330f, 300f).Target);                   // Compact keeps its 40 targets in the strip
        // The strip leaves the 210 Short page room for the band and a first section.
        Assert.True(strip.Height <= ClassicTiers.ShortFloorH * 0.6f + 0.5f);
    }

    [Fact]
    public void Standard_is_the_tuned_design()
    {
        var wide = Hero(1440f, 960f, 1120f);
        Assert.Same(ClassicArtistTiers.StandardForm(ArtistHeroTier.Wide), wide);
        Assert.Equal(ClassicArtistHeroArm.Centred, wide.Arm);
        Assert.Equal(440f, wide.Height);
        Assert.Same(ClassicArtistTiers.StandardForm(ArtistHeroTier.Medium), Hero(1072f, 960f, 752f));
    }

    [Theory]
    [InlineData(2560f, 1544f, 560f)]    // C-Artist-Huge: the 2560 window with the 320 pane and two 340 panels
    [InlineData(3840f, 2664f, 640f)]    // C-Artist-Huge4K: 420 panels
    [InlineData(1920f, 1600f, 560f)]    // what Auto zoom lays a 4K monitor out at
    [InlineData(3840f, 3520f, 640f)]    // 4K with no panel open: the ceiling
    public void Huge_the_hero_is_designed_not_scaled(float window, float content, float height)
    {
        var f = Hero(window, 1440f, content);
        Assert.Equal(ClassicArtistHeroArm.Bottom, f.Arm);
        Assert.Equal(height, f.Height);
        Assert.Equal(1120f, f.CopyMax);
        Assert.Equal(760f, f.BioMax);
        Assert.Equal(40f, f.CopyBottom);
        Assert.Equal(ClassicArtistName.Display, f.Name);
    }

    [Fact]
    public void The_core_reads_the_records_through_the_policy_with_the_one_page_gutter()
    {
        Assert.Same(ClassicArtistTiers.Policy, ClassicPolicies.Bundle.ArtistTiers);
        foreach (var (w, h, c) in new[] { (400f, 860f, 400f), (800f, 600f, 752f), (1440f, 960f, 1120f), (2560f, 1440f, 1544f) })
        {
            var facts = Facts(w, h, c);
            var tier = ClassicArtistTiers.HeroTierFor(c);
            var m = ClassicArtistTiers.Policy.Hero(tier, in facts, c);
            Assert.Equal(ClassicTiers.PageOf(facts).Gutter, m.Gutter);
            Assert.Equal(ClassicArtistTiers.HeroForm(tier, in facts, c).Height, m.MinHeight);
        }
    }

    // ── the artist page per tier ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(400f, 1, 128f, 2f)]      // C-Artist-Compact: one chart column, a two-column discography
    [InlineData(800f, 2, 180f, 2f)]
    [InlineData(1440f, 2, 180f, 2f)]
    [InlineData(2560f, 4, 180f, 3f)]     // C-Artist-Huge: up to four columns (the 264 floor fits three at 1544), 3 : 1 beside the pick
    public void The_page_record_per_window_tier(float window, int chart, float disco, float share)
    {
        var facts = Facts(window, 1000f, window);
        var layout = ClassicArtistTiers.Of(facts);
        Assert.Equal(facts.Window >= WidthTier.Huge ? WidthTier.Huge : facts.Window, layout.Tier);
        Assert.Equal(chart, layout.ChartColumns);
        Assert.Equal(chart, ClassicArtistTiers.Policy.ChartMaxColumns(facts));
        Assert.Equal(disco, layout.DiscoMinColumn);
        Assert.Equal(share, layout.ChartShare);
        Assert.Equal(window < 528f ? 40f : 32f, layout.Target);
    }

    [Fact]
    public void Compact_shelves_are_a_strip_and_Huge_ones_take_ten_columns_to_2400()
    {
        Assert.Equal(164f, ClassicArtistTiers.Compact.Shelf.FixedCardW);
        Assert.False(ClassicArtistTiers.Compact.Shelf.Pager);
        Assert.Equal(10, ClassicArtistTiers.Huge.Shelf.MaxColumns);
        Assert.Equal(2400f, ClassicArtistTiers.Huge.PageMaxWidth);
        // Two Compact discography columns from the 300 floor to the 400 board (a 16 gutter either side, the cards' 8 overhang).
        foreach (float window in new[] { 300f, 400f })
        {
            float grid = window - 2f * 16f;
            int cols = Math.Max(1, (int)((grid + ClassicArtistTiers.Compact.DiscoGap) / (ClassicArtistTiers.Compact.DiscoMinColumn + ClassicArtistTiers.Compact.DiscoGap)));
            Assert.Equal(2, cols);
        }
    }

    [Fact]
    public void The_top_band_goes_beside_at_760_and_holds_to_736()
    {
        Assert.True(ClassicArtistTiers.TopBandWide(760f, wasWide: false));
        Assert.False(ClassicArtistTiers.TopBandWide(759f, wasWide: false));
        Assert.True(ClassicArtistTiers.TopBandWide(736f, wasWide: true));
        Assert.False(ClassicArtistTiers.TopBandWide(735f, wasWide: true));
    }

    [Fact]
    public void The_chart_clamps_by_its_rows_and_the_tier()
    {
        Assert.Equal(1, ArtistSections.ChartColumns(5, 4));
        Assert.Equal(2, ArtistSections.ChartColumns(10, 4));
        Assert.Equal(3, ArtistSections.ChartColumns(15, 4));
        Assert.Equal(1, ArtistSections.ChartColumns(10, 1));         // Compact: one column of five
        Assert.Equal(2, ArtistSections.ChartColumns(50));             // a structure that declares nothing: two
    }

    // ── the profile per tier ────────────────────────────────────────────────────────────────────────────────────────

    static ClassicProfileHeroForm Profile(float w, float h, float content)
    {
        var facts = Facts(w, h, content);
        return ClassicProfileTiers.HeroForm(ClassicArtistTiers.HeroTierFor(content), in facts, content);
    }

    [Fact]
    public void The_profile_hero_per_tier_follows_its_board()
    {
        var compact = Profile(400f, 860f, 400f);                           // C-Profile-Compact: centred, a 128 avatar, 96 x 56 stats, 40 actions
        Assert.Equal(ClassicProfileHeroArm.Centred, compact.Arm);
        Assert.Equal(128f, compact.Avatar);
        Assert.Equal(56f, compact.StatMinH);
        Assert.Equal(96f, compact.StatMinW);
        Assert.Equal(40f, compact.Target);
        Assert.Equal(20f + 128f + 12f + 40f + 8f + 56f + 12f + 40f + 16f, compact.Height);

        var small = Profile(800f, 900f, 752f);                             // C-Profile-Small: a 200 row, the actions at its right end
        Assert.Equal(ClassicProfileHeroArm.Row, small.Arm);
        Assert.Equal(200f, small.Height);
        Assert.True(small.ActionsBeside);
        Assert.Equal(ClassicArtistName.Title, small.Name);

        Assert.Equal(260f, Profile(1440f, 960f, 1120f).Height);            // Standard: tuned

        var huge = Profile(2560f, 1440f, 2240f);                           // C-Profile-Huge: a 320 row, a 240 avatar
        Assert.Equal(320f, huge.Height);
        Assert.Equal(240f, huge.Avatar);
        Assert.True(huge.ActionsBeside);
    }

    [Fact]
    public void A_Short_window_never_gives_the_profile_hero_more_than_60_percent_of_its_page()
    {
        foreach (var (w, h, c) in new[] { (400f, 600f, 400f), (300f, 640f, 300f), (800f, 600f, 752f), (800f, 330f, 752f), (300f, 330f, 300f) })
        {
            var f = Profile(w, h, c);
            Assert.True(f.Height <= ClassicArtistTiers.ShortHeroCap, $"{w} x {h}: {f.Height}");
            if (h < 480f) Assert.True(f.Height <= ClassicArtistTiers.StripHeight + 0.5f, $"{w} x {h}: {f.Height}");
        }
    }

    [Fact]
    public void The_profile_list_ladder_and_its_cards()
    {
        Assert.Equal(0, ClassicProfileTiers.List.Resolve(639f, -1));
        Assert.Equal(1, ClassicProfileTiers.List.Resolve(640f, -1));
        Assert.Equal(0, ClassicProfileTiers.List.Resolve(663f, 0));           // re-admitted 24 past the edge
        Assert.Equal(1, ClassicProfileTiers.List.Resolve(664f, 0));
        Assert.Equal(1, ClassicProfileTiers.List.Resolve(640f, 1));
        Assert.Equal(128f, ClassicArtistTiers.Policy.ProfileListMinCell(Facts(400f, 860f, 400f)));
        Assert.Equal(176f, ClassicArtistTiers.Policy.ProfileListMinCell(Facts(1440f, 960f, 1120f)));
        // Two cards at the 300 floor and the 400 board (the body is the window less its 16 gutters).
        Assert.Equal(2, ProfileGridFit.Columns(268f, 12f, 128f));
        Assert.Equal(2, ProfileGridFit.Columns(368f, 12f, 128f));
        var wide = ProfileGridFit.For(1000f, 12f, wide: true, 176f);
        Assert.True(wide.Strip);
        Assert.False(wide.StackTools);
        Assert.Equal(5, wide.Columns);
    }

    // ── the concert event page per tier ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_event_page_record_per_window_tier()
    {
        var compact = ClassicConcertTiers.Of(Facts(400f, 860f, 400f));      // C-Concert-Compact: a 220 stacked pane, 40 targets, no rail
        var c = ClassicConcertTiers.EventHero(compact, wide: true);
        Assert.False(c.Split);
        Assert.Equal(220f, c.MediaHeight);
        Assert.Equal(40f, compact.Target);
        Assert.Equal(0f, compact.RailWidth);

        var small = ClassicConcertTiers.EventHero(ClassicConcertTiers.Of(Facts(800f, 600f, 752f)), wide: false);   // C-Concert-Small: split at 240
        Assert.True(small.Split);
        Assert.Equal(240f, small.Height);

        var standard = ClassicConcertTiers.Of(Facts(1440f, 960f, 1120f));  // tuned
        Assert.Equal(ClassicConcertTiers.EditorialHero(true), ClassicConcertTiers.EventHero(standard, wide: true));
        Assert.Equal(ClassicConcertTiers.EditorialHero(false), ClassicConcertTiers.EventHero(standard, wide: false));
        Assert.Equal(320f, standard.RailWidth);

        var huge = ClassicConcertTiers.Of(Facts(2560f, 1440f, 2240f));     // C-Concert-Huge: split at 400 with 40 of air, a 360 rail
        Assert.Equal(400f, ClassicConcertTiers.EventHero(huge, wide: true).Height);
        Assert.Equal(40f, ClassicConcertTiers.EventHero(huge, wide: true).Padding);
        Assert.Equal(360f, huge.RailWidth);
    }

    [Fact]
    public void The_concert_ladders_hold_like_the_tiers()
    {
        Assert.True(ClassicConcertTiers.ScheduleWide(760f, wasWide: false, initialized: false));
        Assert.False(ClassicConcertTiers.ScheduleWide(759f, wasWide: true, initialized: false));   // a fresh resolve uses the edge itself
        Assert.True(ClassicConcertTiers.DetailWide(860f, wasWide: true));
        Assert.False(ClassicConcertTiers.DetailWide(919f, wasWide: false));
    }
}
