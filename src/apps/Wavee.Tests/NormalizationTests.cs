// ── Wavee.Tests/NormalizationTests.cs — normalization modes, album mode, ReplayGain, the default volume (WP-4c, #167) ─────
//
// Plan §4.15 / §2.7 / D5 / D6. Everything below is a pure fact over numbers and bytes: `NormalizationFactor` and its mode, album
// and cap rules, the figures a voice keeps so a live mode switch can ramp it by `now / baked`, the ReplayGain tag and Spotify
// header parsers, the lossless catalogue's album params, the key defaults. Nothing here writes `Platform.Settings` (it is one
// global store other facts read), so every rule is exercised through the pure overloads with the setting values passed in.
//
//   THE MODE      is applied ONCE, in `NormalizationFactor`: Quiet −9 dB, Normal 0, Loud +3 dB over the figure (the figures are in the
//                 −14 LUFS frame; −23 / −14 / −11 LUFS).
//   THE CAP       factor × peak ≤ the limiter's ceiling (−1.5 dB) — except Loud, which lets the pre-volume limiter limit.
//   THE ALBUM     pair is used only when asked AND present; otherwise the track's.
//   THE LIVE RAMP is `now / baked` computed from THAT voice's figures (a crossfade has two tracks live, and the cap makes the ratio
//                 differ per track).

using System.IO;
using System.Text;
using FluentGpu.Media;
using Google.Protobuf;
using Wavee;
using Xunit;
using Af = Wavee.Protocol.Audiofiles;
using Md = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

public class NormalizationFactorTests
{
    const int Precision = 5;

    static float Db(float db) => MathF.Pow(10f, db / 20f);

    static float F(Playback.Audio.NormalizationMode mode, float gainDb, float peak = 0f, bool album = false,
        float albumGainDb = 0f, float albumPeak = 0f)
        => Playback.Audio.NormalizationFactor(true, mode, album, gainDb, peak, albumGainDb, albumPeak);

    static readonly Playback.Audio.NormalizationMode[] Modes =
        [Playback.Audio.NormalizationMode.Quiet, Playback.Audio.NormalizationMode.Normal, Playback.Audio.NormalizationMode.Loud];

    // ── 1. the modes ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_modes_are_the_persisted_ints_and_carry_the_23_14_11_lufs_pregains()
    {
        Assert.Equal(0, (int)Playback.Audio.NormalizationMode.Quiet);
        Assert.Equal(1, (int)Playback.Audio.NormalizationMode.Normal);
        Assert.Equal(2, (int)Playback.Audio.NormalizationMode.Loud);
        Assert.Equal(-9f, Playback.Audio.PregainDb(Playback.Audio.NormalizationMode.Quiet));
        Assert.Equal(0f, Playback.Audio.PregainDb(Playback.Audio.NormalizationMode.Normal));
        Assert.Equal(3f, Playback.Audio.PregainDb(Playback.Audio.NormalizationMode.Loud));
        Assert.Equal(4f, Playback.Audio.ReplayGainToSpotifyDb);                     // −18 → −14 LUFS
    }

    [Theory]
    [InlineData(0, Playback.Audio.NormalizationMode.Quiet)]
    [InlineData(1, Playback.Audio.NormalizationMode.Normal)]
    [InlineData(2, Playback.Audio.NormalizationMode.Loud)]
    [InlineData(-1, Playback.Audio.NormalizationMode.Normal)]
    [InlineData(3, Playback.Audio.NormalizationMode.Normal)]
    [InlineData(int.MaxValue, Playback.Audio.NormalizationMode.Normal)]
    public void A_persisted_int_outside_the_three_modes_reads_as_normal(int persisted, Playback.Audio.NormalizationMode expected)
        => Assert.Equal(expected, Playback.Audio.ModeOf(persisted));

    [Fact]
    public void A_mode_moves_the_factor_by_its_pregain_and_normal_leaves_the_figure_alone()
    {
        Assert.Equal(Db(-3f - 9f), F(Playback.Audio.NormalizationMode.Quiet, -3f), Precision);
        Assert.Equal(Db(-3f), F(Playback.Audio.NormalizationMode.Normal, -3f), Precision);
        Assert.Equal(Db(-3f + 3f), F(Playback.Audio.NormalizationMode.Loud, -3f), Precision);
        // A track with NO figure still moves by the mode: the modes are a loudness the user asked for, not a per-track gain.
        Assert.Equal(Db(-9f), F(Playback.Audio.NormalizationMode.Quiet, 0f), Precision);
        Assert.Equal(1f, F(Playback.Audio.NormalizationMode.Normal, 0f));
        Assert.Equal(Db(3f), F(Playback.Audio.NormalizationMode.Loud, 0f), Precision);
    }

    [Fact]
    public void Off_is_exactly_one_in_every_mode_and_with_album_mode()
    {
        foreach (var mode in Modes)
            foreach (bool album in new[] { false, true })
            {
                Assert.Equal(1f, Playback.Audio.NormalizationFactor(false, mode, album, -6f, 0.5f, -2f, 0.4f));
                Assert.Equal(1f, Playback.Audio.NormalizationFactor(false, mode, album, 12f, 0.99f));
            }
    }

