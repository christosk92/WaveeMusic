// ── Wavee.Tests/PlaylistPersistenceTests.cs — the playlist row across a restart: the count is authoritative ─────────
//
// Wave D2 (docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §3.2) makes the persisted count the
// truth: `track_count` is rewritten, with the count-known bit, in the SAME transaction as a settled membership list
// (Store.Lists.cs), so a stored 0 is a real, empty playlist. The load-time mask that used to read every persisted 0 as
// "unknown" (bug A1's heal for rows a pre-fix decoder corrupted) is gone — those rows live in a file this schema never
// opens (the file's name carries the schema) — and these facts pin what replaced it: a real zero loads known, a real
// count loads trusted, and the count saved with a list becomes the row's count on the next read even when the row never
// carried one of its own.
//
// This needs a REAL Store round trip (a temp sqlite file, WriteBehind → Read), not the in-memory Staging shortcut —
// StoreTests.cs's own fixture shape and `Playlist_ddl_round_trip_and_owner_uri_cross_reference` are the pattern.

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

    /// <summary>A genuinely EMPTY playlist: the full read said `length: 0` (count known) and the row persisted it. It
    /// reads back as a KNOWN zero — "0 songs", an answer — never as "unknown", which is what re-asked every empty
    /// playlist on every launch while the load-time mask stood.</summary>
    [Fact]
    public void AGenuinelyEmptyPlaylistsPersistedZero_LoadsAsAKnownZero()
    {
        Scope scope = Boot();
        PlaylistTable playlists = scope.Playlists;
        string uri = GidUri(1);
        int slot = playlists.Slot(uri.AsSpan());
        EntityId id = playlists.Id[slot];

        Staging s = Staging.Rent();
        ref var row = ref s.Playlists.Add();
        row.Id = id;
        row.Title = s.AddText("Empty on purpose"u8);
        row.TrackCount = 0;
        row.Known = (uint)(PlaylistFields.Identity | PlaylistFields.TrackCount);
        row.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s));
        Store.Flush();

        Assert.True(Store.Read(scope, playlists, new[] { slot }, (uint)PlaylistFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        Assert.Equal(0, playlists.TrackCount[slot]);
        Assert.True(playlists.Knows(slot, (uint)PlaylistFields.TrackCount));    // an answer, not a hole
        Assert.True(playlists.Knows(slot, (uint)PlaylistFields.Identity));
    }

    /// <summary>The control: a row persisted with a REAL, nonzero count survives the round trip fully trusted.</summary>
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

    /// <summary>THE COUNT SAVED WITH THE LIST IS THE ROW'S COUNT. The batch's header row carried no count at all (only
    /// the revision — the shape of an answer whose Identity came from elsewhere), yet the settled two-row list it
    /// landed rewrote the row's `track_count` and count-known bit in the list's own transaction — so the next read of
    /// the ROW, with no list in sight, knows the playlist has two tracks. The commit's count group lands its own value
    /// for exactly this row: one that knows its count and nothing else.</summary>
    [Fact]
    public void TheCountSavedWithTheList_IsTheRowsCountOnTheNextRead()
    {
        Scope scope = Boot();
        PlaylistTable playlists = scope.Playlists;
        string uri = GidUri(4);

        Staging s = ListAnswers.FullRead(uri, ListAnswers.RevisionA,
                                         [ListAnswers.Member(ListAnswers.TrackUri(41), "0a41"), ListAnswers.Member(ListAnswers.TrackUri(42), "0a42")],
                                         headerKnown: 0);
        Entities.Commit(s);
        Assert.True(Store.WriteBehind(s));
        Store.Flush();

        int slot = playlists.Slot(uri.AsSpan());
        Assert.False(playlists.Knows(slot, (uint)PlaylistFields.TrackCount));     // the answer itself said nothing

        Assert.True(Store.Read(scope, playlists, new[] { slot }, (uint)PlaylistFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        Assert.Equal(2, playlists.TrackCount[slot]);
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
