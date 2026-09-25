// ── Wavee.Tests/StagingLeaseTests.cs — one owner per Staging, and no text read across a Reset ─────────────────────────
//
// 2026-09-25: persisted track images, titles, album uris, share urls, release dates and copyrights were found holding
// OTHER rows' text at small offsets ("unnikhttps://i.scdn.co/image/…", "…f4630a6388risingRich Brian"): two owners had
// appended into one `Staging` text arena. These facts pin the three doors that made that possible:
//   (a) the pool took the same instance back twice, so two renters were handed ONE arena;
//   (b) a `TextRef` taken before a Return was still readable after the next renter's decode — and resolved to that
//       renter's bytes;
//   (c) the store thread appended into a staging it had been handed (`RowWriter.Id` → `AddText`), i.e. wrote into an
//       arena a racing second owner was also writing.

using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;
using Md = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class StagingLeaseTests
{
    /// <summary>The cover the player bar's History asked Spotify for, mangled, on 2026-09-25 (verify profile log).</summary>
    const string CoverHex = "ab67616d0000b27321456c115d2f0f44f4630a63";

    static byte[] Gid(byte seed)
    {
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return gid;
    }

    static ByteString Bs(byte[] bytes) => ByteString.CopyFrom(bytes);

    static byte[] TrackV4(byte seed, string title, string album, string artist, byte[] coverFileId) => new Md.Track
    {
        Gid = Bs(Gid(seed)),
        Name = title,
        Duration = 200_000,
        Album = new Md.Album
        {
            Gid = Bs(Gid((byte)(seed + 1))),
            Name = album,
            CoverGroup = new Md.ImageGroup { Image = { new Md.Image { FileId = Bs(coverFileId), Size = Md.Image.Types.Size.Default } } },
        },
        Artist = { new Md.Artist { Gid = Bs(Gid((byte)(seed + 2))), Name = artist } },
    }.ToByteArray();

    // ── (a) the pool ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_second_return_of_one_lease_is_refused_and_never_hands_one_arena_to_two_renters()
    {
        var s = Staging.Rent();
        Staging.Return(s);

        Exception? second = Record.Exception(() => Staging.Return(s));

        var a = Staging.Rent();
        var b = Staging.Rent();
        try
        {
            Assert.NotSame(a, b);
            Assert.IsAssignableFrom<InvalidOperationException>(second);
        }
        finally
        {
            Staging.Return(a);
            if (!ReferenceEquals(a, b)) Staging.Return(b);
        }
    }

    [Fact]
    public void A_staging_nobody_rented_cannot_be_returned()
    {
        var s = Staging.Rent();
        Staging.Return(s);
        // `s` now sits in the pool: returning it is returning a pooled instance, not a lease.
        Assert.ThrowsAny<InvalidOperationException>(() => Staging.Return(s));
        Assert.ThrowsAny<InvalidOperationException>(() => Staging.Return(s));
        var again = Staging.Rent();
        Staging.Return(again);
    }

    // ── (b) the deterministic reproduction ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rows_staged_before_a_return_never_commit_the_next_renters_text()
    {
        TestScope.Fresh();
        byte[] cover = Convert.FromHexString(CoverHex);
        string expected = "https://i.scdn.co/image/" + CoverHex;

        var s = Staging.Rent();
        Spotify.Decode.TrackV4(TrackV4(10, "Nightrider", "Nightrider", "Warren Hue", cover), s);
        StagedTrack first = s.Tracks[0];                 // the first owner's row, still in its hand
        Staging.Return(s);                                // … while the arena goes back to the pool

        var again = Staging.Rent();
        Assert.Same(s, again);                            // LIFO: the very same arena
        Spotify.Decode.TrackV4(TrackV4(60, "Rich Brian", "88rising", "Rich Brian",
            Convert.FromHexString("ab67616d0000b273ffffffffffffffffffffffff")), again);
        again.Tracks.Clear();
        again.Tracks.Add() = first;                       // the first owner commits ITS rows

        Exception? refused = Record.Exception(() => Entities.Commit(again));
        Entities.Publish();
        Staging.Return(again);

        var track = Entities.Track(EntityId.ForGid(EntityKind.Track, Gid(10)));
        string image = track.ImageId.IsEmpty ? "" : Entities.Strings.Resolve(track.ImageId);
        string title = track.Title;
        Assert.True(image == expected || (refused is InvalidOperationException && image.Length == 0),
            $"committed image '{image}' title '{title}' (refused: {refused?.GetType().Name ?? "no"})");
        Assert.True(refused is InvalidOperationException || title == "Nightrider", $"committed title '{title}'");
    }

    [Fact]
    public void A_text_ref_is_readable_for_exactly_the_lease_that_staged_it()
    {
        var s = Staging.Rent();
        TextRef mine = s.AddText("mine"u8);
        Assert.Equal("mine", Encoding.UTF8.GetString(s.Utf8(mine)));
        s.Reset();
        s.AddText("next"u8);
        Assert.ThrowsAny<InvalidOperationException>(() => s.Utf8(mine).Length);
        Staging.Return(s);
    }

    // ── the seed, found in the verify instance ───────────────────────────────────────────────────────────────────────
    //
    // Not a double return: none fired in six verify sessions. The generation check did — every refusal was a THIN row
    // (TrackV4's album, 0x27 = Title|Image|Year|Release) carrying label/copyright/courtesy/share/date slices of the
    // PREVIOUS lease's row at the same index. The zeroing in `StagedList<T>.Add` (`row = default`) is dropped by the
    // tier-1 PGO JIT once `RowFor`'s `row.Init(...)` follows it (FreshSlot's remark). Both facts below need the method
    // to tier up, so they loop: on .NET 10.0.8 the first stale row shows at round ~161.

    [Fact]
    public void A_reused_staged_list_hands_out_a_zeroed_row_after_the_jit_tiers_up()
    {
        var list = new StagedList<StagedAlbum>();
        var old = new TextRef(5, 7, 9);
        for (int round = 0; round < 3000; round++)
        {
            list.Clear();
            for (int i = 0; i < 40; i++)
            {
                ref var full = ref list.RowFor(new StagedId(EntityId.ForGid(EntityKind.Album, Gid((byte)i))), Authority.Full, 0xff);
                full.Label = old;
                full.ShareUrl = old;
                full.Kind = 2;
            }
            list.Clear();
            for (int i = 0; i < 40; i++)
                list.RowFor(new StagedId(EntityId.ForGid(EntityKind.Album, Gid((byte)i))), Authority.Thin, 0x27).Year = 2020;
            for (int i = 0; i < 40; i++)
                Assert.True(list[i].Label.IsEmpty && list[i].ShareUrl.IsEmpty && list[i].Kind == 0,
                    $"round {round} row {i}: label {list[i].Label} share {list[i].ShareUrl} kind {list[i].Kind}");
        }
    }

    [Fact]
    public void A_thin_album_decoded_into_a_reused_arena_carries_nothing_from_the_previous_lease()
    {
        // The verify instance's own sequence: an artist overview (discography releases WITH label, copyright, share url
        // and date) returned, then a TrackV4 batch (thin albums WITHOUT them) decoded into the same arena.
        TestScope.Fresh();
        byte[] overview = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spotify", "artist-maroon5.json"));
        byte[] artist = Encoding.UTF8.GetBytes("spotify:artist:04gDigrS5kc9YWfZHwBETP");
        byte[][] thin = new byte[20][];
        for (int i = 0; i < thin.Length; i++)
            thin[i] = TrackV4((byte)(i * 3), "T" + i, "A" + i, "Warren Hue", Convert.FromHexString(CoverHex));

        for (int round = 0; round < 500; round++)
        {
            var s = Staging.Rent();
            Spotify.Decode.ArtistPage(overview, artist, s);
            Staging.Return(s);

            var again = Staging.Rent();
            foreach (byte[] track in thin) Spotify.Decode.TrackV4(track, again);
            string? stale = null;
            for (int i = 0; i < again.Albums.Count && stale is null; i++)
            {
                StagedAlbum a = again.Albums[i];
                if (!a.Label.IsEmpty || !a.ShareUrl.IsEmpty || !a.ReleaseDateIso.IsEmpty || !a.Copyright.IsEmpty)
                    stale = $"round {round} row {i}: label {a.Label} share {a.ShareUrl} date {a.ReleaseDateIso} copyright {a.Copyright}";
            }
            Staging.Return(again);
            Assert.True(stale is null, stale);
        }
    }

    [Fact]
    public void A_deferred_concert_run_reusing_a_slot_carries_no_parent_from_the_previous_batch()
    {
        // The other `x = default` through a reference in the staging lists (Concert.cs CloseDeferred's `run = default`),
        // audited against the same miscompile: a deferred run that is never bound must not inherit the previous
        // batch's parent, key or append mode at its index — the commit would land the edges under that parent.
        var list = new StagedConcertEdgeList();
        var parent = new StagedId(EntityId.ForGid(EntityKind.Artist, Gid(3)));
        var key = new TextRef(4, 9, 7);
        for (int round = 0; round < 3000; round++)
        {
            list.Clear();
            for (int i = 0; i < 8; i++)
            {
                int mark = list.PendingMark;
                list.Push();
                if ((i & 1) == 0) list.Close(ConcertLink.Lineup, in parent, mark);
                else list.CloseKeyed(ConcertLink.FeedSection, key, mark, append: true);
            }
            Assert.True(list.RunCount == 8 && !list.Runs[0].Parent.IsEmpty && list.Runs[1].Mode == 1);   // the previous tenants
            list.Clear();
            for (int i = 0; i < 8; i++)
            {
                int mark = list.PendingMark;
                list.Push();
                list.CloseDeferred(ConcertLink.ArtistConcerts, mark);
            }
            for (int i = 0; i < list.RunCount; i++)
            {
                StagedConcertRun run = list.Runs[i];
                Assert.True(run.Parent.IsEmpty && run.ParentKey.IsEmpty && run.Mode == 0,
                    $"round {round} run {i}: parent empty {run.Parent.IsEmpty} key {run.ParentKey} mode {run.Mode}");
            }
        }
    }

    // ── (c) the store thread ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The real track shape, measured: how far the arena moved while the store thread bound the batch.</summary>
    sealed class ArenaWatchTrackShape : KindShape
    {
        readonly TrackShape _inner = new();
        public int Growth = -1;
        public override EntityKind Kind => _inner.Kind;
        public override string Table => _inner.Table;
        public override ReadOnlySpan<StoreColumn> Columns => _inner.Columns;

        public override void Save(Staging s, RowWriter w)
        {
            int before = s.AddText("|"u8).Offset;       // the probe itself: one byte either side
            _inner.Save(s, w);
            Growth = s.AddText("|"u8).Offset - before - 1;
        }

        public override void Load(RowReader r, Staging into) => _inner.Load(r, into);
    }

    [Fact]
    public void The_store_thread_never_appends_into_a_handed_over_staging()
    {
        string db = Path.Combine(Path.GetTempPath(), "wavee-lease-" + Guid.NewGuid().ToString("n") + ".db");
        var shape = new ArenaWatchTrackShape();
        Fetch.Reset();
        Store.Shutdown();
        Store.Post = static a => a();
        Store.Register(shape);
        Store.Use(db);
        try
        {
            Entities.Boot(CatalogScope.Fake());
            Store.Flush();
            Entities.Now = 1_000;

            int faultsBefore = Store.Stats.Faults;
            var s = Staging.Rent();
            Spotify.Decode.TrackV4(TrackV4(10, "Nightrider", "Nightrider", "Warren Hue", Convert.FromHexString(CoverHex)), s);
            Assert.False(s.Tracks[0].AlbumUri.Packed.IsEmpty);          // the album arrives as a gid: the Id() door
            Entities.Commit(s);
            Assert.True(Store.WriteBehind(s));                           // handed over
            Store.Flush();

            Assert.Equal(0, shape.Growth);
            Assert.Equal(faultsBefore, Store.Stats.Faults);

            var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = db, Pooling = false };
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(cs.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT album_uri FROM track";
            Assert.Equal(EntityId.ForGid(EntityKind.Album, Gid(11)).Text, cmd.ExecuteScalar() as string);
        }
        finally
        {
            Fetch.Reset();
            Store.Shutdown();
            Store.Use(null);
            Store.Register(new TrackShape());
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(db + suffix); } catch (IOException) { }
        }
    }
}
