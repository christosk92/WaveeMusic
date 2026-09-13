// ── Wavee.Tests/PlaybackAudioTests.cs — the pump's decisions, without a device ─────────────────────────────────────
//
// Wave 3's gate for `Playback/Playback.Audio.cs` and its named partial `Playback.Audio.Sources.cs`. No audio device,
// no network, no window: every fact below is a decision the pump makes BEFORE it touches hardware, extracted into a
// pure function precisely so it can be pinned here instead of by listening.
//
// Why these and not others. The pump's remaining surface is a WASAPI session and a CDN socket, and a test that fakes
// either tests the fake. What is worth pinning is the set of rules that were each, at some point, a shipped bug:
//
//   THE HAND-OFF SELECTOR, because routing a 0 ms "crossfade" through the crossfade path folds `GainEnvelope.Fade`
//   to `Constant` — two voices at unity for the whole tail, which is not a butt-join and is audible as a doubled
//   outro.
//
//   THE EPOCH GATE, because ten `Next` clicks open ten streams and nine of them must throw their work away rather
//   than publish it. "Stale" accidentally meaning "equal" is one character and silences the last click.
//
//   THE SKIP AND THE SNIFF, because a Spotify FLAC has no container header and every other format has 167 bytes of
//   one. Getting it backwards hands the decoder a stream that starts inside STREAMINFO and fails on every open.
//
//   THE ICY PARSE, because SHOUTcast answers with a status line the BCL refuses, its metadata blocks straddle reads,
//   and a title containing an apostrophe is not a terminator.
//
//   THE SILENT SINK, because `--fake`'s whole value is that ten surfaces light up with no network — and that only
//   works if the silent voice runs out at exactly the declared duration.

using FluentGpu.Media;

using Wavee;

using Xunit;

namespace Wavee.Tests;

public class PlaybackDecoderFactoryTests
{
    static Playback.Audio.Opened Of(Spotify.Audio.Format format)
        => new(format, DurationMs: 180_000, GainDb: 0f, Label: "", BitrateKbps: 0, IsLive: false);

    [Theory]
    [InlineData(Spotify.Audio.Format.Flac)]
    [InlineData(Spotify.Audio.Format.Flac24)]
    public void Both_flac_rungs_get_the_flac_adapter(Spotify.Audio.Format format)
        => Assert.IsType<Playback.Audio.FlacAudioDecoder>(Playback.Audio.CreateDecoderFor(Of(format)));

    [Fact]
    public void Mp3_gets_the_mp3_decoder()
        => Assert.IsType<Playback.Audio.Mp3AudioDecoder>(
            Playback.Audio.CreateDecoderFor(Of(Spotify.Audio.Format.Mp3)));

    [Theory]
    [InlineData(Spotify.Audio.Format.OggVorbis96)]
    [InlineData(Spotify.Audio.Format.OggVorbis160)]
    [InlineData(Spotify.Audio.Format.OggVorbis320)]
    [InlineData(Spotify.Audio.Format.Unknown)]
    public void Every_other_rung_falls_to_vorbis(Spotify.Audio.Format format)
        => Assert.IsType<Playback.Audio.VorbisAudioDecoder>(Playback.Audio.CreateDecoderFor(Of(format)));

    [Fact]
    public void A_fresh_flac_adapter_reports_no_gapless_trim_until_it_has_opened()
    {
        // FLAC has no encoder delay and no padding, so 0/0 is the truth — but the EXACT frame count is STREAMINFO's
        // and is not known before TryOpen. Reporting a length here would let the scheduler join at a guess.
        var decoder = new Playback.Audio.FlacAudioDecoder(0f);
        Assert.Equal(GaplessInfo.None, decoder.Gapless);
    }
}

public class PlaybackHandOffTests
{
    const long Dur = 200_000;

