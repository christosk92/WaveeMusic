// ── Wavee.Tests/AccentFloorTests.cs — the page accent's contrast floor (tuned Classic D13) ────────────────────────────────
//
// An artwork accent is the ink of the hearts and the accent text AND the fill of the Play split, so `ColorScience.AccentFloor`
// holds it to 4.5:1 both ways: as ink on the page ground, and under its own label ink. A failing accent steps along its own ramp
// (lighter on a dark ground, darker on a light one); when no rung passes, the system accent stands in. Pure: no theme state.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class AccentFloorTests
{
    static readonly ColorF DarkGround = ColorF.FromRgba(0x2B, 0x2B, 0x2B);
    static readonly ColorF LightGround = ColorF.FromRgba(0xFB, 0xFB, 0xFB);
    static readonly ColorF DarkSystem = ColorF.FromRgba(0x60, 0xCD, 0xFF);
    static readonly ColorF LightSystem = ColorF.FromRgba(0x00, 0x5F, 0xB8);

    [Fact]
    public void A_passing_accent_is_returned_unchanged()
    {
        var orange = ColorF.FromRgba(0xF0, 0xA3, 0x5E);   // the playlist board's Play
        Assert.True(ColorScience.PassesFloor(orange, DarkGround));
        Assert.Equal(orange, ColorScience.AccentFloor(orange, ThemeKind.Dark, DarkGround, DarkSystem));
    }

    [Fact]
    public void A_dim_accent_on_a_dark_ground_steps_to_a_lighter_rung_of_its_own_ramp()
    {
        var plum = ColorF.FromRgba(0x8E, 0x24, 0xAA);
        Assert.False(ColorScience.PassesFloor(plum, DarkGround));

        var floored = ColorScience.AccentFloor(plum, ThemeKind.Dark, DarkGround, DarkSystem);
        var ramp = AccentRamp.Derive(plum);
        Assert.Contains(floored, new[] { ramp.Light1, ramp.Light2, ramp.Light3 });
        Assert.True(ColorScience.PassesFloor(floored, DarkGround));
    }

    [Fact]
    public void A_pale_accent_on_a_light_ground_steps_to_a_darker_rung()
    {
        var pale = ColorF.FromRgba(0x99, 0xD6, 0xFF);   // the "pale blue with dark text" the canvas calls out
        Assert.False(ColorScience.PassesFloor(pale, LightGround));

        var floored = ColorScience.AccentFloor(pale, ThemeKind.Light, LightGround, LightSystem);
        var ramp = AccentRamp.Derive(pale);
        Assert.Contains(floored, new[] { ramp.Dark1, ramp.Dark2, ramp.Dark3 });
        Assert.True(ColorScience.PassesFloor(floored, LightGround));
    }

    [Fact]
    public void An_accent_no_rung_can_save_is_the_system_accent()
    {
        // A mid-grey ground: no colour reaches 4.5:1 on it AND carries a 4.5:1 label, so every rung fails.
        var ground = ColorF.FromRgba(0x77, 0x77, 0x77);
        Assert.Equal(DarkSystem, ColorScience.AccentFloor(ColorF.FromRgba(0x80, 0x40, 0x40), ThemeKind.Dark, ground, DarkSystem));
    }

    [Theory]
    [InlineData(ThemeKind.Dark)]
    [InlineData(ThemeKind.Light)]
    public void Every_hue_lands_on_a_passing_accent_or_the_system_one(ThemeKind theme)
    {
        var ground = theme == ThemeKind.Dark ? DarkGround : LightGround;
        var system = theme == ThemeKind.Dark ? DarkSystem : LightSystem;
        for (int h = 0; h < 360; h += 15)
            foreach (float v in new[] { 0.35f, 0.6f, 0.82f, 1f })
            {
                var accent = ColorF.FromHsv(h, 0.7f, v, 1f);
                var floored = ColorScience.AccentFloor(accent, theme, ground, system);
                Assert.True(floored == system || ColorScience.PassesFloor(floored, ground), $"hue {h}, value {v}");
            }
        Assert.True(ColorScience.PassesFloor(system, ground), "the system accent itself passes");
    }
}
