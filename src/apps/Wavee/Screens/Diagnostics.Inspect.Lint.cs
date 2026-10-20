// ── Screens/Diagnostics.Inspect.Lint.cs — the `--inspect` lint rules: a snapshot of nodes in, findings out ───────────────────
// gutter, right-edge, clip, trim, type, target, duplicate, rhythm and contrast over a flat list of the mounted scene's visible nodes
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
                Target = "target", Duplicate = "duplicate", Rhythm = "rhythm", Contrast = "contrast";
        }

        /// <summary>Every rule, in the order the report lists them.</summary>
        public static readonly string[] RuleNames =
            [Rules.Gutter, Rules.RightEdge, Rules.Clip, Rules.Trim, Rules.Type, Rules.Target, Rules.Duplicate, Rules.Rhythm, Rules.Contrast];

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

            /// <summary>The colour the run is declared in (straight alpha). Alpha 0 (the default) is "not known": the contrast rule skips the run.</summary>
            public ColorF Color { get; init; }
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

            /// <summary>The node's opacity times every ancestor's: how much of its colour reaches the screen. Default 1.</summary>
            public float Opacity { get; init; } = 1f;

            /// <summary>The node or an ancestor is disabled: its ink is dimmed on purpose and exempt from the contrast rule.</summary>
            public bool Inert { get; init; }

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

            /// <summary>The rendered frame the contrast rule reads its backgrounds from; null when nothing was rendered (the rule then checks nothing).</summary>
            public Pixels? Backdrop { get; init; }
        }

        // ── the rendered frame ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A rendered frame as tightly packed, top-down BGRA8 (premultiplied, as the offscreen surface captures it), with the pixels per DIP.</summary>
        public sealed class Pixels
        {
            readonly byte[] _bgra;

            public Pixels(byte[] bgra, int width, int height, float scale)
            {
                _bgra = bgra;
                Width = width;
                Height = height;
                Scale = scale;
            }

            public int Width { get; }
            public int Height { get; }
            public float Scale { get; }

            /// <summary>The device-pixel bounds of <paramref name="rect"/> (DIP), clamped to the frame. False when they hold no pixel.</summary>
            bool Bounds(RectF rect, out int x0, out int y0, out int x1, out int y1)
            {
                x0 = Math.Clamp((int)MathF.Floor(rect.X * Scale), 0, Width);
                x1 = Math.Clamp((int)MathF.Ceiling(rect.Right * Scale), 0, Width);
                y0 = Math.Clamp((int)MathF.Floor(rect.Y * Scale), 0, Height);
                y1 = Math.Clamp((int)MathF.Ceiling(rect.Bottom * Scale), 0, Height);
                return x1 > x0 && y1 > y0;
            }

            /// <summary>The straight-alpha colour of pixel <paramref name="i"/> (a BGRA byte offset), 0..1 per channel.</summary>
            void Rgb(int i, out float r, out float g, out float b)
            {
                int a = Math.Max(1, (int)_bgra[i + 3]);
                r = Math.Min(255, _bgra[i + 2] * 255 / a) / 255f;
                g = Math.Min(255, _bgra[i + 1] * 255 / a) / 255f;
                b = Math.Min(255, _bgra[i] * 255 / a) / 255f;
            }

            /// <summary>The ground a run's glyphs sit on, judged at its worst. The glyphs are found in the frame itself: a pixel past <see cref="InkShare"/>
            /// of the way from the box's median grey to the run's <paramref name="ink"/> (its alpha times <paramref name="opacity"/>) is ink, and every
            /// device column of <paramref name="rect"/> within <see cref="InkGuard"/> pixels of a column holding ink is the glyphs' (their anti-aliased
            /// edges included). The other columns are the gaps between and around the glyphs: the ground under the line, sampled where it shows. Each
            /// gap pixel's contrast against the ink is taken and the one at <paramref name="percentile"/> of the ordered ratios is the answer (0.1: the
            /// 10 % worst pixels, the WCAG G18 approach for text over imagery). A box with fewer than <see cref="MinGapPixels"/> gap pixels (the ground
            /// itself is ink-coloured: dark text on a dark photograph, light on a bright one) is judged over all of its pixels, which is the honest
            /// answer there: it does not read. False when the rect holds no pixel.</summary>
            public bool TryWorstContrast(RectF rect, ColorF ink, float opacity, float percentile, out double ratio, out ColorF worst, out int gapPixels)
            {
                ratio = 21d;
                worst = default;
                gapPixels = 0;
                if (!Bounds(rect, out int x0, out int y0, out int x1, out int y1)) return false;
                int cols = x1 - x0;
                var inked = new bool[cols];
                // Where the ink lands, as a share of the way from the box's median grey to the ink's own grey: a pixel past InkShare of that way
                // is the glyph's (its core or its anti-aliased edge). Grey is the sRGB-encoded luma, the axis the ink and its ground differ on.
                var greys = new float[cols * (y1 - y0)];
                for (int y = y0, k = 0; y < y1; y++)
                    for (int x = x0; x < x1; x++, k++)
                    {
                        Rgb((y * Width + x) * 4, out float r, out float g, out float b);
                        greys[k] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    }
                float[] sorted = (float[])greys.Clone();
                Array.Sort(sorted);
                float median = sorted[sorted.Length / 2];
                float a = Math.Clamp(ink.A * opacity, 0f, 1f);
                float inkGrey = a * (0.2126f * ink.R + 0.7152f * ink.G + 0.0722f * ink.B) + (1f - a) * median;
                float span = inkGrey - median;
                bool separable = MathF.Abs(span) >= InkSpan;
                if (separable)
                    for (int y = y0, k = 0; y < y1; y++)
                        for (int x = x0; x < x1; x++, k++)
                            if ((greys[k] - median) / span > InkShare) inked[x - x0] = true;
                int guard = Math.Max(1, (int)MathF.Ceiling(InkGuard * Scale));
                var picked = new List<int>();
                for (int x = x0; x < x1; x++)
                {
                    bool near = false;
                    for (int k = Math.Max(x0, x - guard); k <= Math.Min(x1 - 1, x + guard) && !near; k++) near = inked[k - x0];
                    if (near) continue;
                    for (int y = y0; y < y1; y++) picked.Add((y * Width + x) * 4);
                }
                if (separable && picked.Count < MinGapPixels)
                {
                    // A glyph that fills its box (an icon): its ground is the ring just outside the box, less any ink that reaches into it.
                    int ring = Math.Max(1, (int)MathF.Ceiling(RingDip * Scale));
                    int rx0 = Math.Max(0, x0 - ring), rx1 = Math.Min(Width, x1 + ring), ry0 = Math.Max(0, y0 - ring), ry1 = Math.Min(Height, y1 + ring);
                    for (int y = ry0; y < ry1; y++)
                        for (int x = rx0; x < rx1; x++)
                        {
                            if (x >= x0 && x < x1 && y >= y0 && y < y1) continue;
                            int i = (y * Width + x) * 4;
                            Rgb(i, out float r, out float g, out float b);
                            if ((0.2126f * r + 0.7152f * g + 0.0722f * b - median) / span > InkShare) continue;
                            picked.Add(i);
                        }
                }
                gapPixels = picked.Count;
                if (!separable || picked.Count < MinGapPixels)
                {
                    picked.Clear();
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) picked.Add((y * Width + x) * 4);
                }
                var ratios = new double[picked.Count];
                var colors = new ColorF[picked.Count];
                for (int k = 0; k < picked.Count; k++)
                {
                    Rgb(picked[k], out float r, out float g, out float b);
                    colors[k] = new ColorF(r, g, b, 1f);
                    ratios[k] = ContrastOf(ink, colors[k], opacity);
                }
                int[] order = new int[ratios.Length];
                for (int k = 0; k < order.Length; k++) order[k] = k;
                Array.Sort(ratios, order);
                int at = Math.Clamp((int)MathF.Floor(percentile * (ratios.Length - 1)), 0, ratios.Length - 1);
                ratio = ratios[at];
                worst = colors[order[at]];
                return true;
            }

            /// <summary>The share of the way from the box's median grey to the ink's grey past which a pixel is the glyph's.</summary>
            public const float InkShare = 0.25f;

            /// <summary>The least grey difference between the ink and the box's median for the glyphs to be told from their ground (below it the
            /// ground is ink-coloured and every pixel is judged).</summary>
            public const float InkSpan = 0.12f;

            /// <summary>The DIP either side of an inked column that still belong to the glyphs (their anti-aliased edge).</summary>
            public const float InkGuard = 1.5f;

            /// <summary>The ring (DIP) outside a box whose glyph fills it that stands for its ground.</summary>
            public const float RingDip = 3f;

            /// <summary>The fewest gap pixels that make a ground.</summary>
            public const int MinGapPixels = 24;

            /// <summary>The colour most of <paramref name="rect"/> (DIP) is painted in, and the share of its pixels that colour has. A pixel within
            /// <paramref name="tolerance"/> (0..1 per channel) of <paramref name="ignore"/> is not counted: the ink of a glyph that fills its box must
            /// not read as its own background. False when the rect holds no pixel.</summary>
            public bool TryBackground(RectF rect, ColorF? ignore, float tolerance, out ColorF background, out float share)
            {
                background = default;
                share = 0f;
                int x0 = Math.Clamp((int)MathF.Floor(rect.X * Scale), 0, Width), x1 = Math.Clamp((int)MathF.Ceiling(rect.Right * Scale), 0, Width);
                int y0 = Math.Clamp((int)MathF.Floor(rect.Y * Scale), 0, Height), y1 = Math.Clamp((int)MathF.Ceiling(rect.Bottom * Scale), 0, Height);
                if (x1 <= x0 || y1 <= y0) return false;
                var counts = new Dictionary<int, int>();
                int total = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = (y * Width + x) * 4;
                        byte a = _bgra[i + 3];
                        if (a == 0) continue;
                        // Un-premultiply: the offscreen surface is opaque at rest, so a is 255 and this is the identity there.
                        int r = Math.Min(255, _bgra[i + 2] * 255 / a), g = Math.Min(255, _bgra[i + 1] * 255 / a), b = Math.Min(255, _bgra[i] * 255 / a);
                        if (ignore is { } ink
                            && MathF.Abs(r / 255f - ink.R) <= tolerance && MathF.Abs(g / 255f - ink.G) <= tolerance && MathF.Abs(b / 255f - ink.B) <= tolerance)
                            continue;
                        int key = (r << 16) | (g << 8) | b;
                        counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                        total++;
                    }
                if (total == 0) return false;
                int best = 0, bestCount = 0;
                foreach (var (key, count) in counts)
                    if (count > bestCount) { best = key; bestCount = count; }
                background = new ColorF(((best >> 16) & 255) / 255f, ((best >> 8) & 255) / 255f, (best & 255) / 255f, 1f);
                share = bestCount / (float)total;
                return true;
            }
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

            /// <summary>The contrast body text needs (WCAG AA, 4.5 : 1).</summary>
            public float ContrastBody { get; init; } = 4.5f;

            /// <summary>The contrast large text, bold large text and icon glyphs need (3 : 1).</summary>
            public float ContrastLarge { get; init; } = 3f;

            /// <summary>Over a busy ground (a photograph, a scrim, a gradient), the share of a run's glyph pixels allowed under the ratio it needs:
            /// the ratio is read at this percentile of the ordered per-pixel ratios. Default 0.1 (judged against its p90 worst ground).</summary>
            public float MediaPercentile { get; init; } = 0.1f;

            /// <summary>A run whose own and ancestors' opacity multiply below this is faded on purpose (the stage's distance-faded lyric lines, chrome
            /// fading out) and is incidental text in the WCAG sense: the contrast rule skips it. Default 0.5.</summary>
            public float IncidentalOpacity { get; init; } = 0.5f;

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
                    Rules.Contrast => ContrastRule(snapshot, o, found),
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

        // ── contrast ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The WCAG contrast ratio of <paramref name="ink"/> on <paramref name="ground"/>: the ink's alpha (times <paramref name="opacity"/>)
        /// is composited over the ground first, the ground is opaque. 1 to 21.</summary>
        public static double ContrastOf(ColorF ink, ColorF ground, float opacity = 1f)
        {
            double a = Math.Clamp(ink.A * opacity, 0f, 1f);
            double r = a * ink.R + (1 - a) * ground.R, g = a * ink.G + (1 - a) * ground.G, b = a * ink.B + (1 - a) * ground.B;
            double lt = Luminance(r, g, b), lb = Luminance(ground.R, ground.G, ground.B);
            return (Math.Max(lt, lb) + 0.05) / (Math.Min(lt, lb) + 0.05);
        }

        static double Luminance(double r, double g, double b)
        {
            static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
        }

        /// <summary>The contrast a run needs: <see cref="Options.ContrastLarge"/> for an icon glyph, for 24 DIP and up and for bold 18.66 DIP and up
        /// (the WCAG large-text sizes), <see cref="Options.ContrastBody"/> otherwise.</summary>
        public static float ContrastNeeded(TextRun t, Options o)
            => t.Glyph || t.Size >= 24f || (t.Size >= 18.66f && t.Weight >= 700) ? o.ContrastLarge : o.ContrastBody;

        /// <summary>Each run's declared colour (and its opacity) against what is painted under it. Over a SOLID ground (one colour holds half of the
        /// box) the run is judged against that colour, a warning. Over a BUSY ground (a photograph, a veil, a gradient: no colour holds half of it)
        /// the run is judged against the rendered pixels around its glyphs at their worst (<see cref="Pixels.TryWorstContrast"/> at
        /// <see cref="Options.MediaPercentile"/>): an error over an image (<see cref="OverImage"/>; text over a photograph that does not read is a bug,
        /// whatever the declared fill says), a warning over a gradient or a wash. Runs the
        /// check cannot judge are skipped, not passed: a disabled subtree, a node mid-animation, a fully transparent ink, a run faded below <see cref="Options.IncidentalOpacity"/>, a span run and a box mostly
        /// off the frame. No frame, no check.</summary>
        static int ContrastRule(Snapshot s, Options o, List<Finding> into)
        {
            if (s.Backdrop is not { } px) return 0;
            int looked = 0;
            foreach (Node n in s.Nodes)
            {
                if (n.Text is not { } t || t.Spans || t.String.Length == 0 || n.Inert || n.Animating) continue;
                if (t.Color.A * n.Opacity < 0.02f) continue;
                if (n.Opacity < o.IncidentalOpacity) continue;   // faded on purpose (a lyric's distance fade): incidental, not read
                if (IsGlowCopy(s, n, t)) continue;                // a bloom or shadow drawn under the same words: the readable copy is judged
                float w = t.NaturalW > 0f ? MathF.Min(n.Rect.W, t.NaturalW + 2f) : n.Rect.W;
                var box = new RectF(n.Rect.X, n.Rect.Y, w, n.Rect.H);
                float x0 = MathF.Max(0f, box.X), x1 = MathF.Min(s.Width, box.Right), y0 = MathF.Max(0f, box.Y), y1 = MathF.Min(s.Height, box.Bottom);
                if (x1 - x0 < MathF.Min(2f, w) || y1 - y0 < MathF.Min(2f, n.Rect.H) || (x1 - x0) * (y1 - y0) < 0.6f * w * n.Rect.H) continue;
                var seen = new RectF(x0, y0, x1 - x0, y1 - y0);
                if (!px.TryBackground(seen, null, 0f, out ColorF ground, out float share)) continue;
                if (ContrastOf(t.Color, ground, n.Opacity) < 1.15)
                {
                    // The ink fills its own box (a heart, a solid glyph): look at what is left once its colour is taken out.
                    if (!px.TryBackground(seen, t.Color, 0.03f, out ground, out share)) continue;
                }
                float need = ContrastNeeded(t, o);
                string kind = t.Glyph ? "icon" : "text";
                if (share < 0.45f)
                {
                    if (!px.TryWorstContrast(seen, t.Color, n.Opacity, o.MediaPercentile, out double worstRatio, out ColorF worst, out int gaps))
                        continue;
                    looked++;
                    if (worstRatio + 0.005 >= need) continue;
                    bool photo = OverImage(s, n);
                    into.Add(new Finding(Rules.Contrast, photo ? Severity.Error : Severity.Warn,
                        $"{kind} {Q(t.String)} over {(photo ? "an image" : "a gradient")} reads {F(worstRatio)} : 1 on its p{F((1f - o.MediaPercentile) * 100f)} ground #{Hex(worst)}, under the {F(need)} : 1 it needs", [n.Ref])
                    {
                        Measured = [M("ratio", worstRatio), M("size", t.Size), M("weight", t.Weight), M("gapPixels", gaps)],
                        Expected = [M("minRatio", need)],
                    });
                    continue;
                }
                looked++;
                double ratio = ContrastOf(t.Color, ground, n.Opacity);
                if (ratio + 0.005 >= need) continue;
                into.Add(new Finding(Rules.Contrast, Severity.Warn,
                    $"{kind} {Q(t.String)} reads {F(ratio)} : 1 on #{Hex(ground)}, under the {F(need)} : 1 it needs", [n.Ref])
                {
                    Measured = [M("ratio", ratio), M("size", t.Size), M("weight", t.Weight)],
                    Expected = [M("minRatio", need)],
                });
            }
            return looked;
        }

        /// <summary><paramref name="n"/> is a decorative copy of another run: the same words at (nearly) the same box, in a fainter ink (a karaoke
        /// line's accent bloom under its text). The copy with the strongest ink is the one that is read.</summary>
        static bool IsGlowCopy(Snapshot s, Node n, TextRun t)
        {
            float alpha = t.Color.A * n.Opacity;
            foreach (Node m in s.Nodes)
            {
                if (m.Index == n.Index || m.Text is not { } u || !string.Equals(u.String, t.String, StringComparison.Ordinal)) continue;
                if (MathF.Abs(m.Rect.X - n.Rect.X) > GlowSlack || MathF.Abs(m.Rect.Y - n.Rect.Y) > GlowSlack
                    || MathF.Abs(m.Rect.W - n.Rect.W) > GlowSlack || MathF.Abs(m.Rect.H - n.Rect.H) > GlowSlack) continue;
                float other = u.Color.A * m.Opacity;
                if (other > alpha || (other == alpha && m.Index > n.Index)) return true;
            }
            return false;
        }

        /// <summary>How far (DIP) a glow copy's box may sit from its text's.</summary>
        const float GlowSlack = 2f;

        /// <summary>An image paints under <paramref name="n"/>: a visible image node earlier in paint order (the snapshot is depth-first, so earlier is
        /// under) covers most of its box. Text over a photograph that does not read is an error; over a gradient or a wash, a warning.</summary>
        static bool OverImage(Snapshot s, Node n)
        {
            float area = MathF.Max(1f, n.Rect.W * n.Rect.H);
            for (int i = 0; i < n.Index; i++)
            {
                Node m = s.Nodes[i];
                if (m.Kind != "Image" || m.Opacity < 0.5f) continue;
                float w = MathF.Min(m.Rect.Right, n.Rect.Right) - MathF.Max(m.Rect.X, n.Rect.X);
                float h = MathF.Min(m.Rect.Bottom, n.Rect.Bottom) - MathF.Max(m.Rect.Y, n.Rect.Y);
                if (w > 0f && h > 0f && w * h >= 0.5f * area) return true;
            }
            return false;
        }

        static string Hex(ColorF c)
            => ((int)MathF.Round(c.R * 255f)).ToString("x2", CultureInfo.InvariantCulture) + ((int)MathF.Round(c.G * 255f)).ToString("x2", CultureInfo.InvariantCulture)
               + ((int)MathF.Round(c.B * 255f)).ToString("x2", CultureInfo.InvariantCulture);

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
