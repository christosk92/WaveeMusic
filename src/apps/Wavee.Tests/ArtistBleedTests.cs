// ── Wavee.Tests/ArtistBleedTests.cs — the EXPERIMENTAL artist bleed's pure rules and its material protocol ───────────────
//
// `ArtistBleed` (Entities/Artist.Bleed.cs) is the artist hero photo drawn from the window top through the shell material
// channel. Everything it decides is pure: when it applies, how long the photo holds full strength (the hero's own expanded
// fade), where the solid ground's top edge sits (the hero's presented bottom), the photo's parallax, the scrim and the span.
// The protocol half pins that a backdrop rides the SAME ownership outcome as the tint: a successor's claim, a neutral write
// or a stray publish can never leave a stale photo behind.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Signals;
using Xunit;

namespace Wavee.Tests;

public class ArtistBleedGateTests
{
    [Fact]
    public void It_applies_in_zune_with_the_switch_on_tinted_surfaces_and_a_header_photo()
    {
        Assert.True(ArtistBleed.Applies(true, WashLevel.Subtle, "u", zune: true, stacked: false));
        Assert.True(ArtistBleed.Applies(true, WashLevel.Rich, "u", zune: true, stacked: false));
    }

    [Fact]
    public void It_does_not_apply_without_the_switch_the_tint_or_a_photo()
    {
        Assert.False(ArtistBleed.Applies(false, WashLevel.Rich, "u", true, false));
        Assert.False(ArtistBleed.Applies(true, WashLevel.Off, "u", true, false));
        Assert.False(ArtistBleed.Applies(true, WashLevel.Rich, null, true, false));
        Assert.False(ArtistBleed.Applies(true, WashLevel.Rich, "", true, false));
    }

    [Fact]
    public void It_does_not_apply_outside_zune()
    {
        Assert.False(ArtistBleed.Applies(true, WashLevel.Rich, "u", zune: false, stacked: false));
        Assert.False(ArtistBleed.Applies(true, WashLevel.Subtle, "u", zune: false, stacked: true));
    }

    [Fact]
    public void It_does_not_apply_on_a_stacked_tier()
    {
        Assert.False(ArtistBleed.Applies(true, WashLevel.Rich, "u", zune: true, stacked: true));
    }
}

public class ArtistBleedPhotoRightTests
{
    [Fact]
    public void Without_a_floating_rail_the_photo_runs_to_the_cards_right_edge()
    {
        Assert.Equal(1000f, ArtistBleed.PhotoRight(1000f, zune: true, railFloats: false, 1000f, 320f, 0f));
    }

    [Fact]
    public void A_floating_zune_rail_stops_the_photo_at_the_panels_left_edge()
    {
        Assert.Equal(680f, ArtistBleed.PhotoRight(1000f, zune: true, railFloats: true, 1000f, 320f, 0f));
        Assert.Equal(672f, ArtistBleed.PhotoRight(1000f, zune: true, railFloats: true, 1000f, 320f, 8f));
    }

    [Fact]
    public void Outside_zune_the_card_edge_stands()
    {
        Assert.Equal(1000f, ArtistBleed.PhotoRight(1000f, zune: false, railFloats: true, 1000f, 320f, 0f));
    }

    [Fact]
    public void The_edge_never_passes_the_card_or_goes_negative()
    {
        Assert.Equal(600f, ArtistBleed.PhotoRight(600f, zune: true, railFloats: true, 1000f, 320f, 0f));
        Assert.Equal(0f, ArtistBleed.PhotoRight(600f, zune: true, railFloats: true, 300f, 320f, 0f));
    }

    [Fact]
    public void The_band_above_a_floating_panel_keeps_the_photo_through_the_extension()
    {
        float right = ArtistBleed.PhotoRight(1000f, zune: true, railFloats: true, 1000f, 320f, 0f);
        Assert.Equal(320f, ArtistBleed.BandExtensionWidth(1000f, right));
        Assert.Equal(0f, ArtistBleed.BandExtensionWidth(1000f, 1000f));
        Assert.Equal(0f, ArtistBleed.BandExtensionWidth(600f, 700f));
    }
}

public class ArtistBleedScrollCurveTests
{
    [Fact]
    public void The_photo_holds_full_strength_until_the_heros_own_fade_starts()
    {
        const float collapse = 300f;
        Assert.Equal(1f, ArtistBleed.HeroVisible(0, collapse));
        Assert.Equal(1f, ArtistBleed.HeroVisible(ArtistHeroLayout.ExpandedFadeStart(collapse), collapse));
        Assert.Equal(1f, ArtistBleed.HeroVisible(-40, collapse));   // an overscroll
    }

