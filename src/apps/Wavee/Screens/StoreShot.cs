// ── Screens/StoreShot.cs — the Store screenshot capture: wavee://diag?cmd=shot|present|seek|stage|rail ─────────────────
//
// The Microsoft Store listing is shot from a VERIFY instance (ops/tools/evidence/Start-VerifyWavee.ps1: its own --profile,
// signed in, never the owner's window) driven by these developer-only verbs, then composed by
// ops/release/tools/New-StoreImage.py. What the verbs add over the evidence bundle:
//
//   present  PRESENTATION MODE: the developer surfaces (lyrics debug pill, inspector button, FPS overlay, the rail's
//            presentation switch) and the account's identity (the title bar's name and picture, the daylist greeting's
//            name) are hidden, so a listing image carries no account data and no tooling.
//   shot     presentation mode on, an optional render zoom (a callout's source at 2-3x the pixels, from the SAME app),
//            a settle wait, then the next composited present read back with its alpha (Mica stays see-through) as
//            shot.png, and shot.json: the scale and every SHOWN keyed node's window rect, so the composer places a
//            spotlight or a callout on the real element instead of on hand-measured coordinates. Restores both after.
//   seek     a position (and optionally pause): a paused word-synced line holds the wipe on one word.
//   stage    the full-screen stage: open/close, mode, face, gallery.
//   rail     the right-hand rail: lyrics / queue / closed.
//   reveal   scroll a viewport so a keyed element sits at its top (also a shot option, after the zoom's reflow).
//   video    the video surface's placement: docked / floating (mini player) / detached (own window) / fullscreen / off.
//
// Files land in logs\evidence\shots\<yyyyMMdd-HHmmss>-<tag>\; every verb answers on logs\evidence\replies.tsv (the
// evidence harness's reply channel). Threading: verbs arrive on the UI thread; the settle wait is a timer that posts back
// through ToUi; the frame lands on the UI thread's FrameCompleted; a worker writes the files.

