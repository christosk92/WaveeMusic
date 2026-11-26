// ── Entities/Mix.Lesson.Songs.cs — #48 Mix, the lesson's demo pair, made on this PC ─────────────────────────────────────────────
//
// Night Drive (124 BPM, A minor, 8A) and Sunrise (128 BPM, C major, 8B): eight bars each, the concept's scores (lesson-v3) note for
// note, synthesised into stereo PCM at the mix rate. Each voice is an envelope (an exponential attack and decay, as the concept's Web
// Audio ramps) over an oscillator or the noise table, through its own 12 dB/oct filter (the engine's RBJ biquad). Pure, worker thread.

using FluentGpu.Media;

namespace Wavee;

internal static class MixLessonSongs
{
    const float Out = 0.9f;

    /// <summary>Night Drive: kick on every beat, off-beat hats, a sawtooth bassline (A, A, C, G), a pad every two bars (Am, F), a two-note
    /// triangle hook.</summary>
    public static float[] NightDrive(int rate)
    {
        double beat = 60.0 / MixLesson.BpmA, bar = 4 * beat;
        var k = new Kit(rate, 8 * bar + 0.6);
        double[] bass = [55, 55, 65.41, 49];
        double[][] chords = [[220, 261.63, 329.63], [174.61, 220, 261.63]];
        for (int b = 0; b < 8; b++)
        {
            double t0 = b * bar;
            if (b % 2 == 0) k.Pad(t0, chords[b / 2 % 2], 2 * bar, 0.035f);
            for (int i = 0; i < 4; i++)
            {
                double t = t0 + i * beat;
                k.Kick(t);
                k.Hat(t + beat / 2, 0.22f, 0.09);
                k.Tone(t + beat / 2, bass[b % 4], 0.2, Wave.Saw, 0.32f, 520);
            }
            k.Tone(t0 + 1.5 * beat, 440, 0.12, Wave.Triangle, 0.16f, 4000);
            k.Tone(t0 + 3.5 * beat, 523.25, 0.12, Wave.Triangle, 0.13f, 4000);
        }
        return k.Stereo();
    }

    /// <summary>Sunrise: kick on every beat, a clap on two and four, sixteenth hats, a square bass (C, C, A, G), a square arpeggio.</summary>
    public static float[] Sunrise(int rate)
    {
        double beat = 60.0 / MixLesson.BpmB, bar = 4 * beat;
        var k = new Kit(rate, 8 * bar + 0.6);
        double[] bass = [65.41, 65.41, 55, 49];
        double[] arp = [523.25, 659.25, 783.99, 987.77];
        for (int b = 0; b < 8; b++)
        {
            double t0 = b * bar;
            for (int i = 0; i < 4; i++)
            {
                double t = t0 + i * beat;
                k.Kick(t);
                if (i % 2 == 1) k.Clap(t);
                k.Tone(t + beat / 2, bass[b % 4], 0.18, Wave.Square, 0.18f, 380);
                for (int s = 0; s < 4; s++)
                {
                    k.Hat(t + s * beat / 4, s == 2 ? 0.14f : 0.06f, 0.04);
                    k.Tone(t + s * beat / 4, arp[(i * 4 + s + b) % 4], 0.11, Wave.Square, 0.055f, 2600);
                }
            }
        }
        return k.Stereo();
    }

    enum Wave : byte { Sine, Saw, Square, Triangle }

    /// <summary>A mono bus the voices add into.</summary>
    sealed class Kit
    {
        readonly int _rate;
        readonly float[] _bus;
        readonly float[] _noise;

        public Kit(int rate, double seconds)
        {
            _rate = rate;
            _bus = new float[(int)Math.Ceiling(seconds * rate)];
            // The concept's noise: a Park-Miller generator, one second of it, read from a place each hit picks.
            _noise = new float[rate];
            long s = 7;
            for (int i = 0; i < _noise.Length; i++) { s = s * 16807 % 2147483647; _noise[i] = (float)(s / 1073741823.5 - 1); }
        }

        /// <summary>Exponential attack to <paramref name="peak"/> over <paramref name="a"/>, then decay over <paramref name="dec"/> (0.0001 at
        /// both ends, as Web Audio's exponential ramps).</summary>
        static float Env(double dt, double a, float peak, double dec)
        {
            const double floor = 0.0001;
            if (dt < 0) return 0f;
            if (dt < a) return (float)(floor * Math.Pow(peak / floor, dt / a));
            double d = dt - a;
            if (d >= dec) return 0f;
            return (float)(peak * Math.Pow(floor / peak, d / dec));
        }

