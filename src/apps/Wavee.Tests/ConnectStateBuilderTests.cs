using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Core;
using Wavee.Protocol.Player;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

public class ConnectStateBuilderTests
{
    [Fact]
    public void BuildPutState_EmitsInboundCommandAttribution()
    {
        var builder = new ConnectStateBuilder("device", "Wavee");

        var req = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged,
            snap: null,
            messageId: 9,
            isActive: false,
            nowMs: 10_000,
            lastCommandSentByDeviceId: "controller-device",
            lastCommandMessageId: 604162001));

        Assert.Equal("controller-device", req.LastCommandSentByDeviceId);
        Assert.Equal(604162001u, req.LastCommandMessageId);
    }

    [Fact]
    public void BuildPutState_PreservesVideoMetadataAndSynthesizesAudioContextMetadata()
    {
        var builder = new ConnectStateBuilder("device", "Wavee");
        var videoMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["track_player"] = "video",
            ["media.type"] = "video",
            ["media.manifest_id"] = "manifest",
            ["save_track.uri"] = "spotify:track:audio",
            ["context_uri"] = "spotify:playlist:p",
        };
        var autoplayMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["track_player"] = "audio",
        };
        var contextMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format_list_type"] = "liked-songs",
            ["context_description"] = "Liked Songs",
            ["mixer_enabled"] = "true",
        };
        var snap = new LocalPlaybackSnapshot(
            Track: new SnapshotTrack("spotify:track:video", "uv", "context", "Video", "Album",
                "spotify:artist:a", "Artist", "spotify:album:al", "", true, 5, videoMetadata),
            ContextUri: "spotify:playlist:p",
            PositionMs: 123,
            DurationMs: 456,
            IsPlaying: true,
            IsPaused: false,
            Shuffle: false,
            Repeat: RepeatMode.Off,
            PrevTracks: Array.Empty<SnapshotTrack>(),
            NextTracks: new[]
            {
                new SnapshotTrack("spotify:track:audio-next", "un", "context", "Audio", "Album",
                    "spotify:artist:a", "Artist", "spotify:album:al", "https://i.scdn.co/image/abc",
                    false, 6, new Dictionary<string, string>()),
                new SnapshotTrack("spotify:track:auto", "ua", "autoplay", "Auto", "",
                    "", "", "", "", false, 7, autoplayMetadata),
            },
            ContextMetadata: contextMetadata,
            ContextIndex: 5,
            InteractionId: "interaction",
            PageInstanceId: "page",
            QueueRevision: "42",
            SessionId: "session",
            PlaybackId: "playback",
            HasBeenPlayingForMs: 1000,
            StartedPlayingAtMs: 9000,
            Volume01: 1);

        var req = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000));

        var ps = req.Device.PlayerState;
        Assert.Equal("spotify:playlist:p", ps.ContextUri);
        Assert.Equal("context://spotify:playlist:p", ps.ContextUrl);
        Assert.Equal("42", ps.QueueRevision);
        Assert.Equal((uint)5, ps.Index.Track);
        Assert.Equal("your_library", ps.PlayOrigin.FeatureIdentifier);
        Assert.Equal("your_library", ps.PlayOrigin.ReferrerIdentifier);
        Assert.Equal(BitrateLevel.High, ps.PlaybackQuality.BitrateLevel);
        Assert.Equal(BitrateStrategy.CachedFile, ps.PlaybackQuality.Strategy);
        Assert.Equal(HiFiStatus.Off, ps.PlaybackQuality.HifiStatus);
        Assert.Equal("2", ps.ContextMetadata["player.arch"]);
        Assert.Equal("true", ps.ContextMetadata["mixer_enabled"]);

        var video = ps.Track.Metadata;
        Assert.Equal("video", video["track_player"]);
        Assert.Equal("manifest", video["media.manifest_id"]);
        Assert.Equal("spotify:track:audio", video["save_track.uri"]);
        Assert.Equal("interaction", video["interaction_id"]);
        Assert.Equal("page", video["page_instance_id"]);
        Assert.False(video.ContainsKey("entity_uri"));
        Assert.False(video.ContainsKey("view_index"));
        Assert.False(video.ContainsKey("iteration"));

        var audio = ps.NextTracks[0].Metadata;
        Assert.Equal("audio", audio["track_player"]);
        Assert.Equal("spotify:playlist:p", audio["context_uri"]);
        Assert.Equal("spotify:playlist:p", audio["entity_uri"]);
        Assert.Equal("6", audio["view_index"]);
        Assert.Equal("0", audio["iteration"]);
        Assert.Equal("spotify:image:abc", audio["image_url"]);

        var autoplay = ps.NextTracks[1];
        Assert.Equal("autoplay", autoplay.Provider);
        Assert.Equal("true", autoplay.Metadata["autoplay.is_autoplay"]);
        Assert.False(autoplay.Metadata.ContainsKey("context_uri"));
        Assert.False(autoplay.Metadata.ContainsKey("entity_uri"));
    }

    // ── M0: `track_player` is the controller's LIVE media kind, not a bare has-video flag ────────────────────────────
    // The current track's track_player names the HOST that is actually decoding. A video-capable track played as AUDIO must
    // report "audio" (remote controllers render the wrong player otherwise) — and a track playing as VIDEO must report
    // "video" even if the wire snapshot's own metadata said otherwise. So: video IFF the current kind is Video.
    static LocalPlaybackSnapshot Snap(bool hasVideo, Dictionary<string, string>? trackMetadata = null) =>
        new(
            Track: new SnapshotTrack("spotify:track:t", "uid", "context", "Title", "Album",
                "spotify:artist:a", "Artist", "spotify:album:al", "", hasVideo, 3,
                trackMetadata ?? new Dictionary<string, string>(StringComparer.Ordinal)),
            ContextUri: "spotify:playlist:p",
            PositionMs: 0,
            DurationMs: 1000,
            IsPlaying: true,
            IsPaused: false,
            Shuffle: false,
            Repeat: RepeatMode.Off,
            PrevTracks: Array.Empty<SnapshotTrack>(),
            NextTracks: new[]
            {
                new SnapshotTrack("spotify:track:next", "un", "context", "Next", "Album",
                    "spotify:artist:a", "Artist", "spotify:album:al", "", false, 4,
                    new Dictionary<string, string>(StringComparer.Ordinal)),
            },
            ContextMetadata: new Dictionary<string, string>(StringComparer.Ordinal),
            ContextIndex: 3,
            InteractionId: "interaction",
            PageInstanceId: "page",
            QueueRevision: "1",
            SessionId: "session",
            PlaybackId: "playback",
            HasBeenPlayingForMs: 0,
            StartedPlayingAtMs: 9000);

    static string TrackPlayerOf(LocalPlaybackSnapshot snap, PlayableKind? kind)
    {
        var builder = new ConnectStateBuilder("device", "Wavee");
        var ps = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000, currentKind: kind)).Device.PlayerState;
        return ps.Track.Metadata["track_player"];
    }

    [Theory]
    [InlineData(PlayableKind.Video, "video")]
    [InlineData(PlayableKind.Audio, "audio")]
    [InlineData(PlayableKind.LocalFile, "audio")]
    public void BuildPutState_TrackPlayer_FollowsTheCurrentMediaKind_ForAPlainTrack(PlayableKind kind, string expected)
        => Assert.Equal(expected, TrackPlayerOf(Snap(hasVideo: false), kind));

    [Theory]
    [InlineData(PlayableKind.Video, "video")]
    [InlineData(PlayableKind.Audio, "audio")]
    [InlineData(PlayableKind.LocalFile, "audio")]
    public void BuildPutState_TrackPlayer_FollowsTheCurrentMediaKind_ForAVideoCapableTrack(PlayableKind kind, string expected)
    {
        // has-video AND a stale metadata claim of "video" — the live kind still wins (this is the M0 defect: the app used to
        // advertise track_player="video" while the audio host was the one playing).
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["track_player"] = "video",
            ["media.type"] = "video",
        };
        Assert.Equal(expected, TrackPlayerOf(Snap(hasVideo: true, meta), kind));
    }

    [Fact]
    public void BuildPutState_TrackPlayer_KeepsTheWireHeuristic_WhenNoKindIsSupplied()
    {
        // The empty NewConnection announce (and any caller that genuinely doesn't know the kind) keeps the pre-M0 behavior.
        Assert.Equal("audio", TrackPlayerOf(Snap(hasVideo: false), null));
        Assert.Equal("video", TrackPlayerOf(Snap(hasVideo: true), null));
    }

    [Fact]
    public void BuildPutState_CurrentMediaKind_DoesNotLeakOntoNextTracks()
    {
        var builder = new ConnectStateBuilder("device", "Wavee");
        var ps = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, Snap(hasVideo: false), 1, true,
            nowMs: 10_000, currentKind: PlayableKind.Video)).Device.PlayerState;

        Assert.Equal("video", ps.Track.Metadata["track_player"]);
        Assert.Equal("audio", ps.NextTracks[0].Metadata["track_player"]);   // next-up is not the current media
    }

    // ── bug 3: an up-next row's bare HasVideo (availability, not "playing as video") must not over-claim ────────────
    [Fact]
    public void BuildPutState_NextTrack_HasVideoAvailabilityAlone_DoesNotClaimVideo()
    {
        var snap = Snap(hasVideo: false) with
        {
            NextTracks = new[]
            {
                // HasVideo=true (mere availability), no wire metadata at all — never "playing as video".
                new SnapshotTrack("spotify:track:next-video", "un", "context", "Next", "Album",
                    "spotify:artist:a", "Artist", "spotify:album:al", "", true, 4,
                    new Dictionary<string, string>(StringComparer.Ordinal)),
            },
        };
        var builder = new ConnectStateBuilder("device", "Wavee");
        var ps = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000, currentKind: PlayableKind.Audio))
            .Device.PlayerState;

        var next = ps.NextTracks[0].Metadata;
        Assert.Equal("audio", next["track_player"]);          // availability alone is not enough
        Assert.True(next.ContainsKey("entity_uri"));           // …so the ordinary context-row shape is not suppressed
        Assert.True(next.ContainsKey("view_index"));
        Assert.True(next.ContainsKey("iteration"));
    }

    // ── bug 7: while genuinely hosting video, stamp media.type/media.manifest_id/media.start_position ───────────────
    [Fact]
    public void BuildPutState_VideoHost_StampsMediaTypeManifestIdAndStartPosition()
    {
        var snap = Snap(hasVideo: true) with { PositionMs = 4200 };
        var builder = new ConnectStateBuilder("device", "Wavee") { AssociatedVideoGid = _ => Gid };
        var ps = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000, currentKind: PlayableKind.Video))
            .Device.PlayerState;

        var meta = ps.Track.Metadata;
        Assert.Equal("video", meta["media.type"]);
        Assert.Equal(Gid, meta["media.manifest_id"]);          // same gid as associated_video_id (verified equivalence)
        Assert.Equal(Gid, meta["associated_video_id"]);
        Assert.Equal("4200", meta["media.start_position"]);
    }

    [Fact]
    public void BuildPutState_AudioHost_NeverStampsMediaType()
    {
        // Hosting audio (even for a video-capable track) must not fabricate media.* — those fields name a video session
        // that is not actually running.
        var ps = PlayerStateOf(Snap(hasVideo: true), PlayableKind.Audio, _ => Gid);
        Assert.False(ps.Track.Metadata.ContainsKey("media.type"));
        Assert.False(ps.Track.Metadata.ContainsKey("media.manifest_id"));
        Assert.False(ps.Track.Metadata.ContainsKey("media.start_position"));
    }

    // ── Connect-state video parity: `associated_video_id` + the switch-to-video/switch-to-audio signal/disallow pair ──
    // Wire shape proven against captured desktop PUT bodies: while hosting audio with a known music video the state carries
    // the 32-hex gid on the CURRENT track, offers "switch-to-video" in `signals`, and disallows only "switch-to-audio"
    // (reason "no_associated_track"). With no known video BOTH signals are disallowed and no gid is stamped.
    const string Gid = "10302a7889774d2f8b1aef877119786c";

    static Wavee.Protocol.Player.PlayerState PlayerStateOf(LocalPlaybackSnapshot snap, PlayableKind? kind,
        Func<string, string?>? associatedVideoGid)
    {
        var builder = new ConnectStateBuilder("device", "Wavee") { AssociatedVideoGid = associatedVideoGid };
        return PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000, currentKind: kind)).Device.PlayerState;
    }

    static string[] ReasonsFor(Wavee.Protocol.Player.PlayerState ps, string signal) =>
        ps.Restrictions.DisallowSignals.TryGetValue(signal, out var r) ? [.. r.Reasons] : [];

    [Fact]
    public void BuildPutState_HasVideoWithGid_StampsAssociatedVideoIdAndOffersSwitchToVideo()
    {
        var ps = PlayerStateOf(Snap(hasVideo: true), PlayableKind.Audio, _ => Gid);

        Assert.Equal(Gid, ps.Track.Metadata["associated_video_id"]);
        Assert.Equal("audio", ps.Track.Metadata["track_player"]);   // the offer never changes the host we report
        Assert.Contains("switch-to-video", ps.Signals);
        Assert.Equal(["no_associated_track"], ReasonsFor(ps, "switch-to-audio"));
        Assert.False(ps.Restrictions.DisallowSignals.ContainsKey("switch-to-video"));
        // the gid rides the current track ONLY
        Assert.False(ps.NextTracks[0].Metadata.ContainsKey("associated_video_id"));
    }

    [Fact]
    public void BuildPutState_HasVideoWithoutGid_DisallowsBothSignals()
    {
        // A Pathfinder totalCount can light the badge with no gid resolved yet — until one lands the wire must keep saying
        // "no_associated_track", because the reference client only ever offers the switch together with the gid.
        var ps = PlayerStateOf(Snap(hasVideo: true), PlayableKind.Audio, _ => null);

        Assert.False(ps.Track.Metadata.ContainsKey("associated_video_id"));
        Assert.DoesNotContain("switch-to-video", ps.Signals);
        Assert.Equal(["no_associated_track"], ReasonsFor(ps, "switch-to-video"));
        Assert.Equal(["no_associated_track"], ReasonsFor(ps, "switch-to-audio"));
    }

    [Fact]
    public void BuildPutState_NoVideo_DisallowsBothSignals()
    {
        var ps = PlayerStateOf(Snap(hasVideo: false), PlayableKind.Audio, associatedVideoGid: null);

        Assert.False(ps.Track.Metadata.ContainsKey("associated_video_id"));
        Assert.DoesNotContain("switch-to-video", ps.Signals);
        Assert.Equal(["no_associated_track"], ReasonsFor(ps, "switch-to-video"));
        Assert.Equal(["no_associated_track"], ReasonsFor(ps, "switch-to-audio"));
    }

    [Fact]
    public void BuildPutState_VideoHost_OffersTheSwitchBackToAudio()
    {
        var ps = PlayerStateOf(Snap(hasVideo: true), PlayableKind.Video, _ => Gid);

        Assert.Equal("video", ps.Track.Metadata["track_player"]);
        Assert.Contains("switch-to-audio", ps.Signals);
        Assert.Equal(["no_associated_track"], ReasonsFor(ps, "switch-to-video"));
        Assert.False(ps.Restrictions.DisallowSignals.ContainsKey("switch-to-audio"));
    }

    [Fact]
    public void BuildPutState_AssociatedVideoGidLookupThrows_DegradesToNoOffer()
    {
        var ps = PlayerStateOf(Snap(hasVideo: true), PlayableKind.Audio, _ => throw new InvalidOperationException("store down"));

        Assert.False(ps.Track.Metadata.ContainsKey("associated_video_id"));
        Assert.Equal(["no_associated_track"], ReasonsFor(ps, "switch-to-video"));
    }

    // ── Near-1:1 PutState parity with the reference desktop client (decoded from 24 captured PUT bodies) ──────────────
    // Everything below is "constant in 24/24" on the wire, so the assertions are exact-value, not shape-only.

    [Fact]
    public void BuildPutState_EmitsTheFourUnconditionalBaselineSignals()
    {
        var ps = PlayerStateOf(Snap(hasVideo: false), PlayableKind.Audio, associatedVideoGid: null);

        Assert.Equal(new[] { "interact", "automix-preview", "speed-preview", "stop-speed-preview" }, ps.Signals);
    }

    [Fact]
    public void BuildPutState_VideoOffer_PutsSwitchToVideoFirst_AheadOfTheBaseline()
    {
        var ps = PlayerStateOf(Snap(hasVideo: true), PlayableKind.Audio, _ => Gid);

        Assert.Equal(new[] { "switch-to-video", "interact", "automix-preview", "speed-preview", "stop-speed-preview" }, ps.Signals);
    }

    [Fact]
    public void BuildPutState_EmitsTheConstantRestrictionsAndEmptyButPresentSubmessages()
    {
        var ps = PlayerStateOf(Snap(hasVideo: false), PlayableKind.Audio, associatedVideoGid: null);

        Assert.Equal(["not_supported_by_content_type"], ps.Restrictions.DisallowSettingPlaybackSpeedReasons);
        Assert.Equal(["already_set"], ps.Restrictions.UnknownDisallowReasons31);
        // 24 is absent in 24/24 captured PUTs — never guessed at.
        Assert.Empty(ps.Restrictions.DisallowAddToQueueReasons);
        // the tags desktop always emits with no content
        Assert.NotNull(ps.ContextRestrictions);
        Assert.NotNull(ps.Suppressions);
        Assert.Empty(ps.Suppressions.Providers);
    }

    [Fact]
    public void BuildPutState_DisallowSettingModes_IsOmittedOnPlaylistContextsAndPresentElsewhere()
    {
        // playlist → Enhance is supported there, so desktop publishes no mode restriction at all
        var playlist = PlayerStateOf(Snap(hasVideo: false), PlayableKind.Audio, associatedVideoGid: null);
        Assert.Empty(playlist.Restrictions.DisallowSettingModes);

        // album (and album-radio / autoplay) → context_enhancement.RECOMMENDATION disallowed
        var album = PlayerStateOf(Snap(hasVideo: false) with { ContextUri = "spotify:album:a" }, PlayableKind.Audio, null);
        Assert.True(album.Restrictions.DisallowSettingModes.TryGetValue("context_enhancement", out var mode));
        Assert.True(mode!.Values.TryGetValue("RECOMMENDATION", out var reasons));
        Assert.Equal(["not_supported_by_content_type"], reasons!.Reasons);
        Assert.Single(album.Restrictions.DisallowSettingModes);
    }

    [Fact]
    public void BuildPutState_EmitsTheThreeConstantPlayerOptionModes()
    {
        var ps = PlayerStateOf(Snap(hasVideo: false), PlayableKind.Audio, associatedVideoGid: null);

        Assert.Equal(3, ps.Options.Modes.Count);
        Assert.Equal(["context_enhancement", "media", "jam"], ps.Options.Modes.Select(m => m.Key));
        Assert.Equal(["NONE", "", "off"], ps.Options.Modes.Select(m => m.Value));
        // the empty `media` value is EXPLICITLY on the wire (`12 00`), as desktop sends it — not a dropped default
        Assert.True(ps.Options.Modes[1].HasValue);
    }

    [Fact]
    public void BuildPutState_SessionCommandId_IsAPerSessionConstantThatTurnsOverWithTheSessionId()
    {
        var builder = new ConnectStateBuilder("device", "Wavee");
        var snap = Snap(hasVideo: false);

        string First() => PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000)).Device.PlayerState.SessionCommandId;

        string id = First();
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, First());                                     // stable across PUTs of the same session
        Assert.Equal(id, PutStateRequest.Parser.ParseFrom(builder.BuildPutState(   // …and across track/pause/queue changes
            PutStateReasonKind.PlayerStateChanged, snap with { IsPaused = true, QueueRevision = "9", PlaybackId = "p2" },
            2, true, nowMs: 11_000)).Device.PlayerState.SessionCommandId);

        string next = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap with { SessionId = "session-2" }, 3, true, nowMs: 12_000))
            .Device.PlayerState.SessionCommandId;
        Assert.NotEqual(id, next);
        Assert.Matches("^[0-9a-f]{32}$", next);

        // a separate builder mints its own — the value is opaque, never derived from a command id
        Assert.NotEqual(id, PutStateRequest.Parser.ParseFrom(new ConnectStateBuilder("device", "Wavee").BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000)).Device.PlayerState.SessionCommandId);
    }

    [Fact]
    public void BuildPutState_EmitsThePlayerStateUnknown38Placeholder()
    {
        var ps = PlayerStateOf(Snap(hasVideo: false), PlayableKind.Audio, associatedVideoGid: null);

        Assert.NotNull(ps.UnknownField38);
        Assert.Equal("", ps.UnknownField38.Unknown1);
        Assert.True(ps.UnknownField38.HasUnknown1);   // explicit presence → the `0a 00` desktop always sends
    }

    [Fact]
    public void BuildDeviceInfo_PopulatesAudioOutputDeviceInfoFromTheLiveEndpointName()
    {
        var builder = new ConnectStateBuilder("device", "Wavee") { AudioOutputDeviceName = () => "Speakers (Realtek)" };
        var info = builder.BuildDeviceInfo();

        Assert.Equal("Speakers (Realtek)", info.AudioOutputDeviceInfo.DeviceName);
        Assert.Equal(AudioOutputDeviceType.UnknownAudioOutputDeviceType, info.AudioOutputDeviceInfo.AudioOutputDeviceType);
        Assert.True(info.AudioOutputDeviceInfo.HasAudioOutputDeviceType);   // explicitly serialized despite being the default
        Assert.Equal(3u, info.AudioOutputDeviceInfo.UnknownField5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildDeviceInfo_AudioOutputDeviceInfo_FallsBackToANamelessEndpoint(string? name)
    {
        // No live name (unwired hook, no roster yet) → still the submessage desktop always sends, with an empty name. We
        // never publish the captured machine's endpoint string as a stand-in.
        var builder = new ConnectStateBuilder("device", "Wavee") { AudioOutputDeviceName = () => name };
        Assert.Equal("", builder.BuildDeviceInfo().AudioOutputDeviceInfo.DeviceName);

        var unwired = new ConnectStateBuilder("device", "Wavee").BuildDeviceInfo().AudioOutputDeviceInfo;
        Assert.Equal("", unwired.DeviceName);
        Assert.Equal(3u, unwired.UnknownField5);

        var faulted = new ConnectStateBuilder("device", "Wavee") { AudioOutputDeviceName = () => throw new InvalidOperationException("no wasapi") };
        Assert.Equal("", faulted.BuildDeviceInfo().AudioOutputDeviceInfo.DeviceName);
    }

    [Fact]
    public void BuildDeviceInfo_EmitsTheDesktopCapabilityConstants()
    {
        var c = new ConnectStateBuilder("device", "Wavee").BuildDeviceInfo().Capabilities;

        Assert.True(c.UnknownCapability33);
        Assert.True(c.UnknownCapability34);
        Assert.True(c.UnknownCapability35);
        Assert.True(c.UnknownCapability36);
        Assert.True(c.UnknownCapability38);
        // desktop sends 15 supported types — "audio/audio" was the one we were missing
        Assert.Equal(15, c.SupportedTypes.Count);
        Assert.Contains("audio/audio", c.SupportedTypes);
    }

    [Fact]
    public void BuildPutState_Paused_KeepsIsPlayingTrueAndAddsPauseRestrictions()
    {
        var builder = new ConnectStateBuilder("device", "Wavee");
        var snap = new LocalPlaybackSnapshot(
            Track: new SnapshotTrack("spotify:track:t", "uid", "context", "Title", "Album",
                "spotify:artist:a", "Artist", "spotify:album:al", "", false, 0,
                new Dictionary<string, string>()),
            ContextUri: "spotify:playlist:p",
            PositionMs: 131_090,
            DurationMs: 227_866,
            IsPlaying: true,
            IsPaused: true,
            Shuffle: false,
            Repeat: RepeatMode.Off,
            PrevTracks: Array.Empty<SnapshotTrack>(),
            NextTracks: Array.Empty<SnapshotTrack>(),
            ContextMetadata: new Dictionary<string, string>(),
            ContextIndex: 0,
            InteractionId: "interaction",
            PageInstanceId: "page",
            QueueRevision: "1",
            SessionId: "session",
            PlaybackId: "playback",
            HasBeenPlayingForMs: 0,
            StartedPlayingAtMs: 9000);

        var ps = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000)).Device.PlayerState;

        Assert.True(ps.IsPlaying);
        Assert.True(ps.IsPaused);
        Assert.Equal(0.0, ps.PlaybackSpeed);
        Assert.Contains("already_paused", ps.Restrictions.DisallowPausingReasons);
        Assert.Contains("no_prev_track", ps.Restrictions.DisallowSkippingPrevReasons);
        Assert.Empty(ps.Restrictions.DisallowResumingReasons);
    }
}

