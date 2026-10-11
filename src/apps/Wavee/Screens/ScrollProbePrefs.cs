// ── Screens/ScrollProbePrefs.cs ────────────────────────────────────────────────────────────────────────────────────────
// the scroll probe's persisted settings: the level (Off · Summary · Trace) and the feel profile, read at boot, written
// persist-then-publish-then-apply, and the CSV export of the probe rings
//
// Role: CORE (engine data only: `ScrollProbe` and `ScrollTunables`; no element, component or overlay type)
// Spec: docs/themes/plan/technical-plan.md §5.1 C15 (X38)
//
// Moved out of `Diagnostics.Scroll.cs` (was the nested `Diagnostics.ScrollSettings`) unchanged. The decisions stay
// `ScrollDiagRules` (pure, tested); the Diagnostics "Scroll" card reads and writes this and holds no state of its own.

using System.Globalization;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The scroll probe level and feel profile: persisted, applied to the engine, and published as signals the
/// card binds. Written persist-then-publish-then-apply, UI thread only.</summary>
public static class ScrollProbePrefs
{
    /// <summary>The probe level (<see cref="ScrollDiagRules.LevelOff"/>..<see cref="ScrollDiagRules.LevelTrace"/>).</summary>
    public static readonly Signal<int> Level = new(ScrollDiagRules.LevelSummary);
    /// <summary>The selected profile's index in <see cref="ProfileNames"/>.</summary>
    public static readonly Signal<int> Profile = new(0);

    static string[]? s_profileNames;

    /// <summary>The engine's named feel presets, in <c>FeelProfiles.All</c> order.</summary>
    public static IReadOnlyList<string> ProfileNames => s_profileNames ??= Names();

    static string[] Names()
    {
        var all = FeelProfiles.All;
        var names = new string[all.Count];
        for (int i = 0; i < names.Length; i++) names[i] = all[i].Name;
        return names;
    }

    /// <summary>Boot: apply the persisted level and profile to the engine before the first window.</summary>
    public static void Load(IAppSettings settings)
    {
        int level = ScrollDiagRules.ClampLevel(settings.Get(Platform.Keys.ScrollProbeLevel));
        Level.Value = level;
        ScrollProbe.Level = (ProbeLevel)level;
        int profile = ScrollDiagRules.ProfileIndex(ProfileNames, settings.Get(Platform.Keys.ScrollFeelProfile));
        Profile.Value = profile;
        ScrollTunables.ApplyProfile(ProfileNames[profile]);
    }

    public static void SetLevel(int level)
    {
        level = ScrollDiagRules.ClampLevel(level);
        Platform.Settings.Set(Platform.Keys.ScrollProbeLevel, level);
        Level.Value = level;
        ScrollProbe.Level = (ProbeLevel)level;
    }

    public static void SetProfile(int index)
    {
        if ((uint)index >= (uint)ProfileNames.Count) return;
        string name = ProfileNames[index];
        Platform.Settings.Set(Platform.Keys.ScrollFeelProfile, name);
        Profile.Value = index;
        ScrollTunables.ApplyProfile(name);
    }

    /// <summary>Writes every record the probe rings still hold to <c>logs/scroll-&lt;timestamp&gt;.csv</c>; the path, or
    /// null when the write failed (logged).</summary>
    public static string? ExportCsv(float dpiScale)
    {
        string path = Path.Combine(Platform.LogFolder, ScrollDiagRules.ExportFileName(DateTimeOffset.Now));
        try
        {
            Directory.CreateDirectory(Platform.LogFolder);
            using var writer = File.CreateText(path);
            ScrollProbe.ExportCsv(writer, System.Diagnostics.Stopwatch.Frequency,
                ScrollDiagRules.RefreshHz(Diagnostics.NavigationFrameWatch.RefreshIntervalMs), dpiScale > 0f ? dpiScale : 1f);
            Log.Info("scroll", "scroll probe exported path=" + path + " level=" + Level.Peek().ToString(CultureInfo.InvariantCulture));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("scroll", "scroll probe export failed path=" + path, ex);
            return null;
        }
    }
}
