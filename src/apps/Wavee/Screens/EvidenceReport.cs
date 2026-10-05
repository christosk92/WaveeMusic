// ── Screens/EvidenceReport.cs — the evidence bundle's pure half (docs/plans/evidence-diagnostics-implementation.md §B) ──
//
// Everything about an evidence bundle that is a DECISION or a FORMAT, engine-state-free so it is a unit test
// (EvidenceReportTests): the `wavee://diag?cmd=…` verb parse, the bundle folder name, the viewport match, the card's
// health verdict and its pixel rows, and every file's rows — items.tsv, placements.tsv, ledger.tsv, stale.tsv,
// walks.tsv, pixel-x-y.tsv, nodes.tsv, vps.tsv, meta.json, census.json. Hand-written TSV/JSON (NativeAOT-safe, like the
// engine's ScrollProbeCsv); UTF-8 without BOM is the writer's job (EvidenceBundle.cs).

using System.Globalization;
using System.Text;
using FluentGpu.Hosting;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;

namespace Wavee;

/// <summary>The <c>wavee://diag</c> commands (developer-only; refused unless developer mode). <see cref="Shot"/> ..
/// <see cref="Rail"/> are the Store screenshot capture (Screens/StoreShot.cs).</summary>
public enum DiagCommand : byte { None, Bundle, Pixel, Scroll, Viewports, Probe, Xm, Shot, Present, Seek, Stage, Rail, Reveal, Video }

/// <summary>A parsed <c>wavee://diag?cmd=…</c> verb. <see cref="Level"/>: 0 Off, 1 Summary, 2 Trace (−1 = none).
/// <see cref="Uris"/>/<see cref="Kinds"/> are the <c>xm</c> probe's comma-separated entity uris and extension kinds.
/// <para>The capture verbs: <see cref="Zoom"/> (shot: render at this percent of the current zoom, 0 = as is),
/// <see cref="SettleMs"/> (shot: wait this long after presentation mode / zoom before capturing), <see cref="Alpha"/>
/// (shot: keep the window's translucency), <see cref="Keys"/> (shot: the key prefixes to export, empty = every shown
/// keyed node), <see cref="On"/> (present: 1/0; seek: 1 pause after, 0 resume, −1 leave; stage: 1 open, 0 close, −1
/// leave), <see cref="Ms"/> (seek: the position), <see cref="Mode"/> (stage: lyrics/visualizer/queue/artist; rail:
/// lyrics/queue/off), <see cref="Face"/> (stage: a visualizer face name), <see cref="Gallery"/> (stage: 1/0, −1 leave).
/// <c>reveal</c>: <see cref="Viewport"/> (a scroll key prefix) scrolled so the keyed element <see cref="Keys"/>[0] sits
/// at its top; <c>video</c>: <see cref="Mode"/> the video surface's placement (docked/floating/detached/fullscreen/off);
/// on <c>shot</c>, <see cref="Viewport"/> + <see cref="Reveal"/> do the same after the zoom reflowed the page.</para></summary>
public readonly record struct DiagVerb(DiagCommand Command, string Tag = "", int X = 0, int Y = 0, bool Dip = false,
    string Viewport = "", double To = 0.0, bool Glide = false, int Level = -1, string[]? Uris = null, int[]? Kinds = null,
    int Zoom = 0, int SettleMs = 0, bool Alpha = false, string[]? Keys = null, int On = -1, int Ms = -1, string Mode = "",
    string Face = "", int Gallery = -1, string Reveal = "");

/// <summary>One nodes.tsv row: a node referenced by any exported record, with its readable name, element kind and
/// window rect (DIP; empty when gone).</summary>
public readonly record struct EvidenceNodeRow(int Index, uint Gen, string Path, string Type, float X, float Y, float W, float H,
    float LayoutX = float.NaN, float LayoutY = float.NaN, float OwnDx = 0f, float OwnDy = 0f);

/// <summary>One row of the Evidence card's pixel table.</summary>
public readonly record struct EvidencePixelRow(string Index, string Kind, string Node, string Alpha, string Feather1, string Feather2,
    string Tile, string Raster, bool Stale);

/// <summary>One keyed node of a Store shot (shot.json): its key, element kind and window rect (DIP).</summary>
public readonly record struct ShotKeyRow(string Key, string Type, float X, float Y, float W, float H);

/// <summary>shot.json's facts. <see cref="Scale"/> = device px per DIP of the captured present (zoom included);
/// <see cref="PositionMs"/> = the playback position when the frame was armed (−1 = nothing playing).</summary>
public readonly record struct ShotMeta(string Tag, string Route, string Arg, float Scale, float Zoom, int WidthPx, int HeightPx,
    bool Alpha, bool Presenting, int PositionMs, string CreatedLocal);

/// <summary>meta.json's facts.</summary>
public readonly record struct EvidenceMeta(string Build, int Pid, string Profile, string Route, string Arg, long NavId,
    ulong PublishSeq, int TableFrame, long Qpc, float Scale, int WidthPx, int HeightPx, bool Captured, string Tag,
    string CreatedLocal);

