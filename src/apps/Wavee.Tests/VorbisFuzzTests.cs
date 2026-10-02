// ── Wavee.Tests/VorbisFuzzTests.cs — hostile input never throws, hangs or balloons (playback plan §5, WP-4d, #167) ──
//
// The Vorbis decoder and the Ogg reader are hand-rolled and `unsafe`, so the one property no reading of the code can prove
// is "whatever the bytes are, the answer is a result code, not an exception, not a hang and not a gigabyte". Spotify's own
// files are valid, but a local file, a half-downloaded cache slot or a flipped bit is not, and an exception on the
// producer thread is a dead track. The harness is a fixed-seed mutation loop (xorshift32, so a failing iteration number
// reproduces on any runtime) over four real streams chosen for their different code paths:
//
//   sine-440.ogg        floor 1 + residue 2, mono, 256 / 2048 (the Spotify shape)
//   test-short.ogg      floor 0 + residue 0, stereo (the early-encoder paths)
//   48k-mono.ogg        floor 1 + residue 1, 512 / 4096
//   singlemap-test.ogg  one mode, one mapping, 2048 / 2048
//
// and asserts, per layer:
//   PACKETS   a mutated or random audio packet decodes or is refused (`PacketResult`), never throws, never leaves frames
//             behind a refusal, never exceeds `MaxFrames`, and allocates NOTHING (the decoder's zero-allocation rule holds
//             for garbage too);
//   SETUPS    a mutated setup header either opens or is refused, each `Open` allocates at most 32 MiB (P-11: the audit's
//             "a hostile header can allocate hundreds of MB"), a header that parses decodes the real packets without
//             throwing, and the same decoder object still opens the CLEAN setup and decodes it bit for bit like a fresh one
//             (the pool re-uses decoders across tracks);
//   STREAMS   a mutated Ogg file (bit flips and noise INSIDE valid pages, re-sealed with a good CRC so the packet really
//             reaches the decoder; granules, flags, sequence numbers, serials and lacing tables rewritten; pages dropped,
//             duplicated, truncated, interleaved with junk) walks the reader to its end in a bounded number of steps with
//             zero reader allocation, drives the decoder and the adapter's sample clock (`VorbisClock.Admit`) with sane
//             runs, and survives the adapter's landing peek and the seek planner (bounded probes, no exception);
//   OVERSIZE  a packet that never ends is `Corrupt` at the reader's 256 KiB cap and never grows it; a flood of indexed pages
//             never grows the page index;
//   ADAPTER   a handful of mutated files through the real `VorbisAudioDecoder`: `TryOpen`, `Read`, `Seek`, `Dispose`, never a
//             throw (its own class, in the audio collection: it rents from the process-wide decoder and window pools).
//
// The iteration counts are the budget, not the ambition: about 1,000 packet decodes and a few hundred setup parses in all,
// sized to stay within roughly two seconds of a Debug build. Raise the consts to soak; `[Trait("Category", "Fuzz")]` selects it.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FluentGpu.Media;
using Wavee;
using Xunit;
using Clock = Wavee.Playback.Audio.VorbisClock;
using Ogg = Wavee.Playback.Ogg;
using Vorbis = Wavee.Playback.Vorbis;

namespace Wavee.Tests;

/// <summary>xorshift32: the same sequence on every runtime.</summary>
internal sealed class FuzzRng(uint seed)
{
    uint _s = seed == 0 ? 0x9E37_79B9u : seed;

    public uint Next()
    {
        uint x = _s;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return _s = x;
    }

    /// <summary>0 .. n−1 (0 when n ≤ 1).</summary>
    public int Below(int n) => n <= 1 ? 0 : (int)(Next() % (uint)n);

    public bool Chance(int percent) => Below(100) < percent;

    public byte Byte() => (byte)(Next() >> 11);
}