    [Fact]
    public void A_figure_that_is_not_finite_is_no_figure_and_an_absurd_one_is_clamped_to_30_db()
    {
        foreach (var mode in Modes)
        {
            Assert.Equal(1f, F(mode, float.NaN));
            Assert.Equal(1f, F(mode, float.PositiveInfinity));
        }
        Assert.Equal(Db(30f), F(Playback.Audio.NormalizationMode.Normal, 400f), Precision);
        Assert.Equal(Db(-30f), F(Playback.Audio.NormalizationMode.Normal, -400f), Precision);
        Assert.Equal(Db(30f), F(Playback.Audio.NormalizationMode.Quiet, 400f), Precision);     // the clamp is after the pregain
        Assert.Equal(Db(-30f), F(Playback.Audio.NormalizationMode.Loud, -400f), Precision);
    }

    // ── 2. the cap ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_limiter_ceiling_is_the_engines_default_ceiling()
        => Assert.Equal(Playback.Audio.LimiterCeilingLinear, MathF.Pow(10f, LimiterSpec.Default.CeilingDbTp / 20f), Precision);

    [Fact]
    public void Quiet_and_normal_cap_a_boost_at_the_limiter_ceiling_and_loud_caps_it_at_one()
    {
        const float peak = 0.9f;
        float ceiling = Playback.Audio.LimiterCeilingLinear;
        // +10 dB on a 0.9 peak: every mode wants more than its cap allows.
        Assert.Equal(ceiling / peak, F(Playback.Audio.NormalizationMode.Normal, 10f, peak), Precision);
        Assert.Equal(ceiling / peak, F(Playback.Audio.NormalizationMode.Quiet, 10f, peak), Precision);   // +1 dB after the −9 pregain
        Assert.Equal(1f / peak, F(Playback.Audio.NormalizationMode.Loud, 10f, peak), Precision);
        // A cut is never capped.
        Assert.Equal(Db(-6f), F(Playback.Audio.NormalizationMode.Normal, -6f, peak), Precision);
    }

    [Fact]
    public void Loud_lifts_the_cap_to_one_where_the_other_modes_hold_it_at_the_ceiling()
    {
        // −4 dB + 3 dB pregain = −1 dB: ×1.0 peak = 0.891, over the ceiling (0.841) but under unity — Loud leaves it …
        Assert.Equal(Db(-1f), F(Playback.Audio.NormalizationMode.Loud, -4f, 1f), Precision);
        // … while the same −1 dB without a pregain is cut to the ceiling.
        Assert.Equal(Playback.Audio.LimiterCeilingLinear, F(Playback.Audio.NormalizationMode.Normal, -1f, 1f), Precision);
    }

    [Fact]
    public void The_factor_times_the_peak_never_exceeds_the_mode_s_cap()
    {
        foreach (var mode in Modes)
        {
            float cap = mode == Playback.Audio.NormalizationMode.Loud ? 1f : Playback.Audio.LimiterCeilingLinear;
            for (float gain = -24f; gain <= 24f; gain += 3f)
                foreach (float peak in new[] { 0.05f, 0.3f, 0.7f, 0.9f, 1f, 1.5f, 3.9f })
                    Assert.True(F(mode, gain, peak) * peak <= cap + 1e-5f, $"{mode} gain {gain} peak {peak}");
        }
    }

    // ── 3. the two-argument form ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The two-argument overload is Normal mode over the TRACK pair — every caller before the modes. It now caps at the
    /// limiter ceiling (N-1) rather than at unity: <c>AudioAdapterTests</c>' "6 dB on a 0.9 peak" expects the ceiling's cap.</summary>
    [Fact]
    public void The_two_argument_form_is_the_normal_track_result()
    {
        foreach (bool enabled in new[] { false, true })
            foreach (float gain in new[] { -6f, 0f, 6f, 400f, float.NaN })
                foreach (float peak in new[] { 0f, 0.5f, 0.9f })
                    Assert.Equal(
                        Playback.Audio.NormalizationFactor(enabled, Playback.Audio.NormalizationMode.Normal, false, gain, peak),
                        Playback.Audio.NormalizationFactor(enabled, gain, peak));
        Assert.Equal(Playback.Audio.LimiterCeilingLinear / 0.9f, Playback.Audio.NormalizationFactor(true, 6f, 0.9f), Precision);
    }

    // ── 4. album mode ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_album_pair_is_used_only_when_asked_and_present()
    {
        var normal = Playback.Audio.NormalizationMode.Normal;
        Assert.Equal(Db(-6f), F(normal, -6f, 0.5f, album: false, albumGainDb: -2f, albumPeak: 0.4f), Precision);
        Assert.Equal(Db(-2f), F(normal, -6f, 0.5f, album: true, albumGainDb: -2f, albumPeak: 0.4f), Precision);
        // No album figure (0) or a broken one: the track pair, not unity and not a crash.
        Assert.Equal(Db(-6f), F(normal, -6f, 0.5f, album: true, albumGainDb: 0f, albumPeak: 0f), Precision);
        Assert.Equal(Db(-6f), F(normal, -6f, 0.5f, album: true, albumGainDb: float.NaN, albumPeak: 0.4f), Precision);
    }

