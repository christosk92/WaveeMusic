// ── Themes/Classic/Structure/Views/Dialogs/ClassicSignature.cs — Classic's `Dialog.Signature` (themes plan 6.1, T11a) ──────────────
//
// The digital-signature sheet's BODY (ch 19 W33), moved from `Shell.Overlays.UI.cs`: a verdict badge beside the summary line (14/600, wrapping),
// a divider, then nine stacked label (11, secondary) over value (12, wrapping; "-" when blank) rows. The sheet itself is a nested engine
// ContentDialog at rung 4 (548) with Close as its one command and its default; the core opens it and resolves every string.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's signature sheet body.</summary>
internal static class ClassicSignature
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogSignature"/>.</summary>
    public static Element Template(SignatureSheetProps props, in ViewContext ctx)
    {
        var kids = new List<Element>(props.Rows.Count + 2)
        {
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    InfoBadge.Icon(props.Trusted ? InfoBadgeSeverity.Success : InfoBadgeSeverity.Caution),
                    new TextEl(props.Summary)
                    {
                        Size = ClassicDialogRules.SignatureSummarySize, LineHeight = ClassicDialogRules.SignatureSummaryLine, Weight = 600,
                        Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Grow = 1f, MinWidth = 0f,
                    },
                ],
            },
            new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault },
        };
        for (int i = 0; i < props.Rows.Count; i++)
            kids.Add(Row(props.Rows[i]));
        return new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids.ToArray() };
    }

    /// <summary>A stacked label (11 TextSecondary) over its value (12 TextPrimary, wrapping; "-" when blank).</summary>
    static Element Row(in SignatureSheetRow row) => new BoxEl
    {
        Direction = 1, Gap = ClassicDialogRules.SignatureRowGap,
        Children =
        [
            R.Type.MicroMeta(row.Label) with { Color = Tok.TextSecondary },
            new TextEl(string.IsNullOrWhiteSpace(row.Value) ? "-" : row.Value)
                { Size = ClassicDialogRules.SignatureValueSize, LineHeight = ClassicDialogRules.SignatureValueLine, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
        ],
    };
}
