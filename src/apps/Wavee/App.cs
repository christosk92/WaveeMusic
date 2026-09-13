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
    static void Main()
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; // P16a, once
        Glyphs.Register();               // wavee-icons.otf PUA + bundled SegoeFluentIcons.ttf — BEFORE the harness runs, or every Fluent-Icons glyph is tofu on Win10 (ch 00 §9.5)
        Platform.Boot();                 // log, credentials, settings, update policy (shell); calls ZoomAutoPolicy.MigrateMode(settings) at settings load, before anything reads appearance.zoom.mode (ch 00 §9.5)
        Entities.Boot(Platform.Scope);   // table set for the last scope, Store thread, Fetch
        Spotify.Boot();                  // session static, signals created once; login later
        Playback.Boot();                 // state, host loop, audio pump, os bridges
        Modules.Boot();
        Shell.Run();                     // window + engine loop; never returns until exit
        Platform.Shutdown();             // the log's file sink writes on a pool thread: without this the exit tail never reaches wavee-yyyyMMdd.log
    }
}
