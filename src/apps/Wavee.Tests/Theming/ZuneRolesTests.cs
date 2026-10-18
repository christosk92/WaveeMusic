// ── Wavee.Tests/Theming/ZuneRolesTests.cs — the Zune style's roles are the boards' (themes plan 6.3, Z1) ─────────────────────
//
// Pins Zune's role table against the boards (Z-Tokens first): every role defined (the reflection walk the Classic table passes), the type
// in Segoe UI Variable Display from 20 DIP up at the light weights, lowercase for every kind of word the theme writes, the square shapes,
// the flat ground ladder and its light twin, the 5 % hover and 12 % accent selection, and the accent's two families moving ONLY colour.
//
// NO TOKEN CHANGES HERE (the assembly runs facts in parallel and `Tok` is process-wide): every colour is read per kind through
// `ColorRole.Resolve`, never through the live facade, and nothing installs a definition.

using System.Reflection;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Themes.Zune;
using Wavee.Theming.Roles;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZuneRolesTests
{
    const string Display = "Segoe UI Variable Display";

    static WaveeStyleRoles Table => ZuneRoles.Table;

    static IEnumerable<PropertyInfo> Members(Type record) => record
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract");

    static void Text(TextRole role, float size, float line, ushort weight, string? face, string name)
    {
        Assert.True(role.Size == size, name + " size " + role.Size);
        Assert.True(role.LineHeight == line, name + " line height " + role.LineHeight);
        Assert.True(role.Weight == weight, name + " weight " + role.Weight);
        Assert.True(role.Face == face, name + " face " + role.Face);
    }

    static ColorF Rgb(byte r, byte g, byte b) => ColorF.FromRgba(r, g, b);

    // ── every role is defined (themes plan S2) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_defines_every_role()
    {
        foreach (var family in Members(typeof(WaveeStyleRoles)))
            Assert.True(family.GetValue(Table) is not null, "Zune: " + family.Name + " is not defined");
        foreach (var role in Members(typeof(TypeRoles)))
            Assert.True(role.GetValue(Table.Type) is not null, "Zune: Type." + role.Name + " is not defined");
        foreach (var radius in Members(typeof(ShapeRoles)).Where(p => p.PropertyType == typeof(float)))
        {
            float r = (float)radius.GetValue(Table.Shape)!;
            Assert.True(float.IsFinite(r) && r >= 0f, "Zune: Shape." + radius.Name + " is " + r);
        }
        // The function-valued roles (a recipe, not a pair) are all present.
        foreach (var (owner, value) in new (Type, object)[] { (typeof(InkRoles), Table.Ink), (typeof(StageArmTable), Table.StageArm), (typeof(WashRoles), Table.Wash) })
            foreach (var fn in Members(owner).Where(p => typeof(Delegate).IsAssignableFrom(p.PropertyType)))
                Assert.True(fn.GetValue(value) is not null, "Zune: " + owner.Name + "." + fn.Name + " is not defined");
    }

    [Fact]
    public void Every_text_role_states_its_size_line_height_and_weight_and_takes_the_display_face_from_twenty_up()
    {
        // Z-Tokens: "segoe ui variable display for everything 20 and up, text below". Every Zune role states all three numbers.
        foreach (var member in Members(typeof(TypeRoles)).Where(p => p.PropertyType == typeof(TextRole)))
        {
            var role = (TextRole)member.GetValue(Table.Type)!;
            Assert.True(role.Size is > 0f && role.LineHeight is > 0f && role.Weight is > 0, member.Name + " states size, line height and weight");
            Assert.True(role.LineHeight >= role.Size, member.Name + " line height under its size");
            if (role.Size >= 20f) Assert.True(role.Face == Display, member.Name + " (" + role.Size + ") is in the display face");
            else Assert.True(role.Face is null, member.Name + " (" + role.Size + ") is in the text face (the definition's body chain)");
        }
        Assert.Equal(Display, Table.Type.DisplayFace);
    }

    // ── type, against Z-Tokens ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_type_roles_are_the_token_boards()
    {
        var t = Table.Type;
        Text(t.Panorama, 120f, 130f, 200, Display, "Panorama");
        Text(t.PanoramaCompact, 64f, 72f, 200, Display, "PanoramaCompact");
        Text(t.Hero, 64f, 72f, 300, Display, "Hero");
        Text(t.PageHero, 40f, 48f, 300, Display, "PageHero (title 40)");
        Text(t.Pivot, 28f, 36f, 350, Display, "Pivot (the rest voice)");
        Text(t.Heading, 26f, 32f, 300, Display, "Heading");
        Text(t.QuickplayHead, 22f, 28f, 300, Display, "QuickplayHead (group 22)");
        Text(t.Numeral, 22f, 28f, 300, Display, "Numeral");
        Text(t.NumeralLarge, 28f, 36f, 300, Display, "NumeralLarge (28 on the artist page)");
        Text(t.SubPivot, 14f, 20f, 400, null, "SubPivot");
        Text(t.TrackTitle, 15f, 20f, 400, null, "TrackTitle (body 15/20)");
        Text(t.DenseMeta, 13f, 18f, 400, null, "DenseMeta (meta 13/18)");
        Text(t.MicroMeta, 11f, 14f, 400, null, "MicroMeta (micro 11/14)");
        Text(t.NowPlayingDisplay, 150f, 150f, 300, Display, "NowPlayingDisplay");
        Text(t.LyricsLine, 56f, 64f, 350, Display, "LyricsLine (56 semilight)");
        Text(t.Wordmark, 20f, 28f, 350, Display, "Wordmark");
        Assert.True(t.TrackMeta.Secondary, "TrackMeta sets the secondary ink");
        Assert.Equal(-17f, t.Panorama.Tracking);   // -2 px on 120
        Assert.Equal(-16f, t.Hero.Tracking);       // -1 px on 64
        Assert.Equal(-11f, t.Pivot.Tracking);      // -0.3 px on 28
    }

    [Fact]
    public void The_pivot_rows_carry_the_rest_and_selected_weights()
    {
        // ZV hand-off: a TextRole has one weight. Pivot is the rest voice (350); the pivot row (FacetTitle) carries 350 at rest and 600 selected
        // in the same 28/36 display cut; the second row is 14/20 at 400 / 600.
        var t = Table.Type;
        Assert.Equal(350, t.FacetTitle.RestWeight);
        Assert.Equal(600, t.FacetTitle.SelectedWeight);
        Assert.Equal(t.Pivot.Size, t.FacetTitle.LabelSize);
        Assert.Equal(t.Pivot.LineHeight, t.FacetTitle.LineHeight);
        Assert.Equal(t.Pivot.Face, t.FacetTitle.Face);
        Assert.Equal((int)t.Pivot.Weight!.Value, t.FacetTitle.RestWeight);
        Assert.Equal(24f, t.FacetTitle.ItemGap);
        Assert.Equal(44f, t.FacetTitle.ItemHeight);
        Assert.Equal(400, t.FacetCompact.RestWeight);
        Assert.Equal(600, t.FacetCompact.SelectedWeight);
        Assert.Equal(t.SubPivot.Size, t.FacetCompact.LabelSize);
        Assert.Equal(20f, t.FacetCompact.ItemGap);
        Assert.Equal(32f, t.FacetCompact.ItemHeight);
    }

    [Fact]
    public void Big_numerals_off_turns_both_numerals_into_body_and_touches_nothing_else()
    {
        var on = ZuneRoles.For(ZuneAccent.Pink, followsWindows: false, bigNumerals: true);
        var off = ZuneRoles.For(ZuneAccent.Pink, followsWindows: false, bigNumerals: false);
        Assert.Equal(on.Type, ZuneRoles.TypeTable);
        Assert.Equal(new TextRole(TypeRung.Body), off.Type.Numeral);
        Assert.Equal(new TextRole(TypeRung.Body), off.Type.NumeralLarge);
        Assert.Equal(on.Type with { Numeral = off.Type.Numeral, NumeralLarge = off.Type.NumeralLarge }, off.Type);
        Assert.False(on.DiffersOnlyInColours(off), "the numerals are a type role: a transaction, not a colour fade");
    }

    // ── case, shape, space ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_lowercases_every_kind_of_word_it_writes()
    {
        foreach (var kind in Enum.GetValues<LabelKind>())
            Assert.Equal(TextCase.Lower, Table.Case.For(kind));
    }

    [Fact]
    public void The_shapes_are_square_with_two_on_covers_and_full_on_people_and_the_playing_mark()
    {
        var s = Table.Shape;
        Assert.Equal(2f, s.Cover);
        Assert.Equal(4f, s.PinArt);
        Assert.True(s.Avatar >= 999f && s.CurrentMark >= 999f, "people and the playing dot are round");
        foreach (var role in new[] { ShapeRole.Card, ShapeRole.Control, ShapeRole.Overlay, ShapeRole.Tile, ShapeRole.ContentSeam, ShapeRole.OmniRow, ShapeRole.SeekTrack })
            Assert.True(s.For(role) == 0f, role + " is square");
    }

    [Fact]
    public void The_space_roles_are_the_rhythm_board()
    {
        var sp = Table.Space;
        Assert.Equal(32f, sp.SectionGap);
        Assert.Equal(48f, sp.SectionGapWide);
        Assert.Equal(16f, sp.GridGap);
        Assert.Equal(150f, sp.ShelfCardMin);
        Assert.Equal(2400f, sp.PageMaxW);   // P5 decision 2: pages cap at 2400 at 4K
        var metrics = ZuneStyle.MetricsFor(ZuneDensity.Standard);
        Assert.Equal(metrics.SeekRow + metrics.TransportRow, sp.DockReserve);   // the bar's 2 + 70
    }

    // ── colour ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_grounds_are_flat_and_opaque_in_both_kinds()
    {
        var l = Table.Layer;
        Assert.Equal(Rgb(0x16, 0x16, 0x16), l.ShellGround.Resolve(ThemeKind.Dark));
        Assert.Equal(Rgb(0xF5, 0xF5, 0xF5), l.ShellGround.Resolve(ThemeKind.Light));
        Assert.Equal(Rgb(0x11, 0x11, 0x11), l.PlayerBar.Resolve(ThemeKind.Dark));
        Assert.Equal(Rgb(0xEB, 0xEB, 0xEB), l.PlayerBar.Resolve(ThemeKind.Light));
        Assert.Equal(Rgb(0x1B, 0x1B, 0x1B), l.FloatingPane.Resolve(ThemeKind.Dark));
        Assert.Equal(Rgb(0x24, 0x24, 0x24), l.FloatingChrome.Resolve(ThemeKind.Dark));
        foreach (var kind in new[] { ThemeKind.Light, ThemeKind.Dark })
        {
            // No content step, no material rung: the frame is one ground.
            Assert.Equal(l.ShellGround.Resolve(kind), l.ContentSurface.Resolve(kind));
            Assert.Equal(l.ShellGround.Resolve(kind), l.Toolbar.Resolve(kind));
            Assert.Equal(1f, l.ShellGround.Resolve(kind).A);
            Assert.Equal(0f, l.RowZebra.Resolve(kind).A);
        }
    }

    [Fact]
    public void Hover_is_a_five_percent_ink_wash_and_selection_a_twelve_percent_accent_wash()
    {
        var l = Table.Layer;
        Assert.Equal(ColorF.FromRgba(0xFF, 0xFF, 0xFF) with { A = 0.05f }, l.RowHover.Resolve(ThemeKind.Dark));
        Assert.Equal(ColorF.FromRgba(0x11, 0x11, 0x11) with { A = 0.05f }, l.RowHover.Resolve(ThemeKind.Light));
        Assert.Equal(ZuneAccent.Pink.Dark with { A = 0.12f }, l.SelectedRest.Resolve(ThemeKind.Dark));
        Assert.Equal(ZuneAccent.Pink.Light with { A = ZuneTokens.SelectedALight }, l.SelectedRest.Resolve(ThemeKind.Light));
        Assert.Equal(0.06f, ZuneTokens.SelectedALight);
        // Hovering a selected row is strictly stronger than the selection alone.
        Assert.True(l.SelectedHover.Resolve(ThemeKind.Dark).A > l.SelectedRest.Resolve(ThemeKind.Dark).A);
    }

    [Fact]
    public void The_accent_is_the_board_swatch_as_fill_everywhere_and_as_ink_on_dark_and_a_darker_ink_on_light()
    {
        foreach (var pair in ZuneAccent.Swatches)
        {
            var t = ZuneRoles.For(pair, followsWindows: false, bigNumerals: true);
            Assert.Equal(pair.Dark, t.Ink.AccentAction.Resolve(ThemeKind.Dark));
            Assert.Equal(pair.Light, t.Ink.AccentAction.Resolve(ThemeKind.Light));
            Assert.Equal(pair.Light, t.Ink.AccentSelection.Resolve(ThemeKind.Light));
            Assert.Equal(pair.Dark, t.Ink.AccentDecor.Resolve(ThemeKind.Dark));
            Assert.Equal(pair.LightInk, t.Ink.AccentDecor.Resolve(ThemeKind.Light));
            Assert.Equal(pair.LightInk, t.Ink.PremiumText.Resolve(ThemeKind.Light));
            // The fill keeps the board's swatch: white on it (a glyph on the play disc) reads 3 : 1 or better.
            Assert.True(ColorContrast.Ratio(ColorF.FromRgba(255, 255, 255), pair.Light) >= 3f, "white on the light fill: " + pair);
            // The ink reads 4.5 : 1 on every light plate accent text sits on, and is no lighter than the fill.
            foreach (var plate in new[] { ZuneTokens.LightGround, ZuneTokens.LightWell, ColorF.FromRgba(0xE2, 0xE2, 0xE2), ColorF.Lerp(ZuneTokens.LightGround, pair.Light, ZuneTokens.SelectedALight) })
                Assert.True(ColorContrast.Ratio(pair.LightInk, plate) >= 4.5f, "accent ink on a light plate: " + pair);
            Assert.True(Luma(pair.LightInk) <= Luma(pair.Light));
            Assert.Equal(pair.Dark, t.Layer.Badge.Resolve(ThemeKind.Dark));
            Assert.True(Luma(pair.Light) < Luma(pair.Dark), "the light step is darker: " + pair);
        }
    }

    static float Luma(ColorF c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    [Fact]
    public void Two_accents_differ_only_in_colour_and_the_windows_accent_reads_the_live_tokens()
    {
        var pink = ZuneRoles.For(ZuneAccent.Pink, followsWindows: false, bigNumerals: true);
        var blue = ZuneRoles.For(ZuneAccent.Blue, followsWindows: false, bigNumerals: true);
        var windows = ZuneRoles.For(ZuneAccent.Pink, followsWindows: true, bigNumerals: true);
        Assert.NotEqual(pink, blue);
        Assert.True(pink.DiffersOnlyInColours(blue), "an accent pick is colour only (live)");
        Assert.True(pink.DiffersOnlyInColours(windows), "following the OS accent is colour only (live)");
        Assert.NotNull(windows.Ink.AccentAction.Of);
        Assert.NotNull(windows.Layer.SelectedRest.Of);
        Assert.Null(pink.Ink.AccentAction.Of);
        // Pure: equal inputs build equal tables, so an unchanged pick is not a change.
        Assert.Equal(pink, ZuneRoles.For(ZuneAccent.Pink, followsWindows: false, bigNumerals: true));
        Assert.Equal(windows, ZuneRoles.For(ZuneAccent.Blue, followsWindows: true, bigNumerals: true));
        Assert.Equal(ZuneRoles.Table, pink);
    }

    [Fact]
    public void A_colour_that_is_not_a_swatch_takes_the_engine_step_for_its_light_arm()
    {
        Assert.Equal(ZuneAccent.Pink, ZuneAccent.PairOf(ColorF.FromRgba(0xFF, 0x3C, 0xA6)));
        Assert.Equal(ZuneAccent.Purple, ZuneAccent.PairOf(ColorF.FromRgba(0xB9, 0x83, 0xFF, 0x80)));   // alpha is ignored
        var custom = ColorF.FromRgba(0x20, 0xC0, 0x80);
        var pair = ZuneAccent.PairOf(custom);
        Assert.Equal(custom, pair.Dark);
        Assert.Equal(AccentRamp.Derive(custom).Dark1, pair.Light);
        Assert.True(ColorContrast.Ratio(pair.LightInk, ZuneTokens.LightWell) >= 4.5f, "a custom accent's ink reads 4.5 : 1 too");
        // The ink of a swatch's fill is the pinned ink, within a byte (the table is authored, the function derives it).
        foreach (var s in ZuneAccent.Swatches)
            Assert.True(MathF.Abs(ZuneAccent.InkOf(s.Light).R - s.LightInk.R) < 0.02f && MathF.Abs(ZuneAccent.InkOf(s.Light).G - s.LightInk.G) < 0.02f && MathF.Abs(ZuneAccent.InkOf(s.Light).B - s.LightInk.B) < 0.02f, "the derived ink of " + s);
    }

    [Fact]
    public void The_ramp_puts_the_dark_swatch_on_the_dark_shades_and_the_light_swatch_on_the_light_ones()
    {
        // The engine reads the fill as Light2 (dark) / Dark1 (light) and the text as Light3 (dark) / Dark2 (light).
        var ramp = ZuneAccent.RampOf(ZuneAccent.Orange);
        Assert.Equal(ZuneAccent.Orange.Dark, ramp.Light2);
        Assert.Equal(ZuneAccent.Orange.Dark, ramp.Light3);
        Assert.Equal(ZuneAccent.Orange.Light, ramp.Dark1);
        Assert.Equal(ZuneAccent.Orange.LightInk, ramp.Dark2);   // the text shade is the ink, the fill shade the board's swatch
        Assert.Equal(new[] { "ff3ca6", "ff7a1a", "60cdff", "8fd14f", "b983ff" }, ZuneAccent.Swatches.Select(p => Hex(p.Dark)));
        Assert.Equal(new[] { "e0197f", "d65a00", "0a7fb5", "4f8f1a", "8a4fd6" }, ZuneAccent.Swatches.Select(p => Hex(p.Light)));
        Assert.Equal(new[] { "bd156b", "a94700", "086b99", "3e7115", "7e48c3" }, ZuneAccent.Swatches.Select(p => Hex(p.LightInk)));
    }

    [Fact]
    public void The_light_text_inks_hold_4_5_to_1_on_the_ground_the_well_and_a_selected_row()
    {
        var selected = ColorContrast.Over(ColorF.FromRgba(0, 0, 0) with { A = ZuneTokens.PressedA }, ZuneTokens.LightGround);   // the 8 % grey a selected row takes
        foreach (var plate in new[] { ZuneTokens.LightGround, ZuneTokens.LightWell, ZuneTokens.LightPanel, selected })
        {
            Assert.True(ColorContrast.Ratio(ZuneTokens.LightInk, plate) >= 4.5f);
            Assert.True(ColorContrast.Ratio(ZuneTokens.LightInkSecondary, plate) >= 4.5f);
            Assert.True(ColorContrast.Ratio(ZuneTokens.LightInkTertiary, plate) >= 4.5f, "tertiary on " + Hex(plate));
        }
        Assert.True(Luma(ZuneTokens.LightInkSecondary) < Luma(ZuneTokens.LightInkTertiary), "the tertiary stays the lighter voice");
        // The accent's own selected wash is the lighter 6 %: the tertiary ink holds on it too.
        var accentWash = ColorContrast.Over(ZuneAccent.Pink.Light with { A = ZuneTokens.SelectedALight }, ZuneTokens.LightGround);
        Assert.True(ColorContrast.Ratio(ZuneTokens.LightInkTertiary, accentWash) >= 4.5f);
        Assert.True(ColorContrast.Ratio(ZuneAccent.Pink.LightInk, accentWash) >= 4.5f);
    }

    static string Hex(ColorF c)
    {
        static int B(float v) => Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return B(c.R).ToString("x2") + B(c.G).ToString("x2") + B(c.B).ToString("x2");
    }
}
