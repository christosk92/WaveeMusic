// ── Wavee.Tests/VideoJoinPhaseTests.cs — the loading picture, decided from the host's events ────────────────────────
//
// `Video.Joining` is what the four video surfaces show while they are not showing video. `Video.UI.cs`'s `JoinWatch`
// folds the host's events — `Playback.Video.Phase`, `.FirstFrame`, `.Player` — plus the resolved placement into the
// one `Video.JoinNow` value, and every surface's video area reads that. Until this existed the phase signals had ZERO
// consumers: the surfaces discriminated on player presence and a blind `UseTimeout`, so a dead licence and a slow one
// drew the identical picture, and a switch that landed in 200 ms still flashed a spinner.
//
// Pure values only: a `SwitchPhase`, three bools and a long. No engine, no signal, no timer — the clock belongs to the
// caller and all it decides here is whether the join budget is spent.

using Wavee;
using Xunit;

using static Wavee.Video;

using Phase = Wavee.Playback.Video.SwitchPhase;

namespace Wavee.Tests;

public class VideoJoinVisualTests
{
    /// <summary>Video is wanted, nothing is bound and nothing has decoded: the manifest / DRM-licence round-trip that
    /// ch 24 §0.8's poster exists to cover.</summary>
    static JoinState Waiting(Phase phase) => new(phase, PlayerPresent: false, Wanted: true, FrameSeen: false);

    /// <summary>A player is bound and this source's first frame is already on screen.</summary>
    static JoinState Onscreen(Phase phase) => new(phase, PlayerPresent: true, Wanted: true, FrameSeen: true);

    static readonly Phase[] EveryLoadingPhase =
        [Phase.Idle, Phase.Resolving, Phase.Licensing, Phase.Buffering, Phase.Attaching, Phase.Presenting, Phase.Playing];

    [Fact]
    public void A_bound_player_is_the_picture_and_the_app_draws_nothing_over_it()
    {
        // ch 24 §9 "must not be simplified": a live stage shows the stage ALONE — the engine element is the one
        // loading affordance, and stacking the app's overlay on it produced two spinners at once.
        foreach (var phase in EveryLoadingPhase)
        {
            Assert.Equal(JoinVisual.Video, Joining.Decide(new JoinState(phase, true, true, false), 0));
            Assert.Equal(JoinVisual.Video, Joining.Decide(new JoinState(phase, true, true, false), 60_000));
        }
    }

    [Fact]
    public void Without_a_player_the_poster_holds_until_the_join_budget_is_spent()
    {
        // The long phase: a DRM licence round-trip is seconds. The artwork is up from the first frame of the join and
        // the ring only joins it once the join has earned it.
        Assert.Equal(JoinVisual.Poster, Joining.Decide(Waiting(Phase.Licensing), 0));
        Assert.Equal(JoinVisual.Poster, Joining.Decide(Waiting(Phase.Licensing), Joining.SpinnerDelayMs - 1));
        Assert.Equal(JoinVisual.Working, Joining.Decide(Waiting(Phase.Licensing), Joining.SpinnerDelayMs));
        Assert.Equal(JoinVisual.Working, Joining.Decide(Waiting(Phase.Licensing), 2_500));
    }

    [Fact]
    public void Every_pre_picture_phase_without_a_player_reads_the_same_budget()
    {
        foreach (var phase in EveryLoadingPhase)
        {
            Assert.Equal(JoinVisual.Poster, Joining.Decide(Waiting(phase), 399));
            Assert.Equal(JoinVisual.Working, Joining.Decide(Waiting(phase), 400));
        }
    }

    [Fact]
    public void The_budget_is_the_hosts_own_no_spinner_window()
    {
        // One number, named once (the video plan §3.4 rule 1 / §6.2 K: "no spinner under 400 ms").
        Assert.Equal(Playback.Video.Budgets.JoiningNoSpinnerMs, Joining.SpinnerDelayMs);
        Assert.Equal(400, Joining.SpinnerDelayMs);
    }

