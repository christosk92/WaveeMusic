// ── Wavee.Tests/Theming/ZuneDiscoveryRulesTests.cs — Zune's Home, Browse and Search rules (P5 Z5) ──────────────────────────────────────────
//
// The pure rules of `Themes\Zune\Structure\Rules\Zune{Home,Browse,Search}Rules.cs` against the boards (Z-Home, Z-Home-Podcasts, Z-Browse,
// Z-Search) and the plan (decision 2: pages cap at 2400; the frame's gutter is every page's), the page plan (`ZuneHomePlan.Rows`: the quickplay
// strip takes the Recents zone, the pairs share a row), the new contract key, and the core host's row 2 (`HomeHost.ChromeRow`: the facet words as
// views, the lit one current, the Following toggle, "all" alone before the words land).

using FluentGpu.Scroll.Runtime;
using Wavee.HomeUi;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZuneDiscoveryRulesTests
{
    static readonly WidthTier[] s_tiers = [WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K];

    /// <summary>The narrowest window of each tier (the content is the window less the gutters: Zune has no nav pane).</summary>
    static float NarrowestOf(WidthTier tier) => tier switch
    {
        WidthTier.Compact => 300f,
        WidthTier.Small => ZuneTiers.SmallFrom - ZuneTiers.Hold,
        WidthTier.Standard => ZuneTiers.StandardFrom - ZuneTiers.Hold,
        WidthTier.Huge => ZuneTiers.HugeFrom - ZuneTiers.Hold,
        _ => ZuneTiers.Huge4KFrom - ZuneTiers.Hold,
    };

    // ── the contract ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_quickplay_key_is_published()
        => Assert.Equal("Home.Quickplay", ViewKeys.HomeQuickplay.Id);

    // ── the plan ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void All_leads_with_the_quickplay_strip_which_takes_the_recents_and_the_pairs_share_a_row()
    {
        ZoneKind[] all =
        [
            ZoneKind.Daylist, ZoneKind.RecentGrid, ZoneKind.CoverShelf, ZoneKind.WideTiles, ZoneKind.ReleaseList, ZoneKind.ClusterCards,
            ZoneKind.MixedCovers, ZoneKind.RadioShelf, ZoneKind.BrowseTiles,
        ];
        var rows = ZuneHomePlan.Rows(all, quickplay: true);
        Assert.Equal(new[]
        {
            new ZuneHomeRow(ZuneHomeRowKind.Quickplay, 1, 4),
            new ZuneHomeRow(ZuneHomeRowKind.Single, 0),
            new ZuneHomeRow(ZuneHomeRowKind.Single, 2),
            new ZuneHomeRow(ZuneHomeRowKind.Single, 3),
            new ZuneHomeRow(ZuneHomeRowKind.Pair, 4, 5),
            new ZuneHomeRow(ZuneHomeRowKind.Single, 6),
            new ZuneHomeRow(ZuneHomeRowKind.Single, 7),
            new ZuneHomeRow(ZuneHomeRowKind.Single, 8),
        }, rows.ToArray());
        // Every zone but the Recents stands exactly once in the column (the strip's history is the Recents).
        var placed = new List<int>();
        foreach (var row in rows)
        {
            if (row.Kind == ZuneHomeRowKind.Quickplay) continue;
            placed.Add(row.First);
            if (row.Kind == ZuneHomeRowKind.Pair) placed.Add(row.Second);
        }
        Assert.Equal(new[] { 0, 2, 3, 4, 5, 6, 7, 8 }, placed.Order().ToArray());
    }

    [Fact]
    public void A_facet_without_quickplay_keeps_its_recents_and_the_podcast_episodes_pair()
    {
        Assert.Equal(new[] { new ZuneHomeRow(ZuneHomeRowKind.Single, 0), new ZuneHomeRow(ZuneHomeRowKind.Single, 1) },
            ZuneHomePlan.Rows([ZoneKind.RecentGrid, ZoneKind.CoverShelf], quickplay: false).ToArray());
        Assert.Equal(new[] { new ZuneHomeRow(ZuneHomeRowKind.Pair, 0, 1), new ZuneHomeRow(ZuneHomeRowKind.Single, 2) },
            ZuneHomePlan.Rows([ZoneKind.EpisodeLead, ZoneKind.ContinueEpisodes, ZoneKind.ShowGrid], quickplay: false).ToArray());
        // A strip with no Recents and no release list still leads All (pins alone).
        Assert.Equal(new ZuneHomeRow(ZuneHomeRowKind.Quickplay, -1, -1), ZuneHomePlan.Rows([ZoneKind.CoverShelf], quickplay: true)[0]);
        Assert.True(ZuneHomePlan.HasQuickplay(""));
        Assert.False(ZuneHomePlan.HasQuickplay("podcasts-chip"));
        Assert.False(ZuneHomePlan.Pairs(ZoneKind.CoverShelf, ZoneKind.ClusterCards));
    }

    // ── the records ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_standard_home_record_is_the_board()
    {
        var home = ZuneHomeTiers.For(WidthTier.Standard);
        Assert.Equal(48f, home.Gutter);
        Assert.Equal(28f, home.ZoneGap);
        Assert.Equal(4, home.QuickPins);
        Assert.Equal(132f, home.PinTileW);
        Assert.Equal(3, home.QuickTiles);
        Assert.Equal(100f, home.QuickTileW);
        Assert.Equal(ZuneDaylistForm.Strip, home.DaylistForm);
        Assert.Equal(176f, home.DaylistH);
        Assert.Equal(320f, home.DaylistArtW);
        Assert.Equal(260f, home.ClockW);
        Assert.True(home.PairBeside);
        Assert.Equal(4, home.WideCols);
        Assert.Equal(4, home.TileCols);
        Assert.True(home.ChartsBeside);
        Assert.Equal(154f, ZuneHomeRules.ShelfCardW);
        Assert.Equal(64f, ZuneHomeRules.ReleaseRowH);
        // Z-Browse and Z-Search.
        var browse = ZuneBrowseTiers.For(WidthTier.Standard);
        Assert.True(browse.ChartsBeside);
        Assert.Equal(420f, browse.ChartsW);
        Assert.Equal(3, browse.ForYouCols);
        Assert.Equal(6, browse.LinkCols);
        Assert.Equal(6, browse.MoodCols);
        Assert.Equal(200f, browse.BannerH);
        var search = ZuneSearchTiers.For(WidthTier.Standard);
        Assert.True(search.TopBeside);
        Assert.Equal(440f, search.TopW);
        Assert.Equal(2, search.BestCols);
        Assert.Equal(180f, search.HeroArt);
    }

    [Fact]
    public void Every_record_is_its_own_tiers_and_stands_on_the_frames_gutter_and_four_k_is_huges()
    {
        foreach (var tier in s_tiers)
        {
            float gutter = ZuneTiers.Frame(tier).Gutter;
            Assert.Equal(tier, ZuneHomeTiers.For(tier).Tier);
            Assert.Equal(tier, ZuneBrowseTiers.For(tier).Tier);
            Assert.Equal(tier, ZuneSearchTiers.For(tier).Tier);
            Assert.Equal(gutter, ZuneHomeTiers.For(tier).Gutter);
            Assert.Equal(gutter, ZuneBrowseTiers.For(tier).Gutter);
            Assert.Equal(gutter, ZuneSearchTiers.For(tier).Gutter);
        }
        // Plan "Zune specifics": Home at 4K is built from the Huge rules (the page caps at 2400, decision 2).
        Assert.Equal(ZuneHomeTiers.For(WidthTier.Huge) with { Tier = WidthTier.Huge4K, Gutter = 0f },
                     ZuneHomeTiers.For(WidthTier.Huge4K) with { Gutter = 0f });
        // A frame that resolved no tiers reads Standard's.
        Assert.Equal(WidthTier.Standard, ZuneHomeTiers.Of(TierFacts.Unknown).Tier);
    }

    [Fact]
    public void Every_tier_fits_its_narrowest_window()
    {
        foreach (var tier in s_tiers)
        {
            var home = ZuneHomeTiers.For(tier);
            float inner = NarrowestOf(tier) - 2f * home.Gutter;
            // The quickplay groups wrap, but each fits on its own.
            float pins = home.QuickPins * home.PinTileW + (home.QuickPins - 1) * ZuneHomeRules.QuickTileGap;
            float tiles = home.QuickTiles * home.QuickTileW + (home.QuickTiles - 1) * ZuneHomeRules.QuickTileGap;
            Assert.True(pins <= inner, $"{tier}: the pins ({pins}) overflow {inner}");
            Assert.True(tiles <= inner, $"{tier}: the history ({tiles}) overflows {inner}");
            // The daylist strip leaves its copy room beside the art and the clock.
            if (home.DaylistForm == ZuneDaylistForm.Strip)
                Assert.True(home.DaylistArtW + home.ClockW + 2f * ZuneHomeRules.DaylistCopyPad <= inner, $"{tier}: the daylist strip overflows {inner}");
            else Assert.Equal(0f, home.DaylistArtW);

            var browse = ZuneBrowseTiers.For(tier);
            if (browse.ChartsBeside)
                Assert.True(browse.ChartsW + ZuneBrowseRules.PairGap + browse.ForYouCols * ZuneBrowseRules.ListMinCol <= inner,
                    $"{tier}: charts | for you overflows {inner}");
            var search = ZuneSearchTiers.For(tier);
            if (search.TopBeside)
                Assert.True(search.TopW + ZuneSearchRules.TopGap + ZuneSearchRules.BestMinCol <= inner, $"{tier}: top | best overflows {inner}");
            Assert.True(search.HeroArt + ZuneSearchRules.HeroGap <= inner, $"{tier}: the top result's art overflows {inner}");
        }
    }

    [Fact]
    public void The_narrow_tiers_stack_what_the_wide_tiers_set_side_by_side()
    {
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small })
        {
            Assert.False(ZuneHomeTiers.For(tier).PairBeside);
            Assert.False(ZuneHomeTiers.For(tier).ChartsBeside);
            Assert.True(ZuneHomeTiers.For(tier).DaylistStacked);
            Assert.False(ZuneBrowseTiers.For(tier).ChartsBeside);
            Assert.False(ZuneSearchTiers.For(tier).TopBeside);
        }
        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            Assert.True(ZuneHomeTiers.For(tier).PairBeside);
            Assert.False(ZuneHomeTiers.For(tier).DaylistStacked);
        }
    }
}

