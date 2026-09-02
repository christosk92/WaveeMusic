using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE STUCK SPINNER at launch. Session recovery seeds a paused session from the Connect cluster — and that cluster is
/// this machine's OWN last publish from a previous run, which can perfectly well carry <c>is_buffering = true</c> (the
/// previous session was mid-load when it went away). The cluster fold adopts the flag; nothing on the recovery path
/// then clears it, because the flag is normally retired by the local audio host's Playing/Ended edge and recovery
/// starts NO host — it publishes Paused and stops. The result was a player bar whose top edge swept an indeterminate
/// progress bar for the whole session over a track that was merely paused at its restored position.
///
/// The fix: a Paused/Ended/BecameInactive local snapshot clears the two transient flags with the play-state. All three
/// mean "nothing here is waiting on audio", so none of them may leave a buffering state standing.
/// </summary>
public class LaunchRecoveryBufferingTests
{
    static RemoteTrack Remote(string uri) =>
        new(uri, "Song", "Artist", "spotify:artist:a", "Album", "spotify:album:al", "https://img/x", 208_000);

    static Track Local(string uri) => new(
        Id: uri, Uri: uri, Title: "Song", Artists: Array.Empty<ArtistRef>(), Album: new AlbumRef("", "", ""),
        DurationMs: 208_000, IsExplicit: false, Image: null);

    /// <summary>A cluster naming US the (stale) active device, buffering, paused — the launch shape.</summary>
    static ClusterDelta StaleOwnCluster(string uri, bool buffering) =>
        new("us", true, Remote(uri), "spotify:album:al",
            IsPlaying: false, IsPaused: true, IsBuffering: buffering,
            PositionAsOfMs: 1785, TimestampMs: 0, ServerTimestampMs: 0, DurationMs: 208_000,
            Shuffle: false, Repeat: RepeatMode.Off,
            Devices: Array.Empty<ConnectDeviceRow>(), NextTracks: Array.Empty<RemoteTrack>());

    static QueueSnapshot Snap(Track track) => new(
        Revision: 1, ContextUri: "spotify:album:al", AutoplayContextUri: null,
        Current: new QueueEntry(QueueItemId.None, "now", track, QueueBucket.NowPlaying, QueueProvider.Context, false, "u-now"),
        History: ImmutableArray<QueueEntry>.Empty, UserQueue: ImmutableArray<QueueEntry>.Empty,
        Upcoming: ImmutableArray<QueueEntry>.Empty, Shuffle: false, Repeat: RepeatMode.Off,
        ClusterQueueRevision: "", ContextCursor: 0);

    [Fact]
    public void RecoveryPublishingPaused_RetiresABufferingFlagAdoptedFromTheCluster()
    {
        const string uri = "spotify:track:5odlY52u43F5BjByhxg7wg";
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());

        p.OnCluster(StaleOwnCluster(uri, buffering: true));
        Assert.True(p.IsBuffering);   // adopted from the wire — the fold is doing its job

        // What SessionRecovery does: seed the local session, publish Paused to the viewer only.
        p.ApplyLocalSnapshot(Snap(Local(uri)), new PlaybackEvent(EvKind.Paused, Local(uri), 1785));

