using System;
using System.Globalization;
using System.Text;

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
    /// <summary>Push the persisted equalizer + crossfade settings to the live DSP, when one is attached. No-op
    /// offline / on the fake backend (no <see cref="Services.LiveHost"/>, or its audio host doesn't implement
    /// <c>IAudioDspControl</c>) — exactly like the shipped Settings tab's writer.</summary>
    public static void Push(Services? svc)
    {
        if (svc?.LiveHost?.Connect.Audio?.Host is not Wavee.Backend.IAudioDspControl dsp) return;
        var settings = svc.Settings;
        dsp.SetEqualizer(settings.Get(WaveeSettings.EqualizerEnabled), ReadEqGains(settings));
        dsp.SetCrossfade(settings.Get(WaveeSettings.CrossfadeEnabled),
            Math.Clamp(settings.Get(WaveeSettings.CrossfadeMs), 0, 12_000));
    }

    /// <summary>The persisted 10-band gain vector, clamped to +/-12 dB. Shared with the Settings tab's
    /// equalizer UI so the wizard and Settings can never disagree about how gains are parsed.</summary>
    public static float[] ReadEqGains(IAppSettings? settings)
    {
        var gains = new float[10];
        string raw = settings?.Get(WaveeSettings.EqualizerGains) ?? WaveeSettings.EqualizerGains.Default;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        for (int i = 0; i < gains.Length && i < parts.Length; i++)
            if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                gains[i] = Math.Clamp(v, -12f, 12f);
        return gains;
    }

    /// <summary>Serialize a ten-band gain vector in the same invariant form consumed by <see cref="ReadEqGains"/>.</summary>
    public static string SerializeEqGains(float[] gains)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++)
        {
            if (i > 0) sb.Append(',');
            float gain = i < gains.Length ? Math.Clamp(gains[i], -12f, 12f) : 0f;
            sb.Append(gain.ToString("0.#", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
