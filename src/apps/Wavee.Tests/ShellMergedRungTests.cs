using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

// The PALETTE's translucent MUX rungs (body plate + content pane) and the shell surfaces built on top of them.
//
// WHAT THE SHELL PAINTS (the contract these tests exist to protect). The authenticated shell is the STOCK Windows 11
// Mica stack: the root paints NOTHING (live Mica is the base layer, and every chrome band — merged row, sidebar, player
// dock — is a paint-site omission over it), and the CONTENT REGION and the right-rail band paint the TRANSLUCENT
// WaveeColors.FileArea rung (stock LayerFillColorDefault) over that base, PAIRED with a 1px Tok.StrokeCardDefault on
// their LEFT+TOP edges only, one rounded corner, and no shadow. The fill and the stroke are one treatment: at ~30%
// alpha the fill alone is not an edge, which is why stock pairs them and why nothing here may re-introduce a shadow or
// a gutter as the separator. That pairing lives in the shell's element tree (WaveeShell.ContentRegionStroke) and is not
// token-level, so what CAN be pinned here is the ladder the fills come from — which is what the tests below do.
//
// The first three tests pin the rung ALGEBRA: the merge of the two rungs must composite to the same bytes as
// pane-on-plate over ANY backdrop (source-over is associative), it must stay translucent, and the neutral preset must be
// the stock compounded alpha. FileArea is now painted for real again (over Mica, at the content layer), and the merged
// two-rung form remains the palette's definition of the preset tint — the contrast gates and PresetSwatch measure
// through it — so the algebra has to hold either way.
//
// The rest pin the OPAQUE ladder, which the shell no longer paints under its chrome but still resolves for every
// FLOATING stand-in (WaveeColors.FloatingChrome / FloatingPane), for the login view (no shell under it at all) and as
// the no-Mica fallback: one ground (light #EDEDED, dark #202020) and ONE opaque content rung above it.
public class ShellMergedRungTests
{
    static ThemePalette[] All => [Tok.WarmPalette, Tok.SlatePalette, Tok.NeutralPalette, Tok.AccentTintedPalette];

    static int MaxChannelDelta(in ColorF a, in ColorF b)
    {
        static int Ch(float f) => (int)MathF.Round(f * 255f);
        return Math.Max(Math.Abs(Ch(a.R) - Ch(b.R)), Math.Max(Math.Abs(Ch(a.G) - Ch(b.G)), Math.Abs(Ch(a.B) - Ch(b.B))));
    }

    static int Byte(float f) => (int)MathF.Round(f * 255f);

    // Channel sum, not a perceptual luminance: the ladder's step is a plain mix toward white, so "lighter" here means
    // every rung moves the same direction in the same space the renderer blends in.
    static float Lum(in ColorF c) => c.R + c.G + c.B;

    [Fact]
    public void MergedRung_CompositesIdenticallyOverEveryBackdrop()
    {
        foreach (var p in All)
        {
            foreach (var (light, shell, set) in new (bool, ShellPalette, TokenSet)[]
            {
                (true, p.LightShell, p.Light),
                (false, p.DarkShell, p.Dark),
            })
            {
                var merged = ColorContrast.Over(shell.FileArea, shell.Toolbar);
                ColorF[] backdrops = light
                    ? new[] { MicaRef.LightDefault, MicaRef.LightBright, MicaRef.LightDim, set.WindowBackground }
                    : new[] { MicaRef.DarkDefault, MicaRef.DarkBright, MicaRef.DarkDim, set.WindowBackground };
                foreach (var bd in backdrops)
                {
                    // ONE pass vs TWO — the algebra the merge rests on, asserted to the byte (the renderer blends
                    // straight source-over into a BGRA8_UNORM target, i.e. in exactly this space).
                    var one = ColorContrast.Flatten(merged, bd);
                    var two = ColorContrast.Flatten(shell.FileArea, ColorContrast.Flatten(shell.Toolbar, bd));
                    Assert.True(MaxChannelDelta(one, two) == 0,
                        $"{p.Id}/{(light ? "light" : "dark")}: merged composite drifted {MaxChannelDelta(one, two)}/255 from the two-rung stack");
                }
            }
        }
    }

    [Fact]
    public void MergedRung_StaysTranslucent_SoMicaStillReadsThrough()
    {
        foreach (var p in All)
        {
            foreach (var shell in new[] { p.LightShell, p.DarkShell })
            {
                var merged = ColorContrast.Over(shell.FileArea, shell.Toolbar);
                float want = shell.FileArea.A + shell.Toolbar.A * (1f - shell.FileArea.A);
                Assert.Equal(want, merged.A, 5);
                Assert.True(merged.A < 1f, $"{p.Id}: a merged rung must not go opaque — the backdrop is the point");
                Assert.True(merged.A > shell.Toolbar.A, $"{p.Id}: the merge must be MORE opaque than the plate alone");
            }
        }
    }

