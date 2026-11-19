// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Notifications.cs — Notifications' and Startup & system's sections (settings rework phase 4) ──────
//
// Notifications: the OS-block banner over Delivery (when Windows suppresses Wavee every dial below is theatre until that is fixed), the quiet hours' two hour
// combos as one row, and one ladder per topic (off · in wavee · windows; two segments for a topic that cannot reach Windows, never three with one dead), with
// the developer's "send a test event" beside it. Startup & system: the language (the languages the build cannot offer greyed in its combo) and the render GPU.
// Every control writes through its dial's writer; the page re-reads (`Settings.Bump`) when the writer asks.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    // ══ NOTIFICATIONS ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the Topics group leads with: what the ladders mean, and (while Windows banners are off) that only "in wavee" reaches you.</summary>
    static void TopicsLead(Paint d, List<Element> into)
    {
        _ = Notify.Prefs.Epoch.Value;          // any dial write, from here or the centre, re-reads the group
        into.Add(ZuneText.Prose(Loc.Get(Strings.Settings.Notify.TopicsHint)) with { Key = "zs:topics:hint", MaxLines = 3 });
        if (!Notify.Prefs.Policy().WindowsEnabled)
            into.Add(ZuneKit.Notice(InfoBarSeverity.Informational, "", Loc.Get(Strings.Settings.Notify.WindowsOffHint)) with { Key = "zs:topics:off" });
    }

    /// <summary>The quiet hours' start and end as one row of two hour combos, keyed as the start dial's row (the end's combo keyed as its own).</summary>
    static Element QuietRange(Paint d, SettingsBodyRow from)
    {
        var k = d.K;
        string[] hours = Settings.NotifyRules.HourLabels();
        Element Hour(SettingsDial dial, Func<NotificationPolicy, int> read) => new BoxEl
        {
            Key = SettingsKeys.ZuneRow(dial.Id), Direction = 0, Shrink = 0f, OnRealized = NoteRow(dial.Id),
            Children = [k.Choice(dial.Id, new ZuneChoice(hours, () => Math.Clamp(read(Notify.Prefs.Policy()), 0, 23),
                h => { if (dial.Writer.Choose?.Invoke(h) == true) Settings.Bump(); }, Chips: false, Width: Z.HourComboWidth))],
        };
        var controls = new List<Element>(2) { Hour(from.Dial, static p => p.Quiet.FromHour) };
        string label = from.Label;
        if (SettingsSpec.TryDial(QuietToDial, out var to))
        {
            controls.Add(Hour(to, static p => p.Quiet.ToHour));
            label += " / " + Loc.Get(to.TitleKey);
        }
        return DialRow(d, from, new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Shrink = 0f, MinWidth = 0f, Wrap = true, Children = controls.ToArray(),
        }, null, label: label);
    }

    const string QuietToDial = "notifications.quiet.to";

    /// <summary>One topic: its ladder (off, in wavee, windows; two segments for a topic that cannot reach Windows), and in developer mode its test event beside
    /// it (the child dial, keyed as its row). A scheduled topic's caption says it is closed.</summary>
    static Element TopicRow(Paint d, SettingsBodyRow row)
    {
        var k = d.K;
        NotifyTopic? topic = null;
        foreach (var t in Notify.Prefs.AllTopics)
            if (string.Equals(SettingsSpec.TopicDialId(t), row.Id, StringComparison.Ordinal)) { topic = t; break; }
        bool canWindows = topic is not { } known || NotificationPolicy.CeilingFor(known) == NotifyLevel.Windows;
        IReadOnlyList<string> labels = canWindows || row.Choices.Length < 3 ? row.Choices : [row.Choices[0], row.Choices[1]];
        var writer = row.Writer;
        SettingsDial dial = row.Dial;
        var kids = new List<Element>(2)
        {
            k.Choice(row.Id, new ZuneChoice(labels, () => dial.Current is int at ? at : 0, i => { if (writer.Choose?.Invoke(i) == true) Settings.Bump(); }, Chips: true)),
        };
        foreach (var child in row.Children)
            if (child.Kind == SettingsRowKind.Action)
            {
                var test = child.Writer;
                kids.Add(new BoxEl
                {
                    Key = SettingsKeys.ZuneRow(child.Id), Direction = 0, Shrink = 0f, OnRealized = NoteRow(child.Id),
                    Children = [k.Cmd(child.Label, () => { if (test.Run?.Invoke() == true) Settings.Bump(); }, child.Enabled)],
                });
            }
        string sub = row.Description ?? "";
        if (topic is { } scheduled && NotificationPolicy.IsScheduled(scheduled)) sub += SummarySeparator + Loc.Get(Strings.Settings.Notify.ClosedBadge);
        return DialRow(d, row, new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, MinWidth = 0f, Children = kids.ToArray() }, sub);
    }

    /// <summary>The banner when Windows suppresses Wavee's notifications (the app, the user or a policy), with the door to Windows' settings where it helps.</summary>
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

    // ══ STARTUP & SYSTEM ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The app language: its combo greys the languages this build cannot offer.</summary>
    static Element LanguageRow(Paint d, SettingsBodyRow row)
    {
        SettingsDial dial = row.Dial;
        var writer = row.Writer;
        return DialRow(d, row, d.K.Choice(row.Id, new ZuneChoice(row.Choices, () => dial.Current is int at ? at : 0, i => { if (writer.Choose?.Invoke(i) == true) Settings.Bump(); },
            Chips: false, ItemEnabled: Settings.Language.Enabled)), row.Description);
    }

    sealed record GpuArgs(SettingsBodyRow Row, SettingsArrival Focus);

    /// <summary>The render-GPU row. The adapter list is enumerated ONCE at mount (a cold DXGI walk) and frozen; a pick persists and live-applies through the engine's
    /// device-reset path (the dial's writer).</summary>
    sealed class GpuRow : ZuneScreenComponent
    {
        readonly IReadOnlyList<GpuAdapterDesc> _adapters = SettingsFacts.RefreshGpus();

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var args = UseProps<GpuArgs>();
            var row = args.Row;
            SettingsDial dial = row.Dial;
            var writer = row.Writer;
            var labels = new string[_adapters.Count + 1];
            labels[0] = Loc.Get(Strings.Settings.Gpu.Automatic);
            for (int i = 0; i < _adapters.Count; i++)
            {
                var a = _adapters[i];
                labels[i + 1] = a.IsCurrent ? Strings.Settings.Gpu.InUse(a.Name) : a.Name;
            }
            int count = _adapters.Count;
            return k.Row(SettingsKeys.ZuneRow(row.Id), LabelOf(args.Focus, row.Id, ZuneText.Label(row.Label)), row.Description,
                k.Choice(row.Id, new ZuneChoice(labels, () => dial.Current is int at && at > 0 && at <= count ? at : 0,
                    i => writer.Choose?.Invoke(i), Chips: false, Width: Z.WideComboWidth, Combo: true)), row.Enabled) with { OnRealized = NoteRow(row.Id) };
        }
    }
}
