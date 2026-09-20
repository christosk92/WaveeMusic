using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ActivityLog over the in-memory store: record/order, prune (count + age), read/failed/undone transitions, suppression,
// and the IsUndoable matrix. No engine, no network — the log cache is the source of truth for the panel's Snapshot.
public class ActivityLogTests
{
    static ActivityLog NewLog(out InMemoryActivityStore store)
    {
        store = new InMemoryActivityStore();
        return new ActivityLog(store);
    }

    [Fact]
    public void Record_NewestFirst_AssignsMonotonicIds()
    {
        var log = NewLog(out _);
        long a = log.Record(ActivityKind.Save, "spotify:track:a");
        long b = log.Record(ActivityKind.Save, "spotify:track:b");

        Assert.True(b > a);
        Assert.Equal(2, log.Snapshot.Count);
        Assert.Equal("spotify:track:b", log.Snapshot[0].TargetUri);   // newest first
        Assert.Equal("spotify:track:a", log.Snapshot[1].TargetUri);
    }

    [Fact]
    public void Record_WhileSuppressed_DoesNotLog()
    {
        var log = NewLog(out _);
        using (log.SuppressRecording())
        {
            long id = log.Record(ActivityKind.Save, "spotify:track:a");
            Assert.Equal(-1, id);
        }
        Assert.Empty(log.Snapshot);
        // recording resumes after the scope
        log.Record(ActivityKind.Save, "spotify:track:b");
        Assert.Single(log.Snapshot);
    }

    [Fact]
    public void Suppression_IsRefCounted()
    {
        var log = NewLog(out _);
        var s1 = log.SuppressRecording();
        var s2 = log.SuppressRecording();
        s1.Dispose();
        Assert.True(log.IsSuppressed);   // still suppressed by s2
        s2.Dispose();
        Assert.False(log.IsSuppressed);
    }

    [Fact]
    public void MarkFailed_And_MarkUndone_TransitionStatus()
    {
        var log = NewLog(out _);
        long id = log.Record(ActivityKind.Save, "spotify:track:a");
        log.MarkFailed(id);
        Assert.Equal(ActivityStatus.Failed, Find(log, id).Status);
        long id2 = log.Record(ActivityKind.PlaylistRename, "spotify:playlist:p", "New", new ActivityPayload(OldName: "Old", NewName: "New"));
        log.MarkUndone(id2);
        Assert.Equal(ActivityStatus.Undone, Find(log, id2).Status);
    }

    [Fact]
    public void MarkAllRead_ClearsUnread()
    {
        var log = NewLog(out _);
        log.Record(ActivityKind.Save, "spotify:track:a");
        log.Record(ActivityKind.Save, "spotify:track:b");
        Assert.All(log.Snapshot, e => Assert.False(e.Read));
        log.MarkAllRead();
        Assert.All(log.Snapshot, e => Assert.True(e.Read));
    }

    [Fact]
    public void Clear_EmptiesTheLog()
    {
        var log = NewLog(out _);
        log.Record(ActivityKind.Save, "spotify:track:a");
        log.Clear();
        Assert.Empty(log.Snapshot);
    }

    [Fact]
    public void Prune_ByAge_DropsOldEntries_OnConstruction()
    {
        var store = new InMemoryActivityStore();
        long old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (40L * 24 * 3600 * 1000);   // 40 days ago
        long recent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Append(new ActivityEntry(1, ActivityKind.Save, "spotify:track:old", null, null, old, ActivityStatus.Done, false));
        store.Append(new ActivityEntry(2, ActivityKind.Save, "spotify:track:new", null, null, recent, ActivityStatus.Done, false));

        var log = new ActivityLog(store);   // ctor prunes 200 / 30 days
        Assert.Single(log.Snapshot);
        Assert.Equal("spotify:track:new", log.Snapshot[0].TargetUri);
    }

    [Fact]
    public void Prune_ByCount_CapsAt200()
    {
        var store = new InMemoryActivityStore();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < 250; i++)
            store.Append(new ActivityEntry(i + 1, ActivityKind.Save, "spotify:track:" + i, null, null, now + i, ActivityStatus.Done, false));

        var log = new ActivityLog(store);
        Assert.Equal(200, log.Snapshot.Count);
        Assert.Equal("spotify:track:249", log.Snapshot[0].TargetUri);   // newest kept
    }

    [Theory]
    [InlineData(ActivityKind.Save, true)]
    [InlineData(ActivityKind.Unsave, true)]
    [InlineData(ActivityKind.PlaylistAddTracks, true)]
    [InlineData(ActivityKind.PlaylistRemoveTracks, true)]
    [InlineData(ActivityKind.PlaylistMoveTracks, true)]
    [InlineData(ActivityKind.PlaylistRename, true)]
    [InlineData(ActivityKind.PlaylistVisibility, true)]
    [InlineData(ActivityKind.PlaylistCreate, false)]
    [InlineData(ActivityKind.PlaylistDelete, false)]
    [InlineData(ActivityKind.PlaylistCoverSet, false)]
    [InlineData(ActivityKind.PlaylistCoverClear, false)]
    [InlineData(ActivityKind.ContributorInvite, false)]
    public void IsUndoable_Matrix(ActivityKind kind, bool undoable)
    {
        var done = new ActivityEntry(1, kind, "spotify:x", null, null, 0, ActivityStatus.Done, false);
        Assert.Equal(undoable, done.IsUndoable);
        // Non-Done statuses are never undoable, even for invertible kinds.
        var failed = done with { Status = ActivityStatus.Failed };
        var undone = done with { Status = ActivityStatus.Undone };
        Assert.False(failed.IsUndoable);
        Assert.False(undone.IsUndoable);
    }

    static ActivityEntry Find(ActivityLog log, long id)
    {
        foreach (var e in log.Snapshot) if (e.Id == id) return e;
        throw new Xunit.Sdk.XunitException("entry " + id + " not found");
    }
}
