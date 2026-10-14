// ── Screens/Diagnostics.Scroll.cs — the scroll probe's persisted settings and the Diagnostics "Scroll" card ───────────
//
// The engine's scroll diagnostics are runtime DATA, never switches (no env var, no #if): `ScrollProbe.Level`
// (Off · Summary · Trace) gates what the probe rings record, and `ScrollTunables` holds the live `MotionFeel`, picked here
// from the named `FeelProfiles` presets. Both persist as app settings (`diag.scrollProbeLevel`, `diag.scrollFeelProfile`),
// applied once in `Platform.Boot` before the first window and set from the card on the capture-diagnostics page.
//
// What each level buys: Summary keeps the always-on `scroll.burst` line (Diagnostics.Host.cs — one line per wheel/drag
// burst, the engine's own `BurstSummary`); Trace additionally records every input, plan and pose, which is what the CSV
// export (`logs/scroll-<timestamp>.csv`, analyze.py's column layout) is for. The decisions are `ScrollDiagRules` (pure,
// tested); this file owns the engine calls, the settings writes and the card's node; the card is the Common view's (`CommonDiagnostics.Widgets.cs`).

using System.Diagnostics;
using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The scroll card's pure decisions: the stored level's clamp, the stored profile's index, the export's file name
/// and the CSV's refresh rate.</summary>
public static class ScrollDiagRules
{
    /// <summary>The persisted level values — <c>ScrollProbe.Level</c>'s own byte order (Off · Summary · Trace).</summary>
    public const int LevelOff = 0, LevelSummary = 1, LevelTrace = 2;

    /// <summary>The engine's default feel (<c>FeelProfiles.Standard</c>'s name). A stored retired name (the old
    /// <c>WinUiExact</c>/<c>Snappy</c>) is unknown to <see cref="ProfileIndex"/> and reads as this, the first profile.</summary>
    public const string DefaultProfile = "Standard";

    /// <summary>A stored level outside Off..Trace (a corrupted or future value) reads as the default, Summary.</summary>
    public static int ClampLevel(int stored) => stored is >= LevelOff and <= LevelTrace ? stored : LevelSummary;

    /// <summary>The index of <paramref name="name"/> in <paramref name="names"/> (case-insensitive, like
    /// <c>ScrollTunables.ApplyProfile</c>); an unknown or empty name is the first profile.</summary>
    public static int ProfileIndex(IReadOnlyList<string> names, string? name)
    {
        if (!string.IsNullOrEmpty(name))
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary><c>scroll-yyyyMMdd-HHmmss.csv</c> in local time — sorts by capture time next to the dated wavee logs.</summary>
    public static string ExportFileName(DateTimeOffset now)
        => "scroll-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";

    /// <summary>The CSV header's refresh rate from the last frame's refresh interval; 60 Hz before any frame.</summary>
    public static double RefreshHz(double refreshIntervalMs) => refreshIntervalMs > 0 ? 1000.0 / refreshIntervalMs : 60.0;
}

public static partial class Diagnostics
{
    static readonly ScrollHost s_scrollHost = new();

    /// <summary>The Diagnostics "Scroll" card: the probe level (Off · Summary · Trace), the feel profile, and the CSV
    /// export. The Common view (`CommonDiagnostics.Widgets.cs`) draws it; every control reads/writes <see cref="ScrollProbePrefs"/>
    /// through <see cref="s_scrollHost"/>; the card holds no state of its own.</summary>
    static DiagNode ScrollNode() => new DiagScroll(
        Loc.Get(Strings.Diagnostics.Scroll.Title), Loc.Get(Strings.Diagnostics.Scroll.ProbeLevel),
        [Loc.Get(Strings.Diagnostics.Scroll.LevelOff), Loc.Get(Strings.Diagnostics.Scroll.LevelSummary), Loc.Get(Strings.Diagnostics.Scroll.LevelTrace)],
        Loc.Get(Strings.Diagnostics.Scroll.LevelBody), Loc.Get(Strings.Diagnostics.Scroll.Profile), Loc.Get(Strings.Diagnostics.Scroll.Export),
        Loc.Get(Strings.Diagnostics.Scroll.Caption), s_scrollHost);

    sealed class ScrollHost : IDiagScrollHost
    {
        public Signal<int> Level => ScrollProbePrefs.Level;

        public Signal<int> Profile => ScrollProbePrefs.Profile;

        public IReadOnlyList<string> ProfileNames => ScrollProbePrefs.ProfileNames;

        public void SetLevel(int level) => ScrollProbePrefs.SetLevel(level);

        public void SetProfile(int index) => ScrollProbePrefs.SetProfile(index);

        public void Export(float deviceScale)
        {
            string? path = ScrollProbePrefs.ExportCsv(deviceScale);
            if (path is null) Notify.Say(Loc.Get(Strings.Diagnostics.Scroll.ExportFailed), InfoBarSeverity.Error);
            else Notify.Say(Strings.Diagnostics.Scroll.Exported(path), InfoBarSeverity.Success);
        }
    }
}
