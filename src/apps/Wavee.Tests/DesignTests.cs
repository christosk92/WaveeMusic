// ── Wavee.Tests/DesignTests.cs — the token layer, the type ramp, the colour maths, the motion system ──────────────
//
// Wave 4's gate for `Platform/Design.cs` (owner L). These are the 0.2.9 suites ch 00 §9.3(6) names, ported with the
// rules they pin: DesignTokenConvergence, MotionSystem, EntranceStagger, ShellMergedRung, VoiceUnification,
// LightModeOverhaul, DetailPageTone, ShellWashGeometry, HoverMotionGate, DetailRevealRamp, ContentHostPageTransition.
//
// NOTHING HERE STARTS THE ENGINE LOOP OR OPENS A WINDOW. Every theme-dependent fact goes through the PURE overload
// (`ShellGroundFor(set, theme)`, `Hairline(seed, theme, bg)`, `PageTone(scheme, theme)`, `StageArm.For(theme)`) rather
// than mutating `Tok.Theme` — which is exactly why those overloads exist, and why this file can run beside every other
// suite without a collection of its own.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DesignTypeRampTests
{
    // ── the three-part contract: every alias resolves a SIZE, a LINE HEIGHT and a WEIGHT ────────────────────────────

    // Keyed by NAME so the theory data stays serializable (a TextEl is not), and resolved inside the fact.
    public static TheoryData<string> OnRampAliases => new()
    {
        "TrackTitle", "CardTitle", "TrackMeta", "Eyebrow", "RailHeader", "ModuleHeader",
        "PageHero", "DetailHero", "SurfaceDisplay", "NowPlayingTitle", "PickQuote",
    };

    static TextEl Alias(string name) => name switch
    {
        "TrackTitle" => Design.Type.TrackTitle("x"),
        "CardTitle" => Design.Type.CardTitle("x"),
        "TrackMeta" => Design.Type.TrackMeta("x"),
        "Eyebrow" => Design.Type.Eyebrow("x"),
        "RailHeader" => Design.Type.RailHeader("x"),
        "ModuleHeader" => Design.Type.ModuleHeader("x"),
        "PageHero" => Design.Type.PageHero("x"),
        "DetailHero" => Design.Type.DetailHero("x"),
        "SurfaceDisplay" => Design.Type.SurfaceDisplay("x"),
        "NowPlayingTitle" => Design.Type.NowPlayingTitle("x"),
        _ => Design.Type.PickQuote("x"),
    };

    [Theory]
    [MemberData(nameof(OnRampAliases))]
    public void Every_alias_resolves_size_line_height_and_weight(string name)
    {
        var el = Alias(name);
        // A bare `with { Size = 13f }` keeps the previous rung's line box and destroys the vertical rhythm. That is the
        // whole reason an alias exists rather than a raw size, so the contract is asserted per alias.
        Assert.True(el.Size > 0f, name + " has no size");
        Assert.True(el.LineHeight > 0f, name + " has no line height");
        Assert.True(el.ResolvedWeight > 0, name + " has no weight");
    }

    [Theory]
    [MemberData(nameof(OnRampAliases))]
    public void Every_on_ramp_alias_is_400_or_600(string name)
        => Assert.True(Alias(name).ResolvedWeight is 400 or 600, name + " is weight " + Alias(name).ResolvedWeight);

    [Fact]
    public void The_six_sanctioned_weight_divergences_and_no_seventh()
    {
        // THREE display-face 700s — the masthead voice, not a UI label.
        Assert.Equal(700, Design.Type.ArtistDisplay("x").ResolvedWeight);
        Assert.Equal(700, Design.Type.ArtistTitle("x").ResolvedWeight);
        Assert.Equal(700, Design.Type.ArtistCompactTitle("x").ResolvedWeight);
        // THREE SemiLight 350s — the same cut the engine's own pivot header uses.
        Assert.Equal(350, Design.Type.PivotLabel("x").ResolvedWeight);
        Assert.Equal(350, Design.Type.NpvLyric("x").ResolvedWeight);
        Assert.Equal(350, Design.Type.StatHero("2B", null).Weight);
    }

    [Fact]
    public void The_three_artist_aliases_carry_their_shrink_floors()
    {
        // The floor is what makes a long artist name step DOWN inside one rung rather than break to a second line.
        Assert.Equal(68f, Design.Type.ArtistDisplay("x").MinSize);
        Assert.Equal(40f, Design.Type.ArtistTitle("x").MinSize);
        Assert.Equal(28f, Design.Type.ArtistCompactTitle("x").MinSize);
    }

    [Fact]
    public void PivotLabel_is_the_one_off_ramp_rung()
    {
        // 19/25 is deliberately OFF the eight-rung engine ramp. Named here so a THIRD off-ramp cannot arrive unlabelled.
        var p = Design.Type.PivotLabel("x");
        Assert.Equal(19f, p.Size);
        Assert.Equal(25f, p.LineHeight);
    }

    [Fact]
    public void FoldTitle_is_PickQuote()
    {
        // Named for the surface, not for the voice — but it borrows every one of PickQuote's numbers, and that
        // borrowing is the thing that must not silently become a second cut.
        var a = Design.Type.FoldTitle("x");
        var b = Design.Type.PickQuote("x");
        Assert.Equal(b.Size, a.Size);
        Assert.Equal(b.LineHeight, a.LineHeight);
        Assert.Equal(b.ResolvedWeight, a.ResolvedWeight);
        Assert.Equal(b.CharSpacing, a.CharSpacing);
    }

    [Fact]
    public void Eyebrow_carries_the_one_tracking()
    {
        // 30/1000 em. The role used to carry NINE tracking values across 58 call sites, which is why two eyebrows
        // stacked on one page never looked like the same label. (Sentence case is the other half of the rule and is a
        // parity item rather than a fact: the alias simply passes the string through, so there is no transform to
        // assert the absence of.)
        Assert.Equal(30f, Design.Type.EyebrowTracking);
        Assert.Equal(Design.Type.EyebrowTracking, Design.Type.Eyebrow("Daily Mix").CharSpacing);
    }

    [Fact]
    public void A_baseline_paired_span_never_wraps()
    {
        // The small run would land alone on line 2 with no heading to sit on.
        foreach (var span in new[] { Design.Type.ModuleHeader("Radio", "20 stations"),
                                     Design.Type.RailHeader("Radio", "20 stations"),
                                     Design.Type.StatHero("2B", "monthly") })
        {
            Assert.Equal(TextWrap.NoWrap, span.Wrap);
            Assert.Equal(1, span.MaxLines);
            Assert.Equal(TextTrim.CharacterEllipsis, span.Trim);
        }
    }

    [Fact]
    public void A_paired_span_keeps_the_heading_rungs_metrics()
    {
        // The small run shares the heading's BASELINE because the two are one paragraph — which only works if the
        // paragraph carries the HEADING's size and line height, not an average of the two.
        var heading = Ui.Subtitle("");
        var paired = Design.Type.ModuleHeader("Radio", "20 stations");
        Assert.Equal(heading.Size, paired.Size);
        Assert.Equal(heading.LineHeight, paired.LineHeight);
    }
}

