using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.MediaSources;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Gabo;

namespace Wavee.SpotifyLive;

// ── Stage 0 + H — the live Connect+playback composition (the session glue) ────────────────────────────────────────────
// Wires the whole control plane + bidirectional projection + controller + silent host + resolver onto a single dealer
// transport, and exposes the Wavee.Core seams (IPlaybackPlayer / IPlaybackState / IConnectDevices) the UI binds to. This is
// the "LiveSession owner": it captures the connection_id + announces the device, ingests cluster state, routes inbound
// commands to the controller, forwards outbound commands when another device is active, drives a SILENT host for local
// playback (real audio is the deferred host behind the same IAudioHost seam), and resolves tracks (CDN + key).
//
// ONE AP connection: the persistent AP channel for audio keys is the LOGIN socket, adopted by SpotifyLiveSpclient and
// passed in here — there is NO second handshake. If it's null (couldn't be retained), the resolver falls back gracefully.
public sealed partial class LiveConnect : IDisposable
{
    public NowPlayingProjection Projection { get; }
    public LiveConnectDevices Devices { get; }
    public PlaybackController Controller { get; }
    public AudioPlaybackStack? Audio => _audio;
    /// <summary>The dealer connection id (null until the first pusher hello / after a reconnect drop). Replays last value.</summary>
    public IObservable<string?> ConnectionId => _connect.ConnectionId;
    /// <summary>The current dealer connection id, or null if none has been captured yet.</summary>
    public string? CurrentConnectionId => _connect.CurrentConnectionId;
    /// <summary>Track uri → associated music-video gid, for the connect-state video parity (see
    /// <see cref="ConnectStateBuilder.AssociatedVideoGid"/>). Wired at go-live to the video-association store.</summary>
    public Func<string, string?>? AssociatedVideoGid
    {
        get => _stateBuilder.AssociatedVideoGid;
        set => _stateBuilder.AssociatedVideoGid = value;
    }
    /// <summary>The current OS render-endpoint friendly name for <c>DeviceInfo.audio_output_device_info</c> (see
    /// <see cref="ConnectStateBuilder.AudioOutputDeviceName"/>). Wired at go-live to the local-output picker service.</summary>
    public Func<string?>? AudioOutputDeviceName
    {
        get => _stateBuilder.AudioOutputDeviceName;
        set => _stateBuilder.AudioOutputDeviceName = value;
    }
    /// <summary>The output-device control for the LOCAL media stack — the audio host's own control with mute additionally
    /// fanned out to the video host (see <see cref="LocalMediaOutputControl"/>). Null when the wired audio host exposes no
    /// device control (the silent/fake backend), which is exactly the case where the picker/mute affordances stay hidden.
    /// The composition root wires the picker service and the notice/volume subscriptions to THIS, never to the audio host
    /// directly, so a mute can never be lost at a video boundary.</summary>
    public IAudioOutputDeviceControl? OutputDeviceControl { get; }
    /// <summary>Re-publish the player state for a wire-visible change that produced no playback event (a music-video
    /// association landing under the playing track). One PutState, no host/kind change.</summary>
    public void RepublishPlayerState() => _publisher.PublishStateChanged();
    /// <summary>Re-announce this device to the cluster (a <c>NewConnection</c> PutState) after an OS resume — a sleeping
    /// machine's device registration is gone even when the socket looks alive. Not the same as
    /// <see cref="RepublishPlayerState"/>, which only reports transport state.</summary>
    public void AnnounceNewConnection() => _publisher.AnnounceNewConnection();
    /// <summary>Apply the global protected-video preference to the live session (and future loads).</summary>
    public void SetVideoQualityPreference(int height) => _videoHost.SetPreferredQuality(height);
    /// <summary>Re-evaluate the metered Auto cap for the live protected-video session.</summary>
    public void RefreshVideoQualityPolicy() => _videoHost.RefreshQualityPolicy();
    readonly ConnectService _connect;
    readonly DeviceStatePublisher _publisher;
    readonly ConnectStateBuilder _stateBuilder;
    readonly ClusterIngest _ingest;
    readonly ConnectCommandRouter _commands;
    readonly IAudioHost _host;
    readonly FluentVideoMediaHost _videoHost;   // the VIDEO half of the ONE current media (Milestone B) — OWNS the player
    readonly WaveeLogger _playbackLog;
    Action<FluentGpu.Media.MediaPlayer?>? _onVideoPlayerChanged;   // the wired PlayerChanged relay (detached on Dispose)
    Action<string, long>? _onVideoDurationKnown;                   // the wired DurationKnown relay (detached on Dispose)
    Action<string, Wavee.Core.LiveWindow>? _onVideoLiveWindow;     // the wired LiveWindowKnown relay (detached on Dispose)
    readonly SpotifyServerClock _clock;   // server-clock skew estimator → corrects remote-position aging
    readonly ApConnection? _apChannel;   // owned: the adopted login socket
    readonly AudioPlaybackStack? _audio; // optional local-audio stack (null = silent/stub resolver)
    readonly RawCoreStreamProjection? _gabo;
    readonly ResumePointProjection? _resume;
    readonly GaboBatcher? _gaboBatcher;
    readonly Wavee.Backend.Modules.ModuleHost? _modules;              // the app-level module host (pre-login composed)
    readonly Wavee.Backend.Modules.ModuleProjectionRelay _moduleRelay;
    readonly Wavee.Backend.Modules.ModuleReloadPolicy _moduleReload = new();
    Action<string, string?>? _onLiveMetadata;                        // the ICY StreamTitle relay (detached on Dispose)