        BiquadCoeffs Filter(BiquadType type, double hz, double q) => BiquadCoeffs.Design(new BiquadBand(type, (float)hz, (float)q, 0f), _rate);

        int At(double t) => (int)Math.Round(t * _rate);

        public void Kick(double t)
        {
            int i0 = At(t), n = At(0.45);
            double phase = 0;
            for (int i = 0; i < n && i0 + i < _bus.Length; i++)
            {
                double dt = (double)i / _rate;
                double f = dt < 0.13 ? 150 * Math.Pow(42.0 / 150, dt / 0.13) : 42;
                phase += 2 * Math.PI * f / _rate;
                _bus[i0 + i] += (float)Math.Sin(phase) * Env(dt, 0.003, 1f, 0.36) * Out;
            }
        }

        public void Hat(double t, float v, double dec)
        {
            var c = Filter(BiquadType.HighPass, 7200, 0.707);
            Noise(t, c, BiquadCoeffs.Identity, v, 0.002, dec, (int)(t * 7.3 % 0.5 * _rate));
        }

        public void Clap(double t)
        {
            // The concept's band-pass at 1.5 kHz (Q 0.9) as a high-pass and a low-pass either side of it.
            Noise(t, Filter(BiquadType.HighPass, 900, 0.707), Filter(BiquadType.LowPass, 2500, 0.707), 0.5f, 0.004, 0.16, (int)(t * 3.1 % 0.5 * _rate));
        }

        void Noise(double t, BiquadCoeffs c, BiquadCoeffs c2, float peak, double a, double dec, int offset)
        {
            var st = new BiquadState();
            var st2 = new BiquadState();
            int i0 = At(t), n = At(a + dec + 0.05);
            for (int i = 0; i < n && i0 + i < _bus.Length; i++)
            {
                float x = _noise[(offset + i) % _noise.Length];
                _bus[i0 + i] += st2.Process(st.Process(x, in c), in c2) * Env((double)i / _rate, a, peak, dec) * Out;
            }
        }

        public void Tone(double t, double hz, double dur, Wave w, float peak, double cutoff)
        {
            var c = Filter(BiquadType.LowPass, cutoff, 0.707);
            var st = new BiquadState();
            int i0 = At(t), n = At(dur + 0.006 + 0.05);
            for (int i = 0; i < n && i0 + i < _bus.Length; i++)
            {
                double dt = (double)i / _rate;
                _bus[i0 + i] += st.Process(Osc(w, hz * dt), in c) * Env(dt, 0.006, peak, dur) * Out;
            }
        }

        /// <summary>Two detuned saws per note (±6 cents), low-passed, a linear swell to <paramref name="peak"/> at a third of the way and back.</summary>
        public void Pad(double t, double[] notes, double dur, float peak)
        {
            var c = Filter(BiquadType.LowPass, 1300, 0.707);
            int i0 = At(t), n = At(dur + 0.05);
            foreach (double hz in notes)
                foreach (int cents in (ReadOnlySpan<int>)[-6, 6])
                {
                    var st = new BiquadState();
                    double f = hz * Math.Pow(2, cents / 1200.0);
                    for (int i = 0; i < n && i0 + i < _bus.Length; i++)
                    {
                        double dt = (double)i / _rate;
                        double g = dt < dur * 0.35 ? dt / (dur * 0.35) : Math.Max(0, 1 - (dt - dur * 0.35) / (dur * 0.65));
                        _bus[i0 + i] += st.Process(Osc(Wave.Saw, f * dt), in c) * (float)(peak * g) * Out;
                    }
                }
        }

        /// <summary>One sample of a waveform at <paramref name="cycles"/> (phase in cycles).</summary>
        static float Osc(Wave w, double cycles)
        {
            double p = cycles - Math.Floor(cycles);
            return w switch
            {
                Wave.Saw => (float)(2 * p - 1),
                Wave.Square => p < 0.5 ? 1f : -1f,
                Wave.Triangle => (float)(p < 0.5 ? 4 * p - 1 : 3 - 4 * p),
                _ => (float)Math.Sin(2 * Math.PI * p),
            };
        }

        public float[] Stereo()
        {
            var o = new float[_bus.Length * 2];
            for (int i = 0; i < _bus.Length; i++) o[2 * i] = o[2 * i + 1] = _bus[i];
            return o;
        }
    }
}