        Assert.False(p.IsBuffering);   // …and the spinner is retired with it
        Assert.False(p.IsPlaying);
        Assert.Equal(1785, p.PositionMs);
    }

    [Fact]
    public void EndedAndBecameInactive_AlsoRetireIt()
    {
        const string uri = "spotify:track:x";
        foreach (var kind in new[] { EvKind.Ended, EvKind.BecameInactive })
        {
            using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
            p.OnCluster(StaleOwnCluster(uri, buffering: true));
            Assert.True(p.IsBuffering);

            p.ApplyLocalSnapshot(Snap(Local(uri)), new PlaybackEvent(kind, Local(uri), 0));

            Assert.False(p.IsBuffering);
        }
    }

    [Fact]
    public void ANonBufferingClusterIsUnaffected()
    {
        const string uri = "spotify:track:y";
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());

        p.OnCluster(StaleOwnCluster(uri, buffering: false));
        p.ApplyLocalSnapshot(Snap(Local(uri)), new PlaybackEvent(EvKind.Paused, Local(uri), 1785));

        Assert.False(p.IsBuffering);
        Assert.Equal(uri, p.CurrentTrack!.Uri);
    }

    // ── the SECOND stuck-spinner shape: a paused LOCAL restore (no stale cluster involved at all) ─────────────────────
    //
    // SessionRecovery's queue.recovery.snapshot path loads the current track PAUSED (LoadAndPlayCurrentAsync's
    // initiallyPaused: true — Play() is never called) purely so the player can show it sitting at its saved position.
    // FluentMediaAudioHost's own LoadFastStart/SupplyBodyAsync used to announce Prebuffering/Buffering unconditionally
    // while attaching the clear head / encrypted body — work the host does regardless of whether anyone asked to HEAR
    // it. With no Play() ever called, nothing retires the flag: the state-pump ticker that would normally clear it on
    // the next Playing/Ended edge only starts from Play()/auto-resume.
    //
    // The fix has two halves. FluentMediaAudioHost now withholds those signals while it has no play intent
    // (PlayIntentGate.ShouldAnnounceBuffering — not exercisable here, the host cannot be built headlessly), so in
    // production a signal shaped like this never reaches OnHostSignal in the first place. What IS exercisable at this
    // level is the belt-and-suspenders half: PlaybackController now calls the SAME ClearTransientBuffering() SwitchHost
    // already uses (PlaybackProjection.cs:528) both right after an initiallyPaused load and when SupplyBodyWhenReadyAsync
    // completes for a host with no play intent — so even a signal that DID slip through (the race the gate alone cannot
    // fully close) does not latch.

    [Fact]
    public void SnapshotRestore_PausedFold_ThenAStrayHostBufferingSignal_IsRetiredByClearTransientBuffering()
    {
        const string uri = "spotify:track:z";
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());

        // The restore: seed the local session, publish Paused — nobody has asked to hear this track yet.
        p.ApplyLocalSnapshot(Snap(Local(uri)), new PlaybackEvent(EvKind.Paused, Local(uri), 1785));
        Assert.False(p.IsBuffering);

        // A host-reported Buffering signal for the fast-start head/body attach of that same paused load (what
        // FluentMediaAudioHost used to emit unconditionally, and what the play-intent gate now withholds — this pins
        // the OTHER half: even adopted, it must not survive the controller's own no-play-intent clear).
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Buffering, 1785));
        Assert.True(p.IsBuffering);   // OnHostSignal itself has no play-intent concept — it adopts whatever it's told

        // PlaybackController.SupplyBodyWhenReadyAsync's belt-and-suspenders clear, for a host with no play intent.
        p.ClearTransientBuffering();

        Assert.False(p.IsBuffering);
        Assert.False(p.IsPlaying);
        Assert.Equal(1785, p.PositionMs);
    }

    [Fact]
    public void SnapshotRestore_AfterPlay_AGenuineBufferingSignalIsAllowedToShow()
    {
        const string uri = "spotify:track:z2";
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());

        p.ApplyLocalSnapshot(Snap(Local(uri)), new PlaybackEvent(EvKind.Paused, Local(uri), 1785));
        Assert.False(p.IsBuffering);

        // The user presses Play (PlaybackController.Play → the audio host's Resumed fold): there IS play intent now,
        // so a buffering signal that lands afterward is a REAL wait on audio and must reach the UI, not be swallowed
        // by the same clear that protects the paused-restore window.
        p.ApplyLocalSnapshot(Snap(Local(uri)), new PlaybackEvent(EvKind.Resumed, Local(uri), 1785));
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Buffering, 1785));

        Assert.True(p.IsBuffering);
    }
}
