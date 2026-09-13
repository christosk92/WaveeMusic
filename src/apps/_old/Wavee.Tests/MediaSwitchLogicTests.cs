using Xunit;

namespace Wavee.Tests;

public class MediaSwitchLogicTests
{
    // ── KindOf ──────────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void KindOf_Video_WhenVideoFlagSet()
        => Assert.Equal(PlayableKind.Video, MediaSwitchLogic.KindOf(isVideoTrack: true, isLocalFile: false));

    [Fact]
    public void KindOf_LocalFile_WhenOnlyLocal()
        => Assert.Equal(PlayableKind.LocalFile, MediaSwitchLogic.KindOf(isVideoTrack: false, isLocalFile: true));

    [Fact]
    public void KindOf_Audio_WhenNeither()
        => Assert.Equal(PlayableKind.Audio, MediaSwitchLogic.KindOf(isVideoTrack: false, isLocalFile: false));

    [Fact]
    public void KindOf_Video_WinsOverLocal_WhenBothSet()
        => Assert.Equal(PlayableKind.Video, MediaSwitchLogic.KindOf(isVideoTrack: true, isLocalFile: true));

    // ── Decide ──────────────────────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(PlayableKind.Audio)]
    [InlineData(PlayableKind.Video)]
    [InlineData(PlayableKind.LocalFile)]
    public void Decide_LoadOnCurrent_WhenSameKind(PlayableKind kind)
        => Assert.Equal(MediaSwitchLogic.SwitchAction.LoadOnCurrent, MediaSwitchLogic.Decide(kind, kind));

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Audio, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void Decide_SwapThenLoad_WhenDifferentKind(PlayableKind current, PlayableKind next)
        => Assert.Equal(MediaSwitchLogic.SwitchAction.SwapThenLoad, MediaSwitchLogic.Decide(current, next));

    // ── AllowCrossfade ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AllowCrossfade_True_OnlyForAudioToAudio()
        => Assert.True(MediaSwitchLogic.AllowCrossfade(PlayableKind.Audio, PlayableKind.Audio));

    [Fact]
    public void AllowCrossfade_False_ForVideoToVideo()
        => Assert.False(MediaSwitchLogic.AllowCrossfade(PlayableKind.Video, PlayableKind.Video));

    [Fact]
    public void AllowCrossfade_False_ForLocalToLocal()
        => Assert.False(MediaSwitchLogic.AllowCrossfade(PlayableKind.LocalFile, PlayableKind.LocalFile));

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Audio, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void AllowCrossfade_False_ForEveryCrossKindPair(PlayableKind from, PlayableKind to)
        => Assert.False(MediaSwitchLogic.AllowCrossfade(from, to));

    // ── TrackPlayer ─────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void TrackPlayer_Video_ForVideo()
        => Assert.Equal("video", MediaSwitchLogic.TrackPlayer(PlayableKind.Video));

    [Fact]
    public void TrackPlayer_Audio_ForAudio()
        => Assert.Equal("audio", MediaSwitchLogic.TrackPlayer(PlayableKind.Audio));

    [Fact]
    public void TrackPlayer_Audio_ForLocalFile()
        => Assert.Equal("audio", MediaSwitchLogic.TrackPlayer(PlayableKind.LocalFile));

    // ── ShouldStopOutgoingHost ──────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(PlayableKind.Audio)]
    [InlineData(PlayableKind.Video)]
    [InlineData(PlayableKind.LocalFile)]
    public void ShouldStopOutgoingHost_False_WhenSameKind(PlayableKind kind)
        => Assert.False(MediaSwitchLogic.ShouldStopOutgoingHost(kind, kind));

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Audio, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void ShouldStopOutgoingHost_True_WhenKindChanges(PlayableKind current, PlayableKind next)
        => Assert.True(MediaSwitchLogic.ShouldStopOutgoingHost(current, next));

