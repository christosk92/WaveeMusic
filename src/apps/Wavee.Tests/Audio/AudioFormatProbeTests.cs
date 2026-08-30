using System;
using Google.Protobuf;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using Xunit;
using M = Wavee.Protocol.Metadata;
using Af = Wavee.Protocol.Audiofiles;

namespace Wavee.Tests.Audio;

public class AudioFormatProbeTests
{
    static byte[] Id(byte seed) => A.Bytes(seed, 20);
    static M.AudioFile File(byte seed, M.AudioFile.Types.Format fmt) =>
        new() { FileId = ByteString.CopyFrom(Id(seed)), Format = fmt };

    [Fact]
    public void DescribePrefix_DetectsClearMp3Shapes()
    {
        Assert.Equal("clear-mp3:id3", AudioFormatProbe.DescribePrefix("ID3"u8));
        Assert.Equal("clear-mp3:frame-sync", AudioFormatProbe.DescribePrefix(new byte[] { 0xFF, 0xFB, 0x90, 0x64 }));
    }

    [Fact]
    public void DescribePrefix_DetectsContainerMagic_AtZeroOrSpotifyHeader()
    {
        var ogg = new byte[SpotifyAesCtr.SpotifyHeaderSize + 8];
        "OggS"u8.CopyTo(ogg.AsSpan(SpotifyAesCtr.SpotifyHeaderSize));
        Assert.Equal("clear-ogg:offset0xa7", AudioFormatProbe.DescribePrefix(ogg));

        var flac = new byte[12];
        "fLaC"u8.CopyTo(flac);
        Assert.Equal("clear-flac:offset0", AudioFormatProbe.DescribePrefix(flac));
    }

    [Fact]
    public void DescribePrefix_UnknownRandomBytes_AreNotLabeledPlayable()
    {
        var random = Convert.FromHexString("102030405060708090a0b0c0d0e0f001");
        Assert.Equal("encrypted-or-unknown", AudioFormatProbe.DescribePrefix(random));
    }

    [Fact]
    public void CollectAudioCandidates_IncludesTrackAlternativesAndAudioFiles_Deduped()
    {
        var track = new M.Track { Gid = ByteString.CopyFrom(A.Gid16()) };
        track.File.Add(File(1, M.AudioFile.Types.Format.OggVorbis320));
        track.File.Add(File(2, M.AudioFile.Types.Format.Mp3160));
        var alt = new M.Track();
        alt.File.Add(File(3, M.AudioFile.Types.Format.Mp396));
        track.Alternative.Add(alt);

        var af = new Af.AudioFilesExtensionResponse();
        af.Files.Add(new Af.ExtendedAudioFile { File = File(4, M.AudioFile.Types.Format.FlacFlac) });
        af.Files.Add(new Af.ExtendedAudioFile { File = File(2, M.AudioFile.Types.Format.Mp3160) }); // duplicate

        var c = AudioFormatProbe.CollectAudioCandidates(track, af);

        Assert.Equal(4, c.Count);
        Assert.Contains(c, x => x.Format == M.AudioFile.Types.Format.Mp3160);
        Assert.Contains(c, x => x.Format == M.AudioFile.Types.Format.Mp396);
        Assert.Contains(c, x => x.Format == M.AudioFile.Types.Format.FlacFlac);
    }

    [Fact]
    public void DescribeVideoManifest_SummarizesWidevinePlayReadyAndProfiles()
    {
        const string json = """
        {
          "contents": [{
            "encryption_infos": [
              { "key_system": "widevine", "license_server_endpoint": "https://lic/wv" },
              { "key_system": "playready", "license_server_endpoint": "https://lic/pr" }
            ],
            "profiles": [
              { "id": 101, "file_type": "webm", "video_codec": "vp9", "encryption_index": 0 },
              { "id": 202, "file_type": "mp4", "audio_codec": "mp4a.40.2", "encryption_index": 1 }
            ]
          }]
        }
        """;

        var desc = AudioFormatProbe.DescribeVideoManifest(json);

        Assert.Contains("0:widevine:license=<set>", desc);
        Assert.Contains("1:playready:license=<set>", desc);
        Assert.Contains("101:webm:vp9:enc=0", desc);
        Assert.Contains("202:mp4:mp4a.40.2:enc=1", desc);
    }
}
