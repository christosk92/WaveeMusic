namespace Wavee.Backend.Audio;

/// <summary>One parsed ADTS frame header (ISO/IEC 13818-7 §6.2).</summary>
/// <param name="Mpeg2">The <c>ID</c> bit: true = MPEG-2 AAC, false = MPEG-4 AAC.</param>
/// <param name="Profile">The 2-bit profile field; the MPEG-4 audio object type is <c>Profile + 1</c>.</param>
/// <param name="SamplingFrequencyIndex">Index into the standard rate table.</param>
/// <param name="SampleRate">The CORE sample rate. With HE-AAC the decoder's OUTPUT rate is double this.</param>
/// <param name="ChannelConfiguration">1 = mono, 2 = stereo, 0 = "read it from the payload" (we treat as stereo).</param>
/// <param name="ProtectionAbsent">False means a 2-byte CRC follows the 7-byte header.</param>
/// <param name="FrameLength">Total frame bytes INCLUDING the header.</param>
/// <param name="HeaderLength">7, or 9 when a CRC is present.</param>
/// <param name="RawDataBlocks">Number of AAC raw data blocks in this frame (1 for every real-world encoder).</param>
internal readonly record struct AdtsHeader(
    bool Mpeg2,
    int Profile,
    int SamplingFrequencyIndex,
    int SampleRate,
    int ChannelConfiguration,
    bool ProtectionAbsent,
    int FrameLength,
    int HeaderLength,
    int RawDataBlocks);

/// <summary>ADTS header parsing + AudioSpecificConfig synthesis.
///
/// <para>Why the app parses ADTS at all when the Media Foundation AAC MFT can be fed ADTS directly: the MFT still has to
/// be CONFIGURED before the first frame (channel count, core sample rate, and the 2-byte AudioSpecificConfig inside
/// <c>MF_MT_USER_DATA</c>) and an ICY stream hands us bytes with no container to ask. So the first frame's header is the
/// configuration, and the frame boundaries are what let a reconnect splice resync.</para></summary>
internal static class AdtsFrameParser
{
    /// <summary>The 13-13818-7 sampling-frequency table. Indices 13–15 are reserved (0 = invalid).</summary>
    static ReadOnlySpan<int> SampleRates =>
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0];

    /// <summary>The smallest buffer a header parse can consume.</summary>
    public const int MinHeaderBytes = 7;

    public static bool TryParseHeader(ReadOnlySpan<byte> data, out AdtsHeader header)
    {
        header = default;
        if (data.Length < MinHeaderBytes) return false;
        // syncword: 12 set bits.
        if (data[0] != 0xFF || (data[1] & 0xF0) != 0xF0) return false;
        // layer must be 00 (an MP3 frame also starts FFFx — the layer bits are what separate them).
        if (((data[1] >> 1) & 0x03) != 0) return false;

        bool mpeg2 = (data[1] & 0x08) != 0;
        bool protectionAbsent = (data[1] & 0x01) != 0;
        int profile = (data[2] >> 6) & 0x03;
        int sfi = (data[2] >> 2) & 0x0F;
        int rate = SampleRates[sfi];
        if (rate == 0) return false;
        int channels = ((data[2] & 0x01) << 2) | ((data[3] >> 6) & 0x03);
        int frameLength = ((data[3] & 0x03) << 11) | (data[4] << 3) | ((data[5] >> 5) & 0x07);
        int headerLength = protectionAbsent ? 7 : 9;
        if (frameLength < headerLength) return false;
        int blocks = (data[6] & 0x03) + 1;

        header = new AdtsHeader(mpeg2, profile, sfi, rate, channels, protectionAbsent, frameLength, headerLength, blocks);
        return true;
    }

    /// <summary>Find the first byte offset in <paramref name="data"/> that starts a plausible ADTS frame, or −1.
    ///
    /// <para>A bare 12-bit syncword produces false positives constantly inside compressed audio, so a candidate is only
    /// accepted when the frame it declares lands on ANOTHER valid header — unless the buffer is too short to check, in
    /// which case the candidate is taken (the caller reads more and re-validates).</para></summary>
    public static int FindSync(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + MinHeaderBytes <= data.Length; i++)
        {
            if (!TryParseHeader(data[i..], out var h)) continue;
            int next = i + h.FrameLength;
            if (next + MinHeaderBytes > data.Length) return i;      // cannot verify yet — accept
            if (TryParseHeader(data[next..], out _)) return i;      // second sync confirms
        }
        return -1;
    }

    /// <summary>Write the 2-byte MPEG-4 AudioSpecificConfig this stream implies (audio object type, sampling-frequency
    /// index, channel configuration). This is what the AAC MFT wants appended to the HEAACWAVEINFO tail in
    /// <c>MF_MT_USER_DATA</c>. Returns the number of bytes written (always 2).</summary>
    public static int WriteAudioSpecificConfig(in AdtsHeader header, Span<byte> dst)
    {
        if (dst.Length < 2) throw new ArgumentException("AudioSpecificConfig needs 2 bytes", nameof(dst));
        int objectType = header.Profile + 1;            // ADTS profile is 0-based, AOT is 1-based
        int sfi = header.SamplingFrequencyIndex & 0x0F;
        int channels = header.ChannelConfiguration <= 0 ? 2 : header.ChannelConfiguration;
        dst[0] = (byte)(((objectType & 0x1F) << 3) | ((sfi >> 1) & 0x07));
        dst[1] = (byte)(((sfi & 0x01) << 7) | ((channels & 0x0F) << 3));
        return 2;
    }
}

