// ── Screens/SettingsDoc.cs — the old Settings pages' address, read through to the registry (settings rework phase 1) ───────────
//
// Role: CORE, for one phase. Every dial and its writer live in the registry now (`SettingsSpec`, `Screens/SettingsSpec*.cs`). The
// theme views still draw the old pages — eleven document tabs of sections, grouped into areas (`Settings.Areas.cs`) — until each theme
// draws the registry (phase 4 deletes this file). This is those pages' ADDRESS and nothing else: which dials each old section held, in
// order, under which heading and glyph, and the old row id each dial had on its tab (its first `LegacyIds` entry). No writer, value or
// rule lives here; `Row(tab, id)` returns the registry's own dial, so a view and the inspector call the same writer.

namespace Wavee;

/// <summary>A section of an old document tab: its heading and its dials, in order.</summary>
/// <param name="Tab">Its tab.</param>
/// <param name="Section">The old section title (the key the views and the areas name it by).</param>
/// <param name="TitleKey">The heading (a loc key).</param>
/// <param name="SubtitleKey">The caption under the heading (a loc key), or null.</param>
/// <param name="Rows">Its dials, in order.</param>
public sealed record SettingsSection(Settings.Tab Tab, string Section, string TitleKey, string? SubtitleKey, IReadOnlyList<SettingsDial> Rows);

/// <summary>The old pages' address of every dial (see the file header).</summary>
public static class SettingsDoc
{
    /// <summary>One old section: its title, glyph NAME (null where the old catalogue gave it none), heading keys and dial ids in order.</summary>
    sealed record Place(Settings.Tab Tab, string Section, string? Glyph, string TitleKey, string? SubtitleKey, string[] Dials);

