// ── Wavee.Tests/PutStateWireTests.cs — the connect-state PUT, decoded back through the generated message (R4-1) ────────
//
// The gate for `Spotify/Spotify.Decode.PutState.cs` and the snapshot's parity half (`Playback/Playback.Wire.cs`). 0.2.9's
// `ConnectStateBuilderTests` ported over DECODED BYTES: the zero-allocation writer encodes a snapshot, the generated
// `PutStateRequest` parser reads it back, and every assertion is on what a controller would read. All pure: no session,
// no socket, no reducer, no table.
//
//   THE QUEUE IS THE PUT'S. A phone shows the queue, "Playing from" and the restrictions off prev_tracks / next_tracks,
//   play_origin and restrictions — 0.3 sent one track and nothing else (G-240).
//   A PAUSE IS NOT A STOP. is_playing stays true, speed drops to 0, already_paused is stated.
//   A WARM 50 + 50 ENCODE ALLOCATES NOTHING.

using Wavee;
using Xunit;
using P = Wavee.Protocol.Player;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

public class PutStateWireTests
{
    const string Gid = "10302a7889774d2f8b1aef877119786c";

    static readonly EntityId Track = EntityId.ForGid(EntityKind.Track, (UInt128)0x1234_5678_9ABC_DEF0UL);
    static readonly EntityId Playlist = EntityId.ForGid(EntityKind.Playlist, (UInt128)0x0FED_CBA9_8765_4321UL);
    static readonly EntityId AlbumContext = EntityId.ForGid(EntityKind.Album, (UInt128)0xA1B2_C3D4UL);
    static readonly EntityId Album = EntityId.ForGid(EntityKind.Album, (UInt128)0x7777UL);
    static readonly EntityId Artist = EntityId.ForGid(EntityKind.Artist, (UInt128)0x8888UL);

    static EntityId TrackId(int n) => EntityId.ForGid(EntityKind.Track, (UInt128)(ulong)(0x9000 + n));

    static Playback.WireRow Row(int n, QueueProvider provider = QueueProvider.Context, string image = "", string? uid = null,
        ulong itemId = 0)
        => new(TrackId(n), itemId, uid, provider, "Title " + n, "Artist " + n, "Album " + n, image, Album, Artist);

    static Playback.WireWindow Window(Playback.WireRow[]? prev = null, Playback.WireRow[]? next = null, int contextIndex = 0,
        Playback.WireRow current = default)
        => new(in current, prev ?? Array.Empty<Playback.WireRow>(), next ?? Array.Empty<Playback.WireRow>(), contextIndex);

    static readonly Playback.WireIds Ids = new(
        SessionId: (UInt128)0x1111UL, PlaybackId: (UInt128)0x2222UL, SessionCommandId: (UInt128)0x3333UL,
        InteractionId: (UInt128)0x4444UL, PageInstanceId: (UInt128)0x5555UL);

    static Playback.Snapshot Snap(Playback.PlayableKind kind = Playback.PlayableKind.Audio, bool hasVideo = false,
        string videoGid = "", bool paused = false, bool buffering = false, EntityId context = default,
        Playback.WireWindow? window = null, ulong revision = 0, bool privateSession = false, string output = "",
        string sender = "", uint lastCommandMessageId = 0, long startedPlayingAtMs = 0, string uid = "")
    {
        var identity = new Playback.DeviceIdentity("wavee-device-id", "PC", "65b708073fc0480ea92a077233ca87bd",
            "Win32_x86_64", "1.2.94.583.g60394bd5", "3.2.6");
        var wire = new Playback.WireExtras(window, revision, in Ids, privateSession, output, sender);
        return new Playback.Snapshot(in identity, isActive: true, reason: Playback.PublishReason.PlayerStateChanged,
            messageId: 9, volume: 32_768, clientTimestampMs: 1_700_000_050_000, startedPlayingAtMs: startedPlayingAtMs,
            hasBeenPlayingForMs: 4_000, hasTrack: true, track: Track, context: context.IsEmpty ? Playlist : context, uid: uid,
            positionAsOfMs: 42_000, timestampMs: 1_700_000_050_000, durationMs: 200_000,
            isPlaying: !paused && !buffering, isPaused: paused, isBuffering: buffering, shuffling: false,
            repeat: RepeatMode.Off, kind: kind, hasVideo: hasVideo, lastCommandMessageId: lastCommandMessageId,
            videoGid: videoGid, wire: in wire);
    }

