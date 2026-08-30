using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
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

/// <summary>Phase 6 Concert Hub (docs/concert-navigation-hub-geolocation-plan.md): the `concerts` destination. ONE page
/// ScrollEl over accent header + location control → the concepts SelectorBar in a horizontal edge-fade scroller → the
/// feed (Nearby/Recommended shelves, playlist promos, All Events grid, load-more). The saved/inferred location drives
/// every query; location or concept changes bump a query generation that cancels the visible request, resets accumulated
/// pagination, and drops late completions. Navigation only — no playback anywhere.</summary>
sealed class ConcertHubPage : Component
{
    // Hub state lives on the instance (plain signals — reads in Render subscribe): the page is mounted once per
    // KeepAlive slot and the accumulated feed survives park/reactivate.
    readonly Signal<ConcertPlace?> _place = new(null);
    readonly Signal<bool?> _inferred = new(null);
    readonly Signal<IReadOnlyList<ConcertConcept>> _concepts = new(Array.Empty<ConcertConcept>());
    readonly Signal<IReadOnlyList<string>> _selected = new(Array.Empty<string>());   // multi-select concept uris
    readonly Signal<int> _radius = new(100);
    readonly Signal<ConcertWhen> _when = new(ConcertWhen.Any);
    readonly Signal<int?> _matchCount = new(null);                                    // live concertCount preview
    readonly Signal<bool> _appendLoading = new(false);
    // Infinite-scroll proximity gate: true while the scroller's bottom edge is within ~1.5 viewport heights of the
    // content end. Seeded TRUE so a first page shorter than the viewport still fills the screen once; dropped after
    // every append so only a fresh scroll-geometry event continues the chain.
    readonly Signal<bool> _nearTail = new(true);
    // The live page-scroll offset (LazyScroll.Slot) + the accumulated All Events list the virtualized grid reads
    // through its delegates — the grid realizes only the rows in the scroll window no matter how many pages append.
    readonly Signal<float> _pageScroll = new(0f);
    readonly Signal<IReadOnlyList<Concert>> _allEvents = new(Array.Empty<Concert>());
    readonly Signal<int> _locEpoch = new(0);   // bumped after a saved location → re-resolve place + requery
    readonly Loadable<ConcertFeedPage?> _feed = Loadable<ConcertFeedPage?>.Pending(FeedSeed());

    // The query generation (stale-response discipline): location/concept changes bump it and cancel the visible
    // request; every completion posts through a closure that re-checks its captured generation before publishing.
    int _generation;
    CancellationTokenSource? _queryCts;

    readonly ConcertLocationController _location = new();
    Services? _svc;
    ActionServices? _acts;      // resolved per render; read at drag PROMOTION by the promo card's payload factory
    Action<Action> _post = static run => run();
    Ref<NodeHandle> _anchor = null!;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var overlay = UseContext(Overlay.Service);
        // Only the playlist-promo card uses this: it is the one concert surface that stands for a real Wavee resource
        // (concerts, merch and gallery photos have no resource kind, so they are deliberately not drag sources).
        _acts = UseContext(ActionServices.Slot);
        if (svc is null) return new BoxEl { Grow = 1f };

        _svc = svc;
        _post = UsePost();
        _anchor = UseRef<NodeHandle>(default);

        _location.Attach(svc, overlay, _post, onSaved: place =>
        {
            _place.Value = place;
            _inferred.Value = false;
            _locEpoch.Value++;
        });
        UseSignalEffect(_location.TrackQuery);
        // Resolve the saved/inferred place, then load concepts + feed. Re-runs when a save bumps the epoch; the kept
        // concept re-queries against the new place (and clears only if the new concepts list no longer offers it).
        UseSignalEffect(() =>
        {
            _ = _locEpoch.Value;   // subscribe
            var current = _svc;
            if (current is null) return;
            ResolveLocationAndLoad(current);
        });
        UseSignalEffect(() => Reactive.OnCleanup(() => { _queryCts?.Cancel(); _queryCts?.Dispose(); }));

        var place = _place.Value;                  // subscribe → empty-state copy + queries follow the active place