    static IReadOnlyList<Place> Layout()
    {
        var topics = new List<string>(Notify.Prefs.AllTopics.Length * 2);
        foreach (var topic in Notify.Prefs.AllTopics)
        {
            topics.Add(SettingsSpec.TopicDialId(topic));
            topics.Add(SettingsSpec.TopicDialId(topic) + ".test");
        }
        var shortcuts = new string[Settings.Shortcuts.All.Length];
        for (int i = 0; i < shortcuts.Length; i++) shortcuts[i] = "help.shortcuts." + Settings.Shortcuts.All[i].Id;

        const Settings.Tab General = Settings.Tab.General, Appearance = Settings.Tab.Appearance, Playback = Settings.Tab.Playback,
            Notifications = Settings.Tab.Notifications, Storage = Settings.Tab.Storage, Privacy = Settings.Tab.PrivacyDiagnostics,
            About = Settings.Tab.About, Account = Settings.Tab.Account, Devices = Settings.Tab.Devices, Keyboard = Settings.Tab.Keyboard,
            Feedback = Settings.Tab.Feedback;
        return
        [
            new(General, "Language & region", "Globe", Strings.Settings.Language.Title, Strings.Settings.Language.Subtitle, ["system.language"]),
            new(General, "Links", "Link", Strings.Settings.Links.Title, Strings.Settings.Links.Subtitle, ["system.spotifyLinks"]),
            new(General, "Notification area", "ThisPc", Strings.Settings.General.Tray.Title, Strings.Settings.General.Tray.Subtitle,
                ["system.tray.icon", "system.tray.closeToTray", "system.tray.minimizeToTray", "system.startHidden", "system.startOnLogin"]),
            new(General, "Graphics", "Devices", Strings.Settings.Gpu.Title, Strings.Settings.Gpu.Subtitle, ["system.gpu"]),

            new(Appearance, "Theme", "Brush", Strings.Settings.Appearance.Title, Strings.Settings.ThemePicker.Subtitle, ["appearance.theme"]),
            new(Appearance, "Theme options", "Design", Strings.Settings.ThemeOptions.TitleKey, null,
                ["appearance.option.sidebar.reset", "appearance.option.sidebar.customize", "appearance.option.sidebar.resetEverything"]),
            new(Appearance, "Display", "Devices", Strings.Settings.ThemeOptions.DisplayTitle, null,
                ["appearance.appMode", "appearance.zoom", "appearance.marquee", "appearance.fastNavigation"]),
            new(Appearance, "Lyrics", "Microphone", Strings.Settings.Lyrics.Title, Strings.Settings.Lyrics.Subtitle,
                ["lyrics.blur", "lyrics.seekTip", "lyrics.ai", "lyrics.ai.wordSync", "lyrics.ai.plainText", "lyrics.ai.batterySaver",
                 "lyrics.ai.languages", "lyrics.ai.files"]),
            new(Appearance, "Fullscreen", "TvMonitor", Strings.Stage.Settings.Title, Strings.Stage.Settings.Subtitle,
                ["lyrics.stage.visualizer", "lyrics.stage.layout", "lyrics.stage.sensitivity", "lyrics.stage.lyricsOverlay",
                 "lyrics.stage.syncOffset", "lyrics.stage.calm", "lyrics.stage.moments"]),
            new(Appearance, "Now playing", "Album", Strings.Settings.NowPlaying.Title, Strings.Settings.NowPlaying.Subtitle, ["lyrics.playerStyle"]),
            new(Appearance, "Home", "Home", Strings.Settings.Home.Title, Strings.Settings.Home.Subtitle, ["appearance.daylistClock"]),

            new(Playback, "Runtime", null, Strings.Settings.Playback.RuntimeNotSetUp, null, ["playback.runtime", "playback.runtime.signature"]),
            new(Playback, "Playback", "Forward", Strings.Settings.Tabs.Playback, null,
                ["sound.rememberVolume", "playback.autoplay", "playback.crossfade", "playback.crossfade.duration"]),
            new(Playback, "Audio", "MusicNote", Strings.Settings.Playback.AudioTitle, null,
                ["sound.quality", "sound.meteredQuality", "sound.normalization", "sound.normalization.mode", "sound.normalization.album"]),
            new(Playback, "Sound", "Speakers", Strings.Settings.Sound.Title, null,
                ["sound.equalizer", "sound.equalizer.preset", "sound.equalizer.curve", "sound.equalizer.reset"]),
            new(Playback, "Video", "Movie", Strings.VideoOverride.SettingsTitle, null,
                ["playback.video.quality", "playback.video.meteredQuality", "playback.video.prepareAhead", "playback.canvas",
                 "playback.video.endCountdown", "playback.videoOverrides", "playback.videoOverrides.item"]),
            new(Playback, "Player bar", "Pin", Strings.Settings.Playback.PlayerBar, null, ["playback.remainingTime"]),

            new(Notifications, "Delivery", null, Strings.Settings.Notify.DeliveryTitle, null,
                ["notifications.windows", "notifications.sound", "notifications.quiet", "notifications.quiet.from", "notifications.quiet.to"]),
            new(Notifications, "Topics", null, Strings.Settings.Notify.TopicsTitle, null, topics.ToArray()),

            new(Storage, "On this PC", "ThisPc", Strings.Settings.Storage.OnThisPc, null,
                ["storage.library", "storage.runtime", "storage.logs", "storage.logs.clear", "storage.localStore", "storage.imageCache",
                 "storage.aiModels", "storage.aiModels.remove", "storage.otherData"]),
            new(Storage, "Playback cache", "Download", Strings.Settings.Storage.PlaybackCache, null,
                ["storage.cache.audio", "storage.cache.keys", "storage.cache.budget", "storage.cache.budget.preset", "storage.cache.budget.size",
                 "storage.cache.budget.percent", "storage.cache.location", "storage.cache.location.default", "storage.cache.audioBodies",
                 "storage.cache.audioBodies.clear", "storage.cache.licenseKeys", "storage.cache.licenseKeys.clear"]),
            new(Storage, "Metadata cache", "Document", Strings.Settings.Storage.MetadataCache, null, ["storage.metadata.budget", "storage.metadata.clear"]),
            new(Storage, "Reset", "Delete", Strings.Settings.Storage.FactoryReset, Strings.Settings.Storage.FactoryResetSub, ["storage.factoryReset"]),

            new(Privacy, "Privacy", "Shield", Strings.Settings.Privacy.Facts.Title, Strings.Settings.Privacy.Facts.Subtitle,
                ["account.privacy.whatLeaves", "account.privacy.whatLeavesAi", "account.privacy.crashReports", "account.privacy.crashDump",
                 "account.privacy.crashContents", "account.privacy.crashService", "account.privacy.crashServiceCopy", "account.privacy.policy"]),
            new(Privacy, "Crash reports", "Warning", Strings.Settings.Privacy.Reports.Title, Strings.Settings.Privacy.Reports.Subtitle,
                ["account.reports.saved", "account.reports.sendNow", "account.reports.discard", "account.reports.openFolder", "account.reports.deleteAll"]),
            new(Privacy, "Logs", "Document", Strings.Settings.Privacy.Logs.Title, Strings.Settings.Privacy.Logs.Subtitle,
                ["help.logs.viewer", "help.logs.detailLevel", "help.logs.viewerLevel", "help.logs.fileLevel", "help.logs.files"]),
            new(Privacy, "Tools", "Repair", Strings.Settings.Privacy.Tools.Title, Strings.Settings.Privacy.Tools.Subtitle,
                ["help.tools.playbackRuntime", "help.tools.capture", "help.tools.captureViewer", "help.tools.captureFolder"]),
            new(Privacy, "Developer", "Code", Strings.Settings.Privacy.Developer.Title, Strings.Settings.Privacy.Developer.Subtitle,
                ["help.developer.mode", "help.developer.fpsOverlay", "help.developer.simulateUpdate", "help.developer.sendTestCrashReport"]),

            new(About, "Update", null, Strings.Update.Status.Title, null,
                ["help.update.hero", "help.update.openReleasePage", "help.update.dismiss", "help.update.store", "help.update.channel",
                 "help.update.installOnQuit", "help.update.onMetered", "help.update.autoShow"]),
            new(About, "Links", null, Strings.Settings.About.Website, null,
                ["help.whatsNew", "help.links.allIssues", "help.links.website", "help.links.privacyPolicy", "help.links.thirdPartyNotices",
                 "help.links.openDataFolder", "help.showSummaryAgain"]),
            new(About, "Right now", null, Strings.Settings.About.RightNow, null, ["help.receipts"]),
            new(About, "Licenses", null, Strings.Settings.About.Licenses, null, ["help.links.licenses"]),

            new(Account, "Account", null, Strings.Settings.Areas.Account, Strings.Settings.Account.Subtitle,
                ["account.identity", "account.plan", "account.profile", "account.manage", "account.signOut"]),
            new(Devices, "Connect", null, Strings.Settings.Devices.Title, Strings.Settings.Devices.Subtitle, ["sound.connectName", "help.connectDiagnostics"]),
            new(Keyboard, "Shortcuts", null, Strings.Settings.Keyboard.Title, Strings.Settings.Keyboard.Subtitle, shortcuts),
            new(Feedback, "Help", null, Strings.Settings.Feedback.Title, Strings.Settings.Feedback.Subtitle,
                ["help.feedback.report", "help.feedback.suggest", "help.feedback.ask", "help.feedback.reportHolds"]),
            new(Feedback, "Diagnostics", null, Strings.Settings.Feedback.DiagnosticsTitle, null, ["help.feedback.copyDiagnostics"]),
        ];
    }

