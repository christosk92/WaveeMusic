// ── Wavee.Tests/ControlsTests.cs — the pure decisions inside the shared control family ────────────────────────────────
//
// Wave 4's gate for `Platform/Controls*.cs` (owner L). A control that needs the engine loop to exist cannot be unit
// tested, so — the house rule — every DECISION inside one is a pure function, and it is the function that is pinned here:
// the search-highlight wrap cap, the shelf/grid extent maths the renderer and the estimator must agree on, the face-pile
// geometry, the countdown breakdown, the rich-text parser, the selection-bar fit tier, the equalizer's motion policy,
// the accent CTA's pressed-ink alpha and the cover-url resolver. Ch 02 §8 marks five of these "none — add one"; they are
// added here.
//
// No window, no loop, no element is rendered. The rich-text facts touch the ROUTE SEAM, which is process state, so each
// of them restores it.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ControlsGeometryTests
{
    [Theory]
    [InlineData(14f, 20f)]
    [InlineData(12f, 16f)]
    [InlineData(20f, 29f)]     // off the ladder: ceil(size × 1.43)
    [InlineData(18f, 26f)]
    public void The_highlight_wrap_cap_follows_the_engine_type_ladder(float size, float lineBox)
        => Assert.Equal(lineBox, Controls.LineBoxFor(size));

    [Theory]
    [InlineData(148f)]
    [InlineData(173.3f)]
    [InlineData(188f)]
    public void The_shelf_extent_is_the_card_width_plus_its_label_block(float cardW)
    {
        // 6 gutter + 20 plate padding + the cover + 8 gap + 20 title + 2 + 32 subtitle. The renderer AND the estimator
        // call this; an estimate that disagrees re-pins the scroll anchor mid-scroll and the feed jumps under the cursor.
        Assert.Equal(cardW + 72f, Controls.ShelfHeight(cardW));
    }

    [Fact]
    public void A_whole_shelf_row_adds_its_header_and_its_gaps()
        => Assert.Equal(32f + Controls.ShelfHeight(160f) + 24f, Controls.ShelfExtent(160f));

    [Fact]
    public void A_grid_cell_grows_by_exactly_one_title_line_per_line()
    {
        float one = Controls.GridCardChromeFor(1, hasSubtitle: true);
        float two = Controls.GridCardChromeFor(2, hasSubtitle: true);
        Assert.Equal(Controls.GridTitleLineH, two - one);
        Assert.Equal(Controls.GridSubtitleBlockH,
                     Controls.GridCardChromeFor(1, true) - Controls.GridCardChromeFor(1, false));
    }

    [Fact]
    public void The_shelf_card_band_lives_in_the_token_layer()
    {
        Assert.Equal(148f, Design.Size.ShelfCardMin);
        Assert.Equal(188f, Design.Size.ShelfCardMax);
        Assert.True(Controls.ShelfDecodePx >= Design.Size.ShelfCardMax);   // a shelf cover never upsamples its decode
    }

    [Fact]
    public void The_chip_rail_extent_includes_its_semantic_gap()
        => Assert.Equal(Controls.ChipRailHeight + Spacing.S, Controls.ChipRailExtent);

    [Fact]
    public void The_dialog_width_ladder_is_three_rungs_inside_the_engine_clamp()
    {
        Assert.Equal(320f, Controls.DialogWidthCompact);
        Assert.Equal(480f, Controls.DialogWidthWide);
        Assert.Equal(548f, Controls.DialogWidthMax);
    }

    [Fact]
    public void The_overflow_button_rests_at_the_reported_middle()
    {
        // 0 was undiscoverable; 1 was a real scanning cost on a 1,500-row list.
        Assert.Equal(0.45f, Controls.MoreRestOpacity);
    }
}

public class ControlsFacePileTests
{
    [Fact]
    public void The_step_is_the_frame_minus_the_overlap()
    {
        Assert.Equal(Controls.FaceAvatar + 2f * Controls.FaceRing, Controls.FaceOuter);
        Assert.Equal(Controls.FaceOuter - Controls.FaceOverlap, Controls.FaceStep);
        Assert.Equal(32f, Controls.FaceOuter);
        Assert.Equal(20f, Controls.FaceStep);
    }

