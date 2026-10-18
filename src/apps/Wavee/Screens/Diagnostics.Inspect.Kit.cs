// ── Screens/Diagnostics.Inspect.Kit.cs ─────────────────────────────────────────────────────────────────────────────────
// the mounted-shell helpers the `--inspect` server and the shell tests share: frame pumping (Until / Settle), the subtree
// walk, the fake single-track play, the fixture palette grader and the theme activation (Developer mode for Zune)
//
// Role: SHELL (UI thread only, on a shell booted by `Shell.Headless.Boot`)
// Spec: handoff/themes-2026-10-17/agent-inspect-plan.md lane A2 ("move the test-only helpers into the app")
//
// These were test-only (`Wavee.ShellTests/ShellHost.cs`, `ShellProbe.cs`, `ZuneHost.cs`). They live here so the inspect
// server drives the app exactly the way the facts do; the facts call these where the move was trivial. Nothing here
// asserts: every helper answers, and the caller decides what a miss means (a fact fails, the server replies ok:false).
//
// AOT: no reflection. Theme ids go through `ThemeHost.SelectForRun` (WVTH007: a `theme.*` key is minted only by theming).

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

        /// <summary>Run frames until the animation engine has no active track for two frames in a row, at most
        /// <paramref name="maxFrames"/>. Returns the frames run and whether it settled.</summary>
        public static (int Frames, bool Settled) Settle(Shell.HeadlessShell shell, int maxFrames = 600)
        {
            int quiet = 0, ran = 0;
            while (ran < maxFrames)
            {
                shell.Host.RunFrame();
                ran++;
                quiet = shell.Host.Animation.HasActive ? 0 : quiet + 1;
                if (quiet >= 2) return (ran, true);
            }
            return (ran, false);
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

        /// <summary>The centre of a node's presented rect (window DIP).</summary>
        public static Point2 CentreOf(SceneStore scene, NodeHandle node)
        {
            RectF r = scene.PresentedAbsoluteRect(node);
            return new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        }

        // ── playback ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Play one track or episode as its own context from <paramref name="fromMs"/> (the headless probe's
        /// single-track recipe: BuildContextQueue, Queue.Replace, Playback.PlayNow). False when the uri does not parse.</summary>
        /// <summary><paramref name="next"/> more seeded tracks (the fake catalogue's `tr<k+1>`, `tr<k+2>`, ... after a `spotify:track:tr<k>`) follow it in the
        /// same context, so "up next" has rows; 0 plays the track alone.</summary>
        public static bool Play(string uri, int fromMs = 0, int next = 0, bool video = false)
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
            => Palette.Filler = static (uris, _) => Task.FromResult(Array.ConvertAll(uris, static _ => (Palette.Graded?)FixtureGrade));

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
}