public class DesignSizeTests
{
    [Fact]
    public void The_zoom_design_box_agrees_with_the_token_layer()
    {
        // `ZoomAutoPolicy` keeps BCL-only literals so it source-includes cleanly; this is the convergence test ch 00
        // §9.6 asks for, and it is the whole reason `DesignH` was promoted out of a bare literal.
        Assert.Equal(Design.Size.PageMaxW, Design.Size.DesignW);
        Assert.Equal(Design.Size.DesignW, ZoomAutoPolicy.DesignW);
        Assert.Equal(Design.Size.DesignH, ZoomAutoPolicy.DesignH);
    }

    [Fact]
    public void The_rail_widths_agree_with_their_persisted_defaults()
    {
        // A default that drifts from its token is a user who opens an album and sees a rail one step off the design.
        Assert.Equal(Design.Size.RailAlbum, Platform.Keys.DetailAlbumRailWidth.Default);
        Assert.Equal(Design.Size.RailPlaylist, Platform.Keys.DetailPlaylistRailWidth.Default);
    }

    [Fact]
    public void The_thumb_ladder_is_an_eight_step_on_the_four_grid()
    {
        float[] ladder = [Design.Size.Thumb32, Design.Size.Thumb40, Design.Size.Thumb48,
                          Design.Size.Thumb56, Design.Size.Thumb64];
        for (int i = 1; i < ladder.Length; i++) Assert.Equal(8f, ladder[i] - ladder[i - 1]);
    }

    [Fact]
    public void An_icon_button_is_control_height_by_construction()
        => Assert.Equal(Design.Size.ControlH, Controls.IconButtonSize);

    [Fact]
    public void The_media_pill_is_one_step_above_the_control_ladder()
        => Assert.Equal(36f, Controls.PillHeight);

    [Fact]
    public void The_dock_reserves_exactly_its_own_height()
        => Assert.Equal(Design.Dock.BarH, Design.Dock.Reserve);

    [Fact]
    public void The_content_pane_has_exactly_one_rounded_corner()
    {
        var c = Design.Size.ContentPaneCorners;
        Assert.Equal(Radii.Card, c.TopLeft);
        Assert.Equal(0f, c.TopRight);
        Assert.Equal(0f, c.BottomRight);
        Assert.Equal(0f, c.BottomLeft);
    }

    [Fact]
    public void The_two_focus_insets_are_the_named_pair()
    {
        // A sixth arm is a regression unless it is named in the census. These two are the rule.
        Assert.Equal(2f, Design.FocusInsetBordered.Left);
        Assert.Equal(1f, Design.FocusInsetRow.Left);
    }
}

public class DesignColorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_layer_rung_composites_to_the_opaque_rung(bool light)
    {
        // Over(ContentLayer, ShellGround) == ContentSurface, in BOTH spaces. This identity is what lets a fallback or a
        // contrast gate reason about the ladder as a layer OR as a solid, and it is why the two steps are expressed as
        // mix FRACTIONS rather than as hand-mixed greys.
        // The NEUTRAL palette — the only one Wavee ships. The light identity is solved against its #F3F3F3 canvas, so a
        // preset with a different canvas is not what this pins.
        var theme = light ? ThemeKind.Light : ThemeKind.Dark;
        var set = light ? Tok.NeutralPalette.Light : Tok.NeutralPalette.Dark;
        var composed = ColorContrast.Over(Design.Colors.ContentLayerFor(theme), Design.Colors.ShellGroundFor(set, theme));
        var opaque = Design.Colors.ContentSurfaceFor(set, theme);
        Assert.True(Near(composed, opaque), $"{composed} != {opaque}");
    }

    [Fact]
    public void A_striped_row_composes_its_state_rather_than_stacking_two_plates()
    {
        // A row paints ONE Fill. `Over` is associative, so the merged rung composites pixel-identically to painting the
        // two rungs in sequence — which is exactly what the old hand-picked literals were approximating.
        Assert.Equal(ColorContrast.Over(Design.Colors.RowHover, Design.Colors.RowZebra), Design.Colors.RowHoverZebra);
        Assert.Equal(ColorContrast.Over(Design.Colors.RowPressed, Design.Colors.RowZebra), Design.Colors.RowPressedZebra);
    }

    [Fact]
    public void The_zebra_is_quieter_than_the_state_that_lands_on_it()
    {
        // THE invariant. In dark the shell's own zebra is LITERALLY the hover fill, which is why the app overrides it:
        // a stripe that equals hover means the row has no hover at all.
        Assert.True(Design.Colors.RowZebra.A < Design.Colors.RowHover.A,
                    "the stripe must be quieter than the hover that lands on it");
    }

    [Fact]
    public void The_selection_ladder_only_ever_goes_up()
    {
        // The bug this replaced was an INVERSION: hovering the selected row swapped DOWN to a quieter plate, so pointing
        // at the row you are on looked like a deselection. Hover is strictly stronger than rest; press sits between.
        float rest = Alpha(Design.Colors.SelectedRest);
        float hover = Alpha(Design.Colors.SelectedHover);
        float press = Alpha(Design.Colors.SelectedPressed);
        Assert.True(hover > rest, "hovered-selected must be stronger than selected");
        Assert.True(press > rest && press <= hover, "pressed sits above rest and no higher than hover");
    }

    [Fact]
    public void The_neutral_ground_is_never_transparent()
    {
        // Transparent is premultiplied BLACK: cross-fading into or out of it drags the whole ramp toward black, which is
        // what read as "the shell tint goes neutral AND DARKER" at almost every navigation.
        Assert.True(Design.Wash.NeutralGround.A > 0f);
        Assert.Equal(0.03f, Design.Wash.NeutralGround.A, 4);
    }

    [Fact]
    public void On_media_ink_is_theme_invariant()
    {
        // White ink on a dark scrim, in BOTH themes. The one surface that flips is the stage, and it has its own arm.
        Assert.Equal(1f, Design.OnMedia.Ink.R, 3);
        Assert.Equal(1f, Design.OnMedia.Ink.G, 3);
        Assert.Equal(1f, Design.OnMedia.Ink.B, 3);
        Assert.Equal(0f, Design.OnMedia.ScrimRest.R, 3);
    }

    [Fact]
    public void The_ink_plate_rests_above_the_glass_hover()
    {
        // A REST state needs its own edge before the pointer arrives — which is exactly why the stage's way out is
        // ink-plated rather than scrim-plated on a ground that is already 76% black.
        Assert.True(Design.OnMedia.GlassPlate.A > Design.OnMedia.GlassHover.A);
    }

    static float Alpha(in ColorF c) => c.A;
    static bool Near(in ColorF a, in ColorF b)
        => MathF.Abs(a.R - b.R) < 0.01f && MathF.Abs(a.G - b.G) < 0.01f
        && MathF.Abs(a.B - b.B) < 0.01f && MathF.Abs(a.A - b.A) < 0.01f;
}

