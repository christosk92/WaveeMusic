using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Features.Browse;

/// <summary>The Browse landing surface: an eyebrow, a title, and every category grouped into bands (Top / Charts /
/// For you / Genres / Mood &amp; activity / More) — but no longer at ONE density. Each band's CELL now encodes how
/// much weight its destinations carry, cheapest to most expressive: Top's four entry points (Music / Podcasts /
/// Audiobooks / Live Events) need no further identity than their own name set large, so they get bare Display type
/// on the mica ground; For you and Genres are one step down, a colour pip beside a name or a link; Mood &amp;
/// activity earns a filled colour bar because a mood IS its colour rather than a label pointing at one; and Charts
/// earns a full card — the shared Fold deck, stacked covers and a live item count — because a chart is CONTENT (a
/// ranked, refreshing playlist) and not a category. More, the long tail Spotify has not curated into a band yet,
/// gets that same card weight rather than being demoted for being unsorted. Density is what this page spends to say
/// "here is how much this destination is"; the reader learns the hierarchy from weight, not from column position.
///
/// Rendered as the <c>browse</c> keep-alive page (<see cref="BrowseDirectoryPage"/>). Search is a separate
/// results page keyed by committed query.</summary>
sealed class BrowseDirectory : Component
{
    internal sealed record Model(Action<string, string> OnOpenCategory, Action<string> OnOpenFeature);
    internal static readonly Context<Model?> Props = new(null);

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var model = UseContext(Props);
        var go = UseContext(HistoryStore.NavCtx);
        var sectionPreview = UseContext(HomeSectionPreviewStore.Slot);
        var navPreview = UseContext(NavPreviewStore.Slot);
        var cache = UseContext(BrowseDirectoryStore.Slot);

        // One fetch per mount, on the engine's own loader. The service caches on a 6h TTL, so re-entering Browse inside
        // a session is instant and this never becomes a per-keystroke cost when the user clears the search box.
        //
        // The hand-rolled `_loading` signal this replaces could strand the page on its skeleton: it was flipped only
        // from inside an async continuation, so any path that did not reach that continuation left the shimmer up with
        // nothing to explain it. Skel.Region owns Pending / Ready / Empty / Failed, so "stuck loading" stops being a
        // state this component can express.
        //
        // T11: the shell's KeepAlive holds only 8 slots, so a long browsing session can still evict the directory
        // before the user returns — cold-remounting this component. `cache` (BrowseDirectoryStore,
        // Context-provided at the shell root) survives that eviction: the seed below is the LAST loaded value when the
        // store has one (full-height content immediately, so the ScrollView's keyed offset restores against real
        // layout instead of a skeleton), and LoadCategoriesAsync below serves fresh-cached/stale-cached/empty-cached
        // with three different network policies (see its own comment).
        var cachedCats = cache?.Categories;
        var cats = UseResource(
            async ct => svc is null
                ? Array.Empty<BrowseCategory>()
                : await LoadCategoriesAsync(svc, cache, ct).ConfigureAwait(false),
            seed: cachedCats ?? Array.Empty<BrowseCategory>(),
            deps: svc is null ? 0 : 1);

        // Charts is chrome, not a taxonomy band — the SAME ChartSections.All deck Home's Charts row fetches.
        // Independent of `cats`/browseAll so a renamed browseAll category never blanks the card, and a Featured
        // outage never blanks the REST of the directory.
        var browseSvc = svc?.Browse;
        var cachedCharts = cache?.Charts;
        var charts = UseResource<IReadOnlyList<HomeSection>>(
            async ct => browseSvc is null
                ? Array.Empty<HomeSection>()
                : await LoadChartsAsync(browseSvc, cache, ct).ConfigureAwait(false),
            seed: cachedCharts ?? HomeBrowseCards.ChartDeckSeed, deps: DepKey.From(browseSvc is null ? 0 : 1));

        void OpenBrowseSection(HomeSection s) =>
            HomeCardNav.OpenBrowseSection(s, navPreview, sectionPreview, go,
                uri => { if (svc is not null) _ = svc.Player.PlayTrackAsync(uri); });

