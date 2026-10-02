// ── Wavee.Tests/FlacTests.cs — the gate for the FLAC decoder (Wave 3-parallel, owner U) ──────────────────────────
//
// `Playback/Playback.Audio.Flac.cs` is CORE: span in, samples out, no engine, no stream, no thread. So every fact
// here is a pure fact over a byte array, and the suite needs neither `TestScope.Fresh()` nor a scope.
//
// THE ORACLE IS THE FORMAT'S OWN. STREAMINFO carries an MD5 over the decoded, natural-depth, interleaved,
// little-endian samples (RFC 9639 §8.2). `Decodes_bit_exact_against_the_streaminfo_md5` decodes EVERY vendored
// xiph vector and compares — one assert that a wrong decorrelation, a wrong wasted-bits shift, an off-by-one in a
// fixed predictor, a mis-read Rice escape or a bad LPC accumulator width all fail. `flac -t` and Symphonia's
// `validate.rs` prove themselves the same way. The vectors and why each one is here: `Fixtures/flac/README.md`.
//
// WHAT THE VECTORS CANNOT COVER, THIS FILE ENCODES. Every vendored vector is fixed-blocksize, 44.1/48 kHz and
// ≤ 24-bit, and the smallest variable-blocksize vector is 2.3 MB. `Synthetic` is a ~90-line FLAC ENCODER (VERBATIM
// subframes, real CRC-8 and CRC-16, a real STREAMINFO MD5) that builds the rest: 8/12/16/20/24/32-bit, block sizes
// to 65,535, a 655,350 Hz rate, variable blocking with coded sample numbers, and each stereo decorrelation as an
// exact round trip. A synthetic stream that the decoder mis-reads fails its own MD5, exactly like a vendored one.
//
// The allocation fact at the bottom is the CORE rule that cannot be read off the code (P8): after `Open` a full
// decode of a 550 KB vector must move `GC.GetAllocatedBytesForCurrentThread()` by ZERO bytes.
//
// §9 GATES THE KERNELS ONE BY ONE. `Playback.Audio.Flac.Kernels.cs` is unsafe, pointer-walking and vectorised; the
// MD5 theory proves the chain, and §9 proves each inner loop against a plain reference written here, at a length
// below P15's ~16-element threshold AND above it, with the SIMD sites run both ways through `Flac.ForceScalar` and
// compared to the bit. The throughput fact prints samples/s for `subset/01` and holds a deliberately generous floor.

using System.IO;
using System.Security.Cryptography;
using Wavee;
using Xunit;
using Flac = Wavee.Playback.Flac;

namespace Wavee.Tests;

