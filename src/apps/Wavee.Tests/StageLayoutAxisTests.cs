// ── Wavee.Tests/StageLayoutAxisTests.cs — the visualizer LAYOUT axis: Stage.LayoutRules, Stage.HeroRules and the per-look geometry ──
//
// Pins the pure rules of Shell/Stage.Layouts.cs (Role CORE, System-only): which layout is drawn, which spectrum, which pane and
// caption, what the lease needs, the artist hero's header pick / scrim / decode arithmetic, and the allocator's per-look
// geometry — the boards' (Main / Centered / Hero / HeroLyrics .dc.html) 1440×900 numbers as fractions, with the invariants that
// keep every rect inside the stage, the strip clear of the transport and the ring box inside its block.

using Wavee;
using Xunit;

using Layout = Wavee.Stage.Layout;
using Look = Wavee.Stage.Look;
using M = Wavee.Stage.Mode;
using Rules = Wavee.Stage.LayoutRules;
using Spec = Wavee.Stage.SpectrumStyle;
using VL = Wavee.Stage.VizLayout;

namespace Wavee.Tests;

public class StageLayoutAxisTests
{
    // ── the persisted ints ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 1)] [InlineData(2, 2)] [InlineData(3, 3)]
    [InlineData(4, 0)] [InlineData(-1, 0)] [InlineData(999, 0)]
    public void A_stored_layout_is_coerced_to_a_real_one(int stored, int expected) => Assert.Equal(expected, Rules.Coerce(stored));

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 1)] [InlineData(2, 2)] [InlineData(3, 3)]
    [InlineData(4, 0)] [InlineData(-7, 0)]
    public void A_stored_spectrum_is_coerced_to_a_real_one(int stored, int expected) => Assert.Equal(expected, Rules.CoerceSpectrum(stored));

    [Fact]
    public void The_dimming_is_clamped_to_zero_and_a_hundred()
    {
        Assert.Equal(0, Rules.ClampDim(-5));
        Assert.Equal(100, Rules.ClampDim(400));
        Assert.Equal(75, Rules.ClampDim(75));
    }

    // ── what is drawn ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_always_draws_Card_and_an_artist_without_a_header_draws_Large_art()
    {
        foreach (var stored in new[] { VL.Card, VL.LargeArt, VL.Centered, VL.Artist })
            Assert.Equal(VL.Card, Rules.Effective(stored, Stage.Aspect.Compact, Stage.HeroState.Header));
        Assert.Equal(VL.LargeArt, Rules.Effective(VL.Artist, Stage.Aspect.Desktop, Stage.HeroState.None));
        Assert.Equal(VL.Artist, Rules.Effective(VL.Artist, Stage.Aspect.Desktop, Stage.HeroState.Header));
        // Pending (the overview is in flight) STAYS Artist: a first-time artist never flips Large art → Artist a second later
        Assert.Equal(VL.Artist, Rules.Effective(VL.Artist, Stage.Aspect.Desktop, Stage.HeroState.Pending));
        Assert.Equal(VL.Centered, Rules.Effective(VL.Centered, Stage.Aspect.Portrait, Stage.HeroState.None));
    }

    [Fact]
    public void The_spectrum_is_none_for_Card_and_Ring_becomes_Line_under_the_Artist_hero()
    {
        foreach (var s in new[] { Spec.Bars, Spec.Ring, Spec.Line, Spec.Off }) Assert.Equal(Spec.Off, Rules.Spectrum(VL.Card, s));
        Assert.Equal(Spec.Ring, Rules.Spectrum(VL.Centered, Spec.Ring));
        Assert.Equal(Spec.Ring, Rules.Spectrum(VL.LargeArt, Spec.Ring));
        Assert.Equal(Spec.Line, Rules.Spectrum(VL.Artist, Spec.Ring));
        Assert.Equal(Spec.Bars, Rules.Spectrum(VL.Artist, Spec.Bars));
        Assert.Equal(Spec.Off, Rules.Spectrum(VL.Artist, Spec.Off));
    }

    [Fact]
    public void Large_art_and_centered_run_the_face_behind_the_cover_and_artist_alone_replaces_it()
    {
        Assert.True(Rules.ShowsFace(VL.Card)); Assert.True(Rules.ShowsFace(VL.LargeArt)); Assert.True(Rules.ShowsFace(VL.Centered));
        Assert.False(Rules.ShowsFace(VL.Artist));
        // text faces fall back to Bloom behind a layout (no duplicate lyrics); Card and Artist never substitute
        Assert.Equal(Visualizer.Kind.Bloom, Rules.BackdropKind(VL.LargeArt, Visualizer.Kind.Verse));
        Assert.Equal(Visualizer.Kind.Bloom, Rules.BackdropKind(VL.Centered, Visualizer.Kind.Type));
        Assert.Equal(Visualizer.Kind.Verse, Rules.BackdropKind(VL.Card, Visualizer.Kind.Verse));
        Assert.Equal(Visualizer.Kind.Ring, Rules.BackdropKind(VL.Centered, Visualizer.Kind.Ring));
        // cover-carrying faces sit under the deeper scrim; Card has none
        Assert.True(Rules.BackdropScrim(VL.LargeArt, Visualizer.Kind.Mosaic) > Rules.BackdropScrim(VL.LargeArt, Visualizer.Kind.Ring));
        Assert.Equal(0f, Rules.BackdropScrim(VL.Card, Visualizer.Kind.Mosaic));
        Assert.True(Rules.KeysStepFaces(VL.Centered)); Assert.False(Rules.KeysStepFaces(VL.Artist));
    }

    [Fact]
    public void A_layout_with_no_spectrum_still_holds_the_level_the_backdrop_field_breathes_with()
    {
        var kind = Visualizer.Kind.Bloom;
        Assert.Equal(Visualizer.Catalog.NeedsOf(kind), Rules.NeedsOf(VL.Card, Spec.Bars, kind));
        Assert.Equal(Visualizer.Need.Level | Visualizer.Need.Spectrum, Rules.NeedsOf(VL.LargeArt, Spec.Bars, kind));   // the backdrop face's level + the strip
        Assert.Equal(Visualizer.Need.Spectrum, Rules.NeedsOf(VL.Artist, Spec.Ring, kind));   // Ring draws as Line under the hero
        Assert.Equal(Visualizer.Need.Level, Rules.NeedsOf(VL.Centered, Spec.Off, kind));
        Assert.Equal(Visualizer.Tier.Level, Visualizer.Catalog.TierOf(Rules.NeedsOf(VL.LargeArt, Spec.Off, kind)));
    }

    [Fact]
    public void The_pane_is_the_modes_pane_and_in_Visualizer_mode_the_lyrics_for_Large_art_and_the_hero_with_lyrics()
    {
        Assert.Equal(Stage.PaneKind.Lyrics, Rules.Pane(M.Lyrics, VL.Artist, true));
        Assert.Equal(Stage.PaneKind.Queue, Rules.Pane(M.Queue, VL.LargeArt, false));
        Assert.Equal(Stage.PaneKind.Artist, Rules.Pane(M.Artist, VL.Centered, false));
        Assert.Equal(Stage.PaneKind.None, Rules.Pane(M.Visualizer, VL.Card, false));
        Assert.Equal(Stage.PaneKind.Lyrics, Rules.Pane(M.Visualizer, VL.LargeArt, false));
        Assert.Equal(Stage.PaneKind.None, Rules.Pane(M.Visualizer, VL.Centered, false));
        Assert.Equal(Stage.PaneKind.None, Rules.Pane(M.Visualizer, VL.Artist, false));
        Assert.Equal(Stage.PaneKind.Lyrics, Rules.Pane(M.Visualizer, VL.Artist, true));
    }

    [Fact]
    public void The_caption_follows_the_Card_rule_and_shows_one_line_under_Centered_and_never_under_the_other_layouts()
    {
        var kind = Visualizer.Kind.Bars;   // caption-friendly
        // Card: exactly ModeRules.ShowsCaption
        foreach (var mode in new[] { M.Lyrics, M.Visualizer, M.Queue, M.Artist })
            foreach (bool overlay in new[] { false, true })
                foreach (bool timed in new[] { false, true })
                    foreach (bool pane in new[] { false, true })
                        Assert.Equal(Stage.ModeRules.ShowsCaption(mode, overlay, timed, pane, kind), Rules.ShowsCaption(mode, VL.Card, overlay, timed, pane, kind));
        Assert.True(Rules.ShowsCaption(M.Visualizer, VL.Centered, true, true, true, kind));
        Assert.False(Rules.ShowsCaption(M.Visualizer, VL.Centered, false, true, true, kind));
        Assert.False(Rules.ShowsCaption(M.Visualizer, VL.Centered, true, false, true, kind));
        Assert.False(Rules.ShowsCaption(M.Visualizer, VL.LargeArt, true, true, true, kind));
        Assert.False(Rules.ShowsCaption(M.Visualizer, VL.Artist, true, true, true, kind));
        // outside Visualizer mode a layout changes nothing: Compact Lyrics mode still carries the caption when no pane shows
        Assert.True(Rules.ShowsCaption(M.Lyrics, VL.Artist, false, true, false, kind));
        Assert.False(Rules.CaptionContext(VL.Centered, true));
        Assert.True(Rules.CaptionContext(VL.Card, true));
        Assert.False(Rules.CaptionContext(VL.Card, false));
    }

    [Fact]
    public void Outside_Visualizer_mode_every_layout_overload_answers_as_the_mode_does()
    {
        var l = Layout.Seed(1920f, 1080f);
        foreach (var mode in new[] { M.Lyrics, M.Queue, M.Artist })
        {
            var k = Rules.LookOf(mode, VL.Artist, Spec.Bars, heroLyricsPref: true, caption: false);
            Assert.Equal(VL.Card, k.Eff);
            Assert.False(k.HeroLyrics);
            Assert.Equal(l.CoverSize(mode), l.CoverSize(in k));
            Assert.Equal(l.CoverX(mode), l.CoverX(in k));
            Assert.Equal(l.CoverY(mode), l.CoverY(in k));
            Assert.Equal(l.TitleX(mode), l.TitleX(in k));
            Assert.Equal(l.TitleY(mode), l.TitleY(in k));
            Assert.Equal(l.TitleW(mode), l.TitleW(in k));
            Assert.Equal(l.TitleMaxLines(mode), l.TitleMaxLines(in k));
            Assert.Equal((l.PaneX, l.PaneTop, l.PaneW, l.PaneH), l.PaneRect(in k));
            Assert.Equal(0f, l.StripH(in k));
            Assert.Equal(Stage.Tone.ScrimFor(mode, Visualizer.Kind.Bloom), Rules.ScrimFor(mode, VL.Artist, Visualizer.Kind.Bloom));
            Assert.Equal(Visualizer.Field.BaseOpacity(0.3f, false), Rules.BaseFieldFor(0.3f, mode, VL.LargeArt));
        }
        // Visualizer mode in Card is the same plain look; under a layout the scrim and the field take the Lyrics-mode depth
        Assert.Equal(Stage.Tone.ScrimFor(M.Visualizer, Visualizer.Kind.Bloom), Rules.ScrimFor(M.Visualizer, VL.Card, Visualizer.Kind.Bloom));
        Assert.Equal(Stage.Tone.ScrimA, Rules.ScrimFor(M.Visualizer, VL.Centered, Visualizer.Kind.Bloom));
        Assert.Equal(Visualizer.Field.BaseOpacity(0.3f, true), Rules.BaseFieldFor(0.3f, M.Visualizer, VL.Card));
        Assert.Equal(Visualizer.Field.BaseOpacity(0.3f, false), Rules.BaseFieldFor(0.3f, M.Visualizer, VL.Artist));
    }

    [Fact]
    public void The_bracket_keys_step_the_stored_layout_through_the_three_non_Card_layouts_only()
    {
        Assert.Equal(VL.Centered, Rules.Step(VL.LargeArt, 1));
        Assert.Equal(VL.Artist, Rules.Step(VL.Centered, 1));
        Assert.Equal(VL.LargeArt, Rules.Step(VL.Artist, 1));    // wraps within the three — never into Card
        Assert.Equal(VL.Artist, Rules.Step(VL.LargeArt, -1));
        Assert.Equal(VL.LargeArt, Rules.Step(VL.Centered, -1));
        for (int i = 0; i < 12; i++)
            Assert.NotEqual(VL.Card, Rules.Step((VL)(1 + i % 3), i % 2 == 0 ? 1 : -1));
    }

    [Fact]
    public void The_layout_grid_cursor_moves_within_its_two_by_two()
    {
        Assert.Equal(1, Rules.CardNav(0, 39));   // → from Card to Large art
        Assert.Equal(0, Rules.CardNav(1, 37));
        Assert.Equal(2, Rules.CardNav(0, 40));   // ↓ from Card to Centered
        Assert.Equal(0, Rules.CardNav(2, 38));
        Assert.Equal(3, Rules.CardNav(1, 40));
        Assert.Equal(2, Rules.CardNav(3, 37));
        Assert.Equal(1, Rules.CardNav(1, 13));   // any other key leaves it
    }

    [Fact]
    public void The_lease_for_explicit_needs_matches_the_one_for_a_face_and_stops_under_reduced_motion()
    {
        var bars = Visualizer.Kind.Bars;
        Assert.Equal(Visualizer.Demand.For(bars, true, false, true, true, true, true, false, false),
                     Visualizer.Demand.For(Visualizer.Catalog.NeedsOf(bars), true, false, true, true, true, true, false, false));
        Assert.Equal(Visualizer.Tier.Spectrum, Visualizer.Demand.For(Visualizer.Need.Spectrum, true, false, true, true, true, true, false, false));
        Assert.Equal(Visualizer.Tier.Level, Visualizer.Demand.For(Visualizer.Need.Level, true, false, true, true, true, true, false, false));
        // reduced motion freezes every strip, like every face
        Assert.Equal(Visualizer.Tier.None, Visualizer.Demand.For(Visualizer.Need.Spectrum, true, false, true, true, true, true, false, true));
        Assert.Equal(Visualizer.Tier.None, Visualizer.Demand.For(Visualizer.Need.Spectrum, true, false, true, false, true, true, false, false));   // paused
    }

    // ── the artist hero ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_billed_artist_means_no_header_and_a_local_file_never_waits_forever()
        => Assert.Equal((Stage.HeroState.None, -1), Stage.HeroRules.Pick(default, default));

    [Fact]
    public void The_header_walk_follows_the_billing_order()
    {
        // #1 known with a header wins
        Assert.Equal((Stage.HeroState.Header, 0), Stage.HeroRules.Pick([true, true], [true, true]));
        // #1 still unknown while #2 has a header: wait (the photo and the name never switch from #2 to #1 when #1 lands)
        Assert.Equal((Stage.HeroState.Pending, -1), Stage.HeroRules.Pick([false, true], [false, true]));
        // #1 known with NO header, #2 has one: the second artist
        Assert.Equal((Stage.HeroState.Header, 1), Stage.HeroRules.Pick([true, true], [false, true]));
        // everyone known, nobody has one
        Assert.Equal((Stage.HeroState.None, -1), Stage.HeroRules.Pick([true, true, true], [false, false, false]));
        // a known-empty first and an unknown second still waits
        Assert.Equal((Stage.HeroState.Pending, -1), Stage.HeroRules.Pick([true, false], [false, false]));
    }

    [Fact]
    public void The_foot_alpha_runs_from_forty_to_a_hundred_and_the_default_dimming_is_the_prototypes_085()
    {
        Assert.Equal(0.40f, Stage.HeroRules.FootAlpha(0), 4);
        Assert.Equal(1.00f, Stage.HeroRules.FootAlpha(100), 4);
        Assert.Equal(0.85f, Stage.HeroRules.FootAlpha(Stage.HeroRules.DefaultDim), 4);
        Assert.Equal(1.00f, Stage.HeroRules.FootAlpha(500), 4);   // clamped
    }

    [Fact]
    public void The_hero_decode_fills_a_non_16_by_9_stage_and_never_asks_for_more_than_the_source()
    {
        // a 16:10 stage (1440×900 DIP, 1×): the cover-fit needs the 16:9 source 1600 wide, ×1.10 for the zoom
        Assert.Equal(1760, Stage.HeroRules.DecodePx(1440f, 900f, 1f));
        // a 16:9 stage: its own width ×1.10
        Assert.Equal(1056, Stage.HeroRules.DecodePx(960f, 540f, 1f));
        // never past the largest source, never under the floor
        Assert.Equal(Stage.HeroRules.DecodeMax, Stage.HeroRules.DecodePx(3440f, 1440f, 1.5f));
        Assert.Equal(320, Stage.HeroRules.DecodePx(100f, 50f, 1f));
        // a scale below 1 is the 1× decode
        Assert.Equal(Stage.HeroRules.DecodePx(1440f, 900f, 1f), Stage.HeroRules.DecodePx(1440f, 900f, 0.5f));
    }

    [Fact]
    public void The_pan_alternates_by_slot_and_stays_inside_its_zoom_range()
    {
        var a = Stage.HeroRules.Pan(0); var b = Stage.HeroRules.Pan(1);
        Assert.Equal(1.03f, a.ScaleFrom); Assert.Equal(1.10f, a.ScaleTo);
        Assert.True(a.Dx > 0f && b.Dx < 0f);
        for (int s = 0; s < 64; s++) Assert.InRange(MathF.Abs(Stage.HeroRules.Pan(s).Dy), 0f, 0.0101f);
    }

    [Fact]
    public void The_next_tracks_photo_is_decoded_under_the_hero_in_the_last_ten_seconds()
    {
        Assert.True(Stage.HeroRules.PrefetchDue(190_000, 200_000));
        Assert.False(Stage.HeroRules.PrefetchDue(100_000, 200_000));
        Assert.False(Stage.HeroRules.PrefetchDue(0, 0));
    }

    // ── the geometry ────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly (float W, float H)[] Sizes = [(1920f, 1080f), (1440f, 900f), (2560f, 1440f), (3440f, 1440f), (900f, 1600f)];

    static IEnumerable<(Layout L, Look K)> Looks()
    {
        foreach (var (w, h) in Sizes)
        {
            var l = Layout.Seed(w, h);
            foreach (var eff in new[] { VL.LargeArt, VL.Centered, VL.Artist })
                foreach (var spec in new[] { Spec.Bars, Spec.Ring, Spec.Line, Spec.Off })
                    foreach (bool lyrics in new[] { false, true })
                        foreach (bool caption in new[] { false, true })
                            yield return (l, Rules.LookOf(M.Visualizer, eff, spec, lyrics, caption && eff == VL.Centered));
        }
    }

    [Fact]
    public void The_strip_sits_between_the_top_bar_and_the_transport_and_never_overlaps_it()
    {
        foreach (var (l, k) in Looks())
        {
            var (x, y, w, h) = l.StripRect(in k);
            if (h == 0f) { Assert.True(k.Spectrum is Spec.Off or Spec.Ring, k.ToString()); continue; }
            Assert.True(x >= 0f && x + w <= l.W + 0.01f, $"{k} x within the stage at {l.W}x{l.H}");
            Assert.True(y >= Layout.TopBarH, $"{k} below the top bar at {l.W}x{l.H}");
            Assert.True(y + h <= l.TransportTop + 0.01f, $"{k} above the transport at {l.W}x{l.H}");
            Assert.Equal(l.StripTop(in k), y, 3);
        }
    }

    [Fact]
    public void The_bars_strip_is_the_Main_boards_120_and_the_line_strips_are_the_hero_boards_56_and_36()
    {
        var l = Layout.Seed(1440f, 900f);
        Assert.InRange(l.StripH(Rules.LookOf(M.Visualizer, VL.LargeArt, Spec.Bars, false, false)), 112f, 124f);
        Assert.InRange(l.StripH(Rules.LookOf(M.Visualizer, VL.Artist, Spec.Line, false, false)), 48f, 60f);
        Assert.InRange(l.StripH(Rules.LookOf(M.Visualizer, VL.Artist, Spec.Line, true, false)), 32f, 40f);
        Assert.Equal(0f, l.StripH(Rules.LookOf(M.Visualizer, VL.LargeArt, Spec.Off, false, false)));
        Assert.Equal(0f, l.StripH(Rules.LookOf(M.Visualizer, VL.Centered, Spec.Ring, false, false)));
        Assert.Equal(0f, l.StripH(Look.Plain(M.Visualizer)));
    }

    [Fact]
    public void Large_art_is_the_boards_34_percent_cover_capped_at_440_with_the_lyrics_column_to_its_right()
    {
        var l = Layout.Seed(1440f, 900f);
        var k = Rules.LookOf(M.Visualizer, VL.LargeArt, Spec.Bars, false, false);
        Assert.InRange(l.CoverSize(in k), 420f, 440f);   // 34 % of 1440 is 490, capped at 440, then fitted above the strip
        Assert.Equal(l.PadXFor(in k), l.CoverX(in k));
        var pane = l.PaneRect(in k);
        Assert.True(pane.X >= l.CoverX(in k) + l.CoverSize(in k), "the column starts right of the cover");
        Assert.True(pane.X + pane.W <= l.W + 0.01f);
        Assert.True(pane.Y >= Layout.TopBarH && pane.Y + pane.H <= l.StripTop(in k) + 0.01f);
        Assert.Equal((34f, 40f), l.TitleFont(in k));
        Assert.Equal((17f, 24f), l.MetaFont(in k));
        // the cap scales with the stage's height past the board, so a 1440-tall stage is not mostly empty
        var tall = Layout.Seed(2560f, 1440f);
        Assert.True(tall.CoverSize(Rules.LookOf(M.Visualizer, VL.LargeArt, Spec.Bars, false, false)) > 440f);
    }

    [Fact]
    public void The_centered_ring_is_the_boards_1_6875_box_with_its_radius_0_725_of_the_cover()
    {
        Assert.Equal(1.6875f, Layout.RingBoxRatio);
        var l = Layout.Seed(1920f, 1080f);
        var k = Rules.LookOf(M.Visualizer, VL.Centered, Spec.Ring, false, true);
        var box = l.RingBox(in k);
        float cover = l.CoverSize(in k);
        Assert.Equal(cover * 1.6875f, box.Size, 2);
        Assert.Equal((l.W - box.Size) * 0.5f, box.X, 2);
        Assert.Equal((l.W - cover) * 0.5f, l.CoverX(in k), 2);
        Assert.True(box.Y >= Layout.TopBarH && box.Y + box.Size <= l.ContentBottom(in k) + 0.01f, "the ring box fits its block");
        var (radius, width, len0, gain) = Layout.RingMetrics(320f);
        Assert.Equal(232f, radius, 3);
        Assert.Equal(4f, width, 3);
        Assert.Equal(6.08f, len0, 2);
        Assert.Equal(38.4f, gain, 2);
    }

    [Fact]
    public void Every_look_keeps_its_cover_and_ring_box_inside_the_stage()
    {
        foreach (var (l, k) in Looks())
        {
            float cover = l.CoverSize(in k), cx = l.CoverX(in k), cy = l.CoverY(in k);
            Assert.True(cover > 0f, k.ToString());
            Assert.True(cx >= 0f && cx + cover <= l.W + 0.01f, $"{k} cover x at {l.W}x{l.H}");
            Assert.True(cy >= Layout.TopBarH - 0.01f, $"{k} cover y at {l.W}x{l.H}");
            Assert.True(cy + cover <= l.TransportTop + 0.01f, $"{k} cover above the transport at {l.W}x{l.H}");
            var box = l.RingBox(in k);
            if (box.Size > 0f)
            {
                Assert.True(k.Eff is VL.LargeArt or VL.Centered && k.Spectrum == Spec.Ring, k.ToString());
                Assert.True(box.X >= -0.01f && box.X + box.Size <= l.W + 0.01f, $"{k} ring box x at {l.W}x{l.H}");
                Assert.True(box.Y >= Layout.TopBarH - 0.01f && box.Y + box.Size <= l.ContentBottom(in k) + 0.01f, $"{k} ring box y at {l.W}x{l.H}");
            }
        }
    }

    [Fact]
    public void The_artist_name_stays_inside_its_clamp_for_any_stage_height_and_carries_the_boards_112()
    {
        for (float h = 400f; h <= 2400f; h += 25f)
        {
            var l = Layout.Seed(MathF.Max(1000f, h * 1.6f), h);
            Assert.InRange(l.HeroNameSize, Layout.HeroNameMin, Layout.HeroNameMax);
        }
        var board = Layout.Seed(1440f, 900f);
        Assert.InRange(board.HeroNameSize, 104f, 112f);
        Assert.Equal(0.7f * 1440f, board.HeroNameMaxW, 2);
        var k = Rules.LookOf(M.Visualizer, VL.Artist, Spec.Line, false, false);
        Assert.Equal(64f, board.CoverSize(in k));
        Assert.Equal(64f, board.PadXFor(in k));
        Assert.Equal((22f, 28f), board.TitleFont(in k));
        // the name block grows upward from the identity row: its bottom edge is 18 above the cover
        Assert.Equal(board.H - (board.CoverY(in k) - 18f), board.HeroNameBottomOffset(in k), 2);
    }

    [Fact]
    public void The_hero_with_lyrics_is_the_HeroLyrics_board_a_48_cover_over_a_54_percent_pane()
    {
        var l = Layout.Seed(1440f, 900f);
        var k = Rules.LookOf(M.Visualizer, VL.Artist, Spec.Line, heroLyricsPref: true, caption: false);
        Assert.True(k.HeroLyrics);
        Assert.Equal(48f, l.CoverSize(in k));
        Assert.Equal(Layout.TopBarH + 16f, l.CoverY(in k));
        var pane = l.PaneRect(in k);
        Assert.Equal(l.PadXFor(in k), pane.X);
        Assert.Equal(0.54f * 1440f - 2f * l.PadXFor(in k), pane.W, 2);
        Assert.Equal(Layout.TopBarH + 16f + 48f + 18f, pane.Y, 2);
        Assert.True(pane.Y + pane.H <= l.StripTop(in k) + 0.01f);
        Assert.Equal((17f, 22f), l.TitleFont(in k));
        // the pane type scale follows the pane the look gives it, not the Lyrics-mode pane
        Assert.True(l.LyricsTypeScaleFor(in k) >= 1f);
        Assert.Equal(l.LyricsTypeScale, l.LyricsTypeScaleFor(Look.Plain(M.Lyrics)));
    }

    [Fact]
    public void The_centered_caption_sits_under_the_meta_line_and_Card_keeps_the_allocators_caption()
    {
        var l = Layout.Seed(1920f, 1080f);
        var centered = Rules.LookOf(M.Visualizer, VL.Centered, Spec.Ring, false, true);
        float w = l.CaptionWFor(in centered, galleryShown: false);
        Assert.True(w <= Layout.CenteredCaptionMaxW + 0.01f);
        Assert.Equal((l.W - w) * 0.5f, l.CaptionXFor(in centered, false), 2);
        Assert.False(l.CaptionContextFor(in centered));
        Assert.True(l.CaptionBottomFor(in centered) >= l.H - l.TransportTop - 0.01f, "the line is above the transport");
        var card = Look.Plain(M.Visualizer, caption: true);
        Assert.Equal(l.CaptionW(M.Visualizer, true), l.CaptionWFor(in card, true));
        Assert.Equal(l.CaptionX(M.Visualizer, true), l.CaptionXFor(in card, true));
        Assert.Equal(l.CaptionBottom, l.CaptionBottomFor(in card));
        Assert.Equal(l.CaptionShowsContext, l.CaptionContextFor(in card));
    }

    [Fact]
    public void Portrait_Large_art_centres_the_cover_over_a_full_width_column()
    {
        var l = Layout.Seed(900f, 1600f);
        Assert.Equal(Stage.Aspect.Portrait, l.Aspect);
        var k = Rules.LookOf(M.Visualizer, VL.LargeArt, Spec.Bars, false, false);
        Assert.Equal((l.W - l.CoverSize(in k)) * 0.5f, l.CoverX(in k), 2);
        Assert.True(l.TitlesCentered(in k));
        var pane = l.PaneRect(in k);
        Assert.True(pane.Y >= l.CoverY(in k) + l.CoverSize(in k));
        Assert.Equal(l.W - 2f * l.PadXFor(in k), pane.W, 2);
    }
}
