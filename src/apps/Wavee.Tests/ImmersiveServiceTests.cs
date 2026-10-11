// ── Wavee.Tests/ImmersiveServiceTests.cs — fullscreen, the focus scope and the idle machine, keyed by the mode (themes plan C4, X12)
// (Shell/ImmersiveService.cs.) The REAL service over a recording host: no window, no input hooks, no mounted view.
//
//   ENTER / EXIT ARE IDEMPOTENT  the second Enter keeps the FIRST one's remembered prior state; an Exit without an Enter is nothing.
//   RESTORE WHAT WAS FOUND       a window that was already fullscreen stays fullscreen on exit; one that was windowed goes back.
//   A REMOUNT DOES NOT EXIT      a view's teardown releases through "is the mode still wanted?": a remount while it is leaves the
//                                window, the scope and the idle machine exactly as they are; the exit happens when the mode ends.

using FluentGpu.Controls.Media;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

sealed class RecordingImmersiveHost : IImmersiveHost
{
    public bool Fullscreen;
    public NodeHandle Focus;
    public readonly List<string> Calls = [];

    public bool IsWindowFullscreen() => Fullscreen;
    public void SetWindowFullscreen(bool on) { Fullscreen = on; Calls.Add("fullscreen:" + on); }
    public NodeHandle GetFocus() => Focus;
    public void PushFocusScope(NodeHandle root) => Calls.Add("push:" + root.Raw.Index);
    public void PopFocusScope(NodeHandle root) => Calls.Add("pop:" + root.Raw.Index);
    public void FocusNode(NodeHandle node, bool visible) { Focus = node; Calls.Add("focus:" + node.Raw.Index); }
    public void RestoreFocus(NodeHandle node) { Focus = node; Calls.Add("restore:" + node.Raw.Index); }
}

public class ImmersiveWindowTests
{
    const ImmersiveKind StageKind = ImmersiveKind.Stage;

    static NodeHandle Node(uint index) => new(new Handle(index, 1));

    [Fact]
    public void Entering_goes_fullscreen_and_exiting_restores_the_windowed_state_it_found()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost();
        Assert.True(service.EnterWindow(StageKind, host));
        Assert.True(host.Fullscreen);
        Assert.True(service.IsEntered(StageKind));
        Assert.True(service.ExitWindow(StageKind, host));
        Assert.False(host.Fullscreen);
        Assert.False(service.IsEntered(StageKind));
        Assert.Equal(new[] { "fullscreen:True", "fullscreen:False" }, host.Calls);
    }

    [Fact]
    public void A_window_that_was_already_fullscreen_is_left_fullscreen()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost { Fullscreen = true };
        service.EnterWindow(StageKind, host);
        service.ExitWindow(StageKind, host);
        Assert.True(host.Fullscreen);
        Assert.Empty(host.Calls);                              // never asked to change what it already was
    }

    [Fact]
    public void Entering_twice_keeps_the_first_remembered_state_and_asks_the_window_once()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost();
        Assert.True(service.EnterWindow(StageKind, host));
        Assert.False(service.EnterWindow(StageKind, host));        // the second sees a fullscreen window, which is OUR doing
        service.ExitWindow(StageKind, host);
        Assert.False(host.Fullscreen);                         // so exit still restores windowed
        Assert.Equal(new[] { "fullscreen:True", "fullscreen:False" }, host.Calls);
    }

    [Fact]
    public void Exiting_without_entering_does_nothing_and_exiting_twice_restores_once()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost();
        Assert.False(service.ExitWindow(StageKind, host));
        service.EnterWindow(StageKind, host);
        Assert.True(service.ExitWindow(StageKind, host));
        Assert.False(service.ExitWindow(StageKind, host));
        Assert.Equal(new[] { "fullscreen:True", "fullscreen:False" }, host.Calls);
    }

    [Fact]
    public void A_remount_of_the_view_while_the_mode_is_wanted_does_not_exit_fullscreen()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost();
        service.EnterWindow(StageKind, host);

        // the old view tears down and a new one mounts, in a root swap: the stage is still wanted
        Assert.False(service.ReleaseWindow(StageKind, host, stillWanted: () => true));
        Assert.False(service.EnterWindow(StageKind, host));
        Assert.True(host.Fullscreen);
        Assert.True(service.IsEntered(StageKind));
        Assert.Equal(new[] { "fullscreen:True" }, host.Calls); // no exit, no second entry

        // the mode ends: the teardown now leaves
        Assert.True(service.ReleaseWindow(StageKind, host, stillWanted: () => false));
        Assert.False(host.Fullscreen);
    }

    [Fact]
    public void The_stage_and_the_video_fullscreen_are_independent_kinds()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost();
        service.EnterWindow(StageKind, host);
        Assert.False(service.IsEntered(ImmersiveKind.VideoFullscreen));
        Assert.False(service.ExitWindow(ImmersiveKind.VideoFullscreen, host));
        Assert.True(service.IsEntered(StageKind));
    }

    // ── the focus scope ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_scope_is_pushed_at_the_root_focus_parks_on_it_and_pops_back_to_whoever_had_it()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost { Focus = Node(7) };
        service.PushFocus(StageKind, host, Node(1), focusRoot: true);
        Assert.True(service.HasFocusScope(StageKind));
        Assert.Equal(new[] { "push:1", "focus:1" }, host.Calls);

        Assert.True(service.PopFocus(StageKind, host));
        Assert.Equal(new[] { "push:1", "focus:1", "pop:1", "restore:7" }, host.Calls);
        Assert.False(service.PopFocus(StageKind, host));            // idempotent
    }

    [Fact]
    public void Focus_stays_where_it_is_when_the_root_is_not_to_take_it_and_nobody_is_restored_when_nobody_had_it()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost();                // nobody focused
        service.PushFocus(StageKind, host, Node(1), focusRoot: false);
        service.PopFocus(StageKind, host);
        Assert.Equal(new[] { "push:1", "pop:1" }, host.Calls);
    }

    [Fact]
    public void A_null_root_pushes_nothing()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost();
        service.PushFocus(StageKind, host, default, focusRoot: true);
        Assert.False(service.HasFocusScope(StageKind));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void A_remount_moves_the_scope_to_the_new_root_and_still_returns_focus_to_the_original_opener()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost { Focus = Node(7) };
        service.PushFocus(StageKind, host, Node(1), focusRoot: true);

        Assert.False(service.ReleaseFocus(StageKind, host, stillWanted: () => true));   // the old view's teardown: kept
        service.PushFocus(StageKind, host, Node(2), focusRoot: true);                    // the new view's mount: scope moves

        Assert.True(service.ReleaseFocus(StageKind, host, stillWanted: () => false));
        Assert.Equal(new[] { "push:1", "focus:1", "pop:1", "push:2", "focus:2", "pop:2", "restore:7" }, host.Calls);
    }

    // ── the idle machine ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_idle_machine_is_the_same_object_across_a_remount_until_the_mode_exits()
    {
        var service = new ImmersiveService();
        var host = new RecordingImmersiveHost();
        service.EnterWindow(StageKind, host);
        var before = service.IdleOf(StageKind);
        service.ReleaseWindow(StageKind, host, stillWanted: () => true);                // the root swap
        Assert.Same(before, service.IdleOf(StageKind));

        service.ReleaseWindow(StageKind, host, stillWanted: () => false);               // the stage closes
        Assert.NotSame(before, service.IdleOf(StageKind));
    }

    [Fact]
    public void Each_kind_has_its_own_idle_machine()
    {
        var service = new ImmersiveService();
        Assert.NotSame(service.IdleOf(ImmersiveKind.Stage), service.IdleOf(ImmersiveKind.VideoFullscreen));
    }
}

