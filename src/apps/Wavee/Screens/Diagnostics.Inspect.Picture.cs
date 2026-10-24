// ── Screens/Diagnostics.Inspect.Picture.cs — the picture rules (picture.blank, picture.unpainted, overlay.open) and what `status` adds ───
// lint reads nodes; these read the rendered frame (and what a verb left open) for the pages that are visibly wrong while every node is fine:
// a frame that paints nothing at all, a page that stops painting short of its frame, and an overlay the verb that staged a state left over it
//
// Role: CORE for the rules (pure: a prepared `InspectLint.Snapshot` with its frame); SHELL for the session half (UI thread: the overlay
// layers a `state` verb opened, and `status`'s overlays, memory and GC figures).
// Spec: handoff/audit-2026-10-23 FIX-PLAN A1 item 5 (tooling.md T4: `video=fullscreen` renders all black; `video=floating` stops painting the
// WinUI page under the mini player; `state kind=loading` at Zune 400x860 leaves the omni search flyout open over the state). Reference:
// docs/guide/agent-lint.md.

using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class InspectLint
    {
        /// <summary>An overlay layer a `state` verb opened and that is still open when the lint runs: its layer key, the first key inside it and
        /// the state that staged it.</summary>
        public readonly record struct StagedOverlay(string Layer, string First, string State);

        /// <summary>The frame's channel tolerance (0..255) for "one colour": an encoder's or a gradient's last step, not a glyph's edge.</summary>
        public const int BlankTolerance = 2;

        /// <summary>picture.blank: the rendered frame is one colour, every pixel (within <see cref="BlankTolerance"/>): nothing painted at all, or
        /// a surface with nothing on it (the fullscreen video stage headless). One finding per frame; none without a frame.</summary>
        static int BlankRule(Snapshot s, List<Finding> into)
        {
            if (s.Backdrop is not { } px) return 0;
            if (!px.TryUniform(BlankTolerance, out ColorF colour)) return 1;
            int inScope = 0, words = 0;
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope) continue;
                inScope++;
                if (IsWords(n)) words++;
            }
            into.Add(new Finding(Rules.PictureBlank, Severity.Error,
                $"the rendered frame is blank: every pixel of its {px.Width} x {px.Height} is #{Hex(colour)} ({inScope} nodes in scope, {words} text runs among them, none of it on screen)", [])
            {
                Measured = [M("width", px.Width), M("height", px.Height), M("nodesInScope", inScope), M("textRuns", words)],
                Pattern = "#frame",
                Cause = "uniform",
                Rects = [new RectF(0f, 0f, s.Width, s.Height)],
            });
            return 1;
        }

        /// <summary>The shortest band (DIP) under a cut-short scroller that counts as a region.</summary>
        public const float MinBand = 40f;

        /// <summary>How far (DIP) a part painted over a band may cast its shadow: that ring is not the band's ground either.</summary>
        public const float ShadowReach = 24f;

        /// <summary>The share of a band's pixels one colour must hold for the band to be empty.</summary>
        public const float EmptyShare = 0.98f;

        /// <summary>picture.unpainted: the page stops painting where its frame goes on. A vertical scroller that shows (not under a modal scrim)
        /// with more to show (its offset short of its max) ends at least <see cref="MinBand"/> above the bottom of the box that holds its
        /// width, and the band between (less what paints over it after the page, with its shadow: a mini player, a pane) is one colour in the
        /// rendered frame: WinUI `video=floating` cut the page's viewport at the mini player's top and left the card's plate below it.
        /// Text a node says is there but the frame does not show is not judged here: an edge fade, a sticky header's plate or a translucent
        /// cover hides text on purpose in ways the snapshot cannot tell from a fault.</summary>
        static int UnpaintedRule(Snapshot s, List<Finding> into)
        {
            if (s.Backdrop is not { } px) return 0;
            return UnpaintedBands(s, px, into);
        }

        /// <summary>The band rule of <see cref="UnpaintedRule"/>: one finding per cut-short scroller whose band is empty.</summary>
        static int UnpaintedBands(Snapshot s, Pixels px, List<Finding> into)
        {
            IReadOnlyList<Node> nodes = s.Nodes;
            int looked = 0;
            foreach (Node sc in nodes)
            {
                // a scroller is a container, not a leaf: judged when any of it shows (a pane over part of it does not take it out of scope)
                if (sc.ScrollAxis != 2 || sc.Covered || sc.OnScreen <= 0f || sc.ScrollMax - sc.ScrollOffset <= Slack) continue;
                float bottom = sc.Rect.Bottom;
                Node? frame = null;
                for (int a = sc.Parent; a >= 0; a = nodes[a].Parent)
                {
                    Node anc = nodes[a];
                    if (anc.Rect.X > sc.Rect.X + Slack || anc.Rect.Right < sc.Rect.Right - Slack) break;   // no longer holds the scroller's width
                    if (anc.Rect.Bottom >= bottom + MinBand) { frame = anc; break; }
                }
                if (frame is null) continue;
                var band = new RectF(sc.Rect.X, bottom, sc.Rect.W, MathF.Min(frame.Rect.Bottom, s.Height) - bottom);
                if (band.H < MinBand) continue;
                looked++;
                // what paints over the band after the page (a mini player, a pane, a footer: later in paint order, outside the scroller) is not
                // the band's ground, so not judged; what paints before it (the frame's plate, the scroller's ancestors) is the ground, and the
                // scroller's own descendants are clipped by its viewport
                var skip = new List<RectF>();
                foreach (Node n in nodes)
                {
                    // a part that fills its box (a plate, an image, text, a drawn visual); a border alone paints only its edges
                    bool fills = n.Image || n.Text is not null || n.Gradient || n.Fill.A >= 0.05f || n.Visual.Length > 0;
                    if (!fills || n.Index <= sc.Index || n.Opacity < 0.05f || IsUnder(nodes, n, sc.Index)) continue;
                    RectF r = n.Visible.IsEmpty ? n.Rect : n.Visible;
                    RectF part = new RectF(r.X - ShadowReach, r.Y - ShadowReach, r.W + 2f * ShadowReach, r.H + 2f * ShadowReach).Intersect(band);   // its shadow too
                    if (part.W > 0f && part.H > 0f) skip.Add(part);
                }
                if (!px.TryEmpty(band, skip, EmptyShare, out ColorF colour, out float judged) || judged < 0.5f) continue;
                into.Add(new Finding(Rules.PictureUnpainted, Severity.Error,
                    $"the page stops painting short of its frame: {sc.Ref} ends at y={F(bottom)} with {F(sc.ScrollMax - sc.ScrollOffset)} DIP more to show, " +
                    $"{F(band.H)} above the bottom of {frame.Ref}, and the band {F(band.X)},{F(band.Y)} {F(band.W)} x {F(band.H)} under it paints only #{Hex(colour)}",
                    [sc.Ref, frame.Ref])
                {
                    Measured = [M("scrollerBottom", bottom), M("frameBottom", frame.Rect.Bottom), M("band", band.H), M("moreToShow", sc.ScrollMax - sc.ScrollOffset)],
                    Expected = [M("band", 0)],
                    Pattern = PatternOfKey(sc.Key ?? sc.Pattern),
                    Cause = "band",
                    Rects = [band, Where(sc)],
                    Nodes = [sc.Index],
                });
            }
            return looked;
        }

        /// <summary><paramref name="n"/> is the node at <paramref name="ancestor"/> or sits under it.</summary>
        static bool IsUnder(IReadOnlyList<Node> nodes, Node n, int ancestor)
        {
            for (int i = n.Index; i >= 0; i = nodes[i].Parent)
                if (i == ancestor) return true;
            return false;
        }

        /// <summary>overlay.open: an overlay layer the verb that staged a state opened is still open (`state kind=loading` opens a search the fake
        /// catalogue never answers; at Zune Compact its suggestion flyout stays over the page). The picture is then the overlay over the state,
        /// not the state: close it (key esc), or stage the state a way that opens none.</summary>
        static int OverlayOpenRule(Snapshot s, List<Finding> into)
        {
            foreach (StagedOverlay staged in s.StagedOverlays)
                into.Add(new Finding(Rules.OverlayOpen, Severity.Error,
                    $"overlay {staged.Layer}{(staged.First.Length > 0 ? " (" + staged.First + ")" : "")} that `state kind={staged.State}` opened is still open over the page: the frame shows it over the state",
                    [staged.First.Length > 0 ? staged.First : staged.Layer])
                {
                    Pattern = staged.First.Length > 0 ? PatternOfKey(staged.First) : "overlay",
                    Cause = "state:" + staged.State,
                });
            return s.StagedOverlays.Count;
        }

        public sealed partial class Pixels
        {
            /// <summary>Every pixel of the frame is within <paramref name="tolerance"/> (0..255 per channel) of the first: one colour, nothing on it.</summary>
            public bool TryUniform(int tolerance, out ColorF colour)
            {
                colour = default;
                if (Width <= 0 || Height <= 0) return false;
                int b0 = _bgra[0], g0 = _bgra[1], r0 = _bgra[2];
                for (int i = 0; i < Width * Height * 4; i += 4)
                    if (Math.Abs(_bgra[i] - b0) > tolerance || Math.Abs(_bgra[i + 1] - g0) > tolerance || Math.Abs(_bgra[i + 2] - r0) > tolerance) return false;
                Rgb(0, out float r, out float g, out float b);
                colour = new ColorF(r, g, b, 1f);
                return true;
            }

            /// <summary>Whether <paramref name="rect"/> (DIP), less the <paramref name="skip"/> rects, is one colour: the most common colour holds at
            /// least <paramref name="share"/> of the pixels judged. <paramref name="judged"/> is the share of the rect's pixels that were judged
            /// (not skipped). False when the rect holds no pixel to judge.</summary>
            public bool TryEmpty(RectF rect, List<RectF> skip, float share, out ColorF colour, out float judged)
            {
                colour = default;
                judged = 0f;
                if (!Bounds(rect, out int x0, out int y0, out int x1, out int y1)) return false;
                var picked = new List<int>();
                int all = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        all++;
                        float dx = (x + 0.5f) / Scale, dy = (y + 0.5f) / Scale;
                        bool skipped = false;
                        foreach (RectF r in skip)
                            if (dx >= r.X && dx < r.Right && dy >= r.Y && dy < r.Bottom) { skipped = true; break; }
                        if (!skipped) picked.Add((y * Width + x) * 4);
                    }
                if (picked.Count == 0) return false;
                judged = picked.Count / (float)all;
                var counts = new Dictionary<int, int>();
                foreach (int i in picked)
                {
                    int key = (_bgra[i + 2] << 16) | (_bgra[i + 1] << 8) | _bgra[i];
                    counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                }
                int best = 0, bestCount = 0;
                foreach (var (key, count) in counts)
                    if (count > bestCount) { best = key; bestCount = count; }
                int br = (best >> 16) & 255, bg = (best >> 8) & 255, bb = best & 255, near = 0;
                foreach (int i in picked)   // a gradient's or a dither's last steps count as the one colour
                    if (Math.Abs(_bgra[i + 2] - br) <= EmptyTolerance && Math.Abs(_bgra[i + 1] - bg) <= EmptyTolerance && Math.Abs(_bgra[i] - bb) <= EmptyTolerance) near++;
                colour = new ColorF(br / 255f, bg / 255f, bb / 255f, 1f);
                return near >= share * picked.Count;
            }

            /// <summary>The channel tolerance (0..255) of "one colour" in <see cref="TryEmpty"/>: a plate's dither or a soft gradient's last steps.</summary>
            public const int EmptyTolerance = 8;

        }
    }

    public sealed partial class InspectSession
    {
        /// <summary>The overlay layers open before the `state` verb that is running (null when none runs).</summary>
        HashSet<NodeHandle>? _overlaysBeforeState;

        /// <summary>The overlay layers the last `state` verb opened, in the scene they live in, with the state's kind.</summary>
        (SceneStore Scene, List<(NodeHandle Layer, string State)> Layers)? _stateOverlays;

        /// <summary>Every shown overlay layer (`overlay:<id>`), top first, with the first key inside each.</summary>
        static List<(NodeHandle Node, string Key, string First)> OpenOverlays(Shell.HeadlessShell shell)
        {
            SceneStore scene = shell.Host.Scene;
            List<(NodeHandle Node, string Key)> layers = shell.FindPrefix("overlay:");
            layers.Sort(static (x, y) => LayerId(y.Key).CompareTo(LayerId(x.Key)));   // the overlay host's ids grow with each open: top first
            var open = new List<(NodeHandle Node, string Key, string First)>();
            foreach (var (node, key) in layers)
            {
                if (!scene.IsShown(node)) continue;
                string first = "";
                foreach (NodeHandle n in InspectKit.Subtree(scene, node))
                    if (n != node && scene.DebugKeyOf(n) is { Length: > 0 } inner) { first = inner; break; }
                open.Add((node, key, first));
            }
            return open;
        }

        /// <summary>`overlays: [{key, first}]`: the open overlay layers, top first (`after` and `status` both write it).</summary>
        static void WriteOverlays(Utf8JsonWriter w, Shell.HeadlessShell shell)
        {
            w.WriteStartArray("overlays");
            foreach (var (_, key, first) in OpenOverlays(shell))
            {
                w.WriteStartObject();
                w.WriteString("key", key);
                if (first.Length > 0) w.WriteString("first", first);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        /// <summary>Before a `state` verb stages its state: remember the overlays already open.</summary>
        void BeforeState(Shell.HeadlessShell shell)
        {
            _overlaysBeforeState = [];
            foreach (var (node, _, _) in OpenOverlays(shell)) _overlaysBeforeState.Add(node);
        }

        /// <summary>After a `state` verb settled: the overlays it opened (what was not open before), remembered for `overlay.open`, and written
        /// into its reply as <c>leftOpen</c>. `state kind=none` forgets them.</summary>
        void AfterState(Utf8JsonWriter w, Shell.HeadlessShell shell, string kind)
        {
            var opened = new List<(NodeHandle Layer, string State)>();
            w.WriteStartArray("leftOpen");
            foreach (var (node, key, first) in OpenOverlays(shell))
            {
                if (_overlaysBeforeState?.Contains(node) == true) continue;
                opened.Add((node, kind));
                w.WriteStartObject();
                w.WriteString("key", key);
                if (first.Length > 0) w.WriteString("first", first);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            _overlaysBeforeState = null;
            _stateOverlays = kind is "none" or "" || opened.Count == 0 ? null : (shell.Host.Scene, opened);
        }

        /// <summary>The overlays a `state` verb opened that are still open on this mount (none after a remount: another scene).</summary>
        List<InspectLint.StagedOverlay> StagedOverlays(Shell.HeadlessShell shell)
        {
            var staged = new List<InspectLint.StagedOverlay>();
            if (_stateOverlays is not { } so || !ReferenceEquals(so.Scene, shell.Host.Scene)) return staged;
            SceneStore scene = shell.Host.Scene;
            foreach (var (layer, state) in so.Layers)
            {
                if (!scene.IsLive(layer) || !scene.IsShown(layer)) continue;
                string first = "";
                foreach (NodeHandle n in InspectKit.Subtree(scene, layer))
                    if (n != layer && scene.DebugKeyOf(n) is { Length: > 0 } inner) { first = inner; break; }
                staged.Add(new InspectLint.StagedOverlay(scene.DebugKeyOf(layer) ?? "overlay", first, state));
            }
            return staged;
        }

        /// <summary>`status`'s health figures: <c>overlays</c> (as `after` lists them), and <c>memory</c> {managedBytes, heapBytes, workingSetBytes,
        /// allocatedBytes, gen0, gen1, gen2, pauseMs}: the managed heap now, the GC's heap size at its last collection, the process working set,
        /// the bytes allocated since start, the collections per generation since start and the total pause time.</summary>
        void WriteHealth(Utf8JsonWriter w)
        {
            if (_shell is { } shell) WriteOverlays(w, shell);
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            w.WriteStartObject("memory");
            w.WriteNumber("managedBytes", GC.GetTotalMemory(forceFullCollection: false));
            w.WriteNumber("heapBytes", info.HeapSizeBytes);
            w.WriteNumber("workingSetBytes", Environment.WorkingSet);
            w.WriteNumber("allocatedBytes", GC.GetTotalAllocatedBytes(precise: false));
            w.WriteNumber("gen0", GC.CollectionCount(0));
            w.WriteNumber("gen1", GC.CollectionCount(1));
            w.WriteNumber("gen2", GC.CollectionCount(2));
            w.WriteNumber("pauseMs", Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1));
            w.WriteEndObject();
        }
    }
}
