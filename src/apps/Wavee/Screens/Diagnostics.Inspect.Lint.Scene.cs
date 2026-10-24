// ── Screens/Diagnostics.Inspect.Lint.Scene.cs — the `lint` verb: the mounted scene into a snapshot, the report into JSON ───────
// `lint {rules?, scope?, root?, nth?, min?, limit?, outcomes?, baseline?, save?, replay?, typeFloor?, targetFloor?, targetFloorCompact?,
//        gapMin?, allowTrim?, allowDuplicate?, ignoreUnder?}`   (docs/guide/agent-lint.md is the reference)
//
// Role: SHELL (UI thread; reads the engine's public scene inspection only, like the dump)
// Spec: scratch/agentic-e2e/PLAN.md 4.4-4.5 and 5.1-5.5; the rules are Diagnostics.Inspect.Lint*.cs (pure).
//
// THE CAPTURE walks the scene exactly as `dump` does (visible, unparked, non-transparent subtrees; a node counts once it has a box, or when it
// clips with none: a collapsed clip hides its children; the overlays that do not hang under the root follow; a `root=` subtree starts from the
// cut its live ancestors put on it) and keeps what the rules read: the presented rect, clip and scroll axes (with the scroll offset, its
// range, any edge fade and whether it is still moving), an authored clip, the DECLARED layout (gap, padding, margin, sizes, flex, alignment, wrap), the paint
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
using FluentGpu.Input;
using FluentGpu.Scene;
using FluentGpu.Text;
using Wavee.Theming;
using Wavee.Theming.Contracts;
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

        /// <summary>A list argument read strictly: a comma-separated string or a JSON array whose entries are all non-empty strings. False with a
        /// reason for anything else (a number in the array; an empty list unless <paramref name="allowEmpty"/>: `allowTrim=[]` and
        /// `ignoreUnder=` mean "none", which is how a caller clears a default).</summary>
        public static bool TryListOf(Inspect.Args a, string name, out List<string> list, out string error, bool allowEmpty = false)
        {
            list = [];
            error = "";
            if (!a.TryGet(name, out Inspect.Value value)) return true;
            if (value.Kind == Inspect.ValueKind.Json)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(value.Str);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) { error = name + " is a list (a comma list or a JSON array of strings)"; return false; }
                    foreach (JsonElement e in doc.RootElement.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.String || e.GetString() is not { Length: > 0 } s) { error = name + " lists strings only, not " + e.GetRawText(); return false; }
                        list.Add(s);
                    }
                }
                catch (JsonException) { error = name + " is not a JSON array"; return false; }
            }
            else
                foreach (string part in value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    list.Add(part);
            if (list.Count == 0 && !allowEmpty) { error = name + " is empty"; return false; }
            return true;
        }

        /// <summary>A file path argument: absent is fine (""); present, it must be a non-empty string (`save=true` or `baseline=5` is refused, not
        /// written to a file named "true").</summary>
        public static bool TryPath(Inspect.Args a, string name, out string path, out string error)
        {
            path = "";
            error = "";
            if (!a.TryGet(name, out Inspect.Value value)) return true;
            if (value.Kind != Inspect.ValueKind.String || value.Str.Trim().Length == 0)
            {
                error = name + " is a file path (a non-empty string), not " + (value.Kind == Inspect.ValueKind.String ? "''" : value.ToString());
                return false;
            }
            path = value.Str;
            return true;
        }

        /// <summary>The most findings `limit` lists per rule name.</summary>
        public const int MaxLimit = 10_000;

        /// <summary>A number argument read strictly: absent gives <paramref name="fallback"/>; present, it must be a finite number at or above
        /// <paramref name="min"/> (and above it when <paramref name="exclusive"/>).</summary>
        static bool TryNumber(Inspect.Args a, string name, float fallback, float min, bool exclusive, out float value, out string error)
        {
            value = fallback;
            error = "";
            if (!a.Has(name)) return true;
            if (!a.TryNum(name, out double d) || !double.IsFinite(d) || d < min || (exclusive && d <= min))
            {
                error = name + " is a number " + (exclusive ? "above " : "at or above ") + min.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", not '" + a.Str(name) + "'";
                return false;
            }
            value = (float)d;
            return true;
        }

        /// <summary>Every arg `lint` reads: the options below, plus `root`, `nth`, `save` and `replay`. The verb table
        /// (Diagnostics.Inspect.Protocol.cs: `s_lintArgs`, `s_lintOnlyArgs` and lint's own root and nth) declares the same names, so `help verb=lint`
        /// lists them and any other name is refused before the lint runs; `crossing` declares only `s_lintArgs`, so it refuses `save` and
        /// `replay`.</summary>
        public static readonly string[] ArgNames =
        [
            "rules", "scope", "root", "nth", "min", "limit", "outcomes", "baseline", "save", "replay", "frame", "typeFloor", "targetFloor",
            "targetFloorCompact", "gapMin", "allowTrim", "allowDuplicate", "ignoreUnder", "moving",
        ];

        /// <summary>The lint options an args bag spells: <c>rules</c> (ids, today's names, families: <c>align</c>, <c>all</c>), <c>scope</c>
        /// (screen|content|all), <c>min</c> (info|warn|error), <c>limit</c> (a whole number of findings listed per rule, clamped to 1..10000 as it
        /// always was), <c>outcomes</c> (fail,review,suppressed,report or all: which are LISTED; every one is counted), <c>baseline</c> (a
        /// JSON-lines file), <c>typeFloor</c>, <c>targetFloor</c>, <c>targetFloorCompact</c>, <c>gapMin</c>, <c>allowTrim</c>,
        /// <c>allowDuplicate</c>, <c>ignoreUnder</c> (lists; an empty one means none). Unknown arg names are not checked here: `crossing` passes
        /// its own args through this too, and the verb table refuses them before either verb runs (<see cref="ArgNames"/>).</summary>
        public static bool TryOptions(Inspect.Args a, out InspectLint.Options options, out string error)
        {
            error = "";
            options = new InspectLint.Options();
            IReadOnlyCollection<string> rules = InspectLint.RuleIds;
            if (a.Has("rules"))
            {
                if (!TryListOf(a, "rules", out List<string> words, out error)) return false;
                var ids = new List<string>();
                foreach (string rule in words)
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
            IReadOnlyCollection<InspectLint.Outcome> outcomes = InspectLint.DefaultOutcomes;
            if (a.Has("outcomes"))
            {
                if (!TryListOf(a, "outcomes", out List<string> words, out error)) return false;
                var listed = new List<InspectLint.Outcome>();
                foreach (string word in words)
                {
                    if (string.Equals(word, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        listed = [.. InspectLint.AllOutcomes];
                        break;
                    }
                    if (!InspectLint.TryParseOutcome(word, out InspectLint.Outcome o))
                    {
                        error = "outcomes lists fail, review, suppressed, report (or all)";
                        return false;
                    }
                    if (!listed.Contains(o)) listed.Add(o);
                }
                outcomes = listed;
            }
            if (!TryNumber(a, "typeFloor", 11f, 0f, exclusive: true, out float typeFloor, out error)
                || !TryNumber(a, "targetFloor", 32f, 0f, exclusive: true, out float targetFloor, out error)
                || !TryNumber(a, "targetFloorCompact", 40f, 0f, exclusive: true, out float targetFloorCompact, out error)
                || !TryNumber(a, "gapMin", 2f, 0f, exclusive: false, out float gapMin, out error))
                return false;
            int limit = 100;
            if (a.Has("limit"))
            {
                // A whole number; 0 or less lists one and more than the cap lists the cap (the clamp `limit` always had).
                if (!a.TryNum("limit", out double l) || !double.IsFinite(l) || l != Math.Floor(l))
                {
                    error = "limit is a whole number (clamped to 1.." + MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "), not '" + a.Str("limit") + "'";
                    return false;
                }
                limit = (int)Math.Clamp(l, 1d, MaxLimit);
            }
            List<string> allowTrim = [], allowDuplicate = [], ignoreUnder = [];
            if ((a.Has("allowTrim") && !TryListOf(a, "allowTrim", out allowTrim, out error, allowEmpty: true))
                || (a.Has("allowDuplicate") && !TryListOf(a, "allowDuplicate", out allowDuplicate, out error, allowEmpty: true))
                || (a.Has("ignoreUnder") && !TryListOf(a, "ignoreUnder", out ignoreUnder, out error, allowEmpty: true)))
                return false;
            string moving = a.Str("moving", "review").ToLowerInvariant();
            if (moving is not ("review" or "judge"))
            {
                error = "moving is review or judge";
                return false;
            }
            List<InspectLint.BaselineEntry> baseline = [];
            if (!TryPath(a, "baseline", out string baselineFile, out error)) return false;
            if (baselineFile.Length > 0 && !InspectLintStore.TryReadBaseline(baselineFile, out baseline, out error)) return false;
            options = new InspectLint.Options
            {
                Rules = rules,
                Scope = scope,
                TypeFloor = typeFloor,
                TargetFloor = targetFloor,
                TargetFloorCompact = targetFloorCompact,
                GapMin = gapMin,
                AllowTrim = allowTrim,
                AllowDuplicate = allowDuplicate,
                IgnoreUnder = a.Has("ignoreUnder") ? ignoreUnder : InspectLint.DefaultIgnoreUnder,
                MaxPerRule = limit,
                Min = min,
                Outcomes = outcomes,
                Baseline = baseline,
                BaselineFile = baselineFile,
                JudgeMoving = moving == "judge",
            };
            return true;
        }

        // ── the capture ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A private-use character or two: an icon glyph, not words.</summary>
        public static bool IsGlyph(string text) => InspectLint.IsGlyphText(text);

        /// <summary>The icon name of a glyph label in its face (<c>InspectDump.TryGlyphName</c>), "" when it is words or no named icon.</summary>
        static string GlyphNameOf(string text, string? family) => IsGlyph(text) && InspectDump.TryGlyphName(text, family, out string? name) ? name : "";

        static bool IsActionRole(AutomationRole role) => role is AutomationRole.Button or AutomationRole.ToggleButton or AutomationRole.NavigationItem
            or AutomationRole.CheckBox or AutomationRole.RadioButton or AutomationRole.Hyperlink or AutomationRole.ToggleSwitch
            or AutomationRole.MenuItem or AutomationRole.Tab or AutomationRole.ComboBox or AutomationRole.Slider or AutomationRole.ScrollBar;

        /// <summary>The first text at or under <paramref name="node"/>: a button's label, or its glyph (<paramref name="family"/>: its face).</summary>
        static string LabelOf(SceneStore scene, StringTable strings, NodeHandle node, out string? family)
        {
            foreach (NodeHandle n in InspectKit.Subtree(scene, node))
            {
                ref NodePaint p = ref scene.Paint(n);
                if (p.VisualKind != VisualKind.Text || p.Text.Value <= 0) continue;
                family = InspectDump.FamilyOf(strings, in scene.Layout(n).TextStyle);
                return strings.Resolve(p.Text);
            }
            family = null;
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
            // The glyphs' own ink (their black boxes), relative to the box: what text.clip judges a cut by (the line box carries leading
            // above the caps and descent under the baseline that no glyph fills).
            RectF ink = default;
            bool inked = !glyph && shell.Host.TryQueryTextInk(node, out ink);
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
                InkTop = inked ? ink.Y : float.NaN,
                InkBottom = inked ? ink.Bottom : float.NaN,
                OverflowX = overflow.HorizontalOverflow,
                OverflowY = overflow.VerticalOverflow,
                Glyph = glyph,
                Copy = !glyph && IsCopy(value),
                Case = style.Case == TextCase.AsWritten ? "" : style.Case.ToString(),
                GlyphName = glyph ? GlyphNameOf(value, InspectDump.FamilyOf(strings, in style)) : "",
                Spans = spans,
                Color = paint.TextColor,
                Align = style.Align.ToString(),
            };
        }

        /// <summary>The base culture's strings, case-folded (assets/loc/en-US.json beside the exe, read once): what makes a run the app's own copy
        /// (<see cref="InspectLint.TextRun.Copy"/>). Empty when the file cannot be read: no run is copy, and <c>type.voice</c> judges nothing.</summary>
        static HashSet<string>? s_copy;

        static bool IsCopy(string text)
        {
            if (text.Length is 0 or > 80) return false;
            s_copy ??= LoadCopy();
            return s_copy.Contains(text.Trim().ToLowerInvariant());
        }

        static HashSet<string> LoadCopy()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string file = Path.Combine(AppContext.BaseDirectory, "assets", "loc", "en-US.json");
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file));
                var stack = new Stack<JsonElement>();
                stack.Push(doc.RootElement);
                while (stack.Count > 0)
                {
                    JsonElement e = stack.Pop();
                    if (e.ValueKind == JsonValueKind.Object)
                        foreach (JsonProperty p in e.EnumerateObject())
                        {
                            if (p.Name.StartsWith('$')) continue;   // $culture, $comment, $unusedAllow: the file's own notes
                            stack.Push(p.Value);
                        }
                    else if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= 80 } v && v.IndexOf('{') < 0)
                        set.Add(v.Trim().ToLowerInvariant());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            return set;
        }

        /// <summary>`lint replay=<snapshot> frame=<png>`: the pixels of a `render` of the same frame, so the rules that read the rendered frame
        /// (contrast, the picture rules) judge a saved snapshot too: an older exe's capture with its render, or a page you can no longer mount.
        /// The PNG is decoded with WIC at its own size; its scale is its width over the snapshot's width in DIP.</summary>
        public static bool TryReadFrame(string path, float widthDip, out InspectLint.Pixels? pixels, out string error)
        {
            pixels = null;
            error = "";
            string full = Path.GetFullPath(path);
            byte[] png;
            try { png = File.ReadAllBytes(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = "frame: " + ex.Message;
                return false;
            }
            // the PNG's own size: the IHDR chunk follows the 8-byte signature (width and height are big-endian at 16 and 20)
            if (png.Length < 24 || png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
            {
                error = "frame: '" + full + "' is not a PNG";
                return false;
            }
            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            if (w <= 0 || h <= 0 || (long)w * h > 64L * 1024 * 1024)
            {
                error = "frame: '" + full + "' has an unusable size " + w + " x " + h;
                return false;
            }
            var bgra = new byte[w * h * 4];
            using var codec = new FluentGpu.Media.Codecs.Wic.WicImageCodec();
            if (!codec.DecodeConstrained(png, w, h, bgra, out int dw, out int dh) || dw != w || dh != h)
            {
                error = "frame: '" + full + "' did not decode";
                return false;
            }
            pixels = new InspectLint.Pixels(bgra, w, h, widthDip > 0f ? w / widthDip : 1f);
            return true;
        }

        /// <summary>The right edges the active guide declares at Standard, moved by how far this tier's left lead sits from Standard's: a right
        /// edge per left lead the guide mirrors at Standard (band.last-action against band.first-pivot, page.column against itself). Empty below
        /// Huge, or when the guide declares right edges at this tier itself.</summary>
        static IReadOnlyList<LintEdge> MirrorEdges(IThemeLintGuide guide, in LintContext here, IReadOnlyList<LintEdge> edges)
        {
            if (here.Window is not (WidthTier.Huge or WidthTier.Huge4K)) return [];
            foreach (LintEdge e in edges)
                if (e.Side == LintSide.Right && !e.Advisory) return [];
            IReadOnlyList<LintEdge> standard = guide.Edges(here with { Window = WidthTier.Standard });
            float LeftOf(IReadOnlyList<LintEdge> list, string id)
            {
                foreach (LintEdge e in list)
                    if (e.Side == LintSide.Left && string.Equals(e.Id, id, StringComparison.Ordinal)) return e.Expected;
                return float.NaN;
            }
            var mirrored = new List<LintEdge>();
            foreach (LintEdge e in standard)
            {
                if (e.Side != LintSide.Right || e.Advisory) continue;
                string pair = float.IsFinite(LeftOf(standard, e.Id)) ? e.Id : "band.first-pivot";
                float shift = LeftOf(edges, pair) - LeftOf(standard, pair);
                if (!float.IsFinite(shift)) continue;
                mirrored.Add(e with
                {
                    Expected = e.Expected - shift,
                    What = e.What + " (mirrored from " + pair + ": " + (here.Window == WidthTier.Huge ? "Huge" : "Huge4K") + " leads " + shift.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " past Standard)",
                });
            }
            return mirrored;
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

        /// <summary>The theme's design scale as named values: the active definition's <c>Spacing</c> rungs, the style's <c>R.Space</c> rhythm
        /// and <c>R.Shape</c> radii, and the guide's board steps (<see cref="IThemeLintGuide.BoardScale"/>): what <c>space.scale</c> accepts
        /// besides 2 and the 4-grid up to 64.</summary>
        public static IReadOnlyList<InspectLint.ScaleEntry> ScaleOf()
        {
            SpaceRoles space = R.Space;
            ShapeRoles shape = R.Shape;
            List<InspectLint.ScaleEntry> scale =
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
            // The steps the theme's boards space with beyond the shared tokens (Zune: 6, 10, 14, 18, 22): the guide declares them.
            foreach (LintScaleStep step in ThemeInspection.ActiveLintGuide?.BoardScale ?? [])
                scale.Add(new(step.Name, step.Value));
            return scale;
        }

        /// <summary>What a node draws beyond a fill, a border, a text run or an image: a drawn visual kind (<c>Path</c>, <c>ListRow</c>, ...) by name,
        /// else an SDF arc (<c>Arc</c>: a progress ring), a gradient border (<c>BorderBrush</c>) or a material (<c>Acrylic</c>); "" for none.</summary>
        static string VisualOf(SceneStore scene, NodeHandle node, VisualKind kind)
        {
            if (kind is not (VisualKind.None or VisualKind.Box or VisualKind.Text or VisualKind.Image)) return kind.ToString();
            if (scene.TryGetArc(node, out _)) return "Arc";
            if (scene.TryGetBorderBrush(node, out _)) return "BorderBrush";
            return scene.TryGetAcrylic(node, out _) ? "Acrylic" : "";
        }

        /// <summary>The route as `status` names it.</summary>
        static string RouteNow()
        {
            var r = Wavee.Shell.Current.Peek();
            string? arg = Wavee.Shell.ArgOf(r);
            return Wavee.Shell.NameOf(r) + (arg is null ? "" : " " + arg);
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

        /// <summary>Where a scroll bar's lane starts inside <paramref name="slop"/>, per side, measured out from <paramref name="rect"/> along its
        /// middle line in 1 DIP steps (<c>InputDispatcher.IsOverScrollbarLane</c>: a press there is the bar's before any target's); -1 where no lane
        /// lies within the slop. The top is not walked (a bar's lane runs down a viewport's side or along its foot). Read at capture so a replay
        /// cuts a target's slop as the live lint does.</summary>
        static Edges4 LaneStops(InputDispatcher input, in RectF rect, in Edges4 slop)
        {
            float cy = rect.Y + rect.H / 2f, cx = rect.X + rect.W / 2f;
            float right = -1f, left = -1f, bottom = -1f;
            for (float d = 0f; d < slop.Right; d += 1f)
                if (input.IsOverScrollbarLane(new Point2(rect.Right + d + 0.5f, cy))) { right = d; break; }
            for (float d = 0f; d < slop.Left; d += 1f)
                if (input.IsOverScrollbarLane(new Point2(rect.X - d - 0.5f, cy))) { left = d; break; }
            for (float d = 0f; d < slop.Bottom; d += 1f)
                if (input.IsOverScrollbarLane(new Point2(cx, rect.Bottom + d + 0.5f))) { bottom = d; break; }
            return new Edges4(left, -1f, right, bottom);
        }

        /// <summary>The shell's own intents, under every theme (a theme guide's follow them). The Winamp deck face (`Deck.Faces.cs`, key
        /// <c>deck:winamp@&lt;side&gt;</c>) is a scale model of the 275 x 116 Winamp 2 window: its title bars, LCD title and status letters are the
        /// skin's pixel type at a fixed fraction of the deck's side (6-7 DIP at the rail's 324), part of the device's look and never reading copy
        /// (the same title and time read at full size in the rail beside it). Enlarging them would break the skin's proportions, so the type floor
        /// allows them there (B11, audit B8). A key starting <c>deco:</c> is an illustration, allowed by the contrast rule (audit B8).</summary>
        static readonly IReadOnlyList<LintPolicy> CorePolicies =
        [
            new(LintIntent.Allow, "deck:winamp@", "the Winamp deck's pixel type is the skin's (a scale model of the 275 x 116 window), never reading copy",
                Rule: InspectLint.Rules.TypeFloor),
            // A `deco:` key marks an illustration: a glyph that stands in for art (an event's calendar where it has no photograph, the concerts
            // browse-all tile's pictogram). It carries no information the page does not say in words beside it, so it is not held to text contrast.
            new(LintIntent.Allow, "deco:", "a decorative illustration (a glyph standing in for art), never information",
                Rule: InspectLint.Rules.ContrastText),
        ];

        /// <summary>The visible nodes under <paramref name="roots"/> as a lint snapshot, with the active theme's guide and scale. UI thread, between
        /// frames. <paramref name="pixels"/> is the rendered frame the grounds under the text are read from (null: none read).</summary>
        public static InspectLint.Snapshot Capture(Shell.HeadlessShell shell, ReadOnlySpan<NodeHandle> roots, InspectLint.Pixels? pixels = null, string devices = "")
        {
            SceneStore scene = shell.Host.Scene;
            StringTable strings = scene.Strings ?? Entities.Strings;
            float scale = Math.Max(0.01f, shell.Window.Scale);
            float width = shell.Window.ClientSizePx.Width / scale, height = shell.Window.ClientSizePx.Height / scale;
            var window = new RectF(0f, 0f, width, height);
            var nodes = new List<InspectLint.Node>();
            var clicks = new Dictionary<Delegate, int>();
            var stack = new Stack<(NodeHandle Node, int Parent, int Depth, float Opacity, bool Inert, int Root)>();
            var children = new List<NodeHandle>();
            var blockers = new List<RectF>();
            var exiting = new List<InspectLint.ExitingRun>();
            Span<char> path = stackalloc char[256];
            // A root under live ancestors (`root=`) starts from the cut they put on it, so the subtree is judged as the page shows it.
            var seeds = new (bool Seeded, RectF Screen, RectF Content, bool Shown)[roots.Length];
            for (int r = 0; r < roots.Length; r++)
            {
                seeds[r] = (false, RectF.Infinite, RectF.Infinite, true);
                if (!roots[r].IsNull && scene.IsLive(roots[r]) && !scene.Parent(roots[r]).IsNull)
                {
                    InspectVisible.AncestorCut(scene, roots[r], window, out RectF screen, out RectF content, out bool shown);
                    seeds[r] = (true, screen, content, shown);
                }
            }
            for (int r = roots.Length - 1; r >= 0; r--) stack.Push((roots[r], -1, 0, 1f, false, r));
            while (stack.Count > 0)
            {
                var (node, parent, depth, above, inertAbove, root) = stack.Pop();
                if (node.IsNull || !scene.IsLive(node)) continue;
                NodeFlags flags = scene.Flags(node);
                ref NodePaint paint = ref scene.Paint(node);
                if ((flags & NodeFlags.Visible) == 0 || (flags & NodeFlags.Parked) != 0) continue;
                if ((flags & NodeFlags.Exiting) != 0)
                {
                    // on its way out: not the page, but what it still draws can sit over what replaces it (text.copies)
                    CollectExiting(scene, strings, node, above, KeyAbove(scene, node), default, exiting);
                    continue;
                }
                if (paint.Opacity <= 0f)
                {
                    // Paints nothing, but the hit test is box and clip, never paint: its targets (an auto-hidden scroll bar) still take presses.
                    CollectHitBlockers(scene, node, blockers);
                    continue;
                }
                // A spring mid-flight can overshoot: a presented width below 0, an opacity above 1. The snapshot keeps what the reader accepts
                // (and what the rules mean): an empty box, a whole opacity, so a replay reads the page the live lint judged.
                RectF presented = scene.PresentedAbsoluteRect(node);
                RectF rect = new(presented.X, presented.Y, MathF.Max(0f, presented.W), MathF.Max(0f, presented.H));
                float opacity = Math.Clamp(above * paint.Opacity, 0f, 1f);
                bool inert = inertAbove || (flags & NodeFlags.Disabled) != 0;
                int self = parent;
                bool scrolls = scene.TryGetScroll(node, out ScrollState scroll);
                // A box with no area is skipped like the dump skips it, unless it cuts its children: a collapsed clip hides what is under it.
                bool cuts = (flags & NodeFlags.ClipsToBounds) != 0 || !paint.ClipRect.IsInfinite || scrolls;
                if ((rect.W > 0f && rect.H > 0f) || cuts)
                {
                    self = nodes.Count;
                    ref InteractionInfo it = ref scene.Interaction(node);
                    bool click = (it.HandlerMask & InteractionInfo.ClickBit) != 0;
                    bool drag = (it.HandlerMask & (InteractionInfo.DragBit | InteractionInfo.PointerBit | InteractionInfo.GestureBit)) != 0;
                    bool interactive = (flags & NodeFlags.HitTestVisible) != 0 && (click || IsActionRole(it.Role));
                    int clickId = 0;
                    if (click && scene.GetClickHandler(node) is { } handler && !clicks.TryGetValue(handler, out clickId))
                        clicks[handler] = clickId = clicks.Count + 1;
                    // What the hit test adds outside the box: only a whole-box target takes hits in its slop (InputDispatcher.Hit).
                    const uint slopHandlers = InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit
                        | InteractionInfo.DragBit | InteractionInfo.GestureBit | InteractionInfo.ContextBit;
                    Edges4 slop = interactive && (it.HandlerMask & slopHandlers) != 0 && (flags & NodeFlags.Disabled) == 0
                        ? shell.Host.Input.EffectiveHitSlop(node, rect.W, rect.H) : default;
                    Edges4 lane = slop.Horizontal + slop.Vertical > 0f ? LaneStops(shell.Host.Input, rect, slop) : InspectLint.NoLane;
                    int axis = 0;
                    float offset = 0f, max = 0f;
                    byte fade = 0;
                    float fadeBand = 0f;
                    bool moving = false;
                    if (scrolls)
                    {
                        axis = scroll.Orientation == 1 ? 1 : 2;
                        offset = (float)scroll.Offset;
                        max = scroll.MaxOffset;
                        moving = scroll.Motion.IsMoving || Math.Abs(scroll.Velocity) > 1.0;
                        if (scroll.AutoEdgeFade && scroll.AutoEdgeFadeBand > 0f)
                        {
                            fade |= (byte)(axis == 1 ? EdgeMask.Horizontal : EdgeMask.Vertical);
                            fadeBand = scroll.AutoEdgeFadeBand;
                        }
                    }
                    if (scene.TryGetEdgeFade(node, out EdgeFadeSpec edgeFade) && !edgeFade.IsNone)
                    {
                        fade |= (byte)edgeFade.Edges;
                        fadeBand = MathF.Max(fadeBand, MathF.Max(MathF.Max(edgeFade.BandLeft, edgeFade.BandRight), MathF.Max(edgeFade.BandTop, edgeFade.BandBottom)));
                    }
                    RectF authored = paint.ClipRect;
                    string? labelFamily = null;
                    string label = interactive ? LabelOf(scene, strings, node, out labelFamily) : "";
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
                        ScrollMoving = moving,
                        Seeded = parent < 0 && seeds[root].Seeded,
                        SeedScreen = parent < 0 ? seeds[root].Screen : RectF.Infinite,
                        SeedContent = parent < 0 ? seeds[root].Content : RectF.Infinite,
                        SeedShown = parent >= 0 || seeds[root].Shown,
                        FadeEdges = fade,
                        FadeBand = fadeBand,
                        ClipRect = authored.IsInfinite ? RectF.Infinite : new RectF(rect.X + authored.X, rect.Y + authored.Y, authored.W, authored.H),
                        ZStack = (flags & NodeFlags.ZStack) != 0,
                        Column = layout.Direction == 1,
                        Layout = DeclaredOf(in layout),
                        Interactive = interactive,
                        Slop = slop,
                        Lane = lane,
                        Pressable = (it.HandlerMask & slopHandlers) != 0,
                        Disabled = (flags & NodeFlags.Disabled) != 0,
                        Role = it.Role == AutomationRole.None ? "" : it.Role.ToString(),
                        ClickId = clickId,
                        Drag = drag,
                        Label = label,
                        LabelGlyph = GlyphNameOf(label, labelFamily),
                        Animating = shell.Host.Animation.HasTracks(node),
                        Opacity = opacity,
                        Inert = inert,
                        Sticky = (flags & NodeFlags.StickyPinned) != 0,
                        Fill = paint.Fill,
                        BorderWidth = paint.BorderWidth,
                        BorderColor = paint.BorderColor,
                        Image = paint.VisualKind == VisualKind.Image,
                        Gradient = scene.TryGetGradient(node, out _),
                        Visual = VisualOf(scene, node, paint.VisualKind),
                        Text = paint.VisualKind == VisualKind.Text ? TextOf(shell, node, ref paint, strings) : null,
                    });
                }
                children.Clear();
                for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c)) children.Add(c);
                for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], self, self == parent ? depth : depth + 1, opacity, inert, root));
            }

            // Exit orphans (a crossfade's outgoing line) are detached from the tree while they animate out: their rects are relative to the
            // parent-world origin the scene froze for them.
            for (int o = 0; o < scene.OrphanCount; o++)
            {
                NodeHandle orphan = scene.OrphanAt(o, out float ox, out float oy);
                if (!orphan.IsNull && scene.IsLive(orphan))
                    CollectExiting(scene, strings, orphan, 1f, scene.DebugKeyOf(orphan) ?? "", new Point2(ox, oy), exiting);
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
            // The page the capture SAW: the keep-alive slot (`Shell.SlotKey`: tab, route, arg) showing the most of itself. Mid-navigation the
            // shell already names the target while the old page is still the one on screen; its findings belong to the old page's route.
            string route = RouteNow(), navigating = "";
            string seenName = "", seenArg = "";
            float seenArea = 0f;
            foreach (InspectLint.Node n in nodes)
            {
                if (n.Key is not { } key || !InspectLint.TrySlotRoute(key, out string slotName, out string slotArg)) continue;
                RectF shown = n.Rect.Intersect(window);
                float area = shown.IsEmpty ? 0f : shown.W * shown.H * n.Opacity;
                if (area > seenArea) { seenArea = area; seenName = slotName; seenArg = slotArg; }
            }
            string seen = seenName + (seenArg.Length > 0 ? " " + seenArg : "");
            // A route not keyed by its arg has a slot without it: `artist` holds every artist page's slot, so its name alone must match.
            if (seenName.Length > 0 && !string.Equals(route, seen, StringComparison.Ordinal)
                && !(seenArg.Length == 0 && route.StartsWith(seenName + " ", StringComparison.Ordinal)))
            {
                navigating = route;
                route = seen;
            }
            IReadOnlyList<LintEdge> edges = [];
            IReadOnlyList<LintEdge> mirror = [];
            IReadOnlyList<LintPolicy> policies = CorePolicies;
            string guideId = "";
            IReadOnlyList<string> bleeds = ThemeInspection.ActiveLintGuide?.DesignedTopBleeds ?? [];
            if (ThemeInspection.ActiveLintGuide is { } guide)
            {
                RectF? RectOfKey(string key)
                {
                    NodeHandle found = shell.Find(key);
                    return found.IsNull ? (RectF?)null : scene.PresentedAbsoluteRect(found);
                }
                var context = new LintContext(tiers.Window, tiers.Height, width, height, RectOfKey) { Route = InspectLint.RouteNameOf(route) };
                edges = guide.Edges(context);
                mirror = MirrorEdges(guide, context, edges);
                policies = [.. CorePolicies, .. guide.Policies(context)];
                guideId = InspectKit.ActiveThemeId;
            }
            return new InspectLint.Snapshot(width, height, tiers.Window, nodes)
            {
                Edges = edges,
                Policies = policies,
                TopBleeds = bleeds,
                HitBlockers = blockers,
                Scale = ScaleOf(),
                Theme = InspectKit.ActiveThemeId,
                GuideId = guideId,
                Route = route,
                Navigating = navigating,
                TrimSource = InspectDump.TrimSource(shell),
                Devices = devices,
                Backdrop = pixels,
                MirrorEdges = mirror,
                Playing = Playback.PhaseSignal.Peek().ToString().ToLowerInvariant(),
                PlayingTitle = PlayingTitle(),
                PlayingFromContext = PlayingFromContext(),
                ChromeHeld = InspectSwitches.HoverChromeAtRest,
                Reserved = Video.FloatingSurfaceReserve.Peek(),
                ExitingText = exiting,
            };
        }

        /// <summary>The nearest key at or above <paramref name="node"/> ("" when none).</summary>
        static string KeyAbove(SceneStore scene, NodeHandle node)
        {
            for (NodeHandle n = node; !n.IsNull; n = scene.Parent(n))
                if (scene.DebugKeyOf(n) is { Length: > 0 } key) return key;
            return "";
        }

        /// <summary>The words runs an exiting subtree still draws (visible, opacity above 0, a colour with alpha), with the opacity that reaches
        /// the screen and the nearest key.</summary>
        static void CollectExiting(SceneStore scene, StringTable strings, NodeHandle node, float above, string key, Point2 origin, List<InspectLint.ExitingRun> into)
        {
            if ((scene.Flags(node) & NodeFlags.Visible) == 0) return;
            ref NodePaint paint = ref scene.Paint(node);
            float opacity = above * paint.Opacity;
            if (opacity <= 0.01f) return;
            if (scene.DebugKeyOf(node) is { Length: > 0 } own) key = own;
            if (paint.VisualKind == VisualKind.Text && paint.Text.Value > 0 && paint.TextColor.A > 0.05f)
            {
                string text = strings.Resolve(paint.Text);
                RectF p = scene.PresentedAbsoluteRect(node);
                var r = new RectF(p.X + origin.X, p.Y + origin.Y, p.W, p.H);
                if (text.Length > 0 && !IsGlyph(text) && r.W > 0f && r.H > 0f) into.Add(new InspectLint.ExitingRun(key, r, text, Math.Clamp(opacity * paint.TextColor.A, 0f, 1f)));
            }
            for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c)) CollectExiting(scene, strings, c, opacity, key, origin, into);
        }

        /// <summary>The player plays from a context the stage's source line names (the same read <c>Stage.Frame</c> makes).</summary>
        static bool PlayingFromContext()
        {
            EntityId context = Playback.ContextUri.Peek();
            Queue.ContextWire wire = Playback.ContextLabel.Peek();
            return Queue.ContextName(context, in wire) is { Length: > 0 };
        }

        /// <summary>The title of what the player holds ("" when nothing).</summary>
        static string PlayingTitle()
        {
            EntityRef current = Playback.Current.Peek();
            return current.IsNone ? "" : LibraryRows.TitleOf(current.Kind, current.Slot);
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
        /// <c>modal</c>? (<c>{scrim, covered}</c>: a dialog's scrim covers the page), <c>occluded</c>? (nodes an opaque fill painted after them
        /// hides: a menu's plate, a pinned header), <c>animating</c> (<c>{nodes, subtree, scrolling}</c>: nodes with live tracks, the nodes under
        /// them, scrollers still moving in the frame read), <c>summary</c> (fail findings and report lines of the gate rules at or above `min`, listed or not: what
        /// "lint min=error clean" means), <c>outcomes</c>, <c>levels</c>, <c>suppressed</c>, <c>baseline</c>?, <c>rules</c> (per rule:
        /// <c>checked</c>, its counts, level and ids), <c>truncated</c>? and <c>findings</c>.</summary>
        public static void WriteReport(Utf8JsonWriter w, InspectLint.Snapshot snapshot, InspectLint.Report report, InspectLint.Options options, string root = "")
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
            InspectLint.Snapshot prepared = report.Prepared ?? snapshot;
            if (prepared.Modal.Length > 0)
            {
                w.WriteStartObject("modal");
                w.WriteString("scrim", prepared.Modal);
                w.WriteNumber("covered", prepared.CoveredCount);
                w.WriteEndObject();
            }
            if (prepared.OccludedCount > 0) w.WriteNumber("occluded", prepared.OccludedCount);
            int animating = 0, scrolling = 0;
            foreach (InspectLint.Node n in prepared.Nodes)
            {
                if (n.Animating) animating++;
                if (n.ScrollMoving) scrolling++;
            }
            w.WriteStartObject("animating");
            w.WriteNumber("nodes", animating);
            w.WriteNumber("subtree", prepared.MovingCount);
            w.WriteNumber("scrolling", scrolling);
            w.WriteEndObject();
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
            w.WriteNumber("report", report.CountAll(InspectLint.Outcome.Report));
            w.WriteEndObject();
            w.WriteStartObject("levels");
            w.WriteNumber("gate", report.CountAll(InspectLint.Level.Gate));
            w.WriteNumber("todo", report.CountAll(InspectLint.Level.Todo));
            w.WriteEndObject();
            // What a gate counts: every fail of the rules that ran, per level and severity, whatever `min`, `outcomes` and `limit` list.
            w.WriteStartObject("fails");
            foreach (InspectLint.Level level in (ReadOnlySpan<InspectLint.Level>)[InspectLint.Level.Gate, InspectLint.Level.Todo])
            {
                w.WriteStartObject(InspectLint.Name(level));
                foreach (InspectLint.Severity sev in (ReadOnlySpan<InspectLint.Severity>)[InspectLint.Severity.Error, InspectLint.Severity.Warn, InspectLint.Severity.Info])
                    w.WriteNumber(InspectLint.Name(sev), report.Fails(level, sev));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            // What narrowed this lint: a listing option (min, outcomes, limit: the counts above still hold every fail) or the judgement
            // itself (rules: only those ran; root: only that subtree). Empty when nothing did.
            w.WriteStartArray("narrowed");
            foreach (string why in Narrowed(options, report, root)) w.WriteStringValue(why);
            w.WriteEndArray();
            w.WriteStartObject("suppressed");
            w.WriteNumber("intent", report.CountSuppressed("intent"));
            w.WriteNumber("baseline", report.CountSuppressed("baseline"));
            w.WriteEndObject();
            if (options.BaselineFile.Length > 0)
            {
                w.WriteStartObject("baseline");
                w.WriteString("file", options.BaselineFile);
                w.WriteNumber("entries", options.Baseline.Count);
                w.WriteNumber("considered", report.BaselineConsidered);
                w.WriteNumber("matched", report.BaselineMatched);
                if (report.BaselineDuplicates > 0) w.WriteNumber("duplicates", report.BaselineDuplicates);
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
                int review = 0, suppressed = 0, reports = 0;
                foreach (InspectLint.Finding f in report.All)
                {
                    if (!ids.Contains(f.Id)) continue;
                    if (f.Outcome == InspectLint.Outcome.Review) review++;
                    else if (f.Outcome == InspectLint.Outcome.Suppressed) suppressed++;
                    else if (f.Outcome == InspectLint.Outcome.Report) reports++;
                }
                w.WriteStartObject(name);
                w.WriteNumber("checked", looked);
                w.WriteNumber("error", report.Count(name, InspectLint.Severity.Error));
                w.WriteNumber("warn", report.Count(name, InspectLint.Severity.Warn));
                w.WriteNumber("info", report.Count(name, InspectLint.Severity.Info));
                w.WriteNumber("review", review);
                w.WriteNumber("suppressed", suppressed);
                if (reports > 0) w.WriteNumber("report", reports);
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

        /// <summary>What narrowed a lint, one word each: what it judged (<c>rules=text.clip,overlap.sibling</c>, <c>root=zune-band</c>) or which
        /// fails it lists (<c>outcomes=review</c>, <c>limit=5 (cut: target)</c>). Read by `inspect.py --gate`, which warns on each. `min` is not
        /// here: the reply's <c>fails</c> count every fail whatever `min` lists, and the gate compares them with what is listed.</summary>
        public static List<string> Narrowed(InspectLint.Options options, InspectLint.Report report, string root)
        {
            var why = new List<string>();
            if (options.Rules.Count < InspectLint.RuleIds.Length) why.Add("rules=" + string.Join(",", options.Rules));
            bool listsAll = true;
            foreach (InspectLint.Outcome o in InspectLint.DefaultOutcomes)
                if (!options.Outcomes.Contains(o)) listsAll = false;
            if (!listsAll) why.Add("outcomes=" + string.Join(",", options.Outcomes.Select(static o => InspectLint.Name(o))));
            if (report.Truncated.Count > 0) why.Add("limit=" + options.MaxPerRule.ToString(System.Globalization.CultureInfo.InvariantCulture) + " (cut: " + string.Join(",", report.Truncated) + ")");
            if (root.Length > 0) why.Add(root);
            return why;
        }

        /// <summary>Into the open object: the window the lint ran in (<c>theme</c>, <c>tier</c>, <c>size</c>), the route of the page it saw
        /// (<c>navigating</c>: the route the shell is going to, when that page is not on screen yet), where trims come from (<c>trimSource</c>),
        /// the devices, and the saved snapshot a replay read (<c>replay</c>).</summary>
        public static void WriteHead(Utf8JsonWriter w, InspectLint.Snapshot snapshot)
        {
            w.WriteString("theme", snapshot.Theme);
            w.WriteString("tier", snapshot.Tier.ToString());
            w.WriteStartObject("size");
            Number(w, "w", snapshot.Width);
            Number(w, "h", snapshot.Height);
            w.WriteEndObject();
            w.WriteString("route", snapshot.Route);
            if (snapshot.Navigating.Length > 0) w.WriteString("navigating", snapshot.Navigating);
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
            if (!InspectLintScene.TryPath(a, "replay", out string replay, out error) || !InspectLintScene.TryPath(a, "save", out string save, out error))
                return Inspect.Reply.Fail(id, name, error);
            InspectLint.Snapshot snapshot;
            if (replay.Length > 0)
            {
                if (a.Has("root") || a.Has("nth")) return Inspect.Reply.Fail(id, name, "replay lints the whole saved snapshot: root= and nth= pick nodes of a mounted page (save a root= lint instead)");
                if (!InspectLintStore.TryLoad(replay, out snapshot, out error)) return Inspect.Reply.Fail(id, name, error);
                if (!InspectLintScene.TryPath(a, "frame", out string frame, out error)) return Inspect.Reply.Fail(id, name, error);
                if (frame.Length > 0)
                {
                    if (!InspectLintScene.TryReadFrame(frame, snapshot.Width, out InspectLint.Pixels? framePixels, out error)) return Inspect.Reply.Fail(id, name, error);
                    snapshot = snapshot with { Backdrop = framePixels };
                }
            }
            else
            {
                if (shell is null) return Inspect.Reply.Fail(id, name, "nothing is mounted (mount first, or lint replay=<file>)");
                if (!TryRoots(a, shell, out List<NodeHandle> roots, out error)) return Inspect.Reply.Fail(id, name, error);
                // the rules that read the rendered frame: contrast (the ground under the text) and the picture rules (Diagnostics.Inspect.Picture.cs)
                bool wantPixels = options.Enabled(InspectLint.Rules.Contrast) || options.Enabled(InspectLint.Rules.PictureBlank)
                    || options.Enabled(InspectLint.Rules.PictureUnpainted) || save.Length > 0;
                InspectLint.Pixels? pixels = wantPixels ? RenderedFrame(shell) : null;
                snapshot = InspectLintScene.Capture(shell, CollectionsMarshal.AsSpan(roots), pixels, Devices) with { StagedOverlays = StagedOverlays(shell) };
            }
            InspectLint.Report report = InspectLint.Run(snapshot, options);
            string saved = "";
            if (save.Length > 0)
            {
                try { saved = InspectLintStore.Save(report.Prepared ?? snapshot, save); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return Inspect.Reply.Fail(id, name, "save: " + ex.Message);
                }
            }
            return Inspect.Reply.Ok(id, name, w =>
            {
                InspectLintScene.WriteHead(w, snapshot);
                if (saved.Length > 0) w.WriteString("saved", saved);
                InspectLintScene.WriteReport(w, report.Prepared ?? snapshot, report, options,
                    a.Has("root") ? "root=" + a.Str("root") + (a.Has("nth") ? " nth=" + a.Str("nth") : "") : "");
            });
        }
    }
}
