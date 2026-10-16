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
    [InlineData(2560f, 3, 180f, 3f)]     // C-Artist-Huge: three columns of five, 3 : 1 beside the pick
    [InlineData(3840f, 4, 180f, 4f)]     // C-Artist-Huge4K (past the 3200 rung): four columns of four, 4 : 1
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

    [Theory]
    [InlineData(2560f, false)]
    [InlineData(3199f, false)]
    [InlineData(3200f, true)]
    [InlineData(3840f, true)]
    public void A_Huge_window_reads_the_4K_artist_record_from_3200(float window, bool wide)
    {
        var facts = Facts(window, 1440f, window - 320f);
        var layout = ClassicArtistTiers.Of(facts);
        Assert.Same(wide ? ClassicArtistTiers.Huge4K : ClassicArtistTiers.Huge, layout);
        Assert.Equal(WidthTier.Huge, layout.Tier);
        // The core's chart reads the same columns and rows through the policy.
        Assert.Equal(layout.ChartColumns, ClassicArtistTiers.Policy.ChartMaxColumns(facts));
        Assert.Equal(layout.ChartRows, ClassicArtistTiers.Policy.ChartRows(facts));
        Assert.Equal(wide ? 4 : 5, layout.ChartRows);
        Assert.Equal(wide, layout.UpcomingBesidePick);
        Assert.True(layout.SectionRow);                                  // the 56 section row under the hero at every Huge width
        Assert.Equal(4, layout.DrawerColumns);                           // C-Discography-Huge: the drawer's four columns
    }

    [Fact]
    public void Only_Huge_draws_the_section_row_and_only_Compact_draws_the_discography_strip()
    {
        foreach (var l in new[] { ClassicArtistTiers.Compact, ClassicArtistTiers.Small, ClassicArtistTiers.Standard })
            Assert.False(l.SectionRow);
        Assert.True(ClassicArtistTiers.Huge.SectionRow);
        Assert.True(ClassicArtistTiers.Huge4K.SectionRow);
        Assert.Equal(56f, ClassicArtistTiers.SectionRowHeight);
        Assert.True(ClassicArtistTiers.Compact.DiscoStrip);
        foreach (var l in new[] { ClassicArtistTiers.Small, ClassicArtistTiers.Standard, ClassicArtistTiers.Huge, ClassicArtistTiers.Huge4K })
            Assert.False(l.DiscoStrip);
        // C-Artist-Compact's albums: 152 cards on 12 is the shelves' 164 strip.
        Assert.Equal(152f + 12f, ClassicArtistTiers.Compact.Shelf.FixedCardW);
    }

    [Fact]
    public void The_column_inset_is_the_gutter_plus_half_of_what_the_measure_leaves()
    {
        float measure = ClassicArtistTiers.Standard.PageMaxWidth;
        Assert.Equal(36f, ClassicArtistTiers.Standard.ColumnInset(measure - 400f, 36f), 3);   // inside the measure: the gutter
        Assert.Equal(56f, ClassicArtistTiers.Standard.ColumnInset(measure + 40f, 36f), 3);    // past it: half of the 40 it leaves
        Assert.Equal(36f, ClassicArtistTiers.Huge.ColumnInset(2400f, 36f), 3);
    }

    [Fact]
    public void The_4K_hero_copy_lines_up_with_the_capped_body_column()
    {
        // 3840 less the 320 pane: a 3520 page, the 2400 column centred in it, so the copy and Top tracks both start 36 + 560 in.
        var facts = Facts(3840f, 2160f, 3520f);
        var layout = ClassicArtistTiers.Of(facts);
        float inset = layout.ColumnInset(3520f, 36f);
        Assert.Equal(36f + (3520f - 2400f) / 2f, inset, 3);
        var form = ClassicArtistTiers.HeroForm(ArtistHeroTier.Wide, in facts, 3520f);
        Assert.Equal(ClassicArtistHeroArm.Bottom, form.Arm);
        Assert.True(form.More);                                          // C-Artist-Huge4K's More button
        var m = form.Metrics(36f);
        // The copy keeps the board's 1120 measure from the inset, and the veil holds to its right edge.
        Assert.Equal(1120f, ClassicArtistRules.HeroCopyWidth(3520f, in m, form, inset));
        Assert.Equal((inset + 1120f) / 3520f, ClassicArtistRules.HeroVeilHold(3520f, in m, form, inset), 4);
        // NaN reads the gutter (every caller before the inset).
        Assert.Equal(ClassicArtistRules.HeroCopyWidth(3520f, in m, form), ClassicArtistRules.HeroCopyWidth(3520f, in m, form, 36f));
    }

    [Fact]
    public void Only_the_Huge_hero_ends_in_a_More_button()
    {
        Assert.True(Hero(2560f, 1440f, 1544f).More);                     // C-Artist-Huge
        Assert.False(Hero(1440f, 960f, 1120f).More);                     // tuned Standard
        Assert.False(Hero(800f, 900f, 752f).More);
        Assert.False(Hero(400f, 860f, 400f).More);
    }

    [Fact]
    public void The_discography_drawer_takes_its_columns_pitch_and_head_from_the_tier()
    {
        Assert.Equal(new Album.DrawerShape(1, 40f, 52f), ClassicArtistTiers.Compact.Drawer);   // C-Discography-Compact: one column of 40 rows, a 48 head
        Assert.Equal(new Album.DrawerShape(2, 32f, 40f), ClassicArtistTiers.Small.Drawer);     // C-Discography-Small: two columns
        Assert.Equal(new Album.DrawerShape(0, 32f, 40f), ClassicArtistTiers.Standard.Drawer);  // tuned: the grid's rule
        Assert.Equal(new Album.DrawerShape(4, 32f, 40f), ClassicArtistTiers.Huge.Drawer);      // C-Discography-Huge: four columns
        Assert.Equal(44f, ClassicArtistTiers.Compact.DiscoTab);
        Assert.True(float.IsNaN(ClassicArtistTiers.Standard.DiscoTab));

        // Huge: ten tracks in four columns of three; the verdict sizes the panel at the pitch.
        var huge = Album.DrawerVerdict.For("spotify:album:H", "spotify:album:H", 10, 0, 10, pending: false, gridCols: 11, ClassicArtistTiers.Huge.Drawer);
        Assert.Equal(4, huge.Columns);
        Assert.Equal(3, huge.Rows);
        Assert.Equal(32f, huge.Pitch);
        Assert.Equal(40f + 3f * 32f, huge.PanelHeight);
        // Compact: one column of 40 rows under a 52 head allowance.
        var compact = Album.DrawerVerdict.For("spotify:album:C", "spotify:album:C", 7, 0, 7, pending: false, gridCols: 2, ClassicArtistTiers.Compact.Drawer);
        Assert.Equal(1, compact.Columns);
        Assert.Equal(7, compact.Rows);
        Assert.Equal(40f, compact.Pitch);
        Assert.Equal(52f + 7f * 40f, compact.PanelHeight);
        Assert.True(compact.Pitch >= ClassicArtistTiers.CompactTarget);
        // Standard keeps the grid's rule: two columns from five grid columns, one below.
        Assert.Equal(2, Album.DrawerVerdict.For("u", "u", 13, 0, 13, false, 5, ClassicArtistTiers.Standard.Drawer).Columns);
        Assert.Equal(1, Album.DrawerVerdict.For("u", "u", 13, 0, 13, false, 4, ClassicArtistTiers.Standard.Drawer).Columns);
        // No shape is the drawer before tiers.
        Assert.Equal(Album.DrawerVerdict.RowPitch, Album.DrawerVerdict.For("u", "u", 3, 0, 3, false, 3).Pitch);
    }

    [Fact]
    public void The_pivot_words_reach_40_at_Compact_with_the_underline_inside()
    {
        Assert.Equal(40f, ClassicArtistTiers.Compact.Target);
        Assert.Equal(40f, ClassicProfileTiers.Compact.Target);
        Assert.Equal(32f, ClassicProfileTiers.Standard.Target);
        Assert.Equal(Detail.BandLayout.UnderlineY, ClassicArtistRules.PivotUnderlineY(Detail.BandLayout.ItemHeight));
        Assert.True(ClassicArtistRules.PivotUnderlineY(40f) + Detail.BandLayout.UnderlineHeight <= 40f);
    }

    [Fact]
    public void The_overlay_meta_wraps_4_apart_with_16_between_its_runs()
    {
        Assert.Equal(4f, ClassicArtistTiers.OverlayMetaRowGap);
        Assert.Equal(16f, ClassicArtistTiers.OverlayMetaColumnGap);
    }

    [Fact]
    public void The_chart_takes_the_tiers_rows_and_its_skeleton_the_tiers_columns()
    {
        Assert.Equal(4, ArtistSections.ChartColumns(16, 4, 4));      // C-Artist-Huge4K: 4 x 4
        Assert.Equal(4, ArtistSections.ChartColumns(15, 4, 4));
        Assert.Equal(2, ArtistSections.ChartColumns(5, 4, 4));
        Assert.Equal(3, ArtistSections.ChartColumns(15, 3));         // C-Artist-Huge: 3 x 5
        // The skeleton's seed (ten) at each tier's columns and rows: Compact 1 x 5 (two pages), Standard 2 x 5, Huge 2 x 5, Huge 4K 3 x 4.
        Assert.Equal((10, 1, 5), ArtistSections.ChartSkeletonShape(0, 1, 5));
        Assert.Equal((10, 2, 5), ArtistSections.ChartSkeletonShape(0, 2, 5));
        Assert.Equal((10, 2, 5), ArtistSections.ChartSkeletonShape(0, 3, 5));
        Assert.Equal((10, 3, 4), ArtistSections.ChartSkeletonShape(0, 4, 4));
        Assert.Equal((5, 2, 3), ArtistSections.ChartSkeletonShape(5, 4, 4));
        // A structure that declares nothing: the old 2 x 5.
        Assert.Equal((10, 2, 5), ArtistSections.ChartSkeletonShape(0));
        Assert.Equal(ArtistSections.ChartMaxRows, ArtistTierPolicy.Default.ChartRows(default));
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
        Assert.True(compact.Eyebrow);                                       // the board's "Profile" eyebrow, flush over the name
        Assert.Equal(0f, compact.EyebrowGap);
        Assert.Equal(20f + 128f + 12f + 16f + 40f + 8f + 56f + 12f + 40f + 16f, compact.Height);

        var small = Profile(800f, 900f, 752f);                             // C-Profile-Small: a 200 row, the actions at its right end
        Assert.Equal(ClassicProfileHeroArm.Row, small.Arm);
        Assert.Equal(200f, small.Height);                                  // the eyebrow 4 over the name: the 136 copy sets the row
        Assert.True(small.Eyebrow);
        Assert.Equal(4f, small.EyebrowGap);
        Assert.True(small.ActionsBeside);
        Assert.Equal(ClassicArtistName.Title, small.Name);

        Assert.Equal(260f, Profile(1440f, 960f, 1120f).Height);            // Standard: tuned
        Assert.False(Profile(1440f, 960f, 1120f).Eyebrow);                  // the tuned Standard hero keeps no eyebrow (P3b)

        var huge = Profile(2560f, 1440f, 2240f);                           // C-Profile-Huge: a 320 row, a 240 avatar
        Assert.Equal(320f, huge.Height);
        Assert.True(huge.Eyebrow);
        Assert.Equal(12f, huge.EyebrowGap);
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
    public void The_hub_pills_reach_40_at_Compact_and_Huge_sets_the_lineup_beside_the_dates()
    {
        Assert.Equal(40f, ClassicConcertLayout.HubPillOf(Facts(400f, 860f, 400f)));
        Assert.Equal(40f, ClassicConcertLayout.HubPillOf(Facts(300f, 640f, 300f)));
        Assert.Equal(32f, ClassicConcertLayout.HubPillOf(Facts(800f, 600f, 752f)));
        Assert.Equal(32f, ClassicConcertLayout.HubPillOf(Facts(1440f, 960f, 1120f)));
        Assert.Equal(32f, ClassicConcertLayout.HubPillOf(Facts(2560f, 1440f, 2240f)));
        // C-Concert-Huge: the lineup a 640 column beside the four-column dates grid; the other tiers keep the full-width lineup and the related shelf.
        Assert.Equal(640f, ClassicConcertTiers.Huge.LineupWidth);
        Assert.Equal(4, ClassicConcertTiers.Huge.DateColumns);
        Assert.Equal(0f, ClassicConcertTiers.Standard.LineupWidth);
        Assert.Equal(0f, ClassicConcertTiers.Small.LineupWidth);
        Assert.Equal(0f, ClassicConcertTiers.Compact.LineupWidth);
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
