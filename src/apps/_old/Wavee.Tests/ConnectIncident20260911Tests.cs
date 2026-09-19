using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using P = Wavee.Protocol.Player;

namespace Wavee.Tests;

// Replay fixtures for the six Connect incidents of 2026-09-11 (docs/plans/wavee/handoff-20260911-connect-detail.md §2
// timeline, §3 per-bug causes), transcribed into ClusterDelta sequences and driven through the SAME objects production
// uses (NowPlayingProjection + PlaybackController, PlaybackOwnership underneath, ClusterMapper for the wire boundary).
// Every test asserts the DESIGN's outcome (PlaybackOwnership.cs's F0-F6/P1-P4/A1-A3 fold table), not the historical bug.
//
// Real identities from the evidence (§0): our device id af11fca0…, the iPhone 756f53b6…. Server timestamps below are
// synthetic ms-offsets anchored at each incident's first decoded frame — the log gives wall-clock times only, never the
// wire's own server_timestamp_ms — but they are STRICTLY INCREASING in the exact order the frames arrived, which is the
// only thing PlaybackOwnership.OnCluster's F0 guard (and the ordering the rest of the fold depends on) cares about.
public class ConnectIncident20260911Tests
{
    const string Us = "af11fca0";      // our device id (evidence §0)
    const string Phone = "756f53b6";   // the iPhone (evidence §0)

