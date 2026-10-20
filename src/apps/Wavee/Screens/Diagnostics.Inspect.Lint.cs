// ── Screens/Diagnostics.Inspect.Lint.cs — the `--inspect` lint: a snapshot of nodes in, findings with outcomes out ───────────────
// the model (snapshot, finding, report), the rule catalog, the run (prepare → rules → intents → baseline → fingerprints) and today's nine
// rules (edge.left/right, text.clip, overlap.sibling, text.trim, type.floor/scale, target.size, duplicate.action, space.stack, contrast.text)
//
// Role: CORE (pure: no engine, no host, no thread; the scene is read into a `Snapshot` by Diagnostics.Inspect.Lint.Scene.cs, or a saved one
// is read back by Diagnostics.Inspect.Lint.Store.cs). The report-only rules (align.inset, align.baseline, space.scale, text.placeholder,
// key.hygiene) are Diagnostics.Inspect.Lint.Layout.cs and .Lint.Content.cs. User reference: docs/guide/agent-lint.md.
// Spec: scratch/agentic-e2e/PLAN.md 4.5 and 5.1-5.5; the algorithms and false-positive classes: scratch/agentic-e2e/design/design-qa.md.
//
// THE SNAPSHOT is the dump's node list without the JSON: depth-first, `Nodes[i].Index == i`, a node's `Parent` an earlier index (-1 for a
// root), every rect the PRESENTED absolute rect in window DIP. Only what paints is in it (hidden, parked and empty-box nodes are not).
// `Prepare` derives what the rules judge by: the VISIBLE rect and on-screen fraction (Diagnostics.Inspect.Visible.cs), ink, the scroll
// REGION and the BACKDROP (the plate a node sits on), the key PATTERN (`zune-history:*`), the repeated key FAMILY with its CELL PATH, and,
// from a rendered frame, the ground under each text run. Every rule then reads only what is in scope (`scope=screen`: at least half visible).
//
// THE FINDING (PLAN 4.5): `rule` (today's name for the nine, the id for the new ones), `id` (family.name), severity, OUTCOME (fail: the rule's
// verdict; review: it cannot decide, look at the render; suppressed: an intent or a baseline entry matched), LEVEL (gate: today's nine; todo:
// report-only, never counted in `summary`), `fp` (rule|theme|route|pattern|cause, size-independent, so one baseline line covers every size),
// `group` {count, pattern} (members collapse into one finding), a message, measured/expected, and a one-line `fix` that never says to shrink
// Zune display type, to touch motion or to remove a feature (and on Classic only says "report; do not fix unless asked").
//
// CONSERVATIVE BY DESIGN. A scroller may cut its content where scrolling reveals it (the fold); an edge fade or a guide `Bleed` excuses a hard
// scroll edge; ZStack children and sticky headers may overlap; glyph (icon) text is not words; a designed ellipsis (MaxLines + ellipsis in a
// repeated family, or a guide `TrimOk`) is counted, not reported; a text rule under the headless text model is a `review`, never a verdict.

