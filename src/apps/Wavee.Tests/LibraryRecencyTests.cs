// ── Wavee.Tests/LibraryRecencyTests.cs — the library's play-recency read and the slot path of the ordering rule ────────
//
// The "RecentsRecency (library part)" of ch 15 §8: the Recents sort reads `Shell.PlayLog.Recency` (uri → last played,
// unix ms) through `LibraryRows.PlayedOf`, which probes the map with the uri formatted into a SPAN so a gid row never
// becomes a string. What is pinned here is the read (text form, gid form, the non-Dictionary fallback, never-played)
// and that the page's allocation-free path — slots in a buffer, `LibraryNavSorter<LibraryRows>` — orders and keys
// EXACTLY as the record rule 0.2.9's tests pin (`LibraryNavOrderTests`). `RecentsRecency.Stamps` itself is owner P's.

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
}