        // SkelReveal.None, not the default Soft: the CONTENT owns its entrance here (the title + each band cascade in
        // through WaveeEntrance below), and a block-level blur-reveal on top of that would fade the whole directory in
        // as one slab while its bands were still arriving — two entrances for one mount. Same entrance-vs-reveal split
        // SearchPage documents for its bound Songs list.
        // smoothResize:false for the same reason the search facet body (SearchPage.cs) sets it: this is a PAGE-level
        // region whose two branches differ by hundreds of DIP, not a section whose height nudges by a row. Easing that
        // makes the region clip its own directory into a strip that grows. WaveeEntrance below owns the entrance.
        return Skel.Region(cats.Loadable, Skeleton, c => Body(c, model, index => ChartsBand(charts, OpenBrowseSection, index, model)),
            reveal: SkelReveal.None, smoothResize: false,
            isEmpty: c => c.Count == 0,
            onEmpty: () => EmptyState.Build(Loc.Get(Strings.Browse.Unavailable)),
            onFailed: () => ErrorState.Build(cats.Loadable.Error, onRetry: cats.Refresh));
    }

    // `chartsBandAt` builds the Charts band FOR a given entrance index — supplied by the caller so this one Body
    // drives both the loaded page (Render, above — a live Skel.Region over the `charts` resource) and the loading
    // shape (Skeleton, below — a static render off ChartDeckSeed, no resource, no hooks to hold one).
    static Element Body(IReadOnlyList<BrowseCategory> categories, Model? model, Func<int, Element> chartsBandAt)
    {
        var groups = BrowseTaxonomy.Grouped(categories);
        // The directory is EAGER and mounts exactly once per BrowseDirectoryPage mount (no virtualization anywhere in it), and
        // its band count is fixed by the taxonomy (plus the always-present Charts band) — the two conditions
        // WaveeEntrance requires. So the title lands first and the bands follow it 40ms apart, which is the whole Zune
        // "the page assembles itself" moment on the surface a user sees the instant they open Browse.
        var children = new List<Element>(groups.Count + 2);

        // G2c-B: the masthead itself moved OUT — the shell's ShellMastheadBand renders "Browse" above the KeepAlive
        // swap now (Browse home is a family route with a route-derived title arm), so this wrapper no longer
        // carries a masthead child. `band` still starts at 1, not 0: that offset was never about the masthead
        // CHILD occupying a cascade slot (BrowseMasthead owns its own entrance timing, never WaveeEntrance — see
        // the old comment this replaced) — it is the first real band's OWN entrance delay, unrelated to this list's
        // shape, so leaving it at 1 keeps that band's timing byte-identical to before this task.
        int band = 1;
        IReadOnlyList<BrowseCategory>? Band(BrowseGroup g)
        {
            foreach (var (group, items) in groups)
                if (group == g) return items;
            return null;
        }

        // Prototype browseHtml(): top → charts → for you → genres → mood → more, walked off BrowseTaxonomy.BandOrder
        // (THE one spelling of that sequence). Charts is chrome (chartsBandAt), not a browseAll tile band — always
        // injected in its band-order slot even when Grouped omitted a Charts category.
        foreach (var g in BrowseTaxonomy.BandOrder)
        {
            if (g == BrowseGroup.Charts) { children.Add(chartsBandAt(band++)); continue; }
            if (Band(g) is { Count: > 0 } items) children.Add(BandOf(g, items, model, band++));
        }

        return new BoxEl
        {
            // The HOST owns the frame (BrowseDirectoryPage's own Padding; the shell's ShellMastheadBand owns the
            // masthead itself now — G2c-B) — this body is gutterless.
            Direction = 1, Gap = Spacing.L, MinWidth = 0f,
            Children = children.ToArray(),
        };
    }

    // T11 cache policy, mirrored across categories and charts (LoadCategoriesAsync / LoadChartsAsync below) — three
    // paths, one per BrowseDirectoryStore state:
    //   FRESH (a cached value younger than the store's TTL): return it — no network call at all.
    //   STALE (a cached value past the TTL): return the stale value immediately, so THIS mount paints now instead of
    //          a skeleton, and fire a DETACHED background refresh that fetches + writes the store for the NEXT mount
    //          to read. The refresh never touches this mount's own resource/ct — a background refresh must not flip
    //          an already-visible page back to a skeleton or ErrorState on failure, so its own failure is logged and
    //          swallowed (see RefreshCategoriesAsync / RefreshChartsAsync), never rethrown here.
    //   EMPTY (no cached value — first load of the session): fetch exactly as before and write the store on success.
    //          A first-load failure with no cache to fall back on must still fail LOUDLY into the retry ErrorState,
    //          so this path keeps the existing rethrow-not-swallow contract untouched.
    static async System.Threading.Tasks.Task<IReadOnlyList<BrowseCategory>> LoadCategoriesAsync(
        Services svc, BrowseDirectoryStore? cache, System.Threading.CancellationToken ct)
    {
        var cached = cache?.Categories;
        if (cached is not null)
        {
            if (BrowseDirectoryStore.IsFresh(cache!.CategoriesAtMs)) return cached;
            _ = RefreshCategoriesAsync(svc, cache);   // detached — see policy comment above
            return cached;
        }
        var fresh = await LoadAsync(svc, ct).ConfigureAwait(false);
        cache?.SetCategories(fresh);
        return fresh;
    }

    static async System.Threading.Tasks.Task RefreshCategoriesAsync(Services svc, BrowseDirectoryStore cache)
    {
        try
        {
            // CancellationToken.None: this task outlives the mount that fired it (that is the whole point — the NEXT
            // mount reads whatever it writes), so it must not inherit this mount's resource ct.
            var fresh = await svc.Browse.GetCategoriesAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
            cache.SetCategories(fresh);
        }
        catch (Exception ex)
        {
            svc.Log?.Event(WaveeLogLevel.Warning, "browse", "browse.directory.refresh.fail",
                "browse directory background refresh failed", ex: ex);
        }
    }

    static async System.Threading.Tasks.Task<IReadOnlyList<BrowseCategory>> LoadAsync(Services svc, System.Threading.CancellationToken ct)
    {
        try
        {
            return await svc.Browse.GetCategoriesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }   // cancellation is the resource's own concern, not an empty result
        catch (Exception ex)
        {
            // The SERVICE logs transport failures; this also catches a mapper/grouping throw, which it does not see.
            // An unreachable browse is a FAILURE the user can retry, never a silently empty directory that reads
            // identical to a genuinely empty one — so log for diagnostics, then RETHROW: UseResource's Loadable flips
            // to Failed and Skel.Region paints ErrorState instead of "Browse unavailable".
            svc.Log?.Event(WaveeLogLevel.Warning, "browse", "browse.directory.fail",
                "browse directory load failed", ex: ex);
            throw;
        }
    }

    // Same three-path policy as LoadCategoriesAsync, over the Charts deck. No local try/catch on the empty path here
    // either — mirrors HomeBrowseCards.LoadChartDeckAsync's existing contract (a null Featured section already
    // throws; UseResource's Loadable surfaces it as Failed, same as before this store existed).
    static async System.Threading.Tasks.Task<IReadOnlyList<HomeSection>> LoadChartsAsync(
        IBrowseService browseSvc, BrowseDirectoryStore? cache, System.Threading.CancellationToken ct)
    {
        var cached = cache?.Charts;
        if (cached is not null)
        {
            if (BrowseDirectoryStore.IsFresh(cache!.ChartsAtMs)) return cached;
            _ = RefreshChartsAsync(browseSvc, cache);   // detached — see LoadCategoriesAsync's policy comment
            return cached;
        }
        var fresh = await HomeBrowseCards.LoadChartDeckAsync(browseSvc, ct).ConfigureAwait(false);
        cache?.SetCharts(fresh);
        return fresh;
    }

    static async System.Threading.Tasks.Task RefreshChartsAsync(IBrowseService browseSvc, BrowseDirectoryStore cache)
    {
        try
        {
            var fresh = await HomeBrowseCards.LoadChartDeckAsync(browseSvc, System.Threading.CancellationToken.None).ConfigureAwait(false);
            cache.SetCharts(fresh);
        }
        catch (Exception)
        {
            // No `svc.Log` here (this overload only carries IBrowseService, matching HomeBrowseCards.LoadChartDeckAsync's
            // own signature) — a failed background refresh is not otherwise actionable, so it is swallowed silently,
            // same as this deck's existing revalidation failures under UseResource's own SWR (see ResourceCell.Settle).
        }
    }

    // One band: a module header over the density this band's destinations earn (see the class doc-comment).
    // `index` is the band's position in the entrance cascade (see Body) — the ONLY thing it is used for.
    static Element BandOf(BrowseGroup group, IReadOnlyList<BrowseCategory> items, Model? model, int index)
        => new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Animate = WaveeEntrance.Row(index),
            Children =
            [
                BandLabel(GroupLabel(group)),
                group switch
                {
                    BrowseGroup.Top => WrapRow(items, model, BrowseTiles.Word, BrowseLayout.ChipGap),
                    BrowseGroup.ForYou => WrapRow(items, model, BrowseTiles.Name, BrowseLayout.ChipGap),
                    BrowseGroup.Genres => Responsive.Of(width => LinkGrid(items, model, width), fallback: BrowseLayout.DirectoryFallbackWidth),
                    BrowseGroup.MoodActivity => Responsive.Of(width => BarGrid(items, model, width), fallback: BrowseLayout.DirectoryFallbackWidth),
                    BrowseGroup.More => Responsive.Of(width => MoreGrid(items, model, width), fallback: BrowseLayout.DirectoryFallbackWidth),
                    _ => throw new InvalidOperationException("BandOf is never asked for Charts — see Body/ChartsBand."),
                },
            ],
        };

    // The Charts band: the SAME shared Fold deck Home's own Charts row renders, off the SAME Featured resource — the
    // deck supplies its own header (title + drill-in chevron via HomeModules.ModuleHeader), so unlike every other band
    // this one carries no separate BandLabel eyebrow; a second "Charts" caption above it would just repeat the deck's
    // own title.
    static Element ChartsBand(Resource<IReadOnlyList<HomeSection>> charts, Action<HomeSection> openBrowseSection,
        int index, Model? model)
        => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Animate = WaveeEntrance.Row(index),
            Children =
            [
                Skel.Region(charts.Loadable,
                    content: list => HomeModules.FoldDeck(list, Loc.Get(Strings.Browse.Charts), openBrowseSection,
                        openHeader: model is null ? null : () => model.OnOpenCategory(ChartPages.Charts, Loc.Get(Strings.Home.Charts))),
                    isEmpty: list => list.Count == 0,
                    onEmpty: () => EmptyState.Compact(Loc.Get(Strings.Home.ChartsEmpty)),
                    onFailed: () => ErrorState.Build(charts.Loadable.Error, onRetry: charts.Refresh),
                    reveal: SkelReveal.None, smoothResize: false),
            ],
        };

    // Top / For you: a wrapping row of bare words or pip+name cells — no grid, no fixed column count, just the
    // density-appropriate cell wrapping at its own natural width.
    static Element WrapRow(IReadOnlyList<BrowseCategory> items, Model? model, Func<BrowseTileModel, Element> cell, float gap)
    {
        var cells = new Element[items.Count];
        for (int i = 0; i < items.Count; i++) cells[i] = cell(ToModel(items[i], model));
        return new BoxEl { Direction = 0, Gap = gap, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = cells };
    }

    // Delegates to BrowseTiles.LinkGrid — the SAME grid BrowsePage.CategoryBlock renders, one column/cell
    // implementation instead of two. NOTE: this mints BrowseTiles.LinkGrid's OWN key ("browse-link-grid:" + cols)
    // rather than this band's former "browse-genres-grid:" + cols — a one-time remount of the Genres band's grid on
    // first run after this change, never again after that (T9 dedup; BrowseTiles.LinkGrid's key stays the stable one).
    static Element LinkGrid(IReadOnlyList<BrowseCategory> items, Model? model, float width)
    {
        var tiles = new BrowseTileModel[items.Count];
        for (int i = 0; i < tiles.Length; i++) tiles[i] = ToModel(items[i], model);
        return BrowseTiles.LinkGrid(tiles, width);
    }

    static Element BarGrid(IReadOnlyList<BrowseCategory> items, Model? model, float width)
    {
        int cols = BrowseLayout.BarColumns(width > 0f ? width : BrowseLayout.DirectoryFallbackWidth);
        var cells = new Element[items.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = BrowseTiles.Bar(ToModel(items[i], model));
        return BrowseLayout.StarGrid(cols, Spacing.XS, Spacing.XS, cells) with { Key = "browse-mood-grid:" + cols };
    }

    static Element MoreGrid(IReadOnlyList<BrowseCategory> items, Model? model, float width)
    {
        int cols = BrowseLayout.MoreColumns(width > 0f ? width : BrowseLayout.DirectoryFallbackWidth);
        float cellW = width > 0f ? width / cols : BrowseLayout.MoreColMin;
        var cells = new Element[items.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = BrowseTiles.Peek(ToModel(items[i], model), cellW);
        return BrowseLayout.StarGrid(cols, Spacing.M, Spacing.M, cells) with { Key = "browse-more-grid:" + cols };
    }

    // Same header grammar as the Charts Fold deck / a Featured Charts shelf — flush at FrameX, no tick+gap stepping
    // "Top" a rung right of Music/Podcasts.
    /// <summary>A band heading NAMES the row below it; it is not a peer of the destinations in it. It used to be
    /// <see cref="WaveeType.ModuleHeader"/> — the exact alias <c>BrowseTiles.Name</c> set its links in, and a rung
    /// SMALLER than the Display type <c>BrowseTiles.Word</c> used — so "Top" and "Music" read as one stack and nothing
    /// marked which half was pressable. The eyebrow rung (Caption/600 + tracking, already secondary-coloured) demotes
    /// the label to what it is, and the links take the plate; the two changes only work as a pair.
    /// <para>Sentence case, NOT caps: <see cref="WaveeType.Eyebrow"/> forbids a <c>ToUpper</c> on a localized string
    /// (Turkish dotted i, German ß) and the app is sentence-case throughout.</para></summary>
    static Element BandLabel(string label) =>
        WaveeType.Eyebrow(label) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    // Browse's categories as the shared tile model. A null model means the directory is inert (no navigation host
    // yet) — the cell still renders and still highlights, it just does nothing, exactly as before.
    static BrowseTileModel ToModel(BrowseCategory c, Model? model) =>
        BrowseTiles.ToModel(c, model?.OnOpenCategory, model?.OnOpenFeature);

    static readonly Action<HomeSection> NoopSection = static _ => { };

    /// <summary>The localised band heading. Membership is fixed in BrowseTaxonomy (uri-keyed, culture-independent);
    /// only the label translates, so the two concerns stay on opposite sides of the UI boundary. Never asked for
    /// Charts — that band carries its own Fold-deck header (see ChartsBand).</summary>
    static string GroupLabel(BrowseGroup g) => g switch
    {
        BrowseGroup.Top => Loc.Get(Strings.Browse.Top),
        BrowseGroup.ForYou => Loc.Get(Strings.Browse.ForYou),
        BrowseGroup.Genres => Loc.Get(Strings.Browse.Genres),
        BrowseGroup.MoodActivity => Loc.Get(Strings.Browse.MoodActivity),
        BrowseGroup.More => Loc.Get(Strings.Browse.More),
        _ => throw new InvalidOperationException("GroupLabel is never asked for Charts — see ChartsBand."),
    };

    // The loading shape: BrowseDirectorySeeds' fake categories carry Grouped through every real band so this renders
    // the SAME Body the loaded page uses. Nested Charts shimmers off HomeBrowseCards.ChartDeckSeed.
    static Element Skeleton()
    {
        Element ChartsBandAt(int index) => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Animate = WaveeEntrance.Row(index),
            Children = [HomeModules.FoldDeck(HomeBrowseCards.ChartDeckSeed, Loc.Get(Strings.Browse.Charts), NoopSection)],
        };

        return Body(BrowseDirectorySeeds.Categories, null, ChartsBandAt).Skeletonized(true);
    }
}
