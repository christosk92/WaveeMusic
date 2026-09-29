// ── Wavee.Tests/HomeDecodeTests.cs — Spotify.Decode.HomeFeed / WhatsNew / UserTop (Wave 5, owner P) ─────────────────
//
// The Home fold for ANY subject, read against the captured answer the app ships (`Fixtures/spotify/home.json`, 31 bands),
// plus the what's-new feed (0.2.9's `whatsnew-feed.json`, copied into `Fixtures/home/`) and the userTopContent document
// 0.2.9's `UserTopMapperTests` used. Every count below was MEASURED from the fixture (a JSON read), not guessed:
//   band 0  HomeShortsSectionData   10 items, one `UnknownType` wrapper (`spotify:user:@:collection`, no data) → 9 cards,
//           total 64, nextOffset 10
//   band 1  HomeSpotlightSectionData the KIMMUSEUM album, colorDark #C02868
//   band 3  HomeRecentlyPlayedSectionData one `List` wrapper whose 20 entities are the band (the list's total is 20)
//   bands 11-30 HomeFeedBaselineSectionData
//
// Facts read HANDLES after the commit, except where a card depends on the unapplied `Edges.Staging.cs` collection patch
// (the SectionCards arm resolves a card through `Entities.TableFor`, which answers null for Liked Songs): those are read
// at the STAGED level and say so.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class HomeDecodeTests
{
    const string ShortsUri = "spotify:section:0JQ5DAIiKWzVFULQfUm85Y";
    const string SpotlightUri = "spotify:section:0JQ5DAKjx0PtyWwzEGt6uI";
    const string MadeForUri = "spotify:section:0JQ5DAUnp4wcj0bCb3wh3S";
    const string RecentsUri = "spotify:section:0JQ5DAroEmF9ANbLaiJ7XR";
    const string JumpBackInUri = "spotify:section:0JQ5DAIiKWzVFULQfUm85X";
    const string TopMixesUri = "spotify:section:0JQ5DAnM3wGh0gz1MXnu89";
    const string DaylistUri = "spotify:playlist:37i9dQZF1EP6YuccBxUcC1";

    static byte[] HomeJson => HomeFixtures.Fixture("spotify", "home.json");

    static void LoadHome(string subject = "wavee:home")
    {
        var s = Staging.Rent();
        Spotify.Decode.HomeFeed(HomeJson, Encoding.UTF8.GetBytes(subject), s);
        TestScope.CommitAndPublish(s);
    }

    static HomeSectionView View(string uri) => HomeSectionView.Of(Entities.Section(uri.AsSpan()));

    static HomeCard CardTitled(string sectionUri, string title) => View(sectionUri).Cards.First(c => c.Title == title);

    // ── the document ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_feed_lands_every_band_on_its_subject_with_greeting_and_chips()
    {
        TestScope.Fresh();
        LoadHome();

        var home = Entities.HomeFeed();
        Assert.True(home.Knows(HomeFields.All));
        Assert.Equal(EdgeState.Complete, home.SectionState);
        Assert.Equal(31, home.SectionCount);
        Assert.Equal("Good morning", Entities.Strings.Resolve(home.GreetingId));
        Assert.Equal(5, home.ChipIds.Length);
        Assert.Equal("music-chip", Entities.Strings.Resolve(home.ChipIds[0]));
    }

    [Fact]
    public void Every_band_takes_its_typename_verdict()
    {
        TestScope.Fresh();
        LoadHome();

        var slots = Entities.HomeFeed().SectionSlots.ToArray();
        Assert.Equal(SectionKind.HomeShorts, new Section(slots[0]).Kind);
        Assert.Equal(SectionKind.HomeSpotlight, new Section(slots[1]).Kind);
        Assert.Equal(SectionKind.HomeGeneric, new Section(slots[2]).Kind);
        Assert.Equal(SectionKind.HomeRecentlyPlayed, new Section(slots[3]).Kind);
        for (int i = 4; i <= 10; i++) Assert.Equal(SectionKind.HomeGeneric, new Section(slots[i]).Kind);
        for (int i = 11; i <= 30; i++) Assert.Equal(SectionKind.HomeBaseline, new Section(slots[i]).Kind);
        foreach (int slot in slots) Assert.False(new Section(slot).IsChart);
    }

    [Fact]
    public void Every_bands_ledger_accounts_for_every_raw_item()
    {
        TestScope.Fresh();
        LoadHome();

        foreach (int slot in Entities.HomeFeed().SectionSlots)
        {
            var section = new Section(slot);
            Assert.Equal(section.Raw, section.Cards + section.Unsupported + section.Duplicates);
        }
    }

    [Fact]
    public void The_shorts_bands_unknown_wrapper_is_unsupported_and_never_a_liked_card()
    {
        // B1's fold read the ITEM's own uri, so `spotify:user:@:collection` became a nameless Liked card. The card walk
        // reads `content.data` only.
        TestScope.Fresh();
        LoadHome();

        var shorts = Entities.Section(ShortsUri.AsSpan());
        Assert.Equal(SectionKind.HomeShorts, shorts.Kind);
        Assert.Equal(10, shorts.Raw);
        Assert.Equal(9, shorts.Cards);
        Assert.Equal(1, shorts.Unsupported);
        Assert.Equal(0, shorts.Duplicates);
        Assert.Equal(64, shorts.Total);
        Assert.Equal(10, shorts.NextOffset);
        Assert.True(shorts.HasMore);

        var view = View(ShortsUri);
        Assert.Equal(9, view.Cards.Count);
        Assert.DoesNotContain(view.Cards, c => c.Kind == HomeCardKind.Liked);
    }

    [Fact]
    public void The_spotlight_album_carries_its_payload_accent_and_first_artist()
    {
        TestScope.Fresh();
        LoadHome();

        var spotlight = Entities.Section(SpotlightUri.AsSpan());
        Assert.Equal("New release from KIMMUSEUM", Entities.Strings.Resolve(spotlight.TitleId));
        Assert.Equal(SectionPaging.NoCursor, spotlight.NextOffset);   // `nextOffset: null`

        var card = Assert.Single(View(SpotlightUri).Cards);
        Assert.Equal(HomeCardKind.Album, card.Kind);
        Assert.Equal("Still, Sometimes", card.Title);
        Assert.Equal("KIMMUSEUM", card.Subtitle);
        Assert.Equal(0xFFC02868u, card.Accent);
        Assert.NotNull(card.ImageUrl);
    }

    [Fact]
    public void The_recently_played_band_is_its_wrapped_list()
    {
        // 0.2.9 `RecentCards` / `RecentsTotal`: the band's one `List` wrapper is not an item; its twenty entities are, and
        // the list's own total (20) is the band's (the wrapper says 1).
        TestScope.Fresh();
        LoadHome();

        var recents = Entities.Section(RecentsUri.AsSpan());
        Assert.Equal(SectionKind.HomeRecentlyPlayed, recents.Kind);
        Assert.Equal(20, recents.Raw);
        Assert.Equal(20, recents.Cards);
        Assert.Equal(20, recents.Total);

        var cards = View(RecentsUri).Cards;
        Assert.Equal("Easy", cards[0].Title);
        Assert.Equal("Single - SHAUN", cards[0].Subtitle);                                      // "type - contributors"
        Assert.Equal("Album - Arcane, League of Legends", cards[1].Subtitle);
        Assert.Equal(HomeCardKind.Artist, cards[2].Kind);
        Assert.Equal("Artist", cards[2].Subtitle);
        Assert.Equal("Spotify", cards[5].Subtitle);                                            // a playlist: its first contributor
        // The Liked Songs entity (list index 6) is attached only once the Edges.Staging.cs collection patch lands; the
        // ledger counts it either way.
        Assert.True(recents.CardSlots.Length is 19 or 20);
    }

    [Fact]
    public void The_recently_played_liked_songs_entity_is_staged_under_the_canonical_uri_with_no_image()
    {
        // Read at the STAGED level: the committed SectionCards run drops a collection target until the patch lands.
        TestScope.Fresh();
        var s = Staging.Rent();
        try
        {
            Spotify.Decode.HomeFeed(HomeJson, "wavee:home"u8, s);

            bool likedFact = false;
            foreach (ref readonly var fact in s.CardFacts.Span)
                if (fact.Target.Packed.IsEmpty && s.Utf8(fact.Target.Text).SequenceEqual("spotify:collection:tracks"u8))
                    likedFact = true;
            Assert.True(likedFact);

            bool likedRow = false;
            foreach (ref readonly var row in s.Playlists.Span)
                if (row.Id.Packed.IsEmpty && s.Utf8(row.Id.Text).SequenceEqual("spotify:collection:tracks"u8))
                {
                    likedRow = true;
                    Assert.True(row.Image.IsEmpty);
                    Assert.Equal("Liked Songs", Encoding.UTF8.GetString(s.Utf8(row.Title)));
                }
            Assert.True(likedRow);
        }
        finally
        {
            Staging.Return(s);
        }
    }

    // ── the card facts ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_daily_mix_card_keeps_its_raw_format_and_its_comma_seeds()
    {
        TestScope.Fresh();
        LoadHome();

        var card = CardTitled(MadeForUri, "Daily Mix 1");
        Assert.Equal("daily-mix", card.Format);
        Assert.Equal(["IVE", "Urban Zakapa", "10CM"], card.Seeds!);
        Assert.Equal(3, card.SeedCount);
        Assert.Equal("Urban Zakapa", card.SeedAt(1));
        Assert.Equal("IVE, Urban Zakapa, 10CM and more", card.Subtitle);
        Assert.Equal(0xFF008585u, card.Accent);
        // HomeComposer.ModuleFor (the Wave-5 Home UI's card→module classifier) was deleted with that page
        // (docs/plans/wavee/home-rebuild-implementation.md §7); the new Home page classifies sections, not cards
        // (Wavee.HomeUi.SectionRoles.Of), so this format/seed decode fact no longer has a like-for-like module assert.
    }

    [Fact]
    public void A_topic_mix_card_reads_its_seeds_from_the_bare_href_anchors()
    {
        TestScope.Fresh();
        LoadHome();

        var card = CardTitled(TopMixesUri, "Chill Mix");
        Assert.Equal("topic-mix", card.Format);
        Assert.Equal(["vaultboy", "Troye Sivan", "Henry Moodie"], card.Seeds!);
    }

    [Fact]
    public void A_descripto_card_reads_its_seeds_from_the_quoted_anchors_including_non_ascii()
    {
        TestScope.Fresh();
        LoadHome();

        var card = CardTitled(JumpBackInUri, "Nostalgia 2000s Mix");
        Assert.Equal("descripto", card.Format);                         // a token PlaylistFormat folds into Other
        Assert.Equal(["pop", "easy listening", "kayōkyoku", "crunk", "dance"], card.Seeds!);
    }

    [Fact]
    public void An_inspired_by_card_strips_with_and_the_localized_trailer()
    {
        TestScope.Fresh();
        LoadHome();

        var card = CardTitled(JumpBackInUri, "earthquake Radio");
        Assert.Equal("inspiredby-mix", card.Format);
        Assert.Equal(["JISOO", "ROSÉ", "BABYMONSTER"], card.Seeds!);
        // HomeComposer.ModuleFor (the Wave-5 Home UI's card→module classifier) was deleted with that page
        // (docs/plans/wavee/home-rebuild-implementation.md §7); the new Home page classifies sections, not cards
        // (Wavee.HomeUi.SectionRoles.Of), so this format/seed decode fact no longer has a like-for-like module assert.
    }

    [Fact]
    public void A_prose_description_is_never_parsed_into_seeds()
    {
        TestScope.Fresh();
        LoadHome();

        var card = CardTitled(MadeForUri, "Discover Weekly");
        Assert.Equal("discover-weekly", card.Format);
        Assert.Null(card.Seeds);
        Assert.Equal(0, card.SeedCount);
    }

    [Fact]
    public void The_daylist_card_lands_its_generic_title_header_and_window_on_the_playlist_row()
    {
        TestScope.Fresh();
        LoadHome();

        var daylist = Entities.Playlist(EntityUri.Parse(DaylistUri.AsSpan()));
        Assert.True(daylist.Knows(PlaylistFields.Format));
        Assert.True(daylist.Knows(PlaylistFields.Daylist));
        Assert.Equal("daylist", Entities.Strings.Resolve(daylist.GenericTitleId));
        Assert.Equal("https://daylist.spotifycdn.com/headers/desktop/morning.jpg", Entities.Strings.Resolve(daylist.HeaderImageId));
        Assert.Equal((int)DateTimeOffset.Parse("2026-06-20T11:55:21Z").ToUnixTimeSeconds(), daylist.DaylistExpiresAt);
        Assert.Equal((int)DateTimeOffset.Parse("2026-06-20T08:55:21Z").ToUnixTimeSeconds(), daylist.DaylistCreatedAt);

        var card = CardTitled(JumpBackInUri, "daylist");
        Assert.Equal("daylist", card.Format);
        Assert.True(card.NeedsHydration);                               // its name is still the generic pre-title
        Assert.Equal(daylist.DaylistExpiresAt * 1000L, card.ExpiresAtMs);
        // HomeComposer.ModuleFor (the Wave-5 Home UI's card→module classifier) was deleted with that page
        // (docs/plans/wavee/home-rebuild-implementation.md §7); the new Home page classifies sections, not cards
        // (Wavee.HomeUi.SectionRoles.Of), so this format/seed decode fact no longer has a like-for-like module assert.
    }

    [Fact]
    public void NonDaylistPlaylist_KeepsHeaderImage()
    {
        // D1 (F31): `header_image_url_desktop` was parsed for every playlist card but written to the row only
        // `if (daylist)`, so `HomeCard.HeaderImageUrl`/`Playlist.HeaderImageId` stayed empty for every other card —
        // including the live fixture's own Discover Weekly card, carrying the same attribute (5). Moving the write
        // out of the `if (daylist)` block is what lets the WideEditorial role and the Discover-Weekly lead fire on
        // the live feed's own shape.
        TestScope.Fresh();
        LoadHome();

        var dw = Entities.Playlist(EntityUri.Parse("spotify:playlist:37i9dQZEVXcKDbGa6CckPI".AsSpan()));
        Assert.False(dw.Knows(PlaylistFields.Daylist));
        Assert.Equal("https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/dw/desktop/en",
            Entities.Strings.Resolve(dw.HeaderImageId));
    }

    [Fact]
    public void A_sparse_recents_mention_never_blanks_the_rich_row_decoded_before_it()
    {
        // Daily Mix 1 is a full card in band 2 and a title-only mention in band 3; the mention writes at Seed authority.
        TestScope.Fresh();
        LoadHome();

        var mix = Entities.Playlist(EntityUri.Parse("spotify:playlist:37i9dQZF1E38wY9VFrwrWy".AsSpan()));
        Assert.Equal("IVE, Urban Zakapa, 10CM and more", Entities.Strings.Resolve(mix.DescriptionId));
        Assert.Equal(50, mix.TrackCount);
    }

    // ── subjects ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_facet_subject_lands_on_its_own_row_and_leaves_the_feed_untouched()
    {
        TestScope.Fresh();
        LoadHome("wavee:home:podcasts-chip");

        var facet = Entities.HomeFeed("podcasts-chip".AsSpan());
        Assert.True(facet.Knows(HomeFields.Sections));
        Assert.Equal(31, facet.SectionCount);
        Assert.Equal("podcasts-chip", Entities.Strings.Resolve(facet.FacetId));

        var feed = Entities.HomeFeed();
        Assert.False(feed.Knows(HomeFields.Sections));
        Assert.Equal(0, feed.SectionCount);
    }

    [Fact]
    public void An_empty_facet_answer_is_an_answered_complete_empty_list()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.HomeFeed("""{"data":{"home":{"sectionContainer":{"sections":{"items":[]}}}}}"""u8,
            "wavee:home:audiobooks-chip"u8, s);
        TestScope.CommitAndPublish(s);

        var facet = Entities.HomeFeed("audiobooks-chip".AsSpan());
        Assert.True(facet.Knows(HomeFields.Sections));
        Assert.Equal(EdgeState.Complete, facet.SectionState);
        Assert.Equal(0, facet.SectionCount);
        Assert.Equal(HomeState.Empty, facet.State(concluded: true));
    }

    [Fact]
    public void A_null_home_stages_nothing()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.HomeFeed("""{"data":{"home":null}}"""u8, "wavee:home"u8, s);
        Assert.Equal(0, s.Homes.Count);
        Assert.Equal(0, s.Sections.Count);
        Staging.Return(s);
    }

    // ── the new card facts (A6: ReleasedAtMs, PlayedState, VideoThumbUrl, Explicit, Unplayable, Show) ────────────────────
    // Small hand-written fixtures (not the personal captures) shaped exactly like `podcasts.json`'s episode cards and
    // `home.json`'s album cards (§4.3): an episode with a video thumb, an in-progress resume and an explicit rating
    // behind a paywall, and an album whose release date lands on the entity table the same way every album page reads it.

    const string FactsSectionUri = "spotify:section:0JQ5DAFacts0000000000";

    static void LoadFacts(string json, string subject = "wavee:home")
    {
        var s = Staging.Rent();
        Spotify.Decode.HomeFeed(Encoding.UTF8.GetBytes(json), Encoding.UTF8.GetBytes(subject), s);
        TestScope.CommitAndPublish(s);
    }

    static string FactsDocument(string cardJson) =>
        "{\"data\":{\"home\":{\"sectionContainer\":{\"sections\":{\"items\":[{\"uri\":\""
        + FactsSectionUri
        + "\",\"data\":{\"__typename\":\"HomeGenericSectionData\",\"title\":\"Card facts\"},\"sectionItems\":{\"totalCount\":1,\"items\":[{\"content\":{\"data\":"
        + cardJson
        + "}}]}}]}}}}}";

    const string EpisodeCardJson = """
        {"__typename":"Episode","uri":"spotify:episode:facts0episode000000001","name":"Ep. 42: The Facts",
         "duration":{"totalMilliseconds":2400000},
         "releaseDate":{"isoString":"2026-09-20T00:00:00Z"},
         "playedState":{"playPositionMilliseconds":60000,"state":"IN_PROGRESS"},
         "mediaTypes":["VIDEO","AUDIO"],
         "videoPreviewThumbnail":{"imagePreview":{"data":{"sources":[
           {"imageFormat":"WEBP","maxWidth":1280,"maxHeight":720,"url":"https://img.example/1280.jpg"},
           {"imageFormat":"WEBP","maxWidth":640,"maxHeight":360,"url":"https://img.example/640.jpg"}
         ]}}},
         "contentRating":{"label":"EXPLICIT"},
         "playability":{"playable":true},
         "restrictions":{"paywallContent":true},
         "podcastV2":{"data":{"name":"The Fake Show","uri":"spotify:show:facts0show00000000001"}}}
        """;

    [Fact]
    public void Episode_card_stages_release_played_state_video_thumb_explicit_and_show()
    {
        TestScope.Fresh();
        LoadFacts(FactsDocument(EpisodeCardJson));

        var card = CardTitled(FactsSectionUri, "Ep. 42: The Facts");
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), card.ReleasedAtMs);
        Assert.Equal(2, card.PlayedState);                    // IN_PROGRESS
        Assert.Equal(60000, card.ResumeMs);
        Assert.Equal("https://img.example/1280.jpg", card.VideoThumbUrl);
        Assert.True(card.HasVideo);
        Assert.True(card.IsExplicit);
        // restrictions.paywallContent: true, even though playability.playable is true — paywall wins.
        Assert.False(card.IsPlayable);
        Assert.Equal("The Fake Show", card.ShowName);
        Assert.Equal("spotify:show:facts0show00000000001", card.ShowUri);
    }

    [Fact]
    public void Episode_card_not_started_and_fully_played_states_and_playable_default()
    {
        TestScope.Fresh();
        LoadFacts(FactsDocument("""
            {"__typename":"Episode","uri":"spotify:episode:facts0episode000000002","name":"Ep. 1: Fresh",
             "playedState":{"playPositionMilliseconds":0,"state":"NOT_STARTED"},
             "contentRating":{"label":"NONE"}}
            """));
        var fresh = CardTitled(FactsSectionUri, "Ep. 1: Fresh");
        Assert.Equal(1, fresh.PlayedState);                   // NOT_STARTED
        Assert.False(fresh.IsExplicit);
        Assert.True(fresh.IsPlayable);                        // unruled → never dimmed by omission
        Assert.False(fresh.HasVideo);
        Assert.Null(fresh.VideoThumbUrl);

        TestScope.Fresh();
        LoadFacts(FactsDocument("""
            {"__typename":"Episode","uri":"spotify:episode:facts0episode000000003","name":"Ep. 0: Done",
             "playedState":{"playPositionMilliseconds":2400000,"state":"COMPLETED"}}
            """));
        Assert.Equal(3, CardTitled(FactsSectionUri, "Ep. 0: Done").PlayedState);   // COMPLETED reads as FullyPlayed
    }

    [Fact]
    public void Album_card_ReleasedAtMs_reads_the_Album_entitys_own_ReleaseAt_column()
    {
        TestScope.Fresh();
        LoadFacts(FactsDocument("""
            {"__typename":"Album","uri":"spotify:album:facts0album0000000000001","name":"Fake LP",
             "releaseDate":{"isoString":"2026-08-01T00:00:00Z"},
             "artists":{"items":[{"profile":{"name":"Fake Artist"},"uri":"spotify:artist:facts0artist000000001"}]}}
            """));

        var album = Entities.Album(EntityUri.Parse("spotify:album:facts0album0000000000001".AsSpan()));
        var card = CardTitled(FactsSectionUri, "Fake LP");
        Assert.Equal(album.ReleaseAt * 1000L, card.ReleasedAtMs);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), card.ReleasedAtMs);
    }

    [Fact]
    public void Show_card_contentRatingV2_labels_carry_Explicit()
    {
        TestScope.Fresh();
        LoadFacts(FactsDocument("""
            {"__typename":"Podcast","uri":"spotify:show:facts0show000000000002","name":"Spicy Takes",
             "publisher":{"name":"Fake Publisher"},"contentRatingV2":{"labels":["EXPLICIT"]}}
            """));
        Assert.True(CardTitled(FactsSectionUri, "Spicy Takes").IsExplicit);
    }

    [Fact]
    public void Non_episode_cards_carry_no_show_or_video_thumb()
    {
        TestScope.Fresh();
        LoadFacts(FactsDocument("""
            {"__typename":"Album","uri":"spotify:album:facts0album0000000000002","name":"No Show LP"}
            """));
        var card = CardTitled(FactsSectionUri, "No Show LP");
        Assert.Null(card.ShowName);
        Assert.Null(card.ShowUri);
        Assert.Null(card.VideoThumbUrl);
        Assert.Equal(0, card.ReleasedAtMs);
    }

    // ── the allocation gate (P1, P8) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_warm_home_decode_allocates_nothing()
    {
        TestScope.Fresh();
        var json = HomeJson;
        var s = Staging.Rent();
        for (int i = 0; i < 2; i++) { Spotify.Decode.HomeFeed(json, "wavee:home"u8, s); s.Reset(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Spotify.Decode.HomeFeed(json, "wavee:home"u8, s);
        long after = GC.GetAllocatedBytesForCurrentThread();
        s.Reset();
        Staging.Return(s);

        Assert.Equal(0L, after - before);
    }

    // ── what's new (0.2.9 WhatsNewParserTests) ────────────────────────────────────────────────────────────────────────

    static List<Notification> WhatsNewRows()
    {
        var rows = new List<Notification>();
        Assert.Equal(2, Spotify.Decode.WhatsNew(HomeFixtures.Fixture("home", "whatsnew-feed.json"), rows));
        return rows;
    }

    [Fact]
    public void WhatsNew_KeepsKnownTypenames_SkipsUnknown()
    {
        var rows = WhatsNewRows();
        Assert.Equal(2, rows.Count);                                    // the Chapter is skipped
        Assert.Contains(rows, n => n.ReleaseKind == NewReleaseKind.Album);
        Assert.Contains(rows, n => n.ReleaseKind == NewReleaseKind.Episode);
        Assert.DoesNotContain(rows, n => n.Id.Contains("chapter", StringComparison.Ordinal));
        Assert.All(rows, n => Assert.Equal(NotifyCategory.NewRelease, n.Category));
    }

    [Fact]
    public void WhatsNew_Album_MapsFields_SeenIsRead()
    {
        var album = WhatsNewRows().First(n => n.ReleaseKind == NewReleaseKind.Album);
        Assert.Equal("spotify:album:fakealb1", album.Id);
        Assert.Equal("Fake Album One", album.Title);
        Assert.Equal("ALBUM", album.AlbumType);
        Assert.Equal("Fake Artist, Guest", album.Creator);
        Assert.Equal("https://i.example/alb1.jpg", album.ImageUrl);
        Assert.False(album.IsUnread);                                   // state == SEEN
        Assert.False(album.Played);
        Assert.Equal(DateTimeOffset.Parse("2024-01-15T00:00:00Z").ToUnixTimeMilliseconds(), album.TimestampMs);
        Assert.False(album.Subject.IsValid);                            // a text-form uri: the host parses it on the UI thread
    }

    [Fact]
    public void WhatsNew_Episode_MapsFields_UnseenIsUnread()
    {
        var episode = WhatsNewRows().First(n => n.ReleaseKind == NewReleaseKind.Episode);
        Assert.Equal("spotify:episode:fakeep1", episode.Id);
        Assert.Equal("Fake Episode One", episode.Title);
        Assert.Equal("Fake Podcast", episode.Creator);
        Assert.Equal("https://i.example/ep1.jpg", episode.ImageUrl);
        Assert.True(episode.IsUnread);                                  // state != SEEN
        Assert.True(episode.Played);                                    // playedState == FULLY_PLAYED
        Assert.Null(episode.AlbumType);
    }

    [Fact]
    public void WhatsNew_EmptyOrMalformed_ReturnsEmpty()
    {
        var rows = new List<Notification>();
        Assert.Equal(0, Spotify.Decode.WhatsNew("""{"data":{}}"""u8, rows));
        Assert.Equal(0, Spotify.Decode.WhatsNew("[]"u8, rows));
        Assert.Empty(rows);
    }

    [Fact]
    public void WhatsNew_AGidUri_CarriesItsSubject()
    {
        var rows = new List<Notification>();
        Spotify.Decode.WhatsNew("""
            {"data":{"whatsNewFeedItems":{"items":[{"state":{"state":"NONE"},"timestamp":{"isoString":"2024-01-15T00:00:00Z"},
             "content":{"data":{"__typename":"Album","uri":"spotify:album:1I80HwIDdWXtmA3Fqsbqnl","name":"Still, Sometimes"}}}]}}}
            """u8, rows);
        var row = Assert.Single(rows);
        Assert.True(row.Subject.IsValid);
        Assert.Equal(EntityKind.Album, row.Subject.Kind);
    }

    // ── the account's top content (0.2.9 UserTopMapperTests) ──────────────────────────────────────────────────────────

    const string UserTopResponse = """
    {
      "data": { "me": { "profile": {
        "topArtists": { "items": [
          { "data": { "uri": "spotify:artist:a1", "profile": { "name": "Artist One" } } }
        ] },
        "topTracks": { "items": [
          { "data": {
            "uri": "spotify:track:t1", "name": "Track One",
            "duration": { "totalMilliseconds": 201000 },
            "artists": { "items": [
              { "uri": "spotify:artist:a1", "profile": { "name": "Artist One" } }
            ] },
            "albumOfTrack": { "uri": "spotify:album:r1", "coverArt": { "sources": [] } }
          } },
          { "data": { "uri": "spotify:track:t1", "name": "Duplicate" } }
        ] }
      } } }
    }
    """;

    static void LoadTop(string json)
    {
        var s = Staging.Rent();
        Spotify.Decode.UserTop(Encoding.UTF8.GetBytes(json), "spotify:user:me"u8, s);
        TestScope.CommitAndPublish(s);
    }

    static int Me => Entities.User(EntityUri.Parse("spotify:user:me".AsSpan())).Slot;

    [Fact]
    public void UserTop_MapsArtistsAndTracksFromTheSameDocument()
    {
        TestScope.Fresh();
        LoadTop(UserTopResponse);

        var edges = Entities.Current.Edges;
        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:a1".AsSpan()));
        var track = Entities.Track(EntityUri.Parse("spotify:track:t1".AsSpan()));
        Assert.Equal(new[] { artist.Slot }, edges.UserTopArtists.Targets(Me).ToArray());
        Assert.Equal(new[] { track.Slot }, edges.UserTopTracks.Targets(Me).ToArray());
        Assert.Equal("Artist One", artist.Name);
        Assert.Equal("Track One", track.Title);                         // the repeated "Duplicate" never staged a row
        Assert.Equal(201000, track.DurationMs);
    }

    [Fact]
    public void UserTop_TheProfilePathWins_OverTheBareDataPath()
    {
        TestScope.Fresh();
        LoadTop("""
            {"data":{
              "topArtists":{"items":[{"data":{"uri":"spotify:artist:a9","profile":{"name":"Nine"}}}]},
              "me":{"profile":{"topArtists":{"items":[{"data":{"uri":"spotify:artist:a1","profile":{"name":"Artist One"}}}]}}}
            }}
            """);

        var edges = Entities.Current.Edges;
        Assert.Equal(new[] { Entities.Artist(EntityUri.Parse("spotify:artist:a1".AsSpan())).Slot },
                     edges.UserTopArtists.Targets(Me).ToArray());
        // No track list anywhere is still an answer: an empty, complete run (a new account's podium).
        Assert.Equal(0, edges.UserTopTracks.Count(Me));
        Assert.Equal(EdgeState.Complete, edges.UserTopTracks.State(Me));
    }

    [Fact]
    public void UserTop_ADocumentWithoutData_StagesNothing()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.UserTop("""{"errors":[{"message":"nope"}]}"""u8, "spotify:user:me"u8, s);
        Assert.Equal(0, s.TopRuns.Count);
        Staging.Return(s);
    }
}
