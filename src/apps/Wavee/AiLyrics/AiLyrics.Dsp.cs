// ── AiLyrics/AiLyrics.Dsp.cs ─────────────────────────────────────────────────────────────────────────────────────────
// Fft, RealFft, Stft, Resampler, Envelope
//
// Role: CORE (pure: no I/O, no clock, no engine)
//
// The signal processing around the two NPU models, written to match what the models were validated against in the lab
// (torch.stft / torch.istft and scipy.signal.resample_poly), so the C# pipeline feeds the NPU the same numbers:
//
//   Fft        mixed-radix (2, 3, 4, 5) complex FFT: iterative Stockham (self-sorting, decimation in frequency) over
//              split float arrays, twiddles precomputed per stage. The separator's frame is 7680 samples, which is not
//              a power of two (2^9 * 3 * 5); its real FFT runs as a complex FFT of 3840 = 4^4 * 3 * 5.
//   RealFft    a real-input FFT through a complex FFT of half the size (the even/odd packing): half the work.
//   Stft       torch.stft(center=True, pad_mode='reflect', periodic Hann) and the matching torch.istft (overlap-add,
//              divided by the summed squared window, then the n_fft/2 centre padding trimmed). The window-square sum
//              depends only on the frame count, so its reciprocal is computed once.
//   Resampler  the polyphase FIR scipy.signal.resample_poly designs (Kaiser beta 5, 2 * 10 * max(up, down) + 1 taps),
//              with the taps regrouped per phase so each output sample is one contiguous dot product.
//   Envelope   vocal level in dB per 10 ms, used to snap word starts to where the singing begins.
//
// Allocation: plans and scratch are allocated once per instance; per-chunk calls write into caller buffers. None of
// these types is thread-safe; each thread that runs them owns its own instances.

using System.Runtime.CompilerServices;

namespace Wavee;

public static partial class AiLyrics
{
    public static class Dsp
    {
        /// <summary>A complex FFT plan for one size whose prime factors are 2, 3 and 5. The inverse is unscaled (divide by
        /// N yourself).</summary>
        public sealed class Fft
        {
            public readonly int N;
            readonly int[] _radix;
            readonly float[][] _twRe, _twIm;             // per stage: [(k - 1) * m + p] = W_n^(p * k), k in 1..radix-1
            readonly float[] _xr, _xi, _yr, _yi;
            static readonly float S3 = (float)(Math.Sqrt(3) / 2);
            static readonly float C5a = (float)Math.Cos(2 * Math.PI / 5), C5b = (float)Math.Cos(4 * Math.PI / 5);
            static readonly float S5a = (float)Math.Sin(2 * Math.PI / 5), S5b = (float)Math.Sin(4 * Math.PI / 5);

            public Fft(int n)
            {
                if (n < 1) throw new ArgumentOutOfRangeException(nameof(n));
                N = n;
                var r = new List<int>();
                int rest = n;
                while (rest % 4 == 0) { r.Add(4); rest /= 4; }
                while (rest % 2 == 0) { r.Add(2); rest /= 2; }
                while (rest % 3 == 0) { r.Add(3); rest /= 3; }
                while (rest % 5 == 0) { r.Add(5); rest /= 5; }
                if (rest != 1) throw new ArgumentException($"FFT size {n} has a prime factor other than 2, 3 or 5", nameof(n));
                _radix = r.ToArray();
                _twRe = new float[_radix.Length][]; _twIm = new float[_radix.Length][];
                int len = n;
                for (int st = 0; st < _radix.Length; st++)
                {
                    int rad = _radix[st], m = len / rad;
                    var tr = new float[(rad - 1) * m]; var ti = new float[(rad - 1) * m];
                    for (int k = 1; k < rad; k++)
                        for (int p = 0; p < m; p++)
                        {
                            double ang = -2.0 * Math.PI * p * k / len;
                            tr[(k - 1) * m + p] = (float)Math.Cos(ang); ti[(k - 1) * m + p] = (float)Math.Sin(ang);
                        }
                    _twRe[st] = tr; _twIm[st] = ti;
                    len = m;
                }
                _xr = new float[n]; _xi = new float[n]; _yr = new float[n]; _yi = new float[n];
            }

