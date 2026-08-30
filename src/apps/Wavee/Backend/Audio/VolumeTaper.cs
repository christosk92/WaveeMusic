namespace Wavee.Backend.Audio;

/// <summary>Slider position (0..1) → linear amplitude. Cubic audio taper: perceived loudness tracks
/// the slider (0.5 ≈ −18 dB, 0.1 ≈ −60 dB), exact at 0 and 1. The Connect wire, the persisted
/// setting, and every UI surface keep the untapered slider position — this runs ONLY at the
/// amplitude boundary (WasapiRenderer).</summary>
public static class VolumeTaper
{
    public static float Amplitude(float slider01)
    {
        float v = Math.Clamp(slider01, 0f, 1f);
        return v * v * v;
    }

    /// <summary>The inverse of <see cref="Amplitude"/>: recover the slider position (0..1) from a linear amplitude
    /// (the cube root). Used for two-way Windows session-volume readback (plan §B2) — bijective with Amplitude.</summary>
    public static float Slider(float amplitude) => MathF.Cbrt(Math.Clamp(amplitude, 0f, 1f));
}

/// <summary>
/// Documents the three independent gain domains used by local playback. Windows applies endpoint master volume after
/// Wavee's per-app session, while prepared-track fades use a per-stream scalar inside that session.
/// </summary>
public static class OutputGain
{
    public static float EffectiveAmplitude(float osMaster01, float appSlider01, float streamGain01) =>
        Math.Clamp(osMaster01, 0f, 1f)
        * VolumeTaper.Amplitude(appSlider01)
        * Math.Clamp(streamGain01, 0f, 1f);
}