    [Fact]
    public void A_zero_fade_boundary_is_a_gapless_join_inside_the_last_one_and_a_half_seconds()
    {
        Assert.Equal(Playback.Audio.HandOff.None,
            Playback.Audio.HandOffAt(Dur - 1_600, Dur, fadeMs: 0, prepared: true, overlapAllowed: true, handOffInFlight: false));
        Assert.Equal(Playback.Audio.HandOff.Gapless,
            Playback.Audio.HandOffAt(Dur - 1_400, Dur, fadeMs: 0, prepared: true, overlapAllowed: true, handOffInFlight: false));
    }

    [Fact]
    public void A_real_fade_boundary_is_a_crossfade_and_it_opens_exactly_one_fade_before_the_end()
    {
        Assert.Equal(Playback.Audio.HandOff.None,
            Playback.Audio.HandOffAt(Dur - 5_001, Dur, fadeMs: 5_000, prepared: true, overlapAllowed: true, handOffInFlight: false));
        Assert.Equal(Playback.Audio.HandOff.Crossfade,
            Playback.Audio.HandOffAt(Dur - 5_000, Dur, fadeMs: 5_000, prepared: true, overlapAllowed: true, handOffInFlight: false));
    }

    [Fact]
    public void A_zero_fade_never_produces_a_crossfade()
    {
        // Rule 3: `GainEnvelope.Fade(..., 0 frames)` folds to Constant, which is two voices at unity for the whole
        // tail. The selector must answer Gapless, never Crossfade, however late the position is.
        for (long pos = Dur - 3_000; pos <= Dur; pos += 250)
        {
            Playback.Audio.HandOff at = Playback.Audio.HandOffAt(pos, Dur, fadeMs: 0, prepared: true,
                overlapAllowed: true, handOffInFlight: false);
            Assert.NotEqual(Playback.Audio.HandOff.Crossfade, at);
        }
    }

    [Fact]
    public void Nothing_prepared_or_no_overlap_or_already_in_flight_is_never_a_hand_off()
    {
        Assert.Equal(Playback.Audio.HandOff.None,
            Playback.Audio.HandOffAt(Dur, Dur, 0, prepared: false, overlapAllowed: true, handOffInFlight: false));
        Assert.Equal(Playback.Audio.HandOff.None,
            Playback.Audio.HandOffAt(Dur, Dur, 0, prepared: true, overlapAllowed: false, handOffInFlight: false));
        Assert.Equal(Playback.Audio.HandOff.None,
            Playback.Audio.HandOffAt(Dur, Dur, 0, prepared: true, overlapAllowed: true, handOffInFlight: true));
    }

    [Fact]
    public void An_unknown_duration_is_never_a_hand_off()
        // A live stream has no end to approach; arming one would schedule a join at a frame that never arrives.
        => Assert.Equal(Playback.Audio.HandOff.None,
            Playback.Audio.HandOffAt(600_000, durationMs: 0, fadeMs: 0, prepared: true, overlapAllowed: true,
                handOffInFlight: false));

    [Fact]
    public void The_arm_snapshot_is_taken_at_least_two_seconds_out_even_with_no_fade()
    {
        Assert.Equal(2_000, Playback.Audio.ArmLeadMs(0));
        Assert.Equal(2_000, Playback.Audio.ArmLeadMs(1_500));
        Assert.Equal(6_000, Playback.Audio.ArmLeadMs(6_000));
    }

    [Fact]
    public void A_track_shorter_than_the_ending_soon_margin_is_its_own_window()
    {
        // A 6 s interlude must ask for the next track on its FIRST tick or never — the margin is longer than it is.
        Assert.Equal(6_000L, Playback.Audio.EndingSoonMs(fadeMs: 0, durMs: 6_000));
        Assert.Equal(8_000L, Playback.Audio.EndingSoonMs(fadeMs: 0, durMs: 200_000));
        Assert.Equal(13_000L, Playback.Audio.EndingSoonMs(fadeMs: 5_000, durMs: 200_000));
    }