    [Theory]
    [InlineData(0f, 1)]      // unmeasured → 1, never 0
    [InlineData(31f, 1)]
    [InlineData(32f, 1)]
    [InlineData(52f, 2)]
    [InlineData(112f, 5)]
    public void Slots_fit_by_the_first_frame_plus_steps(float width, int expected)
        => Assert.Equal(expected, Controls.SlotsIn(width));

    [Fact]
    public void Nobody_to_show_shows_nothing()
        => Assert.Equal(0, Controls.VisibleFaces(200f, 0));

    [Fact]
    public void Everyone_fits_so_no_count_frame()
        => Assert.Equal(3, Controls.VisibleFaces(112f, 3));

    [Fact]
    public void When_anyone_would_clip_one_slot_becomes_the_count()
    {
        // Five slots, nine people: four portraits and a "+5" — the strip never overflows.
        Assert.Equal(4, Controls.VisibleFaces(112f, 9));
    }

    [Fact]
    public void A_single_slot_still_shows_one_portrait()
        => Assert.Equal(1, Controls.VisibleFaces(10f, 7));
}

public class ControlsCountdownTests
{
    [Fact]
    public void The_breakdown_is_per_unit_remainders_not_totals()
    {
        var (d, h, m, s) = Controls.Breakdown(new TimeSpan(3, 4, 5, 6));
        Assert.Equal((3, 4, 5, 6), (d, h, m, s));
    }

    [Fact]
    public void A_tick_past_the_instant_never_renders_a_negative_tile()
    {
        // The released gate flips on the NEXT render, not mid-frame, so one tick can land a hair past zero.
        Assert.Equal((0, 0, 0, 0), Controls.Breakdown(TimeSpan.FromMilliseconds(-400)));
    }

    [Fact]
    public void A_long_wait_keeps_its_days_unbounded()
        => Assert.Equal(143, Controls.Breakdown(TimeSpan.FromDays(143.5)).Days);
}

public class ControlsMotionPolicyTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]   // paused → flat, no tick
    [InlineData(true, true, false, false)]     // hover-paused under an invisible reveal → no tick
    [InlineData(true, false, true, false)]     // reduced motion → no loop, ever
    public void The_equalizer_ticks_only_while_playing_visible_and_allowed(bool playing, bool hoverPaused,
                                                                         bool reduced, bool ticks)
        => Assert.Equal(ticks, Controls.ShouldTick(playing, hoverPaused, reduced));

    [Fact]
    public void Reduced_motion_settles_to_a_still_PLAYING_shape_and_a_paused_track_stays_flat()
    {
        // A reduced-motion user can still tell which row is playing from a mid-list glance, without anything looping.
        Assert.True(Controls.ShouldShowStillShape(playing: true, reducedMotion: true));
        Assert.False(Controls.ShouldShowStillShape(playing: false, reducedMotion: true));
        Assert.False(Controls.ShouldShowStillShape(playing: true, reducedMotion: false));
    }

    [Theory]
    [InlineData(1200f, 0)]
    [InlineData(760f, 0)]
    [InlineData(759f, 1)]
    [InlineData(390f, 1)]
    [InlineData(389f, 2)]
    [InlineData(0f, 2)]
    public void The_selection_bar_fit_tier_follows_the_measured_lane(float width, int tier)
        => Assert.Equal(tier, Controls.SelectionFitFor(width));
}

public class ControlsCtaTests
{
    [Fact]
    public void The_pressed_label_alpha_is_keyed_off_the_inks_LUMINANCE()
    {
        // An artwork accent can invert the ink against the theme, and a caller may pass an explicit ink that is not the
        // palette's own near-black — so neither a theme read nor token equality is correct here.
        Assert.Equal(0x80 / 255f, Controls.OnFillSecondaryAlpha(ColorF.FromRgba(0, 0, 0)), 4);
        Assert.Equal(0x80 / 255f, Controls.OnFillSecondaryAlpha(ColorF.FromRgba(20, 20, 24)), 4);
        Assert.Equal(0xB3 / 255f, Controls.OnFillSecondaryAlpha(ColorF.FromRgba(255, 255, 255)), 4);
    }
}

