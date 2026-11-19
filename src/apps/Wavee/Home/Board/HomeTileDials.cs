// ── Home/Board/HomeTileDials.cs: the app-wide settings a tile's panel draws in place (settings design 5.1-5.3) ──────────────────────────
//
// Role: CORE. A tile's options are the tile's own (the Home document's item options). A feature's app-wide settings live in Settings, and its tile's
// panel may draw them too, under a small "For every daylist" label (owner, 2026-11-18: "the daylist setting should be visible here"). They are the
// SAME settings: each reads the value its Settings row reads and writes through that row's DIAL (`SettingsDial.WriteChoice`, the registry's one writer)
// with the panel as its source (`SettingsSurfaces.HomeDaylist`), so a pick in the panel shows in Settings and the reverse, Recently changed says it
// came from Home, the hints about it retire, and nothing is stored or mirrored here. A theme's panel draws them in its own grammar
// (`HomeTileOptionsProps.Dials`).

using System.Collections.Immutable;

namespace Wavee;

/// <summary>The app-wide settings a tile source's panel draws (<see cref="HomeSourceDef.Dials"/>).</summary>
public static class HomeTileDials
{
    /// <summary>The daylist's: its clock face first (what the tile shows), then whether its refresh is announced.</summary>
    public static ImmutableArray<HomeTileDial> Daylist { get; } =
    [
        // Settings › Appearance › Home and library › Daylist clock: the index is the DaylistClockMode int (the Settings combo's order).
        new HomeTileDial
        {
            Id = "daylistClock", LabelKey = Strings.Home.Board.Dial.Clock,
            ChoiceKeys =
            [
                Strings.Settings.Appearance.DaylistClockBoth, Strings.Settings.Appearance.DaylistClockUpdateTime,
                Strings.Settings.Appearance.DaylistClockCountdown,
            ],
            Value = static () => (int)Prefs.Appearance.DaylistClock(),
            Choose = static i => Write(ClockDial, i),
        },
        // Settings › Notifications › Your daylist refreshed: Off · In Wavee · Windows (the topic reaches Windows, so all three).
        new HomeTileDial
        {
            Id = "daylistRefresh", LabelKey = Strings.Home.Board.Dial.Refresh,
            ChoiceKeys = [Strings.Settings.Notify.LevelOff, Strings.Settings.Notify.LevelInApp, Strings.Settings.Notify.LevelWindows],
            Value = static () => (int)RefreshLevel(),
            // The Settings ladder's dial: its verb persists (and reconciles a scheduled toast).
            Choose = static i => Write(SettingsSpec.TopicDialId(NotifyTopic.DaylistRefresh), i),
            // "Windows" acts as "In Wavee" while Windows notifications are off: the panel says why, in the Settings page's own words.
            NoteKey = static () => RefreshLevel() == NotifyLevel.Windows && !Notify.Prefs.Policy().WindowsEnabled
                ? Strings.Settings.Notify.WindowsOffHint : null,
        },
    ];

    /// <summary>The daylist clock's dial (Settings › Appearance › Home and library).</summary>
    public const string ClockDial = "appearance.daylistClock";

    /// <summary>A pick in the panel: the dial's own writer, with the daylist tile as the source; a write that asks the page to re-read bumps an open
    /// Settings page, as a Settings row's does.</summary>
    static void Write(string dialId, int index)
    {
        if (SettingsSpec.Dial(dialId).WriteChoice(index, SettingsSurfaces.HomeDaylist)) Settings.Bump();
    }

    /// <summary>The daylist refresh topic's level: a subscribing read (the notification settings' epoch), the value the Settings ladder shows.</summary>
    static NotifyLevel RefreshLevel()
    {
        _ = Notify.Prefs.Epoch.Value;
        return Notify.Prefs.Level(NotifyTopic.DaylistRefresh);
    }
}