    static P.PutStateRequest Encode(in Playback.Snapshot snapshot, long frameToUnixMs = 0)
    {
        byte[] buffer = new byte[Spotify.Decode.PutStateCapacity(in snapshot)];
        int written = Spotify.Decode.PutState(in snapshot, buffer, frameToUnixMs);
        Assert.True(written > 0 && written <= buffer.Length);
        return P.PutStateRequest.Parser.ParseFrom(buffer, 0, written);
    }

    static P.PlayerState Player(in Playback.Snapshot snapshot) => Encode(in snapshot).Device.PlayerState;

    static string[] ReasonsFor(P.PlayerState ps, string signal)
        => ps.Restrictions.DisallowSignals.TryGetValue(signal, out var r) ? [.. r.Reasons] : [];

    // ── attribution (G-073, G-246) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_put_names_the_controller_command_it_answers_and_the_device_that_sent_it()
    {
        var request = Encode(Snap(lastCommandMessageId: 604_162_001, sender: "controller-device"));

        Assert.Equal(604_162_001u, request.LastCommandMessageId);
        Assert.Equal("controller-device", request.LastCommandSentByDeviceId);
        Assert.Equal(0u, Encode(Snap()).LastCommandMessageId);
        Assert.Equal("", Encode(Snap()).LastCommandSentByDeviceId);
    }

    [Fact]
    public void Started_playing_at_is_moved_from_the_frame_clock_onto_unix_ms()
    {
        // The reducer stamped the frame clock at 5 000 ms; the glue measured unix − frame = 1 700 000 000 000.
        Assert.Equal(1_700_000_005_000UL, Encode(Snap(startedPlayingAtMs: 5_000), frameToUnixMs: 1_700_000_000_000).StartedPlayingAt);
    }

    [Theory]
    [InlineData(0L, 1_700_000_000_000L, 0UL)]
    [InlineData(-3L, 1_700_000_000_000L, 0UL)]
    [InlineData(5_000L, 1_700_000_000_000L, 1_700_000_005_000UL)]
    [InlineData(1_700_000_005_000L, 0L, 1_700_000_005_000UL)]
    public void A_stamp_never_made_is_not_written_and_one_that_was_is_offset(long started, long offset, ulong expected)
        => Assert.Equal(expected, Spotify.Decode.StartedPlayingAt(started, offset));

    // ── the device half ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_device_says_private_session_and_the_endpoint_it_renders_to_with_the_captured_constants()
    {
        var info = Encode(Snap(privateSession: true, output: "Speakers (Realtek)")).Device.DeviceInfo;

        Assert.True(info.IsPrivateSession);
        Assert.Equal("Speakers (Realtek)", info.AudioOutputDeviceInfo.DeviceName);
        Assert.Equal(P.AudioOutputDeviceType.UnknownAudioOutputDeviceType, info.AudioOutputDeviceInfo.AudioOutputDeviceType);
        Assert.True(info.AudioOutputDeviceInfo.HasAudioOutputDeviceType);   // explicitly serialized despite being the default
        Assert.Equal(3u, info.AudioOutputDeviceInfo.UnknownField5);
        Assert.True(info.Capabilities.UnknownCapability33);
        Assert.Equal(15, info.Capabilities.SupportedTypes.Count);
    }

    [Fact]
    public void An_unknown_endpoint_is_a_nameless_one_never_another_machines_and_a_public_session_is_not_private()
    {
        var info = Encode(Snap()).Device.DeviceInfo;

        Assert.False(info.IsPrivateSession);
        Assert.Equal("", info.AudioOutputDeviceInfo.DeviceName);
        Assert.True(info.AudioOutputDeviceInfo.HasDeviceName);
        Assert.Equal(3u, info.AudioOutputDeviceInfo.UnknownField5);
    }

