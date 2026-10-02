using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

public sealed class PodcastPlayerBarTests
{
    // ── the ±10 s step buttons: a direct seek, no accumulator (D4 — `PlayerSeekAccumulator` is gone) ───────────────────

    static Playback.LiveWindow Dvr(long start, long end, long position)
        => new(IsLive: true, SeekableStartMs: start, SeekableEndMs: end, LiveEdgeMs: end, PositionMs: position, IsAtLiveEdge: false);

    [Fact]
    public void A_step_button_seeks_straight_from_the_reported_position()
    {
        Assert.Equal(20_000L, Shell.SeekRail.StepTargetMs(10_000, 10_000, 200_000, Playback.LiveWindow.None));
        Assert.Equal(10_000L, Shell.SeekRail.StepTargetMs(20_000, -10_000, 200_000, Playback.LiveWindow.None));
    }

    [Fact]
    public void A_step_button_clamps_through_the_reducers_own_seek_clamp()
    {
        // Past the end it lands at the tail guard (what `DoSeek` will do with it), never at the duration itself…
        Assert.Equal(25_000L - Playback.SeekTarget.TailGuardMs, Shell.SeekRail.StepTargetMs(20_000, 10_000, 25_000, Playback.LiveWindow.None));
        // …and never before the start.
        Assert.Equal(0L, Shell.SeekRail.StepTargetMs(4_000, -10_000, 25_000, Playback.LiveWindow.None));
        // The painted drop point and the reducer's landing are one number: running it through the clamp again changes nothing.
        long target = Shell.SeekRail.StepTargetMs(20_000, 10_000, 25_000, Playback.LiveWindow.None);
        Assert.Equal(target, Playback.SeekTarget.Clamp((int)target, 25_000));
    }

    [Fact]
    public void A_dvr_step_never_leaves_the_seekable_window()
    {
        var w = Dvr(50_000, 100_000, 52_000);
        Assert.Equal(50_000L, Shell.SeekRail.StepTargetMs(52_000, -10_000, 0, w));
        Assert.Equal(100_000L, Shell.SeekRail.StepTargetMs(95_000, 10_000, 0, w));
        Assert.Equal(62_000L, Shell.SeekRail.StepTargetMs(52_000, 10_000, 0, w));
    }

    // ── the keyboard ladder works in [0, span]: a track's duration, or a DVR window's width offset by its start ────────

    [Fact]
    public void The_ladder_space_of_a_track_is_its_duration()
    {
        var (baseMs, span, fromMs) = Shell.SeekRail.KeySpace(Playback.LiveWindow.None, 200_000, 42_000);
        Assert.Equal(0L, baseMs);
        Assert.Equal(200_000, span);
        Assert.Equal(42_000, fromMs);
    }

    [Fact]
    public void The_ladder_space_of_a_dvr_window_is_its_width_offset_by_its_start()
    {
        var w = Dvr(50_000, 100_000, 52_000);
        var (baseMs, span, fromMs) = Shell.SeekRail.KeySpace(w, 0, 52_000);
        Assert.Equal(50_000L, baseMs);
        Assert.Equal(50_000, span);
        Assert.Equal(2_000, fromMs);
        // a playhead that has slid out of the window (the window moved on while it sat there) is pinned to its edge
        Assert.Equal(0, Shell.SeekRail.KeySpace(w, 0, 10_000).FromMs);
        Assert.Equal(50_000, Shell.SeekRail.KeySpace(w, 0, 900_000).FromMs);
    }

    [Fact]
    public void A_live_source_without_a_window_has_no_ladder_space_so_no_key_can_commit_a_seek_to_zero()
    {
        var noWindow = Dvr(0, 10_000, 5_000);                           // under the 30 s minimum: a breathing line, not a rail
        Assert.False(noWindow.HasWindow);
        var (_, span, _) = Shell.SeekRail.KeySpace(noWindow, 0, 5_000);
        Assert.Equal(0, span);                                           // the bar's key handler refuses a span of 0
    }

