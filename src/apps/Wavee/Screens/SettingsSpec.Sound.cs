// ── Screens/SettingsSpec.Sound.cs — Sound: what you hear (the dials and their verbs) ───────────────────────────────────────────
//
// Role: CORE (settings rework phase 1). Quality (the streaming ladder with the D7 lossless rung, the metered cap, the player bar's
// Lossless badge) · Loudness and
// volume (normalization, remember volume) · Equalizer (the curve bound to the live DSP) · Output and Connect (the output device the
// device picker sets, this PC's Connect name).
//
// THE LIVE DSP. Every equalizer write persists and then pushes the WHOLE state to `Playback.Audio` (`SetEqualizer`), so the ear follows
// the curve while it is dragged; normalization goes through the `Settings.ApplyNormalization` seam until the audio host exposes its own
// setter. The views keep only the mirrors of their frozen controls (a combo's selection signal); the persistence and the push are these verbs.

using FluentGpu.Localization;
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
                value: s => Settings.Quality.AudioIndex(s.Get(Platform.Keys.PlaybackQuality), Spotify.Audio.CanDerive),
                aliases: ["flac", "hires", "hi-res", "kbps", "320"], choices: QualityChoices, contexts: [SettingsSurfaces.DevicePicker]),
            // The description IS the detector's verdict (a live sentence, `Settings.MeteredStatusLine`): every state says something,
            // "Unknown" included. The view words it; the dial carries no static caption.
            Make("sound.meteredQuality", K.Choice, Strings.Settings.Playback.MeteredQuality,
                SettingsWriter.OfChoice("audio.meteredCap", SettingsVerbs.SetMeteredQuality), legacy: ["meteredQuality"], glyph: "RadioTower",
                value: _ => Platform.Network.MeteredQualityCap.Peek(), choices: QualityChoices),
            // The player bar's Lossless badge, DEFAULT ON: the one display option about quality, after the two rungs it reports on (what is asked
            // for, and the metered cap). Off, the bar's host hands every theme no format; the write bumps the bar's epoch, so it applies at once.
            Make("sound.losslessBadge", K.Toggle, Strings.Settings.Sound.LosslessBadge,
                SettingsWriter.OfToggle("playerBar.losslessBadge", WriteBool(Platform.Keys.PlayerBarLosslessBadge, _ => Prefs.PlayerBar.Bump())),
                glyph: "Tag", captionKey: Strings.Settings.Sound.LosslessBadgeSub, value: BoolOf(Platform.Keys.PlayerBarLosslessBadge),
                aliases: ["flac", "hires", "hi-res"])),

        Group("loudness", Strings.Settings.Groups.Sound.Loudness, null,
            // G-132 / D5: the switch, the mode and album mode all apply LIVE to the playing track.
            Make("sound.normalization", K.Toggle, Strings.Settings.Playback.Normalization,
                SettingsWriter.OfToggle("audio.normalization", on => SettingsVerbs.SetNormalizationToggle(Platform.Keys.NormalizationEnabled, on)),
                legacy: ["normalization"], glyph: "Equalizer",
                captionKey: Strings.Settings.Playback.NormalizationSub, value: BoolOf(Platform.Keys.NormalizationEnabled),
                aliases: ["replaygain", "lufs"]),
            Make("sound.normalization.mode", K.Choice, Strings.Settings.Playback.NormalizationMode,
                SettingsWriter.OfChoice("audio.normalizationMode", SettingsVerbs.SetNormalizationMode),
                legacy: ["normalizationMode"], parent: "sound.normalization", glyph: "Speakers",
                captionKey: Strings.Settings.Playback.NormalizationModeSub,
                value: s => Math.Clamp(s.Get(Platform.Keys.NormalizationMode), 0, 2),
                choices: [Strings.Settings.Playback.NormQuiet, Strings.Settings.Playback.NormNormal, Strings.Settings.Playback.NormLoud]),
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
                captionKey: Strings.Settings.Sound.EqualizerSub, value: BoolOf(Platform.Keys.EqualizerEnabled), aliases: ["eq", "equaliser"]),
            Make("sound.equalizer.preset", K.Choice, Strings.Settings.Sound.Preset,
                SettingsWriter.OfChoice("eq.preset", SettingsVerbs.ApplyEqPreset), legacy: ["eqPreset"], parent: "sound.equalizer",
                value: s => Settings.Eq.PresetIndex(s.Get(Platform.Keys.EqualizerPreset)),
                enabled: EqOn, silent: true,
                choices: [Strings.Settings.Sound.Presets.Flat, Strings.Settings.Sound.Presets.Bass, Strings.Settings.Sound.Presets.Treble,
                    Strings.Settings.Sound.Presets.Vocal, Strings.Settings.Sound.Presets.Radio, Strings.Settings.Sound.Presets.Proof]),
            // The curve reports every pointer sample: (band, gain) — the owning verb is `SettingsVerbs.SetEqBand`.
            Make("sound.equalizer.curve", K.Composite, Strings.Settings.Sound.Curve,
                SettingsWriter.Owned("SettingsVerbs.SetEqBand"), legacy: ["eqCurve"], parent: "sound.equalizer",
                caption: f => f.EqualizerEnabled ? Strings.Settings.Sound.CurveOn : Strings.Settings.Sound.CurveOff,
                enabled: EqOn, silent: true),
            Make("sound.equalizer.reset", K.Action, Strings.Settings.Sound.ResetCurve,
                SettingsWriter.OfAction("eq.reset", SettingsVerbs.ResetEqCurve), legacy: ["eqReset"], parent: "sound.equalizer",
                enabled: EqOn, silent: true)),

        Group("output", Strings.Settings.Groups.Sound.Output, null,
            // The output device: the device picker's this-PC rows (index 0 is Match Windows, then the outputs in roster
            // order), written through the picker's own door. Choosing it here changes the output; it does not pull playback here.
            Make("sound.outputDevice", K.Choice, Strings.Settings.Playback.OutputDevice,
                SettingsWriter.OfChoice("audio.outputDevice", SettingsVerbs.SetOutputDevice), glyph: "Speakers",
                captionKey: Strings.Settings.Playback.OutputDeviceSub, value: s => SettingsVerbs.OutputDeviceIndex(s.Get(Platform.Keys.OutputDeviceId)),
                contexts: [SettingsSurfaces.DevicePicker], liveChoices: OutputDeviceNames),
            Make("sound.connectName", K.Fact, Strings.Settings.Devices.ThisPc, SettingsWriter.ReadOnly, legacy: ["thisPc"],
                captionKey: Strings.Settings.Devices.ThisPcSub, value: _ => Playback.DeviceName)),

        // #154: timecode for lighting and video software. The switch is enabled once an output is chosen; the output is chosen while it
        // is off (so the switch can come on), and the other dials grey with the switch. Every write reaches the LTC output at once
        // (Playback.TimecodeOutput.Apply: a dial is heard on the next 10 ms block).
        Group("timecode", Strings.Settings.Groups.Sound.Timecode, null,
            Make("sound.timecode", K.Toggle, Strings.Settings.Sound.Timecode.Send,
                SettingsWriter.OfToggle("timecode.ltc.enabled", SettingsVerbs.SetTimecodeEnabled), glyph: "Clock",
                captionKey: Strings.Settings.Sound.Timecode.SendSub, value: BoolOf(Platform.Keys.TimecodeLtcEnabled),
                enabled: static f => f.TimecodeDeviceChosen ? SettingsEnablement.On : SettingsEnablement.Off(Strings.Settings.Sound.Timecode.ChooseFirst),
                aliases: ["ltc", "smpte", "timecode", "clx", "resolume", "vj"]),
            Make("sound.timecode.device", K.Choice, Strings.Settings.Sound.Timecode.Device,
                SettingsWriter.OfChoice("timecode.ltc.device", SettingsVerbs.SetTimecodeDevice), parent: "sound.timecode", glyph: "Speakers",
                caption: static _ => SettingsVerbs.TimecodeDeviceIsMusic() ? Strings.Settings.Sound.Timecode.IsMusic : Strings.Settings.Sound.Timecode.DeviceSub,
                value: static s => SettingsVerbs.TimecodeDeviceIndex(s.Get(Platform.Keys.TimecodeLtcDeviceId)), liveChoices: TimecodeDeviceNames),
            Make("sound.timecode.rate", K.Choice, Strings.Settings.Sound.Timecode.Rate,
                SettingsWriter.OfChoice("timecode.ltc.rate", SettingsVerbs.SetTimecodeRate), parent: "sound.timecode",
                value: static s => Math.Clamp(s.Get(Platform.Keys.TimecodeLtcRate), 0, 3), enabled: TimecodeOn,
                choices: [Strings.Settings.Sound.Timecode.Fps24, Strings.Settings.Sound.Timecode.Fps25, Strings.Settings.Sound.Timecode.Fps2997,
                    Strings.Settings.Sound.Timecode.Fps30]),
            Make("sound.timecode.start", K.Choice, Strings.Settings.Sound.Timecode.Start,
                SettingsWriter.OfChoice("timecode.startHour", SettingsVerbs.SetTimecodeStartHour), parent: "sound.timecode",
                value: static s => Math.Clamp(s.Get(Platform.Keys.TimecodeStartHour), 0, 1), enabled: TimecodeOn,
                liveChoices: static () => ["00:00:00:00", "01:00:00:00"]),
            Make("sound.timecode.level", K.Slider, Strings.Settings.Sound.Timecode.Level,
                SettingsWriter.OfSlider("timecode.ltc.level", SettingsVerbs.SetTimecodeLevel), parent: "sound.timecode",
                captionKey: Strings.Settings.Sound.Timecode.LevelSub,
                value: static s => (float)Math.Clamp(s.Get(Platform.Keys.TimecodeLtcLevelDb), -24, 0), enabled: TimecodeOn, silent: true),
            Make("sound.timecode.offset", K.Slider, Strings.Settings.Sound.Timecode.Offset,
                SettingsWriter.OfSlider("timecode.offset", SettingsVerbs.SetTimecodeOffset), parent: "sound.timecode",
                captionKey: Strings.Settings.Sound.Timecode.OffsetSub,
                value: static s => (float)Math.Clamp(s.Get(Platform.Keys.TimecodeOffsetMs), -500, 500), enabled: TimecodeOn, silent: true),
            Make("sound.timecode.status", K.Fact, Strings.Settings.Sound.Timecode.Status, SettingsWriter.ReadOnly, parent: "sound.timecode",
                value: static _ => Playback.TimecodeOutput.StatusLine(), enabled: TimecodeOn)),
    ]);

    /// <summary>The streaming ladder's rungs (the metered cap offers the same ones); Lossless is offered only with a key deriver, but it is
    /// always a word the search knows.</summary>
    static readonly string[] QualityChoices =
    [
        Strings.Settings.Playback.QualityNormal, Strings.Settings.Playback.QualityHigh, Strings.Settings.Playback.QualityVeryHigh,
        Strings.Settings.Playback.QualityLossless,
    ];

    /// <summary>The output device's words: Match Windows, then this PC's outputs in roster order (named as the picker names them) (the indexes the dial's value and writer use). Read
    /// when a body row is built: the roster is tracked, so a plugged headset re-reads the page.</summary>
    static IReadOnlyList<string> OutputDeviceNames()
    {
        var devices = Playback.Audio.Devices.Value;
        var names = new string[devices.Length + 1];
        names[0] = Loc.Get(Strings.Player.DevicePicker.MatchWindows);
        for (int i = 0; i < devices.Length; i++) names[i + 1] = devices[i].Name;
        return names;
    }

    /// <summary>The timecode output's words: "Choose an output", then this PC's outputs in roster order (the indexes the dial's value and
    /// writer use). The music's own output is listed (the caption says to choose another) so a saved choice never vanishes from the list.</summary>
    static IReadOnlyList<string> TimecodeDeviceNames()
    {
        var devices = Playback.Audio.Devices.Value;
        var names = new string[devices.Length + 1];
        names[0] = Loc.Get(Strings.Settings.Sound.Timecode.ChooseOutput);
        for (int i = 0; i < devices.Length; i++) names[i + 1] = devices[i].Name;
        return names;
    }

    /// <summary>The timecode dials follow the switch (greyed, no reason of their own); the output itself is always choosable.</summary>
    static SettingsEnablement TimecodeOn(ISettingsFacts f) => f.TimecodeEnabled ? SettingsEnablement.On : SettingsEnablement.Off(null);

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
    // ── Timecode (#154) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The switch: on only with an output chosen.</summary>
    public static bool SetTimecodeEnabled(bool on)
    {
        if (on && Platform.Settings.Get(Platform.Keys.TimecodeLtcDeviceId).Length == 0) return false;
        Platform.Settings.Set(Platform.Keys.TimecodeLtcEnabled, on);
        Playback.TimecodeOutput.Apply();
        return true;
    }

    /// <summary>The output: index 0 is "Choose an output" (none, which also turns the switch off), then the roster.</summary>
    public static bool SetTimecodeDevice(int index)
    {
        var devices = Playback.Audio.Devices.Peek();
        if ((uint)index > (uint)devices.Length) return false;
        string id = index == 0 ? "" : devices[index - 1].Id;
        Platform.Settings.Set(Platform.Keys.TimecodeLtcDeviceId, id);
        if (id.Length == 0) Platform.Settings.Set(Platform.Keys.TimecodeLtcEnabled, false);
        Playback.TimecodeOutput.Apply();
        return true;
    }

    /// <summary>The chosen timecode output's index in the dial's words (0 = none, or a saved id not in the roster now).</summary>
    public static int TimecodeDeviceIndex(string? deviceId) => OutputDeviceIndex(deviceId);

    /// <summary>The chosen timecode output is the one the music plays on (it is then not opened).</summary>
    public static bool TimecodeDeviceIsMusic()
    {
        string id = Platform.Settings.Get(Platform.Keys.TimecodeLtcDeviceId);
        return id.Length > 0 && string.Equals(id, Playback.Audio.MusicEndpointId(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool SetTimecodeRate(int index)
    {
        if ((uint)index > 3) return false;
        Platform.Settings.Set(Platform.Keys.TimecodeLtcRate, index);
        Playback.TimecodeOutput.Apply();
        return true;
    }

    public static bool SetTimecodeStartHour(int index)
    {
        if ((uint)index > 1) return false;
        Platform.Settings.Set(Platform.Keys.TimecodeStartHour, index);
        Playback.TimecodeOutput.Apply();
        return true;
    }

    /// <summary>The level, dB (-24..0, whole dB).</summary>
    public static void SetTimecodeLevel(float db)
    {
        Platform.Settings.Set(Platform.Keys.TimecodeLtcLevelDb, (int)MathF.Round(Math.Clamp(db, -24f, 0f)));
        Playback.TimecodeOutput.Apply();
    }

    /// <summary>The offset, ms (-500..+500, whole ms).</summary>
    public static void SetTimecodeOffset(float ms)
    {
        Platform.Settings.Set(Platform.Keys.TimecodeOffsetMs, (int)MathF.Round(Math.Clamp(ms, -500f, 500f)));
        Playback.TimecodeOutput.Apply();
    }

    public static int OutputDeviceIndex(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return 0;
        var devices = Playback.Audio.Devices.Peek();
        for (int i = 0; i < devices.Length; i++)
            if (string.Equals(devices[i].Id, deviceId, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }
}
