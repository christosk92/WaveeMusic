// ── Wavee.Tests/TestLocalization.cs — the loc tables every test reads, loaded once and before any test runs ────────────────
//
// The app loads `assets/loc` at boot (`Platform.Host.cs`, `HostOpenLocale`); the test process never boots, so a test that reads a string (`Strings.Home.Release.Album`, a card's
// detail line, a "5 min ago") used to pass only when some OTHER test in the same process had already loaded the folder (the crash-recovery screens do) and failed with the
// unresolved `[home.release.album]` when it ran first, alone, or in parallel with that load. The tables are process-global, so the one fix is to load them here: a module
// initializer runs when the test assembly loads, before xunit schedules anything, and pins English, which is what every string assertion in the suite is written against.

using System.Runtime.CompilerServices;
using FluentGpu.Localization;

namespace Wavee.Tests;

static class TestLocalization
{
    [ModuleInitializer]
    internal static void Load()
    {
        Localization.DefaultCulture = "en-US";
        Localization.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "loc"));
        Localization.SetCulture("en-US");
    }
}
