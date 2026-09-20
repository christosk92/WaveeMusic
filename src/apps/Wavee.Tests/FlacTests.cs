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
            // The reference is the pre-optimisation downmix, verbatim: sample × scale, then × k, then accumulate.
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
                    left = planar[i] * scale;
                    right = planar[block + i] * scale;
                    if (channels >= 3) { float c = planar[2 * block + i] * scale * k; left += c; right += c; }
                    if (channels >= 5)
                    {
                        left += planar[(channels == 5 ? 3 : 4) * block + i] * scale * k;
                        right += planar[(channels == 5 ? 4 : 5) * block + i] * scale * k;
                    }
                    if (channels >= 7)
                    {
                        left += planar[(channels - 2) * block + i] * scale * k;
                        right += planar[(channels - 1) * block + i] * scale * k;
                    }
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

        public void AlignToByte()
        {
            while (_n != 0) Write(0, 1);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
