// ── Wavee.Tests/ClassicDiscoveryTierTests.cs — Classic's Home and Search records per content tier (themes plan 3.5, 3.6; TC4) ───────────────
//
// Pure rules, no host: the records the discovery pages read (`ClassicHomeTiers`, `ClassicSearchTiers`; the Browse ones are pinned in BrowseLayoutTests)
// carry the numbers of the boards — C-Home-Compact / -Small / -Huge, C-Search-Compact / -Small / -Huge, C-Error-Compact, C-Shell-Short — and Standard is the
// tuned design unchanged. Asserted on the records, never on pixels.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Xunit;

namespace Wavee.Tests;

public class ClassicDiscoveryTierTests
{
    static ClassicHomeLayout Home(WidthTier tier, bool shortWindow = false) => ClassicHomeTiers.For(tier, shortWindow);

    // ── Home ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_StacksTheDaylist_AndFollowsTheBoard()
    {
        var l = Home(WidthTier.Compact);
        Assert.Equal(ClassicDaylistForm.Stacked, l.Daylist.Form);
        Assert.Equal(140f, l.Daylist.ArtH);                       // C-Home-Compact: 140 art
        Assert.Equal(ControlSize.Large, l.Daylist.Buttons);       // 40 high actions
        Assert.Equal(40f, l.Daylist.IconBox);
        Assert.Equal(ClassicClockForm.Bars, l.Daylist.Clock);
        Assert.Equal(12f, l.PageTop);
        Assert.True(l.CompactFacets);
        // The strip: 148 of cover in a 164 cell, no pager, the lead span off; the tiles are 2 x 56 with 40 art.
        Assert.Equal(164f, l.Shelf.FixedCardW);
        Assert.False(l.Shelf.Pager);
        Assert.False(l.ShelfLead);
        Assert.Equal(2, l.Recents.MaxCols);
        Assert.Equal((40f, 56f), (l.RecentsArt, l.RecentsRowH));
        Assert.Equal(1, l.Releases.MaxCols);
        // C-Error-Compact: the failure bar is 72 tall, its Retry 40.
        Assert.Equal(72f, l.FailureBarH);
        Assert.Equal(ControlSize.Large, l.FailureRetry);
        Assert.True(float.IsNaN(l.PageMaxW));
    }

    [Fact]
    public void Small_IsA216Banner_With364OfCopy()
    {
        var l = Home(WidthTier.Small);
        Assert.Equal(ClassicDaylistForm.Banner, l.Daylist.Form);
        Assert.Equal((216f, 364f), (l.Daylist.CardH, l.Daylist.CopyW));
        Assert.Equal(ClassicClockForm.Line, l.Daylist.Clock);
        Assert.Equal(3, l.Recents.MaxCols);                       // 3 x 2 tiles
        Assert.Equal(16f, l.PageTop);
        Assert.Equal(0f, l.FailureBarH);
    }

    [Fact]
    public void Standard_IsTheTunedDesign()
    {
        var l = Home(WidthTier.Standard);
        Assert.Equal(ClassicDaylistForm.Card, l.Daylist.Form);
        Assert.Equal(R.Space.PageMaxW, l.PageMaxW);
        Assert.Equal(R.Space.SectionGapWide, l.ZoneGap);
        Assert.Equal(Spacing.XXL, l.PageTop);
        Assert.Equal(Spacing.XL, l.HeadGap);
        Assert.Equal((ClassicHomeRules.RecentsMinCol, ClassicHomeRules.RecentsMaxCols), (l.Recents.MinCol, l.Recents.MaxCols));
        Assert.Equal((R.Space.ShelfCardMin, R.Space.ShelfCardMax, 0), (l.Shelf.MinCardW, l.Shelf.MaxCardW, l.Shelf.MaxColumns));
        Assert.Equal(ClassicClockForm.Full, l.Daylist.Clock);
        Assert.Equal(ClassicDaylistTitle.Adaptive, l.Daylist.Title);
        Assert.False(l.DaylistBeside);
        Assert.Equal(ClassicHomeRules.SectionCardMinWidth, l.SectionMinCell);
    }