    // ── a pause reads as a pause (G-240) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Paused_keeps_is_playing_true_drops_the_speed_to_zero_and_says_already_paused()
    {
        var ps = Player(Snap(paused: true, window: Window()));

        Assert.True(ps.IsPlaying);
        Assert.True(ps.IsPaused);
        Assert.Equal(0.0, ps.PlaybackSpeed);
        Assert.Contains("already_paused", ps.Restrictions.DisallowPausingReasons);
        Assert.Contains("no_prev_track", ps.Restrictions.DisallowSkippingPrevReasons);   // index 0 and nothing behind
        Assert.Empty(ps.Restrictions.DisallowResumingReasons);
    }

    [Fact]
    public void Playing_says_not_paused_at_speed_one_and_a_paused_row_with_history_can_still_go_back()
    {
        var playing = Player(Snap(window: Window()));
        Assert.Equal(1.0, playing.PlaybackSpeed);
        Assert.Contains("not_paused", playing.Restrictions.DisallowResumingReasons);
        Assert.Empty(playing.Restrictions.DisallowPausingReasons);

        var paused = Player(Snap(paused: true, window: Window(prev: [Row(1)], contextIndex: 1)));
        Assert.Empty(paused.Restrictions.DisallowSkippingPrevReasons);
    }

    [Fact]
    public void A_row_still_opening_is_the_engaged_transport_buffering()
    {
        var ps = Player(Snap(buffering: true));

        Assert.True(ps.IsPlaying);
        Assert.True(ps.IsBuffering);
        Assert.Contains("not_paused", ps.Restrictions.DisallowResumingReasons);
    }

    // ── the queue a controller shows (G-240) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_context_its_url_the_index_the_origin_the_revision_the_ids_and_the_quality_ride_the_player_state()
    {
        var ps = Player(Snap(window: Window(prev: [Row(1), Row(2)], contextIndex: 5), revision: 17_146_072_722_624_078_579UL));

        Assert.Equal(Playlist.Text, ps.ContextUri);
        Assert.Equal("context://" + Playlist.Text, ps.ContextUrl);
        Assert.Equal(5u, ps.Index.Track);
        Assert.Equal("playlist", ps.PlayOrigin.FeatureIdentifier);
        Assert.Equal("playlist", ps.PlayOrigin.ReferrerIdentifier);
        Assert.Equal(Spotify.Decode.FeatureVersion, ps.PlayOrigin.FeatureVersion);
        Assert.Equal("17146072722624078579", ps.QueueRevision);
        Assert.Equal("00000000000000000000000000001111", ps.SessionId);
        Assert.Equal("00000000000000000000000000002222", ps.PlaybackId);
        Assert.Equal("00000000000000000000000000003333", ps.SessionCommandId);
        Assert.Equal("2", ps.ContextMetadata["player.arch"]);
        Assert.Equal(P.BitrateLevel.High, ps.PlaybackQuality.BitrateLevel);
        Assert.Equal(P.BitrateStrategy.CachedFile, ps.PlaybackQuality.Strategy);
        Assert.Equal(P.BitrateLevel.High, ps.PlaybackQuality.TargetBitrateLevel);
        Assert.True(ps.PlaybackQuality.TargetBitrateAvailable);
        Assert.Equal(P.HiFiStatus.Off, ps.PlaybackQuality.HifiStatus);
    }

