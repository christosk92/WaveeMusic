// ── Wavee.Tests/Theming/ZuneDiscoveryRulesTests.cs — Zune's Home, Browse and Search rules (P5 Z5) ──────────────────────────────────────────
//
// The pure rules of `Themes\Zune\Structure\Rules\Zune{Home,Browse,Search}Rules.cs` against the boards (Z-Home, Z-Home-Podcasts, Z-Browse,
// Z-Search) and the plan (decision 2: pages cap at 2400; the frame's gutter is every page's), the page plan (`ZuneHomePlan.Rows`: the quickplay
// strip takes the Recents zone, the pairs share a row), the new contract key, and the core host's row 2 (`HomeHost.ChromeRow`: the facet words as
// views, the lit one current, the Following toggle, "all" alone before the words land).

using FluentGpu.Foundation;
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
        // ... except the cover shelf: 4K holds fixed 220 cards ("min 220") where Huge fits eight across.
        Assert.Equal(ZuneHomeTiers.For(WidthTier.Huge) with { Tier = WidthTier.Huge4K, Gutter = 0f, ShelfCols = 0, ShelfCardW = 220f },
                     ZuneHomeTiers.For(WidthTier.Huge4K) with { Gutter = 0f });
        Assert.Equal(220f, ZuneHomeTiers.For(WidthTier.Huge4K).ShelfCardW);
        Assert.Equal(0, ZuneHomeTiers.For(WidthTier.Huge4K).ShelfCols);
        Assert.Equal(ZuneHomeRules.ShelfCardW, ZuneHomeTiers.For(WidthTier.Standard).ShelfCardW);
        Assert.Equal(8, ZuneHomeTiers.For(WidthTier.Huge).ShelfCols);
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
            // The quickplay groups wrap, and a group's tiles wrap inside it: each TILE fits on its own; where the groups stand in a row (not Compact) each
            // group fits on its own too.
            float pins = home.QuickPins * home.PinTileW + (home.QuickPins - 1) * home.QuickTileGap;
            float tiles = home.QuickTiles * home.QuickTileW + (home.QuickTiles - 1) * home.QuickTileGap;
            Assert.True(home.PinTileW <= inner && home.QuickTileW <= inner, $"{tier}: a quickplay tile overflows {inner}");
            if (!home.QuickStacked)
            {
                Assert.True(pins <= inner, $"{tier}: the pins ({pins}) overflow {inner}");
                if (!home.HistoryRows) Assert.True(tiles <= inner, $"{tier}: the history ({tiles}) overflows {inner}");
            }
            // The daylist leaves its copy room beside the art, the clock and the play ring.
            if (home.DaylistForm == ZuneDaylistForm.Strip)
                Assert.True(home.DaylistArtW + home.ClockW + 2f * home.DaylistPad <= inner, $"{tier}: the daylist strip overflows {inner}");
            else if (home.DaylistBleed)
            {
                // The bled strip: the art is the ground, so the copy has the strip's whole width; the countdown keeps room beside the three foot rings.
                Assert.Equal(0f, home.ClockW);
                // (at the narrowest window the countdown keeps 80 beside the rings, and wraps there rather than being cut)
                Assert.True(home.DaylistTailRingsW + ZuneHomeRules.DaylistBleedRowGap + 2f * home.DaylistPad + 80f <= inner,
                    $"{tier}: the daylist's foot rings leave the countdown under 80 of {inner}");
                if (tier == WidthTier.Compact) Assert.True(MathF.Max(home.RingSize, home.RingTarget) >= 40f, $"{tier}: the daylist rings are under a 40 target");
            }
            else
            {
                Assert.Equal(0f, home.ClockW);
                Assert.True(home.DaylistArtW + home.RingSize + 2f * home.DaylistPad <= inner, $"{tier}: the daylist row overflows {inner}");
            // A Compact ring is a 36 disc inside a 40 hit box (the brief's Compact target rule).
            if (tier == WidthTier.Compact) Assert.True(MathF.Max(home.RingSize, home.RingTarget) >= 40f, $"{tier}: the daylist rings are under a 40 target");
            }

            var browse = ZuneBrowseTiers.For(tier);
            if (browse.ChartsBeside)
                Assert.True(browse.ChartsW + browse.PairGap + browse.ForYouCols * ZuneBrowseRules.ListMinCol <= inner,
                    $"{tier}: charts | for you overflows {inner}");
            var search = ZuneSearchTiers.For(tier);
            if (search.TopBeside)
                Assert.True(search.TopW + search.TopGap + search.BestMinCol <= inner, $"{tier}: top | best overflows {inner}");
            Assert.True(search.HeroArt + ZuneSearchRules.HeroGap <= inner, $"{tier}: the top result's art overflows {inner}");
        }
    }

    [Fact]
    public void Compact_stacks_everything_and_small_sets_the_boards_pairs_side_by_side()
    {
        // Compact (Z-Frame-Compact, Z-Home-Compact, Z-Browse-Compact, Z-Search-Compact): one column; four 80 pins, the history as rows, no new group; the
        // daylist BLED (the owner's i8 option C): the art is the strip's ground and the copy runs its full width over it.
        var c = ZuneHomeTiers.For(WidthTier.Compact);
        Assert.True(c.QuickStacked);
        Assert.True(c.HistoryRows);
        Assert.Equal(4, c.QuickPins);
        Assert.Equal(80f, c.PinTileW);
        Assert.Equal(0, c.QuickFresh);
        Assert.Equal(ZuneDaylistForm.Bleed, c.DaylistForm);
        Assert.True(c.DaylistBleed);
        Assert.True(c.DaylistStacked);
        // i8 option C at 400: four UNLABELLED 36 rings in 40 hit boxes 10 apart (play at the eyebrow row's end, shuffle, save and "…" ending the countdown's
        // row), the title at its full 40/48 step (the Title role, as at Standard: Zune is a large typeface, so the title is not shrunk to fit) across the
        // strip's whole width on as many lines as the name needs (no cap, no ellipsis); the strip is a 160 floor that grows with its copy.
        Assert.Equal(160f, c.DaylistH);
        Assert.True(c.DaylistGrows);
        Assert.Equal(0f, c.DaylistArtAspect);
        Assert.True(c.ClockInline);
        Assert.Equal(0f, c.ClockW);
        Assert.False(c.PlayAtEnd);
        Assert.Equal(4, c.DaylistRings);
        Assert.False(c.RingLabels);
        Assert.Equal(36f, c.RingSize);
        Assert.Equal(40f, c.RingTarget);
        Assert.Equal(14f, c.RingTarget - c.RingSize + c.RingsGap);   // the visible gap between discs
        Assert.Equal(ZuneDaylistTitle.Title, c.DaylistTitle);
        Assert.Equal(0, c.DaylistTitleLines);
        // The foot's three rings and the countdown's room share the copy's row at 400.
        Assert.Equal(3f * 40f + 2f * 10f, c.DaylistTailRingsW);
        Assert.True(c.DaylistTailRingsW + ZuneHomeRules.DaylistBleedRowGap + 120f <= 400f - 2f * c.Gutter - 2f * c.DaylistPad);
        Assert.False(c.PairBeside);
        Assert.False(c.ChartsBeside);
        Assert.Equal(2, c.ShelfCols);
        Assert.Equal(56f, c.ReleaseRowH);
        Assert.False(ZuneBrowseTiers.For(WidthTier.Compact).ChartsBeside);
        Assert.Equal(1, ZuneBrowseTiers.For(WidthTier.Compact).ForYouCols);
        Assert.Equal(56f, ZuneBrowseTiers.For(WidthTier.Compact).ForYouRowH);
        Assert.False(ZuneSearchTiers.For(WidthTier.Compact).TopBeside);
        Assert.Equal(ZuneSearchHeroName.Above, ZuneSearchTiers.For(WidthTier.Compact).HeroName);
        Assert.Equal(40f, ZuneSearchTiers.For(WidthTier.Compact).RingSize);

        // Small (Z-Frame-Small, Z-Home-Small, Z-Browse-Small, Z-Search-Small): pins beside history (three 88 tiles, the name alone), the 112 row (140 art,
        // play and shuffle with words), the release list beside "because you like" 32 apart, the charts beside For you, the top result beside best matches.
        var s = ZuneHomeTiers.For(WidthTier.Small);
        Assert.False(s.QuickStacked);
        Assert.Equal(3, s.QuickPins);
        Assert.Equal(88f, s.PinTileW);
        Assert.Equal(88f, s.QuickTileW);
        Assert.False(s.QuickSubs);
        Assert.Equal(0, s.QuickFresh);
        Assert.Equal(ZuneDaylistForm.Row, s.DaylistForm);
        Assert.Equal(140f, s.DaylistArtW);
        Assert.Equal(2, s.DaylistRings);
        Assert.True(s.RingLabels);
        Assert.False(s.PlayAtEnd);
        Assert.True(s.PairBeside);
        Assert.Equal(32f, s.PairGap);
        Assert.False(s.ChartsBeside);
        Assert.Equal(5, s.ShelfCols);
        Assert.True(ZuneBrowseTiers.For(WidthTier.Small).ChartsBeside);
        Assert.Equal(280f, ZuneBrowseTiers.For(WidthTier.Small).ChartsW);
        Assert.Equal(120f, ZuneBrowseTiers.For(WidthTier.Small).BannerH);
        Assert.True(ZuneSearchTiers.For(WidthTier.Small).TopBeside);
        Assert.Equal(280f, ZuneSearchTiers.For(WidthTier.Small).TopW);
        Assert.Equal(ZuneSearchHeroName.Below, ZuneSearchTiers.For(WidthTier.Small).HeroName);

        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            Assert.True(ZuneHomeTiers.For(tier).PairBeside);
            Assert.Equal(ZuneDaylistForm.Strip, ZuneHomeTiers.For(tier).DaylistForm);
            Assert.False(ZuneHomeTiers.For(tier).DaylistStacked);
        }
    }

    [Fact]
    public void The_huge_records_are_the_boards()
    {
        var h = ZuneHomeTiers.For(WidthTier.Huge);
        Assert.Equal(36f, h.ZoneGap);
        Assert.Equal(6, h.QuickPins);
        Assert.Equal(180f, h.PinTileW);
        Assert.Equal(4, h.QuickTiles);
        Assert.Equal(140f, h.QuickTileW);
        Assert.Equal(3, h.QuickFresh);
        Assert.Equal(48f, h.QuickGroupGap);
        Assert.Equal(220f, h.DaylistH);
        Assert.Equal(440f, h.DaylistArtW);
        Assert.Equal(320f, h.ClockW);
        Assert.Equal(ZuneDaylistTitle.Hero, h.DaylistTitle);
        Assert.Equal(8, h.ShelfCols);
        Assert.Equal(6, h.WideCols);
        Assert.Equal(3, h.ClusterCols);
        Assert.Equal(8, h.TileCols);
        var b = ZuneBrowseTiers.For(WidthTier.Huge);
        Assert.Equal(640f, b.ChartsW);
        Assert.Equal(216f, b.BigPlateH);
        Assert.True(b.ForYouPlates);
        Assert.Equal(8, b.LinkCols);
        Assert.Equal(260f, b.BannerH);
        var q = ZuneSearchTiers.For(WidthTier.Huge);
        Assert.Equal(560f, q.TopW);                      // wide enough for the 64/72 name (it truncated in 480)
        Assert.Equal(240f, q.HeroArt);
        Assert.True(q.HeroStacked);
        Assert.Equal(3, q.BestCols);
    }

    [Fact]
    public void The_bled_daylists_scrim_keeps_the_ink_legible_on_the_worst_art()
    {
        // The scrim is the theme's ground over the art, thinnest at its trailing edge. On the worst cover there is (pure white under the dark theme's
        // ground, pure black under the light theme's) the primary ink still reads at 4.5 : 1 (body text: the eyebrow and the countdown) everywhere,
        // so the 40/48 title (3 : 1) and the rings' strokes do too.
        Assert.True(ZuneHomeRules.DaylistScrimLead >= ZuneHomeRules.DaylistScrimTail);
        foreach (var (ground, worstArt, ink) in new[]
                 {
                     (ZuneTokens.DarkGround, ColorF.FromRgba(255, 255, 255), ZuneTokens.DarkInk),
                     (ZuneTokens.LightGround, ColorF.FromRgba(0, 0, 0), ZuneTokens.LightInk),
                 })
        {
            foreach (float a in new[] { ZuneHomeRules.DaylistScrimLead, ZuneHomeRules.DaylistScrimTail })
            {
                var under = ColorF.Lerp(worstArt, ground with { A = 1f }, a);
                Assert.True(ColorContrast.Ratio(ink, under) >= 4.5f, $"the ink on the scrim at {a} over {worstArt} reads at {ColorContrast.Ratio(ink, under)}");
            }
        }
    }

    [Fact]
    public void The_daylist_title_keeps_its_role_at_every_tier_and_wraps_while_the_strip_grows()
    {
        // Zune is a large typeface (owner directive): the title is never set smaller to fit and never ellipsised on one line while it could wrap. Compact
        // sets it in the same 40/48 role as Standard; every strip tier wraps it over two lines or more and its height is a floor that grows.
        Assert.Equal(ZuneDaylistTitle.Title, ZuneHomeTiers.For(WidthTier.Compact).DaylistTitle);
        Assert.Equal(ZuneDaylistTitle.Heading, ZuneHomeTiers.For(WidthTier.Small).DaylistTitle);
        Assert.Equal(ZuneDaylistTitle.Title, ZuneHomeTiers.For(WidthTier.Standard).DaylistTitle);
        Assert.Equal(ZuneDaylistTitle.Hero, ZuneHomeTiers.For(WidthTier.Huge).DaylistTitle);
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var lay = ZuneHomeTiers.For(tier);
            Assert.True(lay.DaylistTitleLines == 0 || lay.DaylistTitleLines >= 2, tier + ": the title wraps instead of truncating");
            Assert.True(lay.DaylistGrows, tier + ": the strip grows to hug a wrapped title");
        }
        // Only the one-row form (a window too short for the strip) keeps one line.
        var oneRow = ZuneHomeTiers.Of(WindowTiers.Resolve(ZuneTiers.Policy, 1440f, 330f, 1440f));
        Assert.Equal(1, oneRow.DaylistTitleLines);
    }

    [Fact]
    public void A_window_too_short_for_the_strip_folds_the_daylist_to_one_row_and_keeps_every_other_tier_fact()
    {
        var policy = ZuneTiers.Policy;
        Assert.Contains(new NamedRung(ZuneHomeRules.DaylistRung, new TierEdge(ZuneHomeRules.DaylistFullFrom, NarrowHold: ZuneTiers.Hold, WidenHold: 0f), RungAxis.Height),
            policy.Rungs);
        // Z-Shell-Short: 330 tall folds it at any width; the Small board's 600 does not (it keeps its 112 row), nor does a regular height.
        var short330 = ZuneHomeTiers.Of(WindowTiers.Resolve(policy, 800f, 330f, 800f));
        Assert.Equal(ZuneDaylistForm.OneRow, short330.DaylistForm);
        Assert.Equal(56f, short330.DaylistH);
        Assert.False(short330.DaylistGrows);
        Assert.Equal(1, short330.DaylistTitleLines);
        Assert.Equal(56f, short330.DaylistArtW);
        Assert.Equal(0f, short330.ClockW);
        Assert.True(short330.PlayAtEnd);
        Assert.False(short330.DaylistMeta);
        Assert.Equal(ZuneHomeTiers.For(WidthTier.Small).PinTileW, short330.PinTileW);
        Assert.Equal(ZuneDaylistForm.Row, ZuneHomeTiers.Of(WindowTiers.Resolve(policy, 800f, 600f, 800f)).DaylistForm);
        Assert.Equal(ZuneDaylistForm.Strip, ZuneHomeTiers.Of(WindowTiers.Resolve(policy, 1440f, 960f, 1440f)).DaylistForm);
        Assert.Equal(ZuneDaylistForm.OneRow, ZuneHomeTiers.Of(WindowTiers.Resolve(policy, 1440f, 330f, 1440f)).DaylistForm);
        // The edge commits at 480 on widening and holds 24 on shrinking (456).
        Assert.Equal(ZuneDaylistForm.OneRow, ZuneHomeTiers.Of(WindowTiers.Resolve(policy, 1440f, 479f, 1440f)).DaylistForm);
        Assert.Equal(ZuneDaylistForm.Strip, ZuneHomeTiers.Of(WindowTiers.Resolve(policy, 1440f, 480f, 1440f)).DaylistForm);
        var from960 = WindowTiers.Resolve(policy, 1440f, 960f, 1440f);
        Assert.Equal(ZuneDaylistForm.Strip, ZuneHomeTiers.Of(WindowTiers.Resolve(policy, 1440f, 456f, 1440f, previous: from960)).DaylistForm);
        Assert.Equal(ZuneDaylistForm.OneRow, ZuneHomeTiers.Of(WindowTiers.Resolve(policy, 1440f, 455f, 1440f, previous: from960)).DaylistForm);
        // A view mounted with no policy reads Standard's strip.
        Assert.Equal(ZuneDaylistForm.Strip, ZuneHomeTiers.Of(TierFacts.Unknown).DaylistForm);
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
