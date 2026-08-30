using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Browse;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Phase 5 concert-detail surface (docs/concert-navigation-hub-geolocation-plan.md). ONE page ScrollEl over a
/// seeded <see cref="Skel"/> region: event identity + facts (preserved local-offset times) → ticket offers → optional
/// lineup → optional related shelf. Sections with no data are omitted entirely. Ticket actions are validated external
/// http/https links only — no playback anywhere, and the ticket panel never owns a vertical scroller.</summary>
sealed class ConcertDetailPage : Component
{
    readonly string _concertUri;
    readonly string? _title;

    // Detail breakpoint hysteresis (pure ConcertLayout decision; persisted across width-change rebuilds).
    bool _wasWide;
    bool _wideInit;

    public ConcertDetailPage(string concertUri, string? title)
    {
        _concertUri = concertUri;
        _title = title;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        if (svc is null) return new BoxEl { Grow = 1f };

        var reload = UseSignal(0);
        int gen = reload.Value;   // subscribe → Retry re-fires the load

        // The seed is a representative shape (facts + two offers + lineup rows) so Skel.Region derives its shimmer from
        // the SAME subtree the loaded page renders (never a hand-authored skeleton).
        var details = UseResource(
            ct => svc.Concerts.GetDetailsAsync(_concertUri, cancellationToken: ct),
            (ConcertDetails?)SeedDetails(_concertUri, _title), (_concertUri, gen)).Loadable;

        var region = Skel.Region(
            details,
            content: d => Responsive.Of(width =>
            {
                bool wide = ConcertLayout.DetailWide(width, _wasWide, _wideInit);
                _wasWide = wide;
                _wideInit = true;
                return BuildBody(d!, wide, go);
            }, fallback: 1000f),
            reveal: SkelReveal.Soft,
            onFailed: () => ErrorState.Build(details.Error, onRetry: () => reload.Value++),
            isEmpty: d => d is null,
            onEmpty: () => EmptyState.Build(Loc.Get(Strings.Concerts.Detail.NotAvailable),
                Loc.Get(Strings.Concerts.Detail.NotAvailableSubtitle)),
            group: "concert-detail:" + _concertUri);

        var content = new BoxEl
        {
            Direction = 1,
            Padding = new Edges4(32f, BrowseLayout.MastheadReserve + 40f, 32f, PlayerDock.Reserve + 40f),
            Children = [ region ],
        };
        return ScrollView(content) with { Grow = 1f, MinHeight = 0f, ScrollKey = "concert-detail:" + _concertUri };
    }

