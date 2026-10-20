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
// THE THREE SCOPES (`lint scope=`): `screen` (the default) keeps a node when at least half of its rect is visible; `content` keeps what a
// person reaches by scrolling (below a scroller nothing cuts along its axis, every other clip and the window still do: a page audit); `all`
// keeps everything the capture walked (the lint before this file). Occlusion (a dialog's scrim over the page) is not a cut: the rules that
// care look at paint.
//
// NOT HERE. Hidden, parked, exiting and fully transparent subtrees never reach the snapshot (the capture skips them), so the pure half never
// sees them; `TryGetVisibleRect` checks them itself because it starts from any live node.

using FluentGpu.Foundation;
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

        /// <summary>Is a node with these fractions in <paramref name="scope"/>?</summary>
        public static bool InScope(Scope scope, float onScreen, float inContent) => scope switch
        {
            Scope.All => true,
            Scope.Content => inContent >= MinFraction,
            _ => onScreen >= MinFraction,
        };

        // ── the snapshot half (pure) ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>The visible rect, the on-screen fraction and the content fraction of every node of a depth-first list (a parent before its
        /// children, <c>Parent</c> an earlier index or -1) in a window of <paramref name="windowW"/> x <paramref name="windowH"/> DIP.
        /// A node is cut by what its ANCESTORS clip (a box's own clip cuts its children, never itself): <c>Clips</c>, a scroll viewport
        /// (<c>ScrollAxis</c> != 0) and an authored <c>ClipRect</c>. The content fraction lifts, below a scroller, every cut along its axis (the
        /// window and whatever clips above the viewport, the viewport included): scrolling brings that content into view; a panel parked past
        /// the window's edge stays out.</summary>
        public static void Compute(IReadOnlyList<InspectLint.Node> nodes, float windowW, float windowH,
            Span<RectF> visible, Span<float> onScreen, Span<float> inContent)
        {
            int count = nodes.Count;
            if (visible.Length < count || onScreen.Length < count || inContent.Length < count)
                throw new ArgumentException("the output spans are shorter than the node list");
            var window = new RectF(0f, 0f, windowW, windowH);
            var screenCut = new RectF[count];
            var contentCut = new RectF[count];
            for (int i = 0; i < count; i++)
            {
                InspectLint.Node n = nodes[i];
                int p = n.Parent;
                RectF inheritedScreen = p >= 0 && p < i ? screenCut[p] : window;
                RectF inheritedContent = p >= 0 && p < i ? contentCut[p] : window;
                RectF r = n.Rect;
                RectF seen = r.Intersect(inheritedScreen);
                visible[i] = seen;
                onScreen[i] = Fraction(r, seen);
                inContent[i] = Fraction(r, r.Intersect(inheritedContent));

                // What this node cuts its descendants to.
                RectF childScreen = inheritedScreen, childContent = inheritedContent;
                if (n.Clips || n.ScrollAxis != 0) childScreen = childScreen.Intersect(r);
                if (n.ScrollAxis == 2) childContent = Unbounded(childContent.Intersect(r), vertical: true);   // scrolling reaches the whole axis
                else if (n.ScrollAxis == 1) childContent = Unbounded(childContent.Intersect(r), vertical: false);
                else if (n.Clips) childContent = childContent.Intersect(r);
                if (!n.ClipRect.IsInfinite)
                {
                    childScreen = childScreen.Intersect(n.ClipRect);
                    childContent = childContent.Intersect(n.ClipRect);
                }
                screenCut[i] = childScreen;
                contentCut[i] = childContent;
            }
        }

        /// <summary><paramref name="cut"/> opened along one axis: what a scroller's content reaches by scrolling (the cross axis stays cut).</summary>
        static RectF Unbounded(in RectF cut, bool vertical)
        {
            if (cut.IsEmpty) return cut;
            RectF open = RectF.Infinite;
            return vertical ? new RectF(cut.X, open.Y, cut.W, open.H) : new RectF(open.X, cut.Y, open.W, cut.H);
        }

        // ── the live half (UI thread) ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The visible rect of a mounted node in window DIP and the share of its presented rect it is: its presented rect cut by
        /// <paramref name="window"/>, by every ancestor that clips its children or scrolls, and by every authored clip on the way up. A hidden,
        /// parked, exiting or transparent node (or one under such an ancestor) reports an empty rect and 0. False only for a dead handle.
        /// For a click's actionability: a target with <paramref name="fraction"/> under <see cref="MinFraction"/> is not on screen.</summary>
        public static bool TryGetVisibleRect(SceneStore scene, NodeHandle node, in RectF window, out RectF visible, out float fraction)
        {
            visible = default;
            fraction = 0f;
            if (node.IsNull || !scene.IsLive(node)) return false;
            if (Hidden(scene, node)) return true;
            RectF rect = scene.PresentedAbsoluteRect(node);
            RectF cut = window;
            for (NodeHandle a = scene.Parent(node); !a.IsNull; a = scene.Parent(a))
            {
                if (Hidden(scene, a)) return true;
                RectF ar = scene.PresentedAbsoluteRect(a);
                if ((scene.Flags(a) & NodeFlags.ClipsToBounds) != 0 || scene.TryGetScroll(a, out _)) cut = cut.Intersect(ar);
                RectF authored = scene.Paint(a).ClipRect;
                if (!authored.IsInfinite) cut = cut.Intersect(new RectF(ar.X + authored.X, ar.Y + authored.Y, authored.W, authored.H));
                if (cut.IsEmpty) return true;
            }
            visible = rect.Intersect(cut);
            fraction = Fraction(rect, visible);
            return true;
        }

        static bool Hidden(SceneStore scene, NodeHandle node)
        {
            NodeFlags flags = scene.Flags(node);
            return (flags & NodeFlags.Visible) == 0 || (flags & (NodeFlags.Parked | NodeFlags.Exiting)) != 0 || scene.Paint(node).Opacity <= 0f;
        }
    }
}
