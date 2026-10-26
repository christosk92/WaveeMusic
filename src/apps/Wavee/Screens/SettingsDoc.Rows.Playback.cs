// ── Screens/SettingsDoc.Rows.Playback.cs — the Playback tab as document rows + their verbs ─────────────────────────
//
// Role: CORE (PR C13; extraction X36). The runtime card's doors · Audio (quality with the D7 lossless rung, the metered cap,
// remember volume, autoplay, normalization) · Sound (the equalizer curve, crossfade — both bound to the live DSP) · Video
// (quality, the metered cap, prepare-ahead, the attached-videos row) · Player bar.
//
// THE LIVE DSP. Every equalizer / crossfade write persists and then pushes the WHOLE state to `Playback.Audio`
// (`SetEqualizer`, `SetCrossfade`), so the ear follows the curve while it is dragged; normalization goes through the
// `Settings.ApplyNormalization` seam until the audio host exposes its own setter. The views keep only the mirrors of their
// frozen controls (a combo's selection signal); the persistence and the push are these verbs.

using FluentGpu.Controls;
using FluentGpu.Localization;
using T = Wavee.Settings.Tab;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsDoc
{
    static IReadOnlyList<SettingsSection> PlaybackSections() =>
    [
        // The runtime card has no section header of its own; its two doors are rows so a theme can draw them anywhere.
        Sect(T.Playback, "Runtime", Strings.Settings.Playback.RuntimeNotSetUp, null,
            Make(T.Playback, "Runtime", "runtimeSetup", K.Action, Strings.Playback.Runtime.SetUp,
                SettingsWriter.OfAction("runtime.requestOpen", SettingsVerbs.RequestRuntimeSetup), catalog: false),
            Make(T.Playback, "Runtime", "runtimeSignature", K.Action, Strings.Playback.Runtime.ViewSignature,
                SettingsWriter.OfAction("runtime.viewSignature", SettingsVerbs.ViewRuntimeSignature), catalog: false)),

        // The board's playback section: what the queue does by itself (remember the volume, autoplay, crossfade). How it SOUNDS is Audio.
        Sect(T.Playback, "Playback", Strings.Settings.Tabs.Playback, null,
            Make(T.Playback, "Playback", "rememberVolume", K.Toggle, Strings.Settings.Playback.RememberVolume,
                SettingsWriter.OfToggle("audio.rememberVolume", WriteBool(Platform.Keys.RememberVolume)),
                captionKey: Strings.Settings.Playback.RememberVolumeSub, value: BoolOf(Platform.Keys.RememberVolume)),
            Make(T.Playback, "Playback", "autoplay", K.Toggle, Strings.Settings.Playback.Autoplay,
                SettingsWriter.OfToggle("audio.autoplay", WriteBool(Platform.Keys.AutoplayEnabled)),
                captionKey: Strings.Settings.Playback.AutoplaySub, value: BoolOf(Platform.Keys.AutoplayEnabled)),
            Make(T.Playback, "Playback", "crossfade", K.Toggle, Strings.Settings.Sound.Crossfade,
                SettingsWriter.OfToggle("crossfade.enabled", SettingsVerbs.SetCrossfadeEnabled),
                captionKey: Strings.Settings.Sound.CrossfadeSub, value: BoolOf(Platform.Keys.CrossfadeEnabled)),
            // The slider and the number box commit through ONE verb (seconds; the verb snaps to a millisecond).
            Make(T.Playback, "Playback", "crossfadeDuration", K.Slider, Strings.Settings.Sound.CrossfadeDuration,
                SettingsWriter.OfSlider("crossfade.duration", v => SettingsVerbs.CommitCrossfade(v)),
                value: s => (float)Settings.Quality.CrossfadeSeconds(s.Get(Platform.Keys.CrossfadeMs)),
                enabled: f => f.CrossfadeEnabled ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true, catalog: false)),

        Sect(T.Playback, "Audio", Strings.Settings.Playback.AudioTitle, null,
            Make(T.Playback, "Audio", "audioQuality", K.Choice, Strings.Settings.Playback.AudioQuality,
                SettingsWriter.OfChoice("audio.quality", SettingsVerbs.SetAudioQuality),
                captionKey: Strings.Settings.Playback.AudioSub,
                value: s => Settings.Quality.AudioIndex(s.Get(Platform.Keys.PlaybackQuality), Spotify.Audio.CanDerive)),
            // The description IS the detector's verdict (a live sentence, `Settings.MeteredStatusLine`): every state says
            // something, "Unknown" included. The view words it; the row carries no static caption.
            Make(T.Playback, "Audio", "meteredQuality", K.Choice, Strings.Settings.Playback.MeteredQuality,
                SettingsWriter.OfChoice("audio.meteredCap", SettingsVerbs.SetMeteredQuality),
                value: _ => Platform.Network.MeteredQualityCap.Peek()),
            // G-132 / D5: the switch, the mode and album mode all apply LIVE to the playing track.
            Make(T.Playback, "Audio", "normalization", K.Toggle, Strings.Settings.Playback.Normalization,
                SettingsWriter.OfToggle("audio.normalization", on => SettingsVerbs.SetNormalizationToggle(Platform.Keys.NormalizationEnabled, on)),
                captionKey: Strings.Settings.Playback.NormalizationSub, value: BoolOf(Platform.Keys.NormalizationEnabled)),
            Make(T.Playback, "Audio", "normalizationMode", K.Choice, Strings.Settings.Playback.NormalizationMode,
                SettingsWriter.OfChoice("audio.normalizationMode", SettingsVerbs.SetNormalizationMode),
                captionKey: Strings.Settings.Playback.NormalizationModeSub,
                value: s => Math.Clamp(s.Get(Platform.Keys.NormalizationMode), 0, 2)),
            Make(T.Playback, "Audio", "normalizationAlbum", K.Toggle, Strings.Settings.Playback.NormalizationAlbum,
                SettingsWriter.OfToggle("audio.normalizationAlbum", on => SettingsVerbs.SetNormalizationToggle(Platform.Keys.NormalizationAlbum, on)),
                captionKey: Strings.Settings.Playback.NormalizationAlbumSub, value: BoolOf(Platform.Keys.NormalizationAlbum))),

        Sect(T.Playback, "Sound", Strings.Settings.Sound.Title, null,
            // The expander's header switch; the items below grey with it, and say nothing more about why.
            Make(T.Playback, "Sound", "equalizer", K.Toggle, Strings.Settings.Sound.Equalizer,
                SettingsWriter.OfToggle("eq.enabled", SettingsVerbs.SetEqualizerEnabled),
                captionKey: Strings.Settings.Sound.EqualizerSub, value: BoolOf(Platform.Keys.EqualizerEnabled)),
            Make(T.Playback, "Sound", "eqPreset", K.Choice, Strings.Settings.Sound.Preset,
                SettingsWriter.OfChoice("eq.preset", SettingsVerbs.ApplyEqPreset),
                value: s => Settings.Eq.PresetIndex(s.Get(Platform.Keys.EqualizerPreset)),
                enabled: EqOn, silent: true, catalog: false),
            // The curve reports every pointer sample: (band, gain) — the owning verb is `SettingsVerbs.SetEqBand`.
            Make(T.Playback, "Sound", "eqCurve", K.Composite, Strings.Settings.Sound.Curve,
                SettingsWriter.Owned("SettingsVerbs.SetEqBand"),
                caption: f => f.EqualizerEnabled ? Strings.Settings.Sound.CurveOn : Strings.Settings.Sound.CurveOff,
                enabled: EqOn, silent: true, catalog: false),
            Make(T.Playback, "Sound", "eqReset", K.Action, Strings.Settings.Sound.ResetCurve,
                SettingsWriter.OfAction("eq.reset", SettingsVerbs.ResetEqCurve), enabled: EqOn, silent: true, catalog: false)),

        Sect(T.Playback, "Video", Strings.VideoOverride.SettingsTitle, null,
            Make(T.Playback, "Video", "videoQuality", K.Choice, Strings.Settings.Playback.VideoQuality,
                SettingsWriter.OfChoice("video.quality", SettingsVerbs.SetVideoQuality),
                captionKey: Strings.Settings.Playback.VideoQualitySub, value: s => Settings.Quality.VideoIndex(s.Get(Platform.Keys.VideoQuality))),
            Make(T.Playback, "Video", "videoMetered", K.Choice, Strings.Settings.Playback.VideoMeteredQuality,
                SettingsWriter.OfChoice("video.meteredCap", SettingsVerbs.SetMeteredVideo),
                captionKey: Strings.Settings.Playback.VideoMeteredQualitySub,
                value: s => Settings.Quality.MeteredVideoIndex(s.Get(Platform.Keys.VideoMeteredMaxHeight))),
            // D15's off switch, DEFAULT ON; the write persists and re-decides the keeper's beat immediately.
            Make(T.Playback, "Video", "videoPrepareAhead", K.Toggle, Strings.Settings.Playback.VideoPrepareAhead,
                SettingsWriter.OfToggle("video.prepareAhead", SettingsVerbs.SetVideoPrepareAhead),
                captionKey: Strings.Settings.Playback.VideoPrepareAheadSub, value: BoolOf(Playback.Video.PrepareAhead)),
            // "Clear all" is the ONE confirmed verb (N times, no per-row undo); no roster store → the whole row greys, no control.
            Make(T.Playback, "Video", "videoOverrides", K.Action, Strings.VideoOverride.SettingsHeader,
                SettingsWriter.OfAction("video.overrides.clearAll", SettingsVerbs.ClearAllVideoOverrides),
                captionKey: Strings.VideoOverride.SettingsSub,
                enabled: f => f.VideoOverridesPresent ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true,
                confirm: new SettingsConfirm(Strings.VideoOverride.ClearAll, Strings.VideoOverride.ClearAllBody, Strings.VideoOverride.ClearAll)),
            // The manager flyout's per-item verbs (replace · locate · show in Explorer · remove) are `SettingsVerbs` members.
            Make(T.Playback, "Video", "videoOverrideItem", K.Composite, Strings.VideoOverride.Manage,
                SettingsWriter.Owned("SettingsVerbs.RemoveVideoOverride/PickVideoOverride/RevealInExplorer"), catalog: false)),

        Sect(T.Playback, "Player bar", Strings.Settings.Playback.PlayerBar, null,
            Make(T.Playback, "Player bar", "playerBarRemaining", K.Toggle, Strings.Settings.Playback.ShowRemaining,
                SettingsWriter.OfToggle("playerBar.remaining", WriteBool(Platform.Keys.PlayerBarShowRemaining, _ => Prefs.PlayerBar.Bump())),
                captionKey: Strings.Settings.Playback.ShowRemainingSub, value: BoolOf(Platform.Keys.PlayerBarShowRemaining))),
    ];

    /// <summary>The curve, the preset and the reset follow the equalizer's switch (greyed, no reason of their own).</summary>
    static SettingsEnablement EqOn(ISettingsFacts f) => f.EqualizerEnabled ? SettingsEnablement.On : SettingsEnablement.Off(null);
}