public class DesignPaletteTests
{
    static Scheme Green => new(0xFF1B5E20, 0xFF2E7D32, 0xFFFFFFFF, 0xFF81C784, 0xFFFFFFFF);
    static Scheme Grey => new(0xFF303030, 0xFF3A3A3A, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF);

    [Fact]
    public void Lift_only_ever_lifts()
    {
        var dim = ColorF.FromRgba(10, 20, 10);
        var lifted = Design.Palette.Lift(dim);
        Assert.True(MathF.Max(lifted.R, MathF.Max(lifted.G, lifted.B)) > MathF.Max(dim.R, MathF.Max(dim.G, dim.B)));
        // Already bright enough ⇒ unchanged. Never darkens.
        var bright = ColorF.FromRgba(250, 250, 250);
        Assert.Equal(bright, Design.Palette.Lift(bright));
    }

    [Fact]
    public void Lift_of_pure_black_is_a_neutral_grey_at_the_target()
    {
        var c = Design.Palette.Lift(ColorF.FromRgba(0, 0, 0));
        Assert.Equal(c.R, c.G, 3);
        Assert.Equal(c.G, c.B, 3);
        Assert.Equal(210f / 255f, c.R, 2);
    }

    [Fact]
    public void Vivid_leaves_a_near_neutral_alone()
    {
        var grey = ColorF.FromRgba(120, 121, 120);
        Assert.Equal(grey, Design.Palette.Vivid(grey));
    }

    [Fact]
    public void Accent_prefers_the_background_roles_over_the_bright_accent_role()
    {
        // The bright-accent role is pure white in every dark grading and pure black in every light one — 100% over 9,316
        // cached gradings. Reading it as "the accent" made the neutral guard fire on EVERY cover, so every Play CTA in
        // the app rendered system blue. The accent is the most SATURATED role, and white has no saturation.
        var accent = Design.Palette.Accent(Green);
        Assert.NotEqual(Design.Palette.ToColor(Green.TextBrightAccent), accent);
        var (_, sat, _) = accent.ToHsv();
        Assert.True(sat > Design.Palette.NeutralS);
    }

