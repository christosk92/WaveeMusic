// ── Playback/LocalTags.Mpeg.cs — ID3v2, ID3v1, APE and the MPEG audio length ────────────────────────────────────────
//
// ID3v2 (2.2, 2.3, 2.4) is walked frame by frame through the byte window: only the text frames a row needs are read, and
// an APIC/PIC frame is read only far enough to know its mime, type and where its picture starts. A v2.3 tag with
// tag-level unsynchronisation is the one case read whole and decoded (every boundary shifts). v2.4 frame sizes are
// syncsafe, except where iTunes wrote them plain: the size that lands on a real frame header wins. Duplicate tags merge.
//
// The tail (one read of the last 160 bytes) holds ID3v1, a Lyrics3v2 marker and an APE footer, checked in lofty's
// order with its `APETAGEX` collision check: an APE header ending at End-123 puts the letters "TAG" at End-128. They fill
// only what the ID3v2 tag left empty, and size the bitrate estimate.
//
// The length: our own Xing/Info frame count (at 4 + side info) or VBRI (at +36) × samples per frame ÷ rate. Without
// either, the audio bytes × 8 ÷ the average bitrate of the frames in the head window, flagged `DurationEstimated` (a VBR
// file without a header is still off by up to about 10 %, and §3.4 gives it a wider tolerance).

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Wavee;

public static partial class LocalTags
{
    /// <summary>The biggest ID3v2.3 tag read whole for de-unsynchronisation (a tag-level unsynchronised tag is rare and old).</summary>
    const int MaxUnsyncTagBytes = 32 << 20;

    /// <summary>How far past the tag the first MPEG frame is looked for (junk before the audio, lofty's cap is 1 KiB).</summary>
    const int MaxJunkBytes = 4096;

    // ── ID3v2 ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every ID3v2 tag from <paramref name="at"/> up to <paramref name="end"/> (back-to-back tags merge: one source).</summary>
    static void ReadId3v2Chain(Bytes io, long at, long end, Builder b)
    {
        for (int i = 0; i < 4 && at < end; i++)
        {
            if (!io.TryGet(at, 10, out ReadOnlySpan<byte> h)) return;
            long len = Playback.Audio.Id3v2Length(h);
            if (len <= 0) return;
            ReadId3v2(io, at, b);
            at += len;
        }
    }

    /// <summary>One ID3v2 tag at <paramref name="at"/> in <paramref name="io"/> (a file, or a WAV/AIFF chunk's body).</summary>
    static void ReadId3v2(Bytes io, long at, Builder b)
    {
        if (!io.TryGet(at, 10, out ReadOnlySpan<byte> h)) return;
        int version = h[3];
        byte flags = h[5];
        long size = ((long)h[6] << 21) | ((long)h[7] << 14) | ((long)h[8] << 7) | h[9];
        if (version is < 2 or > 4) return;
        long start = at + 10, end = Math.Min(start + size, io.Length);
        bool tagUnsync = (flags & 0x80) != 0;

        if (tagUnsync && version < 4)
        {
            ReadUnsyncedTag(io, start, end, version, (flags & 0x40) != 0, b);
            return;
        }
        long pos = start;
        if ((flags & 0x40) != 0 && version >= 3)
        {
            if (!io.TryGet(pos, 4, out ReadOnlySpan<byte> ext)) return;
            pos += version == 3 ? 4 + BinaryPrimitives.ReadUInt32BigEndian(ext) : Syncsafe(ext);
        }
        WalkFrames(io, pos, end, version, tagUnsync, b);
    }

