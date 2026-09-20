// ── Wavee.Tests/DetailSkeletonGeometryTests.cs — D49: the loading band IS the loaded band ─────────────────────────
//
// Ported from _old/Wavee.Tests/DetailSkeletonGeometryTests.cs onto `Detail.VerticalLayout` / `Detail.Skeleton`
// (Entities/Detail.cs). Every 0.2.9 assertion is kept; only the call shape changed. Added in 0.3:
//   · W28 — the CHART caption's presence flag. 0.2.9's hero emitted `hero-chart` but the band arithmetic had no flag for
//     it, so a chart playlist's caption (+ one gap) arrived outside the reserved band and shoved the toolbar, the
//     column header and every row. The flag reserves it exactly like the daylist pulse.
//   · the skeleton's bar geometry (fractions, the 32-DIP floor, the last-line-short rule), now a named rule.
//
// The defect D49 fixed: in the hero system the hero and chrome are PREFIX ITEMS of the virtualized list, so while the
// model was pending they did not exist, and several hundred DIP materialised above the rows when content landed. The
// fix is arithmetic, not a second design: the skeleton reserves `HeroBandHeight`, the same function the loaded hero's
// pre-measure collapse binds use.
//
// Podcast rework wave P2 (§5.4) — the two-column RAIL gets the same treatment: `Skeleton.RailPlanFor` (the skeleton's
// prediction, now told which rail slots the page declared) and `RailLayout.RowsFor` (the loaded column's own row
// decisions) are measured by ONE nominal height, `RailLayout.HeightOf`. Pinned here: without the podcast slots the plan
// is exactly the pre-podcast one; with each slot a podcast page declares, the reservation IS the reveal; and each row
// costs exactly its nominal plus one gap.

