// ── Wavee.Tests/VorbisTests.cs — the gate for the Vorbis decoder (Wave 3-parallel, owner V) ─────────────────────────
//
// `Playback/Playback.Audio.Vorbis.cs` is CORE: a packet span in, interleaved stereo floats out, no engine, no stream,
// no thread. Every fact here is a pure fact over a byte array; the suite needs no `TestScope`.
//
// THE ORACLE IS FFMPEG'S OWN DECODER. `Fixtures/ogg/*.s16` are the first 3 s of `ffmpeg -i x.ogg -f s16le` — a
// second, independent Vorbis implementation — and two float decoders of the same stream agree to well under one
// LSB, so the facts allow two (an MD5 over s16 would be brittle for the same reason). A wrong codebook, floor,
// residue, coupling, IMDCT or window fails that comparison by thousands of LSBs, not by two. The IMDCT is also
// pinned on its own against the textbook basis function, the kernels against their scalar twins bit for bit, and
// the sample CONVENTION against the page granules (the packet boundaries a seek lands on). `Fixtures/ogg/README.md`
// has the ffmpeg command line of every file.
//
// The allocation fact is the CORE rule no reading of the code can prove (P8): after `Open`, 200 packets move
// `GC.GetAllocatedBytesForCurrentThread()` by ZERO bytes, and a second `Open` of the same setup costs nothing.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Wavee;
using Xunit;
using Ogg = Wavee.Playback.Ogg;
using Vorbis = Wavee.Playback.Vorbis;

namespace Wavee.Tests;

/// <summary>Shared by <see cref="VorbisTests"/> and <see cref="OggTests"/>: the fixtures, an opened decoder, a
/// whole-file decode with the granule trims applied, and a byte source that counts its requests.</summary>
internal static class VorbisFixture
{
    public static readonly string[] All =
        ["pink-320.ogg", "pink-96.ogg", "vbr-q8.ogg", "sine-440.ogg", "sweep-48k.ogg", "pages-100ms.ogg"];

