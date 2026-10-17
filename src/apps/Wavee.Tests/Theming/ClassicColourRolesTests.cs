// ── Wavee.Tests/Theming/ClassicColourRolesTests.cs — Classic's colour roles are today's `Design` colours (T2b) ──────────────
//
// Classic renders pixel-identical to before T2b because every colour a view reads through `R.Layer`, `R.Ink`, `R.OnMedia`,
// `R.StageArm` / `StageInk`, `R.Wash` and `R.Lyrics` equals what it read from `Design.Colors`, `Design.Accent`, `Design.OnMedia`,
// `Design.StageArm`, `Design.Wash` and the `Tok.Theme` branches. This file pins that: each role against the formula it replaced,
// WRITTEN OUT HERE as the old code wrote it (the old `Design` members are gone), once per theme kind.
//
// NO TOKEN CHANGES HERE. `Tok` is process-wide and this assembly runs its facts in parallel, so nothing installs a definition or
// flips the kind. A role whose recipe reads only fixed inputs, the palette's per-kind token sets (`Tok.Palette.Light` / `.Dark`) or
// the shell palettes is pinned for BOTH kinds; a role that reads the engine's LIVE tokens (`Tok.FillSubtleTertiary`, the accent
// ramp, `Tok.FillCardSecondary`) can only be asked for the kind the process is in, so it is pinned for that kind here and for the
// other in the shell process, where the kind may flip (`Wavee.ShellTests\ColourRolesTests.cs`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Wavee.Theming.Roles;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ClassicColourRolesTests
{
    // ── the old formulas ─────────────────────────────────────────────────────────────────────────────────────────────────

    // `Design.Colors.ActiveShell`, for a kind: the shell palette of the active definition.
    static ShellPalette Shell(ThemeKind k) => k == ThemeKind.Light ? Tok.Palette.LightShell : Tok.Palette.DarkShell;

    static TokenSet Set(ThemeKind k) => k == ThemeKind.Light ? Tok.Palette.Light : Tok.Palette.Dark;

    // The constants `Design.Colors` derived the ground and the content step from.
    const float DarkGroundL = 0.125f;
    const float DarkContentLift = (40f / 255f - DarkGroundL) / (1f - DarkGroundL);
    const float LightGroundL = 243f / 255f;
    const float LightGroundDrop = (LightGroundL - 237f / 255f) / LightGroundL;
    const float LightContentLayerA = (249f / 255f - 237f / 255f) / (1f - 237f / 255f);

    static ColorF OldShellGround(ThemeKind k) => k == ThemeKind.Light
        ? ColorRamp.Darken(Set(k).FillSolidBase, LightGroundDrop)
        : Set(k).FillSolidBase;

    static ColorF OldContentSurface(ThemeKind k) => k == ThemeKind.Light
        ? Set(k).FillSolidTertiary
        : ColorRamp.Lighten(Set(k).FillSolidBase, DarkContentLift);

    static ColorF OldContentLayer(ThemeKind k) => ColorF.FromRgba(0xFF, 0xFF, 0xFF,
        (byte)((k == ThemeKind.Light ? LightContentLayerA : DarkContentLift) * 255f + 0.5f));

    static ColorF OldZebra(ThemeKind k) => k == ThemeKind.Light ? Shell(k).RowZebra : Tok.FillSubtleTertiary;

    // `Design.OnMedia`'s literals.
    static readonly ColorF ScrimHover = ColorF.FromRgba(0, 0, 0, 190);
    static readonly ColorF ScrimPressed = ColorF.FromRgba(0, 0, 0, 220);
    static readonly ColorF OnMediaStroke = ColorF.FromRgba(255, 255, 255, 58);

    // `Design.StageArm`, for a kind (its `Dark` flag was `theme == ThemeKind.Dark`).
    static ColorF ArmVeil(ThemeKind k) => k == ThemeKind.Dark ? Tok.MediaStage : ColorScience.PageToneNeutralLight;
    static ColorF ArmInk(ThemeKind k) => k == ThemeKind.Dark ? Tok.OnMediaPrimary : Tok.MediaStage;

    /// <summary>One role, the formula it replaced, and whether that formula depends only on the kind (true) or also on live tokens
    /// that belong to the process's own kind (false).</summary>
    public sealed record Row(string Name, ColorRole Role, Func<ThemeKind, ColorF> Old, bool KindExact);

    static Row Pin(string name, ColorRole role, Func<ThemeKind, ColorF> old, bool kindExact = true) => new(name, role, old, kindExact);

    static readonly Row[] Rows = BuildRows();

    static Row[] BuildRows()
    {
        var layer = ClassicRoles.Table.Layer;
        var ink = ClassicRoles.Table.Ink;
        var om = ClassicRoles.Table.OnMedia;
        var arm = ClassicRoles.Table.StageArm;
        var wash = ClassicRoles.Table.Wash;
        var lyrics = ClassicRoles.Table.Lyrics;
        return
        [
            // ── Design.Colors ──
            Pin("Layer.Toolbar", layer.Toolbar, k => Shell(k).Toolbar),
            Pin("Layer.Sidebar", layer.Sidebar, k => Shell(k).Sidebar),
            Pin("Layer.PlayerBar", layer.PlayerBar, k => Shell(k).PlayerBar),
            Pin("Layer.FileArea", layer.FileArea, k => Shell(k).FileArea),
            Pin("Layer.Content", layer.Content, k => Shell(k).Content),
            Pin("Layer.ContentAlt", layer.ContentAlt, k => Shell(k).ContentAlt),
            Pin("Layer.ShellGround", layer.ShellGround, OldShellGround),
            Pin("Layer.ContentSurface", layer.ContentSurface, OldContentSurface),
            Pin("Layer.ContentLayer", layer.ContentLayer, OldContentLayer),
            Pin("Layer.FloatingChrome", layer.FloatingChrome, OldShellGround),
            Pin("Layer.FloatingPane", layer.FloatingPane, OldContentSurface),
            Pin("Layer.RowZebra", layer.RowZebra, OldZebra, kindExact: false),
            Pin("Layer.RowHover", layer.RowHover, k => Shell(k).RowHover),
            Pin("Layer.RowPressed", layer.RowPressed, k => Shell(k).RowPressed),
            Pin("Layer.RowHoverZebra", layer.RowHoverZebra, k => ColorContrast.Over(Shell(k).RowHover, OldZebra(k)), kindExact: false),
            Pin("Layer.RowPressedZebra", layer.RowPressedZebra, k => ColorContrast.Over(Shell(k).RowPressed, OldZebra(k)), kindExact: false),
            Pin("Layer.SelectedRest", layer.SelectedRest, _ => Tok.AccentSubtle, kindExact: false),
            Pin("Layer.SelectedHover", layer.SelectedHover, _ => ColorContrast.Over(Tok.FillSubtleSecondary, Tok.AccentSubtle), kindExact: false),
            Pin("Layer.SelectedPressed", layer.SelectedPressed, _ => ColorContrast.Over(Tok.FillSubtleTertiary, Tok.AccentSubtle), kindExact: false),
            Pin("Layer.ChromeHover", layer.ChromeHover, _ => Tok.FillSubtleSecondary, kindExact: false),
            Pin("Layer.ChromePressed", layer.ChromePressed, _ => Tok.FillSubtleTertiary, kindExact: false),
            Pin("Layer.Badge", layer.Badge, _ => Tok.AccentDefault, kindExact: false),
            // `Design.ArtworkPlaceholder{Dark,Light}`.
            Pin("Layer.ArtPlaceholder", layer.ArtPlaceholder, k => k == ThemeKind.Dark ? ColorF.FromRgba(0x2A, 0x2A, 0x2A) : ColorF.FromRgba(0xF2, 0xF2, 0xF2)),
            // The old `Concert.UI.DarkOr(r, g, b)` (the branches are these two roles since T10b3).
            Pin("Layer.EventPlate", layer.EventPlate, k => k == ThemeKind.Dark ? ColorF.FromRgba(0x1C, 0x1C, 0x1E) : Tok.FillCardSecondary, kindExact: false),
            Pin("Layer.EventHeroPlate", layer.EventHeroPlate, k => k == ThemeKind.Dark ? ColorF.FromRgba(0x1B, 0x1B, 0x1D) : Tok.FillCardSecondary, kindExact: false),

            // ── Design.Accent and the premium green ──
            Pin("Ink.AccentAction", ink.AccentAction, _ => Tok.AccentDefault, kindExact: false),
            Pin("Ink.AccentSelection", ink.AccentSelection, _ => Tok.AccentDefault, kindExact: false),
            Pin("Ink.AccentDecor", ink.AccentDecor, _ => Tok.AccentTextPrimary, kindExact: false),
            Pin("Ink.PremiumText", ink.PremiumText, k => k == ThemeKind.Light ? Tok.SystemFillSuccess : ColorF.FromRgba(0x1D, 0xB9, 0x54), kindExact: false),

            // ── Design.OnMedia (theme-INVARIANT by contract) ──
            Pin("OnMedia.ScrimRest", om.ScrimRest, _ => Tok.MediaScrim),
            Pin("OnMedia.ScrimHover", om.ScrimHover, _ => ScrimHover),
            Pin("OnMedia.ScrimPressed", om.ScrimPressed, _ => ScrimPressed),
            Pin("OnMedia.CoverScrim", om.CoverScrim, _ => ColorF.FromRgba(0, 0, 0, 110)),
            Pin("OnMedia.Stroke", om.Stroke, _ => OnMediaStroke),
            Pin("OnMedia.Ink", om.Ink, _ => Tok.OnMediaPrimary),
            Pin("OnMedia.InkSecondary", om.InkSecondary, _ => Tok.OnMediaSecondary),
            Pin("OnMedia.InkTertiary", om.InkTertiary, _ => Tok.OnMediaTertiary),
            Pin("OnMedia.GlassRest", om.GlassRest, _ => ColorF.Transparent),
            Pin("OnMedia.GlassHover", om.GlassHover, _ => Tok.OnMediaPrimary with { A = 0.10f }),
            Pin("OnMedia.GlassPressed", om.GlassPressed, _ => Tok.OnMediaPrimary with { A = 0.16f }),
            Pin("OnMedia.GlassPlate", om.GlassPlate, _ => Tok.OnMediaPrimary with { A = 0.14f }),
            Pin("OnMedia.GlassPlateHover", om.GlassPlateHover, _ => Tok.OnMediaPrimary with { A = 0.22f }),
            Pin("OnMedia.GlassPlatePressed", om.GlassPlatePressed, _ => Tok.OnMediaPrimary with { A = 0.28f }),
            Pin("OnMedia.BackdropDim", om.BackdropDim, _ => ColorF.FromRgba(8, 8, 10) with { A = 0.45f }),
            Pin("OnMedia.SpotlightInner", om.SpotlightInner, _ => ColorF.FromRgba(255, 255, 255, 46)),
            Pin("OnMedia.SpotlightMid", om.SpotlightMid, _ => ColorF.FromRgba(255, 255, 255, 20)),
            Pin("OnMedia.LightButton", om.LightButton, _ => Tok.OnMediaPrimary),
            Pin("OnMedia.LightButtonHover", om.LightButtonHover, _ => ColorRamp.Darken(Tok.OnMediaPrimary, 0.08f)),
            Pin("OnMedia.LightButtonPressed", om.LightButtonPressed, _ => ColorRamp.Darken(Tok.OnMediaPrimary, 0.157f)),
            Pin("OnMedia.LightButtonInk", om.LightButtonInk, _ => Tok.MediaStage),

            // ── Design.StageArm (the dark arm is the on-media ladder verbatim; the light arm mirrors the ground) ──
            Pin("StageArm.Veil", arm.Veil, ArmVeil),
            Pin("StageArm.Floor", arm.Floor, ArmVeil),
            Pin("StageArm.Ink", arm.Ink, ArmInk),
            Pin("StageArm.InkSecondary", arm.InkSecondary, k => k == ThemeKind.Dark ? Tok.OnMediaSecondary : Tok.MediaStage with { A = Tok.OnMediaSecondary.A }),
            Pin("StageArm.InkTertiary", arm.InkTertiary, k => k == ThemeKind.Dark ? Tok.OnMediaTertiary : Tok.MediaStage with { A = Tok.OnMediaTertiary.A }),
            Pin("StageArm.GlassRest", arm.GlassRest, _ => ColorF.Transparent),
            Pin("StageArm.GlassHover", arm.GlassHover, k => ArmInk(k) with { A = 0.10f }),
            Pin("StageArm.GlassPressed", arm.GlassPressed, k => ArmInk(k) with { A = 0.16f }),
            Pin("StageArm.GlassPlate", arm.GlassPlate, k => ArmInk(k) with { A = 0.14f }),
            Pin("StageArm.GlassPlateHover", arm.GlassPlateHover, k => ArmInk(k) with { A = 0.22f }),
            Pin("StageArm.GlassPlatePressed", arm.GlassPlatePressed, k => ArmInk(k) with { A = 0.28f }),
            Pin("StageArm.ScrimRest", arm.ScrimRest, k => k == ThemeKind.Dark ? Tok.MediaScrim : ArmVeil(k) with { A = Tok.MediaScrim.A }),
            Pin("StageArm.ScrimHover", arm.ScrimHover, k => k == ThemeKind.Dark ? ScrimHover : ArmVeil(k) with { A = ScrimHover.A }),
            Pin("StageArm.ScrimPressed", arm.ScrimPressed, k => k == ThemeKind.Dark ? ScrimPressed : ArmVeil(k) with { A = ScrimPressed.A }),
            Pin("StageArm.Stroke", arm.Stroke, k => k == ThemeKind.Dark ? OnMediaStroke : ArmInk(k) with { A = OnMediaStroke.A }),
            Pin("StageArm.ButtonFill", arm.ButtonFill, k => k == ThemeKind.Dark ? Tok.OnMediaPrimary : ArmInk(k)),
            Pin("StageArm.ButtonFillHover", arm.ButtonFillHover, k => k == ThemeKind.Dark
                ? ColorRamp.Darken(Tok.OnMediaPrimary, 0.08f) : ColorRamp.Lighten(ArmInk(k), 0.08f)),
            Pin("StageArm.ButtonFillPressed", arm.ButtonFillPressed, k => k == ThemeKind.Dark
                ? ColorRamp.Darken(Tok.OnMediaPrimary, 0.157f) : ColorRamp.Lighten(ArmInk(k), 0.157f)),
            Pin("StageArm.ButtonInk", arm.ButtonInk, k => k == ThemeKind.Dark ? Tok.MediaStage : ArmVeil(k)),
            Pin("StageArm.SkeletonBar", arm.SkeletonBar, k => ArmInk(k) with { A = 0.12f }),
            Pin("StageArm.Plate", arm.Plate, k => ArmInk(k) with { A = 0.045f }),
            Pin("StageArm.PlateHover", arm.PlateHover, k => ArmInk(k) with { A = 0.075f }),

            // ── Design.Wash.NeutralGround and the lyrics bloom ──
            Pin("Wash.NeutralGround", wash.NeutralGround, k => OldShellGround(k) with { A = 0.03f }),
            Pin("Lyrics.BloomOnMedia", lyrics.BloomOnMedia, k => k == ThemeKind.Dark ? ArmInk(k) : ArmVeil(k)),
        ];
    }

    public static TheoryData<string> RoleNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var row in Rows) data.Add(row.Name);
            return data;
        }
    }

    static Row Find(string name) => Rows.First(r => r.Name == name);

    [Theory]
    [MemberData(nameof(RoleNames))]
    public void A_colour_role_equals_the_formula_it_replaced_for_the_kind_the_process_is_in(string name)
    {
        var row = Find(name);
        var kind = Tok.Theme;
        Assert.Equal(row.Old(kind), row.Role.Resolve(kind));
        Assert.Equal(row.Old(kind), row.Role.Now);
    }

    [Theory]
    [MemberData(nameof(RoleNames))]
    public void A_colour_role_that_reads_only_the_kind_equals_the_formula_for_both_kinds(string name)
    {
        var row = Find(name);
        if (!row.KindExact) return;   // reads live tokens: pinned for the process's own kind above, and for both in the shell process
        foreach (var kind in new[] { ThemeKind.Light, ThemeKind.Dark })
            Assert.Equal(row.Old(kind), row.Role.Resolve(kind));
    }

    [Fact]
    public void The_facades_read_the_same_roles_for_the_active_kind()
    {
        var kind = Tok.Theme;
        Assert.Equal(OldZebra(kind), R.Layer.RowZebra);
        Assert.Equal(Shell(kind).RowHover, R.Layer.RowHover);
        Assert.Equal(Shell(kind).RowPressed, R.Layer.RowPressed);
        Assert.Equal(OldShellGround(kind), R.Layer.ShellGround);
        Assert.Equal(OldShellGround(kind), R.Layer.FloatingChrome);
        Assert.Equal(OldContentSurface(kind), R.Layer.FloatingPane);
        Assert.Equal(Tok.AccentSubtle, R.Layer.SelectedRest);
        Assert.Equal(Tok.AccentTextPrimary, R.Ink.AccentDecor);
        Assert.Equal(Tok.MediaScrim, R.OnMedia.ScrimRest);
        Assert.Equal(ScrimHover, R.OnMedia.ScrimHover);
        Assert.Equal(OnMediaStroke, R.OnMedia.Stroke);
        Assert.Equal(OldShellGround(kind) with { A = 0.03f }, R.Wash.NeutralGround);
        Assert.Equal(0.08f, R.OnMedia.LightHoverDrop);
        Assert.Equal(0.157f, R.OnMedia.LightPressedDrop);
    }

    [Fact]
    public void The_stage_facade_is_the_arm_of_the_active_kind()
    {
        var kind = Tok.Theme;
        Assert.Equal(kind == ThemeKind.Dark, StageInk.IsDark);
        Assert.Equal(ArmVeil(kind), StageInk.Veil);
        Assert.Equal(ArmVeil(kind), StageInk.Floor);
        Assert.Equal(ArmInk(kind), StageInk.Ink);
        Assert.Equal(R.StageArm.For(kind).Plate, StageInk.Plate);
        Assert.Equal(R.StageArm.For(kind).Card, StageInk.Card);
        Assert.Equal(R.StageArm.For(kind).Dark, StageInk.Arm(kind).Dark);
    }

    // ── the stage arm's polarity, the card and the accent ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_stage_is_dark_exactly_where_the_theme_is_dark()
    {
        Assert.True(R.StageArm.For(ThemeKind.Dark).Dark);
        Assert.False(R.StageArm.For(ThemeKind.Light).Dark);
    }

    [Fact]
    public void The_stage_card_is_the_in_app_base_recipe_in_dark_and_its_light_twin_in_light()
    {
        Assert.Equal(AcrylicSpec.InAppBase, R.StageArm.For(ThemeKind.Dark).Card);
        Assert.Equal(
            new AcrylicSpec(ColorF.FromRgba(0xF3, 0xF3, 0xF3), 0f, 30f, 0.02f, 0.90f, ColorF.FromRgba(0xEE, 0xEE, 0xEE)),
            R.StageArm.For(ThemeKind.Light).Card);
    }

    [Fact]
    public void The_stage_accent_is_the_chrome_accent_in_dark_and_a_solved_ink_in_light()
    {
        var chrome = ColorF.FromRgba(0x2E, 0x7D, 0x32);
        Assert.Equal(chrome, R.StageArm.For(ThemeKind.Dark).AccentFrom(chrome));
        var ground = ColorContrast.Over(ColorScience.PageToneNeutralLight with { A = 0.46f }, Tok.OnMediaPrimary);
        Assert.Equal(ColorScience.TextInk(chrome, ThemeKind.Light, ground), R.StageArm.For(ThemeKind.Light).AccentFrom(chrome));
        Assert.Equal(0.46f, R.StageArm.For(ThemeKind.Light).ScrimBaseA);
    }

    [Fact]
    public void The_gallery_tile_wash_is_stronger_where_the_stage_is_dark()
    {
        Assert.Equal(0.12f, R.StageArm.For(ThemeKind.Dark).SelectedTileWash);
        Assert.Equal(0.08f, R.StageArm.For(ThemeKind.Light).SelectedTileWash);
    }

    // ── the wash roles: strengths per polarity, and the recipes ──────────────────────────────────────────────────────────

    [Fact]
    public void The_wash_strengths_are_the_literals_the_call_sites_branched_on()
    {
        var w = ClassicRoles.Table.Wash;
        Assert.Equal(new ModePair<float>(0.055f, 0.10f), w.HeroAlpha);        // Design.Wash.HeroAlphaLight / HeroAlphaDark
        Assert.Equal(new ModePair<float>(0.05f, 0.085f), w.ShelfAlpha);       // ShelfAlphaLight / ShelfAlphaDark
        Assert.Equal(new ModePair<float>(0.16f, 0.24f), w.HeroVeilPull);      // Controls.ArtistHeroVeil: light ? 0.16 : 0.24
        Assert.Equal(new ModePair<float>(0.42f, 0.78f), w.HeroVeilPeak);      // light ? 0.42 : 0.78
        Assert.Equal(new ModePair<float>(0.10f, 0.18f), w.CoverMix);          // ClassicHeroWash.Of: dark ? 0.18 : 0.10
        Assert.Equal(new ModePair<float>(0.18f, 0.30f), w.ConcertMediaMix);   // Concert.SplitHero: dark ? 0.30 : 0.18
        Assert.Equal(new ModePair<float>(0.30f, 0.20f), w.PageTonePlaneAlpha);   // CoverPageTonePlane.PlaneAlphaLight / Dark
        Assert.Equal(new ModePair<float>(0.20f, 0.30f), w.BlendWashTop);      // CoverArtistBlendWash: light ? 0.20 : 0.30
        Assert.Equal(new ModePair<float>(0.06f, 0.08f), w.BlendWashBoundary); // light ? 0.06 : 0.08
        Assert.Equal(new ModePair<float>(0.05f, 0.14f), w.ShellTintAlpha);    // CoverShellTintBinder.TintAlphaLight / Dark
    }

    [Fact]
    public void Dark_carries_roughly_twice_the_light_strength_of_a_wash()
    {
        var w = ClassicRoles.Table.Wash;
        Assert.True(w.HeroAlpha.Dark > w.HeroAlpha.Light * 1.5f);
        Assert.True(w.ShelfAlpha.Dark > w.ShelfAlpha.Light * 1.5f);
    }

    [Fact]
    public void The_lyrics_ink_numbers_are_the_literals_the_seam_carried()
    {
        var l = ClassicRoles.Table.Lyrics;
        Assert.Equal(0.92f, l.ThemePlateAlpha);
        Assert.Equal(0.55f, l.RingTrackThemeAlpha);
        Assert.Equal(0.30f, l.RingTrackOnMediaAlpha);
        Assert.Equal(new ModePair<float>(0.5f, 1f), l.BloomScale);   // half survives on the light stage
    }

    static Scheme Green => new(0xFF1B5E20, 0xFF2E7D32, 0xFFFFFFFF, 0xFF81C784, 0xFFFFFFFF);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_shell_tint_arms_are_the_two_roles_of_the_grading(bool light)
    {
        // LIGHT lifts the cover's TEXT role to a 5% whisper; DARK takes the tinted background role at 14%.
        var kind = light ? ThemeKind.Light : ThemeKind.Dark;
        var scheme = Green;
        ColorF expected = light
            ? ColorScience.Lift(ColorScience.ToColor(scheme.TextBase)) with { A = 0.05f }
            : ColorScience.TintedDark(scheme) with { A = 0.14f };
        Assert.Equal(expected, ClassicRoles.Table.Wash.ShellTintFromScheme(kind, scheme));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_page_tone_role_is_the_clamped_tone_for_the_kind(bool light)
    {
        var kind = light ? ThemeKind.Light : ThemeKind.Dark;
        var hue = ColorScience.Accent(Green);
        Assert.Equal(ColorScience.PageToneFromHue(hue, kind), ClassicRoles.Table.Wash.PageToneFromHue(kind, hue));
        var grey = ColorF.FromRgba(0x80, 0x80, 0x80);
        Assert.Equal(light ? ColorScience.PageToneNeutralLight : ColorScience.PageToneNeutralDark,
                     ClassicRoles.Table.Wash.PageToneFromHue(kind, grey));
    }

    [Fact]
    public void The_blend_wash_lifts_the_hue_in_light_and_sinks_it_to_the_dark_background_in_dark()
    {
        var wash = ClassicRoles.Table.Wash;
        Scheme? graded = Green;

        var light = wash.BlendWashLadder(ThemeKind.Light, graded, 0u, true);
        Assert.Equal(AccentLadder.Rung.Graded, light.Rung);
        Assert.Equal(ColorScience.Lift(ColorScience.Accent(Green)), light.Color);

        var dark = wash.BlendWashLadder(ThemeKind.Dark, graded, 0u, true);
        Assert.Equal(AccentLadder.Rung.Graded, dark.Rung);
        Assert.Equal(ColorScience.BackgroundDark(Green), dark.Color);

        // No grading yet, a wire colour: LIGHT lifts it, DARK sinks it to the brightness the grading will settle on.
        var lightPayload = wash.BlendWashLadder(ThemeKind.Light, null, 0xFFE0A030u, false);
        Assert.Equal(AccentLadder.Rung.Payload, lightPayload.Rung);
        Assert.Equal(ColorScience.Lift(ColorScience.ToColor(0xFFE0A030u)), lightPayload.Color);
        var darkPayload = wash.BlendWashLadder(ThemeKind.Dark, null, 0xFFE0A030u, false);
        Assert.Equal(AccentLadder.Rung.Payload, darkPayload.Rung);
        Assert.Equal(ColorScience.DarkFromPayload(0xFFE0A030u), darkPayload.Color);

        // Nothing at all and nothing better to come: the neutral rung of the kind.
        var darkDefault = wash.BlendWashLadder(ThemeKind.Dark, null, 0u, true);
        Assert.Equal(AccentLadder.Rung.Default, darkDefault.Rung);
        Assert.Equal(ColorScience.BackgroundDark(ColorScience.Neutral), darkDefault.Color);
        var lightDefault = wash.BlendWashLadder(ThemeKind.Light, null, 0u, true);
        Assert.Equal(AccentLadder.Rung.Default, lightDefault.Rung);
        Assert.Equal(Tok.AccentDefault, lightDefault.Color);
    }

    [Theory]
    [InlineData(0xFFFFD400u)]
    [InlineData(0xFF3F51B5u)]
    public void The_data_dot_role_is_a_passthrough_in_dark_and_a_regraded_ink_in_light(uint argb)
    {
        var dot = ClassicRoles.Table.Ink.DataDot;
        Assert.Equal(ColorScience.ToColor(argb), dot(argb, ThemeKind.Dark));
        Assert.Equal(ColorScience.DataDotInk(argb, ThemeKind.Light), dot(argb, ThemeKind.Light));
        Assert.NotEqual(ColorScience.ToColor(argb), dot(argb, ThemeKind.Light));
    }

    // ── the shapes a role takes, and what a colour role does to the table ────────────────────────────────────────────────

    [Fact]
    public void A_pair_resolves_per_kind_and_a_fixed_colour_in_both()
    {
        var pair = ColorRole.Pair(ColorF.FromRgba(1, 2, 3), ColorF.FromRgba(4, 5, 6));
        Assert.Equal(ColorF.FromRgba(1, 2, 3), pair.Resolve(ThemeKind.Light));
        Assert.Equal(ColorF.FromRgba(4, 5, 6), pair.Resolve(ThemeKind.Dark));
        var fixedColour = ColorRole.Fixed(ColorF.FromRgba(7, 8, 9));
        Assert.Equal(fixedColour.Resolve(ThemeKind.Light), fixedColour.Resolve(ThemeKind.Dark));
        Assert.Equal(pair, ColorRole.Pair(ColorF.FromRgba(1, 2, 3), ColorF.FromRgba(4, 5, 6)));   // pairs compare by value
        Assert.NotEqual(pair, fixedColour);
    }

    [Fact]
    public void A_recipe_is_evaluated_for_the_kind_it_is_asked_for_and_compares_by_delegate()
    {
        Func<ThemeKind, ColorF> recipe = static k => k == ThemeKind.Light ? ColorF.FromRgba(255, 255, 255) : ColorF.FromRgba(0, 0, 0);
        var role = ColorRole.By(recipe);
        Assert.Equal(ColorF.FromRgba(255, 255, 255), role.Resolve(ThemeKind.Light));
        Assert.Equal(ColorF.FromRgba(0, 0, 0), role.Resolve(ThemeKind.Dark));
        Assert.Equal(role, ColorRole.By(recipe));
        Assert.Throws<ArgumentNullException>(() => ColorRole.By(null!));
    }

    [Fact]
    public void A_mode_pair_answers_per_kind_and_per_flag()
    {
        var pair = new ModePair<float>(0.1f, 0.2f);
        Assert.Equal(0.1f, pair.For(ThemeKind.Light));
        Assert.Equal(0.2f, pair.For(ThemeKind.Dark));
        Assert.Equal(0.1f, pair.For(true));
        Assert.Equal(0.2f, pair.For(false));
    }

    [Fact]
    public void A_table_that_differs_only_in_colour_roles_is_a_colour_only_change()
    {
        var table = ClassicRoles.Table;
        var recoloured = table with
        {
            Layer = table.Layer with { RowHover = ColorRole.Fixed(ColorF.FromRgba(1, 2, 3)) },
            OnMedia = table.OnMedia with { ScrimRest = ColorRole.Fixed(ColorF.FromRgba(9, 9, 9)) },
            Wash = table.Wash with { HeroAlpha = new ModePair<float>(0.5f, 0.6f) },
            Lyrics = table.Lyrics with { ThemePlateAlpha = 0.5f },
        };
        Assert.NotEqual(table, recoloured);
        Assert.True(table.DiffersOnlyInColours(recoloured));          // the live cross-fade path, not a transaction
        Assert.True(recoloured.DiffersOnlyInColours(table));
        // …and a non-colour role beside the colour change is not.
        Assert.False(table.DiffersOnlyInColours(recoloured with { Space = recoloured.Space with { SectionGap = 48f } }));
        // …nor is a shape (contract 1.26): a radius is geometry, so a definition swap that moves one is a transaction, never a live colour fade.
        Assert.False(table.DiffersOnlyInColours(recoloured with { Shape = recoloured.Shape with { Card = 0f } }));
        Assert.False(recoloured.DiffersOnlyInColours(table with { Shape = table.Shape with { Avatar = 0f } }));
    }

    [Fact]
    public void Two_reads_of_the_colour_tables_are_the_one_instance_and_compare_equal()
    {
        Assert.Same(ClassicRoles.Table.Layer, R.Current.Layer);
        Assert.Equal(ClassicRoles.Table.Layer, ClassicRoles.Table.Layer with { });
        Assert.Equal(ClassicRoles.Table.StageArm, ClassicRoles.Table.StageArm with { });
        Assert.Equal(ClassicRoles.Table.Wash, ClassicRoles.Table.Wash with { });
    }

    // ── the pure maths that moved to ColorScience ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_placeholder_tile_is_the_neutral_lerped_toward_the_cover_tint()
    {
        var neutral = R.Current.Layer.ArtPlaceholder.Resolve(ThemeKind.Dark);
        Assert.Equal(ColorF.Lerp(neutral, ColorScience.ToColor(0xFF2E7D32u), 0.55f), ColorScience.Placeholder(neutral, 0xFF2E7D32u));
        Assert.Equal(0.55f, ColorScience.TintStrength);
    }

    [Fact]
    public void The_chrome_accent_takes_its_fallback_as_an_argument()
    {
        var fallback = ColorF.FromRgba(0x12, 0x34, 0x56);
        var grey = new Scheme(0xFF303030, 0xFF3A3A3A, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF);
        Assert.Equal(fallback, ColorScience.ChromeAccent(grey, fallback));
        Assert.Equal(fallback, ColorScience.ChromeFromPayload(0u, fallback));
        Assert.Equal(Tok.AccentDefault, Design.Palette.ChromeAccent(grey));   // the Tok-bound face passes the active accent
        Assert.NotEqual(fallback, ColorScience.ChromeAccent(Green, fallback));
    }

    [Fact]
    public void The_tone_key_is_the_pages_surface_and_never_the_theme()
    {
        // Classic tuning F10: a light/dark flip keeps the plane's node, so its bound fill fades with every other colour (EP20).
        Assert.Equal("detail-tone:album:1", Palette.ToneKey("album:1"));
        Assert.NotEqual(Palette.ToneKey("album:1"), Palette.ToneKey("album:2"));
    }
}
