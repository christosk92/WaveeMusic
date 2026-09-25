// ── Screens/EvidenceBundle.cs — the evidence bundle writer and the wavee://diag commands (evidence-diagnostics §B) ───
//
// ONE export that ties a screenshot to the engine's own account of it: the captured present (frame.png) and, for THAT
// SAME present, the composite items, the tile placements with their raster ledger, the stale tiles, the raster and walk
// ledger tails, the scroll probe CSV, the census, the viewports, node names and the log tail — written under
// logs\evidence\<yyyyMMdd-HHmmss>-<tag>\. Triggered from Diagnostics ▸ Evidence or by `wavee://diag?cmd=bundle&tag=…`
// (WM_COPYDATA from the evidence harness, ops/tools/evidence). Replies are FILES (WM_COPYDATA is one-way): every command
// appends one line to logs\evidence\replies.tsv and a bundle also to logs\evidence\index.txt; the harness polls them.
//
// Threading: the capture is armed on the UI thread; the engine completes it on the turn it presents; the UI thread reads
// every ledger / name / census (they are UI-side reads or lock-copies) and a worker writes the files — the
// SaveLyricsBundle pattern. The formats are EvidenceReport (pure, tested).

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentGpu;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Runtime;

namespace Wavee;

public static partial class Diagnostics
{
    public static class Evidence
    {
        /// <summary>logs\evidence — bundles, adhoc pixel queries, vps.tsv, replies.tsv, index.txt.</summary>
        public static string Root => Path.Combine(Platform.LogFolder, "evidence");

        static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
        const int CaptureTimeoutMs = 3000;
        const int LogTailLines = 400;

        static string? s_lastFolder;
        static int s_busy;
        static string s_tag = "";
        static long s_deadline;
        static bool s_attached;
        static readonly Action<FrameStats> s_onFrame = OnFrame;
        static readonly List<(string Name, string Tsv)> s_pendingPixels = new();
        static readonly object s_replyLock = new();
        static int s_replySeq;

        /// <summary>The last bundle folder written (null before the first) — the card polls it.</summary>
        public static string? LastFolder => Volatile.Read(ref s_lastFolder);

        /// <summary>A bundle is being captured or written.</summary>
        public static bool Busy => Volatile.Read(ref s_busy) != 0;

        /// <summary>UI THREAD. The deep-link door's <c>DeepLinkKind.Diag</c> arm.</summary>
        public static void Apply(in DiagVerb v)
        {
            switch (v.Command)
            {
                case DiagCommand.Bundle: RequestBundle(v.Tag); break;
                case DiagCommand.Pixel: QueryPixel(v.X, v.Y, v.Dip, writeNow: true, out _); break;
                case DiagCommand.Scroll: ScrollViewport(v.Viewport, v.To, v.Glide); break;
                case DiagCommand.Viewports: WriteViewports(); break;
                case DiagCommand.Probe: SetProbe(v.Level); break;
                case DiagCommand.Xm: QueryExtensions(v.Uris ?? [], v.Kinds ?? [], v.Tag); break;
            }
        }

        // ── bundle ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>UI THREAD. Arm a capture of the next composited present and export the bundle when it lands (or, after
        /// 3 s without one, from the latest composite record with no frame.png).</summary>
        public static void RequestBundle(string tag)
        {
            var host = Probe.Host;
            if (host is null) { Reply("bundle", "no-host", ""); return; }
            if (Interlocked.CompareExchange(ref s_busy, 1, 0) != 0) { Reply("bundle", "busy", ""); return; }
            s_tag = EvidenceReport.SanitizeTag(tag);
            s_deadline = Environment.TickCount64 + CaptureTimeoutMs;
            host.RequestFrameCapture();
            if (!s_attached) { FluentApp.FrameCompleted += s_onFrame; s_attached = true; }
        }

        static void OnFrame(FrameStats _)
        {
            var host = Probe.Host;
            if (host is null) { Detach(); Volatile.Write(ref s_busy, 0); return; }
            bool landed = host.TryTakeFrameCapture(out FrameCaptureResult? capture);
            if (!landed && Environment.TickCount64 < s_deadline) return;
            Detach();
            try { Export(host, capture, s_tag); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Volatile.Write(ref s_busy, 0);
                Log.Warn("evidence", "[evidence] bundle export failed", ex);
                Reply("bundle", "failed", "");
            }
        }

