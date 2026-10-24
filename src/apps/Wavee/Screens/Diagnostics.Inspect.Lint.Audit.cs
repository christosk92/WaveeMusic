// ── Screens/Diagnostics.Inspect.Lint.Audit.cs — the report-only rules from the 2026-10-23 audit's blind spots (lane A3) ─────────────────
// stage.overlap, text.copies, overlay.narrow, text.ruled, edge.gutter, edge.huge, type.voice, state.rest, playing.status, control.required,
// edge.window, space.huge. The pixel rules of the same lane (overlay.translucent, picture.text) are in Diagnostics.Inspect.Picture.cs.
//
// Role: CORE (pure, like Diagnostics.Inspect.Lint.cs: a prepared snapshot in, findings out). User reference: docs/guide/agent-lint.md.
// Spec: handoff/audit-2026-10-23/FIX-PLAN.md lane A3 item 3; the source cases are visual.md (findings 1, 2, 4, 5, 6, 8, 9, 10, 12, 16, 19,
// 20, 24), winui.md section 5 (8 and 11) and their PNGs.
//
// EVERY RULE HERE IS TODO (report-only) until its sweep hits are all confirmed real by eye (scratch/a3/PROGRESS.md keeps the counts): a
// finding never fails `--gate` (only `--gate-todo`). Each rule names the case it was built from in its summary.

