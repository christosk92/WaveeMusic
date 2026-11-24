// ── Playback/Timecode.cs ───────────────────────────────────────────────────────────────────────────────────────────────────
// SMPTE linear timecode (#154): the four rates, drop-frame labelling, the 80-bit word and the bi-phase mark encoder.
//
// Role: CORE (pure; no I/O, no allocation). The generator and the self-check reader are in Timecode.Ltc.cs, the settings and the
// output's lifetime in Timecode.Host.cs.
//
// THE WORD (SMPTE ST 12-1 LTC; bit 0 sent first, each field least significant bit first). 0-3 frame units, 4-7 user group 1,
// 8-9 frame tens, 10 drop-frame flag, 11 colour-frame flag, 12-15 user 2, 16-19 seconds units, 20-23 user 3, 24-26 seconds tens,
// 27 polarity correction (30 / 29.97 / 24 fps; BGF0 at 25), 28-31 user 4, 32-35 minutes units, 36-39 user 5, 40-42 minutes tens,
// 43 BGF0 (BGF2 at 25), 44-47 user 6, 48-51 hours units, 52-55 user 7, 56-57 hours tens, 58 BGF1, 59 BGF2 (polarity correction at
// 25), 60-63 user 8, 64-79 the sync word 0011 1111 1111 1101. User bits and flags are sent as zero. The polarity correction bit
// makes the word's count of ones even, so every word starts on the same edge.
//
// CHECKED. Written from the layout, not from any library (libltc is LGPL); decoded by an independent decoder (the `ltc-reader`
// package, its own layout table) at all four rates, every frame compared with the expected label (night-2 hand-over).
//
// BI-PHASE MARK. Every bit is two half-bit cells: the level flips at every bit boundary, and once more in the middle of a 1.
// The cell clock is a phase accumulator, so 29.97 fps, a 44.1 kHz device and a varispeed need no special case.

using System.Numerics;

namespace Wavee;

/// <summary>An LTC frame rate.</summary>
public enum LtcRate : byte { Fps24, Fps25, Fps2997Drop, Fps30 }

/// <summary>LTC arithmetic: rates, labels (drop-frame included) and the 80-bit word. Pure.</summary>
public static class Timecode
{
    /// <summary>Frames per second of <paramref name="rate"/> (29.97 is 30000/1001).</summary>
    public static double FramesPerSecond(LtcRate rate) => rate switch
    {
        LtcRate.Fps24 => 24, LtcRate.Fps25 => 25, LtcRate.Fps2997Drop => 30000.0 / 1001.0, _ => 30,
    };

    /// <summary>The frame count a label's frame field wraps at (24, 25 or 30).</summary>
    public static int Nominal(LtcRate rate) => rate switch { LtcRate.Fps24 => 24, LtcRate.Fps25 => 25, _ => 30 };

    /// <summary>Frame count since 00:00:00:00 → the label on that frame. Drop-frame skips the labels :00 and :01 at the start of
    /// every minute that is not a multiple of ten (17 982 frames per ten minutes, 1798 per dropping minute). Wraps at 24 h.</summary>
    public static void Label(long frame, LtcRate rate, out int h, out int m, out int s, out int f)
    {
        if (frame < 0) frame = 0;
        int n = Nominal(rate);
        if (rate == LtcRate.Fps2997Drop)
        {
            long tens = frame / 17982, rest = frame % 17982;
            frame += 18 * tens + (rest < 2 ? 0 : 2 * ((rest - 2) / 1798));
        }
        f = (int)(frame % n);
        long sec = frame / n;
        s = (int)(sec % 60);
        m = (int)(sec / 60 % 60);
        h = (int)(sec / 3600 % 24);
    }

    /// <summary>The inverse of <see cref="Label"/>: the frame count a label stands for (a drop-frame label that does not exist maps to
    /// the next one that does).</summary>
    public static long FrameOf(int h, int m, int s, int f, LtcRate rate)
    {
        int n = Nominal(rate);
        long nominal = ((long)h * 3600 + m * 60 + s) * n + f;
        if (rate != LtcRate.Fps2997Drop) return nominal;
        long minutes = (long)h * 60 + m;
        return nominal - 2 * (minutes - minutes / 10);
    }

