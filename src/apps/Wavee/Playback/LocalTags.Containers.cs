// ── Playback/LocalTags.Containers.cs — FLAC, Ogg, MP4, ADTS, WAV, AIFF, WMA and EBML headers ──────────────────────────
//
// Each reader walks its container's headers through the byte window (LocalTags.cs) and seeks past what it does not
// want: FLAC blocks by their 24-bit length, Ogg pages by their lacing values, MP4 boxes and RIFF / AIFF / ASF chunks by
// their sizes. The Vorbis comments of FLAC and Ogg go through the one shared walk (`VorbisComments`); an Ogg comment
// packet is served page by page (`OggPacket`), so a base64 cover inside it is skipped without reading its pages' bodies.
// Byte layouts: research `tag-readers.md` §5.

using System.Buffers.Binary;
using System.Text;

namespace Wavee;

public static partial class LocalTags
{
    // ── the Vorbis comment fields ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The comment fields a row needs, from FLAC's VORBIS_COMMENT or an Ogg comment packet. Pictures
    /// (<c>METADATA_BLOCK_PICTURE</c>, base64 of a FLAC PICTURE block) are only located here and decoded after the walk.</summary>
    struct CommentFields(Builder b) : VorbisComments.IVisitor
    {
        public List<(long At, int Length)>? Pictures;

        public void Field(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, long valueAt)
        {
            if (Ascii.EqualsIgnoreCase(key, "METADATA_BLOCK_PICTURE"u8)) { (Pictures ??= []).Add((valueAt, value.Length)); return; }
            if (value.Length > MaxTextBytes) return;
            if (Ascii.EqualsIgnoreCase(key, "TITLE"u8)) b.SetTitle(Encoding.UTF8.GetString(value));
            else if (Ascii.EqualsIgnoreCase(key, "ARTIST"u8)) b.AddArtist(Encoding.UTF8.GetString(value), 0);
            else if (Ascii.EqualsIgnoreCase(key, "ALBUM"u8)) b.SetAlbum(Encoding.UTF8.GetString(value));
            else if (Ascii.EqualsIgnoreCase(key, "ALBUMARTIST"u8) || Ascii.EqualsIgnoreCase(key, "ALBUM ARTIST"u8)
                     || Ascii.EqualsIgnoreCase(key, "ALBUM_ARTIST"u8)) b.SetAlbumArtist(Encoding.UTF8.GetString(value));
            else if (Ascii.EqualsIgnoreCase(key, "TRACKNUMBER"u8)) b.SetTrack(LeadingInt(Encoding.UTF8.GetString(value)));
            else if (Ascii.EqualsIgnoreCase(key, "DISCNUMBER"u8)) b.SetDisc(LeadingInt(Encoding.UTF8.GetString(value)));
            else if (Ascii.EqualsIgnoreCase(key, "ISRC"u8)) b.SetIsrc(Encoding.UTF8.GetString(value));
            else if (Ascii.EqualsIgnoreCase(key, "DATE"u8) || Ascii.EqualsIgnoreCase(key, "YEAR"u8)) b.SetYear(YearOf(Encoding.UTF8.GetString(value)));
        }

        public void LargeField(ReadOnlySpan<byte> key, long valueAt, int valueLength)
        {
            if (Ascii.EqualsIgnoreCase(key, "METADATA_BLOCK_PICTURE"u8)) (Pictures ??= []).Add((valueAt, valueLength));
        }
    }

    /// <summary>Decode the start of each base64 picture (picture type, mime) and offer its location. <paramref name="fileOffset"/>
    /// maps a block offset to the file.</summary>
    static void OfferBase64Pictures<TSource>(ref TSource source, List<(long At, int Length)>? pictures, Func<long, long> fileOffset, Builder b)
        where TSource : VorbisComments.ISource, allows ref struct
    {
        if (pictures is null) return;
        Span<byte> decoded = stackalloc byte[96];
        for (int i = 0; i < pictures.Count && i < 8; i++)
        {
            var (at, length) = pictures[i];
            int peek = Math.Min(length, 128) & ~3;
            if (peek < 12 || !source.TryRead(at, peek, out ReadOnlySpan<byte> text)) continue;
            if (System.Buffers.Text.Base64.DecodeFromUtf8(text, decoded, out _, out int written, isFinalBlock: false) is not
                (System.Buffers.OperationStatus.Done or System.Buffers.OperationStatus.NeedMoreData) || written < 8) continue;
            uint type = BinaryPrimitives.ReadUInt32BigEndian(decoded);
            uint mimeLen = BinaryPrimitives.ReadUInt32BigEndian(decoded[4..]);
            var mime = mimeLen <= (uint)(written - 8) ? MimeOf(decoded.Slice(8, (int)mimeLen)) : LocalCoverMime.Unknown;
            b.OfferCover(new LocalCover(fileOffset(at), length, mime, LocalCoverEncoding.Base64, (byte)Math.Min(type, 255u)));
        }
    }

    /// <summary>A contiguous comment block in the file.</summary>
    readonly ref struct BlockSource(Bytes io, long at, long length) : VorbisComments.ISource
    {
        public bool TryRead(long offset, int count, out ReadOnlySpan<byte> bytes)
        {
            bytes = default;
            if (offset < 0 || offset + count > length) return false;
            return io.TryGet(at + offset, count, out bytes);
        }
    }

    // ── FLAC ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>fLaC</c> at <paramref name="start"/>, then metadata blocks (a 4-byte header: last flag, type, 24-bit length).
    /// STREAMINFO gives the length, VORBIS_COMMENT the tags, PICTURE the cover's location; the rest is skipped by length.</summary>
    static void ReadFlac(Bytes io, long start, Builder b)
    {
        long pos = start + 4;
        for (int guard = 0; guard < 4096; guard++)
        {
            if (!io.TryGet(pos, 4, out ReadOnlySpan<byte> h, readAhead: 8192)) return;
            bool last = (h[0] & 0x80) != 0;
            int type = h[0] & 0x7F;
            int len = (h[1] << 16) | (h[2] << 8) | h[3];
            long body = pos + 4;
            if (type == 127) return;
            try
            {
                if (type == 0) ReadStreamInfo(io, body, b);
                else if (type == 4) ReadCommentBlock(io, body, len, b);
                else if (type == 6) ReadFlacPicture(io, body, len, b);
            }
            catch (Exception) { /* best attempt */ }
            pos = body + len;
            if (last) break;
        }
        if (b.DurationMs > 0) b.BitrateKbps = (int)Math.Min((io.Length - pos) * 8 / b.DurationMs, 100_000);
    }