    [Fact]
    public void The_photo_is_gone_at_the_collapse_and_never_increases_on_the_way()
    {
        const float collapse = 300f;
        Assert.Equal(0f, ArtistBleed.HeroVisible(collapse, collapse));
        Assert.Equal(0f, ArtistBleed.HeroVisible(collapse + 500, collapse));
        float prev = 1f;
        for (int o = 0; o <= 300; o++)
        {
            float v = ArtistBleed.HeroVisible(o, collapse);
            Assert.InRange(v, 0f, 1f);
            Assert.True(v <= prev + 1e-6f, $"HeroVisible rose at {o}");
            prev = v;
        }
    }

    [Fact]
    public void A_collapse_shorter_than_the_fade_distance_still_resolves()
    {
        // ExpandedFadeStart(60) is 0: the whole collapse is the fade.
        Assert.Equal(1f, ArtistBleed.HeroVisible(0, 60f));
        Assert.Equal(0f, ArtistBleed.HeroVisible(60, 60f));
        Assert.InRange(ArtistBleed.HeroVisible(30, 60f), 0.01f, 0.99f);
    }

    [Fact]
    public void The_grounds_top_edge_rides_the_heros_presented_bottom_down_to_the_floor()
    {
        Assert.Equal(400f, ArtistBleed.HeroBottom(0, 400f, 0f));
        Assert.Equal(250f, ArtistBleed.HeroBottom(150, 400f, 0f));
        Assert.Equal(0f, ArtistBleed.HeroBottom(900, 400f, 0f));
        Assert.Equal(56f, ArtistBleed.HeroBottom(900, 400f, 56f));
    }
}

public class ArtistBleedGeometryTests
{
    [Fact]
    public void The_photo_nets_the_hero_scroll_less_the_media_parallax()
    {
        Assert.Equal(0f, ArtistBleed.ParallaxY(0));
        Assert.Equal(-100f * (1f - ArtistHeroLayout.PhotoParallaxFraction), ArtistBleed.ParallaxY(100), 3);
    }

    [Fact]
    public void A_top_overpan_never_slides_the_photo_down()
    {
        Assert.Equal(0f, ArtistBleed.ParallaxY(-40));
    }

    [Theory]
    [InlineData(0.0, 1f)]
    [InlineData(37.3, 2f)]
    [InlineData(150.0, 1.25f)]
    [InlineData(900.0, 1.5f)]
    public void The_strip_and_the_riser_tile_the_clip_at_one_line_for_any_scroll(double offset, float scale)
    {
        const float photoH = 440f, floor = 56f;
        float line = ArtistBleed.SnapToPixel(ArtistBleed.RiserTop(offset, photoH, floor, photoH), scale);
        // The strip is a clip-tall box shifted up so its bottom lands on the line; the riser starts on it.
        Assert.Equal(line, ArtistBleed.StripShift(line, photoH) + photoH, 3);
        Assert.InRange(line, floor, photoH);
        // A device pixel boundary.
        Assert.Equal(line * scale, MathF.Round(line * scale), 3);
    }

    [Theory]
    [InlineData(320f)]
    [InlineData(384f)]
    [InlineData(440f)]
    public void The_wash_never_yields_and_starts_at_the_photos_feather(float photoH)
    {
        float inset = ArtistBleed.WashTopInset(photoH);
        Assert.True(inset > 0f);
        Assert.Equal(photoH, inset + ArtistHeroLayout.PhotoFadeBandFor(photoH), 3);
        Assert.True(ArtistBleed.WashTopInset(100f) >= 0f);
    }

    [Fact]
    public void The_ground_line_never_leaves_the_photos_clip()
    {
        Assert.Equal(300f, ArtistBleed.RiserTop(0, 400f, 0f, 300f));   // a hero taller than the photo: the ground below already stands
        Assert.Equal(0f, ArtistBleed.RiserTop(900, 400f, 0f, 400f));
        Assert.Equal(440f, ArtistBleed.RiserTop(-40, 440f, 56f, 440f)); // an overscroll keeps the line at rest
    }

    [Fact]
    public void The_scrim_is_one_partial_veil_in_both_themes_and_as_tall_as_the_chrome()
    {
        // The field is dark in both themes, so the scrim is a single constant, not a per-theme pair.
        Assert.Equal(ArtistBleed.ScrimTop, ArtistBleed.ScrimTopAlpha());
        Assert.InRange(ArtistBleed.ScrimTopAlpha(), 0.01f, 0.99f);
        Assert.Equal(132f, ArtistBleed.ScrimHeight(132f));
        Assert.Equal(0f, ArtistBleed.ScrimHeight(-4f));
    }

