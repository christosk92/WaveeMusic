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
        // The wordmark (A9): drawn whole from Small up, never at Compact, where Back leads on the gutter.
        Until(shell, () => bar.Wordmark == !shell.Find(ZuneFrameRules.WordmarkKey).IsNull,
            $"{size}: the wordmark {(bar.Wordmark ? "drawn" : "gone")} (a crossing's exit fade settles first)");
        if (!bar.Wordmark) Assert.Equal(frame.Gutter, scene.AbsoluteRect(Node(shell, ZuneFrameRules.BackKey)).X, 1f);

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

    // ── the page column (the T1 audit: A1, A2) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE column: the band's first pivot, the page's content and row 2's leading edge start at max(gutter, (W - 2400) / 2) (48 at
    /// 1440, 80 at 2560, 720 at 3840); the page's content and row 2's last action end on its mirror. The play bar is window chrome: its
    /// cover and its last glyph sit on the frame's gutter, which is the column's edge until the cap holds (at 1440 all three right edges
    /// agree).</summary>
    [Theory]
    [InlineData(1440f, 960f, true)]
    [InlineData(2560f, 1440f, false)]
    [InlineData(3840f, 2160f, false)]
    public void The_band_the_page_and_the_bar_share_one_column(float width, float height, bool barOnColumn) => Mounted(shell =>
    {
        Until(shell, () => shell.FindPrefix(Home.ReadyKeyPrefix).Count > 0, "Home on screen");
        shell.RunFrames(60);
        var scene = shell.Host.Scene;
        float gutter = ZuneTiers.FrameOf(Tiers).Gutter;
        var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
        float lead = ZuneFrameRules.ColumnLead(content.W, gutter);
        float left = content.X + lead, right = content.X + content.W - lead;

        // The band's lead x is the page's content x.
        Assert.Equal(left, scene.AbsoluteRect(Node(shell, ZuneBandRules.PivotKeyPrefix + "home")).X, 1f);
        var column = scene.AbsoluteRect(Node(shell, ZuneHomeRules.ColumnKey));
        Assert.Equal(left, column.X + gutter, 1f);
        Assert.Equal(right, column.X + column.W - gutter, 1f);
        Assert.True(column.W - 2f * gutter <= ZuneFrameRules.PageCap + 0.5f, $"{width}: the page's content is {column.W - 2f * gutter}, over the cap");

        // The bar: the last tool's glyph on the frame's gutter (A2), the page's right edge until the cap holds.
        Play(PlayerTrack, fromMs: 20_000);
        UntilBackground(shell, () => !shell.Find(ZunePlayerLayout.RightKey).IsNull, "the bar's tools");
        shell.RunFrames(40);
        var rec = ZunePlayerTiers.BarOf(Tiers);
        var look = ZunePlayerTiers.Look(ExpectedLevel(width, rec));
        var tools = scene.AbsoluteRect(Node(shell, ZunePlayerLayout.RightKey));
        float lastGlyph = tools.X + tools.W - (look.Button - look.Glyph) / 2f;
        Assert.Equal(width - gutter, lastGlyph, 1f);
        Assert.Equal(gutter, scene.AbsoluteRect(Node(shell, ZunePlayerLayout.LeftKey)).X, 1f);
        if (barOnColumn) Assert.Equal(right, lastGlyph, 1f);

        // Row 2 on a detail page (al2: its title, views and play / shuffle): it leads on the column and its last action ends on its edge.
        Shell.GoTo(Shell.For(EntityUri.Parse("spotify:album:al2"), "Album"));
        UntilBackground(shell, () => !shell.Find(ZuneBandRules.ActionKeyPrefix + "play").IsNull, "the album's actions in the band");
        shell.RunFrames(60);
        var row2 = scene.AbsoluteRect(Node(shell, ZuneBandRules.Row2Key));
        Assert.Equal(left, row2.X, 1f);
        float actionsEnd = 0f;
        foreach (var (node, _) in shell.FindPrefix(ZuneBandRules.ActionKeyPrefix))
        {
            var r = scene.AbsoluteRect(node);
            if (r.W > 0f) actionsEnd = MathF.Max(actionsEnd, r.X + r.W);
        }
        Assert.Equal(right, actionsEnd, 1f);
        Assert.Equal(right, row2.X + row2.W, 1f);
        if (barOnColumn) Assert.Equal(lastGlyph, actionsEnd, 1f);

        Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        shell.RunFrames(30);
    }, width, height);
}
