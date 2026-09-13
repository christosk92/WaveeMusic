// ── Wavee.Tests/TableTests.cs — slots, the two identity indexes, text ownership, D16 authority, publication ───────
//
// Wave 1's gate for the Table half of Entities/Entities.cs (plan §5). Everything runs against a local probe table, so
// these tests pin the MECHANISM and not any one kind's columns: a change to TrackTable cannot make them lie, and they
// were writable before TrackTable existed.
//
// Since 2026-09-12 the table keeps TWO indexes over one `Column<EntityId>` (docs/plans/wavee/wavee-0.3-entity-identity-
// memory.md, option 2): an open-addressed `int[]` for the gid-form rows and today's `Dictionary<StringId,int>` for the
// text-form ones. Three things have to hold whatever the form, and this file is where they are held:
//   · one entity is one row, however it was spelled and whichever door it came through (a uri, a StringId, 16 raw gid
//     bytes, the user-namespaced playlist spelling);
//   · a hit allocates nothing, and a MISS does not intern the probe;
//   · a freed row gives its interned text back — the leak (defect 1) that made a trim pointless.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>A minimal kind table: two field groups with their own authority columns, which is the shape every real one
/// has (plan §4.2's <c>IdentityAuthority</c> / <c>ExtrasAuthority</c>) — including the <see cref="Table.ReleaseText"/>
/// override every table with a <c>Column&lt;StringId&gt;</c> owes the interner.</summary>
sealed class ProbeTable : Table
{
    public const uint Title = 1 << 0;
    public const uint Duration = 1 << 1;
    public const uint Identity = Title | Duration;
    public const uint PlayCount = 1 << 8;

    public Column<StringId> TitleText;
    public Column<int> DurationMs, PlayCounts;
    public Column<byte> IdentityAuthority, ExtrasAuthority;

    public override EntityKind Kind => EntityKind.Track;

    protected override void GrowColumns(int capacity)
    {
        TitleText.EnsureCapacity(capacity);
        DurationMs.EnsureCapacity(capacity);
        PlayCounts.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        ExtrasAuthority.EnsureCapacity(capacity);
    }

    /// <summary>One <see cref="Table.ClearText"/> per text column — the whole of the discipline (defect 1).</summary>
    protected override void ReleaseText(int slot) => ClearText(ref TitleText, slot);
}

[Collection(EntitiesCollection.Name)]
public class TableTests
{
    static StringId Uri(string s) => Entities.Strings.Intern(s);

