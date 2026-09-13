// ── App.cs ─────────────────────────────────────────────────────────────────────────────────────────────────────────
// Main, window, composition order, GC latency mode, Glyphs before FluentAppHarness.Run (ch 00 §9.5)
//
// Role: CORE
// Owner: —
// Wave: 0
// Budget: 400 lines
// Spec: plan

using System;
using System.IO;
using System.Runtime;

namespace Wavee;

public static class App
{
    [STAThread]
    static int Main(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; // P16a, once
        Glyphs.Register();               // wavee-icons.otf PUA + bundled SegoeFluentIcons.ttf, BEFORE the harness runs, or every Fluent-Icons glyph is tofu on Win10 (ch 00 §9.5)
        Platform.ProfileRoot = Diagnostics.Probe.ProfileArg(args);   // "" = the default profile folder; read by LocalFolder (headless plan §3.6)
        Platform.Boot();                 // log, credentials, settings, update policy (shell); parses --fake; migrates the zoom mode at settings load (ch 00 §9.5)
        if (Diagnostics.Probe.TryRun(args, out int code))              // --headless (headless plan §3.2): no window, no Shell; installs its own marshallers
        {
            Platform.Shutdown();
            return code;
        }

        Shell.InstallMarshallers();      // FIRST of the GUI boot: every seam posts to the UI thread; posts queue until the root attaches (G-013)
        if (!Platform.Args.Fake)
        {
            Setup.BootstrapInstall();    // fresh-install detection over the profile's disk witnesses (read-only probe), before any store opens (B5)
            Store.Use(Path.Combine(Platform.LocalFolder, "library.db"));   // the persistent graph (G-007); --fake stays memory-only
        }
        Entities.Boot(Platform.Scope);   // table set for the last scope, Store thread, Fetch
        if (Platform.Args.Fake) Entities.SeedFake(Platform.Clock.SeedEpoch);   // --fake: the offline seed (ch 31), after the tables exist and before any page reads them
        Spotify.Boot();                  // session static, signals created once; login later
        Spotify.Api.Boot();              // Fetch.Register(Transport): the planner's provider (G-003)
        // A monotonic clock that never freezes (decision D22): the audio thread, the Connect snapshot and the SMTC throttles
        // stamp with it off the UI thread, and it keeps running while the window is idle, minimized or tray-hidden. Same QPC
        // domain as Design.FrameTime, so render sites convert explicitly.
        Playback.FrameNowMs = static () => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        Playback.Boot();                 // state, host loop, audio pump, os bridges
        if (Platform.Args.Fake) Playback.Audio.UseSilentEndpoint();              // --fake never opens a device
        Modules.Boot();
        Sidebar.Boot();                  // design, pane state, the layout document and pins; before its action seams and the UI (B6)
        Sidebar.InstallActionSeams();    // Actions.Services.IsPinned / SetPinned (J1)
        Shell.InstallUi();               // RootFactory + the Wave-4 pages; must precede Run (I1)
        if (!Platform.Args.Fake && Platform.HasStoredCredential())
            Spotify.Login();             // resume the stored credential at launch (G-002); its posts wait for the root's poster
        Shell.Run();                     // window + engine loop; never returns until exit

        // The exit tail (G-012), after the loop and while the log is still alive: layout write, telemetry batch, audio,
        // video, the graph store, the stream caches, then the platform (whose log sink flushes last).
        Sidebar.Shutdown();
        Spotify.Telemetry.Shutdown();
        Playback.Audio.Shutdown();
        Playback.Video.Shutdown();
        Store.Shutdown();
        (Spotify.Audio.DiskCache.Shared as IDisposable)?.Dispose();
        Spotify.Audio.Fetcher.Shared.Dispose();
        Platform.Shutdown();             // the log's file sink writes on a pool thread: without this the exit tail never reaches wavee-yyyyMMdd.log
        return 0;
    }
}