    [Fact]
    public void Prev_and_next_carry_their_rows_with_provider_uid_and_the_metadata_a_controller_paints()
    {
        var window = Window(
            prev: [Row(1, uid: "5bd5aabfe4434940c96f")],
            next:
            [
                Row(2, QueueProvider.Queue, uid: "q2"),
                Row(3, image: "https://i.scdn.co/image/abc", itemId: 0x0ab15c9f39e1de3bUL),
                Row(4, QueueProvider.Autoplay),
            ],
            contextIndex: 1);
        var ps = Player(Snap(window: window));

        Assert.Single(ps.PrevTracks);
        Assert.Equal(TrackId(1).Text, ps.PrevTracks[0].Uri);
        Assert.Equal("5bd5aabfe4434940c96f", ps.PrevTracks[0].Uid);
        Assert.False(ps.PrevTracks[0].Metadata.ContainsKey("view_index"));

        Assert.Equal(3, ps.NextTracks.Count);
        var queued = ps.NextTracks[0];
        Assert.Equal("queue", queued.Provider);
        Assert.Equal("q2", queued.Uid);
        Assert.Equal("true", queued.Metadata["is_queued"]);
        Assert.False(queued.Metadata.ContainsKey("context_uri"));
        Assert.False(queued.Metadata.ContainsKey("iteration"));

        var context = ps.NextTracks[1].Metadata;
        Assert.Equal("context", ps.NextTracks[1].Provider);
        Assert.Equal("0ab15c9f39e1de3b", ps.NextTracks[1].Uid);                 // a packed 16-hex uid formats itself back
        Assert.Equal("Title 3", context["title"]);
        Assert.Equal("Artist 3", context["artist_name"]);
        Assert.Equal("Album 3", context["album_title"]);
        Assert.Equal(Album.Text, context["album_uri"]);
        Assert.Equal(Artist.Text, context["artist_uri"]);
        Assert.Equal("audio", context["track_player"]);
        Assert.Equal(Playlist.Text, context["context_uri"]);
        Assert.Equal(Playlist.Text, context["entity_uri"]);
        Assert.Equal("2", context["view_index"]);                               // the deck is 1, the queued row is not numbered
        Assert.Equal("0", context["iteration"]);
        Assert.Equal("spotify:image:abc", context["image_url"]);
        Assert.Equal("spotify:image:abc", context["image_xlarge_url"]);
        Assert.Equal("resume", context["actions.skipping_next_past_track"]);
        Assert.Equal("00000000-0000-0000-0000-000000004444", context["interaction_id"]);
        Assert.Equal("00000000-0000-0000-0000-000000005555", context["page_instance_id"]);

        var autoplay = ps.NextTracks[2];
        Assert.Equal("autoplay", autoplay.Provider);
        Assert.Equal("true", autoplay.Metadata["autoplay.is_autoplay"]);
        Assert.False(autoplay.Metadata.ContainsKey("context_uri"));
        Assert.False(autoplay.Metadata.ContainsKey("entity_uri"));
    }

    [Fact]
    public void The_current_row_carries_its_metadata_and_its_provenance()
    {
        var current = new Playback.WireRow(Track, 0, null, QueueProvider.Queue, "Now", "Someone", "Somewhere", "ab67616d0000b273",
            Album, Artist);
        var track = Player(Snap(window: Window(current: current), uid: "q7")).Track;

        Assert.Equal(Track.Text, track.Uri);
        Assert.Equal("q7", track.Uid);
        Assert.Equal("queue", track.Provider);
        Assert.Equal("Now", track.Metadata["title"]);
        Assert.Equal("spotify:image:ab67616d0000b273", track.Metadata["image_url"]);   // a bare image id becomes a uri
        Assert.Equal("true", track.Metadata["is_queued"]);
    }

    [Fact]
    public void A_window_keeps_the_newest_fifty_behind_and_the_first_fifty_ahead()
    {
        var prev = new Playback.WireRow[60];
        var next = new Playback.WireRow[70];
        for (int i = 0; i < prev.Length; i++) prev[i] = Row(i);
        for (int i = 0; i < next.Length; i++) next[i] = Row(100 + i);

        var window = Window(prev, next);
        Assert.Equal(Playback.WireWindow.MaxPrev, window.Prev.Length);
        Assert.Equal(TrackId(10), window.Prev[0].Id);
        Assert.Equal(Playback.WireWindow.MaxNext, window.Next.Length);
        Assert.Equal(TrackId(100), window.Next[0].Id);

        var ps = Player(Snap(window: window));
        Assert.Equal(50, ps.PrevTracks.Count);
        Assert.Equal(50, ps.NextTracks.Count);
    }

    // ── track_player and the video offer (0.2.9 M0, bug 3, bug 7; D37) ─────────────────────────────────────────────

    [Theory]
    [InlineData(Playback.PlayableKind.Video, "video")]
    [InlineData(Playback.PlayableKind.Audio, "audio")]
    [InlineData(Playback.PlayableKind.LocalFile, "audio")]
    public void Track_player_follows_the_current_media_kind(Playback.PlayableKind kind, string expected)
        => Assert.Equal(expected, Player(Snap(kind, hasVideo: true, videoGid: Gid)).Track.Metadata["track_player"]);