public static partial class SettingsVerbs
{
    // ── Runtime ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Manage" / "Set up" / "Retry setup": open the playback-runtime setup card.</summary>
    public static bool RequestRuntimeSetup()
    {
        Setup.Runtime.RequestOpen();
        return false;
    }

    /// <summary>"View signature": the digital-signature sheet, over the Settings page's overlay host.</summary>
    public static bool ViewRuntimeSignature()
    {
        if (SettingsContext.Overlay is { } overlay) Shell.OpenSignatureDialog(overlay, Setup.Runtime.Status.Peek());
        return false;
    }

    // ── Audio ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>D7: Lossless is OFFERED only when a key deriver is installed — a rung that is not offered is not written.</summary>
    public static bool SetAudioQuality(int index)
    {
        if (!Settings.Quality.IsWritableAudio(index, Spotify.Audio.CanDerive)) return false;
        Platform.Settings.Set(Platform.Keys.PlaybackQuality, index);
        return true;
    }

    /// <summary>The metered cap offers the same ladder (flac plan §5.4), under the same D7 gate. Persist, then publish.</summary>
    public static bool SetMeteredQuality(int index)
    {
        if ((uint)index >= (uint)Settings.Quality.MeteredRungCount(Spotify.Audio.CanDerive)) return false;
        Platform.Network.SetMeteredQualityCap(index);
        return true;
    }