    [Theory]
    [InlineData(WidthTier.Huge)]
    [InlineData(WidthTier.Huge4K)]
    public void Huge_PutsTheHeroBesideTheTiles_AndCapsAt2400(WidthTier tier)
    {
        var l = Home(tier);
        Assert.Equal(WidthTier.Huge, l.Tier);                     // Classic draws no 4K band: Huge's record
        Assert.True(l.DaylistBeside);
        Assert.Equal(ClassicDaylistForm.Hero, l.Daylist.Form);
        Assert.Equal((400f, 760f), (l.Daylist.CardH, l.Daylist.CopyW));
        Assert.Equal(ClassicDaylistTitle.Display68, l.Daylist.Title);
        Assert.Equal(ClassicClockForm.Boxed, l.Daylist.Clock);
        Assert.Equal(2400f, l.PageMaxW);
        Assert.Equal(10, l.Shelf.MaxColumns);                     // 10-card shelves
        Assert.Equal(5, l.WideShelf.MaxColumns);                  // Featured: 5 across
        Assert.Equal(3, l.Releases.MaxCols);
        Assert.Equal(216f, l.SectionMinCell);
    }

    [Theory]
    [InlineData(2, 4)]      // 2 columns x 4 rows beside the hero: 7 played + the history tile
    [InlineData(3, 2)]
    [InlineData(1, 8)]
    public void Huge_Recents_FillTheHeroBand(int cols, int rows)
    {
        var l = Home(WidthTier.Huge);
        Assert.Equal(rows, l.RecentsRows(cols));
        Assert.Equal(Math.Min(rows * cols - 1, 7), l.RecentsCells(cols, 7));
    }

    [Theory]
    [InlineData(WidthTier.Compact, 2, 3)]
    [InlineData(WidthTier.Small, 3, 5)]
    [InlineData(WidthTier.Standard, 4, 7)]     // two flush rows less the history tile, as before tiers
    public void Recents_AreTwoFlushRows_LessTheHistoryTile(WidthTier tier, int cols, int played)
    {
        var l = Home(tier);
        Assert.Equal(2, l.RecentsRows(cols));
        Assert.Equal(played, l.RecentsCells(cols, 7));
    }

    [Theory]
    [InlineData(WidthTier.Compact)]
    [InlineData(WidthTier.Small)]
    [InlineData(WidthTier.Standard)]
    [InlineData(WidthTier.Huge)]
    public void Short_CapsTheHeroAt60PercentOfTheFloorPage(WidthTier tier)
    {
        var l = ClassicHomeTiers.For(tier, short_: true, strip: true);
        Assert.True(l.Short);
        Assert.Equal(ClassicDaylistForm.Strip, l.Daylist.Form);
        Assert.Equal(126f, l.Daylist.CardH, 0.01f);               // C-Shell-Short: 60 % of the 210 page
        Assert.Equal(ClassicTiers.ShortFloorH * 0.6f, l.Daylist.CardH, 0.01f);
        Assert.False(l.Daylist.Details);                          // the eyebrow and the title only
        Assert.Equal(ClassicClockForm.None, l.Daylist.Clock);
        Assert.Equal(16f, l.PageTop);
        Assert.False(l.DaylistBeside);
    }

    [Theory]
    [InlineData(WidthTier.Compact, ClassicDaylistForm.Stacked)]
    [InlineData(WidthTier.Small, ClassicDaylistForm.Banner)]      // C-Home-Small is drawn at 800 x 600, a Short window: its 216 banner fits the 60 % cap (288)
    [InlineData(WidthTier.Standard, ClassicDaylistForm.Card)]
    [InlineData(WidthTier.Huge, ClassicDaylistForm.Banner)]       // the 400 hero would not fit the cap: Small's banner, beside nothing
    public void Short_AboveTheHeroRung_KeepsTheHeroAtItsOwnHeight(WidthTier tier, ClassicDaylistForm form)
    {
        var l = ClassicHomeTiers.For(tier, short_: true, strip: false);
        Assert.True(l.Short);
        Assert.Equal(form, l.Daylist.Form);
        Assert.Equal(16f, l.PageTop);
        Assert.False(l.DaylistBeside);
    }