    // The stock MUX values, so a palette-recipe change can never silently re-tune the surface the whole app sits on:
    // dark LayerFillColorDefault #4C3A3A3A over LayerOnMicaBaseAltFillColorDefault #733A3A3A == #9D3A3A3A (both rungs
    // carry the same rgb at sat 0, so only alpha compounds), light #80FFFFFF over #B3FFFFFF == #D9FFFFFF. Composited,
    // those are still the ladder's #303030 dark / #FCFCFC light.
    [Fact]
    public void MergedRung_NeutralPresetIsTheStockCompoundedAlpha()
    {
        var dark = ColorContrast.Over(Tok.NeutralPalette.DarkShell.FileArea, Tok.NeutralPalette.DarkShell.Toolbar);
        Assert.Equal(0x3A, Byte(dark.R));
        Assert.Equal(0x3A, Byte(dark.G));
        Assert.Equal(0x3A, Byte(dark.B));
        Assert.Equal(0x9D, Byte(dark.A));
        Assert.Equal(0x30, Byte(ColorContrast.Flatten(dark, MicaRef.DarkDefault).R));

        var lite = ColorContrast.Over(Tok.NeutralPalette.LightShell.FileArea, Tok.NeutralPalette.LightShell.Toolbar);
        Assert.Equal(0xFF, Byte(lite.R));
        Assert.Equal(0xD9, Byte(lite.A));
        Assert.Equal(0xFC, Byte(ColorContrast.Flatten(lite, MicaRef.LightDefault).R));
    }

    // The OPAQUE ladder, which is now the FLOATING/fallback ladder rather than the docked one: FloatingChrome stands in
    // for a chrome band that had to leave the window material behind (the narrow drawer's own acrylic aside), FloatingPane
    // for a content band that did, and both must stay one real opaque step apart. The docked content region takes the
    // translucent FileArea rung instead — asserted at the bottom of this file.
    [Fact]
    public void ContentSurface_IsOneOpaqueStepAboveTheGround()
    {
        var shell = Tok.Theme == ThemeKind.Light ? Tok.Palette.LightShell : Tok.Palette.DarkShell;
        var surface = WaveeColors.ContentSurface;
        Assert.Equal(1f, surface.A);                                          // opaque: nothing behind the shell shows through
        Assert.Equal(1f, WaveeColors.ShellGround.A);                          // …and so is the ground it steps off
        Assert.Equal(1f, WaveeColors.FloatingChrome.A);
        Assert.NotEqual(Byte(WaveeColors.ShellGround.R), Byte(surface.R));    // a real step, not the ground repainted
        // The raw translucent rungs stay published untouched — the palette gates still assert the two-rung ladder.
        Assert.Equal(shell.Toolbar, WaveeColors.Toolbar);
        Assert.Equal(shell.FileArea, WaveeColors.FileArea);
    }

    // Both themes' pinned values: light is the stock FillSolidTertiary #F9F9F9 over the #EDEDED ground; dark is the
    // deliberate #282828 over the #202020 ground, derived as a mix-toward-white FRACTION so a tinted preset canvas gets
    // the same step (neutral lands on exactly 40/255).
    [Fact]
    public void ContentSurface_NeutralPresetIsPinned()
    {
        var p = Tok.NeutralPalette;

        var lite = WaveeColors.ContentSurfaceFor(p.Light, ThemeKind.Light);
        Assert.Equal(0xED, Byte(WaveeColors.ShellGroundFor(p.Light, ThemeKind.Light).R));   // the ground
        Assert.Equal(0xF9, Byte(lite.R));
        Assert.Equal(1f, lite.A);

        var dark = WaveeColors.ContentSurfaceFor(p.Dark, ThemeKind.Dark);
        Assert.Equal(0x20, Byte(WaveeColors.ShellGroundFor(p.Dark, ThemeKind.Dark).R));     // the ground
        Assert.Equal(0x28, Byte(dark.R));
        Assert.Equal(0x28, Byte(dark.G));
        Assert.Equal(0x28, Byte(dark.B));
        Assert.Equal(1f, dark.A);
    }

