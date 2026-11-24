// ── Themes/Common/CommonDropPanel.cs — the drop panel, one layout for WinUI and Classic (#240 §3.15, UX S12) ──────────────────
//
// Board frames 22-23 (WinUI; Classic uses the WinUI layout, so both register this one view; Zune draws its own band). Over the window's
// empty space during an OS drag the shell shows a card centred on the content with two to four large zones that fit what is dragged
// (songs: Play now · Play next · Add to queue; folders: Play · Add as a music folder · Add to queue; a playlist file: Play as a queue;
// Spotify links: Open · Play), each a glyph plate, the verb and its one-line consequence. The zone under the pointer lights: an accent
// stroke and tint and an accent plate. The core decides the zones and runs a drop (`DropAnywhere`); this draws them.
//
// TIERS: none needed. The card is at most its zones' width and keeps 16 from the window's edges; narrower, the zones shrink together and
// their words wrap (two lines each), the 226 zone height holding, so the card never changes height inside a drag.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>The drop panel (WinUI and Classic).</summary>
internal static class CommonDropPanel
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellFileDrop"/> (WinUI and Classic register it: one layout): the drop
    /// panel (#240 §3.15, board 22-23). A card centred on the content with two to four large zones side by side, each a glyph plate,
    /// the verb and its one-line consequence; the zone under the pointer lights (accent stroke and tint, an accent plate). The card
    /// fades in and out with WinUI's flyout timing and is removed while hidden, so it never catches a pointer it is not showing for.
    /// It re-renders once per drag (the zones); the hover, the counts and the centring are bound.</summary>
    public static Element Template(FileDropProps props, in ViewContext ctx)
    {
        var over = props.Over;
        if (props.Zones is not { } zonesOf || props.ZoneTarget is not { } zoneTarget) return new BoxEl { Grow = 1f, HitTestPassThrough = true };
        var looks = zonesOf.Value;   // subscribe: the zones change once per drag
        var hot = props.Hot;
        var centre = props.CentreOffset;
        var zones = new Element[looks.Length];
        for (int i = 0; i < looks.Length; i++) zones[i] = DropZone(looks[i], i, hot, zoneTarget(i));
        var card = new BoxEl
        {
            Key = CommonDropPanelRules.DropPanelKey, Direction = 0, Gap = CommonDropPanelRules.DropZoneGap, Padding = CommonDropPanelRules.DropPanelPadding,
            Width = CommonDropPanelRules.DropPanelMaxW(looks.Length), Shrink = 1f, MinWidth = 0f,
            Corners = CornerRadius4.All(Radii.Overlay), Fill = Prop.Of(static () => Tok.FillSolidBase),
            BorderColor = Prop.Of(static () => Tok.StrokeSurfaceDefault), BorderWidth = CommonDropPanelRules.DropStroke, Shadow = Elevation.Flyout,
            DropTarget = props.PanelTarget,
            Transform = Prop.Of(() => centre is null ? Affine2D.Identity : Affine2D.Translation(centre.Value.X, centre.Value.Y)),
            Children = zones,
        };
        // The boundary holds the whole full-bleed layer (a ZStack layer fills the stack), so the card inside is a real flex child that
        // shrinks with a narrow window; the layer fades in and out and is gone while hidden.
        return Flow.Show(() => over.Value, new BoxEl
        {
            // A centred ROW: the card is its main-axis child, so it keeps its zones' width and shrinks only where the window is narrower.
            Grow = 1f, HitTestPassThrough = true, Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Padding = CommonDropPanelRules.DropLayerPadding, Animate = CommonDropPanelRules.DropPanelMotion,
            Children = [card],
        });
    }

    /// <summary>One zone: a resting dashed tile and, over it, the lit plate whose opacity follows the hover (no re-render).</summary>
    static Element DropZone(DropZoneLook look, int index, IReadSignal<int>? hot, DropTargetSpec target)
    {
        Func<bool> lit = hot is null ? static () => false : () => hot.Value == index;
        var detail = look.Detail;
        return new BoxEl
        {
            Key = CommonDropPanelRules.DropZoneKey(index), ZStack = true, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
            MaxWidth = CommonDropPanelRules.DropZoneW, MinHeight = CommonDropPanelRules.DropZoneH, DropTarget = target,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, HitTestVisible = false, Corners = CornerRadius4.All(Radii.Card),
                    Fill = Prop.Of(static () => Tok.FillSubtleSecondary), BorderColor = Prop.Of(static () => Tok.StrokeControlStrongDefault),
                    BorderWidth = CommonDropPanelRules.DropStroke, BorderDashOn = CommonDropPanelRules.DropDash, BorderDashOff = CommonDropPanelRules.DropDash,
                },
                new BoxEl
                {
                    Grow = 1f, HitTestVisible = false, Corners = CornerRadius4.All(Radii.Card),
                    Fill = Prop.Of(static () => Tok.AccentDefault with { A = CommonDropPanelRules.DropHotTintA }),
                    BorderColor = Prop.Of(static () => Tok.AccentDefault), BorderWidth = CommonDropPanelRules.DropHotStroke,
                    Opacity = Prop.Of(() => lit() ? 1f : 0f),
                },
                new BoxEl
                {
                    Grow = 1f, HitTestVisible = false, Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                    Padding = CommonDropPanelRules.DropZonePadding,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = CommonDropPanelRules.DropGlyphPlate, Height = CommonDropPanelRules.DropGlyphPlate, Shrink = 0f,
                            Corners = CornerRadius4.All(CommonDropPanelRules.DropGlyphPlate / 2f), Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                            Fill = Prop.Of(() => lit() ? Tok.AccentDefault : Tok.FillControlDefault),
                            Margin = CommonDropPanelRules.DropGlyphMargin,
                            Children = [Ui.Icon(look.Glyph, CommonDropPanelRules.DropGlyph, family: look.GlyphFont) with
                                { Color = Prop.Of(() => lit() ? Tok.TextOnAccentPrimary : Tok.TextPrimary) }],
                        },
                        Ui.Subtitle(look.Title) with
                        {
                            Color = Tok.TextPrimary, TextAlign = TextAlign.Center, Wrap = TextWrap.Wrap, MaxLines = CommonDropPanelRules.DropTitleLines,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        Ui.Body("") with
                        {
                            Text = Prop.Of(() => detail()), Color = Tok.TextSecondary, TextAlign = TextAlign.Center,
                            MaxLines = CommonDropPanelRules.DropDetailLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                            Margin = CommonDropPanelRules.DropDetailMargin,
                        },
                    ],
                },
            ],
        };
    }
}