/// <summary>The mutators, shared by the core harness and the adapter harness.</summary>
internal static class VorbisFuzz
{
    /// <summary>1..<paramref name="maxEdits"/> random edits of a copy: a bit, a byte, a run of 0x00 / 0xFF / a constant, a
    /// truncation (possibly to nothing), noise appended, two bytes swapped, a run of noise. <paramref name="favourHead"/>
    /// sends a quarter of the positional edits to the first 64 bytes (the mode bits of a packet, the counts of a setup).</summary>
    public static byte[] Mutate(FuzzRng rng, byte[] source, int maxEdits, bool favourHead)
    {
        byte[] b = (byte[])source.Clone();
        int edits = 1 + rng.Below(maxEdits);
        for (int e = 0; e < edits && b.Length > 0; e++)
        {
            switch (rng.Below(9))
            {
                case 0:
                    b[At(rng, b.Length, favourHead)] ^= (byte)(1 << rng.Below(8));
                    break;
                case 1:
                    b[At(rng, b.Length, favourHead)] = rng.Byte();
                    break;
                case 2:
                    Run(rng, b, 0x00);
                    break;
                case 3:
                    Run(rng, b, 0xFF);
                    break;
                case 4:
                    Run(rng, b, rng.Byte());
                    break;
                case 5:
                    b = b.AsSpan(0, rng.Below(b.Length + 1)).ToArray();
                    break;
                case 6:
                {
                    var longer = new byte[b.Length + 1 + rng.Below(64)];
                    b.CopyTo(longer, 0);
                    for (int i = b.Length; i < longer.Length; i++) longer[i] = rng.Byte();
                    b = longer;
                    break;
                }
                case 7:
                {
                    int x = At(rng, b.Length, favourHead), y = rng.Below(b.Length);
                    (b[x], b[y]) = (b[y], b[x]);
                    break;
                }
                default:
                {
                    int start = At(rng, b.Length, favourHead), n = Math.Min(1 + rng.Below(16), b.Length - start);
                    for (int i = 0; i < n; i++) b[start + i] = rng.Byte();
                    break;
                }
            }
        }
        return b;
    }

    static int At(FuzzRng rng, int length, bool favourHead)
        => favourHead && rng.Chance(25) ? rng.Below(Math.Min(length, 64)) : rng.Below(length);

    static void Run(FuzzRng rng, byte[] b, byte value)
    {
        int start = rng.Below(b.Length);
        int n = Math.Min(1 + rng.Below(24), b.Length - start);
        b.AsSpan(start, n).Fill(value);
    }

    /// <summary>Every CRC-valid page of a clean file: offset and length.</summary>
    public static (int At, int Length)[] Pages(byte[] file)
    {
        var pages = new List<(int At, int Length)>();
        int at = 0;
        while ((at = Ogg.FindPage(file, at, out Ogg.Page p, out _)) >= 0)
        {
            pages.Add((at, p.Length));
            at += p.Length;
        }
        return [.. pages];
    }

    /// <summary>Re-seal the page at <paramref name="at"/> with the length ITS OWN lacing table now names (so a rewritten
    /// lacing table yields a valid page of another size); a page that no longer fits the buffer is left alone.</summary>
    static void Reseal(byte[] file, int at)
    {
        if (at < 0 || at + Ogg.HeaderBytes > file.Length) return;
        int segments = file[at + 26];
        int header = Ogg.HeaderBytes + segments;
        if (at + header > file.Length) return;
        int body = 0;
        for (int i = 0; i < segments; i++) body += file[at + Ogg.HeaderBytes + i];
        int length = header + body;
        if (at + length > file.Length) return;
        Span<byte> page = file.AsSpan(at, length);
        page.Slice(Ogg.CrcFieldOffset, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(Ogg.CrcFieldOffset, 4), Ogg.Crc32(page, Ogg.CrcFieldOffset));
    }

