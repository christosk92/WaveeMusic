// ── Wavee.ShellTests/ShellSmokeTests.cs — the headless shell host boots like the app and mounts the real frame ─────
//
// The host's own guarantees (themes plan §2.1, H0): the boot leaves the GUI's `boot.*` marks in the GUI's order, the
// frame mounts at a wide and a narrow size, a route navigates, and nothing reaches the network.

using FluentGpu.Scene;
using Wavee;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ShellSmokeTests(ShellHost host)
{
    [Fact]
    public void The_boot_leaves_the_gui_marks_in_the_gui_order()
    {
        string[] expected = ["boot.platform", "boot.entities", "boot.playback", "boot.pages", "boot.documents"];
        var marks = new List<string>();
        foreach (var entry in Log.Snapshot())
            if (entry.Category == "app" && entry.EventId.StartsWith("boot.", StringComparison.Ordinal) && !marks.Contains(entry.EventId))
                marks.Add(entry.EventId);
        Assert.Equal(expected, marks);
    }

    [Theory]
    [InlineData(1180f, 760f)]
    [InlineData(400f, 860f)]
    public void The_frame_mounts(float width, float height)
        => host.Mounted(shell =>
        {
            shell.RunFrames(10);
            Assert.False(shell.Host.Scene.Root.IsNull);
            Assert.NotEmpty(shell.FindPrefix("sidebar."));
        }, width, height);

    [Fact]
    public void A_route_navigates()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            shell.RunFrames(20);
            Assert.Equal(Shell.RouteKind.Liked, Shell.Current.Peek().Kind);
            Assert.Equal(Shell.RouteKind.Liked, Shell.Shown.Peek().Kind);
        });

    [Fact]
    public void Nothing_reaches_the_network()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(20);
            ShellHost.AssertNoNetwork();
        });
}