    /// <param name="hydrator">THE hydration façade the now-playing projection upgrades a thin cluster row through
    /// (design §1.5) — REQUIRED and POSITIONAL, never an optional tail parameter. The `?? NotOwnedEntityHydrator.Instance`
    /// coalesce this used to carry is the exact shape wiring-discipline forbids: a composition-root miss became a session
    /// where every now-playing upgrade silently answered "Unsupported".</param>
    /// <param name="store">The store the upgraded row is read back from — REQUIRED for the same reason. The one caller
    /// with genuinely no backend (SpotifyLibrarySync's CLI sync demo) passes the named stand-ins itself.</param>
    public LiveConnect(ITransport transport, string deviceId, ApConnection? apChannel,
        IEntityHydrator hydrator, IStore store,
        IContextResolver? contexts = null, WaveeLogger log = default,
        AudioPlaybackStack? audio = null, double initialVolume01 = 0.7,
        Func<CancellationToken, Task<string>>? refreshTokens = null, IAppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(hydrator);
        ArgumentNullException.ThrowIfNull(store);
        _apChannel = apChannel;
        _audio = audio;
        var playbackLog = log.With("playback");
        _playbackLog = playbackLog;
        var telemetryLog = log.With("telemetry");

        // Server-clock estimator: probes GET /melody/v1/time over the authenticated spclient pipeline; its corrected
        // "server now" feeds the projection's remote-position aging (the offset-dependent transit term).
        _clock = new SpotifyServerClock(ct => FetchServerTimeMs(transport, ct), log);
        // The projection upgrades a thin now-playing row through THE façade and re-reads the store (design §1.5). Both
        // seams arrive REQUIRED (see the ctor params) — no coalesce, no null seam anywhere on this path.
        Projection = new NowPlayingProjection(deviceId, hydrator, store,
            serverNowUnixMs: _clock.ServerNowUnixMs, initialVolume01: initialVolume01);
        Devices = new LiveConnectDevices();
        _ingest = new ClusterIngest(transport, Projection, Devices, deviceId, log, _clock.ObservePassive);

        // The NewConnection announce (OnConnectionId, below) fires with no snapshot yet, so BuildPutState falls back to
        // whatever DeviceInfo.Volume the builder was CONSTRUCTED with — leaving the ctor default (MaxVolume/2) meant an
        // idle Wavee always announced 50% until the first real volume publish overwrote it.
        var builder = new ConnectStateBuilder(deviceId, "Wavee",
            volume: (int)Math.Round(Math.Clamp(initialVolume01, 0, 1) * ConnectStateBuilder.MaxVolume),
            isPrivateSession: () => Projection.IsPrivateSession);
        _stateBuilder = builder;
        _connect = new ConnectService(transport);   // connection-id capture only
        // bug 6 / M0: the current track's Connect `track_player` must come from the controller's LIVE media kind (the ONE
        // truth about which host is playing), not from a bare has-video flag on the wire snapshot. The controller does not
        // exist yet here, so the publisher's build delegate reads it through this late-bound thunk (Audio until then).
        // bug 2: gated on IsActiveOwner() — null (not Audio) while we are merely a passive VIEWER mirroring another
        // device's row, so BuildPutState falls back to the wire's own track_player instead of stamping our idle
        // "Audio" over an explicit remote claim of "video". Only a device that is ACTUALLY decoding is authoritative.
        Func<PlayableKind?>? mediaKind = null;
        // The SINGLE PutState writer: NewConnection announce on the connection-id + our local player_state on playback
        // changes (so other devices/controllers see us as the active player). Re-injects the response cluster.
        _publisher = new DeviceStatePublisher(transport, deviceId, Projection, _connect.ConnectionId, () => _connect.CurrentConnectionId,
            (reason, snap, mid, isActive, attribution) => builder.BuildPutState(reason, snap, mid, isActive,
                currentKind: mediaKind?.Invoke(),
                lastCommandSentByDeviceId: attribution.SenderDeviceId,
                lastCommandMessageId: attribution.MessageId),
            onCluster: _ingest.OnAnnounceResponse, log: log)
        {
            CurrentMediaKind = () => mediaKind?.Invoke(),
        };

        _host = audio is not null ? audio.Host : new SilentAudioHost();
        // The video-media host: the VIDEO half of the ONE current media. Constructed regardless of the audio backend (it is
        // self-contained — a resolved PopOutVideoSource carries its own descriptor/relay), so the SilentAudioHost path still
        // has a real video host available for the swap.
        settings ??= AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        _videoHost = new FluentVideoMediaHost(playbackLog, settings: settings);
        // Mute is a property of the CURRENT MEDIA, not of its audio half: the player bar / picker set it through
        // IAudioOutputDeviceControl, which only the audio host implements, so a mute set while (or before) a music video is
        // the current media used to be dropped on the floor. The composite routes it to both hosts; everything else is the
        // audio host's control verbatim. Null on the silent/fake backend, where no device affordances are shown at all.
        OutputDeviceControl = _host is IAudioOutputDeviceControl audioDeviceControl
            ? new LocalMediaOutputControl(audioDeviceControl, _videoHost)
            : null;
        var resolver = audio?.TrackResolver ?? (ITrackResolver)new StubTrackResolver();
        // Instant-start: when the local-audio stack is present, resolve head+key in parallel and start on the clear head.
        var fast = audio is not null
            ? new FastTrackPlayback(audio.TrackResolver, audio.HeadClient, playbackLog, audio.TrackResolver.InvalidateCdn)
            : null;
        // Source-agnostic playable seams: resolve / warm / wire-meta all dispatch through the provider registry, which routes
        // by uri ownership — no code between play-intent and the host inspects the scheme any more. The fake/silent bootstrap
        // has no live resolver: it keeps the stub resolver wired directly and leaves every registry-driven hook null
        // (= today's behavior).
        //
        // REGISTRATION ORDER IS THE ROUTING TABLE (first Owns wins): Spotify first because it is every hot playable, then the
        // two engine-free local sources. Those two are constructed UNCONDITIONALLY — they need no session, no network and no
        // credentials, which is precisely what makes them the validation cases for the seam.
        //
        // …and the PLAYBACK MODULES come last, with their provider instances taken VERBATIM from the app-level
        // ModuleHost (composed pre-login in Services). Registration order is the routing table, so go-live is exactly
        // "prepend Spotify to the table the app already had" — the same ModuleMediaProvider objects keep serving, and a
        // module link that was playing before sign-in keeps routing to the very provider that resolved it.
        MediaProviderRegistry? media = audio?.TrackResolver is { } liveResolver && fast is not null
            ? new MediaProviderRegistry(BuildProviderTable(new SpotifyMediaProvider(liveResolver, fast)))
            : null;
        var outbound = new LiveOutboundControl(transport, deviceId, () => _connect.CurrentConnectionId);
        var gaboCtx = GaboContextFactory.Create();
        var gaboSeq = settings.Get(WaveeSettings.GaboGlobalSequence);
        _gaboBatcher = new GaboBatcher(transport, gaboCtx, initialSequenceNumber: gaboSeq, refreshTokens: refreshTokens,
            persistSequence: seq => settings.Set(WaveeSettings.GaboGlobalSequence, seq), log: telemetryLog);
        _gabo = new RawCoreStreamProjection(_gaboBatcher, () => Projection.ContextUri, () => true);
        var herodotus = new HerodotusClient(transport, telemetryLog);
        _resume = new ResumePointProjection(herodotus, () => Projection.IsPrivateSession, telemetryLog);
        Controller = new PlaybackController(_host, media ?? resolver, Projection,
            contexts ?? EmptyContextResolver.Instance,
            deviceId, outbound, new IPlaybackProjection[] { _gabo, _resume, _publisher }, playbackLog,
            SpotifyClientIdentity.XpuiSnapshotVersion,   // play_origin.feature_version
            fast: (IFastTrackResolver?)media ?? fast, videoHost: _videoHost,
            transferDecoder: new ProtoTransferStateDecoder());
        // close the late-bound `track_player` loop (see above): null while merely viewing another device's session.
        mediaKind = () => Controller.IsActiveOwner() ? Controller.CurrentMediaKind : (PlayableKind?)null;
        Controller.EpisodeResumeMicros = (uri, ct) => herodotus.TryGetEpisodeResumeMicrosAsync(uri, ct);
        // Same corrected clock the projection's own remote-position aging reads — an inbound transfer ages its
        // restored position against it too, rather than our own (possibly unsynced/skewed) wall clock.
        Controller.ServerNowUnixMs = _clock.ServerNowUnixMs;
        Controller.PublishInactiveOnWire = _publisher.PublishInactive;
        Controller.PublishFreshStateOnWire = _publisher.PublishStateChanged;
        if (media is not null)
        {
            Controller.MetaResolver = media.ResolveWireMetaAsync;
            Controller.CanPrepareNext = t => media.SupportsPreparedNext(t.Uri);
            // CONNECT MASKING ON (P4). Now that non-Spotify playables exist, the publisher must stop putting uris on the
            // cluster that no remote controller can resolve: publishable ones (Spotify's) go verbatim, everything else is
            // rewritten into Spotify's own self-describing local-file namespace. The mask touches ONLY the uri field —
            // the QueueEntry uid is untouched, so skip_to/remove from a controller still address the right row.
            _publisher.PublishUriMask = ConnectUriMask.For(media);
            // …and the CONTEXT with it. A masked row under an unresolvable `context_uri` is still an unresolvable state:
            // the connect-state service will not adopt us as the cluster's active device, and the state it echoes back
            // then reads to us as somebody else's (see ConnectUriMask.LocalFilesContext).
            _publisher.PublishContextMask = ConnectUriMask.ContextMask;
            // "Can anything here play this uri?" — the launch-recovery paths ask before seeding a cluster/snapshot current,
            // so our OWN masked publish (`spotify:local:…`, a display shape) is never handed to a resolver.
            Controller.IsPlayableHere = uri => media.OwnerOf(uri) is not null;
        }
        // A module playable resolved as VIDEO has no audio body at all: pin it to the video host so no audio-first rule
        // (Connect, launch restore) and no surface-intent edge can re-host it onto a decoder that can only refuse it.
        Controller.IsVideoOnlyPlayable = Wavee.Backend.Modules.ModulePlayables.HasVideo;
        // …and the OWNER of a non-catalogue uri answers what the playable IS. A restart has nothing to restore a module
        // playable from — no catalogue to ask, and the cluster only ever holds our own masked publish — so without this
        // a YouTube broadcast came back as its raw uri with no art, no artists and a stale length. Re-resolving through
        // the module restores the display facts AND fills ModulePlayables, so has-video / live-ness are known at restore
        // time rather than one beat later (the ModuleProjectionRelay then states the live override).
        Controller.HydratePlayable = HydrateModulePlayableAsync;

        // ── the MODULE facts on the now-playing projection ────────────────────────────────────────────────────────
        // LIVE-ness follows the current playable, a module's `playback/metadata` becomes the now-playing override, and
        // an expired locator asks for a reload. Identical wiring pre-login (Services) and live (here) — one relay.
        _modules = Wavee.Backend.Modules.ModuleHost.Current;
        _moduleRelay = Wavee.Backend.Modules.ModuleProjectionRelay.Attach(Projection, _modules);
        _moduleRelay.CurrentPlayableExpired += OnModulePlayableExpired;

        // The station's own in-band "now playing" line (the ICY StreamTitle block). The audio host discovers this
        // capability by interface — the IAudioDspControl precedent — so a host without it simply never fires.
        if (_host is ILiveMetadataSource liveMeta)
        {
            _onLiveMetadata = (raw, station) => _moduleRelay.OnLiveStreamTitle(raw, station);
            liveMeta.MetadataKnown += _onLiveMetadata;
        }

        _commands = new ConnectCommandRouter(
            transport,
            (cmd, ct) => Controller.HandleRemoteCommandAsync(cmd, ct),
            (volume, ct) => Controller.HandleInboundVolumeAsync(volume, ct),
            log,
            // findings §9: an inbound connect/volume MESSAGE carries only ITS OWN message_id (no sender device id on
            // that wire, unlike a JSON command) — note it on the publisher before the dispatch above applies the
            // volume and emits VolumeChanged, so the resulting PutState's LastCommandMessageId credits the sender
            // that actually moved the slider instead of publishing with none.
            onVolumeMessageId: mid => _publisher.NoteCommand(new ConnectCommandAttribution("", mid, "")));
        Devices.TransferHandler = (id, c) => Controller.TransferToAsync(id, c);
        _clock.Start();
    }

