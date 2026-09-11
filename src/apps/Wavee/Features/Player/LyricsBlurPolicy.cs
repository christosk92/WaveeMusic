using System;

namespace Wavee;

/// <summary>The lyrics blur STRENGTH decision — one 0..100% dial that scales BOTH the depth-of-field ladder
/// (<c>LyricsFx</c>) and the active-line/held-note glow halo σ in <c>LyricsView</c>. Pure and engine-free by
/// construction (System only) so the auto-tier resolution and the scale math are unit-testable without a scene,
/// a settings store or a GPU.
///
/// <para><see cref="WaveeSettings.LyricsBlurStrength"/> stores -1 (AUTO) by default: a fresh install lets the
/// device decide rather than shipping one fixed number that is too heavy for a weak iGPU or too light for a
/// desktop card. <see cref="Resolve"/> is the ONLY place -1 is interpreted — everywhere else in the app deals in
/// the resolved 0..100 int, so a caller that forgets the auto case cannot silently treat -1 as "1% blur".</para>
///
/// <para>0 is OFF: <see cref="Enabled"/> and a <see cref="Scale"/> of exactly 0 both fall out of the same clamp,
/// and LyricsView relies on the engine's own "0 ⇒ no blur layer at all" rule (SceneRecorder drops σ ≤ 0.01) rather
/// than a second branch here — see the LyricsFx type doc.</para></summary>
static class LyricsBlurPolicy
{
    public const int Auto = -1;
    public const int WeakGpuDefault = 40;
    public const int StrongGpuDefault = 100;

    /// <summary>The stored setting (-1 = auto, else 0..100) resolved against the device's GPU tier into the
    /// strength this frame actually paints at. Clamps a stored value from an older/newer build's ladder into
    /// range rather than trusting the registry.</summary>
    public static int Resolve(int setting, bool weakGpu)
        => setting == Auto ? (weakGpu ? WeakGpuDefault : StrongGpuDefault) : Math.Clamp(setting, 0, 100);

    /// <summary>The resolved strength (0..100, NOT -1 — call <see cref="Resolve"/> first) as the 0..1 multiplier
    /// LyricsView scales its DoF ladder and halo σ by.</summary>
    public static float Scale(int strength) => Math.Clamp(strength, 0, 100) / 100f;

    /// <summary>Whether the resolved strength paints any blur at all. Strength 0 is a real, deliberate "off" — not
    /// a rounding edge of Scale — so callers gate the whole DoF/halo path on this rather than comparing Scale to 0.</summary>
    public static bool Enabled(int strength) => strength > 0;
}