    /// <summary>The 80-bit word for a label: bits 0-63 in <paramref name="lo"/>, the sync word in <paramref name="hi"/> (bit 64 = bit 0).</summary>
    public static void Pack(int h, int m, int s, int f, LtcRate rate, out ulong lo, out ushort hi)
    {
        lo = (ulong)(f % 10) | (ulong)(f / 10) << 8 | (ulong)(s % 10) << 16 | (ulong)(s / 10) << 24
           | (ulong)(m % 10) << 32 | (ulong)(m / 10) << 40 | (ulong)(h % 10) << 48 | (ulong)(h / 10) << 56;
        if (rate == LtcRate.Fps2997Drop) lo |= 1UL << 10;
        hi = 0xBFFC;                                            // 0011 1111 1111 1101, bit 64 first
        int ones = BitOperations.PopCount(lo) + BitOperations.PopCount(hi);
        if ((ones & 1) != 0) lo |= 1UL << (rate == LtcRate.Fps25 ? 59 : 27);
    }

    /// <summary>The label of a word's 64 data bits (what a reader recovers): the inverse of <see cref="Pack"/>'s time fields.</summary>
    public static void Unpack(ulong lo, out int h, out int m, out int s, out int f, out bool dropFrame)
    {
        f = (int)(lo & 0xF) + 10 * (int)(lo >> 8 & 0x3);
        s = (int)(lo >> 16 & 0xF) + 10 * (int)(lo >> 24 & 0x7);
        m = (int)(lo >> 32 & 0xF) + 10 * (int)(lo >> 40 & 0x7);
        h = (int)(lo >> 48 & 0xF) + 10 * (int)(lo >> 56 & 0x3);
        dropFrame = (lo >> 10 & 1) != 0;
    }

    /// <summary>"hh:mm:ss:ff" (";" before the frames for drop-frame, the convention). Allocates: diagnostics only.</summary>
    public static string Format(long frame, LtcRate rate)
    {
        Label(frame, rate, out int h, out int m, out int s, out int f);
        return $"{h:00}:{m:00}:{s:00}{(rate == LtcRate.Fps2997Drop ? ';' : ':')}{f:00}";
    }
}

/// <summary>The bi-phase mark modulator: 160 half-bit cells per frame, a phase-accumulator cell clock. No allocation.</summary>
public struct LtcEncoder
{
    ulong _lo; ushort _hi; int _cell; double _phase; float _level;

    /// <summary>The frame being sent (frames since 00:00:00:00 at <see cref="Rate"/>).</summary>
    public long Frame;
    public LtcRate Rate;

    /// <summary>Jump to <paramref name="frame"/>, <paramref name="cellPhase"/> (0..1) of the way through it, at
    /// <paramref name="amplitude"/>. The level keeps its sign, so a relocation adds no extra edge.</summary>
    public void Relocate(long frame, double cellPhase, float amplitude)
    {
        cellPhase = Math.Clamp(cellPhase, 0, 0.999999);
        Frame = Math.Max(0, frame);
        _cell = (int)(cellPhase * 160);
        _phase = cellPhase * 160 - _cell;
        _level = _level < 0 ? -amplitude : amplitude;
        Load();
    }

    /// <summary>Change the amplitude in place (a level dial), keeping the sign.</summary>
    public void SetAmplitude(float amplitude) => _level = _level < 0 ? -amplitude : amplitude;

    /// <summary>Position inside the timeline, in frames with a fraction: what a reader will decode next.</summary>
    public readonly double FramePosition => Frame + (_cell + _phase) / 160.0;

    /// <summary>Fill <paramref name="frames"/> interleaved frames, every channel the same, advancing <paramref name="cellsPerSample"/>
    /// half-bit cells per sample.</summary>
    public void Render(Span<float> dst, int frames, int channels, double cellsPerSample)
    {
        for (int i = 0; i < frames; i++)
        {
            _phase += cellsPerSample;
            while (_phase >= 1.0) { _phase -= 1.0; Step(); }
            for (int c = 0; c < channels; c++) dst[i * channels + c] = _level;
        }
    }

    void Step()
    {
        if (++_cell == 160) { _cell = 0; Frame++; Load(); }
        int bit = _cell >> 1;
        bool one = bit < 64 ? (_lo >> bit & 1) != 0 : (_hi >> (bit - 64) & 1) != 0;
        if ((_cell & 1) == 0 || one) _level = -_level;          // every bit boundary; mid-bit only for a 1
    }

    void Load()
    {
        Timecode.Label(Frame, Rate, out int h, out int m, out int s, out int f);
        Timecode.Pack(h, m, s, f, Rate, out _lo, out _hi);
    }
}