    sealed class Index
    {
        public readonly List<(Place Place, SettingsSection Section)> Sections = [];
        public readonly Dictionary<(Settings.Tab, string), SettingsDial> Rows = new();
        public readonly Dictionary<SettingsDial, (Settings.Tab Tab, string RowId)> Addresses = new();
    }

    static Index? s_index;

    static Index Built => s_index ??= Build();

    static Index Build()
    {
        var index = new Index();
        foreach (Place place in Layout())
        {
            var dials = new SettingsDial[place.Dials.Length];
            for (int i = 0; i < dials.Length; i++)
            {
                SettingsDial dial = SettingsSpec.Dial(place.Dials[i]);
                if (dial.LegacyIds.Count == 0)
                    throw new InvalidOperationException("SettingsDoc: the dial '" + dial.Id + "' sits on the old page but has no old row id.");
                dials[i] = dial;
                index.Rows[(place.Tab, dial.LegacyIds[0])] = dial;
                index.Addresses[dial] = (place.Tab, dial.LegacyIds[0]);
            }
            index.Sections.Add((place, new SettingsSection(place.Tab, place.Section, place.TitleKey, place.SubtitleKey, dials)));
        }
        return index;
    }

    /// <summary>The dial that was row <paramref name="id"/> of <paramref name="tab"/>. An unknown id throws: a typo must fail loudly,
    /// never silently drop a row.</summary>
    public static SettingsDial Row(Settings.Tab tab, string id)
        => Built.Rows.TryGetValue((tab, id), out var dial) ? dial
            : throw new InvalidOperationException("SettingsDoc: no row '" + id + "' on tab " + tab + ".");

