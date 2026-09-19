// ── Wavee.Tests/LibraryRecencyTests.cs — the library's play-recency read and the slot path of the ordering rule ────────
//
// The "RecentsRecency (library part)" of ch 15 §8: the Recents sort reads `Shell.PlayLog.Recency` (uri → last played,
// unix ms) through `LibraryRows.PlayedOf`, which probes the map with the uri formatted into a SPAN so a gid row never
// becomes a string. What is pinned here is the read (text form, gid form, the non-Dictionary fallback, never-played)
// and that the page's allocation-free path — slots in a buffer, `LibraryNavSorter<LibraryRows>` — orders and keys
// EXACTLY as the record rule 0.2.9's tests pin (`LibraryNavOrderTests`). `RecentsRecency.Stamps` itself is owner P's.
//
// Added by the 2026-09-17 library rework: the artists navigator's counts. "3 albums · 34 songs" and the `Albums` sort are
// pure reads over the SAME relations the navigator already demanded (`SavedAlbums` off the account row ∩ `AlbumArtists`),
// so they are pinned here, against real edges, beside the other slot-path facts — a count that asked for anything would be
// a fetch on a render. What is pinned here is the SAVED-ALBUM half, on an account that has liked nothing; the 2026-09-18
// correction widened `LibraryAlbumCountOf` to the whole release list (saved ∪ the albums your liked songs sit on) and that
// half — with `LibraryReleasesOf`, `LikedTracksOfAlbum` and `LibraryArtistsOf` — is `LibraryReleasesTests`.

