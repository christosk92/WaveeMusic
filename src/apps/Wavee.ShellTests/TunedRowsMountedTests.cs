// ── Wavee.ShellTests/TunedRowsMountedTests.cs — the tuned Classic track rows at rest (themes plan 6.6.3 L1-L4, CT5c) ─────────────
//
// What the tuned canvas fixes about a track row at REST, in BOTH row styles, on the real mounted shell under --fake:
//
//   L1  the versions chevron shows only on a song with versions (al2's first rows carry a music video / an alternate; the rest
//       keep the lane empty)
//   L2  the heart lane shows a filled heart only on a liked row (every Liked Songs row; al14's unliked rows show nothing), the
//       outline and the "…" are hover reveals (rest 0, hover 1)
//       — and on the row that holds the list's focus, so a keyboard user sees them too
//   L3  no lane carries a film glyph: the trailing lanes are the "…" and the chevron only
//   L4  a withheld row states its reason in the duration lane (al14's waterfall rows are not out yet: their date, not a clock)

using FluentGpu.Foundation;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Scene;
using Wavee;
using Wavee.Theming;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class TunedRowsMountedTests(ShellHost host)
{
    static readonly Shell.Route s_album = Shell.For(EntityUri.Parse("spotify:album:al2"), "Album");
    static readonly Shell.Route s_waterfall = Shell.For(EntityUri.Parse("spotify:album:al14"), "Album");

    static void WithRowStyle(Shell.HeadlessShell shell, int rowStyle, Action body)
    {
        var before = ClassicOptions.RowStyle();
        _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.RowStyleQualifiedId, (TrackRowStyle)rowStyle));
        try { body(); }
        finally
        {
            _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.RowStyleQualifiedId, before));
            shell.RunFrames(30);
        }
    }

    static void Open(Shell.HeadlessShell shell, Shell.Route route, string what)
    {
        Shell.GoTo(route);
        Until(shell, () => shell.FindPrefix("row").Count(k => k.Key == "row") > 2 && shell.FindPrefix("row-pill").Count > 0, what);
        shell.RunFrames(60);
    }

    /// <summary>The ROW cells of one lane on the page on screen: the lane's keyed cells that hold content (the header's cell
    /// of the same key is an empty box with no child), under no hidden or transparent ancestor (a kept-alive page — Home's
    /// eager rows, the page before — stays mounted).</summary>
    static List<NodeHandle> RowCells(Shell.HeadlessShell shell, string key)
    {
        var scene = shell.Host.Scene;
        return shell.FindPrefix(key).Where(k => k.Key == key && !scene.FirstChild(k.Node).IsNull && OnScreen(scene, k.Node))
            .Select(k => scene.FirstChild(k.Node)).ToList();
    }

    static bool OnScreen(SceneStore scene, NodeHandle cell)
    {
        for (var n = cell; !n.IsNull; n = scene.Parent(n))
            if (!Shown(scene, n) || scene.Paint(n).Opacity <= 0f) return false;
        return true;
    }

    static bool Shown(SceneStore scene, NodeHandle node) => (scene.Flags(node) & NodeFlags.Visible) != 0;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_versions_chevron_shows_only_on_songs_with_versions(int rowStyle)
        => host.Mounted(shell => WithRowStyle(shell, rowStyle, () =>
        {
            Open(shell, s_album, "the album's rows");
            var scene = shell.Host.Scene;
            var chevrons = RowCells(shell, Track.CellKey.Expand);
            Assert.NotEmpty(chevrons);
            Assert.Contains(chevrons, c => Shown(scene, c));    // al2's drawer rows: a music video / an alternate
            Assert.Contains(chevrons, c => !Shown(scene, c));   // a plain song keeps the lane empty
        }), widthDip: 1600f, heightDip: 900f);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void At_rest_a_row_shows_only_a_liked_heart_and_the_more_button_is_a_hover_reveal(int rowStyle)
        => host.Mounted(shell => WithRowStyle(shell, rowStyle, () =>
        {
            var scene = shell.Host.Scene;

            // al14's rows are its own (an album from the pool is the Liked set, every row liked).
            Open(shell, s_waterfall, "the waterfall album's rows");
            var hearts = RowCells(shell, Track.CellKey.Heart);
            Assert.NotEmpty(hearts);
            Assert.All(hearts, h => Assert.Equal(1f, scene.Paint(h).HoverOpacity));
            Assert.All(hearts, h => Assert.Equal(0f, scene.Paint(h).Opacity));   // an unliked song: no outline at rest
            var more = RowCells(shell, Track.CellKey.More);
            Assert.NotEmpty(more);
            Assert.All(more, m =>
            {
                Assert.Equal(0f, scene.Paint(m).Opacity);
                Assert.Equal(1f, scene.Paint(m).HoverOpacity);
            });

            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            Until(shell, () => Shell.Shown.Peek().Kind == Shell.RouteKind.Liked && RowCells(shell, Track.CellKey.Heart).Count > 0,
                  "the liked rows");
            shell.RunFrames(60);
            var liked = RowCells(shell, Track.CellKey.Heart);
            Assert.NotEmpty(liked);
            Assert.All(liked, h => Assert.Equal(1f, scene.Paint(h).Opacity));   // every liked song keeps its filled heart
        }), widthDip: 1600f, heightDip: 900f);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_focused_row_shows_its_outline_heart_and_more_button_without_hover(int rowStyle)
        => host.Mounted(shell => WithRowStyle(shell, rowStyle, () =>
        {
            Open(shell, s_waterfall, "the waterfall album's rows");
            var scene = shell.Host.Scene;
            var first = shell.FindPrefix("row").Where(k => k.Key == "row" && OnScreen(scene, k.Node))
                .Select(k => scene.PresentedAbsoluteRect(k.Node)).Where(r => r.H > 0f).OrderBy(r => r.Y).First();
            // Click the first row (the list's roving focus lands on it), take the pointer away, then arrow to the second.
            var p = new Point2(first.X + first.W / 2f, first.Y + first.H / 2f);
            shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0));
            shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1000));
            shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1010));
            shell.RunFrames(2);
            shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(4f, 600f), 0, 0));
            shell.RunFrames(2);
            Key(shell, Keys.Down);
            shell.RunFrames(30);

            // al14's rows are unliked: only the focused row offers its outline heart and its "…" at rest.
            Assert.Single(RowCells(shell, Track.CellKey.Heart), h => scene.Paint(h).Opacity == 1f);
            Assert.Single(RowCells(shell, Track.CellKey.More), m => scene.Paint(m).Opacity == 1f);
        }), widthDip: 1600f, heightDip: 900f);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_song_that_is_not_out_yet_states_its_date_in_the_duration_lane(int rowStyle)
        => host.Mounted(shell => WithRowStyle(shell, rowStyle, () =>
        {
            Open(shell, s_waterfall, "the waterfall album's rows");
            var scene = shell.Host.Scene;
            var texts = shell.FindPrefix(Track.CellKey.Duration).Where(k => k.Key == Track.CellKey.Duration && OnScreen(scene, k.Node))
                .Select(k => TextUnder(scene, k.Node)).Where(t => t.Length > 0).ToList();
            Assert.Contains(texts, t => t.Contains(':'));                                // the released rows: a clock
            Assert.Contains(texts, t => !t.Contains(':') && t != Track.Format.Dash);     // the pending rows: when
        }), widthDip: 1600f, heightDip: 900f);

    static string TextUnder(SceneStore scene, NodeHandle node)
    {
        foreach (var n in Subtree(scene, node))
            if (Shown(scene, n) && scene.Paint(n).VisualKind == VisualKind.Text && !scene.Paint(n).Text.IsEmpty)
                return Entities.Strings.Resolve(scene.Paint(n).Text);
        return "";
    }
}
