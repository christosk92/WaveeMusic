// ── Themes/Classic/Structure/Views/Pages/ClassicConcertDetail.cs — Classic's `Pages.Concert` (themes plan 6.1, T10b3) ────────────────────────────
//
// From `Entities\Concert.Page.cs` §8 (`DetailPage`; 0.2.9 `ConcertDetailPage`): the split hero carries the identity and the provider's local-clock facts; tickets
// ride a 320 rail at wide (920 enter / 860 leave, `ClassicConcertTiers.DetailWide`) ONLY when offers exist — else one full column — and move inline after the
// hero when narrow; then the lineup card and the related shelf, each omitted when empty. `Concert.DetailHost` (core) demands the whole row, publishes the verdict
// and reads the row into the page's values (`ConcertDetailView`); the skeleton is the same builder over `ConcertDetailView.Seed` (§9 #4: facts + two offers +
// three lineup rows, the exact silhouette of the loaded page).
//
// TICKETS (§0 #13): one card per offer — provider, availability, price, sale window, then Buy (a validated external link, lifted 4 DIP) or the quiet "Tickets
// not available online": never a dead button. THE LINEUP: only a canonical artist navigates; a billing-only act is inert — no fill, no chevron, arrow cursor,
// not focusable (parity 49).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Cells = Wavee.Themes.Classic.ClassicConcertCells;
using Rules = Wavee.Themes.Classic.ClassicConcertRules;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's concert page.</summary>
internal static class ClassicConcertDetail
{
    static readonly Action s_goHub = static () => ConcertVerbs.OpenHub();
    static readonly Func<Element> s_never = static () => new BoxEl();

    static readonly Func<ConcertShelfItem, int, string> s_itemKey = static (item, _) => item.Slot.ToString(CultureInfo.InvariantCulture);

    static readonly Func<ConcertShelfItem, int, float, Element> s_relatedCard = static (item, _, _) =>
    {
        if (item.Slot <= Table.None) return Cells.BrowseAllCard(s_goHub);
        int slot = item.Slot;
        return Cells.Tile(Concert.TileOf(new Concert(slot)), () => ConcertVerbs.OpenConcert(slot));
    };

    /// <summary>The view's one hook cell: the 920 / 860 hysteresis latch (the skeleton's seed build shares it, as the old page's fields did).</summary>
    sealed class Latch
    {
        public bool WasWide, WideInit;
    }