    static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    sealed class RecordingAudioHost : IAudioHost
    {
        public readonly List<string> Calls = new();
        readonly SimpleSubject<AudioHostSignal> _sig = new();
        public IObservable<AudioHostSignal> Signals => _sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public bool ClockValid { get; private set; }
        public void Load(in AudioStreamHandle s) { Calls.Add("load:" + s.TrackUri); ClockValid = true; }
        public void LoadFastStart(in AudioFastStart s) { Calls.Add("faststart:" + s.TrackUri); ClockValid = true; }
        public void SupplyBody(in AudioStreamHandle s) { Calls.Add("body:" + s.TrackUri); }
        public void Play() { IsPlaying = true; Calls.Add("play"); }
        public void Pause() { IsPlaying = false; Calls.Add("pause"); }
        public void Stop() { IsPlaying = false; ClockValid = false; Calls.Add("stop"); }
        public void Seek(long ms, SeekMode mode) { PositionMs = ms; Calls.Add("seek:" + ms); }
        public void SetVolume(double v) { Calls.Add("vol"); }
        public void Emit(AudioHostSignal s) => _sig.OnNext(s);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class RecordingOutbound : IOutboundControl
    {
        public readonly List<(string Target, string Json)> Sent = new();
        public readonly List<(string Target, int Volume)> Volumes = new();
        public readonly List<(string From, string Target, bool HostingVideo)> Transfers = new();
        public bool TransferOk { get; set; } = true;
        public Task<OutboundResult> SendAsync(string targetDeviceId, string commandJson, System.Threading.CancellationToken ct = default)
        { Sent.Add((targetDeviceId, commandJson)); return Task.FromResult(new OutboundResult(true, "ack-test", 200)); }
        public Task<OutboundResult> SetVolumeAsync(string targetDeviceId, int volume0_65535, System.Threading.CancellationToken ct = default)
        { Volumes.Add((targetDeviceId, volume0_65535)); return Task.FromResult(new OutboundResult(true, "ack-test", 200)); }
        public Task<OutboundResult> TransferAsync(string fromDeviceId, string targetDeviceId, System.Threading.CancellationToken ct = default, bool hostingVideo = false)
        {
            Transfers.Add((fromDeviceId, targetDeviceId, hostingVideo));
            return Task.FromResult(new OutboundResult(TransferOk, TransferOk ? "ack-test" : null, TransferOk ? 200 : 500));
        }
    }

    sealed class RecordingProjection : IPlaybackProjection
    {
        public readonly List<PlaybackEvent> Events = new();
        public void OnEvent(in PlaybackEvent e) => Events.Add(e);
        public int Count(EvKind kind) => Events.Count(e => e.Kind == kind);
    }

    static IContextResolver Ctx(params string[] uris) => new FakeContextResolver(uris);

    static PlaybackController Make(out RecordingAudioHost host, out NowPlayingProjection proj, out RecordingOutbound outbound,
        IContextResolver? ctx = null, IReadOnlyList<IPlaybackProjection>? extra = null)
    {
        host = new RecordingAudioHost();
        proj = new NowPlayingProjection(Us, NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        outbound = new RecordingOutbound();
        return new PlaybackController(host, new StubTrackResolver(), proj, ctx ?? EmptyContextResolver.Instance, Us,
            outbound, extra);
    }

    static void Dispatch(PlaybackController c, string commandJson)
    {
        ConnectCommand.TryParse(new WireRequest("k", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes(commandJson), NoHeaders), out var cmd);
        c.HandleRemoteCommand(cmd);
    }

    static RemoteTrack Remote(string uri, string title, string artistName, string artistUri, long dur) =>
        new(uri, title, artistName, artistUri, "", "", null, dur);

    /// <summary>A cluster naming <paramref name="active"/> with a track — the common frame shape.</summary>
    static ClusterDelta Cluster(string active, RemoteTrack track, long pos, bool playing, long serverTsMs,
        int updateReason = 0, ClusterOrigin origin = ClusterOrigin.Push, uint putMsgId = 0) =>
        new(active, true, track, "spotify:playlist:ctx", playing, !playing, false, pos, 0, serverTsMs,
            track.DurationMs, false, RepeatMode.Off, Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>(),
            Origin: origin, PutMsgId: putMsgId, UpdateReason: updateReason);

    /// <summary>A cluster with an EMPTY active device id (NEW_CONNECTION / DEVICES_DISAPPEARED) but a player_state
    /// snapshot still describing whoever last held it — exactly how the dealer sends these frames.</summary>
    static ClusterDelta EmptyActive(long serverTsMs, int updateReason, RemoteTrack? track = null, long pos = 0, bool playing = false) =>
        new("", track is not null, track ?? default, "spotify:playlist:ctx", playing, !playing, false, pos, 0, serverTsMs,
            track?.DurationMs ?? 0, false, RepeatMode.Off, Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>(),
            UpdateReason: updateReason);

    /// <summary>The shape a restored/seeded LOCAL session takes (ApplyLocalSnapshot's own input) — matches
    /// ConnectProjectionTests' identical helper.</summary>
    static QueueSnapshot Snap(Track current) => new(
        Revision: 1, ContextUri: "spotify:playlist:local", AutoplayContextUri: null,
        Current: new QueueEntry(QueueItemId.None, "now", current, QueueBucket.NowPlaying, QueueProvider.Context, false, "u-now"),
        History: ImmutableArray<QueueEntry>.Empty, UserQueue: ImmutableArray<QueueEntry>.Empty,
        Upcoming: ImmutableArray<QueueEntry>.Empty, Shuffle: false, Repeat: RepeatMode.Off,
        ClusterQueueRevision: "", ContextCursor: 0);

    // ── Incident 1 — the flip loop, 14:58:02.8–14:58:43.2 (handoff §2, §3.1) ───────────────────────────────────────────
    // The phone owns "Lost on You" (40x5K8) after an earlier takeover; NEW_CONNECTION/DEVICES_DISAPPEARED frames keep
    // naming an EMPTY active device while the phone flaps in and out of the roster (`devices={iPhone,Wavee}` then
    // `{Wavee only}`, `active` itself staying "" per §2's own note on the 14:58:33/35/43 trio). The old code read an
    // empty active id as "ownership regained" and reloaded our stale session at the PHONE's playhead (log: "seek
    // deferred ms=1788" / "ms=2578"). Under F5, an empty frame after Foreign is Nobody(FromForeign) — never a reload,
    // and the display keeps the departed device's snapshot, paused.
    [Fact]
    public async Task Incident1_FlipLoop_EmptyActiveFrames_NeverReloadOrSeek_DisplayKeepsPhonesSnapshotPaused()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out _, extra: new[] { events }, ctx: Ctx("spotify:track:40x5K8"));
        await c.PlayAsync("spotify:playlist:p");   // we were playing "Lost on You" locally, immediately before the loop

        var phoneTrack = Remote("spotify:track:40x5K8", "Lost on You", "LP", "spotify:artist:lp", 268_000);
        Assert.True(proj.OnCluster(Cluster(Phone, phoneTrack, pos: 1_137, playing: true, serverTsMs: 0)));   // the 14:57:43.0 takeover (not itself in scope)
        Assert.Equal(OwnerKind.Foreign, proj.Ownership.Current.Kind);
        int baseline = events.Count(EvKind.BecameInactive);   // the pre-window takeover — not part of the flip loop itself
        host.Calls.Clear();

        // 14:58:02.8 — NEW_CONNECTION, active="", devices={iPhone,Wavee}, player_state = the phone's, paused@1788.
        Assert.True(proj.OnCluster(EmptyActive(19_800, updateReason: 6, phoneTrack, pos: 1_788)));
        Assert.Equal(NobodyCause.FromForeign, proj.Ownership.Current.Cause);   // F5
        // 14:58:03.4 — reason=2, active=PHONE again (the flap back in) → L "stray stop" in the old log.
        Assert.True(proj.OnCluster(Cluster(Phone, phoneTrack, pos: 1_788, playing: true, serverTsMs: 20_400, updateReason: 2)));
        Assert.Equal(OwnerKind.Foreign, proj.Ownership.Current.Kind);
        // 14:58:14.55 — reason=2, active=PHONE, pos=2578, paused.
        Assert.True(proj.OnCluster(Cluster(Phone, phoneTrack, pos: 2_578, playing: false, serverTsMs: 31_550, updateReason: 2)));
        // 14:58:14.65 — DEVICES_DISAPPEARED, active="", devices={Wavee only} — player_state still the phone's.
        Assert.True(proj.OnCluster(EmptyActive(31_650, updateReason: 1, phoneTrack, pos: 2_578)));
        Assert.Equal(NobodyCause.FromForeign, proj.Ownership.Current.Cause);
        // 14:58:33 / :35 / :43 — the phone flaps in the DEVICE ROSTER while `active` itself stays empty on the wire
        // (§2: "the phone flaps in and out, active stays \"\""); none of this churn may touch the host.
        Assert.True(proj.OnCluster(EmptyActive(50_000, updateReason: 6, phoneTrack, pos: 2_578)));
        Assert.True(proj.OnCluster(EmptyActive(52_000, updateReason: 2, phoneTrack, pos: 2_578)));
        Assert.True(proj.OnCluster(EmptyActive(60_000, updateReason: 1, phoneTrack, pos: 2_578)));   // 14:58:43.2 window close

        Assert.DoesNotContain(host.Calls, x => x.StartsWith("load:", StringComparison.Ordinal)
                                            || x.StartsWith("faststart:", StringComparison.Ordinal)
                                            || x.StartsWith("seek:", StringComparison.Ordinal)
                                            || x == "play");
        Assert.Equal(baseline, events.Count(EvKind.BecameInactive));   // no ADDITIONAL loss during the loop — we never held it
        Assert.False(c.OwnsPlaybackOnWire);
        Assert.Equal(NobodyCause.FromForeign, proj.Ownership.Current.Cause);
        Assert.Equal("spotify:track:40x5K8", proj.CurrentTrack!.Uri);   // the phone's snapshot, never our stale session
        Assert.False(proj.IsPlaying);
        Assert.Equal(2_578, proj.PositionMs);                            // frozen where the phone left it
    }