    // ── loaded layout ────────────────────────────────────────────────────────────────────────────────────────────────
    Element BuildBody(ConcertDetails d, bool wide, Action<string, string?> go)
    {
        Element? tickets = d.Offers is { Count: > 0 } offers ? TicketSection(offers) : null;

        // Match the tour dashboard's grounded split hero. Prefer a wide lineup banner, then event/artist artwork; the
        // shared composition supplies an intentional tinted fallback when the provider has no usable image.
        var heroArtist = d.Artists?.FirstOrDefault(a => a.HeaderImage is { Url.Length: > 0 });
        Image? artwork = heroArtist?.HeaderImage
            ?? d.Summary.Image
            ?? d.Artists?.FirstOrDefault(a => a.Image is { Url.Length: > 0 })?.Image;
        uint? accent = d.Summary.AccentColor ?? heroArtist?.AccentColor;
        var main = new List<Element>(5);
        main.Add(ConcertUi.SplitEditorialHero(artwork, accent, Identity(d), wide));
        if (!wide && tickets is not null) main.Add(tickets);   // narrow: inline right after the facts
        if (d.Artists is { Count: > 0 } artists) main.Add(Lineup(artists, go));
        if (d.Related is { Count: > 0 } related) main.Add(RelatedShelf(related, go));

        var mainColumn = new BoxEl { Direction = 1, Gap = Spacing.XL, MinWidth = 0f, Children = main.ToArray() };
        if (!wide || tickets is null) return mainColumn;

        // Wide: the 320-DIP ticket panel rides the page scroll (no nested vertical viewport).
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Gap = Spacing.XL, MinWidth = 0f,
            Children =
            [
                mainColumn with { Grow = 1f, Basis = 0f },
                new BoxEl { Width = 320f, Shrink = 0f, Direction = 1, Children = [ tickets ] },
            ],
        };
    }

    // The event display title, shared by the hero and the plain headline.
    string HeroTitle(ConcertDetails d)
    {
        var s = d.Summary;
        return !string.IsNullOrWhiteSpace(s.Title) ? s.Title!
            : s.Venue.Length > 0 ? s.Venue
            : _title is { Length: > 0 } t ? t : Loc.Get(Strings.Concerts.Detail.Concert);
    }

    // ── event identity + facts ───────────────────────────────────────────────────────────────────────────────────────
    // Identity is the hero's copy pane: headline, status, and the provider's local-time facts stay together.
    Element Identity(ConcertDetails d)
    {
        var s = d.Summary;
        var culture = CultureInfo.CurrentCulture;
        string title = HeroTitle(d);

        var headline = new List<Element>(3);
        headline.Add(WaveeType.Eyebrow(Loc.Get(Strings.Concerts.Detail.Concert)) with
        {
            Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        });
        headline.Add(WaveeType.PageHero(title) with
        { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis });
        if (ConcertDetailInfo.DisplayStatus(d.Status) is { } status) headline.Add(StatusPill(status));

        // Facts, each omitted when its data is absent. DateTimeOffset formats its STORED clock time — the provider's
        // local offset is preserved (never converted to the machine's zone).
        var facts = new List<Element>(4)
        {
            FactRow(Icons.Calendar, s.Date.ToString("dddd, MMMM d, yyyy", culture) + " · " + s.Date.ToString("t", culture)),
        };
        if (d.DoorsOpenAt is { } doors)
            facts.Add(FactRow(Icons.Clock, Strings.Concerts.Detail.DoorsOpen(doors.ToString("t", culture))));
        string location = ConcertDetailInfo.LocationLine(s.City, d.Region ?? s.Region, d.Country ?? s.Country);
        string place = s.Venue.Length > 0 && location.Length > 0 ? s.Venue + " · " + location
            : s.Venue.Length > 0 ? s.Venue : location;
        if (place.Length > 0) facts.Add(FactRow(Icons.MapPin, place));
        if (!string.IsNullOrWhiteSpace(d.AgeRestriction))
            facts.Add(FactRow(Icons.Info, Strings.Concerts.Detail.Ages(d.AgeRestriction!)));

        var blocks = new List<Element>(2);
        if (headline.Count > 0)
            blocks.Add(new BoxEl { Direction = 1, Gap = 4f, MinWidth = 0f, Children = headline.ToArray() });
        blocks.Add(new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = facts.ToArray() });

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f,
            Children = blocks.ToArray(),
        };
    }

    static Element FactRow(string glyph, string text) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
        Children =
        [
            Icon(glyph, 16f, Tok.TextSecondary) with { Shrink = 0f },
            Body(text) with
            {
                Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2,
                Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    // A notable state (cancelled / postponed / ...) as a quiet critical-text pill — neutral fill, accent-free.
    static Element StatusPill(string status) => new BoxEl
    {
        AlignSelf = FlexAlign.Start, Padding = new Edges4(Spacing.S, 2f, Spacing.S, 2f),
        Corners = CornerRadius4.All(Radii.Full), Fill = Tok.FillSubtleSecondary,
        Children = [ Caption(status) with { Color = Tok.SystemFillCritical, Weight = 600, MaxLines = 1 } ],
    };

    // ── ticket offers ────────────────────────────────────────────────────────────────────────────────────────────────
    static Element TicketSection(IReadOnlyList<ConcertOffer> offers)
    {
        var kids = new List<Element>(offers.Count + 1) { SectionCaption(Loc.Get(Strings.Concerts.Detail.Tickets)) };
        for (int i = 0; i < offers.Count; i++) kids.Add(OfferCard(offers[i], i));
        return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = kids.ToArray() };
    }

    static Element OfferCard(ConcertOffer offer, int index)
    {
        string provider = string.IsNullOrWhiteSpace(offer.ProviderName)
            ? Loc.Get(Strings.Concerts.Detail.TicketProvider) : offer.ProviderName!;
        var lines = new List<Element>(5)
        {
            WaveeType.TrackTitle(provider) with
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (ConcertOffers.AvailabilityLabel(offer.Availability) is { } availability)
            lines.Add(WaveeType.TrackMeta(availability) with
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        if (ConcertOffers.PriceLabel(offer) is { } price)
            lines.Add(Body(price) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        if (ConcertOffers.SaleWindowLabel(offer) is { } sale)
            lines.Add(WaveeType.TrackMeta(sale) with
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });

        // BUY is an external link only: a validated absolute http/https URL opens through the platform OpenUri seam
        // (the merch/hyperlink idiom); a missing/invalid URL renders quiet text — never a dead button.
        if (ConcertOffers.ValidTicketUrl(offer.Url) is { } url)
            lines.Add(new BoxEl
            {
                Direction = 0, Padding = new Edges4(0f, Spacing.XS, 0f, 0f),
                Children = [ Button.Accent(Loc.Get(Strings.Concerts.Detail.BuyTickets),
                    () => InputHooks.Current.Default.OpenUri?.Invoke(url)) ],
            });
        else
            lines.Add(WaveeType.TrackMeta(Loc.Get(Strings.Concerts.Detail.TicketsUnavailable)) with { MaxLines = 1 });

        return new BoxEl
        {
            // Offers carry no canonical URI; provider+index keeps siblings unique in this bounded, stable-at-mount list.
            Key = "offer:" + index + ":" + provider,
            Direction = 1, Gap = Spacing.XS, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = lines.ToArray(),
        };
    }

    // ── lineup ───────────────────────────────────────────────────────────────────────────────────────────────────────
    static Element Lineup(IReadOnlyList<ConcertArtist> artists, Action<string, string?> go)
    {
        var rows = new Element[artists.Count];
        for (int i = 0; i < artists.Count; i++) rows[i] = LineupRow(artists[i], i, go);
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children = [ SectionCaption(Loc.Get(Strings.Concerts.Detail.Lineup)) ],
                        },
                        WaveeType.Eyebrow(Strings.Concerts.Detail.ArtistCount(artists.Count)) with
                        { Color = Tok.TextSecondary, MaxLines = 1 },
                    ],
                },
                Divider(),
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.S),
                    Children = rows,
                },
            ],
        };
    }

    static Element LineupRow(ConcertArtist artist, int index, Action<string, string?> go)
    {
        string name = string.IsNullOrWhiteSpace(artist.Name) ? Loc.Get(Strings.Concerts.Detail.ArtistFallback) : artist.Name;
        // Only a canonical spotify:artist: URI navigates; billing-text-only names stay plain rows (no dead affordance).
        string? artistUri = artist.Uri is { } uri && EntityUri.Parse(uri) is { IsSpotify: true, Kind: EntityKind.Artist }
            ? uri : null;

        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Width = 44f, Height = 44f, Shrink = 0f, Corners = CornerRadius4.All(22f), ClipToBounds = true,
                Children = [ Surfaces.Artwork(artist.Image, StableSeed(artistUri ?? name), 44f, 44f, 22f) ],
            },
            WaveeType.TrackTitle(name) with
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (artistUri is not null) kids.Add(Icon(Icons.ChevronRight, 14f, Tok.TextSecondary) with { Shrink = 0f });

        return new BoxEl
        {
            Key = artistUri ?? ("lineup:" + index + ":" + name),
            Direction = 0, MinHeight = 60f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
            Corners = CornerRadius4.All(Radii.Control),
            HoverFill = artistUri is not null ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = artistUri is not null ? Tok.FillSubtleTertiary : ColorF.Transparent,
            Role = artistUri is not null ? AutomationRole.Button : AutomationRole.None,
            Focusable = artistUri is not null,
            Cursor = artistUri is not null ? CursorId.Hand : CursorId.Arrow,
            OnClick = artistUri is not null ? () => go("artist:" + artistUri, name) : null,
            Children = kids.ToArray(),
        };
    }

    // ── related concerts ─────────────────────────────────────────────────────────────────────────────────────────────
    // Cell 0 is the Browse-all navigation tile (the shelf's standing exit to the Concert Hub); the related events follow.
    static Element RelatedShelf(IReadOnlyList<Concert> related, Action<string, string?> go)
    {
        int n = Math.Min(related.Count, 15) + 1;
        return PagedShelf.Create(
            n,
            cardAt: (i, w) => i == 0
                ? ConcertUi.BrowseAllCard(Loc.Get(Strings.Concerts.LiveMusic),
                    Loc.Get(Strings.Concerts.BrowseAll),
                    () => go(ConcertRoutes.Hub, Loc.Get(Strings.Concerts.Title)))
                : ConcertUi.VerticalCard(related[i - 1],
                    () => go(ConcertRoutes.Detail(related[i - 1].Uri), related[i - 1].Title ?? related[i - 1].Venue)),
            header: SectionCaption(Loc.Get(Strings.Concerts.Detail.RelatedConcerts)),
            headerGap: Spacing.S,
            measured: true, keyOf: i => i == 0 ? "browse-all" : related[i - 1].Uri);
    }

    // The accent eyebrow shared by every section (matches the schedule page's header style). AccentDecor: kept.
    static Element SectionCaption(string label) => WaveeType.Eyebrow(label) with
    {
        Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    static int StableSeed(string value)
    {
        int hash = 17;
        for (int i = 0; i < value.Length; i++) hash = unchecked(hash * 31 + value[i]);
        return hash & 0x7fffffff;
    }

    // Representative pending shape (facts + two offers + three lineup rows). The engine repaints all text/media, so none
    // of this placeholder content is shown — it only gives Skel.Region a real subtree to derive the shimmer from.
    static ConcertDetails SeedDetails(string concertUri, string? title)
    {
        var date = new DateTimeOffset(2025, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var summary = new Concert(concertUri, title ?? "", "Venue name placeholder", "City placeholder", date);
        return new ConcertDetails(
            summary,
            Artists:
            [
                new ConcertArtist("Artist placeholder", "spotify:artist:seed-a"),
                new ConcertArtist("Artist placeholder", "spotify:artist:seed-b"),
                new ConcertArtist("Artist placeholder"),
            ],
            Offers:
            [
                new ConcertOffer("Provider placeholder", "https://seed.invalid/tickets",
                    ConcertOfferAvailability.Available, MinPrice: 45m, MaxPrice: 75m, Currency: "EUR"),
                new ConcertOffer("Provider placeholder", null),
            ],
            DoorsOpenAt: date.AddHours(-1));
    }
}
