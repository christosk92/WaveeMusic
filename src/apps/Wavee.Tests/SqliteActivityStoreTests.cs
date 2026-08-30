using System;
using System.IO;
using Wavee.Backend.Persistence;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// SqliteActivityStore against a temp library.db: durability across reopen, the prune SQL (count + age), and coexistence
// with SqliteColdStore on the SAME file (WAL + busy_timeout). Flush() drains the write-behind queue so reads are
// deterministic.
public class SqliteActivityStoreTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), "wavee-acttest-" + Guid.NewGuid().ToString("N") + ".db");

    static ActivityEntry Entry(long id, long ts, ActivityPayload? payload = null) =>
        new(id, ActivityKind.PlaylistAddTracks, "spotify:playlist:p", "My List", payload, ts, ActivityStatus.Done, false);

    [Fact]
    public void Survives_Reopen_WithPayloadRoundTrip()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new ActivityPayload(
            Tracks: new[] { new ActivityTrackRef("spotify:track:a", "Song A", "uid-a") },
            OldName: "Old", NewName: "New", NewIsPublic: true, FromIndex: 1, ToIndex: 4);

        using (var store = new SqliteActivityStore(_path))
        {
            store.Append(Entry(1, now, payload));
            store.Append(Entry(2, now + 1));
            store.Flush();
        }

        using (var reopened = new SqliteActivityStore(_path))
        {
            var recent = reopened.LoadRecent(10);
            Assert.Equal(2, recent.Count);
            Assert.Equal(2, recent[0].Id);              // newest first
            var e = recent[1];
            Assert.Equal("My List", e.TargetName);
            Assert.NotNull(e.Payload);
            Assert.Equal("Old", e.Payload!.OldName);
            Assert.Equal("New", e.Payload.NewName);
            Assert.True(e.Payload.NewIsPublic);
            Assert.Equal(1, e.Payload.FromIndex);
            var track = Assert.Single(e.Payload.Tracks!);
            Assert.Equal("spotify:track:a", track.Uri);
            Assert.Equal("Song A", track.Name);
            Assert.Equal("uid-a", track.ItemId);
        }
    }

    [Fact]
    public void Prune_ByCount_KeepsNewest()
    {
        using var store = new SqliteActivityStore(_path);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 1; i <= 20; i++) store.Append(Entry(i, now + i));
        store.Flush();
        store.Prune(maxCount: 5, maxAgeMs: long.MaxValue / 2);
        store.Flush();

        var recent = store.LoadRecent(100);
        Assert.Equal(5, recent.Count);
        Assert.Equal(20, recent[0].Id);
        Assert.Equal(16, recent[4].Id);
    }

    [Fact]
    public void Prune_ByAge_DropsOld()
    {
        using var store = new SqliteActivityStore(_path);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Append(Entry(1, now - (40L * 24 * 3600 * 1000)));   // 40 days old
        store.Append(Entry(2, now));
        store.Flush();
        store.Prune(maxCount: 200, maxAgeMs: 30L * 24 * 3600 * 1000);
        store.Flush();

        var recent = store.LoadRecent(100);
        Assert.Single(recent);
        Assert.Equal(2, recent[0].Id);
    }

    [Fact]
    public void Coexists_WithSqliteColdStore_OnSameFile()
    {
        // The cold store owns its own tables + schema-version key; the activity store owns only activity_log.
        var cold = new SqliteColdStore(_path);
        _ = cold.GetRootlistRevision();   // exercise a cold read (its schema is intact)

        using var store = new SqliteActivityStore(_path);
        store.Append(Entry(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        store.Flush();
        Assert.Single(store.LoadRecent(10));

        // The cold store keeps working after the activity store wrote to the same file.
        Assert.Null(cold.GetRootlistRevision());
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { if (File.Exists(_path + suffix)) File.Delete(_path + suffix); } catch { }
    }
}
