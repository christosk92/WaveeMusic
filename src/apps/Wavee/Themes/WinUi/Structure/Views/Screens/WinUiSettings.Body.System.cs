// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Body.System.cs — Notifications' and Startup & system's WinUI cards ─────────────────────────────────
//
// NOTIFICATIONS. The honesty layer leads the Delivery group: when Windows suppresses Wavee every dial below is theatre until that is fixed, so its Warning bar
// (with "Open Windows settings" where Windows lets the user change it) comes first. The quiet hours' From and To are hour combos. What you're told about
// is ONE LADDER PER TOPIC (Off → In Wavee → Windows), in `Notify.Prefs.AllTopics` order; a topic that cannot reach Windows draws TWO segments, never three
// with one dead; a scheduled topic says it is closed; in Developer mode its chevron reveals "Send a test event" (the topic's child dial).
//
// STARTUP & SYSTEM. The language combo greys the languages that cannot be picked (the verb refuses what the combo refuses). The render-GPU picker enumerates the
// adapters ONCE at mount (a cold DXGI walk) and freezes them; a pick persists LUID + name and live-applies through the engine's device-reset path.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    // ══ 1. NOTIFICATIONS (W13, W14) ═══════════════════════════════════════════════════════════════════════════════

    const string TopicPrefix = "notifications.topic.";

    /// <summary>The Warning bar when Windows suppresses Wavee's notifications (the app, the user or a policy), or null.</summary>
    static Element? NotifyBlockedBar()
    {
        _ = Notify.Prefs.Epoch.Value;   // any dial write, from here or the centre, re-reads the body
        var banner = Settings.NotifyRules.Blocked(SettingsFacts.ToastDelivery());
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
        return InfoBar.Create(InfoBarSeverity.Warning, t.Title, t.Body, isClosable: false, actionButton: action) with { Key = "settings:notify:blocked" };
    }

    /// <summary>What the topics group says first: what a ladder means, and that Windows banners are off where they are.</summary>
    static Element TopicsHint()
    {
        var policy = Notify.Prefs.Policy();
        var kids = new List<Element>(2)
        {
            new BoxEl
            {
                MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Spacing.XS),
                Children = [Body(Loc.Get(Strings.Settings.Notify.TopicsHint)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 3 }],
            },
        };
        if (!policy.WindowsEnabled)
            kids.Add(InfoBar.Create(InfoBarSeverity.Informational, title: "", message: Loc.Get(Strings.Settings.Notify.WindowsOffHint), isClosable: false));
        return new BoxEl { Key = "settings:notify:topics-hint", Direction = 1, Gap = WinUiSettingsRules.CardSpacing, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>One topic's ladder: two or three segments (its ceiling), "closed" for a scheduled topic, and the developer's test event under its chevron.</summary>
    static Element TopicCard(RowCtx x)
    {
        var row = x.Row;
        var dial = row.Dial;
        NotifyTopic topic = default;
        foreach (var t in Notify.Prefs.AllTopics)
            if (string.Equals(SettingsSpec.TopicDialId(t), row.Id, StringComparison.Ordinal)) { topic = t; break; }
        bool canWindows = NotificationPolicy.CeilingFor(topic) == NotifyLevel.Windows;
        string[] labels = canWindows
            ? [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp), Loc.Get(Strings.Settings.Notify.LevelWindows)]
            : [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp)];
        Element ladder = Selector(labels, new Signal<int>(row.Value is int at ? at : 0), onChange: i =>
        {
            // The verb persists and, for a SCHEDULED topic dialled down, gives back the toast the OS already holds.
            if (dial.Writer.Choose is { } choose && choose(i)) Settings.Bump();
        });
        string sub = row.Description ?? "";
        if (NotificationPolicy.IsScheduled(topic)) sub = sub + "  ·  " + Loc.Get(Strings.Settings.Notify.ClosedBadge);
        var card = BaseCard(x, ladder) with { Description = sub };
        if (row.Children.IsEmpty) return DialCard(card);
        var items = new Element[row.Children.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var test = row.Children[i];
            items[i] = ChildHost(x, test, PlainItem(test.Label, test.Description,
                StdButton(Loc.Get(Strings.Settings.Notify.SendButton), () => Press(test.Dial))));
        }
        return Expander(card, items, x.Expanded);
    }

    /// <summary>A quiet-hours bound (From, To): the hours in a combo.</summary>
    static Element QuietHourCard(RowCtx x)
    {
        int hour = Math.Clamp(x.Row.Value is int h ? h : 0, 0, 23);
        return DialCard(BaseCard(x, DialCombo(x.Row, Settings.NotifyRules.HourLabels(), WinUiSettingsRules.HourComboWidth, value: hour)));
    }

    // ══ 2. STARTUP & SYSTEM ═══════════════════════════════════════════════════════════════════════════════════════

    static string[] LanguageLabels() =>
    [
        Loc.Get(Strings.Settings.Language.System),
        Loc.Get(Strings.Settings.Language.EnglishUs),
        Loc.Get(Strings.Settings.Language.Dutch),
        Loc.Get(Strings.Settings.Language.Korean),
    ];

    /// <summary>The app language: the languages that cannot be picked are greyed (the verb refuses them too).</summary>
    static Element LanguageCard(RowCtx x)
        => DialCard(BaseCard(x, DialCombo(x.Row, LanguageLabels(), WinUiSettingsRules.GeneralComboWidth, itemEnabled: Settings.Language.Enabled)));

    /// <summary>The render GPU (see the file header): its own component, so the adapter walk happens once, at its mount.</summary>
    static Element GpuCard(RowCtx x) => Embed.Comp(new GpuProps(x), static () => new GpuPickerCard());

    sealed record GpuProps(RowCtx X);

    sealed class GpuPickerCard : SettingsComponent
    {
        readonly IReadOnlyList<GpuAdapterDesc> _adapters = SettingsFacts.RefreshGpus();

        public override Element Render()
        {
            UseLayout();
            var x = UseProps<GpuProps>().X;
            var labels = new string[_adapters.Count + 1];
            labels[0] = Loc.Get(Strings.Settings.Gpu.Automatic);
            for (int k = 0; k < _adapters.Count; k++)
            {
                var a = _adapters[k];
                labels[k + 1] = a.IsCurrent ? Strings.Settings.Gpu.InUse(a.Name) : a.Name;
            }
            int at = x.Row.Value is int index && index > 0 && index <= _adapters.Count ? index : 0;
            return DialCard(BaseCard(x, DialCombo(x.Row, labels, WinUiSettingsRules.GpuComboWidth, value: at)));
        }
    }
}
