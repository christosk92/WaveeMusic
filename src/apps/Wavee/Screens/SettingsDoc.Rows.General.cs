// ── Screens/SettingsDoc.Rows.General.cs — the General and Notifications tabs as document rows + their verbs ─────────
//
// Role: CORE (PR C13; extraction X34). Language & region · Links · Notification area (the tray group) · Graphics, and the
// Notifications tab's two gates and one ladder per topic. Every writer here is a `SettingsVerbs` member: no view persists,
// registers, enumerates or asks the OS itself any more.

using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using T = Wavee.Settings.Tab;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsDoc
{
    // ══ GENERAL ═════════════════════════════════════════════════════════════════════════════════════════════════════

    static IReadOnlyList<SettingsSection> GeneralSections() =>
    [
        Sect(T.General, "Language & region", Strings.Settings.Language.Title, Strings.Settings.Language.Subtitle,
            Make(T.General, "Language & region", "language", K.Choice, Strings.Settings.Language.Label,
                SettingsWriter.OfChoice("language.set", SettingsVerbs.SetLanguage),
                captionKey: Strings.Settings.Language.RestartSub,
                value: s => Settings.Language.IndexOf(s.Get(Platform.Keys.UiCulture)))),

        Sect(T.General, "Links", Strings.Settings.Links.Title, Strings.Settings.Links.Subtitle,
            // A packaged build's manifest owns its protocols, so the row says Windows manages it.
            Make(T.General, "Links", "spotifyLinks", K.Toggle, Strings.Settings.Links.Spotify,
                SettingsWriter.OfToggle("links.spotify", SettingsVerbs.SetHandleSpotifyLinks),
                captionKey: Strings.Settings.Links.SpotifySub, value: BoolOf(Platform.Keys.HandleSpotifyLinks),
                enabled: f => f.Packaged ? SettingsEnablement.Off(Strings.Settings.General.SpotifyLinksPackagedSub) : SettingsEnablement.On)),

        Sect(T.General, "Notification area", Strings.Settings.General.Tray.Title, Strings.Settings.General.Tray.Subtitle,
            Make(T.General, "Notification area", "trayIcon", K.Choice, Strings.Settings.General.Tray.IconMode,
                SettingsWriter.OfChoice("tray.iconMode", SettingsVerbs.SetTrayIconMode),
                caption: f => f.TrayMode == Tray.IconMode.Never ? Strings.Settings.General.Tray.IconNeverSub : Strings.Settings.General.Tray.IconModeSub,
                value: s => (int)Tray.ModeFrom(s.Get(Platform.Keys.TrayIconMode))),
            Make(T.General, "Notification area", "closeToTray", K.Toggle, Strings.Settings.General.Tray.CloseToTray,
                SettingsWriter.OfToggle("tray.closeToTray", on => SettingsVerbs.SetTrayFlag(Platform.Keys.TrayCloseToTray, on)),
                captionKey: Strings.Settings.General.Tray.CloseToTraySub, value: BoolOf(Platform.Keys.TrayCloseToTray),
                enabled: HideAllowed),
            Make(T.General, "Notification area", "minimizeToTray", K.Toggle, Strings.Settings.General.Tray.MinimizeToTray,
                SettingsWriter.OfToggle("tray.minimizeToTray", on => SettingsVerbs.SetTrayFlag(Platform.Keys.TrayMinimizeToTray, on)),
                captionKey: Strings.Settings.General.Tray.MinimizeToTraySub, value: BoolOf(Platform.Keys.TrayMinimizeToTray),
                enabled: HideAllowed),
            Make(T.General, "Notification area", "startHidden", K.Toggle, Strings.Settings.General.Tray.StartHidden,
                SettingsWriter.OfToggle("tray.startHidden", on => SettingsVerbs.SetTrayFlag(Platform.Keys.TrayStartHidden, on)),
                captionKey: Strings.Settings.General.Tray.StartHiddenSub, value: BoolOf(Platform.Keys.TrayStartHidden),
                enabled: f => Tray.StartHiddenAvailable(f.StartOnLogin, f.TrayMode)
                    ? SettingsEnablement.On
                    : SettingsEnablement.Off(!Tray.HideModesAllowed(f.TrayMode)
                        ? Strings.Settings.General.Tray.NeedsIcon : Strings.Settings.General.Tray.NeedsStartOnLogin)),
            Make(T.General, "Notification area", "startOnLogin", K.Toggle, Strings.Settings.General.StartOnLogin,
                SettingsWriter.OfToggle("tray.startOnLogin", SettingsVerbs.SetStartOnLogin),
                captionKey: Strings.Settings.General.StartOnLoginSub, value: BoolOf(Platform.Keys.StartOnLogin))),

        Sect(T.General, "Graphics", Strings.Settings.Gpu.Title, Strings.Settings.Gpu.Subtitle,
            // The index is into the adapters the card enumerated when it mounted (SettingsFacts.Gpus); 0 is Automatic.
            Make(T.General, "Graphics", "preferredGpu", K.Choice, Strings.Settings.Gpu.Label,
                SettingsWriter.OfChoice("gpu.pick", SettingsVerbs.PickGpu),
                captionKey: Strings.Settings.Gpu.RestartSub,
                value: s => SettingsFacts.GpuIndexOf(SettingsFacts.Gpus, s.Get(Platform.Keys.PreferredGpuLuid), s.Get(Platform.Keys.PreferredGpuName)))),
    ];

    /// <summary>The close/minimize-to-tray rows need an icon: with "Never" they grey and say so.</summary>
    static SettingsEnablement HideAllowed(ISettingsFacts f)
        => Tray.HideModesAllowed(f.TrayMode) ? SettingsEnablement.On : SettingsEnablement.Off(Strings.Settings.General.Tray.NeedsIcon);

    // ══ NOTIFICATIONS ═══════════════════════════════════════════════════════════════════════════════════════════════
    //
    // Two global gates, then ONE LADDER PER TOPIC (Off → In Wavee → Windows), in `Notify.Prefs.AllTopics` order — read,
    // never re-listed (A9). Not in the catalog: the Bell/gear glyphs repeat on purpose (ch 27 parity 75a), so no GlyphRole.

    static IReadOnlyList<SettingsSection> NotificationSections()
    {
        var topics = new List<SettingsRow>(Notify.Prefs.AllTopics.Length * 2);
        foreach (var topic in Notify.Prefs.AllTopics)
        {
            var captured = topic;
            topics.Add(Make(T.Notifications, "Topics", TopicRowId(topic), K.Segmented, TopicTitleKey(topic),
                SettingsWriter.OfChoice("notify.topicLevel", i => SettingsVerbs.SetTopicLevel(captured, i)),
                captionKey: TopicCaptionKey(topic), value: _ => (int)Notify.Prefs.Level(captured), catalog: false));
            // Developer surface: the dial stays where it is; the chevron reveals "Send a test event" (enabled at every level,
            // Off included: "nothing happens, as configured" is otherwise unverifiable).
            topics.Add(Make(T.Notifications, "Topics", TopicTestRowId(topic), K.Action, Strings.Settings.Notify.Send,
                SettingsWriter.OfAction("notify.sendTestEvent", () => SettingsVerbs.SendTestEvent(captured)),
                captionKey: NotificationPolicy.IsScheduled(topic) ? Strings.Settings.Notify.SendScheduledSub : Strings.Settings.Notify.SendSub,
                visible: f => f.DeveloperMode, catalog: false));
        }

        return
        [
            Sect(T.Notifications, "Delivery", Strings.Settings.Notify.DeliveryTitle, null,
                Make(T.Notifications, "Delivery", "windows", K.Toggle, Strings.Settings.Notify.Windows,
                    SettingsWriter.OfToggle("notify.windows", on => SettingsVerbs.SetNotifyToggle(Platform.Keys.NotifyWindows, on, reconcile: true)),
                    captionKey: Strings.Settings.Notify.WindowsSub, value: BoolOf(Platform.Keys.NotifyWindows), catalog: false),
                // Sound and quiet hours only mean something while Windows delivery is on: both rows stay visible but go inert.
                Make(T.Notifications, "Delivery", "sound", K.Toggle, Strings.Settings.Notify.Sound,
                    SettingsWriter.OfToggle("notify.sound", on => SettingsVerbs.SetNotifyToggle(Platform.Keys.NotifySound, on, reconcile: false)),
                    captionKey: Strings.Settings.Notify.SoundSub, value: BoolOf(Platform.Keys.NotifySound),
                    enabled: NeedsWindowsDelivery, silent: true, catalog: false),
                Make(T.Notifications, "Delivery", "quiet", K.Toggle, Strings.Settings.Notify.Quiet,
                    SettingsWriter.OfToggle("notify.quiet", on => SettingsVerbs.SetNotifyToggle(Platform.Keys.NotifyQuietEnabled, on, reconcile: true)),
                    captionKey: Strings.Settings.Notify.QuietSub, value: BoolOf(Platform.Keys.NotifyQuietEnabled),
                    enabled: NeedsWindowsDelivery, silent: true, catalog: false),
                // The From / To pair is composed only while Windows delivery AND quiet hours are on.
                Make(T.Notifications, "Delivery", "quietFrom", K.Choice, Strings.Settings.Notify.QuietFrom,
                    SettingsWriter.OfChoice("notify.quietFrom", h => SettingsVerbs.SetQuietHour(Platform.Keys.NotifyQuietFromHour, h)),
                    value: IntOf(Platform.Keys.NotifyQuietFromHour), visible: f => f.WindowsNotifications && f.QuietHours, catalog: false),
                Make(T.Notifications, "Delivery", "quietTo", K.Choice, Strings.Settings.Notify.QuietTo,
                    SettingsWriter.OfChoice("notify.quietTo", h => SettingsVerbs.SetQuietHour(Platform.Keys.NotifyQuietToHour, h)),
                    value: IntOf(Platform.Keys.NotifyQuietToHour), visible: f => f.WindowsNotifications && f.QuietHours, catalog: false)),
            Sect(T.Notifications, "Topics", Strings.Settings.Notify.TopicsTitle, null, topics.ToArray()),
        ];
    }

    static SettingsEnablement NeedsWindowsDelivery(ISettingsFacts f)
        => f.WindowsNotifications ? SettingsEnablement.On : SettingsEnablement.Off(null);

    /// <summary>The topic ladder's row id.</summary>
    public static string TopicRowId(NotifyTopic topic) => "topic:" + topic;

    /// <summary>The topic's "Send a test event" row id.</summary>
    public static string TopicTestRowId(NotifyTopic topic) => "topicTest:" + topic;

    /// <summary>The topic's title (a loc key).</summary>
    public static string TopicTitleKey(NotifyTopic topic) => topic switch
    {
        NotifyTopic.NewAlbums => Strings.Settings.Notify.NewAlbums,
        NotifyTopic.NewEpisodes => Strings.Settings.Notify.NewEpisodes,
        NotifyTopic.ReleaseDrops => Strings.Settings.Notify.ReleaseDrops,
        NotifyTopic.Concerts => Strings.Settings.Notify.Concerts,
        NotifyTopic.Followers => Strings.Settings.Notify.Followers,
        NotifyTopic.DaylistRefresh => Strings.Settings.Notify.Daylist,
        NotifyTopic.AppUpdates => Strings.Settings.Notify.AppUpdates,
        _ => Strings.Settings.Notify.LibraryActivity,
    };

    /// <summary>The topic's description (a loc key; the view appends the "closed" badge to a scheduled topic).</summary>
    public static string TopicCaptionKey(NotifyTopic topic) => topic switch
    {
        NotifyTopic.NewAlbums => Strings.Settings.Notify.NewAlbumsSub,
        NotifyTopic.NewEpisodes => Strings.Settings.Notify.NewEpisodesSub,
        NotifyTopic.ReleaseDrops => Strings.Settings.Notify.ReleaseDropsSub,
        NotifyTopic.Concerts => Strings.Settings.Notify.ConcertsSub,
        NotifyTopic.Followers => Strings.Settings.Notify.FollowersSub,
        NotifyTopic.DaylistRefresh => Strings.Settings.Notify.DaylistSub,
        NotifyTopic.AppUpdates => Strings.Settings.Notify.AppUpdatesSub,
        _ => Strings.Settings.Notify.LibraryActivitySub,
    };
}

