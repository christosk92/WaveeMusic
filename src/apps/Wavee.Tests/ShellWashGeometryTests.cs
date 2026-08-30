using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

// The shell's three Home washes are CLIPPED to the bounding box of their own ellipse at the transparent stop, so each
// one blends over roughly a quarter of the window instead of all of it. That is only sound if the clip and the
// re-derived node-relative gradient geometry describe the SAME ellipse — these pin the solver against the authored
// window-relative extents.
public class ShellWashGeometryTests
{
    const float Eps = 0.005f;

    // The authored extents, from the prototype: layer1 x∈[0,0.52]w y∈[0,0.57]h · layer2 x∈[0.55,1]w y∈[0,0.60]h ·
    // layer3 x∈[0,1]w y∈[0.54,1]h.
    [Theory]
    [InlineData(0.06f, 0.00f, 0.74f, 0.92f, 0.62f, 0.52f, 0.57f, false, false)]
    [InlineData(0.92f, 0.10f, 0.58f, 0.78f, 0.64f, 0.45f, 0.60f, true, false)]
    [InlineData(0.58f, 1.00f, 0.90f, 0.70f, 0.66f, 1.00f, 0.46f, false, true)]
    public void Resolve_ClipsToTheEllipseAtTheTransparentStop(
        float cx, float cy, float rx, float ry, float fade,
        float wantW, float wantH, bool wantRight, bool wantBottom)
    {
        var p = ShellWashGeometry.Resolve(new Point2(cx, cy), new Point2(rx, ry), fade);

        Assert.Equal(wantW, p.W, Eps);
        Assert.Equal(wantH, p.H, Eps);
        Assert.Equal(wantRight, p.AnchorRight);
        Assert.Equal(wantBottom, p.AnchorBottom);
        Assert.Equal(fade, p.FadeOffset);
    }

    // The re-derivation is the load-bearing half: node-relative centre + radius must reproduce the ORIGINAL
    // window-relative ellipse once scaled back through the clipped box.
    [Theory]
    [InlineData(0.06f, 0.00f, 0.74f, 0.92f, 0.62f)]
    [InlineData(0.92f, 0.10f, 0.58f, 0.78f, 0.64f)]
    [InlineData(0.58f, 1.00f, 0.90f, 0.70f, 0.66f)]
    public void Resolve_NodeRelativeGeometryRoundTripsToTheWindowEllipse(
        float cx, float cy, float rx, float ry, float fade)
    {
        var p = ShellWashGeometry.Resolve(new Point2(cx, cy), new Point2(rx, ry), fade);
        // The clip's origin, recovered from the anchor + extent (the shell places the box by ZStack self-alignment).
        float x0 = p.AnchorRight ? 1f - p.W : 0f;
        float y0 = p.AnchorBottom ? 1f - p.H : 0f;

        Assert.Equal(cx, x0 + p.Center.X * p.W, Eps);
        Assert.Equal(cy, y0 + p.Center.Y * p.H, Eps);
        Assert.Equal(rx, p.Radius.X * p.W, Eps);
        Assert.Equal(ry, p.Radius.Y * p.H, Eps);
    }

    // The two Theories above drive the SOLVER. These pin the three PUBLISHED placements — the constants the shell
    // actually mounts — so a hand edit to Hero/Weekly/Mix cannot silently re-place a wash while the solver tests stay
    // green. Solved to 1e-4, which is far tighter than the 0.005 the extents above allow.
    [Fact]
    public void ThePublishedPlacements_AreTheAuthoredWindowEllipses()
    {
        Check(ShellWashGeometry.Hero, "hero",
            w: 0.5188f, h: 0.5704f, right: false, bottom: false,
            cx: 0.1156515f, cy: 0.0f, rx: 1.4263685f, ry: 1.6129032f, fade: 0.62f);
        Check(ShellWashGeometry.Weekly, "weekly",
            w: 0.4512f, h: 0.5992f, right: true, bottom: false,
            cx: 0.8226950f, cy: 0.1668892f, rx: 1.2854610f, ry: 1.3017356f, fade: 0.64f);
        Check(ShellWashGeometry.Mix, "mix",
            w: 1.0f, h: 0.462f, right: false, bottom: true,
            cx: 0.58f, cy: 1.0f, rx: 0.90f, ry: 1.5151515f, fade: 0.66f);

        static void Check(in ShellWashPlacement p, string who,
            float w, float h, bool right, bool bottom, float cx, float cy, float rx, float ry, float fade)
        {
            const float Tight = 1e-4f;
            Assert.Equal(w, p.W, Tight);
            Assert.Equal(h, p.H, Tight);
            Assert.Equal(right, p.AnchorRight);
            Assert.Equal(bottom, p.AnchorBottom);
            Assert.Equal(cx, p.Center.X, Tight);
            Assert.Equal(cy, p.Center.Y, Tight);
            Assert.Equal(rx, p.Radius.X, Tight);
            Assert.Equal(ry, p.Radius.Y, Tight);
            Assert.Equal(fade, p.FadeOffset);
            Assert.True(w * h < 0.55f, $"{who}: the clip is a PAINT BUDGET — a layer covering the window buys nothing");
        }
    }

