// ── Entities/Concert.Schedule.cs ───────────────────────────────────────────────────────────────────────────────────
// the artist's tour dashboard's CORE HOST (0.2.9 ArtistSchedulePage); T10b3 moved the pictures to Classic (`Pages.ArtistConcerts`)
//
// Role: UI (core hosts)
// Owner: N (stream N-C)
// Spec: ch 17 §0 (the fifteen non-negotiables), §1 (the three trees), §2 W13-W18, §5 motion, §6 interaction, §7 readiness, §9 must-not-simplify
//
// The host demands the schedule edge through Fetch while Unknown (and the artist's identity and the two location reads), publishes the readiness verdict and the
// selected month, and shapes the dashboard's MODEL from the artist's shows (`ConcertScheduleShaping`: the spotlight, the month boards with their runs, the
// near-you guard, the stats). The view draws it at the measured width (the 760 / 720 hysteresis is Classic's tier rule), keeps the board's expansions and
// the shy month pill, and shimmers over `Seed`'s eight placeholder dates (§9 #4).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct Concert
{
    /// <summary>The artist's tour dashboard (0.2.9 <c>ArtistSchedulePage</c>): the split hero owns the next show; ONE month
    /// at a time renders in the tour-dates card, remembered by its <c>yyyy-MM</c> key. Demands the schedule edge through Fetch
    /// while Unknown.</summary>
    [CoreHost]
    sealed class ScheduleHost : Component
    {
        readonly Signal<string?> _month = new(null);
        readonly PlacePicker _picker = new();

        Scope? _scope;
        EntityUri _subject;
        Artist _artist;
        string _routeName = "";
        bool _pending, _failed, _locationAsked;
        IOverlayService? _overlay;
        NodeHandle _anchor;

        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<ConcertScheduleModel> _model, _seed;
        readonly Action _demand, _retry, _openPicker, _openSpotlight;
        readonly Action<string> _selectMonth;
        readonly Action<NodeHandle> _captureAnchor;
        int _spotlightSlot;

        public ScheduleHost()
        {
            _pendingFn = () => _pending;
            _failedFn = () => _failed;
            _demand = Demand;
            _retry = () => { if (_artist.IsValid) Entities.EnsureEdge(FetchEdge.ArtistConcerts, _artist.Slot); };
            _openPicker = () => _picker.Toggle(_overlay, () => _anchor);
            _openSpotlight = () => { if (_spotlightSlot > 0) ConcertVerbs.OpenConcert(_spotlightSlot); };
            _captureAnchor = h => _anchor = h;
            _selectMonth = key => _month.Value = key;
            _model = Model;
            _seed = Seed;
            // A saved place moves the near-you bits: the schedule is re-asked against it.
            _picker.Saved = () => { if (_artist.IsValid) Entities.RefreshEdge(FetchEdge.ArtistConcerts, _artist.Slot); };
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Artists.Changed.Value;
            _ = scope.Concerts.Changed.Value;
            _ = scope.Places.Changed.Value;
            _ = e.ArtistConcerts.Changed.Value;
            _ = _month.Value;
            _overlay = UseContext(Overlay.Service);
            string scrollScope = UseContext(Shell.PageScrollScope);
            _routeName = Entities.Strings.Resolve(p.Arg);
            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject))
            {
                _scope = scope;
                _subject = p.Subject;
                _artist = p.Subject.IsValid ? Entities.Artist(p.Subject) : default;
                _locationAsked = false;
            }
            var a = _artist;
            UseEffect(_demand, DepKey.From(a.Slot, (int)epoch));

            var readiness = a.IsValid ? e.ArtistConcerts.Readiness(a.Slot) : EdgeState.Failed;
            _pending = readiness == EdgeState.Unknown;
            _failed = readiness == EdgeState.Failed;

            return Slots.View(ViewKeys.ArtistConcerts, new ConcertScheduleProps
            {
                RouteKey = p.RouteKey,
                ScrollScope = scrollScope,
                Pending = _pendingFn,
                Failed = _failedFn,
                Empty = !_pending && !_failed && e.ArtistConcerts.Count(a.Slot) == 0,
                Retry = _retry,
                Model = _model,
                Seed = _seed,
                SelectMonth = _selectMonth,
                OpenPicker = _openPicker,
                OpenSpotlight = _openSpotlight,
                CaptureAnchor = _captureAnchor,
            });
        }

        void Demand()
        {
            var a = _artist;
            if (!a.IsValid) return;
            var scope = Entities.Current;
            if (scope.Edges.ArtistConcerts.State(a.Slot) == EdgeState.Unknown && !scope.Edges.ArtistConcerts.IsFailed(a.Slot))
                Entities.EnsureEdge(FetchEdge.ArtistConcerts, a.Slot);
            if (!a.Knows(ArtistFields.Identity)) Entities.Ensure(a, ArtistFields.Identity);
            if (_locationAsked) return;
            _locationAsked = true;
            // The label reads the saved place, else the artist page's own location read (a second, independent read).
            if (scope.SavedPlace <= 0) ConcertHost.Current.ResolveLocation(null);
            if (scope.ArtistPagePlace <= 0) ConcertHost.Current.ResolveArtistPageLocation(null);
        }

        /// <summary>The loaded dashboard's model, read at build time off the schedule edge.</summary>
        ConcertScheduleModel Model()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(Store.ToUnix(Entities.Now));
            return Shaped(ConcertShows.From(Entities.Current.Edges.ArtistConcerts.Targets(_artist.Slot)), now, seed: false);
        }

        /// <summary>The shimmer seed's model (§9 #4): eight dates with a 2-night run at +33/+34.</summary>
        ConcertScheduleModel Seed()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(Store.ToUnix(Entities.Now));
            return Shaped(SeedShows(now), now, seed: true);
        }

        ConcertScheduleModel Shaped(IReadOnlyList<ConcertShow> shows, DateTimeOffset now, bool seed)
        {
            var chronological = ConcertSchedules.Chronological(shows);
            var spotlight = ConcertScheduleShaping.Spotlight(chronological, now);
            var itinerary = ConcertScheduleShaping.BoardConcerts(chronological, spotlight);
            var groups = ConcertScheduleShaping.GroupByMonth(itinerary);
            var near = new List<ConcertShow>();
            for (int i = 0; i < chronological.Count; i++) if (chronological[i].IsNearUser) near.Add(chronological[i]);
            var nearby = ConcertScheduleShaping.GuardedNearSet(itinerary, near);

            uint accent = 0;
            if (!seed)
            {
                _spotlightSlot = 0;
                if (spotlight is { } next)
                {
                    _spotlightSlot = Entities.Current.Concerts.Slot(next.Uri.AsSpan());
                    accent = new Concert(_spotlightSlot).Accent;
                }
            }
            return new ConcertScheduleModel
            {
                Name = seed ? " " : ArtistName(),
                Spotlight = spotlight,
                Stats = ConcertScheduleShaping.Stats(chronological),
                Now = now,
                Groups = groups,
                Selected = ConcertScheduleShaping.ResolveMonthIndex(groups, _month.Peek(), spotlight),
                Nearby = nearby,
                SpotlightAccent = accent,
                HeroImageUrl = seed ? null : Controls.ArtUrl(_artist.HeaderId),
                LocationLabel = seed ? " " : LocationLabel(),
                Seed = seed,
            };
        }

        string ArtistName()
        {
            string name = _artist.IsValid ? _artist.Name : "";
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return _routeName.Length > 0 ? _routeName : Loc.Get(Strings.Concerts.Detail.ArtistFallback);
        }

        string LocationLabel()
        {
            var scope = Entities.Current;
            if (ConcertPlaces.From(scope.SavedPlace)?.Name is { Length: > 0 } saved) return saved;
            if (ConcertPlaces.From(scope.ArtistPagePlace)?.Name is { Length: > 0 } page) return page;
            return Loc.Get(Strings.Concerts.Location.Set);
        }

        static ConcertShow[] SeedShows(DateTimeOffset now)
        {
            int[] offsets = [5, 12, 26, 33, 34, 48, 62, 76];
            var start = new DateTimeOffset(now.Year, now.Month, now.Day, 20, 0, 0, now.Offset);
            var shows = new ConcertShow[offsets.Length];
            for (int i = 0; i < shows.Length; i++)
                shows[i] = new ConcertShow("seed:" + i.ToString(CultureInfo.InvariantCulture), "",
                    i is 3 or 4 ? "Residency venue placeholder" : "Venue name placeholder", "City placeholder", start.AddDays(offsets[i]));
            return shows;
        }
    }
}