    [Fact]
    public void Album_mode_caps_by_the_album_peak_and_only_that()
    {
        var normal = Playback.Audio.NormalizationMode.Normal;
        // +6 dB album gain on a 0.95 album peak is held at the ceiling, though the track pair would not be.
        Assert.Equal(Playback.Audio.LimiterCeilingLinear / 0.95f, F(normal, -6f, 0.5f, album: true, albumGainDb: 6f, albumPeak: 0.95f), Precision);
        // An unknown album peak (0) caps nothing — the track's peak is not borrowed.
        Assert.Equal(Db(6f), F(normal, -6f, 0.99f, album: true, albumGainDb: 6f, albumPeak: 0f), Precision);
    }

    // ── 5. a live switch: now / baked, per voice, from THAT voice's figures ─────────────────────────────────────────

    [Fact]
    public void A_live_mode_switch_ramps_each_voice_by_now_over_baked_from_its_own_figures()
    {
        var normal = Playback.Audio.NormalizationMode.Normal;
        var loud = Playback.Audio.NormalizationMode.Loud;
        var plain = new Playback.Audio.NormalizationFigures(-3f, 0f);       // uncapped
        var hot = new Playback.Audio.NormalizationFigures(5f, 0.95f);       // held by the cap in both modes

        float plainRatio = plain.Factor(true, loud, false) / plain.Factor(true, normal, false);
        float hotRatio = hot.Factor(true, loud, false) / hot.Factor(true, normal, false);

        Assert.Equal(Db(3f), plainRatio, Precision);                        // exactly the pregain
        Assert.Equal(1f / Playback.Audio.LimiterCeilingLinear, hotRatio, Precision);   // 1.0 cap over the ceiling cap
        Assert.NotEqual(plainRatio, hotRatio, 3);                           // one ratio for both voices would be wrong for one
        Assert.Equal(1f, plain.Factor(true, normal, false) / plain.Factor(true, normal, false));   // the same mode again: no ramp
    }

    [Fact]
    public void Toggling_normalization_ramps_from_and_to_unity()
    {
        var figures = new Playback.Audio.NormalizationFigures(-4f, 0.5f);
        float on = figures.Factor(true, Playback.Audio.NormalizationMode.Normal, false);
        Assert.Equal(1f, figures.Factor(false, Playback.Audio.NormalizationMode.Normal, false));
        Assert.Equal(Db(-4f), on, Precision);
        // baked = 1 (it opened off), now = on: the ramp is the whole factor; and back: 1 / baked.
        Assert.Equal(on, on / 1f);
        Assert.Equal(1f, (on * (1f / on)), Precision);
    }

    [Fact]
    public void Figures_report_the_factor_the_pure_function_gives()
    {
        var figures = new Playback.Audio.NormalizationFigures(-6f, 0.5f, -2f, 0.4f);
        foreach (var mode in Modes)
            foreach (bool album in new[] { false, true })
                Assert.Equal(Playback.Audio.NormalizationFactor(true, mode, album, -6f, 0.5f, -2f, 0.4f), figures.Factor(true, mode, album));
    }
}

