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
}