    /// <summary>A copy of a clean Ogg file with 1..3 structural or payload edits. Most land on audio pages (the first three
    /// pages are the headers, whose own fuzz is the setup test); all but the last kind re-seal the CRC, so the damage
    /// reaches the reader's packet logic and the decoder instead of being dropped as a bad page.</summary>
    public static byte[] MutateOgg(FuzzRng rng, byte[] clean, (int At, int Length)[] pages)
    {
        byte[] b = (byte[])clean.Clone();
        int edits = 1 + rng.Below(3);
        for (int e = 0; e < edits; e++)
        {
            int index = pages.Length > 3 && rng.Chance(85) ? 3 + rng.Below(pages.Length - 3) : rng.Below(pages.Length);
            (int at, int length) = pages[index];
            if (length <= Ogg.HeaderBytes + 1 || at + length > b.Length) continue;      // an earlier edit moved the page
            int header = Ogg.HeaderBytes + b[at + 26];
            int body = Math.Max(1, length - header);
            switch (rng.Below(12))
            {
                case 0:                                                   // a corrupt PACKET inside a valid page
                    for (int k = 1 + rng.Below(8); k > 0; k--)
                        b[at + Math.Min(header + rng.Below(body), length - 1)] ^= (byte)(1 << rng.Below(8));
                    Reseal(b, at);
                    break;
                case 1:
                {
                    int start = header + rng.Below(body), n = Math.Min(1 + rng.Below(48), length - start);
                    for (int k = 0; k < n; k++) b[at + start + k] = rng.Byte();
                    Reseal(b, at);
                    break;
                }
                case 2:                                                   // the granule: −1, 0, the extremes, noise, small
                {
                    long g = rng.Below(6) switch
                    {
                        0 => -1L,
                        1 => 0L,
                        2 => long.MaxValue,
                        3 => long.MinValue,
                        4 => ((long)rng.Next() << 32) | rng.Next(),
                        _ => rng.Below(1_000_000),
                    };
                    BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(at + 6, 8), g);
                    Reseal(b, at);
                    break;
                }
                case 3:                                                   // BOS / EOS / continued, any combination
                    b[at + 5] = rng.Byte();
                    Reseal(b, at);
                    break;
                case 4:                                                   // a sequence hole or a repeat
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 18, 4),
                        rng.Chance(50) ? rng.Next() : (uint)(index + 1 + rng.Below(5)));
                    Reseal(b, at);
                    break;
                case 5:                                                   // a foreign logical stream
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 14, 4), rng.Next());
                    Reseal(b, at);
                    break;
                case 6:                                                   // one lacing value: the packet boundaries move
                    if (b[at + 26] > 0) b[at + Ogg.HeaderBytes + rng.Below(b[at + 26])] = rng.Byte();
                    Reseal(b, at);
                    break;
                case 7:                                                   // the segment count itself
                    b[at + 26] = rng.Byte();
                    Reseal(b, at);
                    break;
                case 8:                                                   // the file ends here
                    Array.Resize(ref b, rng.Below(b.Length + 1));
                    break;
                case 9:                                                   // a page lost, or delivered twice
                {
                    int copies = rng.Chance(50) ? 0 : 2;
                    var next = new byte[b.Length - length + copies * length];
                    b.AsSpan(0, at).CopyTo(next);
                    for (int c = 0; c < copies; c++) b.AsSpan(at, length).CopyTo(next.AsSpan(at + c * length));
                    b.AsSpan(at + length).CopyTo(next.AsSpan(at + copies * length));
                    b = next;
                    break;
                }
                case 10:                                                  // damage the CRC cannot hide: the page must be dropped
                    b[at + rng.Below(length)] ^= (byte)(1 << rng.Below(8));
                    break;
                default:                                                  // junk between pages: the reader must resync
                {
                    int junk = 1 + rng.Below(300);
                    var next = new byte[b.Length + junk];
                    b.AsSpan(0, at).CopyTo(next);
                    for (int k = 0; k < junk; k++) next[at + k] = rng.Byte();
                    b.AsSpan(at).CopyTo(next.AsSpan(at + junk));
                    b = next;
                    break;
                }
            }
        }
        return b;
    }

    /// <summary>A real, CRC-sealed Ogg page.</summary>
    public static byte[] MakePage(uint serial, uint sequence, byte flags, long granule, ReadOnlySpan<byte> lacing,
                                  ReadOnlySpan<byte> body)
    {
        var page = new byte[Ogg.HeaderBytes + lacing.Length + body.Length];
        "OggS"u8.CopyTo(page);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6, 8), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14, 4), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18, 4), sequence);
        page[26] = (byte)lacing.Length;
        lacing.CopyTo(page.AsSpan(Ogg.HeaderBytes));
        body.CopyTo(page.AsSpan(Ogg.HeaderBytes + lacing.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(Ogg.CrcFieldOffset, 4), Ogg.Crc32(page, Ogg.CrcFieldOffset));
        return page;
    }
}