    [Fact]
    public void ChromeAccent_falls_back_to_the_system_accent_for_greyscale_art()
    {
        // A black-and-white sleeve keeps the system blue rather than shipping a grey Play button.
        Assert.Equal(Tok.AccentDefault, Design.Palette.ChromeAccent(Grey));
        Assert.NotEqual(Tok.AccentDefault, Design.Palette.ChromeAccent(Green));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PageTone_forces_lightness_and_caps_saturation(bool light)
    {
        // THE CLAMP IS THE POINT: hue from the record, lightness and saturation from the PAGE. Two albums by the same
        // artist must read as the same page in two colours.
        var theme = light ? ThemeKind.Light : ThemeKind.Dark;
        var tone = Design.Palette.PageTone(Green, theme);
        Assert.NotNull(tone);
        var (_, s, l) = Design.Palette.ToHsl(tone!.Value);
        Assert.Equal(light ? Design.Palette.PageToneLightL : Design.Palette.PageToneDarkL, l, 2);
        Assert.True(s <= (light ? Design.Palette.PageToneLightSMax : Design.Palette.PageToneDarkSMax) + 0.001f);
    }

    [Fact]
    public void PageTone_of_greyscale_art_is_the_neutral_tone_and_not_an_invented_hue()
    {
        Assert.Equal(Design.Palette.PageToneNeutralDark, Design.Palette.PageTone(Grey, ThemeKind.Dark));
        Assert.Equal(Design.Palette.PageToneNeutralLight, Design.Palette.PageTone(Grey, ThemeKind.Light));
    }

    [Fact]
    public void PageTone_of_no_grading_is_null_so_the_page_paints_nothing()
        => Assert.Null(Design.Palette.PageTone(null, ThemeKind.Dark));

    [Fact]
    public void The_neutral_fallback_scheme_is_greyscale_on_purpose()
    {
        // A fabricated blue made the fallback the one "cover" in the app with a hue, which is precisely the wrong shape
        // to test chrome against.
        var n = Design.Palette.Neutral;
        foreach (uint role in new[] { n.BackgroundBase, n.BackgroundTintedBase, n.TextBase, n.TextSubdued, n.TextBrightAccent })
        {
            var (_, sat, _) = Design.Palette.ToColor(role).ToHsv();
            Assert.True(sat <= Design.Palette.NeutralS, "role " + role.ToString("X8") + " has a hue");
        }
    }

    [Fact]
    public void DataDotInk_is_a_passthrough_in_dark()
    {
        // The wire colours already ARE the dark-surface answer, and re-grading them would break the one property the
        // Camelot wheel guarantees: harmonically adjacent keys stay adjacent hues.
        const uint yellow = 0xFFFFD400;
        Assert.Equal(Design.Palette.ToColor(yellow), Design.Palette.DataDotInk(yellow, ThemeKind.Dark));
    }

    [Fact]
    public void DataDotInk_darkens_the_light_hue_band_further_than_the_rest()
    {
        // Darkening for a light surface is HUE-DEPENDENT: yellow and cyan sit near the top of the luminance curve and
        // need ~three rungs; red and blue need about one, and darkening them as far as yellow turns the wheel into
        // twelve browns.
        var (_, _, lYellow) = Design.Palette.ToHsl(Design.Palette.DataDotInk(0xFFFFD400, ThemeKind.Light));
        var (_, _, lRed) = Design.Palette.ToHsl(Design.Palette.DataDotInk(0xFFE53935, ThemeKind.Light));
        Assert.Equal(0.30f, lYellow, 2);
        Assert.Equal(0.40f, lRed, 2);
        Assert.True(lYellow < lRed);
    }

    [Fact]
    public void DataDotInk_never_invents_a_hue_for_a_grey()
    {
        var c = Design.Palette.DataDotInk(0xFF808080, ThemeKind.Light);
        Assert.Equal(c.R, c.G, 3);
        Assert.Equal(c.G, c.B, 3);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    [InlineData(210f)]
    [InlineData(359f)]
    public void Hsl_round_trips(float hue)
    {
        var c = Design.Palette.FromHsl(hue, 0.6f, 0.5f);
        var (h, s, l) = Design.Palette.ToHsl(c);
        Assert.Equal(hue, h, 1);
        Assert.Equal(0.6f, s, 2);
        Assert.Equal(0.5f, l, 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hairline_solves_to_its_contrast_target(bool light)
    {
        // The 22-iteration bisection IS the contrast solve; a cheaper approximation changes every identity hairline and
        // every accent-ink digit in the app.
        var theme = light ? ThemeKind.Light : ThemeKind.Dark;
        var ground = light ? ColorF.FromRgba(0xF9, 0xF9, 0xF9) : ColorF.FromRgba(0x28, 0x28, 0x28);
        var ink = Design.Palette.Hairline(ColorF.FromRgba(0x2E, 0x7D, 0x32), theme, ground);
        Assert.True(ColorContrast.Ratio(ink, ground) >= 3.1f);
    }

    [Fact]
    public void TextInk_meets_AA_or_falls_back_to_a_contrast_pick()
    {
        // Some hues cannot hit AA at the saturation cap (a mid-blue on a dark card tops out ≈4.4:1); those fall back so
        // the digits stay readable rather than staying pretty and illegible.
        var ground = ColorF.FromRgba(0x28, 0x28, 0x28);
        var ink = Design.Palette.TextInk(ColorF.FromRgba(0x21, 0x96, 0xF3), ThemeKind.Dark, ground);
        Assert.True(ColorContrast.MeetsAaText(ink, ground));
    }

    [Fact]
    public void ChromeFromPayload_of_nothing_is_the_semantic_accent()
        => Assert.Equal(Tok.AccentDefault, Design.Palette.ChromeFromPayload(0u));
}

public class DesignStageArmTests
{
    [Fact]
    public void The_dark_arm_is_byte_identical_to_the_on_media_ladder()
    {
        // "Dark theme is byte-identical to what shipped" is an executable claim here rather than a promise — and it is
        // what keeps the on-media ladder theme-INVARIANT while the stage alone gets a polarity.
        var dark = Design.StageArm.For(ThemeKind.Dark);
        Assert.Equal(Design.OnMedia.Ink, dark.Ink);
        Assert.Equal(Design.OnMedia.InkSecondary, dark.InkSecondary);
        Assert.Equal(Design.OnMedia.InkTertiary, dark.InkTertiary);
        Assert.Equal(Design.OnMedia.ScrimRest, dark.ScrimRest);
        Assert.Equal(Design.OnMedia.ScrimHover, dark.ScrimHover);
        Assert.Equal(Design.OnMedia.ScrimPressed, dark.ScrimPressed);
        Assert.Equal(Design.OnMedia.Stroke, dark.Stroke);
        Assert.Equal(Design.OnMedia.LightButton, dark.ButtonFill);
        Assert.Equal(Design.OnMedia.LightButtonInk, dark.ButtonInk);
        Assert.Equal(Tok.MediaStage, dark.Veil);
    }

    [Fact]
    public void The_light_arm_mirrors_the_ground_and_keeps_the_alphas()
    {
        // The scrim ALPHAS need no light arm: mixing toward black at a partial alpha destroys far more perceptual
        // luminance than mixing toward white, so the light arm's alpha'd ink clears a HIGHER ratio than dark ships.
        var light = Design.StageArm.For(ThemeKind.Light);
        Assert.Equal(Design.Palette.PageToneNeutralLight, light.Veil);
        Assert.Equal(Tok.MediaStage, light.Ink);
        Assert.Equal(Design.OnMedia.ScrimRest.A, light.ScrimRest.A, 3);
        Assert.Equal(Design.OnMedia.GlassHover.A, light.GlassHover.A, 3);
        Assert.Equal(Design.OnMedia.GlassPlate.A, light.GlassPlate.A, 3);
    }

    [Fact]
    public void The_floor_is_the_veil_in_both_arms()
    {
        // Every comment on the surface already claimed this; the code painted the solid base and flashed near-white
        // under the dark scrim in light theme.
        foreach (var theme in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            var arm = Design.StageArm.For(theme);
            Assert.Equal(arm.Veil, arm.Floor);
        }
    }

    [Fact]
    public void The_hairline_inverts_with_the_ink()
    {
        // A white hairline on a light plate is not a quiet ring, it is an absent one.
        var light = Design.StageArm.For(ThemeKind.Light);
        Assert.Equal(light.Ink.R, light.Stroke.R, 3);
        Assert.Equal(Design.OnMedia.Stroke.A, light.Stroke.A, 3);
    }

    [Fact]
    public void The_stage_accent_is_the_chrome_accent_in_dark_and_a_solved_ink_in_light()
    {
        var chrome = ColorF.FromRgba(0x2E, 0x7D, 0x32);
        Assert.Equal(chrome, Design.StageArm.For(ThemeKind.Dark).AccentFrom(chrome));
        Assert.NotEqual(chrome, Design.StageArm.For(ThemeKind.Light).AccentFrom(chrome));
    }
}

public class DesignMotionTests
{
    [Fact]
    public void The_duration_ladder_is_the_WinUI_ladder_and_nothing_between()
    {
        Assert.Equal(83f, Design.Motion.Faster);
        Assert.Equal(167f, Design.Motion.Fast);
        Assert.Equal(250f, Design.Motion.Standard);
    }

    [Fact]
    public void The_middle_rung_diverges_from_the_engine_token_and_that_is_recorded()
    {
        // Two rungs, near-identical names, different files. The collision is real and load-bearing: matching the bare
        // number to the wrong name gets the wrong duration.
        Assert.NotEqual(Design.Motion.Fast, MotionTok.ControlFast.DurationMs);
        Assert.Equal(Design.Motion.Faster, MotionTok.ControlFaster.DurationMs);
        Assert.Equal(Design.Motion.Standard, MotionTok.ControlNormal.DurationMs);
    }

    [Fact]
    public void Three_scale_tiers_and_nothing_else()
    {
        Assert.Equal(1.02f, Design.Motion.ScaleSubtle.HoverTarget);
        Assert.Equal(0.98f, Design.Motion.ScaleSubtle.PressTarget);
        Assert.Equal(1.04f, Design.Motion.ScaleStandard.HoverTarget);
        Assert.Equal(0.96f, Design.Motion.ScaleStandard.PressTarget);
        Assert.Equal(1.07f, Design.Motion.ScaleEmphatic.HoverTarget);
        Assert.Equal(0.92f, Design.Motion.ScaleEmphatic.PressTarget);
    }

    [Fact]
    public void A_dead_affordance_does_not_answer_the_pointer()
    {
        Assert.Equal(1f, Design.Motion.ScaleStandard.HoverIf(false));
        Assert.Equal(1f, Design.Motion.ScaleStandard.PressIf(false));
    }

    [Fact]
    public void The_entrance_cascade_is_bounded_at_320ms()
    {
        // The source comment said 360; the arithmetic and its test say 320. The code was right, the comment was not.
        Assert.Equal(8, Design.Entrance.StaggerCap);
        Assert.Equal(320f, Design.Entrance.StaggerCap * Design.Motion.StaggerMs);
        Assert.Equal(320f, Design.Entrance.DelayMs(8));
        Assert.Equal(320f, Design.Entrance.DelayMs(50));   // everything past the cap lands together
        Assert.Equal(0f, Design.Entrance.DelayMs(0));
        Assert.Equal(40f, Design.Entrance.DelayMs(1));
    }

    [Fact]
    public void A_negative_index_never_produces_a_negative_delay()
        => Assert.Equal(0f, Design.Entrance.DelayMs(-3));

    [Fact]
    public void The_entrance_recipe_never_changes_shape()
    {
        // Gating an entrance HOOK on the reduced-motion flag changes the hook COUNT between renders and crashes the
        // reconciler the moment the flag flips mid-session — a resize grip flips it. Only the DELAY may move.
        var a = Design.Entrance.Row(0);
        var b = Design.Entrance.Row(4);
        Assert.Equal(a.Channels, b.Channels);
        Assert.Equal(a.Enter, b.Enter);
    }

    [Fact]
    public void The_masthead_stagger_is_its_own_rung()
    {
        // A separate rung for a MECHANICAL reason: the per-item one is capped because a list is unbounded, and this one
        // offsets exactly two authored lines, so the uncapped index × ms spelling is safe.
        Assert.Equal(45f, Design.Motion.MastheadStaggerMs);
        Assert.NotEqual(Design.Motion.StaggerMs, Design.Motion.MastheadStaggerMs);
    }
}

public class DesignHoverGateTests
{
    [Fact]
    public void The_first_sample_is_a_baseline_and_never_a_hover()
    {
        // The engine re-fires a node's move-within at the SAME point when new content lands under a resting cursor —
        // exactly the back-navigation case — so the first sample must never arm.
        var gate = default(Design.HoverMotionGate);
        Assert.False(gate.Observe(new Point2(10f, 10f)));
        Assert.False(gate.Observe(new Point2(10f, 10f)));
    }

    [Fact]
    public void Sub_pixel_jitter_does_not_read_as_movement()
    {
        var gate = default(Design.HoverMotionGate);
        gate.Observe(new Point2(10f, 10f));
        Assert.False(gate.Observe(new Point2(10.3f, 10.2f)));
    }

    [Fact]
    public void A_real_move_arms_it_and_it_stays_armed()
    {
        // A one-shot "has real input happened since mount" latch, not a per-hover-cycle re-check: a page the user is
        // already interacting with must not re-litigate every hover-out/in.
        var gate = default(Design.HoverMotionGate);
        gate.Observe(new Point2(10f, 10f));
        Assert.True(gate.Observe(new Point2(40f, 10f)));
        Assert.True(gate.Observe(new Point2(40f, 10f)));
    }
}

public class DesignNavMotionTests
{
    [Fact]
    public void Direction_is_not_part_of_the_slot_key()
    {
        // Folding it in made a motion-only write on the already-active key look like an activation change, which
        // re-seeded the entrance and re-faded the whole page with no content change at all.
        var a = Design.Nav.SlotKey(new Design.PageSlot(0, "album", "spotify:album:1"));
        var b = Design.Nav.SlotKey(new Design.PageSlot(0, "album", "spotify:album:1"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Every_tab_route_and_argument_gets_its_own_slot()
    {
        Assert.NotEqual(Design.Nav.SlotKey(new Design.PageSlot(0, "search", "abba")),
                        Design.Nav.SlotKey(new Design.PageSlot(0, "search", "beatles")));
        Assert.NotEqual(Design.Nav.SlotKey(new Design.PageSlot(0, "search", "abba")),
                        Design.Nav.SlotKey(new Design.PageSlot(1, "search", "abba")));
        Assert.NotEqual(Design.Nav.SlotKey(new Design.PageSlot(0, "album", null)),
                        Design.Nav.SlotKey(new Design.PageSlot(0, "artist", null)));
    }

    [Fact]
    public void The_separator_cannot_occur_in_a_route_or_an_argument()
        => Assert.Contains('\u001F', Design.Nav.SlotKey(new Design.PageSlot(0, "album", "x")));

    [Fact]
    public void Sidebar_to_sidebar_is_Entrance_and_list_to_album_is_DrillIn()
    {
        Assert.Equal(Design.NavRelation.Entrance, Design.Nav.RelationOf(
            Design.NavSurface.TopLevel, Design.NavSurface.TopLevel, sameKind: false, sameIdentity: false,
            Design.NavTransitionKind.Forward));
        Assert.Equal(Design.NavRelation.DrillIn, Design.Nav.RelationOf(
            Design.NavSurface.TopLevel, Design.NavSurface.Detail, sameKind: false, sameIdentity: false,
            Design.NavTransitionKind.Forward));
        Assert.Equal(Design.NavRelation.DrillIn, Design.Nav.RelationOf(
            Design.NavSurface.Detail, Design.NavSurface.TopLevel, sameKind: false, sameIdentity: false,
            Design.NavTransitionKind.Back));
    }

    [Fact]
    public void Same_kind_different_identity_is_Sibling_including_artist_to_artist()
    {
        Assert.Equal(Design.NavRelation.Sibling, Design.Nav.RelationOf(
            Design.NavSurface.Detail, Design.NavSurface.Detail, sameKind: true, sameIdentity: false,
            Design.NavTransitionKind.Forward));
        Assert.Equal(Design.NavRelation.Fade, Design.Nav.RelationOf(
            Design.NavSurface.TopLevel, Design.NavSurface.TopLevel, sameKind: true, sameIdentity: true,
            Design.NavTransitionKind.Neutral));
    }

    [Fact]
    public void Every_directed_recipe_keeps_enter_and_exit_active()
    {
        foreach (var relation in new[] { Design.NavRelation.Entrance, Design.NavRelation.DrillIn, Design.NavRelation.Sibling })
        foreach (var kind in new[] { Design.NavTransitionKind.Forward, Design.NavTransitionKind.Back })
        {
            var r = Design.Nav.RecipeFor(kind, relation);
            Assert.True(r.Enter.Active);
            Assert.True(r.Exit.Active);
        }
    }

    /// <summary>THE regression guard: every SEQUENCED style (everything but None) must finish the exit leg before the
    /// enter leg starts, on every relation and both directions. This is what the old `DelayMs == 0` test got backwards
    /// — the enter delay must be AT LEAST the exit duration, or two full-bleed pages are briefly both legible (measured:
    /// summed opacity peaking at 1.83 for ~130ms). <c>ExitDelayMs</c> must be the explicit 0f, never null (null inherits
    /// `DelayMs` and pushes the exit out by the same amount, flashing the content card empty).</summary>
    [Fact]
    public void Every_sequenced_style_finishes_the_exit_leg_before_the_enter_leg_starts()
    {
        foreach (var style in new[] { Design.PageMotionStyle.Fluent, Design.PageMotionStyle.Spatial, Design.PageMotionStyle.WinUi, Design.PageMotionStyle.Classic })
        foreach (var relation in new[] { Design.NavRelation.Entrance, Design.NavRelation.DrillIn, Design.NavRelation.Sibling })
        foreach (var kind in new[] { Design.NavTransitionKind.Forward, Design.NavTransitionKind.Back })
        {
            var r = Design.Nav.RecipeFor(style, kind, relation);
            Assert.True(r.Exit.Active);
            Assert.NotNull(r.ExitDynamics);
            Assert.Equal(0f, r.ExitDelayMs);
            Assert.True(r.DelayMs >= r.ExitDynamics!.Value.DurationMs,
                $"{style}/{kind}/{relation}: enter delay {r.DelayMs}ms must be >= the exit duration {r.ExitDynamics!.Value.DurationMs}ms, or the two legs overlap.");
        }
    }

    [Fact]
    public void Fluent_is_a_plain_crossfade_with_no_geometry_on_any_relation()
    {
        foreach (var relation in new[] { Design.NavRelation.Entrance, Design.NavRelation.DrillIn, Design.NavRelation.Sibling })
        foreach (var kind in new[] { Design.NavTransitionKind.Forward, Design.NavTransitionKind.Back })
        {
            var r = Design.Nav.RecipeFor(Design.PageMotionStyle.Fluent, kind, relation);
            Assert.Equal(0f, r.Enter.Dx);
            Assert.Equal(0f, r.Enter.Dy);
            Assert.Equal(1f, r.Enter.Sx);
            Assert.Equal(1f, r.Enter.Sy);
            Assert.Equal(0f, r.Exit.Dx);
            Assert.Equal(0f, r.Exit.Dy);
            Assert.Equal(1f, r.Exit.Sx);
            Assert.Equal(1f, r.Exit.Sy);
        }
    }

    [Fact]
    public void Spatial_entrance_has_no_translation_but_sibling_and_drillin_do()
    {
        var entrance = Design.Nav.RecipeFor(Design.PageMotionStyle.Spatial, Design.NavTransitionKind.Forward, Design.NavRelation.Entrance);
        Assert.Equal(0f, entrance.Enter.Dx);
        Assert.Equal(0f, entrance.Enter.Dy);
        Assert.Equal(1f, entrance.Enter.Sx);

        var drill = Design.Nav.RecipeFor(Design.PageMotionStyle.Spatial, Design.NavTransitionKind.Forward, Design.NavRelation.DrillIn);
        Assert.True(drill.Enter.Sx is > 0f and < 1f);
        Assert.True(drill.Exit.Sx > 1f);

        var sibling = Design.Nav.RecipeFor(Design.PageMotionStyle.Spatial, Design.NavTransitionKind.Forward, Design.NavRelation.Sibling);
        Assert.NotEqual(0f, sibling.Enter.Dx);
    }

    /// <summary>Spatial's Sibling moves ONLY the incoming page — the outgoing page fades in place instead of sliding
    /// away with it (the WinUI-style "both pages move" treatment is <see cref="PageMotionStyle.WinUi"/>'s job).</summary>
    [Fact]
    public void Spatial_sibling_moves_only_the_incoming_page()
    {
        var fwd = Design.Nav.RecipeFor(Design.PageMotionStyle.Spatial, Design.NavTransitionKind.Forward, Design.NavRelation.Sibling);
        var back = Design.Nav.RecipeFor(Design.PageMotionStyle.Spatial, Design.NavTransitionKind.Back, Design.NavRelation.Sibling);
        Assert.Equal(0f, fwd.Exit.Dx);
        Assert.Equal(0f, back.Exit.Dx);
        Assert.Equal(-fwd.Enter.Dx, back.Enter.Dx);
    }

    [Fact]
    public void WinUi_entrance_rises_140_DIP_and_the_outgoing_side_only_fades()
    {
        var fwd = Design.Nav.RecipeFor(Design.PageMotionStyle.WinUi, Design.NavTransitionKind.Forward, Design.NavRelation.Entrance);
        var back = Design.Nav.RecipeFor(Design.PageMotionStyle.WinUi, Design.NavTransitionKind.Back, Design.NavRelation.Entrance);
        Assert.Equal(140f, fwd.Enter.Dy);
        Assert.Equal(0f, fwd.Exit.Dy);
        Assert.Equal(0f, fwd.Exit.Dx);
        Assert.Equal(-140f, back.Enter.Dy);
    }

    [Fact]
    public void WinUi_sibling_moves_both_pages_by_different_amounts()
    {
        var fwd = Design.Nav.RecipeFor(Design.PageMotionStyle.WinUi, Design.NavTransitionKind.Forward, Design.NavRelation.Sibling);
        Assert.Equal(200f, fwd.Enter.Dx);
        Assert.Equal(-150f, fwd.Exit.Dx);
    }

    [Fact]
    public void Classic_is_the_old_fade_through_uniform_across_every_relation()
    {
        foreach (var relation in new[] { Design.NavRelation.Entrance, Design.NavRelation.DrillIn, Design.NavRelation.Sibling })
        {
            var r = Design.Nav.RecipeFor(Design.PageMotionStyle.Classic, Design.NavTransitionKind.Forward, relation);
            Assert.Equal(Expressive.DistBase, r.Enter.Dx);
            Assert.Equal(-Expressive.DistBase, r.Exit.Dx);
        }
    }

    [Fact]
    public void None_is_an_instant_cut_with_exit_still_active()
    {
        foreach (var relation in new[] { Design.NavRelation.Entrance, Design.NavRelation.DrillIn, Design.NavRelation.Sibling })
        {
            var r = Design.Nav.RecipeFor(Design.PageMotionStyle.None, Design.NavTransitionKind.Forward, relation);
            Assert.True(r.Exit.Active);
            Assert.True(r.Dynamics.DurationMs <= 1f);
            Assert.True(r.ExitDynamics!.Value.DurationMs <= 1f);
        }
    }

    [Fact]
    public void DrillIn_is_a_semantic_zoom_not_an_eight_DIP_fade()
    {
        var fwd = Design.Nav.RecipeFor(Design.NavTransitionKind.Forward, Design.NavRelation.DrillIn);
        var back = Design.Nav.RecipeFor(Design.NavTransitionKind.Back, Design.NavRelation.DrillIn);
        Assert.True(fwd.Enter.Sx is > 0f and < 1f);
        Assert.True(fwd.Exit.Sx > 1f);
        Assert.True(back.Enter.Sx > 1f);
        Assert.True(back.Exit.Sx is > 0f and < 1f);
    }

    [Fact]
    public void No_video_safe_recipe_touches_opacity_and_travel_matches_sibling()
    {
        foreach (var kind in new[] { Design.NavTransitionKind.Forward, Design.NavTransitionKind.Back })
        {
            var r = Design.Nav.RecipeForVideoSafe(kind);
            Assert.NotNull(r);
            Assert.Equal(TransitionChannels.Position, r!.Value.Channels);
            Assert.True(r.Value.Exit.Active);
            Assert.Equal(Design.Nav.SiblingDx, MathF.Abs(r.Value.Enter.Dx));
        }
    }

    [Fact]
    public void Neutral_has_no_video_safe_form_and_says_so()
        => Assert.Null(Design.Nav.RecipeForVideoSafe(Design.NavTransitionKind.Neutral));

    [Fact]
    public void AccentFor_a_coverless_page_is_the_payload_hex_not_a_live_track()
        => Assert.Equal(Design.Palette.ChromeFromPayload(0xFFC2185Bu), Detail.AccentFor(null, 0xFFC2185Bu));
}

public class DesignWashTests
{
    [Fact]
    public void A_placement_round_trips_from_window_space_to_node_space()
    {
        // The clipped box plus the node-relative ellipse must describe the SAME ellipse the window-relative pair did,
        // or a resize slides the wash off its own peak.
        var center = new Point2(0.5f, 0.5f);
        var radius = new Point2(0.4f, 0.4f);
        var p = Design.Wash.Resolve(center, radius, 1f);
        float x0 = MathF.Max(0f, center.X - radius.X);
        Assert.Equal(center.X, x0 + p.Center.X * p.W, 3);
        Assert.Equal(radius.X, p.Radius.X * p.W, 3);
    }

    [Fact]
    public void A_box_that_leaves_the_leading_edge_hangs_off_the_trailing_one()
    {
        // …and a FULL-SPAN axis takes LEADING, because Start is also the fill-the-slot arm of the stack arranger.
        Assert.False(Design.Wash.Hero.AnchorRight);        // the hero's ellipse reaches x = 0
        Assert.True(Design.Wash.Weekly.AnchorRight);       // the weekly's does not
        Assert.True(Design.Wash.Mix.AnchorBottom);
    }

    [Fact]
    public void Dark_carries_roughly_twice_the_light_strength()
    {
        // The same colour reads far weaker over the dark ground, and the light ground has less headroom before a wash
        // turns into a smudge.
        Assert.True(Design.Wash.HeroAlpha(light: false) > Design.Wash.HeroAlpha(light: true) * 1.5f);
        Assert.True(Design.Wash.ShelfAlpha(light: false) > Design.Wash.ShelfAlpha(light: true) * 1.5f);
    }

    [Fact]
    public void A_vanishing_stop_keeps_its_own_rgb()
    {
        // Stop interpolation is STRAIGHT-alpha; a premultiplied-black transparent would drag the whole falloff toward
        // black.
        var c = ColorF.FromRgba(0x2E, 0x7D, 0x32);
        var v = Design.Wash.Vanish(c);
        Assert.Equal(c.R, v.R, 4);
        Assert.Equal(0f, v.A);
    }

    [Fact]
    public void The_wash_host_inset_is_the_dock()
        => Assert.Equal(Design.Dock.Reserve, Design.Wash.HostBottomInset);
}

public class DesignTintOwnershipTests
{
    static readonly object PageA = new();
    static readonly object PageB = new();

    [Fact]
    public void A_stray_publish_from_a_page_that_is_not_the_owner_never_lands()
    {
        // KeepAlive keeps an outgoing page mounted and drawing for its whole exit, so its effects can still fire — and
        // unconditionally re-publish its own colour — while the incoming page is already current.
        Assert.Equal(TintOwnership.Outcome.NoWrite,
            TintOwnership.Resolve(PageA, new TintOwnership.Request(PageB, IsClaim: false, Definite: true, HasColor: true)));
    }

    [Fact]
    public void A_claim_with_no_colour_yet_HOLDS_what_is_showing()
    {
        // THE hand-over. This is what stops the chrome dipping to neutral and back between two coloured pages.
        Assert.Equal(TintOwnership.Outcome.WriteHeldColor,
            TintOwnership.Resolve(PageA, new TintOwnership.Request(PageB, IsClaim: true, Definite: false, HasColor: false)));
    }

    [Fact]
    public void A_page_that_has_DECIDED_it_carries_no_colour_writes_neutral()
    {
        // "Washes are off" / "this layout applies no tint" is a decision, not a transient; it eases to neutral like any
        // other colour rather than holding the previous page's.
        Assert.Equal(TintOwnership.Outcome.WriteNeutral,
            TintOwnership.Resolve(PageA, new TintOwnership.Request(PageA, IsClaim: false, Definite: true, HasColor: false)));
    }

    [Fact]
    public void A_refresh_from_the_owner_with_nothing_new_writes_nothing()
        => Assert.Equal(TintOwnership.Outcome.NoWrite,
            TintOwnership.Resolve(PageA, new TintOwnership.Request(PageA, IsClaim: false, Definite: false, HasColor: false)));

    [Fact]
    public void A_known_colour_always_lands_for_the_owner_or_a_claimant()
    {
        Assert.Equal(TintOwnership.Outcome.WriteKnownColor,
            TintOwnership.Resolve(PageA, new TintOwnership.Request(PageA, IsClaim: false, Definite: false, HasColor: true)));
        Assert.Equal(TintOwnership.Outcome.WriteKnownColor,
            TintOwnership.Resolve(PageA, new TintOwnership.Request(PageB, IsClaim: true, Definite: false, HasColor: true)));
    }

    [Fact]
    public void The_owner_is_compared_by_REFERENCE_and_never_by_value()
    {
        // Two pages of the same kind are two owners. A value comparison would let a re-mounted twin steal the slot.
        Assert.Equal(TintOwnership.Outcome.NoWrite,
            TintOwnership.Resolve(new object(), new TintOwnership.Request(new object(), false, true, true)));
    }

    // ── the artist page's tint gate (ch 08 BUG D): "ready" must mean "the art is usable", not "Knows(Overview)" ──────

    [Fact]
    public void A_claim_with_a_cached_grading_writes_the_KNOWN_colour_even_when_the_overview_is_still_unknown()
    {
        // The artist page used to gate the tint's `ready` flag on Knows(Overview) — eight field groups committing at
        // once — so a claim from a page whose ART is already graded (the search/home card's avatar, warmed by an
        // earlier batch) still had nothing to say until the whole overview landed, and the chrome held the PREVIOUS
        // artist's colour for that whole stretch. The gate only needs to know the claim HAS a colour.
        Assert.Equal(TintOwnership.Outcome.WriteKnownColor,
            TintOwnership.Resolve(PageA, new TintOwnership.Request(PageB, IsClaim: true, Definite: false, HasColor: true)));
    }

    [Fact]
    public void A_claim_with_no_cached_grading_still_HOLDS_even_when_the_overview_is_still_unknown()
    {
        // The other half of the same fix: an artist whose art has never been graded yet must not dip the chrome to
        // neutral while its overview is still in flight — the hold is exactly what an overview-agnostic gate needs,
        // so it must survive moving the gate off Knows(Overview).
        Assert.Equal(TintOwnership.Outcome.WriteHeldColor,
            TintOwnership.Resolve(PageA, new TintOwnership.Request(PageB, IsClaim: true, Definite: false, HasColor: false)));
    }
}

public class DesignRevealRampTests
{
    [Fact]
    public void The_ramp_reveals_a_chunk_per_frame()
    {
        Assert.Equal(12, Design.RevealRamp.Chunk);
        Assert.Equal(12, Design.RevealRamp.Next(0, 100));
        Assert.Equal(24, Design.RevealRamp.Next(12, 100));
    }

    [Fact]
    public void The_ramp_is_DONE_once_it_covers_the_realized_band()
    {
        // Past the cap there is nothing left to ramp — the rest of a 5,000-row list is never on screen.
        Assert.Equal(Design.RevealRamp.Done, Design.RevealRamp.Next(Design.RevealRamp.Cap, 5000));
        Assert.Equal(Design.RevealRamp.Done, Design.RevealRamp.Next(0, 8));   // a short list finishes in one chunk
    }

    [Fact]
    public void A_finished_ramp_pays_nothing()
    {
        // Rows scrolled in later never re-shimmer.
        Assert.True(Design.RevealRamp.Revealed(4000, Design.RevealRamp.Done));
        Assert.False(Design.RevealRamp.Revealed(12, 12));
        Assert.True(Design.RevealRamp.Revealed(11, 12));
    }

    // ── G-259: a step after Done wrapped int.MaxValue + 12 negative and blanked every row of a short list ──────────────

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(24)]
    [InlineData(5000)]
    [InlineData(int.MaxValue)]
    public void A_step_after_Done_stays_Done(int visible)
        => Assert.Equal(Design.RevealRamp.Done, Design.RevealRamp.Next(Design.RevealRamp.Done, visible));

    [Fact]
    public void No_input_overflows_or_goes_negative()
    {
        Assert.Equal(Design.RevealRamp.Done, Design.RevealRamp.Next(int.MaxValue - 1, 2));
        Assert.Equal(Design.RevealRamp.Done, Design.RevealRamp.Next(int.MaxValue - Design.RevealRamp.Chunk, 5000));
        Assert.Equal(Design.RevealRamp.Done, Design.RevealRamp.Next(Design.RevealRamp.Cap + 1, int.MaxValue));
        // A negative count is "nothing revealed yet": it saturates to 0 and takes one ordinary chunk.
        Assert.Equal(Design.RevealRamp.Chunk, Design.RevealRamp.Next(int.MinValue, 100));
        Assert.Equal(Design.RevealRamp.Chunk, Design.RevealRamp.Next(-1, 100));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]     // al3, the Single the gate shot blank
    [InlineData(10)]    // al5
    [InlineData(12)]    // al2
    [InlineData(13)]
    [InlineData(24)]    // the top of the window the bug blanked
    [InlineData(25)]
    [InlineData(60)]
    [InlineData(4000)]
    public void A_list_ramped_from_the_armed_chunk_reaches_Done_and_holds_it(int visible)
    {
        // The table arms at Chunk, then the ticker steps once per frame until Done and may step again before it unmounts.
        int reveal = Design.RevealRamp.Chunk;
        int steps = 0;
        while (reveal != Design.RevealRamp.Done)
        {
            int next = Design.RevealRamp.Next(reveal, visible);
            Assert.True(next > reveal, $"the ramp must only move forward (visible={visible}, {reveal} → {next})");
            reveal = next;
            Assert.True(++steps <= Design.RevealRamp.Cap / Design.RevealRamp.Chunk, $"the ramp must finish near the cap (visible={visible})");
        }
        for (int extra = 0; extra < 3; extra++) reveal = Design.RevealRamp.Next(reveal, visible);
        Assert.Equal(Design.RevealRamp.Done, reveal);
        for (int row = 0; row < Math.Min(visible, 100); row++) Assert.True(Design.RevealRamp.Revealed(row, reveal));
    }

    [Fact]
    public void Any_start_finishes_within_Cap_over_Chunk_steps()
    {
        foreach (int start in new[] { int.MinValue, -1, 0, 1, 11, 12, 47, 59, 60, 61, int.MaxValue - 1 })
            foreach (int visible in new[] { int.MinValue, 0, 1, 12, 24, 59, 60, 61, int.MaxValue })
            {
                int reveal = start;
                int steps = 0;
                while (reveal != Design.RevealRamp.Done)
                {
                    reveal = Design.RevealRamp.Next(reveal, visible);
                    Assert.True(reveal >= 0, $"never negative (start={start}, visible={visible})");
                    Assert.True(++steps <= Design.RevealRamp.Cap / Design.RevealRamp.Chunk, $"terminates (start={start}, visible={visible})");
                }
            }
    }
}

public class DesignDecodeScaleTests
{
    [Fact]
    public void Nothing_to_decode_returns_zero()
    {
        Assert.Equal(0, Design.ImageDecodeScale.For(0f, 1f));
        Assert.Equal(0, Design.ImageDecodeScale.For(float.NaN, 1f));
        Assert.Equal(0, Design.ImageDecodeScale.For(-4f, 1f));
    }

    [Fact]
    public void A_corrupt_scale_falls_back_to_1x_rather_than_propagating()
    {
        Assert.Equal(Design.ImageDecodeScale.For(100f, 1f), Design.ImageDecodeScale.For(100f, 0f));
        Assert.Equal(Design.ImageDecodeScale.For(100f, 1f), Design.ImageDecodeScale.For(100f, float.NaN));
    }

    [Fact]
    public void The_budget_buckets_up_to_the_grid_and_clamps_at_the_ceiling()
    {
        // A rare, ladder-stepped zoom still leaves the ambient scale a float; bucketing keeps the decode cache keyed on
        // a handful of stable sizes instead of thrashing on float noise.
        Assert.Equal(0, Design.ImageDecodeScale.For(100f, 1f) % Design.ImageDecodeScale.BucketPx);
        Assert.True(Design.ImageDecodeScale.For(100f, 1.01f) >= 104);
        Assert.Equal(Design.ImageDecodeScale.Ceiling, Design.ImageDecodeScale.For(4000f, 2.5f));
    }

    [Fact]
    public void A_scaled_budget_is_never_smaller_than_the_unscaled_one()
        => Assert.True(Design.ImageDecodeScale.For(188f, 1.5f) > Design.ImageDecodeScale.For(188f, 1f));
}

// A2 (G-259 follow-up): Controls.cs's Artwork unscaled branch already has a final device-pixel edge (the laid-out DIP
// size) and only needs the CACHE KEY to land on the same grid a scaled decode would — this is that standalone rounding
// rule, exercised with no engine/UI dependency.
public class DesignDecodeScaleBucketTests
{
    [Fact]
    public void Rounds_up_to_the_next_multiple_of_the_bucket()
    {
        Assert.Equal(128, Design.ImageDecodeScale.Bucket(121));
        Assert.Equal(8, Design.ImageDecodeScale.Bucket(1));
        Assert.Equal(304, Design.ImageDecodeScale.Bucket(300));   // 300 is not a multiple of 8
    }

    [Fact]
    public void Is_idempotent_on_an_exact_multiple()
    {
        Assert.Equal(128, Design.ImageDecodeScale.Bucket(128));
        Assert.Equal(Design.ImageDecodeScale.BucketPx, Design.ImageDecodeScale.Bucket(Design.ImageDecodeScale.BucketPx));
        Assert.Equal(640, Design.ImageDecodeScale.Bucket(640));
    }

    [Fact]
    public void Never_returns_zero_for_a_positive_input()
    {
        foreach (int px in new[] { 1, 2, 7, 8, 9, 127, 128, 129, 4000 })
            Assert.True(Design.ImageDecodeScale.Bucket(px) > 0, $"px={px}");
    }
}

public class DesignMorphKeyTests
{
    [Fact]
    public void The_key_is_the_route_key_the_card_navigates_with()
    {
        Assert.Equal("album:spotify:album:1", Design.MorphKeys.For(EntityKind.Album, "spotify:album:1"));
        Assert.Equal("pl:spotify:playlist:2", Design.MorphKeys.For(EntityKind.Playlist, "spotify:playlist:2"));
    }

    [Fact]
    public void Everything_else_has_no_Hero_key()
    {
        // The liked collection has no uri and artist covers are circular; both were deferred, and null is how the
        // convention says so.
        Assert.Null(Design.MorphKeys.For(EntityKind.Artist, "spotify:artist:3"));
        Assert.Null(Design.MorphKeys.For(EntityKind.Album, null));
        Assert.Null(Design.MorphKeys.For(EntityKind.Album, ""));
    }
}
