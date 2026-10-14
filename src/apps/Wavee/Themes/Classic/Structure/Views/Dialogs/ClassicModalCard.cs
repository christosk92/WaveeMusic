// ── Themes/Classic/Structure/Views/Dialogs/ClassicModalCard.cs — Classic's bare modal plate (themes plan 6.1, T11a) ──────────────
//
// The off-ladder modal plate the log-out card and the play-a-link card share (ch 19): FillSolidBase, a 1 px surface stroke, the dialog
// elevation, r8, padded 24 with a 12 gap. Not a ContentDialog: no title band, no separator, no command-row fill. The popup around it
// (modal chrome, focus trap, no light dismiss) is the core's `PopupOptions`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The bare modal plate.</summary>
internal static class ClassicModalCard
{
    /// <summary>The plate: <paramref name="width"/> wide, bounded by <paramref name="min"/> and <paramref name="max"/>.</summary>
    public static Element Plate(float width, float min, float max, Element[] children) => new BoxEl
    {
        Direction = 1, Width = width, MinWidth = min, MaxWidth = max,
        Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
        Shadow = Elevation.Dialog, Padding = Edges4.All(ClassicDialogRules.CardPadding), Gap = Spacing.M,
        Children = children,
    };

    /// <summary>The card's title.</summary>
    public static Element Title(string text) => new TextEl(text)
    {
        Size = ClassicDialogRules.CardTitleSize, LineHeight = ClassicDialogRules.CardTitleLine, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
    };
}
