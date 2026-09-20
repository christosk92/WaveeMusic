// ── Wavee.Tests/ArtistReaderShapeTests.cs — the artist reader's block shape (Entities/Artist.Reader.cs §1) ──────────
//
// `Artist.ReaderShape` is the whole of what the library's artist reader decides before it renders: which blocks exist,
// in which order, how tall each one is, and the one string the list remounts on. The plan (library-rework-
// implementation.md §5.6, §6 rules 4-7) makes all four pure, so all four are facts here rather than something a screen-
// shot has to prove.
//
// TWO HALVES, two fixtures. `ExtentOf` / `OrderKey` / `SortOf` are arithmetic over values — no scope needed. `Build`
// reads ALBUM ROWS and the `AlbumTracks` edge (a block's row count and its failure bit), so those facts boot a real
// offline graph and stage albums through `Staging` + `Commit` exactly as `AlbumDrawerVerdictOfTests` does: the ordering
// rule is pinned against the real column reads (`Knows(Year)`, `Title`, the uri form), never against a projection that
// could disagree with them.
//
// THE LIBRARY GROUP IS PASSED IN, NOT LOOKED UP (the 2026-09-18 correction). "In your library" is now two groups —
// saved albums and albums you only hold LIKED tracks of — and `Build` takes them as three parallel arrays (slot ·
// `likedOnly` · that block's liked-track count) rather than reading the account's relations itself. So every liked-only
// fact below is a pure one: no `Edges.Liked`, no `MeSlot`, just the rows `User.LibraryReleasesOf` would have handed it.

using Wavee;
using Xunit;
using ReaderBlock = Wavee.Artist.ReaderBlock;
using ReaderSort = Wavee.Artist.ReaderSort;
using Shape = Wavee.Artist.ReaderShape;

namespace Wavee.Tests;

public class ArtistReaderShapeTests
{
    // ── 1. ExtentOf: the height the list lays out before a block renders ────────────────────────────────────────────

    // pad 16 + max(cover + 8 + 16, head 44 + rows * 36 + 8) + pad 8 + divider 1.

    [Fact]
    public void A_block_with_no_rows_is_as_tall_as_its_cover_and_the_cover_size_decides()
    {
        var b = new ReaderBlock(AlbumSlot: 7, Saved: true, Rows: 0, Failed: false, LikedOnly: false);

        // wide: 16 + max(120 + 24, 44 + 0 + 8) + 8 + 1 = 16 + 144 + 9
        Assert.Equal(169f, Shape.ExtentOf(b, narrow: false));
        // narrow: the 88 cover still wins over a 52-px body — 16 + 112 + 9
        Assert.Equal(137f, Shape.ExtentOf(b, narrow: true));
    }

    [Fact]
    public void The_cover_size_stops_mattering_once_the_rows_outgrow_it()
    {
        // 2 rows: body 44 + 72 + 8 = 124 — still under the wide cover (144), already over the narrow one (112).
        var two = new ReaderBlock(3, false, 2, false, false);
        Assert.Equal(169f, Shape.ExtentOf(two, narrow: false));
        Assert.Equal(149f, Shape.ExtentOf(two, narrow: true));

        // 4 rows: body 44 + 144 + 8 = 196 — the body wins at both widths, so the two answers agree.
        var four = new ReaderBlock(3, false, 4, false, false);
        Assert.Equal(221f, Shape.ExtentOf(four, narrow: false));
        Assert.Equal(221f, Shape.ExtentOf(four, narrow: true));

        // 12 rows: body 44 + 432 + 8 = 484.
        var twelve = new ReaderBlock(3, false, 12, false, false);
        Assert.Equal(509f, Shape.ExtentOf(twelve, narrow: false));
        Assert.Equal(509f, Shape.ExtentOf(twelve, narrow: true));
    }

    [Fact]
    public void The_extent_ignores_the_failure_bit_the_retry_note_lives_inside_the_same_box()
    {
        var ok = new ReaderBlock(5, false, 6, Failed: false, LikedOnly: false);
        var failed = ok with { Failed = true };
        Assert.Equal(Shape.ExtentOf(ok, narrow: false), Shape.ExtentOf(failed, narrow: false));
    }

