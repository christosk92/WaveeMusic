using System;
using System.IO;
using Xunit;

namespace Wavee.Tests;

// PlayLogStore (§C1.8.1): the local "recently PLAYED" log behind a JumpBackIn section with Recents = Played. Covers the
// 200-entry ring cap, the context-first dedupe read API the sidebar consumes, context classification, the play-log.json
// persistence round trip (the HistoryStore pattern), and the corrupt-file fallback.
//
// Every test points the store at its own temp file; the real %LOCALAPPDATA% is never touched.
public class PlayLogStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-playlog-tests", Guid.NewGuid().ToString("n"));
    readonly string _path;

    public PlayLogStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "play-log.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    PlayLogStore Store()
    {
        var s = new PlayLogStore();
        s.Init(_path);
        s.LoadFromDisk();
        return s;
    }

    static long Ms(int i) => 1753000000000L + i * 60_000L;

    // ── append + ring cap ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Append_RecordsTrackContextKindAndTime()
    {
        var store = Store();
        Assert.True(store.Append("spotify:track:t1", "spotify:album:a1", Ms(0)));

        var e = Assert.Single(store.Entries);
        Assert.Equal("spotify:track:t1", e.TrackUri);
        Assert.Equal("spotify:album:a1", e.ContextUri);
        Assert.Equal(PlayContextKind.Album, e.ContextKind);
        Assert.Equal(Ms(0), e.PlayedAtMs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Ms(0)).UtcDateTime, e.PlayedAtUtc);
        Assert.Equal(1, store.Revision);
    }

    [Fact]
    public void Append_RejectsAnEmptyTrackUri()
    {
        var store = Store();
        Assert.False(store.Append(null, "spotify:album:a1", Ms(0)));
        Assert.False(store.Append("", "spotify:album:a1", Ms(0)));
        Assert.Empty(store.Entries);
        Assert.Equal(0, store.Revision);
    }

    [Fact]
    public void Append_StampsNowWhenNoTimeIsGiven()
    {
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = Store();
        store.Append("spotify:track:t1", null);
        Assert.True(store.Entries[0].PlayedAtMs >= before);
    }

    [Fact]
    public void Append_SuppressesAPushStormAtTheSameBoundary()
    {
        var store = Store();
        Assert.True(store.Append("spotify:track:t1", "spotify:album:a1", Ms(0)));
        Assert.False(store.Append("spotify:track:t1", "spotify:album:a1", Ms(0)));           // identical push
        Assert.False(store.Append("spotify:track:t1", "spotify:album:a1", Ms(0) + 400));     // 400 ms later — same play
        Assert.True(store.Append("spotify:track:t1", "spotify:album:a1", Ms(0) + 5000));     // a genuine replay
        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(2, store.Revision);
    }

    [Fact]
    public void Append_SameTrackInADifferentContextIsANewPlay()
    {
        var store = Store();
        store.Append("spotify:track:t1", "spotify:album:a1", Ms(0));
        Assert.True(store.Append("spotify:track:t1", "spotify:playlist:p1", Ms(0)));
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void RingIsCappedAt200_DroppingTheOldest()
    {
        var store = Store();
        for (int i = 0; i < 260; i++) store.Append("spotify:track:t" + i, "spotify:album:a" + i, Ms(i));

        Assert.Equal(PlayLogStore.MaxEntries, store.Entries.Count);
        Assert.Equal(200, PlayLogStore.MaxEntries);
        Assert.Equal("spotify:track:t60", store.Entries[0].TrackUri);      // 260 - 200
        Assert.Equal("spotify:track:t259", store.Entries[^1].TrackUri);    // newest LAST
    }

    // ── context classification ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("spotify:album:1DFixLWuPkv3KT3TnV35m3", PlayContextKind.Album)]
    [InlineData("spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", PlayContextKind.Playlist)]
    [InlineData("spotify:user:someone:playlist:abc", PlayContextKind.Playlist)]
    [InlineData("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", PlayContextKind.Artist)]
    [InlineData("spotify:show:4rOoJ6Egrf8K2IrywzwOMk", PlayContextKind.Show)]
    [InlineData("spotify:collection:tracks", PlayContextKind.Collection)]
    [InlineData("spotify:station:artist:x", PlayContextKind.Other)]
    [InlineData("", PlayContextKind.None)]
    [InlineData(null, PlayContextKind.None)]
    public void ClassifyContext_MapsTheContextFamilies(string? uri, PlayContextKind expected)
    {
        Assert.Equal(expected, PlayLogStore.ClassifyContext(uri));
    }

    // ── the sidebar's context-first read API ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecentContexts_CollapsesToDistinctContextsNewestFirst()
    {
        var store = Store();
        store.Append("spotify:track:t1", "spotify:album:a1", Ms(0));
        store.Append("spotify:track:t2", "spotify:album:a1", Ms(1));    // same album — one row
        store.Append("spotify:track:t3", "spotify:playlist:p1", Ms(2));
        store.Append("spotify:track:t4", "spotify:album:a2", Ms(3));
        store.Append("spotify:track:t5", "spotify:playlist:p1", Ms(4));  // p1 again — moves to the FRONT, still one row

        var rows = store.RecentContexts(8);

        Assert.Equal(3, rows.Count);
        Assert.Equal("spotify:playlist:p1", rows[0].Uri);
        Assert.Equal(PlayContextKind.Playlist, rows[0].Kind);
        Assert.Equal("spotify:track:t5", rows[0].TrackUri);              // the track that produced the newest play
        Assert.Equal(Ms(4), rows[0].PlayedAtMs);
        Assert.Equal("spotify:album:a2", rows[1].Uri);
        Assert.Equal("spotify:album:a1", rows[2].Uri);
        Assert.All(rows, r => Assert.False(r.IsTrack));
    }

    [Fact]
    public void RecentContexts_FallsBackToATrackRowWhenThereIsNoContext()
    {
        var store = Store();
        store.Append("spotify:track:single", null, Ms(0));
        store.Append("spotify:track:t2", "spotify:album:a1", Ms(1));

        var rows = store.RecentContexts(8);

        Assert.Equal(2, rows.Count);
        Assert.Equal("spotify:album:a1", rows[0].Uri);
        Assert.False(rows[0].IsTrack);
        Assert.Equal("spotify:track:single", rows[1].Uri);
        Assert.True(rows[1].IsTrack);
        Assert.Equal(PlayContextKind.None, rows[1].Kind);
        Assert.Equal("spotify:track:single", rows[1].TrackUri);
    }

    [Fact]
    public void RecentContexts_HonorsTheMaxAndDegradesGracefully()
    {
        var store = Store();
        for (int i = 0; i < 20; i++) store.Append("spotify:track:t" + i, "spotify:album:a" + i, Ms(i));

        Assert.Equal(3, store.RecentContexts(3).Count);
        Assert.Equal(5, store.RecentContexts(5).Count);
        Assert.Empty(store.RecentContexts(0));
        Assert.Empty(store.RecentContexts(-1));
        Assert.Empty(Store().RecentContexts(8));                          // an empty log is not an error
        Assert.Equal("spotify:album:a19", store.RecentContexts(3)[0].Uri);
    }

    [Fact]
    public void RecentContexts_PrefersTheNewestPlayOfARepeatedContext()
    {
        var store = Store();
        store.Append("spotify:track:t1", "spotify:album:a1", Ms(0));
        store.Append("spotify:track:t9", "spotify:album:a1", Ms(9));
        var row = Assert.Single(store.RecentContexts(8));
        Assert.Equal(Ms(9), row.PlayedAtMs);
        Assert.Equal("spotify:track:t9", row.TrackUri);
    }

    // ── persistence ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SurvivesReopen_WithEveryFieldRoundTripped()
    {
        var store = Store();
        store.Append("spotify:track:t1", "spotify:album:a1", Ms(0));
        store.Append("spotify:track:t2", null, Ms(1));
        store.Append("spotify:track:t3", "spotify:show:s1", Ms(2));
        store.SaveAndWait();

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));   // write-then-rename leaves no temp

        var reopened = Store();
        Assert.Equal(3, reopened.Entries.Count);
        Assert.Equal("spotify:track:t1", reopened.Entries[0].TrackUri);
        Assert.Equal(PlayContextKind.Album, reopened.Entries[0].ContextKind);
        Assert.Equal("", reopened.Entries[1].ContextUri);
        Assert.Equal(PlayContextKind.None, reopened.Entries[1].ContextKind);
        Assert.Equal(PlayContextKind.Show, reopened.Entries[2].ContextKind);
        Assert.Equal(Ms(2), reopened.Entries[2].PlayedAtMs);
        Assert.Equal(0, reopened.Revision);          // LoadFromDisk does not bump: no listeners exist at startup
    }

    [Fact]
    public void ContextTitle_RoundTripsAndSurfacesOnRecentContexts()
    {
        var store = Store();
        Assert.True(store.Append("spotify:track:t1", "spotify:playlist:p1", Ms(0), "Summer Hits"));
        store.SaveAndWait();

        var reopened = Store();
        Assert.Equal("Summer Hits", reopened.Entries[0].ContextTitle);
        var row = Assert.Single(reopened.RecentContexts(8));
        Assert.Equal("spotify:playlist:p1", row.Uri);
        Assert.Equal("Summer Hits", row.Title);
    }

    [Fact]
    public void LegacyFileWithoutTitle_LoadsWithNullTitle()
    {
        File.WriteAllText(_path,
            "[{\"track\":\"spotify:track:t1\",\"context\":\"spotify:playlist:p1\",\"kind\":2,\"atMs\":1}]");
        var store = Store();
        Assert.Null(store.Entries[0].ContextTitle);
        Assert.Null(Assert.Single(store.RecentContexts(8)).Title);
    }

    [Fact]
    public void PersistedFile_NeverExceedsTheRingCap()
    {
        var store = Store();
        for (int i = 0; i < 500; i++) store.Append("spotify:track:t" + i, "spotify:album:a" + i, Ms(i));
        store.SaveAndWait();

        var reopened = Store();
        Assert.Equal(PlayLogStore.MaxEntries, reopened.Entries.Count);
        Assert.Equal("spotify:track:t300", reopened.Entries[0].TrackUri);
        Assert.Equal("spotify:track:t499", reopened.Entries[^1].TrackUri);
    }

    [Fact]
    public void AnOverlongFileOnDisk_IsTrimmedOnLoad()
    {
        // A file written by a build with a larger cap must not blow the ring open.
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < 400; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"track\":\"spotify:track:t{i}\",\"context\":\"spotify:album:a{i}\",\"kind\":1,\"atMs\":{Ms(i)}}}");
        }
        sb.Append(']');
        File.WriteAllText(_path, sb.ToString());

        var store = Store();
        Assert.Equal(PlayLogStore.MaxEntries, store.Entries.Count);
        Assert.Equal("spotify:track:t399", store.Entries[^1].TrackUri);
    }

    [Fact]
    public void CorruptFile_StartsEmpty_AndNeverThrows()
    {
        File.WriteAllText(_path, "{ this is not the array we expected");
        byte[] original = File.ReadAllBytes(_path);
        var log = new WaveeLog();
        var store = new PlayLogStore(log);
        store.Init(_path);
        store.LoadFromDisk();
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(_path));
        Assert.Equal(original, File.ReadAllBytes(_path + ".corrupt"));
        var failure = Assert.Single(log.Snapshot(), e => e.EventId == "sidebar.play_log.load_failed");
        Assert.DoesNotContain(_path, failure.Format());

        // …and the store is still fully usable: the next save simply replaces the unusable file.
        store.Append("spotify:track:t1", "spotify:album:a1", Ms(0));
        store.SaveAndWait();
        Assert.Single(Store().Entries);
        Assert.Equal(original, File.ReadAllBytes(_path + ".corrupt"));
    }

    [Fact]
    public void BinaryGarbageFile_StartsEmpty()
    {
        File.WriteAllBytes(_path, new byte[] { 0x00, 0xFF, 0x13, 0x37, 0xDE, 0xAD });
        Assert.Empty(Store().Entries);
    }

    [Fact]
    public void RowsWithNoTrackUri_AreSkippedOnLoad()
    {
        File.WriteAllText(_path,
            """[{"track":"","context":"spotify:album:a1","kind":1,"atMs":1},{"track":"spotify:track:t1","kind":0,"atMs":2}]""");
        var store = Store();
        Assert.Single(store.Entries);
        Assert.Equal("spotify:track:t1", store.Entries[0].TrackUri);
    }

    [Fact]
    public void AnUnknownPersistedKind_DegradesToOther()
    {
        File.WriteAllText(_path, """[{"track":"spotify:track:t1","context":"spotify:future:x","kind":250,"atMs":1}]""");
        var store = Store();
        Assert.Equal(PlayContextKind.Other, store.Entries[0].ContextKind);
        Assert.Equal("spotify:future:x", store.Entries[0].ContextUri);   // the uri is preserved verbatim
    }

    [Fact]
    public void Clear_EmptiesTheLogAndBumpsTheRevision()
    {
        var store = Store();
        store.Append("spotify:track:t1", "spotify:album:a1", Ms(0));
        store.SaveAndWait();
        int revision = store.Revision;

        store.Clear();

        Assert.Empty(store.Entries);
        Assert.Equal(revision + 1, store.Revision);
        Assert.Empty(store.RecentContexts(8));
    }

    [Fact]
    public void NoPathConfigured_IsInert()
    {
        // Before Init (e.g. the fake backend / a headless harness) the store still works in memory and writes nothing.
        var store = new PlayLogStore();
        store.LoadFromDisk();
        Assert.True(store.Append("spotify:track:t1", "spotify:album:a1", Ms(0)));
        store.Flush();
        store.SaveAndWait();
        Assert.Single(store.Entries);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void SaveFailure_IsLoggedOnce_AndRecoveryIsLoggedWithoutPaths()
    {
        string blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "x");
        string target = Path.Combine(blocker, "play-log.json");
        var log = new WaveeLog();
        var store = new PlayLogStore(log);
        store.Init(target);
        store.Append("spotify:track:t1", "spotify:album:a1", Ms(0));

        store.SaveAndWait();
        store.SaveAndWait(); // the same failure edge is deduped

        var failed = Assert.Single(log.Snapshot(), e => e.EventId == "sidebar.play_log.save_failed");
        Assert.DoesNotContain(target, failed.Format());

        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        store.SaveAndWait();

        Assert.True(File.Exists(target));
        Assert.Single(log.Snapshot(), e => e.EventId == "sidebar.play_log.save_recovered");
    }

    [Fact]
    public void Revision_TracksAcceptedAppendsOnly()
    {
        var store = Store();
        Assert.Equal(0, store.Revision);
        store.Append("spotify:track:t1", "spotify:album:a1", Ms(0));
        Assert.Equal(1, store.Revision);
        store.Append("spotify:track:t1", "spotify:album:a1", Ms(0));   // suppressed duplicate
        Assert.Equal(1, store.Revision);
        Assert.Equal(store.Revision, store.Version.Peek());
    }

    [Fact]
    public void DefaultPath_SitsBesideHistoryJson()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wavee", "WaveeMusic", "play-log.json");
        Assert.Equal(expected, PlayLogStore.DefaultPath());
    }
}