    [Fact]
    public void TheHeroRung_FollowsTheLadder_AndTheStripOnlyBelowIt()
    {
        // 480 tall is where 60 % of the page (window less 120) still holds a 216 banner; the rung widens 40 later (the height ladder's hold).
        Assert.Equal(480f, ClassicTiers.HeroFullFrom);
        Assert.Equal(216f / ClassicHomeTiers.ShortHeroFrac + (330f - ClassicTiers.ShortFloorH), ClassicTiers.HeroFullFrom, 0.5f);
        var fresh = (float h) => WindowTiers.Resolve(ClassicTiers.Policy, 800f, h, 752f);
        Assert.False(fresh(479f).Rung(ClassicTiers.HeroFullRung));
        Assert.True(fresh(480f).Rung(ClassicTiers.HeroFullRung));
        Assert.Equal(ClassicDaylistForm.Strip, ClassicHomeTiers.Of(fresh(330f)).Daylist.Form);
        Assert.Equal(ClassicDaylistForm.Strip, ClassicHomeTiers.Of(fresh(479f)).Daylist.Form);
        Assert.Equal(ClassicDaylistForm.Banner, ClassicHomeTiers.Of(fresh(600f)).Daylist.Form);       // the Small board's 800 x 600
        Assert.Equal(ClassicDaylistForm.Card, ClassicHomeTiers.Of(WindowTiers.Resolve(ClassicTiers.Policy, 1440f, 960f, 1120f)).Daylist.Form);
        // Widening back out of the strip holds 40 (the edge's hold), narrowing enters it at once.
        var from500 = WindowTiers.Resolve(ClassicTiers.Policy, 800f, 500f, 752f, previous: fresh(300f));
        Assert.False(from500.Rung(ClassicTiers.HeroFullRung));
        Assert.True(WindowTiers.Resolve(ClassicTiers.Policy, 800f, 520f, 752f, previous: fresh(300f)).Rung(ClassicTiers.HeroFullRung));
    }

    [Fact]
    public void ClassicGridBand_FloorFitsInsideTheCap()
    {
        var band = new ClassicGridBand(200f, 4);
        Assert.Equal(1, band.Columns(0f, 12f));
        Assert.Equal(3, band.Columns(632f, 12f));                 // the owner's 632 fits three (pinned before tiers)
        Assert.Equal(4, band.Columns(836f, 12f));
        Assert.Equal(4, band.Columns(5000f, 12f));
        Assert.Equal(6, ClassicGridBand.Fixed(6).Columns(0f));    // a fixed count ignores the width
    }

    [Fact]
    public void EveryTier_HasARecord_AndShortCoversEveryTier()
    {
        foreach (WidthTier tier in Enum.GetValues<WidthTier>())
        foreach (bool s in new[] { false, true })
        {
            var l = Home(tier, s);
            Assert.True(l.Tier <= WidthTier.Huge);
            Assert.Equal(s, l.Short);
            Assert.True(l.SectionMinCell > 0f && l.Recents.MaxCols >= 1 && l.Shelf.MinCardW > 0f);
        }
    }

