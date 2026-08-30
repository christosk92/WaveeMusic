using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The pinned Concert-Hub filter card (concerts v2, rev-7). One horizontally-scrolling row of three groups
/// separated by thin dividers — <c>[📍 where ▾] │ [📅 when] │ [✓All top-3 genres +N]</c> — under a caption row that
/// pairs the "FILTER BY" eyebrow with the live <see cref="CountTicker"/>. The bar owns NO queries: every edit writes one
/// of the page's signals and fires <see cref="OnFiltersChanged"/>, which funnels into the page's one RequeryFeed
/// generation. Props are frozen at mount — live data flows through the injected signals, read in <see cref="Render"/>.
/// Navigation/flyouts only; no playback anywhere.</summary>
sealed class ConcertFilterBar : Component
{
    // Page-owned live inputs (signals — stable identities, read here to subscribe).
    public required Signal<ConcertPlace?> Place;
    public required Signal<bool?> Inferred;
    public required Signal<int> RadiusKm;
    public required Signal<ConcertWhen> When;
    public required Signal<IReadOnlyList<string>> Concepts;                 // selected concept uris (multi-select)
    public required Signal<IReadOnlyList<ConcertConcept>> AllConcepts;      // the provider's weighted concept list
    public required Signal<int?> MatchCount;
    // The where pill publishes its node here so the page's existing location picker anchors at it (the _anchor idiom).
    public required Ref<NodeHandle> WhereAnchor;
    // Callbacks the page wires: requery the feed, open the existing city picker, request OS location.
    public required Action OnFiltersChanged;
    public required Action OnOpenLocationPicker;
    public required Action OnUseMyLocation;

    static readonly int[] DefaultRadiusKm = { 100 };   // the only "no radius chip" value (label hides " · N km")

    readonly Signal<bool> _expanded = new(false);      // genre strip: capped head vs the full list

    IOverlayService? _overlay;
    OverlayHandle? _whenHandle;
    OverlayHandle? _whereHandle;
    Ref<NodeHandle> _whenAnchor = null!;

    public override Element Render()
    {
        _overlay = UseContext(Overlay.Service);
        _whenAnchor = UseRef<NodeHandle>(default);

        var place = Place.Value;                        // subscribe → where label
        int radius = RadiusKm.Value;                    // subscribe → where label + radius flyout check
        var when = When.Value;                          // subscribe → rest/fused when-area swap
        var selected = Concepts.Value;                  // subscribe → token selection
        var all = AllConcepts.Value;                    // subscribe → token strip
        bool expanded = _expanded.Value;                // subscribe → +N / Show less
        var culture = CultureInfo.CurrentCulture;

        Element caption = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = Spacing.S,
            Children =
            [
                WaveeType.Eyebrow(Loc.Get(Strings.Concerts.Filter.FilterBy)) with
                {
                    Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                new BoxEl { Grow = 1f },
                Embed.Comp(() => new CountTicker
                {
                    Target = MatchCount,
                    Suffix = Loc.Get(Strings.Concerts.Filter.Events),
                }) with { Key = "concert-count" },
            ],
        };

        Element wherePill = ConcertUi.WherePill(WhereLabel(place, radius), OpenWhereFlyout)
            with { OnRealized = h => WhereAnchor.Value = h };

        Element row = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Children = [ wherePill, Divider(), WhenArea(when, culture), Divider(), Genres(all, selected, expanded) ],
        };