    [Theory]
    [InlineData(true, 5_000, 5_000)]
    [InlineData(false, 5_000, 0)]      // disabled ⇒ gapless, whatever the stored number says
    [InlineData(true, 0, 0)]           // zero ⇒ gapless
    [InlineData(true, 30_000, Playback.Audio.MaxCrossfadeMs)]   // an older build's 30 s becomes the ceiling
    [InlineData(true, -1, 0)]
    public void The_crossfade_is_clamped_once_and_in_one_place(bool enabled, int requested, int expected)
        => Assert.Equal(expected, Playback.Audio.EffectiveFadeMsFor(enabled, requested));
}

public class PlaybackEpochGateTests
{
    [Fact]
    public void Work_started_for_the_current_epoch_is_still_ours()
    {
        var gate = default(Playback.Audio.EpochGate);
        long mine = gate.Bump();
        Assert.False(gate.IsStale(mine));
    }

    [Fact]
    public void Ten_clicks_leave_exactly_one_live_epoch_and_nine_stale_ones()
    {
        var gate = default(Playback.Audio.EpochGate);
        Span<long> started = stackalloc long[10];
        for (int i = 0; i < started.Length; i++) started[i] = gate.Bump();

        int live = 0;
        for (int i = 0; i < started.Length; i++) if (!gate.IsStale(started[i])) live++;
        Assert.Equal(1, live);
        Assert.False(gate.IsStale(started[^1]));
        Assert.True(gate.IsStale(started[0]));
    }

    [Fact]
    public void The_gate_starts_before_the_first_epoch_so_a_default_is_never_current()
        // `default(EpochGate).Current == 0` and the first Bump answers 1: a caller that never started work cannot
        // accidentally look live.
        => Assert.True(default(Playback.Audio.EpochGate).IsStale(1));
}

public class PlaybackVolumeTaperTests
{
    [Fact]
    public void The_ends_are_exact()
    {
        Assert.Equal(0f, Playback.Audio.VolumeTaper.Amplitude(0f));
        Assert.Equal(1f, Playback.Audio.VolumeTaper.Amplitude(1f));
    }

    [Fact]
    public void The_middle_is_cubic_not_linear()
        // A half-way slider is ~12 % amplitude, which is roughly half the perceived loudness. A linear taper here is
        // why every naive volume control does nothing for the top half of its travel.
        => Assert.Equal(0.125f, Playback.Audio.VolumeTaper.Amplitude(0.5f), 4);

    [Fact]
    public void It_is_monotone_and_clamped()
    {
        float previous = -1f;
        for (int i = -2; i <= 12; i++)
        {
            float v = Playback.Audio.VolumeTaper.Amplitude(i / 10f);
            Assert.InRange(v, 0f, 1f);
            Assert.True(v >= previous);
            previous = v;
        }
    }
}

public class PlaybackAudioSniffTests
{
    [Fact]
    public void A_spotify_flac_body_has_no_container_header()
        // FLAC plan §1.1 item 1: the bytes are a plain `fLaC` stream. Passing the 167-byte skip would hand the
        // decoder a stream that starts inside STREAMINFO and fail every lossless open.
        => Assert.Equal(0, Playback.Audio.SkipFor(Spotify.Audio.Format.Flac24, ReadOnlySpan<byte>.Empty));

    [Fact]
    public void An_ogg_body_whose_magic_is_not_at_zero_carries_the_spotify_header()
        => Assert.Equal(Spotify.Audio.Ctr.HeaderBytes,
            Playback.Audio.SkipFor(Spotify.Audio.Format.OggVorbis320, ReadOnlySpan<byte>.Empty));

    [Fact]
    public void A_body_whose_magic_is_already_at_zero_is_not_skipped()
        => Assert.Equal(0, Playback.Audio.SkipFor(Spotify.Audio.Format.OggVorbis320, "OggS"u8));

