// ── Platform/ButtonRules.cs ────────────────────────────────────────────────────────────────────────────────────────
// The button-standardisation colour/geometry MATH, split out of Controls.cs/Controls.Cta.cs so it can be unit-tested
// without an engine host (docs/plans/wavee/home-redesign-implementation.md Workstream B, Group A).
//
// Role: CORE
// Owner: L
// Wave: 2
// Budget: 300 lines
// Spec: DERIVED
//
// Only FluentGpu.Foundation (ColorF, ColorContrast, MicaRef — plain math, no rendering) and FluentGpu.Dsl (Tok, the
// live token set) are touched. `FollowTint` itself never reads `Tok`: the "composited on-fill" it needs to judge the
// heart's contrast is a LITERAL calibration backdrop (`MicaRef`), the same anchor the engine's own gates solve
// against, so the tint math is pure and themeable by a bool the caller already has (never a `Tok.Theme` mutation —
// ch 00 keeps that mutation confined to DesignTests). `FollowStyle` composes the ToggleButton `Style` on top and DOES
// read `Tok`, because a `Style` is inherently a bag of live tokens (every other default style in the engine works the
// same way) — it is not a "no source-text tests" violation, it is ordinary token plumbing.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>The pure math behind the button-standardisation grammar (Workstream B): the Follow/Pre-save toggle's
/// accent tint ramp and the ToggleButton <see cref="ToggleButton.Style"/> built from it, plus the primary CTA's
/// nominal width. See `docs/plans/wavee/home-redesign-implementation.md` Workstream B for the state table this
/// mirrors exactly.</summary>
public static class ButtonRules
{
    /// <summary>The primary media CTA's floor width (<c>Controls.PrimaryButton</c>'s <c>PartRoot</c> min-width) —
    /// named here, not just as a literal in Controls.Cta.cs, so a layout rule elsewhere (a skeleton shape, a CTA-row
    /// metric) can reference the SAME number instead of copying 120.</summary>
    public const float PrimaryWidthNominal = 120f;

    /// <summary>The Play split's primary half FLOOR and its chevron half; with the 1-DIP divider between them they sum to
    /// <see cref="PlaySplitWidthNominal"/>, which replaces <see cref="PrimaryWidthNominal"/> on a detail page's command
    /// row. Named here so a layout rule (a skeleton shape, a CTA-row budget) reads the SAME numbers the control is built
    /// from.</summary>
    public const float PlaySplitPrimaryMinW = 88f, PlaySplitChevronW = 32f;

    /// <summary>The Play split's nominal width: primary floor + the 1-DIP divider + the chevron half (121).</summary>
    public const float PlaySplitWidthNominal = PlaySplitPrimaryMinW + 1f + PlaySplitChevronW;

    /// <summary>The verbs in the Play split's chevron menu.</summary>
    public enum PlaySplitVerb : byte { AddToQueue, PlayNext, StartRadio }

    /// <summary>The Play split's menu, in order — ALWAYS these three, on every container and in every state (the menu
    /// never grows or loses a row; a verb that cannot run says so with its own toast). PURE.</summary>
    public static ReadOnlySpan<PlaySplitVerb> PlaySplitItems
        => [PlaySplitVerb.AddToQueue, PlaySplitVerb.PlayNext, PlaySplitVerb.StartRadio];

