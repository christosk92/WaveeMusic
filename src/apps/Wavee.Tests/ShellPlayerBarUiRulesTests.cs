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

    [Fact]
    public void An_error_keeps_previous_and_next_armed_where_the_context_allows_them()
    {
        // The way OUT of a dead row: Retry re-plays the same row, but `Advance` has no error guard and heals the fault
        // through `PutOnDeck`, so the skip verbs must stay live under Error even though the transport (play/pause) is
        // not. The context's own restriction still wins — a cluster that forbids the skip greys it as ever.
        var f = Shell.PlayerBarRules.Fold(hasCurrent: true, Playback.Fault.Unavailable, Playback.Phase.Paused,
            buffering: false, Playback.RecoveryKind.None, canSkipPrev: true, canSkipNext: true);
        Assert.Equal(Shell.PlayerState.Error, f.State);
        Assert.False(f.CanTransport);
        Assert.True(f.PrevEnabled);
        Assert.True(f.NextEnabled);
        Assert.Equal(Shell.PrimaryVerb.Retry, f.Primary);

        var restricted = Shell.PlayerBarRules.Fold(true, Playback.Fault.DecodeFailed, Playback.Phase.Paused, false,
            Playback.RecoveryKind.None, canSkipPrev: false, canSkipNext: true);
        Assert.False(restricted.PrevEnabled);
        Assert.True(restricted.NextEnabled);

        // NoTrack and Loading are unchanged: nothing to skip from, so the skip verbs stay dead.
        var idle = Shell.PlayerBarRules.Fold(false, Playback.Fault.None, Playback.Phase.Idle, false,
            Playback.RecoveryKind.None, true, true);
        Assert.False(idle.PrevEnabled);
        Assert.False(idle.NextEnabled);
        var loading = Shell.PlayerBarRules.Fold(true, Playback.Fault.None, Playback.Phase.Loading, false,
            Playback.RecoveryKind.None, true, true);
        Assert.False(loading.NextEnabled);
    }

    [Theory]
    [InlineData(true, Playback.Fault.None, Shell.RowAction.Toggle)]           // the deck row toggles pause/resume
    [InlineData(true, Playback.Fault.Unavailable, Shell.RowAction.Start)]     // the deck row under a fault is dead: start it afresh
    [InlineData(true, Playback.Fault.Network, Shell.RowAction.Start)]
    [InlineData(true, Playback.Fault.Unknown, Shell.RowAction.Start)]
    [InlineData(false, Playback.Fault.None, Shell.RowAction.Start)]           // any other row starts
    [InlineData(false, Playback.Fault.Unavailable, Shell.RowAction.Start)]
    public void A_row_click_toggles_only_the_healthy_deck_row(bool deckRow, Playback.Fault error, Shell.RowAction expected)
        => Assert.Equal(expected, Shell.PlayerBarRules.RowVerb(deckRow, error));

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
    public void The_inline_video_slot_rides_the_queue_tier_and_needs_a_video()
    {
        // The queue tier AND a video: with no video the cluster reclaims the split's width (user decision 2026-09-16).
        Assert.True(Shell.PlayerBarRules.VideoSlotReserved(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Full), hasVideo: true));
        Assert.True(Shell.PlayerBarRules.VideoSlotReserved(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Wide), hasVideo: true));
        Assert.False(Shell.PlayerBarRules.VideoSlotReserved(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Comfortable), hasVideo: true));
        Assert.False(Shell.PlayerBarRules.VideoSlotReserved(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Minimal), hasVideo: true));
        foreach (var tier in new[]
                 {
                     Shell.PlayerBarTier.Minimal, Shell.PlayerBarTier.Compact, Shell.PlayerBarTier.Medium,
                     Shell.PlayerBarTier.Comfortable, Shell.PlayerBarTier.Wide, Shell.PlayerBarTier.Full,
                 })
            Assert.False(Shell.PlayerBarRules.VideoSlotReserved(Shell.PlayerBarLayout.ForTier(tier), hasVideo: false));
    }

    [Fact]
    public void The_video_row_belongs_to_the_tiers_without_the_inline_split()
    {
        // Wide/Full carry the split inline, so the menu never duplicates it; below Wide the menu is the only route.
        Assert.DoesNotContain(Shell.OverflowCommand.Video, Build(Shell.PlayerBarTier.Wide, video: true));
        Assert.DoesNotContain(Shell.OverflowCommand.Video, Build(Shell.PlayerBarTier.Full, video: true));
        Assert.Contains(Shell.OverflowCommand.Video, Build(Shell.PlayerBarTier.Comfortable, video: true));
        Assert.Contains(Shell.OverflowCommand.Video, Build(Shell.PlayerBarTier.Minimal, video: true));
        Assert.DoesNotContain(Shell.OverflowCommand.Video, Build(Shell.PlayerBarTier.Comfortable, active: false, video: true));
    }
}

