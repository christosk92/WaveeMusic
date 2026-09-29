// ── Wavee.Tests/HomeUi/PodcastPlannerTests.cs — §4.4 rules 1–12 over cards built through the real commit ───────────
//
// `HomeCard` is a live handle over the entity/section-fact tables (see `Wavee.Tests.HomeFixtures`'s own header for
// why a fixture cannot shortcut a column write). `PodcastFx` below is that same technique — stage, commit, read
// back — extended with the podcast-only facts `PodcastPlanner` reads (`ReleasedAtMs`, `PlayedState`,
// `VideoThumbUrl`, `IsExplicit`/`IsPlayable`, `ShowName`/`ShowUri`) that `Wavee.Tests.HomeFixtures.Spec` does not
// carry. It does not touch that shared file (parallel Home-rebuild agents own disjoint files); it is local to this
// test class on purpose.

using System.Collections.Generic;
using System.Linq;
using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public class PodcastPlannerTests
{
    static readonly ZoneTitles Titles = new(
        MadeForYou: "Made for you", BecauseYouLike: "Because you like", MoreForYou: "More for you",
        RadioAndMixes: "Radio & mixes", Browse: "Browse", JumpBackIn: "Jump back in", RecentlyPlayed: "Recently played",
        NewEpisodes: "New episodes", ContinueListening: "Continue listening", VideosYouMightLike: "Videos you might like",
        EpisodesYouMightLike: "Episodes you might like", BecauseYouListenTo: "Because you listen to",
        FromArtistsYouFollow: "From artists you follow", YourShows: "Your shows", ShowsYouMightLike: "Shows you might like");

    const long Day = 86_400_000L;
    const long Now = 1_758_700_800_000L; // 2026-09-24T12:00:00Z-ish; only relative deltas matter to the planner

    static Zone? Find(IReadOnlyList<Zone> zones, ZoneKind kind) => zones.FirstOrDefaultZone(kind);

    // ── rule 1: New episodes — lead = newest with a video thumb ─────────────────────────────────────────────────

    [Fact]
    public void Lead_IsNewestEpisodeWithVideoThumb_NotSimplyTheNewest()
    {
        TestScope.Fresh();
        var newest = PodcastFx.NewEpisodeSection("Hey Tablo", new PodcastFx.EpisodeSpec("Newest, audio only", Now - Day));
        var older = PodcastFx.NewEpisodeSection("Patrick Boyle On Finance",
            new PodcastFx.EpisodeSpec("Older, has video", Now - 3 * Day, VideoThumbUrl: "https://img/video.jpg"));

        var zones = PodcastPlanner.Plan([newest, older], Titles, Now);

        var lead = Find(zones, ZoneKind.EpisodeLead);
        Assert.NotNull(lead);
        Assert.NotNull(lead!.Lead);
        Assert.Equal("Older, has video", lead.Lead!.Value.Title);
        Assert.Single(lead.Items);
        Assert.Equal("Newest, audio only", lead.Items[0].Title);
    }

    [Fact]
    public void Lead_FallsBackToNewestOverall_WhenNothingHasVideo()
    {
        TestScope.Fresh();
        var a = PodcastFx.NewEpisodeSection("Show A", new PodcastFx.EpisodeSpec("Newest", Now - Day));
        var b = PodcastFx.NewEpisodeSection("Show B", new PodcastFx.EpisodeSpec("Older", Now - 5 * Day));

        var zones = PodcastPlanner.Plan([a, b], Titles, Now);

        var lead = Find(zones, ZoneKind.EpisodeLead);
        Assert.Equal("Newest", lead!.Lead!.Value.Title);
    }

    // ── rule 2: in-progress removed from every other zone, first into Continue listening ───────────────────────

    [Fact]
    public void InProgressEpisode_MovesToContinueListening_AndLeavesEveryOtherZone()
    {
        TestScope.Fresh();
        // Staged as a "New episode from" single, exactly like the real capture's baseline sections — but with
        // playedState IN_PROGRESS, which rule 2 says wins over rule 1 regardless of where the card lives.
        var inProgress = PodcastFx.NewEpisodeSection("Patrick Boyle On Finance",
            new PodcastFx.EpisodeSpec("Scott Bessent Is at War With Prices", Now - 25 * Day, DurationMs: 44 * 60_000, ResumeMs: 8_334, PlayedState: 2));
        var untouched = PodcastFx.NewEpisodeSection("Hey Tablo", new PodcastFx.EpisodeSpec("Hot Take", Now - Day));
        var catchUp = PodcastFx.Section(PodcastPhrases.CatchUpOnYourShows, SectionKind.HomeGeneric,
            new PodcastFx.EpisodeSpec("Hey, Whatever Happened to NFTs?", Now - 150 * Day));

        var zones = PodcastPlanner.Plan([inProgress, untouched, catchUp], Titles, Now);

        var continueZone = Find(zones, ZoneKind.ContinueEpisodes);
        Assert.NotNull(continueZone);
        Assert.Equal(2, continueZone!.Items.Count);
        Assert.Equal("Scott Bessent Is at War With Prices", continueZone.Items[0].Title); // in-progress first
        Assert.Equal("Hey, Whatever Happened to NFTs?", continueZone.Items[1].Title);      // then catch-up

        var lead = Find(zones, ZoneKind.EpisodeLead);
        Assert.All(lead!.Items, c => Assert.NotEqual("Scott Bessent Is at War With Prices", c.Title));
        Assert.NotEqual("Scott Bessent Is at War With Prices", lead.Lead?.Title);
    }

    // ── rule 3: Videos tops up to 4 from the "Episodes you might like" pool, newest video first ─────────────────

    [Fact]
    public void Videos_ToppedUpToFour_FromEpisodePool_AndRemovedFromEpisodeRows()
    {
        TestScope.Fresh();
        var videos = PodcastFx.Section(PodcastPhrases.VideosYouMightLike, SectionKind.HomeGeneric,
            new PodcastFx.EpisodeSpec("Video 1", Now - Day, VideoThumbUrl: "https://img/v1.jpg"),
            new PodcastFx.EpisodeSpec("Video 2", Now - 2 * Day, VideoThumbUrl: "https://img/v2.jpg"));
        var pool = PodcastFx.Section(PodcastPhrases.EpisodesYouMightLike, SectionKind.HomeGeneric,
            new PodcastFx.EpisodeSpec("Pool video, newer", Now - 3 * Day, VideoThumbUrl: "https://img/v3.jpg"),
            new PodcastFx.EpisodeSpec("Pool video, older", Now - 10 * Day, VideoThumbUrl: "https://img/v4.jpg"),
            new PodcastFx.EpisodeSpec("Pool audio only", Now - 4 * Day));

        var zones = PodcastPlanner.Plan([videos, pool], Titles, Now);

        var videoZone = Find(zones, ZoneKind.VideoTiles);
        Assert.NotNull(videoZone);
        Assert.Equal(4, videoZone!.Items.Count);
        var titles = videoZone.Items.Select(c => c.Title).ToList();
        Assert.Contains("Video 1", titles);
        Assert.Contains("Video 2", titles);
        Assert.Contains("Pool video, newer", titles);
        Assert.Contains("Pool video, older", titles);

        // Moved, not copied: a video pulled into VideoTiles is gone from EpisodeRows.
        var rows = Find(zones, ZoneKind.EpisodeRows);
        Assert.NotNull(rows);
        Assert.DoesNotContain(rows!.Items, c => c.Title == "Pool video, newer" || c.Title == "Pool video, older");
        Assert.Contains(rows.Items, c => c.Title == "Pool audio only");
    }

    // ── rule 4: dedupe by uri, then by (title, releasedAtMs) ─────────────────────────────────────────────────────

    [Fact]
    public void EpisodeRows_CollapsesTwoUrisWithTheSameTitleAndReleaseDate()
    {
        TestScope.Fresh();
        long releaseA = Now - 6 * Day;
        var similar = PodcastFx.Section(PodcastPhrases.SimilarToYourInterests, SectionKind.HomeGeneric,
            new PodcastFx.EpisodeSpec("Should markets discount the AI apocalypse?", releaseA, Uri: "spotify:episode:economist-feed-a"),
            new PodcastFx.EpisodeSpec("Should markets discount the AI apocalypse?", releaseA, Uri: "spotify:episode:economist-feed-b"));

        var zones = PodcastPlanner.Plan([similar], Titles, Now);

        var rows = Find(zones, ZoneKind.EpisodeRows);
        Assert.NotNull(rows);
        Assert.Single(rows!.Items);
    }

    // ── rule 5/6: groups under 3 episodes fold into "Episodes you might like" ───────────────────────────────────

    [Fact]
    public void PopularWithListenersOf_FoldsIntoEpisodeRows_WhenUnderThree()
    {
        TestScope.Fresh();
        var group = PodcastFx.Section("Popular with listeners of ColdFusion", SectionKind.HomeGeneric,
            new PodcastFx.EpisodeSpec("Why The Nepal Flood Was Way Scarier", Now - Day),
            new PodcastFx.EpisodeSpec("The most expensive 33 hours", Now - 2 * Day));

        var zones = PodcastPlanner.Plan([group], Titles, Now);

        Assert.Null(Find(zones, ZoneKind.PodcastGroups));
        var rows = Find(zones, ZoneKind.EpisodeRows);
        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Items.Count);
    }

    [Fact]
    public void PopularWithListenersOf_BecomesCluster_AtThreeOrMore_WithAShowsFooter()
    {
        TestScope.Fresh();
        var episodes = PodcastFx.Section("Popular with listeners of Patrick Boyle On Finance", SectionKind.HomeGeneric,
            new PodcastFx.EpisodeSpec("Ep 1", Now - Day), new PodcastFx.EpisodeSpec("Ep 2", Now - 2 * Day),
            new PodcastFx.EpisodeSpec("Ep 3", Now - 3 * Day));
        const string unhedgedUri = "spotify:show:unhedged";
        var showsShelf = PodcastFx.ShowSection("Popular with listeners of Patrick Boyle On Finance",
            new PodcastFx.ShowSpec("Unhedged", Uri: unhedgedUri), new PodcastFx.ShowSpec("The Economics Show"));
        var showsYouMightLike = PodcastFx.ShowSection(PodcastPhrases.ShowsYouMightLike,
            new PodcastFx.ShowSpec("Unhedged", Uri: unhedgedUri)); // same show as the footer thumb — footer must not consume its uri

        var zones = PodcastPlanner.Plan([episodes, showsShelf, showsYouMightLike], Titles, Now);

        var groups = Find(zones, ZoneKind.PodcastGroups);
        Assert.NotNull(groups);
        var cluster = Assert.Single(groups!.Clusters!);
        Assert.Equal("Popular with listeners of", cluster.Over);
        Assert.Equal("Patrick Boyle On Finance", cluster.Name);
        Assert.Equal(3, cluster.Rows.Count);
        Assert.NotNull(cluster.FooterShows);
        Assert.Equal(2, cluster.FooterShows!.Count);

        // The footer thumb ("Unhedged") did not consume its uri: it still shows up in "Shows you might like".
        var showsZone = Find(zones, ZoneKind.ShowGrid);
        Assert.NotNull(showsZone);
        Assert.Contains(showsZone!.Items, c => c.Title == "Unhedged");
    }

    // ── rule 9: "Your shows" is identity — never deduped away ────────────────────────────────────────────────────

    [Fact]
    public void YourShows_KeepsEveryCard_EvenWhenTheSameShowAlsoAppearsElsewhere()
    {
        TestScope.Fresh();
        const string hubermanUri = "spotify:show:huberman-lab";
        var yourShows = PodcastFx.ShowSection(PodcastPhrases.YourShows,
            new PodcastFx.ShowSpec("Huberman Lab", Uri: hubermanUri), new PodcastFx.ShowSpec("ColdFusion"));
        var moreShows = PodcastFx.ShowSection(PodcastPhrases.ShowsYouMightLike,
            new PodcastFx.ShowSpec("Huberman Lab", Uri: hubermanUri));

        var zones = PodcastPlanner.Plan([yourShows, moreShows], Titles, Now);

        var yours = zones.Where(z => z.Key == "podcasts:your-shows").Single();
        Assert.Equal(2, yours.Items.Count);
        Assert.Contains(yours.Items, c => c.Title == "Huberman Lab");

        // Page-wide dedupe (rule 10) still removes the duplicate from the LATER zone.
        var showsYouMightLike = zones.Where(z => z.Key == "podcasts:shows-you-might-like").ToList();
        Assert.Empty(showsYouMightLike); // its only card was already claimed by Your shows → zone renders nothing
    }

    // ── rule 11 + 12: unplayable dropped everywhere; an all-empty document is the single EmptyFacet zone ─────────

    [Fact]
    public void UnplayableCard_NeverAppearsInAnyZone()
    {
        TestScope.Fresh();
        var section = PodcastFx.NewEpisodeSection("Some Show",
            new PodcastFx.EpisodeSpec("Paywalled episode", Now - Day, Unplayable: true));

        var zones = PodcastPlanner.Plan([section], Titles, Now);

        Assert.Single(zones);
        Assert.Equal(ZoneKind.EmptyFacet, zones[0].Kind);
    }

    [Fact]
    public void NoSections_YieldsTheSingleEmptyFacetZone()
    {
        TestScope.Fresh();
        var zones = PodcastPlanner.Plan([], Titles, Now);
        var zone = Assert.Single(zones);
        Assert.Equal(ZoneKind.EmptyFacet, zone.Kind);
    }
}

static class ZoneListExtensions
{
    public static Zone? FirstOrDefaultZone(this IReadOnlyList<Zone> zones, ZoneKind kind)
    {
        foreach (var z in zones) if (z.Kind == kind) return z;
        return null;
    }
}
