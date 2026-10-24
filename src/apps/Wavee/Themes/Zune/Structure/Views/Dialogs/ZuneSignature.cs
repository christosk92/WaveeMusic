// ── Themes/Zune/Structure/Views/Dialogs/ZuneSignature.cs — Zune's `Dialog.Signature` (themes plan 6.3, P5 Z8a) ───────────────────────────────────────
//
// The digital-signature sheet's BODY (no board draws it, owner Q18): a verdict badge beside the summary line (15/20, wrapping), one hairline, then stacked label (lowercase, the
// micro voice, tertiary) over value (13/18, wrapping; "-" when blank) rows. The sheet itself is a nested engine ContentDialog (the control table's square 548 plate) with Close
// as its one command and its default; the core opens it and resolves every string. A window shorter than the sheet scrolls the rows inside the card: the command stays where it is.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's signature sheet body.</summary>
internal static class ZuneSignature
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogSignature"/>.</summary>
    public static Element Template(SignatureSheetProps props, in ViewContext ctx)
    {
        var kids = new List<Element>(props.Rows.Count + 2)
        {
            new BoxEl
            {
                Key = "zsig:verdict", Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    InfoBadge.Icon(props.Trusted ? InfoBadgeSeverity.Success : InfoBadgeSeverity.Caution),
                    R.Type.TrackTitle(props.Summary) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Grow = 1f, MinWidth = 0f },
                ],
            },
            new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false },
        };
        for (int i = 0; i < props.Rows.Count; i++)
            kids.Add(Row(props.Rows[i]));
        // A window shorter than the sheet scrolls the rows inside the card: the Close command stays where it is.
        float max = ZuneOverlayTiers.Of(ctx.Tier()).Core.Dialog.BodyMaxHeight(
            ZuneOverlayRules.SignatureMaxHeight, ctx.Hooks.UseContextSignal(Viewport.Size).Value.Height);
        return new ScrollEl
        {
            ContentSized = true, MaxHeight = max, EdgeCues = ScrollEdgeCues.None,
            Content = new BoxEl { Direction = 1, Gap = ZuneOverlayRules.SignatureBodyGap, Children = kids.ToArray() },
        };
    }

    /// <summary>A stacked label (the micro voice, lowercase, tertiary) over its value (13/18, wrapping; "-" when blank).</summary>
    static Element Row(in SignatureSheetRow row) => new BoxEl
    {
        Direction = 1, Gap = ZuneOverlayRules.SignatureRowGap,
        Children =
        [
            ZuneText.Hint(row.Label),
            R.Type.DenseMeta(string.IsNullOrWhiteSpace(row.Value) ? "-" : row.Value) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f },
        ],
    };
}