public class ReplayGainTagTests
{
    /// <summary>A Vorbis comment BLOCK as bytes: vendor, count, then length-prefixed fields, little-endian.</summary>
    static byte[] CommentBlock(params string[] fields)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        byte[] vendor = Encoding.UTF8.GetBytes("Wavee.Tests");
        w.Write((uint)vendor.Length);
        w.Write(vendor);
        w.Write((uint)fields.Length);
        foreach (string field in fields)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(field);
            w.Write((uint)utf8.Length);
            w.Write(utf8);
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>The Ogg Vorbis comment HEADER packet: type 3, "vorbis", the block, the framing bit.</summary>
    static byte[] CommentPacket(params string[] fields)
    {
        byte[] block = CommentBlock(fields);
        var packet = new List<byte> { 3, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' };
        packet.AddRange(block);
        packet.Add(1);
        return packet.ToArray();
    }

    static byte[] AsciiBytes(string s) => Encoding.ASCII.GetBytes(s);

    // ── the value parsers ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("-6.54 dB", -6.54f)]
    [InlineData("+3.20 dB", 3.2f)]
    [InlineData("-6.54", -6.54f)]
    [InlineData("  -6.54dB  ", -6.54f)]
    [InlineData("0.00 DB", 0f)]
    [InlineData("12 db", 12f)]
    [InlineData("-30 dB", -30f)]
    public void A_gain_tag_parses_with_or_without_its_unit(string text, float expected)
    {
        Assert.True(Playback.Audio.ReplayGainTags.TryParseGainDb(AsciiBytes(text), out float db));
        Assert.Equal(expected, db);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dB")]
    [InlineData("loud")]
    [InlineData("NaN dB")]
    [InlineData("Infinity")]
    [InlineData("31 dB")]
    [InlineData("-99.0 dB")]
    [InlineData("1,5 dB")]
    [InlineData("-6.54 dB extra")]
    public void A_gain_tag_that_is_not_a_believable_gain_is_no_gain(string text)
    {
        Assert.False(Playback.Audio.ReplayGainTags.TryParseGainDb(AsciiBytes(text), out float db));
        Assert.Equal(0f, db);
    }

    [Theory]
    [InlineData("0.988553", 0.988553f)]
    [InlineData(" 1.012000 ", 1.012f)]
    [InlineData("3.9", 3.9f)]
    public void A_peak_tag_parses_to_a_linear_amplitude(string text, float expected)
    {
        Assert.True(Playback.Audio.ReplayGainTags.TryParsePeak(AsciiBytes(text), out float peak));
        Assert.Equal(expected, peak);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0.000000")]
    [InlineData("-0.5")]
    [InlineData("9.0")]
    [InlineData("NaN")]
    [InlineData("peak")]
    public void A_peak_that_is_not_a_believable_peak_is_unknown(string text)
    {
        Assert.False(Playback.Audio.ReplayGainTags.TryParsePeak(AsciiBytes(text), out float peak));
        Assert.Equal(0f, peak);
    }

    // ── the comment walkers ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_comment_block_yields_the_four_tags_whatever_the_key_case_and_ignores_the_rest()
    {
        byte[] block = CommentBlock("TITLE=Sea of Voices", "replaygain_track_gain=-6.54 dB", "REPLAYGAIN_TRACK_PEAK=0.988553",
            "ReplayGain_Album_Gain=-5.10 dB", "REPLAYGAIN_ALBUM_PEAK=1.020000", "REPLAYGAIN_REFERENCE_LOUDNESS=89.0 dB", "NOEQUALS");
        var tags = Playback.Audio.ReplayGainTags.FromCommentBlock(block);
        Assert.True(tags.Any);
        Assert.True(tags.HasTrackGain);
        Assert.True(tags.HasAlbumGain);
        Assert.Equal(-6.54f, tags.TrackGainDb);
        Assert.Equal(0.988553f, tags.TrackPeak);
        Assert.Equal(-5.10f, tags.AlbumGainDb);
        Assert.Equal(1.02f, tags.AlbumPeak);
    }

    [Fact]
    public void A_block_with_no_replaygain_tags_has_none()
    {
        var tags = Playback.Audio.ReplayGainTags.FromCommentBlock(CommentBlock("TITLE=x", "ARTIST=y"));
        Assert.False(tags.Any);
        Assert.False(tags.HasTrackGain);
        Assert.False(tags.HasAlbumGain);
        Assert.Equal(default(Playback.Audio.ReplayGainTags), tags);
    }

    [Fact]
    public void A_repeated_key_keeps_the_last_value_and_a_bad_value_is_an_absent_tag()
    {
        var twice = Playback.Audio.ReplayGainTags.FromCommentBlock(CommentBlock("REPLAYGAIN_TRACK_GAIN=-1 dB", "REPLAYGAIN_TRACK_GAIN=-2 dB"));
        Assert.Equal(-2f, twice.TrackGainDb);
        var bad = Playback.Audio.ReplayGainTags.FromCommentBlock(CommentBlock("REPLAYGAIN_TRACK_GAIN=quiet", "REPLAYGAIN_ALBUM_GAIN=-3 dB"));
        Assert.False(bad.HasTrackGain);
        Assert.True(bad.HasAlbumGain);
        Assert.Equal(-3f, bad.AlbumGainDb);
    }

    [Fact]
    public void A_vorbis_comment_packet_is_the_block_after_its_seven_byte_prefix()
    {
        var tags = Playback.Audio.ReplayGainTags.FromVorbisComment(CommentPacket("REPLAYGAIN_TRACK_GAIN=-6.54 dB", "REPLAYGAIN_TRACK_PEAK=0.9"));
        Assert.True(tags.HasTrackGain);
        Assert.Equal(-6.54f, tags.TrackGainDb);
        Assert.Equal(0.9f, tags.TrackPeak);
        Assert.False(tags.HasAlbumGain);

        byte[] notAComment = CommentPacket("REPLAYGAIN_TRACK_GAIN=-6.54 dB");
        notAComment[0] = 1;                                                    // an identification header
        Assert.Equal(default(Playback.Audio.ReplayGainTags), Playback.Audio.ReplayGainTags.FromVorbisComment(notAComment));
        Assert.Equal(default(Playback.Audio.ReplayGainTags), Playback.Audio.ReplayGainTags.FromVorbisComment([]));
        Assert.Equal(default(Playback.Audio.ReplayGainTags), Playback.Audio.ReplayGainTags.FromVorbisComment([3, (byte)'v', (byte)'o']));
    }

    [Fact]
    public void A_hostile_or_truncated_comment_block_never_throws_and_never_reads_past_its_end()
    {
        byte[] good = CommentBlock("REPLAYGAIN_TRACK_GAIN=-6.54 dB", "REPLAYGAIN_ALBUM_GAIN=-5 dB");
        for (int cut = 0; cut < good.Length; cut++)
            _ = Playback.Audio.ReplayGainTags.FromCommentBlock(good.AsSpan(0, cut));       // every prefix: no exception

        // A vendor length past the end, and a count of four billion over three bytes of data.
        byte[] vendor = [0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3];
        Assert.Equal(default(Playback.Audio.ReplayGainTags), Playback.Audio.ReplayGainTags.FromCommentBlock(vendor));
        byte[] count = [0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 5, 0, 0, 0, (byte)'a', (byte)'=', (byte)'b', (byte)'c', (byte)'d'];
        Assert.Equal(default(Playback.Audio.ReplayGainTags), Playback.Audio.ReplayGainTags.FromCommentBlock(count));
    }

    // ── into the −14 frame ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tags_are_folded_at_plus_4_db_only_when_the_source_carried_no_track_gain()
    {
        var none = new Playback.Audio.NormalizationFigures(0f, 0f);
        var tags = new Playback.Audio.ReplayGainTags(-6.5f, 0.97f, -5.5f, 0.99f, HasTrackGain: true, HasAlbumGain: true);

        Assert.Equal(new Playback.Audio.NormalizationFigures(-2.5f, 0.97f, -1.5f, 0.99f),
            Playback.Audio.NormalizationFigures.WithReplayGain(in none, in tags));

        // The catalogue's / header's figure wins: Spotify's own measurement beats a tag.
        var spotify = new Playback.Audio.NormalizationFigures(-8f, 0.9f, -7f, 0.8f);
        Assert.Equal(spotify, Playback.Audio.NormalizationFigures.WithReplayGain(in spotify, in tags));

        // No tags: unchanged.
        var noTags = default(Playback.Audio.ReplayGainTags);
        Assert.Equal(none, Playback.Audio.NormalizationFigures.WithReplayGain(in none, in noTags));
    }

    [Fact]
    public void Track_tags_without_album_tags_leave_the_album_pair_empty_and_album_tags_alone_stand_in_for_the_track()
    {
        var none = new Playback.Audio.NormalizationFigures(0f, 0f);

        var trackOnly = new Playback.Audio.ReplayGainTags(-6.5f, 0.97f, 0f, 0.99f, HasTrackGain: true, HasAlbumGain: false);
        // The album PEAK tag without an album GAIN tag is not an album pair.
        Assert.Equal(new Playback.Audio.NormalizationFigures(-2.5f, 0.97f, 0f, 0f),
            Playback.Audio.NormalizationFigures.WithReplayGain(in none, in trackOnly));

        var albumOnly = new Playback.Audio.ReplayGainTags(0f, 0f, -5.5f, 0.99f, HasTrackGain: false, HasAlbumGain: true);
        Assert.Equal(new Playback.Audio.NormalizationFigures(-1.5f, 0.99f, -1.5f, 0.99f),
            Playback.Audio.NormalizationFigures.WithReplayGain(in none, in albumOnly));
    }

    [Fact]
    public void A_replaygain_track_gain_of_minus_18_lufs_plays_at_the_minus_14_figure_plus_the_mode()
    {
        // A tag of −6.5 dB is a −11.5 LUFS track against the −18 reference; against −14 that is −2.5 dB.
        var none = new Playback.Audio.NormalizationFigures(0f, 0f);
        var tags = Playback.Audio.ReplayGainTags.FromCommentBlock(CommentBlock("REPLAYGAIN_TRACK_GAIN=-6.50 dB"));
        var figures = Playback.Audio.NormalizationFigures.WithReplayGain(in none, in tags);
        Assert.Equal(MathF.Pow(10f, -2.5f / 20f), figures.Factor(true, Playback.Audio.NormalizationMode.Normal, false), 5);
        Assert.Equal(MathF.Pow(10f, (-2.5f - 9f) / 20f), figures.Factor(true, Playback.Audio.NormalizationMode.Quiet, false), 5);
    }
}

public class SpotifyHeaderNormalizationTests
{
    static byte[] Header(int length = 160, float gain = -3.5f, float peak = 0.9f, float albumGain = -2.25f, float albumPeak = 0.8f)
    {
        var header = new byte[length];
        if (length >= 148) BitConverter.TryWriteBytes(header.AsSpan(144), gain);
        if (length >= 152) BitConverter.TryWriteBytes(header.AsSpan(148), peak);
        if (length >= 156) BitConverter.TryWriteBytes(header.AsSpan(152), albumGain);
        if (length >= 160) BitConverter.TryWriteBytes(header.AsSpan(156), albumPeak);
        return header;
    }

    [Fact]
    public void The_header_needs_160_bytes_for_its_four_floats()
        => Assert.Equal(160, Spotify.Audio.HeaderGainBytes);

    [Fact]
    public void The_album_gain_and_peak_sit_at_bytes_152_and_156()
    {
        byte[] header = Header();
        Assert.Equal(-2.25f, Spotify.Audio.HeadAlbumGainDb(header));
        Assert.Equal(0.8f, Spotify.Audio.HeadAlbumPeak(header));
        // The track pair is where it always was.
        Assert.Equal(-3.5f, Spotify.Audio.HeadGainDb(header));
        Assert.Equal(0.9f, Spotify.Audio.HeadPeak(header));
    }

    [Fact]
    public void A_head_too_short_to_hold_a_float_answers_unknown()
    {
        byte[] header = Header();
        Assert.Equal(0f, Spotify.Audio.HeadAlbumGainDb(header.AsSpan(0, 155)));
        Assert.Equal(-2.25f, Spotify.Audio.HeadAlbumGainDb(header.AsSpan(0, 156)));
        Assert.Equal(0f, Spotify.Audio.HeadAlbumPeak(header.AsSpan(0, 159)));
        Assert.Equal(0.8f, Spotify.Audio.HeadAlbumPeak(header.AsSpan(0, 160)));
    }

    [Fact]
    public void A_garbage_album_figure_is_unknown_and_never_takes_the_track_pair_with_it()
    {
        byte[] header = Header(albumGain: 99f, albumPeak: float.NaN);
        Assert.Equal(0f, Spotify.Audio.HeadAlbumGainDb(header));           // beyond ±30 dB
        Assert.Equal(0f, Spotify.Audio.HeadAlbumPeak(header));
        Assert.Equal((-3.5f, 0.9f, 0f, 0f),
            Spotify.Audio.GainWithAlbumFor(Spotify.Audio.Format.OggVorbis320, 0f, 0f, 0f, 0f, header));
    }

    [Fact]
    public void An_ogg_body_opens_with_the_header_s_four_figures_and_nothing_else_does()
    {
        byte[] header = Header();
        Assert.Equal((-3.5f, 0.9f, -2.25f, 0.8f),
            Spotify.Audio.GainWithAlbumFor(Spotify.Audio.Format.OggVorbis320, 0f, 0f, 0f, 0f, header));
        // A FLAC's byte 144 is STREAMINFO data and an MP3 carries no header at all.
        Assert.Equal((0f, 0f, 0f, 0f), Spotify.Audio.GainWithAlbumFor(Spotify.Audio.Format.Flac, 0f, 0f, 0f, 0f, header));
        Assert.Equal((0f, 0f, 0f, 0f), Spotify.Audio.GainWithAlbumFor(Spotify.Audio.Format.Mp3, 0f, 0f, 0f, 0f, header));
        // No header at hand yet (the body learns it off chunk 0), and one a byte short of the album peak.
        Assert.Equal((0f, 0f, 0f, 0f),
            Spotify.Audio.GainWithAlbumFor(Spotify.Audio.Format.OggVorbis160, 0f, 0f, 0f, 0f, ReadOnlySpan<byte>.Empty));
        Assert.Equal((0f, 0f, 0f, 0f),
            Spotify.Audio.GainWithAlbumFor(Spotify.Audio.Format.OggVorbis160, 0f, 0f, 0f, 0f, header.AsSpan(0, 159)));
    }

    [Fact]
    public void The_catalogue_s_figures_win_and_bring_their_own_album_pair_not_the_header_s()
    {
        byte[] header = Header();
        Assert.Equal((-6f, 0.7f, -4f, 0.6f),
            Spotify.Audio.GainWithAlbumFor(Spotify.Audio.Format.Flac24, -6f, 0.7f, -4f, 0.6f, header));
        // A catalogue with a track gain and no album params has no album pair: it is not borrowed from bytes that are not a header.
        Assert.Equal((-6f, 0.7f, 0f, 0f),
            Spotify.Audio.GainWithAlbumFor(Spotify.Audio.Format.OggVorbis320, -6f, 0.7f, 0f, 0f, header));
    }

    [Fact]
    public void The_track_pair_form_still_answers_the_track_pair()
    {
        byte[] header = Header();
        Assert.Equal((-3.5f, 0.9f), Spotify.Audio.GainFor(Spotify.Audio.Format.OggVorbis320, 0f, 0f, header));
        Assert.Equal((-6f, 0.7f), Spotify.Audio.GainFor(Spotify.Audio.Format.Flac24, -6f, 0.7f, header));
    }

    // ── the lossless catalogue's album params ───────────────────────────────────────────────────────────────────────

    static Md.AudioFile AudioFileOf(Md.AudioFile.Types.Format format, byte marker)
    {
        var id = new byte[20];
        id[0] = marker;
        return new Md.AudioFile { FileId = ByteString.CopyFrom(id), Format = format };
    }

    static Md.Track TrackOf(byte gidMarker)
    {
        var gid = new byte[16];
        gid[0] = gidMarker;
        return new Md.Track { Gid = ByteString.CopyFrom(gid), Duration = 210_000 };
    }

    [Fact]
    public void A_lossless_choice_carries_the_album_params_at_the_minus_14_reference_with_no_mode()
    {
        var lossless = new Af.AudioFilesExtensionResponse();
        lossless.Files.Add(new Af.ExtendedAudioFile { File = AudioFileOf(Md.AudioFile.Types.Format.FlacFlac24Bit, 0xF2) });
        lossless.DefaultFileNormalizationParams = new Af.NormalizationParams { LoudnessDb = -8f, TruePeakDb = -3f };
        lossless.DefaultAlbumNormalizationParams = new Af.NormalizationParams { LoudnessDb = -10f, TruePeakDb = -2f };

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(TrackOf(0x44), lossless, Spotify.Audio.Quality.Lossless, 0, "NL");

        Assert.Equal(Spotify.Audio.Format.Flac24, choice.Fmt);
        // The track pair is unchanged; the album pair is the same formula over the album's loudness and peak.
        Assert.Equal(Spotify.Audio.NormalizationGain(-8f, -3f), choice.GainDb);
        Assert.Equal(Spotify.Audio.PeakLinear(-3f), choice.Peak);
        Assert.Equal(Spotify.Audio.NormalizationGain(-10f, -2f), choice.AlbumGainDb);
        Assert.Equal(-4f, choice.AlbumGainDb, 3);                            // −14 − (−10): no mode pregain on the catalogue figure
        Assert.Equal(Spotify.Audio.PeakLinear(-2f), choice.AlbumPeak);
    }

    [Fact]
    public void A_lossless_choice_without_album_params_has_an_empty_album_pair()
    {
        var lossless = new Af.AudioFilesExtensionResponse();
        lossless.Files.Add(new Af.ExtendedAudioFile { File = AudioFileOf(Md.AudioFile.Types.Format.FlacFlac, 0xF1) });
        lossless.DefaultFileNormalizationParams = new Af.NormalizationParams { LoudnessDb = -8f, TruePeakDb = -3f };

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(TrackOf(0x45), lossless, Spotify.Audio.Quality.Lossless, 0, "NL");

        Assert.Equal(Spotify.Audio.NormalizationGain(-8f, -3f), choice.GainDb);
        Assert.Equal(0f, choice.AlbumGainDb);
        Assert.Equal(0f, choice.AlbumPeak);
    }
}

public class NormalizationWiringTests
{
    // ── the decoders report the figures behind what they folded ────────────────────────────────────────────────────

    [Fact]
    public void The_opened_album_pair_reaches_the_flac_and_vorbis_adapters()
    {
        var opened = new Playback.Audio.Opened(Spotify.Audio.Format.Flac24, 180_000, -5f, "", 0, false, 0.9f, -3f, 0.8f);
        var flac = Playback.Audio.CreateDecoderFor(in opened);
        Assert.IsType<Playback.Audio.FlacAudioDecoder>(flac);
        Assert.Equal((Playback.Audio.NormalizationFigures?)new Playback.Audio.NormalizationFigures(-5f, 0.9f, -3f, 0.8f),
            ((Playback.Audio.IGainFolding)flac).AppliedFigures);

        var ogg = Playback.Audio.CreateDecoderFor(opened with { Format = Spotify.Audio.Format.OggVorbis320 });
        Assert.IsType<Playback.Audio.VorbisAudioDecoder>(ogg);
        Assert.Equal((Playback.Audio.NormalizationFigures?)new Playback.Audio.NormalizationFigures(-5f, 0.9f, -3f, 0.8f),
            ((Playback.Audio.IGainFolding)ogg).AppliedFigures);
        (ogg as IDisposable)?.Dispose();
    }

    [Fact]
    public void A_decoder_with_nothing_to_add_leaves_the_figures_to_the_pump()
    {
        // MP3 (and AAC) fold the factor but keep no figures of their own: the default member answers null and the pump uses the Opened's.
        var mp3 = new Playback.Audio.Mp3AudioDecoder(0f);
        Assert.Null(((Playback.Audio.IGainFolding)mp3).AppliedFigures);
    }

    sealed class TrackOnlySource : Playback.Audio.INormalizationSource
    {
        public float GainDb => -1f;
        public float Peak => 0.5f;
    }

    [Fact]
    public void A_source_that_knows_no_album_answers_zero_through_the_default_members()
    {
        Playback.Audio.INormalizationSource source = new TrackOnlySource();
        Assert.Equal(-1f, source.GainDb);
        Assert.Equal(0f, source.AlbumGainDb);
        Assert.Equal(0f, source.AlbumPeak);
    }

    /// <summary>A Spotify body that learned its figures off chunk 0, behind the random-access face — what the Vorbis adapter reads
    /// after the header pages, so a late figure (and its album pair) still reaches the first sample.</summary>
    sealed class AlbumSource(byte[] data, float gainDb, float peak, float albumGainDb, float albumPeak)
        : IMediaByteSource, Playback.Audio.IRandomAccessBytes, Playback.Audio.INormalizationSource
    {
        long _cursor;
        uint _epoch;

        public float GainDb => gainDb;
        public float Peak => peak;
        public float AlbumGainDb => albumGainDb;
        public float AlbumPeak => albumPeak;
        public long? Length => data.Length;
        public SourceCaps Caps => new() { Seekable = true, KnownLength = true, ExpensiveSeek = false };
        public uint Epoch => _epoch;
        public long TailGranule => -1;
        public bool TryOpen(in DataSpec spec) { _cursor = Math.Max(0, spec.Position); return true; }

        public int Read(Span<byte> dst)
        {
            int n = ReadAt(_cursor, dst, _epoch);
            if (n > 0) _cursor += n;
            return n;
        }

        public long Seek(long offset) => _cursor = Math.Max(0, offset);
        public void Cancel() { }
        public void Close() { }

        public int ReadAt(long offset, Span<byte> dst, uint epoch)
        {
            if (epoch != _epoch) return -1;
            if (offset >= data.Length || dst.Length == 0) return 0;
            int n = (int)Math.Min(dst.Length, data.Length - offset);
            data.AsSpan((int)offset, n).CopyTo(dst);
            return n;
        }

        public void Retarget(long probeOffset, int probeBytes, uint epoch) => _epoch = epoch;
        public void ResumeFrom(long offset) { }
    }

    [Fact]
    public void The_vorbis_adapter_takes_the_byte_source_s_four_figures_when_it_opens()
    {
        byte[] file = VorbisFixture.Bytes("pink-320.ogg");
        var decoder = new Playback.Audio.VorbisAudioDecoder(0f);
        Assert.True(decoder.TryOpen(new AlbumSource(file, -6f, 0.9f, -2f, 0.8f), new MixFormat(48_000, 2), out _));
        var figures = ((Playback.Audio.IGainFolding)decoder).AppliedFigures!.Value;
        Assert.Equal(new Playback.Audio.NormalizationFigures(-6f, 0.9f, -2f, 0.8f), figures);

        bool enabled = Platform.Settings.Get(Platform.Keys.NormalizationEnabled);
        var mode = Playback.Audio.ModeOf(Platform.Settings.Get(Platform.Keys.NormalizationMode));
        bool album = Platform.Settings.Get(Platform.Keys.NormalizationAlbum);
        Assert.Equal(figures.Factor(enabled, mode, album), decoder.AppliedGainLinear);
        decoder.Dispose();
    }

    // ── the settings keys and the default volume ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_new_keys_spell_their_persisted_names_and_default_to_normal_track_gain()
    {
        Assert.Equal("playback.normalization.mode", Platform.Keys.NormalizationMode.Name);
        Assert.Equal(1, Platform.Keys.NormalizationMode.Default);              // Normal
        Assert.Equal((int)Playback.Audio.NormalizationMode.Normal, Platform.Keys.NormalizationMode.Default);
        Assert.Equal("playback.normalization.album", Platform.Keys.NormalizationAlbum.Name);
        Assert.False(Platform.Keys.NormalizationAlbum.Default);
        // The existing switch is where it was.
        Assert.Equal("playback.normalization", Platform.Keys.NormalizationEnabled.Name);
        Assert.True(Platform.Keys.NormalizationEnabled.Default);
    }

    [Fact]
    public void The_mode_and_album_choices_round_trip_through_a_store_and_an_unset_store_reads_the_defaults()
    {
        var store = new MemoryAppSettings();
        Assert.Equal(Playback.Audio.NormalizationMode.Normal, Playback.Audio.ModeOf(store.Get(Platform.Keys.NormalizationMode)));
        Assert.False(store.Get(Platform.Keys.NormalizationAlbum));

        store.Set(Platform.Keys.NormalizationMode, (int)Playback.Audio.NormalizationMode.Loud);
        store.Set(Platform.Keys.NormalizationAlbum, true);
        Assert.Equal(Playback.Audio.NormalizationMode.Loud, Playback.Audio.ModeOf(store.Get(Platform.Keys.NormalizationMode)));
        Assert.True(store.Get(Platform.Keys.NormalizationAlbum));
    }

    /// <summary>D6: a fresh profile plays at about −6 dB. 0.794³ = 0.5005 through the cubic taper, and the unmute default is the
    /// same number, so un-muting a fresh profile does not change the level.</summary>
    [Fact]
    public void A_fresh_profile_starts_at_minus_6_db_and_unmute_returns_to_it()
    {
        Assert.Equal(0.794f, Platform.Keys.SavedVolume.Default);
        Assert.Equal(Playback.UnmuteDefault, Platform.Keys.SavedVolume.Default);
        float dB = 20f * MathF.Log10(MathF.Pow(Platform.Keys.SavedVolume.Default, 3f));
        Assert.InRange(dB, -6.1f, -5.9f);
    }
}
