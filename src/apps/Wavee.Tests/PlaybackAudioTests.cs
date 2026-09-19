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
    [Theory]
    [InlineData(EntityProvider.Spotify, Playback.Audio.SourceRoute.Spotify)]
    [InlineData(EntityProvider.WaveePodcast, Playback.Audio.SourceRoute.Podcast)]
    [InlineData(EntityProvider.Local, Playback.Audio.SourceRoute.Local)]
    [InlineData(EntityProvider.Module, Playback.Audio.SourceRoute.Module)]
    [InlineData(EntityProvider.Fake, Playback.Audio.SourceRoute.Silent)]
    [InlineData(EntityProvider.UserPlaylist, Playback.Audio.SourceRoute.None)]
    [InlineData(EntityProvider.None, Playback.Audio.SourceRoute.None)]
    public void Every_provider_routes_to_its_own_source(EntityProvider provider, Playback.Audio.SourceRoute expected)
        // G-109: a `wavee:episode:` fell into the Spotify track ladder, asked TRACK_V4 for it and failed as Restricted.
        => Assert.Equal(expected, Playback.Audio.RouteOf(provider));

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
        Assert.Equal(Spotify.Audio.Format.Aac, Playback.Audio.SniffFormat([0xFF, 0xF1, 0x50, 0x80]));
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
    public void A_module_body_is_read_from_its_own_byte_zero()
    {
        // A module hands container-relative bytes. The 167-byte skip this seam used to sniff for skipped the first 167
        // bytes of every module MP3, whose magic is not `OggS` or `fLaC`.
        var body = new byte[8];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)i;
        var src = new Playback.Audio.Prefetching(new SlowStream(body, misses: 0), seekable: true);
        Assert.True(src.TryOpen(new DataSpec { Position = 0, Length = -1 }));
        Assert.Equal(8L, src.Length);
        Span<byte> buf = stackalloc byte[4];
        Assert.Equal(4, src.Read(buf));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, buf.ToArray());
    }

    /// <summary>A growing stream: 0 until a wall-clock moment, then bytes — a module body that stalled for a while.</summary>
    sealed class LateStream(byte[] body, long availableAtTicks) : Stream
    {
        int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(Span<byte> buffer)
        {
            if (Environment.TickCount64 < availableAtTicks || _position >= body.Length) return 0;
            int n = Math.Min(buffer.Length, body.Length - _position);
            body.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void A_body_silent_past_the_fast_path_is_still_waited_for()
    {
        // G-103: at the fast path's deadline the read answered 0 — permanent EOF to the codec — and a slow module or radio
        // body was truncated. Past it the read keeps waiting (coarser) until the whole budget.
        var late = new LateStream([9, 8, 7], Environment.TickCount64 + 300);
        var src = new Playback.Audio.Prefetching(late, seekable: false, fastWaitMs: 50, totalWaitMs: 5_000);
        Assert.True(src.TryOpen(new DataSpec { Position = 0, Length = -1 }));
        Span<byte> buf = stackalloc byte[3];
        Assert.Equal(3, src.Read(buf));
        Assert.Equal(new byte[] { 9, 8, 7 }, buf.ToArray());

        var never = new Playback.Audio.Prefetching(new LateStream([1], long.MaxValue), seekable: false, fastWaitMs: 20, totalWaitMs: 150);
        Assert.Equal(0, never.Read(buf));                               // only a body silent for the whole budget reads as ended
    }
}

public class PlaybackPumpRulesTests
{
    [Fact]
    public void The_engine_never_adds_its_own_normalization_on_top_of_the_decoders()
    {
        // G-104 / D6: `NormMode.Track` over a voice with no ReplayGain tags is +4 dB at the −14 LUFS reference — on every
        // voice, on top of the decoders' own gain.
        Assert.Equal(NormMode.Off, Playback.Audio.EngineNormalization);
        Assert.Equal(1f, ReplayGain.ScalarLinear(default, Playback.Audio.EngineNormalization, -14f));
        Assert.True(ReplayGain.ScalarLinear(default, NormMode.Track, -14f) > 1.5f);   // what every track used to get
    }

    [Fact]
    public void Ten_loads_leave_nine_cancelled_and_one_live()
    {
        // G-117: a superseded load stops being worth its round trips the moment the next one starts.
        var loads = new Playback.Audio.Supersede();
        var tokens = new CancellationToken[10];
        for (int i = 0; i < tokens.Length; i++) tokens[i] = loads.Next();

        int cancelled = 0;
        foreach (CancellationToken t in tokens) if (t.IsCancellationRequested) cancelled++;
        Assert.Equal(9, cancelled);
        Assert.False(tokens[^1].IsCancellationRequested);

        loads.Cancel();                                                 // a Stop: nothing live
        Assert.True(tokens[^1].IsCancellationRequested);
    }

    [Theory]
    [InlineData(1_000L, 0L, 441_000L, 44_100, 442_000L)]              // opened at clock 1,000: the end is 441,000 frames later
    [InlineData(500_000L, 5_000L, 441_000L, 44_100, 500_000L + 441_000L - 220_500L)]   // anchored by a seek to 5 s
    [InlineData(10L, 20_000L, 441_000L, 44_100, 10L)]                 // an anchor past the end: never in the past of the anchor
    public void A_join_from_the_exact_length_is_the_anchor_clock_plus_the_frames_still_to_play(long clock, long playheadMs,
        long exactFrames, int rate, long expected)
        // G-113: rule 4, with the voice's decoded length instead of the catalogue duration.
        => Assert.Equal(expected, Playback.Audio.JoinFrameExact(clock, playheadMs, exactFrames, rate));
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
    public void The_silent_sink_is_a_paced_buffered_endpoint_not_the_engine_s_null_sink()
    {
        // The engine's HeadlessAudioEndpoint accepts every frame (WritableFrames = int.MaxValue), so a fake track's
        // clock ran as fast as the feeder spun. The silent sink is a finite buffer the "hardware" drains at the wall
        // clock's rate (headless plan §8 Q4, fixed in Wavee); everything above it is still the shipping graph.
        var format = new MixFormat(48_000, 2);
        IAudioEndpoint endpoint = Playback.Audio.SilentSink(format);
        Assert.IsType<Playback.Audio.PacedSilentEndpoint>(endpoint);
        Assert.True(endpoint.IsReady);
        Assert.Equal(format, endpoint.Sink.Format);
        Assert.Equal(format.SampleRate, endpoint.Clock.MixRate);
        var buffered = Assert.IsAssignableFrom<IBufferedAudioSink>(endpoint.Sink);
        Assert.Equal(format.SampleRate * Playback.Audio.PacedSilentEndpoint.DefaultCapacityMs / 1000, buffered.CapacityFrames);
        endpoint.Dispose();
    }

    [Fact]
    public void The_paced_endpoint_plays_queued_frames_at_the_clock_rate_holds_when_stopped_and_loses_starved_time()
    {
        long now = 0;
        var format = new MixFormat(48_000, 2);
        var endpoint = new Playback.Audio.PacedSilentEndpoint(format, capacityMs: 100, clock: () => now, ticksPerSecond: 1_000);
        var block = new float[4_800 * 2];

        Assert.Equal(4_800, endpoint.CapacityFrames);
        Assert.Equal(4_800, endpoint.Write(block, 4_800));
        Assert.Equal(0, endpoint.Write(block, 10));                     // full: a finite device buffer
        now = 50;
        Assert.Equal(0, endpoint.WritableFrames);                       // not started: no hardware time passes

        endpoint.Start();                                               // t = 50
        now = 75;
        Assert.Equal(1_200, endpoint.WritableFrames);                   // 25 ms × 48 kHz played
        Assert.True(endpoint.TryGetPlayed(out long played, out long qpc));
        Assert.Equal(1_200L, played);
        Assert.Equal(750_000L, qpc);                                    // stamped at 75 ms, in the 100 ns domain

        now = 200;
        Assert.Equal(4_800, endpoint.WritableFrames);                   // drained at 150 ms, starved since
        now = 300;
        Assert.Equal(4_800, endpoint.Write(block, 4_800));
        Assert.Equal(4_800, endpoint.PaddingFrames);                    // the starved 150 ms did not "play" these frames
        now = 310;
        Assert.Equal(4_320, endpoint.PaddingFrames);                    // 10 ms later, 480 frames are gone

        endpoint.Stop();
        now = 1_000;
        Assert.Equal(4_320, endpoint.PaddingFrames);                    // stopped: the clock holds (a pause)
        Assert.True(endpoint.TryGetPlayed(out played, out _));
        Assert.Equal(4_800L + 480L, played);

        endpoint.Reset();                                               // a flush: a new device epoch
        Assert.Equal(0, endpoint.PaddingFrames);
        Assert.True(endpoint.TryGetPlayed(out played, out _));
        Assert.Equal(0L, played);
    }

    [Fact]
    public async Task The_silent_session_is_fed_and_paced_so_it_plays_to_the_end_at_wall_clock_speed()
    {
        // The engine starts a session's output only once it is connected, and a feed-less session drains transport
        // commands on the caller's thread while its feeder renders: the silent session is connected AND driven by an
        // RT feed. Nothing here pumps — the feed must carry it from Opening to Ended — and 600 ms of silence cannot end
        // sooner than the endpoint's wall clock lets it play.
        var format = new MixFormat(48_000, 2);
        PcmAudioSession session = Playback.Audio.OpenSilentSession(format, voiceMs: 600, effects: null, volume: 1f);
        try
        {
            Assert.NotEqual(PlaybackState.Idle, session.CurrentState);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await session.PlayAsync();
            while (session.CurrentState != PlaybackState.Ended && clock.ElapsedMilliseconds < 15_000) await Task.Delay(5);
            Assert.Equal(PlaybackState.Ended, session.CurrentState);
            Assert.True(clock.ElapsedMilliseconds >= 450, $"600 ms of silence ended after {clock.ElapsedMilliseconds} ms");
        }
        finally { await session.DisposeAsync(); }
    }

    [Fact]
    public void An_endpoint_that_is_never_fed_filler_reads_empty_after_the_tail()
    {
        // The engine used to end a session only when its mixer was drained AND the buffered sink was empty, while its
        // RT feed kept rendering the drained mixer's silence into the sink regardless — queued, that filler refilled
        // the sink every wake and the session never reached Ended, its clock running past the end. The fix lives in
        // the engine now (PcmAudioSession.RenderBlock / DrainVerdict): once a session's mixer is drained, RenderBlock
        // stops rendering and submitting anything at all, so NO buffered sink — this silent one included — is ever fed
        // trailing filler again. This endpoint therefore needs no special "was this write the drained mixer's tail?"
        // detection of its own; it only has to behave like any other finite buffered sink and read empty once its
        // real content has drained and nothing further ever arrives.
        long now = 0;
        var format = new MixFormat(48_000, 2);
        var endpoint = new Playback.Audio.PacedSilentEndpoint(format, capacityMs: 100, clock: () => now, ticksPerSecond: 1_000);
        var block = new float[480 * 2];

        endpoint.Start();
        Assert.Equal(480, endpoint.Write(block, 480));                  // the content's last real block — queued, like any content
        Assert.Equal(480, endpoint.PaddingFrames);

        now = 20;                                                       // the queued tail plays out in 10 ms (480 frames @ 48 kHz)
        Assert.Equal(endpoint.CapacityFrames, endpoint.WritableFrames); // empty: nothing else was ever written —
                                                                          // the engine's Ended gate opens with no help from here
        Assert.True(endpoint.TryGetPlayed(out long played, out _));
        Assert.Equal(480L, played);                                     // the clock holds at the end of the content

        now = 500;                                                      // no further writes ever arrive: the empty state holds
        Assert.Equal(endpoint.CapacityFrames, endpoint.WritableFrames);
        Assert.True(endpoint.TryGetPlayed(out played, out _));
        Assert.Equal(480L, played);
    }

    [Fact]
    public async Task A_paused_silent_session_holds_its_clock_and_a_resume_plays_it_to_the_end()
    {
        // Pause and resume reach the SILENT session itself now (the player facade has none): the pause fades, drains
        // the endpoint and stops its clock; the resume starts it again.
        var format = new MixFormat(48_000, 2);
        PcmAudioSession session = Playback.Audio.OpenSilentSession(format, voiceMs: 1_200, effects: null, volume: 1f);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await session.PlayAsync();
            while (session.PlayedFrames < 9_600 && clock.ElapsedMilliseconds < 10_000) await Task.Delay(5);
            Assert.True(session.PlayedFrames >= 9_600, "200 ms never played");

            await session.PauseAsync();
            while (session.CurrentState != PlaybackState.Paused && clock.ElapsedMilliseconds < 10_000) await Task.Delay(5);
            Assert.Equal(PlaybackState.Paused, session.CurrentState);
            long held = session.PlayedFrames;
            await Task.Delay(300);
            Assert.Equal(held, session.PlayedFrames);

            await session.PlayAsync();
            while (session.CurrentState != PlaybackState.Ended && clock.ElapsedMilliseconds < 20_000) await Task.Delay(5);
            Assert.Equal(PlaybackState.Ended, session.CurrentState);
        }
        finally { await session.DisposeAsync(); }
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

public class PlaybackMetricsTests
{
    static Spotify.Audio.Stream.Stats Counters(long probes = 0, long cacheHits = 0, long requests = 0)
        => default(Spotify.Audio.Stream.Stats) with { Probes = probes, CacheHits = cacheHits, Requests = requests };

    [Fact]
    public void A_seek_is_far_when_a_probe_went_out_disk_when_only_the_cache_answered_and_ring_otherwise()
    {
        // The kind is DERIVED from the stream counters across the seek, never guessed from the distance (headless plan
        // §2.7). The fill that resumes at the landing is a plain range, not a probe, so it never makes a seek "far".
        Assert.Equal(Playback.Audio.SeekKind.Far,
            Playback.Audio.SeekKindOf(Counters(probes: 3, cacheHits: 1), Counters(probes: 4, cacheHits: 5)));
        Assert.Equal(Playback.Audio.SeekKind.Disk,
            Playback.Audio.SeekKindOf(Counters(cacheHits: 1, requests: 9), Counters(cacheHits: 3, requests: 10)));
        Assert.Equal(Playback.Audio.SeekKind.Ring,
            Playback.Audio.SeekKindOf(Counters(requests: 7), Counters(requests: 8)));
        Assert.Equal(Playback.Audio.SeekKind.Ring, Playback.Audio.SeekKindOf(default, default));
        // The wire values the headless snapshot carries as a byte: 0 ring · 1 far · 2 disk.
        Assert.Equal(0, (byte)Playback.Audio.SeekKind.Ring);
        Assert.Equal(1, (byte)Playback.Audio.SeekKind.Far);
        Assert.Equal(2, (byte)Playback.Audio.SeekKind.Disk);
    }

    [Fact]
    public void Decode_throughput_is_audio_seconds_per_wall_second_spent_decoding()
    {
        Assert.Equal(50f, Playback.Audio.XRealtime(sourceMicros: 10_000_000, wallTicks: 200, ticksPerSecond: 1_000), 3);
        Assert.Equal(1f, Playback.Audio.XRealtime(sourceMicros: 1_000_000, wallTicks: 10_000_000, ticksPerSecond: 10_000_000), 3);
        Assert.Equal(0f, Playback.Audio.XRealtime(0, 100, 1_000));      // nothing decoded yet
        Assert.Equal(0f, Playback.Audio.XRealtime(1_000, 0, 1_000));    // no time measured
    }

    [Fact]
    public void A_metrics_reset_clears_the_first_audio_and_seek_measurements()
    {
        Playback.Audio.ResetMetrics();
        Playback.Audio.Metrics m = Playback.Audio.Metrics.Read();
        Assert.Equal(-1, m.FirstAudioMs);
        Assert.False(m.FirstAudioFromHead);
        Assert.Equal(-1, m.LastSeekLatencyMs);
        Assert.Equal(-1, m.RingSeekLatencyMs);
        Assert.Equal(-1, m.FarSeekLatencyMs);
        Assert.Equal(-1, m.DiskSeekLatencyMs);
        Assert.Equal(0, m.Seeks);
        Assert.Equal(0, m.GaplessExact);
        Assert.Equal(0, m.GaplessAbandoned);
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