    /// <summary>The go-live routing table: Spotify first (every hot playable), then the two engine-free local sources,
    /// then one provider per installed playback module — the SAME instances the app-level host owns.</summary>
    static IPlayableMediaProvider[] BuildProviderTable(SpotifyMediaProvider spotify)
    {
        var modules = Wavee.Backend.Modules.ModuleHost.Current?.Providers ?? Array.Empty<IPlayableMediaProvider>();
        var table = new List<IPlayableMediaProvider>(3 + modules.Count)
        {
            spotify,
            new LocalFileMediaProvider(probeDurationMs: LocalAudioDurationProbe.Probe),
            new GenericMediaProvider(probeDurationMs: LocalAudioDurationProbe.Probe),
        };
        table.AddRange(modules);
        return table.ToArray();
    }

    /// <summary>The <c>PlaybackController.HydratePlayable</c> impl: re-hydrate a PLAYBACK-MODULE playable through the
    /// module that owns it. Anything else (a local file, a generic media url, an unrecognised scheme) answers null and
    /// keeps the caller's own row. Throwing is the contract for "the owner could not answer" — the controller logs it at
    /// Info and falls back to the placeholder, so a launch while offline stays silent.</summary>
    static async Task<Track?> HydrateModulePlayableAsync(string uri, CancellationToken ct)
    {
        if (Wavee.Backend.Modules.ModuleHost.Current is not { } host) return null;
        if (!Wavee.Sdk.ModuleUri.TryDecode(uri, out string moduleId, out string playableId)) return null;
        // force:false — a cache hit from earlier in this launch is exactly as good an answer, and a live locator that
        // is still valid should not be thrown away to restore a paused session that nobody has pressed play on yet.
        Wavee.Sdk.ResolvedPlayable resolved = await host.ResolveAsync(uri, force: false, ct).ConfigureAwait(false);
        return LocalPlayables.ForModule(moduleId, playableId, resolved.Title, resolved.Form,
            resolved.Artists, resolved.ArtworkUrl);
    }

