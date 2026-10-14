// ── Wavee.ShellTests/ListSurfacesMountedTests.cs — the list surfaces, drawn by the registered styles (themes plan 6.1, T8c) ─────
//
// The eager track row, the podcast episode row and the media surface are core hosts that keep their hooks, state and decisions and draw
// through the structure's styles since T8c (`StyleKeys.EagerTrackRow`, `EpisodeRow`, `Surface`; the artist chart and the drawer have theirs
// too, and need a catalogue to mount: their registration is `Wavee.Tests\Theming\ListSurfaceStylesTests`). What only a mounted tree
// can say, under the installed (Classic) registry:
//
//   eager row    the one-shot grid stands in its lanes (keyed cells: number, heart, title, duration, the "..." lane) inside a hover
//                container that is at least the row's height; a host with no registry provides none and draws nothing
//   surface      a live row and a seed row of the same shape stand at the SAME height (a hydrating card never re-measures its list), and
//                the seed is core: it draws with no registry at all
//   episode row  the cold row (the seed the reveal gate shows while the episode's identity is not there) stands at the row's real
//                geometry (art plus the two vertical paddings), and a host with no style draws nothing

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ListSurfacesMountedTests(ShellHost host)
{
    const float RowH = 40f;

    static Element Under(ViewRegistry registry, Element content) => Ctx.Provide(ViewRegistry.Context, registry, content);

    static Element Hosted(string key, float width, Element content)
        => new BoxEl { Key = key, Direction = 1, Width = width, Children = [content] };

    static Element EagerRow()
        => Hosted("eager:host", 600f, Track.EagerRow(default, 0, Track.EmbeddedColumns, Track.EmbeddedTracks, RowH,
                                                     static () => { }, new Track.EagerRowOptions()));

    // ── the eager row ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_eager_row_stands_in_its_lanes_inside_a_container_of_the_rows_height()
        => host.Run(() =>
        {
            using var shell = Shell.Headless.Mount(() => Under(ThemeHost.Registry, EagerRow()), 800f, 300f, 1f);
            shell.RunFrames(5);

            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Num));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Heart));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Title));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Duration));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.More));
            Assert.True(shell.Host.Scene.Bounds(Node(shell, "eager:host")).H >= RowH - 0.5f, "the hover container holds the row's height");
            ShellHost.AssertNoNetwork();
        });

    [Fact]
    public void An_eager_row_with_no_registry_draws_nothing()
        => host.Run(() =>
        {
            using var shell = Shell.Headless.Mount(() => Under(ViewRegistry.Empty, EagerRow()), 800f, 300f, 1f);
            shell.RunFrames(5);

            Assert.Empty(shell.FindPrefix(Track.CellKey.Num));
            Assert.Empty(shell.FindPrefix(Track.CellKey.Title));
            ShellHost.AssertNoNetwork();
        });

    // ── the media surface ────────────────────────────────────────────────────────────────────────────────────────────────

    static Element LiveRow()
        => Hosted("surface:live", 360f, Controls.Surface(new Controls.CardData("spotify:track:fixture", "A title", null, null, static () => { }),
                                                         Shape.Row()));

    static Element SeedRow()
        => Hosted("surface:seed", 360f, Controls.Surface(Controls.CardData.Seed, Shape.Row()));

    [Fact]
    public void A_live_surface_row_and_its_seed_stand_at_the_same_height()
        => host.Run(() =>
        {
            using var shell = Shell.Headless.Mount(
                () => Under(ThemeHost.Registry, new BoxEl { Direction = 1, Children = [LiveRow(), SeedRow()] }), 800f, 400f, 1f);
            shell.RunFrames(5);

            float live = shell.Host.Scene.Bounds(Node(shell, "surface:live")).H;
            float seed = shell.Host.Scene.Bounds(Node(shell, "surface:seed")).H;
            Assert.True(live >= 64f - 0.5f, "a media row is never under its 64 floor");
            Assert.Equal(seed, live, 0.5f);
            ShellHost.AssertNoNetwork();
        });

    [Fact]
    public void A_surface_with_no_registry_draws_no_live_row_but_the_seed_is_core()
        => host.Run(() =>
        {
            using var shell = Shell.Headless.Mount(
                () => Under(ViewRegistry.Empty, new BoxEl { Direction = 1, Children = [LiveRow(), SeedRow()] }), 800f, 400f, 1f);
            shell.RunFrames(5);

            Assert.True(shell.Host.Scene.Bounds(Node(shell, "surface:live")).H < 1f, "the live row needs the structure's templates");
            Assert.True(shell.Host.Scene.Bounds(Node(shell, "surface:seed")).H >= 64f - 0.5f, "the seed face is the core's geometry");
            ShellHost.AssertNoNetwork();
        });

    // ── the episode row ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_cold_episode_row_stands_at_the_rows_geometry_and_no_style_draws_nothing()
        => host.Run(() =>
        {
            Assert.True(ThemeHost.Registry.TryGetStyle(StyleKeys.EpisodeRow, out var style), "Classic registers the episode row");
            Element Row(EpisodeRowStyle? s)
                => Hosted("episode:host", 600f, Episode.ReaderRow(s, Episode.Fixed(default), new Episode.RowContext
                {
                    Tone = static () => default, Play = static _ => { },
                }, narrow: false));

            using (var shell = Shell.Headless.Mount(() => Row(style), 800f, 300f, 1f))
            {
                shell.RunFrames(5);
                Assert.True(shell.Host.Scene.Bounds(Node(shell, "episode:host")).H >= Episode.RowArt + 2f * Episode.RowPadY - 0.5f,
                    "the seed row is the art plus the row's own vertical padding");
            }

            using (var shell = Shell.Headless.Mount(() => Row(null), 800f, 300f, 1f))
            {
                shell.RunFrames(5);
                Assert.True(shell.Host.Scene.Bounds(Node(shell, "episode:host")).H < 1f);
            }
            ShellHost.AssertNoNetwork();
        });
}
