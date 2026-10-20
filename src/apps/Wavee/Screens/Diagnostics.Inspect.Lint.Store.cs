// ── Screens/Diagnostics.Inspect.Lint.Store.cs — saved lint snapshots (`lint save=`, `lint replay=`) and baseline files (`lint baseline=`) ──
//
// Role: CORE (pure JSON: Utf8JsonWriter out, JsonDocument in; file reads and writes only where a caller passes a path). Spec: PLAN 4.5
// (baselines) and 5.2 ("saved snapshots": a rule is validated on pinned pages while the branch keeps moving; the owner allowed replays of
// saved pages).
//
// A SAVED SNAPSHOT is what `lint` captured, as JSON: the window, theme, route, text source, the guide's edges and policies, the theme's
// spacing scale, and every captured node with its rect, flags, declared layout, paint and text run (with the ground the rendered frame put
// under it, so `contrast.text` replays without pixels). Replay reads it back and runs the same pure rules: no page needs to be mounted. The
// derived fields (visible rect, region, family...) are written for a reader's eyes and recomputed on replay, for the scope asked.
//
// A BASELINE is JSON lines `{fp, rule, reason, by, date}`: accepted findings, matched by fingerprint (never by message). A reason is mandatory;
// `#` lines and blank lines are comments.

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>Lint snapshots to and from JSON, and baseline files. See the file header.</summary>
    public static class InspectLintStore
    {
        /// <summary>The snapshot format's version (a reader refuses a newer one).</summary>
        public const int Version = 1;

        const string Kind = "lint-snapshot";

        static readonly JsonWriterOptions s_writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

        // ── writing ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Write <paramref name="s"/> to <paramref name="path"/> (directories created). Returns the full path.</summary>
        public static string Save(InspectLint.Snapshot s, string path)
        {
            string full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            using FileStream file = File.Create(full);
            using var w = new Utf8JsonWriter(file, s_writer);
            Write(w, s);
            w.Flush();
            return full;
        }

        // Numbers are written at round-trip float precision: a row exactly half on screen must stay so on replay.
        static void Num(Utf8JsonWriter w, string name, float v)
        {
            if (float.IsFinite(v)) w.WriteNumber(name, v);
            else w.WriteNull(name);
        }

        static void Rect(Utf8JsonWriter w, string name, in RectF r)
        {
            w.WriteStartArray(name);
            w.WriteNumberValue(r.X);
            w.WriteNumberValue(r.Y);
            w.WriteNumberValue(r.W);
            w.WriteNumberValue(r.H);
            w.WriteEndArray();
        }

        static void Edges(Utf8JsonWriter w, string name, in Edges4 e)
        {
            w.WriteStartArray(name);
            w.WriteNumberValue(e.Left);
            w.WriteNumberValue(e.Top);
            w.WriteNumberValue(e.Right);
            w.WriteNumberValue(e.Bottom);
            w.WriteEndArray();
        }

        static string Color(ColorF c)
        {
            static int B(float v) => Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
            return "#" + B(c.R).ToString("X2", CultureInfo.InvariantCulture) + B(c.G).ToString("X2", CultureInfo.InvariantCulture)
                + B(c.B).ToString("X2", CultureInfo.InvariantCulture) + B(c.A).ToString("X2", CultureInfo.InvariantCulture);
        }

        static void Strings(Utf8JsonWriter w, string name, IReadOnlyList<string>? list)
        {
            if (list is not { Count: > 0 }) return;
            w.WriteStartArray(name);
            foreach (string v in list) w.WriteStringValue(v);
            w.WriteEndArray();
        }

        /// <summary>The snapshot object.</summary>
        public static void Write(Utf8JsonWriter w, InspectLint.Snapshot s)
        {
            w.WriteStartObject();
            w.WriteString("kind", Kind);
            w.WriteNumber("version", Version);
            w.WriteString("theme", s.Theme);
            w.WriteString("route", s.Route);
            w.WriteString("tier", s.Tier.ToString());
            w.WriteStartObject("size");
            Num(w, "w", s.Width);
            Num(w, "h", s.Height);
            w.WriteEndObject();
            w.WriteString("trimSource", s.TrimSource);
            w.WriteString("devices", s.Devices);
            w.WriteString("guide", s.GuideId);
            w.WriteStartArray("edges");
            foreach (LintEdge e in s.Edges)
            {
                w.WriteStartObject();
                w.WriteString("rule", e.Rule);
                w.WriteString("id", e.Id);
                w.WriteString("what", e.What);
                w.WriteString("side", e.Side == LintSide.Right ? "right" : "left");
                Num(w, "expected", e.Expected);
                Num(w, "tolerance", e.Tolerance);
                Strings(w, "keys", e.Keys);
                Strings(w, "prefixes", e.Prefixes);
                if (e.UnderKey.Length > 0) w.WriteString("underKey", e.UnderKey);
                if (e.GlyphsOnly) w.WriteBoolean("glyphsOnly", true);
                if (e.Advisory) w.WriteBoolean("advisory", true);
                if (e.Column) w.WriteBoolean("column", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("policies");
            foreach (LintPolicy p in s.Policies)
            {
                w.WriteStartObject();
                w.WriteString("intent", p.Intent.ToString());
                w.WriteString("prefix", p.Prefix);
                w.WriteString("reason", p.Reason);
                if (p.Value != 0f) Num(w, "value", p.Value);
                if (p.Rule.Length > 0) w.WriteString("rule", p.Rule);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            Strings(w, "topBleeds", s.TopBleeds);
            w.WriteStartArray("scale");
            foreach (InspectLint.ScaleEntry e in s.Scale)
            {
                w.WriteStartObject();
                w.WriteString("name", e.Name);
                Num(w, "value", e.Value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("nodes");
            foreach (InspectLint.Node n in s.Nodes) WriteNode(w, n);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        static void WriteNode(Utf8JsonWriter w, InspectLint.Node n)
        {
            w.WriteStartObject();
            w.WriteNumber("i", n.Index);
            w.WriteNumber("p", n.Parent);
            if (n.Key is not null) w.WriteString("key", n.Key);
            w.WriteString("path", n.Path);
            w.WriteString("kind", n.Kind);
            w.WriteNumber("d", n.Depth);
            Rect(w, "r", n.Rect);
            if (n.Clips) w.WriteBoolean("clips", true);
            if (n.ScrollAxis != 0)
            {
                w.WriteStartObject("scroll");
                w.WriteNumber("axis", n.ScrollAxis);
                Num(w, "offset", n.ScrollOffset);
                Num(w, "max", n.ScrollMax);
                if (n.ScrollMoving) w.WriteBoolean("moving", true);
                w.WriteEndObject();
            }
            if (!n.SeedScreen.IsInfinite) Rect(w, "seed", n.SeedScreen);
            if (!n.SeedContent.IsInfinite) Rect(w, "seedContent", n.SeedContent);
            if (n.FadeEdges != 0) w.WriteNumber("fade", n.FadeEdges);
            if (!n.ClipRect.IsInfinite) Rect(w, "clip", n.ClipRect);
            if (n.ZStack) w.WriteBoolean("zstack", true);
            if (!n.Column) w.WriteBoolean("row", true);
            WriteLayout(w, n.Layout);
            if (n.Interactive) w.WriteBoolean("interactive", true);
            if (n.Disabled) w.WriteBoolean("disabled", true);
            if (n.Role.Length > 0) w.WriteString("role", n.Role);
            if (n.ClickId != 0) w.WriteNumber("click", n.ClickId);
            if (n.Label.Length > 0) w.WriteString("label", n.Label);
            if (n.LabelGlyph.Length > 0) w.WriteString("labelGlyph", n.LabelGlyph);
            if (n.Animating) w.WriteBoolean("animating", true);
            if (n.Opacity != 1f) Num(w, "opacity", n.Opacity);
            if (n.Inert) w.WriteBoolean("inert", true);
            if (n.Sticky) w.WriteBoolean("sticky", true);
            if (n.Fill.A > 0f) w.WriteString("fill", Color(n.Fill));
            if (n.BorderWidth > 0f)
            {
                w.WriteStartObject("border");
                Num(w, "w", n.BorderWidth);
                w.WriteString("color", Color(n.BorderColor));
                w.WriteEndObject();
            }
            if (n.Image) w.WriteBoolean("image", true);
            if (n.Gradient) w.WriteBoolean("gradient", true);
            if (n.Text is { } t) WriteText(w, t);
            if (n.TextCount != 0) w.WriteNumber("tc", n.TextCount);
            if (n.ImageCount != 0) w.WriteNumber("ic", n.ImageCount);
            // Derived (for a reader; replay recomputes them).
            Num(w, "on", n.OnScreen);
            if (n.Covered) w.WriteBoolean("covered", true);
            if (n.Family.Length > 0) w.WriteString("family", n.Family);
            if (n.CellPath.Length > 0) w.WriteString("cell", n.CellPath);
            w.WriteEndObject();
        }

        static void WriteLayout(Utf8JsonWriter w, InspectLint.Declared l)
        {
            if (ReferenceEquals(l, InspectLint.Declared.None)) return;
            w.WriteStartObject("layout");
            if (l.Gap != 0f) Num(w, "gap", l.Gap);
            if (l.Padding != default) Edges(w, "pad", l.Padding);
            if (l.Margin != default) Edges(w, "margin", l.Margin);
            if (!float.IsNaN(l.W)) Num(w, "w", l.W);
            if (!float.IsNaN(l.H)) Num(w, "h", l.H);
            if (!float.IsNaN(l.MinW)) Num(w, "minW", l.MinW);
            if (!float.IsNaN(l.MinH)) Num(w, "minH", l.MinH);
            if (!float.IsNaN(l.MaxW)) Num(w, "maxW", l.MaxW);
            if (!float.IsNaN(l.MaxH)) Num(w, "maxH", l.MaxH);
            if (l.Grow != 0f) Num(w, "grow", l.Grow);
            if (l.Shrink != 0f) Num(w, "shrink", l.Shrink);
            if (!float.IsNaN(l.Basis)) Num(w, "basis", l.Basis);
            if (l.Justify != "Start") w.WriteString("justify", l.Justify);
            if (l.AlignItems != "Stretch") w.WriteString("alignItems", l.AlignItems);
            if (l.AlignSelf != "Auto") w.WriteString("alignSelf", l.AlignSelf);
            if (l.JustifySelf != "Auto") w.WriteString("justifySelf", l.JustifySelf);
            if (l.Wrap) w.WriteBoolean("wrap", true);
            w.WriteEndObject();
        }

        static void WriteText(Utf8JsonWriter w, InspectLint.TextRun t)
        {
            w.WriteStartObject("text");
            w.WriteString("s", t.String);
            Num(w, "size", t.Size);
            if (t.LineHeight != 0f) Num(w, "lh", t.LineHeight);
            w.WriteNumber("weight", t.Weight);
            if (t.MaxLines != 0) w.WriteNumber("maxLines", t.MaxLines);
            if (t.Wraps) w.WriteBoolean("wraps", true);
            if (t.Trim != "None") w.WriteString("trim", t.Trim);
            if (t.Trimmed) w.WriteBoolean("trimmed", true);
            Num(w, "nw", t.NaturalW);
            Num(w, "nh", t.NaturalH);
            if (t.OverflowX) w.WriteBoolean("ox", true);
            if (t.OverflowY) w.WriteBoolean("oy", true);
            if (t.Glyph) w.WriteBoolean("glyph", true);
            if (t.GlyphName.Length > 0) w.WriteString("glyphName", t.GlyphName);
            if (t.Spans) w.WriteBoolean("spans", true);
            if (t.Color.A > 0f) w.WriteString("color", Color(t.Color));
            if (t.Align != "Start") w.WriteString("align", t.Align);
            if (t.Ground.A > 0f)
            {
                w.WriteString("ground", Color(t.Ground));
                Num(w, "groundShare", t.GroundShare);
                if (t.Worst > 0f)
                {
                    Num(w, "worst", t.Worst);
                    w.WriteString("worstGround", Color(t.WorstGround));
                    w.WriteNumber("gapPixels", t.GapPixels);
                }
            }
            w.WriteEndObject();
        }

        // ── reading ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Read a saved snapshot. False with a reason when the file is missing, not a lint snapshot, or newer than this reader.</summary>
        public static bool TryLoad(string path, out InspectLint.Snapshot snapshot, out string error)
        {
            snapshot = new InspectLint.Snapshot(0f, 0f, WidthTier.Compact, []);
            error = "";
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { error = "replay: bad path '" + path + "'"; return false; }
            if (!File.Exists(full)) { error = "replay: no file '" + full + "'"; return false; }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(full));
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Str(root, "kind") != Kind) { error = "replay: '" + full + "' is not a lint snapshot"; return false; }
                if (Int(root, "version") > Version) { error = "replay: '" + full + "' is version " + Int(root, "version") + " (this reader knows " + Version + ")"; return false; }
                WidthTier tier = Enum.TryParse(Str(root, "tier"), out WidthTier t) ? t : WidthTier.Compact;
                float w = 0f, h = 0f;
                if (root.TryGetProperty("size", out JsonElement size)) { w = Float(size, "w"); h = Float(size, "h"); }
                if (!(w > 0f) || !(h > 0f) || !float.IsFinite(w) || !float.IsFinite(h)) { error = "replay: '" + full + "' has no window size (size.w and size.h must be above 0)"; return false; }
                var nodes = new List<InspectLint.Node>();
                if (root.TryGetProperty("nodes", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement n in list.EnumerateArray())
                    {
                        if (n.ValueKind != JsonValueKind.Object) { error = "replay: '" + full + "' node " + nodes.Count + " is not an object"; return false; }
                        InspectLint.Node node = ReadNode(n, nodes.Count);
                        if (node.Parent < -1 || node.Parent >= node.Index)
                        {
                            error = "replay: '" + full + "' node " + node.Index + " names parent " + node.Parent + " (a parent comes before its children: -1 or an earlier index)";
                            return false;
                        }
                        RectF r = node.Rect;
                        if (!float.IsFinite(r.X) || !float.IsFinite(r.Y) || !float.IsFinite(r.W) || !float.IsFinite(r.H) || r.W < 0f || r.H < 0f)
                        {
                            error = "replay: '" + full + "' node " + node.Index + " has no finite rect";
                            return false;
                        }
                        nodes.Add(node);
                    }
                snapshot = new InspectLint.Snapshot(w, h, tier, nodes)
                {
                    Theme = Str(root, "theme"),
                    Route = Str(root, "route"),
                    TrimSource = Str(root, "trimSource", "dwrite"),
                    Devices = Str(root, "devices"),
                    GuideId = Str(root, "guide"),
                    Edges = ReadEdges(root),
                    Policies = ReadPolicies(root),
                    TopBleeds = StringsOf(root, "topBleeds") ?? [],
                    Scale = ReadScale(root),
                    Source = full,
                };
                return true;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                error = "replay: '" + full + "': " + ex.Message;
                return false;
            }
        }

        static string Str(JsonElement e, string name, string fallback = "")
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

        static float Float(JsonElement e, string name, float fallback = 0f)
            => e.TryGetProperty(name, out JsonElement v) ? v.ValueKind == JsonValueKind.Number ? v.GetSingle() : v.ValueKind == JsonValueKind.Null ? float.NaN : fallback : fallback;

        static int Int(JsonElement e, string name, int fallback = 0)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

        static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        static RectF RectOf(JsonElement e, string name, RectF fallback)
        {
            if (!e.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 4) return fallback;
            return new RectF(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());
        }

        static Edges4 EdgesOf(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 4) return default;
            return new Edges4(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());
        }

        static ColorF ColorOf(JsonElement e, string name)
        {
            string hex = Str(e, name);
            if (hex.Length != 9 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return default;
            return new ColorF(((v >> 24) & 255) / 255f, ((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        }

        static List<string>? StringsOf(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (JsonElement v in a.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s) list.Add(s);
            return list;
        }

        static InspectLint.Node ReadNode(JsonElement e, int index)
        {
            string? key = e.TryGetProperty("key", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            int axis = 0;
            float offset = 0f, max = 0f;
            bool moving = false;
            if (e.TryGetProperty("scroll", out JsonElement scroll))
            {
                axis = Int(scroll, "axis");
                offset = Float(scroll, "offset");
                max = Float(scroll, "max");
                moving = Bool(scroll, "moving");
            }
            float borderW = 0f;
            ColorF borderC = default;
            if (e.TryGetProperty("border", out JsonElement border))
            {
                borderW = Float(border, "w");
                borderC = ColorOf(border, "color");
            }
            return new InspectLint.Node(index, Int(e, "p", -1), key, Str(e, "path"), RectOf(e, "r", default))
            {
                Depth = Int(e, "d"),
                Kind = Str(e, "kind"),
                Clips = Bool(e, "clips"),
                ScrollAxis = axis,
                ScrollOffset = offset,
                ScrollMax = max,
                ScrollMoving = moving,
                SeedScreen = RectOf(e, "seed", RectF.Infinite),
                SeedContent = RectOf(e, "seedContent", RectF.Infinite),
                FadeEdges = (byte)Int(e, "fade"),
                ClipRect = RectOf(e, "clip", RectF.Infinite),
                ZStack = Bool(e, "zstack"),
                Column = !Bool(e, "row"),
                Layout = e.TryGetProperty("layout", out JsonElement layout) ? ReadLayout(layout) : InspectLint.Declared.None,
                Interactive = Bool(e, "interactive"),
                Disabled = Bool(e, "disabled"),
                Role = Str(e, "role"),
                ClickId = Int(e, "click"),
                Label = Str(e, "label"),
                LabelGlyph = Str(e, "labelGlyph"),
                Animating = Bool(e, "animating"),
                Opacity = e.TryGetProperty("opacity", out _) ? Float(e, "opacity", 1f) : 1f,
                Inert = Bool(e, "inert"),
                Sticky = Bool(e, "sticky"),
                Fill = ColorOf(e, "fill"),
                BorderWidth = borderW,
                BorderColor = borderC,
                Image = Bool(e, "image"),
                Gradient = Bool(e, "gradient"),
                Text = e.TryGetProperty("text", out JsonElement text) ? ReadText(text) : null,
                TextCount = Int(e, "tc"),
                ImageCount = Int(e, "ic"),
            };
        }

        static InspectLint.Declared ReadLayout(JsonElement e) => new(Float(e, "gap"), EdgesOf(e, "pad"), EdgesOf(e, "margin"))
        {
            W = Float(e, "w", float.NaN),
            H = Float(e, "h", float.NaN),
            MinW = Float(e, "minW", float.NaN),
            MinH = Float(e, "minH", float.NaN),
            MaxW = Float(e, "maxW", float.NaN),
            MaxH = Float(e, "maxH", float.NaN),
            Grow = Float(e, "grow"),
            Shrink = Float(e, "shrink"),
            Basis = Float(e, "basis", float.NaN),
            Justify = Str(e, "justify", "Start"),
            AlignItems = Str(e, "alignItems", "Stretch"),
            AlignSelf = Str(e, "alignSelf", "Auto"),
            JustifySelf = Str(e, "justifySelf", "Auto"),
            Wrap = Bool(e, "wrap"),
        };

        static InspectLint.TextRun ReadText(JsonElement e) => new(Str(e, "s"), Float(e, "size"))
        {
            LineHeight = Float(e, "lh"),
            Weight = Int(e, "weight", 400),
            MaxLines = Int(e, "maxLines"),
            Wraps = Bool(e, "wraps"),
            Trim = Str(e, "trim", "None"),
            Trimmed = Bool(e, "trimmed"),
            NaturalW = Float(e, "nw"),
            NaturalH = Float(e, "nh"),
            OverflowX = Bool(e, "ox"),
            OverflowY = Bool(e, "oy"),
            Glyph = Bool(e, "glyph"),
            GlyphName = Str(e, "glyphName"),
            Spans = Bool(e, "spans"),
            Color = ColorOf(e, "color"),
            Align = Str(e, "align", "Start"),
            Ground = ColorOf(e, "ground"),
            GroundShare = Float(e, "groundShare"),
            Worst = Float(e, "worst"),
            WorstGround = ColorOf(e, "worstGround"),
            GapPixels = Int(e, "gapPixels"),
        };

        static IReadOnlyList<LintEdge> ReadEdges(JsonElement root)
        {
            var edges = new List<LintEdge>();
            if (!root.TryGetProperty("edges", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return edges;
            foreach (JsonElement e in list.EnumerateArray())
                edges.Add(new LintEdge(Str(e, "rule"), Str(e, "id"), Str(e, "what"), Str(e, "side") == "right" ? LintSide.Right : LintSide.Left,
                    Float(e, "expected"), Float(e, "tolerance"), StringsOf(e, "keys"), StringsOf(e, "prefixes"), Str(e, "underKey"),
                    Bool(e, "glyphsOnly"), Bool(e, "advisory"), Bool(e, "column")));
            return edges;
        }

        static IReadOnlyList<LintPolicy> ReadPolicies(JsonElement root)
        {
            var policies = new List<LintPolicy>();
            if (!root.TryGetProperty("policies", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return policies;
            foreach (JsonElement e in list.EnumerateArray())
                if (Enum.TryParse(Str(e, "intent"), out LintIntent intent))
                    policies.Add(new LintPolicy(intent, Str(e, "prefix"), Str(e, "reason"), Float(e, "value"), Str(e, "rule")));
            return policies;
        }

        static IReadOnlyList<InspectLint.ScaleEntry> ReadScale(JsonElement root)
        {
            var scale = new List<InspectLint.ScaleEntry>();
            if (!root.TryGetProperty("scale", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return scale;
            foreach (JsonElement e in list.EnumerateArray()) scale.Add(new InspectLint.ScaleEntry(Str(e, "name"), Float(e, "value")));
            return scale;
        }

        // ── baselines ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Read a baseline file: one JSON object per line, <c>{fp, rule, reason, by, date}</c>; <c>#</c> lines and blank lines are skipped.
        /// False with a reason when the file is missing, a line is not JSON, or an entry lacks <c>fp</c> or <c>reason</c>.</summary>
        public static bool TryReadBaseline(string path, out List<InspectLint.BaselineEntry> entries, out string error)
        {
            entries = [];
            error = "";
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { error = "baseline: bad path '" + path + "'"; return false; }
            if (!File.Exists(full)) { error = "baseline: no file '" + full + "'"; return false; }
            string[] lines;
            try { lines = File.ReadAllLines(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "baseline: '" + full + "': " + ex.Message; return false; }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement e = doc.RootElement;
                    string fp = e.ValueKind == JsonValueKind.Object ? Str(e, "fp") : "";
                    string reason = e.ValueKind == JsonValueKind.Object ? Str(e, "reason") : "";
                    if (fp.Length == 0 || reason.Trim().Length == 0)
                    {
                        error = "baseline: '" + full + "' line " + (i + 1) + ": an entry needs fp and a reason";
                        return false;
                    }
                    entries.Add(new InspectLint.BaselineEntry(fp, Str(e, "rule"), reason, Str(e, "by"), Str(e, "date")));
                }
                catch (JsonException ex)
                {
                    error = "baseline: '" + full + "' line " + (i + 1) + ": " + ex.Message;
                    return false;
                }
            }
            return true;
        }
    }
}
