// ── Screens/Settings.Areas.cs — the Settings page's twelve sections, as data ────────────────────────────────────────────
//
// Role: CORE. The Zune boards (Z-Settings-Sections and its neighbours) list twelve sections: account · appearance · playback · audio · lyrics · devices & connect ·
// downloads & storage · notifications · keyboard · privacy · feedback & help · about. The app's document has eleven TABS (where a row lives: `Settings.Tab`), seven of
// which were the old pivot. The PAGE shows AREAS: each area is a list of document SECTIONS (a `Part`), so a row moves between areas by editing this table, never by
// changing its identity (its `(Tab, Id)`, its writer, its tests). Both themes draw the same area from the same parts: Zune as a column of groups beside the section
// list, Classic as its cards under the tab strip.
//
// DEEP LINKS. The settings route carries a slug (`wavee://open?route=settings&arg=<slug>`, the route arg, the palette): `AreaFromSlug` takes the area's own slug and
// every slug the page ever answered to (the seven old tab slugs, plus the boards' spellings), so an old link lands on the area that now holds what it opened.

namespace Wavee;

public static partial class Settings
{
    /// <summary>The page's sections, in list order (the order of <see cref="AreaSlugs"/>).</summary>
    public enum Area : byte { Account, Appearance, Playback, Audio, Lyrics, Devices, Storage, Notifications, Keyboard, Privacy, Feedback, About }

    /// <summary>One document section a page area draws.</summary>
    /// <param name="Tab">The tab the section lives in.</param>
    /// <param name="Section">The section's catalog title (the key into <see cref="SettingsDoc.Section"/>).</param>
    public readonly record struct Part(Tab Tab, string Section);

    /// <summary>1:1 with <see cref="Area"/>. The slug is the route's arg and the scroll key's suffix.</summary>
    public static readonly string[] AreaSlugs =
        ["account", "appearance", "playback", "audio", "lyrics", "devices", "storage", "notifications", "keyboard", "privacy", "feedback", "about"];

    /// <summary>The area's name (a loc key), the list's word.</summary>
    public static readonly string[] AreaTitleKeys =
    [
        Strings.Settings.Areas.Account, Strings.Settings.Areas.Appearance, Strings.Settings.Areas.Playback, Strings.Settings.Areas.Audio, Strings.Settings.Areas.Lyrics,
        Strings.Settings.Areas.Devices, Strings.Settings.Areas.Storage, Strings.Settings.Areas.Notifications, Strings.Settings.Areas.Keyboard,
        Strings.Settings.Areas.Privacy, Strings.Settings.Areas.Feedback, Strings.Settings.Areas.About,
    ];

    /// <summary>The document sections each area draws, in order. EVERY section of the document appears in exactly one area (`SettingsAreaTests` checks it).</summary>
    public static IReadOnlyList<Part> PartsOf(Area area) => s_parts[(int)area];

    static readonly Part[][] s_parts =
    [
        // account: who is signed in, and the Spotify links Wavee handles.
        [new(Tab.Account, "Account"), new(Tab.General, "Links")],
        // appearance: the theme, then how the window behaves (the board's "window" group: language, the notification-area icon and what closing does) and what draws it.
        [
            new(Tab.Appearance, "Theme"), new(Tab.Appearance, "Theme options"), new(Tab.Appearance, "Display"),
            new(Tab.General, "Language & region"), new(Tab.General, "Notification area"), new(Tab.General, "Graphics"),
            new(Tab.Appearance, "Fullscreen"), new(Tab.Appearance, "Now playing"), new(Tab.Appearance, "Home"),
        ],
        // playback: what the queue does by itself, the video it plays, the bar it plays from.
        [new(Tab.Playback, "Runtime"), new(Tab.Playback, "Playback"), new(Tab.Playback, "Video"), new(Tab.Playback, "Player bar")],
        // audio: the quality ladder, loudness and the equalizer.
        [new(Tab.Playback, "Audio"), new(Tab.Playback, "Sound")],
        [new(Tab.Appearance, "Lyrics")],
        [new(Tab.Devices, "Connect")],
        [new(Tab.Storage, "On this PC"), new(Tab.Storage, "Playback cache"), new(Tab.Storage, "Metadata cache"), new(Tab.Storage, "Reset")],
        [new(Tab.Notifications, "Delivery"), new(Tab.Notifications, "Topics")],
        [new(Tab.Keyboard, "Shortcuts")],
        // privacy: what leaves this PC, and the crash reports.
        [new(Tab.PrivacyDiagnostics, "Privacy"), new(Tab.PrivacyDiagnostics, "Crash reports")],
        // feedback & help: tell the developer, then the diagnostics that go with a report.
        [
            new(Tab.Feedback, "Help"), new(Tab.Feedback, "Diagnostics"), new(Tab.PrivacyDiagnostics, "Logs"),
            new(Tab.PrivacyDiagnostics, "Tools"), new(Tab.PrivacyDiagnostics, "Developer"),
        ],
        [new(Tab.About, "Update"), new(Tab.About, "Links"), new(Tab.About, "Right now"), new(Tab.About, "Licenses")],
    ];

