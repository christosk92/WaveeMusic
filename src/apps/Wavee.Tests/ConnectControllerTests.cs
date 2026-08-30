using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Protocol.Transfer;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// Stage E — command arbitration: the routing spine (local iff nobody/we active, else forward), ghost resume, per-verb
// routing, "another device active stops local", transfer self/away, and inbound-always-local. See
// docs/plans/wavee-playback-arbitration-rules.md.
public class ConnectControllerTests
{
    static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    static IContextResolver Ctx(params string[] uris) => new FakeContextResolver(uris);

    sealed class RecordingAudioHost : IAudioHost, IAudioOutputDeviceControl
    {
        public readonly List<string> Calls = new();
        readonly SimpleSubject<AudioHostSignal> _sig = new();
        public IObservable<AudioHostSignal> Signals => _sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        // Stateful, unlike the other fakes: teardown tests (BecameInactive/DeactivateIfActiveOwner) need a host whose
        // clock actually goes stale on Stop() and recovers on the next Load, so they exercise the ClockValid fallback
        // path (PlaybackController.PublishPositionMs) rather than trivially always reading true. Starts false, like
        // the real FluentMediaAudioHost: a never-opened host has no honest clock either.
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

        // IAudioOutputDeviceControl (Phase A/B)
        public event Action<OutputDeviceNotice>? OutputDeviceNotice;
        public event Action<double, bool>? ExternalVolumeChanged;
        public void SetOutputDevice(string? deviceId) { Calls.Add("setoutput:" + (deviceId ?? "(default)")); }
        public void SetOutputMuted(bool muted) { Calls.Add("mute:" + muted); }
        public void RaiseDeviceNotice(OutputDeviceNotice n) => OutputDeviceNotice?.Invoke(n);
        public void RaiseExternalVolume(double v, bool muted) => ExternalVolumeChanged?.Invoke(v, muted);
    }

    sealed class FakeDeviceMonitor : Wavee.SpotifyLive.Audio.IAudioDeviceMonitor
    {
        public event Action<Wavee.SpotifyLive.Audio.AudioDeviceEvent>? Changed;
        public IReadOnlyList<Wavee.SpotifyLive.Audio.AudioEndpointInfo> EnumerateRenderEndpoints() =>
            System.Array.Empty<Wavee.SpotifyLive.Audio.AudioEndpointInfo>();
        public string? GetDefaultRenderId() => null;
        public string? GetFriendlyName(string deviceId) => null;
        public void Dispose() { }
    }

    sealed class RecordingOutbound : IOutboundControl
    {
        public readonly List<(string Target, string Json)> Sent = new();
        public readonly List<(string Target, int Volume)> Volumes = new();
        public readonly List<(string From, string Target, bool HostingVideo)> Transfers = new();
        public bool TransferOk { get; set; } = true;
        public string? LastTarget => Sent.Count > 0 ? Sent[^1].Target : null;
        public string? LastJson => Sent.Count > 0 ? Sent[^1].Json : null;
        public int? LastVolume => Volumes.Count > 0 ? Volumes[^1].Volume : null;
        public Task<OutboundResult> SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default)
        { Sent.Add((targetDeviceId, commandJson)); return Task.FromResult(new OutboundResult(true, "ack-test", 200)); }
        public Task<OutboundResult> SetVolumeAsync(string targetDeviceId, int volume0_65535, CancellationToken ct = default)
        { Volumes.Add((targetDeviceId, volume0_65535)); return Task.FromResult(new OutboundResult(true, "ack-test", 200)); }
        public Task<OutboundResult> TransferAsync(string fromDeviceId, string targetDeviceId, CancellationToken ct = default, bool hostingVideo = false)
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

    sealed class RecordingAttributionProjection : IPlaybackProjection, IConnectCommandAttributionSink
    {
        public ConnectCommandAttribution Last;
        public void OnEvent(in PlaybackEvent e) { }
        public void NoteCommand(in ConnectCommandAttribution attribution) => Last = attribution;
    }