            /// <summary>In-place forward transform of (<paramref name="re"/>, <paramref name="im"/>), or the unscaled inverse.</summary>
            public void Transform(Span<float> re, Span<float> im, bool inverse)
            {
                if (re.Length != N || im.Length != N) throw new ArgumentException("length must equal the plan size");
                re.CopyTo(_xr);
                if (inverse) for (int i = 0; i < N; i++) _xi[i] = -im[i];        // inverse = conj(fft(conj(x)))
                else im.CopyTo(_xi);
                float[] xr = _xr, xi = _xi, yr = _yr, yi = _yi;
                int n = N, s = 1;
                for (int st = 0; st < _radix.Length; st++)
                {
                    int rad = _radix[st], m = n / rad;
                    float[] tr = _twRe[st], ti = _twIm[st];
                    switch (rad)
                    {
                        case 4: Radix4(xr, xi, yr, yi, m, s, tr, ti); break;
                        case 2: Radix2(xr, xi, yr, yi, m, s, tr, ti); break;
                        case 3: Radix3(xr, xi, yr, yi, m, s, tr, ti); break;
                        default: Radix5(xr, xi, yr, yi, m, s, tr, ti); break;
                    }
                    (xr, yr) = (yr, xr); (xi, yi) = (yi, xi);
                    n = m; s *= rad;
                }
                xr.AsSpan(0, N).CopyTo(re);
                if (inverse) for (int i = 0; i < N; i++) im[i] = -xi[i];
                else xi.AsSpan(0, N).CopyTo(im);
            }

            // Stockham DIF step for radix r: input stride s, m = n / r groups. Output y[s * (r p + k) + q] =
            // W_n^(p k) * sum_j x[s * (p + j m) + q] * W_r^(j k).

            static void Radix2(float[] xr, float[] xi, float[] yr, float[] yi, int m, int s, float[] tr, float[] ti)
            {
                for (int p = 0; p < m; p++)
                {
                    float wr = tr[p], wi = ti[p];
                    int i0 = s * p, i1 = s * (p + m), o0 = s * (2 * p), o1 = o0 + s;
                    for (int q = 0; q < s; q++)
                    {
                        float ar = xr[i0 + q], ai = xi[i0 + q], br = xr[i1 + q], bi = xi[i1 + q];
                        yr[o0 + q] = ar + br; yi[o0 + q] = ai + bi;
                        float dr = ar - br, di = ai - bi;
                        yr[o1 + q] = dr * wr - di * wi; yi[o1 + q] = dr * wi + di * wr;
                    }
                }
            }

            static void Radix4(float[] xr, float[] xi, float[] yr, float[] yi, int m, int s, float[] tr, float[] ti)
            {
                for (int p = 0; p < m; p++)
                {
                    float w1r = tr[p], w1i = ti[p], w2r = tr[m + p], w2i = ti[m + p], w3r = tr[2 * m + p], w3i = ti[2 * m + p];
                    int i0 = s * p, i1 = s * (p + m), i2 = s * (p + 2 * m), i3 = s * (p + 3 * m), o0 = s * (4 * p);
                    for (int q = 0; q < s; q++)
                    {
                        float a0r = xr[i0 + q], a0i = xi[i0 + q], a1r = xr[i1 + q], a1i = xi[i1 + q];
                        float a2r = xr[i2 + q], a2i = xi[i2 + q], a3r = xr[i3 + q], a3i = xi[i3 + q];
                        float s02r = a0r + a2r, s02i = a0i + a2i, d02r = a0r - a2r, d02i = a0i - a2i;
                        float s13r = a1r + a3r, s13i = a1i + a3i, d13r = a1r - a3r, d13i = a1i - a3i;
                        // b0 = s02 + s13; b1 = d02 - i d13; b2 = s02 - s13; b3 = d02 + i d13
                        float b1r = d02r + d13i, b1i = d02i - d13r, b2r = s02r - s13r, b2i = s02i - s13i, b3r = d02r - d13i, b3i = d02i + d13r;
                        yr[o0 + q] = s02r + s13r; yi[o0 + q] = s02i + s13i;
                        yr[o0 + s + q] = b1r * w1r - b1i * w1i; yi[o0 + s + q] = b1r * w1i + b1i * w1r;
                        yr[o0 + 2 * s + q] = b2r * w2r - b2i * w2i; yi[o0 + 2 * s + q] = b2r * w2i + b2i * w2r;
                        yr[o0 + 3 * s + q] = b3r * w3r - b3i * w3i; yi[o0 + 3 * s + q] = b3r * w3i + b3i * w3r;
                    }
                }
            }

