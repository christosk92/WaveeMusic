// ── Wavee.Tests/ShellPlayerBarUiRulesTests.cs — the player bar's surface rules (Shell/Shell.PlayerBar.cs) ────────────
//
// Every rule here was an inline expression inside 0.2.9's `PlayerBar.cs` / `SeekBar.cs`, where nothing pinned it
// (ch 20 §8, G6: "a rule the chapter marks untested gets its test written with the port"). The rules each of these
// exists to stop being "simplified" away:
//
//   THE SCRUB GATE. While the finger is down the drawn fraction ignores the model, or the 1 Hz report yanks the thumb
//   back under it.
//
//   THE OWNER GATES "PLAYING ON". 0.3's active slot also names the device that just LEFT, and the bar must stop saying
//   "Playing on iPhone" the moment ownership says nobody (parity 80).
//
//   THE OVERFLOW ORDER IS THE MENU ORDER, and everything that leaves the row is in it.

using Xunit;

using DeviceKind = Wavee.Spotify.Decode.DeviceKind;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

public class PlayerBarStateFoldTests
{
    [Fact]
    public void An_error_outranks_everything_and_the_primary_becomes_a_retry()
    {
        var f = Shell.PlayerBarRules.Fold(hasCurrent: true, Playback.Fault.Network, Playback.Phase.Loading,
            buffering: true, Playback.RecoveryKind.Network, canSkipPrev: true, canSkipNext: true);
        Assert.Equal(Shell.PlayerState.Error, f.State);
        Assert.Equal(Shell.PrimaryVerb.Retry, f.Primary);
        Assert.True(f.PrimaryEnabled);
    }

    [Fact]
    public void Nothing_playing_and_loading_kill_the_primary()
    {
        var idle = Shell.PlayerBarRules.Fold(false, Playback.Fault.None, Playback.Phase.Idle, false,
            Playback.RecoveryKind.None, true, true);
        Assert.Equal(Shell.PlayerState.NoTrack, idle.State);
        Assert.False(idle.PrimaryEnabled);
        Assert.False(idle.CanTransport);
        Assert.False(idle.PrevEnabled);

        var loading = Shell.PlayerBarRules.Fold(true, Playback.Fault.None, Playback.Phase.Loading, false,
            Playback.RecoveryKind.None, true, true);
        Assert.Equal(Shell.PlayerState.Loading, loading.State);
        Assert.False(loading.PrimaryEnabled);
    }

    [Fact]
    public void Buffering_keeps_the_transport_armed_while_loading()
    {
        var f = Shell.PlayerBarRules.Fold(true, Playback.Fault.None, Playback.Phase.Loading, buffering: true,
            Playback.RecoveryKind.None, true, true);
        Assert.True(f.CanTransport);
        Assert.False(f.PrimaryEnabled);
    }

    [Fact]
    public void Reconnecting_keeps_the_transport_and_the_primary_live()
    {
        var f = Shell.PlayerBarRules.Fold(true, Playback.Fault.None, Playback.Phase.Playing, false,
            Playback.RecoveryKind.Network, true, true);
        Assert.Equal(Shell.PlayerState.Reconnecting, f.State);
        Assert.True(f.CanTransport);
        Assert.Equal(Shell.PrimaryVerb.TogglePlay, f.Primary);
    }

    [Fact]
    public void The_model_s_skip_restrictions_grey_previous_and_next()
    {
        var f = Shell.PlayerBarRules.Fold(true, Playback.Fault.None, Playback.Phase.Playing, false,
            Playback.RecoveryKind.None, canSkipPrev: false, canSkipNext: true);
        Assert.False(f.PrevEnabled);
        Assert.True(f.NextEnabled);
    }

