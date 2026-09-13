// ── Wavee.Tests/LyricsRulesTests.cs — the lyrics SURFACE's pure rules ─────────────────────────────────────────────
//
// Ported suites, name for name: `LyricsBlurPolicyTests`, `LyricsSyncGateTests`, `LyricsRowShapeTests`,
// `LyricsMediaClockTests`, `Lyrics/NpvLyricsPeekTests`. Plus the EIGHT rule sets ch 22 §8 marks UNVERIFIED — no test
// existed for them in 0.2.9 and the chapter says to add them with the port: `Fx`, `Emphasis.Pack`/`OpacityOf`,
// `ResolveLine`/`SungOutMs`/`AdvancePastInterlude`, `Wipe.ComputeSplit`/`HeldSyllableGlow`, the cascade math,
// `Wipe.SoftnessOfLine`, `Prefs`, and `Authority.IsRicher`.
//
// Every fact here is a function of values: no scene, no GPU, no clock of its own — the media clock is driven at an
// injected QPC frequency of 1000 so a "QPC" reads directly as milliseconds.

using Wavee;
using Xunit;

namespace Wavee.Tests;

static class Lyr
{
    public static Lyrics.Syllable S(long start, long end, string text) => new(start, end, text);

    public static Lyrics.Line Line(long start, string text, params Lyrics.Syllable[] syl)
        => new(start, text, syl, null, null, null, syl.Length > 0);

    public static Lyrics.Line Timed(long start, long end, string text)
        => new(start, text, [], end);

    public static Lyrics.Doc Doc(Lyrics.SyncKind sync, params Lyrics.Line[] lines)
        => new("t", sync is Lyrics.SyncKind.Line or Lyrics.SyncKind.Syllable, lines, sync, "test");
}

public class LyricsFxTests
{
    [Theory]
    [InlineData(-1, 0f)]
    [InlineData(0, 0f)]
    [InlineData(1, 1.25f)]
    [InlineData(2, 2.5f)]
    [InlineData(3, 4f)]
    [InlineData(4, 5.5f)]
    [InlineData(9, 6.5f)]
    public void The_rail_ladder_is_the_measured_reference(int dist, float sigma)
        => Assert.Equal(sigma, Lyrics.Fx.DofSigma(dist, large: false));

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(1, 1.2f)]
    [InlineData(2, 2.0f)]
    [InlineData(3, 2.6f)]
    [InlineData(9, 3.0f)]
    public void The_stage_ladder_keeps_the_shape_and_flattens_the_tail(int dist, float sigma)
        => Assert.Equal(sigma, Lyrics.Fx.DofSigma(dist, large: true));

    [Fact]
    public void Both_ladders_are_monotone_and_start_at_zero_on_the_focus()
    {
        for (int large = 0; large <= 1; large++)
        {
            bool big = large == 1;
            Assert.Equal(0f, Lyrics.Fx.DofSigma(0, big));
            for (int d = 1; d < 12; d++)
                Assert.True(Lyrics.Fx.DofSigma(d, big) >= Lyrics.Fx.DofSigma(d - 1, big));
        }
    }

    [Fact]
    public void The_stage_is_never_foggier_than_the_rail_at_any_ring()
    {
        // The whole reason the stage has its own ladder: at the rail's far rungs a READING surface dissolves.
        for (int d = 0; d < 12; d++)
            Assert.True(Lyrics.Fx.DofSigma(d, large: true) <= Lyrics.Fx.DofSigma(d, large: false));
    }
}

public class LyricsBlurPolicyTests
{
    [Fact]
    public void Auto_resolves_off_the_gpu_tier()
    {
        Assert.Equal(Lyrics.BlurPolicy.WeakGpuDefault, Lyrics.BlurPolicy.Resolve(Lyrics.BlurPolicy.Auto, weakGpu: true));
        Assert.Equal(Lyrics.BlurPolicy.StrongGpuDefault, Lyrics.BlurPolicy.Resolve(Lyrics.BlurPolicy.Auto, weakGpu: false));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(140, 100)]      // a value from a newer build's ladder clamps rather than trusting the store
    [InlineData(-9, 0)]         // …and any other negative is NOT the auto sentinel
    public void A_stored_value_is_clamped_into_range(int stored, int expected)
        => Assert.Equal(expected, Lyrics.BlurPolicy.Resolve(stored, weakGpu: false));

    [Fact]
    public void Scale_is_the_zero_to_one_multiplier()
    {
        Assert.Equal(0f, Lyrics.BlurPolicy.Scale(0));
        Assert.Equal(0.4f, Lyrics.BlurPolicy.Scale(40));
        Assert.Equal(1f, Lyrics.BlurPolicy.Scale(100));
    }

    [Fact]
    public void Enabled_is_false_only_at_zero()
    {
        Assert.False(Lyrics.BlurPolicy.Enabled(0));
        for (int s = 1; s <= 100; s++) Assert.True(Lyrics.BlurPolicy.Enabled(s));
    }
}

public class LyricsSyncGateTests
{
    [Fact]
    public void Sync_is_suppressed_exactly_while_a_video_is_the_current_media()
    {
        Assert.True(Lyrics.SyncGate.SyncSuppressed(videoActive: true));
        Assert.False(Lyrics.SyncGate.SyncSuppressed(videoActive: false));
    }
}

