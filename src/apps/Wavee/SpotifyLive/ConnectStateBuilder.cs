using System;
using System.Collections.Generic;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Core;
using Wavee.Protocol.Media;
using Wavee.Protocol.Player;
// The UI defines an internal `enum PlayerState` in the parent Wavee namespace (PlayerBar.cs), which shadows the proto type
// here — alias the proto PlayerState explicitly so this stays unambiguous.
using ProtoPlayerState = Wavee.Protocol.Player.PlayerState;

namespace Wavee.SpotifyLive;

// Builds the connect-state PutStateRequest (DeviceInfo + Capabilities + PrivateDeviceInfo + PlayerState) → protobuf bytes.
// The DeviceInfo/Capabilities/supported-types are desktop-parity / anti-fraud values ported VERBATIM from the reference
// (decision #11): "spotify"/"PC laptop", license=premium (Recently-Played eligibility), NeedsFullPlayerState=true, 64 volume
// steps, the 14 supported_types, AudioQuality.VeryHigh. Changing them silently breaks Recently Played / can get us throttled.
// Proto-building lives in SpotifyLive (the wire boundary); the proto-free ConnectService orchestrates the PUT.
public sealed class ConnectStateBuilder
{
    // Single owner: Wavee.Backend.Audio.SpotifyRuntimeIdentity.KeymasterClientId.
    const string KeymasterClientId = Wavee.Backend.Audio.SpotifyRuntimeIdentity.KeymasterClientId;
    public const int MaxVolume = 65535;
    public const int DefaultVolumeSteps = 64;

    // The desktop client's 15 supported_types, verbatim from the captured PUT bodies (24/24 identical).
    static readonly string[] DesktopSupportedTypes =
    {
        "audio/ad", "audio/audio", "audio/episode", "audio/episode+track", "audio/interruption", "audio/local",
        "audio/media", "audio/podcast-chapter", "audio/track", "audio/user-highlight", "video/ad", "video/episode",
        "video/podcast-chapter", "video/track", "video/user-highlight",
    };

    // ── PutState parity constants, all proven byte-exact over the 24 decoded desktop PUT bodies ──────────────────────
    // The baseline `signals` set: present unconditionally in 24/24 PUTs (playing / paused / buffering, every context).
    // Emission order is NOT stable on the wire (the middle two swap on buffering PUTs) — only "switch-to-video first,
    // when present" is invariant, so this fixed order is within observed desktop behavior.
    internal static readonly string[] BaselineSignals = { "interact", "automix-preview", "speed-preview", "stop-speed-preview" };
    const string NotSupportedByContentType = "not_supported_by_content_type";
    internal const string ContextEnhancementMode = "context_enhancement";
    internal const string RecommendationMode = "RECOMMENDATION";
    // UNNAMED restriction reason on Restrictions#31 — always exactly this one entry, 24/24.
    internal const string AlreadySetReason = "already_set";
    // The three ContextPlayerOptions.modes entries; values constant in 24/24 (media stays "" even on video-offer PUTs).
    internal const string MediaMode = "media";
    internal const string JamMode = "jam";
    // AudioOutputDeviceInfo#5 — UNNAMED, always varint 3.
    internal const uint AudioOutputDeviceUnknown5 = 3;

    readonly string _deviceId;
    readonly string _deviceName;
    readonly string _clientId;
    readonly Func<bool> _isPrivateSession;
    readonly object _sessionCommandGate = new();
    string _sessionCommandSessionId = "";
    string _sessionCommandId = "";
    int _volume;

