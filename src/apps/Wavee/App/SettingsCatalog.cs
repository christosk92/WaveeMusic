using System;

namespace Wavee;

// ── Settings tab identity ────────────────────────────────────────────────────────────────────────────────────────────
// Kept here (not on SettingsPage) so the pure catalog below can name a tab without depending on the engine-bound page.
public enum SettingsTab { General, Appearance, Playback, Notifications, Storage, Logs, About }

// SETTINGS CATALOG — the fix for "same icon on every row of a section, no logical grouping" (the plan's Workstream B
// complaint #2). Engine-free by construction (System only — no FluentGpu reference), so Wavee.Tests can assert the
// invariant directly instead of trusting the page to have gotten it right: EVERY ROW CARRIES ITS OWN GLYPH, and A
// SECTION'S GLYPH IS NEVER REUSED BY ONE OF ITS OWN ROWS.
//
// Glyphs are stored as NAMES (the string keys in the engine's glyphs.json — "Globe", "Zoom", …), not as
// FluentGpu.Controls.Icons.* constants: Icons is engine-bound and Wavee.Tests does not reference FluentGpu.Controls.
// SettingsGlyphs (Features/Shell/SettingsGlyphs.cs, app-side, NOT test-included) resolves a name to the matching
// Icons.* constant and throws on an unknown one, so a catalog entry that outruns the resolver's switch fails loudly at
// first render in a Debug build rather than silently painting nothing.
//
// SCOPE: General, Appearance, Playback and Storage — the four tabs whose rows are a FIXED table (the ones the
// icon-repeat bug actually lived in). Notifications is deliberately excluded: its rows are one per NotifyTopic,
// enumerated at runtime from NotificationPrefs.AllTopics, and already carry distinct per-topic glyphs (Glyph(NotifyTopic)
// in SettingsPage.Notifications.cs) — a second, static catalog of the same facts would drift the moment a topic is
// added. About is excluded too: every one of its sections holds at most one card (the update panel, the receipts, the
// links list, Reports, Licenses) with no per-row icon to collide. Logs has no rows at all — it is one embedded panel.
public static class SettingsCatalog
{
    public readonly record struct Section(SettingsTab Tab, string Title, string Glyph);

    /// <summary><paramref name="RowId"/> is unique within its <paramref name="Tab"/> (not merely its section) — the
    /// shape every lookup in <see cref="SettingsCatalog"/> keys on.</summary>
    public readonly record struct Row(SettingsTab Tab, string Section, string RowId, string Glyph);

    public static readonly Section[] Sections =
    [
        new(SettingsTab.General, "Language & region", "Globe"),
        new(SettingsTab.General, "Links", "Link"),
        new(SettingsTab.General, "Graphics", "Devices"),
        new(SettingsTab.General, "Developer", "Code"),

        new(SettingsTab.Appearance, "Theme", "Brush"),
        new(SettingsTab.Appearance, "Lists", "List"),
        new(SettingsTab.Appearance, "Sidebar", "DockLeft"),
        new(SettingsTab.Appearance, "Lyrics", "Microphone"),

        new(SettingsTab.Playback, "Audio", "MusicNote"),
        new(SettingsTab.Playback, "Sound", "Speakers"),
        new(SettingsTab.Playback, "Video", "Movie"),
        new(SettingsTab.Playback, "Player bar", "Pin"),

        new(SettingsTab.Storage, "On this PC", "ThisPc"),
        new(SettingsTab.Storage, "Playback cache", "Download"),
        new(SettingsTab.Storage, "Metadata cache", "Document"),
        new(SettingsTab.Storage, "Memory", "List"),
        new(SettingsTab.Storage, "Reset", "Delete"),
    ];

