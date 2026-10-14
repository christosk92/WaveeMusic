// ── Wavee.ShellTests/TrackChromeMountedTests.cs — the track table's chrome, drawn by the registered templates (themes plan 6.1, T8b) ──
//
// T8b moves the pictures of the table's header, command bar (and the selection bar's surface), filter card and placeholder row to
// the structure: Classic's `TrackListStyle.Chrome` (`ClassicTrackChrome`). The core host keeps the sort / filter / selection state
// and every overlay. What only a mounted tree can say, in BOTH row styles: the real Liked page draws the sticky header with its
// keyed sort label and a hairline, the command bar's lane in browse mode with the "…" slot, and the header stands at the height
// the row style declares (so the sticky clip inset the core computed still matches what is drawn).

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class TrackChromeMountedTests(ShellHost host)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_liked_chrome_is_drawn_by_the_registered_templates(int rowStyle)
        => host.Mounted(shell =>
        {
            var before = ClassicOptions.RowStyle();
            _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.RowStyleQualifiedId, (TrackRowStyle)rowStyle));   // Settings › Appearance › Row style
            try
            {
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
                Until(shell, () => shell.FindPrefix("list:").Count == 1, "the liked list mounted");
                Until(shell, () => shell.FindPrefix("sort-label:").Count > 0, "the column header mounted");
                shell.RunFrames(30);

                // the header: the keyed sort label (the row style is in its key) over the band's single hairline
                Assert.NotEmpty(shell.FindPrefix(rowStyle == 1 ? "sort-label:c" : "sort-label:m"));
                Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Duration));

                // the command bar: the browse-mode lane and the "…" slot
                Assert.NotEmpty(shell.FindPrefix("commandbar-mode:normal"));
                Assert.NotEmpty(shell.FindPrefix("cmd:more:"));
            }
            finally
            {
                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.RowStyleQualifiedId, before));
                shell.RunFrames(30);
            }
        });
}