    // ── M0 — "one media, one host, one player": the app-level video hooks ─────────────────────────────────────────────────
    // This is the wiring the old TODO(B-wire) described. It lives here (not in Backend/) precisely because the controller's
    // hooks are DELEGATES: PlaybackController never references PlaybackBridge, the SpotifyLive video types, or FluentGpu.
    //
    //   ShouldPlayAsVideo      → the bridge's derived per-track predicate (the ONE VideoPlacementLogic.VideoActive rule, read
    //                            without subscribing), so turning video off / dismissing it routes the media back to audio.
    //   LoadCurrentVideoAsync  → the bridge's existing resolve path, then FluentVideoMediaHost.LoadVideo — the HOST builds and
    //                            owns the engine MediaPlayer; the mounted surface only presents it.
    //   PlayerChanged          → mirrored onto PlaybackBridge.VideoPlayer (UI-thread marshalled) so the surface re-binds.
    //   RequestMediaKindRefresh→ the controller's re-evaluation entry point, so a mid-track "watch video" takes effect NOW.
    /// <summary>Hand the controller the app-level video hooks (M0). Called ONCE by the live composition root
    /// (<c>LiveSessionHost</c>); idempotent.
    /// <para>KNOWN GAP, not a switch: a DRM music video has no audio track yet — the native CENC source demuxes video only
    /// (the manifest parser drops every audio profile; <c>ProtectedMediaBackend</c> advertises no audio voice), so the swap
    /// stops the song and the video plays silent. The milestone that demuxes the video's OWN audio is what makes this fully
    /// correct (<c>docs/plans/wavee-surfaces-placement-design.md</c>, M7).</para></summary>
    public void WireVideoMedia(PlaybackBridge bridge, VideoOverrideService? overrides = null)
    {
        if (_onVideoPlayerChanged is not null) return;

        // Eagerly preload the native PlayReady component (E2) so the FIRST real protected open does not pay for an
        // implicit LoadLibrary of the native CDM + its MF/PlayReady dependency chain. WireVideoMedia is called ONCE,
        // right as the live session comes up — well before the user is likely to open a DRM video — and the call
        // itself is documented idempotent/non-blocking, so firing it here needs no extra guard beyond the one above.
        FluentGpu.WindowsApi.Media.PlayReady.ProtectedMediaBackend.WarmupNative();

        // 1. The per-playable video decision. The bridge folds the sticky intent × this track's has-video × the per-content
        //    dismissal through the one pure rule; a throwing predicate degrades to audio inside the controller.
        Controller.ShouldPlayAsVideo = track => bridge.ShouldPlayAsVideo(track);

        // 1b. Next-track video preload (A4): SchedulePreparedNext calls this when the UPCOMING playable will play as
        //     video, well before its track boundary — the bridge resolves (and CDN-warms) it in the background so the
        //     eventual video→video skip finds the memo already warm instead of paying the resolve+handshake tail after
        //     the boundary.
        Controller.PrefetchVideo = track => bridge.PrefetchVideoSource(track.Uri);

        // 2. The async source handoff. Reuses the bridge's resolve path (same resolver, same publish onto PopOutVideoSource so
        //    the surfaces key their content on the very source the host plays). Returns FALSE on "no playable video", which is
        //    the controller's signal to fall back to audio for this playable instead of leaving the user in silence.
        //    startAtMs carries a position across an audio→video switch. It rides the LOAD rather than a follow-up seek
        //    because the video player does not exist yet at the moment of the swap (the host serializes teardown→build→open),
        //    so a bare seek would be dropped — see VideoLoadRequest.
        Controller.LoadCurrentVideoAsync = async (track, startAtMs, ct) =>
        {
            var src = await bridge.ResolveVideoSourceForPlaybackAsync(track.Uri, ct).ConfigureAwait(false);
            if (src is null)
            {
                _playbackLog.Info($"no playable video source resolved for {track.Uri} - the controller will play it as audio");
                return false;
            }
            _videoHost.LoadVideo(src, startAtMs);   // the HOST builds/owns the player and raises PlayerChanged (relayed below)
            return true;
        };

        // 3. The player reaches the surfaces REACTIVELY (never a frozen field): the host raises PlayerChanged on its own
        //    thread, the bridge marshals it onto the UI thread as one atomic (player, generation) value, and the mounted
        //    MediaPlayerElement is keyed on that generation so it re-binds to the new instance.
        _onVideoPlayerChanged = p => bridge.NotifyVideoPlayerChanged(p);
        // 3b. THE DEAD-VIDEO EDGE. The controller is the only place that KNOWS video did not happen for a playable (it
        //     fell back to audio because nothing was playable, or the open failed and the recovery hook declined). Without
        //     this the placement model never learns: availability still says "this track has a video", so the mounted
        //     surface keeps showing its indeterminate loading poster over audio that is already playing, forever. The app
        //     latches THAT PLAYABLE (never the intent), so the next video-bearing track opens exactly as before.
        Controller.OnVideoMediaUnavailable = track => bridge.NotifyVideoMediaEnded(track.Uri);
        _videoHost.PlayerChanged += _onVideoPlayerChanged;
        // Seed only if a player somehow already exists (re-wire after a logout/login); the default binding is already "none",
        // so seeding null would bump the generation and re-render the surfaces for nothing.
        if (_videoHost.CurrentPlayer is { } existing) bridge.NotifyVideoPlayerChanged(existing);

        // 4. Every writer of the video INTENT asks the controller to re-evaluate the CURRENT playable's kind, so "watch video",
        //    "switch to audio", and the surface ✕ swap the media host for the track already playing — not only at the next
        //    track boundary. Fire-and-forget: it is a locked backend operation, never awaited from a UI handler.
        //    forceReloadIfVideo: an override attach/replace/remove changes the video SOURCE without changing the KIND, so the
        //    same-kind early return has to be overridden for that one case. clearConnectAudioFirst is ORTHOGONAL and travels
        //    separately: only an explicit user media intent may drop the remote playback ids (see the controller's remarks).
        bridge.RequestMediaKindRefresh = (forced, clearConnect) => _ = RefreshMediaKindAsync(forced, clearConnect);

        // 5. ENGINE-AUTHORITATIVE DURATION, for EVERY video source. Whatever is playing is a different edit from the audio
        //    track with its own length — a user-attached mp4, a Canvas loop, and a Spotify music video alike (a music video
        //    routinely runs longer than the song: intros, outros, alternate arrangements). The moment the media engine knows
        //    the real length it becomes the projection's truth for that playable, so the seek bar and the remaining-time
        //    readout stop mis-scaling against the catalog duration. Dropped again when video stops being the current media
        //    (PlaybackController.SwitchHost), so audio goes straight back to the catalog length.
        //    This used to be scoped to `local:video:` keys only, which is precisely why a music video showed the song's length.
        //    NoteDuration stays local-only: it persists a fact about a user's attached FILE, not about the now-playing edit.
        _onVideoDurationKnown = (key, ms) =>
        {
            if (Projection.CurrentTrack?.Uri is not { Length: > 0 } uri) return;
            Projection.SetDurationOverride(uri, ms);
            if (key.StartsWith(Wavee.Backend.VideoOverride.SourceKeyPrefix, StringComparison.Ordinal))
                overrides?.NoteDuration(uri, ms);
            _playbackLog.Info($"video duration adopted for {uri}: {ms} ms (source {key})");
        };
        _videoHost.DurationKnown += _onVideoDurationKnown;

        // 5b. ENGINE-AUTHORITATIVE LIVE-NESS, the same handoff for the fact a duration cannot express. A broadcast has
        //     no length, so the host suppresses DurationKnown for it entirely and publishes the media pipeline's own
        //     seekable window instead: that window is what the DVR rail scrubs inside, what re-arms CanSeek, and what
        //     tells the LIVE pill from the "GO LIVE −m:ss" button. Scoped to the CURRENT playable exactly like the
        //     duration override, and cleared by the projection at the next track change.
        _onVideoLiveWindow = (key, window) =>
        {
            if (Projection.CurrentTrack?.Uri is not { Length: > 0 } uri) return;
            Projection.SetLiveWindow(uri, window);
        };
        _videoHost.LiveWindowKnown += _onVideoLiveWindow;

        // 6. OPEN-FAILURE RECOVERY. A local attachment that exists but cannot be decoded must not become a dead end: peek the
        //    source the host was playing, and if it is an override, quarantine that exact (uri, key) for the session, drop the
        //    cached source, tell the user once, and answer TRUE so the controller re-runs the load — which now walks past the
        //    attachment to the official video, or to audio. Anything else answers FALSE, i.e. today's behavior byte-for-byte.
        Controller.TryRecoverVideoAsync = async (track, ct) =>
        {
            // A MODULE playable's video failed to open. The overwhelmingly likely cause is a dead locator (signed,
            // IP-bound, minutes-to-hours-lived), so the recovery is the same as the expiry path: re-resolve with
            // `force`, drop the cached source, and answer TRUE so the controller re-runs the load against the fresh
            // url. Bounded by ModuleReloadPolicy — after three tries the answer is FALSE and the controller faults
            // honestly rather than looping on a broadcast that has genuinely ended.
            if (_modules is { } host && Wavee.Sdk.ModuleUri.TryDecode(track.Uri, out _, out _))
            {
                if (!await _moduleReload.TryReResolveAsync(host, track.Uri, ct).ConfigureAwait(false))
                {
                    _playbackLog.Info($"module video for {track.Uri} could not be re-resolved - giving up");
                    return false;
                }

                bridge.InvalidateVideoSource(track.Uri);
                _playbackLog.Info($"module video for {track.Uri} re-resolved after a media failure - reloading");
                return true;
            }

            return RecoverAttachedVideo(track, bridge, overrides);
        };
    }