    [Fact]
    public void The_current_media_kind_never_leaks_onto_next_tracks()
    {
        var ps = Player(Snap(Playback.PlayableKind.Video, hasVideo: true, videoGid: Gid, window: Window(next: [Row(1)])));

        Assert.Equal("video", ps.Track.Metadata["track_player"]);
        Assert.Equal("audio", ps.NextTracks[0].Metadata["track_player"]);
        Assert.False(ps.NextTracks[0].Metadata.ContainsKey("associated_video_id"));
    }

    [Fact]
    public void A_row_on_the_video_host_says_media_type_manifest_and_start_position_and_drops_entity_uri()
    {
        var metadata = Player(Snap(Playback.PlayableKind.Video, hasVideo: true, videoGid: Gid, window: Window())).Track.Metadata;

        Assert.Equal("video", metadata["media.type"]);
        Assert.Equal(Gid, metadata["media.manifest_id"]);
        Assert.Equal(Gid, metadata["associated_video_id"]);
        Assert.Equal("42000", metadata["media.start_position"]);
        Assert.False(metadata.ContainsKey("entity_uri"));
        Assert.False(metadata.ContainsKey("view_index"));
    }

    [Fact]
    public void A_video_capable_row_played_as_audio_never_claims_a_video_session()
    {
        var metadata = Player(Snap(Playback.PlayableKind.Audio, hasVideo: true, videoGid: Gid)).Track.Metadata;

        Assert.Equal("audio", metadata["track_player"]);
        Assert.False(metadata.ContainsKey("media.type"));
        Assert.False(metadata.ContainsKey("media.manifest_id"));
        Assert.False(metadata.ContainsKey("media.start_position"));
    }

    [Fact]
    public void A_known_video_offers_switch_to_video_first_and_disallows_only_switch_to_audio()
    {
        var ps = Player(Snap(Playback.PlayableKind.Audio, hasVideo: true, videoGid: Gid));

        Assert.Equal(new[] { "switch-to-video", "interact", "automix-preview", "speed-preview", "stop-speed-preview" }, ps.Signals);
        Assert.Equal(new[] { "no_associated_track" },ReasonsFor(ps, "switch-to-audio"));
        Assert.False(ps.Restrictions.DisallowSignals.ContainsKey("switch-to-video"));
    }

    [Fact]
    public void With_no_video_gid_both_switches_are_disallowed_and_only_the_baseline_signals_ride()
    {
        var ps = Player(Snap(Playback.PlayableKind.Audio, hasVideo: false));

        Assert.Equal(new[] { "interact", "automix-preview", "speed-preview", "stop-speed-preview" }, ps.Signals);
        Assert.Equal(new[] { "no_associated_track" },ReasonsFor(ps, "switch-to-video"));
        Assert.Equal(new[] { "no_associated_track" },ReasonsFor(ps, "switch-to-audio"));
        Assert.False(ps.Track.Metadata.ContainsKey("associated_video_id"));
    }

    [Fact]
    public void The_video_host_offers_the_switch_back_to_audio()
    {
        var ps = Player(Snap(Playback.PlayableKind.Video, hasVideo: true, videoGid: Gid));

        Assert.Equal("switch-to-audio", ps.Signals[0]);
        Assert.Equal(new[] { "no_associated_track" },ReasonsFor(ps, "switch-to-video"));
        Assert.False(ps.Restrictions.DisallowSignals.ContainsKey("switch-to-audio"));
    }

    // ── the constants desktop always sends (24/24 captures) ─────────────────────────────────────────────────────────

    [Fact]
    public void The_constant_restrictions_and_the_empty_but_present_submessages_ride_every_track()
    {
        var ps = Player(Snap());

        Assert.Equal(new[] { "not_supported_by_content_type" },ps.Restrictions.DisallowSettingPlaybackSpeedReasons);
        Assert.Equal(new[] { "already_set" }, ps.Restrictions.UnknownDisallowReasons31);
        Assert.Empty(ps.Restrictions.DisallowAddToQueueReasons);
        Assert.NotNull(ps.ContextRestrictions);
        Assert.NotNull(ps.Suppressions);
        Assert.Empty(ps.Suppressions.Providers);
        Assert.NotNull(ps.UnknownField38);
        Assert.True(ps.UnknownField38.HasUnknown1);
        Assert.Equal("", ps.UnknownField38.Unknown1);
    }

