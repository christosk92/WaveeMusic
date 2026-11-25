// ── Screens/Diagnostics.Inspect.Mix.Auto.cs ──────────────────────────────────────────────────────────────────────────
// `mix autocheck=<dir>` (#48 Mix, full-support plan §2.3.2, M3's gate): Spotify's Auto against the official client's own
// answers. The folder stays outside the repo (it holds account data) and carries:
//
//   auto-pairs*.json   the client's Auto per adjacent pair: {playlist: {pairs: [{a, b, auto: {overlap {startAMs, startBMs,
//                      durationMs, durationBars, isBeatmatched, speedB, …}, preset {id, type}}}]}}
//   *inputs*.json      what the client read for those tracks: {uri: {"28": {start, end, startCands[], endCands[]} (cue =
//                      {ms, bpm, ml, conf}, ms absent when 0), "10": {duration_ms}, "dur_ms" (whole seconds), "222": {bpm,
//                      camelot}, "217": {beats: [[ms, position in bar]]}, "218": {rate, step, frames[]}, "6": {concepts[]}}};
//                      the first file (by name) to give a uri's kind wins
//
// Not beatmatched: each pair's inputs go through the real planner (MixPlanner.Plan, a pair with no saved recipe) and its
// answer is compared with the client's startAMs and durationMs: once with A's kind 10 duration (exact, what Wavee plays
// with) and once with A's duration taken as the client's own startAMs + durationMs. Beatmatched: every pair whose two
// tracks have a grid and a kind 10 duration goes through MixPlanner.AutoBeatmatched with the full inputs (which pairs it
// beatmatches, the window, speedB to the bit, the preset) and once more with only what the app holds live
// (BeatmatchFacts.Live). Pairs are named by file, playlist and pair index, never by uri.
//
// Role: SHELL (diagnostics; reads files, needs no mount)

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>One track's inputs, merged over the input files.</summary>
        sealed class AutoInput
        {
            public bool Has28;
            public TrackCues Cues;
            public long Kind10Ms = -1, RoundedMs = -1;
            public ushort TempoX10;
            public byte Camelot;
            public uint[]? Beats;
            public long BestStartMs = -1, BestEndMs = -1;
            public uint[] StartCands = [], EndCands = [];
            public byte[]? Vocal;
            public float VocalFps;
            public Playback.MixGenres Genres;
        }

        /// <summary>One pair of the dataset: where it is, its two tracks and the client's answer.</summary>
        readonly record struct AutoPair(string Where, string A, string B, uint StartAMs, uint StartBMs, uint DurationMs, int Bars,
            bool Beatmatched, float SpeedB, int Preset);

        /// <summary>The beatmatched fit over every pair with grids and kind 10 durations on both sides (`mix autocheck`).</summary>
        sealed class BeatmatchTally
        {
            public int Pairs, Decided, MatchedPairs, Window, Speed, Preset, LiveWindow, LiveDecided;
            public readonly List<string> Misses = [];
        }

        static void CheckBeatmatched(in AutoPair p, AutoInput a, AutoInput b, BeatmatchTally t)
        {
            if (a.Beats is null || b.Beats is null || a.Kind10Ms <= 0 || b.Kind10Ms <= 0) return;
            t.Pairs++;
            var fa = new Playback.PairFacts(a.Kind10Ms, a.TempoX10, a.Camelot, a.Cues, true, false, p.A);
            var fb = new Playback.PairFacts(b.Kind10Ms, b.TempoX10, b.Camelot, b.Cues, true, false, p.B);
            var ia = new Playback.BeatmatchFacts(a.Beats, a.BestStartMs, a.BestEndMs, a.StartCands, a.EndCands, a.Vocal, a.VocalFps, a.Genres);
            var ib = new Playback.BeatmatchFacts(b.Beats, b.BestStartMs, b.BestEndMs, b.StartCands, b.EndCands, b.Vocal, b.VocalFps, b.Genres);
            bool matched = Playback.MixPlanner.AutoBeatmatched(in fa, in ia, in fb, in ib, out var m);
            var la = Playback.BeatmatchFacts.Live(in fa, a.Beats);
            var lb = Playback.BeatmatchFacts.Live(in fb, b.Beats);
            bool liveMatched = Playback.MixPlanner.AutoBeatmatched(in fa, in la, in fb, in lb, out var lm);
            if (matched == p.Beatmatched) t.Decided++;
            else t.Misses.Add(p.Where + ": client " + (p.Beatmatched ? "beatmatches" : "does not beatmatch") + ", Wavee " + (matched ? "does" : "does not"));
            if (liveMatched == p.Beatmatched) t.LiveDecided++;
            if (!p.Beatmatched || !matched) return;
            t.MatchedPairs++;
            bool window = m.OutStartMs == p.StartAMs && m.OutDurationMs == p.DurationMs && m.InStartMs == p.StartBMs && m.Bars == p.Bars;
            bool speed = BitConverter.SingleToInt32Bits(m.SpeedB) == BitConverter.SingleToInt32Bits(p.SpeedB);
            if (window) t.Window++;
            if (window && speed) t.Speed++;
            if (window && m.Preset == p.Preset) t.Preset++;
            if (liveMatched && lm.OutStartMs == p.StartAMs && lm.OutDurationMs == p.DurationMs && lm.InStartMs == p.StartBMs && lm.Bars == p.Bars) t.LiveWindow++;
            if (!window || !speed || m.Preset != p.Preset)
                t.Misses.Add(p.Where + ": client start=" + p.StartAMs + " dur=" + p.DurationMs + " in=" + p.StartBMs + " bars=" + p.Bars
                    + " speedB=" + p.SpeedB.ToString("R", CultureInfo.InvariantCulture) + " preset=" + p.Preset
                    + ", Wavee start=" + m.OutStartMs + " dur=" + m.OutDurationMs + " in=" + m.InStartMs + " bars=" + m.Bars
                    + " speedB=" + m.SpeedB.ToString("R", CultureInfo.InvariantCulture) + " preset=" + m.Preset);
        }

        string MixAutoCheck(int id, string name, string dir)
        {
            if (!Directory.Exists(dir)) return Inspect.Reply.Fail(id, name, "no folder '" + dir + "'");
            var files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            var inputs = new Dictionary<string, AutoInput>(StringComparer.Ordinal);
            var pairs = new List<AutoPair>();
            var read = new List<string>();
            foreach (string path in files)
            {
                string file = Path.GetFileName(path);
                if (file.Contains("inputs", StringComparison.OrdinalIgnoreCase)) { read.Add(file + ": " + ReadAutoInputs(path, inputs) + " tracks"); continue; }
                if (file.StartsWith("auto-pairs", StringComparison.OrdinalIgnoreCase)) read.Add(file + ": " + ReadAutoPairs(path, file, pairs) + " pairs");
            }

            // Not beatmatched: the exact rule. Beatmatched: what Wavee plans now (the rule; the fit is MixPlanner.AutoBeatmatched).
            int nb = 0, nbInputs = 0, matchKind10 = 0, kind10Pairs = 0, matchClientEnd = 0, toEndKind10 = 0;
            int inWith = 0, inFallback = 0;
            long inMin = long.MaxValue, inMax = 0;
            var misses = new List<string>();
            int bm = 0, bmInputs = 0;
            var bmBars = new SortedDictionary<int, int>();
            var bmPresets = new SortedDictionary<int, int>();
            int bmInB = 0, bmStretched = 0, bmBeyond5000 = 0;
            var fit = new BeatmatchTally();
            foreach (var p in pairs)
            {
                if (inputs.TryGetValue(p.A, out var ba) && inputs.TryGetValue(p.B, out var bb)) CheckBeatmatched(in p, ba, bb, fit);
                if (!inputs.TryGetValue(p.A, out var a) || !a.Has28 || !inputs.TryGetValue(p.B, out var b))
                {
                    if (p.Beatmatched) bm++; else { nb++; misses.Add(p.Where + ": no kind 28 for A or no inputs for B in the folder"); }
                    continue;
                }
                long clientEnd = (long)p.StartAMs + p.DurationMs;
                long durB = b.Kind10Ms >= 0 ? b.Kind10Ms : b.RoundedMs >= 0 ? b.RoundedMs : 600_000;
                var fb = new Playback.PairFacts(durB, b.TempoX10, 0, b.Cues, true, false);
                if (!p.Beatmatched)
                {
                    nb++;
                    nbInputs++;
                    // A's duration as the client's own answer states it (startAMs + durationMs): dur_ms is rounded to seconds.
                    var fa = new Playback.PairFacts(clientEnd, a.TempoX10, 0, a.Cues, true, false);
                    var t = Playback.MixPlanner.Plan(in fa, default, in fb, default, false, default);
                    bool same = t.Mix && t.OutStartMs == p.StartAMs && t.OutDurationMs == p.DurationMs;
                    if (same) matchClientEnd++;
                    else misses.Add(p.Where + " (A = client's end " + clientEnd + "): client start=" + p.StartAMs + " dur=" + p.DurationMs
                        + ", Wavee " + (t.Mix ? "start=" + t.OutStartMs + " dur=" + t.OutDurationMs + " notes=" + t.Notes : "refused " + t.WhyNot));
                    if (a.Kind10Ms >= 0)
                    {
                        // A's exact kind 10 duration: what the app plans with.
                        kind10Pairs++;
                        var fa10 = new Playback.PairFacts(a.Kind10Ms, a.TempoX10, 0, a.Cues, true, false);
                        var t10 = Playback.MixPlanner.Plan(in fa10, default, in fb, default, false, default);
                        if (t10.Mix && t10.OutStartMs == p.StartAMs && t10.OutDurationMs == p.DurationMs) matchKind10++;
                        else misses.Add(p.Where + " (A = kind 10 " + a.Kind10Ms + "): client start=" + p.StartAMs + " dur=" + p.DurationMs
                            + ", Wavee " + (t10.Mix ? "start=" + t10.OutStartMs + " dur=" + t10.OutDurationMs + " notes=" + t10.Notes : "refused " + t10.WhyNot));
                        // The rule as first written down: the overlap runs to A's end (no 2500 ms after the candidate).
                        if (a.Cues.HasLastEndCandidate && a.Kind10Ms - ((long)a.Cues.LastEndCandidateMs - Playback.MixPlanner.CueReachMs) == p.DurationMs) toEndKind10++;
                    }
                    if (t.Mix)
                    {
                        if ((t.Notes & MixPlanNotes.InFallback) != 0) inFallback++; else inWith++;
                        inMin = Math.Min(inMin, t.InDurationMs);
                        inMax = Math.Max(inMax, t.InDurationMs);
                    }
                    continue;
                }
                bm++;
                bmInputs++;
                bmBars[p.Bars] = bmBars.GetValueOrDefault(p.Bars) + 1;
                bmPresets[p.Preset] = bmPresets.GetValueOrDefault(p.Preset) + 1;
                if (p.StartBMs > 0) bmInB++;
                if (p.SpeedB != 1f) bmStretched++;
                if (p.DurationMs > 5_000) bmBeyond5000++;
            }
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("dir", dir);
                w.WriteStartArray("files");
                foreach (var f in read) w.WriteStringValue(f);
                w.WriteEndArray();
                w.WriteStartObject("nonBeatmatched");
                w.WriteNumber("pairs", nb);
                w.WriteNumber("withInputs", nbInputs);
                w.WriteString("matchClientEnd", matchClientEnd + " of " + nbInputs + " (startAMs and durationMs equal; A's duration = the client's startAMs + durationMs)");
                w.WriteString("matchKind10", matchKind10 + " of " + kind10Pairs + " (A's duration = its kind 10 duration, as Wavee plans)");
                w.WriteString("ruleToAEnd", toEndKind10 + " of " + kind10Pairs + " (the overlap run to A's kind 10 end instead of 2500 ms past the candidate)");
                w.WriteString("bFadeIn", inWith + " with B's first start candidate + 2500, " + inFallback + " fallbacks (2510); "
                    + (inMin <= inMax ? inMin + ".." + inMax + " ms" : "-") + " (the client's B length is not in the dataset; 5 of 5 published seams follow the rule)");
                w.WriteStartArray("misses");
                foreach (var m in misses) w.WriteStringValue(m);
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteStartObject("beatmatched");
                w.WriteNumber("pairs", bm);
                w.WriteNumber("withInputs", bmInputs);
                w.WriteString("whichPairs", fit.Decided + " of " + fit.Pairs + " (pairs with grids and kind 10 on both sides: Wavee beatmatches exactly when the client does)");
                w.WriteString("window", fit.Window + " of " + fit.MatchedPairs + " (startAMs, durationMs, startBMs and bars equal)");
                w.WriteString("speedB", fit.Speed + " of " + fit.Window + " (bit-exact, on the equal windows)");
                w.WriteString("preset", fit.Preset + " of " + fit.Window + " (on the equal windows)");
                w.WriteString("live", "which pairs " + fit.LiveDecided + " of " + fit.Pairs + ", window " + fit.LiveWindow + " of " + fit.MatchedPairs
                    + " (BeatmatchFacts.Live: the grid, Camelot, the best cues and two candidates; no kind 218 vocals, no kind 6 genres)");
                w.WriteStartArray("fitMisses");
                foreach (var m in fit.Misses) w.WriteStringValue(m);
                w.WriteEndArray();
                w.WriteString("clientBars", string.Join(", ", bmBars.Select(kv => kv.Key + " bars: " + kv.Value)));
                w.WriteString("clientPresets", string.Join(", ", bmPresets.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + ": " + kv.Value)));
                w.WriteString("clientShape", bmInB + " enter B past 0 (startBMs > 0), " + bmStretched + " stretch B (speedB != 1), "
                    + bmBeyond5000 + " overlap longer than 5000 ms");
                w.WriteEndObject();
            });
        }

        /// <summary>One inputs file into <paramref name="into"/> (a uri's kind already given by an earlier file is kept). The track count.</summary>
        static int ReadAutoInputs(string path, Dictionary<string, AutoInput> into)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            int n = 0;
            foreach (var track in doc.RootElement.EnumerateObject())
            {
                if (track.Value.ValueKind != JsonValueKind.Object) continue;
                n++;
                if (!into.TryGetValue(track.Name, out var input)) into[track.Name] = input = new AutoInput();
                var v = track.Value;
                if (!input.Has28 && v.TryGetProperty("28", out var cues) && cues.ValueKind == JsonValueKind.Object)
                {
                    input.Has28 = true;
                    uint first = uint.MaxValue, last = 0;
                    var has = CueCandidates.None;
                    if (cues.TryGetProperty("startCands", out var sc) && sc.ValueKind == JsonValueKind.Array)
                        foreach (var c in sc.EnumerateArray()) { has |= CueCandidates.Start; first = Math.Min(first, CueMs(c)); }
                    if (cues.TryGetProperty("endCands", out var ec) && ec.ValueKind == JsonValueKind.Array)
                        foreach (var c in ec.EnumerateArray()) { has |= CueCandidates.End; last = Math.Max(last, CueMs(c)); }
                    uint start = cues.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Object ? CueMs(s) : 0;
                    uint end = cues.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Object ? CueMs(e) : 0;
                    input.Cues = new TrackCues(start, end, 0, has, has.HasFlag(CueCandidates.Start) ? first : 0, last);
                    input.BestStartMs = cues.TryGetProperty("start", out var bs) && bs.ValueKind == JsonValueKind.Object ? CueMs(bs) : -1;
                    input.BestEndMs = cues.TryGetProperty("end", out var be) && be.ValueKind == JsonValueKind.Object ? CueMs(be) : -1;
                    input.StartCands = sc.ValueKind == JsonValueKind.Array ? [.. sc.EnumerateArray().Select(CueMs)] : [];
                    input.EndCands = ec.ValueKind == JsonValueKind.Array ? [.. ec.EnumerateArray().Select(CueMs)] : [];
                }
                if (input.Beats is null && v.TryGetProperty("217", out var k217) && k217.ValueKind == JsonValueKind.Object
                    && k217.TryGetProperty("beats", out var bl) && bl.ValueKind == JsonValueKind.Array)
                {
                    // [ms, position in bar], ms = the wire's float seconds x 1000 to 0.001: back to that float, then to whole ms
                    // as the client does, the FLOAT product rounded (Spotify.Decode.MixBeats multiplies in double, which tips a
                    // beat on a half millisecond the other way; reference §7.2).
                    var beats = new List<uint>();
                    foreach (var beat in bl.EnumerateArray())
                    {
                        if (beat.ValueKind != JsonValueKind.Array || beat.GetArrayLength() < 2) continue;
                        double ms = beat[0].GetDouble();
                        bool down = beat[1].ValueKind == JsonValueKind.Number && beat[1].GetInt32() == 1;
                        float seconds = (float)(ms / 1000.0);
                        float product = seconds * 1000f;
                        beats.Add((uint)(long)Math.Floor(product + 0.5) | (down ? BeatGrid.DownbeatBit : 0u));
                    }
                    input.Beats = [.. beats];
                }
                if (input.Vocal is null && v.TryGetProperty("218", out var k218) && k218.ValueKind == JsonValueKind.Object
                    && k218.TryGetProperty("frames", out var fr) && fr.ValueKind == JsonValueKind.Array)
                {
                    double rate = k218.TryGetProperty("rate", out var ra) ? ra.GetDouble() : 0, step = k218.TryGetProperty("step", out var st) ? st.GetDouble() : 0;
                    input.Vocal = [.. fr.EnumerateArray().Select(static x => (byte)Math.Clamp(x.GetInt32(), 0, 255))];
                    input.VocalFps = step > 0 ? (float)(rate / step) : 0f;
                }
                if (input.Genres == Playback.MixGenres.Unknown && v.TryGetProperty("6", out var k6) && k6.ValueKind == JsonValueKind.Object
                    && k6.TryGetProperty("concepts", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    input.Genres = Playback.MixPlanner.GenresOf(cs.EnumerateArray().Select(static x => x.GetString() ?? ""));
                if (input.Kind10Ms < 0 && v.TryGetProperty("10", out var k10) && k10.ValueKind == JsonValueKind.Object
                    && k10.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number)
                    input.Kind10Ms = d.GetInt64();
                if (input.RoundedMs < 0 && v.TryGetProperty("dur_ms", out var r) && r.ValueKind == JsonValueKind.Number) input.RoundedMs = r.GetInt64();
                if (input.TempoX10 == 0 && v.TryGetProperty("222", out var k222) && k222.ValueKind == JsonValueKind.Object
                    && k222.TryGetProperty("bpm", out var bpm) && bpm.ValueKind == JsonValueKind.Number)
                {
                    input.TempoX10 = (ushort)Math.Clamp(bpm.GetDouble() * 10 + 0.5, 0, ushort.MaxValue);
                    if (k222.TryGetProperty("camelot", out var cam) && cam.ValueKind == JsonValueKind.String)
                        input.Camelot = Spotify.Decode.CamelotCode(System.Text.Encoding.ASCII.GetBytes(cam.GetString() ?? ""));
                }
            }
            return n;

            static uint CueMs(JsonElement cue) => cue.TryGetProperty("ms", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetUInt32() : 0;
        }

        /// <summary>One auto-pairs file into <paramref name="into"/>. The pair count.</summary>
        static int ReadAutoPairs(string path, string file, List<AutoPair> into)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            int n = 0, playlist = 0;
            foreach (var list in doc.RootElement.EnumerateObject())
            {
                if (list.Value.ValueKind != JsonValueKind.Object || !list.Value.TryGetProperty("pairs", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;
                int i = 0;
                foreach (var p in arr.EnumerateArray())
                {
                    string where = file + "#" + playlist + ":" + i++;
                    if (!p.TryGetProperty("auto", out var auto) || auto.ValueKind != JsonValueKind.Object || !auto.TryGetProperty("overlap", out var o)) continue;
                    int preset = auto.TryGetProperty("preset", out var pr) && pr.TryGetProperty("id", out var pid) ? pid.GetInt32() : 0;
                    into.Add(new AutoPair(where, Str(p, "a"), Str(p, "b"), (uint)Num(o, "startAMs"), (uint)Num(o, "startBMs"), (uint)Num(o, "durationMs"),
                        (int)Num(o, "durationBars"), o.TryGetProperty("isBeatmatched", out var bmv) && bmv.ValueKind == JsonValueKind.True,
                        (float)(o.TryGetProperty("speedB", out var sb) && sb.ValueKind == JsonValueKind.Number ? sb.GetDouble() : 1.0), preset));
                    n++;
                }
                playlist++;
            }
            return n;

            static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            static double Num(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        }
    }
}
