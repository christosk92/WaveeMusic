using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The pure rule behind "lyrics stop following while a video plays". Tiny by design — the VALUE of pinning it is that
/// the reason is recorded next to the behaviour: a music video is a different edit of the song, the lyric line timings
/// belong to the AUDIO edit, so following them against the video's clock highlights the wrong line.
/// </summary>
public class LyricsSyncGateTests
{
    [Fact]
    public void VideoActive_SuppressesSync()
        => Assert.True(LyricsSyncGate.SyncSuppressed(videoActive: true));

    [Fact]
    public void AudioOnly_KeepsSync()
        => Assert.False(LyricsSyncGate.SyncSuppressed(videoActive: false));

    /// <summary>Suppression tracks the CURRENT media both ways: switching back to audio must restore sync, not leave the
    /// panel permanently unfollowed. (The surfaces get this for free by re-deriving from the placement signal, but the
    /// rule itself must be stateless for that to hold.)</summary>
    [Fact]
    public void Suppression_IsStateless_AndReversible()
    {
        Assert.False(LyricsSyncGate.SyncSuppressed(false));
        Assert.True(LyricsSyncGate.SyncSuppressed(true));
        Assert.False(LyricsSyncGate.SyncSuppressed(false));
    }
}