    /// <summary>The normalization switch and album mode: persist, then re-ramp the playing voices (null seam → the next open reads it).</summary>
    public static bool SetNormalizationToggle(SettingKey<bool> key, bool on)
    {
        Platform.Settings.Set(key, on);
        Settings.ApplyNormalization?.Invoke();
        return true;
    }

    /// <summary>Quiet · Normal · Loud. Applies live; the page does not re-read.</summary>
    public static bool SetNormalizationMode(int index)
    {
        Platform.Settings.Set(Platform.Keys.NormalizationMode, index);
        Settings.ApplyNormalization?.Invoke();
        return false;
    }

    // ── Sound: the equalizer ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The whole EQ state to the live graph: a gain-only change ramps; an on/off flip changes the topology.</summary>
    public static void PushEqualizer()
        => Playback.Audio.SetEqualizer(Platform.Settings.Get(Platform.Keys.EqualizerEnabled), Settings.Eq.ReadGains(Platform.Settings));

    public static bool SetEqualizerEnabled(bool on)
    {
        Platform.Settings.Set(Platform.Keys.EqualizerEnabled, on);
        PushEqualizer();
        return true;
    }

    /// <summary>One band from the curve (drag, click or keys): snap to half a decibel; a pointer sample that lands on the
    /// band's current value writes nothing.</summary>
    public static bool SetEqBand(int band, float gain)
    {
        if ((uint)band >= (uint)Settings.Eq.BandCount) return false;
        var gains = Settings.Eq.ReadGains(Platform.Settings);
        float snapped = Settings.Eq.SnapGain(gain);
        if (gains[band] == snapped) return false;
        gains[band] = snapped;
        Platform.Settings.Set(Platform.Keys.EqualizerGains, Settings.Eq.SerializeGains(gains));
        PushEqualizer();
        return true;
    }