public class LyricsRowShapeTests
{
    static Lyrics.Doc WordSynced()
        => Lyr.Doc(Lyrics.SyncKind.Syllable, Lyr.Line(0, "hello", Lyr.S(0, 200, "hel"), Lyr.S(200, 400, "lo")));

    [Fact]
    public void The_same_instance_keeps_its_rows() { var d = WordSynced(); Assert.True(Lyrics.RowShape.SameRows(d, d)); }

    [Fact]
    public void A_different_track_never_keeps_its_rows()
    {
        var a = WordSynced();
        var b = a with { TrackId = "other" };
        Assert.False(Lyrics.RowShape.SameRows(a, b));
    }

    [Fact]
    public void Identical_text_with_NO_syllables_is_NOT_the_same_row()
    {
        // THE regression: "syllable lyrics work sometimes and sometimes not". A line-synced first answer and its
        // word-synced upgrade usually carry byte-identical TEXT, so a text-only rule kept rows with no wipe.
        var word = WordSynced();
        var line = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "hello", []));
        Assert.False(Lyrics.RowShape.SameRows(word, line));
    }

    [Fact]
    public void A_changed_syllable_timing_rebuilds_the_rows()
    {
        var a = WordSynced();
        var b = Lyr.Doc(Lyrics.SyncKind.Syllable, Lyr.Line(0, "hello", Lyr.S(0, 250, "hel"), Lyr.S(250, 400, "lo")));
        Assert.False(Lyrics.RowShape.SameRows(a, b));
    }

    [Fact]
    public void A_changed_secondary_layer_rebuilds_the_rows()
    {
        var a = WordSynced();
        var b = a with { Lines = [a.Lines[0] with { Translation = "hallo" }] };
        Assert.False(Lyrics.RowShape.SameRows(a, b));
    }

    [Fact]
    public void A_byte_identical_rebuild_keeps_the_rows()
    {
        Assert.True(Lyrics.RowShape.SameRows(WordSynced(), WordSynced()));
    }

    [Fact]
    public void A_different_line_count_rebuilds_the_rows()
    {
        var a = WordSynced();
        var b = a with { Lines = [a.Lines[0], a.Lines[0]] };
        Assert.False(Lyrics.RowShape.SameRows(a, b));
    }
}

public class LyricsMediaClockTests
{
    static Lyrics.MediaClock New() => new(qpcFrequency: 1000);   // a "QPC" IS a millisecond

    [Fact]
    public void The_first_sample_ever_snaps()
    {
        var c = New();
        Assert.True(c.OnSample(1000, 0, playing: true));
        Assert.Equal(1000, c.At(0));
        Assert.Equal(1500, c.At(500));
    }

    [Fact]
    public void A_big_disagreement_snaps_and_a_small_one_slews()
    {
        var c = New();
        c.OnSample(1000, 0, true);
        Assert.True(c.OnSample(5000, 100, true));     // a seek: > 250 ms from the prediction
        var d = New();
        d.OnSample(1000, 0, true);
        Assert.False(d.OnSample(1100, 100, true));    // 0 ms of drift at t=100 predicted 1100 — no snap
    }

    [Fact]
    public void The_slew_is_bounded_and_the_value_never_jumps()
    {
        var c = New();
        c.OnSample(1000, 0, true);
        long before = c.At(200);
        c.OnSample(1240, 200, true);                  // 40 ms ahead of the prediction — inside the snap threshold
        long after = c.At(200);
        Assert.InRange(after - before, -1, 1);        // continuity at the pivot: the value on screen does not move
        // …and the correction closes over about a second, never as a step.
        long later = c.At(1200);
        Assert.InRange(later - (before + 1000), 0, 60);
    }

    [Fact]
    public void While_paused_the_line_is_pinned_flat()
    {
        var c = New();
        c.OnSample(4000, 0, playing: false);
        Assert.Equal(4000, c.At(0));
        Assert.Equal(4000, c.At(100_000));            // however far the frame clock drifts
        Assert.False(c.Playing);
    }

    [Fact]
    public void A_paused_scrub_is_reflected_immediately()
    {
        var c = New();
        c.OnSample(4000, 0, false);
        c.OnSample(9000, 10, false);
        Assert.Equal(9000, c.At(50));
    }

    [Fact]
    public void A_resume_rebases_but_is_not_reported_as_a_jump()
    {
        var c = New();
        c.OnSample(4000, 0, false);
        Assert.False(c.OnSample(4000, 100, playing: true));   // a plain resume is not a seek
        Assert.Equal(4000, c.At(100));
        Assert.Equal(4100, c.At(200));
    }

    [Fact]
    public void It_never_goes_backward_while_playing_between_snaps()
    {
        var c = New();
        c.OnSample(1000, 0, true);
        long last = long.MinValue;
        for (long q = 0; q < 2000; q += 7)
        {
            long ms = c.At(q);
            Assert.True(ms >= last);
            last = ms;
        }
    }