    [Fact]
    public void The_scrim_is_an_eased_four_stop_falloff_that_ends_clear()
    {
        var s = ArtistBleed.ScrimStops;
        Assert.Equal(GradientSpec.MaxStops, s.Length);               // the recorder's ceiling, no more
        Assert.Equal((0f, 0.55f), s[0]);
        Assert.Equal((0.4f, 0.38f), s[1]);
        Assert.Equal((0.8f, 0.12f), s[2]);
        Assert.Equal((1f, 0f), s[3]);
        for (int i = 1; i < s.Length; i++)
        {
            Assert.True(s[i].Offset > s[i - 1].Offset);              // strictly increasing offsets
            Assert.True(s[i].Alpha < s[i - 1].Alpha);                // and a strictly falling alpha
        }
        // Eased, not linear: the first leg falls slower than a straight ramp would (0.55 to 0 over 0..1 is 0.33 at 0.4).
        Assert.True(s[1].Alpha > ArtistBleed.ScrimTop * (1f - s[1].Offset));
    }

    [Fact]
    public void The_bleed_veil_is_the_production_veil_in_both_themes()
    {
        var accent = ColorF.FromRgba(200, 40, 40, 255);
        foreach (var (theme, set) in new[] { (ThemeKind.Light, Tok.Light), (ThemeKind.Dark, Tok.Dark) })
        {
            bool light = theme == ThemeKind.Light;
            var expected = ColorF.Lerp(set.FillLayerDefault, accent, light ? 0.16f : 0.24f);
            var horizontal = Controls.ArtistHeroVeil(accent, vertical: false, theme, set.FillLayerDefault);
            Assert.Equal(expected.R, horizontal.Stops[0].Color.R, 4);
            Assert.Equal(expected.G, horizontal.Stops[0].Color.G, 4);
            Assert.Equal(expected.B, horizontal.Stops[0].Color.B, 4);
            Assert.Equal(0.96f, horizontal.Stops[0].Color.A);
            var vertical = Controls.ArtistHeroVeil(accent, vertical: true, theme, set.FillLayerDefault);
            Assert.Equal(light ? 0.42f : 0.78f, vertical.Stops[2].Color.A);
        }
    }

    [Fact]
    public void The_field_takes_the_themes_polarity_and_the_heros_tone()
    {
        const uint red = 0xFFC82828;
        var light = ArtistBleed.FieldBase(ThemeKind.Light, null, red);
        var dark = ArtistBleed.FieldBase(ThemeKind.Dark, null, red);
        Assert.True(Design.Palette.ToHsl(light).L > 0.8f);
        Assert.True(Design.Palette.ToHsl(dark).L < 0.25f);
        Assert.True(light.ToHsv().S > 0f);                  // tinted by the hero, not a neutral
        Assert.True(dark.ToHsv().S > 0f);
        Assert.NotEqual(Tok.MediaStage, light);
        Assert.NotEqual(Tok.MediaStage, dark);
        Assert.False(ArtistBleed.FieldDark(ThemeKind.Light));
        Assert.True(ArtistBleed.FieldDark(ThemeKind.Dark));
        // No hero hue (no accent, or a grey one) is the theme's neutral page tone.
        Assert.Equal(Design.Palette.PageToneNeutralLight, ArtistBleed.FieldBase(ThemeKind.Light, null, 0));
        Assert.Equal(Design.Palette.PageToneNeutralDark, ArtistBleed.FieldBase(ThemeKind.Dark, null, 0));
        Assert.Equal(Design.Palette.PageToneNeutralLight, ArtistBleed.FieldBase(ThemeKind.Light, null, 0xFF808080));
        Assert.Equal(Design.Palette.PageToneNeutralDark, ArtistBleed.FieldBase(ThemeKind.Dark, null, 0xFF808080));
    }

    [Fact]
    public void The_field_prefers_the_graded_scheme_over_the_payload_accent()
    {
        // The scrim's ground is the page's own tone with CoverPageTonePlane.Resolve's precedence: the graded scheme first.
        var scheme = new Scheme(0xFF191414, 0xFF1DB954, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF);
        foreach (var theme in new[] { ThemeKind.Light, ThemeKind.Dark })
        {
            var graded = ArtistBleed.FieldBase(theme, scheme, 0xFFC82828);
            Assert.Equal(Design.Palette.PageTone(scheme, theme), graded);
            Assert.NotEqual(ArtistBleed.FieldBase(theme, null, 0xFFC82828), graded);
        }
    }

