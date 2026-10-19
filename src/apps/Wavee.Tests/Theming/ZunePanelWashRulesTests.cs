// ── Wavee.Tests/Theming/ZunePanelWashRulesTests.cs — the side panel's cover glow, pure (owner feedback: the whole-panel wash read as a flat muddy plum) ──
//
// The panel keeps the board's flat plate; the cover lights only its head:
//   · the glow keeps the cover's hue at both kinds (it reads as the cover's colour, never a grey);
//   · it falls monotonically and is gone within the panel's first 40 % (the lyrics and the queue's rows sit on the plain plate);
//   · the panel's primary ink keeps a strong ratio on the glow's peak over the plate, for every hue, dark and light.

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
}
