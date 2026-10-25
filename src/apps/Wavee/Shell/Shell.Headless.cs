// ── Shell/Shell.Headless.cs — the real shell frame on the engine's headless host, for tests ────────────────────────
//
// The shell test host (themes plan §2.1, H0): `Boot` runs the app's own boot (`Platform.Boot` + `App.BootCore` +
// the documents prelude of `Shell.Run`) ONCE per process under `BootProfile.HeadlessFake`, and `Mount` puts the real
// `Shell.Frame()` on a headless `AppHost` — the same root the window mounts, minus the GUI process's services
// (`RootHost.RootServices`). Nothing here is a second implementation of a surface: a test that drives a mounted shell
// drives production code.
//
// PROCESS RULES. The boot leaves process statics behind (the page table, AppActions, the Modules latch, `Platform.Args`,
// the settings backing, `Platform.UiThreadId`), so the host lives in its own test process (`Wavee.ShellTests`), boots
// once, and runs every mount on the one thread that booted it: that thread is `Platform.UiThreadId`, and the DEBUG
// settings assert holds only there. One mount at a time. A mount points every cross-thread seam at its host's poster and
// puts the previous values back on dispose, so nothing posts into a disposed host.
//
// WHAT IT NEVER DOES: open the user's profile (a temporary folder, memory settings, `--fake`), reach the network (the
// fake composition, no login, no lyrics providers, and `Wire.GoOffline` refusing whatever the seed does not answer), write the log file, install crash reporting, hold the
// single-instance gate, register protocols, show a tray icon or toast, run the updater, the jump list, the audio warm or
// the memory poll.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text;
using FluentGpu.Text.Headless;

namespace Wavee;

public static partial class Shell
{
    /// <summary>Set once by <see cref="Headless.Boot"/>: the process is the shell test host.</summary>
    static bool s_headless;

    /// <summary>The shell test host's boot options.</summary>
    /// <param name="ProfileDir">The temporary profile folder (documents, the credential slot, the sidebar store). Created
    /// when missing. Never the user's profile.</param>
    /// <param name="Theme">The theme the host seeds instead of the OS theme (the GUI reads the registry).</param>
    public sealed record ShellHostOptions(string ProfileDir, ThemeKind Theme = ThemeKind.Dark);

    /// <summary>The devices a NON-TEST mount runs on (the `--inspect` server, Diagnostics.Inspect.cs): the platform window the
    /// host is built over (an offscreen surface's window, or a plain headless one), the headless model behind it (size, scale,
    /// input queue), the GPU device (the host owns and disposes it), the font system (a real DirectWrite one: true wrap and
    /// trim) and two hooks: <paramref name="Attach"/> runs once the host exists (an offscreen surface binds to it there) and
    /// <paramref name="Release"/> after the host is disposed (the caller's font system and surface go then, in that order).
    /// The shell tests never pass one: their mounts stay on the recording device and the deterministic headless font.</summary>
    public sealed record HeadlessDevices(IPlatformWindow Window, HeadlessWindow Input, IGpuDevice Device, IFontSystem Fonts,
        Action<AppHost>? Attach = null, Action? Release = null)
    {
        /// <summary>The image pipeline the host decodes through; null: the engine's deterministic fake decoder (a per-image gradient).</summary>
        public ImageCache? Images { get; init; }
    }

    /// <summary>The shell test host: boot once, then mount the real frame headlessly. UI THREAD = the booting thread.</summary>
    public static class Headless
    {
        static int s_uiThread;
        static HeadlessShell? s_mounted;

        /// <summary>The shell currently mounted, or null (UI thread).</summary>
        public static HeadlessShell? Mounted => s_mounted;

