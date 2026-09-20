using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// PlayIntentGate is the pure half of the buffering-bar-on-a-paused-restored-track fix: FluentMediaAudioHost's
// LoadFastStart/SupplyBodyAsync used to announce Prebuffering/Buffering unconditionally while attaching a clear head /
// encrypted body, even for a launch-recovery restore that loads the current track PAUSED (Play() never called) purely
// to show it sitting at its saved position. The host itself opens a real WASAPI device and cannot be instantiated
// headlessly, so this one-fact decision is pinned here instead — trivial as it is, it is the ONE place the three call
// sites' behavior is decided, rather than three copies of the same inline `if`.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class PlayIntentGateTests
{
    [Fact]
    public void ShouldAnnounceBuffering_NoPlayIntent_IsFalse()
        => Assert.False(PlayIntentGate.ShouldAnnounceBuffering(playIntent: false));

    [Fact]
    public void ShouldAnnounceBuffering_WithPlayIntent_IsTrue()
        => Assert.True(PlayIntentGate.ShouldAnnounceBuffering(playIntent: true));
}
