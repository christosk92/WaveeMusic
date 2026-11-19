// ── Screens/SettingsSpec.Notifications.cs — Notifications: what Wavee tells you, and where (the dials and their verbs) ────────────
//
// Role: CORE (settings rework phase 1). Delivery (Windows banners, the sound, quiet hours) · What you're told about (ONE LADDER PER
// TOPIC, Off → In Wavee → Windows, in `Notify.Prefs.AllTopics` order — read, never re-listed (A9) — each with its developer-only
// "Send a test event") · Tips (phase 3: the hint service's own switch, "Tips about features", and "Reset tips"; design 6.2). No
// glyphs: the Bell/gear glyphs repeat on purpose (ch 27 parity 75a).

using FluentGpu.Controls;
using FluentGpu.Localization;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsSpec
{
    static SettingsCategory NotificationsCategory()
    {
        var topics = new List<SettingsDial>(Notify.Prefs.AllTopics.Length * 2);
        foreach (var topic in Notify.Prefs.AllTopics)
        {
            var captured = topic;
            string id = TopicDialId(topic);
            topics.Add(Make(id, K.Segmented, TopicTitleKey(topic),
                SettingsWriter.OfChoice("notify.topicLevel", i => SettingsVerbs.SetTopicLevel(captured, i)),
                legacy: ["topic:" + topic], captionKey: TopicCaptionKey(topic), value: _ => (int)Notify.Prefs.Level(captured),
                choices: TopicLevelChoices));
            // Developer surface: the dial stays where it is; the chevron reveals "Send a test event" (enabled at every level, Off
            // included: "nothing happens, as configured" is otherwise unverifiable).
            topics.Add(Make(id + ".test", K.Action, Strings.Settings.Notify.Send,
                SettingsWriter.OfAction("notify.sendTestEvent", () => SettingsVerbs.SendTestEvent(captured)),
                legacy: ["topicTest:" + topic], parent: id, tier: SettingsTier.Developer,
                captionKey: NotificationPolicy.IsScheduled(topic) ? Strings.Settings.Notify.SendScheduledSub : Strings.Settings.Notify.SendSub));
        }

        return new("notifications", Strings.Settings.Categories.Notifications.Title, Strings.Settings.Categories.Notifications.Strip,
            Strings.Settings.Categories.Notifications.Subtitle, "Bell",
        [
            Group("delivery", Strings.Settings.Groups.Notifications.Delivery, null,
                Make("notifications.windows", K.Toggle, Strings.Settings.Notify.Windows,
                    SettingsWriter.OfToggle("notify.windows", on => SettingsVerbs.SetNotifyToggle(Platform.Keys.NotifyWindows, on, reconcile: true)),
                    legacy: ["windows"], captionKey: Strings.Settings.Notify.WindowsSub, value: BoolOf(Platform.Keys.NotifyWindows)),
                // Sound and quiet hours only mean something while Windows delivery is on: both dials stay visible but go inert.
                Make("notifications.sound", K.Toggle, Strings.Settings.Notify.Sound,
                    SettingsWriter.OfToggle("notify.sound", on => SettingsVerbs.SetNotifyToggle(Platform.Keys.NotifySound, on, reconcile: false)),
                    legacy: ["sound"], captionKey: Strings.Settings.Notify.SoundSub, value: BoolOf(Platform.Keys.NotifySound),
                    enabled: NeedsWindowsDelivery, silent: true),
                Make("notifications.quiet", K.Toggle, Strings.Settings.Notify.Quiet,
                    SettingsWriter.OfToggle("notify.quiet", on => SettingsVerbs.SetNotifyToggle(Platform.Keys.NotifyQuietEnabled, on, reconcile: true)),
                    legacy: ["quiet"], captionKey: Strings.Settings.Notify.QuietSub, value: BoolOf(Platform.Keys.NotifyQuietEnabled), aliases: ["dnd"],
                    enabled: NeedsWindowsDelivery, silent: true),
                // The From / To pair is composed only while Windows delivery AND quiet hours are on.
                Make("notifications.quiet.from", K.Choice, Strings.Settings.Notify.QuietFrom,
                    SettingsWriter.OfChoice("notify.quietFrom", h => SettingsVerbs.SetQuietHour(Platform.Keys.NotifyQuietFromHour, h)),
                    legacy: ["quietFrom"], parent: "notifications.quiet",
                    value: IntOf(Platform.Keys.NotifyQuietFromHour), visible: f => f.WindowsNotifications && f.QuietHours),
                Make("notifications.quiet.to", K.Choice, Strings.Settings.Notify.QuietTo,
                    SettingsWriter.OfChoice("notify.quietTo", h => SettingsVerbs.SetQuietHour(Platform.Keys.NotifyQuietToHour, h)),
                    legacy: ["quietTo"], parent: "notifications.quiet",
                    value: IntOf(Platform.Keys.NotifyQuietToHour), visible: f => f.WindowsNotifications && f.QuietHours)),
            new SettingsGroup("topics", Strings.Settings.Groups.Notifications.Topics, null, topics.ToArray()),
            // The hint service's switch (every hint's "Don't show tips" writes it) and its reset (Shell/Hints.cs).
            Group("tips", Strings.Settings.Groups.Notifications.Tips, null,
                Make(Hints.TipsDialId, K.Toggle, Strings.Settings.Notify.Tips,
                    SettingsWriter.OfToggle("hints.tips", SettingsVerbs.SetTips), legacy: ["tips"],
                    captionKey: Strings.Settings.Notify.TipsSub, value: BoolOf(Platform.Keys.TipsEnabled)),
                Make("notifications.resetTips", K.Action, Strings.Settings.Notify.ResetTips,
                    SettingsWriter.OfAction("hints.resetTips", SettingsVerbs.ResetTips), legacy: ["resetTips"],
                    captionKey: Strings.Settings.Notify.ResetTipsSub)),
        ]);
    }

    /// <summary>Every topic ladder's three levels.</summary>
    static readonly string[] TopicLevelChoices = [Strings.Settings.Notify.LevelOff, Strings.Settings.Notify.LevelInApp, Strings.Settings.Notify.LevelWindows];

    static SettingsEnablement NeedsWindowsDelivery(ISettingsFacts f)
        => f.WindowsNotifications ? SettingsEnablement.On : SettingsEnablement.Off(null);

    /// <summary>The topic ladder's dial id (<c>notifications.topic.daylistRefresh</c>); its test event is the id plus <c>.test</c>.</summary>
    public static string TopicDialId(NotifyTopic topic)
    {
        string name = topic.ToString();
        return "notifications.topic." + char.ToLowerInvariant(name[0]) + name[1..];
    }

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
    /// <summary>A delivery switch: persist, wake the centre, and (when the change moves what the OS holds) reconcile.</summary>
    public static bool SetNotifyToggle(SettingKey<bool> key, bool on, bool reconcile)
    {
        Platform.Settings.Set(key, on);
        Notify.Prefs.Bump();
        if (reconcile) Notify.RequestReconcile();
        return true;
    }

    /// <summary>"Tips about features": persist; the hint slots re-evaluate (a write of this dial bumps them, `Hints.OnDialWritten`).</summary>
    public static bool SetTips(bool on)
    {
        Platform.Settings.Set(Platform.Keys.TipsEnabled, on);
        return true;
    }

    /// <summary>"Reset tips": every hint may show again (its shows, snoozes, dismissals and retirement cleared); the slots re-evaluate.</summary>
    public static bool ResetTips()
    {
        HintState.ResetHints();
        Hints.Bump();
        return false;
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
