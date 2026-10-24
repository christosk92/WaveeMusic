// ── Screens/Diagnostics.Inspect.Lint.Store.cs — saved lint snapshots (`lint save=`, `lint replay=`) and baseline files (`lint baseline=`) ──
//
// Role: CORE (pure JSON: Utf8JsonWriter out, JsonDocument in; file reads and writes only where a caller passes a path). Spec: PLAN 4.5
// (baselines) and 5.2 ("saved snapshots": a rule is validated on pinned pages while the branch keeps moving; the owner allowed replays of
// saved pages).
//
// A SAVED SNAPSHOT is what `lint` captured, as JSON: the window, theme, route, text source, the guide's edges and policies, the theme's
// spacing scale, and every captured node with its rect, flags, declared layout, paint and text run (with the ground the rendered frame put
// under it, so `contrast.text` replays without pixels). Replay reads it back and runs the same pure rules: no page needs to be mounted. The
// derived fields (visible rect, region, family...) are written for a reader's eyes and recomputed on replay, for the scope asked. The reader
// is strict: a field of the wrong type, a node without a finite rect, an unknown intent or an empty node list fails the replay with the node
// and the field, never a silent default (a doctored copy must say what it got wrong).
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
            if (s.Navigating.Length > 0) w.WriteString("navigating", s.Navigating);
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
            w.WriteStartArray("hitBlockers");
            foreach (RectF b in s.HitBlockers)
            {
                w.WriteStartArray();
                w.WriteNumberValue(b.X);
                w.WriteNumberValue(b.Y);
                w.WriteNumberValue(b.W);
                w.WriteNumberValue(b.H);
                w.WriteEndArray();
            }
            w.WriteEndArray();
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
            if (n.Seeded)
            {
                Rect(w, "seed", n.SeedScreen);
                Rect(w, "seedContent", n.SeedContent);
                if (!n.SeedShown) w.WriteBoolean("seedShown", false);
            }
            if (n.FadeEdges != 0) w.WriteNumber("fade", n.FadeEdges);
            if (!n.ClipRect.IsInfinite) Rect(w, "clip", n.ClipRect);
            if (n.ZStack) w.WriteBoolean("zstack", true);
            if (!n.Column) w.WriteBoolean("row", true);
            WriteLayout(w, n.Layout);
            if (n.Interactive) w.WriteBoolean("interactive", true);
            if (n.Disabled) w.WriteBoolean("disabled", true);
            if (n.Role.Length > 0) w.WriteString("role", n.Role);
            if (n.ClickId != 0) w.WriteNumber("click", n.ClickId);
            if (n.Drag) w.WriteBoolean("drag", true);
            if (n.Slop != default) Edges(w, "slop", n.Slop);
            if (n.Lane != InspectLint.NoLane) Edges(w, "lane", n.Lane);
            if (!n.Pressable) w.WriteBoolean("pressable", false);
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
            if (n.Visual.Length > 0) w.WriteString("visual", n.Visual);
            if (n.Text is { } t) WriteText(w, t);
            if (n.TextCount != 0) w.WriteNumber("tc", n.TextCount);
            if (n.ImageCount != 0) w.WriteNumber("ic", n.ImageCount);
            // Derived (for a reader; replay recomputes them).
            Num(w, "on", n.OnScreen);
            if (n.Covered) w.WriteBoolean("covered", true);
            if (n.Occluded > 0f) Num(w, "occluded", n.Occluded);
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
            if (t.HasInk)
            {
                Num(w, "inkTop", t.InkTop);
                Num(w, "inkBottom", t.InkBottom);
            }
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

        /// <summary>Read a saved snapshot. False with a reason when the file is missing, not a lint snapshot, newer than this reader, or malformed
        /// (the reason names the node and the field).</summary>
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
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out JsonElement kind) || kind.ValueKind != JsonValueKind.String
                    || kind.GetString() != Kind) { error = "replay: '" + full + "' is not a lint snapshot"; return false; }
                var r = new Strict("");
                int version = r.Int(root, "version", 0);
                if (version > Version) { error = "replay: '" + full + "' is version " + version + " (this reader knows " + Version + ")"; return false; }
                WidthTier tier = WidthTier.Compact;
                string tierName = r.Str(root, "tier", "Compact");
                if (!Enum.TryParse(tierName, out tier)) r.Fail("tier", "is a width tier name, not '" + tierName + "'");
                float w = 0f, h = 0f;
                if (root.TryGetProperty("size", out JsonElement size) && size.ValueKind == JsonValueKind.Object) { w = r.Num(size, "w", 0f); h = r.Num(size, "h", 0f); }
                if (r.Error.Length == 0 && (!(w > 0f) || !(h > 0f) || !float.IsFinite(w) || !float.IsFinite(h)))
                    r.Fail("size", "needs w and h, finite numbers above 0");
                if (r.Error.Length > 0) { error = "replay: '" + full + "': " + r.Error; return false; }
                if (!root.TryGetProperty("nodes", out JsonElement list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                {
                    error = "replay: '" + full + "': nodes must be a non-empty array of nodes";
                    return false;
                }
                var nodes = new List<InspectLint.Node>(list.GetArrayLength());
                foreach (JsonElement n in list.EnumerateArray())
                {
                    int index = nodes.Count;
                    var at = new Strict("node " + index.ToString(CultureInfo.InvariantCulture));
                    if (n.ValueKind != JsonValueKind.Object) { error = "replay: '" + full + "': node " + index + " is not an object"; return false; }
                    InspectLint.Node node = ReadNode(n, index, at);
                    if (at.Error.Length == 0 && n.TryGetProperty("i", out _) && at.Int(n, "i", index) != index) at.Fail("i", "says " + at.Int(n, "i", index) + " but the node is the " + index + "th (nodes are read in order)");
                    if (at.Error.Length == 0 && (node.Parent < -1 || node.Parent >= index))
                        at.Fail("p", "names parent " + node.Parent + " (a parent comes before its children: -1 or an earlier index)");
                    if (at.Error.Length > 0) { error = "replay: '" + full + "': " + at.Error; return false; }
                    nodes.Add(node);
                }
                var head = new Strict("");
                snapshot = new InspectLint.Snapshot(w, h, tier, nodes)
                {
                    Theme = head.Str(root, "theme", ""),
                    Route = head.Str(root, "route", ""),
                    Navigating = head.Str(root, "navigating", ""),
                    TrimSource = head.Str(root, "trimSource", "dwrite"),
                    Devices = head.Str(root, "devices", ""),
                    GuideId = head.Str(root, "guide", ""),
                    Edges = ReadEdges(root, head),
                    Policies = ReadPolicies(root, head),
                    TopBleeds = head.Strings(root, "topBleeds") ?? [],
                    HitBlockers = ReadRects(root, "hitBlockers", head),
                    Scale = ReadScale(root, head),
                    Source = full,
                };
                if (head.Error.Length > 0) { error = "replay: '" + full + "': " + head.Error; return false; }
                return true;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                error = "replay: '" + full + "': " + ex.Message;
                return false;
            }
        }

        /// <summary>A strict field reader: an absent field gives the fallback; a present one of the wrong type records the first error (with
        /// <c>Where</c>: "node 12") and gives the fallback, so the caller fails the replay once it has read the object.</summary>
        sealed class Strict(string where)
        {
            public string Error { get; private set; } = "";

            public void Fail(string field, string want)
            {
                if (Error.Length == 0) Error = (where.Length > 0 ? where + ": " : "") + "'" + field + "' " + want;
            }

            bool Get(JsonElement e, string name, out JsonElement v) => e.TryGetProperty(name, out v) && v.ValueKind != JsonValueKind.Null;

            public string Str(JsonElement e, string name, string fallback)
            {
                if (!Get(e, name, out JsonElement v)) return fallback;
                if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? fallback;
                Fail(name, "is a string, not " + v.GetRawText());
                return fallback;
            }

            public string? NullableStr(JsonElement e, string name)
            {
                if (!Get(e, name, out JsonElement v)) return null;
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                Fail(name, "is a string, not " + v.GetRawText());
                return null;
            }

            /// <summary>A number; <c>null</c> reads as NaN where the writer wrote one for a value that was not finite.</summary>
            public float Num(JsonElement e, string name, float fallback)
            {
                if (!e.TryGetProperty(name, out JsonElement v)) return fallback;
                if (v.ValueKind == JsonValueKind.Null) return float.NaN;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float f)) return f;
                Fail(name, "is a number, not " + v.GetRawText());
                return fallback;
            }

            /// <summary>A number that must be finite (a size, an opacity, a scroll offset).</summary>
            public float Finite(JsonElement e, string name, float fallback, float min = float.NegativeInfinity, float max = float.PositiveInfinity)
            {
                float f = Num(e, name, fallback);
                if (e.TryGetProperty(name, out _) && (!float.IsFinite(f) || f < min || f > max))
                {
                    Fail(name, "is a finite number" + (float.IsFinite(min) ? " from " + min.ToString(CultureInfo.InvariantCulture) : "")
                        + (float.IsFinite(max) ? " to " + max.ToString(CultureInfo.InvariantCulture) : "") + ", not " + e.GetProperty(name).GetRawText());
                    return fallback;
                }
                return f;
            }

            public int Int(JsonElement e, string name, int fallback)
            {
                if (!Get(e, name, out JsonElement v)) return fallback;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
                Fail(name, "is a whole number, not " + v.GetRawText());
                return fallback;
            }

            public bool Bool(JsonElement e, string name)
            {
                if (!Get(e, name, out JsonElement v)) return false;
                if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.ValueKind == JsonValueKind.True;
                Fail(name, "is true or false, not " + v.GetRawText());
                return false;
            }

            /// <summary>A rect <c>[x, y, w, h]</c>: four finite numbers, w and h at or above 0. <paramref name="required"/>: absent is an error too.</summary>
            public RectF Rect(JsonElement e, string name, RectF fallback, bool required = false)
            {
                if (!Get(e, name, out JsonElement a))
                {
                    if (required) Fail(name, "is missing (a rect [x, y, w, h])");
                    return fallback;
                }
                if (a.ValueKind == JsonValueKind.Array && a.GetArrayLength() == 4)
                {
                    Span<float> v = stackalloc float[4];
                    bool ok = true;
                    for (int k = 0; k < 4 && ok; k++) ok = a[k].ValueKind == JsonValueKind.Number && a[k].TryGetSingle(out v[k]) && float.IsFinite(v[k]);
                    if (ok && v[2] >= 0f && v[3] >= 0f) return new RectF(v[0], v[1], v[2], v[3]);
                }
                Fail(name, "is a rect [x, y, w, h] of finite numbers with w and h at or above 0, not " + a.GetRawText());
                return fallback;
            }

            public Edges4 Edges(JsonElement e, string name)
            {
                if (!Get(e, name, out JsonElement a)) return default;
                if (a.ValueKind == JsonValueKind.Array && a.GetArrayLength() == 4)
                {
                    Span<float> v = stackalloc float[4];
                    bool ok = true;
                    for (int k = 0; k < 4 && ok; k++) ok = a[k].ValueKind == JsonValueKind.Number && a[k].TryGetSingle(out v[k]) && float.IsFinite(v[k]);
                    if (ok) return new Edges4(v[0], v[1], v[2], v[3]);
                }
                Fail(name, "is four finite numbers [left, top, right, bottom], not " + a.GetRawText());
                return default;
            }

            /// <summary>A colour <c>#RRGGBBAA</c>; absent: transparent (the default every reader of a colour treats as "not known").</summary>
            public ColorF Color(JsonElement e, string name)
            {
                if (!Get(e, name, out JsonElement c)) return default;
                string hex = c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                if (hex.Length == 9 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
                    return new ColorF(((v >> 24) & 255) / 255f, ((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
                Fail(name, "is a colour '#RRGGBBAA', not " + c.GetRawText());
                return default;
            }

            public List<string>? Strings(JsonElement e, string name)
            {
                if (!Get(e, name, out JsonElement a)) return null;
                if (a.ValueKind != JsonValueKind.Array) { Fail(name, "is an array of strings, not " + a.GetRawText()); return null; }
                var list = new List<string>();
                foreach (JsonElement v in a.EnumerateArray())
                {
                    if (v.ValueKind != JsonValueKind.String) { Fail(name, "is an array of strings; it holds " + v.GetRawText()); return null; }
                    list.Add(v.GetString() ?? "");
                }
                return list;
            }

            /// <summary>An object field (a node's <c>scroll</c>, <c>border</c>, <c>layout</c>, <c>text</c>); a present non-object is an error.</summary>
            public bool Object(JsonElement e, string name, out JsonElement value)
            {
                if (!Get(e, name, out value)) return false;
                if (value.ValueKind == JsonValueKind.Object) return true;
                Fail(name, "is an object, not " + value.GetRawText());
                return false;
            }
        }

        static InspectLint.Node ReadNode(JsonElement e, int index, Strict r)
        {
            if (!e.TryGetProperty("p", out _)) r.Fail("p", "is missing (the parent's index, -1 for a root)");
            int axis = 0;
            float offset = 0f, max = 0f;
            bool moving = false;
            if (r.Object(e, "scroll", out JsonElement scroll))
            {
                axis = r.Int(scroll, "axis", 0);
                if (axis is < 0 or > 2) r.Fail("scroll.axis", "is 0, 1 (horizontal) or 2 (vertical), not " + axis);
                offset = r.Finite(scroll, "offset", 0f);
                max = r.Finite(scroll, "max", 0f);
                moving = r.Bool(scroll, "moving");
            }
            float borderW = 0f;
            ColorF borderC = default;
            if (r.Object(e, "border", out JsonElement border))
            {
                borderW = r.Finite(border, "w", 0f, 0f);
                borderC = r.Color(border, "color");
            }
            int fade = r.Int(e, "fade", 0);
            if (fade is < 0 or > 255) r.Fail("fade", "is an edge mask from 0 to 255, not " + fade);
            bool seeded = e.TryGetProperty("seed", out _);
            InspectLint.Declared layout = r.Object(e, "layout", out JsonElement l) ? ReadLayout(l, r) : InspectLint.Declared.None;
            InspectLint.TextRun? text = r.Object(e, "text", out JsonElement t) ? ReadText(t, r) : null;
            return new InspectLint.Node(index, r.Int(e, "p", -1), r.NullableStr(e, "key"), r.Str(e, "path", ""), r.Rect(e, "r", default, required: true))
            {
                Depth = r.Int(e, "d", 0),
                Kind = r.Str(e, "kind", ""),
                Clips = r.Bool(e, "clips"),
                ScrollAxis = axis,
                ScrollOffset = offset,
                ScrollMax = max,
                ScrollMoving = moving,
                Seeded = seeded,
                SeedScreen = r.Rect(e, "seed", RectF.Infinite),
                // A snapshot written before the seed was a flag has a seed without its content cut when the cut was open: unbounded.
                SeedContent = r.Rect(e, "seedContent", RectF.Infinite),
                SeedShown = !e.TryGetProperty("seedShown", out _) || r.Bool(e, "seedShown"),
                FadeEdges = (byte)fade,
                ClipRect = r.Rect(e, "clip", RectF.Infinite),
                ZStack = r.Bool(e, "zstack"),
                Column = !r.Bool(e, "row"),
                Layout = layout,
                Interactive = r.Bool(e, "interactive"),
                Disabled = r.Bool(e, "disabled"),
                Role = r.Str(e, "role", ""),
                ClickId = r.Int(e, "click", 0),
                Drag = r.Bool(e, "drag"),
                Slop = r.Edges(e, "slop"),
                Lane = e.TryGetProperty("lane", out _) ? r.Edges(e, "lane") : InspectLint.NoLane,
                Pressable = !e.TryGetProperty("pressable", out _) || r.Bool(e, "pressable"),
                Label = r.Str(e, "label", ""),
                LabelGlyph = r.Str(e, "labelGlyph", ""),
                Animating = r.Bool(e, "animating"),
                Opacity = r.Finite(e, "opacity", 1f, 0f, 1f),
                Inert = r.Bool(e, "inert"),
                Sticky = r.Bool(e, "sticky"),
                Fill = r.Color(e, "fill"),
                BorderWidth = borderW,
                BorderColor = borderC,
                Image = r.Bool(e, "image"),
                Gradient = r.Bool(e, "gradient"),
                Visual = r.Str(e, "visual", ""),
                Text = text,
                TextCount = r.Int(e, "tc", 0),
                ImageCount = r.Int(e, "ic", 0),
            };
        }

        static InspectLint.Declared ReadLayout(JsonElement e, Strict r) => new(r.Finite(e, "gap", 0f), r.Edges(e, "pad"), r.Edges(e, "margin"))
        {
            W = r.Num(e, "w", float.NaN),
            H = r.Num(e, "h", float.NaN),
            MinW = r.Num(e, "minW", float.NaN),
            MinH = r.Num(e, "minH", float.NaN),
            MaxW = r.Num(e, "maxW", float.NaN),
            MaxH = r.Num(e, "maxH", float.NaN),
            Grow = r.Finite(e, "grow", 0f),
            Shrink = r.Finite(e, "shrink", 0f),
            Basis = r.Num(e, "basis", float.NaN),
            Justify = r.Str(e, "justify", "Start"),
            AlignItems = r.Str(e, "alignItems", "Stretch"),
            AlignSelf = r.Str(e, "alignSelf", "Auto"),
            JustifySelf = r.Str(e, "justifySelf", "Auto"),
            Wrap = r.Bool(e, "wrap"),
        };

        static InspectLint.TextRun ReadText(JsonElement e, Strict r)
        {
            if (!e.TryGetProperty("size", out _)) r.Fail("text.size", "is missing (the run's size in DIP)");
            float size = r.Finite(e, "size", 0f, 0f);
            if (size <= 0f && e.TryGetProperty("size", out _)) r.Fail("text.size", "is above 0");
            string trim = r.Str(e, "trim", "None");
            if (trim is not ("None" or "Clip" or "CharacterEllipsis" or "WordEllipsis")) r.Fail("text.trim", "is None, Clip, CharacterEllipsis or WordEllipsis, not '" + trim + "'");
            return new InspectLint.TextRun(r.Str(e, "s", ""), size)
            {
                LineHeight = r.Finite(e, "lh", 0f, 0f),
                Weight = r.Int(e, "weight", 400),
                MaxLines = r.Int(e, "maxLines", 0),
                Wraps = r.Bool(e, "wraps"),
                Trim = trim,
                Trimmed = r.Bool(e, "trimmed"),
                NaturalW = r.Finite(e, "nw", 0f),
                NaturalH = r.Finite(e, "nh", 0f),
                InkTop = r.Finite(e, "inkTop", float.NaN),
                InkBottom = r.Finite(e, "inkBottom", float.NaN),
                OverflowX = r.Bool(e, "ox"),
                OverflowY = r.Bool(e, "oy"),
                Glyph = r.Bool(e, "glyph"),
                GlyphName = r.Str(e, "glyphName", ""),
                Spans = r.Bool(e, "spans"),
                Color = r.Color(e, "color"),
                Align = r.Str(e, "align", "Start"),
                Ground = r.Color(e, "ground"),
                GroundShare = r.Finite(e, "groundShare", 0f, 0f, 1f),
                Worst = r.Finite(e, "worst", 0f, 0f),
                WorstGround = r.Color(e, "worstGround"),
                GapPixels = r.Int(e, "gapPixels", 0),
            };
        }

        static IReadOnlyList<LintEdge> ReadEdges(JsonElement root, Strict r)
        {
            var edges = new List<LintEdge>();
            if (!root.TryGetProperty("edges", out JsonElement list) || list.ValueKind == JsonValueKind.Null) return edges;
            if (list.ValueKind != JsonValueKind.Array) { r.Fail("edges", "is an array, not " + list.GetRawText()); return edges; }
            foreach (JsonElement e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) { r.Fail("edges", "holds objects, not " + e.GetRawText()); break; }
                string at = "edges[" + edges.Count.ToString(CultureInfo.InvariantCulture) + "]";
                string side = r.Str(e, "side", "left"), rule = r.Str(e, "rule", "");
                if (side is not ("left" or "right")) r.Fail(at + ".side", "is left or right, not '" + side + "'");
                if (rule is not (InspectLint.Rules.Gutter or InspectLint.Rules.RightEdge) || r.Str(e, "id", "").Length == 0 || !e.TryGetProperty("expected", out _))
                {
                    r.Fail(at, "needs rule (gutter or right-edge), id and expected");
                    break;
                }
                edges.Add(new LintEdge(r.Str(e, "rule", ""), r.Str(e, "id", ""), r.Str(e, "what", ""), side == "right" ? LintSide.Right : LintSide.Left,
                    r.Finite(e, "expected", 0f), r.Finite(e, "tolerance", 0f, 0f), r.Strings(e, "keys"), r.Strings(e, "prefixes"), r.Str(e, "underKey", ""),
                    r.Bool(e, "glyphsOnly"), r.Bool(e, "advisory"), r.Bool(e, "column")));
            }
            return edges;
        }

        static IReadOnlyList<LintPolicy> ReadPolicies(JsonElement root, Strict r)
        {
            var policies = new List<LintPolicy>();
            if (!root.TryGetProperty("policies", out JsonElement list) || list.ValueKind == JsonValueKind.Null) return policies;
            if (list.ValueKind != JsonValueKind.Array) { r.Fail("policies", "is an array, not " + list.GetRawText()); return policies; }
            foreach (JsonElement e in list.EnumerateArray())
            {
                string at = "policies[" + policies.Count + "]";
                if (e.ValueKind != JsonValueKind.Object) { r.Fail(at, "is an object, not " + e.GetRawText()); break; }
                string intentName = r.Str(e, "intent", "");
                if (!Enum.TryParse(intentName, out LintIntent intent) || !Enum.IsDefined(intent) || intentName.Length == 0 || char.IsAsciiDigit(intentName[0]))
                {
                    r.Fail(at + ".intent", "is one of " + string.Join(", ", Enum.GetNames<LintIntent>()) + ", not '" + intentName + "'");
                    break;
                }
                string prefix = r.Str(e, "prefix", ""), reason = r.Str(e, "reason", "");
                if (prefix.Length == 0 || reason.Trim().Length == 0) { r.Fail(at, "needs a prefix and a reason"); break; }
                policies.Add(new LintPolicy(intent, prefix, reason, r.Finite(e, "value", 0f), r.Str(e, "rule", "")));
            }
            return policies;
        }

        /// <summary>An array of rects <c>[[x, y, w, h], ...]</c> (absent or null: none), each checked as <see cref="Strict.Rect"/> checks one.</summary>
        static IReadOnlyList<RectF> ReadRects(JsonElement root, string name, Strict r)
        {
            var rects = new List<RectF>();
            if (!root.TryGetProperty(name, out JsonElement list) || list.ValueKind == JsonValueKind.Null) return rects;
            if (list.ValueKind != JsonValueKind.Array) { r.Fail(name, "is an array of rects [x, y, w, h], not " + list.GetRawText()); return rects; }
            int k = 0;
            Span<float> v = stackalloc float[4];
            foreach (JsonElement e in list.EnumerateArray())
            {
                bool ok = e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 4;
                for (int j = 0; j < 4 && ok; j++) ok = e[j].ValueKind == JsonValueKind.Number && e[j].TryGetSingle(out v[j]) && float.IsFinite(v[j]);
                if (!ok || v[2] < 0f || v[3] < 0f)
                {
                    r.Fail(name + "[" + k.ToString(CultureInfo.InvariantCulture) + "]", "is a rect [x, y, w, h] of finite numbers with w and h at or above 0, not " + e.GetRawText());
                    break;
                }
                rects.Add(new RectF(v[0], v[1], v[2], v[3]));
                k++;
            }
            return rects;
        }

        static IReadOnlyList<InspectLint.ScaleEntry> ReadScale(JsonElement root, Strict r)
        {
            var scale = new List<InspectLint.ScaleEntry>();
            if (!root.TryGetProperty("scale", out JsonElement list) || list.ValueKind == JsonValueKind.Null) return scale;
            if (list.ValueKind != JsonValueKind.Array) { r.Fail("scale", "is an array, not " + list.GetRawText()); return scale; }
            foreach (JsonElement e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) { r.Fail("scale", "holds objects {name, value}, not " + e.GetRawText()); break; }
                scale.Add(new InspectLint.ScaleEntry(r.Str(e, "name", ""), r.Finite(e, "value", 0f)));
            }
            return scale;
        }

        /// <summary>A string field of a baseline line ("" when absent or not a string).</summary>
        static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

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