public static partial class SettingsVerbs
{
    // ── General ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Pick the app language (the belt-and-braces behind the combo's own disabled items). Takes effect on restart.</summary>
    public static bool SetLanguage(int index)
    {
        if (!Settings.Language.CanPick(index)) return false;
        Platform.Settings.Set(Platform.Keys.UiCulture, Settings.Language.Codes[index]);
        return true;
    }

    /// <summary>The opt-in <c>spotify:</c> handler, applied AT THE TOGGLE (the next link opens here / the scheme goes straight back).</summary>
    public static bool SetHandleSpotifyLinks(bool on)
    {
        Platform.Settings.Set(Platform.Keys.HandleSpotifyLinks, on);
        Settings.SyncSpotifyScheme(on);
        return true;
    }

    /// <summary>Where the notification-area icon shows (0 always · 1 while hidden · 2 never). Every tray writer calls
    /// <c>Tray.Host.OnSettingsChanged()</c>: icon presence, the foreground hook and — through it — the startup registration follow.</summary>
    public static bool SetTrayIconMode(int index)
    {
        if (index is < 0 or > 2) return false;
        Platform.Settings.Set(Platform.Keys.TrayIconMode, index);
        Tray.Host.OnSettingsChanged();
        return true;
    }

    /// <summary>Close / minimize to tray, start hidden: persist and tell the tray host.</summary>
    public static bool SetTrayFlag(SettingKey<bool> key, bool on)
    {
        Platform.Settings.Set(key, on);
        Tray.Host.OnSettingsChanged();
        return true;
    }

