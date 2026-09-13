// ── Wavee.Tests/AudioAdapterTests.cs — the engine-facing adapters over the new byte path (Vorbis plan §6, owner H) ──
//
// `Playback/Playback.Audio.cs` hands the engine a `VorbisAudioDecoder` over the CORE Ogg reader and Vorbis decoder, and
// reads every Spotify format through `RingSource`, a thin face over the stream layer's `Body`. The decisions between the
// CORE and the engine are extracted into `Playback.Audio.VorbisClock` and pinned here against the real fixtures, with no
// device and no network:
//
//   THE FRAME ACCOUNTING. Every packet is placed by the page that ends it, exactly where a straight decode puts it, and
//   the landing peek places the first frame before a single packet of a seek decodes.
//
//   THE GAPLESS TRIM. The EOS granule is the exact length. A stream that starts before granule 0 reports its lead-in in
//   MIX frames, and the engine trimming it leaves exactly the exact length.
//
//   THE GAIN. One factor, clamped, capped by a known peak, exactly 1 when off — and folded into the decoder's interleave
//   multiply bit for bit.
//
//   THE SEEK HAND-OFF. Planner page → landing peek → the clock's drop: the first frame out is the target sample on every
//   fixture. Then the adapter itself, over a random-access fake that counts its re-targets and resumes, plays every
//   fixture bit-exact and seeks sample-exact, cold and warm.
//
// The FLAC adapter moved onto the same byte path, so it must decode the same samples through `ReadAt` as through `Read`
// and seek through `Retarget` / `ResumeFrom`. `RingSource` has one rule of its own — a probe covers the whole window it
// was asked for, even across a slot edge — pinned over a counting fake CDN in the stream layer's collection.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FluentGpu.Media;
using Wavee;
using Xunit;
using Clock = Wavee.Playback.Audio.VorbisClock;
using Flac = Wavee.Playback.Flac;
using Ogg = Wavee.Playback.Ogg;
using Vorbis = Wavee.Playback.Vorbis;

namespace Wavee.Tests;

public sealed class AudioAdapterTests(ITestOutputHelper output)
{
    public static TheoryData<string> Fixtures => new()
    {
        "pink-320.ogg", "pink-96.ogg", "vbr-q8.ogg", "sine-440.ogg", "sweep-48k.ogg", "pages-100ms.ogg",
    };

    // ── 1. the frame accounting and the end trim ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_clock_places_every_packet_on_its_page_granule_and_trims_the_end_at_the_eos_granule(string name)
    {
        byte[] file = VorbisFixture.Bytes(name);
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        var reader = new Ogg.Reader();
        Vorbis.Decoder dec = VorbisFixture.Open(file, reader, out long firstAudioPage);
        ReadOnlySpan<byte> audio = file.AsSpan((int)firstAudioPage);

        long origin = Clock.LandingStart(reader, dec, audio, firstAudioPage, out bool needMore);
        Assert.False(needMore);
        Assert.Equal(-linear.LeadIn, origin);

        var clock = Clock.At(origin);
        var pcm = new float[linear.Pcm.Length];
        long emitted = 0, cut = 0;
        int pinned = 0;
        while (true)
        {
            Ogg.Reader.Next next = reader.NextPacket(audio, out ReadOnlySpan<byte> packet, out long granule);
            if (next == Ogg.Reader.Next.Corrupt) continue;
            if (next != Ogg.Reader.Next.Packet) break;
            Assert.Equal(Vorbis.PacketResult.Ok, dec.DecodePacket(packet));
            bool eos = granule >= 0 && reader.SawEos;
            long running = clock.Position;
            Clock.Run run = clock.Admit(dec.Frames, granule, eos);
            Assert.Equal(running, run.Start);                         // on a clean stream the page and the count agree
            Assert.Equal(0, run.Skip);
            if (granule >= 0 && !eos)
            {
                Assert.Equal(granule, run.Start + dec.Frames);        // §A.2: the granule ENDS the packet
                pinned++;
            }
            dec.OutputSpan.Slice(0, run.Count * 2).CopyTo(pcm.AsSpan((int)(emitted * 2)));
            emitted += run.Count;
            cut += dec.Frames - run.Count;
        }

        Assert.Equal(linear.LastGranule, emitted);
        Assert.Equal(linear.Untrimmed - linear.Frames, cut);
        Assert.True(pinned > 1, name + ": no page granule pinned the clock");
        Assert.True(MemoryMarshal.AsBytes(pcm.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(linear.Pcm.AsSpan())),
            name + ": the clock's frames differ from the straight decode");
        output.WriteLine($"{name}: {emitted:N0} frames from origin {origin}, {pinned} page pins, {cut} cut at the EOS granule");
    }

