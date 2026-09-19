// ── Wavee.Tests/LibraryReleasesTests.cs — "in your library" for an artist (Entities/User.cs §11) ──────────────────────
//
// Until 2026-09-18 an artist's library was `SavedAlbums ∩ AlbumArtists` and nothing else, so an account that hearts songs
// and saves no albums had an EMPTY Artists tab and every artist it did show claimed a discography it did not have. The
// rule is two groups now — the saved albums billed to the artist (whole tracklist), then the albums holding at least one
// liked track credited to it (only those tracks) — and the Artists navigator is followed ∪ billed-on-saved ∪
// credited-on-liked.
//
// Everything below runs against REAL edges in a fake scope, the way `LibraryRecencyTests` pins the counts: these helpers
// are reads a navigator row and a reader block call while they render, so what has to be true is the answer AND that
// nothing was demanded to get it. Pinned: the two groups and their order, the `likedOnly` flag, `LikedTracksOfAlbum`'s
// liked-edge order, the overlap (a saved album that also holds liked tracks is ONE block, not liked-only, and its songs
// are not counted twice), a feature credit, the short/empty-span totals, and `LibraryArtistsOf`'s followed-first
// first-seen dedup.

using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class LibraryReleasesTests
{
    public LibraryReleasesTests() => TestScope.Fresh();

    // ── the fixture: rows and edges, written straight into the tables (no decoder, no network) ──────────────────────

    static int Me(string key)
    {
        var scope = Entities.Current;
        scope.MeSlot = scope.Users.Slot(("spotify:user:" + key).AsSpan());
        return scope.MeSlot;
    }

    static int ArtistRow(string key, string name)
    {
        var t = Entities.Current.Artists;
        int slot = t.Slot(("spotify:artist:" + key).AsSpan());
        t.SetText(ref t.Name, slot, Entities.Strings.Intern(name));
        return slot;
    }

    /// <summary>An album row with a KNOWN track count and its billed artists (<c>Edges.AlbumArtists</c>).</summary>
    static int AlbumRow(string key, string title, int tracks, params int[] billed)
    {
        var t = Entities.Current.Albums;
        int slot = t.Slot(("spotify:album:" + key).AsSpan());
        t.SetText(ref t.Title, slot, Entities.Strings.Intern(title));
        if (tracks >= 0) { t.TrackCount[slot] = tracks; t.Known[slot] |= (uint)AlbumFields.TrackCount; }
        Entities.Current.Edges.AlbumArtists.ReplaceRun(slot, billed, default);
        return slot;
    }

    /// <summary>A track row on <paramref name="album"/> with its credited artists (<c>Edges.TrackArtists</c>).</summary>
    static int TrackRow(string key, string title, int album, params int[] credited)
    {
        var scope = Entities.Current;
        var t = scope.Tracks;
        int slot = t.Slot(("spotify:track:" + key).AsSpan());
        t.SetText(ref t.Title, slot, Entities.Strings.Intern(title));
        t.Album[slot] = album;
        scope.Edges.TrackArtists.ReplaceRun(slot, credited, default);
        return slot;
    }

    static void Saves(int me, params int[] albums) => Entities.Current.Edges.SavedAlbums.ReplaceRun(me, albums, default);
    static void Likes(int me, params int[] tracks) => Entities.Current.Edges.Liked.ReplaceRun(me, tracks, default);
    static void Follows(int me, params int[] artists) => Entities.Current.Edges.FollowedArtists.ReplaceRun(me, artists, default);

    /// <summary>The release list, sized exactly, as a pair of arrays — the shape a caller reads it in.</summary>
    static (int[] Albums, bool[] LikedOnly) Releases(int artist)
    {
        int total = User.LibraryReleaseCountOf(artist);
        var albums = new int[total];
        var likedOnly = new bool[total];
        Assert.Equal(total, User.LibraryReleasesOf(artist, albums, likedOnly));
        return (albums, likedOnly);
    }

    static int[] LikedTracks(int artist, int album)
    {
        var into = new int[User.LikedTracksOfAlbum(artist, album, default)];
        Assert.Equal(into.Length, User.LikedTracksOfAlbum(artist, album, into));
        return into;
    }

    static int[] LibraryArtists()
    {
        var into = new int[User.LibraryArtistsOf(default)];
        Assert.Equal(into.Length, User.LibraryArtistsOf(into));
        return into;
    }

    // ── group 1 alone: saved albums, unchanged ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SavedAlbumsOnly_AreTheWholeList_AndNoneOfThemIsLikedOnly()
    {
        int me = Me("releases-saved");
        int mj = ArtistRow("mj-saved", "Michael Jackson");
        int thriller = AlbumRow("thriller-saved", "Thriller", 9, mj);
        int bad = AlbumRow("bad-saved", "Bad", 11, mj);
        _ = AlbumRow("dangerous-saved", "Dangerous", 14, mj);          // billed to MJ but NOT saved
        Saves(me, thriller, bad);

        var (albums, likedOnly) = Releases(mj);
        Assert.Equal(new[] { thriller, bad }, albums);              // saved-edge order
        Assert.Equal(new[] { false, false }, likedOnly);
        Assert.Equal(2, User.LibraryReleaseCountOf(mj));
        Assert.Equal(2, User.LibraryAlbumCountOf(mj));              // the row reads the RELEASE count now
        Assert.Equal(20, User.LibrarySongCountOf(mj));              // 9 + 11, the whole records

        // The saved-only primitive is still the saved-only primitive.
        var saved = new int[4];
        Assert.Equal(2, User.LibraryAlbumsOf(mj, saved));
        Assert.Equal(new[] { thriller, bad }, saved[..2]);
    }

    [Fact]
    public void AnArtistWithNothingSavedAndNothingLiked_HasNoReleasesAndNoSongs()
    {
        int me = Me("releases-empty");
        int nobody = ArtistRow("nobody-empty", "Nobody");
        Saves(me, AlbumRow("other-empty", "Other", 5, ArtistRow("someone-empty", "Someone")));

        Assert.Empty(Releases(nobody).Albums);
        Assert.Equal(0, User.LibraryReleaseCountOf(nobody));
        Assert.Equal(0, User.LibrarySongCountOf(nobody));
        Assert.Equal(0, User.LibraryReleaseCountOf(Table.None));     // an invalid slot is never a read
        Assert.Equal(0, User.LibrarySongCountOf(Table.None));
    }

    // ── group 2: the liked-only album ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAlbumHoldingOnlyLikedTracks_IsAReleaseMarkedLikedOnly_AndListsOnlyThoseTracks()
    {
        // The defect in one fixture: nothing saved, two songs hearted. Before the correction this artist had no
        // releases and no songs at all.
        int me = Me("releases-liked");
        int boygenius = ArtistRow("boygenius", "boygenius");
        int record = AlbumRow("record", "the record", 12, boygenius);
        int notStrong = TrackRow("not-strong", "Not Strong Enough", record, boygenius);
        int cool = TrackRow("cool-about-it", "Cool About It", record, boygenius);
        _ = TrackRow("true-blue", "True Blue", record, boygenius);      // on the album, NOT liked
        Likes(me, notStrong, cool);

        var (albums, likedOnly) = Releases(boygenius);
        Assert.Equal(new[] { record }, albums);
        Assert.Equal(new[] { true }, likedOnly);

        // …and the block lists the LIKED tracks only, in liked-edge order (newest first), never the whole record.
        Assert.Equal(new[] { notStrong, cool }, LikedTracks(boygenius, record));
        Assert.Equal(2, User.LibrarySongCountOf(boygenius));         // the two songs, not the album's 12
    }

    [Fact]
    public void LikedOnlyAlbumsFollowTheSavedOnes_InLikedEdgeOrder_AndAppearOnce()
    {
        int me = Me("releases-order");
        int artist = ArtistRow("order-artist", "Ordered");
        int savedA = AlbumRow("order-saved-a", "Saved A", 4, artist);
        int savedB = AlbumRow("order-saved-b", "Saved B", 4, artist);
        // Deliberately created in the OPPOSITE order to the one they will be liked in, so the expectation cannot pass by
        // accident on slot order.
        int older = AlbumRow("order-liked-older", "Older", 10, artist);
        int newer = AlbumRow("order-liked-newer", "Newer", 10, artist);
        Saves(me, savedA, savedB);
        // Two liked tracks on `newer` — the album is listed ONCE, at its FIRST liked row (the relation is newest-first).
        Likes(me,
              TrackRow("order-t1", "One", newer, artist),
              TrackRow("order-t2", "Two", older, artist),
              TrackRow("order-t3", "Three", newer, artist));

        var (albums, likedOnly) = Releases(artist);
        Assert.Equal(new[] { savedA, savedB, newer, older }, albums);
        Assert.Equal(new[] { false, false, true, true }, likedOnly);
    }

    [Fact]
    public void ACreditAsAFeature_CountsForTheFeaturedArtistToo()
    {
        // The artist is not billed on the album and has saved nothing: the ONLY reason this release exists for them is
        // the track credit, which is exactly the case the old rule could not see.
        int me = Me("releases-feature");
        int host = ArtistRow("feature-host", "Host");
        int guest = ArtistRow("feature-guest", "Guest");
        int album = AlbumRow("feature-album", "Host's Record", 11, host);
        int duet = TrackRow("feature-duet", "Duet", album, host, guest);
        int solo = TrackRow("feature-solo", "Solo", album, host);
        Likes(me, duet, solo);

        var (guestAlbums, guestLikedOnly) = Releases(guest);
        Assert.Equal(new[] { album }, guestAlbums);
        Assert.Equal(new[] { true }, guestLikedOnly);
        Assert.Equal(new[] { duet }, LikedTracks(guest, album));      // only the track they are ON
        Assert.Equal(1, User.LibrarySongCountOf(guest));

        // …and the host sees both of their liked tracks under the same (unsaved) album.
        Assert.Equal(new[] { duet, solo }, LikedTracks(host, album));
        Assert.Equal(2, User.LibrarySongCountOf(host));
    }

    [Fact]
    public void ALikedTrackWithNoCreditedArtists_BelongsToNobody()
    {
        // `Edges.TrackArtists` is not persisted: a row whose identity has not answered credits nobody, which is the
        // honest answer — never "everybody" and never a guess.
        int me = Me("releases-uncredited");
        int artist = ArtistRow("uncredited-artist", "Uncredited");
        int album = AlbumRow("uncredited-album", "Album", 8, artist);
        Likes(me, TrackRow("uncredited-track", "Track", album));         // no TrackArtists run at all

        Assert.Empty(Releases(artist).Albums);
        Assert.Equal(0, User.LibrarySongCountOf(artist));
    }

    // ── the overlap: saved AND liked ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASavedAlbumThatAlsoHoldsLikedTracks_IsOneBlock_NotLikedOnly_AndItsSongsAreNotCountedTwice()
    {
        // You saved the record, so you get the record: the block stays group 1 (whole tracklist) and the liked rows on
        // it are already inside its track count. Counting them again is how "3 albums · 34 songs" became a lie.
        int me = Me("releases-overlap");
        int artist = ArtistRow("overlap-artist", "Overlapped");
        int saved = AlbumRow("overlap-saved", "Saved", 10, artist);
        int elsewhere = AlbumRow("overlap-elsewhere", "Elsewhere", 10, artist);
        int onSaved = TrackRow("overlap-t1", "On The Saved One", saved, artist);
        int alsoOnSaved = TrackRow("overlap-t2", "Also On It", saved, artist);
        int outside = TrackRow("overlap-t3", "Outside", elsewhere, artist);
        Saves(me, saved);
        Likes(me, onSaved, alsoOnSaved, outside);

        var (albums, likedOnly) = Releases(artist);
        Assert.Equal(new[] { saved, elsewhere }, albums);
        Assert.Equal(new[] { false, true }, likedOnly);
        Assert.Equal(2, User.LibraryReleaseCountOf(artist));

        // 10 (the saved album, whole) + 1 (the liked track that sits outside it) — the two on the saved album are in
        // its ten already.
        Assert.Equal(11, User.LibrarySongCountOf(artist));

        // The liked rows on the saved album are still readable; the PANE just does not use them (it lists all ten).
        Assert.Equal(new[] { onSaved, alsoOnSaved }, LikedTracks(artist, saved));
        Assert.Equal(new[] { outside }, LikedTracks(artist, elsewhere));
    }

    [Fact]
    public void ASavedAlbumTheArtistIsNotBilledOn_IsALikedOnlyBlock()
    {
        // A compilation you saved: the artist's presence on it is one track, not the record, so the block lists that
        // track and the songs line counts it individually.
        int me = Me("releases-compilation");
        int various = ArtistRow("compilation-various", "Various Artists");
        int guest = ArtistRow("compilation-guest", "Guest");
        int comp = AlbumRow("compilation", "Now That's What I Call Edges", 40, various);
        int theirs = TrackRow("compilation-theirs", "Theirs", comp, guest);
        Saves(me, comp);
        Likes(me, theirs);

        var (albums, likedOnly) = Releases(guest);
        Assert.Equal(new[] { comp }, albums);
        Assert.Equal(new[] { true }, likedOnly);                      // saved, but not THEIR record
        Assert.Equal(1, User.LibrarySongCountOf(guest));              // not the compilation's 40

        // The artist the compilation IS billed to gets the whole thing.
        Assert.Equal(new[] { false }, Releases(various).LikedOnly);
        Assert.Equal(40, User.LibrarySongCountOf(various));
    }

    [Fact]
    public void ALikedTrackWhoseAlbumHasNotAnswered_IsASongWithNoRelease()
    {
        // A song you liked is a song you have, even before its album is a row — but it cannot be a BLOCK, so the row
        // honestly reads "0 albums" and still says "1 song".
        int me = Me("releases-albumless");
        int artist = ArtistRow("albumless-artist", "Albumless");
        Likes(me, TrackRow("albumless-track", "Floating", Table.None, artist));

        Assert.Empty(Releases(artist).Albums);
        Assert.Equal(0, User.LibraryReleaseCountOf(artist));
        Assert.Equal(1, User.LibrarySongCountOf(artist));
    }

    [Fact]
    public void AnAlbumWhoseTrackCountHasNotAnswered_ContributesNothingToTheSongsLine()
    {
        int me = Me("releases-unknown-count");
        int artist = ArtistRow("unknown-count-artist", "Unknown");
        int known = AlbumRow("unknown-count-known", "Known", 4, artist);
        int pending = AlbumRow("unknown-count-pending", "Pending", -1, artist);   // the track count has not answered
        Saves(me, known, pending);

        Assert.Equal(2, User.LibraryReleaseCountOf(artist));
        Assert.Equal(4, User.LibrarySongCountOf(artist));
    }

    // ── the span contract: a short or empty span still answers the total ────────────────────────────────────────────

    [Fact]
    public void AShortOrEmptySpan_StillReturnsTheTotal_AndNeverWritesPastItsEnd()
    {
        int me = Me("releases-spans");
        int artist = ArtistRow("spans-artist", "Spanned");
        int savedA = AlbumRow("spans-saved-a", "A", 3, artist);
        int savedB = AlbumRow("spans-saved-b", "B", 3, artist);
        int liked = AlbumRow("spans-liked", "C", 3, artist);
        Saves(me, savedA, savedB);
        int t1 = TrackRow("spans-t1", "One", liked, artist), t2 = TrackRow("spans-t2", "Two", liked, artist);
        Likes(me, t1, t2);

        Assert.Equal(3, User.LibraryReleasesOf(artist, default, default));      // the counting call
        Assert.Equal(3, User.LibraryReleaseCountOf(artist));

        Span<int> two = stackalloc int[2];
        Span<bool> twoFlags = stackalloc bool[2];
        two[0] = two[1] = -1; twoFlags[0] = twoFlags[1] = true;
        Assert.Equal(3, User.LibraryReleasesOf(artist, two, twoFlags));
        Assert.Equal(new[] { savedA, savedB }, two.ToArray());
        Assert.Equal(new[] { false, false }, twoFlags.ToArray());

        // Mismatched spans are allowed (each takes what fits); the total is one number either way.
        Span<int> three = stackalloc int[3];
        Assert.Equal(3, User.LibraryReleasesOf(artist, three, default));
        Assert.Equal(new[] { savedA, savedB, liked }, three.ToArray());

        Assert.Equal(2, User.LikedTracksOfAlbum(artist, liked, default));
        Span<int> one = stackalloc int[1];
        one[0] = -1;
        Assert.Equal(2, User.LikedTracksOfAlbum(artist, liked, one));   // the TOTAL, through a span that holds one
        Assert.Equal(t1, one[0]);

        // Two reads of the same unchanged library agree — the shared dedup scratch is per call, not per session.
        Assert.Equal(3, User.LibraryReleaseCountOf(artist));
        Assert.Equal(3, User.LibraryReleaseCountOf(artist));
    }

    // ── the navigator's precomputed counts: one walk, the same numbers ─────────────────────────────────────────────

    [Fact]
    public void FillCounts_AnswersExactlyWhatThePerSlotReadAnswers_InOneWalk()
    {
        // `LibraryRows.FillCounts` walks the library ONCE for the whole list instead of once per row, because the count
        // reads the liked relation now. It is an optimisation, so what has to be true is that it decides nothing: every
        // cell is the per-slot read, overlaps, features, repeats, invalid slots and all.
        int me = Me("fill-counts");
        int solo = ArtistRow("fill-solo", "Solo");
        int feature = ArtistRow("fill-feature", "Feature");
        int savedOnly = ArtistRow("fill-saved-only", "Saved Only");
        int nothing = ArtistRow("fill-nothing", "Nothing");

        int soloSaved = AlbumRow("fill-solo-saved", "Saved Record", 10, solo);
        int soloLiked = AlbumRow("fill-solo-liked", "Unsaved Record", 10, solo);
        int savedOnlyAlbum = AlbumRow("fill-saved-only-album", "Theirs", 7, savedOnly);
        Saves(me, soloSaved, savedOnlyAlbum);
        Likes(me,
              TrackRow("fill-t1", "On the saved one", soloSaved, solo),        // already inside group 1
              TrackRow("fill-t2", "On the unsaved one", soloLiked, solo, feature),
              TrackRow("fill-t3", "Also on the unsaved one", soloLiked, solo), // the same album again
              TrackRow("fill-t4", "Guest spot", savedOnlyAlbum, feature));     // saved, but not the feature's record

        int[] slots = [solo, feature, savedOnly, nothing, solo, Table.None];
        var counts = new int[slots.Length];
        LibraryRows.FillCounts(EntityKind.Artist, slots, counts);

        Assert.Equal(new[] { 2, 2, 1, 0, 2, 0 }, counts);
        for (int i = 0; i < slots.Length; i++)
            Assert.Equal(User.LibraryReleaseCountOf(slots[i]), counts[i]);     // the one and only contract

        // Reusable: a second fill of a different list through the same warm scratch is not a running total.
        var again = new int[2];
        LibraryRows.FillCounts(EntityKind.Artist, [feature, nothing], again);
        Assert.Equal(new[] { 2, 0 }, again);
    }

    // ── the Artists navigator's set ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LibraryArtistsOf_IsFollowedFirst_ThenBilledOnSaved_ThenCreditedOnLiked_Deduplicated()
    {
        int me = Me("artists-set");
        int followedOnly = ArtistRow("set-followed", "Followed Only");
        int billed = ArtistRow("set-billed", "Billed");
        int coBilled = ArtistRow("set-cobilled", "Co-Billed");
        int credited = ArtistRow("set-credited", "Credited");
        int both = ArtistRow("set-both", "Followed And Billed");
        int stranger = ArtistRow("set-stranger", "Stranger");             // in no relation at all

        int savedAlbum = AlbumRow("set-saved", "Saved", 6, billed, coBilled);
        int likedAlbum = AlbumRow("set-liked", "Liked", 6, both);
        Follows(me, followedOnly, both);
        Saves(me, savedAlbum);
        Likes(me,
              TrackRow("set-t1", "One", likedAlbum, credited, both),     // `both` is already in from the follow
              TrackRow("set-t2", "Two", likedAlbum, credited));          // a repeat credit adds nothing

        // Followed in followed-edge order first, then everybody else in first-seen order.
        Assert.Equal(new[] { followedOnly, both, billed, coBilled, credited }, LibraryArtists());
        Assert.DoesNotContain(stranger, LibraryArtists());

        // The empty span counts, and a short one takes the prefix and still answers the total.
        Assert.Equal(5, User.LibraryArtistsOf(default));
        Span<int> two = stackalloc int[2];
        Assert.Equal(5, User.LibraryArtistsOf(two));
        Assert.Equal(new[] { followedOnly, both }, two.ToArray());
    }

    [Fact]
    public void LibraryArtistsOf_FindsTheArtistWhoseOnlyPresenceIsALikedSong()
    {
        // The Artists tab used to be `FollowedArtists` alone: an account that follows nobody and hearts everything saw
        // an empty page while its library was full.
        int me = Me("artists-liked-only");
        int artist = ArtistRow("liked-only-artist", "Hearted");
        Likes(me, TrackRow("liked-only-track", "Song", AlbumRow("liked-only-album", "Album", 9, artist), artist));

        Assert.Equal(new[] { artist }, LibraryArtists());
        Assert.Equal(1, User.LibraryReleaseCountOf(artist));
        Assert.Equal(1, User.LibrarySongCountOf(artist));
    }

    [Fact]
    public void LibraryArtistsOf_IsEmptyWithoutAnAccount_AndSkipsInvalidSlots()
    {
        // No `MeSlot` is an EMPTY library, never a wrong one (the boot rule) — and a Table.None target in any of the
        // three relations is a marker, not a row.
        Assert.Equal(0, User.LibraryArtistsOf(default));

        int me = Me("artists-invalid");
        int artist = ArtistRow("invalid-artist", "Real");
        Entities.Current.Edges.FollowedArtists.ReplaceRun(me, [Table.None, artist, Table.None], default);
        Assert.Equal(new[] { artist }, LibraryArtists());
    }
}
