// ── Themes/Zune/Structure/Views/Dialogs/ZuneCreditsSheet.cs — Zune's `Dialog.Sheet` (themes plan 6.3, P5 Z8a) ─────────────────────────────────────────
//
// The "view credits" sheet's BODY (no board draws it, owner Q18): bars while the credits are unanswered (never an empty sheet that fills in), the server's group headings in its
// own casing (never re-cased) over one row per contributor in a bounded scroll, or the empty line. A credit linked to an artist slot is an accent link that opens the artist; an
// unlinked contributor is plain text. The sheet's shell (a one-button engine ContentDialog on the control table's square plate: the title is the track's, Close is the one way
// out) and the demand, the grouping and the order are the core's.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's credits sheet body.</summary>
internal static class ZuneCreditsSheet
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogSheet"/>.</summary>
    public static Element Template(CreditsSheetProps props, in ViewContext ctx)
    {
        // A short window bounds the list (the card scrolls the body, never the command out of the window).
        float listMax = ZuneOverlayTiers.Of(ctx.Tier()).Core.Dialog.BodyMaxHeight(
            ZuneOverlayRules.CreditsListMaxHeight, ctx.Hooks.UseContextSignal(Viewport.Size).Value.Height);
        Element body = props.State switch
        {
            Track.DrawerRules.CreditsView.Loading => new BoxEl
            {
                Key = "zcredits:loading", Direction = 1, Gap = ZuneOverlayRules.CreditsLoadingGap, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
                Children =
                [
                    LoadingBar(ZuneOverlayRules.CreditsBar1H, ZuneOverlayRules.CreditsBar1W),
                    LoadingBar(ZuneOverlayRules.CreditsBar2H, ZuneOverlayRules.CreditsBar2W),
                    LoadingBar(ZuneOverlayRules.CreditsBar2H, ZuneOverlayRules.CreditsBar3W),
                    LoadingBar(ZuneOverlayRules.CreditsBar2H, ZuneOverlayRules.CreditsBar4W),
                ],
            },
            Track.DrawerRules.CreditsView.List => CreditsList(props, listMax),
            _ => ZuneText.Prose(Loc.Get(Strings.Menu.NoCredits)),
        };
        return new BoxEl { Key = "zcredits", Direction = 1, MinWidth = 0f, MaxWidth = ZuneOverlayRules.CreditsMaxWidth, Children = [body] };
    }

    // The server's group headings in its own casing (never re-cased), one row per contributor, in a bounded scroll.
    static Element CreditsList(CreditsSheetProps props, float listMax)
    {
        var kids = new List<Element>(props.Entries.Count + 4);
        for (int i = 0; i < props.Entries.Count; i++)
        {
            var e = props.Entries[i];
            if (e.Heading is { } heading)
                kids.Add(R.Type.Eyebrow(heading) with { Color = Tok.TextTertiary });
            kids.Add(CreditRow(e.Name, e.Role, e.ArtistSlot, props.OpenArtist));
        }
        return ScrollView(new BoxEl { Direction = 1, Gap = ZuneOverlayRules.CreditsRowGap, MinWidth = 0f, Children = kids.ToArray() })
            with { MaxHeight = listMax };
    }

    // A credit linked to an artist slot is an accent link; an unlinked contributor is plain text.
    static Element CreditRow(string name, string role, int artistSlot, Action<int> openArtist)
    {
        var credit = R.Type.TrackTitle("");
        Element label = artistSlot > 0
            ? new SpanTextEl(new TextSpan[] { new(name, OnClick: () => openArtist(artistSlot)) })
            {
                Size = credit.Size, LineHeight = credit.LineHeight, Weight = credit.Weight,
                Color = Tok.AccentTextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            }
            : R.Type.TrackTitle(name) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
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
        Height = h, Width = w, Corners = CornerRadius4.All(ZuneOverlayRules.SkeletonBarRadius), Fill = Tok.FillSubtleSecondary,
    };
}