/// <summary>A2 (2026-09-16 recording): the now-playing bar reflowed on track start because the heart, lyrics, video and
/// "⋯" were ADDED on <c>active</c>, each arrival stealing width from the seek bar while every cluster animated its
/// bounds. Slot presence is now a function of the tier, with ONE state input — <c>hasVideo</c> — that adds or removes
/// the video split (user decision, same day: an unlit 52-DIP hole beside lyrics was worse than the row easing once
/// when a video track lands). <see cref="Shell.PlayerState"/> still lights faces only. These facts pin that split.</summary>
public class PlayerBarSlotReservationTests
{
    static readonly Shell.PlayerBarTier[] Tiers =
    [
        Shell.PlayerBarTier.Minimal, Shell.PlayerBarTier.Compact, Shell.PlayerBarTier.Medium,
        Shell.PlayerBarTier.Comfortable, Shell.PlayerBarTier.Wide, Shell.PlayerBarTier.Full,
    ];

    static readonly Shell.PlayerState[] States =
    [
        Shell.PlayerState.NoTrack, Shell.PlayerState.Loading, Shell.PlayerState.Reconnecting,
        Shell.PlayerState.Error, Shell.PlayerState.Active,
    ];

    /// <summary>The slot sum a test computes for itself, walking the bits the way the row does.</summary>
    static float SlotSum(in Shell.PlayerBarLayout layout, bool hasVideo)
    {
        var slots = Shell.PlayerBarRules.RightSlots(layout, hasVideo);
        float w = 0f;
        int n = 0;
        for (int bit = 1; bit <= (int)Shell.RightSlot.More; bit <<= 1)
        {
            var slot = (Shell.RightSlot)bit;
            if ((slots & slot) == 0) continue;
            w += Shell.PlayerBarRules.SlotWidth(slot, layout);
            n++;
        }
        return n == 0 ? 0f : w + layout.RightGap * (n - 1);
    }

    [Fact]
    public void The_right_width_is_the_slot_sum_at_every_tier_with_and_without_a_video()
    {
        foreach (var tier in Tiers)
        {
            var layout = Shell.PlayerBarLayout.ForTier(tier);
            foreach (bool video in new[] { false, true })
                Assert.Equal(SlotSum(layout, video), Shell.PlayerBarRules.RightWidth(layout, video));
            Assert.True(Shell.PlayerBarRules.RightWidth(layout, hasVideo: false) > 0f);   // the device picker is always in the row
        }
    }

    [Fact]
    public void The_layout_s_max_width_is_the_widest_cluster_of_the_tier()
    {
        foreach (var tier in Tiers)
        {
            var L = Shell.PlayerBarLayout.ForTier(tier);
            Assert.Equal(Shell.PlayerBarRules.RightWidth(L, hasVideo: true), L.RightWMax);
            Assert.True(Shell.PlayerBarRules.RightWidth(L, hasVideo: false) <= L.RightWMax);
        }
    }