[Trait("Category", "Fuzz")]
public sealed class VorbisFuzzTests
{
    // The budget: every count here is multiplied by four seeds (two for the stream layer); the class is sized to stay within
    // about two seconds of a Debug build, where the decoder runs ~10-50× slower than Release.
    const int PacketMutationsPerSeed = 48;
    const int SetupMutationsPerSeed = 32;
    const int FileMutationsPerSeed = 32;
    const int DecodesPerMutatedFile = 3;
    const int MaxWalkSteps = 20_000;

    /// <summary>P-11's bound: one `Vorbis.Decoder.Open` may allocate at most this much, whatever the header claims.</summary>
    const long SetupAllocationLimit = 32L * 1024 * 1024;

    /// <summary>The decoder and the reader allocate nothing after `Open` / construction; this is the noise floor, not a budget.</summary>
    const long HotPathSlackBytes = 16 * 1024;

    static readonly string[] SeedNames = ["sine-440.ogg", "xiph/test-short.ogg", "xiph/48k-mono.ogg", "xiph/singlemap-test.ogg"];

    sealed record Seed(string Name, byte[] File, byte[] Ident, byte[] Setup, long FirstAudioPage, byte[][] Packets);

    static readonly Lazy<Seed[]> s_seeds = new(LoadSeeds);

    static Seed[] Seeds => s_seeds.Value;

    static Seed[] LoadSeeds()
    {
        var seeds = new Seed[SeedNames.Length];
        for (int i = 0; i < seeds.Length; i++)
        {
            byte[] file = VorbisFixture.Bytes(SeedNames[i]);
            var reader = new Ogg.Reader();
            var (ident, setup, firstAudioPage) = VorbisFixture.Headers(file, reader);
            var packets = new List<byte[]>();
            while (packets.Count < 48
                   && reader.NextPacket(file, out ReadOnlySpan<byte> packet, out _) == Ogg.Reader.Next.Packet)
                packets.Add(packet.ToArray());
            seeds[i] = new Seed(SeedNames[i], file, ident, setup, firstAudioPage, [.. packets]);
        }
        return seeds;
    }

    // ── 1. packets ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mutated_and_random_audio_packets_never_throw_and_never_allocate()
    {
        long allocated = 0;
        int ok = 0, refused = 0;
        for (int s = 0; s < Seeds.Length; s++)
        {
            Seed seed = Seeds[s];
            var rng = new FuzzRng(0xC0FFEE00u + (uint)s);
            var dec = new Vorbis.Decoder();
            Assert.True(dec.Open(seed.Ident, seed.Setup));
            for (int k = 0; k < 6; k++)
                Assert.Equal(Vorbis.PacketResult.Ok, dec.DecodePacket(seed.Packets[k]));   // warm: the decode path JIT'd

            for (int i = 0; i < PacketMutationsPerSeed; i++)
            {
                byte[] packet;
                if (rng.Chance(15))
                {
                    packet = new byte[rng.Below(700)];                                // pure noise, mostly "an audio packet"
                    for (int k = 0; k < packet.Length; k++) packet[k] = rng.Byte();
                    if (packet.Length > 0 && rng.Chance(80)) packet[0] &= 0xFE;
                }
                else packet = VorbisFuzz.Mutate(rng, seed.Packets[rng.Below(seed.Packets.Length)], 4, favourHead: true);
                if (rng.Chance(10)) dec.Prime();                                      // some decode with no previous block

                long before = GC.GetAllocatedBytesForCurrentThread();
                Vorbis.PacketResult result = dec.DecodePacket(packet);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.InRange(dec.Frames, 0, dec.MaxFrames);
                if (result == Vorbis.PacketResult.Ok) ok++;
                else
                {
                    Assert.Equal(0, dec.Frames);                                      // a refusal hands out nothing
                    refused++;
                }
            }
        }
        Print($"{ok} packets decoded, {refused} refused, {allocated} bytes allocated by DecodePacket");
        Assert.True(ok > 0);
        Assert.True(allocated <= HotPathSlackBytes, $"DecodePacket allocated {allocated:N0} bytes over mutated packets");
    }

