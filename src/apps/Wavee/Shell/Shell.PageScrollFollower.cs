// ── Shell/Shell.PageScrollFollower.cs — the content host names each page's main scroller to the page-scroll signal (P5 Z2b) ─────
//
// Role: CORE (the content host's half of `PageChromeSink.ReportScroll`, contract 1.26)
//
// A band-style structure (Zune) collapses its band while the page scrolls down (`BandProps.Scroll`, `Shell.PageChromeChannel.Scroll`).
// ZK built the signal and the sink but left the reporting to "pages or templates", and none reports yet. Rather than teach every page
// (and every theme's page template) to report, the CONTENT HOST names the page's main scroller itself: `PageBody` wraps the page in
// this follower, which finds the first VERTICAL scroller in the page's subtree (pre-order, so the outermost one, preferring one whose
// `ScrollKey` carries the page's scroll scope, "tabN/...": every page scroller composes its key onto `Shell.PageScrollScope`) and reports
// its `ScrollHandle.Offset` under the route on screen. A page that names its scroller through the sink itself still wins: whichever
// reports last for the route is followed, and a page reports from its own effect after this follower's mount scan.
//
//   · WHEN. On mount, and again on a KeepAlive reactivation, retried every `RetryMs` until a scroller is found (a page's real scroller often
//     mounts after its first frame: a skeleton, a deferred list), at most `MaxTries` times. Keyed scrollers only for the first
//     `KeyedOnlyTries`, then any vertical scroller (a page whose scroller carries no key).
//   · WHICH ROUTE. The route on screen when it maps to this page's slot (Home's facets change the route's argument inside one mounted
//     page), so the signal is found under `KeyOf(Current)`, which is what `FollowScroll` reads.
//   · UNMOUNT. The report is withdrawn, so a stale offset never reads as "scrolled down" on a page without a scroller.
//
// Cost: the scan walks only subtrees that hold a scroller (`SceneStore.HasScrollInSubtree`), a few times per navigation, never per frame.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Shell
{
    /// <summary>The follower's cadence (P5 Z2b).</summary>
    public static class PageScrollFollow
    {
        /// <summary>The retry period while no scroller is found.</summary>
        public const float RetryMs = 150f;

        /// <summary>How many scans before giving up (about 6 s).</summary>
        public const int MaxTries = 40;

        /// <summary>How many scans accept only a scroller keyed with the page's scope (then any vertical scroller).</summary>
        public const int KeyedOnlyTries = 4;
    }

    /// <summary>Wraps a page so the content host names its main scroller to the page-scroll signal.</summary>
    static Element FollowPageScroll(in Route route, Element page)
        => Embed.Comp(new PageScrollFollower.Props(route, ScrollScopeOf(route.Tab), page), static () => new PageScrollFollower());

    /// <summary>See the file header. Renders its page unchanged (its host node is the page's root).</summary>
    sealed class PageScrollFollower : Component
    {
        public sealed record Props(Route Route, string Scope, Element Page);

        Props? _p;
        ScrollHandle? _adopted;
        NodeHandle _node;
        Route _reported = Route.None;
        int _tries;
        TimerHandle _timer;
        Action? _retry;

        public override Element Render()
        {
            var p = UseProps<Props>();
            _p = p;
            _retry ??= Retry;
            _timer = UseTimeout(_retry, PageScrollFollow.RetryMs, DepKey.Empty);
            // The first scan right after the page's first paint; the timer retries while none is found.
            UseEffect(() => { if (TryAdopt()) _timer.Cancel(); }, DepKey.Empty);
            UseActivation(onActivated: () => { _tries = 0; Retry(); });
            // Follow the route on screen while it is this page's (an argument change inside one mounted page re-keys the report).
            UseEffect(() =>
            {
                var current = Current.Value;
                if (_adopted is { } handle && _p is { } q && string.Equals(SlotKey(current), SlotKey(q.Route), StringComparison.Ordinal))
                    Report(current, handle);
            });
            UseEffect(() => () =>
            {
                if (!_reported.IsNone) PageChromeChannel.ReportScroll(_reported, null);
                _reported = Route.None;
            }, DepKey.Empty);
            return p.Page;
        }

        void Report(in Route route, ScrollHandle handle)
        {
            if (!_reported.IsNone && !RouteEquals(_reported, route)) PageChromeChannel.ReportScroll(_reported, null);
            _reported = route;
            PageChromeChannel.ReportScroll(route, handle.Offset);
        }

        static bool RouteEquals(in Route a, in Route b)
            => a.Kind == b.Kind && a.Tab == b.Tab && string.Equals(NameOf(a), NameOf(b), StringComparison.Ordinal)
               && string.Equals(ArgOf(a), ArgOf(b), StringComparison.Ordinal);

        void Retry()
        {
            if (TryAdopt()) return;
            if (++_tries < PageScrollFollow.MaxTries) _timer.Restart();
        }

        bool TryAdopt()
        {
            var scene = Context.Scene;
            if (scene is null || _p is not { } p) return false;
            var current = Current.Peek();
            var route = string.Equals(SlotKey(current), SlotKey(p.Route), StringComparison.Ordinal) ? current : p.Route;
            if (_adopted is { IsBound: true } kept && !_node.IsNull && scene.IsLive(_node))
            {
                Report(route, kept);   // a reactivation names it again (a no-op when it is still the route's scroller)
                return true;
            }
            var root = Context.HostNode;
            if (root.IsNull || !scene.IsLive(root)) return false;
            var found = Find(scene, root, p.Scope, keyedOnly: _tries < PageScrollFollow.KeyedOnlyTries);
            if (found.IsNull || scene.ScrollHandleFor(found) is not { Horizontal: false } handle) return false;
            _node = found;
            _adopted = handle;
            Report(route, handle);
            return true;
        }

        /// <summary>The first vertical scroller under <paramref name="root"/> in pre-order (the outermost), preferring one keyed with
        /// <paramref name="scope"/>; any vertical one unless <paramref name="keyedOnly"/>.</summary>
        static NodeHandle Find(SceneStore scene, NodeHandle root, string scope, bool keyedOnly)
        {
            NodeHandle any = NodeHandle.Null;
            var stack = new Stack<NodeHandle>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.IsNull || !scene.IsLive(n) || !scene.HasScrollInSubtree(n)) continue;
                if (scene.TryGetScroll(n, out var sc) && sc.Orientation == 0)
                {
                    if (sc.ScrollKey is { } key && key.StartsWith(scope, StringComparison.Ordinal)) return n;
                    if (any.IsNull) any = n;
                }
                // Children pushed last-first, so the first child is visited first (pre-order).
                var kids = new List<NodeHandle>();
                for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) kids.Add(c);
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
            }
            return keyedOnly ? NodeHandle.Null : any;
        }
    }
}