using System.Collections.ObjectModel;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class LibraryRecencyTests
{
    public LibraryRecencyTests() => TestScope.Fresh();

    /// <summary>A real-shaped gid: 22 base62 characters (a fixture id would take the text form).</summary>
    static string Gid(int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 11), buf);
        return new string(buf);
    }

    static int AlbumRow(string uri, string title)
    {
        var t = Entities.Current.Albums;
        int slot = t.Slot(uri.AsSpan());
        t.SetText(ref t.Title, slot, Entities.Strings.Intern(title));
        return slot;
    }

    static int ArtistRow(string uri, string name)
    {
        var t = Entities.Current.Artists;
        int slot = t.Slot(uri.AsSpan());
        t.SetText(ref t.Name, slot, Entities.Strings.Intern(name));
        return slot;
    }

    /// <summary>A saved album billed to <paramref name="artists"/>, with a KNOWN track count.</summary>
    static int SavedAlbum(string key, string title, int tracks, params int[] artists)
    {
        var t = Entities.Current.Albums;
        int slot = AlbumRow("spotify:album:" + key, title);
        if (tracks >= 0)
        {
            t.TrackCount[slot] = tracks;
            t.Known[slot] |= (uint)AlbumFields.TrackCount;
        }
        Entities.Current.Edges.AlbumArtists.ReplaceRun(slot, artists, default);
        return slot;
    }

    // ── PlayedOf ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlayedOf_ReadsATextFormUri()
    {
        int slot = AlbumRow("spotify:album:recency-text", "Text");
        var id = LibraryRows.IdOf(EntityKind.Album, slot);
        Assert.Equal(EntityForm.Text, id.Form);

        var map = new Dictionary<string, long>(StringComparer.Ordinal) { ["spotify:album:recency-text"] = 1_234L };
        Assert.Equal(1_234L, LibraryRows.PlayedOf(map, id));
    }

    [Fact]
    public void PlayedOf_ReadsAGidUri_ThroughTheFormattedSpan()
    {
        string uri = "spotify:album:" + Gid(77);
        int slot = AlbumRow(uri, "Gid");
        var id = LibraryRows.IdOf(EntityKind.Album, slot);
        Assert.Equal(EntityForm.Gid, id.Form);

        var map = new Dictionary<string, long>(StringComparer.Ordinal) { [uri] = 99L };
        Assert.Equal(99L, LibraryRows.PlayedOf(map, id));
    }

    [Fact]
    public void PlayedOf_NeverPlayed_MissingMap_OrNoIdentity_IsZero()
    {
        int slot = AlbumRow("spotify:album:recency-never", "Never");
        var id = LibraryRows.IdOf(EntityKind.Album, slot);
        var other = new Dictionary<string, long>(StringComparer.Ordinal) { ["spotify:album:someone-else"] = 5L };

        Assert.Equal(0L, LibraryRows.PlayedOf(other, id));
        Assert.Equal(0L, LibraryRows.PlayedOf(new Dictionary<string, long>(), id));
        Assert.Equal(0L, LibraryRows.PlayedOf(null, id));
        Assert.Equal(0L, LibraryRows.PlayedOf(other, default));
    }

    [Fact]
    public void PlayedOf_WorksThroughAMapThatIsNotADictionary()
    {
        // The span probe is an optimisation over the concrete PlayLog map, never a requirement of the read.
        int slot = AlbumRow("spotify:album:recency-readonly", "ReadOnly");
        var map = new ReadOnlyDictionary<string, long>(new Dictionary<string, long> { ["spotify:album:recency-readonly"] = 42L });
        Assert.Equal(42L, LibraryRows.PlayedOf(map, LibraryRows.IdOf(EntityKind.Album, slot)));
    }

    [Fact]
    public void FillPlayed_WritesOneStampPerSlot_InSlotOrder()
    {
        int a = AlbumRow("spotify:album:fill-a", "A");
        int b = AlbumRow("spotify:album:fill-b", "B");
        var map = new Dictionary<string, long>(StringComparer.Ordinal) { ["spotify:album:fill-b"] = 7L };
        var into = new long[2];
        LibraryRows.FillPlayed(EntityKind.Album, [a, b], map, into);
        Assert.Equal(new[] { 0L, 7L }, into);
    }

    // ── the slot path agrees with the record rule ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Recents_OverSlots_OrdersExactlyAsTheRecordRule()
    {
        string[] titles = ["A", "B", "C", "D", "E"];
        var slots = new int[titles.Length];
        var facts = new LibraryNavFacts[titles.Length];
        for (int i = 0; i < titles.Length; i++)
        {
            string uri = "spotify:album:order-" + titles[i];
            slots[i] = AlbumRow(uri, titles[i]);
            facts[i] = new LibraryNavFacts(uri, titles[i], "", 0, null);
        }
        var recency = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["spotify:album:order-A"] = 100, ["spotify:album:order-C"] = 300, ["spotify:album:order-E"] = 200,
        };

        var played = new long[slots.Length];
        LibraryRows.FillPlayed(EntityKind.Album, slots, recency, played);
        var perm = new int[slots.Length];
        var sorter = new LibraryNavSorter<LibraryRows>();
        sorter.Order(new LibraryRows(EntityKind.Album, slots, slots.Length, played), LibraryNavSort.Recents, desc: false, perm);

        Assert.Equal(new[] { 2, 4, 0, 1, 3 }, perm);
        Assert.Equal(LibraryNavOrder.Order(facts, LibraryNavSort.Recents, desc: false, recency), perm);

        // The same sorter instance is reusable across sorts (the page keeps one per list): no state leaks between runs.
        sorter.Order(new LibraryRows(EntityKind.Album, slots, slots.Length), LibraryNavSort.Alphabetical, desc: true, perm);
        Assert.Equal(new[] { 4, 3, 2, 1, 0 }, perm);
    }

    [Fact]
    public void TheRemountKeys_OverSlots_EqualTheRecordKeys()
    {
        // The page's navigator key is computed over SLOTS; it must be the key 0.2.9's record rule produced, or the
        // nav.remount log line would name a different reason than the one the tests pin.
        string[] titles = ["Thriller", "Bad", "Off the Wall"];
        var slots = new int[titles.Length];
        var facts = new LibraryNavFacts[titles.Length];
        for (int i = 0; i < titles.Length; i++)
        {
            string uri = "spotify:album:keys-" + i;
            slots[i] = AlbumRow(uri, titles[i]);
            facts[i] = new LibraryNavFacts(uri, titles[i], "", 0, null);
        }
        var rows = new LibraryRows(EntityKind.Album, slots, slots.Length);

        Assert.Equal(LibraryNavOrder.OrderKey(facts), LibraryNavOrder.OrderKey(rows));
        Assert.Equal(LibraryNavOrder.FactsKey(facts), LibraryNavOrder.FactsKey(rows));
        Assert.StartsWith("3:", LibraryNavOrder.OrderKey(rows));
    }

    [Fact]
    public void Filter_IsACaseInsensitiveContains_InSourceOrder_AndAnEmptyQueryPassesEveryRow()
    {
        int a = AlbumRow("spotify:album:filter-a", "Thriller");
        int b = AlbumRow("spotify:album:filter-b", "Bad");
        int c = AlbumRow("spotify:album:filter-c", "Thriller 25");
        int[] source = [a, b, c, Table.None];
        var into = new int[source.Length];

        int n = LibraryRows.Filter(EntityKind.Album, source, "THRILL", into);
        Assert.Equal(new[] { a, c }, into[..n]);

        n = LibraryRows.Filter(EntityKind.Album, source, "   ", into);
        Assert.Equal(new[] { a, b, c }, into[..n]);   // the empty-slot marker is never a row
    }

    // ── the artists navigator's counts (the rework's "3 albums · 34 songs" and the Albums sort) ────────────────────

    [Fact]
    public void LibraryAlbumsOf_IsSavedAlbumsIntersectedWithTheArtistsBilling()
    {
        var scope = Entities.Current;
        scope.MeSlot = scope.Users.Slot("spotify:user:counts".AsSpan());
        int mj = ArtistRow("spotify:artist:mj", "Michael Jackson");
        int prince = ArtistRow("spotify:artist:prince", "Prince");
        int nobody = ArtistRow("spotify:artist:nobody", "Nobody Saved");

        int thriller = SavedAlbum("thriller", "Thriller", 9, mj);
        int bad = SavedAlbum("bad", "Bad", 11, mj);
        int duet = SavedAlbum("duet", "A Duet", 1, mj, prince);
        _ = SavedAlbum("dangerous", "Dangerous", 14, mj);                    // billed to MJ but NOT in the library
        scope.Edges.SavedAlbums.ReplaceRun(scope.MeSlot, [thriller, bad, duet], default);

        // The saved albums, in the library's own order, and the total is the return value.
        Span<int> into = stackalloc int[8];
        int n = User.LibraryAlbumsOf(mj, into);
        Assert.Equal(3, n);
        Assert.Equal(new[] { thriller, bad, duet }, into[..n].ToArray());
        Assert.Equal(3, User.LibraryAlbumCountOf(mj));
        Assert.Equal(1, User.LibraryAlbumCountOf(prince));            // only the duet
        Assert.Equal(0, User.LibraryAlbumCountOf(nobody));
        Assert.Equal(0, User.LibraryAlbumCountOf(Table.None));

        // A short buffer still answers the TOTAL (the caller resizes) and never writes past its end.
        Span<int> two = stackalloc int[2];
        two[0] = two[1] = -1;
        Assert.Equal(3, User.LibraryAlbumsOf(mj, two));
        Assert.Equal(new[] { thriller, bad }, two.ToArray());

        // the songs line: 9 + 11 + 1, and only over the SAVED albums.
        Assert.Equal(21, User.LibrarySongCountOf(mj));
        Assert.Equal(1, User.LibrarySongCountOf(prince));
        Assert.Equal(0, User.LibrarySongCountOf(nobody));
    }

    [Fact]
    public void LibrarySongCountOf_SkipsAnAlbumWhoseTrackCountHasNotAnswered()
    {
        // The count is a fact about what is KNOWN: an album still loading contributes 0 rather than a guess, and the row
        // drops the "songs" clause when the sum is 0 instead of claiming an empty discography.
        var scope = Entities.Current;
        scope.MeSlot = scope.Users.Slot("spotify:user:counts-unknown".AsSpan());
        int artist = ArtistRow("spotify:artist:unknown-counts", "Unnamed");
        int known = SavedAlbum("known", "Known", 4, artist);
        int pending = SavedAlbum("pending", "Pending", -1, artist);           // the track count has not answered
        scope.Edges.SavedAlbums.ReplaceRun(scope.MeSlot, [known, pending], default);

        Assert.Equal(2, User.LibraryAlbumCountOf(artist));
        Assert.Equal(4, User.LibrarySongCountOf(artist));
    }

    [Fact]
    public void Albums_OverSlots_OrdersByTheSavedAlbumCount_ThroughAPrecomputedBuffer()
    {
        var scope = Entities.Current;
        scope.MeSlot = scope.Users.Slot("spotify:user:counts-sort".AsSpan());
        int adele = ArtistRow("spotify:artist:adele", "Adele");
        int blur = ArtistRow("spotify:artist:blur", "Blur");
        int bowie = ArtistRow("spotify:artist:bowie", "Bowie");
        int dio = ArtistRow("spotify:artist:dio", "Dio");

        int a1 = SavedAlbum("a1", "21", 11, adele);
        int b1 = SavedAlbum("b1", "Parklife", 16, blur);
        int b2 = SavedAlbum("b2", "13", 13, blur);
        int b3 = SavedAlbum("b3", "Blur", 14, blur);
        int c1 = SavedAlbum("c1", "Low", 11, bowie);
        int c2 = SavedAlbum("c2", "Heroes", 10, bowie);
        int c3 = SavedAlbum("c3", "Hunky Dory", 11, bowie);
        scope.Edges.SavedAlbums.ReplaceRun(scope.MeSlot, [a1, b1, b2, b3, c1, c2, c3], default);

        int[] slots = [adele, blur, bowie, dio];
        var counts = new int[slots.Length];
        LibraryRows.FillCounts(EntityKind.Artist, slots, counts);
        Assert.Equal(new[] { 1, 3, 3, 0 }, counts);

        var perm = new int[slots.Length];
        new LibraryNavSorter<LibraryRows>()
            .Order(new LibraryRows(EntityKind.Artist, slots, slots.Length, null, counts), LibraryNavSort.Albums, desc: false, perm);

        // 3 (Blur), 3 (Bowie) — the tie breaks by title — then 1 (Adele), then the artist with nothing saved.
        Assert.Equal(new[] { 1, 2, 0, 3 }, perm);

        // With no precomputed buffer the same order comes out of the live read (the one-off path), so the page's
        // optimisation can never be the thing that decides the order.
        var live = new int[slots.Length];
        new LibraryNavSorter<LibraryRows>()
            .Order(new LibraryRows(EntityKind.Artist, slots, slots.Length), LibraryNavSort.Albums, desc: false, live);
        Assert.Equal(perm, live);
    }

    [Fact]
    public void FillCounts_ForAnyKindButArtist_IsZeroEverywhere()
    {
        // Albums and shows do not offer the word (LibraryWordRailTests), and the comparator must not invent a number for
        // them: the arm degrades to title order.
        var scope = Entities.Current;
        scope.MeSlot = scope.Users.Slot("spotify:user:counts-kind".AsSpan());
        int a = AlbumRow("spotify:album:count-kind-a", "A");
        int b = AlbumRow("spotify:album:count-kind-b", "B");
        var counts = new int[2];
        LibraryRows.FillCounts(EntityKind.Album, [a, b], counts);
        Assert.Equal(new[] { 0, 0 }, counts);
        Assert.Equal(0, new LibraryRows(EntityKind.Album, [a, b], 2).CountOf(0));
    }
}
