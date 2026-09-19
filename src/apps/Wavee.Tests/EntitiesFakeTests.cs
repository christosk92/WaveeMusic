// ── Wavee.Tests/EntitiesFakeTests.cs — the offline demo seed's contract (ch 31; gap G-015, decision D17) ────────────
//
// This is the SEED-CORE cut's gate: determinism (no clock read beyond `now0`, no randomness), the fixture counts the
// gap register names verbatim (rootlist with folders, the library's four sets, pins, 166 liked, 16 covers), and the
// customizer's `Sample`/`SampleShortcutCount` answering the same on a scope that never seeded at all (ch 31 §7.2
// rule D). No source-text tests: everything below drives the real `Entities.SeedFake`, never greps it.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EntitiesFakeTests
{
    const long Now0 = 1_788_000_000;   // Platform.Clock.FixedSeedEpoch, restated so this file has no Platform dependency

    [Fact]
    public void Wrap_is_total_for_every_int_including_the_extremes()
    {
        // ch 31 §7.2 rule B, and the exact crash the 0.2.9 file names: a huge or negative index must never throw or
        // fall outside [0, n).
        foreach (int i in new[] { int.MinValue, -1, 0, int.MaxValue, 165, -165 })
        {
            int w = Entities.Wrap(i, 16);
            Assert.InRange(w, 0, 15);
        }
    }

    [Fact]
    public void Seeding_twice_with_the_same_epoch_produces_identical_fixtures()
    {
        // ch 31 §8 assertion 1 (scoped to the columns this cut writes, not "every column of every table").
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        var track0First = Entities.Track(EntityUri.Parse("spotify:track:tr0"));
        string title0First = track0First.Title;
        uint playCount0First = track0First.PlayCount;
        var liked0First = Wavee.User.Me.LikedEdges[0];

        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        var track0Second = Entities.Track(EntityUri.Parse("spotify:track:tr0"));

        Assert.Equal(title0First, track0Second.Title);
        Assert.Equal(playCount0First, track0Second.PlayCount);
        Assert.Equal(liked0First.AddedAt, Wavee.User.Me.LikedEdges[0].AddedAt);
    }

    [Fact]
    public void Only_the_dated_columns_move_when_now0_shifts_by_exactly_one_day()
    {
        // ch 31 §8 assertion 2: proof there is no second clock read anywhere in the seed.
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        string titleBefore = Entities.Track(EntityUri.Parse("spotify:track:tr12")).Title;
        int likedAddedAtBefore = Wavee.User.Me.LikedEdges[12].AddedAt;

        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0 + 86_400);
        string titleAfter = Entities.Track(EntityUri.Parse("spotify:track:tr12")).Title;
        int likedAddedAtAfter = Wavee.User.Me.LikedEdges[12].AddedAt;

        Assert.Equal(titleBefore, titleAfter);                       // undated: unchanged
        Assert.Equal(86_400, likedAddedAtAfter - likedAddedAtBefore); // dated: shifted by exactly the offset
    }

    [Fact]
    public void The_library_has_the_registers_four_sets_plus_liked_and_pins()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        var me = Wavee.User.Me;

        Assert.True(me.IsValid);
        Assert.Equal(166, me.Count(LibraryEdgeKind.Liked));
        Assert.Equal(18, me.Count(LibraryEdgeKind.SavedAlbums));   // 13 base + the 5 library-rework letter/failure albums (Entities.Fake.Library.cs)
        Assert.Equal(12, me.Count(LibraryEdgeKind.FollowedArtists));
        Assert.Equal(8, me.Count(LibraryEdgeKind.SavedShows));
        Assert.Equal(5, me.Count(LibraryEdgeKind.Pins));
        Assert.Equal(EdgeState.Complete, me.State(LibraryEdgeKind.Liked));
    }

    [Fact]
    public void The_rootlist_holds_seven_playlists_behind_a_folder_inside_a_folder()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        var me = Wavee.User.Me;

        var rows = me.Rootlist;
        Assert.Equal(EdgeState.Complete, me.RootlistState);

        int items = 0, starts = 0, ends = 0, maxDepth = 0;
        foreach (var row in rows)
        {
            if (row.Kind == (byte)RootlistKind.Item) items++;
            else if (row.Kind == (byte)RootlistKind.FolderStart) starts++;
            else if (row.Kind == (byte)RootlistKind.FolderEnd) ends++;
            if (row.Depth > maxDepth) maxDepth = row.Depth;
        }

        Assert.Equal(7, items);     // "7 named playlists" (ch 31 §8 assertion 5)
        Assert.Equal(2, starts);
        Assert.Equal(2, ends);
        Assert.True(maxDepth >= 2, "a folder inside a folder must reach depth 2");
    }

    [Fact]
    public void Sixteen_covers_are_interned_and_wrap_around()
    {
        // ch 31 §8 assertion 10 (the count only — the pixel dimensions are a fixture-asset check, not a unit fact).
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        string cover0 = Entities.Strings.Resolve(Entities.Track(EntityUri.Parse("spotify:track:tr0")).ImageId);
        string cover16 = Entities.Strings.Resolve(Entities.Track(EntityUri.Parse("spotify:track:tr16")).ImageId);
        Assert.Equal(cover0, cover16);   // index 16 wraps back to cover 0's slot (16 distinct covers, not 166)
        Assert.EndsWith("cover00.jpg", cover0);
    }

    [Fact]
    public void Every_liked_track_resolves_to_a_row_with_a_title()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        var slots = Wavee.User.Me.LikedTrackSlots;
        Assert.Equal(166, slots.Length);
        foreach (int slot in slots)
        {
            var track = new Track(slot);
            Assert.True(track.IsValid);
            Assert.False(string.IsNullOrEmpty(track.Title));
        }
    }

    [Fact]
    public void Album_tracks_and_playlist_tracks_are_not_empty()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        var album = Entities.Album(EntityUri.Parse("spotify:album:al2"));
        var playlist = Entities.Playlist(EntityUri.Parse("spotify:playlist:pl0"));

        Assert.True(Entities.Current.Edges.AlbumTracks.Count(album.Slot) > 0);
        Assert.True(Entities.Current.Edges.PlaylistTracks.Count(playlist.Slot) > 0);
    }

    // ── the customizer's sample: pure, index-addressable, and answers even without a seed (ch 31 §7.2 rules C/D) ────

    [Fact]
    public void Sample_answers_the_same_index_the_same_way_on_a_scope_that_never_seeded()
    {
        Entities.Boot(CatalogScope.Fake());   // deliberately NOT calling SeedFake — rule D's whole point

        var first = Entities.Sample(EntityKind.Playlist, 7);
        var second = Entities.Sample(EntityKind.Playlist, 7);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(first.Image, second.Image);
        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public void Sample_is_stable_across_the_customizers_nine_hand_indexed_slots()
    {
        Entities.Boot(CatalogScope.Fake());
        foreach (int i in new[] { 1, 2, 5, 7, 8, 10, 12, 14 })
        {
            var a = Entities.Sample(EntityKind.Playlist, i);
            var b = Entities.Sample(EntityKind.Playlist, i);
            Assert.Equal(a, b);
        }
        var artist = Entities.Sample(EntityKind.Artist, 3);
        Assert.Equal(artist, Entities.Sample(EntityKind.Artist, 3));
    }

    [Fact]
    public void SampleShortcutCount_matches_the_seeded_library_counts()
    {
        Assert.Equal(166, Entities.SampleShortcutCount("liked"));
        Assert.Equal(13, Entities.SampleShortcutCount("albums"));
        Assert.Equal(12, Entities.SampleShortcutCount("artists"));
        Assert.Equal(8, Entities.SampleShortcutCount("podcasts"));
        Assert.Null(Entities.SampleShortcutCount("recents"));
    }

    // ── the podcast visits (Entities.Fake.Podcast.cs; podcast plan §9): the three first screens the show reader's head
    //    switches on are told apart by the COLUMNS alone — played / in progress / unplayed and the last-play stamp ──────

    const long Day = 86_400;

    static Show ShowOf(int i) => Entities.Show(EntityUri.Parse("spotify:show:sh" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>(played, in progress, unplayed) over a show's resident episodes, through <c>Episode.Rules</c>.</summary>
    static (int Played, int InProgress, int Unplayed) Tally(Show show)
    {
        int played = 0, progress = 0, unplayed = 0;
        foreach (int slot in show.EpisodeSlots)
        {
            float pct = Episode.Rules.PctOf(new Episode(slot));
            if (Episode.Rules.Played(pct)) played++;
            else if (Episode.Rules.InProgress(pct)) progress++;
            else unplayed++;
        }
        return (played, progress, unplayed);
    }

    /// <summary>The show's newest resume-point stamp — "your last visit" — 0 when nothing was ever played.</summary>
    static int LastPlay(Show show)
    {
        int last = 0;
        foreach (int slot in show.EpisodeSlots) last = Math.Max(last, new Episode(slot).PlayedAt);
        return last;
    }

    static int PublishedAfter(Show show, int at)
    {
        int n = 0;
        foreach (int slot in show.EpisodeSlots) if (new Episode(slot).PublishedAt > at) n++;
        return n;
    }

    [Fact]
    public void The_three_podcast_visits_are_told_apart_by_the_columns()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);

        Assert.Equal((8, 1, 5), Tally(ShowOf(0)));      // returning: episodes 1-8 played, 9 in progress, 10-14 to go
        Assert.Equal((0, 0, 9), Tally(ShowOf(1)));      // new: no progress at all
        Assert.Equal((10, 0, 0), Tally(ShowOf(2)));     // caught up: nothing unplayed or in progress

        Assert.Equal(0, LastPlay(ShowOf(1)));
        Assert.True(LastPlay(ShowOf(0)) > 0 && LastPlay(ShowOf(2)) > 0);
        Assert.Equal(0, PublishedAfter(ShowOf(2), LastPlay(ShowOf(2))));   // caught up: nothing new since the last play
    }

    [Fact]
    public void Sh0_is_a_serial_numbered_fourteen_to_one_resuming_nine_with_two_new_since_the_last_play()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        var sh0 = ShowOf(0);

        Assert.True(sh0.Knows(ShowFields.Facts | ShowFields.Rating));
        Assert.Equal(ConsumptionOrder.Sequential, sh0.Order);
        Assert.Equal(480, sh0.RatingX100);
        Assert.Equal(12_431, sh0.RatingCount);
        Assert.NotEqual(0u, sh0.Tone);

        var slots = sh0.EpisodeSlots;
        Assert.Equal(14, slots.Length);
        for (int i = 0; i < slots.Length; i++) Assert.Equal(14 - i, new Episode(slots[i]).Number);

        var nine = new Episode(slots[14 - 9]);
        Assert.Equal(0.46f, Episode.Rules.PctOf(nine), 3);
        Assert.Equal((int)(Now0 - 9 * Day), nine.PlayedAt);
        Assert.Equal((int)(Now0 - 9 * Day), LastPlay(sh0));
        Assert.Equal(2, PublishedAfter(sh0, LastPlay(sh0)));             // 13 and 14: "new since you were here"

        // The trailer is its own episode row, kind Trailer, OUTSIDE the fourteen.
        var trailer = Entities.Episode(EntityUri.Parse(Entities.Strings.Resolve(sh0.TrailerId)));
        Assert.Equal(EpisodeKind.Trailer, trailer.Kind);
        Assert.True(trailer.Knows(EpisodeFields.All));
        Assert.Equal(sh0.Slot, trailer.ShowSlot);
        Assert.True(sh0.EpisodeSlots.IndexOf(trailer.Slot) < 0);
    }

    [Fact]
    public void Sh1_is_new_and_episodic_with_explicit_on_the_even_numbers()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        var sh1 = ShowOf(1);

        Assert.Equal(ConsumptionOrder.Episodic, sh1.Order);
        Assert.Equal(460, sh1.RatingX100);
        Assert.True((sh1.Flags & ShowFlags.Explicit) != 0);
        foreach (int slot in sh1.EpisodeSlots)
        {
            var ep = new Episode(slot);
            Assert.Equal(ep.Number % 2 == 0, (ep.Flags & EpisodeFlags.Explicit) != 0);
            Assert.Equal(0, ep.ProgressMs);
        }
    }

    [Fact]
    public void Sh2_is_a_caught_up_video_exclusive_with_one_paywalled_preview()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        var sh2 = ShowOf(2);

        Assert.Equal(ShowFlags.Video | ShowFlags.Exclusive, sh2.Flags & (ShowFlags.Video | ShowFlags.Exclusive));
        Assert.Equal(5, sh2.MyRating);
        int paywalled = 0;
        foreach (int slot in sh2.EpisodeSlots)
        {
            var ep = new Episode(slot);
            Assert.True((ep.Flags & EpisodeFlags.Video) != 0);
            Assert.True(Episode.Rules.Completed(ep.ProgressMs, ep.DurationMs));
            if ((ep.Flags & (EpisodeFlags.Paywalled | EpisodeFlags.PreviewOnly)) == (EpisodeFlags.Paywalled | EpisodeFlags.PreviewOnly)) paywalled++;
        }
        Assert.Equal(1, paywalled);
    }

    [Fact]
    public void The_other_shows_answer_facts_and_rating_empty_and_the_stamps_move_with_now0()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
        for (int s = 3; s <= 8; s++)
        {
            var show = ShowOf(s);
            Assert.True(show.Knows(ShowFields.Facts | ShowFields.Rating));   // answered, so offline never asks
            Assert.Equal(0, show.RatingX100);
            Assert.True(show.TrailerId.IsEmpty);
            Assert.Equal(ShowFlags.None, show.Flags);
        }
        int before = LastPlay(ShowOf(0));

        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0 + Day);
        Assert.Equal((int)Day, LastPlay(ShowOf(0)) - before);             // a dated column: shifts with now0, exactly
    }
}