        static void Detach()
        {
            if (!s_attached) return;
            FluentApp.FrameCompleted -= s_onFrame;
            s_attached = false;
        }

        /// <summary>A node's window position from its laid-out BOUNDS alone — <c>SceneStore.AbsoluteRect</c> minus every
        /// LocalTransform / child shift on the chain (evidence: layout truth vs pose truth, item H).</summary>
        static void LayoutOnly(FluentGpu.Scene.SceneStore scene, NodeHandle h, out float x, out float y)
        {
            x = 0f; y = 0f;
            for (var n = h; !n.IsNull; n = scene.Parent(n))
            {
                ref RectF b = ref scene.Bounds(n);
                x += b.X; y += b.Y;
            }
        }

        /// <summary>UI THREAD: read everything (engine ledgers, names, census, probe rows), then write on a worker.</summary>
        static void Export(AppHost host, FrameCaptureResult? capture, string tag)
        {
            var frame = capture?.Ledger ?? new CompositeFrameCopy();
            if (capture is null) host.CompositeLedger?.CopyLatest(frame);
            var view = frame.View;

            var names = new Dictionary<(int, uint), string>();
            string Name(int index, uint gen)
            {
                if (names.TryGetValue((index, gen), out var s)) return s;
                s = host.DescribeNode(index, gen);
                names[(index, gen)] = s;
                return s;
            }

            var rasters = new RasterEntry[RasterLedger.RasterLedgerCapacity];
            int nRasters = host.RasterLedger?.ReadTail(rasters) ?? 0;
            var walks = new WalkEntry[WalkLedger.WalkLedgerCapacity];
            int nWalks = host.WalkLedger?.ReadTail(walks) ?? 0;
            var vps = new List<ViewportInfo>();
            host.CopyViewports(vps);
            TileCensus census = host.LastTileCensus;
            host.TryGetDeviceCounters(out GpuFrameCounters device);

            var files = new List<(string Name, string Text)>
            {
                ("items.tsv", EvidenceReport.ItemsTsv(view.Items, Name)),
                ("placements.tsv", EvidenceReport.PlacementsTsv(view.Placements)),
                ("stale.tsv", EvidenceReport.StaleTsv(view.Placements, view.Items, Name)),
                ("ledger.tsv", EvidenceReport.LedgerTsv(rasters.AsSpan(0, nRasters), Name)),
                ("walks.tsv", EvidenceReport.WalksTsv(walks.AsSpan(0, nWalks), Name)),
                ("vps.tsv", EvidenceReport.ViewportsTsv(vps)),
                ("census.json", EvidenceReport.CensusJson(census, device, TileInvariants.StaleTurns)),
            };
            float scale = frame.Header.Scale > 0f ? frame.Header.Scale : 1f;
            using (var sw = new StringWriter(CultureInfo.InvariantCulture))
            {
                ScrollProbe.ExportCsv(sw, Stopwatch.Frequency, ScrollDiagRules.RefreshHz(NavigationFrameWatch.RefreshIntervalMs), scale);
                files.Add(("scroll.csv", sw.ToString()));
            }
            lock (s_pendingPixels)
            {
                foreach (var (name, tsv) in s_pendingPixels) files.Add((name, tsv));
                s_pendingPixels.Clear();
            }

            // nodes.tsv: every node the rows above named (resolved now, on the UI thread, against the live scene)
            var scene = host.Scene;
            var nodes = new List<EvidenceNodeRow>(names.Count);
            foreach (var ((index, gen), path) in names)
            {
                var h = scene.HandleAt(index);
                bool live = !h.IsNull && h.Raw.Gen == gen && scene.IsLive(h);
                RectF r = live ? scene.AbsoluteRect(h) : default;
                nodes.Add(new EvidenceNodeRow(index, gen, path, live ? NodeDescriber.ElementTypeName(scene.ElementTypeId(h)) : "-",
                    r.X, r.Y, r.W, r.H));
            }
            nodes.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            files.Add(("nodes.tsv", EvidenceReport.NodesTsv(nodes)));

            // keyed.tsv: EVERY live keyed node with its window rect — the scenario scripts find the page's parts by key
            // (the artist band's collapse distance = the magazine's content y − 56) without guessing layout constants.
            var keyed = new List<(NodeHandle Node, string Key)>();
            scene.CopyDebugKeys(keyed);
            var keyedRows = new List<EvidenceNodeRow>(keyed.Count);
            foreach (var (n, key) in keyed)
            {
                RectF r = scene.AbsoluteRect(n);
                LayoutOnly(scene, n, out float lx, out float ly);
                var t = scene.Paint(n).LocalTransform;
                keyedRows.Add(new EvidenceNodeRow((int)n.Raw.Index, n.Raw.Gen, key, NodeDescriber.ElementTypeName(scene.ElementTypeId(n)),
                    r.X, r.Y, r.W, r.H, lx, ly, t.Dx, t.Dy));
            }
            keyedRows.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            files.Add(("keyed.tsv", EvidenceReport.NodesTsv(keyedRows)));

            DateTime now = DateTime.Now;
            var meta = new EvidenceMeta(Build: typeof(Evidence).Assembly.GetName().Version?.ToString() ?? "?", Pid: Environment.ProcessId,
                Profile: Platform.ProfileRoot, Route: NavigationFrameWatch.Route, Arg: NavigationFrameWatch.Arg ?? "",
                NavId: NavigationFrameWatch.NavigationId, PublishSeq: capture?.PublishSeq ?? frame.Header.PublishSeq,
                TableFrame: frame.Header.Frame, Qpc: capture?.Qpc ?? frame.Header.Qpc, Scale: scale,
                WidthPx: frame.Header.WidthPx, HeightPx: frame.Header.HeightPx, Captured: capture?.Bgra is not null, Tag: tag,
                CreatedLocal: now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
            files.Add(("meta.json", EvidenceReport.MetaJson(in meta, vps)));

            string folder = Path.Combine(Root, EvidenceReport.BundleFolderName(now, tag));
            byte[]? png = capture?.Bgra;
            int pw = capture?.WidthPx ?? 0, ph = capture?.HeightPx ?? 0;
            string? logPath = Log.FilePath;
            _ = Task.Run(() => Write(folder, files, png, pw, ph, logPath));
        }

        /// <summary>WORKER. Write the bundle's files, frame.png, the log tail; append index.txt and the reply.</summary>
        static void Write(string folder, List<(string Name, string Text)> files, byte[]? bgra, int w, int h, string? logPath)
        {
            try
            {
                Directory.CreateDirectory(folder);
                foreach (var (name, text) in files) File.WriteAllText(Path.Combine(folder, name), text, Utf8);
                if (bgra is not null && w > 0 && h > 0) PngWriter.WriteBgra(Path.Combine(folder, "frame.png"), bgra, w, h);
                File.WriteAllText(Path.Combine(folder, "log-tail.txt"), EvidenceReport.Tail(ReadLines(logPath), LogTailLines), Utf8);
                lock (s_replyLock) File.AppendAllText(Path.Combine(Root, "index.txt"), folder + "\n", Utf8);
                Volatile.Write(ref s_lastFolder, folder);
                Log.Info("evidence", "[evidence] bundle=" + folder + " frame=" + (bgra is not null ? "1" : "0"));
                Reply("bundle", "ok", folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Warn("evidence", "[evidence] bundle write failed folder=" + folder, ex);
                Reply("bundle", "failed", folder);
            }
            finally { Volatile.Write(ref s_busy, 0); }
        }

        /// <summary>How far back from the log's end the tail is read. The dated log is shared by every run of the day
        /// (4–5 MB by evening), and reading it WHOLE for a 400-line tail turned every auto clamp bundle into ~50 MB of
        /// line strings in the middle of the drag it was capturing (EventPipe, 2026-09-25: <c>ReadLines</c> 51 MB of
        /// 393 MB sampled) — gen-2 pressure the UI thread paid for. 1 MiB holds 400 lines with room to spare.</summary>
        const int LogTailBytes = 1 << 20;

        static IReadOnlyList<string> ReadLines(string? path)
        {
            var lines = new List<string>(LogTailLines + 1);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return lines;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool partial = fs.Length > LogTailBytes;
            if (partial) fs.Seek(-LogTailBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            if (partial) reader.ReadLine();          // the first line is cut by the seek
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }

        // ── pixel ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>UI THREAD. The pixel query at window (<paramref name="x"/>, <paramref name="y"/>) — device px, or DIP when
        /// <paramref name="dip"/> — on the latest composite. Remembered for the next bundle; <paramref name="writeNow"/>
        /// also writes it at once to logs\evidence\adhoc\ (the <c>cmd=pixel</c> reply). Returns the hits, top last.</summary>
        public static PixelHit[] QueryPixel(int x, int y, bool dip, bool writeNow, out CompositeFrameHeader frame)
        {
            frame = default;
            var host = Probe.Host;
            if (host is null) { if (writeNow) Reply("pixel", "no-host", ""); return []; }
            var buf = new PixelHit[32];
            int n = dip ? host.QueryPixelDip(x, y, buf, out frame) : host.QueryPixel(x, y, buf, out frame);
            float s = frame.Scale > 0f ? frame.Scale : 1f;
            int px = dip ? (int)MathF.Floor(x * s) : x, py = dip ? (int)MathF.Floor(y * s) : y;
            var hits = buf.AsSpan(0, n).ToArray();
            var names = new Dictionary<(int, uint), string>();
            string Name(int index, uint gen)
            {
                if (!names.TryGetValue((index, gen), out var v)) names[(index, gen)] = v = host.DescribeNode(index, gen);
                return v;
            }
            string tsv = EvidenceReport.PixelTsv(px, py, in frame, hits, Name);
            string fileName = "pixel-" + px.ToString(CultureInfo.InvariantCulture) + "-" + py.ToString(CultureInfo.InvariantCulture) + ".tsv";
            lock (s_pendingPixels) s_pendingPixels.Add((fileName, tsv));
            if (writeNow)
            {
                string dir = Path.Combine(Root, "adhoc");
                string path = Path.Combine(dir, DateTime.Now.ToString("HHmmssfff", CultureInfo.InvariantCulture) + "-" + fileName);
                _ = Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(path, tsv, Utf8);
                        Log.Info("evidence", "[evidence] pixel=" + path + " hits=" + n.ToString(CultureInfo.InvariantCulture));
                        Reply("pixel", "ok", path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn("evidence", "[evidence] pixel write failed", ex);
                        Reply("pixel", "failed", path);
                    }
                });
            }
            return hits;
        }

        // ── viewports / scroll / probe ──────────────────────────────────────────────────────────────────────────────

        /// <summary>UI THREAD. logs\evidence\vps.tsv: every live viewport (key, offset, extent).</summary>
        public static void WriteViewports()
        {
            var host = Probe.Host;
            if (host is null) { Reply("vps", "no-host", ""); return; }
            var vps = new List<ViewportInfo>();
            host.CopyViewports(vps);
            string tsv = EvidenceReport.ViewportsTsv(vps), path = Path.Combine(Root, "vps.tsv");
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Root);
                    File.WriteAllText(path, tsv, Utf8);
                    Reply("vps", "ok", path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("evidence", "[evidence] vps write failed", ex);
                    Reply("vps", "failed", path);
                }
            });
        }

        /// <summary>UI THREAD. <c>ScrollHandle.ScrollTo</c> on the first viewport whose ScrollKey answers
        /// <paramref name="prefix"/> (<see cref="EvidenceReport.ViewportMatches"/>) — the engine's own handle, no input.</summary>
        public static void ScrollViewport(string prefix, double to, bool glide)
        {
            var host = Probe.Host;
            if (host is null) { Reply("scroll", "no-host", ""); return; }
            var vps = new List<ViewportInfo>();
            host.CopyViewports(vps);
            foreach (var v in vps)
            {
                if (!EvidenceReport.ViewportMatches(v.ScrollKey, prefix)) continue;
                var handle = host.TryGetScrollHandle(host.Scene.HandleAt(v.NodeIndex));
                if (handle is null) continue;
                handle.ScrollTo(to, glide ? ScrollMove.Glide : ScrollMove.Immediate);
                Log.Info("evidence", "[evidence] scroll vp=" + v.ScrollKey + " to=" + to.ToString("0.###", CultureInfo.InvariantCulture)
                    + " move=" + (glide ? "glide" : "immediate"));
                Reply("scroll", "ok", v.ScrollKey ?? "");
                return;
            }
            Log.Info("evidence", "[evidence] scroll vp=" + prefix + " not found");
            Reply("scroll", "not-found", prefix);
        }

        /// <summary>UI THREAD. The engine probe level for this session (NOT persisted — the Scroll card's setting is).</summary>
        /// <summary>UI THREAD. The extended-metadata evidence probe (<c>wavee://diag?cmd=xm&amp;uris=…&amp;kinds=…</c>): the
        /// server's own answer for these (kind, uri) pairs — what kinds 99 / 182 / 185 return for a track with no video
        /// or no counted plays — written to logs\evidence\xm-&lt;yyyyMMdd-HHmmss&gt;-&lt;tag&gt;.tsv with every payload as hex,
        /// plus one always-on <c>[evidence] xm</c> line with the per-kind counts. Reads no table and writes none; the POST
        /// runs on a worker (the api calls block; the UI thread never does).</summary>
        public static void QueryExtensions(string[] uris, int[] kinds, string tag)
        {
            if (uris.Length == 0 || kinds.Length == 0) { Reply("xm", "usage", ""); return; }
            string path = Path.Combine(Root, "xm-" + EvidenceReport.BundleFolderName(DateTime.Now, tag) + ".tsv");
            _ = Task.Run(() =>
            {
                try
                {
                    (int status, XmAnswerSummary? summary) = Spotify.Api.ProbeExtensions(uris, kinds);
                    Directory.CreateDirectory(Root);
                    File.WriteAllText(path, summary?.ToTsv() ?? "status\t" + status.ToString(CultureInfo.InvariantCulture) + "\n", Utf8);
                    var counts = new StringBuilder();
                    if (summary is not null)
                        foreach (var t in summary.Tally)
                            counts.Append(" k").Append(t.Kind.ToString(CultureInfo.InvariantCulture)).Append("=\"")
                                  .Append(XmAnswerSummary.Format(in t)).Append('"');
                    Log.Info("evidence", "[evidence] xm status=" + status.ToString(CultureInfo.InvariantCulture)
                        + " uris=" + uris.Length.ToString(CultureInfo.InvariantCulture) + counts + " file=" + path);
                    Reply("xm", summary is null ? "failed" : "ok", path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or HttpRequestException)
                {
                    Log.Warn("evidence", "[evidence] xm probe failed", ex);
                    Reply("xm", "failed", path);
                }
            });
        }

        public static void SetProbe(int level)
        {
            int l = ScrollDiagRules.ClampLevel(level);
            ScrollProbe.Level = (ProbeLevel)l;
            ScrollSettings.Level.Value = l;
            Log.Info("evidence", "[evidence] probe level=" + ((ProbeLevel)l).ToString());
            Reply("probe", "ok", ((ProbeLevel)l).ToString());
        }

        /// <summary>Append one reply line (<c>seq \t cmd \t result \t path</c>) to logs\evidence\replies.tsv — the file the
        /// harness polls (WM_COPYDATA cannot answer).</summary>
        static void Reply(string cmd, string result, string path)
        {
            try
            {
                lock (s_replyLock)
                {
                    Directory.CreateDirectory(Root);
                    int seq = ++s_replySeq;
                    File.AppendAllText(Path.Combine(Root, "replies.tsv"),
                        seq.ToString(CultureInfo.InvariantCulture) + "\t" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                        + "\t" + cmd + "\t" + result + "\t" + path + "\n", Utf8);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("evidence", "[evidence] reply write failed cmd=" + cmd, ex);
            }
        }
    }
}