    [Theory]
    [InlineData(Shell.PlayerState.NoTrack, true, Shell.NowPlayingText.NothingPlaying, Shell.NowPlayingInk.Secondary)]
    [InlineData(Shell.PlayerState.Reconnecting, true, Shell.NowPlayingText.Reconnecting, Shell.NowPlayingInk.Secondary)]
    [InlineData(Shell.PlayerState.Error, true, Shell.NowPlayingText.CannotPlay, Shell.NowPlayingInk.Critical)]
    [InlineData(Shell.PlayerState.Loading, true, Shell.NowPlayingText.Title, Shell.NowPlayingInk.Primary)]
    [InlineData(Shell.PlayerState.Loading, false, Shell.NowPlayingText.Loading, Shell.NowPlayingInk.Primary)]
    [InlineData(Shell.PlayerState.Active, false, Shell.NowPlayingText.Loading, Shell.NowPlayingInk.Primary)]
    public void The_title_never_renders_an_unresolved_playable(Shell.PlayerState state, bool titleKnown,
        Shell.NowPlayingText text, Shell.NowPlayingInk ink)
    {
        Assert.Equal(text, Shell.PlayerBarRules.TextOf(state, titleKnown));
        Assert.Equal(ink, Shell.PlayerBarRules.InkOf(state));
    }

    [Fact]
    public void The_artists_line_is_absent_for_idle_and_error_and_present_while_reconnecting()
    {
        Assert.False(Shell.PlayerBarRules.ShowsArtistLine(Shell.PlayerState.NoTrack, true));
        Assert.False(Shell.PlayerBarRules.ShowsArtistLine(Shell.PlayerState.Error, true));
        Assert.True(Shell.PlayerBarRules.ShowsArtistLine(Shell.PlayerState.Reconnecting, true));
        Assert.True(Shell.PlayerBarRules.ShowsArtistLine(Shell.PlayerState.Loading, true));
    }

    [Fact]
    public void The_top_edge_sweeps_for_loading_buffering_and_reconnecting_only()
    {
        Assert.True(Shell.PlayerBarRules.TopEdgeSweeps(Playback.Phase.Loading, false, Playback.RecoveryKind.None));
        Assert.True(Shell.PlayerBarRules.TopEdgeSweeps(Playback.Phase.Playing, true, Playback.RecoveryKind.None));
        Assert.True(Shell.PlayerBarRules.TopEdgeSweeps(Playback.Phase.Paused, false, Playback.RecoveryKind.Network));
        Assert.False(Shell.PlayerBarRules.TopEdgeSweeps(Playback.Phase.Playing, false, Playback.RecoveryKind.None));
    }

    [Fact]
    public void The_bar_keeps_the_transport_for_every_placement_but_fullscreen()
    {
        Assert.True(Shell.PlayerBarRules.OwnsTransport(Video.TransportOwner.GlobalBar));
        Assert.True(Shell.PlayerBarRules.OwnsTransport(Video.TransportOwner.Docked));
        Assert.True(Shell.PlayerBarRules.OwnsTransport(Video.TransportOwner.PopOut));
        Assert.False(Shell.PlayerBarRules.OwnsTransport(Video.TransportOwner.Fullscreen));
    }
}

public class PlayerBarOverflowTests
{
    static Shell.OverflowCommand[] Build(Shell.PlayerBarTier tier, bool owns = true, bool active = true, bool video = false)
    {
        var layout = Shell.PlayerBarLayout.ForTier(tier);
        Span<Shell.OverflowCommand> buffer = stackalloc Shell.OverflowCommand[Shell.PlayerBarRules.MaxOverflow];
        int n = Shell.PlayerBarRules.Overflow(layout, owns, active, video, buffer);
        return buffer[..n].ToArray();
    }

    [Fact]
    public void Full_carries_nothing_in_the_overflow()
        => Assert.Empty(Build(Shell.PlayerBarTier.Full, video: true));

    [Fact]
    public void Wide_overflows_only_now_playing()
        => Assert.Equal(new[] { Shell.OverflowCommand.NowPlaying }, Build(Shell.PlayerBarTier.Wide, video: true));