    /// <summary>The dial that was row <paramref name="id"/> of <paramref name="tab"/>, or false.</summary>
    public static bool TryRow(Settings.Tab tab, string id, out SettingsDial dial) => Built.Rows.TryGetValue((tab, id), out dial!);

    /// <summary>The old section <paramref name="section"/> of <paramref name="tab"/>.</summary>
    public static SettingsSection Section(Settings.Tab tab, string section)
    {
        foreach (var (place, built) in Built.Sections)
            if (place.Tab == tab && string.Equals(place.Section, section, StringComparison.Ordinal)) return built;
        throw new InvalidOperationException("SettingsDoc: no section '" + section + "' on tab " + tab + ".");
    }

    /// <summary>The old section's glyph NAME. A section the old catalogue gave no glyph throws, as it did.</summary>
    public static string SectionGlyph(Settings.Tab tab, string section)
    {
        foreach (var (place, _) in Built.Sections)
            if (place.Tab == tab && string.Equals(place.Section, section, StringComparison.Ordinal) && place.Glyph is { } glyph) return glyph;
        throw new InvalidOperationException("SettingsDoc: no glyph for the section '" + section + "' on tab " + tab + ".");
    }

    /// <summary>The glyph NAME of the dial that was row <paramref name="id"/> of <paramref name="tab"/>; a dial without one throws.</summary>
    public static string RowGlyph(Settings.Tab tab, string id)
        => Row(tab, id).Glyph ?? throw new InvalidOperationException("SettingsDoc: no glyph for the row '" + id + "' on tab " + tab + ".");

    /// <summary>The old tab a dial was a row of. The only dials a view draws outside the old pages are the active theme's generated
    /// options, which lived on Appearance.</summary>
    public static Settings.Tab TabOf(SettingsDial dial) => Built.Addresses.TryGetValue(dial, out var a) ? a.Tab : Settings.Tab.Appearance;

    /// <summary>The row id a dial had on its old tab (a generated theme option's: its qualified id).</summary>
    public static string RowIdOf(SettingsDial dial)
        => Built.Addresses.TryGetValue(dial, out var a) ? a.RowId : dial.LegacyIds.Count > 0 ? dial.LegacyIds[0] : dial.Id;

    /// <summary>The old tab and row id of <paramref name="dial"/>, or false for a dial the old pages never drew (a promoted or new one).</summary>
    public static bool TryAddress(SettingsDial dial, out Settings.Tab tab, out string rowId)
    {
        bool found = Built.Addresses.TryGetValue(dial, out var a);
        (tab, rowId) = found ? (a.Tab, a.RowId) : (default, "");
        return found;
    }

    /// <summary>The topic ladder's old row id.</summary>
    public static string TopicRowId(NotifyTopic topic) => "topic:" + topic;

    /// <summary>The topic's "Send a test event" old row id.</summary>
    public static string TopicTestRowId(NotifyTopic topic) => "topicTest:" + topic;
}
