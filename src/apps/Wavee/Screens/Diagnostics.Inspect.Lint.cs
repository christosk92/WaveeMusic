// ── Screens/Diagnostics.Inspect.Lint.cs — the `--inspect` lint rules: a snapshot of nodes in, findings out ───────────────────
// gutter, right-edge, clip, trim, type, target, duplicate and rhythm over a flat list of the mounted scene's visible nodes
//
// Role: CORE (pure: no engine, no host, no thread; the scene is read into a `Snapshot` by Diagnostics.Inspect.Lint.Scene.cs)
// Spec: handoff/themes-2026-10-17/agent-inspect-plan.md lane A3; docs/guide/agent-inspect.md is the user-facing reference.
//
// THE SNAPSHOT is the dump's node list without the JSON: depth-first, `Nodes[i].Index == i`, a node's `Parent` an earlier index (-1 for a
// root), every rect the PRESENTED absolute rect in window DIP. Only what paints is in it (hidden, parked and empty-box nodes are not), so a
// finding is always about something on screen. A rule reads the snapshot and its options and nothing else, so each is unit-testable on a
// hand-built tree (Wavee.Tests/InspectLintTests.cs). The two EDGE rules (gutter, right-edge) check the expectations a theme's guide
// describes (`IThemeLintGuide`); every other rule is theme-blind.
//
// THE FINDING: rule, severity (error|warn|info), the node keys involved (a keyless node is named by its dump path), what was MEASURED, what
// was EXPECTED and a one-line message. Errors sort first within a rule; a rule keeps at most `MaxPerRule` findings (the reply names the rules it cut).
//
// CONSERVATIVE BY DESIGN. A scroller's own axis is allowed to cut its content (a pivot run fading at the window's edge, a list scrolled
// past its fold), ZStack children and sticky headers are allowed to overlap, glyph (icon) text is not text for the type scale, and
// siblings only count as overlapping when both carry text or a target.

