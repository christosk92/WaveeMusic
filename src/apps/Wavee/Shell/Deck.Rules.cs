// ── Shell/Deck.Rules.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the deck's shared analyser FOLD (themes plan C4, X17): the rail's own 48-band fold of the engine's spectrum
//
// Role: CORE
// Spec: ch 21 §6.3
//
// MOVED VERBATIM from `Deck.UI.cs`: `Deck.SpectrumFold` keeps its name and its static state (one fold per process, shared by
// every analyser deck mounted). The wash colour mixing half of X17 becomes style roles later (themes plan T2b); it is not here.

namespace Wavee;

public static partial class Deck
{
    // ══ 4b. THE SPECTRUM FOLD ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The rail's own 48-band fold (one per process): CopySpectrum -> Visualizer.Bands.Unit -> AGC -> gain -> Follow
    /// into <see cref="Levels"/>. Shared by every analyser deck mounted, so the follower is advanced ONCE per published
    /// spectrum (gated on SpectrumInfo.Sequence) and the no-live release ONCE per frame (gated on the frame clock) — two
    /// decks never double the attack or the decay (V-D25). Called only from a deck clock's tick (UI thread): static state,
    /// no allocation, no prefs read (the gain arrives as an argument the clock cached per epoch).</summary>
    public static class SpectrumFold
    {
        static readonly float[] s_db = new float[Visualizer.Bands.Count], s_target = new float[Visualizer.Bands.Count], s_levels = new float[Visualizer.Bands.Count];
        static long s_seq, s_decayedAtMs, s_foldedAtMs;
        static float s_agc = Visualizer.Bands.AgcFloor;

        /// <summary>The 0..1 band levels the last <see cref="Pull"/> produced (all zeros until a live spectrum arrives).</summary>
        public static ReadOnlySpan<float> Levels => s_levels;

        /// <summary>The follower's constants were authored per 30 Hz tick (<see cref="Visualizer.Bands.RefHz"/>); the fold
        /// steps by the REFERENCE ticks since its previous step instead, so the deck reads the same whether it is pulled at
        /// 30 Hz, 60 Hz or every frame. One tick on the first step; clamped so a resume never integrates a long gap.</summary>
        public static float TicksSince(long lastMs, long nowMs)
            => lastMs == 0L ? 1f : Math.Clamp((nowMs - lastMs) * (Visualizer.Bands.RefHz / 1000f), 0.03f, 3f);

        /// <param name="gain">The deck clock's cached <c>Visualizer.Bands.Gain(sensitivity, calm: false)</c>.</param>
        public static void Pull(float gain)
        {
            int n = Playback.Audio.CopySpectrum(s_db, out var info);
            long now = FrameTime.NowMs;
            if (n < Visualizer.Bands.Count || !info.Live || info.Muted)
            {
                if (now != s_decayedAtMs)                                                              // once per frame, however many decks pull
                {
                    Visualizer.Bands.Decay(s_levels, TicksSince(s_decayedAtMs, now));
                    s_decayedAtMs = now;
                }
                return;
            }
            if (info.Sequence == s_seq) return;                                                        // this publish was already folded by another deck
            s_seq = info.Sequence;
            float ticks = TicksSince(s_foldedAtMs, now);
            s_foldedAtMs = now;
            float frameMax = 0f;
            for (int i = 0; i < n; i++) { float u = Visualizer.Bands.Unit(s_db[i]); s_target[i] = u; if (u > frameMax) frameMax = u; }
            float g = Visualizer.Bands.Agc(ref s_agc, frameMax, ticks) * gain;
            for (int i = 0; i < n; i++) s_target[i] = MathF.Min(1f, s_target[i] * g);
            Visualizer.Bands.Follow(s_target, s_levels, false, ticks);
        }
    }

}