    [Fact]
    public void A_stream_that_starts_before_granule_zero_reports_its_lead_in_in_mix_frames()
    {
        byte[] original = VorbisFixture.Bytes("pink-320.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(original);
        VorbisFixture.Open(original, new Ogg.Reader(), out long firstAudioPage);
        byte[] shifted = ShiftAudioGranules(original, firstAudioPage, -1_000);

        var reader = new Ogg.Reader();
        Vorbis.Decoder dec = VorbisFixture.Open(shifted, reader, out long first);
        Assert.Equal(firstAudioPage, first);
        ReadOnlySpan<byte> audio = shifted.AsSpan((int)first);
        long origin = Clock.LandingStart(reader, dec, audio, first, out _);
        long last = VorbisFixture.LastGranule(shifted);
        Assert.Equal(-1_000L, origin);
        Assert.Equal(linear.LastGranule - 1_000, last);

        Assert.Equal(new GaplessInfo(1_000, 0, last, TailKnown: true), Clock.Gapless(origin, last, 44_100, 44_100));
        GaplessInfo at48 = Clock.Gapless(origin, last, 44_100, 48_000);
        Assert.Equal(1_088, at48.LeadInFrames);                       // 1,000 × 48 / 44.1 = 1,088.4
        Assert.Equal((long)Math.Round(last * 48_000d / 44_100), at48.ExactFrames);

        // The decoder emits everything from the origin — the lead-in is audio, and the ENGINE trims it — so skipping the
        // reported lead-in leaves exactly ExactFrames, and the samples are the unshifted file's.
        var clock = Clock.At(origin);
        var pcm = new float[linear.Pcm.Length];
        long emitted = 0;
        while (true)
        {
            Ogg.Reader.Next next = reader.NextPacket(audio, out ReadOnlySpan<byte> packet, out long granule);
            if (next == Ogg.Reader.Next.Corrupt) continue;
            if (next != Ogg.Reader.Next.Packet) break;
            Assert.Equal(Vorbis.PacketResult.Ok, dec.DecodePacket(packet));
            Clock.Run run = clock.Admit(dec.Frames, granule, granule >= 0 && reader.SawEos);
            dec.OutputSpan.Slice(run.Skip * 2, run.Count * 2).CopyTo(pcm.AsSpan((int)(emitted * 2)));
            emitted += run.Count;
        }
        Assert.Equal(linear.Frames, emitted);
        Assert.Equal(last, emitted - 1_000);
        Assert.True(MemoryMarshal.AsBytes(pcm.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(linear.Pcm.AsSpan())));
    }

    // ── 2. the gain ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_gain_is_one_clamped_factor_capped_by_a_known_peak_and_folded_into_the_interleave()
    {
        float minus6 = MathF.Pow(10f, -6f / 20f), plus6 = MathF.Pow(10f, 6f / 20f);
        Assert.Equal(1f, Playback.Audio.NormalizationFactor(enabled: false, -6f));
        Assert.Equal(1f, Playback.Audio.NormalizationFactor(enabled: true, 0f));
        Assert.Equal(1f, Playback.Audio.NormalizationFactor(enabled: true, float.NaN));
        Assert.Equal(minus6, Playback.Audio.NormalizationFactor(enabled: true, -6f));
        Assert.Equal(plus6, Playback.Audio.NormalizationFactor(enabled: true, 6f));
        Assert.Equal(1f / 0.9f, Playback.Audio.NormalizationFactor(enabled: true, 6f, peak: 0.9f));   // 1.995 × 0.9 > 1
        Assert.Equal(minus6, Playback.Audio.NormalizationFactor(enabled: true, -6f, peak: 0.9f));     // a cut is never capped
        Assert.Equal(MathF.Pow(10f, Playback.Audio.MaxGainDb / 20f), Playback.Audio.NormalizationFactor(enabled: true, 400f));

        // One multiply in the interleave: the output at a gain is the unity output × the factor, to the bit.
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        var (ident, setup, first) = VorbisFixture.Headers(file, new Ogg.Reader());
        float factor = Playback.Audio.NormalizationFactor(enabled: true, -3.5f);
        var unity = new Vorbis.Decoder();
        var gained = new Vorbis.Decoder();
        Assert.True(unity.Open(ident, setup));
        Assert.True(gained.Open(ident, setup, factor));
        var unityReader = new Ogg.Reader();
        var gainedReader = new Ogg.Reader();
        Clock.Restart(unityReader, first);
        Clock.Restart(gainedReader, first);
        ReadOnlySpan<byte> audio = file.AsSpan((int)first);
        long samples = 0, mismatches = 0;
        for (int p = 0; p < 60; p++)
        {
            Assert.Equal(Ogg.Reader.Next.Packet, unityReader.NextPacket(audio, out ReadOnlySpan<byte> a, out _));
            Assert.Equal(Ogg.Reader.Next.Packet, gainedReader.NextPacket(audio, out ReadOnlySpan<byte> b, out _));
            Assert.Equal(Vorbis.PacketResult.Ok, unity.DecodePacket(a));
            Assert.Equal(Vorbis.PacketResult.Ok, gained.DecodePacket(b));
            Assert.Equal(unity.Frames, gained.Frames);
            ReadOnlySpan<float> u = unity.OutputSpan, g = gained.OutputSpan;
            for (int i = 0; i < u.Length; i++) if (u[i] * factor != g[i]) mismatches++;
            samples += u.Length;
        }
        Assert.True(samples > 100_000);
        Assert.Equal(0L, mismatches);
    }

    // ── 3. the seek hand-off ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_landing_peek_places_the_first_frame_and_the_clock_hands_out_the_target_sample(string name)
    {
        byte[] file = VorbisFixture.Bytes(name);
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        long total = linear.Frames;
        var reader = new Ogg.Reader();
        Vorbis.Decoder dec = VorbisFixture.Open(file, reader, out long firstAudioPage);
        VorbisFixture.StartPlayback(file, reader, dec);
        var src = new VorbisFixture.CountingSource(file);
        const int want = 4_096;

        foreach (long target in new[] { total * 70 / 100, total * 72 / 100, total / 10, 100, 0, total * 95 / 100, total - 10, total / 3 })
        {
            Ogg.SeekPlan plan = VorbisFixture.Plan(file, reader, firstAudioPage, total, target, src);
            ReadOnlySpan<byte> rest = file.AsSpan((int)plan.Offset);
            long start = Clock.LandingStart(reader, dec, rest, plan.Offset, out bool needMore);
            Assert.False(needMore);
            Assert.NotEqual(Clock.Unknown, start);
            Assert.True(start <= target, $"{name} → {target}: the landing starts at {start}, past the target");

            dec.Prime();
            var clock = Clock.At(start);
            clock.Target = target;
            int compare = (int)Math.Min(want, total - target);
            var got = new float[compare * 2];
            int filled = 0;
            long firstOut = Clock.Unknown;
            while (filled < compare)
            {
                Ogg.Reader.Next next = reader.NextPacket(rest, out ReadOnlySpan<byte> packet, out long granule);
                if (next == Ogg.Reader.Next.Corrupt) continue;
                Assert.Equal(Ogg.Reader.Next.Packet, next);
                if (dec.DecodePacket(packet) != Vorbis.PacketResult.Ok) continue;
                Clock.Run run = clock.Admit(dec.Frames, granule, granule >= 0 && reader.SawEos);
                if (run.Count == 0) continue;
                if (firstOut == Clock.Unknown) firstOut = run.Start + run.Skip;
                int n = Math.Min(run.Count, compare - filled);
                dec.OutputSpan.Slice(run.Skip * 2, n * 2).CopyTo(got.AsSpan(filled * 2));
                filled += n;
            }

            Assert.Equal(target, firstOut);
            Assert.True(MemoryMarshal.AsBytes(got.AsSpan()).SequenceEqual(
                    MemoryMarshal.AsBytes(linear.Pcm.AsSpan((int)(target * 2), compare * 2))),
                $"{name} → {target}: the handed-out samples differ from the straight decode");
            output.WriteLine($"{name}: target {target,7} → page {plan.Offset,7} ({plan.Tier}, {plan.Probes} probes), first frame {start}");
        }
    }

    [Fact]
    public void The_seek_frame_and_the_reached_frame_convert_between_the_mix_and_the_granules()
    {
        Assert.Equal(441_000L, Clock.TargetGranule(480_000, 44_100, 48_000, origin: 0));
        Assert.Equal(480_000L, Clock.MixFrameOf(441_000, 44_100, 48_000, origin: 0));
        Assert.Equal(0L, Clock.TargetGranule(-5, 44_100, 44_100, origin: 0));          // never before the first frame

        // A lead-in: the engine's TrimmingSource seeks its voice to `frame + LeadInFrames`, so post-trim frame 0 is
        // granule 0, and a reached granule comes back counted from the origin.
        GaplessInfo g = Clock.Gapless(origin: -1_000, lastGranule: 440_000, 44_100, 44_100);
        Assert.Equal(0L, Clock.TargetGranule(0 + g.LeadInFrames, 44_100, 44_100, origin: -1_000));
        Assert.Equal((long)g.LeadInFrames, Clock.MixFrameOf(0, 44_100, 44_100, origin: -1_000));
        Assert.Equal(g.ExactFrames + g.LeadInFrames, Clock.MixFrameOf(440_000, 44_100, 44_100, origin: -1_000));

        // A stream cut out of a longer one starts at a positive granule: no lead-in, and the length counts from there.
        Assert.Equal(new GaplessInfo(0, 0, 300_000, TailKnown: true), Clock.Gapless(origin: 50_000, lastGranule: 350_000, 48_000, 48_000));
        Assert.Equal(50_000L, Clock.TargetGranule(0, 48_000, 48_000, origin: 50_000));

        // No tail: the None shape, so the engine keeps the bare voice and the declared duration.
        Assert.Equal(GaplessInfo.None, Clock.Gapless(origin: 0, lastGranule: -1, 44_100, 48_000));

        for (long f = 0; f < 5_000_000; f += 99_991)
            Assert.Equal(f, Clock.MixFrameOf(Clock.TargetGranule(f, 48_000, 48_000, 0), 48_000, 48_000, 0));
    }

    [Fact]
    public void A_tail_granule_is_believed_only_near_the_declared_duration()
    {
        Assert.True(Clock.PlausibleTail(441_000, durationMs: 10_000, 44_100));
        Assert.True(Clock.PlausibleTail(441_000 + 5 * 44_100, durationMs: 10_000, 44_100));
        Assert.False(Clock.PlausibleTail(441_000 + 5 * 44_100 + 1, durationMs: 10_000, 44_100));   // a false `OggS`
        Assert.False(Clock.PlausibleTail(-1, durationMs: 10_000, 44_100));
        Assert.True(Clock.PlausibleTail(12_345, durationMs: 0, 44_100));                          // nothing to compare with
        Assert.True(Clock.PlausibleTail(240L * 44_100 + 500_000, durationMs: 240_000, 44_100));   // 5 % of four minutes

        foreach (string name in VorbisFixture.All)
        {
            byte[] file = VorbisFixture.Bytes(name);
            Assert.Equal(VorbisFixture.LastGranule(file), Clock.LastGranule(file.AsSpan(Math.Max(0, file.Length - 64 * 1024))));
        }
    }

    // ── 4. the adapters themselves, over in-memory byte seams ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("pink-320.ogg", true, true)]
    [InlineData("vbr-q8.ogg", true, false)]          // the tail not read yet at open: the decoder trims at EOS itself
    [InlineData("sine-440.ogg", true, true)]         // mono: the mix format is still stereo
    [InlineData("sweep-48k.ogg", false, true)]       // a local file: sequential, the tail scanned at open
    [InlineData("pages-100ms.ogg", true, false)]
    public void The_vorbis_adapter_plays_bit_exact_and_seeks_sample_exact(string name, bool randomAccess, bool tailAtOpen)
    {
        byte[] file = VorbisFixture.Bytes(name);
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        long durationMs = linear.Frames * 1000 / id.SampleRate;
        var mix = new MixFormat(id.SampleRate, 2);
        IMediaByteSource NewSource() => randomAccess
            ? new RandomAccessFake(file, tailAtOpen ? linear.LastGranule : -1)
            : new SequentialFake(file);

        IMediaByteSource source = NewSource();
        var decoder = new Playback.Audio.VorbisAudioDecoder(0f, durationMs);
        Assert.True(decoder.TryOpen(source, mix, out DecodedInfo info));
        Assert.Equal(new MixFormat(id.SampleRate, Vorbis.OutputChannels), info.SourceFormat);
        Assert.Equal(CodecId.Vorbis, info.Codec.Audio);
        Assert.Equal(tailAtOpen ? new GaplessInfo(0, 0, linear.Frames, TailKnown: true) : GaplessInfo.None, decoder.Gapless);

        float[] all = Drain(decoder, linear.Frames + 8_192);
        Assert.Equal(linear.Pcm.Length, all.Length);
        Assert.True(MemoryMarshal.AsBytes(all.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(linear.Pcm.AsSpan())),
            name + ": the adapter's samples differ from the straight decode");
        if (source is RandomAccessFake played) Assert.Equal(0, played.Reads);

        // Cold: a fresh decoder, only the first pages indexed. Warm: the decoder that played it all.
        IMediaByteSource coldSource = NewSource();
        var cold = new Playback.Audio.VorbisAudioDecoder(0f, durationMs);
        Assert.True(cold.TryOpen(coldSource, mix, out _));
        long target = linear.Frames * 7 / 10;
        foreach ((Playback.Audio.VorbisAudioDecoder d, IMediaByteSource s, bool warm) in
                 new[] { (cold, coldSource, false), (decoder, source, true) })
        {
            (s as RandomAccessFake)?.ResetCounts();
            Assert.Equal(target, d.Seek(target));
            float[] after = Drain(d, 4_096);
            Assert.Equal(4_096 * 2, after.Length);
            Assert.True(MemoryMarshal.AsBytes(after.AsSpan()).SequenceEqual(
                MemoryMarshal.AsBytes(linear.Pcm.AsSpan((int)(target * 2), after.Length))),
                $"{name}: the {(warm ? "warm" : "cold")} seek landed on the wrong samples");
            if (s is RandomAccessFake counted)
            {
                Assert.Equal(0, counted.Reads);                           // the random-access face only
                Assert.Equal(1, counted.Resumes);                         // the fill resumes once, at the landing
                if (warm) Assert.Equal(1, counted.Retargets);             // every page indexed: no probe, just the landing
                else Assert.InRange(counted.Retargets, 1, 9);             // ≤ 8 probes + the landing
                output.WriteLine($"{name}: {(warm ? "warm" : "cold")} seek to {target}: {counted.Retargets} re-targets");
            }
        }

        // At or past the end: nothing more plays, and nothing throws.
        Assert.True(decoder.Seek(linear.Frames + 10_000) >= 0);
        Assert.Equal(0, decoder.Read(new float[512]));
    }

    [Fact]
    public void The_flac_adapter_reads_the_same_samples_through_the_random_access_face_and_seeks_through_it()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flac", "subset",
            "47 - only STREAMINFO.flac"));
        var points = new Flac.SeekPoint[8];
        Flac.Headers h = Flac.ParseHeaders(file, points);
        Assert.True(h.Valid);
        Assert.True(h.Info.TotalSamples > 0);
        var mix = new MixFormat(h.Info.SampleRate, 2);

        var reference = new Playback.Audio.FlacAudioDecoder(0f);
        Assert.True(reference.TryOpen(new SequentialFake(file), mix, out DecodedInfo sequentialInfo));
        float[] expected = Drain(reference, h.Info.TotalSamples + 8_192);
        Assert.Equal(h.Info.TotalSamples * 2, expected.Length);

        var ra = new RandomAccessFake(file, tail: -1);
        var decoder = new Playback.Audio.FlacAudioDecoder(0f);
        Assert.True(decoder.TryOpen(ra, mix, out DecodedInfo randomInfo));
        Assert.Equal(sequentialInfo.SourceFormat, randomInfo.SourceFormat);
        Assert.Equal(reference.Gapless, decoder.Gapless);
        float[] got = Drain(decoder, h.Info.TotalSamples + 8_192);
        Assert.True(MemoryMarshal.AsBytes(got.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(expected.AsSpan())));
        Assert.Equal(0, ra.Reads);

        ra.ResetCounts();
        long target = h.Info.TotalSamples * 6 / 10;
        Assert.Equal(target, decoder.Seek(target));
        float[] after = Drain(decoder, 4_096);
        Assert.Equal(4_096 * 2, after.Length);
        Assert.True(MemoryMarshal.AsBytes(after.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(expected.AsSpan((int)(target * 2), after.Length))));
        Assert.Equal(0, ra.Reads);
        Assert.Equal(1, ra.Resumes);
        Assert.InRange(ra.Retargets, 1, 33);                              // ≤ 32 probes + the landing
        output.WriteLine($"flac seek to {target}: {ra.Retargets} re-targets");
    }

    // ── 5. a seek's interrupt: silence, never the end ───────────────────────────────────────────────────────────────

    /// <summary>A seek interrupts the decoder's blocked byte wait (the engine's SeekAsync waits for the decode-ahead
    /// producer, and a producer blocked in an underrun would sit out the ring's 8 s bound). The engine latches the first
    /// non-positive read as EOF, so an interrupted read must come out of the adapter as SILENCE — and the seek that follows
    /// must land sample-exact exactly as if nothing had happened.</summary>
    [Fact]
    public void An_interrupted_vorbis_read_is_silence_not_the_end_and_the_seek_that_follows_is_sample_exact()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        var mix = new MixFormat(id.SampleRate, 2);
        var source = new RandomAccessFake(file, linear.LastGranule);
        var decoder = new Playback.Audio.VorbisAudioDecoder(0f, linear.Frames * 1000 / id.SampleRate);
        Assert.True(decoder.TryOpen(source, mix, out _));
        Assert.Equal(8_192 * 2, Drain(decoder, 8_192).Length);

        source.Interrupt = true;
        Assert.Equal(1, SilentBlocksUntilNoEof(decoder, blocks: 3));

        long target = linear.Frames / 2;
        Assert.Equal(target, decoder.Seek(target));
        Assert.False(source.Interrupt);                                   // the seek's own retarget ended it
        float[] after = Drain(decoder, 4_096);
        Assert.Equal(4_096 * 2, after.Length);
        Assert.True(MemoryMarshal.AsBytes(after.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(linear.Pcm.AsSpan((int)(target * 2), after.Length))));
    }

    [Fact]
    public void An_interrupted_flac_read_is_silence_not_the_end_and_the_seek_that_follows_is_sample_exact()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flac", "subset",
            "47 - only STREAMINFO.flac"));
        var points = new Flac.SeekPoint[8];
        Flac.Headers h = Flac.ParseHeaders(file, points);
        var mix = new MixFormat(h.Info.SampleRate, 2);
        var reference = new Playback.Audio.FlacAudioDecoder(0f);
        Assert.True(reference.TryOpen(new SequentialFake(file), mix, out _));
        float[] expected = Drain(reference, h.Info.TotalSamples + 8_192);

        var source = new RandomAccessFake(file, tail: -1);
        var decoder = new Playback.Audio.FlacAudioDecoder(0f);
        Assert.True(decoder.TryOpen(source, mix, out _));
        Assert.Equal(4_096 * 2, Drain(decoder, 4_096).Length);

        source.Interrupt = true;
        Assert.Equal(1, SilentBlocksUntilNoEof(decoder, blocks: 3));

        long target = h.Info.TotalSamples / 2;
        Assert.Equal(target, decoder.Seek(target));
        Assert.False(source.Interrupt);
        float[] after = Drain(decoder, 4_096);
        Assert.Equal(4_096 * 2, after.Length);
        Assert.True(MemoryMarshal.AsBytes(after.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(expected.AsSpan((int)(target * 2), after.Length))));
    }

    /// <summary>Read until <paramref name="blocks"/> all-silent blocks came out, asserting no read ever answers ≤ 0 (what
    /// the engine would latch as the end). Returns 1 when the silent blocks arrived, 0 when the bound ran out first.</summary>
    static int SilentBlocksUntilNoEof(IAudioDecoder decoder, int blocks)
    {
        var block = new float[1_024 * 2];
        int silent = 0;
        for (int i = 0; i < 20_000 && silent < blocks; i++)
        {
            int n = decoder.Read(block);
            Assert.True(n > 0, $"read {i} answered {n}: an interrupted read must never look like the end of the track");
            if (block.AsSpan(0, n * 2).IndexOfAnyExcept(0f) < 0) silent++;
        }
        return silent == blocks ? 1 : 0;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Up to <paramref name="maxFrames"/> frames of interleaved stereo, read the way the engine's producer reads.</summary>
    static float[] Drain(IAudioDecoder decoder, long maxFrames)
    {
        var all = new List<float>();
        var block = new float[1_024 * 2];
        long frames = 0;
        while (frames < maxFrames)
        {
            int n = decoder.Read(block);
            if (n <= 0) break;
            int take = (int)Math.Min(n, maxFrames - frames);
            for (int i = 0; i < take * 2; i++) all.Add(block[i]);
            frames += take;
        }
        return [.. all];
    }

    /// <summary>The file with every audio page's granule moved by <paramref name="delta"/> and its CRC re-sealed.</summary>
    static byte[] ShiftAudioGranules(byte[] file, long firstAudioPage, long delta)
    {
        byte[] copy = (byte[])file.Clone();
        int at = (int)firstAudioPage;
        while ((at = Ogg.FindPage(copy, at, out Ogg.Page page, out _)) >= 0)
        {
            if (page.Granule >= 0)
            {
                Span<byte> whole = copy.AsSpan(at, page.Length);
                BitConverter.TryWriteBytes(whole.Slice(6, 8), page.Granule + delta);
                BitConverter.TryWriteBytes(whole.Slice(Ogg.CrcFieldOffset, 4), Ogg.Crc32(whole, Ogg.CrcFieldOffset));
            }
            at += page.Length;
        }
        return copy;
    }

    /// <summary>A byte array behind the random-access face, answering short reads at 64 KiB slot edges the way the ring
    /// does, and counting what the adapter asked of it.</summary>
    internal sealed class RandomAccessFake(byte[] data, long tail) : IMediaByteSource, Playback.Audio.IRandomAccessBytes
    {
        const int Slot = 64 * 1024;
        long _cursor;

        public int Retargets, Resumes, Reads;
        /// <summary>A seek's interrupt, as the ring answers it: every read says <c>InterruptedRead</c> until the adapter's
        /// own <see cref="Retarget"/> arrives and ends it.</summary>
        public bool Interrupt;
        public uint Epoch { get; private set; }
        public long TailGranule => tail;
        public long? Length => data.Length;
        public SourceCaps Caps => new() { Seekable = true, KnownLength = true, ExpensiveSeek = false };

        public void ResetCounts() => Retargets = Resumes = Reads = 0;

        public bool TryOpen(in DataSpec spec) { _cursor = Math.Max(0, spec.Position); return true; }

        public int Read(Span<byte> dst)
        {
            Reads++;
            int n = ReadAt(_cursor, dst, Epoch);
            if (n > 0) _cursor += n;
            return n;
        }

        public long Seek(long offset) => _cursor = Math.Max(0, offset);

        public void Cancel() { }

        public void Close() { }

        public int ReadAt(long offset, Span<byte> dst, uint epoch)
        {
            if (epoch != Epoch) return -1;
            if (Interrupt) return Playback.Audio.InterruptedRead;
            if (offset >= data.Length || dst.Length == 0) return 0;
            int toSlotEdge = (int)(Slot - offset % Slot);
            int n = (int)Math.Min(Math.Min(dst.Length, toSlotEdge), data.Length - offset);
            data.AsSpan((int)offset, n).CopyTo(dst);
            return n;
        }

        public void Retarget(long probeOffset, int probeBytes, uint epoch) { Retargets++; Epoch = epoch; Interrupt = false; }

        public void ResumeFrom(long offset) => Resumes++;
    }

    /// <summary>A byte array behind the engine's plain sequential face — a local file, as the adapters see one.</summary>
    internal sealed class SequentialFake(byte[] data) : IMediaByteSource
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

/// <summary><see cref="Playback.Audio.RingSource"/> over the real stream layer and a counting fake CDN. It runs in the
/// stream layer's collection: the fetch thread is timing-honest and must not share a loaded runner.</summary>
[Collection(AudioStreamCollection.Name)]
public sealed class RingSourceTests
{
    const int Slot = Spotify.Audio.Ring.SlotBytes;

    [Fact]
    public void A_probe_just_short_of_a_slot_edge_covers_the_whole_window_in_one_range()
    {
        var file = new byte[3 * 1024 * 1024];
        new Random(20260913).NextBytes(file);
        var cdn = new CountingCdn(file);
        using var fetcher = new Spotify.Audio.Fetcher();
        var body = new Spotify.Audio.Body(cdn, ["https://cdn.test/audio"], key: null, skip: 0, fileLength: file.Length,
            lengthKnown: true, durationMs: 60_000, fmt: Spotify.Audio.Format.Flac, gainDb: 0f,
            fileIdHex: "00ff00ff00ff00ff00ff00ff00ff00ff", head: null, disk: null, fetcher: fetcher);
        var source = new Playback.Audio.RingSource(body);
        body.Start();
        WaitUntil(() => fetcher.Outstanding == 0, "the opening fill to finish");
        int before = cdn.Count;

        // 1,000 bytes short of the edge between chunks 39 and 40 — far past the opening window. The ring aligns a probe out
        // to whole slots at BOTH ends (RingSource passes the window through untouched); aligning only the start made the
        // range stop at the edge, and the last 47 KiB of the window cost a second range and a second wait.
        long offset = 40L * Slot - 1_000;
        const int window = 48 * 1024;
        uint epoch = body.Epoch + 1;
        source.Retarget(offset, window, epoch);
        var got = new byte[window];
        int filled = 0;
        while (filled < window)
        {
            int n = source.ReadAt(offset + filled, got.AsSpan(filled), epoch);
            Assert.True(n > 0, $"read at {offset + filled} answered {n}");
            filled += n;
        }

        Assert.Equal(file.AsSpan((int)offset, window).ToArray(), got);
        Assert.Equal((39L * Slot, 41L * Slot), cdn.Ranges[before]);    // one range, aligned out at BOTH ends
        Assert.True(body.Ring.Waits <= 1, $"{body.Ring.Waits} waits");  // at most the probe's own; the slot edge cost none

        // The sequential face reads the same bytes from wherever it is put.
        Assert.Equal(1_000L, source.Seek(1_000));
        var sequential = new byte[10_000];
        int s = 0;
        while (s < sequential.Length)
        {
            int n = source.Read(sequential.AsSpan(s));
            Assert.True(n > 0);
            s += n;
        }
        Assert.Equal(file.AsSpan(1_000, 10_000).ToArray(), sequential);

        // Cancel releases the body: a read that would have waited answers −1 at once, never a 0 a codec calls EOF.
        source.Cancel();
        var clock = Stopwatch.StartNew();
        Assert.Equal(-1, source.ReadAt(30L * Slot, new byte[1_024], source.Epoch));
        Assert.True(clock.ElapsedMilliseconds < 2_000, $"a cancelled read waited {clock.ElapsedMilliseconds} ms");
    }

    static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("timed out waiting for " + what);
            Thread.Sleep(1);
        }
    }

    sealed class CountingCdn(byte[] data) : Spotify.Audio.IRangeSource
    {
        readonly Lock _gate = new();
        readonly List<(long Start, long End)> _ranges = [];

        public int Count { get { lock (_gate) return _ranges.Count; } }
        public (long Start, long End)[] Ranges { get { lock (_gate) return [.. _ranges]; } }

        public Spotify.Audio.IRangeReply? Open(string url, long start, long end, CancellationToken ct)
        {
            lock (_gate) _ranges.Add((start, end + 1));
            return start >= data.Length ? null : new Reply(data, start, Math.Min(end + 1, data.Length));
        }

        sealed class Reply(byte[] file, long start, long stop) : Spotify.Audio.IRangeReply
        {
            long _at = start;

            public long TotalLength => file.Length;

            public int Read(Span<byte> dst)
            {
                int n = (int)Math.Min(dst.Length, stop - _at);
                if (n <= 0) return 0;
                file.AsSpan((int)_at, n).CopyTo(dst);
                _at += n;
                return n;
            }

            public void Dispose() { }
        }
    }
}