            static void Radix3(float[] xr, float[] xi, float[] yr, float[] yi, int m, int s, float[] tr, float[] ti)
            {
                for (int p = 0; p < m; p++)
                {
                    float w1r = tr[p], w1i = ti[p], w2r = tr[m + p], w2i = ti[m + p];
                    int i0 = s * p, i1 = s * (p + m), i2 = s * (p + 2 * m), o0 = s * (3 * p);
                    for (int q = 0; q < s; q++)
                    {
                        float a0r = xr[i0 + q], a0i = xi[i0 + q], a1r = xr[i1 + q], a1i = xi[i1 + q], a2r = xr[i2 + q], a2i = xi[i2 + q];
                        float sr = a1r + a2r, si = a1i + a2i, dr = a1r - a2r, di = a1i - a2i;
                        float mr = a0r - 0.5f * sr, mi = a0i - 0.5f * si;
                        // W3 = e^{-2 pi i / 3}: b1 = m - i S3 d, b2 = m + i S3 d
                        float b1r = mr + S3 * di, b1i = mi - S3 * dr, b2r = mr - S3 * di, b2i = mi + S3 * dr;
                        yr[o0 + q] = a0r + sr; yi[o0 + q] = a0i + si;
                        yr[o0 + s + q] = b1r * w1r - b1i * w1i; yi[o0 + s + q] = b1r * w1i + b1i * w1r;
                        yr[o0 + 2 * s + q] = b2r * w2r - b2i * w2i; yi[o0 + 2 * s + q] = b2r * w2i + b2i * w2r;
                    }
                }
            }