    [Fact]
    public void A_liked_only_block_is_laid_out_off_its_liked_rows_like_any_other_block()
    {
        // The extent rule knows nothing about the two groups: a liked-only block with 2 liked rows occupies exactly what
        // a saved block with 2 rows occupies, which is what keeps the list's table honest when a block changes group.
        var liked = new ReaderBlock(9, Saved: true, Rows: 2, Failed: false, LikedOnly: true);
        Assert.Equal(Shape.ExtentOf(new ReaderBlock(9, true, 2, false, false), narrow: false),
                     Shape.ExtentOf(liked, narrow: false));
    }

    // ── 2. OrderKey: the identity of the SEQUENCE ──────────────────────────────────────────────────────────────────

    [Fact]
    public void OrderKey_is_stable_and_count_prefixed()
    {
        ReaderBlock[] blocks = [new(11, true, 3, false, false), new(12, true, 9, false, false), new(13, false, 4, false, false)];

        string key = Shape.OrderKey(blocks);
        Assert.Equal(key, Shape.OrderKey(blocks));                 // same sequence, same key — twice
        Assert.Matches("^3:[0-9a-f]{16}$", key);                   // count first, so two lengths can never collide

        Assert.StartsWith("0:", Shape.OrderKey(ReadOnlySpan<ReaderBlock>.Empty));
        // ...and the pre-mount sentinel is NOT a key any real shape can produce, so the first compute always counts.
        Assert.NotEqual(Artist.ReaderShapeKey.Empty.OrderKey, Shape.OrderKey(ReadOnlySpan<ReaderBlock>.Empty));
    }

    [Fact]
    public void OrderKey_keys_on_the_slots_and_the_two_group_bits_and_on_nothing_else()
    {
        ReaderBlock[] blocks = [new(11, true, 3, false, false), new(12, false, 4, false, false)];
        string key = Shape.OrderKey(blocks);

        // A block whose rows landed, or whose tracks edge failed, re-renders ITSELF — it must never remount the list.
        ReaderBlock[] grown = [new(11, true, 18, true, false), new(12, false, 12, false, false)];
        Assert.Equal(key, Shape.OrderKey(grown));

        // The order is part of the identity...
        Assert.NotEqual(key, Shape.OrderKey([new(12, false, 4, false, false), new(11, true, 3, false, false)]));
        // ...and so is "saved", because it decides which group a block sits in and what the block paints.
        Assert.NotEqual(key, Shape.OrderKey([new(11, false, 3, false, false), new(12, false, 4, false, false)]));
        // A different slot is a different sequence.
        Assert.NotEqual(key, Shape.OrderKey([new(11, true, 3, false, false), new(99, false, 4, false, false)]));
    }

    [Fact]
    public void OrderKey_hashes_LikedOnly_because_it_decides_what_the_block_paints()
    {
        // Same slot, same "in your library" bit, same row count — but one lists the whole record and the other lists two
        // liked tracks. That is a different block, not a grown one, so the list must remount rather than re-diff.
        string saved = Shape.OrderKey([new(11, true, 2, false, LikedOnly: false)]);
        string likedOnly = Shape.OrderKey([new(11, true, 2, false, LikedOnly: true)]);
        Assert.NotEqual(saved, likedOnly);

        // ...and the bit is hashed per block, not folded into one flag for the sequence.
        Assert.NotEqual(Shape.OrderKey([new(11, true, 2, false, true), new(12, true, 2, false, false)]),
                        Shape.OrderKey([new(11, true, 2, false, false), new(12, true, 2, false, true)]));
    }

    // ── 2b. ReaderShapeKey: what a re-compute has to be able to say changed ────────────────────────────────────────