public static class EvidenceReport
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── the verb ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Parse the query of <c>wavee://diag?…</c> (with or without the leading <c>?</c>). False for a missing /
    /// unknown <c>cmd</c> or a command missing its required argument (pixel: x and y; scroll: vp and to; probe: level).
    /// Never throws.</summary>
    public static bool TryParseDiag(string? query, out DiagVerb verb)
    {
        verb = default;
        if (string.IsNullOrEmpty(query)) return false;
        string cmd = "", tag = "", vp = "", move = "", level = "", uris = "", kinds = "";
        string zoom = "", settle = "", reveal = "", alpha = "", keys = "", on = "", ms = "", pause = "", open = "", mode = "", face = "", gallery = "";
        string? x = null, y = null, to = null;
        bool dip = false;
        ReadOnlySpan<char> q = query;
        if (q.Length > 0 && q[0] == '?') q = q[1..];
        while (q.Length > 0)
        {
            int amp = q.IndexOf('&');
            ReadOnlySpan<char> pair = amp < 0 ? q : q[..amp];
            q = amp < 0 ? default : q[(amp + 1)..];
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            string key = Unescape(eq < 0 ? pair : pair[..eq]);
            string val = eq < 0 ? "" : Unescape(pair[(eq + 1)..]);
            switch (key.ToLowerInvariant())
            {
                case "cmd": cmd = val; break;
                case "tag": tag = val; break;
                case "x": x = val; break;
                case "y": y = val; break;
                case "dip": dip = val is "1" or "true"; break;
                case "vp": vp = val; break;
                case "to": to = val; break;
                case "move": move = val; break;
                case "level": level = val; break;
                case "uris": uris = val; break;
                case "kinds": kinds = val; break;
                case "zoom": zoom = val; break;
                case "settle": settle = val; break;
                case "reveal": reveal = val; break;
                case "alpha": alpha = val; break;
                case "keys": keys = val; break;
                case "on": on = val; break;
                case "ms": ms = val; break;
                case "pause": pause = val; break;
                case "open": open = val; break;
                case "mode": mode = val; break;
                case "face": face = val; break;
                case "gallery": gallery = val; break;
            }
        }
        switch (cmd.ToLowerInvariant())
        {
            case "bundle":
                verb = new DiagVerb(DiagCommand.Bundle, Tag: SanitizeTag(tag));
                return true;
            case "pixel":
                if (!TryInt(x, out int px) || !TryInt(y, out int py)) return false;
                verb = new DiagVerb(DiagCommand.Pixel, X: px, Y: py, Dip: dip);
                return true;
            case "scroll":
                if (vp.Length == 0 || !double.TryParse(to, NumberStyles.Float, Inv, out double t) || !double.IsFinite(t)) return false;
                verb = new DiagVerb(DiagCommand.Scroll, Viewport: vp, To: t, Glide: move.Equals("glide", StringComparison.OrdinalIgnoreCase));
                return true;
            case "vps":
                verb = new DiagVerb(DiagCommand.Viewports);
                return true;
            case "xm":
            {
                // The extended-metadata evidence probe: `uris` required (spotify uris, comma-separated, at most 300),
                // `kinds` optional (default 10,99,182,185 — the track row's identity, both video traits, play count).
                string[] list = SplitList(uris);
                if (list.Length == 0 || list.Length > 300) return false;
                int[] kindList = kinds.Length == 0 ? [10, 99, 182, 185] : ParseKinds(kinds);
                if (kindList.Length == 0) return false;
                verb = new DiagVerb(DiagCommand.Xm, Tag: SanitizeTag(tag.Length == 0 ? "xm" : tag), Uris: list, Kinds: kindList);
                return true;
            }
            case "probe":
            {
                int l = level.ToLowerInvariant() switch { "off" => 0, "summary" => 1, "trace" => 2, _ => -1 };
                if (l < 0) return false;
                verb = new DiagVerb(DiagCommand.Probe, Level: l);
                return true;
            }
            case "shot":
            {
                // zoom: a percent of the CURRENT zoom (100 = as is), 50..400; settle: 0..10 s, default 700 ms; alpha
                // defaults ON (the Store composer lays the window over its own field).
                int z = 0;
                if (zoom.Length > 0 && (!TryInt(zoom, out z) || z < 50 || z > 400)) return false;
                int st = 700;
                if (settle.Length > 0 && (!TryInt(settle, out st) || st < 0 || st > 10_000)) return false;
                string[] prefixes = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                verb = new DiagVerb(DiagCommand.Shot, Tag: SanitizeTag(tag.Length == 0 ? "shot" : tag), Zoom: z == 100 ? 0 : z,
                    SettleMs: st, Alpha: alpha is not ("0" or "false"), Keys: prefixes, Viewport: reveal.Length > 0 ? vp : "", Reveal: vp.Length > 0 ? reveal : "");
                return true;
            }
            case "present":
            {
                int o = Flag(on);
                if (o < 0) return false;
                verb = new DiagVerb(DiagCommand.Present, On: o);
                return true;
            }
            case "seek":
            {
                if (!TryInt(ms, out int pos) || pos < 0) return false;
                verb = new DiagVerb(DiagCommand.Seek, Ms: pos, On: Flag(pause));
                return true;
            }
            case "stage":
            {
                string m = mode.ToLowerInvariant();
                if (m.Length > 0 && m is not ("lyrics" or "visualizer" or "queue" or "artist")) return false;
                int o = Flag(open), g = Flag(gallery);
                if (o < 0 && m.Length == 0 && face.Length == 0 && g < 0) return false;   // a stage verb that changes nothing
                verb = new DiagVerb(DiagCommand.Stage, On: o, Mode: m, Face: face, Gallery: g);
                return true;
            }
            case "video":
            {
                string m = mode.ToLowerInvariant();
                if (m is not ("docked" or "floating" or "detached" or "fullscreen" or "off")) return false;
                verb = new DiagVerb(DiagCommand.Video, Mode: m);
                return true;
            }
            case "reveal":
            {
                if (vp.Length == 0 || keys.Trim().Length == 0) return false;
                verb = new DiagVerb(DiagCommand.Reveal, Viewport: vp, Keys: [keys.Trim()]);
                return true;
            }
            case "rail":
            {
                string m = mode.ToLowerInvariant();
                if (m is not ("lyrics" or "queue" or "off")) return false;
                verb = new DiagVerb(DiagCommand.Rail, Mode: m);
                return true;
            }
            default:
                return false;
        }

        // "1"/"true" = 1, "0"/"false" = 0, anything else (absent included) = −1
        static int Flag(string v) => v is "1" or "true" ? 1 : v is "0" or "false" ? 0 : -1;

        static string[] SplitList(string s)
        {
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kept = new List<string>(parts.Length);
            foreach (string p in parts) if (p.StartsWith("spotify:", StringComparison.Ordinal)) kept.Add(p);
            return kept.ToArray();
        }

        static int[] ParseKinds(string s)
        {
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kept = new List<int>(parts.Length);
            foreach (string p in parts)
            {
                if (!int.TryParse(p, NumberStyles.Integer, Inv, out int k) || k <= 0) return [];
                if (!kept.Contains(k)) kept.Add(k);
            }
            return kept.ToArray();
        }

        static bool TryInt(string? s, out int v)
        {
            v = 0;
            if (!double.TryParse(s, NumberStyles.Float, Inv, out double d) || !double.IsFinite(d)) return false;
            v = (int)Math.Floor(d);
            return true;
        }

        static string Unescape(ReadOnlySpan<char> s)
        {
            try { return Uri.UnescapeDataString(s.ToString()).Trim(); }
            catch (UriFormatException) { return s.ToString().Trim(); }
        }
    }

    /// <summary>A tag safe in a folder name: letters, digits, '-', '_' and '.' kept, anything else '_', at most 48
    /// chars; empty → "bundle".</summary>
    public static string SanitizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "bundle";
        var sb = new StringBuilder(Math.Min(tag.Length, 48));
        foreach (char c in tag.Trim())
        {
            if (sb.Length == 48) break;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        string s = sb.ToString().Trim('.');
        return s.Length == 0 ? "bundle" : s;
    }

    /// <summary><c>yyyyMMdd-HHmmss-&lt;tag&gt;</c> (local time) — sorts by capture time.</summary>
    public static string BundleFolderName(DateTime local, string tag)
        => local.ToString("yyyyMMdd-HHmmss", Inv) + "-" + SanitizeTag(tag);

    /// <summary>A viewport answers <c>vp=&lt;prefix&gt;</c> when its ScrollKey starts with it, or when the key's part after
    /// its page scope (<c>tab0/…</c>) does — the shell composes every page's key onto <c>Shell.PageScrollScope</c>.</summary>
    public static bool ViewportMatches(string? scrollKey, string prefix)
    {
        if (string.IsNullOrEmpty(scrollKey) || string.IsNullOrEmpty(prefix)) return false;
        if (scrollKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        int slash = scrollKey.IndexOf('/');
        return slash >= 0 && scrollKey.AsSpan(slash + 1).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The card's verdict: no stale tile, no exposed-missing tile, no refused scratch lease.</summary>
    public static bool IsHealthy(TileCensus c, int scratchRefused) => c.StaleTiles == 0 && c.ExposedMissing == 0 && scratchRefused == 0;

    // ── flags / numbers ──────────────────────────────────────────────────────────────────────────────────────────

    public static string ItemFlags(byte f)
    {
        if (f == 0) return "-";
        var sb = new StringBuilder();
        void Add(string s) { if (sb.Length > 0) sb.Append('|'); sb.Append(s); }
        if ((f & ItemRecordFlags.HasLayer) != 0) Add("layer");
        if ((f & ItemRecordFlags.GroupHit) != 0) Add("groupHit");
        if ((f & ItemRecordFlags.GroupRendered) != 0) Add("groupRendered");
        if ((f & ItemRecordFlags.Degraded) != 0) Add("degraded");
        if ((f & ItemRecordFlags.StickyEngaged) != 0) Add("sticky");
        if ((f & ItemRecordFlags.ClipBounded) != 0) Add("clip");
        return sb.ToString();
    }

    public static string KindName(byte kind) => ((CompositeKind)kind).ToString();

    static string F(float v) => float.IsFinite(v) ? v.ToString("0.###", Inv) : "";
    static string F3(float v) => v.ToString("0.000", Inv);
    static string H(ulong v) => v.ToString("x16", Inv);
    static string I(long v) => v.ToString(Inv);

    static string Feather(in EdgeFeather f)
        => f.IsNone ? "-" : $"{F(f.Rect.X)},{F(f.Rect.Y)},{F(f.Rect.W)},{F(f.Rect.H)} L{F(f.BandLeft)} T{F(f.BandTop)} R{F(f.BandRight)} B{F(f.BandBottom)} {f.Falloff} i{F(f.Intensity)}";

    // ── the files ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>items.tsv: the frame's composite items in painter order.</summary>
    public static string ItemsTsv(ReadOnlySpan<ItemRecord> items, Func<int, uint, string> nodeName)
    {
        var sb = new StringBuilder(128 + items.Length * 160);
        sb.Append("index\tkind\tnode\tnodeKey\tslice\talpha\tinherited\tflags\tclip\ttrans\tfeather1\tfeather2\tblur\tstickyTopPx\tgroupCount\tfeatherInterior\n");
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly ItemRecord it = ref items[i];
            bool clip = (it.Flags & ItemRecordFlags.ClipBounded) != 0;
            sb.Append(I(i)).Append('\t').Append(KindName(it.Kind)).Append('\t')
              .Append(I(it.NodeIndex)).Append(':').Append(I(it.Gen)).Append('\t')
              .Append(it.NodeIndex > 0 ? nodeName(it.NodeIndex, it.Gen) : "-").Append('\t')
              .Append(I(it.SliceId)).Append('\t').Append(F3(it.AlphaQ8 / 255f)).Append('\t').Append(I(it.Inherited)).Append('\t')
              .Append(ItemFlags(it.Flags)).Append('\t')
              .Append(clip ? $"{it.ClipX},{it.ClipY},{it.ClipW},{it.ClipH}" : "-").Append('\t')
              .Append(I(it.TransDx)).Append(',').Append(I(it.TransDy)).Append('\t')
              .Append(Feather(it.Feather1)).Append('\t').Append(Feather(it.Feather2)).Append('\t')
              .Append(F(it.BlurSigma)).Append('\t')
              .Append(it.StickyTopPx == short.MinValue ? "-" : I(it.StickyTopPx)).Append('\t')
              .Append(I(it.GroupCount)).Append('\t')
              .Append(it.Feather1.IsNone && it.Feather2.IsNone ? "-" : $"{Edge(it.InteriorL)},{Edge(it.InteriorT)},{Edge(it.InteriorR)},{Edge(it.InteriorB)}")
              .Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>A feather-interior edge: "inf" when unbounded (<see cref="EdgeFeatherMask.Unbounded"/>).</summary>
    static string Edge(float v) => MathF.Abs(v) >= EdgeFeatherMask.Unbounded ? (v < 0 ? "-inf" : "inf") : F(v);

    /// <summary>placements.tsv: every tile the frame sampled, with its raster ledger facts.</summary>
    public static string PlacementsTsv(ReadOnlySpan<PlacementRecord> placements)
    {
        var sb = new StringBuilder(64 + placements.Length * 96);
        sb.Append("slice\ttx\tty\tw\th\trasterFrame\trasterHash\twantHash\tstale\trasteredNow\n");
        foreach (ref readonly PlacementRecord p in placements)
            sb.Append(I(p.SliceId)).Append('\t').Append(I(p.Tx)).Append('\t').Append(I(p.Ty)).Append('\t')
              .Append(I(p.W)).Append('\t').Append(I(p.H)).Append('\t').Append(I(p.RasterFrame)).Append('\t')
              .Append(H(p.RasterHash)).Append('\t').Append(H(p.WantHash)).Append('\t')
              .Append(p.Stale != 0 ? "1" : "0").Append('\t').Append(p.RasteredNow != 0 ? "1" : "0").Append('\n');
        return sb.ToString();
    }

    /// <summary>stale.tsv: the frame's STALE tiles (placements whose raster hash differs from the content wanted).</summary>
    public static string StaleTsv(ReadOnlySpan<PlacementRecord> placements, ReadOnlySpan<ItemRecord> items, Func<int, uint, string> nodeName)
    {
        var sb = new StringBuilder(256);
        sb.Append("slice\ttx\tty\tnode\tnodeKey\twant\thave\trasterFrame\n");
        foreach (ref readonly PlacementRecord p in placements)
        {
            if (p.Stale == 0) continue;
            int node = -1; uint gen = 0;
            foreach (ref readonly ItemRecord it in items)
                if (it.SliceId == p.SliceId) { node = it.NodeIndex; gen = it.Gen; break; }
            sb.Append(I(p.SliceId)).Append('\t').Append(I(p.Tx)).Append('\t').Append(I(p.Ty)).Append('\t')
              .Append(I(node)).Append(':').Append(I(gen)).Append('\t').Append(node > 0 ? nodeName(node, gen) : "-").Append('\t')
              .Append(H(p.WantHash)).Append('\t').Append(H(p.RasterHash)).Append('\t').Append(I(p.RasterFrame)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>ledger.tsv: the raster ledger tail (every scheduled raster), oldest first.</summary>
    public static string LedgerTsv(ReadOnlySpan<RasterEntry> entries, Func<int, uint, string> nodeName)
    {
        var sb = new StringBuilder(96 + entries.Length * 110);
        sb.Append("frame\tnode\tnodeKey\tslice\ttx\tty\tw\th\treason\torder\talpha\tfaithful\tscratchRefused\thash\n");
        foreach (ref readonly RasterEntry e in entries)
            sb.Append(I(e.Frame)).Append('\t').Append(I(e.NodeIndex)).Append(':').Append(I(e.Gen)).Append('\t')
              .Append(e.NodeIndex > 0 ? nodeName(e.NodeIndex, e.Gen) : "-").Append('\t')
              .Append(I(e.SliceId)).Append('\t').Append(I(e.Tx)).Append('\t').Append(I(e.Ty)).Append('\t')
              .Append(I(e.W)).Append('\t').Append(I(e.H)).Append('\t').Append(((InvalidationReason)e.Reason).ToString()).Append('\t')
              .Append(I(e.Order)).Append('\t').Append(F3(e.AlphaQ8 / 255f)).Append('\t')
              .Append((e.Flags & RasterEntryFlags.Faithful) != 0 ? "1" : "0").Append('\t')
              .Append((e.Flags & RasterEntryFlags.ScratchRefused) != 0 ? "1" : "0").Append('\t')
              .Append(H(e.Hash)).Append('\n');
        return sb.ToString();
    }

    /// <summary>walks.tsv: the walk ledger tail (every slice re-record and why), oldest first.</summary>
    public static string WalksTsv(ReadOnlySpan<WalkEntry> walks, Func<int, uint, string> nodeName)
    {
        var sb = new StringBuilder(64 + walks.Length * 72);
        sb.Append("frame\tnode\tnodeKey\trole\twhy\tdetail\tbytes\n");
        foreach (ref readonly WalkEntry w in walks)
            sb.Append(I(w.Frame)).Append('\t').Append(I(w.NodeIndex)).Append(':').Append(I(w.Gen)).Append('\t')
              .Append(w.NodeIndex > 0 ? nodeName(w.NodeIndex, w.Gen) : "-").Append('\t')
              .Append(I(w.Role)).Append('\t').Append(((WalkWhy)w.Why).ToString()).Append('\t')
              .Append("0x").Append(w.Detail.ToString("x", Inv)).Append('\t').Append(I(w.Bytes)).Append('\n');
        return sb.ToString();
    }

    /// <summary>pixel-x-y.tsv: one pixel query — the items under window px (<paramref name="x"/>, <paramref name="y"/>) in
    /// painter order, bottom first.</summary>
    public static string PixelTsv(int x, int y, in CompositeFrameHeader frame, ReadOnlySpan<PixelHit> hits, Func<int, uint, string> nodeName)
    {
        var sb = new StringBuilder(256 + hits.Length * 140);
        sb.Append("# pixel x=").Append(I(x)).Append(" y=").Append(I(y)).Append(" frame=").Append(I(frame.Frame))
          .Append(" publishSeq=").Append(frame.PublishSeq.ToString(Inv)).Append(" scale=").Append(F(frame.Scale)).Append('\n');
        sb.Append("order\titem\tkind\tnode\tnodeKey\tslice\talpha\tfeather1\tfeather2\tcoverage\ttile\trasterFrame\trasterHash\twantHash\tstale\tfeatherPiece\n");
        for (int i = 0; i < hits.Length; i++)
        {
            ref readonly PixelHit h = ref hits[i];
            bool tiled = h.Item.Kind is (byte)CompositeKind.Tiles or (byte)CompositeKind.Region;
            sb.Append(I(i)).Append('\t').Append(I(h.ItemIndex)).Append('\t').Append(KindName(h.Item.Kind)).Append('\t')
              .Append(I(h.Item.NodeIndex)).Append(':').Append(I(h.Item.Gen)).Append('\t')
              .Append(h.Item.NodeIndex > 0 ? nodeName(h.Item.NodeIndex, h.Item.Gen) : "-").Append('\t')
              .Append(I(h.Item.SliceId)).Append('\t').Append(F3(h.Alpha)).Append('\t')
              .Append(F3(h.Feather1)).Append('\t').Append(F3(h.Feather2)).Append('\t').Append(F3(h.Coverage)).Append('\t')
              .Append(tiled ? $"{h.Tile.Tx},{h.Tile.Ty}" : "-").Append('\t')
              .Append(tiled ? I(h.TileRasterFrame) : "-").Append('\t')
              .Append(tiled ? H(h.TileRasterHash) : "-").Append('\t').Append(tiled ? H(h.TileWantHash) : "-").Append('\t')
              .Append(h.TileStale ? "1" : "0").Append('\t')
              .Append(h.Item.Feather1.IsNone && h.Item.Feather2.IsNone ? "-" : h.InFeatherBand ? "band" : "interior").Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The Evidence card's pixel table (top item first, as a reader scans it).</summary>
    public static IReadOnlyList<EvidencePixelRow> PixelRows(ReadOnlySpan<PixelHit> hits, Func<int, uint, string> nodeName)
    {
        var rows = new List<EvidencePixelRow>(hits.Length);
        for (int i = hits.Length - 1; i >= 0; i--)
        {
            ref readonly PixelHit h = ref hits[i];
            bool tiled = h.Item.Kind is (byte)CompositeKind.Tiles or (byte)CompositeKind.Region;
            rows.Add(new EvidencePixelRow(I(h.ItemIndex), KindName(h.Item.Kind),
                h.Item.NodeIndex > 0 ? nodeName(h.Item.NodeIndex, h.Item.Gen) : "-",
                h.Alpha.ToString("0.00", Inv),
                h.Item.Feather1.IsNone ? "—" : h.Feather1.ToString("0.00", Inv),
                h.Item.Feather2.IsNone ? "—" : h.Feather2.ToString("0.00", Inv),
                tiled ? $"({h.Tile.Tx},{h.Tile.Ty})" : "—",
                tiled ? "f" + I(h.TileRasterFrame) : "—",
                h.TileStale));
        }
        return rows;
    }

    /// <summary>nodes.tsv: every node the bundle's rows reference.</summary>
    public static string NodesTsv(IReadOnlyList<EvidenceNodeRow> nodes)
    {
        var sb = new StringBuilder(64 + nodes.Count * 80);
        // layoutX/layoutY (2026-09-25, item H): the same window position from the laid-out BOUNDS alone (no translation),
        // and ownDx/ownDy the node's OWN LocalTransform translation — so a row that sits 12 DIP off its sibling tells
        // layout (layoutX differs) from a pose / animation leftover (layoutX equal, x differs).
        sb.Append("index\tgen\tpath\ttype\tx\ty\tw\th\tlayoutX\tlayoutY\townDx\townDy\n");
        foreach (var n in nodes)
            sb.Append(I(n.Index)).Append('\t').Append(I(n.Gen)).Append('\t').Append(n.Path).Append('\t').Append(n.Type).Append('\t')
              .Append(F(n.X)).Append('\t').Append(F(n.Y)).Append('\t').Append(F(n.W)).Append('\t').Append(F(n.H)).Append('\t')
              .Append(F(n.LayoutX)).Append('\t').Append(F(n.LayoutY)).Append('\t').Append(F(n.OwnDx)).Append('\t').Append(F(n.OwnDy))
              .Append('\n');
        return sb.ToString();
    }

    /// <summary>vps.tsv: every live viewport (key, offset, extent, viewport).</summary>
    public static string ViewportsTsv(IReadOnlyList<ViewportInfo> vps)
    {
        var sb = new StringBuilder(64 + vps.Count * 96);
        // The last twelve columns (2026-09-25, item G): the viewport's window rect (DIP — where a harness finds its thumb)
        // and the virtualizer's coverage — cover start/end and the window origin (content DIP), the realized window
        // [first, last), the item count, the persistent prefix and MeasureAll — what a coverage clamp is judged against.
        sb.Append("node\tkey\toffset\textent\tviewport\thorizontal\tx\ty\tw\th\tcoverStart\tcoverEnd\twindowOrigin\tfirst\tlast\titems\tprefix\tmeasureAll\n");
        foreach (var v in vps)
            sb.Append(I(v.NodeIndex)).Append(':').Append(I(v.Gen)).Append('\t').Append(v.ScrollKey ?? "-").Append('\t')
              .Append(v.Offset.ToString("0.###", Inv)).Append('\t').Append(v.Extent.ToString("0.###", Inv)).Append('\t')
              .Append(v.Viewport.ToString("0.###", Inv)).Append('\t').Append(v.Horizontal ? "1" : "0").Append('\t')
              .Append(v.X.ToString("0.###", Inv)).Append('\t').Append(v.Y.ToString("0.###", Inv)).Append('\t')
              .Append(v.W.ToString("0.###", Inv)).Append('\t').Append(v.H.ToString("0.###", Inv)).Append('\t')
              .Append(v.CoverStart.ToString("0.###", Inv)).Append('\t').Append(v.CoverEnd.ToString("0.###", Inv)).Append('\t')
              .Append(v.WindowOrigin.ToString("0.###", Inv)).Append('\t').Append(I(v.FirstRealized)).Append('\t')
              .Append(I(v.LastRealized)).Append('\t').Append(I(v.ItemCount)).Append('\t').Append(I(v.PersistentPrefix)).Append('\t')
              .Append(v.MeasureAll ? "1" : "0").Append('\n');
        return sb.ToString();
    }

    /// <summary>meta.json.</summary>
    public static string MetaJson(in EvidenceMeta m, IReadOnlyList<ViewportInfo> vps)
    {
        var sb = new StringBuilder(512 + vps.Count * 128);
        sb.Append("{\n");
        Str(sb, "build", m.Build); Str(sb, "created", m.CreatedLocal); Str(sb, "tag", m.Tag);
        Num(sb, "pid", m.Pid); Str(sb, "profile", m.Profile); Str(sb, "route", m.Route); Str(sb, "arg", m.Arg);
        Num(sb, "navId", m.NavId); Num(sb, "publishSeq", (long)m.PublishSeq); Num(sb, "tableFrame", m.TableFrame);
        Num(sb, "qpc", m.Qpc); sb.Append("  \"scale\": ").Append(F(m.Scale)).Append(",\n");
        Num(sb, "widthPx", m.WidthPx); Num(sb, "heightPx", m.HeightPx);
        sb.Append("  \"captured\": ").Append(m.Captured ? "true" : "false").Append(",\n");
        sb.Append("  \"viewports\": [");
        for (int i = 0; i < vps.Count; i++)
        {
            var v = vps[i];
            sb.Append(i == 0 ? "\n" : ",\n").Append("    { \"node\": ").Append(I(v.NodeIndex)).Append(", \"gen\": ").Append(I(v.Gen))
              .Append(", \"key\": ").Append(Quote(v.ScrollKey ?? "")).Append(", \"offset\": ").Append(v.Offset.ToString("0.###", Inv))
              .Append(", \"extent\": ").Append(v.Extent.ToString("0.###", Inv)).Append(", \"viewport\": ").Append(v.Viewport.ToString("0.###", Inv))
              .Append(", \"horizontal\": ").Append(v.Horizontal ? "true" : "false").Append(" }");
        }
        sb.Append(vps.Count > 0 ? "\n  ]\n" : "]\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    // ── the Store shot (Screens/StoreShot.cs) ──────────────────────────────────────────────────────────────────────

    /// <summary>A key is exported when no prefix is given or it starts with one of them (ordinal, case-insensitive).</summary>
    public static bool ShotKeyWanted(string key, IReadOnlyList<string>? prefixes)
    {
        if (prefixes is null || prefixes.Count == 0) return true;
        foreach (string p in prefixes) if (p.Length > 0 && key.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The rows a shot exports: wanted by <paramref name="prefixes"/>, non-empty, and at least partly inside the
    /// window (<paramref name="windowW"/> × <paramref name="windowH"/> DIP) — a row scrolled or parked off-window names no
    /// pixel in the PNG. Clipped to the window, ordered by key then top-left, so two runs over the same page diff cleanly.</summary>
    public static List<ShotKeyRow> ShotKeys(IEnumerable<ShotKeyRow> shown, float windowW, float windowH, IReadOnlyList<string>? prefixes)
    {
        var rows = new List<ShotKeyRow>();
        foreach (var r in shown)
        {
            if (!ShotKeyWanted(r.Key, prefixes) || r.W <= 0f || r.H <= 0f) continue;
            float x0 = MathF.Max(0f, r.X), y0 = MathF.Max(0f, r.Y);
            float x1 = MathF.Min(windowW, r.X + r.W), y1 = MathF.Min(windowH, r.Y + r.H);
            if (x1 <= x0 || y1 <= y0) continue;
            rows.Add(r with { X = x0, Y = y0, W = x1 - x0, H = y1 - y0 });
        }
        rows.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.Key, b.Key);
            if (c != 0) return c;
            c = a.Y.CompareTo(b.Y);
            return c != 0 ? c : a.X.CompareTo(b.X);
        });
        return rows;
    }

    /// <summary>shot.json: the facts, then every exported key with its rect in DIP (multiply by <c>scale</c> for the
    /// PNG's pixels).</summary>
    public static string ShotJson(in ShotMeta m, IReadOnlyList<ShotKeyRow> keys)
    {
        var sb = new StringBuilder(512 + keys.Count * 96);
        sb.Append("{\n");
        Str(sb, "tag", m.Tag); Str(sb, "created", m.CreatedLocal); Str(sb, "route", m.Route); Str(sb, "arg", m.Arg);
        sb.Append("  \"scale\": ").Append(F(m.Scale)).Append(",\n");
        sb.Append("  \"zoom\": ").Append(F(m.Zoom)).Append(",\n");
        Num(sb, "widthPx", m.WidthPx); Num(sb, "heightPx", m.HeightPx); Num(sb, "positionMs", m.PositionMs);
        sb.Append("  \"alpha\": ").Append(m.Alpha ? "true" : "false").Append(",\n");
        sb.Append("  \"presenting\": ").Append(m.Presenting ? "true" : "false").Append(",\n");
        sb.Append("  \"keys\": [");
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            sb.Append(i == 0 ? "\n" : ",\n").Append("    { \"key\": ").Append(Quote(k.Key)).Append(", \"type\": ").Append(Quote(k.Type))
              .Append(", \"x\": ").Append(F(k.X)).Append(", \"y\": ").Append(F(k.Y))
              .Append(", \"w\": ").Append(F(k.W)).Append(", \"h\": ").Append(F(k.H)).Append(" }");
        }
        sb.Append(keys.Count > 0 ? "\n  ]\n" : "]\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>census.json: the frame's tile census, the device counters and the process-lifetime stale tally.</summary>
    public static string CensusJson(TileCensus c, GpuFrameCounters d, long staleTurnsSinceLaunch)
    {
        var sb = new StringBuilder(1024);
        sb.Append("{\n  \"tiles\": {\n");
        (string, long)[] tiles =
        [
            ("turn", c.Turn), ("slices", c.Slices), ("liveTiles", c.LiveTiles), ("residentTiles", c.ResidentTiles),
            ("residentBytes", c.ResidentBytes), ("budgetBytes", c.BudgetBytes), ("scheduled", c.Scheduled), ("rastered", c.Rastered),
            ("evicted", c.Evicted), ("degradedSlices", c.DegradedSlices), ("exposedMissing", c.ExposedMissing),
            ("staleTiles", c.StaleTiles), ("coverageClamps", c.CoverageClamps), ("items", c.Items), ("noTexture", c.NoTexture),
            ("content", c.Content), ("primCount", c.PrimCount), ("validRectChanged", c.ValidRectChanged),
            ("scaleChanged", c.ScaleChanged), ("sliceGeometry", c.SliceGeometry), ("backgroundOrTheme", c.BackgroundOrTheme),
            ("evictedReason", c.EvictedReason), ("degraded", c.Degraded), ("effectSlices", c.EffectSlices), ("folded", c.Folded),
            ("acrylicSlices", c.AcrylicSlices), ("acrylicFallbacks", c.AcrylicFallbacks), ("freeFades", c.FreeFades),
            ("visibleNeedBytes", c.VisibleNeedBytes), ("groupSurfaces", c.GroupSurfaces), ("groupCacheHits", c.GroupCacheHits),
            ("retainedBytes", c.RetainedBytes),
        ];
        for (int i = 0; i < tiles.Length; i++)
            sb.Append("    ").Append(Quote(tiles[i].Item1)).Append(": ").Append(I(tiles[i].Item2)).Append(i + 1 < tiles.Length ? ",\n" : "\n");
        sb.Append("  },\n  \"device\": {\n");
        (string, long)[] dev =
        [
            ("sequence", (long)d.Sequence), ("featherItems", d.FeatherItems), ("offscreenPx", d.OffscreenPx),
            ("offscreenSurfaces", d.OffscreenSurfaces), ("passBreaks", d.PassBreaks), ("draws", d.Draws),
            ("glyphInstances", d.GlyphInstances), ("uploadBytes", d.UploadBytes), ("imageUploads", d.ImageUploads),
            ("groupSurfaces", d.GroupSurfaces), ("groupCacheHits", d.GroupCacheHits), ("retainedBytes", d.RetainedBytes),
            ("scratchRefused", d.ScratchRefused),
        ];
        for (int i = 0; i < dev.Length; i++)
            sb.Append("    ").Append(Quote(dev[i].Item1)).Append(": ").Append(I(dev[i].Item2)).Append(",\n");
        sb.Append("    \"route\": ").Append(Quote(d.Route.ToString())).Append('\n');
        sb.Append("  },\n  \"staleTurnsSinceLaunch\": ").Append(I(staleTurnsSinceLaunch)).Append("\n}\n");
        return sb.ToString();
    }

    /// <summary>The last <paramref name="max"/> lines of a log (the bundle's log-tail.txt).</summary>
    public static string Tail(IReadOnlyList<string> lines, int max)
    {
        int from = Math.Max(0, lines.Count - max);
        var sb = new StringBuilder();
        for (int i = from; i < lines.Count; i++) sb.Append(lines[i]).Append('\n');
        return sb.ToString();
    }

    static void Str(StringBuilder sb, string k, string v) => sb.Append("  ").Append(Quote(k)).Append(": ").Append(Quote(v)).Append(",\n");
    static void Num(StringBuilder sb, string k, long v) => sb.Append("  ").Append(Quote(k)).Append(": ").Append(I(v)).Append(",\n");

    /// <summary>A JSON string literal.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