        /// <summary>Boot the app under <see cref="BootProfile.HeadlessFake"/>, on the calling thread, which becomes the UI
        /// thread of every mount. Once per process. The marks are the GUI's: `boot.platform`, `boot.entities`,
        /// `boot.playback`, `boot.pages`, `boot.documents`.</summary>
        public static void Boot(ShellHostOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (s_headless) throw new InvalidOperationException("Shell.Headless.Boot runs once per process.");
            s_headless = true;
            // the headless frame clock has its own epoch (its present QPC is frame x 16 ms, not the counter): the lyrics media clock anchors its samples in it, or a
            // playing track reads a huge negative time and no line is ever sung (the shell tests and `--inspect` both boot here)
            Lyrics.SampleClock.NowQpc = static () => FrameTime.NowQpc;
            // ...and the silent endpoint plays on it too, so the played count the position reports carry keeps the pace the caption extrapolates at
            // (on the wall clock the reports fell behind the frames and pulled the caption back a line, audit FU2 item 7). Before BootCore builds the backend.
            Playback.Audio.SilentPaceQpc = static () => FrameTime.NowQpc;
            s_uiThread = Environment.CurrentManagedThreadId;
            Directory.CreateDirectory(options.ProfileDir);
            Platform.ProfileRoot = options.ProfileDir;
            Platform.UiThreadId = s_uiThread;
            Wire.GoOffline();   // before anything can send: the seed does not answer every page a test may open
            Platform.Boot(["--fake"], BootProfile.HeadlessFake);
            Log.Event(WaveeLogLevel.Info, "app", "boot.platform", "", null, Log.SinceStartMs);
            App.BootCore(BootProfile.HeadlessFake);
            // ...and the playback host's stamps with it (BootCore set the wall clock, decision D22, for a window that keeps time while idle): the position
            // the host publishes is its last report extrapolated on this clock, and the stage caption extrapolates that on the frame clock. With the wall clock
            // here the published position ran at a different rate from the frames, so the caption stepped back a line now and then (audit FU2 item 7). A
            // headless host only: its time IS the frames it runs.
            Playback.FrameNowMs = static () => FrameTime.NowMs;
            Appearance.Seed(options.Theme);   // a fixed kind: no OS read, the token-default accent
            LoadDocuments();
            // Between mounts there is no host to post into. A post from the UI thread still runs inline (as the boot default
            // did); one from any other thread waits for the UI thread (the next mount forwards it into its host). Run inline on
            // the posting thread instead, a pool thread could run the playback drain beside the UI thread's and leave the
            // inbox's "a wake is queued" latch set for good.
            Playback.ToUi = ParkOrRun;
            Spotify.Post = ParkOrRun;
            Store.Post = ParkOrRun;
            Wavee.Palette.Post = ParkOrRun;
            Lyrics.Store.ToUi = ParkOrRun;
            Diagnostics.StoreShot.ToUi = ParkOrRun;
            s_bootDeck = Playback.Snap();
            s_bootQueueKnown = Queue.State != EdgeState.Unknown;
            s_bootPlaysEmpty = PlayLog.Entries.Count == 0 && PlayLog.Recency.Count == 0;
        }

        /// <summary>The deck, whether the queue had answered and whether the play log was empty, as the boot left them (<see cref="ResetPlayback"/>).</summary>
        static Playback.State s_bootDeck;
        static bool s_bootQueueKnown, s_bootPlaysEmpty;

        /// <summary>Put playback back where a fresh session starts: the deck as the boot left it (no row, nothing loaded), the
        /// queue as the boot left it and, on a profile that booted with none, no plays in the play log (a play stamps the recency
        /// the library panes sort on and the Up next page's history). Between mounts only. The deck is PROCESS state and a stop keeps its row on purpose (the bar
        /// holds the stopped track's face), so the shell test host calls this between FACTS: a fact that mounts after one that
        /// played a track would otherwise draw the bar of that track (its video split, its lyrics face, its heart).</summary>
        public static void ResetPlayback()
        {
            RequireUiThread();
            if (s_mounted is not null) throw new InvalidOperationException("Reset playback between mounts, not under one.");
            RunParked(static a => a());   // a pool thread's late post (the previous fact's audio chain) lands before the reset, not after it
            Playback.ResetDeckForTests(in s_bootDeck);
            if (s_bootQueueKnown) Queue.Replace(default(ReadOnlySpan<int>), default);
            else Queue.Clear();
            if (s_bootPlaysEmpty) PlayLog.Clear();
        }

        static readonly ConcurrentQueue<Action> s_parked = new();

        /// <summary>The between-mounts poster: inline on the UI thread (after anything parked), parked from any other.</summary>
        internal static void ParkOrRun(Action action)
        {
            if (Environment.CurrentManagedThreadId != s_uiThread) { s_parked.Enqueue(action); return; }
            RunParked(static a => a());
            action();
        }

        /// <summary>Hand everything parked between mounts to <paramref name="post"/> (UI thread).</summary>
        internal static void RunParked(Action<Action> post)
        {
            while (s_parked.TryDequeue(out var a)) post(a);
        }

        /// <summary>Mount the frame at <paramref name="widthDip"/> × <paramref name="heightDip"/> (DIP; the window's pixel
        /// size is that times <paramref name="scale"/>) and run its first frame. <paramref name="frameTime"/> defaults to a
        /// fixed 16 ms step, so animations advance the same way on every run. Dispose the handle before the next mount.</summary>
        public static HeadlessShell Mount(float widthDip = 1180f, float heightDip = 760f, float scale = 1f,
            IFrameTimeSource? frameTime = null)
            => Mount(content: null, widthDip, heightDip, scale, frameTime);

