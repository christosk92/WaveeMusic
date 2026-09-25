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
// tested); this file owns the engine calls, the settings writes and the card.

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
    /// <summary>The scroll probe level and feel profile: persisted, applied to the engine, and published as signals the
    /// card binds. Written persist-then-publish-then-apply, UI thread only.</summary>
    public static class ScrollSettings
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
                ScrollProbe.ExportCsv(writer, Stopwatch.Frequency,
                    ScrollDiagRules.RefreshHz(NavigationFrameWatch.RefreshIntervalMs), dpiScale > 0f ? dpiScale : 1f);
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

    /// <summary>The Diagnostics "Scroll" card: the probe level (Off · Summary · Trace), the feel profile, and the CSV
    /// export. Every control reads/writes <see cref="ScrollSettings"/>; the card holds no state of its own.</summary>
    sealed class ScrollCardView : Component
    {
        static SegmentedItem[] s_levelItems = [];

        static SegmentedItem[] LevelItems()
        {
            if (s_levelItems.Length == 0)   // the launch locale is fixed for the process
                s_levelItems =
                [
                    new(Loc.Get(Strings.Diagnostics.Scroll.LevelOff)), new(Loc.Get(Strings.Diagnostics.Scroll.LevelSummary)),
                    new(Loc.Get(Strings.Diagnostics.Scroll.LevelTrace)),
                ];
            return s_levelItems;
        }

        public override Element Render() => Card(Loc.Get(Strings.Diagnostics.Scroll.Title),
            Labeled(Loc.Get(Strings.Diagnostics.Scroll.ProbeLevel),
                Segmented.Create(LevelItems(), ScrollSettings.Level, onChange: ScrollSettings.SetLevel)),
            Body(Loc.Get(Strings.Diagnostics.Scroll.LevelBody)),
            Labeled(Loc.Get(Strings.Diagnostics.Scroll.Profile),
                ComboBox.Create(ScrollSettings.ProfileNames, ScrollSettings.Profile, width: 220f, onChange: ScrollSettings.SetProfile)),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S,
                Children = [Button.Standard(Loc.Get(Strings.Diagnostics.Scroll.Export), Export)],
            },
            Caption(Loc.Get(Strings.Diagnostics.Scroll.Caption)));

        void Export()
        {
            string? path = ScrollSettings.ExportCsv(Context.Scene?.DeviceScale ?? 1f);
            if (path is null) Notify.Say(Loc.Get(Strings.Diagnostics.Scroll.ExportFailed), InfoBarSeverity.Error);
            else Notify.Say(Strings.Diagnostics.Scroll.Exported(path), InfoBarSeverity.Success);
        }

        static Element Labeled(string label, Element control) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = [new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = RuntimeLabelWidth, Shrink = 0f }, control],
        };
    }
}
