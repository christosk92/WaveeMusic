// ── Wavee.Tests/VideoHostRulesTests.cs — the video HOST's decisions, pinned without an engine, a CDM or a socket ──────
//
// Spec: docs/plans/wavee/wavee-0.3-video-engine-implementation.md §4.1 (the `Video.Plan` and `Manifest.Parse` rows),
// §3.1.5, §3.2.3, §3.4; ch 24 §7 DATA GAPS 1-4; gap register G-056, G-058, G-141, G-142, G-144, G-147, G-148, G-149,
// G-150. Every fact is over a pure function in `Playback/Playback.Video.cs`, `Playback/Playback.Video.Source.cs` or
// `Shell/Video.Host.Wiring.cs`: values in, a value out. The wire fixtures are BUILT from the generated protobuf
// messages (the DecodeTests precedent) and the manifest fixture is a reduced copy of 0.2.9's captured v9 manifest —
// numeric profile ids, a Widevine index before the PlayReady one, an Opus rung under the PlayReady index, TS and WebM
// rungs, and the duration on the ROOT only.

using Google.Protobuf;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Wavee;
using Xunit;

using static Wavee.Video;
using Md = Wavee.Protocol.Metadata;
using V = Wavee.Playback.Video;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

// ── the switch plan (0.2.9's VideoSwitchPolicyTests, re-hosted) ──────────────────────────────────────────────────────

public class VideoSwitchPlanTests
{
    [Theory]
    [InlineData(false, false, "", "A", 0, V.SwitchAction.Rebuild)]
    [InlineData(false, false, "", "A", 5_000, V.SwitchAction.Rebuild)]
    [InlineData(false, false, "A", "A", 0, V.SwitchAction.Rebuild)]      // a stale live key left over from a Stop
    [InlineData(false, true, "", "A", 0, V.SwitchAction.Rebuild)]
    [InlineData(true, true, "A", "A", 0, V.SwitchAction.Rebuild)]        // faulted: never trusted, even for the same key
    [InlineData(true, true, "A", "A", 5_000, V.SwitchAction.Rebuild)]
    [InlineData(true, true, "A", "B", 0, V.SwitchAction.Rebuild)]
    [InlineData(true, false, "A", "A", 0, V.SwitchAction.None)]          // a placement move re-asks: never restart from 0
    [InlineData(true, false, "A", "A", -1, V.SwitchAction.None)]
    [InlineData(true, false, "A", "A", 1, V.SwitchAction.SeekOnly)]
    [InlineData(true, false, "A", "A", 126_034, V.SwitchAction.SeekOnly)]
    [InlineData(true, false, "A", "B", 0, V.SwitchAction.Switch)]
    [InlineData(true, false, "A", "B", 5_000, V.SwitchAction.Switch)]    // the carried start rides the open, not a seek
    [InlineData(true, false, "", "B", 0, V.SwitchAction.Switch)]
    [InlineData(true, false, "", "", 0, V.SwitchAction.None)]
    [InlineData(true, false, "", "", 250, V.SwitchAction.SeekOnly)]
    public void A_load_does_exactly_what_the_live_session_allows(bool hasPlayer, bool faulted, string liveKey, string requestKey,
        long startAtMs, V.SwitchAction expected)
        => Assert.Equal(expected, V.Plan(new V.SwitchInput(hasPlayer, faulted, liveKey, requestKey, startAtMs)));

    [Fact]
    public void Two_manifest_ids_that_differ_only_in_case_are_two_videos()
        => Assert.Equal(V.SwitchAction.Switch, V.Plan(new V.SwitchInput(true, false, "ABC", "abc", 0)));
}

// ── the open: where it lands, the phase, the seek call, the quality ceiling ───────────────────────────────────────────

public class VideoHostOpenRulesTests
{
    [Fact]
    public void S1_the_open_lands_at_the_carried_position_and_never_on_the_credits()
    {
        Assert.Equal(83_000L, V.HostRules.StartAt(83_000, 217_561));
        Assert.Equal(217_561 - V.HostRules.StartClampBackoffMs, V.HostRules.StartAt(217_400, 217_561));
        Assert.Equal(0L, V.HostRules.StartAt(-5, 0));
        Assert.Equal(5_000L, V.HostRules.StartAt(5_000, 0));       // an unknown duration clamps nothing
    }

