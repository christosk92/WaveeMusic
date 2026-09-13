using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The FACET page's composition. A facet is a different document, not the landing page with a filter on it:
/// Spotify renders it as the server's own ordered list of sections. These pin what that means — every titled section
/// survives as its own module, in server order, wearing its own title — and the single exception (a run of consecutive
/// single-card baseline recommendations folds into one discover feed).</summary>
public sealed class HomeFacetProjectionTests
{
    static HomeCard Card(string id, HomeCardKind kind = HomeCardKind.Playlist) =>
        new("spotify:" + id, id, null, null, kind);

    static HomeSection Section(string uri, string? title, params HomeCard[] cards) =>
        new(uri, title, null, cards, cards.Length, cards.Length);

    /// <summary>A composed group as the composer emits it for one section: it carries that section's URI, which is the
    /// only link the projection has back to what the composer decided the section IS.</summary>
    static HomeGroup Group(HomeGroupKind kind, HomeSection section) =>
        new(kind, section.Title, section.Cards, section.Subtitle, section.Uri, section.TotalCount);

    static HomeFeed Feed(HomeSection[] sections, HomeGroup[] groups) =>
        new("", groups, null, sections, "podcasts-chip");

    static IReadOnlyList<HomeFacetRow> Project(HomeFeed feed) =>
        HomeFacetProjection.Rows(feed, HomeModuleTitles.Default);

    [Fact]
    public void Podcasts_KeepsEveryTitledShelfInServerOrder()
    {
        // The bug this exists for: the Podcasts facet returns four separately titled show shelves, and the authored
        // landing merged them all into ONE "Podcasts" module — three server titles deleted from the page.
        var yours = Section("spotify:section:yours", "Your shows",
            Card("show1", HomeCardKind.Podcast), Card("show2", HomeCardKind.Podcast));
        var forYou = Section("spotify:section:for-you", "Podcasts for you", Card("show3", HomeCardKind.Podcast));
        var crime = Section("spotify:section:crime", "Best of true crime",
            Card("show4", HomeCardKind.Podcast), Card("show5", HomeCardKind.Podcast));
        var feed = Feed([yours, forYou, crime],
        [
            Group(HomeGroupKind.PodcastShelf, yours),
            Group(HomeGroupKind.PodcastShelf, forYou),
            Group(HomeGroupKind.PodcastShelf, crime),
        ]);

        var rows = Project(feed);

        Assert.Equal(
            [HomeFacetRowKind.Podcasts, HomeFacetRowKind.Podcasts, HomeFacetRowKind.Podcasts],
            rows.Select(r => r.Kind));
        Assert.Equal(["Your shows", "Podcasts for you", "Best of true crime"], rows.Select(r => r.Group.Title));
        Assert.Equal([2, 1, 2], rows.Select(r => r.Group.Cards.Count));
        // Each row drills into ITS OWN section, and wears the podcast shelf shape.
        Assert.Same(yours, rows[0].Section);
        Assert.Same(forYou, rows[1].Section);
        Assert.Same(crime, rows[2].Section);
        Assert.All(rows, r => Assert.Equal(HomeGroupKind.PodcastShelf, r.Group.Kind));
    }