// The bridge-side edge detector behind that wire state: a badge-only association land must produce EXACTLY ONE extra
// PutState (nothing else re-publishes it — no host swap, no playback event), and nothing else may.
public class ConnectStateVideoFactsTests
{
    const string Uri = "spotify:track:a";
    const string Gid = "10302a7889774d2f8b1aef877119786c";

    [Fact]
    public void FirstObservationOfATrack_IsOnlyABaseline()
    {
        var facts = new ConnectVideoFacts();
        // the track-change PutState the publisher already sends carries these facts — announcing again would double it
        Assert.False(facts.Observe(Uri, hasVideo: true, videoGidHex: Gid));
    }

    [Fact]
    public void AssociationLandingMidTrack_AnnouncesExactlyOnce()
    {
        var facts = new ConnectVideoFacts();
        Assert.False(facts.Observe(Uri, false, null));            // baseline: playing, nothing known yet
        Assert.True(facts.Observe(Uri, true, null));              // the badge lands → one PutState
        Assert.False(facts.Observe(Uri, true, null));             // re-observations (bulk store changes) are silent
        Assert.False(facts.Observe(Uri, true, null));
    }

    [Fact]
    public void GidLandingAfterTheBadge_AnnouncesAgain_BecauseTheOfferOnlyExistsWithAGid()
    {
        var facts = new ConnectVideoFacts();
        Assert.False(facts.Observe(Uri, false, null));
        Assert.True(facts.Observe(Uri, true, null));
        Assert.True(facts.Observe(Uri, true, Gid));
        Assert.False(facts.Observe(Uri, true, Gid));
    }

    [Fact]
    public void ATrackChange_NeverAnnounces()
    {
        var facts = new ConnectVideoFacts();
        Assert.False(facts.Observe(Uri, false, null));
        Assert.True(facts.Observe(Uri, true, Gid));
        Assert.False(facts.Observe("spotify:track:b", true, "6c68d9d90e50486aafc6119885f04c3f"));
        Assert.False(facts.Observe(null, false, null));
    }

    [Fact]
    public void LosingVideo_NeverAnnounces()
    {
        var facts = new ConnectVideoFacts();
        Assert.False(facts.Observe(Uri, true, Gid));
        Assert.False(facts.Observe(Uri, false, null));   // a downgrade rides the media-kind change it causes
    }
}
