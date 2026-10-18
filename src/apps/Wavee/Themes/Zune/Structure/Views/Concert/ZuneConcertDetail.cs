// ── Themes/Zune/Structure/Views/Concert/ZuneConcertDetail.cs — Zune's `Pages.Concert` (themes plan 6.3; P5 Z6b2) ──────────────────────────
//
// Boards Z-Concert (Standard), -Compact, -Small and -Huge. One event, flat on the ground:
//
//   · the CAPTION ("concert · on sale"), the TITLE (the hero voice, 64/72 light; a name, never re-cased), the DATE as a big light NUMERAL over its
//     lowercase month and weekday ("14" over "november · saturday"), and the FACTS as sentences ("20:00 · doors open 19:00", "Paradiso · Amsterdam",
//     "ages 18+") — the provider's local clock, the venue a name as written;
//   · the TICKETS SLAB (the raised plate, 320 wide beside the facts, stacked under them at Compact): one row per offer — provider, price, and a
//     round accent ring that buys (a validated external link) or the quiet "tickets not available online" — never a dead button; the footnote says
//     the prices are the providers';
//   · the LINEUP (the artists as round faces and names; rows with a chevron at Compact and Small; only a canonical artist navigates, a
//     billing-only act is inert) and the RELATED concerts as date cards (a grid on the raised plate, rows below Standard).
//
// ROW 2. The band's row 2 is the page's views: details · lineup · related concerts (each only when the page holds it). The page publishes them
// through the `PageChromeSink` from a tiny component mounted beside the scroll (`ChromeHost`): the spy's answer is current, a click scrolls its
// section. `Concert.DetailHost` (core) demands the whole row, publishes the verdict and reads the row into the page's values
// (`ConcertDetailView`); the skeleton is the same builder over `ConcertDetailView.Seed` (facts + two offers + three lineup rows: the exact silhouette
// of the loaded page, a seed date standing in for the numeral).
//
// GAPS WITH NO VERB IN THE PROPS (the board's ring row): interested, share, play the lineup, add to calendar. The page carries neither a uri nor a
// verb for them; they are listed in the lane report and drawn nowhere (never a dead ring).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Cells = Wavee.Themes.Zune.ZuneConcertCells;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using Rules = Wavee.Themes.Zune.ZuneConcertRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's concert page.</summary>
internal static class ZuneConcertDetail
{
    static readonly Action s_goHub = static () => ConcertVerbs.OpenHub();

    static readonly Func<bool> s_never = static () => false;

    static readonly Func<Element> s_empty = static () => new BoxEl();

    /// <summary>The view's one hook cell: the viewport's scroll handle, the section anchors (details · lineup · related) and the spy's answer, with
    /// the stable callbacks (a fresh delegate would re-arm what holds it).</summary>
    internal sealed class Cell
    {
        public const int Sections = 3;
        public readonly ScrollHandle Scroll = new();
        public readonly NodeHandle[] Anchors = new NodeHandle[Sections];
        public readonly Action<NodeHandle>[] Realized = new Action<NodeHandle>[Sections];
        public readonly Signal<int> Active = new(0);
        public NodeHandle Viewport;
        public readonly Action<NodeHandle> CaptureViewport;

        public Cell()
        {
            for (int i = 0; i < Sections; i++)
            {
                int index = i;
                Realized[i] = h => Anchors[index] = h;
            }
            CaptureViewport = h => Viewport = h;
        }
    }