    [Fact]
    public void The_shape_key_carries_the_release_total_and_the_song_count_so_a_landing_edge_is_a_change()
    {
        // Both numbers ride the KEY rather than being read live by the rail and the band: the rail's element is cached
        // across artists and a thunk that reads an edge table subscribes to nothing, which is exactly how "all releases
        // · N" froze at the first artist's number. On the key, a new total is a new key — the memo re-fires.
        var six = new Artist.ReaderShapeKey(3, 1, "3:abc", Scope: 1, Sort: 0, TotalReleases: 6, Songs: 35);
        Assert.NotEqual(six, six with { TotalReleases = 9 });
        Assert.NotEqual(six, six with { Songs = 36 });
        Assert.Equal(six, six with { });

        // The pre-mount sentinel still answers "nothing known yet" for both.
        Assert.Equal(0, Artist.ReaderShapeKey.Empty.TotalReleases);
        Assert.Equal(0, Artist.ReaderShapeKey.Empty.Songs);
    }

    // ── 3. SortOf: the persisted code ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_persisted_discography_codes_clamp_to_newest()
    {
        // The reader reuses `LibraryAlbumSort("artists")`, whose old discography values ran 0..4 (§4).
        Assert.Equal(ReaderSort.Newest, Shape.SortOf(0));
        Assert.Equal(ReaderSort.Oldest, Shape.SortOf(1));
        Assert.Equal(ReaderSort.Alphabetical, Shape.SortOf(2));
        Assert.Equal(ReaderSort.Newest, Shape.SortOf(3));
        Assert.Equal(ReaderSort.Newest, Shape.SortOf(4));
        Assert.Equal(ReaderSort.Newest, Shape.SortOf(-1));
    }
}

/// <summary>`Build` against a real offline graph: staged albums, saved slots, the three facet edges.</summary>
[Collection(EntitiesCollection.Name)]
public class ArtistReaderShapeBuildTests
{
    const string ArtistUri = "spotify:artist:reader";

    // ── the fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    static Artist Reader()
    {
        var s = Staging.Rent();
        ref var row = ref s.Artists.RowFor(new StagedId(s.Text(ArtistUri)), Authority.Full, (uint)ArtistFields.Identity);
        row.Name = s.Text("Reader");
        TestScope.CommitAndPublish(s);
        return Entities.Artist(EntityUri.Parse(ArtistUri));
    }

    /// <summary>One album row. <paramref name="year"/> 0 withholds the <c>Year</c> BIT (not just the value), which is
    /// what "unknown year" means to the sort; <paramref name="trackCount"/> 0 does the same for the count.</summary>
    static int Staged(string uri, string title, int year = 0, int trackCount = 0)
    {
        var s = Staging.Rent();
        var known = AlbumFields.Title | AlbumFields.TrackCount | (year > 0 ? AlbumFields.Year : AlbumFields.None);
        ref var row = ref s.Albums.RowFor(new StagedId(s.Text(uri)), Authority.Full, (uint)known);
        row.Title = s.Text(title);
        row.Year = (ushort)year;
        row.TrackCount = trackCount;
        TestScope.CommitAndPublish(s);
        return Entities.Album(EntityUri.Parse(uri)).Slot;
    }

    static void Facets(Artist a, int[]? albums = null, int[]? singles = null, int[]? compilations = null)
    {
        var e = Entities.Current.Edges;
        e.ArtistAlbums.ReplaceRun(a.Slot, albums ?? [], default);
        e.ArtistSingles.ReplaceRun(a.Slot, singles ?? [], default);
        e.ArtistCompilations.ReplaceRun(a.Slot, compilations ?? [], default);
    }

