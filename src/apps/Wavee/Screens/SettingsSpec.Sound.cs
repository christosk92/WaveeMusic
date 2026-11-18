// ── Screens/SettingsSpec.Sound.cs — Sound: what you hear (the dials and their verbs) ───────────────────────────────────────────
//
// Role: CORE (settings rework phase 1). Quality (the streaming ladder with the D7 lossless rung, the metered cap) · Loudness and
// volume (normalization, remember volume) · Equalizer (the curve bound to the live DSP) · Output and Connect (the output device the
// device picker sets, this PC's Connect name).
//
// THE LIVE DSP. Every equalizer write persists and then pushes the WHOLE state to `Playback.Audio` (`SetEqualizer`), so the ear follows
// the curve while it is dragged; normalization goes through the `Settings.ApplyNormalization` seam until the audio host exposes its own
// setter. The views keep only the mirrors of their frozen controls (a combo's selection signal); the persistence and the push are these verbs.

using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsSpec
{
    static SettingsCategory SoundCategory() => new("sound", Strings.Settings.Categories.Sound.Title,
        Strings.Settings.Categories.Sound.Strip, Strings.Settings.Categories.Sound.Subtitle, "Speakers",
    [
        Group("quality", Strings.Settings.Groups.Sound.Quality, null,
            Make("sound.quality", K.Choice, Strings.Settings.Playback.AudioQuality,
                SettingsWriter.OfChoice("audio.quality", SettingsVerbs.SetAudioQuality), legacy: ["audioQuality"], glyph: "Headphones",
                captionKey: Strings.Settings.Playback.AudioSub,
                value: s => Settings.Quality.AudioIndex(s.Get(Platform.Keys.PlaybackQuality), Spotify.Audio.CanDerive)),
            // The description IS the detector's verdict (a live sentence, `Settings.MeteredStatusLine`): every state says something,
            // "Unknown" included. The view words it; the dial carries no static caption.
            Make("sound.meteredQuality", K.Choice, Strings.Settings.Playback.MeteredQuality,
                SettingsWriter.OfChoice("audio.meteredCap", SettingsVerbs.SetMeteredQuality), legacy: ["meteredQuality"], glyph: "RadioTower",
                value: _ => Platform.Network.MeteredQualityCap.Peek())),

        Group("loudness", Strings.Settings.Groups.Sound.Loudness, null,
            // G-132 / D5: the switch, the mode and album mode all apply LIVE to the playing track.
            Make("sound.normalization", K.Toggle, Strings.Settings.Playback.Normalization,
                SettingsWriter.OfToggle("audio.normalization", on => SettingsVerbs.SetNormalizationToggle(Platform.Keys.NormalizationEnabled, on)),
                legacy: ["normalization"], glyph: "Equalizer",
                captionKey: Strings.Settings.Playback.NormalizationSub, value: BoolOf(Platform.Keys.NormalizationEnabled)),
            Make("sound.normalization.mode", K.Choice, Strings.Settings.Playback.NormalizationMode,
                SettingsWriter.OfChoice("audio.normalizationMode", SettingsVerbs.SetNormalizationMode),
                legacy: ["normalizationMode"], parent: "sound.normalization", glyph: "Speakers",
                captionKey: Strings.Settings.Playback.NormalizationModeSub,
                value: s => Math.Clamp(s.Get(Platform.Keys.NormalizationMode), 0, 2)),
            Make("sound.normalization.album", K.Toggle, Strings.Settings.Playback.NormalizationAlbum,
                SettingsWriter.OfToggle("audio.normalizationAlbum", on => SettingsVerbs.SetNormalizationToggle(Platform.Keys.NormalizationAlbum, on)),
                legacy: ["normalizationAlbum"], parent: "sound.normalization", glyph: "Album",
                captionKey: Strings.Settings.Playback.NormalizationAlbumSub, value: BoolOf(Platform.Keys.NormalizationAlbum)),
            Make("sound.rememberVolume", K.Toggle, Strings.Settings.Playback.RememberVolume,
                SettingsWriter.OfToggle("audio.rememberVolume", WriteBool(Platform.Keys.RememberVolume)), legacy: ["rememberVolume"], glyph: "Volume",
                captionKey: Strings.Settings.Playback.RememberVolumeSub, value: BoolOf(Platform.Keys.RememberVolume))),

        Group("equalizer", Strings.Settings.Groups.Sound.Equalizer, null,
            // The expander's header switch; the items below grey with it, and say nothing more about why.
            Make("sound.equalizer", K.Toggle, Strings.Settings.Sound.Equalizer,
                SettingsWriter.OfToggle("eq.enabled", SettingsVerbs.SetEqualizerEnabled), legacy: ["equalizer"], glyph: "Equalizer",
                captionKey: Strings.Settings.Sound.EqualizerSub, value: BoolOf(Platform.Keys.EqualizerEnabled)),
            Make("sound.equalizer.preset", K.Choice, Strings.Settings.Sound.Preset,
                SettingsWriter.OfChoice("eq.preset", SettingsVerbs.ApplyEqPreset), legacy: ["eqPreset"], parent: "sound.equalizer",
                value: s => Settings.Eq.PresetIndex(s.Get(Platform.Keys.EqualizerPreset)),
                enabled: EqOn, silent: true),
            // The curve reports every pointer sample: (band, gain) — the owning verb is `SettingsVerbs.SetEqBand`.
            Make("sound.equalizer.curve", K.Composite, Strings.Settings.Sound.Curve,
                SettingsWriter.Owned("SettingsVerbs.SetEqBand"), legacy: ["eqCurve"], parent: "sound.equalizer",
                caption: f => f.EqualizerEnabled ? Strings.Settings.Sound.CurveOn : Strings.Settings.Sound.CurveOff,
                enabled: EqOn, silent: true),
            Make("sound.equalizer.reset", K.Action, Strings.Settings.Sound.ResetCurve,
                SettingsWriter.OfAction("eq.reset", SettingsVerbs.ResetEqCurve), legacy: ["eqReset"], parent: "sound.equalizer",
                enabled: EqOn, silent: true)),

        Group("output", Strings.Settings.Groups.Sound.Output, null,
            // The output device: the device picker's this-computer rows (index 0 is the system default, then the endpoints in roster
            // order), written through the picker's own door. Choosing it here changes the output; it does not pull playback here.
            Make("sound.outputDevice", K.Choice, Strings.Settings.Playback.OutputDevice,
                SettingsWriter.OfChoice("audio.outputDevice", SettingsVerbs.SetOutputDevice), glyph: "Speakers",
                captionKey: Strings.Settings.Playback.OutputDeviceSub, value: s => SettingsVerbs.OutputDeviceIndex(s.Get(Platform.Keys.OutputDeviceId)),
                contexts: [SettingsSurfaces.DevicePicker]),
            Make("sound.connectName", K.Fact, Strings.Settings.Devices.ThisPc, SettingsWriter.ReadOnly, legacy: ["thisPc"],
                captionKey: Strings.Settings.Devices.ThisPcSub, value: _ => Playback.DeviceName)),
    ]);

    /// <summary>The curve, the preset and the reset follow the equalizer's switch (greyed, no reason of their own).</summary>
    static SettingsEnablement EqOn(ISettingsFacts f) => f.EqualizerEnabled ? SettingsEnablement.On : SettingsEnablement.Off(null);
}