    /// <summary>The registered template of <see cref="ViewKeys.ConcertDetail"/>.</summary>
    public static Element Template(ConcertDetailProps props, in ViewContext ctx)
    {
        var cell = ctx.Hooks.UseRef(new Cell()).Value;
        var facts = ctx.Tier();
        var lay = ZuneConcertTiers.Of(in facts);
        var rings = ZuneShow.RingLayoutOf(in facts);
        float gutter = ZuneTiers.FrameOf(in facts).Gutter;
        Func<Element> content = () => props.Unavailable
            ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.Detail.NotAvailable),
                subtitle: Loc.Get(Strings.Concerts.Detail.NotAvailableSubtitle))
            : Compose(props.Detail(), lay, rings, gutter, cell);
        Element region = Cells.Region(props.Pending, s_never, content, () => Compose(ConcertDetailView.Seed, lay, rings, gutter, cell),
            s_empty, "concert-detail:" + props.RouteKey);
        Element scroll = Cells.SubPageScroll(props.ScrollScope + "concert-detail:" + props.RouteKey, region, gutter) with
        {
            Handle = cell.Scroll, OnRealized = cell.CaptureViewport,
        };
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ZStack = true,
            Children = [scroll, Embed.Comp(new ChromeProps(props, cell), static () => new ChromeHost()) with { Key = "concert-chrome:" + props.RouteKey }],
        };
    }

    // ══ THE PAGE ══════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Compose(ConcertDetailView d, ZuneConcertLayout lay, ZuneDetailLayout rings, float gutter, Cell cell)
    {
        bool seed = ReferenceEquals(d, ConcertDetailView.Seed);
        var blocks = new List<Element>(3) { Anchor(0, "details", Hero(d, lay, rings, gutter, seed), cell) };
        if (d.Lineup.Length > 0) blocks.Add(Anchor(1, "lineup", Lineup(d.Lineup, lay), cell));
        if (d.Related.Length > 0) blocks.Add(Anchor(2, "related", Related(d.Related, lay), cell));
        return new BoxEl { Key = "zune-concert:column", Direction = 1, Gap = lay.SectionGap, MinWidth = 0f, Children = blocks.ToArray() };
    }

    /// <summary>A section under its anchor: the outer box IS the anchor, so its top is exactly what a row-2 click parks at and what the spy reads.</summary>
    static Element Anchor(int index, string key, Element body, Cell cell) => new BoxEl
    {
        Key = "zune-concert:" + key, Direction = 1, MinWidth = 0f, OnRealized = cell.Realized[index], Children = [body],
    };

    /// <summary>The hero: the identity (caption, title, date, facts) and the tickets slab — beside each other where the tier has the room, the slab
    /// stacked under the facts otherwise.</summary>
    static Element Hero(ConcertDetailView d, ZuneConcertLayout lay, ZuneDetailLayout rings, float gutter, bool seed)
    {
        Element identity = Identity(d, lay, seed);
        if (d.Offers.Length == 0) return identity;
        Element slab = Tickets(d.Offers, lay, rings, gutter);
        // ONE tree at every tier: only the hero's direction and the two wrappers' sizing change across the Compact/Small edge, so the identity (and the
        // date inside it) is the SAME node after a crossing and glides instead of being remounted under a different parent.
        bool beside = lay.TicketsW > 0f;
        return new BoxEl
        {
            Key = "zune-concert:hero", Direction = beside ? (byte)0 : (byte)1, Gap = beside ? lay.HeroGap : lay.SlabTop, MinWidth = 0f,
            AlignItems = beside ? FlexAlign.Start : FlexAlign.Stretch,
            Children =
            [
                new BoxEl
                {
                    Key = "zune-concert:identity", Direction = 1, Grow = beside ? 1f : 0f, Basis = beside ? 0f : float.NaN, MinWidth = 0f,
                    Children = [identity],
                },
                new BoxEl
                {
                    Key = "zune-concert:slab", Direction = 1, Width = beside ? lay.TicketsW : float.NaN, Shrink = beside ? 0f : 1f,
                    Children = [slab],
                },
            ],
        };
    }

    // ══ THE IDENTITY: CAPTION, TITLE, DATE, FACTS ══════════════════════════════════════════════════════════════════════

    static Element Identity(ConcertDetailView d, ZuneConcertLayout lay, bool seed)
    {
        var culture = CultureInfo.CurrentCulture;
        DateTimeOffset? date = d.Date ?? (seed ? Rules.SeedDate : null);

        // the caption: "concert · on sale" (the status is the provider's word for what is notable)
        var caption = new List<Element>(3)
        {
            R.Type.DenseMeta(Loc.Get(Strings.Concerts.Detail.Concert)) with
            {
                Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
            },
        };
        if (d.Status is { Length: > 0 } status)
        {
            caption.Add(R.Type.DenseMeta("·") with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f });
            caption.Add(R.Type.DenseMeta(status) with
            {
                Color = Prop.Of(static () => R.Ink.AccentAction), Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        }

        // The title keeps its tier's step and wraps over up to TitleLines lines (the identity column grows to hug it), never a one-line ellipsis or a bleed.
        var title = R.Type.Hero(d.Title) with
        {
            Size = lay.TitleSize, LineHeight = lay.TitleLine, Color = Tok.TextPrimary, MinWidth = 0f,
            MaxLines = Rules.TitleLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, Shrink = 1f,
        };

        var kids = new List<Element>(4)
        {
            new BoxEl { Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = caption.ToArray() },
            title,
        };

        if (date is { } moment)
            kids.Add(new BoxEl
            {
                Key = "zune-concert:date", Direction = lay.DateStacked ? (byte)0 : (byte)1, Gap = lay.DateGap, MinWidth = 0f,
                AlignItems = FlexAlign.Start, Margin = new Edges4(0f, lay.DateTop, 0f, 0f),
                Children = [DateBlock(moment, lay, culture), Facts(d, lay, moment, culture)],
            });
        else
            kids.Add(new BoxEl
            {
                Key = "zune-concert:date", Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, lay.DateTop, 0f, 0f),
                Children = [Facts(d, lay, null, culture)],
            });
        return new BoxEl { Direction = 1, Gap = lay.IdentityGap, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>The date as a block: the big light numeral (64, 150 at Huge) and, with it, "november · saturday" (the label voice, lowercased). Stacked
    /// at the wider tiers; the numeral then the month over "saturday · 2026" at Compact.</summary>
    static Element DateBlock(DateTimeOffset date, ZuneConcertLayout lay, CultureInfo culture)
    {
        var numeral = R.Type.Panorama(date.Day.ToString(culture)) with
        {
            Size = lay.NumeralSize, LineHeight = lay.NumeralLine, LineStacking = LineStacking.BlockLineHeight, Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
        };
        string month = date.ToString("MMMM", culture), weekday = date.ToString("dddd", culture);
        if (lay.DateStacked)
            return new BoxEl
            {
                Direction = 1, Gap = Rules.NumeralGap, Shrink = 0f, MinWidth = 0f,
                Children =
                [
                    numeral,
                    R.Type.DenseTitle(month + " · " + weekday) with
                    {
                        Size = lay.DateWordsSize, LineHeight = lay.DateWordsLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label),
                        MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        return new BoxEl
        {
            Direction = 0, Gap = Rules.NumeralBesideGap, AlignItems = FlexAlign.End, Shrink = 0f, MinWidth = 0f,
            Children =
            [
                numeral,
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Rules.NumeralBesideLift),
                    Children =
                    [
                        R.Type.DenseTitle(month) with
                        {
                            Size = lay.DateWordsSize, LineHeight = lay.DateWordsLine, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label),
                            MaxLines = 1, Wrap = TextWrap.NoWrap,
                        },
                        R.Type.TrackMeta(weekday + " · " + date.Year.ToString(culture)) with
                        {
                            Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The facts as sentences, each omitted when absent, in the PROVIDER's clock: "20:00 · doors open 19:00", the venue (a name, as written)
    /// then its place, "ages 18+". A page whose row carries no date reads the long date line instead.</summary>
    static Element Facts(ConcertDetailView d, ZuneConcertLayout lay, DateTimeOffset? date, CultureInfo culture)
    {
        var rows = new List<Element>(4);
        if (date is { } moment)
        {
            var time = FactWord(moment.ToString("t", culture), lay, primary: true, lowered: false);
            rows.Add(d.Doors is { Length: > 0 } doors
                ? FactRow([time, Dot(), FactWord(doors, lay, primary: false, lowered: true)])
                : time);
        }
        else rows.Add(FactWord(d.DateLine, lay, primary: true, lowered: false));
        if (date is null && d.Doors is { Length: > 0 } onlyDoors) rows.Add(FactWord(onlyDoors, lay, primary: false, lowered: true));
        if (d.Place.Length > 0)
        {
            int dot = d.Place.IndexOf(" · ", StringComparison.Ordinal);
            rows.Add(dot > 0
                ? FactRow([FactWord(d.Place[..dot], lay, primary: true, lowered: false), Dot(), FactWord(d.Place[(dot + 3)..], lay, primary: false, lowered: false)])
                : FactWord(d.Place, lay, primary: true, lowered: false));
        }
        if (d.Ages is { Length: > 0 } ages)
            rows.Add(R.Type.TrackMeta(ages) with { Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f });
        return new BoxEl
        {
            Direction = 1, Gap = lay.FactsGap, Grow = lay.DateStacked ? 1f : 0f, Basis = lay.DateStacked ? 0f : float.NaN, MinWidth = 0f,
            Padding = new Edges4(0f, lay.DateStacked ? Rules.FactsLift : 0f, 0f, 0f), Children = rows.ToArray(),
        };
    }

    static Element FactRow(Element[] words) => new BoxEl { Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = words };

    static Element Dot() => R.Type.DenseMeta("·") with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f };

    /// <summary>One fact word at the tier's sentence size: the primary ink for the time and the venue, the secondary for what qualifies them. Words
    /// the theme writes (doors open …) are lowered; a venue and a place are names.</summary>
    static TextEl FactWord(string text, ZuneConcertLayout lay, bool primary, bool lowered) => R.Type.TrackTitle(text) with
    {
        Size = lay.FactsSize, LineHeight = lay.FactsLine, Color = primary ? Tok.TextPrimary : Tok.TextSecondary,
        Case = lowered ? R.Case.For(LabelKind.Label) : TextCase.AsWritten,
        MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    // ══ THE TICKETS SLAB ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"tickets" over one row per offer — provider, price, availability and the sale window as quiet lines, and the round accent ring that
    /// buys (a validated external link) or the quiet "tickets not available online" — never a dead button. The slab is the raised plate; at Compact it
    /// bleeds to the gutters.</summary>
    static Element Tickets(ConcertOfferView[] offers, ZuneConcertLayout lay, ZuneDetailLayout rings, float gutter)
    {
        var kids = new List<Element>(offers.Length + 2) { K.Heading(Loc.Get(Strings.Concerts.Detail.Tickets)) with { Size = lay.SlabHeadSize, LineHeight = lay.SlabHeadLine } };
        for (int i = 0; i < offers.Length; i++)
        {
            var o = offers[i];
            var words = new List<Element>(3)
            {
                R.Type.TrackTitle(o.Provider) with
                {
                    Size = lay.OfferSize, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                },
            };
            string quiet = o.Availability is { Length: > 0 } a ? (o.Sale is { Length: > 0 } s ? a + " · " + s : a) : o.Sale ?? "";
            if (quiet.Length > 0)
                words.Add(R.Type.TrackMeta(quiet) with { Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
            var row = new List<Element>(3) { new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Children = words.ToArray() } };
            if (o.Price is { Length: > 0 } price)
                row.Add(R.Type.TrackMeta(price) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f });
            row.Add(o.Url is { } url
                ? K.Ring("buy:" + i.ToString(CultureInfo.InvariantCulture), Icons.ChevronRight, Loc.Get(Strings.Concerts.Detail.BuyTickets), false,
                    Rules.AccentRing, () => Actions.Services.OpenExternal?.Invoke(url), rings)
                : R.Type.TrackMeta(Loc.Get(Strings.Concerts.Detail.TicketsUnavailable)) with
                {
                    Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
                });
            kids.Add(new BoxEl
            {
                Key = "offer:" + i.ToString(CultureInfo.InvariantCulture) + ":" + o.Provider,
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = lay.OfferRowH,
                Padding = new Edges4(0f, Rules.OfferPadY, 0f, Rules.OfferPadY), BorderWidth = 0f, Children = row.ToArray(),
            });
            if (i < offers.Length - 1) kids.Add(new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeDividerDefault });
        }
        kids.Add(R.Type.MicroMeta(Loc.Get(Strings.Theme.Zune.Detail.TicketsNote)) with
        {
            Size = Rules.NoteSize, LineHeight = Rules.NoteLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label),
            Wrap = TextWrap.Wrap, MinWidth = 0f, Margin = new Edges4(0f, Rules.NoteTop, 0f, 0f),
        });
        float bleed = lay.SlabBleed ? gutter : 0f;
        return new BoxEl
        {
            Key = "zune-concert:tickets", Direction = 1, MinWidth = 0f, Gap = Rules.SlabGap,
            Fill = Prop.Of(static () => R.Layer.EventPlate), Corners = R.Shape.All(ShapeRole.Card),
            Padding = new Edges4(lay.SlabPadX + bleed, lay.SlabPadY, lay.SlabPadX + bleed, lay.SlabPadY + Rules.SlabPadBottomExtra),
            Margin = new Edges4(-bleed, 0f, -bleed, 0f), Children = kids.ToArray(),
        };
    }

    // ══ THE LINEUP ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"lineup" + "2 artists", then the artists: round faces with names in a wrapping line, or rows with a chevron below Standard. Only a
    /// canonical artist navigates; a billing-only act is inert — no wash, no chevron, arrow cursor, not focusable.</summary>
    static Element Lineup(ConcertActView[] acts, ZuneConcertLayout lay)
    {
        var rows = new Element[acts.Length];
        for (int i = 0; i < rows.Length; i++) rows[i] = Act(acts[i], i, lay);
        return new BoxEl
        {
            Direction = 1, Gap = Rules.SectionHeadGap, MinWidth = 0f,
            Children =
            [
                Cells.SectionHead(Loc.Get(Strings.Concerts.Detail.Lineup), Strings.Concerts.Detail.ArtistCount(acts.Length)),
                lay.LineupRows
                    ? new BoxEl { Direction = 1, MinWidth = 0f, Children = rows }
                    : new BoxEl { Direction = 0, Wrap = true, Gap = Rules.LineupGap, MinWidth = 0f, Children = rows },
            ],
        };
    }

    static Element Act(ConcertActView act, int index, ZuneConcertLayout lay)
    {
        bool live = act.Navigable;
        int slot = act.ArtistSlot;
        float face = lay.LineupAvatar;
        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Width = face, Height = face, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Avatar), ClipToBounds = true,
                Children = [Controls.Artwork(act.Image, face, face, face / 2f)],
            },
            R.Type.TrackTitle(act.Name) with { Grow = lay.LineupRows ? 1f : 0f, Basis = lay.LineupRows ? 0f : float.NaN, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis },
        };
        if (live && lay.LineupRows) kids.Add(Icon(Icons.ChevronRight, Rules.ActChevron, Tok.TextSecondary) with { Shrink = 0f });
        return new BoxEl
        {
            Key = live ? "act:" + slot.ToString(CultureInfo.InvariantCulture) : "lineup:" + index.ToString(CultureInfo.InvariantCulture) + ":" + act.Name,
            Direction = 0, MinHeight = lay.LineupRows ? lay.LineupRowH : face, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            HoverFill = live ? Prop.Of(static () => R.Layer.RowHover) : Prop.Of(static () => ColorF.Transparent),
            PressedFill = live ? Prop.Of(static () => R.Layer.RowPressed) : Prop.Of(static () => ColorF.Transparent),
            Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live, AllowFocusOnInteraction = false,
            Cursor = live ? CursorId.Hand : CursorId.Arrow,
            OnClick = live ? () => Track.GoToArtist(new Artist(slot)) : null,
            Children = kids.ToArray(),
        };
    }

    // ══ THE RELATED CONCERTS ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"related concerts" with the link word to the hub, then the date cards: a grid on the raised plate (Z-Concert: four across), rows
    /// below Standard. The app has no tour-dates feed, so the grid carries the related concerts.</summary>
    static Element Related(ConcertShelfItem[] related, ZuneConcertLayout lay)
    {
        var tracks = new TrackSize[Math.Max(1, lay.RelatedColumns)];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = TrackSize.Star();
        var cards = new Element[related.Length];
        for (int i = 0; i < cards.Length; i++) cards[i] = DateCard(related[i].Slot, lay);
        return new BoxEl
        {
            Direction = 1, Gap = Rules.SectionHeadGap, MinWidth = 0f,
            Children =
            [
                Cells.SectionHead(Loc.Get(Strings.Concerts.Detail.RelatedConcerts), null,
                    K.Link("related:near", Loc.Get(Strings.Concerts.NearYou), s_goHub, lay.LinkTarget)),
                new GridEl
                {
                    Columns = tracks, ColGap = lay.RelatedGap, RowGap = lay.RelatedGap, RowHeight = lay.RelatedRowH, Children = cards,
                },
            ],
        };
    }

    /// <summary>One date card (Z-Concert's related): the light numeral over its lowercase month, the title and the venue beside it, on the raised
    /// plate where the tier writes one; a click opens the concert.</summary>
    static Element DateCard(int slot, ZuneConcertLayout lay)
    {
        var c = new Concert(slot);
        var tile = Concert.TileOf(c);
        var date = c.LocalDate;
        return new BoxEl
        {
            Key = "date:" + slot.ToString(CultureInfo.InvariantCulture),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f, Height = lay.RelatedRowH,
            Padding = lay.RelatedPlate ? new Edges4(Rules.CardPadX, 0f, Rules.CardPadX, 0f) : new Edges4(0f, 0f, 0f, 0f),
            Fill = lay.RelatedPlate ? Prop.Of(static () => R.Layer.EventPlate) : ColorF.Transparent,
            HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = () => ConcertVerbs.OpenConcert(slot),
            Children =
            [
                Cells.DateColumn(date, lay.RelatedDateW, lay.RelatedDaySize, lay.RelatedDayLine),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Rules.TileTextGap, Justify = FlexJustify.Center,
                    Children =
                    [
                        R.Type.TrackTitle(tile.Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        R.Type.TrackMeta(tile.Place) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
            ],
        };
    }

    // ══ ROW 2: THE PAGE'S VIEWS ═══════════════════════════════════════════════════════════════════════════════════════

    internal sealed record ChromeProps(ConcertDetailProps Props, Cell Cell);

    /// <summary>A zero-size component beside the scroll: it publishes the page's views to the band's row 2 (details · lineup · related concerts, each
    /// when the page holds it), follows the scroll with the spy, and scrolls a section when its word is clicked. Gated on what the row is built from.</summary>
    sealed class ChromeHost : Component
    {
        ChromeProps? _p;
        PageChromeSink _sink = PageChromeSink.None;
        bool _shown, _lineup, _related;
        string? _title;   // the concert's name, row 2's title (contract 1.30); null while the row is pending
        long _sig = long.MinValue;
        readonly Action _publish, _spy;
        readonly Action[] _go = new Action[Cell.Sections];

        public ChromeHost()
        {
            _publish = Publish;
            _spy = Spy;
            for (int i = 0; i < _go.Length; i++)
            {
                int section = i;
                _go[i] = () => GoTo(section);
            }
        }

        public override Element Render()
        {
            _p = UseProps<ChromeProps>();
            _sink = UseContext(PageChromeSink.Context);
            var props = _p.Props;
            // "details" is always there once the event exists (so the band's row 2 stands at its height from the first frame); the lineup and the related
            // concerts join it when the row has answered.
            _shown = !props.Unavailable;
            bool answered = _shown && !props.Pending();
            var d = answered ? props.Detail() : null;
            _lineup = d is { Lineup.Length: > 0 };
            _related = d is { Related.Length: > 0 };
            _title = d is { Title.Length: > 0 } ? d.Title : null;
            UseEffect(_spy);        // auto-tracks the viewport's scroll offset
            UseEffect(_publish);    // auto-tracks the spy's answer
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        /// <summary>The sections the page holds, in draw order (the details always).</summary>
        int Present(Span<int> sections)
        {
            int n = 0;
            sections[n++] = 0;
            if (_lineup) sections[n++] = 1;
            if (_related) sections[n++] = 2;
            return n;
        }

        /// <summary>THE SPY: <see cref="Detail.ScrollSpy.ActiveSectionOf"/> over each held section's viewport-relative top.</summary>
        void Spy()
        {
            var cell = _p!.Cell;
            _ = cell.Scroll.Offset.Value;
            var scene = Context.Scene;
            if (scene is null || !_shown || cell.Viewport.IsNull || !scene.IsLive(cell.Viewport)) return;
            RectF vp = scene.AbsoluteRect(cell.Viewport);
            Span<int> sections = stackalloc int[Cell.Sections];
            int n = Present(sections);
            Span<float> tops = stackalloc float[Cell.Sections];
            for (int i = 0; i < n; i++)
            {
                var node = cell.Anchors[sections[i]];
                tops[i] = node.IsNull || !scene.IsLive(node) ? float.NaN : scene.AbsoluteRect(node).Y - vp.Y;
            }
            int at = Detail.ScrollSpy.ActiveSectionOf(tops[..n], 0f, vp.H);
            if (at >= 0) cell.Active.SetIfChanged(sections[at]);
        }

        /// <summary>Scrolls a section to the viewport's top (a row-2 click).</summary>
        void GoTo(int section)
        {
            var cell = _p!.Cell;
            var scene = Context.Scene;
            var node = cell.Anchors[section];
            if (scene is null || node.IsNull || cell.Viewport.IsNull || !scene.IsLive(node) || !scene.IsLive(cell.Viewport)) return;
            scene.BringIntoView(cell.Viewport, node, align: 0f, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide, margin: 0f);
        }

        /// <summary>Publishes row 2 (an auto-tracked effect: it reads the spy's answer, so the current word follows the scroll).</summary>
        void Publish()
        {
            var cell = _p!.Cell;
            int active = cell.Active.Value;
            if (!_shown) return;
            long sig = HashCode.Combine(_lineup, _related, active, _title);
            if (sig == _sig) return;
            _sig = sig;
            var views = new List<PageView>(3)
            {
                new("details", Loc.Get(Strings.Theme.Zune.Detail.Details), active == 0, _go[0]),
            };
            if (_lineup) views.Add(new PageView("lineup", Loc.Get(Strings.Concerts.Detail.Lineup), active == 1, _go[1]));
            if (_related) views.Add(new PageView("related", Loc.Get(Strings.Concerts.Detail.RelatedConcerts), active == 2, _go[2]));
            _sink.Publish(new PageChromeRow(views, [], Title: _title));
        }
    }
}