using System.Globalization;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>The lint's pure half: the snapshot model, the options, the rules. See the file header.</summary>
    public static class InspectLint
    {
        // ── the rules ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The rule names (what `rules=` takes and a finding's <c>rule</c> says).</summary>
        public static class Rules
        {
            public const string Gutter = "gutter", RightEdge = "right-edge", Clip = "clip", Trim = "trim", Type = "type",
                Target = "target", Duplicate = "duplicate", Rhythm = "rhythm";
        }

        /// <summary>Every rule, in the order the report lists them.</summary>
        public static readonly string[] RuleNames =
            [Rules.Gutter, Rules.RightEdge, Rules.Clip, Rules.Trim, Rules.Type, Rules.Target, Rules.Duplicate, Rules.Rhythm];

        /// <summary>How tall the ink of a line of text is, in ems: ascender to descender. A clip that cuts below this much of a line cuts descenders.</summary>
        public const float InkEm = 1.2f;

        /// <summary>The slack (DIP) before an edge, a clip or an overlap counts: sub-pixel rounding is not a finding.</summary>
        public const float Slack = 0.5f;

        public enum Severity : byte { Info, Warn, Error }

        public static string Name(Severity s) => s switch { Severity.Error => "error", Severity.Warn => "warn", _ => "info" };

        public static bool TryParseSeverity(string word, out Severity severity)
        {
            switch (word.Trim().ToLowerInvariant())
            {
                case "error": severity = Severity.Error; return true;
                case "warn" or "warning": severity = Severity.Warn; return true;
                case "info": severity = Severity.Info; return true;
                default: severity = Severity.Info; return false;
            }
        }

        // ── the snapshot ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One text run of a text node: the style the layout used and what the engine says about its fit.</summary>
        public sealed record TextRun(string String, float Size)
        {
            /// <summary>The line height in DIP, 0 when the font's natural one is used.</summary>
            public float LineHeight { get; init; }
            public int Weight { get; init; } = 400;
            public int MaxLines { get; init; }
            public bool Wraps { get; init; }

            /// <summary>The trim mode name (<c>None</c>, <c>Clip</c>, <c>CharacterEllipsis</c>, <c>WordEllipsis</c>).</summary>
            public string Trim { get; init; } = "None";

            /// <summary>The engine says the run does not show all of its content (<c>QueryTextOverflow</c>).</summary>
            public bool Trimmed { get; init; }

            /// <summary>The size the run wants, uncut.</summary>
            public float NaturalW { get; init; }
            public float NaturalH { get; init; }
            public bool OverflowX { get; init; }
            public bool OverflowY { get; init; }

            /// <summary>An icon glyph (a private-use character), not words: out of the type scale and the clip rule.</summary>
            public bool Glyph { get; init; }

            /// <summary>A span run (its words live in the spans, <see cref="String"/> is empty).</summary>
            public bool Spans { get; init; }
        }

        /// <summary>One visible node. <c>Index</c> is its place in <see cref="Snapshot.Nodes"/>.</summary>
        public sealed record Node(int Index, int Parent, string? Key, string Path, RectF Rect)
        {
            public int Depth { get; init; }
            public string Kind { get; init; } = "";

            /// <summary>The node clips its children to its bounds.</summary>
            public bool Clips { get; init; }

            /// <summary>0 not a scroller, 1 scrolls horizontally, 2 vertically.</summary>
            public int ScrollAxis { get; init; }

            /// <summary>Its children overlay each other on purpose.</summary>
            public bool ZStack { get; init; }

            /// <summary>It lays its children out in a column (vertical stack).</summary>
            public bool Column { get; init; } = true;

            /// <summary>A click target (a click handler or an action role), hit-testable.</summary>
            public bool Interactive { get; init; }
            public bool Disabled { get; init; }
            public string Role { get; init; } = "";

            /// <summary>The first text under an interactive node (an icon-only button: its glyph).</summary>
            public string Label { get; init; } = "";

            /// <summary>It carries live animation tracks (mid-transition): skipped by the geometry rules.</summary>
            public bool Animating { get; init; }

            /// <summary>A sticky header (pinned over the rows under it on purpose).</summary>
            public bool Sticky { get; init; }

            public TextRun? Text { get; init; }

            /// <summary>The words (not glyph runs) in this node's subtree, itself included.</summary>
            public int TextCount { get; init; }

            /// <summary>What a finding calls the node: its key, else its path.</summary>
            public string Ref => Key ?? Path;
        }

        /// <summary>What the rules read.</summary>
        public sealed record Snapshot(float Width, float Height, WidthTier Tier, IReadOnlyList<Node> Nodes)
        {
            /// <summary>The active theme's edge expectations (the gutter and right-edge rules check these).</summary>
            public IReadOnlyList<LintEdge> Edges { get; init; } = [];

            public string Theme { get; init; } = "";
        }

        // ── the options ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Key prefixes under which the duplicate rule does not look: window chrome and the play bar repeat the page's verbs by design.</summary>
        public static readonly string[] DefaultIgnoreUnder = ["shell.titlebar", "shell.playerbar"];

        public sealed record Options
        {
            /// <summary>The rules that run (default: all).</summary>
            public IReadOnlyCollection<string> Rules { get; init; } = RuleNames;

            /// <summary>Text under this size (DIP) is an error. Default 11.</summary>
            public float TypeFloor { get; init; } = 11f;

            /// <summary>The smallest hit target (DIP) outside Compact. Default 32.</summary>
            public float TargetFloor { get; init; } = 32f;

            /// <summary>The smallest hit target (DIP) at Compact. Default 40.</summary>
            public float TargetFloorCompact { get; init; } = 40f;

            /// <summary>Stacked text blocks closer than this (DIP) are cramped. Default 2.</summary>
            public float GapMin { get; init; } = 2f;

            /// <summary>Keys or key prefixes (of the node or any ancestor) whose text may be trimmed.</summary>
            public IReadOnlyList<string> AllowTrim { get; init; } = [];

            /// <summary>Labels the duplicate rule lets repeat.</summary>
            public IReadOnlyList<string> AllowDuplicate { get; init; } = [];

            /// <summary>Key prefixes the duplicate rule skips (default: the title bar and the play bar).</summary>
            public IReadOnlyList<string> IgnoreUnder { get; init; } = DefaultIgnoreUnder;

            /// <summary>The most findings one rule reports.</summary>
            public int MaxPerRule { get; init; } = 100;

            /// <summary>Findings below this severity are left out of the report.</summary>
            public Severity Min { get; init; } = Severity.Info;

            public bool Enabled(string rule)
            {
                foreach (string r in Rules)
                    if (string.Equals(r, rule, StringComparison.Ordinal)) return true;
                return false;
            }
        }

        // ── the findings ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A named number: one measured or expected value of a finding.</summary>
        public readonly record struct Metric(string Name, double Value);

        public sealed record Finding(string Rule, Severity Severity, string Message, IReadOnlyList<string> Keys)
        {
            public IReadOnlyList<Metric> Measured { get; init; } = [];
            public IReadOnlyList<Metric> Expected { get; init; } = [];
        }

        /// <summary>The findings, how many things each rule looked at, and the rules whose findings were cut at the cap.</summary>
        public sealed record Report(IReadOnlyList<Finding> Findings, IReadOnlyDictionary<string, int> Checked, IReadOnlyList<string> Truncated)
        {
            public int Count(Severity severity)
            {
                int n = 0;
                foreach (Finding f in Findings)
                    if (f.Severity == severity) n++;
                return n;
            }

            public int Count(string rule, Severity severity)
            {
                int n = 0;
                foreach (Finding f in Findings)
                    if (f.Severity == severity && string.Equals(f.Rule, rule, StringComparison.Ordinal)) n++;
                return n;
            }
        }

        // ── running ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Run the enabled rules over <paramref name="snapshot"/>.</summary>
        public static Report Run(Snapshot snapshot, Options? options = null)
        {
            Options o = options ?? new Options();
            var all = new List<Finding>();
            var checks = new Dictionary<string, int>(StringComparer.Ordinal);
            var truncated = new List<string>();
            foreach (string rule in RuleNames)
            {
                if (!o.Enabled(rule)) continue;
                var found = new List<Finding>();
                int looked = rule switch
                {
                    Rules.Gutter or Rules.RightEdge => EdgeRule(snapshot, rule, found),
                    Rules.Clip => ClipRule(snapshot, found) + OverlapRule(snapshot, found),
                    Rules.Trim => TrimRule(snapshot, o, found),
                    Rules.Type => TypeRule(snapshot, o, found),
                    Rules.Target => TargetRule(snapshot, o, found),
                    Rules.Duplicate => DuplicateRule(snapshot, o, found),
                    Rules.Rhythm => RhythmRule(snapshot, o, found),
                    _ => 0,
                };
                checks[rule] = looked;
                List<Finding> ordered = found.OrderByDescending(static f => f.Severity).ToList();   // stable: a rule's own order within a severity
                ordered.RemoveAll(f => f.Severity < o.Min);
                if (ordered.Count > o.MaxPerRule)
                {
                    ordered.RemoveRange(o.MaxPerRule, ordered.Count - o.MaxPerRule);
                    truncated.Add(rule);
                }
                all.AddRange(ordered);
            }
            return new Report(all, checks, truncated);
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        static string Q(string text)
        {
            string one = text.Replace('\n', ' ');
            return "'" + (one.Length > 40 ? one[..37] + "..." : one) + "'";
        }

        static Metric M(string name, double value) => new(name, Math.Round(value, 2));

        static bool HasKey(Node n, string key) => n.Key is not null && string.Equals(n.Key, key, StringComparison.Ordinal);

        /// <summary>The node or an ancestor has the key <paramref name="key"/>.</summary>
        static bool InsideKey(Snapshot s, Node n, string key)
        {
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
                if (HasKey(s.Nodes[i], key)) return true;
            return false;
        }

        /// <summary>The key of the node or of an ancestor equals, or starts with, one of <paramref name="list"/>.</summary>
        static bool KeyedUnder(Snapshot s, Node n, IReadOnlyList<string> list)
        {
            if (list.Count == 0) return false;
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
            {
                string? key = s.Nodes[i].Key;
                if (key is null) continue;
                foreach (string entry in list)
                    if (entry.Length > 0 && key.StartsWith(entry, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static bool IsWords(Node n) => n.Text is { Glyph: false } t && (t.String.Length > 0 || t.Spans);

        // ── gutter and right-edge: a theme's edge expectations ─────────────────────────────────────────────────────

        static bool EdgeMatches(Snapshot s, Node n, LintEdge e)
        {
            if (n.Rect.W <= 0f || n.Rect.H <= 0f) return false;
            if (n.Rect.Right <= 0f || n.Rect.X >= s.Width) return false;   // off the window: scrolled away, not an edge
            bool keyed = e.Keys is { Count: > 0 } || e.Prefixes is { Count: > 0 };
            if (keyed)
            {
                if (n.Key is null) return false;
                bool hit = false;
                if (e.Keys is not null)
                    foreach (string k in e.Keys)
                        if (string.Equals(n.Key, k, StringComparison.Ordinal)) { hit = true; break; }
                if (!hit && e.Prefixes is not null)
                    foreach (string p in e.Prefixes)
                        if (n.Key.StartsWith(p, StringComparison.Ordinal)) { hit = true; break; }
                if (!hit) return false;
            }
            else if (e.UnderKey.Length == 0) return false;
            if (e.UnderKey.Length > 0 && !InsideKey(s, n, e.UnderKey)) return false;
            if (e.GlyphsOnly && n.Text is not { Glyph: true }) return false;
            return true;
        }

        /// <summary>For each of the guide's edges of <paramref name="rule"/>: find the leftmost (or rightmost) matching node and compare its edge. An
        /// edge nothing on screen matches is not checked (and not a finding).</summary>
        static int EdgeRule(Snapshot s, string rule, List<Finding> into)
        {
            int looked = 0;
            foreach (LintEdge e in s.Edges)
            {
                if (!string.Equals(e.Rule, rule, StringComparison.Ordinal)) continue;
                bool left = e.Side == LintSide.Left;
                Node? pick = null;
                float at = 0f;
                foreach (Node n in s.Nodes)
                {
                    if (!EdgeMatches(s, n, e)) continue;
                    float x = left ? n.Rect.X : n.Rect.Right;
                    if (pick is null || (left ? x < at : x > at)) { pick = n; at = x; }
                }
                if (pick is null) continue;
                looked++;
                float delta = at - e.Expected;
                if (MathF.Abs(delta) <= MathF.Max(e.Tolerance, Slack)) continue;
                string side = left ? "left" : "right";
                into.Add(new Finding(rule, e.Advisory ? Severity.Warn : Severity.Error,
                    $"{e.What} ({e.Id}): {side} edge at {F(at)}, expected {F(e.Expected)} ({(delta > 0f ? "+" : "")}{F(delta)})", [pick.Ref])
                {
                    Measured = [M("x", at), M("delta", delta)],
                    Expected = [M("x", e.Expected), M("tolerance", e.Tolerance)],
                });
            }
            return looked;
        }

        // ── clip: text cut by an ancestor, and siblings that overlap ───────────────────────────────────────────────

        /// <summary>The box the ink of a text node needs: its laid-out box, the natural height of an unwrapped run that wants more, and the
        /// ink a tight explicit line height leaves outside the line box (<see cref="InkEm"/>).</summary>
        static RectF InkBox(Node n, TextRun t)
        {
            RectF r = n.Rect;
            float y = r.Y, h = r.H;
            if (!t.Wraps && t.NaturalH > h) h = t.NaturalH;
            if (t.LineHeight > 0f)
            {
                float pad = MathF.Max(0f, (InkEm * t.Size - t.LineHeight) / 2f);
                y -= pad;
                h += 2f * pad;
            }
            return new RectF(r.X, y, r.W, h);
        }

        /// <summary>Text whose ink runs outside the nearest clipping ancestor that cuts it (clipped descenders are the vertical case). An
        /// ancestor that SCROLLS on an axis is allowed to cut on that axis, and so is everything above it.</summary>
        static int ClipRule(Snapshot s, List<Finding> into)
        {
            int looked = 0;
            IReadOnlyList<Node> nodes = s.Nodes;
            foreach (Node n in nodes)
            {
                if (n.Text is not { } t || !IsWords(n) || n.Animating) continue;
                looked++;
                RectF ink = InkBox(n, t);
                bool scrollsV = false, scrollsH = false;
                for (int a = n.Parent; a >= 0; a = nodes[a].Parent)
                {
                    Node anc = nodes[a];
                    if (!anc.Clips || anc.Rect.W <= 0f || anc.Rect.H <= 0f) continue;
                    RectF c = anc.Rect;
                    if (!ink.Overlaps(c)) break;   // wholly outside the clip: hidden, not clipped
                    if (anc.ScrollAxis == 2) scrollsV = true;
                    if (anc.ScrollAxis == 1) scrollsH = true;
                    float vOver = scrollsV ? 0f : MathF.Max(c.Y - ink.Y, ink.Bottom - c.Bottom);
                    float hOver = scrollsH ? 0f : MathF.Max(c.X - ink.X, ink.Right - c.Right);
                    if (vOver > Slack)
                    {
                        bool above = c.Y - ink.Y > ink.Bottom - c.Bottom;
                        into.Add(new Finding(Rules.Clip, Severity.Error,
                            $"text {Q(t.String)} is clipped by {anc.Ref}: its ink runs {F(vOver)} past the {(above ? "top" : "bottom")} ({F(ink.Y)}..{F(ink.Bottom)} in {F(c.Y)}..{F(c.Bottom)})",
                            [n.Ref, anc.Ref])
                        {
                            Measured = [M("inkTop", ink.Y), M("inkBottom", ink.Bottom), M("over", vOver), M("size", t.Size), M("lineHeight", t.LineHeight)],
                            Expected = [M("clipTop", c.Y), M("clipBottom", c.Bottom)],
                        });
                        break;
                    }
                    if (hOver > Slack)
                    {
                        bool leftSide = c.X - ink.X > ink.Right - c.Right;
                        into.Add(new Finding(Rules.Clip, Severity.Warn,
                            $"text {Q(t.String)} is clipped by {anc.Ref}: it runs {F(hOver)} past the {(leftSide ? "left" : "right")} edge ({F(ink.X)}..{F(ink.Right)} in {F(c.X)}..{F(c.Right)})",
                            [n.Ref, anc.Ref])
                        {
                            Measured = [M("inkLeft", ink.X), M("inkRight", ink.Right), M("over", hOver)],
                            Expected = [M("clipLeft", c.X), M("clipRight", c.Right)],
                        });
                        break;
                    }
                }
            }
            return looked;
        }

        /// <summary>Sibling boxes that overlap, in a parent that is not a ZStack or a scroller, when both carry text or a target. Pinned headers
        /// and nodes mid-animation are left out.</summary>
        static int OverlapRule(Snapshot s, List<Finding> into)
        {
            int looked = 0;
            IReadOnlyList<Node> nodes = s.Nodes;
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < nodes.Count; i++)
            {
                Node n = nodes[i];
                if (n.Parent < 0 || n.Animating || n.Sticky) continue;
                if (n.TextCount == 0 && !n.Interactive) continue;
                if (!groups.TryGetValue(n.Parent, out List<int>? list)) groups[n.Parent] = list = [];
                list.Add(i);
            }
            foreach (var (parentIndex, list) in groups)
            {
                Node parent = nodes[parentIndex];
                if (parent.ZStack || parent.ScrollAxis != 0 || list.Count < 2) continue;
                int cap = Math.Min(list.Count, 200);
                for (int x = 0; x < cap; x++)
                {
                    for (int y = x + 1; y < cap; y++)
                    {
                        Node a = nodes[list[x]], b = nodes[list[y]];
                        looked++;
                        RectF o = a.Rect.Intersect(b.Rect);
                        if (o.W <= 1f || o.H <= 1f) continue;
                        into.Add(new Finding(Rules.Clip, Severity.Warn,
                            $"siblings {a.Ref} and {b.Ref} overlap by {F(o.W)} x {F(o.H)} in {parent.Ref}", [a.Ref, b.Ref, parent.Ref])
                        {
                            Measured = [M("overlapX", o.X), M("overlapY", o.Y), M("overlapW", o.W), M("overlapH", o.H)],
                            Expected = [M("overlapW", 0), M("overlapH", 0)],
                        });
                    }
                }
            }
            return looked;
        }

        // ── trim ────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Text the engine reports trimmed (<c>QueryTextOverflow</c>), unless the node or an ancestor is in <see cref="Options.AllowTrim"/>.</summary>
        static int TrimRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            foreach (Node n in s.Nodes)
            {
                if (n.Text is not { } t || !IsWords(n)) continue;
                looked++;
                if (!t.Trimmed || KeyedUnder(s, n, o.AllowTrim)) continue;
                string how = t.Wraps
                    ? (t.MaxLines > 0 && t.OverflowY ? $"wraps past its {t.MaxLines}-line budget" : "wraps taller than its box")
                    : "is wider than its box";
                string mode = t.Trim switch { "None" => "overflows", "Clip" => "hard-clipped", _ => "ellipsised" };
                into.Add(new Finding(Rules.Trim, Severity.Warn,
                    $"text {Q(t.String)} {how} and is {mode}: natural {F(t.NaturalW)} x {F(t.NaturalH)}, box {F(n.Rect.W)} x {F(n.Rect.H)}", [n.Ref])
                {
                    Measured = [M("w", n.Rect.W), M("h", n.Rect.H), M("size", t.Size)],
                    Expected = [M("w", t.NaturalW), M("h", t.NaturalH)],
                });
            }
            return looked;
        }

        // ── type: the scale in use, and the floor ──────────────────────────────────────────────────────────────────

        readonly record struct ScaleKey(float Size, float Line, int Weight);

        sealed class Bin
        {
            public int Count;
            public readonly List<string> Samples = [];

            public void Add(string sample, int cap)
            {
                Count++;
                if (Samples.Count < cap) Samples.Add(sample);
            }
        }

        /// <summary>Every distinct size / line height / weight with its count (info), and an error per size under <see cref="Options.TypeFloor"/>.</summary>
        static int TypeRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            var scale = new Dictionary<ScaleKey, Bin>();
            var small = new SortedDictionary<float, Bin>();
            foreach (Node n in s.Nodes)
            {
                if (n.Text is not { } t || !IsWords(n)) continue;
                looked++;
                var key = new ScaleKey(MathF.Round(t.Size, 2), MathF.Round(t.LineHeight, 2), t.Weight);
                if (!scale.TryGetValue(key, out Bin? bin)) scale[key] = bin = new Bin();
                bin.Add(n.Ref, 5);
                if (t.Size + 0.01f < o.TypeFloor)
                {
                    if (!small.TryGetValue(key.Size, out Bin? low)) small[key.Size] = low = new Bin();
                    low.Add(n.Ref, 8);
                }
            }
            foreach (var (size, bin) in small)
            {
                into.Add(new Finding(Rules.Type, Severity.Error,
                    $"{bin.Count} text node{(bin.Count == 1 ? "" : "s")} at {F(size)} DIP, under the {F(o.TypeFloor)} DIP floor", bin.Samples)
                {
                    Measured = [M("size", size), M("count", bin.Count)],
                    Expected = [M("minSize", o.TypeFloor)],
                });
            }
            foreach (var (key, bin) in scale.OrderByDescending(static kv => kv.Key.Size).ThenByDescending(static kv => kv.Key.Line).ThenBy(static kv => kv.Key.Weight))
            {
                string line = key.Line > 0f ? F(key.Line) : "auto";
                into.Add(new Finding(Rules.Type, Severity.Info,
                    $"type scale: {F(key.Size)}/{line} weight {key.Weight} x{bin.Count}", bin.Samples)
                {
                    Measured = [M("size", key.Size), M("lineHeight", key.Line), M("weight", key.Weight), M("count", bin.Count)],
                });
            }
            return looked;
        }

        // ── target ──────────────────────────────────────────────────────────────────────────────────────────────────

        static bool ExemptFromTarget(Node n) => n.Role is "Slider" or "ScrollBar" or "ProgressBar" or "Hyperlink";

        /// <summary>Click targets smaller than <see cref="Options.TargetFloorCompact"/> (Compact: an error) or <see cref="Options.TargetFloor"/> (a warning).
        /// A target that sits exactly on its interactive parent is that parent's, not its own.</summary>
        static int TargetRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            bool compact = s.Tier == WidthTier.Compact;
            float floor = compact ? o.TargetFloorCompact : o.TargetFloor;
            foreach (Node n in s.Nodes)
            {
                if (!n.Interactive || n.Disabled || ExemptFromTarget(n)) continue;
                looked++;
                if (n.Rect.W >= floor - Slack && n.Rect.H >= floor - Slack) continue;
                if (n.Parent >= 0)
                {
                    Node p = s.Nodes[n.Parent];
                    if (p.Interactive && MathF.Abs(p.Rect.W - n.Rect.W) <= Slack && MathF.Abs(p.Rect.H - n.Rect.H) <= Slack) continue;
                }
                string what = n.Label.Length > 0 ? " " + Q(n.Label) : "";
                into.Add(new Finding(Rules.Target, compact ? Severity.Error : Severity.Warn,
                    $"hit target {n.Ref}{what} is {F(n.Rect.W)} x {F(n.Rect.H)}, under {F(floor)} x {F(floor)}"
                    + (compact ? " at Compact" : ""), [n.Ref])
                {
                    Measured = [M("w", n.Rect.W), M("h", n.Rect.H)],
                    Expected = [M("w", floor), M("h", floor)],
                });
            }
            return looked;
        }

        // ── duplicate ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The surface an action belongs to: the nearest keyed ancestor's key up to its first <c>:</c> or <c>#</c> (rows of one list share one).</summary>
        static string Home(Snapshot s, Node n)
        {
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
            {
                if (s.Nodes[i].Key is not { Length: > 0 } key) continue;
                int cut = key.AsSpan().IndexOfAny(':', '#');
                return cut > 0 ? key[..cut] : key;
            }
            return "";
        }

        /// <summary>The same action twice: visible click targets with the same role and label whose homes differ (the band's Play and the hero's Play).</summary>
        static int DuplicateRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            var groups = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
            foreach (Node n in s.Nodes)
            {
                if (!n.Interactive || n.Disabled || n.Label.Length == 0 || KeyedUnder(s, n, o.IgnoreUnder)) continue;
                if (n.Role is "Slider" or "ScrollBar" or "ProgressBar") continue;
                bool allowed = false;
                foreach (string label in o.AllowDuplicate)
                    if (string.Equals(label, n.Label, StringComparison.OrdinalIgnoreCase)) { allowed = true; break; }
                if (allowed) continue;
                bool inner = false;   // only the outermost target of a label counts
                for (int i = n.Parent; i >= 0 && !inner; i = s.Nodes[i].Parent)
                    inner = s.Nodes[i].Interactive && string.Equals(s.Nodes[i].Label, n.Label, StringComparison.Ordinal);
                if (inner) continue;
                looked++;
                string group = n.Role + "|" + n.Label.ToLowerInvariant();
                if (!groups.TryGetValue(group, out List<Node>? list)) groups[group] = list = [];
                list.Add(n);
            }
            foreach (var (_, list) in groups)
            {
                if (list.Count < 2) continue;
                var homes = new List<string>();
                foreach (Node n in list)
                {
                    string home = Home(s, n);
                    if (!homes.Contains(home)) homes.Add(home);
                }
                if (homes.Count < 2) continue;
                var keys = new List<string>(list.Count);
                foreach (Node n in list)
                    if (keys.Count < 6) keys.Add(n.Ref);
                into.Add(new Finding(Rules.Duplicate, Severity.Warn,
                    $"{Q(list[0].Label)} is offered {list.Count} times on one screen ({string.Join(", ", homes.Select(static h => h.Length == 0 ? "(unkeyed)" : h))})", keys)
                {
                    Measured = [M("count", list.Count), M("homes", homes.Count)],
                    Expected = [M("count", 1)],
                });
            }
            return looked;
        }

        // ── rhythm ──────────────────────────────────────────────────────────────────────────────────────────────────

        static bool IsTextBlock(Node n) => IsWords(n) || (n.Text is null && n.TextCount == 1 && !n.Interactive);

        /// <summary>The vertical gaps between consecutive text blocks stacked in one column (info), and a warning under <see cref="Options.GapMin"/> (cramped, or overlapping).</summary>
        static int RhythmRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            IReadOnlyList<Node> nodes = s.Nodes;
            var groups = new Dictionary<int, List<Node>>();
            foreach (Node n in nodes)
            {
                if (n.Parent < 0 || n.Animating || !IsTextBlock(n)) continue;
                if (!groups.TryGetValue(n.Parent, out List<Node>? list)) groups[n.Parent] = list = [];
                list.Add(n);
            }
            foreach (var (parentIndex, list) in groups)
            {
                Node parent = nodes[parentIndex];
                if (parent.ZStack || !parent.Column || list.Count < 2) continue;
                list.Sort(static (a, b) => a.Rect.Y.CompareTo(b.Rect.Y));
                var gaps = new List<Metric>();
                var refs = new List<string> { parent.Ref };
                for (int i = 0; i + 1 < list.Count; i++)
                {
                    Node a = list[i], b = list[i + 1];
                    if (a.Rect.Intersect(new RectF(b.Rect.X, a.Rect.Y, b.Rect.W, a.Rect.H)).W <= 0f) continue;   // side by side, not stacked
                    float gap = b.Rect.Y - a.Rect.Bottom;
                    looked++;
                    gaps.Add(M("gap" + gaps.Count.ToString(CultureInfo.InvariantCulture), gap));
                    if (refs.Count < 7) { if (!refs.Contains(a.Ref)) refs.Add(a.Ref); if (!refs.Contains(b.Ref) && refs.Count < 7) refs.Add(b.Ref); }
                    if (gap < o.GapMin)
                    {
                        into.Add(new Finding(Rules.Rhythm, Severity.Warn,
                            $"text blocks {a.Ref} and {b.Ref} are {(gap < 0f ? "overlapping by " + F(-gap) : "only " + F(gap) + " apart")} in {parent.Ref} (under {F(o.GapMin)})",
                            [a.Ref, b.Ref])
                        {
                            Measured = [M("gap", gap)],
                            Expected = [M("minGap", o.GapMin)],
                        });
                    }
                }
                if (gaps.Count == 0) continue;
                into.Add(new Finding(Rules.Rhythm, Severity.Info,
                    $"{gaps.Count + 1} stacked text blocks in {parent.Ref}: gaps {string.Join(", ", gaps.Select(static g => F(g.Value)))}", refs)
                {
                    Measured = gaps,
                });
            }
            return looked;
        }
    }
}