    // ── HostChanges (the controller's real swap trigger — Audio↔LocalFile share the audio host) ──────────────────────
    [Theory]
    [InlineData(PlayableKind.Audio)]
    [InlineData(PlayableKind.Video)]
    [InlineData(PlayableKind.LocalFile)]
    public void HostChanges_False_WhenSameKind(PlayableKind kind)
        => Assert.False(MediaSwitchLogic.HostChanges(kind, kind));

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Audio)]
    public void HostChanges_False_ForAudioLocalFileBoundary(PlayableKind current, PlayableKind next)
        => Assert.False(MediaSwitchLogic.HostChanges(current, next));   // both play through the audio host — no swap

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void HostChanges_True_OnEveryVideoBoundary(PlayableKind current, PlayableKind next)
        => Assert.True(MediaSwitchLogic.HostChanges(current, next));

    // Every host boundary is also a kind change, so the stop-first rule agrees at a real swap (no two decoders at once).
    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void HostChanges_ImpliesShouldStopOutgoingHost(PlayableKind current, PlayableKind next)
    {
        Assert.True(MediaSwitchLogic.HostChanges(current, next));
        Assert.True(MediaSwitchLogic.ShouldStopOutgoingHost(current, next));
    }

    // ── M0: the whole audio→video→audio sequence, walked through the pure rules ──────────────────────────────────────
    // The controller composes exactly these four rules per boundary. Walking a sequence pins the composition, not just the
    // individual predicates: every host change stops the outgoing host, never crossfades, and reports the right track_player.
    [Fact]
    public void AudioVideoAudioSequence_SwapsTwice_StopsOutgoingBothTimes_AndNeverCrossfades()
    {
        PlayableKind[] sequence = [PlayableKind.Audio, PlayableKind.Video, PlayableKind.Audio];
        var swaps = new List<(PlayableKind From, PlayableKind To)>();
        for (int i = 1; i < sequence.Length; i++)
        {
            var from = sequence[i - 1];
            var to = sequence[i];
            if (!MediaSwitchLogic.HostChanges(from, to)) continue;
            swaps.Add((from, to));
            Assert.Equal(MediaSwitchLogic.SwitchAction.SwapThenLoad, MediaSwitchLogic.Decide(from, to));
            Assert.True(MediaSwitchLogic.ShouldStopOutgoingHost(from, to));   // stop-first at EVERY real host boundary
            Assert.False(MediaSwitchLogic.AllowCrossfade(from, to));          // a kind boundary is always a hard cut
        }
        Assert.Equal(2, swaps.Count);
        Assert.Equal("audio", MediaSwitchLogic.TrackPlayer(sequence[0]));
        Assert.Equal("video", MediaSwitchLogic.TrackPlayer(sequence[1]));
        Assert.Equal("audio", MediaSwitchLogic.TrackPlayer(sequence[2]));
    }

    // A LocalFile leg in the middle must NOT move the host (Audio and LocalFile share it), so an audio→local→audio run keeps
    // the audio fast-start / prepared-next path untouched — the "preserve the audio path exactly" guarantee.
    [Fact]
    public void AudioLocalAudioSequence_NeverSwapsTheHost()
    {
        PlayableKind[] sequence = [PlayableKind.Audio, PlayableKind.LocalFile, PlayableKind.Audio];
        for (int i = 1; i < sequence.Length; i++)
            Assert.False(MediaSwitchLogic.HostChanges(sequence[i - 1], sequence[i]));
    }

    // Idempotence: asking again for the boundary we already took is never a second swap.
    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    public void HostChanges_IsIdempotent_OnceTheBoundaryHasBeenTaken(PlayableKind from, PlayableKind to)
    {
        Assert.True(MediaSwitchLogic.HostChanges(from, to));
        Assert.False(MediaSwitchLogic.HostChanges(to, to));            // already there → no second swap
        Assert.False(MediaSwitchLogic.ShouldStopOutgoingHost(to, to)); // …and nothing to stop
    }
}
