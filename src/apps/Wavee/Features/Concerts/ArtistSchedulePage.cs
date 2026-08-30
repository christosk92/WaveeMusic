using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Browse;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The artist-owned Tour Dashboard: one split editorial hero owns the next show, while one controlled month at
/// a time renders in a bounded Fluent itinerary surface. Location/loading/navigation behavior remains provider-neutral.</summary>
sealed class ArtistSchedulePage : Component
{
    readonly string _artistUri;
    readonly string? _artistName;
    readonly ConcertLocationController _location = new();
    readonly ShyMonthTracker _tracker = new();
    readonly Signal<string?> _selectedMonth = new(null);
    readonly Signal<ConcertPlace?> _savedPlace = new(null);
    readonly Signal<int> _monthStateRevision = new(0);
    readonly HashSet<string> _expandedMonths = new(StringComparer.Ordinal);
    readonly HashSet<string> _expandedRuns = new(StringComparer.Ordinal);

    Signal<int>? _reloadSignal;
    Ref<NodeHandle> _anchor = null!;
    bool _wasWide;
    bool _wideInit;

    public ArtistSchedulePage(string artistUri, string? artistName)
    {
        _artistUri = artistUri;
        _artistName = artistName;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var overlay = UseContext(Overlay.Service);
        if (svc is null) return new BoxEl { Grow = 1f };

        var post = UsePost();
        _anchor = UseRef<NodeHandle>(default);
        var reload = UseSignal(0);
        var pageScroll = UseSignal(0f);
        _reloadSignal = reload;
        int gen = reload.Value;
        var now = DateTimeOffset.Now;

        _location.Attach(svc, overlay, post, onSaved: place =>
        {
            _savedPlace.Value = place;
            if (_reloadSignal is { } r) r.Value++;
        });
        UseSignalEffect(_location.TrackQuery);

        var savedPlace = _savedPlace.Value;
        var schedule = UseResource(
            ct => svc.Concerts.GetArtistScheduleAsync(_artistUri, savedPlace?.GeoHash, cancellationToken: ct),
            (ArtistConcertSchedule?)SeedSchedule(_artistUri, _artistName), DepKey.From(HashCode.Combine(_artistUri, savedPlace?.GeoHash, gen))).Loadable;
        var locationLabel = UseResource(
            ct => svc.Concerts.GetArtistPageLocationAsync(ct), (ConcertPlace?)null, gen).Loadable;

        string locLabel = savedPlace?.Name is { Length: > 0 } savedName
            ? savedName
            : locationLabel.Value.Value?.Name is { Length: > 0 } nm ? nm : Loc.Get(Strings.Concerts.Location.Set);
        var region = Skel.Region(
            schedule,
            content: s => Responsive.Of(width =>
            {
                bool wide = ConcertLayout.ScheduleWide(width, _wasWide, _wideInit);
                _wasWide = wide;
                _wideInit = true;
                return BuildBody(s!, wide, go, locLabel, now);
            }, fallback: 900f),
            reveal: SkelReveal.Soft,
            onFailed: () => ErrorState.Build(schedule.Error, onRetry: () => reload.Value++),
            isEmpty: s => s is null || s.Concerts.Count == 0,
            onEmpty: () => EmptyState.Build(Loc.Get(Strings.Concerts.Schedule.NoUpcoming),
                Loc.Get(Strings.Concerts.Schedule.NoUpcomingSubtitle)),
            group: "artist-schedule:" + _artistUri);

        var content = new BoxEl
        {
            Direction = 1,
            Padding = new Edges4(32f, BrowseLayout.MastheadReserve + 40f, 32f, PlayerDock.Reserve + 40f),
            Children = [ region ],
        };
        var scroll = ScrollView(content) with
        {
            Grow = 1f, MinHeight = 0f, ScrollKey = "artist-schedule:" + _artistUri,
            OnScrollGeometryChanged = (g => (long)(g.OffsetY / 8f), g => pageScroll.Value = g.OffsetY),
            OnRealized = h => _tracker.Viewport = h,
        };

        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ZStack = true,
            Children =
            [
                scroll,
                Embed.Comp(() => new ShyMonthPill(_tracker, pageScroll)) with { Key = "shy-month:" + _artistUri },
            ],
        };
    }

    Element BuildBody(ArtistConcertSchedule schedule, bool wide, Action<string, string?> go,
        string locationLabel, DateTimeOffset now)
    {
        var chronological = ConcertSchedules.Chronological(schedule.Concerts);
        var spotlight = ConcertScheduleShaping.Spotlight(chronological, now);
        var itinerary = ConcertScheduleShaping.BoardConcerts(chronological, spotlight);
        var groups = ConcertScheduleShaping.GroupByMonth(itinerary);
        var nearby = ConcertScheduleShaping.GuardedNearSet(itinerary, schedule.Nearby);
        var stats = ConcertScheduleShaping.Stats(chronological);
        string artistName = string.IsNullOrWhiteSpace(schedule.Artist.Name)
            ? (_artistName is { Length: > 0 } n ? n : Loc.Get(Strings.Concerts.Detail.ArtistFallback))
            : schedule.Artist.Name;

        var sections = new List<Element>(2)
        {
            TourHero(schedule, artistName, spotlight, stats, wide, locationLabel, now, go),
        };

        int selected = ConcertScheduleShaping.ResolveMonthIndex(groups, _selectedMonth.Value, spotlight);
        if (selected >= 0)
        {
            var group = groups[selected];
            var culture = CultureInfo.CurrentCulture;
            string month = new DateTime(group.Year, group.Month, 1).ToString("MMMM yyyy", culture);
            _tracker.Label = month + " · " + Strings.Concerts.Schedule.ShowCount(group.ShowCount);
            sections.Add(TourDates(groups, selected, nearby, artistName, wide, go));
        }
        else
        {
            _tracker.Label = "";
            _tracker.PanelHeader = NodeHandle.Null;
            // The hero (spotlight/stats) rendered but the itinerary has no months — e.g. the one show on sale IS the
            // spotlight. An intentional bounded card fills what would otherwise be a page-sized void under the hero.
            sections.Add(new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.XL, Spacing.XXL, Spacing.XL, Spacing.XXL),
                Corners = CornerRadius4.All(Radii.Card),
                Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    EmptyState.Build(Loc.Get(Strings.Concerts.Schedule.NoMoreDates),
                        Loc.Get(Strings.Concerts.Schedule.NoMoreDatesSubtitle),
                        actionLabel: Loc.Get(Strings.Concerts.Location.Set),
                        onAction: () => _location.TogglePicker(() => _anchor.Value)),
                ],
            });
        }

        return new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.XL, Children = sections.ToArray() };
    }

    Element TourHero(ArtistConcertSchedule schedule, string artistName, Concert? spotlight,
        ConcertTourStats stats, bool wide, string locationLabel, DateTimeOffset now, Action<string, string?> go)
    {
        var blocks = new List<Element>(6)
        {
            WaveeType.Eyebrow(Loc.Get(Strings.Concerts.Schedule.OnTour)) with
            {
                Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            WaveeType.PageHero(artistName) with
            { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
            Body(ConcertScheduleShaping.StatsLine(stats)) with
            { Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };

        if (spotlight is { } next)
        {
            var text = ConcertScheduleShaping.TileText(next, artistName);
            string caption = Strings.Concerts.Schedule.NextShow(ConcertScheduleShaping.RelativeTime(next.Date, now));
            blocks.Add(new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeDividerDefault });
            blocks.Add(new BoxEl
            {
                Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Children =
                [
                    ConcertUi.DateBlock(next.Date),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
                        Children =
                        [
                            WaveeType.Eyebrow(caption) with
                            {
                                Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            },
                            WaveeType.TrackTitle(text.Primary) with
                            { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            WaveeType.TrackMeta(text.Secondary) with
                            { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            });
        }

        Element location = ConcertUi.LocationButton(locationLabel, () => _location.TogglePicker(() => _anchor.Value))
            with { OnRealized = h => _anchor.Value = h };
        Action open = spotlight is { } s
            ? () => go(ConcertRoutes.Detail(s.Uri), s.Title ?? s.Venue)
            : static () => { };
        Element actions = spotlight is null
            ? location
            : wide
                ? new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Children =
                    [
                        Button.Accent(Loc.Get(Strings.Concerts.Schedule.ViewEvent), open) with { Shrink = 0f },
                        new BoxEl { Width = 220f, MinWidth = 0f, Children = [ location ] },
                    ],
                }
                : new BoxEl
                {
                    Direction = 1, Gap = Spacing.S,
                    Children = [ Button.Accent(Loc.Get(Strings.Concerts.Schedule.ViewEvent), open), location ],
                };
        blocks.Add(actions);

        var copy = new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.M, Children = blocks.ToArray() };
        return ConcertUi.SplitEditorialHero(schedule.HeaderImage, spotlight?.AccentColor, copy, wide);
    }

    Element TourDates(IReadOnlyList<ConcertMonthGroup> groups, int selected, HashSet<string> nearby,
        string artistName, bool wide, Action<string, string?> go)
    {
        var labels = ConcertScheduleShaping.MonthTabLabels(groups);
        Element selector = ScrollView(
            new BoxEl
            {
                Direction = 0,
                Children = [ SelectorBar.Create(labels, new Signal<int>(selected), onChange: i => _selectedMonth.Value = groups[i].Key) ],
            }, horizontal: true) with
        {
            Height = 48f, Grow = 0f, AutoEdgeFade = true, SuppressScrollBar = true,
            ScrollKey = "artist-schedule-months:" + _artistUri,
        };

        var group = groups[selected];
        string sig = group.Key + ":" + group.Runs.Count + ":" + group.ShowCount + ":" +
            (group.Runs.Count > 0 ? group.Runs[0].Uri : "") + ":" + wide;
        Element month = Embed.Comp(() => new MonthBoard(group, nearby, artistName, go, wide,
            _expandedMonths, _expandedRuns, _monthStateRevision, h => _tracker.PanelHeader = h)) with
        { Key = "dashboard-month:" + sig };

        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Gap = Spacing.S,
                    Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.S),
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Concerts.Schedule.TourDates))
                        // Title (28/36/600) - the page-level heading over the month board, whose own month
                        // headers take the rung below it (Subtitle 20/28). Was an off-ramp 24/650.
                        { Size = 28f, LineHeight = 36f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
                        selector,
                    ],
                },
                month,
            ],
        };
    }

    static ArtistConcertSchedule SeedSchedule(string artistUri, string? artistName) =>
        new(new ArtistRef("", artistUri, artistName ?? ""), HeaderImage: null,
            Concerts: SeedConcerts, Nearby: null, NearbyLocationName: null);

    static readonly IReadOnlyList<Concert> SeedConcerts = BuildSeedConcerts();

    static IReadOnlyList<Concert> BuildSeedConcerts()
    {
        var start = DateTimeOffset.Now.Date.AddHours(20);
        int[] offsets = { 5, 12, 26, 33, 34, 48, 62, 76 };
        var rows = new Concert[offsets.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            bool run = i is 3 or 4;
            rows[i] = new Concert("seed:" + i, "", run ? "Residency venue placeholder" : "Venue name placeholder",
                "City placeholder", new DateTimeOffset(start.AddDays(offsets[i])));
        }
        return rows;
    }
}
