using System;

namespace Wavee;

/// <summary>The one push from settings to the live audio DSP (equalizer + crossfade) — extracted verbatim from
/// <c>SettingsPage.Playback.cs</c>'s private <c>PushDsp</c>/<c>ReadEqGains</c> (~lines 357-433) so the setup
/// wizard's Sound &amp; storage page can call the SAME body without that file exposing it.
///
/// <para><c>SettingsPage.Playback.cs</c> keeps its own private copy for now rather than being edited in this step
/// (out of scope — another agent may be mid-edit on it); a follow-up should repoint its `PushDsp`/`ReadEqGains`
/// call sites at this class so the two bodies can never drift apart.</para></summary>
static class PlaybackDsp
{
    /// <summary>Push the persisted equalizer + crossfade settings to EVERY live DSP the app currently owns — the real
    /// backend's <see cref="Services.LiveHost"/> AND the pre-login/logged-out local host
    /// (<see cref="Services.LocalAudioDsp"/>, local files/radio/modules). The two are mutually exclusive in practice
    /// (go-live disposes the local one), but pushing to both unconditionally means this call site never needs to know
    /// which mode the app is in — whichever host is actually alive picks it up. No-op offline / on the fake backend
    /// (neither host attached, or an audio host doesn't implement <c>IAudioDspControl</c>) — exactly like the shipped
    /// Settings tab's writer.</summary>
    public static void Push(Services? svc)
    {
        if (svc is null) return;
        var settings = svc.Settings;
        if (svc.LiveHost?.Connect.Audio?.Host is Wavee.Backend.IAudioDspControl liveDsp) PushTo(liveDsp, settings);
        if (svc.LocalAudioDsp is { } localDsp) PushTo(localDsp, settings);
    }

    static void PushTo(Wavee.Backend.IAudioDspControl dsp, IAppSettings settings)
    {
        dsp.SetEqualizer(settings.Get(WaveeSettings.EqualizerEnabled), ReadEqGains(settings));
        dsp.SetCrossfade(settings.Get(WaveeSettings.CrossfadeEnabled),
            Math.Clamp(settings.Get(WaveeSettings.CrossfadeMs), 0, 12_000));
    }

    /// <summary>Seed a freshly-constructed DSP host with the persisted equalizer + crossfade settings before it ever
    /// opens a session — shared by <see cref="Wavee.SpotifyLive.Audio.AudioPlaybackStack"/> (the live host) and
    /// <see cref="Services"/>'s pre-login local host construction, so the two can never parse/clamp differently.
    /// Also migrates a persisted crossfade value beyond the current 12 s clamp (older builds exposed up to 30 s)
    /// back into settings once, so UI, parent and child all report the same effective duration.</summary>
    public static void SeedFromSettings(Wavee.Backend.IAudioDspControl dsp, IAppSettings settings)
    {
        int storedCrossfadeMs = settings.Get(WaveeSettings.CrossfadeMs);
        int crossfadeMs = Math.Clamp(storedCrossfadeMs, 0, 12_000);
        if (storedCrossfadeMs != crossfadeMs) settings.Set(WaveeSettings.CrossfadeMs, crossfadeMs);
        dsp.SetEqualizer(settings.Get(WaveeSettings.EqualizerEnabled), ReadEqGains(settings));
        dsp.SetCrossfade(settings.Get(WaveeSettings.CrossfadeEnabled), crossfadeMs);
    }

    /// <summary>The persisted 10-band gain vector, clamped to +/-12 dB. Shared with the Settings tab's equalizer UI
    /// so the wizard and Settings can never disagree about how gains are parsed. Forwards to
    /// <see cref="EqualizerSettings.ReadGains"/> (the dependency-free, unit-tested home for the actual parse/clamp).</summary>
    public static float[] ReadEqGains(IAppSettings? settings) => EqualizerSettings.ReadGains(settings);

    /// <summary>Serialize a ten-band gain vector in the same invariant form consumed by <see cref="ReadEqGains"/>.
    /// Forwards to <see cref="EqualizerSettings.SerializeGains"/>.</summary>
    public static string SerializeEqGains(float[] gains) => EqualizerSettings.SerializeGains(gains);
}
