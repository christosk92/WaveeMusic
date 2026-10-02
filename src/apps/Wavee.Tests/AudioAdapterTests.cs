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
        Assert.Equal(Playback.Audio.LimiterCeilingLinear / 0.9f, Playback.Audio.NormalizationFactor(enabled: true, 6f, peak: 0.9f));   // 1.995 × 0.9 > the limiter's ceiling (N-1: the cap moved from 1.0)
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

    // ── 6. gap batch B4: a starve waits, the late tail, the late gain, the pooled working set ───────────────────────────

    [Fact]
    public void A_starved_read_is_asked_again_and_the_track_still_plays_bit_exact_to_its_end()
    {
        // D5, G-102: a wait that runs out used to come back from the adapter as the end of the track.
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        var source = new RandomAccessFake(file, linear.LastGranule) { Starves = 3 };
        var decoder = new Playback.Audio.VorbisAudioDecoder(0f, linear.Frames * 1000 / id.SampleRate);
        Assert.True(decoder.TryOpen(source, new MixFormat(id.SampleRate, 2), out _));

        source.Starves = 25;                                             // starve mid-track, repeatedly
        float[] all = Drain(decoder, linear.Frames + 8_192);

        Assert.Equal(0, source.Starves);
        Assert.Equal(linear.Pcm.Length, all.Length);
        Assert.True(MemoryMarshal.AsBytes(all.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(linear.Pcm.AsSpan())));
    }

    [Fact]
    public void A_tail_that_lands_after_the_open_still_gives_the_pump_the_exact_length()
    {
        // G-113: the engine fixes a voice's length when it is built; the pump asks the adapter for the late one instead of
        // joining on the catalogue duration.
        byte[] file = VorbisFixture.Bytes("vbr-q8.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        var source = new RandomAccessFake(file, tail: -1);
        var decoder = new Playback.Audio.VorbisAudioDecoder(0f, linear.Frames * 1000 / id.SampleRate);
        Assert.True(decoder.TryOpen(source, new MixFormat(id.SampleRate, 2), out _));
        Assert.Equal(GaplessInfo.None, decoder.Gapless);
        Assert.Equal(-1L, decoder.LateExactFrames(id.SampleRate));

        // What the engine would have been told had the tail been known at open.
        var reference = new Playback.Audio.VorbisAudioDecoder(0f, linear.Frames * 1000 / id.SampleRate);
        Assert.True(reference.TryOpen(new RandomAccessFake(file, linear.LastGranule), new MixFormat(id.SampleRate, 2), out _));
        Assert.True(reference.Gapless.TailKnown);

        source.Tail = linear.LastGranule;
        Assert.Equal(reference.Gapless.ExactFrames, decoder.LateExactFrames(id.SampleRate));
        Assert.Equal(Clock.ToMix(reference.Gapless.ExactFrames, id.SampleRate, 48_000), decoder.LateExactFrames(48_000));
    }

    /// <summary>The random-access fake plus a normalization figure, as a Spotify body that learned its gain off chunk 0.</summary>
    sealed class NormalizedFake(RandomAccessFake inner, float gainDb) : IMediaByteSource, Playback.Audio.IRandomAccessBytes,
        Playback.Audio.INormalizationSource
    {
        public float GainDb => gainDb;
        public float Peak => 0f;
        public long? Length => inner.Length;
        public SourceCaps Caps => inner.Caps;
        public uint Epoch => inner.Epoch;
        public long TailGranule => inner.TailGranule;
        public bool TryOpen(in DataSpec spec) => inner.TryOpen(spec);
        public int Read(Span<byte> dst) => inner.Read(dst);
        public long Seek(long offset) => inner.Seek(offset);
        public void Cancel() => inner.Cancel();
        public void Close() => inner.Close();
        public int ReadAt(long offset, Span<byte> dst, uint epoch) => inner.ReadAt(offset, dst, epoch);
        public void Retarget(long probeOffset, int probeBytes, uint epoch) => inner.Retarget(probeOffset, probeBytes, epoch);
        public void ResumeFrom(long offset) => inner.ResumeFrom(offset);
    }

    [Fact]
    public void The_source_s_gain_wins_over_the_one_the_adapter_was_built_with()
    {
        // G-107: the adapter is built before a byte lands; a body that opened with no header at hand knows its gain only
        // once chunk 0 is in, which is before the header packets are parsed.
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        var mix = new MixFormat(id.SampleRate, 2);
        long durationMs = linear.Frames * 1000 / id.SampleRate;

        var built = new Playback.Audio.VorbisAudioDecoder(-6f, durationMs);
        Assert.True(built.TryOpen(new RandomAccessFake(file, linear.LastGranule), mix, out _));
        var learned = new Playback.Audio.VorbisAudioDecoder(0f, durationMs);
        Assert.True(learned.TryOpen(new NormalizedFake(new RandomAccessFake(file, linear.LastGranule), -6f), mix, out _));

        float[] a = Drain(built, 20_000), b = Drain(learned, 20_000);
        Assert.Equal(a.Length, b.Length);
        Assert.True(MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan())));
    }

    [Fact]
    public void A_disposed_vorbis_adapter_answers_nothing_and_the_next_one_plays_bit_exact_on_its_working_set()
    {
        // G-134: the working set goes back to the pool at Dispose and the next track decodes on it.
        byte[] file = VorbisFixture.Bytes("sine-440.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        var mix = new MixFormat(id.SampleRate, 2);
        long durationMs = linear.Frames * 1000 / id.SampleRate;

        var first = new Playback.Audio.VorbisAudioDecoder(0f, durationMs);
        Assert.True(first.TryOpen(new RandomAccessFake(file, linear.LastGranule), mix, out _));
        Assert.True(Drain(first, 4_096).Length > 0);
        first.Dispose();
        Assert.Equal(0, first.Read(new float[512]));
        Assert.Equal(-1L, first.Seek(0));

        var second = new Playback.Audio.VorbisAudioDecoder(0f, durationMs);
        Assert.True(second.TryOpen(new RandomAccessFake(file, linear.LastGranule), mix, out _));
        float[] all = Drain(second, linear.Frames + 8_192);
        Assert.True(MemoryMarshal.AsBytes(all.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(linear.Pcm.AsSpan())));
        second.Dispose();
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

    // ── 7. playback smoothness (#167), wave 2: S-1, S-7, P-5, V-PA35 ───────────────────────────────────────────────────
    //
    //   S-1   A seek's LANDING read was interrupted by the next seek of a scrub. Latching `_eof` there ended the track for every seek storm
    //         that raced it; the decoder is not primed, so it cannot keep decoding like the FLAC arm — it latches `_needsLanding` and `Read`
    //         serves silence while it repeats the landing read WITHOUT re-targeting the source (that would end the interrupt window the
    //         next seek just opened).
    //   S-7   A page-sequence hole (or an undecodable packet) re-primes the decoder and the clock owes the span it cost: the next page
    //         granule names it and `Read` pads it as silence, so the track keeps its length and the audio after the hole keeps its place.
    //   P-5   `ID3` names a TAG, not a codec: where the block ends is 10 + the syncsafe size (+10 for a footer).
    //   V-PA35  A decoder reopened on the setup it already holds skips the parse; the pool rents by setup hash.

    [Fact]
    public void An_interrupted_landing_latches_serves_silence_and_lands_without_re_targeting_once_the_bytes_arrive()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        var source = new RandomAccessFake(file, linear.LastGranule);
        var decoder = new Playback.Audio.VorbisAudioDecoder(0f, linear.Frames * 1000 / id.SampleRate);
        Assert.True(decoder.TryOpen(source, new MixFormat(id.SampleRate, 2), out _));
        Assert.Equal(4_096 * 2, Drain(decoder, 4_096).Length);

        long target = linear.Frames / 2;
        source.ResetCounts();
        source.InterruptAfterResume = true;                               // the next seek's interrupt lands while THIS landing is read
        Assert.Equal(target, decoder.Seek(target));                       // never −1, never a throw: the seek completes at its target
        int retargets = source.Retargets;
        Assert.Equal(1, source.Resumes);                                  // the landing's own resume, once
        Assert.True(source.Interrupt);

        // not the end, whatever the engine would latch: silence, for as long as the interrupt stands …
        Assert.Equal(1, SilentBlocksUntilNoEof(decoder, blocks: 3));
        Assert.Equal(retargets, source.Retargets);                        // … and the retries never re-target (that would end the interrupt window)
        Assert.Equal(1, source.Resumes);

        // The bytes arrive: the landing reads its window, primes, places the clock, and the first frame out is the target sample.
        source.InterruptAfterResume = false;
        source.Interrupt = false;
        float[] after = Drain(decoder, 4_096);
        Assert.Equal(4_096 * 2, after.Length);
        Assert.True(MemoryMarshal.AsBytes(after.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(linear.Pcm.AsSpan((int)(target * 2), after.Length))));
        Assert.Equal(retargets, source.Retargets);                        // the landing's retry re-targeted nothing …
        Assert.Equal(1, source.Resumes);                                  // … and resumed nothing: the failed attempt's request stands
    }

    [Fact]
    public void A_seek_that_follows_an_interrupted_landing_supersedes_it_and_lands_sample_exact()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        var source = new RandomAccessFake(file, linear.LastGranule);
        var decoder = new Playback.Audio.VorbisAudioDecoder(0f, linear.Frames * 1000 / id.SampleRate);
        Assert.True(decoder.TryOpen(source, new MixFormat(id.SampleRate, 2), out _));
        Assert.Equal(4_096 * 2, Drain(decoder, 4_096).Length);

        long first = linear.Frames / 3, second = linear.Frames * 3 / 4;
        source.InterruptAfterResume = true;
        Assert.Equal(first, decoder.Seek(first));
        Assert.Equal(1, SilentBlocksUntilNoEof(decoder, blocks: 1));      // latched on `first`

        source.InterruptAfterResume = false;                              // the scrub's NEXT seek arrives — through the front door, as ever
        Assert.Equal(second, decoder.Seek(second));
        Assert.False(source.Interrupt);                                   // its own retarget ended the interrupt
        float[] after = Drain(decoder, 4_096);
        Assert.Equal(4_096 * 2, after.Length);
        Assert.True(MemoryMarshal.AsBytes(after.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(linear.Pcm.AsSpan((int)(second * 2), after.Length))));   // the superseded landing left no trace
    }

    [Fact]
    public void A_landing_that_fails_for_any_reason_but_an_interrupt_ends_the_track_as_it_always_did_and_a_latched_one_that_loses_its_source_ends_too()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        var mix = new MixFormat(id.SampleRate, 2);
        long durationMs = linear.Frames * 1000 / id.SampleRate;
        long target = linear.Frames / 2;

        // a closed body: the landing read answers −1 — no interrupt, nothing owed but the end
        var closedSource = new RandomAccessFake(file, linear.LastGranule);
        var closed = new Playback.Audio.VorbisAudioDecoder(0f, durationMs);
        Assert.True(closed.TryOpen(closedSource, mix, out _));
        Assert.Equal(4_096 * 2, Drain(closed, 4_096).Length);
        closedSource.Closed = true;
        Assert.Equal(target, closed.Seek(target));
        Assert.Equal(0, closed.Read(new float[512]));                     // EOF, never silence: silence is only ever owed to an INTERRUPT

        // an interrupted landing whose source then goes away: the latch ends the track and clears itself
        var lostSource = new RandomAccessFake(file, linear.LastGranule);
        var lost = new Playback.Audio.VorbisAudioDecoder(0f, durationMs);
        Assert.True(lost.TryOpen(lostSource, mix, out _));
        Assert.Equal(4_096 * 2, Drain(lost, 4_096).Length);
        lostSource.InterruptAfterResume = true;
        Assert.Equal(target, lost.Seek(target));
        Assert.Equal(1, SilentBlocksUntilNoEof(lost, blocks: 1));
        lostSource.InterruptAfterResume = false;
        lostSource.Interrupt = false;
        lostSource.Closed = true;
        Assert.Equal(0, lost.Read(new float[512]));
        Assert.Equal(0, lost.Read(new float[512]));                       // and it stays ended: no latch is left to serve silence forever
    }

    // ── S-7: the clock's pad (pure) ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_hole_names_the_span_it_cost_at_the_next_page_pin_and_pads_it_once_ahead_of_the_frames_that_follow()
    {
        var clock = Clock.At(10_000);                                     // 10 000 granules were admitted before the hole
        clock.Hole();                                                     // packets were lost: the decoder re-primed

        // The packet after a re-prime returns no frames; it is the last on its page, whose granule says 14 000 — the lost span plus
        // this packet's own (never decoded) frames. Nothing is handed out, but the gap is now owed.
        Clock.Run primed = clock.Admit(frames: 0, granuleAtEnd: 14_000, eos: false);
        Assert.Equal(0, primed.Count);
        Assert.Equal(14_000, clock.Position);

        Clock.Run next = clock.Admit(frames: 512, granuleAtEnd: -1, eos: false);
        Assert.Equal(512, next.Count);
        Assert.Equal(14_000L, next.Start);
        Assert.Equal(4_000, next.Pad);                                    // silence AHEAD of these frames: 14 000 − 10 000

        Clock.Run after = clock.Admit(frames: 512, granuleAtEnd: -1, eos: false);
        Assert.Equal(0, after.Pad);                                       // paid once
        Assert.Equal(14_512L, after.Start);
    }

    [Fact]
    public void A_hole_pads_nothing_while_a_landing_is_still_dropping_frames_and_nothing_for_a_gap_that_is_a_wrong_pin()
    {
        // After a seek the clock drops frames up to its target: nothing before the target is heard, so a hole there costs nothing audible.
        var landing = Clock.At(10_000);
        landing.Target = 15_000;
        landing.Hole();
        Clock.Run dropped = landing.Admit(frames: 600, granuleAtEnd: 14_800, eos: false);        // pinned: [14 200, 14 800) — all before the target
        Assert.Equal(0, dropped.Count);
        Clock.Run reached = landing.Admit(frames: 600, granuleAtEnd: 15_400, eos: false);       // [14 800, 15 400): 200 dropped, 400 heard
        Assert.Equal(200, reached.Skip);
        Assert.Equal(400, reached.Count);
        Assert.Equal(0, reached.Pad);

        // A gap beyond MaxPad is not "a lost page", it is a wrong pin: the clock re-pins and pads nothing.
        var wrong = Clock.At(10_000);
        wrong.Hole();
        Clock.Run repinned = wrong.Admit(frames: 0, granuleAtEnd: 10_000 + Clock.MaxPad + 1, eos: false);
        Assert.Equal(0, repinned.Count);
        Clock.Run follow = wrong.Admit(frames: 512, granuleAtEnd: -1, eos: false);
        Assert.Equal(0, follow.Pad);
        Assert.Equal(10_000 + Clock.MaxPad + 1, follow.Start);

        // …and the largest believable gap is padded in full
        var biggest = Clock.At(10_000);
        biggest.Hole();
        biggest.Admit(frames: 0, granuleAtEnd: 10_000 + Clock.MaxPad, eos: false);
        Assert.Equal((int)Clock.MaxPad, biggest.Admit(frames: 64, granuleAtEnd: -1, eos: false).Pad);
    }

    /// <summary>The file with the audio page nearest the middle of its granule range removed WHOLE: a page-sequence hole — the pages
    /// either side keep their own sequence numbers, so the reader sees the gap.</summary>
    static byte[] WithoutTheMiddlePage(byte[] file, long firstAudioPage, long lastGranule)
    {
        var pages = new List<(int At, int Length, long Granule)>();
        int at = (int)firstAudioPage;
        while ((at = Ogg.FindPage(file, at, out Ogg.Page page, out _)) >= 0)
        {
            pages.Add((at, page.Length, page.Granule));
            at += page.Length;
        }
        int best = -1;
        long bestDistance = long.MaxValue;
        for (int i = 3; i < pages.Count - 3; i++)                         // never the first or last pages: the open and the end are not the point
        {
            if (pages[i].Granule < 0) continue;
            long distance = Math.Abs(pages[i].Granule - lastGranule / 2);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        Assert.True(best > 0, "the fixture has no page to remove");
        (int removedAt, int removedLength, _) = pages[best];
        var damaged = new byte[file.Length - removedLength];
        file.AsSpan(0, removedAt).CopyTo(damaged);
        file.AsSpan(removedAt + removedLength).CopyTo(damaged.AsSpan(removedAt));
        return damaged;
    }

    [Fact]
    public void A_hole_re_primes_the_decoder_and_the_owed_span_is_padded_so_the_track_keeps_its_length_and_the_audio_after_it_keeps_its_place()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        var (ident, _, firstAudioPage) = VorbisFixture.Headers(file, new Ogg.Reader());
        Assert.True(Vorbis.TryParseIdentification(ident, out Vorbis.Identification id));
        byte[] damaged = WithoutTheMiddlePage(file, firstAudioPage, linear.LastGranule);
        Assert.True(damaged.Length < file.Length);

        var decoder = new Playback.Audio.VorbisAudioDecoder(0f, linear.Frames * 1000 / id.SampleRate);
        Assert.True(decoder.TryOpen(new RandomAccessFake(damaged, linear.LastGranule), new MixFormat(id.SampleRate, 2), out _));
        float[] repaired = Drain(decoder, linear.Frames + 8_192);
        float[] straight = linear.Pcm;

        Assert.Equal(straight.Length, repaired.Length);                   // the lost span came back as silence: the length is the whole track's
        int quarter = straight.Length / 8 * 2;                            // a quarter of the samples, a whole number of frames
        Assert.True(MemoryMarshal.AsBytes(repaired.AsSpan(0, quarter)).SequenceEqual(MemoryMarshal.AsBytes(straight.AsSpan(0, quarter))),
            "the audio BEFORE the hole differs from the straight decode");
        Assert.True(MemoryMarshal.AsBytes(repaired.AsSpan(repaired.Length - quarter)).SequenceEqual(MemoryMarshal.AsBytes(straight.AsSpan(straight.Length - quarter))),
            "the audio AFTER the hole is not where the straight decode puts it");

        // …and the hole itself is exact silence the length of what was lost (pink noise never has two silent frames, let alone a thousand)
        int straightSilence = LongestSilentRun(straight, quarter, straight.Length - quarter);
        int longestSilence = LongestSilentRun(repaired, quarter, repaired.Length - quarter);
        Assert.True(straightSilence < 100, $"the straight decode already has {straightSilence} silent frames in a row: the fixture cannot tell a pad from noise");
        Assert.True(longestSilence >= 1_000, $"the longest run of silent frames in the repaired middle is {longestSilence}");
    }

    /// <summary>The most consecutive all-zero stereo frames in <paramref name="pcm"/>[from, to) (sample indices).</summary>
    static int LongestSilentRun(float[] pcm, int from, int to)
    {
        int best = 0, run = 0;
        for (int i = from; i + 1 < to; i += 2)
        {
            if (pcm[i] == 0f && pcm[i + 1] == 0f) { run++; if (run > best) best = run; }
            else run = 0;
        }
        return best;
    }

    // ── P-5: where an ID3v2 block ends (pure) ───────────────────────────────────────────────────────────────────────

    static byte[] Id3Header(byte versionMajor, byte flags, int size)
    {
        var h = new byte[10];
        "ID3"u8.CopyTo(h);
        h[3] = versionMajor;
        h[4] = 0;
        h[5] = flags;
        h[6] = (byte)((size >> 21) & 0x7F);
        h[7] = (byte)((size >> 14) & 0x7F);
        h[8] = (byte)((size >> 7) & 0x7F);
        h[9] = (byte)(size & 0x7F);
        return h;
    }

    [Fact]
    public void An_id3v2_block_ends_ten_plus_its_syncsafe_size_plus_ten_more_for_a_footer()
    {
        Assert.Equal(10L, Playback.Audio.Id3v2Length(Id3Header(3, 0, 0)));                       // an empty tag is still its header
        Assert.Equal(267L, Playback.Audio.Id3v2Length(Id3Header(3, 0, 257)));                    // 0x00 0x00 0x02 0x01 = (2 << 7) | 1
        Assert.Equal(10L + 2_097_152L, Playback.Audio.Id3v2Length(Id3Header(4, 0, 2_097_152)));  // a 2 MiB cover: the size spans all four bytes
        Assert.Equal(10L + 0x0FFF_FFFFL, Playback.Audio.Id3v2Length(Id3Header(4, 0, 0x0FFF_FFFF)));
        Assert.Equal(10L + 257 + 10, Playback.Audio.Id3v2Length(Id3Header(4, 0x10, 257)));       // the footer flag (byte 5, bit 4): +10
        Assert.Equal(267L, Playback.Audio.Id3v2Length(Id3Header(3, 0xE0, 257)));                 // the other flags (unsync, extended, experimental) add nothing
        Assert.Equal(267L, Playback.Audio.Id3v2Length([.. Id3Header(3, 0, 257), .. new byte[64]]));   // bytes after the header are not the header's business
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void A_head_shorter_than_an_id3v2_header_has_no_tag(int length)
        => Assert.Equal(0L, Playback.Audio.Id3v2Length(Id3Header(3, 0, 257).AsSpan(0, length)));

    [Fact]
    public void A_head_that_is_not_a_well_formed_id3v2_header_has_no_tag()
    {
        byte[] Mutated(int index, byte value) { byte[] h = Id3Header(3, 0, 257); h[index] = value; return h; }

        Assert.Equal(0L, Playback.Audio.Id3v2Length("OggS\0\0\0\0\0\0\0\0"u8));                  // not ID3
        Assert.Equal(0L, Playback.Audio.Id3v2Length("fLaC\0\0\0\0\0\0\0\0"u8));
        Assert.Equal(0L, Playback.Audio.Id3v2Length("ID\0\0\0\0\0\0\0\0\0"u8));                  // a near miss
        Assert.Equal(0L, Playback.Audio.Id3v2Length(Mutated(3, 0xFF)));                          // a 0xFF version
        Assert.Equal(0L, Playback.Audio.Id3v2Length(Mutated(4, 0xFF)));                          // a 0xFF revision
        for (int sizeByte = 6; sizeByte <= 9; sizeByte++)
            Assert.Equal(0L, Playback.Audio.Id3v2Length(Mutated(sizeByte, 0x80)));               // syncsafe: seven bits per byte, the top one is never set
        Assert.Equal(267L, Playback.Audio.Id3v2Length(Id3Header(3, 0, 257)));                    // (and the unmutated header is accepted)
    }

    [Fact]
    public void A_tagged_flac_is_a_flac_once_the_tag_is_skipped_where_the_old_rule_called_every_id3_file_mp3()
    {
        var tag = new byte[10 + 100];
        Id3Header(3, 0, 100).CopyTo(tag, 0);
        byte[] file = [.. tag, .. "fLaC"u8.ToArray(), .. new byte[40]];

        Assert.Equal(Spotify.Audio.Format.Mp3, Playback.Audio.SniffFormat(file));                // the old rule: the magic of the TAG
        long id3 = Playback.Audio.Id3v2Length(file);
        Assert.Equal(110L, id3);
        Assert.Equal(Spotify.Audio.Format.Flac, Playback.Audio.SniffFormat(file.AsSpan((int)id3)));   // P-5: sniff what comes AFTER it
    }

    // ── V-PA35: the setup-hash hit and the decoder pool ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_setup_hash_is_pure_nonzero_and_names_exactly_the_bytes_the_tables_were_built_from()
    {
        byte[] pink = VorbisFixture.Bytes("pink-320.ogg"), sine = VorbisFixture.Bytes("sine-440.ogg");
        var (ident, setup, _) = VorbisFixture.Headers(pink, new Ogg.Reader());
        var (sineIdent, sineSetup, _) = VorbisFixture.Headers(sine, new Ogg.Reader());

        ulong hash = Vorbis.Decoder.HashSetup(ident, setup);
        Assert.NotEqual(0UL, hash);                                                        // 0 means "no tables"
        Assert.Equal(hash, Vorbis.Decoder.HashSetup(ident, setup));
        Assert.NotEqual(hash, Vorbis.Decoder.HashSetup(sineIdent, sineSetup));              // another file's setup
        Assert.NotEqual(hash, Vorbis.Decoder.HashSetup(ident, setup.AsSpan(0, setup.Length - 1)));   // a byte short
        byte[] flipped = (byte[])setup.Clone();
        flipped[^1] ^= 0x01;
        Assert.NotEqual(hash, Vorbis.Decoder.HashSetup(ident, flipped));                    // one bit
        // the lengths are mixed in: moving a byte from one header to the other is a different setup
        Assert.NotEqual(Vorbis.Decoder.HashSetup([1, 2, 3], [4, 5]), Vorbis.Decoder.HashSetup([1, 2], [3, 4, 5]));
    }

    [Fact]
    public void A_decoder_reopened_on_the_setup_it_already_holds_skips_the_parse_allocates_nothing_and_decodes_exactly_like_a_fresh_one()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        var (ident, setup, first) = VorbisFixture.Headers(file, new Ogg.Reader());
        ulong hash = Vorbis.Decoder.HashSetup(ident, setup);

        var held = new Vorbis.Decoder();
        Assert.Equal(0UL, held.SetupHash);                                                  // nothing built yet
        Assert.True(held.Open(ident, setup, 0.5f));
        Assert.Equal(hash, held.SetupHash);
        DecodeFirstPackets(file, first, held, 25);                                          // leave lapping state behind

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(held.Open(ident, setup, 1f));                                           // the same bytes: the hit
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);                                                        // a few microseconds of lapping reset, not the ~1 ms table build
        Assert.Equal(hash, held.SetupHash);
        Assert.Equal(1f, held.Gain);                                                        // the gain is the new one …
        Assert.Equal(0, held.PreviousBlockSize);                                            // … and the overlap state is reset: the next packet primes

        var fresh = new Vorbis.Decoder();
        Assert.True(fresh.Open(ident, setup, 1f));
        float[] a = DecodeFirstPackets(file, first, held, 40), b = DecodeFirstPackets(file, first, fresh, 40);
        Assert.True(a.Length > 8_000, $"only {a.Length} samples out of 40 packets: the comparison proves nothing");
        Assert.True(MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan())),
            "a decoder that skipped the parse decodes differently from one that built its tables");
    }

    [Fact]
    public void A_different_setup_rebuilds_the_tables_and_a_failed_open_leaves_no_hash_to_hit()
    {
        byte[] pink = VorbisFixture.Bytes("pink-320.ogg"), sine = VorbisFixture.Bytes("sine-440.ogg");
        var (ident, setup, first) = VorbisFixture.Headers(pink, new Ogg.Reader());
        var (sineIdent, sineSetup, _) = VorbisFixture.Headers(sine, new Ogg.Reader());
        ulong pinkHash = Vorbis.Decoder.HashSetup(ident, setup), sineHash = Vorbis.Decoder.HashSetup(sineIdent, sineSetup);

        var dec = new Vorbis.Decoder();
        Assert.True(dec.Open(ident, setup));
        Assert.Equal(pinkHash, dec.SetupHash);
        Assert.True(dec.Open(sineIdent, sineSetup));
        Assert.Equal(sineHash, dec.SetupHash);                                              // re-parsed in place: the tables are the sine file's now

        Assert.False(dec.Open(ident, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));              // not a setup header
        Assert.Equal(0UL, dec.SetupHash);                                                   // a hash can never name half-built tables
        Assert.False(dec.IsOpen);

        Assert.True(dec.Open(ident, setup, 1f));                                            // and the good setup is parsed again, from scratch
        Assert.Equal(pinkHash, dec.SetupHash);
        var reference = new Vorbis.Decoder();
        Assert.True(reference.Open(ident, setup, 1f));
        Assert.True(MemoryMarshal.AsBytes(DecodeFirstPackets(pink, first, dec, 30).AsSpan())
            .SequenceEqual(MemoryMarshal.AsBytes(DecodeFirstPackets(pink, first, reference, 30).AsSpan())));
    }

    /// <summary>The first <paramref name="packets"/> audio packets of <paramref name="file"/> through <paramref name="decoder"/>, as floats.</summary>
    static float[] DecodeFirstPackets(byte[] file, long firstAudioPage, Vorbis.Decoder decoder, int packets)
    {
        var reader = new Ogg.Reader();
        Clock.Restart(reader, firstAudioPage);
        ReadOnlySpan<byte> audio = file.AsSpan((int)firstAudioPage);
        var all = new List<float>();
        for (int p = 0; p < packets; p++)
        {
            Assert.Equal(Ogg.Reader.Next.Packet, reader.NextPacket(audio, out ReadOnlySpan<byte> packet, out _));
            Assert.Equal(Vorbis.PacketResult.Ok, decoder.DecodePacket(packet));
            foreach (float v in decoder.OutputSpan) all.Add(v);
        }
        return [.. all];
    }

    /// <summary>Open the file on an adapter and play a little. The adapter holds a CORE decoder (rented by the setup's hash); disposing it
    /// returns the decoder to the pool still holding that file's tables.</summary>
    static Playback.Audio.VorbisAudioDecoder OpenAndPlay(byte[] file)
    {
        VorbisFixture.Decoded linear = VorbisFixture.DecodeAll(file);
        Assert.True(Vorbis.TryParseIdentification(VorbisFixture.Headers(file, new Ogg.Reader()).Ident, out Vorbis.Identification id));
        var adapter = new Playback.Audio.VorbisAudioDecoder(0f, linear.Frames * 1000 / id.SampleRate);
        Assert.True(adapter.TryOpen(new RandomAccessFake(file, linear.LastGranule), new MixFormat(id.SampleRate, 2), out _));
        Assert.True(Drain(adapter, 2_048).Length > 0);
        return adapter;
    }

    static ulong SetupHashOf(byte[] file)
    {
        var (ident, setup, _) = VorbisFixture.Headers(file, new Ogg.Reader());
        return Vorbis.Decoder.HashSetup(ident, setup);
    }

    // The pool is PROCESS-WIDE and other test classes open Vorbis adapters beside this one, so each claim below is retried until the pool
    // is as this test just left it — a concurrent claim can take a decoder this test returned, but not every time.

    [Fact]
    public void The_pool_rents_the_decoder_whose_tables_match_the_setup_asked_for_not_merely_the_first_one_in_it()
    {
        byte[] pink = VorbisFixture.Bytes("pink-320.ogg"), sine = VorbisFixture.Bytes("sine-440.ogg");
        ulong pinkHash = SetupHashOf(pink), sineHash = SetupHashOf(sine);
        Assert.NotEqual(pinkHash, sineHash);

        bool matched = false;
        for (int attempt = 0; attempt < 25 && !matched; attempt++)
        {
            // TWO adapters alive at once hold two different decoders (one file's tables each). Returned pink first, the pool holds the
            // pink decoder in an EARLIER slot than the sine one: "the first pooled" is wrong for the sine rent, "the match" is right.
            Playback.Audio.VorbisAudioDecoder a = OpenAndPlay(pink), b = OpenAndPlay(sine);
            a.Dispose();
            b.Dispose();
            Vorbis.Decoder rented = Playback.Audio.VorbisDecoderPool.Rent(sineHash);
            matched = rented.SetupHash == sineHash && rented.IsOpen;
        }
        Assert.True(matched, "Rent(hash) never answered the decoder holding that hash's tables");

        matched = false;
        for (int attempt = 0; attempt < 25 && !matched; attempt++)
        {
            OpenAndPlay(pink).Dispose();                                  // (the pink decoder is back in the pool)
            Vorbis.Decoder rented = Playback.Audio.VorbisDecoderPool.Rent(pinkHash);
            matched = rented.SetupHash == pinkHash && rented.IsOpen;      // the tables are built: opening it on this setup skips the parse
        }
        Assert.True(matched);
    }

    [Fact]
    public void A_decoder_rented_back_by_hash_after_its_adapter_was_disposed_opens_on_its_tables_with_no_parse_and_is_pooled_once()
    {
        byte[] pink = VorbisFixture.Bytes("pink-320.ogg");
        ulong pinkHash = SetupHashOf(pink);
        var (ident, setup, _) = VorbisFixture.Headers(pink, new Ogg.Reader());

        bool reused = false;
        for (int attempt = 0; attempt < 25 && !reused; attempt++)
        {
            Playback.Audio.VorbisAudioDecoder first = OpenAndPlay(pink);
            first.Dispose();
            first.Dispose();                                              // idempotent: the decoder goes back to the pool ONCE
            Vorbis.Decoder rented = Playback.Audio.VorbisDecoderPool.Rent(pinkHash);
            Vorbis.Decoder another = Playback.Audio.VorbisDecoderPool.Rent(pinkHash);
            Assert.NotSame(rented, another);                              // a decoder pooled twice would be rented to two voices at once
            if (rented.SetupHash != pinkHash) continue;                   // (a concurrent test took ours: try again)

            reused = true;
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(rented.Open(ident, setup, 1f));                   // its own setup: a hit — no parse, no allocation
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.True(reused);
    }

    [Fact]
    public void The_pool_hands_any_pooled_decoder_when_none_matches_and_a_new_one_when_it_is_empty()
    {
        byte[] pink = VorbisFixture.Bytes("pink-320.ogg");
        const ulong nobodys = 0x1234_5678_9ABC_DEF0UL;                    // no file hashes to this

        bool pooled = false;
        for (int attempt = 0; attempt < 25 && !pooled; attempt++)
        {
            OpenAndPlay(pink).Dispose();
            Vorbis.Decoder rented = Playback.Audio.VorbisDecoderPool.Rent(nobodys);
            pooled = rented.IsOpen && rented.SetupHash != nobodys;        // a pooled decoder (it re-parses in place), not a new one
        }
        Assert.True(pooled, "Rent(unknown hash) did not fall back to a pooled decoder");

        bool sawNew = false;
        for (int i = 0; i < 3 * Playback.Audio.VorbisDecoderPool.Capacity + 4 && !sawNew; i++)
        {
            Vorbis.Decoder rented = Playback.Audio.VorbisDecoderPool.Rent(nobodys);
            sawNew = !rented.IsOpen && rented.SetupHash == 0UL;           // the pool ran dry: a fresh decoder with no tables
        }
        Assert.True(sawNew, "an emptied pool never produced a new decoder");
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
        /// <summary>A starving link: this many reads answer <c>StarvedRead</c> before bytes flow again.</summary>
        public int Starves;
        /// <summary>S-1: the NEXT seek's interrupt arrives while this seek's LANDING window is being read. The adapter's own
        /// <c>Retarget</c> ends any interrupt, so the one that lands after it is raised by its <c>ResumeFrom</c> — which the landing
        /// (and only the landing) issues before it reads a byte.</summary>
        public bool InterruptAfterResume;
        /// <summary>The source is gone (a closed body): every read answers −1.</summary>
        public bool Closed;
        /// <summary>The tail granule — settable, because the stream layer learns it after the decoder opened.</summary>
        public long Tail { get; set; } = tail;
        public uint Epoch { get; private set; }
        public long TailGranule => Tail;
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
            if (Closed) return -1;
            if (Interrupt) return Playback.Audio.InterruptedRead;
            if (Starves > 0) { Starves--; return Playback.Audio.StarvedRead; }
            if (offset >= data.Length || dst.Length == 0) return 0;
            int toSlotEdge = (int)(Slot - offset % Slot);
            int n = (int)Math.Min(Math.Min(dst.Length, toSlotEdge), data.Length - offset);
            data.AsSpan((int)offset, n).CopyTo(dst);
            return n;
        }

        public void Retarget(long probeOffset, int probeBytes, uint epoch) { Retargets++; Epoch = epoch; Interrupt = false; }

        public void ResumeFrom(long offset)
        {
            Resumes++;
            if (InterruptAfterResume) Interrupt = true;
        }
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

        public ValueTask<Spotify.Audio.IRangeReply?> OpenAsync(string url, long start, long end, CancellationToken ct)
        {
            lock (_gate) _ranges.Add((start, end + 1));
            return new ValueTask<Spotify.Audio.IRangeReply?>(
                start >= data.Length ? null : new Reply(data, start, Math.Min(end + 1, data.Length)));
        }

        sealed class Reply(byte[] file, long start, long stop) : Spotify.Audio.IRangeReply
        {
            long _at = start;
            readonly long _from = start;

            public long TotalLength => file.Length;

            public long Start => _from;

            public ValueTask<int> ReadAsync(Memory<byte> dst, CancellationToken ct)
            {
                int n = (int)Math.Min(dst.Length, stop - _at);
                if (n <= 0) return new ValueTask<int>(0);
                file.AsSpan((int)_at, n).CopyTo(dst.Span);
                _at += n;
                return new ValueTask<int>(n);
            }

            public void Dispose() { }
        }
    }
}
