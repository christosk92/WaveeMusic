// ── Screens/Diagnostics.Inspect.Visible.cs — what of a node a person can see: its visible rect and its on-screen fraction ───────
// visible = presented rect ∩ window ∩ every clipping ancestor ∩ every scroll viewport ∩ every authored clip (a sticky or collapse cut)
//
// Role: CORE for the snapshot half (pure: a depth-first node list in, rects and fractions out), SHELL for `TryGetVisibleRect` (reads a mounted
// scene on the UI thread, between frames). Spec: scratch/agentic-e2e/PLAN.md 5.1 ("judge what a person sees") and 5.5 (scope).
//
// WHY. The scene keeps far more than the screen shows: below-the-fold scroll content, cards a shelf scrolled away, a closed side panel placed
// past the window's edge. Only about one node in ten of a Zune home dump is ink on screen, so a rule that reads presented rects alone judges
// what nobody sees (a "duplicate" 2,500 DIP below the fold, a parked panel's 28-DIP button). Every lint rule reads `OnScreen` instead.
//
// THE THREE SCOPES (`lint scope=`): `screen` (the default) keeps a node when at least half of its rect is visible, or when what is visible
// of it fills at least half of the viewport it sits in (a page column taller than two screens) and that viewport is itself on screen (a
// card cut to a sliver at the fold is no viewport: what fills it is a sliver too); `content` keeps what a person reaches by scrolling (below
// a scroller nothing cuts along its axis, every other clip and the window still do: a page audit); `all` keeps everything the capture
// walked (the lint before this file). A modal scrim (an interactive fill over the whole window) and an opaque fill painted over earlier
// nodes (a menu's plate) hide what they cover: the lint marks those nodes covered (Diagnostics.Inspect.Lint.cs `Prepare`).
//
// VISIBLE IS NOT ACTIONABLE. A visible rect says what shows, not what a click reaches: `TryGetActionable` adds the hit test (the target, or a
// node of its own chain, must be what a click at the centre of its visible rect lands on).
//
// NOT HERE. Hidden, parked, exiting and fully transparent subtrees never reach the snapshot (the capture skips them), so the pure half never
// sees them; `TryGetVisibleRect` checks them itself because it starts from any live node.