    public static byte[] Bytes(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ogg", name));

    /// <summary>The three header packets of a file, copied out (the comment one is skipped).</summary>
    public static (byte[] Ident, byte[] Setup, long FirstAudioPage) Headers(byte[] file, Ogg.Reader reader)
    {
        reader.Reset();
        reader.Index.Clear();
        Assert.Equal(Ogg.Reader.Next.Packet, reader.NextPacket(file, out ReadOnlySpan<byte> ident, out _));
        byte[] identBytes = ident.ToArray();
        Assert.Equal(Ogg.Reader.Next.Packet, reader.NextPacket(file, out ReadOnlySpan<byte> comment, out _));
        Assert.Equal(3, Vorbis.HeaderType(comment));
        Assert.Equal(Ogg.Reader.Next.Packet, reader.NextPacket(file, out ReadOnlySpan<byte> setup, out _));
        return (identBytes, setup.ToArray(), reader.WindowOffset + reader.Cursor);
    }

    public static Vorbis.Decoder Open(byte[] file, Ogg.Reader reader, out long firstAudioPage)
    {
        var (ident, setup, first) = Headers(file, reader);
        var dec = new Vorbis.Decoder();
        Assert.True(dec.Open(ident, setup));
        firstAudioPage = first;
        return dec;
    }

    /// <summary>The granule of the file's last page, found the way the stream layer finds it: a scan of the tail.</summary>
    public static long LastGranule(byte[] file)
    {
        int from = Math.Max(0, file.Length - 64 * 1024);
        long last = -1;
        ReadOnlySpan<byte> tail = file.AsSpan(from);
        int at = 0;
        while ((at = Ogg.FindPage(tail, at, out Ogg.Page p, out _)) >= 0)
        {
            if (p.Granule >= 0) last = p.Granule;
            at += p.Length;
        }
        return last;
    }

    public sealed record Decoded(float[] Pcm, long Frames, long Untrimmed, long LastGranule, long LeadIn,
                                 int Packets, long Overruns, long AllocatedBytes);

    /// <summary>Decode a whole file linearly: prime on the first audio packet, apply the lead-in from the first
    /// audio page's granule and the end truncation from the last (Vorbis I §A.2) — the same trims ffmpeg applies,
    /// so index <c>2·p</c> of <see cref="Decoded.Pcm"/> is granule position p.</summary>
    public static Decoded DecodeAll(byte[] file)
    {
        var reader = new Ogg.Reader();
        var dec = Open(file, reader, out _);
        long total = LastGranule(file);
        var pcm = new float[(total + 2 * dec.MaxFrames) * 2];
        long outFrames = 0, leadIn = -1, lastGranule = -1;
        int packets = 0;
        while (true)
        {
            Ogg.Reader.Next next = reader.NextPacket(file, out ReadOnlySpan<byte> packet, out long granule);
            if (next == Ogg.Reader.Next.Corrupt) continue;
            if (next != Ogg.Reader.Next.Packet) break;
            Assert.Equal(Vorbis.PacketResult.Ok, dec.DecodePacket(packet));
            packets++;
            dec.OutputSpan.CopyTo(pcm.AsSpan((int)(outFrames * 2)));
            outFrames += dec.Frames;
            if (granule >= 0)
            {
                lastGranule = granule;
                if (leadIn < 0) leadIn = Ogg.LeadIn(granule, outFrames);
            }
        }
        long li = leadIn < 0 ? 0 : leadIn;
        long avail = outFrames - li;
        long exact = Ogg.ExactFrames(lastGranule);
        long keep = exact >= 0 && exact < avail ? exact : avail;
        return new Decoded(pcm.AsSpan((int)(li * 2), (int)(keep * 2)).ToArray(), keep, outFrames, lastGranule, li,
                           packets, dec.Overruns, dec.AllocatedBytes);
    }

    /// <summary>A range source over a byte array that counts every request — the fake CDN of §7.2.</summary>
    public sealed class CountingSource(byte[] data)
    {
        public int Requests;

        public byte[] ReadAt(long offset, int bytes)
        {
            Requests++;
            if (offset >= data.Length) return [];
            int n = (int)Math.Min(bytes, data.Length - offset);
            return data.AsSpan((int)offset, n).ToArray();
        }
    }

    /// <summary>Drive the CORE planner exactly as the adapter does: probe windows until it stops asking.</summary>
    public static Ogg.SeekPlan Plan(byte[] file, Ogg.Reader reader, long firstAudioPage, long total, long target,
                                    CountingSource src)
    {
        Ogg.SeekPlan plan = Ogg.BeginSeek(reader.Index, firstAudioPage, file.Length, total, target, reader.MaxPageSeen);
        while (Ogg.TryNextProbe(ref plan, out long at))
        {
            byte[] window = src.ReadAt(at, plan.WindowBytes);
            if (Ogg.Observe(ref plan, reader.Index, window, at) == Ogg.ProbeResult.NoPage) break;
        }
        return plan;
    }

    /// <summary>"Playback has started": read packets until the first audio page's last packet has been returned.</summary>
    public static void StartPlayback(byte[] file, Ogg.Reader reader, Vorbis.Decoder? dec)
    {
        while (true)
        {
            Ogg.Reader.Next next = reader.NextPacket(file, out ReadOnlySpan<byte> packet, out long granule);
            Assert.Equal(Ogg.Reader.Next.Packet, next);
            dec?.DecodePacket(packet);
            if (granule >= 0) return;
        }
    }
}

public unsafe class VorbisTests
{
    static void Print(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    // ── 1. the oracle: ffmpeg's decoder, and the sample convention ──────────────────────────────────────────────────

    [Fact]
    public void Decodes_pink320_within_two_lsb_of_ffmpeg() => AgainstFfmpeg("pink-320");

    [Fact]
    public void Decodes_vbr_within_two_lsb_of_ffmpeg() => AgainstFfmpeg("vbr-q8");

    static void AgainstFfmpeg(string name)
    {
        VorbisFixture.Decoded d = VorbisFixture.DecodeAll(VorbisFixture.Bytes(name + ".ogg"));
        byte[] raw = VorbisFixture.Bytes(name + ".s16");
        ReadOnlySpan<short> reference = MemoryMarshal.Cast<byte, short>(raw);

        Assert.Equal(441_000L, d.Frames);                            // 10 s: the EOS granule, lead-in 0
        Assert.Equal(441_000L, d.LastGranule);
        Assert.Equal(0L, d.LeadIn);
        Assert.True(reference.Length <= d.Pcm.Length);

        double maxLsb = 0, sumSq = 0;
        int worstAt = -1;
        for (int i = 0; i < reference.Length; i++)
        {
            double delta = d.Pcm[i] * 32768.0 - reference[i];
            double a = Math.Abs(delta);
            if (a > maxLsb) { maxLsb = a; worstAt = i; }
            sumSq += (delta / 32768.0) * (delta / 32768.0);
        }
        double rms = Math.Sqrt(sumSq / reference.Length);
        Print($"{name}: {reference.Length / 2:N0} frames vs ffmpeg, max |Δ| {maxLsb:F3} LSB at {worstAt}, RMS {rms:E2}, " +
              $"{d.Packets} packets, {d.Overruns} overruns");
        Assert.True(maxLsb <= 2.0, $"max |Δ| {maxLsb:F2} LSB at sample {worstAt}");
        Assert.True(rms < 1e-4, $"RMS {rms:E3}");
    }

    [Theory]
    [InlineData("pink-320.ogg", 2, 44_100, 441_000L)]
    [InlineData("pink-96.ogg", 2, 44_100, 441_000L)]
    [InlineData("vbr-q8.ogg", 2, 44_100, 441_000L)]
    [InlineData("sine-440.ogg", 1, 44_100, 352_800L)]
    [InlineData("sweep-48k.ogg", 2, 48_000, 384_000L)]
    [InlineData("pages-100ms.ogg", 2, 44_100, 441_000L)]
    public void Decodes_every_fixture_to_its_last_granule(string name, int channels, int rate, long frames)
    {
        byte[] file = VorbisFixture.Bytes(name);
        var reader = new Ogg.Reader();
        var (ident, _, _) = VorbisFixture.Headers(file, reader);
        Assert.True(Vorbis.TryParseIdentification(ident, out Vorbis.Identification id));
        Assert.Equal(channels, id.Channels);
        Assert.Equal(rate, id.SampleRate);
        Assert.Equal(256, id.BlockSize0);                            // libvorbis's short/long pair at 44.1 and 48 kHz
        Assert.Equal(2048, id.BlockSize1);

        VorbisFixture.Decoded d = VorbisFixture.DecodeAll(file);
        Assert.Equal(frames, d.Frames);
        Assert.Equal(frames, d.LastGranule);
        Assert.True(d.Untrimmed > d.Frames);                         // every fixture carries an end truncation
        float peak = 0;
        foreach (float v in d.Pcm)
        {
            Assert.True(float.IsFinite(v));
            float a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        Assert.InRange(peak, 0.01f, 1.0f);
        Print($"{name}: {d.Frames:N0} frames ({d.Untrimmed - d.Frames} trimmed at EOS), {d.Packets} packets, peak {peak:F4}, " +
              $"{d.Overruns} overruns, decoder holds {d.AllocatedBytes:N0} bytes");
    }

    [Fact]
    public void Sine_decodes_with_snr_above_40_db()
    {
        // 440 Hz at −6 dBFS, mono, -q:a 5. ffmpeg's own decode of this file measures 48.4 dB against the fitted
        // sinusoid (a perceptual codec does not reach the 60 dB the plan guessed); a wrong window shape or a
        // mis-scaled IMDCT lands far below 40 dB or far off the 0.5 amplitude.
        VorbisFixture.Decoded d = VorbisFixture.DecodeAll(VorbisFixture.Bytes("sine-440.ogg"));
        int n = (int)d.Frames;
        for (int i = 0; i < n; i++) Assert.Equal(d.Pcm[2 * i], d.Pcm[2 * i + 1]);   // mono duplicated exactly

        const int lo = 4096;
        int hi = n - 4096;
        double w = 2 * Math.PI * 440 / 44_100;
        double ss = 0, cc = 0, sc = 0, xs = 0, xc = 0;
        for (int i = lo; i < hi; i++)
        {
            double s = Math.Sin(w * i), c = Math.Cos(w * i), x = d.Pcm[2 * i];
            ss += s * s; cc += c * c; sc += s * c; xs += x * s; xc += x * c;
        }
        double det = ss * cc - sc * sc;
        double a = (xs * cc - xc * sc) / det, b = (xc * ss - xs * sc) / det;
        double signal = 0, noise = 0;
        for (int i = lo; i < hi; i++)
        {
            double fit = a * Math.Sin(w * i) + b * Math.Cos(w * i);
            double r = d.Pcm[2 * i] - fit;
            signal += fit * fit;
            noise += r * r;
        }
        double snr = 10 * Math.Log10(signal / noise);
        double amp = Math.Sqrt(a * a + b * b);
        Print($"sine-440: amplitude {amp:F5}, SNR {snr:F2} dB");
        Assert.InRange(amp, 0.49, 0.515);
        Assert.True(snr > 40, $"SNR {snr:F2} dB");
    }

    [Fact]
    public void Packet_frames_follow_the_spec_convention_and_peek_agrees_with_decode()
    {
        // blocksize(prev)/4 + blocksize(cur)/4 per packet (§1.3.2), so the frames of the packets ending on a page sum
        // to that page's granule step — the convention a seek lands by. PeekFrames must predict every packet.
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        var reader = new Ogg.Reader();
        var dec = VorbisFixture.Open(file, reader, out _);
        long position = 0;
        int packets = 0, mismatches = 0, pages = 0;
        bool first = true;
        while (reader.NextPacket(file, out ReadOnlySpan<byte> packet, out long granule) == Ogg.Reader.Next.Packet)
        {
            int prevN = dec.PreviousBlockSize;
            int peek = dec.PeekFrames(packet, ref prevN);
            Assert.Equal(Vorbis.PacketResult.Ok, dec.DecodePacket(packet));
            if (first) { Assert.Equal(0, dec.Frames); first = false; }   // the primed packet returns nothing
            if (peek != dec.Frames) mismatches++;
            Assert.Equal(dec.PreviousBlockSize, prevN);
            Assert.True(dec.Frames <= dec.MaxFrames);
            position += dec.Frames;
            packets++;
            if (granule >= 0)
            {
                pages++;
                if (reader.SawEos) Assert.True(position >= granule);  // the last page truncates
                else Assert.Equal(granule, position);
            }
        }
        Assert.Equal(0, mismatches);
        Assert.True(pages >= 10);
        Print($"pink-320: {packets} packets on {pages} audio pages, every page granule == Σ packet frames");
    }

    [Fact]
    public void Gapless_fields_come_from_the_granules()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        VorbisFixture.Decoded d = VorbisFixture.DecodeAll(file);
        Assert.Equal(441_000L, Ogg.ExactFrames(d.LastGranule));      // ExactFrames = the last page's granule
        Assert.Equal(441_088L, d.Untrimmed);                         // the decoder produced 88 more: the EOS trim
        Assert.Equal(0L, d.LeadIn);

        // The lead-in from the first audio page alone, the way the adapter computes it at open: PeekFrames over the
        // packets ending on that page, minus its granule.
        var reader = new Ogg.Reader();
        var dec = VorbisFixture.Open(file, reader, out long firstAudioPage);
        ReadOnlySpan<byte> page = file.AsSpan((int)firstAudioPage);
        Assert.Equal(Ogg.PageResult.Ok, Ogg.TryParsePage(page, 0, out Ogg.Page p));
        Span<int> starts = stackalloc int[256];
        Span<int> lengths = stackalloc int[256];
        int count = Ogg.PacketSpans(page, in p, starts, lengths, out bool continues);
        Assert.False(continues);
        int prevN = 0;
        long sum = 0;
        for (int i = 0; i < count; i++) sum += dec.PeekFrames(page.Slice(starts[i], lengths[i]), ref prevN);
        Assert.Equal(p.Granule, sum);
        Assert.Equal(0L, Ogg.LeadIn(p.Granule, sum));

        // The end truncation, packet by packet: TrimTail cuts exactly the 88 frames past the last granule.
        long pos = 0, kept = 0;
        reader = new Ogg.Reader();
        dec = VorbisFixture.Open(file, reader, out _);
        while (reader.NextPacket(file, out ReadOnlySpan<byte> packet, out _) == Ogg.Reader.Next.Packet)
        {
            dec.DecodePacket(packet);
            kept += Ogg.TrimTail(pos, dec.Frames, d.LastGranule);
            pos += dec.Frames;
        }
        Assert.Equal(441_000L, kept);

        // A synthetic first page claiming 1,000 fewer samples than its packets produce ⇒ a 1,000-frame lead-in, and
        // the decoded audio is the original shifted by exactly that much.
        byte[] patched = (byte[])file.Clone();
        Span<byte> hdr = patched.AsSpan((int)firstAudioPage, p.Length);
        BitConverter.TryWriteBytes(hdr.Slice(6, 8), p.Granule - 1000);
        hdr.Slice(22, 4).Clear();
        BitConverter.TryWriteBytes(hdr.Slice(22, 4), Ogg.Crc32(hdr, 22));
        VorbisFixture.Decoded shifted = VorbisFixture.DecodeAll(patched);
        Assert.Equal(1000L, shifted.LeadIn);
        Assert.Equal(441_088L - 1000, shifted.Frames);
        Assert.True(shifted.Pcm.AsSpan(0, 8192).SequenceEqual(d.Pcm.AsSpan(2000, 8192)));
    }

    // ── 2. headers ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Comment_header_walks_vendor_and_fields()
    {
        byte[] file = VorbisFixture.Bytes("sine-440.ogg");
        var reader = new Ogg.Reader();
        Assert.Equal(Ogg.Reader.Next.Packet, reader.NextPacket(file, out ReadOnlySpan<byte> ident, out _));
        Assert.Equal(1, Vorbis.HeaderType(ident));
        Assert.Equal(Ogg.Reader.Next.Packet, reader.NextPacket(file, out ReadOnlySpan<byte> comment, out _));
        Assert.True(Vorbis.CommentReader.TryCreate(comment, out Vorbis.CommentReader cr));
        Assert.True(cr.Vendor.SequenceEqual("ffmpeg"u8));
        bool title = false, artist = false;
        int seen = 0;
        while (cr.TryNext(out ReadOnlySpan<byte> field))
        {
            seen++;
            if (field.SequenceEqual("title=Wavee sine fixture"u8)) title = true;
            if (field.SequenceEqual("artist=Wavee"u8)) artist = true;
        }
        Assert.Equal(cr.Count, seen);
        Assert.True(title && artist);
        Assert.Equal(-1, Vorbis.HeaderType([0, 1, 2, 3, 4, 5, 6, 7]));   // an audio packet is never a header
    }

    // ── 3. codebooks ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Codewords_are_canonical_and_the_fast_table_agrees_with_the_slow_path()
    {
        uint seed = 0x9E37_79B9;
        for (int set = 0; set < 12; set++)
        {
            sbyte[] len = RandomCompleteLengths(ref seed, maxLen: 13, leaves: 8 + set * 4);
            var codes = new uint[len.Length];
            Assert.True(Vorbis.BuildCodewords(len, codes));
            uint[] expected = ReferenceCanonical(len);
            for (int i = 0; i < len.Length; i++) if (len[i] > 0) Assert.Equal(expected[i], codes[i]);

            // real tables: fast path for ≤ 10 bits, the sorted fallback beyond
            var fast = new uint[Vorbis.FastSize];
            var sorted = new uint[len.Length];
            var sortedEntry = new int[len.Length];
            Vorbis.BuildTables(len, codes, fast, sorted, sortedEntry, out int sortedCount, out int maxLen);
            var book = new Vorbis.Book { Entries = len.Length, Dims = 1, SortedCount = sortedCount, MaxLen = maxLen, VqOff = -1 };
            AssertEveryEntryDecodes(len, codes, book, fast, sorted, sortedEntry);

            // every entry through DecodeSlow: a table of misses and all codes in the sorted arena
            var misses = new uint[Vorbis.FastSize];
            Array.Fill(misses, Vorbis.Miss);
            int used = 0;
            for (int i = 0; i < len.Length; i++) if (len[i] > 0) used++;
            var allSorted = new uint[used];
            var allEntry = new int[used];
            int k = 0;
            for (int i = 0; i < len.Length; i++)
            {
                if (len[i] <= 0) continue;
                allSorted[k] = codes[i] << (maxLen - len[i]);
                allEntry[k++] = (len[i] << 24) | i;
            }
            Array.Sort(allSorted, allEntry);
            var slowBook = book with { SortedCount = used };
            if (maxLen > Vorbis.FastBits) AssertEveryEntryDecodes(len, codes, slowBook, misses, allSorted, allEntry);
        }
    }

    [Fact]
    public void Single_entry_codebook_reads_one_bit()
    {
        sbyte[] len = [0, 1, 0];
        var codes = new uint[3];
        Assert.True(Vorbis.BuildCodewords(len, codes));
        var fast = new uint[Vorbis.FastSize];
        Vorbis.BuildTables(len, codes, fast, new uint[3], new int[3], out int sortedCount, out _);
        Assert.Equal(0, sortedCount);
        var book = new Vorbis.Book { Entries = 3, Dims = 1, VqOff = -1 };
        foreach (uint bit in new uint[] { 0, 1 })
        {
            var w = new LsbBitWriter();
            w.Write(bit, 1);
            w.Write(0x5A, 7);
            byte[] stream = w.ToArray();
            fixed (byte* s = stream)
            fixed (uint* f = fast)
            {
                var r = new Vorbis.BitReader(s, stream.Length);
                Assert.Equal(1, Vorbis.DecodeScalar(ref r, &book, f, null, null));
                Assert.Equal(0x5Au, r.Read(7));                        // exactly one bit was consumed
            }
        }
    }

    static void AssertEveryEntryDecodes(sbyte[] len, uint[] codes, Vorbis.Book book, uint[] fast, uint[] sorted,
                                        int[] sortedEntry)
    {
        for (int i = 0; i < len.Length; i++)
        {
            if (len[i] <= 0) continue;
            var w = new LsbBitWriter();
            w.WriteCode(codes[i], len[i]);
            w.Write(0xA5C3, 16);
            w.Write(0, 32);
            byte[] stream = w.ToArray();
            fixed (byte* s = stream)
            fixed (uint* f = fast)
            fixed (uint* so = sorted)
            fixed (int* se = sortedEntry)
            {
                var r = new Vorbis.BitReader(s, stream.Length);
                Vorbis.Book b = book;
                Assert.Equal(i, Vorbis.DecodeScalar(ref r, &b, f, so, se));
                Assert.Equal(0xA5C3u, r.Read(16));
                Assert.False(r.Overrun);
            }
        }
    }

    /// <summary>A Kraft-complete length set, built by splitting random leaves, in a shuffled entry order with a
    /// few unused entries mixed in.</summary>
    static sbyte[] RandomCompleteLengths(ref uint seed, int maxLen, int leaves)
    {
        var l = new List<int> { 1, 1 };
        while (l.Count < leaves)
        {
            int at = (int)(Next(ref seed) % (uint)l.Count);
            if (l[at] >= maxLen) continue;
            int d = l[at] + 1;
            l[at] = d;
            l.Add(d);
        }
        for (int i = 0; i < leaves / 4; i++) l.Add(0);
        for (int i = l.Count - 1; i > 0; i--)
        {
            int j = (int)(Next(ref seed) % (uint)(i + 1));
            (l[i], l[j]) = (l[j], l[i]);
        }
        var r = new sbyte[l.Count];
        for (int i = 0; i < r.Length; i++) r[i] = (sbyte)l[i];
        return r;
    }

    /// <summary>The spec's definition, brute force: each used entry in order takes the numerically lowest codeword
    /// of its length that no assigned codeword is a prefix of and that is a prefix of none.</summary>
    static uint[] ReferenceCanonical(sbyte[] len)
    {
        var codes = new uint[len.Length];
        var assigned = new List<(uint Code, int Len)>();
        for (int i = 0; i < len.Length; i++)
        {
            int L = len[i];
            if (L <= 0) continue;
            for (uint c = 0; c < 1u << L; c++)
            {
                bool clash = false;
                foreach (var (code, cl) in assigned)
                {
                    int m = Math.Min(cl, L);
                    if ((code >> (cl - m)) == (c >> (L - m))) { clash = true; break; }
                }
                if (clash) continue;
                codes[i] = c;
                assigned.Add((c, L));
                break;
            }
        }
        return codes;
    }

    static uint Next(ref uint x)
    {
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return x;
    }

    // ── 4. floor 1 and residue 2 against the spec's own pseudocode ─────────────────────────────────────────────────

    [Theory]
    [InlineData(128)]                                                // the long block the floor was set up for
    [InlineData(64)]                                                 // a short block: posts beyond n2 truncate
    public void Floor1_render_matches_the_spec_pseudocode(int n2)
    {
        int[] xs = [0, 128, 50, 20, 90, 5, 110, 35, 70, 127];
        const int multiplier = 2, range = 128;
        uint seed = (uint)(0xC0FFEE + n2);
        for (int trial = 0; trial < 8; trial++)
        {
            int[] y = new int[xs.Length];
            for (int i = 0; i < y.Length; i++)
                y[i] = trial % 3 == 0 && i >= 2 && i % 2 == 0 ? 0 : (int)(Next(ref seed) % range);

            var f = new Vorbis.Floor { Type = 1, Multiplier = multiplier, RangeBits = 7, Values = xs.Length };
            for (int i = 0; i < xs.Length; i++) f.X[i] = (ushort)xs[i];
            Assert.True(Vorbis.PrepareFloor1(&f));

            int[] finalRef = SpecUnwrap(xs, y, range, out bool[] step2);
            var spec = new float[n2];
            for (int i = 0; i < n2; i++) spec[i] = 0.5f + (Next(ref seed) & 0xFFFF) / 65536f;
            float[] expected = (float[])spec.Clone();
            SpecRender(xs, finalRef, step2, multiplier, n2, expected);

            var final = new int[Vorbis.MaxPosts];
            fixed (int* yp = y)
            fixed (int* fp = final)
            fixed (float* sp = spec)
            fixed (float* db = Vorbis.Floor1InverseDb)
            {
                Vorbis.UnwrapPosts(&f, yp, fp);
                for (int i = 0; i < xs.Length; i++)
                {
                    Assert.Equal(finalRef[i], final[i] & 0x7FFF);
                    if (i >= 2) Assert.Equal(step2[i], (final[i] & 0x8000) == 0);
                }
                Vorbis.RenderFloor1(&f, fp, sp, n2, db);
            }
            for (int i = 0; i < n2; i++)
                Assert.Equal(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits(spec[i]));
        }
    }

    /// <summary>§7.2.4 step 1, verbatim.</summary>
    static int[] SpecUnwrap(int[] xs, int[] y, int range, out bool[] step2)
    {
        int n = xs.Length;
        var final = new int[n];
        step2 = new bool[n];
        step2[0] = step2[1] = true;
        final[0] = y[0];
        final[1] = y[1];
        for (int i = 2; i < n; i++)
        {
            int lo = 0, hi = 1, lx = -1, hx = int.MaxValue;
            for (int j = 0; j < i; j++)
            {
                if (xs[j] < xs[i] && xs[j] > lx) { lx = xs[j]; lo = j; }
                if (xs[j] > xs[i] && xs[j] < hx) { hx = xs[j]; hi = j; }
            }
            int dy = final[hi] - final[lo], adx = xs[hi] - xs[lo], ady = Math.Abs(dy);
            int off = ady * (xs[i] - xs[lo]) / adx;
            int predicted = dy < 0 ? final[lo] - off : final[lo] + off;
            int val = y[i], highroom = range - predicted, lowroom = predicted;
            int room = highroom < lowroom ? highroom * 2 : lowroom * 2;
            if (val != 0)
            {
                step2[lo] = step2[hi] = step2[i] = true;
                if (val >= room) final[i] = highroom > lowroom ? val - lowroom + predicted : predicted - val + highroom - 1;
                else final[i] = (val & 1) != 0 ? predicted - (val + 1) / 2 : predicted + val / 2;
            }
            else
            {
                step2[i] = false;
                final[i] = predicted;
            }
        }
        return final;
    }

    /// <summary>§7.2.4 step 2 + §9.2.7 render_line into an integer floor vector, then the §4.3.6 dot product.</summary>
    static void SpecRender(int[] xs, int[] final, bool[] step2, int multiplier, int n, float[] spec)
    {
        int count = xs.Length;
        var order = Enumerable.Range(0, count).OrderBy(i => xs[i]).ToArray();
        var floor = new int[Math.Max(n, xs.Max()) + 1];
        int hx = 0, hy = 0, lx = 0, ly = final[0] * multiplier;
        for (int q = 1; q < count; q++)
        {
            int j = order[q];
            if (!step2[j]) continue;
            hy = final[j] * multiplier;
            hx = xs[j];
            RenderLine(lx, ly, hx, hy, floor);
            lx = hx;
            ly = hy;
        }
        if (hx < n) RenderLine(hx, hy, n, hy, floor);
        ReadOnlySpan<float> db = Vorbis.Floor1InverseDb;
        for (int i = 0; i < n; i++) spec[i] *= db[floor[i]];
    }

    static void RenderLine(int x0, int y0, int x1, int y1, int[] v)
    {
        int dy = y1 - y0, adx = x1 - x0, ady = Math.Abs(dy), bas = dy / adx, x = x0, y = y0, err = 0;
        int sy = dy < 0 ? bas - 1 : bas + 1;
        ady -= Math.Abs(bas) * adx;
        v[x] = y;
        for (x = x0 + 1; x < x1; x++)
        {
            err += ady;
            if (err >= adx) { err -= adx; y += sy; } else y += bas;
            v[x] = y;
        }
    }

    [Theory]
    [InlineData(2, 2)]                                               // the stereo, even-dims fast path
    [InlineData(2, 1)]                                               // odd dims: the generic stereo path
    [InlineData(3, 2)]                                               // three channels: the generic path
    public void Residue2_interleave_writes_channels_round_robin(int ch, int dims)
    {
        const int n2 = 8, psize = 4;
        int size = ch * n2, partitions = size / psize, words = psize / dims;
        // book 0: the classbook — one entry of length 1 (any bit decodes to class 0)
        // book 1: the VQ book — 4 entries of length 2, value[e][d] = 10·e + d + 1
        sbyte[] len0 = [1];
        sbyte[] len1 = [2, 2, 2, 2];
        var fast = new uint[2 * Vorbis.FastSize];
        var codes1 = new uint[4];
        Assert.True(Vorbis.BuildCodewords(len1, codes1));
        Vorbis.BuildTables(len0, new uint[1], fast.AsSpan(0, Vorbis.FastSize), new uint[1], new int[1], out _, out _);
        Vorbis.BuildTables(len1, codes1, fast.AsSpan(Vorbis.FastSize, Vorbis.FastSize), new uint[4], new int[4], out _, out _);
        var vq = new float[4 * dims];
        for (int e = 0; e < 4; e++) for (int d = 0; d < dims; d++) vq[e * dims + d] = 10 * e + d + 1;
        Vorbis.Book[] books =
        [
            new Vorbis.Book { Dims = 1, Entries = 1, FastOff = 0, VqOff = -1 },
            new Vorbis.Book { Dims = dims, Entries = 4, FastOff = Vorbis.FastSize, VqOff = 0 },
        ];
        var res = new Vorbis.Residue { Type = 2, Begin = 0, End = size, PartitionSize = psize, Classifications = 1, Classbook = 0 };
        res.Cascade[0] = 1;                                          // pass 0 only
        for (int i = 0; i < 64 * 8; i++) res.Books[i] = -1;
        res.Books[0] = 1;
        byte[] classMap = [0];

        // the stream: per partition one classword bit, then `words` 2-bit VQ codewords (entry e = the e-th code)
        uint seed = (uint)(ch * 31 + dims);
        var entries = new List<int>();
        var w = new LsbBitWriter();
        for (int p = 0; p < partitions; p++)
        {
            w.Write(Next(ref seed) & 1, 1);
            for (int q = 0; q < words; q++)
            {
                int e = (int)(Next(ref seed) % 4);
                entries.Add(e);
                w.WriteCode(codes1[e], 2);
            }
        }
        w.Write(0, 32);
        byte[] stream = w.ToArray();

        var expected = new float[size];                               // the interleaved vector, spec §8.6.2
        int o = 0;
        foreach (int e in entries) for (int d = 0; d < dims; d++) expected[o++] += vq[e * dims + d];

        var spec = new float[ch * n2];
        var ptrs = new nint[ch];
        var partClass = new byte[partitions + 1];
        fixed (float* sp = spec)
        fixed (byte* s = stream)
        fixed (Vorbis.Book* bp = books)
        fixed (uint* f = fast)
        fixed (float* v = vq)
        fixed (byte* cm = classMap)
        fixed (nint* pp = ptrs)
        fixed (byte* pc = partClass)
        {
            for (int c = 0; c < ch; c++) pp[c] = (nint)(sp + c * n2);
            var r = new Vorbis.BitReader(s, stream.Length);
            Vorbis.DecodeResidue2(ref r, &res, bp, f, null, null, v, cm, (float**)pp, ch, n2, pc);
        }
        for (int j = 0; j < size; j++) Assert.Equal(expected[j], spec[(j % ch) * n2 + j / ch]);
    }

    // ── 5. the IMDCT and the kernels ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(2048)]
    public void Imdct_of_a_unit_impulse_is_the_basis_function(int n)
    {
        // y[i] = cos(2π/n · (i + ½ + n/4) · (k + ½)), scale exactly 1 — stb's eight stages repay the "missing ×2"
        // in B. 64 and 128 are the sizes stb's fixed step-3 loops get wrong (stage applied twice); they are here so
        // the correction stays corrected.
        int n2 = n / 2;
        var a = new float[n2];
        var b = new float[n2];
        var c = new float[n / 4];
        var rev = new ushort[n / 8];
        Vorbis.ComputeImdctTables(n, a, b, c, rev);
        var buf = new float[n];
        var buf2 = new float[n2];
        double worst = 0;
        foreach (int k in new[] { 0, 1, 7, n / 4, n2 - 1 })
            foreach (bool scalar in new[] { false, true })
            {
                Array.Clear(buf);
                buf[k] = 1;
                RunImdct(buf, buf2, n, a, b, c, rev, scalar);
                for (int i = 0; i < n; i++)
                {
                    double expect = Math.Cos(2 * Math.PI / n * (i + 0.5 + n / 4.0) * (k + 0.5));
                    worst = Math.Max(worst, Math.Abs(buf[i] - expect));
                }
            }
        Print($"IMDCT n={n}: max |error| {worst:E2} against the basis function");
        Assert.True(worst < 1e-5, $"n={n}: {worst:E3}");
    }

    static void RunImdct(float[] buf, float[] buf2, int n, float[] a, float[] b, float[] c, ushort[] rev, bool scalar)
    {
        bool saved = Vorbis.ForceScalar;
        Vorbis.ForceScalar = scalar;
        try
        {
            fixed (float* bp = buf)
            fixed (float* b2 = buf2)
            fixed (float* ap = a)
            fixed (float* bb = b)
            fixed (float* cp = c)
            fixed (ushort* rp = rev)
                Vorbis.Imdct(bp, b2, n, ap, bb, cp, rp);
        }
        finally { Vorbis.ForceScalar = saved; }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1024)]
    public void Kernels_vector_and_scalar_agree_bit_for_bit(int n)
    {
        uint seed = (uint)(n * 7919 + 1);
        float[] Noise(int count, bool signed)
        {
            var v = new float[count];
            for (int i = 0; i < count; i++)
            {
                float u = (Next(ref seed) & 0xFFFFFF) / 16777216f;
                v[i] = signed ? u * 2 - 1 : u;
                if (signed && i % 11 == 3) v[i] = 0;                 // zeros hit the > 0 edge of the coupling
            }
            return v;
        }

        float[] cur = Noise(n, true), prev = Noise(n, true), w = Noise(n, false), wr = Noise(n, false);
        float[] m = Noise(n, true), ang = Noise(n, true), p = Noise(n, true);
        float gain = 0.8123f;

        float[] Run(bool scalar, Func<float[]> body)
        {
            bool saved = Vorbis.ForceScalar;
            Vorbis.ForceScalar = scalar;
            try { return body(); } finally { Vorbis.ForceScalar = saved; }
        }

        float[] Overlap() { var x = (float[])cur.Clone(); fixed (float* xp = x) fixed (float* pp = prev) fixed (float* wp = w) fixed (float* rp = wr) Vorbis.OverlapAdd(xp, pp, wp, rp, n); return x; }
        float[] Couple() { var x = (float[])m.Clone(); var y = (float[])ang.Clone(); fixed (float* xp = x) fixed (float* yp = y) Vorbis.Uncouple(xp, yp, n); return [.. x, .. y]; }
        float[] Scale() { var x = (float[])p.Clone(); fixed (float* xp = x) Vorbis.MultiplyConstant(xp, n, gain); return x; }
        float[] Interleave() { var o = new float[2 * n]; fixed (float* lp = cur) fixed (float* rp = prev) fixed (float* op = o) Vorbis.InterleaveStereo(lp, rp, op, n, gain); return o; }

        foreach (var (name, kernel) in new (string, Func<float[]>)[] { ("OverlapAdd", new Func<float[]>(Overlap)), ("Uncouple", new Func<float[]>(Couple)), ("MultiplyConstant", new Func<float[]>(Scale)), ("InterleaveStereo", new Func<float[]>(Interleave)) })
        {
            float[] vector = Run(false, kernel), scalar = Run(true, kernel);
            Assert.True(MemoryMarshal.AsBytes(vector.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(scalar.AsSpan())),
                        $"{name} n={n}: the vector path differs from the scalar path");
        }

        // and the scalar paths are the spec's arithmetic
        float[] ov = Run(true, Overlap);
        for (int i = 0; i < n; i++) Assert.Equal(cur[i] * w[i] + prev[i] * wr[i], ov[i]);
        float[] il = Run(false, Interleave);
        for (int i = 0; i < n; i++) { Assert.Equal(cur[i] * gain, il[2 * i]); Assert.Equal(prev[i] * gain, il[2 * i + 1]); }

        if (n >= 256 && (n & (n - 1)) == 0)
        {
            var a = new float[n / 2]; var b = new float[n / 2]; var c = new float[n / 4]; var rev = new ushort[n / 8];
            Vorbis.ComputeImdctTables(n, a, b, c, rev);
            var x1 = new float[n]; var x2 = new float[n];
            Noise(n / 2, true).CopyTo(x1, 0);
            x1.AsSpan(0, n / 2).CopyTo(x2);
            RunImdct(x1, new float[n / 2], n, a, b, c, rev, scalar: false);
            RunImdct(x2, new float[n / 2], n, a, b, c, rev, scalar: true);
            Assert.True(MemoryMarshal.AsBytes(x1.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(x2.AsSpan())), $"IMDCT n={n}");
        }
    }