    /// <summary>Start with Windows. <c>Tray.Host.OnSettingsChanged</c> calls <c>Shell.SyncStartupRegistration</c> first; it is
    /// called directly too so the Run value follows even before the tray host has booted.</summary>
    public static bool SetStartOnLogin(bool on)
    {
        Platform.Settings.Set(Platform.Keys.StartOnLogin, on);
        Shell.SyncStartupRegistration();
        Tray.Host.OnSettingsChanged();
        return true;
    }

    /// <summary>Pick the render GPU: index 0 (or out of range) is Automatic; otherwise the adapter the card enumerated at mount.
    /// Persists the LUID and the name and live-applies through the engine's device-reset path.</summary>
    public static bool PickGpu(int index)
    {
        var adapters = SettingsFacts.Gpus;
        if (index <= 0 || index > adapters.Count)
        {
            Platform.Settings.Set(Platform.Keys.PreferredGpuLuid, 0L);
            Platform.Settings.Set(Platform.Keys.PreferredGpuName, "");
            GpuAdapterInfo.RequestAdapterSwitch(0L);
            return false;
        }
        var a = adapters[index - 1];
        Platform.Settings.Set(Platform.Keys.PreferredGpuLuid, a.Luid);
        Platform.Settings.Set(Platform.Keys.PreferredGpuName, a.Name);
        GpuAdapterInfo.RequestAdapterSwitch(a.Luid);
        return false;
    }