    [Fact]
    public void A_seek_behind_a_picture_that_is_already_up_never_shows_a_spinner()
    {
        // §6.2 K's rule, in its strongest form. A seek re-buffers, so the phase steps back to `Buffering` / `Attaching`
        // with the player still bound and the previous frame still decoded — that is not a join, at ANY elapsed time,
        // so a quick seek cannot flash and a slow one cannot blank the picture either (the previous frame stays).
        foreach (long elapsed in new long[] { 0, 399, 400, 5_000 })
        {
            Assert.Equal(JoinVisual.Video, Joining.Decide(Onscreen(Phase.Buffering), elapsed));
            Assert.Equal(JoinVisual.Video, Joining.Decide(Onscreen(Phase.Attaching), elapsed));
        }
        Assert.False(Joining.IsJoining(Onscreen(Phase.Buffering)));
        Assert.False(Joining.IsJoining(Onscreen(Phase.Attaching)));
    }

    [Fact]
    public void A_failure_during_the_join_is_a_different_picture_from_a_slow_one()
    {
        // The complaint this whole item came from: in 0.2.9 a dead licence and a two-second one both drew the ring.
        var dead = new JoinState(Phase.Failed, PlayerPresent: false, Wanted: true, FrameSeen: false);
        var slow = Waiting(Phase.Licensing);

        Assert.Equal(JoinVisual.Failed, Joining.Decide(dead, 0));
        Assert.Equal(JoinVisual.Failed, Joining.Decide(dead, 399));
        Assert.Equal(JoinVisual.Failed, Joining.Decide(dead, 10_000));

        Assert.NotEqual(Joining.Decide(dead, 0), Joining.Decide(slow, 0));
        Assert.NotEqual(Joining.Decide(dead, 10_000), Joining.Decide(slow, 10_000));

        // …and a failure is never "still coming": it can never earn the ring, at any elapsed time.
        Assert.False(Joining.IsJoining(dead));
    }

    [Fact]
    public void A_failure_with_a_player_bound_still_replaces_the_loading_picture()
    {
        // A licence that dies mid-join leaves the player bound and pumping nothing. "Still coming" would be a lie.
        Assert.Equal(JoinVisual.Failed, Joining.Decide(new JoinState(Phase.Failed, true, true, false), 0));
    }

    [Fact]
    public void A_failure_after_the_first_frame_keeps_the_picture()
    {
        // The other half of "no flashing, no empty black box": blanking a frame the user is already watching is the
        // jarring half of ugly. The demote path (a toast, then the surface turns off) owns that story instead.
        Assert.Equal(JoinVisual.Video, Joining.Decide(Onscreen(Phase.Failed), 0));
        Assert.Equal(JoinVisual.Video, Joining.Decide(Onscreen(Phase.Failed), 10_000));
    }

    [Fact]
    public void Nothing_asked_for_is_never_still_coming()
    {
        // Video off (or demoted away) with no player: the poster, never a ring — otherwise a surface that mounts later
        // would find a spinner already spinning and show it with no grace at all.
        var off = new JoinState(Phase.Idle, PlayerPresent: false, Wanted: false, FrameSeen: false);
        Assert.Equal(JoinVisual.Poster, Joining.Decide(off, 0));
        Assert.Equal(JoinVisual.Poster, Joining.Decide(off, 60_000));
        Assert.False(Joining.IsJoining(off));
    }

    [Fact]
    public void A_mounted_surface_with_no_player_is_a_join_whatever_the_phase_says()
    {
        // The app resolves the manifest BEFORE it calls the host's `Load`, so the phase is still `Idle` for the whole
        // cold round-trip. A surface that is mounted with nothing bound is waiting, by construction.
        foreach (var phase in EveryLoadingPhase) Assert.True(Joining.IsJoining(Waiting(phase)));
    }

    [Fact]
    public void The_first_frame_ends_the_join_even_while_the_phase_lags()
    {
        // `FirstFrame` is the authority (the video plan §3.4 rule 2: the surface drops its poster on this, never on a
        // state guess) — a phase still reporting `Attaching` behind a decoded picture cannot re-open the join.
        Assert.True(Joining.IsJoining(new JoinState(Phase.Attaching, true, true, FrameSeen: false)));
        Assert.False(Joining.IsJoining(new JoinState(Phase.Attaching, true, true, FrameSeen: true)));
    }
}
