// ── Screens/Diagnostics.Inspect.Crossing.cs — the `crossing` verb: resize across a tier edge the way the user does, report what broke ─
// `crossing {from:{w,h}, to:{w,h}, frames?, items?, max?, + the mount args (theme, kind, route, gpu...) and the lint options}`
//
// Role: SHELL (UI thread; drives the headless window and reads the engine's public scene inspection)
// Spec: handoff/themes-2026-10-17/agent-inspect-plan.md lane A3; the sequence is Wavee.ShellTests ZuneTierLayoutTests.Discovery.cs `CrossPage`.
//
// THE SEQUENCE (engine plan 3.3): mount (or resize) to `from` and settle; remember every keyed node that is shown. NARROWING: the OS modal size
// loop (`InModalLoop`/`SizedInModalLoop`), the resize, a keep-alive paint inside the loop, out of the loop `frames` more keep-alive paints
// (still the OLD tier's layout at the new size), then the keep-alive paint that is the COMMIT frame, where the new tier lands. WIDENING: the
// resize and `frames` keep-alive paints, then a normal frame as the commit. Then settle and lint.
//
// WHAT IT REPORTS.
//   remounted  a keyed node (unique before) that is a different node after the settle: its subtree and state were thrown away.
//   vanished   a keyed node that is gone (or no longer shown) after the settle; tiers that drop a part on purpose show up here.
//   appeared   keys shown after the settle that were not before.
//   snapped    nodes whose laid-out rect changed ON THE COMMIT FRAME (against the resized frame, so plain window resizing is not counted) with
//              no animation to cover it: no track on the node or any ancestor, and the presented rect already equal to the laid-out one.
//   jumped     the snapped nodes that only MOVED (same size): the content below something that changed height, shifting at once.
// Only the topmost snapped or jumped node of a subtree is listed; `counts` has the totals. Under reduced motion nothing glides, so everything
// that changed reads as snapped (`reducedMotion` says so).

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee.Theming;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>One keyed node followed through a crossing.</summary>
        sealed class Tracked(string key, NodeHandle node, RectF before)
        {
            public string Key { get; } = key;
            public NodeHandle Node { get; } = node;
            public RectF Before { get; } = before;
            public bool ResizedLive;
            public RectF Resized;
            public RectF Commit;

            /// <summary>0 unchanged on the commit frame, 1 glided, 2 snapped, 3 jumped.</summary>
            public int Motion;
        }

        const int Glided = 1, Snapped = 2, Jumped = 3;

        static readonly char[] s_sizeSeparators = ['x', ',', ' '];

        /// <summary>`from` / `to` as <c>{"w","h"}</c> (JSON), <c>"WxH"</c> (text), or the flat <paramref name="wName"/> / <paramref name="hName"/> args.</summary>
        static bool TrySize(Inspect.Args a, string name, string wName, string hName, out float w, out float h)
        {
            w = h = 0f;
            if (a.TryGet(name, out Inspect.Value value))
            {
                double dw = 0, dh = 0;
                bool ok = false;
                if (value.Kind == Inspect.ValueKind.Json)
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(value.Str);
                        JsonElement root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object)
                            ok = root.TryGetProperty("w", out JsonElement we) && we.TryGetDouble(out dw)
                                && root.TryGetProperty("h", out JsonElement he) && he.TryGetDouble(out dh);
                        else if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 2)
                            ok = root[0].TryGetDouble(out dw) && root[1].TryGetDouble(out dh);
                    }
                    catch (JsonException) { ok = false; }
                }
                else
                {
                    string[] parts = value.ToString().ToLowerInvariant().Split(s_sizeSeparators, StringSplitOptions.RemoveEmptyEntries);
                    ok = parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out dw)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out dh);
                }
                w = (float)dw;
                h = (float)dh;
                return ok && w >= 1f && h >= 1f;
            }
            if (!a.TryNum(wName, out double fw) || !a.TryNum(hName, out double fh)) return false;
            w = (float)fw;
            h = (float)fh;
            return w >= 1f && h >= 1f;
        }

        static bool SameRect(in RectF a, in RectF b)
            => MathF.Abs(a.X - b.X) <= 0.5f && MathF.Abs(a.Y - b.Y) <= 0.5f && MathF.Abs(a.W - b.W) <= 0.5f && MathF.Abs(a.H - b.H) <= 0.5f;

        /// <summary>Every shown keyed node with a box, and its laid-out rect now.</summary>
        static List<Tracked> TrackKeyed(Shell.HeadlessShell shell)
        {
            SceneStore scene = shell.Host.Scene;
            var tracked = new List<Tracked>();
            foreach (var (node, key) in shell.FindPrefix(""))
            {
                if (!scene.IsShown(node)) continue;
                RectF rect = scene.AbsoluteRect(node);
                if (rect.W > 0f && rect.H > 0f) tracked.Add(new Tracked(key, node, rect));
            }
            return tracked;
        }

        /// <summary>The node or an ancestor holds a live animation track.</summary>
        static bool TracksUp(Shell.HeadlessShell shell, NodeHandle node)
        {
            SceneStore scene = shell.Host.Scene;
            for (NodeHandle n = node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
                if (shell.Host.Animation.HasTracks(n)) return true;
            return false;
        }

        /// <summary>A keep-alive paint: a frame of a window that is being resized (the shell tests' rig), counted like any frame.</summary>
        void KeepAlivePaint()
        {
            _shell!.Host.Paint(0, keepAlive: true);
            _ms += 16;
            _frames++;
        }

        /// <summary>`resize loop=true`: the size change the way a user's drag ends it, as `crossing` narrows: inside the OS modal size loop a keep-alive paint
        /// at the new size, then out of the loop one more keep-alive paint, then the keep-alive commit paint where a held crossing lands. What follows
        /// (`settle=false`, `trace=N`, `render`) sees the frames right after that commit.</summary>
        void ResizeThroughModalLoop(Shell.HeadlessShell shell, float w, float h)
        {
            bool wasComposited = shell.Window.Composited;
            shell.Window.Composited = true;
            try
            {
                shell.Window.InModalLoop = true;
                shell.Window.SizedInModalLoop = true;
                shell.Resize(w, h);
                KeepAlivePaint();
                shell.Window.InModalLoop = false;
                shell.Window.SizedInModalLoop = false;
                KeepAlivePaint();
                KeepAlivePaint();
            }
            finally
            {
                shell.Window.InModalLoop = false;
                shell.Window.SizedInModalLoop = false;
                shell.Window.Composited = wasComposited;
            }
        }

        static void WriteTierState(Utf8JsonWriter w, string name, in TierFacts t)
        {
            w.WriteStartObject(name);
            w.WriteString("window", t.Window.ToString());
            w.WriteString("height", t.Height.ToString());
            w.WriteString("content", t.Content.ToString());
            InspectDump.WriteRungs(w, in t);   // ids, as `tiers` lists them
            w.WriteEndObject();
        }

        static bool SameTiers(in TierFacts a, in TierFacts b) => a.Window == b.Window && a.Height == b.Height && a.Rungs == b.Rungs;

        string Crossing(int id, string name, Inspect.Args a)
        {
            if (!TrySize(a, "from", "fromW", "fromH", out float fromW, out float fromH)) return Inspect.Reply.Fail(id, name, "crossing wants from {w,h} (or from=WxH)");
            if (!TrySize(a, "to", "toW", "toH", out float toW, out float toH)) return Inspect.Reply.Fail(id, name, "crossing wants to {w,h} (or to=WxH)");
            // Over a mount that is up (no remount), the crossing resizes it: the mount args would do nothing, so they are refused.
            if (_shell is not null && !a.Bool("remount", false))
                foreach (string arg in a.Names)
                    if (Inspect.IsMountOnly(arg)) return Inspect.Reply.Fail(id, name, "'" + arg + "' applies only when the crossing mounts (nothing mounted, or remount=true)");
            if (!InspectLintScene.TryOptions(a, out InspectLint.Options options, out string error)) return Inspect.Reply.Fail(id, name, error);
            int resizedFrames = Math.Clamp(a.Int("frames", 1), 1, 60);
            int items = Math.Clamp(a.Int("items", 40), 1, 1000);
            int max = Math.Clamp(a.Int("max", 600), 1, 100_000);

            // 1. the from-size, settled (a mount when nothing is up or `remount=true`: the mount args apply)
            if (_shell is null || a.Bool("remount", false))
            {
                if (!MountShell(a, fromW, fromH, out error, out _)) return Inspect.Reply.Fail(id, name, error);
            }
            else
            {
                _shell.Resize(fromW, fromH);
                if ((a.Has("route") || a.Has("uri") || a.Has("deeplink")) && !Navigate(a, out error)) return Inspect.Reply.Fail(id, name, error);
            }
            Shell.HeadlessShell shell = _shell ?? throw new InvalidOperationException("the crossing has no mount");
            Settle(max);
            SceneStore scene = shell.Host.Scene;
            List<Tracked> tracked = TrackKeyed(shell);
            TierFacts tiersBefore = ThemeHost.Facts.Peek().Tiers;

            bool narrowing = toW < fromW || toH < fromH;
            bool wasComposited = shell.Window.Composited;
            TierFacts tiersResized, tiersCommit;
            InspectKit.SettleResult settle;
            int changed = 0;
            shell.Window.Composited = true;
            try
            {
                // 2. the resized frames: the old tier, laid out at the new size
                if (narrowing)
                {
                    shell.Window.InModalLoop = true;
                    shell.Window.SizedInModalLoop = true;
                    shell.Resize(toW, toH);
                    KeepAlivePaint();
                    shell.Window.InModalLoop = false;
                    shell.Window.SizedInModalLoop = false;
                    for (int i = 0; i < resizedFrames; i++) KeepAlivePaint();
                }
                else
                {
                    shell.Resize(toW, toH);
                    for (int i = 0; i < resizedFrames; i++) KeepAlivePaint();
                }
                tiersResized = ThemeHost.Facts.Peek().Tiers;
                foreach (Tracked t in tracked)
                {
                    if (!scene.IsLive(t.Node)) continue;
                    t.ResizedLive = true;
                    t.Resized = scene.AbsoluteRect(t.Node);
                }

                // 3. the commit frame: the new tier lands here
                if (narrowing) KeepAlivePaint();
                else RunFrames(1);
                tiersCommit = ThemeHost.Facts.Peek().Tiers;
                foreach (Tracked t in tracked)
                {
                    if (!t.ResizedLive || !scene.IsLive(t.Node) || !scene.IsShown(t.Node)) continue;
                    RectF now = scene.AbsoluteRect(t.Node);
                    t.Commit = now;
                    if (SameRect(t.Resized, now)) continue;
                    changed++;
                    bool presenting = !SameRect(scene.PresentedAbsoluteRect(t.Node), now);   // still easing toward the laid-out rect
                    if (presenting || TracksUp(shell, t.Node)) { t.Motion = Glided; continue; }
                    bool onlyMoved = MathF.Abs(now.W - t.Resized.W) <= 0.5f && MathF.Abs(now.H - t.Resized.H) <= 0.5f;
                    t.Motion = onlyMoved ? Jumped : Snapped;
                }

                // 4. settle
                settle = Settle(max);
            }
            finally
            {
                shell.Window.InModalLoop = false;
                shell.Window.SizedInModalLoop = false;
                shell.Window.Composited = wasComposited;
            }

            // 5. identity after the settle
            var shown = new Dictionary<string, List<NodeHandle>>(StringComparer.Ordinal);
            foreach (var (node, key) in shell.FindPrefix(""))
            {
                if (!scene.IsShown(node)) continue;
                RectF rect = scene.AbsoluteRect(node);
                if (rect.W <= 0f || rect.H <= 0f) continue;
                if (!shown.TryGetValue(key, out List<NodeHandle>? list)) shown[key] = list = [];
                list.Add(node);
            }
            var beforeCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Tracked t in tracked) beforeCount[t.Key] = beforeCount.GetValueOrDefault(t.Key) + 1;
            var remounted = new List<Tracked>();
            var vanished = new List<Tracked>();
            var recounted = new List<(string Key, int Before, int After)>();
            foreach (Tracked t in tracked)
            {
                if (beforeCount[t.Key] > 1) continue;   // repeated keys (rows): compared by count below
                bool live = scene.IsLive(t.Node);
                bool same = live && string.Equals(scene.DebugKeyOf(t.Node), t.Key, StringComparison.Ordinal);
                if (same && shown.TryGetValue(t.Key, out List<NodeHandle>? mine) && mine.Contains(t.Node)) continue;
                if (shown.ContainsKey(t.Key)) remounted.Add(t);
                else vanished.Add(t);
            }
            foreach (var (key, count) in beforeCount)
            {
                if (count < 2) continue;
                int after = shown.TryGetValue(key, out List<NodeHandle>? list) ? list.Count : 0;
                if (after != count) recounted.Add((key, count, after));
            }
            var appeared = new List<string>();
            foreach (string key in shown.Keys)
                if (!beforeCount.ContainsKey(key)) appeared.Add(key);
            appeared.Sort(StringComparer.Ordinal);

            // The topmost snapped or jumped node of each subtree.
            var flagged = new HashSet<NodeHandle>();
            foreach (Tracked t in tracked)
                if (t.Motion >= Snapped) flagged.Add(t.Node);
            var topmost = new List<Tracked>();
            foreach (Tracked t in tracked)
            {
                if (t.Motion < Snapped) continue;
                bool nested = false;
                for (NodeHandle p = scene.IsLive(t.Node) ? scene.Parent(t.Node) : NodeHandle.Null; !p.IsNull && !nested; p = scene.Parent(p))
                    nested = flagged.Contains(p);
                if (!nested) topmost.Add(t);
            }
            topmost.Sort(static (x, y) => x.Commit.Y.CompareTo(y.Commit.Y));   // top of the page first

            // 6. lint the settled scene (no frame is read: contrast checks nothing here; the head names the devices it ran on)
            if (!TryRoots(Inspect.Args.Empty, shell, out List<NodeHandle> roots, out error)) return Inspect.Reply.Fail(id, name, error);
            InspectLint.Snapshot snapshot = InspectLintScene.Capture(shell, CollectionsMarshal.AsSpan(roots), pixels: null, devices: Devices);
            InspectLint.Report report = InspectLint.Run(snapshot, options);

            int glided = 0, snapped = 0, jumped = 0;
            foreach (Tracked t in tracked)
            {
                if (t.Motion == Glided) glided++;
                else if (t.Motion == Snapped) snapped++;
                else if (t.Motion == Jumped) jumped++;
            }
            bool committed = !SameTiers(tiersBefore, tiersCommit);
            bool heldOldTier = SameTiers(tiersBefore, tiersResized);
            int trackedCount = tracked.Count;

            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteStartObject("from");
                Inspect.Reply.Num(w, "w", fromW);
                Inspect.Reply.Num(w, "h", fromH);
                w.WriteEndObject();
                w.WriteStartObject("to");
                Inspect.Reply.Num(w, "w", toW);
                Inspect.Reply.Num(w, "h", toH);
                w.WriteEndObject();
                w.WriteBoolean("narrowing", narrowing);
                w.WriteNumber("resizedFrames", resizedFrames);
                WriteTierState(w, "tierBefore", in tiersBefore);
                WriteTierState(w, "tierResized", in tiersResized);
                WriteTierState(w, "tierAfter", in tiersCommit);
                w.WriteBoolean("committed", committed);
                w.WriteBoolean("heldOldTier", heldOldTier);
                w.WriteBoolean("reducedMotion", Design.Reduced);
                w.WriteStartObject("counts");
                w.WriteNumber("tracked", trackedCount);
                w.WriteNumber("changedOnCommit", changed);
                w.WriteNumber("glided", glided);
                w.WriteNumber("snapped", snapped);
                w.WriteNumber("jumped", jumped);
                w.WriteNumber("remounted", remounted.Count);
                w.WriteNumber("vanished", vanished.Count);
                w.WriteNumber("appeared", appeared.Count);
                w.WriteNumber("recounted", recounted.Count);
                w.WriteEndObject();

                w.WriteStartArray("remounted");
                foreach (Tracked t in remounted.Take(items))
                {
                    w.WriteStartObject();
                    w.WriteString("key", t.Key);
                    Inspect.Reply.Rect(w, "before", t.Before);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("vanished");
                foreach (Tracked t in vanished.Take(items))
                {
                    w.WriteStartObject();
                    w.WriteString("key", t.Key);
                    Inspect.Reply.Rect(w, "before", t.Before);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("appeared");
                foreach (string key in appeared.Take(items)) w.WriteStringValue(key);
                w.WriteEndArray();

                w.WriteStartArray("recounted");
                foreach (var (key, before, after) in recounted.Take(items))
                {
                    w.WriteStartObject();
                    w.WriteString("key", key);
                    w.WriteNumber("before", before);
                    w.WriteNumber("after", after);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("snapped");
                foreach (Tracked t in topmost.Where(static t => t.Motion == Snapped).Take(items)) WriteMotion(w, t);
                w.WriteEndArray();
                w.WriteStartArray("jumped");
                foreach (Tracked t in topmost.Where(static t => t.Motion == Jumped).Take(items)) WriteMotion(w, t);
                w.WriteEndArray();

                w.WriteStartObject("settle");
                w.WriteNumber("frames", settle.Frames);
                w.WriteBoolean("settled", settle.Settled);
                if (!settle.Settled) InspectKit.WriteBusy(w, shell, in settle);
                InspectKit.WriteAmbient(w, in settle);
                w.WriteEndObject();

                w.WriteStartObject("lint");
                InspectLintScene.WriteHead(w, snapshot);
                InspectLintScene.WriteReport(w, snapshot, report, options);
                w.WriteEndObject();
            });
        }

        /// <summary>A snapped or jumped node: its key, the rect it had on the resized frame, the rect it jumped to and the difference.</summary>
        static void WriteMotion(Utf8JsonWriter w, Tracked t)
        {
            w.WriteStartObject();
            w.WriteString("key", t.Key);
            Inspect.Reply.Rect(w, "from", t.Resized);
            Inspect.Reply.Rect(w, "to", t.Commit);
            Inspect.Reply.Num(w, "dx", t.Commit.X - t.Resized.X);
            Inspect.Reply.Num(w, "dy", t.Commit.Y - t.Resized.Y);
            Inspect.Reply.Num(w, "dw", t.Commit.W - t.Resized.W);
            Inspect.Reply.Num(w, "dh", t.Commit.H - t.Resized.H);
            w.WriteEndObject();
        }
    }
}