    [Fact]
    public void Wide_carries_nine_slots_and_the_plan_s_width_with_a_video_and_eight_without()
    {
        var L = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Wide);
        var withoutVideo = Shell.RightSlot.Shuffle | Shell.RightSlot.Repeat | Shell.RightSlot.Volume | Shell.RightSlot.VolumeSlider
                           | Shell.RightSlot.Lyrics | Shell.RightSlot.Queue | Shell.RightSlot.Devices | Shell.RightSlot.More;
        Assert.Equal(withoutVideo | Shell.RightSlot.Video, Shell.PlayerBarRules.RightSlots(L, hasVideo: true));
        Assert.Equal(withoutVideo, Shell.PlayerBarRules.RightSlots(L, hasVideo: false));
        // 7 plain buttons + the 96 rail + the 32+20 split, and 8 gaps between 9 slots (388 with the shipped constants).
        float expected = 7f * L.ButtonBox + Shell.PlayerBarLayout.VolumeSliderW + (L.ButtonBox + Shell.PlayerBarLayout.SplitChevronW)
                         + 8f * L.RightGap;
        Assert.Equal(expected, L.RightWMax);
        Assert.Equal(expected, Shell.PlayerBarRules.RightWidth(L, hasVideo: true));
        // Without a video the split AND its gap go: 7 buttons + the rail, 7 gaps between 8 slots.
        Assert.Equal(7f * L.ButtonBox + Shell.PlayerBarLayout.VolumeSliderW + 7f * L.RightGap,
            Shell.PlayerBarRules.RightWidth(L, hasVideo: false));
    }

    [Fact]
    public void A_track_starting_without_a_video_moves_nothing()
    {
        // The width has NO input from PlayerState: with hasVideo fixed, every state at every tier yields one number.
        // (The rules take no state argument at all; the fact pins that the FACE gate is the only place state enters.)
        foreach (var tier in Tiers)
        {
            var L = Shell.PlayerBarLayout.ForTier(tier);
            float idle = Shell.PlayerBarRules.RightWidth(L, hasVideo: false);
            var idleSlots = Shell.PlayerBarRules.RightSlots(L, hasVideo: false);
            foreach (var state in States)
            {
                // A no-video track in any of the five states: the same slots, the same width as the idle bar.
                Assert.Equal(idle, Shell.PlayerBarRules.RightWidth(L, hasVideo: false));
                Assert.Equal(idleSlots, Shell.PlayerBarRules.RightSlots(L, hasVideo: false));
                Assert.False(Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.Video, state, hasVideo: false));
            }
        }
    }

    [Fact]
    public void A_video_arriving_adds_exactly_the_split_slot()
    {
        foreach (var tier in Tiers)
        {
            var L = Shell.PlayerBarLayout.ForTier(tier);
            var without = Shell.PlayerBarRules.RightSlots(L, hasVideo: false);
            var with = Shell.PlayerBarRules.RightSlots(L, hasVideo: true);
            float delta = Shell.PlayerBarRules.RightWidth(L, hasVideo: true) - Shell.PlayerBarRules.RightWidth(L, hasVideo: false);
            Assert.Equal(Shell.RightSlot.None, without & Shell.RightSlot.Video);
            if (L.ShowQueue)
            {
                Assert.Equal(without | Shell.RightSlot.Video, with);
                Assert.Equal(L.ButtonBox + Shell.PlayerBarLayout.SplitChevronW + L.RightGap, delta);
            }
            else
            {
                // Below Wide the video verb lives in the "⋯" menu; the row is untouched.
                Assert.Equal(without, with);
                Assert.Equal(0f, delta);
            }
        }
    }

    [Fact]
    public void Slot_widths_are_the_button_box_but_for_the_rail_and_the_split()
    {
        var L = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Wide);
        Assert.Equal(Shell.PlayerBarLayout.VolumeSliderW, Shell.PlayerBarRules.SlotWidth(Shell.RightSlot.VolumeSlider, L));
        Assert.Equal(L.ButtonBox + Shell.PlayerBarLayout.SplitChevronW, Shell.PlayerBarRules.SlotWidth(Shell.RightSlot.Video, L));
        Assert.Equal(L.ButtonBox, Shell.PlayerBarRules.SlotWidth(Shell.RightSlot.Lyrics, L));
        Assert.Equal(L.ButtonBox, Shell.PlayerBarRules.SlotWidth(Shell.RightSlot.More, L));
    }

    [Fact]
    public void Slot_presence_follows_the_tier_flags_and_only_the_video_split_follows_has_video()
    {
        foreach (var tier in Tiers)
        foreach (bool video in new[] { false, true })
        {
            var L = Shell.PlayerBarLayout.ForTier(tier);
            var slots = Shell.PlayerBarRules.RightSlots(L, video);
            Assert.Equal(L.ShowShuffleRepeat, (slots & Shell.RightSlot.Shuffle) != 0);
            Assert.Equal(L.ShowShuffleRepeat, (slots & Shell.RightSlot.Repeat) != 0);
            Assert.Equal(L.ShowVolumeButton, (slots & Shell.RightSlot.Volume) != 0);
            Assert.Equal(L.ShowVolumeSlider, (slots & Shell.RightSlot.VolumeSlider) != 0);
            // Lyrics stays a face-only slot: RESERVED wherever the tier shows it, whatever plays.
            Assert.Equal(L.ShowLyrics, (slots & Shell.RightSlot.Lyrics) != 0);
            // The video split is the ONE slot with a state input: the queue tier AND a video.
            Assert.Equal(L.ShowQueue && video, (slots & Shell.RightSlot.Video) != 0);
            Assert.Equal(L.ShowQueue, (slots & Shell.RightSlot.Queue) != 0);
            Assert.Equal(L.ShowDevices, (slots & Shell.RightSlot.Devices) != 0);
            Assert.Equal(L.ShowExpand, (slots & Shell.RightSlot.Expand) != 0);
            Assert.Equal(Shell.PlayerBarRules.OverflowSlotReserved(L), (slots & Shell.RightSlot.More) != 0);
        }
    }

    [Fact]
    public void The_overflow_slot_never_moves_with_playback()
    {
        // The invariant the reservation rests on: at every tier the menu is non-empty iff the slot is reserved, for
        // EVERY combination of transport ownership, activity and video — so no state can make "⋯" appear or vanish.
        Span<Shell.OverflowCommand> buffer = stackalloc Shell.OverflowCommand[Shell.PlayerBarRules.MaxOverflow];
        foreach (var tier in Tiers)
        {
            var L = Shell.PlayerBarLayout.ForTier(tier);
            bool reserved = Shell.PlayerBarRules.OverflowSlotReserved(L);
            foreach (bool owns in new[] { false, true })
            foreach (bool active in new[] { false, true })
            foreach (bool video in new[] { false, true })
            {
                int n = Shell.PlayerBarRules.Overflow(L, owns, active, video, buffer);
                Assert.Equal(reserved, n > 0);
            }
        }
        Assert.False(Shell.PlayerBarRules.OverflowSlotReserved(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Full)));
        Assert.True(Shell.PlayerBarRules.OverflowSlotReserved(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Wide)));
    }

    [Fact]
    public void Faces_follow_state_while_slots_do_not()
    {
        // Lyrics need a playable; the video split (present only WITH a video) additionally needs an Active playable;
        // everything else is always lit.
        foreach (var state in States)
        {
            bool active = state == Shell.PlayerState.Active;
            Assert.Equal(active, Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.Lyrics, state, hasVideo: true));
            Assert.Equal(active, Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.Lyrics, state, hasVideo: false));
            Assert.Equal(active, Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.Video, state, hasVideo: true));
            Assert.False(Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.Video, state, hasVideo: false));
            Assert.True(Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.Shuffle, state, false));
            Assert.True(Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.Volume, state, false));
            Assert.True(Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.Queue, state, false));
            Assert.True(Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.Devices, state, false));
            Assert.True(Shell.PlayerBarRules.SlotFaceVisible(Shell.RightSlot.More, state, false));
        }
    }

    [Fact]
    public void The_heart_s_face_needs_a_playable_and_its_slot_survives_to_the_floor()
    {
        var minimal = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Minimal);
        var wide = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Wide);
        Assert.True(Shell.PlayerBarRules.LikeFaceVisible(wide, Shell.PlayerState.Active));
        Assert.True(Shell.PlayerBarRules.LikeFaceVisible(minimal, Shell.PlayerState.Active));   // identity-first
        Assert.False(Shell.PlayerBarRules.LikeFaceVisible(wide, Shell.PlayerState.Loading));
        Assert.False(Shell.PlayerBarRules.LikeFaceVisible(wide, Shell.PlayerState.NoTrack));
        Assert.False(Shell.PlayerBarRules.LikeFaceVisible(wide, Shell.PlayerState.Error));
    }

    [Fact]
    public void The_minimal_tier_at_the_300_dip_floor_leaves_the_seek_bar_48()
    {
        // 300 − row pads − identity block − two row gaps − (primary + cluster gap) − the WIDEST right cluster ≥ 48. Below
        // Medium there are no prev/next and no time labels, so the centre is the primary and the seek rail alone.
        var L = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Minimal);
        Assert.False(L.ShowPrevNext);
        Assert.False(L.ShowTimesElapsed);
        Assert.False(L.ShowTimesRemaining);
        float seek = 300f - 2f * L.RowPad - L.LeftW - 2f * L.RowGap - (L.PrimaryBox + L.ClusterGap) - L.RightWMax;
        Assert.True(seek >= 48f, "the seek bar has " + seek + " DIP at the 300-DIP floor");
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

    // G-210: the device picker had no height cap of its own — a dozen Connect devices overran the window.
    [Fact]
    public void The_picker_caps_at_the_window_height_less_the_inset()
        => Assert.Equal(900f - Shell.DeviceRoster.PickerWindowInset, Shell.DeviceRoster.PickerMaxHeight(900f));

    [Fact]
    public void A_tiny_window_still_leaves_a_usable_picker()
        => Assert.Equal(Shell.DeviceRoster.PickerMinHeight, Shell.DeviceRoster.PickerMaxHeight(50f));
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

