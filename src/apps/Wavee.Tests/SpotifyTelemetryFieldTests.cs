// ── Wavee.Tests/SpotifyTelemetryFieldTests.cs — the RawCoreStream field set and the head reply decision ─────────────
//
// Field numbers are the official client's own descriptor (1.2.96.518): media_id (4) is the track gid as lowercase hex
// text, core_bundle is 38, and the fields this player has no honest value for (audio_id 63, connect_controller_device_id_v2
// 44, controlling_device_* 78-80) stay unset rather than carry a stand-in. player_session_id (59; segment 64, last
// segment only) is the put-state session the stream started in, and jam_session_id (64) is an always-written "".

using System;
using Google.Protobuf;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SpotifyTelemetryFieldTests
{
    static Spotify.Telemetry.Registration Facts(byte[]? mediaId) => new()
    {
        Ids = new Spotify.Telemetry.PlaybackIds(
            new byte[16], new byte[16], new string('a', 32), "page", "interaction", "0123456789abcdef0123456789abcdef"),
        ContentUri = "spotify:track:abc",
        PlayContext = "spotify:album:xyz",
        Provider = "context",
        ReasonStart = "clickrow",
        SourceStart = "album",
        AudioFormatName = "ogg_vorbis_160",
        MediaId = mediaId,
    };

    [Fact]
    public void MediaIdIsTheGidAsLowercaseHexText()
    {
        byte[] gid = new byte[16];
        for (int i = 0; i < gid.Length; i++) gid[i] = (byte)(0xA0 + i);
        var raw = Spotify.Telemetry.BuildRawCoreStream(Facts(gid), "endplay", true);
        Assert.Equal("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf", raw.MediaId.ToStringUtf8());
        Assert.Equal(32, raw.MediaId.Length);
    }

    [Fact]
    public void MediaIdIsEmptyWithoutAGid()
        => Assert.True(Spotify.Telemetry.BuildRawCoreStream(Facts(null), "endplay", true).MediaId.IsEmpty);

    [Fact]
    public void AudioIdDoesNotCarryTheCommandId()
        => Assert.True(Spotify.Telemetry.BuildRawCoreStream(Facts(new byte[16]), "endplay", true).AudioId.IsEmpty);

    [Fact]
    public void CoreBundleIsFullAndTheV2ControllerIdIsUnset()
    {
        var raw = Spotify.Telemetry.BuildRawCoreStream(Facts(new byte[16]), "endplay", true);
        Assert.Equal("full", raw.CoreBundle);
        Assert.Equal("", raw.ConnectControllerDeviceIdV2);
    }

    const string Session = "1A2b3C4d5E6f7G8h9I0jKl";

    [Fact]
    public void PlayerSessionIdIsTheSessionTheStreamStartedIn()
    {
        var facts = Facts(new byte[16]);
        facts.PlayerSessionId = Session;
        var raw = Spotify.Telemetry.BuildRawCoreStream(facts, "endplay", true);
        Assert.Equal(Session, raw.PlayerSessionId);
        Assert.True(raw.HasPlayerSessionId);
    }

    [Fact]
    public void AVideoStreamSendsAnEmptyPlayerSessionIdButStillWritesIt()
    {
        var facts = Facts(new byte[16]);
        facts.PlayerSessionId = Playback.PlayerSession.TelemetryText(UInt128.One, video: true);
        var raw = Spotify.Telemetry.BuildRawCoreStream(facts, "endplay", true);
        Assert.Equal("", raw.PlayerSessionId);
        Assert.True(raw.HasPlayerSessionId);
    }

    [Fact]
    public void JamSessionIdIsSentEmpty()
    {
        var raw = Spotify.Telemetry.BuildRawCoreStream(Facts(new byte[16]), "endplay", true);
        Assert.Equal("", raw.JamSessionId);
        Assert.True(raw.HasJamSessionId);
    }

    [Fact]
    public void OnlyTheLastSegmentCarriesThePlayerSessionId()
    {
        var facts = Facts(new byte[16]);
        facts.PlayerSessionId = Session;
        Assert.Equal("", Spotify.Telemetry.BuildSegment(facts, 1_000, isPause: true, isLast: false, "pause", 900, 5).PlayerSessionId);
        Assert.Equal(Session, Spotify.Telemetry.BuildSegment(facts, 2_000, isPause: false, isLast: true, "endplay", 900, 6).PlayerSessionId);
    }

    [Fact]
    public void ATelemetrySessionIsTheSamePublishedTextOrEmpty()
    {
        Assert.True(Base62.TryDecode(Session.AsSpan(), out UInt128 value));
        Assert.Equal(Session, Playback.PlayerSession.TelemetryText(value, video: false));
        Assert.Equal("", Playback.PlayerSession.TelemetryText(value, video: true));
        Assert.Equal("", Playback.PlayerSession.TelemetryText(UInt128.Zero, video: false));
    }

    [Fact]
    public void ControllingDeviceFieldsAreOmittedForASelfControlledPlay()
    {
        var raw = Spotify.Telemetry.BuildRawCoreStream(Facts(new byte[16]), "endplay", true);
        Assert.Equal("", raw.ControllingDeviceBrand);
        Assert.Equal("", raw.ControllingDeviceModel);
        Assert.Equal("", raw.ControllingDeviceType);
    }

    // ── the head reply ──────────────────────────────────────────────────────────────────────────────────────────

    const int Max = Spotify.Audio.HeadMaxBytes;

    [Fact]
    public void A206OfTheRequestedRangeReadsTheWholeHead()
        => Assert.Equal(Max, Spotify.Audio.HeadResponse.Decide(206, 629_313, Max));

    [Fact]
    public void A200FromAHostThatIgnoredTheRangeIsCappedAtTheHead()
        => Assert.Equal(Max, Spotify.Audio.HeadResponse.Decide(200, null, 2_100_000));

    [Fact]
    public void AShort206ReadsOnlyWhatTheFileHolds()
        => Assert.Equal(5_000, Spotify.Audio.HeadResponse.Decide(206, 5_000, 5_000));

    [Fact]
    public void ALengthlessReplyFallsBackToTheContentRangeTotal()
        => Assert.Equal(3_000, Spotify.Audio.HeadResponse.Decide(206, 3_000, null));

    [Theory]
    [InlineData(416)]
    [InlineData(404)]
    [InlineData(500)]
    public void ARefusalIsAnEmptyHead(int status)
        => Assert.Equal(0, Spotify.Audio.HeadResponse.Decide(status, 0, 0));
}