    [Fact]
    public void The_engine_phase_reaches_the_surfaces_name_for_name()
    {
        (ProtectedVideoPhase Engine, V.SwitchPhase App)[] pairs =
        [
            (ProtectedVideoPhase.Idle, V.SwitchPhase.Idle), (ProtectedVideoPhase.Resolving, V.SwitchPhase.Resolving),
            (ProtectedVideoPhase.Licensing, V.SwitchPhase.Licensing), (ProtectedVideoPhase.Buffering, V.SwitchPhase.Buffering),
            (ProtectedVideoPhase.Attaching, V.SwitchPhase.Attaching), (ProtectedVideoPhase.Presenting, V.SwitchPhase.Presenting),
            (ProtectedVideoPhase.Playing, V.SwitchPhase.Playing), (ProtectedVideoPhase.Failed, V.SwitchPhase.Failed),
        ];
        foreach (var (engine, app) in pairs) Assert.Equal(app, V.HostRules.PhaseOf(engine));
    }

    [Fact]
    public void A_clear_source_presents_once_it_has_a_frame_size_and_not_before()
    {
        Assert.Equal(V.SwitchPhase.Playing, V.HostRules.PhaseOfClear(PlaybackState.Playing, framePresented: true));
        Assert.Equal(V.SwitchPhase.Attaching, V.HostRules.PhaseOfClear(PlaybackState.Playing, framePresented: false));
        Assert.Equal(V.SwitchPhase.Presenting, V.HostRules.PhaseOfClear(PlaybackState.Paused, framePresented: true));
        Assert.Equal(V.SwitchPhase.Buffering, V.HostRules.PhaseOfClear(PlaybackState.Opening, framePresented: false));
        Assert.Equal(V.SwitchPhase.Failed, V.HostRules.PhaseOfClear(PlaybackState.Failed, framePresented: true));
        Assert.Equal(V.SwitchPhase.Idle, V.HostRules.PhaseOfClear(PlaybackState.Idle, framePresented: false));
    }

    [Fact]
    public void S8_a_scrub_preview_shows_the_planned_keyframe_and_never_decodes_to_the_pointer()
    {
        long[] keyframes = [0, 2_000, 4_000, 6_000, 8_000];
        long[] buffered = [0, 10_000];
        var ix = new V.SeekIndex(keyframes, buffered, 4_000, 200_000, 1_000, playing: true);

        V.SeekPlan plan = V.SeekPlanner.Plan(in ix, 7_300, V.SeekIntent.Preview);
        var call = V.HostRules.SeekCallFor(in plan, 7_300, V.SeekIntent.Preview, 4_000);
        Assert.True(call.Engine);
        Assert.False(call.Accurate);
        Assert.Equal(plan.KeyframeMs, call.TargetMs);
        Assert.Equal(plan.KeyframeMs, call.KeyframeHintMs);
    }

    [Fact]
    public void A_committed_seek_decodes_to_the_target_and_hands_native_the_keyframe()
    {
        long[] keyframes = [0, 4_000, 8_000];
        long[] buffered = [0, 12_000];
        var ix = new V.SeekIndex(keyframes, buffered, 4_000, 200_000, 1_000, playing: true);

        V.SeekPlan instant = V.SeekPlanner.Plan(in ix, 9_500, V.SeekIntent.Commit);
        Assert.Equal(new V.HostRules.SeekCall(true, 9_500, true, 8_000), V.HostRules.SeekCallFor(in instant, 9_500, V.SeekIntent.Commit, 4_000));

        var far = new V.SeekIndex(keyframes, buffered, 4_000, 200_000, 1_000, playing: true);
        V.SeekPlan fetch = V.SeekPlanner.Plan(in far, 185_500, V.SeekIntent.Commit);
        Assert.Equal(V.SeekVerb.Fetch, fetch.Verb);
        Assert.Equal(new V.HostRules.SeekCall(true, 185_500, true, 184_000), V.HostRules.SeekCallFor(in fetch, 185_500, V.SeekIntent.Commit, 4_000));
    }

