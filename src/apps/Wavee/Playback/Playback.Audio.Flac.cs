// ── Playback/Playback.Audio.Flac.cs ────────────────────────────────────────────────────────────────────────────────
// The FLAC decoder, from scratch: bit reader, metadata, frame header + CRC-8, the four subframes, the Rice residual,
// stereo decorrelation, float conversion, exact seek, CRC-16 per frame and the STREAMINFO MD5 oracle
//
// Role: CORE
// Owner: U
// Wave: 3 (parallel — pure over spans, no wave dependency)
// Budget: 1100 lines (over by §6's seek planner, ~110 lines: §3.9 left the probe loop in the SHELL, where a probe
//         count cannot be unit-tested, so `SeekPlan` / `BeginSeek` / `TryNextProbe` / `Observe` live here instead).
//         The INNER LOOPS are the named partial `Playback.Audio.Flac.Kernels.cs` (920 lines, same owner): the bit
//         reader, the Rice partition, the LPC and FIXED restorations, the three SIMD sites and the two CRCs. The
//         optimisation pass took the pair past 1,430 (+30 %), so they split: this file is the FORMAT, that one the
//         ARITHMETIC.
// Spec: docs/plans/wavee/wavee-0.3-flac-implementation.md §3 + RFC 9639
//
// WHAT THIS FILE IS. Spotify Lossless and a dropped .flac are the same bytes: `fLaC`, metadata blocks, then frames.
// This file turns those bytes into `int` samples and interleaved `float`s and nothing else — no `Stream`, no
// `IMediaByteSource`, no engine type, no thread, no clock. The SHELL (`Playback.Audio.cs`, owner H) owns the byte
// window, the resampler and the engine seam; it hands this file a `ReadOnlySpan<byte>` that holds a frame and gets
// back samples. That is what makes every fact in `FlacTests.cs` a pure fact over a byte array, and it is why the
// decoder can be written and gated before the pump exists.
//
// THE ORACLE IS THE FORMAT'S OWN. STREAMINFO carries an MD5 of the decoded, natural-depth, interleaved,
// little-endian samples (RFC 9639 §8.2). `Md5Verifier` computes it as the frames go by, so a wrong decorrelation, a
// wrong wasted-bits shift, an off-by-one in a fixed predictor or a mis-read Rice escape is one failing assert over a
// vendored xiph vector — the same oracle `flac -t` and Symphonia's `validate.rs` use. Nothing here is "probably
// right": every vendored vector is bit-exact or the gate is red.
//
// Rules this file is written under (P1-P16 / C1-C10, relationships_wavee.md §5.11):
//   P8   ZERO allocation after `Decoder.Open`. Two buffers, both sized from STREAMINFO: `MaxBlock × Channels` ints
//        for the planar block and 32 ints for the LPC coefficients. The bit reader is a `ref struct` over the
//        caller's span; every header, every block and every seek probe is stack-only. §3.11 is the whole list.
//   P9   No LINQ, no closures, no async, no boxing, no exceptions on the decode path. An overrun sets a flag and
//        yields zeros; a corrupt frame is a return value (`FrameResult`), not a throw. The SHELL resyncs.
//   P15  SIMD only where the elements are independent and there are more than ~16 of them: the stereo
//        decorrelation, the wasted-bits shift and the int→float scale. Each has a scalar tail that is also the
//        whole path on a short block and on a host without acceleration (arm64 ships). The predictors and the Rice
//        loop are serial recurrences and stay scalar — libFLAC ships no `restore_signal` intrinsic either; the
//        Kernels partial's header says why, kernel by kernel.
//   C2   Synchronous and stateless across frames. `Seek` keeps no decoder state (Symphonia's `reset` is a no-op for
//        the same reason): the SHELL re-points the window and decodes the frame it finds.
//
// The reference for anything the RFC leaves implicit is Symphonia's `symphonia-bundle-flac` (frame.rs, decoder.rs,
// parser.rs, demuxer.rs, validate.rs) and libFLAC's `stream_decoder.c` / `lpc.c` / `crc.c`; the plan's §1.5 table
// maps each of them to the section below that transliterates it.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The FLAC format (RFC 9639), as a set of pure functions over spans plus one small class that owns the
    /// decode buffers. See the file header for the rules; see the plan §3 for the derivation. The inner loops are
    /// the other half of this partial, <c>Playback.Audio.Flac.Kernels.cs</c>.</summary>
    [SkipLocalsInit]
    public static unsafe partial class Flac
    {
        // ── 1. Constants (the bit reader is §K1 of the Kernels partial) ─────────────────────────────────────────────

        /// <summary>"fLaC", the stream marker (§8).</summary>
        public const uint Magic = 0x664C6143;

        /// <summary>STREAMINFO is always exactly 34 bytes (§8.2).</summary>
        public const int StreamInfoBytes = 34;

        /// <summary>The format's ceiling (§8.2): 1..8 channels.</summary>
        public const int MaxChannels = 8;

        /// <summary>The format's ceiling on a block: 65,535 samples per channel (§8.2).</summary>
        public const int MaxBlockSize = 65_535;

        /// <summary>The format's ceiling on an LPC order (§9.2.6): 32 coefficients.</summary>
        public const int MaxLpcOrder = 32;

        // ── 2. Metadata: STREAMINFO, SEEKTABLE, VORBIS_COMMENT, PICTURE (§8) ────────────────────────────────────────
        //
        // `ParseHeaders` walks the metadata blocks once at open. It is also the whole of the local-file duration and
        // tag probe (plan §6): the same function, called with a file's first 64 KiB, answers duration, title/artist/
        // album and where the cover bytes are. Text comes out as byte RANGES into the caller's span (P14: the caller
        // interns on the UI thread; this file never makes a `string`).

        public enum BlockType : byte
        {
            StreamInfo = 0,
            Padding = 1,
            Application = 2,
            SeekTable = 3,
            VorbisComment = 4,
            CueSheet = 5,
            Picture = 6,
        }

        /// <summary>STREAMINFO (§8.2). <see cref="TotalSamples"/> 0 = unknown (a live encode); an all-zero
        /// <see cref="Md5"/> = absent. Plain fields, no behaviour: the decoder, the probe and the tests all read it.</summary>
        public struct StreamInfo
        {
            public ushort MinBlock, MaxBlock;
            public uint MinFrame, MaxFrame;         // 0 = unknown
            public int SampleRate;                  // 1..655,350
            public byte Channels;                   // 1..8
            public byte Bps;                        // 4..32
            public long TotalSamples;               // 0 = unknown
            public Md5Digest Md5;

            /// <summary>False when the encoder wrote sixteen zero bytes — "no MD5" (§8.2).</summary>
            public readonly bool HasMd5 => !Md5.IsZero;

            /// <summary>0 when the total sample count is unknown; the row then shows "–:–" until EOF.</summary>
            public readonly long DurationMs => SampleRate > 0 ? TotalSamples * 1000 / SampleRate : 0;
        }

        /// <summary>The 128-bit MD5 of the decoded samples, as sixteen bytes.</summary>
        [InlineArray(16)]
        public struct Md5Digest
        {
            byte _e0;

            /// <summary>Sixteen zero bytes — STREAMINFO's "the encoder did not compute one" convention (§8.2).</summary>
            public readonly bool IsZero
            {
                get
                {
                    ref byte first = ref Unsafe.AsRef(in _e0);
                    for (int i = 0; i < 16; i++) if (Unsafe.Add(ref first, i) != 0) return false;
                    return true;
                }
            }
        }

        /// <summary>A seek point (§8.5): sample number, byte offset from the FIRST FRAME, samples in that frame.</summary>
        public readonly record struct SeekPoint(long Sample, long Offset, ushort Samples);

        /// <summary>A byte range inside the span the parser was given — a tag value, the picture bytes.</summary>
        public readonly record struct ByteRange(int Offset, int Length)
        {
            public bool IsEmpty => Length == 0;
        }

        /// <summary>The row-facing tags (plan §6). Ranges into the header span; empty when the file carries none.</summary>
        public struct Tags
        {
            public ByteRange Title, Artist, Album, AlbumArtist, Date, TrackNumber;
            public ByteRange PictureMime, PictureData;

            /// <summary>§8.8's picture type; 3 = front cover. The first picture wins, a front cover replaces it.</summary>
            public uint PictureKind;
        }

        /// <summary>What <see cref="ParseHeaders"/> answers. <see cref="FirstFrame"/> is the byte offset of the first
        /// frame's sync byte, which every seek-table offset is relative to.</summary>
        public struct Headers
        {
            public StreamInfo Info;
            public Tags Tags;
            public int FirstFrame;
            public int SeekPointCount;              // how many were written into the caller's seek span
            public bool Complete;                   // false ⇒ the span ended before the last metadata block
            public bool Valid;                      // magic + a sane STREAMINFO, and every block inside the span
        }

        /// <summary>Parse "fLaC" + the metadata blocks at the start of <paramref name="head"/>. Pure. Seek points go
        /// into <paramref name="seek"/> (the caller sizes it; 1,024 points cover a 3-hour file at one per 10 s — the
        /// excess is dropped, never allocated; an empty span is legal and means "I do not want them"). A span that
        /// stops inside a block reports <c>Complete = false</c> so the caller can read further and call again.</summary>
        public static Headers ParseHeaders(ReadOnlySpan<byte> head, Span<SeekPoint> seek)
        {
            Headers h = default;
            if (head.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(head) != Magic) return h;
            int pos = 4;
            bool last = false;
            bool sawInfo = false;
            while (!last)
            {
                if (pos + 4 > head.Length) return h;                               // Complete = false
                byte b0 = head[pos];
                last = (b0 & 0x80) != 0;
                int typeCode = b0 & 0x7F;
                int len = (head[pos + 1] << 16) | (head[pos + 2] << 8) | head[pos + 3];
                pos += 4;
                if (typeCode == 127) return h;                                      // forbidden, to avoid a sync fake (§8.1)
                if (len < 0 || pos + len > head.Length) return h;                   // Complete = false
                ReadOnlySpan<byte> body = head.Slice(pos, len);
                switch ((BlockType)typeCode)
                {
                    case BlockType.StreamInfo:
                        if (sawInfo || len != StreamInfoBytes || !ParseStreamInfo(body, out h.Info)) return h;
                        sawInfo = true;
                        break;
                    case BlockType.SeekTable:
                        h.SeekPointCount = ParseSeekTable(body, seek);
                        break;
                    case BlockType.VorbisComment:
                        ParseVorbisComment(body, pos, ref h.Tags);
                        break;
                    case BlockType.Picture:
                        ParsePicture(body, pos, ref h.Tags);
                        break;
                    default:
                        break;                                                      // padding, application, cuesheet
                }
                pos += len;
            }
            if (!sawInfo) return h;                                                 // STREAMINFO is mandatory (§8.2)
            h.FirstFrame = pos;
            h.Complete = true;
            h.Valid = true;
            return h;
        }

        static bool ParseStreamInfo(ReadOnlySpan<byte> b, out StreamInfo si)
        {
            si = default;
            var r = new BitReader(b, 0);
            si.MinBlock = (ushort)r.Read(16);
            si.MaxBlock = (ushort)r.Read(16);
            si.MinFrame = r.Read(24);
            si.MaxFrame = r.Read(24);
            si.SampleRate = (int)r.Read(20);
            si.Channels = (byte)(r.Read(3) + 1);
            si.Bps = (byte)(r.Read(5) + 1);
            si.TotalSamples = (long)r.ReadLong(36);
            Span<byte> md5 = si.Md5;
            b.Slice(18, 16).CopyTo(md5);
            // §8.2: 16 ≤ min ≤ max ≤ 65,535; rate 1..655,350 (0 means "not audio", which we do not play).
            return si.MinBlock >= 16 && si.MaxBlock >= si.MinBlock
                && si.SampleRate is >= 1 and <= 655_350 && si.Channels is >= 1 and <= MaxChannels
                && si.Bps is >= 4 and <= 32;
        }

        static int ParseSeekTable(ReadOnlySpan<byte> b, Span<SeekPoint> into)
        {
            int n = 0;
            for (int i = 0; i + 18 <= b.Length && n < into.Length; i += 18)
            {
                ulong sample = BinaryPrimitives.ReadUInt64BigEndian(b[i..]);
                if (sample > (ulong)long.MaxValue) continue;                        // placeholder 2^64−1 (§8.5.1)
                ulong offset = BinaryPrimitives.ReadUInt64BigEndian(b[(i + 8)..]);
                if (offset > (ulong)long.MaxValue) continue;
                ushort samples = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 16)..]);
                into[n++] = new SeekPoint((long)sample, (long)offset, samples);
            }
            return n;
        }

        /// <summary>§8.6, LITTLE-endian lengths (the one little-endian block in the format). Keys are ASCII and
        /// case-insensitive; the FIRST '=' splits. Only the six keys a row needs are kept; the rest is skipped by
        /// length, so a 1,000-comment block costs one pass and no allocation.</summary>
        static void ParseVorbisComment(ReadOnlySpan<byte> b, int baseOffset, ref Tags tags)
        {
            int pos = 0;
            if (!ReadLeU32(b, ref pos, out uint vendorLen) || vendorLen > (uint)(b.Length - pos)) return;
            pos += (int)vendorLen;
            if (!ReadLeU32(b, ref pos, out uint count)) return;
            for (uint i = 0; i < count; i++)
            {
                if (!ReadLeU32(b, ref pos, out uint len) || len > (uint)(b.Length - pos)) return;
                ReadOnlySpan<byte> field = b.Slice(pos, (int)len);
                int eq = field.IndexOf((byte)'=');
                if (eq > 0)
                {
                    ReadOnlySpan<byte> key = field[..eq];
                    var value = new ByteRange(baseOffset + pos + eq + 1, (int)len - eq - 1);
                    if (KeyIs(key, "TITLE"u8)) tags.Title = value;
                    else if (KeyIs(key, "ARTIST"u8)) tags.Artist = value;
                    else if (KeyIs(key, "ALBUM"u8)) tags.Album = value;
                    else if (KeyIs(key, "ALBUMARTIST"u8) || KeyIs(key, "ALBUM ARTIST"u8)) tags.AlbumArtist = value;
                    else if (KeyIs(key, "DATE"u8)) tags.Date = value;
                    else if (KeyIs(key, "TRACKNUMBER"u8)) tags.TrackNumber = value;
                }
                pos += (int)len;
            }
        }

        static bool KeyIs(ReadOnlySpan<byte> key, ReadOnlySpan<byte> name)
        {
            if (key.Length != name.Length) return false;
            for (int i = 0; i < key.Length; i++)
            {
                byte k = key[i];
                if (k >= (byte)'a' && k <= (byte)'z') k -= 32;
                if (k != name[i]) return false;
            }
            return true;
        }

        static bool ReadLeU32(ReadOnlySpan<byte> b, ref int pos, out uint v)
        {
            v = 0;
            if (pos < 0 || pos + 4 > b.Length) return false;
            v = BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
            pos += 4;
            return true;
        }

        /// <summary>§8.8, big-endian. The first picture is kept; a later FRONT COVER (kind 3) replaces it.</summary>
        static void ParsePicture(ReadOnlySpan<byte> b, int baseOffset, ref Tags tags)
        {
            int pos = 0;
            if (!ReadBeU32(b, ref pos, out uint kind)) return;
            if (!ReadBeU32(b, ref pos, out uint mimeLen) || mimeLen > (uint)(b.Length - pos)) return;
            var mime = new ByteRange(baseOffset + pos, (int)mimeLen);
            pos += (int)mimeLen;
            if (!ReadBeU32(b, ref pos, out uint descLen) || descLen > (uint)(b.Length - pos)) return;
            pos += (int)descLen;
            pos += 16;                                                              // width, height, depth, colours
            if (!ReadBeU32(b, ref pos, out uint dataLen) || dataLen > (uint)(b.Length - pos)) return;
            if (tags.PictureData.IsEmpty || kind == 3)
            {
                tags.PictureKind = kind;
                tags.PictureMime = mime;
                tags.PictureData = new ByteRange(baseOffset + pos, (int)dataLen);
            }
        }

        static bool ReadBeU32(ReadOnlySpan<byte> b, ref int pos, out uint v)
        {
            v = 0;
            if (pos < 0 || pos + 4 > b.Length) return false;
            v = BinaryPrimitives.ReadUInt32BigEndian(b[pos..]);
            pos += 4;
            return true;
        }

        // ── 3. The frame header, the four code tables, CRC-8 (§9.1) ─────────────────────────────────────────────────

        /// <summary>What a frame header says (§9.1). <see cref="SampleNumber"/> is ABSOLUTE for both blocking
        /// strategies: a fixed-size stream carries a FRAME number and the decoder multiplies by STREAMINFO's min
        /// block (Symphonia <c>calc_sync_info</c>, parser.rs:566-584).</summary>
        public struct FrameHeader
        {
            public int BlockSize;                   // samples per channel
            public int SampleRate;
            public byte Channels;                   // 1..8
            public byte Assignment;                 // 0 independent, 1 left/side, 2 side/right, 3 mid/side
            public byte Bps;
            public bool Variable;                   // true ⇒ the coded number IS the sample number
            public long SampleNumber;
            public int HeaderBytes;                 // sync .. CRC-8 inclusive
        }

        public enum HeaderResult : byte { Ok, NotSync, Reserved, BadCrc, Truncated, Mismatch }

        /// <summary>The cheap gate before the CRC-8 parse, for the sync scanner and the seek probe (Symphonia
        /// <c>is_likely_frame_header</c>, frame.rs:225-267). Four bytes: the widened 16-bit sync, and no reserved
        /// code in the block-size / rate / channel / bit-depth fields.</summary>
        public static bool LooksLikeHeader(ReadOnlySpan<byte> b)
        {
            if (b.Length < 4) return false;
            if (b[0] != 0xFF || (b[1] & 0xFC) != 0xF8) return false;
            int block = b[2] >> 4, rate = b[2] & 0xF, chan = b[3] >> 4, bps = (b[3] >> 1) & 7;
            return block != 0 && rate != 0xF && chan < 0xB && bps != 3 && (b[3] & 1) == 0;
        }

        /// <summary>Parse one frame header at <paramref name="b"/>[0]. STREAMINFO fills the "0000 = from STREAMINFO"
        /// codes and is the cross-check for rate / channels / bit depth: a header whose CRC-8 is valid but whose
        /// shape contradicts STREAMINFO is a false sync inside audio data, not a format change (Symphonia
        /// <c>strict_frame_header_check</c>, parser.rs:586-647).</summary>
        public static HeaderResult ParseFrameHeader(ReadOnlySpan<byte> b, in StreamInfo si, out FrameHeader h)
        {
            h = default;
            if (b.Length < 6) return HeaderResult.Truncated;
            if (b[0] != 0xFF || (b[1] & 0xFC) != 0xF8) return HeaderResult.NotSync;
            h.Variable = (b[1] & 1) != 0;
            int blockCode = b[2] >> 4, rateCode = b[2] & 0xF, chanCode = b[3] >> 4, bpsCode = (b[3] >> 1) & 7;
            if (blockCode == 0 || rateCode == 0xF || chanCode > 0xA || bpsCode == 3 || (b[3] & 1) != 0)
                return HeaderResult.Reserved;

            int pos = 4;
            // The coded number comes BEFORE the block-size / sample-rate extension bytes (§9.1, frame.rs:103-136).
            if (!ReadCodedNumber(b, ref pos, out ulong number))
                return pos >= b.Length ? HeaderResult.Truncated : HeaderResult.Reserved;
            if (h.Variable)
            {
                if (number > 0xF_FFFF_FFFF) return HeaderResult.Reserved;            // 36 bits (§9.1.5)
                h.SampleNumber = (long)number;
            }
            else
            {
                if (number > 0x7FFF_FFFF) return HeaderResult.Reserved;              // 31 bits of frame number
                h.SampleNumber = (long)number * si.MinBlock;
            }

            switch (blockCode)                                                       // §9.1.1
            {
                case 1: h.BlockSize = 192; break;
                case >= 2 and <= 5: h.BlockSize = 576 << (blockCode - 2); break;
                case 6:
                    if (pos + 1 > b.Length) return HeaderResult.Truncated;
                    h.BlockSize = b[pos++] + 1;
                    break;
                case 7:
                    if (pos + 2 > b.Length) return HeaderResult.Truncated;
                    h.BlockSize = ((b[pos] << 8) | b[pos + 1]) + 1;
                    pos += 2;
                    break;
                default: h.BlockSize = 256 << (blockCode - 8); break;                 // 8..15
            }
            switch (rateCode)                                                        // §9.1.2
            {
                case 0: h.SampleRate = si.SampleRate; break;
                case 1: h.SampleRate = 88_200; break;
                case 2: h.SampleRate = 176_400; break;
                case 3: h.SampleRate = 192_000; break;
                case 4: h.SampleRate = 8_000; break;
                case 5: h.SampleRate = 16_000; break;
                case 6: h.SampleRate = 22_050; break;
                case 7: h.SampleRate = 24_000; break;
                case 8: h.SampleRate = 32_000; break;
                case 9: h.SampleRate = 44_100; break;
                case 10: h.SampleRate = 48_000; break;
                case 11: h.SampleRate = 96_000; break;
                case 12:
                    if (pos + 1 > b.Length) return HeaderResult.Truncated;
                    h.SampleRate = b[pos++] * 1000;
                    break;
                case 13:
                    if (pos + 2 > b.Length) return HeaderResult.Truncated;
                    h.SampleRate = (b[pos] << 8) | b[pos + 1];
                    pos += 2;
                    break;
                default:                                                             // 14: tens of Hz, to 655,350
                    if (pos + 2 > b.Length) return HeaderResult.Truncated;
                    h.SampleRate = ((b[pos] << 8) | b[pos + 1]) * 10;
                    pos += 2;
                    break;
            }
            if (chanCode <= 7) { h.Channels = (byte)(chanCode + 1); h.Assignment = 0; }   // §9.1.3
            else { h.Channels = 2; h.Assignment = (byte)(chanCode - 7); }                 // 8 → L/S, 9 → S/R, 10 → M/S
            h.Bps = bpsCode switch                                                        // §9.1.4
            {
                0 => si.Bps,
                1 => 8,
                2 => 12,
                4 => 16,
                5 => 20,
                6 => 24,
                _ => 32,
            };

            if (pos + 1 > b.Length) return HeaderResult.Truncated;
            if (Crc8(b[..pos]) != b[pos]) return HeaderResult.BadCrc;
            h.HeaderBytes = pos + 1;

            if (h.SampleRate != si.SampleRate || h.Channels != si.Channels || h.Bps != si.Bps
                || h.BlockSize > si.MaxBlock || h.BlockSize < 1) return HeaderResult.Mismatch;
            return HeaderResult.Ok;
        }

        /// <summary>§9.1.5: a UTF-8-shaped number, up to 7 bytes / 36 bits (Symphonia <c>utf8_decode_be_u64</c>).
        /// False on an invalid lead or continuation byte — which is how the sync scanner rejects a false sync.</summary>
        public static bool ReadCodedNumber(ReadOnlySpan<byte> b, ref int pos, out ulong value)
        {
            value = 0;
            if (pos < 0 || pos >= b.Length) return false;
            byte lead = b[pos++];
            int extra;
            if (lead < 0x80) { value = lead; return true; }
            else if ((lead & 0xE0) == 0xC0) { value = (ulong)(lead & 0x1F); extra = 1; }
            else if ((lead & 0xF0) == 0xE0) { value = (ulong)(lead & 0x0F); extra = 2; }
            else if ((lead & 0xF8) == 0xF0) { value = (ulong)(lead & 0x07); extra = 3; }
            else if ((lead & 0xFC) == 0xF8) { value = (ulong)(lead & 0x03); extra = 4; }
            else if ((lead & 0xFE) == 0xFC) { value = (ulong)(lead & 0x01); extra = 5; }
            else if (lead == 0xFE) { value = 0; extra = 6; }
            else return false;                                                       // 0xFF is never a lead byte
            for (int i = 0; i < extra; i++)
            {
                if (pos >= b.Length) return false;
                byte c = b[pos++];
                if ((c & 0xC0) != 0x80) return false;
                value = (value << 6) | (uint)(c & 0x3F);
            }
            return true;
        }

        // CRC-8 (§9.1.8) and CRC-16 (§9.3) are §K2 of the Kernels partial: one table for the header's eight
        // bits, slicing-by-8 for the frame's sixteen, both built from the polynomial at type init.

        // ── 4. The four subframes: CONSTANT, VERBATIM, FIXED, LPC (§9.2) ────────────────────────────────────────────
        //
        // `DecodeSubframe` writes one channel's block into `samples`. Residuals are decoded IN PLACE from index
        // `order` onward and the predictor is restored over them (Symphonia decoder.rs:429-511; libFLAC does the
        // same). Wasted bits are shifted back at the end, before the caller sees a sample.

        public enum FrameResult : byte { Ok, Overrun, Reserved, BadResidual, BadCrc16, Unsupported }

        /// <summary>One channel's subframe (§9.2). <paramref name="bps"/> already includes the side channel's extra bit.</summary>
        static FrameResult DecodeSubframe(ref BitReader r, int bps, Span<int> samples, Span<int> coefs)
        {
            if (r.ReadBit()) return FrameResult.Reserved;                            // the zero pad bit (§9.2.1)
            int type = (int)r.Read(6);
            int wasted = 0;
            if (r.ReadBit()) wasted = r.ReadUnary() + 1;                             // §9.2.2: unary (k−1)
            if (wasted >= bps) return FrameResult.Reserved;
            bps -= wasted;

            FrameResult res;
            if (type == 0) res = DecodeConstant(ref r, bps, samples);
            else if (type == 1) res = DecodeVerbatim(ref r, bps, samples);
            else if ((type & 0x38) == 0x08 && (type & 7) <= 4) res = DecodeFixed(ref r, bps, type & 7, samples);
            else if ((type & 0x20) != 0) res = DecodeLpc(ref r, bps, (type & 0x1F) + 1, samples, coefs);
            else return FrameResult.Reserved;
            if (res != FrameResult.Ok) return res;
            if (r.Overrun) return FrameResult.Overrun;

            if (wasted > 0) ShiftLeft(samples, wasted);                              // §9.2.2: k zero bits back on
            return FrameResult.Ok;
        }

        static FrameResult DecodeConstant(ref BitReader r, int bps, Span<int> s)
        {
            s.Fill(r.ReadSigned(bps));
            return FrameResult.Ok;
        }

        static FrameResult DecodeVerbatim(ref BitReader r, int bps, Span<int> s)
        {
            for (int i = 0; i < s.Length; i++) s[i] = r.ReadSigned(bps);
            return FrameResult.Ok;
        }

        /// <summary>§9.2.5. Warm-up samples verbatim, residual in place, then the fixed polynomial restored over them
        /// by <see cref="RestoreFixed"/> (§K4). Order 0 restores nothing — the residual IS the signal.</summary>
        static FrameResult DecodeFixed(ref BitReader r, int bps, int order, Span<int> s)
        {
            if (order > s.Length) return FrameResult.Reserved;
            for (int i = 0; i < order; i++) s[i] = r.ReadSigned(bps);
            FrameResult res = DecodeResidual(ref r, order, s);
            if (res != FrameResult.Ok) return res;
            RestoreFixed(s, order);
            return FrameResult.Ok;
        }

        /// <summary>§9.2.6. Coefficients are read into the caller's 32-slot scratch (P8: no per-frame array); a
        /// precision of 0b1111 and a negative shift are both forbidden by the RFC and rejected here.</summary>
        static FrameResult DecodeLpc(ref BitReader r, int bps, int order, Span<int> s, Span<int> coefs)
        {
            if (order > s.Length || order > MaxLpcOrder) return FrameResult.Reserved;
            for (int i = 0; i < order; i++) s[i] = r.ReadSigned(bps);
            int precision = (int)r.Read(4) + 1;
            if (precision == 16) return FrameResult.Reserved;                        // 0b1111 forbidden
            int shift = r.ReadSigned(5);
            if (shift < 0) return FrameResult.Unsupported;
            for (int i = 0; i < order; i++) coefs[i] = r.ReadSigned(precision);
            FrameResult res = DecodeResidual(ref r, order, s);
            if (res != FrameResult.Ok) return res;
            RestoreLpc(s, coefs[..order], shift,
                bps + precision + System.Numerics.BitOperations.Log2((uint)order) + 1);
            return FrameResult.Ok;
        }

        /// <summary>§9.2.7. Partitions are 2^order equal slices of the block; the first is short by the predictor
        /// order. The escape (a parameter of all ones) switches a partition to raw signed samples of the given width,
        /// and a width of zero means the partition is silence (xiph vector 64).</summary>
        static FrameResult DecodeResidual(ref BitReader r, int predictorOrder, Span<int> s)
        {
            int method = (int)r.Read(2);
            if (method > 1) return FrameResult.Reserved;
            int paramBits = method == 0 ? 4 : 5;
            int escape = (1 << paramBits) - 1;
            int partitionOrder = (int)r.Read(4);
            int partitions = 1 << partitionOrder;
            int perPartition = s.Length >> partitionOrder;
            if (perPartition << partitionOrder != s.Length) return FrameResult.BadResidual;   // block not divisible
            if (perPartition < predictorOrder) return FrameResult.BadResidual;                // first partition < 0

            int pos = predictorOrder;
            for (int p = 0; p < partitions; p++)
            {
                int count = p == 0 ? perPartition - predictorOrder : perPartition;
                int param = (int)r.Read(paramBits);
                Span<int> part = s.Slice(pos, count);
                if (param == escape)
                {
                    int width = (int)r.Read(5);
                    if (width == 0) part.Clear();
                    else for (int i = 0; i < count; i++) part[i] = r.ReadSigned(width);
                }
                else
                {
                    r.ReadRicePartition(param, part);                                         // §K1, the pointer loop
                }
                pos += count;
                if (r.Overrun) return FrameResult.Overrun;
            }
            return FrameResult.Ok;
        }

        // ── 5. Decorrelation, wasted bits, and the float conversion → §K5 of the Kernels partial ────────────────
        //
        // `Decorrelate`, `ShiftLeft`, `ToFloat` and `ToFloatMulti` are the three places in the decoder where the
        // elements are independent, and they are the three SIMD sites (P15): `Vector256`, a `Vector128` path under
        // it, and a scalar tail that is also the whole path below ~16 samples. `Flac.ForceScalar` turns them off.

        // ── 6. Sync, and the seek planner (§3.9) ────────────────────────────────────────────────────────────────────

        /// <summary>Find the next plausible frame header at or after <paramref name="from"/>: sync bytes, no reserved
        /// codes, CRC-8 valid, STREAMINFO-consistent. −1 when none is in the span. The CRC-16 check of the WHOLE
        /// frame is the caller's — it needs the frame's end, which needs a decode.</summary>
        public static int FindHeader(ReadOnlySpan<byte> win, int from, in StreamInfo si, out FrameHeader h)
        {
            h = default;
            if (from < 0) from = 0;
            int i = from;
            while (i + 4 <= win.Length)
            {
                // `IndexOf` over a byte span is the runtime's own vectorised scan, so a resync over a 64 KiB window
                // is one pass at memory speed instead of 65,536 compares. Everything after it is the old test.
                int rel = win[i..].IndexOf((byte)0xFF);
                if (rel < 0) return -1;
                i += rel;
                if (i + 4 > win.Length) return -1;
                if ((win[i + 1] & 0xFC) != 0xF8 || !LooksLikeHeader(win[i..])) { i++; continue; }
                HeaderResult r = ParseFrameHeader(win[i..], si, out h);
                if (r == HeaderResult.Ok) return i;
                if (r == HeaderResult.Truncated) return -1;                           // need more bytes, not another offset
                i++;
            }
            return -1;
        }

        /// <summary>The next probe position for the bracketed search: linear interpolation over the bracket, backed
        /// off by one maximum frame so the probe lands BEFORE the target (libFLAC <c>seek_to_absolute_sample_</c>).
        /// Pure, and tested on its own.</summary>
        public static long EstimateOffset(long lo, long loSample, long hi, long hiSample, long target, uint maxFrame)
        {
            if (hiSample <= loSample || hi <= lo) return lo;
            long pos = lo + (long)((double)(target - loSample) / (hiSample - loSample) * (hi - lo));
            long backoff = maxFrame > 0 ? maxFrame : 16 * 1024;
            pos -= backoff;
            return Math.Clamp(pos, lo, Math.Max(lo, hi - 1));
        }

        /// <summary>Which tier answered a seek (§3.9): the file's own seek table, the bracketed search, or a plain
        /// forward decode because the stream says nothing about its own length.</summary>
        public enum SeekTier : byte { Table, Bracket, Linear }

        /// <summary>What one probe of the bracketed search answered.</summary>
        public enum ProbeResult : byte { Continue, Found, NoFrame }

        /// <summary>The seek state machine (§3.9), pure and drivable by any caller that can read bytes at an offset:
        /// <see cref="BeginSeek"/>, then <see cref="TryNextProbe"/> / <see cref="Observe"/> until it stops asking,
        /// then decode forward from <see cref="Offset"/> (at most <see cref="Forward"/> samples) to the frame that
        /// holds the target. Keeping the loop here rather than in the SHELL is what makes the probe count a fact a
        /// unit test can assert. Buffers: none — the caller's window is the only memory.</summary>
        public struct SeekPlan
        {
            public long Target;                     // the source sample asked for
            public long Offset;                     // byte offset of the best known frame start at or before Target
            public long Sample;                     // that frame's first sample
            public bool Resolved;                   // Offset/Sample is close enough to decode forward from
            public int Probes;                      // how many windows the caller had to read
            public SeekTier Tier;

            public long Lo, LoSample, Hi, HiSample; // the bracket, in bytes and samples
            public long Forward;                    // how many samples forward decoding may cover before re-probing
            public long Limit;                      // stop bracketing once the bracket is this small, in bytes
            public uint MaxFrame;                   // STREAMINFO's max frame size (0 = unknown)
            public int MaxProbes;
        }

        /// <summary>Plan a seek to <paramref name="target"/> (a SOURCE sample). The seek table narrows the bracket
        /// first (tier 1, Symphonia demuxer.rs:249-393); a stream that carries neither a table nor a total sample
        /// count can only be decoded forward (tier 3).</summary>
        public static SeekPlan BeginSeek(in StreamInfo si, ReadOnlySpan<SeekPoint> points, long firstFrame,
                                         long streamLength, long target, int maxProbes = 32)
        {
            SeekPlan p = default;
            p.Target = target < 0 ? 0 : target;
            p.Lo = firstFrame;
            p.LoSample = 0;
            p.Hi = streamLength > firstFrame ? streamLength : firstFrame;
            p.HiSample = si.TotalSamples > 0 ? si.TotalSamples : -1;
            p.MaxFrame = si.MaxFrame;
            p.MaxProbes = maxProbes;
            p.Forward = 8L * si.MaxBlock;                   // ≈ 0.7 s at 44.1 kHz — cheaper than another range request
            long bracket = si.MaxFrame;                     // 0 = the encoder wrote no frame sizes (xiph vector 46)
            if (bracket < 16 * 1024) bracket = 16 * 1024;
            p.Limit = 2 * bracket;
            p.Tier = points.Length > 0 ? SeekTier.Table : (p.HiSample > 0 ? SeekTier.Bracket : SeekTier.Linear);

            for (int i = 0; i < points.Length; i++)
            {
                long at = firstFrame + points[i].Offset;
                if (points[i].Sample <= p.Target)
                {
                    if (at >= p.Lo) { p.Lo = at; p.LoSample = points[i].Sample; }
                }
                else if (at < p.Hi) { p.Hi = at; p.HiSample = points[i].Sample; }
            }
            p.Offset = p.Lo;
            p.Sample = p.LoSample;
            p.Resolved = p.Target - p.LoSample < p.Forward;
            return p;
        }

        /// <summary>The offset the caller should read a window at, or false when the plan is done — either resolved,
        /// out of probes, bracketed as tightly as it is worth, or unable to interpolate at all.</summary>
        public static bool TryNextProbe(ref SeekPlan plan, out long offset)
        {
            offset = plan.Offset;
            if (plan.Resolved || plan.HiSample <= 0 || plan.Probes >= plan.MaxProbes) return false;
            if (plan.Hi - plan.Lo <= plan.Limit) return false;
            offset = EstimateOffset(plan.Lo, plan.LoSample, plan.Hi, plan.HiSample, plan.Target, plan.MaxFrame);
            plan.Probes++;
            return true;
        }

        /// <summary>Feed the window read at <paramref name="windowOffset"/> back to the plan. The first frame in it
        /// that parses AND passes CRC-16 (<paramref name="decoder"/> does the decode; a false sync inside audio data
        /// is exactly what the frame CRC rejects) narrows the bracket, or resolves the plan when it holds the target
        /// or is within <see cref="SeekPlan.Forward"/> samples of it.</summary>
        public static ProbeResult Observe(ref SeekPlan plan, Decoder decoder, ReadOnlySpan<byte> window, long windowOffset)
        {
            int from = 0;
            while (true)
            {
                int at = FindHeader(window, from, decoder.Info, out FrameHeader h);
                if (at < 0) return ProbeResult.NoFrame;
                FrameResult fr = decoder.DecodeFrame(window[at..], out _, out _);
                if (fr == FrameResult.Overrun) return ProbeResult.NoFrame;             // the window holds no whole frame
                if (fr != FrameResult.Ok) { from = at + 1; continue; }                 // a false sync: the CRC-16 said so

                long abs = windowOffset + at;
                if (h.SampleNumber <= plan.Target && plan.Target < h.SampleNumber + h.BlockSize)
                {
                    plan.Offset = abs;
                    plan.Sample = h.SampleNumber;
                    plan.Resolved = true;
                    return ProbeResult.Found;
                }
                if (h.SampleNumber <= plan.Target)
                {
                    plan.Offset = abs;
                    plan.Sample = h.SampleNumber;
                    plan.Lo = abs + 1;
                    plan.LoSample = h.SampleNumber + h.BlockSize;
                    if (plan.Target - h.SampleNumber < plan.Forward) { plan.Resolved = true; return ProbeResult.Found; }
                }
                else
                {
                    plan.Hi = abs;
                    plan.HiSample = h.SampleNumber;
                }
                return ProbeResult.Continue;
            }
        }

        // ── 7. The frame decoder, CRC-16, and the MD5 oracle ────────────────────────────────────────────────────────

        /// <summary>The decoded block: planar samples, one run of <see cref="BlockSize"/> per channel inside the
        /// decoder's buffer, at the frame's own bit depth and NOT up-shifted — the MD5 is over natural-depth samples
        /// (Symphonia validate.rs:36-69).</summary>
        public readonly ref struct Block
        {
            public readonly ReadOnlySpan<int> Planar;
            public readonly int BlockSize;
            public readonly int Channels;
            public readonly int Bps;
            public readonly long SampleNumber;

            public Block(ReadOnlySpan<int> planar, int block, int channels, int bps, long sample)
            {
                Planar = planar;
                BlockSize = block;
                Channels = channels;
                Bps = bps;
                SampleNumber = sample;
            }

            /// <summary>One channel's run of <see cref="BlockSize"/> samples.</summary>
            public ReadOnlySpan<int> Channel(int c) => Planar.Slice(c * BlockSize, BlockSize);
        }

        /// <summary>The stateless-across-frames decoder. Owns exactly two buffers, sized once from STREAMINFO:
        /// <c>MaxBlock × Channels</c> ints for the planar samples and 32 ints for LPC coefficients. Reused across
        /// seeks and, by the SHELL, across tracks of the same shape — a second <see cref="Open"/> with a smaller or
        /// equal <c>MaxBlock × Channels</c> allocates nothing.</summary>
        public sealed class Decoder
        {
            int[] _pcm = [];
            readonly int[] _coefs = new int[MaxLpcOrder];
            StreamInfo _si;

            /// <summary>The stream this decoder was opened for.</summary>
            public StreamInfo Info => _si;

            /// <summary>How many ints the planar buffer holds — the one number the allocation gate reads.</summary>
            public int BufferInts => _pcm.Length;

            /// <summary>Size the buffers for this stream. The only allocation site after construction.</summary>
            public void Open(in StreamInfo si)
            {
                _si = si;
                int need = si.MaxBlock * si.Channels;
                if (_pcm.Length < need) _pcm = new int[need];
            }

            /// <summary>Decode the frame whose sync byte is <paramref name="win"/>[0]. On <see cref="FrameResult.Ok"/>,
            /// <paramref name="consumed"/> is the frame's byte length (sync .. CRC-16) and <paramref name="block"/>
            /// views the samples until the next call. <see cref="FrameResult.Overrun"/> means the window ended inside
            /// the frame: refill and call again with the same start (no state was kept). Any other result: the caller
            /// skips one byte and resyncs.</summary>
            public FrameResult DecodeFrame(ReadOnlySpan<byte> win, out int consumed, out Block block)
            {
                consumed = 0;
                block = default;
                HeaderResult hr = ParseFrameHeader(win, _si, out FrameHeader h);
                if (hr == HeaderResult.Truncated) return FrameResult.Overrun;
                if (hr != HeaderResult.Ok) return FrameResult.Reserved;
                if (h.BlockSize > _si.MaxBlock || h.Channels != _si.Channels) return FrameResult.Reserved;
                if (h.BlockSize * h.Channels > _pcm.Length) return FrameResult.Unsupported;   // Open was never called

                var r = new BitReader(win, h.HeaderBytes);
                Span<int> pcm = _pcm.AsSpan(0, h.BlockSize * h.Channels);
                for (int c = 0; c < h.Channels; c++)
                {
                    // §4.2: the side channel carries one extra bit — L/S: channel 1; S/R: channel 0; M/S: channel 1.
                    int bps = h.Bps + ((h.Assignment == 1 && c == 1) || (h.Assignment == 2 && c == 0)
                                    || (h.Assignment == 3 && c == 1) ? 1 : 0);
                    FrameResult fr = DecodeSubframe(ref r, bps, pcm.Slice(c * h.BlockSize, h.BlockSize), _coefs);
                    if (fr != FrameResult.Ok) return fr;
                }
                r.AlignToByte();
                if (r.Overrun) return FrameResult.Overrun;
                int end = r.BytePosition;
                if (end + 2 > win.Length) return FrameResult.Overrun;
                ushort crc = (ushort)((win[end] << 8) | win[end + 1]);
                if (Crc16(win[..end]) != crc) return FrameResult.BadCrc16;

                if (h.Assignment != 0)
                    Decorrelate(h.Assignment, pcm[..h.BlockSize], pcm.Slice(h.BlockSize, h.BlockSize));
                consumed = end + 2;
                block = new Block(pcm, h.BlockSize, h.Channels, h.Bps, h.SampleNumber);
                return FrameResult.Ok;
            }
        }

        /// <summary>The correctness oracle (§8.2, Symphonia validate.rs:36-98): every sample of every channel,
        /// interleaved, signed, little-endian, in ceil(bps/8) bytes, hashed with MD5 and compared to STREAMINFO.
        /// Used by the tests and by the <c>--flac-probe</c> arm; never by the pump. <c>IncrementalHash</c> allocates
        /// once per verifier and the packing buffer grows to its high-water mark on the first block.</summary>
        public sealed class Md5Verifier : IDisposable
        {
            readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            byte[] _pack = [];

            /// <summary>Pack one block and feed it to the running hash. The transpose (planar → interleaved) and the
            /// LE narrowing are one pointer loop per byte width — no inner <c>k</c> loop, no bounds check per byte,
            /// and no allocation after the packing buffer reaches its high-water mark on the first block.</summary>
            public void Append(in Block b)
            {
                int bytesPer = (b.Bps + 7) >> 3;
                int block = b.BlockSize, channels = b.Channels;
                int need = block * channels * bytesPer;
                if (need == 0 || b.Planar.Length < block * channels) return;
                if (_pack.Length < need) _pack = new byte[need];
                fixed (byte* dst = _pack)
                fixed (int* src = b.Planar)
                {
                    byte* o = dst;
                    switch (bytesPer)
                    {
                        case 1:
                            for (int i = 0; i < block; i++)
                                for (int c = 0; c < channels; c++) *o++ = (byte)src[c * block + i];
                            break;
                        case 2:
                            for (int i = 0; i < block; i++)
                                for (int c = 0; c < channels; c++)
                                {
                                    int v = src[c * block + i];
                                    o[0] = (byte)v; o[1] = (byte)(v >> 8);
                                    o += 2;
                                }
                            break;
                        case 3:
                            for (int i = 0; i < block; i++)
                                for (int c = 0; c < channels; c++)
                                {
                                    int v = src[c * block + i];
                                    o[0] = (byte)v; o[1] = (byte)(v >> 8); o[2] = (byte)(v >> 16);
                                    o += 3;
                                }
                            break;
                        default:
                            for (int i = 0; i < block; i++)
                                for (int c = 0; c < channels; c++)
                                {
                                    int v = src[c * block + i];
                                    o[0] = (byte)v; o[1] = (byte)(v >> 8); o[2] = (byte)(v >> 16); o[3] = (byte)(v >> 24);
                                    o += 4;
                                }
                            break;
                    }
                }
                _md5.AppendData(_pack.AsSpan(0, need));
            }

            /// <summary>Finish, and compare with STREAMINFO's digest. Resets the verifier.</summary>
            public bool Matches(in StreamInfo si)
            {
                Span<byte> digest = stackalloc byte[16];
                _md5.GetHashAndReset(digest);
                ReadOnlySpan<byte> expected = si.Md5;
                return digest.SequenceEqual(expected);
            }

            /// <summary>Finish, and hand back the digest. Resets the verifier.</summary>
            public void Finish(Span<byte> digest)
            {
                _md5.GetHashAndReset(digest);
            }

            public void Dispose() => _md5.Dispose();
        }
    }
}
