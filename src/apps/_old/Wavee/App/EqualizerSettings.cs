using System;
using System.Globalization;
using System.Text;

namespace Wavee;

/// <summary>Pure parse/clamp/serialize for the persisted 10-band equalizer gain vector. Deliberately dependency-free
/// (no <see cref="Services"/>, no live audio host) — extracted out of <c>PlaybackDsp</c> (which stays the one place
/// that actually pushes a parsed vector to a live <c>IAudioDspControl</c>) so this half is unit-testable and
/// includable in <c>Wavee.Tests</c> without pulling in the whole app composition root. <c>PlaybackDsp.ReadEqGains</c>
/// / <c>SerializeEqGains</c> are thin forwarders to <see cref="ReadGains"/> / <see cref="SerializeGains"/> so every
/// existing call site keeps working unchanged.</summary>
public static class EqualizerSettings
{
    public const int BandCount = 10;
    public const float MinGainDb = -12f;
    public const float MaxGainDb = 12f;

    /// <summary>Parse the persisted comma-separated gain string into a <see cref="BandCount"/>-band vector, each
    /// value clamped to [<see cref="MinGainDb"/>, <see cref="MaxGainDb"/>]. A missing settings store, a missing/blank
    /// persisted value, too few entries, or a non-numeric entry all default the affected band(s) to 0 dB (flat) —
    /// garbage never throws, it just means "no boost/cut on this band".</summary>
    public static float[] ReadGains(IAppSettings? settings)
    {
        var gains = new float[BandCount];
        string raw = settings?.Get(WaveeSettings.EqualizerGains) ?? WaveeSettings.EqualizerGains.Default;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        for (int i = 0; i < gains.Length && i < parts.Length; i++)
            if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                gains[i] = Math.Clamp(v, MinGainDb, MaxGainDb);
        return gains;
    }

    /// <summary>Serialize a gain vector in the same invariant, comma-separated form <see cref="ReadGains"/> consumes.
    /// A vector shorter than <see cref="BandCount"/> pads the missing bands with 0 dB; a longer one is truncated.</summary>
    public static string SerializeGains(float[] gains)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < BandCount; i++)
        {
            if (i > 0) sb.Append(',');
            float gain = i < gains.Length ? Math.Clamp(gains[i], MinGainDb, MaxGainDb) : 0f;
            sb.Append(gain.ToString("0.#", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