    [Fact]
    public void FirstFrame_IsSeededFromTheTier_NotAFixed1100()
    {
        // A Compact 400 window: no nav in front of the page (the drawer), the 16 gutter either side.
        var compact = WindowTiers.Resolve(ClassicTiers.Policy, 400f, 860f, 400f);
        Assert.Equal(368f, ClassicRegion.EstimateWidth(compact, 400f), 0.5f);
        // A Small 800 window: the 48 rail, the 32 gutter: the content width the facts carry (752) less 64.
        var small = WindowTiers.Resolve(ClassicTiers.Policy, 800f, 600f, 752f);
        Assert.Equal(688f, ClassicRegion.EstimateWidth(small, 800f), 0.5f);
        // Standard 1440 with a 320 pane: 1120 content, 36 gutters.
        var std = WindowTiers.Resolve(ClassicTiers.Policy, 1440f, 960f, 1120f);
        Assert.Equal(1048f, ClassicRegion.EstimateWidth(std, 1440f), 0.5f);
        // Huge: the hero band is 1.35 of 2.35 of the capped page.
        var huge = WindowTiers.Resolve(ClassicTiers.Policy, 2560f, 1440f, 2240f);
        float page = ClassicRegion.EstimateWidth(huge, 2560f);
        Assert.Equal(2240f - 72f, page, 0.5f);
        float hero = ClassicRegion.EstimateHomeBand(huge, 2560f, hero: true), tiles = ClassicRegion.EstimateHomeBand(huge, 2560f, hero: false);
        Assert.Equal(page - ClassicHomeTiers.HeroGap, hero + tiles, 0.5f);
        Assert.True(hero > tiles);
        // A window resized inside its tier since the last crossing falls back to the viewport less the chrome estimate: still the right tier's width.
        float stale = ClassicRegion.EstimateWidth(std, 1200f);
        Assert.Equal(1200f - 320f - 72f, stale, 0.5f);
        // No facts at all (a view mounted alone): the viewport is Standard's.
        Assert.True(ClassicRegion.EstimateWidth(TierFacts.Unknown, 1000f) > 0f);
    }

    // ── the facet row's trailing button and the chapter subtitles (C-Home-Compact, C-Home-Huge) ───────────────────────────────

    [Theory]
    [InlineData(WidthTier.Compact, true, false, false)]   // C-Home-Compact: the 40 Friend activity button, the titles alone
    [InlineData(WidthTier.Small, false, false, true)]     // the tuned row
    [InlineData(WidthTier.Standard, false, false, true)]  // the tuned row
    [InlineData(WidthTier.Huge, false, true, true)]       // C-Home-Huge: "Following" beside "All"
    [InlineData(WidthTier.Huge4K, false, true, true)]
    public void TheFacetRow_AndTheHeaders_FollowTheBoard(WidthTier tier, bool friends, bool followingEverywhere, bool subtitles)
    {
        foreach (bool shortWindow in new[] { false, true })
        {
            var l = Home(tier, shortWindow);
            Assert.Equal(friends, l.FriendsButton);
            Assert.Equal(followingEverywhere, l.FollowingOnEveryWord);
            Assert.Equal(subtitles, l.HeaderSubtitles);
        }
    }

    // ── Search ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    static ClassicSearchLayout SearchOf(WidthTier tier) => ClassicSearchTiers.For(tier, ClassicTiers.Page(tier).Gutter);

    [Fact]
    public void Search_Compact_RunsFlush_WithA44TabStrip_And60Rows()
    {
        var l = SearchOf(WidthTier.Compact);
        Assert.True(l.Flush);
        Assert.False(l.TabsWrap);
        Assert.Equal(44f, l.TabStripH);                           // C-Search-Compact
        Assert.Equal((44f, 60f), (l.RowArt, l.RowMinH));
        Assert.Equal(1, l.Songs.MaxCols);
        Assert.Equal(16f, l.Gutter);
        Assert.Equal(8f, l.BodyInset);                            // a row's plate adds its 8: the text still starts on the 16
        Assert.Equal(2, l.Genres.Columns(0f));
        // Compact's hit target floors.
        Assert.True(l.TabStripH >= 40f && l.RowMinH >= 40f);
    }

