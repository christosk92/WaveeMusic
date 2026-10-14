// ── Themes/Classic/Structure/Views/Screens/ClassicReleaseNotes.Plate.cs — Classic's `ReleaseNotes.AfterUpdatePlate` (themes plan 6.1, T11c) ─────────
//
// From `Screens\ReleaseNotes.UI.cs` §7: the plate. A raw Modal overlay, not ContentDialog (which clamps its card): 720 wide, Modal dismiss + chrome, the chrome's own
// smoke, no ClosingAction veto, so Escape closes it. The core's after-update chrome decides WHEN (the gates, the latch, the load: the plate opens only once the load
// landed, so every input here is final at mount) and opens the overlay; this template draws it. The checkbox is the only live state (the template's own hook cell).
// Geometry comes from `ClassicReleaseNotesRules.Card` — the same numbers the 620-DIP plate-cap test asserts.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.Classic.ClassicReleaseNotesRules;
using M = Wavee.Themes.Classic.ClassicReleaseNotesRules.Card;

namespace Wavee.Themes.Classic;

internal static partial class ClassicReleaseNotes
{
    /// <summary>The registered template of <see cref="ViewKeys.ReleaseNotesAfterUpdatePlate"/>.</summary>
    public static Element PlateTemplate(AfterUpdatePlateProps props, in ViewContext ctx)
    {
        var dontShow = ctx.Hooks.UseSignal(false);
        var cards = props.Cards;
        var openCard = props.OpenCard;
        var showFull = props.ShowFull;

        var body = new List<Element>(3)
        {
            new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Padding = new Edges4(M.PlatePadX, M.HeroPadTop, M.PlatePadX, M.HeroPadBottom),
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
            for (int i = 0; i < cards.Length; i++)
            {
                int idx = i;
                row[i] = CardFor(cards[i], () => openCard(idx), props.OpenStoreListing, compact: true)
                    with { Key = "dlg-hl:" + (cards[i].Highlight.Id is { Length: > 0 } id ? id : idx.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            }
            body.Add(new BoxEl
            {
                Direction = 0, Gap = M.CardGap, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
                Padding = new Edges4(M.PlatePadX, M.RowPadTop, M.PlatePadX, M.RowPadBottom),
                Children = row,
            });
        }

        body.Add(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = new Edges4(M.PlatePadX, K.PlateFooterPadY, M.PlatePadX, K.PlateFooterPadY),
            Fill = Tok.FillLayerAlt, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
            Children =
            [
                // Written IMMEDIATELY, not on close.
                CheckBox.Create(Loc.Get(Strings.WhatsNew.Dialog.DontShow), dontShow, props.SetDontShowAgain),
                Spacer(),
                // close THEN navigate — the reverse of the viewer's "Try it": this plate is the thing being replaced.
                Button.Standard(Loc.Get(Strings.WhatsNew.Dialog.Full), showFull),
                Button.Accent(Loc.Get(Strings.WhatsNew.Dialog.GotIt), props.Close),
            ],
        });

        return new BoxEl
        {
            Width = M.PlateWidth, Direction = 1, MaxHeight = M.PlateMaxHeight,
            Corners = CornerRadius4.All(Radii.Overlay), ClipToBounds = true, Fill = Tok.FillSolidBase,
            Children = body.ToArray(),
        };
    }
}
