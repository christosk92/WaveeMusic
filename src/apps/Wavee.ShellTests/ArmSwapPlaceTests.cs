// ── Wavee.ShellTests/ArmSwapPlaceTests.cs — an arm swap keeps the user's place (themes plan 3.3; the owner's "no layout jumps") ──────────
//
// A tier crossing swaps a page's ARM (the detail frame's `detail:arm:vertical` ↔ `detail:arm:two-column`, the library's `lib:wide` ↔
// `lib:collapsed`) and cross-fades the two. The swap must not cost the user their place. On the real Classic frame, the window sized by hand
// the way the OS sizes it (the `TierResizeTests` rig, composited):
//
//   detail    a playlist scrolled to a row and two rows selected, across the vertical arm's edge (the page's 560, held 20) and back, and across
//             the 1008 window edge: the same row tops the list's visible band (under the vertical arm's pinned hero band, at the viewport's top
//             in the two-column arm), the selection survives (the selection bar stands), and the arriving arm fades in (it has tracks)
//   library   the albums navigator (a list and a grid) scrolled to an item and an album picked, across the 600 page edge and back: the same item
//             tops the navigator, the pick is the same, the album pane keeps its scroll, and the arriving arm fades in
//
// Rows and items are named by their DISPLAY index in the list's own layout (the vertical arm's two prefix items, the hero and the chrome, are
// not rows), and "tops the band" is "its laid-out start is the viewport's offset plus the band's inset", asserted on the virtual layout, never
// on pixels.