    /// <summary>The page's own call, with the pooled buffers `Capacity` sizes (a test allocates them per fact).
    /// <paramref name="likedOnly"/> / <paramref name="likedRows"/> are the parallel library rows
    /// <c>User.LibraryReleasesOf</c> would have filled; omitted, every library release is a SAVED album.</summary>
    static ReaderBlock[] Build(Artist a, int scope, ReaderSort sort, int[] library,
                               out int libraryCount, bool[]? likedOnly = null, int[]? likedRows = null)
    {
        var e = Entities.Current.Edges;
        var albums = e.ArtistAlbums.Targets(a.Slot);
        var singles = e.ArtistSingles.Targets(a.Slot);
        var compilations = e.ArtistCompilations.Targets(a.Slot);
        int cap = Shape.Capacity(library.Length, albums.Length, singles.Length, compilations.Length);
        var scratch = new int[cap];
        var perm = new int[cap];
        var into = new ReaderBlock[cap];
        int n = Shape.Build(a.Slot, scope, sort, library, likedOnly ?? new bool[library.Length],
                            likedRows ?? new int[library.Length], library.Length,
                            albums, singles, compilations, scratch, perm, into, out libraryCount);
        return into[..n];
    }

    static string[] Titles(ReaderBlock[] blocks)
    {
        var titles = new string[blocks.Length];
        for (int i = 0; i < blocks.Length; i++) titles[i] = new Album(blocks[i].AlbumSlot).Title;
        return titles;
    }

    // ── 4. the two groups ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scope_0_is_the_library_blocks_only()
    {
        TestScope.Fresh();
        var artist = Reader();
        int multitude = Staged("spotify:album:multitude", "Multitude", 2022, 12);
        int racine = Staged("spotify:album:racine", "racine carrée", 2013, 16);
        int live = Staged("spotify:album:live", "Racine Carrée Live", 2015, 20);
        Facets(artist, albums: [multitude, racine, live]);

        var blocks = Build(artist, scope: 0, ReaderSort.Newest, [multitude, racine], out int library);

        Assert.Equal(2, blocks.Length);
        Assert.Equal(2, library);
        Assert.Equal(["Multitude", "racine carrée"], Titles(blocks));
        Assert.All(blocks, b => Assert.True(b.Saved));             // scope 0 never shows a catalogue release
    }

    [Fact]
    public void Scope_1_appends_the_union_minus_saved_deduped_across_the_three_facets()
    {
        TestScope.Fresh();
        var artist = Reader();
        int saved = Staged("spotify:album:saved", "Saved One", 2022, 10);
        int b = Staged("spotify:album:b", "Beta", 2021, 3);
        int c = Staged("spotify:album:c", "Gamma", 2020, 2);
        // `saved` is listed by a facet too (it is the artist's own release); `b` and `c` each appear in two facets.
        Facets(artist, albums: [saved, b], singles: [b, c], compilations: [c, saved]);

        var blocks = Build(artist, scope: 1, ReaderSort.Newest, [saved], out int library);

        Assert.Equal(1, library);
        Assert.Equal(3, blocks.Length);                            // the saved block once, then B and C once each
        Assert.Equal(["Saved One", "Beta", "Gamma"], Titles(blocks));
        Assert.True(blocks[0].Saved);
        Assert.False(blocks[1].Saved);
        Assert.False(blocks[2].Saved);
    }

    [Fact]
    public void Library_blocks_lead_whatever_the_sort_would_say()
    {
        TestScope.Fresh();
        var artist = Reader();
        int oldSaved = Staged("spotify:album:old-saved", "Zulu", 1994, 9);      // oldest AND last alphabetically
        int newCat = Staged("spotify:album:new-cat", "Alpha", 2026, 5);
        Facets(artist, albums: [newCat]);

        foreach (var sort in new[] { ReaderSort.Newest, ReaderSort.Oldest, ReaderSort.Alphabetical })
        {
            var blocks = Build(artist, scope: 1, sort, [oldSaved], out int library);
            Assert.Equal(1, library);
            Assert.Equal(["Zulu", "Alpha"], Titles(blocks));        // "in your library" is the subject, not a sort key
        }
    }