    /// <summary>The slugs older links used, and the boards' spellings: each lands on the area that now holds what it opened.</summary>
    static readonly (string Slug, Area Area)[] s_slugAliases =
    [
        ("general", Area.Appearance), ("logs", Area.Feedback), ("diagnostics", Area.Feedback), ("help", Area.Feedback), ("feedback-help", Area.Feedback),
        ("downloads", Area.Storage), ("downloads-storage", Area.Storage), ("cache", Area.Storage),
        ("devices-connect", Area.Devices), ("connect", Area.Devices), ("shortcuts", Area.Keyboard),
    ];

    /// <summary>A deep link's name → the area. An unknown or missing name lands where the old default (General) now lives: Appearance (a bad arg opens the page, never nowhere).</summary>
    public static Area AreaFromSlug(string? slug)
    {
        if (slug is { Length: > 0 })
        {
            for (int i = 0; i < AreaSlugs.Length; i++)
                if (string.Equals(AreaSlugs[i], slug, StringComparison.OrdinalIgnoreCase)) return (Area)i;
            foreach (var (alias, area) in s_slugAliases)
                if (string.Equals(alias, slug, StringComparison.OrdinalIgnoreCase)) return area;
        }
        return Area.Appearance;
    }

    /// <summary>The document tab of <paramref name="area"/>'s first section.</summary>
    public static Tab FirstTab(Area area) => s_parts[(int)area][0].Tab;

    /// <summary>The area a tab's first section opens in (what <see cref="Open(Tab)"/> uses).</summary>
    public static Area HomeArea(Tab tab) => tab switch
    {
        Tab.General => Area.Appearance,
        Tab.Appearance => Area.Appearance,
        Tab.Playback => Area.Playback,
        Tab.Notifications => Area.Notifications,
        Tab.Storage => Area.Storage,
        Tab.PrivacyDiagnostics => Area.Privacy,
        Tab.About => Area.About,
        Tab.Account => Area.Account,
        Tab.Devices => Area.Devices,
        Tab.Keyboard => Area.Keyboard,
        _ => Area.Feedback,
    };

    /// <summary>The area that draws the document section <paramref name="tab"/> / <paramref name="section"/>, or null when none does.</summary>
    public static Area? AreaOf(Tab tab, string section)
    {
        for (int a = 0; a < s_parts.Length; a++)
            foreach (var part in s_parts[a])
                if (part.Tab == tab && string.Equals(part.Section, section, StringComparison.Ordinal)) return (Area)a;
        return null;
    }
}

public static partial class Settings
{
    /// <summary>The keyboard section's reference: the chords the shell defines (`Shell.UI.cs`' chords, `OnShellKey`, and the player's own keys, `Shell.PlayerKey`). It is a READ of
    /// what the app answers to: the app has no rebinding, so the section offers none.</summary>
    public static class Shortcuts
    {
        /// <summary>One shortcut: the row id, its words, the chord as it is spoken ("Ctrl+F"), and whether it exists only under a theme with tabs.</summary>
        public sealed record Entry(string Id, string TitleKey, string? CaptionKey, string Chord, bool NeedsTabs = false);

        /// <summary>"Ctrl+F" as the boards' small cap reads it: "Ctrl F". The plus sign itself ("Ctrl++") is the one chord whose separator is also its key.</summary>
        public static string Words(string chord) => chord == "Ctrl++" ? "Ctrl +" : chord.Replace('+', ' ');

        public static readonly Entry[] All =
        [
            new("playPause", Strings.Settings.Keyboard.PlayPause, Strings.Settings.Keyboard.PlayPauseSub, "Space"),
            new("seekBack", Strings.Settings.Keyboard.SeekBack, Strings.Settings.Keyboard.SeekBackSub, "←"),
            new("seekForward", Strings.Settings.Keyboard.SeekForward, Strings.Settings.Keyboard.SeekForwardSub, "→"),
            new("seekBackFine", Strings.Settings.Keyboard.SeekBackFine, null, "Shift+←"),
            new("seekForwardFine", Strings.Settings.Keyboard.SeekForwardFine, null, "Shift+→"),
            new("volumeUp", Strings.Settings.Keyboard.VolumeUp, Strings.Settings.Keyboard.VolumeUpSub, "↑"),
            new("volumeDown", Strings.Settings.Keyboard.VolumeDown, Strings.Settings.Keyboard.VolumeDownSub, "↓"),
            new("search", Strings.Settings.Keyboard.Search, null, "Ctrl+F"),
            new("palette", Strings.Settings.Keyboard.Palette, null, "Ctrl+K"),
            new("settings", Strings.Settings.Keyboard.Settings, null, "Ctrl+,"),
            new("newTab", Strings.Settings.Keyboard.NewTab, null, "Ctrl+T", NeedsTabs: true),
            new("back", Strings.Settings.Keyboard.Back, null, "Alt+←"),
            new("forward", Strings.Settings.Keyboard.Forward, null, "Alt+→"),
            new("fullscreen", Strings.Settings.Keyboard.Fullscreen, null, "F11"),
            new("zoomIn", Strings.Settings.Keyboard.ZoomIn, null, "Ctrl++"),
            new("zoomOut", Strings.Settings.Keyboard.ZoomOut, null, "Ctrl+−"),
            new("zoomReset", Strings.Settings.Keyboard.ZoomReset, null, "Ctrl+0"),
            new("escape", Strings.Settings.Keyboard.Escape, null, "Esc"),
        ];
    }
}