    [Theory]
    [InlineData("OggS", Spotify.Audio.Format.OggVorbis320)]
    [InlineData("fLaC", Spotify.Audio.Format.Flac)]
    [InlineData("ID3", Spotify.Audio.Format.Mp3)]
    public void The_magic_decides_a_local_file_s_format_not_its_extension(string magic, Spotify.Audio.Format expected)
    {
        Span<byte> head = stackalloc byte[16];
        for (int i = 0; i < magic.Length; i++) head[i] = (byte)magic[i];
        Assert.Equal(expected, Playback.Audio.SniffFormat(head));
    }

    [Fact]
    public void An_mpeg_frame_sync_is_mp3_and_an_adts_sync_is_not()
    {
        // Both start with the same 11-bit sync; only the LAYER field separates them, and layer 00 is AAC.
        Assert.Equal(Spotify.Audio.Format.Mp3, Playback.Audio.SniffFormat([0xFF, 0xFB, 0x90, 0x00]));
        Assert.Equal(Spotify.Audio.Format.Unknown, Playback.Audio.SniffFormat([0xFF, 0xF1, 0x50, 0x80]));
    }

    [Fact]
    public void An_mp4_content_type_is_refused_before_the_aac_test_runs()
        // `audio/mp4` is a CONTAINER this pipeline does not open; routing it to a raw ADTS decoder produces noise
        // rather than an honest refusal.
        => Assert.Null(Playback.Audio.SniffContentType("audio/mp4; codecs=\"mp4a.40.2\""));

    [Theory]
    [InlineData("audio/mpeg", Spotify.Audio.Format.Mp3)]
    [InlineData("application/ogg", Spotify.Audio.Format.OggVorbis320)]
    [InlineData("audio/flac", Spotify.Audio.Format.Flac)]
    public void A_named_container_wins_over_the_bytes(string contentType, Spotify.Audio.Format expected)
        => Assert.Equal(expected, Playback.Audio.SniffContentType(contentType));

    [Fact]
    public void Only_the_fixed_rate_rungs_carry_a_bitrate_hint()
    {
        Assert.Equal(320, Playback.Audio.BitrateHintKbps(Spotify.Audio.Format.OggVorbis320));
        Assert.Equal(1_800, Playback.Audio.BitrateHintKbps(Spotify.Audio.Format.Flac24));
        // MP3 is per-file CBR/VBR: 0 means "measure instead", not "no bandwidth".
        Assert.Equal(0, Playback.Audio.BitrateHintKbps(Spotify.Audio.Format.Mp3));
    }

    [Fact]
    public void The_badge_names_the_source_format_never_the_output()
    {
        Assert.Equal("FLAC 24/44.1", Playback.Audio.LabelFor(Spotify.Audio.Format.Flac24, 24, 44_100));
        Assert.Equal("FLAC 16/48", Playback.Audio.LabelFor(Spotify.Audio.Format.Flac, 16, 48_000));
        Assert.Equal("FLAC 24-bit", Playback.Audio.LabelFor(Spotify.Audio.Format.Flac24, 0, 0));
        Assert.Equal("OGG 320", Playback.Audio.LabelFor(Spotify.Audio.Format.OggVorbis320, 0, 0));
        Assert.Equal("", Playback.Audio.LabelFor(Spotify.Audio.Format.Unknown, 0, 0));
    }
}

public class PlaybackByteSourceTests
{
    /// <summary>A stream that answers 0 for a while and then produces bytes — a CDN chunk that is not resident yet.
    /// The point of the shim is that a codec above it never sees that zero.</summary>
    sealed class SlowStream : Stream
    {
        readonly byte[] _body;
        int _misses;
        int _position;

