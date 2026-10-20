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
        /// playhead, a visualizer): they run forever by design, so waiting for them would never end.</summary>
        public const FluentGpu.Hosting.WakeReasons AmbientWake = FluentGpu.Hosting.WakeReasons.Caret | FluentGpu.Hosting.WakeReasons.WarmCadence
            | FluentGpu.Hosting.WakeReasons.Timer | FluentGpu.Hosting.WakeReasons.FrameClockPoller | FluentGpu.Hosting.WakeReasons.FrameClockPaceable;

        /// <summary>What a settle saw on its last frame: the frames run, whether it settled, and (when it did not) what still
        /// moved: live animation tracks, a scroller in motion, and the wake reasons outside <see cref="AmbientWake"/>.</summary>
        public readonly record struct SettleResult(int Frames, bool Settled, bool Tracks, bool Scrolling, FluentGpu.Hosting.WakeReasons Wake);

        /// <summary>Busy this frame: a live animation track, a scroller in user-driven motion (a wheel notch's glide, a fling),
        /// or a wake reason outside <see cref="AmbientWake"/> (pending effects, image uploads and decodes, a smooth
        /// programmatic scroll, crossfades, exit orphans...). The reasons outside the ambient set, or None.</summary>
        public static FluentGpu.Hosting.WakeReasons BusyWake(Shell.HeadlessShell shell) => shell.Host.CurrentWakeReasons & ~AmbientWake;

        /// <summary>Run frames until nothing moves for two frames in a row (no animation track, no scroller in user motion, no
        /// wake reason but the ambient ones: <see cref="BusyWake"/>), at most <paramref name="maxFrames"/>.</summary>
        public static SettleResult Settle(Shell.HeadlessShell shell, int maxFrames = 600)
        {
            int quiet = 0, ran = 0;
            bool tracks = false, scrolling = false;
            FluentGpu.Hosting.WakeReasons wake = default;
            while (ran < maxFrames)
            {
                shell.Host.RunFrame();
                ran++;
                tracks = shell.Host.Animation.HasActive;
                scrolling = shell.Host.AnyUserScrollMoving;
                wake = BusyWake(shell);
                quiet = tracks || scrolling || wake != 0 ? 0 : quiet + 1;
                if (quiet >= 2) return new SettleResult(ran, true, false, false, default);
            }
            return new SettleResult(ran, false, tracks, scrolling, wake);
        }

        /// <summary>`busy:[{key, kind}]`: what still moves after a settle that hit its cap. `anim` = a node with live tracks
        /// (named by its nearest keyed ancestor), `scroll` = a scroller in motion (`motion` = its kind), `wake` = a wake
        /// reason with no node (`reason`). At most <paramref name="max"/> nodes per kind.</summary>
        public static void WriteBusy(System.Text.Json.Utf8JsonWriter w, Shell.HeadlessShell shell, in SettleResult r, int max = 12)
        {
            SceneStore scene = shell.Host.Scene;
            w.WriteStartArray("busy");
            int anims = 0, scrolls = 0;
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
                w.WriteEndObject();
            }
            w.WriteEndArray();
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
            Span<char> path = stackalloc char[256];
            int length = NodeDescriber.Describe(scene, (int)node.Raw.Index, node.Raw.Gen, path);
            return new string(path[..length]);
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
        // The visible part of a node: its presented rect cut by the window, every ancestor that clips (ClipsToBounds, a scroll
        // viewport) and every ancestor's authored clip rect (node-local, NodePaint.ClipRect). Lint has its own copy of this math
        // (lane i3); the two are meant to become one.

        /// <summary>The part of <paramref name="node"/>'s presented rect that can be seen (empty when none), in window DIP.</summary>
        public static RectF VisibleRect(SceneStore scene, NodeHandle node, in RectF window)
        {
            RectF r = scene.PresentedAbsoluteRect(node).Intersect(window);
            for (NodeHandle a = scene.Parent(node); !a.IsNull && !r.IsEmpty; a = scene.Parent(a))
            {
                RectF box = scene.PresentedAbsoluteRect(a);
                if ((scene.Flags(a) & NodeFlags.ClipsToBounds) != 0 || scene.HasScroll(a)) r = r.Intersect(box);
                RectF clip = scene.Paint(a).ClipRect;
                if (!clip.IsInfinite) r = r.Intersect(new RectF(box.X + clip.X, box.Y + clip.Y, clip.W, clip.H));
            }
            return r;
        }

        /// <summary>The share of <paramref name="full"/> that <paramref name="visible"/> covers, 0..1 (0 for an empty box).</summary>
        public static float Fraction(in RectF visible, in RectF full)
            => full.W > 0f && full.H > 0f && !visible.IsEmpty ? Math.Clamp(visible.W * visible.H / (full.W * full.H), 0f, 1f) : 0f;

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

        /// <summary>Whether a pointer verb may aim at <paramref name="node"/>, and where: see <see cref="Actionability"/>.</summary>
        public static Actionability.Verdict Assess(Shell.HeadlessShell shell, NodeHandle node, in RectF window)
        {
            SceneStore scene = shell.Host.Scene;
            RectF rect = scene.PresentedAbsoluteRect(node);
            bool shown = scene.IsShown(node);
            RectF visible = shown ? VisibleRect(scene, node, window) : default;   // a parked or hidden node shows nothing, wherever its rect lies
            bool disabled = false;
            for (NodeHandle n = node; !n.IsNull && !disabled; n = scene.Parent(n)) disabled = (scene.Flags(n) & NodeFlags.Disabled) != 0;
            Point2 aim = CentreOf(visible.IsEmpty ? rect : visible);
            NodeHandle hit = shell.Host.Input.HitTest(aim);
            Actionability.Hit relation = hit.IsNull ? Actionability.Hit.Nothing
                : IsUnder(scene, hit, node) ? Actionability.Hit.Inside
                : IsUnder(scene, node, hit) && (scene.Interaction(node).HandlerMask & TakesHits) == 0 ? Actionability.Hit.Ancestor
                : Actionability.Hit.Other;
            return Actionability.Decide(shown, Fraction(visible, rect), disabled, relation, aim, visible,
                hit.IsNull ? null : KeyOf(scene, hit), hit.IsNull ? null : WhatIs(scene, hit));
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
    /// be shown, at least <see cref="MinVisible"/> of it visible after the window and every clipping ancestor, enabled, and
    /// the hit test at its visible centre must land in its own subtree (or on an ancestor, when the target is a passive part of
    /// it: a label inside a button). `force=true` on the verb skips all of it.</summary>
    public static class Actionability
    {
        /// <summary>The share of a target that must be visible.</summary>
        public const float MinVisible = 0.5f;

        /// <summary>Where the hit test at the visible centre landed, relative to the target.</summary>
        public enum Hit : byte { Nothing, Inside, Ancestor, Other }

        /// <summary>The verdict: <see cref="Reason"/> is empty when actionable, else a short word (`notShown`, `offscreen`,
        /// `disabled`, `covered`) and <see cref="Why"/> a sentence naming the numbers and the occluder's key.</summary>
        public readonly record struct Verdict(string Reason, string Why, Point2 Aim, RectF Visible, float Fraction, string? HitKey)
        {
            public bool Ok => Reason.Length == 0;
        }

        /// <summary><paramref name="hitNote"/> says what an unkeyed occluder is, when the key path alone does not.</summary>
        public static Verdict Decide(bool shown, float fraction, bool disabled, Hit hit, Point2 aim, RectF visible, string? hitKey, string? hitNote = null)
        {
            if (!shown)
                return new("notShown", "not shown (hidden, parked, transparent or no box on its chain)", aim, visible, fraction, hitKey);
            if (fraction < MinVisible)
                return new("offscreen", string.Create(CultureInfo.InvariantCulture,
                    $"only {fraction * 100f:0}% of it is visible after the window and its clipping ancestors (needs {MinVisible * 100f:0}%): scroll it into view"),
                    aim, visible, fraction, hitKey);
            if (disabled) return new("disabled", "disabled (it or an ancestor)", aim, visible, fraction, hitKey);
            if (hit == Hit.Other)
                return new("covered", string.Create(CultureInfo.InvariantCulture,
                    $"its visible centre ({aim.X:0.##}, {aim.Y:0.##}) hits '{hitKey}'{(hitNote is null ? "" : " (" + hitNote + ")")}, which is not inside it"),
                    aim, visible, fraction, hitKey);
            return new("", "", aim, visible, fraction, hitKey);
        }

        /// <summary>Reading order: top first (to the whole DIP), then left.</summary>
        public static int ReadingOrder(in RectF a, in RectF b)
        {
            int byTop = MathF.Round(a.Y).CompareTo(MathF.Round(b.Y));
            return byTop != 0 ? byTop : a.X.CompareTo(b.X);
        }
    }
}
