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
        // 4 gutter + 8 plate top + the cover (w − 16) + 8 gap + 20 title + 2 + 32 two-line subtitle + 8 plate bottom
        // + 0 gutter. The renderer AND the estimator call this; an estimate that disagrees re-pins the scroll anchor
        // mid-scroll and the feed jumps under the cursor.
        Assert.Equal(cardW + 66f, SurfaceGeometry.ShelfHeight(cardW));
    }

    [Theory]
    [InlineData(148f)]
    [InlineData(165f)]
    [InlineData(188f)]
    public void The_default_shelf_height_is_the_general_form_at_aspect_one_with_two_caption_lines(float cardW)
    {
        Assert.Equal(SurfaceGeometry.ShelfHeight(cardW), SurfaceGeometry.ShelfHeight(cardW, 1f, captionLines: 2, metaLine: false));
        Assert.Equal(cardW - 2f * Spacing.S, SurfaceGeometry.CoverHeight(cardW - 2f * Spacing.S, 1f));
    }

    [Theory]
    [InlineData(148f)]
    [InlineData(188f)]
    public void A_plain_one_line_shelf_reserves_exactly_one_caption_line(float cardW)
        // 4 + 8 + (w − 16) + 8 + 20 title + 2 + 16 caption + 8 + 0: no dead line under a one-line card.
        => Assert.Equal(cardW + 50f, SurfaceGeometry.ShelfHeight(cardW, 1f, captionLines: 1, metaLine: false));

    [Theory]
    [InlineData(148f)]
    [InlineData(188f)]
    public void A_lead_shelf_reserves_two_caption_lines_and_no_meta_line(float cardW)
    {
        // The lead's meta rides INLINE on its two caption lines: 20 title + 2 + 2·16 = 54 of labels, nothing more.
        Assert.Equal(cardW + 66f, SurfaceGeometry.ShelfHeight(cardW, 1f, captionLines: 2, metaLine: false));
        Assert.Equal(16f, SurfaceGeometry.ShelfHeight(cardW, 1f, 2, false) - SurfaceGeometry.ShelfHeight(cardW, 1f, 1, false));
    }

    [Theory]
    [InlineData(148f)]
    [InlineData(188f)]
    public void A_title_only_card_reserves_no_caption_gap(float cardW)
        => Assert.Equal(cardW + 32f, SurfaceGeometry.ShelfHeight(cardW, 1f, captionLines: 0, metaLine: false));

    [Fact]
    public void A_wide_tile_stacks_its_rounded_cover_one_caption_line_and_the_meta_line()
    {
        // 428 wide: the inner 412 at 16:9 is 231.75 → 232 (rounded to the pixel grid); + 4 + 8 + 8 + 8 of gutter and
        // plate, 20 title, 2 + 16 caption, 2 + 16 meta = 232 + 84.
        Assert.Equal(232f, SurfaceGeometry.CoverHeight(428f - 2f * Spacing.S, Design.Size.WideTileAspect));
        Assert.Equal(232f + 84f, SurfaceGeometry.ShelfHeight(428f, Design.Size.WideTileAspect, captionLines: 1, metaLine: true));
        // The meta line is the labels' 2 gap + one 16 caption line.
        Assert.Equal(18f, SurfaceGeometry.ShelfHeight(428f, Design.Size.WideTileAspect, 1, true)
                          - SurfaceGeometry.ShelfHeight(428f, Design.Size.WideTileAspect, 1, false));
    }

    [Fact]
    public void The_shelf_chrome_constants_are_the_ones_the_card_renders()
    {
        // SurfaceGeometry restates the spacing tokens as whole-DIP literals (it is engine-free); these pin the two.
        Assert.Equal(Spacing.XS, SurfaceGeometry.ShelfGutterTop);
        Assert.Equal(0f, SurfaceGeometry.ShelfGutterBottom);
        Assert.Equal(Spacing.S, SurfaceGeometry.ShelfPlatePad);
        Assert.Equal(20f, SurfaceGeometry.CardTitleLineH);
        Assert.Equal(16f, SurfaceGeometry.CardCaptionLineH);
        Assert.Equal(2f, SurfaceGeometry.CardLabelGap);
    }

    [Fact]
    public void The_recents_grid_fits_three_columns_on_a_632_page()
    {
        Assert.Equal(200f, Wavee.HomeUi.Zones.RecentsMinCol);
        Assert.Equal(3, GridEl.AutoFillColumnCount(632f, Wavee.HomeUi.Zones.RecentsMinCol, Spacing.M, 4));
        Assert.Equal(4, GridEl.AutoFillColumnCount(836f, Wavee.HomeUi.Zones.RecentsMinCol, Spacing.M, 4));
        Assert.Equal(3, GridEl.AutoFillColumnCount(835f, Wavee.HomeUi.Zones.RecentsMinCol, Spacing.M, 4));
    }

    [Theory]
    [InlineData(148f)]
    [InlineData(165f)]
    [InlineData(188f)]
    public void A_lead_cover_spanning_two_squares_is_exactly_as_tall_as_they_are(float squareW)
    {
        // A lead card spans two square cards plus the gap; its aspect is (leadInner / squareInner), so its cover height
        // must land on the squares' cover height — the shelf's one row stays level.
        float leadW = 2f * squareW + Spacing.M;
        float squareInner = squareW - 2f * Spacing.S, leadInner = leadW - 2f * Spacing.S;
        float aspect = leadInner / squareInner;
        Assert.Equal(squareInner, SurfaceGeometry.CoverHeight(leadInner, aspect));
        Assert.Equal(SurfaceGeometry.ShelfHeight(squareW, 1f, 2, false), SurfaceGeometry.ShelfHeight(leadW, aspect, 2, false));
    }

    [Fact]
    public void The_wide_tile_band_lives_in_the_token_layer()
    {
        Assert.Equal(330f, Design.Size.WideTileMin);
        Assert.Equal(440f, Design.Size.WideTileMax);
        Assert.Equal(16f / 9f, Design.Size.WideTileAspect);
        Assert.True(Controls.WideDecodePx >= Design.Size.WideTileMax);   // a wide cover never upsamples its decode
    }

    [Fact]
    public void A_grid_cell_grows_by_exactly_one_title_line_per_line()
    {
        float one = SurfaceGeometry.GridCardChromeFor(1, hasSubtitle: true);
        float two = SurfaceGeometry.GridCardChromeFor(2, hasSubtitle: true);
        Assert.Equal(SurfaceGeometry.GridTitleLineH, two - one);
        Assert.Equal(SurfaceGeometry.GridSubtitleBlockH,
                     SurfaceGeometry.GridCardChromeFor(1, true) - SurfaceGeometry.GridCardChromeFor(1, false));
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
    public void The_chip_capsule_geometry_is_shared_by_the_toggle_chip_and_the_link_chip()
    {
        Assert.Equal(Spacing.M, Controls.ChipPadX);
        Assert.Equal(13f, Controls.ChipFontSize);
        Assert.Equal(32f, Controls.ChipHeight);
    }

    [Fact]
    public void A_link_chip_is_a_keyed_hyperlink_that_never_shrinks()
    {
        var chip = Assert.IsType<BoxEl>(Controls.LinkChip("Podcasts", static () => { }, "spotify:page:podcasts"));
        Assert.Equal("spotify:page:podcasts", chip.Key);
        Assert.Equal(AutomationRole.Hyperlink, chip.Role);
        Assert.Equal(0f, chip.Shrink);                       // a wrapping row breaks to a new line instead of ellipsising
        Assert.Equal(Controls.ChipHeight, chip.MinHeight);   // one fixed height per line
        Assert.Equal(Controls.ChipPadX, chip.Padding.Left);
        Assert.Equal(Controls.ChipPadX, chip.Padding.Right);
    }

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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Each_bars_render_thread_track_is_its_pattern_as_an_even_linear_seamless_loop(int bar)
    {
        // The meter runs on the render thread as keyframe tracks; the loop must stay the exact function the bars always
        // followed (linear between five evenly spaced keys) and wrap without a jump.
        var keys = Controls.EqualizerTrack(bar);
        Assert.Equal(5, keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal(i / 4f, keys[i].Offset);
            Assert.Equal(Controls.EqualizerSample(bar, i / 4f), keys[i].Value);
            Assert.Equal((EasingSpec)Easing.Linear, keys[i].Easing);
        }
        Assert.Equal(keys[0].Value, keys[^1].Value);
        for (float u = 0f; u <= 1f; u += 0.01f)
        {
            int k = Math.Min(3, (int)MathF.Floor(u * 4f));
            float local = (u - keys[k].Offset) / (keys[k + 1].Offset - keys[k].Offset);
            Assert.Equal(Controls.EqualizerSample(bar, u), keys[k].Value + (keys[k + 1].Value - keys[k].Value) * local, 5);
        }
    }

    [Theory]
    [InlineData(13f, 1.25f)]
    [InlineData(13f, 1.5f)]
    [InlineData(13f, 1.75f)]
    [InlineData(14f, 1.5f)]
    public void The_render_thread_meter_poses_the_same_pixels_the_per_frame_ticker_wrote(float heightDip, float scale)
    {
        // The ticker wrote round(sample(u) · h_px) / h_px; the render thread samples the track and snaps through the engine.
        float hPx = heightDip * scale;
        for (int bar = 0; bar < 3; bar++)
        {
            var track = Controls.EqualizerTrack(bar);
            for (int k = 0; k <= 2000; k++)
            {
                float u = k / 2000f;
                float ticker = MathF.Round(Controls.EqualizerSample(bar, u) * hPx) / hPx;
                float render = FluentGpu.Animation.AnimEngine.SnapToDevicePixels(FluentGpu.Animation.AnimChannel.ScaleY,
                    FluentGpu.Animation.AnimEngine.SampleKeyframes(track, u), heightDip, scale);
                Assert.True(ticker == render, $"bar {bar} u={u} ticker={ticker} render={render}");
            }
        }
    }

    [Fact]
    public void A_covered_window_stops_the_loop_and_nothing_else_about_the_window_does()
    {
        Assert.True(Controls.ShouldTick(true, false, false, windowHidden: false));
        Assert.False(Controls.ShouldTick(true, false, false, windowHidden: true));   // covered / cloaked: nobody sees the bars
        // There is no live-video, GPU-tier or focus input: a visible meter keeps its rate (motion policy, 2026-10-03).
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

    [Fact]
    public void The_play_split_is_88_plus_a_divider_plus_a_32_chevron()
    {
        Assert.Equal(121f, ButtonRules.PlaySplitWidthNominal);
        Assert.Equal(ButtonRules.PlaySplitPrimaryMinW + 1f + ButtonRules.PlaySplitChevronW, ButtonRules.PlaySplitWidthNominal);
    }

    [Fact]
    public void The_play_split_menu_is_always_queue_then_play_next_then_radio()
    {
        var items = ButtonRules.PlaySplitItems;
        Assert.Equal(3, items.Length);
        Assert.Equal(ButtonRules.PlaySplitVerb.AddToQueue, items[0]);
        Assert.Equal(ButtonRules.PlaySplitVerb.PlayNext, items[1]);
        Assert.Equal(ButtonRules.PlaySplitVerb.StartRadio, items[2]);
    }
}

public class ControlsChipToneTests
{
    [Fact]
    public void The_neutral_chip_fills_with_primary_ink_and_picks_a_contrast_label()
    {
        var neutral = Controls.ChipStyle(Controls.ChipTone.Neutral);
        Assert.Equal(Tok.TextPrimary, neutral.OnBackground);
        Assert.Equal(ColorContrast.PickContrast(Tok.TextPrimary), neutral.OnForeground);
    }

    [Fact]
    public void The_neutral_chip_off_state_is_the_stock_toggle_ramp_and_it_never_wears_the_accent()
    {
        // The off plate is the standard-button fill/border (the same look as LinkChip and Browse's pills), not an outline.
        var neutral = Controls.ChipStyle(Controls.ChipTone.Neutral);
        var stock = FluentGpu.Controls.ToggleButton.DefaultStyle;
        Assert.Equal(stock.OffBackground, neutral.OffBackground);
        Assert.Equal(stock.OffBorder, neutral.OffBorder);
        Assert.Equal(stock.OffHoverBorder, neutral.OffHoverBorder);
        Assert.NotEqual(ColorF.Transparent, neutral.OffBackground);
        Assert.NotEqual(Tok.AccentDefault, neutral.OnBackground);
        Assert.Equal(ColorF.Transparent, neutral.OnBorder!.Value.Stops[0].Color);
        Assert.Equal(ColorF.Transparent, neutral.OnHoverBorder!.Value.Stops[0].Color);
    }

    [Fact]
    public void The_accent_chip_is_unchanged()
    {
        var accent = Controls.ChipStyle(Controls.ChipTone.Accent);
        var stock = Controls.AccentToggleStyle(Tok.AccentDefault);
        Assert.Equal(Tok.AccentDefault, accent.OnBackground);
        Assert.Equal(stock.OnForeground, accent.OnForeground);
        Assert.Equal(stock.OffBackground, accent.OffBackground);
        Assert.Equal(stock.OffBorder, accent.OffBorder);   // the stock ramp object, untouched
        Assert.Equal(Radii.Full, accent.CornerRadius);
        Assert.Equal(Controls.ChipHeight, accent.MinHeight);
    }

    [Fact]
    public void Both_tones_share_the_capsule_geometry()
    {
        var a = Controls.ChipStyle(Controls.ChipTone.Accent);
        var n = Controls.ChipStyle(Controls.ChipTone.Neutral);
        Assert.Equal(a.CornerRadius, n.CornerRadius);
        Assert.Equal(a.MinHeight, n.MinHeight);
        Assert.Equal(a.Padding, n.Padding);
        Assert.Equal(a.FontSize, n.FontSize);
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

    private const string TileA = "ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TileB = "ab67616d0000b273bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string TileC = "ab67616d0000b273cccccccccccccccccccccccc";
    private const string TileD = "ab67616d0000b273dddddddddddddddddddddddd";

    [Fact]
    public void A_spotify_image_token_resolves_to_its_cdn_url()
        => Assert.Equal("https://i.scdn.co/image/" + TileA,
            Controls.ArtUrl(Entities.Strings.Intern("spotify:image:" + TileA)));

    [Fact]
    public void A_mosaic_token_resolves_to_its_lead_tile()
        => Assert.Equal("https://i.scdn.co/image/" + TileA,
            Controls.ArtUrl(Entities.Strings.Intern($"spotify:mosaic:{TileA}:{TileB}:{TileC}:{TileD}")));

    [Fact]
    public void Any_other_spotify_token_has_no_url()
        => Assert.Null(Controls.ArtUrl(Entities.Strings.Intern("spotify:other:abc")));

    [Fact]
    public void MosaicTiles_answers_four_urls_for_a_mosaic_and_none_for_a_cover()
    {
        var mosaic = Entities.Strings.Intern($"spotify:mosaic:{TileA}:{TileB}:{TileC}:{TileD}");
        var tiles = Controls.MosaicTiles(mosaic);

        // each part is the 300-px album tile (CoverToken.MosaicTileId), same suffix, in wire order
        Assert.Equal(4, tiles.Length);
        Assert.Equal("https://i.scdn.co/image/ab67616d00001e02" + TileA[16..], tiles[0]);
        Assert.Equal("https://i.scdn.co/image/ab67616d00001e02" + TileB[16..], tiles[1]);
        Assert.Equal("https://i.scdn.co/image/ab67616d00001e02" + TileC[16..], tiles[2]);
        Assert.Equal("https://i.scdn.co/image/ab67616d00001e02" + TileD[16..], tiles[3]);

        // the cache answers the SAME tile strings on a repeat, so a re-render allocates nothing
        var again = Controls.MosaicTiles(mosaic);
        for (int i = 0; i < 4; i++) Assert.Same(tiles[i], again[i]);

        Assert.True(Controls.MosaicTiles(default).IsEmpty);
        Assert.True(Controls.MosaicTiles(Entities.Strings.Intern("https://i.scdn.co/image/" + TileA)).IsEmpty);
        Assert.True(Controls.MosaicTiles(Entities.Strings.Intern("spotify:image:" + TileA)).IsEmpty);
        // 1-3 tiles is Mosaic's own "paint one cover" rule: none here, ArtUrl's lead tile instead
        Assert.True(Controls.MosaicTiles(Entities.Strings.Intern($"spotify:mosaic:{TileA}:{TileB}:{TileC}")).IsEmpty);
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

/// <summary>The surface family's props gate on DATA. An entity adapter rebuilds its closures (and its subtitle element)
/// on every parent render; if those counted by identity, every surface / NowPlayingOverlay host on the page re-rendered
/// on every parent render (13× a frame on the artist page). A delegate counts by PRESENCE, never identity.</summary>
public class ControlsCardEqualityTests
{
    // Each call returns FRESH delegates and a FRESH subtitle element with the same data — the shape a re-rendering
    // adapter hands over.
    static Controls.CardData Card(string uri = "spotify:album:1", string title = "Blue", string? subtitle = "Joni Mitchell · 1971",
                                  bool play = true, string? dragKind = "album", bool circular = false)
        => new(uri, title, subtitle is null ? null : new TextEl(subtitle) { Size = 12f, MaxLines = 1 }, "https://i.scdn.co/image/x",
               OnClick: () => { }, OnPlay: play ? () => { } : null, Circular: circular,
               Drag: dragKind is null ? null : new DragSource(dragKind, () => null));

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
    }

    [Fact]
    public void The_cover_aspect_and_the_meta_line_are_data()
    {
        // A wide tile and its square twin are different cards; so are a card with and without its third line.
        Assert.Equal(Card() with { CoverAspect = 16f / 9f }, Card() with { CoverAspect = 16f / 9f });
        Assert.NotEqual(Card(), Card() with { CoverAspect = 16f / 9f });
        Assert.Equal(Card() with { Meta = "Spotify · 200 songs" }, Card() with { Meta = "Spotify · 200 songs" });
        Assert.NotEqual(Card(), Card() with { Meta = "Spotify · 200 songs" });
        Assert.NotEqual(Card() with { Meta = "200 songs" }, Card() with { Meta = "Spotify · 200 songs" });
        Assert.Equal((Card() with { Meta = "200 songs" }).GetHashCode(), (Card() with { Meta = "200 songs" }).GetHashCode());
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
        Assert.NotEqual(a, a with { AtRest = true });   // a video's FAB at rest is a different overlay
    }
}

/// <summary>The surface's lazy chrome (the now-playing overlay and the "…") costs ~1 ms and ~68 KB a surface to mount
/// and is invisible until hover, so it exists only while the surface is HOT (pointer within, focus within) or RELATES to
/// playback (the equalizer pill must show on a card nobody points at). The host feeds the decision; this pins it.</summary>
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
        => Assert.True(Controls.CardChromeRules.Hot(pointerIn: true, focusWithin: false));

    [Fact]
    public void Hot_is_true_with_the_pointer_out_and_focus_within()
        => Assert.True(Controls.CardChromeRules.Hot(pointerIn: false, focusWithin: true));

    [Fact]
    public void Hot_is_false_with_neither_bit_set()
        => Assert.False(Controls.CardChromeRules.Hot(pointerIn: false, focusWithin: false));

    [Fact]
    public void FocusWithin_holds_on_the_shells_own_focus()
        => Assert.True(Controls.CardChromeRules.FocusWithin(self: true, inner: false));

    [Fact]
    public void FocusWithin_holds_when_a_Tab_moves_from_the_shell_onto_its_own_FAB()
        // The shell hears that move as a LOSS (self false); the inner wrapper hears it as focus ENTERING (inner true).
        => Assert.True(Controls.CardChromeRules.FocusWithin(self: false, inner: true));

    [Fact]
    public void FocusWithin_is_false_once_focus_left_the_surface()
        => Assert.False(Controls.CardChromeRules.FocusWithin(self: false, inner: false));
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