    [Theory]
    [InlineData(48f, 48f, 48f)]     // Classic / Library: the card's top is the title bar's bottom
    [InlineData(132f, 48f, 48f)]    // Zune with a row 2: the inline right panel starts at the title bar's bottom, not under the band
    [InlineData(100f, 48f, 48f)]    // Zune with no row 2, mid-FLIP
    [InlineData(20f, 48f, 20f)]     // never taller than the card's presented top
    public void The_right_side_field_stops_at_the_title_bar_so_it_never_shows_through_the_rail_gap(float cardTop, float titleBar, float expected)
        => Assert.Equal(expected, ArtistBleed.SideFieldHeight(cardTop, titleBar));
}

public class ArtistBleedFrameTests
{
    static readonly float[] Aspects = [1.5f, 2.4f, 3.6f];
    static readonly float[] CardWidths = [600f, 1200f, 1900f];
    static readonly float[] PhotoHeights = [320f, 440f];
    static readonly float[] Tops = [40f, 92f, 124f];

    [Fact]
    public void The_cards_photo_box_translated_by_the_pose_is_the_shells_box_and_crops_the_same_pixels()
    {
        foreach (float aspect in Aspects)
        foreach (float cardW in CardWidths)
        foreach (float photoH in PhotoHeights)
        foreach (float rectY in Tops)
        foreach (float poseY in Tops)   // equal to rectY and not
        {
            var f = ArtistBleed.FrameFor(cardW, poseY, rectY, photoH);

            // The shell's inner photo box, in window coordinates: top 0, the card's final width, the taller chrome plus the photo.
            var shell = new RectF(0f, 0f, cardW, MathF.Max(rectY, poseY) + photoH);
            // The card's box, in card-local coordinates, translated by the card's presented top (poseY).
            var card = new RectF(0f, f.Top + poseY, f.Width, f.Height);
            Assert.Equal(shell, card);

            int srcH = 1000;
            int srcW = (int)MathF.Round(srcH * aspect);
            var (shellDraw, shellUv) = SceneRecorder.ImageContentFit(ImageFit.Cover, new RectF(0f, 0f, f.Width, f.Height), srcW, srcH,
                                                                     ArtistHeroLayout.PhotoFocusX, ArtistBleed.PhotoFocusY);
            var (cardDraw, cardUv) = SceneRecorder.ImageContentFit(ImageFit.Cover, new RectF(0f, 0f, card.W, card.H), srcW, srcH,
                                                                   ArtistHeroLayout.PhotoFocusX, ArtistBleed.PhotoFocusY);
            Assert.Equal(shellDraw, cardDraw);
            Assert.Equal(shellUv, cardUv);
        }
    }

    [Theory]
    [InlineData(1200f, 92f, 92f, 440f)]
    [InlineData(1200f, 40f, 124f, 320f)]
    [InlineData(600f, 124f, 40f, 440f)]
    public void The_frame_is_the_taller_chrome_plus_the_photo_and_rises_by_the_pose(float cardW, float poseY, float rectY, float photoH)
    {
        var f = ArtistBleed.FrameFor(cardW, poseY, rectY, photoH);
        Assert.Equal(MathF.Max(rectY, poseY) + photoH, f.Height);
        Assert.Equal(-poseY, f.Top);
        Assert.Equal(cardW, f.Width);
    }

    [Fact]
    public void The_photo_is_unscaled_and_focused_on_the_top()
    {
        Assert.Equal(1f, ArtistBleed.PhotoScale);
        Assert.Equal(0f, ArtistBleed.PhotoFocusY);
    }
}