    [Fact]
    public void A_ride_makes_no_engine_call()
    {
        var ride = new V.SeekPlan(V.SeekVerb.Ride, 1_000, 0, 0);
        Assert.False(V.HostRules.SeekCallFor(in ride, 1_200, V.SeekIntent.Commit, 4_000).Engine);
    }

    [Fact]
    public void With_no_segment_grid_a_fetch_seeks_the_raw_target_and_gives_native_no_hint()
    {
        // A segment start of 0 is not a position when the grid is unknown: a preview there would jump to 0:00.
        var fetch = new V.SeekPlan(V.SeekVerb.Fetch, 0, 0, 0);
        Assert.Equal(new V.HostRules.SeekCall(true, 50_000, false, -1), V.HostRules.SeekCallFor(in fetch, 50_000, V.SeekIntent.Preview, 0));
        Assert.Equal(new V.HostRules.SeekCall(true, 50_000, true, -1), V.HostRules.SeekCallFor(in fetch, 50_000, V.SeekIntent.Commit, 0));
    }

    [Theory]
    [InlineData(0, int.MaxValue, int.MaxValue)]      // auto, unmetered: no ceiling
    [InlineData(720, int.MaxValue, 720)]             // the user's pin
    [InlineData(0, 480, 480)]                        // auto on a metered link
    [InlineData(1080, 480, 480)]                     // the metered cap outranks a higher pin
    [InlineData(360, 480, 360)]                      // …and never raises a lower one
    [InlineData(1080, 0, 1080)]                      // a stored cap of 0 means unlimited
    public void G148_the_quality_ceiling_is_the_lower_of_the_pin_and_the_metered_cap(int pin, int metered, int expected)
        => Assert.Equal(expected, V.HostRules.QualityCap(pin, metered));
}

// ── the v9 manifest (G-147, the KID form, numeric profile ids) ────────────────────────────────────────────────────────

public class VideoManifestParseTests
{
    const string Kid = "bfef92b1bb10b936eb1000638cf2d57a";

    const string Real = """
    {
      "contents": [
        {
          "encoding_id": "e04cc9307e5311f18f2b7ba46f79bc65",
          "segment_length": 4,
          "profiles": [
            { "id": 13, "audio_bitrate": 160000, "audio_codec": "mp4a.40.2", "file_type": "mp4", "encryption_indices": [0, 1], "max_bitrate": 167662, "key_id": "v++SsbsQuTbrEABjjPLVeg==" },
            { "id": 12, "audio_bitrate": 96000, "audio_codec": "mp4a.40.2", "file_type": "mp4", "encryption_indices": [0, 1], "max_bitrate": 100000, "key_id": "v++SsbsQuTbrEABjjPLVeg==" },
            { "id": 19, "audio_codec": "opus", "file_type": "mp4", "encryption_indices": [0, 1], "max_bitrate": 999999, "key_id": "v++SsbsQuTbrEABjjPLVeg==" },
            { "id": 5, "file_type": "mp4", "encryption_indices": [0, 1], "max_bitrate": 368818, "video_codec": "avc1.4d400d", "video_height": 180, "video_width": 320, "key_id": "v++SsbsQuTbrEABjjPLVeg==" },
            { "id": 1, "file_type": "mp4", "encryption_indices": [0, 1], "max_bitrate": 5364554, "video_codec": "avc1.4d4020", "video_height": 720, "video_width": 1280, "key_id": "v++SsbsQuTbrEABjjPLVeg==" },
            { "id": 2, "file_type": "mp4", "encryption_indices": [0, 1], "max_bitrate": 2425450, "video_codec": "avc1.4d401f", "video_height": 480, "video_width": 854, "key_id": "v++SsbsQuTbrEABjjPLVeg==" },
            { "id": 24, "file_type": "ts", "encryption_indices": [2], "video_codec": "avc1.4d400d", "video_height": 180, "key_id": "v++SsbsQuTbrEABjjPLVevrM" },
            { "id": 17, "file_type": "webm", "encryption_indices": [0, 1], "video_codec": "vp9", "video_height": 1080, "key_id": "v++SsbsQuTbrEABjjPLVeg==" }
          ],
          "encryption_infos": [
            { "key_system": "widevine", "license_server_endpoint": "/widevine-license/v1/video/license", "encryption_data": "AAAA" },
            { "key_system": "playready", "license_server_endpoint": "/playready-license/v1/video/license", "encryption_data": "AAAAIHBzc2g=" }
          ]
        }
      ],
      "end_time_millis": 217561,
      "initialization_template": "v1/origins/o/sources/s/encodings/e/profiles/{{profile_id}}/inits/{{file_type}}?token=SCRUBBED",
      "segment_template": "v1/origins/o/sources/s/encodings/e/profiles/{{profile_id}}/{{segment_timestamp}}.{{file_type}}?token=SCRUBBED",
      "base_urls": ["https://video-fa.scdn.co/segments/", "https://video-cf.spotifycdn.com/segments/"]
    }
    """;

