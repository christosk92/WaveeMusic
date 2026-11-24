// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Cards.cs — which dials Classic draws as its own cards (settings rework 7.1) ─────────────
//
// The one table of the dials whose row is not the generic grammar (`ClassicSettings.Body.cs`): a composite card Classic draws today (the theme picker, the AI
// lyrics card, the equalizer, crossfade, the storage census rows, the update hero, the attached videos, the runtime card, the saved reports, the privacy and log
// cards), a control the generic grammar cannot word (the zoom ladder, the quality ladder with its D7 rung, the video heights, the sliders' ranges, the GPU list,
// the languages' availability, the topics' ceilings, the quiet hours' clock), or a dial another card draws (the plan inside the identity row). The cards live
// in the domain files (`ClassicSettings.<Domain>.cs`); this file only routes a row to its card.

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    /// <summary>The card Classic draws for <paramref name="row"/> (null with true: another card draws it, or it has no row here); false for the generic grammar.
    /// <paramref name="group"/> is the row's group on a category page, null for a search result.</summary>
    static bool TryCustom(SettingsBodyRow row, SettingsBodyGroup? group, SettingsPageProps props, out Element? card)
    {
        card = null;
        switch (row.Id)
        {
            // Appearance
            case "appearance.theme": card = ThemePickerView(); return true;
            case "appearance.zoom": card = CardRow(row, Embed.Comp(static () => new ZoomPicker())); return true;
            // Sound
            case "sound.quality": card = CardRow(row, QualityCombo(s_quality)); return true;
            // The description IS the detector's verdict: every state says something, "Unknown" included (never blank).
            case "sound.meteredQuality": card = CardRow(row, MeteredQualityCombo(), sub: MeteredStatus()); return true;
            case "sound.equalizer": card = EqualizerGroup(row); return true;
            // Playback
            case "playback.crossfade": card = CrossfadeGroup(row); return true;
            case "playback.episodeSpeed": card = CardRow(row, Embed.Comp(static () => new EpisodeSpeedSlider())); return true;
            case "playback.video.quality": card = CardRow(row, VideoQualityCombo()); return true;
            case "playback.video.meteredQuality": card = CardRow(row, MeteredVideoCombo()); return true;
            case "playback.videoOverrides": card = VideoOverridesRow(row); return true;
            case "playback.runtime": card = RuntimeCard(row); return true;
            // Lyrics & Now playing
            case "lyrics.blur": card = CardRow(row, LyricsBlurControl()); return true;
            case "lyrics.ai": card = AiLyricsCard(row); return true;
            case "lyrics.stage.sensitivity": card = CardRow(row, Embed.Comp(static () => new StageSensitivitySlider())); return true;
            case "lyrics.stage.syncOffset": card = CardRow(row, Embed.Comp(static () => new StageSyncSlider())); return true;
            // Startup & system
            case "system.language": card = LanguageRow(row, s_language); return true;
            case "system.gpu": card = Embed.Comp(static () => new GpuPickerCard()); return true;
            // Account & privacy
            case "account.identity": card = IdentityRow(row, group); return true;
            case "account.plan": return group is not null;   // the identity row's description (a result draws its own fact)
            case "account.privacy.whatLeaves": card = WhatLeavesExpander(row); return true;
            case "account.privacy.crashReports": card = CrashReportsExpander(row); return true;
            case "account.privacy.crashService": card = Embed.Comp(static () => new CrashServiceCard()) with { Key = "privacy.crash-service" }; return true;
            case "account.privacy.policy": card = PrivacyPolicyCard(row); return true;
            case "account.reports.saved": card = Embed.Comp(static () => new SavedReportsGroup()) with { Key = "privacy.reports" }; return true;
            // Help & about › Diagnostics, Developer
            case "help.logs.viewer": card = Embed.Comp(static () => new LogViewerCard()) with { Key = "privacy.log-viewer" }; return true;
            case "help.logs.detailLevel": card = Embed.Comp(static () => new DetailLevelGroup()) with { Key = "privacy.detail-level" }; return true;
            case "help.logs.files": card = LogFilesCard(row); return true;
            case "help.tools.capture": card = RealtimeCaptureExpander(row); return true;
            case "help.developer.mode": card = Embed.Comp(static () => new DeveloperGroup()) with { Key = "privacy.developer" }; return true;
        }
        if (TryTopicOf(row.Id, out var topic)) { card = TopicRow(row, topic); return true; }
        if (row.Id.StartsWith("storage.", StringComparison.Ordinal) && TryStorageCard(row, props.Storage, out card)) return true;
        if (row.Id.StartsWith("help.", StringComparison.Ordinal) && TryAboutCard(row, group, out card)) return true;
        card = null;
        return false;
    }

    /// <summary>The item Classic draws for a child dial inside a generic expander; false for the generic item.</summary>
    static bool TryCustomItem(SettingsBodyRow row, out Element? item)
    {
        item = row.Id switch
        {
            "sound.normalization.mode" => NormalizationModeItem(row),
            "lyrics.stage.spectrum" => SpectrumItem(row),
            "lyrics.stage.heroDim" => Item(row.Label, row.Description, Badged(row, Embed.Comp(static () => new StageDimSlider())),
                isEnabled: row.Enabled, icon: GlyphOf(row.Dial)),
            "notifications.quiet.from" or "notifications.quiet.to" => QuietHourItem(row),
            "sound.timecode.level" => TimecodeSliderItem(row, s_timecodeLevelOptions),
            "sound.timecode.offset" => TimecodeSliderItem(row, s_timecodeOffsetOptions),
            _ => null,
        };
        return item is not null;
    }

    /// <summary>The signed-in account: the name as the row's title, the plan as its description (the old Account card's one row; the plan dial rides it).</summary>
    static Element IdentityRow(SettingsBodyRow row, SettingsBodyGroup? group)
    {
        string name = row.Value as string ?? "";
        int tier = 0;
        if (group is not null)
            foreach (SettingsBodyRow sibling in group.Rows)
                if (string.Equals(sibling.Id, "account.plan", StringComparison.Ordinal) && sibling.Value is int t) tier = t;
        string? plan = tier == 0 ? null : Loc.Get(tier == 2 ? Strings.Auth.PremiumBadge : Strings.Auth.FreeBadge);
        return Row(name.Length > 0 ? name : row.Label, plan, null, GlyphOf(row.Dial));
    }
}
