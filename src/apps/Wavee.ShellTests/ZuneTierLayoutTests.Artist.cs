// ── Wavee.ShellTests/ZuneTierLayoutTests.Artist.cs — Zune's artist and people pages at every tier size and edge (themes plan 3.5, 6.3; P5 ZT6) ──
//
// The artist page, the standalone discography page, the concert hub, the concert event page, the profile and the people list on the mounted app at every
// size of the harness, on the facts and the records, never on pixels. For each page and size:
//
//   · the page's record is its content tier's (`ZuneArtistTiers.Of`, `ZuneConcertTiers.Of`, `ZuneProfileTiers.Of`: folded on a Short window) and the
//     core reads the same one through the policy (`ThemePolicies.ArtistTiers`: the chart's columns, the section clearance, the list's narrowest card);
//   · what the record decides is what is mounted: the hero at least the record's height and above the first section, its copy on the section column's x,
//     the chart's cap, the rail's width, the concert slab beside the facts or under them, the profile's avatar edge, its actions inside the column;
//   · nothing runs outside the content region (the harness checks the top-level regions; these check the pages' own parts) and at Compact every link
//     word, ring, row and tab is at least 40 x 40.
//
// And, on the pages' parts across every edge both ways (639 / 640, 1007 / 1008, 1919 / 1920, 3199 / 3200 and the heights, 800 x 600 to 800 x 330): the
// resized frame keeps the old tier, the crossing frame commits the new one, and the parts are the SAME live nodes after it (nothing remounted).
//
// Plus the two core follow-ups of the lane: a pivot click parks its section `SectionAir` under the viewport's top (Zune's band is outside it; Classic's
// 56 is its sticky band), and YOUR OWN profile reaches Ready in `--fake` (its row is the bare id "wavee-listener", not a `spotify:user:` uri).

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class ZuneTierLayoutTests
{
    // ── the fixtures (the fake catalogue's artist ar0 and its discography, the concert hub and cc3n0 (the one show with offers, a lineup and related shows), the fake user mia-solace and her list) ──

    const string Zt6ArtistUri = "spotify:artist:ar0", Zt6ConcertUri = "spotify:concert:cc3n0", Zt6PersonUri = "spotify:user:mia-solace";

    static readonly DiscoveryFixture ArtistFixture = new("zune artist", static () => Shell.For(EntityUri.Parse(Zt6ArtistUri), "Artist"),
        static shell => UntilBackground(shell, () => !shell.Find(ZuneArtistRules.HeroKey).IsNull && !shell.Find(ZuneArtistRules.PopularKey).IsNull,
            "Zune's artist hero and its popular band"),
        [ZuneArtistRules.HeroKey, ZuneArtistRules.NameKey, ZuneArtistRules.CaptionKey, ZuneArtistRules.PopularKey, "zune-artist-magazine"],
        [ZuneArtistRules.AnchorPrefix, ZuneArtistRules.SectionPrefix]);

    static readonly DiscoveryFixture DiscographyFixture = new("zune discography", static () => Shell.Parse("disco:0:" + Zt6ArtistUri),
        static shell => UntilBackground(shell, () => !shell.Find(ZuneArtistRules.DiscoPageKey).IsNull && !shell.Find(ZuneArtistRules.DiscoHeadKey).IsNull,
            "Zune's discography page and its head"),
        [ZuneArtistRules.DiscoPageKey, ZuneArtistRules.DiscoHeadKey], ["zune-discography-tab:"]);

    static readonly DiscoveryFixture ConcertHubFixture = new("zune concert hub", static () => Concert.HubRoute,
        static shell => UntilBackground(shell, () => !shell.Find("concert-hub:page").IsNull && !shell.Find("concert-filter-bar:row").IsNull,
            "Zune's concert hub and its filter row"),
        ["concert-hub:page", "concert-hub:head", "concert-filter-bar:row"], []);

    static readonly DiscoveryFixture ConcertEventFixture = new("zune concert", static () => Shell.For(EntityUri.Parse(Zt6ConcertUri), "Concert"),
        // The column and the date stand from the seed; the hero and the tickets slab arrive with the event's offers (a background load).
        static shell => UntilBackground(shell, () => !shell.Find("zune-concert:column").IsNull && !shell.Find("zune-concert:date").IsNull
            && !shell.Find("zune-concert:hero").IsNull && !shell.Find("zune-concert:tickets").IsNull, "Zune's event page and its tickets"),
        ["zune-concert:column", "zune-concert:hero", "zune-concert:date", "zune-concert:tickets"], []);

    static readonly DiscoveryFixture ProfileFixture = new("zune profile", static () => ProfileRoute.For(EntityUri.Parse(Zt6PersonUri), "Mia"),
        static shell => UntilBackground(shell, () => !shell.Find(ZuneProfileRules.ColumnKey).IsNull && !shell.Find(ZuneProfileRules.HeroKey).IsNull,
            "Zune's profile column and its hero"),
        [ZuneProfileRules.ColumnKey, ZuneProfileRules.HeroKey, "zune-profile-avatar", "zune-profile-name", ZuneProfileRules.ActionsKey],
        [ZuneProfileRules.SectionPrefix]);

    static readonly DiscoveryFixture PeopleFixture = new("zune people list",
        static () => ProfileListRoute.For(EntityUri.Parse(Zt6PersonUri), ProfileFacet.Following),
        static shell => UntilBackground(shell, () => !shell.Find(ZuneProfileRules.ListPageKey).IsNull && !shell.Find(ZuneProfileRules.ListHeadKey).IsNull,
            "Zune's people list and its head"),
        [ZuneProfileRules.ListPageKey, ZuneProfileRules.ListHeadKey], []);

    static PageChrome? ArtistChrome() => Sidebar.DockedPaneProps.Band!.Chrome.Peek();

    /// <summary>A node's rect, or default where it is not mounted.</summary>
    static RectF ArtistRectOf(Shell.HeadlessShell shell, string key)
    {
        var n = shell.Find(key);
        return n.IsNull ? default : shell.Host.Scene.AbsoluteRect(n);
    }

    // ── the artist ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(FrameSizes))]
    public void Zunes_artist_page_holds_its_tier_record(string size) => AssertDiscoveryPage(ArtistFixture, size, (shell, tiers) =>
    {
        var scene = shell.Host.Scene;
        var lay = ZuneArtistTiers.Of(tiers);
        Assert.Equal(tiers.Content, lay.Tier);
        // The core reads Zune's answers: the chart's columns and rows, the clearance a pivot click leaves under the band.
        Assert.Same(ZuneArtistTiers.Policy, ShellPolicy.Now.ArtistTiers);
        Assert.Equal(lay.ChartColumns, ShellPolicy.Now.ArtistTiers.ChartMaxColumns(tiers));
        Assert.Equal(lay.ChartRows, ShellPolicy.Now.ArtistTiers.ChartRows(tiers));
        Assert.Equal(ZuneArtistRules.SectionAir, ShellPolicy.Now.ArtistTiers.BandClearance(tiers));
        // The Short rung folds the hero.
        if (tiers.Height == HeightTier.Short) Assert.Equal(ZuneArtistTiers.ShortHeroH, lay.HeroH);

        var content = ArtistRectOf(shell, ZuneFrameLayout.ContentKey);
        var hero = ArtistRectOf(shell, ZuneArtistRules.HeroKey);
        var popular = ArtistRectOf(shell, ZuneArtistRules.PopularKey);
        var caption = ArtistRectOf(shell, ZuneArtistRules.CaptionKey);
        AssertWithin(size, "the hero", hero, content.X, content.X + content.W);
        AssertWithin(size, "the popular band", popular, content.X, content.X + content.W);
        // The hero is at least the record's height, hugs its copy and never runs under the first section.
        Assert.True(hero.H >= lay.HeroH - 0.5f, $"{size}: the hero is {hero.H} tall, under the record's {lay.HeroH}");
        Assert.True(hero.Y + hero.H <= popular.Y + 0.5f, $"{size}: the hero ({hero}) ends above the first section ({popular})");
        Assert.True(caption.Y + caption.H <= hero.Y + hero.H + 0.5f, $"{size}: the caption ({caption}) stays inside the hero ({hero})");
        // ONE alignment source: the hero's copy starts where the sections start, and the column stops at the cap.
        Assert.True(MathF.Abs(caption.X - popular.X) <= 0.5f, $"{size}: the caption starts at {caption.X}, the sections at {popular.X}");
        Assert.True(popular.W <= ZuneFrameRules.PageCap + 0.5f, $"{size}: the column is {popular.W} wide, over the cap");

        // The chart's cap and the rail's width are the record's.
        var chart = ArtistRectOf(shell, "zune-artist-chart");
        if (lay.ChartMaxW > 0f && chart.W > 0f) Assert.True(chart.W <= lay.ChartMaxW + 0.5f, $"{size}: the chart is {chart.W} wide, over {lay.ChartMaxW}");
        var rail = ArtistRectOf(shell, ZuneArtistRules.RailKey);
        if (lay.RailBeside && rail.W > 0f) Assert.True(MathF.Abs(rail.W - lay.RailW) <= 0.5f, $"{size}: the rail is {rail.W} wide, not {lay.RailW}");

        // One home per verb: the Compact row under the hero alone carries play, shuffle and follow; from Small up they are the band's.
        Assert.Equal(lay.Actions == ZuneArtistActions.BelowHero, !shell.Find(ZuneArtistRules.ActionsKey + ":below").IsNull);
        if (lay.Actions == ZuneArtistActions.Band) Assert.True(shell.Find(ZuneArtistRules.ActionsKey).IsNull, $"{size}: no primary action row in the hero");

        // Every section's holder stands inside the content region.
        foreach (var (node, key) in shell.FindPrefix(ZuneArtistRules.AnchorPrefix))
            AssertWithin(size, key, scene.AbsoluteRect(node), content.X, content.X + content.W);

        if (tiers.Window == WidthTier.Compact)
        {
            AssertTargets(shell, size, ZuneArtistRules.DiscographyLinkPrefix, "zune-artist-link:");
            // The Compact row under the hero: every focusable part of it is at least 40 x 40.
            var row = shell.Find(ZuneArtistRules.ActionsKey + ":below");
            if (!row.IsNull)
                foreach (var n in Subtree(scene, row))
                {
                    var flags = scene.Flags(n);
                    if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0 || scene.InCollapsedSubtree(n)) continue;
                    var r = scene.AbsoluteRect(n);
                    if (r.W <= 0f || r.H <= 0f) continue;
                    Assert.True(r.W >= 39.5f && r.H >= 39.5f, $"{size}: a hero action is {r.W} x {r.H}, under 40 x 40");
                }
        }
    });

    [Theory, MemberData(nameof(DiscoveryCrossings))]
    public void Zunes_artist_parts_survive_a_crossing(float fromW, float fromH, float toW, float toH)
        => CrossPage(ArtistFixture, fromW, fromH, toW, toH, glide: []);

    // ── the discography ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(PageSizes))]
    public void Zunes_discography_page_holds_its_tier_record(string size) => AssertDiscoveryPage(DiscographyFixture, size, (shell, tiers) =>
    {
        var lay = ZuneArtistTiers.Of(tiers);
        Assert.Equal(tiers.Content, lay.Tier);
        var content = ArtistRectOf(shell, ZuneFrameLayout.ContentKey);
        AssertWithin(size, "the head", ArtistRectOf(shell, ZuneArtistRules.DiscoHeadKey), content.X, content.X + content.W);
        // The three release-type words: all there, every one inside the column (they wrap at 300 instead of running off), and a row of targets.
        var tabs = shell.FindPrefix("zune-discography-tab:");
        Assert.Equal(3, tabs.Count);
        foreach (var (node, key) in tabs)
            AssertWithin(size, key, shell.Host.Scene.AbsoluteRect(node), content.X, content.X + content.W);
        if (tiers.Window == WidthTier.Compact) AssertTargets(shell, size, "zune-discography-tab:", "zune-discography-crumb");
    });

    [Theory, MemberData(nameof(DiscoveryCrossings))]
    public void Zunes_discography_parts_survive_a_crossing(float fromW, float fromH, float toW, float toH)
        => CrossPage(DiscographyFixture, fromW, fromH, toW, toH, glide: []);

    // ── the concerts ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(PageSizes))]
    public void Zunes_concert_hub_holds_its_tier_record(string size) => AssertDiscoveryPage(ConcertHubFixture, size, (shell, tiers) =>
    {
        var rec = ZuneConcertTiers.Of(tiers);
        Assert.Equal(tiers.Content, rec.Tier);
        Assert.Equal(tiers.Window == WidthTier.Compact ? 40f : 32f, rec.HubPill);
        var content = ArtistRectOf(shell, ZuneFrameLayout.ContentKey);
        AssertWithin(size, "the hub", ArtistRectOf(shell, "concert-hub:page"), content.X, content.X + content.W);
        // The filter bar's when pill (the rest pill or the fused one, one key) is the record's height or more: the Compact 40 target.
        foreach (var (pill, _) in shell.FindPrefix("when-pill"))
        {
            if (shell.Host.Scene.InCollapsedSubtree(pill)) continue;
            var r = shell.Host.Scene.AbsoluteRect(pill);
            if (r.H > 0f) Assert.True(r.H >= rec.HubPill - 0.5f, $"{size}: the when pill is {r.H} tall, under {rec.HubPill}");
        }
    });

    [Theory, MemberData(nameof(DiscoveryCrossings))]
    public void Zunes_concert_hub_parts_survive_a_crossing(float fromW, float fromH, float toW, float toH)
        => CrossPage(ConcertHubFixture, fromW, fromH, toW, toH, glide: []);

    [Theory, MemberData(nameof(PageSizes))]
    public void Zunes_concert_event_page_holds_its_tier_record(string size) => AssertDiscoveryPage(ConcertEventFixture, size, (shell, tiers) =>
    {
        var rec = ZuneConcertTiers.Of(tiers);
        Assert.Equal(tiers.Content, rec.Tier);
        var content = ArtistRectOf(shell, ZuneFrameLayout.ContentKey);
        var column = ArtistRectOf(shell, "zune-concert:column");
        AssertWithin(size, "the column", column, content.X, content.X + content.W);
        Assert.True(column.W <= ZuneFrameRules.PageCap + 0.5f, $"{size}: the column is {column.W} wide, over the page cap");
        // The slab's slot is the record's width beside the facts at the tiers that give it one; elsewhere it is the identity's width under them (the
        // tickets inside it bled to the gutters at Compact). It is the same slot at every tier, so a crossing keeps it.
        var slab = ArtistRectOf(shell, "zune-concert:slab");
        var identity = ArtistRectOf(shell, "zune-concert:identity");
        if (rec.TicketsW > 0f) Assert.True(MathF.Abs(slab.W - rec.TicketsW) <= 0.5f, $"{size}: the slab is {slab.W} wide, not {rec.TicketsW}");
        else Assert.True(slab.Y >= identity.Y + identity.H - 0.5f && MathF.Abs(slab.W - identity.W) <= 0.5f, $"{size}: the slab ({slab}) is not under the identity ({identity})");
        var tickets = ArtistRectOf(shell, "zune-concert:tickets");
        AssertWithin(size, "the tickets", tickets, content.X, content.X + content.W);
        if (tiers.Window == WidthTier.Compact) AssertTargets(shell, size, "related:near");
    });

    [Theory, MemberData(nameof(DiscoveryCrossings))]
    public void Zunes_concert_event_parts_survive_a_crossing(float fromW, float fromH, float toW, float toH)
        => CrossPage(ConcertEventFixture, fromW, fromH, toW, toH, glide: []);

    // ── the profile ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(FrameSizes))]
    public void Zunes_profile_holds_its_tier_record(string size) => AssertDiscoveryPage(ProfileFixture, size, (shell, tiers) =>
    {
        var scene = shell.Host.Scene;
        var rec = ZuneProfileTiers.Of(tiers);
        Assert.Equal(tiers.Content, rec.Tier);
        var content = ArtistRectOf(shell, ZuneFrameLayout.ContentKey);
        var column = ArtistRectOf(shell, ZuneProfileRules.ColumnKey);
        AssertWithin(size, "the column", column, content.X, content.X + content.W);
        var hero = ArtistRectOf(shell, ZuneProfileRules.HeroKey);
        AssertWithin(size, "the hero", hero, column.X, column.X + column.W);
        var avatar = ArtistRectOf(shell, "zune-profile-avatar");
        Assert.True(MathF.Abs(avatar.W - rec.Avatar) <= 0.5f, $"{size}: the avatar is {avatar.W} wide, not {rec.Avatar}");

        // The actions stay inside the column: at 300 they wrap to a second row of targets instead of running off the page.
        var actions = shell.Find(ZuneProfileRules.ActionsKey);
        if (!actions.IsNull)
        {
            Assert.True(scene.AbsoluteRect(actions).H >= rec.ActionsH - 0.5f, $"{size}: the actions row is under the record's {rec.ActionsH}");
            for (var child = scene.FirstChild(actions); !child.IsNull; child = scene.NextSibling(child))
                AssertWithin(size, "an action", scene.AbsoluteRect(child), column.X, column.X + column.W);
        }

        // The ranked rows are the record's height at least, inside the column, and 40 x 40 at Compact; the sections stand in the column.
        foreach (var (node, key) in shell.FindPrefix(ZuneProfileRules.TopPrefix))
        {
            var r = scene.AbsoluteRect(node);
            if (r.W <= 0f) continue;
            Assert.True(r.H >= rec.TopRowH - 0.5f, $"{size}: {key} is {r.H} tall, under {rec.TopRowH}");
            AssertWithin(size, key, r, column.X, column.X + column.W);
        }
        foreach (var (node, key) in shell.FindPrefix(ZuneProfileRules.SectionPrefix))
            AssertWithin(size, key, scene.AbsoluteRect(node), column.X, column.X + column.W);

        if (tiers.Window == WidthTier.Compact)
            AssertTargets(shell, size, "zune-profile-stat:", "zune-profile-seeall:", ZuneProfileRules.TopPrefix, "zune-profile-open-account", "zune-profile-share");
    });

    [Theory, MemberData(nameof(DiscoveryCrossings))]
    public void Zunes_profile_parts_survive_a_crossing(float fromW, float fromH, float toW, float toH)
        => CrossPage(ProfileFixture, fromW, fromH, toW, toH, glide: []);

    [Theory, MemberData(nameof(PageSizes))]
    public void Zunes_people_list_holds_its_tier_record(string size) => AssertDiscoveryPage(PeopleFixture, size, (shell, tiers) =>
    {
        var rec = ZuneProfileTiers.Of(tiers);
        Assert.Equal(tiers.Content, rec.Tier);
        // The core plans the grid off the policy's narrowest card: the record's.
        Assert.Equal(rec.ListMinCell, ShellPolicy.Now.ArtistTiers.ProfileListMinCell(tiers));
        var content = ArtistRectOf(shell, ZuneFrameLayout.ContentKey);
        AssertWithin(size, "the page", ArtistRectOf(shell, ZuneProfileRules.ListPageKey), content.X, content.X + content.W);
        AssertWithin(size, "the head", ArtistRectOf(shell, ZuneProfileRules.ListHeadKey), content.X, content.X + content.W);
        foreach (var (node, key) in shell.FindPrefix("zune-people-word:"))
            AssertWithin(size, key, shell.Host.Scene.AbsoluteRect(node), content.X, content.X + content.W);
        if (tiers.Window == WidthTier.Compact) AssertTargets(shell, size, "zune-people-word:", "zune-people-profile");
    });

    // ── the core follow-ups ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>YOUR OWN profile (--fake's account row is the bare id "wavee-listener", not a <c>spotify:user:</c> uri) mounts Zune's page with its hero (it
    /// used to answer "isn't available"), and the band's row 2 carries "open account" for it.</summary>
    [Fact]
    public void Your_own_profile_reaches_ready_in_the_fake_dataset() => Mounted(shell =>
    {
        shell.RunFrames(10);
        try
        {
            var route = ProfileRoute.For(new User(Entities.Current.MeSlot));
            Assert.False(route.IsNone, "the fake dataset addresses your own profile");
            Shell.GoTo(route);
            UntilBackground(shell, () => !shell.Find(ZuneProfileRules.HeroKey).IsNull, "your own profile's hero");
            Assert.True(shell.Find(ZuneProfileRules.UnavailableKey).IsNull, "your own profile is not 'unavailable'");
            UntilBackground(shell, () => ArtistChrome() is { Views.Count: > 0 }, "your profile's sections in the band's row 2");
            Assert.Contains(ArtistChrome()!.Actions, a => a.Id == "open-account");
        }
        finally
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(10);
        }
    }, 1440f, 960f);

    /// <summary>A pivot click parks its section `SectionAir` under the top of the page's viewport (Zune's band is outside it), not Classic's 56 (the
    /// sticky band, which Zune does not draw inside the page): the first view's section is a destination well down the fake artist's page.</summary>
    [Fact]
    public void A_pivot_click_parks_the_section_at_the_clearance_not_under_a_band_that_is_not_there() => Mounted(shell =>
    {
        shell.RunFrames(10);
        try
        {
            Shell.GoTo(Shell.For(EntityUri.Parse(Zt6ArtistUri), "Artist"));
            UntilBackground(shell, () => !shell.Find(ZuneArtistRules.PopularKey).IsNull && ArtistChrome() is { Views.Count: > 1 }, "the artist's page and its pivot");
            shell.RunFrames(60);
            var scene = shell.Host.Scene;
            var scroll = shell.FindPrefix(ZuneArtistRules.ScrollPrefix);
            Assert.NotEmpty(scroll);
            var viewport = scene.AbsoluteRect(scroll[0].Node);

            ArtistChrome()!.Views[0].Go();
            shell.RunFrames(120);   // the glide settles

            // Some section holder stands at the viewport's top plus the clearance; the 56 of a band inside the viewport would put it 40 lower.
            float best = float.MaxValue;
            foreach (var (node, _) in shell.FindPrefix(ZuneArtistRules.AnchorPrefix))
            {
                float top = scene.AbsoluteRect(node).Y - viewport.Y;
                if (MathF.Abs(top - ZuneArtistRules.SectionAir) < MathF.Abs(best - ZuneArtistRules.SectionAir)) best = top;
            }
            Assert.True(MathF.Abs(best - ZuneArtistRules.SectionAir) <= 1.5f,
                $"the clicked section parks {best} under the viewport's top, not {ZuneArtistRules.SectionAir}");
        }
        finally
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(10);
        }
    }, 1440f, 960f);
}