using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using Wavee;
using Wavee.Theming;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ArmSwapPlaceTests(ShellHost host)
{
    // pl0: the fake catalogue's 50-track playlist (pl1 has four), long enough to scroll in either arm.
    static readonly Shell.Route s_playlist = Shell.For(EntityUri.Parse("spotify:playlist:pl0"), "Playlist");
    const string Arm = "detail:arm:";

    // ── the rig (TierResizeTests) ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A size change that settles: the resized paint (the old tier at the new size), then the frames that commit the crossing.</summary>
    static void Resized(Shell.HeadlessShell shell, float widthDip, float heightDip)
    {
        shell.Resize(widthDip, heightDip);
        shell.Host.Paint(0, keepAlive: true);
    }

    /// <summary>After a resize: run frames until <paramref name="arm"/> is mounted, and require that it arrives ANIMATED (its Enter fade has
    /// tracks on the frame it appears), then let the cross-fade and every reveal settle.</summary>
    static void CrossInto(Shell.HeadlessShell shell, string arm)
    {
        Until(shell, () => !shell.Find(arm).IsNull, "the arm " + arm, maxFrames: 30);
        Assert.True(shell.Host.Animation.HasTracks(Node(shell, arm)), $"the arriving arm {arm} snapped in instead of fading in");
        shell.RunFrames(60);
    }

    // ── the list's own geometry ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The virtual viewport under <paramref name="root"/> (the scroller that has items).</summary>
    static NodeHandle VirtualViewport(Shell.HeadlessShell shell, NodeHandle root)
    {
        var scene = shell.Host.Scene;
        foreach (var n in Subtree(scene, root))
            if (scene.TryGetScroll(n, out var sc) && sc.ItemCount > 0 && sc.Layout is not null) return n;
        Assert.Fail("no virtual list under the node");
        return NodeHandle.Null;
    }

    static float Inset(in ScrollState sc) => float.IsFinite(sc.ItemClipTopInset) ? MathF.Max(0f, sc.ItemClipTopInset) : 0f;

    static float Cross(in ScrollState sc) => sc.ContentW > 0f ? sc.ContentW : sc.ViewportW;

    /// <summary>The item under the top of the visible band (a little under its edge, across the row: a grid's gutters hold no item) — what the
    /// leaving list hands the arriving one.</summary>
    static int ItemAtBandTop(SceneStore scene, NodeHandle vp)
    {
        Assert.True(scene.TryGetScroll(vp, out var sc));
        float cross = Cross(in sc), inset = Inset(in sc);
        for (float dy = 1f; dy <= 33f; dy += 8f)
            foreach (float rx in new[] { 0.5f, 0.25f, 0.1f })
            {
                var p = new Point2(rx * sc.ViewportW, sc.OffsetY + inset + dy);
                for (int i = 0; i < sc.ItemCount; i++)
                    if (sc.Layout!.ItemRect(i, cross).Contains(p)) return i;
            }
        return -1;
    }

    /// <summary>Scroll the list so <paramref name="item"/> starts exactly at the top of its visible band.</summary>
    static void ScrollItemToBandTop(Shell.HeadlessShell shell, NodeHandle vp, int item)
    {
        var scene = shell.Host.Scene;
        Assert.True(scene.TryGetScroll(vp, out var sc));
        float target = sc.Layout!.ItemRect(item, Cross(in sc)).Y - Inset(in sc);
        var handle = shell.Host.TryGetScrollHandle(vp);
        Assert.NotNull(handle);
        handle!.ScrollTo(target, ScrollMove.Immediate);
        shell.RunFrames(5);
        AssertTopsTheBand(scene, vp, item, "scrolled");
    }

    /// <summary><paramref name="item"/> starts at the top of the visible band (within a DIP).</summary>
    static void AssertTopsTheBand(SceneStore scene, NodeHandle vp, int item, string what)
    {
        Assert.True(scene.TryGetScroll(vp, out var sc));
        float start = sc.Layout!.ItemRect(item, Cross(in sc)).Y;
        float top = sc.OffsetY + Inset(in sc);
        Assert.True(MathF.Abs(start - top) <= 1f, $"{what}: item {item} starts at {start}, the band's top is {top} (offset {sc.OffsetY})");
    }

    /// <summary>The screen point at the centre of <paramref name="item"/> (where `TunedRowsMountedTests` clicks a row: the row takes the press).</summary>
    static Point2 RowPoint(SceneStore scene, NodeHandle vp, int item)
    {
        Assert.True(scene.TryGetScroll(vp, out var sc));
        var r = sc.Layout!.ItemRect(item, Cross(in sc));
        var at = scene.PresentedAbsoluteRect(vp);
        return new Point2(at.X + r.X + r.W / 2f, at.Y + r.Y - sc.OffsetY + r.H / 2f);
    }

    /// <summary>A screen point inside the part of <paramref name="item"/> its viewport shows (the middle of that part), scrolling the item's top to
    /// the band's top first when less than half of it shows: a click near a cell's edge can land outside the list.</summary>
    static Point2 ShownPoint(Shell.HeadlessShell shell, NodeHandle vp, int item)
    {
        var scene = shell.Host.Scene;
        Assert.True(scene.TryGetScroll(vp, out var sc));
        var r = sc.Layout!.ItemRect(item, Cross(in sc));
        if (ShownSpan(in sc, r.Y, r.H) < r.H * 0.5f)
        {
            var handle = shell.Host.TryGetScrollHandle(vp);
            Assert.NotNull(handle);
            handle!.ScrollTo(MathF.Max(0f, r.Y - Inset(in sc)), ScrollMove.Immediate);
            shell.RunFrames(5);
            Assert.True(scene.TryGetScroll(vp, out sc));
            r = sc.Layout!.ItemRect(item, Cross(in sc));
        }
        float top = MathF.Max(r.Y, sc.OffsetY + Inset(in sc));
        float bottom = MathF.Min(r.Y + r.H, sc.OffsetY + sc.ViewportH);
        Assert.True(bottom - top >= 8f, $"item {item} shows {bottom - top} DIP of its {r.H} in the viewport");
        var at = scene.PresentedAbsoluteRect(vp);
        return new Point2(at.X + r.X + r.W / 2f, at.Y + (top + bottom) / 2f - sc.OffsetY);
    }

    /// <summary>How much of the span [<paramref name="y"/>, + <paramref name="h"/>) the viewport shows (below its band's inset).</summary>
    static float ShownSpan(in ScrollState sc, float y, float h)
        => MathF.Max(0f, MathF.Min(y + h, sc.OffsetY + sc.ViewportH) - MathF.Max(y, sc.OffsetY + Inset(in sc)));

    static void ClickAt(Shell.HeadlessShell shell, Point2 p, KeyModifiers mods = KeyModifiers.None, uint at = 1000)
    {
        shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, mods));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, mods, TimestampMs: at));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, mods, TimestampMs: at + 10));
        shell.RunFrames(2);
    }

    /// <summary>The bar's selection arm stands while two or more rows are selected (<c>SelectionBarRules.Visible</c>).</summary>
    static bool SelectionBarShown(Shell.HeadlessShell shell) => shell.FindPrefix("commandbar-mode:selection").Count > 0;

    // ── the detail frame ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_detail_arm_swap_keeps_the_first_visible_row_and_the_selection()
        => host.Mounted(shell =>
        {
            shell.Window.Composited = true;
            Shell.GoTo(s_playlist);
            // A wall-clock wait, not a frame budget: a Spotify playlist this process never revalidated opens RevalidateThenPaint and the page HOLDS its
            // rows (the shimmer) on that /diff for up to ListFreshness.BlockingBudgetMs (1500 ms of the Stopwatch clock, ListOpen); the offline host
            // does not settle it sooner, and 240 headless frames run in a fraction of that.
            UntilBackground(shell, () => !shell.Find(Arm + "two-column").IsNull && shell.FindPrefix("list:").Count == 1 && shell.FindPrefix("row").Count > 4,
                  "the playlist's two-column arm and its rows");
            shell.RunFrames(60);
            var scene = shell.Host.Scene;

            // The list scrolled to row 10, then rows 11 and 12 picked (a click, then a Ctrl-click: Extended selection).
            const int Row = 10;
            var vp = VirtualViewport(shell, Single(shell, "list:").Node);
            Assert.True(scene.TryGetScroll(vp, out var two));
            Assert.Equal(0, two.PersistentPrefixCount);
            ScrollItemToBandTop(shell, vp, Row);
            Assert.Equal(Row, ItemAtBandTop(scene, vp));
            ClickAt(shell, RowPoint(scene, vp, Row + 1));
            ClickAt(shell, RowPoint(scene, vp, Row + 2), KeyModifiers.Ctrl, at: 2000);
            shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(4f, 4f), 0, 0));
            shell.RunFrames(20);
            Assert.True(SelectionBarShown(shell), "two rows selected: the selection bar stands");

            // ── into the vertical arm: the page falls under 560 ──
            Resized(shell, 400f, 760f);
            Assert.False(shell.Find(Arm + "two-column").IsNull, "the resized frame still lays out the old arm");
            CrossInto(shell, Arm + "vertical");
            Until(shell, () => shell.FindPrefix(Arm).Count == 1 && shell.FindPrefix("list:").Count == 1, "the leaving arm gone");
            vp = VirtualViewport(shell, Single(shell, "list:").Node);
            Assert.True(scene.TryGetScroll(vp, out var vertical));
            int prefix = vertical.PersistentPrefixCount;   // the hero and the chrome
            Assert.True(prefix > 0, "the vertical arm's list carries the hero as its prefix");
            AssertTopsTheBand(scene, vp, Row + prefix, "the vertical arm");
            Assert.True(SelectionBarShown(shell), "the vertical arm kept the selection");

            // ── and back: the page clears 580 again ──
            Resized(shell, 800f, 760f);
            CrossInto(shell, Arm + "two-column");
            Until(shell, () => shell.FindPrefix(Arm).Count == 1 && shell.FindPrefix("list:").Count == 1, "the leaving arm gone");
            vp = VirtualViewport(shell, Single(shell, "list:").Node);
            AssertTopsTheBand(scene, vp, Row, "back in the two-column arm");
            Assert.True(SelectionBarShown(shell), "the two-column arm kept the selection");

            // ── across 1008 (Small → Standard): the same arm, the same list ──
            var before = vp;
            Resized(shell, 1180f, 760f);
            shell.Host.RunFrame();
            shell.RunFrames(60);
            Assert.False(shell.Find(Arm + "two-column").IsNull);
            vp = VirtualViewport(shell, Single(shell, "list:").Node);
            Assert.True(vp == before, "a window crossing inside the two-column arm keeps the list's viewport");
            Assert.Equal(Row, ItemAtBandTop(scene, vp));
            Assert.True(SelectionBarShown(shell), "the Standard window kept the selection");
        }, 800f, 760f);

    // ── the library ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Store the navigator's view code (list 1, grid 3), run <paramref name="body"/>, put the kind's state back.</summary>
    void WithAlbumsView(int view, Action body)
    {
        LibraryViewState saved = default;
        host.Run(() =>
        {
            saved = LibraryViewPrefs.Read("albums");
            LibraryViewPrefs.Save("albums", saved with { View = view }, artists: false);
        });
        try { body(); }
        finally { host.Run(() => LibraryViewPrefs.Save("albums", saved, artists: false)); }
    }

    /// <summary>The scroller with the most room under <paramref name="root"/> (the album pane's own scroller, not its nested natural lists).</summary>
    static ScrollHandle? MainScroller(Shell.HeadlessShell shell, NodeHandle root)
    {
        ScrollHandle? best = null;
        foreach (var n in Subtree(shell.Host.Scene, root))
        {
            if (!shell.Host.Scene.HasScroll(n) || shell.Host.TryGetScrollHandle(n) is not { Horizontal: false } h) continue;
            if (best is null || h.MaxOffset > best.MaxOffset) best = h;
        }
        return best;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void A_library_arm_swap_keeps_the_first_visible_item_the_pick_and_the_pane_scroll(int view)
        => WithAlbumsView(view, () => host.Mounted(shell =>
        {
            shell.Window.Composited = true;
            string mount = "nav:" + view.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
            Shell.GoTo(new Shell.Route(Shell.RouteKind.LibraryAlbums));
            Until(shell, () => !shell.Find("lib:wide").IsNull && shell.FindPrefix(mount).Count == 1, "the split library and its navigator");
            shell.RunFrames(60);
            var scene = shell.Host.Scene;
            var vp = VirtualViewport(shell, shell.FindPrefix(mount)[0].Node);
            Assert.True(scene.TryGetScroll(vp, out var sc));
            Assert.True(sc.ItemCount > 12, $"the fake library has {sc.ItemCount} albums, too few to scroll");

            // Pick the third item, then scroll the navigator so the item at the band's top is a few rows down. The click lands inside the part of
            // the item the navigator SHOWS: at this 560-tall window the Small navigator's grid (240 wide, two 96-floor cards a row under its 156 head)
            // ends about where the second row's foot is, so a point near the cell's foot can fall under the viewport and pick nothing.
            string first = LibraryViewPrefs.Read("albums").SelectedKey;
            ClickAt(shell, ShownPoint(shell, vp, 2));
            shell.RunFrames(20);
            string picked = LibraryViewPrefs.Read("albums").SelectedKey;
            Assert.False(string.IsNullOrEmpty(picked), "the pick is the page's");
            Assert.NotEqual(first, picked);
            ScrollItemToBandTop(shell, vp, view == 3 ? ItemOfRow(scene, vp, 1) : 4);
            int top = ItemAtBandTop(scene, vp);

            // The pane's own scroll (the picked album's rows and its trailing band).
            Until(shell, () => !shell.Find("lib:detail").IsNull, "the album pane");
            shell.RunFrames(30);
            var pane = MainScroller(shell, Node(shell, "lib:detail"));
            Assert.NotNull(pane);
            Assert.True(pane!.MaxOffset > 8.0, $"the album pane does not scroll at this height ({pane.MaxOffset})");
            double paneOffset = Math.Min(pane.MaxOffset, 120.0);
            pane.ScrollTo(paneOffset, ScrollMove.Immediate);
            shell.RunFrames(5);

            // ── into the one column: the page falls under 600 ──
            Resized(shell, 400f, 560f);
            Assert.False(shell.Find("lib:wide").IsNull, "the resized frame still lays out the split");
            CrossInto(shell, "lib:collapsed");
            Until(shell, () => shell.Find("lib:wide").IsNull && shell.FindPrefix(mount).Count == 1, "the split gone");
            Assert.False(shell.Find("depth:0").IsNull, "a crossing into the one column opens at the navigator");
            vp = VirtualViewport(shell, shell.FindPrefix(mount)[0].Node);
            AssertTopsTheBand(scene, vp, top, "the one column");
            Assert.Equal(picked, LibraryViewPrefs.Read("albums").SelectedKey);

            // ── and back: the split again, the same item at the top, the pane where it was ──
            Resized(shell, 800f, 560f);
            CrossInto(shell, "lib:wide");
            Until(shell, () => shell.Find("lib:collapsed").IsNull && shell.FindPrefix(mount).Count == 1, "the one column gone");
            vp = VirtualViewport(shell, shell.FindPrefix(mount)[0].Node);
            AssertTopsTheBand(scene, vp, top, "the split again");
            Assert.Equal(picked, LibraryViewPrefs.Read("albums").SelectedKey);
            Until(shell, () => !shell.Find("lib:detail").IsNull, "the album pane again");
            shell.RunFrames(30);
            var paneAgain = MainScroller(shell, Node(shell, "lib:detail"));
            Assert.NotNull(paneAgain);
            // Where it was, within the pane's range now: the offset the test picks can be the old maximum, and a re-mounted pane may measure
            // a pixel or two shorter (text measured afresh), which clamps it.
            double expected = Math.Min(paneOffset, paneAgain!.MaxOffset);
            Assert.True(Math.Abs(paneAgain.LastShown - expected) <= 1.0, $"the pane's scroll is {paneAgain.LastShown}, it was {paneOffset} (max now {paneAgain.MaxOffset})");
        }, 800f, 560f));

    /// <summary>The first item of the grid's row <paramref name="row"/> (rows counted by distinct item tops).</summary>
    static int ItemOfRow(SceneStore scene, NodeHandle vp, int row)
    {
        Assert.True(scene.TryGetScroll(vp, out var sc));
        float cross = Cross(in sc);
        float lastTop = float.NaN;
        int rows = -1;
        for (int i = 0; i < sc.ItemCount; i++)
        {
            float y = sc.Layout!.ItemRect(i, cross).Y;
            if (y != lastTop) { rows++; lastTop = y; }
            if (rows == row) return i;
        }
        Assert.Fail("the grid has fewer than " + (row + 1) + " rows");
        return -1;
    }
}
