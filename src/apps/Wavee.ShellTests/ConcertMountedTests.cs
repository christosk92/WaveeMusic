// ── Wavee.ShellTests/ConcertMountedTests.cs — the concert surfaces' views, mounted (themes plan 6.1, T10b3) ───────────────────────────────────────
//
// The Concerts hub, its filter bar, tail and location picker, an artist's tour dashboard and the event page are the structure's since T10b3; the core hosts keep
// the demands, the filter tuple, the append gate and the shaping (`Wavee.Tests` pins the pure ones: `ConcertContractTests`, `ConcertSchedulePageTests`,
// `ConcertHubModelTests`, `ConcertLayoutTests`). What only a mounted tree can say:
//
//   registered   Classic is the registered template of every concert key
//   hub          the filter bar is the keyed pinned card at rest (the "Dates" pill and the "This weekend" chip) and fuses its pill once a window is picked, from the
//                SAME signal the host publishes; a feed body mounts the host's tail under the block's key and no grid it did not plan
//   schedule     a pending page asks no model, an empty schedule draws the vacancy, a ready one is the keyed month board
//   detail       a pending page reads no row, a ready one keys each offer by its provider and each billed act (a canonical artist by slot, a billing-only act by name)
//   picker       the results are the keyed rows, and no rows draw while only searching

