// ── Themes/WinUi/Structure/Views/Screens/WinUiReleaseNotes.Plate.cs — Classic's `ReleaseNotes.AfterUpdatePlate` (themes plan 6.1, T11c) ─────────
//
// From `Screens\ReleaseNotes.UI.cs` §7: the plate. A raw Modal overlay, not ContentDialog (which clamps its card): 720 wide, Modal dismiss + chrome, the chrome's own
// smoke, no ClosingAction veto, so Escape closes it. The core's after-update chrome decides WHEN (the gates, the latch, the load: the plate opens only once the load
// landed, so every input here is final at mount) and opens the overlay; this template draws it. The checkbox is the only live state (the template's own hook cell).
// Geometry comes from `WinUiReleaseNotesRules.Card` — the same numbers the 620-DIP plate-cap test asserts — and the window's overlay record
// (`WinUiReleaseNotesTiers.Overlay`: the plate fits the window less 16, Compact stacks it, a Compact or Short window scrolls its body above a pinned footer).

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
using K = Wavee.Themes.WinUi.WinUiReleaseNotesRules;
using M = Wavee.Themes.WinUi.WinUiReleaseNotesRules.Card;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiReleaseNotes
{
    /// <summary>The registered template of <see cref="ViewKeys.ReleaseNotesAfterUpdatePlate"/>. The window's overlay record sizes it: the 720 plate shrinks to the window less
    /// 16 a side; at Compact its cards are ROW cards in one column over a stacked 40-high footer; at Compact and in a Short window the body (hero and cards) scrolls above a
    /// pinned footer, so the buttons are never cut off. Standard is the tuned plate: one unscrolled column under the 620 cap.</summary>
    public static Element PlateTemplate(AfterUpdatePlateProps props, in ViewContext ctx)
    {
        var dontShow = ctx.Hooks.UseSignal(false);
        var vp = ctx.Hooks.UseContext(Viewport.Size);
        var ov = WinUiReleaseNotesTiers.Overlay(ctx.Tier());
        var cards = props.Cards;
        var openCard = props.OpenCard;
        var showFull = props.ShowFull;
        float padX = ov.PlatePadX;

        var scrolled = new List<Element>(3)
        {
            new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Padding = new Edges4(padX, M.HeroPadTop, padX, M.HeroPadBottom),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true,
                        Children =
                        [
                            Pill(Loc.Get(Strings.WhatsNew.Dialog.Updated), accent: true),
                            // LEFT: the previous run's quad with its fourth part dropped; RIGHT: the running quad verbatim.
                            Pill(props.VersionRange, mono: true),
                        ],
                    },
                    new TextEl(Strings.WhatsNew.Dialog.Welcome(props.VersionText)) { Size = K.PlateWelcomeSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                    new TextEl(props.Tagline) { Size = K.PlateTaglineSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = K.PlateTaglineMaxWidth },
                ],
            },
        };

        if (cards.Length > 0)
        {
            var row = new Element[cards.Length];
            var rowLayout = WinUiReleaseNotesTiers.For(WidthTier.Compact, short_: false);   // the ROW card's numbers (its 14/20 title, its 8 gap)
            for (int i = 0; i < cards.Length; i++)
            {
                int idx = i;
                Element card = ov.CardsColumn
                    ? RowCard(cards[i], () => openCard(idx), props.OpenStoreListing, rowLayout, inRow: false)
                    : CardFor(cards[i], () => openCard(idx), props.OpenStoreListing, compact: true);
                row[i] = card with { Key = "dlg-hl:" + (cards[i].Highlight.Id is { Length: > 0 } id ? id : idx.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            }
            scrolled.Add(new BoxEl
            {
                Direction = ov.CardsColumn ? (byte)1 : (byte)0, Gap = ov.CardsColumn ? rowLayout.HighlightGap : M.CardGap,
                AlignItems = FlexAlign.Stretch, MinWidth = 0f,
                Padding = new Edges4(padX, M.RowPadTop, padX, M.RowPadBottom),
                Children = row,
            });
        }

        // Written IMMEDIATELY, not on close.
        Element dontShowBox = (ov.Target > 0f
            ? CheckBox.Create(Loc.Get(Strings.WhatsNew.Dialog.DontShow), dontShow, props.SetDontShowAgain, style: CheckBox.DefaultStyle with { MinHeight = ov.Target })
            : CheckBox.Create(Loc.Get(Strings.WhatsNew.Dialog.DontShow), dontShow, props.SetDontShowAgain)) with { Key = "rn:plate:check" };
        // close THEN navigate — the reverse of the viewer's "Try it": this plate is the thing being replaced.
        BoxEl full = Btn(Loc.Get(Strings.WhatsNew.Dialog.Full), showFull, ButtonAppearance.Standard, ControlSize.Medium, ov.Target) with { Key = "rn:plate:full" };
        BoxEl gotIt = Btn(Loc.Get(Strings.WhatsNew.Dialog.GotIt), props.Close, ButtonAppearance.Accent, ControlSize.Medium, ov.Target) with { Key = "rn:plate:ok" };

        Element footer = ov.FooterStacked
            ? new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = 0f, Shrink = 0f,
                Padding = new Edges4(padX, K.PlateFooterPadY, padX, K.PlateFooterPadY),
                Fill = Tok.FillLayerAlt, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
                Children =
                [
                    dontShowBox,
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                        Children =
                        [
                            full with { Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f },
                            gotIt with { Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f },
                        ],
                    },
                ],
            }
            : new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f,
                Padding = new Edges4(padX, K.PlateFooterPadY, padX, K.PlateFooterPadY),
                Fill = Tok.FillLayerAlt, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
                Children = [dontShowBox, Spacer(), full, gotIt],
            };

        // A scrolling body sums its content up to the plate's cap (ContentSized, as the viewer's text does), then scrolls under the pinned footer.
        Element[] body = ov.ScrollBody
            ? [ScrollView(new BoxEl { Direction = 1, MinWidth = 0f, Children = scrolled.ToArray() }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ContentSized = true }, footer]
            : [.. scrolled, footer];

        return new BoxEl
        {
            Key = "rn:plate", Width = ov.PlateWidth(vp.Width), Direction = 1, MaxHeight = ov.PlateMaxHeight(vp.Height),
            Corners = CornerRadius4.All(Radii.Overlay), ClipToBounds = true, Fill = Tok.FillSolidBase,
            Children = body,
        };
    }
}
