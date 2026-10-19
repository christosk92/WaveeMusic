// ── Themes/WinUi/Structure/Views/Dialogs/WinUiModalCard.cs — WinUI's bare modal plate (themes plan 6.1, T11a; TC9 tiers) ──────
//
// The off-ladder modal plate the log-out card and the play-a-link card share (ch 19): FillSolidBase, a 1 px surface stroke, the dialog
// elevation, r8, padded 24 with a 12 gap. Not a ContentDialog: no title band, no separator, no command-row fill. The popup around it
// (modal chrome, focus trap, no light dismiss) is the core's `PopupOptions`.
//
// TC9: the plate's width, padding and button row come from the window band's record (`WinUiOverlayTiers`): at Compact it fills the window less
// 16 either side (board C-Dialogs-Compact), with 16/20 padding and 40-tall buttons sharing the row; at every other band it keeps its own width,
// never wider than the window less the margin, so a narrow Small window never overflows it.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.WinUi;

/// <summary>The bare modal plate.</summary>
internal static class WinUiModalCard
{
    /// <summary>The plate: <paramref name="width"/> wide at the bands that keep their own width, bounded by <paramref name="max"/> and the window.</summary>
    public static Element Plate(in ViewContext ctx, float width, float max, Element[] children)
    {
        var tier = WinUiOverlayTiers.For(ctx.Tier().Window);
        float viewport = ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width;
        float w = tier.CardWidth(width, max, viewport);
        return new BoxEl
        {
            Direction = 1, Width = w, MaxWidth = w,
            Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
            Shadow = Elevation.Dialog, Padding = tier.CardPadding, Gap = tier.CardGap ?? Spacing.M,
            Children = children,
        };
    }

    /// <summary>The card's title.</summary>
    public static Element Title(string text) => new TextEl(text)
    {
        Size = WinUiDialogRules.CardTitleSize, LineHeight = WinUiDialogRules.CardTitleLine, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
    };

    /// <summary>The button row. Where the band's buttons are tall (Compact: 40) they share the row equally; otherwise they sit right-aligned at the
    /// control's own height. <paramref name="buttons"/> are <see cref="BoxEl"/>s (the stock buttons).</summary>
    public static Element Buttons(in ViewContext ctx, params BoxEl[] buttons)
    {
        var tier = WinUiOverlayTiers.For(ctx.Tier().Window);
        float h = tier.CardButtonHeight;
        if (h <= 0f)
            return new BoxEl { Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End, Margin = new Edges4(0f, Spacing.M, 0f, 0f), Children = buttons };
        var shared = new Element[buttons.Length];
        for (int i = 0; i < buttons.Length; i++)
            shared[i] = buttons[i] with { Grow = 1f, Basis = 0f, MinWidth = 0f, Height = h, MinHeight = h, Justify = FlexJustify.Center };
        return new BoxEl { Direction = 0, Gap = Spacing.S, Margin = new Edges4(0f, Spacing.S, 0f, 0f), Children = shared };
    }
}
