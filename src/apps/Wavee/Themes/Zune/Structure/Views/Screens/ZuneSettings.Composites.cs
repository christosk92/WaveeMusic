// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Composites.cs — where Zune's own sections stand in the registry's body (settings rework phase 4) ──
//
// Zune draws some dials as sections of its own (the boards' account card, AI lyrics card, equalizer curve, runtime, Storage census, crash reporting, update hero,
// feedback actions, key caps, …). Each is drawn where its dial lives in the body (`Composite`, by the dial's id) and draws that dial's children itself; a dial a
// sibling's section already draws is skipped (`Skipped`); a group that leads with something that is no dial (the notification banners, the census) gets it here
// (`GroupLead`). Everything else is the body's generic row (`ZuneSettings.Body.cs`).

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    /// <summary>The dial's Zune section, or null for the generic row.</summary>
    static Element? Composite(Paint d, SettingsBodyRow row)
    {
        string id = row.Id;
        if (id.StartsWith(TopicPrefix, StringComparison.Ordinal)) return TopicRow(d, row);
        if (id.StartsWith(ShortcutPrefix, StringComparison.Ordinal)) return DialRow(d, row, KeyChip(row.Value as string ?? ""), row.Description);
        if (StoragePart(id) is int hue) return CensusRow(d, row, hue);
        return id switch
        {
            "appearance.theme" => PickerBlock(row),
            "appearance.zoom" => DialRow(d, row, Embed.Comp(static () => new ZoomCell()) with { Key = "zs:zoom" }, row.Description),
            "sound.quality" => QualityRow(d, row, metered: false),
            "sound.meteredQuality" => QualityRow(d, row, metered: true),
            "sound.equalizer.preset" => PresetRow(d, row),
            "sound.equalizer.curve" => EqCurveRow(d, row),
            "sound.outputDevice" => OutputDeviceRow(d, row),
            "playback.linkedVideos" => Embed.Comp(new VideoArgs(row, d.Focus), static () => new LinkedVideosRow()) with { Key = "zs:video-overrides" },
            "playback.runtime" => Embed.Comp(new RuntimeArgs(row), static () => new RuntimeBlock()) with { Key = "zs:runtime" },
            "lyrics.ai" => Embed.Comp(new AiArgs(row, d.Focus), static () => new AiLyricsRow()) with { Key = "zs:ai" },
            "notifications.quiet.from" => QuietRange(d, row),
            "system.language" => LanguageRow(d, row),
            "system.gpu" => Embed.Comp(new GpuArgs(row, d.Focus), static () => new GpuRow()) with { Key = "zs:gpu" },
            "storage.music.folders" => Embed.Comp(new MusicArgs(row, d.Focus), static () => new MusicFoldersBlock()) with { Key = "zs:music" },
            "storage.music.options" => MusicOptions(d, row),
            "storage.cache.budget" => Embed.Comp(new BudgetArgs(row, d.Props.Storage.Snapshot, d.Props.Storage.Audio, d.Focus), static () => new BudgetRow()) with { Key = "zs:budget" },
            "storage.cache.location" => CacheLocationRow(d, row),
            "storage.metadata.clear" => DialRow(d, row, ConfirmCmd(d.K, row, Words(row.Confirm?.TitleKey) ?? row.Label), MetadataSub(d.Props.Storage.Meta)),
            "account.identity" => AccountCard(d, row),
            "account.privacy.whatLeaves" => WhatLeaves(d, row),
            "account.privacy.crashReports" => Embed.Comp(new CrashArgs(row, d.Focus), static () => new CrashModeRows()) with { Key = "zs:crash-mode" },
            "account.privacy.crashService" => Embed.Comp(new CrashArgs(row, d.Focus), static () => new CrashServiceRow()) with { Key = "zs:crash-service" },
            "account.reports.saved" => Embed.Comp(new ReportsArgs(row), static () => new SavedReports()) with { Key = "zs:reports" },
            "help.update.hero" => Embed.Comp(new UpdateArgs(row), static () => new UpdatePanel()) with { Key = "zs:about:update" },
            "help.update.channel" => DialRow(d, row, d.K.Cmd(Loc.Get(Strings.Update.Channel.Beta), RunOf(row)), row.Description),
            "help.receipts" => ReceiptsBlock(row),
            "help.feedback.report" => ActionRow(row, ActionMarkReport, accent: true),
            "help.feedback.suggest" => ActionRow(row, ActionMarkSuggest, accent: false),
            "help.feedback.ask" => ActionRow(row, ActionMarkAsk, accent: false),
            "help.feedback.reportHolds" => HoldsCard(d.K, row),
            "help.logs.viewer" => Embed.Comp(new LogArgs(row), static () => new LogViewerRow()) with { Key = "zs:log-viewer" },
            "help.logs.detailLevel" => Embed.Comp(new LogArgs(row), static () => new DetailLevelRows()) with { Key = "zs:detail-level" },
            "help.logs.files" => LogFilesRow(d, row),
            "help.tools.capture" => CaptureRow(d, row),
            "help.links.licenses" => LicensesBlock(row),
            _ => null,
        };
    }

    /// <summary>The dial has a Zune section (it is never one of a run of plain command words).</summary>
    static bool HasComposite(string id)
        => id is "help.feedback.report" or "help.feedback.suggest" or "help.feedback.ask" or "help.logs.viewer" or "help.logs.files" or "help.update.hero"
           or "playback.linkedVideos" or "playback.runtime" or "storage.cache.location"
           || StoragePart(id) is not null;

    /// <summary>A dial another dial's section draws (the plan in the account card, the curve's reset, the quiet hours' end beside their start), and the
    /// update rows a Store install has no use for (the Store owns its updates) or only a Store install has.</summary>
    static bool Skipped(string id) => id switch
    {
        "account.plan" or "sound.equalizer.reset" or "notifications.quiet.to" => true,
        "help.update.store" => !Platform.Version.IsStore,
        "help.update.channel" or "help.update.installOnQuit" or "help.update.onMetered" => Platform.Version.IsStore,
        "help.showSummaryAgain" => SettingsContext.Overlay is null || !ReleaseNotes.CanShowSummaryAgain,
        _ => false,
    };

    /// <summary>What a group leads with that is no dial: the notification banners over Delivery and the Topics, the census over On this PC.</summary>
    static void GroupLead(Paint d, SettingsBodyGroup group, List<Element> into)
    {
        switch (d.Category, group.Id)
        {
            case ("notifications", "delivery"):
                if (BlockedBar(d.K, Settings.NotifyRules.Blocked(SettingsFacts.ToastDelivery())) is { } blocked) into.Add(blocked);
                break;
            case ("notifications", "topics"):
                TopicsLead(d, into);
                break;
            case ("storage", "onThisPc"):
                into.Add(Census(d));
                break;
        }
    }

    /// <summary>A press of an action dial's writer (the page re-reads when it asks).</summary>
    static Action RunOf(SettingsBodyRow row)
    {
        var writer = row.Writer;
        return () => { if (writer.Run?.Invoke() == true) Settings.Bump(); };
    }

    /// <summary>A loc key's text, or null.</summary>
    static string? Words(string? key) => key is null ? null : Loc.Get(key);

    const string TopicPrefix = "notifications.topic.", ShortcutPrefix = "help.shortcuts.";
}