    [Fact]
    public void Uncouple_matches_the_four_cases()
    {
        float[] values = [0.75f, -0.5f, 0f, 0.25f, -1f, 0.125f];
        int count = values.Length * values.Length;
        foreach (int n in new[] { 15, count })                       // 15 forces the scalar path; 36 takes the vectors
        {
            var mag = new float[n];
            var ang = new float[n];
            for (int i = 0; i < n; i++) { mag[i] = values[i / values.Length % values.Length]; ang[i] = values[i % values.Length]; }
            var expectM = new float[n];
            var expectA = new float[n];
            for (int i = 0; i < n; i++)
            {
                float M = mag[i], A = ang[i];
                if (M > 0) { if (A > 0) { expectM[i] = M; expectA[i] = M - A; } else { expectA[i] = M; expectM[i] = M + A; } }
                else { if (A > 0) { expectM[i] = M; expectA[i] = M + A; } else { expectA[i] = M; expectM[i] = M - A; } }
            }
            fixed (float* mp = mag)
            fixed (float* ap = ang)
                Vorbis.Uncouple(mp, ap, n);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(expectM[i], mag[i]);
                Assert.Equal(expectA[i], ang[i]);
            }
        }
    }

    // ── 6. seeking: exact samples, bounded probes ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pink-320.ogg")]
    [InlineData("pink-96.ogg")]
    [InlineData("vbr-q8.ogg")]
    [InlineData("sine-440.ogg")]
    [InlineData("sweep-48k.ogg")]
    [InlineData("pages-100ms.ogg")]
    public void Seek_lands_on_the_exact_sample(string name)
    {
        byte[] file = VorbisFixture.Bytes(name);
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        long total = linear.Frames;

        var reader = new Ogg.Reader();
        var dec = VorbisFixture.Open(file, reader, out long firstAudioPage);
        VorbisFixture.StartPlayback(file, reader, dec);
        var src = new VorbisFixture.CountingSource(file);
        const int want = 4096;

        foreach (long target in new[] { total * 70 / 100, total * 72 / 100, total / 10, total * 95 / 100, 0, total - 10, total / 3 })
        {
            Ogg.SeekPlan plan = VorbisFixture.Plan(file, reader, firstAudioPage, total, target, src);
            Assert.InRange(plan.Probes, 0, 8);
            float[] got = DecodeFrom(file, reader, dec, plan.Offset, target + want, out long startPos, out long frames);
            Assert.True(startPos <= target, $"{name} → {target}: landed at {startPos}, past the target");
            int compare = (int)Math.Min(want, total - target);
            Assert.True(startPos + frames >= target + compare, $"{name} → {target}: decoded only to {startPos + frames}");
            ReadOnlySpan<float> seeked = got.AsSpan((int)((target - startPos) * 2), compare * 2);
            ReadOnlySpan<float> straight = linear.Pcm.AsSpan((int)(target * 2), compare * 2);
            Assert.True(MemoryMarshal.AsBytes(seeked).SequenceEqual(MemoryMarshal.AsBytes(straight)),
                        $"{name} → {target}: the seeked samples differ from the linear decode");
            Print($"{name}: seek {target,7} → page {plan.Offset,7}, probes {plan.Probes}, tier {plan.Tier}, " +
                  $"resolved {plan.Resolved}, primed from {startPos}");
        }

        // past the end: no exception, and nothing decodes at the target
        Ogg.SeekPlan beyond = VorbisFixture.Plan(file, reader, firstAudioPage, total, total + 50_000, src);
        Assert.InRange(beyond.Probes, 0, 8);
        DecodeFrom(file, reader, dec, beyond.Offset, total + 50_000, out long s2, out long f2);
        Assert.True(s2 + f2 <= total + dec.MaxFrames);
    }

