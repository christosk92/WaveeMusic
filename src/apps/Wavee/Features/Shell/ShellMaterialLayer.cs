using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

// The shell's MATERIAL layer — everything between the window's BASE LAYER and the chrome column. That base is live Mica,
// full stop: the shell root paints nothing at all (WaveeColors.ShellGround is the no-Mica fallback value and the flatten
// base for opaque floating surfaces, NOT something painted here). Two forms, mutually exclusive by construction:
//   • Tint — one flat full-bleed rect (detail pages publish one colour).
//   • Wash — up to three clipped radial gradients (Home publishes one per artwork-bearing module).
//
// A COMPONENT rather than inline elements in WaveeShell for two reasons, both engine contracts:
//   • GradientSpec is NOT a Prop, so a wash colour can only change by re-rendering. Isolating that here keeps a material
//     change off the shell's own render path (the shell must never re-render on navigation).
//   • The implicit BrushTransition arms only on a re-rendered STATIC fill — a BOUND channel owns paint.Fill and is
//     excluded (Reconciler, "a BOUND fill is excluded per-channel"). Rendering the tint from a subscribed component with
//     a static fill is therefore what makes a page→page tint swap cross-fade instead of snapping.
// Re-renders at navigation rate only; the viewport is read through BOUND size props so a resize never reaches here.
sealed class ShellMaterialLayer : Component
{
    readonly IReadSignal<ShellMaterialState> _material;
    readonly IReadSignal<Size2> _viewport;

    public ShellMaterialLayer(IReadSignal<ShellMaterialState> material, IReadSignal<Size2> viewport)
    {
        _material = material;
        _viewport = viewport;
    }

    // Gradients carry no brush-fade channel, so a wash cross-fades by MOUNT: each layer is keyed on its artwork, so a
    // new grading exits the old layer and enters the new one over the same pixels. Read as a VALUE, never a hook branch.
    static EnterExit? WashFade => Motion.ReducedMotion ? null : new EnterExit(Opacity: 0f, Active: true);

    public override Element Render()
    {
        var state = _material.Value;     // subscribe → re-render on a tint / wash change (navigation rate)
        bool light = Tok.Theme == ThemeKind.Light;

        Element[] kids;
        if (state.Wash is { } wash)
        {
            var hero = Wash(wash.Hero, ShellWashGeometry.Hero, ShellWashGeometry.HeroAlpha(light), "shell.wash.hero");
            var weekly = Wash(wash.Weekly, ShellWashGeometry.Weekly, ShellWashGeometry.ShelfAlpha(light), "shell.wash.weekly");
            var mix = Wash(wash.Mix, ShellWashGeometry.Mix, ShellWashGeometry.ShelfAlpha(light), "shell.wash.mix");
            kids = [Tint(state.Tint), WashHost([.. Only(hero), .. Only(weekly), .. Only(mix)])];
        }
        else kids = [Tint(state.Tint)];

        return new BoxEl { Grow = 1f, ZStack = true, HitTestVisible = false, Children = kids };
    }

    /// <summary>THE WASHES STOP AT THE DOCK LINE. The player dock paints nothing (it is a Mica-passthrough omission
    /// like every other chrome band), so whatever the material layer paints under it IS the dock. The Mix wash is
    /// bottom-anchored with its ellipse centre at window y = 1.00, i.e. its PEAK alpha landed exactly across the dock
    /// band — which is what read as "the dock has a pastel gradient". It never had a gradient; it had the shell's.
    ///
    /// <para>The fix is a host box inset by <see cref="PlayerDock.Reserve"/> at the bottom, clipping what it holds. Of
    /// the three placements only Mix hangs off the bottom edge (Hero and Weekly both clamp to y0 = 0 and are
    /// TOP-anchored), so Hero and Weekly are bit-for-bit unmoved and Home's approved look above the dock — the D20–D23
    /// decisions, which are about wash SOURCE and cross-fade identity, not geometry — is untouched. Mix translates up
    /// by the dock height and is cut at that line.</para>
    ///
    /// <para>A MARGIN rather than a re-anchored ellipse, deliberately: <c>ShellWashPlacement.Center</c>/<c>Radius</c>
    /// are node-relative CONSTANTS precisely because the box is a fraction of the window, and <c>GradientSpec</c> is
    /// not a Prop — it can only change by re-rendering, which happens at navigation rate. Subtracting a fixed 72 DIP
    /// from the box HEIGHT would make those two ratios viewport-dependent and they would go stale on the next resize.
    /// Insetting the whole host keeps every ratio constant and costs one box.</para>
    /// <para>The flat TINT stays full-bleed: it is a uniform low-alpha scrim with no peak to land anywhere, so the dock
    /// carrying it is the page's colour reaching the whole window, which is the intent.</para></summary>
    static Element WashHost(Element[] washes) => new BoxEl
    {
        Grow = 1f, ZStack = true, HitTestVisible = false, ClipToBounds = true,
        Margin = new Edges4(0f, 0f, 0f, PlayerDock.Reserve),
        Children = washes,
    };

    static Element[] Only(Element? e) => e is null ? [] : [e];

    // One flat full-bleed layer. Always mounted (even at Transparent) so the node is LIVE across a material change and
    // the BrushTransition has a previous colour to fade FROM.
    static Element Tint(ColorF? tint) => new BoxEl
    {
        Key = "shell.material.tint",
        Grow = 1f, HitTestVisible = false,
        Fill = tint ?? ColorF.Transparent,
        BrushTransitionMs = WaveeMotion.Standard,
    };

    Element? Wash(WashLayer? layer, in ShellWashPlacement p, float alpha, string key)
    {
        if (layer is not { } w) return null;
        var vp = _viewport;
        float wf = p.W, hf = p.H;
        return new BoxEl
        {
            // Keyed on the ARTWORK, so re-grading remounts (Exit old + Enter new = the cross-fade); the same artwork
            // through a theme flip keeps the node and simply re-records its stops.
            Key = key + ":" + (w.ArtworkKey ?? ""),
            HitTestVisible = false,
            // The clipped box: a fraction of the live window, parked on the window edge its ellipse hangs off. Bound, so
            // a resize re-scales the layer without re-rendering this component.
            Width = Prop.Of(() => vp.Value.Width * wf),
            Height = Prop.Of(() => vp.Value.Height * hf),
            JustifySelf = p.AnchorRight ? FlexAlign.End : FlexAlign.Start,
            AlignSelf = p.AnchorBottom ? FlexAlign.End : FlexAlign.Start,
            // Straight-alpha stop interpolation: the transparent stop MUST carry the wash's own RGB. ColorF.Transparent
            // is premultiplied-black and would drag the ramp's hue toward black across the whole falloff.
            Gradient = new GradientSpec(GradientShape.Radial, 0f,
            [
                new GradientStop(0f, w.Color with { A = alpha }),
                new GradientStop(p.FadeOffset, w.Color with { A = 0f }),
            ])
            {
                RadialCenter = p.Center,
                RadialRadius = p.Radius,
            },
            Enter = WashFade,
            Exit = WashFade,
        };
    }
}
