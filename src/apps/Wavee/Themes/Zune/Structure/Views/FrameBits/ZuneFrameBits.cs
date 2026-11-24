// ── Themes/Zune/Structure/Views/FrameBits/ZuneFrameBits.cs — Zune's small frame views (themes plan 6.3; P5 Z2a) ────────────────────────
//
// No board draws these (owner Q18: built from Zune's parts). Each is a picture over a decision the core keeps:
//
//   History menu   the engine menu over the rows the core listed (newest first, "View all history" when the stack is longer); the style's
//                  menu entry makes it Zune's square plate with 36-tall rows inset 16.
//   File drop      the drop panel (#240 §3.15): a full-width band over the content, the zones as lowercase text verbs; the zone under the
//                  pointer takes the accent. Bound to the core's signals, so a drag hover never re-renders the shell.
//   Drag chip      the engine layer that draws the chip under the pointer; the core resolves what the dragged payload is called.
//   Cover scrim    the setup wizard's dim (the smoke fill, 250 ms linear, 0 under reduced motion; hit-test transparent unless dimming).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

using FluentGpu.Signals;

namespace Wavee.Themes.Zune;

/// <summary>Zune's small frame views.</summary>
internal static class ZuneFrameBits
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellHistoryMenu"/>.</summary>
    public static Element HistoryMenu(HistoryMenuProps props, in ViewContext ctx)
        => MenuFlyout.Create(props.Items, props.Close, size: OverlayFit.MenuSize) with { Key = ZuneFrameRules.HistoryMenuKey };

    /// <summary>The registered template of <see cref="ViewKeys.ShellFileDrop"/>: Zune's drop panel (#240 §3.15). A full-width band across
    /// the content (Zune's banners are bands), the zones as lowercase text verbs in pivot type with their consequence under each; the zone
    /// under the pointer takes the accent (its verb, and a bar along its foot). The verbs wrap onto a second row where the band is narrow,
    /// never shrink. The band fades in and out and is removed while hidden; it re-renders once per drag, the rest is bound.</summary>
    public static Element FileDrop(FileDropProps props, in ViewContext ctx)
    {
        var over = props.Over;
        if (props.Zones is not { } zonesOf || props.ZoneTarget is not { } zoneTarget) return new BoxEl { Grow = 1f, HitTestPassThrough = true };
        var looks = zonesOf.Value;   // subscribe: the zones change once per drag
        var hot = props.Hot;
        var centre = props.CentreOffset;
        var zones = new Element[looks.Length];
        for (int i = 0; i < looks.Length; i++) zones[i] = DropZone(looks[i], i, hot, zoneTarget(i));
        var band = new BoxEl
        {
            Key = ZuneFrameRules.DropBandKey, Direction = 0, Wrap = true, Gap = ZuneFrameRules.DropZoneGap, Padding = ZuneFrameRules.DropBandPadding,
            // Grow to the row, at most the band's widest: a definite width before the verbs wrap (a fixed width shrunk after the wrap
            // would leave the wrapped rows outside the band).
            Grow = 1f, MaxWidth = ZuneFrameRules.DropBandMaxW, Shrink = 1f, MinWidth = 0f,
            Corners = R.Shape.All(ShapeRole.Overlay), Fill = Prop.Of(static () => R.Layer.FloatingChrome), Shadow = Elevation.Flyout,
            DropTarget = props.PanelTarget,
            Transform = Prop.Of(() => centre is null ? Affine2D.Identity : Affine2D.Translation(centre.Value.X, centre.Value.Y)),
            Children = zones,
        };
        // The boundary holds the whole full-bleed layer (a ZStack layer fills the stack), so the card inside is a real flex child that
        // shrinks with a narrow window; the layer fades in and out and is gone while hidden.
        return Flow.Show(() => over.Value, new BoxEl
        {
            // A centred ROW: the band is its main-axis child, as wide as the content allows up to its widest.
            Grow = 1f, HitTestPassThrough = true, Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Padding = ZuneFrameRules.DropLayerPadding, Animate = ZuneFrameRules.DropBandMotion,
            Children = [band],
        });
    }

    /// <summary>One zone: the verb in pivot type (accent while lit), its consequence, and the lit bar along its foot.</summary>
    static Element DropZone(DropZoneLook look, int index, IReadSignal<int>? hot, DropTargetSpec target)
    {
        Func<bool> lit = hot is null ? static () => false : () => hot.Value == index;
        var detail = look.Detail;
        return new BoxEl
        {
            Key = ZuneFrameRules.DropZoneKey(index), Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = ZuneFrameRules.DropZoneMinW,
            Padding = ZuneFrameRules.DropZonePadding, DropTarget = target,
            Children =
            [
                R.Type.Pivot(look.Title) with
                {
                    Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f,
                    Color = Prop.Of(() => lit() ? Tok.AccentDefault : Tok.TextPrimary),
                },
                R.Type.TrackMeta("") with
                {
                    Text = Prop.Of(() => detail()), Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = ZuneFrameRules.DropDetailLines,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Case = R.Case.For(LabelKind.Label),
                },
                new BoxEl
                {
                    Height = ZuneFrameRules.DropLitBarH, Shrink = 0f, Margin = ZuneFrameRules.DropLitBarMargin, HitTestVisible = false,
                    Fill = Prop.Of(static () => Tok.AccentDefault), Opacity = Prop.Of(() => lit() ? 1f : 0f),
                },
            ],
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.ShellDragChip"/>.</summary>
    public static Element DragChip(DragChipProps props, in ViewContext ctx) => DragPreviewLayer.Of(props.Preview);

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverScrim"/>: the setup wizard's dim (the plate opens with the engine's
    /// own overlay scrim off, so this box is the only dim).</summary>
    public static Element CoverScrim(CoverScrimProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var mounted = hooks.UseRef(false);
        bool dim = props.Dim();
        float ms = Design.Reduced ? 0f : ZuneFrameRules.CoverScrimFadeMs;
        float target = dim ? 1f : 0f;
        hooks.UseTransition(AnimChannel.Opacity, mounted.Value ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(dim));
        mounted.Value = true;
        return new BoxEl { Grow = 1f, Fill = Tok.FillSmoke with { A = ZuneFrameRules.CoverScrimA }, Opacity = target, HitTestVisible = dim };
    }
}
