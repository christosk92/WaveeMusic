// ── Wavee.ShellTests/TierLayoutTests.cs — the frame at every tier size and threshold edge (themes plan 3.5; TC1) ───────────────
//
// The REAL Classic frame on the headless host, mounted (or resized into) each size the plan lists: the floor, the minimum height, the
// boards (400 x 860, 800 x 330, 800 x 600, 1440 x 960, 2560 x 1440, 3840 x 2160), what Auto zoom produces (1920 x 1056, 2048 x 1114) and
// every threshold edge of Classic's ladder (527/528, 567 from below, 1007/1008, 1047 from below, 1879/1880 from above, 1919/1920).
// For each it asserts, on the facts and the records, never on pixels:
//
//   · the tier is final in the FIRST frame (or the first settled frame after a resize) and does not change on the next;
//   · the record in use is the structure's record for that tier: the frame's (the card square only at Compact), the page's gutter,
//     the nav pane's mode, the title bar's form (the tab count at Compact, the strip elsewhere, Forward only above 520);
//   · the four top-level regions (title bar, nav column, content card, player bar) lie inside the window and never overlap;
//   · at Compact every frame hit target is at least 40 x 40;
//   · at 330 tall the page region is at least the structure's ShortFloor (210).
//
// EXTENDING IT (TC2-TC8). This file is the core; a lane adds its page fixtures in its own partial file, never here:
//
//     public sealed partial class TierLayoutTests
//     {
//         static readonly TierPage Album = new("album", new Shell.Route(Shell.RouteKind.Album, ...), "detail:");
//         [Theory, MemberData(nameof(PageSizes))]
//         public void The_album_page_holds_at_every_tier(string size) => AssertPage(Album, size, (shell, tiers) => { /* its record */ });
//     }
//
// `AssertPage` runs every frame assertion above with the page on screen, then the lane's own assertions on its layout record.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests(ShellHost host)
{
    // ── the sizes ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One size the frame is checked at. <paramref name="From"/> (when set) is the width the window is mounted at first and then
    /// resized from, for the edges whose tier depends on the direction it is reached from.</summary>
    public sealed record TierSize(string Name, float Width, float Height, WidthTier Window, HeightTier Tall, float? From = null);

    /// <summary>The plan 3.5 sizes and Classic's threshold edges, by name (the theory data carries the name only).</summary>
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
        new TierSize("edge 527", 527f, 760f, WidthTier.Compact, HeightTier.Regular),
        new TierSize("edge 528", 528f, 760f, WidthTier.Small, HeightTier.Regular),
        new TierSize("edge 567 from below", 567f, 760f, WidthTier.Compact, HeightTier.Regular, From: 400f),
        new TierSize("edge 1007", 1007f, 760f, WidthTier.Small, HeightTier.Regular),
        new TierSize("edge 1008", 1008f, 760f, WidthTier.Standard, HeightTier.Regular),
        new TierSize("edge 1047 from below", 1047f, 760f, WidthTier.Small, HeightTier.Regular, From: 800f),
        new TierSize("edge 1879 from above", 1879f, 760f, WidthTier.Standard, HeightTier.Regular, From: 2560f),
        new TierSize("edge 1880 from above", 1880f, 760f, WidthTier.Huge, HeightTier.Regular, From: 2560f),
        new TierSize("edge 1919", 1919f, 760f, WidthTier.Standard, HeightTier.Regular),
        new TierSize("edge 1920", 1920f, 760f, WidthTier.Huge, HeightTier.Regular),
    }.ToDictionary(static s => s.Name, StringComparer.Ordinal);

    /// <summary>Every size, for the frame and for a page fixture of a key surface.</summary>
    public static TheoryData<string> FrameSizes() => Names(static _ => true);

    /// <summary>Every size but the 3840 one, for a page fixture that is not a key surface (plan 3.5: 3840 is for the key surfaces).</summary>
    public static TheoryData<string> PageSizes() => Names(static s => s.Width < 3840f);

    static TheoryData<string> Names(Func<TierSize, bool> keep)
    {
        var data = new TheoryData<string>();
        foreach (var s in Sizes.Values) if (keep(s)) data.Add(s.Name);
        return data;
    }

    // ── the page fixtures (lanes add theirs in their own partial files) ────────────────────────────────────────────────────

    /// <summary>A page the frame is checked with: its route and a node key (or key prefix) that is mounted once the page is on screen.</summary>
    public sealed record TierPage(string Name, Shell.Route Route, string ReadyKeyPrefix);

    /// <summary>The frame with Home (C-Shell-*): the route a fresh session opens on.</summary>
    public static readonly TierPage Home = new("home", new Shell.Route(Shell.RouteKind.Home), "home:column");

    // ── the frame's facts ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_frame_holds_its_tier_layout(string size) => AssertPage(Home, size);

    [Fact]
    public void The_size_table_reproduces_the_boards_ladder()
    {
        // A fresh size resolves to its row's tier on the shipped policy; a directional edge only from the side its row names.
        foreach (var s in Sizes.Values)
        {
            var facts = s.From is { } from
                ? WindowTiers.Resolve(ClassicTiers.Policy, s.Width, s.Height, s.Width, previous: WindowTiers.Resolve(ClassicTiers.Policy, from, s.Height, from))
                : WindowTiers.Resolve(ClassicTiers.Policy, s.Width, s.Height, s.Width);
            Assert.True(s.Window == facts.Window, $"{s.Name}: {facts.Window}");
            Assert.True(s.Tall == facts.Height, $"{s.Name}: {facts.Height}");
        }
    }

    // ── the helper every fixture runs ──────────────────────────────────────────────────────────────────────────────────

    static TierFacts Tiers => ThemeHost.Facts.Peek().Tiers;

    /// <summary>Mount the frame at <paramref name="sizeName"/> (resizing into it from its <c>From</c> width when it has one), show
    /// <paramref name="page"/>, run every frame assertion, then <paramref name="more"/> (the page's own record checks).</summary>
    internal void AssertPage(TierPage page, string sizeName, Action<Shell.HeadlessShell, TierFacts>? more = null)
    {
        var size = Sizes[sizeName];
        host.Mounted(shell =>
        {
            if (size.From is not null)
            {
                // Reached by a resize: the settled size's crossing commits on the first frame that is not resized.
                shell.RunFrames(20);
                shell.Resize(size.Width, size.Height);
                shell.Host.Paint(0, keepAlive: true);
                shell.Host.RunFrame();
            }
            // The tier is final in this frame, and the next one does not change it.
            AssertTier(size);
            shell.Host.RunFrame();
            AssertTier(size);

            if (page.Route.Kind != Shell.RouteKind.Home) Shell.GoTo(page.Route);
            Until(shell, () => shell.FindPrefix(page.ReadyKeyPrefix).Count > 0, page.Name + " on screen at " + size.Name);
            shell.RunFrames(40);   // entrances and the pane settle
            AssertTier(size);      // navigating never moves the tier

            var tiers = Tiers;
            AssertRecords(shell, size, tiers);
            AssertRegions(shell, size, tiers);
            if (size.Window == WidthTier.Compact) AssertCompactTargets(shell, size);
            more?.Invoke(shell, tiers);
        }, size.From ?? size.Width, size.Height);
    }

    static void AssertTier(TierSize size)
    {
        var t = Tiers;
        Assert.True(t.Window == size.Window, $"{size.Name}: the window tier is {t.Window}, not {size.Window}");
        Assert.True(t.Height == size.Tall, $"{size.Name}: the height tier is {t.Height}, not {size.Tall}");
        Assert.Same(ClassicTiers.Policy, t.Policy);
        // The facts are a snapshot taken at a crossing: a resize that crosses nothing (a held edge) keeps the mount's size in them.
        if (size.From is null) Assert.Equal(size.Width, t.ViewportWidth, 0.5f);
    }

    /// <summary>The records in use are the structure's for the tier: the frame's card, the page's gutter, the nav mode, the title bar's form.</summary>
    static void AssertRecords(Shell.HeadlessShell shell, TierSize size, TierFacts tiers)
    {
        var frame = ClassicTiers.FrameOf(tiers);
        Assert.Equal(size.Window, frame.Tier);
        Assert.Equal(size.Window == WidthTier.Compact, frame.SquareCard);
        Assert.Equal(ClassicTiers.Page(tiers.Content).Gutter, ClassicTiers.PageOf(tiers).Gutter);
        if (size.Window == WidthTier.Compact) Assert.Equal(16f, ClassicTiers.PageOf(tiers).Gutter);   // a Compact window's content is Compact too

        var mode = Sidebar.Mode.Peek();
        var expectedMode = size.Window switch
        {
            WidthTier.Compact => SidebarPaneMode.Minimal,      // the drawer
            WidthTier.Small => SidebarPaneMode.Compact,        // the forced 48 rail over the whole band
            _ => Sidebar.UserCollapsed.Peek() ? SidebarPaneMode.Compact : SidebarPaneMode.Expanded,
        };
        Assert.True(expectedMode == mode, $"{size.Name}: the nav pane is {mode}, not {expectedMode}");

        var row = Shell.ChromeLayout.Peek();
        Assert.True(row.TabCount == (size.Window == WidthTier.Compact), $"{size.Name}: the tab count form is {row.TabCount}");
        Assert.True(shell.Find("chrome-tab-count").IsNull != (size.Window == WidthTier.Compact), $"{size.Name}: the tab-count button");
        Assert.True(shell.Find("chrome-tab-strip").IsNull == (size.Window == WidthTier.Compact), $"{size.Name}: the tab strip");
        if (size.Width <= Shell.Layout.ChromeForwardEnterW) Assert.False(row.ShowForward, $"{size.Name}: Forward hides below 520");
        if (size.Window == WidthTier.Huge) Assert.Equal(240f, row.TabMaxW);    // the Huge budget's tab cap
    }

    /// <summary>The four top-level regions lie inside the window and never overlap; the page never runs wider than its card; at 330 tall
    /// the page region is at least the structure's ShortFloor.</summary>
    static void AssertRegions(Shell.HeadlessShell shell, TierSize size, TierFacts tiers)
    {
        var scene = shell.Host.Scene;
        RectF At(string key) => scene.AbsoluteRect(Node(shell, key));
        var title = At(ClassicFrameLayout.TitleBarKey);
        var nav = At(ClassicFrameLayout.NavColumnKey);
        var card = At(ClassicFrameLayout.ContentCardKey);
        var bar = At(ClassicFrameLayout.PlayerBarKey);

        foreach (var (name, r) in new[] { ("title bar", title), ("nav column", nav), ("content card", card), ("player bar", bar) })
        {
            Assert.True(r.X >= -0.5f && r.X + r.W <= size.Width + 0.5f, $"{size.Name}: the {name} runs outside the window horizontally ({r})");
            Assert.True(r.Y >= -0.5f && r.Y + r.H <= size.Height + 0.5f, $"{size.Name}: the {name} runs outside the window vertically ({r})");
        }
        Assert.True(title.Y + title.H <= card.Y + 0.5f, $"{size.Name}: the title bar overlaps the content card");
        Assert.True(card.Y + card.H <= bar.Y + 0.5f, $"{size.Name}: the content card overlaps the player bar");
        Assert.True(title.Y + title.H <= bar.Y + 0.5f, $"{size.Name}: the title bar overlaps the player bar");
        if (nav.W > 0.5f)
        {
            Assert.True(nav.X + nav.W <= card.X + 0.5f, $"{size.Name}: the nav column overlaps the content card");
            Assert.True(title.Y + title.H <= nav.Y + 0.5f, $"{size.Name}: the title bar overlaps the nav column");
            Assert.True(nav.Y + nav.H <= bar.Y + 0.5f, $"{size.Name}: the nav column overlaps the player bar");
        }
        else
        {
            Assert.Equal(0f, card.X, 0.5f);   // Compact: the card is flush to the window's left edge
        }

        // No page-level horizontal overflow: the page body never runs wider than the card it sits in.
        // Only the pages IN the card: a page the keep-alive parked after a navigation (Home, once a detail fixture is on screen) sits outside it.
        var inCard = new HashSet<NodeHandle>(Subtree(scene, Node(shell, ClassicFrameLayout.ContentCardKey)));
        foreach (var (node, key) in shell.FindPrefix("page:"))
        {
            var r = scene.AbsoluteRect(node);
            if (r.W <= 0f || !inCard.Contains(node)) continue;   // a parked page
            Assert.True(r.X >= card.X - 0.5f && r.X + r.W <= card.X + card.W + 0.5f, $"{size.Name}: {key} runs outside the content card ({r} in {card})");
        }

        if (size.Height <= TierPolicy.WindowMinHeight)
            Assert.True(card.H >= ClassicTiers.Policy.ShortFloor - 0.5f, $"{size.Name}: the page region is {card.H}, under the ShortFloor {ClassicTiers.Policy.ShortFloor}");
        Assert.Equal(size.Height, title.H + card.H + bar.H, 1f);   // the column is the window: nothing else takes height
    }

    /// <summary>At Compact every frame hit target is at least 40 x 40 (the brief's Compact rule). The identity chip is the profile chip's
    /// own view (the overlays lane's) and is dropped from a Compact row that cannot afford it.</summary>
    static void AssertCompactTargets(Shell.HeadlessShell shell, TierSize size)
    {
        var scene = shell.Host.Scene;
        var title = Node(shell, ClassicFrameLayout.TitleBarKey);
        var excluded = new HashSet<NodeHandle>();
        foreach (var (chip, _) in shell.FindPrefix("chrome-profile"))
            foreach (var n in Subtree(scene, chip)) excluded.Add(n);
        int targets = 0;
        foreach (var n in Subtree(scene, title))
        {
            if (excluded.Contains(n) || scene.InCollapsedSubtree(n)) continue;
            var flags = scene.Flags(n);
            if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
            var r = scene.AbsoluteRect(n);
            if (r.W <= 0f || r.H <= 0f) continue;
            targets++;
            Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{size.Name}: a title-bar target is {r.W} x {r.H}, under 40 x 40");
        }
        Assert.True(targets >= 2, $"{size.Name}: the Compact title bar offers its targets (found {targets})");
    }
}
