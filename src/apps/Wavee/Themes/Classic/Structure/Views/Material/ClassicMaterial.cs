// ── Themes/Classic/Structure/Views/Material/ClassicMaterial.cs — Classic's `Shell.Material` (themes plan 6.1, T4c) ───────────
//
// From `Shell.Masthead.UI.cs` (`MaterialLayerView`) and `Design.cs` §17 (the four cover leaves, `ClassicCoverLeaves.cs`).
//
// The material between the window's base layer (live Mica; the root paints nothing) and the chrome column: a flat page tint, or
// Home's three clipped radial washes. A TEMPLATE re-rendering at NAVIGATION rate (it reads the published state, never a resize: the
// washes' sizes are bound to the viewport), not inline elements: a GradientSpec is not a Prop (a wash changes only by re-rendering),
// and the implicit brush transition arms only on a re-rendered STATIC fill, so the tint cross-fades page to page.
//
// THE HAND-OVER IS CORE (`ShellMaterial.Publish` / `TintOwnership`, `Platform/ShellMaterial.cs`): the page that last CLAIMED the slot
// owns it, and nothing here writes. The state this view is handed is the slot AS OF THE PAGE ON SCREEN (`Shell.PresentedMaterial`,
// tuning D15): a navigation's new colour arrives when its page does, and the tint cross-fades there.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's shell material layer.</summary>
internal static class ClassicMaterial
{
    /// <summary>Gradients carry no brush-fade channel, so a wash cross-fades BY MOUNT (keyed on its artwork). A VALUE under reduced
    /// motion, never a hook branch.</summary>
    static EnterExit? WashFade => Design.Reduced ? null : new EnterExit(Opacity: 0f, Active: true);

    /// <summary>"The navigation pane is docked beside the content card", provided by <see cref="ClassicFrame"/> (the core's
    /// <c>ShellFrameProps.SidebarPaneDocked</c>). Null where no frame provides it (a view mounted alone): read as undocked.</summary>
    internal static readonly Context<Func<bool>?> PaneDockedSlot = new(null);

    /// <summary>Colour washes are drawn: Classic's Surfaces option is anything but Off (Subtle with no store, as a bare unit test
    /// reads it). A tracked read: the caller re-renders when the option changes.</summary>
    internal static bool WashesOn(IOptionReader? options) => ClassicStructure.WashesOn(Level(options));

    /// <summary>Classic's Surfaces level (Subtle with no store). A tracked read: the caller re-renders when the option changes.</summary>
    internal static SurfaceWash Level(IOptionReader? options)
        => options is null ? ClassicStructure.Surfaces.Default : options.Get(ClassicStructure.Surfaces.Key);

    /// <summary>The registered template of <see cref="ViewKeys.ShellMaterialLayer"/>.</summary>
    public static Element Template(MaterialProps props, in ViewContext ctx)
    {
        var state = props.State.Value;                              // navigation rate
        // Surfaces Off: the layer paints the neutral ground whatever the pages published (the core publishes data; the theme decides
        // whether it is drawn), so Home's and Recents' washes leave and the tint fades to neutral in place. Rich: the style's stronger pairs.
        var level = Level(ctx.Options);
        if (!ClassicStructure.WashesOn(level)) state = state with { Tint = null, Wash = null };
        bool rich = ClassicStructure.RichWashes(level);
        var vp = ctx.Hooks.UseContextSignal(Viewport.Size);         // read through BOUND sizes: a resize never re-renders this

        // Always mounted, even at the neutral ground, so the node is live across a change and the brush transition has
        // a colour to fade FROM. Never Transparent (premultiplied black drags the ramp dark).
        Element tint = new BoxEl
        {
            Key = "shell.material.tint", Grow = 1f, HitTestVisible = false,
            Fill = state.Tint ?? R.Wash.NeutralGround,
            BrushTransitionMs = Design.Motion.Standard,
        };
        if (state.Wash is not { } wash)
            return new BoxEl { Grow = 1f, ZStack = true, HitTestVisible = false, Children = [tint] };

        var legs = new List<Element>(3);
        var strength = R.Wash;
        AddWash(legs, wash.Hero, Design.Wash.Hero, strength.HeroAlphaAt(rich), "shell.wash.hero", vp);
        AddWash(legs, wash.Weekly, Design.Wash.Weekly, strength.ShelfAlphaAt(rich), "shell.wash.weekly", vp);
        AddWash(legs, wash.Mix, Design.Wash.Mix, strength.ShelfAlphaAt(rich), "shell.wash.mix", vp);
        return new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestVisible = false,
            Children =
            [
                tint,
                // THE WASHES STOP AT THE DOCK LINE: the dock paints nothing, so a peak landing under it read as "the
                // dock has a gradient". An inset MARGIN keeps every placement ratio viewport-independent.
                new BoxEl
                {
                    Grow = 1f, ZStack = true, HitTestVisible = false, ClipToBounds = true,
                    Margin = new Edges4(0f, 0f, 0f, Design.Wash.HostBottomInset),
                    Children = legs.ToArray(),
                },
            ],
        };
    }

    static void AddWash(List<Element> legs, WashLayer? layer, ShellWashPlacement p, float alpha, string key, IReadSignal<Size2> vp)
    {
        if (layer is not { } w) return;
        float wf = p.W, hf = p.H;
        legs.Add(new BoxEl
        {
            // Keyed on the ARTWORK: a re-grade remounts (the cross-fade); a theme flip keeps the node and re-records.
            Key = key + ":" + (w.ArtworkKey ?? ""),
            HitTestVisible = false,
            Width = Prop.Of(() => vp.Value.Width * wf),
            Height = Prop.Of(() => vp.Value.Height * hf),
            JustifySelf = p.AnchorRight ? FlexAlign.End : FlexAlign.Start,
            AlignSelf = p.AnchorBottom ? FlexAlign.End : FlexAlign.Start,
            // Straight-alpha stops: the transparent stop carries the wash's own RGB.
            Gradient = new GradientSpec(GradientShape.Radial, 0f,
            [
                new GradientStop(0f, w.Color with { A = alpha }),
                new GradientStop(p.FadeOffset, Design.Wash.Vanish(w.Color)),
            ])
            {
                RadialCenter = p.Center,
                RadialRadius = p.Radius,
            },
            Enter = WashFade,
            Exit = WashFade,
        });
    }

    // ── the cover leaves ────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Each leaf is a Component (the cover-colour watch must sit in a LEAF, never in a page's Render: a graded batch would re-render
    // the whole Artist/Detail/Home tree). Their inputs change while they stay mounted, so the template mounts the component with the
    // props record as its re-pushed props, exactly as the pages' mount helpers did before the move.

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverTone"/>.</summary>
    public static Element CoverTone(CoverToneProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicCoverTone());

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverBlend"/>.</summary>
    public static Element CoverBlend(CoverBlendProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicCoverBlend());

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverVeil"/>.</summary>
    public static Element CoverVeil(CoverVeilProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicCoverVeil());

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverTint"/>.</summary>
    public static Element CoverTint(CoverTintProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicCoverTint());
}