/// <summary>The core host's row 2 (P5 Z5): what `HomeHost` publishes for a band-style structure.</summary>
[Collection(EntitiesCollection.Name)]
public sealed class HomeChromeRowTests
{
    public HomeChromeRowTests() => TestScope.Fresh();

    static readonly ChipInput Music = new("music-chip", "Music", "music-following-chip", "Following");
    static readonly ChipInput Podcasts = new("podcasts-chip", "Podcasts", null, null);

    static ScreenModel Model(string facet, ChipInput[] chips)
        => new(facet, [new Zone(ZoneKind.CoverShelf, "z0", null, null, Array.Empty<HomeCard>())], "", chips);

    [Fact]
    public void Before_the_words_land_the_row_is_all_alone_and_lit()
    {
        var host = new HomeHost(new ScrollHandle());
        host.SyncFacetIndex();
        var row = host.ChromeRow!;
        Assert.Single(row.Views);
        Assert.Equal(HomeHost.AllViewId, row.Views[0].Id);
        Assert.True(row.Views[0].Current);
        Assert.Empty(row.Actions);
    }

    [Fact]
    public void The_words_are_the_views_with_the_target_lit_and_a_following_toggle_on_a_word_that_has_one()
    {
        var host = new HomeHost(new ScrollHandle());
        host.RecordShown(Model("", [Music, Podcasts]));

        host.Switch.Value = FacetSwitchState.Initial("podcasts-chip");
        host.SyncFacetIndex();
        var row = host.ChromeRow!;
        Assert.Equal(new[] { HomeHost.AllViewId, "music-chip", "podcasts-chip" }, row.Views.Select(v => v.Id).ToArray());
        Assert.Equal("podcasts-chip", row.Views.Single(v => v.Current).Id);
        Assert.Empty(row.Actions);                                               // Podcasts has no Following sub-chip

        host.Switch.Value = FacetSwitchState.Initial("music-following-chip");     // a sub-chip lights its word
        host.SyncFacetIndex();
        row = host.ChromeRow!;
        Assert.Equal("music-chip", row.Views.Single(v => v.Current).Id);
        Assert.Equal(HomeHost.FollowingActionId, Assert.Single(row.Actions).Id);
    }

    [Fact]
    public void An_unchanged_row_is_the_same_instance_and_its_verbs_are_stable()
    {
        var host = new HomeHost(new ScrollHandle());
        host.RecordShown(Model("", [Music, Podcasts]));
        host.SyncFacetIndex();
        var first = host.ChromeRow;
        host.SyncFacetIndex();
        Assert.Same(first, host.ChromeRow);

        host.Switch.Value = FacetSwitchState.Initial("music-chip");
        host.SyncFacetIndex();
        Assert.NotSame(first, host.ChromeRow);
        Assert.Same(first!.Views[2].Go, host.ChromeRow!.Views[2].Go);            // one Go per word id
    }
}
