// ── Screens/Diagnostics.Inspect.Lint.Pack.cs ─────────────────────────────────────────────────────────────────────────
// lint rules over the pack grids a page mounts (the pinned Home's board): pack.hole (gate) and pack.tools (todo)
//
// Role: SHELL (UI thread for the capture; the rules are pure)
// Spec: C:/WAVEE/scratch/pins/pinned-home-tech.md section 3.3.
//
// The snapshot carries every registered grid (HomeBoardInspect.RegisterPack) as a PackShot: its entries, metrics, width, the
// key of each entry, and each realized wrapper's presented rect with the grid's window origin. pack.hole re-packs the
// entries (PackLayout is pure: the result depends on entries, metrics and width only) and runs PackLayout.Verify, so the
// rule covers the whole board, not just what is on screen; then, on a settled frame, every realized wrapper that PAINTS (in the
// snapshot: not a parked keep-alive page's, not a hidden one's) must sit on its packed rect (a layout or animation bug that
// leaves a tile short of its box). pack.tools: an edit tool strip (home:tile:<id>:tools) drawn over text or a control of its
// own tile's content.

using System.Globalization;
using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class InspectLint
    {
        /// <summary>One mounted pack grid as the lint sees it. <see cref="Nodes"/>: each entry's realized wrapper rect in window space
        /// (<see cref="RectF"/> with zero size: not realized); <see cref="Origin"/>: the grid's window-space origin.</summary>
        public sealed record PackShot(string Key, PackEntry[] Entries, PackMetrics Metrics, float Width, string?[] Keys, RectF[] Nodes,
            Point2 Origin, bool HasOrigin);

        /// <summary>A packed rect may sit this far off its wrapper before pack.hole's scene half calls it a fault.</summary>
        const float PackSlackDip = 0.5f;

        /// <summary>pack.hole: <see cref="PackLayout.Verify"/> over each grid, then (settled frames only) every realized wrapper that paints on its
        /// packed rect. A parked page's grid stays registered (Home kept alive behind Settings) and draws nothing: a tile it parked mid-motion
        /// is off its rect where no one sees it and lands when the page comes back, so the scene half judges only wrappers in the snapshot.</summary>
        static int PackHoleRule(Snapshot s, List<Finding> into)
        {
            int looked = 0;
            Span<PackFault> faults = stackalloc PackFault[64];
            var painted = new HashSet<string>(StringComparer.Ordinal);
            foreach (Node n in s.Nodes)
                if (n.Key is { } nk && nk.StartsWith(TilePrefix, StringComparison.Ordinal)) painted.Add(nk);
            foreach (PackShot p in s.Packs)
            {
                looked++;
                var result = new PackResult();
                PackLayout.Pack(p.Entries, p.Metrics, p.Width, result);
                int n = PackLayout.Verify(p.Entries, p.Metrics, result, faults);
                for (int i = 0; i < Math.Min(n, faults.Length); i++)
                {
                    PackFault f = faults[i];
                    var keys = KeysAround(p, result, f);
                    into.Add(new Finding(Rules.PackHole, Severity.Error,
                        $"{p.Key}: group {f.Group}, band {f.Band}: {Describe(f)} at x {F(f.Where.X)} y {F(f.Where.Y)} ({F(f.Where.W)} x {F(f.Where.H)})",
                        keys.Count > 0 ? keys : [p.Key])
                    {
                        Pattern = p.Key,
                        Cause = "pack:" + f.Kind.ToString().ToLowerInvariant(),
                    });
                }
                // The scene half: a settled frame's wrappers on their packed rects.
                if (!p.HasOrigin || s.MovingCount > 0) continue;
                for (int i = 0; i < p.Entries.Length && i < result.Count; i++)
                {
                    if (i >= p.Nodes.Length || p.Nodes[i].W <= 0f || result.BandOf[i] < 0) continue;
                    if (p.Keys[i] is not { } tileKey || !painted.Contains(TileKey(tileKey))) continue;   // parked or hidden: it draws nothing
                    RectF packed = result.Rects[i];
                    var want = new RectF(p.Origin.X + packed.X, p.Origin.Y + packed.Y, packed.W, packed.H);
                    RectF got = p.Nodes[i];
                    float off = Math.Max(Math.Max(Math.Abs(got.X - want.X), Math.Abs(got.Y - want.Y)),
                                         Math.Max(Math.Abs(got.W - want.W), Math.Abs(got.H - want.H)));
                    if (off <= PackSlackDip) continue;
                    string key = TileKey(tileKey);
                    into.Add(new Finding(Rules.PackHole, Severity.Error,
                        $"{key} sits {F(off)} DIP off its packed rect: drawn at {F(got.X)},{F(got.Y)} {F(got.W)} x {F(got.H)}, packed at {F(want.X)},{F(want.Y)} {F(want.W)} x {F(want.H)}",
                        [key])
                    {
                        Pattern = p.Key,
                        Cause = "pack:off-rect",
                    });
                }
            }
            return looked;
        }

        const string TilePrefix = "home:tile:";

        /// <summary>The wrapper node's key of a grid item: the board's item keys already carry the tile prefix (a second one named no node).</summary>
        static string TileKey(string itemKey) => itemKey.StartsWith(TilePrefix, StringComparison.Ordinal) ? itemKey : TilePrefix + itemKey;

        static string Describe(PackFault f) => f.Kind switch
        {
            PackFaultKind.EmptyCell => "an empty cell",
            PackFaultKind.Overlap => "two tiles overlap",
            PackFaultKind.RaggedBand => "a band short of the right edge",
            PackFaultKind.BelowMinSpan => "a tile below its size class",
            PackFaultKind.OutOfOrder => "reading order differs from entry order",
            PackFaultKind.OutOfGroup => "a tile outside its group",
            _ => f.Kind.ToString(),
        };

        /// <summary>The keys of the tiles in the fault's band (the fault's own entry first).</summary>
        static List<string> KeysAround(PackShot p, PackResult r, PackFault f)
        {
            var keys = new List<string>();
            if (f.Entry >= 0 && f.Entry < p.Keys.Length && p.Keys[f.Entry] is { } own) keys.Add(TileKey(own));
            for (int i = 0; i < r.Count && i < p.Keys.Length && keys.Count < 6; i++)
                if (r.BandOf[i] == f.Band && i != f.Entry && p.Keys[i] is { } k) keys.Add(TileKey(k));
            return keys;
        }

        static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>pack.tools: an edit tool strip (<c>home:tile:&lt;id&gt;:tools</c>) whose rect crosses text or a control of its own tile's content.</summary>
        static int PackToolsRule(Snapshot s, List<Finding> into)
        {
            int looked = 0;
            var nodes = s.Nodes;
            for (int t = 0; t < nodes.Count; t++)
            {
                Node tools = nodes[t];
                if (tools.Key is not { } key || !key.StartsWith("home:tile:", StringComparison.Ordinal) || !key.EndsWith(":tools", StringComparison.Ordinal)) continue;
                string tileKey = key[..^":tools".Length];
                int tile = -1;
                for (int i = tools.Parent; i >= 0; i = nodes[i].Parent)
                    if (string.Equals(nodes[i].Key, tileKey, StringComparison.Ordinal)) { tile = i; break; }
                if (tile < 0) continue;
                looked++;
                RectF tr = tools.Rect;
                if (tr.W <= 0f || tr.H <= 0f) continue;
                for (int i = tile + 1; i < nodes.Count; i++)
                {
                    Node n = nodes[i];
                    if (!Under(nodes, n, tile)) continue;
                    if (Under(nodes, n, t)) continue;                       // the tools' own parts
                    bool content = n.Text is not null || (n.Interactive && !n.Disabled);
                    if (!content || n.Opacity <= 0f) continue;
                    RectF r = n.Rect;
                    float w = Math.Min(tr.X + tr.W, r.X + r.W) - Math.Max(tr.X, r.X), h = Math.Min(tr.Y + tr.H, r.Y + r.H) - Math.Max(tr.Y, r.Y);
                    if (w <= 0.5f || h <= 0.5f) continue;
                    into.Add(new Finding(Rules.PackTools, Severity.Warn,
                        $"{key} crosses {(n.Text is not null ? "text" : "a control")} {n.Ref} of its tile by {F(w)} x {F(h)} DIP: edit tools never sit on content",
                        [key, n.Ref])
                    {
                        Pattern = PatternOfKey(key),
                    });
                    break;
                }
            }
            return looked;
        }

        static bool Under(IReadOnlyList<Node> nodes, Node n, int ancestor)
        {
            for (int i = n.Parent; i >= 0; i = nodes[i].Parent)
                if (i == ancestor) return true;
            return false;
        }
    }

    public static partial class InspectLintScene
    {
        /// <summary>Every registered pack grid as the lint's shots (HomeBoardInspect.RegisterPack).</summary>
        internal static IReadOnlyList<InspectLint.PackShot> CapturePacks()
        {
            var packs = HomeBoardInspect.Packs;
            if (packs.Count == 0) return [];
            var shots = new List<InspectLint.PackShot>(packs.Count);
            foreach (var (key, grid) in packs)
            {
                var entries = grid.Entries.ToArray();
                var keys = new string?[entries.Length];
                var rects = new RectF[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    keys[i] = grid.KeyOf(i);
                    if (keys[i] is { } k && grid.TryNodeRect(k, out RectF r)) rects[i] = r;
                }
                bool hasOrigin = grid.TryOrigin(out Point2 origin);
                shots.Add(new InspectLint.PackShot(key, entries, grid.Metrics, grid.Layout.Width, keys, rects, origin, hasOrigin));
            }
            return shots;
        }
    }

    public static partial class InspectLintStore
    {
        static void WritePacks(Utf8JsonWriter w, IReadOnlyList<InspectLint.PackShot> packs)
        {
            if (packs.Count == 0) return;
            w.WriteStartArray("packs");
            foreach (var p in packs)
            {
                w.WriteStartObject();
                w.WriteString("key", p.Key);
                Num(w, "width", p.Width);
                w.WriteStartObject("metrics");
                w.WriteNumber("columns", p.Metrics.Columns);
                Num(w, "unit", p.Metrics.Unit);
                Num(w, "gap", p.Metrics.Gap);
                Num(w, "groupGap", p.Metrics.GroupGap);
                Num(w, "toolStrip", p.Metrics.ToolStrip);
                w.WriteNumber("toolStripMinRows", p.Metrics.ToolStripMinRows);
                w.WriteEndObject();
                if (p.HasOrigin) { Num(w, "originX", p.Origin.X); Num(w, "originY", p.Origin.Y); }
                w.WriteStartArray("entries");
                for (int i = 0; i < p.Entries.Length; i++)
                {
                    PackEntry e = p.Entries[i];
                    w.WriteStartObject();
                    if (p.Keys[i] is { } k) w.WriteString("key", k);
                    w.WriteNumber("kind", (int)e.Kind);
                    w.WriteNumber("cols", e.Cols);
                    w.WriteNumber("rows", e.Rows);
                    w.WriteNumber("flags", (int)e.Flags);
                    Num(w, "extent", e.Extent);
                    w.WriteNumber("openMinCols", e.OpenMinCols);
                    if (p.Nodes[i].W > 0f) { Num(w, "x", p.Nodes[i].X); Num(w, "y", p.Nodes[i].Y); Num(w, "w", p.Nodes[i].W); Num(w, "h", p.Nodes[i].H); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        /// <summary>The snapshot's packs; an old snapshot has none (the field defaults), and a malformed one is read as none.</summary>
        static IReadOnlyList<InspectLint.PackShot> ReadPacks(JsonElement root)
        {
            if (!root.TryGetProperty("packs", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return [];
            var packs = new List<InspectLint.PackShot>();
            foreach (JsonElement p in list.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("entries", out JsonElement es) || es.ValueKind != JsonValueKind.Array) continue;
                if (!p.TryGetProperty("metrics", out JsonElement m) || m.ValueKind != JsonValueKind.Object) continue;
                var metrics = new PackMetrics(I(m, "columns"), N(m, "unit"), N(m, "gap"), N(m, "groupGap"), N(m, "toolStrip"), I(m, "toolStripMinRows", 2));
                var entries = new List<PackEntry>();
                var keys = new List<string?>();
                var rects = new List<RectF>();
                foreach (JsonElement e in es.EnumerateArray())
                {
                    entries.Add(new PackEntry((PackEntryKind)I(e, "kind"), (byte)I(e, "cols", 1), (byte)I(e, "rows", 1), (PackFlags)I(e, "flags"),
                        N(e, "extent"), (byte)I(e, "openMinCols")));
                    keys.Add(e.TryGetProperty("key", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null);
                    rects.Add(new RectF(N(e, "x"), N(e, "y"), N(e, "w"), N(e, "h")));
                }
                bool hasOrigin = p.TryGetProperty("originX", out _);
                packs.Add(new InspectLint.PackShot(p.TryGetProperty("key", out JsonElement pk) ? pk.GetString() ?? "" : "", [.. entries], metrics,
                    N(p, "width"), [.. keys], [.. rects], new Point2(N(p, "originX"), N(p, "originY")), hasOrigin));
            }
            return packs;

            static float N(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;
            static int I(JsonElement e, string name, int fallback = 0) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
        }
    }
}