public class ArtistBleedHandOverTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(0.998f)]
    public void The_cards_layers_stay_until_the_shells_are_fully_present(float presence)
        => Assert.Equal(1f, ArtistBleed.CardLayerOpacity(true, presence));

    [Fact]
    public void The_cards_layers_yield_in_one_step_at_full_presence()
    {
        Assert.Equal(0f, ArtistBleed.CardLayerOpacity(true, 1f));
        Assert.Equal(0f, ArtistBleed.CardLayerOpacity(true, 0.999f));
    }

    [Fact]
    public void The_hand_over_eases_the_cards_layers_out_only_once_the_shell_is_fully_present()
    {
        // No hand-over yet: nothing yields. The hand-over never starts before presence 1, however far it has run.
        Assert.Equal(1f, ArtistBleed.CardLayerOpacity(true, 1f, 0f));
        Assert.Equal(1f, ArtistBleed.CardLayerOpacity(true, 0.9f, 1f));
        float prev = 1f;
        for (float h = 0f; h <= 1f; h += 0.1f)
        {
            float o = ArtistBleed.CardLayerOpacity(true, 1f, h);
            Assert.InRange(o, 0f, prev);   // monotone, never a step back up
            prev = o;
        }
        Assert.Equal(0f, ArtistBleed.CardLayerOpacity(true, 1f, 1f));
        Assert.Equal(0.5f, ArtistBleed.CardLayerOpacity(true, 1f, 0.5f), 5);
        Assert.Equal(1f, ArtistBleed.CardLayerOpacity(false, 1f, 1f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.7f)]
    [InlineData(1f)]
    public void Nothing_yields_when_the_shell_does_not_draw_the_photo(float presence)
        => Assert.Equal(1f, ArtistBleed.CardLayerOpacity(false, presence));

    [Fact]
    public void The_chrome_ink_mix_is_the_presence_times_the_hero_still_showing_when_the_polarities_differ()
    {
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(0f, 1f, fieldDark: true, themeDark: false));
        Assert.Equal(1f, ArtistBleed.ChromeInkMix(1f, 1f, fieldDark: true, themeDark: false));
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(1f, 0f, fieldDark: true, themeDark: false));
        Assert.Equal(0.25f, ArtistBleed.ChromeInkMix(0.5f, 0.5f, fieldDark: true, themeDark: false), 5);
        Assert.Equal(1f, ArtistBleed.ChromeInkMix(3f, 2f, fieldDark: true, themeDark: false));   // clamped both ways
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(-1f, 1f, fieldDark: true, themeDark: false));
        float prev = 0f;
        for (int i = 0; i <= 10; i++)
        {
            float v = ArtistBleed.ChromeInkMix(i / 10f, 0.8f, fieldDark: true, themeDark: false);
            Assert.True(v >= prev - 1e-6f);
            prev = v;
        }
        prev = 0f;
        for (int i = 0; i <= 10; i++)
        {
            float v = ArtistBleed.ChromeInkMix(0.8f, i / 10f, fieldDark: true, themeDark: false);
            Assert.True(v >= prev - 1e-6f);
            prev = v;
        }
    }

    [Fact]
    public void The_ink_is_the_themes_at_zero_and_the_medias_at_one()
    {
        var a = ColorF.FromRgba(10, 20, 30, 255);
        var b = ColorF.FromRgba(240, 230, 220, 255);
        Assert.Equal(a, ArtistBleed.Ink(a, b, 0f));
        Assert.Equal(b, ArtistBleed.Ink(a, b, 1f));
        Assert.Equal(a, ArtistBleed.Ink(a, b, -3f));
        Assert.Equal(b, ArtistBleed.Ink(a, b, 3f));
    }

    [Fact]
    public void The_real_ink_endpoints_are_the_theme_token_at_zero_and_the_on_media_token_at_one()
    {
        Assert.Equal(Tok.TextPrimary, ArtistBleed.Ink(Tok.TextPrimary, Design.OnMedia.Ink, 0f));
        Assert.Equal(Design.OnMedia.Ink, ArtistBleed.Ink(Tok.TextPrimary, Design.OnMedia.Ink, 1f));
        Assert.Equal(Tok.TextSecondary, ArtistBleed.Ink(Tok.TextSecondary, Design.OnMedia.InkSecondary, 0f));
        Assert.Equal(Design.OnMedia.InkSecondary, ArtistBleed.Ink(Tok.TextSecondary, Design.OnMedia.InkSecondary, 1f));
        Assert.Equal(Tok.TextTertiary, ArtistBleed.Ink(Tok.TextTertiary, Design.OnMedia.InkTertiary, 0f));
        Assert.Equal(Design.OnMedia.InkTertiary, ArtistBleed.Ink(Tok.TextTertiary, Design.OnMedia.InkTertiary, 1f));
    }

    [Fact]
    public void The_accent_word_binds_the_on_media_accent_arms_only_when_the_band_gives_them()
    {
        // Over the dark bleed the light theme's own accent text is dark on dark: an accent word takes the dark-theme shades once given.
        Assert.Equal(Tok.AccentTextPrimary, ArtistBleed.Ink(Tok.AccentTextPrimary, Design.OnMedia.AccentInk, 0f));
        Assert.Equal(Design.OnMedia.AccentInk, ArtistBleed.Ink(Tok.AccentTextPrimary, Design.OnMedia.AccentInk, 1f));
        Assert.Equal(Tok.AccentTextPrimary, Shell.Ui.ChromeInkAccent());   // no backdrop: today's accent text

        var plain = Controls.TextAction("Play", null, primary: true);
        Assert.False(((TextEl)plain.Children![0]).Color.IsBound);

        var arms = new Controls.TextActionInk(Shell.Ui.ChromeInkSecondary, Design.OnMedia.Ink, Design.OnMedia.InkSecondary,
            Shell.Ui.ChromeInkAccent, Design.OnMedia.AccentInkSecondary, Design.OnMedia.AccentInkTertiary);
        var accent = (TextEl)Controls.TextAction("Play", null, primary: true, ink: arms).Children![0];
        Assert.True(accent.Color.IsBound);
        Assert.Equal(Design.OnMedia.AccentInkSecondary, accent.HoverColor);
        Assert.Equal(Design.OnMedia.AccentInkTertiary, accent.PressedColor);

        // Without accent arms an accent word keeps the page's own ramp even when the plain words are given ink.
        var noAccent = new Controls.TextActionInk(Shell.Ui.ChromeInkSecondary, Design.OnMedia.Ink, Design.OnMedia.InkSecondary);
        var kept = (TextEl)Controls.TextAction("Play", null, primary: true, ink: noAccent).Children![0];
        Assert.False(kept.Color.IsBound);
        Assert.Equal(Tok.AccentTextSecondary, kept.HoverColor);
    }

    [Fact]
    public void The_chrome_ink_mix_is_zero_at_no_presence_and_at_no_hero_when_the_polarities_differ()
    {
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(0f, 0f, fieldDark: true, themeDark: false));
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(0f, 1f, fieldDark: true, themeDark: false));      // nothing drawn: the theme's ink
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(1f, 0f, fieldDark: true, themeDark: false));      // the hero scrolled away: the theme's ink
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(0.6f, 0f, fieldDark: true, themeDark: false));
    }

    [Fact]
    public void In_light_the_chrome_ink_is_the_themes_at_full_presence()
    {
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(1f, 1f, ArtistBleed.FieldDark(ThemeKind.Light), themeDark: false));
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(1f, 1f, ArtistBleed.FieldDark(ThemeKind.Dark), themeDark: true));
    }

    [Fact]
    public void The_hero_copy_is_theme_ink_in_light()
    {
        Assert.False(ArtistBleed.CopyOnMedia(true, ThemeKind.Light));
        Assert.True(ArtistBleed.CopyOnMedia(true, ThemeKind.Dark));
        Assert.False(ArtistBleed.CopyOnMedia(false, ThemeKind.Dark));
    }

    [Fact]
    public void With_no_backdrop_the_shells_chrome_ink_is_exactly_the_themes()
    {
        Assert.Null(Shell.Ui.BleedBackdrop.Peek());
        Assert.False(Shell.Ui.ChromeOnMedia);
        Assert.Equal(0f, Shell.Ui.ChromeInkMix());
        Assert.Equal(Tok.TextPrimary, Shell.Ui.ChromeInkPrimary());
        Assert.Equal(Tok.TextSecondary, Shell.Ui.ChromeInkSecondary());
        Assert.Equal(Tok.TextTertiary, Shell.Ui.ChromeInkTertiary());
    }

    [Fact]
    public void The_chrome_props_are_always_bound_and_at_mix_zero_read_as_the_theme_tokens()
    {
        // A bind is wired only when a node mounts, so a site that was static at mount and bound once the bleed published kept the
        // theme ink on a reused node. The chrome ink is therefore bound with or without a backdrop; at mix 0 it IS the theme's token.
        Assert.True(Shell.Ui.ChromePrimary.IsBound);
        Assert.True(Shell.Ui.ChromeSecondary.IsBound);
        Assert.True(Shell.Ui.ChromeTertiary.IsBound);
        Assert.Equal(0f, Shell.Ui.ChromeInkMix());
        Assert.Equal(Tok.TextPrimary, Shell.Ui.ChromeInkPrimary());
        Assert.Equal(Tok.TextSecondary, Shell.Ui.ChromeInkSecondary());
        Assert.Equal(Tok.TextTertiary, Shell.Ui.ChromeInkTertiary());
        Assert.False(Shell.Ui.ChromeArmsOnMedia.Peek());
    }

    [Fact]
    public void The_row_ink_is_always_given_so_a_row_two_word_never_flips_from_static_to_bound()
    {
        Assert.Equal(Tok.TextSecondary, Shell.Ui.ChromeInkSecondary());   // mix 0: the rest ink is the theme's, bound
        var plain = Controls.TextAction("Share", null, ink: new Controls.TextActionInk(Shell.Ui.ChromeInkSecondary, Tok.TextPrimary, Tok.TextSecondary));
        Assert.True(((TextEl)plain.Children![0]).Color.IsBound);
    }

    [Fact]
    public void The_side_field_is_heavier_than_the_photo_scrim()
    {
        // It has to hold light ink on the plain theme ground, where the scrim only has to calm a photo.
        Assert.True(ArtistBleed.SideFieldAlpha > ArtistBleed.ScrimTop);
        Assert.InRange(ArtistBleed.SideFieldAlpha, 0f, 1f);
    }
}