    [Fact]
    public void The_diagnostics_read_and_reset()
    {
        var c = New();
        c.OnSample(1000, 0, true);
        c.At(0); c.At(16); c.At(33);
        var d = c.ReadAndResetDiagnostics();
        Assert.Equal(3, d.Frames);
        Assert.True(d.Snaps >= 1);
        var zeroed = c.ReadAndResetDiagnostics();
        Assert.Equal(0, zeroed.Frames);
        Assert.Equal(0, zeroed.Snaps);
    }

    [Fact]
    public void Reset_seeds_unconditionally()
    {
        var c = New();
        c.OnSample(1000, 0, true);
        c.Reset(60_000, 10, playing: true);
        Assert.Equal(60_000, c.At(10));
    }
}

public class LyricsResolveTests
{
    static readonly Lyrics.Line[] Lines =
    [
        Lyr.Timed(1000, 2000, "one"),
        Lyr.Timed(3000, 4000, "two"),
        Lyr.Timed(5000, 6000, "three"),
    ];

    [Theory]
    [InlineData(0, -1)]
    [InlineData(999, -1)]
    [InlineData(1000, 0)]
    [InlineData(2999, 0)]
    [InlineData(3000, 1)]
    [InlineData(999_999, 2)]
    public void ResolveLine_is_the_last_line_started(long now, int expected)
        => Assert.Equal(expected, Lyrics.ResolveLine(Lines, now));

    [Fact]
    public void ResolveLine_on_an_empty_document_is_minus_one()
        => Assert.Equal(-1, Lyrics.ResolveLine([], 0));

    [Fact]
    public void SungOut_prefers_the_last_syllable_then_the_authored_end_then_the_next_start()
    {
        var word = Lyr.Doc(Lyrics.SyncKind.Syllable,
            Lyr.Line(0, "ab", Lyr.S(0, 300, "a"), Lyr.S(300, 700, "b")),
            Lyr.Timed(2000, 2500, "next"));
        Assert.Equal(700, Lyrics.SungOutMs(word, 0));

        var authored = Lyr.Doc(Lyrics.SyncKind.Line, Lyr.Timed(0, 900, "a"), Lyr.Timed(2000, 2500, "b"));
        Assert.Equal(900, Lyrics.SungOutMs(authored, 0));

        var derived = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "a", []), Lyr.Timed(2000, 2500, "b"));
        Assert.Equal(2000, Lyrics.SungOutMs(derived, 0));

        var last = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "a", []));
        Assert.Equal(long.MaxValue, Lyrics.SungOutMs(last, 0));
    }

    [Fact]
    public void A_word_synced_break_advances_the_focus_and_reports_the_gap()
    {
        var doc = Lyr.Doc(Lyrics.SyncKind.Syllable,
            Lyr.Line(0, "a", Lyr.S(0, 1000, "a")),
            Lyr.Timed(9000, 10_000, "b"));
        int next = Lyrics.AdvancePastInterlude(doc, 0, 4000, out long gs, out long ge);
        Assert.Equal(1, next);
        Assert.Equal(1000, gs);
        Assert.Equal(9000, ge);
    }

    [Fact]
    public void A_line_synced_break_reports_the_gap_but_does_NOT_advance()
    {
        // A line-synced row has no wipe with which to look "unsung", so retiring it early would light the NEXT line
        // fully white seconds before it is sung.
        var doc = Lyr.Doc(Lyrics.SyncKind.Line, Lyr.Timed(0, 1000, "a"), Lyr.Timed(9000, 10_000, "b"));
        int next = Lyrics.AdvancePastInterlude(doc, 0, 4000, out long gs, out long ge);
        Assert.Equal(0, next);
        Assert.Equal(1000, gs);
        Assert.Equal(9000, ge);
    }

    [Fact]
    public void A_gap_shorter_than_the_threshold_is_not_an_interlude()
    {
        var doc = Lyr.Doc(Lyrics.SyncKind.Syllable,
            Lyr.Line(0, "a", Lyr.S(0, 1000, "a")),
            Lyr.Timed(1000 + Lyrics.InterludeGapMs - 1, 9000, "b"));
        Assert.Equal(0, Lyrics.AdvancePastInterlude(doc, 0, 4000, out long gs, out long ge));
        Assert.Equal(0, gs);
        Assert.Equal(0, ge);
    }

    [Fact]
    public void The_last_line_is_never_advanced_past()
    {
        var doc = Lyr.Doc(Lyrics.SyncKind.Syllable, Lyr.Line(0, "a", Lyr.S(0, 1000, "a")));
        Assert.Equal(0, Lyrics.AdvancePastInterlude(doc, 0, 999_999, out _, out _));
    }

    [Fact]
    public void Before_the_sung_out_point_nothing_advances()
    {
        var doc = Lyr.Doc(Lyrics.SyncKind.Syllable,
            Lyr.Line(0, "a", Lyr.S(0, 1000, "a")),
            Lyr.Timed(9000, 10_000, "b"));
        Assert.Equal(0, Lyrics.AdvancePastInterlude(doc, 0, 500, out _, out _));
    }
}

