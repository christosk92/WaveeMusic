// ── Themes/Zune/Structure/Views/Dialogs/ZunePlayLink.cs — Zune's `Dialog.PlayLink` (themes plan 6.3, P5 Z8a) ────────────────────────────────────────
//
// The paste-a-link card (no board draws it, owner Q18: the Z-Dialogs plate and its ruled field): a bare modal plate (480 wide, to 548) with the title, one single-line field
// (the control table's rule, Enter commits through the field's own commit so the card needs no key handler), a status line whose 18 DIP are reserved even when empty so the
// commands never jump when a status appears, then the commands: the accent disc with "start" (disabled while empty or while a lookup runs), an "open in browser" word only after a
// web url failed, and "cancel". The core card keeps the field's text, the lookup (cancelled when the card closes), the status text, the failed input and the toast on failure.
// At Compact the field is the 40-DIP one.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's play-a-link card.</summary>
internal static class ZunePlayLink
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogPlayLink"/>.</summary>
    public static Element Template(PlayLinkProps props, in ViewContext ctx)
    {
        var submit = props.Submit;
        var layout = ZuneOverlayTiers.Of(ctx.Tier());
        var kit = ZuneKit.Of(ctx.Tier());
        // The field is the plate less its padding: derived from the tier's own record, never typed.
        float field = ZuneModalPlate.InnerWidth(layout, ZuneOverlayRules.PlayLinkWidth, ZuneOverlayRules.PlayLinkMaxWidth, ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width);

        var commands = new List<Element>(3) { kit.Go(Loc.Get(Strings.Play.Start), submit, enabled: props.CanPlay) };
        if (props.OffersBrowser) commands.Add(kit.Cmd(Loc.Get(Strings.Play.OpenInBrowser), props.OpenInBrowser));
        commands.Add(kit.Cmd(Loc.Get(Strings.Auth.Cancel), props.Cancel));

        return ZuneModalPlate.Plate(in ctx, ZuneOverlayRules.PlayLinkWidth, ZuneOverlayRules.PlayLinkMaxWidth,
        [
            ZuneModalPlate.Title(Loc.Get(Strings.Play.Title)),
            // Enter commits: the single-line field raises OnCommit, so the card needs no key handler of its own.
            TextBox.Create(props.Text(), null, new TextBox.TextBoxOptions
            {
                Placeholder = props.Placeholder, Width = field, Height = layout.FieldHeight, OnCommit = _ => submit(),
            }),
            // 18 DIP reserved even when empty, so the commands never jump when a status appears.
            new BoxEl
            {
                Key = "zplaylink:status", MinHeight = ZuneOverlayRules.PlayLinkStatusHeight, Direction = 0, AlignItems = FlexAlign.Center,
                Children =
                [
                    R.Type.MicroMeta(props.Status) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                ],
            },
            ZuneModalPlate.Commands(commands.ToArray()),
        ]);
    }
}
