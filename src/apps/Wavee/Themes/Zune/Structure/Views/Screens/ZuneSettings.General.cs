// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.General.cs — the shared helpers, the General and the Notifications tabs (themes plan 6.3, Z8b) ──
//
// General: language, links, the notification-area group and the render GPU. Notifications: the OS-block banner, the delivery gates, the quiet hours and one
// ladder per topic. Every control writes through its document row's writer; the page re-reads (`Settings.Bump`) when the writer asks.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
using Wavee.Theming;
using static FluentGpu.Dsl.Ui;
using Tab = Wavee.Settings.Tab;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    // ══ shared ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The document's row <paramref name="id"/> of <paramref name="tab"/>.</summary>
    static SettingsRow D(Tab tab, string id) => SettingsDoc.Row(tab, id);

    /// <summary>The row exists for the facts (a developer-only row is absent, never greyed).</summary>
    static bool Shown(SettingsRow row) => row.Visible(SettingsFacts.Live);

    /// <summary>A group of the open tab: the document section's own title and caption over its rows.</summary>
    static Element Group(ZuneKit k, Tab tab, string section, params Element?[] rows)
    {
        var doc = SettingsDoc.Section(tab, section);
        var kids = new List<Element>(rows.Length);
        foreach (var row in rows)
            if (row is not null) kids.Add(row);
        return k.Block("zs:section:" + (int)tab + ":" + section, Loc.Get(doc.TitleKey), doc.SubtitleKey is { } sub ? Loc.Get(sub) : null, kids);
    }

    /// <summary>A group with a title of the page's own (a group the document has no section for).</summary>
    static Element Group(ZuneKit k, string key, string title, string? sub, params Element?[] rows)
    {
        var kids = new List<Element>(rows.Length);
        foreach (var row in rows)
            if (row is not null) kids.Add(row);
        return k.Block(key, title, sub, kids);
    }

    /// <summary>A toggle row of the document: the row's own words and its switch (greyed with its reason where the document says so).</summary>
    static Element? ToggleRow(ZuneKit k, Tab tab, string id)
    {
        var row = D(tab, id);
        if (!Shown(row)) return null;
        return k.DocRow(row, k.Toggle(row, row.Enabled(SettingsFacts.Live).Enabled));
    }

    /// <summary>A confirmed destructive action: the ONE confirm dialog, Cancel the default.</summary>
    static void ConfirmThen(string title, string body, string primary, Action onConfirm)
        => Controls.Confirm(SettingsContext.Overlay, title, body, primary, onConfirm);

    // ══ GENERAL ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element[] GeneralTab(ZuneKit k)
    {
        var language = D(Tab.General, "language");
        var tray = D(Tab.General, "trayIcon");
        var links = D(Tab.General, "spotifyLinks");
        return
        [
            Group(k, Tab.General, "Language & region",
                k.DocRow(language, k.Choice(language, LanguageLabels(), chips: false, itemEnabled: Settings.Language.Enabled))),
            Group(k, Tab.General, "Links",
                k.DocRow(links, k.Toggle(links, links.Enabled(SettingsFacts.Live).Enabled))),
            Group(k, Tab.General, "Notification area",
                k.DocRow(tray, k.Choice(tray,
                [
                    Loc.Get(Strings.Settings.General.Tray.IconAlways),
                    Loc.Get(Strings.Settings.General.Tray.IconWhileHidden),
                    Loc.Get(Strings.Settings.General.Tray.IconNever),
                ], chips: false)),
                ToggleRow(k, Tab.General, "closeToTray"), ToggleRow(k, Tab.General, "minimizeToTray"),
                ToggleRow(k, Tab.General, "startHidden"), ToggleRow(k, Tab.General, "startOnLogin")),
            Group(k, Tab.General, "Graphics", Embed.Comp(static () => new GpuRow()) with { Key = "zs:gpu" }),
        ];
    }

    static string[] LanguageLabels() =>
    [
        Loc.Get(Strings.Settings.Language.System),
        Loc.Get(Strings.Settings.Language.EnglishUs),
        Loc.Get(Strings.Settings.Language.Dutch),
        Loc.Get(Strings.Settings.Language.Korean),
    ];

    /// <summary>The render-GPU row. The adapter list is enumerated ONCE at mount (a cold DXGI walk) and frozen; a pick persists and live-applies through the engine's
    /// device-reset path (the row's verb).</summary>
    sealed class GpuRow : ZuneScreenComponent
    {
        readonly IReadOnlyList<GpuAdapterDesc> _adapters = SettingsFacts.RefreshGpus();

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var row = D(Tab.General, "preferredGpu");
            var labels = new string[_adapters.Count + 1];
            labels[0] = Loc.Get(Strings.Settings.Gpu.Automatic);
            for (int i = 0; i < _adapters.Count; i++)
            {
                var a = _adapters[i];
                labels[i + 1] = a.IsCurrent ? Strings.Settings.Gpu.InUse(a.Name) : a.Name;
            }
            int count = _adapters.Count;
            return k.DocRow(row, k.Choice(row.Tab + "." + row.Id, new ZuneChoice(labels,
                () => row.Current is int at && at > 0 && at <= count ? at : 0,
                i => row.Writer.Choose!(i), Chips: false, Width: Z.WideComboWidth)));
        }
    }

    // ══ NOTIFICATIONS ════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element[] NotificationsTab(ZuneKit k)
    {
        _ = Notify.Prefs.Epoch.Value;          // any dial write, from here or the centre, re-reads the tab
        bool dev = SettingsFacts.Live.DeveloperMode;
        var policy = Notify.Prefs.Policy();
        var groups = new List<Element>(3);

        // The honesty layer first: when Windows suppresses Wavee every dial below is theatre until that is fixed.
        if (BlockedBar(k, Settings.NotifyRules.Blocked(SettingsFacts.ToastDelivery())) is { } blocked) groups.Add(blocked);

        var quietFrom = D(Tab.Notifications, "quietFrom");
        var quietTo = D(Tab.Notifications, "quietTo");
        groups.Add(Group(k, Tab.Notifications, "Delivery",
            ToggleRow(k, Tab.Notifications, "windows"), ToggleRow(k, Tab.Notifications, "sound"), ToggleRow(k, Tab.Notifications, "quiet"),
            Shown(quietFrom) ? QuietRange(k, quietFrom, quietTo) : null));

        var topics = new List<Element?>(Z.TopicCapacity)
        {
            ZuneText.Prose(Loc.Get(Strings.Settings.Notify.TopicsHint)) with { Key = "zs:topics:hint", MaxLines = 3 },
        };
        if (!policy.WindowsEnabled)
            topics.Add(ZuneKit.Notice(InfoBarSeverity.Informational, "", Loc.Get(Strings.Settings.Notify.WindowsOffHint)) with { Key = "zs:topics:off" });
        foreach (var topic in Notify.Prefs.AllTopics) topics.Add(TopicRow(k, topic, dev));
        groups.Add(Group(k, Tab.Notifications, "Topics", topics.ToArray()));
        return groups.ToArray();
    }

    static Element QuietRange(ZuneKit k, SettingsRow from, SettingsRow to)
    {
        string[] hours = Settings.NotifyRules.HourLabels();
        Element Hour(SettingsRow row, Func<NotificationPolicy, int> read) => k.Choice(row.Id, new ZuneChoice(hours,
            () => Math.Clamp(read(Notify.Prefs.Policy()), 0, 23),
            h => { if (row.Writer.Choose!(h)) Settings.Bump(); }, Chips: false, Width: Z.HourComboWidth));
        return k.Row("zs:row:quiet-range", Loc.Get(Strings.Settings.Notify.QuietFrom) + " / " + Loc.Get(Strings.Settings.Notify.QuietTo), null,
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Shrink = 0f, MinWidth = 0f,
                Children = [Hour(from, static p => p.Quiet.FromHour), Hour(to, static p => p.Quiet.ToHour)],
            });
    }

    /// <summary>One topic: its ladder (off, in wavee, windows), and in developer mode a command that sends a test event.</summary>
    static Element TopicRow(ZuneKit k, NotifyTopic topic, bool dev)
    {
        var ladder = D(Tab.Notifications, SettingsDoc.TopicRowId(topic));
        // A topic that cannot reach Windows renders TWO segments, never three with one dead.
        bool canWindows = NotificationPolicy.CeilingFor(topic) == NotifyLevel.Windows;
        string[] labels = canWindows
            ? [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp), Loc.Get(Strings.Settings.Notify.LevelWindows)]
            : [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp)];
        var kids = new List<Element>(2)
        {
            k.Choice(ladder.Id, new ZuneChoice(labels, () => (int)Notify.Prefs.Level(topic),
                i => { if (ladder.Writer.Choose!(i)) Settings.Bump(); }, Chips: true)),
        };
        if (dev)
            kids.Add(k.Cmd(Loc.Get(Strings.Settings.Notify.SendButton), () =>
            {
                if (D(Tab.Notifications, SettingsDoc.TopicTestRowId(topic)).Writer.Run!()) Settings.Bump();
            }));
        string sub = Loc.Get(SettingsDoc.TopicCaptionKey(topic));
        if (NotificationPolicy.IsScheduled(topic)) sub += "  ·  " + Loc.Get(Strings.Settings.Notify.ClosedBadge);
        return k.Row(ZuneKit.RowKey(ladder), Loc.Get(SettingsDoc.TopicTitleKey(topic)), sub,
            new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, MinWidth = 0f, Children = kids.ToArray() });
    }

    static Element? BlockedBar(ZuneKit k, Settings.BlockedBanner banner)
    {
        (string Title, string Body)? text = banner switch
        {
            Settings.BlockedBanner.App => (Loc.Get(Strings.Settings.Notify.BlockedApp), Loc.Get(Strings.Settings.Notify.BlockedAppSub)),
            Settings.BlockedBanner.User => (Loc.Get(Strings.Settings.Notify.BlockedUser), Loc.Get(Strings.Settings.Notify.BlockedUserSub)),
            Settings.BlockedBanner.Policy => (Loc.Get(Strings.Settings.Notify.BlockedPolicy), Loc.Get(Strings.Settings.Notify.BlockedPolicySub)),
            _ => null,
        };
        if (text is not { } t) return null;
        Element? action = Settings.NotifyRules.OffersOpenSettings(banner)
            ? k.Cmd(Loc.Get(Strings.Settings.Notify.OpenWindows), static () => SettingsVerbs.OpenUri("ms-settings:notifications"))
            : null;
        return ZuneKit.Notice(InfoBarSeverity.Warning, t.Title, t.Body, action) with { Key = "zs:notifications:blocked" };
    }
}
