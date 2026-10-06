// ── Playback/Playback.Audio.Mp4.cs ───────────────────────────────────────────────────────────────────────────────────
// A local MP4-family file (.mp4 / .m4a / .m4v / .mov: an ISO BMFF or QuickTime container) on the AUDIO host. A partial of
// `Playback.Audio`, beside `LocalSource`, which is its only caller.
//
// WHY THIS EXISTS. A dropped or imported .mp4 is a local track first: it plays on the audio host (`PlayableKind.LocalFile`),
// and the attached-video roster only lights the video BUTTON (Shell/Video.Overrides.Mirror.cs, "attaching a file lights the
// button and plays nothing"), so the picture is the user's click away and the sound must come from here. `LocalSource` used to
// sniff only Ogg / FLAC / ID3 / an MPEG sync and fall back to MP3 for anything else, so every MP4 reached NLayer and failed
// with an IndexOutOfRangeException (2026-10-06).
//
// HOW. The app has an AAC decoder already — Media Foundation's AAC MFT behind `Modules.AacAudioDecoder`, which eats ADTS
// frames. So the container is undone here and nothing new is decoded: the `moov` sample table of the first AAC sound track is
// read once (offsets, sizes, the AudioSpecificConfig), and `Mp4AdtsSource` presents that track as a VIRTUAL ADTS byte stream,
// a 7-byte header synthesised in front of each sample, read straight from the file. It is seekable and knows its length, and
// it answers frame → offset (`IAdtsFrameIndex`), which is what lets the AAC decoder seek on a local file.
//
// WHAT IS REFUSED, AND SAYS SO. Anything that is not plain AAC (LC / Main / SSR / LTP, HE-AAC signalled implicitly or in the
// config) in a progressive file: a fragmented MP4, an encrypted (DRM) track, ALAC, AC-3 / E-AC-3, Opus or MP3 inside MP4, a
// file with no sound track. `TryReadAacTrack` answers why, and `LocalSource` turns it into `Fault.Unsupported`.

using System.Buffers.Binary;
using System.Globalization;
using FluentGpu.Media;

namespace Wavee;