    // ── 4b. the LIKED-ONLY half of "in your library" ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_liked_only_release_is_a_library_block_and_sorts_inside_the_library_group()
    {
        TestScope.Fresh();
        var artist = Reader();
        int saved2013 = Staged("spotify:album:saved-2013", "Saved 2013", 2013, 10);
        int liked2022 = Staged("spotify:album:liked-2022", "Liked 2022", 2022, 14);   // not saved: two liked tracks on it
        int catalogue = Staged("spotify:album:cat-2026", "Catalogue 2026", 2026, 5);
        Facets(artist, albums: [catalogue]);

        // The two groups sort TOGETHER — the liked-only 2022 release is NEWER, so under "newest" it leads the saved one,
        // and both still precede the catalogue release whatever its year.
        var blocks = Build(artist, scope: 1, ReaderSort.Newest, [saved2013, liked2022], out int library,
                           likedOnly: [false, true], likedRows: [0, 2]);

        Assert.Equal(2, library);
        Assert.Equal(["Liked 2022", "Saved 2013", "Catalogue 2026"], Titles(blocks));
        Assert.True(blocks[0].LikedOnly);
        Assert.True(blocks[0].Saved);                              // "Saved" is the LIBRARY bit — both groups carry it
        Assert.False(blocks[1].LikedOnly);
        Assert.False(blocks[2].Saved);

        // ...and "oldest" flips them the same way, which is the point of one group rather than two.
        var oldest = Build(artist, scope: 1, ReaderSort.Oldest, [saved2013, liked2022], out _,
                           likedOnly: [false, true], likedRows: [0, 2]);
        Assert.Equal(["Saved 2013", "Liked 2022", "Catalogue 2026"], Titles(oldest));
    }

    [Fact]
    public void The_rows_of_a_liked_only_block_are_its_liked_tracks_not_the_albums_own_count()
    {
        TestScope.Fresh();
        var artist = Reader();
        // A 14-track record you did not save, of which you liked two. The block lists TWO rows: the other twelve are not
        // yours, and the counted skeleton must not reserve height for them.
        int album = Staged("spotify:album:feature", "Feature", 2024, trackCount: 14);
        Facets(artist);

        var blocks = Build(artist, scope: 0, ReaderSort.Newest, [album], out int library,
                           likedOnly: [true], likedRows: [2]);

        Assert.Equal(1, library);
        Assert.Equal(2, blocks[0].Rows);
        Assert.True(blocks[0].LikedOnly);
    }

    [Fact]
    public void A_liked_only_block_never_carries_the_albums_tracks_edge_failure()
    {
        TestScope.Fresh();
        var artist = Reader();
        int album = Staged("spotify:album:broken-feature", "Broken Feature", 2024, trackCount: 9);
        Entities.Current.Edges.AlbumTracks.MarkFailed(album, 0, 503);
        Facets(artist);

        // Nothing demands that edge for a liked-only block and nothing reads it, so its failure is not this block's
        // news — a Retry strip here would offer to re-fetch a list the block would not have shown.
        var blocks = Build(artist, scope: 0, ReaderSort.Newest, [album], out _, likedOnly: [true], likedRows: [3]);

        Assert.False(blocks[0].Failed);
        Assert.Equal(3, blocks[0].Rows);
    }

    [Fact]
    public void A_liked_only_release_that_a_facet_also_lists_appears_once_as_the_library_block()
    {
        TestScope.Fresh();
        var artist = Reader();
        int liked = Staged("spotify:album:liked-and-listed", "Both", 2021, 11);
        int other = Staged("spotify:album:other", "Other", 2020, 4);
        Facets(artist, albums: [liked, other], singles: [liked]);      // the catalogue lists it, twice even

        var blocks = Build(artist, scope: 1, ReaderSort.Newest, [liked], out int library,
                           likedOnly: [true], likedRows: [1]);

        Assert.Equal(1, library);
        Assert.Equal(2, blocks.Length);                                // "Both" once, then "Other"
        Assert.Equal(["Both", "Other"], Titles(blocks));
        Assert.True(blocks[0].LikedOnly);                              // ...and as the LIBRARY block, listing 1 row
        Assert.Equal(1, blocks[0].Rows);
        Assert.False(blocks[1].Saved);
    }

