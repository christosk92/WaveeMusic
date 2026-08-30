using System;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Backend.Audio;
using Wavee.Core;
using Wavee.Sdk.Streams;

namespace Wavee;

/// <summary>Every settings write the six non-phase setup pages make, in one auditable file. Each writer mirrors the
/// shipped Settings-tab writer body it corresponds to (named in its own doc comment below) MINUS
/// <c>SettingsPage</c>'s local <c>_uiEpoch</c> bump, which has no equivalent here — the setup pages re-render off
/// their own page-local state, never a page-wide epoch signal.
///
/// <para>Callers pass a non-null <c>IAppSettings</c> — every setup page already null-guards
/// <c>UseContext(Services.Slot)?.Settings</c> before wiring a control's <c>onChange</c>, the same convention
/// <c>SettingsPage</c> itself uses.</para></summary>
static class SetupWrites
{
    // ── Appearance (page 4) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mirrors <c>SettingsPage.GeneralTab</c>'s <c>SetTheme</c>.</summary>
    public static void SetThemeMode(int mode, IAppSettings settings, Action<float>? requestTheme)
    {
        WaveeTheme.ApplyThemeMode(mode, settings);
        requestTheme?.Invoke(250f);
    }

    /// <summary>Mirrors <c>SettingsPage.PaletteRow</c>'s apply lambda.</summary>
    public static void SetPalette(string id, IAppSettings settings, Action<float>? requestTheme)
    {
        WaveeTheme.ApplyPalette(id, settings);
        requestTheme?.Invoke(250f);
    }

    /// <summary>Mirrors <c>SettingsPage.GeneralTab</c>'s <c>SetWindowMaterial</c>. The picker index is INVERTED
    /// relative to the stored bool (SettingsPage.General.cs:~68-76): index 0 = base Mica = TRUE; index 1 = Mica Alt
    /// = FALSE — the picker orders base Mica first (the default), while the setting stores "is it base Mica" rather
    /// than "is it the alt", so the two run backwards from each other.</summary>
    public static void SetWindowMaterial(int selectorIndex, IAppSettings settings)
    {
        bool baseMica = selectorIndex == 0;
        settings.Set(WaveeSettings.WindowMaterialBaseMica, baseMica);
        FluentApp.SetWindowMaterialAlt(!baseMica);   // live, no restart
    }

    /// <summary>Mirrors <c>SettingsPage.GeneralTab</c>'s <c>SetDensity</c> — with one deliberate addition: the shipped
    /// row does NOT bump <see cref="AppearancePrefs"/> (a known, already-tracked live-preview gap; a later step
    /// fixes the consumer side), but the setup wizard's whole premise is that a choice previews live, so this writer
    /// bumps it.</summary>
    public static void SetRowDensity(int index, IAppSettings settings)
    {
        settings.Set(WaveeSettings.RowDensity, index);
        AppearancePrefs.Bump();
    }

    /// <summary>Mirrors <c>SettingsPage.GeneralTab</c>'s <c>SetTrackListStyle</c> (0 Modern · 1 Classic) — the choice
    /// Appearance never asked before this pass (<c>SetupWrites.SetPalette</c> had the same gap, fixed alongside it).</summary>
    public static void SetTrackRowStyle(int index, IAppSettings settings)
    {
        if ((uint)index >= 2u) return;
        settings.Set(WaveeSettings.TrackRowStyle, index);
        AppearancePrefs.Bump();
    }

    /// <summary>Mirrors <c>SettingsPage.AppearanceToggle</c> — every boolean appearance flag (DisableMarquee,
    /// DisableColorWashes, DetailPageToneHeroOnly, LyricsAnimatedBackdrop) goes through this one writer.</summary>
    public static void SetAppearanceFlag(SettingKey<bool> key, bool value, IAppSettings settings)
    {
        settings.Set(key, value);
        AppearancePrefs.Bump();
    }

