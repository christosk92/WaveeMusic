// ── Wavee.Tests/StageSurfaceTests.cs — the immersive surface's stage-B rules ───────────────────────────────────────
//
// `Stage.Band` (W14's height arithmetic), `Stage.Transport` (StageIdentity.cs:89-90's two predicates), and the backdrop
// drift: `StageDriftClockTests` ports 0.2.9's four facts verbatim onto `Stage.DriftClock`, and `StageDriftTests` pins the
// pose and the ticker's gate. All pure.

using Wavee;
using Xunit;

using B = Wavee.Stage.Band;
using D = Wavee.Stage.Drift;
using L = Wavee.Stage.Layout;
using T = Wavee.Stage.Transport;

namespace Wavee.Tests;

public class StageBandTests
{
    [Theory]
    [InlineData(900f, 692f)]
    [InlineData(700f, 492f)]
    [InlineData(680f, 472f)]
    [InlineData(640f, 432f)]
    [InlineData(614f, 406f)]
    public void Column_height_is_the_W14_arithmetic(float viewportH, float availH)
        => Assert.Equal(availH, B.ColumnAvailH(viewportH));

    [Fact]
    public void The_demotion_point_is_where_the_ladder_says()
    {
        Assert.True(L.Seed(1440f, B.ColumnAvailH(614f)).Wide);
        Assert.False(L.Seed(1440f, B.ColumnAvailH(613f)).Wide);
    }

    [Fact]
    public void The_volume_track_is_264() => Assert.Equal(264f, B.VolumeTrackW);

    [Fact]
    public void The_top_band_follows_the_shape()
    {
        Assert.Equal(B.TopBandH, B.TopBandFor(true));
        Assert.Equal(B.CompactTopBandH, B.TopBandFor(false));
    }

    [Fact]
    public void A_degenerate_viewport_never_goes_negative()
    {
        Assert.True(B.BodyH(0f) > 0f);
        Assert.Equal(0f, B.ColumnAvailH(0f));
    }
}

public class StageTransportTests
{
    [Fact]
    public void An_error_kills_the_transport_but_not_the_play_disc()
    {
        Assert.False(T.CanTransport(hasTrack: true, hasError: true));
        Assert.True(T.PrimaryEnabled(hasTrack: true, loading: false));
    }

    [Fact]
    public void Loading_kills_only_the_play_disc()
    {
        Assert.True(T.CanTransport(hasTrack: true, hasError: false));
        Assert.False(T.PrimaryEnabled(hasTrack: true, loading: true));
    }

    [Fact]
    public void Nothing_playing_disables_both()
    {
        Assert.False(T.CanTransport(false, false));
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

public class StageDriftClockTests
{
    [Fact]
    public void Initial_and_paused_stage_do_not_advance()
    {
        var clock = new Stage.DriftClock();
        Assert.Equal(0d, clock.Sample(100));
        clock.SetRunning(true, 100);
        Assert.Equal(0d, clock.Sample(101));
        Assert.Equal(2d, clock.Sample(103));
        clock.SetRunning(false, 103.1);
        Assert.False(clock.Running);
        Assert.Equal(2d, clock.Sample(1000));
    }

    [Fact]
    public void Resume_continues_the_phase_without_spending_paused_time()
    {
        var clock = new Stage.DriftClock();
        clock.SetRunning(true, 10);
        clock.Sample(10);
        Assert.Equal(7d, clock.Sample(17));
        clock.SetRunning(false, 17.02);
        clock.SetRunning(true, 10_000);
        Assert.Equal(7d, clock.Sample(10_000));
        Assert.Equal(7.5d, clock.Sample(10_000.5));
        clock.SetRunning(true, 10_001);
        Assert.Equal(8d, clock.Sample(10_001));
    }

    [Fact]
    public void Repeated_pause_resume_cycles_preserve_only_playing_time()
    {
        var clock = new Stage.DriftClock();
        clock.SetRunning(true, 0);
        clock.Sample(0);
        for (int cycle = 0; cycle < 100; cycle++)
        {
            double start = cycle * 100d;
            clock.SetRunning(true, start);
            Assert.Equal(cycle + 1d, clock.Sample(start + 1));
            clock.SetRunning(false, start + 1);
            Assert.Equal(cycle + 1d, clock.Sample(start + 99));
        }
    }

    [Fact]
    public void Reset_restores_identity_and_a_fresh_first_tick()
    {
        var clock = new Stage.DriftClock();
        clock.SetRunning(true, 1);
        clock.Sample(1);
        clock.Sample(15);
        clock.Reset();
        Assert.False(clock.Running);
        Assert.Equal(0d, clock.Sample(100));
        clock.SetRunning(true, 100);
        Assert.Equal(0d, clock.Sample(101));
        Assert.Equal(1d, clock.Sample(102));
    }
}

public class StageDriftTests
{
    [Fact]
    public void Time_zero_is_identity()
    {
        var p = D.At(0d, 1440f, 780f);
        Assert.Equal(0f, p.Dx, 4);
        Assert.Equal(0f, p.Dy, 4);
        Assert.Equal(1f, p.Scale, 5);
    }

    [Fact]
    public void The_translation_never_exceeds_its_amplitude_and_the_scale_its_wobble()
    {
        const float w = 1440f, h = 780f;
        for (double t = 0d; t < 600d; t += 0.37d)
        {
            var p = D.At(t, w, h);
            Assert.InRange(p.Dx, -D.AmpFrac * w / D.Overscale - 0.01f, D.AmpFrac * w / D.Overscale + 0.01f);
            Assert.InRange(p.Dy, -D.AmpFrac * h / D.Overscale - 0.01f, D.AmpFrac * h / D.Overscale + 0.01f);
            Assert.InRange(p.Scale, 1f - D.ScaleAmp - 1e-4f, 1f + D.ScaleAmp + 1e-4f);
        }
    }

    [Fact]
    public void The_overscale_margin_covers_the_whole_wobble()
    {
        // 15 % of margin each side against ≤ 4 % translate + ≤ 2 % scale: an edge can never show.
        float margin = (D.Overscale - 1f) * 0.5f;
        Assert.True(margin > D.AmpFrac + D.ScaleAmp);
    }

    [Theory]
    [InlineData(true, false, true, true, true)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, false, true, false, false)]
    public void The_ticker_runs_only_when_everything_allows_it(bool setting, bool reduced, bool playing, bool art, bool runs)
        => Assert.Equal(runs, D.Runs(setting, reduced, playing, art));

    [Fact]
    public void An_invisible_delta_is_not_worth_a_write()
    {
        var a = new D.Pose(10f, 10f, 1f);
        Assert.False(D.Worth(a, a with { Dx = 10.1f }));
        Assert.True(D.Worth(a, a with { Dx = 10.2f }));
        Assert.True(D.Worth(a, a with { Scale = 1.001f }));
    }
}