        /// <summary>As <see cref="Mount(float, float, float, IFrameTimeSource?)"/> with <paramref name="content"/> in place of the
        /// frame: it is built inside the same theme providers the frame gets (view registry, option store, theme facts), so a
        /// view mounted alone resolves its keys, options and facts exactly as it does in the shell. Null mounts the frame.
        /// This is the seam the shell tests' <c>ViewHarness</c> mounts a single view through.</summary>
        public static HeadlessShell Mount(Func<Element>? content, float widthDip = 1180f, float heightDip = 760f, float scale = 1f,
            IFrameTimeSource? frameTime = null)
        {
            RequireUiThread();
            if (s_mounted is not null) throw new InvalidOperationException("A headless shell is already mounted; dispose it first.");
            var shell = new HeadlessShell(widthDip, heightDip, scale, frameTime ?? new FixedFrameTimeSource(16f), content);
            s_mounted = shell;
            return shell;
        }

        /// <summary>Mount the frame on <paramref name="devices"/> (an offscreen D3D12 surface with DirectWrite, or a headless
        /// window with DirectWrite) instead of the test devices. The window in <paramref name="devices"/> already carries its
        /// size and scale. Dispose the handle before the next mount; it releases the devices after the host.</summary>
        public static HeadlessShell Mount(HeadlessDevices devices, IFrameTimeSource? frameTime = null)
        {
            ArgumentNullException.ThrowIfNull(devices);
            RequireUiThread();
            if (s_mounted is not null) throw new InvalidOperationException("A headless shell is already mounted; dispose it first.");
            var shell = new HeadlessShell(devices, frameTime ?? new FixedFrameTimeSource(16f));
            s_mounted = shell;
            return shell;
        }

        /// <summary>Put the navigation back where a fresh session starts: Home, with no back or forward step and the omnibar
        /// empty. Between mounts only. The route is PROCESS state (a remount opens on the page the last root showed, which
        /// <c>RootScopeTests</c> pins), so the shell test host calls this between FACTS: a fact that starts on the page the
        /// previous fact left would mount that page and park it in the KeepAlive on its first navigation, and the parked
        /// page's keyed nodes (its track list, its find box) would stand beside the ones the fact asks for.</summary>
        public static void ResetNavigation()
        {
            RequireUiThread();
            if (s_mounted is not null) throw new InvalidOperationException("Reset the navigation between mounts, not under one.");
            Tabs.Expand();   // a fact under a theme without tabs collapses the workspace: the next fact starts with every tab back
            var home = new Route(RouteKind.Home, Tab: Tabs.ActiveId);
            s_nav = new Nav(home);
            Motion.Value = NavTransitionKind.Neutral;
            Current.Value = home;
            CanBack.Value = false;
            CanForward.Value = false;
            SyncActiveTab(home);
            SyncOmnibar(home);
        }

        internal static void Unmounted(HeadlessShell shell)
        {
            if (ReferenceEquals(s_mounted, shell)) s_mounted = null;
        }

        internal static void RequireUiThread()
        {
            if (!s_headless) throw new InvalidOperationException("Shell.Headless.Boot has not run.");
            if (Environment.CurrentManagedThreadId != s_uiThread)
                throw new InvalidOperationException("The headless shell runs on the thread that booted it.");
        }
    }

    /// <summary>The headless root: the frame alone (<see cref="RootHost"/> without its GUI services), or a test's content
    /// inside the same theme providers.</summary>
    sealed class HeadlessRoot(Func<Element>? content) : Component
    {
        public override Element Render()
        {
            var input = UseContext(FluentGpu.Hooks.InputHooks.Current);
            UseSignalEffect(() => ApplyHitFloor(input));
            return content is null ? RootFrame() : Theming.ThemeRoot.Wrap(content);
        }
    }

    /// <summary>One mounted shell (<see cref="Headless.Mount"/>). Every member runs on the UI thread.</summary>
    public sealed class HeadlessShell : IDisposable
    {
        readonly HeadlessPlatformApp _app;
        readonly PosterSeams _seams;
        readonly Func<bool>? _hostQuiet;
        readonly List<(NodeHandle Node, string Key)> _keys = new();
        bool _disposed;

        /// <summary>The engine host: the scene (<see cref="AppHost.Scene"/>), the animation engine
        /// (<see cref="AppHost.Animation"/>), <see cref="AppHost.RunFrame"/>.</summary>
        public AppHost Host { get; }

        /// <summary>The headless window: its size, scale and input queue (<see cref="HeadlessWindow.QueueInput"/>).</summary>
        public HeadlessWindow Window { get; }

        /// <summary>The text backend this mount measures with (the headless model unless <see cref="HeadlessDevices"/> supplied
        /// another).</summary>
        public IFontSystem Fonts { get; }

        readonly Action? _release;

        internal HeadlessShell(float widthDip, float heightDip, float scale, IFrameTimeSource frameTime, Func<Element>? content = null)
            : this(TestDevices(widthDip, heightDip, scale), frameTime, content) { }