        // One line, riding the page's horizontal edge-fade viewport (the ConceptsRow pattern).
        var scroller = ScrollView(new BoxEl { Direction = 0, Children = [ row ] }, horizontal: true) with
        {
            Grow = 0f, Height = 44f, AutoEdgeFade = true, SuppressScrollBar = true, ScrollKey = "concert-hub-filter",
        };

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillLayerDefault,
            // Stroke OR shadow, never both statically (the pinned bar is a docked surface, not a floating card).
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Children = [ caption, scroller ],
        }.Sticky();
    }

    // ── where ────────────────────────────────────────────────────────────────────────────────────────────────────────
    string WhereLabel(ConcertPlace? place, int radius)
    {
        string name = place is null || string.IsNullOrWhiteSpace(place.Name)
            ? Loc.Get(Strings.Concerts.Location.Set)
            : place.Name;
        return radius != DefaultRadiusKm[0] ? name + " · " + radius + " km" : name;
    }

    // ── when ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    Element WhenArea(ConcertWhen when, CultureInfo culture)
    {
        if (when.Kind == ConcertWhenKind.Any)
        {
            BoxEl pill = ConcertUi.RestDatePill(OpenWhenFlyout) with { OnRealized = h => _whenAnchor.Value = h };
            // The chip's Exit leg flies it INTO the pill (left) as the fused segment enters from the right.
            Element chip = ConcertUi.FilterToken(Loc.Get(Strings.Concerts.Filter.ThisWeekend), false, PickThisWeekend) with
            {
                Key = "when-chip",
                Animate = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
                    TransitionDynamics.Tween(220f, Easing.FluentAccelerate),
                    Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true)),
            };
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = [ pill, chip ] };
        }

        BoxEl fused = ConcertUi.SegmentedDatePill(when.Name, ConcertHub.WhenLabel(when.Range!, culture), OpenWhenFlyout)
            with { OnRealized = h => _whenAnchor.Value = h };
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = [ fused ] };
    }

    void PickThisWeekend()
    {
        var now = DateTimeOffset.Now;
        When.Value = new ConcertWhen(ConcertWhenKind.ThisWeekend, Loc.Get(Strings.Concerts.Filter.ThisWeekend),
            ConcertHub.PresetRange(ConcertWhenKind.ThisWeekend, now));
        OnFiltersChanged();
    }

    // ── genres ───────────────────────────────────────────────────────────────────────────────────────────────────────
    Element Genres(IReadOnlyList<ConcertConcept> all, IReadOnlyList<string> selected, bool expanded)
    {
        var kids = new List<Element>(all.Count + 3)
        {
            ConcertUi.FilterToken(Loc.Get(Strings.Concerts.AllGenres), selected.Count == 0, ClearGenres),
        };
        var shown = ConcertHub.TopConcepts(all, selected, expanded);
        foreach (var concept in shown)
        {
            string uri = concept.Uri;
            bool active = selected.Contains(uri, StringComparer.Ordinal);
            kids.Add(ConcertUi.FilterToken(ConcertHub.ConceptLabel(concept.Name, Loc.Get(Strings.Concerts.GenreFallback)),
                active, () => ToggleGenre(uri)));
        }
        // The expand/collapse affordance only exists when there is a head to grow past.
        if (all.Count > shown.Count)
            kids.Add(ConcertUi.MoreToken(Strings.Concerts.Filter.MoreGenres(all.Count - shown.Count), false,
                () => _expanded.Value = true));
        else if (expanded && all.Count > 3)
            kids.Add(ConcertUi.MoreToken(Loc.Get(Strings.Concerts.Filter.ShowLess), true, () => _expanded.Value = false));

        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = kids.ToArray() };
    }

    void ClearGenres()
    {
        if (Concepts.Peek().Count == 0) return;
        Concepts.Value = Array.Empty<string>();
        OnFiltersChanged();
    }

    void ToggleGenre(string uri)
    {
        Concepts.Value = ConcertHub.ToggleConcept(Concepts.Peek(), uri);
        OnFiltersChanged();
    }

    // ── flyouts (the ConcertLocationController anchored-overlay mechanics) ────────────────────────────────────────────
    void OpenWhenFlyout()
    {
        if (_overlay is not { } overlay) return;
        if (_whenHandle is { IsOpen: true } open) { open.Close(); return; }
        _whenHandle = overlay.Open(
            () => _whenAnchor.Value,
            () => Embed.Comp(() => new ConcertDateFlyout
            {
                Now = DateTimeOffset.Now,
                Culture = CultureInfo.CurrentCulture,
                OnPick = PickWhen,
            }),
            FlyoutPlacement.BottomEdgeAlignedLeft,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            {
                ConstrainToRootBounds = true,
            });
        _whenHandle.ClosedAction = () => _whenHandle = null;
    }

    void PickWhen(ConcertWhen when)
    {
        _whenHandle?.Close();
        When.Value = when;
        OnFiltersChanged();
    }

    void OpenWhereFlyout()
    {
        if (_overlay is not { } overlay) return;
        if (_whereHandle is { IsOpen: true } open) { open.Close(); return; }
        _whereHandle = overlay.Open(
            () => WhereAnchor.Value,
            () => Embed.Comp(() => new ConcertWhereFlyout
            {
                Radius = RadiusKm,
                OnRadius = PickRadius,
                OnSearchCities = SearchCities,
                OnUseMyLocation = UseMyLocation,
            }),
            FlyoutPlacement.BottomEdgeAlignedLeft,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            {
                ConstrainToRootBounds = true,
            });
        _whereHandle.ClosedAction = () => _whereHandle = null;
    }

    void PickRadius(int km)
    {
        if (RadiusKm.Peek() == km) return;
        RadiusKm.Value = km;   // the where flyout stays open + re-reads Radius (check moves); the bar re-labels the pill
        OnFiltersChanged();
    }

    void SearchCities()
    {
        _whereHandle?.Close();
        OnOpenLocationPicker();
    }

    void UseMyLocation()
    {
        _whereHandle?.Close();
        OnUseMyLocation();
    }

    static Element Divider() => new BoxEl { Width = 1f, Height = 20f, Shrink = 0f, Fill = Tok.StrokeSurfaceDefault };
}