    /// <summary>The registered template of <see cref="ViewKeys.ConcertDetail"/>.</summary>
    public static Element Template(ConcertDetailProps props, in ViewContext ctx)
    {
        var latch = ctx.Hooks.UseRef(new Latch()).Value;
        Func<float, Element> build = w => Compose(props.Detail(), Wide(latch, w));
        Func<float, Element> buildSeed = w => Compose(ConcertDetailView.Seed, Wide(latch, w));
        Func<Element> content = () => props.Unavailable
            ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.Detail.NotAvailable),
                subtitle: Loc.Get(Strings.Concerts.Detail.NotAvailableSubtitle))
            : Responsive.Of("concert-detail", build, fallback: Rules.DetailFallbackWidth);
        Element region = Cells.Region(props.Pending, static () => false, content, () => Responsive.Of("concert-detail", buildSeed, fallback: Rules.DetailFallbackWidth),
            s_never, "concert-detail:" + props.RouteKey);
        return Cells.SubPageScroll(props.ScrollScope + "concert-detail:" + props.RouteKey, region, ClassicTiers.PageOf(ctx.Tier()).Gutter);
    }

    static bool Wide(Latch latch, float width)
    {
        bool wide = ClassicConcertTiers.DetailWide(width, latch.WasWide, latch.WideInit);
        latch.WasWide = wide;
        latch.WideInit = true;
        return wide;
    }

    static Element Compose(ConcertDetailView d, bool wide)
    {
        Element? tickets = d.Offers.Length > 0 ? Tickets(d.Offers) : null;
        var main = new List<Element>(4) { Cells.SplitHero(d.ImageUrl, d.Accent, Identity(d), wide) };
        if (!wide && tickets is not null) main.Add(tickets);                  // narrow: inline, right after the facts
        if (d.Lineup.Length > 0) main.Add(Lineup(d.Lineup));
        if (d.Related.Length > 0) main.Add(Related(d.Related));
        var column = new BoxEl { Direction = 1, Gap = Spacing.XL, MinWidth = 0f, Children = main.ToArray() };
        if (!wide || tickets is null) return column;                           // wide with NO offers: no rail (parity 64)
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Gap = Spacing.XL, MinWidth = 0f,
            Children = [column with { Grow = 1f, Basis = 0f }, new BoxEl { Width = Rules.TicketsRailW, Shrink = 0f, Direction = 1, Children = [tickets] }],
        };
    }

    /// <summary>The hero's copy: "Concert", the title (≤ 2 lines), the status pill when notable, then the facts — each
    /// with its 16-DIP glyph and omitted when absent — in the PROVIDER's clock.</summary>
    static Element Identity(ConcertDetailView d)
    {
        var headline = new List<Element>(3)
        {
            Cells.SectionCaption(Loc.Get(Strings.Concerts.Detail.Concert)),
            R.Type.PageHero(d.Title) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
        };
        if (d.Status is { } status)
            headline.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Start, Padding = new Edges4(Spacing.S, Rules.StatusPadY, Spacing.S, Rules.StatusPadY), Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary,
                Children = [Caption(status) with { Color = Tok.SystemFillCritical, Weight = 600, MaxLines = 1 }],
            });
        var facts = new List<Element>(4) { Fact(Icons.Calendar, d.DateLine) };
        if (d.Doors is { } doors) facts.Add(Fact(Icons.Clock, doors));
        if (d.Place.Length > 0) facts.Add(Fact(Icons.MapPin, d.Place));
        if (d.Ages is { } ages) facts.Add(Fact(Icons.Info, ages));
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 1, Gap = Rules.HeadlineGap, MinWidth = 0f, Children = headline.ToArray() },
                new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = facts.ToArray() },
            ],
        };
    }

    static Element Fact(string glyph, string text) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
        Children =
        [
            Icon(glyph, Rules.FactGlyph, Tok.TextSecondary) with { Shrink = 0f },
            Body(text) with { Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    /// <summary>"Tickets" + one card per offer: provider, availability, price, sale window, then Buy (a validated
    /// external link, lifted 4 DIP) or the quiet "Tickets not available online" — never a dead button (§0 #13).</summary>
    static Element Tickets(ConcertOfferView[] offers)
    {
        var kids = new Element[offers.Length + 1];
        kids[0] = Cells.SectionCaption(Loc.Get(Strings.Concerts.Detail.Tickets));
        for (int i = 0; i < offers.Length; i++)
        {
            var o = offers[i];
            var lines = new List<Element>(5)
            {
                R.Type.TrackTitle(o.Provider) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            };
            if (o.Availability is { } availability) lines.Add(R.Type.TrackMeta(availability) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            if (o.Price is { } price) lines.Add(Body(price) with { Color = Tok.TextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            if (o.Sale is { } sale) lines.Add(R.Type.TrackMeta(sale) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            if (o.Url is { } url)
                lines.Add(new BoxEl
                {
                    Direction = 0, Padding = new Edges4(0f, Spacing.XS, 0f, 0f),
                    Children = [Button.Accent(Loc.Get(Strings.Concerts.Detail.BuyTickets), () => Actions.Services.OpenExternal?.Invoke(url))],
                });
            else lines.Add(R.Type.TrackMeta(Loc.Get(Strings.Concerts.Detail.TicketsUnavailable)) with { MaxLines = 1 });
            kids[i + 1] = new BoxEl
            {
                Key = "offer:" + i.ToString(CultureInfo.InvariantCulture) + ":" + o.Provider,
                Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Padding = Edges4.All(Spacing.M), Corners = Radii.CardAll,
                Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = lines.ToArray(),
            };
        }
        return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = kids };
    }

    /// <summary>The lineup card: "Lineup" + "N artists", a divider, 60-DIP rows with 44 round avatars. Only a canonical
    /// artist navigates; a billing-only act is inert — no fill, no chevron, arrow cursor, not focusable (parity 49).</summary>
    static Element Lineup(ConcertActView[] acts)
    {
        var rows = new Element[acts.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var act = acts[i];
            bool live = act.Navigable;
            int slot = act.ArtistSlot;
            var kids = new Element[live ? 3 : 2];
            kids[0] = new BoxEl
            {
                Width = Rules.ActAvatar, Height = Rules.ActAvatar, Shrink = 0f, Corners = CornerRadius4.All(Rules.ActAvatar / 2f), ClipToBounds = true,
                Children = [Controls.Artwork(act.Image, Rules.ActAvatar, Rules.ActAvatar, Rules.ActAvatar / 2f)],
            };
            kids[1] = R.Type.TrackTitle(act.Name) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
            if (live) kids[2] = Icon(Icons.ChevronRight, Rules.ActChevron, Tok.TextSecondary) with { Shrink = 0f };
            rows[i] = new BoxEl
            {
                Key = live ? "act:" + slot.ToString(CultureInfo.InvariantCulture) : "lineup:" + i.ToString(CultureInfo.InvariantCulture) + ":" + act.Name,
                Direction = 0, MinHeight = Rules.ActRowMinH, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
                HoverFill = live ? Tok.FillSubtleSecondary : ColorF.Transparent, PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
                Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live,
                Cursor = live ? CursorId.Hand : CursorId.Arrow,
                OnClick = live ? () => Track.GoToArtist(new Artist(slot)) : null,
                Children = kids,
            };
        }
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
                    Children =
                    [
                        new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [Cells.SectionCaption(Loc.Get(Strings.Concerts.Detail.Lineup))] },
                        R.Type.Eyebrow(Strings.Concerts.Detail.ArtistCount(acts.Length)) with { Color = Tok.TextSecondary, MaxLines = 1 },
                    ],
                },
                new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeSurfaceDefault },
                new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.S), Children = rows },
            ],
        };
    }

    /// <summary>"Related concerts": cell 0 is the Browse-all tile (to the hub), then ≤ 15 related tiles.</summary>
    static Element Related(ConcertShelfItem[] related)
    {
        var items = new ConcertShelfItem[related.Length + 1];
        Array.Copy(related, 0, items, 1, related.Length);
        return PagedShelf.Create(items, s_relatedCard, header: Cells.SectionCaption(Loc.Get(Strings.Concerts.Detail.RelatedConcerts)),
            headerGap: Spacing.S, measured: true, keyOf: s_itemKey);
    }
}