public class LyricsPeekClockTests
{
    static Lyrics.Doc Timed() => Lyr.Doc(Lyrics.SyncKind.Line,
        Lyr.Timed(1000, 2000, "one"), Lyr.Timed(3000, 4000, "two"), Lyr.Timed(5000, 6000, "three"));

    [Fact]
    public void ShouldShow_needs_a_timed_document_with_lines()
    {
        Assert.True(Lyrics.PeekClock.ShouldShow(Timed()));
        Assert.False(Lyrics.PeekClock.ShouldShow(null));
        Assert.False(Lyrics.PeekClock.ShouldShow(Lyr.Doc(Lyrics.SyncKind.Line)));
        Assert.False(Lyrics.PeekClock.ShouldShow(Lyr.Doc(Lyrics.SyncKind.Unsynced, new Lyrics.Line(0, "x", []))));
    }

    [Fact]
    public void A_hidden_document_returns_minus_one_for_both()
        => Assert.Equal((-1, -1), Lyrics.PeekClock.ActiveAndPeek(null, 0));

    [Fact]
    public void Before_the_first_line_the_peek_is_the_faded_first_line()
        => Assert.Equal((-1, 0), Lyrics.PeekClock.ActiveAndPeek(Timed(), 0));

    [Fact]
    public void The_lead_is_applied_here_not_by_the_caller()
    {
        var doc = Timed();
        Assert.Equal((-1, 0), Lyrics.PeekClock.ActiveAndPeek(doc, 1000 - Lyrics.LeadMs - 1));
        Assert.Equal((0, 1), Lyrics.PeekClock.ActiveAndPeek(doc, 1000 - Lyrics.LeadMs));
    }

    [Fact]
    public void After_the_last_line_the_last_holds_and_the_peek_is_gone()
        => Assert.Equal((2, -1), Lyrics.PeekClock.ActiveAndPeek(Timed(), 999_999));

    [Fact]
    public void The_peek_is_always_the_next_line()
        => Assert.Equal((1, 2), Lyrics.PeekClock.ActiveAndPeek(Timed(), 3500));

    [Fact]
    public void The_peek_lead_is_the_views_lead()
        => Assert.Equal(Lyrics.LeadMs, Lyrics.PeekClock.LeadMs);
}

public class LyricsEmphasisTests
{
    [Fact]
    public void The_active_line_is_bucket_zero_and_full_opacity()
    {
        int packed = Lyrics.Emphasis.Pack(5, active: 5, reserveLine: -1);
        Assert.Equal(0, Lyrics.Emphasis.DistOf(packed));
        Assert.Equal(1f, Lyrics.Emphasis.OpacityOf(packed));
    }

    [Fact]
    public void The_bucket_saturates_at_six_so_far_lines_share_one_value()
    {
        Assert.Equal(Lyrics.Emphasis.Pack(20, 5, -1), Lyrics.Emphasis.Pack(40, 5, -1));
        Assert.Equal(Lyrics.Emphasis.MaxBucket, Lyrics.Emphasis.DistOf(Lyrics.Emphasis.Pack(40, 5, -1)));
    }

    [Fact]
    public void No_active_line_puts_every_row_at_the_saturated_bucket()
        => Assert.Equal(Lyrics.Emphasis.MaxBucket, Lyrics.Emphasis.DistOf(Lyrics.Emphasis.Pack(0, active: -1, reserveLine: -1)));

    [Fact]
    public void A_sung_line_rides_the_dimmer_ladder()
    {
        int past = Lyrics.Emphasis.Pack(4, active: 5, reserveLine: -1);
        int future = Lyrics.Emphasis.Pack(6, active: 5, reserveLine: -1);
        Assert.Equal(1, Lyrics.Emphasis.DistOf(past));
        Assert.Equal(1, Lyrics.Emphasis.DistOf(future));
        Assert.True(Lyrics.Emphasis.OpacityOf(past) < Lyrics.Emphasis.OpacityOf(future));
    }

    [Fact]
    public void The_past_bit_is_NOT_set_at_the_saturated_bucket()
    {
        // Both ladders bottom out at the same value there, so tagging far lines would be visually identical while
        // breaking the far-line no-op — every line above the active one would re-render on a seek.
        int far = Lyrics.Emphasis.Pack(0, active: 30, reserveLine: -1);
        Assert.Equal(Lyrics.Emphasis.MaxBucket, far);
    }

    [Fact]
    public void The_reserve_bit_carries_no_ladder_meaning()
    {
        int plain = Lyrics.Emphasis.Pack(3, 5, reserveLine: -1);
        int held = Lyrics.Emphasis.Pack(3, 5, reserveLine: 3);
        Assert.True(Lyrics.Emphasis.HasReserve(held));
        Assert.False(Lyrics.Emphasis.HasReserve(plain));
        Assert.Equal(Lyrics.Emphasis.OpacityOf(plain), Lyrics.Emphasis.OpacityOf(held));
        Assert.Equal(Lyrics.Emphasis.DistOf(plain), Lyrics.Emphasis.DistOf(held));
    }

    [Fact]
    public void Both_ladders_are_monotone_away_from_the_focus()
    {
        float prev = 2f;
        for (int d = 0; d <= 7; d++)
        {
            float o = Lyrics.Emphasis.OpacityOf(d);
            Assert.True(o <= prev);
            prev = o;
        }
    }
}