public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>A byte source whose bytes are ADTS frames it can address by index (<see cref="Mp4AdtsSource"/>): what lets
        /// <see cref="Modules.AacAudioDecoder"/> seek a local AAC track. A live radio body never implements it.</summary>
        public interface IAdtsFrameIndex
        {
            /// <summary>How many ADTS frames the source carries.</summary>
            int FrameCount { get; }

            /// <summary>The byte offset frame <paramref name="frame"/> starts at (clamped; <see cref="FrameCount"/> is the end).</summary>
            long OffsetOfFrame(int frame);

            /// <summary>The track's length, from the container; 0 when it did not say.</summary>
            long DurationMs { get; }
        }

        /// <summary>One AAC sound track's sample table, read out of an MP4 <c>moov</c>. <see cref="Profile"/> is the ADTS profile
        /// (MPEG-4 object type − 1), <see cref="SamplingFrequencyIndex"/> the CORE rate's index (HE-AAC doubles it on output).</summary>
        public sealed record Mp4AacTrack(int Profile, int SamplingFrequencyIndex, int CoreSampleRate, int Channels,
            long[] Offsets, int[] Sizes, long DurationMs);

        /// <summary>The MP4 reading. PURE over a seekable stream: no decoder, no engine.</summary>
        public static class Mp4
        {
            const uint Ftyp = 0x66747970, Moov = 0x6D6F6F76, Moof = 0x6D6F6F66, Mdat = 0x6D646174, Wide = 0x77696465,
                Trak = 0x7472616B, Mdia = 0x6D646961, Hdlr = 0x68646C72, Mdhd = 0x6D646864, Minf = 0x6D696E66,
                Stbl = 0x7374626C, Stsd = 0x73747364, Stsz = 0x7374737A, Stz2 = 0x73747A32, Stsc = 0x73747363,
                Stco = 0x7374636F, Co64 = 0x636F3634, Soun = 0x736F756E, Mp4a = 0x6D703461, Esds = 0x65736473,
                Wave = 0x77617665, Enca = 0x656E6361, Alac = 0x616C6163, Ac3 = 0x61632D33, Ec3 = 0x65632D33,
                Opus = 0x4F707573, DotMp3 = 0x2E6D7033;

            /// <summary>A sound track's moov is kilobytes; a feature film's is a few megabytes. Past this it is not a file to
            /// read into memory on the playback chain.</summary>
            const long MaxMoovBytes = 64L << 20;

            /// <summary>ADTS carries a frame length in 13 bits, header included.</summary>
            public const int MaxAdtsFrameBytes = 8191;

            static ReadOnlySpan<int> Rates => [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

            /// <summary>Is <paramref name="head"/> the start of an MP4-family file? An ISO BMFF file opens with <c>ftyp</c>; an
            /// old QuickTime movie may open straight on <c>moov</c>, <c>mdat</c> or <c>wide</c>. Decided by the box, never by the
            /// extension. PURE.</summary>
            public static bool LooksLikeMp4(ReadOnlySpan<byte> head)
            {
                if (head.Length < 8) return false;
                uint size = BinaryPrimitives.ReadUInt32BigEndian(head);
                uint type = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
                if (type == Ftyp) return size is >= 8 and <= 4096;
                return (type is Moov or Mdat or Wide) && (size == 1 || size >= 8);
            }

            /// <summary>Read the first AAC sound track's sample table. False with <paramref name="why"/> (a short English reason
            /// for the log) when the file holds nothing this app can play.</summary>
            public static bool TryReadAacTrack(Stream file, out Mp4AacTrack? track, out string why)
            {
                track = null;
                long length = file.Length;
                byte[]? moov = null;
                bool fragmented = false;
                Span<byte> hdr = stackalloc byte[16];
                long pos = 0;
                while (pos + 8 <= length)
                {
                    file.Position = pos;
                    if (ReadFully(file, hdr[..8]) < 8) break;
                    long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
                    uint type = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..]);
                    int headerLen = 8;
                    if (size == 1)
                    {
                        if (ReadFully(file, hdr.Slice(8, 8)) < 8) break;
                        size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[8..]);
                        headerLen = 16;
                    }
                    else if (size == 0) size = length - pos;
                    if (size < headerLen) { why = "a damaged box at offset " + pos.ToString(CultureInfo.InvariantCulture); return false; }
                    if (type == Moof) fragmented = true;
                    if (type == Moov)
                    {
                        long body = Math.Min(size, length - pos) - headerLen;
                        if (body > MaxMoovBytes) { why = "a movie header too large to read"; return false; }
                        moov = new byte[body];
                        file.Position = pos + headerLen;
                        if (ReadFully(file, moov) < body) { why = "a truncated movie header"; return false; }
                    }
                    pos += size;
                }
                if (moov is null) { why = fragmented ? "a fragmented MP4 (no sample table)" : "no movie header (moov)"; return false; }
                if (!TryAacTrack(moov, length, out track, out why)) return false;
                if (track!.Sizes.Length == 0) { why = fragmented ? "a fragmented MP4 (no sample table)" : "an empty audio track"; track = null; return false; }
                return true;
            }

            static bool TryAacTrack(ReadOnlySpan<byte> moov, long fileLength, out Mp4AacTrack? track, out string why)
            {
                track = null;
                why = "no audio track";
                int cursor = 0;
                while (NextBox(moov, ref cursor, out uint type, out ReadOnlySpan<byte> trak))
                {
                    if (type != Trak) continue;
                    if (!Child(trak, Mdia, out ReadOnlySpan<byte> mdia)) continue;
                    if (!Child(mdia, Hdlr, out ReadOnlySpan<byte> hdlr) || hdlr.Length < 12 || U32(hdlr, 8) != Soun) continue;
                    if (!Child(mdia, Minf, out ReadOnlySpan<byte> minf) || !Child(minf, Stbl, out ReadOnlySpan<byte> stbl)
                        || !Child(stbl, Stsd, out ReadOnlySpan<byte> stsd))
                    {
                        why = "an audio track with no sample table";
                        continue;
                    }
                    if (!TryAudioConfig(stsd, out int profile, out int sfi, out int channels, out why)) continue;
                    if (!TrySamples(stbl, fileLength, out long[] offsets, out int[] sizes, out why)) continue;

                    int rate = Rates[sfi];
                    long durationMs = 0;
                    if (Child(mdia, Mdhd, out ReadOnlySpan<byte> mdhd) && mdhd.Length >= 24)
                    {
                        bool v1 = mdhd[0] == 1;
                        long timescale = v1 ? (mdhd.Length >= 32 ? U32(mdhd, 20) : 0) : U32(mdhd, 12);
                        long units = v1 ? (mdhd.Length >= 32 ? (long)BinaryPrimitives.ReadUInt64BigEndian(mdhd[24..]) : 0) : U32(mdhd, 16);
                        if (timescale > 0 && units > 0 && units != uint.MaxValue) durationMs = units * 1000 / timescale;
                    }
                    if (durationMs <= 0) durationMs = (long)sizes.Length * 1024 * 1000 / rate;
                    track = new Mp4AacTrack(profile, sfi, rate, channels, offsets, sizes, durationMs);
                    return true;
                }
                return false;
            }

            /// <summary>The first sample description: an <c>mp4a</c> entry whose <c>esds</c> names AAC, as ADTS fields.</summary>
            static bool TryAudioConfig(ReadOnlySpan<byte> stsd, out int profile, out int sfi, out int channels, out string why)
            {
                profile = sfi = channels = 0;
                int cursor = 8;                                                   // version/flags + entry_count
                if (stsd.Length < 16 || !NextBox(stsd, ref cursor, out uint format, out ReadOnlySpan<byte> entry))
                {
                    why = "an empty sample description";
                    return false;
                }
                if (format != Mp4a)
                {
                    why = format switch
                    {
                        Enca => "encrypted (DRM-protected) audio",
                        Alac => "Apple Lossless (ALAC) audio",
                        Ac3 or Ec3 => "Dolby Digital audio",
                        Opus => "Opus audio in an MP4",
                        DotMp3 => "MP3 audio in an MP4",
                        _ => "an unsupported audio codec '" + FourCc(format) + "'",
                    };
                    return false;
                }
                // AudioSampleEntry: 8 bytes of SampleEntry, then 20 of audio fields (QuickTime's sound description v1 adds 16,
                // v2 adds 36), then the child boxes.
                if (entry.Length < 28) { why = "a damaged audio sample entry"; return false; }
                int version = BinaryPrimitives.ReadUInt16BigEndian(entry[8..]);
                int entryChannels = BinaryPrimitives.ReadUInt16BigEndian(entry[16..]);
                int childrenAt = 28 + (version == 1 ? 16 : version == 2 ? 36 : 0);
                if (entry.Length < childrenAt) { why = "a damaged audio sample entry"; return false; }
                ReadOnlySpan<byte> children = entry[childrenAt..];
                if (!Child(children, Esds, out ReadOnlySpan<byte> esds)
                    && !(Child(children, Wave, out ReadOnlySpan<byte> wave) && Child(wave, Esds, out esds)))
                {
                    why = "an MPEG-4 audio entry with no decoder configuration";
                    return false;
                }
                if (!TryDecoderConfig(esds, out int oti, out ReadOnlySpan<byte> asc)) { why = "a damaged decoder configuration"; return false; }
                if (oti is not (0x40 or 0x66 or 0x67 or 0x68))                     // MPEG-4 audio, MPEG-2 AAC Main / LC / SSR
                {
                    why = oti is 0x69 or 0x6B ? "MP3 audio in an MP4"
                        : "an unsupported audio codec (object type 0x" + oti.ToString("X2", CultureInfo.InvariantCulture) + ")";
                    return false;
                }
                int objectType;
                if (asc.Length >= 2)
                {
                    if (!TryParseAsc(asc, out objectType, out sfi, out channels)) { why = "an unreadable AAC configuration"; return false; }
                }
                else if (oti != 0x40)                                              // MPEG-2 AAC with no config: the entry says it
                {
                    objectType = oti - 0x65;
                    sfi = IndexOfRate((int)(U32(entry, 24) >> 16));
                    channels = entryChannels;
                    if (sfi < 0) { why = "an AAC track at an unsupported sample rate"; return false; }
                }
                else { why = "an AAC track with no decoder configuration"; return false; }
                if (objectType is < 1 or > 4)
                {
                    why = objectType switch
                    {
                        42 => "xHE-AAC (USAC) audio",
                        23 or 39 => "low-delay AAC audio",
                        _ => "AAC object type " + objectType.ToString(CultureInfo.InvariantCulture),
                    };
                    return false;
                }
                if (channels is <= 0 or > 7) channels = entryChannels is >= 1 and <= 7 ? entryChannels : 0;
                if (channels <= 0) { why = "an AAC track with an undeclared channel layout"; return false; }
                profile = objectType - 1;
                why = "";
                return true;
            }

            /// <summary>The <c>esds</c> payload: ES_Descriptor → DecoderConfigDescriptor (the object type indication) →
            /// DecoderSpecificInfo (the AudioSpecificConfig, possibly empty).</summary>
            static bool TryDecoderConfig(ReadOnlySpan<byte> esds, out int oti, out ReadOnlySpan<byte> asc)
            {
                oti = 0;
                asc = default;
                if (esds.Length < 4) return false;
                int at = 4;                                                        // version/flags
                if (!ReadDescriptor(esds, ref at, out int tag, out ReadOnlySpan<byte> es) || tag != 0x03 || es.Length < 3) return false;
                int flags = es[2], p = 3;
                if ((flags & 0x80) != 0) p += 2;                                   // dependsOn_ES_ID
                if ((flags & 0x40) != 0) { if (p >= es.Length) return false; p += 1 + es[p]; }   // URL
                if ((flags & 0x20) != 0) p += 2;                                   // OCR_ES_Id
                if (p > es.Length) return false;
                while (ReadDescriptor(es, ref p, out tag, out ReadOnlySpan<byte> dcd))
                {
                    if (tag != 0x04) continue;
                    if (dcd.Length < 13) return false;
                    oti = dcd[0];
                    int inner = 13;
                    while (ReadDescriptor(dcd, ref inner, out int t, out ReadOnlySpan<byte> dsi))
                        if (t == 0x05) { asc = dsi; return true; }
                    return true;                                                   // no DecoderSpecificInfo: asc stays empty
                }
                return false;
            }

            /// <summary>One MPEG-4 descriptor at <paramref name="at"/> in <paramref name="d"/>: its tag and body, and
            /// <paramref name="at"/> moved past it.</summary>
            static bool ReadDescriptor(ReadOnlySpan<byte> d, scoped ref int at, out int tag, out ReadOnlySpan<byte> body)
            {
                tag = 0;
                body = default;
                if (at < 0 || d.Length - at < 2) return false;
                tag = d[at];
                int len = 0, i = at + 1;
                for (int k = 0; k < 4; k++)
                {
                    if (i >= d.Length) return false;
                    byte b = d[i++];
                    len = (len << 7) | (b & 0x7F);
                    if ((b & 0x80) == 0) break;
                }
                if (len > d.Length - i) return false;
                body = d.Slice(i, len);
                at = i + len;
                return true;
            }

            /// <summary>An MPEG-4 AudioSpecificConfig → the CORE object type (an explicit SBR/PS config names its core after
            /// the extension rate), the core rate's index and the channel configuration. PURE.</summary>
            public static bool TryParseAsc(ReadOnlySpan<byte> asc, out int objectType, out int sfi, out int channels)
            {
                objectType = sfi = channels = 0;
                // Everything read here sits in the first 8 bytes (two object types, two rates with 24-bit escapes, channels).
                ulong word = 0;
                int have = Math.Min(asc.Length, 8);
                for (int i = 0; i < 8; i++) word = (word << 8) | (i < have ? asc[i] : 0u);
                var r = new AscBits(word, have * 8);

                objectType = ObjectType(ref r);
                if (Rate(ref r, out sfi) <= 0 || sfi < 0) return false;
                channels = r.Take(4);
                if (channels < 0) return false;
                if (objectType is 5 or 29)
                {
                    if (Rate(ref r, out _) <= 0) return false;
                    objectType = ObjectType(ref r);
                }
                return objectType > 0;

                static int ObjectType(ref AscBits r)
                {
                    int t = r.Take(5);
                    if (t != 31) return t;
                    int ext = r.Take(6);
                    return ext < 0 ? -1 : 32 + ext;
                }

                static int Rate(ref AscBits r, out int index)
                {
                    index = r.Take(4);
                    if (index == 15)
                    {
                        int hz = r.Take(24);
                        index = IndexOfRate(hz);
                        return hz;
                    }
                    return index is >= 0 and < 13 ? Rates[index] : -1;
                }
            }

            /// <summary>An MSB-first bit cursor over at most 64 bits.</summary>
            struct AscBits(ulong word, int total)
            {
                int _at;

                public int Take(int n)
                {
                    if (_at + n > total) return -1;
                    int v = (int)((word >> (64 - _at - n)) & ((1UL << n) - 1));
                    _at += n;
                    return v;
                }
            }

            static int IndexOfRate(int hz)
            {
                for (int i = 0; i < Rates.Length; i++) if (Rates[i] == hz) return i;
                return -1;
            }

            /// <summary><c>stsz</c>/<c>stz2</c> + <c>stsc</c> + <c>stco</c>/<c>co64</c> → each sample's file offset and size.
            /// Samples past the end of the file (a copy still in progress) are dropped, not failed.</summary>
            static bool TrySamples(ReadOnlySpan<byte> stbl, long fileLength, out long[] offsets, out int[] sizes, out string why)
            {
                offsets = [];
                sizes = [];
                if (Child(stbl, Stsz, out ReadOnlySpan<byte> stsz))
                {
                    if (stsz.Length < 12) { why = "a damaged sample size table"; return false; }
                    uint fixedSize = U32(stsz, 4);
                    long count = U32(stsz, 8);
                    if (fixedSize == 0 && count > (stsz.Length - 12) / 4) { why = "a damaged sample size table"; return false; }
                    if (count > int.MaxValue / 8) { why = "an implausible sample count"; return false; }
                    sizes = new int[count];
                    for (int i = 0; i < count; i++) sizes[i] = (int)(fixedSize != 0 ? fixedSize : U32(stsz, 12 + i * 4));
                }
                else if (Child(stbl, Stz2, out ReadOnlySpan<byte> stz2))
                {
                    if (stz2.Length < 12) { why = "a damaged sample size table"; return false; }
                    int field = stz2[7];
                    long count = U32(stz2, 8);
                    if (field is not (4 or 8 or 16) || count > (long)(stz2.Length - 12) * 8 / field) { why = "a damaged sample size table"; return false; }
                    sizes = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        sizes[i] = field switch
                        {
                            16 => BinaryPrimitives.ReadUInt16BigEndian(stz2[(12 + i * 2)..]),
                            8 => stz2[12 + i],
                            _ => (stz2[12 + i / 2] >> ((i & 1) == 0 ? 4 : 0)) & 0x0F,
                        };
                    }
                }
                else { why = "no sample size table"; return false; }

                bool wide = false;
                if (!Child(stbl, Stco, out ReadOnlySpan<byte> chunks))
                {
                    if (!Child(stbl, Co64, out chunks)) { why = "no chunk offset table"; return false; }
                    wide = true;
                }
                if (chunks.Length < 8) { why = "a damaged chunk offset table"; return false; }
                long chunkCount = U32(chunks, 4);
                if (chunkCount > (chunks.Length - 8) / (wide ? 8 : 4)) { why = "a damaged chunk offset table"; return false; }
                if (!Child(stbl, Stsc, out ReadOnlySpan<byte> stsc) || stsc.Length < 8) { why = "no sample-to-chunk table"; return false; }
                long runs = U32(stsc, 4);
                if (runs > (stsc.Length - 8) / 12) { why = "a damaged sample-to-chunk table"; return false; }

                int n = sizes.Length;
                offsets = new long[n];
                int s = 0;
                for (int r = 0; r < runs && s < n; r++)
                {
                    long first = U32(stsc, 8 + r * 12);
                    long perChunk = U32(stsc, 12 + r * 12);
                    long nextFirst = r + 1 < runs ? U32(stsc, 8 + (r + 1) * 12) : chunkCount + 1;
                    if (first < 1 || perChunk == 0) continue;
                    for (long c = first; c < nextFirst && c <= chunkCount && s < n; c++)
                    {
                        int at = 8 + (int)(c - 1) * (wide ? 8 : 4);
                        long o = wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(chunks[at..]) : U32(chunks, at);
                        for (long k = 0; k < perChunk && s < n; k++)
                        {
                            offsets[s] = o;
                            o += sizes[s];
                            s++;
                        }
                    }
                }

                // Keep the playable prefix: every sample mapped to a chunk, inside the file, and small enough for an ADTS frame.
                int keep = 0;
                while (keep < s && sizes[keep] is > 0 and <= MaxAdtsFrameBytes - 7 && offsets[keep] + sizes[keep] <= fileLength) keep++;
                if (keep < s && sizes[keep] > MaxAdtsFrameBytes - 7) { why = "an AAC frame too large to play"; return false; }
                if (keep < n)
                {
                    Array.Resize(ref offsets, keep);
                    Array.Resize(ref sizes, keep);
                }
                why = keep == 0 ? "an empty audio track" : "";
                return keep > 0;
            }

            // ── box walking (in memory) ──────────────────────────────────────────────────────────────────────────────

            static bool NextBox(ReadOnlySpan<byte> parent, scoped ref int cursor, out uint type, out ReadOnlySpan<byte> payload)
            {
                type = 0;
                payload = default;
                if (cursor < 0 || cursor + 8 > parent.Length) return false;
                long size = U32(parent, cursor);
                type = U32(parent, cursor + 4);
                int header = 8;
                if (size == 1)
                {
                    if (cursor + 16 > parent.Length) return false;
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(parent[(cursor + 8)..]);
                    header = 16;
                }
                else if (size == 0) size = parent.Length - cursor;
                if (size < header || size > parent.Length - cursor) return false;
                payload = parent.Slice(cursor + header, (int)size - header);
                cursor += (int)size;
                return true;
            }

            static bool Child(ReadOnlySpan<byte> parent, uint type, out ReadOnlySpan<byte> payload)
            {
                int cursor = 0;
                while (NextBox(parent, ref cursor, out uint t, out payload))
                    if (t == type) return true;
                payload = default;
                return false;
            }

            static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b[at..]);

            static string FourCc(uint t)
            {
                Span<char> c = stackalloc char[4];
                for (int i = 0; i < 4; i++)
                {
                    char ch = (char)((t >> (24 - i * 8)) & 0xFF);
                    c[i] = ch is >= ' ' and <= '~' ? ch : '?';
                }
                return new string(c);
            }

            static int ReadFully(Stream s, Span<byte> dst)
            {
                int total = 0;
                while (total < dst.Length)
                {
                    int n = s.Read(dst[total..]);
                    if (n <= 0) break;
                    total += n;
                }
                return total;
            }
        }

        /// <summary>One MP4 AAC track as a seekable, length-known ADTS byte stream: frame <c>k</c> is a synthesised 7-byte
        /// header (no CRC) followed by sample <c>k</c>'s bytes, read from the file where the sample table says they are.
        /// Reads never allocate; the file is opened on <see cref="TryOpen"/> and released on <see cref="Close"/>.</summary>
        public sealed class Mp4AdtsSource : IMediaByteSource, IAdtsFrameIndex
        {
            readonly string _path;
            readonly Mp4AacTrack _track;
            readonly long[] _virtual;              // frame k starts at _virtual[k]; _virtual[^1] is the length
            readonly byte[] _header = new byte[7];
            FileStream? _fs;
            long _pos;
            int _frame;                            // the frame _pos was last in (the sequential read's fast path)

            public Mp4AdtsSource(string path, Mp4AacTrack track)
            {
                _path = path;
                _track = track;
                _virtual = new long[track.Sizes.Length + 1];
                for (int i = 0; i < track.Sizes.Length; i++) _virtual[i + 1] = _virtual[i] + 7 + track.Sizes[i];
            }

            public int FrameCount => _track.Sizes.Length;

            public long DurationMs => _track.DurationMs;

            public long OffsetOfFrame(int frame) => _virtual[Math.Clamp(frame, 0, FrameCount)];

            public long? Length => _virtual[^1];

            public SourceCaps Caps => new() { Seekable = true, KnownLength = true };

            public bool TryOpen(in DataSpec spec)
            {
                try
                {
                    _fs ??= new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                }
                catch (Exception ex)
                {
                    Log.Warn("audio", "mp4 open failed", ex);
                    return false;
                }
                _pos = Math.Clamp(spec.Position, 0, _virtual[^1]);
                return true;
            }

            public int Read(Span<byte> dst)
            {
                FileStream? fs = _fs;
                if (fs is null) return -1;
                long end = _virtual[^1];
                int written = 0;
                try
                {
                    while (written < dst.Length && _pos < end)
                    {
                        int k = FrameAt(_pos);
                        long into = _pos - _virtual[k];
                        if (into < 7)
                        {
                            WriteHeader(_track, _track.Sizes[k], _header);
                            int take = (int)Math.Min(7 - into, dst.Length - written);
                            _header.AsSpan((int)into, take).CopyTo(dst[written..]);
                            written += take;
                            _pos += take;
                            continue;
                        }
                        long payload = into - 7;
                        int want = (int)Math.Min(_track.Sizes[k] - payload, dst.Length - written);
                        long at = _track.Offsets[k] + payload;
                        if (fs.Position != at) fs.Position = at;
                        int got = fs.Read(dst.Slice(written, want));
                        if (got <= 0) return written > 0 ? written : -1;   // the file shrank under us: an error, not the end
                        written += got;
                        _pos += got;
                    }
                }
                catch (ObjectDisposedException) { return written > 0 ? written : -1; }
                catch (IOException ex)
                {
                    Log.Warn("audio", "mp4 read failed", ex);
                    return written > 0 ? written : -1;
                }
                return written;                                            // 0 only at the end
            }

            public long Seek(long offset)
            {
                if (_fs is null) return -1;
                _pos = Math.Clamp(offset, 0, _virtual[^1]);
                return _pos;
            }

            public void Cancel() { }

            public void Close()
            {
                FileStream? fs = Interlocked.Exchange(ref _fs, null);
                try { fs?.Dispose(); } catch { }
            }

            int FrameAt(long pos)
            {
                int k = _frame;
                if ((uint)k < (uint)FrameCount && _virtual[k] <= pos && pos < _virtual[k + 1]) return k;
                if ((uint)(k + 1) < (uint)FrameCount && _virtual[k + 1] <= pos && pos < _virtual[k + 2]) return _frame = k + 1;
                int i = Array.BinarySearch(_virtual, 0, FrameCount, pos);
                return _frame = i >= 0 ? i : Math.Max(0, ~i - 1);
            }

            /// <summary>The 7-byte ADTS header (MPEG-4, no CRC, one raw data block) for one frame of <paramref name="track"/>. PURE.</summary>
            public static void WriteHeader(Mp4AacTrack track, int payloadBytes, Span<byte> h)
            {
                int len = payloadBytes + 7;
                int ch = track.Channels;
                h[0] = 0xFF;
                h[1] = 0xF1;
                h[2] = (byte)(((track.Profile & 0x03) << 6) | ((track.SamplingFrequencyIndex & 0x0F) << 2) | ((ch >> 2) & 0x01));
                h[3] = (byte)(((ch & 0x03) << 6) | ((len >> 11) & 0x03));
                h[4] = (byte)((len >> 3) & 0xFF);
                h[5] = (byte)(((len & 0x07) << 5) | 0x1F);
                h[6] = 0xFC;
            }
        }
    }
}