    [Fact]
    public void Search_Small_ScrollsItsTabs_AndSplitsSongsInTwo()
    {
        var l = SearchOf(WidthTier.Small);
        Assert.False(l.TabsWrap);
        Assert.Equal(36f, l.TabStripH);
        Assert.Equal(2, l.Songs.MaxCols);
        Assert.Equal(32f, l.Gutter);
        Assert.False(l.Flush);
    }

    [Fact]
    public void Search_Standard_IsTheTunedDesign()
    {
        var l = SearchOf(WidthTier.Standard);
        Assert.True(l.TabsWrap);
        Assert.Equal(0f, l.TabH);
        Assert.Equal(0f, l.RowArt);
        Assert.Equal(int.MaxValue, l.Songs.MaxCols);
        Assert.Equal(ClassicSearchRules.HeroHeight, l.HeroH);
        Assert.True(float.IsNaN(l.HeroBesideW));
        Assert.Equal(ClassicSearchHeroForm.Card, l.HeroForm);
        Assert.Null(l.ArtistRail);
        Assert.Equal(36f, l.Gutter);
        // The Albums / Playlists grid is a floor-fit at 180 + the 16 gap, uncapped: floor((1368 + 16) / 196) = 7.
        Assert.Equal(7, l.FacetGridColumns(1368f));
    }

    [Fact]
    public void Search_Huge_PutsTheTopResultBesideBestMatches()
    {
        var l = SearchOf(WidthTier.Huge4K);
        Assert.Equal(WidthTier.Huge, l.Tier);
        Assert.Equal((700f, 300f), (l.HeroBesideW, l.HeroH));     // C-Search-Huge
        Assert.True(l.GenresBeside);
        Assert.Equal(6, l.Genres.Columns(0f));
        Assert.Equal(10, l.Rail.MaxColumns);
        Assert.Equal(10, l.FacetGridColumns(9000f));              // capped at 10
        // The avatar top result and the ten-card Artists rail (C-Search-Huge).
        Assert.Equal(ClassicSearchHeroForm.Avatar, l.HeroForm);
        Assert.Equal((128f, 28f), (ClassicSearchTiers.AvatarArt, ClassicSearchTiers.AvatarPad));
        Assert.NotNull(l.ArtistRail);
        Assert.Equal(10, l.ArtistRail!.Value.MaxColumns);
        Assert.True(l.ArtistRail!.Value.Pager);
        // The avatar card's copy fits its 300: the inset, the art and its step, the 40/52 title, the kind line.
        float copy = 2f * ClassicSearchTiers.AvatarPad + ClassicSearchTiers.AvatarArt + ClassicSearchTiers.AvatarTitleGap
                     + 2f * ClassicSearchTiers.AvatarLineGap + 52f + ClassicSearchTiers.AvatarMetaLineHeight;
        Assert.True(copy <= l.HeroH, $"the avatar copy needs {copy}, the card is {l.HeroH}");
    }

    [Theory]
    [InlineData(WidthTier.Compact)]
    [InlineData(WidthTier.Small)]
    [InlineData(WidthTier.Standard)]
    public void Search_BelowHuge_KeepsTheTunedCard_AndDrawsNoArtistRail(WidthTier tier)
    {
        var l = SearchOf(tier);
        Assert.Equal(ClassicSearchHeroForm.Card, l.HeroForm);
        Assert.Null(l.ArtistRail);
    }

    [Fact]
    public void Search_RowShape_OnlyReshapesTheTierRow()
    {
        var row = Shape.Row(SearchHitRules.RowArt);
        Assert.Equal(row, SearchOf(WidthTier.Standard).RowShape(row));
        var compact = SearchOf(WidthTier.Compact).RowShape(row);
        Assert.Equal((44f, 60f), (compact.ArtEdge, compact.MinHeight));
        // The large lead row keeps its own 84 in 112.
        Assert.Equal(Shape.RowLarge, SearchOf(WidthTier.Compact).RowShape(Shape.RowLarge));
    }
}