    [Fact]
    public void An_invalid_library_slot_compacts_its_liked_bits_with_it()
    {
        TestScope.Fresh();
        var artist = Reader();
        int real = Staged("spotify:album:kept", "Kept", 2022, 8);
        Facets(artist);

        // The dropped slot's row must not leave the survivor wearing somebody else's bits — the compaction moves all
        // three arrays in step (§1 deviation 4).
        var blocks = Build(artist, scope: 0, ReaderSort.Newest, [Table.None, real], out int library,
                           likedOnly: [false, true], likedRows: [0, 4]);

        Assert.Single(blocks);
        Assert.Equal(1, library);
        Assert.True(blocks[0].LikedOnly);
        Assert.Equal(4, blocks[0].Rows);
    }

    // ── 5. the three sorts ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Newest_is_year_descending_with_the_unknown_years_sinking()
    {
        TestScope.Fresh();
        var artist = Reader();
        int dateless = Staged("spotify:album:dateless", "Dateless");            // no Year bit at all
        int y2013 = Staged("spotify:album:y2013", "Thirteen", 2013, 4);
        int y2022 = Staged("spotify:album:y2022", "Twenty-two", 2022, 4);
        int y2015 = Staged("spotify:album:y2015", "Fifteen", 2015, 4);
        Facets(artist, albums: [dateless, y2013, y2022, y2015]);

        var blocks = Build(artist, scope: 1, ReaderSort.Newest, [], out int library);

        Assert.Equal(0, library);
        Assert.Equal(["Twenty-two", "Fifteen", "Thirteen", "Dateless"], Titles(blocks));
    }

    [Fact]
    public void Oldest_is_year_ascending_and_the_unknown_years_still_sink()
    {
        TestScope.Fresh();
        var artist = Reader();
        int dateless = Staged("spotify:album:dateless", "Dateless");
        int y2013 = Staged("spotify:album:y2013", "Thirteen", 2013, 4);
        int y2022 = Staged("spotify:album:y2022", "Twenty-two", 2022, 4);
        int y2015 = Staged("spotify:album:y2015", "Fifteen", 2015, 4);
        Facets(artist, albums: [dateless, y2013, y2022, y2015]);

        var blocks = Build(artist, scope: 1, ReaderSort.Oldest, [], out _);

        // A release nobody has dated is not "the oldest" — it sinks in BOTH directions, so it cannot jump to the top
        // and then move when the year lands.
        Assert.Equal(["Thirteen", "Fifteen", "Twenty-two", "Dateless"], Titles(blocks));
    }

    [Fact]
    public void Alphabetical_ignores_case_and_ignores_the_year_entirely()
    {
        TestScope.Fresh();
        var artist = Reader();
        int cherry = Staged("spotify:album:cherry", "cherry", 1999, 4);
        int apple = Staged("spotify:album:apple", "apple", 2026, 4);
        int banana = Staged("spotify:album:banana", "Banana", 2010, 4);
        Facets(artist, albums: [cherry, apple, banana]);

        var blocks = Build(artist, scope: 1, ReaderSort.Alphabetical, [], out _);

        Assert.Equal(["apple", "Banana", "cherry"], Titles(blocks));
    }

    [Fact]
    public void A_tie_breaks_by_title_then_by_uri_so_the_order_is_total()
    {
        TestScope.Fresh();
        var artist = Reader();
        // Same year: the title breaks it (and does so case-insensitively).
        int zed = Staged("spotify:album:tie-z", "zed", 2020, 4);
        int abel = Staged("spotify:album:tie-a", "Abel", 2020, 4);
        // Same year AND the same title: only the uri is left. Nothing can reach the source-index tie-break through a
        // built shape (two blocks can never be the same slot — the union dedups), but it is what keeps the unstable
        // sort's answer reproducible, and therefore `OrderKey` stable.
        int twinB = Staged("spotify:album:twin-b", "Twin", 2020, 4);
        int twinA = Staged("spotify:album:twin-a", "Twin", 2020, 4);
        Facets(artist, albums: [zed, abel, twinB, twinA]);

        var blocks = Build(artist, scope: 1, ReaderSort.Newest, [], out _);

        Assert.Equal(["Abel", "Twin", "Twin", "zed"], Titles(blocks));
        Assert.Equal("spotify:album:twin-a", new Album(blocks[1].AlbumSlot).Uri.Text);
        Assert.Equal("spotify:album:twin-b", new Album(blocks[2].AlbumSlot).Uri.Text);

        // ...and the same inputs give the same sequence, every time.
        Assert.Equal(Shape.OrderKey(blocks), Shape.OrderKey(Build(artist, 1, ReaderSort.Newest, [], out _)));
    }

