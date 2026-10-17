// ── Wavee.ShellTests/TierLayoutTests.Player.cs — the player bar and the side panels at every tier size (themes plan 3.5; TC2) ──────────
//
// TC2's partial of `TierLayoutTests`: the REAL Classic frame with Home on screen, at every plan 3.5 size and threshold edge, with no panel,
// one panel (lyrics) and two (lyrics and the queue beside it). On the records, never on pixels:
//
//   · the bar is its tier's record: the 56 mini bar at Compact (every target in it at least 40 x 40), 72 elsewhere (Short included);
//   · the panel is placed by its tier's record: a sheet across the window at Compact, floating 340 wide 8 in from the right at Small,
//     docked beside the page at Standard and Huge (the content card ends where the panel's band begins: no overlap);
//   · two panels dock only at Huge (D3), side by side, each at the tier's floor (380 at 2560, 420 at 3840); elsewhere the queue beside
//     the panel is not shown;
//   · the panels lie inside the window and the content region never runs under a docked one.
//   · the now-playing SHEET (Compact, C-NowPlaying-Compact) carries its own 48-tall header (close · "Playing from" · ⋯, inset 8, every
//     target at least 40 x 40) and the Devices door between Lyrics and Queue, one of three equal 48-tall doors, 14 under the transport row and 8 apart;
//   · a request for the device picker (a toast's "Choose device") at Compact opens the sheet's Devices picker: the sheet slides up if it is down,
//     shows the now-playing body if it shows another, and with the now-playing sheet already open just opens the picker.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    [Theory, MemberData(nameof(FrameSizes))]
    public void The_player_bar_is_its_tiers_record(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        var record = ClassicPlayerTiers.BarOf(tiers);
        Assert.Equal(s.Window == WidthTier.Compact ? ClassicBarForm.Mini : s.Window >= WidthTier.Huge ? ClassicBarForm.Huge : ClassicBarForm.Regular, record.Form);
        var bar = shell.Host.Scene.AbsoluteRect(Node(shell, ClassicFrameLayout.PlayerBarKey));
        Assert.Equal(record.Height, bar.H, 0.5f);
        Assert.Equal(s.Window == WidthTier.Compact ? 56f : 72f, bar.H, 0.5f);   // a Short window keeps its tier's bar (C-Shell-Short: 72)
        if (record.Form == ClassicBarForm.Mini)
        {
            Assert.False(shell.Find("mini-door").IsNull, $"{s.Name}: the mini bar's identity door");
            AssertTargetsAtLeast40(shell, Node(shell, ClassicFrameLayout.PlayerBarKey), s.Name);
        }
        else Assert.True(shell.Find("mini-door").IsNull, $"{s.Name}: no mini door outside Compact");
    });

    [Theory, MemberData(nameof(FrameSizes))]
    public void One_panel_is_placed_by_its_tiers_record(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        try
        {
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.Lyrics);
            shell.RunFrames(40);
            var rule = ClassicPanelTiers.Of(tiers);
            var placement = rule.Rule.Placement;
            Assert.Equal(placement, Shell.Ui.PanelPlacement.Peek());
            Assert.Equal(placement == PanelPlacement.Dock, Shell.Ui.RailFits.Peek());
            Assert.False(Shell.PanelHost.Shared.SecondOpenNow());

            var scene = shell.Host.Scene;
            var host = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.HostKey));
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            var title = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.TitleBarKey));
            var bar = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.PlayerBarKey));
            Assert.True(host.X >= -0.5f && host.X + host.W <= s.Width + 0.5f, $"{s.Name}: the panel runs outside the window ({host})");
            Assert.True(host.Y >= title.Y + title.H - 0.5f && host.Y + host.H <= s.Height + 0.5f, $"{s.Name}: the panel runs outside the body ({host})");
            switch (placement)
            {
                case PanelPlacement.Sheet:     // C-Queue-Compact: the window's width, 8 under the title bar, down over the mini bar
                    Assert.Equal(s.Width, host.W, 0.5f);
                    Assert.Equal(title.Y + title.H + rule.Inset, host.Y, 0.5f);
                    Assert.Equal(s.Height, host.Y + host.H, 0.5f);
                    break;
                case PanelPlacement.Float:     // C-Queue-Small: 340, 8 from the right edge, the title bar and the bar; the page keeps its width
                    Assert.Equal(rule.FloatWidth, host.W, 0.5f);
                    Assert.Equal(s.Width - rule.Inset, host.X + host.W, 0.5f);
                    Assert.Equal(bar.Y - rule.Inset, host.Y + host.H, 0.5f);
                    Assert.Equal(s.Width, card.X + card.W, 0.5f);
                    break;
                default:                       // docked: the page ends where the panel's 8-DIP gap begins
                    Assert.True(card.X + card.W + Shell.FrameRules.RailGapW <= host.X + 0.5f, $"{s.Name}: the content card runs under the docked panel");
                    Assert.Equal(s.Width, host.X + host.W, 0.5f);
                    Assert.Equal(bar.Y, host.Y + host.H, 0.5f);
                    Assert.Equal(MathF.Max(Shell.Ui.RailWidth.Peek(), rule.Rule.FloorFor(s.Width)), host.W, 0.5f);
                    break;
            }
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            shell.RunFrames(30);
        }
    });

    [Theory, MemberData(nameof(FrameSizes))]
    public void Two_panels_dock_side_by_side_only_at_huge(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        try
        {
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.Lyrics);
            Shell.PanelHost.Shared.Toggle(Shell.RailMode.Queue);
            shell.RunFrames(40);
            bool two = ClassicPanelTiers.Of(tiers).Rule.DocksTwo;
            Assert.Equal(s.Window >= WidthTier.Huge, two);
            Assert.Equal(two, Shell.PanelHost.Shared.SecondOpenNow());
            if (!two)
            {
                // One panel shows where one docks: the queue's door switched the panel to the queue.
                Assert.Equal(Shell.RailMode.Queue, Shell.Ui.Mode.Peek());
                return;
            }
            Assert.Equal(Shell.RailMode.Lyrics, Shell.Ui.Mode.Peek());   // Lyrics + Queue (C-Shell-Huge)
            var scene = shell.Host.Scene;
            var primary = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.HostKey));
            var second = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SecondHostKey));
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            float floor = ClassicPanelTiers.Huge.Rule.FloorFor(s.Width);
            Assert.Equal(floor, second.W, 0.5f);
            Assert.True(primary.W >= floor - 0.5f, $"{s.Name}: the primary pane is {primary.W}, under the floor {floor}");
            Assert.Equal(s.Width, second.X + second.W, 0.5f);
            Assert.Equal(second.X - Shell.FrameRules.RailGapW, primary.X + primary.W, 0.5f);   // 8 between the two panes
            Assert.True(card.X + card.W + Shell.FrameRules.RailGapW <= primary.X + 0.5f, $"{s.Name}: the content card runs under the panes");
            Assert.True(card.W > 0f, $"{s.Name}: the page keeps a width beside two panes");
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            Shell.PanelHost.Shared.CloseQueuePane();
            shell.RunFrames(30);
        }
    });

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_now_playing_sheet_carries_its_header_and_the_devices_door(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        if (ClassicPanelTiers.Of(tiers).Rule.Placement != PanelPlacement.Sheet) return;   // the sheet is Compact's
        try
        {
            Play("spotify:track:tr40", fromMs: 20_000);
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.NowPlaying);
            shell.RunFrames(60);
            var scene = shell.Host.Scene;

            // The header: 48 tall, across the sheet less its 8 insets, the chevron first and the "⋯" last, the source between them.
            var header = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetHeaderKey));
            var close = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetCloseKey));
            var source = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetSourceKey));
            var more = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetMoreKey));
            Assert.Equal(ClassicPanelTiers.SheetHeaderH, header.H, 0.5f);
            Assert.True(header.X >= -0.5f && header.X + header.W <= s.Width + 0.5f, $"{s.Name}: the header leaves the window ({header})");
            Assert.Equal(header.X + ClassicPanelTiers.SheetHeaderInset, close.X, 0.5f);
            Assert.Equal(header.X + header.W - ClassicPanelTiers.SheetHeaderInset, more.X + more.W, 0.5f);
            Assert.True(close.X + close.W <= source.X + 0.5f && source.X + source.W <= more.X + 0.5f, $"{s.Name}: close {close}, source {source}, more {more}");
            Assert.True(source.W > 0f, $"{s.Name}: the source column has a width ({source})");
            AssertTargetsAtLeast40(shell, Node(shell, ClassicPanelTiers.SheetHeaderKey), s.Name);

            // The doors: Lyrics, Devices, Queue, three equal 48-tall columns; Devices is the middle one.
            var doors = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetDoorsKey));
            var devices = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetDevicesKey));
            float share = (doors.W - 2f * ClassicPanelTiers.SheetDoorInset - 2f * ClassicPanelTiers.SheetDoorGap) / 3f;
            Assert.Equal(share, devices.W, 0.5f);
            Assert.Equal(ClassicPanelTiers.SheetDoorH, devices.H, 0.5f);
            Assert.Equal(doors.X + ClassicPanelTiers.SheetDoorInset + share + ClassicPanelTiers.SheetDoorGap, devices.X, 0.5f);
            AssertTargetsAtLeast40(shell, Node(shell, ClassicPanelTiers.SheetDoorsKey), s.Name);
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            shell.RunFrames(30);
        }
    });

    /// <summary>The sheet's doors: the board's 14 under the transport row (it was 8), the Devices door 48 tall and 8 from its neighbours.</summary>
    [Theory]
    [InlineData("compact 400x860")]
    [InlineData("floor 300x640")]
    public void The_sheets_doors_stand_14_under_the_transport_and_8_apart(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        Assert.Equal(PanelPlacement.Sheet, ClassicPanelTiers.Of(tiers).Rule.Placement);
        try
        {
            Play("spotify:track:tr40", fromMs: 20_000);
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.NowPlaying);
            shell.RunFrames(60);
            var scene = shell.Host.Scene;
            var transport = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetTransportKey));
            var doors = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetDoorsKey));
            var devices = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetDevicesKey));
            Assert.Equal(14f, ClassicPanelTiers.SheetDoorTop);
            Assert.Equal(transport.Y + transport.H, doors.Y, 0.5f);                                  // the row follows the transport directly
            Assert.Equal(doors.Y + ClassicPanelTiers.SheetDoorTop, devices.Y, 0.5f);                 // 14 of air above the doors
            Assert.Equal(ClassicPanelTiers.SheetDoorTop + ClassicPanelTiers.SheetDoorH + ClassicPanelTiers.SheetDoorBottom, doors.H, 0.5f);
            float share = (doors.W - 2f * ClassicPanelTiers.SheetDoorInset - 2f * ClassicPanelTiers.SheetDoorGap) / 3f;
            Assert.Equal(doors.X + ClassicPanelTiers.SheetDoorInset + share + ClassicPanelTiers.SheetDoorGap, devices.X, 0.5f);   // 8 from Lyrics
            Assert.Equal(doors.X + doors.W - ClassicPanelTiers.SheetDoorInset - share - ClassicPanelTiers.SheetDoorGap, devices.X + devices.W, 0.5f);   // and from Queue
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            shell.RunFrames(30);
        }
    });

    /// <summary>The toast's "Choose device" (and the tray's Devices) bump <c>Playback.DevicePickerRequest</c>; at Compact the mini bar has no Devices
    /// button, so the sheet answers: from closed (on the lyrics body, the shell's default, and on the now-playing body), from the sheet open on
    /// Lyrics, and with the now-playing sheet already open. The picker is the door's (its menu is keyed "devices:"); one request opens exactly one.</summary>
    [Theory]
    [InlineData("compact 400x860", Shell.RailMode.Lyrics, false)]
    [InlineData("compact 400x860", Shell.RailMode.NowPlaying, false)]
    [InlineData("compact 400x860", Shell.RailMode.Lyrics, true)]
    [InlineData("compact 400x860", Shell.RailMode.NowPlaying, true)]
    [InlineData("floor 300x640", Shell.RailMode.Lyrics, false)]
    [InlineData("floor 300x640", Shell.RailMode.NowPlaying, false)]
    [InlineData("floor 300x640", Shell.RailMode.Lyrics, true)]
    [InlineData("floor 300x640", Shell.RailMode.NowPlaying, true)]
    public void A_device_picker_request_opens_the_sheet_and_its_devices_picker(string size, Shell.RailMode before, bool startClosed) => AssertPage(Home, size, (shell, tiers) =>
    {
        Assert.Equal(PanelPlacement.Sheet, ClassicPanelTiers.Of(tiers).Rule.Placement);
        try
        {
            Play("spotify:track:tr40", fromMs: 20_000);
            Shell.PanelHost.Shared.SetMode(before);
            shell.RunFrames(60);
            Assert.True(Shell.Ui.RailOpen.Peek(), $"{size}: the sheet is open on {before}");
            if (startClosed)
            {
                Shell.PanelHost.Shared.Close();
                shell.RunFrames(40);
                Assert.False(Shell.Ui.RailOpen.Peek(), $"{size}: the sheet is closed");
            }
            Assert.Empty(shell.FindPrefix("devices:"));

            Playback.RequestDevicePicker();
            shell.RunFrames(90);

            Assert.True(Shell.Ui.RailOpen.Peek(), $"{size}: the request opens the sheet");
            Assert.Equal(Shell.RailMode.NowPlaying, Shell.Ui.Mode.Peek());
            Assert.Equal(PanelPlacement.Sheet, Shell.Ui.PanelPlacement.Peek());
            var picker = shell.FindPrefix("devices:");
            Assert.True(picker.Count > 0, $"{size}: the Devices picker is open (from {before}{(startClosed ? ", closed" : "")})");
            var door = shell.Host.Scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SheetDevicesKey));
            Assert.True(door.W > 0f && door.Y + door.H <= Sizes[size].Height + 0.5f, $"{size}: the Devices door stands in the window ({door})");
            int count = picker.Count;

            // A second request while the picker is up opens no second one.
            Playback.RequestDevicePicker();
            shell.RunFrames(40);
            Assert.Equal(count, shell.FindPrefix("devices:").Count);
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            shell.RunFrames(30);
        }
    });

    /// <summary>Every focusable, visible node under <paramref name="root"/> is at least 40 x 40 (the Compact target floor).</summary>
    static void AssertTargetsAtLeast40(Shell.HeadlessShell shell, NodeHandle root, string sizeName)
    {
        var scene = shell.Host.Scene;
        int targets = 0;
        foreach (var n in Subtree(scene, root))
        {
            if (n.Equals(root) || scene.InCollapsedSubtree(n)) continue;
            var flags = scene.Flags(n);
            if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
            var r = scene.AbsoluteRect(n);
            if (r.W <= 0f || r.H <= 0f) continue;
            targets++;
            Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{sizeName}: a Compact target is {r.W} x {r.H}, under 40 x 40");
        }
        Assert.True(targets >= 2, $"{sizeName}: the region offers its targets (found {targets})");
    }
}