    [Fact]
    public void ConsecutiveBaselines_CoalesceIntoOneFeedRow()
    {
        // Twenty single-card "because you listened to X" sections in a row is not twenty shelves. A RUN folds into one
        // paged discover feed wearing the app's own copy; a titled section closes the run, so the server's order is
        // never rewritten.
        var top = Section("spotify:section:top", "Top picks", Card("p1"), Card("a1", HomeCardKind.Album));
        var b1 = Section("spotify:section:b1", "Because you listened to IU", Card("b1"));
        var b2 = Section("spotify:section:b2", "Because you listened to GFRIEND", Card("b2"));
        var b3 = Section("spotify:section:b3", "More like NewJeans", Card("b3"));
        var tail = Section("spotify:section:tail", "New releases", Card("p2"), Card("a2", HomeCardKind.Album));
        var feed = Feed([top, b1, b2, b3, tail],
        [
            Group(HomeGroupKind.QuickGrid, top),
            Group(HomeGroupKind.DiscoverFeed, b1),
            Group(HomeGroupKind.DiscoverFeed, b2),
            Group(HomeGroupKind.DiscoverFeed, b3),
            Group(HomeGroupKind.QuickGrid, tail),
        ]);

        var rows = Project(feed);

        Assert.Equal([HomeFacetRowKind.Shelf, HomeFacetRowKind.Feed, HomeFacetRowKind.Shelf], rows.Select(r => r.Kind));
        var discover = rows[1];
        Assert.Equal(["spotify:b1", "spotify:b2", "spotify:b3"], discover.Group.Cards.Select(c => c.Uri));
        Assert.Equal(HomeGroupKind.DiscoverFeed, discover.Group.Kind);
        Assert.Equal(HomeModuleTitles.Default.BecauseYouListened, discover.Group.Title);
        Assert.Equal(3, discover.Group.TotalCount);
        // The coalesced feed is not ONE section, so it has nothing honest to drill into.
        Assert.Null(discover.Section);
    }

    [Fact]
    public void TwoBaselineRunsSeparatedByASection_StayTwoFeedRows()
    {
        var b1 = Section("spotify:section:b1", "Because you listened to IU", Card("b1"));
        var b2 = Section("spotify:section:b2", "Because you listened to BOL4", Card("b2"));
        var shelf = Section("spotify:section:shelf", "Your shows", Card("show", HomeCardKind.Podcast));
        var b3 = Section("spotify:section:b3", "More like NewJeans", Card("b3"));
        var feed = Feed([b1, b2, shelf, b3],
        [
            Group(HomeGroupKind.DiscoverFeed, b1),
            Group(HomeGroupKind.DiscoverFeed, b2),
            Group(HomeGroupKind.PodcastShelf, shelf),
            Group(HomeGroupKind.DiscoverFeed, b3),
        ]);

        var rows = Project(feed);

        Assert.Equal([HomeFacetRowKind.Feed, HomeFacetRowKind.Podcasts, HomeFacetRowKind.Feed],
            rows.Select(r => r.Kind));
        Assert.Equal(["spotify:b1", "spotify:b2"], rows[0].Group.Cards.Select(c => c.Uri));
        Assert.Equal(["spotify:b3"], rows[2].Group.Cards.Select(c => c.Uri));
    }

    [Fact]
    public void Spotlight_OneCardHeroSection_IsAHeroRow()
    {
        var spotlight = Section("spotify:section:spotlight", "Spotlight", Card("daylist"));
        var hero = Group(HomeGroupKind.Hero, spotlight);
        var shows = Section("spotify:section:shows", "Your shows", Card("show", HomeCardKind.Podcast));
        var feed = Feed([spotlight, shows], [hero, Group(HomeGroupKind.PodcastShelf, shows)]);

        var rows = Project(feed);

        Assert.Equal([HomeFacetRowKind.Hero, HomeFacetRowKind.Podcasts], rows.Select(r => r.Kind));
        // The hero row renders the composer's OWN hero group — the band reads Meta/format off that card.
        Assert.Same(hero, rows[0].Group);
        Assert.Same(spotlight, rows[0].Section);
    }

    [Fact]
    public void MixedSection_IsAGenericShelf()
    {
        // Playlists and albums together name no single module. On the landing they were split per card kind and the
        // section's own title went to whichever half was dominant; a facet keeps the section whole, under its title.
        var mixed = Section("spotify:section:mixed", "Made for you",
            Card("p1"), Card("a1", HomeCardKind.Album), Card("p2"));
        var feed = Feed([mixed with { TotalCount = 30 }], [Group(HomeGroupKind.QuickGrid, mixed)]);

        var row = Assert.Single(Project(feed));

        Assert.Equal(HomeFacetRowKind.Shelf, row.Kind);
        Assert.Equal(HomeGroupKind.Shelf, row.Group.Kind);
        Assert.Equal("Made for you", row.Group.Title);
        Assert.Equal("spotify:section:mixed", row.Group.Uri);
        // The server's own total survives, so the shelf's "show all" knows there is more than the page it holds.
        Assert.Equal(30, row.Group.TotalCount);
        Assert.Equal(["spotify:p1", "spotify:a1", "spotify:p2"], row.Group.Cards.Select(c => c.Uri));
    }

