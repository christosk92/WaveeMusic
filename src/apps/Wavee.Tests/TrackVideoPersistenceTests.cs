// ── Wavee.Tests/TrackVideoPersistenceTests.cs — the film mark must survive the disk ──────────────────────────────────
//
// `TrackFlags.HasVideo` is the whole music-video verdict, and extension kind 99 is the only wire answer that ever sets
// it (`Spotify.Decode.VideoAssociations`). `TrackShape` persisted the Video group as the COUNTERPART URI ALONE while
// listing `TrackFields.Video` in `ExtrasFields`, so `Load` restored the group's KNOWN bit with none of its fact behind
// it: a cached track came back saying "Video: answered, and there is none", `Fetch.NeedOf = wanted & ~Known` found no
// hole, kind 99 was never asked again, and the film mark was gone for that track for good.
//
// It bit hardest on a SELF-CONTAINED music video, which the decoder documents answers with `files` and NO
// `associated_uri` (G-057) — its `VideoUri` is empty, so the single persisted column held nothing at all and the
// restore lost 100% of the group.
//
// This is `TrackShape`'s own stated rule ("a bit with no data behind it is a promise the disk cannot keep", the class
// doc) and bug C's shape one group over, so it gets the same kind of gate: a real file, a real cold read into a fresh
// slot, and an assertion on what the FILE restores rather than on what memory still remembers.
//
// Harness shape is `EpisodeShapeAuthorityTests`': a temp db, `Store.Post` pumped by the test thread, `FreeSlot` +
// `Store.Read` for the cold leg.

using System;
using System.Collections.Concurrent;
using System.IO;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class TrackVideoPersistenceTests : IDisposable
{
    const string CounterpartTrack = "spotify:track:0TDLuuLlV54CkRRUOahJb4";
    const string SelfContainedTrack = "spotify:track:6habFhsOp2NvshLv26DqMb";
    const string VideoUri = "spotify:track:1kNkXcKsbcM1fEVFF4AVnL";

    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-track-video-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public TrackVideoPersistenceTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Post = a => _posted.Enqueue(a);
        Store.Register(new TrackShape());
        Store.Use(_dbPath);
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    /// <summary>Write one track's Video group to the file, drop the slot, read it back cold, and hand back the fresh
    /// slot — the same cold leg `EpisodeShapeAuthorityTests` uses, so the assertions are about the FILE.</summary>
    static int RoundTrip(TrackTable tracks, Scope scope, EntityId id, Action<Staging> stage)
    {
        int slot = tracks.Slot(id);
        Staging wire = Staging.Rent();
        stage(wire);
        Assert.True(Store.WriteBehind(wire));
        Store.Flush();

        tracks.FreeSlot(slot);
        int cold = tracks.Slot(id);
        Assert.True(Store.Read(scope, tracks, new[] { cold }, (uint)TrackFields.All, FetchPriority.Visible));
        Store.Flush();
        return cold;
    }

    /// <summary>THE regression: a self-contained music video (files, no counterpart uri) must still say it has a video
    /// after a cold start. Before the fix this restored `Knows(Video) == true` with `HasVideo == false` — the worst of
    /// both, because the true bit is exactly what stops the fetcher asking again.</summary>
    [Fact]
    public void A_self_contained_music_video_survives_a_cold_read()
    {
        Entities.Boot(CatalogScope.Fake());
        Store.Flush();
        Scope scope = Entities.Current;
        TrackTable tracks = scope.Tracks;
        Assert.True(EntityId.TryParseGid(SelfContainedTrack.AsSpan(), out EntityId id));

        int cold = RoundTrip(tracks, scope, id, s =>
        {
            ref var row = ref s.Tracks.RowFor(id, Authority.Full, (uint)TrackFields.Video);
            // No VideoUri at all: the G-057 shape the decoder calls out, where `files > 0` is the whole verdict.
            row.VideoImage = s.Text("ab67616d0000b273deadbeefdeadbeefdeadbeef");
            row.VideoW = 1920;
            row.VideoH = 1080;
            row.Flags |= (uint)TrackFlags.HasVideo;
        });
        DrainPosts();

        var track = new Track(cold);
        Assert.True(track.Knows(TrackFields.Video));
        Assert.True(track.HasVideo);                       // the bug: this used to read false, permanently
        Assert.Equal(1920, tracks.VideoW[cold]);
        Assert.Equal(1080, tracks.VideoH[cold]);
    }

    /// <summary>The counterpart shape keeps working, still and size included — the columns that were dropped along with
    /// the verdict.</summary>
    [Fact]
    public void A_counterpart_video_restores_its_uri_still_and_size()
    {
        Entities.Boot(CatalogScope.Fake());
        Store.Flush();
        Scope scope = Entities.Current;
        TrackTable tracks = scope.Tracks;
        Assert.True(EntityId.TryParseGid(CounterpartTrack.AsSpan(), out EntityId id));

        int cold = RoundTrip(tracks, scope, id, s =>
        {
            ref var row = ref s.Tracks.RowFor(id, Authority.Full, (uint)TrackFields.Video);
            row.VideoUri = s.Text(VideoUri);
            row.VideoImage = s.Text("ab67616d0000b273cafebabecafebabecafebabe");
            row.VideoW = 1280;
            row.VideoH = 720;
            row.Flags |= (uint)TrackFlags.HasVideo;
        });
        DrainPosts();

        var track = new Track(cold);
        Assert.True(track.Knows(TrackFields.Video));
        Assert.True(track.HasVideo);
        Assert.True(track.VideoCounterpart.IsValid);
        Assert.Equal(1280, tracks.VideoW[cold]);
        Assert.Equal(720, tracks.VideoH[cold]);
    }

    /// <summary>...and a REAL negative round-trips as a negative. "Answered, and there is no video" is a fact worth
    /// persisting — it is what stops the film lane asking again — so the restore must keep the group known and the
    /// verdict false, which is indistinguishable from the bug unless the positive cases above also pass.</summary>
    [Fact]
    public void An_answered_absence_restores_as_known_and_still_false()
    {
        Entities.Boot(CatalogScope.Fake());
        Store.Flush();
        Scope scope = Entities.Current;
        TrackTable tracks = scope.Tracks;
        Assert.True(EntityId.TryParseGid(CounterpartTrack.AsSpan(), out EntityId id));

        int cold = RoundTrip(tracks, scope, id, s =>
            s.Tracks.RowFor(id, Authority.Full, (uint)TrackFields.Video));
        DrainPosts();

        var track = new Track(cold);
        Assert.True(track.Knows(TrackFields.Video));
        Assert.False(track.HasVideo);
    }
}