[Collection(EntitiesCollection.Name)]
public class ControlsArtUrlTests
{
    [Fact]
    public void An_empty_image_has_no_url()
        => Assert.Null(Controls.ArtUrl(default));

    [Fact]
    public void A_bare_file_id_becomes_a_cdn_url()
        => Assert.Equal("https://i.scdn.co/image/ab67616d0000b273deadbeefdeadbeefdeadbeef",
            Controls.ArtUrl(Entities.Strings.Intern("ab67616d0000b273deadbeefdeadbeefdeadbeef")));

    [Theory]
    [InlineData("https://i.scdn.co/image/ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("file:///C:/Music/cover.jpg")]
    [InlineData("C:\\Music\\cover.jpg")]
    public void A_formed_url_or_a_local_path_passes_through(string value)
        => Assert.Equal(value, Controls.ArtUrl(Entities.Strings.Intern(value)));
}

[Collection(EntitiesCollection.Name)]
public class ControlsRichTextTests
{
    static readonly ColorF Link = ColorF.FromRgba(0, 120, 212);

    [Fact]
    public void Plain_text_is_one_span()
    {
        var spans = Controls.ParseRich("Just words.", Link, onNavRoute: null);
        Assert.Single(spans);
        Assert.Equal("Just words.", spans[0].Text);
    }

    [Fact]
    public void Bold_marks_its_run_and_releases_after_it()
    {
        var spans = Controls.ParseRich("a <b>bold</b> word", Link, null);
        Assert.Equal(3, spans.Count);
        Assert.Equal(700, spans[1].Weight);
        Assert.Equal(0, spans[2].Weight);
    }

    [Fact]
    public void Entities_are_decoded_and_unknown_ones_left_literal()
    {
        var spans = Controls.ParseRich("Rock &amp; roll &#39;n&#39; &bogus; x&lt;y", Link, null);
        Assert.Equal("Rock & roll 'n' &bogus; x<y", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void An_unknown_tag_is_dropped_and_its_text_is_kept()
    {
        var spans = Controls.ParseRich("one<br/>two <i>three</i>", Link, null);
        Assert.Equal("onetwo three", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void A_stray_angle_bracket_stays_text()
        => Assert.Equal("3 < 4", string.Concat(Controls.ParseRich("3 < 4", Link, null).Select(s => s.Text)));

    [Fact]
    public void An_anchor_with_no_route_seam_is_styled_but_inert()
    {
        // A link to a route nothing renders is worse than a link that does not click.
        var saved = Controls.RouteForUri;
        try
        {
            Controls.RouteForUri = null;
            var spans = Controls.ParseRich("by <a href=\"spotify:artist:1\">Björk</a>", Link, onNavRoute: _ => { });
            var anchor = spans.Single(s => s.Text == "Björk");
            Assert.Equal(Link, anchor.Color);
            Assert.Null(anchor.OnClick);
        }
        finally { Controls.RouteForUri = saved; }
    }

    [Fact]
    public void An_anchor_the_seam_can_route_navigates_to_the_route_key()
    {
        var saved = Controls.RouteForUri;
        try
        {
            Controls.RouteForUri = uri => uri.StartsWith("spotify:artist:", StringComparison.Ordinal) ? "artist:" + uri : null;
            string? went = null;
            var spans = Controls.ParseRich("by <a href='spotify:artist:1'>Björk</a>", Link, key => went = key);
            var anchor = spans.Single(s => s.Text == "Björk");
            Assert.NotNull(anchor.OnClick);
            anchor.OnClick!();
            Assert.Equal("artist:spotify:artist:1", went);
        }
        finally { Controls.RouteForUri = saved; }
    }
}
