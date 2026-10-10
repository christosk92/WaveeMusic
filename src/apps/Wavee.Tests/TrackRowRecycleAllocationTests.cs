// ── Wavee.Tests/TrackRowRecycleAllocationTests.cs — a recycled track row re-renders nothing and allocates (almost) nothing ──
//
// THE DEFECT (the real Q-top 1500 thumb drag, 2026-09-25: `scroll.frames comps=4495 hotAllocKB=76109 gc=20/11/4` over 900
// frames, ~17 KB per rendered row component; an EventPipe allocation-tick session over four drag legs attributed 35 MB to
// `TableSlot.Render` — `TableHost.Skin` 17 MB of BoxEl, the slot's own BoxEl 8 MB, the check lane 5 MB — and 4.5 MB to
// `TableVerticalItem.Render`). Both slots read `_scope.Index.Value` in Render, so EVERY recycle (the virtualizer writing a new
// index into a realized slot) re-rendered them and rebuilt the whole row skin: a dozen closures, bound props, three boxes,
// the check lane, the drag source and the context menu — work whose only input that changed was an index the bound props
// already read on their own. The engine's recycler contract (docs/site/app-authors/virtualized-lists-and-itemsview.md:
// "slots recycle positionally and rebind by signal") is that a recycle allocates nothing in the row's components.
//
// The fact, measured on the real table in a headless host (the fake catalog's 166 liked songs, the flat arm): scrolling the
// list so that every realized slot recycles many times re-renders no row component and allocates at most a small bounded
// number of bytes per recycled row.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class TrackRowRecycleAllocationTests
{
    /// <summary>What one recycled row may allocate on the UI thread: its new title/credit text laid out and shaped, the
    /// presentation memo and bound props re-evaluating, the realize bookkeeping — the same order as the artist chart's
    /// rows (~5 KB per row in the live drag). MEASURED 2026-09-25 on this test: 5,351 B after the fix, 20,874 B before
    /// it (comps 10 vs 151 over the same 10 steps). 7 KB keeps ~30 % headroom and fails the re-render class 3× over.</summary>
    const long BudgetBytesPerRecycledRow = 7 * 1024;

    /// <summary>What a step may mount on top of the recycle rule once the date groups are on. A header row and a plain row
    /// recycle from separate pools (<c>LikedGroups.PoolOf</c>), so a window whose header count differs from the last one
    /// builds a header slot (TableSlot + row content + tooltip, ~3 components) in place of an incompatible leaver - at most a
    /// couple per step on this list (a group every ~14 rows). Four per step still fails the re-render class (15 per step)
    /// by a factor of ~4.</summary>
    const int GroupedCompsPerStep = 4;

    sealed class LikedTable(Detail.Config config) : Component
    {
        public override Element Render()
            => new BoxEl
            {
                Width = 900f, Height = 600f, Direction = 1,
                Children =
                [
                    Track.Table(new Track.TableArgs
                    {
                        Source = Track.TableSource.ForLiked(new User(Entities.Current.MeSlot)),
                        Profile = Track.TableProfile.From(config),
                        ShowToolbar = false, Embedded = true, ScrollKey = "recycle-alloc",
                    }),
                ],
            };
    }

    static NodeHandle FindList(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.HasScroll(n) && s.TryGetScroll(n, out var sc) && sc.ItemCount > 20) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var found = FindList(s, c);
            if (!found.IsNull) return found;
        }
        return NodeHandle.Null;
    }

    /// <summary>The recycle rule on the plain list: Liked's data and defaults with the date groups OFF (no HoverHeart), so
    /// every row has one shape and a recycle re-renders nothing.</summary>
    [Fact]
    public void Scrolling_recycles_track_rows_without_re_rendering_or_allocating_them()
        => Run(Detail.Config.Liked with { HoverHeart = false, PlainRows = false }, compsPerStep: 1);

    /// <summary>The same sweep on the real Liked list, groups ON (Date added, the default): rows still allocate within the
    /// per-row budget, and no row re-renders on a recycle - the only extra components are the header slots a window mounts
    /// when it holds more group starts than the window before it (<see cref="GroupedCompsPerStep"/>).</summary>
    [Fact]
    public void Scrolling_the_grouped_liked_list_recycles_rows_within_the_same_budget()
        => Run(Detail.Config.Liked, GroupedCompsPerStep);

    static void Run(Detail.Config config, int compsPerStep)
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(1_790_000_000);

        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("recycle-alloc", new Size2(900f, 600f), 1f));
        window.Show();
        var dev = new HeadlessGpuDevice();
        var strings = Entities.Strings;
        using var host = new AppHost(app, window, dev, new HeadlessFontSystem(strings), strings, new LikedTable(config));
        for (int i = 0; i < 30; i++) host.RunFrame();

        var list = FindList(host.Scene, host.Scene.Root);
        Assert.False(list.IsNull, "the liked table's virtual list did not mount");
        var handle = host.TryGetScrollHandle(list);
        Assert.NotNull(handle);
        host.Scene.TryGetScroll(list, out var sc0);
        Assert.True(sc0.ItemCount >= 150, $"items={sc0.ItemCount}");
        double max = Math.Max(0.0, sc0.ContentH - sc0.ViewportH);
        Assert.True(max > 3000, $"extent={sc0.ContentH} viewport={sc0.ViewportH}");

        // Warm-up: one full pass down and back (JIT, pools, the slots' first render at each shape).
        Sweep(host, handle!, max, out _, out _);
        Sweep(host, handle!, max, out _, out _);

        // Measured: a pass that recycles every realized slot many times over.
        long before = GC.GetAllocatedBytesForCurrentThread();
        Sweep(host, handle!, max, out int comps, out int steps);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        host.Scene.TryGetScroll(list, out var sc);
        int realized = Math.Max(1, sc.LastRealized - sc.FirstRealized);
        // Each step moves a whole viewport, so every realized slot takes a new index: steps × realized recycles.
        long recycles = (long)steps * realized;
        long perRow = bytes / Math.Max(1, recycles);
        string facts = $"comps={comps} over {steps} steps ({recycles} recycles), {perRow} B per recycled row ({bytes} B)";
        Assert.True(comps <= steps * compsPerStep, "row components re-rendered on recycle: " + facts);
        Assert.True(perRow <= BudgetBytesPerRecycledRow, $"over the {BudgetBytesPerRecycledRow} B budget: " + facts);
    }

    static void Sweep(AppHost host, ScrollHandle handle, double max, out int comps, out int steps)
    {
        comps = 0; steps = 0;
        const double Step = 700.0;               // > the 600-DIP viewport: every realized row is replaced each step
        for (double at = Step; at <= max; at += Step)
        {
            handle.ScrollTo(at, ScrollMove.Immediate);
            comps += host.RunFrame().ComponentsRendered;
            comps += host.RunFrame().ComponentsRendered;
            steps++;
        }
        handle.ScrollTo(0.0, ScrollMove.Immediate);
        host.RunFrame();
        host.RunFrame();
    }
}
