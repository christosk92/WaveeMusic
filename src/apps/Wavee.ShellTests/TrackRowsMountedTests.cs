// ── Wavee.ShellTests/TrackRowsMountedTests.cs — the track table's rows, drawn by the registered templates (themes plan 6.1, T8a) ──
//
// The track table is a core host (`Track.TableHost`: the rows, the presentation memo, the drawer, selection, drag, keys and the
// context menu) and the structure's rows since T8a: Classic registers a `TrackListStyle` under `StyleKeys.TrackTable` holding a
// Modern and a Classic template pair (the bound grid of lanes, the row skin). What only a mounted tree can say, in BOTH row
// styles: the real Liked page's realized rows carry the skin's pill and divider, the grid's number and title lanes, and stand at
// the row height the style's ladder gives. The re-keying on the setting, the density and the hidden art are
// `LiveOptionBehaviourTests`.

using FluentGpu.Foundation;
using Wavee;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class TrackRowsMountedTests(ShellHost host)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_liked_rows_are_drawn_by_the_registered_templates(int rowStyle)
        => host.Mounted(shell =>
        {
            int before = Platform.Settings.Get(Platform.Keys.TrackRowStyle);
            Prefs.Appearance.Set(Platform.Keys.TrackRowStyle, rowStyle);   // Settings › Appearance › Track rows
            try
            {
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
                Until(shell, () => shell.FindPrefix("list:").Count == 1, "the liked list mounted");
                Until(shell, () => shell.FindPrefix("row-pill").Count > 0, "a row skin mounted");
                shell.RunFrames(30);

                Assert.NotEmpty(shell.FindPrefix("row-pill"));
                Assert.NotEmpty(shell.FindPrefix("row-divider"));
                Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Num));
                Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Title));

                var rows = Track.RowMetrics.Rows();   // the style's density
                float expected = rowStyle == 1 ? rows.ClassicRow : rows.ModernRow;
                var row = shell.FindPrefix("row").First(r => r.Key == "row").Node;
                RectF bounds = shell.Host.Scene.Bounds(row);
                Assert.InRange(bounds.H, expected - 1f, expected + 1f);
            }
            finally
            {
                Prefs.Appearance.Set(Platform.Keys.TrackRowStyle, before);
                shell.RunFrames(30);
            }
        });
}
