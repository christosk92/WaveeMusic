// ── Wavee.ShellTests/WinUiTierLayoutTests.cs — the app under WinUI at every tier size and threshold edge (themes plan 3.5; P6 W0) ─────
//
// The harness every WinUI lane extends: the app mounted under WinUI (`WinUiHost`) at each size the plan lists, the boards' sizes
// (400 x 860, 800 x 330, 800 x 600, 1440 x 960, 2560 x 1440, 3840 x 2160), what Auto zoom produces (1920 x 1056, 2048 x 1114) and every
// threshold edge of WinUI's ladder (W-Tiers: 640/641, 617 and 616 from above, 1007/1008, 984 and 983 from above, 1919/1920, 1896 and 1895
// from above; heights 719/720, 696 and 695 from above; no Huge 4K edge, so 3840 is Huge). For each it asserts, on the facts and the
// records, never on pixels:
//
//   · the tier is final in the FIRST frame (or the first settled frame after a resize) and does not change on the next;
//   · the records in use are WinUI's for that tier (`WinUiTiers.FrameOf`) and the shell offers WinUI's capabilities (tabs on by default);
//   · the nav pane presents NavigationView's mode for the tier under `navigationPane = Automatic`: nothing docked at Compact (minimal), the
//     48 rail at Small, the 280 pane from Standard;
//   · the three top-level regions (title bar, content column, play bar) lie inside the window, never overlap and take the whole height;
//     no page in the content column runs wider than it;
//   · at Compact every title-bar hit target is at least 40 x 40, once WinUI draws its own title bar (W2a);
//   · at 330 tall the content column is at least WinUI's ShortFloor (170).
//
// EXTENDING IT. A lane adds its page fixtures in its own partial file `WinUiTierLayoutTests.<Family>.cs`, never here:
//
//     public sealed partial class WinUiTierLayoutTests
//     {
//         static readonly TierPage Album = new("album", new Shell.Route(Shell.RouteKind.Album, ...), "page:album");
//         [Theory, MemberData(nameof(PageSizes))]
//         public void The_album_page_holds_at_every_tier(string size) => AssertPage(Album, size, (shell, tiers) => { /* its record */ });
//     }

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class WinUiTierLayoutTests(ShellHost host)
{
    // ── the sizes ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One size the app is checked at. <paramref name="From"/> / <paramref name="FromHeight"/> (when set) are the width / height
    /// the window is mounted at first and then resized from, for the edges whose tier depends on the direction they are reached from.</summary>
    public sealed record TierSize(string Name, float Width, float Height, WidthTier Window, HeightTier Tall, float? From = null, float? FromHeight = null)
    {
        /// <summary>Reached by a resize rather than mounted at.</summary>
        public bool Resized => From is not null || FromHeight is not null;
    }

    /// <summary>The plan 3.5 sizes and WinUI's threshold edges, by name (the theory data carries the name only).</summary>
    public static readonly IReadOnlyDictionary<string, TierSize> Sizes = new[]
    {
        new TierSize("floor 300x640", 300f, 640f, WidthTier.Compact, HeightTier.Short),
        new TierSize("floor 300x330", 300f, 330f, WidthTier.Compact, HeightTier.Short),
        new TierSize("compact 400x860", 400f, 860f, WidthTier.Compact, HeightTier.Regular),
        new TierSize("short 800x330", 800f, 330f, WidthTier.Small, HeightTier.Short),
        new TierSize("small 800x600", 800f, 600f, WidthTier.Small, HeightTier.Short),
        new TierSize("standard 1440x960", 1440f, 960f, WidthTier.Standard, HeightTier.Regular),
        new TierSize("auto 1920x1056", 1920f, 1056f, WidthTier.Huge, HeightTier.Regular),
        new TierSize("auto 2048x1114", 2048f, 1114f, WidthTier.Huge, HeightTier.Regular),
        new TierSize("huge 2560x1440", 2560f, 1440f, WidthTier.Huge, HeightTier.Regular),
        new TierSize("huge4k 3840x2160", 3840f, 2160f, WidthTier.Huge, HeightTier.Regular),
        new TierSize("edge 640", 640f, 760f, WidthTier.Compact, HeightTier.Regular),
        new TierSize("edge 641", 641f, 760f, WidthTier.Small, HeightTier.Regular),
        new TierSize("edge 617 from above", 617f, 760f, WidthTier.Small, HeightTier.Regular, From: 800f),
        new TierSize("edge 616 from above", 616f, 760f, WidthTier.Compact, HeightTier.Regular, From: 800f),
        new TierSize("edge 1007", 1007f, 760f, WidthTier.Small, HeightTier.Regular),
        new TierSize("edge 1008", 1008f, 760f, WidthTier.Standard, HeightTier.Regular),
        new TierSize("edge 984 from above", 984f, 760f, WidthTier.Standard, HeightTier.Regular, From: 1440f),
        new TierSize("edge 983 from above", 983f, 760f, WidthTier.Small, HeightTier.Regular, From: 1440f),
        new TierSize("edge 1919", 1919f, 760f, WidthTier.Standard, HeightTier.Regular),
        new TierSize("edge 1920", 1920f, 760f, WidthTier.Huge, HeightTier.Regular),
        new TierSize("edge 1896 from above", 1896f, 760f, WidthTier.Huge, HeightTier.Regular, From: 2560f),
        new TierSize("edge 1895 from above", 1895f, 760f, WidthTier.Standard, HeightTier.Regular, From: 2560f),
        new TierSize("height 719", 1440f, 719f, WidthTier.Standard, HeightTier.Short),
        new TierSize("height 720", 1440f, 720f, WidthTier.Standard, HeightTier.Regular),
        new TierSize("height 696 from above", 1440f, 696f, WidthTier.Standard, HeightTier.Regular, FromHeight: 960f),
        new TierSize("height 695 from above", 1440f, 695f, WidthTier.Standard, HeightTier.Short, FromHeight: 960f),
    }.ToDictionary(static s => s.Name, StringComparer.Ordinal);

    /// <summary>Every size, for the frame and for a page fixture of a key surface.</summary>
    public static TheoryData<string> FrameSizes() => Names(static _ => true);

    /// <summary>Every size under 3840 wide, for a page fixture that is not a key surface (plan 3.5: 3840 is for the key surfaces).</summary>
    public static TheoryData<string> PageSizes() => Names(static s => s.Width < 3840f && (s.From ?? 0f) < 3840f);

    static TheoryData<string> Names(Func<TierSize, bool> keep)
    {
        var data = new TheoryData<string>();
        foreach (var s in Sizes.Values) if (keep(s)) data.Add(s.Name);
        return data;
    }

    // ── the page fixtures (lanes add theirs in their own partial files) ────────────────────────────────────────────────────

    /// <summary>A page the frame is checked with: its route and a node key (or key prefix) that is mounted once the page is on screen.</summary>
    public sealed record TierPage(string Name, Shell.Route Route, string ReadyKeyPrefix);

    /// <summary>The frame with Home: the route a fresh session opens on (its content state is keyed by the core, whoever draws it).</summary>
    public static readonly TierPage Home = new("home", new Shell.Route(Shell.RouteKind.Home), "page:" + Shell.NameOf(new Shell.Route(Shell.RouteKind.Home)));

    // ── the smoke and the frame's facts ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1440f, 960f)]
    [InlineData(400f, 860f)]
    public void The_app_mounts_under_winui(float width, float height)
        => WinUiHost.Mounted(host, shell =>
        {
            shell.RunFrames(30);
            Assert.Equal(WinUiTheme.Id, ThemeHost.Current.Peek().Manifest.Id);
            var facts = ThemeHost.Facts.Peek();
            Assert.Same(WinUiTiers.Policy, facts.Policies.Tiers);
            Assert.Equal(WinUiPolicies.CapabilitiesFor(WinUiParameters.Default), facts.Capabilities);
            Until(shell, () => shell.FindPrefix(Home.ReadyKeyPrefix).Count > 0, "Home on screen under WinUI");
            shell.RunFrames(30);
        }, width, height);

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_frame_holds_its_tier_layout(string size) => AssertPage(Home, size);

    [Fact]
    public void The_size_table_reproduces_the_boards_ladder()
    {
        // A fresh size resolves to its row's tier on WinUI's policy; a directional edge only from the side its row names.
        foreach (var s in Sizes.Values)
        {
            TierFacts facts;
            if (s.Resized)
            {
                float fromW = s.From ?? s.Width, fromH = s.FromHeight ?? s.Height;
                facts = WindowTiers.Resolve(WinUiTiers.Policy, s.Width, s.Height, s.Width,
                    previous: WindowTiers.Resolve(WinUiTiers.Policy, fromW, fromH, fromW));
            }
            else facts = WindowTiers.Resolve(WinUiTiers.Policy, s.Width, s.Height, s.Width);
            Assert.True(s.Window == facts.Window, $"{s.Name}: {facts.Window}");
            Assert.True(s.Tall == facts.Height, $"{s.Name}: {facts.Height}");
        }
    }

    // ── the helper every fixture runs ──────────────────────────────────────────────────────────────────────────────────

    static TierFacts Tiers => ThemeHost.Facts.Peek().Tiers;

    /// <summary>The app under WinUI at <paramref name="width"/> x <paramref name="height"/> (the partial files' door to the host).</summary>
    internal void Mounted(Action<Shell.HeadlessShell> body, float width, float height) => WinUiHost.Mounted(host, body, width, height);

    /// <summary>Mount the app under WinUI at <paramref name="sizeName"/> (resizing into it from its <c>From</c> size when it has one), show
    /// <paramref name="page"/>, run every frame assertion, then <paramref name="more"/> (the page's own record checks). <paramref name="openFirst"/>
    /// opens the page at the <c>From</c> size and resizes it into the edge, the way a user narrows a window with the page open.</summary>
    internal void AssertPage(TierPage page, string sizeName, Action<Shell.HeadlessShell, TierFacts>? more = null, bool openFirst = false)
    {
        var size = Sizes[sizeName];
        WinUiHost.Mounted(host, shell =>
        {
            bool opened = false;
            if (size.Resized)
            {
                // Reached by a resize: the settled size's crossing commits on the first frame that is not resized.
                shell.RunFrames(20);
                if (openFirst && page.Route.Kind != Shell.RouteKind.Home)
                {
                    Shell.GoTo(page.Route);
                    Until(shell, () => shell.FindPrefix(page.ReadyKeyPrefix).Count > 0, page.Name + " on screen at " + size.Name + "'s starting size");
                    shell.RunFrames(40);
                    opened = true;
                }
                shell.Resize(size.Width, size.Height);
                shell.Host.Paint(0, keepAlive: true);
                shell.Host.RunFrame();
            }
            // The tier is final in this frame, and the next one does not change it.
            AssertTier(size);
            shell.Host.RunFrame();
            AssertTier(size);

            if (!opened && page.Route.Kind != Shell.RouteKind.Home) Shell.GoTo(page.Route);
            Until(shell, () => shell.FindPrefix(page.ReadyKeyPrefix).Count > 0, page.Name + " on screen at " + size.Name);
            shell.RunFrames(40);   // entrances, the pane's width motion and the panel settle
            AssertTier(size);      // navigating never moves the tier

            var tiers = Tiers;
            AssertRecords(size, tiers);
            AssertRegions(shell, size);
            // The 40-DIP Compact rule is the WinUI title bar's (W2a); Classic's stand-in title bar never drew a Compact arm at these widths.
            if (size.Window == WidthTier.Compact && WinUiDraws(ViewKeys.ShellTitleBar.Id)) AssertCompactTargets(shell, size);
            more?.Invoke(shell, tiers);
        }, size.From ?? size.Width, size.FromHeight ?? size.Height);
    }

    static void AssertTier(TierSize size)
    {
        var t = Tiers;
        Assert.True(t.Window == size.Window, $"{size.Name}: the window tier is {t.Window}, not {size.Window}");
        Assert.True(t.Height == size.Tall, $"{size.Name}: the height tier is {t.Height}, not {size.Tall}");
        Assert.Same(WinUiTiers.Policy, t.Policy);
        // The facts are a snapshot taken at a crossing: a resize that crosses nothing (a held edge) keeps the mount's size in them.
        if (!size.Resized) Assert.Equal(size.Width, t.ViewportWidth, 0.5f);
    }

    /// <summary>The records in use are WinUI's for the tier, the shell offers WinUI's capabilities, and the nav pane presents the tier's mode.</summary>
    static void AssertRecords(TierSize size, TierFacts tiers)
    {
        var frame = WinUiTiers.FrameOf(tiers);
        Assert.Equal(size.Window, frame.Tier);
        Assert.Same(WinUiTiers.Frame(size.Window), frame);
        Assert.Equal(WinUiPolicies.CapabilitiesFor(WinUiParameters.Default), ThemeHost.Facts.Peek().Capabilities);

        // NavigationView's modes under Automatic (W-Tiers): minimal at Compact (nothing docked), the 48 rail at Small, the 280 pane from Standard.
        float expected = size.Window switch
        {
            WidthTier.Compact => 0f,
            WidthTier.Small => WinUiTiers.RailW,
            _ => WinUiTiers.PaneW,
        };
        Assert.True(MathF.Abs(Sidebar.PresentedWidth.Peek() - expected) <= 0.5f,
            $"{size.Name}: the nav pane presents {Sidebar.PresentedWidth.Peek()}, not {expected}");
    }

    /// <summary>The three top-level regions lie inside the window, never overlap and take the whole height; no page runs wider than the
    /// content column; at 330 tall the content column is at least the ShortFloor.</summary>
    static void AssertRegions(Shell.HeadlessShell shell, TierSize size)
    {
        var scene = shell.Host.Scene;
        RectF At(string key) => scene.AbsoluteRect(Node(shell, key));
        var title = At(WinUiFrameLayout.TitleBarKey);
        var content = At(WinUiFrameLayout.ContentKey);
        var bar = At(WinUiFrameLayout.PlayerBarKey);

        foreach (var (name, r) in new[] { ("title bar", title), ("content column", content), ("play bar", bar) })
        {
            Assert.True(r.X >= -0.5f && r.X + r.W <= size.Width + 0.5f, $"{size.Name}: the {name} runs outside the window horizontally ({r})");
            Assert.True(r.Y >= -0.5f && r.Y + r.H <= size.Height + 0.5f, $"{size.Name}: the {name} runs outside the window vertically ({r})");
        }
        Assert.True(title.Y + title.H <= content.Y + 0.5f, $"{size.Name}: the title bar overlaps the content column");
        Assert.True(content.Y + content.H <= bar.Y + 0.5f, $"{size.Name}: the content column overlaps the play bar");
        Assert.True(title.Y + title.H <= bar.Y + 0.5f, $"{size.Name}: the title bar overlaps the play bar");

        // No page-level horizontal overflow: a page in the content column never runs wider than it (a page the keep-alive parked is outside it).
        var inContent = new HashSet<NodeHandle>(Subtree(scene, Node(shell, WinUiFrameLayout.ContentKey)));
        foreach (var (node, key) in shell.FindPrefix("page:"))
        {
            var r = scene.AbsoluteRect(node);
            if (r.W <= 0f || !inContent.Contains(node)) continue;
            Assert.True(r.X >= content.X - 0.5f && r.X + r.W <= content.X + content.W + 0.5f, $"{size.Name}: {key} runs outside the content column ({r} in {content})");
        }

        if (size.Height <= TierPolicy.WindowMinHeight)
            Assert.True(content.H >= WinUiTiers.Policy.ShortFloor - 0.5f, $"{size.Name}: the content column is {content.H}, under the ShortFloor {WinUiTiers.Policy.ShortFloor}");
        Assert.Equal(size.Height, title.H + content.H + bar.H, 1f);   // the column is the window: nothing else takes height
    }

    /// <summary>True when WinUI draws <paramref name="viewId"/> itself (not Classic's stand-in).</summary>
    static bool WinUiDraws(string viewId) => ThemeHost.Registry.TemplateIdOf(viewId)?.StartsWith("winui.", StringComparison.Ordinal) == true;

    /// <summary>At Compact every title-bar hit target is at least 40 x 40 (the board's Compact rule). The identity chip is the overlays lane's
    /// own view and is left out.</summary>
    static void AssertCompactTargets(Shell.HeadlessShell shell, TierSize size)
    {
        var scene = shell.Host.Scene;
        var title = Node(shell, WinUiFrameLayout.TitleBarKey);
        var excluded = new HashSet<NodeHandle>();
        foreach (var (chip, _) in shell.FindPrefix("chrome-profile"))
            foreach (var n in Subtree(scene, chip)) excluded.Add(n);
        foreach (var (chip, _) in shell.FindPrefix(WinUiFrameRules.AvatarKey))   // WinUI's identity slot holds the same chip (W2a)
            foreach (var n in Subtree(scene, chip)) excluded.Add(n);
        foreach (var n in Subtree(scene, title))
        {
            if (excluded.Contains(n) || scene.InCollapsedSubtree(n)) continue;
            var flags = scene.Flags(n);
            if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
            var r = scene.AbsoluteRect(n);
            if (r.W <= 0f || r.H <= 0f) continue;
            Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{size.Name}: a title-bar target is {r.W} x {r.H}, under 40 x 40");
        }
    }
}