using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Scene;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>Visible rects and on-screen fractions (see the file header).</summary>
    public static class InspectVisible
    {
        /// <summary>What a lint judges: what is on screen, what scrolling reaches, or everything captured.</summary>
        public enum Scope : byte { Screen, Content, All }

        /// <summary>The share of its rect a node must show to count as on screen (and as reached, under <see cref="Scope.Content"/>).</summary>
        public const float MinFraction = 0.5f;

        public static string Name(Scope scope) => scope switch { Scope.Content => "content", Scope.All => "all", _ => "screen" };

        public static bool TryParseScope(string word, out Scope scope)
        {
            switch (word.Trim().ToLowerInvariant())
            {
                case "screen" or "": scope = Scope.Screen; return true;
                case "content": scope = Scope.Content; return true;
                case "all": scope = Scope.All; return true;
                default: scope = Scope.Screen; return false;
            }
        }

        /// <summary>The share of <paramref name="rect"/>'s area that <paramref name="visible"/> (a part of it) covers: 0 for an empty rect.</summary>
        public static float Fraction(in RectF rect, in RectF visible)
        {
            float area = rect.W * rect.H;
            if (!(area > 0f) || visible.IsEmpty) return 0f;
            return Math.Clamp(visible.W * visible.H / area, 0f, 1f);
        }

        /// <summary>How far under <see cref="MinFraction"/> a fraction may round and still count: a row exactly half visible stays in scope after
        /// a saved snapshot's rects round-trip.</summary>
        const float FractionSlack = 1e-3f;

        /// <summary>Is a node with these fractions in <paramref name="scope"/>? <paramref name="cover"/> is the share of the viewport it sits in
        /// that its visible part fills (0 when that viewport is not on screen itself): a container taller than twice its viewport (a page column,
        /// a long section) shows under half of itself yet fills the screen, so it is judged too.</summary>
        public static bool InScope(Scope scope, float onScreen, float inContent, float cover = 0f) => scope switch
        {
            Scope.All => true,
            Scope.Content => inContent >= MinFraction - FractionSlack || cover >= MinFraction - FractionSlack,
            _ => onScreen >= MinFraction - FractionSlack || cover >= MinFraction - FractionSlack,
        };

        /// <summary>What a node passes on to its children: one that clips or scrolls cuts the screen to its rect; below a scroller nothing cuts
        /// the content along its axis (scrolling reaches it) while its cross axis still does; an authored clip cuts both. The one step the pure
        /// half runs per node and <see cref="AncestorCut"/> runs per live ancestor.</summary>
        public static void CutChildren(in RectF rect, bool clips, int scrollAxis, in RectF clipRect, ref RectF screen, ref RectF content)
        {
            if (clips || scrollAxis != 0) screen = screen.Intersect(rect);
            if (scrollAxis == 2) content = Unbounded(content.Intersect(rect), vertical: true);
            else if (scrollAxis == 1) content = Unbounded(content.Intersect(rect), vertical: false);
            else if (clips) content = content.Intersect(rect);
            if (!clipRect.IsInfinite)
            {
                screen = screen.Intersect(clipRect);
                content = content.Intersect(clipRect);
            }
        }

        // ── the snapshot half (pure) ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>The visible rect, the on-screen fraction and the content fraction of every node of a depth-first list (a parent before its
        /// children, <c>Parent</c> an earlier index or -1) in a window of <paramref name="windowW"/> x <paramref name="windowH"/> DIP.
        /// A node is cut by what its ANCESTORS clip (a box's own clip cuts its children, never itself): <c>Clips</c>, a scroll viewport
        /// (<c>ScrollAxis</c> != 0) and an authored <c>ClipRect</c>. The content fraction lifts, below a scroller, every cut along its axis (the
        /// window and whatever clips above the viewport, the viewport included): scrolling brings that content into view; a panel parked past
        /// the window's edge stays out. A root (<c>Parent</c> -1) starts from the cut its live ancestors put on it when it is <c>Seeded</c>
        /// (<c>SeedScreen</c>, <c>SeedContent</c>, <c>SeedShown</c>: set by a `root=` capture), else from the window.</summary>
        public static void Compute(IReadOnlyList<InspectLint.Node> nodes, float windowW, float windowH,
            Span<RectF> visible, Span<float> onScreen, Span<float> inContent)
            => Compute(nodes, windowW, windowH, visible, onScreen, inContent, new RectF[nodes.Count], new float[nodes.Count]);

        /// <summary>The same, plus each node's REACH (its rect cut by what scrolling cannot open: what a page audit judges) and its COVER (the
        /// share of the viewport it sits in that its visible part fills; see <see cref="InScope"/>).</summary>
        public static void Compute(IReadOnlyList<InspectLint.Node> nodes, float windowW, float windowH,
            Span<RectF> visible, Span<float> onScreen, Span<float> inContent, Span<RectF> reach, Span<float> cover)
        {
            int count = nodes.Count;
            if (visible.Length < count || onScreen.Length < count || inContent.Length < count || reach.Length < count || cover.Length < count)
                throw new ArgumentException("the output spans are shorter than the node list");
            var window = new RectF(0f, 0f, windowW, windowH);
            var screenCut = new RectF[count];
            var contentCut = new RectF[count];
            var portShown = new bool[count];   // what a node passes on: is the viewport its children sit in on screen itself?
            for (int i = 0; i < count; i++)
            {
                InspectLint.Node n = nodes[i];
                int p = n.Parent;
                bool inherited = p >= 0 && p < i;
                RectF inheritedScreen = inherited ? screenCut[p] : n.Seeded ? n.SeedScreen : window;
                RectF inheritedContent = inherited ? contentCut[p] : n.Seeded ? n.SeedContent : window;
                bool shown = inherited ? portShown[p] : !n.Seeded || n.SeedShown;
                RectF r = n.Rect;
                RectF seen = r.Intersect(inheritedScreen);
                RectF reached = r.Intersect(inheritedContent);
                visible[i] = seen;
                onScreen[i] = Fraction(r, seen);
                reach[i] = reached;
                inContent[i] = Fraction(r, reached);
                cover[i] = shown ? Fraction(inheritedScreen.Intersect(window), seen) : 0f;

                // What this node cuts its descendants to. One that cuts is the viewport they sit in: shown when it is on screen itself.
                RectF childScreen = inheritedScreen, childContent = inheritedContent;
                CutChildren(r, n.Clips, n.ScrollAxis, n.ClipRect, ref childScreen, ref childContent);
                screenCut[i] = childScreen;
                contentCut[i] = childContent;
                portShown[i] = Cuts(n.Clips, n.ScrollAxis, n.ClipRect) ? InScope(Scope.Screen, onScreen[i], 0f, cover[i]) : shown;
            }
        }

        /// <summary>A node that cuts its children (it clips, scrolls or carries an authored clip): the viewport they sit in.</summary>
        static bool Cuts(bool clips, int scrollAxis, in RectF clipRect) => clips || scrollAxis != 0 || !clipRect.IsInfinite;

        /// <summary><paramref name="cut"/> opened along one axis: what a scroller's content reaches by scrolling (the cross axis stays cut).</summary>
        static RectF Unbounded(in RectF cut, bool vertical)
        {
            if (cut.IsEmpty) return cut;
            RectF open = RectF.Infinite;
            return vertical ? new RectF(cut.X, open.Y, cut.W, open.H) : new RectF(open.X, cut.Y, open.W, cut.H);
        }

        // ── the live half (UI thread) ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The cut the live ancestors of <paramref name="node"/> put on it, as the pure half derives it below a root: the window, then
        /// every ancestor from the top down (<see cref="CutChildren"/>, on presented rects as the capture reads them). A hidden, parked, exiting
        /// or transparent ancestor cuts everything. What a `root=` capture seeds its root with, so a subtree is judged as the page shows it.</summary>
        public static void AncestorCut(SceneStore scene, NodeHandle node, in RectF window, out RectF screen, out RectF content)
            => AncestorCut(scene, node, window, out screen, out content, out _);

        /// <summary>The same, plus <paramref name="shown"/>: is the viewport the node sits in (its nearest ancestor that cuts) on screen itself?
        /// When it is not, the node's cover counts for nothing (a `root=` subtree under a card cut to a sliver is a sliver).</summary>
        public static void AncestorCut(SceneStore scene, NodeHandle node, in RectF window, out RectF screen, out RectF content, out bool shown)
        {
            screen = window;
            content = window;
            shown = true;
            var chain = new List<NodeHandle>();
            for (NodeHandle a = scene.Parent(node); !a.IsNull; a = scene.Parent(a)) chain.Add(a);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                NodeHandle a = chain[i];
                if (Hidden(scene, a))
                {
                    screen = default;
                    content = default;
                    shown = false;
                    return;
                }
                RectF ar = scene.PresentedAbsoluteRect(a);
                RectF authored = scene.Paint(a).ClipRect;
                RectF clip = authored.IsInfinite ? RectF.Infinite : new RectF(ar.X + authored.X, ar.Y + authored.Y, authored.W, authored.H);
                int axis = scene.TryGetScroll(a, out ScrollState scroll) ? (scroll.Orientation == 1 ? 1 : 2) : 0;
                bool clips = (scene.Flags(a) & NodeFlags.ClipsToBounds) != 0;
                RectF seen = ar.Intersect(screen);
                bool onScreen = InScope(Scope.Screen, Fraction(ar, seen), 0f, shown ? Fraction(screen.Intersect(window), seen) : 0f);
                CutChildren(ar, clips, axis, clip, ref screen, ref content);
                if (Cuts(clips, axis, clip)) shown = onScreen;
            }
        }

        /// <summary>The visible rect of a mounted node in window DIP and the share of its painted rect it is: the engine's
        /// <c>SceneStore.VisibleWindowRect</c> (the painted box with its transforms, cut by every clipping ancestor at its presented size and by
        /// every authored clip, its own included: the rect hit-testing agrees with), cut by <paramref name="window"/> and by any scroller that
        /// does not clip. A hidden, parked, exiting or transparent node (or one under such an ancestor) reports an empty rect and 0. False only
        /// for a dead handle. A VISIBILITY test only: a node under a modal scrim, a menu or any other overlay still reports what its clips leave
        /// of it (a <paramref name="fraction"/> under <see cref="MinFraction"/> is not on screen; one over it may still be covered). For a click,
        /// use <see cref="TryGetActionable"/>.</summary>
        public static bool TryGetVisibleRect(SceneStore scene, NodeHandle node, in RectF window, out RectF visible, out float fraction)
        {
            visible = default;
            fraction = 0f;
            if (node.IsNull || !scene.IsLive(node)) return false;
            for (NodeHandle a = node; !a.IsNull; a = scene.Parent(a))
                if (Hidden(scene, a)) return true;
            RectF painted = scene.AbsoluteTransformedRect(node);
            RectF cut = scene.VisibleWindowRect(node).Intersect(window);
            for (NodeHandle a = scene.Parent(node); !a.IsNull && !cut.IsEmpty; a = scene.Parent(a))
                if ((scene.Flags(a) & NodeFlags.ClipsToBounds) == 0 && scene.TryGetScroll(a, out _)) cut = cut.Intersect(scene.AbsoluteTransformedRect(a));
            visible = cut;
            fraction = Fraction(painted, visible);
            return true;
        }

        /// <summary>Can a click reach <paramref name="node"/>? It is on screen (<see cref="TryGetVisibleRect"/>: at least <see cref="MinFraction"/>
        /// of it) and the engine's hit test at the centre of its visible rect lands on it, inside it, or on an ancestor whose click it is part of (a
        /// label inside its button). <paramref name="hit"/> is what the hit test found there: a scrim, a menu's plate or a pinned header when it
        /// takes the click instead. UI thread, between frames.</summary>
        public static bool TryGetActionable(AppHost host, NodeHandle node, in RectF window, out RectF visible, out float fraction, out NodeHandle hit)
        {
            hit = NodeHandle.Null;
            SceneStore scene = host.Scene;
            if (!TryGetVisibleRect(scene, node, window, out visible, out fraction) || visible.IsEmpty) return false;
            hit = host.Input.HitTest(new Point2(visible.X + visible.W / 2f, visible.Y + visible.H / 2f));
            return fraction >= MinFraction - FractionSlack && !hit.IsNull && (Within(scene, hit, node) || Within(scene, node, hit));
        }

        /// <summary><paramref name="node"/> is <paramref name="ancestor"/> or lies under it.</summary>
        static bool Within(SceneStore scene, NodeHandle node, NodeHandle ancestor)
        {
            for (NodeHandle a = node; !a.IsNull; a = scene.Parent(a))
                if (a == ancestor) return true;
            return false;
        }

        static bool Hidden(SceneStore scene, NodeHandle node)
        {
            NodeFlags flags = scene.Flags(node);
            return (flags & NodeFlags.Visible) == 0 || (flags & (NodeFlags.Parked | NodeFlags.Exiting)) != 0 || scene.Paint(node).Opacity <= 0f;
        }
    }
}