    // …and the same three, re-derived: the clipped box plus its node-relative ellipse must reproduce the authored
    // WINDOW-relative spec exactly. This is the half that makes the clip safe — the box moves the ellipse's origin, and
    // Center/Radius have to absorb that move or the wash lands somewhere else.
    [Fact]
    public void ThePublishedPlacements_RoundTripToTheWindowRelativeSpec()
    {
        Check(ShellWashGeometry.Hero, 0.06f, 0.00f, 0.74f, 0.92f);
        Check(ShellWashGeometry.Weekly, 0.92f, 0.10f, 0.58f, 0.78f);
        Check(ShellWashGeometry.Mix, 0.58f, 1.00f, 0.90f, 0.70f);

        static void Check(in ShellWashPlacement p, float cx, float cy, float rx, float ry)
        {
            const float Tight = 1e-4f;
            float x0 = p.AnchorRight ? 1f - p.W : 0f;
            float y0 = p.AnchorBottom ? 1f - p.H : 0f;
            Assert.Equal(cx, x0 + p.Center.X * p.W, Tight);
            Assert.Equal(cy, y0 + p.Center.Y * p.H, Tight);
            Assert.Equal(rx, p.Radius.X * p.W, Tight);
            Assert.Equal(ry, p.Radius.Y * p.H, Tight);
        }
    }

    // The wash's ONE theme-keyed quantity, pinned to the value. Everything else about a wash — which cards feed it, what
    // colour they resolve to, where the ellipse sits — is theme-free; strength is where light and dark part company, so
    // these four numbers are the whole theme axis of the feature.
    [Fact]
    public void WashAlphas_ArePinnedPerTheme()
    {
        Assert.Equal(0.055f, ShellWashGeometry.HeroAlpha(light: true));
        Assert.Equal(0.05f, ShellWashGeometry.ShelfAlpha(light: true));
        Assert.Equal(0.10f, ShellWashGeometry.HeroAlpha(light: false));
        Assert.Equal(0.085f, ShellWashGeometry.ShelfAlpha(light: false));
        // The published consts and the accessors are one value, not two that happen to agree today.
        Assert.Equal(ShellWashGeometry.HeroAlphaLight, ShellWashGeometry.HeroAlpha(light: true));
        Assert.Equal(ShellWashGeometry.ShelfAlphaLight, ShellWashGeometry.ShelfAlpha(light: true));
        Assert.Equal(ShellWashGeometry.HeroAlphaDark, ShellWashGeometry.HeroAlpha(light: false));
        Assert.Equal(ShellWashGeometry.ShelfAlphaDark, ShellWashGeometry.ShelfAlpha(light: false));
    }

    // Both halves of the alpha ramp are theme-keyed, and dark always carries the stronger wash (the same colour reads
    // far weaker over the dark ground).
    [Fact]
    public void WashAlphas_AreThemeKeyed_AndDarkIsStronger()
    {
        Assert.True(ShellWashGeometry.HeroAlpha(light: false) > ShellWashGeometry.HeroAlpha(light: true));
        Assert.True(ShellWashGeometry.ShelfAlpha(light: false) > ShellWashGeometry.ShelfAlpha(light: true));
        // The hero leads: it is the module the eye lands on, so its wash is the anchor and the shelves sit under it.
        Assert.True(ShellWashGeometry.HeroAlpha(light: true) > ShellWashGeometry.ShelfAlpha(light: true));
        Assert.True(ShellWashGeometry.HeroAlpha(light: false) > ShellWashGeometry.ShelfAlpha(light: false));
    }
}
