// ── Screens/Diagnostics.Inspect.Perf.cs ────────────────────────────────────────────────────────────────────────────────
// `perf`: a frame-cost capture around any verbs. `perf on` starts it, every frame any later verb runs (its own frames and its settle)
// is recorded under that verb's label, `perf report` judges them against the frame budget and names why a frame went over.
//
// Role: SHELL (UI thread; reads the engine's FrameStats, its opt-in relayout scope log and the scene keys only)
// Spec: docs/guide/agent-inspect.md "perf"; the skill's verify/debug-home.md is the recipe.
//
// WHAT IS MEASURED. The inspect host runs on a fixed 16 ms frame clock, but every millisecond here is real wall time the frame took on
// the UI thread (Stopwatch phases the engine stamps: flush, layout, anim, record, post-submit) plus submit, which under the inspector
// is the command build and the offscreen present: on gpu=warp that is WARP's CPU rasteriser, so it is reported apart and labelled, never
// judged as a GPU number. JIT time (FrameStats.JitMs) is reported and subtracted: a Debug JIT build pays it the first time a path runs,
// the shipped NativeAOT build never does. What remains is a Debug-build CPU cost: relative, not the shipped number. Judge real GPU
// cost on hardware (gpu=hardware) only through C:/WAVEE/scratch/gpulock.sh, one protected session at a time.
//
// THE VERDICT. A frame is OVER when its judged cost (ui - jit; judge=total adds submit) exceeds the budget (budget=ms; default the
// display the mount's frame clock stands for: dt=16 is a 60 Hz panel, 16.67 ms; dt=8.333 a 120 Hz one. The headless host paces at no
// real display, so its own refresh interval means nothing here). A steady animation frame (something animating; no component rendered, no reactive unit, no bind
// write, no measure) that allocated on the engine's hot phases (FrameStats.HotPhaseAllocBytes) is an ALLOC finding: rule 7 of the skill.
// `perf report` replies `gate:{over, alloc, failed}`; tools/inspect.py --gate-perf exits 1 on a failed gate.

