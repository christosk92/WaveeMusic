// ── Playback/LocalTags.cs — a local file's format, tags, length and cover location, from its headers (#240, plan §3.5) ──
//
// The local index (P1b) and a dropped file (`Playlist.ImportLocalFile`, `Playback.Audio.LocalSource`) read a file's tags
// here. Our own readers, no tag library (plan §5 "ID3 in the wild"; research `tag-readers.md` §7): MP3 (ID3v2.2-2.4,
// ID3v1, APE, Lyrics3v2), FLAC, Ogg Vorbis / Opus / FLAC, MP4 / M4A (the `ilst` atoms; a `vide` handler marks a video,
// D19), raw ADTS, WAV and AIFF (INFO / NAME and an ID3 chunk), the WMA header, and the EBML header of .mkv / .webm.
//
// THE I/O SHAPE IS THE POINT. One `File.OpenHandle`, one 64 KiB read at offset 0 into a pooled buffer, at most one tail
// read, and a seek where a format needs one (an MP4 `moov` after `mdat`, a FLAC PICTURE before the comments, an MP3
// whose ID3 tag holds a large picture). Everything is parsed from spans and only the wanted fields become strings.
// PICTURES ARE NEVER READ: a cover is recorded as a `LocalCover` (offset, length, mime, encoding) and skipped by length,
// because a cover is most of a tag's bytes. Best attempt: a field that does not parse is left empty and never fails the
// file. Never throws: an unreadable file answers false with a reason.
//
// The parts: LocalTags.cs (this: the API, the byte window, the builder, text decoding, sniffing), LocalTags.Mpeg.cs
// (ID3v2 / ID3v1 / APE and the MPEG length), LocalTags.Containers.cs (FLAC, Ogg, MP4, ADTS, WAV, AIFF, WMA, EBML).

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wavee;

/// <summary>A local file's format, tags and length from its headers (plan §3.5). Scan thread (or any thread but the UI).</summary>
public static partial class LocalTags
{
    /// <summary>The head read, and the size of the one window every later read lands in.</summary>
    const int WindowBytes = 64 * 1024;

    /// <summary>The longest text field kept (a 64 KiB "title" is a broken tag, not a title).</summary>
    const int MaxTextBytes = 16 * 1024;

    /// <summary>Read one file. One head read + at most one tail read + 1-2 seeks. Never decodes audio, never throws, never reads
    /// picture bytes. Unreadable: false, format Unknown, <paramref name="why"/> set. Unknown content with an audio extension
    /// (<see cref="IsScanExtension"/>): true, format Unknown, <paramref name="why"/> set.</summary>
    public static bool TryRead(string path, out Spotify.Audio.Format format, out LocalTagSet tags, out string why)
    {
        format = Spotify.Audio.Format.Unknown;
        tags = Empty;
        SafeFileHandle? file = null;
        try
        {
            file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
            long length = RandomAccess.GetLength(file);
            return TryRead(file, length, Path.GetFileName(path.AsSpan()), out format, out tags, out why);
        }
        catch (Exception ex)
        {
            why = "the file could not be opened (" + ex.GetType().Name + ")";
            return false;
        }
        finally
        {
            file?.Dispose();
        }
    }