using System.Globalization;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>The lint's pure half: the snapshot model, the options, the run, today's rules. See the file header.</summary>
    public static partial class InspectLint
    {
        // ── the rules ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The rule names: today's nine (what `rules=` took and a finding's <c>rule</c> says, kept as aliases) and the ids (family.name).</summary>
        public static class Rules
        {
            public const string Gutter = "gutter", RightEdge = "right-edge", Clip = "clip", Trim = "trim", Type = "type",
                Target = "target", Duplicate = "duplicate", Rhythm = "rhythm", Contrast = "contrast";

            public const string EdgeLeft = "edge.left", EdgeRight = "edge.right", TextClip = "text.clip", OverlapSibling = "overlap.sibling",
                TextTrim = "text.trim", TypeFloor = "type.floor", TypeScale = "type.scale", TargetSize = "target.size",
                DuplicateAction = "duplicate.action", SpaceStack = "space.stack", ContrastText = "contrast.text";

            public const string AlignInset = "align.inset", AlignBaseline = "align.baseline", SpaceScale = "space.scale",
                TextPlaceholder = "text.placeholder", KeyHygiene = "key.hygiene";
        }

        /// <summary>Today's nine rule names, in the order the report lists them (each is an alias of one or two ids).</summary>
        public static readonly string[] RuleNames =
            [Rules.Gutter, Rules.RightEdge, Rules.Clip, Rules.Trim, Rules.Type, Rules.Target, Rules.Duplicate, Rules.Rhythm, Rules.Contrast];

        /// <summary>gate: a rule whose fail findings count in the report's summary (today's nine); todo: report-only until it is promoted on the
        /// replay set (PLAN 5.6), so a new rule never breaks anyone's "lint min=error clean".</summary>
        public enum Level : byte { Gate, Todo }

        /// <summary>fail: the rule's verdict; review: the rule cannot decide (look at the render); suppressed: an intent or a baseline entry matched.</summary>
        public enum Outcome : byte { Fail, Review, Suppressed }

        /// <summary>One rule of the catalog: its id, the alias today's callers know it by ("" for a new rule), its level and what it checks.</summary>
        public readonly record struct RuleSpec(string Id, string Alias, Level Level, string What);

        /// <summary>Every rule, in report order.</summary>
        public static readonly RuleSpec[] Catalog =
        [
            new(Rules.EdgeLeft, Rules.Gutter, Level.Gate, "left edges on the theme guide's declared x"),
            new(Rules.EdgeRight, Rules.RightEdge, Level.Gate, "right edges on the theme guide's declared x"),
            new(Rules.TextClip, Rules.Clip, Level.Gate, "text ink cut by a clipping ancestor or a hard scroll edge"),
            new(Rules.OverlapSibling, Rules.Clip, Level.Gate, "sibling boxes with text or a target that overlap"),
            new(Rules.TextTrim, Rules.Trim, Level.Gate, "trimmed text, by class: near-miss, never, overflow, designed, single"),
            new(Rules.TypeFloor, Rules.Type, Level.Gate, "text under the type floor"),
            new(Rules.TypeScale, Rules.Type, Level.Gate, "the type styles in use (a report)"),
            new(Rules.TargetSize, Rules.Target, Level.Gate, "click targets under the tier's floor, by their effective area"),
            new(Rules.DuplicateAction, Rules.Duplicate, Level.Gate, "the same verb on the same subject in two homes"),
            new(Rules.SpaceStack, Rules.Rhythm, Level.Gate, "stacked text blocks that overlap (touching ones are a report)"),
            new(Rules.ContrastText, Rules.Contrast, Level.Gate, "text contrast against its rendered ground, by ink and ground"),
            new(Rules.AlignInset, "", Level.Todo, "a section's ink lead 4-16 DIP off its region's column line"),
            new(Rules.AlignBaseline, "", Level.Todo, "a repeated family's text drifting against its row top"),
            new(Rules.SpaceScale, "", Level.Todo, "declared gap, padding and margin off the theme's spacing scale"),
            new(Rules.TextPlaceholder, "", Level.Todo, "a missing loc key, a missing argument, NaN or null on screen"),
            new(Rules.KeyHygiene, "", Level.Todo, "keys that break selectors: duplicates, control characters, run ids, tier words"),
        ];

        /// <summary>Every rule id, in report order.</summary>
        public static readonly string[] RuleIds = Catalog.Select(static r => r.Id).ToArray();

        /// <summary>The catalog entry of <paramref name="id"/>.</summary>
        public static bool TrySpec(string id, out RuleSpec spec)
        {
            foreach (RuleSpec r in Catalog)
                if (string.Equals(r.Id, id, StringComparison.Ordinal)) { spec = r; return true; }
            spec = default;
            return false;
        }

        /// <summary>The ids a `rules=` word names: an id (<c>text.trim</c>), an alias (<c>clip</c> = text.clip + overlap.sibling), a family
        /// (<c>align</c> or <c>align.*</c>), or <c>all</c>. Case-insensitive. Empty when the word names nothing.</summary>
        public static List<string> IdsOf(string word)
        {
            var ids = new List<string>();
            string w = word.Trim().ToLowerInvariant();
            if (w.Length == 0) return ids;
            if (w == "all") { ids.AddRange(RuleIds); return ids; }
            string family = w.EndsWith(".*", StringComparison.Ordinal) ? w[..^2] : w;
            foreach (RuleSpec r in Catalog)
            {
                if (string.Equals(r.Id, w, StringComparison.Ordinal) || string.Equals(r.Alias, w, StringComparison.Ordinal)
                    || r.Id.StartsWith(family + ".", StringComparison.Ordinal))
                    ids.Add(r.Id);
            }
            return ids;
        }

        /// <summary>The name a finding of <paramref name="id"/> reports under: its alias for today's nine, else the id.</summary>
        public static string ReportName(string id) => TrySpec(id, out RuleSpec spec) && spec.Alias.Length > 0 ? spec.Alias : id;

        /// <summary>How tall the ink of a line of text is, in ems: ascender to descender. A clip that cuts below this much of a line cuts descenders.</summary>
        public const float InkEm = 1.2f;

        /// <summary>The slack (DIP) before an edge, a clip or an overlap counts: sub-pixel rounding is not a finding.</summary>
        public const float Slack = 0.5f;

        /// <summary>The share of a text box one colour must hold to be its ground; under it the ground is busy (art, a gradient).</summary>
        public const float BusyShare = 0.45f;

        public enum Severity : byte { Info, Warn, Error }

        public static string Name(Severity s) => s switch { Severity.Error => "error", Severity.Warn => "warn", _ => "info" };

        public static string Name(Outcome o) => o switch { Outcome.Review => "review", Outcome.Suppressed => "suppressed", _ => "fail" };

        public static string Name(Level l) => l == Level.Todo ? "todo" : "gate";

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

        public static bool TryParseOutcome(string word, out Outcome outcome)
        {
            switch (word.Trim().ToLowerInvariant())
            {
                case "fail": outcome = Outcome.Fail; return true;
                case "review": outcome = Outcome.Review; return true;
                case "suppressed": outcome = Outcome.Suppressed; return true;
                default: outcome = Outcome.Fail; return false;
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

            /// <summary>The glyph's icon name (<c>Icons.TryGetName</c>: <c>Play</c>), "" for words or an unnamed glyph.</summary>
            public string GlyphName { get; init; } = "";

            /// <summary>A span run (its words live in the spans, <see cref="String"/> is empty).</summary>
            public bool Spans { get; init; }

            /// <summary>The colour the run is declared in (straight alpha). Alpha 0 (the default) is "not known": the contrast rule skips the run.</summary>
            public ColorF Color { get; init; }

            /// <summary>The text alignment inside its box (<c>Start</c>, <c>Center</c>, <c>End</c>, <c>Justify</c>).</summary>
            public string Align { get; init; } = "Start";

            /// <summary>The colour most of the run's box was rendered in (alpha 0: no frame was read), and the share of the box it covers.</summary>
            public ColorF Ground { get; init; }
            public float GroundShare { get; init; }
        }

        /// <summary>What a node DECLARES for layout (<c>SceneStore.Layout</c>), not what the solve measured: the space rules read these. NaN = auto.</summary>
        public sealed record Declared(float Gap, Edges4 Padding, Edges4 Margin)
        {
            public float W { get; init; } = float.NaN;
            public float H { get; init; } = float.NaN;
            public float MinW { get; init; } = float.NaN;
            public float MinH { get; init; } = float.NaN;
            public float MaxW { get; init; } = float.NaN;
            public float MaxH { get; init; } = float.NaN;
            public float Grow { get; init; }
            public float Shrink { get; init; }
            public float Basis { get; init; } = float.NaN;

            /// <summary>The container's main-axis distribution and cross alignment, and the node's own (FlexJustify / FlexAlign names).</summary>
            public string Justify { get; init; } = "Start";
            public string AlignItems { get; init; } = "Stretch";
            public string AlignSelf { get; init; } = "Auto";
            public string JustifySelf { get; init; } = "Auto";
            public bool Wrap { get; init; }

            public static Declared None { get; } = new(0f, default, default);
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

            /// <summary>A scroller's offset and its largest offset on its axis (what scrolling can still reveal).</summary>
            public float ScrollOffset { get; init; }
            public float ScrollMax { get; init; }

            /// <summary>The edges it feathers (an edge fade, or a scroller's auto fade): <see cref="EdgeMask"/> bits.</summary>
            public byte FadeEdges { get; init; }

            /// <summary>An authored clip on its children in window DIP (a sticky or collapse cut, a reveal box); <see cref="RectF.Infinite"/> when none.</summary>
            public RectF ClipRect { get; init; } = RectF.Infinite;

            /// <summary>Its children overlay each other on purpose.</summary>
            public bool ZStack { get; init; }

            /// <summary>It lays its children out in a column (vertical stack).</summary>
            public bool Column { get; init; } = true;

            /// <summary>What it declares for layout.</summary>
            public Declared Layout { get; init; } = Declared.None;

            /// <summary>A click target (a click handler or an action role), hit-testable.</summary>
            public bool Interactive { get; init; }
            public bool Disabled { get; init; }
            public string Role { get; init; } = "";

            /// <summary>Which click it runs: equal ids are the same handler (0: none of its own). The target rule's "same click".</summary>
            public int ClickId { get; init; }

            /// <summary>The first text under an interactive node (an icon-only button: its glyph).</summary>
            public string Label { get; init; } = "";

            /// <summary>The label glyph's icon name (<c>Play</c>), "" when the label is words or an unnamed glyph.</summary>
            public string LabelGlyph { get; init; } = "";

            /// <summary>It carries live animation tracks (mid-transition): skipped by the geometry rules.</summary>
            public bool Animating { get; init; }

            /// <summary>The node's opacity times every ancestor's: how much of its colour reaches the screen. Default 1.</summary>
            public float Opacity { get; init; } = 1f;

            /// <summary>The node or an ancestor is disabled: its ink is dimmed on purpose and exempt from the contrast rule.</summary>
            public bool Inert { get; init; }

            /// <summary>A sticky header (pinned over the rows under it on purpose).</summary>
            public bool Sticky { get; init; }

            /// <summary>Its paint: a fill, a border, an image, a gradient (what makes a box ink, and a busy ground).</summary>
            public ColorF Fill { get; init; }
            public float BorderWidth { get; init; }
            public ColorF BorderColor { get; init; }
            public bool Image { get; init; }
            public bool Gradient { get; init; }

            public TextRun? Text { get; init; }

            /// <summary>The words (not glyph runs) in this node's subtree, itself included.</summary>
            public int TextCount { get; init; }

            /// <summary>The images in this node's subtree, itself included (artwork marks a content card).</summary>
            public int ImageCount { get; init; }

            // ── derived by Prepare ──

            /// <summary>The part of <see cref="Rect"/> a person can see, and the share of it that is (<see cref="InspectVisible"/>).</summary>
            public RectF Visible { get; init; }
            public float OnScreen { get; init; } = 1f;

            /// <summary>The share of its rect scrolling reaches (the window and the scroll viewports do not cut).</summary>
            public float InContent { get; init; } = 1f;

            /// <summary>In the run's scope (<c>scope=screen</c>: at least half visible).</summary>
            public bool InScope { get; init; } = true;

            /// <summary>It paints: text, an image, a gradient, a visible fill or border.</summary>
            public bool Ink { get; init; }

            /// <summary>Ink nodes in its subtree, itself included.</summary>
            public int InkCount { get; init; }

            /// <summary>The nearest vertical scroller above it (-1: none): the scroll region.</summary>
            public int Region { get; init; } = -1;

            /// <summary>It sits inside a horizontal scroller (a rail: content-driven x).</summary>
            public bool InRail { get; init; }

            /// <summary>The plate it sits on: the last earlier ink node whose visible rect covers it (-1: the page).</summary>
            public int Backdrop { get; init; } = -1;

            /// <summary>The nearest keyed node's pattern (<c>zune-history:*</c>, <c>c.title</c>; <c>#root</c> when none): its family as a finding names it.</summary>
            public string Pattern { get; init; } = "";

            /// <summary>The nearest key family repeated at least 3 times in scope ("" when none), its instance root and the child-ordinal path from it.</summary>
            public string Family { get; init; } = "";
            public int Instance { get; init; } = -1;
            public string CellPath { get; init; } = "";

            /// <summary>What a finding calls the node: its key, else its path.</summary>
            public string Ref => Key ?? Path;
        }

        /// <summary>A named value of the theme's design scale (<c>Spacing.M</c> = 12): what <c>space.scale</c> accepts and names.</summary>
        public readonly record struct ScaleEntry(string Name, float Value);

        /// <summary>What the rules read.</summary>
        public sealed record Snapshot(float Width, float Height, WidthTier Tier, IReadOnlyList<Node> Nodes)
        {
            /// <summary>The active theme's edge expectations (the edge rules check these).</summary>
            public IReadOnlyList<LintEdge> Edges { get; init; } = [];

            /// <summary>The active theme's intents by key prefix (<see cref="LintPolicy"/>).</summary>
            public IReadOnlyList<LintPolicy> Policies { get; init; } = [];

            /// <summary>The theme's design scale for <c>space.scale</c>.</summary>
            public IReadOnlyList<ScaleEntry> Scale { get; init; } = [];

            public string Theme { get; init; } = "";

            /// <summary>The id of the guide the edges and policies came from ("": the theme registered none).</summary>
            public string GuideId { get; init; } = "";

            /// <summary>The route as `status` names it (<c>album spotify:album:al2</c>).</summary>
            public string Route { get; init; } = "";

            /// <summary>Where trims come from: <c>dwrite</c> (real glyphs) or <c>headless-model</c> (the 0.55 x size model: the text rules only review).</summary>
            public string TrimSource { get; init; } = "dwrite";

            /// <summary>The devices the mount renders on (`status` names them).</summary>
            public string Devices { get; init; } = "";

            /// <summary>The saved snapshot it was read from ("" when captured live).</summary>
            public string Source { get; init; } = "";

            /// <summary>The rendered frame the grounds under the text are read from; null when nothing was rendered.</summary>
            public Pixels? Backdrop { get; init; }

            /// <summary>The scope <see cref="Prepare"/> derived the node fields for (null: not prepared).</summary>
            public InspectVisible.Scope? PreparedFor { get; init; }

            /// <summary>The route's name (<c>album</c>): the fingerprint's route part.</summary>
            public string RouteName
            {
                get
                {
                    int cut = Route.AsSpan().IndexOfAny(' ', ':');
                    return cut > 0 ? Route[..cut] : Route;
                }
            }

            /// <summary>The page's own entity (<c>spotify:album:al2</c>), "" on a page about none.</summary>
            public string PageEntity => EntityIn(Route);

            public bool RealText => string.Equals(TrimSource, "dwrite", StringComparison.Ordinal);
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

        /// <summary>One accepted finding of a baseline file (a JSON line): matched by fingerprint, never by message. A reason is mandatory.</summary>
        public sealed record BaselineEntry(string Fp, string Rule, string Reason, string By, string Date);

        public sealed record Options
        {
            /// <summary>The rule ids that run (default: all; `rules=` takes ids, aliases and families).</summary>
            public IReadOnlyCollection<string> Rules { get; init; } = RuleIds;

            /// <summary>What the rules judge: on screen (default), what scrolling reaches, or everything captured.</summary>
            public InspectVisible.Scope Scope { get; init; } = InspectVisible.Scope.Screen;

            /// <summary>Text under this size (DIP) is an error. Default 11.</summary>
            public float TypeFloor { get; init; } = 11f;

            /// <summary>The smallest hit target (DIP) outside Compact. Default 32.</summary>
            public float TargetFloor { get; init; } = 32f;

            /// <summary>The smallest hit target (DIP) at Compact. Default 40.</summary>
            public float TargetFloorCompact { get; init; } = 40f;

            /// <summary>Stacked text blocks closer than this (DIP) touch (a report; an overlap is a warning). Default 2.</summary>
            public float GapMin { get; init; } = 2f;

            /// <summary>Keys or key prefixes (of the node or any ancestor) whose text may ellipsise: a per-call <see cref="LintIntent.TrimOk"/>.</summary>
            public IReadOnlyList<string> AllowTrim { get; init; } = [];

            /// <summary>Labels the duplicate rule lets repeat.</summary>
            public IReadOnlyList<string> AllowDuplicate { get; init; } = [];

            /// <summary>Key prefixes the duplicate rule skips (default: the title bar and the play bar).</summary>
            public IReadOnlyList<string> IgnoreUnder { get; init; } = DefaultIgnoreUnder;

            /// <summary>The contrast body text needs (WCAG AA, 4.5 : 1).</summary>
            public float ContrastBody { get; init; } = 4.5f;

            /// <summary>The contrast large text, bold large text and icon glyphs need (3 : 1).</summary>
            public float ContrastLarge { get; init; } = 3f;

            /// <summary>The most findings one rule lists.</summary>
            public int MaxPerRule { get; init; } = 100;

            /// <summary>Findings below this severity are left out of the report.</summary>
            public Severity Min { get; init; } = Severity.Info;

            /// <summary>The outcomes the report lists (default fail and review; every outcome is counted either way).</summary>
            public IReadOnlyCollection<Outcome> Outcomes { get; init; } = [Outcome.Fail, Outcome.Review];

            /// <summary>Accepted findings (`baseline=<file>`): a finding whose fingerprint is here is suppressed, with the entry's reason.</summary>
            public IReadOnlyList<BaselineEntry> Baseline { get; init; } = [];

            /// <summary>The baseline file read ("" none).</summary>
            public string BaselineFile { get; init; } = "";

            /// <summary>Does a rule <paramref name="rule"/> names (an id or an alias) run?</summary>
            public bool Enabled(string rule)
            {
                foreach (string id in IdsOf(rule))
                    foreach (string r in Rules)
                        if (string.Equals(r, id, StringComparison.Ordinal)) return true;
                return false;
            }

            bool EnabledId(string id)
            {
                foreach (string r in Rules)
                    if (string.Equals(r, id, StringComparison.Ordinal)) return true;
                return false;
            }

            internal bool Runs(string id) => EnabledId(id);

            internal bool Lists(Outcome outcome)
            {
                foreach (Outcome o in Outcomes)
                    if (o == outcome) return true;
                return false;
            }
        }

        // ── the findings ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A named number: one measured or expected value of a finding.</summary>
        public readonly record struct Metric(string Name, double Value);

        /// <summary>One finding. A rule fills <see cref="Rule"/> with its id, the severity, the message, the member refs (<see cref="Keys"/>, at
        /// most a few: <see cref="Count"/> says how many there are), the <see cref="Pattern"/> and <see cref="Cause"/> the fingerprint is made of,
        /// and its outcome; the run adds the id/alias split, the level, the fingerprint, the fix and any suppression.</summary>
        public sealed record Finding(string Rule, Severity Severity, string Message, IReadOnlyList<string> Keys)
        {
            public IReadOnlyList<Metric> Measured { get; init; } = [];
            public IReadOnlyList<Metric> Expected { get; init; } = [];

            /// <summary>The rule id (family.name); <see cref="Rule"/> is what the report calls it (today's alias for the nine).</summary>
            public string Id { get; init; } = "";
            public Outcome Outcome { get; init; } = Outcome.Fail;
            public Level Level { get; init; } = Level.Gate;

            /// <summary>The family the finding is about (<c>zune-history:*</c>): its members collapse into this one finding.</summary>
            public string Pattern { get; init; } = "";

            /// <summary>The cause bucket (<c>lead:+8</c>, <c>near-miss</c>, <c>#a8a8a8ff on #161616</c>): size-independent.</summary>
            public string Cause { get; init; } = "";

            /// <summary>How many members the finding collapses.</summary>
            public int Count { get; init; } = 1;

            /// <summary>The members' visible rects (as many as <see cref="Keys"/>): where to look, and what a marked render boxes.</summary>
            public IReadOnlyList<RectF> Rects { get; init; } = [];

            /// <summary>The fingerprint: rule|theme|route|pattern|cause.</summary>
            public string Fp { get; init; } = "";

            /// <summary>One line on what to do (never: shrink Zune display type, touch motion, remove a feature; Classic: report only).</summary>
            public string Fix { get; init; } = "";

            /// <summary>Why it is suppressed (the intent's or baseline entry's reason) or under review.</summary>
            public string Reason { get; init; } = "";

            /// <summary>What suppressed it: <c>intent</c> or <c>baseline</c> ("" when not suppressed).</summary>
            public string SuppressedBy { get; init; } = "";
        }

        /// <summary>The listed findings, how many things each rule looked at (by id), the rules whose findings were cut at the cap, and the counts
        /// over every finding (listed or not).</summary>
        public sealed record Report(IReadOnlyList<Finding> Findings, IReadOnlyDictionary<string, int> Checked, IReadOnlyList<string> Truncated)
        {
            /// <summary>The snapshot as the rules saw it (prepared for the scope).</summary>
            public Snapshot? Prepared { get; init; }

            /// <summary>Every finding at or above `min`, whatever its outcome (the listed ones are a subset).</summary>
            public IReadOnlyList<Finding> All { get; init; } = [];

            /// <summary>The guide's edges that matched an in-scope node.</summary>
            public int EdgesMatched { get; init; }

            /// <summary>Nodes in scope.</summary>
            public int InScope { get; init; }

            /// <summary>Baseline entries that matched a finding.</summary>
            public int BaselineMatched { get; init; }

            /// <summary>Listed fail findings of a gate rule at <paramref name="severity"/>: the summary an agent gates on.</summary>
            public int Count(Severity severity)
            {
                int n = 0;
                foreach (Finding f in Findings)
                    if (f.Severity == severity && f.Outcome == Outcome.Fail && f.Level == Level.Gate) n++;
                return n;
            }

            /// <summary>Listed fail findings of rule <paramref name="rule"/> (an id or what the report calls it) at <paramref name="severity"/>.</summary>
            public int Count(string rule, Severity severity) => Count(rule, severity, Outcome.Fail);

            public int Count(string rule, Severity severity, Outcome outcome)
            {
                int n = 0;
                foreach (Finding f in Findings)
                    if (f.Severity == severity && f.Outcome == outcome && (string.Equals(f.Rule, rule, StringComparison.Ordinal) || string.Equals(f.Id, rule, StringComparison.Ordinal))) n++;
                return n;
            }

            /// <summary>Findings at or above `min` with <paramref name="outcome"/>, listed or not.</summary>
            public int CountAll(Outcome outcome)
            {
                int n = 0;
                foreach (Finding f in All)
                    if (f.Outcome == outcome) n++;
                return n;
            }

            /// <summary>Fail findings at or above `min` of <paramref name="level"/>, listed or not.</summary>
            public int CountAll(Level level)
            {
                int n = 0;
                foreach (Finding f in All)
                    if (f.Outcome == Outcome.Fail && f.Level == level) n++;
                return n;
            }

            /// <summary>Findings at or above `min` suppressed by <paramref name="by"/> (<c>intent</c> or <c>baseline</c>).</summary>
            public int CountSuppressed(string by)
            {
                int n = 0;
                foreach (Finding f in All)
                    if (f.Outcome == Outcome.Suppressed && string.Equals(f.SuppressedBy, by, StringComparison.Ordinal)) n++;
                return n;
            }
        }

        // ── running ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Run the enabled rules over <paramref name="snapshot"/>: prepare it for the scope, run each rule, apply the guide's generic
        /// intents and the baseline, fingerprint, attach the fix, then filter by `min`, the listed outcomes and the per-rule cap.</summary>
        public static Report Run(Snapshot snapshot, Options? options = null)
        {
            Options o = options ?? new Options();
            Snapshot s = Prepare(snapshot, o.Scope);
            var listed = new List<Finding>();
            var all = new List<Finding>();
            var checks = new Dictionary<string, int>(StringComparer.Ordinal);
            var truncated = new List<string>();
            var baseline = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);
            foreach (BaselineEntry e in o.Baseline) baseline.TryAdd(e.Fp, e);
            var baselineHit = new HashSet<string>(StringComparer.Ordinal);
            int edgesMatched = 0;
            foreach (RuleSpec spec in Catalog)
            {
                if (!o.Runs(spec.Id)) continue;
                var found = new List<Finding>();
                int looked = spec.Id switch
                {
                    Rules.EdgeLeft or Rules.EdgeRight => EdgeRule(s, spec.Id, found),
                    Rules.TextClip => ClipRule(s, found),
                    Rules.OverlapSibling => OverlapRule(s, found),
                    Rules.TextTrim => TrimRule(s, o, found),
                    Rules.TypeFloor => TypeFloorRule(s, o, found),
                    Rules.TypeScale => TypeScaleRule(s, found),
                    Rules.TargetSize => TargetRule(s, o, found),
                    Rules.DuplicateAction => DuplicateRule(s, o, found),
                    Rules.SpaceStack => RhythmRule(s, o, found),
                    Rules.ContrastText => ContrastRule(s, o, found),
                    Rules.AlignInset => AlignInsetRule(s, found),
                    Rules.AlignBaseline => AlignBaselineRule(s, found),
                    Rules.SpaceScale => SpaceScaleRule(s, found),
                    Rules.TextPlaceholder => PlaceholderRule(s, found),
                    Rules.KeyHygiene => KeyHygieneRule(s, found),
                    _ => 0,
                };
                checks[spec.Id] = looked;
                if (spec.Id is Rules.EdgeLeft or Rules.EdgeRight) edgesMatched += looked;

                var kept = new List<Finding>();
                foreach (Finding raw in found)
                {
                    Finding f = Finish(s, spec, raw);
                    if (f.Outcome != Outcome.Suppressed && AllowedByPolicy(s, f, out LintPolicy? policy))
                        f = f with { Outcome = Outcome.Suppressed, SuppressedBy = "intent", Reason = policy!.Reason };
                    if (f.Outcome != Outcome.Suppressed && baseline.TryGetValue(f.Fp, out BaselineEntry? entry))
                    {
                        f = f with { Outcome = Outcome.Suppressed, SuppressedBy = "baseline", Reason = entry.Reason + (entry.By.Length > 0 ? " (" + entry.By + (entry.Date.Length > 0 ? ", " + entry.Date : "") + ")" : "") };
                        baselineHit.Add(f.Fp);
                    }
                    if (f.Severity < o.Min) continue;
                    all.Add(f);
                    if (o.Lists(f.Outcome)) kept.Add(f);
                }
                // Errors first, then warnings, then the rest; a rule's own order within a severity, fails before reviews before suppressions.
                List<Finding> ordered = kept.OrderByDescending(static f => f.Severity).ThenBy(static f => f.Outcome).ToList();
                if (ordered.Count > o.MaxPerRule)
                {
                    ordered.RemoveRange(o.MaxPerRule, ordered.Count - o.MaxPerRule);
                    truncated.Add(ReportName(spec.Id));
                }
                listed.AddRange(ordered);
            }
            int inScope = 0;
            foreach (Node n in s.Nodes)
                if (n.InScope) inScope++;
            return new Report(listed, checks, truncated)
            {
                Prepared = s,
                All = all,
                EdgesMatched = edgesMatched,
                InScope = inScope,
                BaselineMatched = baselineHit.Count,
            };
        }

        /// <summary>The run's half of a finding: id and alias, level, fingerprint, fix.</summary>
        static Finding Finish(Snapshot s, RuleSpec spec, Finding f)
        {
            string pattern = f.Pattern.Length > 0 ? f.Pattern : PatternOfRefs(f.Keys);
            string cause = f.Cause;
            string fp = string.Join("|", spec.Id, s.Theme, s.RouteName, pattern, cause);
            string fix = string.Equals(s.Theme, "classic", StringComparison.OrdinalIgnoreCase)
                ? "report; do not fix unless asked (Classic)"
                : f.Fix.Length > 0 ? f.Fix : FixFor(spec.Id, cause);
            return f with
            {
                Id = spec.Id,
                Rule = spec.Alias.Length > 0 ? spec.Alias : spec.Id,
                Level = spec.Level,
                Pattern = pattern,
                Fp = fp,
                Fix = fix,
            };
        }

        /// <summary>A guide <see cref="LintIntent.Allow"/> for the finding's rule covering one of its members.</summary>
        static bool AllowedByPolicy(Snapshot s, Finding f, out LintPolicy? policy)
        {
            policy = null;
            foreach (LintPolicy p in s.Policies)
            {
                if (p.Intent != LintIntent.Allow || p.Prefix.Length == 0) continue;
                if (!string.Equals(p.Rule, f.Id, StringComparison.Ordinal) && !string.Equals(p.Rule, f.Rule, StringComparison.Ordinal)) continue;
                foreach (string key in f.Keys)
                    if (key.StartsWith(p.Prefix, StringComparison.Ordinal)) { policy = p; return true; }
            }
            return false;
        }

        /// <summary>The pattern of a finding a rule did not name one for: its first member's key family.</summary>
        static string PatternOfRefs(IReadOnlyList<string> refs)
        {
            if (refs.Count == 0) return "";
            string r = refs[0];
            int slash = r.IndexOf('/');
            return PatternOfKey(slash > 0 ? r[..slash] : r);
        }

        /// <summary>The one-line fix per rule and cause. It never says to shrink Zune display type, to add, remove or retime motion, or to remove or
        /// disable a feature (the owner's fix-hint rules; docs/guide/agent-lint.md).</summary>
        static string FixFor(string id, string cause) => id switch
        {
            Rules.EdgeLeft or Rules.EdgeRight => "take the x from the surface's tier record (the guide's numbers), not a literal tuned to one size",
            Rules.TextClip when cause.StartsWith("scroll-edge", StringComparison.Ordinal)
                => "give the scroller an edge fade on that side (or declare a guide Bleed with a reason); never shrink the type",
            Rules.TextClip => "grow the clipping box or move the text inside it; Zune display type is never shrunk to fit (wrap, grow, bleed)",
            Rules.OverlapSibling => "give the overlap a ZStack (if it is a designed overlay) or space the siblings; never hide one of them",
            Rules.TextTrim => cause switch
            {
                "near-miss" => "the box is a hair narrower than its words: let the lane grow or take its width from the tier record; never shrink the type",
                "never" => "this text must not trim: wrap it, grow its box or move the secondary content; never shrink Zune display type",
                "overflow" => "the text spills its box: give it a lane that fits (Shrink=1, MinWidth=0, a wrap or an ellipsis by design)",
                "hard-clip" => "Trim=Clip cuts a glyph mid-way: use an ellipsis or room for the words",
                "single" => "a lone title ellipsises: look at the render; under width pressure wrap or re-flow before cutting a title",
                "model" => "the headless text model cannot judge a trim: lint again on a DirectWrite mount",
                _ => "designed ellipses: nothing to do",
            },
            Rules.TypeFloor => "use a type role at or above the floor; the role table decides sizes, not the view",
            Rules.TargetSize => "grow the hit area (padding on the target, or hand the click to the row it sits in); keep the glyph size",
            Rules.DuplicateAction => "one home per verb (owner rule): find the verb's home on the board and ask before taking a control away; the feature stays",
            Rules.SpaceStack => "the blocks overlap: give the stack a gap from the tier record or fix the line height",
            Rules.ContrastText => cause.StartsWith("busy", StringComparison.Ordinal)
                ? "text over art or a gradient: look at the render; a scrim or an on-media ink role fixes it, never a node-by-node colour"
                : "change the theme's ink or ground role for every run of this pair, never node by node",
            Rules.AlignInset => "move the section's extra lead into its plate (padding on the hover plate only) so its ink sits on the column; leave the column alone",
            Rules.AlignBaseline => "anchor the cell's first line at one offset in every row (AlignItems=Start, or reserve the optional line in every row)",
            Rules.SpaceScale => "take the value from Spacing.* or the theme's R.Space/tier record (or declare OffScaleOk with a reason)",
            Rules.TextPlaceholder => "a missing string or argument: add the loc key or pass the argument; never blank the text",
            Rules.KeyHygiene => "key the node by its entity or slot id, without tier, form, count or run-id parts (keys must be stable selectors)",
            _ => "",
        };

        // ── preparing a snapshot ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The key's family: the key up to its first <c>:</c>, <c>#</c>, <c>|</c> or control character (<c>zune-history:spotify:...</c>
        /// gives <c>zune-history</c>; <c>c.title</c> stays itself).</summary>
        public static string FamilyOfKey(string key)
        {
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c is ':' or '#' or '|' || c < ' ') return i > 0 ? key[..i] : key;
            }
            return key;
        }

        /// <summary>The key's pattern: its family, with <c>:*</c> when the key carries more than the family (an id, a slot).</summary>
        public static string PatternOfKey(string key)
        {
            string family = FamilyOfKey(key);
            return family.Length < key.Length ? family + ":*" : key;
        }

        /// <summary>The first <c>spotify:&lt;kind&gt;:&lt;id&gt;</c> in <paramref name="text"/>, "" when none.</summary>
        public static string EntityIn(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int at = text.IndexOf("spotify:", StringComparison.Ordinal);
            if (at < 0) return "";
            int colons = 0, end = at;
            for (; end < text.Length; end++)
            {
                char c = text[end];
                if (c == ':' && ++colons > 2) break;
                if (c is ' ' or '/' or '|' or '#' || c < ' ') break;
            }
            return colons >= 2 ? text[at..end] : "";
        }

        static bool IsInk(Node n)
        {
            if (n.Text is { } t) return t.String.Length > 0 || t.Spans;
            if (n.Image || n.Gradient) return true;
            if (n.Fill.A * n.Opacity >= 0.04f) return true;
            return n.BorderWidth > 0f && n.BorderColor.A * n.Opacity >= 0.04f;
        }

        /// <summary>Derive the fields the rules judge by (see the file header) for <paramref name="scope"/>. Idempotent per scope.</summary>
        public static Snapshot Prepare(Snapshot s, InspectVisible.Scope scope)
        {
            if (s.PreparedFor == scope) return s;
            IReadOnlyList<Node> nodes = s.Nodes;
            int count = nodes.Count;
            var visible = new RectF[count];
            var onScreen = new float[count];
            var inContent = new float[count];
            InspectVisible.Compute(nodes, s.Width, s.Height, visible, onScreen, inContent);

            var inScope = new bool[count];
            var ordinal = new int[count];
            var childCount = new int[count];
            var region = new int[count];
            var inRail = new bool[count];
            var ink = new bool[count];
            var inkCount = new int[count];
            var keyed = new int[count];   // the nearest keyed node, itself included (-1: none)
            for (int i = 0; i < count; i++)
            {
                Node n = nodes[i];
                int p = n.Parent;
                inScope[i] = InspectVisible.InScope(scope, onScreen[i], inContent[i]);
                if (p >= 0) ordinal[i] = childCount[p]++;
                region[i] = p < 0 ? -1 : nodes[p].ScrollAxis == 2 ? p : region[p];
                inRail[i] = p >= 0 && (nodes[p].ScrollAxis == 1 || inRail[p]);
                ink[i] = IsInk(n);
                keyed[i] = n.Key is { Length: > 0 } ? i : p >= 0 ? keyed[p] : -1;
            }
            for (int i = count - 1; i >= 0; i--)
            {
                if (ink[i]) inkCount[i]++;
                int p = nodes[i].Parent;
                if (p >= 0) inkCount[p] += inkCount[i];
            }

            // Families: keyed nodes in scope, counted per key family; a family seen 3 times or more is a repeated one, and each such keyed node is an
            // instance root. A node belongs to its nearest instance root (itself included).
            var familyCount = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
                if (inScope[i] && nodes[i].Key is { Length: > 0 } key)
                {
                    string family = FamilyOfKey(key);
                    familyCount[family] = familyCount.TryGetValue(family, out int c) ? c + 1 : 1;
                }
            var instance = new int[count];
            for (int i = 0; i < count; i++)
            {
                int p = nodes[i].Parent;
                bool root = nodes[i].Key is { Length: > 0 } k && familyCount.TryGetValue(FamilyOfKey(k), out int c) && c >= 3;
                instance[i] = root ? i : p >= 0 ? instance[p] : -1;
            }

            // Backdrops (design-qa proto/geo.py): for each visible ink node in paint order, the last earlier visible ink node (not text) whose visible rect
            // covers 90 % of it and is larger; a ground that covers the window is the page itself.
            var backdrop = new int[count];
            Array.Fill(backdrop, -1);
            var order = new List<int>();
            float windowArea = s.Width * s.Height;
            for (int i = 0; i < count; i++)
            {
                if (!ink[i] || visible[i].IsEmpty) continue;
                RectF v = visible[i];
                float area = v.W * v.H;
                for (int k = order.Count - 1; k >= 0; k--)
                {
                    int m = order[k];
                    if (nodes[m].Text is not null) continue;
                    RectF mv = visible[m];
                    float marea = mv.W * mv.H;
                    RectF both = mv.Intersect(v);
                    if (both.W * both.H >= 0.9f * area && marea > area * 1.05f)
                    {
                        backdrop[i] = marea >= 0.95f * windowArea ? -1 : m;
                        break;
                    }
                }
                order.Add(i);
            }

            var path = new List<int>(16);
            var prepared = new Node[count];
            for (int i = 0; i < count; i++)
            {
                Node n = nodes[i];
                int at = keyed[i];
                string pattern = at >= 0 ? PatternOfKey(nodes[at].Key!) : "#root";
                int root = instance[i];
                string cell = "";
                if (root >= 0 && root != i)
                {
                    path.Clear();
                    for (int j = i; j != root && j >= 0; j = nodes[j].Parent) path.Add(ordinal[j]);
                    path.Reverse();
                    cell = string.Join("/", path);
                }
                TextRun? text = n.Text;
                if (text is not null && s.Backdrop is { } px && text.Ground.A <= 0f) text = WithGround(s, n, text, px);
                prepared[i] = n with
                {
                    Visible = visible[i],
                    OnScreen = onScreen[i],
                    InContent = inContent[i],
                    InScope = inScope[i],
                    Ink = ink[i],
                    InkCount = inkCount[i],
                    Region = region[i],
                    InRail = inRail[i],
                    Backdrop = backdrop[i],
                    Pattern = pattern,
                    Family = root >= 0 ? FamilyOfKey(nodes[root].Key!) : "",
                    Instance = root,
                    CellPath = cell,
                    Text = text,
                };
            }
            return s with { Nodes = prepared, PreparedFor = scope };
        }

        /// <summary>The colour most of a text run's box is rendered in (width capped at its natural width + 2), and its share. A run whose ink fills
        /// its own box (a heart, a solid glyph) is read again without its ink colour. Nothing for a box mostly off the frame.</summary>
        static TextRun WithGround(Snapshot s, Node n, TextRun t, Pixels px)
        {
            float w = t.NaturalW > 0f ? MathF.Min(n.Rect.W, t.NaturalW + 2f) : n.Rect.W;
            var box = new RectF(n.Rect.X, n.Rect.Y, w, n.Rect.H);
            float x0 = MathF.Max(0f, box.X), x1 = MathF.Min(s.Width, box.Right), y0 = MathF.Max(0f, box.Y), y1 = MathF.Min(s.Height, box.Bottom);
            if (x1 - x0 < MathF.Min(2f, w) || y1 - y0 < MathF.Min(2f, n.Rect.H) || (x1 - x0) * (y1 - y0) < 0.6f * w * n.Rect.H) return t;
            var seen = new RectF(x0, y0, x1 - x0, y1 - y0);
            if (!px.TryBackground(seen, null, 0f, out ColorF ground, out float share)) return t;
            if (t.Color.A > 0f && ContrastOf(t.Color, ground, n.Opacity) < 1.15)
            {
                if (!px.TryBackground(seen, t.Color, 0.03f, out ground, out share)) return t;
            }
            // Small type and glyphs anti-alias into many blends of ink and ground, so no single colour may hold half the box over a plain ground.
            // Leave out every pixel near the ink: what remains over a plain ground is one colour; over art or a gradient it stays busy.
            if (share < BusyShare && t.Color.A > 0f && px.TryBackground(seen, t.Color, 0.3f, out ColorF plain, out float plainShare) && plainShare >= BusyShare)
            {
                ground = plain;
                share = plainShare;
            }
            return t with { Ground = ground, GroundShare = share };
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        static string Signed(double v) => (v > 0 ? "+" : "") + F(v);

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

        /// <summary>The guide policy of <paramref name="intent"/> covering <paramref name="n"/> (its key or an ancestor's starts with the prefix).</summary>
        static LintPolicy? PolicyFor(Snapshot s, Node n, LintIntent intent)
        {
            foreach (LintPolicy p in s.Policies)
            {
                if (p.Intent != intent || p.Prefix.Length == 0) continue;
                for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
                    if (s.Nodes[i].Key is { } key && key.StartsWith(p.Prefix, StringComparison.Ordinal)) return p;
            }
            return null;
        }

        static bool IsWords(Node n) => n.Text is { Glyph: false } t && (t.String.Length > 0 || t.Spans);

        /// <summary>The pattern of the repeated family <paramref name="n"/> belongs to (its instance root's), else its own key pattern.</summary>
        static string FamilyPattern(Snapshot s, Node n) => n.Instance >= 0 && s.Nodes[n.Instance].Key is { } key ? PatternOfKey(key) : n.Pattern;

        /// <summary>Up to <paramref name="cap"/> refs and visible rects of <paramref name="members"/>.</summary>
        static (List<string> Refs, List<RectF> Rects) Members(IEnumerable<Node> members, int cap = 6)
        {
            var refs = new List<string>(cap);
            var rects = new List<RectF>(cap);
            foreach (Node n in members)
            {
                if (refs.Count >= cap) break;
                refs.Add(n.Ref);
                rects.Add(n.Visible.IsEmpty ? n.Rect : n.Visible);
            }
            return (refs, rects);
        }

        static string Plural(int n, string one, string many) => n == 1 ? one : many;

        // ── edge.left and edge.right (gutter, right-edge): a theme's edge expectations ─────────────────────────────

        static bool EdgeMatches(Snapshot s, Node n, LintEdge e)
        {
            if (!n.InScope || n.Rect.W <= 0f || n.Rect.H <= 0f) return false;
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

        /// <summary>For each of the guide's edges of the rule: find the leftmost (or rightmost) matching node IN SCOPE (a parked keep-alive copy or a
        /// node scrolled away is never picked) and compare its edge. Returns the edges that matched; an edge nothing matches is not checked.</summary>
        static int EdgeRule(Snapshot s, string id, List<Finding> into)
        {
            string guideRule = id == Rules.EdgeLeft ? Rules.Gutter : Rules.RightEdge;
            int looked = 0;
            foreach (LintEdge e in s.Edges)
            {
                if (!string.Equals(e.Rule, guideRule, StringComparison.Ordinal)) continue;
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
                into.Add(new Finding(id, e.Advisory ? Severity.Warn : Severity.Error,
                    $"{e.What} ({e.Id}): {side} edge at {F(at)}, expected {F(e.Expected)} ({Signed(delta)})", [pick.Ref])
                {
                    Measured = [M("x", at), M("delta", delta)],
                    Expected = [M("x", e.Expected), M("tolerance", e.Tolerance)],
                    Pattern = e.Id,
                    Cause = delta > 0f ? "right-of" : "left-of",
                    Rects = [pick.Visible],
                });
            }
            return looked;
        }

        // ── text.clip and overlap.sibling (clip): text cut by an ancestor, and siblings that overlap ─────────────

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

        static bool Fades(Node n, EdgeMask edge) => (n.FadeEdges & (byte)edge) != 0;

        /// <summary>A cut at a scroller's edge on its own axis, or null when it is no finding: scrolling still reveals the text there (the fold of a
        /// list, a vertical scroller only) or the edge feathers (an edge fade). A guide <see cref="LintIntent.Bleed"/> makes it an intended one.</summary>
        static Finding? ScrollEdgeCut(Snapshot s, Node n, TextRun t, Node scroller, bool vertical, bool start, float over)
        {
            EdgeMask edge = vertical ? (start ? EdgeMask.Top : EdgeMask.Bottom) : (start ? EdgeMask.Left : EdgeMask.Right);
            bool reveals = start ? scroller.ScrollOffset > Slack : scroller.ScrollOffset < scroller.ScrollMax - Slack;
            if (Fades(scroller, edge) || (vertical && reveals)) return null;
            string side = edge switch { EdgeMask.Top => "top", EdgeMask.Bottom => "bottom", EdgeMask.Left => "left", _ => "right" };
            var f = new Finding(Rules.TextClip, vertical ? Severity.Error : Severity.Warn,
                $"text {Q(t.String)} is cut by {scroller.Ref}'s {side} scroll edge, which has no edge fade: {F(over)} past it", [n.Ref, scroller.Ref])
            {
                Measured = [M("over", over), M("scrollOffset", scroller.ScrollOffset), M("scrollMax", scroller.ScrollMax)],
                Expected = [M("over", 0)],
                Pattern = PatternOfKey(scroller.Key ?? scroller.Pattern),
                Cause = "scroll-edge:" + side,
                Rects = [n.Visible, scroller.Visible],
                Outcome = s.RealText ? Outcome.Fail : Outcome.Review,
                Reason = s.RealText ? "" : "headless text model: the ink box is not real glyph metrics",
            };
            if ((PolicyFor(s, n, LintIntent.Bleed) ?? PolicyFor(s, scroller, LintIntent.Bleed)) is { } bleed)
                f = f with { Outcome = Outcome.Suppressed, SuppressedBy = "intent", Reason = bleed.Reason };
            return f;
        }

        /// <summary>Text (in scope, not a glyph, not animating) whose ink runs outside the nearest clipping ancestor that cuts it. A scroller's own
        /// axis is judged by its edges: where scrolling can still reveal the cut text (the fold of a list), or the edge feathers (an edge fade), it is
        /// no finding; a guide <see cref="LintIntent.Bleed"/> makes it an intended one; a hard horizontal scroll edge is a warning, and a vertical
        /// scroll edge that scrolling cannot open (the top at offset 0) an error. Once a scroller cut is judged, the ancestors above it are not.</summary>
        static int ClipRule(Snapshot s, List<Finding> into)
        {
            int looked = 0;
            IReadOnlyList<Node> nodes = s.Nodes;
            foreach (Node n in nodes)
            {
                if (!n.InScope || n.Text is not { } t || !IsWords(n) || n.Animating) continue;
                looked++;
                RectF ink = InkBox(n, t);
                bool doneV = false, doneH = false;
                for (int a = n.Parent; a >= 0 && !(doneV && doneH); a = nodes[a].Parent)
                {
                    Node anc = nodes[a];
                    if (!(anc.Clips || anc.ScrollAxis != 0) || anc.Rect.W <= 0f || anc.Rect.H <= 0f) continue;
                    RectF c = anc.Rect;
                    if (!ink.Overlaps(c)) break;   // wholly outside the clip: hidden, not clipped
                    float top = c.Y - ink.Y, bottom = ink.Bottom - c.Bottom, leftOver = c.X - ink.X, rightOver = ink.Right - c.Right;
                    float vOver = doneV ? 0f : MathF.Max(top, bottom);
                    float hOver = doneH ? 0f : MathF.Max(leftOver, rightOver);
                    bool above = top > bottom, leftSide = leftOver > rightOver;
                    if (anc.ScrollAxis != 0)
                    {
                        // A scroller judges its own axis by its edges, once (the ancestors above it no longer cut that axis); its cross axis clips as any box.
                        bool vertical = anc.ScrollAxis == 2;
                        float over = vertical ? vOver : hOver;
                        if (vertical) { doneV = true; vOver = 0f; } else { doneH = true; hOver = 0f; }
                        if (over > Slack && ScrollEdgeCut(s, n, t, anc, vertical, vertical ? above : leftSide, over) is { } cut)
                        {
                            into.Add(cut);
                            break;
                        }
                    }
                    if (vOver > Slack)
                    {
                        into.Add(new Finding(Rules.TextClip, Severity.Error,
                            $"text {Q(t.String)} is clipped by {anc.Ref}: its ink runs {F(vOver)} past the {(above ? "top" : "bottom")} ({F(ink.Y)}..{F(ink.Bottom)} in {F(c.Y)}..{F(c.Bottom)})",
                            [n.Ref, anc.Ref])
                        {
                            Measured = [M("inkTop", ink.Y), M("inkBottom", ink.Bottom), M("over", vOver), M("size", t.Size), M("lineHeight", t.LineHeight)],
                            Expected = [M("clipTop", c.Y), M("clipBottom", c.Bottom)],
                            Pattern = PatternOfKey(anc.Key ?? anc.Pattern),
                            Cause = above ? "top" : "bottom",
                            Rects = [n.Visible, anc.Visible],
                            Outcome = s.RealText ? Outcome.Fail : Outcome.Review,
                            Reason = s.RealText ? "" : "headless text model: the ink box is not real glyph metrics",
                        });
                        break;
                    }
                    if (hOver > Slack)
                    {
                        into.Add(new Finding(Rules.TextClip, Severity.Warn,
                            $"text {Q(t.String)} is clipped by {anc.Ref}: it runs {F(hOver)} past the {(leftSide ? "left" : "right")} edge ({F(ink.X)}..{F(ink.Right)} in {F(c.X)}..{F(c.Right)})",
                            [n.Ref, anc.Ref])
                        {
                            Measured = [M("inkLeft", ink.X), M("inkRight", ink.Right), M("over", hOver)],
                            Expected = [M("clipLeft", c.X), M("clipRight", c.Right)],
                            Pattern = PatternOfKey(anc.Key ?? anc.Pattern),
                            Cause = leftSide ? "left" : "right",
                            Rects = [n.Visible, anc.Visible],
                            Outcome = s.RealText ? Outcome.Fail : Outcome.Review,
                            Reason = s.RealText ? "" : "headless text model: the ink box is not real glyph metrics",
                        });
                        break;
                    }
                }
            }
            return looked;
        }

        /// <summary>Sibling boxes that overlap, in a parent that is not a ZStack or a scroller, when both carry text or a target and both are in
        /// scope. Pinned headers and nodes mid-animation are left out.</summary>
        static int OverlapRule(Snapshot s, List<Finding> into)
        {
            int looked = 0;
            IReadOnlyList<Node> nodes = s.Nodes;
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < nodes.Count; i++)
            {
                Node n = nodes[i];
                if (n.Parent < 0 || !n.InScope || n.Animating || n.Sticky) continue;
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
                        into.Add(new Finding(Rules.OverlapSibling, Severity.Warn,
                            $"siblings {a.Ref} and {b.Ref} overlap by {F(o.W)} x {F(o.H)} in {parent.Ref}", [a.Ref, b.Ref, parent.Ref])
                        {
                            Measured = [M("overlapX", o.X), M("overlapY", o.Y), M("overlapW", o.W), M("overlapH", o.H)],
                            Expected = [M("overlapW", 0), M("overlapH", 0)],
                            Pattern = parent.Pattern,
                            Cause = PatternOfKey(a.Key ?? a.Pattern) + "+" + PatternOfKey(b.Key ?? b.Pattern),
                            Rects = [a.Visible, b.Visible],
                        });
                    }
                }
            }
            return looked;
        }

        // ── text.trim (trim) ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>How far a no-wrap run's words overrun its box, or a wrapped run's lines its height.</summary>
        static float Overrun(Node n, TextRun t) => t.Wraps ? MathF.Max(0f, t.NaturalH - n.Rect.H) : MathF.Max(0f, t.NaturalW - n.Rect.W);

        /// <summary>A near miss: the box is short by at most max(2 DIP, 1.5 % of what the words want). It visibly ellipsises for nothing.</summary>
        static bool NearMiss(Node n, TextRun t)
        {
            if (t.Wraps) return false;   // a wrapped run that misses its budget loses a line, not a hair
            float over = Overrun(n, t);
            return over > 0f && over <= MathF.Max(2f, 0.015f * t.NaturalW);
        }

        static bool Ellipsis(TextRun t) => t.Trim is "CharacterEllipsis" or "WordEllipsis";

        /// <summary>Text the engine reports trimmed (<c>QueryTextOverflow</c>), in scope, by class (first match wins):
        /// model (the headless text model: review), never (a guide <see cref="LintIntent.NeverTrim"/>: error), overflow (<c>Trim=None</c>: the words
        /// spill, error), near-miss (warn), designed (a guide <see cref="LintIntent.TrimOk"/>, an <c>allowTrim</c> prefix, or MaxLines plus an
        /// ellipsis inside a repeated key family: suppressed, and one info line counts them), hard-clip (<c>Trim=Clip</c>: warn), single (a lone
        /// ellipsis: review).</summary>
        static int TrimRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            var designed = new List<Node>();
            var designedFamilies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Text is not { } t || !IsWords(n)) continue;
                looked++;
                if (!t.Trimmed) continue;
                string how = t.Wraps
                    ? (t.MaxLines > 0 && t.OverflowY ? $"wraps past its {t.MaxLines}-line budget" : "wraps taller than its box")
                    : "is wider than its box";
                string mode = t.Trim switch { "None" => "overflows", "Clip" => "hard-clipped", _ => "ellipsised" };
                float over = Overrun(n, t);
                string cls;
                Severity sev;
                Outcome outcome = Outcome.Fail;
                string reason = "";
                LintPolicy? never = PolicyFor(s, n, LintIntent.NeverTrim);
                LintPolicy? ok = PolicyFor(s, n, LintIntent.TrimOk);
                bool allowed = KeyedUnder(s, n, o.AllowTrim);
                bool implicitDesign = t.MaxLines >= 1 && Ellipsis(t) && n.Family.Length > 0;
                if (!s.RealText) { cls = "model"; sev = Severity.Warn; outcome = Outcome.Review; reason = "headless text model: the trim is the 0.55 x size model's, not DirectWrite's"; }
                else if (never is not null) { cls = "never"; sev = Severity.Error; reason = never.Reason; }
                else if (t.Trim == "None") { cls = "overflow"; sev = Severity.Error; }
                else if (NearMiss(n, t)) { cls = "near-miss"; sev = Severity.Warn; }
                else if (ok is not null || allowed || implicitDesign)
                {
                    cls = "designed";
                    sev = Severity.Info;
                    outcome = Outcome.Suppressed;
                    reason = ok?.Reason ?? (allowed ? "allowTrim" : "MaxLines " + t.MaxLines.ToString(CultureInfo.InvariantCulture) + " + ellipsis in the repeated family " + n.Family);
                    designed.Add(n);
                    string fam = FamilyPattern(s, n);
                    designedFamilies[fam] = designedFamilies.TryGetValue(fam, out int c) ? c + 1 : 1;
                }
                else if (t.Trim == "Clip") { cls = "hard-clip"; sev = Severity.Warn; }
                else { cls = "single"; sev = Severity.Warn; outcome = Outcome.Review; reason = "a lone ellipsis outside any repeated family: is the cut designed?"; }

                string lead = cls switch
                {
                    "near-miss" => $"near-miss: text {Q(t.String)} misses its box by {F(over)} DIP and is {mode}",
                    "never" => $"text {Q(t.String)} must never trim, yet {how} and is {mode}",
                    _ => $"text {Q(t.String)} {how} and is {mode}",
                };
                into.Add(new Finding(Rules.TextTrim, sev,
                    $"{lead}: natural {F(t.NaturalW)} x {F(t.NaturalH)}, box {F(n.Rect.W)} x {F(n.Rect.H)}", [n.Ref])
                {
                    Measured = [M("w", n.Rect.W), M("h", n.Rect.H), M("size", t.Size), M("over", over)],
                    Expected = [M("w", t.NaturalW), M("h", t.NaturalH)],
                    Pattern = FamilyPattern(s, n),
                    Cause = cls,
                    Outcome = outcome,
                    SuppressedBy = outcome == Outcome.Suppressed ? "intent" : "",
                    Reason = reason,
                    Rects = [n.Visible],
                });
            }
            if (designed.Count > 0)
            {
                var (refs, rects) = Members(designed);
                string families = string.Join(", ", designedFamilies.OrderByDescending(static kv => kv.Value).Select(static kv => kv.Key + " x" + kv.Value.ToString(CultureInfo.InvariantCulture)));
                into.Add(new Finding(Rules.TextTrim, Severity.Info,
                    $"{designed.Count} designed {Plural(designed.Count, "ellipsis", "ellipses")} (MaxLines + ellipsis in a repeated family, or a guide TrimOk): {families}", refs)
                {
                    Measured = [M("count", designed.Count), M("families", designedFamilies.Count)],
                    Pattern = "designed",
                    Cause = "designed-total",
                    Count = designed.Count,
                    Rects = rects,
                });
            }
            return looked;
        }

        // ── type.floor and type.scale (type) ────────────────────────────────────────────────────────────────────────

        readonly record struct ScaleKey(float Size, float Line, int Weight);

        sealed class Bin
        {
            public int Count;
            public readonly List<Node> Samples = [];

            public void Add(Node sample, int cap)
            {
                Count++;
                if (Samples.Count < cap) Samples.Add(sample);
            }
        }

        /// <summary>An error per size under <see cref="Options.TypeFloor"/> (words in scope; glyphs are not type).</summary>
        static int TypeFloorRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            var small = new SortedDictionary<float, Bin>();
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Text is not { } t || !IsWords(n)) continue;
                looked++;
                if (t.Size + 0.01f >= o.TypeFloor) continue;
                float size = MathF.Round(t.Size, 2);
                if (!small.TryGetValue(size, out Bin? low)) small[size] = low = new Bin();
                low.Add(n, 8);
            }
            foreach (var (size, bin) in small)
            {
                var (refs, rects) = Members(bin.Samples, 8);
                into.Add(new Finding(Rules.TypeFloor, Severity.Error,
                    $"{bin.Count} text {Plural(bin.Count, "node", "nodes")} at {F(size)} DIP, under the {F(o.TypeFloor)} DIP floor", refs)
                {
                    Measured = [M("size", size), M("count", bin.Count)],
                    Expected = [M("minSize", o.TypeFloor)],
                    Pattern = bin.Samples[0].Pattern,
                    Cause = "size:" + F(size),
                    Count = bin.Count,
                    Rects = rects,
                });
            }
            return looked;
        }

        /// <summary>Every distinct size / line height / weight in scope with its count (info).</summary>
        static int TypeScaleRule(Snapshot s, List<Finding> into)
        {
            int looked = 0;
            var scale = new Dictionary<ScaleKey, Bin>();
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Text is not { } t || !IsWords(n)) continue;
                looked++;
                var key = new ScaleKey(MathF.Round(t.Size, 2), MathF.Round(t.LineHeight, 2), t.Weight);
                if (!scale.TryGetValue(key, out Bin? bin)) scale[key] = bin = new Bin();
                bin.Add(n, 5);
            }
            foreach (var (key, bin) in scale.OrderByDescending(static kv => kv.Key.Size).ThenByDescending(static kv => kv.Key.Line).ThenBy(static kv => kv.Key.Weight))
            {
                string line = key.Line > 0f ? F(key.Line) : "auto";
                var (refs, rects) = Members(bin.Samples, 5);
                into.Add(new Finding(Rules.TypeScale, Severity.Info,
                    $"type scale: {F(key.Size)}/{line} weight {key.Weight} x{bin.Count}", refs)
                {
                    Measured = [M("size", key.Size), M("lineHeight", key.Line), M("weight", key.Weight), M("count", bin.Count)],
                    Pattern = "type",
                    Cause = F(key.Size) + "/" + line + "/" + key.Weight.ToString(CultureInfo.InvariantCulture),
                    Count = bin.Count,
                    Rects = rects,
                });
            }
            return looked;
        }

        // ── contrast.text (contrast) ────────────────────────────────────────────────────────────────────────────────

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

        static string Hex(ColorF c)
            => ((int)MathF.Round(c.R * 255f)).ToString("x2", CultureInfo.InvariantCulture) + ((int)MathF.Round(c.G * 255f)).ToString("x2", CultureInfo.InvariantCulture)
               + ((int)MathF.Round(c.B * 255f)).ToString("x2", CultureInfo.InvariantCulture);

        static string HexA(ColorF c) => Hex(c) + ((int)MathF.Round(c.A * 255f)).ToString("x2", CultureInfo.InvariantCulture);

        sealed class Pair
        {
            public readonly List<Node> Runs = [];
            public double Worst = double.MaxValue;
            public float Need;
            public ColorF Ground;
            public string Over = "";
            public readonly List<string> Inks = [];
        }

        /// <summary>Each in-scope run's declared colour (and its opacity) against the colour most of its box was rendered in, GROUPED by (ink, ground):
        /// one finding per pair, with its runs as members. A run over a busy ground (no colour covers 45 % of its box: art, a gradient) cannot be
        /// judged from one colour, so its group is a `review` naming what it sits on, never silence. Skipped: a disabled subtree, a node mid-animation,
        /// a transparent ink, a span run, and a run with no ground read (no frame: `gpu=none`, a crossing).</summary>
        static int ContrastRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            var pairs = new Dictionary<string, Pair>(StringComparer.Ordinal);
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Text is not { } t || t.Spans || t.String.Length == 0 || n.Inert || n.Animating) continue;
                if (t.Color.A * n.Opacity < 0.02f || t.Ground.A <= 0f) continue;
                looked++;
                float need = ContrastNeeded(t, o);
                string ink = HexA(t.Color) + (n.Opacity < 0.995f ? "@" + F(n.Opacity) : "");
                ColorF ground = t.Ground;
                if (t.GroundShare < BusyShare)
                {
                    Node? under = n.Backdrop >= 0 ? s.Nodes[n.Backdrop] : null;
                    if (under is { Image: false, Gradient: false } plate && plate.Fill.A * plate.Opacity >= 0.98f)
                        ground = plate.Fill with { A = 1f };   // a solid plate: its declared fill is the ground
                    else
                    {
                        string over = under is null ? "the page" : (under.Image ? "art " : under.Gradient ? "a gradient " : "a plate ") + under.Pattern;
                        string busyKey = "busy|" + over;
                        if (!pairs.TryGetValue(busyKey, out Pair? busy)) pairs[busyKey] = busy = new Pair { Need = need, Over = over };
                        busy.Runs.Add(n);
                        if (!busy.Inks.Contains(ink)) busy.Inks.Add(ink);
                        continue;
                    }
                }
                double ratio = ContrastOf(t.Color, ground, n.Opacity);
                if (ratio + 0.005 >= need) continue;
                string pairKey = ink + "|" + Hex(ground) + "|" + F(need);
                if (!pairs.TryGetValue(pairKey, out Pair? pair)) pairs[pairKey] = pair = new Pair { Need = need, Ground = ground };
                pair.Runs.Add(n);
                pair.Worst = Math.Min(pair.Worst, ratio);
            }
            foreach (var (key, pair) in pairs)
            {
                var (refs, rects) = Members(pair.Runs);
                Node first = pair.Runs[0];
                TextRun t = first.Text!;
                string ink = "#" + HexA(t.Color);
                string runs = pair.Runs.Count == 1 ? $"text {Q(t.String)}" : $"{pair.Runs.Count} runs ({Q(t.String)}, ...)";
                if (key.StartsWith("busy|", StringComparison.Ordinal))
                {
                    string inks = string.Join(", ", pair.Inks.Take(4).Select(static i => "#" + i)) + (pair.Inks.Count > 4 ? ", ..." : "");
                    into.Add(new Finding(Rules.ContrastText, Severity.Warn,
                        $"{runs} ({inks}) sit on a busy ground ({pair.Over}): no colour holds half a box, so contrast cannot be read from one colour; look at the render", refs)
                    {
                        Measured = [M("count", pair.Runs.Count), M("inks", pair.Inks.Count)],
                        Expected = [M("minRatio", pair.Need)],
                        Pattern = pair.Over,
                        Cause = "busy",
                        Count = pair.Runs.Count,
                        Outcome = Outcome.Review,
                        Reason = "busy ground",
                        Rects = rects,
                    });
                    continue;
                }
                into.Add(new Finding(Rules.ContrastText, Severity.Warn,
                    $"{runs} in {ink} read {F(pair.Worst)} : 1 on #{Hex(pair.Ground)}, under the {F(pair.Need)} : 1 they need", refs)
                {
                    Measured = [M("ratio", pair.Worst), M("count", pair.Runs.Count), M("size", t.Size), M("weight", t.Weight)],
                    Expected = [M("minRatio", pair.Need)],
                    Pattern = ink,
                    Cause = "on #" + Hex(pair.Ground),
                    Count = pair.Runs.Count,
                    Rects = rects,
                });
            }
            return looked;
        }

        // ── target.size (target) ────────────────────────────────────────────────────────────────────────────────────

        static bool ExemptFromTarget(Node n) => n.Role is "Slider" or "ScrollBar" or "ProgressBar" or "Hyperlink";

        /// <summary>The area a click on <paramref name="n"/> really has: the largest interactive ancestor that runs the same click and contains it
        /// (a role-only node hands its click to the first clickable ancestor), else the node itself.</summary>
        static RectF EffectiveArea(Snapshot s, Node n)
        {
            RectF best = n.Rect;
            int click = n.ClickId;
            for (int i = n.Parent; i >= 0; i = s.Nodes[i].Parent)
            {
                Node a = s.Nodes[i];
                if (!a.Interactive) continue;
                if (click == 0 && a.ClickId != 0) click = a.ClickId;
                else if (a.ClickId != click) break;
                RectF both = a.Rect.Intersect(n.Rect);
                if (both.W < n.Rect.W - Slack || both.H < n.Rect.H - Slack) break;   // does not contain it
                if (a.Rect.W * a.Rect.H > best.W * best.H) best = a.Rect;
            }
            return best;
        }

        /// <summary>A drag bar (a splitter, a seek line): thin across, long along. It is reached along its length, so the floor does not apply.</summary>
        static bool IsBar(in RectF area, float floor)
            => MathF.Min(area.W, area.H) <= floor / 2f && MathF.Max(area.W, area.H) >= 4f * floor;

        static int Root(int[] parent, int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        /// <summary>In-scope click targets whose effective area is under the floor: <see cref="Options.TargetFloorCompact"/> (40) at window tier
        /// Compact, an error; <see cref="Options.TargetFloor"/> (32) above it, a warning. Drag bars are exempt. Grouped by family: targets that share
        /// a key pattern or a parent are one finding (the band's Play and Shuffle, a pager's dots, a crumb trail).</summary>
        static int TargetRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            bool compact = s.Tier == WidthTier.Compact;
            float floor = compact ? o.TargetFloorCompact : o.TargetFloor;
            var small = new List<(Node Node, RectF Area)>();
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || !n.Interactive || n.Disabled || ExemptFromTarget(n)) continue;
                looked++;
                if (n.Rect.W >= floor - Slack && n.Rect.H >= floor - Slack) continue;
                if (n.Parent >= 0)
                {
                    Node p = s.Nodes[n.Parent];
                    if (p.Interactive && MathF.Abs(p.Rect.W - n.Rect.W) <= Slack && MathF.Abs(p.Rect.H - n.Rect.H) <= Slack) continue;
                }
                RectF area = EffectiveArea(s, n);
                if (area.W >= floor - Slack && area.H >= floor - Slack) continue;
                if (IsBar(area, floor)) continue;
                small.Add((n, area));
            }
            // One family: the same key pattern, or the same parent.
            var root = new int[small.Count];
            for (int i = 0; i < root.Length; i++) root[i] = i;
            var byPattern = new Dictionary<string, int>(StringComparer.Ordinal);
            var byParent = new Dictionary<int, int>();
            for (int i = 0; i < small.Count; i++)
            {
                Node n = small[i].Node;
                string pattern = PatternOfKey(n.Key ?? n.Pattern);
                if (byPattern.TryGetValue(pattern, out int j)) root[Root(root, i)] = Root(root, j); else byPattern[pattern] = i;
                if (byParent.TryGetValue(n.Parent, out int k)) root[Root(root, i)] = Root(root, k); else byParent[n.Parent] = i;
            }
            var groups = new Dictionary<int, List<(Node Node, RectF Area)>>();
            for (int i = 0; i < small.Count; i++)
            {
                int g = Root(root, i);
                if (!groups.TryGetValue(g, out var list)) groups[g] = list = [];
                list.Add(small[i]);
            }
            string floorText = compact ? $"the {F(floor)} x {F(floor)} Compact floor" : $"the {F(floor)} x {F(floor)} floor (Small and up)";
            foreach (var (_, list) in groups)
            {
                Node n = list[0].Node;
                var (refs, rects) = Members(list.Select(static e => e.Node));
                string sizes = string.Join(", ", list.Take(4).Select(static e => TargetName(e.Node) + " " + F(e.Area.W) + " x " + F(e.Area.H)))
                    + (list.Count > 4 ? ", ..." : "");
                bool narrow = list.Exists(e => e.Area.W < floor - Slack), low = list.Exists(e => e.Area.H < floor - Slack);
                string pattern = list.Select(static e => PatternOfKey(e.Node.Key ?? e.Node.Pattern)).Order(StringComparer.Ordinal).First();
                float minW = list.Min(static e => e.Area.W), minH = list.Min(static e => e.Area.H);
                string head = list.Count > 1 ? $"{list.Count} hit targets in {pattern}" : $"hit target {n.Ref}";
                into.Add(new Finding(Rules.TargetSize, compact ? Severity.Error : Severity.Warn, $"{head} under {floorText}: {sizes}", refs)
                {
                    Measured = [M("w", minW), M("h", minH), M("count", list.Count)],
                    Expected = [M("w", floor), M("h", floor)],
                    Pattern = pattern,
                    Cause = (narrow ? "narrow" : "") + (narrow && low ? "+" : "") + (low ? "low" : ""),
                    Count = list.Count,
                    Rects = rects,
                });
            }
            return looked;
        }

        /// <summary>A target as a message names it: its glyph's icon name, its label, else its ref.</summary>
        static string TargetName(Node n) => n.LabelGlyph.Length > 0 ? "glyph:" + n.LabelGlyph : n.Label.Length > 0 ? Q(n.Label) : n.Ref;

        // ── duplicate.action (duplicate) ────────────────────────────────────────────────────────────────────────────

        /// <summary>A verb: a button-like target (Button, ToggleButton, MenuItem or no role) that is not a content card (a card or row carries art or
        /// a title and a subtitle: its label is a name, not a verb).</summary>
        static bool IsVerb(Node n) => n.Role is "Button" or "ToggleButton" or "MenuItem" or "" && n.ImageCount == 0 && n.TextCount < 2;

        /// <summary>What a verb acts on: the first spotify entity in its key or an ancestor's, else the page's own entity, else the page.</summary>
        static string SubjectOf(Snapshot s, Node n)
        {
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
                if (EntityIn(s.Nodes[i].Key) is { Length: > 0 } entity) return entity;
            return s.PageEntity.Length > 0 ? s.PageEntity : "page";
        }

        /// <summary>The same verb on the same subject offered in two homes on screen (the band's Shuffle and the song list's Shuffle on an album). A
        /// verb is matched by role and label (a glyph by its icon name); a key family is one home ("See all" per shelf is one pattern); content cards
        /// and links are not verbs; the title bar and play bar (<see cref="Options.IgnoreUnder"/>) repeat the page's verbs by design.</summary>
        static int DuplicateRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            var groups = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || !n.Interactive || n.Disabled || n.Label.Length == 0 || !IsVerb(n) || KeyedUnder(s, n, o.IgnoreUnder)) continue;
                bool allowed = false;
                foreach (string label in o.AllowDuplicate)
                    if (string.Equals(label, n.Label, StringComparison.OrdinalIgnoreCase) || string.Equals(label, n.LabelGlyph, StringComparison.OrdinalIgnoreCase)) { allowed = true; break; }
                if (allowed) continue;
                bool inner = false;   // only the outermost target of a label counts
                for (int i = n.Parent; i >= 0 && !inner; i = s.Nodes[i].Parent)
                    inner = s.Nodes[i].Interactive && string.Equals(s.Nodes[i].Label, n.Label, StringComparison.Ordinal);
                if (inner) continue;
                looked++;
                string verb = n.LabelGlyph.Length > 0 ? "glyph:" + n.LabelGlyph : n.Label.ToLowerInvariant();
                string group = n.Role + "|" + verb + "|" + SubjectOf(s, n);
                if (!groups.TryGetValue(group, out List<Node>? list)) groups[group] = list = [];
                list.Add(n);
            }
            foreach (var (group, list) in groups)
            {
                if (list.Count < 2) continue;
                var homes = new List<string>();
                foreach (Node n in list)
                {
                    string home = n.Pattern == "#root" ? "" : FamilyOfKey(n.Pattern);
                    if (!homes.Contains(home)) homes.Add(home);
                }
                if (homes.Count < 2) continue;
                var (refs, rects) = Members(list);
                Node first = list[0];
                string verb = first.LabelGlyph.Length > 0 ? "glyph:" + first.LabelGlyph : Q(first.Label);
                string subject = group[(group.LastIndexOf('|') + 1)..];
                var f = new Finding(Rules.DuplicateAction, Severity.Warn,
                    $"{verb} on {subject} is offered {list.Count} times on screen, in {homes.Count} homes ({string.Join(", ", homes.Select(static h => h.Length == 0 ? "(unkeyed)" : h))})", refs)
                {
                    Measured = [M("count", list.Count), M("homes", homes.Count)],
                    Expected = [M("homes", 1)],
                    Pattern = verb,
                    Cause = string.Join("+", homes),
                    Count = list.Count,
                    Rects = rects,
                };
                foreach (Node n in list)
                    if (PolicyFor(s, n, LintIntent.RepeatOk) is { } repeat)
                    {
                        f = f with { Outcome = Outcome.Suppressed, SuppressedBy = "intent", Reason = repeat.Reason };
                        break;
                    }
                into.Add(f);
            }
            return looked;
        }

        // ── space.stack (rhythm) ────────────────────────────────────────────────────────────────────────────────────

        static bool IsTextBlock(Node n) => IsWords(n) || (n.Text is null && n.TextCount == 1 && !n.Interactive);

        sealed class StackGroup
        {
            public readonly List<Node> Parents = [];
            public readonly List<float> Gaps = [];
            public float Worst = float.MaxValue;
        }

        /// <summary>The vertical gaps between consecutive text blocks stacked in one in-scope column. Blocks that OVERLAP (a gap under -0.5) are a
        /// warning; blocks closer than <see cref="Options.GapMin"/> TOUCH (a line box includes its leading, so a title and its subtitle at 0 apart
        /// read fine) and are a report, as are the gaps themselves. Grouped by the column's key pattern: one finding per family and kind.</summary>
        static int RhythmRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            IReadOnlyList<Node> nodes = s.Nodes;
            var stacks = new Dictionary<int, List<Node>>();
            foreach (Node n in nodes)
            {
                if (n.Parent < 0 || !n.InScope || n.Animating || !IsTextBlock(n)) continue;
                if (!stacks.TryGetValue(n.Parent, out List<Node>? list)) stacks[n.Parent] = list = [];
                list.Add(n);
            }
            var overlap = new Dictionary<string, StackGroup>(StringComparer.Ordinal);
            var touch = new Dictionary<string, StackGroup>(StringComparer.Ordinal);
            var gaps = new Dictionary<string, StackGroup>(StringComparer.Ordinal);
            foreach (var (parentIndex, list) in stacks)
            {
                Node parent = nodes[parentIndex];
                if (parent.ZStack || !parent.Column || list.Count < 2) continue;
                list.Sort(static (a, b) => a.Rect.Y.CompareTo(b.Rect.Y));
                string pattern = parent.Pattern;   // the column's nearest key: every row's stack of one family is one group
                for (int i = 0; i + 1 < list.Count; i++)
                {
                    Node a = list[i], b = list[i + 1];
                    if (a.Rect.Intersect(new RectF(b.Rect.X, a.Rect.Y, b.Rect.W, a.Rect.H)).W <= 0f) continue;   // side by side, not stacked
                    float gap = b.Rect.Y - a.Rect.Bottom;
                    looked++;
                    Dictionary<string, StackGroup> into2 = gap < -Slack ? overlap : gap < o.GapMin ? touch : gaps;
                    if (!into2.TryGetValue(pattern, out StackGroup? g)) into2[pattern] = g = new StackGroup();
                    if (!g.Parents.Contains(parent)) g.Parents.Add(parent);
                    g.Gaps.Add(gap);
                    g.Worst = MathF.Min(g.Worst, gap);
                }
            }
            foreach (var (pattern, g) in overlap)
            {
                var (refs, rects) = Members(g.Parents);
                into.Add(new Finding(Rules.SpaceStack, Severity.Warn,
                    $"stacked text blocks overlap by up to {F(-g.Worst)} in {pattern} ({g.Gaps.Count} {Plural(g.Gaps.Count, "pair", "pairs")})", refs)
                {
                    Measured = [M("gap", g.Worst), M("pairs", g.Gaps.Count)],
                    Expected = [M("minGap", 0)],
                    Pattern = pattern,
                    Cause = "overlap",
                    Count = g.Parents.Count,
                    Rects = rects,
                });
            }
            foreach (var (pattern, g) in touch)
            {
                var (refs, rects) = Members(g.Parents);
                into.Add(new Finding(Rules.SpaceStack, Severity.Info,
                    $"stacked text blocks touch in {pattern}: {g.Gaps.Count} {Plural(g.Gaps.Count, "gap", "gaps")} under {F(o.GapMin)} (smallest {F(g.Worst)}); fine where the render shows air between the ink", refs)
                {
                    Measured = [M("gap", g.Worst), M("pairs", g.Gaps.Count)],
                    Expected = [M("minGap", o.GapMin)],
                    Pattern = pattern,
                    Cause = "touching",
                    Count = g.Parents.Count,
                    Rects = rects,
                });
            }
            foreach (var (pattern, g) in gaps)
            {
                var (refs, rects) = Members(g.Parents);
                into.Add(new Finding(Rules.SpaceStack, Severity.Info,
                    $"{g.Gaps.Count + g.Parents.Count} stacked text blocks in {pattern}: gaps {string.Join(", ", g.Gaps.Take(8).Select(static v => F(v)))}{(g.Gaps.Count > 8 ? ", ..." : "")}", refs)
                {
                    Measured = g.Gaps.Take(8).Select((v, i) => M("gap" + i.ToString(CultureInfo.InvariantCulture), v)).ToList(),
                    Pattern = pattern,
                    Cause = "gaps",
                    Count = g.Parents.Count,
                    Rects = rects,
                });
            }
            return looked;
        }
    }
}