using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ConcertMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_concert_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.ConcertsHub.Id, "classic.concerts.hub"),
                (ViewKeys.ArtistConcerts.Id, "classic.concerts.artist-schedule"),
                (ViewKeys.ConcertDetail.Id, "classic.concerts.detail"),
                (ViewKeys.ConcertTail.Id, "classic.concerts.tail"),
                (ViewKeys.ConcertPlacePicker.Id, "classic.concerts.place-picker"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    // ── the hub ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed record HubFixture(ConcertHubProps Props, Signal<ConcertWhen> When);

    static HubFixture Hub(ConcertHubBody? body, bool pending = false)
    {
        var when = new Signal<ConcertWhen>(ConcertWhen.Any);
        var bar = new ConcertFilterBarProps
        {
            Place = new Signal<ConcertPlace?>(null),
            Inferred = new Signal<bool?>(null),
            Radius = new Signal<int>(ConcertHub.DefaultRadiusKm),
            When = when,
            Selected = new Signal<IReadOnlyList<string>>(Array.Empty<string>()),
            Concepts = new Signal<IReadOnlyList<ConcertConcept>>(Array.Empty<ConcertConcept>()),
            Count = new Signal<int?>(null),
            SetWhen = w => when.Value = w,
            SetSelected = static _ => { },
            SetRadius = static _ => { },
            CaptureWhere = static _ => { },
            WhereAnchor = static () => default,
            OpenPicker = static () => { },
        };
        var props = new ConcertHubProps
        {
            ScrollScope = "test:",
            Scroll = new ScrollHandle(),
            Pending = () => pending,
            Failed = static () => false,
            Body = body,
            Retry = static () => { },
            OpenPicker = static () => { },
            Bar = bar,
            GridCount = new Signal<int>(0),
            GridSlotAt = static _ => 0,
            Tail = static key => new BoxEl { Key = key },
        };
        return new HubFixture(props, when);
    }

    [Fact]
    public void The_filter_bar_is_the_keyed_pinned_card_with_the_rest_pill_and_the_weekend_chip()
    {
        var fixture = Hub(body: null, pending: true);
        ViewHarness.With(host, ViewKeys.ConcertsHub, fixture.Props,
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("concert-filter-bar"), "the bar is the keyed child of the page column");
                Assert.True(harness.Contains("when-pill"), "the rest state is the Dates pill");
                Assert.True(harness.Contains("when-chip"), "and the This weekend chip");
                Assert.False(harness.Contains("when-pill:seg"), "no window is picked yet");
            }, UnderClassic());
    }

    [Fact]
    public void A_picked_window_fuses_the_pill_from_the_hosts_signal_and_takes_the_chip_away()
    {
        var fixture = Hub(body: null, pending: true);
        ViewHarness.With(host, ViewKeys.ConcertsHub, fixture.Props,
            harness =>
            {
                harness.RunFrames(30);
                var day = new DateOnly(2030, 6, 8);
                fixture.When.Value = new ConcertWhen(ConcertWhenKind.Custom, "Weekend", new ConcertDateRange(day, day.AddDays(2)));
                harness.RunFrames(60);
                Assert.True(harness.Contains("when-pill:seg"), "the fused pill docks its segment");
            }, UnderClassic());
    }

    [Fact]
    public void A_feed_body_mounts_the_hosts_tail_under_the_blocks_key_and_no_grid_it_did_not_plan()
    {
        var body = new ConcertHubBody(ConcertHubBodyKind.Feed,
            [new ConcertFeedBlock(ConcertBlockKind.Tail, "concert-append:cursor", false, Array.Empty<ConcertShelfItem>(), 0)]);
        ViewHarness.With(host, ViewKeys.ConcertsHub, Hub(body).Props,
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("concert-append:cursor"), "the tail is the host's keyed mount");
                Assert.False(harness.Contains("hub-grid"), "no grid block was planned");
            }, UnderClassic());
    }

    [Fact]
    public void A_pending_hub_draws_no_feed()
    {
        var body = new ConcertHubBody(ConcertHubBodyKind.Feed,
            [new ConcertFeedBlock(ConcertBlockKind.Tail, "concert-append:cursor", false, Array.Empty<ConcertShelfItem>(), 0)]);
        ViewHarness.With(host, ViewKeys.ConcertsHub, Hub(body, pending: true).Props,
            harness =>
            {
                harness.RunFrames(30);
                Assert.False(harness.Contains("concert-append:cursor"), "the region shows its shimmer, not the feed");
                Assert.True(harness.Contains("concert-filter-bar"));
            }, UnderClassic());
    }

    [Theory]
    [InlineData(ConcertTailState.None)]
    [InlineData(ConcertTailState.Spacer)]
    [InlineData(ConcertTailState.Shimmer)]
    public void The_tail_mounts_in_every_state(ConcertTailState state)
        => ViewHarness.With(host, ViewKeys.ConcertTail, new ConcertTailProps(state),
            harness => harness.RunFrames(10), UnderClassic());

    // ── the artist's schedule ────────────────────────────────────────────────────────────────────────────────────────────

    static ConcertScheduleModel ModelOf(bool seed)
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var shows = new List<ConcertShow>
        {
            new("u1", null, "Venue A", "City A", new DateTimeOffset(2030, 3, 5, 20, 0, 0, TimeSpan.Zero)),
            new("u2", null, "Venue B", "City B", new DateTimeOffset(2030, 3, 19, 20, 0, 0, TimeSpan.Zero)),
            new("u3", null, "Venue C", "City C", new DateTimeOffset(2030, 4, 2, 20, 0, 0, TimeSpan.Zero)),
        };
        var chronological = ConcertSchedules.Chronological(shows);
        var groups = ConcertScheduleShaping.GroupByMonth(ConcertScheduleShaping.BoardConcerts(chronological, null));
        return new ConcertScheduleModel
        {
            Name = "Artist",
            Spotlight = null,
            Stats = ConcertScheduleShaping.Stats(chronological),
            Now = now,
            Groups = groups,
            Selected = 0,
            Nearby = new HashSet<string>(),
            SpotlightSlot = 0,
            SpotlightAccent = 0,
            HeroImageUrl = null,
            LocationLabel = "City A",
            Seed = seed,
        };
    }

    static ConcertScheduleProps Schedule(bool pending, bool empty, Func<ConcertScheduleModel> model) => new()
    {
        RouteKey = "fixture",
        ScrollScope = "test:",
        Pending = () => pending,
        Failed = static () => false,
        Empty = empty,
        Retry = static () => { },
        Model = model,
        Seed = static () => ModelOf(seed: true),
        SelectMonth = static _ => { },
        OpenPicker = static () => { },
        OpenSpotlight = static () => { },
        CaptureAnchor = static _ => { },
    };

    [Fact]
    public void A_pending_schedule_asks_no_model_and_an_empty_one_draws_the_vacancy_without_asking_either()
    {
        int asked = 0;
        Func<ConcertScheduleModel> model = () => { asked++; return ModelOf(seed: false); };
        ViewHarness.With(host, ViewKeys.ArtistConcerts, Schedule(pending: true, empty: false, model),
            harness =>
            {
                harness.RunFrames(30);
                Assert.Equal(0, asked);
                Assert.Empty(harness.FindPrefix("dashboard-month:"));
            }, UnderClassic());
        ViewHarness.With(host, ViewKeys.ArtistConcerts, Schedule(pending: false, empty: true, model),
            harness =>
            {
                harness.RunFrames(30);
                Assert.Equal(0, asked);
                Assert.Empty(harness.FindPrefix("dashboard-month:"));
            }, UnderClassic());
    }

    [Fact]
    public void A_ready_schedule_is_the_keyed_month_board_under_the_shy_pill_slot()
        => ViewHarness.With(host, ViewKeys.ArtistConcerts, Schedule(pending: false, empty: false, () => ModelOf(seed: false)),
            harness =>
            {
                harness.RunFrames(60);
                Assert.NotEmpty(harness.FindPrefix("dashboard-month:2030-03"));
                Assert.True(harness.Contains("shy-month:fixture"), "the pill's slot is keyed by the route");
                Assert.True(harness.Contains("u1"), "a show's tile is keyed by its uri");
            }, UnderClassic());

    // ── the event page ───────────────────────────────────────────────────────────────────────────────────────────────────

    static ConcertDetailView Detail() => new("Title", "Saturday, June 8, 2030 · 20:00", null, "Venue · City", null, null,
        [new ConcertOfferView("Ticketmaster", "Available", "From 40", null, "https://tickets.example/e"), new ConcertOfferView("Door", null, null, null, null)],
        [new ConcertActView("Headliner", null, 5, true), new ConcertActView("Support", null, 0, false)],
        [], null, 0);

    static ConcertDetailProps DetailProps(bool pending, bool unavailable, Func<ConcertDetailView> detail) => new()
    {
        RouteKey = "fixture",
        ScrollScope = "test:",
        Pending = () => pending,
        Unavailable = unavailable,
        Detail = detail,
    };

    [Fact]
    public void A_pending_event_page_reads_no_row_and_a_ready_one_keys_offers_and_acts()
    {
        int read = 0;
        Func<ConcertDetailView> detail = () => { read++; return Detail(); };
        ViewHarness.With(host, ViewKeys.ConcertDetail, DetailProps(pending: true, unavailable: false, detail),
            harness =>
            {
                harness.RunFrames(30);
                Assert.Equal(0, read);
                Assert.False(harness.Contains("offer:0:Ticketmaster"));
            }, UnderClassic());
        ViewHarness.With(host, ViewKeys.ConcertDetail, DetailProps(pending: false, unavailable: false, detail),
            harness =>
            {
                harness.RunFrames(60);
                Assert.True(harness.Contains("offer:0:Ticketmaster"), "an offer is keyed by its index and provider");
                Assert.True(harness.Contains("offer:1:Door"));
                Assert.True(harness.Contains("act:5"), "a canonical artist is keyed by its slot");
                Assert.True(harness.Contains("lineup:1:Support"), "a billing-only act by its row and name");
                Assert.False(harness.Contains("browse-all-concerts"), "no related shelf without related concerts");
            }, UnderClassic());
    }

    [Fact]
    public void An_unavailable_event_draws_no_row()
    {
        int read = 0;
        ViewHarness.With(host, ViewKeys.ConcertDetail, DetailProps(pending: false, unavailable: true, () => { read++; return Detail(); }),
            harness =>
            {
                harness.RunFrames(30);
                Assert.Equal(0, read);
                Assert.False(harness.Contains("offer:0:Ticketmaster"));
            }, UnderClassic());
    }

    // ── the location picker ──────────────────────────────────────────────────────────────────────────────────────────────

    static ConcertPlacePickerProps Picker(int[] results, bool loading) => new()
    {
        Query = static () => new Signal<string>(""),
        Results = new Signal<int[]>(results),
        Loading = new Signal<bool>(loading),
        Error = new Signal<string?>(null),
        UseMyLocation = static () => { },
        Select = static _ => { },
    };

    [Fact]
    public void The_pickers_results_are_keyed_rows_and_none_draw_while_searching()
    {
        ViewHarness.With(host, ViewKeys.ConcertPlacePicker, Picker([7, 9], loading: false),
            harness =>
            {
                harness.RunFrames(20);
                Assert.True(harness.Contains("place:7"));
                Assert.True(harness.Contains("place:9"));
            }, UnderClassic());
        ViewHarness.With(host, ViewKeys.ConcertPlacePicker, Picker([], loading: true),
            harness =>
            {
                harness.RunFrames(20);
                Assert.Empty(harness.FindPrefix("place:"));
            }, UnderClassic());
    }
}