    /// <summary>The adapter's landing, in miniature: re-point the reader at the page, prime, decode forward, and fix
    /// the absolute position of the collected frames from the first page granule reached (§4.3).</summary>
    static float[] DecodeFrom(byte[] file, Ogg.Reader reader, Vorbis.Decoder dec, long offset, long until,
                              out long startPos, out long frames)
    {
        reader.Reposition(offset);
        dec.Prime();
        ReadOnlySpan<byte> rest = file.AsSpan((int)offset);
        var buf = new float[1 << 16];
        long acc = 0;
        startPos = long.MinValue;
        while (true)
        {
            Ogg.Reader.Next next = reader.NextPacket(rest, out ReadOnlySpan<byte> packet, out long granule);
            if (next == Ogg.Reader.Next.Corrupt) continue;
            if (next != Ogg.Reader.Next.Packet) break;
            if (dec.DecodePacket(packet) != Vorbis.PacketResult.Ok) continue;
            if ((acc + dec.Frames) * 2 > buf.Length) Array.Resize(ref buf, buf.Length * 2);
            dec.OutputSpan.CopyTo(buf.AsSpan((int)(acc * 2)));
            acc += dec.Frames;
            if (startPos == long.MinValue && granule >= 0) startPos = granule - acc;
            if (startPos != long.MinValue && startPos + acc >= until) break;
        }
        if (startPos == long.MinValue) startPos = until;
        frames = acc;
        return buf;
    }

