// ── Wavee.ShellTests/ZuneTierLayoutTests.Frame.cs — Zune's frame at every tier size and edge (themes plan 3.5, 6.3; P5 ZT1) ─────────
//
// The frame's own records on the mounted app, at every size of the harness (`FrameSizes`), on the facts and the records, never on pixels:
//
//   · the title bar is the record's height (48, 56 at Huge 4K) and draws the record's search form (the magnifier at Compact and Small, the pill
//     from Standard up), the trail everywhere but Compact, the bell on the pill tiers, and never a pane toggle;
//   · the core's allocator answers the same search form (Zune's chrome budgets, ZT1);
//   · the band is drawn at one of its record's two heights, the pins only where the tier shows them, and at Compact every band target is
//     at least 40 x 40;
//   · the floating panel hangs from the band's foot as the band stands now (ZT1's BandH fix), on a page with row 2 and on one without.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class ZuneTierLayoutTests
{
    [Theory, MemberData(nameof(FrameSizes))]
    public void The_title_bar_and_the_band_hold_their_tier_records(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        var scene = shell.Host.Scene;
        var frame = ZuneTiers.FrameOf(tiers);
        var bar = ZuneFrameRules.TitleBarOf(tiers);
        var band = ZuneBandRules.BandOf(tiers);

        // The title bar: the record's height and form.
        Until(shell, () => MathF.Abs(scene.AbsoluteRect(Node(shell, ZuneFrameLayout.TitleBarKey)).H - frame.TitleBarH) < 0.5f,
            $"{size}: the title bar at {frame.TitleBarH}");
        Assert.Equal(bar.Pill, !shell.Find(ZuneFrameRules.PillKey).IsNull);
        Assert.Equal(!bar.Pill, !shell.Find(ZuneFrameRules.SearchIconKey).IsNull);
        Assert.Equal(bar.BellInRow, !shell.Find(ZuneFrameRules.BellKey).IsNull);
        if (!bar.Crumbs) Assert.True(shell.Find(ZuneFrameRules.CrumbsKey).IsNull, $"{size}: no trail at Compact");
        Assert.True(shell.Find(ZuneFrameRules.PaneToggleKey).IsNull, $"{size}: no pane toggle");
        Assert.False(shell.Find("zune-account-host").IsNull, $"{size}: the identity's host");

        // The core's allocation answers the form Zune draws.
        Assert.Equal(bar.Pill ? Shell.MergedSearchMode.Field : Shell.MergedSearchMode.Icon, Shell.ChromeLayout.Peek().SearchMode);

        // The band: one of its record's heights; the pins only where the tier shows them.
        float bandH = scene.AbsoluteRect(Node(shell, ZuneBandRules.BandKey)).H;
        Assert.True(MathF.Abs(bandH - band.ExpandedH) < 0.5f || MathF.Abs(bandH - band.CollapsedH) < 0.5f,
            $"{size}: the band is {bandH}, neither {band.ExpandedH} nor {band.CollapsedH}");
        Assert.Equal(frame.BandH, band.ExpandedH);
        if (!band.Pins) Assert.True(shell.Find(ZuneBandRules.PinsKey).IsNull, $"{size}: no pins at {s.Window}");

        // At Compact every band target is a 40 target.
        if (s.Window == WidthTier.Compact)
        {
            foreach (var (node, key) in shell.FindPrefix(ZuneBandRules.PivotKeyPrefix))
            {
                var r = scene.AbsoluteRect(node);
                if (r.W <= 0f) continue;
                Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{size}: {key} is {r.W} x {r.H}, under 40 x 40");
            }
            foreach (var (node, key) in shell.FindPrefix(ZuneBandRules.ViewKeyPrefix))
            {
                var r = scene.AbsoluteRect(node);
                if (r.W <= 0f) continue;
                Assert.True(r.H >= 40f - 0.5f, $"{size}: {key} is {r.H} tall, under 40");
            }
        }
    });

    [Theory]
    [InlineData(800f, 960f)]     // Small: the panel floats (Z-Frame-Narrow)
    [InlineData(800f, 330f)]     // Short: the panel floats over a 52-high band too
    public void The_floating_panel_hangs_from_the_bands_foot_as_the_band_stands_now(float width, float height) => Mounted(shell =>
    {
        shell.RunFrames(20);
        var scene = shell.Host.Scene;
        float Foot() { var r = scene.AbsoluteRect(Node(shell, ZuneFrameRules.BandSlotKey)); return r.Y + r.H; }
        float PanelTop() => scene.AbsoluteRect(Node(shell, ZuneFrameRules.PanelHostKey)).Y;
        try
        {
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.Queue);
            shell.RunFrames(40);
            Assert.Equal(PanelPlacement.Float, Shell.Ui.PanelPlacement.Peek());

            // A page with row 2: the band's full height.
            Shell.GoTo(new Shell.Route(Shell.RouteKind.LibraryArtists));
            Until(shell, () => !shell.Find(ZuneBandRules.Row2Key).IsNull, "row 2 on a library route");
            shell.RunFrames(40);
            Assert.Equal(Foot(), PanelTop(), 0.5f);

            // A page without row 2 (History: Home and Recents publish row 2 since rounds D and E): the band is row 1 alone (52), and the panel
            // rises with it, never leaving a gap or hiding under the band.
            Shell.GoTo(new Shell.Route(Shell.RouteKind.History));
            Until(shell, () => shell.Find(ZuneBandRules.Row2Key).IsNull, "no row 2 on History");
            shell.RunFrames(40);
            var band = ZuneBandRules.BandOf(ThemeHost.Facts.Peek().Tiers);
            Assert.Equal(band.CollapsedH, scene.AbsoluteRect(Node(shell, ZuneBandRules.BandKey)).H, 0.5f);
            Assert.Equal(Foot(), PanelTop(), 0.5f);
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(30);
        }
    }, width, height);
}