    // ── Incident 2 — the launch "SLOT STEAL", 15:21:31.9–15:21:41.9 (handoff §3.5) ─────────────────────────────────────
    // A NEW_CONNECTION echo of our own pre-restart state (active="", ChangedDevices=[us]) arrives; a local session is
    // seeded PAUSED from the persisted snapshot (queue.recovery.seeded, positionMs=72777) WITHOUT ever claiming; a
    // stray push then names US with no claim in this process (F2 StaleSelf); the phone's inbound pause is applied
    // locally without claiming either (pause is not a ClaimCause). Only the USER'S OWN action legitimately claims —
    // contrasted with the historical bug, where the video badge (RecomputeHasVideo → PublishStateChanged) published a
    // literal is_active=true and stole the slot from the phone (put-state msgId=2, 15:21:32.11) with nobody asking.
    // Once legitimately Us/Adopted, the server's usual takeover pair (an empty transitional frame, then the phone) is
    // A3-then-A2: exactly one BecameInactive, the host stopped.
    [Fact]
    public async Task Incident2_Launch_RecoverySeedNeverClaims_StaleSelfNoReload_ThenLegitimateClaim_TakeoverPairOnce()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out _, extra: new[] { events });

        var ourEcho = Remote("spotify:track:40x5K8", "Lost on You", "LP", "spotify:artist:lp", 268_000);

        // 15:21:31.94 — NEW_CONNECTION, active="", ChangedDevices=[us]: an echo of OUR OWN pre-restart state.
        Assert.True(proj.OnCluster(EmptyActive(0, updateReason: 6, ourEcho, pos: 22_962, playing: true)));
        Assert.Equal(OwnerKind.Nobody, proj.Ownership.Current.Kind);
        Assert.Equal(NobodyCause.Launch, proj.Ownership.Current.Cause);

        // queue.recovery.seeded (paused) positionMs=72777 — a LOCAL session seeded paused, with NO claim (contract:
        // recovery never claims). Modelled directly via ApplyLocalSnapshot — the "restored local session" shape
        // ConnectProjectionTests uses elsewhere; the launch recovery PIPELINE itself is already covered by
        // ConnectControllerTests' SessionRecovery_* suite and is not re-tested here.
        var recovered = new Track("40x5K8", "spotify:track:40x5K8", "Lost on You",
            new[] { new ArtistRef("lp", "spotify:artist:lp", "LP") }, new AlbumRef("", "", ""), 268_000, false, null);
        proj.ApplyLocalSnapshot(Snap(recovered), new PlaybackEvent(EvKind.Paused, recovered, 72_777));
        Assert.Equal(OwnerKind.Nobody, proj.Ownership.Current.Kind);   // still no claim
        host.Calls.Clear();

        // 15:21:35.13 — a push naming US with no claim in this process (F2 StaleSelf): must never reload/seek/play.
        Assert.True(proj.OnCluster(Cluster(Us, ourEcho, pos: 72_777, playing: false, serverTsMs: 3_190, updateReason: 2)));
        Assert.Equal(NobodyCause.StaleSelf, proj.Ownership.Current.Cause);
        Assert.Empty(host.Calls);
        Assert.Equal("spotify:track:40x5K8", proj.CurrentTrack!.Uri);   // the LOCAL recovered row, untouched by the echo
        Assert.Equal(72_777, proj.PositionMs);
        Assert.False(PlaybackOwnership.IsActiveOnWire(proj.Ownership.Current));   // no put-state would carry is_active=true

        // 15:21:36.08 — the phone's inbound pause, addressed to us. Whether it lands on a session (launch recovery seeds
        // one from the first fold) or on none, pause is not a ClaimCause — pause/seek/options/volume/queue never start
        // the host — so handling it must not claim and must not make a sound.
        ConnectCommand.TryParse(new WireRequest("k", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes("{\"sent_by_device_id\":\"756f53b6\",\"command\":{\"endpoint\":\"pause\"}}"), NoHeaders),
            out var pauseCmd);
        await c.HandleRemoteCommandAsync(pauseCmd);
        Assert.NotEqual(OwnerKind.Us, proj.Ownership.Current.Kind);
        Assert.False(PlaybackOwnership.IsActiveOnWire(proj.Ownership.Current));
        Assert.DoesNotContain(host.Calls, x => x == "play" || x.StartsWith("load:", StringComparison.Ordinal));

        // Only the user's own action legitimately claims. Resume ghost-resumes the DISPLAYED row — the recovered local
        // session, since F2/ShowsLocalNowPlaying never let the stale echo overwrite it — at ITS OWN playhead, never a
        // foreign position.
        host.Calls.Clear();
        await c.ResumeAsync();
        Assert.True(c.OwnsPlaybackOnWire);
        Assert.Contains("load:spotify:track:40x5K8", host.Calls);
        // At THIS device's own position — the recovery seed read it from our pre-restart echo (22962); the persisted
        // snapshot said 72777. Either is ours; what must never happen is a foreign playhead on our track.
        Assert.Contains(host.Calls, x => x is "seek:22962" or "seek:72777");
        Assert.Contains("play", host.Calls);

        // A server heartbeat confirming the claim (A1 — Adopted, fence set), so the takeover pair below is genuinely
        // A3-then-A2 — the same technique ConnectControllerTests' Takeover_WithEmptyTransitionalFrame… test uses.
        Assert.True(proj.OnCluster(Cluster(Us, ourEcho, pos: 72_777, playing: false, serverTsMs: 5_000, updateReason: 2)));
        Assert.Equal(ClaimPhase.Adopted, proj.Ownership.Current.Claim);

        // 15:21:38.428 → 15:21:38.943 — the server's usual takeover pair: an empty transitional frame (still the
        // phone's OWN row, buffering@0), then the phone itself.
        Assert.True(proj.OnCluster(EmptyActive(6_488, updateReason: 2, ourEcho, pos: 0, playing: false)));
        Assert.Equal(OwnerKind.Us, proj.Ownership.Current.Kind);          // A3 alone changes nothing
        Assert.Equal(0, events.Count(EvKind.BecameInactive));

        Assert.True(proj.OnCluster(Cluster(Phone, ourEcho, pos: 0, playing: true, serverTsMs: 7_003, updateReason: 2)));
        Assert.Equal(1, events.Count(EvKind.BecameInactive));             // exactly once
        Assert.Contains("stop", host.Calls);
        Assert.False(c.OwnsPlaybackOnWire);
    }

    // ── Incident 3 — transfer-to-self while foreign (handoff §3.3; 15:22:54–15:24:05) ─────────────────────────────────
    // Picking "This computer" while the phone plays 2t4RCW ('The First Time') posts the PULL (transfer phone→us) and
    // never resumes our stale 40x5K8 session; the pull 400s (self/self), so the fallback claims and ghost-resumes the
    // CLUSTER's own track at its extrapolated 6448 ms. The claim's own put-state response then still names the phone
    // (rejected, P3): the host stops at once, the owner is Foreign(phone), and the bar's play button forwards to it —
    // never local audio under a bar that says "Playing on iPhone".
    [Fact]
    public async Task Incident3_TransferToSelf_PullsFirst_FallbackClaimRejected_StopsHostAndForwardsPlay()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out var outbound, extra: new[] { events }, ctx: Ctx("spotify:track:40x5K8"));
        await c.PlayAsync("spotify:playlist:p");   // a stale local session (40x5K8) is lying around

        var firstTime = Remote("spotify:track:2t4RCW", "The First Time", "", "spotify:artist:7AaGb", 217_000);
        Assert.True(proj.OnCluster(Cluster(Phone, firstTime, pos: 6_448, playing: true, serverTsMs: 100)));   // 15:22:59.52's read
        Assert.Equal(OwnerKind.Foreign, proj.Ownership.Current.Kind);
        int baseline = events.Count(EvKind.BecameInactive);   // the takeover that establishes "while foreign" (not the rejection)
        host.Calls.Clear();
        outbound.TransferOk = false;                 // the pull 400s (self/self — wavee-playback-arbitration-rules.md)
        proj.Ownership.AttachAcknowledger();          // production always has a publisher attached; the claim below waits for a verdict

        await c.TransferToAsync(Us);

        Assert.Equal((Phone, Us, false), Assert.Single(outbound.Transfers));    // the pull was tried FIRST, phone → us
        Assert.DoesNotContain("load:spotify:track:40x5K8", host.Calls);         // never our stale session
        Assert.Contains("load:spotify:track:2t4RCW", host.Calls);               // the fallback: the CLUSTER's own track…
        Assert.Contains("seek:6448", host.Calls);                               // …at ITS OWN extrapolated position
        Assert.Contains("play", host.Calls);
        Assert.True(c.OwnsPlaybackOnWire);
        Assert.Equal(ClaimPhase.Protected, proj.Ownership.Current.Claim);

        // 15:23:00.02 — the claim's own put-state response still names the phone: REJECTED (P3).
        proj.Ownership.OnPutSent(7, isActive: true);
        Assert.True(proj.OnCluster(Cluster(Phone, firstTime, pos: 6_448, playing: true, serverTsMs: 150,
            origin: ClusterOrigin.PutResponse, putMsgId: 7)));

        Assert.Contains("stop", host.Calls);
        Assert.False(host.IsPlaying);
        Assert.Equal(OwnerKind.Foreign, proj.Ownership.Current.Kind);
        Assert.Equal(Phone, proj.Ownership.Current.DeviceId);
        Assert.False(c.OwnsPlaybackOnWire);
        Assert.Equal(baseline + 1, events.Count(EvKind.BecameInactive));   // exactly one MORE loss — the rejected claim
        host.Calls.Clear();

        // 15:23:50.39 — the bar's play button, routed by the SAME owner authority, forwards to the phone: no local
        // audio, no "Playing on iPhone" split.
        await c.ResumeAsync();
        Assert.Contains(outbound.Sent, s => s.Target == Phone && s.Json.Contains("\"endpoint\":\"resume\""));
        Assert.DoesNotContain("play", host.Calls);
    }

    // ── Incident 4 — the PLAY glyph over audible/muted playback (handoff §3.4) ─────────────────────────────────────────
    // Under Us, the last-PUBLISHED (IsPlaying, ...) tuple is committed by EVERY writer in FireChanges now — not only
    // OnHostSignal — so a local writer publishing "not playing" (the phone's inbound pause, applied locally) is
    // correctly OVERTURNED by the very next disagreeing host tick, instead of a stale memory swallowing it forever.
    // Under Foreign (the real takeover that follows, to 'The First Time'), local host ticks — still in flight before
    // Stop() physically lands — move nothing at all: not "no change", structurally dropped.
    [Fact]
    public async Task Incident4_PlayGlyph_CommittedAcrossWriters_ThenForeignTakeover_HostTicksSwallowed()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out _, extra: new[] { events }, ctx: Ctx("spotify:track:40x5K8"));
        await c.PlayAsync("spotify:playlist:p");   // Us, playing "Lost on You" (40x5K8) locally
        Assert.True(proj.IsPlaying);

        // 15:21:36.08 — the phone's inbound pause (sender=iPhone), applied locally; this is what COMMITS
        // "last published = not playing" — before the fix that memory was written only by OnHostSignal.
        Dispatch(c, "{\"sent_by_device_id\":\"756f53b6\",\"command\":{\"endpoint\":\"pause\"}}");
        await Task.Delay(20);
        Assert.False(proj.IsPlaying);

        host.Emit(new AudioHostSignal(AudioHostSignalKind.PositionTick, 1_000));   // the host itself never actually silenced
        await Task.Delay(20);
        Assert.True(proj.IsPlaying);   // corrected — the committed-tuple fix (would have stuck False forever before it)

        // 15:21:38.94 — the real takeover: the phone becomes active playing 'The First Time' (2t4RCW).
        Assert.True(proj.OnCluster(Cluster(Phone, Remote("spotify:track:2t4RCW", "The First Time", "", "spotify:artist:7AaGb", 217_000),
            pos: 0, playing: true, serverTsMs: 100)));
        Assert.Equal(OwnerKind.Foreign, proj.Ownership.Current.Kind);
        Assert.Contains("stop", host.Calls);
        Assert.True(proj.IsPlaying);                       // the PHONE's state now — it is playing
        long phonePos = proj.PositionMs;

        // Our host's stray ticks — still in flight before Stop() physically silences the decoder — must move nothing.
        host.Emit(new AudioHostSignal(AudioHostSignalKind.PositionTick, 5_000));
        host.Emit(new AudioHostSignal(AudioHostSignalKind.Paused, 5_200));
        await Task.Delay(20);

        Assert.True(proj.IsPlaying);                       // our host's pause is not the phone's
        Assert.Equal(phonePos, proj.PositionMs);           // nor is its clock
        Assert.Equal("spotify:track:2t4RCW", proj.CurrentTrack!.Uri);
        Assert.Equal(1, events.Count(EvKind.BecameInactive));
    }

    // ── Incident 5 — the video-badge republish while not Us (handoff §3.5; 15:21:32.08) ────────────────────────────────
    // RecomputeHasVideo → PublishStateChanged used to publish a literal is_active=true over a recovery seed that never
    // claimed (the "SLOT STEAL", put-state msgId=2 @15:21:32.11). The fix: PublishStateChanged asks the ONE ownership
    // authority first and publishes NOTHING while it says not-Us — never a literal true, not even a corrected false.
    [Fact]
    public async Task Incident5_VideoBadgeRepublish_WhileNotUs_PublishesNothing_NeverIsActiveTrue()
    {
        var transport = new StubTransport();
        var proj = new NowPlayingProjection(Us, NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 1_000);
        var connId = new SimpleSubject<string?>(null);
        string? currentConnId = null;
        using var publisher = new DeviceStatePublisher(transport, Us, proj, proj.Ownership, connId, () => currentConnId,
            (reason, snap, mid, active) => Encoding.UTF8.GetBytes(reason + "|" + active), onCluster: null, clock: () => 1_000);

        // 15:21:31.94 — the recovery seed: projection only, no claim (Ownership stays Nobody(Launch)).
        proj.OnEvent(new PlaybackEvent(EvKind.Started,
            new Track("40x5K8", "spotify:track:40x5K8", "Lost on You",
                new[] { new ArtistRef("lp", "spotify:artist:lp", "LP") }, new AlbumRef("", "", ""), 268_000, false, null),
            72_777));

        currentConnId = "c1"; connId.OnNext("c1");   // the NewConnection announce
        await Task.Delay(20);
        Assert.Equal(1, transport.PublishCount);
        Assert.StartsWith("NewConnection|False", Encoding.UTF8.GetString(transport.LastPublishBody!));

        publisher.PublishStateChanged();             // 15:21:32.08 — the video badge lands
        await Task.Delay(20);
        Assert.Equal(1, transport.PublishCount);      // NOTHING published — never the slot-stealing is_active=true

        // 15:21:38.94 — the phone genuinely takes over; still not Us. A later badge (another video association
        // landing on the mirrored row) must stay just as silent.
        Assert.True(proj.OnCluster(new ClusterDelta(Phone, true,
            new RemoteTrack("spotify:track:40x5K8", "Lost on You", "LP", "spotify:artist:lp", "", "", null, 268_000),
            "spotify:playlist:ctx", true, false, false, 0, 0, 200, 268_000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>())));
        Assert.Equal(OwnerKind.Foreign, proj.Ownership.Current.Kind);

        publisher.PublishStateChanged();
        await Task.Delay(20);
        Assert.Equal(1, transport.PublishCount);      // still nothing
    }

    // ── Incident 6 — "title missing artist_name" (handoff §3.2; 15:21:40.4) ────────────────────────────────────────────
    // The phone's own frame for 2t4RCW carries a title and an artist_uri but NO artist_name. The "LP / LP" /
    // "Damiano David / Damiano David" reports were the bar's marquee reconciliation (unkeyed title/artist lines reused
    // when "Playing on …" was inserted — #139/B3), never this data: every published row kept the correct wire title.
    // This pins the DATA side of that finding through the REAL wire boundary (ClusterMapper.Map) and the pure identity
    // tripwire (NowPlayingIdentity.Suspicion) together.
    [Fact]
    public void Incident6_TitleMissingArtistName_KeepsWireTitle_NoFalseRepeatSuspicion()
    {
        var track = new P.ProvidedTrack { Uri = "spotify:track:2t4RCW", ArtistUri = "spotify:artist:7AaGb" };
        track.Metadata["title"] = "The First Time";
        var cluster = new P.Cluster
        {
            ActiveDeviceId = Phone,
            ServerTimestampMs = 100,
            PlayerState = new P.PlayerState { Track = track, Duration = 217_000 },
        };
        var delta = ClusterMapper.Map(cluster, Us);

        var proj = new NowPlayingProjection(Us, NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        Assert.True(proj.OnCluster(delta));

        var mapped = proj.CurrentTrack!;
        Assert.Equal("The First Time", mapped.Title);            // the wire title, verbatim — never blanked
        var artist = Assert.Single(mapped.Artists);
        Assert.Equal("spotify:artist:7AaGb", artist.Uri);
        Assert.Equal("", artist.Name);

        // NOTE for the orchestrator: the design brief phrased this as "Suspicion flags nothing for it," but
        // NowPlayingIdentity.Suspicion (Backend/NowPlayingIdentity.cs) DOES flag this exact shape — ArtistsUnnamed
        // exists specifically for "a cluster row carrying artist_uri but no artist_name, not yet enriched" per its own
        // doc comment. What matters (and is asserted below) is that it is NEVER the severe "LP / LP" shape
        // (TitleEqualsArtist — an unnamed artist can never equal a real title) and never touches the title itself.
        var suspicion = NowPlayingIdentity.Suspicion(mapped);
        Assert.NotEqual(IdentitySuspicion.TitleEqualsArtist, suspicion);
        Assert.NotEqual(IdentitySuspicion.TitleEmpty, suspicion);
        Assert.NotEqual(IdentitySuspicion.TitleIsUri, suspicion);
        Assert.Equal(IdentitySuspicion.ArtistsUnnamed, suspicion);
    }
}
