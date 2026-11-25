// ── Playback/Playback.Audio.Pcm.cs ──────────────────────────────────────────────────────────────────────────────────
// uncompressed PCM in WAV (RIFF, RF64, BW64) and AIFF/AIFC: the layout parser (pure, shared by the tag reader, the content
// probe and the decoder), the decoder, and the decoder a format with none gets (#240, 0dm's "WAV first")
//
// Role: CORE (the parser: any thread, pure; the decoder: the engine's decode-ahead thread, blocking only in the byte seam)
// Spec: C:\WAVEE\fgpu\handoff\240-0dm-suggestions.md §2 (W1, the AIFF half, the re-read)
//
// WHAT IS PLAYED. WAV: WAVE_FORMAT_PCM (8-bit unsigned, 16, 24 and 32-bit signed), WAVE_FORMAT_IEEE_FLOAT (32 and 64-bit) and
// WAVE_FORMAT_EXTENSIBLE with either sub-format (wValidBitsPerSample is the label's depth; the samples are read at their container
// size). AIFF: big-endian signed 8-32 bit; AIFC: NONE/twos, sowt (little-endian), fl32 and fl64. Anything else is REFUSED with a
// plain why that names the encoding ("a WAV file with ADPCM audio", "an AIFF-C file with µ-law audio"): ADPCM, µ-law, A-law, MP3
// or MPEG in WAV, an unknown sub-format.
//
// THE WALK seeks chunk to chunk (it never stops at `data`): `fmt ` may follow `data`, and LIST, cue, bext (BWF), iXML, id3 and
// JUNK/PAD chunks are stepped over, each padded to an even size. RF64/BW64 take the 64-bit `data` size from `ds64`. A streamed
// recording's size (0 or 0xFFFFFFFF with no `ds64`) runs to the end of the file. AIFF's `COMM` frame count bounds `SSND`.
//
// THE OUTPUT is stereo float at the mix rate. Mono is copied to both sides; more than two channels are downmixed by the standard
// matrix (fronts at unity, centre and surrounds at -3 dB, back centre at -6 dB to each side, LFE dropped), by the channel mask when the
// file has one, else by the default order for its channel count: the same matrix as a multichannel FLAC (Flac.ToFloatMulti), so a
// 5.1 file plays as loud in either container (a peak over full scale meets the output limiter, as FLAC's does). The normalisation
// figure is folded into the conversion's scale (IGainFolding). PCM has no encoder delay or padding: the length is data / block-align frames,
// exactly, reported as the gapless info's exact frames, so a join or a crossfade is planned on it as on FLAC's; a seek is
// frame-exact (data start + frame x block align).

using System.Buffers.Binary;
using FluentGpu.Media;

namespace Wavee;