public class PlayerBarFaultTitleTests
{
    // G-212: the bar printed "Can't play this track" for every Fault reason. One loc key per reason, and every
    // non-None reason must resolve to something OTHER than the generic fallback, or the fix is a no-op in disguise.
    [Fact]
    public void Every_fault_reason_gets_its_own_title_key()
    {
        Assert.Equal(Strings.Player.Fault.Network, Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.Network));
        Assert.Equal(Strings.Player.Fault.Unavailable, Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.Unavailable));
        Assert.Equal(Strings.Player.Fault.DrmRequired, Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.DrmRequired));
        Assert.Equal(Strings.Player.Fault.DecodeFailed, Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.DecodeFailed));
        Assert.Equal(Strings.Player.Fault.RuntimeMissing, Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.RuntimeMissing));
        Assert.Equal(Strings.Player.Fault.Unknown, Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.Unknown));
    }

    [Fact]
    public void The_reasons_are_pairwise_distinct_keys()
    {
        var keys = new[]
        {
            Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.Network),
            Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.Unavailable),
            Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.DrmRequired),
            Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.DecodeFailed),
            Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.RuntimeMissing),
        };
        Assert.Equal(keys.Length, System.Linq.Enumerable.Distinct(keys).Count());
    }

    [Fact]
    public void An_out_of_sequence_None_falls_back_to_the_generic_title_rather_than_throwing()
        => Assert.Equal(Strings.Player.Fault.Unknown, Shell.PlayerBarRules.FaultTitleKey(Playback.Fault.None));
}

