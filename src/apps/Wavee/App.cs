// ── App.cs ─────────────────────────────────────────────────────────────────────────────────────────────────────────
// Main, window, composition order, GC latency mode, Glyphs before FluentAppHarness.Run (ch 00 §9.5)
//
// Role: CORE
// Owner: —
// Wave: 0
// Budget: 400 lines
// Spec: plan

using System.Runtime;

namespace Wavee;

public static class App
{
    [STAThread]
    static int Main(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; // P16a, once
        Glyphs.Register();               // wavee-icons.otf PUA + bundled SegoeFluentIcons.ttf — BEFORE the harness runs, or every Fluent-Icons glyph is tofu on Win10 (ch 00 §9.5)
        Platform.ProfileRoot = Diagnostics.Probe.ProfileArg(args);   // "" = the default profile folder; read by LocalFolder (headless plan §3.6)
        Platform.Boot();                 // log, credentials, settings, update policy (shell); calls ZoomAutoPolicy.MigrateMode(settings) at settings load, before anything reads appearance.zoom.mode (ch 00 §9.5)
        if (Diagnostics.Probe.TryRun(args, out int code))              // --headless (headless plan §3.2); no window, no Shell
        {
            Platform.Shutdown();
            return code;
        }
        if (!Platform.Args.Fake) Setup.BootstrapInstall();   // fresh-install detection over the profile's disk witnesses (read-only probe), before any store opens (B5)
        Entities.Boot(Platform.Scope);   // table set for the last scope, Store thread, Fetch
        if (Platform.Args.Fake) Entities.SeedFake(Platform.Clock.SeedEpoch);   // --fake: the offline seed (ch 31), after the tables exist and before any page reads them
        Spotify.Boot();                  // session static, signals created once; login later
        Playback.FrameNowMs = static () => Design.FrameTime.NowMs;   // the frame clock, not TickCount64: position extrapolation and lyrics sample ages read it
        Playback.Boot();                 // state, host loop, audio pump, os bridges
        if (Platform.Args.Fake) Playback.Audio.UseSilentEndpoint();              // --fake never opens a device
        Modules.Boot();
        Sidebar.Boot();                  // design, pane state, the layout document and pins; before its action seams and the UI (B6)
        Sidebar.InstallActionSeams();    // Actions.Services.IsPinned / SetPinned (J1)
        Shell.InstallUi();               // RootFactory + the Wave-4 pages; must precede Run (I1)
        Shell.Run();                     // window + engine loop; never returns until exit; installs the UI-thread marshallers
        Sidebar.Shutdown();              // flush the debounced layout write while the log is still alive (bounded wait)
        Platform.Shutdown();             // the log's file sink writes on a pool thread: without this the exit tail never reaches wavee-yyyyMMdd.log
        return 0;
    }
}
