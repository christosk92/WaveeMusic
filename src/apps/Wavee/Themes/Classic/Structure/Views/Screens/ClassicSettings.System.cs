// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.System.cs — Startup & system's and Notifications' own controls (settings rework 7.1) ─────
//
// From `Screens\Settings.UI.cs` §5 and §6. Startup & system: the language combo (the disabled languages say so in the list) and the render-GPU picker (the adapter
// list walked once per mount); the tray rows are the generic grammar. Notifications: the OS-block banner above Delivery, quiet hours' From / Until combos inside
// the Quiet hours expander, the topics' sentence and Windows-off note, and ONE ladder per topic (Off → In Wavee → Windows; a topic that cannot reach Windows
// draws two segments) with its developer "Send a test event" behind the chevron. Every control writes through its dial's writer; the page re-reads
// (`Settings.Bump`) when the writer asks.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. STARTUP & SYSTEM ═══════════════════════════════════════════════════════════════════════════════════════

    static readonly Signal<int> s_language = new(0);

    /// <summary>The page mount: the language combo's signal from its dial.</summary>
    static void SeedSystem()
        => s_language.Value = Dial("system.language").Current is int language ? language : 0;

    /// <summary>The App language row: the stock choices, the ones this build cannot show greyed in the list (their dial refuses them too), its combo on
    /// <paramref name="selected"/> (the page's signal, or the setup wizard's own, seeded from the dial).</summary>
    static Element LanguageRow(SettingsBodyRow row, Signal<int> selected)
        => CardRow(row, Combo(LanguageLabels(), selected, width: ClassicSettingsRules.GeneralComboWidth, onChange: SetLanguage,
            itemEnabled: Settings.Language.Enabled));

    static string[] LanguageLabels() =>
    [
        Loc.Get(Strings.Settings.Language.System),
        Loc.Get(Strings.Settings.Language.EnglishUs),
        Loc.Get(Strings.Settings.Language.Dutch),
        Loc.Get(Strings.Settings.Language.Korean),
    ];

    static void SetLanguage(int index)
    {
        if (!Dial("system.language").Writer.Choose!(index)) return;   // the verb refuses what the combo's own disabled items refuse
        s_language.Value = index;
        Settings.Bump();
    }

    /// <summary>The render-GPU picker: its own component so the seeding effect lives on a child whose lifetime IS the category. The adapter list is enumerated
    /// ONCE at mount (a cold DXGI walk) and frozen; picking persists LUID + name and live-applies through the engine's device-reset path.</summary>
    sealed class GpuPickerCard : SettingsComponent
    {
        readonly Signal<int> _selected = new(0);
        readonly IReadOnlyList<GpuAdapterDesc> _adapters = SettingsFacts.RefreshGpus();

        public override Element Render()
        {
            UseLayout();
            UseEffect(() =>
            {
                _selected.Value = Dial("system.gpu").Current is int index ? index : 0;
            }, DepKey.Empty);

            var labels = new string[_adapters.Count + 1];
            labels[0] = Loc.Get(Strings.Settings.Gpu.Automatic);
            for (int k = 0; k < _adapters.Count; k++)
            {
                var a = _adapters[k];
                labels[k + 1] = a.IsCurrent ? Strings.Settings.Gpu.InUse(a.Name) : a.Name;
            }

            return DialRow(Dial("system.gpu"), Combo(labels, _selected, width: ClassicSettingsRules.GpuComboWidth, onChange: Pick));
        }

        // The verb persists and live-applies; the combo's selection is this card's mirror (0 = Automatic).
        void Pick(int i)
        {
            Dial("system.gpu").Writer.Choose!(i);
            _selected.Value = i <= 0 || i > _adapters.Count ? 0 : i;
        }
    }

    // ══ 2. NOTIFICATIONS (W13, W14) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The topics' sentence, then the note that Windows banners are off (the "Windows" segments have no effect until they are on).</summary>
    static void TopicsLead(List<Element> kids)
    {
        _ = Notify.Prefs.Epoch.Value;          // any dial write, from here or the centre, re-reads the category
        kids.Add(new BoxEl
        {
            MinWidth = 0f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
            Children = [Body(Loc.Get(Strings.Settings.Notify.TopicsHint)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 3 }],
        });
        if (!Notify.Prefs.Policy().WindowsEnabled)
            kids.Add(InfoBar.Create(InfoBarSeverity.Informational, title: "",
                message: Loc.Get(Strings.Settings.Notify.WindowsOffHint), isClosable: false));
    }

    /// <summary>The topic a ladder dial is (<c>notifications.topic.&lt;topic&gt;</c>), or false.</summary>
    static bool TryTopicOf(string dialId, out NotifyTopic topic)
    {
        foreach (var t in Notify.Prefs.AllTopics)
            if (string.Equals(SettingsSpec.TopicDialId(t), dialId, StringComparison.Ordinal)) { topic = t; return true; }
        topic = default;
        return false;
    }

    /// <summary>One topic's ladder. A topic that cannot reach Windows renders TWO segments, never three-with-one-dead; in developer mode the chevron reveals
    /// "Send a test event" (its child dial).</summary>
    static Element TopicRow(SettingsBodyRow row, NotifyTopic topic)
    {
        var level = Notify.Prefs.Level(topic);
        bool canWindows = NotificationPolicy.CeilingFor(topic) == NotifyLevel.Windows;
        string[] labels = canWindows
            ? [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp), Loc.Get(Strings.Settings.Notify.LevelWindows)]
            : [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp)];

        SettingsDial ladder = row.Dial;
        var dial = Selector(labels, new Signal<int>((int)level), onChange: i =>
        {
            // The verb persists and, for a SCHEDULED topic dialled down, gives back the toast the OS already holds.
            if (ladder.Writer.Choose!(i)) Settings.Bump();
        });
        string sub = TopicSub(row, topic);
        if (row.Children.IsDefaultOrEmpty) return Row(row.Label, sub, dial, TopicGlyph(topic));

        // Developer surface: the dial stays where it is; the chevron reveals "Send a test event".
        var items = new List<Element>(row.Children.Length);
        foreach (SettingsBodyRow child in row.Children)
        {
            SettingsDial test = child.Dial;
            items.Add(Framed(child, Item(child.Label, child.Description,
                StdButton(Loc.Get(Strings.Settings.Notify.SendButton), () =>
                {
                    if (test.Writer.Run!()) Settings.Bump();
                })), item: true));
        }
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = row.Label,
            Description = sub,
            HeaderIcon = TopicGlyph(topic),
            Content = dial,
            Items = items,
            IsExpanded = ExpandCell(row.Id, false),
        });
    }

    static string TopicSub(SettingsBodyRow row, NotifyTopic topic)
    {
        string sub = row.Description ?? "";
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

    /// <summary>A quiet-hours bound (From or Until) as the Quiet hours expander's item: the 24 hours in the culture's clock.</summary>
    static Element QuietHourItem(SettingsBodyRow row)
    {
        string[] hours = Settings.NotifyRules.HourLabels();
        int value = row.Value is int h ? Math.Clamp(h, 0, 23) : 0;
        SettingsDial dial = row.Dial;
        return Item(row.Label, row.Description,
            new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    Combo(hours, new Signal<int>(value), width: ClassicSettingsRules.HourComboWidth, onChange: hour =>
                    {
                        if (dial.Writer.Choose!(hour)) Settings.Bump();
                    }) with { Key = "quiet:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ],
            },
            icon: GlyphOf(dial));
    }

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
            ? StdButton(Loc.Get(Strings.Settings.Notify.OpenWindows), static () => SettingsVerbs.OpenUri("ms-settings:notifications"))
            : null;
        return InfoBar.Create(InfoBarSeverity.Warning, t.Title, t.Body, isClosable: false, actionButton: action);
    }
}