using System.Text;
using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Layout;
using FluentGpu.Scene;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        PerfCapture? _perf;
        /// <summary>The mount's frame step (dt=): the display the inspector stands in for, so the default frame budget.</summary>
        float _dtMs = 16f;
        int _perfSeq;

        /// <summary>The label of the verb now running: every frame recorded while it runs carries it.</summary>
        void PerfBegin(in Inspect.Command cmd)
        {
            if (_perf is not { } p) return;
            if (cmd.Verb == Inspect.Verb.Perf) return;
            var sb = new StringBuilder(cmd.Name);
            foreach (string n in cmd.Args.Names)
            {
                string v = cmd.Args.Str(n);
                sb.Append(' ').Append(n).Append('=').Append(v.Length > 40 ? v[..40] + "…" : v);
            }
            p.Label = "#" + (++_perfSeq) + " " + sb;
            if (_shell is { } shell) p.Attach(shell.Host);   // a mount replaced the host: follow it
        }

        void PerfEnd()
        {
            if (_perf is { } p && _shell is { } shell) p.Attach(shell.Host);
        }

        /// <summary>`perf do=on|off|report|status`.</summary>
        string Perf(int id, string name, Inspect.Args a)
        {
            string act = a.Str("do", "report");
            switch (act)
            {
                case "on":
                    {
                        if (_shell is not { } shell) return Inspect.Reply.Fail(id, name, "nothing is mounted (mount first; perf follows later mounts)");
                        _perf?.Detach();
                        // The default budget is the display the mount's clock stands for: dt=16 is a 60 Hz panel (16.67 ms), dt=8.333 a
                        // 120 Hz one. Not the host's own paced interval: the headless host paces at none (it reads 10 ms there).
                        float display = _dtMs >= 15.9f && _dtMs <= 16.8f ? 1000f / 60f : _dtMs;
                        _perf = new PerfCapture(a.Float("budget", display),
                            a.Has("budget") ? "perf on budget=" : "the mount's frame clock (dt=" + _dtMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " ms: a " + Math.Round(1000f / display) + " Hz display)",
                            a.Str("judge", "ui") == "total", a.Bool("census", false));
                        _perf.Attach(shell.Host);
                        _perfSeq = 0;
                        _perf.Label = "#0 (after perf on)";
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteBoolean("recording", true);
                            w.WriteString("devices", _devices);
                            w.WriteBoolean("census", _perf.Census);
                        });
                    }
                case "off":
                    {
                        _perf?.Detach();
                        _perf = null;
                        return Inspect.Reply.Ok(id, name, w => w.WriteBoolean("recording", false));
                    }
                case "status":
                    return Inspect.Reply.Ok(id, name, w =>
                    {
                        w.WriteBoolean("recording", _perf is not null);
                        w.WriteNumber("frames", _perf?.Frames.Count ?? 0);
                    });
                default:
                    {
                        if (_perf is not { } p) return Inspect.Reply.Fail(id, name, "perf is not recording (perf on first, then the verbs, then perf report)");
                        string rows = a.Str("rows", "over");
                        int items = Math.Clamp(a.Int("items", 20), 1, 5000);
                        bool reset = a.Bool("reset", false);
                        string path = a.Str("path");
                        PerfCapture.Report report = p.Judge(a.Has("budget") ? a.Float("budget", 0f) : (float?)null);
                        string body = "";
                        if (path.Length > 0)
                        {
                            string full = Path.GetFullPath(path);
                            if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                            using (var fs = File.Create(full))
                            using (var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false }))
                            {
                                jw.WriteStartObject();
                                p.Write(jw, report, "all", int.MaxValue, _devices);
                                jw.WriteEndObject();
                            }
                            body = full;
                        }
                        string reply = Inspect.Reply.Ok(id, name, w =>
                        {
                            p.Write(w, report, rows, items, _devices);
                            if (body.Length > 0) w.WriteString("path", body);
                        });
                        if (reset) p.Frames.Clear();
                        return reply;
                    }
            }
        }

        /// <summary>The recorder: one row per frame the host completes while it is attached.</summary>
        sealed class PerfCapture
        {
            public readonly List<Row> Frames = [];
            public string Label = "";
            public readonly bool Census;
            readonly float _budget;
            readonly string _budgetFrom;
            readonly bool _judgeTotal;
            readonly Action<FrameStats> _onFrame;
            readonly List<RelayoutScope> _scopes = [];
            AppHost? _host;
            bool _censusBefore;

            public PerfCapture(float budget, string budgetFrom, bool judgeTotal, bool census)
            {
                _budget = budget;
                _budgetFrom = budgetFrom;
                _judgeTotal = judgeTotal;
                Census = census;
                _onFrame = OnFrame;
            }

            public void Attach(AppHost host)
            {
                if (ReferenceEquals(_host, host)) { if (Census) host.RenderCensus = true; return; }
                Detach();
                _host = host;
                host.FrameCompleted += _onFrame;
                host.RelayoutScopeLog = _scopes;
                _scopes.Clear();
                _censusBefore = host.RenderCensus;
                if (Census) host.RenderCensus = true;
            }

            public void Detach()
            {
                if (_host is not { } host) return;
                host.FrameCompleted -= _onFrame;
                if (ReferenceEquals(host.RelayoutScopeLog, _scopes)) host.RelayoutScopeLog = null;
                if (Census) host.RenderCensus = _censusBefore;
                _host = null;
            }

            public sealed class Row
            {
                public int Index;
                public string Label = "";
                public double Flush, Layout, Anim, Record, Submit, Post, Jit, Gpu, LongestUnit, SlowestEffect;
                public int Measures, ScopedMarks, Escapes, Components, ReactiveUnits, BindingWrites, TextShapes, Gc0, Gc1, Gc2;
                public long HotAlloc;
                public bool Animating, Rendered, Presented;
                public string? Census, EffectOwner;
                public List<(string Key, int Measures, string Kind)> Scopes = [];
                public double Ui => Flush + Layout + Anim + Record + Post;
                public double Judged(bool total) => Math.Max(0, Ui - Jit) + (total ? Submit : 0);
                /// <summary>A steady animation frame: something animates and nothing else happened (no component rendered, no reactive unit
                /// ran, no bound channel wrote, no measure). A text bind firing on a data change may format a string; a steady frame may not.</summary>
                public bool Steady => Animating && Components == 0 && ReactiveUnits == 0 && BindingWrites == 0 && Measures == 0;
            }

            void OnFrame(FrameStats s)
            {
                if (_host is not { } host) return;
                var row = new Row
                {
                    Index = Frames.Count, Label = Label,
                    Flush = s.FlushMs, Layout = s.LayoutMs, Anim = s.AnimMs, Record = s.RecordMs, Submit = s.SubmitMs, Post = s.PostSubmitMs,
                    Jit = s.JitMs, Gpu = s.GpuRenderMs, LongestUnit = s.ReactiveLongestUnitMs, SlowestEffect = s.SlowestEffectMs,
                    Measures = s.MeasureCount, ScopedMarks = s.ScopedRelayoutMarks, Escapes = s.RootRelayoutEscapes, Components = s.ComponentsRendered,
                    ReactiveUnits = s.ReactiveUnits, BindingWrites = s.BindingWrites, TextShapes = s.TextShapes, Gc0 = s.Gc0Delta, Gc1 = s.Gc1Delta, Gc2 = s.Gc2Delta,
                    HotAlloc = s.HotPhaseAllocBytes, Animating = host.Animation.HasActive, Rendered = s.Rendered, Presented = s.Presented,
                    Census = s.Census, EffectOwner = s.SlowestEffectOwnerType?.Name,
                };
                if (_scopes.Count > 0)
                {
                    _scopes.Sort(static (x, y) => y.Measures.CompareTo(x.Measures));
                    for (int i = 0; i < _scopes.Count && i < 4; i++)
                        row.Scopes.Add((KeyAbove(host.Scene, _scopes[i].Root), _scopes[i].Measures, KindName(_scopes[i].Kind)));
                    _scopes.Clear();
                }
                Frames.Add(row);
            }

            static string KindName(RelayoutScopeKind k) => k switch
            {
                RelayoutScopeKind.Scoped => "scoped",
                RelayoutScopeKind.Window => "window",
                RelayoutScopeKind.Full => "full",
                RelayoutScopeKind.EscapeResolved => "escape-resolved",
                _ => "escape-probe",
            };

            /// <summary>The node's own key, else its nearest keyed ancestor's as "key>" (the scope sits under it).</summary>
            static string KeyAbove(SceneStore scene, NodeHandle n)
            {
                if (n.IsNull || !scene.IsLive(n)) return "(gone)";
                if (n == scene.Root) return "(window)";
                int up = 0;
                for (var p = n; !p.IsNull; p = scene.Parent(p), up++)
                    if (scene.DebugKeyOf(p) is { Length: > 0 } k) return up == 0 ? k : k + " >" + up;
                return "(unkeyed #" + n.Raw.Index + ")";
            }

            public readonly record struct Over(Row Row, double Cost, string Top, double TopMs, string Why);

            public sealed class Report
            {
                public double Budget;
                public string BudgetFrom = "";
                public List<Over> Over = [];
                public List<Row> Alloc = [];
                public List<Row> AllocOther = [];
            }

            public Report Judge(float? budgetArg)
            {
                var r = new Report();
                if (budgetArg is > 0f) { r.Budget = budgetArg.Value; r.BudgetFrom = "report budget="; }
                else { r.Budget = _budget; r.BudgetFrom = _budgetFrom; }
                foreach (Row f in Frames)
                {
                    double cost = f.Judged(_judgeTotal);
                    if (cost > r.Budget)
                    {
                        (string top, double topMs) = Top(f);
                        r.Over.Add(new Over(f, cost, top, topMs, Why(f, top)));
                    }
                    if (f.HotAlloc > 0) (f.Steady ? r.Alloc : r.AllocOther).Add(f);
                }
                return r;
            }

            (string, double) Top(Row f)
            {
                (string, double) best = ("flush", f.Flush);
                if (f.Layout > best.Item2) best = ("layout", f.Layout);
                if (f.Anim > best.Item2) best = ("anim", f.Anim);
                if (f.Record > best.Item2) best = ("record", f.Record);
                if (f.Post > best.Item2) best = ("postSubmit", f.Post);
                if (_judgeTotal && f.Submit > best.Item2) best = ("submit", f.Submit);
                return best;
            }

            static string Why(Row f, string top)
            {
                var sb = new StringBuilder();
                switch (top)
                {
                    case "layout":
                        sb.Append(f.Measures).Append(" measures");
                        if (f.Scopes.Count > 0)
                        {
                            sb.Append(" in ");
                            for (int i = 0; i < f.Scopes.Count; i++)
                            {
                                if (i > 0) sb.Append(", ");
                                sb.Append(f.Scopes[i].Key).Append(" (").Append(f.Scopes[i].Kind).Append(' ').Append(f.Scopes[i].Measures).Append(')');
                            }
                        }
                        break;
                    case "flush":
                        sb.Append(f.Components).Append(" components rendered, ").Append(f.ReactiveUnits).Append(" reactive units, the longest ")
                          .Append(Math.Round(f.LongestUnit, 2)).Append(" ms");
                        sb.Append(f.Census is { Length: > 0 } c ? "; census: " + c : "; census=true on perf on names the components");
                        break;
                    case "postSubmit":
                        sb.Append("passive effects: the slowest ").Append(Math.Round(f.SlowestEffect, 2)).Append(" ms");
                        if (f.EffectOwner is { } o) sb.Append(" in ").Append(o);
                        break;
                    case "record":
                        sb.Append(f.TextShapes).Append(" text shapes; record walks the drawn scene (a big repaint or new text)");
                        break;
                    case "anim":
                        sb.Append("animation ticks and projections (phase 7), the video pump included");
                        break;
                    default:
                        sb.Append("submit: command build and the offscreen present");
                        break;
                }
                if (f.Jit > 0.5) sb.Append("; jit ").Append(Math.Round(f.Jit, 2)).Append(" ms subtracted");
                if (f.Gc0 + f.Gc1 + f.Gc2 > 0) sb.Append("; gc ").Append(f.Gc0).Append('/').Append(f.Gc1).Append('/').Append(f.Gc2);
                return sb.ToString();
            }

            public void Write(Utf8JsonWriter w, Report r, string rows, int items, string devices)
            {
                bool warp = devices.Contains("WARP", StringComparison.OrdinalIgnoreCase) || devices.Contains("warp", StringComparison.Ordinal);
                w.WriteNumber("frames", Frames.Count);
                Ms(w, "budgetMs", r.Budget);
                w.WriteString("budgetFrom", r.BudgetFrom);
                w.WriteString("judged", _judgeTotal ? "ui - jit + submit" : "ui - jit (ui = flush + layout + anim + record + postSubmit)");
                w.WriteString("devices", devices);
                w.WriteString("note", "CPU wall time of a Debug build on the inspector's fixed 16 ms clock: relative, not the shipped number. "
                    + (warp ? "submit and gpu are WARP (CPU-emulated), never a GPU number. " : "")
                    + "Real GPU numbers: gpu=hardware via C:/WAVEE/scratch/gpulock.sh only.");
                w.WriteStartObject("gate");
                w.WriteNumber("over", r.Over.Count);
                w.WriteNumber("alloc", r.Alloc.Count);
                w.WriteBoolean("failed", r.Over.Count > 0 || r.Alloc.Count > 0);
                w.WriteEndObject();

                // per verb: frames, the worst judged frame, p95, presents, jit and hot alloc
                w.WriteStartArray("verbs");
                var order = new List<string>();
                var groups = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
                foreach (Row f in Frames)
                {
                    if (!groups.TryGetValue(f.Label, out var g)) { groups[f.Label] = g = []; order.Add(f.Label); }
                    g.Add(f);
                }
                foreach (string label in order)
                {
                    List<Row> g = groups[label];
                    var costs = new List<double>(g.Count);
                    double maxGpu = 0, jit = 0, submitMax = 0;
                    long alloc = 0;
                    int presents = 0, over = 0, measures = 0, animating = 0;
                    Row? worst = null;
                    foreach (Row f in g)
                    {
                        double c = f.Judged(_judgeTotal);
                        costs.Add(c);
                        if (worst is null || c > worst.Judged(_judgeTotal)) worst = f;
                        if (c > r.Budget) over++;
                        maxGpu = Math.Max(maxGpu, f.Gpu); submitMax = Math.Max(submitMax, f.Submit);
                        jit += f.Jit; alloc += f.HotAlloc; measures += f.Measures;
                        if (f.Presented) presents++;
                        if (f.Animating) animating++;
                    }
                    costs.Sort();
                    w.WriteStartObject();
                    w.WriteString("verb", label);
                    w.WriteNumber("frames", g.Count);
                    w.WriteNumber("animating", animating);
                    w.WriteNumber("presents", presents);
                    w.WriteNumber("over", over);
                    Ms(w, "maxMs", costs[^1]);
                    Ms(w, "p95Ms", costs[Math.Min(costs.Count - 1, (int)Math.Ceiling(costs.Count * 0.95) - 1)]);
                    Ms(w, "medianMs", costs[costs.Count / 2]);
                    if (worst is not null) w.WriteNumber("maxFrame", worst.Index);
                    Ms(w, "jitMs", jit);
                    Ms(w, "submitMaxMs", submitMax);
                    Ms(w, warp ? "gpuMaxMsWarp" : "gpuMaxMs", maxGpu);
                    w.WriteNumber("measures", measures);
                    w.WriteNumber("hotAllocBytes", alloc);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("over");
                int n = 0;
                foreach (Over o in r.Over)
                {
                    if (n++ >= items) break;
                    w.WriteStartObject();
                    w.WriteNumber("frame", o.Row.Index);
                    w.WriteString("verb", o.Row.Label);
                    Ms(w, "ms", o.Cost);
                    w.WriteString("top", o.Top);
                    Ms(w, "topMs", o.TopMs);
                    w.WriteString("why", o.Why);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("alloc");
                n = 0;
                foreach (Row f in r.Alloc)
                {
                    if (n++ >= items) break;
                    w.WriteStartObject();
                    w.WriteNumber("frame", f.Index);
                    w.WriteString("verb", f.Label);
                    w.WriteNumber("bytes", f.HotAlloc);
                    w.WriteString("why", "a steady animation frame (animating; no component, reactive unit, bind write or measure) allocated on the hot phases 6-13: wire binds once at mount, nothing per frame may new, box or build a closure");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("allocOtherFrames", r.AllocOther.Count);

                if (rows is "all" or "over")
                {
                    w.WriteStartArray("rows");
                    n = 0;
                    foreach (Row f in Frames)
                    {
                        bool isOver = f.Judged(_judgeTotal) > r.Budget || (f.HotAlloc > 0 && f.Steady);
                        if (rows == "over" && !isOver) continue;
                        if (n++ >= items && rows == "over") break;
                        WriteRow(w, f);
                    }
                    w.WriteEndArray();
                }
            }

            static void WriteRow(Utf8JsonWriter w, Row f)
            {
                w.WriteStartObject();
                w.WriteNumber("f", f.Index);
                w.WriteString("verb", f.Label);
                w.WriteStartObject("ms");
                Ms(w, "ui", f.Ui); Ms(w, "jit", f.Jit); Ms(w, "flush", f.Flush); Ms(w, "layout", f.Layout); Ms(w, "anim", f.Anim);
                Ms(w, "record", f.Record); Ms(w, "postSubmit", f.Post); Ms(w, "submit", f.Submit); Ms(w, "gpu", f.Gpu);
                w.WriteEndObject();
                w.WriteNumber("measures", f.Measures);
                w.WriteNumber("scopedMarks", f.ScopedMarks);
                w.WriteNumber("escapes", f.Escapes);
                w.WriteNumber("components", f.Components);
                w.WriteNumber("hotAlloc", f.HotAlloc);
                w.WriteBoolean("animating", f.Animating);
                w.WriteBoolean("presented", f.Presented);
                if (f.Scopes.Count > 0)
                {
                    w.WriteStartArray("scopes");
                    foreach (var (key, m, kind) in f.Scopes)
                    {
                        w.WriteStartObject();
                        w.WriteString("key", key); w.WriteNumber("measures", m); w.WriteString("kind", kind);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                if (f.Census is { Length: > 0 } c) w.WriteString("census", c);
                w.WriteEndObject();
            }

            static void Ms(Utf8JsonWriter w, string name, double v) => w.WriteNumber(name, double.IsFinite(v) ? Math.Round(v, 2) : 0d);
        }
    }
}