    [Fact]
    public void A_dvr_ladder_target_commits_inside_the_window()
    {
        var w = Dvr(50_000, 100_000, 52_000);
        var (baseMs, span, fromMs) = Shell.SeekRail.KeySpace(w, 0, 52_000);
        var ladder = new KeyboardScrubLadder();

        int back = ladder.Step(-1, false, fromMs, span, 1_000);   // 5 s back from 2 s into the window: the window's start
        Assert.Equal(0, back);
        Assert.Equal(50_000L, baseMs + back);

        ladder = new KeyboardScrubLadder();
        (baseMs, span, fromMs) = Shell.SeekRail.KeySpace(w, 0, 95_000);
        int forward = ladder.Step(1, false, fromMs, span, 5_000);  // 5 s forward from 45 s into a 50 s window: its end
        Assert.Equal(50_000, forward);
        Assert.Equal(100_000L, baseMs + forward);
    }

    // ── the key map ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Keys.Left, Shell.PlayerKeyIntent.SeekBack)]
    [InlineData(Keys.Right, Shell.PlayerKeyIntent.SeekForward)]
    [InlineData(Keys.Up, Shell.PlayerKeyIntent.VolumeUp)]
    [InlineData(Keys.Down, Shell.PlayerKeyIntent.VolumeDown)]
    [InlineData(Keys.Space, Shell.PlayerKeyIntent.Toggle)]
    public void Keyboard_intents_only_belong_to_focused_container(int key, Shell.PlayerKeyIntent expected)
    {
        Assert.Equal(expected, Shell.PlayerKey(key, true, false, false, false));
        Assert.Equal(Shell.PlayerKeyIntent.None, Shell.PlayerKey(key, false, false, false, false));
        Assert.Equal(Shell.PlayerKeyIntent.None, Shell.PlayerKey(key, true, true, false, false));
        Assert.Equal(Shell.PlayerKeyIntent.None, Shell.PlayerKey(key, true, false, true, false));
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(440f)]
    [InlineData(760f)]
    [InlineData(900f)]
    [InlineData(1100f)]
    [InlineData(1240f)]
    public void Podcast_transport_speed_and_secondary_commands_fit_every_pressure_tier(float width)
    {
        var layout = Shell.PodcastBarLayout(Shell.PlayerBarLayout.Initial(width));
        float transport = layout.PrimaryBox + 2 * layout.ButtonBox + Shell.EpisodeSpeedButtonWidth
            + (layout.ShowPrevNext ? 2 * layout.ButtonBox : 0);
        float occupied = 2 * layout.RowPad + layout.LeftW + 2 * layout.RowGap + transport
            + layout.ClusterGap + Shell.PlayerBarRules.RightWidth(layout, false);
        Assert.True(width - occupied >= 20, $"width={width} occupied={occupied}");
        Assert.False(layout.ShowShuffleRepeat);
        Assert.False(layout.ShowVolumeSlider);
        Span<Shell.OverflowCommand> commands = stackalloc Shell.OverflowCommand[Shell.PlayerBarRules.MaxOverflow];
        int count = Shell.PlayerBarRules.Overflow(layout, true, true, false, commands);
        Assert.Contains(Shell.OverflowCommand.Shuffle, commands[..count].ToArray());
        Assert.Contains(Shell.OverflowCommand.Repeat, commands[..count].ToArray());
        // The now-playing rail's toggle rides every tier's overflow (the Full tier's Expand slot opens the stage instead).
        Assert.Contains(Shell.OverflowCommand.NowPlaying, commands[..count].ToArray());
        // The stage's door: the Expand slot where the row has it (Full), the "⋯" menu's Full screen row everywhere else.
        Assert.Equal(!layout.ShowExpand, Array.IndexOf(commands[..count].ToArray(), Shell.OverflowCommand.FullScreen) >= 0);
        if (!layout.ShowPrevNext)
        {
            Assert.Contains(Shell.OverflowCommand.Previous, commands[..count].ToArray());
            Assert.Contains(Shell.OverflowCommand.Next, commands[..count].ToArray());
        }
    }
}