    /// <summary>A preset: its id and its gain vector, pushed to the live graph.</summary>
    public static bool ApplyEqPreset(int index)
    {
        int idx = Math.Clamp(index, 0, Settings.Eq.PresetIds.Length - 1);
        Platform.Settings.Set(Platform.Keys.EqualizerPreset, Settings.Eq.PresetIds[idx]);
        Platform.Settings.Set(Platform.Keys.EqualizerGains, Settings.Eq.SerializeGains(Settings.Eq.PresetGains[idx]));
        PushEqualizer();
        return true;
    }

    public static bool ResetEqCurve() => ApplyEqPreset(0);

    // ── Sound: crossfade ─────────────────────────────────────────────────────────────────────────────────────────────

    public static void PushCrossfade()
        => Playback.Audio.SetCrossfade(Platform.Settings.Get(Platform.Keys.CrossfadeEnabled), Platform.Settings.Get(Platform.Keys.CrossfadeMs));

    public static bool SetCrossfadeEnabled(bool on)
    {
        Platform.Settings.Set(Platform.Keys.CrossfadeEnabled, on);
        PushCrossfade();
        return true;
    }

    /// <summary>The slider and the number box commit here: <c>round(clamp(s, 0, 12) × 1000)</c> ms, persisted and pushed only
    /// when the millisecond value actually moved (a slider drag reports every sample).</summary>
    public static bool CommitCrossfade(double seconds)
    {
        int ms = Settings.Quality.CrossfadeMs(seconds);
        if (Platform.Settings.Get(Platform.Keys.CrossfadeMs) == ms) return false;
        Platform.Settings.Set(Platform.Keys.CrossfadeMs, ms);
        PushCrossfade();
        return true;
    }

    // ── Video ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Stored as a height (0 = Auto): persisted, then the live pin — the ABR ceiling and the engine's selection.</summary>
    public static bool SetVideoQuality(int index)
    {
        if ((uint)index >= (uint)Settings.Quality.VideoHeights.Length) return false;
        Playback.Video.PinQuality(Settings.Quality.VideoHeights[index]);
        return true;
    }

