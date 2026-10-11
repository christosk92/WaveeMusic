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
    /// <summary>The Diagnostics "Scroll" card: the probe level (Off · Summary · Trace), the feel profile, and the CSV
    /// export. Every control reads/writes <see cref="ScrollProbePrefs"/>; the card holds no state of its own.</summary>
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
                Segmented.Create(LevelItems(), ScrollProbePrefs.Level, onChange: ScrollProbePrefs.SetLevel)),
            Body(Loc.Get(Strings.Diagnostics.Scroll.LevelBody)),
            Labeled(Loc.Get(Strings.Diagnostics.Scroll.Profile),
                ComboBox.Create(ScrollProbePrefs.ProfileNames, ScrollProbePrefs.Profile, width: 220f, onChange: ScrollProbePrefs.SetProfile)),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S,
                Children = [Button.Standard(Loc.Get(Strings.Diagnostics.Scroll.Export), Export)],
            },
            Caption(Loc.Get(Strings.Diagnostics.Scroll.Caption)));

        void Export()
        {
            string? path = ScrollProbePrefs.ExportCsv(Context.Scene?.DeviceScale ?? 1f);
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