public class LyricsWipeTests
{
    [Fact]
    public void A_line_with_no_syllables_has_no_split()
        => Assert.Equal(0f, Lyrics.Wipe.ComputeSplit(new Lyrics.Line(0, "hello", []), 500));

    [Fact]
    public void The_split_is_char_weighted_and_exact_at_the_ends()
    {
        var line = Lyr.Line(0, "aaabbbbbbb", Lyr.S(0, 100, "aaa"), Lyr.S(100, 200, "bbbbbbb"));
        Assert.Equal(0f, Lyrics.Wipe.ComputeSplit(line, -1));
        Assert.Equal(0.3f, Lyrics.Wipe.ComputeSplit(line, 100), 4);
        Assert.Equal(1f, Lyrics.Wipe.ComputeSplit(line, 200));
        Assert.Equal(1f, Lyrics.Wipe.ComputeSplit(line, 999));
    }

    [Fact]
    public void The_syllable_being_sung_contributes_its_linear_share()
    {
        var line = Lyr.Line(0, "aabb", Lyr.S(0, 100, "aa"), Lyr.S(100, 200, "bb"));
        Assert.Equal(0.75f, Lyrics.Wipe.ComputeSplit(line, 150), 4);
    }

    [Fact]
    public void The_split_is_monotone_in_time()
    {
        var line = Lyr.Line(0, "abcdef", Lyr.S(0, 100, "ab"), Lyr.S(150, 300, "cd"), Lyr.S(300, 400, "ef"));
        float prev = -1f;
        for (long t = -50; t < 500; t += 7)
        {
            float s = Lyrics.Wipe.ComputeSplit(line, t);
            Assert.True(s >= prev);
            prev = s;
        }
    }

    [Fact]
    public void A_short_syllable_never_glows()
    {
        var line = Lyr.Line(0, "a", Lyr.S(0, 300, "a"));
        Assert.Equal(0f, Lyrics.Wipe.HeldSyllableGlow(line, 150));
    }

    [Fact]
    public void A_held_note_swells_and_melts_and_never_exceeds_one()
    {
        var line = Lyr.Line(0, "a", Lyr.S(0, 2000, "a"));
        Assert.Equal(0f, Lyrics.Wipe.HeldSyllableGlow(line, -1));
        float mid = Lyrics.Wipe.HeldSyllableGlow(line, 1000);
        Assert.InRange(mid, 0.99f, 1f);
        Assert.True(Lyrics.Wipe.HeldSyllableGlow(line, 100) < mid);    // still swelling in
        Assert.True(Lyrics.Wipe.HeldSyllableGlow(line, 1950) < mid);   // already melting out
        Assert.Equal(0f, Lyrics.Wipe.HeldSyllableGlow(line, 2000));    // past the end
    }

    [Fact]
    public void A_halo_never_nests_inside_a_depth_of_field_sigma()
    {
        // THE glow rule: a nested blur layer is what the compositor's pin key refuses.
        Assert.Equal(0f, Lyrics.Wipe.GlowSigma(rowDofSigma: 1.25f, glowAlpha: 1f, large: false, blurScale: 1f));
        Assert.True(Lyrics.Wipe.GlowSigma(rowDofSigma: 0f, glowAlpha: 1f, large: false, blurScale: 1f) > 0f);
    }

    [Fact]
    public void The_halo_scales_with_the_blur_dial()
    {
        float full = Lyrics.Wipe.GlowSigma(0f, 1f, large: true, blurScale: 1f);
        float half = Lyrics.Wipe.GlowSigma(0f, 1f, large: true, blurScale: 0.5f);
        Assert.Equal(full * 0.5f, half, 4);
    }

    [Fact]
    public void The_feather_is_a_per_line_fraction_clamped_at_both_ends()
    {
        Assert.Equal(Lyrics.Wipe.SoftnessMax, Lyrics.Wipe.SoftnessOfLine(runLengthDip: 10f, large: false));
        Assert.Equal(Lyrics.Wipe.SoftnessMin, Lyrics.Wipe.SoftnessOfLine(runLengthDip: 5000f, large: false));
        Assert.Equal(0.05f, Lyrics.Wipe.SoftnessOfLine(runLengthDip: 100f, large: false), 4);
        Assert.Equal(0.07f, Lyrics.Wipe.SoftnessOfLine(runLengthDip: 100f, large: true), 4);
    }

    [Fact]
    public void Reduced_motion_zeroes_the_per_word_lift_but_not_the_wipe()
    {
        Assert.Equal(0f, Lyrics.Wipe.LiftFor(large: false, reducedMotion: true));
        Assert.Equal(Lyrics.Wipe.LiftDip, Lyrics.Wipe.LiftFor(large: false, reducedMotion: false));
        Assert.Equal(Lyrics.Wipe.LiftDipLarge, Lyrics.Wipe.LiftFor(large: true, reducedMotion: false));
    }