    // The GROUND rung's own neutral pins (stage O2). Light drops the stock #F3F3F3 canvas to #EDEDED — MicaRef.LightDefault,
    // the bare Mica Alt tone the Files tab rail carries as its DARKEST chrome band, which is the reference this ladder
    // copies; #F3F3F3 read as page rather than as chrome. Dark is FillSolidBase #202020 untouched (it is already that
    // band). Both stay grey (no preset hue in the stock palette) and both stay opaque — the shell paints the ground with
    // the renderer's no-blend PSO.
    [Fact]
    public void ShellGround_NeutralPresetIsPinned()
    {
        var p = Tok.NeutralPalette;

        var lite = WaveeColors.ShellGroundFor(p.Light, ThemeKind.Light);
        Assert.Equal(0xF3, Byte(p.Light.FillSolidBase.R));                     // the canvas it drops off
        Assert.Equal((0xED, 0xED, 0xED), (Byte(lite.R), Byte(lite.G), Byte(lite.B)));
        Assert.Equal(1f, lite.A);
        // …and that is exactly the engine's bare-Mica-Alt reference tone, not a hand-tuned near-miss.
        Assert.Equal(Byte(MicaRef.LightDefault.R), Byte(lite.R));

        var dark = WaveeColors.ShellGroundFor(p.Dark, ThemeKind.Dark);
        Assert.Equal((0x20, 0x20, 0x20), (Byte(dark.R), Byte(dark.G), Byte(dark.B)));
        Assert.Equal(1f, dark.A);
        Assert.Equal(p.Dark.FillSolidBase, dark);                              // dark is the canvas verbatim
    }

    // The selected tab paints the content STEP as a translucent white layer, so the same fill lands on ContentSurface
    // over the bare ground and on the tinted equivalent over a detail page's shell tint. Pin the untinted identity:
    // Over(ContentLayer, ShellGround) == ContentSurface per channel within one 8-bit step (the layer's alpha is byte-
    // quantized), in BOTH themes.
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void ContentLayer_CompositesToTheContentSurface_OverTheBareGround(ThemeKind theme)
    {
        var p = Tok.NeutralPalette;
        var set = theme == ThemeKind.Light ? p.Light : p.Dark;
        var composed = ColorContrast.Flatten(WaveeColors.ContentLayerFor(theme), WaveeColors.ShellGroundFor(set, theme));
        var target = WaveeColors.ContentSurfaceFor(set, theme);
        Assert.True(System.Math.Abs(Byte(composed.R) - Byte(target.R)) <= 1, $"R {Byte(composed.R)} vs {Byte(target.R)}");
        Assert.True(System.Math.Abs(Byte(composed.G) - Byte(target.G)) <= 1, $"G {Byte(composed.G)} vs {Byte(target.G)}");
        Assert.True(System.Math.Abs(Byte(composed.B) - Byte(target.B)) <= 1, $"B {Byte(composed.B)} vs {Byte(target.B)}");
    }

    // The neutral pins, completed: the ground and the content rung are both NEUTRAL greys (all three channels equal, so
    // no preset hue leaks into the stock palette), and the step between them is the authored size — 12/255 in light
    // (the ground dropped 6 while the content rung stayed on the stock #F9F9F9), 8/255 in dark. A recipe change that
    // kept #F9F9F9's red but drifted its green, or that re-tuned the dark lift or the light drop, is a different surface
    // for the whole app and has to be a deliberate edit here.
    [Fact]
    public void ContentSurface_NeutralStepIsGreyAndTheAuthoredSize()
    {
        var p = Tok.NeutralPalette;

        foreach (var (set, theme, wantGround, wantContent) in new (TokenSet, ThemeKind, int, int)[]
        {
            (p.Light, ThemeKind.Light, 0xED, 0xF9),
            (p.Dark, ThemeKind.Dark, 0x20, 0x28),
        })
        {
            var ground = WaveeColors.ShellGroundFor(set, theme);
            var content = WaveeColors.ContentSurfaceFor(set, theme);

            // grey on both rungs — the stock preset carries no tint
            Assert.Equal((wantGround, wantGround, wantGround), (Byte(ground.R), Byte(ground.G), Byte(ground.B)));
            Assert.Equal((wantContent, wantContent, wantContent), (Byte(content.R), Byte(content.G), Byte(content.B)));
            // …and the step is exactly one rung, in the lighter direction
            Assert.Equal(wantContent - wantGround, Byte(content.R) - Byte(ground.R));
        }
    }

    // The deterministic stack is TWO opaque rungs, and it has to be two opaque rungs in every preset and both themes —
    // not just in the neutral one the pins above measure. Opaque is the load-bearing half: the authenticated shell takes
    // the renderer's no-blend PSO, so a rung that quietly went translucent would sample whatever the compositor left
    // behind rather than a known colour.
    [Fact]
    public void TheOpaqueLadder_IsOneOpaqueStep_InEveryPresetAndBothThemes()
    {
        foreach (var p in All)
        {
            foreach (var (set, theme) in new (TokenSet, ThemeKind)[] { (p.Light, ThemeKind.Light), (p.Dark, ThemeKind.Dark) })
            {
                var ground = WaveeColors.ShellGroundFor(set, theme);
                var content = WaveeColors.ContentSurfaceFor(set, theme);
                string who = $"{p.Id}/{theme}";

                Assert.True(ground.A == 1f, $"{who}: the chrome GROUND must be opaque");
                Assert.True(content.A == 1f, $"{who}: the CONTENT rung must be opaque");
                // A real step, always AWAY from the ground toward more light — a content pane that sat below its own
                // ground would read as a well, and one that matched it would erase the ladder entirely.
                Assert.True(MaxChannelDelta(content, ground) > 0, $"{who}: the content rung is the ground repainted");
                Assert.True(Lum(content) > Lum(ground), $"{who}: the content rung must be LIGHTER than the ground");
            }
        }
    }