public class FlacTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    const string Baseline = "subset/01 - blocksize 4096.flac";           // 16/44.1 stereo, block 4096, a seek table
    const string NoSeekTable = "subset/47 - only STREAMINFO.flac";       // the Spotify shape for seeking
    const string Picture = "subset/56 - JPG PICTURE.flac";
    const string TwentyFourBit = "subset/63 - predictor overflow check, 24-bit.flac";
    const string NoTotalSamples = "subset/45 - no total number of samples set.flac";
    const string WastedBits = "subset/14 - wasted bits.flac";
    const string EscapeZero = "subset/64 - rice partitions with escape code zero.flac";
    const string Mono = "subset/60 - mono audio.flac";
    const string ThreeChannels = "subset/38 - 3 channels (3.0).flac";
    const string SixChannels = "subset/41 - 6 channels (5.1).flac";

    /// <summary>Every vendored vector that is a VALID stream — the MD5 theory's subjects. The two `faulty/` files
    /// have their own fact below, because a faulty file is supposed to be rejected, not decoded.</summary>
    public static TheoryData<string> Vectors => new()
    {
        Baseline,
        "subset/12 - qlp precision 15 bit.flac",
        "subset/13 - qlp precision 2 bit.flac",
        WastedBits,
        "subset/16 - partition order 8 containing escaped partitions.flac",
        "subset/17 - all fixed orders.flac",
        "subset/22 - 12 bit per sample.flac",
        "subset/23 - 8 bit per sample.flac",
        ThreeChannels,
        SixChannels,
        NoTotalSamples,
        "subset/46 - no min-max framesize set.flac",
        NoSeekTable,
        Picture,
        Mono,
        "subset/61 - predictor overflow check, 16-bit.flac",
        "subset/62 - predictor overflow check, 20-bit.flac",
        TwentyFourBit,
        EscapeZero,
        "uncommon/09 - Rice partition order 15.flac",
    };

    static byte[] Vector(string relative)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flac",
            relative.Replace('/', Path.DirectorySeparatorChar)));

    static Flac.Headers Open(byte[] bytes, Span<Flac.SeekPoint> seek)
    {
        Flac.Headers h = Flac.ParseHeaders(bytes, seek);
        Assert.True(h.Valid);
        Assert.True(h.Complete);
        return h;
    }

    // ── 1. the MD5 oracle ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Decodes_bit_exact_against_the_streaminfo_md5(string file)
    {
        byte[] bytes = Vector(file);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[64];
        Flac.Headers h = Open(bytes, seek);

        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        using var md5 = new Flac.Md5Verifier();

        int pos = h.FirstFrame;
        long samples = 0;
        int frames = 0;
        while (pos < bytes.Length)
        {
            int at = Flac.FindHeader(bytes, pos, h.Info, out _);
            if (at < 0) break;
            Flac.FrameResult fr = dec.DecodeFrame(bytes.AsSpan(at), out int consumed, out Flac.Block block);
            Assert.Equal(Flac.FrameResult.Ok, fr);
            // The coded number and the fixed-block multiply, for free: frame n starts where frame n−1 ended.
            Assert.Equal(samples, block.SampleNumber);
            Assert.Equal(h.Info.Channels, block.Channels);
            Assert.Equal(h.Info.Bps, block.Bps);
            md5.Append(in block);
            samples += block.BlockSize;
            frames++;
            pos = at + consumed;
        }

        Assert.True(frames > 0, file + ": no frame decoded");
        if (h.Info.TotalSamples > 0) Assert.Equal(h.Info.TotalSamples, samples);
        Assert.True(h.Info.HasMd5, file + ": the vector should carry an MD5");
        Assert.True(md5.Matches(in h.Info), file + ": MD5 mismatch");
    }

    // ── 2. CRC-8, CRC-16, and resync ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Crc8_and_crc16_match_the_rfc_check_values()
    {
        // CRC-8: poly 0x07, init 0, no reflection (RFC 9639 §9.1.8); CRC-16/UMTS: poly 0x8005, init 0 (§9.3).
        // Check values from the CRC catalogue (reveng.sourceforge.io).
        Assert.Equal(0xF4, Flac.Crc8("123456789"u8));
        Assert.Equal(0xFEE8, Flac.Crc16("123456789"u8));
        Assert.Equal(0, Flac.Crc8(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, Flac.Crc16(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_corrupt_frame_is_rejected_by_the_crc16_and_the_next_sync_code_recovers()
    {
        byte[] bytes = Vector(Baseline);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        var dec = new Flac.Decoder();
        dec.Open(h.Info);

        // Frame 1 (the second frame) starts here; frame 2 is the one the resync must land on.
        int frame1 = FrameOffset(bytes, h, dec, 1);
        int frame2 = FrameOffset(bytes, h, dec, 2);

        bytes[frame1 + 20] ^= 0xFF;                                         // one byte inside the subframe data
        Assert.Equal(Flac.FrameResult.BadCrc16, dec.DecodeFrame(bytes.AsSpan(frame1), out _, out _));

        // Resync: scan on from the byte after the bad sync until a frame decodes, and land on the next real one.
        int pos = frame1 + 1;
        while (true)
        {
            int at = Flac.FindHeader(bytes, pos, h.Info, out _);
            Assert.True(at >= 0, "the scanner lost the stream");
            if (dec.DecodeFrame(bytes.AsSpan(at), out _, out Flac.Block block) == Flac.FrameResult.Ok)
            {
                Assert.Equal(frame2, at);
                Assert.Equal(2 * h.Info.MinBlock, block.SampleNumber);
                break;
            }
            pos = at + 1;
        }
    }

    [Fact]
    public void A_corrupt_frame_header_is_rejected_by_the_crc8()
    {
        byte[] bytes = Vector(Baseline);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        int frame1 = FrameOffset(bytes, h, dec, 1);

        Assert.Equal(Flac.HeaderResult.Ok, Flac.ParseFrameHeader(bytes.AsSpan(frame1), h.Info, out Flac.FrameHeader good));
        Assert.Equal(6, good.HeaderBytes);

        bytes[frame1 + good.HeaderBytes - 1] ^= 0xFF;                       // the CRC-8 byte itself
        Assert.Equal(Flac.HeaderResult.BadCrc, Flac.ParseFrameHeader(bytes.AsSpan(frame1), h.Info, out _));
        Assert.NotEqual(Flac.FrameResult.Ok, dec.DecodeFrame(bytes.AsSpan(frame1), out _, out _));

        bytes[frame1 + good.HeaderBytes - 1] ^= 0xFF;                       // put it back
        bytes[frame1 + 4] ^= 0x01;                                          // a bit of the coded frame number
        Assert.Equal(Flac.HeaderResult.BadCrc, Flac.ParseFrameHeader(bytes.AsSpan(frame1), h.Info, out _));
    }

    [Theory]
    [InlineData("faulty/01 - wrong max blocksize.flac")]
    [InlineData("faulty/08 - blocksize 65536.flac")]
    public void Faulty_files_are_rejected_without_an_overrun(string file)
    {
        byte[] bytes = Vector(file);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Flac.ParseHeaders(bytes, seek);
        if (!h.Valid) return;                                               // a STREAMINFO that fails its own range

        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        int pos = h.FirstFrame;
        long samples = 0;
        while (pos < bytes.Length)
        {
            int at = Flac.FindHeader(bytes, pos, h.Info, out _);
            if (at < 0) break;
            Flac.FrameResult fr = dec.DecodeFrame(bytes.AsSpan(at), out int consumed, out Flac.Block block);
            if (fr != Flac.FrameResult.Ok) { pos = at + 1; continue; }
            Assert.True(block.BlockSize <= h.Info.MaxBlock);                // never wrote past the planar buffer
            samples += block.BlockSize;
            pos = at + consumed;
        }
        // The point is that the loop terminated, never threw, and never claimed more audio than the file declares.
        Assert.True(samples < h.Info.TotalSamples, file + ": a faulty file must not decode whole");
    }

    // ── 3. the frame header's four code tables (§9.1.1-9.1.4) ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 192)]
    [InlineData(2, 576)]
    [InlineData(3, 1152)]
    [InlineData(4, 2304)]
    [InlineData(5, 4608)]
    [InlineData(8, 256)]
    [InlineData(9, 512)]
    [InlineData(10, 1024)]
    [InlineData(11, 2048)]
    [InlineData(12, 4096)]
    [InlineData(13, 8192)]
    [InlineData(14, 16384)]
    [InlineData(15, 32768)]
    public void Block_size_codes_match_rfc_9639(int code, int expected)
    {
        var si = new Flac.StreamInfo { MinBlock = 16, MaxBlock = 65535, SampleRate = 44100, Channels = 2, Bps = 16 };
        Assert.Equal(Flac.HeaderResult.Ok, Flac.ParseFrameHeader(HandHeader(code, 9, 1, 4, 0), si, out Flac.FrameHeader h));
        Assert.Equal(expected, h.BlockSize);
    }

    [Theory]
    [InlineData(1, 88200)]
    [InlineData(2, 176400)]
    [InlineData(3, 192000)]
    [InlineData(4, 8000)]
    [InlineData(5, 16000)]
    [InlineData(6, 22050)]
    [InlineData(7, 24000)]
    [InlineData(8, 32000)]
    [InlineData(9, 44100)]
    [InlineData(10, 48000)]
    [InlineData(11, 96000)]
    public void Sample_rate_codes_match_rfc_9639(int code, int expected)
    {
        var si = new Flac.StreamInfo { MinBlock = 16, MaxBlock = 65535, SampleRate = expected, Channels = 2, Bps = 16 };
        Assert.Equal(Flac.HeaderResult.Ok, Flac.ParseFrameHeader(HandHeader(12, code, 1, 4, 0), si, out Flac.FrameHeader h));
        Assert.Equal(expected, h.SampleRate);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(7, 8, 0)]
    [InlineData(8, 2, 1)]        // left/side
    [InlineData(9, 2, 2)]        // side/right
    [InlineData(10, 2, 3)]       // mid/side
    public void Channel_codes_match_rfc_9639(int code, int channels, int assignment)
    {
        var si = new Flac.StreamInfo { MinBlock = 16, MaxBlock = 65535, SampleRate = 44100, Channels = (byte)channels, Bps = 16 };
        Assert.Equal(Flac.HeaderResult.Ok, Flac.ParseFrameHeader(HandHeader(12, 9, code, 4, 0), si, out Flac.FrameHeader h));
        Assert.Equal(channels, h.Channels);
        Assert.Equal(assignment, h.Assignment);
    }

    [Theory]
    [InlineData(0, 16)]          // 000 = from STREAMINFO
    [InlineData(1, 8)]
    [InlineData(2, 12)]
    [InlineData(4, 16)]
    [InlineData(5, 20)]
    [InlineData(6, 24)]
    [InlineData(7, 32)]
    public void Bit_depth_codes_match_rfc_9639(int code, int expected)
    {
        var si = new Flac.StreamInfo { MinBlock = 16, MaxBlock = 65535, SampleRate = 44100, Channels = 2, Bps = (byte)expected };
        Assert.Equal(Flac.HeaderResult.Ok, Flac.ParseFrameHeader(HandHeader(12, 9, 1, code, 0), si, out Flac.FrameHeader h));
        Assert.Equal(expected, h.Bps);
    }

    [Theory]
    [InlineData(0, 9, 1, 4)]     // block size code 0 is reserved
    [InlineData(12, 15, 1, 4)]   // rate code 0b1111 is invalid
    [InlineData(12, 9, 11, 4)]   // channel codes 11..15 are reserved
    [InlineData(12, 9, 1, 3)]    // bit depth code 3 is reserved
    public void Reserved_header_codes_are_refused(int block, int rate, int chan, int bps)
    {
        var si = new Flac.StreamInfo { MinBlock = 16, MaxBlock = 65535, SampleRate = 44100, Channels = 2, Bps = 16 };
        Assert.Equal(Flac.HeaderResult.Reserved, Flac.ParseFrameHeader(HandHeader(block, rate, chan, bps, 0), si, out _));
    }

    [Fact]
    public void A_header_that_contradicts_streaminfo_is_a_false_sync()
    {
        var si = new Flac.StreamInfo { MinBlock = 16, MaxBlock = 4096, SampleRate = 44100, Channels = 2, Bps = 16 };
        // A well-formed header with a valid CRC-8, but 48 kHz: inside audio data, not a format change.
        Assert.Equal(Flac.HeaderResult.Mismatch, Flac.ParseFrameHeader(HandHeader(12, 10, 1, 4, 0), si, out _));
        // And a block size larger than STREAMINFO admits.
        Assert.Equal(Flac.HeaderResult.Mismatch, Flac.ParseFrameHeader(HandHeader(15, 9, 1, 4, 0), si, out _));
        Assert.Equal(Flac.HeaderResult.Truncated, Flac.ParseFrameHeader(HandHeader(12, 9, 1, 4, 0).AsSpan(0, 4), si, out _));
        Assert.Equal(Flac.HeaderResult.NotSync, Flac.ParseFrameHeader(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, si, out _));
    }

    [Fact]
    public void Coded_numbers_round_trip_to_thirty_six_bits()
    {
        foreach (ulong value in new ulong[] { 0, 1, 0x7F, 0x80, 0x7FF, 0x800, 0xFFFF, 0x1F_FFFF, 0x3FF_FFFF,
                                              0x7FFF_FFFF, 0xF_FFFF_FFFF })
        {
            var w = new BitWriter();
            WriteCodedNumber(w, value);
            byte[] bytes = w.ToArray();
            int pos = 0;
            Assert.True(Flac.ReadCodedNumber(bytes, ref pos, out ulong read), $"coded number {value}");
            Assert.Equal(value, read);
            Assert.Equal(bytes.Length, pos);
        }

        // A broken continuation byte is "not a header", which is how the sync scanner rejects a false positive.
        int bad = 0;
        Assert.False(Flac.ReadCodedNumber(new byte[] { 0xC2, 0x41 }, ref bad, out _));
        int truncated = 0;
        Assert.False(Flac.ReadCodedNumber(new byte[] { 0xE2 }, ref truncated, out _));
    }

    // ── 4. metadata: STREAMINFO, VORBIS_COMMENT, PICTURE, SEEKTABLE ─────────────────────────────────────────────────

    [Fact]
    public void Streaminfo_carries_the_duration_and_the_shape()
    {
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[64];
        Flac.Headers h = Open(Vector(Baseline), seek);
        Assert.Equal(4096, h.Info.MinBlock);
        Assert.Equal(4096, h.Info.MaxBlock);
        Assert.Equal(44100, h.Info.SampleRate);
        Assert.Equal(2, h.Info.Channels);
        Assert.Equal(16, h.Info.Bps);
        Assert.Equal(308700, h.Info.TotalSamples);
        Assert.Equal(7000, h.Info.DurationMs);                      // 308,700 / 44,100 = exactly 7 s
        Assert.True(h.Info.HasMd5);
        Assert.Equal(8304, h.FirstFrame);
        Assert.Equal(1, h.SeekPointCount);                          // libFLAC's default: one point at sample 0
        Assert.Equal(0, seek[0].Sample);
        Assert.Equal(0, seek[0].Offset);

        // A stream that does not say how long it is: duration 0, and the SHELL falls back to the declared length.
        Flac.Headers none = Open(Vector(NoTotalSamples), seek);
        Assert.Equal(0, none.Info.TotalSamples);
        Assert.Equal(0, none.Info.DurationMs);
        Assert.Equal(48000, none.Info.SampleRate);
    }

    [Fact]
    public void Headers_parse_the_picture_and_the_comments_of_a_tagged_vector()
    {
        byte[] bytes = Vector(Picture);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);

        Assert.Equal(3u, h.Tags.PictureKind);                       // front cover
        Assert.Equal("image/jpeg", Text(bytes, h.Tags.PictureMime));
        Assert.Equal(432368, h.Tags.PictureData.Length);
        Assert.Equal(0xFF, bytes[h.Tags.PictureData.Offset]);       // a JPEG SOI, where the range says it is
        Assert.Equal(0xD8, bytes[h.Tags.PictureData.Offset + 1]);
        Assert.True(h.Tags.PictureData.Offset + h.Tags.PictureData.Length <= bytes.Length);

        // The xiph vectors carry a vendor string and no comments, so the six keys are pinned on a spliced block:
        // the same bytes a tagged file has, in the same place, including the case and space variants of the keys.
        byte[] tagged = WithComments(bytes,
            "TITLE=Sea of Voices", "artist=Porter Robinson", "Album=Worlds", "ALBUM ARTIST=Porter Robinson",
            "DATE=2014", "TRACKNUMBER=1", "REPLAYGAIN_TRACK_GAIN=-6.66 dB");
        Flac.Headers t = Open(tagged, seek);
        Assert.Equal("Sea of Voices", Text(tagged, t.Tags.Title));
        Assert.Equal("Porter Robinson", Text(tagged, t.Tags.Artist));
        Assert.Equal("Worlds", Text(tagged, t.Tags.Album));
        Assert.Equal("Porter Robinson", Text(tagged, t.Tags.AlbumArtist));
        Assert.Equal("2014", Text(tagged, t.Tags.Date));
        Assert.Equal("1", Text(tagged, t.Tags.TrackNumber));
        Assert.Equal(3u, t.Tags.PictureKind);                       // the picture survived the splice
    }

    /// <summary>N-3 / V-PA18: the four REPLAYGAIN_* values are kept as ranges (case-insensitive keys), empty when absent, and the
    /// text they hold parses to the tag's own −18 LUFS figures through <c>Playback.Audio.ReplayGainTags</c>.</summary>
    [Fact]
    public void Headers_keep_the_four_replaygain_tags_as_ranges()
    {
        byte[] plain = Vector(Baseline);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[64];
        Flac.Headers bare = Open(plain, seek);
        Assert.True(bare.Tags.RgTrackGain.IsEmpty);
        Assert.True(bare.Tags.RgTrackPeak.IsEmpty);
        Assert.True(bare.Tags.RgAlbumGain.IsEmpty);
        Assert.True(bare.Tags.RgAlbumPeak.IsEmpty);

        byte[] tagged = WithComments(plain, "TITLE=x", "replaygain_track_gain=-6.66 dB", "REPLAYGAIN_TRACK_PEAK=0.977000",
            "ReplayGain_Album_Gain=+1.25 dB", "REPLAYGAIN_ALBUM_PEAK=1.012000");
        Flac.Headers t = Open(tagged, seek);
        Assert.Equal("-6.66 dB", Text(tagged, t.Tags.RgTrackGain));
        Assert.Equal("0.977000", Text(tagged, t.Tags.RgTrackPeak));
        Assert.Equal("+1.25 dB", Text(tagged, t.Tags.RgAlbumGain));
        Assert.Equal("1.012000", Text(tagged, t.Tags.RgAlbumPeak));

        var parsed = Playback.Audio.ReplayGainTags.Of(Bytes(tagged, t.Tags.RgTrackGain), Bytes(tagged, t.Tags.RgTrackPeak),
            Bytes(tagged, t.Tags.RgAlbumGain), Bytes(tagged, t.Tags.RgAlbumPeak));
        Assert.True(parsed.HasTrackGain);
        Assert.True(parsed.HasAlbumGain);
        Assert.Equal(-6.66f, parsed.TrackGainDb);
        Assert.Equal(0.977f, parsed.TrackPeak);
        Assert.Equal(1.25f, parsed.AlbumGainDb);
        Assert.Equal(1.012f, parsed.AlbumPeak);
    }

    static ReadOnlySpan<byte> Bytes(byte[] file, Flac.ByteRange range) => file.AsSpan(range.Offset, range.Length);

    /// <summary>N-3: with no catalogue figure, the adapter folds a LOCAL file's ReplayGain tags at +4 dB (tags are −18 LUFS, the
    /// figures everything else uses are −14) — the track pair from the track tags, the album pair from the album tags; a
    /// catalogue figure wins over tags; an untagged file keeps the (0, 0) it was built with.</summary>
    [Fact]
    public void The_adapter_folds_a_local_files_replaygain_tags_at_plus_4_db_only_when_nothing_else_carried_a_figure()
    {
        byte[] plain = Vector(Baseline);
        byte[] tagged = WithComments(plain, "REPLAYGAIN_TRACK_GAIN=-6.50 dB", "REPLAYGAIN_TRACK_PEAK=0.970000",
            "REPLAYGAIN_ALBUM_GAIN=-5.50 dB", "REPLAYGAIN_ALBUM_PEAK=0.990000");
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[64];
        var mix = new FluentGpu.Media.MixFormat(Open(tagged, seek).Info.SampleRate, 2);

        var built = new Playback.Audio.FlacAudioDecoder(0f);
        Assert.True(built.TryOpen(new MemorySource(tagged), mix, out _));
        Assert.Equal((Playback.Audio.NormalizationFigures?)new Playback.Audio.NormalizationFigures(-2.5f, 0.97f, -1.5f, 0.99f),
            ((Playback.Audio.IGainFolding)built).AppliedFigures);
        AssertFoldsItsFigures(built);

        var catalogue = new Playback.Audio.FlacAudioDecoder(-8f, 0.9f);
        Assert.True(catalogue.TryOpen(new MemorySource(tagged), mix, out _));
        Assert.Equal((Playback.Audio.NormalizationFigures?)new Playback.Audio.NormalizationFigures(-8f, 0.9f),
            ((Playback.Audio.IGainFolding)catalogue).AppliedFigures);

        var untagged = new Playback.Audio.FlacAudioDecoder(0f);
        Assert.True(untagged.TryOpen(new MemorySource(plain), mix, out _));
        Assert.Equal((Playback.Audio.NormalizationFigures?)new Playback.Audio.NormalizationFigures(0f, 0f),
            ((Playback.Audio.IGainFolding)untagged).AppliedFigures);

        // Album tags only: the track pair falls back to them rather than playing at no figure at all.
        byte[] albumOnly = WithComments(plain, "REPLAYGAIN_ALBUM_GAIN=-5.50 dB", "REPLAYGAIN_ALBUM_PEAK=0.990000");
        var fromAlbum = new Playback.Audio.FlacAudioDecoder(0f);
        Assert.True(fromAlbum.TryOpen(new MemorySource(albumOnly), mix, out _));
        Assert.Equal((Playback.Audio.NormalizationFigures?)new Playback.Audio.NormalizationFigures(-1.5f, 0.99f, -1.5f, 0.99f),
            ((Playback.Audio.IGainFolding)fromAlbum).AppliedFigures);
    }

    /// <summary>The factor a decoder folded IS the figures it reports under the settings in force — whatever they are, so the
    /// fact does not depend on another test's setting writes.</summary>
    static void AssertFoldsItsFigures(Playback.Audio.FlacAudioDecoder decoder)
    {
        var figures = ((Playback.Audio.IGainFolding)decoder).AppliedFigures!.Value;
        bool enabled = Platform.Settings.Get(Platform.Keys.NormalizationEnabled);
        var mode = Playback.Audio.ModeOf(Platform.Settings.Get(Platform.Keys.NormalizationMode));
        bool album = Platform.Settings.Get(Platform.Keys.NormalizationAlbum);
        Assert.Equal(figures.Factor(enabled, mode, album), decoder.AppliedGainLinear);
    }

    /// <summary>A byte array behind the engine's plain sequential face — a local file, as the adapter sees one.</summary>
    sealed class MemorySource(byte[] data) : FluentGpu.Media.IMediaByteSource
    {
        long _cursor;

        public long? Length => data.Length;
        public FluentGpu.Media.SourceCaps Caps => new() { Seekable = true, KnownLength = true, ExpensiveSeek = false };
        public bool TryOpen(in FluentGpu.Media.DataSpec spec) { _cursor = Math.Max(0, spec.Position); return true; }

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

    [Fact]
    public void Truncated_headers_report_incomplete_rather_than_invalid()
    {
        byte[] bytes = Vector(Picture);                             // a 432 KB PICTURE block: the grow-and-retry case
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];

        Flac.Headers cut = Flac.ParseHeaders(bytes.AsSpan(0, 40), seek);
        Assert.False(cut.Valid);
        Assert.False(cut.Complete);

        Flac.Headers window = Flac.ParseHeaders(bytes.AsSpan(0, 64 * 1024), seek);
        Assert.False(window.Valid);                                 // the picture does not fit in 64 KiB
        Assert.False(window.Complete);

        Flac.Headers whole = Flac.ParseHeaders(bytes, seek);        // grow once, read on, and it parses
        Assert.True(whole.Valid);

        Assert.False(Flac.ParseHeaders("OggS"u8, seek).Valid);      // not a FLAC at all
        Assert.False(Flac.ParseHeaders(ReadOnlySpan<byte>.Empty, seek).Valid);
    }

    [Fact]
    public void A_stream_with_no_streaminfo_is_rejected_but_still_syncs()
    {
        // xiph's "file starting at frame header" shape, made here from the baseline: the bytes from the first frame
        // on. A local file like this must be refused honestly (the toast), not crashed on — and a sync scan over it
        // still finds frames, which is what the seek probe relies on.
        byte[] bytes = Vector(Baseline);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        byte[] raw = bytes.AsSpan(h.FirstFrame).ToArray();

        Assert.False(Flac.ParseHeaders(raw, seek).Valid);
        Assert.Equal(0, Flac.FindHeader(raw, 0, h.Info, out Flac.FrameHeader first));
        Assert.Equal(0, first.SampleNumber);
        Assert.Equal(4096, first.BlockSize);
    }

    // ── 5. seeking ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Seek_with_a_seek_table_lands_on_the_frame_that_holds_the_sample_without_probing()
    {
        byte[] bytes = Vector(Baseline);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        Flac.SeekPoint[] table = BuildSeekTable(bytes, h);           // what a libFLAC 10-second table looks like

        foreach (long target in new long[] { 0, 1, 4095, 4096, 100_000, 200_000, h.Info.TotalSamples - 1 })
        {
            (int probes, long landed, int block, int[] samples) = SeekTo(bytes, h, table, target);
            Assert.Equal(0, probes);                                 // tier 1 answers without reading a window
            Assert.True(landed <= target && target < landed + block, $"target {target} landed {landed}+{block}");
            Assert.Equal(Linear(bytes, h, target, samples.Length), samples);
        }
    }

    [Fact]
    public void Seek_without_a_seek_table_lands_exactly_in_four_probes_or_fewer()
    {
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        foreach (string file in new[] { Baseline, NoSeekTable })
        {
            byte[] bytes = Vector(file);
            Flac.Headers h = Open(bytes, seek);

            foreach (double fraction in new[] { 0.0, 0.1, 0.25, 0.5, 0.7, 0.9, 0.99 })
            {
                long target = (long)(h.Info.TotalSamples * fraction);
                (int probes, long landed, int block, int[] samples) = SeekTo(bytes, h, [], target);
                Assert.True(probes <= 4, $"{file} @{fraction}: {probes} probes");
                Assert.True(landed <= target && target < landed + block, $"{file}: target {target} landed {landed}");
                Assert.Equal(Linear(bytes, h, target, samples.Length), samples);
            }
        }
    }

    [Fact]
    public void Seeking_past_the_end_runs_out_of_frames_instead_of_throwing()
    {
        byte[] bytes = Vector(NoSeekTable);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        (int probes, long landed, _, _) = SeekTo(bytes, h, [], h.Info.TotalSamples + 1_000_000);
        Assert.True(probes <= 32);
        Assert.Equal(-1, landed);                                    // no frame holds it; the SHELL reports EOF
    }

    [Fact]
    public void Estimate_offset_is_monotonic_and_clamped()
    {
        const long lo = 1000, hi = 1_000_000, loSample = 0, hiSample = 300_000;
        long previous = lo;
        for (long target = 0; target <= hiSample; target += 5_000)
        {
            long at = Flac.EstimateOffset(lo, loSample, hi, hiSample, target, 8192);
            Assert.InRange(at, lo, hi - 1);
            Assert.True(at >= previous, "the estimate must not go backwards as the target grows");
            previous = at;
        }
        // The back-off is one maximum frame, so the estimate always lands BEFORE the target's own frame.
        Assert.True(Flac.EstimateOffset(lo, loSample, hi, hiSample, hiSample / 2, 8192)
                  < lo + (hi - lo) / 2);
        // A degenerate bracket answers the low edge instead of dividing by zero.
        Assert.Equal(lo, Flac.EstimateOffset(lo, loSample, hi, loSample, 10, 0));
        Assert.Equal(lo, Flac.EstimateOffset(lo, loSample, lo, hiSample, 10, 0));
    }

    // ── 6. decorrelation, wasted bits, float conversion ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_baseline_vector_exercises_all_four_channel_assignments()
    {
        byte[] bytes = Vector(Baseline);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        Span<int> seen = stackalloc int[4];
        int pos = h.FirstFrame;
        while (pos < bytes.Length)
        {
            int at = Flac.FindHeader(bytes, pos, h.Info, out Flac.FrameHeader fh);
            if (at < 0) break;
            seen[fh.Assignment]++;
            if (dec.DecodeFrame(bytes.AsSpan(at), out int consumed, out _) != Flac.FrameResult.Ok) break;
            pos = at + consumed;
        }
        Assert.True(seen[0] > 0 && seen[1] > 0 && seen[2] > 0 && seen[3] > 0,
            $"independent {seen[0]}, left/side {seen[1]}, side/right {seen[2]}, mid/side {seen[3]}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_stereo_mode_restores_the_original_pair(int assignment)
    {
        // 40 samples takes the Vector128 path, 15 the scalar tail — including odd side values, which is where a
        // mid/side decoder that forgets the low bit goes wrong by one.
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[4];
        foreach (int n in new[] { 40, 15 })
        {
            int[] left = new int[n], right = new int[n];
            for (int i = 0; i < n; i++)
            {
                left[i] = (i * 2731 % 65536) - 32768;
                right[i] = (i * 40503 + 7) % 65536 - 32768;
            }
            byte[] file = Synthetic(44100, 16, assignment, [[left, right]], variable: n == 15,
                                    minBlock: n == 15 ? 16 : 0, maxBlock: n == 15 ? 16 : 0);
            Flac.Headers h = Open(file, seek);
            var dec = new Flac.Decoder();
            dec.Open(h.Info);
            Assert.Equal(Flac.FrameResult.Ok, dec.DecodeFrame(file.AsSpan(h.FirstFrame), out _, out Flac.Block block));
            Assert.Equal(left, block.Channel(0).ToArray());
            Assert.Equal(right, block.Channel(1).ToArray());
            using var md5 = new Flac.Md5Verifier();
            md5.Append(in block);
            Assert.True(md5.Matches(in h.Info));
        }
    }

    [Fact]
    public void Decorrelate_ignores_an_assignment_that_is_not_one()
    {
        int[] a = [1, 2, 3], b = [4, 5, 6];
        Flac.Decorrelate(0, a, b);
        Assert.Equal(new[] { 1, 2, 3 }, a);
        Assert.Equal(new[] { 4, 5, 6 }, b);
    }

    [Fact]
    public void Wasted_bits_are_shifted_back_before_the_samples_are_seen()
    {
        // Vector 14 is encoded with wasted bits: whole subframes whose samples all share low zero bits. The MD5
        // theory already proves the shift is right; this proves the shift HAPPENED — an unshifted decode would
        // have the same MD5 only if the file used no wasted bits at all.
        byte[] bytes = Vector(WastedBits);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        var dec = new Flac.Decoder();
        dec.Open(h.Info);

        int pos = h.FirstFrame, wastedRuns = 0, nonZero = 0;
        while (pos < bytes.Length)
        {
            int at = Flac.FindHeader(bytes, pos, h.Info, out _);
            if (at < 0) break;
            if (dec.DecodeFrame(bytes.AsSpan(at), out int consumed, out Flac.Block block) != Flac.FrameResult.Ok) break;
            for (int c = 0; c < block.Channels; c++)
            {
                int all = 0;
                ReadOnlySpan<int> run = block.Channel(c);
                for (int i = 0; i < run.Length; i++) all |= run[i];
                if (all != 0) { nonZero++; if ((all & 1) == 0) wastedRuns++; }
            }
            pos = at + consumed;
        }
        Assert.True(nonZero > 0);
        Assert.True(wastedRuns > 0, "no channel run had its low bit clear: the wasted-bits shift did not run");
    }

    [Fact]
    public void A_rice_escape_of_width_zero_is_silence()
    {
        // Vector 64's escaped partitions are zero-width, so whole runs of residual are zero. With a CONSTANT-zero
        // predictor that shows up as flat runs in the output; the MD5 (theory above) is the bit-exact half.
        byte[] bytes = Vector(EscapeZero);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        Assert.Equal(Flac.FrameResult.Ok, dec.DecodeFrame(bytes.AsSpan(h.FirstFrame), out _, out Flac.Block block));
        Assert.Equal(1, block.Channels);
        Assert.True(block.BlockSize > 0);
    }

    [Fact]
    public void ToFloat_scale_is_one_over_two_to_the_bps_minus_one()
    {
        // THE RULE (plan §3.8, and what libFLAC, Symphonia and go-librespot v0.8.0 all settled on): ONE scale for
        // the whole stream, 1 / 2^(bps−1). Two's complement is asymmetric, so the map is too — −2^(bps−1) lands on
        // exactly −1.0, and the largest positive sample, 2^(bps−1)−1, is one step SHORT of +1.0, at 1 − 2^(1−bps):
        // 127/128 at 8 bits, 0.99999988 at 24. Stretching the positive half to reach +1.0 would mean two different
        // scales inside one stream and would move every sample; nothing clips as it is, because ±1.0 is what the
        // graph calls full scale. `The_twenty_four_bit_vector_reaches_negative_full_scale` is this same fact read
        // off a real file, where the minimum sample IS −2^23.
        foreach (int bps in new[] { 8, 12, 16, 20, 24, 32 })
        {
            int max = (int)((1L << (bps - 1)) - 1);
            int min = (int)-(1L << (bps - 1));
            float step = 1f / (1L << (bps - 1));             // one sample step: an exact power of two
            int[] left = [max, min, 0, max];
            int[] right = [min, max, 0, 0];
            var dst = new float[8];
            Flac.ToFloat(left, right, bps, 1f, dst);
            Assert.True(dst[1] == -1f, $"{bps}-bit: −2^(bps−1) must be exactly −1.0, was {dst[1]}");
            // At 32 bits a float has no room to say "one step short" (its mantissa is 24 bits), so both sides of
            // this comparison round to exactly 1.0 — the honest answer rather than a special case.
            Assert.True(dst[0] == 1f - step, $"{bps}-bit: +full scale must be 1 − 2^(1−bps), was {dst[0]}");
            Assert.True(dst[4] == 0f && dst[5] == 0f, $"{bps}-bit: zero must stay zero");
        }

        // The normalization gain folds into the same multiply, and a long run takes the Vector128 path.
        int[] ones = new int[64], zeros = new int[64];
        for (int i = 0; i < 64; i++) ones[i] = 16384;
        var wide = new float[128];
        Flac.ToFloat(ones, zeros, 16, 0.5f, wide);
        for (int i = 0; i < 64; i++)
        {
            Assert.True(wide[i * 2] == 0.25f, $"sample {i}: {wide[i * 2]}");
            Assert.True(wide[i * 2 + 1] == 0f, $"sample {i}: {wide[i * 2 + 1]}");
        }
    }

    [Fact]
    public void The_twenty_four_bit_vector_reaches_negative_full_scale()
    {
        byte[] bytes = Vector(TwentyFourBit);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        Assert.Equal(24, h.Info.Bps);
        var dec = new Flac.Decoder();
        dec.Open(h.Info);

        int pos = h.FirstFrame, min = 0, max = 0;
        float peak = 0f;
        var scratch = new float[h.Info.MaxBlock * 2];
        while (pos < bytes.Length)
        {
            int at = Flac.FindHeader(bytes, pos, h.Info, out _);
            if (at < 0) break;
            if (dec.DecodeFrame(bytes.AsSpan(at), out int consumed, out Flac.Block block) != Flac.FrameResult.Ok) break;
            ReadOnlySpan<int> run = block.Channel(0);
            for (int i = 0; i < run.Length; i++)
            {
                if (run[i] < min) min = run[i];
                if (run[i] > max) max = run[i];
            }
            Flac.ToFloatMulti(block.Planar, block.Channels, block.BlockSize, block.Bps, 1f, scratch);
            for (int i = 0; i < block.BlockSize * 2; i++)
            {
                float a = scratch[i] < 0 ? -scratch[i] : scratch[i];
                if (a > peak) peak = a;
                Assert.False(float.IsNaN(scratch[i]));
            }
            pos = at + consumed;
        }
        Assert.Equal(-8_388_608, min);                               // exactly −2^23, measured with ffmpeg
        Assert.Equal(7_984_824, max);
        Assert.True(peak == 1f, $"the 24-bit peak must land on exactly ±1.0, was {peak}");
    }

    [Theory]
    [InlineData(ThreeChannels, 3)]
    [InlineData(SixChannels, 6)]
    [InlineData(Mono, 1)]
    public void Multichannel_and_mono_downmix_to_bounded_stereo(string file, int channels)
    {
        byte[] bytes = Vector(file);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        Assert.Equal(channels, h.Info.Channels);
        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        var dst = new float[h.Info.MaxBlock * 2];

        int pos = h.FirstFrame;
        float peak = 0f;
        int blocks = 0;
        while (pos < bytes.Length && blocks < 8)
        {
            int at = Flac.FindHeader(bytes, pos, h.Info, out _);
            if (at < 0) break;
            if (dec.DecodeFrame(bytes.AsSpan(at), out int consumed, out Flac.Block block) != Flac.FrameResult.Ok) break;
            Flac.ToFloatMulti(block.Planar, block.Channels, block.BlockSize, block.Bps, 1f, dst);
            for (int i = 0; i < block.BlockSize * 2; i++)
            {
                Assert.False(float.IsNaN(dst[i]));
                float a = dst[i] < 0 ? -dst[i] : dst[i];
                if (a > peak) peak = a;
            }
            if (channels == 1)
                for (int i = 0; i < block.BlockSize; i++)
                    Assert.True(dst[i * 2] == dst[i * 2 + 1], $"mono must land on both channels (sample {i})");
            blocks++;
            pos = at + consumed;
        }
        Assert.True(blocks > 0);
        Assert.True(peak <= 1f + 2 * 0.7071f + 1e-3f, $"downmix peak {peak}");
    }

    // ── 7. what the vendored vectors cannot say: the synthetic streams ──────────────────────────────────────────────

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Every_bit_depth_decodes_and_hashes(int bps)
    {
        int max = (int)((1L << (bps - 1)) - 1);
        int min = (int)-(1L << (bps - 1));
        int[] signal = new int[64];
        signal[0] = min; signal[1] = max; signal[2] = 0; signal[3] = -1; signal[4] = 1;
        for (int i = 5; i < 64; i++) signal[i] = (int)(((uint)(i * 2654435761) >> (32 - bps)) - (uint)(1L << (bps - 1)));

        byte[] file = Synthetic(44100, bps, 0, [[signal]], variable: false, minBlock: 0, maxBlock: 0);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[4];
        Flac.Headers h = Open(file, seek);
        Assert.Equal(bps, h.Info.Bps);

        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        Assert.Equal(Flac.FrameResult.Ok, dec.DecodeFrame(file.AsSpan(h.FirstFrame), out _, out Flac.Block block));
        Assert.Equal(signal, block.Channel(0).ToArray());
        using var md5 = new Flac.Md5Verifier();
        md5.Append(in block);
        Assert.True(md5.Matches(in h.Info), $"{bps}-bit: MD5 mismatch (the packing rounds up to {(bps + 7) / 8} bytes)");
    }

    [Fact]
    public void The_largest_block_and_the_highest_rate_decode()
    {
        int[] signal = new int[Flac.MaxBlockSize];                   // 65,535 samples, the format's ceiling
        for (int i = 0; i < signal.Length; i++) signal[i] = (i * 2731 % 65536) - 32768;

        byte[] file = Synthetic(655_350, 16, 0, [[signal]], variable: false, minBlock: 0, maxBlock: 0);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[4];
        Flac.Headers h = Open(file, seek);
        Assert.Equal(655_350, h.Info.SampleRate);
        Assert.Equal(Flac.MaxBlockSize, h.Info.MaxBlock);

        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        Assert.Equal(Flac.FrameResult.Ok, dec.DecodeFrame(file.AsSpan(h.FirstFrame), out _, out Flac.Block block));
        Assert.Equal(Flac.MaxBlockSize, block.BlockSize);
        Assert.Equal(655_350, h.Info.SampleRate);
        Assert.Equal(signal, block.Channel(0).ToArray());
    }

    [Fact]
    public void Variable_block_size_frames_carry_their_own_sample_number()
    {
        int[] a = new int[300], b = new int[128], c = new int[4096];
        for (int i = 0; i < a.Length; i++) a[i] = i - 150;
        for (int i = 0; i < b.Length; i++) b[i] = i * 3 - 50;
        for (int i = 0; i < c.Length; i++) c[i] = 7;

        byte[] file = Synthetic(48000, 16, 0, [[a], [b], [c]], variable: true, minBlock: 0, maxBlock: 0);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[4];
        Flac.Headers h = Open(file, seek);
        Assert.NotEqual(h.Info.MinBlock, h.Info.MaxBlock);           // the STREAMINFO shape of a variable stream

        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        using var md5 = new Flac.Md5Verifier();
        int pos = h.FirstFrame;
        long expected = 0;
        int frames = 0;
        foreach (int[] run in new[] { a, b, c })
        {
            // The frames are back to back, so this walks them by `consumed` rather than scanning for a sync code.
            Assert.Equal(Flac.HeaderResult.Ok, Flac.ParseFrameHeader(file.AsSpan(pos), h.Info, out Flac.FrameHeader fh));
            Assert.True(fh.Variable);
            Assert.Equal(Flac.FrameResult.Ok, dec.DecodeFrame(file.AsSpan(pos), out int consumed, out Flac.Block block));
            Assert.Equal(expected, block.SampleNumber);              // a fixed-block decoder would multiply and miss
            Assert.Equal(run.Length, block.BlockSize);
            Assert.Equal(run, block.Channel(0).ToArray());
            md5.Append(in block);
            expected += run.Length;
            frames++;
            pos += consumed;
        }
        Assert.Equal(3, frames);
        Assert.True(md5.Matches(in h.Info));
    }

    // ── 8. the allocation gate (P8) ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decoding_a_whole_vector_allocates_nothing_after_warm_up()
    {
        byte[] bytes = Vector(Baseline);                             // 76 frames, 308,700 samples, 550 KB
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        var dec = new Flac.Decoder();
        dec.Open(h.Info);

        int warm = DecodeWhole(bytes, h, dec);                       // grows the planar buffer, runs the type init
        Assert.Equal(76, warm);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int frames = DecodeWhole(bytes, h, dec);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(76, frames);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void Open_allocates_only_the_two_buffers_streaminfo_asks_for()
    {
        byte[] bytes = Vector(Baseline);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);

        var warm = new Flac.Decoder();                               // JIT and type init, off the measurement
        warm.Open(h.Info);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        long after = GC.GetAllocatedBytesForCurrentThread();
        long cost = after - before;

        // MaxBlock 4096 × 2 channels × 4 bytes = 32 KiB of planar samples, plus 32 ints of LPC coefficients, plus
        // the object itself. A 16-bit stereo open is ~33 KB and NOTHING else is allocated for the rest of the track.
        Assert.Equal(h.Info.MaxBlock * h.Info.Channels, dec.BufferInts);
        Assert.InRange(cost, 32 * 1024 + 128, 40 * 1024);

        // A second open of a stream that is no larger reuses the buffer: the SHELL pays nothing per track.
        long reopenBefore = GC.GetAllocatedBytesForCurrentThread();
        dec.Open(h.Info);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - reopenBefore);
    }

    static int DecodeWhole(byte[] bytes, in Flac.Headers h, Flac.Decoder dec)
    {
        int pos = h.FirstFrame, frames = 0;
        while (pos < bytes.Length)
        {
            int at = Flac.FindHeader(bytes, pos, h.Info, out _);
            if (at < 0) break;
            if (dec.DecodeFrame(bytes.AsSpan(at), out int consumed, out _) != Flac.FrameResult.Ok) break;
            frames++;
            pos = at + consumed;
        }
        return frames;
    }

    // ── 9. the kernels: each optimised loop equals its plain reference, below and above sixteen ─────────────────────
    //
    // Every SIMD site runs twice — `Flac.ForceScalar` off (Vector256, or Vector128 on a host without it, plus the
    // scalar tail) and on — and both runs must equal a reference computed here the slowest way, floats compared by
    // their BITS. `ForceScalar` is process-wide; flipping it can only move a concurrent decode onto a path this very
    // section proves identical. The serial kernels (Rice, LPC, FIXED, the bit reader, the CRCs, the MD5 feed) have no
    // vector twin, so their reference is the value-at-a-time form they replaced.

    /// <summary>Below the threshold (1, 7, 15), on it (16, 17), and whole blocks with a ragged tail (4096, 4099).</summary>
    static readonly int[] KernelLengths = [1, 7, 15, 16, 17, 31, 40, 4096, 4099];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Decorrelate_vector_and_scalar_paths_agree_below_and_above_sixteen(int assignment)
    {
        foreach (int n in KernelLengths)
        {
            // Channel 0 is left / side / mid and channel 1 is side / right / side: the side run carries the extra bit.
            int[] a = Noise(n, (uint)(n * 31 + assignment), assignment == 2 ? 17 : 16);
            int[] b = Noise(n, (uint)(n * 17 + assignment + 1), assignment == 2 ? 16 : 17);
            int[] refA = (int[])a.Clone(), refB = (int[])b.Clone();
            for (int i = 0; i < n; i++)
            {
                if (assignment == 1) refB[i] = refA[i] - refB[i];
                else if (assignment == 2) refA[i] = refA[i] + refB[i];
                else
                {
                    long mid = ((long)refA[i] << 1) | (refB[i] & 1L);
                    long side = refB[i];
                    refA[i] = (int)((mid + side) >> 1);
                    refB[i] = (int)((mid - side) >> 1);
                }
            }

            int[] va = (int[])a.Clone(), vb = (int[])b.Clone(), sa = (int[])a.Clone(), sb = (int[])b.Clone();
            Flac.Decorrelate((byte)assignment, va, vb);
            Flac.ForceScalar = true;
            try { Flac.Decorrelate((byte)assignment, sa, sb); }
            finally { Flac.ForceScalar = false; }

            Same(refA, va, $"vector decorrelate {assignment} ch0 n={n}");
            Same(refB, vb, $"vector decorrelate {assignment} ch1 n={n}");
            Same(refA, sa, $"scalar decorrelate {assignment} ch0 n={n}");
            Same(refB, sb, $"scalar decorrelate {assignment} ch1 n={n}");
        }
    }

    [Fact]
    public void ShiftLeft_vector_and_scalar_paths_agree_below_and_above_sixteen()
    {
        foreach (int bits in new[] { 1, 4, 15, 31, 32, 40 })
            foreach (int n in KernelLengths)
            {
                int[] src = Noise(n, (uint)(n * 11 + bits), 16);
                int[] reference = new int[n];
                for (int i = 0; i < n; i++) reference[i] = bits >= 32 ? 0 : src[i] << bits;

                int[] v = (int[])src.Clone(), s = (int[])src.Clone();
                Flac.ShiftLeft(v, bits);
                Flac.ForceScalar = true;
                try { Flac.ShiftLeft(s, bits); }
                finally { Flac.ForceScalar = false; }

                Same(reference, v, $"vector shift {bits} n={n}");
                Same(reference, s, $"scalar shift {bits} n={n}");
            }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void ToFloat_vector_and_scalar_paths_agree_to_the_bit(int bps)
    {
        foreach (float gain in new[] { 1f, 0.5f, 0.891251f })
            foreach (int n in KernelLengths)
            {
                int[] l = Noise(n, (uint)(n * 3 + bps), bps), r = Noise(n, (uint)(n * 5 + bps + 1), bps);
                float scale = gain / (float)(1L << (bps - 1));
                var reference = new float[2 * n];
                for (int i = 0; i < n; i++)
                {
                    reference[2 * i] = l[i] * scale;
                    reference[2 * i + 1] = r[i] * scale;
                }

                var v = new float[2 * n];
                var s = new float[2 * n];
                Flac.ToFloat(l, r, bps, gain, v);
                Flac.ForceScalar = true;
                try { Flac.ToFloat(l, r, bps, gain, s); }
                finally { Flac.ForceScalar = false; }

                SameBits(reference, v, $"vector ToFloat {bps}-bit gain {gain} n={n}");
                SameBits(reference, s, $"scalar ToFloat {bps}-bit gain {gain} n={n}");
            }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ToFloatMulti_vector_and_scalar_paths_agree_to_the_bit(int channels)
    {
        const int bps = 24;
        const float gain = 0.75f;
        const float k = 0.7071f;
        float scale = gain / (float)(1L << (bps - 1));
        foreach (int block in KernelLengths)
        {
            int[] planar = Noise(block * channels, (uint)(block * 7 + channels), bps);
            // The reference is the downmix of RFC 9639 §9.1.3's per-count channel order (P-4), written BY NAME so it does not share
            // the decoder's plane indexes: FL/FR at 1, FC to both sides at k, BL/SL to L and BR/SR to R at k, BC to both at 0.5,
            // LFE dropped. Each weight multiplies in the scalar path's order — (sample × scale) × k — and accumulates in the fixed
            // order FL, FC, the first surround pair, the second pair, BC, so the three paths must agree to the BIT.
            string[] names = ChannelOrder[channels];
            int Plane(string name) => Array.IndexOf(names, name);
            int fc = Plane("FC"), bc = Plane("BC");
            var pairs = new List<(int L, int R)>();
            foreach ((string l, string r) in new[] { ("BL", "BR"), ("SL", "SR") })
                if (Plane(l) >= 0) pairs.Add((Plane(l), Plane(r)));
            var reference = new float[2 * block];
            for (int i = 0; i < block; i++)
            {
                float left, right;
                if (channels == 1)
                {
                    left = right = planar[i] * scale;
                }
                else
                {
                    left = planar[Plane("FL") * block + i] * scale;
                    right = planar[Plane("FR") * block + i] * scale;
                    if (fc >= 0) { float c = planar[fc * block + i] * scale * k; left += c; right += c; }
                    foreach ((int pl, int pr) in pairs)
                    {
                        left += planar[pl * block + i] * scale * k;
                        right += planar[pr * block + i] * scale * k;
                    }
                    if (bc >= 0) { float b = planar[bc * block + i] * scale * FoldBc; left += b; right += b; }
                }
                reference[2 * i] = left;
                reference[2 * i + 1] = right;
            }

            var v = new float[2 * block];
            var s = new float[2 * block];
            Flac.ToFloatMulti(planar, channels, block, bps, gain, v);
            Flac.ForceScalar = true;
            try { Flac.ToFloatMulti(planar, channels, block, bps, gain, s); }
            finally { Flac.ForceScalar = false; }

            SameBits(reference, v, $"vector ToFloatMulti {channels} ch block={block}");
            SameBits(reference, s, $"scalar ToFloatMulti {channels} ch block={block}");
        }
    }

    [Fact]
    public void RestoreLpc_matches_the_plain_recurrence_for_every_order_and_both_accumulators()
    {
        for (int order = 1; order <= Flac.MaxLpcOrder; order++)
        {
            int[] coefs = Noise(order, (uint)(order * 13), 12);
            // 3 and 14 restored samples (under sixteen), and a whole 4096 block.
            foreach (int n in new[] { order + 3, order + 14, 4096 })
            {
                int[] input = Noise(n, (uint)(order * 7 + n), 16);
                foreach (bool wide in new[] { false, true })
                {
                    int[] expected = (int[])input.Clone();
                    for (int i = order; i < n; i++)                                   // libFLAC's "slower but clearer"
                    {
                        if (wide)
                        {
                            long sum = 0;
                            for (int j = 0; j < order; j++) sum += (long)coefs[j] * expected[i - 1 - j];
                            expected[i] += (int)(sum >> 9);
                        }
                        else
                        {
                            int sum = 0;
                            for (int j = 0; j < order; j++) sum += coefs[j] * expected[i - 1 - j];
                            expected[i] += sum >> 9;
                        }
                    }

                    int[] actual = (int[])input.Clone();
                    Flac.RestoreLpc(actual, coefs, 9, wide ? 33 : 32);
                    Same(expected, actual, $"LPC order {order} n={n} {(wide ? "64-bit" : "32-bit")} accumulator");
                }
            }
        }
    }

    [Fact]
    public void RestoreFixed_matches_the_plain_polynomials_for_orders_zero_to_four()
    {
        for (int order = 0; order <= 4; order++)
            foreach (int n in new[] { 3, 5, 15, 16, 17, 4096 })
            {
                int[] input = Noise(n, (uint)(order * 19 + n), 32);                   // full 32-bit: the wrap matters
                int[] expected = (int[])input.Clone();
                for (int i = order; i < n && order > 0; i++)
                {
                    expected[i] += order switch
                    {
                        1 => expected[i - 1],
                        2 => (int)(2L * expected[i - 1] - expected[i - 2]),
                        3 => (int)(3L * expected[i - 1] - 3L * expected[i - 2] + expected[i - 3]),
                        _ => (int)(4L * expected[i - 1] - 6L * expected[i - 2] + 4L * expected[i - 3] - expected[i - 4]),
                    };
                }

                int[] actual = (int[])input.Clone();
                Flac.RestoreFixed(actual, order);
                Same(expected, actual, $"FIXED order {order} n={n}");
            }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(20)]
    [InlineData(30)]
    public void Rice_partition_kernel_matches_the_value_at_a_time_reader(int param)
    {
        foreach (int n in new[] { 1, 5, 15, 16, 17, 1000 })
            foreach (bool trailingOnes in new[] { false, true })
            {
                int[] values = new int[n];
                uint x = (uint)(param * 977 + n) | 1;
                long range = 1L << Math.Min(param + 3, 30);
                for (int i = 0; i < n; i++)
                {
                    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                    values[i] = (int)((long)(x % (ulong)range) - range / 2);
                }
                // Quotients of 32, 63, 64 and 100 where the parameter leaves room: the kernel's tail path, and the
                // 64-bit consume `ReadUnary` has to special-case.
                int slot = 1;
                foreach (uint q in new uint[] { 32, 63, 64, 100 })
                    if (slot < n && ((ulong)q << param) + ((1UL << param) - 1) <= uint.MaxValue)
                    {
                        uint u = (uint)(((ulong)q << param) | 1);
                        values[slot++] = (int)(u >> 1) ^ -(int)(u & 1);
                    }

                var w = new BitWriter();
                foreach (int v in values)
                {
                    uint u = (uint)((v << 1) ^ (v >> 31));
                    uint q = u >> param;
                    for (uint z = 0; z < q; z++) w.Write(0, 1);
                    w.Write(1, 1);
                    w.Write(param == 0 ? 0 : u & (uint)((1L << param) - 1), param);
                }
                w.AlignToByte();
                byte[] encoded = w.ToArray();
                byte[] bytes = encoded;
                if (trailingOnes)                                  // garbage 1-bits under the cache's valid bits
                {
                    bytes = new byte[encoded.Length + 16];
                    encoded.CopyTo(bytes, 0);
                    bytes.AsSpan(encoded.Length).Fill(0xFF);
                }

                (int[] kernel, bool kernelOverrun, int kernelEnd) = RiceByKernel(bytes, param, n);
                (int[] byValue, bool valueOverrun, int valueEnd) = RiceByValue(bytes, param, n);
                Same(values, kernel, $"kernel rice k={param} n={n} trailing={trailingOnes}");
                Same(values, byValue, $"value rice k={param} n={n} trailing={trailingOnes}");
                Assert.False(kernelOverrun);
                Assert.False(valueOverrun);
                Assert.Equal(encoded.Length, kernelEnd);
                Assert.Equal(valueEnd, kernelEnd);

                if (n >= 16 && !trailingOnes)                      // a window that ends mid-partition: same zeros
                {
                    byte[] cut = encoded.AsSpan(0, encoded.Length / 2).ToArray();
                    (int[] kc, bool ko, _) = RiceByKernel(cut, param, n);
                    (int[] vc, bool vo, _) = RiceByValue(cut, param, n);
                    Assert.True(ko && vo, $"k={param} n={n}: a truncated partition must overrun");
                    Same(vc, kc, $"truncated rice k={param} n={n}");
                }
            }
    }

    static (int[] Values, bool Overrun, int End) RiceByKernel(byte[] bytes, int param, int n)
    {
        var r = new Flac.BitReader(bytes, 0);
        int[] got = new int[n];
        r.ReadRicePartition(param, got);
        bool overrun = r.Overrun;
        r.AlignToByte();
        return (got, overrun, r.BytePosition);
    }

    static (int[] Values, bool Overrun, int End) RiceByValue(byte[] bytes, int param, int n)
    {
        var r = new Flac.BitReader(bytes, 0);
        int[] got = new int[n];
        for (int i = 0; i < n; i++)
        {
            uint q = (uint)r.ReadUnary();
            uint u = (q << param) | r.Read(param);
            got[i] = (int)(u >> 1) ^ -(int)(u & 1);
        }
        bool overrun = r.Overrun;
        r.AlignToByte();
        return (got, overrun, r.BytePosition);
    }

    [Fact]
    public void Bit_reader_matches_a_bit_at_a_time_reference_over_every_width()
    {
        foreach (int length in new[] { 1, 7, 8, 9, 15, 16, 17, 64, 1000 })
        {
            byte[] bytes = new byte[length];
            uint x = (uint)length * 2654435761u | 1;
            for (int i = 0; i < length; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; bytes[i] = (byte)x; }
            for (int i = 3; i + 10 < length; i += 97) bytes.AsSpan(i, 10).Clear();   // long unary runs

            var r = new Flac.BitReader(bytes, 0);
            long bit = 0, total = 8L * length;
            for (int step = 1; ; step++)
            {
                if (step % 5 == 0)
                {
                    long at = bit;
                    while (at < total && RefBit(bytes, at) == 0) at++;
                    int zeros = r.ReadUnary();
                    if (at >= total) { Assert.True(r.Overrun, $"length {length}: unary past the end"); break; }
                    Assert.Equal((int)(at - bit), zeros);
                    bit = at + 1;
                }
                else
                {
                    int width = step * 7 % 33;                                           // 0..32, every width
                    if (bit + width > total)
                    {
                        Assert.Equal(0u, r.Read(width));
                        Assert.True(r.Overrun, $"length {length}: a {width}-bit read past the end");
                        break;
                    }
                    uint expected = 0;
                    for (int k = 0; k < width; k++) expected = (expected << 1) | (uint)RefBit(bytes, bit + k);
                    Assert.Equal(expected, r.Read(width));
                    bit += width;
                }
                Assert.False(r.Overrun);
            }

            if (length >= 3)                                                             // BytePosition after a refill
            {
                var p = new Flac.BitReader(bytes, 1);
                p.Read(3);
                p.AlignToByte();
                Assert.Equal(2, p.BytePosition);
                p.Read(8);
                Assert.Equal(3, p.BytePosition);
            }
        }
    }

    static int RefBit(byte[] b, long bit) => (b[bit >> 3] >> (7 - (int)(bit & 7))) & 1;

    [Fact]
    public void Crc_kernels_match_the_bitwise_polynomials_at_every_length_and_offset()
    {
        byte[] bytes = new byte[4200];
        uint x = 0x9E3779B9;
        for (int i = 0; i < bytes.Length; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; bytes[i] = (byte)x; }

        var lengths = new List<int> { 255, 256, 4096, 4099 };
        for (int i = 0; i <= 40; i++) lengths.Add(i);
        foreach (int start in new[] { 0, 5 })
            foreach (int length in lengths)
            {
                ReadOnlySpan<byte> b = bytes.AsSpan(start, length);
                Assert.Equal(BitwiseCrc16(b), Flac.Crc16(b));
                Assert.Equal(BitwiseCrc8(b), Flac.Crc8(b));
            }
    }

    static ushort BitwiseCrc16(ReadOnlySpan<byte> b)
    {
        int c = 0;
        foreach (byte v in b)
        {
            c ^= v << 8;
            for (int k = 0; k < 8; k++) c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x8005) & 0xFFFF : (c << 1) & 0xFFFF;
        }
        return (ushort)c;
    }

    static byte BitwiseCrc8(ReadOnlySpan<byte> b)
    {
        int c = 0;
        foreach (byte v in b)
        {
            c ^= v;
            for (int k = 0; k < 8; k++) c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) & 0xFF : (c << 1) & 0xFF;
        }
        return (byte)c;
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Md5_feed_packs_exactly_like_the_byte_at_a_time_reference(int bps)
    {
        byte[] digest = new byte[16];
        foreach (int channels in new[] { 1, 2, 6 })
            foreach (int block in new[] { 1, 15, 16, 4096 })
            {
                int[] planar = Noise(block * channels, (uint)(block * 23 + channels + bps), bps);
                int bytesPer = (bps + 7) / 8;
                var packed = new byte[block * channels * bytesPer];
                int o = 0;
                for (int i = 0; i < block; i++)
                    for (int c = 0; c < channels; c++)
                    {
                        int v = planar[c * block + i];
                        for (int k = 0; k < bytesPer; k++) packed[o++] = (byte)(v >> (8 * k));
                    }

                using var md5 = new Flac.Md5Verifier();
                md5.Append(new Flac.Block(planar, block, channels, bps, 0));
                md5.Finish(digest);
                Assert.Equal(MD5.HashData(packed), digest);
            }
    }

    [Fact]
    public void Decoding_the_baseline_vector_runs_far_faster_than_real_time()
    {
        byte[] bytes = Vector(Baseline);                             // 16/44.1 stereo, 308,700 samples, 7 s
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[8];
        Flac.Headers h = Open(bytes, seek);
        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        Assert.Equal(76, DecodeWhole(bytes, h, dec));                // JIT, type init, the planar buffer

        var clock = System.Diagnostics.Stopwatch.StartNew();
        int passes = 0;
        do
        {
            Assert.Equal(76, DecodeWhole(bytes, h, dec));
            passes++;
        } while (passes < 5 || clock.ElapsedMilliseconds < 500);
        clock.Stop();

        double perChannel = passes * h.Info.TotalSamples / clock.Elapsed.TotalSeconds;
        double realTime = perChannel / h.Info.SampleRate;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"subset/01 (16/44.1 stereo): {perChannel:N0} samples/s per channel, {perChannel * h.Info.Channels:N0} " +
            $"samples/s in all, {realTime:N0}x real time, {passes} passes in {clock.Elapsed.TotalMilliseconds:N0} ms");
        // Generous on purpose: a Debug JIT on a loaded runner is still far above this. The floor is there to catch a
        // kernel that fell off a cliff (a bounds check back in the Rice loop, a byte-wise refill), not to benchmark;
        // the printed number is the benchmark.
        Assert.True(realTime >= 10, $"decoded at only {realTime:N1}x real time ({perChannel:N0} samples/s per channel)");
    }

    // ── 10. playback smoothness (#167) wave 0: P-4, P-8, S-9, P-1 ───────────────────────────────────────────────────────
    //
    // Plan docs/plans/wavee/playback-smoothness-implementation.md §4.17 and §5. The audit's findings, in one line each:
    //   P-4  The multichannel fold-down used one channel order for every count, so a 4-channel file's BL was treated as the centre
    //        and its BR dropped, and a 7-channel file's channels were mis-ordered. RFC 9639 §9.1.3 fixes a DIFFERENT order per count.
    //   P-8  The frame sync is 15 bits (0xFF, then 0b1111100 + the blocking bit); the mask was 0xFC, so a second byte of 0xFA/0xFB
    //        — the reserved bit set — was accepted as a candidate (CRC-8, STREAMINFO and CRC-16 still rejected it, at a cost).
    //   S-9  A seek probe whose window ended inside a candidate returned "no frame" even when a whole maximum-size frame would have
    //        fitted — so a false sync near the window's start discarded the real frame behind it, and the seek decoded forward.
    //   P-1  A 32-bit stereo stream coded L/S, S/R or M/S carries its side channel at 33 bits (RFC 9639 §9.2): the 32-bit reader
    //        truncated it to full-scale garbage with a passing CRC-16. Now a 64-bit path (`ReadSignedLong`, `RestoreFixed33`,
    //        `RestoreLpc33`, `ShiftLeftWide`, `DecorrelateWide`), and `Unsupported` is no longer emitted for it.

    /// <summary>RFC 9639 §9.1.3: the channel order of each count, by name (index = channel count). 1 is mono: duplicated to both
    /// sides, no table.</summary>
    static readonly string[][] ChannelOrder =
    [
        [],
        ["M"],
        ["FL", "FR"],
        ["FL", "FR", "FC"],
        ["FL", "FR", "BL", "BR"],
        ["FL", "FR", "FC", "BL", "BR"],
        ["FL", "FR", "FC", "LFE", "BL", "BR"],
        ["FL", "FR", "FC", "LFE", "BC", "SL", "SR"],
        ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"],
    ];

    /// <summary>−3 dB for FC and every surround / side channel; 0.5 for BC, to EACH side.</summary>
    const float FoldK = 0.7071f, FoldBc = 0.5f;

    /// <summary>What a channel contributes to (left, right) in the stereo downmix.</summary>
    static (float L, float R) FoldWeight(string name) => name switch
    {
        "FL" => (1f, 0f),
        "FR" => (0f, 1f),
        "FC" => (FoldK, FoldK),
        "BL" or "SL" => (FoldK, 0f),
        "BR" or "SR" => (0f, FoldK),
        "BC" => (FoldBc, FoldBc),
        _ => (0f, 0f),                                                       // LFE is dropped
    };

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Every_channel_lands_on_the_sides_its_rfc_position_says(int channels)
    {
        // One channel at half scale and the rest silent, per channel: where does it come out? The decoder's table is the thing
        // under test, so the expectation is built from the NAMES in the RFC's order, not from plane indexes.
        string[] names = ChannelOrder[channels];
        Assert.Equal(channels, names.Length);
        const int bps = 24, block = 4, level = 1 << 22;                      // 0.5 of full scale at 24 bit
        for (int c = 0; c < channels; c++)
        {
            var planar = new int[channels * block];
            planar[c * block] = level;
            var dst = new float[2 * block];
            Flac.ToFloatMulti(planar, channels, block, bps, 1f, dst);

            (float wl, float wr) = FoldWeight(names[c]);
            Assert.True(Math.Abs(0.5f * wl - dst[0]) < 1e-6f, $"{channels} ch: {names[c]} (plane {c}) came out {dst[0]} on the left, expected {0.5f * wl}");
            Assert.True(Math.Abs(0.5f * wr - dst[1]) < 1e-6f, $"{channels} ch: {names[c]} (plane {c}) came out {dst[1]} on the right, expected {0.5f * wr}");
            for (int i = 2; i < dst.Length; i++) Assert.True(dst[i] == 0f, $"{channels} ch: {names[c]} leaked into sample {i / 2}");
        }
    }

    [Fact]
    public void The_four_and_seven_channel_layouts_are_not_the_old_orders()
    {
        // The two counts the old single table got wrong, stated directly. 4 = FL FR BL BR: plane 2 is BL (left only, NOT a centre).
        var four = new int[4 * 4];
        four[2 * 4] = 1 << 22;
        var dst = new float[8];
        Flac.ToFloatMulti(four, 4, 4, 24, 1f, dst);
        Assert.True(dst[0] > 0.3f && dst[1] == 0f, $"4 ch: BL must reach the left only (was a centre): L {dst[0]} R {dst[1]}");
        Array.Clear(four);
        four[3 * 4] = 1 << 22;                                              // BR: was DROPPED
        Flac.ToFloatMulti(four, 4, 4, 24, 1f, dst);
        Assert.True(dst[1] > 0.3f && dst[0] == 0f, $"4 ch: BR must reach the right only (was dropped): L {dst[0]} R {dst[1]}");

        // 7 = FL FR FC LFE BC SL SR: plane 3 is the LFE (dropped), plane 4 the back CENTRE (0.5 to each side).
        var seven = new int[7 * 4];
        seven[3 * 4] = 1 << 22;
        Flac.ToFloatMulti(seven, 7, 4, 24, 1f, dst);
        Assert.True(dst[0] == 0f && dst[1] == 0f, "7 ch: the LFE is dropped");
        Array.Clear(seven);
        seven[4 * 4] = 1 << 22;
        Flac.ToFloatMulti(seven, 7, 4, 24, 1f, dst);
        Assert.True(Math.Abs(dst[0] - 0.25f) < 1e-6f && Math.Abs(dst[1] - 0.25f) < 1e-6f, $"7 ch: BC is 0.5 to each side: L {dst[0]} R {dst[1]}");
    }

    // P-8: the sync code is fifteen bits.

    [Theory]
    [InlineData(0xF8, true)]                                                // fixed blocking
    [InlineData(0xF9, true)]                                                // variable blocking
    [InlineData(0xFA, false)]                                               // the reserved bit set: the 0xFC mask accepted these two
    [InlineData(0xFB, false)]
    [InlineData(0xFC, false)]
    [InlineData(0xFE, false)]
    [InlineData(0xFF, false)]
    [InlineData(0xF0, false)]
    public void The_sync_code_is_fifteen_bits_not_fourteen(int second, bool accepted)
    {
        var si = new Flac.StreamInfo { MinBlock = 16, MaxBlock = 4096, SampleRate = 44100, Channels = 2, Bps = 16 };
        byte[] header = HandHeader(12, 9, 1, 4, 0);
        header[1] = (byte)second;
        header[^1] = (byte)Flac.Crc8(header.AsSpan(0, header.Length - 1));    // a VALID CRC-8: only the sync code is wrong

        Assert.Equal(accepted, Flac.LooksLikeHeader(header));
        Flac.HeaderResult parsed = Flac.ParseFrameHeader(header, si, out _);
        Assert.Equal(accepted ? Flac.HeaderResult.Ok : Flac.HeaderResult.NotSync, parsed);
        Assert.Equal(accepted ? 0 : -1, Flac.FindHeader(header, 0, si, out _));
    }

    // S-9: a probe window that ends inside a candidate.

    /// <summary>The probe fixture: a REAL frame (the third of a 4-frame variable-blocking file, 64 samples each), STREAMINFO with
    /// its block ceiling raised to 4096 (so a hand-built 4096-sample header is "consistent"), and a false candidate: a header
    /// with a valid CRC-8 whose first subframe is a VERBATIM one that would need 8 KiB — it can only ever overrun a window.</summary>
    static (Flac.StreamInfo Si, byte[] Real, long RealSample, byte[] FalseHeader, int RealFrameBytes) ProbeFixture(uint maxFrame)
    {
        int[][][] frames = new int[4][][];
        for (int f = 0; f < frames.Length; f++)
            frames[f] = [Noise(64, (uint)(f * 11 + 3), 16), Noise(64, (uint)(f * 17 + 5), 16)];
        byte[] file = Synthetic(44100, 16, 0, frames, variable: true, minBlock: 0, maxBlock: 0);
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[4];
        Flac.Headers h = Open(file, seek);

        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        int at = FrameOffset(file, h, dec, 2);
        Assert.Equal(Flac.FrameResult.Ok, dec.DecodeFrame(file.AsSpan(at), out int length, out Flac.Block block));
        Flac.StreamInfo si = h.Info;
        si.MaxBlock = 4096;
        si.MaxFrame = maxFrame;
        return (si, file.AsSpan(at, length).ToArray(), block.SampleNumber, HandHeader(12, 9, 1, 4, 0), length);
    }

    /// <summary>[false header][0x02 0x00 …: a VERBATIM 16-bit subframe head and filler][the real frame][tail zeros].</summary>
    static byte[] ProbeWindow(byte[] falseHeader, byte[] real, int gap, int tail)
    {
        var window = new byte[falseHeader.Length + gap + real.Length + tail];
        falseHeader.CopyTo(window, 0);
        window[falseHeader.Length] = 0x02;                                    // pad 0, type 000001 (VERBATIM), no wasted bits
        real.CopyTo(window, falseHeader.Length + gap);
        return window;
    }

    [Fact]
    public void A_false_sync_that_runs_off_the_window_with_room_to_spare_is_skipped_and_the_real_frame_behind_it_is_found()
    {
        var (si, real, realSample, falseHeader, realBytes) = ProbeFixture(maxFrame: 0);
        si.MaxFrame = (uint)realBytes + 32;                                  // a plausible STREAMINFO ceiling: ~300 bytes
        const int gap = 24;
        byte[] window = ProbeWindow(falseHeader, real, gap, tail: 64);
        Assert.True(window.Length >= si.MaxFrame, "fixture: the window holds a whole maximum-size frame from the false candidate");

        var dec = new Flac.Decoder();
        dec.Open(si);
        Assert.Equal(Flac.FrameResult.Overrun, dec.DecodeFrame(window, out _, out _));   // the candidate IS an overrun…

        Flac.SeekPlan plan = Flac.BeginSeek(si, Array.Empty<Flac.SeekPoint>(), 0, 1_000_000, realSample + 5);
        Flac.ProbeResult result = Flac.Observe(ref plan, dec, window, windowOffset: 5_000);

        // …and with a whole frame's room behind it, a REAL frame could not have overrun: it is a false sync (the CRC-16 would have
        // said so had the window been longer). Before S-9 this was NoFrame and the seek decoded forward from the bracket.
        Assert.Equal(Flac.ProbeResult.Found, result);
        Assert.Equal(5_000L + falseHeader.Length + gap, plan.Offset);
        Assert.Equal(realSample, plan.Sample);
        Assert.True(plan.Resolved);
    }

    [Fact]
    public void A_candidate_with_fewer_than_max_frame_bytes_left_is_no_frame_because_a_real_frame_could_be_cut()
    {
        var (si, real, _, falseHeader, realBytes) = ProbeFixture(maxFrame: 0);
        si.MaxFrame = (uint)realBytes + 32;
        byte[] window = ProbeWindow(falseHeader, real, gap: 24, tail: 64);
        byte[] shortened = window.AsSpan(0, (int)si.MaxFrame - 1).ToArray();   // one byte short of a whole maximum-size frame

        var dec = new Flac.Decoder();
        dec.Open(si);
        Flac.SeekPlan plan = Flac.BeginSeek(si, Array.Empty<Flac.SeekPoint>(), 0, 1_000_000, 100);
        Assert.Equal(Flac.ProbeResult.NoFrame, Flac.Observe(ref plan, dec, shortened, windowOffset: 0));   // the caller reads a bigger or later window
        Assert.Equal(0, plan.Probes);                                         // (Observe never counts probes: TryNextProbe does)
    }

    [Fact]
    public void A_real_frame_cut_by_the_window_is_no_frame_and_a_whole_one_is_found()
    {
        var (si, real, realSample, _, realBytes) = ProbeFixture(maxFrame: 0);
        si.MaxFrame = (uint)realBytes + 32;
        var dec = new Flac.Decoder();
        dec.Open(si);

        Flac.SeekPlan cutPlan = Flac.BeginSeek(si, Array.Empty<Flac.SeekPoint>(), 0, 1_000_000, realSample);
        Assert.Equal(Flac.ProbeResult.NoFrame, Flac.Observe(ref cutPlan, dec, real.AsSpan(0, real.Length / 2), windowOffset: 0));

        Flac.SeekPlan wholePlan = Flac.BeginSeek(si, Array.Empty<Flac.SeekPoint>(), 0, 1_000_000, realSample);
        Assert.Equal(Flac.ProbeResult.Found, Flac.Observe(ref wholePlan, dec, real, windowOffset: 777));
        Assert.Equal(777L, wholePlan.Offset);
        Assert.Equal(realSample, wholePlan.Sample);
    }

    [Fact]
    public void An_unknown_max_frame_makes_every_overrun_skippable()
    {
        // STREAMINFO's max frame size is 0 when the encoder did not write it (xiph vector 46): then there is no "a whole frame would
        // have fitted" to measure, and an Overrun is a false sync like any other.
        var (si, real, realSample, falseHeader, _) = ProbeFixture(maxFrame: 0);
        Assert.Equal(0u, si.MaxFrame);
        byte[] window = ProbeWindow(falseHeader, real, gap: 24, tail: 64);

        var dec = new Flac.Decoder();
        dec.Open(si);
        Flac.SeekPlan plan = Flac.BeginSeek(si, Array.Empty<Flac.SeekPoint>(), 0, 1_000_000, realSample + 5);
        Assert.Equal(Flac.ProbeResult.Found, Flac.Observe(ref plan, dec, window, windowOffset: 0));
        Assert.Equal((long)(falseHeader.Length + 24), plan.Offset);
    }

    // P-1: the 33-bit side channel of a 32-bit stereo stream.

    /// <summary>A 32-bit STEREO stream of ONE frame with the side channel written at its true 33 bits — what the suite's
    /// <see cref="Synthetic"/> cannot do (it computes the side in <c>int</c>, which wraps, and writes at most 32 bits).
    /// <paramref name="assignment"/> 1/2/3 = left/side, side/right, mid/side. The side is VERBATIM (33 bits) or, with
    /// <paramref name="fixedSide"/>, a FIXED order-1 predictor whose residuals are one escaped partition of 16-bit values (so the
    /// data must be a slow ramp: side[i] − side[i−1] fits 16 bits) — the wide path's <c>RestoreFixed33</c>.</summary>
    static byte[] SyntheticWide(int assignment, int[] left, int[] right, bool fixedSide)
    {
        int n = left.Length;
        var side = new long[n];
        for (int i = 0; i < n; i++) side[i] = (long)left[i] - right[i];       // −(2^32 − 1) .. 2^32 − 1: 33 bits

        var packed = new byte[n * 8];
        for (int i = 0; i < n; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(i * 8), left[i]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(i * 8 + 4), right[i]);
        }
        byte[] md5 = MD5.HashData(packed);

        var info = new BitWriter();
        info.Write((uint)n, 16);
        info.Write((uint)n, 16);
        info.Write(0, 24);
        info.Write(0, 24);
        info.Write(44100, 20);
        info.Write(1, 3);                                                    // two channels
        info.Write(31, 5);                                                   // 32 bits per sample
        info.Write(0, 4);
        info.Write((uint)n, 32);

        var output = new List<byte>();
        Push(output, "fLaC"u8);
        output.Add(0x80);
        output.Add(0);
        output.Add(0);
        output.Add(Flac.StreamInfoBytes);
        Push(output, info.ToArray());
        Push(output, md5);

        var w = new BitWriter();
        w.Write(0xFF, 8);
        w.Write(0xF8, 8);                                                    // fixed blocking
        w.Write(0x70, 8);                                                    // block size code 7, rate from STREAMINFO
        w.Write((uint)(assignment + 7) << 4, 8);                             // 8 = L/S, 9 = S/R, 10 = M/S; depth from STREAMINFO
        WriteCodedNumber(w, 0);
        w.Write((uint)(n - 1) >> 8, 8);
        w.Write((uint)(n - 1) & 0xFF, 8);
        w.Write(Flac.Crc8(w.ToArray()), 8);

        void Narrow(int[] samples)
        {
            w.Write(0, 1);
            w.Write(1, 6);                                                   // VERBATIM
            w.Write(0, 1);
            foreach (int s in samples) w.WriteSigned(s, 32);
        }

        void Side()
        {
            w.Write(0, 1);
            if (!fixedSide)
            {
                w.Write(1, 6);                                               // VERBATIM, 33 bits per sample
                w.Write(0, 1);
                foreach (long s in side) w.WriteLong(s, 33);
                return;
            }
            w.Write(9, 6);                                                   // FIXED, order 1
            w.Write(0, 1);
            w.WriteLong(side[0], 33);                                        // the warm-up
            w.Write(0, 2);                                                   // Rice method 0
            w.Write(0, 4);                                                   // partition order 0: one partition
            w.Write(15, 4);                                                  // the escape code: raw residuals…
            w.Write(16, 5);                                                  // …of 16 bits
            for (int i = 1; i < n; i++) w.WriteSigned(checked((int)(side[i] - side[i - 1])), 16);
        }

        switch (assignment)
        {
            case 1: Narrow(left); Side(); break;
            case 2: Side(); Narrow(right); break;
            default:
                var mid = new int[n];
                for (int i = 0; i < n; i++) mid[i] = (int)(((long)left[i] + right[i]) >> 1);
                Narrow(mid);
                Side();
                break;
        }
        w.AlignToByte();
        w.Write(Flac.Crc16(w.ToArray()), 16);
        Push(output, w.ToArray());
        return output.ToArray();
    }

    /// <summary>Full-scale 32-bit extremes first (both signs of the 33-bit side: ±(2^32 − 1)), then noise across the whole range.</summary>
    static (int[] Left, int[] Right) FullScalePair(int n)
    {
        int[] l = Noise(n, 101, 32), r = Noise(n, 202, 32);
        l[0] = int.MinValue; r[0] = int.MaxValue;                            // side = −(2^32 − 1)
        l[1] = int.MaxValue; r[1] = int.MinValue;                            // side = 2^32 − 1
        l[2] = int.MinValue; r[2] = int.MinValue;                            // side = 0
        l[3] = int.MaxValue; r[3] = int.MaxValue;
        l[4] = 0; r[4] = int.MinValue;                                       // side = 2^31: the first value an int cannot hold
        return (l, r);
    }

    /// <summary>A slow ramp whose side channel sits near +2^32 (the 33rd bit is set from the first sample) and whose
    /// sample-to-sample step is 1000: a FIXED order-1 side channel with 16-bit residuals.</summary>
    static (int[] Left, int[] Right) RampPair(int n)
    {
        var l = new int[n];
        var r = new int[n];
        for (int i = 0; i < n; i++)
        {
            r[i] = -1_950_000_000 - 100 * i;
            l[i] = 2_050_000_000 + 900 * i;                                   // side = 4_000_000_000 + 1000 i
        }
        return (l, r);
    }

    [Theory]
    [InlineData(1, false, false)]               // full-scale noise, VERBATIM 33-bit side: left/side
    [InlineData(2, false, false)]               // side/right
    [InlineData(3, false, false)]               // mid/side
    [InlineData(1, true, false)]                // a ramp near +2^32, VERBATIM side
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(1, true, true)]                 // the same ramp, FIXED order-1 side: RestoreFixed33 inside DecodeFrame
    [InlineData(2, true, true)]
    [InlineData(3, true, true)]
    public void A_32_bit_stereo_stream_decodes_bit_exactly_through_the_33_bit_side_channel(int assignment, bool ramp, bool fixedSide)
    {
        if (fixedSide) Assert.True(ramp, "fixture: the FIXED side needs the slow ramp (16-bit residuals)");
        const int n = 64;
        (int[] left, int[] right) = ramp ? RampPair(n) : FullScalePair(n);
        byte[] file = SyntheticWide(assignment, left, right, fixedSide);

        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[4];
        Flac.Headers h = Open(file, seek);
        Assert.Equal(32, h.Info.Bps);
        Assert.Equal(2, h.Info.Channels);

        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        Assert.Equal(n, dec.WideLongs);                                       // the 33-bit block exists for exactly this shape

        Flac.FrameResult result = dec.DecodeFrame(file.AsSpan(h.FirstFrame), out int consumed, out Flac.Block block);
        Assert.Equal(Flac.FrameResult.Ok, result);                            // not Unsupported (and not garbage with a passing CRC)
        Assert.Equal(file.Length - h.FirstFrame, consumed);
        Assert.Equal(left, block.Channel(0).ToArray());
        Assert.Equal(right, block.Channel(1).ToArray());

        using var md5 = new Flac.Md5Verifier();
        md5.Append(in block);
        Assert.True(md5.Matches(in h.Info), $"assignment {assignment}, ramp={ramp}, fixed={fixedSide}: MD5 mismatch");
    }

    [Fact]
    public void The_wide_block_exists_only_for_a_32_bit_stereo_stream()
    {
        Span<Flac.SeekPoint> seek = stackalloc Flac.SeekPoint[4];
        int[] noise = Noise(64, 7, 32);
        int[] noise2 = Noise(64, 8, 32);

        // 32-bit stereo (even with INDEPENDENT channels: the shape decides, the assignment is not known at Open): a block's worth.
        Flac.Headers stereo32 = Open(Synthetic(44100, 32, 0, [[noise, noise2]], variable: false, minBlock: 0, maxBlock: 0), seek);
        var wide = new Flac.Decoder();
        wide.Open(stereo32.Info);
        Assert.Equal(64, wide.WideLongs);

        // 32-bit MONO: no side channel.
        Flac.Headers mono32 = Open(Synthetic(44100, 32, 0, [[noise]], variable: false, minBlock: 0, maxBlock: 0), seek);
        var mono = new Flac.Decoder();
        mono.Open(mono32.Info);
        Assert.Equal(0, mono.WideLongs);

        // 24-bit stereo: the side channel fits 25 bits, an int is enough.
        Flac.Headers stereo24 = Open(Synthetic(44100, 24, 0, [[Noise(64, 9, 24), Noise(64, 10, 24)]], variable: false, minBlock: 0, maxBlock: 0), seek);
        var narrow = new Flac.Decoder();
        narrow.Open(stereo24.Info);
        Assert.Equal(0, narrow.WideLongs);

        // A real 16-bit stereo vector.
        byte[] bytes = Vector(Baseline);
        Flac.Headers cd = Open(bytes, stackalloc Flac.SeekPoint[64]);
        var cdDecoder = new Flac.Decoder();
        cdDecoder.Open(cd.Info);
        Assert.Equal(0, cdDecoder.WideLongs);

        // Independent 32-bit stereo still decodes (the narrow path: no side channel in the frame).
        byte[] independent = Synthetic(44100, 32, 0, [[noise, noise2]], variable: false, minBlock: 0, maxBlock: 0);
        Assert.Equal(Flac.FrameResult.Ok, wide.DecodeFrame(independent.AsSpan(stereo32.FirstFrame), out _, out Flac.Block block));
        Assert.Equal(noise, block.Channel(0).ToArray());
        Assert.Equal(noise2, block.Channel(1).ToArray());
    }

    [Fact]
    public void ReadSignedLong_sign_extends_every_width_from_one_to_sixty_four_bits()
    {
        byte[] bytes = new byte[256];
        uint x = 0xC0FFEE | 1;
        for (int i = 0; i < bytes.Length; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; bytes[i] = (byte)x; }
        bytes.AsSpan(40, 16).Fill(0xFF);                                      // all-ones: −1 at every width
        bytes.AsSpan(60, 16).Clear();

        var r = new Flac.BitReader(bytes, 0);
        long bit = 0;
        int[] widths = [33, 1, 33, 2, 40, 5, 33, 64, 34, 7, 33, 63, 32, 33, 17, 33];
        for (int round = 0; round < 5; round++)
            foreach (int width in widths)
            {
                if (bit + width > 8L * bytes.Length) return;
                ulong raw = 0;
                for (int k = 0; k < width; k++) raw = (raw << 1) | (uint)RefBit(bytes, bit + k);
                long expected = width == 64 ? (long)raw : (long)(raw << (64 - width)) >> (64 - width);
                Assert.Equal(expected, r.ReadSignedLong(width));
                Assert.False(r.Overrun);
                bit += width;
            }
    }

    [Fact]
    public void RestoreFixed33_matches_the_plain_polynomials_in_64_bit_for_orders_zero_to_four()
    {
        for (int order = 0; order <= 4; order++)
            foreach (int n in new[] { 5, 15, 16, 17, 300 })
            {
                var res = Noise(n, (uint)(order * 31 + n), 16);                // 16-bit residuals…
                var start = new long[n];
                long[] warm = [4_294_967_295L, -4_294_967_296L, 2_147_483_648L, -2_147_483_649L];   // …on 33-bit warm-ups
                for (int i = 0; i < order; i++) start[i] = warm[i];

                var expected = (long[])start.Clone();
                for (int i = order; i < n; i++)
                {
                    long prediction = order switch
                    {
                        0 => 0,
                        1 => expected[i - 1],
                        2 => 2 * expected[i - 1] - expected[i - 2],
                        3 => 3 * expected[i - 1] - 3 * expected[i - 2] + expected[i - 3],
                        _ => 4 * expected[i - 1] - 6 * expected[i - 2] + 4 * expected[i - 3] - expected[i - 4],
                    };
                    expected[i] = res[i] + prediction;
                }

                var actual = (long[])start.Clone();
                Flac.RestoreFixed33(actual, res, order);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"RestoreFixed33 order {order} n={n}");
            }
    }

    [Fact]
    public void RestoreLpc33_matches_the_plain_recurrence_in_64_bit_for_every_order()
    {
        for (int order = 1; order <= Flac.MaxLpcOrder; order++)
            foreach (int n in new[] { order + 3, order + 14, 300 })
            {
                int[] coefs = Noise(order, (uint)(order * 13 + n), 12);
                var res = Noise(n, (uint)(order * 7 + n), 16);
                var start = new long[n];
                for (int i = 0; i < order; i++) start[i] = (i & 1) == 0 ? 4_294_967_295L - i : -4_294_967_296L + i;   // 33-bit warm-ups

                var expected = (long[])start.Clone();
                for (int i = order; i < n; i++)
                {
                    long sum = 0;
                    for (int j = 0; j < order; j++) sum += (long)coefs[j] * expected[i - 1 - j];
                    expected[i] = res[i] + (sum >> 9);
                }

                var actual = (long[])start.Clone();
                Flac.RestoreLpc33(actual, res, coefs, 9);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"RestoreLpc33 order {order} n={n}");
            }
    }

    [Fact]
    public void ShiftLeftWide_shifts_in_64_bit_for_every_wasted_bit_count_a_33_bit_channel_allows()
    {
        // wasted ≥ bps is rejected by the subframe header, and bps is 33 here: 1..32 wasted bits.
        for (int bits = 0; bits <= 32; bits++)
        {
            long[] input = [1, -1, 0, 3, -3, 0x7FFF_FFFF, -0x8000_0000L, 1L << 31];
            var expected = new long[input.Length];
            for (int i = 0; i < input.Length; i++) expected[i] = input[i] << bits;
            var actual = (long[])input.Clone();
            Flac.ShiftLeftWide(actual, bits);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"ShiftLeftWide by {bits}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DecorrelateWide_restores_the_original_pair_at_full_scale(int assignment)
    {
        (int[] left, int[] right) = FullScalePair(64);
        int n = left.Length;
        var side = new long[n];
        for (int i = 0; i < n; i++) side[i] = (long)left[i] - right[i];

        var ch0 = new int[n];
        var ch1 = new int[n];
        for (int i = 0; i < n; i++)
            switch (assignment)
            {
                case 1: ch0[i] = left[i]; ch1[i] = 0x5A5A5A5A; break;                   // L/S: the narrow channel is the left
                case 2: ch0[i] = 0x5A5A5A5A; ch1[i] = right[i]; break;                  // S/R: the narrow channel is the right
                default: ch0[i] = (int)(((long)left[i] + right[i]) >> 1); ch1[i] = 0x5A5A5A5A; break;   // M/S: mid
            }

        Flac.DecorrelateWide((byte)assignment, ch0, ch1, side);

        Assert.Equal(left, ch0);
        Assert.Equal(right, ch1);

        // Anything that is not 1..3 is not a decorrelation: a no-op.
        var a = new[] { 1, 2, 3 };
        var b = new[] { 4, 5, 6 };
        Flac.DecorrelateWide(0, a, b, new long[] { 9, 9, 9 });
        Flac.DecorrelateWide(4, a, b, new long[] { 9, 9, 9 });
        Assert.Equal(new[] { 1, 2, 3 }, a);
        Assert.Equal(new[] { 4, 5, 6 }, b);
    }

    /// <summary>Deterministic xorshift noise, signed, spanning the whole <paramref name="bps"/>-bit range.</summary>
    static int[] Noise(int n, uint seed, int bps)
    {
        var v = new int[n];
        uint x = seed * 2654435761u | 1;
        for (int i = 0; i < n; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            v[i] = bps >= 32 ? (int)x : (int)(x >> (32 - bps)) - (1 << (bps - 1));
        }
        return v;
    }

    static void Same(ReadOnlySpan<int> expected, ReadOnlySpan<int> actual, string what)
    {
        Assert.True(expected.Length == actual.Length, what + ": length differs");
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i]) Assert.Fail($"{what}: [{i}] is {actual[i]}, expected {expected[i]}");
    }

    static void SameBits(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string what)
    {
        Assert.True(expected.Length == actual.Length, what + ": length differs");
        for (int i = 0; i < expected.Length; i++)
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
                Assert.Fail($"{what}: [{i}] is {actual[i]:R}, expected {expected[i]:R}");
    }

    // ── helpers: the seek driver, the tag splice, and a minimal FLAC encoder ────────────────────────────────────────

    /// <summary>What the SHELL does around <see cref="Flac.BeginSeek"/>: probe with a 64 KiB window, then decode
    /// forward to the frame that holds the target. Answers the probe count and the target's own samples.</summary>
    static (int Probes, long Landed, int Block, int[] Samples) SeekTo(byte[] file, in Flac.Headers h,
                                                                     Flac.SeekPoint[] table, long target)
    {
        const int Window = 64 * 1024;
        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        Flac.SeekPlan plan = Flac.BeginSeek(h.Info, table, h.FirstFrame, file.Length, target);
        while (Flac.TryNextProbe(ref plan, out long offset))
        {
            int start = (int)offset;
            int length = Math.Min(Window, file.Length - start);
            if (length <= 0) break;
            if (Flac.Observe(ref plan, dec, file.AsSpan(start, length), offset) == Flac.ProbeResult.NoFrame) break;
        }

        long pos = plan.Offset;
        while (pos < file.Length)
        {
            int at = Flac.FindHeader(file, (int)pos, h.Info, out _);
            if (at < 0) break;
            if (dec.DecodeFrame(file.AsSpan(at), out int consumed, out Flac.Block block) != Flac.FrameResult.Ok)
            {
                pos = at + 1;
                continue;
            }
            if (block.SampleNumber <= target && target < block.SampleNumber + block.BlockSize)
            {
                int from = (int)(target - block.SampleNumber);
                int take = Math.Min(64, block.BlockSize - from);
                return (plan.Probes, block.SampleNumber, block.BlockSize, block.Channel(0).Slice(from, take).ToArray());
            }
            pos = at + consumed;
        }
        return (plan.Probes, -1, 0, []);
    }

    /// <summary>The same samples, reached by decoding from the very beginning — the seek's answer must equal it.</summary>
    static int[] Linear(byte[] file, in Flac.Headers h, long from, int count)
    {
        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        var got = new int[count];
        int filled = 0, pos = h.FirstFrame;
        while (pos < file.Length && filled < count)
        {
            int at = Flac.FindHeader(file, pos, h.Info, out _);
            if (at < 0) break;
            if (dec.DecodeFrame(file.AsSpan(at), out int consumed, out Flac.Block block) != Flac.FrameResult.Ok) break;
            long end = block.SampleNumber + block.BlockSize;
            if (end > from)
            {
                int start = (int)Math.Max(0, from - block.SampleNumber);
                ReadOnlySpan<int> run = block.Channel(0);
                for (int i = start; i < run.Length && filled < count; i++) got[filled++] = run[i];
            }
            pos = at + consumed;
        }
        return got;
    }

    /// <summary>A seek table with one point per frame — what libFLAC writes for a local file, at the finest grain.</summary>
    static Flac.SeekPoint[] BuildSeekTable(byte[] file, in Flac.Headers h)
    {
        var dec = new Flac.Decoder();
        dec.Open(h.Info);
        var points = new List<Flac.SeekPoint>();
        int pos = h.FirstFrame;
        while (pos < file.Length)
        {
            int at = Flac.FindHeader(file, pos, h.Info, out _);
            if (at < 0) break;
            if (dec.DecodeFrame(file.AsSpan(at), out int consumed, out Flac.Block block) != Flac.FrameResult.Ok) break;
            points.Add(new Flac.SeekPoint(block.SampleNumber, at - h.FirstFrame, (ushort)block.BlockSize));
            pos = at + consumed;
        }
        return points.ToArray();
    }

    static int FrameOffset(byte[] file, in Flac.Headers h, Flac.Decoder dec, int index)
    {
        int pos = h.FirstFrame;
        for (int i = 0; ; i++)
        {
            int at = Flac.FindHeader(file, pos, h.Info, out _);
            Assert.True(at >= 0);
            if (i == index) return at;
            Assert.Equal(Flac.FrameResult.Ok, dec.DecodeFrame(file.AsSpan(at), out int consumed, out _));
            pos = at + consumed;
        }
    }

    /// <summary>A six-byte frame header with the four code fields set by hand and a real CRC-8 — the only way to ask
    /// the parser about a code no vendored file happens to use. Codes that carry an extension byte (block 6/7, rate
    /// 12/13/14) are not built here; the vectors and <see cref="Synthetic"/> cover those.</summary>
    static byte[] HandHeader(int blockCode, int rateCode, int chanCode, int bpsCode, int number)
    {
        var w = new BitWriter();
        w.Write(0xFF, 8);
        w.Write(0xF8, 8);                                                    // fixed blocking
        w.Write((uint)((blockCode << 4) | rateCode), 8);
        w.Write((uint)((chanCode << 4) | (bpsCode << 1)), 8);                // the low bit is the reserved zero
        WriteCodedNumber(w, (ulong)number);
        w.Write(Flac.Crc8(w.ToArray()), 8);
        return w.ToArray();
    }

    static string Text(byte[] file, Flac.ByteRange range)
        => System.Text.Encoding.UTF8.GetString(file, range.Offset, range.Length);

    /// <summary>Splice a VORBIS_COMMENT block carrying these fields in after the file's STREAMINFO — where a tagged
    /// file keeps it, so the result is a valid stream and not the "metadata before STREAMINFO" faulty shape.</summary>
    static byte[] WithComments(byte[] file, params string[] comments)
    {
        var body = new List<byte>();
        AddLe(body, 11);                                                     // the vendor string's length
        Push(body, "Wavee.Tests"u8);
        AddLe(body, (uint)comments.Length);
        foreach (string comment in comments)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(comment);
            AddLe(body, (uint)utf8.Length);
            Push(body, utf8);
        }

        const int AfterStreamInfo = 4 + 4 + Flac.StreamInfoBytes;            // "fLaC" + the block header + 34 bytes
        var output = new List<byte>();
        Push(output, file.AsSpan(0, AfterStreamInfo));
        output.Add((byte)Flac.BlockType.VorbisComment);                      // not the last block
        output.Add((byte)(body.Count >> 16));
        output.Add((byte)(body.Count >> 8));
        output.Add((byte)body.Count);
        Push(output, body);
        Push(output, file.AsSpan(AfterStreamInfo));
        return output.ToArray();

        static void AddLe(List<byte> into, uint v)
        {
            into.Add((byte)v);
            into.Add((byte)(v >> 8));
            into.Add((byte)(v >> 16));
            into.Add((byte)(v >> 24));
        }
    }

    static void Push(List<byte> into, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++) into.Add(bytes[i]);
    }

    static void Push(List<byte> into, List<byte> bytes)
    {
        for (int i = 0; i < bytes.Count; i++) into.Add(bytes[i]);
    }

    /// <summary>A minimal FLAC ENCODER: "fLaC", a STREAMINFO with a real MD5, and one VERBATIM frame per block with
    /// a real CRC-8 and CRC-16. <paramref name="assignment"/> 0 writes the channels as they are; 1/2/3 write the
    /// left/side, side/right and mid/side forms the decoder has to undo. This is how the suite reaches what no
    /// affordable xiph vector covers: 32-bit samples, a 65,535-sample block, a 655,350 Hz rate, and variable
    /// blocking. A stream this encoder gets wrong fails its own MD5, so the two halves check each other.</summary>
    static byte[] Synthetic(int rate, int bps, int assignment, int[][][] frames, bool variable, int minBlock, int maxBlock)
    {
        int channels = frames[0].Length;
        long total = 0;                                                      // 36 bits on the wire, so never an int
        int smallest = int.MaxValue, largest = 0;
        foreach (int[][] frame in frames)
        {
            total += frame[0].Length;
            smallest = Math.Min(smallest, frame[0].Length);
            largest = Math.Max(largest, frame[0].Length);
        }
        if (minBlock > 0) smallest = minBlock;
        if (maxBlock > 0) largest = maxBlock;

        int bytesPer = (bps + 7) / 8;
        var packed = new List<byte>();
        foreach (int[][] frame in frames)
            for (int i = 0; i < frame[0].Length; i++)
                for (int c = 0; c < channels; c++)
                {
                    int v = frame[c][i];
                    for (int k = 0; k < bytesPer; k++) packed.Add((byte)(v >> (8 * k)));
                }
        byte[] md5 = MD5.HashData(packed.ToArray());

        var info = new BitWriter();
        info.Write((uint)smallest, 16);
        info.Write((uint)largest, 16);
        info.Write(0, 24);
        info.Write(0, 24);
        info.Write((uint)rate, 20);
        info.Write((uint)(channels - 1), 3);
        info.Write((uint)(bps - 1), 5);
        info.Write((uint)(total >> 32), 4);
        info.Write((uint)total, 32);

        var output = new List<byte>();
        Push(output, "fLaC"u8);
        output.Add(0x80);                                                    // the last metadata block …
        output.Add(0);
        output.Add(0);
        output.Add(Flac.StreamInfoBytes);                                    // … STREAMINFO, 34 bytes
        Push(output, info.ToArray());
        Push(output, md5);

        long number = 0;
        for (int f = 0; f < frames.Length; f++)
        {
            int[][] frame = frames[f];
            int block = frame[0].Length;
            int[][] coded;
            int[] depths;
            if (assignment == 0)
            {
                coded = frame;
                depths = new int[channels];
                for (int c = 0; c < channels; c++) depths[c] = bps;
            }
            else
            {
                int[] left = frame[0], right = frame[1];
                var side = new int[block];
                for (int i = 0; i < block; i++) side[i] = left[i] - right[i];
                if (assignment == 1) { coded = [left, side]; depths = [bps, bps + 1]; }
                else if (assignment == 2) { coded = [side, right]; depths = [bps + 1, bps]; }
                else
                {
                    var mid = new int[block];
                    for (int i = 0; i < block; i++) mid[i] = (left[i] + right[i]) >> 1;
                    coded = [mid, side];
                    depths = [bps, bps + 1];
                }
            }
            Push(output, Frame(block, coded, depths, assignment, variable ? (ulong)number : (ulong)f, variable));
            number += block;
        }
        return output.ToArray();
    }

    static byte[] Frame(int block, int[][] coded, int[] depths, int assignment, ulong number, bool variable)
    {
        var w = new BitWriter();
        w.Write(0xFF, 8);
        w.Write((uint)(0xF8 | (variable ? 1 : 0)), 8);
        w.Write(0x70, 8);                                                    // block size code 7, rate from STREAMINFO
        uint channelCode = assignment == 0 ? (uint)coded.Length - 1 : (uint)assignment + 7;
        w.Write(channelCode << 4, 8);                                        // bit depth from STREAMINFO, reserved 0
        WriteCodedNumber(w, number);
        w.Write((uint)(block - 1) >> 8, 8);
        w.Write((uint)(block - 1) & 0xFF, 8);
        w.Write(Flac.Crc8(w.ToArray()), 8);

        for (int c = 0; c < coded.Length; c++)
        {
            w.Write(0, 1);                                                   // the mandatory zero pad bit
            w.Write(1, 6);                                                   // subframe type: VERBATIM
            w.Write(0, 1);                                                   // no wasted bits
            foreach (int sample in coded[c]) w.WriteSigned(sample, depths[c]);
        }
        w.AlignToByte();
        w.Write(Flac.Crc16(w.ToArray()), 16);
        return w.ToArray();
    }

    /// <summary>§9.1.5's UTF-8-shaped number, the encoder half of <see cref="Flac.ReadCodedNumber"/>.</summary>
    static void WriteCodedNumber(BitWriter w, ulong value)
    {
        if (value < 0x80) { w.Write((uint)value, 8); return; }
        int length = value switch
        {
            < 0x800 => 2,
            < 0x1_0000 => 3,
            < 0x20_0000 => 4,
            < 0x400_0000 => 5,
            < 0x8000_0000 => 6,
            _ => 7,
        };
        uint lead = (uint)((0xFF << (8 - length)) & 0xFF);
        w.Write(lead | (uint)(value >> ((length - 1) * 6)), 8);
        for (int i = length - 2; i >= 0; i--) w.Write(0x80u | (uint)((value >> (i * 6)) & 0x3F), 8);
    }

    /// <summary>MSB-first bit writer — the mirror of <see cref="Flac.BitReader"/>, for the encoder above.</summary>
    sealed class BitWriter
    {
        readonly List<byte> _bytes = [];
        int _acc, _n;

        public void Write(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | (int)((value >> i) & 1);
                if (++_n == 8) { _bytes.Add((byte)_acc); _acc = 0; _n = 0; }
            }
        }

        public void WriteSigned(int value, int bits)
            => Write(bits >= 32 ? (uint)value : (uint)value & ((1u << bits) - 1), bits);

        /// <summary>Two's-complement <paramref name="bits"/> (1..64) of a 64-bit value, MSB first — the 33-bit side channel's
        /// samples (P-1), which <see cref="Write"/> (a 32-bit value) cannot carry.</summary>
        public void WriteLong(long value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | (int)(((ulong)value >> i) & 1);
                if (++_n == 8) { _bytes.Add((byte)_acc); _acc = 0; _n = 0; }
            }
        }

        public void AlignToByte()
        {
            while (_n != 0) Write(0, 1);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
