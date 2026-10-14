// ── Themes/Classic/Structure/Views/Dialogs/ClassicPlayLink.cs — Classic's `Dialog.PlayLink` (themes plan 6.1, T11a) ─────────────────
//
// The paste-a-link card (ch 19 W9-W13, section 6.7), moved from `Shell.Overlays.UI.cs`: a bare modal card (420 wide, 360..480) with the
// title, one single-line field (Enter commits through the field's own commit, so the card needs no key handler), a status line whose 18 DIP
// are reserved even when empty so the button row never jumps when a status appears, then the buttons: an "Open in browser" escape hatch only
// after a web url failed, Cancel, and the accent Start (disabled while empty or while a lookup runs). The core card keeps the field's text,
// the lookup (cancelled when the card closes), the status text, the failed input and the toast on failure.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's play-a-link card.</summary>
internal static class ClassicPlayLink
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogPlayLink"/>.</summary>
    public static Element Template(PlayLinkProps props, in ViewContext ctx)
    {
        var submit = props.Submit;
        var buttons = new List<Element>(3);
        if (props.OffersBrowser)
            buttons.Add(Button.Standard(Loc.Get(Strings.Play.OpenInBrowser), props.OpenInBrowser));
        buttons.Add(Button.Standard(Loc.Get(Strings.Auth.Cancel), props.Cancel) with { MinWidth = ClassicDialogRules.CardButtonMinWidth });
        buttons.Add(Button.Accent(Loc.Get(Strings.Play.Start), submit, isEnabled: props.CanPlay) with { MinWidth = ClassicDialogRules.CardButtonMinWidth });

        return ClassicModalCard.Plate(ClassicDialogRules.PlayLinkWidth, ClassicDialogRules.PlayLinkMinWidth, ClassicDialogRules.PlayLinkMaxWidth,
        [
            ClassicModalCard.Title(Loc.Get(Strings.Play.Title)),
            // Enter commits: the single-line field raises OnCommit, so the card needs no key handler of its own.
            TextBox.Create(props.Text(), null, new TextBox.TextBoxOptions
            {
                Placeholder = props.Placeholder, Width = ClassicDialogRules.PlayLinkFieldWidth, OnCommit = _ => submit(),
            }),
            // 18 DIP reserved even when empty, so the button row never jumps when a status appears.
            new BoxEl
            {
                MinHeight = ClassicDialogRules.PlayLinkStatusHeight, Direction = 0, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(props.Status)
                    {
                        Size = ClassicDialogRules.PlayLinkStatusSize, LineHeight = ClassicDialogRules.PlayLinkStatusLine, Color = Tok.TextSecondary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            },
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End, Margin = new Edges4(0f, Spacing.S, 0f, 0f),
                Children = buttons.ToArray(),
            },
        ]);
    }
}