using System.Globalization;
using System.Text;
using FluentGpu;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Diagnostics
{
    public static class StoreShot
    {
        /// <summary>Presentation mode: developer surfaces and the account's identity hidden. Read it (subscribing) wherever
        /// one of them renders — through <see cref="DeveloperSurfaces"/> for the developer ones.</summary>
        public static readonly Signal<bool> Presenting = new(false);

        /// <summary>The UI-thread poster (installed by <c>Shell.InstallMarshallers</c>; inline until then).</summary>
        public static Action<Action> ToUi { get; set; } = static a => a();

        /// <summary>Developer mode's VISIBLE surfaces: on with developer mode, off while presenting. The diag verbs
        /// themselves keep the plain setting (presentation mode must not lock the harness out). Subscribes to both.</summary>
        public static bool DeveloperSurfaces()
        {
            _ = Platform.SettingsChanged.Value;
            return Platform.Settings.Get(Platform.Keys.DeveloperMode) && !Presenting.Value;
        }

        static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
        const int CaptureTimeoutMs = 4000;

        static string Root => Path.Combine(Evidence.Root, "shots");

        // one shot at a time
        static int s_busy;
        static Pending? s_pending;
        static bool s_attached;
        static System.Threading.Timer? s_settle, s_reveal;
        static readonly Action<FrameStats> s_onFrame = OnFrame;

        sealed record Pending(DiagVerb Verb, bool WasPresenting, float PriorZoom, long Deadline);

        /// <summary>UI THREAD. The deep-link door's arm for the capture verbs.</summary>
        public static void Apply(in DiagVerb v)
        {
            switch (v.Command)
            {
                case DiagCommand.Present:
                    Presenting.Value = v.On == 1;
                    Evidence.Reply("present", "ok", v.On == 1 ? "on" : "off");
                    break;
                case DiagCommand.Seek:
                    Playback.SeekTo(v.Ms);
                    if (v.On == 1) Playback.Pause("diag.shot");
                    else if (v.On == 0) Playback.Resume("diag.shot");
                    Evidence.Reply("seek", "ok", v.Ms.ToString(CultureInfo.InvariantCulture));
                    break;
                case DiagCommand.Stage: ApplyStage(v); break;
                case DiagCommand.Rail:
                    if (v.Mode == "off") Shell.Ui.RailOpen.Value = false;
                    else
                    {
                        Shell.Ui.Mode.Value = v.Mode == "queue" ? Shell.RailMode.Queue : Shell.RailMode.Lyrics;
                        Shell.Ui.RailOpen.Value = true;
                    }
                    Evidence.Reply("rail", "ok", v.Mode);
                    break;
                case DiagCommand.Video:
                    if (v.Mode == "off") Video.State.TurnOff();
                    else Video.State.OpenAt(v.Mode switch
                    {
                        "floating" => Video.SurfacePlacement.Floating,
                        "detached" => Video.SurfacePlacement.Detached,
                        "fullscreen" => Video.SurfacePlacement.Fullscreen,
                        _ => Video.SurfacePlacement.Docked,
                    });
                    Evidence.Reply("video", "ok", Video.State.Resolved.ToString());
                    break;
                case DiagCommand.Reveal: Reveal(v.Viewport, v.Keys is [var key, ..] ? key : ""); break;
                case DiagCommand.Shot: Request(v); break;
            }
        }

        static void ApplyStage(in DiagVerb v)
        {
            // Preferences first: the stage reads them as it mounts, so an open in the same verb lands on the asked face.
            if (v.Mode.Length > 0)
                Prefs.Stage.SetMode((int)(v.Mode switch
                {
                    "visualizer" => Stage.Mode.Visualizer,
                    "queue" => Stage.Mode.Queue,
                    "artist" => Stage.Mode.Artist,
                    _ => Stage.Mode.Lyrics,
                }));
            if (v.Face.Length > 0)
            {
                if (!Enum.TryParse(v.Face, ignoreCase: true, out Visualizer.Kind kind)) { Evidence.Reply("stage", "unknown-face", v.Face); return; }
                Prefs.Stage.SetVisualizer((int)kind);
            }
            if (v.Gallery >= 0) Prefs.Stage.SetGalleryOpen(v.Gallery == 1);
            if (v.On == 1) Stage.Open(null, "diag.shot");
            else if (v.On == 0) Stage.Close(null, "diag.shot");
            Evidence.Reply("stage", "ok", Shell.Ui.ImmersiveLyrics.Peek() ? "open" : "closed");
        }

        /// <summary>UI THREAD. Scroll the viewport named by <paramref name="prefix"/> so the element keyed
        /// <paramref name="key"/> sits 16 DIP below its top — the scene's element in view at ANY zoom, where a fixed
        /// offset would land somewhere else once the page reflows.</summary>
        static void Reveal(string prefix, string key)
        {
            var host = Probe.Host;
            if (host is null) { Evidence.Reply("reveal", "no-host", ""); return; }
            var scene = host.Scene;
            var keyed = new List<(NodeHandle Node, string Key)>();
            scene.CopyDebugKeys(keyed);
            NodeHandle target = default;
            foreach (var (n, k) in keyed) if (k == key && scene.IsShown(n)) { target = n; break; }
            if (target.IsNull) { Evidence.Reply("reveal", "no-key", key); return; }
            var vps = new List<ViewportInfo>();
            host.CopyViewports(vps);
            foreach (var v in vps)
            {
                if (!EvidenceReport.ViewportMatches(v.ScrollKey, prefix)) continue;
                var handle = host.TryGetScrollHandle(scene.HandleAt(v.NodeIndex));
                if (handle is null) continue;
                RectF r = scene.AbsoluteRect(target);
                double to = Math.Max(0.0, v.Offset + (v.Horizontal ? r.X - v.X : r.Y - v.Y) - 16.0);
                handle.ScrollTo(to, ScrollMove.Immediate);
                Evidence.Reply("reveal", "ok", to.ToString("0.###", CultureInfo.InvariantCulture));
                return;
            }
            Evidence.Reply("reveal", "no-viewport", prefix);
        }

        // ── shot ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>UI THREAD. Presentation mode on, the zoom applied, then the capture is armed after the settle wait.</summary>
        static void Request(in DiagVerb v)
        {
            if (Probe.Host is null) { Evidence.Reply("shot", "no-host", ""); return; }
            if (Interlocked.CompareExchange(ref s_busy, 1, 0) != 0) { Evidence.Reply("shot", "busy", ""); return; }
            var p = new Pending(v, Presenting.Peek(), FluentApp.Zoom, 0L);
            s_pending = p;
            Presenting.Value = true;
            if (v.Zoom > 0) FluentApp.SetZoom(p.PriorZoom * v.Zoom / 100f);
            s_settle?.Dispose();
            // A reveal runs half-way through the settle: after the zoom's reflow, before the frame is taken.
            if (v.Reveal.Length > 0)
            {
                string vp = v.Viewport, key = v.Reveal;
                s_reveal?.Dispose();
                s_reveal = new System.Threading.Timer(_ => ToUi(() => Reveal(vp, key)), null, Math.Max(0, v.SettleMs / 2), System.Threading.Timeout.Infinite);
            }
            s_settle = new System.Threading.Timer(static _ => ToUi(Arm), null, Math.Max(0, v.SettleMs), System.Threading.Timeout.Infinite);
        }

        /// <summary>UI THREAD, after the settle wait: arm a capture of the next composited present.</summary>
        static void Arm()
        {
            var host = Probe.Host;
            if (host is null || s_pending is not { } p) { Finish(ok: false, "no-host", ""); return; }
            s_pending = p with { Deadline = Environment.TickCount64 + CaptureTimeoutMs };
            host.RequestFrameCapture();
            if (!s_attached) { FluentApp.FrameCompleted += s_onFrame; s_attached = true; }
        }

        static void OnFrame(FrameStats stats)
        {
            var host = Probe.Host;
            if (host is null || s_pending is not { } p) { Detach(); Finish(ok: false, "no-host", ""); return; }
            bool landed = host.TryTakeFrameCapture(out FrameCaptureResult? capture);
            if (!landed && Environment.TickCount64 < p.Deadline) return;
            Detach();
            if (capture?.Bgra is not { } bgra) { Finish(ok: false, "no-frame", ""); return; }

            // UI thread: the keyed rects of THIS scene (the capture landed on the turn that presented it), then restore.
            var scene = host.Scene;
            float scale = capture.Ledger.Header.Scale > 0f ? capture.Ledger.Header.Scale : 1f;
            var keyed = new List<(NodeHandle Node, string Key)>();
            scene.CopyDebugKeys(keyed);
            var shown = new List<ShotKeyRow>(keyed.Count);
            foreach (var (n, key) in keyed)
            {
                if (!scene.IsShown(n)) continue;
                RectF r = scene.AbsoluteRect(n);
                shown.Add(new ShotKeyRow(key, NodeDescriber.ElementTypeName(scene.ElementTypeId(n)), r.X, r.Y, r.W, r.H));
            }
            var rows = EvidenceReport.ShotKeys(shown, capture.WidthPx / scale, capture.HeightPx / scale, p.Verb.Keys);
            DateTime now = DateTime.Now;
            var meta = new ShotMeta(p.Verb.Tag, NavigationFrameWatch.Route, NavigationFrameWatch.Arg ?? "", scale, FluentApp.Zoom,
                capture.WidthPx, capture.HeightPx, p.Verb.Alpha, Presenting.Peek(), Playback.PositionMs.Peek(),
                now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
            string json = EvidenceReport.ShotJson(in meta, rows);
            string folder = Path.Combine(Root, EvidenceReport.BundleFolderName(now, p.Verb.Tag));
            int w = capture.WidthPx, h = capture.HeightPx;
            bool alpha = p.Verb.Alpha;
            Restore(p);
            _ = Task.Run(() => Write(folder, bgra, w, h, alpha, json));
        }

        static void Restore(Pending p)
        {
            if (p.Verb.Zoom > 0) FluentApp.SetZoom(p.PriorZoom);
            Presenting.Value = p.WasPresenting;
            s_pending = null;
        }

        /// <summary>WORKER. shot.png + shot.json, then the reply.</summary>
        static void Write(string folder, byte[] bgra, int w, int h, bool alpha, string json)
        {
            try
            {
                Directory.CreateDirectory(folder);
                PngWriter.WriteBgra(Path.Combine(folder, "shot.png"), bgra, w, h, keepAlpha: alpha);
                File.WriteAllText(Path.Combine(folder, "shot.json"), json, Utf8);
                Log.Info("evidence", "[shot] " + folder + " " + w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture));
                Evidence.Reply("shot", "ok", folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Warn("evidence", "[shot] write failed folder=" + folder, ex);
                Evidence.Reply("shot", "failed", folder);
            }
            finally { Volatile.Write(ref s_busy, 0); }
        }

        static void Finish(bool ok, string result, string path)
        {
            if (s_pending is { } p) Restore(p);
            Volatile.Write(ref s_busy, 0);
            if (!ok) Evidence.Reply("shot", result, path);
        }

        static void Detach()
        {
            if (!s_attached) return;
            FluentApp.FrameCompleted -= s_onFrame;
            s_attached = false;
        }
    }
}