        internal SlowStream(byte[] body, int misses) { _body = body; _misses = misses; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _body.Length;
        public override long Position { get => _position; set => _position = (int)value; }

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _body.Length) return 0;                       // a TRUE end of stream
            if (_misses > 0) { _misses--; return 0; }                      // a transient miss
            int n = Math.Min(buffer.Length, _body.Length - _position);
            _body.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) { _position = (int)offset; return _position; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void A_transient_miss_is_a_wait_and_never_a_zero_read()
    {
        // Every codec above this seam latches the first zero as PERMANENT eof, so a slow-but-alive connection would
        // silently truncate the track.
        var src = new Playback.Audio.Prefetching(new SlowStream([1, 2, 3, 4], misses: 3), seekable: true);
        Assert.True(src.TryOpen(new DataSpec { Position = 0, Length = -1 }));
        Span<byte> buf = stackalloc byte[4];
        Assert.Equal(4, src.Read(buf));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf.ToArray());
    }

    [Fact]
    public void A_true_end_of_stream_is_still_zero()
    {
        var src = new Playback.Audio.Prefetching(new SlowStream([1, 2], misses: 0), seekable: true);
        Assert.True(src.TryOpen(new DataSpec { Position = 0, Length = -1 }));
        Span<byte> buf = stackalloc byte[4];
        Assert.Equal(2, src.Read(buf));
        Assert.Equal(0, src.Read(buf));
    }

    [Fact]
    public void The_container_skip_is_invisible_above_the_seam()
    {
        // A Spotify Ogg body starts 167 bytes in; the decoder must see its own byte 0 at logical 0.
        var body = new byte[8];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)i;
        var src = new Playback.Audio.Prefetching(new SlowStream(body, misses: 0), seekable: true);
        src.SetSkip(4);
        Assert.True(src.TryOpen(new DataSpec { Position = 0, Length = -1 }));
        Assert.Equal(4L, src.Length);
        Span<byte> buf = stackalloc byte[4];
        Assert.Equal(4, src.Read(buf));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, buf.ToArray());
    }
}

public class PlaybackIcyTests
{
    static byte[] Ascii(string s)
    {
        var bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
        return bytes;
    }

    [Fact]
    public void A_shoutcast_status_line_parses_even_though_the_bcl_refuses_it()
    {
        byte[] head = Ascii("ICY 200 OK\r\nicy-name:Radio Paradise\r\nicy-br:128\r\nicy-metaint:16000\r\n"
            + "content-type:audio/mpeg\r\n\r\nAUDIO");
        Assert.True(Playback.Audio.TryParseIcyHead(head, out Playback.Audio.IcyHead parsed, out int consumed));
        Assert.Equal(200, parsed.Status);
        Assert.Equal("Radio Paradise", parsed.Name);
        Assert.Equal(128, parsed.BitrateKbps);
        Assert.Equal(16_000, parsed.MetaInt);
        Assert.Equal("audio/mpeg", parsed.ContentType);
        // `consumed` is where the BODY starts: those bytes are already read and must never be fetched twice.
        Assert.Equal("AUDIO", System.Text.Encoding.Latin1.GetString(head.AsSpan(consumed)));
    }

    [Fact]
    public void A_bare_lf_head_parses_too()
    {
        byte[] head = Ascii("ICY 200 OK\nicy-metaint:8192\n\nX");
        Assert.True(Playback.Audio.TryParseIcyHead(head, out Playback.Audio.IcyHead parsed, out int consumed));
        Assert.Equal(8_192, parsed.MetaInt);
        Assert.Equal(head.Length - 1, consumed);
    }

    [Fact]
    public void An_ordinary_http_redirect_carries_its_location()
    {
        byte[] head = Ascii("HTTP/1.0 302 Found\r\nLocation: http://stream.example/live\r\n\r\n");
        Assert.True(Playback.Audio.TryParseIcyHead(head, out Playback.Audio.IcyHead parsed, out _));
        Assert.Equal(302, parsed.Status);
        Assert.Equal("http://stream.example/live", parsed.Location);
    }

    [Fact]
    public void A_head_that_has_not_finished_arriving_is_not_a_parse_failure()
        => Assert.False(Playback.Audio.TryParseIcyHead(Ascii("ICY 200 OK\r\nicy-name:Half"), out _, out _));