    static void ReadStreamInfo(Bytes io, long body, Builder b)
    {
        if (!io.TryGet(body, 18, out ReadOnlySpan<byte> si)) return;
        int rate = StreamInfoFormat(si, b);
        long total = ((long)(si[13] & 0x0F) << 32) | BinaryPrimitives.ReadUInt32BigEndian(si[14..]);
        if (rate > 0 && total > 0) b.SetLength(total * 1000 / rate);
    }

    /// <summary>A FLAC STREAMINFO body's packed rate (20 bits), channels (3 bits, minus one) and sample size (5 bits, minus one):
    /// recorded on <paramref name="b"/>; the rate is returned for the length.</summary>
    static int StreamInfoFormat(ReadOnlySpan<byte> si, Builder b)
    {
        int rate = (si[10] << 12) | (si[11] << 4) | (si[12] >> 4);
        int channels = ((si[12] >> 1) & 0x07) + 1;
        int bits = (((si[12] & 0x01) << 4) | (si[13] >> 4)) + 1;
        b.SetStream(rate, bits, channels);
        return rate;
    }

    static void ReadCommentBlock(Bytes io, long at, int length, Builder b)
    {
        var source = new BlockSource(io, at, length);
        var fields = new CommentFields(b);
        VorbisComments.ForEach(ref source, ref fields);
        OfferBase64Pictures(ref source, fields.Pictures, offset => at + offset, b);
    }

    /// <summary>A PICTURE block: type, mime length + mime, description length + description, width, height, depth, colours, then the
    /// data length and the data. Only the part before the data is read.</summary>
    static void ReadFlacPicture(Bytes io, long body, int len, Builder b)
    {
        ReadOnlySpan<byte> p = io.UpTo(body, Math.Min(len, 4096));
        if (p.Length < 32) return;
        uint type = BinaryPrimitives.ReadUInt32BigEndian(p);
        uint mimeLen = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
        if (mimeLen > (uint)(p.Length - 8)) return;
        var mime = MimeOf(p.Slice(8, (int)mimeLen));
        int at = 8 + (int)mimeLen;
        if (at + 4 > p.Length) return;
        uint descLen = BinaryPrimitives.ReadUInt32BigEndian(p[at..]);
        if (descLen > (uint)(p.Length - at - 4 - 20)) return;
        at += 4 + (int)descLen + 16;
        uint dataLen = BinaryPrimitives.ReadUInt32BigEndian(p[at..]);
        at += 4;
        if (dataLen == 0 || dataLen > (uint)(len - at)) return;
        b.OfferCover(new LocalCover(body + at, (int)dataLen, mime, LocalCoverEncoding.Raw, (byte)Math.Min(type, 255u)));
    }

