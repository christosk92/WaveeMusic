// ── Screens/Diagnostics.Inspect.Lint.Scene.cs — the `lint` verb: the mounted scene into a snapshot, the report into JSON ───────
// `lint {rules?, root?, nth?, min?, limit?, typeFloor?, targetFloor?, targetFloorCompact?, gapMin?, allowTrim?, allowDuplicate?, ignoreUnder?}`
//
// Role: SHELL (UI thread; reads the engine's public scene inspection only, like the dump)
// Spec: handoff/themes-2026-10-17/agent-inspect-plan.md lane A3; the rules are Diagnostics.Inspect.Lint.cs (pure).
//
// THE CAPTURE walks the scene exactly as `dump` does (visible, unparked, non-transparent subtrees; a node counts once it has a box; the
// overlays that do not hang under the root follow) and keeps what the rules read: the presented rect, clip and scroll axes, the text run
// (the dump's style + the engine's trim query), the click targets with their label, and the active theme's edge expectations.

using System.Runtime.InteropServices;
using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Text;
using Wavee.Theming;

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

        /// <summary>The lint options an args bag spells: <c>rules</c>, <c>min</c> (info|warn|error), <c>limit</c> (findings per rule), <c>typeFloor</c>,
        /// <c>targetFloor</c>, <c>targetFloorCompact</c>, <c>gapMin</c>, <c>allowTrim</c>, <c>allowDuplicate</c>, <c>ignoreUnder</c>.</summary>
        public static bool TryOptions(Inspect.Args a, out InspectLint.Options options, out string error)
        {
            error = "";
            options = new InspectLint.Options();
            IReadOnlyCollection<string> rules = InspectLint.RuleNames;
            if (a.Has("rules"))
            {
                List<string> asked = ListOf(a, "rules");
                foreach (string rule in asked)
                {
                    if (Array.IndexOf(InspectLint.RuleNames, rule.ToLowerInvariant()) >= 0) continue;
                    error = "unknown lint rule '" + rule + "' (" + string.Join("|", InspectLint.RuleNames) + ")";
                    return false;
                }
                if (asked.Count > 0) rules = asked.ConvertAll(static r => r.ToLowerInvariant());
            }
            InspectLint.Severity min = InspectLint.Severity.Info;
            if (a.Has("min") && !InspectLint.TryParseSeverity(a.Str("min"), out min))
            {
                error = "min is info, warn or error";
                return false;
            }
            options = new InspectLint.Options
            {
                Rules = rules,
                TypeFloor = a.Float("typeFloor", 11f),
                TargetFloor = a.Float("targetFloor", 32f),
                TargetFloorCompact = a.Float("targetFloorCompact", 40f),
                GapMin = a.Float("gapMin", 2f),
                AllowTrim = ListOf(a, "allowTrim"),
                AllowDuplicate = ListOf(a, "allowDuplicate"),
                IgnoreUnder = a.Has("ignoreUnder") ? ListOf(a, "ignoreUnder") : InspectLint.DefaultIgnoreUnder,
                MaxPerRule = Math.Clamp(a.Int("limit", 100), 1, 10_000),
                Min = min,
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
                Glyph = IsGlyph(value),
                Spans = spans,
                Color = paint.TextColor,
            };
        }

        /// <summary>The press targets under an invisible (opacity 0) <paramref name="root"/>: hit-testable, so their real hits still beat a neighbour's slop.</summary>
        static void CollectHitBlockers(SceneStore scene, NodeHandle root, List<RectF> into)
        {
            const uint press = InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit | InteractionInfo.DragBit | InteractionInfo.GestureBit;
            var stack = new Stack<NodeHandle>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                NodeHandle node = stack.Pop();
                if (node.IsNull || !scene.IsLive(node)) continue;
                NodeFlags flags = scene.Flags(node);
                if ((flags & (NodeFlags.Visible | NodeFlags.HitTestVisible)) != (NodeFlags.Visible | NodeFlags.HitTestVisible) || (flags & NodeFlags.Parked) != 0) continue;
                RectF rect = scene.PresentedAbsoluteRect(node);
                if (rect.W > 0f && rect.H > 0f && (scene.Interaction(node).HandlerMask & press) != 0 && (flags & NodeFlags.Disabled) == 0) into.Add(rect);
                for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
            }
        }

        /// <summary>The visible nodes under <paramref name="roots"/> as a lint snapshot, with the active theme's edge expectations. UI thread, between frames.</summary>
        public static InspectLint.Snapshot Capture(Shell.HeadlessShell shell, ReadOnlySpan<NodeHandle> roots, InspectLint.Pixels? pixels = null)
        {
            SceneStore scene = shell.Host.Scene;
            StringTable strings = scene.Strings ?? Entities.Strings;
            var nodes = new List<InspectLint.Node>();
            var stack = new Stack<(NodeHandle Node, int Parent, int Depth, float Opacity, bool Inert)>();
            var children = new List<NodeHandle>();
            var blockers = new List<RectF>();
            Span<char> path = stackalloc char[256];
            for (int r = roots.Length - 1; r >= 0; r--) stack.Push((roots[r], -1, 0, 1f, false));
            while (stack.Count > 0)
            {
                var (node, parent, depth, above, inertAbove) = stack.Pop();
                if (node.IsNull || !scene.IsLive(node)) continue;
                NodeFlags flags = scene.Flags(node);
                ref NodePaint paint = ref scene.Paint(node);
                if ((flags & NodeFlags.Visible) == 0 || (flags & (NodeFlags.Parked | NodeFlags.Exiting)) != 0) continue;
                if (paint.Opacity <= 0f)
                {
                    // Paints nothing, but the hit test is box and clip, never paint: its targets (an auto-hidden scroll bar) still take presses.
                    CollectHitBlockers(scene, node, blockers);
                    continue;
                }
                RectF rect = scene.PresentedAbsoluteRect(node);
                // A clipping box with no area shows nothing of its subtree (a closed seam's 0-wide strip): none of it is on screen, so none of it is linted.
                if ((rect.W <= 0f || rect.H <= 0f) && (flags & NodeFlags.ClipsToBounds) != 0) continue;
                float opacity = above * paint.Opacity;
                bool inert = inertAbove || (flags & NodeFlags.Disabled) != 0;
                int self = parent;
                if (rect.W > 0f && rect.H > 0f)
                {
                    self = nodes.Count;
                    ref InteractionInfo it = ref scene.Interaction(node);
                    bool click = (it.HandlerMask & InteractionInfo.ClickBit) != 0;
                    bool interactive = (flags & NodeFlags.HitTestVisible) != 0 && (click || IsActionRole(it.Role));
                    // What the hit test adds outside the box: only a whole-box target takes hits in its slop (InputDispatcher.Hit).
                    const uint slopHandlers = InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit
                        | InteractionInfo.DragBit | InteractionInfo.GestureBit | InteractionInfo.ContextBit;
                    Edges4 slop = interactive && (it.HandlerMask & slopHandlers) != 0 && (flags & NodeFlags.Disabled) == 0
                        ? shell.Host.Input.EffectiveHitSlop(node, rect.W, rect.H) : default;
                    int axis = 0;
                    if (scene.TryGetScroll(node, out ScrollState scroll)) axis = scroll.Orientation == 1 ? 1 : 2;
                    int pathLength = NodeDescriber.Describe(scene, (int)node.Raw.Index, node.Raw.Gen, path);
                    nodes.Add(new InspectLint.Node(self, parent, scene.DebugKeyOf(node), new string(path[..pathLength]), rect)
                    {
                        Depth = depth,
                        Kind = NodeDescriber.ElementTypeName(scene.ElementTypeId(node)),
                        Clips = (flags & NodeFlags.ClipsToBounds) != 0,
                        ScrollAxis = axis,
                        ZStack = (flags & NodeFlags.ZStack) != 0,
                        Column = scene.Layout(node).Direction == 1,
                        Interactive = interactive,
                        Slop = slop,
                        Pressable = (it.HandlerMask & slopHandlers) != 0,
                        Disabled = (flags & NodeFlags.Disabled) != 0,
                        Role = it.Role == AutomationRole.None ? "" : it.Role.ToString(),
                        Label = interactive ? LabelOf(scene, strings, node) : "",
                        Animating = shell.Host.Animation.HasTracks(node),
                        Opacity = opacity,
                        Inert = inert,
                        Sticky = (flags & NodeFlags.StickyPinned) != 0,
                        Text = paint.VisualKind == VisualKind.Text ? TextOf(shell, node, ref paint, strings) : null,
                    });
                }
                children.Clear();
                for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c)) children.Add(c);
                for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], self, self == parent ? depth : depth + 1, opacity, inert));
            }

            // The words under each node (a text block is a node with exactly one text run below it): children follow their parent, so one backwards pass.
            var counts = new int[nodes.Count];
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                InspectLint.Node n = nodes[i];
                if (n.Text is { Glyph: false } t && (t.String.Length > 0 || t.Spans)) counts[i]++;
                if (n.Parent >= 0) counts[n.Parent] += counts[i];
            }
            for (int i = 0; i < nodes.Count; i++) nodes[i] = nodes[i] with { TextCount = counts[i] };

            TierFacts tiers = ThemeHost.Facts.Peek().Tiers;
            float scale = Math.Max(0.01f, shell.Window.Scale);
            float width = shell.Window.ClientSizePx.Width / scale, height = shell.Window.ClientSizePx.Height / scale;
            IReadOnlyList<LintEdge> edges = [];
            IReadOnlyList<string> bleeds = ThemeInspection.ActiveLintGuide?.DesignedTopBleeds ?? [];
            if (ThemeInspection.ActiveLintGuide is { } guide)
            {
                RectF? RectOfKey(string key)
                {
                    NodeHandle found = shell.Find(key);
                    return found.IsNull ? (RectF?)null : scene.PresentedAbsoluteRect(found);
                }
                edges = guide.Edges(new LintContext(tiers.Window, tiers.Height, width, height, RectOfKey));
            }
            return new InspectLint.Snapshot(width, height, tiers.Window, nodes) { Edges = edges, TopBleeds = bleeds, HitBlockers = blockers, OverScrollbarLane = shell.Host.Input.IsOverScrollbarLane, Theme = InspectKit.ActiveThemeId, Backdrop = pixels };
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

        /// <summary>One finding: <c>{rule, severity, message, keys, measured?, expected?}</c>.</summary>
        public static void WriteFinding(Utf8JsonWriter w, InspectLint.Finding f)
        {
            w.WriteStartObject();
            w.WriteString("rule", f.Rule);
            w.WriteString("severity", InspectLint.Name(f.Severity));
            w.WriteString("message", f.Message);
            w.WriteStartArray("keys");
            foreach (string key in f.Keys) w.WriteStringValue(key);
            w.WriteEndArray();
            WriteMetrics(w, "measured", f.Measured);
            WriteMetrics(w, "expected", f.Expected);
            w.WriteEndObject();
        }

        /// <summary>Into the open object: <c>summary</c>, <c>rules</c> (per rule: <c>checked</c> and its counts), <c>truncated</c>? and <c>findings</c>.</summary>
        public static void WriteReport(Utf8JsonWriter w, InspectLint.Snapshot snapshot, InspectLint.Report report, InspectLint.Options options)
        {
            w.WriteStartObject("summary");
            w.WriteNumber("error", report.Count(InspectLint.Severity.Error));
            w.WriteNumber("warn", report.Count(InspectLint.Severity.Warn));
            w.WriteNumber("info", report.Count(InspectLint.Severity.Info));
            w.WriteNumber("nodes", snapshot.Nodes.Count);
            w.WriteNumber("edges", snapshot.Edges.Count);
            w.WriteEndObject();
            w.WriteStartObject("rules");
            foreach (string rule in InspectLint.RuleNames)
            {
                if (!options.Enabled(rule)) continue;
                w.WriteStartObject(rule);
                w.WriteNumber("checked", report.Checked.TryGetValue(rule, out int looked) ? looked : 0);
                w.WriteNumber("error", report.Count(rule, InspectLint.Severity.Error));
                w.WriteNumber("warn", report.Count(rule, InspectLint.Severity.Warn));
                w.WriteNumber("info", report.Count(rule, InspectLint.Severity.Info));
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

        /// <summary>Into the open object: the window the lint ran in (<c>theme</c>, <c>tier</c>, <c>size</c>, <c>guide</c>).</summary>
        public static void WriteHead(Utf8JsonWriter w, InspectLint.Snapshot snapshot)
        {
            w.WriteString("theme", snapshot.Theme);
            w.WriteString("tier", snapshot.Tier.ToString());
            w.WriteStartObject("size");
            Number(w, "w", snapshot.Width);
            Number(w, "h", snapshot.Height);
            w.WriteEndObject();
            w.WriteBoolean("guide", snapshot.Edges.Count > 0);
        }
    }

    public sealed partial class InspectSession
    {
        /// <summary>The frame the contrast rule reads its backgrounds from: one frame presented at rest (as `render` does), read back from the offscreen
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

        /// <summary>`lint`: capture the mounted scene (or the subtree `root` names), run the rules, reply with the report. Reads the scene as it is: settle first.</summary>
        string Lint(int id, string name, Inspect.Args a, Shell.HeadlessShell shell)
        {
            if (!InspectLintScene.TryOptions(a, out InspectLint.Options options, out string error)) return Inspect.Reply.Fail(id, name, error);
            if (!TryRoots(a, shell, out List<NodeHandle> roots, out error)) return Inspect.Reply.Fail(id, name, error);
            InspectLint.Pixels? pixels = options.Enabled(InspectLint.Rules.Contrast) ? RenderedFrame(shell) : null;
            InspectLint.Snapshot snapshot = InspectLintScene.Capture(shell, CollectionsMarshal.AsSpan(roots), pixels);
            InspectLint.Report report = InspectLint.Run(snapshot, options);
            return Inspect.Reply.Ok(id, name, w =>
            {
                InspectLintScene.WriteHead(w, snapshot);
                InspectLintScene.WriteReport(w, snapshot, report, options);
            });
        }
    }
}
