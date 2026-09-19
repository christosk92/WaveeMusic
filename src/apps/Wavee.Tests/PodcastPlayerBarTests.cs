using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

public sealed class PodcastPlayerBarTests
{
    static EntityId Episode(string suffix = "4rOoJ6Egrf8K2IrywzwOMk")
    {
        Assert.True(EntityId.TryParseGid(("spotify:episode:" + suffix).AsSpan(), out var id));
        return id;
    }

    [Fact]
    public void Repeated_steps_accumulate_while_remote_position_is_still_the_old_sample()
    {
        var intent = new Shell.PlayerSeekAccumulator();
        var id = Episode();
        Assert.Equal(20_000, intent.Step(id, 1, Playback.Owner.Foreign, 2, 10_000, 200_000, 100, 10_000));
        Assert.Equal(30_000, intent.Step(id, 1, Playback.Owner.Foreign, 2, 10_000, 200_000, 120, 10_000));
        Assert.Equal(20_000, intent.Step(id, 1, Playback.Owner.Foreign, 2, 10_000, 200_000, 140, -10_000));
        Assert.Equal(30_400, intent.Step(id, 1, Playback.Owner.Foreign, 2, 20_400, 200_000, 180, 10_000));
    }

    [Fact]
    public void Steps_clamp_and_reset_on_account_device_and_expired_confirmation()
    {
        var intent = new Shell.PlayerSeekAccumulator();
        var id = Episode();
        Assert.Equal(25_000, intent.Step(id, 1, Playback.Owner.Us, 0, 20_000, 25_000, 100, 10_000));
        Assert.Equal(0, intent.Step(id, 2, Playback.Owner.Us, 0, 1_000, 25_000, 120, -10_000));
        Assert.Equal(15_000, intent.Step(id, 2, Playback.Owner.Foreign, 3, 5_000, 25_000, 140, 10_000));
        Assert.Equal(11_000, intent.Step(id, 2, Playback.Owner.Foreign, 3, 1_000, 25_000, 2141, 10_000));
        intent.Reset();
        Assert.Equal(12_000, intent.Step(id, 2, Playback.Owner.Foreign, 3, 2_000, 25_000, 2150, 10_000));
    }

    [Fact]
    public void Dvr_steps_never_leave_the_seekable_window()
    {
        var intent = new Shell.PlayerSeekAccumulator();
        Assert.Equal(50_000, intent.Step(Episode(), 1, Playback.Owner.Us, 0, 52_000, 100_000, 100, -10_000, 50_000));
        Assert.Equal(100_000, intent.Step(Episode(), 1, Playback.Owner.Us, 0, 95_000, 100_000, 2500, 10_000, 50_000));
    }

    [Fact]
    public void New_episode_does_not_inherit_pending_seek()
    {
        var intent = new Shell.PlayerSeekAccumulator();
        intent.Step(Episode(), 1, Playback.Owner.Us, 0, 80_000, 200_000, 100, 10_000);
        Assert.Equal(10_000, intent.Step(Episode("0Q86acNRm6V9GYx55SXKwf"), 1,
            Playback.Owner.Us, 0, 0, 200_000, 120, 10_000));
    }

    [Theory]
    [InlineData(Keys.Left, Shell.PlayerKeyIntent.SeekBack)]
    [InlineData(Keys.Right, Shell.PlayerKeyIntent.SeekForward)]
    [InlineData(Keys.Up, Shell.PlayerKeyIntent.VolumeUp)]
    [InlineData(Keys.Down, Shell.PlayerKeyIntent.VolumeDown)]
    [InlineData(Keys.Space, Shell.PlayerKeyIntent.Toggle)]
    public void Keyboard_intents_only_belong_to_focused_container(int key, Shell.PlayerKeyIntent expected)
    {
        Assert.Equal(expected, Shell.PlayerKey(key, true, false, false));
        Assert.Equal(Shell.PlayerKeyIntent.None, Shell.PlayerKey(key, false, false, false));
        Assert.Equal(Shell.PlayerKeyIntent.None, Shell.PlayerKey(key, true, true, false));
        Assert.Equal(Shell.PlayerKeyIntent.None, Shell.PlayerKey(key, true, false, true));
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
        if (!layout.ShowPrevNext)
        {
            Assert.Contains(Shell.OverflowCommand.Previous, commands[..count].ToArray());
            Assert.Contains(Shell.OverflowCommand.Next, commands[..count].ToArray());
        }
    }
}