    static ClusterDelta Cluster(string active, RemoteTrack? track = null, long pos = 0, bool playing = false) =>
        new(active, track is not null, track ?? default, "spotify:playlist:ctx",
            playing, !playing, false, pos, 0, 0, track?.DurationMs ?? 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    static RemoteTrack Remote(string uri, long dur = 200000) => new(uri, "G", "A", "spotify:artist:a", "Al", "spotify:album:al", null, dur);

    PlaybackController Make(out RecordingAudioHost host, out NowPlayingProjection proj, out RecordingOutbound outbound,
        IContextResolver? ctx = null, Func<long>? clock = null, IReadOnlyList<IPlaybackProjection>? extra = null,
        ITransferStateDecoder? transferDecoder = null)
    {
        host = new RecordingAudioHost();
        proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), clock ?? (() => 0));
        outbound = new RecordingOutbound();
        return new PlaybackController(host, new StubTrackResolver(), proj,
            ctx ?? Ctx("spotify:track:a", "spotify:track:b"), "us", outbound, extra,
            transferDecoder: transferDecoder);
    }

    [Fact]
    public async Task NoActiveDevice_Play_RoutesLocal()
    {
        using var c = Make(out var host, out _, out var outbound);
        await c.PlayAsync("spotify:playlist:p");
        Assert.Contains("load:spotify:track:a", host.Calls);
        Assert.Contains("play", host.Calls);
        Assert.Empty(outbound.Sent);
    }

    [Fact]
    public async Task InboundCommand_CapturesSenderMessageAndCommandIds_ForPutState()
    {
        var attribution = new RecordingAttributionProjection();
        using var controller = Make(out _, out _, out _, extra: [attribution]);
        var command = new ConnectCommand(
            ConnectCmd.Pause, "pause", "k", 604162001, "controller-device",
            0, false, [], CommandId: "command-intent-id");

        await controller.HandleRemoteCommandAsync(command);

        Assert.Equal("controller-device", attribution.Last.SenderDeviceId);
        Assert.Equal(604162001u, attribution.Last.MessageId);
        Assert.Equal("command-intent-id", attribution.Last.CommandId);
    }

    [Fact]
    public async Task InboundPauseThenResume_AppliesInDealerOrder_ToLocalHost()
    {
        using var controller = Make(out var host, out _, out _);
        await controller.PlayAsync("spotify:playlist:p");
        host.Calls.Clear();

        var pause = new ConnectCommand(
            ConnectCmd.Pause, "pause", "pause-key", 604162002, "spotify-controller",
            0, false, [], CommandId: "pause-command");
        var resume = new ConnectCommand(
            ConnectCmd.Resume, "resume", "resume-key", 604162003, "spotify-controller",
            0, false, [], CommandId: "resume-command");

        Assert.Equal(ConnectCommandOutcome.Applied, await controller.HandleRemoteCommandAsync(pause));
        Assert.Equal(ConnectCommandOutcome.Applied, await controller.HandleRemoteCommandAsync(resume));
        Assert.Equal(["pause", "play"], host.Calls);
    }

    [Fact]
    public async Task PlayTrack_WithKnownTrack_PublishesMetadataImmediately()
    {
        using var c = Make(out var host, out var proj, out var outbound, ctx: EmptyContextResolver.Instance);
        var track = new Track("known", "spotify:track:known", "Known Title",
            [new ArtistRef("artist", "spotify:artist:artist", "Known Artist")],
            new AlbumRef("album", "spotify:album:album", "Known Album"), 123000, false,
            new Image("https://i.scdn.co/image/known", 300, 300));

        await c.PlayTrackAsync(track);

        Assert.Contains("load:spotify:track:known", host.Calls);
        Assert.Equal("Known Title", proj.CurrentTrack?.Title);
        Assert.Equal("Known Artist", proj.CurrentTrack?.Artists[0].Name);
        Assert.Equal("Known Album", proj.CurrentTrack?.Album.Name);
        Assert.Equal("https://i.scdn.co/image/known", proj.CurrentTrack?.Image?.Url);
        Assert.Empty(outbound.Sent);
    }

    [Fact]
    public async Task PlayTrack_UriOnly_HydratesBeforePublishing_NotSyntheticUriTitle()
    {
        using var c = Make(out var host, out var proj, out _, ctx: Ctx("spotify:track:ignored"));

        await c.PlayTrackAsync("spotify:track:clicked");

        Assert.Contains("load:spotify:track:clicked", host.Calls);
        Assert.Equal("T:spotify:track:clicked", proj.CurrentTrack?.Title);
        Assert.NotEqual("spotify:track:clicked", proj.CurrentTrack?.Title);
    }

    [Fact]
    public async Task NoActiveDevice_Pause_RoutesLocal_NotForward()
    {
        using var c = Make(out var host, out _, out var outbound);
        await c.PauseAsync();
        Assert.Contains("pause", host.Calls);
        Assert.Empty(outbound.Sent);
    }

    [Fact]
    public async Task NoActiveDevice_Resume_GhostResumesFromClusterSnapshot()
    {
        using var c = Make(out var host, out var proj, out _, ctx: Ctx());
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 5000));   // ghost: cluster has a track, nobody active
        await c.ResumeAsync();
        await Task.Delay(20);
        Assert.Contains("load:spotify:track:ghost", host.Calls);   // seeded from the cluster
        Assert.Contains("seek:5000", host.Calls);                  // resumed at the cluster position
        Assert.Contains("play", host.Calls);
    }

    // A skip forward publishes the NEW track's own start (0), never the OUTGOING track's carried-forward position —
    // "b" had played 0 ms, and must never be stamped with "a"'s 2578 ms on the wire (the phone-inherits-a-stale-
    // position defect).
    [Fact]
    public async Task Next_PublishesNewTrack_NeverCarriesPreviousTrackPosition()
    {
        var recording = new RecordingProjection();
        using var c = Make(out var host, out _, out _, extra: new IPlaybackProjection[] { recording });
        await c.PlayAsync("spotify:playlist:p");   // a
        host.PositionMs = 2578;                    // "a" had played 2578 ms by the time the user skips
        recording.Events.Clear();

        await c.NextAsync();

        var trackChanged = recording.Events.Single(e => e.Kind == EvKind.TrackChanged);
        Assert.Equal("spotify:track:b", trackChanged.Track?.Uri);
        Assert.Equal(0, trackChanged.AtMs);
    }

    // TransferToAsync must publish our LIVE position/play-state before forwarding — the target device otherwise
    // inherits whatever we last happened to PUT, which can be seconds behind the user's actual position (nothing
    // re-announces on a timer).
    [Fact]
    public async Task TransferToAsync_PublishesFreshState_BeforeForwarding()
    {
        using var c = Make(out var host, out _, out var outbound);
        await c.PlayAsync("spotify:playlist:p");
        host.PositionMs = 4200;   // the live position has moved on since our last PUT

        var publishedAt = new List<long>();
        int transferCountAtPublish = -1;
        c.PublishFreshStateOnWire = () =>
        {
            publishedAt.Add(host.PositionMs);
            transferCountAtPublish = outbound.Transfers.Count;   // must fire BEFORE the forward, not after
        };

        await c.TransferToAsync("phone-1");

        Assert.Equal(new[] { 4200L }, publishedAt);   // published, carrying the live position
        Assert.Equal(0, transferCountAtPublish);      // nothing had been forwarded yet at publish time
        Assert.Single(outbound.Transfers);
        Assert.Equal(("us", "phone-1", false), outbound.Transfers[0]);   // audio session → no video_persistence
    }

    // Not the active owner (another device already plays) — there is no local truth to give the target, so the fresh
    // publish must not fire; only the forward happens.
    [Fact]
    public async Task TransferToAsync_WhileNotActiveOwner_DoesNotPublishFreshState()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));   // somebody else already owns the cluster

        int publishCount = 0;
        c.PublishFreshStateOnWire = () => publishCount++;

        await c.TransferToAsync("phone-1");

        Assert.Equal(0, publishCount);
        Assert.Single(outbound.Transfers);
    }

    [Fact]
    public async Task Ended_AutoAdvances_ToNextTrack()
    {
        using var c = Make(out var host, out _, out _);
        await c.PlayAsync("spotify:playlist:p");   // a
        host.Calls.Clear();
        host.Emit(new AudioHostSignal(AudioHostSignalKind.Ended, 60000));
        await Task.Delay(60);
        Assert.Contains("load:spotify:track:b", host.Calls);
    }

    // A RESUMED reopen (a nonzero seek target — the takeover/regain-ownership reload) whose Ended lands BEHIND that
    // target (here, at 0) never actually played what it opened — a natural end lands at/after where it started. This
    // is a FAILED REOPEN wearing an end-of-track costume (a reopen racing a just-attached, still-streaming body), and
    // auto-advancing on it is exactly the "pressed play 4-5 times before a song actually played" defect: every failed
    // reopen silently skips the queue forward instead of reporting. This must report a failure and leave the current
    // track selected.
    [Fact]
    public async Task Ended_BehindAResumedLoadsOwnTarget_DoesNotAdvance_AndReportsFailure()
    {
        using var c = Make(out var host, out _, out _);
        var errors = new List<PlaybackErrorInfo>();
        c.OnPlaybackError = errors.Add;
        await c.PlayAsync("spotify:playlist:p");   // a, playing
        host.PositionMs = 3000;                    // "a" is 3 s in when ownership is taken away
        c.DeactivateIfActiveOwner();                // simulates the takeover teardown (arms the reload latch)
        host.Calls.Clear();

        await c.ResumeAsync();                      // ownership-regain-style reload: resumes "a" at 3000 ms
        Assert.Contains("seek:3000", host.Calls);   // the reopen really did ask to resume at 3000
        host.Calls.Clear();

        host.Emit(new AudioHostSignal(AudioHostSignalKind.Ended, 0));   // …but the reopen instantly "ended" at 0
        await Task.Delay(60);

        Assert.DoesNotContain(host.Calls, x => x.StartsWith("load:", StringComparison.Ordinal));   // queue not advanced
        Assert.Single(errors);
    }

    // The mirror-image guard: an ORDINARY fresh play (target 0) that ends at 0 still advances normally — the guard
    // only ever suppresses an Ended that lands BEHIND a genuinely resumed (nonzero) target, never a plain short/empty
    // playable starting from the top.
    [Fact]
    public async Task Ended_AtPositionZero_AfterAFreshStart_StillAdvances()
    {
        using var c = Make(out var host, out _, out _);
        await c.PlayAsync("spotify:playlist:p");   // a, starts fresh at 0 — not a resume
        host.Calls.Clear();

        host.Emit(new AudioHostSignal(AudioHostSignalKind.Ended, 0));
        await Task.Delay(60);

        Assert.Contains("load:spotify:track:b", host.Calls);
    }

    // ── radio "Start radio" (radio-inspiredby-mix-design §5.3) ───────────────────────────────────────────────────────
    [Fact]
    public async Task StartRadio_NothingPlaying_PlaysRadioPlaylistImmediately()
    {
        var ctx = new FakeContextResolver("spotify:track:a", "spotify:track:b") { RadioSeedResult = "spotify:playlist:radio" };
        using var c = Make(out var host, out _, out _, ctx: ctx);

        var uri = await c.StartRadioAsync("spotify:track:seed", "Seed");

        Assert.Equal("spotify:playlist:radio", uri);
        Assert.Contains("load:spotify:track:a", host.Calls);   // radio playlist played from the top
        Assert.Contains("play", host.Calls);
    }

    [Fact]
    public async Task StartRadio_WhilePlaying_ParksAfterCurrent_NoReload_ThenFlowsIn()
    {
        var ctx = new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c")
        { RadioSeedResult = "spotify:playlist:radio" };
        using var c = Make(out var host, out _, out var outbound, ctx: ctx);
        await c.PlayAsync("spotify:playlist:p");               // playing "a"
        host.Calls.Clear();

        var uri = await c.StartRadioAsync("spotify:track:a", "A");

        Assert.Equal("spotify:playlist:radio", uri);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("load:", StringComparison.Ordinal));   // current track NOT reloaded
        Assert.Empty(outbound.Sent);                          // local op, nothing forwarded

        // Track-end flows into the radio via the existing Ended → AutoAdvance path, skipping the duplicate seed "a".
        host.Emit(new AudioHostSignal(AudioHostSignalKind.Ended, 60000));
        await Task.Delay(60);
        Assert.Contains("load:spotify:track:b", host.Calls);
    }

    [Fact]
    public async Task StartRadio_NoPlaylist_ReturnsNull_NoContextChange()
    {
        var ctx = new FakeContextResolver("spotify:track:a", "spotify:track:b") { RadioSeedResult = null };
        using var c = Make(out var host, out _, out _, ctx: ctx);
        await c.PlayAsync("spotify:playlist:p");               // playing "a"
        host.Calls.Clear();

        var uri = await c.StartRadioAsync("spotify:track:seed");

        Assert.Null(uri);
        Assert.Empty(host.Calls);                              // nothing loaded / no context change
    }

    [Fact]
    public async Task RemoteActive_Pause_Seek_Volume_Play_AllForward()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out var outbound, extra: new[] { events });
        proj.OnCluster(Cluster("other-device"));
        await c.PauseAsync();
        await c.SeekAsync(4242, SeekMode.Accurate);
        await c.SetVolumeAsync(0.5);
        await c.PlayAsync("spotify:playlist:p");
        Assert.DoesNotContain(host.Calls, x => x is "pause" or "play");   // nothing driven locally
        Assert.Contains(outbound.Sent, s => s.Json.Contains("pause"));
        Assert.Contains(outbound.Sent, s => s.Json.Contains("seek_to") && s.Json.Contains("4242"));
        Assert.Equal((int)System.Math.Round(0.5 * 65535), outbound.LastVolume);   // volume via the dedicated connect/volume PUT
        Assert.DoesNotContain(outbound.Sent, s => s.Json.Contains("set_volume"));  // NOT a player/command verb
        Assert.Contains(outbound.Sent, s => s.Json.Contains("\"endpoint\":\"play\"") && s.Json.Contains("spotify:playlist:p"));
        Assert.All(outbound.Sent, s => Assert.Equal("other-device", s.Target));
    }

    [Fact]
    public async Task RemoteActive_Repeat_SplitsIntoTrackThenContext()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.SetRepeatAsync(RepeatMode.Context);
        Assert.Equal(2, outbound.Sent.Count);
        Assert.Contains("set_repeating_track", outbound.Sent[0].Json);
        Assert.Contains("set_repeating_context", outbound.Sent[1].Json);
        Assert.Contains("true", outbound.Sent[1].Json);   // context = true
    }

    [Fact]
    public async Task RemoteActive_Enqueue_SendsAddToQueueTrackObject_NotFlatUri()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.EnqueueAsync("spotify:track:x");
        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var cmd = doc.RootElement.GetProperty("command");
        Assert.Equal("add_to_queue", cmd.GetProperty("endpoint").GetString());
        Assert.Equal("spotify:track:x", cmd.GetProperty("track").GetProperty("uri").GetString());
        Assert.False(cmd.TryGetProperty("uri", out _));   // NOT the legacy flat command.uri
        var log = cmd.GetProperty("logging_params");
        Assert.Equal(1, log.GetProperty("interaction_ids").GetArrayLength());   // controller mints a real interaction id
        Assert.False(string.IsNullOrEmpty(log.GetProperty("interaction_ids")[0].GetString()));
        Assert.True(log.TryGetProperty("command_received_time", out _));        // capture-faithful logging_params
        Assert.Equal("other-device", outbound.LastTarget);
    }

    // ── bug 5: EnqueueAsync(Track) forwards a real Track — the remote row must carry the SAME display + video
    // metadata a set_queue row gets, not an erased {}.
    [Fact]
    public async Task RemoteActive_EnqueueTrack_SendsFullMetadata_IncludingVideoAssociation()
    {
        var track = new Track("v", "spotify:track:v", "A Video Song",
            new[] { new ArtistRef("ar1", "spotify:artist:ar1", "Some Artist") },
            new AlbumRef("al1", "spotify:album:al1", "Some Album"), 210_000, false, null);
        var store = new InMemoryStore();
        store.UpsertVideoAssociation(new VideoAssociation(track.Uri, true, "spotify:track:v-video",
            VideoAssociation.NoFiles, null, DateTimeOffset.UtcNow, 0));
        try
        {
            VideoPresence.Attach(null, store);
            using var c = Make(out _, out var proj, out var outbound);
            proj.OnCluster(Cluster("other-device"));
            await c.EnqueueAsync(track);

            using var doc = JsonDocument.Parse(outbound.LastJson!);
            var meta = doc.RootElement.GetProperty("command").GetProperty("track").GetProperty("metadata");
            Assert.Equal("A Video Song", meta.GetProperty("title").GetString());
            Assert.Equal("Some Artist", meta.GetProperty("artist_name").GetString());
            Assert.Equal("video", meta.GetProperty("track_player").GetString());
            Assert.Equal("video", meta.GetProperty("media.type").GetString());
            Assert.Equal("spotify:track:v-video", meta.GetProperty("video_association").GetString());
        }
        finally { VideoPresence.Attach(null); }
    }

    [Fact]
    public async Task RemoteActive_PlayOrdered_EmbedsVisibleOrder_AndSkipTo()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.PlayOrderedAsync("spotify:playlist:p", new[]
        {
            new PlaybackContextTrack("spotify:track:c", "uc"),
            new PlaybackContextTrack("spotify:track:a", "ua"),
            new PlaybackContextTrack("spotify:track:b", "ub"),
        }, startIndex: 1);

        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var cmd = doc.RootElement.GetProperty("command");
        Assert.Equal("play", cmd.GetProperty("endpoint").GetString());
        var tracks = cmd.GetProperty("context").GetProperty("pages")[0].GetProperty("tracks");
        Assert.Equal("spotify:track:c", tracks[0].GetProperty("uri").GetString());   // visible order, verbatim
        Assert.Equal("spotify:track:a", tracks[1].GetProperty("uri").GetString());
        Assert.Equal("spotify:track:b", tracks[2].GetProperty("uri").GetString());
        var skip = cmd.GetProperty("prepare_play_options").GetProperty("skip_to");
        Assert.Equal("spotify:track:a", skip.GetProperty("track_uri").GetString());  // startIndex 1
        Assert.Equal("ua", skip.GetProperty("track_uid").GetString());
        Assert.Equal(1, skip.GetProperty("track_index").GetInt32());
    }

    // ── bug 4: a forwarded play whose selected row carries a live video intent must tell the target so, or it opens
    // the video-capable context/track in plain audio.
    [Fact]
    public async Task RemoteActive_PlayOrdered_SelectedRowIsVideo_CarriesModesMedia()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        var videoMeta = new Dictionary<string, string> { ["track_player"] = "video" };
        await c.PlayOrderedAsync("spotify:playlist:p", new[]
        {
            new PlaybackContextTrack("spotify:track:a", "ua", videoMeta),
        }, startIndex: 0);

        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var modes = doc.RootElement.GetProperty("command").GetProperty("prepare_play_options")
            .GetProperty("player_options_override").GetProperty("modes");
        Assert.Equal("VIDEO", modes.GetProperty("media").GetString());
    }

    [Fact]
    public async Task RemoteActive_PlayOrdered_PlainTrack_CarriesNoModesOverride()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.PlayOrderedAsync("spotify:playlist:p", new[] { new PlaybackContextTrack("spotify:track:a", "ua") }, startIndex: 0);

        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var playerOptions = doc.RootElement.GetProperty("command").GetProperty("prepare_play_options").GetProperty("player_options_override");
        Assert.False(playerOptions.TryGetProperty("modes", out _));
    }

    [Fact]
    public async Task Local_PlayOrdered_HonorsEmbeddedOrder_NotResolver()
    {
        // Resolver's fixed list is [x]; the visible order is [b,a]. Local play must honor the SUPPLIED order.
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:x"));
        await c.PlayOrderedAsync("spotify:playlist:p", new[]
        {
            new PlaybackContextTrack("spotify:track:b", "ub"),
            new PlaybackContextTrack("spotify:track:a", "ua"),
        }, startIndex: 0);
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:b", host.Calls);        // embedded order honored
        Assert.DoesNotContain("load:spotify:track:x", host.Calls);  // NOT the resolver's list
    }

    [Fact]
    public async Task AnotherDeviceBecomesActive_StopsLocalPlayback()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out _, extra: new[] { events });
        await c.PlayAsync("spotify:playlist:p");
        Assert.True(host.IsPlaying);
        proj.OnCluster(Cluster("other-device"));   // someone else takes over
        Assert.Contains("stop", host.Calls);
        Assert.False(host.IsPlaying);
        Assert.Equal(1, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task TransferToSelf_GhostResumes_TransferAway_ForwardsAndStops()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out var outbound, extra: new[] { events });
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 1000));
        await c.TransferToAsync("us");                 // self → ghost resume
        await Task.Delay(20);
        Assert.Contains("load:spotify:track:ghost", host.Calls);

        host.Calls.Clear();
        await c.TransferToAsync("other-device");        // away → forward + stop
        Assert.Contains(outbound.Transfers, t => t.From == "us" && t.Target == "other-device");
        Assert.Contains("stop", host.Calls);
        Assert.Equal(1, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task RemoteViewer_TransferToAnotherDevice_UsesConnectTransfer_WithoutInactive()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out var outbound, extra: new[] { events });
        proj.OnCluster(Cluster("active-device", Remote("spotify:track:remote"), playing: true));

        await c.TransferToAsync("target-device");

        Assert.Contains(outbound.Transfers, t => t.From == "active-device" && t.Target == "target-device");
        Assert.DoesNotContain("stop", host.Calls);
        Assert.Equal(0, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task ActiveOwner_TransferFailure_DoesNotStopOrPublishInactive()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out _, out var outbound, extra: new[] { events });
        await c.PlayAsync("spotify:playlist:p");
        Assert.True(host.IsPlaying);
        outbound.TransferOk = false;
        host.Calls.Clear();

        await c.TransferToAsync("target-device");

        Assert.Contains(outbound.Transfers, t => t.From == "us" && t.Target == "target-device");
        Assert.DoesNotContain("stop", host.Calls);
        Assert.True(host.IsPlaying);
        Assert.Equal(0, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task RemoteViewer_ActiveDeviceSwitch_DoesNotPublishInactive()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out _, extra: new[] { events });
        proj.OnCluster(Cluster("remote-a", Remote("spotify:track:a"), playing: true));
        proj.OnCluster(Cluster("remote-b", Remote("spotify:track:b"), playing: true));

        Assert.DoesNotContain("stop", host.Calls);
        Assert.Equal(0, events.Count(EvKind.BecameInactive));
    }

    // An empty active-device id is our OWN echo (a put-state announcing active=False), never a takeover: the host
    // keeps playing and the LOCAL projection hears nothing (no BecameInactive fold, which would poison the local
    // position/play-state for a transition that never touched the host) — only the wire is told, through the new
    // PublishInactiveOnWire seam (mirroring LiveConnect's real DeviceStatePublisher.PublishInactive wiring), so a
    // remote controller still sees us go inactive. Ownership release itself is exercised by that seam firing at
    // all: PublishInactiveOnWire is invoked from the one branch that also releases ownership (see OnProjectionChanged).
    [Fact]
    public async Task ActiveOwner_ActiveDeviceClears_PublishesInactiveOnce()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out _, extra: new[] { events });
        int wireInactiveCalls = 0;
        c.PublishInactiveOnWire = () => wireInactiveCalls++;
        await c.PlayAsync("spotify:playlist:p");
        proj.OnCluster(Cluster("us", Remote("spotify:track:a"), playing: true));
        host.Calls.Clear();

        proj.OnCluster(Cluster(""));

        Assert.DoesNotContain("stop", host.Calls);
        Assert.Equal(0, events.Count(EvKind.BecameInactive));
        Assert.Equal(1, wireInactiveCalls);
    }

    [Fact]
    public async Task InboundCommand_AlwaysLocal_EvenWhenClusterShowsAnotherActive()
    {
        using var c = Make(out var host, out var proj, out _, ctx: Ctx("spotify:track:a"));
        await c.PlayAsync("spotify:playlist:p");
        proj.OnCluster(Cluster("other-device"));   // routing would say "forward"...
        host.Calls.Clear();
        ConnectCommand.TryParse(new WireRequest("k", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes("{\"command\":{\"endpoint\":\"pause\"}}"), NoHeaders), out var cmd);
        c.HandleRemoteCommand(cmd);                 // ...but an inbound REQUEST is for us → drive local
        await Task.Delay(20);
        Assert.Contains("pause", host.Calls);
    }

    // ── Phase A: inbound play resolves the context (+ skip_to / embedded pages) ───────────────────────────────────────
    static void Dispatch(PlaybackController c, string commandJson)
    {
        ConnectCommand.TryParse(new WireRequest("k", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes(commandJson), NoHeaders), out var cmd);
        c.HandleRemoteCommand(cmd);
    }

    [Fact]
    public async Task InboundPlay_Context_ResolvesAndPlaysFirstTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:a", host.Calls);
        Assert.Contains("play", host.Calls);
    }

    [Fact]
    public async Task InboundPlay_SkipToUid_StartsAtThatTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c"));
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"},\"prepare_play_options\":{\"skip_to\":{\"track_uid\":\"uid2\"}}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:c", host.Calls);   // uid2 → index 2
    }

    [Fact]
    public async Task InboundPlay_SkipToIndex_StartsAtThatTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c"));
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"},\"prepare_play_options\":{\"skip_to\":{\"track_index\":1}}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:b", host.Calls);
    }

    [Fact]
    public async Task InboundPlay_EmbeddedPages_PlayVerbatim_OverResolver()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:x"));   // the resolver's fixed list
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\",\"pages\":[{\"tracks\":[{\"uri\":\"spotify:track:e1\",\"uid\":\"u1\"},{\"uri\":\"spotify:track:e2\",\"uid\":\"u2\"}]}]}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:e1", host.Calls);          // embedded pages win
        Assert.DoesNotContain("load:spotify:track:x", host.Calls);
    }

    [Fact]
    public async Task InboundPlay_LargeEmbeddedPlaylist_IsDataDrivenAndUntruncated()
    {
        const int count = 1600; // deliberately differs from every captured playlist length
        var json = new StringBuilder("{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:large\",\"pages\":[{\"tracks\":[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append("{\"uri\":\"spotify:track:t").Append(i).Append("\",\"uid\":\"u").Append(i).Append("\"}");
        }
        json.Append("]}]},\"prepare_play_options\":{\"skip_to\":{\"track_uid\":\"u")
            .Append(count - 1).Append("\"}}}}");

        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:wrong"));
        ConnectCommand.TryParse(new WireRequest("large", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes(json.ToString()), NoHeaders), out var command);
        await c.HandleRemoteCommandAsync(command);

        Assert.Contains($"load:spotify:track:t{count - 1}", host.Calls);
        Assert.DoesNotContain("load:spotify:track:wrong", host.Calls);
    }

    [Fact]
    public async Task InboundPlay_IsAudioFirst_AndExplicitLocalMediaIntentCanRestoreVideo()
    {
        var audio = new RecordingAudioHost();
        var video = new RecordingAudioHost();
        var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        int videoLoads = 0;
        using var controller = new PlaybackController(
            audio, new StubTrackResolver(), projection, Ctx("spotify:track:a"), "us", videoHost: video);
        controller.ShouldPlayAsVideo = _ => true;
        controller.LoadCurrentVideoAsync = (_, _, _) =>
        {
            Interlocked.Increment(ref videoLoads);
            return Task.FromResult(true);
        };
        byte[] payload = Encoding.UTF8.GetBytes(
            "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"}}}");
        var command = new ConnectCommand(
            ConnectCmd.Play, "play", "remote-play", 8, "spotify-controller",
            0, false, payload);

        await controller.HandleRemoteCommandAsync(command);

        Assert.Equal(PlayableKind.Audio, controller.CurrentMediaKind);
        Assert.Equal(0, Volatile.Read(ref videoLoads));
        Assert.Contains("load:spotify:track:a", audio.Calls);

        await controller.RefreshCurrentMediaKindAsync();

        Assert.Equal(PlayableKind.Video, controller.CurrentMediaKind);
        Assert.Equal(1, Volatile.Read(ref videoLoads));
    }

    /// <summary>The Connect guard: a refresh that is NOT an explicit local media intent (the availability edge behind a
    /// music-video association landing) must not clear the remote playback ids — doing so wipes _connectOriginatedPlayback
    /// and silently defeats the audio-first rule for a Connect-originated session. The user's own click still does.</summary>
    [Fact]
    public async Task AvailabilityEdgeRefresh_KeepsConnectAudioFirst_UnlikeAnExplicitMediaIntent()
    {
        var audio = new RecordingAudioHost();
        var video = new RecordingAudioHost();
        var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        int videoLoads = 0;
        using var controller = new PlaybackController(
            audio, new StubTrackResolver(), projection, Ctx("spotify:track:a"), "us", videoHost: video);
        controller.ShouldPlayAsVideo = _ => true;
        controller.LoadCurrentVideoAsync = (_, _, _) => { Interlocked.Increment(ref videoLoads); return Task.FromResult(true); };
        byte[] payload = Encoding.UTF8.GetBytes(
            "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"}}}");
        await controller.HandleRemoteCommandAsync(new ConnectCommand(
            ConnectCmd.Play, "play", "remote-play", 8, "spotify-controller", 0, false, payload));
        Assert.Equal(PlayableKind.Audio, controller.CurrentMediaKind);

        await controller.RefreshCurrentMediaKindAsync(clearConnectAudioFirst: false);

        Assert.Equal(PlayableKind.Audio, controller.CurrentMediaKind);   // still audio-first — the ids survived
        Assert.Equal(0, Volatile.Read(ref videoLoads));

        await controller.RefreshCurrentMediaKindAsync();                 // an explicit intent still restores video

        Assert.Equal(PlayableKind.Video, controller.CurrentMediaKind);
        Assert.Equal(1, Volatile.Read(ref videoLoads));
    }

    [Fact]
    public async Task InboundTransfer_DecodesInnerState_DerivesGid_AndStartsPausedAtPosition()
    {
        byte[] gid = [0x12, 0x8a, 0x44, 0x9c, 0x22, 0x71, 0x08, 0x5e, 0xa1, 0x04, 0xe8, 0x95, 0x33, 0x07, 0x61, 0xf0];
        string expectedUri = "spotify:track:" + Base62.Encode(gid);
        var transfer = new TransferState
        {
            Options = new TransferPlayerOptions(),
            Playback = new TransferPlayback
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PositionAsOfTimestamp = 30_000,
                Speed = 1.0,
                Paused = true,
                CurrentTrack = new TransferContextTrack { Uri = "", Uid = "current", Gid = ByteString.CopyFrom(gid) },
            },
            CurrentSession = new TransferSession
            {
                CurrentUid = "current",
                Context = new TransferContext { Uri = "spotify:playlist:transfer", Url = "context://transfer" },
            },
            Queue = new TransferQueue(),
        };
        string body = "{\"message_id\":77,\"sent_by_device_id\":\"desktop\",\"command\":{" +
            "\"endpoint\":\"transfer\",\"data\":\"" + Convert.ToBase64String(transfer.ToByteArray()) + "\"," +
            "\"options\":{\"restore_paused\":\"restore\",\"restore_position\":\"extrapolate\"," +
            "\"restore_track\":\"always_play_something\",\"retain_session\":\"do_not_retain\"}}}";

        using var c = Make(out var host, out var projection, out _, ctx: new FakeContextResolver("spotify:track:other"),
            transferDecoder: new ProtoTransferStateDecoder());
        ConnectCommand.TryParse(new WireRequest("transfer", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes(body), NoHeaders), out var command);
        var outcome = await c.HandleRemoteCommandAsync(command);

        Assert.Equal(ConnectCommandOutcome.Applied, outcome);
        Assert.Equal(expectedUri, projection.CurrentTrack?.Uri);
        Assert.Contains("load:" + expectedUri, host.Calls);
        Assert.Contains("seek:30000", host.Calls);
        Assert.DoesNotContain("play", host.Calls);
        Assert.False(projection.IsPlaying);
    }

    // An inbound transfer's current track carrying the wire's music-video association keys (track_player: "video",
    // media.type, media.manifest_id — the SAME shape a cluster row uses) must restore on the VIDEO host even though
    // HandleInboundPlayOrTransferAsync marks this Connect-originated (audio-first) and no ShouldPlayAsVideo hook is
    // wired at all — the forced kind bypasses that gate exactly like an inbound modes.media override would.
    [Fact]
    public async Task InboundTransfer_CurrentTrackCarriesVideoMetadata_RestoresOnVideoHost()
    {
        var audio = new RecordingAudioHost();
        var video = new RecordingAudioHost();
        var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        using var controller = new PlaybackController(
            audio, new StubTrackResolver(), projection, Ctx("spotify:track:other"), "us",
            videoHost: video, transferDecoder: new ProtoTransferStateDecoder());
        controller.LoadCurrentVideoAsync = (_, _, _) => Task.FromResult(true);

        var currentTrack = new TransferContextTrack { Uri = "spotify:track:vid1", Uid = "current" };
        currentTrack.Metadata["track_player"] = "video";
        currentTrack.Metadata["media.type"] = "video";
        currentTrack.Metadata["media.manifest_id"] = "b0d0cccc2fe240de8d6c94b3746af402";
        var transfer = new TransferState
        {
            Options = new TransferPlayerOptions(),
            Playback = new TransferPlayback
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PositionAsOfTimestamp = 5000,
                Speed = 1.0,
                Paused = false,
                CurrentTrack = currentTrack,
            },
            CurrentSession = new TransferSession
            {
                CurrentUid = "current",
                Context = new TransferContext { Uri = "spotify:playlist:transfer", Url = "context://transfer" },
            },
            Queue = new TransferQueue(),
        };
        string body = "{\"message_id\":78,\"sent_by_device_id\":\"desktop\",\"command\":{" +
            "\"endpoint\":\"transfer\",\"data\":\"" + Convert.ToBase64String(transfer.ToByteArray()) + "\"," +
            "\"options\":{\"restore_paused\":\"restore\",\"restore_position\":\"extrapolate\"," +
            "\"restore_track\":\"always_play_something\",\"retain_session\":\"do_not_retain\"}}}";

        ConnectCommand.TryParse(new WireRequest("transfer", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes(body), NoHeaders), out var command);
        var outcome = await controller.HandleRemoteCommandAsync(command);

        Assert.Equal(ConnectCommandOutcome.Applied, outcome);
        Assert.Equal(PlayableKind.Video, controller.CurrentMediaKind);
        Assert.DoesNotContain(audio.Calls, x => x.StartsWith("load:", StringComparison.Ordinal));
        Assert.Contains("play", video.Calls);
    }

    // bug 8: TransferPlayerOptions.modes["video_persistence"]=="VIDEO" forces the video host even when the CURRENT
    // track's own metadata carries no video markers at all — the sender's hand-off-level claim, honored the same way
    // the per-track metadata check already is.
    [Fact]
    public async Task InboundTransfer_VideoPersistenceMode_ForcesVideoHost_WithoutPerTrackMetadata()
    {
        var audio = new RecordingAudioHost();
        var video = new RecordingAudioHost();
        var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        using var controller = new PlaybackController(
            audio, new StubTrackResolver(), projection, Ctx("spotify:track:other"), "us",
            videoHost: video, transferDecoder: new ProtoTransferStateDecoder());
        controller.LoadCurrentVideoAsync = (_, _, _) => Task.FromResult(true);

        var options = new TransferPlayerOptions();
        options.Modes.Add(new Wavee.Protocol.Player.ModeEntry { Key = "video_persistence", Value = "VIDEO" });
        var transfer = new TransferState
        {
            Options = options,
            Playback = new TransferPlayback
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PositionAsOfTimestamp = 5000,
                Speed = 1.0,
                Paused = false,
                CurrentTrack = new TransferContextTrack { Uri = "spotify:track:vid2", Uid = "current" },   // no per-track video metadata
            },
            CurrentSession = new TransferSession
            {
                CurrentUid = "current",
                Context = new TransferContext { Uri = "spotify:playlist:transfer", Url = "context://transfer" },
            },
            Queue = new TransferQueue(),
        };
        string body = "{\"message_id\":79,\"sent_by_device_id\":\"desktop\",\"command\":{" +
            "\"endpoint\":\"transfer\",\"data\":\"" + Convert.ToBase64String(transfer.ToByteArray()) + "\"," +
            "\"options\":{\"restore_paused\":\"restore\",\"restore_position\":\"extrapolate\"," +
            "\"restore_track\":\"always_play_something\",\"retain_session\":\"do_not_retain\"}}}";

        ConnectCommand.TryParse(new WireRequest("transfer", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes(body), NoHeaders), out var command);
        var outcome = await controller.HandleRemoteCommandAsync(command);

        Assert.Equal(ConnectCommandOutcome.Applied, outcome);
        Assert.Equal(PlayableKind.Video, controller.CurrentMediaKind);
        Assert.DoesNotContain(audio.Calls, x => x.StartsWith("load:", StringComparison.Ordinal));
        Assert.Contains("play", video.Calls);
    }

    // ── Phase B: the queue verbs (add_to_queue / set_queue / set_options) + prev<3s ──────────────────────────────────
    const string PlayP = "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"}}}";

    [Fact]
    public async Task InboundAddToQueue_WhenIdle_StartsPlayingIt()
    {
        using var c = Make(out var host, out var proj, out _, ctx: new FakeContextResolver());   // empty context → idle
        Dispatch(c, "{\"command\":{\"endpoint\":\"add_to_queue\",\"track\":{\"uri\":\"spotify:track:q1\",\"uid\":\"uq1\"}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:q1", host.Calls);
        Assert.Equal("spotify:track:q1", proj.CurrentTrack!.Uri);
    }

    [Fact]
    public async Task InboundAddToQueue_WhilePlaying_EnqueuesIntoUpNext()
    {
        using var c = Make(out _, out var proj, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(30);
        Dispatch(c, "{\"command\":{\"endpoint\":\"add_to_queue\",\"track\":{\"uri\":\"spotify:track:q1\",\"uid\":\"uq1\"}}}");
        await Task.Delay(30);
        Assert.Contains(proj.Queue, e => e.Bucket == QueueBucket.UserQueue && e.Track.Uri == "spotify:track:q1");
    }

    [Fact]
    public async Task InboundSetQueue_ReplacesUpNext()
    {
        using var c = Make(out _, out var proj, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        Dispatch(c, "{\"command\":{\"endpoint\":\"add_to_queue\",\"track\":{\"uri\":\"spotify:track:old\"}}}");
        await Task.Delay(20);
        Dispatch(c, "{\"command\":{\"endpoint\":\"set_queue\",\"next_tracks\":[" +
            "{\"uri\":\"spotify:track:n1\",\"uid\":\"u1\",\"provider\":\"queue\"}," +
            "{\"uri\":\"spotify:track:n2\",\"uid\":\"u2\",\"provider\":\"queue\"}]}}");
        await Task.Delay(30);
        var uq = proj.Queue.Where(e => e.Bucket == QueueBucket.UserQueue).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:n1", "spotify:track:n2" }, uq);   // 'old' replaced
        Assert.DoesNotContain(proj.Queue, e => e.Track.Uri == "spotify:track:old");
    }

    [Fact]
    public async Task InboundSetQueue_OnlyQueueProviderRows_BecomeUserQueue()
    {
        using var c = Make(out _, out var proj, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        // next_tracks = user queue (provider:queue) THEN context continuation (provider:context) — queue rows land in
        // UserQueue; context continuation rows reconcile into Upcoming (§6 F8 full reconcile).
        Dispatch(c, "{\"command\":{\"endpoint\":\"set_queue\",\"next_tracks\":[" +
            "{\"uri\":\"spotify:track:n1\",\"uid\":\"q1\",\"provider\":\"queue\"}," +
            "{\"uri\":\"spotify:track:n2\",\"uid\":\"\",\"provider\":\"queue\"}," +
            "{\"uri\":\"spotify:track:cx\",\"uid\":\"h1\",\"provider\":\"context\"}," +
            "{\"uri\":\"spotify:track:cy\",\"uid\":\"h2\",\"provider\":\"context\"}]}}");
        await Task.Delay(30);
        var uq = proj.Queue.Where(e => e.Bucket == QueueBucket.UserQueue).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:n1", "spotify:track:n2" }, uq);
        var up = proj.Queue.Where(e => e.Bucket == QueueBucket.NextUp).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:cx", "spotify:track:cy" }, up);
    }

    [Fact]
    public async Task InboundSetQueue_DropsDelimiterRows()
    {
        using var c = Make(out _, out var proj, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        Dispatch(c, "{\"command\":{\"endpoint\":\"set_queue\",\"next_tracks\":[" +
            "{\"uri\":\"spotify:track:n1\",\"provider\":\"queue\"}," +
            "{\"uri\":\"spotify:delimiter\",\"uid\":\"delimiter0\",\"provider\":\"context\"}]}}");
        await Task.Delay(30);
        var uq = proj.Queue.Where(e => e.Bucket == QueueBucket.UserQueue).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:n1" }, uq);
        Assert.DoesNotContain(proj.Queue, e => e.Track.Uri == "spotify:delimiter");
    }

    [Fact]
    public async Task Local_PlayNext_InsertsAtFrontOfUserQueue()
    {
        using var c = Make(out _, out var proj, out var outbound, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        await c.PlayAsync("spotify:playlist:p");            // local: seed a resident context
        await c.EnqueueAsync("spotify:track:existing");     // a pre-existing user-queue item
        await c.PlayNextAsync(new[]
        {
            new PlaybackContextTrack("spotify:track:t1", ""),
            new PlaybackContextTrack("spotify:track:t2", ""),
        });
        var uq = proj.Queue.Where(e => e.Bucket == QueueBucket.UserQueue).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:t1", "spotify:track:t2", "spotify:track:existing" }, uq);  // play-next at front
        Assert.Empty(outbound.Sent);                        // local → nothing forwarded
    }

    [Fact]
    public async Task RemoteActive_PlayNext_SendsSetQueue_InsertedRowsAreQueueProvider()
    {
        using var c = Make(out _, out var proj, out var outbound, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        proj.OnCluster(Cluster("other-device"));
        await c.PlayNextAsync(new[]
        {
            new PlaybackContextTrack("spotify:track:t1", "q1"),
            new PlaybackContextTrack("spotify:track:t2", ""),
        });
        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var cmd = doc.RootElement.GetProperty("command");
        Assert.Equal("set_queue", cmd.GetProperty("endpoint").GetString());
        Assert.Empty(cmd.GetProperty("prev_tracks").EnumerateArray());
        var next = cmd.GetProperty("next_tracks");
        Assert.Equal("spotify:track:t1", next[0].GetProperty("uri").GetString());
        Assert.Equal("queue", next[0].GetProperty("provider").GetString());
        Assert.Equal("spotify:track:t2", next[1].GetProperty("uri").GetString());
        Assert.Equal("queue", next[1].GetProperty("provider").GetString());
        var log = cmd.GetProperty("logging_params");
        Assert.Equal(1, log.GetProperty("interaction_ids").GetArrayLength());   // controller-driven set_queue carries an interaction id
        Assert.False(string.IsNullOrEmpty(log.GetProperty("interaction_ids")[0].GetString()));
        Assert.Equal("other-device", outbound.LastTarget);
    }

    [Fact]
    public async Task RemoteActive_PlayNext_EchoesQueueRevisionFromCluster()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device") with { QueueRevision = "10355548321371651421" });   // threaded from the proto
        await c.PlayNextAsync(new[] { new PlaybackContextTrack("spotify:track:t1", "") });
        using var doc = JsonDocument.Parse(outbound.LastJson!);
        Assert.Equal(10355548321371651421UL,
            doc.RootElement.GetProperty("command").GetProperty("queue_revision").GetUInt64());
    }

    [Fact]
    public async Task RemoteActive_PlayNext_RewritesClusterQueue_InsertedFrontThenDeviceQueueVerbatim()
    {
        using var c = Make(out _, out var proj, out var outbound);
        // The active remote device's REAL queue (from its cluster): a queued row, then a context-continuation row, plus history.
        proj.OnCluster(Cluster("other-device") with
        {
            QueueRevision = "42",
            NextTracks = new[]
            {
                new RemoteTrack("spotify:track:eq", "", "", "", "", "", null, 0, Uid: "uq", Provider: "queue"),
                new RemoteTrack("spotify:track:cx", "", "", "", "", "", null, 0, Uid: "uc", Provider: "context"),
            },
            PrevTracks = new[]
            {
                new RemoteTrack("spotify:track:hist", "", "", "", "", "", null, 0, Uid: "uh", Provider: "context"),
            },
        });
        await c.PlayNextAsync(new[] { new PlaybackContextTrack("spotify:track:t1", "") });

        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var cmd = doc.RootElement.GetProperty("command");
        Assert.Equal("set_queue", cmd.GetProperty("endpoint").GetString());
        Assert.Equal(42UL, cmd.GetProperty("queue_revision").GetUInt64());   // from the same cluster snapshot, not 0

        var prevT = cmd.GetProperty("prev_tracks");                          // device history echoed verbatim (NOT empty)
        Assert.Equal("spotify:track:hist", prevT[0].GetProperty("uri").GetString());
        Assert.Equal("context", prevT[0].GetProperty("provider").GetString());

        var next = cmd.GetProperty("next_tracks");
        Assert.Equal("spotify:track:t1", next[0].GetProperty("uri").GetString());   // inserted at the FRONT
        Assert.Equal("queue", next[0].GetProperty("provider").GetString());
        Assert.Equal("spotify:track:eq", next[1].GetProperty("uri").GetString());   // then the device's own queue row...
        Assert.Equal("queue", next[1].GetProperty("provider").GetString());
        Assert.Equal("spotify:track:cx", next[2].GetProperty("uri").GetString());   // ...then its context continuation
        Assert.Equal("context", next[2].GetProperty("provider").GetString());
    }

    [Fact]
    public async Task InboundSetOptions_RepeatTrack_NextStaysOnSameTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        Dispatch(c, "{\"command\":{\"endpoint\":\"set_options\",\"repeating_track\":true}}");
        await Task.Delay(20);
        host.Calls.Clear();
        Dispatch(c, "{\"command\":{\"endpoint\":\"skip_next\"}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:a", host.Calls);   // repeat-one → reloads a, not b
    }

    [Fact]
    public async Task InboundSkipPrev_After3s_RestartsCurrentTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        host.PositionMs = 5000;
        host.Calls.Clear();
        Dispatch(c, "{\"command\":{\"endpoint\":\"skip_prev\"}}");
        await Task.Delay(30);
        Assert.Contains("seek:0", host.Calls);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("load:"));
    }

    [Fact]
    public async Task InboundSkipPrev_Within3s_StepsToPrevTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"},\"prepare_play_options\":{\"skip_to\":{\"track_index\":1}}}}");
        await Task.Delay(20);   // current = b
        host.PositionMs = 1000;
        host.Calls.Clear();
        Dispatch(c, "{\"command\":{\"endpoint\":\"skip_prev\"}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:a", host.Calls);   // stepped back to a
    }

    // ── Volume parity: dedicated connect/volume endpoint + read the ACTIVE device's volume + react to remote changes ───
    [Fact]
    public async Task RemoteActive_SetVolume_UsesConnectVolumeEndpoint_NotPlayerCommand()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.SetVolumeAsync(0.25);
        Assert.Equal((int)System.Math.Round(0.25 * 65535), outbound.LastVolume);
        Assert.Equal("other-device", outbound.Volumes[^1].Target);
        Assert.Empty(outbound.Sent);   // no player/command verb at all
    }

    [Fact]
    public void Cluster_ActiveDeviceVolume_DrivesSlider_AndRemoteChangeReacts()
    {
        var proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 1_000_000);   // clock far ahead → outside any local-command window
        proj.OnCluster(Cluster("other-device") with { ActiveVolume0_65535 = 32768 });
        Assert.Equal(0.5, proj.Volume, 2);   // the active device's volume drives the slider
        proj.OnCluster(Cluster("other-device") with { ActiveVolume0_65535 = 13107 });   // a remote controller turned it down
        Assert.Equal(0.2, proj.Volume, 2);   // reacted to the remote change
    }

    // ── Local playback rejection: with OnLocalPlaybackRejected set (local audio unsupported), every local play path aborts
    // + fires the hook (the app's "choose a remote device" toast); remote forwarding is untouched. Default (null) = the
    // existing tests above, which prove local playback still works when the hook is absent. ─────────────────────────────
    [Fact]
    public async Task LocalPlay_Rejected_WhenHookSet()
    {
        using var c = Make(out var host, out _, out var outbound);   // no active device → routes local
        int rejects = 0; c.OnLocalPlaybackRejected = () => rejects++;
        await c.PlayAsync("spotify:playlist:p");
        await Task.Delay(20);
        Assert.DoesNotContain(host.Calls, x => x == "play" || x.StartsWith("load:"));   // nothing loaded / played locally
        Assert.True(rejects >= 1);                                                       // the toast hook fired
        Assert.Empty(outbound.Sent);                                                     // and nothing was forwarded
    }

    [Fact]
    public async Task Resume_GhostResume_Rejected_WhenHookSet()
    {
        using var c = Make(out var host, out var proj, out _, ctx: Ctx());
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 5000));   // a cluster track, nobody active → local ghost-resume
        int rejects = 0; c.OnLocalPlaybackRejected = () => rejects++;
        await c.ResumeAsync();
        await Task.Delay(20);
        Assert.DoesNotContain(host.Calls, x => x == "play" || x.StartsWith("load:"));
        Assert.True(rejects >= 1);
    }

    [Fact]
    public async Task TransferToSelf_Rejected_WhenHookSet()
    {
        using var c = Make(out var host, out var proj, out _);
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 1000));
        int rejects = 0; c.OnLocalPlaybackRejected = () => rejects++;
        await c.TransferToAsync("us");   // transfer to THIS device = local playback → rejected
        await Task.Delay(20);
        Assert.DoesNotContain(host.Calls, x => x == "play" || x.StartsWith("load:"));
        Assert.True(rejects >= 1);
    }

    [Fact]
    public async Task RemoteForward_Unaffected_WhenHookSet()
    {
        using var c = Make(out var host, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));           // another device active → routes REMOTE
        int rejects = 0; c.OnLocalPlaybackRejected = () => rejects++;
        await c.PlayAsync("spotify:playlist:p");
        await c.PauseAsync();
        Assert.Equal(0, rejects);                                                        // remote routing never trips the local hook
        Assert.Contains(outbound.Sent, s => s.Json.Contains("\"endpoint\":\"play\""));   // forwarded to the remote device
        Assert.DoesNotContain(host.Calls, x => x == "play");                             // nothing played locally
    }

    // ── Phase A/C: host Error surfaces + one-click transfer+route ─────────────────────────────────────────────────────
    [Fact]
    public async Task HostErrorSignal_FiresOnPlaybackError()
    {
        using var c = Make(out var host, out _, out _);
        PlaybackErrorInfo? err = null;
        c.OnPlaybackError = e => err = e;
        host.Emit(new AudioHostSignal(AudioHostSignalKind.Error, 0));
        await Task.Delay(20);
        Assert.NotNull(err);   // decode/output-loop deaths now surface (were silent)
    }

    [Fact]
    public async Task SelectLocalOutput_WhileRemoteActive_RoutesFirstThenTransfersHome()
    {
        var host = new RecordingAudioHost();
        var svc = new LocalAudioDeviceService(
            new FakeDeviceMonitor(),
            host,                                         // IAudioOutputDeviceControl — records set-output ids
            (id, ct) => { host.Calls.Add("transfer:" + id); return Task.CompletedTask; },
            "us",
            () => "other-device",                         // a remote device owns playback
            (_, _) => { });

        await svc.SelectAsync("dev-1");

        Assert.Contains("setoutput:dev-1", host.Calls);
        Assert.Contains("transfer:us", host.Calls);
        Assert.True(host.Calls.IndexOf("setoutput:dev-1") < host.Calls.IndexOf("transfer:us"));   // route FIRST, then transfer home
    }

    // ── launch session restore (docs/plans/wavee/playback-restore-findings.md §§1, 2, 5, 8) ───────────────────────────
    // Recovery is scheduled from the projection callback and runs on its own task (it takes the controller lock), so
    // these await a condition rather than a returned Task.
    static async Task<bool> Settle(Func<bool> until, int timeoutMs = 2000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 10)
        {
            if (until()) return true;
            await Task.Delay(10);
        }
        return until();
    }

    [Fact]
    public async Task SessionRecovery_FirstCluster_SeedsThePausedSessionAndNeverPlays()
    {
        // The launch hole (root cause 1): a cluster naming a track with nobody active used to leave now-playing over an
        // empty queue until the user pressed Play — and then ghost-resume AUTOPLAYED. Recovery seeds it paused instead.
        using var c = Make(out var host, out var proj, out var outbound);

        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 42_000));

        Assert.True(await Settle(() => proj.CurrentTrack is not null && proj.Queue.Count > 0),
            "recovery did not seed the session from the cluster");
        Assert.False(proj.IsPlaying);                  // paused, never autoplayed on launch
        Assert.DoesNotContain("play", host.Calls);
        Assert.Empty(outbound.Sent);                   // recovery is local-only: it must not announce on the wire
    }

    [Fact]
    public async Task SessionRecovery_ThenResume_LoadsAtTheStoredPosition_WithoutASecondSeed()
    {
        using var c = Make(out var host, out var proj, out _);
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 42_000));
        Assert.True(await Settle(() => proj.Queue.Count > 0));
        host.Calls.Clear();

        await c.ResumeAsync();

        // The seeded-but-unloaded session goes through the ONE load pipeline (fix §4) and honours the cluster position
        // (fix §5) — not a bare Play() over empty media.
        Assert.True(await Settle(() => host.Calls.Any(x => x.StartsWith("load:", StringComparison.Ordinal)
                                                        || x.StartsWith("faststart:", StringComparison.Ordinal))),
            "resume after recovery did not load the seeded current: " + string.Join(",", host.Calls));
        Assert.Contains("play", host.Calls);
        Assert.Contains(host.Calls, x => x == "seek:42000" || x.StartsWith("faststart:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionRecovery_AnotherDeviceActive_StaysAViewer_AndSeedsNoLocalSession()
    {
        using var c = Make(out var host, out var proj, out _);

        proj.OnCluster(Cluster("other-device", Remote("spotify:track:remote"), pos: 5_000, playing: true));
        await Task.Delay(60);   // give a (wrongly) scheduled recovery time to do damage

        Assert.DoesNotContain("play", host.Calls);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("load:", StringComparison.Ordinal));
        Assert.False(c.HasLocalSession);   // the cluster owns the session; we mirror it
    }

    // ── bug 2: IsActiveOwner() — the ONE predicate the Connect state builder gates the CURRENT track's authoritative
    // track_player/media.* overwrite on. A Wavee that has never played must answer false here even though the
    // projection mirrors another device's now-playing row (proj.CurrentTrack is not null); otherwise a viewer echo's
    // idle "Audio" CurrentMediaKind gets stamped over the wire's own (possibly "video") claim.
    [Fact]
    public async Task IsActiveOwner_FalseWhilePassivelyViewingAnotherDevice_EvenThoughATrackMirrors()
    {
        using var c = Make(out _, out var proj, out _);
        proj.OnCluster(Cluster("other-device", Remote("spotify:track:remote"), pos: 5_000, playing: true));
        await Task.Delay(20);

        Assert.NotNull(proj.CurrentTrack);   // the viewer DOES mirror a track…
        Assert.False(c.IsActiveOwner());     // …but never claims ownership of it
    }

    [Fact]
    public async Task IsActiveOwner_TrueOnceWePlayLocally()
    {
        using var c = Make(out _, out _, out _);
        await c.PlayAsync("spotify:playlist:p");
        Assert.True(c.IsActiveOwner());
    }

    [Fact]
    public async Task SessionRecovery_StaleActiveEcho_StillFillsTheQueueAndOptions()
    {
        // Root cause 3: when the announce echo still named US active with no local session, the fold took the track but
        // refused the cluster queue AND shuffle/repeat — now-playing showed a track over an empty queue panel.
        using var c = Make(out _, out var proj, out _);

        proj.OnCluster(Cluster("us", Remote("spotify:track:mine"), pos: 1_000) with
        {
            NextTracks = [Remote("spotify:track:next1"), Remote("spotify:track:next2")],
            Shuffle = true,
            Repeat = RepeatMode.Context,
        });

        Assert.True(await Settle(() => proj.Queue.Count > 0), "the stale-active fold left the queue empty");
        Assert.True(proj.IsShuffle);
        Assert.Equal(RepeatMode.Context, proj.Repeat);
    }

    [Fact]
    public async Task LocalPrev_AfterARestoreWithHistory_StepsBackIntoThePrevTracksTail()
    {
        // Root cause 2, the payoff: prev_tracks now rebuild History, so Previous after a restore actually goes back.
        using var c = Make(out var host, out var proj, out _);
        proj.OnCluster(Cluster("", Remote("spotify:track:current"), pos: 1_000) with
        {
            PrevTracks = [Remote("spotify:track:older"), Remote("spotify:track:played")],
        });
        Assert.True(await Settle(() => proj.Queue.Count > 0));
        await c.ResumeAsync();
        Assert.True(await Settle(() => host.Calls.Contains("play")));
        host.PositionMs = 500;   // under the 3 s restart window, so this is a real step-back, not a seek(0)
        host.Calls.Clear();

        await c.PreviousAsync();

        Assert.True(await Settle(() => proj.CurrentTrack?.Uri == "spotify:track:played"),
            "Previous did not step into the restored history; current = " + proj.CurrentTrack?.Uri);
    }

    [Fact]
    public async Task LocalPrev_AfterARestoreWithNoHistory_RestartsPast3s_AndNoOpsUnderIt()
    {
        using var c = Make(out var host, out var proj, out _);
        proj.OnCluster(Cluster("", Remote("spotify:track:only"), pos: 0));   // no prev_tracks → History stays empty
        Assert.True(await Settle(() => proj.Queue.Count > 0));
        await c.ResumeAsync();
        Assert.True(await Settle(() => host.Calls.Contains("play")));

        // Past 3 s → restart the current track.
        host.PositionMs = 7_000;
        host.Calls.Clear();
        await c.PreviousAsync();
        Assert.True(await Settle(() => host.Calls.Contains("seek:0")), "Previous past 3 s did not restart the track");

        // Under 3 s with nothing behind it → a TRUE no-op (the old enabled-no-op bug), and the affordance says so.
        host.PositionMs = 900;
        host.Calls.Clear();
        await c.PreviousAsync();
        await Task.Delay(60);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("load:", StringComparison.Ordinal)
                                            || x.StartsWith("faststart:", StringComparison.Ordinal));
        Assert.Equal("spotify:track:only", proj.CurrentTrack?.Uri);
        Assert.False(proj.CanSkipPrev);
    }

    [Fact]
    public async Task GhostResume_EmptyCluster_RestoresTheLocalSnapshotPaused_AndNeverAutoplays()
    {
        // §8 — the empty-cluster launch fallback. The snapshot restores the identity/position/options; playback stays
        // paused (a launch must never start music) and the user queue survives the restart.
        using var c = Make(out var host, out var proj, out _, ctx: Ctx("spotify:track:a", "spotify:track:b"));
        c.RestoreSnapshot = () => new PlaybackSessionSnapshot(
            ContextUri: "spotify:playlist:saved",
            CurrentUri: "spotify:track:b",
            CurrentUid: "",
            CurrentIndex: 1,
            PositionMs: 33_000,
            Shuffle: true,
            Repeat: RepeatMode.Context,
            UserQueue: [new QueuedRef("spotify:track:queued", "")],
            AutoplayActive: false);

        await c.ResumeAsync();   // nothing in the cluster → the snapshot path

        Assert.True(await Settle(() => proj.CurrentTrack?.Uri == "spotify:track:b"),
            "the snapshot restore did not land on the saved current; got " + proj.CurrentTrack?.Uri);
        Assert.False(proj.IsPlaying);
        Assert.DoesNotContain("play", host.Calls);
        Assert.True(proj.IsShuffle);
        Assert.Equal(RepeatMode.Context, proj.Repeat);
    }

    // ── the cluster echo of OUR OWN masked publish (ConnectUriMask) ──────────────────────────────────────────────────

    [Fact]
    public async Task SessionRecovery_ClusterCurrentNothingCanPlay_IsSkipped_AndTheLocalSnapshotWins()
    {
        // After a non-Spotify session (a playback-module link), what the cluster holds at the next launch is our own
        // publish AFTER masking: context `wavee:module:…` with a `spotify:local:…` DISPLAY row where the playable was.
        // Seeding that asked Spotify to resolve a track that does not exist ("Restricted: no TRACK_V4 extension") and to
        // resolve a context it has never heard of (400) — two error lines before the user touched anything. The real
        // playable is in the local snapshot, and that is what must be restored.
        const string moduleUri = "wavee:module:wavee.youtube:dFJzUXNUTXZQTmc";
        using var c = Make(out var host, out var proj, out _, ctx: Ctx(moduleUri));
        var errors = new List<PlaybackErrorInfo>();
        c.OnPlaybackError = errors.Add;
        c.IsPlayableHere = uri => !uri.StartsWith("spotify:local:", StringComparison.Ordinal);
        c.RestoreSnapshot = () => new PlaybackSessionSnapshot(
            ContextUri: moduleUri, CurrentUri: moduleUri, CurrentUid: "", CurrentIndex: 0,
            PositionMs: 0, Shuffle: false, Repeat: RepeatMode.Off,
            UserQueue: Array.Empty<QueuedRef>(), AutoplayActive: false);

        proj.OnCluster(Cluster("", Remote("spotify:local:Claude::Claude+FM:0"), pos: 5_000) with
        {
            ContextUri = moduleUri,
        });

        Assert.True(await Settle(() => proj.CurrentTrack?.Uri == moduleUri),
            "the masked cluster row was seeded instead of the real playable; current = " + proj.CurrentTrack?.Uri);
        Assert.False(proj.IsPlaying);                       // a restore is paused, always
        Assert.Empty(errors);                               // and silent — no error toast at launch
        Assert.DoesNotContain(host.Calls, x => x.Contains("spotify:local:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionRecovery_ClusterCurrentNothingCanPlay_AndNoSnapshot_StaysColdAndSilent()
    {
        using var c = Make(out var host, out var proj, out _);
        var errors = new List<PlaybackErrorInfo>();
        c.OnPlaybackError = errors.Add;
        c.IsPlayableHere = uri => !uri.StartsWith("spotify:local:", StringComparison.Ordinal);

        proj.OnCluster(Cluster("", Remote("spotify:local:Claude::Claude+FM:0"), pos: 5_000));
        await Task.Delay(120);

        Assert.False(c.HasLocalSession);
        // "Cold" = nothing was loaded or started. The first cluster fold always echoes the ACTIVE device's volume down to
        // the host (OnProjectionChanged, _lastVolume starts at -1), which is orthogonal to session recovery.
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("load:", StringComparison.Ordinal)
                                            || x.StartsWith("faststart:", StringComparison.Ordinal)
                                            || x == "play");
        Assert.Empty(errors);
    }

    [Fact]
    public async Task SessionRecovery_WithoutTheHook_StillSeedsEveryClusterCurrent()
    {
        // The seam is additive: an unwired IsPlayableHere (unit tests, the fake bootstrap) trusts every uri exactly as
        // before, so nothing about the Spotify recovery path moved.
        using var c = Make(out _, out var proj, out _);

        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 42_000));

        Assert.True(await Settle(() => proj.CurrentTrack?.Uri == "spotify:track:ghost"));
    }

    // ── re-hydrating a playable NO CATALOGUE OWNS through its owner (the module host) ─────────────────────────────────
    // A restart used to bring a playing YouTube broadcast back as its own raw uri: no title, no artwork, no artists, a
    // length left over from whatever the media engine last measured, and no live-ness. Nothing persisted or echoed by
    // Connect can carry a module's display facts, so the only honest source is the module itself.

    const string ModuleUri = "wavee:module:wavee.youtube:dFJzUXNUTXZQTmc";

    /// <summary>What the module answers with — deliberately carrying a LENGTH, so the restore's "a module playable is
    /// restored with no duration" rule is proved rather than accidentally satisfied.</summary>
    static Track ModuleAnswer() => new(
        "dFJzUXNUTXZQTmc", ModuleUri, "Claude FM — 24/7 lofi",
        new[] { new ArtistRef("", "", "Anthropic") }, new AlbumRef("", "", ""),
        DurationMs: 206_000, IsExplicit: false, Image: new Image("https://img.example/hq.jpg"),
        Origin: TrackOrigin.Streamed, Availability: Availability.Playable, Source: "module:wavee.youtube");

    [Fact]
    public async Task SnapshotRestore_ModulePlayable_IsReHydratedThroughItsOwner_WithNoDuration()
    {
        // The context resolve misses (Spotify has never heard of a `wavee:module:` context) and the hydrator can only
        // answer with the uri-only placeholder — so the OWNER is asked, and its answer becomes the restored row.
        var ctx = new FakeContextResolver { HydrateAsPlaceholder = true };
        using var c = Make(out var host, out var proj, out _, ctx: ctx);
        var errors = new List<PlaybackErrorInfo>();
        c.OnPlaybackError = errors.Add;
        var asked = new List<string>();
        c.HydratePlayable = (uri, _) => { asked.Add(uri); return Task.FromResult<Track?>(ModuleAnswer()); };
        c.RestoreSnapshot = () => new PlaybackSessionSnapshot(
            ContextUri: ModuleUri, CurrentUri: ModuleUri, CurrentUid: "", CurrentIndex: 0,
            PositionMs: 0, Shuffle: false, Repeat: RepeatMode.Off,
            UserQueue: Array.Empty<QueuedRef>(), AutoplayActive: false);

        await c.ResumeAsync();   // nothing in the cluster → the snapshot path

        Assert.True(await Settle(() => proj.CurrentTrack?.Uri == ModuleUri),
            "the module playable was not restored; current = " + proj.CurrentTrack?.Uri);
        Assert.Equal(new[] { ModuleUri }, asked.ToArray());
        var restored = proj.CurrentTrack!;
        Assert.Equal("Claude FM — 24/7 lofi", restored.Title);           // …not the raw uri
        Assert.Equal("Anthropic", Assert.Single(restored.Artists).Name);
        Assert.NotNull(restored.Image);
        // THE STALE LENGTH. A broadcast has none, and the number the owner happened to state is not the restored row's
        // business: a restored module playable always carries 0 (the LIVE rail's own contract).
        Assert.Equal(0, restored.DurationMs);
        Assert.False(proj.IsPlaying);                 // a restore is paused, always
        Assert.DoesNotContain("play", host.Calls);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task SnapshotRestore_OwnerCannotAnswer_DegradesToTheThinRow_SilentlyAndPaused()
    {
        // Offline, or the module was uninstalled between launches. That is not an error the user should see at launch:
        // the placeholder survives, the session is still restored, and nothing plays.
        var ctx = new FakeContextResolver { HydrateAsPlaceholder = true };
        using var c = Make(out var host, out var proj, out _, ctx: ctx);
        var errors = new List<PlaybackErrorInfo>();
        c.OnPlaybackError = errors.Add;
        c.HydratePlayable = (_, _) => throw new InvalidOperationException("module process is not running");
        c.RestoreSnapshot = () => new PlaybackSessionSnapshot(
            ContextUri: ModuleUri, CurrentUri: ModuleUri, CurrentUid: "", CurrentIndex: 0,
            PositionMs: 0, Shuffle: false, Repeat: RepeatMode.Off,
            UserQueue: Array.Empty<QueuedRef>(), AutoplayActive: false);

        await c.ResumeAsync();

        Assert.True(await Settle(() => proj.CurrentTrack?.Uri == ModuleUri),
            "a declining owner took the restore down with it; current = " + proj.CurrentTrack?.Uri);
        Assert.Equal(ModuleUri, proj.CurrentTrack!.Title);   // the thin row, exactly as before
        Assert.False(proj.IsPlaying);
        Assert.DoesNotContain("play", host.Calls);
        Assert.Empty(errors);                                // Info, never a toast
    }

    [Fact]
    public async Task SnapshotRestore_ASpotifyUri_NeverAsksTheOwner()
    {
        // The catalogue owns `spotify:` playables. Asking a module about one would be a guaranteed miss on every launch.
        using var c = Make(out _, out var proj, out _, ctx: Ctx("spotify:track:a", "spotify:track:b"));
        int asked = 0;
        c.HydratePlayable = (_, _) => { Interlocked.Increment(ref asked); return Task.FromResult<Track?>(null); };
        c.RestoreSnapshot = () => new PlaybackSessionSnapshot(
            ContextUri: "spotify:playlist:saved", CurrentUri: "spotify:track:b", CurrentUid: "", CurrentIndex: 1,
            PositionMs: 0, Shuffle: false, Repeat: RepeatMode.Off,
            UserQueue: [new QueuedRef("spotify:track:queued", "")], AutoplayActive: false);

        await c.ResumeAsync();

        Assert.True(await Settle(() => proj.CurrentTrack?.Uri == "spotify:track:b"));
        Assert.Equal(0, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task SessionRecovery_ClusterModulePlayable_IsReHydratedThroughItsOwner_WithNoDuration()
    {
        // The cluster half of the same defect: what a cluster row holds for a module uri is OUR OWN publish from a
        // previous session — a title and a duration we wrote down once. The owner is re-asked before it becomes the
        // restored now-playing, so the seeded row is never last session's stale display data.
        using var c = Make(out var host, out var proj, out _, ctx: Ctx());
        var errors = new List<PlaybackErrorInfo>();
        c.OnPlaybackError = errors.Add;
        c.IsPlayableHere = _ => true;
        var asked = new List<string>();
        c.HydratePlayable = (uri, _) => { asked.Add(uri); return Task.FromResult<Track?>(ModuleAnswer()); };

        proj.OnCluster(Cluster("", Remote(ModuleUri, dur: 206_000), pos: 0) with { ContextUri = ModuleUri });

        Assert.True(await Settle(() => proj.CurrentTrack?.Title == "Claude FM — 24/7 lofi"),
            "the cluster row was seeded verbatim; title = " + proj.CurrentTrack?.Title);
        Assert.Contains(ModuleUri, asked);
        Assert.Equal(0, proj.CurrentTrack!.DurationMs);
        Assert.Equal("Anthropic", Assert.Single(proj.CurrentTrack!.Artists).Name);
        Assert.False(proj.IsPlaying);                 // seeded, never started
        Assert.DoesNotContain("play", host.Calls);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task GhostResume_EmptyCluster_WithNoSnapshot_StaysAQuietNoOp()
    {
        using var c = Make(out var host, out var proj, out var outbound);

        await c.ResumeAsync();
        await Task.Delay(60);

        Assert.Empty(host.Calls);
        Assert.Null(proj.CurrentTrack);
        Assert.Empty(outbound.Sent);
    }
}
