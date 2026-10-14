// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.General.cs — the General and Notifications tabs (themes plan 6.1, T11b) ─────────
//
// From `Screens\Settings.UI.cs` §5 (General: language, links, the tray's Notification-area group, the render-GPU picker) and §6 (Notifications: the OS-block
// banner, the two delivery gates, one ladder per topic). Every control writes through its document row's writer; the page re-reads (`Settings.Bump`) when the
// writer asks.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Tab = Wavee.Settings.Tab;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. GENERAL (W1; tray plan §8) ═════════════════════════════════════════════════════════════════════════════

    static readonly Signal<int> s_language = new(0);
    static readonly Signal<int> s_trayMode = new((int)Tray.DefaultIconMode);

    /// <summary>The page mount: the two combos' signals from the document's values.</summary>
    static void SeedGeneral()
    {
        s_language.Value = D(Tab.General, "language").Current is int language ? language : 0;
        s_trayMode.Value = D(Tab.General, "trayIcon").Current is int mode ? mode : (int)Tray.DefaultIconMode;
    }

    static Element GeneralTab()
    {
        var kids = new List<Element>(20)
        {
            SectionHeader(Loc.Get(Strings.Settings.Language.Title), SectionGlyph(Tab.General, "Language & region"),
                Loc.Get(Strings.Settings.Language.Subtitle)),
            DocRow(D(Tab.General, "language"),
                ComboBox.Create(LanguageLabels(), s_language, width: ClassicSettingsRules.GeneralComboWidth, onChange: SetLanguage,
                    itemEnabled: Settings.Language.Enabled)),

            SectionHeader(Loc.Get(Strings.Settings.Links.Title), SectionGlyph(Tab.General, "Links"),
                Loc.Get(Strings.Settings.Links.Subtitle)),
            SpotifyLinksRow(),
        };

        kids.Add(SectionHeader(Loc.Get(Strings.Settings.General.Tray.Title), SectionGlyph(Tab.General, "Notification area"),
            Loc.Get(Strings.Settings.General.Tray.Subtitle)));
        AddTrayRows(kids);

        kids.Add(SectionHeader(Loc.Get(Strings.Settings.Gpu.Title), SectionGlyph(Tab.General, "Graphics"),
            Loc.Get(Strings.Settings.Gpu.Subtitle)));
        kids.Add(Embed.Comp(static () => new GpuPickerCard()));

        return TabStack(kids.ToArray());
    }

    static string[] LanguageLabels() =>
    [
        Loc.Get(Strings.Settings.Language.System),
        Loc.Get(Strings.Settings.Language.EnglishUs),
        Loc.Get(Strings.Settings.Language.Dutch),
        Loc.Get(Strings.Settings.Language.Korean),
    ];

    static void SetLanguage(int index)
    {
        if (!D(Tab.General, "language").Writer.Choose!(index)) return;   // the verb refuses what the combo's own disabled items refuse
        s_language.Value = index;
        Settings.Bump();
    }

    /// <summary>The opt-in <c>spotify:</c> handler, applied AT THE TOGGLE (the next link opens here / the scheme goes
    /// straight back). A packaged build's manifest owns its protocols, so the row says Windows manages it.</summary>
    static Element SpotifyLinksRow()
    {
        var row = D(Tab.General, "spotifyLinks");
        return DocRow(row, RowToggle(row, row.Enabled(SettingsFacts.Live).Enabled));
    }

    /// <summary>The tray plan §8 group. Every writer calls <c>Tray.Host.OnSettingsChanged()</c> (icon presence, the
    /// foreground hook and — through it — <c>Shell.SyncStartupRegistration()</c> follow).</summary>
    static void AddTrayRows(List<Element> kids)
    {
        var facts = SettingsFacts.Live;
        var iconMode = D(Tab.General, "trayIcon");
        kids.Add(DocRow(iconMode,
            ComboBox.Create(
                [
                    Loc.Get(Strings.Settings.General.Tray.IconAlways),
                    Loc.Get(Strings.Settings.General.Tray.IconWhileHidden),
                    Loc.Get(Strings.Settings.General.Tray.IconNever),
                ],
                s_trayMode, width: ClassicSettingsRules.GeneralComboWidth, onChange: static i =>
                {
                    if (!D(Tab.General, "trayIcon").Writer.Choose!(i)) return;
                    s_trayMode.Value = i;
                    Settings.Bump();
                })));

        // The three rows that need the icon (or the startup task) grey and give their reason in the description.
        foreach (string id in new[] { "closeToTray", "minimizeToTray", "startHidden" })
        {
            var row = D(Tab.General, id);
            kids.Add(DocRow(row, RowToggle(row, row.Enabled(facts).Enabled)));
        }

        var startOnLogin = D(Tab.General, "startOnLogin");
        kids.Add(DocRow(startOnLogin, RowToggle(startOnLogin)));
    }

    /// <summary>Settings › General › Graphics — the render-GPU picker. Its own component so the seeding effect lives on
    /// a child whose lifetime IS the tab. The adapter list is enumerated ONCE at mount (a cold DXGI walk) and frozen;
    /// picking persists LUID + name and live-applies through the engine's device-reset path.</summary>
    sealed class GpuPickerCard : Component
    {
        readonly Signal<int> _selected = new(0);
        readonly IReadOnlyList<GpuAdapterDesc> _adapters = SettingsFacts.RefreshGpus();

        public override Element Render()
        {
            UseEffect(() =>
            {
                _selected.Value = D(Tab.General, "preferredGpu").Current is int index ? index : 0;
            }, DepKey.Empty);

            var labels = new string[_adapters.Count + 1];
            labels[0] = Loc.Get(Strings.Settings.Gpu.Automatic);
            for (int k = 0; k < _adapters.Count; k++)
            {
                var a = _adapters[k];
                labels[k + 1] = a.IsCurrent ? Strings.Settings.Gpu.InUse(a.Name) : a.Name;
            }

            return DocRow(D(Tab.General, "preferredGpu"), ComboBox.Create(labels, _selected, width: ClassicSettingsRules.GpuComboWidth, onChange: Pick));
        }

        // The verb persists and live-applies; the combo's selection is this card's mirror (0 = Automatic).
        void Pick(int i)
        {
            D(Tab.General, "preferredGpu").Writer.Choose!(i);
            _selected.Value = i <= 0 || i > _adapters.Count ? 0 : i;
        }
    }

    // ══ 2. NOTIFICATIONS (W13, W14) ═══════════════════════════════════════════════════════════════════════════════
    //
    // Two global gates, then ONE LADDER PER TOPIC (Off → In Wavee → Windows), in `Notify.Prefs.AllTopics` order — read,
    // never re-listed (A9). Not catalog-driven: the Bell/gear glyphs repeat on purpose (ch 27 parity 75a).

    static Element NotificationsTab()
    {
        _ = Notify.Prefs.Epoch.Value;          // any dial write, from here or the centre, re-reads the tab
        bool dev = SettingsFacts.Live.DeveloperMode;
        var policy = Notify.Prefs.Policy();
        var children = new List<Element>(16);

        // The honesty layer FIRST: when Windows suppresses Wavee every dial below is theatre until that is fixed.
        if (BlockedBar(Settings.NotifyRules.Blocked(SettingsFacts.ToastDelivery())) is { } blocked) children.Add(blocked);

        children.Add(SectionHeader(Loc.Get(Strings.Settings.Notify.DeliveryTitle), Icons.Bell));
        var windows = D(Tab.Notifications, "windows");
        children.Add(DocRow(windows, RowToggle(windows), icon: Icons.Bell));
        // Sound and quiet hours only mean something while Windows delivery is on: both rows stay visible but go inert.
        children.Add(DependentNotifyRow(D(Tab.Notifications, "sound"), Icons.Bell));
        children.Add(DependentNotifyRow(D(Tab.Notifications, "quiet"), Icons.Moon));
        if (D(Tab.Notifications, "quietFrom").Visible(SettingsFacts.Live))
            children.Add(QuietRange(policy));

        children.Add(SectionHeader(Loc.Get(Strings.Settings.Notify.TopicsTitle), Icons.Settings));
        children.Add(new BoxEl
        {
            MinWidth = 0f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
            Children = [Body(Loc.Get(Strings.Settings.Notify.TopicsHint)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 3 }],
        });
        if (!policy.WindowsEnabled)
            children.Add(InfoBar.Create(InfoBarSeverity.Informational, title: "",
                message: Loc.Get(Strings.Settings.Notify.WindowsOffHint), isClosable: false));
        foreach (var topic in Notify.Prefs.AllTopics)
            children.Add(TopicRow(topic, dev));

        return TabStack(children.ToArray());
    }

    static Element TopicRow(NotifyTopic topic, bool dev)
    {
        var level = Notify.Prefs.Level(topic);
        // A topic that cannot reach Windows renders TWO segments, never three-with-one-dead.
        bool canWindows = NotificationPolicy.CeilingFor(topic) == NotifyLevel.Windows;
        string[] labels = canWindows
            ? [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp), Loc.Get(Strings.Settings.Notify.LevelWindows)]
            : [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp)];

        var ladder = D(Tab.Notifications, SettingsDoc.TopicRowId(topic));
        var dial = SelectorBar.Create(labels, new Signal<int>((int)level), onChange: i =>
        {
            // The verb persists and, for a SCHEDULED topic dialled down, gives back the toast the OS already holds.
            if (ladder.Writer.Choose!(i)) Settings.Bump();
        });

        if (!dev) return Row(TopicLabel(topic), TopicSub(topic), dial, TopicGlyph(topic));

        // Developer surface: the dial stays where it is; the chevron reveals "Send a test event".
        string sendSub = Loc.Get(NotificationPolicy.IsScheduled(topic) ? Strings.Settings.Notify.SendScheduledSub : Strings.Settings.Notify.SendSub);
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = TopicLabel(topic),
            Description = TopicSub(topic),
            HeaderIcon = TopicGlyph(topic),
            Content = dial,
            Items =
            [
                Item(Loc.Get(Strings.Settings.Notify.Send), sendSub,
                    Button.Standard(Loc.Get(Strings.Settings.Notify.SendButton), () =>
                    {
                        if (D(Tab.Notifications, SettingsDoc.TopicTestRowId(topic)).Writer.Run!()) Settings.Bump();
                    })),
            ],
        });
    }

    static string TopicLabel(NotifyTopic topic) => Loc.Get(SettingsDoc.TopicTitleKey(topic));

    static string TopicSub(NotifyTopic topic)
    {
        string sub = Loc.Get(SettingsDoc.TopicCaptionKey(topic));
        return NotificationPolicy.IsScheduled(topic) ? sub + "  ·  " + Loc.Get(Strings.Settings.Notify.ClosedBadge) : sub;
    }

    static string TopicGlyph(NotifyTopic topic) => topic switch
    {
        NotifyTopic.NewAlbums => Icons.Album,
        NotifyTopic.NewEpisodes => Icons.Microphone,
        NotifyTopic.ReleaseDrops => Icons.Heart,
        NotifyTopic.Concerts => Icons.Calendar,
        NotifyTopic.Followers => Icons.Bell,
        NotifyTopic.DaylistRefresh => Icons.Sun,
        NotifyTopic.AppUpdates => Icons.Download,
        _ => Icons.Clock,
    };

    /// <summary>A notification row that depends on another setting. The engine does not cascade a card's IsEnabled into its
    /// content (#183: a greyed card kept a live, enabled-looking switch), so ONE flag (the row's enablement) drives the card
    /// and its toggle.</summary>
    static Element DependentNotifyRow(SettingsRow row, string icon)
        => DocRow(row, RowToggle(row, row.Enabled(SettingsFacts.Live).Enabled), icon: icon);

    static Element QuietRange(in NotificationPolicy policy)
    {
        string[] hours = Settings.NotifyRules.HourLabels();
        return Row(Loc.Get(Strings.Settings.Notify.QuietFrom) + " / " + Loc.Get(Strings.Settings.Notify.QuietTo), null,
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f,
                Children =
                [
                    HourCombo(hours, D(Tab.Notifications, "quietFrom"), policy.Quiet.FromHour),
                    HourCombo(hours, D(Tab.Notifications, "quietTo"), policy.Quiet.ToHour),
                ],
            },
            Icons.Clock);
    }

    static Element HourCombo(string[] hours, SettingsRow row, int value)
        => ComboBox.Create(hours, new Signal<int>(Math.Clamp(value, 0, 23)), width: ClassicSettingsRules.HourComboWidth, onChange: h =>
        {
            if (row.Writer.Choose!(h)) Settings.Bump();
        });

    static Element? BlockedBar(Settings.BlockedBanner banner)
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
            ? Button.Standard(Loc.Get(Strings.Settings.Notify.OpenWindows), static () => SettingsVerbs.OpenUri("ms-settings:notifications"))
            : null;
        return InfoBar.Create(InfoBarSeverity.Warning, t.Title, t.Body, isClosable: false, actionButton: action);
    }
}