public class ArtistBleedMaterialProtocolTests
{
    static readonly ColorF Tint = ColorF.FromRgba(40, 60, 90, 255);

    static ShellBackdrop Backdrop(string key) => new("u:" + key, 440f, 440f, 0f, new Signal<double>(0.0), 344f, 1280, 560, key);

    static Signal<ShellMaterialState> Slot() => new(default);

    [Fact]
    public void A_claim_with_a_backdrop_stores_it()
    {
        var slot = Slot();
        var owner = new object();
        var bd = Backdrop("a");
        ShellMaterial.Publish(slot, owner, isClaim: true, definite: false, Tint, wash: null, bd);
        Assert.Same(bd, slot.Peek().Backdrop);
        Assert.Same(owner, slot.Peek().Owner);
    }

    [Fact]
    public void A_second_owners_claim_without_a_backdrop_clears_it()
    {
        var slot = Slot();
        var a = new object();
        var b = new object();
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, Tint, wash: null, Backdrop("a"));
        // The successor knows its colour: a known-colour write that carries no backdrop.
        ShellMaterial.Publish(slot, b, isClaim: true, definite: false, Tint, wash: null);
        Assert.Null(slot.Peek().Backdrop);
        Assert.Same(b, slot.Peek().Owner);
    }

    [Fact]
    public void A_successor_that_does_not_know_its_colour_yet_holds_the_tint_but_drops_the_backdrop()
    {
        var slot = Slot();
        var a = new object();
        var b = new object();
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, Tint, wash: null, Backdrop("a"));
        ShellMaterial.Publish(slot, b, isClaim: true, definite: false, tint: null, wash: null);
        Assert.Equal(Tint, slot.Peek().Tint);
        Assert.Null(slot.Peek().Backdrop);
    }

    [Fact]
    public void A_neutral_write_clears_the_backdrop()
    {
        var slot = Slot();
        var a = new object();
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, Tint, wash: null, Backdrop("a"));
        ShellMaterial.Publish(slot, a, isClaim: false, definite: true, tint: null, wash: null);
        Assert.Null(slot.Peek().Backdrop);
        Assert.Null(slot.Peek().Tint);
    }

    [Fact]
    public void An_owner_refresh_without_a_colour_still_carries_its_backdrop()
    {
        var slot = Slot();
        var a = new object();
        // The claim lands before the palette has graded: nothing to show yet, no backdrop either.
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, tint: null, wash: null);
        Assert.Null(slot.Peek().Backdrop);
        // The body comes up with its backdrop while the colour is still unknown: the refresh must store it.
        var bd = Backdrop("a");
        ShellMaterial.Publish(slot, a, isClaim: false, definite: false, tint: null, wash: null, bd);
        Assert.Same(bd, slot.Peek().Backdrop);
        Assert.Same(a, slot.Peek().Owner);
        // A later geometry change (a tier switch) updates it while the colour is still unknown.
        var bd2 = Backdrop("a") with { PhotoHeight = 320f };
        ShellMaterial.Publish(slot, a, isClaim: false, definite: false, tint: null, wash: null, bd2);
        Assert.Same(bd2, slot.Peek().Backdrop);
    }

    [Fact]
    public void A_stray_publish_from_a_non_owner_writes_nothing()
    {
        var slot = Slot();
        var a = new object();
        var stray = new object();
        var bd = Backdrop("a");
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, Tint, wash: null, bd);
        ShellMaterial.Publish(slot, stray, isClaim: false, definite: false, Tint, wash: null, Backdrop("stray"));
        Assert.Same(bd, slot.Peek().Backdrop);
        Assert.Same(a, slot.Peek().Owner);
    }
}