    [Fact]
    public void Compact_order_is_the_menu_order()
    {
        Assert.Equal(new[]
        {
            Shell.OverflowCommand.Previous, Shell.OverflowCommand.Next, Shell.OverflowCommand.Shuffle,
            Shell.OverflowCommand.Repeat, Shell.OverflowCommand.Lyrics, Shell.OverflowCommand.Queue,
            Shell.OverflowCommand.NowPlaying, Shell.OverflowCommand.Video, Shell.OverflowCommand.Mute,
        }, Build(Shell.PlayerBarTier.Compact, video: true));
    }

    [Fact]
    public void The_video_row_is_not_reserved_so_no_video_means_no_row()
        => Assert.DoesNotContain(Shell.OverflowCommand.Video, Build(Shell.PlayerBarTier.Compact, video: false));

    [Fact]
    public void A_non_owning_bar_carries_no_transport_verbs()
    {
        var rows = Build(Shell.PlayerBarTier.Compact, owns: false);
        Assert.DoesNotContain(Shell.OverflowCommand.Previous, rows);
        Assert.DoesNotContain(Shell.OverflowCommand.Next, rows);
    }

    [Fact]
    public void An_idle_bar_below_medium_has_no_volume_or_lyrics_affordance()
    {
        var rows = Build(Shell.PlayerBarTier.Compact, active: false);
        Assert.DoesNotContain(Shell.OverflowCommand.Mute, rows);
        Assert.DoesNotContain(Shell.OverflowCommand.Lyrics, rows);
    }

    [Fact]
    public void The_inline_video_slot_rides_the_queue_tier()
    {
        Assert.True(Shell.PlayerBarRules.VideoSlotReserved(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Wide), true));
        Assert.False(Shell.PlayerBarRules.VideoSlotReserved(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Comfortable), true));
        Assert.False(Shell.PlayerBarRules.VideoSlotReserved(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Full), false));
    }
}

public class PlayerBarSmallRulesTests
{
    [Fact]
    public void Repeat_cycles_off_context_track_off()
    {
        Assert.Equal(RepeatMode.Context, Shell.PlayerBarRules.NextRepeat(RepeatMode.Off));
        Assert.Equal(RepeatMode.Track, Shell.PlayerBarRules.NextRepeat(RepeatMode.Context));
        Assert.Equal(RepeatMode.Off, Shell.PlayerBarRules.NextRepeat(RepeatMode.Track));
        Assert.Equal(FluentGpu.Controls.Icons.RepeatOne, Shell.PlayerBarRules.RepeatGlyph(RepeatMode.Track));
        Assert.Equal(FluentGpu.Controls.Icons.RepeatAll, Shell.PlayerBarRules.RepeatGlyph(RepeatMode.Context));
    }

    [Fact]
    public void The_mute_glyph_follows_the_session_mute_or_a_zero_volume()
    {
        Assert.True(Shell.PlayerBarRules.ShowsMuteGlyph(true, 0.8f));
        Assert.True(Shell.PlayerBarRules.ShowsMuteGlyph(false, 0.001f));
        Assert.False(Shell.PlayerBarRules.ShowsMuteGlyph(false, 0.01f));
    }

    [Fact]
    public void The_software_mute_toggles_zero_and_seven_tenths()
    {
        Assert.Equal(0f, Shell.PlayerBarRules.SoftwareMuteTarget(0.5f));
        Assert.Equal(0.7f, Shell.PlayerBarRules.SoftwareMuteTarget(0f));
    }

    [Theory]
    [InlineData(0.72f, "72%")]
    [InlineData(0f, "0%")]
    [InlineData(1.4f, "100%")]
    [InlineData(-0.2f, "0%")]
    public void The_volume_bubble_has_no_space_and_is_clamped(float volume, string expected)
        => Assert.Equal(expected, Shell.PlayerBarRules.VolumePercent(volume));

    [Fact]
    public void The_heart_pops_only_on_the_same_playable_s_save_edge()
    {
        Assert.True(Shell.PlayerBarRules.LikePops(7, false, 7, true));
        Assert.False(Shell.PlayerBarRules.LikePops(6, false, 7, true));    // a track change, not a like
        Assert.False(Shell.PlayerBarRules.LikePops(7, true, 7, false));    // an unlike is a plain swap
        Assert.False(Shell.PlayerBarRules.LikePops(0, false, 0, true));    // nothing playing
    }
}

