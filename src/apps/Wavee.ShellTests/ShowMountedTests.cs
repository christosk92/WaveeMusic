// ── Wavee.ShellTests/ShowMountedTests.cs — the show page, drawn by the registered style (themes plan 6.1, T9c) ──────────────────
//
// The podcast show page is the shared detail frame (T9a) with a rail of podcast slots and the episode reader. Since T9c its pictures are the
// structure's `ShowReaderStyle` (`StyleKeys.ShowReader`); the core hosts keep the model, the snapshot, the stamps and the verbs
// (`Wavee.Tests\ShowReaderTests` and friends pin the pure decisions; `Wavee.Tests\ShowContractTests` the registration). What only a mounted
// tree can say, under the installed (Classic) registry and the --fake seed (sh0: the returning serial):
//
//   toolbar    the sticky word rail draws ONE row: its right group stands inside the plane the core reserves for it
//   date rail  the year strip's column is always the core's width, and the sticky month's plate is always mounted (a presence flip would relayout)
//   rail       the CTA's keyed satellites stand in the rail: share and the "..." (the bell is disabled with its reason)

using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ShowMountedTests(ShellHost host)
{
    /// <summary>The fake seed's returning serial: numbered episodes 14 to 1, 8 played and one in progress.</summary>
    const string SerialShow = "spotify:show:sh0";

    [Fact]
    public void Classic_registers_the_show_style()
        => host.Run(() => Assert.True(ThemeHost.Registry.TryGetStyle(StyleKeys.ShowReader, out var style) && style is not null));

    [Fact]
    public void The_real_shell_draws_the_toolbar_the_date_rail_and_the_satellites()
        => host.Mounted(shell =>
        {
            Shell.GoTo(Shell.For(EntityUri.Parse(SerialShow), "Show"));
            UntilBackground(shell, () => !shell.Find("tb:right").IsNull, "the sticky toolbar");
            shell.RunFrames(30);

            // ONE row at every rung: the right group stands inside the plane the core reserves for the rail
            var group = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, "tb:right"));
            Assert.True(group.H <= ShowReaderRules.RailExtent + 0.5f, "the toolbar is one row");

            // the strip's column is reserved whatever the view holds
            var years = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, "rd:years"));
            Assert.Equal(ShowReaderRules.YearStripWidth, years.W, 0.5f);
            Assert.False(shell.Find("rd:sticky-month").IsNull, "the pinned month is always mounted");

            // the CTA's satellites are keyed by the rail
            Assert.False(shell.Find("sat:share").IsNull, "share");
            Assert.False(shell.Find("sat:bell").IsNull, "the bell, disabled with its reason");
        }, widthDip: 1600f, heightDip: 900f);
}
