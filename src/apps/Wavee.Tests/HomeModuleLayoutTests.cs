// ── Wavee.Tests/HomeModuleLayoutTests.cs — ONE source of truth for Home's module geometry (Wave 5, owner P) ──────────
//
// 0.2.9 `HomeModules.cs:490-804` had no direct tests; the renderer AND the landing estimator read these numbers, and an
// estimate that disagrees with the rendered height re-pins the scroll anchor mid-scroll. Every expected value below is
// the skin's own arithmetic restated in tokens (Spacing XXS 2 · XS 4 · S 8 · M 12 · L 16 · XL 20 · XXL 24 · XXXL 32;
// Design.Size Thumb32/48/56/64, SectionGap 32 / SectionGapWide 40), so a hand-picked change to either side fails here.
//
// ONE DELIBERATE DIFFERENCE FROM 0.2.9: the grid card chrome DELEGATES to `Controls.GridCardChromeFor` (label overhead 28
// + 20 per title line + 18 for the metadata line), so `GridCardChrome` is 66 where 0.2.9's MediaCard stated 52 — the
// estimator must say what 0.3's card draws.

using FluentGpu.Scene;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class HomeModuleLayoutTests
{
    // ── columns ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HomeGroupKind.MixBand, 1081f, 6)]
    [InlineData(HomeGroupKind.MixBand, 1080f, 3)]
    [InlineData(HomeGroupKind.MixBand, 621f, 3)]
    [InlineData(HomeGroupKind.MixBand, 620f, 2)]
    [InlineData(HomeGroupKind.QuickGrid, 1121f, 4)]
    [InlineData(HomeGroupKind.QuickGrid, 1120f, 3)]
    [InlineData(HomeGroupKind.QuickGrid, 781f, 3)]
    [InlineData(HomeGroupKind.QuickGrid, 780f, 2)]
    [InlineData(HomeGroupKind.ChipCards, 1021f, 3)]
    [InlineData(HomeGroupKind.ChipCards, 1020f, 2)]
    [InlineData(HomeGroupKind.ChipCards, 681f, 2)]
    [InlineData(HomeGroupKind.ChipCards, 680f, 1)]
    [InlineData(HomeGroupKind.WeeklyPair, 761f, 2)]
    [InlineData(HomeGroupKind.WeeklyPair, 760f, 1)]
    [InlineData(HomeGroupKind.Hero, 2000f, 1)]
    [InlineData(HomeGroupKind.QueueList, 2000f, 1)]
    public void Columns_are_the_prototypes_container_queries(HomeGroupKind kind, float width, int columns)
        => Assert.Equal(columns, HomeModuleLayout.Columns(kind, width));

    [Fact]
    public void The_radio_dial_floor_fits_one_or_two_station_columns_and_never_three()
    {
        // 32 art + 2×8 row pad + a 12 gap to the text + three 64 thumb rungs of name.
        Assert.Equal(252f, HomeModuleLayout.RadioColMin);
        Assert.Equal(1, HomeModuleLayout.RadioColumns(0f));
        Assert.Equal(1, HomeModuleLayout.RadioColumns(527f));      // (527 + 24) / 276 < 2
        Assert.Equal(2, HomeModuleLayout.RadioColumns(528f));      // (528 + 24) / 276 = 2
        Assert.Equal(2, HomeModuleLayout.RadioColumns(4000f));     // capped
        Assert.Equal(HomeModuleLayout.RadioColumns(900f), HomeModuleLayout.Columns(HomeGroupKind.RadioDial, 900f));
    }

    [Fact]
    public void The_module_gap_breaks_at_1080_on_the_section_rhythm()
    {
        Assert.Equal(40f, HomeModuleLayout.Gap(1080f));
        Assert.Equal(32f, HomeModuleLayout.Gap(1079.9f));
        Assert.Equal(Design.Size.SectionGapWide, HomeModuleLayout.ModuleGap);
        Assert.Equal(Design.Size.SectionGap, HomeModuleLayout.ModuleGapNarrow);
    }

    // ── per-card heights, row gaps, display caps ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HomeGroupKind.QuickGrid, 56f)]                   // the tile is exactly its cover
    [InlineData(HomeGroupKind.WeeklyPair, 88f)]                  // 56 art + 16 padding a side
    [InlineData(HomeGroupKind.MixBand, 142f)]                    // 2×16 + 36 + 4 + 16 + 3×16 + 3×2
    [InlineData(HomeGroupKind.ChipCards, 96f)]                   // 20 + 20 + 16 + 2×8 + 2×12
    [InlineData(HomeGroupKind.RadioDial, 48f)]
    [InlineData(HomeGroupKind.QueueList, 53f)]                   // 20 + 16 + 2×8 + the 1px divider
    [InlineData(HomeGroupKind.RatedShelf, 64f)]                  // 48 + 2×8
    [InlineData(HomeGroupKind.Shelf, 56f)]
    public void Card_heights_are_the_skins_own_arithmetic(HomeGroupKind kind, float height)
        => Assert.Equal(height, HomeModuleLayout.CardHeight(kind));

    [Theory]
    [InlineData(HomeGroupKind.QuickGrid, 12f)]
    [InlineData(HomeGroupKind.WeeklyPair, 12f)]
    [InlineData(HomeGroupKind.ChipCards, 12f)]
    [InlineData(HomeGroupKind.RatedShelf, 2f)]                   // the audiobook stack keeps its dense 2
    [InlineData(HomeGroupKind.MixBand, 0f)]
    [InlineData(HomeGroupKind.QueueList, 0f)]
    public void One_row_gap_for_every_wrapped_grid(HomeGroupKind kind, float gap)
        => Assert.Equal(gap, HomeModuleLayout.RowGap(kind));

    [Theory]
    [InlineData(HomeGroupKind.Hero, 5, 1)]
    [InlineData(HomeGroupKind.QuickGrid, 20, 8)]
    [InlineData(HomeGroupKind.ChipCards, 20, 6)]
    [InlineData(HomeGroupKind.RadioDial, 20, 12)]
    [InlineData(HomeGroupKind.QueueList, 20, 6)]
    [InlineData(HomeGroupKind.RatedShelf, 20, 6)]
    [InlineData(HomeGroupKind.Featured, 20, 4)]                  // the feature + three companions
    [InlineData(HomeGroupKind.MixBand, 20, 20)]
    [InlineData(HomeGroupKind.QuickGrid, 3, 3)]
    public void The_estimator_sizes_what_is_SHOWN(HomeGroupKind kind, int count, int shown)
        => Assert.Equal(shown, HomeModuleLayout.Shown(kind, count));

    // ── content extents ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_wrapped_grid_is_rows_of_cards_plus_the_row_gaps_between_them()
    {
        Assert.Equal(2f * 56f + 12f, HomeModuleLayout.ContentExtent(HomeGroupKind.QuickGrid, 1200f, 8));     // 4 cols, 2 rows
        Assert.Equal(3f * 56f + 2f * 12f, HomeModuleLayout.ContentExtent(HomeGroupKind.QuickGrid, 800f, 20));  // capped 8, 3 cols
        Assert.Equal(6f * 53f, HomeModuleLayout.ContentExtent(HomeGroupKind.QueueList, 1200f, 10));            // capped 6
        Assert.Equal(6f * 64f + 5f * 2f, HomeModuleLayout.ContentExtent(HomeGroupKind.RatedShelf, 1200f, 6));
        Assert.Equal(0f, HomeModuleLayout.ContentExtent(HomeGroupKind.QuickGrid, 1200f, 0));
    }

    [Fact]
    public void The_mix_band_adds_a_hairline_between_wrapped_rows_and_its_contour()
    {
        Assert.Equal(142f + 2f, HomeModuleLayout.ContentExtent(HomeGroupKind.MixBand, 1200f, 6));             // one row
        Assert.Equal(2f * 142f + 1f + 2f, HomeModuleLayout.ContentExtent(HomeGroupKind.MixBand, 700f, 6));     // 3 cols, 2 rows
    }

    [Fact]
    public void The_hero_and_the_shelves_read_their_own_geometry()
    {
        Assert.Equal(HomeHeroLayout.HeightFor(1200f), HomeModuleLayout.ContentExtent(HomeGroupKind.Hero, 1200f, 3));
        Assert.Equal(HomeHeroLayout.HeightFor(1200f), HomeModuleLayout.HeroHeight(1200f));

        float shelf = HomeModuleLayout.ShelfExtent(1100f);
        Assert.Equal(Controls.ShelfExtent(FillRowVirtualLayout.Fit(1100f, 148f, 188f, 12f).CardW), shelf);
        Assert.Equal(shelf, HomeModuleLayout.ContentExtent(HomeGroupKind.Recents, 1100f, 5));
        Assert.Equal(shelf, HomeModuleLayout.ContentExtent(HomeGroupKind.PodcastShelf, 1100f, 5));
        Assert.Equal(shelf, HomeModuleLayout.ContentExtent(HomeGroupKind.DiscoverFeed, 1100f, 50));
    }

    [Fact]
    public void The_editorial_break_sits_beside_its_companions_when_wide_and_above_them_when_narrow()
    {
        const float feature = 2f * 20f + 148f;                             // 188
        const float three = 3f * (2f * 12f + 48f) + 2f * 8f;               // 232
        Assert.Equal(MathF.Max(feature, three), HomeModuleLayout.ContentExtent(HomeGroupKind.Featured, 980f, 9));
        Assert.Equal(feature + 16f + three, HomeModuleLayout.ContentExtent(HomeGroupKind.Featured, 979f, 9));
        Assert.Equal(feature, HomeModuleLayout.FeaturedExtent(1200f, 1));
        Assert.Equal(feature, HomeModuleLayout.FeaturedExtent(1200f, 2));                  // max(188, 72)
        Assert.Equal(feature + 16f + 72f, HomeModuleLayout.FeaturedExtent(700f, 2));
    }

    // ── the grid card chrome (delegated) and the shelf card ─────────────────────────────────────────────────────────

    [Fact]
    public void The_grid_card_chrome_is_the_cards_own_number()
    {
        Assert.Equal(66f, HomeModuleLayout.GridCardChrome);                                   // 0.2.9: 52 (file header)
        Assert.Equal(HomeModuleLayout.GridCardChrome, HomeModuleLayout.GridCardChromeFor(1, hasSubtitle: true));
        Assert.Equal(Controls.GridCardChromeFor(2, true), HomeModuleLayout.GridCardChromeFor(2, true));
        Assert.Equal(28f + 2f * 20f, HomeModuleLayout.GridCardChromeFor(2, hasSubtitle: false));
    }

    [Fact]
    public void A_title_line_count_below_one_clamps_to_one()
    {
        Assert.Equal(HomeModuleLayout.GridCardChromeFor(1, true), HomeModuleLayout.GridCardChromeFor(0, true));
        Assert.Equal(HomeModuleLayout.GridCardChromeFor(1, false), HomeModuleLayout.GridCardChromeFor(-3, false));
    }

    [Fact]
    public void The_shelf_card_height_is_the_controls_shelf_height()
    {
        Assert.Equal(Controls.ShelfHeight(160f), HomeModuleLayout.ShelfCardHeight(160f));
        Assert.Equal(232f, HomeModuleLayout.ShelfCardHeight(160f));
    }

    // ── the Fold tile ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_zero_width_first_frame_never_parks_a_cover_at_a_negative_x()
    {
        HomeModuleLayout.FoldRest(0, 0f, out float x0, out float y0, out float r0);
        HomeModuleLayout.FoldRest(1, 0f, out float x1, out float y1, out float r1);
        HomeModuleLayout.FoldRest(2, 0f, out float x2, out float y2, out float r2);
        Assert.Equal((0f, 38f, -11f), (x0, y0, r0));
        Assert.Equal((44f, 22f, 5f), (x1, y1, r1));
        Assert.Equal((92f, 8f, -2f), (x2, y2, r2));
    }

    [Fact]
    public void The_stack_hangs_off_the_right_edge_of_a_real_card()
    {
        // right:-40 on a 250-wide stack box → the stack's left is cardW − 210.
        HomeModuleLayout.FoldRest(0, 500f, out float x, out _, out _);
        Assert.Equal(290f, x);
        HomeModuleLayout.FoldRest(7, 500f, out float xLast, out _, out _);              // every index past 1 is the back cover
        Assert.Equal(290f + 92f, xLast);
    }

    [Fact]
    public void The_hover_fan_is_a_delta_on_the_rest_pose()
    {
        HomeModuleLayout.FoldFan(0, out float dx0, out float dy0, out float dr0);
        HomeModuleLayout.FoldFan(1, out float dx1, out float dy1, out float dr1);
        HomeModuleLayout.FoldFan(2, out float dx2, out float dy2, out float dr2);
        Assert.Equal((-10f, 6f, -5f), (dx0, dy0, dr0));
        Assert.Equal((2f, -6f, 3f), (dx1, dy1, dr1));
        Assert.Equal((10f, 0f, 3f), (dx2, dy2, dr2));
    }

    [Fact]
    public void The_fold_rows_never_estimate_zero()
    {
        Assert.Equal(32f + 176f + 24f, HomeModuleLayout.FoldExtent);
        Assert.Equal(32f + 96f + 24f, HomeModuleLayout.FoldStateExtent);
    }

    // ── the keys ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_row_key_names_the_kind_exactly_as_its_enum_spelling()
    {
        Assert.Equal("home-QuickGrid:spotify:section:x", HomeModuleLayout.RowKey(HomeGroupKind.QuickGrid, "spotify:section:x"));
        foreach (HomeGroupKind kind in Enum.GetValues<HomeGroupKind>())
            Assert.Equal("home-" + kind + ":u", HomeModuleLayout.RowKey(kind, "u"));
    }

    [Fact]
    public void A_source_card_key_is_the_groups_identity_plus_the_cards()
    {
        var card = HomeCard.Blank(HomeCardKind.Playlist, 3);
        string suffix = "\u001F" + card.DedupeKey.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("spotify:section:s" + suffix,
            HomeModuleLayout.SourceCardKey(new HomeGroup(HomeGroupKind.Shelf, "Title", [card], Uri: "spotify:section:s"), card));
        Assert.Equal("Title" + suffix, HomeModuleLayout.SourceCardKey(new HomeGroup(HomeGroupKind.Shelf, "Title", [card]), card));
        Assert.Equal("MixBand" + suffix, HomeModuleLayout.SourceCardKey(new HomeGroup(HomeGroupKind.MixBand, null, [card]), card));
    }

    [Fact]
    public void A_source_group_key_is_stable_per_instance_and_by_content()
    {
        HomeCard[] cards = [HomeCard.Blank(HomeCardKind.Playlist, 1), HomeCard.Blank(HomeCardKind.Album, 2)];
        var group = new HomeGroup(HomeGroupKind.QuickGrid, "Jump back in", cards, Uri: "spotify:section:keys");

        string key = HomeModuleLayout.SourceGroupKey(group);
        Assert.StartsWith("home-source:", key, StringComparison.Ordinal);
        Assert.Same(key, HomeModuleLayout.SourceGroupKey(group));                               // memoized per instance
        Assert.Equal(key, HomeModuleLayout.SourceGroupKey(group with { Cards = cards.ToArray() }));  // same content, new instance
    }

    [Fact]
    public void A_source_group_key_moves_when_the_rendered_structure_does()
    {
        HomeCard[] cards = [HomeCard.Blank(HomeCardKind.Playlist, 1), HomeCard.Blank(HomeCardKind.Album, 2)];
        var group = new HomeGroup(HomeGroupKind.QuickGrid, "Jump back in", cards, Uri: "spotify:section:keys2");
        string key = HomeModuleLayout.SourceGroupKey(group);

        Assert.NotEqual(key, HomeModuleLayout.SourceGroupKey(group with { Cards = [cards[0], HomeCard.Blank(HomeCardKind.Album, 9)] }));
        Assert.NotEqual(key, HomeModuleLayout.SourceGroupKey(group with { Cards = [cards[1], cards[0]] }));     // order
        Assert.NotEqual(key, HomeModuleLayout.SourceGroupKey(group with { Title = "Something else" }));
        Assert.NotEqual(key, HomeModuleLayout.SourceGroupKey(group with { TotalCount = 40 }));
        Assert.NotEqual(key, HomeModuleLayout.SourceGroupKey(group with { Kind = HomeGroupKind.MixBand }));
    }

    [Fact]
    public void A_section_set_key_is_memoized_per_list_and_moves_with_its_content()
    {
        static HomeSectionView View(string uri, params HomeCard[] cards)
            => new(Table.None, uri, "Title " + uri, null, cards, cards.Length, cards.Length);

        IReadOnlyList<HomeSectionView> sections =
            [View("spotify:section:a", HomeCard.Blank(index: 1)), View("spotify:section:b", HomeCard.Blank(index: 2))];
        string key = HomeModuleLayout.SectionSetKey(sections);

        Assert.StartsWith("home-section-set:", key, StringComparison.Ordinal);
        Assert.Same(key, HomeModuleLayout.SectionSetKey(sections));
        Assert.Equal(key, HomeModuleLayout.SectionSetKey(sections.ToArray()));
        Assert.NotEqual(key, HomeModuleLayout.SectionSetKey(
            [View("spotify:section:a", HomeCard.Blank(index: 1)), View("spotify:section:b", HomeCard.Blank(index: 3))]));
        Assert.NotEqual(key, HomeModuleLayout.SectionSetKey([View("spotify:section:a", HomeCard.Blank(index: 1))]));
    }
}