public class PlayerBarDeviceRosterTests
{
    static Playback.Devices.Row Row(string id, DeviceKind kind) => new() { Id = id, Name = id, Kind = kind };

    [Fact]
    public void Playing_on_needs_a_foreign_owner_not_merely_an_active_slot()
    {
        Playback.Devices.Row[] rows = [Row("pc", DeviceKind.ThisDevice), Row("phone", DeviceKind.Phone)];
        Assert.Equal(1, Shell.DeviceRoster.RemoteSlot(Playback.Owner.Foreign, 1, rows));
        // Nobody (the phone just left) keeps its slot in 0.3's active-slot signal — the line must still disappear.
        Assert.Equal(-1, Shell.DeviceRoster.RemoteSlot(Playback.Owner.Nobody, 1, rows));
        Assert.Equal(-1, Shell.DeviceRoster.RemoteSlot(Playback.Owner.Us, 0, rows));
    }

    [Fact]
    public void This_device_is_never_remote_and_an_out_of_range_slot_is_nothing()
    {
        Playback.Devices.Row[] rows = [Row("pc", DeviceKind.ThisDevice)];
        Assert.Equal(-1, Shell.DeviceRoster.RemoteSlot(Playback.Owner.Foreign, 0, rows));
        Assert.Equal(-1, Shell.DeviceRoster.RemoteSlot(Playback.Owner.Foreign, 5, rows));
        Assert.Equal(-1, Shell.DeviceRoster.RemoteSlot(Playback.Owner.Foreign, -1, rows));
    }

    [Fact]
    public void Roster_lookups_find_by_id_and_find_home()
    {
        Playback.Devices.Row[] rows = [Row("tv", DeviceKind.Tv), Row("PC", DeviceKind.ThisDevice)];
        Assert.Equal(0, Shell.DeviceRoster.SlotOfId(rows, "TV"));
        Assert.Equal(-1, Shell.DeviceRoster.SlotOfId(rows, ""));
        Assert.Equal(1, Shell.DeviceRoster.ThisDeviceSlot(rows));
    }

    [Fact]
    public void The_glyph_maps()
    {
        Assert.Equal(FluentGpu.Controls.Icons.CellPhone, Shell.DeviceRoster.ConnectGlyph(DeviceKind.Phone));
        Assert.Equal(FluentGpu.Controls.Icons.Speakers, Shell.DeviceRoster.ConnectGlyph(DeviceKind.Speaker));
        Assert.Equal(FluentGpu.Controls.Icons.TvMonitor, Shell.DeviceRoster.ConnectGlyph(DeviceKind.Tv));
        Assert.Equal(FluentGpu.Controls.Icons.ThisPc, Shell.DeviceRoster.ConnectGlyph(DeviceKind.Computer));
        Assert.Equal(FluentGpu.Controls.Icons.Speakers, Shell.DeviceRoster.LocalGlyph(0));
        Assert.Equal(FluentGpu.Controls.Icons.Headphones, Shell.DeviceRoster.LocalGlyph(2));
        Assert.Equal(FluentGpu.Controls.Icons.ThisPc, Shell.DeviceRoster.LocalGlyph(9));
    }
}

public class SeekRailRulesTests
{
    static Playback.LiveWindow Dvr(long start, long end, long pos) => new(true, start, end, end, pos, false);

    [Fact]
    public void The_mode_is_the_timeline_s_shape()
    {
        Assert.Equal(Shell.SeekRailMode.Track, Shell.SeekRail.ModeOf(Playback.LiveWindow.None));
        Assert.Equal(Shell.SeekRailMode.Dvr, Shell.SeekRail.ModeOf(Dvr(0, 60_000, 30_000)));
        // Under 30 s is not a window: the rail becomes the breathing line.
        Assert.Equal(Shell.SeekRailMode.Line, Shell.SeekRail.ModeOf(Dvr(0, 29_999, 10_000)));
    }

