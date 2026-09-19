using System;
using System.Collections.Immutable;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE DURATION FOLD. Two rules that used to be one, and the bug was the missing half:
/// <list type="number">
/// <item>a stated length (&gt; 0) always wins, and a SAME-URI republish never regresses it to 0 (a queue mutation
///   re-publishing a thin row must not erase a duration that is already correct);</item>
/// <item>a REAL track change adopts the new playable's length VERBATIM — including 0. The old "write only when &gt; 0"
///   rule left the previous track's number in place for anything whose length is unknown (a live broadcast, a module
///   link resolved with <c>durationMs 0</c>), which is how a 3:25 song left "-3:25" counting down over a stream that
///   will never reach it.</item>
/// </list>
/// And on top of both: while the playable is LIVE the published duration is 0 whatever the slab holds — a broadcast has
/// no end to count down to, and that answer is folded on READ so the real length survives for the next track.
/// </summary>
public class NowPlayingDurationFoldTests
{
    const string SongUri = "spotify:track:song";
    const string StreamUri = "wavee:module:wavee.youtube:dFJzUXNUTXZQTmc";

    static NowPlayingProjection New() => new("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());

    static Track TrackFor(string uri, long durationMs) => new(
        Id: uri, Uri: uri, Title: "t", Artists: Array.Empty<ArtistRef>(), Album: new AlbumRef("", "", ""),
        DurationMs: durationMs, IsExplicit: false, Image: null);

    static void Start(NowPlayingProjection p, string uri, long durationMs)
        => p.OnEvent(new PlaybackEvent(EvKind.Started, TrackFor(uri, durationMs), 0));

    [Fact]
    public void ATrackChangeToAnUnknownLength_ZeroesTheDuration()
    {
        using var p = New();
        Start(p, SongUri, 205_000);
        Assert.Equal(205_000, p.DurationMs);

        Start(p, StreamUri, 0);   // a live/unknown-length playable

        Assert.Equal(0, p.DurationMs);
    }

    [Fact]
    public void ASameUriRepublishWithNoLength_KeepsTheKnownDuration()
    {
        using var p = New();
        Start(p, SongUri, 205_000);

        // The same playable, re-published thin (a queue mutation, a cluster echo that carried no length).
        p.OnEvent(new PlaybackEvent(EvKind.TrackChanged, TrackFor(SongUri, 0), 0));

        Assert.Equal(205_000, p.DurationMs);
    }

    [Fact]
    public void ALiveBroadcast_PublishesNoDurationAtAll()
    {
        using var p = New();
        // The poisoned-restore shape: the SAME uri already carries a stale length, and only live-ness can answer it.
        Start(p, StreamUri, 205_000);
        Assert.Equal(205_000, p.DurationMs);

        p.SetLiveOverride(StreamUri, true);

        Assert.True(p.IsLive);
        Assert.Equal(0, p.DurationMs);
    }

    [Fact]
    public void ClearingLiveness_GivesTheRealLengthBack()
    {
        using var p = New();
        Start(p, SongUri, 205_000);
        p.SetLiveOverride(SongUri, true);
        Assert.Equal(0, p.DurationMs);

        p.SetLiveOverride(SongUri, false);

        Assert.Equal(205_000, p.DurationMs);
    }

    [Fact]
    public void ADurationOverrideStillOutranksTheFold()
    {
        using var p = New();
        Start(p, SongUri, 205_000);
        p.SetDurationOverride(SongUri, 90_000);   // a user-attached video is its own edit

        p.OnEvent(new PlaybackEvent(EvKind.TrackChanged, TrackFor(SongUri, 0), 0));

        Assert.Equal(90_000, p.DurationMs);
    }

    [Fact]
    public void TheLocalSnapshotFoldObeysTheSameRules()
    {
        using var p = New();
        var song = Row(1, SongUri, 205_000);
        var stream = Row(2, StreamUri, 0);

        p.ApplyLocalSnapshot(Snap(song), new PlaybackEvent(EvKind.Started, song.Track, 0));
        Assert.Equal(205_000, p.DurationMs);

        p.ApplyLocalSnapshot(Snap(stream), new PlaybackEvent(EvKind.TrackChanged, stream.Track, 0));
        Assert.Equal(0, p.DurationMs);

        // …and a pure queue republish for the SAME (unknown-length) playable does not invent one either.
        p.ApplyLocalSnapshot(Snap(stream));
        Assert.Equal(0, p.DurationMs);
    }

    // bug 9: a QueueChanged/OptionsChanged/VolumeChanged event folds NO play-state — but the snapshot's Current can
    // already have advanced to a NEW track while the OUTGOING track's Ended fold (is_playing=false) is the last thing
    // that touched it. That must not get echoed as the new track's own state — a track transition can never publish
    // "not playing" over a track that is starting.
    [Fact]
    public void QueueChangedMidTransition_DoesNotEchoStalePlayState()
    {
        using var p = New();
        var song = Row(1, SongUri, 205_000);
        var next = Row(2, StreamUri, 0);

        p.ApplyLocalSnapshot(Snap(song), new PlaybackEvent(EvKind.Started, song.Track, 0));
        Assert.True(p.IsPlaying);

        p.ApplyLocalSnapshot(Snap(song), new PlaybackEvent(EvKind.Ended, song.Track, 205_000));
        Assert.False(p.IsPlaying);   // correct FOR THE OUTGOING track

        // The next track's OWN Started/TrackChanged fold hasn't landed yet — only a queue mutation whose snapshot
        // already points Current at it (e.g. autoplay reshaping up-next the instant the new track becomes current).
        p.ApplyLocalSnapshot(Snap(next), new PlaybackEvent(EvKind.QueueChanged, next.Track, 0));

        Assert.Equal(StreamUri, p.CurrentTrack?.Uri);
        Assert.True(p.IsPlaying);   // must NOT still say "not playing" for a track that is starting
    }

    static QueueEntry Row(ulong id, string uri, long durationMs) => new(
        new QueueItemId(id), "i" + id, TrackFor(uri, durationMs), QueueBucket.NowPlaying, QueueProvider.Context, false);

    static QueueSnapshot Snap(QueueEntry current) => new(
        Revision: 1, ContextUri: current.Track.Uri, AutoplayContextUri: null, Current: current,
        History: ImmutableArray<QueueEntry>.Empty, UserQueue: ImmutableArray<QueueEntry>.Empty,
        Upcoming: ImmutableArray<QueueEntry>.Empty, Shuffle: false, Repeat: RepeatMode.Off,
        ClusterQueueRevision: "", ContextCursor: 0);
}