        var region = Skel.Region(
            _feed,
            content: page => FeedBody(page!, svc, go),
            reveal: SkelReveal.Soft,
            onFailed: () => ErrorState.Build(_feed.Error, onRetry: () => RequeryFeed(svc)),
            isEmpty: ConcertHub.IsFeedEmpty,
            onEmpty: () => EmptyState.Build(Loc.Get(Strings.Concerts.EmptyTitle),
                place is null
                    ? Loc.Get(Strings.Concerts.EmptyWithoutLocation)
                    : Loc.Get(Strings.Concerts.EmptyForLocation),
                actionLabel: place is null ? Loc.Get(Strings.Concerts.Location.Set) : null,
                onAction: place is null ? () => _location.TogglePicker(() => _anchor.Value) : null),
            group: "concert-hub");

        var kids = new List<Element>(3)
        {
            Header(),
            Embed.Comp(() => new ConcertFilterBar
            {
                Place = _place,
                Inferred = _inferred,
                RadiusKm = _radius,
                When = _when,
                Concepts = _selected,
                AllConcepts = _concepts,
                MatchCount = _matchCount,
                WhereAnchor = _anchor,
                OnFiltersChanged = () => RequeryFeed(svc),
                OnOpenLocationPicker = () => _location.TogglePicker(() => _anchor.Value),
                OnUseMyLocation = () => _location.TogglePicker(() => _anchor.Value),
            }) with { Key = "concert-filter-bar" },
            region,
        };