    /// <summary>A distinct, real-shaped catalog uri: <c>spotify:track:</c> + 22 base62 characters.</summary>
    static string GidUri(int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL + 11, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 3), buf);
        return "spotify:track:" + new string(buf);
    }

    // ── slots ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Slot_zero_is_never_handed_out_because_zero_means_none()
    {
        var table = new ProbeTable();
        Assert.Equal(1, table.Count);                       // slot 0 exists and is nobody's
        Assert.Equal(0, table.LiveCount);

        int first = table.Alloc(Uri("spotify:track:a"));
        int second = table.Alloc(Uri("spotify:track:b"));
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(2, table.LiveCount);
        Assert.NotEqual(Table.None, first);
    }

    [Fact]
    public void An_unseen_uri_gets_an_empty_row_and_a_seen_one_gets_the_same_slot()
    {
        var table = new ProbeTable();
        int slot = table.Slot(Uri("spotify:track:c"));
        Assert.Equal(0u, table.Known[slot]);                // it exists; nothing about it is known — the skeleton state
        Assert.False(table.Knows(slot, ProbeTable.Title));
        Assert.Equal(slot, table.Slot(Uri("spotify:track:c")));
        Assert.Equal(1, table.LiveCount);
    }

    [Fact]
    public void A_freed_slot_is_recycled_and_its_version_keeps_climbing()
    {
        var table = new ProbeTable();
        int slot = table.Alloc(Uri("spotify:track:d"));
        table.Applied(slot, ProbeTable.Identity, Authority.Full, ref table.IdentityAuthority);
        uint before = table.Version[slot];

        table.FreeSlot(slot);
        Assert.True(table.Version[slot] > before);
        Assert.False(table.TryGetSlot(Uri("spotify:track:d"), out _));      // the uri stops resolving
        Assert.Equal(0, table.LiveCount);
        Assert.True(table.Id[slot].IsEmpty);                                // …and the row carries no identity at all

        int reused = table.Alloc(Uri("spotify:track:e"));
        Assert.Equal(slot, reused);                                         // P5: the slot comes back
        Assert.True(table.Version[reused] > before + 1);                    // …with a version a stale handle can detect
        Assert.Equal(0u, table.Known[reused]);
    }

    [Fact]
    public void AllocRun_hands_out_one_contiguous_block_that_Bind_gives_identities_to()
    {
        var table = new ProbeTable();
        table.Alloc(Uri("spotify:track:f"));
        int first = table.AllocRun(1_700);                                  // ch 31's seed shape

        Assert.Equal(2, first);                                             // straight after the one earlier row
        for (int i = 0; i < 1_700; i++)
        {
            Assert.Equal(0u, table.Known[first + i]);                       // every row of the run starts empty…
            Assert.True(table.Version[first + i] >= 1);                     // …and versioned, so a handle can compare
        }
        Assert.Equal(first + 1_700, table.Count);
        Assert.True(table.Version.Capacity >= table.Count);
        Assert.Equal(Table.None, table.AllocRun(0));

        // The seed's own shape: allocate the run, then bind each row's identity. Bind is what indexes it — a bare
        // `Id[slot] = …` would index nothing and refcount nothing.
        for (int i = 0; i < 8; i++) table.Bind(first + i, EntityId.Parse(GidUri(i).AsSpan()));
        for (int i = 0; i < 8; i++) Assert.Equal(first + i, table.Slot(GidUri(i).AsSpan()));
        Assert.Equal(8, table.IndexedRows);
    }

    // ── one entity, one row, whichever door it came through (P6, P14) ───────────────────────────────────────────────

    [Fact]
    public void The_same_row_is_reached_from_a_StringId_a_char_span_and_utf8_bytes()
    {
        var table = new ProbeTable();
        int slot = table.Slot(Uri("spotify:track:g"));                      // a fixture id: the TEXT form

        Assert.Equal(slot, table.Slot("spotify:track:g".AsSpan()));
        Assert.Equal(slot, table.Slot("spotify:track:g"u8));
        Assert.True(table.TryGetSlot("spotify:track:g"u8, out int found));
        Assert.Equal(slot, found);
        Assert.Equal(1, table.LiveCount);                                   // three spellings, one row
        Assert.Equal(1, table.TextRows);
        Assert.Equal(0, table.IndexedRows);
    }

    /// <summary>The GID form, through all four doors — including the 16 raw protobuf bytes, which is the one that used
    /// to cost a 569 ns base62 encode plus a text lookup before it could name a row (doc §2).</summary>
    [Fact]
    public void A_catalog_uri_and_the_raw_gid_behind_it_are_the_same_row()
    {
        var table = new ProbeTable();
        string uri = GidUri(42);
        int slot = table.Slot(uri.AsSpan());

        Assert.Equal(EntityForm.Gid, table.Id[slot].Form);
        Assert.Equal(slot, table.Slot(System.Text.Encoding.UTF8.GetBytes(uri)));
        Assert.Equal(slot, table.Slot(Uri(uri)));                           // even through an interned string
        Assert.Equal(slot, table.Slot(EntityId.Parse(uri.AsSpan())));

        Span<byte> gid = stackalloc byte[Base62.GidBytes];
        table.Id[slot].WriteGid(gid);
        Assert.Equal(slot, table.Slot(EntityKind.Track, gid));              // the decoder's door
        Assert.True(table.TryGetSlot(EntityKind.Track, gid, out int found));
        Assert.Equal(slot, found);

        Assert.Equal(1, table.LiveCount);                                   // five doors, one row
        Assert.Equal(1, table.IndexedRows);
        Assert.Equal(0, table.TextRows);                                    // …and not one byte of uri text
        Assert.Equal(uri, table.Id[slot].Text);
    }

    /// <summary>DEFECT 4, at the table: two spellings of one playlist allocated two rows because they interned to two
    /// <c>StringId</c>s (doc §4.4). The gid is decoded from the trailing segment, so there is one row by construction.</summary>
    [Fact]
    public void The_user_namespaced_playlist_spelling_lands_on_the_canonical_row()
    {
        var table = new ProbeTable();
        string gid = GidUri(7)["spotify:track:".Length..];

        int canonical = table.Slot($"spotify:playlist:{gid}".AsSpan());
        int namespaced = table.Slot($"spotify:user:christos:playlist:{gid}".AsSpan());

        Assert.Equal(canonical, namespaced);
        Assert.Equal(1, table.LiveCount);
        Assert.Equal($"spotify:playlist:{gid}", table.Id[canonical].Text);
    }

    [Fact]
    public void A_span_lookup_that_hits_allocates_nothing_in_either_form()
    {
        var table = new ProbeTable();
        string gidUri = GidUri(3);
        byte[] gidBytes = System.Text.Encoding.UTF8.GetBytes(gidUri);
        byte[] textBytes = System.Text.Encoding.UTF8.GetBytes("wavee:local:file:allocation-probe");
        table.Slot(gidBytes);
        table.Slot(textBytes);
        for (int i = 0; i < 4; i++)                                         // warm-up
        {
            table.TryGetSlot(gidBytes, out _);
            table.TryGetSlot(textBytes, out _);
            table.TryGetSlot(gidUri.AsSpan(), out _);
        }

        int hits = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)                                       // one wire batch (P14)
        {
            if (table.TryGetSlot(gidBytes, out _)) hits++;
            if (table.TryGetSlot(textBytes, out _)) hits++;
            if (table.TryGetSlot(gidUri.AsSpan(), out _)) hits++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(900, hits);
        Assert.Equal(0L, allocated);
    }

    /// <summary>A miss allocates no row — and, for a text-form uri, does not INTERN the probe either: an id nobody
    /// AddRefs is permanent, so a lookup that quietly interned every miss would be the same leak in another coat.</summary>
    [Fact]
    public void A_lookup_that_misses_allocates_no_row_and_interns_nothing()
    {
        var table = new ProbeTable();
        int mapBefore = Entities.Strings.MapCount;

        Assert.False(table.TryGetSlot("wavee:local:file:never-seen-20260912"u8, out int slot));
        Assert.False(table.TryGetSlot("wavee:local:file:never-seen-20260912".AsSpan(), out _));
        Assert.False(table.TryGetSlot(GidUri(999).AsSpan(), out _));
        Assert.Equal(0, slot);
        Assert.Equal(0, table.LiveCount);
        Assert.Equal(mapBefore, Entities.Strings.MapCount);
    }

    // ── the open-addressed index (doc §4.1) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_index_grows_with_the_table_and_every_row_stays_findable()
    {
        var table = new ProbeTable();
        const int Rows = 2_000;                                             // several doublings past the initial 32
        var slots = new int[Rows];
        for (int i = 0; i < Rows; i++) slots[i] = table.Slot(GidUri(i).AsSpan());

        Assert.Equal(Rows, table.IndexedRows);
        Assert.True(table.IndexCapacity >= Rows * 4 / 3, $"load {Rows / (double)table.IndexCapacity:N2} is above 0.75");
        for (int i = 0; i < Rows; i++)
        {
            Assert.True(table.TryGetSlot(GidUri(i).AsSpan(), out int found));
            Assert.Equal(slots[i], found);
        }
    }

    /// <summary>Removal is a backward shift, not a tombstone (a like/unlike storm must not degrade the probe into a
    /// scan) — and a backward shift is exactly the operation that silently loses rows when its wrap-around case is
    /// wrong. Free two thirds and every survivor must still be found.</summary>
    [Fact]
    public void Freeing_rows_keeps_every_survivor_findable_through_the_index()
    {
        var table = new ProbeTable();
        const int Rows = 600;
        for (int i = 0; i < Rows; i++) table.Slot(GidUri(i).AsSpan());

        for (int i = 0; i < Rows; i++)
            if (i % 3 != 0) table.FreeSlot(table.Slot(GidUri(i).AsSpan()));

        for (int i = 0; i < Rows; i++)
        {
            bool alive = i % 3 == 0;
            Assert.Equal(alive, table.TryGetSlot(GidUri(i).AsSpan(), out _));
        }
        Assert.Equal(Rows / 3, table.IndexedRows);

        // …and the freed identities can come back, on recycled slots, and be found again.
        for (int i = 1; i < Rows; i += 3) table.Slot(GidUri(i).AsSpan());
        for (int i = 1; i < Rows; i += 3) Assert.True(table.TryGetSlot(GidUri(i).AsSpan(), out _));
    }

    [Fact]
    public void The_two_forms_share_a_table_without_sharing_an_index()
    {
        var table = new ProbeTable();
        int gid = table.Slot(GidUri(1).AsSpan());
        int local = table.Slot("wavee:local:file:QzpcbXVzaWNcYS5tcDM".AsSpan());
        int fixture = table.Slot("spotify:track:abc".AsSpan());

        Assert.Equal(3, table.LiveCount);
        Assert.Equal(1, table.IndexedRows);
        Assert.Equal(2, table.TextRows);
        Assert.NotEqual(gid, local);
        Assert.NotEqual(local, fixture);
        Assert.Equal(EntityProvider.Local, table.Id[local].Provider);       // provider is a field, not a re-parse
        Assert.Equal(EntityProvider.Spotify, table.Id[fixture].Provider);
    }

    // ── text ownership (defect 1) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE leak this change exists to fix. Before it, <c>FreeSlot</c> dropped the map entry and left the uri,
    /// the title and the image in the interner for the life of the process, so a scope's floor only ever rose
    /// (doc §4.4). A freed row now hands both back.</summary>
    [Fact]
    public void A_freed_row_gives_its_uri_and_its_columns_back_to_the_interner()
    {
        var table = new ProbeTable();
        int mapBefore = Entities.Strings.MapCount;

        int slot = table.Slot("wavee:local:file:a-uri-nothing-else-interns-20260912".AsSpan());
        table.SetText(ref table.TitleText, slot, Entities.Strings.Intern("a title nothing else interns 20260912"));
        Assert.Equal(mapBefore + 2, Entities.Strings.MapCount);             // the uri and the title

        table.FreeSlot(slot);
        Assert.Equal(mapBefore, Entities.Strings.MapCount);                 // …both reclaimed
        Assert.Equal(StringId.Empty, table.TitleText[slot]);                // and blanked, so the recycled slot is clean
    }

    /// <summary>A row re-answered a hundred times owns ONE title's worth of interner at the end of it: <c>SetText</c>
    /// releases what it overwrites. Writing the column directly is what leaks the other ninety-nine.</summary>
    [Fact]
    public void Overwriting_a_text_column_releases_the_value_it_replaced()
    {
        var table = new ProbeTable();
        int slot = table.Slot("wavee:local:file:overwrite-probe-20260912".AsSpan());
        int mapBefore = Entities.Strings.MapCount;

        for (int i = 0; i < 100; i++)
            table.SetText(ref table.TitleText, slot, Entities.Strings.Intern($"take {i} 20260912"));

        Assert.Equal(mapBefore + 1, Entities.Strings.MapCount);             // one live title, not a hundred
        Assert.Equal("take 99 20260912", Entities.Strings.Resolve(table.TitleText[slot]));
    }

    /// <summary>A retired scope hands its whole text budget back in one call (<c>Entities.Switch</c> does this for the
    /// table set it drops). A gid-form row has nothing to give: that is the 158 B/row the packed id removes.</summary>
    [Fact]
    public void Releasing_the_whole_table_returns_every_string_it_owned()
    {
        var table = new ProbeTable();
        int mapBefore = Entities.Strings.MapCount;

        for (int i = 0; i < 20; i++)
        {
            int slot = table.Slot($"wavee:local:file:scope-teardown-{i}-20260912".AsSpan());
            table.SetText(ref table.TitleText, slot, Entities.Strings.Intern($"scope teardown title {i} 20260912"));
        }
        for (int i = 0; i < 20; i++) table.Slot(GidUri(500 + i).AsSpan());  // gid rows: no text to leak in the first place
        Assert.Equal(mapBefore + 40, Entities.Strings.MapCount);

        table.ReleaseAllText();
        Assert.Equal(mapBefore, Entities.Strings.MapCount);
        Assert.Equal(0, table.IndexedRows);
        Assert.Equal(0, table.TextRows);
    }

    // ── known bits and the D16 authority merge ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Knows_asks_for_ALL_of_the_bits_not_any_of_them()
    {
        var table = new ProbeTable();
        int slot = table.Alloc(Uri("spotify:track:h"));
        table.Known[slot] = ProbeTable.Title;
        Assert.True(table.Knows(slot, ProbeTable.Title));
        Assert.False(table.Knows(slot, ProbeTable.Identity));               // Duration is still missing
        table.Known[slot] |= ProbeTable.Duration;
        Assert.True(table.Knows(slot, ProbeTable.Identity));
    }

    /// <summary>D16, as a table. The rule in one line: a lower authority never overwrites a higher one, but it always
    /// fills a group nobody has filled.</summary>
    [Theory]
    [InlineData(Authority.Thin, Authority.Full, false, true)]   // hole → a thin answer may fill it
    [InlineData(Authority.Thin, Authority.Full, true, false)]   // already full → a thin answer may NOT degrade it
    [InlineData(Authority.Full, Authority.Full, true, true)]    // same rung → the newer answer wins
    [InlineData(Authority.Full, Authority.Thin, true, true)]    // higher rung → wins
    [InlineData(Authority.Seed, Authority.Thin, true, false)]   // the demo seed never beats the wire (ch 31 GAP 2)
    [InlineData(Authority.Seed, Authority.None, false, true)]   // …but it does fill an empty row
    [InlineData(Authority.Full, Authority.Local, true, false)]  // a user edit is not overwritten by the catalog
    public void Accepts_is_the_authority_rule(Authority incoming, Authority existing, bool known, bool expected)
        => Assert.Equal(expected, Table.Accepts(incoming, existing, known ? ProbeTable.Identity : 0u, ProbeTable.Identity));

    [Fact]
    public void A_thin_answer_fills_an_unknown_group_and_then_cannot_overwrite_a_full_one()
    {
        var table = new ProbeTable();
        int slot = table.Alloc(Uri("spotify:track:i"));
        table.Inflight[slot] = 3;
        Entities.Now = 1_000;

        // 1. a search hit fills the empty identity group
        Assert.True(table.Accepts(slot, ProbeTable.Identity, Authority.Thin, in table.IdentityAuthority));
        table.SetText(ref table.TitleText, slot, Uri("thin title"));
        table.Applied(slot, ProbeTable.Identity, Authority.Thin, ref table.IdentityAuthority);
        Assert.True(table.Knows(slot, ProbeTable.Identity));
        Assert.Equal((byte)Authority.Thin, table.IdentityAuthority[slot]);
        Assert.Equal(1_000, table.FetchedAt[slot]);
        Assert.Equal(0u, table.Inflight[slot]);                             // the request is answered (C7)

        // 2. the real fetch lands and wins
        Assert.True(table.Accepts(slot, ProbeTable.Identity, Authority.Full, in table.IdentityAuthority));
        table.SetText(ref table.TitleText, slot, Uri("full title"));
        table.Applied(slot, ProbeTable.Identity, Authority.Full, ref table.IdentityAuthority);
        Assert.Equal((byte)Authority.Full, table.IdentityAuthority[slot]);

        // 3. a second search hit for the same row is refused
        Assert.False(table.Accepts(slot, ProbeTable.Identity, Authority.Thin, in table.IdentityAuthority));
        Assert.Equal("full title", Entities.Strings.Resolve(table.TitleText[slot]));

        // 4. …but it may still fill the OTHER group, which nobody has written
        Assert.True(table.Accepts(slot, ProbeTable.PlayCount, Authority.Thin, in table.ExtrasAuthority));
    }

    [Fact]
    public void Applied_bumps_the_version_so_a_bound_row_re_reads()
    {
        var table = new ProbeTable();
        int slot = table.Alloc(Uri("spotify:track:j"));
        uint before = table.Version[slot];
        table.Applied(slot, ProbeTable.Title, Authority.Full, ref table.IdentityAuthority);
        Assert.Equal(before + 1, table.Version[slot]);
    }

    // ── capacity (P5) ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Columns_double_and_never_shrink()
    {
        var table = new ProbeTable();
        int start = table.Version.Capacity;
        Assert.True(start >= 16);

        for (int i = 0; i < 200; i++) table.Alloc(Uri("spotify:track:cap" + i));
        int grown = table.Version.Capacity;
        Assert.True(grown >= 201);
        Assert.Equal(grown, table.TitleText.Capacity);                      // the kind's columns grew in lockstep
        Assert.Equal(grown, table.IdentityAuthority.Capacity);
        Assert.Equal(grown, table.Id.Capacity);

        for (int i = 0; i < 200; i++) table.FreeSlot(i + 1);
        Assert.Equal(grown, table.Version.Capacity);                        // freeing 200 rows shrinks nothing
    }

    /// <summary>Presizing is what turns a warm read of a known row count into one allocation instead of a doubling
    /// chain (doc §4.2) — and it has to presize the INDEX too, or the map that replaced the dictionary rebuilds itself
    /// eleven times on the way to 10k rows.</summary>
    [Fact]
    public void EnsureCapacity_presizes_the_index_as_well_as_the_columns()
    {
        var table = new ProbeTable();
        table.EnsureCapacity(10_001);

        Assert.True(table.Id.Capacity >= 10_001);
        Assert.True(table.IndexCapacity >= 10_001 * 4 / 3);
        Assert.Equal(16_384, table.IndexCapacity);                          // the power of two above 10,001 / 0.75
    }

    // ── publication (D8, C3) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Many_writes_in_one_drain_publish_exactly_once_per_table()
    {
        Entities.Publish();                                                 // drain whatever an earlier test left
        var a = new ProbeTable();
        var b = new ProbeTable();
        uint publicationBefore = Entities.Publication;
        uint aBefore = a.Changed.Peek();

        for (int i = 0; i < 50; i++) a.Alloc(Uri("spotify:track:pub-a" + i));
        b.Alloc(Uri("spotify:track:pub-b"));
        Assert.Equal(2, Entities.PendingPublications);                      // 51 writes, two dirty tables

        uint publication = Entities.Publish();
        Assert.Equal(publicationBefore + 1, publication);
        Assert.Equal(publication, a.Changed.Peek());
        Assert.Equal(publication, b.Changed.Peek());
        Assert.NotEqual(aBefore, a.Changed.Peek());
        Assert.Equal(0, Entities.PendingPublications);
    }

    [Fact]
    public void A_drain_that_changed_nothing_does_not_bump_the_publication()
    {
        Entities.Publish();
        uint before = Entities.Publication;
        Assert.Equal(before, Entities.Publish());
        Assert.Equal(before, Entities.Publication);
    }
}