using System.Globalization;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class InspectLint
    {
        // ── shared ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A node an overlay layer holds: it or an ancestor is keyed <c>overlay:N</c> (the overlay host's layers).</summary>
        static int OverlayOf(Snapshot s, Node n)
        {
            int found = -1;
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
                if (s.Nodes[i].Key is { } key && key.StartsWith("overlay:", StringComparison.Ordinal)) found = i;
            return found;
        }

        /// <summary>The x range a run's words take in its box: its natural width placed by its alignment (a centred caption's ink is not its box).</summary>
        static (float L, float R) InkX(Node n, TextRun t)
        {
            float w = t.NaturalW > 0f ? MathF.Min(t.NaturalW, n.Rect.W) : n.Rect.W;
            return t.Align switch
            {
                "Center" => (n.Rect.X + (n.Rect.W - w) / 2f, n.Rect.X + (n.Rect.W + w) / 2f),
                "End" => (n.Rect.Right - w, n.Rect.Right),
                _ => (n.Rect.X, n.Rect.X + w),
            };
        }

        /// <summary>The box a run's glyphs ink: <see cref="InkX"/> across, the measured ink (or the line box) down.</summary>
        static RectF GlyphBox(Node n, TextRun t)
        {
            var (l, r) = InkX(n, t);
            float top = t.HasInk ? n.Rect.Y + t.InkTop : n.Rect.Y, bottom = t.HasInk ? n.Rect.Y + t.InkBottom : n.Rect.Bottom;
            return RectF.FromLTRB(l, top, r, bottom);
        }

        static float Overlap(in RectF a, in RectF b, out float w, out float h)
        {
            w = MathF.Min(a.Right, b.Right) - MathF.Max(a.X, b.X);
            h = MathF.Min(a.Bottom, b.Bottom) - MathF.Max(a.Y, b.Y);
            return w > 0f && h > 0f ? w * h : 0f;
        }

        static bool Holds(in RectF outer, in RectF inner, float slack = 2f)
            => inner.X >= outer.X - slack && inner.Y >= outer.Y - slack && inner.Right <= outer.Right + slack && inner.Bottom <= outer.Bottom + slack;

        /// <summary>A words run a person reads on this frame: in scope, not covered, not glyphs, visibly opaque.</summary>
        static bool Reads(Node n, float opacity = 0.3f) => n.InScope && !n.Covered && IsWords(n) && n.Opacity >= opacity;

        // ── stage.overlap ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The smallest plate (DIP) a stage layer counts: a cover, a card, an art plate; not a seek line or a dot.</summary>
        public const float StagePlateMin = 48f;

        /// <summary>The top-level stage layer a node sits in: its outermost ancestor keyed <c>stage:</c> (stage:face, stage:npc, stage:caption,
        /// stage:chrome ...); -1 off the stage.</summary>
        static int StageLayerOf(Snapshot s, Node n)
        {
            int found = -1;
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
                if (s.Nodes[i].Key is { } key && key.StartsWith("stage:", StringComparison.Ordinal)) found = i;
            return found;
        }

        /// <summary>stage.overlap (visual.md 2): the now-playing stage's layers collide. Two plates of different layers (an opaque fill or an image,
        /// <see cref="StagePlateMin"/> or more, under 60 % of the window: not a backdrop) overlap and neither holds the other (Zune's card over the
        /// centre art plate), or a line of text in one layer straddles another layer's plate edge (the caption half on the art, half off). The
        /// stage loops forever, so moving nodes are judged as they stand.</summary>
        static int StageOverlapRule(Snapshot s, List<Finding> into)
        {
            IReadOnlyList<Node> nodes = s.Nodes;
            float big = 0.6f * s.Width * s.Height;
            var plates = new List<(Node N, int Layer)>();
            var texts = new List<(Node N, int Layer, RectF Box)>();
            foreach (Node n in nodes)
            {
                if (n.Covered || n.Visible.IsEmpty) continue;
                int layer = StageLayerOf(s, n);
                if (layer < 0) continue;
                RectF r = n.Visible;
                bool plate = (n.Image || n.Fill.A * n.Opacity >= 0.5f) && r.W >= StagePlateMin && r.H >= StagePlateMin && r.W * r.H < big;
                if (plate)
                {
                    bool outer = true;   // the outermost plate of a subtree speaks for it (a cover's image inside its plate)
                    for (int a = n.Parent; a >= 0 && outer; a = nodes[a].Parent)
                    {
                        Node up = nodes[a];
                        if ((up.Image || up.Fill.A * up.Opacity >= 0.5f) && up.Visible.W * up.Visible.H < big && Holds(up.Visible, r)) outer = false;
                    }
                    if (outer) plates.Add((n, layer));
                }
                else if (n.Text is { } t && IsWords(n) && n.Opacity >= 0.3f) texts.Add((n, layer, GlyphBox(n, t).Intersect(r)));
            }
            int looked = plates.Count + texts.Count;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < plates.Count; i++)
                for (int j = i + 1; j < plates.Count; j++)
                {
                    var (a, la) = plates[i];
                    var (b, lb) = plates[j];
                    if (la == lb) continue;
                    if (Overlap(a.Visible, b.Visible, out float w, out float h) <= 0f || w < 12f || h < 12f) continue;
                    if (Holds(a.Visible, b.Visible) || Holds(b.Visible, a.Visible)) continue;
                    string pair = nodes[la].Ref + "+" + nodes[lb].Ref;
                    if (!seen.Add("p|" + pair)) continue;
                    into.Add(new Finding(Rules.StageOverlap, Severity.Warn,
                        $"two stage layers' plates collide: {a.Ref} ({nodes[la].Ref}) and {b.Ref} ({nodes[lb].Ref}) overlap by {F(w)} x {F(h)}, and neither holds the other", [a.Ref, b.Ref])
                    {
                        Measured = [M("w", w), M("h", h)],
                        Expected = [M("overlap", 0)],
                        Pattern = pair,
                        Cause = "plates",
                        Rects = [a.Visible, b.Visible],
                        Nodes = [a.Index, b.Index],
                        Fix = "give each stage layer its own place on the layout's grid (the card beside the art, not over it); never hide one of them",
                    });
                }
            foreach (var (t, lt, box) in texts)
                foreach (var (p, lp) in plates)
                {
                    if (lt == lp || box.IsEmpty) continue;
                    if (Overlap(box, p.Visible, out float w, out float h) <= 0f || w < 8f || h < 0.5f * box.H) continue;
                    float outside = MathF.Max(p.Visible.X - box.X, 0f) + MathF.Max(box.Right - p.Visible.Right, 0f);
                    if (outside < 8f) continue;   // on the plate, or past it by a hair: not a straddle
                    string pair = nodes[lt].Ref + "+" + nodes[lp].Ref;
                    if (!seen.Add("t|" + pair)) continue;
                    into.Add(new Finding(Rules.StageOverlap, Severity.Warn,
                        $"{TextName(t.Text!)} ({nodes[lt].Ref}) straddles the edge of {p.Ref} ({nodes[lp].Ref}): {F(w)} DIP of it on the plate, {F(outside)} off it", [t.Ref, p.Ref])
                    {
                        Measured = [M("on", w), M("off", outside)],
                        Expected = [M("off", 0)],
                        Pattern = pair,
                        Cause = "text-edge",
                        Rects = [box, p.Visible],
                        Nodes = [t.Index, p.Index],
                        Fix = "keep the line on its plate or clear of it (wrap it to the plate's measure, or move the layer); never shrink Zune display type",
                    });
                }
            return looked;
        }

        // ── text.copies ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>text.copies (visual.md 2): two DIFFERENT lines of one text family drawn over each other, both readable: a crossfade that
        /// stacks the outgoing and the incoming lyric line in one slot at full size. The same words twice in one place are a designed layer (a
        /// karaoke fill over its dim copy, a glow) and are left alone. Judges moving nodes as they stand (a crossfade is motion).</summary>
        static int TextCopiesRule(Snapshot s, List<Finding> into)
        {
            var runs = new List<(Node N, RectF Box)>();
            foreach (Node n in s.Nodes)
                if (n.Text is { } t && !n.Covered && IsWords(n) && n.Opacity >= 0.2f && !n.Visible.IsEmpty && t.String.Length > 0)
                    runs.Add((n, GlyphBox(n, t).Intersect(n.Visible)));
            var groups = new Dictionary<string, (Node A, Node B, int Count, RectF Box)>(StringComparer.Ordinal);
            for (int i = 0; i < runs.Count; i++)
                for (int j = i + 1; j < runs.Count; j++)
                {
                    var (a, ba) = runs[i];
                    var (b, bb) = runs[j];
                    if (ba.IsEmpty || bb.IsEmpty || a.Pattern != b.Pattern || a.Pattern == "#root") continue;
                    if (string.Equals(a.Text!.String, b.Text!.String, StringComparison.Ordinal)) continue;
                    if (Under(s, a.Index, b.Index) || Under(s, b.Index, a.Index)) continue;
                    float area = Overlap(ba, bb, out _, out float h);
                    float small = MathF.Min(ba.W * ba.H, bb.W * bb.H);
                    if (small <= 0f || area < 0.4f * small || h < 0.5f * MathF.Min(ba.H, bb.H)) continue;
                    string key = a.Pattern + "|" + (a.Animating || b.Animating || a.Moving || b.Moving ? "crossfade" : "stacked");
                    groups[key] = groups.TryGetValue(key, out var g) ? (g.A, g.B, g.Count + 1, g.Box) : (a, b, 1, ba);
                }
            foreach (var (key, (a, b, count, box)) in groups)
            {
                string cause = key[(key.LastIndexOf('|') + 1)..];
                into.Add(new Finding(Rules.TextCopies, Severity.Warn,
                    $"two lines of {a.Pattern} draw over each other: {TextName(a.Text!)} (opacity {F(a.Opacity)}) and {TextName(b.Text!)} (opacity {F(b.Opacity)})" +
                    (count > 1 ? $", and {count - 1} more pair{(count > 2 ? "s" : "")}" : "") + (cause == "crossfade" ? " mid-crossfade" : ""), [a.Ref, b.Ref])
                {
                    Measured = [M("pairs", count), M("opacityA", a.Opacity), M("opacityB", b.Opacity)],
                    Pattern = a.Pattern,
                    Cause = cause,
                    Count = count,
                    Rects = [a.Visible, b.Visible],
                    Nodes = [a.Index, b.Index],
                    Fix = "let the outgoing line leave its slot as the incoming one arrives (slide or scale it out), or fade one out before the other draws; production motion is the spec: compare with origin/main first",
                });
            }
            return runs.Count;
        }

        // ── overlay.narrow ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>overlay.narrow (visual.md 1): an overlay narrower than its content. Of its word runs, three or more and at least half are
        /// ellipsised to under 40 % of the words they hold (Zune's search suggestions squeezed into a 154 DIP column at Compact: every row "L...",
        /// "Followin"). One finding per overlay layer.</summary>
        static int OverlayNarrowRule(Snapshot s, List<Finding> into)
        {
            var byLayer = new Dictionary<int, (int Runs, List<Node> Cut)>();
            foreach (Node n in s.Nodes)
            {
                if (n.Text is not { } t || !IsWords(n) || !n.InScope || n.Covered || t.String.Length == 0) continue;
                int layer = OverlayOf(s, n);
                if (layer < 0) continue;
                byLayer.TryGetValue(layer, out var e);
                e.Cut ??= [];
                e.Runs++;
                if ((t.Trimmed || t.OverflowX) && t.NaturalW > 0f && n.Rect.W < 0.4f * t.NaturalW) e.Cut.Add(n);
                byLayer[layer] = e;
            }
            foreach (var (layer, (runs, cut)) in byLayer)
            {
                if (cut.Count < 3 || cut.Count * 2 < runs) continue;
                Node root = s.Nodes[layer];
                float shown = 0f, wants = 0f;
                foreach (Node n in cut) { shown += n.Rect.W; wants += n.Text!.NaturalW; }
                var (refs, rects) = Members(cut);
                into.Add(new Finding(Rules.OverlayNarrow, Severity.Warn,
                    $"overlay {root.Ref} is narrower than its content: {cut.Count} of its {runs} text runs are cut to under 40 % of their words " +
                    $"(they show {F(shown / cut.Count)} DIP of {F(wants / cut.Count)} on average; first {TextName(cut[0].Text!)})", refs)
                {
                    Measured = [M("cut", cut.Count), M("runs", runs), M("shownW", shown / cut.Count), M("wantsW", wants / cut.Count)],
                    Pattern = cut[0].Pattern,
                    Cause = "narrow",
                    Count = cut.Count,
                    Rects = rects,
                    Nodes = Ids(cut),
                    Fix = "give the overlay the width its rows need (anchor it to the field's row or the window's gutter, not to a squeezed host); never shrink the type",
                });
            }
            return byLayer.Count;
        }

        // ── text.ruled ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A rule is at most this thick (DIP).</summary>
        public const float RuleThickness = 2.5f;

        /// <summary>A line primitive: a thin opaque fill with no words or art, not a slider's or a scroll bar's track (a seek line).</summary>
        static bool IsRuleLine(Snapshot s, Node n, out bool horizontal)
        {
            horizontal = n.Rect.H <= RuleThickness && n.Rect.W >= 16f;
            bool vertical = n.Rect.W <= RuleThickness && n.Rect.H >= 16f;
            if (!(horizontal || vertical) || n.Text is not null || n.Image || n.Interactive || n.Drag || n.Fill.A * n.Opacity < 0.12f) return false;
            for (int i = n.Parent; i >= 0; i = s.Nodes[i].Parent)
            {
                Node up = s.Nodes[i];
                if (up.Role is "Slider" or "ProgressBar" or "ScrollBar" || up.Drag) return false;
            }
            return true;
        }

        /// <summary>text.ruled (visual.md 8): a rule or line crossing a text's glyph box. A horizontal line inside the run's ink, from its top to a
        /// hair under its descenders (Zune's playlist title editor draws its underline at rest through the 120 title's p and y tails), or a
        /// vertical one through its words. Needs the glyph ink (a DirectWrite mount); a line of the run's own ancestor chain counts too.</summary>
        static int TextRuledRule(Snapshot s, List<Finding> into)
        {
            var lines = new List<(Node N, bool H)>();
            foreach (Node n in s.Nodes)
                if (n.InScope && !n.Covered && IsRuleLine(s, n, out bool h)) lines.Add((n, h));
            int looked = 0;
            var groups = new Dictionary<string, List<(Node Text, Node Line, float Into)>>(StringComparer.Ordinal);
            foreach (Node n in s.Nodes)
            {
                if (n.Text is not { HasInk: true } t || !Reads(n)) continue;
                RectF ink = GlyphBox(n, t);
                if (ink.W <= 0f || ink.H <= 0f) continue;
                looked++;
                foreach (var (line, horizontal) in lines)
                {
                    RectF l = line.Visible.IsEmpty ? line.Rect : line.Visible;
                    float into1;
                    if (horizontal)
                    {
                        float across = MathF.Min(l.Right, ink.Right) - MathF.Max(l.X, ink.X);
                        if (across < 12f || l.Y > ink.Bottom + 1f || l.Bottom < ink.Y + 0.25f * ink.H) continue;
                        into1 = ink.Bottom + 1f - l.Y;
                    }
                    else
                    {
                        float down = MathF.Min(l.Bottom, ink.Bottom) - MathF.Max(l.Y, ink.Y);
                        if (down < 0.5f * ink.H || l.X < ink.X + 2f || l.Right > ink.Right - 2f) continue;
                        into1 = down;
                    }
                    string key = n.Pattern + "|" + line.Pattern;
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                    list.Add((n, line, into1));
                }
            }
            foreach (var (key, list) in groups)
            {
                var (text, line, depth) = list[0];
                into.Add(new Finding(Rules.TextRuled, Severity.Warn,
                    $"a {F(line.Rect.W)} x {F(line.Rect.H)} line ({line.Ref}) crosses the glyphs of {TextName(text.Text!)} ({text.Ref}): it runs {F(depth)} DIP into their ink box" +
                    (list.Count > 1 ? $" (and {list.Count - 1} more)" : ""), [text.Ref, line.Ref])
                {
                    Measured = [M("into", depth), M("lineY", line.Rect.Y), M("inkBottom", GlyphBox(text, text.Text!).Bottom)],
                    Expected = [M("into", 0)],
                    Pattern = key,
                    Cause = line.Rect.H <= RuleThickness ? "underline" : "divider",
                    Count = list.Count,
                    Rects = [GlyphBox(text, text.Text!), line.Visible.IsEmpty ? line.Rect : line.Visible],
                    Nodes = [text.Index, line.Index],
                    Fix = "move the rule clear of the descenders (under the line box, or off at rest when it is an editor's underline); never shrink the type",
                });
            }
            return looked;
        }

        // ── edge.gutter ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The page's gutter at this tier: the guide's lead for the page title or the band's first pivot (the larger), 0 when the
        /// guide declares neither.</summary>
        static float GutterOf(Snapshot s)
        {
            float gutter = 0f;
            foreach (LintEdge e in s.Edges)
                if (e.Side == LintSide.Left && !e.Advisory && e.Id is "page.title" or "band.first-pivot") gutter = MathF.Max(gutter, e.Expected);
            return gutter;
        }

        /// <summary>edge.gutter (visual.md 10, 18): page text that leads left of the guide's gutter, on a column the guide does not list (the
        /// guide's own edges are edge.left's). Zune's episode at Compact puts `episode-section:*` ("about", "chapters", the body) at x0 under a 16
        /// gutter. Not judged: a rail (it scrolls under the edge), window chrome and overlays (no page slot), a run starting off the window (a
        /// bleed), a guide Hang or Bleed covering it, and text whose own key the guide's edges match.</summary>
        static int GutterRule(Snapshot s, List<Finding> into)
        {
            float gutter = GutterOf(s);
            if (gutter < 8f) return 0;
            int looked = 0;
            var groups = new Dictionary<string, List<(Node N, float Lead)>>(StringComparer.Ordinal);
            foreach (Node n in s.Nodes)
            {
                if (n.Text is not { } t || !Reads(n, 0.5f) || n.Moving || n.InRail || PageOf(s, n) < 0) continue;
                looked++;
                float lead = InkX(n, t).L;
                if (lead >= gutter - 3f || lead < 0f) continue;
                if (PolicyFor(s, n, LintIntent.Bleed) is not null) continue;
                if (PolicyFor(s, n, LintIntent.Hang) is { } hang && lead >= gutter + hang.Value - 3f) continue;
                bool listed = false;
                foreach (LintEdge e in s.Edges)
                    if (e.Side == LintSide.Left && EdgeKeyed(s, n, e)) { listed = true; break; }
                if (listed) continue;
                string pattern = n.Family.Length > 0 ? n.Family : n.Pattern;
                if (!groups.TryGetValue(pattern, out var list)) groups[pattern] = list = [];
                list.Add((n, lead));
            }
            foreach (var (pattern, list) in groups)
            {
                var (n, lead) = list[0];
                var (refs, rects) = Members(list.Select(static e => e.N));
                into.Add(new Finding(Rules.EdgeGutter, Severity.Warn,
                    $"{TextName(n.Text!)} leads at x={F(lead)}, {F(gutter - lead)} left of the page's {F(gutter)} gutter" + (list.Count > 1 ? $" ({list.Count} runs of {pattern})" : ""), refs)
                {
                    Measured = [M("x", lead), M("count", list.Count)],
                    Expected = [M("x", gutter)],
                    Pattern = pattern,
                    Cause = "lead:" + F(MathF.Round(lead - gutter)),
                    Count = list.Count,
                    Rects = rects,
                    Nodes = Ids(list.Select(static e => e.N)),
                    Fix = "lead the column on the page gutter (the tier record's inset), as the page title and band do; or declare it in the guide if the board bleeds it",
                });
            }
            return looked;
        }

        // ── edge.huge ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>edge.huge (visual.md 9, 19): the right edges the guide declares at Standard, checked at Huge and Huge4K against the lead this
        /// tier's guide gives (<see cref="Snapshot.MirrorEdges"/>): the band's last action ends as far from the right as its first pivot starts
        /// from the left. edge.right's own check, as a todo warning.</summary>
        static int HugeEdgeRule(Snapshot s, List<Finding> into)
        {
            if (s.MirrorEdges.Count == 0) return 0;
            var found = new List<Finding>();
            int looked = EdgeRule(s with { Edges = s.MirrorEdges }, Rules.EdgeRight, found);
            foreach (Finding f in found)
                into.Add(f with
                {
                    Rule = Rules.EdgeHuge,
                    Severity = Severity.Warn,
                    Fix = "end the part on the mirrored gutter (the tier record's right inset at Huge), as it does at Standard",
                });
            return looked;
        }

        // ── type.voice ──────────────────────────────────────────────────────────────────────────────────────────────

        static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal) { "a", "an", "and", "the", "of", "in", "on", "to", "for", "by", "or", "at", "with", "from" };

        /// <summary>Title Case: two or more words of three letters or more, every one of them capitalised ("Artwork Wash", "Open Playlist").</summary>
        static bool TitleCase(string text)
        {
            int capital = 0;
            foreach (string word in text.Split([' ', ' ', '/', '-'], StringSplitOptions.RemoveEmptyEntries))
            {
                string w = word.Trim('(', ')', '.', ',', ':', '"', '\'', '…', '!', '?');
                if (w.Length < 3 || SmallWords.Contains(w.ToLowerInvariant()) || !char.IsLetter(w[0])) continue;
                if (!char.IsUpper(w[0])) return false;
                capital++;
            }
            return capital >= 2;
        }

        static bool AllLower(string text)
        {
            bool letters = false;
            foreach (char c in text)
            {
                if (char.IsUpper(c)) return false;
                if (char.IsLetter(c)) letters = true;
            }
            return letters;
        }

        /// <summary>type.voice (visual.md 12, 20): the app's own copy (<see cref="TextRun.Copy"/>: a base-culture loc string, not a title or a
        /// name) in another theme's voice. Zune speaks lowercase and light: Title Case copy, or a capitalised or semibold heading (20 DIP and
        /// up), is a stand-in's or another theme's voice. Classic heads in semibold sentence case: a light all-lowercase head (18 DIP and up)
        /// is Zune's voice leaking in (the show reader's "continue", "new since you were here"). WinUI is not judged.</summary>
        static int VoiceRule(Snapshot s, List<Finding> into)
        {
            bool zune = string.Equals(s.Theme, "zune", StringComparison.OrdinalIgnoreCase);
            bool classic = string.Equals(s.Theme, "classic", StringComparison.OrdinalIgnoreCase);
            if (!zune && !classic) return 0;
            int looked = 0;
            var groups = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
            foreach (Node n in s.Nodes)
            {
                if (n.Text is not { Copy: true } t || !Reads(n, 0.5f)) continue;
                looked++;
                string? cause = null;
                if (zune)
                {
                    if (TitleCase(t.String)) cause = "title-case";
                    else if (t.Size >= 20f && t.Weight >= 600) cause = "semibold-head";
                    else if (t.Size >= 20f && !AllLower(t.String) && char.IsUpper(t.String.TrimStart()[0])) cause = "capital-head";
                }
                else if (t.Size >= 18f && t.Weight <= 300 && AllLower(t.String)) cause = "light-lowercase-head";
                if (cause is null) continue;
                string key = cause + "|" + n.Pattern;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                list.Add(n);
            }
            foreach (var (key, list) in groups)
            {
                string cause = key[..key.IndexOf('|')];
                Node n = list[0];
                var (refs, rects) = Members(list);
                string voice = cause switch
                {
                    "title-case" => "Title Case",
                    "semibold-head" => "a semibold head (weight " + n.Text!.Weight.ToString(CultureInfo.InvariantCulture) + ")",
                    "capital-head" => "a capitalised head",
                    _ => "a light lowercase head (Zune's voice)",
                };
                into.Add(new Finding(Rules.TypeVoice, Severity.Warn,
                    $"{TextName(n.Text!)} ({F(n.Text!.Size)} DIP, weight {n.Text!.Weight.ToString(CultureInfo.InvariantCulture)}) is {voice} on {s.Theme}" + (list.Count > 1 ? $" ({list.Count} runs of {n.Pattern})" : ""), refs)
                {
                    Measured = [M("size", n.Text!.Size), M("weight", n.Text!.Weight), M("count", list.Count)],
                    Pattern = n.Pattern,
                    Cause = cause,
                    Count = list.Count,
                    Rects = rects,
                    Nodes = Ids(list),
                    Fix = zune
                        ? "give the part Zune's voice through its roles (R.* type roles and the theme's case transform), or give the page its own Zune view; never shrink Zune display type"
                        : "report; do not fix unless asked (Classic)",
                });
            }
            return looked;
        }

        // ── state.rest ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The words a saved or following state reads (en-US, the inspector's culture) and the glyphs that draw it.</summary>
        static readonly HashSet<string> StateWords = new(StringComparer.Ordinal)
        {
            "follow", "following", "unfollow", "save", "saved", "save to your library", "remove from your library", "add to library",
            "remove from library", "in your library", "liked", "like", "subscribe", "subscribed",
        };

        static readonly HashSet<string> StateGlyphs = new(StringComparer.Ordinal) { "Heart", "HeartFill", "Contact", "FavoriteStar", "FavoriteStarFill", "Like" };

        /// <summary>state.rest (visual.md 4; owner rule "state is visible at rest"): an album, artist or show page whose own subject shows no
        /// saved or following toggle on screen at rest (Zune's short layout at 800 x 600 drops the band's verb row: the album keeps share and
        /// more, the artist a radio glyph). A toggle counts when it acts on the page's entity (its key, else no row's, and it sits in the page),
        /// is on screen and uncovered, and reads a state word or draws a heart or a person. Playlists are not judged (an owned one has no such
        /// state).</summary>
        static int StateRestRule(Snapshot s, List<Finding> into)
        {
            if (s.RouteName is not ("album" or "artist" or "show") || s.PageEntity.Length == 0) return 0;
            bool paged = HasPageSlot(s);
            foreach (Node n in s.Nodes)
            {
                if (!n.Interactive || n.Disabled || !n.InScope || Hidden(n)) continue;
                bool state = StateWords.Contains(n.Label.Trim().ToLowerInvariant()) || StateGlyphs.Contains(n.LabelGlyph);
                if (!state) continue;
                if (string.Equals(SubjectOf(s, n, paged), s.PageEntity, StringComparison.Ordinal)) return 1;
            }
            into.Add(new Finding(Rules.StateRest, Severity.Warn,
                $"the page's subject {s.PageEntity} shows no saved or following state on screen at rest (no toggle that acts on it reads follow/save or draws a heart)", [])
            {
                Measured = [M("toggles", 0)],
                Expected = [M("toggles", 1)],
                Pattern = "#page",
                Cause = s.RouteName,
                Fix = "keep the subject's state toggle on screen at rest at every size (owner rule: state is visible at rest); never drop the feature at a tier",
            });
            return 1;
        }

        // ── playing.status ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>playing.status (visual.md 2, 6): the now-playing surfaces disagree with the player. While it holds a track (playing or
        /// paused), a run reading "Nothing playing" (the device picker's "nothing playing" while a track plays), or a source line the stage
        /// keeps empty (`stage:transport:source`: the board's "playing from ..." is blank). Needs the player's phase (a live capture).</summary>
        static int PlayingStatusRule(Snapshot s, List<Finding> into)
        {
            if (s.Playing is not ("playing" or "paused")) return 0;
            int looked = 0;
            var idle = new List<Node>();
            var empty = new List<Node>();
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Covered) continue;
                if (n.Text is { } t && IsWords(n))
                {
                    looked++;
                    if (string.Equals(t.String.Trim(), "nothing playing", StringComparison.OrdinalIgnoreCase)) idle.Add(n);
                }
                else if (n.Key is { } key && key.StartsWith("stage:", StringComparison.Ordinal) && key.EndsWith(":source", StringComparison.Ordinal)
                         && n.Rect.W >= 40f && n.TextCount == 0)
                {
                    looked++;
                    empty.Add(n);
                }
            }
            string what = s.PlayingTitle.Length > 0 ? Q(s.PlayingTitle) : "a track";
            if (idle.Count > 0)
            {
                var (refs, rects) = Members(idle);
                into.Add(new Finding(Rules.PlayingStatus, Severity.Warn, $"{TextName(idle[0].Text!)} shows while the player is {s.Playing} {what}", refs)
                {
                    Pattern = idle[0].Pattern,
                    Cause = "nothing-playing",
                    Count = idle.Count,
                    Rects = rects,
                    Nodes = Ids(idle),
                    Fix = "read the surface from the player's state (the now-playing signals), not from a default that never updates",
                });
            }
            if (empty.Count > 0)
            {
                var (refs, rects) = Members(empty);
                into.Add(new Finding(Rules.PlayingStatus, Severity.Warn, $"{empty[0].Ref} is empty while the player is {s.Playing} {what} (the source line says where it plays from)", refs)
                {
                    Pattern = empty[0].Pattern,
                    Cause = "empty-source",
                    Count = empty.Count,
                    Rects = rects,
                    Nodes = Ids(empty),
                    Fix = "fill the line from the playing context (Playback's source) as the board draws it",
                });
            }
            return looked;
        }

        // ── control.required ────────────────────────────────────────────────────────────────────────────────────────

        static bool PlayGlyph(Node n)
            => n.LabelGlyph.StartsWith("Play", StringComparison.Ordinal) || n.LabelGlyph.StartsWith("Pause", StringComparison.Ordinal)
               || n.Label.Trim().ToLowerInvariant() is "play" or "pause" or "resume";

        /// <summary>control.required (visual.md 5): a surface without a control it must offer. A transport the inspector holds shown
        /// (`chrome=shown`) over a full-window video or the now-playing stage must show a play/pause target and a seek control (a slider or a
        /// drag bar) on screen: Zune's fullscreen video shows only its top row.</summary>
        static int ControlRequiredRule(Snapshot s, List<Finding> into)
        {
            if (!s.ChromeHeld) return 0;
            string surface = "";
            Node? host = null;
            foreach (Node n in s.Nodes)
            {
                if (n.Visible.IsEmpty || n.Visible.W * n.Visible.H < 0.5f * s.Width * s.Height) continue;
                if (n.Visual == "Video") { surface = "the fullscreen video"; host = n; break; }
                if (surface.Length == 0 && n.Key is { } key && key.StartsWith("stage:", StringComparison.Ordinal)) { surface = "the now-playing stage"; host = n; }
            }
            if (host is null) return 0;
            bool play = false, seek = false;
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Covered || n.Disabled) continue;
                if (n.Interactive && PlayGlyph(n)) play = true;
                if ((n.Role is "Slider" || n.Drag) && n.Rect.W >= 100f && n.Rect.W > 3f * n.Rect.H) seek = true;
            }
            if (play && seek) return 1;
            string missing = !play && !seek ? "a play/pause target and a seek control" : !play ? "a play/pause target" : "a seek control";
            into.Add(new Finding(Rules.ControlRequired, Severity.Warn, $"{surface} holds its transport shown (chrome=shown) but has no {missing} on screen", [host.Ref])
            {
                Pattern = host.Pattern,
                Cause = (!play ? "play" : "") + (!seek ? "seek" : ""),
                Rects = [host.Visible],
                Nodes = [host.Index],
                Fix = "draw the surface's transport (play/pause, seek) as its board does at this size; never call a surface without one done",
            });
            return 1;
        }

        // ── edge.window ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>edge.window (winui.md 5.8): content laid out past the window's right edge: a node that holds words or a target, starts inside
        /// the window and ends 2 DIP or more past it, outside any rail (a horizontal scroller reveals its overflow) and not cut inside the window by
        /// a clipping ancestor (that is text.clip's). WinUI's Small artist reader laid `verbs:row` out to x840 in an 800 window. The topmost such
        /// node speaks for its subtree.</summary>
        static int PastWindowRule(Snapshot s, List<Finding> into)
        {
            IReadOnlyList<Node> nodes = s.Nodes;
            var past = new bool[nodes.Count];
            int looked = 0;
            var hits = new List<Node>();
            foreach (Node n in nodes)
            {
                if (n.Moving || n.Covered || n.OnScreen <= 0f || n.InRail || n.ScrollAxis == 1) continue;
                if (n.TextCount == 0 && !n.Interactive) continue;
                if (n.Rect.X >= s.Width - 1f || n.Rect.Right <= s.Width + 2f || n.Rect.W > s.Width) continue;
                looked++;
                bool cut = false;
                for (int a = n.Parent; a >= 0 && !cut; a = nodes[a].Parent)
                {
                    Node up = nodes[a];
                    if ((up.Clips || up.ScrollAxis != 0 || !up.ClipRect.IsInfinite) && up.Rect.Right <= s.Width + 0.5f && up.Rect.Right < n.Rect.Right - 1f) cut = true;
                    if (up.ScrollAxis == 1) cut = true;
                }
                if (cut) continue;
                past[n.Index] = true;
                if (n.Parent >= 0 && past[n.Parent]) continue;
                hits.Add(n);
            }
            foreach (Node n in hits)
                into.Add(new Finding(Rules.EdgeWindow, Severity.Warn,
                    $"{n.Ref} is laid out to x={F(n.Rect.Right)}, {F(n.Rect.Right - s.Width)} past the {F(s.Width)} DIP window" +
                    (n.TextCount > 0 ? $" ({n.TextCount} text run{(n.TextCount == 1 ? "" : "s")} under it)" : ""), [n.Ref])
                {
                    Measured = [M("right", n.Rect.Right), M("past", n.Rect.Right - s.Width)],
                    Expected = [M("right", s.Width)],
                    Pattern = n.Pattern,
                    Cause = "past-window",
                    Rects = [n.Rect],
                    Nodes = [n.Index],
                    Fix = "let the row take the window's width at this tier (Shrink=1, MinWidth=0, a wrap or a restack from the tier record); never clip it to hide the overflow",
                });
            return looked;
        }

        // ── space.huge ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>space.huge (visual.md 9, winui.md 5.8): at Huge and Huge4K, a band of the page from top to bottom with nothing in it, as wide
        /// as a column (320 DIP and 20 % of the page or more): Zune centres a 2400 column at 4K and leaves 650 empty each side; the settings page
        /// uses 760 of 2400. The page's own full-width plates and art (90 % of its width or more) are its ground, not content.</summary>
        static int HugeSpaceRule(Snapshot s, List<Finding> into)
        {
            if (s.Tier is not (WidthTier.Huge or WidthTier.Huge4K)) return 0;
            Node? page = null;
            foreach (Node n in s.Nodes)
                if (n.Key is { } key && TrySlotRoute(key, out _, out _) && !n.Visible.IsEmpty && (page is null || n.Visible.W * n.Visible.H > page.Visible.W * page.Visible.H)) page = n;
            if (page is null) return 0;
            RectF p = page.Visible;
            int cells = Math.Max(1, (int)MathF.Ceiling(p.W / 4f));
            var covered = new bool[cells];
            int looked = 0;
            foreach (Node n in s.Nodes)
            {
                if (!n.Ink || n.Covered || n.Visible.IsEmpty || !Under(s, n.Index, page.Index)) continue;
                RectF r = n.Visible.Intersect(p);
                if (r.IsEmpty || r.W >= 0.9f * p.W) continue;
                looked++;
                int a = Math.Clamp((int)((r.X - p.X) / 4f), 0, cells - 1), b = Math.Clamp((int)MathF.Ceiling((r.Right - p.X) / 4f), 0, cells);
                for (int i = a; i < b; i++) covered[i] = true;
            }
            int bestStart = -1, bestLen = 0;
            for (int i = 0; i < cells;)
            {
                if (covered[i]) { i++; continue; }
                int j = i;
                while (j < cells && !covered[j]) j++;
                if (j - i > bestLen) { bestLen = j - i; bestStart = i; }
                i = j;
            }
            float width = bestLen * 4f;
            if (width < MathF.Max(320f, 0.2f * p.W)) return looked;
            float x0 = p.X + bestStart * 4f;
            into.Add(new Finding(Rules.SpaceHuge, Severity.Warn,
                $"the page leaves x={F(x0)}..{F(x0 + width)} ({F(width)} DIP, {F(100f * width / p.W)} % of its {F(p.W)}) empty from top to bottom at {s.Tier}", [page.Ref])
            {
                Measured = [M("x", x0), M("w", width), M("share", width / p.W)],
                Pattern = page.Pattern,
                Cause = x0 <= p.X + 1f ? "left" : x0 + width >= p.Right - 4f ? "right" : "middle",
                Rects = [new RectF(x0, p.Y, width, p.H)],
                Nodes = [page.Index],
                Fix = "follow the Huge board: lead at the window gutter and give the room to a docked pane or a wider grid (D3); never stretch a row to fill it",
            });
            return looked;
        }
    }
}