    [Fact]
    public void A_title_containing_an_apostrophe_is_not_terminated_by_it()
    {
        // The naive "first `';`" rule truncates every song with a possessive or a contraction in its name.
        Assert.Equal("Don't Stop Me Now",
            Playback.Audio.IcyStreamTitle("StreamTitle='Don't Stop Me Now';StreamUrl='http://x';"));
    }

    [Fact]
    public void A_following_key_value_pair_does_terminate_it()
        => Assert.Equal("Queen - Bohemian Rhapsody",
            Playback.Audio.IcyStreamTitle("StreamTitle='Queen - Bohemian Rhapsody';StreamUrl='';"));

    [Fact]
    public void A_truncated_block_still_yields_what_arrived()
        => Assert.Equal("Half A Title", Playback.Audio.IcyStreamTitle("StreamTitle='Half A Title"));

    [Fact]
    public void A_block_with_no_title_field_is_null()
        => Assert.Null(Playback.Audio.IcyStreamTitle("StreamUrl='http://example';"));

    [Fact]
    public void The_title_splits_on_the_first_separator_only()
    {
        (string title, string? artist) = Playback.Audio.SplitIcyTitle("Nine Inch Nails - Something I Can Never Have");
        Assert.Equal("Nine Inch Nails", artist);
        Assert.Equal("Something I Can Never Have", title);
    }

    [Fact]
    public void A_title_with_no_separator_keeps_the_whole_string_and_no_artist()
    {
        (string title, string? artist) = Playback.Audio.SplitIcyTitle("Station identification");
        Assert.Equal("Station identification", title);
        Assert.Null(artist);
    }
}

public class PlaybackSilentSinkTests
{
    // ch 31 GAP 6: `--fake`'s whole value is that a seeded context lights ten surfaces with no network. That only
    // works if the silent voice runs out at exactly the declared duration, so `Ended` lands where the bar says it
    // should — and if it is genuinely silent, because the level meters read the same tap a real track does.
    [Theory]
    [InlineData(1_000)]
    [InlineData(180_000)]
    [InlineData(37)]
    public void The_silent_voice_runs_for_exactly_the_declared_duration(long durationMs)
    {
        var format = new MixFormat(48_000, 2);
        IAudioSource voice = Playback.Audio.SilentVoice(format, durationMs);
        long expected = Math.Max(1, durationMs * format.SampleRate / 1000);

        var buffer = new float[1024 * format.Channels];
        long frames = 0;
        int guard = 0;
        while (!voice.Exhausted && guard++ < 100_000)
        {
            int n = voice.Read(buffer, format.Channels);
            if (n <= 0) break;
            for (int i = 0; i < n * format.Channels; i++) Assert.Equal(0f, buffer[i]);
            frames += n;
        }
        Assert.Equal(expected, frames);
        Assert.True(voice.Exhausted);
    }

    [Fact]
    public void The_silent_sink_is_the_engine_s_own_headless_endpoint()
    {
        // Deliberately NOT a Wavee type: the graph, the mixer, the DSP chain and the level tap must all run exactly
        // as they do on a real device, so what `--fake` exercises is the shipping path and not a second one.
        var format = new MixFormat(48_000, 2);
        IAudioEndpoint endpoint = Playback.Audio.SilentSink(format);
        Assert.True(endpoint.IsReady);
        Assert.Equal(format, endpoint.Sink.Format);
        Assert.Equal(format.SampleRate, endpoint.Clock.MixRate);
        endpoint.Dispose();
    }

