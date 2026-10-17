// ── Wavee.ShellTests/ZuneTierLayoutTests.Crossing.cs — a Zune tier crossing keeps the frame's parts and animates them (plan 3.3; P5 ZT1) ──
//
// Every window edge of Zune's ladder both ways (640, 1008, 1920, 3200) and the height edge (700), each by the plan 3.3 sequence: a RESIZED
// frame (a maximize, or a composited modal drag that settles) that still lays the OLD tier out, then the first frame that is not resized, where
// the crossing commits. On a library route (so the band carries row 2) it asserts:
//
//   · the resized frame keeps the old tier; the crossing frame commits the new one;
//   · the title bar's slot, the band, its slot, its rows and every pivot are the SAME nodes after the crossing (nothing remounted), and so are
//     the identity's host and the search's host where the form does not change (the 1008 edge swaps the magnifier and the pill under the
//     shared cross-fade, and the allocator's form follows on that frame);
//   · each of them that moved or resized in its parent glides: it, or a box that carries it, holds animation tracks on the crossing frame
//     (the title bar 48 -> 56 and the band 84 -> 112 at 3200, the pill 220 -> 320 -> 400, the band's inset, gap and type).

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
    /// <summary>Each window edge both ways: from, to, the height both sit at.</summary>
    public static TheoryData<float, float, float> WindowCrossings() => new()
    {
        { 400f, 800f, 860f }, { 800f, 400f, 860f },        // 640: Compact <-> Small
        { 800f, 1440f, 960f }, { 1440f, 800f, 960f },      // 1008: Small <-> Standard (the magnifier <-> the pill)
        { 1440f, 2048f, 1114f }, { 2048f, 1440f, 1114f },  // 1920: Standard <-> Huge (the pill 220 <-> 320)
        { 2560f, 3840f, 1440f }, { 3840f, 2560f, 1440f },  // 3200: Huge <-> Huge 4K (the title bar 48 <-> 56, the band 84 <-> 112)
    };

    /// <summary>The height edge both ways: width, from height, to height.</summary>
    public static TheoryData<float, float, float> HeightCrossings() => new()
    {
        { 1440f, 960f, 600f }, { 1440f, 600f, 960f },      // 700 at Standard
        { 2560f, 1440f, 660f }, { 2560f, 660f, 1440f },    // 700 at Huge: the band may collapse only while Short
    };

    static WidthTier ZuneTierAt(float width) => (WidthTier)ZuneTiers.Policy.Window.Resolve(width, -1);

    static HeightTier ZuneHeightAt(float height) => (HeightTier)ZuneTiers.Policy.Height.Resolve(height, -1);

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_window_crossing_keeps_the_frames_parts_and_animates_them(float from, float to, float height)
        => Cross(from, height, to, height, ZuneTierAt(to), ZuneHeightAt(height));

    [Theory, MemberData(nameof(HeightCrossings))]
    public void A_height_crossing_keeps_the_frames_parts(float width, float from, float to)
        => Cross(width, from, width, to, ZuneTierAt(width), ZuneHeightAt(to));

    readonly record struct FramePart(string Name, NodeHandle Node, RectF Local);

    void Cross(float fromW, float fromH, float toW, float toH, WidthTier window, HeightTier tall) => Mounted(shell =>
    {
        var scene = shell.Host.Scene;
        try
        {
            shell.RunFrames(20);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.LibraryArtists));
            Until(shell, () => !shell.Find(ZuneBandRules.Row2Key).IsNull, "row 2 on a library route");
            shell.RunFrames(40);
            var before = Tiers;
            bool pillBefore = ZuneFrameRules.TitleBarOf(before).Pill;

            var parts = new List<FramePart>();
            void Take(string key) { var n = shell.Find(key); if (!n.IsNull) parts.Add(new FramePart(key, n, scene.Bounds(n))); }
            Take(ZuneFrameLayout.TitleBarKey);
            Take(ZuneFrameRules.BandSlotKey);
            Take(ZuneBandRules.BandKey);
            Take(ZuneBandRules.Row1Key);
            Take(ZuneBandRules.Row2Key);
            Take(ZuneBandRules.PivotRunKey);
            Take("zune-account-host");
            foreach (var (node, key) in shell.FindPrefix(ZuneBandRules.PivotKeyPrefix)) parts.Add(new FramePart(key, node, scene.Bounds(node)));
            Assert.True(parts.Count >= 8, "the frame's parts are mounted");
            var search = new List<FramePart>();
            foreach (string key in new[] { "zune-search-pill-host", "zune-search-icon-host", ZuneFrameRules.PillKey })
            {
                var n = shell.Find(key);
                if (!n.IsNull) search.Add(new FramePart(key, n, scene.Bounds(n)));
            }

            // The resized frame: the old tier, laid out at the new size.
            bool narrowing = toW < fromW || toH < fromH;
            shell.Window.Composited = true;
            if (narrowing)
            {
                shell.Window.InModalLoop = true;
                shell.Window.SizedInModalLoop = true;
                shell.Resize(toW, toH);
                shell.Host.Paint(0, keepAlive: true);
                shell.Window.InModalLoop = false;
                shell.Window.SizedInModalLoop = false;
                shell.Host.Paint(0, keepAlive: true);
            }
            else
            {
                shell.Resize(toW, toH);
                shell.Host.Paint(0, keepAlive: true);
            }
            Assert.Equal(before.Window, Tiers.Window);
            Assert.Equal(before.Height, Tiers.Height);
            parts = parts.ConvertAll(p => p with { Local = scene.Bounds(p.Node) });
            search = search.ConvertAll(p => p with { Local = scene.Bounds(p.Node) });

            // The crossing frame: the new tier commits here.
            if (narrowing) shell.Host.Paint(0, keepAlive: true); else shell.Host.RunFrame();
            Assert.True(window == Tiers.Window, $"the window tier is {Tiers.Window}, not {window}");
            Assert.True(tall == Tiers.Height, $"the height tier is {Tiers.Height}, not {tall}");

            string what = $"{fromW}x{fromH} -> {toW}x{toH}";
            foreach (var p in parts) AssertKeptGliding(shell, p, what);

            bool pillAfter = ZuneFrameRules.TitleBarOf(Tiers).Pill;
            if (pillAfter == pillBefore)
                foreach (var p in search) AssertKeptGliding(shell, p, what);
            else
            {
                // The form swaps under the shared cross-fade; the allocator's form follows on the same frame.
                Assert.False(shell.Find(pillAfter ? "zune-search-pill-host" : "zune-search-icon-host").IsNull, $"{what}: the new search form is mounted");
                Assert.Equal(pillAfter ? Shell.MergedSearchMode.Field : Shell.MergedSearchMode.Icon, Shell.ChromeLayout.Peek().SearchMode);
            }

            // Settled: the records of the new tier.
            shell.RunFrames(40);
            var frame = ZuneTiers.FrameOf(Tiers);
            Assert.Equal(frame.TitleBarH, scene.AbsoluteRect(Node(shell, ZuneFrameLayout.TitleBarKey)).H, 0.5f);
            var band = ZuneBandRules.BandOf(Tiers);
            float bandH = scene.AbsoluteRect(Node(shell, ZuneBandRules.BandKey)).H;
            Assert.True(MathF.Abs(bandH - band.ExpandedH) < 0.5f || MathF.Abs(bandH - band.CollapsedH) < 0.5f, $"{what}: the band settles at {bandH}");
        }
        finally
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(10);
        }
    }, fromW, fromH);

    /// <summary>The part is the same live node; if it moved or resized in its parent it, or a box that carries it, holds animation tracks.</summary>
    static void AssertKeptGliding(Shell.HeadlessShell shell, FramePart p, string what)
    {
        var scene = shell.Host.Scene;
        Assert.True(scene.IsLive(p.Node), $"{what}: {p.Name} was thrown away on the crossing (its node is gone)");
        Assert.True(shell.Find(p.Name).IsNull || shell.Find(p.Name).Equals(p.Node) || p.Name.StartsWith(ZuneBandRules.PivotKeyPrefix, StringComparison.Ordinal),
            $"{what}: {p.Name} was remounted on the crossing");
        var now = scene.Bounds(p.Node);
        bool moved = MathF.Abs(now.X - p.Local.X) > 0.5f || MathF.Abs(now.Y - p.Local.Y) > 0.5f
                  || MathF.Abs(now.W - p.Local.W) > 0.5f || MathF.Abs(now.H - p.Local.H) > 0.5f;
        if (!moved || Design.Reduced) return;
        bool glides = false;
        for (var n = p.Node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
            if (shell.Host.Animation.HasTracks(n)) { glides = true; break; }
        Assert.True(glides, $"{what}: {p.Name} snapped into its new box ({p.Local} -> {now}) instead of gliding");
    }
}