    /// <summary>The six colours the Follow/Pre-save toggle's ON state needs, derived from the page accent
    /// <paramref name="a"/> and the theme (<paramref name="light"/> — a value, never a global read, so this stays
    /// testable both ways in the same process). Mirrors the plan's state table exactly:
    /// <list type="bullet">
    /// <item>On rest fill: A@.14 dark / .10 light.</item>
    /// <item>On hover fill: A@.20 / .15.</item>
    /// <item>On pressed fill: A@.10 / .07.</item>
    /// <item>On rest/hover stroke: A@.50 / .60 (WinUI pins the hover border to the rest border — one value covers
    /// both, per <see cref="ToggleButton.Style.OnHoverBorder"/>'s own doc).</item>
    /// <item>On pressed stroke: A@.35 / .42.</item>
    /// </list>
    /// <paramref name="a"/>'s own alpha channel is ignored — every returned swatch carries exactly the tier alpha
    /// above, so a caller passing a translucent accent by mistake cannot double up.</summary>
    public static (ColorF RestFill, ColorF Hover, ColorF Pressed, ColorF Stroke, ColorF PressedStroke, ColorF HeartInk)
        FollowTint(ColorF a, bool light)
    {
        ColorF opaque = a with { A = 1f };
        float restA = light ? 0.10f : 0.14f;
        float hoverA = light ? 0.15f : 0.20f;
        float pressedA = light ? 0.07f : 0.10f;
        float strokeA = light ? 0.60f : 0.50f;
        float pressedStrokeA = light ? 0.42f : 0.35f;

        ColorF rest = opaque with { A = restA };
        ColorF hover = opaque with { A = hoverA };
        ColorF pressed = opaque with { A = pressedA };
        ColorF stroke = opaque with { A = strokeA };
        ColorF pressedStroke = opaque with { A = pressedStrokeA };

        // HeartInk: the accent itself reads as the glyph ink IF it clears WCAG 3:1 (the large-content-ish threshold a
        // 14px glyph on a small plate is judged against) once composited over the resting on-fill; otherwise the
        // theme's own accent-on-neutral ink, which is guaranteed legible on ordinary page chrome. The composite needs
        // an OPAQUE backdrop to flatten against — `MicaRef`'s calibration tone for the theme stands in for "whatever
        // page surface this toggle sits on" (the same anchor the engine's own contrast gates use), not a live read of
        // the actual window material.
        ColorF backdrop = light ? MicaRef.LightDefault : MicaRef.DarkDefault;
        ColorF onFill = ColorContrast.Flatten(rest, backdrop);
        // The BAKED per-theme token (Tok.Light/Tok.Dark), never the live Tok.AccentTextPrimary — that reads whichever
        // theme happens to be ambient right now (plus any OS/dev accent override), which would make this "pure"
        // function's answer depend on global mutable state instead of the `light` argument the caller already
        // resolved. Same reasoning DesignTests documents for why its own palette facts take a theme PARAMETER instead
        // of mutating `Tok.Theme`.
        ColorF fallbackInk = (light ? Tok.Light : Tok.Dark).AccentTextPrimary;
        ColorF heartInk = ColorContrast.Ratio(opaque, onFill) >= 3.0f ? opaque : fallbackInk;

        return (rest, hover, pressed, stroke, pressedStroke, heartInk);
    }

    /// <summary>The Follow/Pre-save <see cref="ToggleButton.Style"/>: the OFF arm rides <see
    /// cref="ToggleButton.DefaultStyle"/> verbatim (the plan's off row IS the stock Standard ramp — TextPrimary heart,
    /// ControlElevationBorder), and only the ON arm + glyph/motion knobs are overridden from <see cref="FollowTint"/>.
    /// 32/r4 geometry (the new button-standardisation ladder), not the old 36/capsule Follow pill.
    /// <para><paramref name="onMedia"/> (the artist bleed's dark hero, either theme): the tint ramp is the dark one and the
    /// "Following" label is the dark theme's accent rung, so it reads on the near-black field; the OFF arm keeps its
    /// stock plate.</para></summary>
    public static ToggleButton.Style FollowStyle(ColorF a, bool light, bool onMedia = false)
    {
        if (onMedia) light = false;
        var t = FollowTint(a, light);
        return ToggleButton.DefaultStyle with
        {
            CornerRadius = Radii.Control,
            MinHeight = Controls.ButtonHeight,

            OnBackground = t.RestFill,
            OnHover = t.Hover,
            OnPressed = t.Pressed,
            OnBorder = GradientSpec.Solid(t.Stroke),
            OnHoverBorder = GradientSpec.Solid(t.Stroke),     // pinned to rest, per the table's "same" hover stroke
            OnPressedBorder = GradientSpec.Solid(t.PressedStroke),
            OnDisabledBackground = Tok.FillControlDisabled,   // the table's one "Disabled" row is the neutral ramp,
            OnDisabledBorder = GradientSpec.Solid(Tok.StrokeControlDefault), // not a washed-out accent — a disabled
            OnDisabledForeground = Tok.TextDisabled,          // Follow toggle reads as the neutral disabled control.

            OnForeground = onMedia ? Tok.Dark.AccentTextPrimary : Tok.AccentTextPrimary, // "Following" label ink, rest + hover
                                                                // (WinUI pins hover to rest — ToggleButton.Build never re-reads it)
            OnPressedForeground = onMedia ? Design.OnMedia.InkSecondary : Tok.TextSecondary, // the table's pressed-label pull-back to neutral ink

            OnGlyphForeground = t.HeartInk,
            OffGlyphForeground = null,                        // ride the label's own TextPrimary ramp (off row)

            GlyphSize = 14f,
            GlyphGap = Spacing.XS,

            // 260ms FluentDecelerate root reflow (a checkedLabel swap widening "Follow" → "Following") and a matching
            // label cross-fade. Both are STRUCTURAL motion (BoxEl.Animate), which the engine's own reduced-motion snap
            // policy already consults — no app-side reduced-motion branch needed here (Design.cs §9's rule: only the
            // hover/press SCALE channel needs one, and this toggle carries none).
            ContentReflow = new LayoutTransition(TransitionChannels.Size,
                TransitionDynamics.Tween(260f, Easing.FluentDecelerate), SizeMode.Relayout),
            LabelSwap = new LayoutTransition(TransitionChannels.Opacity,
                TransitionDynamics.Tween(260f, Easing.FluentDecelerate),
                Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),

            CheckedPopScale = 1.18f,
            CheckedPopMs = 250f,
        };
    }
}
