// ── Wavee.Tests/Mp3DecoderTests.cs — the local-MP3 path: NLayer behind Playback.Audio.Mp3AudioDecoder (plan §5, WP-4d) ──
//
// Spotify never serves MP3, but a local library does, and the app plays it through ONE adapter over ONE third-party
// decoder (NLayer 1.15.0, a managed MPEG-1/2/2.5 Layer I-III decoder that only speaks `Stream`). Everything below goes
// through the adapter's public seam — `TryOpen` / `Read` / `Seek` / `Gapless` — over `Fixtures/mp3/sine-440.mp3`: six
// seconds of a 440 Hz sine, 44.1 kHz mono, 64 kbit/s, written by ffmpeg's libmp3lame with a LAME tag
// (`Fixtures/mp3/README.md` has the command and every number measured with ffmpeg and with NLayer directly).
//
//   THE TONE      the decode has the right frequency, level and purity at the file's own rate and after the engine's linear
//                 resample to a 48 kHz device; the mono source is conformed to stereo exactly.
//   THE GAPLESS   the LAME tag's delay and padding become the engine's lead-in and trail with the decoder's own 529 added,
//                 and the three numbers account for every sample NLayer decodes — which is also what ffmpeg trims.
//   THE ALLOCATION  the plan asked for "two hundred frames allocate < 64 KiB". NLayer itself allocates ~40 KB per MPEG
//                 frame (the README has the measurement), so the fact is a CEILING per frame that catches a regression
//                 (the adapter re-allocating a block buffer would not show against that floor; it is one reused array).
//   THE SEEK      a seek lands within a few MPEG frames of the target. NLayer's `Position` is in BYTES of float samples.

