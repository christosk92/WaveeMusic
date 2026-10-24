// ── Themes/WinUi/Structure/Views/Material/WinUiMaterial.cs — WinUI's `Shell.Material` and its four cover leaves (P6 W2b) ─────────────
//
// WinUI draws no wash and no tint: the window's own material (Mica, Mica Alt or Acrylic, the style's `backdrop`) is the ground under the title bar, the
// pane and the player, and the content is ONE flat layer (the frame's). So the layer between the window's base and the chrome column paints NOTHING at any
// state, and the leaves, which the core mounts for the pages that colour themselves from artwork, do the same:
//
//   Material     nothing (the ground is the window's material; with the backdrop None the style's base role is solid).
//   PageTone     nothing: no page tone.
//   ArtistBlend  nothing: the artist's hero is the page's own picture (W6a).
//   ArtistVeil   a NEUTRAL veil (the layer colour, no hue): it darkens the photo for the title and never tints it.
//   TintBinder   publishes a definite "no colour", so the chrome stays the flat ground (the page that claims the slot still decides nothing here).
//
// The hand-over is the core's (`ShellMaterial.Publish` / `TintOwnership`); nothing here decides who owns the slot. Derived from Zune's material, which
// is the same shape (a flat ground, no cover wash under the default option).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's shell material layer and its four cover leaves.</summary>
internal static class WinUiMaterial
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellMaterialLayer"/>: a pass-through box that paints nothing.</summary>
    public static Element Template(MaterialProps props, in ViewContext ctx)
        => new BoxEl { Grow = 1f, HitTestVisible = false };

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverTone"/>.</summary>
    public static Element CoverTone(CoverToneProps props, in ViewContext ctx) => Embed.Comp(props, static () => new WinUiCoverNothing());

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverBlend"/>.</summary>
    public static Element CoverBlend(CoverBlendProps props, in ViewContext ctx) => Embed.Comp(props, static () => new WinUiCoverNothing());

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverVeil"/>.</summary>
    public static Element CoverVeil(CoverVeilProps props, in ViewContext ctx) => Embed.Comp(props, static () => new WinUiCoverVeil());

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverTint"/>.</summary>
    public static Element CoverTint(CoverTintProps props, in ViewContext ctx) => Embed.Comp(props, static () => new WinUiCoverTint());
}

/// <summary>A leaf WinUI draws no pixel for (the core mounts it, so it keeps its registration).</summary>
internal sealed class WinUiCoverNothing : Component
{
    public override Element Render() => new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
}

/// <summary>The photography veil: neutral (the layer colour, no hue), so the title stays legible on the photo. Sized like the core asks (a finite extent, or
/// NaN for the stretch arm).</summary>
internal sealed class WinUiCoverVeil : Component
{
    public override Element Render()
    {
        var p = UseProps<CoverVeilProps>();
        return new BoxEl
        {
            Width = p.Width, Height = p.Height, Grow = float.IsNaN(p.Height) ? 1f : 0f,
            ZStack = true, HitTestVisible = false,
            Children = [new BoxEl { HitTestVisible = false, Gradient = Controls.ArtistHeroVeil(Tok.FillLayerDefault, p.Vertical, p.Hold) }],
        };
    }
}

/// <summary>The shell tint publisher: WinUI has no tint, so it publishes a DEFINITE "no colour" once per claim and renders a 0 x 0 box.</summary>
internal sealed class WinUiCoverTint : Component
{
    public override Element Render()
    {
        var p = UseProps<CoverTintProps>();
        var claimedOnce = UseRef(false);
        const bool definite = true;
        ColorF? known = null;
        void Publish(bool isClaim) => p.Port.Publish(p.Owner, isClaim, definite, known, wash: null);
        UseEffect(() =>
        {
            Publish(isClaim: !claimedOnce.Value);
            claimedOnce.Value = true;
        }, DepKey.From(Tok.Epoch));
        // A KeepAlive reactivation always claims the slot back.
        UseActivation(onActivated: () => Publish(isClaim: true));
        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}