    /// <summary>Mirrors <c>SettingsPage.GeneralTab</c>'s <c>SetPageLayout</c>.</summary>
    public static void SetDetailPageLayout(int index, IAppSettings settings)
    {
        settings.Set(WaveeSettings.DetailPageLayout, index);
        DetailHeroPrefs.Bump();
    }

    // ── Sidebar (page 5) ─────────────────────────────────────────────────────────────────────────────────────────
    // The design switch itself is NOT here: SidebarDesignPicker.Row already applies through
    // SidebarPreferences.SwitchDesign (or the settings fallback) on its own — this file only owns the template pick.

    /// <summary>Apply a starting-point template. Callers MUST only offer this when the live document is still
    /// "pristine" for its own template (<c>SidebarLayoutCompare.EqualTemplateSectionsIgnoringIds</c> against
    /// <c>SidebarTemplates.Build(prefs.Layout.TemplateId)</c>) — see the page's own doc comment for the re-run
    /// safety rule. Goes through the same public sugar the customizer's own template buttons use
    /// (<c>SidebarPreferences.ApplyTemplateId</c> → <c>Dispatch(new ApplyTemplate(id))</c>).</summary>
    public static void ApplySidebarTemplate(string templateId, SidebarPreferences prefs) => prefs.ApplyTemplateId(templateId);

    // ── Sound & storage (page 6) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mirrors <c>SettingsPage.QualityCombo</c>'s apply lambda.</summary>
    public static void SetPlaybackQuality(int index, IAppSettings settings)
    {
        if (index < 0 || index > 2) return;   // Lossless (index 3) is offered disabled — never reachable here
        settings.Set(WaveeSettings.PlaybackQuality, index);
    }

    /// <summary>Mirrors <c>SettingsPage.MeteredQualityCombo</c>'s apply lambda.</summary>
    public static void SetMeteredQualityCap(int index, IAppSettings settings)
    {
        if (index < 0 || index > 2) return;
        settings.Set(WaveeSettings.MeteredQualityCap, index);
    }

    /// <summary>Mirrors <c>SettingsPage.CrossfadeGroup</c>'s toggle writer, plus the <see cref="PlaybackDsp"/> push
    /// the shipped Settings row does — both now call the SAME <see cref="PlaybackDsp.Push"/>, so the wizard and
    /// Settings cannot drift apart on how the DSP is fed.</summary>
    public static void SetCrossfadeEnabled(bool enabled, IAppSettings settings, Services? svc)
    {
        settings.Set(WaveeSettings.CrossfadeEnabled, enabled);
        PlaybackDsp.Push(svc);
    }

    /// <summary>Mirrors <c>SettingsPage.CrossfadeGroup</c>'s <c>Commit</c> (duration editor).</summary>
    public static void SetCrossfadeSeconds(double seconds, IAppSettings settings, Services? svc)
    {
        int ms = (int)MathF.Round((float)Math.Clamp(seconds, 0, 12) * 1000f);
        settings.Set(WaveeSettings.CrossfadeMs, ms);
        PlaybackDsp.Push(svc);
    }

    /// <summary>Mirrors <c>SettingsPage.EqualizerGroup</c>'s toggle writer.</summary>
    public static void SetEqualizerEnabled(bool enabled, IAppSettings settings, Services? svc)
    {
        settings.Set(WaveeSettings.EqualizerEnabled, enabled);
        PlaybackDsp.Push(svc);
    }

    /// <summary>Apply one of the setup page's named EQ curves and push it to the live DSP.</summary>
    public static void SetEqualizerPreset(string id, float[] gains, IAppSettings settings, Services? svc)
    {
        settings.Set(WaveeSettings.EqualizerPreset, id);
        settings.Set(WaveeSettings.EqualizerGains, PlaybackDsp.SerializeEqGains(gains));
        PlaybackDsp.Push(svc);
    }

