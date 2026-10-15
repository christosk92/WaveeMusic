// ── Wavee.ShellTests/ClassicMotionMountedTests.cs — the tuned Classic motion, mounted (themes plan 6.6, CT2) ───────────────
//
// Motion cannot be judged from one screenshot, so these facts read the mounted shell frame by frame on the headless clock (a fixed
// 16 ms step). The page swap is 90 ms out, then 210 ms in (`Design.Nav`), and stays so; what CT2 tunes around it:
//
//   F7    a leaving page is a frozen snapshot for its exit (Home keeps its daylist); the masthead's head leaves with the old page
//         and arrives with the new one, on the same enter leg as the body; Home's busy bar fades in its fixed 3-px slot
//   D15   the shell material (the chrome's tint) keeps the leaving page's colour for the exit leg and cross-fades when the new
//         page arrives
//   F10   a light/dark flip on the live path cross-fades the page's tone plane IN PLACE (it is no longer keyed on the theme)
//   F11   a page entrance seeded while the window waits for its reveal starts on the first shown frame, exactly as a visible one
//   F12   a virtual list dropped from KeepAlive reopens at its remembered offset in its first frame

using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee;
using Wavee.HomeUi;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ClassicMotionMountedTests(ShellHost host)
{
    const float W = 1600f, H = 900f;
    static readonly Shell.Route s_album = Shell.For(EntityUri.Parse("spotify:album:al2"), "Album");

    /// <summary>The opacity a node is drawn at: its own times every ancestor's (the page swap animates the KeepAlive entry root).</summary>
    static float Drawn(Shell.HeadlessShell shell, NodeHandle node)
    {
        var scene = shell.Host.Scene;
        float o = 1f;
        for (var n = node; !n.IsNull; n = scene.Parent(n)) o *= scene.Paint(n).Opacity;
        return o;
    }

    static string TextUnder(Shell.HeadlessShell shell, NodeHandle node)
    {
        if (node.IsNull) return "";
        var scene = shell.Host.Scene;
        foreach (var n in Subtree(scene, node))
            if (scene.Paint(n).VisualKind == VisualKind.Text) return Entities.Strings.Resolve(scene.Paint(n).Text);
        return "";
    }

    static bool Landed(Shell.RouteKind kind) => Shell.Current.Peek().Kind == kind && Shell.Shown.Peek().Equals(Shell.Current.Peek());

    // ── F7 ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_leaving_Home_is_a_frozen_snapshot_for_its_exit_and_keeps_its_daylist()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            Until(shell, () => !shell.Find("daylist-copy").IsNull, "the daylist card");
            shell.RunFrames(60);
            var column = Node(shell, "home:column");
            var daylist = Node(shell, "daylist-copy");
            int nodes = CountUnder(shell, column);

            Shell.GoTo(s_album);
            int exitFrames = 0;
            while (!Landed(Shell.RouteKind.Album))
            {
                shell.RunFrames(1);
                Assert.True(++exitFrames <= 10, "the exit leg ran past 10 frames");
                Assert.True(shell.Host.Scene.IsLive(daylist), "the daylist stays on the leaving page");
                Assert.Equal(nodes, CountUnder(shell, column));   // nothing is rebuilt against the incoming route
            }
            Assert.InRange(exitFrames, 5, 7);   // the 90 ms exit leg, unchanged
        }, W, H);

    /// <summary>The drawn opacity of the masthead band whose head ends in <paramref name="title"/> (0 when none is mounted).</summary>
    static float Head(Shell.HeadlessShell shell, string title)
    {
        foreach (var (node, key) in shell.FindPrefix("shell.masthead:"))
            if (key.EndsWith(title, StringComparison.Ordinal)) return Drawn(shell, node);
        return 0f;
    }

    [Fact]
    public void The_masthead_head_leaves_with_the_old_page_and_arrives_with_the_new_one_on_its_enter_leg()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse));
            Until(shell, () => TextUnder(shell, shell.Find("masthead-current")) == "Browse", "the Browse head");
            shell.RunFrames(40);
            Assert.Equal(1f, Head(shell, "Browse"), 3);

            // Frame by frame, each head is drawn exactly as its page's body: the old one leaves on the 90 ms exit leg, the new one
            // waits that leg out and arrives on the 210 ms enter leg, so a new title never shows over the leaving body.
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Concerts));
            var log = new List<string>();
            for (int i = 0; i < 24; i++)
            {
                shell.RunFrames(1);
                var browse = shell.Find("page:browse");
                float oldHead = Head(shell, "Browse"), oldBody = browse.IsNull ? 0f : Drawn(shell, browse);
                float newHead = Head(shell, "Concerts"), newBody = Drawn(shell, Node(shell, "page:concerts"));
                log.Add($"f{i} old {oldHead:0.000}/{oldBody:0.000} new {newHead:0.000}/{newBody:0.000}");
                Assert.True(MathF.Abs(oldHead - oldBody) < 0.02f && MathF.Abs(newHead - newBody) < 0.02f,
                    "a head moves with its body: " + string.Join(" | ", log));
            }
            Assert.Contains(log, l => l.Contains(" new 0.000/0.000", StringComparison.Ordinal));   // the new head waited out the exit leg
            Assert.Equal(0f, Head(shell, "Browse"));
            Assert.Equal(1f, Head(shell, "Concerts"), 3);
        }, W, H);

    [Fact]
    public void Homes_busy_bar_fades_in_and_out_in_its_fixed_three_pixel_slot()
    {
        var state = new Signal<FacetSwitchState>(FacetSwitchState.Initial(""));
        var index = new Signal<int>(0);
        FacetWord[] words = [new("", "All", false, null, null)];
        var props = new FacetRowProps(new Signal<FacetWord[]>(words), state, () => index, static _ => { }, static () => { }, static _ => false);
        ViewHarness.With(host, ViewKeys.HomeFacetRow, props, harness =>
        {
            harness.RunFrames(5);
            var slot = harness.Node("home:facet:bar");
            var rest = harness.AbsoluteBounds(slot);
            Assert.Equal(ClassicHomeRules.BarH, rest.H, 0.5f);
            Assert.False(harness.Contains("home:facet:bar:busy"));

            state.Value = state.Peek() with { Phase = FacetPhase.Loading };
            var seen = new List<float>();
            for (int i = 0; i < 20; i++)
            {
                harness.RunFrames(1);
                seen.Add(Drawn(harness.Shell, harness.Node("home:facet:bar:busy")));
                Assert.Equal(rest, harness.AbsoluteBounds(slot));   // the slot never moves or grows
            }
            Assert.True(seen[0] < 0.6f && seen[^1] > 0.99f, "the bar fades in: " + string.Join(", ", seen));

            state.Value = state.Peek() with { Phase = FacetPhase.Idle };
            harness.RunFrames(3);
            var leaving = harness.Find("home:facet:bar:busy");
            Assert.False(leaving.IsNull, "the bar fades out rather than vanishing");
            Assert.InRange(Drawn(harness.Shell, leaving), 0.05f, 0.99f);
            harness.RunFrames(30);
            Assert.False(harness.Contains("home:facet:bar:busy"));
            Assert.Equal(rest, harness.AbsoluteBounds(slot));
        }, new ViewHarnessOptions { Composition = ClassicTheme.Create() });
    }

    // ── D15 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_material_keeps_the_leaving_pages_colour_for_the_exit_and_cross_fades_when_the_new_page_arrives()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(s_album);
            Until(shell, () => shell.FindPrefix("trail:album:").Count == 1, "the album");
            shell.RunFrames(60);
            var tint = Node(shell, "shell.material.tint");
            var scene = shell.Host.Scene;
            ColorF before = scene.Paint(tint).Fill;

            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            var log = new List<string>();
            while (true)
            {
                shell.RunFrames(1);
                if (Landed(Shell.RouteKind.Home)) break;
                Assert.True(log.Count < 10, "the exit leg ran past 10 frames");
                log.Add("exit " + scene.Paint(tint).Fill);
                Assert.Equal(before, scene.Paint(tint).Fill);           // the leaving page keeps its colour …
                Assert.False(scene.TryGetBrushAnim(tint, out _));       // … and nothing starts fading under it
            }
            UntilBackground(shell, () => scene.Paint(tint).Fill != before, "Home's own tint");
            Assert.True(scene.TryGetBrushAnim(tint, out var fade) && fade.T < 0.5f,
                "the colour cross-fades in with the page: " + string.Join(" | ", log));
        }, W, H);

    // ── F10 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The node of a mounted tone plane that paints its fill.</summary>
    static NodeHandle TonePlane(ViewHarness harness)
    {
        var scene = harness.Shell.Host.Scene;
        foreach (var n in harness.Subtree(scene.Root))
            if (scene.Paint(n).Fill.A > 0f && scene.Paint(n).Fill.A < 0.5f) return n;
        return NodeHandle.Null;
    }

    [Fact]
    public void A_light_dark_flip_cross_fades_the_tone_plane_in_place()
        => ViewHarness.With(host, ViewKeys.ShellCoverTone,
            // The payload rung (an album's own accent) colours the plane without a network grading, as a detail page does first.
            new CoverToneProps(null, null, 0f, 0f, HeroOnly: false, PayloadAccent: 0xFF3366CCu),
            harness =>
            {
                harness.RunFrames(5);
                var scene = harness.Shell.Host.Scene;
                var tone = TonePlane(harness);
                Assert.False(tone.IsNull, "the plane paints the album's tone");
                ColorF old = scene.Paint(tone).Fill;
                ThemeKind kind = Tok.Theme;
                try
                {
                    Design.SeedPalette(kind == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);   // what every light/dark verb runs (live path)
                    harness.RunFrames(1);
                    Assert.True(scene.IsLive(tone), "the plane is the same node: a flip never remounts it");
                    Assert.NotEqual(old, scene.Paint(tone).Fill);   // the wash role really moved
                    for (int i = 0; i < 30 && (!scene.TryGetBrushAnim(tone, out var f) || f.T < 0.4f); i++) harness.RunFrames(1);
                    Assert.True(scene.TryGetBrushAnim(tone, out var fade), "the bound plane fades (EP20); a theme-keyed plane remounted and snapped");
                    Assert.InRange(fade.T, 0.4f, 0.7f);
                    Assert.Equal(old, fade.FillFrom);   // from the colour it showed
                    harness.RunFrames(20);
                    Assert.False(scene.TryGetBrushAnim(tone, out _));
                }
                finally
                {
                    Design.SeedPalette(kind);
                    harness.RunFrames(20);
                }
            }, new ViewHarnessOptions { Composition = ClassicTheme.Create() });

    // ── F11 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The drawn opacity of <paramref name="pageKey"/>'s body on each of <paramref name="frames"/> frames.</summary>
    static List<float> BodyOpacities(Shell.HeadlessShell shell, string pageKey, int frames)
    {
        var seen = new List<float>();
        for (int i = 0; i < frames; i++)
        {
            var page = shell.Find(pageKey);
            seen.Add(page.IsNull ? 0f : Drawn(shell, page));
            shell.RunFrames(1);
        }
        return seen;
    }

    static string Row(List<float> values) => string.Join(", ", values.Select(static v => v.ToString("0.000")));

    [Fact]
    public void A_page_entering_behind_the_reveal_gate_starts_on_the_first_shown_frame_exactly_as_a_visible_one()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse));
            shell.RunFrames(1);
            var visible = BodyOpacities(shell, "page:browse", 24);
            shell.RunFrames(30);

            // The window waits for its reveal (the boot and every tray restore): the frames it builds are not shown.
            bool ready = false;
            shell.Host.BeginRevealGate(new RevealGateOptions { FirstFrameReady = () => ready, TimeoutMs = 60_000 });
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Concerts));
            shell.RunFrames(1);
            for (int i = 0; i < 12; i++)
            {
                var page = shell.Find("page:concerts");
                Assert.Equal(0f, page.IsNull ? 0f : Drawn(shell, page), 4);   // nothing moves while nobody can see it
                shell.RunFrames(1);
            }
            ready = true;
            var gated = BodyOpacities(shell, "page:concerts", 24);
            Assert.False(shell.Host.IsRevealPending);
            // gated[0] is the last held frame; gated[1] is the first shown one, which poses t = 0 as the commit frame (visible[0]) did.
            Assert.Equal(0f, gated[0], 4);
            for (int i = 0; i + 1 < gated.Count; i++)
                Assert.True(MathF.Abs(visible[i] - gated[i + 1]) < 1e-3f, $"frame {i}: visible {Row(visible)} | gated {Row(gated)}");
        }, W, H);

    // ── F12 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    static ScrollHandle LikedScroll(Shell.HeadlessShell shell)
    {
        var vp = ScrollUnder(shell, Single(shell, "list:liked:").Node);
        Assert.False(vp.IsNull, "the list's viewport");
        return shell.Host.TryGetScrollHandle(vp) ?? throw new InvalidOperationException("no scroll handle on the list's viewport");
    }

    [Fact]
    public void A_list_dropped_from_keep_alive_reopens_at_its_offset_in_its_first_frame()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            Until(shell, () => shell.FindPrefix("list:liked:").Count == 1 && LikedScroll(shell).MaxOffset > 1000.0, "the Liked list with rows to scroll");
            shell.RunFrames(20);
            LikedScroll(shell).ScrollTo(400.0, ScrollMove.Immediate);
            shell.RunFrames(5);
            Assert.Equal(400.0, LikedScroll(shell).OffsetNow, 0.5);

            foreach (var kind in new[] { Shell.RouteKind.Home, Shell.RouteKind.Browse, Shell.RouteKind.Recents, Shell.RouteKind.Concerts })
            {
                Shell.GoTo(new Shell.Route(kind));
                shell.RunFrames(30);
            }
            Assert.Empty(shell.FindPrefix("list:liked:"));   // dropped: its scroller is gone

            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            double first = double.NaN;
            for (int i = 0; i < 240 && double.IsNaN(first); i++)
            {
                shell.RunFrames(1);
                if (shell.FindPrefix("list:liked:").Count == 1) first = LikedScroll(shell).OffsetNow;
            }
            Assert.Equal(400.0, first, 0.5);   // the first frame that has the list has the offset: no top-then-jump
        }, W, H);
}
