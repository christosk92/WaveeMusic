using System.Linq;
using FluentAssertions;
using Wavee.Core.Video;
using Xunit;

namespace Wavee.Tests.Core.Video;

public sealed class SpotifyWebEmeVideoManifestTests
{
    [Fact]
    public void FromJson_ShouldParseContentsShapeAndSelectBestWidevineWebmProfiles()
    {
        var manifest = SpotifyWebEmeVideoManifest.FromJson("""
        {
          "initialization_template": "init/{{profile_id}}.{{file_type}}",
          "segment_template": "seg/{{profile_id}}/{{segment_timestamp}}.{{file_type}}",
          "base_urls": ["https://cdn.example/"],
          "contents": [
            {
              "segment_length": 4,
              "duration": 10000,
              "encryption_infos": [
                { "key_system": "playready", "license_server_endpoint": "/playready" },
                { "key_system": "widevine", "license_server_endpoint": "/widevine" }
              ],
              "profiles": [
                { "id": 10, "file_type": "webm", "video_codec": "vp9", "video_width": 640, "video_height": 360, "max_bitrate": 300000, "encryption_indices": [0] },
                { "id": 11, "file_type": "webm", "video_codec": "vp9", "video_width": 1280, "video_height": 720, "max_bitrate": 1200000, "encryption_indices": [1] },
                { "id": 12, "file_type": "webm", "video_codec": "vp9", "video_width": 1920, "video_height": 1080, "max_bitrate": 2200000, "encryption_indices": [1] },
                { "id": 13, "file_type": "webm", "audio_codec": "opus", "max_bitrate": 96000, "encryption_indices": [1] },
                { "id": 14, "file_type": "webm", "audio_codec": "opus", "max_bitrate": 128000, "encryption_indices": [1] },
                { "id": 15, "file_type": "mp4", "video_codec": "avc1", "video_width": 3840, "video_height": 2160, "max_bitrate": 5000000, "encryption_indices": [1] }
              ]
            }
          ]
        }
        """);

        manifest.VideoProfileId.Should().Be(12);
        manifest.AudioProfileId.Should().Be(14);
        manifest.DurationMs.Should().Be(10000);
        manifest.SegmentLength.Should().Be(4);
        manifest.LicenseServerEndpoint.Should().Be("/widevine");
        manifest.SegmentTimes.Should().Equal(0, 4, 8);
        manifest.Video.ContentType.Should().Be("video/webm; codecs=\"vp9\"");
        manifest.Audio.ContentType.Should().Be("audio/webm; codecs=\"opus\"");
        manifest.VideoTracks.Select(track => track.ProfileId).Should().Equal(12, 11);
        manifest.Video.Label.Should().Be("1080p - 2.2 Mbps");
        manifest.Video.InitUrl.Should().Be("https://cdn.example/init/12.webm");
        manifest.Video.SegmentUrls.Should().Equal(
            "https://cdn.example/seg/12/0.webm",
            "https://cdn.example/seg/12/4.webm",
            "https://cdn.example/seg/12/8.webm");
    }

    [Fact]
    public void FromJson_ShouldParseSourcesShapeAndDurationFromStartEndTimes()
    {
        var manifest = SpotifyWebEmeVideoManifest.FromJson("""
        {
          "sources": [
            {
              "segment_length": 5,
              "start_time_millis": 2000,
              "end_time_millis": 14000,
              "initialization_template": "i/{{profile_id}}.{{file_type}}",
              "segment_template": "s/{{profile_id}}/{{segment_timestamp}}.{{file_type}}",
              "base_urls": ["https://source.example/"],
              "encryption_infos": [
                { "key_system": "widevine", "license_server_endpoint": "/wv" }
              ],
              "profiles": [
                { "id": 21, "file_type": "webm", "video_codec": "vp9", "width": 640, "height": 360, "bandwidth_estimate": 450000, "encryption_index": 0 },
                { "id": 22, "file_type": "webm", "audio_codec": "opus", "bandwidth_estimate": 128000, "encryption_index": 0 }
              ]
            }
          ]
        }
        """);

        manifest.DurationMs.Should().Be(12000);
        manifest.SegmentLength.Should().Be(5);
        manifest.SegmentTimes.Should().Equal(0, 5, 10);
        manifest.Video.InitUrl.Should().Be("https://source.example/i/21.webm");
        manifest.Audio.SegmentUrls[1].Should().Be("https://source.example/s/22/5.webm");
    }

    [Fact]
    public void FromJson_ShouldThrowWhenWidevineWebmVideoIsMissing()
    {
        var act = () => SpotifyWebEmeVideoManifest.FromJson("""
        {
          "initialization_template": "init/{{profile_id}}.{{file_type}}",
          "segment_template": "seg/{{profile_id}}/{{segment_timestamp}}.{{file_type}}",
          "contents": [
            {
              "segment_length": 4,
              "duration": 10000,
              "encryption_infos": [{ "key_system": "widevine" }],
              "profiles": [
                { "id": 1, "file_type": "webm", "audio_codec": "opus", "max_bitrate": 128000, "encryption_index": 0 }
              ]
            }
          ]
        }
        """);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Widevine WebM video profile*");
    }

    [Fact]
    public void DescribeManifestForLog_ShouldIncludeUsefulDiagnostics()
    {
        var diagnostics = SpotifyWebEmeVideoManifest.DescribeManifestForLog("""
        {
          "contents": [
            {
              "segment_length": 4,
              "duration": 10000,
              "encryption_infos": [{ "key_system": "widevine", "license_server_endpoint": "/wv" }],
              "profiles": [
                { "id": 1, "file_type": "webm", "video_codec": "vp9", "video_width": 1280, "video_height": 720, "encryption_index": 0 }
              ]
            }
          ]
        }
        """);

        diagnostics.Should().Contain("durationMs=10000");
        diagnostics.Should().Contain("segmentLength=4");
        diagnostics.Should().Contain("widevine");
        diagnostics.Should().Contain("1:webm:vp9:1280x720:enc=0");
    }
}
