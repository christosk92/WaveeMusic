// ── Screens/Diagnostics.Inspect.Kit.cs ─────────────────────────────────────────────────────────────────────────────────
// the mounted-shell helpers of the `--inspect` server: frame pumping (Until / Settle and what keeps it busy), the subtree
// walk, the visible-rect math and the pointer verbs' actionability check, the fake single-track play, the fixture palette
// grader and the theme activation (Developer mode for Zune)
//
// Role: SHELL (UI thread only, on a shell booted by `Shell.Headless.Boot`)
// Spec: handoff/themes-2026-10-17/agent-inspect-plan.md lane A2 ("move the test-only helpers into the app")
//
// These were test-only (`Wavee.ShellTests/ShellHost.cs`, `ShellProbe.cs`, `ZuneHost.cs`). They live here so the inspect
// server drives the app exactly the way the facts do; the facts call these where the move was trivial. Nothing here
// asserts: every helper answers, and the caller decides what a miss means (a fact fails, the server replies ok:false).
//
// AOT: no reflection. Theme ids go through `ThemeHost.SelectForRun` (WVTH007: a `theme.*` key is minted only by theming).

using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee.Theming;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>Helpers over a mounted <see cref="Shell.HeadlessShell"/>. UI thread.</summary>
    public static class InspectKit
    {
        // ── frames ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Run frames until <paramref name="ready"/> holds; false after <paramref name="maxFrames"/> frames.</summary>
        public static bool Until(Shell.HeadlessShell shell, Func<bool> ready, int maxFrames = 240)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (ready()) return true;
                shell.Host.RunFrame();
            }
            return ready();
        }

        /// <summary>Run frames until <paramref name="ready"/> holds, for a condition that waits on BACKGROUND work (an image
        /// decode, a palette grade): a wall-clock deadline, yielding the CPU between frames. False on the deadline.</summary>
        public static bool UntilBackground(Shell.HeadlessShell shell, Func<bool> ready, int timeoutMs = 10_000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (ready()) return true;
                shell.Host.RunFrame();
                Thread.Sleep(1);
            }
            return ready();
        }

        /// <summary>The wake reasons that never make a frame "busy": the caret's blink, the real window's post-input warm
        /// cadence, a host timer coming due, and the frame-clock subscribers that tick for as long as something plays (the
        /// playhead, a visualizer): they run forever by design, so waiting for them would never end. A settle that saw any of
        /// them says so (`ambient` on its reply): motion they drive was not waited for.</summary>
        public const FluentGpu.Hosting.WakeReasons AmbientWake = FluentGpu.Hosting.WakeReasons.Caret | FluentGpu.Hosting.WakeReasons.WarmCadence
            | FluentGpu.Hosting.WakeReasons.Timer | FluentGpu.Hosting.WakeReasons.FrameClockPoller | FluentGpu.Hosting.WakeReasons.FrameClockPaceable;

        /// <summary>What a settle saw: the frames run, whether it settled, and (from its last frame, when it did not) what still
        /// moved: live animation tracks, a scroller in motion, and the wake reasons outside <see cref="AmbientWake"/>.</summary>
        public readonly record struct SettleResult(int Frames, bool Settled, bool Tracks, bool Scrolling, FluentGpu.Hosting.WakeReasons Wake)
        {
            /// <summary>The ambient wake reasons set on any frame of the settle (ignored, but reported).</summary>
            public FluentGpu.Hosting.WakeReasons Ambient { get; init; }
            /// <summary>Quiet frames in a row at the end: a settle the cap stopped with nothing busy had fewer than 2.</summary>
            public int Quiet { get; init; }
            /// <summary>The theme epoch (Tok.Epoch) moved on the last frame: one of the FrameNeeded terms (<see cref="WriteFrameNeeded"/>).</summary>
            public bool EpochMoved { get; init; }
            /// <summary>The keyed nodes whose tracks were all LOOPS when the settle ended on them (<see cref="LoopsOnly"/>): motion that
            /// never ends by design (a marquee title, a looping indicator), not waited for. Null when the settle saw none.</summary>
            public IReadOnlyList<string>? Loops { get; init; }

            /// <summary>The settle's old shape, `var (frames, settled) = Settle(...)`.</summary>
            public void Deconstruct(out int frames, out bool settled)
            {
                frames = Frames;
                settled = Settled;
            }
        }

        /// <summary>Busy this frame: a live animation track, a scroller in user-driven motion (a wheel notch's glide, a fling),
        /// or a wake reason outside <see cref="AmbientWake"/> (pending effects, image uploads and decodes, a smooth
        /// programmatic scroll, crossfades, exit orphans...). The reasons outside the ambient set, or None.</summary>
        public static FluentGpu.Hosting.WakeReasons BusyWake(Shell.HeadlessShell shell) => shell.Host.CurrentWakeReasons & ~AmbientWake;

        /// <summary>Every live, unparked animation row is a LOOP (<c>AnimFlags.Loop</c>: a marquee's ping-pong, a looping indicator), so the
        /// tracks that keep the frame awake never end by design. The settle treats them as ambient: it does not wait for them, and the
        /// <see cref="FluentGpu.Hosting.WakeReasons.Anim"/> they raise is theirs. A row of any other kind (a FLIP, a fade, a spring) is busy
        /// as before. (A connected-animation flight also raises Anim with no row of its own; one beside a loop is not waited for.)</summary>
        public static bool LoopsOnly(Shell.HeadlessShell shell)
            => shell.Host.Animation.HasActive && shell.Host.Animation.ActiveNonLoopCount == 0;

        /// <summary>Run frames until nothing moves for two frames in a row (no animation track, no scroller in user motion, no
        /// wake reason but the ambient ones: <see cref="BusyWake"/>), at most <paramref name="maxFrames"/>. Tracks that are all loops
        /// (<see cref="LoopsOnly"/>) count as quiet, with the Anim wake they raise: the reply names their nodes (`loops`).</summary>
        public static SettleResult Settle(Shell.HeadlessShell shell, int maxFrames = 600)
        {
            int quiet = 0, ran = 0;
            bool tracks = false, scrolling = false, epochMoved = false, looping = false;
            FluentGpu.Hosting.WakeReasons wake = default, ambient = default;
            while (ran < maxFrames)
            {
                int epoch = Tok.Epoch;
                shell.Host.RunFrame();
                ran++;
                epochMoved = Tok.Epoch != epoch;
                tracks = shell.Host.Animation.HasActive;
                scrolling = shell.Host.AnyUserScrollMoving;
                FluentGpu.Hosting.WakeReasons all = shell.Host.CurrentWakeReasons;
                wake = all & ~AmbientWake;
                ambient |= all & AmbientWake;
                looping = tracks && LoopsOnly(shell);
                if (looping)
                {
                    tracks = false;
                    wake &= ~FluentGpu.Hosting.WakeReasons.Anim;
                }
                quiet = tracks || scrolling || wake != 0 ? 0 : quiet + 1;
                if (quiet >= 2)
                    return new SettleResult(ran, true, false, false, default) { Ambient = ambient, Quiet = quiet, Loops = looping ? LoopKeys(shell) : null };
            }
            return new SettleResult(ran, false, tracks, scrolling, wake) { Ambient = ambient, Quiet = quiet, EpochMoved = epochMoved, Loops = looping ? LoopKeys(shell) : null };
        }

        /// <summary>The keys of the nodes that carry live tracks (each named by its nearest keyed ancestor), read while every track is a loop.</summary>
        static List<string> LoopKeys(Shell.HeadlessShell shell, int max = 12)
        {
            SceneStore scene = shell.Host.Scene;
            var keys = new List<string>();
            foreach (NodeHandle n in Subtree(scene, scene.Root))
            {
                if (!shell.Host.Animation.HasTracks(n)) continue;
                string key = KeyOf(scene, n);
                if (!keys.Contains(key)) keys.Add(key);
                if (keys.Count >= max) break;
            }
            return keys;
        }

        /// <summary>`busy:[{key, kind}]`: what still moves after a settle that hit its cap. `anim` = a node with live tracks
        /// (named by its nearest keyed ancestor; `tracks` beside the array is the engine's census of those rows), `scroll` = a scroller in motion (`motion` = its kind), `wake` = a wake
        /// reason with no node (`reason`; FrameNeeded adds its `terms`, <see cref="WriteFrameNeeded"/>). At most
        /// <paramref name="max"/> nodes per kind. Nothing busy (the cap came before two quiet frames): `quietFrames` and a `note`.</summary>
        public static void WriteBusy(System.Text.Json.Utf8JsonWriter w, Shell.HeadlessShell shell, in SettleResult r, int max = 12)
        {
            SceneStore scene = shell.Host.Scene;
            w.WriteStartArray("busy");
            int anims = 0, scrolls = 0, wakes = 0;
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (NodeHandle n in Subtree(scene, scene.Root))
            {
                if (anims < max && r.Tracks && shell.Host.Animation.HasTracks(n) && named.Add("anim|" + KeyOf(scene, n)))
                {
                    w.WriteStartObject();
                    w.WriteString("key", KeyOf(scene, n));
                    w.WriteString("kind", "anim");
                    w.WriteEndObject();
                    anims++;
                }
                if (scrolls < max && scene.HasScroll(n) && scene.TryGetScroll(n, out ScrollState s) && s.Motion.IsMoving
                    && named.Add("scroll|" + KeyOf(scene, n)))
                {
                    w.WriteStartObject();
                    w.WriteString("key", KeyOf(scene, n));
                    w.WriteString("kind", "scroll");
                    w.WriteString("motion", s.Motion.Kind.ToString());
                    w.WriteEndObject();
                    scrolls++;
                }
            }
            foreach (FluentGpu.Hosting.WakeReasons bit in Enum.GetValues<FluentGpu.Hosting.WakeReasons>())
            {
                if (bit == 0 || (r.Wake & bit) == 0) continue;
                w.WriteStartObject();
                w.WriteString("kind", "wake");
                w.WriteString("reason", bit.ToString());
                if (bit == FluentGpu.Hosting.WakeReasons.FrameNeeded) WriteFrameNeeded(w, "terms", shell, r.EpochMoved);
                w.WriteEndObject();
                wakes++;
            }
            w.WriteEndArray();
            if (r.Tracks)
            {
                // The engine's live-row census behind the anim entries: `anim=N:Channel*k,... done=d parked=p loop=l`.
                var census = new System.Text.StringBuilder();
                shell.Host.Animation.AppendLiveTrackCensus(census);
                w.WriteString("tracks", census.ToString().TrimStart(' ', '|'));
            }
            if (anims + scrolls + wakes == 0)
            {
                w.WriteNumber("quietFrames", r.Quiet);
                w.WriteString("note", "nothing moved on the last frame, but the cap came before 2 quiet frames in a row: raise max");
            }
        }

        /// <summary>`<paramref name="name"/>:[terms]`: which FrameNeeded terms the inspector can see from outside the engine.
        /// `themeEpoch` = a theme token was written on the last frame (Tok.Epoch moved: something writes the theme every frame);
        /// `freeze` = a freeze holds presents (AppHost.IsFrozen); `engineSettle` = an engine settle is running (AppHost.IsSettling);
        /// `themeTransaction` = a theme transaction is running; `explicit` = none of those: an explicit wake, a posted action,
        /// a scroll plan write or a frame owed after paint, which the engine does not attribute.</summary>
        public static void WriteFrameNeeded(System.Text.Json.Utf8JsonWriter w, string name, Shell.HeadlessShell shell, bool epochMoved)
        {
            w.WriteStartArray(name);
            int n = 0;
            if (epochMoved) { w.WriteStringValue("themeEpoch"); n++; }
            if (shell.Host.IsFrozen) { w.WriteStringValue("freeze"); n++; }
            if (shell.Host.IsSettling) { w.WriteStringValue("engineSettle"); n++; }
            if (ThemeHost.IsTransactionRunning) { w.WriteStringValue("themeTransaction"); n++; }
            if (n == 0) w.WriteStringValue("explicit");
            w.WriteEndArray();
        }

        /// <summary>`ambient:[names]` for the ambient wake reasons a settle saw (nothing when it saw none), and `loops:[keys]` for the
        /// nodes whose looping tracks it did not wait for (<see cref="SettleResult.Loops"/>).</summary>
        public static void WriteAmbient(System.Text.Json.Utf8JsonWriter w, in SettleResult r)
        {
            if (r.Ambient != 0)
            {
                w.WriteStartArray("ambient");
                foreach (FluentGpu.Hosting.WakeReasons bit in Enum.GetValues<FluentGpu.Hosting.WakeReasons>())
                    if (bit != 0 && (r.Ambient & bit) == bit) w.WriteStringValue(bit.ToString());
                w.WriteEndArray();
            }
            if (r.Loops is { Count: > 0 } loops)
            {
                w.WriteStartArray("loops");
                foreach (string key in loops) w.WriteStringValue(key);
                w.WriteEndArray();
            }
        }

        /// <summary>`wake:[names]`: the bits of <paramref name="wake"/>, with `ambient:[names]` for those a settle ignores.</summary>
        public static void WriteWake(System.Text.Json.Utf8JsonWriter w, FluentGpu.Hosting.WakeReasons wake)
        {
            w.WriteStartArray("wake");
            foreach (FluentGpu.Hosting.WakeReasons bit in Enum.GetValues<FluentGpu.Hosting.WakeReasons>())
                if (bit != 0 && (wake & bit) == bit && (AmbientWake & bit) == 0) w.WriteStringValue(bit.ToString());
            w.WriteEndArray();
            w.WriteStartArray("ambient");
            foreach (FluentGpu.Hosting.WakeReasons bit in Enum.GetValues<FluentGpu.Hosting.WakeReasons>())
                if (bit != 0 && (wake & bit) == bit && (AmbientWake & bit) != 0) w.WriteStringValue(bit.ToString());
            w.WriteEndArray();
        }

        /// <summary>The key of <paramref name="node"/> or of its nearest keyed ancestor (what verbs accept), else its dump path.</summary>
        public static string KeyOf(SceneStore scene, NodeHandle node)
        {
            for (NodeHandle n = node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
                if (scene.DebugKeyOf(n) is { Length: > 0 } key) return key;
            return PathOf(scene, node);
        }

        // ── the tree ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The node and every live descendant, depth first, in child order.</summary>
        public static IEnumerable<NodeHandle> Subtree(SceneStore scene, NodeHandle root)
        {
            var stack = new Stack<NodeHandle>();
            stack.Push(root);
            var children = new List<NodeHandle>();
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.IsNull || !scene.IsLive(n)) continue;
                yield return n;
                children.Clear();
                for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) children.Add(c);
                for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
        }

        /// <summary>True when <paramref name="root"/> or a descendant carries live animation tracks.</summary>
        public static bool AnyTracksUnder(Shell.HeadlessShell shell, NodeHandle root)
        {
            foreach (var n in Subtree(shell.Host.Scene, root))
                if (shell.Host.Animation.HasTracks(n)) return true;
            return false;
        }

        /// <summary>The centre of a rect (window DIP).</summary>
        public static Point2 CentreOf(in RectF r) => new(r.X + r.W / 2f, r.Y + r.H / 2f);

        /// <summary>The centre of a node's presented rect (window DIP).</summary>
        public static Point2 CentreOf(SceneStore scene, NodeHandle node) => CentreOf(scene.PresentedAbsoluteRect(node));

        // ── on screen ───────────────────────────────────────────────────────────────────────────────────────────────
        // The visible part of a node is the lint's (InspectVisible.TryGetVisibleRect, Diagnostics.Inspect.Visible.cs): one helper, so
        // a target `click` refuses as off screen and a node `lint scope=screen` leaves out are judged by the same rect.

        /// <summary>The window's rect in DIP.</summary>
        public static RectF WindowRect(Shell.HeadlessShell shell)
        {
            float scale = Math.Max(0.01f, shell.Window.Scale);
            return new RectF(0f, 0f, shell.Window.ClientSizePx.Width / scale, shell.Window.ClientSizePx.Height / scale);
        }

        /// <summary>True when <paramref name="node"/> is <paramref name="root"/> or lies under it.</summary>
        public static bool IsUnder(SceneStore scene, NodeHandle node, NodeHandle root)
        {
            for (NodeHandle n = node; !n.IsNull; n = scene.Parent(n))
                if (n == root) return true;
            return false;
        }

        /// <summary>The handlers that make a node a hit target in its own right (the dispatcher's hit-anywhere set).</summary>
        const uint TakesHits = InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit | InteractionInfo.DragBit
            | InteractionInfo.CursorBit | InteractionInfo.SelectableTextBit | InteractionInfo.GestureBit | InteractionInfo.ContextBit;

        /// <summary>Whether a pointer verb may aim at <paramref name="node"/>, and where: see <see cref="Actionability"/>.
        /// <paramref name="wheel"/> (`scroll`): the wheel goes to the deepest visible node under the pointer, handler or not,
        /// so a centre over no hit target is fine there.</summary>
        public static Actionability.Verdict Assess(Shell.HeadlessShell shell, NodeHandle node, in RectF window, bool wheel = false)
        {
            SceneStore scene = shell.Host.Scene;
            RectF rect = scene.PresentedAbsoluteRect(node);
            // Shown as the engine says (live, linked to the root, visible and opaque up the chain, a box) and as the lint says (no parked,
            // exiting or transparent node up the chain); a node that is not shows nothing, wherever its rect lies. The visible rect is
            // the engine's (the painted box, transforms folded in, cut by every clip the hit test honours) cut by the window.
            bool shown = scene.IsShown(node) && InspectVisible.Shown(scene, node);
            InspectVisible.TryGetVisibleRect(scene, node, window, out RectF visible, out float fraction);
            if (!shown)
            {
                visible = default;
                fraction = 0f;
            }
            Point2 aim = CentreOf(visible.IsEmpty ? rect : visible);
            NodeHandle hit = shell.Host.Input.HitTest(aim);
            bool passive = (scene.Interaction(node).HandlerMask & TakesHits) == 0;
            // A passive target (a label, an icon) is reached through what owns it: the hit may be an ancestor or a sibling part of the
            // same click owner; or, painted over a hit target while taking no hits itself (the search pill's "ctrl f" hint over its
            // field), the press passes through it to what lies beneath, which is what a user's click on it reaches.
            Actionability.Hit relation = hit.IsNull ? Actionability.Hit.Nothing
                : IsUnder(scene, hit, node) ? Actionability.Hit.Inside
                : passive && (IsUnder(scene, node, hit) || SharesClickOwner(scene, node, hit)) ? Actionability.Hit.Ancestor
                : passive && PaintsBelow(scene, hit, node) ? Actionability.Hit.Through
                : Actionability.Hit.Other;
            // Disabled the way the dispatcher reads it: the target itself, or the click owner the press reaches (the nearest self-or-ancestor
            // of the hit, or of the target when the hit is elsewhere, carrying a click handler). A Disabled container does not disable an
            // enabled clickable inside it (the dispatcher walks past it), so an ancestor alone is not enough.
            NodeHandle owner = ClickOwner(scene, relation is Actionability.Hit.Inside or Actionability.Hit.Ancestor or Actionability.Hit.Through ? hit : node, skipDisabled: false);
            bool disabled = (scene.Flags(node) & NodeFlags.Disabled) != 0 || (!owner.IsNull && (scene.Flags(owner) & NodeFlags.Disabled) != 0);
            string? hitKey = null, note = null;
            if (!hit.IsNull)
            {
                hitKey = KeyOf(scene, hit);
                note = WhatIs(scene, hit);
                // A passive target over the overlay host's input-blocking layer (a menu's header text, a dialog's body): the press passes
                // through it to that layer, which a user's press reaches instead (a light-dismiss one closes the overlay). Covered, not reached.
                if (relation == Actionability.Hit.Through && note is not null)
                {
                    relation = Actionability.Hit.Other;
                    note += "; a press there reaches that layer instead, and a light-dismiss one closes its overlay";
                }
                // The hit's nearest key is the target's own ancestor (a part of it beside the target): name the part by its path instead.
                if (relation == Actionability.Hit.Other && note is null && KeyedSelfOrAncestor(scene, hit) is { IsNull: false } keyed && IsUnder(scene, node, keyed))
                {
                    note = "a part of '" + hitKey + "' that is not inside the target takes the press";
                    hitKey = PathOf(scene, hit);
                }
            }
            Actionability.Verdict v = Actionability.Decide(shown, fraction, disabled, relation, wheel ? Actionability.Reach.None : ReachOf(scene, node),
                aim, visible, hitKey, note);
            return wheel ? v with { Note = "" } : v;   // the wheel reaches the scroller whatever is hit: no "nothing reacts" note
        }

        /// <summary>Where the pointer handlers around <paramref name="node"/> sit: on it or an ancestor (a pointer at it is meant to
        /// reach them), only on parts of it, or nowhere (a passive target: a pointer over it reaches nothing, by design).</summary>
        static Actionability.Reach ReachOf(SceneStore scene, NodeHandle node)
        {
            for (NodeHandle a = node; !a.IsNull; a = scene.Parent(a))
                if ((scene.Interaction(a).HandlerMask & TakesHits) != 0) return Actionability.Reach.Chain;
            foreach (NodeHandle n in Subtree(scene, node))
                if ((scene.Interaction(n).HandlerMask & TakesHits) != 0) return Actionability.Reach.Parts;
            return Actionability.Reach.None;
        }

        /// <summary>True when <paramref name="below"/> paints under <paramref name="above"/>: under their nearest common ancestor,
        /// its branch comes first in child order (a later sibling paints over an earlier one). False when one holds the other, or
        /// when they share no ancestor (an overlay layer paints over the root, so a hit there is above).</summary>
        static bool PaintsBelow(SceneStore scene, NodeHandle below, NodeHandle above)
        {
            var chain = new List<NodeHandle>();
            for (NodeHandle n = above; !n.IsNull; n = scene.Parent(n)) chain.Add(n);
            NodeHandle prev = below;
            for (NodeHandle b = below; !b.IsNull; prev = b, b = scene.Parent(b))
            {
                int at = chain.IndexOf(b);
                if (at < 0) continue;
                if (b == below || at == 0) return false;
                NodeHandle branchAbove = chain[at - 1];
                for (NodeHandle c = scene.FirstChild(b); !c.IsNull; c = scene.NextSibling(c))
                {
                    if (c == prev) return true;
                    if (c == branchAbove) return false;
                }
                return false;
            }
            return false;
        }

        /// <summary>The nearest self-or-ancestor of <paramref name="node"/> carrying a click handler (the dispatcher's click owner;
        /// with <paramref name="skipDisabled"/>, past disabled ones as the dispatcher walks), or Null.</summary>
        static NodeHandle ClickOwner(SceneStore scene, NodeHandle node, bool skipDisabled)
        {
            for (NodeHandle n = node; !n.IsNull; n = scene.Parent(n))
            {
                if (skipDisabled && (scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
                if ((scene.Interaction(n).HandlerMask & InteractionInfo.ClickBit) != 0) return n;
            }
            return NodeHandle.Null;
        }

        /// <summary>A press at the hit activates what a press on the target would: both reach the same click owner.</summary>
        static bool SharesClickOwner(SceneStore scene, NodeHandle node, NodeHandle hit)
        {
            NodeHandle owner = ClickOwner(scene, node, skipDisabled: true);
            return !owner.IsNull && owner == ClickOwner(scene, hit, skipDisabled: true);
        }

        /// <summary>The nearest keyed self-or-ancestor of <paramref name="node"/>, or Null.</summary>
        static NodeHandle KeyedSelfOrAncestor(SceneStore scene, NodeHandle node)
        {
            for (NodeHandle n = node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
                if (scene.DebugKeyOf(n) is { Length: > 0 }) return n;
            return NodeHandle.Null;
        }

        /// <summary>The node's dump path (its nearest keyed ancestor and the child-index path below it).</summary>
        static string PathOf(SceneStore scene, NodeHandle node)
        {
            Span<char> path = stackalloc char[256];
            int length = NodeDescriber.Describe(scene, (int)node.Raw.Index, node.Raw.Gen, path);
            return new string(path[..length]);
        }

        /// <summary>A word on an unkeyed hit the key path does not explain: the overlay host's input-blocking layer (an open
        /// menu's light-dismiss scrim, a modal's) is an unkeyed sibling of the open layers (`overlay:<id>`). Null otherwise.</summary>
        static string? WhatIs(SceneStore scene, NodeHandle hit)
        {
            if (scene.DebugKeyOf(hit) is not null) return null;
            NodeHandle parent = scene.Parent(hit);
            if (parent.IsNull) return null;
            for (NodeHandle c = scene.FirstChild(parent); !c.IsNull; c = scene.NextSibling(c))
                if (scene.DebugKeyOf(c) is { } key && key.StartsWith("overlay:", StringComparison.Ordinal))
                    return "the input-blocking layer under the open overlay " + key;
            return null;
        }

        // ── between mounts ──────────────────────────────────────────────────────────────────────────────────────────

        static int[]? s_bootRefs;
        static QueueEdge[] s_bootRows = [];

        // The cold playlist (Entities.Fake.Cold.cs) as the boot seeded it: `state kind=cold-*` rewrites its membership and header.
        static int s_coldSlot = Table.None;
        static int[] s_coldTargets = [];
        static PlaylistTrackEdge[] s_coldPayload = [];
        static EdgeState s_coldState;
        static int s_coldTotal, s_coldCount;
        static uint s_coldKnown;

        /// <summary>A `state kind=cold-*` ran: the next mount puts the cold playlist back (<see cref="RestoreColdPlaylist"/>).</summary>
        public static bool ColdStaged { get; set; }

        /// <summary>Remember the cold playlist's membership and header as the boot seeded them. Once, right after <c>Shell.Headless.Boot</c>.</summary>
        public static void RememberColdPlaylist()
        {
            if (!EntityId.TryParse(Entities.FakeColdPlaylistUri.AsSpan(), out EntityId id)) return;
            EntityRef list = Entities.Ref(id);
            if (list.Kind != EntityKind.Playlist || list.Slot == Table.None) return;
            Scope s = Entities.Current;
            s_coldSlot = list.Slot;
            s_coldTargets = s.Edges.PlaylistTracks.Targets(list.Slot).ToArray();
            s_coldPayload = s.Edges.PlaylistTracks.Payload(list.Slot).ToArray();
            s_coldState = s.Edges.PlaylistTracks.State(list.Slot);
            s_coldTotal = s.Edges.PlaylistTracks.Total(list.Slot);
            s_coldKnown = s.Playlists.Known[list.Slot];
            s_coldCount = s.Playlists.TrackCount[list.Slot];
        }

        /// <summary>After a `state kind=cold-*`: the cold playlist's boot membership and header back in place (the cold rows it minted stay
        /// in the store, unreferenced). Nothing when no cold stage ran.</summary>
        public static void RestoreColdPlaylist()
        {
            if (!ColdStaged) return;
            ColdStaged = false;
            if (s_coldSlot == Table.None) return;
            Scope s = Entities.Current;
            s.Edges.PlaylistTracks.Replace(s_coldSlot, s_coldTargets, s_coldPayload, s_coldState, s_coldTotal);
            s.Playlists.Known[s_coldSlot] = s_coldKnown;
            s.Playlists.TrackCount[s_coldSlot] = s_coldCount;
            s.PublishAll();
        }

        /// <summary>Every shipped theme's persisted option values forgotten (<c>ThemeOptionStore.ResetOptions</c>: the store half of the
        /// Settings "Reset options" verb, which `option` writes through <c>ThemeHost.Apply</c>): the next <see cref="ActivateTheme"/>
        /// builds the theme's cells from its composition's defaults, as the boot did.</summary>
        public static void ForgetThemeOptions()
        {
            foreach (string id in App.ShippedThemeIds) ThemeOptionStore.ResetOptions(id);
        }

        /// <summary>Remember the queue as the boot left it (under `--fake`, the seeded default queue: the rows the queue panel,
        /// the stage pane and "Next up" render before anything plays). Once, right after <c>Shell.Headless.Boot</c>.</summary>
        public static void RememberBootQueue()
        {
            if (Queue.State == EdgeState.Unknown) return;
            s_bootRefs = Queue.PackedRefs.ToArray();
            s_bootRows = Queue.Rows.ToArray();
        }

        /// <summary>Playback as a fresh session starts (between mounts): <c>Shell.Headless.ResetPlayback</c> (the boot's deck, the
        /// play log emptied, a late post from the last mount run first), then the boot's queue rows back in place.</summary>
        public static void ResetPlayback()
        {
            Shell.Headless.ResetPlayback();
            if (s_bootRefs is { } refs) Queue.Replace(refs, s_bootRows);
        }

        /// <summary>The tab workspace as a fresh session has it (between mounts): every tab back (a theme without tabs parks them),
        /// then the first one active and the others closed. History opens in a tab of its own, so a mount after it would otherwise
        /// land on that second tab.</summary>
        public static void ResetTabs()
        {
            Shell.TabWorkspace tabs = Shell.Tabs;
            tabs.Expand();
            if (tabs.Count <= 1) return;
            tabs.Activate(0);
            int keep = tabs.ActiveId;
            tabs.CloseWhere(t => t.Id != keep);
        }

        // ── playback ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Play one track or episode as its own context from <paramref name="fromMs"/> (the headless probe's
        /// single-track recipe: BuildContextQueue, Queue.Replace, Playback.PlayNow). False when the uri does not parse.</summary>
        /// <summary><paramref name="next"/> more seeded tracks (the fake catalogue's `tr<k+1>`, `tr<k+2>`, ... after a `spotify:track:tr<k>`) follow it in the
        /// same context, so "up next" has rows; 0 plays the track alone. <paramref name="video"/> plays it as a video.</summary>
        public static bool Play(string uri, int fromMs = 0, int next = 0, bool video = false, int agoMin = 0)
        {
            if (!EntityId.TryParse(uri.AsSpan(), out EntityId id)) return false;
            EntityRef row = Entities.Ref(id);
            next = Math.Clamp(next, 0, 64);
            var members = new List<EntityRef>(1 + next) { row };
            const string prefix = "spotify:track:tr";
            if (next > 0 && uri.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(uri.AsSpan(prefix.Length), out int k))
                for (int i = 1; i <= next; i++)
                    if (EntityId.TryParse((prefix + (k + i).ToString(System.Globalization.CultureInfo.InvariantCulture)).AsSpan(), out EntityId more)) members.Add(Entities.Ref(more));
            Span<EntityRef> refs = new EntityRef[members.Count];
            Span<QueueEdge> rows = new QueueEdge[members.Count];
            int n = Headless.BuildContextQueue(members.ToArray(), 0, refs, rows);
            Queue.Replace(refs[..n], rows[..n]);
            Playback.PlayNow(row, id, Queue.CursorOf(0), video ? Playback.PlayableKind.Video : Playback.PlayableKind.Audio, fromMs);
            // The harness has no play report, so the play log (the Up next page's "played before this") is fed here; `agoMin` backdates the play.
            Shell.RecordPlay(id, id, DateTimeOffset.UtcNow.AddMinutes(-agoMin).ToUnixTimeMilliseconds());
            return true;
        }

        // ── the palette ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The one colour scheme every cover grades to under the fixture grader.</summary>
        public static readonly Scheme FixtureScheme = new(0xFF2E4A7Au, 0xFF3D5F99u, 0xFFFFFFFFu, 0xFFB3C2DDu, 0xFF8FB8FFu);

        /// <summary>The fixture grade (dark and light both <see cref="FixtureScheme"/>).</summary>
        public static readonly Palette.Graded FixtureGrade = new(FixtureScheme, FixtureScheme, HasLight: true, BestFitIsLight: false);

        /// <summary>The network grader (<c>Palette.Filler</c>) is null under --fake, so no cover is ever graded and no surface
        /// that colours itself from artwork would exist. The fixture grades every cover one colour from the first frame on
        /// (a cover asked while no grader exists is marked failed for good, so install it right after the boot).</summary>
        public static void InstallFixtureGrader()
            => Palette.Filler = static (uris, _) => Task.FromResult(Array.ConvertAll(uris, static uri => (Palette.Graded?)GradeOf(uri)));

        /// <summary>A grade whose hue follows the cover's uri (a stable hash), so covers of different tracks tint differently: the dark half deep and saturated,
        /// the light half pale, as the provider grades them.</summary>
        public static Palette.Graded GradeOf(string uri)
        {
            uint h = 2166136261u;
            foreach (char c in uri) h = (h ^ c) * 16777619u;
            float hue = h % 360u;
            var dark = new Scheme(Hsv(hue, 0.60f, 0.30f), Hsv(hue, 0.65f, 0.46f), 0xFFFFFFFFu, 0xFFC8C8C8u, 0xFFFFFFFFu);
            var light = new Scheme(Hsv(hue, 0.18f, 0.96f), Hsv(hue, 0.30f, 0.90f), 0xFF000000u, 0xFF404040u, 0xFF000000u);
            return new Palette.Graded(dark, light, HasLight: true, BestFitIsLight: false);
        }

        /// <summary>A hueless grade (a black-and-white cover): every role grey, dark and light.</summary>
        public static readonly Palette.Graded GreyScheme = new(
            new Scheme(0xFF3A3A3Au, 0xFF555555u, 0xFFFFFFFFu, 0xFFC8C8C8u, 0xFFFFFFFFu),
            new Scheme(0xFFEDEDEDu, 0xFFDDDDDDu, 0xFF000000u, 0xFF404040u, 0xFF000000u), HasLight: true, BestFitIsLight: false);

        static uint Hsv(float h, float s, float v)
        {
            float c = v * s, x = c * (1f - MathF.Abs(h / 60f % 2f - 1f)), m = v - c;
            (float r, float g, float b) = (int)(h / 60f) switch { 0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x) };
            return 0xFF000000u | (uint)((r + m) * 255f) << 16 | (uint)((g + m) * 255f) << 8 | (uint)((b + m) * 255f);
        }

        // ── themes ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The theme ids the app ships (what `mount theme=` and `theme id=` accept).</summary>
        public static IReadOnlyList<string> ThemeIds => App.ShippedThemeIds;

        /// <summary>The theme a fresh session starts on.</summary>
        public static string DefaultThemeId => App.ShippedThemeIds[0];

        /// <summary>True for an id the app ships (case-insensitive).</summary>
        public static bool IsShipped(string themeId)
        {
            foreach (string id in App.ShippedThemeIds)
                if (string.Equals(id, themeId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Between mounts: make <paramref name="themeId"/> the active theme the way a launch would resolve it (the
        /// id stored in this run's memory settings, the shipped themes installed as App.cs installs them, the palette seeded
        /// with its definition). A Developer-only theme (Zune) turns Developer mode on; any other turns it off.
        /// <paramref name="kind"/>, when given, seeds light or dark first (no OS read). False for an id the app does not ship.</summary>
        public static bool ActivateTheme(string themeId, ThemeKind? kind = null)
        {
            if (!IsShipped(themeId)) return false;
            if (kind is { } k) Appearance.Seed(k);
            ThemeHost.SelectForRun(themeId);   // Developer mode on + theme.id for this run (memory settings under --fake)
            App.InstallThemes();               // installs and activates the stored id (Developer mode is on, so any id resolves)
            Platform.Developer.Enabled.Value = ThemeInspection.IsDeveloperOnly(themeId);
            Design.SeedPalette(kind ?? Tok.Theme);
            return true;
        }

        /// <summary>The active theme's id.</summary>
        public static string ActiveThemeId => ThemeInspection.ActiveId;
    }

    /// <summary>Whether `click`, `hover` and `scroll key=` may aim at a node (pure: the facts in, a verdict out). A target must
    /// be shown, at least <see cref="MinVisible"/> of it visible after the window and every clipping ancestor, enabled (itself,
    /// and the click owner the press reaches), and the hit test at its visible centre must land in its own subtree (or, for a
    /// passive target such as a label inside a button, on what owns it). A centre over no hit target at all is refused when
    /// the target is meant to take the pointer (`unreachable`), and noted when nothing there takes it. `force=true` on the
    /// verb skips all of it.</summary>
    public static class Actionability
    {
        /// <summary>The share of a target that must be visible.</summary>
        public const float MinVisible = InspectVisible.MinFraction;

        /// <summary>Where the hit test at the visible centre landed, relative to the target: <see cref="Ancestor"/> also covers a
        /// sibling part of the same click owner; <see cref="Through"/> is a hit target painted beneath a passive target, which the
        /// press passes through to.</summary>
        public enum Hit : byte { Nothing, Inside, Ancestor, Through, Other }

        /// <summary>Where the pointer handlers around a target sit: <see cref="Chain"/> on it or an ancestor, <see cref="Parts"/> only
        /// on parts of it, <see cref="None"/> nowhere (a passive target).</summary>
        public enum Reach : byte { None, Chain, Parts }

        /// <summary>The verdict: <see cref="Reason"/> is empty when actionable, else a short word (`notShown`, `offscreen`,
        /// `disabled`, `covered`, `unreachable`) and <see cref="Why"/> a sentence naming the numbers and the occluder's key.
        /// <see cref="Note"/> qualifies an actionable verdict (a passive target over no hit target).</summary>
        public readonly record struct Verdict(string Reason, string Why, Point2 Aim, RectF Visible, float Fraction, string? HitKey, string Note = "")
        {
            public bool Ok => Reason.Length == 0;
        }

        /// <summary><paramref name="reach"/>: where the target's pointer handlers sit (<see cref="Reach.None"/> for the wheel, which
        /// needs none). <paramref name="hitNote"/> says what the occluder is, when its key alone does not.</summary>
        public static Verdict Decide(bool shown, float fraction, bool disabled, Hit hit, Reach reach, Point2 aim, RectF visible, string? hitKey, string? hitNote = null)
        {
            if (!shown)
                return new("notShown", "not shown (hidden, parked, transparent or no box on its chain)", aim, visible, fraction, hitKey);
            if (!InspectVisible.OnScreen(fraction))
                return new("offscreen", string.Create(CultureInfo.InvariantCulture,
                    $"only {fraction * 100f:0}% of it is visible after the window and its clipping ancestors (needs {MinVisible * 100f:0}%): scroll it into view"),
                    aim, visible, fraction, hitKey);
            if (disabled) return new("disabled", "disabled (it, or the click owner a press on it reaches)", aim, visible, fraction, hitKey);
            if (hit == Hit.Other)
                return new("covered", string.Create(CultureInfo.InvariantCulture,
                    $"its visible centre ({aim.X:0.##}, {aim.Y:0.##}) hits '{hitKey}'{(hitNote is null ? "" : " (" + hitNote + ")")}, which is not inside it"),
                    aim, visible, fraction, hitKey);
            if (hit == Hit.Nothing && reach == Reach.Chain)
                return new("unreachable", string.Create(CultureInfo.InvariantCulture,
                    $"the pointer at its visible centre ({aim.X:0.##}, {aim.Y:0.##}) reaches no node: it takes the pointer, but is not hit-testable there (hit testing off on its chain, or a sticky or collapse cut)"),
                    aim, visible, fraction, hitKey);
            if (hit == Hit.Nothing && reach == Reach.Parts)
                return new("unreachable", string.Create(CultureInfo.InvariantCulture,
                    $"nothing at its visible centre ({aim.X:0.##}, {aim.Y:0.##}) takes the pointer: only parts of it do, elsewhere (aim at their keys; find prefix= lists them)"),
                    aim, visible, fraction, hitKey);
            string note = hit switch
            {
                Hit.Nothing => "passive: nothing at its visible centre takes the pointer, so nothing reacts there",
                Hit.Through => "it takes no hits itself: the press passes through it to '" + hitKey + "' beneath, as a user's would",
                _ => "",
            };
            return new("", "", aim, visible, fraction, hitKey, note);
        }

        /// <summary>Reading order: top first (to the whole DIP), then left.</summary>
        public static int ReadingOrder(in RectF a, in RectF b)
        {
            int byTop = MathF.Round(a.Y).CompareTo(MathF.Round(b.Y));
            return byTop != 0 ? byTop : a.X.CompareTo(b.X);
        }
    }
}