    [Fact]
    public void A_settled_endpoint_stops_writing()
    {
        Assert.True(Lyrics.Wipe.SplitSettled(1f, 1f));
        Assert.True(Lyrics.Wipe.SplitSettled(0f, 0f));
        Assert.False(Lyrics.Wipe.SplitSettled(1f, 0.9f));
        Assert.False(Lyrics.Wipe.SplitSettled(0.5f, 0.5f));
    }
}

public class LyricsCascadeTests
{
    static (float[] Comp, float[] Vel, float[] Delay, float[] Rate, byte[] Write) Slab(int n)
        => (new float[n], new float[n], new float[n], new float[n], new byte[n]);

    [Fact]
    public void Arming_with_no_delta_does_nothing()
    {
        var s = Slab(4);
        Assert.False(Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 0f, 2, false));
    }

    [Fact]
    public void Rank_zero_moves_immediately_and_the_tail_staggers_to_the_cap()
    {
        var s = Slab(12);
        Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, delta: 100f, newActive: 5, reducedMotion: false);
        Assert.Equal(0f, s.Delay[4]);                                   // the outgoing line — rank 0
        Assert.Equal(0f, s.Delay[0]);                                   // …and everything above it
        Assert.Equal(Lyrics.Cascade.StaggerMs, s.Delay[5]);
        Assert.Equal(Lyrics.Cascade.StaggerMs * Lyrics.Cascade.MaxRank, s.Delay[11]);   // capped
    }

    [Fact]
    public void Reduced_motion_drops_every_stagger_to_one_rigid_translate()
    {
        var s = Slab(12);
        Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 100f, 5, reducedMotion: true);
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(0f, s.Delay[i]);
            Assert.Equal(Lyrics.Cascade.SettleY / Lyrics.Cascade.TotalS, s.Rate[i], 4);
            Assert.Equal(100f, s.Comp[i]);   // the lines STILL have to end up where the latched viewport put them
        }
    }

    [Fact]
    public void A_re_target_ADDS_rather_than_assigning()
    {
        var s = Slab(4);
        Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 100f, 1, false);
        Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 50f, 1, false);
        Assert.Equal(150f, s.Comp[0]);
    }

    [Fact]
    public void A_reversing_re_target_clamps_j1_so_it_can_never_overshoot()
    {
        var s = Slab(1);
        Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 100f, 0, false);
        Lyrics.Cascade.Step(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 60f);   // build some velocity
        Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, -300f, 0, false);
        float start = s.Comp[0];
        Assert.True(start < 0f);
        for (int i = 0; i < 200; i++)
        {
            Lyrics.Cascade.Step(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 16f);
            Assert.True(s.Comp[0] <= 0.5f);    // never crosses through zero into an overshoot
        }
    }

    [Fact]
    public void Everything_lands_exactly_and_the_landing_write_is_flagged()
    {
        var s = Slab(3);
        Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 120f, 1, false);
        bool moving = true;
        int frames = 0;
        bool sawLanding = false;
        while (moving && frames++ < 200)
        {
            moving = Lyrics.Cascade.Step(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 16f);
            for (int i = 0; i < 3; i++) if (s.Write[i] == Lyrics.Cascade.WriteLanded) sawLanding = true;
        }
        Assert.False(moving);
        Assert.True(sawLanding);
        for (int i = 0; i < 3; i++) { Assert.Equal(0f, s.Comp[i]); Assert.Equal(0f, s.Vel[i]); }
        // ~0.48 s at 16 ms a frame, plus the deepest rank's 240 ms stagger.
        Assert.InRange(frames * 16, 400, 900);
    }

    [Fact]
    public void Out_of_band_lines_are_RETIRED_rather_than_left_holding_a_stale_transform()
    {
        int n = Lyrics.Cascade.WriteBand * 3;
        var s = Slab(n);
        Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 100f, newActive: n - 1, reducedMotion: false);
        Assert.Equal(100f, s.Comp[n - 1]);
        Assert.Equal(0f, s.Comp[0]);                                   // far above the band
        // Now re-arm around line 0: the line that just left the band is retired with an exact identity write.
        Lyrics.Cascade.Arm(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 100f, newActive: 0, reducedMotion: false);
        Assert.Equal(0f, s.Comp[n - 1]);
        Assert.Equal(Lyrics.Cascade.WriteLanded, s.Write[n - 1]);
    }

    [Fact]
    public void A_settled_line_costs_no_write()
    {
        var s = Slab(4);
        Assert.False(Lyrics.Cascade.Step(s.Comp, s.Vel, s.Delay, s.Rate, s.Write, 16f));
        for (int i = 0; i < 4; i++) Assert.Equal(Lyrics.Cascade.WriteNone, s.Write[i]);
    }

    [Fact]
    public void The_dt_clamp_stops_a_parked_window_teleporting()
    {
        Assert.Equal(Lyrics.Cascade.DtMaxMs, Lyrics.Cascade.ClampDt(10_000f, 16f));
        Assert.Equal(16f, Lyrics.Cascade.ClampDt(0f, 16f));
        Assert.Equal(33f, Lyrics.Cascade.ClampDt(33f, 16f));
    }

    [Fact]
    public void The_write_gate_exempts_the_landing()
    {
        Assert.True(Lyrics.Cascade.ShouldWrite(Lyrics.Cascade.WriteLanded, 0f, 0f));
        Assert.False(Lyrics.Cascade.ShouldWrite(Lyrics.Cascade.WriteMoving, 1f, 1.01f));
        Assert.True(Lyrics.Cascade.ShouldWrite(Lyrics.Cascade.WriteMoving, 1f, 2f));
        Assert.False(Lyrics.Cascade.ShouldWrite(Lyrics.Cascade.WriteNone, 1f, 99f));
    }

    [Fact]
    public void A_later_rank_runs_faster_so_every_rank_lands_together()
        => Assert.True(Lyrics.Cascade.RateFor(Lyrics.Cascade.StaggerMs * 4) > Lyrics.Cascade.RateFor(0f));
}

