// ── Entities/Concert.Detail.cs ─────────────────────────────────────────────────────────────────────────────────────
// the concert page's CORE HOST (0.2.9 ConcertDetailPage); T10b3 moved the pictures to Classic (`Pages.Concert`)
//
// Role: UI (core hosts)
// Owner: N (stream N-C)
// Spec: ch 17 §0 (the fifteen non-negotiables), §1 (the three trees), §2 W19-W22, §5 motion, §6 interaction, §7 readiness, §9 must-not-simplify
//
// The host demands the whole row (<see cref="ConcertFields.All"/>) once per slot per scope, publishes the readiness verdict (settled = the whole row was asked
// and nothing is in flight: a group that never landed stays unknown, W22) and reads the row into the page's VALUES (`ConcertDetailView`: the facts in the
// PROVIDER's clock, the offers with their validated links, the lineup, the related items, the hero photo and its tint). The view draws them.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct Concert
{
    /// <summary>The concert page (0.2.9 <c>ConcertDetailPage</c>): demands the whole row (<see cref="ConcertFields.All"/>) once per slot per scope.</summary>
    [CoreHost]
    sealed class DetailHost : Component
    {
        Scope? _scope;
        EntityUri _subject;
        Concert _concert;
        string _routeTitle = "";
        bool _pending;
        readonly Func<bool> _pendingFn;
        readonly Func<ConcertDetailView> _detail;
        readonly Action _demand;

        public DetailHost()
        {
            _pendingFn = () => _pending;
            _detail = () => DetailViewOf(_concert, _routeTitle);
            _demand = () => { if (_concert.IsValid && !_concert.Knows(ConcertFields.All)) Entities.Ensure(_concert, ConcertFields.All); };
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Concerts.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = e.ConcertOffers.Changed.Value;
            _ = e.ConcertLineup.Changed.Value;
            _ = e.ConcertRelated.Changed.Value;
            _routeTitle = Entities.Strings.Resolve(p.Arg);
            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject))
            {
                _scope = scope;
                _subject = p.Subject;
                _concert = p.Subject.IsValid ? Entities.Concert(p.Subject) : default;
            }
            var c = _concert;
            UseEffect(_demand, DepKey.From(c.Slot, (int)epoch));

            // Settled = the whole row was asked and nothing is in flight: a group that never landed stays unknown (W22).
            bool all = c.IsValid && c.Knows(ConcertFields.All);
            bool settled = !c.IsValid || ((scope.Concerts.Asked[c.Slot] & (uint)ConcertFields.All) != 0 && scope.Concerts.Inflight[c.Slot] == 0);
            _pending = !all && !settled;
            return Slots.View(ViewKeys.ConcertDetail, new ConcertDetailProps
            {
                RouteKey = p.RouteKey,
                ScrollScope = UseContext(Shell.PageScrollScope),
                Pending = _pendingFn,
                Unavailable = !_pending && (!c.IsValid || !c.Knows(ConcertFields.Identity)),
                Detail = _detail,
            });
        }

        /// <summary>Everything the detail paints, as VALUES read off the handle.</summary>
        static ConcertDetailView DetailViewOf(Concert c, string routeTitle)
        {
            var s = Entities.Strings;
            var culture = CultureInfo.CurrentCulture;
            string title = s.Resolve(c.TitleId), venue = s.Resolve(c.VenueId);
            title = !string.IsNullOrWhiteSpace(title) ? title : venue.Length > 0 ? venue
                  : routeTitle.Length > 0 ? routeTitle : Loc.Get(Strings.Concerts.Detail.Concert);
            string location = ConcertDetailInfo.LocationLine(s.Resolve(c.CityId), s.Resolve(c.RegionId), s.Resolve(c.CountryId));
            string ages = s.Resolve(c.AgeRestrictionId);

            var offerRows = c.Offers;
            var offers = new ConcertOfferView[offerRows.Length];
            for (int i = 0; i < offers.Length; i++)
            {
                ref readonly var o = ref offerRows[i];
                string provider = s.Resolve(o.Provider);
                offers[i] = new ConcertOfferView(provider.Length > 0 ? provider : Loc.Get(Strings.Concerts.Detail.TicketProvider),
                    ConcertOffers.AvailabilityLabel((ConcertOfferAvailability)o.Availability), ConcertOffers.PriceLabel(in o),
                    ConcertOffers.SaleWindowLabel(in o, culture), ConcertOffers.ValidTicketUrl(s.Resolve(o.Url)));
            }

            // The hero photo, in strict order: the first act with a banner, the show's own image, the first act's avatar.
            // The tint the other way round: the show's accent, then that banner act's (W19).
            var acts = c.Lineup;
            var slots = c.LineupSlots;
            var lineup = new ConcertActView[acts.Length];
            string? banner = null, avatar = null;
            uint bannerAccent = 0;
            for (int i = 0; i < acts.Length; i++)
            {
                ref readonly var act = ref acts[i];
                int slot = i < slots.Length ? slots[i] : Table.None;
                var artist = slot > Table.None ? new Artist(slot) : default;
                string name = s.Resolve(act.BillingName);
                if (name.Length == 0 && artist.IsValid) name = artist.Name;
                string? image = Controls.ArtUrl(act.Image) ?? (artist.IsValid ? Controls.ArtUrl(artist.ImageId) : null);
                lineup[i] = new ConcertActView(name.Length > 0 ? name : Loc.Get(Strings.Concerts.Detail.ArtistFallback), image, slot,
                    (act.Flags & LineupEdge.Navigable) != 0 && artist.IsValid);
                if (banner is null && Controls.ArtUrl(act.HeaderImage) is { } header) { banner = header; bannerAccent = act.Accent; }
                avatar ??= image;
            }
            return new ConcertDetailView(title, ConcertTime.DetailDateLine(c.LocalDate, culture),
                c.DoorsLocal is { } doors ? Strings.Concerts.Detail.DoorsOpen(doors.ToString("t", culture)) : null,
                venue.Length > 0 && location.Length > 0 ? venue + " · " + location : venue.Length > 0 ? venue : location,
                ages.Length > 0 ? Strings.Concerts.Detail.Ages(ages) : null,
                ConcertDetailInfo.DisplayStatus(s.Resolve(c.StatusTextId)),
                offers, lineup, ConcertVerbs.ConcertItems(c.RelatedSlots, 15),
                banner ?? Controls.ArtUrl(c.ImageId) ?? avatar, c.Accent != 0 ? c.Accent : bannerAccent)
            {
                Date = c.LocalDate,
            };
        }
    }
}