    static V.Manifest.Parsed Parse(string json)
        => V.Manifest.Parse(System.Text.Encoding.UTF8.GetBytes(json)) ?? throw new Xunit.Sdk.XunitException("the manifest did not parse");

    [Fact]
    public void A_real_manifest_with_numeric_profile_ids_parses_into_its_h264_ladder()
    {
        // The real wire spells every profile id as a NUMBER; a string-only read skipped them all and every music video
        // became "no video".
        V.Manifest.Parsed m = Parse(Real);
        Assert.Collection(m.Video,
            r => Assert.Equal(("5", 180), (r.Id, r.Height)),
            r => Assert.Equal(("2", 480), (r.Id, r.Height)),
            r => Assert.Equal(("1", 720), (r.Id, r.Height)));
        Assert.Equal((1280, 720), (m.NaturalWidth, m.NaturalHeight));
        Assert.Equal(4, m.SegmentLengthSeconds);
        Assert.Equal("/playready-license/v1/video/license", m.LicenseEndpoint);
        Assert.Equal(8, m.Pssh.Length);
    }

    [Fact]
    public void G147_the_duration_falls_back_to_the_roots_end_time_with_a_missing_start_as_zero()
        => Assert.Equal(217_561L, Parse(Real).DurationMs);

    [Fact]
    public void The_soundtrack_is_the_highest_bitrate_AAC_and_never_the_Opus_rung_under_the_same_index()
    {
        V.Manifest.Parsed m = Parse(Real);
        Assert.True(m.HasAudio);
        Assert.Equal("13", m.Audio.Id);
        Assert.StartsWith("mp4a", m.Audio.Codec, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_id_reaches_the_licence_cache_as_32_lowercase_hex()
    {
        // The wire's base64 KID handed to the native acquire as-is is an E_INVALIDARG: the licence never starts.
        V.Manifest.Parsed m = Parse(Real);
        Assert.Equal(Kid, m.DefaultKid);
        Assert.All(m.Video, r => Assert.Equal(Kid, r.KeyId));
    }

    [Theory]
    [InlineData("v++SsbsQuTbrEABjjPLVeg==", Kid)]
    [InlineData("BFEF92B1-BB10-B936-EB10-00638CF2D57A", Kid)]
    [InlineData("bfef92b1bb10b936eb1000638cf2d57a", Kid)]
    [InlineData("v++SsbsQuTbrEABjjPLVevrM", null)]     // 18 bytes: the TS rungs' id is not a CENC KID
    [InlineData("not-a-kid", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_key_id_is_normalised_or_dropped_never_passed_through(string? wire, string? expected)
        => Assert.Equal(expected, V.Manifest.KeyIdHex(wire));

    [Fact]
    public void The_descriptor_carries_the_seek_grid_the_duration_and_a_conservative_first_rung()
    {
        V.Manifest.Parsed m = Parse(Real);
        DashSourceDescriptor d = V.Manifest.ToDescriptor(in m) ?? throw new Xunit.Sdk.XunitException("no descriptor");

        Assert.Equal(4_000, d.SegmentLengthMs);
        Assert.Equal(217_561L, d.DurationMs);
        Assert.True(d.SegmentsStartWithKeyframe);
        Assert.Equal(55, d.SegmentCount);               // ceil(217.561 s / 4 s): the tail is a partial segment
        Assert.Equal(4, d.SegmentStride);               // Spotify names segments by absolute time
        Assert.Equal(0, d.StartNumber);
        Assert.Equal("2", d.RepresentationId);          // ≤ 480p opens; the ABR climbs
        Assert.Equal(Kid, d.DefaultKid);
        Assert.Equal("https://video-fa.scdn.co/segments/v1/origins/o/sources/s/encodings/e/profiles/2/inits/mp4?token=SCRUBBED", d.InitUrl);
        Assert.Equal("https://video-fa.scdn.co/segments/v1/origins/o/sources/s/encodings/e/profiles/2/", d.SegmentBaseUrl);
        Assert.Equal("", d.SegmentPrefix);
        Assert.Equal(".mp4?token=SCRUBBED", d.SegmentSuffix);
        Assert.Contains("/profiles/13/", d.AudioInitUrl, StringComparison.Ordinal);
        Assert.Equal(d.SegmentSuffix, d.AudioSegmentSuffix);
        var video = Assert.Single(d.Catalog!.Tracks, t => t.Kind == FluentGpu.Media.TrackKind.Video);
        Assert.Equal(3, video.Representations.Count);
    }

    [Fact]
    public void A_manifest_with_no_PlayReady_info_has_no_video_for_this_pipeline()
    {
        string widevineOnly = Real.Replace("\"key_system\": \"playready\"", "\"key_system\": \"fairplay\"", StringComparison.Ordinal);
        Assert.Null(V.Manifest.Parse(System.Text.Encoding.UTF8.GetBytes(widevineOnly)));
    }

    [Fact]
    public void The_nested_sources_shape_and_a_string_id_parse_too()
    {
        const string nested = """
        { "sources": [ {
            "segment_length": 4, "duration": 8000,
            "initialization_template": "p/{{profile_id}}/init.{{file_type}}",
            "segment_template": "p/{{profile_id}}/{{segment_timestamp}}.{{file_type}}",
            "base_urls": ["https://cdn/"],
            "encryption_infos": [ { "key_system": "PlayReady", "encryption_data": "AAAAIHBzc2g=" } ],
            "profiles": [ { "id": "7", "file_type": "mp4", "video_codec": "avc1.64001f", "video_width": 640, "video_height": 360 } ]
        } ] }
        """;
        V.Manifest.Parsed m = Parse(nested);
        Assert.Equal("7", Assert.Single(m.Video).Id);
        Assert.Equal(8_000L, m.DurationMs);
        Assert.Null(m.LicenseEndpoint);
        Assert.Equal(2, V.Manifest.ToDescriptor(in m)!.SegmentCount);
    }

    [Theory]
    [InlineData(null, "/playready-license")]
    [InlineData("  ", "/playready-license")]
    [InlineData("/playready-license/v1/video/license", "/playready-license/v1/video/license")]
    [InlineData("https://spclient.wg.spotify.com/playready-license/v1/video/license?x=1", "/playready-license/v1/video/license?x=1")]
    [InlineData("@webgate/playready-license", "/webgate/playready-license")]
    [InlineData("https://host.only", "/playready-license")]
    public void The_licence_endpoint_becomes_a_path_on_the_spclient_host(string? endpoint, string expected)
        => Assert.Equal(expected, V.License.Normalize(endpoint));

    [Fact]
    public void G144_the_manifest_and_licence_routes_carry_0_2_9s_request_shape()
    {
        const Spotify.HeaderSet manifest = V.Manifest.RequestHeaders;
        Assert.Equal(Spotify.HeaderSet.XpuiOrigin, manifest & Spotify.HeaderSet.XpuiOrigin);
        Assert.Equal(Spotify.HeaderSet.AcceptAny, manifest & Spotify.HeaderSet.AcceptAny);
        Assert.Equal(Spotify.HeaderSet.None, manifest & (Spotify.HeaderSet.Origin | Spotify.HeaderSet.AcceptJson));

        const Spotify.HeaderSet licence = V.License.RequestHeaders;
        Assert.Equal(Spotify.HeaderSet.SoapLicense, licence & Spotify.HeaderSet.SoapLicense);
        Assert.Equal(Spotify.HeaderSet.XpuiOrigin, licence & Spotify.HeaderSet.XpuiOrigin);
        Assert.Equal(Spotify.HeaderSet.None, licence & (Spotify.HeaderSet.Origin | Spotify.HeaderSet.ContentProtobuf));
    }
}

// ── the resolver tiers and the wire tier (G-056, G-148) ───────────────────────────────────────────────────────────────

public class VideoResolverTierTests
{
    const string ManifestId = "0102030405060708090a0b0c0d0e0f10";

    [Fact]
    public void The_users_attachment_always_wins()
    {
        Assert.Equal(V.SourceTier.Override, V.SourceTiers.First(OverrideTier.UseOverride, spotifyTrack: true, ManifestId, chained: true));
        Assert.Equal(V.SourceTier.Override, V.SourceTiers.First(OverrideTier.UseOverride, spotifyTrack: false, null, chained: true));
    }

    [Fact]
    public void A_broken_or_quarantined_attachment_falls_through_and_never_blocks_the_music()
    {
        Assert.Equal(V.SourceTier.Chained, V.SourceTiers.First(OverrideTier.Broken, spotifyTrack: false, null, chained: true));
        Assert.Equal(V.SourceTier.Manifest, V.SourceTiers.First(OverrideTier.Quarantined, spotifyTrack: true, ManifestId, chained: true));
    }

    [Fact]
    public void A_module_row_is_answered_by_the_resolver_installed_before_the_tiers_and_never_by_a_manifest()
    {
        Assert.Equal(V.SourceTier.Chained, V.SourceTiers.First(OverrideTier.None, spotifyTrack: false, ManifestId, chained: true));
        Assert.Equal(V.SourceTier.None, V.SourceTiers.First(OverrideTier.None, spotifyTrack: false, ManifestId, chained: false));
    }

    [Fact]
    public void A_spotify_track_with_no_manifest_id_in_the_catalogue_asks_the_wire()
    {
        Assert.Equal(V.SourceTier.Wire, V.SourceTiers.First(OverrideTier.None, spotifyTrack: true, "", chained: true));
        Assert.Equal(V.SourceTier.Wire, V.SourceTiers.First(OverrideTier.None, spotifyTrack: true, "ABCDEF", chained: false));
    }

    [Theory]
    [InlineData(ManifestId, true)]
    [InlineData("0102030405060708090A0B0C0D0E0F10", false)]
    [InlineData("0102030405060708090a0b0c0d0e0f1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_manifest_id_is_32_lowercase_hex(string? id, bool expected)
        => Assert.Equal(expected, V.SourceTiers.IsManifestId(id));

    static readonly byte[] VideoGid = Convert.FromHexString(ManifestId);
    const string Song = "spotify:track:4uLU6hMCjMI75M1A2tKUQC";
    const string Clip = "spotify:track:0000000000000000000001";

    static Xm.EntityExtensionDataArray Answer(Xm.ExtensionKind kind, string uri, byte[] payload, int status = 200) => new()
    {
        ExtensionKind = kind,
        ExtensionData =
        {
            new Xm.EntityExtensionData
            {
                Header = new Xm.EntityExtensionDataHeader { StatusCode = status },
                EntityUri = uri,
                ExtensionData = new Google.Protobuf.WellKnownTypes.Any { TypeUrl = "type.googleapis.com/x", Value = ByteString.CopyFrom(payload) },
            },
        },
    };

    static byte[] TrackV4(byte[]? videoGid)
    {
        var track = new Md.Track { Name = "t" };
        if (videoGid is not null) track.OriginalVideo.Add(new Md.Video { Gid = ByteString.CopyFrom(videoGid) });
        return track.ToByteArray();
    }

    static byte[] Associated(string uri) => new Xm.VideoAssociations { Association = new Xm.Association { AssociatedUri = uri } }.ToByteArray();

    [Fact]
    public void A_self_contained_music_video_track_names_its_manifest_in_its_own_TrackV4()
    {
        byte[] response = new Xm.BatchedExtensionResponse
        {
            ExtendedMetadata = { Answer(Xm.ExtensionKind.TrackV4, Clip, TrackV4(VideoGid)), Answer(Xm.ExtensionKind.VideoAssociations, Clip, Associated(Song)) },
        }.ToByteArray();

        Assert.Equal(ManifestId, V.Wire.TrackV4Gid(response, Clip));
        Assert.Equal(Song, V.Wire.AssociatedUri(response, Clip));
    }

    [Fact]
    public void A_linked_track_has_no_gid_of_its_own_but_names_the_video_track()
    {
        byte[] response = new Xm.BatchedExtensionResponse
        {
            ExtendedMetadata = { Answer(Xm.ExtensionKind.TrackV4, Song, TrackV4(null)), Answer(Xm.ExtensionKind.VideoAssociations, Song, Associated(Clip)) },
        }.ToByteArray();

        Assert.Equal("", V.Wire.TrackV4Gid(response, Song));
        Assert.Equal(Clip, V.Wire.AssociatedUri(response, Song));
    }

    [Fact]
    public void A_failed_entity_another_uri_or_another_kind_answers_nothing()
    {
        byte[] response = new Xm.BatchedExtensionResponse
        {
            ExtendedMetadata =
            {
                Answer(Xm.ExtensionKind.TrackV4, Clip, TrackV4(VideoGid), status: 404),
                Answer(Xm.ExtensionKind.VideoAssociations, Song, TrackV4(VideoGid)),
            },
        }.ToByteArray();

        Assert.Equal("", V.Wire.TrackV4Gid(response, Clip));   // 404
        Assert.Equal("", V.Wire.TrackV4Gid(response, Song));   // a kind-99 payload is not a TrackV4
        Assert.Equal("", V.Wire.AssociatedUri(response, Clip));
        Assert.Equal("", V.Wire.TrackV4Gid([], Clip));
    }

    [Fact]
    public void A_local_attachment_is_keyed_by_its_path_and_is_not_drm()
    {
        var local = V.VideoSource.LocalFile(@"D:\videos\clip.mp4") with { PlayableUri = Song, OverrideSourceKey = "k" };
        Assert.Equal(@"local:video:D:\videos\clip.mp4", local.Key);
        Assert.False(local.IsDrm);
        Assert.Equal(Song, local.PlayableUri);
    }
}

// ── the placement → playback edges (G-141, G-150) and the size seed (G-058) ───────────────────────────────────────────

public class VideoPlacementEdgeTests
{
    const PlacementSet Host = PlacementSet.Docked | PlacementSet.Floating;
    static readonly PlacementState Off = PlacementState.Music;
    static PlacementState DockedWith(PlacementSet available) => PlacementCore.OpenAt(Off, SurfacePlacement.Docked) with { Available = available };

    [Fact]
    public void G141_turning_video_on_and_off_tells_the_reducer()
    {
        var on = DockedWith(Host);
        Assert.True(PlacementPost.ShouldPost(in Off, in on, Host, out bool wanted));
        Assert.True(wanted);

        var off = PlacementCore.TurnOff(on);
        Assert.True(PlacementPost.ShouldPost(in on, in off, Host, out wanted));
        Assert.False(wanted);
    }

    [Fact]
    public void A_row_without_a_video_keeps_the_intent_so_the_next_video_opens_straight_on_the_video_host()
    {
        var watching = DockedWith(Host);
        var noVideoRow = PlacementCore.WithAvailability(watching, PlacementSet.None);
        Assert.False(PlacementCore.IsActive(noVideoRow));
        Assert.False(PlacementPost.ShouldPost(in watching, in noVideoRow, Host, out bool wanted));
        Assert.True(wanted);
    }

    [Fact]
    public void A_lit_badges_click_re_posts_so_the_reducer_swaps_a_row_already_playing_as_audio()
    {
        // The deferred upgrade: the intent never went off, but the surface did not come on. Committing it is an
        // ACTIVATION, and the reducer must hear it even though the intent did not change.
        var deferred = DockedWith(PlacementSet.None);
        var clicked = UpgradeGate.PrimaryClick(deferred, hasVideo: true, Host);
        Assert.True(PlacementCore.IsActive(clicked));
        Assert.True(PlacementPost.ShouldPost(in deferred, in clicked, Host, out bool wanted));
        Assert.True(wanted);
    }

    [Fact]
    public void A_placement_move_is_not_news_to_the_reducer()
    {
        var docked = DockedWith(Host);
        var floating = PlacementCore.OpenAt(docked, SurfacePlacement.Floating);
        Assert.False(PlacementPost.ShouldPost(in docked, in floating, Host, out _));
    }

    [Fact]
    public void A_host_that_can_show_nothing_wants_no_video()
        => Assert.False(PlacementPost.WantsVideo(DockedWith(Host), PlacementSet.None));

    [Fact]
    public void G150_a_boundary_is_two_known_different_rows()
    {
        EntityId a = EntityId.ForGid(EntityKind.Track, (UInt128)1), b = EntityId.ForGid(EntityKind.Track, (UInt128)2);
        Assert.True(BoundaryFold.IsBoundary(a, b));
        Assert.False(BoundaryFold.IsBoundary(a, a));
        Assert.False(BoundaryFold.IsBoundary(default, b));    // the first row after launch
        Assert.False(BoundaryFold.IsBoundary(a, default));    // a mid-push glitch
    }

    [Fact]
    public void G150_a_video_that_lands_mid_track_lights_the_badge_and_never_swaps_on_its_own()
    {
        var deferred = DockedWith(PlacementSet.None);
        Assert.False(BoundaryFold.Commits(in deferred, hasVideo: true, Host, boundary: false, out _));
        Assert.True(BoundaryFold.Commits(in deferred, hasVideo: true, Host, boundary: true, out var atBoundary));
        Assert.True(PlacementCore.IsActive(atBoundary));
    }

    [Fact]
    public void G150_a_downgrade_always_commits_and_a_no_op_commits_nothing()
    {
        var watching = DockedWith(Host);
        Assert.True(BoundaryFold.Commits(in watching, hasVideo: false, Host, boundary: false, out var down));
        Assert.False(PlacementCore.IsActive(down));
        Assert.False(BoundaryFold.Commits(in watching, hasVideo: true, Host, boundary: true, out _));
    }

    [Fact]
    public void G058_the_newest_size_fact_sizes_the_surface()
    {
        Assert.Equal(NaturalSource.Decoder, NaturalSeed.Pick(1920, 800, 1280, 720, 640, 480, out int w, out int h));
        Assert.Equal((1920, 800), (w, h));
        Assert.Equal(NaturalSource.Manifest, NaturalSeed.Pick(0, 0, 1280, 720, 640, 480, out w, out h));
        Assert.Equal((1280, 720), (w, h));
        Assert.Equal(NaturalSource.Catalogue, NaturalSeed.Pick(0, 0, 0, 720, 1080, 1920, out w, out h));
        Assert.Equal((1080, 1920), (w, h));                   // a vertical video opens vertical before the resolve
        Assert.Equal(NaturalSource.None, NaturalSeed.Pick(0, 0, 0, 0, 0, 0, out w, out h));
        Assert.Equal("track", NaturalSeed.Name(NaturalSource.Catalogue));
    }
}
