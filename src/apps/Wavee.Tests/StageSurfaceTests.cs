// ── Wavee.Tests/StageSurfaceTests.cs — the stage transport's three predicates ──────────────────────────────────────
//
// `Stage.Transport` (StageIdentity.cs:89-90's predicates, unchanged by the fullscreen flagship). The band arithmetic, the
// backdrop drift's clock and pose and the old allocator's facts were deleted with their types — `Stage.Band`,
// `Stage.DriftClock`, `Stage.Drift` — and the new allocator's facts live in `StageLayoutTests.cs`. All pure.

using Wavee;
using Xunit;

using T = Wavee.Stage.Transport;

namespace Wavee.Tests;

public class StageTransportTests
{
    [Fact]
    public void An_error_leaves_the_play_disc_live()
    {
        Assert.True(T.PrimaryEnabled(hasTrack: true, loading: false));
    }

    [Fact]
    public void Loading_kills_only_the_play_disc()
    {
        Assert.False(T.PrimaryEnabled(hasTrack: true, loading: true));
    }

    [Fact]
    public void Nothing_playing_disables_the_play_disc()
    {
        Assert.False(T.PrimaryEnabled(false, false));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void The_quality_badge_is_silent_without_a_local_format(bool hasFormat, bool remote, bool shows)
        => Assert.Equal(shows, T.ShowsQualityBadge(hasFormat, remote));

    [Theory]
    [InlineData("Song", "spotify:track:1", true)]
    [InlineData("spotify:track:1", "spotify:track:1", false)]
    [InlineData("", "spotify:track:1", false)]
    [InlineData(null, "spotify:track:1", false)]
    public void The_title_is_never_a_raw_uri(string? title, string uri, bool uses)
        => Assert.Equal(uses, T.UsesTitle(title, uri));
}