public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>Where the PCM came from (the label's first word).</summary>
        public enum PcmContainer : byte { Wav, Aiff }

        /// <summary>How one sample is stored.</summary>
        public enum PcmSample : byte { Int, UInt8, Float }

        /// <summary>A PCM stream's layout, or why it cannot be played (<see cref="Why"/> not empty).</summary>
        public readonly record struct PcmLayout(PcmContainer Container, PcmSample Sample, bool BigEndian, int BytesPerSample, int ValidBits,
            int Channels, int SampleRate, uint ChannelMask, long DataStart, long DataBytes, string Why)
        {
            public bool Ok => Why.Length == 0;
            public int BlockAlign => BytesPerSample * Channels;
            public long Frames => BlockAlign > 0 ? DataBytes / BlockAlign : 0;
            public long DurationMs => SampleRate > 0 ? Frames * 1000 / SampleRate : 0;
            /// <summary>The depth people name: a float's container size, else the valid bits.</summary>
            public int Bits => Sample == PcmSample.Float || ValidBits <= 0 ? BytesPerSample * 8 : ValidBits;
            /// <summary>"WAV 24/96", "AIFF 16/44.1": the source's own depth and rate (the output is float at the device rate).</summary>
            public string Label => (Container == PcmContainer.Wav ? "WAV " : "AIFF ") + Bits.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                   + "/" + (SampleRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

            public static PcmLayout Refused(PcmContainer container, string why) => new(container, default, false, 0, 0, 0, 0, 0, 0, 0, why);
        }

        /// <summary>The PCM parser (see the file header). PURE over a reader of absolute offsets.</summary>
        public static class Pcm
        {
            /// <summary>Fill <paramref name="dst"/> from absolute offset <paramref name="offset"/>; false when the bytes are not all there.</summary>
            public delegate bool ReadAt(long offset, Span<byte> dst);

            const uint Riff = 0x52494646, Rf64 = 0x52463634, Bw64 = 0x42573634, Wave = 0x57415645, Form = 0x464F524D, Aiff = 0x41494646, Aifc = 0x41494643;
            const uint FmtId = 0x666D7420, DataId = 0x64617461, Ds64Id = 0x64733634, CommId = 0x434F4D4D, SsndId = 0x53534E44;
            const int MaxChunks = 4096;

            /// <summary>A RIFF, RF64 or BW64 WAVE file's first 12 bytes.</summary>
            public static bool IsWave(ReadOnlySpan<byte> head)
                => head.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(head) is Riff or Rf64 or Bw64 && BinaryPrimitives.ReadUInt32BigEndian(head[8..]) == Wave;

            /// <summary>A FORM AIFF or AIFC file's first 12 bytes.</summary>
            public static bool IsAiff(ReadOnlySpan<byte> head)
                => head.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(head) == Form && BinaryPrimitives.ReadUInt32BigEndian(head[8..]) is Aiff or Aifc;

            /// <summary>The layout of a WAV or AIFF file of <paramref name="length"/> bytes, or a refusal naming why.</summary>
            public static PcmLayout Parse(ReadAt read, long length)
            {
                Span<byte> head = stackalloc byte[12];
                if (!read(0, head)) return PcmLayout.Refused(PcmContainer.Wav, "a damaged WAV file");
                if (IsWave(head)) return ParseWav(read, length, BinaryPrimitives.ReadUInt32BigEndian(head) != Riff);
                if (IsAiff(head)) return ParseAiff(read, length, BinaryPrimitives.ReadUInt32BigEndian(head[8..]) == Aifc);
                return PcmLayout.Refused(PcmContainer.Wav, "no audio format Wavee recognises");
            }

            /// <summary><see cref="Parse(ReadAt, long)"/> over the bytes in hand (a probe's head): a chunk past them is not seen.</summary>
            public static PcmLayout Parse(byte[] bytes, int count, long length)
                => Parse((offset, dst) =>
                {
                    if (offset < 0 || offset + dst.Length > count) return false;
                    bytes.AsSpan((int)offset, dst.Length).CopyTo(dst);
                    return true;
                }, length);

            static PcmLayout ParseWav(ReadAt read, long length, bool wide)
            {
                Span<byte> h = stackalloc byte[8];
                Span<byte> fmt = stackalloc byte[40];
                Span<byte> d = stackalloc byte[16];
                long pos = 12, ds64 = -1, dataStart = -1, dataBytes = -1;
                int fmtLen = 0;
                for (int guard = 0; guard < MaxChunks && pos + 8 <= length; guard++)
                {
                    if (!read(pos, h)) break;
                    uint id = BinaryPrimitives.ReadUInt32BigEndian(h);
                    long size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
                    long body = pos + 8;
                    bool streamed = false;
                    switch (id)
                    {
                        case Ds64Id:
                            if (read(body, d)) ds64 = BinaryPrimitives.ReadInt64LittleEndian(d[8..]);
                            break;
                        case FmtId:
                            fmtLen = (int)Math.Min(size, fmt.Length);
                            if (fmtLen < 16 || !read(body, fmt[..fmtLen])) fmtLen = 0;
                            break;
                        case DataId:
                            dataStart = body;
                            if (size == uint.MaxValue && wide && ds64 >= 0) dataBytes = ds64;
                            else if (size is 0 or uint.MaxValue) { dataBytes = length - body; streamed = true; }   // a streamed recording: to the end
                            else dataBytes = size;
                            dataBytes = Math.Clamp(dataBytes, 0, Math.Max(0, length - body));
                            size = dataBytes;
                            break;
                    }
                    if (dataStart >= 0 && (fmtLen > 0 || streamed)) break;   // both found, or nothing after a data chunk of unknown size
                    pos = body + size + (size & 1);
                }
                if (fmtLen == 0) return PcmLayout.Refused(PcmContainer.Wav, "a WAV file with no format chunk");
                if (dataStart < 0) return PcmLayout.Refused(PcmContainer.Wav, "a WAV file with no audio data");

                int tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                int rate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]), int.MaxValue);
                int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                int valid = bits;
                uint mask = 0;
                if (tag == 0xFFFE)
                {
                    if (fmtLen < 40) return PcmLayout.Refused(PcmContainer.Wav, "a damaged WAV file");
                    valid = BinaryPrimitives.ReadUInt16LittleEndian(fmt[18..]);
                    mask = BinaryPrimitives.ReadUInt32LittleEndian(fmt[20..]);
                    // KSDATAFORMAT_SUBTYPE_*: the format tag in the first two bytes of {xxxxxxxx-0000-0010-8000-00aa00389b71}.
                    tag = fmt[26..40].SequenceEqual(SubtypeTail) ? BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]) : -1;
                }
                PcmSample sample;
                switch (tag)
                {
                    case 1: sample = PcmSample.Int; break;
                    case 3: sample = PcmSample.Float; break;
                    default: return PcmLayout.Refused(PcmContainer.Wav, WavWhy(tag));
                }
                if (channels <= 0 || channels > 32 || blockAlign <= 0 || blockAlign % channels != 0 || rate <= 0 || rate > 1_536_000)
                    return PcmLayout.Refused(PcmContainer.Wav, "a damaged WAV file");
                int bytes = blockAlign / channels;
                if (sample == PcmSample.Int && bytes == 1) sample = PcmSample.UInt8;   // 8-bit WAV is unsigned
                if (sample == PcmSample.Float ? bytes is not (4 or 8) : bytes is < 1 or > 4)
                    return PcmLayout.Refused(PcmContainer.Wav, "a WAV file with " + bits.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-bit samples");
                if (valid <= 0 || valid > bytes * 8) valid = Math.Min(bits > 0 ? bits : bytes * 8, bytes * 8);
                return new PcmLayout(PcmContainer.Wav, sample, false, bytes, valid, channels, rate, mask, dataStart, dataBytes, "");
            }

            /// <summary>Bytes 2-15 of every KSDATAFORMAT_SUBTYPE GUID (its first two bytes are the format tag).</summary>
            static ReadOnlySpan<byte> SubtypeTail => [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

            /// <summary>Why a WAV file's encoding is not played: the encoding named in plain words.</summary>
            static string WavWhy(int tag) => tag switch
            {
                0x0002 or 0x0011 or 0x0010 or 0x0020 or 0x0040 => "a WAV file with ADPCM audio",
                0x0006 => "a WAV file with A-law audio",
                0x0007 => "a WAV file with µ-law audio",
                0x0055 => "a WAV file with MP3 audio",
                0x0050 => "a WAV file with MPEG audio",
                0x0160 or 0x0161 or 0x0162 or 0x0163 => "a WAV file with WMA audio",
                0x0092 or 0x2000 => "a WAV file with AC-3 audio",
                _ => "a WAV file with compressed audio",
            };

            static PcmLayout ParseAiff(ReadAt read, long length, bool aifc)
            {
                Span<byte> h = stackalloc byte[8];
                Span<byte> comm = stackalloc byte[22];
                Span<byte> ssnd = stackalloc byte[8];
                long pos = 12, dataStart = -1, dataBytes = -1;
                int commLen = 0;
                for (int guard = 0; guard < MaxChunks && pos + 8 <= length; guard++)
                {
                    if (!read(pos, h)) break;
                    uint id = BinaryPrimitives.ReadUInt32BigEndian(h);
                    long size = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
                    long body = pos + 8;
                    if (id == CommId)
                    {
                        commLen = (int)Math.Min(size, comm.Length);
                        if (commLen < 18 || !read(body, comm[..commLen])) commLen = 0;
                    }
                    else if (id == SsndId && read(body, ssnd))
                    {
                        long offset = BinaryPrimitives.ReadUInt32BigEndian(ssnd);
                        dataStart = body + 8 + offset;
                        dataBytes = size is 0 or uint.MaxValue ? length - dataStart : size - 8 - offset;
                        dataBytes = Math.Clamp(dataBytes, 0, Math.Max(0, length - dataStart));
                        if (size is 0 or uint.MaxValue) size = length - body;
                    }
                    if (dataStart >= 0 && commLen > 0) break;
                    pos = body + size + (size & 1);
                }
                if (commLen == 0) return PcmLayout.Refused(PcmContainer.Aiff, "an AIFF file with no format chunk");
                if (dataStart < 0) return PcmLayout.Refused(PcmContainer.Aiff, "an AIFF file with no audio data");

                int channels = BinaryPrimitives.ReadInt16BigEndian(comm);
                long frames = BinaryPrimitives.ReadUInt32BigEndian(comm[2..]);
                int bits = BinaryPrimitives.ReadInt16BigEndian(comm[6..]);
                int exponent = ((comm[8] & 0x7F) << 8) | comm[9];
                ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(comm[10..]);
                double rateD = Math.ScaleB(mantissa, exponent - 16383 - 63);
                int rate = rateD is >= 1 and < 1_536_001 ? (int)Math.Round(rateD) : 0;
                var sample = PcmSample.Int;
                bool bigEndian = true;
                if (aifc && commLen >= 22)
                {
                    uint kind = BinaryPrimitives.ReadUInt32BigEndian(comm[18..]);
                    switch (kind)
                    {
                        case 0x4E4F4E45 or 0x74776F73: break;                                                    // NONE, twos
                        case 0x736F7774: bigEndian = false; break;                                               // sowt
                        case 0x666C3332 or 0x464C3332: sample = PcmSample.Float; bits = 32; break;               // fl32, FL32
                        case 0x666C3634 or 0x464C3634: sample = PcmSample.Float; bits = 64; break;               // fl64, FL64
                        case 0x756C6177 or 0x554C4157: return PcmLayout.Refused(PcmContainer.Aiff, "an AIFF-C file with µ-law audio");
                        case 0x616C6177 or 0x414C4157: return PcmLayout.Refused(PcmContainer.Aiff, "an AIFF-C file with A-law audio");
                        case 0x696D6134: return PcmLayout.Refused(PcmContainer.Aiff, "an AIFF-C file with ADPCM audio");                // ima4
                        default: return PcmLayout.Refused(PcmContainer.Aiff, "an AIFF-C file with compressed audio");
                    }
                }
                if (channels <= 0 || channels > 32 || rate <= 0 || bits <= 0) return PcmLayout.Refused(PcmContainer.Aiff, "a damaged AIFF file");
                int bytes = (bits + 7) / 8;
                if (sample == PcmSample.Float ? bytes is not (4 or 8) : bytes is < 1 or > 4)
                    return PcmLayout.Refused(PcmContainer.Aiff, "an AIFF file with " + bits.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-bit samples");
                long block = (long)bytes * channels;
                dataBytes = Math.Min(dataBytes, frames * block);   // COMM's frame count is the truth; SSND may carry padding
                return new PcmLayout(PcmContainer.Aiff, sample, bigEndian, bytes, sample == PcmSample.Float ? bytes * 8 : bits, channels, rate, 0,
                    dataStart, dataBytes, "");
            }

            // ── the downmix ──────────────────────────────────────────────────────────────────────────────────────────

            const float Minus3Db = 0.7071f;   // Flac.ToFloatMulti's k

            /// <summary>Each channel's weight on the left and on the right output (interleaved L, R per channel): the standard matrix, as
            /// FLAC's downmix. Mono feeds both sides at unity; stereo passes through.</summary>
            public static float[] DownmixOf(int channels, uint mask)
            {
                var w = new float[channels * 2];
                if (channels == 1) { w[0] = w[1] = 1f; return w; }
                if (channels == 2) { w[0] = 1f; w[3] = 1f; return w; }
                Span<uint> speakers = stackalloc uint[channels];
                int bit = 0;
                for (int c = 0; c < channels; c++)
                {
                    if (mask != 0)
                    {
                        while (bit < 32 && (mask & (1u << bit)) == 0) bit++;
                        speakers[c] = bit < 32 ? 1u << bit++ : 0;
                    }
                    else speakers[c] = DefaultSpeaker(channels, c);
                }
                for (int c = 0; c < channels; c++)
                {
                    (float l, float r) = speakers[c] switch
                    {
                        0x1 or 0x40 => (1f, 0f),                         // front left, front left of centre
                        0x2 or 0x80 => (0f, 1f),                         // front right, front right of centre
                        0x4 => (Minus3Db, Minus3Db),                     // front centre
                        0x8 => (0f, 0f),                                 // LFE: dropped
                        0x10 or 0x200 => (Minus3Db, 0f),                 // back left, side left
                        0x20 or 0x400 => (0f, Minus3Db),                 // back right, side right
                        0x100 => (0.5f, 0.5f),                           // back centre
                        0x1000 or 0x8000 => (Minus3Db, 0f),              // top front left, top back left
                        0x4000 or 0x20000 => (0f, Minus3Db),             // top front right, top back right
                        0x800 or 0x2000 or 0x10000 => (0.5f, 0.5f),      // top centre, top front centre, top back centre
                        _ => (0f, 0f),                                   // a channel the mask does not name
                    };
                    w[c * 2] = l;
                    w[c * 2 + 1] = r;
                }
                return w;
            }

            /// <summary>The speaker of channel <paramref name="c"/> when the file has no mask: the WAVE default order for its count
            /// (3: L R C; 4: L R BL BR; 5: L R C BL BR; 6: L R C LFE BL BR; 7: L R C LFE BC SL SR; 8: L R C LFE BL BR SL SR).</summary>
            static uint DefaultSpeaker(int channels, int c)
            {
                ReadOnlySpan<uint> order = channels switch
                {
                    3 => [0x1, 0x2, 0x4],
                    4 => [0x1, 0x2, 0x10, 0x20],
                    5 => [0x1, 0x2, 0x4, 0x10, 0x20],
                    6 => [0x1, 0x2, 0x4, 0x8, 0x10, 0x20],
                    7 => [0x1, 0x2, 0x4, 0x8, 0x100, 0x200, 0x400],
                    8 => [0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x200, 0x400],
                    _ => [],
                };
                return c < order.Length ? order[c] : 0;
            }

            // ── the conversion ───────────────────────────────────────────────────────────────────────────────────────

            /// <summary>Convert <paramref name="frames"/> frames of <paramref name="layout"/>'s samples in <paramref name="raw"/> into
            /// stereo floats (<paramref name="dst"/>: L, R per frame), downmixed by <paramref name="weights"/> and scaled by
            /// <paramref name="gain"/>. No allocation.</summary>
            public static void ToStereo(in PcmLayout layout, ReadOnlySpan<byte> raw, int frames, ReadOnlySpan<float> weights, float gain, Span<float> dst)
            {
                int ch = layout.Channels, bytes = layout.BytesPerSample, block = layout.BlockAlign;
                if (ch == 2 && weights[0] == 1f && weights[3] == 1f && weights[1] == 0f && weights[2] == 0f)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        ReadOnlySpan<byte> s = raw.Slice(f * block, block);
                        dst[f * 2] = SampleAt(layout, s) * gain;
                        dst[f * 2 + 1] = SampleAt(layout, s[bytes..]) * gain;
                    }
                    return;
                }
                for (int f = 0; f < frames; f++)
                {
                    ReadOnlySpan<byte> s = raw.Slice(f * block, block);
                    float l = 0f, r = 0f;
                    for (int c = 0; c < ch; c++)
                    {
                        float wl = weights[c * 2], wr = weights[c * 2 + 1];
                        if (wl == 0f && wr == 0f) continue;
                        float v = SampleAt(layout, s.Slice(c * bytes, bytes));
                        l += v * wl;
                        r += v * wr;
                    }
                    dst[f * 2] = l * gain;
                    dst[f * 2 + 1] = r * gain;
                }
            }

            /// <summary>One sample as a float in [-1, 1).</summary>
            static float SampleAt(in PcmLayout layout, ReadOnlySpan<byte> s)
            {
                bool be = layout.BigEndian;
                switch (layout.Sample)
                {
                    case PcmSample.UInt8: return (s[0] - 128) * (1f / 128f);
                    case PcmSample.Float:
                        return layout.BytesPerSample == 4
                            ? (be ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s))
                            : (float)(be ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s));
                }
                switch (layout.BytesPerSample)
                {
                    case 1: return (sbyte)s[0] * (1f / 128f);   // AIFF's 8-bit is signed
                    case 2: return (be ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s)) * (1f / 32768f);
                    case 3:
                        int v = be ? (s[0] << 24) | (s[1] << 16) | (s[2] << 8) : (s[2] << 24) | (s[1] << 16) | (s[0] << 8);
                        return (v >> 8) * (1f / 8388608f);
                    default: return (be ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s)) * (1f / 2147483648f);
                }
            }
        }

        // ── the decoder ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The engine-facing PCM decoder for WAV and AIFF files (see the file header): the layout walk over the byte seam,
        /// the conversion with the downmix and the normalisation gain folded into it, and the engine's resampler. Runs on the engine's
        /// decode-ahead thread; blocks in the byte seam and nowhere else.</summary>
        public sealed class PcmAudioDecoder : IAudioDecoder, IGainFolding
        {
            const int ChunkFrames = 4096;

            readonly float _gainLinear;
            readonly NormalizationFigures _figures;
            IMediaByteSource? _src;
            PcmLayout _layout;
            MixFormat _target;
            float[] _weights = [];
            byte[] _raw = [];
            float[] _conformed = [];        // stereo floats at the SOURCE rate; [0.._hold) unread
            int _hold;
            long _nextFrame;                // the next source frame the byte cursor reads
            bool _eof, _ending;
            PolyphaseResampler? _resampler;

            /// <param name="gainDb">The normalization figure (0 for a local file: none).</param>
            public PcmAudioDecoder(float gainDb = 0f, float peak = 0f, float albumGainDb = 0f, float albumPeak = 0f)
            {
                _figures = new NormalizationFigures(gainDb, peak, albumGainDb, albumPeak);
                _gainLinear = GainLinear(gainDb, peak, albumGainDb, albumPeak);
            }

            /// <summary>The linear factor folded into this decoder's output (<see cref="IGainFolding"/>, V-PA15).</summary>
            public float AppliedGainLinear => _gainLinear;

            /// <summary>The figures behind <see cref="AppliedGainLinear"/>.</summary>
            public NormalizationFigures? AppliedFigures => _figures;

            public GaplessInfo Gapless { get; private set; } = GaplessInfo.None;

            /// <summary>The source's layout as opened (the inspector's decode reports it).</summary>
            public PcmLayout Layout => _layout;

            /// <summary>Why the last <see cref="TryOpen"/> refused, or "".</summary>
            public string Why { get; private set; } = "";

            public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
            {
                info = default;
                _src = src;
                _target = target;
                if (!src.TryOpen(new DataSpec { Position = 0, Length = -1 })) { Why = "the file could not be opened"; return false; }
                long length = src.Length ?? long.MaxValue;
                _layout = Pcm.Parse(ReadAt, length);
                if (!_layout.Ok)
                {
                    Why = _layout.Why;
                    Log.Warn("audio", "audio.pcm refused: " + Why);
                    return false;
                }
                _weights = Pcm.DownmixOf(_layout.Channels, _layout.ChannelMask);
                _raw = new byte[ChunkFrames * _layout.BlockAlign];
                _conformed = new float[ChunkFrames * 2];
                _resampler = _layout.SampleRate != target.SampleRate ? new PolyphaseResampler(_layout.SampleRate, target.SampleRate, target.Channels) : null;
                if (!SeekSource(0)) { Why = "the file could not be read"; return false; }
                // No encoder delay, no padding: the exact end is data / block align frames (a join is sample-exact).
                Gapless = new GaplessInfo(0, 0, ToMix(_layout.Frames), TailKnown: true);
                Log.Info("audio", $"audio.pcm open {_layout.Label} ch={_layout.Channels} sample={_layout.Sample}{(_layout.BigEndian ? " be" : "")} "
                                  + $"frames={_layout.Frames} mask=0x{_layout.ChannelMask:X} gain={_gainLinear:0.###}");
                var codec = new MediaContentType(Container.Wav, CodecId.None, CodecId.Pcm);
                info = new DecodedInfo(codec, new MixFormat(_layout.SampleRate, _layout.Channels), TimeSpan.FromMilliseconds(_layout.DurationMs), default);
                return true;
            }

            /// <summary>The walk's reader: a seek and a read loop on the byte seam.</summary>
            bool ReadAt(long offset, Span<byte> dst)
            {
                if (_src!.Seek(offset) < 0) return false;
                int got = 0;
                while (got < dst.Length)
                {
                    int n = _src.Read(dst[got..]);
                    if (n <= 0) return false;
                    got += n;
                }
                return true;
            }

            long ToMix(long srcFrames) => (long)Math.Round((double)srcFrames * _target.SampleRate / _layout.SampleRate);

            long ToSrc(long mixFrames) => (long)Math.Round((double)mixFrames * _layout.SampleRate / _target.SampleRate);

            bool SeekSource(long frame)
            {
                if (_src!.Seek(_layout.DataStart + frame * _layout.BlockAlign) < 0) return false;
                _nextFrame = frame;
                return true;
            }

            /// <summary>Read and convert the next chunk into <c>_conformed</c>. False at the end of the data.</summary>
            bool NextChunk()
            {
                long left = _layout.Frames - _nextFrame;
                if (left <= 0) return false;
                int frames = (int)Math.Min(ChunkFrames, left);
                int want = frames * _layout.BlockAlign, got = 0;
                while (got < want)
                {
                    int n = _src!.Read(_raw.AsSpan(got, want - got));
                    if (n <= 0) break;
                    got += n;
                }
                frames = got / _layout.BlockAlign;   // a file cut short ends at its last whole frame
                if (frames == 0) { _nextFrame = _layout.Frames; return false; }
                Pcm.ToStereo(in _layout, _raw.AsSpan(0, frames * _layout.BlockAlign), frames, _weights, _gainLinear, _conformed);
                _nextFrame += frames;
                _hold = frames;
                if (got < want) _nextFrame = _layout.Frames;
                return true;
            }

            public int Read(Span<float> dst)
            {
                if (_src is null || _eof) return 0;
                int ch = _target.Channels;                     // 2 for every session the app opens
                int want = dst.Length / ch;
                if (want <= 0) return 0;
                if (_ending) return DrainTail(dst);
                if (_hold == 0 && !NextChunk()) { _ending = true; return DrainTail(dst); }

                while (_resampler is { IsActive: true } rs)
                {
                    ResampleResult rr = rs.Process(_conformed.AsSpan(0, _hold * ch), _hold, dst);
                    int unread = _hold - rr.Consumed;
                    if (unread > 0 && rr.Consumed > 0) _conformed.AsSpan(rr.Consumed * ch, unread * ch).CopyTo(_conformed);
                    _hold = unread;
                    if (rr.Produced > 0 || rr.Consumed == 0) return rr.Produced;
                    if (_hold == 0 && !NextChunk()) { _ending = true; return DrainTail(dst); }
                }

                int frames = Math.Min(want, _hold);
                _conformed.AsSpan(0, frames * ch).CopyTo(dst);
                int rest = _hold - frames;
                if (rest > 0) _conformed.AsSpan(frames * ch, rest * ch).CopyTo(_conformed);
                _hold = rest;
                return frames;
            }

            /// <summary>The resampler's trailing half kernel (V-PE21), then 0 and <c>_eof</c>.</summary>
            int DrainTail(Span<float> dst)
            {
                int tail = _resampler is { IsActive: true } rs ? rs.Flush(dst) : 0;
                if (tail > 0) return tail;
                _eof = true;
                return 0;
            }

            /// <summary>Seek to a MIX-domain frame: frame-exact in the source (data start + frame x block align). Returns the mix frame
            /// reached, or -1 when the byte seam cannot seek.</summary>
            public long Seek(long frame)
            {
                if (_src is null) return -1;
                long target = Math.Clamp(ToSrc(frame), 0, _layout.Frames);
                _hold = 0; _eof = false; _ending = false;
                _resampler?.Reset();
                if (!SeekSource(target)) return -1;
                return ToMix(target);
            }
        }

        /// <summary>The decoder a format with none gets: it refuses at open with one log line, never decodes the bytes as something
        /// else (an unknown format used to fall through to Vorbis).</summary>
        public sealed class NoDecoder(Spotify.Audio.Format format) : IAudioDecoder
        {
            public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
            {
                info = default;
                Log.Warn("audio", "audio.decoder none for format=" + format);
                return false;
            }

            public int Read(Span<float> dst) => -1;
            public long Seek(long frame) => -1;
            public GaplessInfo Gapless => GaplessInfo.None;
        }
    }
}