    public static readonly Row[] Rows =
    [
        new(SettingsTab.General, "Language & region", "language", "LocaleLanguage"),
        // "OpenInNewWindow", not "Link": the section header already owns Link, and the plan's own no-repeat rule
        // forbids a row from restating its section's glyph — a single-row section still needs its own icon.
        new(SettingsTab.General, "Links", "spotifyLinks", "OpenInNewWindow"),
        // "Device" (singular), not "Devices": the section owns the plural; the engine ships both as distinct glyphs.
        new(SettingsTab.General, "Graphics", "preferredGpu", "Device"),
        new(SettingsTab.General, "Developer", "developerMode", "Settings"),
        new(SettingsTab.General, "Developer", "fpsOverlay", "Clock"),
        new(SettingsTab.General, "Developer", "dealerArchive", "Document"),
        new(SettingsTab.General, "Developer", "simulateUpdate", "Refresh"),

        new(SettingsTab.Appearance, "Theme", "theme", "Sun"),
        new(SettingsTab.Appearance, "Theme", "zoom", "Zoom"),
        new(SettingsTab.Appearance, "Theme", "marquee", "Font"),
        new(SettingsTab.Appearance, "Theme", "colorWashes", "Design"),
        new(SettingsTab.Appearance, "Lists", "rowDensity", "RowSize"),
        new(SettingsTab.Appearance, "Lists", "hideTrackArtwork", "Picture"),
        new(SettingsTab.Appearance, "Lists", "trackListStyle", "ViewList"),
        new(SettingsTab.Appearance, "Lists", "pageLayout", "DockLeft"),
        new(SettingsTab.Appearance, "Lists", "railUniform", "Pin"),
        new(SettingsTab.Appearance, "Lists", "railReset", "Delete"),
        new(SettingsTab.Appearance, "Sidebar", "sidebarDesign", "SplitView"),
        new(SettingsTab.Appearance, "Sidebar", "sidebarCustomize", "Edit"),
        new(SettingsTab.Appearance, "Lyrics", "lyricsSecondary", "Globe"),
        new(SettingsTab.Appearance, "Lyrics", "lyricsBackdrop", "RefineSparkle"),

        new(SettingsTab.Playback, "Audio", "audioQuality", "Headphones"),
        new(SettingsTab.Playback, "Audio", "meteredQuality", "RadioTower"),
        new(SettingsTab.Playback, "Audio", "rememberVolume", "Volume"),
        new(SettingsTab.Playback, "Audio", "autoplay", "Play"),
        new(SettingsTab.Playback, "Sound", "equalizer", "Equalizer"),
        new(SettingsTab.Playback, "Sound", "crossfade", "Audio"),
        new(SettingsTab.Playback, "Video", "videoQuality", "TvMonitor"),
        new(SettingsTab.Playback, "Video", "videoMetered", "RadioTower"),
        new(SettingsTab.Playback, "Video", "videoOverrides", "Edit"),
        new(SettingsTab.Playback, "Player bar", "playerBarRemaining", "Clock"),

        new(SettingsTab.Storage, "On this PC", "library", "Album"),
        new(SettingsTab.Storage, "On this PC", "runtime", "Code"),
        new(SettingsTab.Storage, "On this PC", "logs", "Document"),
        new(SettingsTab.Storage, "On this PC", "localStore", "Folder"),
        new(SettingsTab.Storage, "On this PC", "imageCache", "Picture"),
        new(SettingsTab.Storage, "Playback cache", "cacheAudio", "Audio"),
        // "Document", not "Tag": the table gave both Cache keys and License keys "Tag", which would collide WITHIN
        // this one section. License keys keeps Tag (the more literal match); Cache keys keeps the icon it already had.
        new(SettingsTab.Storage, "Playback cache", "cacheKeys", "Document"),
        new(SettingsTab.Storage, "Playback cache", "budget", "RowSize"),
        new(SettingsTab.Storage, "Playback cache", "cacheLocation", "FolderOpen"),
        new(SettingsTab.Storage, "Playback cache", "audioBodies", "MusicNote"),
        new(SettingsTab.Storage, "Playback cache", "licenseKeys", "Tag"),
        new(SettingsTab.Storage, "Metadata cache", "metadataBudget", "RowSize"),
        new(SettingsTab.Storage, "Metadata cache", "clearMetadata", "Delete"),
        new(SettingsTab.Storage, "Memory", "residentCache", "ThisPc"),
        // "Attention", not "Delete": the section already owns Delete for the same no-repeat reason as Links/Graphics/
        // Developer above — a destructive one-row section still needs a row glyph distinct from its own header.
        new(SettingsTab.Storage, "Reset", "factoryReset", "Attention"),
    ];

    public static string SectionGlyph(SettingsTab tab, string title)
    {
        foreach (var s in Sections)
            if (s.Tab == tab && string.Equals(s.Title, title, StringComparison.Ordinal))
                return s.Glyph;
        throw new InvalidOperationException($"SettingsCatalog: no section '{title}' on tab {tab}.");
    }

    public static string RowGlyph(SettingsTab tab, string rowId)
    {
        foreach (var r in Rows)
            if (r.Tab == tab && string.Equals(r.RowId, rowId, StringComparison.Ordinal))
                return r.Glyph;
        throw new InvalidOperationException($"SettingsCatalog: no row '{rowId}' on tab {tab}.");
    }
}