    // The user-attached-file half of TryRecoverVideoAsync, unchanged: quarantine that exact (uri, key) for the session,
    // drop the cached source, tell the user once, and answer TRUE so the controller walks past the attachment.
    bool RecoverAttachedVideo(Track track, PlaybackBridge bridge, VideoOverrideService? overrides)
    {
        if (overrides is null) return false;
        var key = bridge.PopOutVideoSource.Peek()?.Key;
        if (key is not { Length: > 0 } || !key.StartsWith(Wavee.Backend.VideoOverride.SourceKeyPrefix, StringComparison.Ordinal))
            return false;
        overrides.Quarantine(track.Uri, key);
        bridge.InvalidateVideoSource(track.Uri);   // else the reload would hand the same broken source straight back
        bridge.NotifyVideoOverrideUnplayable(track.Uri);
        _playbackLog.Info($"attached video {key} failed to open for {track.Uri} — quarantined for this session; falling back");
        return true;
    }

    // ── module locator expiry / failure → RE-RESOLVE, then reload ────────────────────────────────────────────────────
    // A module's media locator is short-lived by nature: a YouTube HLS url is signed, IP-bound and dies after ~6 h; a
    // Twitch playback token expires sooner. Two things say so — the module's own `playback/expired` notification (the
    // pre-emptive path, below) and a NETWORK failure from the media engine (the reactive path, folded into the video
    // recovery hook in WireVideoMedia). Both do the same thing: re-resolve with `force`, drop the cached source, and
    // let the controller reload — bounded to three attempts per playable so a genuinely dead broadcast faults honestly
    // instead of looping.
    void OnModulePlayableExpired(string playableUri) => _ = ReloadModulePlayableAsync(playableUri);

