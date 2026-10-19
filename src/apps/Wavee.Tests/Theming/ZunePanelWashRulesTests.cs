// ── Wavee.Tests/Theming/ZunePanelWashRulesTests.cs — the side panel's cover glow, pure (owner feedback: the whole-panel wash read as a flat muddy plum) ──
//
// Design "B, one surface" (owner decision 2026-10-20): the page's wash runs on under the panel under a scrim, and a hued cover lights the head:
//   · the glow keeps the cover's hue at both kinds (it reads as the cover's colour, never a grey);
//   · it falls monotonically and is gone within the panel's first 40 % (the lyrics and the queue's rows sit on the scrimmed page);
//   · the panel's primary ink keeps a strong ratio on the glow's peak over the plate, for every hue, dark and light;
//   · the scrim dims the page a shade, never to black and never past the page's own hue, and the inks keep their ratio on it.

using FluentGpu.Foundation;
using Wavee.Themes.Zune;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZunePanelWashRulesTests
{
    static readonly ColorF DarkPlate = ColorF.FromRgba(0x1B, 0x1B, 0x1B), LightPlate = ColorF.FromRgba(255, 255, 255);

    public static TheoryData<float> Hues => [0f, 16f, 60f, 120f, 187f, 240f, 287f, 330f];

    [Theory]
    [MemberData(nameof(Hues))]
    public void The_glow_keeps_the_covers_hue_at_both_kinds(float hue)
    {
        foreach (bool light in new[] { false, true })
        {
            var (h, s, _) = ZunePanelWashRules.GlowOf(hue, light).ToHsv();
            Assert.InRange(MathF.Abs(((h - hue) % 360f + 540f) % 360f - 180f), 0f, 1f);
            Assert.True(s >= 0.4f, $"hue {hue} {(light ? "light" : "dark")}: saturation {s} reads grey");
        }
    }

    [Fact]
    public void The_glow_falls_monotonically_and_is_gone_within_the_panels_first_40_percent()
    {
        Assert.InRange(ZunePanelWashRules.GlowReach, 0.2f, 0.4f);
        var stops = ZunePanelWashRules.GlowStops;
        float at = 0f, strength = 1f;
        foreach (var stop in stops)
        {
            Assert.True(stop.At > at && stop.At < 1f, "the stops run down the reach in order");
            Assert.True(stop.Strength < strength && stop.Strength > 0f, "the glow only thins");
            (at, strength) = (stop.At, stop.Strength);
        }
    }

    [Theory]
    [MemberData(nameof(Hues))]
    public void The_primary_ink_reads_strongly_on_the_glows_peak(float hue)
    {
        var dark = ColorContrast.Flatten(ZunePanelWashRules.GlowOf(hue, light: false), DarkPlate);
        var light = ColorContrast.Flatten(ZunePanelWashRules.GlowOf(hue, light: true), LightPlate);
        Assert.True(ColorContrast.Ratio(ColorF.FromRgba(255, 255, 255), dark) >= 7f, $"hue {hue}: white on {dark.ToHex()}");
        Assert.True(ColorContrast.Ratio(ColorF.FromRgba(0, 0, 0), light) >= 7f, $"hue {hue}: black on {light.ToHex()}");
    }

    // The owner's mock: the page gradient's head (#0f3157), its foot (the ground, #151515 / the light ground #f5f5f5).
    static readonly ColorF BlueHead = ColorF.FromRgba(0x0F, 0x31, 0x57), DarkGround = ColorF.FromRgba(0x16, 0x16, 0x16), LightGround = ColorF.FromRgba(0xF5, 0xF5, 0xF5);

    [Fact]
    public void The_scrim_darkens_the_page_a_shade_and_keeps_its_hue()
    {
        var dark = ZunePanelWashRules.ScrimOf(light: false);
        Assert.InRange(dark.A, 0.38f, 0.46f);                        // the mock's 0.42
        var head = ColorContrast.Flatten(dark, BlueHead);
        var (h0, _, v0) = BlueHead.ToHsv();
        var (h, s, v) = head.ToHsv();
        Assert.True(v < v0 && v > v0 * 0.45f, $"the panel zone is a shade under the page ({head.ToHex()} on {BlueHead.ToHex()}), never black");
        Assert.InRange(MathF.Abs(((h - h0) % 360f + 540f) % 360f - 180f), 0f, 4f);
        Assert.True(s > 0.4f, $"the page's blue shows through ({head.ToHex()})");
        // Light: a faint veil, still a shade under the pale page.
        var light = ColorContrast.Flatten(ZunePanelWashRules.ScrimOf(light: true), LightGround);
        Assert.True(light.ToHsv().V < LightGround.ToHsv().V, $"the light panel zone is a shade under the page ({light.ToHex()})");
        Assert.True(ColorContrast.Ratio(ColorF.FromRgba(0, 0, 0), light) >= 15f, $"black on the light zone {light.ToHex()}");
    }

    [Theory]
    [MemberData(nameof(Hues))]
    public void The_primary_ink_reads_strongly_on_the_scrimmed_page_under_the_glow(float hue)
    {
        var dark = ColorContrast.Flatten(ZunePanelWashRules.GlowOf(hue, light: false), ColorContrast.Flatten(ZunePanelWashRules.ScrimOf(false), BlueHead));
        var light = ColorContrast.Flatten(ZunePanelWashRules.GlowOf(hue, light: true), ColorContrast.Flatten(ZunePanelWashRules.ScrimOf(true), LightGround));
        Assert.True(ColorContrast.Ratio(ColorF.FromRgba(255, 255, 255), dark) >= 7f, $"hue {hue}: white on {dark.ToHex()}");
        Assert.True(ColorContrast.Ratio(ColorF.FromRgba(0, 0, 0), light) >= 7f, $"hue {hue}: black on {light.ToHex()}");
        Assert.True(ColorContrast.Ratio(ColorF.FromRgba(255, 255, 255), ColorContrast.Flatten(ZunePanelWashRules.ScrimOf(false), DarkGround)) >= 15f);
    }
}