            static void Radix5(float[] xr, float[] xi, float[] yr, float[] yi, int m, int s, float[] tr, float[] ti)
            {
                int sm = s * m;
                for (int p = 0; p < m; p++)
                {
                    int i0 = s * p, o0 = s * (5 * p);
                    float w1r = tr[p], w1i = ti[p], w2r = tr[m + p], w2i = ti[m + p];
                    float w3r = tr[2 * m + p], w3i = ti[2 * m + p], w4r = tr[3 * m + p], w4i = ti[3 * m + p];
                    for (int q = 0; q < s; q++)
                    {
                        int a = i0 + q;
                        float a0r = xr[a], a0i = xi[a];
                        float a1r = xr[a + sm], a1i = xi[a + sm];
                        float a2r = xr[a + 2 * sm], a2i = xi[a + 2 * sm];
                        float a3r = xr[a + 3 * sm], a3i = xi[a + 3 * sm];
                        float a4r = xr[a + 4 * sm], a4i = xi[a + 4 * sm];
                        float s14r = a1r + a4r, s14i = a1i + a4i, d14r = a1r - a4r, d14i = a1i - a4i;
                        float s23r = a2r + a3r, s23i = a2i + a3i, d23r = a2r - a3r, d23i = a2i - a3i;
                        float c1r = a0r + C5a * s14r + C5b * s23r, c1i = a0i + C5a * s14i + C5b * s23i;
                        float c2r = a0r + C5b * s14r + C5a * s23r, c2i = a0i + C5b * s14i + C5a * s23i;
                        // the sine parts of e^{-2 pi i jk / 5} enter as -i (...)
                        float e1r = S5a * d14i + S5b * d23i, e1i = -(S5a * d14r + S5b * d23r);
                        float e2r = S5b * d14i - S5a * d23i, e2i = -(S5b * d14r - S5a * d23r);
                        float b1r = c1r + e1r, b1i = c1i + e1i, b4r = c1r - e1r, b4i = c1i - e1i;
                        float b2r = c2r + e2r, b2i = c2i + e2i, b3r = c2r - e2r, b3i = c2i - e2i;
                        int o = o0 + q;
                        yr[o] = a0r + s14r + s23r; yi[o] = a0i + s14i + s23i;
                        Tw(yr, yi, o + s, b1r, b1i, w1r, w1i);
                        Tw(yr, yi, o + 2 * s, b2r, b2i, w2r, w2i);
                        Tw(yr, yi, o + 3 * s, b3r, b3i, w3r, w3i);
                        Tw(yr, yi, o + 4 * s, b4r, b4i, w4r, w4i);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void Tw(float[] yr, float[] yi, int o, float br, float bi, float wr, float wi)
            { yr[o] = br * wr - bi * wi; yi[o] = br * wi + bi * wr; }
        }

        /// <summary>Real-input FFT of even size N through a complex FFT of N/2 (the even/odd packing): bins [0, N/2] as
        /// split real/imaginary arrays. Inverse is unscaled like <see cref="Fft"/> (divide by N yourself).</summary>
        public sealed class RealFft
        {
            public readonly int N;
            readonly Fft _half;
            readonly float[] _zr, _zi, _wr, _wi;

            public RealFft(int n)
            {
                if (n % 2 != 0 || n < 2) throw new ArgumentException("size must be even", nameof(n));
                N = n; int h = n / 2;
                _half = new Fft(h); _zr = new float[h]; _zi = new float[h]; _wr = new float[h + 1]; _wi = new float[h + 1];
                for (int k = 0; k <= h; k++) { double a = -2.0 * Math.PI * k / n; _wr[k] = (float)Math.Cos(a); _wi[k] = (float)Math.Sin(a); }
            }

            public void Forward(ReadOnlySpan<float> x, Span<float> specRe, Span<float> specIm)
            {
                int h = N / 2;
                for (int i = 0; i < h; i++) { _zr[i] = x[2 * i]; _zi[i] = x[2 * i + 1]; }
                _half.Transform(_zr, _zi, inverse: false);
                for (int k = 0; k <= h; k++)
                {
                    int a = k == h ? 0 : k, b = k == 0 ? 0 : h - k;
                    float zr = _zr[a], zi = _zi[a], cr = _zr[b], ci = -_zi[b];        // Z[k], conj(Z[h - k])
                    float er = 0.5f * (zr + cr), ei = 0.5f * (zi + ci);                // even part
                    float odr = 0.5f * (zi - ci), odi = -0.5f * (zr - cr);             // odd part = (Z - conj) * (-i / 2)
                    specRe[k] = er + _wr[k] * odr - _wi[k] * odi;
                    specIm[k] = ei + _wr[k] * odi + _wi[k] * odr;
                }
            }

            public void Inverse(ReadOnlySpan<float> specRe, ReadOnlySpan<float> specIm, Span<float> x)
            {
                int h = N / 2;
                for (int k = 0; k < h; k++)
                {
                    float xr = specRe[k], xi = specIm[k], cr = specRe[h - k], ci = -specIm[h - k];   // X[k], conj(X[h - k])
                    float er = 0.5f * (xr + cr), ei = 0.5f * (xi + ci);
                    float dr = 0.5f * (xr - cr), di = 0.5f * (xi - ci);
                    float odr = dr * _wr[k] + di * _wi[k], odi = di * _wr[k] - dr * _wi[k];          // d * conj(W[k])
                    _zr[k] = er - odi; _zi[k] = ei + odr;                                             // even + i * odd
                }
                _half.Transform(_zr, _zi, inverse: true);
                for (int i = 0; i < h; i++) { x[2 * i] = _zr[i] * 2; x[2 * i + 1] = _zi[i] * 2; }
            }
        }

        /// <summary>torch.stft / torch.istft for one fixed (n_fft, hop) with a periodic Hann window, centre padding and
        /// reflect mode. A channel's spectrum is two planes (real, imaginary), each laid out [bin * frames + frame].</summary>
        public sealed class Stft
        {
            public readonly int NFft, Hop, Bins;
            readonly float[] _win;
            readonly RealFft _fft;
            readonly float[] _frame, _specRe, _specIm;
            float[] _acc = Array.Empty<float>(), _invNorm = Array.Empty<float>();
            int _normFrames = -1;

            public Stft(int nFft, int hop)
            {
                if (nFft % 2 != 0) throw new ArgumentException("n_fft must be even", nameof(nFft));
                NFft = nFft; Hop = hop; Bins = nFft / 2 + 1;
                _win = new float[nFft];
                for (int i = 0; i < nFft; i++) _win[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / nFft));   // periodic
                _fft = new RealFft(nFft);
                _frame = new float[nFft];
                _specRe = new float[Bins]; _specIm = new float[Bins];
            }

            /// <summary>Number of frames torch.stft(center=True) yields for <paramref name="samples"/> samples.</summary>
            public int Frames(int samples) => 1 + samples / Hop;

            /// <summary>Forward transform of one channel, bins [0, <paramref name="keepBins"/>).</summary>
            public void Forward(ReadOnlySpan<float> x, Span<float> re, Span<float> im, int keepBins)
            {
                int n = x.Length, frames = Frames(n), half = NFft / 2;
                if (n <= half) throw new ArgumentException("signal shorter than the reflect padding", nameof(x));
                for (int f = 0; f < frames; f++)
                {
                    int start = f * Hop - half;                       // in the unpadded signal
                    if (start >= 0 && start + NFft <= n)
                    {
                        var src = x.Slice(start, NFft);
                        for (int i = 0; i < NFft; i++) _frame[i] = src[i] * _win[i];
                    }
                    else
                    {
                        for (int i = 0; i < NFft; i++)
                        {
                            int idx = start + i;
                            if (idx < 0) idx = -idx;                  // reflect without repeating the edge sample
                            else if (idx >= n) idx = 2 * (n - 1) - idx;
                            _frame[i] = x[idx] * _win[i];
                        }
                    }
                    _fft.Forward(_frame, _specRe, _specIm);
                    for (int b = 0; b < keepBins; b++)
                    {
                        re[b * frames + f] = _specRe[b];
                        im[b * frames + f] = _specIm[b];
                    }
                }
            }

            /// <summary>Inverse of <see cref="Forward"/> for one channel; bins at or above <paramref name="usedBins"/>
            /// count as zero. Writes hop * (frames - 1) samples, as torch.istft(center=True) does.</summary>
            public void Inverse(ReadOnlySpan<float> re, ReadOnlySpan<float> im, int frames, int usedBins, Span<float> y)
            {
                int full = NFft + Hop * (frames - 1);
                int outLen = Hop * (frames - 1), half = NFft / 2;
                if (y.Length < outLen) throw new ArgumentException("output too short", nameof(y));
                if (_acc.Length < full) _acc = new float[full];
                if (_normFrames != frames)
                {
                    // the summed squared window depends only on (n_fft, hop, frames): computed once, kept as reciprocals
                    var norm = new float[full];
                    for (int f = 0; f < frames; f++) for (int i = 0; i < NFft; i++) norm[f * Hop + i] += _win[i] * _win[i];
                    _invNorm = new float[outLen];
                    for (int i = 0; i < outLen; i++) { float w = norm[i + half]; _invNorm[i] = w > 1e-11f ? 1f / w : 0f; }
                    _normFrames = frames;
                }
                Array.Clear(_acc, 0, full);
                float inv = 1f / NFft;
                for (int f = 0; f < frames; f++)
                {
                    for (int b = 0; b < Bins; b++)
                    {
                        bool used = b < usedBins;
                        _specRe[b] = used ? re[b * frames + f] : 0f;
                        _specIm[b] = used ? im[b * frames + f] : 0f;
                    }
                    _specIm[0] = 0f; _specIm[Bins - 1] = 0f;                  // a real signal: DC and Nyquist are real
                    _fft.Inverse(_specRe, _specIm, _frame);
                    int o = f * Hop;
                    for (int i = 0; i < NFft; i++) _acc[o + i] += _frame[i] * inv * _win[i];
                }
                for (int i = 0; i < outLen; i++) y[i] = _acc[i + half] * _invNorm[i];
            }
        }

        /// <summary>Rational resampler matching scipy.signal.resample_poly(x, up, down): a Kaiser (beta 5) low-pass of
        /// 2 * 10 * max(up, down) + 1 taps, applied polyphase over a whole buffer.</summary>
        public sealed class Resampler
        {
            public readonly int Up, Down;
            readonly int _half;
            readonly float[][] _phase;                 // per phase k0: taps h[k0 + j * Up], reversed so x is read forward
            readonly int[] _phaseLen;

            public Resampler(int up, int down)
            {
                int g = Gcd(up, down); Up = up / g; Down = down / g;
                int maxRate = Math.Max(Up, Down);
                _half = 10 * maxRate;
                int taps = 2 * _half + 1;
                double fc = 1.0 / maxRate;                         // firwin cutoff, relative to Nyquist
                var h = new double[taps];
                double i0b = BesselI0(5.0), sum = 0;
                for (int i = 0; i < taps; i++)
                {
                    double t = i - _half;
                    double sinc = t == 0 ? fc : Math.Sin(Math.PI * fc * t) / (Math.PI * t);
                    double r = 2.0 * i / (taps - 1) - 1.0;
                    h[i] = sinc * BesselI0(5.0 * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
                    sum += h[i];
                }
                _phase = new float[Up][]; _phaseLen = new int[Up];
                for (int k0 = 0; k0 < Up; k0++)
                {
                    int len = (taps - k0 + Up - 1) / Up;
                    var ph = new float[len];
                    for (int j = 0; j < len; j++) ph[len - 1 - j] = (float)(h[k0 + j * Up] / sum * Up);   // unit DC gain x up
                    _phase[k0] = ph; _phaseLen[k0] = len;
                }
            }

            /// <summary>Output length scipy produces for <paramref name="n"/> input samples.</summary>
            public int OutLength(int n) => (int)((n * (long)Up + Down - 1) / Down);

            public void Process(ReadOnlySpan<float> x, Span<float> y)
            {
                int n = x.Length, outN = Math.Min(y.Length, OutLength(n));
                // y[m] = sum_j h[k0 + j Up] * x[base - j], base = (m Down + half - k0) / Up, k0 = (m Down + half) % Up.
                for (int m = 0; m < outN; m++)
                {
                    long pos = (long)m * Down + _half;
                    int k0 = (int)(pos % Up);
                    long b = (pos - k0) / Up;                       // newest input sample the phase touches
                    float[] ph = _phase[k0];
                    int len = _phaseLen[k0];
                    long first = b - (len - 1);                     // oldest input sample, aligned with ph[0]
                    int j0 = (int)Math.Max(0, -first), j1 = (int)Math.Min(len, n - first);
                    float acc = 0;
                    for (int j = j0; j < j1; j++) acc += ph[j] * x[(int)(first + j)];
                    y[m] = acc;
                }
            }

            static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

            static double BesselI0(double x)
            {
                double sum = 1, term = 1, q = x * x / 4;
                for (int k = 1; k < 60; k++) { term *= q / ((double)k * k); sum += term; if (term < 1e-14 * sum) break; }
                return sum;
            }
        }

        /// <summary>Vocal level in dB per 10 ms frame (mean square of the mono separated vocals), smoothed over three
        /// frames when read. Appended to as separated audio arrives.</summary>
        public sealed class Envelope
        {
            public const double FrameSeconds = 0.01;
            readonly int _hop;
            readonly List<float> _db = new();
            readonly float[] _pending;
            int _pendingN;

            public Envelope(int sampleRate) { _hop = (int)(sampleRate * FrameSeconds); _pending = new float[_hop]; }

            public int Count => _db.Count;

            public void Append(ReadOnlySpan<float> mono)
            {
                foreach (float s in mono)
                {
                    _pending[_pendingN++] = s;
                    if (_pendingN < _hop) continue;
                    double e = 0;
                    for (int i = 0; i < _hop; i++) e += _pending[i] * _pending[i];
                    _db.Add((float)(10 * Math.Log10(e / _hop + 1e-12)));
                    _pendingN = 0;
                }
            }

            /// <summary>Smoothed level at frame <paramref name="i"/> (mean of the frame and its neighbours).</summary>
            public float At(int i)
            {
                if (_db.Count == 0) return -120f;
                i = Math.Clamp(i, 0, _db.Count - 1);
                int a = Math.Max(0, i - 1), b = Math.Min(_db.Count - 1, i + 1);
                float s = 0; for (int k = a; k <= b; k++) s += _db[k];
                return s / (b - a + 1);
            }

            /// <summary>The level below which a frame counts as silence: the 10th percentile so far, plus 15 dB.</summary>
            public float QuietThreshold()
            {
                if (_db.Count == 0) return -60f;
                var copy = _db.ToArray();
                Array.Sort(copy);
                return copy[(int)(0.10 * (copy.Length - 1))] + 15f;
            }
        }
    }
}