public static partial class SettingsVerbs
{
    // ── Quality ──────────────────────────────────────────────────────────────────────────────────────────────────────

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

    // ── Loudness ─────────────────────────────────────────────────────────────────────────────────────────────────────

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

    // ── The equalizer ────────────────────────────────────────────────────────────────────────────────────────────────

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

    // ── Output ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The output device at <paramref name="index"/> (0 the system default, then the endpoints in roster order), through the
    /// device picker's door (<see cref="Shell.DeviceVerbs.Run"/>): it persists the output and routes the next local audio to it. An
    /// index past the roster (it changed under a stale view) writes nothing.</summary>
    public static bool SetOutputDevice(int index)
    {
        var devices = Playback.Audio.Devices.Peek();
        if ((uint)index > (uint)devices.Length) return false;
        string id = index == 0 ? "" : devices[index - 1].Id;
        Shell.DeviceVerbs.Run(new Shell.DeviceIntent(Shell.DeviceIntentKind.SelectLocal, LocalId: id));
        return true;
    }

    /// <summary>The output device's index in the roster (0 the system default, and an endpoint no longer in the roster).</summary>
    public static int OutputDeviceIndex(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return 0;
        var devices = Playback.Audio.Devices.Peek();
        for (int i = 0; i < devices.Length; i++)
            if (string.Equals(devices[i].Id, deviceId, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }
}
