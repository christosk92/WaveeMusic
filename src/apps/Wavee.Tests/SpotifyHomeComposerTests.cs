using System.Linq;
using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The composer routes PER CARD on content while retaining source sections as the lossless accounting authority. Module
// previews may split a section, but exactly one group owns its server title and deduplication never crosses a section URI.
public class SpotifyHomeComposerTests
{
    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────
    static HomeContribution Compose(string json) =>
        SpotifyHomeComposer.Compose(JsonDocument.Parse(json).RootElement, System.Array.Empty<PlaylistSummary>());

    static string Home(params string[] sections) =>
        "{ \"sectionContainer\": { \"sections\": { \"items\": [" + string.Join(",", sections) + "] } } }";

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
        { "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "{{title}}" } },
          "sectionItems": { "items": [ {{string.Join(",", items)}} ] } }
        """;
    }

    static HomeGroup Single(HomeContribution c, HomeGroupKind kind) => Assert.Single(c.Groups, g => g.Kind == kind);

    // -- the recents shelf's liked entry ------------------------------------------------------------------------

    /// <summary>Spotify's stock purple-heart PNG, served off the CDN. This is the exact url the measured
    /// <c>HomeRecentlyPlayedSectionData</c> section carries for the liked collection (assets/spotify/home.json).</summary>
    const string LikedStockCoverUrl = "https://misc.scdn.co/liked-songs/liked-songs-300.png";

    /// <summary>A recents section: its sectionItems array holds ONE `List` wrapper whose own items page carries the
    /// entities (the shape RecentsTotal's remarks describe).</summary>
    static string RecentsSection(string title, params string[] entities) =>
        $$"""
        { "data": { "__typename": "HomeRecentlyPlayedSectionData", "title": { "transformedLabel": "{{title}}" } },
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

    /// <summary>Liked Songs arrives in the recents shelf as a PLAYLIST entity carrying the stock cover, and both facts
    /// used to survive into the card: the rail therefore rendered a "Playlist"-captioned tile painting a CDN copy of
    /// the very PNG the user's chosen collection treatment replaces.
    ///
    /// <para>The mapping is now the honest one - the canonical uri, <c>HomeCardKind.Liked</c>, and NO image, so the
    /// render sites compose the collection cover instead of blitting the provider's stock art. The image assertion is
    /// the load-bearing one: the kind alone would not have stopped the purple heart.</para></summary>
    [Fact]
    public void RecentsShelf_MapsLikedSongsAsTheCollection_AndDropsTheProvidersStockCover()
    {
        var c = Compose(Home(RecentsSection("Recents",
            RecentEntity("spotify:collection:tracks", "Liked Songs", "ENTITY_TYPE_PLAYLIST", LikedStockCoverUrl),
            RecentEntity("spotify:playlist:p1", "A Playlist", "ENTITY_TYPE_PLAYLIST", "https://i.scdn.co/image/p1"))));

        var recents = Single(c, HomeGroupKind.Recents);
        var liked = Assert.Single(recents.Cards, x => x.Kind == HomeCardKind.Liked);
        Assert.Equal("spotify:collection:tracks", liked.Uri);
        Assert.Null(liked.Image);

        // The ordinary playlist beside it is untouched - this is a liked-only correction, not a recents-wide one.
        var plain = Assert.Single(recents.Cards, x => x.Kind == HomeCardKind.Playlist);
        Assert.Equal("spotify:playlist:p1", plain.Uri);
        Assert.Equal("https://i.scdn.co/image/p1", plain.Image?.Url);
    }

    /// <summary>The user-namespaced spelling is the SAME entity and must fold to the canonical uri, or the card, the
    /// nav route and the now-playing match end up comparing three different strings for one collection.</summary>
    [Fact]
    public void RecentsShelf_FoldsTheUserNamespacedCollectionSpelling()
    {
        var c = Compose(Home(RecentsSection("Recents",
            RecentEntity("spotify:user:abc123:collection", "Liked Songs", "ENTITY_TYPE_PLAYLIST", LikedStockCoverUrl))));

        var liked = Assert.Single(Single(c, HomeGroupKind.Recents).Cards);
        Assert.Equal(HomeCardKind.Liked, liked.Kind);
        Assert.Equal("spotify:collection:tracks", liked.Uri);
        Assert.Null(liked.Image);
    }

    [Fact]
    public void LiveShapedPayload_AccountsForEveryCardAndEverySectionTitle()
    {
        // An anonymized structural fixture for homeeee.json: 21 sections, 185 mappable cards and 20 server titles.
        // Its format distribution reproduces the current global-bucket loss exactly: Featured receives 77 cards and
        // QuickGrid 57, so their 24-card caps discard 53 + 33 while the other 51 cards survive.
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

        // Red on the old composer: expected (185, 20), actual (99, 1) => 86 cards and 19 titles unaccounted for.
        Assert.Equal((Cards: 185, Titles: 20), (Cards: survivingCards, Titles: survivingTitles));
    }

    // ── format → module ───────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("daily-mix", HomeGroupKind.MixBand)]
    [InlineData("inspiredby-mix", HomeGroupKind.RadioDial)]
    [InlineData("topic-mix", HomeGroupKind.ChipCards)]
    [InlineData("artist-mix-reader", HomeGroupKind.ChipCards)]
    [InlineData("editorial", HomeGroupKind.Topic)]
    [InlineData("format-shows-shuffle", HomeGroupKind.Topic)]
    [InlineData("discover-weekly", HomeGroupKind.WeeklyPair)]
    [InlineData("release-radar", HomeGroupKind.WeeklyPair)]
    // An unknown or blank format is a loose thing you return to, so it lands in the jump-back-in grid rather than
    // guessing a shape for it.
    [InlineData("artistsets", HomeGroupKind.Topic)]
    [InlineData("descripto", HomeGroupKind.Topic)]
    [InlineData("", HomeGroupKind.QuickGrid)]
    public void PlaylistFormat_SelectsTheModule(string format, HomeGroupKind expected)
    {
        var g = Single(Compose(Home(Generic("Shelf", ("spotify:playlist:A", format), ("spotify:playlist:B", format)))), expected);
        Assert.Equal(2, g.Cards.Count);
        Assert.Equal(format.Length == 0 ? null : format, g.Cards[0].Meta?.Format);
    }

    [Fact]
    public void ModuleShape_DoesNotDependOnTheLocalizedSectionTitle()
    {
        // The original composer matched the English literal "Made For {0}" out of title.translatedBaseText. A localized
        // label — or a server-side copy experiment — must not change the module the cards land in.
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "data": { "__typename": "HomeGenericSectionData", "title": {
                "transformedLabel": "Speciaal voor Christos", "translatedBaseText": "Made For {0}" } },
            "sectionItems": { "items": [
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:M1", "name": "Mix one", "format": "daily-mix" } } },
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:M2", "name": "Mix two", "format": "daily-mix" } } }
            ] } }
        ] } } }
        """;
        var g = Single(Compose(json), HomeGroupKind.MixBand);
        // The band is the ONE module that keeps a server label: "Made For {name}" is better than anything the app could
        // write for it, and it names a real series rather than a page slot.
        Assert.Equal("Speciaal voor Christos", g.Title);
    }

    // ── bucketing across sections ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void OneSection_SplitsAcrossTheModulesItsFormatsName()
    {
        // The real "Made For {name}" section: six daily mixes together with Discover Weekly and Release Radar. Those are
        // three modules' worth of content in one section.
        var c = Compose(Home(Generic("Made For Christos",
            ("spotify:playlist:D1", "daily-mix"), ("spotify:playlist:D2", "daily-mix"),
            ("spotify:playlist:D3", "daily-mix"), ("spotify:playlist:D4", "daily-mix"),
            ("spotify:playlist:D5", "daily-mix"), ("spotify:playlist:D6", "daily-mix"),
            ("spotify:playlist:DW", "discover-weekly"), ("spotify:playlist:RR", "release-radar"))));

        Assert.Equal(6, Single(c, HomeGroupKind.MixBand).Cards.Count);
        var weekly = Single(c, HomeGroupKind.WeeklyPair);
        Assert.Equal(2, weekly.Cards.Count);
        // Response order survives inside each bucket.
        Assert.Equal("spotify:playlist:DW", weekly.Cards[0].Uri);
        Assert.Equal("spotify:playlist:RR", weekly.Cards[1].Uri);
        // The weekly 2-up carries no head of its own — the two cards say what they are.
        Assert.Null(weekly.Title);
    }

    [Fact]
    public void SeveralSections_KeepSeparateSourceOwnedModuleGroups()
    {
        // "Recommended Stations" and "Popular radio" are both inspiredby-mix, and two more stations are buried in a mixed
        // "Jump back in". All twenty are one dial: a module that merges sections cannot honestly wear either label, so it
        // takes the app's own.
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
        // The real "Jump back in": playlists, artists, an album and a podcast. There are no sections left to keep coherent,
        // so every card follows its OWN content — the two stations join the dial, and the loose entities become tiles.
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Jump back in" } },
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

    // ── the shelves that used to vanish ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void AudiobookSection_BecomesARatedShelf_WithRatingAuthorAndLength()
    {
        // THE regression that motivated this work: CardFromEntity handled only Album/Playlist/Artist, so all ten cards
        // mapped to null and the composer's minimum-cards gate discarded the whole shelf.
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Audiobooks for you" } },
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
        Assert.Equal("Matthew Maxwell", first.Meta!.Author);
        Assert.Equal("Matthew Maxwell", first.Subtitle);
        Assert.Equal(4.57, first.Meta.Rating, 2);
        Assert.Equal(4551407, first.Meta.DurationMs);
        Assert.Equal("Included in Premium", first.Meta.Signifier);
        Assert.Equal(0xFF7B776Eu, first.Meta.Accent);
        // showAverage:false means the server sent an average it does not want displayed — the card must not show one.
        Assert.Equal(0d, g.Cards[1].Meta!.Rating);
    }

    [Fact]
    public void EpisodeSection_BecomesAQueueList_WithDurationShowVideoAndResume()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Episodes you might like" } },
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
        Assert.Equal("theMITmonk", first.Subtitle);      // the SHOW is the second line — a title alone says nothing
        Assert.Equal(1028713, first.Meta!.DurationMs);
        Assert.Equal(514356, first.Meta.ResumeMs);
        Assert.True(first.Meta.HasVideo);
        Assert.Equal(0xFF8058F8u, first.Meta.Accent);

        var second = g.Cards[1].Meta!;
        Assert.False(second.HasVideo);
        Assert.Equal(0, second.ResumeMs);
    }

    // ── shorts / hero ─────────────────────────────────────────────────────────────────────────────────────────
    const string ShortsWithDaylist = """
    { "data": { "__typename": "HomeShortsSectionData" },
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
        // It used to be skipped outright — no case label, only a comment — discarding a whole module of real cards. Now it
        // is read like any other section: the editorial tile joins Editors' picks, the album becomes a tile, and the
        // daylist is lifted into the hero.
        var c = Compose(Home(ShortsWithDaylist));
        Assert.Equal("spotify:playlist:S1", Assert.Single(Single(c, HomeGroupKind.Featured).Cards).Uri);
        Assert.Equal("spotify:album:S3", Assert.Single(Single(c, HomeGroupKind.QuickGrid).Cards).Uri);
        Assert.Equal("spotify:playlist:DAY", Assert.Single(Single(c, HomeGroupKind.Hero).Cards).Uri);
    }

    [Fact]
    public void Daylist_IsPromotedToTheHero_FromWhereverItArrives()
    {
        // No live capture we hold carries a Spotlight section, and the sole daylist sits INSIDE the shorts module — so a
        // "generic section of daylists" rule would never fire. The promotion is what puts a hero on the page at all.
        var card = Assert.Single(Single(Compose(Home(ShortsWithDaylist)), HomeGroupKind.Hero).Cards);
        Assert.Equal("daylist", card.Meta!.Format);
        // A daylist's tags come from its localized_terms attribute — a clean comma list — not from parsing its prose.
        Assert.Equal(new[] { "puppy love", "hollywood", "happy pop" }, card.Meta.Seeds);
    }

    [Fact]
    public void DaylistHydrationMarker_UsesTheProviderPretitle_NotANameGuess()
    {
        static HomeCard Map(string name, string format, string pretitle)
        {
            using var doc = JsonDocument.Parse($$"""
            { "__typename": "Playlist", "uri": "spotify:playlist:DAY", "name": "{{name}}",
              "format": "{{format}}", "attributes": [ { "key": "daylist_pretitle", "value": "{{pretitle}}" } ] }
            """);
            return SpotifyExportMapper.CardFromEntity(doc.RootElement)!;
        }

        var shallow = Map("daylist", "daylist", "daylist");
        Assert.True(shallow.Meta!.NeedsHydration);
        Assert.Equal("daylist", shallow.Meta.GenericTitle);

        var exact = Map("teen pop mid 2010s friday afternoon", "daylist", "daylist");
        Assert.False(exact.Meta!.NeedsHydration);
        Assert.Equal("daylist", exact.Meta.GenericTitle);

        var ordinary = Map("daylist", "editorial", "daylist");
        Assert.False(ordinary.Meta!.NeedsHydration);
        Assert.Null(ordinary.Meta.GenericTitle);
    }

    [Fact]
    public void Daylist_MapsExpiresCreatedAndHeaderImageFromAttributes()
    {
        // Wire-verified Pathfinder daylist attributes: expires/created are ISO-8601 UTC; header_image_url_desktop is
        // the authored full-bleed banner (distinct from the square cover). Plain playlists must leave all three at
        // their defaults — the attr walk is gated on format == daylist.
        const string expires = "2026-08-11T23:58:59.559688264Z";
        const string created = "2026-08-11T20:58:59.559688264Z";
        const string header = "https://daylist.spotifycdn.com/headers/desktop/night.jpg";
        using var daylist = JsonDocument.Parse($$"""
        { "__typename": "Playlist", "uri": "spotify:playlist:DAY", "name": "night drive",
          "format": "daylist",
          "attributes": [
            { "key": "expires", "value": "{{expires}}" },
            { "key": "created", "value": "{{created}}" },
            { "key": "header_image_url_desktop", "value": "{{header}}" }
          ] }
        """);
        var meta = SpotifyExportMapper.CardFromEntity(daylist.RootElement)!.Meta!;
        Assert.Equal(DateTimeOffset.Parse(expires, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
            .ToUnixTimeMilliseconds(), meta.ExpiresAtMs);
        Assert.Equal(DateTimeOffset.Parse(created, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
            .ToUnixTimeMilliseconds(), meta.CreatedAtMs);
        Assert.Equal(header, meta.HeaderImageUrl);

        using var plain = JsonDocument.Parse("""
        { "__typename": "Playlist", "uri": "spotify:playlist:P", "name": "Mine", "format": "editorial",
          "attributes": [
            { "key": "expires", "value": "2026-08-11T23:58:59.559688264Z" },
            { "key": "header_image_url_desktop", "value": "https://daylist.spotifycdn.com/headers/desktop/night.jpg" }
          ] }
        """);
        var plainMeta = SpotifyExportMapper.CardFromEntity(plain.RootElement)!.Meta!;
        Assert.Equal(0, plainMeta.ExpiresAtMs);
        Assert.Equal(0, plainMeta.CreatedAtMs);
        Assert.Null(plainMeta.HeaderImageUrl);
    }

    const string Spotlight = """
    { "data": { "__typename": "HomeSpotlightSectionData", "title": { "transformedLabel": "Spotlight" } },
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

    // ── baseline recommendations ──────────────────────────────────────────────────────────────────────────────
    static string Baseline(string title, string uri) => $$"""
    { "data": { "__typename": "HomeFeedBaselineSectionData", "title": { "transformedLabel": "{{title}}" } },
      "sectionItems": { "items": [ { "content": { "data": {
          "__typename": "Playlist", "uri": "{{uri}}", "name": "{{uri}}", "format": "editorial" } } } ] } }
    """;

    [Fact]
    public void BaselineSections_KeepOneTitleOwnerEach_AndRenderAsOneFeedRow()
    {
        // ~20 single-item recs used to be dealt out as repeated 5-card "editorial breaks" interleaved between shelves, so
        // the reader met the same module shape four times on the way down. One bounded feed is a destination instead of an
        // interruption — and the section title is the only explanation of WHY each was picked, so it becomes the eyebrow.
        // This is the ONE place a server section label still reaches the page.
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
        // A baseline stub is frequently the same playlist a full section above it already offers; showing it twice on one
        // screen is the visible bug. The section is read first, so the stub is the one that goes.
        var c = Compose(Home(
            Generic("For fans of IU", ("spotify:playlist:DUP", "editorial"), ("spotify:playlist:OTHER", "editorial")),
            Baseline("For fans of IU", "spotify:playlist:DUP")));

        Assert.Equal(2, c.Sections!.Count);
        Assert.Contains(c.Sections[0].Cards, x => x.Uri == "spotify:playlist:DUP");
        Assert.Contains(c.Sections[1].Cards, x => x.Uri == "spotify:playlist:DUP");
        Assert.Contains(c.Groups, g => g.Kind == HomeGroupKind.DiscoverFeed);
    }

    // ── recents ───────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void RecentlyPlayed_RendersInlineFromHomeResponse_AsItsOwnKind()
    {
        // The wrapper's own totalCount is 1 — it counts the single `List` child, exactly as the capture does. The
        // shelf's real length (20) is on that list.
        const string json = """
        { "sectionContainer": { "sections": { "items": [
            { "data": { "__typename": "HomeRecentlyPlayedSectionData", "title": { "transformedLabel": "Recents" } },
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
        var groups = SpotifyHomeComposer.Compose(JsonDocument.Parse(json).RootElement,
            System.Array.Empty<PlaylistSummary>(), new HomeModuleTitles(Recents: "Onlangs afgespeeld")).Groups;

        var recents = Assert.Single(groups);
        // Its OWN kind, not Shelf: the page looks modules up by kind, and sharing Shelf with the library-derived
        // "Your playlists / albums / artists" groups would make whichever came first shadow the rest.
        Assert.Equal(HomeGroupKind.Recents, recents.Kind);
        Assert.Equal("Recents", recents.Title); // source title wins; app copy is only the absent-title fallback
        Assert.Equal(2, recents.Cards.Count);
        Assert.Equal(HomeCardKind.Playlist, recents.Cards[0].Kind);
        Assert.Equal(HomeCardKind.Artist, recents.Cards[1].Kind);
        // 20, not the wrapper's 1. This is what "Show all" arms on, and reading the wrapper made a twenty-entry shelf
        // claim a total of one — so the affordance could never appear for Recents.
        Assert.Equal(20, recents.TotalCount);
    }

    [Fact]
    public void RecentlyPlayed_TotalCount_ComesFromTheWrappedList_NotTheOneItemWrapper()
    {
        static string Json(string wrapperTotal, string listTotal) => $$"""
        { "sectionContainer": { "sections": { "items": [
          { "data": { "__typename": "HomeRecentlyPlayedSectionData", "title": { "transformedLabel": "Recents" } },
            "sectionItems": { {{wrapperTotal}} "items": [ { "content": { "data": { "__typename": "List", "items": { {{listTotal}} "items": [
              { "entity": { "data": { "entityTypeTrait": { "type": "ENTITY_TYPE_PLAYLIST" },
                  "identityTrait": { "name": "One" }, "uri": "spotify:playlist:R1" } } },
              { "entity": { "data": { "entityTypeTrait": { "type": "ENTITY_TYPE_ARTIST" },
                  "identityTrait": { "name": "Two" }, "uri": "spotify:artist:R2" } } }
            ] } } } } ] } }
        ] } } }
        """;

        // The capture's own numbers: the section wrapper says 1 because its array holds exactly one `List`.
        Assert.Equal(20, Assert.Single(Compose(Json("\"totalCount\": 1,", "\"totalCount\": 20,")).Sections!).TotalCount);
        // No nested count to read: the wrapper's 1 is DISCARDED rather than kept as a fallback, because it counts
        // wrappers and would pin a two-card shelf BELOW its own card count. The mapped cards are the honest floor.
        Assert.Equal(2, Assert.Single(Compose(Json("\"totalCount\": 1,", "")).Sections!).TotalCount);
        Assert.Equal(2, Assert.Single(Compose(Json("", "")).Sections!).TotalCount);
    }

    [Fact]
    public void RecentlyPlayed_Accounting_DistinguishesDuplicatesFromUnsupportedItems()
    {
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "data": { "__typename": "HomeRecentlyPlayedSectionData", "title": { "transformedLabel": "Recents" } },
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
        Assert.Equal(section.RawItemCount,
            section.Cards.Count + section.DuplicateCount + section.UnsupportedCount);
        // The server's total is the WRAPPED list's, and it is independent of the accounting above: the drill-in has 9
        // to fetch even though this page yielded one card out of three raw items.
        Assert.Equal(9, section.TotalCount);
    }

    // ── homeSection (the "Show all" paging axis) ──────────────────────────────────────────────────────────────
    // One persisted document hosts both `home` and `homeSection`; operationName selects data.home vs data.homeSections.
    // The section item wrapper is byte-identical to the one home's inline sections use, which is why the same card
    // mapper serves both — these fixtures are shaped exactly like the captured responses.
    static string SectionPage(string pagingInfo, string totalCount, params string[] items) => $$"""
    { "data": { "homeSections": { "sections": [
      { "uri": "spotify:section:S1", "data": { "__typename": "HomeGenericSectionData",
          "title": { "transformedLabel": "Made for you" }, "subtitle": { "transformedLabel": "Picked today" } },
        "sectionItems": { {{totalCount}} {{pagingInfo}} "items": [ {{string.Join(",", items)}} ] } }
    ] } } }
    """;

    static string PlaylistItem(string uri, string name) => $$"""
    { "uri": "{{uri}}", "data": null, "content": { "__typename": "HomeSectionItemResponseWrapper",
      "data": { "__typename": "Playlist", "uri": "{{uri}}", "name": "{{name}}", "format": "editorial" } } }
    """;

    [Fact]
    public void SectionPage_MapsTheItemsAndBothPagingNumbers()
    {
        var page = SpotifyHomeComposer.SectionPage(JsonDocument.Parse(SectionPage(
            "\"pagingInfo\": { \"nextOffset\": 20 },", "\"totalCount\": 64,",
            PlaylistItem("spotify:playlist:A", "Alpha"), PlaylistItem("spotify:playlist:B", "Beta"))).RootElement);

        Assert.NotNull(page);
        Assert.Equal("spotify:section:S1", page!.Section.Uri);
        Assert.Equal("Made for you", page.Section.Title);
        Assert.Equal("Picked today", page.Section.Subtitle);
        Assert.Equal(["Alpha", "Beta"], page.Section.Cards.Select(c => c.Title));
        // Straight through SpotifyExportMapper.CardFromEntity — the same meta the inline Home sections carry, not a
        // second, thinner card model.
        Assert.Equal("editorial", page.Section.Cards[0].Meta!.Format);
        Assert.Equal(64, page.Section.TotalCount);
        Assert.Equal(2, page.Section.RawItemCount);
        Assert.Equal(20, page.NextOffset);
    }

    [Fact]
    public void SectionPage_NullNextOffset_StaysNull_EvenWhenTheTotalClaimsMore()
    {
        // Measured in 7 of 31 captured sections: fewer items than totalCount, and no cursor. Flattening null to 0 here
        // would be indistinguishable from the complete-section 0 below, and both must terminate.
        var page = SpotifyHomeComposer.SectionPage(JsonDocument.Parse(SectionPage(
            "\"pagingInfo\": { \"nextOffset\": null },", "\"totalCount\": 9,",
            PlaylistItem("spotify:playlist:A", "Alpha"))).RootElement);

        Assert.NotNull(page);
        Assert.Null(page!.NextOffset);
        Assert.Equal(9, page.Section.TotalCount);
        Assert.Equal(1, page.Section.RawItemCount);
    }

    [Fact]
    public void SectionPage_CompleteSectionAnsweringZero_IsCarriedVerbatim_NotAsNull()
    {
        // A COMPLETE section really does answer nextOffset: 0 (6 items / totalCount 6). The mapper reports what the
        // server said; deciding that 0 means "stop" is HomeSectionPaging.CanAdvance's job, not the mapper's.
        var page = SpotifyHomeComposer.SectionPage(JsonDocument.Parse(SectionPage(
            "\"pagingInfo\": { \"nextOffset\": 0 },", "\"totalCount\": 6,",
            PlaylistItem("spotify:playlist:A", "Alpha"))).RootElement);

        Assert.Equal(0, Assert.IsType<HomeSectionPageResult>(page).NextOffset);
    }

    [Fact]
    public void SectionPage_MissingPagingInfo_IsTreatedAsNoCursor()
    {
        var page = SpotifyHomeComposer.SectionPage(JsonDocument.Parse(SectionPage(
            "", "\"totalCount\": 6,", PlaylistItem("spotify:playlist:A", "Alpha"))).RootElement);

        Assert.NotNull(page);
        Assert.Null(page!.NextOffset);
    }

    [Fact]
    public void SectionPage_LedgerAccountsForUnsupportedAndDuplicateItems_LikeTheInlineSections()
    {
        const string notFound = """{ "content": { "data": { "__typename": "NotFound" } } }""";
        var page = SpotifyHomeComposer.SectionPage(JsonDocument.Parse(SectionPage(
            "\"pagingInfo\": { \"nextOffset\": 4 },", "\"totalCount\": 64,",
            PlaylistItem("spotify:playlist:A", "Alpha"),
            PlaylistItem("spotify:playlist:A", "Alpha again"),
            notFound,
            PlaylistItem("spotify:playlist:B", "Beta"))).RootElement);

        Assert.NotNull(page);
        var section = page!.Section;
        Assert.Equal(4, section.RawItemCount);
        Assert.Equal(2, section.Cards.Count);
        Assert.Equal(1, section.DuplicateCount);
        Assert.Equal(1, section.UnsupportedCount);
        Assert.Equal(section.RawItemCount, section.Cards.Count + section.DuplicateCount + section.UnsupportedCount);
    }

    [Fact]
    public void SectionPage_NoSection_IsNull_SoTheCallerCanTellRefusalFromEmptiness()
    {
        // A rejected persisted query answers 400 → no document at all; this covers the other shape: a 200 whose
        // homeSections carried nothing. Both must read as "this did not work", never as "this section is empty".
        Assert.Null(SpotifyHomeComposer.SectionPage(JsonDocument.Parse("""{"data":{"homeSections":{"sections":[]}}}""").RootElement));
        Assert.Null(SpotifyHomeComposer.SectionPage(JsonDocument.Parse("""{"data":{}}""").RootElement));
    }

    [Fact]
    public void SectionPage_EmptyItems_IsAnEmptySection_NotANullPage()
    {
        var page = SpotifyHomeComposer.SectionPage(JsonDocument.Parse(SectionPage(
            "\"pagingInfo\": { \"nextOffset\": null },", "\"totalCount\": 0,")).RootElement);

        Assert.NotNull(page);
        Assert.Empty(page!.Section.Cards);
        Assert.Equal(0, page.Section.RawItemCount);
    }

    [Fact]
    public void SectionPage_TitlelessResponse_FallsBackToTheCallersLabel()
    {
        const string json = """
        { "data": { "homeSections": { "sections": [
          { "uri": "spotify:section:S9", "data": { "__typename": "HomeGenericSectionData" },
            "sectionItems": { "totalCount": 1, "items": [
              { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:Z", "name": "Zeta" } } } ] } }
        ] } } }
        """;
        var page = SpotifyHomeComposer.SectionPage(JsonDocument.Parse(json).RootElement, "From the route");
        Assert.Equal("From the route", Assert.IsType<HomeSectionPageResult>(page).Section.Title);
    }

    // ── greeting ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Greeting_ComesFromTheServer_NotTheLocalClock()
    {
        const string json = """
        { "greeting": { "transformedLabel": "Fijne middag", "translatedBaseText": "Good afternoon" },
          "sectionContainer": { "sections": { "items": [] } } }
        """;
        // transformedLabel is the display form and wins; it is already localized for the ACCOUNT, which is why it is right
        // even when the machine clock and the account locale disagree.
        Assert.Equal("Fijne middag", Compose(json).Greeting);
    }

    [Fact]
    public void Greeting_FallsBackToTranslatedBaseText_ThenEmpty()
    {
        Assert.Equal("Good evening", Compose("""
        { "greeting": { "translatedBaseText": "Good evening" }, "sectionContainer": { "sections": { "items": [] } } }
        """).Greeting);

        // Empty, NOT a synthesised one: a source with no greeting lets the aggregate fall through to another source.
        Assert.Equal("", Compose(Home()).Greeting);
    }

    // ── module titles ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SyntheticLibraryModuleTitle_IsAppSupplied()
    {
        var titles = new HomeModuleTitles(JumpBackIn: "Verder waar je was");
        var library = new[] { new PlaylistSummary("spotify:playlist:L1", "Library one", "Me", 1, null) };
        var group = Assert.Single(SpotifyHomeComposer.Compose(JsonDocument.Parse(Home()).RootElement, library, titles).Groups);
        Assert.Equal(HomeGroupKind.QuickGrid, group.Kind);
        Assert.Equal("Verder waar je was", group.Title);
    }

    [Fact]
    public void GenericSectionTitle_ReachesTheLanding_InsteadOfBecomingTheAppsJumpBackIn()
    {
        // A section whose cards name no module splits into nothing but the shapeless grid, and the composer hands that
        // grid the section's title (its ONE title owner — the 185-card/20-title accounting above depends on exactly one
        // group wearing it). The landing then threw the title away and rendered the row under the app's own copy, which
        // is the one place a real, already localized server label was being deleted. Verbatim: never matched, never
        // re-translated, and a non-Latin label is not special-cased anywhere on the path.
        const string korean = "새로 나온 앨범";
        var single = Compose(Home(Generic(korean, ("spotify:playlist:N1", ""), ("spotify:playlist:N2", ""))));
        Assert.Equal(korean, Single(single, HomeGroupKind.QuickGrid).Title);

        var landing = HomeLandingProjection.Project(
            new HomeFeed("", single.Groups, Sections: single.Sections), HomeModuleTitles.Default);
        Assert.Equal(korean, Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.QuickGrid)).Group.Title);

        // Two labelled sections merging into the ONE grid can honestly wear neither, so the app's copy takes over.
        var merged = Compose(Home(
            Generic(korean, ("spotify:playlist:N1", "")),
            Generic("Verder waar je was", ("spotify:playlist:N2", ""))));
        var mergedLanding = HomeLandingProjection.Project(
            new HomeFeed("", merged.Groups, Sections: merged.Sections), HomeModuleTitles.Default);
        Assert.Equal(HomeModuleTitles.Default.JumpBackIn,
            Assert.IsType<HomeLandingModule>(mergedLanding.Get(HomeGroupKind.QuickGrid)).Group.Title);
    }

    // ── track count + seed parsing (mapper contracts the modules depend on) ───────────────────────────────────
    [Fact]
    public void PlaylistTrackCount_ComesFromTheItemsPageTotal()
    {
        var g = Single(Compose(Home(Generic("Radio",
            ("spotify:playlist:R1", "inspiredby-mix"), ("spotify:playlist:R2", "inspiredby-mix")))), HomeGroupKind.RadioDial);
        Assert.Equal(50, g.Cards[0].Meta!.TrackCount);
    }

    [Theory]
    // Anchors, BARE href — the topic-mix / artist-mix-reader dialect.
    [InlineData("<a href=spotify:playlist:1>ILLIT</a>, <a href=spotify:playlist:2>dori</a> and more",
        new[] { "ILLIT", "dori" })]
    // Anchors, QUOTED href — the daylist / descripto dialect. Bare text between anchors is kept: on a daylist those are
    // real tags the server simply had no link for.
    [InlineData("Here's some <a href=\"spotify:playlist:1\">puppy love</a>, fluttery, <a href=\"spotify:playlist:2\">western</a>",
        new[] { "puppy love", "western" })]
    // A locale-dependent trailer ("and more" / "en meer") is dropped only when the penultimate token is a known
    // conjunction AND the final token is a lowercase quantity word — so multi-token artist names survive intact.
    [InlineData("D.O., Wonstein, KIMMUSEUM and more", new[] { "D.O.", "Wonstein", "KIMMUSEUM" })]
    [InlineData("With LE SSERAFIM, NewJeans, Daniel Seavey en meer", new[] { "LE SSERAFIM", "NewJeans", "Daniel Seavey" })]
    public void ParseSeeds_HandlesEveryDialectTheServerActuallySends(string description, string[] expected)
        => Assert.Equal(expected, SpotifyExportMapper.ParseSeeds(description));

    [Theory]
    // Editorial / weekly / artistsets descriptions are human-written prose. Parsing them would turn a sentence's commas
    // into fake artist chips — which is why seeds are only read for the formats that actually list them.
    [InlineData("Your shortcut to hidden gems, deep cuts, and future faves, updated every Monday.")]
    [InlineData("This is ROSÉ. The essential tracks, all in one playlist.")]
    public void SeedsAreNotParsed_ForProseDescriptions(string prose)
    {
        var g = Single(Compose(Home($$"""
        { "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Editorial" } },
          "sectionItems": { "items": [
            { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:E1", "name": "n",
                "format": "editorial", "description": "{{prose.Replace("\"", "\\\"")}}" } } },
            { "content": { "data": { "__typename": "Playlist", "uri": "spotify:playlist:E2", "name": "n",
                "format": "editorial" } } }
          ] } }
        """)), HomeGroupKind.Topic);
        Assert.Null(g.Cards[0].Meta!.Seeds);
    }

    // ── accent ────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Accent_ReadsColorDarkFromThePerTypenamePath_AndRejectsAFallback()
    {
        // Spotify files extractedColors under a DIFFERENT node per entity type: playlists off the first image,
        // albums/episodes/audiobooks/podcasts off coverArt, artists off the avatar.
        const string json = """
        { "sectionContainer": { "sections": { "items": [
          { "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Mixed" } },
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
        Assert.Equal(0xFF048585u, cards[0].Meta!.Accent);
        Assert.Equal(0xFFC80028u, cards[1].Meta!.Accent);
        // isFallback means "we had no colours and invented one" — worse than the neutral tile, so it is rejected.
        Assert.Equal(0u, cards[2].Meta!.Accent);
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

    // ── the skeleton seed must track the composer ─────────────────────────────────────────────────────────────
    [Fact]
    public void HomeSeed_CoversEveryModuleTheComposerCanEmit()
    {
        // Skel.Region DERIVES the loading shimmer by rendering the real content tree against this seed, so a seed that
        // omits a module shimmers the wrong silhouette and then snaps — the reveal becomes a jump-cut.
        var seeded = FakeData.HomeSeed.Groups.Select(g => g.Kind).ToHashSet();
        foreach (var kind in new[]
        {
            HomeGroupKind.Hero, HomeGroupKind.WeeklyPair, HomeGroupKind.QuickGrid, HomeGroupKind.Recents,
            HomeGroupKind.MixBand, HomeGroupKind.ChipCards, HomeGroupKind.RadioDial, HomeGroupKind.QueueList,
            HomeGroupKind.RatedShelf, HomeGroupKind.Featured, HomeGroupKind.DiscoverFeed,
            HomeGroupKind.Topic, HomeGroupKind.SectionEntry, HomeGroupKind.PodcastShelf,
        })
            Assert.Contains(kind, seeded);
    }
}
