// ── Themes/Classic/Structure/Views/Dialogs/ClassicCreditsSheet.cs — Classic's `Dialog.Sheet` (themes plan 6.1, T11a) ────────────────
//
// The "View credits" sheet's BODY (ch 01 W27, item 74), moved from `Track.Drawer.cs` (T8c left it to T11): bars while the credits are
// unanswered (never an empty sheet that fills in), the server's group headings in its own casing (never re-cased) over one row per
// contributor in a bounded scroll, or the empty line. A credit linked to an artist slot is an accent link that opens the artist; an unlinked
// contributor is plain text. The sheet's shell (a one-button engine ContentDialog: the title is the track's, Close is the one way out) and
// the demand, the grouping and the order are the core's (`Track.OpenCredits`, `Track.DrawerRules`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's credits sheet body.</summary>
internal static class ClassicCreditsSheet
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogSheet"/>.</summary>
    public static Element Template(CreditsSheetProps props, in ViewContext ctx)
    {
        Element body = props.State switch
        {
            Track.DrawerRules.CreditsView.Loading => new BoxEl
            {
                Direction = 1, Gap = ClassicDialogRules.CreditsLoadingGap, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
                Children =
                [
                    LoadingBar(ClassicDialogRules.CreditsBar1H, ClassicDialogRules.CreditsBar1W),
                    LoadingBar(ClassicDialogRules.CreditsBar2H, ClassicDialogRules.CreditsBar2W),
                    LoadingBar(ClassicDialogRules.CreditsBar2H, ClassicDialogRules.CreditsBar3W),
                    LoadingBar(ClassicDialogRules.CreditsBar2H, ClassicDialogRules.CreditsBar4W),
                ],
            },
            Track.DrawerRules.CreditsView.List => CreditsList(props),
            _ => R.Type.DenseMeta(Loc.Get(Strings.Menu.NoCredits)) with { Color = Tok.TextSecondary },
        };
        return new BoxEl { Direction = 1, MinWidth = 0f, MaxWidth = ClassicDialogRules.CreditsMaxWidth, Children = [body] };
    }

    // The server's group headings in its own casing (never re-cased), one row per contributor, in a bounded scroll.
    static Element CreditsList(CreditsSheetProps props)
    {
        var kids = new List<Element>(props.Entries.Count + 4);
        for (int i = 0; i < props.Entries.Count; i++)
        {
            var e = props.Entries[i];
            if (e.Heading is { } heading)
                kids.Add(R.Type.Eyebrow(heading) with { Color = Tok.TextTertiary });
            kids.Add(CreditRow(e.Name, e.Role, e.ArtistSlot, props.OpenArtist));
        }
        return ScrollView(new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = kids.ToArray() })
            with { MaxHeight = ClassicDialogRules.CreditsListMaxHeight };
    }

    // A credit linked to an artist slot is an accent link; an unlinked contributor is plain text.
    static Element CreditRow(string name, string role, int artistSlot, Action<int> openArtist)
    {
        var creditTitle = R.Type.DenseTitle("");
        Element label = artistSlot > 0
            ? new SpanTextEl(new TextSpan[] { new(name, OnClick: () => openArtist(artistSlot)) })
            {
                Size = creditTitle.Size, LineHeight = creditTitle.LineHeight, Weight = ClassicDialogRules.CreditsNameWeight,
                Color = Tok.AccentTextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            }
            : R.Type.DenseTitle(name) with
            {
                Weight = ClassicDialogRules.CreditsNameWeight, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Children =
            [
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [label] },
                role.Length == 0 ? new BoxEl() : R.Type.MicroMeta(role) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    static Element LoadingBar(float h, float w) => new BoxEl
    {
        Height = h, Width = w, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary,
    };
}