public class LyricsMotionDemandTests
{
    static Lyrics.MotionLanes Quiet(long now = 1000, long next = 5000) => new(
        Playing: true, VoiceActive: false, DotsActive: false, GlowFadeActive: false, DofRampPending: false,
        CascadePending: false, FollowUnsettled: false, Following: true, NowMs: now, NextEventMs: next);

    [Fact]
    public void Playing_is_NOT_motion()
    {
        // The whole 2026-09-12 fix: mounting the stepper on `playing` held the UI thread at panel rate for a track.
        var d = Lyrics.MotionDemand.Evaluate(Quiet());
        Assert.False(d.NeedsTicks);
        Assert.Equal(5000 - Lyrics.MotionDemand.ArmLeadMs, d.WakeAtMs);
    }

    [Fact]
    public void Any_live_lane_demands_ticks()
    {
        Assert.True(Lyrics.MotionDemand.Evaluate(Quiet() with { VoiceActive = true }).NeedsTicks);
        Assert.True(Lyrics.MotionDemand.Evaluate(Quiet() with { DotsActive = true }).NeedsTicks);
        Assert.True(Lyrics.MotionDemand.Evaluate(Quiet() with { GlowFadeActive = true }).NeedsTicks);
        Assert.True(Lyrics.MotionDemand.Evaluate(Quiet() with { DofRampPending = true }).NeedsTicks);
        Assert.True(Lyrics.MotionDemand.Evaluate(Quiet() with { FollowUnsettled = true }).NeedsTicks);
    }

    [Fact]
    public void The_media_clock_lanes_only_count_while_the_clock_advances()
    {
        var paused = Quiet() with { Playing = false, VoiceActive = true, DotsActive = true };
        Assert.False(Lyrics.MotionDemand.Evaluate(paused).NeedsTicks);
        Assert.Equal(Lyrics.MotionDemand.None, Lyrics.MotionDemand.Evaluate(paused).WakeAtMs);
    }

    [Fact]
    public void The_two_integrators_survive_a_pause()
    {
        // Neither can go quiescent mid-flight, which is why they are in the lane set unconditionally.
        Assert.True(Lyrics.MotionDemand.Evaluate(Quiet() with { Playing = false, CascadePending = true }).NeedsTicks);
        Assert.True(Lyrics.MotionDemand.Evaluate(Quiet() with { Playing = false, Following = false }).NeedsTicks);
    }

    [Fact]
    public void An_event_already_inside_the_lead_window_stays_live()
    {
        var d = Lyrics.MotionDemand.Evaluate(Quiet(now: 1000, next: 1000 + Lyrics.MotionDemand.ArmLeadMs));
        Assert.True(d.NeedsTicks);
        Assert.Equal(Lyrics.MotionDemand.None, d.WakeAtMs);
    }

    [Fact]
    public void No_future_event_means_nothing_to_wake_for()
    {
        var d = Lyrics.MotionDemand.Evaluate(Quiet(next: Lyrics.MotionDemand.None));
        Assert.False(d.NeedsTicks);
        Assert.Equal(Lyrics.MotionDemand.None, d.WakeAtMs);
    }

    [Fact]
    public void NextEvent_prefers_the_lead_shifted_handoff_then_the_start()
    {
        Lyrics.Line[] lines = [Lyr.Timed(1000, 2000, "a"), Lyr.Timed(5000, 6000, "b")];
        Assert.Equal(5000 - 140, Lyrics.MotionDemand.NextEventMs(lines, 0, 2500, 140));
        Assert.Equal(5000, Lyrics.MotionDemand.NextEventMs(lines, 0, 4900, 140));
        Assert.Equal(Lyrics.MotionDemand.None, Lyrics.MotionDemand.NextEventMs(lines, 0, 99_999, 140));
        Assert.Equal(Lyrics.MotionDemand.None, Lyrics.MotionDemand.NextEventMs(null, 0, 0, 140));
    }
}

public class LyricsAuthorityTests
{
    [Fact]
    public void Any_word_by_word_line_scores_three_whatever_the_declared_kind()
    {
        var doc = Lyr.Doc(Lyrics.SyncKind.Line, Lyr.Line(0, "a", Lyr.S(0, 100, "a")));
        Assert.Equal(3, Lyrics.Authority.Richness(doc));
    }