    // ── Ogg ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ogg: page 1 names the codec (Vorbis, Opus, FLAC), page 2 starts the comment packet, the last page's granule
    /// position gives the length (Opus minus its pre-skip, at 48 kHz).</summary>
    static bool ReadOgg(FileBytes io, long start, Builder b, out Spotify.Audio.Format format, out string why)
    {
        format = Spotify.Audio.Format.Unknown;
        why = "an Ogg file carrying an unknown codec";
        if (!OggPage(io, start, out uint serial, out long body, out int bodyLen, out int firstPacket, out _)) return true;
        ReadOnlySpan<byte> id = io.UpTo(body, Math.Min(firstPacket, 64));
        int rate, preSkip = 0;
        ReadOnlySpan<byte> commentMagic;
        if (id.Length >= 16 && id[0] == 1 && id[1..7].SequenceEqual("vorbis"u8))
        {
            rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(id[12..]);
            b.SetStream(rate, 0, id[11]);
            format = Spotify.Audio.Format.OggVorbis320;
            why = "";
            commentMagic = "\u0003vorbis"u8;
        }
        else if (id.Length >= 12 && id[..8].SequenceEqual("OpusHead"u8))
        {
            preSkip = BinaryPrimitives.ReadUInt16LittleEndian(id[10..]);
            rate = 48000;                                                           // Opus always decodes at 48 kHz
            b.SetStream(rate, 0, id[9]);
            why = "an Ogg file carrying Opus";
            commentMagic = "OpusTags"u8;
        }
        else if (id.Length >= 9 + 4 + 4 + 18 && id[0] == 0x7F && id[1..5].SequenceEqual("FLAC"u8))
        {
            rate = StreamInfoFormat(id[17..], b);
            why = "an Ogg file carrying FLAC";
            commentMagic = default;                                                 // a FLAC block header (type 4) instead
        }
        else return true;

        // The comment packet (page 2 on).
        var packet = new OggPacket(io, body + bodyLen, serial);
        int prefix = commentMagic.IsEmpty ? 4 : commentMagic.Length;
        if (packet.TryRead(0, prefix, out ReadOnlySpan<byte> magic)
            && (commentMagic.IsEmpty ? (magic[0] & 0x7F) == 4 : magic.SequenceEqual(commentMagic)))
        {
            packet.Base = prefix;
            var fields = new CommentFields(b);
            try
            {
                VorbisComments.ForEach(ref packet, ref fields);
                OfferBase64Pictures(ref packet, fields.Pictures, packet.FileOffset, b);
            }
            catch (Exception) { /* best attempt */ }
        }

        // The length: the last page of this stream, found backwards in the file's last 64 KiB (a page is at most 65,307 bytes).
        long tailAt = Math.Max(0, io.Length - WindowBytes);
        ReadOnlySpan<byte> tail = io.UpTo(tailAt, (int)(io.Length - tailAt));
        for (int i = tail.Length - 27; i >= 0; i--)
        {
            if (tail[i] != (byte)'O' || !tail.Slice(i, 4).SequenceEqual("OggS"u8) || tail[i + 4] != 0) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail[(i + 14)..]) != serial) continue;
            long granule = BinaryPrimitives.ReadInt64LittleEndian(tail[(i + 6)..]);
            if (granule <= 0) continue;
            if (rate > 0) b.SetLength(Math.Max(0, granule - preSkip) * 1000 / rate);
            break;
        }
        if (b.DurationMs > 0) b.BitrateKbps = (int)Math.Min(io.Length * 8 / b.DurationMs, 100_000);
        return true;
    }

    /// <summary>One page header: its stream, where its body starts and how long it is, and the first packet's length on it.</summary>
    static bool OggPage(Bytes io, long at, out uint serial, out long body, out int bodyLen, out int firstPacket, out bool continued)
    {
        serial = 0; body = 0; bodyLen = 0; firstPacket = 0; continued = false;
        if (!io.TryGet(at, 27, out ReadOnlySpan<byte> h, readAhead: 1024) || !h[..4].SequenceEqual("OggS"u8)) return false;
        int segments = h[26];
        continued = (h[5] & 1) != 0;
        serial = BinaryPrimitives.ReadUInt32LittleEndian(h[14..]);
        if (!io.TryGet(at, 27 + segments, out h, readAhead: 1024)) return false;
        bool open = true;
        for (int i = 0; i < segments; i++)
        {
            int lace = h[27 + i];
            bodyLen += lace;
            if (open) { firstPacket += lace; if (lace < 255) open = false; }
        }
        body = at + 27 + segments;
        return true;
    }

    /// <summary>An Ogg packet that may span pages, served by packet offset. Page headers are read as the walk needs them
    /// (header-sized reads), so a field skipped by length costs the headers of its pages and none of their bodies.</summary>
    sealed class OggPacket(Bytes io, long firstPage, uint serial) : VorbisComments.ISource
    {
        readonly List<(long File, long Logical, int Length)> _extents = [];
        long _nextPage = firstPage, _known;
        bool _ended;
        byte[]? _scratch;

        /// <summary>Packet bytes before the comment block (the <c>"\x03vorbis"</c> / <c>OpusTags</c> magic).</summary>
        public long Base;

        public bool TryRead(long at, int length, out ReadOnlySpan<byte> bytes)
        {
            bytes = default;
            at += Base;
            if (at < 0 || length < 0) return false;
            while (_known < at + length) if (!Extend()) return false;
            int i = Find(at);
            var e = _extents[i];
            if (at + length <= e.Logical + e.Length) return io.TryGet(e.File + (at - e.Logical), length, out bytes);
            _scratch ??= new byte[VorbisComments.MaxInlineField + 256];
            if (length > _scratch.Length) return false;
            int copied = 0;
            while (copied < length && i < _extents.Count)
            {
                e = _extents[i++];
                long off = at + copied - e.Logical;
                int n = (int)Math.Min(e.Length - off, length - copied);
                if (!io.TryGet(e.File + off, n, out ReadOnlySpan<byte> piece)) return false;
                piece.CopyTo(_scratch.AsSpan(copied));
                copied += n;
            }
            bytes = _scratch.AsSpan(0, length);
            return copied == length;
        }

        /// <summary>Where packet offset <paramref name="at"/> (comment-block relative) is in the file.</summary>
        public long FileOffset(long at)
        {
            at += Base;
            if (_extents.Count == 0) return 0;
            var e = _extents[Find(at)];
            return e.File + (at - e.Logical);
        }

        int Find(long at)
        {
            int lo = 0, hi = _extents.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_extents[mid].Logical <= at) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Map the next page of this stream onto the packet. False when the packet has ended or the pages stop.</summary>
        bool Extend()
        {
            for (int guard = 0; !_ended && guard < 64; guard++)
            {
                long at = _nextPage;
                if (!io.TryGet(at, 27, out ReadOnlySpan<byte> h, readAhead: 1024) || !h[..4].SequenceEqual("OggS"u8)) break;
                int segments = h[26];
                uint pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(h[14..]);
                if (!io.TryGet(at, 27 + segments, out h, readAhead: 1024)) break;
                int bodyLen = 0, take = 0;
                bool end = false;
                for (int i = 0; i < segments; i++)
                {
                    int lace = h[27 + i];
                    bodyLen += lace;
                    if (!end) { take += lace; if (lace < 255) end = true; }
                }
                long body = at + 27 + segments;
                _nextPage = body + bodyLen;
                if (pageSerial != serial) continue;                                  // another stream's page, interleaved
                if (take > 0) { _extents.Add((body, _known, take)); _known += take; }
                if (end) _ended = true;
                return true;
            }
            _ended = true;
            return false;
        }
    }

    // ── MP4 / M4A ────────────────────────────────────────────────────────────────────────────────────────────────────

    const uint Moov = 0x6D6F6F76, Moof = 0x6D6F6F66, Mvhd = 0x6D766864, Mvex = 0x6D766578, Trak = 0x7472616B, Tkhd = 0x746B6864,
        Mdia = 0x6D646961, Mdhd = 0x6D646864, Hdlr = 0x68646C72, Minf = 0x6D696E66, Stbl = 0x7374626C, Stsd = 0x73747364,
        Udta = 0x75647461, Meta = 0x6D657461, Ilst = 0x696C7374, Data = 0x64617461, Soun = 0x736F756E, Vide = 0x76696465,
        Mp4a = 0x6D703461, Esds = 0x65736473, Wave = 0x77617665, Enca = 0x656E6361, Alac = 0x616C6163, Ac3 = 0x61632D33,
        Ec3 = 0x65632D33, Opus = 0x4F707573, DotMp3 = 0x2E6D7033,
        INam = 0xA96E616D, IArt = 0xA9415254, IAlb = 0xA9616C62, AArt = 0x61415254, IDay = 0xA9646179, Trkn = 0x74726B6E,
        Disk = 0x6469736B, Covr = 0x636F7672;

    /// <summary>What the tracks of one <c>moov</c> said.</summary>
    sealed class Mp4Tracks
    {
        public bool HasAudio, HasVideo, Fragmented;
        public long AudioMs, VideoMs, MovieMs;
        public uint Codec;
        public int Oti, Width, Height, Rate, Bits, Channels;
    }

    /// <summary>Top-level boxes to <c>moov</c> (a seek past <c>mdat</c> when it comes first), then only <c>trak/tkhd</c>,
    /// <c>trak/mdia/{hdlr,mdhd,minf/stbl/stsd}</c> and <c>udta/meta/ilst</c>; the sample tables and <c>covr</c> data are skipped.</summary>
    static bool ReadMp4(FileBytes io, Builder b, out Spotify.Audio.Format format, out string why)
    {
        format = Spotify.Audio.Format.Unknown;
        why = "";
        var t = new Mp4Tracks();
        bool moov = false;
        long pos = 0;
        for (int guard = 0; guard < 4096 && pos + 8 <= io.Length; guard++)
        {
            if (!Box(io, pos, io.Length, out long size, out uint type, out int hdr)) break;
            if (type == Moov) { moov = true; ReadMoov(io, pos + hdr, pos + size, t, b); }
            else if (type == Moof) t.Fragmented = true;
            if (moov) break;
            pos += size;
        }
        if (!moov) { why = "an MP4 with no movie header (moov)"; return true; }

        b.Kind = t.HasVideo ? LocalKind.Video : LocalKind.Audio;
        b.HasAudio = t.HasAudio;
        b.VideoWidth = t.Width;
        b.VideoHeight = t.Height;
        if (t.HasAudio) b.SetStream(t.Rate, t.Codec == Alac ? t.Bits : 0, t.Channels);   // only ALAC is lossless here: AAC's sample size is nominal
        long ms = t.HasAudio ? t.AudioMs : t.HasVideo ? t.VideoMs : 0;
        if (ms <= 0) ms = t.MovieMs;
        b.SetLength(ms);
        if (b.DurationMs > 0) b.BitrateKbps = (int)Math.Min(io.Length * 8 / b.DurationMs, 100_000);

        if (!t.HasAudio) why = t.HasVideo ? "a video with no sound track" : "an MP4 with no audio track";
        else if (t.Fragmented) why = "a fragmented MP4 (no sample table)";
        else if (t.Codec == Mp4a && t.Oti is 0x40 or 0x66 or 0x67 or 0x68) format = Spotify.Audio.Format.Aac;
        else why = t.Codec switch
        {
            Mp4a => t.Oti is 0x69 or 0x6B ? "MP3 audio in an MP4" : "an unsupported audio codec in an MP4",
            Enca => "encrypted (DRM-protected) audio",
            Alac => "Apple Lossless (ALAC) audio",
            Ac3 or Ec3 => "Dolby Digital audio",
            Opus => "Opus audio in an MP4",
            DotMp3 => "MP3 audio in an MP4",
            _ => "an unsupported audio codec in an MP4",
        };
        return true;
    }

    /// <summary>A box header at <paramref name="at"/>: its size (64-bit when the 32-bit size is 1; to <paramref name="end"/> when 0)
    /// and type. A size past <paramref name="end"/> is cut to it (a truncated file still yields what it holds).</summary>
    static bool Box(Bytes io, long at, long end, out long size, out uint type, out int hdr)
    {
        size = 0; type = 0; hdr = 8;
        if (at + 8 > end || !io.TryGet(at, 8, out ReadOnlySpan<byte> h)) return false;
        size = BinaryPrimitives.ReadUInt32BigEndian(h);
        type = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
        if (size == 1)
        {
            if (at + 16 > end || !io.TryGet(at + 8, 8, out ReadOnlySpan<byte> big)) return false;
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(big);
            hdr = 16;
        }
        else if (size == 0) size = end - at;
        if (size < hdr) return false;
        if (at + size > end) size = end - at;
        return true;
    }

    static void ReadMoov(Bytes io, long start, long end, Mp4Tracks t, Builder b)
    {
        for (long pos = start; Box(io, pos, end, out long size, out uint type, out int hdr); pos += size)
        {
            long body = pos + hdr;
            if (type == Mvhd && io.TryGet(body, 32, out ReadOnlySpan<byte> mvhd)) t.MovieMs = HeaderMs(mvhd);
            else if (type == Trak) ReadTrak(io, body, pos + size, t);
            else if (type == Mvex) t.Fragmented = true;
            else if (type == Udta)
            {
                for (long u = body; Box(io, u, pos + size, out long us, out uint ut, out int uh); u += us)
                    if (ut == Meta) ReadMeta(io, u + uh, u + us, b);
            }
            else if (type == Meta) ReadMeta(io, body, pos + size, b);
        }
    }

    /// <summary>mvhd / mdhd: version, then (v0) 32-bit or (v1) 64-bit times, timescale, duration.</summary>
    static long HeaderMs(ReadOnlySpan<byte> h)
    {
        bool v1 = h[0] == 1;
        long scale = v1 ? BinaryPrimitives.ReadUInt32BigEndian(h[20..]) : BinaryPrimitives.ReadUInt32BigEndian(h[12..]);
        long units = v1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(h[24..]) : BinaryPrimitives.ReadUInt32BigEndian(h[16..]);
        return scale > 0 && units > 0 && units != uint.MaxValue && units < long.MaxValue / 1000 ? units * 1000 / scale : 0;
    }

    static void ReadTrak(Bytes io, long start, long end, Mp4Tracks t)
    {
        uint handler = 0, codec = 0;
        int oti = 0, width = 0, height = 0;
        (int Rate, int Bits, int Channels) sound = default;
        long ms = 0;
        for (long pos = start; Box(io, pos, end, out long size, out uint type, out int hdr); pos += size)
        {
            long body = pos + hdr;
            if (type == Tkhd && io.TryGet(body, Math.Min((int)(size - hdr), 96), out ReadOnlySpan<byte> tkhd) && tkhd.Length >= 84)
            {
                int at = tkhd[0] == 1 ? 88 : 76;
                if (tkhd.Length >= at + 8)
                {
                    width = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[at..]) >> 16);
                    height = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[(at + 4)..]) >> 16);
                }
            }
            else if (type == Mdia)
            {
                for (long m = body; Box(io, m, pos + size, out long ms2, out uint mt, out int mh); m += ms2)
                {
                    long mb = m + mh;
                    if (mt == Hdlr && io.TryGet(mb, 12, out ReadOnlySpan<byte> hdlr)) handler = BinaryPrimitives.ReadUInt32BigEndian(hdlr[8..]);
                    else if (mt == Mdhd && io.TryGet(mb, 32, out ReadOnlySpan<byte> mdhd)) ms = HeaderMs(mdhd);
                    else if (mt == Minf) ReadSampleEntry(io, mb, m + ms2, out codec, out oti, out sound, ref width, ref height);
                }
            }
        }
        if (handler == Soun && !t.HasAudio)
        {
            t.HasAudio = true;
            t.AudioMs = ms;
            t.Codec = codec;
            t.Oti = oti;
            (t.Rate, t.Bits, t.Channels) = sound;
        }
        else if (handler == Vide && !t.HasVideo)
        {
            t.HasVideo = true;
            t.VideoMs = ms;
            t.Width = width;
            t.Height = height;
        }
    }

    /// <summary>minf/stbl/stsd's first entry: its format, an <c>mp4a</c>'s object type, a visual entry's size when tkhd had none.</summary>
    static void ReadSampleEntry(Bytes io, long start, long end, out uint codec, out int oti, out (int Rate, int Bits, int Channels) sound,
                                ref int width, ref int height)
    {
        codec = 0;
        oti = 0;
        sound = default;
        for (long pos = start; Box(io, pos, end, out long size, out uint type, out int hdr); pos += size)
        {
            if (type != Stbl) continue;
            for (long s = pos + hdr; Box(io, s, pos + size, out long ss, out uint st, out int sh); s += ss)
            {
                if (st != Stsd) continue;
                ReadOnlySpan<byte> stsd = io.UpTo(s + sh, (int)Math.Min(ss - sh, 4096));
                if (stsd.Length < 16) return;
                uint entrySize = BinaryPrimitives.ReadUInt32BigEndian(stsd[8..]);
                codec = BinaryPrimitives.ReadUInt32BigEndian(stsd[12..]);
                ReadOnlySpan<byte> entry = stsd[16..(int)Math.Min(stsd.Length, 8 + entrySize)];
                if (entry.Length >= 28 && (width == 0 || height == 0) && codec != Mp4a)
                {
                    width = BinaryPrimitives.ReadUInt16BigEndian(entry[24..]);       // VisualSampleEntry width / height
                    height = BinaryPrimitives.ReadUInt16BigEndian(entry[26..]);
                }
                if (codec == Mp4a && entry.Length >= 28) oti = EsdsObjectType(entry);
                if (entry.Length >= 28) sound = SoundFormat(codec, entry);                  // kept only for the sound track
                return;
            }
        }
    }

    /// <summary>An AudioSampleEntry's channel count, sample size and 16.16 rate (QuickTime sound version 2 keeps a 64-bit float
    /// rate further on). ALAC's own cookie (an <c>alac</c> child box) carries the real bit depth and a rate past 65,535 Hz.</summary>
    static (int Rate, int Bits, int Channels) SoundFormat(uint codec, ReadOnlySpan<byte> entry)
    {
        int version = BinaryPrimitives.ReadUInt16BigEndian(entry[8..]);
        int channels = BinaryPrimitives.ReadUInt16BigEndian(entry[16..]);
        int bits = BinaryPrimitives.ReadUInt16BigEndian(entry[18..]);
        int rate = (int)(BinaryPrimitives.ReadUInt32BigEndian(entry[24..]) >> 16);
        if (version == 2 && entry.Length >= 64)
        {
            rate = (int)BinaryPrimitives.ReadDoubleBigEndian(entry[32..]);
            channels = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[40..]);
            bits = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[48..]);
        }
        if (codec == Alac)
        {
            int at = 28 + (version == 1 ? 16 : version == 2 ? 36 : 0);
            while (at + 8 <= entry.Length)
            {
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[at..]);
                if (size < 8 || at + size > entry.Length) break;
                if (BinaryPrimitives.ReadUInt32BigEndian(entry[(at + 4)..]) == Alac && size >= 12 + 24)
                {
                    ReadOnlySpan<byte> cookie = entry.Slice(at + 12, size - 12);          // after the full-box version and flags
                    bits = cookie[5];
                    channels = cookie[9];
                    rate = (int)BinaryPrimitives.ReadUInt32BigEndian(cookie[20..]);
                    break;
                }
                at += size;
            }
        }
        return (rate, bits, channels);
    }

    /// <summary>An <c>mp4a</c> entry's <c>esds</c> (directly, or inside QuickTime's <c>wave</c>): the DecoderConfig object type.</summary>
    static int EsdsObjectType(ReadOnlySpan<byte> entry)
    {
        int version = BinaryPrimitives.ReadUInt16BigEndian(entry[8..]);
        int at = 28 + (version == 1 ? 16 : version == 2 ? 36 : 0);
        for (int depth = 0; depth < 2; depth++)
        {
            while (at + 8 <= entry.Length)
            {
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[at..]);
                uint type = BinaryPrimitives.ReadUInt32BigEndian(entry[(at + 4)..]);
                if (size < 8 || at + size > entry.Length) return 0;
                if (type == Esds) return DecoderObjectType(entry.Slice(at + 12, size - 12));
                if (type == Wave) { entry = entry.Slice(at + 8, size - 8); at = 0; break; }
                at += size;
            }
        }
        return 0;

        static int DecoderObjectType(ReadOnlySpan<byte> d)
        {
            int p = 0;
            if (!Descriptor(d, ref p, out int tag, out _) || tag != 3 || p + 3 > d.Length) return 0;
            byte flags = d[p + 2];
            p += 3;
            if ((flags & 0x80) != 0) p += 2;
            if ((flags & 0x40) != 0 && p < d.Length) p += 1 + d[p];
            if ((flags & 0x20) != 0) p += 2;
            if (!Descriptor(d, ref p, out tag, out _) || tag != 4 || p >= d.Length) return 0;
            return d[p];
        }

        static bool Descriptor(ReadOnlySpan<byte> d, ref int p, out int tag, out int len)
        {
            tag = len = 0;
            if (p >= d.Length) return false;
            tag = d[p++];
            for (int i = 0; i < 4 && p < d.Length; i++)
            {
                byte c = d[p++];
                len = (len << 7) | (c & 0x7F);
                if ((c & 0x80) == 0) break;
            }
            return true;
        }
    }

    /// <summary><c>meta</c> (a full box; QuickTime files omit the version, which shows as <c>hdlr</c> right at the start), then
    /// <c>ilst</c>: each item holds <c>data</c> boxes (type, locale, value).</summary>
    static void ReadMeta(Bytes io, long start, long end, Builder b)
    {
        if (io.TryGet(start, 8, out ReadOnlySpan<byte> peek) && BinaryPrimitives.ReadUInt32BigEndian(peek[4..]) != Hdlr) start += 4;
        for (long pos = start; Box(io, pos, end, out long size, out uint type, out int hdr); pos += size)
        {
            if (type != Ilst) continue;
            for (long item = pos + hdr; Box(io, item, pos + size, out long isz, out uint itype, out int ih); item += isz)
            {
                try { ReadIlstItem(io, itype, item + ih, item + isz, b); }
                catch (Exception) { /* best attempt */ }
            }
        }
    }

    static void ReadIlstItem(Bytes io, uint item, long start, long end, Builder b)
    {
        if (item is not (INam or IArt or IAlb or AArt or IDay or Trkn or Disk or Covr)) return;
        for (long pos = start; Box(io, pos, end, out long size, out uint type, out int hdr); pos += size)
        {
            if (type != Data || size < hdr + 8) continue;
            if (!io.TryGet(pos + hdr, 8, out ReadOnlySpan<byte> dh)) return;
            int kind = (int)(BinaryPrimitives.ReadUInt32BigEndian(dh) & 0xFFFFFF);
            long valueAt = pos + hdr + 8;
            int valueLen = (int)Math.Min(size - hdr - 8, int.MaxValue);
            if (item == Covr)
            {
                var mime = kind switch { 13 => LocalCoverMime.Jpeg, 14 => LocalCoverMime.Png, 27 => LocalCoverMime.Bmp, _ => LocalCoverMime.Unknown };
                b.OfferCover(new LocalCover(valueAt, valueLen, mime, LocalCoverEncoding.Raw, 3));
                return;
            }
            if (item is Trkn or Disk)
            {
                if (valueLen >= 4 && io.TryGet(valueAt, 4, out ReadOnlySpan<byte> n))
                {
                    int number = BinaryPrimitives.ReadUInt16BigEndian(n[2..]);
                    if (item == Trkn) b.SetTrack(number); else b.SetDisc(number);
                }
                return;
            }
            if (valueLen > MaxTextBytes || !io.TryGet(valueAt, valueLen, out ReadOnlySpan<byte> v)) return;
            string text = kind == 2 ? Utf16(v, bigEndian: true) : Encoding.UTF8.GetString(v);
            switch (item)
            {
                case INam: b.SetTitle(text); return;
                case IArt: b.AddArtist(text, 4); break;                                // several data boxes: several artists
                case IAlb: b.SetAlbum(text); return;
                case AArt: b.SetAlbumArtist(text); return;
                case IDay: b.SetYear(YearOf(text)); return;
            }
        }
    }

    // ── ADTS ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Raw AAC has no length header: the average frame size over the frames in the window, times the frame count the
    /// file's size implies. An estimate.</summary>
    static void ReadAdtsLength(Bytes io, long start, Builder b)
    {
        ReadOnlySpan<byte> s = io.UpTo(start, WindowBytes);
        int pos = 0, frames = 0, rate = 0, channels = 0;
        long bytes = 0, blocks = 0;
        while (frames < 256 && pos + Modules.Adts.MinHeaderBytes <= s.Length && Modules.Adts.TryParseHeader(s[pos..], out Modules.AdtsHeader h))
        {
            if (pos + h.FrameLength > s.Length) break;
            frames++;
            bytes += h.FrameLength;
            blocks += h.RawDataBlocks;
            rate = h.SampleRate;
            channels = h.ChannelConfiguration;
            pos += h.FrameLength;
        }
        if (frames == 0 || rate == 0) return;
        b.SetStream(rate, 0, channels == 7 ? 8 : channels);                       // channel configuration 7 is 7.1
        double avgBytes = (double)bytes / frames, avgSamples = 1024.0 * blocks / frames;
        double ms = (io.Length - start) / avgBytes * avgSamples * 1000 / rate;
        b.SetEstimate((long)ms);
        b.BitrateKbps = (int)(avgBytes * 8 * rate / avgSamples / 1000);
    }

    // ── WAV / AIFF ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>RIFF / RF64 chunks: <c>fmt </c> (byte rate), <c>data</c> (size; RF64's from <c>ds64</c>), an <c>id3 </c> chunk (read first:
    /// it is the richer tag), then <c>LIST/INFO</c> fills what it left.</summary>
    static void ReadWav(Bytes io, Builder b)
    {
        long pos = 12, data = 0, ds64 = 0, listAt = -1;
        int byteRate = 0, listLen = 0;
        for (int guard = 0; guard < 1024 && pos + 8 <= io.Length; guard++)
        {
            if (!io.TryGet(pos, 8, out ReadOnlySpan<byte> h, readAhead: 4096)) break;
            uint id = BinaryPrimitives.ReadUInt32BigEndian(h);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            long body = pos + 8;
            switch (id)
            {
                case 0x64733634 when io.TryGet(body, 16, out ReadOnlySpan<byte> d): ds64 = BinaryPrimitives.ReadInt64LittleEndian(d[8..]); break;   // ds64
                case 0x666D7420 when io.TryGet(body, 16, out ReadOnlySpan<byte> f):                                                     // "fmt "
                    byteRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[8..]);
                    b.SetStream((int)BinaryPrimitives.ReadUInt32LittleEndian(f[4..]), BinaryPrimitives.ReadUInt16LittleEndian(f[14..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(f[2..]));
                    break;
                case 0x64617461:                                                                                                       // data
                    data = size == uint.MaxValue && ds64 > 0 ? ds64 : size;
                    size = Math.Min(data, io.Length - body);
                    break;
                case 0x4C495354: listAt = body; listLen = (int)Math.Min(size, WindowBytes); break;                                   // LIST
                case 0x69643320 or 0x49443320: ReadId3v2(io, body, b); break;                                                       // "id3 " / "ID3 "
            }
            pos = body + size + (size & 1);
        }
        if (listAt >= 0 && io.TryGet(listAt, listLen, out ReadOnlySpan<byte> list) && list.Length >= 4 && list[..4].SequenceEqual("INFO"u8))
            ReadRiffInfo(list[4..], b);
        if (byteRate > 0 && data > 0)
        {
            b.SetLength(data * 1000 / byteRate);
            b.BitrateKbps = byteRate * 8 / 1000;
        }
    }

    static void ReadRiffInfo(ReadOnlySpan<byte> info, Builder b)
    {
        int pos = 0;
        while (pos + 8 <= info.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32BigEndian(info[pos..]);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(pos + 4)..]);
            if (size < 0 || pos + 8 + size > info.Length) return;
            ReadOnlySpan<byte> v = info.Slice(pos + 8, size);
            string text = Text8(v, b);
            switch (id)
            {
                case 0x494E414D: b.SetTitle(text); break;                           // INAM
                case 0x49415254: b.AddArtist(text, 6); break;                       // IART
                case 0x49505244: b.SetAlbum(text); break;                           // IPRD
                case 0x4954524B or 0x49505254: b.SetTrack(LeadingInt(text)); break; // ITRK / IPRT
                case 0x49435244: b.SetYear(YearOf(text)); break;                    // ICRD
            }
            pos += 8 + size + (size & 1);
        }
    }

    /// <summary>8-bit text of unknown charset (RIFF INFO, AIFF NAME/AUTH): UTF-8 when it is valid UTF-8, else Windows-1252.</summary>
    static string Text8(ReadOnlySpan<byte> v, Builder b)
    {
        int nul = v.IndexOf((byte)0);
        if (nul >= 0) v = v[..nul];
        return Utf8Valid(v) ? Encoding.UTF8.GetString(v) : Windows1252(v, b);

        static bool Utf8Valid(ReadOnlySpan<byte> s) => System.Text.Unicode.Utf8.IsValid(s);
    }

    /// <summary>AIFF / AIFC (big-endian): <c>COMM</c> (frames, 80-bit extended rate), an <c>ID3 </c> chunk, then <c>NAME</c> / <c>AUTH</c>.</summary>
    static void ReadAiff(Bytes io, Builder b)
    {
        long pos = 12, nameAt = -1, authAt = -1;
        int nameLen = 0, authLen = 0;
        for (int guard = 0; guard < 1024 && pos + 8 <= io.Length; guard++)
        {
            if (!io.TryGet(pos, 8, out ReadOnlySpan<byte> h, readAhead: 4096)) break;
            uint id = BinaryPrimitives.ReadUInt32BigEndian(h);
            long size = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
            long body = pos + 8;
            switch (id)
            {
                case 0x434F4D4D when io.TryGet(body, 18, out ReadOnlySpan<byte> c):                                     // COMM
                    uint frames = BinaryPrimitives.ReadUInt32BigEndian(c[2..]);
                    int exponent = ((c[8] & 0x7F) << 8) | c[9];
                    ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(c[10..]);
                    double rate = Math.ScaleB(mantissa, exponent - 16383 - 63);
                    if (rate >= 1 && rate < 1e7)
                    {
                        b.SetLength((long)(frames * 1000.0 / rate));
                        b.SetStream((int)Math.Round(rate), BinaryPrimitives.ReadUInt16BigEndian(c[6..]), BinaryPrimitives.ReadUInt16BigEndian(c));
                    }
                    break;
                case 0x4E414D45: nameAt = body; nameLen = (int)Math.Min(size, MaxTextBytes); break;                    // NAME
                case 0x41555448: authAt = body; authLen = (int)Math.Min(size, MaxTextBytes); break;                    // AUTH
                case 0x49443320 or 0x69643320: ReadId3v2(io, body, b); break;                                           // "ID3 " / "id3 "
            }
            pos = body + size + (size & 1);
        }
        if (nameAt >= 0 && io.TryGet(nameAt, nameLen, out ReadOnlySpan<byte> name)) b.SetTitle(Text8(name, b));
        if (authAt >= 0 && io.TryGet(authAt, authLen, out ReadOnlySpan<byte> auth)) b.AddArtist(Text8(auth, b), 6);
        if (b.DurationMs > 0) b.BitrateKbps = (int)Math.Min(io.Length * 8 / b.DurationMs, 100_000);
    }

    // ── WMA / ASF ────────────────────────────────────────────────────────────────────────────────────────────────────

    static ReadOnlySpan<byte> AsfHeaderGuid => [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
    static ReadOnlySpan<byte> AsfContentGuid => [0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
    static ReadOnlySpan<byte> AsfFilePropertiesGuid => [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];

    /// <summary>The ASF Header Object's children: Content Description (title, author) and File Properties (play duration minus
    /// preroll). Wavee has no WMA decoder; this is for counting and naming unplayable files.</summary>
    static void ReadWma(Bytes io, Builder b)
    {
        if (!io.TryGet(0, 30, out ReadOnlySpan<byte> h)) return;
        long headerEnd = Math.Min((long)BinaryPrimitives.ReadUInt64LittleEndian(h[16..]), io.Length);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
        long pos = 30;
        for (uint i = 0; i < count && pos + 24 <= headerEnd; i++)
        {
            if (!io.TryGet(pos, 24, out ReadOnlySpan<byte> o)) return;
            bool content = o[..16].SequenceEqual(AsfContentGuid), props = o[..16].SequenceEqual(AsfFilePropertiesGuid);
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(o[16..]);
            if (size < 24) return;
            long body = pos + 24;
            if (content && io.TryGet(body, 10, out ReadOnlySpan<byte> lens))
            {
                int title = BinaryPrimitives.ReadUInt16LittleEndian(lens), author = BinaryPrimitives.ReadUInt16LittleEndian(lens[2..]);
                if (title > 0 && io.TryGet(body + 10, title, out ReadOnlySpan<byte> t)) b.SetTitle(Utf16(t, bigEndian: false));
                if (author > 0 && io.TryGet(body + 10 + title, author, out ReadOnlySpan<byte> a)) b.AddArtist(Utf16(a, bigEndian: false), 5);
            }
            else if (props && io.TryGet(body, 64, out ReadOnlySpan<byte> p))
            {
                long play = (long)BinaryPrimitives.ReadUInt64LittleEndian(p[40..]);
                long preroll = (long)BinaryPrimitives.ReadUInt64LittleEndian(p[56..]);
                b.SetLength(play / 10_000 - preroll);
            }
            pos += size;
        }
    }

    // ── EBML (Matroska / WebM) ───────────────────────────────────────────────────────────────────────────────────────

    const uint EbmlMagic = 0x1A45DFA3;

    /// <summary>.mkv / .webm (D19): recorded by the EBML header with the length (Segment/Info) and, when the head holds them, the
    /// tracks (a video track's pixel size; a file with audio tracks only is audio). Read from the head alone: no seek. No decoder.</summary>
    static void ReadEbml(FileBytes io, Builder b, out string why)
    {
        ReadOnlySpan<byte> s = io.Head;
        int pos = 0;
        bool webm = false, tracks = false, video = false, audio = false;
        int width = 0, height = 0;
        long scale = 1_000_000;
        double duration = 0;
        if (ReadElement(s, ref pos, out uint id, out long size) && id == EbmlMagic)
        {
            int end = (int)Math.Min(pos + size, s.Length);
            for (int p = pos; p < end && ReadElement(s, ref p, out uint cid, out long csize);)
            {
                if (cid == 0x4282 && csize > 0 && p + csize <= end) webm = s.Slice(p, (int)csize).SequenceEqual("webm"u8);
                p += (int)Math.Max(0, csize);
            }
            pos = end;
        }
        if (ReadElement(s, ref pos, out id, out _) && id == 0x18538067)                      // Segment
        {
            while (pos < s.Length && ReadElement(s, ref pos, out uint cid, out long csize))
            {
                if (cid == 0x1F43B675 || csize < 0) break;                                  // a Cluster, or a size we cannot skip
                int end = (int)Math.Min(pos + csize, s.Length);
                if (cid == 0x1549A966)                                                     // Info
                {
                    for (int p = pos; p < end && ReadElement(s, ref p, out uint iid, out long isz);)
                    {
                        if (isz < 0 || p + isz > end) break;
                        if (iid == 0x2AD7B1) scale = (long)UInt(s.Slice(p, (int)isz));
                        else if (iid == 0x4489) duration = isz == 4 ? BinaryPrimitives.ReadSingleBigEndian(s[p..]) : isz == 8 ? BinaryPrimitives.ReadDoubleBigEndian(s[p..]) : 0;
                        p += (int)isz;
                    }
                }
                else if (cid == 0x1654AE6B)                                                // Tracks
                {
                    tracks = true;
                    for (int p = pos; p < end && ReadElement(s, ref p, out uint tid, out long tsz);)
                    {
                        if (tsz < 0 || p + tsz > end) break;
                        if (tid == 0xAE) ReadTrackEntry(s.Slice(p, (int)tsz), ref video, ref audio, ref width, ref height);
                        p += (int)tsz;
                    }
                }
                if (pos + csize > s.Length) break;
                pos = end;
            }
        }
        b.Kind = !tracks || video ? LocalKind.Video : LocalKind.Audio;
        b.HasAudio = !tracks || audio;
        b.VideoWidth = width;
        b.VideoHeight = height;
        if (duration > 0 && scale > 0) b.SetLength((long)(duration * scale / 1_000_000));
        if (b.DurationMs > 0) b.BitrateKbps = (int)Math.Min(io.Length * 8 / b.DurationMs, 100_000);
        why = webm ? "a WebM file" : "a Matroska file";

        static void ReadTrackEntry(ReadOnlySpan<byte> e, ref bool video, ref bool audio, ref int width, ref int height)
        {
            for (int p = 0; p < e.Length && ReadElement(e, ref p, out uint id, out long size);)
            {
                if (size < 0 || p + size > e.Length) return;
                if (id == 0x83) { long type = (long)UInt(e.Slice(p, (int)size)); if (type == 1) video = true; else if (type == 2) audio = true; }
                else if (id == 0xE0)
                {
                    ReadOnlySpan<byte> v = e.Slice(p, (int)size);
                    for (int q = 0; q < v.Length && ReadElement(v, ref q, out uint vid, out long vsz);)
                    {
                        if (vsz < 0 || q + vsz > v.Length) break;
                        if (vid == 0xB0 && width == 0) width = (int)UInt(v.Slice(q, (int)vsz));
                        else if (vid == 0xBA && height == 0) height = (int)UInt(v.Slice(q, (int)vsz));
                        q += (int)vsz;
                    }
                }
                p += (int)size;
            }
        }
    }

    /// <summary>An EBML element header: its id (marker bits kept, 1-4 bytes) and data size (marker removed, 1-8 bytes; −1 = unknown).</summary>
    static bool ReadElement(ReadOnlySpan<byte> s, ref int pos, out uint id, out long size)
    {
        id = 0;
        size = 0;
        if (pos >= s.Length) return false;
        int idLen = System.Numerics.BitOperations.LeadingZeroCount((uint)s[pos]) - 24 + 1;
        if (idLen is < 1 or > 4 || pos + idLen > s.Length) return false;
        for (int i = 0; i < idLen; i++) id = (id << 8) | s[pos + i];
        pos += idLen;
        if (pos >= s.Length) return false;
        int sizeLen = System.Numerics.BitOperations.LeadingZeroCount((uint)s[pos]) - 24 + 1;
        if (sizeLen is < 1 or > 8 || pos + sizeLen > s.Length) return false;
        long v = s[pos] & (0xFF >> sizeLen);
        bool allOnes = v == (0xFF >> sizeLen);
        for (int i = 1; i < sizeLen; i++) { v = (v << 8) | s[pos + i]; allOnes &= s[pos + i] == 0xFF; }
        pos += sizeLen;
        size = allOnes ? -1 : v;
        return true;
    }

    static ulong UInt(ReadOnlySpan<byte> s)
    {
        ulong v = 0;
        for (int i = 0; i < s.Length && i < 8; i++) v = (v << 8) | s[i];
        return v;
    }
}