    /// <summary>Commit one EQ band while preserving the other nine persisted gains.</summary>
    public static void SetEqualizerBand(int band, float gain, IAppSettings settings, Services? svc)
    {
        if ((uint)band >= 10u) return;
        var gains = PlaybackDsp.ReadEqGains(settings);
        gains[band] = Math.Clamp(gain, -12f, 12f);
        settings.Set(WaveeSettings.EqualizerGains, PlaybackDsp.SerializeEqGains(gains));
        settings.Set(WaveeSettings.EqualizerPreset, "custom");
        PlaybackDsp.Push(svc);
    }

    /// <summary>Mirrors <c>SettingsPage.BudgetControl</c>'s <c>SetMode</c> — including the cache <c>Trim()</c> the
    /// shipped row runs on every mode change.</summary>
    public static void SetAudioBodyCacheBudgetMode(int mode, IAppSettings settings, Services? svc)
    {
        settings.Set(WaveeSettings.AudioBodyCacheBudgetMode, Math.Clamp(mode, 0, 2));
        svc?.AudioBodyCache?.Trim();
    }

    /// <summary>Mirrors <c>SettingsPage.MetadataBudgetControl</c>'s apply lambda, including the live GC/cold-store
    /// budget push.</summary>
    public static void SetMetadataCacheBudgetBytes(long bytes, IAppSettings settings, Services? svc)
    {
        settings.Set(WaveeSettings.MetadataCacheBudgetBytes, bytes);
        if (svc?.CacheGc is { } gc) gc.BudgetBytes = bytes;
        else svc?.RealCold?.SetCacheBudgetBytes(bytes);
    }

    public static void SetAudioBodyCacheEnabled(bool enabled, IAppSettings settings)
        => settings.Set(WaveeSettings.AudioBodyCacheEnabled, enabled);

    public static void SetAudioKeyCacheEnabled(bool enabled, IAppSettings settings)
        => settings.Set(WaveeSettings.AudioKeyCacheEnabled, enabled);

    /// <summary>"Choose location". Mirrors <c>SettingsPage.PickCacheLocation</c> → <c>BeginCacheRelocation</c>, MINUS
    /// the move/start-empty confirmation dialog: a confirm nested inside the wizard's own modal overlay is exactly
    /// the "nested confirm" mistake this file's Sidebar page is careful to avoid, so this always moves the existing
    /// cache (first-run, it is normally empty anyway) rather than asking. Settings → Storage keeps the full
    /// move/start-empty choice for anyone who wants it later.</summary>
    public static void ChooseCacheLocation(Services? svc, IAppSettings settings, Action<Action> post)
    {
        if (svc?.AudioBodyCache is null) return;
        string? selected = FilePicker.PickFolder(FluentApp.WindowHandle, Loc.Get(Strings.Settings.Storage.ChooseCacheFolder));
        if (string.IsNullOrWhiteSpace(selected)) return;
        var cache = svc!.AudioBodyCache!;
        _ = Task.Run(async () =>
        {
            bool ok = await cache.PrepareRelocationAsync(selected, AudioCacheRelocationMode.Move).ConfigureAwait(false);
            post(() =>
            {
                if (ok) settings.Set(WaveeSettings.AudioBodyCacheBasePath, selected);
            });
        });
    }

    // ── Notifications & links (page 7) ──────────────────────────────────────────────────────────────────────────

    /// <summary>Mirrors <c>SettingsPage.Toggle</c> for <c>NotifyWindows</c> (the master gate) — including the
    /// scheduled-toast reconcile the shipped row runs via its <c>afterWrite</c> hook.</summary>
    public static void SetNotifyWindows(bool enabled, IAppSettings settings)
    {
        settings.Set(WaveeSettings.NotifyWindows, enabled);
        NotificationPrefs.Bump();
        ReconcileScheduled();
    }

    /// <summary>Mirrors <c>SettingsPage.Toggle</c> for <c>NotifyQuietEnabled</c> + its hour combos.</summary>
    public static void SetQuietHours(bool enabled, int fromHour, int toHour, IAppSettings settings)
    {
        settings.Set(WaveeSettings.NotifyQuietEnabled, enabled);
        settings.Set(WaveeSettings.NotifyQuietFromHour, fromHour);
        settings.Set(WaveeSettings.NotifyQuietToHour, toHour);
        NotificationPrefs.Bump();
        ReconcileScheduled();
    }

