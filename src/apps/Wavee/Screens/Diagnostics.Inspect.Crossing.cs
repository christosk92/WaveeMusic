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
// THE CONTROL (`control=true`, the default). A same-tier step already moves some parts a frame late, so before the crossing the verb steps
// the same distance (or 16 DIP when that leaves the old tier) away from the edge, settles, and steps back to `from` the way the crossing
// will step on: what that plain resize snaps or jumps is the control. An edge move of a key the control moved as far (within
// ControlTolerance) is the plain resize's own; every other is EXCESS (`counts.excessSnapped`, `excessJumped`): what the edge adds.
//
// WHAT IT REPORTS.
//   remounted  a keyed node (unique before) that is a different node after the settle: its subtree and state were thrown away. `family` names
//              the swap it sits under (owner rule "a tier crossing keeps its parts": a genuinely different tree swaps keyed per family under
//              the theme's opacity-only cross-fade, SwapFade): the key of the nearest node this step mounted whose registered enter and exit
//              are that cross-fade; null when none is, a rebuilt part (`counts.remountedOutside`, what the gate fails on).
//   vanished   a keyed node that is gone (or no longer shown) after the settle; tiers that drop a part on purpose show up here.
//   appeared   keys shown after the settle that were not before.
//   snapped    nodes whose laid-out rect changed ON THE COMMIT FRAME (against the resized frame, so plain window resizing is not counted) with
//              no animation to cover it: no track on the node or any ancestor, and the presented rect already equal to the laid-out one.
//   jumped     the snapped nodes that only MOVED (same size): the content below something that changed height, shifting at once.
//   unpainted  changed at once, but neither the node nor anything under it that moved with it draws (a container whose children glide): no
//              visible move, so neither snapped nor jumped.
//   committed  the window or height tier or a rung changed by the commit frame; the CONTENT tier is reported apart, in `contentCommitted`
//              (where it first left its band: resized / commit / settle / none) and `tierSettled` (the tiers after the settle).
// The lists hold only the topmost snapped or jumped node of a subtree, with `under` (the flagged nodes under it, itself included) and
// `underExcess`; `counts` has the totals: read the counts, never the length of a list. Under reduced motion nothing glides, so everything
// that changed reads as snapped (`reducedMotion` says so). `inspect.py --gate` fails a crossing on excess over --max-moved (default 0) and on
// any remount outside a swap family.

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

            /// <summary>0 unchanged on the commit frame, 1 glided, 2 snapped, 3 jumped, 4 changed at once but draws nothing of its own (a
            /// container: what a person sees of it is its children, each judged on its own).</summary>
            public int Motion;

            /// <summary>Snapped or jumped: a visible move with no animation.</summary>
            public bool Flagged => Motion is Snapped or Jumped;
        }

        const int Glided = 1, Snapped = 2, Jumped = 3, Unpainted = 4;

        /// <summary>A node under <paramref name="node"/> that draws something of its own changed its rect since <paramref name="resized"/> with no
        /// animation covering it (no track on it or between it and <paramref name="node"/>, its presented rect at its laid-out one): what makes a
        /// container that paints nothing visibly move.</summary>
        static bool PaintedMoveUnder(Shell.HeadlessShell shell, NodeHandle node, Dictionary<NodeHandle, RectF> resized)
        {
            SceneStore scene = shell.Host.Scene;
            var stack = new Stack<NodeHandle>();
            for (NodeHandle c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
            while (stack.Count > 0)
            {
                NodeHandle n = stack.Pop();
                if (!scene.IsLive(n) || !scene.IsShown(n) || shell.Host.Animation.HasTracks(n)) continue;
                RectF now = scene.AbsoluteRect(n);
                if (!SameRect(scene.PresentedAbsoluteRect(n), now)) continue;   // easing toward its laid-out rect: covered
                if (resized.TryGetValue(n, out RectF was) && !SameRect(was, now) && PaintsItself(scene, n)) return true;
                for (NodeHandle c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
            }
            return false;
        }

        /// <summary>The node draws something of its own: text, an image or a drawn visual, a visible fill or border, a gradient, an arc, a
        /// gradient border, a material or a shadow. A box that draws none of these cannot be seen to move; its children can.</summary>
        static bool PaintsItself(SceneStore scene, NodeHandle n)
        {
            ref NodePaint p = ref scene.Paint(n);
            if (p.VisualKind is not (VisualKind.None or VisualKind.Box)) return true;
            if (p.Fill.A > 0.01f || (p.BorderWidth > 0f && p.BorderColor.A > 0.01f)) return true;
            return scene.TryGetGradient(n, out _) || scene.TryGetArc(n, out _) || scene.TryGetBorderBrush(n, out _)
                || scene.TryGetAcrylic(n, out _) || scene.TryGetShadow(n, out _);
        }

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

        /// <summary>Where the CONTENT tier first left its starting band (`committed` and `heldOldTier` compare the window and height tiers and
        /// the rungs only): `resized` (already in the resized frames: it did not hold), `commit` (on the commit frame, as a drag commits),
        /// `settle` (on a frame of the settle after the commit frame), or `none`.</summary>
        static string ContentCommitted(in TierFacts before, in TierFacts resized, in TierFacts commit, in TierFacts settled)
            => resized.Content != before.Content ? "resized"
             : commit.Content != before.Content ? "commit"
             : settled.Content != before.Content ? "settle"
             : "none";

        /// <summary>The commit frame and the settle of one step from where the window stands to the new size, the way a user's drag ends it (see
        /// the file header), with every tracked node classified on the commit frame.</summary>
        sealed class StepResult
        {
            public TierFacts Resized, Commit, Settled;
            public int Changed;
            public InspectKit.SettleResult Settle;
        }

        StepResult Step(Shell.HeadlessShell shell, List<Tracked> tracked, float toW, float toH, bool narrowing, int resizedFrames, int max)
        {
            SceneStore scene = shell.Host.Scene;
            var r = new StepResult();
            bool wasComposited = shell.Window.Composited;
            shell.Window.Composited = true;
            try
            {
                // the resized frames: the old tier, laid out at the new size
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
                r.Resized = ThemeHost.Facts.Peek().Tiers;
                foreach (Tracked t in tracked)
                {
                    if (!scene.IsLive(t.Node)) continue;
                    t.ResizedLive = true;
                    t.Resized = scene.AbsoluteRect(t.Node);
                }
                // every shown node's rect on the resized frame: what an unpainted keyed container's descendants moved by
                var resizedAll = new Dictionary<NodeHandle, RectF>();
                foreach (NodeHandle n in InspectKit.Subtree(scene, scene.Root))
                    if (scene.IsShown(n)) resizedAll[n] = scene.AbsoluteRect(n);

                // the commit frame: the new tier lands here
                if (narrowing) KeepAlivePaint();
                else RunFrames(1);
                r.Commit = ThemeHost.Facts.Peek().Tiers;
                foreach (Tracked t in tracked)
                {
                    if (!t.ResizedLive || !scene.IsLive(t.Node) || !scene.IsShown(t.Node)) continue;
                    RectF now = scene.AbsoluteRect(t.Node);
                    t.Commit = now;
                    if (SameRect(t.Resized, now)) continue;
                    r.Changed++;
                    bool presenting = !SameRect(scene.PresentedAbsoluteRect(t.Node), now);   // still easing toward the laid-out rect
                    if (presenting || TracksUp(shell, t.Node)) { t.Motion = Glided; continue; }
                    bool onlyMoved = MathF.Abs(now.W - t.Resized.W) <= 0.5f && MathF.Abs(now.H - t.Resized.H) <= 0.5f;
                    t.Motion = !PaintsItself(scene, t.Node) && !PaintedMoveUnder(shell, t.Node, resizedAll) ? Unpainted : onlyMoved ? Jumped : Snapped;
                }

                r.Settle = Settle(max);
                r.Settled = ThemeHost.Facts.Peek().Tiers;
            }
            finally
            {
                shell.Window.InModalLoop = false;
                shell.Window.SizedInModalLoop = false;
                shell.Window.Composited = wasComposited;
            }
            return r;
        }

        /// <summary>How far a snapped or jumped node moved on the commit frame: the largest of |dx|, |dy|, |dw|, |dh|.</summary>
        static float Magnitude(Tracked t)
            => MathF.Max(MathF.Max(MathF.Abs(t.Commit.X - t.Resized.X), MathF.Abs(t.Commit.Y - t.Resized.Y)),
                         MathF.Max(MathF.Abs(t.Commit.W - t.Resized.W), MathF.Abs(t.Commit.H - t.Resized.H)));

        /// <summary>A move at the edge is the control's own when the control moved the same key at most this much less (DIP): a plain step of
        /// the same size moves a frame-late part by the same amount.</summary>
        public const float ControlTolerance = 1f;

        static bool SameAllTiers(in TierFacts a, in TierFacts b) => SameTiers(a, b) && a.Content == b.Content;

        /// <summary>The control: what a plain resize of the crossing's own step moves INSIDE the old tier (a same-tier step already moves some
        /// parts a frame late). From the from-size it steps away from the edge by the crossing's distance (or 16 DIP when that leaves the old
        /// tier), settles, then steps back to the from-size the way the crossing will step on, and records the snapped and jumped nodes by key with
        /// how far each moved. Null with a note when no same-tier step exists; the window is back at the from-size either way.</summary>
        Dictionary<string, List<float>>? Control(Shell.HeadlessShell shell, float fromW, float fromH, float toW, float toH, int resizedFrames, int max,
            out float ctlW, out float ctlH, out int ctlSnapped, out int ctlJumped, out string note)
        {
            ctlW = ctlH = 0f;
            ctlSnapped = ctlJumped = 0;
            note = "";
            TierFacts atFrom = ThemeHost.Facts.Peek().Tiers;
            float dw = fromW - toW, dh = fromH - toH;
            Span<(float W, float H)> tries = [(fromW + dw, fromH + dh), (fromW + 16f * MathF.Sign(dw), fromH + 16f * MathF.Sign(dh))];
            bool narrowing = toW < fromW || toH < fromH;
            for (int i = 0; i < tries.Length; i++)
            {
                var (w, h) = tries[i];
                if (w < 1f || h < 1f || (i == 1 && MathF.Abs(tries[0].W - w) < 0.5f && MathF.Abs(tries[0].H - h) < 0.5f)) continue;
                shell.Resize(w, h);
                Settle(max);
                if (!SameAllTiers(atFrom, ThemeHost.Facts.Peek().Tiers)) continue;
                List<Tracked> tracked = TrackKeyed(shell);
                StepResult step = Step(shell, tracked, fromW, fromH, narrowing, resizedFrames, max);
                if (!SameAllTiers(atFrom, step.Settled)) note = "the control's step back to the from-size left the old tier: its counts may not be a same-tier step";
                ctlW = w;
                ctlH = h;
                var moved = new Dictionary<string, List<float>>(StringComparer.Ordinal);
                foreach (Tracked t in tracked)
                {
                    if (!t.Flagged) continue;
                    if (t.Motion == Snapped) ctlSnapped++; else ctlJumped++;
                    if (!moved.TryGetValue(t.Key, out List<float>? list)) moved[t.Key] = list = [];
                    list.Add(Magnitude(t));
                }
                foreach (List<float> list in moved.Values) list.Sort(static (x, y) => y.CompareTo(x));   // largest first
                return moved;
            }
            shell.Resize(fromW, fromH);
            Settle(max);
            note = "no same-tier step before the from-size (the crossing's distance and 16 DIP both leave the old tier): every move is excess";
            return null;
        }

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
            bool control = a.Bool("control", true);

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

            // 2. the control: a same-tier step of the same size, ending at the from-size
            float ctlW = 0f, ctlH = 0f;
            int ctlSnapped = 0, ctlJumped = 0;
            string controlNote = "";
            Dictionary<string, List<float>>? controlMoved = control
                ? Control(shell, fromW, fromH, toW, toH, resizedFrames, max, out ctlW, out ctlH, out ctlSnapped, out ctlJumped, out controlNote)
                : null;

            SceneStore scene = shell.Host.Scene;
            List<Tracked> tracked = TrackKeyed(shell);
            var before = new HashSet<NodeHandle>(InspectKit.Subtree(scene, scene.Root));
            TierFacts tiersBefore = ThemeHost.Facts.Peek().Tiers;

            // 3. the crossing: the resized frames, the commit frame, the settle
            bool narrowing = toW < fromW || toH < fromH;
            StepResult step = Step(shell, tracked, toW, toH, narrowing, resizedFrames, max);
            TierFacts tiersResized = step.Resized, tiersCommit = step.Commit, tiersSettled = step.Settled;
            InspectKit.SettleResult settle = step.Settle;
            int changed = step.Changed;

            // 4. identity after the settle
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
            var remounted = new List<(Tracked T, string? Family)>();
            var vanished = new List<Tracked>();
            var recounted = new List<(string Key, int Before, int After)>();
            foreach (Tracked t in tracked)
            {
                if (beforeCount[t.Key] > 1) continue;   // repeated keys (rows): compared by count below
                bool live = scene.IsLive(t.Node);
                bool same = live && string.Equals(scene.DebugKeyOf(t.Node), t.Key, StringComparison.Ordinal);
                if (same && shown.TryGetValue(t.Key, out List<NodeHandle>? mine) && mine.Contains(t.Node)) continue;
                if (shown.TryGetValue(t.Key, out List<NodeHandle>? now)) remounted.Add((t, SwapFamily(shell, now[0], before)));
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

            // 5. the excess over the control: an edge move the control made too (the same key, as far within the tolerance; each control move
            // explains one edge move, largest with largest) is the plain resize's own; every other is the edge's
            var spent = new Dictionary<string, int>(StringComparer.Ordinal);
            var excess = new HashSet<Tracked>();
            foreach (Tracked t in tracked.Where(static t => t.Flagged).OrderByDescending(Magnitude))
            {
                if (controlMoved is not null && controlMoved.TryGetValue(t.Key, out List<float>? mags))
                {
                    int used = spent.GetValueOrDefault(t.Key);
                    if (used < mags.Count && mags[used] + ControlTolerance >= Magnitude(t)) { spent[t.Key] = used + 1; continue; }
                }
                excess.Add(t);
            }

            // The topmost snapped or jumped node of each subtree, with how many flagged nodes sit under it (itself included).
            var flagged = new Dictionary<NodeHandle, Tracked>();
            foreach (Tracked t in tracked)
                if (t.Flagged) flagged[t.Node] = t;
            var under = new Dictionary<Tracked, (int All, int Excess)>();
            foreach (Tracked t in tracked)
            {
                if (!t.Flagged) continue;
                Tracked top = t;
                for (NodeHandle p = scene.IsLive(t.Node) ? scene.Parent(t.Node) : NodeHandle.Null; !p.IsNull; p = scene.Parent(p))
                    if (flagged.TryGetValue(p, out Tracked? up)) top = up;
                var (all, ex) = under.GetValueOrDefault(top);
                under[top] = (all + 1, ex + (excess.Contains(t) ? 1 : 0));
            }
            var topmost = under.Keys.ToList();
            topmost.Sort(static (x, y) => x.Commit.Y.CompareTo(y.Commit.Y));   // top of the page first

            // 6. lint the settled scene (no frame is read: contrast checks nothing here; the head names the devices it ran on)
            if (!TryRoots(Inspect.Args.Empty, shell, out List<NodeHandle> roots, out error)) return Inspect.Reply.Fail(id, name, error);
            InspectLint.Snapshot snapshot = InspectLintScene.Capture(shell, CollectionsMarshal.AsSpan(roots), pixels: null, devices: Devices) with { StagedOverlays = StagedOverlays(shell) };
            InspectLint.Report report = InspectLint.Run(snapshot, options);

            int glided = 0, snapped = 0, jumped = 0, excessSnapped = 0, excessJumped = 0, unpainted = 0;
            foreach (Tracked t in tracked)
            {
                if (t.Motion == Glided) glided++;
                else if (t.Motion == Unpainted) unpainted++;
                else if (t.Motion == Snapped) { snapped++; if (excess.Contains(t)) excessSnapped++; }
                else if (t.Motion == Jumped) { jumped++; if (excess.Contains(t)) excessJumped++; }
            }
            int swapped = remounted.Count(static r => r.Family is not null);
            bool committed = !SameTiers(tiersBefore, tiersCommit);
            bool heldOldTier = SameTiers(tiersBefore, tiersResized);
            string contentCommitted = ContentCommitted(in tiersBefore, in tiersResized, in tiersCommit, in tiersSettled);
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
                WriteTierState(w, "tierSettled", in tiersSettled);
                w.WriteBoolean("committed", committed);
                w.WriteBoolean("heldOldTier", heldOldTier);
                w.WriteString("contentCommitted", contentCommitted);
                w.WriteBoolean("reducedMotion", Design.Reduced);
                if (!control) w.WriteString("control", "off");
                else if (controlMoved is null) w.WriteNull("control");
                else
                {
                    w.WriteStartObject("control");
                    w.WriteStartObject("from");
                    Inspect.Reply.Num(w, "w", ctlW);
                    Inspect.Reply.Num(w, "h", ctlH);
                    w.WriteEndObject();
                    w.WriteNumber("snapped", ctlSnapped);
                    w.WriteNumber("jumped", ctlJumped);
                    w.WriteEndObject();
                }
                if (controlNote.Length > 0) w.WriteString("controlNote", controlNote);
                w.WriteStartObject("counts");
                w.WriteNumber("tracked", trackedCount);
                w.WriteNumber("changedOnCommit", changed);
                w.WriteNumber("glided", glided);
                w.WriteNumber("snapped", snapped);
                w.WriteNumber("jumped", jumped);
                w.WriteNumber("excessSnapped", excessSnapped);
                w.WriteNumber("excessJumped", excessJumped);
                w.WriteNumber("unpainted", unpainted);
                w.WriteNumber("remounted", remounted.Count);
                w.WriteNumber("remountedSwap", swapped);
                w.WriteNumber("remountedOutside", remounted.Count - swapped);
                w.WriteNumber("vanished", vanished.Count);
                w.WriteNumber("appeared", appeared.Count);
                w.WriteNumber("recounted", recounted.Count);
                w.WriteEndObject();

                // outside a swap family first: those are what fail the gate
                w.WriteStartArray("remounted");
                foreach (var (t, family) in remounted.OrderBy(static r => r.Family is null ? 0 : 1).Take(items))
                {
                    w.WriteStartObject();
                    w.WriteString("key", t.Key);
                    if (family is null) w.WriteNull("family");
                    else w.WriteString("family", family);
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
                foreach (Tracked t in topmost.Where(static t => t.Motion == Snapped).Take(items)) WriteMotion(w, t, under[t], excess.Contains(t));
                w.WriteEndArray();
                w.WriteStartArray("jumped");
                foreach (Tracked t in topmost.Where(static t => t.Motion == Jumped).Take(items)) WriteMotion(w, t, under[t], excess.Contains(t));
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

        /// <summary>An enter or exit terminal is a swap cross-fade: opacity only (no offset, scale or blur; a stagger delay aside). Every theme
        /// declares its tier swap so (`ClassicTierMotion.SwapFade`, `ZuneMotion.SwapFade`, `WinUiDiscoveryMotion.SwapFade`,
        /// `WinUiScreenMotion.SwapFade`: `new EnterExit(Opacity: 0f, Active: true)`); core code may not name a theme's types (WVTH001), so the
        /// inspector reads the swap off the node's registered transition by that shape. A part under it fades, it never pops.</summary>
        static bool IsSwapFade(in EnterExit e)
            => e.Active && e.Opacity < 1f && e.Dx == 0f && e.Dy == 0f && e.Sx == 1f && e.Sy == 1f && e.Blur == 0f;

        /// <summary>A node the reconciler registered with the swap cross-fade on both its entry and its exit (`Enter = SwapFade, Exit = SwapFade`).</summary>
        static bool SwapsOut(Shell.HeadlessShell shell, NodeHandle n)
            => shell.Host.Animation.TryGetTransition(n, out LayoutTransition t) && IsSwapFade(t.Enter) && IsSwapFade(t.Exit);

        /// <summary>The swap family a remounted node belongs to: the key of the nearest node at or above it that this step mounted (not in
        /// <paramref name="before"/>) and that swaps under the theme's cross-fade; null when none does (the remount rebuilt a part that should
        /// have kept its node). The walk stops at the first node that was already there.</summary>
        static string? SwapFamily(Shell.HeadlessShell shell, NodeHandle node, HashSet<NodeHandle> before)
        {
            SceneStore scene = shell.Host.Scene;
            for (NodeHandle n = node; !n.IsNull && scene.IsLive(n) && !before.Contains(n); n = scene.Parent(n))
                if (SwapsOut(shell, n)) return InspectKit.KeyOf(scene, n);
            return null;
        }

        /// <summary>A snapped or jumped node (the topmost of its subtree): its key, how many flagged nodes sit under it (itself included) and how
        /// many of those are excess, whether it is excess itself, the rect it had on the resized frame, the rect it jumped to and the difference.</summary>
        static void WriteMotion(Utf8JsonWriter w, Tracked t, (int All, int Excess) under, bool excess)
        {
            w.WriteStartObject();
            w.WriteString("key", t.Key);
            w.WriteNumber("under", under.All);
            w.WriteNumber("underExcess", under.Excess);
            w.WriteBoolean("excess", excess);
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
