// ── Wavee.Tests/PlaylistPersistenceTests.cs — bug A1 regression: a persisted zero track count must not be trusted
// as count-known ──────────────────────────────────────────────────────────────────────────────────────────────────
//
// A build shipped before the resync-flagged-answer gate existed (Spotify.Decode.cs's PlaylistRevision,
// `changes_require_resync`) could commit `PlaylistFields.TrackCount` known alongside a bogus `TrackCount == 0` — a
// revision-gated `/diff` re-ask answering "resync needed" with a `contents` block anyway, decoded as if it were a
// trustworthy full read (see SidebarProjectionTests.cs's `AResyncFlaggedDiffAnswer_*` facts, which pin the DECODE
// half of the fix). That shape survives to disk exactly as staged, so `PlaylistShape.Load` must not trust it back
// on the next launch — masking the bit whenever the persisted count is 0 heals the row on its NEXT real answer
// instead of a permanent, restart-proof "0 songs".
//
// This needs a REAL Store round trip (a temp sqlite file, WriteBehind → Read), not the in-memory Staging shortcut —
// StoreTests.cs's own fixture shape and `Playlist_ddl_round_trip_and_owner_uri_cross_reference` are the pattern
// this file repeats (a second fixture rather than adding to that file, which this task does not own).

using System;
using System.Collections.Concurrent;
using System.IO;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaylistPersistenceTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-v3-playlist-persist-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public PlaylistPersistenceTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Post = a => _posted.Enqueue(a);      // the UI drain, run by the test thread where it belongs
        Store.Register(new PlaylistShape());
        Store.Use(_dbPath);
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Pins = null;                          // a pin source is process-wide; never leak one into the next test
        Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    Scope Boot()
    {
        Entities.Boot(CatalogScope.Fake());
        Store.Flush();                              // let Warm resolve the scope id before anything reads or writes
        return Entities.Current;
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    /// <summary>A real-shaped playlist uri: <c>spotify:playlist:</c> + 22 base62 characters (the GID form) — the
    /// half of the population that carries no uri STRING in memory at all, so <c>row.Id</c> must be the packed
    /// <see cref="EntityId"/>, never arena text (CLAUDE.md trap: a text-form id assigned this way is dropped by
    /// the store thread, which cannot touch the interner).</summary>
    static string GidUri(int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL + 17, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 5), buf);
        return "spotify:playlist:" + new string(buf);
    }

    /// <summary>THE HEAL: a row written to disk with the corrupted shape (the `TrackCount` bit known, value 0 —
    /// exactly what the pre-fix decoder could commit off a resync-flagged answer) reads back with the bit MASKED,
    /// never trusted — so the next real fetch re-verifies instead of the row being stuck at "0 songs" forever.</summary>
    [Fact]
    public void ARowPersistedWithTheCorruptedZeroShape_LoadsWithTheCountBitMasked()
    {
        Scope scope = Boot();
        PlaylistTable playlists = scope.Playlists;
        string uri = GidUri(1);
        int slot = playlists.Slot(uri.AsSpan());
        EntityId id = playlists.Id[slot];

        Staging s = Staging.Rent();
        ref var row = ref s.Playlists.Add();
        row.Id = id;
        row.Title = s.AddText("Eurodance Mix"u8);
        row.TrackCount = 0;
        // Exactly the corrupted shape found in a real library.db: Identity known, TrackCount ALSO known, but the
        // value is the bogus zero a resync-flagged diff answer left behind.
        row.Known = (uint)(PlaylistFields.Identity | PlaylistFields.TrackCount);
        row.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s));
        Store.Flush();

        Assert.True(Store.Read(scope, playlists, new[] { slot }, (uint)PlaylistFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        Assert.Equal(0, playlists.TrackCount[slot]);
        Assert.False(playlists.Knows(slot, (uint)PlaylistFields.TrackCount));   // masked — never trusted
        Assert.True(playlists.Knows(slot, (uint)PlaylistFields.Identity));      // Identity itself is untouched
    }

    /// <summary>The control: a row persisted with a REAL, nonzero count survives the round trip fully trusted — the
    /// heal targets a persisted zero specifically, never a legitimate count.</summary>
    [Fact]
    public void ARowPersistedWithARealNonzeroCount_LoadsWithTheCountBitTrusted()
    {
        Scope scope = Boot();
        PlaylistTable playlists = scope.Playlists;
        string uri = GidUri(2);
        int slot = playlists.Slot(uri.AsSpan());
        EntityId id = playlists.Id[slot];

        Staging s = Staging.Rent();
        ref var row = ref s.Playlists.Add();
        row.Id = id;
        row.Title = s.AddText("Real Count"u8);
        row.TrackCount = 50;
        row.Known = (uint)(PlaylistFields.Identity | PlaylistFields.TrackCount);
        row.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s));
        Store.Flush();

        Assert.True(Store.Read(scope, playlists, new[] { slot }, (uint)PlaylistFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        Assert.Equal(50, playlists.TrackCount[slot]);
        Assert.True(playlists.Knows(slot, (uint)PlaylistFields.TrackCount));
    }

    /// <summary>The relaunch: yesterday's daylist came back from disk as a Full Identity with NO window (Daylist is not
    /// persisted, so the held window is 0). The feed's first Thin row for today's edition must retitle it — a held
    /// window of 0 yields to any window, so the Thin write is accepted over the persisted Full one.</summary>
    [Fact]
    public void ARelaunchedDaylist_TakesTheFeedsThinRowForTheNewEdition()
    {
        Scope scope = Boot();
        PlaylistTable playlists = scope.Playlists;
        string uri = GidUri(3);
        int slot = playlists.Slot(uri.AsSpan());
        EntityId id = playlists.Id[slot];

        // Yesterday: a Full read of edition W1.
        Staging s = Staging.Rent();
        ref var row = ref s.Playlists.Add();
        row.Id = id;
        row.Title = s.AddText("daylist T1"u8);
        row.DaylistExpiresAt = 1_000;
        row.DaylistCreatedAt = 900;
        row.Known = (uint)(PlaylistFields.Identity | PlaylistFields.Daylist);
        row.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s));
        Store.Flush();

        // Relaunch: a fresh scope reads the row back.
        Store.Shutdown();
        Store.Register(new PlaylistShape());
        Store.Use(_dbPath);
        scope = Boot();
        playlists = scope.Playlists;
        slot = playlists.Slot(uri.AsSpan());
        id = playlists.Id[slot];
        Assert.True(Store.Read(scope, playlists, new[] { slot }, (uint)PlaylistFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        Assert.True(playlists.Knows(slot, (uint)PlaylistFields.Identity));
        Assert.False(playlists.Knows(slot, (uint)PlaylistFields.Daylist));
        Assert.Equal(0, playlists.DaylistExpiresAt[slot]);
        Assert.Equal("daylist T1", Entities.Strings.Resolve(playlists.Title[slot]));

        // Today: the feed's Thin card for edition W2.
        Staging s2 = Staging.Rent();
        ref var thin = ref s2.Playlists.Add();
        thin.Id = id;
        thin.Title = s2.AddText("daylist T2"u8);
        thin.DaylistExpiresAt = 2_000;
        thin.DaylistCreatedAt = 1_900;
        thin.Known = (uint)(PlaylistFields.Identity | PlaylistFields.Daylist);
        thin.Authority = Authority.Thin;
        Entities.Commit(s2);
        Entities.Publish();
        Staging.Return(s2);

        Assert.Equal("daylist T2", Entities.Strings.Resolve(playlists.Title[slot]));
        Assert.Equal(2_000, playlists.DaylistExpiresAt[slot]);
    }
}