    [Fact]
    public void The_scrub_gate_ignores_the_model_while_the_finger_is_down()
    {
        Assert.Equal(0.25f, Shell.SeekRail.Displayed(true, 0.25f, 0.9f));
        Assert.Equal(0.9f, Shell.SeekRail.Displayed(false, 0.25f, 0.9f));
    }

    [Fact]
    public void An_unknown_duration_is_an_empty_rail_and_a_track_clamps()
    {
        Assert.Equal(0f, Shell.SeekRail.ModelFraction(Shell.SeekRailMode.Track, 5000, 0, Playback.LiveWindow.None, false));
        Assert.Equal(1f, Shell.SeekRail.ModelFraction(Shell.SeekRailMode.Track, 9000, 8000, Playback.LiveWindow.None, false));
        Assert.Equal(0.5f, Shell.SeekRail.ModelFraction(Shell.SeekRailMode.Track, 4000, 8000, Playback.LiveWindow.None, false));
    }

    [Fact]
    public void A_dvr_rail_snaps_full_at_the_edge_and_maps_the_window_behind_it()
    {
        var w = Dvr(10_000, 70_000, 40_000);
        Assert.Equal(1f, Shell.SeekRail.ModelFraction(Shell.SeekRailMode.Dvr, 40_000, 0, w, isBehind: false));
        Assert.Equal(0.5f, Shell.SeekRail.ModelFraction(Shell.SeekRailMode.Dvr, 40_000, 0, w, isBehind: true), 3);
    }

    [Fact]
    public void Quantisation_lands_on_whole_pixels_and_passes_through_without_a_width()
    {
        Assert.Equal(MathF.Round(0.3333f * 300f) / 300f, Shell.SeekRail.Quantize(0.3333f, 300f));
        Assert.Equal(0.3333f, Shell.SeekRail.Quantize(0.3333f, 0f));
    }

    [Theory]
    [InlineData(180_000L, 544f, 250f)]     // a 3-minute track dwells the ceiling
    [InlineData(10_000L, 544f, 33f)]       // a 10-second clip hits the floor
    [InlineData(60_000L, 544f, 110.294f)]  // in between it is exactly span / px
    [InlineData(0L, 544f, 100f)]           // unknown span
    [InlineData(180_000L, 0f, 100f)]       // unknown width
    public void The_dwell_is_the_pixel_rate_clamped(long span, float px, float expected)
        => Assert.Equal(expected, Shell.SeekRail.DwellMs(span, px), 2);

    [Fact]
    public void The_ticker_runs_only_while_the_playhead_moves_on_its_own()
    {
        Assert.True(Shell.SeekRail.Advances(true, Playback.Fault.None, Playback.Phase.Playing, false));
        Assert.False(Shell.SeekRail.Advances(true, Playback.Fault.None, Playback.Phase.Paused, false));
        Assert.False(Shell.SeekRail.Advances(true, Playback.Fault.None, Playback.Phase.Playing, buffering: true));
        Assert.False(Shell.SeekRail.Advances(true, Playback.Fault.Network, Playback.Phase.Playing, false));
    }

    [Fact]
    public void The_rail_is_disabled_while_loading_or_unseekable()
    {
        Assert.True(Shell.SeekRail.Enabled(true, Playback.Fault.None, Playback.Phase.Paused, true));
        Assert.False(Shell.SeekRail.Enabled(true, Playback.Fault.None, Playback.Phase.Loading, true));
        Assert.False(Shell.SeekRail.Enabled(true, Playback.Fault.None, Playback.Phase.Playing, false));
        Assert.False(Shell.SeekRail.Enabled(false, Playback.Fault.None, Playback.Phase.Playing, true));
    }