    /// <summary>Same, on a handle the caller already opened (the scanner opens by id / relative to a directory handle). The handle
    /// stays open; <paramref name="fileName"/> only decides the answer for unknown content (and is never logged with a folder).</summary>
    public static bool TryRead(SafeFileHandle file, long length, ReadOnlySpan<char> fileName,
                               out Spotify.Audio.Format format, out LocalTagSet tags, out string why)
    {
        format = Spotify.Audio.Format.Unknown;
        tags = Empty;
        why = "";
        if (file is null || file.IsInvalid || file.IsClosed) { why = "the file is not open"; return false; }
        byte[] buffer = ArrayPool<byte>.Shared.Rent(2 * WindowBytes);
        var b = new Builder();
        bool known = false;
        try
        {
            var io = new FileBytes(file, length, buffer);
            if (!io.LoadHead(out why)) return false;
            known = Read(io, b, out format, out why);
            tags = b.Build();
            if (b.Win1252High) NoteWin1252(fileName);
        }
        catch (Exception ex)
        {
            // A reader bug or an I/O error past the head: what was read so far stands, the rest is empty (best attempt).
            tags = b.Build();
            if (why.Length == 0) why = "a damaged header (" + ex.GetType().Name + ")";
            if (!known && format == Spotify.Audio.Format.Unknown && !b.Sniffed) return false;
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (known) return true;
        if (why.Length == 0) why = "no audio format Wavee recognises";
        return IsScanExtension(fileName);
    }

    /// <summary>The title a file has in a <c>spotify:local:</c> uri: the title tag, else the file name without its extension
    /// (the official client's rule for an untagged file, and <c>Playlist.LocalTitleOf</c>'s).</summary>
    public static string TitleOrFileName(in LocalTagSet tags, ReadOnlySpan<char> fileName)
    {
        if (!string.IsNullOrWhiteSpace(tags.Title)) return tags.Title;
        ReadOnlySpan<char> name = Path.GetFileName(fileName);
        ReadOnlySpan<char> stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length > 0 ? new string(stem) : new string(name);
    }

    /// <summary>Extensions the scan considers: .mp3 .flac .ogg .oga .m4a .mp4 .m4v .aac .wav .opus .mkv .webm, and (contract
    /// revision 2) .wma .aif .aiff, which are read but have no decoder in 0.4.</summary>
    public static bool IsScanExtension(ReadOnlySpan<char> fileName)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(fileName);
        return ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".oga", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase) || ext.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) || ext.Equals(".opus", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wma", StringComparison.OrdinalIgnoreCase) || ext.Equals(".aif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Files whose ID3 text used a Windows-1252 byte in 0x80-0x9F (encoding 0, decoded as Windows-1252 rather than
    /// Latin-1). The first few are logged by file name; the rest only count. Matching against the official client's text depends
    /// on how it decodes these, which is unknown (research `tag-readers.md` §6).</summary>
    public static int Win1252HighByteFiles => Volatile.Read(ref s_win1252High);

    static int s_win1252High;
    const int Win1252LogLines = 20;

    static void NoteWin1252(ReadOnlySpan<char> fileName)
    {
        int n = Interlocked.Increment(ref s_win1252High);
        if (n <= Win1252LogLines)
            Log.Info("local", "local.tags cp1252 n=" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                              + " file=" + new string(Path.GetFileName(fileName)));
    }

    static LocalTagSet Empty => new(LocalKind.Audio, "", [], "", "", 0, 0, 0, 0, false, 0, LocalCover.None, 0, 0, true, 0, 0, 0);

    // ── sniff and dispatch ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Decide the format from the bytes (never the extension) and run its reader. False: content no reader knows.</summary>
    static bool Read(FileBytes io, Builder b, out Spotify.Audio.Format format, out string why)
    {
        format = Spotify.Audio.Format.Unknown;
        why = "";
        ReadOnlySpan<byte> head = io.Head;

        // Containers that never start with an ID3 tag.
        if (Playback.Audio.Mp4.LooksLikeMp4(head)) { b.Sniffed = true; return ReadMp4(io, b, out format, out why); }
        if (head.Length >= 12 && (head[..4].SequenceEqual("RIFF"u8) || head[..4].SequenceEqual("RF64"u8)) && head[8..12].SequenceEqual("WAVE"u8))
        {
            b.Sniffed = true;
            ReadWav(io, b);
            why = "a WAV file";
            return true;
        }
        if (head.Length >= 12 && head[..4].SequenceEqual("FORM"u8) && (head[8..12].SequenceEqual("AIFF"u8) || head[8..12].SequenceEqual("AIFC"u8)))
        {
            b.Sniffed = true;
            ReadAiff(io, b);
            why = "an AIFF file";
            return true;
        }
        if (head.Length >= 30 && head[..16].SequenceEqual(AsfHeaderGuid))
        {
            b.Sniffed = true;
            ReadWma(io, b);
            why = "a WMA file";
            return true;
        }
        if (head.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(head) == EbmlMagic)
        {
            b.Sniffed = true;
            ReadEbml(io, b, out why);
            return true;
        }

        // An ID3v2 tag (or two, back to back) may sit before FLAC, Ogg, ADTS or MPEG audio: find where it ends, sniff after.
        long start = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!io.TryGet(start, 10, out ReadOnlySpan<byte> id3)) break;
            long len = Playback.Audio.Id3v2Length(id3);
            if (len <= 0) break;
            start += len;
        }
        ReadOnlySpan<byte> audio = io.UpTo(start, 4096);
        if (audio.Length >= 4 && audio[..4].SequenceEqual("fLaC"u8))
        {
            b.Sniffed = true;
            format = Spotify.Audio.Format.Flac;
            ReadFlac(io, start, b);
            if (start > 0) ReadId3v2Chain(io, 0, start, b);                     // an illegal ID3 before fLaC only fills what the comments left
            return true;
        }
        if (audio.Length >= 4 && audio[..4].SequenceEqual("OggS"u8))
        {
            b.Sniffed = true;
            return ReadOgg(io, start, b, out format, out why);
        }
        if (Modules.Adts.FindSync(audio) == 0 && Modules.Adts.TryParseHeader(audio, out Modules.AdtsHeader adts)
            && adts.FrameLength + Modules.Adts.MinHeaderBytes <= audio.Length)
        {
            b.Sniffed = true;
            format = Spotify.Audio.Format.Aac;
            if (start > 0) ReadId3v2Chain(io, 0, start, b);
            ReadAdtsLength(io, start, b);
            return true;
        }
        if (FindMpegFrame(io, start, out long frameAt, out MpegHeader mpeg))
        {
            b.Sniffed = true;
            format = Spotify.Audio.Format.Mp3;
            if (start > 0) ReadId3v2Chain(io, 0, start, b);
            ReadMp3Length(io, frameAt, mpeg, b);
            return true;
        }
        // No audio we know, but its tag still names it (a file with an ID3 tag and a codec we cannot sniff).
        if (start > 0) ReadId3v2Chain(io, 0, start, b);
        return false;
    }

    // ── the byte window ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bytes of a file by absolute offset: the 64 KiB head, and one more window that a read outside the head refills (so a
    /// walk that moves forward costs one read per 64 KiB it actually touches). A view over a de-unsynchronised ID3 tag is the
    /// other kind (<see cref="MemoryBytes"/>), with its own offset map back to the file.</summary>
    abstract class Bytes
    {
        /// <summary>The length of what this view covers (the file's length for a file).</summary>
        public long Length;

        /// <summary>Bytes <c>[at, at + length)</c>, or false when they are past the end or could not be read. <paramref name="length"/> is at
        /// most <see cref="WindowBytes"/>. The span lives until the next call.</summary>
        public abstract bool TryGet(long at, int length, out ReadOnlySpan<byte> bytes, int readAhead = WindowBytes);

        /// <summary>Up to <paramref name="max"/> bytes at <paramref name="at"/> (fewer at the end; empty past it).</summary>
        public ReadOnlySpan<byte> UpTo(long at, int max, int readAhead = WindowBytes)
        {
            if (at < 0 || at >= Length) return default;
            int n = (int)Math.Min(max, Length - at);
            return TryGet(at, n, out ReadOnlySpan<byte> s, readAhead) ? s : default;
        }

        /// <summary>Where offset <paramref name="at"/> of this view is in the file.</summary>
        public virtual long FileOffset(long at) => at;
    }

    sealed class FileBytes(SafeFileHandle file, long length, byte[] buffer) : Bytes
    {
        int _headLen;
        long _auxAt = -1;
        int _auxLen;

        /// <summary>Reads made after the head (the seeks a format cost).</summary>
        public int Reads;

        public ReadOnlySpan<byte> Head => buffer.AsSpan(0, _headLen);

        public bool LoadHead(out string why)
        {
            Length = length;
            why = "";
            try { _headLen = ReadAt(0, buffer.AsSpan(0, (int)Math.Min(length, WindowBytes))); }
            catch (Exception ex) { why = "the file could not be read (" + ex.GetType().Name + ")"; return false; }
            if (_headLen <= 0 && length > 0) { why = "the file could not be read"; return false; }
            return true;
        }

        public override bool TryGet(long at, int count, out ReadOnlySpan<byte> bytes, int readAhead = WindowBytes)
        {
            bytes = default;
            if (at < 0 || count < 0 || count > WindowBytes || at + count > Length) return false;
            if (at + count <= _headLen) { bytes = buffer.AsSpan((int)at, count); return true; }
            if (_auxAt >= 0 && at >= _auxAt && at + count <= _auxAt + _auxLen)
            {
                bytes = buffer.AsSpan(WindowBytes + (int)(at - _auxAt), count);
                return true;
            }
            int want = (int)Math.Min(Math.Clamp(Math.Max(readAhead, count), count, WindowBytes), Length - at);
            Reads++;
            _auxAt = at;
            _auxLen = ReadAt(at, buffer.AsSpan(WindowBytes, want));
            if (_auxLen < count) { _auxAt = -1; return false; }
            bytes = buffer.AsSpan(WindowBytes, count);
            return true;
        }

        /// <summary>Read <paramref name="into"/>'s length at <paramref name="at"/> straight from the file (a whole unsynchronised tag).</summary>
        public int ReadInto(long at, Span<byte> into)
        {
            Reads++;
            return ReadAt(at, into);
        }

        int ReadAt(long at, Span<byte> into)
        {
            int total = 0;
            while (total < into.Length)
            {
                int n = RandomAccess.Read(file, into[total..], at + total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }

    /// <summary>A de-unsynchronised ID3v2.3 tag held whole (the one case read whole, plan §3.5). Offsets are into the decoded bytes;
    /// <see cref="FileOffset"/> maps one back through the raw bytes (every dropped 0x00 after a 0xFF moves the file offset by one).</summary>
    sealed class MemoryBytes(byte[] decoded, int decodedLength, byte[] raw, int rawLength, long rawAt) : Bytes
    {
        public override bool TryGet(long at, int count, out ReadOnlySpan<byte> bytes, int readAhead = WindowBytes)
        {
            bytes = default;
            if (at < 0 || count < 0 || at + count > decodedLength) return false;
            bytes = decoded.AsSpan((int)at, count);
            return true;
        }

        public override long FileOffset(long at)
        {
            int target = (int)Math.Clamp(at, 0, decodedLength), d = 0, r = 0;
            while (d < target && r < rawLength)
            {
                if (raw[r] == 0xFF && r + 1 < rawLength && raw[r + 1] == 0x00) { r += 2; d++; continue; }
                r++;
                d++;
            }
            return rawAt + r;
        }

        public void Init() => Length = decodedLength;
    }

    // ── what the readers fill ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tag set as the readers find it. Text fields fill once: the first reader to set one wins (the file's own tag is
    /// read before a fallback). Artists come from one source: the first source that names any keeps all of its values.</summary>
    sealed class Builder
    {
        public string Title = "", Album = "", AlbumArtist = "";
        public readonly List<string> Artists = [];
        int _artistSource = -1;
        public int Track, Disc, Year, DurationMs, BitrateKbps, VideoWidth, VideoHeight, SampleRate, Bits, Channels;
        public bool DurationEstimated, Win1252High, Sniffed, HasAudio = true;
        public LocalKind Kind = LocalKind.Audio;
        public LocalCover Cover;

        public void SetTitle(string v) { if (Title.Length == 0) Title = Clean(v); }
        public void SetAlbum(string v) { if (Album.Length == 0) Album = Clean(v); }
        public void SetAlbumArtist(string v) { if (AlbumArtist.Length == 0) AlbumArtist = Clean(v); }
        public void SetTrack(int v) { if (Track == 0 && v > 0) Track = v; }
        public void SetDisc(int v) { if (Disc == 0 && v > 0) Disc = v; }
        public void SetYear(int v) { if (Year == 0 && v is >= 1000 and <= 2999) Year = v; }

        /// <summary>One artist field from <paramref name="source"/> (a tag; its fields accumulate in order). ONE splitting rule for
        /// every format (contract revision 2): a field splits on NUL, on <c>;</c> and on a spaced <c>" / "</c>, never on a bare
        /// <c>/</c> ("AC/DC" stays one artist). Repeated fields (Vorbis / APE <c>ARTIST</c>, several MP4 <c>data</c> boxes) arrive as
        /// separate calls and stay separate values.</summary>
        public void AddArtist(string field, int source)
        {
            if (string.IsNullOrWhiteSpace(field)) return;
            if (_artistSource < 0) _artistSource = source;
            if (_artistSource != source) return;
            if (field.IndexOfAny(ArtistSeparators) < 0 && !field.Contains(" / ", StringComparison.Ordinal))
            {
                AddOne(field);
                return;
            }
            string split = field.Replace(" / ", ";", StringComparison.Ordinal).Replace('\0', ';');
            foreach (Range r in split.AsSpan().Split(';')) AddOne(split[r]);
        }

        static readonly char[] ArtistSeparators = ['\0', ';'];

        void AddOne(string v)
        {
            v = Clean(v);
            if (v.Length == 0) return;
            foreach (string a in Artists) if (a == v) return;
            Artists.Add(v);
        }

        /// <summary>An exact length (a frame count, a sample count, a header's duration). Replaces an estimate.</summary>
        public void SetLength(long ms)
        {
            if (ms <= 0 || ms > int.MaxValue) return;
            if (DurationMs == 0 || DurationEstimated) { DurationMs = (int)ms; DurationEstimated = false; }
        }

        /// <summary>A length worked out from a bitrate (or a TLEN frame). Never replaces an exact one.</summary>
        public void SetEstimate(long ms)
        {
            if (ms <= 0 || ms > int.MaxValue || DurationMs > 0) return;
            DurationMs = (int)ms;
            DurationEstimated = true;
        }

        /// <summary>The audio stream's format, from the header that describes it (STREAMINFO, fmt, COMM, a frame header, a sample
        /// entry). The first reader to report a rate keeps it; <paramref name="bits"/> is 0 for a lossy codec.</summary>
        public void SetStream(int rate, int bits, int channels)
        {
            if (SampleRate != 0 || rate <= 0 || rate > 1_536_000) return;
            SampleRate = rate;
            Bits = bits is > 0 and <= 64 ? bits : 0;
            Channels = channels is > 0 and <= 255 ? channels : 0;
        }

        /// <summary>A picture's location. The first one is kept; a front cover (type 3) replaces a picture of any other type.</summary>
        public void OfferCover(LocalCover cover)
        {
            if (!cover.Exists) return;
            if (!Cover.Exists || (cover.PictureType == 3 && Cover.PictureType != 3)) Cover = cover;
        }

        public LocalTagSet Build() => new(Kind, Title, [.. Artists], Album, AlbumArtist, Track, Disc, Year,
            DurationMs, DurationEstimated && DurationMs > 0, BitrateKbps, Cover, Kind == LocalKind.Video ? VideoWidth : 0,
            Kind == LocalKind.Video ? VideoHeight : 0, Kind != LocalKind.Video || HasAudio, SampleRate, Bits, Channels);

        static string Clean(string v) => v.Trim().Trim('\0').Trim();
    }

    // ── text ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Windows-1252's 0x80-0x9F (the 32 places it differs from Latin-1). The five it leaves undefined decode as their C1
    /// control, as Windows itself does.</summary>
    static ReadOnlySpan<char> Cp1252Table =>
        "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F" +
        "\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    /// <summary>Single-byte text as Windows-1252 (ID3 encoding 0, ID3v1, RIFF INFO). Stops at the first NUL.</summary>
    static string Windows1252(ReadOnlySpan<byte> bytes, Builder b)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        if (bytes.IsEmpty) return "";
        Span<char> chars = bytes.Length <= 512 ? stackalloc char[bytes.Length] : new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte c = bytes[i];
            if (c is >= 0x80 and <= 0x9F) { chars[i] = Cp1252Table[c - 0x80]; b.Win1252High = true; }
            else chars[i] = (char)c;
        }
        return new string(chars);
    }

    static string Utf8(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        return bytes.IsEmpty ? "" : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>UTF-16 without a terminator: <paramref name="bigEndian"/> unless a BOM says otherwise; stops at a NUL code unit.</summary>
    static string Utf16(ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE) { bigEndian = false; bytes = bytes[2..]; }
            else if (bytes[0] == 0xFE && bytes[1] == 0xFF) { bigEndian = true; bytes = bytes[2..]; }
        }
        int units = bytes.Length / 2;
        if (units == 0) return "";
        Span<char> chars = units <= 512 ? stackalloc char[units] : new char[units];
        int n = 0;
        for (int i = 0; i < units; i++)
        {
            char c = (char)(bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes[(2 * i)..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[(2 * i)..]));
            if (c == '\0') break;
            chars[n++] = c;
        }
        return new string(chars[..n]);
    }

    /// <summary>The leading number of <c>"3"</c>, <c>"03/12"</c>, <c>" 7 "</c>; 0 when there is none.</summary>
    static int LeadingInt(ReadOnlySpan<char> text)
    {
        text = text.TrimStart();
        int v = 0, i = 0;
        while (i < text.Length && i < 6 && char.IsAsciiDigit(text[i])) v = v * 10 + (text[i++] - '0');
        return v;
    }

    /// <summary>The first run of four digits (<c>"2004"</c>, <c>"2004-05-01"</c>, <c>"05/01/2004"</c>); 0 when there is none.</summary>
    static int YearOf(ReadOnlySpan<char> text)
    {
        for (int i = 0; i + 4 <= text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]) || (i > 0 && char.IsAsciiDigit(text[i - 1]))) continue;
            if (char.IsAsciiDigit(text[i + 1]) && char.IsAsciiDigit(text[i + 2]) && char.IsAsciiDigit(text[i + 3])
                && (i + 4 == text.Length || !char.IsAsciiDigit(text[i + 4])))
                return (text[i] - '0') * 1000 + (text[i + 1] - '0') * 100 + (text[i + 2] - '0') * 10 + (text[i + 3] - '0');
        }
        return 0;
    }

    /// <summary>An image mime (or ID3v2.2's three-letter format) → <see cref="LocalCoverMime"/>.</summary>
    static LocalCoverMime MimeOf(ReadOnlySpan<byte> mime)
    {
        int slash = mime.IndexOf((byte)'/');
        ReadOnlySpan<byte> sub = slash >= 0 ? mime[(slash + 1)..] : mime;
        if (Ascii.EqualsIgnoreCase(sub, "jpeg"u8) || Ascii.EqualsIgnoreCase(sub, "jpg"u8)) return LocalCoverMime.Jpeg;
        if (Ascii.EqualsIgnoreCase(sub, "png"u8)) return LocalCoverMime.Png;
        if (Ascii.EqualsIgnoreCase(sub, "webp"u8)) return LocalCoverMime.Webp;
        if (Ascii.EqualsIgnoreCase(sub, "gif"u8)) return LocalCoverMime.Gif;
        if (Ascii.EqualsIgnoreCase(sub, "bmp"u8) || Ascii.EqualsIgnoreCase(sub, "x-ms-bmp"u8)) return LocalCoverMime.Bmp;
        return LocalCoverMime.Unknown;
    }
}
