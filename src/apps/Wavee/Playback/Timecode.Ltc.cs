// ── Playback/Timecode.Ltc.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// The LTC generator (an IAuxAudioGenerator on the aux output's thread) and its self-check reader (#154 phase 3).
//
// Role: SHELL (runs on the engine's aux thread; reads Playback.Timeline, writes only its own counters). No allocation, no lock.
//
// THE GENERATOR CHASES THE TIMELINE; IT IS NEVER SAMPLE-LOCKED TO IT. Two devices, two crystals. For every block the aux output
// says when its first frame will be heard (AuxBlock.FirstFrameAudibleQpc100ns); the timeline says where the music is audible at
// that instant; the difference from where the encoder will be is the error in LTC frames. Past half a frame (or on a new epoch:
// a seek, a pause, a resume, a track change) the encoder relocates; inside it the cell clock slews by at most 0.2 % to absorb the
// drift between the two devices. While nothing plays the output ramps to silence in 5 ms (D7): a stopped reader is the normal
// "transport stopped" state. Outside 0.8x..1.25x (an episode at 2x) it sends real-time frames and relocates as needed.
//
// THE READER is diagnostics code, not a test: a bi-phase mark decoder over the generator's own output (edge intervals -> bits ->
// the sync word -> the label), always on while the generator runs (a sign test per sample on the aux thread). It proves that what
// is sent decodes to what was meant (Diagnostics › Playback runtime, `timecode do=status`); it shares the word layout with the
// encoder, so the layout itself was checked once by an independent decoder (Timecode.cs header).

using System.Threading;

using FluentGpu.Media;

namespace Wavee;

/// <summary>The LTC generator: chases <see cref="Playback.Timeline"/> on the aux output's thread.</summary>
public sealed class LtcGenerator : IAuxAudioGenerator
{
    /// <summary>The settings a block reads, swapped whole (a dial change allocates one; a block never does).</summary>
    public sealed record Config(LtcRate Rate, int StartHour, float Amplitude, long OffsetTicks);

    Config _config = new(LtcRate.Fps30, 1, 0.25f, 0);
    LtcEncoder _enc;
    bool _locked;
    long _epoch;
    float _gain;                // 0..1, ramps 5 ms toward 1 while playing and 0 while not
    readonly LtcReader _reader = new();
    LtcRate _encRate;
    bool _relocatedThisBlock;

    // The last relocations, for the host's log (aux thread writes slot [n & 7] then bumps the count; the host reads behind it).
    readonly long[] _relocQpc = new long[8];
    readonly long[] _relocFrame = new long[8];
    readonly long[] _relocEpoch = new long[8];

    // published for diagnostics (aux thread writes, any thread reads)
    long _sentFrame = -1, _relocations, _blocks;
    long _errorBits;            // double: the last block's error, LTC frames
    int _state;                 // 0 silent, 1 locked
    long _matches, _mismatches;

    /// <summary>Apply new settings (any thread). The next block reads them; a rate change relocates.</summary>
    public void Configure(Config config) => Volatile.Write(ref _config, config);

    public Config Current => Volatile.Read(ref _config);

    /// <summary>Relocation <paramref name=index/> (0-based; only the last eight are kept): the instant its first frame is heard, the LTC
    /// frame it jumped to, the timeline epoch that caused it. False when it is no longer kept.</summary>
    public bool TryGetRelocation(long index, out long qpc100ns, out long frame, out long epoch)
    {
        long n = Interlocked.Read(ref _relocations);
        qpc100ns = frame = epoch = 0;
        if (index < 0 || index >= n || index < n - 8) return false;
        qpc100ns = _relocQpc[index & 7]; frame = _relocFrame[index & 7]; epoch = _relocEpoch[index & 7];
        return true;
    }

    /// <summary>A reading of the generator for the diagnostics page and the inspector (any thread).</summary>
    public readonly record struct Status(bool Locked, long SentFrame, double ErrorFrames, long Relocations, long Blocks,
        long ReadFrame, long ReadCount, long Matches, long Mismatches, LtcRate Rate);