    /// <summary>A v2.2/2.3 tag with tag-level unsynchronisation: read whole, decoded, walked in memory.</summary>
    static void ReadUnsyncedTag(Bytes io, long start, long end, int version, bool extended, Builder b)
    {
        long rawLength = end - start;
        if (rawLength <= 0 || rawLength > MaxUnsyncTagBytes || io is not FileBytes file) return;
        byte[] raw = ArrayPool<byte>.Shared.Rent((int)rawLength);
        byte[] decoded = ArrayPool<byte>.Shared.Rent((int)rawLength);
        try
        {
            int got = file.ReadInto(start, raw.AsSpan(0, (int)rawLength));
            int n = Deunsync(raw.AsSpan(0, got), decoded);
            var view = new MemoryBytes(decoded, n, raw, got, start);
            view.Init();
            long pos = 0;
            if (extended && version == 3 && view.TryGet(0, 4, out ReadOnlySpan<byte> ext)) pos = 4 + BinaryPrimitives.ReadUInt32BigEndian(ext);
            WalkFrames(view, pos, n, version, frameUnsync: false, b, wholeTagUnsync: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(decoded);
        }
    }

    /// <summary>The frame walk. <paramref name="frameUnsync"/>: a v2.4 tag whose header says every frame is unsynchronised.
    /// <paramref name="wholeTagUnsync"/>: <paramref name="io"/> is the decoded copy of a tag that was (a cover's bytes in the
    /// file are still unsynchronised).</summary>
    static void WalkFrames(Bytes io, long pos, long end, int version, bool frameUnsync, Builder b, bool wholeTagUnsync = false)
    {
        int headerLen = version == 2 ? 6 : 10, idLen = version == 2 ? 3 : 4;
        while (pos + headerLen <= end)
        {
            if (!io.TryGet(pos, headerLen, out ReadOnlySpan<byte> fh)) return;
            if (!IsFrameId(fh[..idLen])) return;                                    // padding, or the tag ends here
            byte format = version == 2 ? (byte)0 : fh[9];
            uint id = version == 2 ? (uint)((fh[0] << 16) | (fh[1] << 8) | fh[2]) : BinaryPrimitives.ReadUInt32BigEndian(fh);
            long size = version switch                                              // last: the v2.4 check may refill the window
            {
                2 => (fh[3] << 16) | (fh[4] << 8) | fh[5],
                3 => BinaryPrimitives.ReadUInt32BigEndian(fh[4..]),
                _ => FrameSize24(io, pos, end, fh),
            };
            long dataAt = pos + headerLen;
            if (size <= 0 || dataAt + size > end) return;
            pos = dataAt + size;

            // The bytes before the payload, and whether the payload is compressed / encrypted (then it is skipped).
            int prefix = 0;
            bool unsync = frameUnsync, skip = false;
            if (version == 3)
            {
                if ((format & 0x80) != 0) { prefix += 4; skip = true; }
                if ((format & 0x40) != 0) { prefix += 1; skip = true; }
                if ((format & 0x20) != 0) prefix += 1;
            }
            else if (version == 4)
            {
                if ((format & 0x40) != 0) prefix += 1;
                if ((format & 0x08) != 0 || (format & 0x04) != 0) skip = true;
                if ((format & 0x04) != 0) prefix += 1;
                if ((format & 0x01) != 0) prefix += 4;
                if ((format & 0x02) != 0) unsync = true;
            }
            if (skip || prefix >= size) continue;
            dataAt += prefix;
            int dataLen = (int)Math.Min(size - prefix, int.MaxValue);

            var kind = FrameKindOf(id, version);
            if (kind == FrameKind.None) continue;
            try
            {
                if (kind == FrameKind.Picture) ReadPictureFrame(io, dataAt, dataLen, version, unsync, wholeTagUnsync, b);
                else
                {
                    if (!io.TryGet(dataAt, Math.Min(dataLen, MaxTextBytes), out ReadOnlySpan<byte> data)) continue;
                    if (unsync)
                    {
                        byte[] tmp = ArrayPool<byte>.Shared.Rent(data.Length);
                        try { ApplyText(kind, tmp.AsSpan(0, Deunsync(data, tmp)), version, b); }
                        finally { ArrayPool<byte>.Shared.Return(tmp); }
                    }
                    else ApplyText(kind, data, version, b);
                }
            }
            catch (Exception) { /* best attempt: this frame's field stays empty */ }
        }
    }

    /// <summary>A v2.4 frame size: syncsafe by the spec, plain where iTunes wrote it so. A byte with its top bit set cannot be
    /// syncsafe; otherwise, when the two readings differ, the one that lands on a frame header (or the tag's end) wins.</summary>
    static long FrameSize24(Bytes io, long pos, long end, ReadOnlySpan<byte> fh)
    {
        long plain = BinaryPrimitives.ReadUInt32BigEndian(fh[4..]);
        if (((fh[4] | fh[5] | fh[6] | fh[7]) & 0x80) != 0) return plain;
        long safe = Syncsafe(fh[4..]);
        if (safe == plain) return safe;
        if (LandsOnFrame(io, pos + 10 + safe, end)) return safe;
        return LandsOnFrame(io, pos + 10 + plain, end) ? plain : safe;

        static bool LandsOnFrame(Bytes io, long at, long end)
        {
            if (at == end) return true;
            if (at > end || !io.TryGet(at, Math.Min(4, (int)(end - at)), out ReadOnlySpan<byte> next)) return false;
            return next.Length < 4 || next[0] == 0 || IsFrameId(next);
        }
    }

    static long Syncsafe(ReadOnlySpan<byte> b) => ((long)(b[0] & 0x7F) << 21) | ((long)(b[1] & 0x7F) << 14) | ((long)(b[2] & 0x7F) << 7) | (long)(b[3] & 0x7F);

    static bool IsFrameId(ReadOnlySpan<byte> id)
    {
        foreach (byte c in id) if (!(c is >= (byte)'A' and <= (byte)'Z' || c is >= (byte)'0' and <= (byte)'9')) return false;
        return id.Length > 0;
    }

    enum FrameKind : byte { None, Title, Artist, Album, AlbumArtist, Track, Disc, Year, Date, Length, Picture, Isrc }

    static FrameKind FrameKindOf(uint id, int version) => version == 2
        ? id switch
        {
            0x545432 => FrameKind.Title,        // TT2
            0x545031 => FrameKind.Artist,       // TP1
            0x54414C => FrameKind.Album,        // TAL
            0x545032 => FrameKind.AlbumArtist,  // TP2
            0x54524B => FrameKind.Track,        // TRK
            0x545041 => FrameKind.Disc,         // TPA
            0x545945 => FrameKind.Year,         // TYE
            0x544C45 => FrameKind.Length,       // TLE
            0x504943 => FrameKind.Picture,      // PIC
            0x545243 => FrameKind.Isrc,         // TRC
            _ => FrameKind.None,
        }
        : id switch
        {
            0x54495432 => FrameKind.Title,       // TIT2
            0x54504531 => FrameKind.Artist,      // TPE1
            0x54414C42 => FrameKind.Album,       // TALB
            0x54504532 => FrameKind.AlbumArtist, // TPE2
            0x5452434B => FrameKind.Track,       // TRCK
            0x54504F53 => FrameKind.Disc,        // TPOS
            0x54594552 => FrameKind.Year,        // TYER
            0x54445243 => FrameKind.Date,        // TDRC
            0x544C454E => FrameKind.Length,      // TLEN
            0x41504943 => FrameKind.Picture,     // APIC
            0x54535243 => FrameKind.Isrc,        // TSRC
            _ => FrameKind.None,
        };

    /// <summary>A text frame's payload (encoding byte, then text) into the builder.</summary>
    static void ApplyText(FrameKind kind, ReadOnlySpan<byte> data, int version, Builder b)
    {
        if (data.Length < 2) return;
        byte enc = data[0];
        ReadOnlySpan<byte> text = data[1..];
        if (kind == FrameKind.Artist)
        {
            // Values are NUL-separated (v2.4, and some v2.3 writers); each one then takes the one splitting rule every format shares
            // (Builder.AddArtist: ";" and a spaced " / ", never a bare "/"). The NUL split happens here, on the encoded bytes,
            // because a UTF-16 NUL is two aligned zero bytes.
            int unit = enc is 1 or 2 ? 2 : 1;
            int start = 0;
            for (int i = 0; i + unit <= text.Length; i += unit)
            {
                bool nul = text[i] == 0 && (unit == 1 || text[i + 1] == 0);
                if (!nul) continue;
                b.AddArtist(DecodeText(text[start..i], enc, b), 1);
                start = i + unit;
            }
            if (start < text.Length) b.AddArtist(DecodeText(text[start..], enc, b), 1);
            return;
        }
        string value = DecodeText(FirstValue(text, enc), enc, b);
        switch (kind)
        {
            case FrameKind.Title: b.SetTitle(value); break;
            case FrameKind.Album: b.SetAlbum(value); break;
            case FrameKind.AlbumArtist: b.SetAlbumArtist(value); break;
            case FrameKind.Track: b.SetTrack(LeadingInt(value)); break;
            case FrameKind.Disc: b.SetDisc(LeadingInt(value)); break;
            case FrameKind.Isrc: b.SetIsrc(value); break;
            case FrameKind.Year or FrameKind.Date: b.SetYear(YearOf(value)); break;
            case FrameKind.Length:
                if (long.TryParse(value.AsSpan().Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ms))
                    b.SetEstimate(ms);                                                  // TLEN is often wrong: a last resort only
                break;
        }
    }

    /// <summary>The text before the first terminator (one NUL, or two aligned NULs for UTF-16).</summary>
    static ReadOnlySpan<byte> FirstValue(ReadOnlySpan<byte> text, byte enc)
    {
        if (enc is 1 or 2)
        {
            for (int i = 0; i + 1 < text.Length; i += 2) if (text[i] == 0 && text[i + 1] == 0) return text[..i];
            return text;
        }
        int nul = text.IndexOf((byte)0);
        return nul >= 0 ? text[..nul] : text;
    }

    static string DecodeText(ReadOnlySpan<byte> text, byte enc, Builder b) => enc switch
    {
        0 => Windows1252(text, b),
        1 => Utf16(text, bigEndian: false),                                         // a BOM decides; none = little-endian (the common bug)
        2 => Utf16(text, bigEndian: true),
        3 => Utf8(text),
        _ => "",
    };

    /// <summary>APIC (v2.3/2.4) or PIC (v2.2): encoding, mime (or a three-letter format), type, description, then the picture.
    /// Only the bytes before the picture are read; the picture's offset and length are recorded.</summary>
    static void ReadPictureFrame(Bytes io, long dataAt, int dataLen, int version, bool unsync, bool wholeTagUnsync, Builder b)
    {
        int peek = Math.Min(dataLen, 2048);
        if (!io.TryGet(dataAt, peek, out ReadOnlySpan<byte> raw)) return;
        ReadOnlySpan<byte> p = raw;
        byte[]? tmp = null;
        try
        {
            if (unsync)
            {
                tmp = ArrayPool<byte>.Shared.Rent(raw.Length);
                p = tmp.AsSpan(0, Deunsync(raw, tmp));
            }
            if (p.Length < 4) return;
            byte enc = p[0];
            int i = 1;
            LocalCoverMime mime;
            if (version == 2)
            {
                mime = MimeOf(p.Slice(1, 3));
                i = 4;
            }
            else
            {
                int nul = p[1..].IndexOf((byte)0);
                if (nul < 0) return;
                mime = MimeOf(p.Slice(1, nul));
                i = 1 + nul + 1;
            }
            if (i >= p.Length) return;
            byte type = p[i++];
            // The description, terminated by one NUL (Latin-1 / UTF-8) or two aligned NULs (UTF-16).
            if (enc is 1 or 2)
            {
                while (i + 1 < p.Length && !(p[i] == 0 && p[i + 1] == 0)) i += 2;
                i += 2;
            }
            else
            {
                int nul = p[i..].IndexOf((byte)0);
                if (nul < 0) return;
                i += nul + 1;
            }
            if (i > p.Length) return;
            // `i` counts decoded bytes; in a frame-unsynchronised payload the raw bytes before the picture may be more.
            int rawPrefix = unsync ? RawLengthOf(raw, i) : i;
            long pictureAt = dataAt + rawPrefix;
            long fileAt = io.FileOffset(pictureAt);
            long fileEnd = io.FileOffset(dataAt + dataLen);
            int length = (int)Math.Min(fileEnd - fileAt, int.MaxValue);
            if (length <= 0) return;
            b.OfferCover(new LocalCover(fileAt, length, mime,
                unsync || wholeTagUnsync ? LocalCoverEncoding.Unsync : LocalCoverEncoding.Raw, type));
        }
        finally
        {
            if (tmp is not null) ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    /// <summary>De-unsynchronise <paramref name="raw"/> into <paramref name="into"/> (FF 00 → FF); the length written.</summary>
    static int Deunsync(ReadOnlySpan<byte> raw, Span<byte> into)
    {
        int n = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            into[n++] = raw[i];
            if (raw[i] == 0xFF && i + 1 < raw.Length && raw[i + 1] == 0x00) i++;
        }
        return n;
    }

    /// <summary>How many raw (unsynchronised) bytes decode to the first <paramref name="decoded"/> bytes.</summary>
    static int RawLengthOf(ReadOnlySpan<byte> raw, int decoded)
    {
        int d = 0, r = 0;
        while (d < decoded && r < raw.Length)
        {
            if (raw[r] == 0xFF && r + 1 < raw.Length && raw[r + 1] == 0x00) r += 2;
            else r++;
            d++;
        }
        return r;
    }

    // ── the tail: ID3v1, Lyrics3v2, APE ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Read the last 160 bytes once: ID3v1 and an APE tag fill what is still empty. Returns how many bytes at the end are
    /// tags rather than audio (for the bitrate estimate).</summary>
    static long ReadTail(FileBytes io, long audioStart, Builder b)
    {
        long length = io.Length;
        if (length - audioStart < 128) return 0;
        long tailAt = Math.Max(audioStart, length - 160);
        ReadOnlySpan<byte> tail = io.UpTo(tailAt, (int)(length - tailAt), readAhead: 160);
        if (tail.Length < 128) return 0;
        long tagBytes = 0;
        long v1At = length - 128;
        ReadOnlySpan<byte> v1 = tail[^128..];
        bool apeCollision = tail.Length >= 131 && tail[^131..^123].SequenceEqual("APETAGEX"u8);
        bool hasV1 = v1[..3].SequenceEqual("TAG"u8) && !apeCollision;
        long before = length;
        if (hasV1)
        {
            tagBytes = 128;
            before = v1At;
            ReadId3v1(v1, b);
        }

        // Lyrics3v2 ends with "LYRICS200" right before ID3v1, after a six-digit size.
        if (hasV1 && tail.Length >= 128 + 15 && tail[^137..^128].SequenceEqual("LYRICS200"u8)
            && int.TryParse(System.Text.Encoding.ASCII.GetString(tail[^143..^137]), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int lyrics) && lyrics > 0 && before - 15 - lyrics > audioStart)
        {
            tagBytes += 15 + lyrics;
            before -= 15 + lyrics;
        }

        // An APE footer: the 32 bytes before ID3v1 / Lyrics3, or the file's last 32.
        long footerAt = before - 32;
        if (footerAt > audioStart && io.TryGet(footerAt, 32, out ReadOnlySpan<byte> footer, readAhead: 32) && footer[..8].SequenceEqual("APETAGEX"u8))
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]);
            bool hasHeader = (flags & 0x80000000) != 0;
            if (size >= 32 && size <= before - audioStart)
            {
                tagBytes += size + (hasHeader ? 32 : 0);
                long itemsAt = before - size;
                int itemsLen = (int)size - 32;
                if (NeedsFallback(b) && itemsLen is > 0 and <= WindowBytes && io.TryGet(itemsAt, itemsLen, out ReadOnlySpan<byte> items))
                    ReadApeItems(items, itemsAt, count, b);
            }
        }
        return tagBytes;
    }

    static bool NeedsFallback(Builder b) => b.Title.Length == 0 || b.Artists.Count == 0 || b.Album.Length == 0 || !b.Cover.Exists;

    static void ReadId3v1(ReadOnlySpan<byte> v1, Builder b)
    {
        b.SetTitle(Windows1252(v1.Slice(3, 30), b));
        b.AddArtist(Windows1252(v1.Slice(33, 30), b), 3);
        b.SetAlbum(Windows1252(v1.Slice(63, 30), b));
        b.SetYear(YearOf(Windows1252(v1.Slice(93, 4), b)));
        if (v1[125] == 0 && v1[126] != 0) b.SetTrack(v1[126]);                     // ID3v1.1
    }

    /// <summary>APE v2 items: value size, flags, a NUL-terminated ASCII key, the value (UTF-8; several values NUL-separated).</summary>
    static void ReadApeItems(ReadOnlySpan<byte> items, long itemsAt, uint count, Builder b)
    {
        int pos = 0;
        for (uint i = 0; i < count && pos + 8 < items.Length; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(items[pos..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(items[(pos + 4)..]);
            int keyAt = pos + 8;
            int nul = items[keyAt..].IndexOf((byte)0);
            if (nul < 0) return;
            ReadOnlySpan<byte> key = items.Slice(keyAt, nul);
            int valueAt = keyAt + nul + 1;
            if (size > (uint)(items.Length - valueAt)) return;
            ReadOnlySpan<byte> value = items.Slice(valueAt, (int)size);
            pos = valueAt + (int)size;
            bool text = ((flags >> 1) & 3) == 0;
            if (text)
            {
                if (Ascii.EqualsIgnoreCase(key, "Title"u8)) b.SetTitle(Utf8(value));
                else if (Ascii.EqualsIgnoreCase(key, "Artist"u8))
                    foreach (Range r in value.Split((byte)0)) b.AddArtist(Encoding.UTF8.GetString(value[r]), 2);
                else if (Ascii.EqualsIgnoreCase(key, "Album"u8)) b.SetAlbum(Utf8(value));
                else if (Ascii.EqualsIgnoreCase(key, "Album Artist"u8) || Ascii.EqualsIgnoreCase(key, "AlbumArtist"u8)) b.SetAlbumArtist(Utf8(value));
                else if (Ascii.EqualsIgnoreCase(key, "Track"u8)) b.SetTrack(LeadingInt(Utf8(value)));
                else if (Ascii.EqualsIgnoreCase(key, "Disc"u8)) b.SetDisc(LeadingInt(Utf8(value)));
                else if (Ascii.EqualsIgnoreCase(key, "Year"u8)) b.SetYear(YearOf(Utf8(value)));
            }
            else if (((flags >> 1) & 3) == 1 && Ascii.EqualsIgnoreCase(key, "Cover Art (Front)"u8))
            {
                // A file name, NUL, then the image.
                int name = value.IndexOf((byte)0);
                if (name < 0 || name + 1 >= value.Length) continue;
                ReadOnlySpan<byte> fileName = value[..name];
                int dot = fileName.LastIndexOf((byte)'.');
                var mime = dot >= 0 ? MimeOf(fileName[(dot + 1)..]) : LocalCoverMime.Unknown;
                b.OfferCover(new LocalCover(itemsAt + valueAt + name + 1, (int)size - name - 1, mime, LocalCoverEncoding.Raw, 3));
            }
        }
    }

    // ── MPEG audio: the first frame and the length ───────────────────────────────────────────────────────────────────

    readonly record struct MpegHeader(int Version, int Layer, int BitrateKbps, int SampleRate, bool Mono, int FrameLength, int SamplesPerFrame);

    /// <summary>MPEG-1/2/2.5 frame header at <paramref name="h"/>[0..4]. Version 1, 2 or 25 (2.5); layer 1-3.</summary>
    static bool TryMpegHeader(ReadOnlySpan<byte> h, out MpegHeader header)
    {
        header = default;
        if (h.Length < 4 || h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return false;
        int versionBits = (h[1] >> 3) & 3, layerBits = (h[1] >> 1) & 3;
        int bitrateIndex = h[2] >> 4, rateIndex = (h[2] >> 2) & 3;
        if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || rateIndex == 3) return false;
        int version = versionBits switch { 3 => 1, 2 => 2, _ => 25 };
        int layer = 4 - layerBits;
        int kbps = Bitrate(version, layer, bitrateIndex);
        int rate = (rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 }) / (version switch { 1 => 1, 2 => 2, _ => 4 });
        int padding = (h[2] >> 1) & 1;
        int spf = layer == 1 ? 384 : layer == 2 || version == 1 ? 1152 : 576;
        int length = layer == 1 ? (12 * kbps * 1000 / rate + padding) * 4 : spf / 8 * kbps * 1000 / rate + padding;
        if (kbps <= 0 || rate <= 0 || length < 4) return false;
        header = new MpegHeader(version, layer, kbps, rate, (h[3] >> 6) == 3, length, spf);
        return true;
    }

    static int Bitrate(int version, int layer, int index)
    {
        ReadOnlySpan<short> v1l1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
        ReadOnlySpan<short> v1l2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
        ReadOnlySpan<short> v1l3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        ReadOnlySpan<short> v2l1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
        ReadOnlySpan<short> v2l23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        return version == 1
            ? (layer == 1 ? v1l1 : layer == 2 ? v1l2 : v1l3)[index]
            : (layer == 1 ? v2l1 : v2l23)[index];
    }

    /// <summary>The first MPEG frame at or after <paramref name="start"/>, confirmed by a second frame of the same version, layer and
    /// rate right after it (or the end of what was read). Looks at most <see cref="MaxJunkBytes"/> past the start.</summary>
    static bool FindMpegFrame(Bytes io, long start, out long frameAt, out MpegHeader header)
    {
        frameAt = 0;
        header = default;
        ReadOnlySpan<byte> s = io.UpTo(start, MaxJunkBytes + 2 * 2881 + 4);
        int limit = Math.Min(s.Length - 4, MaxJunkBytes);
        for (int i = 0; i <= limit; i++)
        {
            if (s[i] != 0xFF || !TryMpegHeader(s[i..], out MpegHeader h)) continue;
            int next = i + h.FrameLength;
            if (next + 4 <= s.Length)
            {
                if (!TryMpegHeader(s[next..], out MpegHeader h2) || h2.Version != h.Version || h2.Layer != h.Layer || h2.SampleRate != h.SampleRate)
                    continue;
            }
            else if (start + next < io.Length) continue;                               // cannot confirm: not a frame we trust
            frameAt = start + i;
            header = h;
            return true;
        }
        return false;
    }

    /// <summary>The length from a Xing/Info or VBRI frame count; else the bitrate estimate over the audio bytes. Reads the tail for
    /// ID3v1/APE when a tag field is still empty or the estimate needs the tail's size.</summary>
    static void ReadMp3Length(FileBytes io, long frameAt, MpegHeader h, Builder b)
    {
        b.SetStream(h.SampleRate, 0, h.Mono ? 1 : 2);
        long frames = 0, bytes = 0;
        ReadOnlySpan<byte> frame = io.UpTo(frameAt, Math.Min(h.FrameLength, 200) + 64);
        int side = h.Version == 1 ? (h.Mono ? 17 : 32) : (h.Mono ? 9 : 17);
        int xing = 4 + side;
        if (frame.Length >= xing + 16 && (frame.Slice(xing, 4).SequenceEqual("Xing"u8) || frame.Slice(xing, 4).SequenceEqual("Info"u8)))
        {
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(frame[(xing + 4)..]);
            int at = xing + 8;
            if ((flags & 1) != 0) { frames = BinaryPrimitives.ReadUInt32BigEndian(frame[at..]); at += 4; }
            if ((flags & 2) != 0 && frame.Length >= at + 4) bytes = BinaryPrimitives.ReadUInt32BigEndian(frame[at..]);
        }
        else if (frame.Length >= 36 + 18 && frame.Slice(36, 4).SequenceEqual("VBRI"u8))
        {
            bytes = BinaryPrimitives.ReadUInt32BigEndian(frame[(36 + 10)..]);
            frames = BinaryPrimitives.ReadUInt32BigEndian(frame[(36 + 14)..]);
        }

        bool exact = frames > 0;
        long tagBytes = exact && !NeedsFallback(b) ? 0 : ReadTail(io, frameAt, b);
        if (exact)
        {
            long ms = frames * h.SamplesPerFrame * 1000 / h.SampleRate;
            b.SetLength(ms);
            long audio = bytes > 0 ? bytes : io.Length - frameAt - tagBytes;
            if (ms > 0) b.BitrateKbps = (int)Math.Min(audio * 8 / ms, 10_000);
        }
        else
        {
            // The bitrate estimate wins over a TLEN frame (often wrong) but never over an exact length.
            long audio = io.Length - frameAt - tagBytes;
            AverageRate(io, frameAt, h, out long frameBytes, out long frameSamples);
            long ms = (long)(audio * (frameSamples * 1000.0 / h.SampleRate) / frameBytes);
            if (ms > 0 && (b.DurationMs == 0 || b.DurationEstimated)) { b.DurationMs = (int)Math.Min(ms, int.MaxValue); b.DurationEstimated = true; }
            b.BitrateKbps = (int)Math.Min(frameBytes * 8L * h.SampleRate / frameSamples / 1000, 10_000);
        }
    }

    /// <summary>Bytes and samples of the consecutive frames from <paramref name="frameAt"/> that the head window already
    /// holds (no extra read when it does), for the bitrate estimate. A VBR encoder's first frames are often its lowest rate
    /// over the opening silence (LAME writes 32 kbps there), so the first frame alone made a 91 s VBR song read as 7:48;
    /// the average over the window lands within about 10 %. For CBR it equals the first frame's rate.</summary>
    static void AverageRate(FileBytes io, long frameAt, MpegHeader first, out long bytes, out long samples)
    {
        int max = frameAt < WindowBytes ? (int)(WindowBytes - frameAt) : 16 * 1024;
        ReadOnlySpan<byte> s = io.UpTo(frameAt, max);
        bytes = samples = 0;
        int pos = 0;
        while (pos + 4 <= s.Length && TryMpegHeader(s[pos..], out MpegHeader f)
               && f.Version == first.Version && f.Layer == first.Layer && f.SampleRate == first.SampleRate && pos + f.FrameLength <= s.Length)
        {
            bytes += f.FrameLength;
            samples += f.SamplesPerFrame;
            pos += f.FrameLength;
        }
        if (bytes == 0 || samples == 0) { bytes = first.FrameLength; samples = first.SamplesPerFrame; }
    }
}
