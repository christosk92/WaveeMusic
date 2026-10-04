// ── Wavee.Tests/AiLyricsDspTests.cs — the signal processing around the AI lyrics models ───────────────────────────────
//
// The FFT against a naive DFT (sizes with every radix the plan uses), the real FFT against the complex one, the STFT
// round trip on a separator-sized window, the resampler's length and gain, and the envelope's level. Pure: no NPU.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class AiLyricsDspTests
{
    static (double[] Re, double[] Im) NaiveDft(float[] re, float[] im)
    {
        int n = re.Length;
        var or = new double[n]; var oi = new double[n];
        for (int k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (int t = 0; t < n; t++)
            {
                double a = -2 * Math.PI * k * t / n;
                sr += re[t] * Math.Cos(a) - im[t] * Math.Sin(a);
                si += re[t] * Math.Sin(a) + im[t] * Math.Cos(a);
            }
            or[k] = sr; oi[k] = si;
        }
        return (or, oi);
    }

    static float[] Noise(int n, int seed)
    {
        var r = new Random(seed); var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(r.NextDouble() * 2 - 1);
        return x;
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(15)] [InlineData(60)] [InlineData(120)] [InlineData(3840)]
    public void Fft_matches_a_naive_dft(int n)
    {
        float[] re = Noise(n, n), im = Noise(n, n + 7);
        var (er, ei) = NaiveDft(re, im);
        var fft = new AiLyrics.Dsp.Fft(n);
        float[] gr = (float[])re.Clone(), gi = (float[])im.Clone();
        fft.Transform(gr, gi, inverse: false);
        double err = 0, norm = 0;
        for (int k = 0; k < n; k++) { err += Math.Pow(gr[k] - er[k], 2) + Math.Pow(gi[k] - ei[k], 2); norm += er[k] * er[k] + ei[k] * ei[k]; }
        Assert.True(Math.Sqrt(err / norm) < 1e-4, $"relative error {Math.Sqrt(err / norm)} at n={n}");
    }

    [Theory]
    [InlineData(60)] [InlineData(3840)]
    public void Inverse_fft_returns_the_input_times_n(int n)
    {
        float[] re = Noise(n, 3), im = Noise(n, 4);
        float[] gr = (float[])re.Clone(), gi = (float[])im.Clone();
        var fft = new AiLyrics.Dsp.Fft(n);
        fft.Transform(gr, gi, inverse: false);
        fft.Transform(gr, gi, inverse: true);
        for (int i = 0; i < n; i++) { Assert.True(Math.Abs(re[i] - gr[i] / n) < 2e-3); Assert.True(Math.Abs(im[i] - gi[i] / n) < 2e-3); }   // float, two transforms
    }

    [Fact]
    public void Fft_refuses_a_size_with_a_prime_factor_above_five()
        => Assert.Throws<ArgumentException>(() => new AiLyrics.Dsp.Fft(7 * 4));

    [Theory]
    [InlineData(120)] [InlineData(7680)]
    public void Real_fft_matches_the_complex_fft_and_inverts(int n)
    {
        float[] x = Noise(n, 11);
        var cr = (float[])x.Clone(); var ci = new float[n];
        new AiLyrics.Dsp.Fft(n).Transform(cr, ci, inverse: false);
        var rf = new AiLyrics.Dsp.RealFft(n);
        var sr = new float[n / 2 + 1]; var si = new float[n / 2 + 1];
        rf.Forward(x, sr, si);
        for (int k = 0; k <= n / 2; k++)
        {
            Assert.True(Math.Abs(sr[k] - cr[k]) < 1e-2 * Math.Sqrt(n), $"re bin {k}");
            Assert.True(Math.Abs(si[k] - ci[k]) < 1e-2 * Math.Sqrt(n), $"im bin {k}");
        }
        var back = new float[n];
        rf.Inverse(sr, si, back);
        for (int i = 0; i < n; i++) Assert.True(Math.Abs(x[i] - back[i] / n) < 2e-3, $"sample {i}");
    }

    [Fact]
    public void Stft_then_istft_returns_a_separator_window()
    {
        const int nFft = 7680, hop = 1024, frames = 256, len = hop * (frames - 1);   // 261,120 samples
        var x = new float[len];
        for (int i = 0; i < len; i++) x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * (200 + i * 0.01) * i / 44100.0));  // chirp
        var stft = new AiLyrics.Dsp.Stft(nFft, hop);
        Assert.Equal(frames, stft.Frames(len));
        int bins = nFft / 2 + 1;
        var re = new float[bins * frames]; var im = new float[bins * frames];
        stft.Forward(x, re, im, bins);
        var y = new float[len];
        stft.Inverse(re, im, frames, bins, y);
        double maxErr = 0;
        for (int i = 0; i < len; i++) maxErr = Math.Max(maxErr, Math.Abs(x[i] - y[i]));
        Assert.True(maxErr < 1e-3, $"max abs error {maxErr}");
    }

    [Fact]
    public void Resampler_length_matches_scipy_and_keeps_a_1khz_sine_level()
    {
        var rs = new AiLyrics.Dsp.Resampler(160, 441);
        Assert.Equal(160, rs.Up); Assert.Equal(441, rs.Down);
        Assert.Equal((int)Math.Ceiling(44100 * 160 / 441.0), rs.OutLength(44100));
        var x = new float[44100];
        for (int i = 0; i < x.Length; i++) x[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 44100.0);
        var y = new float[rs.OutLength(x.Length)];
        rs.Process(x, y);
        double peak = 0;
        for (int i = 2000; i < y.Length - 2000; i++) peak = Math.Max(peak, Math.Abs(y[i]));
        Assert.InRange(peak, 0.99, 1.01);
    }

    [Fact]
    public void Envelope_reports_the_level_of_a_known_rms()
    {
        var env = new AiLyrics.Dsp.Envelope(44100);
        var x = new float[44100];
        Array.Fill(x, 0.1f);                                  // RMS 0.1 => -20 dB
        env.Append(x);
        Assert.Equal(100, env.Count);
        Assert.Equal(-20.0, env.At(50), 1);
    }
}