    /// <summary>Persisted and published through the network policy; the video session folds it on its next quality decision.</summary>
    public static bool SetMeteredVideo(int index)
    {
        if ((uint)index >= (uint)Settings.Quality.MeteredVideoHeights.Length) return false;
        Platform.Network.SetMeteredVideoMaxHeight(Settings.Quality.MeteredVideoHeights[index]);
        return true;
    }

    /// <summary>D15: persist, then re-decide the keeper's beat now rather than at the next track.</summary>
    public static bool SetVideoPrepareAhead(bool on)
    {
        Platform.Settings.Set(Playback.Video.PrepareAhead, on);
        Playback.Video.ApplyPrepareAhead();
        return true;
    }

    /// <summary>Detach every attached video (confirmed first): one log line and one toast.</summary>
    public static bool ClearAllVideoOverrides()
    {
        var all = Video.Overrides.All();
        int removed = 0;
        for (int i = 0; i < all.Count; i++)
            if (Video.Overrides.Remove(all[i].Uri)) removed++;
        Log.Event(WaveeLogLevel.Info, "ui", "override.settings.clear_all", "detached every attached video",
            fields: [WaveeLogField.Of("count", removed)]);
        Notify.Say(Loc.Get(Strings.VideoOverride.ClearedAll), InfoBarSeverity.Success);
        return false;
    }

    /// <summary>The manager's "Remove": detach one video — applied at once, with a toast carrying Undo, never a dialog.</summary>
    public static void RemoveVideoOverride(string uri, string path, string sourceKey)
    {
        if (!Video.Overrides.Remove(uri)) return;
        Log.Event(WaveeLogLevel.Info, "ui", "override.settings.remove", "detached the attached video",
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", path)]);
        Notify.Say(Loc.Get(Strings.VideoOverride.Removed), InfoBarSeverity.Success, Loc.Get(Strings.VideoOverride.Undo),
            () =>
            {
                if (Video.Overrides.Attach(uri, path, sourceKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is null)
                    Notify.Say(Loc.Get(Strings.VideoOverride.RejectedNotFound), InfoBarSeverity.Error);
            });
    }

    /// <summary>The manager's "Replace" / "Locate": pick, validate (a refusal is an ERROR toast), attach, success toast. Locate
    /// spells the deepest surviving folder into the dialog caption (<paramref name="start"/>).</summary>
    public static void PickVideoOverride(string uri, string sourceKey, string title, string? start)
    {
        var filter = Video.OverrideUx.PickerFilter(Loc.Get(Strings.VideoOverride.Filter));
        Pickers.Pick(PickerRequest.Open(start is null ? title : title + " — " + start, filter),
            done: picked => { if (picked is not null) PickedVideoOverride(uri, sourceKey, picked); },
            failed: ex => Notify.Say(ex.Message, InfoBarSeverity.Error));
    }

    /// <summary><see cref="PickVideoOverride"/>'s answer, on the UI thread once the dialog (its own thread) has closed.</summary>
    public static void PickedVideoOverride(string uri, string sourceKey, string picked)
    {
        var rejection = Video.OverrideUx.Validate(picked, Video.Overrides.FileExists);
        if (rejection != Video.AttachRejection.None)
        {
            Notify.Say(Loc.Get(rejection == Video.AttachRejection.NotMp4 ? Strings.VideoOverride.RejectedNotMp4 : Strings.VideoOverride.RejectedNotFound), InfoBarSeverity.Error);
            Log.Event(WaveeLogLevel.Warning, "ui", "override.attach.rejected", "the picked file was refused",
                fields: [WaveeLogField.Of("path", picked), WaveeLogField.Of("reason", rejection == Video.AttachRejection.NotMp4 ? "not-mp4" : "not-found")]);
            return;
        }
        if (Video.Overrides.Attach(uri, picked, sourceKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is null) return;
        Log.Event(WaveeLogLevel.Info, "ui", "override.settings.replace", "replaced the attached video",
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", picked)]);
        Notify.Say(Loc.Get(Strings.VideoOverride.Replaced), InfoBarSeverity.Success);
    }

    /// <summary>"Show in Explorer": select the file. Fail-soft (logged).</summary>
    public static void RevealInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("ui", "show in explorer failed", ex); }
    }
}
