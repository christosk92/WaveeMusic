// ── Wavee.Tests/EntitiesFakeAlbumTests.cs — the album / show seed's contract (Entities.Fake.Album.cs) ─────────────────
//
// The WP-5.M contract §4 as facts, plus the gap auditor's three rows: G-260 (nothing the album, show or drawer pages
// demand stays unseeded under --fake, so no demand reaches the provider offline), G-261 (album titles of their own)
// and G-262 (billed artists and a top track that is a member). Counts, determinism (no second clock read), the
// cross-fixture agreement rule (ch 31 §0.8: a header and its membership agree) and the prerelease resolve. Everything
// drives the real `Entities.SeedFake` and reads HANDLES afterwards — no source text, no private seed arrays.

using System.Globalization;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EntitiesFakeAlbumTests
{
    const long Now0 = 1_788_000_000;   // Platform.Clock.FixedSeedEpoch, restated so this file has no Platform dependency
    const long Day = 86_400;

    static void Seed(long now0 = Now0)
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(now0);
    }

    static string Al(int i) => "spotify:album:al" + i.ToString(CultureInfo.InvariantCulture);
    static Album AlbumOf(int i) => Entities.Album(EntityUri.Parse(Al(i)));
    static Show ShowOf(int i) => Entities.Show(EntityUri.Parse("spotify:show:sh" + i.ToString(CultureInfo.InvariantCulture)));
    static ReadOnlySpan<Track> Rows(Album a) => System.Runtime.InteropServices.MemoryMarshal.Cast<int, Track>(a.TrackSlots);

    // ── G-260: every group the pages demand is answered ─────────────────────────────────────────────────────────────

    [Fact]
    public void Every_seeded_album_knows_everything_the_album_page_demands()
    {
        Seed();
        for (int i = 0; i <= 14; i++)
        {
            var album = AlbumOf(i);
            Assert.True(album.Knows(AlbumFields.All), Al(i) + " is missing an album group");
            Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumTracks.State(album.Slot));
        }
    }

    [Fact]
    public void Every_seeded_track_knows_row_audio_tags_and_video()
    {
        Seed();
        const TrackFields Demanded = TrackFields.Row | TrackFields.Audio | TrackFields.Tags | TrackFields.Video;
        for (int i = 0; i < 166; i++)
        {
            var track = Entities.Track(EntityUri.Parse("spotify:track:tr" + i.ToString(CultureInfo.InvariantCulture)));
            Assert.True(track.Knows(Demanded), track.Uri.Text + " is missing a demanded group");
            Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackTags.State(track.Slot));
            Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackArtists.State(track.Slot));
        }
        for (int i = 0; i <= 14; i++)
            foreach (var row in Rows(AlbumOf(i)))
            {
                Assert.True(row.Knows(Demanded), row.Uri.Text + " is missing a demanded group");
                Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackVersions.State(row.Slot));
                Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackCredits.State(row.Slot));
                Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackWaveform.State(row.Slot));
            }
    }

    [Fact]
    public void Every_seeded_episode_knows_all_its_groups_and_every_show_its_about()
    {
        Seed();
        for (int s = 0; s <= 8; s++)
        {
            var show = ShowOf(s);
            Assert.True(show.Knows(ShowFields.All));
            foreach (int slot in show.EpisodeSlots)
                Assert.True(new Episode(slot).Knows(EpisodeFields.All));
        }
    }

    // ── G-261: the album titles are their own ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_album_title_equals_a_playlist_or_folder_name()
    {
        Seed();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int p = 0; p < 7; p++)
            names.Add(Entities.Strings.Resolve(Entities.Playlist(EntityUri.Parse("spotify:playlist:pl" + p.ToString(CultureInfo.InvariantCulture))).TitleId));
        foreach (var row in Wavee.User.Me.Rootlist)
            if (!row.FolderName.IsEmpty) names.Add(Entities.Strings.Resolve(row.FolderName));
        Assert.True(names.Count >= 7);

        for (int i = 0; i <= 14; i++)
        {
            string title = AlbumOf(i).Title;
            Assert.False(string.IsNullOrEmpty(title));
            Assert.DoesNotContain(title, names);
        }
    }

    [Fact]
    public void Al3_is_the_2011_single_two_summers_and_keeps_the_base_shape()
    {
        Seed();
        var al3 = AlbumOf(3);
        Assert.Equal("Two Summers", al3.Title);
        Assert.Equal(AlbumKind.Single, al3.Kind);
        Assert.Equal((ushort)2011, al3.Year);
        Assert.Equal(2, al3.TrackCount);
        Assert.True(al3.Knows(AlbumFields.Detail));
        Assert.EndsWith("cover03.jpg", Entities.Strings.Resolve(al3.ImageId));
    }

    // ── G-262 and the agreement rule (ch 31 §0.8) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_album_knows_its_top_track_and_the_top_track_is_a_member()
    {
        Seed();
        for (int i = 0; i <= 14; i++)
        {
            var album = AlbumOf(i);
            Assert.True(album.Knows(AlbumFields.TopTrack));
            if (i == 13)
            {
                // Every row of the prerelease is pending with no plays: "no star" is the known answer (a star on a row
                // that is not out would contradict ch 05 W20's pending treatment).
                Assert.Equal(Table.None, album.TopTrack.Slot);
                continue;
            }
            Assert.True(album.TrackSlots.IndexOf(album.TopTrack.Slot) >= 0, Al(i) + "'s top track is not a member");
            Assert.Equal(album.TrackSlots[0], album.TopTrack.Slot);        // ch 31 §3.1's descending curve: row 1 is the star
        }
    }

    [Fact]
    public void Every_album_bills_an_artist_and_its_header_agrees_with_its_membership()
    {
        Seed();
        for (int i = 0; i <= 14; i++)
        {
            var album = AlbumOf(i);
            Assert.True(album.ArtistSlots.Length >= 1, Al(i) + " bills nobody");
            Assert.Equal(album.TrackCount, album.TrackSlots.Length);
            foreach (var row in Rows(album))
                Assert.Equal(album.Slot, row.AlbumSlot);                   // the member's album column names this album
        }
    }

    [Fact]
    public void The_four_album_kinds_keep_the_base_six_cycle_counts()
    {
        Seed();
        int[] counts = [1, 5, 12, 2, 18, 10];
        AlbumKind[] kinds = [AlbumKind.Single, AlbumKind.EP, AlbumKind.Album, AlbumKind.Single, AlbumKind.Compilation, AlbumKind.Album];
        for (int i = 0; i < 13; i++)
        {
            Assert.Equal(counts[i % 6], AlbumOf(i).TrackSlots.Length);
            Assert.Equal(kinds[i % 6], AlbumOf(i).Kind);
        }
    }

    // ── al2, the rich album ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Al2_carries_every_trailing_section_at_the_contract_counts()
    {
        Seed();
        var al2 = AlbumOf(2);
        var e = Entities.Current.Edges;
        Assert.Equal(2, al2.ArtistSlots.Length);
        Assert.Equal(2, al2.VersionSlots.Length);
        Assert.Equal(6, al2.MoreBySlots.Length);                           // "Show all 6"
        Assert.Equal(7, e.AlbumRecommendations.Count(al2.Slot));
        Assert.Equal(7, al2.FeaturedOnSlots.Length);
        Assert.Equal(6, al2.SimilarSlots.Length);
        Assert.Equal(6, al2.MerchSlots.Length);

        int noPrice = 0, noShop = 0;
        foreach (int listing in al2.MerchSlots)
        {
            ref var m = ref Album.MerchAt(listing);
            Assert.False(m.Name.IsEmpty);
            if (m.Price.IsEmpty) noPrice++;
            if (m.ShopUrl.IsEmpty) noShop++;
        }
        Assert.Equal(1, noPrice);
        Assert.Equal(1, noShop);

        Span<int> all = stackalloc int[16];
        int distinct = Album.PageRules.DistinctArtists(al2, all);
        Assert.Equal(5, distinct);
        Assert.Equal(3, Album.PageRules.FaceOverflow(al2.ArtistSlots.Length, distinct, drawn: 2));   // the pile's +3

        // The other albums answer each trailing relation EMPTY: absent, not a skeleton.
        var al7 = AlbumOf(7);
        Assert.Equal(EdgeState.Complete, e.AlbumMerch.State(al7.Slot));
        Assert.Equal(0, al7.MoreBySlots.Length + al7.SimilarSlots.Length + al7.MerchSlots.Length);
    }

    [Fact]
    public void Al2_release_facts_are_whole_and_row_one_is_the_most_played()
    {
        Seed();
        var al2 = AlbumOf(2);
        var facts = Album.ReleaseFactsRules.Of(al2, Now0);
        Assert.Equal(12, facts.SongsOut);
        Assert.Equal(12, facts.SongsTotal);
        Assert.NotNull(facts.Label);
        Assert.Equal(2, facts.Notes.Count);
        Assert.NotNull(facts.Released);

        var rows = Rows(al2);
        for (int k = 1; k < rows.Length; k++) Assert.True(rows[k - 1].PlayCount > rows[k].PlayCount);
    }

    [Fact]
    public void The_drawer_fixture_carries_isrc_tags_formats_waveform_credits_and_versions()
    {
        Seed();
        var e = Entities.Current.Edges;
        var t = Rows(AlbumOf(2))[0];
        Assert.True(t.Knows(TrackFields.Isrc | TrackFields.Files | TrackFields.Tags | TrackFields.Video));
        Assert.Equal(12, Entities.Strings.Resolve(t.IsrcId).Length);
        Assert.Equal(2, t.Tags.Length);
        Assert.True(t.HasVideo);
        Assert.True(t.IsLossless);

        bool flac = false;
        foreach (var rung in t.Formats) flac |= rung.FormatId == 16;
        Assert.True(flac);
        Assert.Equal(Spotify.Decode.WaveformColumns, e.TrackWaveform.Count(t.Slot));

        var credits = e.TrackCredits.Payload(t.Slot);
        var linked = e.TrackCredits.Targets(t.Slot);
        Assert.True(credits.Length >= 4);
        bool unlinked = false, songwriters = false;
        for (int i = 0; i < credits.Length; i++)
        {
            unlinked |= linked[i] == Table.None;
            songwriters |= Entities.Strings.Resolve(credits[i].Group) == "Songwriters";
        }
        Assert.True(unlinked && songwriters);

        var versions = e.TrackVersions.Payload(t.Slot);
        Assert.Equal(2, versions.Length);
        Assert.Equal(TrackVersionKind.Video, versions[0].Kind);
        Assert.Equal(TrackVersionKind.Audio, versions[1].Kind);
        Assert.Equal(t.VideoCounterpart.Slot, e.TrackVersions.Targets(t.Slot)[0]);
    }

    // ── al3, al4 ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Al3_is_a_short_release_with_a_video_and_related_artists()
    {
        Seed();
        var al3 = AlbumOf(3);
        var rows = Rows(al3);
        Assert.True(Album.PageRules.IsShortRelease(al3.Kind, rows.Length));
        Assert.True(rows[0].HasVideo);
        Assert.True(rows[0].VideoCounterpart.IsValid);
        Assert.Equal(TrackVersionKind.Video, Entities.Current.Edges.TrackVersions.Payload(rows[0].Slot)[0].Kind);
        int seed = Album.PageRules.SeedTrackIndex(rows);
        Assert.Equal(6, rows[seed].RelatedArtistSlots.Length);
    }

    [Fact]
    public void Al4_the_compilation_credits_a_different_artist_on_its_rows()
    {
        Seed();
        var rows = Rows(AlbumOf(4));
        var seen = new HashSet<int>();
        foreach (var row in rows) seen.Add(row.ArtistSlots[0]);
        Assert.True(seen.Count >= 6);
        Assert.Equal((byte)2, AlbumOf(4).DiscCount);
    }

    // ── al13, the prerelease; al14, the waterfall ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Al13_is_upcoming_its_rows_are_not_out_and_its_route_resolves()
    {
        Seed();
        var al13 = AlbumOf(13);
        Assert.True(al13.IsPreRelease);
        Assert.Equal(10, al13.TrackSlots.Length);
        foreach (var row in Rows(al13)) Assert.True(row.NotYetOut(Now0));

        int at = Album.Upcoming.Of(al13, Now0);
        Assert.Equal(al13.PreReleaseEnd, at);
        Assert.True(Album.Upcoming.NeedsLink(al13, Now0));
        Assert.Equal("spotify:prerelease:pr13", Album.Upcoming.SaveTarget(al13, Now0).Text);
        Assert.Equal(al13, Album.Upcoming.ResolvePreRelease(EntityUri.Parse("spotify:prerelease:pr13")));
        Assert.Equal("spotify:prerelease:pr13",
            Album.Upcoming.PreSaveTarget(EntityUri.Parse("spotify:prerelease:pr13"), Now0).Text);
        Assert.True(Album.ReleaseFactsRules.Of(al13, Now0).ReleasesInFuture);
    }

    [Fact]
    public void The_countdown_survives_the_clock_trap()
    {
        // The fixed seed epoch is already past on the machine running the app; the lead keeps the countdown in the
        // future for two years past it (the seed header's clock trap).
        Seed();
        var al13 = AlbumOf(13);
        Assert.True(Album.Upcoming.Of(al13, Now0 + 365 * Day) > 0);
        Assert.True(Album.Upcoming.Of(al13, Now0 + 730 * Day) > 0);
    }

    [Fact]
    public void Al14_is_the_waterfall_three_of_twelve_with_one_dateless_pending_row()
    {
        Seed();
        var al14 = AlbumOf(14);
        Assert.False(al14.IsPreRelease);                                   // a waterfall carries no album-level flag
        var facts = Album.ReleaseFactsRules.Of(al14, Now0);
        Assert.Equal(3, facts.SongsOut);
        Assert.Equal(12, facts.SongsTotal);

        int dateless = 0;
        foreach (var row in Rows(al14))
            if (row.NotYetOut(Now0) && row.AvailableAt == 0) dateless++;
        Assert.Equal(1, dateless);
        Assert.True(Album.Upcoming.Of(al14, Now0) > Now0);
        Assert.True(Album.Upcoming.NeedsLink(al14, Now0));
    }

    // ── shows ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_show_holds_its_episodes_newest_first_numbered_down_from_its_total()
    {
        Seed();
        for (int s = 0; s < 8; s++)
        {
            var show = ShowOf(s);
            var slots = show.EpisodeSlots;
            Assert.Equal(s == 0 ? 14 : 8 + s % 5, slots.Length);           // sh0 is the podcast rework's 14-episode serial
            for (int i = 0; i < slots.Length; i++)
            {
                var ep = new Episode(slots[i]);
                Assert.Equal(show.Slot, ep.ShowSlot);
                Assert.Equal((22 + (s * 7 + i * 13) % 50) * 60_000, ep.DurationMs);
                Assert.Equal((int)(Now0 - (i * 7 * Day + s * Day)), ep.PublishedAt);
                Assert.Equal(show.TotalEpisodes - i, ep.Number);             // the title's own "#N"
                Assert.Equal(EpisodeKind.Full, ep.Kind);
                if (i > 0) Assert.True(new Episode(slots[i - 1]).PublishedAt > ep.PublishedAt);
            }
        }
    }

    [Fact]
    public void The_chapter_09_shows_keep_one_episode_in_progress_at_a_third()
    {
        // sh0-sh2 are the podcast rework's three visits (EntitiesFakeTests); sh3-sh7 keep ch 09's continue card.
        Seed();
        Span<float> pcts = stackalloc float[16];
        for (int s = 3; s < 8; s++)
        {
            var slots = ShowOf(s).EpisodeSlots;
            for (int i = 0; i < slots.Length; i++) pcts[i] = Episode.Rules.PctOf(new Episode(slots[i]));
            Assert.Equal(1, Episode.Rules.ResumePick(pcts[..slots.Length]));
            Assert.Equal(1f / 3f, pcts[1], 3);
            Assert.True(new Episode(slots[1]).PlayedAt > new Episode(slots[1]).PublishedAt);   // played after it came out
            Assert.Equal(0, new Episode(slots[0]).PlayedAt);                                   // never played: no stamp
        }
    }

    [Fact]
    public void Sh3_has_a_card_with_no_description_and_one_with_no_art()
    {
        Seed();
        int noDescription = 0, noArt = 0;
        foreach (int slot in ShowOf(3).EpisodeSlots)
        {
            var ep = new Episode(slot);
            if (ep.DescriptionId.IsEmpty) noDescription++;
            if (ep.ImageId.IsEmpty) noArt++;
        }
        Assert.Equal(1, noDescription);
        Assert.Equal(1, noArt);
    }

    [Fact]
    public void Sh7_is_partial_and_offers_load_more_sh8_is_an_empty_unsaved_show()
    {
        Seed();
        var e = Entities.Current.Edges.ShowEpisodes;
        var sh7 = ShowOf(7);
        Assert.Equal(EdgeState.Partial, e.State(sh7.Slot));
        Assert.True(sh7.TotalEpisodes > sh7.EpisodeSlots.Length);
        Assert.True(Episode.Rules.CanLoadMore(sh7.EpisodesAsked, 0, sh7.TotalEpisodes));

        var sh8 = ShowOf(8);
        Assert.Equal(EdgeState.Complete, e.State(sh8.Slot));
        Assert.Equal(0, sh8.TotalEpisodes);
        Assert.True(Episode.Rules.IsEmptyShow(sh8.TotalEpisodes, sh8.EpisodeSlots.Length, Episode.Rules.Status.All));
        Assert.False(Wavee.User.Me.Has(LibraryEdgeKind.SavedShows, sh8.Slot));
        Assert.Equal(8, Wavee.User.Me.Count(LibraryEdgeKind.SavedShows));    // the base seed's count still stands
        Assert.Equal(18, Wavee.User.Me.Count(LibraryEdgeKind.SavedAlbums));   // 13 base + the 5 library-rework albums (Entities.Fake.Library.cs)
    }

    // ── determinism (ch 31 §7.2, §8 assertions 1-2) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Seeding_twice_is_identical_and_only_the_dated_values_move_with_now0()
    {
        Seed();
        string title = AlbumOf(2).Title;
        int merchCount = AlbumOf(2).MerchSlots.Length;
        int end = AlbumOf(13).PreReleaseEnd;
        int published = new Episode(ShowOf(3).EpisodeSlots[2]).PublishedAt;
        uint plays = Rows(AlbumOf(2))[0].PlayCount;

        Seed();
        Assert.Equal(title, AlbumOf(2).Title);
        Assert.Equal(merchCount, AlbumOf(2).MerchSlots.Length);
        Assert.Equal(end, AlbumOf(13).PreReleaseEnd);
        Assert.Equal(plays, Rows(AlbumOf(2))[0].PlayCount);

        Seed(Now0 + Day);
        Assert.Equal(title, AlbumOf(2).Title);
        Assert.Equal(plays, Rows(AlbumOf(2))[0].PlayCount);
        Assert.Equal(Day, (long)AlbumOf(13).PreReleaseEnd - end);
        Assert.Equal(Day, (long)new Episode(ShowOf(3).EpisodeSlots[2]).PublishedAt - published);
    }
}