using System.IO;
using FluentGpu.Media;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class Mp3DecoderTests(ITestOutputHelper output)
{
    const int SourceRate = 44_100;
    const int FrameSamples = 1_152;                          // MPEG-1 Layer III: samples per frame
    const long DecodedSamples = 231L * FrameSamples;         // the Info tag's 231 frames: 266,112 samples, none trimmed
    const int MaxFramesForTheFixture = 300_000;              // the 48 kHz decode is 289,645 frames

    /// <summary>The ceiling the allocation fact holds NLayer to, per decoded MPEG frame (measured ~40 KB).</summary>
    const double MaxBytesPerFrame = 64 * 1024;

    static byte[] Bytes() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mp3", "sine-440.mp3"));

    static Playback.Audio.Mp3AudioDecoder Open(int mixRate, out DecodedInfo info)
    {
        var decoder = new Playback.Audio.Mp3AudioDecoder(0f);
        Assert.True(decoder.TryOpen(new BytesSource(Bytes()), new MixFormat(mixRate, 2), out info));
        return decoder;
    }

    /// <summary>Read until the decoder answers 0 twice running (a resampler may answer 0 once between two pulls). Interleaved
    /// <paramref name="channels"/>-channel floats, and the frame count.</summary>
    static (float[] Pcm, long Frames) Drain(Playback.Audio.Mp3AudioDecoder decoder, int channels, int maxFrames)
    {
        var pcm = new float[maxFrames * channels];
        var block = new float[2_048 * channels];
        long frames = 0;
        int zeros = 0;
        for (int calls = 0; zeros < 2 && calls < 1_000_000; calls++)
        {
            int n = decoder.Read(block);
            Assert.True(n >= 0);
            if (n == 0) { zeros++; continue; }
            zeros = 0;
            Assert.True((frames + n) * channels <= pcm.Length, "the decoder produced more than the file holds");
            block.AsSpan(0, n * channels).CopyTo(pcm.AsSpan((int)(frames * channels)));
            frames += n;
        }
        return (pcm, frames);
    }

    // ── 1. open ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Opens_reports_the_source_format_and_a_duration_of_six_seconds()
    {
        Playback.Audio.Mp3AudioDecoder decoder = Open(SourceRate, out DecodedInfo info);
        Assert.Equal(new MixFormat(SourceRate, 1), info.SourceFormat);       // the FILE's format; the mix is stereo
        Assert.Equal(FluentGpu.Media.Container.Mp3, info.Codec.Container);
        Assert.Equal(CodecId.Mp3, info.Codec.Audio);
        Assert.InRange(info.Duration.TotalSeconds, 5.9, 6.1);                // NLayer's figure is 6.034 s: whole frames
        Assert.Equal(1f, decoder.AppliedGainLinear);                         // 0 dB folds nothing
    }

    // ── 2. the tone ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    public void A_440_hz_sine_decodes_at_the_right_frequency_and_level(int mixRate)
    {
        Playback.Audio.Mp3AudioDecoder decoder = Open(mixRate, out _);
        (float[] pcm, long frames) = Drain(decoder, 2, MaxFramesForTheFixture);

        // a mono source in a stereo mix: both channels identical, exactly
        int mismatched = 0;
        for (long i = 0; i < frames; i++) if (pcm[2 * i] != pcm[2 * i + 1]) mismatched++;
        Assert.Equal(0, mismatched);

        long expected = (long)Math.Round(DecodedSamples * (double)mixRate / SourceRate);
        if (mixRate == SourceRate) Assert.Equal(DecodedSamples, frames);       // 231 frames x 1,152, none trimmed
        else Assert.InRange(frames, expected - 8, expected + 8);               // the linear resampler's count

        Tone tone = Analyze(pcm, 2, frames, mixRate, 440);
        output.WriteLine($"{mixRate} Hz mix: {frames:N0} frames, {tone.Hz:F4} Hz, amplitude {tone.Amplitude:F5}, SNR {tone.SnrDb:F1} dB");
        Assert.InRange(tone.Hz, 439.95, 440.05);
        // The file holds 0.4750 (the nominal is 0.5; ffmpeg's decoder measures the same), so 0.465-0.485 is a ±2 % window on it
        Assert.InRange(tone.Amplitude, 0.465, 0.485);
        Assert.True(tone.SnrDb > 50, $"SNR {tone.SnrDb:F1} dB");               // measured 80.7 dB at 44.1 kHz, 75.3 dB at 48 kHz
    }

    readonly record struct Tone(double Hz, double Amplitude, double SnrDb);

    /// <summary>Frequency from the interpolated positive-going zero crossings, amplitude and SNR from a least-squares fit of
    /// a·sin + b·cos at <paramref name="nominalHz"/>, over channel 0 without the first and last 8,192 frames.</summary>
    static Tone Analyze(float[] pcm, int channels, long frames, int rate, double nominalHz)
    {
        int lo = 8_192, hi = (int)frames - 8_192;
        Assert.True(hi - lo > 100_000, "too little audio to measure");

        double first = 0, last = 0;
        int crossings = 0;
        for (int i = lo; i < hi; i++)
        {
            double a = pcm[i * channels], b = pcm[(i + 1) * channels];
            if (a >= 0 || b < 0) continue;
            double at = i - a / (b - a);
            if (crossings == 0) first = at;
            last = at;
            crossings++;
        }
        Assert.True(crossings > 100, "no tone");
        double hz = rate * (crossings - 1) / (last - first);

        double w = 2 * Math.PI * nominalHz / rate;
        double ss = 0, cc = 0, sc = 0, xs = 0, xc = 0;
        for (int i = lo; i < hi; i++)
        {
            double s = Math.Sin(w * i), c = Math.Cos(w * i), x = pcm[i * channels];
            ss += s * s; cc += c * c; sc += s * c; xs += x * s; xc += x * c;
        }
        double det = ss * cc - sc * sc;
        double p = (xs * cc - xc * sc) / det, q = (xc * ss - xs * sc) / det;
        double signal = 0, noise = 0;
        for (int i = lo; i < hi; i++)
        {
            double fit = p * Math.Sin(w * i) + q * Math.Cos(w * i);
            double r = pcm[i * channels] - fit;
            signal += fit * fit;
            noise += r * r;
        }
        return new Tone(hz, Math.Sqrt(p * p + q * q), 10 * Math.Log10(signal / noise));
    }

    // ── 3. the gapless numbers ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_lame_tag_gives_the_gapless_numbers_that_account_for_every_decoded_sample()
    {
        // ffmpeg wrote delay 576, padding 936 and 231 frames: 231 x 1,152 - 576 - 936 = 264,600 = exactly 6 s.
        using var stream = new MemoryStream(Bytes());
        Assert.True(Playback.Audio.Mp3Tag.TryProbe(stream, out Playback.Audio.Mp3Tag tag));
        Assert.Equal(new Playback.Audio.Mp3Tag(DelaySamples: 576, PaddingSamples: 936, TotalSamples: 264_600), tag);
        Assert.Equal(0L, stream.Position);                                     // the probe puts the position back
        using var silence = new MemoryStream(new byte[2_000]);
        Assert.False(Playback.Audio.Mp3Tag.TryProbe(silence, out _));              // no sync word, no tag

        // The adapter: the encoder's delay plus the decoder's own 529 in front, the padding less 529 behind.
        Playback.Audio.Mp3AudioDecoder decoder = Open(SourceRate, out _);
        GaplessInfo g = decoder.Gapless;
        const int DecoderDelay = Playback.Audio.Mp3Tag.DecoderDelaySamples;
        Assert.Equal(new GaplessInfo(576 + DecoderDelay, 936 - DecoderDelay, 264_600, TailKnown: true), g);

        // ...and the three numbers add up to exactly what NLayer hands out, which is what ffmpeg trims to get 264,600
        (_, long frames) = Drain(decoder, 2, MaxFramesForTheFixture);
        Assert.Equal(frames, g.LeadInFrames + g.ExactFrames + g.TrailPadFrames);
    }

    // ── 4. the allocation ceiling ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_hundred_frames_stay_under_the_allocation_ceiling()
    {
        Playback.Audio.Mp3AudioDecoder decoder = Open(SourceRate, out _);
        var block = new float[4_096 * 2];
        Assert.True(decoder.Read(block) > 0);                                  // warm: JIT, NLayer's tables, the adapter's buffers

        long samples = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (samples < 200 * FrameSamples)
        {
            int n = decoder.Read(block);
            if (n <= 0) break;
            samples += n;                                                       // mono source: one frame per sample
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(samples >= 200 * FrameSamples, $"only {samples:N0} samples decoded");
        double perFrame = allocated / ((double)samples / FrameSamples);
        output.WriteLine($"{samples / FrameSamples} MPEG frames allocated {allocated:N0} bytes: {perFrame:N0} bytes per frame");
        Assert.True(perFrame < MaxBytesPerFrame, $"{perFrame:N0} bytes allocated per MPEG frame (ceiling {MaxBytesPerFrame:N0})");
    }

    // ── 5. the seek ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Seek_lands_near_the_target()
    {
        Playback.Audio.Mp3AudioDecoder decoder = Open(SourceRate, out _);
        const long target = 3L * SourceRate;                                    // 132,300 of 266,112
        long landed = decoder.Seek(target);
        // NLayer lands on an MPEG frame boundary (1 152 samples per Layer III frame) near the target, and Seek REPORTS that
        // landing — the clock rebases on the audio that plays, not on the ask. A seek that misjudged the unit lands seconds away.
        Assert.InRange(landed, target - 1_152, target + 1_152);

        // …and the report is the truth: what remains is the file's length less where it landed.
        (_, long remaining) = Drain(decoder, 2, MaxFramesForTheFixture);
        long expected = DecodedSamples - landed;
        output.WriteLine($"seek to {target:N0}: {remaining:N0} samples remain, {expected:N0} expected");
        Assert.InRange(remaining, expected - 3_072, expected + 3_072);
    }

    /// <summary>A byte array behind the engine's plain sequential face — a local file, as the adapter sees one.</summary>
    sealed class BytesSource(byte[] data) : IMediaByteSource
    {
        long _cursor;

        public long? Length => data.Length;
        public SourceCaps Caps => new() { Seekable = true, KnownLength = true, ExpensiveSeek = false };
        public bool TryOpen(in DataSpec spec) { _cursor = Math.Max(0, spec.Position); return true; }

        public int Read(Span<byte> dst)
        {
            if (_cursor >= data.Length) return 0;
            int n = (int)Math.Min(dst.Length, data.Length - _cursor);
            data.AsSpan((int)_cursor, n).CopyTo(dst);
            _cursor += n;
            return n;
        }

        public long Seek(long offset) => _cursor = Math.Clamp(offset, 0, data.Length);

        public void Cancel() { }

        public void Close() { }
    }
}
