// ── Wavee.Tests/TrayHostTests.cs — the pure pieces the tray HOST binds through (W-C) ────────────────────────────────
//
// `Platform/Tray.Host.cs` is SHELL (an HWND, Shell_NotifyIcon, a WinEvent hook), so its behaviour is the on-box checklist
// (tray plan §12.2). What it binds THROUGH is pure and pinned here: the engine event → CORE event map (an engine
// addition must fail loudly, not dispatch the wrong verb), the menu row → native row map (the caption can never be
// chosen), the foreground latch that lets a click on the icon hide a focused Wavee, and `wavee://quit` staying quiet.
// No window, no OS, no source text.

using FluentGpu.WindowsApi.Shell;

using Wavee;

using Xunit;

namespace Wavee.Tests;

public class TrayHostBindingTests
{
    [Fact]
    public void Every_engine_icon_event_maps_to_the_tray_event_of_the_same_name()
    {
        var engine = Enum.GetValues<NotifyIconEvent>();
        foreach (NotifyIconEvent e in engine)
        {
            Assert.True(Tray.Host.TryEventOf(e, out Tray.IconEvent mapped), e + " has no tray event");
            Assert.Equal(e.ToString(), mapped.ToString());
        }
        Assert.Equal(Enum.GetValues<Tray.IconEvent>().Length, engine.Length);
    }

    [Fact]
    public void Menu_rows_reach_the_native_menu_with_their_ids_so_the_caption_can_never_be_chosen()
    {
        var f = TrayFixtures.Playing("Rock & Roll", "Artist") with { Saved = true };
        var rows = TrayFixtures.MenuFor(in f, TrayFixtures.Window(), anyHideModeOn: false);
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            NotifyMenuItem item = Tray.Host.ItemOf(in row);
            Assert.Equal((int)row.Id, item.Id);
            Assert.Equal(row.Text, item.Text);   // verbatim: the CORE already doubled the ampersand
            Assert.Equal(row.Enabled, item.Enabled);
            Assert.Equal(row.Checked, item.Checked);
            Assert.Equal(row.Default, item.Default);
        }
        Assert.True(Tray.Host.ItemOf(in rows[0]).Id < 0);                                    // the now-playing caption
        Assert.Contains(rows, r => r.Id == Tray.MenuId.Separator && Tray.Host.ItemOf(in r).Id == 0);
    }
}

public class TrayForegroundLatchTests
{
    [Fact]
    public void A_window_that_was_never_in_front_was_never_deactivated()
    {
        var latch = new Tray.ForegroundLatch();
        latch.Observe(waveeInFront: false, nowMs: 100);
        Assert.Equal(-1, latch.MsSinceDeactivated(200));
        Assert.False(Tray.ForegroundAtClick(foregroundNow: false, latch.MsSinceDeactivated(200)));
    }

    [Fact]
    public void The_click_that_activated_the_taskbar_still_reads_as_Wavee_in_front()
    {
        var latch = new Tray.ForegroundLatch();
        latch.Seed(waveeInFront: true);
        latch.Observe(waveeInFront: false, nowMs: 1_000);                                     // the mouse-down on the tray
        Assert.Equal(80, latch.MsSinceDeactivated(1_080));                                     // NIN_SELECT on the mouse-up
        Assert.True(Tray.ForegroundAtClick(foregroundNow: false, latch.MsSinceDeactivated(1_080)));
    }

    [Fact]
    public void Coming_back_to_the_front_clears_the_stamp()
    {
        var latch = new Tray.ForegroundLatch();
        latch.Seed(waveeInFront: true);
        latch.Observe(false, 1_000);
        latch.Observe(true, 1_200);
        Assert.Equal(-1, latch.MsSinceDeactivated(1_300));
    }

    [Fact]
    public void Moving_between_other_windows_keeps_the_moment_Wavee_left()
    {
        var latch = new Tray.ForegroundLatch();
        latch.Seed(waveeInFront: true);
        latch.Observe(false, 1_000);
        latch.Observe(false, 5_000);                                                           // another app → another app
        Assert.Equal(5_000, latch.MsSinceDeactivated(6_000));
        Assert.False(Tray.ForegroundAtClick(foregroundNow: false, latch.MsSinceDeactivated(6_000)));
    }
}

public class TrayDeepLinkTests
{
    [Fact]
    public void A_quit_link_parses_and_never_raises_the_window()
    {
        Assert.Equal(Shell.DeepLinkKind.Quit, Shell.DeepLink("wavee://quit").Kind);
        Assert.Equal(Shell.DeepLinkKind.Quit, Shell.DeepLink("WAVEE://Quit/").Kind);
        Assert.False(Tray.WakeFor(Shell.DeepLinkKind.Quit));
    }

    [Fact]
    public void The_jump_list_transport_links_leave_a_hidden_window_hidden_and_an_open_link_raises_it()
    {
        Assert.False(Tray.WakeFor(Shell.DeepLink("wavee://pause").Kind));
        Assert.False(Tray.WakeFor(Shell.DeepLink("wavee://resume").Kind));
        Assert.True(Tray.WakeFor(Shell.DeepLink("wavee://open?route=home").Kind));
        Assert.True(Tray.WakeFor(Shell.DeepLink("not a link").Kind));                          // unknown payload: a relaunch
    }
}
