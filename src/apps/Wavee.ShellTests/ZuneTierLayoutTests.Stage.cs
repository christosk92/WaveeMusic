// ── Wavee.ShellTests/ZuneTierLayoutTests.Stage.cs — Zune's fullscreen stage at every tier size and edge (P5 ZT3) ─────────────────────
//
// The app under Zune with the stage open through its own API, at the plan sizes and Zune's edges (639/640, 1007/1008, 1919/1920,
// 3199/3200; 300 x 330 and 400 x 860; 3840 x 2160). For each, on the records, never on pixels:
//
//   · the window tier is the size's, and the stage draws its tier's record (`ZuneStageTiers.StageOf`): the head is the record's height,
//     the plate is the height the allocator budgets from the tier's metrics, at the foot;
//   · the head and the plate never overlap and neither runs outside the window;
//   · at Compact every target of the head and the plate (the seek rail aside) is at least 40 x 40;
//   · Visualizer mode on a Compact window draws the Stack arrangement with the cover at its record's rect;
//   · a crossing (Standard → Huge → Huge 4K → Small → Compact → Standard) keeps the stage root, the transport's layer and the cover:
//     they move, never remount.

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
    /// <summary>The sizes the stage is checked at (names of <see cref="Sizes"/>).</summary>
    public static TheoryData<string> StageSizes()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[]
        {
            "floor 300x330", "compact 400x860", "small 800x600", "standard 1440x960", "huge 2560x1440", "huge4k 3840x2160",
            "edge 639", "edge 640", "edge 1007", "edge 1008", "edge 1919", "edge 1920", "edge 3199", "edge 3200",
        }) data.Add(name);
        return data;
    }

    static void OpenStage(Shell.HeadlessShell shell, Stage.Mode mode, Stage.VizLayout layout)
    {
        Prefs.Stage.SetMode((int)mode);
        Prefs.Stage.SetLayout((int)layout);
        Prefs.Stage.SetGalleryOpen(false);
        Prefs.Stage.SetSpectrum((int)Stage.SpectrumStyle.Off);
        Stage.Open(null, "test");
        Until(shell, () => !shell.Find("stage:cover").IsNull && !shell.Find("chrome:transport").IsNull, "the stage's cover and transport mounted");
        shell.RunFrames(120);   // the entrance and every spring settle
    }

    static void CloseStage(Shell.HeadlessShell shell)
    {
        Stage.Close(null, "test");
        shell.RunFrames(60);
        Prefs.Stage.SetMode(Platform.Keys.StageMode.Default);
        Prefs.Stage.SetLayout(Platform.Keys.StageLayout.Default);
        Prefs.Stage.SetGalleryOpen(Platform.Keys.StageGalleryOpen.Default);
        Prefs.Stage.SetSpectrum(Platform.Keys.StageSpectrum.Default);
    }

    static RectF StageRect(Shell.HeadlessShell shell, string key) => shell.Host.Scene.PresentedAbsoluteRect(Node(shell, key));

    static NodeHandle StageRoot(Shell.HeadlessShell shell) => shell.Host.Scene.Parent(Node(shell, "stage:backdrop"));

    /// <summary>The head: the first child of the chrome's top layer (content-tall: the record's head).</summary>
    static RectF Head(Shell.HeadlessShell shell) => shell.Host.Scene.PresentedAbsoluteRect(shell.Host.Scene.FirstChild(Node(shell, "chrome:top")));

    /// <summary>The allocator's plate height for the tiers in force (the stage's own metrics, as the core resolves them).</summary>
    static float PlateFor(float w, float h, in TierFacts tiers)
        => Stage.Layout.Seed(w, h, ZuneStageTiers.Policy.Aspect, ZuneStageTiers.Policy.MetricsFor(in tiers)).TransportH;

    [Theory, MemberData(nameof(StageSizes))]
    public void The_stage_holds_its_tier_layout(string sizeName)
    {
        var size = Sizes[sizeName];
        ZuneHost.Mounted(host, shell =>
        {
            OpenStage(shell, Stage.Mode.Lyrics, Stage.VizLayout.Card);
            try
            {
                var tiers = ThemeHost.Facts.Peek().Tiers;
                Assert.True(tiers.Window == size.Window, $"{size.Name}: the window tier is {tiers.Window}");
                var record = ZuneStageTiers.StageOf(in tiers);
                Assert.Equal(size.Window, record.Tier);

                var root = shell.Host.Scene.PresentedAbsoluteRect(StageRoot(shell));
                Assert.Equal(size.Width, root.W, 0.5f);
                Assert.Equal(size.Height, root.H, 0.5f);

                var head = Head(shell);
                Assert.Equal(record.HeadH, head.H, 0.5f);
                Assert.Equal(0f, head.Y, 0.5f);

                var plate = StageRect(shell, "stage:transport:plate");
                Assert.Equal(PlateFor(size.Width, size.Height, in tiers), plate.H, 0.5f);
                Assert.Equal(size.Height, plate.Y + plate.H, 0.5f);   // at the foot

                Assert.True(head.Y + head.H <= plate.Y + 0.5f, $"{size.Name}: the head ({head}) and the plate ({plate}) overlap");
                foreach (var (name, r) in new[] { ("head", head), ("plate", plate) })
                    Assert.True(r.X >= -0.5f && r.X + r.W <= size.Width + 0.5f, $"{size.Name}: the {name} runs outside the window ({r})");

                if (size.Window == WidthTier.Compact)
                {
                    AssertStageTargets(shell, size, Node(shell, "chrome:top"));
                    AssertStageTargets(shell, size, Node(shell, "stage:transport:plate"));
                }
            }
            finally { CloseStage(shell); }
        }, size.Width, size.Height);
    }

    [Fact]
    public void A_compact_visualizer_stacks_the_cover_titles_and_controls()
    {
        const float w = 400f, h = 860f;
        ZuneHost.Mounted(host, shell =>
        {
            OpenStage(shell, Stage.Mode.Visualizer, Stage.VizLayout.Card);
            try
            {
                var tiers = ThemeHost.Facts.Peek().Tiers;
                Assert.Equal(WidthTier.Compact, tiers.Window);
                var record = ZuneStageTiers.StageOf(in tiers);
                var layout = Stage.Layout.Seed(w, h, ZuneStageTiers.Policy.Aspect, ZuneStageTiers.Policy.MetricsFor(in tiers));
                var look = Stage.Look.Plain(Stage.Mode.Visualizer);
                var a = ZuneStageGeometry.Arrange(in layout, in look, record, galleryShown: false);
                Assert.Equal(ZuneStageForm.Stack, a.Form);

                Node(shell, "stage:transport:stack");
                var cover = StageRect(shell, "stage:cover");
                Assert.Equal(a.Cover.X, cover.X, 0.5f);
                Assert.Equal(a.Cover.Y, cover.Y, 0.5f);
                Assert.Equal(a.Cover.W, cover.W, 0.5f);
                Assert.True(Head(shell).Y + Head(shell).H <= cover.Y + 0.5f, "the cover clears the head");
                var controls = StageRect(shell, "stage:transport:controls");
                Assert.True(controls.Y + controls.H <= h + 0.5f && controls.Y >= cover.Y + cover.H, "the controls sit under the cover, inside the stage");
                Assert.True(shell.Find("stage:npcard").IsNull, "the stack draws no now-playing card");
                AssertStageTargets(shell, Sizes["compact 400x860"], Node(shell, "stage:transport:stack"));
                AssertStageTargets(shell, Sizes["compact 400x860"], Node(shell, "chrome:top"));
            }
            finally { CloseStage(shell); }
        }, w, h);
    }

    [Fact]
    public void A_crossing_keeps_the_stage_root_the_transport_and_the_cover()
    {
        ZuneHost.Mounted(host, shell =>
        {
            OpenStage(shell, Stage.Mode.Lyrics, Stage.VizLayout.Card);
            try
            {
                var root = StageRoot(shell);
                var transport = Node(shell, "chrome:transport");
                var cover = Node(shell, "stage:cover");

                foreach (var (w, h, tier) in new[]
                {
                    (2560f, 1440f, WidthTier.Huge), (3840f, 2160f, WidthTier.Huge4K), (800f, 600f, WidthTier.Small), (400f, 860f, WidthTier.Compact),
                    (1440f, 960f, WidthTier.Standard),
                })
                {
                    shell.Resize(w, h);
                    shell.Host.Paint(0, keepAlive: true);
                    shell.Host.RunFrame();   // the crossing commits on the first frame that is not resized
                    var tiers = ThemeHost.Facts.Peek().Tiers;
                    Assert.True(tiers.Window == tier, $"{w}x{h}: the window tier is {tiers.Window}, not {tier}");
                    shell.RunFrames(40);     // the plate cross-fades (the run stays inside the chrome's idle window: the transport is chrome)

                    Assert.Equal(root, StageRoot(shell));
                    Assert.Equal(transport, Node(shell, "chrome:transport"));
                    Assert.Equal(cover, Node(shell, "stage:cover"));

                    var plate = StageRect(shell, "stage:transport:plate");
                    Assert.Equal(PlateFor(w, h, in tiers), plate.H, 0.5f);
                    Assert.Equal(ZuneStageTiers.StageOf(in tiers).HeadH, Head(shell).H, 0.5f);
                }
            }
            finally { CloseStage(shell); }
        }, 1440f, 960f);
    }

    /// <summary>Every focusable, visible target under <paramref name="under"/> is at least 40 x 40 (the seek rail is the player family's own).</summary>
    static void AssertStageTargets(Shell.HeadlessShell shell, TierSize size, NodeHandle under)
    {
        var scene = shell.Host.Scene;
        var excluded = new HashSet<NodeHandle>();
        foreach (var (seek, _) in shell.FindPrefix("stage:transport:seek"))
            foreach (var n in Subtree(scene, seek)) excluded.Add(n);
        foreach (var n in Subtree(scene, under))
        {
            if (excluded.Contains(n) || scene.InCollapsedSubtree(n)) continue;
            var flags = scene.Flags(n);
            if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
            var r = scene.AbsoluteRect(n);
            if (r.W <= 0f || r.H <= 0f) continue;
            Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{size.Name}: a stage target is {r.W} x {r.H}, under 40 x 40 ({r})");
        }
    }
}
