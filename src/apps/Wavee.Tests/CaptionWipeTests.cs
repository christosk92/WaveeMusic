// ── Wavee.Tests/CaptionWipeTests.cs — the stage caption's render-thread wipe and its one-shot edge wake ──
//
// The caption's karaoke wipe is posed on the render thread (AnimChannel.GlyphWipeSplit) while it plays; the UI wakes once per
// view edge. These pin the review fixes: the edge wake re-arms on a seek within a line and after a wake that fired early on
// another clock; every split (the UI's and the render thread's) steps along the measured reading-order run, wrapped or not;
// a line handing off is held with its on-screen split written back; a pause hands the split back to the UI on the same tick.

using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class CaptionWipeTests
{
    // ── the edge wake ──

    [Fact]
    public void The_edge_wake_arms_once_and_keeps_its_timer_through_report_jitter()
    {
        var edge = new Stage.Caption.EdgeWake();
        Assert.True(edge.Arm(10_000, 9_000, 50_000, out float delay));
        Assert.Equal(1000f, delay);
        Assert.False(edge.Arm(10_000, 9_500, 50_500, out _));                // the clock ran on: same due instant
        Assert.False(edge.Arm(10_000, 9_501, 50_500, out _));                // a report re-anchored 1 ms off: inside the slack
        Assert.True(edge.Pending);
    }

    [Fact]
    public void A_seek_within_the_line_re_arms_the_edge_wake_earlier()
    {
        var edge = new Stage.Caption.EdgeWake();
        Assert.True(edge.Arm(10_000, 9_000, 50_000, out _));                 // due at clock 51 000
        Assert.True(edge.Arm(10_000, 9_800, 50_100, out float delay));       // a forward seek of 700 ms: due at 50 300
        Assert.Equal(200f, delay);
        Assert.True(edge.Arm(10_000, 9_000, 50_200, out delay));             // and a backward one
        Assert.Equal(1000f, delay);
    }

    [Fact]
    public void A_wake_that_fires_early_on_another_clock_arms_again_for_the_same_edge()
    {
        var edge = new Stage.Caption.EdgeWake();
        Assert.True(edge.Arm(10_000, 9_000, 50_000, out _));
        edge.Fire();                                                          // the timer queue's clock reached it first
        Assert.False(edge.Pending);
        Assert.True(edge.Arm(10_000, 9_996, 50_996, out float delay));       // the view has not changed yet: arm again
        Assert.Equal(4f, delay);
        Assert.True(edge.Pending);
    }

    [Fact]
    public void Clearing_the_edge_wake_cancels_once()
    {
        var edge = new Stage.Caption.EdgeWake();
        Assert.False(edge.Arm(long.MaxValue, 0, 0, out _));                  // nothing armed: nothing to cancel
        Assert.True(edge.Arm(10_000, 9_000, 50_000, out _));
        Assert.True(edge.Arm(long.MaxValue, 9_100, 50_100, out float delay));
        Assert.True(delay < 0f);
        Assert.False(edge.Arm(long.MaxValue, 9_200, 50_200, out _));
    }

    // ── the wipe ──

    static Lyrics.Line WordLine(long start, long end, params string[] words)
    {
        var syllables = new List<Lyrics.Syllable>(words.Length);
        long slot = (end - start) / words.Length;
        string text = "";
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i] + (i + 1 < words.Length ? " " : "");
            syllables.Add(new Lyrics.Syllable(start + i * slot, start + (i + 1) * slot, w));
            text += w;
        }
        return new Lyrics.Line(start, text, syllables, end, null, null, true);
    }

    [Theory]
    [InlineData(300f, 300f)]     // one line: the run is the node's width
    [InlineData(300f, 527.3f)]   // wrapped onto two lines: the run is longer than the node
    public void The_render_thread_steps_the_split_exactly_as_the_UI_does(float nodeWidth, float run)
    {
        var line = WordLine(1_000, 5_000, "city", "lights", "falling", "over", "the", "river");
        var wipe = new GlyphWipe(default, default, 0f) { Run = run };
        for (long t = 1_000; t <= 5_000; t += 7)
        {
            float raw = Stage.CaptionWipeRows.SplitAt(line, t);
            Assert.Equal(Stage.CaptionWipeRows.UiSplit(line, t, run), wipe.QuantizeSplit(raw, nodeWidth));
        }
    }

    static (SceneStore Scene, AnimEngine Anim, NodeHandle Main, NodeHandle Glow) Fixture(out NodeHandle main2, out NodeHandle glow2)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new(0, 0, 1000, 400);
        NodeHandle Text(ushort id)
        {
            var n = scene.CreateNode(id);
            scene.AppendChild(root, n);
            scene.Bounds(n) = new(0, 0, 300, 80);
            scene.Paint(n).VisualKind = VisualKind.Text;
            scene.SetGlyphWipe(n, new GlyphWipe(new ColorF(1, 1, 1, 1), new ColorF(1, 1, 1, 0.45f), 0f));
            return n;
        }
        var main = Text(2);
        var glow = Text(3);
        main2 = Text(4);
        glow2 = Text(5);
        return (scene, new AnimEngine(scene) { RenderOwnsCompositor = true }, main, glow);
    }

    [Fact]
    public void A_line_that_hands_off_is_held_with_its_on_screen_split_written_back_and_the_next_line_is_seeded()
    {
        var (scene, anim, main, glow) = Fixture(out var main2, out var glow2);
        var line0 = WordLine(1_000, 5_000, "city", "lights", "falling", "over");
        var line1 = WordLine(5_200, 9_000, "the", "river", "we", "keep");
        const float run = 527.3f;
        var rows = new Stage.CaptionWipeRows();
        Assert.True(rows.Seed(anim, scene, line0, 0, main, glow, 1_500, 10_000, run));
        Assert.Equal(0, rows.Line);
        Assert.True(scene.TryGetGlyphWipe(main, out var seeded));
        Assert.Equal(run, seeded.Run);
        Assert.True(anim.TryGetTrackValue(glow, AnimChannel.GlyphWipeSplit, out _));

        // the render thread poses the wipe part-way, and the UI imports that pose
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 1_234);
        anim.ApplyCompositorFeedback(renderer.Feedback);
        Assert.True(snapshot.TryGetGlyphWipe(main, out var onScreen));
        Assert.True(onScreen.Split > 0f && onScreen.Split < 1f);

        // the hand-off: held where it stands, and the authored split IS the on-screen one
        rows.Seed(anim, scene, line1, 1, main2, glow2, 5_300, 10_000, run);
        Assert.Equal(1, rows.Line);
        Assert.True(scene.TryGetGlyphWipe(main, out var heldMain));
        Assert.True(scene.TryGetGlyphWipe(glow, out var heldGlow));
        Assert.Equal(onScreen.Split, heldMain.Split);
        Assert.Equal(onScreen.Split, heldGlow.Split);
        Assert.True(anim.TryGetTrackValue(main, AnimChannel.GlyphWipeSplit, out _));   // held, not cancelled
        Assert.True(anim.TryGetTrackValue(main2, AnimChannel.GlyphWipeSplit, out _));  // the next line's rows
        Assert.True(anim.TryGetTrackValue(glow2, AnimChannel.GlyphWipeSplit, out _));
        snapshot.ReleaseResources();
    }

    [Fact]
    public void A_pause_cancels_the_rows_and_the_UI_writes_the_same_split_on_that_tick()
    {
        var (scene, anim, main, glow) = Fixture(out _, out _);
        var line = WordLine(1_000, 5_000, "hold", "on", "to", "every", "word");
        const float run = 412.6f;
        var rows = new Stage.CaptionWipeRows();
        Assert.True(rows.Seed(anim, scene, line, 0, main, glow, 1_500, 10_000, run));

        rows.Cancel(anim);
        Stage.CaptionWipeRows.DriveUi(scene, main, glow, line, 2_345, run);
        Assert.Equal(-1, rows.Line);
        Assert.False(anim.TryGetTrackValue(main, AnimChannel.GlyphWipeSplit, out _));
        Assert.False(anim.TryGetTrackValue(glow, AnimChannel.GlyphWipeSplit, out _));
        Assert.True(scene.TryGetGlyphWipe(main, out var m));
        Assert.True(scene.TryGetGlyphWipe(glow, out var g));
        float expected = Stage.CaptionWipeRows.UiSplit(line, 2_345, run);
        Assert.Equal(expected, m.Split);
        Assert.Equal(expected, g.Split);
        Assert.Equal(run, m.Run);
    }

    [Fact]
    public void A_report_that_moves_the_clock_re_seeds_and_one_that_does_not_keeps_the_rows()
    {
        var (scene, anim, main, glow) = Fixture(out _, out _);
        var line = WordLine(1_000, 5_000, "turn", "it", "up", "until");
        var rows = new Stage.CaptionWipeRows();
        Assert.True(rows.Seed(anim, scene, line, 0, main, glow, 1_500, 10_000, 300f));
        float first = Lyrics.Wipe.SplitKeyframes(line, 1_500, lead: true, out _)![0].Value;
        Assert.True(scene.TryGetGlyphWipe(main, out var w));
        Assert.Equal(first, w.Split);
        Assert.True(rows.Seed(anim, scene, line, 0, main, glow, 2_000, 10_500, 300f));   // the same clock: kept, nothing written
        Assert.True(scene.TryGetGlyphWipe(main, out w));
        Assert.Equal(first, w.Split);
        Assert.True(rows.Seed(anim, scene, line, 0, main, glow, 2_700, 11_000, 300f));   // a seek of +200: re-seeded from there
        Assert.True(scene.TryGetGlyphWipe(main, out w));
        Assert.Equal(Lyrics.Wipe.SplitKeyframes(line, 2_700, lead: true, out _)![0].Value, w.Split);
        Assert.True(anim.TryGetTrackValue(main, AnimChannel.GlyphWipeSplit, out _));
    }
}