    // ── Notifications ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A delivery switch: persist, wake the centre, and (when the change moves what the OS holds) reconcile.</summary>
    public static bool SetNotifyToggle(SettingKey<bool> key, bool on, bool reconcile)
    {
        Platform.Settings.Set(key, on);
        Notify.Prefs.Bump();
        if (reconcile) Notify.RequestReconcile();
        return true;
    }

    /// <summary>A quiet-hours bound (0..23).</summary>
    public static bool SetQuietHour(SettingKey<int> key, int hour)
    {
        Platform.Settings.Set(key, Math.Clamp(hour, 0, 23));
        Notify.Prefs.Bump();
        Notify.RequestReconcile();
        return true;
    }

    /// <summary>One topic's level. A SCHEDULED topic dialled down must give back the toast the OS already holds.</summary>
    public static bool SetTopicLevel(NotifyTopic topic, int level)
    {
        Notify.Prefs.SetLevel(topic, (NotifyLevel)level);
        if (NotificationPolicy.IsScheduled(topic)) Notify.RequestReconcile();
        return true;
    }

    /// <summary>"Send a test event": push a real event of the topic through the pipeline and report what the PIPELINE decided —
    /// the report is the feature; the banner is one of its outcomes.</summary>
    public static bool SendTestEvent(NotifyTopic topic)
    {
        var result = Settings.SendTestEvent is { } send ? send(topic) : new Settings.TestEventResult(Settings.TestEventOutcome.Unavailable, null);
        string key = Settings.NotifyRules.OutcomeKey(result.Outcome);
        string message = Settings.NotifyRules.OutcomeHasClock(result.Outcome)
            ? Loc.Format(key, ("0", Settings.NotifyRules.Clock(result.At, System.Globalization.CultureInfo.CurrentCulture)))
            : Loc.Get(key);
        var severity = Settings.NotifyRules.OutcomeSeverity(result.Outcome) switch
        {
            1 => InfoBarSeverity.Success,
            2 => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        Notify.Say(message, severity, durationMs: 6000f);
        return true;   // a new bell row / a changed scheduled count is visible on this page
    }
}