/// <summary>Pulls whole ADTS frames off a forward-only stream, resynchronising over junk and over the splice a live
/// reconnect leaves behind. Owns one growable buffer; the span handed back is valid until the next call.</summary>
internal sealed class AdtsFrameReader
{
    const int InitialBufferBytes = 16 * 1024;
    const int MaxFrameBytes = 8 * 1024;      // an ADTS frame length field maxes out at 8191

    readonly Stream _stream;
    byte[] _buf = new byte[InitialBufferBytes];
    int _start;     // first unconsumed byte
    int _end;       // one past the last valid byte
    bool _eof;

    public AdtsFrameReader(Stream stream) => _stream = stream;

    /// <summary>Bytes skipped while resynchronising — non-zero after a splice, and a useful health signal.</summary>
    public long ResyncSkipped { get; private set; }

    /// <summary>Read the next complete frame. Returns false only at end of stream.</summary>
    public bool TryReadFrame(out ReadOnlySpan<byte> frame, out AdtsHeader header)
    {
        while (true)
        {
            int available = _end - _start;
            if (available >= AdtsFrameParser.MinHeaderBytes)
            {
                int sync = AdtsFrameParser.FindSync(_buf.AsSpan(_start, available));
                if (sync > 0) { ResyncSkipped += sync; _start += sync; available -= sync; }
                if (sync >= 0 && AdtsFrameParser.TryParseHeader(_buf.AsSpan(_start, available), out var h))
                {
                    if (h.FrameLength <= available)
                    {
                        frame = _buf.AsSpan(_start, h.FrameLength);
                        header = h;
                        _start += h.FrameLength;
                        return true;
                    }
                }
                else if (sync < 0)
                {
                    // Nothing usable in the window: keep the last 6 bytes (a syncword can straddle a refill).
                    ResyncSkipped += Math.Max(0, available - (AdtsFrameParser.MinHeaderBytes - 1));
                    _start = Math.Max(_start, _end - (AdtsFrameParser.MinHeaderBytes - 1));
                }
            }

            if (_eof) { frame = default; header = default; return false; }
            if (!Fill()) { frame = default; header = default; return false; }
        }
    }

    bool Fill()
    {
        Compact();
        if (_end == _buf.Length)
        {
            if (_buf.Length >= MaxFrameBytes * 2) { _start = _end = 0; ResyncSkipped += _buf.Length; }
            else Array.Resize(ref _buf, _buf.Length * 2);
        }
        int n = _stream.Read(_buf, _end, _buf.Length - _end);
        if (n <= 0) { _eof = true; return false; }
        _end += n;
        return true;
    }

    void Compact()
    {
        if (_start == 0) return;
        int len = _end - _start;
        if (len > 0) Array.Copy(_buf, _start, _buf, 0, len);
        _start = 0;
        _end = len;
    }
}