    // The floating surfaces are not a third and fourth rung: a floating stand-in for a docked CHROME band is the ground
    // colour, and a floating stand-in for the content band is the content rung. Same two values, two names — the names
    // mark intent (docked vs floating), and this is what stops a flyout from inventing its own grey.
    [Fact]
    public void FloatingSurfaces_AreTheOpaqueEquivalentsOfTheGroundAndTheContentRung()
    {
        Assert.Equal(WaveeColors.ShellGround, WaveeColors.FloatingChrome);
        Assert.Equal(WaveeColors.ContentSurface, WaveeColors.FloatingPane);
        Assert.Equal(1f, WaveeColors.FloatingChrome.A);
        Assert.Equal(1f, WaveeColors.FloatingPane.A);
        Assert.Equal(1f, WaveeColors.ContentSurface.A);
        // …and the active theme's rungs are the same values the pure overloads compute, so the gates above measure
        // exactly what the shell paints.
        var set = Tok.Theme == ThemeKind.Light ? Tok.Palette.Light : Tok.Palette.Dark;
        Assert.Equal(WaveeColors.ContentSurfaceFor(set, Tok.Theme), WaveeColors.ContentSurface);
        Assert.Equal(WaveeColors.ShellGroundFor(set, Tok.Theme), WaveeColors.ShellGround);
    }

    // ── the DOCKED contract: the content region is the translucent CONTENT LAYER over live Mica ──────────────────────

    // The content region and the right-rail band paint WaveeColors.FileArea, and the two properties that recipe rests on
    // have to hold in every preset and both themes:
    //   • TRANSLUCENT — the whole point is that the window material reads through the page. An opaque rung here would
    //     put a wallpaper-independent slab in the middle of a Mica window (and, in dark, the #282828 slab that inverted
    //     the model on light-tinted wallpapers: the page read DARKER than the chrome around it).
    //   • LIGHTENING — composited over the bare-Mica reference tone it must move AWAY from the base toward more light,
    //     in both themes. That direction is what makes the page read one step ABOVE the chrome rather than as a well.
    // The paired 1px left+top stroke is element-tree geometry (WaveeShell.ContentRegionStroke), not a token, so it
    // cannot be asserted here — but the fill half being a real, correctly-signed step is exactly what makes a 1px
    // hairline sufficient as the separator instead of a shadow or a gutter.
    [Fact]
    public void ContentLayerRung_IsTranslucentAndLightensTheMicaBase_InEveryPresetAndBothThemes()
    {
        foreach (var p in All)
        {
            foreach (var (shell, theme, mica) in new (ShellPalette, ThemeKind, ColorF)[]
            {
                (p.LightShell, ThemeKind.Light, MicaRef.LightDefault),
                (p.DarkShell, ThemeKind.Dark, MicaRef.DarkDefault),
            })
            {
                string who = $"{p.Id}/{theme}";
                var rung = shell.FileArea;
                Assert.True(rung.A > 0f && rung.A < 1f, $"{who}: the content rung must stay translucent — the base layer is the point");

                var over = ColorContrast.Flatten(rung, mica);
                Assert.Equal(1f, over.A);                                        // …and it still resolves to a solid page
                Assert.True(Lum(over) > Lum(mica), $"{who}: the content layer must LIGHTEN the base, not sink below it");
            }
        }
    }

    // The palette picker's swatch has to be the composite the shell actually produces — the translucent rung over the
    // no-wallpaper Mica reference — not the opaque rung the docked shell stopped painting. (MicaRef is a stand-in by
    // construction: live Mica has no colour without a desktop behind it. Documented on PresetSwatch.)
    [Fact]
    public void PresetSwatch_PreviewsTheContentLayerOverTheMicaReference()
    {
        foreach (var p in All)
        {
            var want = Tok.Theme == ThemeKind.Light
                ? ColorContrast.Flatten(p.LightShell.FileArea, MicaRef.LightDefault)
                : ColorContrast.Flatten(p.DarkShell.FileArea, MicaRef.DarkDefault);
            Assert.Equal(0, MaxChannelDelta(WaveeColors.PresetSwatch(p), want));
            Assert.Equal(1f, WaveeColors.PresetSwatch(p).A);   // a swatch must be a solid chip, never a see-through hole
        }
    }
}
