// ── Screens/Diagnostics.Inspect.Lint.Scene.cs — the `lint` verb: the mounted scene into a snapshot, the report into JSON ───────
// `lint {rules?, scope?, root?, nth?, min?, limit?, outcomes?, baseline?, save?, replay?, typeFloor?, targetFloor?, targetFloorCompact?,
//        gapMin?, allowTrim?, allowDuplicate?, ignoreUnder?}`   (docs/guide/agent-lint.md is the reference)
//
// Role: SHELL (UI thread; reads the engine's public scene inspection only, like the dump)
// Spec: scratch/agentic-e2e/PLAN.md 4.4-4.5 and 5.1-5.5; the rules are Diagnostics.Inspect.Lint*.cs (pure).
//
// THE CAPTURE walks the scene exactly as `dump` does (visible, unparked, non-transparent subtrees; a node counts once it has a box; the
// overlays that do not hang under the root follow) and keeps what the rules read: the presented rect, clip and scroll axes (with the scroll
// offset, its range and any edge fade), an authored clip, the DECLARED layout (gap, padding, margin, sizes, flex, alignment, wrap), the paint
// that makes a node ink (fill, border, image, gradient), the text run (the dump's style + the engine's trim query), the click targets with their
// label (a glyph by its icon name) and which click they run, the active theme's guide (edges and policies) and its spacing scale.
//
// SAVE AND REPLAY. `save=<file>` writes the snapshot the rules saw (Diagnostics.Inspect.Lint.Store.cs); `replay=<file>` lints a saved one and
// reads nothing from the mount (the owner allowed replays of saved pages; a dispatcher that routes `lint` before its mount check can replay
// with nothing mounted).