    /// <summary>Mirrors <c>SettingsPage.TopicRow</c>'s dial writer. <c>NotificationPrefs.SetLevel</c> already clamps
    /// to <c>NotificationPolicy.CeilingFor(topic)</c> — <c>KeyFor</c> is private, this is the public seam.</summary>
    public static void SetTopicLevel(NotifyTopic topic, NotifyLevel level, IAppSettings settings)
    {
        NotificationPrefs.SetLevel(settings, topic, level);
        if (NotificationPolicy.IsScheduled(topic)) ReconcileScheduled();
    }

    /// <summary>Mirrors <c>SettingsPage.SpotifyLinksToggle</c>: the scheme association is applied AT THE TOGGLE, not
    /// at next launch.</summary>
    public static void SetHandleSpotifyLinks(bool enabled, IAppSettings settings)
    {
        settings.Set(WaveeSettings.HandleSpotifyLinks, enabled);
        DeepLink.SyncSpotifySchemeRegistration(enabled);
    }

    public static void SetUiCulture(string code, IAppSettings settings) => settings.Set(WaveeSettings.UiCulture, code);

    /// <summary>Mirrors <c>SettingsPage.ReconcileScheduled</c>: re-derive the OS-held scheduled set after any change
    /// that could alter it.</summary>
    static void ReconcileScheduled()
    {
        ReleaseNotifier.RequestReconcile();
        DaylistNotifier.RequestReconcile();
    }

    // ── "Decide for me" ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every non-phase page's sensible-default writer — a CHOICE, not a skip, mirroring the prototype's
    /// <c>decide(n)</c>. Called from <c>SetupSession.Secondary()</c> (a later step wires that call site;
    /// <c>SetupSession.cs</c> is out of scope for this step).</summary>
    public static void DecideFor(SetupPage page, IAppSettings settings, Action<float>? requestTheme)
    {
        switch (page)
        {
            case SetupPage.Appearance:
                SetThemeMode(0, settings, requestTheme);                          // System
                settings.Set(WaveeSettings.WindowMaterialBaseMica, true);
                FluentApp.SetWindowMaterialAlt(false);                            // base Mica
                SetPalette("neutral", settings, requestTheme);
                SetTrackRowStyle(0, settings);                                    // Modern
                SetRowDensity(1, settings);                                       // Default ("cozy" per the prototype's copy)
                SetAppearanceFlag(WaveeSettings.HideTrackArtwork, false, settings);
                // Visual effects: every flag already defaults to "on" (DisableMarquee/DisableColorWashes default
                // false, LyricsAnimatedBackdrop defaults true) — nothing to write for "all effects on".
                break;

            case SetupPage.Sidebar:
                // Spotify Classic is the shipped default. Custom remains unavailable during onboarding, and this
                // settings-only path must never replace an existing custom document as a side effect.
                settings.Set(WaveeSettings.SidebarDesign, (int)SidebarDesign.Classic);
                break;

            case SetupPage.Sound:
                settings.Set(WaveeSettings.PlaybackQuality, 2);                          // Very High
                settings.Set(WaveeSettings.MeteredQualityCap, 1);                        // High
                settings.Set(WaveeSettings.CrossfadeEnabled, false);
                settings.Set(WaveeSettings.AudioBodyCacheBudgetMode, (int)AudioCacheBudgetMode.DriveShare);
                break;

            case SetupPage.Notifications:
                settings.Set(WaveeSettings.NotifyWindows, false);
                foreach (var topic in NotificationPrefs.AllTopics)
                    NotificationPrefs.SetLevel(settings, topic, NotificationPolicy.DefaultFor(topic));
                settings.Set(WaveeSettings.HandleSpotifyLinks, false);
                break;
        }
    }
}