public class ChromeIdleTests
{
    static ChromeIdle Playing()
    {
        var idle = new ChromeIdle();
        idle.Start(0);
        idle.SetPlayback(playing: true, 0);
        return idle;
    }

    [Fact]
    public void The_machine_exists_only_after_start_and_a_fresh_one_shows_its_controls()
    {
        var idle = new ChromeIdle();
        Assert.False(idle.Started);
        Assert.True(idle.ChromeVisible);
        idle.Start(0);
        Assert.True(idle.Started);
        idle.Start(5_000);                                     // a surface adopting a surviving idle keeps its state
        Assert.True(idle.Started);
    }

    [Fact]
    public void Controls_idle_away_while_playing_with_nothing_holding_them()
    {
        var idle = Playing();
        idle.Tick(100_000);
        Assert.False(idle.ChromeVisible);
    }

    [Fact]
    public void Menus_are_counted_and_the_chrome_stays_until_the_last_one_closes()
    {
        var idle = Playing();
        idle.MenuOpen(true, 0);                                // the options popover
        idle.MenuOpen(true, 0);                                // the teaching tip
        Assert.Equal(2, idle.MenuHolds);
        idle.Tick(100_000);
        Assert.True(idle.ChromeVisible);

        idle.MenuOpen(false, 100_000);                         // one closes: still held
        Assert.Equal(1, idle.MenuHolds);
        idle.Tick(200_000);
        Assert.True(idle.ChromeVisible);

        idle.MenuOpen(false, 200_000);                         // the last closes: the dwell runs again
        Assert.Equal(0, idle.MenuHolds);
        idle.Tick(400_000);
        Assert.False(idle.ChromeVisible);
    }

    [Fact]
    public void The_count_never_goes_below_zero_and_a_menu_opened_before_the_machine_still_holds_it()
    {
        var idle = new ChromeIdle();
        idle.MenuOpen(false, 0);
        Assert.Equal(0, idle.MenuHolds);
        idle.MenuOpen(true, 0);                                // a flyout opened before the machine started
        Assert.Equal(1, idle.MenuHolds);
        idle.Start(0);
        idle.SetPlayback(true, 0);
        idle.Tick(100_000);
        Assert.True(idle.ChromeVisible);                       // the machine is told of the hold it missed, when it starts
    }

    [Fact]
    public void Activity_reveals_the_controls_again()
    {
        var idle = Playing();
        idle.Tick(100_000);
        Assert.False(idle.ChromeVisible);
        idle.Activity(ChromeActivity.Keyboard, 100_000);
        Assert.True(idle.ChromeVisible);
    }
}