    // ── 6. the rows a block lays out, and its failure bit ──────────────────────────────────────────────────────────

    [Fact]
    public void Rows_are_the_advertised_count_then_the_listed_edge_then_four()
    {
        TestScope.Fresh();
        var artist = Reader();
        int counted = Staged("spotify:album:counted", "Counted", 2022, trackCount: 7);
        int listed = Staged("spotify:album:listed", "Listed", 2021);            // count withheld, tracks landed
        int neither = Staged("spotify:album:neither", "Neither", 2020);         // nothing answered yet
        var tracks = Entities.Current.Tracks;
        Entities.Current.Edges.AlbumTracks.ReplaceRun(listed,
            [tracks.Slot("spotify:track:r1".AsSpan()), tracks.Slot("spotify:track:r2".AsSpan())], default);
        Facets(artist, albums: [counted, listed, neither]);

        var blocks = Build(artist, scope: 1, ReaderSort.Newest, [], out _);

        Assert.Equal(7, blocks[0].Rows);                                        // TrackCount wins
        Assert.Equal(2, blocks[1].Rows);                                        // what actually landed
        Assert.Equal(Shape.ShimmerRows, blocks[2].Rows);                        // the counted skeleton's last resort
        Assert.Equal(4, blocks[2].Rows);
        Assert.All(blocks, b => Assert.False(b.Failed));
    }

    [Fact]
    public void A_failed_tracks_edge_is_carried_on_the_block_not_left_as_an_empty_one()
    {
        TestScope.Fresh();
        var artist = Reader();
        int broken = Staged("spotify:album:broken", "Broken", 2022, trackCount: 5);
        Entities.Current.Edges.AlbumTracks.MarkFailed(broken, 0, 503);
        Facets(artist, albums: [broken]);

        var blocks = Build(artist, scope: 1, ReaderSort.Newest, [], out _);

        Assert.True(blocks[0].Failed);                                          // the head + a Retry note (§6 rule 5)
        Assert.Equal(5, blocks[0].Rows);                                        // still counted, so the box keeps its size
    }

    // ── 7. the empty edges ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_invalid_artist_has_no_shape_at_all()
    {
        TestScope.Fresh();
        var scratch = new int[4];
        int n = Shape.Build(Table.None, scope: 1, ReaderSort.Newest, [], [], [], 0, [], [], [],
                            scratch, new int[4], new ReaderBlock[4], out int library);
        Assert.Equal(0, n);
        Assert.Equal(0, library);
    }

    [Fact]
    public void An_artist_with_nothing_saved_and_nothing_listed_is_an_empty_shape()
    {
        TestScope.Fresh();
        var artist = Reader();
        Facets(artist);

        Assert.Empty(Build(artist, scope: 0, ReaderSort.Newest, [], out int lib0));
        Assert.Equal(0, lib0);
        Assert.Empty(Build(artist, scope: 1, ReaderSort.Alphabetical, [], out int lib1));
        Assert.Equal(0, lib1);
    }

    [Fact]
    public void An_invalid_saved_slot_is_dropped_rather_than_blocked_on()
    {
        TestScope.Fresh();
        var artist = Reader();
        int real = Staged("spotify:album:real", "Real", 2022, 4);
        Facets(artist);

        var blocks = Build(artist, scope: 0, ReaderSort.Newest, [Table.None, real], out int library);

        Assert.Single(blocks);
        Assert.Equal(1, library);
        Assert.Equal(real, blocks[0].AlbumSlot);
    }
}
