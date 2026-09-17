// ── Wavee.Tests/HomeComposerTests.cs — the composer over committed rows (Wave 5, owner P; ported from 0.2.9) ─────────────
//
// 0.2.9's `SpotifyHomeComposerTests` fed inline home JSON through the DOM composer. The same fixtures now go through the
// 0.3 path end to end: `Spotify.Decode.HomeFeed` stages the document, `Entities.Commit` lands it, and `HomeComposer`
// composes the Home ROW — so a fact that passes here passes for the live answer and the --fake capture alike.
//
// Two mechanical changes to the fixtures, both 0.3 contracts rather than behaviour changes: every section carries a
// `uri` (a section is a ROW, and a row with no identity is not staged — `StagedRows.Settle`), and the document is wrapped
// as the whole answer (`{"data":{"home":…}}`). Card assertions read the HANDLE's live properties (0.2.9 `card.Meta.X` →
// `card.X`).

using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class HomeComposerTests
{
    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A fresh scope, the document decoded + committed on the unfiltered feed row, the row composed.</summary>
    static HomeFeedView Compose(string homeJson, HomeModuleTitles? titles = null)
    {
        TestScope.Fresh();
        Decode(homeJson);
        return HomeComposer.Compose(Entities.HomeFeed(), titles ?? HomeModuleTitles.Default);
    }

    /// <summary>Decode + commit into the CURRENT scope (a second document for the same subject replaces the first).</summary>
    static void Decode(string homeJson)
    {
        var s = Staging.Rent();
        Spotify.Decode.HomeFeed(Encoding.UTF8.GetBytes("{\"data\":{\"home\":" + homeJson + "}}"), "wavee:home"u8, s);
        TestScope.CommitAndPublish(s);
    }

    static int s_sectionId;

    /// <summary>A section uri unique within the process (a band is a row keyed by it).</summary>
    static string SectionUri() => "spotify:section:composer-" + Interlocked.Increment(ref s_sectionId);

    static string Home(params string[] sections) =>
        "{ \"sectionContainer\": { \"sections\": { \"items\": [" + string.Join(",", sections) + "] } } }";

    static string Str(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

    /// <summary>A generic section of playlists, each `(uri, format)`.</summary>
    static string Generic(string title, params (string Uri, string Format)[] playlists)
    {
        var items = playlists.Select(p =>
            $$"""
            { "content": { "data": {
                "__typename": "Playlist", "uri": "{{p.Uri}}", "name": "{{p.Uri}}",
                "format": "{{p.Format}}", "content": { "totalCount": 50 } } } }
            """);
        return $$"""
        { "uri": "{{SectionUri()}}", "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": {{Str(title)}} } },
          "sectionItems": { "items": [ {{string.Join(",", items)}} ] } }
        """;
    }

    static HomeGroup Single(HomeFeedView c, HomeGroupKind kind) => Assert.Single(c.Groups, g => g.Kind == kind);

    // ── the recents shelf's liked entry ────────────────────────────────────────────────────────────────────────────

    /// <summary>Spotify's stock purple-heart PNG — the url the measured recently-played section carries for Liked Songs.</summary>
    const string LikedStockCoverUrl = "https://misc.scdn.co/liked-songs/liked-songs-300.png";

    static string RecentsSection(string title, params string[] entities) =>
        $$"""
        { "uri": "{{SectionUri()}}", "data": { "__typename": "HomeRecentlyPlayedSectionData", "title": { "transformedLabel": "{{title}}" } },
          "sectionItems": { "items": [ { "content": { "data": {
              "__typename": "List",
              "items": { "totalCount": {{entities.Length}}, "items": [ {{string.Join(",", entities)}} ] } } } } ] } }
        """;

    static string RecentEntity(string uri, string name, string type, string? imageUrl)
    {
        string visual = imageUrl is null ? "" : $$"""
            , "visualIdentityTrait": { "squareCoverImage": { "image": { "data": {
                "sources": [ { "url": "{{imageUrl}}" } ] } } } }
            """;
        return $$"""
        { "entity": { "__typename": "EntityResponseWrapper", "_uri": "{{uri}}", "data": {
            "uri": "{{uri}}",
            "entityTypeTrait": { "type": "{{type}}" },
            "identityTrait": { "name": "{{name}}", "type": "Playlist",
              "contributors": { "items": [ { "name": "Spotify", "uri": "spotify:user:spotify" } ], "totalCount": 1 } }
            {{visual}} } } }
        """;
    }

    /// <summary>Liked Songs arrives in the recents shelf as a PLAYLIST entity carrying the stock cover. The honest mapping
    /// is the canonical uri, the Liked kind, and NO image. (Needs the reported `Edges.Staging.cs` SectionCards patch: the
    /// card edge must resolve a collection target in the playlist table.)</summary>
    [Fact]
    public void RecentsShelf_MapsLikedSongsAsTheCollection_AndDropsTheProvidersStockCover()
    {
        var c = Compose(Home(RecentsSection("Recents",
            RecentEntity("spotify:collection:tracks", "Liked Songs", "ENTITY_TYPE_PLAYLIST", LikedStockCoverUrl),
            RecentEntity("spotify:playlist:p1", "A Playlist", "ENTITY_TYPE_PLAYLIST", "https://i.scdn.co/image/p1"))));

        var recents = Single(c, HomeGroupKind.Recents);
        var liked = Assert.Single(recents.Cards, x => x.Kind == HomeCardKind.Liked);
        Assert.Equal("spotify:collection:tracks", liked.Uri);
        Assert.Null(liked.ImageUrl);

        var plain = Assert.Single(recents.Cards, x => x.Kind == HomeCardKind.Playlist);
        Assert.Equal("spotify:playlist:p1", plain.Uri);
        Assert.Equal("https://i.scdn.co/image/p1", plain.ImageUrl);
    }

    /// <summary>The user-namespaced spelling is the SAME entity and folds to the canonical uri. (Same patch dependency.)</summary>
    [Fact]
    public void RecentsShelf_FoldsTheUserNamespacedCollectionSpelling()
    {
        var c = Compose(Home(RecentsSection("Recents",
            RecentEntity("spotify:user:abc123:collection", "Liked Songs", "ENTITY_TYPE_PLAYLIST", LikedStockCoverUrl))));

        var liked = Assert.Single(Single(c, HomeGroupKind.Recents).Cards);
        Assert.Equal(HomeCardKind.Liked, liked.Kind);
        Assert.Equal("spotify:collection:tracks", liked.Uri);
        Assert.Null(liked.ImageUrl);
    }

    [Fact]
    public void LiveShapedPayload_AccountsForEveryCardAndEverySectionTitle()
    {
        // 21 sections, 185 mappable cards and 20 server titles: every card survives on the ledger and every title has
        // exactly one owning group.
        var sections = new List<string>(21);
        var sourceTitles = new List<string>(20);
        int card = 0;
        int section = 0;

        void Add(string? title, params string[] formats)
        {
            section++;
            string label = title ?? "";
            if (label.Length > 0) sourceTitles.Add(label);
            var cards = new (string Uri, string Format)[formats.Length];
            for (int i = 0; i < formats.Length; i++)
                cards[i] = ($"spotify:playlist:fixture-{++card:000}", formats[i]);
            sections.Add(Generic(label, cards));
        }

        for (int i = 0; i < 9; i++) Add($"Source {section + 1:00}", Enumerable.Repeat("editorial", 7).ToArray());
        for (int i = 0; i < 2; i++) Add($"Source {section + 1:00}", Enumerable.Repeat("editorial", 6).ToArray());
        Add($"Source {section + 1:00}", ["editorial", "editorial", .. Enumerable.Repeat("", 8)]);
        Add(null, Enumerable.Repeat("", 12).ToArray());
        Add($"Source {section + 1:00}", Enumerable.Repeat("", 12).ToArray());
        Add($"Source {section + 1:00}", Enumerable.Repeat("", 12).ToArray());
        Add($"Source {section + 1:00}", Enumerable.Repeat("", 13).ToArray());
        Add($"Source {section + 1:00}", Enumerable.Repeat("daily-mix", 11).ToArray());
        Add($"Source {section + 1:00}", Enumerable.Repeat("inspiredby-mix", 10).ToArray());
        Add($"Source {section + 1:00}", Enumerable.Repeat("topic-mix", 10).ToArray());
        Add($"Source {section + 1:00}", Enumerable.Repeat("discover-weekly", 10).ToArray());
        Add($"Source {section + 1:00}", Enumerable.Repeat("release-radar", 10).ToArray());

        Assert.Equal(21, sections.Count);
        Assert.Equal(185, card);
        Assert.Equal(20, sourceTitles.Count);

        var composed = Compose(Home(sections.ToArray()));
        int survivingCards = composed.Sections!.Sum(s => s.Cards.Count);
        int survivingTitles = sourceTitles.Count(t => composed.Groups.Count(g => g.Title == t) == 1);

        Assert.Equal((Cards: 185, Titles: 20), (Cards: survivingCards, Titles: survivingTitles));
    }

    // ── format → module ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("daily-mix", HomeGroupKind.MixBand)]
    [InlineData("inspiredby-mix", HomeGroupKind.RadioDial)]
    [InlineData("topic-mix", HomeGroupKind.ChipCards)]
    [InlineData("artist-mix-reader", HomeGroupKind.ChipCards)]
    [InlineData("editorial", HomeGroupKind.Topic)]
    [InlineData("format-shows-shuffle", HomeGroupKind.Topic)]
    [InlineData("discover-weekly", HomeGroupKind.WeeklyPair)]
    [InlineData("release-radar", HomeGroupKind.WeeklyPair)]
    [InlineData("artistsets", HomeGroupKind.Topic)]
    [InlineData("descripto", HomeGroupKind.Topic)]
    [InlineData("", HomeGroupKind.QuickGrid)]
    public void PlaylistFormat_SelectsTheModule(string format, HomeGroupKind expected)
    {
        var g = Single(Compose(Home(Generic("Shelf", ("spotify:playlist:A", format), ("spotify:playlist:B", format)))), expected);
        Assert.Equal(2, g.Cards.Count);
        Assert.Equal(format.Length == 0 ? null : format, g.Cards[0].Format);
    }

    [Fact]
    public void ModuleShape_DoesNotDependOnTheLocalizedSectionTitle()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:made-for", "data": { "__typename": "HomeGenericSectionData", "title": {
                "transformedLabel": "Speciaal voor Christos", "translatedBaseText": "Made For {0}" } },
            "sectionItems": { "items": [
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:M1", "name": "Mix one", "format": "daily-mix" } } },
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:M2", "name": "Mix two", "format": "daily-mix" } } }
            ] } }
        ] } } }
        """;
        var g = Single(Compose(json), HomeGroupKind.MixBand);
        Assert.Equal("Speciaal voor Christos", g.Title);
    }

    // ── bucketing across sections ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneSection_SplitsAcrossTheModulesItsFormatsName()
    {
        var c = Compose(Home(Generic("Made For Christos",
            ("spotify:playlist:D1", "daily-mix"), ("spotify:playlist:D2", "daily-mix"),
            ("spotify:playlist:D3", "daily-mix"), ("spotify:playlist:D4", "daily-mix"),
            ("spotify:playlist:D5", "daily-mix"), ("spotify:playlist:D6", "daily-mix"),
            ("spotify:playlist:DW", "discover-weekly"), ("spotify:playlist:RR", "release-radar"))));

        Assert.Equal(6, Single(c, HomeGroupKind.MixBand).Cards.Count);
        var weekly = Single(c, HomeGroupKind.WeeklyPair);
        Assert.Equal(2, weekly.Cards.Count);
        Assert.Equal("spotify:playlist:DW", weekly.Cards[0].Uri);
        Assert.Equal("spotify:playlist:RR", weekly.Cards[1].Uri);
        Assert.Null(weekly.Title);
    }

    [Fact]
    public void SeveralSections_KeepSeparateSourceOwnedModuleGroups()
    {
        var c = Compose(Home(
            Generic("Recommended Stations", ("spotify:playlist:R1", "inspiredby-mix"), ("spotify:playlist:R2", "inspiredby-mix")),
            Generic("Popular radio", ("spotify:playlist:P1", "inspiredby-mix"), ("spotify:playlist:P2", "inspiredby-mix")),
            Generic("Jump back in", ("spotify:playlist:J1", "inspiredby-mix"))));

        var dials = c.Groups.Where(g => g.Kind == HomeGroupKind.RadioDial).ToArray();
        Assert.Equal(3, dials.Length);
        Assert.Equal(new[] { "Recommended Stations", "Popular radio", "Jump back in" }, dials.Select(g => g.Title));
        Assert.Equal(5, dials.Sum(g => g.Cards.Count));
    }

    [Fact]
    public void MixedEntitySection_DistributesEachCardToItsOwnModule()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:jump", "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Jump back in" } },
            "sectionItems": { "items": [
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:J1", "name": "p1", "format": "inspiredby-mix" } } },
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:J2", "name": "p2", "format": "inspiredby-mix" } } },
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:J3", "name": "p3", "format": "" } } },
              { "content": { "data": { "__typename": "Artist", "uri": "spotify:artist:J5", "profile": { "name": "a1" } } } },
              { "content": { "data": { "__typename": "Album", "uri": "spotify:album:J7", "name": "al" } } },
              { "content": { "data": { "__typename": "Podcast", "uri": "spotify:show:J8", "name": "pod", "publisher": { "name": "pub" } } } }
            ] } }
        ] } } }
        """;
        var c = Compose(json);
        Assert.Equal(2, Single(c, HomeGroupKind.RadioDial).Cards.Count);
        var quick = Single(c, HomeGroupKind.QuickGrid);
        Assert.Equal(3, quick.Cards.Count);
        Assert.Equal(HomeGroupKind.PodcastShelf, Single(c, HomeGroupKind.PodcastShelf).Kind);
        Assert.Contains(quick.Cards, x => x.Kind == HomeCardKind.Artist);
        Assert.Contains(quick.Cards, x => x.Kind == HomeCardKind.Album);
    }

    // ── the shelves that used to vanish ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AudiobookSection_BecomesARatedShelf_WithRatingAuthorAndLength()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:books", "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Audiobooks for you" } },
            "sectionItems": { "items": [
              { "content": { "data": {
                  "__typename": "Audiobook", "uri": "spotify:show:B1", "name": "How to Hold a Cockroach",
                  "authorsV2": [ { "name": "Matthew Maxwell" } ],
                  "rating": { "averageRating": { "average": 4.568880688806883, "showAverage": true } },
                  "audiobookDuration": { "totalMilliseconds": 4551407 },
                  "accessInfo": { "signifier": { "text": "Included in Premium" } },
                  "coverArt": { "extractedColors": { "colorDark": { "hex": "#7B776E", "isFallback": false } } } } } },
              { "content": { "data": {
                  "__typename": "Audiobook", "uri": "spotify:show:B2", "name": "Second book",
                  "authorsV2": [ { "name": "Another Author" } ],
                  "rating": { "averageRating": { "average": 3.5, "showAverage": false } },
                  "audiobookDuration": { "totalMilliseconds": 7200000 } } } }
            ] } }
        ] } } }
        """;
        var g = Single(Compose(json), HomeGroupKind.RatedShelf);
        Assert.Equal("Audiobooks for you", g.Title);
        Assert.Equal(2, g.Cards.Count);

        var first = g.Cards[0];
        Assert.Equal(HomeCardKind.Audiobook, first.Kind);
        Assert.Equal("Matthew Maxwell", first.Author);
        Assert.Equal("Matthew Maxwell", first.Subtitle);
        Assert.Equal(4.57, first.Rating, 2);
        Assert.Equal(4551407L, first.DurationMs);
        Assert.Equal("Included in Premium", first.Signifier);
        Assert.Equal(0xFF7B776Eu, first.Accent);
        // showAverage:false — the server sent an average it does not want displayed.
        Assert.Equal(0d, g.Cards[1].Rating);
    }

    [Fact]
    public void EpisodeSection_BecomesAQueueList_WithDurationShowVideoAndResume()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:episodes", "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Episodes you might like" } },
            "sectionItems": { "items": [
              { "content": { "data": {
                  "__typename": "Episode", "uri": "spotify:episode:E1", "name": "Unsexy Habits",
                  "duration": { "totalMilliseconds": 1028713 },
                  "mediaTypes": [ "AUDIO", "VIDEO" ],
                  "playedState": { "playPositionMilliseconds": 514356, "state": "IN_PROGRESS" },
                  "podcastV2": { "data": { "__typename": "Podcast", "name": "theMITmonk", "uri": "spotify:show:S1" } },
                  "coverArt": { "extractedColors": { "colorDark": { "hex": "#8058F8", "isFallback": false } } } } } },
              { "content": { "data": {
                  "__typename": "Episode", "uri": "spotify:episode:E2", "name": "Audio only",
                  "duration": { "totalMilliseconds": 600000 },
                  "mediaTypes": [ "AUDIO" ],
                  "playedState": { "playPositionMilliseconds": 0, "state": "NOT_STARTED" },
                  "podcastV2": { "data": { "__typename": "Podcast", "name": "Some show", "uri": "spotify:show:S2" } } } } }
            ] } }
        ] } } }
        """;
        var g = Single(Compose(json), HomeGroupKind.QueueList);
        Assert.Equal(2, g.Cards.Count);

        var first = g.Cards[0];
        Assert.Equal(HomeCardKind.Episode, first.Kind);
        Assert.Equal("theMITmonk", first.Subtitle);
        Assert.Equal(1028713L, first.DurationMs);
        Assert.Equal(514356L, first.ResumeMs);
        Assert.True(first.HasVideo);
        Assert.Equal(0xFF8058F8u, first.Accent);

        var second = g.Cards[1];
        Assert.False(second.HasVideo);
        Assert.Equal(0L, second.ResumeMs);
    }

    // ── shorts / hero ─────────────────────────────────────────────────────────────────────────────────────────────

    const string ShortsWithDaylist = """
    { "uri": "spotify:section:shorts", "data": { "__typename": "HomeShortsSectionData" },
      "sectionItems": { "items": [
        { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:S1", "name": "Millennium K-Pop", "format": "editorial" } } },
        { "content": { "data": {
            "__typename": "Playlist", "uri": "spotify:playlist:DAY", "name": "puppy love hollywood sunday afternoon",
            "format": "daylist",
            "attributes": [ { "key": "localized_terms", "value": "puppy love,hollywood,happy pop" } ] } } },
        { "content": { "data": { "__typename": "Album", "uri": "spotify:album:S3", "name": "fade away" } } }
      ] } }
    """;

    [Fact]
    public void ShortsSection_IsNoLongerSkipped_AndItsCardsFollowTheirOwnFormats()
    {
        var c = Compose(Home(ShortsWithDaylist));
        Assert.Equal("spotify:playlist:S1", Assert.Single(Single(c, HomeGroupKind.Featured).Cards).Uri);
        Assert.Equal("spotify:album:S3", Assert.Single(Single(c, HomeGroupKind.QuickGrid).Cards).Uri);
        Assert.Equal("spotify:playlist:DAY", Assert.Single(Single(c, HomeGroupKind.Hero).Cards).Uri);
    }

    [Fact]
    public void ShortsSection_IsItsOwnKind_NeverABaselineBand()
    {
        // 0.3: B1's fold filed HomeShortsSectionData as a BASELINE band, which stamped every shorts card with an eyebrow.
        var c = Compose(Home(ShortsWithDaylist));
        Assert.Equal(SectionKind.HomeShorts, Entities.Section("spotify:section:shorts".AsSpan()).Kind);
        Assert.All(c.Groups.SelectMany(g => g.Cards), card => Assert.Null(card.Eyebrow));
    }

    [Fact]
    public void Daylist_IsPromotedToTheHero_FromWhereverItArrives()
    {
        var card = Assert.Single(Single(Compose(Home(ShortsWithDaylist)), HomeGroupKind.Hero).Cards);
        Assert.Equal("daylist", card.Format);
        // A daylist's tags come from its localized_terms attribute — a clean comma list — not from parsing its prose.
        Assert.Equal(new[] { "puppy love", "hollywood", "happy pop" }, card.Seeds);
    }

    static HomeCard MapOne(string cardJson)
    {
        var c = Compose(Home("{ \"uri\": \"" + SectionUri() + "\", \"data\": { \"__typename\": \"HomeGenericSectionData\" },"
            + " \"sectionItems\": { \"items\": [ { \"content\": { \"data\": " + cardJson + " } } ] } }"));
        return Assert.Single(c.Sections!.SelectMany(s => s.Cards));
    }

    [Fact]
    public void DaylistHydrationMarker_UsesTheProviderPretitle_NotANameGuess()
    {
        static HomeCard Map(string name, string format, string pretitle) => MapOne($$"""
            { "__typename": "Playlist", "uri": "spotify:playlist:DAY", "name": "{{name}}",
              "format": "{{format}}", "attributes": [ { "key": "daylist_pretitle", "value": "{{pretitle}}" } ] }
            """);

        var shallow = Map("daylist", "daylist", "daylist");
        Assert.True(shallow.NeedsHydration);
        Assert.Equal("daylist", shallow.GenericTitle);

        var exact = Map("teen pop mid 2010s friday afternoon", "daylist", "daylist");
        Assert.False(exact.NeedsHydration);
        Assert.Equal("daylist", exact.GenericTitle);

        var ordinary = Map("daylist", "editorial", "daylist");
        Assert.False(ordinary.NeedsHydration);
        Assert.Null(ordinary.GenericTitle);
    }

    [Fact]
    public void DaylistHydration_IsLive_TheSameHandleReleasesWhenTheHeaderLands()
    {
        // 0.2.9's HomeDaylistHydrator re-read the header and re-composed Home. In 0.3 the card is a HANDLE: when the exact
        // playlist header commits, the card already in the composed view re-describes itself — no requery, no recompose.
        var shallow = MapOne("""
            { "__typename": "Playlist", "uri": "spotify:playlist:DAY", "name": "daylist",
              "format": "daylist", "attributes": [ { "key": "daylist_pretitle", "value": "daylist" } ] }
            """);
        Assert.True(shallow.NeedsHydration);

        var s = Staging.Rent();
        ref var header = ref s.Playlists.RowFor(new StagedId(s.Text("spotify:playlist:DAY")), Authority.Full,
            (uint)PlaylistFields.Identity);
        header.Title = s.Text("teen pop mid 2010s friday afternoon");
        TestScope.CommitAndPublish(s);

        Assert.Equal("teen pop mid 2010s friday afternoon", shallow.Title);
        Assert.False(shallow.NeedsHydration);
    }

    [Fact]
    public void Daylist_MapsExpiresCreatedAndHeaderImageFromAttributes()
    {
        const string expires = "2026-08-11T23:58:59.559688264Z";
        const string created = "2026-08-11T20:58:59.559688264Z";
        const string header = "https://daylist.spotifycdn.com/headers/desktop/night.jpg";
        var daylist = MapOne($$"""
            { "__typename": "Playlist", "uri": "spotify:playlist:DAY", "name": "night drive",
              "format": "daylist",
              "attributes": [
                { "key": "expires", "value": "{{expires}}" },
                { "key": "created", "value": "{{created}}" },
                { "key": "header_image_url_desktop", "value": "{{header}}" }
              ] }
            """);
        // The playlist table stores the window in UNIX SECONDS (Playlist.cs), so the millisecond reading is whole seconds.
        static long SecondsAsMs(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
            .ToUnixTimeSeconds() * 1000L;
        Assert.Equal(SecondsAsMs(expires), daylist.ExpiresAtMs);
        Assert.Equal(SecondsAsMs(created), daylist.CreatedAtMs);
        Assert.Equal(header, daylist.HeaderImageUrl);

        var plain = MapOne("""
            { "__typename": "Playlist", "uri": "spotify:playlist:P", "name": "Mine", "format": "editorial",
              "attributes": [
                { "key": "expires", "value": "2026-08-11T23:58:59.559688264Z" },
                { "key": "header_image_url_desktop", "value": "https://daylist.spotifycdn.com/headers/desktop/night.jpg" }
              ] }
            """);
        Assert.Equal(0L, plain.ExpiresAtMs);
        Assert.Equal(0L, plain.CreatedAtMs);
        Assert.Null(plain.HeaderImageUrl);
    }

    const string Spotlight = """
    { "uri": "spotify:section:spotlight", "data": { "__typename": "HomeSpotlightSectionData", "title": { "transformedLabel": "Spotlight" } },
      "sectionItems": { "items": [ { "content": { "data": { "__typename": "Album", "uri": "spotify:album:S", "name": "Spot" } } } ] } }
    """;

    [Fact]
    public void Spotlight_PrecedesTheDaylistHero_WithoutDeletingItsSection()
    {
        var c = Compose(Home(Spotlight, ShortsWithDaylist));
        var heroes = c.Groups.Where(g => g.Kind == HomeGroupKind.Hero).ToArray();
        Assert.Equal(2, heroes.Length);
        Assert.Equal("spotify:album:S", Assert.Single(heroes[0].Cards).Uri);
        Assert.Equal("spotify:playlist:DAY", Assert.Single(heroes[1].Cards).Uri);
        Assert.Contains(c.Sections!, s => s.Cards.Any(x => x.Uri == "spotify:playlist:DAY"));
    }

    [Fact]
    public void HeroPreviewCapDoesNotDeleteAdditionalDaylistsFromCore()
    {
        var c = Compose(Home(Spotlight,
            Generic("Made For Christos", ("spotify:playlist:DAY1", "daylist"), ("spotify:playlist:DAY2", "daylist"))));

        var heroes = c.Groups.Where(g => g.Kind == HomeGroupKind.Hero).ToArray();
        Assert.Equal(2, heroes.Length);
        Assert.Equal("spotify:album:S", Assert.Single(heroes[0].Cards).Uri);
        Assert.Equal(2, heroes[1].Cards.Count);
        Assert.Equal(3, c.Sections!.Sum(s => s.Cards.Count));
    }

    // ── baseline recommendations ──────────────────────────────────────────────────────────────────────────────────

    static string Baseline(string title, string uri) => $$"""
    { "uri": "{{SectionUri()}}", "data": { "__typename": "HomeFeedBaselineSectionData", "title": { "transformedLabel": "{{title}}" } },
      "sectionItems": { "items": [ { "content": { "data": {
          "__typename": "Playlist", "uri": "{{uri}}", "name": "{{uri}}", "format": "editorial" } } } ] } }
    """;

    [Fact]
    public void BaselineSections_KeepOneTitleOwnerEach_AndRenderAsOneFeedRow()
    {
        var c = Compose(Home(
            Baseline("For fans of IU", "spotify:playlist:X1"),
            Generic("Some shelf", ("spotify:playlist:S1", "editorial"), ("spotify:playlist:S2", "editorial")),
            Baseline("More like GFRIEND", "spotify:playlist:X2"),
            Baseline("Based on your recent listening", "spotify:playlist:X3")));

        var feeds = c.Groups.Where(g => g.Kind == HomeGroupKind.DiscoverFeed).ToArray();
        Assert.Equal(3, feeds.Length);
        Assert.Equal(new[] { "For fans of IU", "More like GFRIEND", "Based on your recent listening" }, feeds.Select(g => g.Title));
        Assert.Equal(new[] { "For fans of IU", "More like GFRIEND", "Based on your recent listening" },
            feeds.SelectMany(g => g.Cards).Select(x => x.Eyebrow));
        Assert.Equal(2, Single(c, HomeGroupKind.Topic).Cards.Count);
    }

    [Fact]
    public void DuplicateCards_SurviveAcrossSections_ButNotInsideOneSection()
    {
        var c = Compose(Home(
            Generic("For fans of IU", ("spotify:playlist:DUP", "editorial"), ("spotify:playlist:OTHER", "editorial")),
            Baseline("For fans of IU", "spotify:playlist:DUP")));

        Assert.Equal(2, c.Sections!.Count);
        Assert.Contains(c.Sections[0].Cards, x => x.Uri == "spotify:playlist:DUP");
        Assert.Contains(c.Sections[1].Cards, x => x.Uri == "spotify:playlist:DUP");
        Assert.Contains(c.Groups, g => g.Kind == HomeGroupKind.DiscoverFeed);
    }

    // ── recents ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecentlyPlayed_RendersInlineFromHomeResponse_AsItsOwnKind()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
            { "uri": "spotify:section:recents", "data": { "__typename": "HomeRecentlyPlayedSectionData", "title": { "transformedLabel": "Recents" } },
              "sectionItems": { "totalCount": 1, "items": [ { "content": { "data": { "__typename": "List", "items": { "totalCount": 20, "items": [
                { "entity": { "data": {
                    "entityTypeTrait": { "type": "ENTITY_TYPE_PLAYLIST" },
                    "identityTrait": { "name": "Daily Mix 3", "contributors": { "items": [ { "name": "Spotify", "uri": "spotify:user:spotify" } ] } },
                    "uri": "spotify:playlist:P1" } } },
                { "entity": { "data": {
                    "entityTypeTrait": { "type": "ENTITY_TYPE_ARTIST" },
                    "identityTrait": { "name": "GFRIEND" },
                    "uri": "spotify:artist:A1" } } }
              ] } } } } ] } }
        ] } } }
        """;
        var groups = Compose(json, new HomeModuleTitles(Recents: "Onlangs afgespeeld")).Groups;

        var recents = Assert.Single(groups);
        Assert.Equal(HomeGroupKind.Recents, recents.Kind);
        Assert.Equal("Recents", recents.Title); // source title wins; app copy is only the absent-title fallback
        Assert.Equal(2, recents.Cards.Count);
        Assert.Equal(HomeCardKind.Playlist, recents.Cards[0].Kind);
        Assert.Equal(HomeCardKind.Artist, recents.Cards[1].Kind);
        Assert.Equal(20, recents.TotalCount);
    }

    [Fact]
    public void RecentlyPlayed_TotalCount_ComesFromTheWrappedList_NotTheOneItemWrapper()
    {
        static string Json(string wrapperTotal, string listTotal) => $$"""
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:recents", "data": { "__typename": "HomeRecentlyPlayedSectionData", "title": { "transformedLabel": "Recents" } },
            "sectionItems": { {{wrapperTotal}} "items": [ { "content": { "data": { "__typename": "List", "items": { {{listTotal}} "items": [
              { "entity": { "data": { "entityTypeTrait": { "type": "ENTITY_TYPE_PLAYLIST" },
                  "identityTrait": { "name": "One" }, "uri": "spotify:playlist:R1" } } },
              { "entity": { "data": { "entityTypeTrait": { "type": "ENTITY_TYPE_ARTIST" },
                  "identityTrait": { "name": "Two" }, "uri": "spotify:artist:R2" } } }
            ] } } } } ] } }
        ] } } }
        """;

        Assert.Equal(20, Assert.Single(Compose(Json("\"totalCount\": 1,", "\"totalCount\": 20,")).Sections!).TotalCount);
        // No nested count: the wrapper's 1 is DISCARDED, and the mapped cards are the honest floor.
        Assert.Equal(2, Assert.Single(Compose(Json("\"totalCount\": 1,", "")).Sections!).TotalCount);
        Assert.Equal(2, Assert.Single(Compose(Json("", "")).Sections!).TotalCount);
    }

    [Fact]
    public void RecentlyPlayed_Accounting_DistinguishesDuplicatesFromUnsupportedItems()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:recents", "data": { "__typename": "HomeRecentlyPlayedSectionData", "title": { "transformedLabel": "Recents" } },
            "sectionItems": { "totalCount": 1, "items": [ { "content": { "data": { "__typename": "List", "items": { "totalCount": 9, "items": [
              { "entity": { "data": { "entityTypeTrait": { "type": "ENTITY_TYPE_PLAYLIST" },
                  "identityTrait": { "name": "One" }, "uri": "spotify:playlist:R1" } } },
              { "entity": { "data": { "entityTypeTrait": { "type": "ENTITY_TYPE_PLAYLIST" },
                  "identityTrait": { "name": "Duplicate" }, "uri": "spotify:playlist:R1" } } },
              { "entity": { "data": { "entityTypeTrait": { "type": "ENTITY_TYPE_UNKNOWN" },
                  "identityTrait": { "name": "Unsupported" }, "uri": "spotify:unknown:R2" } } }
            ] } } } } ] } }
        ] } } }
        """;

        var section = Assert.Single(Compose(json).Sections!);
        Assert.Equal(3, section.RawItemCount);
        Assert.Single(section.Cards);
        Assert.Equal(1, section.DuplicateCount);
        Assert.Equal(1, section.UnsupportedCount);
        Assert.Equal(section.RawItemCount, section.Cards.Count + section.DuplicateCount + section.UnsupportedCount);
        Assert.Equal(9, section.TotalCount);
    }

    // ── greeting ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Greeting_ComesFromTheServer_NotTheLocalClock()
    {
        const string json = """
        { "greeting": { "transformedLabel": "Fijne middag", "translatedBaseText": "Good afternoon" },
          "sectionContainer": { "sections": { "items": [] } } }
        """;
        Assert.Equal("Fijne middag", Compose(json).Greeting);
    }

    [Fact]
    public void Greeting_FallsBackToTranslatedBaseText_ThenEmpty()
    {
        Assert.Equal("Good evening", Compose("""
        { "greeting": { "translatedBaseText": "Good evening" }, "sectionContainer": { "sections": { "items": [] } } }
        """).Greeting);

        Assert.Equal("", Compose(Home()).Greeting);
    }

    // ── module titles ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GenericSectionTitle_ReachesTheLanding_InsteadOfBecomingTheAppsJumpBackIn()
    {
        const string korean = "새로 나온 앨범";
        var single = Compose(Home(Generic(korean, ("spotify:playlist:N1", ""), ("spotify:playlist:N2", ""))));
        Assert.Equal(korean, Single(single, HomeGroupKind.QuickGrid).Title);

        var landing = HomeLandingProjection.Project(single, HomeModuleTitles.Default);
        Assert.Equal(korean, Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.QuickGrid)).Group.Title);

        var merged = Compose(Home(
            Generic(korean, ("spotify:playlist:N1", "")),
            Generic("Verder waar je was", ("spotify:playlist:N2", ""))));
        var mergedLanding = HomeLandingProjection.Project(merged, HomeModuleTitles.Default);
        Assert.Equal(HomeModuleTitles.Default.JumpBackIn,
            Assert.IsType<HomeLandingModule>(mergedLanding.Get(HomeGroupKind.QuickGrid)).Group.Title);
    }

    // ── track count + seed parsing ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlaylistTrackCount_ComesFromTheItemsPageTotal()
    {
        var g = Single(Compose(Home(Generic("Radio",
            ("spotify:playlist:R1", "inspiredby-mix"), ("spotify:playlist:R2", "inspiredby-mix")))), HomeGroupKind.RadioDial);
        Assert.Equal(50, g.Cards[0].TrackCount);
    }

    [Theory]
    // Anchors, BARE href — the topic-mix / artist-mix-reader dialect.
    [InlineData("topic-mix", "<a href=spotify:playlist:1>ILLIT</a>, <a href=spotify:playlist:2>dori</a> and more",
        new[] { "ILLIT", "dori" })]
    // Anchors, QUOTED href — the daylist / descripto dialect.
    [InlineData("descripto", "Here's some <a href=\"spotify:playlist:1\">puppy love</a>, fluttery, <a href=\"spotify:playlist:2\">western</a>",
        new[] { "puppy love", "western" })]
    // The localized trailer is dropped only when it really is a conjunction + a lower-case quantity word.
    [InlineData("daily-mix", "D.O., Wonstein, KIMMUSEUM and more", new[] { "D.O.", "Wonstein", "KIMMUSEUM" })]
    [InlineData("inspiredby-mix", "With LE SSERAFIM, NewJeans, Daniel Seavey en meer", new[] { "LE SSERAFIM", "NewJeans", "Daniel Seavey" })]
    public void ParseSeeds_HandlesEveryDialectTheServerActuallySends(string format, string description, string[] expected)
    {
        // 0.2.9 called the mapper's ParseSeeds directly; 0.3's parser is private to the decode, so the fact drives it
        // through a card of a format that lists seeds.
        var card = MapOne("{ \"__typename\": \"Playlist\", \"uri\": \"spotify:playlist:SEEDS\", \"name\": \"n\", \"format\": "
            + Str(format) + ", \"description\": " + Str(description) + " }");
        Assert.Equal(expected, card.Seeds);
    }

    [Theory]
    // Editorial / weekly / artistsets descriptions are prose: their commas are not artist chips.
    [InlineData("Your shortcut to hidden gems, deep cuts, and future faves, updated every Monday.")]
    [InlineData("This is ROSÉ. The essential tracks, all in one playlist.")]
    public void SeedsAreNotParsed_ForProseDescriptions(string prose)
    {
        var g = Single(Compose(Home($$"""
        { "uri": "spotify:section:editorial", "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Editorial" } },
          "sectionItems": { "items": [
            { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:E1", "name": "n",
                "format": "editorial", "description": {{Str(prose)}} } } },
            { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:E2", "name": "n",
                "format": "editorial" } } }
          ] } }
        """)), HomeGroupKind.Topic);
        Assert.Null(g.Cards[0].Seeds);
    }

    // ── accent ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Accent_ReadsColorDarkFromThePerTypenamePath_AndRejectsAFallback()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:mixed", "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Mixed" } },
            "sectionItems": { "items": [
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:C1", "name": "p", "format": "",
                  "images": { "items": [ { "extractedColors": { "colorDark": { "hex": "#048585", "isFallback": false } } } ] } } } },
              { "content": { "data": { "__typename": "Artist", "uri": "spotify:artist:C2", "profile": { "name": "a" },
                  "visuals": { "avatarImage": { "extractedColors": { "colorDark": { "hex": "#C80028", "isFallback": false } } } } } } },
              { "content": { "data": { "__typename": "Album", "uri": "spotify:album:C3", "name": "al",
                  "coverArt": { "extractedColors": { "colorDark": { "hex": "#123456", "isFallback": true } } } } } }
            ] } }
        ] } } }
        """;
        var cards = Single(Compose(json), HomeGroupKind.QuickGrid).Cards;
        Assert.Equal(0xFF048585u, cards[0].Accent);
        Assert.Equal(0xFFC80028u, cards[1].Accent);
        Assert.Equal(0u, cards[2].Accent);
    }

    [Fact]
    public void Topic_CarriesSourceSubtitleUriAndServerTotal()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:topic-1",
            "data": { "__typename": "HomeGenericSectionData",
              "title": { "transformedLabel": "Throwback" },
              "subtitle": { "transformedLabel": "Favorites still going strong." } },
            "sectionItems": { "totalCount": 20, "items": [
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:T1", "name": "One", "format": "editorial" } } },
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:T2", "name": "Two", "format": "editorial" } } }
            ] } }
        ] } } }
        """;

        var contribution = Compose(json);
        var group = Single(contribution, HomeGroupKind.Topic);
        Assert.Equal("Throwback", group.Title);
        Assert.Equal("Favorites still going strong.", group.Subtitle);
        Assert.Equal("spotify:section:topic-1", group.Uri);
        Assert.Equal(20, group.TotalCount);

        var section = Assert.Single(contribution.Sections!);
        Assert.Equal(group.Uri, section.Uri);
        Assert.Equal(2, section.RawItemCount);
        Assert.Equal(2, section.Cards.Count);
    }

    [Fact]
    public void TwoEditorialCardsAmongTen_DoNotMakeATopic()
    {
        var formats = new[] { "editorial", "editorial", "inspiredby-mix", "inspiredby-mix", "inspiredby-mix",
            "inspiredby-mix", "inspiredby-mix", "inspiredby-mix", "inspiredby-mix", "inspiredby-mix" };
        var cards = formats.Select((format, i) => ($"spotify:playlist:M{i}", format)).ToArray();
        var contribution = Compose(Home(Generic("Mixed shelf", cards)));

        Assert.DoesNotContain(contribution.Groups, g => g.Kind == HomeGroupKind.Topic);
        var primary = Single(contribution, HomeGroupKind.RadioDial);
        Assert.Equal("Mixed shelf", primary.Title);
        Assert.Equal(8, primary.Cards.Count);
        Assert.Null(Single(contribution, HomeGroupKind.Featured).Title);
    }

    [Fact]
    public void PodcastDominantSection_UsesPodcastShelf()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:podcasts", "data": { "__typename": "HomeGenericSectionData",
              "title": { "transformedLabel": "Shows for you" } }, "sectionItems": { "items": [
            { "content": { "data": { "__typename": "Podcast", "uri": "spotify:show:P1", "name": "One", "publisher": { "name": "A" } } } },
            { "content": { "data": { "__typename": "Podcast", "uri": "spotify:show:P2", "name": "Two", "publisher": { "name": "B" } } } }
          ] } }
        ] } } }
        """;
        var group = Single(Compose(json), HomeGroupKind.PodcastShelf);
        Assert.Equal("Shows for you", group.Title);
        Assert.All(group.Cards, c => Assert.Equal(HomeCardKind.Podcast, c.Kind));
    }

    [Fact]
    public void SectionAccounting_RecordsUnsupportedAndWithinSectionDuplicates()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "uri": "spotify:section:accounting", "data": { "__typename": "HomeGenericSectionData",
              "title": { "transformedLabel": "Accounting" } }, "sectionItems": { "items": [
            { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:D", "name": "One", "format": "" } } },
            { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:D", "name": "Duplicate", "format": "" } } },
            { "content": { "data": { "__typename": "NotFound" } } }
          ] } }
        ] } } }
        """;
        var section = Assert.Single(Compose(json).Sections!);
        Assert.Equal(3, section.RawItemCount);
        Assert.Single(section.Cards);
        Assert.Equal(1, section.DuplicateCount);
        Assert.Equal(1, section.UnsupportedCount);
        Assert.Equal(section.RawItemCount, section.Cards.Count + section.DuplicateCount + section.UnsupportedCount);
    }

    [Fact]
    public void MissingBaselineAndRecents_EmitNoEmptyRows()
    {
        var contribution = Compose(Home(Generic("Quick", ("spotify:playlist:Q", ""))));
        Assert.DoesNotContain(contribution.Groups, g => g.Kind is HomeGroupKind.DiscoverFeed or HomeGroupKind.Recents);
    }

    // ── the skeleton seed must track the composer ─────────────────────────────────────────────────────────────────

    [Fact]
    public void HomeSeed_CoversEveryModuleTheComposerCanEmit()
    {
        var seeded = HomeFeedView.Seed.Groups.Select(g => g.Kind).ToHashSet();
        foreach (var kind in new[]
        {
            HomeGroupKind.Hero, HomeGroupKind.WeeklyPair, HomeGroupKind.QuickGrid, HomeGroupKind.Recents,
            HomeGroupKind.MixBand, HomeGroupKind.ChipCards, HomeGroupKind.RadioDial, HomeGroupKind.QueueList,
            HomeGroupKind.RatedShelf, HomeGroupKind.Featured, HomeGroupKind.DiscoverFeed,
            HomeGroupKind.Topic, HomeGroupKind.SectionEntry, HomeGroupKind.PodcastShelf,
        })
            Assert.Contains(kind, seeded);
    }

    // ── the memo (0.3: `HomeComposer.For`) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void For_ReturnsTheSameDocument_UntilASectionMoves_AndEmptyForAnUnaskedRow()
    {
        TestScope.Fresh();
        Assert.Same(HomeFeedView.Empty, HomeComposer.For(Entities.HomeFeed("never-asked-chip".AsSpan()), HomeModuleTitles.Default));

        Decode(Home(Generic("Radio", ("spotify:playlist:R1", "inspiredby-mix"))));
        var home = Entities.HomeFeed();
        var first = HomeComposer.For(home, HomeModuleTitles.Default);
        Assert.Same(first, HomeComposer.For(home, HomeModuleTitles.Default));
        // Titles compare BY VALUE (Loc is live and rebuilds the record per read).
        Assert.Same(first, HomeComposer.For(home, new HomeModuleTitles()));

        Decode(Home(Generic("Radio", ("spotify:playlist:R1", "inspiredby-mix"), ("spotify:playlist:R2", "inspiredby-mix"))));
        Assert.NotSame(first, HomeComposer.For(home, HomeModuleTitles.Default));
    }
}