    [Theory]
    [InlineData(Lyrics.SyncKind.Syllable, 3)]
    [InlineData(Lyrics.SyncKind.Line, 2)]
    [InlineData(Lyrics.SyncKind.Unsynced, 1)]
    [InlineData(Lyrics.SyncKind.None, 0)]
    public void Otherwise_the_kinds_own_rank_decides(Lyrics.SyncKind kind, int rank)
        => Assert.Equal(rank, Lyrics.Authority.Richness(Lyr.Doc(kind, new Lyrics.Line(0, "a", []))));

    [Fact]
    public void A_higher_rank_wins_and_a_lower_one_is_refused()
    {
        var word = Lyr.Doc(Lyrics.SyncKind.Syllable, Lyr.Line(0, "a", Lyr.S(0, 100, "a")));
        var line = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "a", []));
        Assert.True(Lyrics.Authority.IsRicher(word, line));
        Assert.False(Lyrics.Authority.IsRicher(line, word));
    }

    [Fact]
    public void An_equal_rank_is_broken_by_syllable_count_and_only_inside_the_top_tier()
    {
        var few = Lyr.Doc(Lyrics.SyncKind.Syllable, Lyr.Line(0, "a", Lyr.S(0, 100, "a")));
        var many = Lyr.Doc(Lyrics.SyncKind.Syllable, Lyr.Line(0, "ab", Lyr.S(0, 100, "a"), Lyr.S(100, 200, "b")));
        Assert.True(Lyrics.Authority.IsRicher(many, few));
        Assert.False(Lyrics.Authority.IsRicher(few, many));

        var lineA = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "a", []));
        var lineB = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "b", []));
        Assert.False(Lyrics.Authority.IsRicher(lineB, lineA));   // below the syllable tier a tie is never a win
    }

    [Fact]
    public void An_upgrade_is_HELD_only_while_a_document_is_playing_mid_line()
    {
        var doc = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "a", []));
        Assert.False(Lyrics.Authority.ApplyImmediately(playing: true, doc, activeLine: 3));
        Assert.True(Lyrics.Authority.ApplyImmediately(playing: false, doc, 3));
        Assert.True(Lyrics.Authority.ApplyImmediately(true, null, 3));
        Assert.True(Lyrics.Authority.ApplyImmediately(true, doc, -1));
    }
}

public class LyricsPrefsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 0)]
    [InlineData(-3, 0)]
    public void A_stray_mode_shows_the_original_rather_than_nothing(int stored, int expected)
        => Assert.Equal(expected, Lyrics.Prefs.Clamp(stored));

    [Fact]
    public void The_capability_bits_are_arithmetic_not_a_map()
    {
        Assert.Equal(Lyrics.Prefs.HasTranslation, Lyrics.Prefs.BitFor(Lyrics.Prefs.Translation));
        Assert.Equal(Lyrics.Prefs.HasRomanization, Lyrics.Prefs.BitFor(Lyrics.Prefs.Romanization));
        Assert.Equal(0, Lyrics.Prefs.BitFor(Lyrics.Prefs.None));
    }

    [Fact]
    public void The_cycle_skips_layers_the_document_does_not_have()
    {
        int both = Lyrics.Prefs.HasTranslation | Lyrics.Prefs.HasRomanization;
        Assert.Equal(Lyrics.Prefs.Translation, Lyrics.Prefs.Next(Lyrics.Prefs.None, both));
        Assert.Equal(Lyrics.Prefs.Romanization, Lyrics.Prefs.Next(Lyrics.Prefs.Translation, both));
        Assert.Equal(Lyrics.Prefs.None, Lyrics.Prefs.Next(Lyrics.Prefs.Romanization, both));

        // Romanization only: translation is skipped entirely.
        Assert.Equal(Lyrics.Prefs.Romanization, Lyrics.Prefs.Next(Lyrics.Prefs.None, Lyrics.Prefs.HasRomanization));
        Assert.Equal(Lyrics.Prefs.None, Lyrics.Prefs.Next(Lyrics.Prefs.Romanization, Lyrics.Prefs.HasRomanization));
    }

    [Fact]
    public void None_is_always_reachable_so_the_cycle_can_never_trap_the_user()
    {
        for (int available = 0; available <= 3; available++)
            for (int mode = 0; mode <= 2; mode++)
            {
                int m = mode;
                bool reachedNone = false;
                for (int step = 0; step < 4 && !reachedNone; step++)
                {
                    m = Lyrics.Prefs.Next(m, available);
                    if (m == Lyrics.Prefs.None) reachedNone = true;
                }
                Assert.True(reachedNone);
            }
    }

    [Fact]
    public void The_capability_bits_are_computed_at_commit_on_the_document()
    {
        var plain = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "a", []));
        Assert.Equal(0, plain.SecondaryAvailable);

        var both = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "a", [], null, "t", "r"));
        Assert.Equal(Lyrics.Prefs.HasTranslation | Lyrics.Prefs.HasRomanization, both.SecondaryAvailable);

        var onlyRoman = Lyr.Doc(Lyrics.SyncKind.Line, new Lyrics.Line(0, "a", [], null, null, "r"));
        Assert.Equal(Lyrics.Prefs.HasRomanization, onlyRoman.SecondaryAvailable);
    }
}
