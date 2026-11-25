// ── Screens/Diagnostics.Inspect.Mix.Styles.cs ────────────────────────────────────────────────────────────────────────
// the `mixstyles` verb (#48 Mix, full-support plan §2.3.1, §2.6): the style table (Entities/Mix.Styles.cs) on demand.
//
//   mixstyles preset=17 bars=4 [bpm=120] [volume= eq= filter= fx= looping=]
//       the resolved style ids and every lane's curve on both sides, in the Connect metadata spelling (MixCurves.WriteJson)
//   mixstyles check=<dir>
//       the official client's own tables (volume.json, eq.json, filterfx.json, fx.json, presets.json, looping.json,
//       limits.json, kept outside the repo) against the generators: per lane, style and bar count (and bpm for the
//       effects) the max |generator - table| over the 1025-point engine table, a structural compare of the segments, and
//       a total. The table side is evaluated in double precision; the generator side is MixStyles.Sample (what the
//       engine gets).
//
// Role: SHELL (diagnostics; reads files, needs no mount)

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`mixstyles {preset, bars, bpm, overrides} | {check}`.</summary>
        string MixStylesVerb(int id, string name, Inspect.Args a)
        {
            if (a.Has("check")) return MixStylesCheck(id, name, a.Str("check"), a.Float("tol", 1e-6f), a.Bool("rows", true));
            int preset = a.Int("preset", 1), bars = a.Int("bars", 0);
            float bpm = a.Float("bpm", 120f);
            var o = new MixStyleOverrides(
                a.Has("volume") ? (MixVolumeStyle)a.Int("volume", 0) : null,
                a.Has("eq") ? (MixEqStyle)a.Int("eq", 0) : null,
                a.Has("filter") ? (MixFilterStyle)a.Int("filter", 0) : null,
                a.Has("fx") ? (MixFxStyle)a.Int("fx", 0) : null,
                null,
                a.Has("looping") ? (MixLoopingStyle)a.Int("looping", 0) : null);
            var set = MixStyles.Resolve(preset, in o);
            var curves = StyleCurves(in set, bars);
            Span<MixLoopBlock> blocks = stackalloc MixLoopBlock[MixStyles.MaxLoopBlocks];
            int nb = MixStyles.LoopBlocks(set.Looping, blocks);
            var loop = blocks[..nb].ToArray();
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteNumber("preset", preset);
                w.WriteString("presetName", preset == 0 ? "No transition" : MixStyles.IsKnown(preset) ? ((MixPresetId)preset).ToString() : "unknown (renders as Fade)");
                w.WriteNumber("bars", bars);
                w.WriteNumber("unitBars", MixStyles.UnitBars(bars));
                w.WriteStartObject("styles");
                Style(w, "volume", set.Volume.ToString(), (int)set.Volume);
                Style(w, "eq", set.Eq.ToString(), (int)set.Eq);
                Style(w, "filter", set.Filter.ToString(), (int)set.Filter);
                Style(w, "fx", set.Fx.ToString(), (int)set.Fx);
                w.WriteNumber("jogwheel", set.Jogwheel);
                Style(w, "looping", set.Looping.ToString(), (int)set.Looping);
                w.WriteEndObject();
                w.WriteStartArray("curves");
                foreach (var c in curves)
                {
                    w.WriteStartObject();
                    if (c.Key.Length > 0) w.WriteString("key", c.Key);
                    else w.WriteString("key", "(not seen on the wire) " + c.Lane + " " + c.Side);
                    w.WriteString("lane", c.Lane);
                    w.WriteString("side", c.Side);
                    w.WriteNumber("segments", c.Segments);
                    w.WriteString("value", c.Json);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (set.Fx is not (MixFxStyle.None or MixFxStyle.Unknown))
                {
                    w.WriteStartObject("fx");
                    w.WriteString("lane", MixStyles.FxLane(set.Fx)?.ToString() ?? "");
                    float beats = MixStyles.FxBeats(set.Fx);
                    w.WriteNumber("beats", beats);
                    w.WriteNumber("bpm", bpm);
                    if (beats > 0 && bpm > 0) w.WriteNumber("echoMs", Math.Round(beats * 60_000.0 / bpm, 3));
                    w.WriteBoolean("cutAtEnd", MixStyles.FxCutsAtEnd(set.Fx));
                    w.WriteEndObject();
                }
                w.WriteStartArray("loop");
                foreach (var b in loop)
                {
                    w.WriteStartObject();
                    w.WriteString("type", b.Type.ToString());
                    w.WriteNumber("beats", b.Beats);
                    w.WriteNumber("start", b.Start);
                    w.WriteNumber("end", b.End);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });

            static void Style(Utf8JsonWriter w, string key, string text, int value) => w.WriteString(key, text + " (" + value.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>What `mixstyles` prints for a resolved style set at <paramref name="bars"/>: every lane's curve on both sides, in the
        /// Connect metadata spelling (its key when the lane has one, "" otherwise). `mix show=render` compares the engine's tables with it.</summary>
        static List<(string Key, string Lane, string Side, int Segments, string Json)> StyleCurves(in MixStyleSet set, int bars)
            => StyleCurves(new MixTiming(MixSource.Saved, MixWhyNot.None, 0, 0, 0, 0, 0, set, (byte)Math.Clamp(bars, 0, 255), 1f, 0f, MixPlanNotes.None));

        /// <summary>A join's curves as it plays them (<see cref="MixTiming.Segments"/>: a recipe's hand-drawn curve in place of its style's).</summary>
        static List<(string Key, string Lane, string Side, int Segments, string Json)> StyleCurves(in MixTiming t)
        {
            var curves = new List<(string Key, string Lane, string Side, int Segments, string Json)>();
            Span<CurveSegment> seg = stackalloc CurveSegment[MixStyles.MaxSegments];
            byte[] buf = new byte[160 * MixStyles.MaxSegments];
            foreach (bool incoming in new[] { false, true })
                foreach (MixCurveLane lane in Enum.GetValues<MixCurveLane>())
                {
                    int n = t.Segments(lane, incoming, seg);
                    if (n == 0) continue;
                    int len = MixCurves.WriteJson(seg[..n], buf);
                    string? suffix = MixStyles.ConnectKey(lane);
                    string side = incoming ? "in" : "out";
                    curves.Add((suffix is null ? "" : "audio.fade_" + side + "_" + suffix, lane.ToString(), side, n, System.Text.Encoding.UTF8.GetString(buf, 0, len)));
                }
            return curves;
        }

        // ── check: the generators against the client's tables ─────────────────────────────────────────────────────────

        /// <summary>One segment of a table curve, in double precision as the file holds it.</summary>
        sealed record RefSegment(double Start, double End, double[] X, double[] Y);

        /// <summary>Each table file, its style list and the lanes its entries may carry (the client's own field names).</summary>
        static readonly (string File, string List, string Ingredient, (string Field, MixCurveLane Lane, bool In)[] Lanes)[] s_mixTables =
        [
            ("volume.json", "volumeStyles", "volume", [("fadeOutCurves", MixCurveLane.Volume, false), ("fadeInCurves", MixCurveLane.Volume, true)]),
            ("eq.json", "eqStyles", "eq",
            [
                ("lowEqOutCurves", MixCurveLane.EqLow, false), ("midEqOutCurves", MixCurveLane.EqMid, false), ("highEqOutCurves", MixCurveLane.EqHigh, false),
                ("lowEqInCurves", MixCurveLane.EqLow, true), ("midEqInCurves", MixCurveLane.EqMid, true), ("highEqInCurves", MixCurveLane.EqHigh, true),
            ]),
            ("filterfx.json", "filterFxStyles", "filter",
            [
                ("filterCutoffOutCurves", MixCurveLane.FilterCutoff, false), ("filterResonanceOutCurves", MixCurveLane.FilterResonance, false),
                ("noiseColorOutCurves", MixCurveLane.NoiseColor, false), ("filterCutoffInCurves", MixCurveLane.FilterCutoff, true),
                ("filterResonanceInCurves", MixCurveLane.FilterResonance, true), ("noiseColorInCurves", MixCurveLane.NoiseColor, true),
            ]),
            ("fx.json", "fxStyles", "fx",
            [
                ("echoDryWetOutCurves", MixCurveLane.EchoDryWet, false), ("reverbDryWetOutCurves", MixCurveLane.ReverbDryWet, false),
                ("delayDryWetOutCurves", MixCurveLane.DelayDryWet, false), ("echoDryWetInCurves", MixCurveLane.EchoDryWet, true),
                ("reverbDryWetInCurves", MixCurveLane.ReverbDryWet, true), ("delayDryWetInCurves", MixCurveLane.DelayDryWet, true),
            ]),
        ];

        string MixStylesCheck(int id, string name, string dir, float tol, bool rows)
        {
            if (!Directory.Exists(dir)) return Inspect.Reply.Fail(id, name, "no folder '" + dir + "'");
            var rowLines = new List<string>();
            var exceptions = new List<string>();
            var byLane = new SortedDictionary<string, (int Curves, double Max)>(StringComparer.Ordinal);
            int curves = 0, structural = 0, generatedButAbsent = 0;
            double maxDev = 0;
            Span<CurveSegment> seg = stackalloc CurveSegment[MixStyles.MaxSegments];
            var table = new float[MixStyles.TableSize];
            foreach (var (file, list, ingredient, lanes) in s_mixTables)
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path)) { exceptions.Add(file + ": missing"); continue; }
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    // "4" (bars) in volume/eq/filterfx; "120@4" (bpm@bars) in fx.
                    string key = entry.Name;
                    int at = key.IndexOf('@');
                    int bars = int.Parse(at < 0 ? key : key[(at + 1)..], CultureInfo.InvariantCulture);
                    string where = at < 0 ? "bars=" + key : key[..at] + "bpm bars=" + key[(at + 1)..];
                    foreach (var style in entry.Value.GetProperty(list).EnumerateArray())
                    {
                        int styleId = IdOf(style, "style");
                        foreach (var (field, lane, incoming) in lanes)
                        {
                            int n = Generate(ingredient, styleId, lane, incoming, bars, seg);
                            string label = ingredient + "." + LaneWord(lane) + "." + (incoming ? "in" : "out") + " s" + styleId.ToString(CultureInfo.InvariantCulture) + " " + where;
                            if (!style.TryGetProperty(field, out var curveEl))
                            {
                                if (n > 0) { generatedButAbsent++; exceptions.Add(label + ": generated " + n + " segments, the table has no such lane"); }
                                continue;
                            }
                            curves++;
                            var reference = ParseCurve(curveEl, out double min, out double max);
                            string laneKey = ingredient + "." + LaneWord(lane) + "." + (incoming ? "in" : "out");
                            string? why = null;
                            double dev = 0;
                            if (min != 0 || max != 1) why = "range " + min.ToString("R", CultureInfo.InvariantCulture) + ".." + max.ToString("R", CultureInfo.InvariantCulture);
                            if (reference.Count == 0 || n == 0)
                            {
                                if (reference.Count != n) { why = "lane used by one side only (table " + reference.Count + ", generator " + n + " segments)"; dev = 1; }
                            }
                            else
                            {
                                MixCurves.Sample(seg[..n], table);
                                for (int i = 0; i < table.Length; i++)
                                    dev = Math.Max(dev, Math.Abs(table[i] - EvalReference(reference, i / (double)(table.Length - 1))));
                                why ??= SameShape(reference, seg[..n], tol);
                            }
                            if (why is not null) structural++;
                            maxDev = Math.Max(maxDev, dev);
                            byLane[laneKey] = byLane.TryGetValue(laneKey, out var s) ? (s.Curves + 1, Math.Max(s.Max, dev)) : (1, dev);
                            if (dev > tol || why is not null) exceptions.Add(label + ": dev " + Num(dev) + (why is null ? "" : ", " + why));
                            if (rows) rowLines.Add(label + " dev=" + Num(dev));
                        }
                    }
                }
            }
            string presets = CheckPresets(dir), looping = CheckLooping(dir), limits = CheckLimits(dir);
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("dir", dir);
                w.WriteNumber("tolerance", tol);
                w.WriteString("total", "max deviation " + Num(maxDev) + " over " + curves + " curves");
                w.WriteNumber("curves", curves);
                w.WriteNumber("maxDeviation", maxDev);
                w.WriteNumber("structuralMismatches", structural);
                w.WriteNumber("generatedButAbsent", generatedButAbsent);
                w.WriteString("presets", presets);
                w.WriteString("looping", looping);
                w.WriteString("limits", limits);
                w.WriteStartArray("byLane");
                foreach (var (k, v) in byLane) w.WriteStringValue(k + ": " + v.Curves + " curves, max " + Num(v.Max));
                w.WriteEndArray();
                w.WriteStartArray("exceptions");
                foreach (var e in exceptions) w.WriteStringValue(e);
                w.WriteEndArray();
                if (rows)
                {
                    w.WriteStartArray("rows");
                    foreach (var r in rowLines) w.WriteStringValue(r);
                    w.WriteEndArray();
                }
            });

            static string Num(double v) => v.ToString("0.###E+0", CultureInfo.InvariantCulture);
        }

        static int Generate(string ingredient, int styleId, MixCurveLane lane, bool incoming, int bars, Span<CurveSegment> into) => ingredient switch
        {
            "volume" => MixStyles.Volume((MixVolumeStyle)styleId, incoming, bars, into),
            "eq" => MixStyles.Eq((MixEqStyle)styleId, lane, incoming, bars, into),
            "filter" => MixStyles.Filter((MixFilterStyle)styleId, lane, incoming, bars, into),
            _ => MixStyles.Fx((MixFxStyle)styleId, lane, incoming, into),
        };

        static string LaneWord(MixCurveLane lane) => lane switch
        {
            MixCurveLane.Volume => "volume", MixCurveLane.EqLow => "low", MixCurveLane.EqMid => "mid", MixCurveLane.EqHigh => "high",
            MixCurveLane.FilterCutoff => "cutoff", MixCurveLane.FilterResonance => "resonance", MixCurveLane.NoiseColor => "noise",
            MixCurveLane.EchoDryWet => "echo", MixCurveLane.ReverbDryWet => "reverb", _ => "delay",
        };

        /// <summary>A `{1 id}` style message's id (protobuf JSON drops a 0).</summary>
        static int IdOf(JsonElement parent, string field) =>
            parent.TryGetProperty(field, out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("id", out var v) ? v.GetInt32() : 0;

        static double NumOf(JsonElement e, string field) => e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        static List<RefSegment> ParseCurve(JsonElement curves, out double min, out double max)
        {
            min = NumOf(curves, "minimum");
            max = NumOf(curves, "maximum");
            var list = new List<RefSegment>();
            if (!curves.TryGetProperty("curves", out var arr)) return list;
            foreach (var c in arr.EnumerateArray())
            {
                var pts = c.TryGetProperty("points", out var p) ? p : default;
                int count = pts.ValueKind == JsonValueKind.Array ? pts.GetArrayLength() : 0;
                var xs = new double[count];
                var ys = new double[count];
                int i = 0;
                if (count > 0)
                    foreach (var pt in pts.EnumerateArray()) { xs[i] = NumOf(pt, "x"); ys[i] = NumOf(pt, "y"); i++; }
                list.Add(new RefSegment(NumOf(c, "start"), NumOf(c, "end"), xs, ys));
            }
            return list;
        }

        /// <summary>The table curve's value at window position x, in double precision, with MixCurves.Evaluate's conventions
        /// (before the first segment its first value; at a shared bound the earlier segment; after the last its last value).
        /// A 2-point segment is a line between its points, 3 or 4 points a Bézier whose x is solved by bisection.</summary>
        static double EvalReference(List<RefSegment> curve, double x)
        {
            if (curve.Count == 0) return 0;
            if (x <= curve[0].Start) return curve[0].Y.Length > 0 ? curve[0].Y[0] : 0;
            for (int i = 0; i < curve.Count; i++)
            {
                var c = curve[i];
                if (x > c.End && i < curve.Count - 1) continue;
                double span = c.End - c.Start;
                double u = span <= 0 ? 1 : Math.Clamp((x - c.Start) / span, 0, 1);
                int n = c.X.Length;
                if (n == 0) return 0;
                if (n == 1) return c.Y[0];
                if (n == 2)
                {
                    double dx = c.X[1] - c.X[0];
                    double t2 = dx > 0 ? Math.Clamp((u - c.X[0]) / dx, 0, 1) : 1;
                    return c.Y[0] + (c.Y[1] - c.Y[0]) * t2;
                }
                double lo = 0, hi = 1, t = u;
                for (int k = 0; k < 64; k++)
                {
                    t = (lo + hi) * 0.5;
                    if (Bezier(c.X, t) < u) lo = t; else hi = t;
                }
                return Bezier(c.Y, t);
            }
            var last = curve[^1];
            return last.Y.Length > 0 ? last.Y[^1] : 0;
        }

        static double Bezier(double[] p, double t)
        {
            double m = 1 - t;
            return p.Length == 3
                ? m * m * p[0] + 2 * m * t * p[1] + t * t * p[2]
                : m * m * m * p[0] + 3 * m * m * t * p[1] + 3 * m * t * t * p[2] + t * t * t * p[3];
        }

        /// <summary>Null when the generated segments equal the table's (count, bounds, point count and points within tol).</summary>
        static string? SameShape(List<RefSegment> reference, ReadOnlySpan<CurveSegment> mine, double tol)
        {
            if (reference.Count != mine.Length) return "segments " + mine.Length + " vs table " + reference.Count;
            for (int i = 0; i < mine.Length; i++)
            {
                var r = reference[i];
                ref readonly var c = ref mine[i];
                if (Math.Abs(r.Start - c.Start) > tol || Math.Abs(r.End - c.End) > tol)
                    return "segment " + i + " spans " + c.Start.ToString("R", CultureInfo.InvariantCulture) + "-" + c.End.ToString("R", CultureInfo.InvariantCulture)
                        + " vs table " + r.Start.ToString("R", CultureInfo.InvariantCulture) + "-" + r.End.ToString("R", CultureInfo.InvariantCulture);
                int points = c.Cubic ? 4 : 2;
                if (r.X.Length != points) return "segment " + i + " has " + points + " points vs table " + r.X.Length;
                ReadOnlySpan<float> x = c.Cubic ? [c.X0, c.X1, c.X2, c.X3] : [c.X0, c.X3];
                ReadOnlySpan<float> y = c.Cubic ? [c.Y0, c.Y1, c.Y2, c.Y3] : [c.Y0, c.Y3];
                for (int k = 0; k < points; k++)
                    if (Math.Abs(r.X[k] - x[k]) > tol || Math.Abs(r.Y[k] - y[k]) > tol)
                        return "segment " + i + " point " + k + " (" + x[k].ToString("R", CultureInfo.InvariantCulture) + "," + y[k].ToString("R", CultureInfo.InvariantCulture)
                            + ") vs table (" + r.X[k].ToString("R", CultureInfo.InvariantCulture) + "," + r.Y[k].ToString("R", CultureInfo.InvariantCulture) + ")";
            }
            return null;
        }

        /// <summary>presets.json against MixStyles.Preset: every preset's six style ids.</summary>
        static string CheckPresets(string dir)
        {
            string path = Path.Combine(dir, "presets.json");
            if (!File.Exists(path)) return "presets.json missing";
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            int total = 0, equal = 0;
            var differ = new List<string>();
            foreach (var p in doc.RootElement.GetProperty("presetStyles").EnumerateArray())
            {
                int presetId = p.TryGetProperty("presetId", out var pid) ? pid.GetInt32() : 0;
                var mine = MixStyles.Preset(presetId);
                total++;
                bool same = IdOf(p, "volumeStyle") == (int)mine.Volume && IdOf(p, "eqStyle") == (int)mine.Eq && IdOf(p, "filterFxStyle") == (int)mine.Filter
                    && IdOf(p, "fxStyle") == (int)mine.Fx && IdOf(p, "jogwheelStyle") == mine.Jogwheel && IdOf(p, "loopingStyle") == (int)mine.Looping;
                if (same) equal++; else differ.Add(presetId.ToString(CultureInfo.InvariantCulture));
            }
            return equal + " of " + total + " equal" + (differ.Count > 0 ? "; differ: " + string.Join(",", differ) : "");
        }

        /// <summary>looping.json against MixStyles.LoopBlocks.</summary>
        static string CheckLooping(string dir)
        {
            string path = Path.Combine(dir, "looping.json");
            if (!File.Exists(path)) return "looping.json missing";
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            int total = 0, equal = 0;
            var differ = new List<string>();
            Span<MixLoopBlock> mine = stackalloc MixLoopBlock[MixStyles.MaxLoopBlocks];
            foreach (var s in doc.RootElement.GetProperty("loopingStyleBlocks").EnumerateArray())
            {
                int styleId = IdOf(s, "style");
                int n = MixStyles.LoopBlocks((MixLoopingStyle)styleId, mine);
                total++;
                var blocks = s.TryGetProperty("out", out var o) && o.TryGetProperty("blocks", out var b) ? b : default;
                int count = blocks.ValueKind == JsonValueKind.Array ? blocks.GetArrayLength() : 0;
                bool same = count == n;
                int i = 0;
                if (same && count > 0)
                    foreach (var blk in blocks.EnumerateArray())
                    {
                        ref readonly var m = ref mine[i++];
                        same &= (int)NumOf(blk, "type") == (int)m.Type && Math.Abs(NumOf(blk, "beats") - m.Beats) < 1e-9
                            && Math.Abs(NumOf(blk, "start") - m.Start) < 1e-9 && Math.Abs(NumOf(blk, "end") - m.End) < 1e-9;
                    }
                if (same) equal++; else differ.Add(styleId.ToString(CultureInfo.InvariantCulture));
            }
            return equal + " of " + total + " equal" + (differ.Count > 0 ? "; differ: " + string.Join(",", differ) : "");
        }

        /// <summary>limits.json against MixStyles' constants.</summary>
        static string CheckLimits(string dir)
        {
            string path = Path.Combine(dir, "limits.json");
            if (!File.Exists(path)) return "limits.json missing";
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            double maxMs = NumOf(doc.RootElement, "maxTransitionLengthMs"), optOut = NumOf(doc.RootElement, "beatmatchOptOutMs");
            bool same = maxMs == MixStyles.MaxTransitionMs && optOut == MixStyles.BeatmatchOptOutMs;
            return (same ? "equal" : "differ") + " (max transition " + maxMs.ToString(CultureInfo.InvariantCulture) + " ms, beatmatch opt-out "
                + optOut.ToString(CultureInfo.InvariantCulture) + " ms)";
        }
    }
}