    // ── 2. setups ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mutated_setup_headers_never_throw_stay_under_32_mib_and_leave_the_decoder_reusable()
    {
        long worst = 0;
        int opened = 0, refused = 0;
        for (int s = 0; s < Seeds.Length; s++)
        {
            Seed seed = Seeds[s];
            var rng = new FuzzRng(0x5E7B0000u + (uint)s);
            float[] fresh = DecodeStart(seed, new Vorbis.Decoder());                  // what a new decoder makes of the clean stream
            var dec = new Vorbis.Decoder();
            Assert.True(dec.Open(seed.Ident, seed.Setup));

            for (int i = 0; i < SetupMutationsPerSeed; i++)
            {
                byte[] setup = VorbisFuzz.Mutate(rng, seed.Setup, 3, favourHead: true);
                long before = GC.GetAllocatedBytesForCurrentThread();
                bool parsed = dec.Open(seed.Ident, setup);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                worst = Math.Max(worst, allocated);
                Assert.True(allocated <= SetupAllocationLimit,
                            $"{seed.Name}: setup mutation {i} allocated {allocated:N0} bytes in one Open (P-11)");
                if (!parsed)
                {
                    Assert.False(dec.IsOpen);
                    refused++;
                    continue;
                }
                opened++;
                // a setup that PARSES must decode the real packets without throwing, whatever it now means
                for (int k = 0; k < 2; k++) dec.DecodePacket(seed.Packets[k]);
            }

            // the decoder that took all of that still opens and decodes the clean stream exactly as a fresh one does
            float[] again = DecodeStart(seed, dec);
            Assert.True(MemoryMarshal.AsBytes(again.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(fresh.AsSpan())),
                        $"{seed.Name}: a decoder reused after hostile setups decodes the clean stream differently");
        }
        Print($"{opened} mutated setups parsed, {refused} refused, the largest Open allocated {worst:N0} bytes");
    }