    public ConnectStateBuilder(string deviceId, string deviceName, string? clientId = null, int volume = MaxVolume / 2,
        Func<bool>? isPrivateSession = null)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _clientId = clientId ?? KeymasterClientId;
        _volume = Math.Clamp(volume, 0, MaxVolume);
        _isPrivateSession = isPrivateSession ?? (() => false);
    }

    public string DeviceId => _deviceId;
    public void SetVolume(int spotifyVolume) => _volume = Math.Clamp(spotifyVolume, 0, MaxVolume);

    /// <summary>Track uri → the associated music video's 32-hex gid (the video manifest id), or null when none is known.
    /// Wired at go-live to the video-association store. This is the ONE input behind the connect-state video parity: the
    /// current track's <c>associated_video_id</c> metadata entry and the <c>switch-to-video</c>/<c>switch-to-audio</c>
    /// signal/disallow pair. A bare has-video badge is deliberately NOT enough — the reference client only ever offers the
    /// switch together with the gid, so until a resolve/decode produces one we publish "no_associated_track".</summary>
    public Func<string, string?>? AssociatedVideoGid { get; set; }

    /// <summary>The OS render-endpoint friendly name we are currently outputting to, for <c>DeviceInfo
    /// .audio_output_device_info.device_name</c> (desktop publishes it in 24/24 PUTs). Wired at go-live to the local-output
    /// picker service; null / unwired / throwing degrades to an empty name — the submessage itself is still emitted with the
    /// constants desktop always sends ({type: UNKNOWN, #5: 3}), because those never varied with the endpoint. We deliberately
    /// do NOT ship the captured machine's endpoint string as a fallback: that would publish another box's hardware.</summary>
    public Func<string?>? AudioOutputDeviceName { get; set; }

    public DeviceInfo BuildDeviceInfo()
    {
        var info = new DeviceInfo
        {
            CanPlay = true,
            Volume = (uint)_volume,
            Name = _deviceName,
            DeviceId = _deviceId,
            DeviceType = DeviceType.Computer,
            DeviceSoftwareVersion = SpotifyClientIdentity.DeviceSoftwareVersion,
            ClientId = _clientId,
            SpircVersion = SpotifyClientIdentity.SpircVersion,
            Capabilities = BuildCapabilities(),
            Brand = "spotify",
            Model = "PC laptop",
            License = "premium",          // Recently-Played / play-count eligibility — load-bearing
            IsPrivateSession = _isPrivateSession(),
        };
        info.MetadataMap["debug_level"] = "1";
        info.MetadataMap["tier1_port"] = "0";
        // audio_output_device_info(24): desktop emits it in 24/24 PUTs as {1: UNKNOWN (explicitly serialized), 2: the OS
        // endpoint friendly name, 5: 3}. Both numeric fields are `optional` in the proto so the zero/const values are
        // written rather than defaulted away.
        info.AudioOutputDeviceInfo = new AudioOutputDeviceInfo
        {
            AudioOutputDeviceType = AudioOutputDeviceType.UnknownAudioOutputDeviceType,
            DeviceName = CurrentAudioOutputDeviceName(),
            UnknownField5 = AudioOutputDeviceUnknown5,
        };
        return info;
    }

    string CurrentAudioOutputDeviceName()
    {
        if (AudioOutputDeviceName is not { } lookup) return "";
        // A picker/enumeration fault must degrade to a nameless endpoint, never break the PUT (which takes us off the cluster).
        try { return lookup() ?? ""; }
        catch { return ""; }
    }

    static Capabilities BuildCapabilities(int volumeSteps = DefaultVolumeSteps)
    {
        var c = new Capabilities
        {
            CanBePlayer = true,
            GaiaEqConnectId = true,
            SupportsLogout = true,
            IsObservable = true,
            CommandAcks = true,
            SupportsRename = false,
            SupportsPlaylistV2 = true,
            IsControllable = true,
            SupportsExternalEpisodes = true,
            SupportsSetBackendMetadata = true,
            SupportsTransferCommand = true,
            SupportsCommandRequest = true,
            VolumeSteps = volumeSteps,
            SupportsGzipPushes = true,
            NeedsFullPlayerState = true,   // pull full cluster snapshots, not deltas — the projection reconciles from full
            SupportsSetOptionsCommand = true,
            SupportsHifi = new CapabilitySupportDetails { FullySupported = true, UserEligible = true, DeviceSupported = true },
            SupportsDj = true,
            SupportedAudioQuality = AudioQuality.VeryHigh,   // 320 kbps OGG (claiming HIFI would lie about FLAC)
            // UNNAMED capability bits 33/34/35/36/38 — varint 1 in 24/24 captured desktop PUTs (37 is never emitted).
            // Anti-fraud surface: matching desktop exactly is the goal, so we send the same five constants.
            UnknownCapability33 = true,
            UnknownCapability34 = true,
            UnknownCapability35 = true,
            UnknownCapability36 = true,
            UnknownCapability38 = true,
        };
        foreach (var t in DesktopSupportedTypes) c.SupportedTypes.Add(t);
        return c;
    }

    /// <summary>Serialize a PutStateRequest from OUR local playback snapshot (null = empty player_state, for the initial
    /// NewConnection announce). Matches the DeviceStatePublisher's builder delegate. <paramref name="nowMs"/> overridable
    /// for deterministic tests. <paramref name="currentKind"/> is the controller's LIVE media kind
    /// (<c>PlaybackController.CurrentMediaKind</c>) and makes the current track's <c>track_player</c> truthful — video iff the
    /// video host is what is actually playing. Null (the default) keeps the legacy per-track wire heuristic, for callers that
    /// genuinely do not know the kind.</summary>
    public byte[] BuildPutState(PutStateReasonKind reason, LocalPlaybackSnapshot? snap, uint messageId, bool isActive,
        long? nowMs = null, PlayableKind? currentKind = null,
        string? lastCommandSentByDeviceId = null, uint lastCommandMessageId = 0)
    {
        long ts = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (snap is { } sv) SetVolume((int)Math.Round(Math.Clamp(sv.Volume01, 0, 1) * MaxVolume));   // DeviceInfo.Volume from our live volume
        var ps = snap is { } s ? BuildPlayerState(s, ts, currentKind) : new ProtoPlayerState { Timestamp = ts };
        var req = new PutStateRequest
        {
            MemberType = MemberType.ConnectState,
            Device = new Device
            {
                DeviceInfo = BuildDeviceInfo(),
                PlayerState = ps,
                PrivateDeviceInfo = new PrivateDeviceInfo { Platform = SpotifyClientIdentity.GetPrivateDevicePlatform() },
            },
            IsActive = isActive,
            PutStateReason = reason switch
            {
                PutStateReasonKind.NewConnection => PutStateReason.NewConnection,
                PutStateReasonKind.VolumeChanged => PutStateReason.VolumeChanged,
                PutStateReasonKind.BecameInactive => PutStateReason.BecameInactive,
                _ => PutStateReason.PlayerStateChanged,
            },
            MessageId = messageId,
            ClientSideTimestamp = (ulong)ts,
        };
        if (snap is { } s2)
        {
            if (s2.StartedPlayingAtMs > 0) req.StartedPlayingAt = (ulong)s2.StartedPlayingAtMs;
        }
        if (!string.IsNullOrEmpty(lastCommandSentByDeviceId))
            req.LastCommandSentByDeviceId = lastCommandSentByDeviceId;
        if (lastCommandMessageId != 0)
            req.LastCommandMessageId = lastCommandMessageId;
        return req.ToByteArray();
    }

    ProtoPlayerState BuildPlayerState(LocalPlaybackSnapshot s, long ts, PlayableKind? currentKind = null)
    {
        string contextUri = s.ContextUri ?? "";
        string? videoGid = LookupVideoGid(s.Track.Uri);
        string feature = FeatureOf(contextUri, s.ContextMetadata);
        var ps = new ProtoPlayerState
        {
            Timestamp = ts,
            ContextUri = contextUri,
            ContextUrl = string.IsNullOrEmpty(contextUri) ? "" : "context://" + contextUri,
            PlayOrigin = new PlayOrigin
            {
                FeatureIdentifier = feature,
                FeatureVersion = SpotifyClientIdentity.XpuiSnapshotVersion,
                ReferrerIdentifier = feature,
            },
            PositionAsOfTimestamp = s.PositionMs,
            Duration = s.DurationMs,
            // Spotify desktop keeps is_playing=true while paused (transport engaged, audio frozen).
            IsPlaying = s.IsPlaying || s.IsPaused,
            IsPaused = s.IsPaused,
            PlaybackSpeed = s.IsPaused ? 0.0 : 1.0,
            PlaybackId = s.PlaybackId,
            SessionId = s.SessionId,
            QueueRevision = s.QueueRevision ?? "",
            // Only the CURRENT track carries the live media kind — prev/next are not the current media.
            Track = ToProvided(s.Track, contextUri, s.InteractionId, s.PageInstanceId, currentKind, videoGid,
                isCurrentTrack: true, currentPositionMs: s.PositionMs),
            Index = new ContextIndex { Track = (uint)Math.Max(0, s.ContextIndex) },
            Options = new ContextPlayerOptions
            {
                ShufflingContext = s.Shuffle,
                RepeatingContext = s.Repeat == RepeatMode.Context,
                RepeatingTrack = s.Repeat == RepeatMode.Track,
            },
            Restrictions = new Restrictions(),
            // Empty-but-present submessages: desktop emits both tags with no content in 24/24 PUTs.
            ContextRestrictions = new Restrictions(),
            Suppressions = new Suppressions(),
            // Per-session opaque id — the same value for every PUT of a playback session, a fresh one when session_id turns
            // over. The generator is UNPROVEN (nothing in the capture derives it), so we mint 128 random bits.
            SessionCommandId = SessionCommandIdFor(s.SessionId),
            // UNNAMED PlayerState#38 — always the bytes `c2 02 02 0a 00`, i.e. {1: ""}. The inner field is proto3-`optional`
            // so the empty string is explicitly serialized instead of being defaulted away.
            UnknownField38 = new PlayerStateUnknown38 { Unknown1 = "" },
            PlaybackQuality = new PlaybackQuality
            {
                BitrateLevel = BitrateLevel.High,
                Strategy = BitrateStrategy.CachedFile,
                TargetBitrateLevel = BitrateLevel.High,
                TargetBitrateAvailable = true,
                HifiStatus = HiFiStatus.Off,
            },
        };
        foreach (var (k, v) in s.ContextMetadata)
            if (!string.IsNullOrEmpty(k)) ps.ContextMetadata[k] = v ?? "";
        ps.ContextMetadata["player.arch"] = "2";
        if (s.IsPaused)
        {
            ps.Restrictions.DisallowPausingReasons.Add("already_paused");
            if (s.ContextIndex <= 0 && s.PrevTracks.Count == 0)
                ps.Restrictions.DisallowSkippingPrevReasons.Add("no_prev_track");
        }
        else if (s.IsPlaying)
            ps.Restrictions.DisallowResumingReasons.Add("not_paused");
        // ORDER MATTERS: the mode signal goes in FIRST — when "switch-to-video" is present desktop always puts it first
        // (7/7 captured video PUTs), ahead of the four unconditional baseline entries.
        StampModeSignals(ps, videoGid, currentKind);
        StampDesktopBaseline(ps, contextUri);
        foreach (var t in s.PrevTracks) ps.PrevTracks.Add(ToProvided(t, contextUri, s.InteractionId, s.PageInstanceId));
        foreach (var t in s.NextTracks) ps.NextTracks.Add(ToProvided(t, contextUri, s.InteractionId, s.PageInstanceId));
        return ps;
    }

    // Everything the reference desktop client publishes on EVERY PutState regardless of playback state, proven constant over
    // the 24 decoded PUT bodies. All of it is truthful for us as well: we support neither playback-speed change nor automix
    // nor jam, so desktop's disallow reasons are our reasons too, and the preview signals ride with those disallows.
    static void StampDesktopBaseline(ProtoPlayerState ps, string contextUri)
    {
        foreach (var signal in BaselineSignals) ps.Signals.Add(signal);

        // 25: playback speed is a podcast feature; every captured (music) PUT carries this one reason.
        ps.Restrictions.DisallowSettingPlaybackSpeedReasons.Add(NotSupportedByContentType);
        // 31: UNNAMED, always exactly ["already_set"] — never varies, so there is no state to key it off.
        ps.Restrictions.UnknownDisallowReasons31.Add(AlreadySetReason);
        // 28: Enhance ("context_enhancement" / RECOMMENDATION) is a playlist feature — desktop publishes it as disallowed on
        // album / album-radio / autoplay contexts and omits the field entirely on playlist contexts. Correlates with context
        // type only: independent of video and of playback state.
        if (!contextUri.Contains(":playlist:", StringComparison.Ordinal))
        {
            var reasons = new RestrictionReasons();
            reasons.Reasons.Add(NotSupportedByContentType);
            var mode = new ModeRestrictions();
            mode.Values[RecommendationMode] = reasons;
            ps.Restrictions.DisallowSettingModes[ContextEnhancementMode] = mode;
        }
        // 24 (disallow_add_to_queue_reasons) is deliberately NOT emitted: absent in 24/24 PUTs, no observed key or reason.

        // options.modes: exactly three entries, values constant in 24/24 (media stays "" even on the video-offer PUTs, so it
        // is not a current-media indicator). Desktop's own emission order varies across all six permutations — not
        // load-bearing — so we pin one.
        ps.Options.Modes.Add(new ModeEntry { Key = ContextEnhancementMode, Value = "NONE" });
        ps.Options.Modes.Add(new ModeEntry { Key = MediaMode, Value = "" });
        ps.Options.Modes.Add(new ModeEntry { Key = JamMode, Value = "off" });
    }

    /// <summary>The per-playback-session <c>session_command_id</c>: a fresh opaque 32-hex value iff <c>session_id</c> turns
    /// over, otherwise the one already minted. Proven over 24 PUTs to be stable across track changes, autoplay hand-offs,
    /// seeks, playback_id / queue_revision changes and pause — and NOT derived from any command id. The generator itself is
    /// UNPROVEN, so we treat it as an opaque random 128-bit id minted with the session.</summary>
    internal string SessionCommandIdFor(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return "";
        lock (_sessionCommandGate)
        {
            if (!string.Equals(_sessionCommandSessionId, sessionId, StringComparison.Ordinal))
            {
                _sessionCommandSessionId = sessionId;
                Span<byte> bytes = stackalloc byte[16];
                System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
                _sessionCommandId = Convert.ToHexString(bytes).ToLowerInvariant();
            }
            return _sessionCommandId;
        }
    }

    string? LookupVideoGid(string? trackUri)
    {
        if (AssociatedVideoGid is not { } lookup || string.IsNullOrEmpty(trackUri)) return null;
        // The lookup reaches into the association store from the publish path — a fault there must degrade to "no video
        // offer", never break the whole PUT (which would take the device off the cluster).
        try { return lookup(trackUri) is { Length: > 0 } gid ? gid : null; }
        catch { return null; }
    }

    // The mode-switch offer, in the exact shape the reference desktop client publishes: the mode we are ALREADY hosting is
    // always disallowed with "no_associated_track", and the other one is either offered in `signals` (gid known) or
    // disallowed with the same reason. Only the current track can carry an offer — next_tracks never do.
    static void StampModeSignals(ProtoPlayerState ps, string? videoGid, PlayableKind? currentKind)
    {
        const string NoAssociatedTrack = "no_associated_track";
        // The video-host half is symmetric-by-construction, not capture-proven (every captured session was audio-hosted).
        bool videoHost = currentKind == PlayableKind.Video;
        string hosted = videoHost ? "switch-to-video" : "switch-to-audio";
        string other = videoHost ? "switch-to-audio" : "switch-to-video";
        var reasons = new RestrictionReasons();
        reasons.Reasons.Add(NoAssociatedTrack);
        ps.Restrictions.DisallowSignals[hosted] = reasons;
        if (!string.IsNullOrEmpty(videoGid))
        {
            ps.Signals.Add(other);
        }
        else
        {
            var otherReasons = new RestrictionReasons();
            otherReasons.Reasons.Add(NoAssociatedTrack);
            ps.Restrictions.DisallowSignals[other] = otherReasons;
        }
    }

    static ProvidedTrack ToProvided(in SnapshotTrack t, string contextUri, string interactionId, string pageInstanceId,
        PlayableKind? currentKind = null, string? associatedVideoGid = null,
        bool isCurrentTrack = false, long? currentPositionMs = null)
    {
        var pt = new ProvidedTrack
        {
            Uri = t.Uri,
            Uid = t.Uid ?? "",
            Provider = string.IsNullOrEmpty(t.Provider) ? "context" : t.Provider,
        };
        var meta = pt.Metadata;
        if (t.Metadata is { Count: > 0 })
            foreach (var (k, v) in t.Metadata)
                if (!string.IsNullOrEmpty(k)) meta[k] = v ?? "";

        AddIfMissing(meta, "title", t.Title);
        AddIfMissing(meta, "artist_name", t.ArtistName);
        AddIfMissing(meta, "album_title", t.AlbumTitle);
        AddIfMissing(meta, "album_uri", t.AlbumUri);
        AddIfMissing(meta, "artist_uri", t.ArtistUri);

        // bug 3: video-ness for entity_uri/view_index/iteration suppression + the track_player fallback below. When the
        // CALLER knows the live kind (currentKind supplied), that is authoritative — full stop, never overridden by a
        // stale/absent wire claim. Otherwise (prev/next rows always; the current row on the rare caller that genuinely
        // doesn't know the kind) fall back to the wire's OWN claim (MediaSwitchLogic.HasVideoMetadata) plus — for the
        // CURRENT row only — the legacy has-video-badge heuristic (isCurrentTrack). prev/next rows never get that
        // heuristic: a next-up row's mere video AVAILABILITY is not "playing as video", so it must not claim one.
        bool isVideo = currentKind is { } knownKind
            ? knownKind == PlayableKind.Video
            : (isCurrentTrack && t.HasVideo) || IsVideoTrack(t);
        bool isAutoplay = pt.Provider == "autoplay";
        bool isQueue = pt.Provider == "queue";
        if (!string.IsNullOrEmpty(contextUri) && pt.Provider == "context")
        {
            AddIfMissing(meta, "context_uri", contextUri);
            if (!isVideo) AddIfMissing(meta, "entity_uri", contextUri);
        }
        if (!string.IsNullOrEmpty(t.ImageUrl))
        {
            var image = SpotifyImage(t.ImageUrl);
            AddIfMissing(meta, "image_url", image);
            AddIfMissing(meta, "image_small_url", image);
            AddIfMissing(meta, "image_large_url", image);
            AddIfMissing(meta, "image_xlarge_url", image);
        }
        if (isQueue) meta["is_queued"] = "true";
        if (isAutoplay) meta["autoplay.is_autoplay"] = "true";
        if (!isAutoplay && !isQueue)
        {
            AddIfMissing(meta, "actions.skipping_prev_past_track", "resume");
            AddIfMissing(meta, "actions.skipping_next_past_track", "resume");
        }
        // bug 6 / M0: report the correct player for the current media kind. The single source of truth is
        // MediaSwitchLogic.TrackPlayer. For the CURRENT track the caller supplies the controller's LIVE kind, which is
        // AUTHORITATIVE (it names the host that is actually decoding) — so it OVERWRITES any track_player the wire snapshot
        // carried: a video-capable track played as audio must report "audio", or remote controllers render the wrong player.
        // Without a kind (prev/next rows, the empty NewConnection announce) we fall back to the per-track wire heuristic.
        // LocalFile can't be distinguished from Audio on the wire snapshot, and both play through the audio host, so mapping
        // non-video → Audio is exact.
        // The associated music video's manifest gid (Connect's `associated_video_id`). Authoritative over anything the wire
        // snapshot carried, and stamped on the CURRENT track only — the reference client never puts it on next_tracks.
        if (!string.IsNullOrEmpty(associatedVideoGid)) meta["associated_video_id"] = associatedVideoGid;
        if (currentKind is { } kind) meta["track_player"] = MediaSwitchLogic.TrackPlayer(kind);
        else AddIfMissing(meta, "track_player", MediaSwitchLogic.TrackPlayer(isVideo ? PlayableKind.Video : PlayableKind.Audio));
        // bug 7: while WE genuinely host video for the CURRENT track, stamp the fields a hand-off needs to land on the
        // video's own start point — media.type, media.manifest_id (the SAME gid as associated_video_id: the resolver's
        // own comment on the kind-212 gid is that it IS the manifest id), and media.start_position (our live position —
        // the only honest "where this video begins" fact we have; inbound already treats it as the video's start point
        // and wins with it over the transferred/extrapolated audio position — PlaybackController.cs ~1632).
        if (currentKind == PlayableKind.Video)
        {
            meta["media.type"] = "video";
            if (!string.IsNullOrEmpty(associatedVideoGid)) meta["media.manifest_id"] = associatedVideoGid;
            if (currentPositionMs is { } pos && pos >= 0)
                meta["media.start_position"] = pos.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // No confirmed live kind (prev/next rows; a caller that genuinely doesn't know) — forward only what the wire
        // snapshot already carried, never fabricate a fresh manifest id from a bare availability/heuristic guess.
        else if (currentKind is null && isVideo && t.Metadata is { } vm
            && vm.TryGetValue("media.manifest_id", out var manifestId) && !string.IsNullOrEmpty(manifestId))
            AddIfMissing(meta, "media.manifest_id", manifestId);
        AddIfMissing(meta, "interaction_id", interactionId);
        AddIfMissing(meta, "page_instance_id", pageInstanceId);
        if (!isVideo && !isQueue && !isAutoplay)
        {
            if (t.ViewIndex >= 0) AddIfMissing(meta, "view_index", t.ViewIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddIfMissing(meta, "iteration", "0");
        }
        return pt;
    }

    static void AddIfMissing(IDictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value) && !metadata.ContainsKey(key)) metadata[key] = value;
    }

    // bug 3: the WIRE's own claim only — never a bare has-video/availability flag. t.HasVideo is folded in separately
    // (and only for the CURRENT row) at the call site, so a next-up row's mere video AVAILABILITY can no longer make
    // this true; it takes an actual wire signal (MediaSwitchLogic's shared key list) instead.
    static bool IsVideoTrack(in SnapshotTrack t) => MediaSwitchLogic.HasVideoMetadata(t.Metadata);

    static string FeatureOf(string contextUri, IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("format_list_type", out var listType) && listType == "liked-songs") return "your_library";
        if (metadata.ContainsKey("liked_songs_collection_uri")) return "your_library";
        if (contextUri.Contains(":collection", StringComparison.Ordinal)) return "your_library";
        if (contextUri.Contains(":album:", StringComparison.Ordinal)) return "album";
        if (contextUri.Contains(":artist", StringComparison.Ordinal)) return "artist";
        if (contextUri.Contains(":playlist:", StringComparison.Ordinal)) return "playlist";
        if (contextUri.Contains(":episode:", StringComparison.Ordinal)) return "home";
        return "harmony";
    }

    static string SpotifyImage(string url)
    {
        const string prefix = "https://i.scdn.co/image/";
        return url.StartsWith(prefix, StringComparison.Ordinal) ? "spotify:image:" + url[prefix.Length..] : url;
    }
}