        static HeadlessDevices TestDevices(float widthDip, float heightDip, float scale)
        {
            var window = new HeadlessWindow(new WindowDesc("wavee-shell", new Size2(widthDip * scale, heightDip * scale), scale));
            return new HeadlessDevices(window, window, new HeadlessGpuDevice(), new HeadlessFontSystem(Entities.Strings));
        }

        internal HeadlessShell(HeadlessDevices devices, IFrameTimeSource frameTime, Func<Element>? content = null)
        {
            _app = new HeadlessPlatformApp();
            Window = devices.Input;
            devices.Window.Show();
            Fonts = devices.Fonts;
            _release = devices.Release;
            var strings = Entities.Strings;
            Host = new AppHost(_app, devices.Window, devices.Device, Fonts, strings, new HeadlessRoot(content), devices.Images,
                frameTime: frameTime);
            devices.Attach?.Invoke(Host);
            // Every cross-thread seam posts into THIS host, each post followed by the publish on the cadence — the GUI's
            // poster minus the early-post queue (the host exists before anything posts).
            _seams = PosterSeams.Capture();
            AppHost host = Host;
            AssignPosters(action => host.Post(PublishAfter.Wrap(action)));
            Headless.RunParked(action => host.Post(PublishAfter.Wrap(action)));   // what pool threads posted between mounts
            Host.FrameCompleted += s_frameTick;
            _hostQuiet = Residency.HostQuiet;
            Residency.HostQuiet = () => host.IsSettledForPause(NavGcPolicy.InteractionQuietMs);   // the post-navigation gen-0 collection's quiet gate
            Palette.ResetPump();   // the pump's deadline and in-flight batch belong to the previous host's clock
            Host.RunFrame();
        }

        /// <summary>Run <paramref name="count"/> frames.</summary>
        public void RunFrames(int count)
        {
            for (int i = 0; i < count; i++) Host.RunFrame();
        }

        /// <summary>Resize the window to <paramref name="widthDip"/> × <paramref name="heightDip"/> DIP at its current DPI
        /// scale; the next frame lays out in the new viewport.</summary>
        public void Resize(float widthDip, float heightDip)
        {
            float scale = Window.Scale / Math.Max(0.01f, Window.Zoom);
            Window.ClientSizePx = new Size2(widthDip * scale, heightDip * scale);
        }

        /// <summary>The first live node mounted with exactly <paramref name="key"/>, or <see cref="NodeHandle.Null"/>.</summary>
        public NodeHandle Find(string key)
        {
            RefreshKeys();
            foreach (var (node, k) in _keys)
                if (string.Equals(k, key, StringComparison.Ordinal)) return node;
            return NodeHandle.Null;
        }

        /// <summary>Every live node whose key starts with <paramref name="prefix"/>, with its key. Keys are not unique.</summary>
        public List<(NodeHandle Node, string Key)> FindPrefix(string prefix)
        {
            RefreshKeys();
            var found = new List<(NodeHandle, string)>();
            foreach (var (node, k) in _keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal)) found.Add((node, k));
            return found;
        }

        void RefreshKeys()
        {
            Headless.RequireUiThread();
            _keys.Clear();
            Host.Scene.CopyDebugKeys(_keys);
        }

        /// <summary>End the mount: stop playback a test started, drain what it posted, flush the debounced documents, put the
        /// seams back and dispose the host.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Headless.RequireUiThread();
            try
            {
                // A fake play runs the silent audio graph on its own thread and the playback ticker on a timer: stopping
                // here is what keeps either from posting after the test.
                Playback.Stop();
                for (int i = 0; i < 4; i++) Host.RunFrame();
                Session.Flush();
                History.Store.Flush();
                PlayLog.Flush();
            }
            finally
            {
                Host.FrameCompleted -= s_frameTick;
                _seams.Restore();
                Residency.HostQuiet = _hostQuiet;
                // A pool thread (the fake audio graph, a ticker) can post between the last frame and Restore: those posts sit in
                // this host's queue, which Dispose would drop. A dropped playback drain leaves its "a wake is queued" latch set
                // for the rest of the process, and every later Play / SetShuffle then waits forever. Hand them to the
                // between-mounts poster: on this (UI) thread they run now, and a pool thread's late post waits for the next mount.
                Host.HandOffPosts(Headless.ParkOrRun);
                Host.Dispose();   // unmounts the tree: the frame's cleanup ends its RootScope
                RootScope.EndMounted();   // belt: a mount never leaves a mounted scope behind, whatever the tree's cleanup did
                _app.Dispose();
                Headless.Unmounted(this);
                _release?.Invoke();   // the caller's font system and offscreen surface, after the host that used them
            }
        }
    }
}