    /// <summary>Open <paramref name="dec"/> on the seed's clean headers and decode its first 12 packets.</summary>
    static float[] DecodeStart(Seed seed, Vorbis.Decoder dec)
    {
        Assert.True(dec.Open(seed.Ident, seed.Setup));
        int count = Math.Min(12, seed.Packets.Length);
        var pcm = new float[count * dec.MaxFrames * Vorbis.OutputChannels];
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(Vorbis.PacketResult.Ok, dec.DecodePacket(seed.Packets[i]));
            dec.OutputSpan.CopyTo(pcm.AsSpan(at));
            at += dec.Frames * Vorbis.OutputChannels;
        }
        return pcm;
    }

    /// <summary>P-11, deterministically: the 15-byte shapes of a hostile header. A parser that sizes a buffer from a header
    /// field BEFORE the bytes that field promises have arrived allocates for data that never comes — 42 MB of codeword
    /// scratch for a codebook that claims 8 Mi entries, 41 MB of lookup values for one that claims 256 entries of 40,000
    /// dimensions. Refused, and under the 32 MiB bound, is the contract.</summary>
    [Theory]
    [InlineData(0x80_0000, 1, 0)]
    [InlineData(256, 40_000, 2)]
    public void A_hostile_codebook_header_is_refused_before_it_allocates_32_mib(int entries, int dims, int lookupType)
    {
        byte[] setup = HostileSetup(entries, dims, lookupType);
        var dec = new Vorbis.Decoder();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool opened = dec.Open(Seeds[0].Ident, setup);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(opened);
        Assert.True(allocated <= SetupAllocationLimit,
                    $"a {setup.Length}-byte header claiming {entries:N0} entries x {dims:N0} dims made Open allocate {allocated:N0} bytes (P-11)");
    }

    /// <summary>A setup that is one codebook and nothing after it. With <paramref name="lookupType"/> 2 the codeword lengths
    /// (256 entries, all length 8: a complete code) and the lookup header ARE present, so the parser reaches the lookup
    /// values — which are not.</summary>
    static byte[] HostileSetup(int entries, int dims, int lookupType)
    {
        var w = new LsbBitWriter();
        w.Write(5, 8);
        foreach (char c in "vorbis") w.Write(c, 8);
        w.Write(0, 8);                                  // one codebook
        w.Write(0x56_4342, 24);                         // "BCV"
        w.Write((uint)dims, 16);
        w.Write((uint)entries, 24);
        w.Write(0, 1);                                  // not ordered
        w.Write(0, 1);                                  // not sparse: one 5-bit length per entry should follow
        if (lookupType == 2)
        {
            for (int i = 0; i < entries; i++) w.Write(7, 5);
            w.Write(2, 4);                              // lookup type 2
            w.Write(0, 32);                             // minimum 0.0
            w.Write(0, 32);                             // delta 0.0
            w.Write(0, 4);                              // value bits − 1
            w.Write(0, 1);                              // not sequential — and then the stream ends
        }
        return w.ToArray();
    }

    // ── 3. streams: the reader, the decoder, the clock, the planner ─────────────────────────────────────────────────

    sealed class PassStats
    {
        public long Allocated;
        public int Decoded, Refused, Holes, Corrupt;
    }

    [Fact]
    public void Corrupt_ogg_streams_never_throw_hang_or_allocate_in_the_reader_the_decoder_the_clock_or_the_planner()
    {
        var reader = new Ogg.Reader();
        var stats = new PassStats();
        int files = 0;
        for (int s = 0; s < 2; s++)                                                    // sine-440 (floor 1) and test-short (floor 0)
        {
            Seed seed = Seeds[s];
            var rng = new FuzzRng(0x0663A000u + (uint)s);
            var dec = new Vorbis.Decoder();
            Assert.True(dec.Open(seed.Ident, seed.Setup));
            (int At, int Length)[] pages = VorbisFuzz.Pages(seed.File);
            long total = VorbisFixture.LastGranule(seed.File);
            ModelPass(seed.File, reader, dec, 8, new PassStats());                     // warm every clean path, unmeasured

            for (int i = 0; i < FileMutationsPerSeed; i++)
            {
                byte[] file = VorbisFuzz.MutateOgg(rng, seed.File, pages);
                files++;
                ModelPass(file, reader, dec, DecodesPerMutatedFile, stats);

                // the adapter's landing peek: PeekFrames over every packet to the first page granule
                _ = Clock.LandingStart(reader, dec, file, 0, out _);

                // the seek planner over whatever index the walk left behind, on a source that is the mutated file
                var src = new VorbisFixture.CountingSource(file);
                long target = rng.Below((int)(total * 12 / 10));
                Ogg.SeekPlan plan = Ogg.BeginSeek(reader.Index, seed.FirstAudioPage, file.Length, total, target, reader.MaxPageSeen);
                int probes = 0;
                while (Ogg.TryNextProbe(ref plan, out long at))
                {
                    Assert.True(++probes <= 64, "the seek planner kept asking for probes");
                    byte[] window = src.ReadAt(at, plan.WindowBytes);
                    if (Ogg.Observe(ref plan, reader.Index, window, at) == Ogg.ProbeResult.NoPage) break;
                }
                Assert.InRange(reader.Index.Count, 0, reader.Index.Capacity);
            }
        }
        Print($"{files} mutated files: {stats.Decoded} packets decoded, {stats.Refused} refused, {stats.Holes} holes, " +
              $"{stats.Corrupt} corrupt packets, {stats.Allocated} bytes allocated by the reader and the decoder");
        Assert.True(stats.Decoded > 0);
        Assert.True(stats.Allocated <= HotPathSlackBytes, $"the reader and the decoder allocated {stats.Allocated:N0} bytes");
    }

    /// <summary>The adapter's `NextFrames` loop over a whole buffer, minus the I/O: the reader's answers drive the decoder (a
    /// hole or a corrupt packet re-primes it and tells the clock), every decoded packet is placed by the clock, and the walk
    /// does not stop until the reader says so — only the first <paramref name="decodeBudget"/> audio packets are decoded, so
    /// the whole of the damage is walked by the reader and a slice of it by the decoder.</summary>
    static void ModelPass(byte[] file, Ogg.Reader reader, Vorbis.Decoder dec, int decodeBudget, PassStats stats)
    {
        reader.Reset();
        reader.Index.Clear();
        dec.Prime();
        var clock = Clock.At(0);
        int here = 0;
        for (int step = 0; ; step++)
        {
            Assert.True(step < MaxWalkSteps, "the reader did not reach the end of the buffer");
            long before = GC.GetAllocatedBytesForCurrentThread();
            Ogg.Reader.Next next = reader.NextPacket(file, out ReadOnlySpan<byte> packet, out long granule);
            stats.Allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            if (next is Ogg.Reader.Next.NeedMore or Ogg.Reader.Next.Eos) break;
            if (next is Ogg.Reader.Next.Corrupt or Ogg.Reader.Next.Hole)
            {
                if (next == Ogg.Reader.Next.Corrupt) stats.Corrupt++; else stats.Holes++;
                dec.Prime();
                clock.Hole();
                continue;
            }
            Assert.InRange(reader.OpenPacketBytes, 0, reader.MaxPacketBytes);
            if (here >= decodeBudget) continue;

            before = GC.GetAllocatedBytesForCurrentThread();
            Vorbis.PacketResult result = dec.DecodePacket(packet);
            stats.Allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            if (result == Vorbis.PacketResult.NotAudio) continue;                      // a header packet inside the walk
            here++;
            stats.Decoded++;
            if (result != Vorbis.PacketResult.Ok)
            {
                stats.Refused++;
                dec.Prime();
                clock.Hole();
                continue;
            }
            Clock.Run run = clock.Admit(dec.Frames, granule, granule >= 0 && reader.SawEos);
            Assert.True(run.Skip >= 0 && run.Count >= 0 && run.Pad >= 0 && run.Skip + run.Count <= dec.Frames,
                        "the clock handed out a run that does not fit the packet");
        }
    }

    // ── 4. oversize: nothing grows ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_packet_that_never_ends_is_corrupt_and_a_flood_of_pages_never_grows_the_reader()
    {
        // Six pages of 255 lacing values of 255, each continuing the packet of the page before: 390 KB for ONE packet, 1.5×
        // the reader's 256 KiB cap. The packet never ends, so nothing is ever returned; the cap is `Corrupt`, not a bigger buffer.
        const int Pages = 6;
        var lacing = new byte[255];
        lacing.AsSpan().Fill(255);
        var body = new byte[255 * 255];
        var stream = new List<byte>(Pages * (Ogg.HeaderBytes + lacing.Length + body.Length));
        for (int i = 0; i < Pages; i++)
            stream.AddRange(VorbisFuzz.MakePage(7, (uint)i, i == 0 ? (byte)0 : Ogg.FlagContinued, Ogg.NoGranule, lacing, body));
        byte[] endless = [.. stream];

        var reader = new Ogg.Reader();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool corrupt = false;
        for (int step = 0; step < 16; step++)
        {
            Ogg.Reader.Next next = reader.NextPacket(endless, out _, out _);
            if (next == Ogg.Reader.Next.Corrupt) corrupt = true;
            Assert.NotEqual(Ogg.Reader.Next.Packet, next);
            if (next is Ogg.Reader.Next.NeedMore or Ogg.Reader.Next.Eos) break;
            Assert.InRange(reader.OpenPacketBytes, 0, reader.MaxPacketBytes);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(corrupt, "a packet past the cap was not reported corrupt");
        Assert.True(allocated <= 4096, $"walking an endless packet allocated {allocated:N0} bytes");

        // A flood: 10,000 one-packet pages, every one carrying a granule, so every one is indexed. The index is bounded.
        const int Flood = 10_000;
        var oneLace = new byte[] { 1 };
        var oneByte = new byte[] { 0 };
        var pages = new List<byte>(Flood * (Ogg.HeaderBytes + 2));
        for (int i = 0; i < Flood; i++)
            pages.AddRange(VorbisFuzz.MakePage(9, (uint)i, 0, i * 64L, oneLace, oneByte));
        byte[] flood = [.. pages];

        var floodReader = new Ogg.Reader();
        int capacity = floodReader.Index.Capacity;
        before = GC.GetAllocatedBytesForCurrentThread();
        int packets = 0;
        while (floodReader.NextPacket(flood, out _, out _) == Ogg.Reader.Next.Packet) packets++;
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Flood, packets);
        Assert.Equal(capacity, floodReader.Index.Capacity);
        Assert.InRange(floodReader.Index.Count, 1, capacity);
        Assert.True(allocated <= 4096, $"walking {Flood:N0} indexed pages allocated {allocated:N0} bytes");
    }

    static void Print(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
}

