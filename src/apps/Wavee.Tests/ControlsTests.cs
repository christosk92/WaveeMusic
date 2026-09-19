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

    [Fact]
    public void The_same_id_resolves_to_the_same_url_instance()
    {
        // The concat arm is called per cover per render; a repeated resolve must hand back the cached instance rather
        // than a fresh concatenation (the shelf's 13 cards × every parent render used to allocate one each).
        var id = Entities.Strings.Intern("ab67616d0000b273cafecafecafecafecafecafe");
        var first = Controls.ArtUrl(id);
        Assert.Equal("https://i.scdn.co/image/ab67616d0000b273cafecafecafecafecafecafe", first);
        Assert.Same(first, Controls.ArtUrl(id));
        Assert.Same(first, Controls.ArtUrl(id));
    }

    [Fact]
    public void Different_ids_resolve_to_their_own_urls()
    {
        var a = Entities.Strings.Intern("ab67616d0000b273000000000000000000000001");
        var b = Entities.Strings.Intern("ab67616d0000b273000000000000000000000002");
        Assert.Equal("https://i.scdn.co/image/ab67616d0000b273000000000000000000000001", Controls.ArtUrl(a));
        Assert.Equal("https://i.scdn.co/image/ab67616d0000b273000000000000000000000002", Controls.ArtUrl(b));
        Assert.Same(Controls.ArtUrl(a), Controls.ArtUrl(a));
    }
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
        Assert.Equal("one\ntwo three", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void Description_blocks_preserve_reading_boundaries()
    {
        var spans = Controls.ParseRich("<p>First paragraph.</p><p>Second<br>line.</p><ul><li>One</li><li>Two</li></ul>", Link, null);
        Assert.Equal("First paragraph.\n\nSecond\nline.\n\n\u2022 One\n\u2022 Two\n\n\n", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void Web_description_links_have_an_action_but_unknown_schemes_do_not()
    {
        var safe = Assert.Single(Controls.ParseRich("<a href='https://example.test/guide'>Guide</a>", Link, null));
        Assert.NotNull(safe.OnClick);
        var unsupported = Assert.Single(Controls.ParseRich("<a href='javascript:alert(1)'>Guide</a>", Link, null));
        Assert.Null(unsupported.OnClick);
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

/// <summary>The card family's props gate on DATA. An entity adapter rebuilds its closures (and its subtitle element) on
/// every parent render; if those counted by identity, every ShelfCard / NowPlayingOverlay host on the page re-rendered
/// on every parent render (13× a frame on the artist page). A delegate counts by PRESENCE, never identity.</summary>
public class ControlsCardEqualityTests
{
    // Each call returns FRESH delegates and a FRESH subtitle element with the same data — the shape a re-rendering
    // adapter hands over.
    static Controls.CardData Card(string uri = "spotify:album:1", string title = "Blue", string? subtitle = "Joni Mitchell · 1971",
                                  bool play = true, string? dragKind = "album", bool circular = false, int titleLines = 1)
        => new(uri, title, subtitle is null ? null : new TextEl(subtitle) { Size = 12f, MaxLines = 1 }, "https://i.scdn.co/image/x",
               OnClick: () => { }, OnPlay: play ? () => { } : null, Circular: circular,
               Drag: dragKind is null ? null : new DragSource(dragKind, () => null), TitleLines: titleLines);

    [Fact]
    public void A_card_rebuilt_with_fresh_closures_and_an_identical_subtitle_is_equal()
    {
        var a = Card();
        var b = Card();
        // The compiler caches a non-capturing lambda as one static delegate, so OnClick may legitimately be the same
        // instance; the subtitle element is built fresh per call and proves the "rebuilt adapter" shape.
        Assert.NotSame(a.Subtitle, b.Subtitle);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void A_card_whose_data_changed_is_not_equal()
    {
        var a = Card();
        Assert.NotEqual(a, Card(uri: "spotify:album:2"));
        Assert.NotEqual(a, Card(title: "Clouds"));
        Assert.NotEqual(a, Card(subtitle: "Joni Mitchell · 1969"));
        Assert.NotEqual(a, Card(subtitle: null));
        Assert.NotEqual(a, Card(circular: true));
        Assert.NotEqual(a, Card(titleLines: 2));
    }

    [Fact]
    public void A_delegate_counts_by_presence_not_identity()
    {
        Assert.Equal(Card(play: true), Card(play: true));
        Assert.NotEqual(Card(play: true), Card(play: false));
        Assert.Equal(Card(dragKind: "album"), Card(dragKind: "album"));   // fresh payload factories, same kind
        Assert.NotEqual(Card(dragKind: "album"), Card(dragKind: "playlist"));
        Assert.NotEqual(Card(dragKind: "album"), Card(dragKind: null));
    }

    [Fact]
    public void The_shell_controls_count_by_value_and_the_thunks_by_presence()
    {
        // NaN is the "unpinned" default and must compare equal to itself, or every re-push of a plain card would differ.
        Assert.Equal(Card(), Card() with { Height = float.NaN });
        Assert.NotEqual(Card(), Card() with { Height = 230f });
        Assert.NotEqual(Card(), Card() with { Selected = true });
        Assert.Equal(Card() with { Selected = true, SelectedAccent = () => default },
                     Card() with { Selected = true, SelectedAccent = () => default });
        Assert.Equal(Card() with { Menu = () => null }, Card() with { Menu = () => null });
        Assert.NotEqual(Card(), Card() with { Menu = () => null });
        Assert.Equal(new Controls.GridCardProps(Card()), new Controls.GridCardProps(Card()));
    }

    [Fact]
    public void Shelf_card_props_gate_on_the_card_data_and_the_width()
    {
        Assert.Equal(new Controls.ShelfCardProps(Card(), 148f), new Controls.ShelfCardProps(Card(), 148f));
        Assert.NotEqual(new Controls.ShelfCardProps(Card(), 148f), new Controls.ShelfCardProps(Card(), 172f));
        Assert.NotEqual(new Controls.ShelfCardProps(Card(), 148f), new Controls.ShelfCardProps(Card(title: "Clouds"), 148f));
    }

    [Fact]
    public void Overlay_props_gate_on_data_with_the_play_handler_by_presence()
    {
        var a = new Controls.OverlayProps("spotify:album:1", () => { }, 44f, true, "Play");
        var b = new Controls.OverlayProps("spotify:album:1", () => { }, 44f, true, "Play");
        Assert.NotSame(a.OnPlay, b.OnPlay);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { OnPlay = null });
        Assert.NotEqual(a, a with { Uri = "spotify:album:2" });
        Assert.NotEqual(a, a with { Fab = 30f });
        Assert.NotEqual(a, a with { Centred = false });
        Assert.NotEqual(a, a with { PlayName = "Pause" });
    }
}

/// <summary>The grid card's cover chrome (the now-playing overlay and the corner "…") costs ~1 ms and ~68 KB a card to
/// mount and is invisible until hover, so it exists only while the card is HOT (pointer inside, keyboard focus reached
/// it) or RELATES to playback (the equalizer pill must show on a card nobody points at). The host feeds the decision;
/// this pins it.</summary>
public class CardChromeRulesTests
{
    [Fact]
    public void A_hot_card_mounts_its_chrome_even_when_nothing_plays()
        => Assert.True(Controls.CardChromeRules.Mounted(hot: true, relates: false));

    [Fact]
    public void A_card_that_relates_to_playback_mounts_its_chrome_without_a_pointer()
        => Assert.True(Controls.CardChromeRules.Mounted(hot: false, relates: true));

    [Fact]
    public void A_cold_unrelated_card_mounts_nothing()
        => Assert.False(Controls.CardChromeRules.Mounted(hot: false, relates: false));

    [Fact]
    public void Hot_is_true_with_the_pointer_in_and_focus_out()
        => Assert.True(Controls.CardChromeRules.Hot(pointerIn: true, focusIn: false));

    [Fact]
    public void Hot_is_true_with_the_pointer_out_and_focus_in()
        => Assert.True(Controls.CardChromeRules.Hot(pointerIn: false, focusIn: true));

    [Fact]
    public void Hot_is_false_with_neither_bit_set()
        => Assert.False(Controls.CardChromeRules.Hot(pointerIn: false, focusIn: false));

    [Fact]
    public void FocusIn_latches_true_on_a_genuine_gain()
        => Assert.True(Controls.CardChromeRules.FocusIn(got: false, stillInside: true));

    [Fact]
    public void FocusIn_is_false_on_a_loss_that_lands_outside_the_shell()
        => Assert.False(Controls.CardChromeRules.FocusIn(got: false, stillInside: false));

    [Fact]
    public void FocusIn_is_true_on_a_gain_regardless_of_stillInside()
        => Assert.True(Controls.CardChromeRules.FocusIn(got: true, stillInside: false));
}

/// <summary>The watched placeholder bind is cached per url: the thunk is a pure function of its url, so every slot
/// showing the same cover shares ONE bound <c>Prop</c> and a per-render caller allocates no closure.</summary>
public class DesignWatchedPlaceholderTests
{
    [Fact]
    public void The_same_url_yields_the_same_bound_prop()
    {
        const string url = "https://i.scdn.co/image/ab67616d0000b273feedfeedfeedfeedfeedfeed";
        var a = Design.WatchedPlaceholder(url);
        var b = Design.WatchedPlaceholder(url);
        Assert.True(a.IsBound);
        Assert.Equal(a, b);   // Prop equality is the payload's identity: the very same thunk
    }

    [Fact]
    public void Different_urls_yield_different_binds()
    {
        var a = Design.WatchedPlaceholder("https://i.scdn.co/image/ab67616d0000b273000000000000000000000001");
        var b = Design.WatchedPlaceholder("https://i.scdn.co/image/ab67616d0000b273000000000000000000000002");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_missing_url_and_an_empty_one_share_the_neutral_bind()
        => Assert.Equal(Design.WatchedPlaceholder(null), Design.WatchedPlaceholder(""));
}
