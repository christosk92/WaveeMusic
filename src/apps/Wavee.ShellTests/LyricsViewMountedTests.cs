// ── Wavee.ShellTests/LyricsViewMountedTests.cs — the lyrics surface, mounted (themes plan 6.1, T7b) ─────────────────────────────
//
// The lyrics reading surface is the structure's since T7b: the layer stack, the overlays and the stage's column are `Lyrics.View`, the list
// and its rows the `Lyrics.Lines` style. The core host (`Lyrics.ViewCore`) keeps the document, the clock, the follow, the cascade, the
// depth-of-field ramp and the word wipe (`Wavee.Tests\LyricsModelTests.cs`, `LyricsRulesTests.cs` pin the pure ones;
// `LiveOptionBehaviourTests` pins the blur on the rail's rows and the stage's rule). What only a mounted tree can say:
//
//   rail         the rail's lyrics body (`lyrics:rail`) draws one keyed row per line of the fixture's document, through the style
//   stage        the stage's lyrics pane (`lyrics:stage:<size>`) draws the same rows

using FluentGpu.Foundation;
using Wavee;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class LyricsViewMountedTests(ShellHost host)
{
    /// <summary>The nodes of the document's rows (their keys start with <c>ll</c>) that live under <paramref name="root"/>.</summary>
    static List<NodeHandle> RowsUnder(Shell.HeadlessShell shell, NodeHandle root)
    {
        var inside = new HashSet<NodeHandle>(Subtree(shell.Host.Scene, root));
        var rows = new List<NodeHandle>();
        foreach (var (node, _) in shell.FindPrefix("ll"))
            if (inside.Contains(node)) rows.Add(node);
        return rows;
    }

    [Fact]
    public void The_rail_draws_one_keyed_row_per_line_through_the_style()
        => host.Mounted(shell =>
        {
            try
            {
                // Past the fixture's first line (Lyrics.Fixtures: 4 s), so a line is in focus.
                Play("spotify:track:tr40", fromMs: 20_000);
                if (!(Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Lyrics))
                    Shell.Ui.Toggle(Shell.RailMode.Lyrics);
                Until(shell, () => !shell.Find("lyrics:rail").IsNull && RowsUnder(shell, shell.Find("lyrics:rail")).Count > 1,
                    "the rail's lyrics rows were realized", 900);
                shell.RunFrames(60);

                var rows = RowsUnder(shell, Node(shell, "lyrics:rail"));
                Assert.True(rows.Count > 1, "rows: " + rows.Count);
                foreach (var row in rows) Assert.True(shell.Host.Scene.IsLive(row));
            }
            finally
            {
                if (Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Lyrics) Shell.Ui.Toggle(Shell.RailMode.Lyrics);
                shell.RunFrames(30);
            }
        });

    [Fact]
    public void The_stage_draws_the_same_rows_in_its_reading_column()
        => host.Mounted(shell =>
        {
            try
            {
                Prefs.Stage.SetMode((int)Stage.Mode.Lyrics);
                Play("spotify:track:tr40", fromMs: 20_000);
                Stage.Open(null, "test");
                Until(shell, () => shell.FindPrefix("lyrics:stage:").Count == 1, "the stage's lyrics column mounted", 900);
                var column = shell.FindPrefix("lyrics:stage:")[0].Node;
                Until(shell, () => RowsUnder(shell, column).Count > 1, "the stage's lyrics rows were realized", 900);
                shell.RunFrames(60);

                Assert.True(RowsUnder(shell, column).Count > 1);
            }
            finally
            {
                Stage.Close(null, "test");
                shell.RunFrames(60);
                Prefs.Stage.SetMode(Platform.Keys.StageMode.Default);
            }
        });
}
