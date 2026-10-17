// ── Themes/Zune/Structure/Views/Screens/ZuneReleaseNotes.Plate.cs — Zune's `ReleaseNotes.AfterUpdatePlate` (themes plan 6.3, P5 Z8b) ───────────────
//
// A raw Modal overlay, not a ContentDialog (which clamps its card): 720 wide on the dialog ground, Modal dismiss and chrome, the chrome's own smoke, no ClosingAction veto, so
// Escape closes it. The core's after-update chrome decides WHEN (the gates, the latch, the load: the plate opens only once the load landed, so every input here is final at mount)
// and opens the overlay; this template draws it. The check box is the only live state (the template's own hook cell). No board draws it (owner Q18): the welcome in the 26/300
// voice with the version range as one line of meta, the highlight cards of the page, the footer with "don't show again" at its start, "full release notes" as a word and "got it"
// as the round accent button. The window's overlay record sizes it (`ZuneReleaseNotesTiers.Overlay`): the plate fits the window less 16 a side, Compact stacks it, a Compact or
// Short window scrolls its body above a pinned footer.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.Zune.ZuneReleaseNotesRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneReleaseNotes
{
    /// <summary>The registered template of <see cref="ViewKeys.ReleaseNotesAfterUpdatePlate"/>.</summary>
    public static Element PlateTemplate(AfterUpdatePlateProps props, in ViewContext ctx)
    {
        var dontShow = ctx.Hooks.UseSignal(false);
        var vp = ctx.Hooks.UseContext(Viewport.Size);
        var ov = ZuneReleaseNotesTiers.Overlay(ctx.Tier());
        var layout = ZuneReleaseNotesTiers.For(ov.Tier);   // the card's numbers (its column count, its gap) in the window's tier
        var k = new ZuneKit(ZuneSettingsTiers.For(ov.Tier));
        var cards = props.Cards;
        var openCard = props.OpenCard;
        float padX = ov.PlatePadX;

        var scrolled = new List<Element>(3)
        {
            new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Padding = new Edges4(padX, K.PlateHeroPadTop, padX, K.PlateHeroPadBottom),
                Children =
                [
                    ZuneText.Hint(Loc.Get(Strings.WhatsNew.Dialog.Updated) + "  ·  " + props.VersionRange) with { Color = R.Ink.AccentAction, FontFamily = K.Mono },
                    R.Type.Heading(Strings.WhatsNew.Dialog.Welcome(props.VersionText)) with { Wrap = TextWrap.Wrap, MinWidth = 0f },
                    R.Type.DenseTitle(props.Tagline) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, MaxWidth = K.PlateTaglineMaxWidth },
                ],
            },
        };

        if (cards.Length > 0)
        {
            var row = new Element[cards.Length];
            for (int i = 0; i < cards.Length; i++)
            {
                int idx = i;
                row[i] = new BoxEl
                {
                    Key = "zdlg-hl:" + (cards[i].Highlight.Id is { Length: > 0 } id ? id : idx.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = ov.CardsColumn ? float.NaN : 0f, MinWidth = 0f, MaxWidth = ov.CardsColumn ? float.NaN : K.PlateCardMaxWidth,
                    Children = [Card(cards[i], () => openCard(idx), props.OpenStoreListing, layout, compact: true)],
                };
            }
            scrolled.Add(new BoxEl
            {
                Direction = (byte)(ov.CardsColumn ? 1 : 0), Gap = layout.HighlightGap, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
                Padding = new Edges4(padX, K.PlateRowPadTop, padX, K.PlateRowPadBottom), Children = row,
            });
        }

        // Written IMMEDIATELY, not on close.
        Element dontShowBox = k.Check(Loc.Get(Strings.WhatsNew.Dialog.DontShow), dontShow, props.SetDontShowAgain) with { Key = "zrn:plate:check" };
        // close THEN navigate: the reverse of the viewer's "try it" (this plate is the thing being replaced).
        Element full = k.Cmd(Loc.Get(Strings.WhatsNew.Dialog.Full), props.ShowFull) with { Key = "zrn:plate:full" };
        Element gotIt = k.Go(Loc.Get(Strings.WhatsNew.Dialog.GotIt), props.Close, glyph: Icons.Accept) with { Key = "zrn:plate:ok" };

        Element footer = new BoxEl
        {
            Key = "zrn:plate:footer", Direction = (byte)(ov.FooterStacked ? 1 : 0), Gap = Spacing.S, AlignItems = ov.FooterStacked ? FlexAlign.Stretch : FlexAlign.Center,
            MinWidth = 0f, Shrink = 0f, Padding = new Edges4(padX, K.PlateFooterPadY, padX, K.PlateFooterPadY), Fill = Tok.FillSolidSecondary,
            Children =
            [
                dontShowBox,
                new BoxEl { Direction = 0, Grow = 1f, Justify = FlexJustify.End, AlignItems = FlexAlign.Center, Gap = K.PlateActionGap, Wrap = true, MinWidth = 0f, Children = [full, gotIt] },
            ],
        };

        // A scrolling body sums its content up to the plate's cap (ContentSized, as the viewer's text does), then scrolls under the pinned footer.
        Element[] body = ov.ScrollBody
            ? [ScrollView(new BoxEl { Direction = 1, MinWidth = 0f, Children = scrolled.ToArray() }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ContentSized = true }, footer]
            : [.. scrolled, footer];

        return new BoxEl
        {
            Key = "zrn:plate", Width = ov.PlateWidth(vp.Width), Direction = 1, MaxHeight = ov.PlateMaxHeight(vp.Height), ClipToBounds = true, Fill = Tok.FillLayerAlt,
            Shadow = Elevation.Dialog, Children = body,
        };
    }
}