using System.Runtime.InteropServices;
using System.Text.Json;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Text;
using Wavee.Theming;
using Wavee.Theming.Roles;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>Scene capture, option parsing and the JSON report of the lint. UI thread.</summary>
    public static class InspectLintScene
    {
        // ── the arguments ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A list argument: a comma-separated string (<c>gutter,clip</c>) or a JSON array of strings.</summary>
        public static List<string> ListOf(Inspect.Args a, string name)
        {
            var list = new List<string>();
            if (!a.TryGet(name, out Inspect.Value value)) return list;
            if (value.Kind == Inspect.ValueKind.Json)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(value.Str);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement e in doc.RootElement.EnumerateArray())
                            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) list.Add(s);
                }
                catch (JsonException) { }
                return list;
            }
            foreach (string part in value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add(part);
            return list;
        }

        /// <summary>Every arg `lint` reads (for a dispatcher's arg table): the options below, plus `root`, `nth`, `save` and `replay`.</summary>
        public static readonly string[] ArgNames =
        [
            "rules", "scope", "root", "nth", "min", "limit", "outcomes", "baseline", "save", "replay", "typeFloor", "targetFloor",
            "targetFloorCompact", "gapMin", "allowTrim", "allowDuplicate", "ignoreUnder",
        ];

        /// <summary>The lint options an args bag spells: <c>rules</c> (ids, today's names, families: <c>align</c>, <c>all</c>), <c>scope</c>
        /// (screen|content|all), <c>min</c> (info|warn|error), <c>limit</c> (findings listed per rule), <c>outcomes</c> (fail,review,suppressed or
        /// all: which are LISTED; every one is counted), <c>baseline</c> (a JSON-lines file), <c>typeFloor</c>, <c>targetFloor</c>,
        /// <c>targetFloorCompact</c>, <c>gapMin</c>, <c>allowTrim</c>, <c>allowDuplicate</c>, <c>ignoreUnder</c>.</summary>
        public static bool TryOptions(Inspect.Args a, out InspectLint.Options options, out string error)
        {
            error = "";
            options = new InspectLint.Options();
            IReadOnlyCollection<string> rules = InspectLint.RuleIds;
            if (a.Has("rules"))
            {
                var ids = new List<string>();
                foreach (string rule in ListOf(a, "rules"))
                {
                    List<string> named = InspectLint.IdsOf(rule);
                    if (named.Count == 0)
                    {
                        error = "unknown lint rule '" + rule + "' (" + string.Join("|", InspectLint.RuleIds) + "; or " + string.Join("|", InspectLint.RuleNames) + ", a family like align, or all)";
                        return false;
                    }
                    foreach (string id in named)
                        if (!ids.Contains(id)) ids.Add(id);
                }
                if (ids.Count > 0) rules = ids;
            }
            InspectLint.Severity min = InspectLint.Severity.Info;
            if (a.Has("min") && !InspectLint.TryParseSeverity(a.Str("min"), out min))
            {
                error = "min is info, warn or error";
                return false;
            }
            InspectVisible.Scope scope = InspectVisible.Scope.Screen;
            if (a.Has("scope") && !InspectVisible.TryParseScope(a.Str("scope"), out scope))
            {
                error = "scope is screen, content or all";
                return false;
            }
            IReadOnlyCollection<InspectLint.Outcome> outcomes = [InspectLint.Outcome.Fail, InspectLint.Outcome.Review];
            if (a.Has("outcomes"))
            {
                var listed = new List<InspectLint.Outcome>();
                foreach (string word in ListOf(a, "outcomes"))
                {
                    if (string.Equals(word, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        listed = [InspectLint.Outcome.Fail, InspectLint.Outcome.Review, InspectLint.Outcome.Suppressed];
                        break;
                    }
                    if (!InspectLint.TryParseOutcome(word, out InspectLint.Outcome o))
                    {
                        error = "outcomes lists fail, review, suppressed (or all)";
                        return false;
                    }
                    if (!listed.Contains(o)) listed.Add(o);
                }
                if (listed.Count > 0) outcomes = listed;
            }
            List<InspectLint.BaselineEntry> baseline = [];
            string baselineFile = "";
            if (a.Has("baseline"))
            {
                baselineFile = a.Str("baseline");
                if (!InspectLintStore.TryReadBaseline(baselineFile, out baseline, out error)) return false;
            }
            options = new InspectLint.Options
            {
                Rules = rules,
                Scope = scope,
                TypeFloor = a.Float("typeFloor", 11f),
                TargetFloor = a.Float("targetFloor", 32f),
                TargetFloorCompact = a.Float("targetFloorCompact", 40f),
                GapMin = a.Float("gapMin", 2f),
                AllowTrim = ListOf(a, "allowTrim"),
                AllowDuplicate = ListOf(a, "allowDuplicate"),
                IgnoreUnder = a.Has("ignoreUnder") ? ListOf(a, "ignoreUnder") : InspectLint.DefaultIgnoreUnder,
                MaxPerRule = Math.Clamp(a.Int("limit", 100), 1, 10_000),
                Min = min,
                Outcomes = outcomes,
                Baseline = baseline,
                BaselineFile = baselineFile,
            };
            return true;
        }

        // ── the capture ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A private-use character or two: an icon glyph, not words.</summary>
        public static bool IsGlyph(string text)
        {
            if (text.Length is 0 or > 2) return false;
            foreach (char c in text)
                if (c < '' || c > '') return false;
            return true;
        }

        /// <summary>The icon name of a glyph label (<c>Icons.TryGetName</c>), "" when it is words or no named icon.</summary>
        static string GlyphNameOf(string text) => IsGlyph(text) && Icons.TryGetName(text, out string? name) ? name : "";

        static bool IsActionRole(AutomationRole role) => role is AutomationRole.Button or AutomationRole.ToggleButton or AutomationRole.NavigationItem
            or AutomationRole.CheckBox or AutomationRole.RadioButton or AutomationRole.Hyperlink or AutomationRole.ToggleSwitch
            or AutomationRole.MenuItem or AutomationRole.Tab or AutomationRole.ComboBox or AutomationRole.Slider or AutomationRole.ScrollBar;

        /// <summary>The first text at or under <paramref name="node"/>: a button's label, or its glyph.</summary>
        static string LabelOf(SceneStore scene, StringTable strings, NodeHandle node)
        {
            foreach (NodeHandle n in InspectKit.Subtree(scene, node))
            {
                ref NodePaint p = ref scene.Paint(n);
                if (p.VisualKind == VisualKind.Text && p.Text.Value > 0) return strings.Resolve(p.Text);
            }
            return "";
        }

        static InspectLint.TextRun TextOf(Shell.HeadlessShell shell, NodeHandle node, ref NodePaint paint, StringTable strings)
        {
            SceneStore scene = shell.Host.Scene;
            TextStyle style = scene.Layout(node).TextStyle;
            string value = paint.Text.Value > 0 ? strings.Resolve(paint.Text) : "";
            bool spans = value.Length == 0 && scene.TryGetSpanText(node, out ReadOnlySpan<TextSpan> runs) && runs.Length > 0;
            TextOverflow overflow = shell.Host.QueryTextOverflow(node);
            bool glyph = IsGlyph(value);
            return new InspectLint.TextRun(value, style.SizeDip)
            {
                LineHeight = float.IsFinite(style.LineHeight) ? style.LineHeight : 0f,
                Weight = style.Weight,
                MaxLines = style.MaxLines,
                Wraps = style.Wrap != TextWrap.NoWrap,
                Trim = style.Trim.ToString(),
                Trimmed = overflow.Trimmed,
                NaturalW = overflow.IsText ? overflow.NaturalSize.Width : 0f,
                NaturalH = overflow.IsText ? overflow.NaturalSize.Height : 0f,
                OverflowX = overflow.HorizontalOverflow,
                OverflowY = overflow.VerticalOverflow,
                Glyph = glyph,
                GlyphName = glyph ? GlyphNameOf(value) : "",
                Spans = spans,
                Color = paint.TextColor,
                Align = style.Align.ToString(),
            };
        }

        static InspectLint.Declared DeclaredOf(in LayoutInput l) => new(l.Gap, l.Padding, l.Margin)
        {
            W = l.Width,
            H = l.Height,
            MinW = l.MinW,
            MinH = l.MinH,
            MaxW = l.MaxW,
            MaxH = l.MaxH,
            Grow = l.FlexGrow,
            Shrink = l.FlexShrink,
            Basis = l.FlexBasis,
            Justify = l.Justify.ToString(),
            AlignItems = l.AlignItems.ToString(),
            AlignSelf = l.AlignSelf.ToString(),
            JustifySelf = l.JustifySelf.ToString(),
            Wrap = l.Wrap,
        };

        /// <summary>The theme's design scale as named values: the active definition's <c>Spacing</c> rungs, and the style's <c>R.Space</c> rhythm
        /// and <c>R.Shape</c> radii (what <c>space.scale</c> accepts besides 2 and the 4-grid up to 64).</summary>
        public static IReadOnlyList<InspectLint.ScaleEntry> ScaleOf()
        {
            SpaceRoles space = R.Space;
            ShapeRoles shape = R.Shape;
            return
            [
                new("Spacing.XXS", Spacing.XXS), new("Spacing.XS", Spacing.XS), new("Spacing.S", Spacing.S), new("Spacing.M", Spacing.M),
                new("Spacing.L", Spacing.L), new("Spacing.XL", Spacing.XL), new("Spacing.XXL", Spacing.XXL), new("Spacing.XXXL", Spacing.XXXL),
                new("Spacing.Gutter", Spacing.Gutter), new("Spacing.PageWide", Spacing.PageWide), new("Spacing.PageNarrow", Spacing.PageNarrow),
                new("Spacing.Card", Spacing.Card), new("Spacing.Inner", Spacing.Inner), new("Spacing.StackS", Spacing.StackS),
                new("Spacing.StackM", Spacing.StackM), new("Spacing.StackL", Spacing.StackL),
                new("R.Space.SectionGap", space.SectionGap), new("R.Space.SectionGapWide", space.SectionGapWide), new("R.Space.GridGap", space.GridGap),
                new("R.Shape.Card", shape.Card), new("R.Shape.Cover", shape.Cover), new("R.Shape.Control", shape.Control),
                new("R.Shape.Overlay", shape.Overlay), new("R.Shape.Tile", shape.Tile), new("R.Shape.PinArt", shape.PinArt),
                new("R.Shape.Avatar", shape.Avatar), new("R.Shape.CurrentMark", shape.CurrentMark), new("R.Shape.ContentSeam", shape.ContentSeam),
                new("R.Shape.OmniRow", shape.OmniRow), new("R.Shape.SeekTrack", shape.SeekTrack),
            ];
        }

        /// <summary>The route as `status` names it.</summary>
        static string RouteNow()
        {
            var r = Wavee.Shell.Current.Peek();
            string? arg = Wavee.Shell.ArgOf(r);
            return Wavee.Shell.NameOf(r) + (arg is null ? "" : " " + arg);
        }

        /// <summary>The visible nodes under <paramref name="roots"/> as a lint snapshot, with the active theme's guide and scale. UI thread, between
        /// frames. <paramref name="pixels"/> is the rendered frame the grounds under the text are read from (null: none read).</summary>
        public static InspectLint.Snapshot Capture(Shell.HeadlessShell shell, ReadOnlySpan<NodeHandle> roots, InspectLint.Pixels? pixels = null, string devices = "")
        {
            SceneStore scene = shell.Host.Scene;
            StringTable strings = scene.Strings ?? Entities.Strings;
            var nodes = new List<InspectLint.Node>();
            var clicks = new Dictionary<Delegate, int>();
            var stack = new Stack<(NodeHandle Node, int Parent, int Depth, float Opacity, bool Inert)>();
            var children = new List<NodeHandle>();
            Span<char> path = stackalloc char[256];
            for (int r = roots.Length - 1; r >= 0; r--) stack.Push((roots[r], -1, 0, 1f, false));
            while (stack.Count > 0)
            {
                var (node, parent, depth, above, inertAbove) = stack.Pop();
                if (node.IsNull || !scene.IsLive(node)) continue;
                NodeFlags flags = scene.Flags(node);
                ref NodePaint paint = ref scene.Paint(node);
                if ((flags & NodeFlags.Visible) == 0 || (flags & (NodeFlags.Parked | NodeFlags.Exiting)) != 0 || paint.Opacity <= 0f) continue;
                RectF rect = scene.PresentedAbsoluteRect(node);
                float opacity = above * paint.Opacity;
                bool inert = inertAbove || (flags & NodeFlags.Disabled) != 0;
                int self = parent;
                if (rect.W > 0f && rect.H > 0f)
                {
                    self = nodes.Count;
                    ref InteractionInfo it = ref scene.Interaction(node);
                    bool click = (it.HandlerMask & InteractionInfo.ClickBit) != 0;
                    bool interactive = (flags & NodeFlags.HitTestVisible) != 0 && (click || IsActionRole(it.Role));
                    int clickId = 0;
                    if (click && scene.GetClickHandler(node) is { } handler && !clicks.TryGetValue(handler, out clickId))
                        clicks[handler] = clickId = clicks.Count + 1;
                    int axis = 0;
                    float offset = 0f, max = 0f;
                    byte fade = 0;
                    if (scene.TryGetScroll(node, out ScrollState scroll))
                    {
                        axis = scroll.Orientation == 1 ? 1 : 2;
                        offset = (float)scroll.Offset;
                        max = scroll.MaxOffset;
                        if (scroll.AutoEdgeFade && scroll.AutoEdgeFadeBand > 0f) fade |= (byte)(axis == 1 ? EdgeMask.Horizontal : EdgeMask.Vertical);
                    }
                    if (scene.TryGetEdgeFade(node, out EdgeFadeSpec edgeFade) && !edgeFade.IsNone) fade |= (byte)edgeFade.Edges;
                    RectF authored = paint.ClipRect;
                    string label = interactive ? LabelOf(scene, strings, node) : "";
                    int pathLength = NodeDescriber.Describe(scene, (int)node.Raw.Index, node.Raw.Gen, path);
                    ref LayoutInput layout = ref scene.Layout(node);
                    nodes.Add(new InspectLint.Node(self, parent, scene.DebugKeyOf(node), new string(path[..pathLength]), rect)
                    {
                        Depth = depth,
                        Kind = NodeDescriber.ElementTypeName(scene.ElementTypeId(node)),
                        Clips = (flags & NodeFlags.ClipsToBounds) != 0,
                        ScrollAxis = axis,
                        ScrollOffset = offset,
                        ScrollMax = max,
                        FadeEdges = fade,
                        ClipRect = authored.IsInfinite ? RectF.Infinite : new RectF(rect.X + authored.X, rect.Y + authored.Y, authored.W, authored.H),
                        ZStack = (flags & NodeFlags.ZStack) != 0,
                        Column = layout.Direction == 1,
                        Layout = DeclaredOf(in layout),
                        Interactive = interactive,
                        Disabled = (flags & NodeFlags.Disabled) != 0,
                        Role = it.Role == AutomationRole.None ? "" : it.Role.ToString(),
                        ClickId = clickId,
                        Label = label,
                        LabelGlyph = GlyphNameOf(label),
                        Animating = shell.Host.Animation.HasTracks(node),
                        Opacity = opacity,
                        Inert = inert,
                        Sticky = (flags & NodeFlags.StickyPinned) != 0,
                        Fill = paint.Fill,
                        BorderWidth = paint.BorderWidth,
                        BorderColor = paint.BorderColor,
                        Image = paint.VisualKind == VisualKind.Image,
                        Gradient = scene.TryGetGradient(node, out _),
                        Text = paint.VisualKind == VisualKind.Text ? TextOf(shell, node, ref paint, strings) : null,
                    });
                }
                children.Clear();
                for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c)) children.Add(c);
                for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], self, self == parent ? depth : depth + 1, opacity, inert));
            }

            // The words and images under each node: children follow their parent, so one backwards pass.
            var words = new int[nodes.Count];
            var images = new int[nodes.Count];
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                InspectLint.Node n = nodes[i];
                if (n.Text is { Glyph: false } t && (t.String.Length > 0 || t.Spans)) words[i]++;
                if (n.Image) images[i]++;
                if (n.Parent >= 0)
                {
                    words[n.Parent] += words[i];
                    images[n.Parent] += images[i];
                }
            }
            for (int i = 0; i < nodes.Count; i++) nodes[i] = nodes[i] with { TextCount = words[i], ImageCount = images[i] };

            TierFacts tiers = ThemeHost.Facts.Peek().Tiers;
            float scale = Math.Max(0.01f, shell.Window.Scale);
            float width = shell.Window.ClientSizePx.Width / scale, height = shell.Window.ClientSizePx.Height / scale;
            IReadOnlyList<LintEdge> edges = [];
            IReadOnlyList<LintPolicy> policies = [];
            string guideId = "";
            if (ThemeInspection.ActiveLintGuide is { } guide)
            {
                RectF? RectOfKey(string key)
                {
                    NodeHandle found = shell.Find(key);
                    return found.IsNull ? (RectF?)null : scene.PresentedAbsoluteRect(found);
                }
                var context = new LintContext(tiers.Window, tiers.Height, width, height, RectOfKey);
                edges = guide.Edges(context);
                policies = guide.Policies(context);
                guideId = InspectKit.ActiveThemeId;
            }
            return new InspectLint.Snapshot(width, height, tiers.Window, nodes)
            {
                Edges = edges,
                Policies = policies,
                Scale = ScaleOf(),
                Theme = InspectKit.ActiveThemeId,
                GuideId = guideId,
                Route = RouteNow(),
                TrimSource = InspectDump.TrimSource(shell),
                Devices = devices,
                Backdrop = pixels,
            };
        }

        // ── the report ──────────────────────────────────────────────────────────────────────────────────────────────

        static void Number(Utf8JsonWriter w, string name, double v) => w.WriteNumber(name, double.IsFinite(v) ? Math.Round(v, 2) : 0d);

        static void WriteMetrics(Utf8JsonWriter w, string name, IReadOnlyList<InspectLint.Metric> metrics)
        {
            if (metrics.Count == 0) return;
            w.WriteStartObject(name);
            foreach (InspectLint.Metric m in metrics) Number(w, m.Name, m.Value);
            w.WriteEndObject();
        }

        /// <summary>One finding: <c>{rule, id, severity, outcome, level, message, keys, measured?, expected?, fp, group{count, pattern}, rects, fix,
        /// reason?, suppressedBy?}</c>. <c>rule</c> is today's name for the nine rules (the id for the new ones); <c>keys</c> holds up to six
        /// member refs (a key, or a dump path for a keyless node) and <c>rects</c> their visible rects <c>[x, y, w, h]</c>.</summary>
        public static void WriteFinding(Utf8JsonWriter w, InspectLint.Finding f)
        {
            w.WriteStartObject();
            w.WriteString("rule", f.Rule);
            w.WriteString("id", f.Id.Length > 0 ? f.Id : f.Rule);
            w.WriteString("severity", InspectLint.Name(f.Severity));
            w.WriteString("outcome", InspectLint.Name(f.Outcome));
            w.WriteString("level", InspectLint.Name(f.Level));
            w.WriteString("message", f.Message);
            w.WriteStartArray("keys");
            foreach (string key in f.Keys) w.WriteStringValue(key);
            w.WriteEndArray();
            WriteMetrics(w, "measured", f.Measured);
            WriteMetrics(w, "expected", f.Expected);
            w.WriteString("fp", f.Fp);
            w.WriteStartObject("group");
            w.WriteNumber("count", f.Count);
            w.WriteString("pattern", f.Pattern);
            w.WriteEndObject();
            if (f.Rects.Count > 0)
            {
                w.WriteStartArray("rects");
                foreach (RectF r in f.Rects)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(Math.Round(r.X, 2));
                    w.WriteNumberValue(Math.Round(r.Y, 2));
                    w.WriteNumberValue(Math.Round(r.W, 2));
                    w.WriteNumberValue(Math.Round(r.H, 2));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            if (f.Fix.Length > 0) w.WriteString("fix", f.Fix);
            if (f.Reason.Length > 0) w.WriteString("reason", f.Reason);
            if (f.SuppressedBy.Length > 0) w.WriteString("suppressedBy", f.SuppressedBy);
            w.WriteEndObject();
        }

        /// <summary>Into the open object: <c>guide</c> (<c>{id, edges, matched, policies}</c>, or false when the theme has none), <c>scope</c>,
        /// <c>summary</c> (listed fail findings of the gate rules: what "lint min=error clean" means), <c>outcomes</c>, <c>levels</c>,
        /// <c>suppressed</c>, <c>baseline</c>?, <c>rules</c> (per rule: <c>checked</c>, its counts, level and ids), <c>truncated</c>? and
        /// <c>findings</c>.</summary>
        public static void WriteReport(Utf8JsonWriter w, InspectLint.Snapshot snapshot, InspectLint.Report report, InspectLint.Options options)
        {
            if (snapshot.GuideId.Length > 0 || snapshot.Edges.Count > 0)
            {
                w.WriteStartObject("guide");
                w.WriteString("id", snapshot.GuideId);
                w.WriteNumber("edges", snapshot.Edges.Count);
                w.WriteNumber("matched", report.EdgesMatched);
                w.WriteNumber("policies", snapshot.Policies.Count);
                w.WriteEndObject();
            }
            else w.WriteBoolean("guide", false);
            w.WriteString("scope", InspectVisible.Name(options.Scope));
            if (!snapshot.RealText) w.WriteString("textRules", "review: the headless text model measures trims and ink, so text.trim and text.clip only review");
            w.WriteStartObject("summary");
            w.WriteNumber("error", report.Count(InspectLint.Severity.Error));
            w.WriteNumber("warn", report.Count(InspectLint.Severity.Warn));
            w.WriteNumber("info", report.Count(InspectLint.Severity.Info));
            w.WriteNumber("nodes", snapshot.Nodes.Count);
            w.WriteNumber("edges", snapshot.Edges.Count);
            w.WriteNumber("inScope", report.InScope);
            w.WriteEndObject();
            w.WriteStartObject("outcomes");
            w.WriteNumber("fail", report.CountAll(InspectLint.Outcome.Fail));
            w.WriteNumber("review", report.CountAll(InspectLint.Outcome.Review));
            w.WriteNumber("suppressed", report.CountAll(InspectLint.Outcome.Suppressed));
            w.WriteEndObject();
            w.WriteStartObject("levels");
            w.WriteNumber("gate", report.CountAll(InspectLint.Level.Gate));
            w.WriteNumber("todo", report.CountAll(InspectLint.Level.Todo));
            w.WriteEndObject();
            w.WriteStartObject("suppressed");
            w.WriteNumber("intent", report.CountSuppressed("intent"));
            w.WriteNumber("baseline", report.CountSuppressed("baseline"));
            w.WriteEndObject();
            if (options.BaselineFile.Length > 0)
            {
                w.WriteStartObject("baseline");
                w.WriteString("file", options.BaselineFile);
                w.WriteNumber("entries", options.Baseline.Count);
                w.WriteNumber("matched", report.BaselineMatched);
                w.WriteEndObject();
            }
            w.WriteStartObject("rules");
            var written = new List<string>();
            foreach (InspectLint.RuleSpec spec in InspectLint.Catalog)
            {
                string name = InspectLint.ReportName(spec.Id);
                if (written.Contains(name)) continue;
                var ids = new List<string>();
                foreach (InspectLint.RuleSpec other in InspectLint.Catalog)
                    if (string.Equals(InspectLint.ReportName(other.Id), name, StringComparison.Ordinal) && options.Rules.Contains(other.Id)) ids.Add(other.Id);
                if (ids.Count == 0) continue;
                written.Add(name);
                int looked = 0;
                foreach (string id in ids) looked += report.Checked.TryGetValue(id, out int c) ? c : 0;
                int review = 0, suppressed = 0;
                foreach (InspectLint.Finding f in report.All)
                {
                    if (!ids.Contains(f.Id)) continue;
                    if (f.Outcome == InspectLint.Outcome.Review) review++;
                    else if (f.Outcome == InspectLint.Outcome.Suppressed) suppressed++;
                }
                w.WriteStartObject(name);
                w.WriteNumber("checked", looked);
                w.WriteNumber("error", report.Count(name, InspectLint.Severity.Error));
                w.WriteNumber("warn", report.Count(name, InspectLint.Severity.Warn));
                w.WriteNumber("info", report.Count(name, InspectLint.Severity.Info));
                w.WriteNumber("review", review);
                w.WriteNumber("suppressed", suppressed);
                w.WriteString("level", InspectLint.Name(spec.Level));
                w.WriteStartArray("ids");
                foreach (string id in ids) w.WriteStringValue(id);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            if (report.Truncated.Count > 0)
            {
                w.WriteStartArray("truncated");
                foreach (string rule in report.Truncated) w.WriteStringValue(rule);
                w.WriteEndArray();
            }
            w.WriteStartArray("findings");
            foreach (InspectLint.Finding f in report.Findings) WriteFinding(w, f);
            w.WriteEndArray();
        }

        /// <summary>Into the open object: the window the lint ran in (<c>theme</c>, <c>tier</c>, <c>size</c>), the route, where trims come from
        /// (<c>trimSource</c>), the devices, and the saved snapshot a replay read (<c>replay</c>).</summary>
        public static void WriteHead(Utf8JsonWriter w, InspectLint.Snapshot snapshot)
        {
            w.WriteString("theme", snapshot.Theme);
            w.WriteString("tier", snapshot.Tier.ToString());
            w.WriteStartObject("size");
            Number(w, "w", snapshot.Width);
            Number(w, "h", snapshot.Height);
            w.WriteEndObject();
            w.WriteString("route", snapshot.Route);
            w.WriteString("trimSource", snapshot.TrimSource);
            w.WriteString("devices", snapshot.Devices);
            if (snapshot.Source.Length > 0) w.WriteString("replay", snapshot.Source);
        }
    }

    public sealed partial class InspectSession
    {
        /// <summary>The frame the contrast rule reads its grounds from: one frame presented at rest (as `render` does), read back from the offscreen
        /// surface. Null when the mount has no surface (`gpu=none`, headless text) or the capture is refused: the rule then checks nothing.</summary>
        InspectLint.Pixels? RenderedFrame(Shell.HeadlessShell shell)
        {
            if (_surface is not { } surface) return null;
            try
            {
                RunFrames(1);
                byte[] bgra = surface.CaptureBgra(out int w, out int h);
                float dipW = shell.Window.ClientSizePx.Width / Math.Max(0.01f, shell.Window.Scale);
                return new InspectLint.Pixels(bgra, w, h, dipW > 0f ? w / dipW : 1f);
            }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>`lint`: capture the mounted scene (or the subtree `root` names), or read a saved snapshot (`replay`), run the rules, reply with
        /// the report; `save` writes the snapshot the rules saw. Reads the scene as it is: settle first. A replay needs no mount (pass a null shell).</summary>
        string Lint(int id, string name, Inspect.Args a, Shell.HeadlessShell? shell)
        {
            if (!InspectLintScene.TryOptions(a, out InspectLint.Options options, out string error)) return Inspect.Reply.Fail(id, name, error);
            InspectLint.Snapshot snapshot;
            if (a.Has("replay"))
            {
                if (!InspectLintStore.TryLoad(a.Str("replay"), out snapshot, out error)) return Inspect.Reply.Fail(id, name, error);
            }
            else
            {
                if (shell is null) return Inspect.Reply.Fail(id, name, "nothing is mounted (mount first, or lint replay=<file>)");
                if (!TryRoots(a, shell, out List<NodeHandle> roots, out error)) return Inspect.Reply.Fail(id, name, error);
                bool wantPixels = options.Enabled(InspectLint.Rules.Contrast) || a.Has("save");
                InspectLint.Pixels? pixels = wantPixels ? RenderedFrame(shell) : null;
                snapshot = InspectLintScene.Capture(shell, CollectionsMarshal.AsSpan(roots), pixels, Devices);
            }
            InspectLint.Report report = InspectLint.Run(snapshot, options);
            string saved = "";
            if (a.Has("save"))
            {
                try { saved = InspectLintStore.Save(report.Prepared ?? snapshot, a.Str("save")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return Inspect.Reply.Fail(id, name, "save: " + ex.Message);
                }
            }
            return Inspect.Reply.Ok(id, name, w =>
            {
                InspectLintScene.WriteHead(w, snapshot);
                if (saved.Length > 0) w.WriteString("saved", saved);
                InspectLintScene.WriteReport(w, report.Prepared ?? snapshot, report, options);
            });
        }
    }
}