    [Fact]
    public void The_silent_session_is_connected_so_its_own_feeder_plays_it_to_the_end()
    {
        // The engine starts a session's feeder only in `ConnectSignals`, and `Advance` does nothing without a sink: an
        // unconnected silent session stayed `Idle` forever and `--fake` sat in `Loading`. Nothing here pumps — the
        // session's own feeder thread must carry it from Opening to Ended. (The null sink is not paced, so 200 ms of
        // silence renders in a few feeder passes; wall-clock pacing is an open question, headless plan §8 Q4.)
        var format = new MixFormat(48_000, 2);
        PcmAudioSession session = Playback.Audio.OpenSilentSession(format, voiceMs: 200, effects: null, volume: 1f);
        try
        {
            Assert.NotEqual(PlaybackState.Idle, session.CurrentState);
            _ = session.PlayAsync();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (session.CurrentState != PlaybackState.Ended && clock.ElapsedMilliseconds < 10_000) Thread.Sleep(2);
            Assert.Equal(PlaybackState.Ended, session.CurrentState);
        }
        finally { session.DisposeAsync().AsTask().Wait(5_000); }
    }

    [Theory]
    [InlineData(180_000, 0, 180_000, 0)]
    [InlineData(180_000, 60_000, 120_000, 60_000)]      // a resume at 1:00 plays the last two minutes, reporting from 1:00
    [InlineData(180_000, 999_999, 1, 180_000)]          // past the end: one millisecond of voice, never zero, so Ended arrives
    [InlineData(180_000, -5, 180_000, 0)]
    public void A_silent_load_at_a_position_is_not_a_seek(long durationMs, long fromMs, long voiceMs, long offsetMs)
    {
        // The silent voice is a signal generator, which the engine's SeekAsync refuses; the load sizes the voice to what
        // is left and offsets the reported position instead.
        Assert.Equal(new Playback.Audio.SilentStart(voiceMs, offsetMs), Playback.Audio.SilentStart.For(durationMs, fromMs));
    }
}

public class PlaybackMp3GaplessTests
{
    [Fact]
    public void The_decoder_delay_is_added_to_the_lead_in_and_subtracted_from_the_pad()
    {
        // The LAME convention, and the reason a hardcoded constant is wrong: 529 is the DECODER's own filterbank
        // delay, on top of whatever the encoder wrote.
        var tag = new Playback.Audio.Mp3Tag(DelaySamples: 576, PaddingSamples: 1_800, TotalSamples: 4_000_000);
        GaplessInfo g = tag.ToGapless(srcRate: 44_100, mixRate: 44_100);
        Assert.Equal(576 + Playback.Audio.Mp3Tag.DecoderDelaySamples, g.LeadInFrames);
        Assert.Equal(1_800 - Playback.Audio.Mp3Tag.DecoderDelaySamples, g.TrailPadFrames);
        Assert.Equal(4_000_000L, g.ExactFrames);
        Assert.True(g.TailKnown);
    }

    [Fact]
    public void A_padding_smaller_than_the_decoder_delay_never_goes_negative()
    {
        var tag = new Playback.Audio.Mp3Tag(DelaySamples: 576, PaddingSamples: 100, TotalSamples: -1);
        GaplessInfo g = tag.ToGapless(44_100, 44_100);
        Assert.Equal(0, g.TrailPadFrames);
        Assert.Equal(-1L, g.ExactFrames);
        Assert.False(g.TailKnown);
    }

    [Fact]
    public void The_trim_is_reported_in_mix_frames_so_the_engine_stays_codec_agnostic()
    {
        // 44.1 kHz source into a 48 kHz device: the numbers move with the rate, because `TrimmingSource` counts in
        // the mix domain and a source-rate figure would trim the wrong amount.
        var tag = new Playback.Audio.Mp3Tag(DelaySamples: 1_000, PaddingSamples: 2_000, TotalSamples: 441_000);
        GaplessInfo g = tag.ToGapless(srcRate: 44_100, mixRate: 48_000);
        Assert.Equal(480_000L, g.ExactFrames);
        Assert.True(g.LeadInFrames > 1_000 + Playback.Audio.Mp3Tag.DecoderDelaySamples);
    }
}