    [Fact]
    public void A_commit_clamps_into_the_duration_or_into_the_window()
    {
        Assert.Equal(60_000L, Shell.SeekRail.CommitTargetMs(0.5f, 120_000, Playback.LiveWindow.None));
        Assert.Equal(120_000L, Shell.SeekRail.CommitTargetMs(1.5f, 120_000, Playback.LiveWindow.None));
        var w = Dvr(10_000, 70_000, 20_000);
        Assert.Equal(40_000L, Shell.SeekRail.CommitTargetMs(0.5f, 0, w));
        Assert.False(Shell.SeekRail.CanCommit(Playback.LiveWindow.None, 0));
        Assert.True(Shell.SeekRail.CanCommit(w, 0));
    }

    [Fact]
    public void A_released_drag_holds_the_drop_point_only_briefly()
    {
        Assert.False(Shell.SeekRail.HoldsDrop(0, 10_000));                                   // nothing pending
        Assert.True(Shell.SeekRail.HoldsDrop(10_000, 10_000 + Shell.SeekRail.CommitHoldMs - 1));
        Assert.False(Shell.SeekRail.HoldsDrop(10_000, 10_000 + Shell.SeekRail.CommitHoldMs));   // a refused seek lets go
    }

    [Fact]
    public void The_thumb_never_leaves_the_rail_and_the_pointer_fraction_clamps()
    {
        Assert.Equal(0f, Shell.SeekRail.ThumbX(300f, 0f, 22f));
        Assert.Equal(278f, Shell.SeekRail.ThumbX(300f, 1f, 22f));
        Assert.Equal(139f, Shell.SeekRail.ThumbX(300f, 0.5f, 22f));
        Assert.Equal(1f, Shell.SeekRail.FractionAt(400f, 300f));
        Assert.Equal(0f, Shell.SeekRail.FractionAt(-10f, 300f));
    }
}

public class TimeLabelRulesTests
{
    [Fact]
    public void The_remaining_label_never_prints_a_minus_on_nothing_left()
    {
        Assert.Equal((0L, false), Shell.TimeLabel.Of(true, true, 200_000, 200_000, false, 0, 0));
        Assert.Equal((400L, false), Shell.TimeLabel.Of(true, true, 199_600, 200_000, false, 0, 0));
        Assert.Equal((1_000L, true), Shell.TimeLabel.Of(true, true, 199_000, 200_000, false, 0, 0));
    }

    [Fact]
    public void The_right_label_toggles_to_the_duration_and_the_left_is_elapsed()
    {
        Assert.Equal((200_000L, false), Shell.TimeLabel.Of(true, false, 42_000, 200_000, false, 0, 0));
        Assert.Equal((42_000L, false), Shell.TimeLabel.Of(false, true, 42_000, 200_000, false, 0, 0));
    }

    [Fact]
    public void Live_elapsed_counts_since_tune_in_and_falls_back_to_the_position_before_the_stamp()
    {
        Assert.Equal((90_000L, false), Shell.TimeLabel.Of(false, true, 5_000, 0, isLive: true, 1_000_000, 1_090_000));
        Assert.Equal((5_000L, false), Shell.TimeLabel.Of(false, true, 5_000, 0, isLive: true, 0, 1_090_000));
        Assert.Equal(0L, Shell.TimeLabel.ElapsedSinceTuneIn(2_000, 5_000, 1_000));
    }

    [Fact]
    public void Go_live_is_offered_only_behind_a_rewindable_window()
    {
        var window = new Playback.LiveWindow(true, 0, 60_000, 60_000, 20_000, false);
        var line = new Playback.LiveWindow(true, 0, 10_000, 10_000, 5_000, false);
        Assert.True(Shell.TimeLabel.OffersGoLive(window, isBehind: true));
        Assert.False(Shell.TimeLabel.OffersGoLive(window, isBehind: false));
        Assert.False(Shell.TimeLabel.OffersGoLive(line, isBehind: true));
        Assert.True(Shell.TimeLabel.RightSlotIsLive(true, true));
        Assert.False(Shell.TimeLabel.RightSlotIsLive(false, true));
    }
}