    // ── 7. the allocation gate and the throughput floor ─────────────────────────────────────────────────────────────

    [Fact]
    public void Decoding_two_hundred_packets_allocates_nothing()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        var reader = new Ogg.Reader();
        var dec = VorbisFixture.Open(file, reader, out long firstAudioPage);
        Assert.True(DecodePass(file, reader, dec, firstAudioPage) > 0);        // every path JIT'd, every type loaded
        reader.Reposition(firstAudioPage);
        dec.Prime();
        byte[] audio = file.AsSpan((int)firstAudioPage).ToArray();
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(Ogg.Reader.Next.Packet, reader.NextPacket(audio, out ReadOnlySpan<byte> warm, out _));
            Assert.Equal(Vorbis.PacketResult.Ok, dec.DecodePacket(warm));
        }

        int decoded = 0, failures = 0;
        long frames = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoded < 200)
        {
            if (reader.NextPacket(audio, out ReadOnlySpan<byte> packet, out _) != Ogg.Reader.Next.Packet) { failures++; break; }
            if (dec.DecodePacket(packet) != Vorbis.PacketResult.Ok) failures++;
            frames += dec.Frames;
            decoded++;
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, failures);
        Assert.Equal(200, decoded);
        Assert.True(frames > 150_000);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void Reopening_with_the_same_setup_allocates_nothing()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        var (ident, setup, _) = VorbisFixture.Headers(file, new Ogg.Reader());
        var dec = new Vorbis.Decoder();
        Assert.True(dec.Open(ident, setup));
        long held = dec.AllocatedBytes;

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(dec.Open(ident, setup));
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before);
        Assert.Equal(held, dec.AllocatedBytes);
        Print($"pink-320 (44.1 kHz stereo, 256/2048): the decoder holds {held:N0} bytes after Open");
        Assert.InRange(held, 64 * 1024, 4 * 1024 * 1024);
    }

    [Fact]
    public void Throughput_is_at_least_fifty_times_realtime()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        var reader = new Ogg.Reader();
        var dec = VorbisFixture.Open(file, reader, out long firstAudioPage);
        long perPass = DecodePass(file, reader, dec, firstAudioPage);         // JIT, type init
        Assert.True(perPass >= 441_000);

        var clock = Stopwatch.StartNew();
        int passes = 0;
        do
        {
            Assert.Equal(perPass, DecodePass(file, reader, dec, firstAudioPage));
            passes++;
        } while (passes < 3 || clock.ElapsedMilliseconds < 500);
        clock.Stop();

        double samplesPerSecond = passes * perPass / clock.Elapsed.TotalSeconds;
        double realTime = samplesPerSecond / 44_100;
        // The plan's floor is 50× real time. A Debug build runs the JIT with optimizations off (no inlining of the
        // bit reader, no enregistration) and cannot promise that on a loaded machine, so an unoptimized Wavee
        // assembly is held to FLAC's 10× instead; the printed number is the benchmark either way.
        bool optimized = typeof(Playback).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;
        double floor = optimized ? 50 : 10;
        Print($"pink-320 (~330 kbit/s stereo): {samplesPerSecond:N0} frames/s, {realTime:N0}x real time, " +
              $"{passes} passes in {clock.Elapsed.TotalMilliseconds:N0} ms ({(optimized ? "optimized" : "Debug JIT")}, floor {floor}x)");
        Assert.True(realTime >= floor, $"decoded at only {realTime:N1}x real time");
    }

    static long DecodePass(byte[] file, Ogg.Reader reader, Vorbis.Decoder dec, long firstAudioPage)
    {
        reader.Reposition(firstAudioPage);
        dec.Prime();
        ReadOnlySpan<byte> audio = file.AsSpan((int)firstAudioPage);
        long frames = 0;
        while (reader.NextPacket(audio, out ReadOnlySpan<byte> packet, out _) == Ogg.Reader.Next.Packet)
        {
            dec.DecodePacket(packet);
            frames += dec.Frames;
        }
        return frames;
    }
}

/// <summary>An LSB-first bit writer (Vorbis I §2): fields go in low bit first; a Huffman codeword goes in its
/// spec order, most significant bit first.</summary>
internal sealed class LsbBitWriter
{
    readonly List<byte> _bytes = [];
    int _bit;

    public void Write(uint value, int bits)
    {
        for (int i = 0; i < bits; i++) Bit((value >> i) & 1);
    }

    public void WriteCode(uint code, int len)
    {
        for (int i = len - 1; i >= 0; i--) Bit((code >> i) & 1);
    }

    void Bit(uint b)
    {
        if (_bit % 8 == 0) _bytes.Add(0);
        if (b != 0) _bytes[^1] |= (byte)(1 << (_bit % 8));
        _bit++;
    }

    public byte[] ToArray() => [.. _bytes];
}