    async Task ReloadModulePlayableAsync(string playableUri)
    {
        if (_modules is not { } host) return;
        try
        {
            if (!await _moduleReload.TryReResolveAsync(host, playableUri).ConfigureAwait(false))
            {
                _playbackLog.Info($"module locator for {playableUri} expired and could not be re-resolved");
                return;
            }

            _playbackLog.Info($"module locator for {playableUri} re-resolved after expiry - reloading");
            await Controller.RefreshCurrentMediaKindAsync(forceReloadIfVideo: true, clearConnectAudioFirst: false)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _playbackLog.Info("module reload failed: " + ex.Message);
        }
    }

    async Task RefreshMediaKindAsync(bool forceReloadIfVideo = false, bool clearConnectAudioFirst = true)
    {
        try { await Controller.RefreshCurrentMediaKindAsync(forceReloadIfVideo, clearConnectAudioFirst).ConfigureAwait(false); }
        catch (Exception ex) { _playbackLog.Info("media-kind refresh failed: " + ex.Message); }
    }

    // Fetch the server's wall clock (Unix ms) over the authenticated spclient pipeline. GET /melody/v1/time → {"timestamp": ms}.
    // Tolerates a seconds-resolution payload (scaled to ms) so a unit change on the endpoint can't silently corrupt the offset.
    static async Task<long> FetchServerTimeMs(ITransport transport, CancellationToken ct)
    {
        var resp = await transport.Request(Channel.Spclient, "/melody/v1/time", default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return 0;
        using var doc = System.Text.Json.JsonDocument.Parse(resp.Body);
        if (!doc.RootElement.TryGetProperty("timestamp", out var t) || !t.TryGetInt64(out var ms) || ms <= 0) return 0;
        return ms < 100_000_000_000L ? ms * 1000 : ms;   // < ~1973 in ms ⇒ the payload was seconds; scale up
    }

    public void Dispose()
    {
        if (_onVideoPlayerChanged is { } relay) { try { _videoHost.PlayerChanged -= relay; } catch { } _onVideoPlayerChanged = null; }
        if (_onVideoDurationKnown is { } durRelay) { try { _videoHost.DurationKnown -= durRelay; } catch { } _onVideoDurationKnown = null; }
        if (_onVideoLiveWindow is { } liveRelay) { try { _videoHost.LiveWindowKnown -= liveRelay; } catch { } _onVideoLiveWindow = null; }
        if (_onLiveMetadata is { } metaRelay && _host is ILiveMetadataSource liveMetaSource)
        { try { liveMetaSource.MetadataKnown -= metaRelay; } catch { } _onLiveMetadata = null; }
        try { _moduleRelay.CurrentPlayableExpired -= OnModulePlayableExpired; } catch { }
        try { _moduleRelay.Dispose(); } catch { }
        try { Controller.DeactivateIfActiveOwner(); } catch { }   // best-effort clean is_active=false hand-off on logout
        _commands.Dispose();
        _publisher.Dispose();
        _resume?.Dispose();
        if (_gabo is not null) _ = _gabo.DisposeAsync().AsTask();
        if (_gaboBatcher is not null) _ = _gaboBatcher.DisposeAsync().AsTask();
        _connect.Dispose();
        _ingest.Dispose();
        Controller.Dispose();
        _clock.Dispose();
        _apChannel?.Dispose();
        Projection.Dispose();
        try { _videoHost.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _audio?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    }
}
