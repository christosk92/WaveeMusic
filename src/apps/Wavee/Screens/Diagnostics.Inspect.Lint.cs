// ── Screens/Diagnostics.Inspect.Lint.cs — the `--inspect` lint: a snapshot of nodes in, findings with outcomes out ───────────────
// the model (snapshot, finding, report), the rule catalog, the run (prepare → rules → intents → baseline → fingerprints) and today's nine
// rules (edge.left/right, text.clip, overlap.sibling, text.trim, type.floor/scale, target.size, duplicate.action, space.stack, contrast.text)
//
// Role: CORE (pure: no engine, no host, no thread; the scene is read into a `Snapshot` by Diagnostics.Inspect.Lint.Scene.cs, or a saved one
// is read back by Diagnostics.Inspect.Lint.Store.cs). The report-only rules (align.inset, align.baseline, space.scale, text.placeholder,
// key.hygiene, text.duplicate, target.ink, space.dead) are Diagnostics.Inspect.Lint.Layout.cs, .Lint.Content.cs and .Lint.Ink.cs. User
// reference: docs/guide/agent-lint.md.
// Spec: scratch/agentic-e2e/PLAN.md 4.5 and 5.1-5.5; the algorithms and false-positive classes: scratch/agentic-e2e/design/design-qa.md.
//
// THE SNAPSHOT is the dump's node list without the JSON: depth-first, `Nodes[i].Index == i`, a node's `Parent` an earlier index (-1 for a
// root), every rect the PRESENTED absolute rect in window DIP. Only what paints is in it (hidden, parked and empty-box nodes are not).
// `Prepare` derives what the rules judge by: the VISIBLE rect and on-screen fraction (Diagnostics.Inspect.Visible.cs), what a modal scrim or
// an opaque fill painted later COVERS, ink, the scroll REGION and the BACKDROP (the plate a node sits on), the key PATTERN (`zune-history:*`),
// the repeated key FAMILY with its CELL PATH (counted over everything scrolling reaches, so neither the scope nor the scroll position moves
// it), and, from a rendered frame, the ground under each text run. Every rule then reads only what is in scope (`scope=screen`: at least
// half visible).
//
// THE FINDING (PLAN 4.5): `rule` (today's name for the nine, the id for the new ones), `id` (family.name), severity, OUTCOME (fail: the rule's
// verdict; review: it cannot decide, look at the render; suppressed: an intent or a baseline entry matched; report: an info line stating what
// is there, no verdict), LEVEL (gate: today's nine; todo: report-only, never counted in `summary`), `fp` (rule|theme|route|pattern|cause, no
// sizes or positions, so one baseline line covers a flaw at every window size where the cause is the same), `group` {count, pattern} (members
// collapse into one finding, and findings of one rule that share a fingerprint collapse too; the pattern is the members' nearest keyed node's,
// never a family counted over the scope, so the same flaw keeps its fingerprint at every scope, scroll position and `root=`), a message,
// measured/expected, and a one-line `fix` that never says to shrink Zune display type, to touch motion or to remove a feature (on Classic:
// "report; do not fix unless asked").
// `summary` counts fail findings (and report lines) of the gate rules at or above `min`, listed or not.
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
                TextPlaceholder = "text.placeholder", KeyHygiene = "key.hygiene", TextDuplicate = "text.duplicate", TargetInk = "target.ink",
                SpaceDead = "space.dead";

            public const string PictureBlank = "picture.blank", PictureUnpainted = "picture.unpainted", OverlayOpen = "overlay.open";

            // lane A3 (2026-10-24), report-only: Diagnostics.Inspect.Lint.Audit.cs and Diagnostics.Inspect.Picture.cs
            public const string StageOverlap = "stage.overlap", TextCopies = "text.copies", OverlayTranslucent = "overlay.translucent",
                OverlayNarrow = "overlay.narrow", TextRuled = "text.ruled", EdgeGutter = "edge.gutter", EdgeHuge = "edge.huge",
                TypeVoice = "type.voice", StateRest = "state.rest", PlayingStatus = "playing.status", ControlRequired = "control.required",
                EdgeWindow = "edge.window", SpaceHuge = "space.huge", PictureText = "picture.text";
        }

        /// <summary>Today's nine rule names, in the order the report lists them (each is an alias of one or two ids).</summary>
        public static readonly string[] RuleNames =
            [Rules.Gutter, Rules.RightEdge, Rules.Clip, Rules.Trim, Rules.Type, Rules.Target, Rules.Duplicate, Rules.Rhythm, Rules.Contrast];

        /// <summary>gate: a rule whose fail findings count in the report's summary (today's nine); todo: report-only until it is promoted on the
        /// replay set (PLAN 5.6), so a new rule never breaks anyone's "lint min=error clean".</summary>
        public enum Level : byte { Gate, Todo }

        /// <summary>fail: the rule's verdict; review: the rule cannot decide (look at the render); suppressed: an intent or a baseline entry matched;
        /// report: an info line that states what is there (the type styles in use, the gaps of a stack, the designed ellipses), not a verdict.</summary>
        public enum Outcome : byte { Fail, Review, Suppressed, Report }

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
            new(Rules.TargetSize, Rules.Target, Level.Gate, "click targets under the tier's floor, by their effective area (Compact's 40 DIP floor gates; the 32 DIP floor above it is todo)"),
            new(Rules.DuplicateAction, Rules.Duplicate, Level.Gate, "the same verb on the same subject in two homes"),
            new(Rules.SpaceStack, Rules.Rhythm, Level.Gate, "stacked text blocks that overlap (touching ones are a report)"),
            new(Rules.ContrastText, Rules.Contrast, Level.Gate, "text contrast against its rendered ground, by ink and ground"),
            new(Rules.PictureBlank, "", Level.Gate, "a rendered frame that is one colour: nothing painted"),
            new(Rules.PictureUnpainted, "", Level.Gate, "a page that stops painting short of its frame: a cut-short scroller over a band of one colour"),
            new(Rules.OverlayOpen, "", Level.Gate, "an overlay the verb that staged a state left open over it"),
            new(Rules.AlignInset, "", Level.Todo, "a section's ink lead 4-16 DIP off its region's column line"),
            new(Rules.AlignBaseline, "", Level.Todo, "a repeated family's text drifting against its row top"),
            new(Rules.SpaceScale, "", Level.Todo, "declared gap, padding and margin off the theme's spacing scale"),
            new(Rules.TextPlaceholder, "", Level.Todo, "a missing loc key, a missing argument, NaN or null on screen"),
            new(Rules.KeyHygiene, "", Level.Todo, "keys that break selectors: duplicates, control characters, run ids, tier words"),
            new(Rules.TextDuplicate, "", Level.Todo, "the same visible words twice in one column within a glance (a heading repeated as an eyebrow)"),
            new(Rules.TargetInk, "", Level.Todo, "a click target that paints nothing at rest"),
            new(Rules.SpaceDead, "", Level.Todo, "an empty slot that keeps its room beside content, with no plate (a report)"),
            new(Rules.StageOverlap, "", Level.Todo, "now-playing stage layers whose plates collide, or text straddling another layer's plate edge"),
            new(Rules.TextCopies, "", Level.Todo, "two different lines of one text family drawn over each other (a crossfade stacking both)"),
            new(Rules.OverlayTranslucent, "", Level.Todo, "a popup plate the page bleeds through (its empty plate is not one colour)"),
            new(Rules.OverlayNarrow, "", Level.Todo, "an overlay narrower than its content: most of its rows trimmed to a few letters"),
            new(Rules.TextRuled, "", Level.Gate, "a rule or line crossing a text's glyph box (an underline through descenders); promoted 10-24: every sweep hit real"),
            new(Rules.EdgeGutter, "", Level.Todo, "page text leading left of the theme guide's gutter on a column the guide does not list"),
            new(Rules.EdgeHuge, "", Level.Todo, "right edges at Huge and Huge4K, mirrored from the guide's left lead (the guide declares them at Small and Standard)"),
            new(Rules.TypeVoice, "", Level.Gate, "UI copy in another theme's voice (Title Case on Zune; light lowercase heads on Classic); promoted 10-24: every sweep hit real"),
            new(Rules.StateRest, "", Level.Todo, "a detail page whose subject shows no saved or following state at rest"),
            new(Rules.PlayingStatus, "", Level.Todo, "now-playing surfaces disagreeing with the player (Nothing playing while a track plays, an empty source line)"),
            new(Rules.ControlRequired, "", Level.Todo, "a surface without a control it must offer (a held-open transport with no play/pause or seek)"),
            new(Rules.EdgeWindow, "", Level.Todo, "content laid out past the window's right edge, outside any rail"),
            new(Rules.SpaceHuge, "", Level.Todo, "a band of the page at Huge or Huge4K with nothing in it, as wide as a column"),
            new(Rules.PictureText, "", Level.Todo, "text the scene shows that never reaches the frame (its box paints one colour), less edge fades and sticky plates"),
        ];

        /// <summary>Every rule id, in report order.</summary>
        public static readonly string[] RuleIds = Catalog.Select(static r => r.Id).ToArray();

        /// <summary>Every word `rules=` takes (what <see cref="IdsOf"/> resolves): today's nine names, the ids, each family (<c>align</c>) and its
        /// <c>.*</c> form, and <c>all</c>. The verb table declares these as the choices of `rules`.</summary>
        public static readonly string[] RuleWords = BuildRuleWords();

        static string[] BuildRuleWords()
        {
            var words = new List<string>(RuleNames);
            foreach (RuleSpec r in Catalog)
                if (!words.Contains(r.Id)) words.Add(r.Id);
            foreach (string suffix in (string[])["", ".*"])
                foreach (RuleSpec r in Catalog)
                {
                    string family = r.Id[..r.Id.IndexOf('.')] + suffix;
                    if (!words.Contains(family)) words.Add(family);
                }
            words.Add("all");
            return words.ToArray();
        }

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

        /// <summary><see cref="Node.Lane"/> for a node with no scroll bar lane inside its slop on any side.</summary>
        public static readonly Edges4 NoLane = new(-1f, -1f, -1f, -1f);

        /// <summary>The share of a text box one colour must hold to be its ground; under it the ground is busy (art, a gradient).</summary>
        public const float BusyShare = 0.45f;

        /// <summary>Over a busy ground (a photograph, a scrim, a gradient), the share of a run's ground pixels allowed under the ratio it needs: the
        /// ratio is read at this percentile of the ordered per-pixel ratios (0.1: judged against its p90 worst ground, WCAG G18). Read with the
        /// frame, so a saved snapshot replays the same answer.</summary>
        public const float MediaPercentile = 0.1f;

        public enum Severity : byte { Info, Warn, Error }

        public static string Name(Severity s) => s switch { Severity.Error => "error", Severity.Warn => "warn", _ => "info" };

        public static string Name(Outcome o) => o switch { Outcome.Review => "review", Outcome.Suppressed => "suppressed", Outcome.Report => "report", _ => "fail" };

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
                case "report": outcome = Outcome.Report; return true;
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

            /// <summary>The top and bottom of the glyphs' ink (their black boxes, the engine's <c>TryQueryTextInk</c>), in DIP from the top of
            /// the node's box; NaN when not known (the headless text model, a snapshot saved before ink was read).</summary>
            public float InkTop { get; init; } = float.NaN;
            public float InkBottom { get; init; } = float.NaN;

            /// <summary>The glyph ink is known (<see cref="InkTop"/>, <see cref="InkBottom"/>).</summary>
            public bool HasInk => float.IsFinite(InkTop) && float.IsFinite(InkBottom) && InkBottom > InkTop;

            /// <summary>An icon glyph (a private-use character), not words: out of the type scale and the clip rule.</summary>
            public bool Glyph { get; init; }

            /// <summary>The words are the app's own copy: a base-culture loc string, compared case-folded (Zune lowercases on screen), not data (a
            /// title, a name). What <c>type.voice</c> judges.</summary>
            public bool Copy { get; init; }

            /// <summary>The case transform the run is shaped with (<c>TextStyle.Case</c>: <c>Lower</c>, <c>Upper</c>; "" as written): the string
            /// is stored as written, the screen shows it transformed.</summary>
            public string Case { get; init; } = "";

            /// <summary>The words as the screen shows them (<see cref="Case"/> applied).</summary>
            public string Shown => Case switch { "Lower" => String.ToLowerInvariant(), "Upper" => String.ToUpperInvariant(), _ => String };

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

            /// <summary>Over a busy ground: the contrast of the ink against the rendered pixels around its glyphs at <see cref="MediaPercentile"/>
            /// (<see cref="Pixels.TryWorstContrast"/>; 0: not read), that ground pixel's colour, and how many gap pixels made the ground.</summary>
            public float Worst { get; init; }
            public ColorF WorstGround { get; init; }
            public int GapPixels { get; init; }
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

            /// <summary>A scroller still moving (a smooth scroll or a fling in flight): the frame the lint read is mid-scroll.</summary>
            public bool ScrollMoving { get; init; }

            /// <summary>A root (<c>Parent</c> -1) of a `root=` capture starts from the cut its live ancestors put on it (<see cref="SeedScreen"/>,
            /// <see cref="SeedContent"/>: <see cref="InspectVisible.AncestorCut(FluentGpu.Scene.SceneStore, NodeHandle, in RectF, out RectF, out RectF, out bool)"/>),
            /// not the window; <see cref="SeedShown"/> says whether the viewport it sits in is on screen itself. A flag, not a sentinel: a cut
            /// opened along a horizontal scroller's axis is as wide as <see cref="RectF.Infinite"/>.</summary>
            public bool Seeded { get; init; }
            public RectF SeedScreen { get; init; } = RectF.Infinite;
            public RectF SeedContent { get; init; } = RectF.Infinite;
            public bool SeedShown { get; init; } = true;

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

            /// <summary>It handles a drag, a pointer press-and-move or a gesture (a splitter, a seek line): what makes a thin target a drag bar.</summary>
            public bool Drag { get; init; }

            /// <summary>How far the engine's hit test reaches past the box (its own <c>HitSlop</c> plus the host's touch floor), per side.</summary>
            public Edges4 Slop { get; init; }

            /// <summary>Where a scroll bar's lane starts inside the slop, per side, measured out from the box along its middle line (a press there is
            /// the bar's before any target's): read at capture from the engine's <c>InputDispatcher.IsOverScrollbarLane</c>, so a replay cuts the slop
            /// the same way. -1: no lane within the slop on that side (the top is never walked).</summary>
            public Edges4 Lane { get; init; } = NoLane;

            /// <summary>It carries a press handler (click, pointer, press, drag, gesture or context): a role alone (a pager's arrow on its first
            /// page, drawn but handler-less) takes no press and is no target.</summary>
            public bool Pressable { get; init; } = true;

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

            /// <summary>A drawn visual beyond a box, a text run or an image (<c>Path</c>, <c>PolylineStroke</c>, <c>IconLayer</c>, <c>TabShape</c>,
            /// <c>Video</c>, <c>ListRow</c>, <c>Series</c>, <c>Sprites</c>, an SDF <c>Arc</c>, a <c>BorderBrush</c>, an <c>Acrylic</c> material): it
            /// paints though no fill, image or text says so (a progress ring's arc). "" for none. Read by the rules that ask whether a node draws anything at all (<c>target.ink</c>, <c>space.dead</c>).</summary>
            public string Visual { get; init; } = "";

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

            /// <summary>The part of <see cref="Rect"/> scrolling reaches (cut by what scrolling cannot open): what a page audit judges.</summary>
            public RectF Reach { get; init; }

            /// <summary>The share of the viewport it sits in that its visible part fills (a container taller than its screen still fills it).</summary>
            public float Cover { get; init; }

            /// <summary>A modal scrim painted after it covers it (a dialog is open over the page): out of scope but at <c>scope=all</c>.</summary>
            public bool Covered { get; init; }

            /// <summary>The largest share of its visible rect one opaque fill painted after it covers (a menu's plate, a dialog's card, a pinned
            /// header; not its ancestors). At half or more it is not on screen (its visible rect is empty); under it, its ground may be the overlay's.</summary>
            public float Occluded { get; init; }

            /// <summary>It or an ancestor carries live animation tracks (a page fading in or out): its colour and place are mid-flight.</summary>
            public bool Moving { get; init; }

            /// <summary>In the run's scope (<c>scope=screen</c>: at least half visible, or filling half its viewport; never covered).</summary>
            public bool InScope { get; init; } = true;

            /// <summary>The rect the run's scope judges: <see cref="Visible"/> (screen), <see cref="Reach"/> (content) or <see cref="Rect"/> (all).</summary>
            public RectF Seen { get; init; }

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

            /// <summary>The nearest key family repeated at least 3 times among the nodes scrolling reaches ("" when none; not the scope's nodes, so
            /// the scope and the scroll position never change it), its instance root and the child-ordinal path from it.</summary>
            public string Family { get; init; } = "";
            public int Instance { get; init; } = -1;
            public string CellPath { get; init; } = "";

            /// <summary>How many instances of <see cref="Family"/> carry a node at <see cref="CellPath"/> (scrolling reaches them): 3 or more makes it a
            /// REPEATED cell (a caption per card); fewer, a part only some instances have (one zone's eyebrow).</summary>
            public int CellCount { get; init; }

            /// <summary>What a finding calls the node: its key, else its path.</summary>
            public string Ref => Key ?? Path;
        }

        /// <summary>A words run an exiting subtree still draws: the nearest key at or above it, its presented rect (window DIP), its words and the
        /// opacity that reaches the screen.</summary>
        public readonly record struct ExitingRun(string Key, RectF Rect, string Text, float Opacity);

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

            /// <summary>The active theme's designed top bleeds (<see cref="IThemeLintGuide.DesignedTopBleeds"/>): keys whose text may run its ink
            /// past the top of a clip.</summary>
            public IReadOnlyList<string> TopBleeds { get; init; } = [];

            /// <summary>The boxes of press targets that paint nothing (opacity 0: an auto-hidden scroll bar) but still take hits: a slop beside one is cut by it.</summary>
            public IReadOnlyList<RectF> HitBlockers { get; init; } = [];

            public string Theme { get; init; } = "";

            /// <summary>The id of the guide the edges and policies came from ("": the theme registered none).</summary>
            public string GuideId { get; init; } = "";

            /// <summary>The route of the page the capture SAW, as `status` names it (<c>album spotify:album:al2</c>): during a navigation, the page
            /// still on screen, not the one the shell is going to.</summary>
            public string Route { get; init; } = "";

            /// <summary>The route the shell is navigating to when it is not the page on screen yet ("" when they agree).</summary>
            public string Navigating { get; init; } = "";

            /// <summary>Where trims come from: <c>dwrite</c> (real glyphs) or <c>headless-model</c> (the 0.55 x size model: the text rules only review).</summary>
            public string TrimSource { get; init; } = "dwrite";

            /// <summary>The devices the mount renders on (`status` names them).</summary>
            public string Devices { get; init; } = "";

            /// <summary>The saved snapshot it was read from ("" when captured live).</summary>
            public string Source { get; init; } = "";

            /// <summary>The rendered frame the grounds under the text are read from; null when nothing was rendered.</summary>
            public Pixels? Backdrop { get; init; }

            /// <summary>The overlays a `state` verb opened that are still open (<c>overlay.open</c>); empty in a saved snapshot.</summary>
            public IReadOnlyList<StagedOverlay> StagedOverlays { get; init; } = [];

            /// <summary>The player's phase as `play` names it (<c>playing</c>, <c>paused</c>, <c>idle</c>, <c>loading</c>, <c>ended</c>; "" when not
            /// read) and the title of what it holds ("" when nothing): what <c>playing.status</c> holds the now-playing surfaces to.</summary>
            public string Playing { get; init; } = "";
            public string PlayingTitle { get; init; } = "";

            /// <summary>The player plays from a context the source line can name (a playlist, an album: <c>Queue.ContextName</c> answers). A
            /// context-less play (a lone track) has nothing to say there, so an empty source line is then no finding.</summary>
            public bool PlayingFromContext { get; init; }

            /// <summary>The inspector holds the transport chrome shown (`chrome=shown`): a stage or a video's transport must be on screen.</summary>
            public bool ChromeHeld { get; init; }

            /// <summary>The page band (DIP, at the bottom of the content host) the active theme RESERVES for the floating video card while it sits
            /// at its home: <c>Video.FloatingSurfaceReserve</c>, which the card writes from the theme's <c>PanelPolicy.Pip</c> (a theme whose card
            /// floats over the page, <c>PipBounds.OverlaysContent</c>, reserves 0). Classic's 0.2.9 design ends the page above the card on
            /// purpose; <c>picture.unpainted</c> does not call that band a page that stopped painting. 0 when nothing is reserved, and in a
            /// snapshot saved before the field existed.</summary>
            public float Reserved { get; init; }

            /// <summary>The text runs of subtrees on their way out (an exit animation still drawing them: the outgoing lyric line of a crossfade).
            /// The node list leaves exiting subtrees out (they are not the page); <c>text.copies</c> reads these. Not saved by `save=`.</summary>
            public IReadOnlyList<ExitingRun> ExitingText { get; init; } = [];

            /// <summary>The right edges the guide declares at Standard, moved by how far this tier's left lead sits from Standard's (<c>edge.huge</c>):
            /// filled at Huge and Huge4K only, where the guide declares none.</summary>
            public IReadOnlyList<LintEdge> MirrorEdges { get; init; } = [];

            /// <summary>The scope <see cref="Prepare"/> derived the node fields for (null: not prepared).</summary>
            public InspectVisible.Scope? PreparedFor { get; init; }

            /// <summary>The modal scrim <see cref="Prepare"/> found (its ref; "" when none): the nodes it covers are out of scope.</summary>
            public string Modal { get; init; } = "";

            /// <summary>How many nodes the modal scrim covers.</summary>
            public int CoveredCount { get; init; }

            /// <summary>How many nodes an opaque fill painted after them hides (half or more of what shows of them).</summary>
            public int OccludedCount { get; init; }

            /// <summary>How many nodes sit in an animating subtree (themselves included).</summary>
            public int MovingCount { get; init; }

            /// <summary>The route's name (<c>album</c>): the fingerprint's route part.</summary>
            public string RouteName => RouteNameOf(Route);

            /// <summary>The page's own entity (<c>spotify:album:al2</c>), "" on a page about none.</summary>
            public string PageEntity => EntityIn(Route);

            public bool RealText => string.Equals(TrimSource, "dwrite", StringComparison.Ordinal);
        }

        /// <summary>The name part of a route as `status` names it (<c>album</c> of <c>album:spotify:album:al2</c>).</summary>
        public static string RouteNameOf(string route)
        {
            int cut = route.AsSpan().IndexOfAny(' ', ':');
            return cut > 0 ? route[..cut] : route;
        }

        /// <summary>A keep-alive page slot's key (<c>Shell.SlotKey</c>: <c>&lt;tab&gt;\x1f&lt;route&gt;\x1f&lt;arg&gt;</c>, the arg empty for a route not
        /// keyed by it): the route name and arg of the page it holds.</summary>
        public static bool TrySlotRoute(string key, out string name, out string arg)
        {
            name = arg = "";
            int first = key.IndexOf('\u001f');
            if (first <= 0) return false;
            int second = key.IndexOf('\u001f', first + 1);
            if (second <= first + 1 || key.IndexOf('\u001f', second + 1) >= 0) return false;
            for (int i = 0; i < first; i++)
                if (!char.IsAsciiDigit(key[i])) return false;
            name = key[(first + 1)..second];
            arg = key[(second + 1)..];
            return true;
        }

        // ── the rendered frame ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A rendered frame as tightly packed, top-down BGRA8 (premultiplied, as the offscreen surface captures it), with the pixels per DIP.</summary>
        public sealed partial class Pixels
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
            /// answer there: it does not read. An <paramref name="icon"/> is the exception: a glyph that fills its box makes the box's median its own
            /// ink, so when its box cannot be split it is judged on the ring around it (less what is ink-coloured there), the ground a user sees
            /// it on. False when the rect holds no pixel.</summary>
            public bool TryWorstContrast(RectF rect, ColorF ink, float opacity, float percentile, out double ratio, out ColorF worst, out int gapPixels,
                bool icon = false)
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
                bool ringed = separable ? picked.Count < MinGapPixels : icon;
                if (ringed)
                {
                    // A glyph that fills its box (an icon): its ground is the ring just outside the box, less any ink that reaches into it
                    // (unsplittable: less any pixel near the ink's own colour).
                    if (!separable) picked.Clear();
                    int ring = Math.Max(1, (int)MathF.Ceiling(RingDip * Scale));
                    int rx0 = Math.Max(0, x0 - ring), rx1 = Math.Min(Width, x1 + ring), ry0 = Math.Max(0, y0 - ring), ry1 = Math.Min(Height, y1 + ring);
                    for (int y = ry0; y < ry1; y++)
                        for (int x = rx0; x < rx1; x++)
                        {
                            if (x >= x0 && x < x1 && y >= y0 && y < y1) continue;
                            int i = (y * Width + x) * 4;
                            Rgb(i, out float r, out float g, out float b);
                            if (separable ? (0.2126f * r + 0.7152f * g + 0.0722f * b - median) / span > InkShare
                                : MathF.Max(MathF.Abs(r - ink.R), MathF.Max(MathF.Abs(g - ink.G), MathF.Abs(b - ink.B))) < InkSpan) continue;
                            picked.Add(i);
                        }
                }
                gapPixels = picked.Count;
                if ((!separable && !ringed) || picked.Count < MinGapPixels)
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

        /// <summary>One accepted finding of a baseline file (a JSON line): matched by fingerprint, never by message. A reason is mandatory.</summary>
        public sealed record BaselineEntry(string Fp, string Rule, string Reason, string By, string Date)
        {
            /// <summary>The rule id the entry speaks to: the fingerprint's first part (an entry's <c>rule</c> is for a reader).</summary>
            public string RuleId
            {
                get
                {
                    int bar = Fp.IndexOf('|');
                    return bar > 0 ? Fp[..bar] : Rule;
                }
            }
        }

        /// <summary>The outcomes a report lists by default: everything but the suppressed.</summary>
        public static readonly Outcome[] DefaultOutcomes = [Outcome.Fail, Outcome.Review, Outcome.Report];

        /// <summary>Every outcome (`outcomes=all`).</summary>
        public static readonly Outcome[] AllOutcomes = [Outcome.Fail, Outcome.Review, Outcome.Suppressed, Outcome.Report];

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

            /// <summary>A run whose own and ancestors' opacity multiply below this is faded on purpose (the stage's distance-faded lyric lines, chrome
            /// fading out) and is incidental text in the WCAG sense: the contrast rule skips it. Default 0.5.</summary>
            public float IncidentalOpacity { get; init; } = 0.5f;

            /// <summary>The most findings one rule lists.</summary>
            public int MaxPerRule { get; init; } = 100;

            /// <summary>`moving=judge`: judge the frame as it is, animating subtrees included (looping text, the stage's captions, which never
            /// settle): no node counts as mid-flight, so contrast and the geometry rules give verdicts instead of "still animating" reviews.</summary>
            public bool JudgeMoving { get; init; }

            /// <summary>Findings below this severity are left out of the report.</summary>
            public Severity Min { get; init; } = Severity.Info;

            /// <summary>The outcomes the report lists (default fail, review and report; every outcome is counted either way).</summary>
            public IReadOnlyCollection<Outcome> Outcomes { get; init; } = DefaultOutcomes;

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

            /// <summary>The members' visible rects (as many as <see cref="Keys"/>; a member off the screen: its rect): where to look, and what a
            /// marked render boxes.</summary>
            public IReadOnlyList<RectF> Rects { get; init; } = [];

            /// <summary>Every member's node index (not capped like <see cref="Keys"/>): what a guide <see cref="LintIntent.Allow"/> is tested
            /// against. Not in the reply.</summary>
            public IReadOnlyList<int> Nodes { get; init; } = [];

            /// <summary>The fingerprint: rule|theme|route|pattern|cause.</summary>
            public string Fp { get; init; } = "";

            /// <summary>The flaw belongs to its pattern wherever it shows (a shared component's hit area): the fingerprint's route part is
            /// <see cref="AnyRoute"/>, not the page's.</summary>
            public bool AnyRoute { get; init; }

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

            /// <summary>Every finding the rules that ran made, whatever `min` says: what a gate counts, so no listing option can silence it.</summary>
            public IReadOnlyList<Finding> Every { get; init; } = [];

            /// <summary>Fail findings of <paramref name="level"/> at <paramref name="severity"/> over <see cref="Every"/> (`min`, `outcomes` and
            /// `limit` choose what is listed, never this): the reply's <c>fails</c>, what `inspect.py --gate` counts.</summary>
            public int Fails(Level level, Severity severity)
            {
                int n = 0;
                foreach (Finding f in Every)
                    if (f.Outcome == Outcome.Fail && f.Level == level && f.Severity == severity) n++;
                return n;
            }

            /// <summary>The guide's edges that matched an in-scope node.</summary>
            public int EdgesMatched { get; init; }

            /// <summary>Nodes in scope.</summary>
            public int InScope { get; init; }

            /// <summary>Baseline entries that matched a finding.</summary>
            public int BaselineMatched { get; init; }

            /// <summary>Baseline entries whose rule ran (only those can match: <c>matched</c> under it means an accepted finding is gone).</summary>
            public int BaselineConsidered { get; init; }

            /// <summary>Baseline entries whose fingerprint an earlier entry already holds (the later ones never match).</summary>
            public int BaselineDuplicates { get; init; }

            static bool Verdict(Finding f) => f.Outcome is Outcome.Fail or Outcome.Report;

            /// <summary>Fail findings (and report lines) of a gate rule at <paramref name="severity"/>, at or above `min`, listed or not: the
            /// summary an agent gates on. The `outcomes` filter and `limit` choose what is listed, never what is counted.</summary>
            public int Count(Severity severity)
            {
                int n = 0;
                foreach (Finding f in All)
                    if (f.Severity == severity && Verdict(f) && f.Level == Level.Gate) n++;
                return n;
            }

            /// <summary>Fail findings (and report lines) of rule <paramref name="rule"/> (an id or what the report calls it) at
            /// <paramref name="severity"/>, listed or not.</summary>
            public int Count(string rule, Severity severity)
            {
                int n = 0;
                foreach (Finding f in All)
                    if (f.Severity == severity && Verdict(f) && (string.Equals(f.Rule, rule, StringComparison.Ordinal) || string.Equals(f.Id, rule, StringComparison.Ordinal))) n++;
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

        /// <summary>Run the enabled rules over <paramref name="snapshot"/>: prepare it for the scope, run each rule, fingerprint, apply the guide's
        /// generic intents and the baseline, attach the fix, collapse findings that share a fingerprint, then filter by `min`, the listed outcomes
        /// and the cap per reported rule name.</summary>
        public static Report Run(Snapshot snapshot, Options? options = null)
        {
            Options o = options ?? new Options();
            if (o.JudgeMoving)   // `moving=judge`: this frame is the verdict, whatever still animates
                snapshot = snapshot with { Nodes = snapshot.Nodes.Select(static n => n.Animating ? n with { Animating = false } : n).ToList() };
            Snapshot s = Prepare(snapshot, o.Scope);
            var all = new List<Finding>();
            var every = new List<Finding>();
            var checks = new Dictionary<string, int>(StringComparer.Ordinal);
            var baseline = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);
            int duplicates = 0, considered = 0;
            foreach (BaselineEntry e in o.Baseline)
            {
                if (!baseline.TryAdd(e.Fp, e)) duplicates++;
                else if (o.Runs(e.RuleId)) considered++;
            }
            var baselineHit = new HashSet<string>(StringComparer.Ordinal);
            var byName = new Dictionary<string, List<Finding>>(StringComparer.Ordinal);
            var names = new List<string>();
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
                    Rules.PictureBlank => BlankRule(s, found),
                    Rules.PictureUnpainted => UnpaintedRule(s, found),
                    Rules.OverlayOpen => OverlayOpenRule(s, found),
                    Rules.AlignInset => AlignInsetRule(s, found),
                    Rules.AlignBaseline => AlignBaselineRule(s, found),
                    Rules.SpaceScale => SpaceScaleRule(s, found),
                    Rules.TextPlaceholder => PlaceholderRule(s, found),
                    Rules.KeyHygiene => KeyHygieneRule(s, found),
                    Rules.TextDuplicate => DuplicateTextRule(s, o, found),
                    Rules.TargetInk => TargetInkRule(s, o, found),
                    Rules.SpaceDead => DeadSpaceRule(s, found),
                    Rules.StageOverlap => StageOverlapRule(s, found),
                    Rules.TextCopies => TextCopiesRule(s, found),
                    Rules.OverlayTranslucent => TranslucentRule(s, found),
                    Rules.OverlayNarrow => OverlayNarrowRule(s, found),
                    Rules.TextRuled => TextRuledRule(s, found),
                    Rules.EdgeGutter => GutterRule(s, found),
                    Rules.EdgeHuge => HugeEdgeRule(s, found),
                    Rules.TypeVoice => VoiceRule(s, found),
                    Rules.StateRest => StateRestRule(s, found),
                    Rules.PlayingStatus => PlayingStatusRule(s, found),
                    Rules.ControlRequired => ControlRequiredRule(s, found),
                    Rules.EdgeWindow => PastWindowRule(s, found),
                    Rules.SpaceHuge => HugeSpaceRule(s, found),
                    Rules.PictureText => UnpaintedTextRule(s, found),
                    _ => 0,
                };
                checks[spec.Id] = looked;
                if (spec.Id is Rules.EdgeLeft or Rules.EdgeRight) edgesMatched += looked;

                var finished = new List<Finding>(found.Count);
                foreach (Finding raw in found)
                {
                    Finding f = Finish(s, spec, raw);
                    if (f.Outcome is not (Outcome.Suppressed or Outcome.Report) && AllowedByPolicy(s, f, out LintPolicy? policy))
                        f = f with { Outcome = Outcome.Suppressed, SuppressedBy = "intent", Reason = policy!.Reason };
                    if (f.Outcome is not (Outcome.Suppressed or Outcome.Report) && baseline.TryGetValue(f.Fp, out BaselineEntry? entry))
                    {
                        f = f with { Outcome = Outcome.Suppressed, SuppressedBy = "baseline", Reason = entry.Reason + (entry.By.Length > 0 ? " (" + entry.By + (entry.Date.Length > 0 ? ", " + entry.Date : "") + ")" : "") };
                        baselineHit.Add(f.Fp);
                    }
                    finished.Add(f);
                }
                string name = ReportName(spec.Id);
                if (!byName.TryGetValue(name, out List<Finding>? bucket))
                {
                    byName[name] = bucket = [];
                    names.Add(name);
                }
                foreach (Finding f in Collapse(finished))
                {
                    every.Add(f);
                    if (f.Severity < o.Min) continue;
                    all.Add(f);
                    if (o.Lists(f.Outcome)) bucket.Add(f);
                }
            }
            // Per reported name (`clip` holds text.clip and overlap.sibling): errors first, then warnings, then the rest; fails before reviews
            // before suppressions; then the cap, once per name.
            var listed = new List<Finding>();
            var truncated = new List<string>();
            foreach (string name in names)
            {
                List<Finding> ordered = byName[name].OrderByDescending(static f => f.Severity).ThenBy(static f => f.Outcome).ToList();
                if (ordered.Count > o.MaxPerRule)
                {
                    ordered.RemoveRange(o.MaxPerRule, ordered.Count - o.MaxPerRule);
                    truncated.Add(name);
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
                Every = every,
                EdgesMatched = edgesMatched,
                InScope = inScope,
                BaselineMatched = baselineHit.Count,
                BaselineConsidered = considered,
                BaselineDuplicates = duplicates,
            };
        }

        /// <summary>Findings of one rule that share a fingerprint, an outcome and a severity become ONE finding (the same flaw on every row of a
        /// family: a trimmed caption per card, a clipped title per tile): the first one's message with how many more, every member counted.</summary>
        static List<Finding> Collapse(List<Finding> findings)
        {
            var order = new List<string>();
            var groups = new Dictionary<string, List<Finding>>(StringComparer.Ordinal);
            foreach (Finding f in findings)
            {
                string key = f.Fp + "\u001f" + ((int)f.Outcome).ToString(CultureInfo.InvariantCulture) + "\u001f" + ((int)f.Severity).ToString(CultureInfo.InvariantCulture);
                if (!groups.TryGetValue(key, out List<Finding>? list))
                {
                    groups[key] = list = [];
                    order.Add(key);
                }
                list.Add(f);
            }
            var result = new List<Finding>(order.Count);
            foreach (string key in order)
            {
                List<Finding> list = groups[key];
                if (list.Count == 1) { result.Add(list[0]); continue; }
                Finding first = list[0];
                var refs = new List<string>(6);
                var rects = new List<RectF>(6);
                var nodes = new List<int>();
                int count = 0;
                foreach (Finding f in list)
                {
                    count += f.Count;
                    nodes.AddRange(f.Nodes);
                    for (int k = 0; k < f.Keys.Count && refs.Count < 6; k++)
                    {
                        if (refs.Contains(f.Keys[k])) continue;
                        refs.Add(f.Keys[k]);
                        rects.Add(k < f.Rects.Count ? f.Rects[k] : default);
                    }
                }
                result.Add(first with
                {
                    Message = first.Message + $" (and {list.Count - 1} more like it)",
                    Keys = refs,
                    Rects = rects,
                    Nodes = nodes,
                    Count = count,
                });
            }
            return result;
        }

        /// <summary>The fingerprint's route part for chrome: a finding whose every member sits outside every page slot (the title bar, the
        /// band, the play bar, the nav pane, an overlay) is the same flaw on every route, so it fingerprints once, not once per route.</summary>
        public const string ShellRoute = "shell";

        /// <summary>The fingerprint's route part for a finding about a shared component (<see cref="Finding.AnyRoute"/>: a small target is its
        /// family's, the list header's sort column or the heart column on every page that shows the list): one fix, one fingerprint.</summary>
        public const string AnyRoute = "*";

        /// <summary>The run's half of a finding: id and alias, level (the spec's, or todo when the rule demoted this finding: target.size above
        /// Compact), fingerprint (chrome without the route: <see cref="ShellRoute"/>), fix.</summary>
        static Finding Finish(Snapshot s, RuleSpec spec, Finding f)
        {
            string pattern = f.Pattern.Length > 0 ? f.Pattern : PatternOfRefs(f.Keys);
            string cause = f.Cause;
            string fp = string.Join("|", spec.Id, s.Theme, InChrome(s, f) ? ShellRoute : f.AnyRoute ? AnyRoute : s.RouteName, pattern, cause);
            string fix = string.Equals(s.Theme, "classic", StringComparison.OrdinalIgnoreCase)
                ? "report; do not fix unless asked (Classic)"
                : f.Fix.Length > 0 ? f.Fix : FixFor(spec.Id, cause);
            return f with
            {
                Id = spec.Id,
                Rule = spec.Alias.Length > 0 ? spec.Alias : spec.Id,
                Level = f.Level == Level.Todo ? Level.Todo : spec.Level,
                Pattern = pattern,
                Fp = fp,
                Fix = fix,
            };
        }

        /// <summary>Every member of <paramref name="f"/> sits outside every page slot of a snapshot that holds one (<see cref="PageOf"/>): window
        /// chrome around the page. False for a finding without member nodes, and for a capture with no page slot (a `root=` lint).</summary>
        static bool InChrome(Snapshot s, Finding f)
        {
            if (f.Nodes.Count == 0) return false;
            bool anyPage = false;
            foreach (Node n in s.Nodes)
                if (n.Key is { } key && TrySlotRoute(key, out _, out _)) { anyPage = true; break; }
            if (!anyPage) return false;
            foreach (int i in f.Nodes)
                if (i < 0 || i >= s.Nodes.Count || PageOf(s, s.Nodes[i]) >= 0) return false;
            return true;
        }

        /// <summary>A guide <see cref="LintIntent.Allow"/> for the finding's rule covering EVERY member (the member's key or an ancestor's starts
        /// with the prefix, as every other intent is matched). A finding without member nodes is matched by its refs.</summary>
        static bool AllowedByPolicy(Snapshot s, Finding f, out LintPolicy? policy)
        {
            policy = null;
            foreach (LintPolicy p in s.Policies)
            {
                if (p.Intent != LintIntent.Allow || p.Prefix.Length == 0) continue;
                if (!string.Equals(p.Rule, f.Id, StringComparison.Ordinal) && !string.Equals(p.Rule, f.Rule, StringComparison.Ordinal)) continue;
                bool all = f.Nodes.Count > 0;
                foreach (int node in f.Nodes)
                    if (node < 0 || node >= s.Nodes.Count || !CoveredBy(s, s.Nodes[node], p.Prefix)) { all = false; break; }
                if (!all && f.Nodes.Count == 0)
                    foreach (string key in f.Keys)
                        if (key.StartsWith(p.Prefix, StringComparison.Ordinal)) { all = true; break; }
                if (all) { policy = p; return true; }
            }
            return false;
        }

        /// <summary>The key of <paramref name="n"/> or of an ancestor starts with <paramref name="prefix"/>: a policy covers it.</summary>
        static bool CoveredBy(Snapshot s, Node n, string prefix)
        {
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
                if (s.Nodes[i].Key is { } key && key.StartsWith(prefix, StringComparison.Ordinal)) return true;
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
            Rules.TextClip when cause.EndsWith("designed-bleed", StringComparison.Ordinal)
                => "designed: the theme sets this line flush with the edge above it (DesignedTopBleeds); nothing to do",
            Rules.TextClip when cause.EndsWith("ink-guess", StringComparison.Ordinal)
                => "look at the render: only the 1.2 em ink guess runs past the clip, the laid-out box fits; never shrink the type",
            Rules.TextClip when cause.EndsWith("line-box", StringComparison.Ordinal)
                => "look at the render: the cut lies inside the line box's leading or descent and this snapshot has no glyph ink; lint a live mount to judge it; never shrink the type",
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
                "designed-total" => "report: the designed ellipses on screen; nothing to do",
                _ => "designed ellipses: nothing to do",
            },
            Rules.TypeFloor => "use a type role at or above the floor; the role table decides sizes, not the view",
            Rules.TypeScale => "report: compare with the theme's type ramp; no change unless a style has no role",
            Rules.TargetSize => "grow the hit area (padding on the target, or hand the click to the row it sits in); keep the glyph size",
            Rules.DuplicateAction => "one home per verb (owner rule): find the verb's home on the board and ask before taking a control away; the feature stays",
            Rules.SpaceStack => cause switch
            {
                "overlap" => "the blocks overlap: give the stack a gap from the tier record or fix the line height",
                "touching" => "report: look at the render; where the ink of two blocks touches, take the gap from the tier record",
                _ => "report: the gaps of the stack; compare them with the board",
            },
            Rules.ContrastText => cause switch
            {
                "busy" => "text over art or a gradient: look at the render; a scrim or an on-media ink role fixes it, never a node-by-node colour",
                _ when cause.StartsWith("media:", StringComparison.Ordinal) => "text over a photograph or a wash that does not read: an adaptive scrim under it or the on-media ink role, never a node-by-node colour",
                "no-ground" => "look at the render: the frame gave no ground for these runs (cut by the window edge or a bar)",
                "moving" => "settle and lint again (frames 60): the frame was read mid-animation; the motion itself is not the finding",
                "covered" => "close the overlay and lint again, or look at the render: the ground read under these runs may be the overlay's",
                _ => "change the theme's ink or ground role for every run of this pair, never node by node",
            },
            Rules.AlignInset when cause.StartsWith("column:", StringComparison.Ordinal)
                => "put the lead on its column's x (drop the extra padding on a part with no rest fill), or give it the visible container (plate, frame) that justifies the inset",
            Rules.AlignInset => "move the section's extra lead into its plate (padding on the hover plate only) so its ink sits on the column; leave the column alone",
            Rules.AlignBaseline => "anchor the cell's first line at one offset in every row (AlignItems=Start, or reserve the optional line in every row)",
            Rules.SpaceScale => "take the value from Spacing.* or the theme's R.Space/tier record (or declare OffScaleOk with a reason)",
            Rules.TextPlaceholder => "a missing string or argument: add the loc key or pass the argument; never blank the text",
            Rules.PictureBlank => "look at the render: nothing painted (a surface with no picture, a page that never drew); find what should draw there, never call a blank frame done",
            Rules.PictureUnpainted => "look at the render: the region's nodes are laid out but never reach the screen (a layer or clip covers them, or a pass stops painting); fix the paint, never move the text",
            Rules.OverlayOpen => "close the overlay before judging the state (key esc), or stage the state a way that opens none; the page under it is not what the frame shows",
            Rules.KeyHygiene => "key the node by its entity or slot id, without tier, form, count or run-id parts (keys must be stable selectors)",
            Rules.TextDuplicate => "say it once: keep the heading and drop the repeat inside the section (or give the repeat its own words); the section stays",
            Rules.TargetInk => "give the target ink at rest (its label or glyph), or hand its click to a control a person can see; never remove the feature",
            Rules.SpaceDead => "give the empty slot its plate at rest (a placeholder fill or glyph), or declare the reservation in the guide (Allow space.dead, naming the board); never drop a reservation that keeps the row from jumping",
            _ => "",
        };

        // ── preparing a snapshot ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The key up to its first <c>#</c>, <c>|</c> or control character (a run id, a composite separator): what a family is read from.</summary>
        static string HeadOf(string key)
        {
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c is '#' or '|' || c < ' ') return i > 0 ? key[..i] : key;
            }
            return key;
        }

        /// <summary>A static key segment: a lowercase slug word (<c>link</c>, <c>section</c>, <c>see-all</c>), not an id or a name.</summary>
        static bool IsSlug(string segment)
        {
            if (segment.Length is 0 or > 24 || !char.IsAsciiLetterLower(segment[0])) return false;
            foreach (char c in segment)
                if (!(char.IsAsciiLetterLower(c) || c is '-' or '_' or '.')) return false;
            return true;
        }

        /// <summary>The key's family: the static part the members of a repeated family share. Read by <c>:</c>-segments of <see cref="HeadOf"/>:
        /// the first segment heads it; it stops before the first segment that is an id or a name (a digit, a capital, a space, longer than 24) or
        /// <c>spotify</c> (an entity id follows), and, in a key of three or more segments, before the last one (the slot). So
        /// <c>zune-history:spotify:track:x</c> is <c>zune-history</c>, <c>zs:link:privacy</c> is <c>zs:link</c>, <c>settings:section:Links</c>
        /// is <c>settings:section</c>, an entity key <c>spotify:page:x</c> is <c>spotify:page</c>, while <c>zs:title</c> and <c>zs:body</c>
        /// (parts of one page, not slots of one family) stay two families. <c>c.title</c> stays itself.</summary>
        public static string FamilyOfKey(string key)
        {
            string head = HeadOf(key);
            string[] segments = head.Split(':');
            if (segments.Length == 1 || segments[0].Length == 0) return head;
            if (segments[0] == "spotify") return segments.Length >= 3 ? "spotify:" + segments[1] : head;
            int take = 1;
            for (int j = 1; j < segments.Length; j++)
            {
                if (segments[j] == "spotify" || !IsSlug(segments[j])) break;
                if (j == segments.Length - 1 && segments.Length >= 3) break;
                take = j + 1;
            }
            return string.Join(':', segments, 0, take);
        }

        /// <summary>The key's root: the key up to its first <c>:</c>, <c>#</c>, <c>|</c> or control character (<c>trail:moreby</c> and
        /// <c>trail:similar</c> are both <c>trail</c>). A verb's HOME for <c>duplicate.action</c>: the sections of one trail are one home.</summary>
        public static string RootOfKey(string key)
        {
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c is ':' or '#' or '|' || c < ' ') return i > 0 ? key[..i] : key;
            }
            return key;
        }

        /// <summary>The key's static words: its <c>:</c>-segments up to the first name (a capital, a space, a character outside a slug) or
        /// <c>spotify</c>. What a person typed into the view, not what the content brought (<c>zune-cluster:0:Small Black</c>: <c>zune-cluster:0</c>).</summary>
        public static string SlugOf(string key)
        {
            string head = HeadOf(key);
            string[] segments = head.Split(':');
            int take = 1;
            for (int j = 1; j < segments.Length; j++)
            {
                string seg = segments[j];
                bool slug = seg.Length > 0 && seg != "spotify";
                foreach (char c in seg)
                    if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.')) { slug = false; break; }
                if (!slug) break;
                take = j + 1;
            }
            return string.Join(':', segments, 0, take);
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
            var reach = new RectF[count];
            var cover = new float[count];
            InspectVisible.Compute(nodes, s.Width, s.Height, visible, onScreen, inContent, reach, cover);

            // A modal scrim: an interactive fill over the whole window (a dialog's light-dismiss backdrop). It catches every click and dims
            // what is under it, so whatever was painted before it, its own ancestors aside, is covered: out of scope at screen and content.
            var covered = new bool[count];
            int scrim = -1;
            float windowArea = s.Width * s.Height;
            for (int i = count - 1; i >= 0 && scrim < 0; i--)
            {
                Node n = nodes[i];
                if (n.Interactive && n.Text is null && n.Fill.A * n.Opacity >= 0.04f && visible[i].W * visible[i].H >= 0.95f * windowArea) scrim = i;
            }
            int coveredCount = 0;
            if (scrim >= 0)
            {
                var above = new bool[count];
                for (int a = nodes[scrim].Parent; a >= 0; a = nodes[a].Parent) above[a] = true;
                for (int i = 0; i < scrim; i++)
                {
                    if (above[i]) continue;
                    covered[i] = true;
                    coveredCount++;
                    visible[i] = default;
                    reach[i] = default;
                    onScreen[i] = inContent[i] = cover[i] = 0f;
                }
            }

            // Overlays: an opaque fill (not text) paints over every node earlier in paint order that is not its ancestor: a menu's plate over
            // the page, a dialog's card, a header pinned over rows. A node an occluder hides by half or more of its visible rect is not on screen
            // (scrolling or dismissing reveals it, so `content` still reaches it); under half, its runs may read the overlay as their ground.
            var last = new int[count];   // the last index of each node's subtree (depth-first: a subtree is a run of indices)
            for (int i = 0; i < count; i++) last[i] = i;
            for (int i = count - 1; i >= 0; i--)
            {
                int p = nodes[i].Parent;
                if (p >= 0 && last[i] > last[p]) last[p] = last[i];
            }
            var occluded = new float[count];
            var shown = new List<int>();
            for (int x = 0; x < count; x++)
            {
                Node o = nodes[x];
                RectF ov = visible[x];
                bool occluder = o.Text is null && !o.Image && o.Fill.A * o.Opacity >= 0.98f && ov.W >= 4f && ov.H >= 4f;
                if (occluder)
                    foreach (int i in shown)
                    {
                        if (last[i] >= x) continue;   // an ancestor: the fill paints inside it, not over it
                        RectF v = visible[i];
                        RectF both = v.Intersect(ov);
                        if (both.IsEmpty) continue;
                        float share = both.W * both.H / (v.W * v.H);
                        if (share > occluded[i]) occluded[i] = share;
                    }
                if (!visible[x].IsEmpty) shown.Add(x);
            }
            int occludedCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (occluded[i] < MinOccluded || covered[i]) continue;
                occludedCount++;
                visible[i] = default;
                onScreen[i] = cover[i] = 0f;
            }

            var inScope = new bool[count];
            var ordinal = new int[count];
            var childCount = new int[count];
            var region = new int[count];
            var inRail = new bool[count];
            var ink = new bool[count];
            var inkCount = new int[count];
            var keyed = new int[count];   // the nearest keyed node, itself included (-1: none)
            var moving = new bool[count];
            int movingCount = 0;
            for (int i = 0; i < count; i++)
            {
                Node n = nodes[i];
                int p = n.Parent;
                moving[i] = n.Animating || (p >= 0 && moving[p]);
                if (moving[i]) movingCount++;
                inScope[i] = InspectVisible.InScope(scope, onScreen[i], inContent[i], cover[i]);
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

            // Families: keyed nodes that scrolling reaches (at least half of them, no modal over them), counted per key family, whatever the scope
            // and wherever the page is scrolled; a family seen 3 times or more is a repeated one, and each such keyed node is an instance root. A
            // node belongs to its nearest instance root (itself included).
            var familyCount = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
                if (!covered[i] && InspectVisible.InScope(InspectVisible.Scope.Content, 0f, inContent[i]) && nodes[i].Key is { Length: > 0 } key)
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
            // covers 90 % of it and is larger; a ground that covers the window is the page itself. A node off the screen that scrolling reaches is
            // matched the same way on reach rects (the column line of align.inset is read over the whole scroll content).
            var backdrop = new int[count];
            Array.Fill(backdrop, -1);
            for (int pass = 0; pass < 2; pass++)
            {
                RectF[] rects = pass == 0 ? visible : reach;
                var order = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    if (!ink[i] || rects[i].IsEmpty) continue;
                    if (pass == 0 || visible[i].IsEmpty)
                    {
                        RectF v = rects[i];
                        float area = v.W * v.H;
                        for (int k = order.Count - 1; k >= 0; k--)
                        {
                            int m = order[k];
                            if (nodes[m].Text is not null) continue;
                            RectF mv = rects[m];
                            float marea = mv.W * mv.H;
                            RectF both = mv.Intersect(v);
                            if (both.W * both.H >= 0.9f * area && marea > area * 1.05f)
                            {
                                backdrop[i] = visible[m].W * visible[m].H >= 0.95f * windowArea ? -1 : m;
                                break;
                            }
                        }
                    }
                    order.Add(i);
                }
            }

            // Cell paths, and how many instances of a family carry a node at one (counted over what scrolling reaches, as the families are): a
            // cell three instances carry is a REPEATED cell (a caption per card), one only some instance carries is that instance's own.
            var path = new List<int>(16);
            var cells = new string[count];
            var cellInstances = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int root = instance[i];
                string cell = "";
                if (root >= 0 && root != i)
                {
                    path.Clear();
                    for (int j = i; j != root && j >= 0; j = nodes[j].Parent) path.Add(ordinal[j]);
                    path.Reverse();
                    cell = string.Join("/", path);
                }
                cells[i] = cell;
                if (root >= 0 && !covered[i] && InspectVisible.InScope(InspectVisible.Scope.Content, 0f, inContent[i]))
                {
                    string at = FamilyOfKey(nodes[root].Key!) + "/" + cell;
                    if (!cellInstances.TryGetValue(at, out HashSet<int>? set)) cellInstances[at] = set = [];
                    set.Add(root);
                }
            }
            var prepared = new Node[count];
            for (int i = 0; i < count; i++)
            {
                Node n = nodes[i];
                int at = keyed[i];
                string pattern = at >= 0 ? PatternOfKey(nodes[at].Key!) : "#root";
                int root = instance[i];
                string cell = cells[i];
                string family = root >= 0 ? FamilyOfKey(nodes[root].Key!) : "";
                int cellCount = root >= 0 && cellInstances.TryGetValue(family + "/" + cell, out HashSet<int>? carriers) ? carriers.Count : 0;
                TextRun? text = n.Text;
                if (text is not null && s.Backdrop is { } px && text.Ground.A <= 0f) text = WithGround(n, visible[i], text, px);
                prepared[i] = n with
                {
                    Visible = visible[i],
                    OnScreen = onScreen[i],
                    InContent = inContent[i],
                    Reach = reach[i],
                    Cover = cover[i],
                    Covered = covered[i],
                    Occluded = occluded[i],
                    Moving = moving[i],
                    InScope = inScope[i],
                    Seen = scope switch { InspectVisible.Scope.Screen => visible[i], InspectVisible.Scope.Content => reach[i], _ => n.Rect },
                    Ink = ink[i],
                    InkCount = inkCount[i],
                    Region = region[i],
                    InRail = inRail[i],
                    Backdrop = backdrop[i],
                    Pattern = pattern,
                    Family = family,
                    Instance = root,
                    CellPath = cell,
                    CellCount = cellCount,
                    Text = text,
                };
            }
            return s with
            {
                Nodes = prepared,
                PreparedFor = scope,
                Modal = scrim >= 0 ? nodes[scrim].Ref : "",
                CoveredCount = coveredCount,
                OccludedCount = occludedCount,
                MovingCount = movingCount,
            };
        }

        /// <summary>The share of a node's visible rect an opaque fill painted after it must cover before it is not on screen.</summary>
        public const float MinOccluded = 0.5f;

        /// <summary>The colour most of the VISIBLE part of a text run's box is rendered in (width capped at its natural width + 2), and its share:
        /// a run cut by a scroller, a sticky header or a bar is read where it shows, never over what paints on its cut part. A run whose ink fills
        /// its own box (a heart, a solid glyph) is read again without its ink colour. Nothing when under 2 x 2 DIP of it shows.</summary>
        static TextRun WithGround(Node n, RectF visible, TextRun t, Pixels px)
        {
            float w = t.NaturalW > 0f ? MathF.Min(n.Rect.W, t.NaturalW + 2f) : n.Rect.W;
            RectF seen = visible.Intersect(new RectF(n.Rect.X, n.Rect.Y, w, n.Rect.H));
            if (seen.W < MathF.Min(2f, w) || seen.H < MathF.Min(2f, n.Rect.H)) return t;
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
            // Still busy (a photograph, a scrim, a gradient): read the ground around the glyphs at its worst, so the rule can judge it.
            if (share < BusyShare && t.Color.A > 0f
                && px.TryWorstContrast(seen, t.Color, n.Opacity, MediaPercentile, out double worst, out ColorF worstGround, out int gaps, icon: t.Glyph))
                return t with { Ground = ground, GroundShare = share, Worst = (float)worst, WorstGround = worstGround, GapPixels = gaps };
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

        /// <summary>A run as a message names it: its words in quotes, an icon glyph by its name (<c>glyph:Play</c>, <c>glyph:U+E8FF</c> when it has
        /// none), a span run as <c>spans</c>.</summary>
        static string TextName(TextRun t) => t.Glyph ? GlyphRef(t.String, t.GlyphName) : t.Spans && t.String.Length == 0 ? "spans" : Q(t.String);

        /// <summary>A glyph as a message names it: <c>glyph:Play</c>, or its code points when the icon table has no name for it.</summary>
        static string GlyphRef(string text, string name)
            => "glyph:" + (name.Length > 0 ? name : string.Join("+", text.Select(static c => "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture))));

        /// <summary>Where a finding points at a member: its visible rect, or its rect when none of it is on screen (`scope=content` or `all`).</summary>
        static RectF Where(Node n) => n.Visible.IsEmpty ? n.Rect : n.Visible;

        /// <summary>Up to <paramref name="cap"/> refs and rects (<see cref="Where"/>) of <paramref name="members"/>.</summary>
        static (List<string> Refs, List<RectF> Rects) Members(IEnumerable<Node> members, int cap = 6)
        {
            var refs = new List<string>(cap);
            var rects = new List<RectF>(cap);
            foreach (Node n in members)
            {
                if (refs.Count >= cap) break;
                refs.Add(n.Ref);
                rects.Add(Where(n));
            }
            return (refs, rects);
        }

        /// <summary>Every member's node index (<see cref="Finding.Nodes"/>).</summary>
        static int[] Ids(IEnumerable<Node> members) => members.Select(static n => n.Index).ToArray();

        static string Plural(int n, string one, string many) => n == 1 ? one : many;

        // ── edge.left and edge.right (gutter, right-edge): a theme's edge expectations ─────────────────────────────

        /// <summary>The node matches the edge's keys, prefixes, <c>UnderKey</c> and <c>GlyphsOnly</c>, and the edge it measures is IN SCOPE: on
        /// screen and not cut (a page column taller than the screen still shows its left edge; a header scrolled half under a sticky bar does not
        /// count once its left edge is cut away). Not its share of itself: an edge is a line, not an area.</summary>
        static bool EdgeMatches(Snapshot s, Node n, LintEdge e)
        {
            if (n.Covered || n.Moving || n.Rect.W <= 0f || n.Rect.H <= 0f) return false;   // mid-flight: its x is not where it lands
            if (n.Rect.Right <= 0f || n.Rect.X >= s.Width) return false;   // off the window: scrolled away, not an edge
            RectF seen = n.Seen;
            if (seen.IsEmpty) return false;
            if (e.Side == LintSide.Left ? MathF.Abs(seen.X - n.Rect.X) > Slack : MathF.Abs(seen.Right - n.Rect.Right) > Slack) return false;
            return EdgeKeyed(s, n, e);
        }

        /// <summary>The node matches the edge's keys, prefixes, <c>UnderKey</c> and <c>GlyphsOnly</c>, wherever it is.</summary>
        static bool EdgeKeyed(Snapshot s, Node n, LintEdge e)
        {
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

        /// <summary>An overlay hides the node: a modal scrim, or an opaque fill painted after it over half or more of what shows of it (a menu's
        /// plate, the search suggestions' card).</summary>
        static bool Hidden(Node n) => n.Covered || n.Occluded >= MinOccluded;

        /// <summary>For each of the guide's edges of the rule: find the leftmost (or rightmost) matching node IN SCOPE (a parked keep-alive copy or a
        /// node scrolled away is never picked) and compare its edge. When the FIRST match on the window is covered by an overlay (the search
        /// suggestions over the band's first pivot at Compact), the edge is not checked: the next match is another node, not the edge (the
        /// first pivot's x is the edge, the second's is a pivot's width further). Returns the edges that matched; an edge nothing matches is not
        /// checked.</summary>
        static int EdgeRule(Snapshot s, string id, List<Finding> into)
        {
            string guideRule = id == Rules.EdgeLeft ? Rules.Gutter : Rules.RightEdge;
            int looked = 0;
            foreach (LintEdge e in s.Edges)
            {
                if (!string.Equals(e.Rule, guideRule, StringComparison.Ordinal)) continue;
                bool left = e.Side == LintSide.Left;
                Node? pick = null, covered = null;
                float at = 0f, coveredAt = 0f;
                foreach (Node n in s.Nodes)
                {
                    if (EdgeMatches(s, n, e))
                    {
                        float x = left ? n.Rect.X : n.Rect.Right;
                        if (pick is null || (left ? x < at : x > at)) { pick = n; at = x; }
                    }
                    else if (Hidden(n) && !n.Moving && n.Rect.W > 0f && n.Rect.H > 0f && n.Rect.Right > 0f && n.Rect.X < s.Width && EdgeKeyed(s, n, e))
                    {
                        float x = left ? n.Rect.X : n.Rect.Right;
                        if (covered is null || (left ? x < coveredAt : x > coveredAt)) { covered = n; coveredAt = x; }
                    }
                }
                if (pick is null) continue;
                if (covered is not null && (left ? coveredAt < at - Slack : coveredAt > at + Slack)) continue;   // the first match is under an overlay
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
                    Rects = [Where(pick)],
                    Nodes = [pick.Index],
                });
            }
            return looked;
        }

        // ── text.clip and overlap.sibling (clip): text cut by an ancestor, and siblings that overlap ─────────────

        /// <summary>The share of an em a line box holds outside its glyphs' ink, at most, on either side (the internal leading over the caps,
        /// the descent under a baseline with no descenders): without glyph ink, a cut no deeper than this is a review, never a verdict.</summary>
        public const float LeadingEm = 0.25f;

        /// <summary>The box the ink of a text node needs. With the glyphs' ink known (<see cref="TextRun.HasInk"/>): the box's x extent and the
        /// ink's y extent (<paramref name="guess"/> 0), so a cut through the line box's leading or descent, which no glyph fills, is no cut.
        /// Without it: the laid-out box and the natural height of an unwrapped run that wants more (both measured), widened by
        /// <paramref name="guess"/> above and below: the ink a tight explicit line height leaves outside its line box, a guess
        /// (<see cref="InkEm"/> em of ink, not the glyphs' own metrics).</summary>
        static RectF InkBox(Node n, TextRun t, out float guess)
        {
            RectF r = n.Rect;
            if (t.HasInk)
            {
                guess = 0f;
                return new RectF(r.X, r.Y + t.InkTop, r.W, t.InkBottom - t.InkTop);
            }
            float h = r.H;
            if (!t.Wraps && t.NaturalH > h) h = t.NaturalH;
            guess = t.LineHeight > 0f ? MathF.Max(0f, (InkEm * t.Size - t.LineHeight) / 2f) : 0f;
            return new RectF(r.X, r.Y - guess, r.W, h + 2f * guess);
        }

        /// <summary>Why a vertical cut is under review, "" when it is a verdict: the headless text model; or, without the glyphs' ink, a cut only
        /// the ink guess makes (the laid-out box fits; display type with a tight line height, whose real ascenders may well sit inside) or one
        /// inside the line box's leading or descent (<see cref="LeadingEm"/>), which glyphs need not fill. With the ink known it is a verdict.</summary>
        static string ClipDoubt(Snapshot s, TextRun t, float over, float guess)
            => !s.RealText ? "headless text model: the ink box is not real glyph metrics"
             : t.HasInk ? ""
             : over - guess <= Slack ? "ink guess, not glyph metrics: the laid-out box fits the clip; only the 1.2 em ink guess runs past it"
             : over - guess <= LeadingEm * t.Size ? "no glyph ink in this snapshot: the cut lies inside the line box's leading or descent (under 0.25 em), which the glyphs need not fill"
             : "";

        /// <summary>The cause suffix of a vertical cut under review on real text: <c>:ink-guess</c> (only the 1.2 em guess runs past) or
        /// <c>:line-box</c> (inside the line box's leading or descent, no glyph ink read); "" for a verdict or the headless model.</summary>
        static string DoubtCause(Snapshot s, string doubt, float over, float guess)
            => !s.RealText || doubt.Length == 0 ? "" : over - guess <= Slack ? ":ink-guess" : ":line-box";

        static bool Fades(Node n, EdgeMask edge) => (n.FadeEdges & (byte)edge) != 0;

        /// <summary>A cut at a scroller's edge on its own axis, or null when it is no finding: scrolling still reveals the text there (the fold of a
        /// list, a vertical scroller only) or the edge feathers (an edge fade). A guide <see cref="LintIntent.Bleed"/> makes it an intended one.</summary>
        static Finding? ScrollEdgeCut(Snapshot s, Node n, TextRun t, Node scroller, bool vertical, bool start, float over, float guess)
        {
            EdgeMask edge = vertical ? (start ? EdgeMask.Top : EdgeMask.Bottom) : (start ? EdgeMask.Left : EdgeMask.Right);
            bool reveals = start ? scroller.ScrollOffset > Slack : scroller.ScrollOffset < scroller.ScrollMax - Slack;
            if (Fades(scroller, edge) || (vertical && reveals)) return null;
            string side = edge switch { EdgeMask.Top => "top", EdgeMask.Bottom => "bottom", EdgeMask.Left => "left", _ => "right" };
            string doubt = vertical ? ClipDoubt(s, t, over, guess) : s.RealText ? "" : ClipDoubt(s, t, over, 0f);
            var f = new Finding(Rules.TextClip, vertical ? Severity.Error : Severity.Warn,
                $"text {TextName(t)} is cut by {scroller.Ref}'s {side} scroll edge, which has no edge fade: {F(over)} past it", [n.Ref, scroller.Ref])
            {
                Measured = [M("over", over), M("inkGuess", vertical ? guess : 0f), M("glyphInk", t.HasInk ? 1 : 0), M("scrollOffset", scroller.ScrollOffset), M("scrollMax", scroller.ScrollMax)],
                Expected = [M("over", 0)],
                Pattern = PatternOfKey(scroller.Key ?? scroller.Pattern),
                Cause = "scroll-edge:" + side + (vertical ? DoubtCause(s, doubt, over, guess) : ""),
                Rects = [Where(n), Where(scroller)],
                Nodes = [n.Index],
                Outcome = doubt.Length > 0 ? Outcome.Review : Outcome.Fail,
                Reason = doubt,
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
                RectF ink = InkBox(n, t, out float guess);
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
                        if (over > Slack && ScrollEdgeCut(s, n, t, anc, vertical, vertical ? above : leftSide, over, guess) is { } cut)
                        {
                            into.Add(cut);
                            break;
                        }
                    }
                    if (vOver > Slack)
                    {
                        // A box whose cut side feathers dissolves the text there by design, as on the horizontal axis below (audit B3bc: the Zune
                        // Artist layout's name sinks into the stage's foot under a fade, Z-NowPlaying-Artist); a hard cut still fails.
                        if (Fades(anc, above ? EdgeMask.Top : EdgeMask.Bottom)) break;
                        string doubt = ClipDoubt(s, t, vOver, guess);
                        // A theme's designed top bleed (IThemeLintGuide.DesignedTopBleeds: Zune's panorama voice on its exact line, flush with the edge
                        // above it) is an intended cut at the top; a cut at the bottom (descenders) still fails.
                        bool designed = above && s.TopBleeds.Count > 0 && UnderAny(nodes, n, s.TopBleeds);
                        into.Add(new Finding(Rules.TextClip, Severity.Error,
                            $"text {Q(t.String)} is clipped by {anc.Ref}: its {(t.HasInk ? "glyph ink" : "ink")} runs {F(vOver)} past the {(above ? "top" : "bottom")} ({F(ink.Y)}..{F(ink.Bottom)} in {F(c.Y)}..{F(c.Bottom)}){(designed ? " (a designed bleed: the theme sets this line flush with the edge)" : "")}",
                            [n.Ref, anc.Ref])
                        {
                            Measured = [M("inkTop", ink.Y), M("inkBottom", ink.Bottom), M("over", vOver), M("inkGuess", guess), M("glyphInk", t.HasInk ? 1 : 0), M("size", t.Size), M("lineHeight", t.LineHeight)],
                            Expected = [M("clipTop", c.Y), M("clipBottom", c.Bottom)],
                            Pattern = PatternOfKey(anc.Key ?? anc.Pattern),
                            Cause = designed ? "top:designed-bleed" : (above ? "top" : "bottom") + DoubtCause(s, doubt, vOver, guess),
                            Rects = [Where(n), Where(anc)],
                            Nodes = [n.Index],
                            Outcome = designed ? Outcome.Suppressed : doubt.Length > 0 ? Outcome.Review : Outcome.Fail,
                            SuppressedBy = designed ? "intent" : "",
                            Reason = designed ? "a designed top bleed (the guide's DesignedTopBleeds): the line sits flush with the edge above it" : doubt,
                        });
                        break;
                    }
                    if (hOver > Slack)
                    {
                        // A box whose cut side feathers (an EdgeFade: a marquee's overflow cue, a title that bleeds out under a fade) dissolves the
                        // text there by design, as a scroll edge with a fade does: no finding (audit B3: the stage's marquee and Large art titles).
                        if (Fades(anc, leftSide ? EdgeMask.Left : EdgeMask.Right)) break;
                        into.Add(new Finding(Rules.TextClip, Severity.Warn,
                            $"text {Q(t.String)} is clipped by {anc.Ref}: it runs {F(hOver)} past the {(leftSide ? "left" : "right")} edge ({F(ink.X)}..{F(ink.Right)} in {F(c.X)}..{F(c.Right)})",
                            [n.Ref, anc.Ref])
                        {
                            Measured = [M("inkLeft", ink.X), M("inkRight", ink.Right), M("over", hOver)],
                            Expected = [M("clipLeft", c.X), M("clipRight", c.Right)],
                            Pattern = PatternOfKey(anc.Key ?? anc.Pattern),
                            Cause = leftSide ? "left" : "right",
                            Rects = [Where(n), Where(anc)],
                            Nodes = [n.Index],
                            Outcome = s.RealText ? Outcome.Fail : Outcome.Review,
                            Reason = s.RealText ? "" : "headless text model: the ink box is not real glyph metrics",
                        });
                        break;
                    }
                }
            }
            return looked;
        }

        /// <summary>The node or an ancestor carries one of <paramref name="keys"/>.</summary>
        static bool UnderAny(IReadOnlyList<Node> nodes, Node n, IReadOnlyList<string> keys)
        {
            for (int a = n.Index; a >= 0; a = nodes[a].Parent)
                if (nodes[a].Key is { } k)
                    foreach (string key in keys)
                        if (string.Equals(k, key, StringComparison.Ordinal)) return true;
            return false;
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
                            Rects = [Where(a), Where(b)],
                            Nodes = [a.Index, b.Index],
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
        /// never (a guide <see cref="LintIntent.NeverTrim"/>: error), overflow (<c>Trim=None</c>: the words spill, error), near-miss (warn),
        /// hard-clip (<c>Trim=Clip</c>: warn, whatever the family says: the boards draw an ellipsis), designed (an ELLIPSIS under a guide
        /// <see cref="LintIntent.TrimOk"/>, an <c>allowTrim</c> prefix, or MaxLines plus an ellipsis in a REPEATED CELL, <see cref="Node.CellCount"/>:
        /// the same cell in 3 instances of a key family, a caption per card, not any text under a page section that happens to repeat; warn,
        /// suppressed, and one report line counts them), single (a lone ellipsis: review). Under the headless text model only a designed
        /// ellipsis keeps its class (the intent holds whatever the model measures); the rest is model (review).</summary>
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
                string mode = t.Trim switch { "None" => "overflowing", "Clip" => "hard-clipped", _ => "ellipsised" };
                float over = Overrun(n, t);
                string cls;
                Severity sev;
                Outcome outcome = Outcome.Fail;
                string reason = "";
                LintPolicy? never = PolicyFor(s, n, LintIntent.NeverTrim);
                LintPolicy? ok = PolicyFor(s, n, LintIntent.TrimOk);
                bool allowed = KeyedUnder(s, n, o.AllowTrim);
                bool designable = Ellipsis(t) && (ok is not null || allowed || (t.MaxLines >= 1 && n.CellCount >= 3));
                if (!s.RealText && !(designable && never is null))
                {
                    cls = "model";
                    sev = Severity.Warn;
                    outcome = Outcome.Review;
                    reason = "headless text model: the trim is the 0.55 x size model's, not DirectWrite's";
                }
                else if (never is not null) { cls = "never"; sev = Severity.Error; reason = never.Reason; }
                else if (t.Trim == "None") { cls = "overflow"; sev = Severity.Error; }
                else if (NearMiss(n, t) && s.RealText) { cls = "near-miss"; sev = Severity.Warn; }
                else if (t.Trim == "Clip") { cls = "hard-clip"; sev = Severity.Warn; }
                else if (designable)
                {
                    cls = "designed";
                    sev = Severity.Warn;
                    outcome = Outcome.Suppressed;
                    reason = ok?.Reason ?? (allowed ? "allowTrim" : "MaxLines " + t.MaxLines.ToString(CultureInfo.InvariantCulture) + " + ellipsis in a cell " + n.CellCount.ToString(CultureInfo.InvariantCulture) + " instances of " + n.Family + " repeat");
                    designed.Add(n);
                    designedFamilies[n.Pattern] = designedFamilies.TryGetValue(n.Pattern, out int c) ? c + 1 : 1;
                }
                else { cls = "single"; sev = Severity.Warn; outcome = Outcome.Review; reason = "a lone ellipsis outside any repeated cell: is the cut designed?"; }

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
                    Pattern = n.Pattern,
                    Cause = cls,
                    Outcome = outcome,
                    SuppressedBy = outcome == Outcome.Suppressed ? "intent" : "",
                    Reason = reason,
                    Rects = [Where(n)],
                    Nodes = [n.Index],
                });
            }
            if (designed.Count > 0)
            {
                var (refs, rects) = Members(designed);
                string families = string.Join(", ", designedFamilies.OrderByDescending(static kv => kv.Value).Select(static kv => kv.Key + " x" + kv.Value.ToString(CultureInfo.InvariantCulture)));
                into.Add(new Finding(Rules.TextTrim, Severity.Info,
                    $"{designed.Count} designed {Plural(designed.Count, "ellipsis", "ellipses")} (MaxLines + ellipsis in a cell every row or card repeats, or a guide TrimOk): {families}", refs)
                {
                    Measured = [M("count", designed.Count), M("families", designedFamilies.Count)],
                    Pattern = "designed",
                    Cause = "designed-total",
                    Count = designed.Count,
                    Outcome = Outcome.Report,
                    Rects = rects,
                    Nodes = Ids(designed),
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

        /// <summary>An error per size under <see cref="Options.TypeFloor"/> (words in scope; glyphs are not type). The fingerprint is the size
        /// alone (pattern <c>type</c>): which runs happen to be on screen first changes with the window, the size does not.</summary>
        static int TypeFloorRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            var small = new SortedDictionary<float, List<Node>>();
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Text is not { } t || !IsWords(n)) continue;
                looked++;
                if (t.Size + 0.01f >= o.TypeFloor) continue;
                float size = MathF.Round(t.Size, 2);
                if (!small.TryGetValue(size, out List<Node>? low)) small[size] = low = [];
                low.Add(n);
            }
            foreach (var (size, list) in small)
            {
                var (refs, rects) = Members(list, 8);
                string patterns = string.Join(", ", list.Select(static n => n.Pattern).Distinct().Take(4));
                into.Add(new Finding(Rules.TypeFloor, Severity.Error,
                    $"{list.Count} text {Plural(list.Count, "node", "nodes")} at {F(size)} DIP, under the {F(o.TypeFloor)} DIP floor ({patterns})", refs)
                {
                    Measured = [M("size", size), M("count", list.Count)],
                    Expected = [M("minSize", o.TypeFloor)],
                    Pattern = "type",
                    Cause = "size:" + F(size),
                    Count = list.Count,
                    Rects = rects,
                    Nodes = Ids(list),
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
                    Outcome = Outcome.Report,
                    Rects = rects,
                    Nodes = Ids(bin.Samples),
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
            public readonly List<string> Grounds = [];

            /// <summary>A busy-ground pair judged on the rendered pixels (<see cref="TextRun.Worst"/>): over an image it is an error.</summary>
            public bool Media, Photo;
            public int GapPixels;

            public void Add(Node run, string ink)
            {
                Runs.Add(run);
                if (!Inks.Contains(ink)) Inks.Add(ink);
            }
        }

        /// <summary>What a run's ground is named by in a finding and its fingerprint: the nearest plate under it that paints a ground (a fill of half
        /// opacity or more, an image, a gradient: a border-only box paints none) by its key pattern, else the page. Not the sampled colour: art
        /// tints a page's ground a little differently under every run, and one flaw must stay one finding.</summary>
        static string GroundName(Snapshot s, Node n)
        {
            for (int b = n.Backdrop; b >= 0; b = s.Nodes[b].Backdrop)
            {
                Node m = s.Nodes[b];
                if (m.Image || m.Gradient || m.Fill.A * m.Opacity >= 0.5f) return "plate " + m.Pattern;
            }
            return "the page";
        }

        /// <summary>Two colours a frame can tell apart by at most 0.04 a channel (about 10 of 255): the same paint.</summary>
        static bool SameColour(ColorF a, ColorF b) => MathF.Abs(a.R - b.R) <= 0.04f && MathF.Abs(a.G - b.G) <= 0.04f && MathF.Abs(a.B - b.B) <= 0.04f;

        static string Verb(int count, string one, string many) => count == 1 ? one : many;

        /// <summary>Each in-scope run's declared colour (and its opacity) against what its VISIBLE box was rendered on, GROUPED by (ink, the plate it
        /// sits on or the page): one finding per pair, with its runs as members; the message lists the grounds read. Over a SOLID ground (one colour
        /// holds 45 % of the box, or the backdrop is an opaque plate whose declared fill the frame agrees with: the colour that covers most of the box
        /// IS the fill, anti-aliasing spread it thin) the run is judged against that colour, a warning. Over a BUSY ground (a photograph, a scrim, a
        /// gradient, a highlight under part of it) it is judged against the rendered pixels around its glyphs at their worst (<see cref="TextRun.Worst"/>,
        /// read with the frame at <see cref="MediaPercentile"/>): an error over an image (<see cref="OverImage"/>: text over a photograph that does not
        /// read is a bug, whatever the declared fill says), a warning over a gradient or a wash. A busy run with no worst read (a snapshot saved
        /// before it was) is a `review` naming what it sits on, never silence. So is a run the frame gave no ground for, a run in a subtree still
        /// animating (a page fading in or out: the frame is mid-flight), and a run an opaque overlay partly covers (its ground may be the overlay's).
        /// Skipped: a disabled subtree, a transparent ink, a run faded below <see cref="Options.IncidentalOpacity"/> (incidental text), a fainter
        /// glow copy of the same words (<see cref="IsGlowCopy"/>), a span run, a run off the screen (`scope=content`), and everything when no frame
        /// was read (`gpu=none`, a crossing: `checked` 0).</summary>
        static int ContrastRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            bool framed = s.Backdrop is not null;
            foreach (Node n in s.Nodes)
                if (!framed && n.Text is { } run && run.Ground.A > 0f) framed = true;   // a replay: the saved grounds are the frame
            var pairs = new Dictionary<string, Pair>(StringComparer.Ordinal);
            Pair Group(string key, float need, string over)
            {
                if (!pairs.TryGetValue(key, out Pair? pair)) pairs[key] = pair = new Pair { Need = need, Over = over };
                return pair;
            }
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Text is not { } t || t.Spans || t.String.Length == 0 || n.Inert) continue;
                if (t.Color.A * n.Opacity < 0.02f || !framed || n.Visible.IsEmpty) continue;
                if (n.Opacity < o.IncidentalOpacity) continue;   // faded on purpose (a lyric's distance fade): incidental, not read
                if (IsGlowCopy(s, n, t)) continue;                // a bloom or shadow drawn under the same words: the readable copy is judged
                looked++;
                float need = ContrastNeeded(t, o);
                string ink = HexA(t.Color) + (n.Opacity < 0.995f ? "@" + F(n.Opacity) : "");
                // Why the frame cannot speak for this run: it is mid-flight, or an overlay covers part of its box.
                string doubt = n.Moving ? "moving|" : n.Occluded > 0f ? "covered|" : "";
                if (t.Ground.A <= 0f)
                {
                    Group(doubt.Length > 0 ? doubt : "none|", need, "no ground read").Add(n, ink);
                    continue;
                }
                ColorF ground = t.Ground;
                if (t.GroundShare < BusyShare)
                {
                    Node? under = n.Backdrop >= 0 ? s.Nodes[n.Backdrop] : null;
                    if (doubt.Length == 0 && under is { Image: false, Gradient: false } plate && plate.Fill.A * plate.Opacity >= 0.98f && SameColour(plate.Fill, t.Ground))
                        ground = plate.Fill with { A = 1f };   // a solid plate the frame agrees with: its declared fill is the ground
                    else if (doubt.Length == 0 && t.Worst > 0f)
                    {
                        // Judged on the rendered pixels around its glyphs at their worst (read with the frame): a photograph, a scrim, a gradient.
                        if (t.Worst + 0.005 >= need) continue;
                        bool photo = under is { Image: true } || OverImage(s, n);
                        string over = under is null ? (photo ? "an image" : "the page")
                            : (under.Image ? "art " : under.Gradient ? "a gradient " : "a plate ") + under.Pattern + (photo && !under.Image ? " over an image" : "");
                        string mediaKey = "media|" + ink + "|" + over + "|" + F(need);
                        if (!pairs.TryGetValue(mediaKey, out Pair? media)) pairs[mediaKey] = media = new Pair { Need = need, Over = over, Media = true, Photo = photo };
                        media.Add(n, ink);
                        if (t.Worst < media.Worst)
                        {
                            media.Worst = t.Worst;
                            media.Ground = t.WorstGround;
                            media.GapPixels = t.GapPixels;
                        }
                        continue;
                    }
                    else
                    {
                        string over = under is null ? "the page" : (under.Image ? "art " : under.Gradient ? "a gradient " : "a plate ") + under.Pattern;
                        Group(doubt.Length > 0 ? doubt : "busy|" + over, need, over).Add(n, ink);
                        continue;
                    }
                }
                double ratio = ContrastOf(t.Color, ground, n.Opacity);
                if (ratio + 0.005 >= need) continue;
                if (doubt.Length > 0)
                {
                    Group(doubt, need, "").Add(n, ink);
                    continue;
                }
                string on = GroundName(s, n);
                Pair pair = Group(ink + "|" + on + "|" + F(need), need, on);
                pair.Add(n, ink);
                string hex = "#" + Hex(ground);
                if (!pair.Grounds.Contains(hex)) pair.Grounds.Add(hex);
                if (ratio < pair.Worst)
                {
                    pair.Worst = ratio;
                    pair.Ground = ground;
                }
            }
            foreach (var (key, pair) in pairs)
            {
                var (refs, rects) = Members(pair.Runs);
                Node first = pair.Runs[0];
                TextRun t = first.Text!;
                string ink = "#" + HexA(t.Color);
                int count = pair.Runs.Count;
                string runs = count == 1 ? $"text {TextName(t)}" : $"{count} runs ({TextName(t)}, ...)";
                string inks = string.Join(", ", pair.Inks.Take(4).Select(static i => "#" + i)) + (pair.Inks.Count > 4 ? ", ..." : "");
                if (pair.Media)
                {
                    into.Add(new Finding(Rules.ContrastText, pair.Photo ? Severity.Error : Severity.Warn,
                        $"{runs} in {ink} over {pair.Over} read {F(pair.Worst)} : 1 on their p{F((1f - MediaPercentile) * 100f)} ground #{Hex(pair.Ground)}, under the {F(pair.Need)} : 1 they need", refs)
                    {
                        Measured = [M("ratio", pair.Worst), M("count", count), M("size", t.Size), M("weight", t.Weight), M("gapPixels", pair.GapPixels)],
                        Expected = [M("minRatio", pair.Need)],
                        Pattern = ink,
                        // The fingerprint is the ink and what it sits on, like a solid pair's: two plates are two flaws.
                        Cause = (pair.Photo ? "media:image over " : "media:gradient over ") + pair.Over,
                        Count = count,
                        Rects = rects,
                        Nodes = Ids(pair.Runs),
                    });
                    continue;
                }
                string review = key switch
                {
                    "moving|" => $"{runs} ({inks}) {Verb(count, "sits", "sit")} in a subtree that is still animating (a page fading in or out): the frame read is mid-flight; lint again once it settles",
                    "covered|" => $"{runs} ({inks}) {Verb(count, "is", "are")} partly under an opaque fill painted after {Verb(count, "it", "them")} (a menu, a dialog, a pinned header): the ground read may be the overlay's; look at the render",
                    "none|" => $"{runs} ({inks}) {Verb(count, "is", "are")} on screen, but the frame gave no ground under {Verb(count, "it", "them")} (under 2 x 2 DIP of the box shows); look at the render",
                    _ when key.StartsWith("busy|", StringComparison.Ordinal) => $"{runs} ({inks}) {Verb(count, "sits", "sit")} on a busy ground ({pair.Over}): no colour holds half a box, so contrast cannot be read from one colour; look at the render",
                    _ => "",
                };
                if (review.Length > 0)
                {
                    string cause = key.StartsWith("busy|", StringComparison.Ordinal) ? "busy" : key == "none|" ? "no-ground" : key[..^1];
                    into.Add(new Finding(Rules.ContrastText, Severity.Warn, review, refs)
                    {
                        Measured = [M("count", count), M("inks", pair.Inks.Count)],
                        Expected = [M("minRatio", pair.Need)],
                        Pattern = cause switch { "busy" => pair.Over, "no-ground" => "no ground", "moving" => "mid-animation", _ => "under an overlay" },
                        Cause = cause,
                        Count = count,
                        Outcome = Outcome.Review,
                        Reason = cause switch { "busy" => "busy ground", "no-ground" => "no ground read", "moving" => "mid-animation", _ => "partly under an overlay" },
                        Rects = rects,
                        Nodes = Ids(pair.Runs),
                    });
                    continue;
                }
                string grounds = pair.Grounds.Count > 1 ? $" (grounds {string.Join(", ", pair.Grounds.Take(4))}{(pair.Grounds.Count > 4 ? ", ..." : "")})" : "";
                into.Add(new Finding(Rules.ContrastText, Severity.Warn,
                    $"{runs} in {ink} on {pair.Over} read {F(pair.Worst)} : 1 at worst, on #{Hex(pair.Ground)}{grounds}, under the {F(pair.Need)} : 1 they need", refs)
                {
                    Measured = [M("ratio", pair.Worst), M("count", count), M("size", t.Size), M("weight", t.Weight)],
                    Expected = [M("minRatio", pair.Need)],
                    Pattern = ink,
                    Cause = "on " + pair.Over,
                    Count = count,
                    Rects = rects,
                    Nodes = Ids(pair.Runs),
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
                if (!(m.Image || m.Kind == "Image") || m.Opacity < 0.5f) continue;
                float w = MathF.Min(m.Rect.Right, n.Rect.Right) - MathF.Max(m.Rect.X, n.Rect.X);
                float h = MathF.Min(m.Rect.Bottom, n.Rect.Bottom) - MathF.Max(m.Rect.Y, n.Rect.Y);
                if (w > 0f && h > 0f && w * h >= 0.5f * area) return true;
            }
            return false;
        }

        // ── target.size (target) ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Track controls whose target is the whole track, not one mark on it: a slider, a scroll bar, a progress bar, a pips pager's pips (12-DIP
        /// dots abutting in one strip, a tap anywhere on it lands on a page; its chevrons are ordinary targets), and inline links (checked by eye).</summary>
        static bool ExemptFromTarget(Node n) => n.Role is "Slider" or "ScrollBar" or "ProgressBar" or "Hyperlink" or "Pager";

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

        /// <summary>A drag bar (a splitter, a seek line): thin across, long along, and it drags (<see cref="Drags"/>). It is reached along its
        /// length, so the floor does not apply. A thin, long target that only clicks (a 16 x 130 text button) is no bar.</summary>
        static bool IsBar(Snapshot s, Node n, in RectF area, float floor)
            => MathF.Min(area.W, area.H) <= floor / 2f && MathF.Max(area.W, area.H) >= 4f * floor && Drags(s, n);

        /// <summary>The target or an ancestor its click belongs to (the walk <see cref="EffectiveArea"/> takes) handles a drag, a pointer
        /// press-and-move or a gesture.</summary>
        static bool Drags(Snapshot s, Node n)
        {
            if (n.Drag) return true;
            int click = n.ClickId;
            for (int i = n.Parent; i >= 0; i = s.Nodes[i].Parent)
            {
                Node a = s.Nodes[i];
                if (!a.Interactive) continue;
                if (click == 0 && a.ClickId != 0) click = a.ClickId;
                else if (a.ClickId != click) break;
                if (a.Drag) return true;
            }
            return false;
        }

        /// <summary>The family a small target is grouped by: the key pattern of what it sits in (its parent's nearest key), so the band's Play
        /// and Shuffle, a pager's dots and a crumb trail are one finding each, whatever else is small at this window size.</summary>
        static string TargetFamily(Snapshot s, Node n) => n.Parent >= 0 ? s.Nodes[n.Parent].Pattern : n.Pattern;

        /// <summary>In-scope click targets that reach under the floor: <see cref="Options.TargetFloorCompact"/> (40) at window tier Compact, an
        /// error; <see cref="Options.TargetFloor"/> (32) above it, a warning. A target reaches as far as its effective area (<see cref="EffectiveArea"/>:
        /// the largest ancestor running the same click) or its HIT rect (<see cref="HitRect"/>: the box grown by its slop, cut as the hit test cuts
        /// it), whichever is larger; one that meets the floor either way passes. A role with no press handler is no target, a target its clipping
        /// ancestors hide whole is not on screen, and drag bars are exempt. Grouped by <see cref="TargetFamily"/>: one finding per family, its
        /// fingerprint the family and narrow/low. Only Compact's floor is an owner rule (2026-10, Compact targets >= 40 DIP): above Compact a
        /// finding is todo (report-only, decision D5 of the 10-23 audit), so it never fails `lint min=error` or `--gate`.</summary>
        static int TargetRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            bool compact = s.Tier == WidthTier.Compact;
            float floor = compact ? o.TargetFloorCompact : o.TargetFloor;
            var small = new List<(Node Node, RectF Area, RectF Hit, string CutBy)>();
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || !n.Interactive || n.Disabled || !n.Pressable || ExemptFromTarget(n)) continue;
                looked++;
                if (n.Rect.W >= floor - Slack && n.Rect.H >= floor - Slack) continue;
                if (n.Parent >= 0)
                {
                    Node p = s.Nodes[n.Parent];
                    if (p.Interactive && MathF.Abs(p.Rect.W - n.Rect.W) <= Slack && MathF.Abs(p.Rect.H - n.Rect.H) <= Slack) continue;
                }
                RectF area = EffectiveArea(s, n);
                if (area.W >= floor - Slack && area.H >= floor - Slack) continue;
                RectF hit = HitRect(s, n, out string cutBy);
                if (hit.W <= 0f || hit.H <= 0f) continue;   // a clipping ancestor hides it whole: no target on screen
                if (hit.W >= floor - Slack && hit.H >= floor - Slack) continue;
                if (IsBar(s, n, area, floor)) continue;
                small.Add((n, area, hit, cutBy));
            }
            var groups = new Dictionary<string, List<(Node Node, RectF Area, RectF Hit, string CutBy)>>(StringComparer.Ordinal);
            foreach (var entry in small)
            {
                string family = TargetFamily(s, entry.Node);
                if (!groups.TryGetValue(family, out var list)) groups[family] = list = [];
                list.Add(entry);
            }
            string floorText = compact ? $"the {F(floor)} x {F(floor)} Compact floor" : $"the {F(floor)} x {F(floor)} floor (Small and up)";
            foreach (var (pattern, list) in groups)
            {
                Node n = list[0].Node;
                var (refs, rects) = Members(list.Select(static e => e.Node));
                string sizes = string.Join(", ", list.Take(4).Select(e => TargetName(e.Node) + " " + F(e.Area.W) + " x " + F(e.Area.H) + HitText(e.Node, e.Hit, e.CutBy, compact)))
                    + (list.Count > 4 ? ", ..." : "");
                bool narrow = list.Exists(e => MathF.Max(e.Area.W, e.Hit.W) < floor - Slack), low = list.Exists(e => MathF.Max(e.Area.H, e.Hit.H) < floor - Slack);
                float minW = list.Min(static e => MathF.Max(e.Area.W, e.Hit.W)), minH = list.Min(static e => MathF.Max(e.Area.H, e.Hit.H));
                string head = list.Count > 1 ? $"{list.Count} hit targets in {pattern}" : $"hit target {n.Ref} (in {pattern})";
                into.Add(new Finding(Rules.TargetSize, compact ? Severity.Error : Severity.Warn, $"{head} under {floorText}: {sizes}", refs)
                {
                    Measured = [M("w", minW), M("h", minH), M("hitW", list.Min(static e => e.Hit.W)), M("hitH", list.Min(static e => e.Hit.H)), M("count", list.Count)],
                    Expected = [M("w", floor), M("h", floor)],
                    Pattern = pattern,
                    Cause = (narrow ? "narrow" : "") + (narrow && low ? "+" : "") + (low ? "low" : ""),
                    Level = compact ? Level.Gate : Level.Todo,
                    AnyRoute = true,   // the family's own hit area: the same fix on every page that shows it
                    Count = list.Count,
                    Rects = rects,
                    Nodes = Ids(list.Select(static e => e.Node)),
                });
            }
            return looked;
        }

        /// <summary>What a member's hit test adds to its box in a target finding: " (hit 36 x 40 with its slop; cut by ...)", " (its slop cut by ...)",
        /// " (no slop)" at Compact (where the host's touch floor should have given it one), or nothing.</summary>
        static string HitText(Node n, in RectF hit, string cutBy, bool compact)
        {
            bool slopped = hit.W > n.Rect.W + Slack || hit.H > n.Rect.H + Slack;
            if (slopped) return $" (hit {F(hit.W)} x {F(hit.H)} with its slop{(cutBy.Length > 0 ? "; cut by " + cutBy : "")})";
            if (cutBy.Length > 0) return " (its slop cut by " + cutBy + ")";
            return compact && n.Slop.Horizontal + n.Slop.Vertical <= 0f ? " (no slop)" : "";
        }

        /// <summary>A target as a message names it: its glyph (by icon name, else by code point), its label, else its ref.</summary>
        static string TargetName(Node n)
            => n.LabelGlyph.Length > 0 ? "glyph:" + n.LabelGlyph : IsGlyphText(n.Label) ? GlyphRef(n.Label, "") : n.Label.Length > 0 ? Q(n.Label) : n.Ref;

        /// <summary>The rect a target takes hits in: its box grown by its slop, cut by every clipping ancestor, and kept off the boxes of the other
        /// targets beside it (a real hit always wins over a slop hit, InputDispatcher.Hit).</summary>
        static RectF HitRect(Snapshot s, Node n) => HitRect(s, n, out _);

        static RectF HitRect(Snapshot s, Node n, out string cutBy)
        {
            cutBy = "";
            Edges4 slop = n.Slop;
            if (slop.Left <= 0f && slop.Top <= 0f && slop.Right <= 0f && slop.Bottom <= 0f) return n.Rect;
            RectF r = n.Rect;
            float l = r.X - slop.Left, t = r.Y - slop.Top, rr = r.Right + slop.Right, b = r.Bottom + slop.Bottom;
            var by = new List<string>(2);
            // A target not wholly in its scroller's view is judged as if scrolled in: that scroller, and every ancestor above it, cut only across
            // its axis. A target in view meets every clip as the hit test does (a pager at the end of a paged shelf is cut at the shelf's edge).
            bool scrollsH = false, scrollsV = false;
            for (int a = n.Parent; a >= 0; a = s.Nodes[a].Parent)
            {
                Node anc = s.Nodes[a];
                if (anc.ScrollAxis == 1 && (r.X < anc.Rect.X - Slack || r.Right > anc.Rect.Right + Slack)) scrollsH = true;
                if (anc.ScrollAxis == 2 && (r.Y < anc.Rect.Y - Slack || r.Bottom > anc.Rect.Bottom + Slack)) scrollsV = true;
                if (!anc.Clips && anc.ScrollAxis == 0) continue;
                float cl = scrollsH ? float.NegativeInfinity : anc.Rect.X, cr = scrollsH ? float.PositiveInfinity : anc.Rect.Right;
                float ct = scrollsV ? float.NegativeInfinity : anc.Rect.Y, cb = scrollsV ? float.PositiveInfinity : anc.Rect.Bottom;
                if (cl > l + Slack || ct > t + Slack || cr < rr - Slack || cb < b - Slack)
                    if (by.Count < 2) by.Add("clip " + anc.Ref);
                l = MathF.Max(l, cl); t = MathF.Max(t, ct); rr = MathF.Min(rr, cr); b = MathF.Min(b, cb);
            }
            int scroller = NearestScroller(s, n);
            foreach (Node m in s.Nodes)
            {
                if (!m.Interactive || !m.Pressable || m.Index == n.Index || Related(s, n, m)) continue;
                // Only a neighbour in the same scrolled plane (one under another scroller meets it at these coordinates only off screen), or a scroll
                // bar of a scroller the target sits in (it overlays the target's edge of the view, and its real hit wins).
                if (NearestScroller(s, m) != scroller && !(m.Role == "ScrollBar" && BarOfAncestor(s, n, m))) continue;
                RectF o = m.Rect;
                if (o.X >= rr || o.Right <= l || o.Y >= b || o.Bottom <= t) continue;   // not in the grown rect
                bool cut = false;
                if (o.X >= r.Right) { rr = MathF.Min(rr, o.X); cut = true; }
                else if (o.Right <= r.X) { l = MathF.Max(l, o.Right); cut = true; }
                if (o.Y >= r.Bottom) { b = MathF.Min(b, o.Y); cut = true; }
                else if (o.Bottom <= r.Y) { t = MathF.Max(t, o.Bottom); cut = true; }
                if (cut && by.Count < 3) by.Add("beside " + m.Ref);
            }
            // The invisible targets (an auto-hidden scroll bar) cut it only where the target is on screen in its view: off screen they meet at no real point.
            if (InView(s, n))
                foreach (RectF o in s.HitBlockers)
                {
                    if (o.X >= rr || o.Right <= l || o.Y >= b || o.Bottom <= t) continue;
                    if (o.X >= r.Right) rr = MathF.Min(rr, o.X);
                    else if (o.Right <= r.X) l = MathF.Max(l, o.Right);
                    else if (o.Y >= r.Bottom) b = MathF.Min(b, o.Y);
                    else if (o.Bottom <= r.Y) t = MathF.Max(t, o.Bottom);
                    else continue;
                    if (by.Count < 3) by.Add("beside a target that paints nothing at " + F(o.X) + "," + F(o.Y));
                }
            // A scroll bar's lane takes a press before any target does: each side's slop stops where a lane starts (Node.Lane, read at capture
            // along the target's middle line, so a replay cuts the same).
            if (InView(s, n))
            {
                Edges4 lane = n.Lane;
                bool cut = false;
                if (lane.Right >= 0f && rr > r.Right + lane.Right) { rr = r.Right + lane.Right; cut = true; }
                if (lane.Left >= 0f && l < r.X - lane.Left) { l = r.X - lane.Left; cut = true; }
                if (lane.Bottom >= 0f && b > r.Bottom + lane.Bottom) { b = r.Bottom + lane.Bottom; cut = true; }
                if (cut && by.Count < 4) by.Add("a scroll bar's lane");
            }
            cutBy = string.Join(", ", by);
            return new RectF(l, t, MathF.Max(0f, rr - l), MathF.Max(0f, b - t));
        }

        /// <summary>The node lies wholly inside every scroller above it (and the window): it is on screen, not waiting to be scrolled in.</summary>
        static bool InView(Snapshot s, Node n)
        {
            RectF r = n.Rect;
            if (r.X < -Slack || r.Y < -Slack || r.Right > s.Width + Slack || r.Bottom > s.Height + Slack) return false;
            for (int a = n.Parent; a >= 0; a = s.Nodes[a].Parent)
            {
                Node anc = s.Nodes[a];
                if (anc.ScrollAxis == 0) continue;
                if (r.X < anc.Rect.X - Slack || r.Y < anc.Rect.Y - Slack || r.Right > anc.Rect.Right + Slack || r.Bottom > anc.Rect.Bottom + Slack) return false;
            }
            return true;
        }

        /// <summary>The index of the nearest scrolling ancestor, -1 for none.</summary>
        static int NearestScroller(Snapshot s, Node n)
        {
            for (int a = n.Parent; a >= 0; a = s.Nodes[a].Parent) if (s.Nodes[a].ScrollAxis != 0) return a;
            return -1;
        }

        /// <summary>The scroll bar <paramref name="bar"/> belongs to a scroller that holds <paramref name="n"/> (the bar's nearest scrolling ancestor-or-
        /// sibling root: the first scroller up its chain that is also an ancestor of <paramref name="n"/>).</summary>
        static bool BarOfAncestor(Snapshot s, Node n, Node bar)
        {
            for (int a = bar.Parent; a >= 0; a = s.Nodes[a].Parent)
            {
                if (s.Nodes[a].ScrollAxis == 0) continue;
                for (int b = n.Parent; b >= 0; b = s.Nodes[b].Parent) if (b == a) return true;
                return false;
            }
            return false;
        }

        /// <summary>One of the two nodes is the other's ancestor.</summary>
        static bool Related(Snapshot s, Node a, Node b)
        {
            for (int i = b.Parent; i >= 0; i = s.Nodes[i].Parent) if (i == a.Index) return true;
            for (int i = a.Parent; i >= 0; i = s.Nodes[i].Parent) if (i == b.Index) return true;
            return false;
        }

        /// <summary>A private-use character or two: an icon glyph, not words.</summary>
        public static bool IsGlyphText(string text)
        {
            if (text.Length is 0 or > 2) return false;
            foreach (char c in text)
                if (c < '\uE000' || c > '\uF8FF') return false;
            return true;
        }

        // ── duplicate.action (duplicate) ────────────────────────────────────────────────────────────────────────────

        /// <summary>A verb: a button-like target (Button, ToggleButton, MenuItem or no role) that is not a content card (a card or row carries art or
        /// a title and a subtitle: its label is a name, not a verb).</summary>
        static bool IsVerb(Node n) => n.Role is "Button" or "ToggleButton" or "MenuItem" or "" && n.ImageCount == 0 && n.TextCount < 2;

        /// <summary>What a verb acts on: the first spotify entity in its key or an ancestor's; else the row or card it sits in (an instance of a
        /// repeated family that is a click target or carries art: each track row's More acts on its own track, whatever its key says); else, in
        /// window chrome (<paramref name="paged"/>: the capture holds a page slot, and the verb sits outside the content card that hosts the page
        /// and its band: the nav column, the title bar), the window itself: the nav pane's "+" makes a playlist, it never follows the show on
        /// screen, while Zune's band (in the card, outside the page slot) acts on the page; else the page's own entity, else the page.</summary>
        static string SubjectOf(Snapshot s, Node n, bool paged = false)
        {
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
            {
                Node m = s.Nodes[i];
                if (EntityIn(m.Key) is { Length: > 0 } entity) return entity;
                if (i != n.Index && m.Instance == i && (m.Interactive || m.ImageCount > 0))
                    return "the row " + m.Ref + " #" + i.ToString(CultureInfo.InvariantCulture);
            }
            if (paged && !KeyedUnder(s, n, ContentCard)) return "the window";
            return s.PageEntity.Length > 0 ? s.PageEntity : "page";
        }

        /// <summary>The content card: the frame that hosts the page and what speaks for it (Zune's band); outside it is window chrome.</summary>
        static readonly string[] ContentCard = ["shell.content.card"];

        /// <summary>The capture holds a page slot and the content card around it (a `root=` lint of one subtree holds neither): what tells window
        /// chrome from the page.</summary>
        static bool HasPageSlot(Snapshot s)
        {
            bool slot = false, card = false;
            foreach (Node n in s.Nodes)
            {
                if (n.Key is not { } key) continue;
                if (TrySlotRoute(key, out _, out _)) slot = true;
                else if (string.Equals(key, ContentCard[0], StringComparison.Ordinal)) card = true;
                if (slot && card) return true;
            }
            return false;
        }

        /// <summary>The same verb on the same subject offered in two homes on screen (the band's Shuffle and the song list's Shuffle on an album). A
        /// verb is matched by role and label (a glyph by its icon name); a key family is one home ("See all" per shelf is one pattern); content cards
        /// and links are not verbs; the title bar and play bar (<see cref="Options.IgnoreUnder"/>) repeat the page's verbs by design.</summary>
        static int DuplicateRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            bool paged = HasPageSlot(s);
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
                string verb = n.LabelGlyph.Length > 0 ? "glyph:" + n.LabelGlyph : IsGlyphText(n.Label) ? GlyphRef(n.Label, "") : n.Label.ToLowerInvariant();
                string group = n.Role + "|" + verb + "|" + SubjectOf(s, n, paged);
                if (!groups.TryGetValue(group, out List<Node>? list)) groups[group] = list = [];
                list.Add(n);
            }
            foreach (var (group, list) in groups)
            {
                if (list.Count < 2) continue;
                var homes = new List<string>();
                foreach (Node n in list)
                {
                    string home = n.Pattern == "#root" ? "" : RootOfKey(n.Pattern);
                    if (!homes.Contains(home)) homes.Add(home);
                }
                if (homes.Count < 2) continue;
                var (refs, rects) = Members(list);
                Node first = list[0];
                string verb = first.LabelGlyph.Length > 0 ? "glyph:" + first.LabelGlyph : IsGlyphText(first.Label) ? GlyphRef(first.Label, "") : Q(first.Label);
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
                    Nodes = Ids(list),
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
        /// read fine) and are a report line (outcome report), as are the gaps themselves. Grouped by the column's key pattern: one finding per
        /// family and kind.</summary>
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
                    Nodes = Ids(g.Parents),
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
                    Outcome = Outcome.Report,
                    Rects = rects,
                    Nodes = Ids(g.Parents),
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
                    Outcome = Outcome.Report,
                    Rects = rects,
                    Nodes = Ids(g.Parents),
                });
            }
            return looked;
        }
    }
}
