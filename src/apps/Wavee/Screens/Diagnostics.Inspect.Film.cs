// ── Screens/Diagnostics.Inspect.Film.cs ────────────────────────────────────────────────────────────────────────────────
// `film`: motion you can look at. It traces N frames from where the host stands (start the motion with an acting verb and settle=false)
// and keeps every Nth frame's pixels; the frames are laid into ONE contact-sheet PNG, each cell labelled with its frame number and its
// time on the 16 ms clock, so an agent can Read the sheet and see an open, a close or a slide. Named keys (keys=, prefix=) get a motion
// curve each: their presented x, y, w, h and opacity per frame, with the steps, reversals, stalls and snaps flagged (`InspectMotion`,
// which `trace ... curves=true` writes too).
//
// Role: SHELL (UI thread; the offscreen surface's capture, the trace runner, the trace's node samples)
// Spec: docs/guide/agent-inspect.md "film"; the skill's verify/debug-home.md is the recipe.

using System.Text.Json;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>Per-key motion curves over a trace's rows, and what in them is not smooth. Pure: rows in, findings out.</summary>
    public static class InspectMotion
    {
        /// <summary>One flagged frame of one channel: a step (one frame carries far more than its neighbours), a reversal (the
        /// motion turns back: a spring's overshoot does on purpose, anything else is a wobble), a stall (one still frame inside a
        /// motion: a hitch), a snap (the whole change in one frame).</summary>
        public readonly record struct Finding(string Key, string Channel, string Kind, int Frame, double Delta, double Before, double After);

        static readonly string[] Channels = ["x", "y", "w", "h", "opacity"];

        static double Value(in InspectTrace.NodeSample n, int c) => c switch
        {
            0 => n.Pres.X, 1 => n.Pres.Y, 2 => n.Pres.W, 3 => n.Pres.H, _ => n.Opacity,
        };

        /// <summary>The still threshold and the snap size of a channel: DIP for the rect, a share for opacity.</summary>
        static (double Still, double Snap) Scale(int c) => c == 4 ? (0.004, 0.3) : (0.05, 4.0);

        public static void Write(Utf8JsonWriter w, InspectTrace.Result result)
        {
            var findings = new List<Finding>();
            w.WriteStartArray("curves");
            for (int k = 0; k < result.Watch.Count; k++)
            {
                string key = result.Watch[k];
                var frames = new List<int>();
                var series = new List<double>[5];
                for (int c = 0; c < 5; c++) series[c] = [];
                foreach (InspectTrace.Row r in result.Rows)
                    foreach (InspectTrace.NodeSample n in r.Nodes)
                    {
                        if (n.W != k || !n.Shown) continue;
                        frames.Add(r.Frame);
                        for (int c = 0; c < 5; c++) series[c].Add(Value(in n, c));
                    }
                w.WriteStartObject();
                w.WriteString("key", key);
                w.WriteNumber("frames", frames.Count);
                if (frames.Count > 0) { w.WriteNumber("first", frames[0]); w.WriteNumber("last", frames[^1]); }
                int before = findings.Count;
                for (int c = 0; c < 5; c++)
                {
                    List<double> s = series[c];
                    if (s.Count < 2) continue;
                    double min = s[0], max = s[0];
                    foreach (double v in s) { min = Math.Min(min, v); max = Math.Max(max, v); }
                    if (max - min <= Scale(c).Still) continue;   // the channel never moved
                    w.WriteStartObject(Channels[c]);
                    w.WriteStartArray("v");
                    foreach (double v in s) w.WriteNumberValue(Math.Round(v, c == 4 ? 3 : 1));
                    w.WriteEndArray();
                    w.WriteString("spark", Spark(s, min, max));
                    w.WriteEndObject();
                    Detect(key, Channels[c], frames, s, Scale(c), findings);
                }
                w.WriteNumber("findings", findings.Count - before);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("motionFindings");
            foreach (Finding f in findings)
            {
                w.WriteStartObject();
                w.WriteString("key", f.Key);
                w.WriteString("channel", f.Channel);
                w.WriteString("kind", f.Kind);
                w.WriteNumber("frame", f.Frame);
                w.WriteNumber("delta", Math.Round(f.Delta, 3));
                w.WriteNumber("before", Math.Round(f.Before, 3));
                w.WriteNumber("after", Math.Round(f.After, 3));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            int steps = 0, reversals = 0, stalls = 0, snaps = 0;
            foreach (Finding f in findings)
                switch (f.Kind) { case "step": steps++; break; case "reversal": reversals++; break; case "stall": stalls++; break; default: snaps++; break; }
            w.WriteStartObject("motionCounts");
            w.WriteNumber("steps", steps); w.WriteNumber("reversals", reversals); w.WriteNumber("stalls", stalls); w.WriteNumber("snaps", snaps);
            w.WriteEndObject();
        }

        /// <summary>The flags of one channel's series. Deltas between consecutive SAMPLED frames (a frame the node was not shown on
        /// breaks the run). A run is a stretch of moving deltas with at most one still delta inside it.</summary>
        static void Detect(string key, string ch, List<int> frames, List<double> s, (double Still, double Snap) scale, List<Finding> into)
        {
            int n = s.Count;
            var d = new double[n - 1];
            for (int i = 1; i < n; i++) d[i - 1] = frames[i] - frames[i - 1] == 1 ? s[i] - s[i - 1] : double.NaN;
            bool Moving(int i) => i >= 0 && i < d.Length && !double.IsNaN(d[i]) && Math.Abs(d[i]) > scale.Still;
            int lastSign = 0, lastSignAt = -10;
            for (int i = 0; i < d.Length; i++)
            {
                if (double.IsNaN(d[i])) { lastSign = 0; continue; }
                double a = Math.Abs(d[i]);
                if (!Moving(i))
                {
                    // a still frame with motion on both sides: the motion paused for a frame (a hitch). Not in a settling tail: the moves on both
                    // sides must be visible ones (3/8 of a snap: 1.5 DIP, 0.11 opacity), not a spring's last pixel steps
                    double visible = scale.Snap * 0.375;
                    if (Moving(i - 1) && Moving(i + 1) && Math.Sign(d[i - 1]) == Math.Sign(d[i + 1])
                        && Math.Min(Math.Abs(d[i - 1]), Math.Abs(d[i + 1])) >= visible)
                        into.Add(new Finding(key, ch, "stall", frames[i + 1], d[i], d[i - 1], d[i + 1]));
                    continue;
                }
                int sign = Math.Sign(d[i]);
                if (lastSign != 0 && sign != lastSign && i - lastSignAt <= 2 && a > scale.Still * 4)
                    into.Add(new Finding(key, ch, "reversal", frames[i + 1], d[i], d[lastSignAt], i + 1 < d.Length ? d[i + 1] : 0));
                lastSign = sign; lastSignAt = i;
                bool prevMoving = Moving(i - 1), nextMoving = Moving(i + 1);
                if (!prevMoving && !nextMoving)
                {
                    if (a >= scale.Snap) into.Add(new Finding(key, ch, "snap", frames[i + 1], d[i], 0, 0));
                    continue;
                }
                double pa = prevMoving ? Math.Abs(d[i - 1]) : 0, na = nextMoving ? Math.Abs(d[i + 1]) : 0;
                // a step: this frame carries over 2.5x the larger neighbour, inside a motion (the first and last frames of an ease are small,
                // so a big frame beside them reads as a step only when it is far past both)
                if (a >= scale.Snap * 0.75 && a > 2.5 * Math.Max(pa, na) && prevMoving && nextMoving)
                    into.Add(new Finding(key, ch, "step", frames[i + 1], d[i], d[i - 1], d[i + 1]));
            }
        }

        static string Spark(List<double> s, double min, double max)
        {
            const string bars = "▁▂▃▄▅▆▇█";
            var chars = new char[s.Count];
            double span = max - min;
            for (int i = 0; i < s.Count; i++) chars[i] = bars[span <= 0 ? 0 : (int)Math.Round((s[i] - min) / span * 7)];
            return new string(chars);
        }
    }

    public sealed partial class InspectSession
    {
        /// <summary>Called after each traced frame with its index: `film` captures its pixels here.</summary>
        Action<int>? _traceAfterFrameHook;

        /// <summary>`film frames=N every=K path=<png>`: trace N frames, keep every Kth frame (and the last) in one contact sheet.</summary>
        string Film(int id, string name, Inspect.Args a, Shell.HeadlessShell shell)
        {
            string path = a.Str("path");
            if (path.Length == 0) return Inspect.Reply.Fail(id, name, "film wants a path (.png)");
            if (_surface is not { } surface) return Inspect.Reply.Fail(id, name, "film needs pixels: mount with gpu=warp or gpu=hardware (this mount: " + _devices + ")");
            int frames = Math.Clamp(a.Int("frames", 40), 1, 600);
            int every = Math.Clamp(a.Int("every", 4), 1, 600);
            int cols = Math.Clamp(a.Int("cols", 5), 1, 20);
            float scale = a.Float("scale", 0.5f);
            if (!(scale > 0.05f && scale <= 1f)) return Inspect.Reply.Fail(id, name, "0.05 < scale <= 1");
            if (!InspectTrace.TryParse(a, frames, "", out InspectTrace.Options o, out string error)) return Inspect.Reply.Fail(id, name, error);
            o.Rows = false;
            o.Curves = o.WatchesNodes;
            string full = Path.GetFullPath(path);
            string framesDir = a.Str("framesDir");
            // crop=<key>: the cells show that node's drawn rect (on the window, 8 DIP around it) as it stood when the film began
            RectF? crop = null;
            if (a.Str("crop") is { Length: > 0 } cropKey)
            {
                FluentGpu.Scene.SceneStore scene = shell.Host.Scene;
                foreach (var (node, key) in shell.FindPrefix(cropKey))
                {
                    if (key != cropKey || !scene.IsShown(node)) continue;
                    RectF r = scene.PresentedAbsoluteRect(node);
                    RectF win = InspectKit.WindowRect(shell);
                    float x0 = Math.Max(win.X, r.X - 8f), y0 = Math.Max(win.Y, r.Y - 8f);
                    float x1 = Math.Min(win.X + win.W, r.X + r.W + 8f), y1 = Math.Min(win.Y + win.H, r.Y + r.H + 8f);
                    if (x1 > x0 + 4f && y1 > y0 + 4f) crop = new RectF(x0, y0, x1 - x0, y1 - y0);
                    break;
                }
                if (crop is null) return Inspect.Reply.Fail(id, name, "crop: no shown node keyed '" + cropKey + "' is on the window (scroll it into view first)");
            }
            float pxScale = shell.Window.Scale;
            var cells = new List<(int Frame, byte[] Px, int W, int H)>();
            _traceAfterFrameHook = f =>
            {
                if (f % every != 0 && f != frames - 1) return;
                byte[] all = surface.CaptureBgra(out int w, out int h);
                if (crop is { } c) all = Crop(all, w, h, (int)(c.X * pxScale), (int)(c.Y * pxScale), (int)(c.W * pxScale), (int)(c.H * pxScale), out w, out h);
                cells.Add((f, all, w, h));
            };
            InspectTrace.Result result;
            try { result = RunTrace(shell, o); }
            finally { _traceAfterFrameHook = null; }
            if (cells.Count == 0) return Inspect.Reply.Fail(id, name, "no frame was captured");
            ColorF ground = Tok.T.WindowBackground;
            byte gr = ToByte(ground.R), gg = ToByte(ground.G), gb = ToByte(ground.B);
            if (framesDir.Length > 0)
            {
                string dir = Path.GetFullPath(framesDir);
                Directory.CreateDirectory(dir);
                foreach (var (f, px, w, h) in cells) FluentGpu.Foundation.PngWriter.WriteBgra(Path.Combine(dir, "f" + f.ToString("D3") + ".png"), px, w, h, ground);
            }
            ContactSheet.Write(full, cells, cols, scale, gr, gg, gb);
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("path", full);
                w.WriteNumber("traced", result.Rows.Count);
                w.WriteNumber("cells", cells.Count);
                if (crop is { } c) Inspect.Reply.Rect(w, "crop", c);
                w.WriteNumber("every", every);
                w.WriteString("clock", "16 ms per frame (the inspector's fixed clock); a cell's +ms is its frame's time since the film began");
                w.WriteStartArray("cellFrames");
                foreach (var cell in cells) w.WriteNumberValue(cell.Frame);
                w.WriteEndArray();
                if (framesDir.Length > 0) w.WriteString("framesDir", Path.GetFullPath(framesDir));
                w.WriteStartArray("watch");
                foreach (string k in result.Watch) w.WriteStringValue(k);
                w.WriteEndArray();
                if (o.WatchesNodes) InspectMotion.Write(w, result);
                w.WriteNumber("settledAt", result.Summary.SettledAt);
                w.WriteNumber("snaps", result.Summary.Snaps.Count);
                w.WriteNumber("instants", result.Summary.Instants.Count);
                w.WriteNumber("remounted", result.Remounts.Count);
            });

            static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        }

        /// <summary>A rectangle of a BGRA8 frame (clamped to it).</summary>
        static byte[] Crop(byte[] src, int sw, int sh, int x, int y, int cw, int ch, out int w, out int h)
        {
            x = Math.Clamp(x, 0, sw - 1); y = Math.Clamp(y, 0, sh - 1);
            w = Math.Clamp(cw, 1, sw - x); h = Math.Clamp(ch, 1, sh - y);
            var dst = new byte[w * h * 4];
            for (int row = 0; row < h; row++) Buffer.BlockCopy(src, ((y + row) * sw + x) * 4, dst, row * w * 4, w * 4);
            return dst;
        }

        /// <summary>The contact sheet: cells in rows of <c>cols</c>, each frame scaled by area averaging and flattened over the window
        /// ground, a label bar above each (frame number and +ms on the 16 ms clock) in a 5x7 bitmap font.</summary>
        static class ContactSheet
        {
            const int Pad = 6, Bar = 18;

            public static void Write(string path, List<(int Frame, byte[] Px, int W, int H)> cells, int cols, float scale, byte gr, byte gg, byte gb)
            {
                int sw = cells[0].W, sh = cells[0].H;
                int cw = Math.Max(1, (int)(sw * scale)), ch = Math.Max(1, (int)(sh * scale));
                cols = Math.Min(cols, cells.Count);
                int rows = (cells.Count + cols - 1) / cols;
                int W = Pad + cols * (cw + Pad), H = Pad + rows * (ch + Bar + Pad);
                var sheet = new byte[W * H * 4];
                for (int i = 0; i < W * H; i++) { sheet[i * 4] = 40; sheet[i * 4 + 1] = 40; sheet[i * 4 + 2] = 40; sheet[i * 4 + 3] = 255; }
                for (int c = 0; c < cells.Count; c++)
                {
                    var (frame, px, w, h) = cells[c];
                    int ox = Pad + (c % cols) * (cw + Pad), oy = Pad + (c / cols) * (ch + Bar + Pad);
                    Text(sheet, W, ox + 2, oy + 2, "f" + frame + " +" + ((frame + 1) * 16) + "ms");
                    Blit(sheet, W, ox, oy + Bar, cw, ch, px, w, h, gr, gg, gb);
                }
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                FluentGpu.Foundation.PngWriter.WriteBgra(path, sheet, W, H);
            }

            static void Blit(byte[] dst, int dw, int ox, int oy, int cw, int ch, byte[] src, int sw, int sh, byte gr, byte gg, byte gb)
            {
                double fx = (double)sw / cw, fy = (double)sh / ch;
                for (int y = 0; y < ch; y++)
                {
                    int y0 = (int)(y * fy), y1 = Math.Max(y0 + 1, Math.Min(sh, (int)((y + 1) * fy)));
                    for (int x = 0; x < cw; x++)
                    {
                        int x0 = (int)(x * fx), x1 = Math.Max(x0 + 1, Math.Min(sw, (int)((x + 1) * fx)));
                        int b = 0, g = 0, r = 0, n = 0;
                        for (int yy = y0; yy < y1; yy++)
                            for (int xx = x0; xx < x1; xx++)
                            {
                                int i = (yy * sw + xx) * 4;
                                int inv = 255 - src[i + 3];
                                b += src[i] + (gb * inv + 127) / 255;
                                g += src[i + 1] + (gg * inv + 127) / 255;
                                r += src[i + 2] + (gr * inv + 127) / 255;
                                n++;
                            }
                        int o = ((oy + y) * dw + ox + x) * 4;
                        dst[o] = (byte)Math.Min(255, b / n); dst[o + 1] = (byte)Math.Min(255, g / n); dst[o + 2] = (byte)Math.Min(255, r / n); dst[o + 3] = 255;
                    }
                }
            }

            // 5x7 glyphs, one string of 35 bits per character (rows top to bottom).
            static readonly Dictionary<char, string> s_glyphs = new()
            {
                ['0'] = "01110100011001110101110011000101110", ['1'] = "00100011000010000100001000010001110",
                ['2'] = "01110100010000100010001000100011111", ['3'] = "11111000100010000010000011000101110",
                ['4'] = "00010001100101010010111110001000010", ['5'] = "11111100001111000001000011000101110",
                ['6'] = "00110010001000011110100011000101110", ['7'] = "11111000010001000100010000100001000",
                ['8'] = "01110100011000101110100011000101110", ['9'] = "01110100011000101111000010001001100",
                ['f'] = "00110010000100011100010000100001000", ['m'] = "00000000001101010101101011010110101",
                ['s'] = "00000000000111110000011100000111110", ['+'] = "00000001000010011111001000010000000",
                [' '] = "00000000000000000000000000000000000",
            };

            static void Text(byte[] dst, int dw, int x, int y, string text)
            {
                foreach (char c in text)
                {
                    if (s_glyphs.TryGetValue(c, out string? bits))
                        for (int row = 0; row < 7; row++)
                            for (int col = 0; col < 5; col++)
                                if (bits[row * 5 + col] == '1')
                                    for (int sy = 0; sy < 2; sy++)
                                        for (int sx = 0; sx < 2; sx++)
                                        {
                                            int px = x + col * 2 + sx, py = y + row * 2 + sy;
                                            int o = (py * dw + px) * 4;
                                            if (o >= 0 && o + 3 < dst.Length) { dst[o] = 255; dst[o + 1] = 255; dst[o + 2] = 255; }
                                        }
                    x += 12;
                }
            }
        }
    }
}