        var content = new BoxEl
        {
            Direction = 1, Gap = Spacing.L,
            // The standard desktop page frame: PageWide (36) gutters, the 24 top every other page takes.
            Padding = new Edges4(Spacing.PageWide, BrowseLayout.MastheadReserve + Spacing.XXL, Spacing.PageWide, PlayerDock.Reserve + Spacing.PageWide),
            Children = kids.ToArray(),
        };
        var scroll = ScrollView(content) with
        {
            Grow = 1f, MinHeight = 0f, ScrollKey = "concert-hub",
            OnScrollGeometryChanged = HomeSectionAppendPreloader.NearTailWatch(_nearTail, _pageScroll),
        };
        // Provide the page scroll to the All Events LazyGrid deeper in the body (the LazyVGrid-in-ScrollView wiring).
        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)_pageScroll, scroll);
    }

    // ── header (titles only — the filter bar owns location now) ──────────────────────────────────────────────────────
    Element Header()
    {
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = Spacing.XS,
            Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault,
            // Stroke OR shadow, never both statically: the hairline is the separation model for a docked surface.
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                WaveeType.Eyebrow(Loc.Get(Strings.Concerts.LiveMusic)) with
                {
                    Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                WaveeType.PageHero(Loc.Get(Strings.Concerts.Title)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Body(Loc.Get(Strings.Concerts.Subtitle)) with
                {
                    Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    // ── feed body ────────────────────────────────────────────────────────────────────────────────────────────────────
    Element FeedBody(ConcertFeedPage page, Services svc, Action<string, string?> go)
    {
        var sections = new List<Element>(page.Sections.Count + 1);
        foreach (var section in page.Sections)
        {
            if (section.Concerts.Count > 0)
                sections.Add(section.Kind == ConcertFeedSectionKind.AllEvents
                    ? EventsGrid(section, go)
                    : ConcertShelf(section, go));
            if (section.PlaylistPromotions is { Count: > 0 } promos)
                sections.Add(PromoShelf(section.Key, promos, go));
        }
        if (page.PaginationKey is { } paginationKey)
            sections.Add(Embed.Comp(() => new ConcertAppendPreloader
            {
                PaginationKey = paginationKey,
                Loading = _appendLoading,
                NearTail = _nearTail,
                Start = () => AppendFeed(svc),
            }) with { Key = "concert-append:" + paginationKey });
        return new BoxEl { Direction = 1, Gap = Spacing.XL, MinWidth = 0f, Children = sections.ToArray() };
    }

    // Hub shelves host MANY artists, so the title-primary card family is correct here (unlike the artist schedule).
    Element ConcertShelf(ConcertFeedSection section, Action<string, string?> go)
    {
        var concerts = section.Concerts;
        int n = Math.Min(concerts.Count, 16);
        return PagedShelf.Create(
            n,
            cardAt: (i, w) => ConcertUi.VerticalCard(concerts[i],
                () => go(ConcertRoutes.Detail(concerts[i].Uri), concerts[i].Title ?? concerts[i].Venue)),
            header: SectionCaption(section.Kind == ConcertFeedSectionKind.Nearby
                ? Loc.Get(Strings.Concerts.NearYou)
                : Loc.Get(Strings.Concerts.RecommendedForYou)),
            headerGap: Spacing.S,
            measured: true, keyOf: i => concerts[i].Uri) with { Key = "hub-shelf:" + section.Key };
    }

    // EventTile is uniform-height (every text line is single-line clamped): art = cellW − 16 (the card's side padding),
    // +16 vertical padding, +8 art/text gap, ~62 for caption + title + meta ⇒ cellW + 70; RowGap leaves a real gutter.
    const float GridMinCol = 240f;
    const float EventChrome = 70f;
    const float GridRowGap = 16f;

    Element EventsGrid(ConcertFeedSection section, Action<string, string?> go)
    {
        // The SEED page keeps the plain bounded AutoGrid: Skel derives the pending shimmer from this subtree, and a
        // stateful scroll-windowed component must not mount inside a shimmer derivation (the PagedShelf SkeletonProxy rule).
        if (section.Key.StartsWith("seed-", StringComparison.Ordinal))
        {
            var concerts = section.Concerts;
            var cards = new Element[concerts.Count];
            for (int i = 0; i < concerts.Count; i++)
            {
                var c = concerts[i];
                cards[i] = ConcertUi.VerticalCard(c, () => go(ConcertRoutes.Detail(c.Uri), c.Title ?? c.Venue));
            }
            return new BoxEl
            {
                Key = "hub-grid:" + section.Key,
                Direction = 1, Gap = Spacing.S,
                Children = [ SectionCaption(Loc.Get(Strings.Concerts.AllEvents)), AutoGrid(GridMinCol, Spacing.M, float.NaN, cards) ],
            };
        }

        // The real grid is data-virtualized: the LazyGrid reserves the full extent and realizes only the scroll
        // window, so appended pages grow the COUNT (read live through the delegate), never the realized tree. The
        // nav action is captured once at mount — NavCtx is stable for the page's lifetime.
        return new BoxEl
        {
            Key = "hub-grid:all-events",
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                SectionCaption(Loc.Get(Strings.Concerts.AllEvents)),
                Embed.Comp(() => new LazyGrid(
                    count: () => _allEvents.Value.Count,
                    cell: (i, w) => EventCell(i, go),
                    ensureRange: static (_, _) => { },   // appends land whole in memory; paging is the tail preloader's job
                    minColWidth: GridMinCol, gap: Spacing.M, rowExtra: EventChrome + GridRowGap, overscanRows: 3))
                    with { Key = "hub-grid-lazy" },
            ],
        };
    }

    Element EventCell(int index, Action<string, string?> go)
    {
        var list = _allEvents.Peek();
        if ((uint)index >= (uint)list.Count) return new BoxEl();   // count/window skew during an append reconcile
        var c = list[index];
        return ConcertUi.VerticalCard(c, () => go(ConcertRoutes.Detail(c.Uri), c.Title ?? c.Venue));
    }

    Element PromoShelf(string sectionKey, IReadOnlyList<PlaylistRef> promos, Action<string, string?> go)
    {
        var acts = _acts;
        int n = Math.Min(promos.Count, 16);
        return PagedShelf.Create(
            n,
            cardAt: (i, w) => PlaylistPromoCard(promos[i],
                () => go("pl:" + promos[i].Uri, promos[i].Name), w, acts),
            header: SectionCaption(Loc.Get(Strings.Concerts.PlaylistsForScene)),
            headerGap: Spacing.S,
            measured: true, keyOf: i => promos[i].Uri) with { Key = "hub-promos:" + sectionKey };
    }

    // The promo card keeps the existing playlist-card look (cover + title + source) WITHOUT the play FAB: every
    // MediaCard variant hard-mounts NowPlayingOverlay, and no concert surface may wire playback — navigation only.
    static Element PlaylistPromoCard(PlaylistRef playlist, Action onClick, float cardW, ActionServices? acts)
    {
        float inner = MathF.Max(48f, cardW - 2f * Spacing.S);
        return new BoxEl
        {
            Key = playlist.Uri,
            Direction = 1, Gap = Spacing.S, Grow = 1f, ClipToBounds = true,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand, OnClick = onClick,
            // The promo card IS a playlist: drag it to the sidebar to pin or file it, onto another playlist to copy
            // its tracks. (Concerts / merch / gallery photos stay non-draggable — they have no resource kind.)
            Draggable = Drag.Source(WaveeDragKinds.Resource,
                () => WaveeResourceDragPayload.ForEntity(WaveeResourceKind.Playlist, playlist.Uri, playlist.Name,
                                                         playlist.Cover, acts)),
            Children =
            [
                Surfaces.Artwork(playlist.Cover, playlist.Uri.GetHashCode() & 0x7fffffff, inner, inner,
                    Radii.Control, decodePx: 256),
                WaveeType.TrackTitle(playlist.Name) with
                {
                    Width = inner, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                },
                WaveeType.TrackMeta(playlist.Subtitle) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    static Element SectionCaption(string label) => WaveeType.Eyebrow(label) with
    {
        Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    // ── query orchestration (generation-guarded) ─────────────────────────────────────────────────────────────────────

    /// <summary>Start a new query generation: cancel the visible request and hand back its fresh token.</summary>
    CancellationToken NextGeneration(out int gen)
    {
        gen = ++_generation;
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = new CancellationTokenSource();
        return _queryCts.Token;
    }

    // Location changed (mount / saved place): resolve the saved+inferred place, then reload concepts and the feed with
    // the KEPT concept. Runs on the UI thread up to the first await; publishes through post with a generation guard.
    async void ResolveLocationAndLoad(Services svc)
    {
        var post = _post;
        var ct = NextGeneration(out int gen);
        _feed.SetPending(FeedSeed());
        _appendLoading.Value = false;

        ConcertPlace? place = null;
        bool? inferred = null;
        try { place = await svc.Concerts.GetUserLocationAsync(ct).ConfigureAwait(false); }
        catch { /* unresolved location: the hub still renders with the location prompt prominent */ }
        try { if (place is not null) inferred = await svc.Concerts.IsUserLocationInferredAsync(ct).ConfigureAwait(false); }
        catch { /* approximation marker is best-effort */ }

        post(() =>
        {
            if (gen != _generation) return;   // superseded by a newer location/concept change
            _place.Value = place;
            _inferred.Value = inferred;
            FetchConcepts(svc, place?.GeoHash, gen, ct);
            FetchFeed(svc, gen, ct, paginationKey: null, append: false);
            FetchCount(svc, gen, ct);
        });
    }

    // A filter edit (location / radius / when / genres) or Retry: requery the feed AND re-preview the count for the
    // current filter tuple from page one, under one bumped generation.
    void RequeryFeed(Services svc)
    {
        var ct = NextGeneration(out int gen);
        _feed.SetPending(FeedSeed());
        _appendLoading.Value = false;
        FetchFeed(svc, gen, ct, paginationKey: null, append: false);
        FetchCount(svc, gen, ct);
    }

    async void FetchFeed(Services svc, int gen, CancellationToken ct, string? paginationKey, bool append)
    {
        var post = _post;
        try
        {
            var query = new ConcertFeedQuery(_place.Peek(), SelectedOrNull(), _radius.Peek(), _when.Peek().Range, paginationKey);
            var page = await svc.Concerts.GetFeedAsync(query, ct).ConfigureAwait(false);
            post(() =>
            {
                if (gen != _generation) return;   // a location/concept change reset pagination — drop the late page
                if (append)
                {
                    _appendLoading.Value = false;
                    // Disarm until the next scroll-geometry event re-confirms the user is still heading down —
                    // this is what stops the tail from chain-loading the whole feed while the page sits still.
                    _nearTail.Value = false;
                    var current = _feed.Value.Peek();
                    var merged = current is null ? page : page is null ? current : current.Append(page);
                    _allEvents.Value = AllEventsOf(merged);
                    _feed.SetReady(merged);
                }
                else
                {
                    _allEvents.Value = AllEventsOf(page);
                    _feed.SetReady(page);
                }
            });
        }
        catch (OperationCanceledException) { /* generation superseded / page unmounted */ }
        catch (Exception ex)
        {
            post(() =>
            {
                if (gen != _generation) return;
                if (append) _appendLoading.Value = false;   // quiet append failure: the button re-arms for a retry
                else _feed.SetFailed(ex);
            });
        }
    }

    // The live concertCount preview: debounced 250ms behind a filter edit, under the SAME generation guard as the feed
    // (a newer edit cancels the delay + drops the late count). Runs the count query WITHOUT pagination; the feed itself
    // only refetches on committed changes while the count previews every edit.
    async void FetchCount(Services svc, int gen, CancellationToken ct)
    {
        var post = _post;
        try
        {
            await System.Threading.Tasks.Task.Delay(250, ct).ConfigureAwait(false);
            var query = new ConcertFeedQuery(_place.Peek(), SelectedOrNull(), _radius.Peek(), _when.Peek().Range);
            int? count = await svc.Concerts.GetFeedCountAsync(query, ct).ConfigureAwait(false);
            post(() => { if (gen == _generation) _matchCount.Value = count; });
        }
        catch (OperationCanceledException) { /* generation superseded / page unmounted */ }
        catch
        {
            post(() => { if (gen == _generation) _matchCount.Value = null; });
        }
    }

    async void FetchConcepts(Services svc, string? geoHash, int gen, CancellationToken ct)
    {
        var post = _post;
        if (string.IsNullOrWhiteSpace(geoHash))
        {
            // No geohash → no concepts call; the strip shows only "All" and any stale selection clears via reconcile.
            post(() => { if (gen == _generation) PublishConcepts(svc, Array.Empty<ConcertConcept>()); });
            return;
        }
        try
        {
            string? bias = SelectedOrNull() is { Count: > 0 } s ? s[0] : null;
            var list = await svc.Concerts.GetConceptsAsync(geoHash!, bias, ct).ConfigureAwait(false);
            post(() => { if (gen == _generation) PublishConcepts(svc, list); });
        }
        catch (OperationCanceledException) { /* generation superseded / page unmounted */ }
        catch
        {
            post(() => { if (gen == _generation) PublishConcepts(svc, Array.Empty<ConcertConcept>()); });
        }
    }

    // Publish the concepts for the active generation, then reconcile the multi-selection: a selected concept survives a
    // location change only while still offered — dropping any that vanished AND requerying the visible feed (which was
    // queried with them) when the set actually shrinks.
    void PublishConcepts(Services svc, IReadOnlyList<ConcertConcept> concepts)
    {
        _concepts.Value = concepts;
        var current = _selected.Peek();
        if (current.Count == 0) return;

        var kept = new List<string>(current.Count);
        foreach (var uri in current)
            foreach (var concept in concepts)
                if (string.Equals(concept.Uri, uri, StringComparison.Ordinal)) { kept.Add(uri); break; }

        if (kept.Count != current.Count)
        {
            _selected.Value = kept;
            RequeryFeed(svc);
        }
    }

    IReadOnlyList<string>? SelectedOrNull() => _selected.Peek() is { Count: > 0 } c ? c : null;

    void AppendFeed(Services svc)
    {
        if (_appendLoading.Peek()) return;
        string? key = _feed.Value.Peek()?.PaginationKey;
        if (key is null || _queryCts is null) return;
        _appendLoading.Value = true;
        // Same generation + token as the visible query: the append belongs to the current place/concept, and a
        // location/concept change cancels + drops it with everything else.
        FetchFeed(svc, _generation, _queryCts.Token, key, append: true);
    }

    /// <summary>The merged page's All Events list — the ONE list the virtualized grid reads through its delegates.</summary>
    static IReadOnlyList<Concert> AllEventsOf(ConcertFeedPage? page)
    {
        if (page is null) return Array.Empty<Concert>();
        foreach (var section in page.Sections)
            if (section.Kind == ConcertFeedSectionKind.AllEvents)
                return section.Concerts;
        return Array.Empty<Concert>();
    }

    // Representative pending shape (one shelf + one grid section). The engine repaints all text/media, so none of this
    // placeholder content is shown — it only gives Skel.Region a real subtree to derive the shimmer from.
    static ConcertFeedPage FeedSeed()
    {
        var start = new DateTimeOffset(2025, 1, 1, 20, 0, 0, TimeSpan.Zero);
        var nearby = new Concert[4];
        for (int i = 0; i < nearby.Length; i++)
            nearby[i] = new Concert("seed:n:" + i, "Concert placeholder", "Venue placeholder", "City placeholder",
                start.AddDays(7 * i));
        var all = new Concert[6];
        for (int i = 0; i < all.Length; i++)
            all[i] = new Concert("seed:a:" + i, "Concert placeholder", "Venue placeholder", "City placeholder",
                start.AddDays(10 * i));
        return new ConcertFeedPage(
        [
            new ConcertFeedSection("seed-nearby", ConcertFeedSectionKind.Nearby, nearby),
            new ConcertFeedSection("seed-all", ConcertFeedSectionKind.AllEvents, all),
        ]);
    }
}
