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
using System.Collections.Generic;
using System.IO;
using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
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

    /// <summary>The shell test host: boot once, then mount the real frame headlessly. UI THREAD = the booting thread.</summary>
    public static class Headless
    {
        static int s_uiThread;
        static HeadlessShell? s_mounted;

        /// <summary>True once <see cref="Boot"/> ran in this process.</summary>
        public static bool Booted => s_headless;

        /// <summary>Boot the app under <see cref="BootProfile.HeadlessFake"/>, on the calling thread, which becomes the UI
        /// thread of every mount. Once per process. The marks are the GUI's: `boot.platform`, `boot.entities`,
        /// `boot.playback`, `boot.pages`, `boot.documents`.</summary>
        public static void Boot(ShellHostOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (s_headless) throw new InvalidOperationException("Shell.Headless.Boot runs once per process.");
            s_headless = true;
            s_uiThread = Environment.CurrentManagedThreadId;
            Directory.CreateDirectory(options.ProfileDir);
            Platform.ProfileRoot = options.ProfileDir;
            Platform.UiThreadId = s_uiThread;
            Wire.GoOffline();   // before anything can send: the seed does not answer every page a test may open
            Platform.Boot(["--fake"], BootProfile.HeadlessFake);
            Log.Event(WaveeLogLevel.Info, "app", "boot.platform", "", null, Log.SinceStartMs);
            App.BootCore(BootProfile.HeadlessFake);
            Appearance.Seed(options.Theme);   // a fixed kind: no OS read, the token-default accent
            LoadDocuments();
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
        public override Element Render() => content is null ? RootFrame() : Theming.ThemeRoot.Wrap(content);
    }

    /// <summary>One mounted shell (<see cref="Headless.Mount"/>). Every member runs on the UI thread.</summary>
    public sealed class HeadlessShell : IDisposable
    {
        readonly HeadlessPlatformApp _app;
        readonly PosterSeams _seams;
        readonly List<(NodeHandle Node, string Key)> _keys = new();
        bool _disposed;

        /// <summary>The engine host: the scene (<see cref="AppHost.Scene"/>), the animation engine
        /// (<see cref="AppHost.Animation"/>), <see cref="AppHost.RunFrame"/>.</summary>
        public AppHost Host { get; }

        /// <summary>The headless window: its size, scale and input queue (<see cref="HeadlessWindow.QueueInput"/>).</summary>
        public HeadlessWindow Window { get; }

        internal HeadlessShell(float widthDip, float heightDip, float scale, IFrameTimeSource frameTime, Func<Element>? content = null)
        {
            _app = new HeadlessPlatformApp();
            Window = new HeadlessWindow(new WindowDesc("wavee-shell", new Size2(widthDip * scale, heightDip * scale), scale));
            Window.Show();
            var strings = Entities.Strings;
            Host = new AppHost(_app, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new HeadlessRoot(content),
                frameTime: frameTime);
            // Every cross-thread seam posts into THIS host, each post followed by the publish on the cadence — the GUI's
            // poster minus the early-post queue (the host exists before anything posts).
            _seams = PosterSeams.Capture();
            AppHost host = Host;
            AssignPosters(action => host.Post(PublishAfter.Wrap(action)));
            Host.FrameCompleted += s_frameTick;
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
                Host.Dispose();   // unmounts the tree: the frame's cleanup ends its RootScope
                RootScope.EndMounted();   // belt: a mount never leaves a mounted scope behind, whatever the tree's cleanup did
                _app.Dispose();
                Headless.Unmounted(this);
            }
        }
    }
}