public class ArtistBleedNotchAndPoseTests
{
    [Fact]
    public void The_cards_top_left_radius_fades_with_the_hero_region()
    {
        Assert.Equal(8f, ArtistBleed.CornerFor(8f, 0f));
        Assert.Equal(0f, ArtistBleed.CornerFor(8f, 1f));
        Assert.Equal(8f, ArtistBleed.CornerFor(8f, -3f));   // clamped both ways
        Assert.Equal(0f, ArtistBleed.CornerFor(8f, 3f));
        float last = float.MaxValue;
        for (float cut = 0f; cut <= 1.0001f; cut += 0.05f)
        {
            float r = ArtistBleed.CornerFor(8f, cut);
            Assert.True(r <= last);
            last = r;
        }
    }

    [Fact]
    public void The_photo_clips_end_lands_on_the_heros_presented_bottom_through_the_parallax()
    {
        foreach (double o in new[] { -40.0, 0.0, 60.0, 200.0, 380.0, 900.0 })
        foreach (float heroH in new[] { 280f, 440f })
        foreach (float floor in new[] { 0f, 56f })
        foreach (float photoH in new[] { 300f, 440f, 520f })
        {
            float clip = ArtistBleed.PhotoClip(o, heroH, floor, photoH);
            float screenBottom = clip + ArtistBleed.ParallaxY(o);   // the box carries the parallax translation
            float heroBottom = ArtistBleed.HeroBottom(o, heroH, floor);
            Assert.True(clip <= photoH);
            Assert.True(screenBottom <= heroBottom + 0.001f);
            // Equal to the hero's presented bottom whenever the photo is tall enough to reach it.
            if (photoH + ArtistBleed.ParallaxY(o) >= heroBottom)
                Assert.True(MathF.Abs(heroBottom - screenBottom) <= 0.001f);
        }
    }