/// <summary>The same mutated files through the real adapter. Its own class and in the audio collection (which runs alone):
/// <c>VorbisAudioDecoder</c> rents its decoder and window from process-wide pools, and a hostile setup left in the pool must
/// not be observed by a test running beside it.</summary>
[Collection(AudioStreamCollection.Name)]
[Trait("Category", "Fuzz")]
public sealed class VorbisAdapterFuzzTests
{
    const int FilesPerSeed = 8;
    const int BlockFrames = 1_024;

    [Fact]
    public void Corrupt_files_never_throw_past_the_vorbis_adapter()
    {
        var mix = new MixFormat(44_100, 2);
        var block = new float[BlockFrames * 2];
        int opened = 0, reads = 0;
        int seed = 0;
        foreach (string name in new[] { "sine-440.ogg", "xiph/test-short.ogg" })
        {
            byte[] clean = VorbisFixture.Bytes(name);

            // the harness itself: the clean file opens and plays, so a refusal below is the mutation's doing
            var control = new Playback.Audio.VorbisAudioDecoder(0f);
            try
            {
                Assert.True(control.TryOpen(new BytesSource(clean), mix, out _));
                Assert.True(control.Read(block) > 0);
            }
            finally { control.Dispose(); }

            (int At, int Length)[] pages = VorbisFuzz.Pages(clean);
            var rng = new FuzzRng(0xADA97E00u + (uint)seed++);
            for (int i = 0; i < FilesPerSeed; i++)
            {
                byte[] file = VorbisFuzz.MutateOgg(rng, clean, pages);
                var decoder = new Playback.Audio.VorbisAudioDecoder(0f);
                try
                {
                    if (!decoder.TryOpen(new BytesSource(file), mix, out _)) continue;
                    opened++;
                    for (int r = 0; r < 3; r++)
                    {
                        int n = decoder.Read(block);
                        reads++;
                        Assert.InRange(n, 0, BlockFrames);
                        if (n == 0) break;
                    }
                    decoder.Seek(rng.Below(300_000));                                     // a seek inside, or past, the damage
                    for (int r = 0; r < 2; r++)
                    {
                        int n = decoder.Read(block);
                        reads++;
                        Assert.InRange(n, 0, BlockFrames);
                        if (n == 0) break;
                    }
                }
                finally { decoder.Dispose(); }
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{opened} mutated files opened, {reads} reads, no exception");
        Assert.True(opened > 0, "no mutated file opened: the loop proved nothing");
    }

    /// <summary>A byte array behind the engine's plain sequential face — a local file, as the adapter sees one.</summary>
    sealed class BytesSource(byte[] data) : IMediaByteSource
    {
        long _cursor;

        public long? Length => data.Length;
        public SourceCaps Caps => new() { Seekable = true, KnownLength = true, ExpensiveSeek = false };
        public bool TryOpen(in DataSpec spec) { _cursor = Math.Max(0, spec.Position); return true; }

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
}