    [Fact]
    public void Enhance_is_disallowed_everywhere_but_a_playlist()
    {
        Assert.Empty(Player(Snap(context: Playlist)).Restrictions.DisallowSettingModes);

        var album = Player(Snap(context: AlbumContext)).Restrictions.DisallowSettingModes;
        Assert.True(album.TryGetValue("context_enhancement", out var mode));
        Assert.True(mode!.Values.TryGetValue("RECOMMENDATION", out var reasons));
        Assert.Equal(new[] { "not_supported_by_content_type" },reasons!.Reasons);
        Assert.Single(album);
    }

    [Fact]
    public void The_three_player_option_modes_ride_with_media_explicitly_empty()
    {
        var modes = Player(Snap()).Options.Modes;

        Assert.Equal(new[] { "context_enhancement", "media", "jam" }, modes.Select(m => m.Key));
        Assert.Equal(new[] { "NONE", "", "off" }, modes.Select(m => m.Value));
        Assert.True(modes[1].HasValue);
    }

    [Theory]
    [InlineData("spotify:user:someone:collection", "your_library")]
    [InlineData("spotify:album:1TSZDcvlPtAnekTaItI3qO", "album")]
    [InlineData("spotify:artist:4Z8W4fKeB5YxbusRsdQVPb", "artist")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "playlist")]
    [InlineData("spotify:episode:0Q86acNRm6V9GYx55SXKwf", "home")]
    [InlineData("spotify:station:track:7idegBIikag5rTZP4WZihP", "harmony")]
    [InlineData("", "harmony")]
    public void The_play_origin_feature_is_read_off_the_context_uri(string context, string feature)
        => Assert.Equal(feature, System.Text.Encoding.UTF8.GetString(Spotify.Decode.FeatureOf(System.Text.Encoding.UTF8.GetBytes(context))));

    // ── an idle player, and the budget ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_inactive_put_is_our_volume_and_an_idle_player_state()
    {
        var identity = new Playback.DeviceIdentity("wavee-device-id", "PC", "cid", "Win32_x86_64", "1.2.3", "3.2.6");
        var s = Playback.State.Initial;
        s.Volume = 0.25f;
        var request = Encode(Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.BecameInactive, 1, 10_000, 0));

        Assert.False(request.IsActive);
        Assert.Equal((uint)Math.Round(0.25 * Playback.MaxWireVolume), request.Device.DeviceInfo.Volume);
        Assert.Null(request.Device.PlayerState.Track);
        Assert.Equal("", request.Device.PlayerState.ContextUri);
        Assert.Empty(request.Device.PlayerState.Signals);
        Assert.Equal(10_000L, request.Device.PlayerState.Timestamp);
    }

    [Fact]
    public void A_warm_encode_of_a_fifty_and_fifty_window_allocates_nothing()
    {
        var prev = new Playback.WireRow[50];
        var next = new Playback.WireRow[50];
        for (int i = 0; i < 50; i++)
        {
            prev[i] = Row(i, image: "https://i.scdn.co/image/ab67616d0000b273" + i.ToString("x4"), uid: "5bd5aabfe4434940c96f");
            next[i] = Row(100 + i, i % 5 == 0 ? QueueProvider.Queue : QueueProvider.Context, image: "ab67616d00001e02", itemId: (ulong)(i + 1));
        }
        var snapshot = Snap(Playback.PlayableKind.Video, hasVideo: true, videoGid: Gid, window: Window(prev, next, 50),
            revision: 42, sender: "phone", lastCommandMessageId: 7, startedPlayingAtMs: 5_000, uid: "q1", output: "Speakers");
        byte[] buffer = new byte[Spotify.Decode.PutStateCapacity(in snapshot)];
        for (int i = 0; i < 4; i++) Spotify.Decode.PutState(in snapshot, buffer, 1_700_000_000_000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) Spotify.Decode.PutState(in snapshot, buffer, 1_700_000_000_000);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
        var ps = P.PutStateRequest.Parser.ParseFrom(buffer, 0, Spotify.Decode.PutState(in snapshot, buffer, 1_700_000_000_000))
            .Device.PlayerState;
        Assert.Equal(50, ps.PrevTracks.Count);
        Assert.Equal(50, ps.NextTracks.Count);
    }
}