using Xunit;
using Config = Wavee.Detail.Config;
using RailBadgeRow = Wavee.Detail.RailBadgeRow;
using RailLayout = Wavee.Detail.RailLayout;
using RailSlotSet = Wavee.Detail.RailSlotSet;
using Skeleton = Wavee.Detail.Skeleton;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class DetailSkeletonGeometryTests
{
    const float LadderMin = 240f, LadderMax = 1400f;

    // ── the two-column rail's skeleton plan (the rows it reserves mirror Detail.RailColumn) ───────────────────────

    [Fact]
    public void RailPlan_Playlist_OwnerRowTitleMetaCtaAndBlurb()
    {
        var plan = Skeleton.RailPlanFor(DetailKind.Playlist, BadgeStyle.OwnerRow, heart: true, descriptionMaxLines: 6);
        Assert.False(plan.Eyebrow);
        Assert.True(plan.Owner);
        Assert.False(plan.Artists);
        Assert.True(plan.Meta);
        Assert.Equal(Skeleton.RailTitleLines, plan.TitleLines);
        Assert.Equal(3, plan.Fabs);                       // heart · Share · ⋯
        Assert.Equal(Skeleton.RailDescriptionLines, plan.DescriptionLines);
    }

    [Fact]
    public void RailPlan_Album_EyebrowArtistsNoMetaNoBlurbNoMore()
    {
        var plan = Skeleton.RailPlanFor(DetailKind.Album, BadgeStyle.TypeYear, heart: true, descriptionMaxLines: 6);
        Assert.True(plan.Eyebrow);
        Assert.False(plan.Owner);
        Assert.True(plan.Artists);
        Assert.False(plan.Meta);
        Assert.Equal(2, plan.Fabs);                       // heart · Share — an album's rail has no ⋯ (ch 05 parity 15)
        Assert.Equal(0, plan.DescriptionLines);
    }

    [Fact]
    public void RailPlan_Show_StatesItsPublisherLineAndBlurb()
    {
        var plan = Skeleton.RailPlanFor(DetailKind.Show, BadgeStyle.TypeYear, heart: true, descriptionMaxLines: 3);
        Assert.True(plan.Meta);
        Assert.Equal(3, plan.DescriptionLines);
    }

    [Fact]
    public void RailPlan_Liked_NoHeartNoBlurb()
    {
        var plan = Skeleton.RailPlanFor(DetailKind.Liked, BadgeStyle.None, heart: false, descriptionMaxLines: 6);
        Assert.False(plan.Owner);
        Assert.True(plan.Meta);
        Assert.Equal(2, plan.Fabs);                       // Share · ⋯
        Assert.Equal(0, plan.DescriptionLines);
    }

    [Fact]
    public void RailPlan_BlurbClampsToTheRailsOwnLines()
    {
        Assert.Equal(2, Skeleton.RailPlanFor(DetailKind.Playlist, BadgeStyle.OwnerRow, heart: true, descriptionMaxLines: 2).DescriptionLines);
        Assert.Equal(0, Skeleton.RailPlanFor(DetailKind.Playlist, BadgeStyle.OwnerRow, heart: true, descriptionMaxLines: 0).DescriptionLines);
        Assert.Equal(0, Skeleton.RailPlanFor(DetailKind.Playlist, BadgeStyle.OwnerRow, heart: true, descriptionMaxLines: -1).DescriptionLines);
    }

    // ── the podcast seams (podcast rework §5.4): the rail's row model ────────────────────────────────────────────────

    /// <summary>Declaring NO podcast slot leaves every kind's plan exactly what it was before the seams (the fields the
    /// pre-podcast plan had, the new ones absent) — and an Attribution slot alone moves nothing outside an episode.</summary>
    [Theory]
    [InlineData(DetailKind.Album, BadgeStyle.TypeYear, true, 6)]
    [InlineData(DetailKind.Album, BadgeStyle.TypeYear, true, 3)]
    [InlineData(DetailKind.Playlist, BadgeStyle.OwnerRow, true, 6)]
    [InlineData(DetailKind.Playlist, BadgeStyle.OwnerRow, false, 2)]
    [InlineData(DetailKind.Liked, BadgeStyle.None, false, 6)]
    [InlineData(DetailKind.Show, BadgeStyle.TypeYear, true, 3)]
    [InlineData(DetailKind.Show, BadgeStyle.TypeYear, true, 6)]
    public void RailPlan_WithoutPodcastSlots_IsThePrePodcastPlan(DetailKind kind, BadgeStyle badges, bool heart, int descMax)
    {
        bool typeYear = badges == BadgeStyle.TypeYear;
        var before = new Skeleton.RailPlan(
            Eyebrow: typeYear, Owner: badges == BadgeStyle.OwnerRow, Artists: typeYear,
            Meta: !typeYear || kind == DetailKind.Show, TitleLines: Skeleton.RailTitleLines,
            Fabs: (heart ? 1 : 0) + 1 + (kind != DetailKind.Album ? 1 : 0),
            DescriptionLines: kind is DetailKind.Playlist or DetailKind.Show ? Math.Min(Skeleton.RailDescriptionLines, descMax) : 0);

        Assert.Equal(before, Skeleton.RailPlanFor(kind, badges, heart, descMax));
        Assert.Equal(before, Skeleton.RailPlanFor(kind, badges, heart, descMax, default));
        Assert.Equal(before, Skeleton.RailPlanFor(kind, badges, heart, descMax, new RailSlotSet(Attribution: true)));
        Assert.Equal(RailBadgeRow.None, before.Badges);
        Assert.False(before.Rating || before.Ledger || before.Satellites || before.Topics);
    }

    /// <summary>The nominal height of the pre-podcast skeleton columns, by hand: 24 above, the rows, 14 between, 24 below —
    /// the CTA line wrapping exactly as the engine's flex wrap wraps [Play 104][the FAB group].</summary>
    [Fact]
    public void RailHeight_OfThePrePodcastColumns_IsTheirRowSum()
    {
        // Album, 280 rail (cover 256), 36 title line: cover · eyebrow 16 · title 2 × 36 · artists 16 · CTA 4 + 40 (Play
        // 104 + 12 + [heart · Share] 88 = 204 ≤ 256: one line). Five rows, four gaps.
        var album = Skeleton.RailPlanFor(DetailKind.Album, BadgeStyle.TypeYear, heart: true, descriptionMaxLines: 6);
        Assert.Equal(24f + 256f + 16f + 72f + 16f + 44f + 4 * 14f + 24f, RailLayout.HeightOf(album, 256f, 36f));

        // Playlist, 240 rail (cover 216): cover · owner 24 · title 72 · meta 16 · CTA wrapped (104 + 12 + 136 > 216):
        // 4 + 36 + 12 + 40 · blurb 3 × 18. Six rows, five gaps.
        var playlist = Skeleton.RailPlanFor(DetailKind.Playlist, BadgeStyle.OwnerRow, heart: true, descriptionMaxLines: 6);
        Assert.Equal(24f + 216f + 24f + 72f + 16f + 92f + 54f + 5 * 14f + 24f, RailLayout.HeightOf(playlist, 216f, 36f));
    }

    const int PodcastDescMax = 3;

    /// <summary>A podcast page as waves P3 / P5 declare it: the Attribution slot always (a show's publisher line, an
    /// episode's show link), plus the slot under test.</summary>
    static RailSlotSet PodcastSlots(string slot) => slot switch
    {
        "badges" => new(Attribution: true, Badges: true),
        "rating" => new(Attribution: true, Rating: true),
        "ledger" => new(Attribution: true, Ledger: true),
        "topics" => new(Attribution: true, Topics: true),
        "satellites" => new(Attribution: true, Satellites: true, SatelliteCount: 5),
        "all" => new(Attribution: true, Badges: true, Rating: true, Ledger: true, Topics: true, Satellites: true, SatelliteCount: 5),
        _ => new(Attribution: true),
    };

    /// <summary>The revealed rail's rows for that page: its eyebrow text known ("Podcast" / "Episode"), no billed artists,
    /// its meta line and blurb present, and the fixed FAB group the skeleton predicts (heart when the kind has one,
    /// Share, ⋯) when it declares no satellites.</summary>
    static Skeleton.RailPlan Revealed(DetailKind kind, RailSlotSet slots)
    {
        var cfg = Config.For(kind, AlbumKind.Album);
        return RailLayout.RowsFor(kind, cfg.Badges, eyebrowText: true, ownerKnown: false, artists: false, metaShown: true,
                                  fabs: (cfg.Heart != HeartMode.None ? 1 : 0) + 2, description: true, PodcastDescMax, slots);
    }

    static Skeleton.RailPlan Reserved(DetailKind kind, RailSlotSet slots)
    {
        var cfg = Config.For(kind, AlbumKind.Album);
        return Skeleton.RailPlanFor(kind, cfg.Badges, cfg.Heart != HeartMode.None, PodcastDescMax, slots);
    }

    /// <summary>With each slot a podcast page declares, the skeleton RESERVES exactly the rows the loaded rail LAYS OUT:
    /// the two plans are equal, and so is their nominal height at every rail width the grip allows (180…480).</summary>
    [Theory]
    [InlineData(DetailKind.Show, "")]
    [InlineData(DetailKind.Show, "badges")]
    [InlineData(DetailKind.Show, "rating")]
    [InlineData(DetailKind.Show, "ledger")]
    [InlineData(DetailKind.Show, "topics")]
    [InlineData(DetailKind.Show, "satellites")]
    [InlineData(DetailKind.Show, "all")]
    [InlineData(DetailKind.Episode, "")]
    [InlineData(DetailKind.Episode, "badges")]
    [InlineData(DetailKind.Episode, "rating")]
    [InlineData(DetailKind.Episode, "ledger")]
    [InlineData(DetailKind.Episode, "topics")]
    [InlineData(DetailKind.Episode, "satellites")]
    [InlineData(DetailKind.Episode, "all")]
    public void PodcastSlots_TheReservationIsTheReveal(DetailKind kind, string slot)
    {
        var slots = PodcastSlots(slot);
        var reserved = Reserved(kind, slots);
        var revealed = Revealed(kind, slots);
        Assert.Equal(reserved, revealed);
        for (float railW = Detail.RailPolicy.MinWidth; railW <= Detail.RailPolicy.MaxWidth; railW += 20f)
        {
            float cover = railW - 24f;
            Assert.Equal(RailLayout.HeightOf(reserved, cover, 36f), RailLayout.HeightOf(revealed, cover, 36f));
        }
    }

    /// <summary>Each single-line slot row costs exactly its nominal plus one gap — a show's badges nothing (they share the
    /// eyebrow's 16 line), an episode's their own 16 under the meta line; rating 20, ledger 4 + 6 + 16, topics two 18
    /// lines 4 apart.</summary>
    [Theory]
    [InlineData(DetailKind.Show, "badges", 0f)]
    [InlineData(DetailKind.Episode, "badges", 16f + 14f)]
    [InlineData(DetailKind.Show, "rating", 20f + 14f)]
    [InlineData(DetailKind.Episode, "rating", 20f + 14f)]
    [InlineData(DetailKind.Show, "ledger", 26f + 14f)]
    [InlineData(DetailKind.Episode, "ledger", 26f + 14f)]
    [InlineData(DetailKind.Show, "topics", 40f + 14f)]
    [InlineData(DetailKind.Episode, "topics", 40f + 14f)]
    public void PodcastSlots_EachRowCostsItsNominalPlusOneGap(DetailKind kind, string slot, float cost)
    {
        foreach (float cover in new[] { 156f, 216f, 256f, 456f })
            Assert.Equal(cost,
                RailLayout.HeightOf(Reserved(kind, PodcastSlots(slot)), cover, 36f)
                - RailLayout.HeightOf(Reserved(kind, PodcastSlots("")), cover, 36f));

        Assert.Equal(16f, RailLayout.BadgeHeight);
        Assert.Equal(20f, RailLayout.RatingHeight);
        Assert.Equal(26f, RailLayout.LedgerHeight);
        Assert.Equal(40f, RailLayout.TopicsHeight);
        Assert.Equal(14f, RailLayout.Gap);
    }

    /// <summary>Satellites wrap ONE BY ONE after the primary, 8 apart both ways, 36 each: at a 256 measure three fit beside
    /// the 104 primary, the fourth opens a second line — and none at all leaves the primary alone.</summary>
    [Theory]
    [InlineData(0, 4f + 36f)]
    [InlineData(1, 4f + 36f)]
    [InlineData(3, 4f + 36f)]                 // 104 + 3 × (8 + 36) = 236 ≤ 256
    [InlineData(4, 4f + 36f + 8f + 36f)]      // 280 > 256: the fourth wraps
    [InlineData(9, 4f + 36f + 8f + 36f)]      // line two holds six: 36 + 5 × 44 = 256
    [InlineData(10, 4f + 36f + 8f + 36f + 8f + 36f)]
    public void Satellites_WrapOneByOneAfterThePrimary(int count, float cta)
    {
        var plan = Reserved(DetailKind.Episode, new RailSlotSet(Attribution: true, Satellites: true, SatelliteCount: count));
        Assert.True(plan.Satellites);
        Assert.Equal(count, plan.Fabs);
        Assert.Equal(cta, RailLayout.CtaHeight(plan, 256f));
    }

    /// <summary>The loaded rail's lead and badge rows (<c>RowsFor</c>): an episode is led by its show link (the owner-row
    /// shape) and has no eyebrow; without one it falls back to its eyebrow. A show's badges share the eyebrow's line, an
    /// episode's sit under the meta, a kind with no eyebrow gets them on their own line. A podcast's Attribution slot is
    /// its after-title row whatever the billed artists say; an album's is not (it still needs billed artists).</summary>
    [Fact]
    public void RowsFor_PodcastLeadsBadgesAndAttribution()
    {
        var episodeLed = RailLayout.RowsFor(DetailKind.Episode, BadgeStyle.TypeYear, true, false, false, true, 2, true, 3,
                                            new RailSlotSet(Attribution: true, Badges: true));
        Assert.True(episodeLed.Owner);
        Assert.False(episodeLed.Eyebrow);
        Assert.False(episodeLed.Artists);
        Assert.Equal(RailBadgeRow.AfterMeta, episodeLed.Badges);

        var episodeBare = RailLayout.RowsFor(DetailKind.Episode, BadgeStyle.TypeYear, true, false, false, true, 2, true, 3, default);
        Assert.True(episodeBare.Eyebrow);
        Assert.False(episodeBare.Owner);
        Assert.True(episodeBare.Meta);

        var show = RailLayout.RowsFor(DetailKind.Show, BadgeStyle.TypeYear, true, false, false, true, 2, true, 3,
                                      new RailSlotSet(Attribution: true, Badges: true));
        Assert.True(show.Eyebrow);
        Assert.True(show.Artists);
        Assert.Equal(RailBadgeRow.BesideEyebrow, show.Badges);
        Assert.Equal(RailBadgeRow.OwnRow,
            RailLayout.RowsFor(DetailKind.Show, BadgeStyle.TypeYear, false, false, false, true, 2, true, 3,
                               new RailSlotSet(Badges: true)).Badges);
        Assert.Equal(RailBadgeRow.OwnRow,
            RailLayout.RowsFor(DetailKind.Playlist, BadgeStyle.OwnerRow, false, true, false, true, 3, true, 3,
                               new RailSlotSet(Badges: true)).Badges);

        Assert.False(RailLayout.RowsFor(DetailKind.Album, BadgeStyle.TypeYear, true, false, false, false, 2, false, 6,
                                        new RailSlotSet(Attribution: true)).Artists);
        Assert.True(RailLayout.RowsFor(DetailKind.Album, BadgeStyle.TypeYear, true, false, true, false, 2, false, 6,
                                       new RailSlotSet(Attribution: true)).Artists);
    }

    /// <summary>The loaded rail's pre-podcast decisions, unchanged: the eyebrow needs its text, the owner row a name or
    /// the slot, the album no meta, a TypeYear show its meta; satellites absent ⇒ the fixed group's own count.</summary>
    [Fact]
    public void RowsFor_WithoutPodcastSlots_KeepsThePrePodcastDecisions()
    {
        var album = RailLayout.RowsFor(DetailKind.Album, BadgeStyle.TypeYear, true, false, true, true, 2, false, 6, default);
        Assert.True(album.Eyebrow);
        Assert.True(album.Artists);
        Assert.False(album.Meta);
        Assert.Equal(2, album.Fabs);
        Assert.Equal(0, album.DescriptionLines);
        Assert.False(RailLayout.RowsFor(DetailKind.Album, BadgeStyle.TypeYear, false, false, true, true, 2, false, 6, default).Eyebrow);

        var playlist = RailLayout.RowsFor(DetailKind.Playlist, BadgeStyle.OwnerRow, false, true, false, true, 3, true, 6, default);
        Assert.True(playlist.Owner);
        Assert.True(playlist.Meta);
        Assert.Equal(3, playlist.DescriptionLines);
        Assert.False(RailLayout.RowsFor(DetailKind.Playlist, BadgeStyle.OwnerRow, false, false, false, true, 3, true, 6, default).Owner);
        Assert.True(RailLayout.RowsFor(DetailKind.Playlist, BadgeStyle.OwnerRow, false, false, false, true, 3, true, 6,
                                       new RailSlotSet(Attribution: true)).Owner);

        var show = RailLayout.RowsFor(DetailKind.Show, BadgeStyle.TypeYear, true, false, false, true, 2, true, 3, default);
        Assert.True(show.Meta);
        Assert.False(show.Artists);
        Assert.False(RailLayout.RowsFor(DetailKind.Show, BadgeStyle.TypeYear, true, false, false, false, 2, true, 3, default).Meta);
    }

    // The hero emit predicates, as the two real pages present them.
    const bool Album = true;            // eyebrow "ALBUM · 2019", billed artists, meta line, no blurb
    const bool Playlist = true;         // owner row, meta line, description

    // ── the band's arithmetic ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band is the padded composition PLUS the toolbar row — never less than the artwork it holds.</summary>
    [Fact]
    public void HeroBand_AlwaysClearsTheArtworkPlusPaddingPlusToolbar()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
            foreach (bool rowFlow in new[] { false, true })
            {
                float band = VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true);
                float floor = VerticalLayout.HeroPadFor(w, rowFlow)
                            + VerticalLayout.ArtworkFor(w, rowFlow)
                            + VerticalLayout.HeroBottomPad
                            + VerticalLayout.ExpandedToolbarTopPad
                            + VerticalLayout.ToolbarRowHeight
                            + VerticalLayout.ExpandedToolbarBottomPad;
                Assert.True(band >= floor, $"band {band} < artwork floor {floor} at w={w} (rowFlow={rowFlow})");
            }
    }

    /// <summary>The band is exactly the sum of the parts it declares (the pessimistic null-title plan's).</summary>
    [Theory]
    [InlineData(360f, false)]
    [InlineData(400f, false)]
    [InlineData(539f, false)]
    [InlineData(540f, true)]
    [InlineData(700f, true)]
    [InlineData(1200f, true)]
    public void HeroBand_IsExactlyTheCompositionItDeclares(float w, bool rowFlow)
    {
        var plan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, true, true);
        float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true);
        float art = VerticalLayout.ArtworkFor(w, rowFlow);
        float hero = rowFlow ? MathF.Max(art, identity) : art + VerticalLayout.HeroGapFor(w, rowFlow) + identity;
        float expected = VerticalLayout.HeroPadFor(w, rowFlow) + hero + VerticalLayout.HeroBottomPad
                       + VerticalLayout.ExpandedToolbarTopPad
                       + VerticalLayout.ToolbarRowHeight
                       + VerticalLayout.ExpandedToolbarBottomPad;
        Assert.Equal(expected, VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true));
    }

    /// <summary>The identity column reserves the blocks the hero will emit — no more, no less. Title, rule and action
    /// row are unconditional; every optional block (eyebrow, attribution, meta, description, pulse, and W28's chart)
    /// costs exactly its row plus one inter-block gap.</summary>
    [Theory]
    [InlineData(700f, true)]
    [InlineData(400f, false)]
    public void IdentityHeight_ChargesOnlyForTheBlocksTheHeroEmits(float w, bool rowFlow)
    {
        // A fresh PESSIMISTIC plan per flag combination (the budget's chrome sum moves with the flags, even though the
        // null-title SIZE does not at these widths — the fluid cap binds, not the height budget).
        var barePlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false);
        float bare = VerticalLayout.IdentityHeightFor(barePlan, rowFlow, false, false, false, false);
        Assert.Equal(
            barePlan.BlockHeight
            + VerticalLayout.AccentRuleRowHeight
            + VerticalLayout.ActionRowHeight
            + 2f * VerticalLayout.IdentityGap,
            bare);

        var eyebrowPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, false, false);
        Assert.Equal(bare + VerticalLayout.EyebrowRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(eyebrowPlan, rowFlow, true, false, false, false));

        var attributionPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, true, false);
        Assert.Equal(bare + VerticalLayout.AttributionRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(attributionPlan, rowFlow, false, true, false, false));

        var metaPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, true);
        Assert.Equal(bare + VerticalLayout.MetaRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(metaPlan, rowFlow, false, false, true, false));

        // Description never moves the plan (the title's budget excludes it by design), so it reuses barePlan.
        Assert.Equal(
            bare + VerticalLayout.DescriptionMaxLines(rowFlow) * VerticalLayout.DescriptionLineHeight
                 + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(barePlan, rowFlow, false, false, false, true));

        var pulsePlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false, pulse: true);
        Assert.Equal(bare + VerticalLayout.PulseRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(pulsePlan, rowFlow, false, false, false, false, pulse: true));

        // W28: the chart caption is a block like every other — its row plus one gap.
        var chartPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false, chart: true);
        Assert.Equal(bare + VerticalLayout.ChartRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(chartPlan, rowFlow, false, false, false, false, chart: true));
    }

    /// <summary>W28 end to end: a chart playlist's reserved band IS the composition with the caption in it — the band
    /// the skeleton and the pre-measure binds reserve now matches what a chart playlist composes, so nothing shoves when
    /// the model lands. Stacked, where nothing absorbs it, the band grows by exactly the row plus one gap. In row flow
    /// the caption is paid out of the title's height budget first (the plan may shrink the title to keep the column on
    /// the cover), so there the claim is the exact composition, not a fixed delta.</summary>
    [Theory]
    [InlineData(400f, false)]
    [InlineData(380f, false)]
    [InlineData(520f, true)]
    [InlineData(1100f, true)]
    public void W28_TheChartCaptionIsReservedInTheBand(float w, bool rowFlow)
    {
        Assert.Equal(16f, VerticalLayout.ChartRowHeight);

        // The reserved band is exactly the composition WITH the caption (the pessimistic null-title plan, chart flag on).
        var plan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, true, true, chart: true);
        float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true, chart: true);
        float art = VerticalLayout.ArtworkFor(w, rowFlow);
        float hero = rowFlow ? MathF.Max(art, identity) : art + VerticalLayout.HeroGapFor(w, rowFlow) + identity;
        float expected = VerticalLayout.HeroPadFor(w, rowFlow) + hero + VerticalLayout.HeroBottomPad
                       + VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight
                       + VerticalLayout.ExpandedToolbarBottomPad;
        Assert.Equal(expected, VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true, chart: true));

        // For the SAME title plan, the caption costs exactly its row plus one gap.
        Assert.Equal(VerticalLayout.ChartRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true, chart: true)
            - VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true, chart: false));

        float plainBand = VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true, pulse: false, chart: false);
        float chartBand = VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true, pulse: false, chart: true);
        if (!rowFlow)
            Assert.Equal(VerticalLayout.ChartRowHeight + VerticalLayout.IdentityGap, chartBand - plainBand);
        else
            Assert.Equal(
                MathF.Max(0f, VerticalLayout.TitleHeightBudgetFor(w, rowFlow, true, true, true)
                              - VerticalLayout.ChartRowHeight - VerticalLayout.IdentityGap),
                VerticalLayout.TitleHeightBudgetFor(w, rowFlow, true, true, true, chart: true));

        var (plainChrome, plainBlocks) = VerticalLayout.IdentityChrome(true, true, true, false, false, rowFlow);
        var (chartChrome, chartBlocks) = VerticalLayout.IdentityChrome(true, true, true, false, false, rowFlow, chart: true);
        Assert.Equal(plainChrome + VerticalLayout.ChartRowHeight, chartChrome);
        Assert.Equal(plainBlocks + 1, chartBlocks);
    }

    /// <summary>The ALL-flags case, so a new hero row cannot join the column without its own flag: the chrome is the sum
    /// of every row, over nine blocks (the title + eight).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityChrome_EnumeratesEveryHeroBlock(bool rowFlow)
    {
        var (h, blocks) = VerticalLayout.IdentityChrome(eyebrow: true, attribution: true, meta: true, pulse: true,
                                                         description: true, rowFlow, chart: true);
        Assert.Equal(VerticalLayout.EyebrowRowHeight + VerticalLayout.AccentRuleRowHeight + VerticalLayout.AttributionRowHeight
                     + VerticalLayout.MetaRowHeight + VerticalLayout.PulseRowHeight + VerticalLayout.ChartRowHeight
                     + VerticalLayout.ActionRowHeight
                     + VerticalLayout.DescriptionMaxLines(rowFlow) * VerticalLayout.DescriptionLineHeight, h);
        Assert.Equal(9, blocks);
    }

    /// <summary>An album and a playlist both reserve a real band at every width — never one the 56-DIP band could not
    /// collapse into.</summary>
    [Fact]
    public void HeroBand_IsCollapsibleAtEveryWidthForBothPageKinds()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            bool rowFlow = VerticalLayout.RowFlow(w);
            float album = VerticalLayout.HeroBandHeight(w, rowFlow, Album, true, true, false);
            float playlist = VerticalLayout.HeroBandHeight(w, rowFlow, Playlist, true, true, true);
            foreach (float band in new[] { album, playlist })
            {
                Assert.True(band > VerticalLayout.CompactIdentityHeight,
                    $"band {band} cannot collapse into the 56-DIP context band at w={w}");
                Assert.True(VerticalLayout.CollapseDistance(band) > VerticalLayout.CompactRevealBand,
                    $"collapse distance leaves no reveal window at w={w}");
            }
            Assert.True(playlist >= VerticalLayout.HeroBandHeight(w, rowFlow, Playlist, true, true, false));
        }
    }

    [Fact]
    public void HeroBand_UnmeasuredUsesTheFallbackColumn()
    {
        bool rowFlow = VerticalLayout.RowFlow(VerticalLayout.FallbackW);
        Assert.Equal(
            VerticalLayout.HeroBandHeight(VerticalLayout.FallbackW, rowFlow, true, true, true, true),
            VerticalLayout.HeroBandHeight(0f, rowFlow, true, true, true, true));
    }

    // ── the identity column never inflates past its own content (supersedes issue #78's forced MinHeight) ───────────

    /// <summary>A bare identity is SHORTER than the artwork at a real row-flow width. The column no longer forces
    /// itself up to the artwork's edge to close that gap: issue #78's fix only RELOCATED the resulting dead band
    /// from under the action row to a blank strip INSIDE the identity column (between the metadata and the actions,
    /// or under a bare accent rule when nothing else was reserved) — exactly the "large band of dead whitespace" a
    /// tall cover beside little text produces. The BAND's own height still covers the artwork
    /// (<c>MathF.Max(art, identity)</c>): a taller cover simply runs on past a shorter, natural column, top-aligned,
    /// so nothing inside the column is ever stretched to fill space it has no content for.</summary>
    [Fact]
    public void IdentityMinHeight_NeverInflatesTheColumnPastItsNaturalContent()
    {
        const float w = 424f;
        const bool rowFlow = true;
        float art = VerticalLayout.ArtworkFor(w, rowFlow);
        var plan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false);
        float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow, false, false, false, false);
        Assert.True(identity < art, $"fixture assumption broken: identity {identity} is not shorter than art {art}");

        // The column reports NO MinHeight — never forced taller than its own content, in either flow.
        Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(w, rowFlow: true));
        Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(w, rowFlow: false));
        for (float sw = LadderMin; sw <= LadderMax; sw += 1f)
        {
            Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(sw, rowFlow: false));
            Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(sw, rowFlow: true));
        }

        // The BAND still covers the artwork — removing the column's forced MinHeight changes nothing about the
        // RESERVED total, only where (if anywhere) the surplus between the cover and the text is allowed to show.
        float band = VerticalLayout.HeroBandHeight(w, rowFlow, false, false, false, false);
        float expectedBand = VerticalLayout.HeroPadFor(w, rowFlow) + MathF.Max(art, identity) + VerticalLayout.HeroBottomPad
                            + VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight
                            + VerticalLayout.ExpandedToolbarBottomPad;
        Assert.Equal(expectedBand, band);
    }

    // ── the toolbar reservation (issue #78/#79/#80 parity item D) ────────────────────────────────────────────────────

    /// <summary>The band charges the command bar's BOX (44), never the pill row inside it (32).</summary>
    [Fact]
    public void HeroBand_ReservesTheCommandBarSurfaceNotItsPills()
    {
        Assert.Equal(44f, VerticalLayout.ToolbarRowHeight);
        Assert.Equal(32f, VerticalLayout.ToolbarPillHeight);
        Assert.True(VerticalLayout.ToolbarPillHeight < VerticalLayout.ToolbarRowHeight);

        for (float w = LadderMin; w <= LadderMax; w += 1f)
            foreach (bool rowFlow in new[] { false, true })
            {
                float art = VerticalLayout.ArtworkFor(w, rowFlow);
                var plan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, true, true);
                float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true);
                float hero = rowFlow
                    ? MathF.Max(art, identity)
                    : art + VerticalLayout.HeroGapFor(w, rowFlow) + identity;
                float pillOnlyBand = VerticalLayout.HeroPadFor(w, rowFlow) + hero + VerticalLayout.HeroBottomPad
                                    + VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarPillHeight
                                    + VerticalLayout.ExpandedToolbarBottomPad;
                float band = VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true);
                Assert.Equal(VerticalLayout.ToolbarRowHeight - VerticalLayout.ToolbarPillHeight, band - pillOnlyBand);
            }
    }

    // ── the skeleton's bar geometry ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every bar is max(32, round(measure · fraction)) — the fractions are the hero's own run lengths.</summary>
    [Theory]
    [InlineData(251f, Skeleton.EyebrowFraction, 80f)]        // 80.32 → 80
    [InlineData(251f, Skeleton.AttributionFraction, 100f)]   // 100.4 → 100
    [InlineData(251f, Skeleton.MetaFraction, 156f)]          // 155.62 → 156
    [InlineData(251f, Skeleton.PulseFraction, 88f)]          // 87.85 → 88
    [InlineData(251f, Skeleton.TitleLastLineFraction, 171f)] // 170.68 → 171
    [InlineData(80f, Skeleton.EyebrowFraction, 32f)]         // 25.6 → floored at 32
    [InlineData(0f, Skeleton.MetaFraction, 32f)]
    public void BarWidth_IsARoundedFractionWithAThirtyTwoDipFloor(float measure, float fraction, float expected)
        => Assert.Equal(expected, Skeleton.BarWidth(measure, fraction));

    [Fact]
    public void SkeletonConstants_AreTheHerosOwnShapes()
    {
        Assert.Equal(0.32f, Skeleton.EyebrowFraction);
        Assert.Equal(0.40f, Skeleton.AttributionFraction);
        Assert.Equal(0.62f, Skeleton.MetaFraction);
        Assert.Equal(0.35f, Skeleton.PulseFraction);
        Assert.Equal(0.68f, Skeleton.TitleLastLineFraction);
        Assert.Equal(0.55f, Skeleton.DescriptionLastLineFraction);
        Assert.Equal(32f, Skeleton.BarFloor);
        Assert.Equal(4f, Skeleton.BarRadius);
        Assert.Equal(20f, Skeleton.RuleWidth);
        Assert.Equal(2f, Skeleton.RuleHeight);
        Assert.Equal(1f, Skeleton.RuleRadius);
    }

    /// <summary>N runs at the measure, the last one short; a zero-line block still draws one run.</summary>
    [Fact]
    public void LineWidth_RunsAtTheMeasureAndShortensOnlyTheLastLine()
    {
        const float measure = 352f;
        Assert.Equal(measure, Skeleton.LineWidth(measure, 0, 4, Skeleton.DescriptionLastLineFraction));
        Assert.Equal(measure, Skeleton.LineWidth(measure, 2, 4, Skeleton.DescriptionLastLineFraction));
        Assert.Equal(Skeleton.BarWidth(measure, Skeleton.DescriptionLastLineFraction),
                     Skeleton.LineWidth(measure, 3, 4, Skeleton.DescriptionLastLineFraction));
        Assert.Equal(1, Skeleton.LineCount(0));
        Assert.Equal(Skeleton.BarWidth(measure, Skeleton.TitleLastLineFraction),
                     Skeleton.LineWidth(measure, 0, 0, Skeleton.TitleLastLineFraction));
    }
}