    [Fact]
    public void AllAudiobooks_IsAudiobooks_AllEpisodes_IsEpisodes()
    {
        var books = Section("spotify:section:books", "Audiobooks for you",
            Card("b1", HomeCardKind.Audiobook), Card("b2", HomeCardKind.Audiobook));
        var episodes = Section("spotify:section:episodes", "Episodes for you",
            Card("e1", HomeCardKind.Episode), Card("e2", HomeCardKind.Episode), Card("e3", HomeCardKind.Episode));
        var feed = Feed([books, episodes],
            [Group(HomeGroupKind.RatedShelf, books), Group(HomeGroupKind.QueueList, episodes)]);

        var rows = Project(feed);

        Assert.Equal([HomeFacetRowKind.Audiobooks, HomeFacetRowKind.Episodes], rows.Select(r => r.Kind));
        Assert.Equal(HomeGroupKind.RatedShelf, rows[0].Group.Kind);
        Assert.Equal(HomeGroupKind.QueueList, rows[1].Group.Kind);
        Assert.Equal(["Audiobooks for you", "Episodes for you"], rows.Select(r => r.Group.Title));
    }

    [Fact]
    public void EmptySections_AreSkipped()
    {
        // The ledger is lossless, so it holds sections whose every item was unsupported. A row that renders nothing is
        // still a header and a module gap, which is worse than the section not being there.
        var first = Section("spotify:section:first", "Your shows", Card("show", HomeCardKind.Podcast));
        var empty = new HomeSection("spotify:section:empty", "Nothing here", null, [], 0, 4, UnsupportedCount: 4);
        var last = Section("spotify:section:last", "New releases", Card("a", HomeCardKind.Album));
        var feed = Feed([first, empty, last],
        [
            Group(HomeGroupKind.PodcastShelf, first),
            new HomeGroup(HomeGroupKind.SectionEntry, empty.Title, empty.Cards, null, empty.Uri, 0),
            Group(HomeGroupKind.QuickGrid, last),
        ]);

        var rows = Project(feed);

        Assert.Equal([HomeFacetRowKind.Podcasts, HomeFacetRowKind.Shelf], rows.Select(r => r.Kind));
        Assert.Equal(["Your shows", "New releases"], rows.Select(r => r.Group.Title));
    }

    [Fact]
    public void NoSectionsLedger_FallsBackToGroups()
    {
        // A source that publishes presentation groups only (or the loading seed) has no ledger to walk. The groups are
        // then the order, each one its own row, and no row can drill into a section that does not exist.
        var shows = new HomeGroup(HomeGroupKind.PodcastShelf, "Your shows",
            [Card("show1", HomeCardKind.Podcast), Card("show2", HomeCardKind.Podcast)]);
        var b1 = new HomeGroup(HomeGroupKind.DiscoverFeed, "Because you listened to IU", [Card("b1")]);
        var b2 = new HomeGroup(HomeGroupKind.DiscoverFeed, "More like NewJeans", [Card("b2")]);
        var picks = new HomeGroup(HomeGroupKind.QuickGrid, "Jump back in", [Card("p1"), Card("a1", HomeCardKind.Album)]);
        var empty = new HomeGroup(HomeGroupKind.QuickGrid, "Nothing", []);
        var feed = new HomeFeed("", [shows, b1, b2, picks, empty], Facet: "music-chip");

        var rows = Project(feed);

        Assert.Equal([HomeFacetRowKind.Podcasts, HomeFacetRowKind.Feed, HomeFacetRowKind.Shelf],
            rows.Select(r => r.Kind));
        Assert.Same(shows, rows[0].Group);
        Assert.Equal(["spotify:b1", "spotify:b2"], rows[1].Group.Cards.Select(c => c.Uri));
        Assert.Same(picks, rows[2].Group);
        Assert.All(rows, r => Assert.Null(r.Section));
    }
}