    [Fact]
    public void The_analytic_pose_starts_at_from_and_lands_on_to()
    {
        var from = new RectF(240f, 40f, 800f, 600f);
        var to = new RectF(0f, 0f, 1040f, 640f);
        Assert.Equal(from, ArtistBleed.PoseAt(from, to, 0f, 300f, ArtistBleed.PaneEase));
        Assert.Equal(to, ArtistBleed.PoseAt(from, to, 300f, 300f, ArtistBleed.PaneEase));
        Assert.Equal(to, ArtistBleed.PoseAt(from, to, 900f, 300f, ArtistBleed.PaneEase));
        Assert.Equal(to, ArtistBleed.PoseAt(from, to, 0f, 0f, ArtistBleed.PaneEase));   // a zero-length tween is the end
        var up = new RectF(0f, 0f, 100f, 100f);
        var dn = new RectF(200f, 0f, 100f, 100f);
        float last = 0f;
        for (float ms = 0f; ms <= 320f; ms += 10f)
        {
            float x = ArtistBleed.PoseAt(up, dn, ms, 300f, ArtistBleed.PaneEase).X;
            Assert.True(x >= last);
            last = x;
        }
    }

    [Fact]
    public void The_pane_ease_runs_from_zero_to_one()
    {
        Assert.Equal(0f, ArtistBleed.PaneEase(0f));
        Assert.Equal(1f, ArtistBleed.PaneEase(1f), 4);
        Assert.InRange(ArtistBleed.PaneEase(0.5f), 0.5f, 1f);   // (0, .35, .15, 1) is front-loaded
    }

    [Fact]
    public void Only_a_settle_right_after_a_live_toggle_plays_the_analytic_pose()
    {
        Assert.True(ArtistBleed.TweensPose(true, 10f));
        Assert.True(ArtistBleed.TweensPose(true, ArtistBleed.ToggleArmWindowMs));
        Assert.False(ArtistBleed.TweensPose(true, 400f));    // a resize long after a toggle samples
        Assert.False(ArtistBleed.TweensPose(false, 0f));
    }

    [Fact]
    public void A_drag_or_a_band_cross_never_arms_the_analytic_pose()
    {
        Assert.True(ArtistBleed.ArmsPose(layoutSuppressed: false, bandCrossed: false));
        Assert.False(ArtistBleed.ArmsPose(layoutSuppressed: true, bandCrossed: false));   // a grip drag snaps the card 1:1
        Assert.False(ArtistBleed.ArmsPose(layoutSuppressed: false, bandCrossed: true));   // a resize cancels the FLIP
    }

    [Fact]
    public void A_height_only_settle_keeps_the_running_tween_and_any_other_change_ends_it()
    {
        var to = new RectF(0f, 96f, 1040f, 544f);
        Assert.True(ArtistBleed.ContinuesTween(to, to with { H = 560f }));    // the nav-style FLIP's height relayout
        Assert.False(ArtistBleed.ContinuesTween(to, to with { X = 240f }));   // a pane/resize layout
        Assert.False(ArtistBleed.ContinuesTween(to, to with { Y = 0f }));
        Assert.False(ArtistBleed.ContinuesTween(to, to with { W = 800f }));
    }
}