public class PlayerBarPlayNextDropCapTests
{
    // G-211: a dropped playlist/album had no batch cap at all — a large drop inserted every track with no truncation
    // toast, and a null queue seam answered with total silence.
    [Fact]
    public void A_drop_under_the_cap_inserts_everything_and_is_not_truncated()
    {
        Assert.Equal(40, Shell.PlayerBarRules.DropInsertCount(40));
        Assert.False(Shell.PlayerBarRules.DropWasTruncated(40));
    }

    [Fact]
    public void A_drop_over_the_cap_inserts_only_the_cap_and_is_truncated()
    {
        int over = Shell.PlayerBarRules.MaxPlayNextDrop + 37;
        Assert.Equal(Shell.PlayerBarRules.MaxPlayNextDrop, Shell.PlayerBarRules.DropInsertCount(over));
        Assert.True(Shell.PlayerBarRules.DropWasTruncated(over));
    }

    [Fact]
    public void A_drop_exactly_at_the_cap_is_not_truncated()
    {
        Assert.Equal(Shell.PlayerBarRules.MaxPlayNextDrop,
            Shell.PlayerBarRules.DropInsertCount(Shell.PlayerBarRules.MaxPlayNextDrop));
        Assert.False(Shell.PlayerBarRules.DropWasTruncated(Shell.PlayerBarRules.MaxPlayNextDrop));
    }
}