    public Status Read() => new(Volatile.Read(ref _state) == 1, Interlocked.Read(ref _sentFrame),
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _errorBits)), Interlocked.Read(ref _relocations), Interlocked.Read(ref _blocks),
        _reader.LastFrame, _reader.Completed, Interlocked.Read(ref _matches), Interlocked.Read(ref _mismatches), Volatile.Read(ref _config).Rate);

    /// <inheritdoc/>
    public void Render(Span<float> dst, int frames, in AuxBlock block)
    {
        Interlocked.Increment(ref _blocks);
        _relocatedThisBlock = false;
        Config cfg = Volatile.Read(ref _config);
        float rampStep = 1f / Math.Max(1, block.SampleRate / 200);   // 5 ms
        bool live = Playback.Timeline.TryRead(out Playback.Timeline.Snapshot t) && t.Playing;
        if (!live)
        {
            // Hold the encoder's level and ramp it out; then silence. Nothing relocates until the timeline plays again.
            float level = _gain > 0f ? LevelOf() : 0f;
            for (int i = 0; i < frames; i++)
            {
                _gain = Math.Max(0f, _gain - rampStep);
                float v = level * _gain;
                for (int c = 0; c < block.Channels; c++) dst[i * block.Channels + c] = v;
            }
            _locked = false;
            Volatile.Write(ref _state, 0);
            FeedReader(dst, frames, block);
            return;
        }

        double fps = Timecode.FramesPerSecond(cfg.Rate);
        double speed = t.Rate is >= 0.8 and <= 1.25 ? t.Rate : 1.0;   // varispeed inside the band readers tolerate; real time outside
        double seconds = t.PositionMicrosAt(block.FirstFrameAudibleQpc100ns + cfg.OffsetTicks) / 1e6;
        double target = Timecode.FrameOf(cfg.StartHour, 0, 0, 0, cfg.Rate) + seconds * fps;   // where a reader should be as this block is heard
        if (cfg.Rate != _encRate) { _encRate = cfg.Rate; _enc.Rate = cfg.Rate; _locked = false; }
        double error = target - _enc.FramePosition;
        if (!_locked || t.Epoch != _epoch || Math.Abs(error) > 0.5)
        {
            _enc.Rate = cfg.Rate;
            _enc.Relocate((long)Math.Floor(target), target - Math.Floor(target), cfg.Amplitude);
            _epoch = t.Epoch;
            _locked = true;
            long n = Interlocked.Read(ref _relocations);
            _relocQpc[n & 7] = block.FirstFrameAudibleQpc100ns;
            _relocFrame[n & 7] = (long)Math.Floor(target);
            _relocEpoch[n & 7] = t.Epoch;
            Interlocked.Increment(ref _relocations);
            _relocatedThisBlock = true;
            error = 0;
        }
        else _enc.SetAmplitude(cfg.Amplitude);
        double nominal = fps * 160.0 * speed / block.SampleRate;            // cells per sample at the playing rate
        double slew = Math.Clamp(error * 0.05, -0.002, 0.002);              // absorb the two clocks' drift, at most 0.2 %
        _enc.Render(dst, frames, block.Channels, nominal * (1.0 + slew));
        if (_gain < 1f)
            for (int i = 0; i < frames; i++)
            {
                _gain = Math.Min(1f, _gain + rampStep);
                for (int c = 0; c < block.Channels; c++) dst[i * block.Channels + c] *= _gain;
            }
        Interlocked.Exchange(ref _sentFrame, _enc.Frame);
        Interlocked.Exchange(ref _errorBits, BitConverter.DoubleToInt64Bits(error));
        Volatile.Write(ref _state, 1);
        FeedReader(dst, frames, block);
    }

    float LevelOf()
    {
        // The encoder's current level, without advancing it: render one sample into a stack cell at zero cell rate.
        Span<float> one = stackalloc float[1];
        _enc.Render(one, 1, 1, 0);
        return one[0];
    }

    void FeedReader(Span<float> dst, int frames, in AuxBlock block)
    {
        long before = _reader.Completed;
        _reader.Feed(dst, frames, block.Channels, block.SampleRate, Timecode.FramesPerSecond(Volatile.Read(ref _config).Rate),
            Volatile.Read(ref _config).Rate);
        long done = _reader.Completed;
        // A block that relocated may end a word that straddles the jump: not a fair comparison, and the next word is.
        if (done == before || _state == 0 || _relocatedThisBlock) return;
        // The last word the reader completed is the frame the encoder finished last: the one before the frame in progress.
        if (_reader.LastFrame == _enc.Frame - 1) Interlocked.Increment(ref _matches);
        else Interlocked.Increment(ref _mismatches);
    }
}

/// <summary>A bi-phase mark LTC decoder (the generator's self-check). Feed it the generator's output; it publishes the last label
/// it decoded as a frame count. No allocation.</summary>
public sealed class LtcReader
{
    int _lastSign, _sinceEdge;
    bool _halfPending;
    UInt128 _window;
    int _bits;
    long _lastFrame = -1, _completed;

    /// <summary>The frame count of the last word decoded (−1 before the first).</summary>
    public long LastFrame => Interlocked.Read(ref _lastFrame);

    /// <summary>Words decoded since the last reset.</summary>
    public long Completed => Interlocked.Read(ref _completed);

    public void Reset()
    {
        _lastSign = 0; _sinceEdge = 0; _halfPending = false; _window = 0; _bits = 0;
        Interlocked.Exchange(ref _lastFrame, -1);
    }

    /// <summary>Decode channel 0 of <paramref name="frames"/> interleaved frames. A bit cell is <c>rate / (fps × 80)</c> samples;
    /// an interval shorter than three quarters of it is a half (two make a 1), longer is a 0.</summary>
    public void Feed(ReadOnlySpan<float> src, int frames, int channels, int sampleRate, double fps, LtcRate rate)
    {
        double bitSamples = sampleRate / (fps * 80.0);
        for (int i = 0; i < frames; i++)
        {
            float v = src[i * channels];
            int sign = v > 1e-6f ? 1 : v < -1e-6f ? -1 : 0;
            _sinceEdge++;
            if (sign == 0 || sign == _lastSign) continue;
            bool first = _lastSign == 0;
            _lastSign = sign;
            int interval = _sinceEdge;
            _sinceEdge = 0;
            if (first) continue;
            if (interval < bitSamples * 0.25) continue;            // not an edge of this signal
            if (interval < bitSamples * 0.75)
            {
                if (_halfPending) { _halfPending = false; Push(1, rate); }
                else _halfPending = true;
            }
            else if (interval < bitSamples * 1.5)
            {
                _halfPending = false;
                Push(0, rate);
            }
            else { _halfPending = false; _bits = 0; }                // silence or a gap: start over
        }
    }

    void Push(int bit, LtcRate rate)
    {
        _window = (_window >> 1) | ((UInt128)(uint)bit << 79);
        if (++_bits < 80) return;
        if ((ushort)(_window >> 64) != 0xBFFC) return;
        Timecode.Unpack((ulong)_window, out int h, out int m, out int s, out int f, out _);
        Interlocked.Exchange(ref _lastFrame, Timecode.FrameOf(h, m, s, f, rate));
        Interlocked.Increment(ref _completed);
        _bits = 0;
    }
}
