using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Klankhuis.Hero.Controls;
using Microsoft.UI.Dispatching;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Helpers;
using Windows.UI;
using Wavee.UI.WinUI.Controls.Cards;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// Slim adapter POCO surfaced to <c>SideCard</c> instances in the side rail.
/// Three of these are always live (slot 0..2); empty slots are padded so the
/// XAML can statically reference <c>SideCards[0]</c>..<c>[2]</c> without
/// running through an <c>ItemsRepeater</c>.
/// </summary>
public sealed class SideCardItem
{
    public string Label { get; init; } = "";
    public string Eyebrow { get; init; } = "";
    public Color Accent { get; init; } = Color.FromArgb(255, 0x60, 0xCD, 0xFF);
    public Uri? ImageUri { get; init; }
    public string? NavigationUri { get; init; }
    public bool IsEmpty => string.IsNullOrEmpty(Label);
}

/// <summary>
/// Bridges <see cref="HomeViewModel"/> to the new hero band + region layout.
/// Owns:
/// <list type="bullet">
///   <item><see cref="HeroSlides"/> â€” feeds the <c>HeroCarousel</c>.</item>
///   <item><see cref="SideCard0"/>/<see cref="SideCard1"/>/<see cref="SideCard2"/>
///         â€” three fixed side-rail slots derived from the Shorts section.</item>
///   <item><see cref="Regions"/> â€” bucketed sections by
///         <see cref="HomeSectionClassifier"/>.</item>
/// </list>
/// Reactivity comes from the host: <c>Sections.CollectionChanged</c> and
/// <c>FeaturedItem</c> changes both dispatch a rebuild. The host's existing
/// chip-driven facet refetch swaps the underlying sections list, which flows
/// naturally through this adapter (empty regions get dropped).
/// </summary>
public sealed partial class HomeHeroAdapter : ObservableObject, IDisposable
{
    private static readonly Color FallbackAccent = Color.FromArgb(255, 0x60, 0xCD, 0xFF);

    // High cap rather than a tight one â€” the home feed never serves more than
    // ~15 sections, but the user wants "1 to N of each section" with no
    // implicit drops. Sections whose first item has no resolvable image are
    // skipped (see RebuildHeroSlides), so this is the *upper* bound.
    private const int MaxHeroSlides = 20;

    private readonly HomeViewModel _host;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _disposed;
    private INotifyCollectionChanged? _subscribedSections;

    [ObservableProperty]
    private SideCardItem _sideCard0 = new();

    [ObservableProperty]
    private SideCardItem _sideCard1 = new();

    [ObservableProperty]
    private SideCardItem _sideCard2 = new();

    /// <summary>True until <see cref="LoadBrowseAsync"/> completes for the first
    /// time. Drives the shimmer placeholder in <c>BrowseAllSection</c>.</summary>
    [ObservableProperty]
    private bool _isBrowseLoading = true;

    /// <summary>The grouped Browse All categories, empty until the lazy fetch lands.</summary>
    [ObservableProperty]
    private IList<BrowseAllGroup> _browseGroups = new List<BrowseAllGroup>();

    private bool _browseFetchTriggered;

    // HeroCarousel.ItemsSourceProperty rebuilds slides only on DP-change
    // (Klankhuis.Hero/Controls/HeroCarousel.cs ~line 131-139). It does NOT
    // subscribe to INotifyCollectionChanged on the bound list, so mutating an
    // ObservableCollection in place wouldn't trigger a slide rebuild â€” slides
    // would render as black with pagers but no overlay text. We reassign a
    // fresh List<> each rebuild so the DP setter fires and the carousel
    // rebuilds its slide visuals.
    [ObservableProperty]
    private IList<HeroCarouselItem> _heroSlides = new List<HeroCarouselItem>();

    public HomeHeroAdapter(HomeViewModel host)
    {
        _host = host;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        HeroPrimaryCtaCommand = new RelayCommand<HomeSectionItem>(PlayItemSafe);
        HeroSecondaryCtaCommand = new RelayCommand<HomeSectionItem>(NavigateToItemSafe);
        SideCardNavigationCommand = new RelayCommand<string>(NavigateToUriSafe);

        _host.PropertyChanged += OnHostPropertyChanged;
        AttachSectionsListener(_host.Sections);

        Rebuild();
    }

    public ObservableCollection<HomeRegion> Regions { get; } = [];

    public ICommand HeroPrimaryCtaCommand { get; }
    public ICommand HeroSecondaryCtaCommand { get; }
    public ICommand SideCardNavigationCommand { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.PropertyChanged -= OnHostPropertyChanged;
        DetachSectionsListener();
    }

    // â”€â”€ Reactivity â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        switch (e.PropertyName)
        {
            case nameof(HomeViewModel.Sections):
                AttachSectionsListener(_host.Sections);
                RequestRebuild();
                break;
            case nameof(HomeViewModel.FeaturedItem):
                Dispatch(RebuildHeroSlides);
                break;
            case nameof(HomeViewModel.IsLocalChipActive):
                // Filter toggle is user-driven and should feel instant â€” keep
                // it synchronous so the page doesn't visibly lag the chip
                // press. Goes through Dispatch (not the queued RequestRebuild
                // path) so the visible state change tracks the click.
                Dispatch(() => { RebuildRegions(); RebuildHeroSlides(); });
                break;
        }
    }

    private void OnSectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        // Coalesce the rebuild storm. PopulateSectionsChunkedAsync adds
        // sections one at a time with Task.Yield every 4, and
        // ApplyBackgroundRefresh does Extract â†’ ApplyDiff â†’ Restore which
        // fires N more events. Without coalescing, RebuildRegions ran ~20Ã—
        // per load on a mid-construction Regions collection â€” the path that
        // produced duplicate LocalFiles regions and tripped the layout-cycle
        // guard. One queued dispatch consolidates the whole burst.
        RequestRebuild();
    }

    private bool _rebuildQueued;

    private void RequestRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        _dispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _rebuildQueued = false;
                if (_disposed) return;
                Rebuild();
            });
    }

    private void AttachSectionsListener(INotifyCollectionChanged? newSource)
    {
        if (ReferenceEquals(_subscribedSections, newSource)) return;
        DetachSectionsListener();
        _subscribedSections = newSource;
        if (newSource is not null)
            newSource.CollectionChanged += OnSectionsCollectionChanged;
    }

    private void DetachSectionsListener()
    {
        if (_subscribedSections is null) return;
        _subscribedSections.CollectionChanged -= OnSectionsCollectionChanged;
        _subscribedSections = null;
    }

    private void Dispatch(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
            action();
        else
            _dispatcherQueue.TryEnqueue(() => { if (!_disposed) action(); });
    }

    // â”€â”€ Rebuild orchestration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void Rebuild()
    {
        if (_disposed) return;
        RebuildRegions();
        RebuildHeroSlides();
        RebuildSideCards();
    }

    // â”€â”€ Regions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void RebuildRegions()
    {
        // Bucket sections by classifier in feed order (preserves intra-region order).
        var bucket = new Dictionary<HomeRegionKind, List<HomeSection>>();
        foreach (var section in _host.Sections)
        {
            var kind = HomeSectionClassifier.ClassifyRegion(section);
            if (kind is null) continue;
            if (!bucket.TryGetValue(kind.Value, out var list))
            {
                list = new List<HomeSection>();
                bucket[kind.Value] = list;
            }
            list.Add(section);
        }

        // Local-only filter (driven by the "Local files" chip) collapses
        // the page down to just the LocalFiles band â€” every Spotify-sourced
        // region is dropped from the output.
        var ordered = _host.IsLocalChipActive
            ? new[] { HomeRegionKind.LocalFiles }
            : new[]
            {
                HomeRegionKind.Recents,
                HomeRegionKind.LocalFiles,
                HomeRegionKind.MadeForYou,
                HomeRegionKind.Discover,
                HomeRegionKind.Podcasts
            };

        // Reuse existing HomeRegion identity per Kind where possible so the
        // outer ItemsRepeater can recycle the rendered visual tree. Crucially,
        // TryAdd keeps the FIRST same-kind region and drops any duplicates â€”
        // the previous IndexOfRegion(kind, writeIndex) algorithm searched
        // forward only and could leave a stale region in front of writeIndex,
        // accumulating duplicate "Local files" bands across navigations. This
        // clear-and-rebuild pattern is bounded by HomeRegionKind (â‰¤5 entries)
        // so the perf delta vs. the old in-place Move is negligible.
        var existingByKind = new Dictionary<HomeRegionKind, HomeRegion>();
        foreach (var region in Regions)
            existingByKind.TryAdd(region.Kind, region);

        var desired = new List<HomeRegion>(ordered.Length);
        foreach (var kind in ordered)
        {
            if (!bucket.TryGetValue(kind, out var sectionsForKind) || sectionsForKind.Count == 0)
                continue;

            var region = existingByKind.TryGetValue(kind, out var reuse)
                ? reuse
                : HomeRegion.Create(kind);
            ReplaceSections(region.Sections, sectionsForKind);
            desired.Add(region);
        }

        // Reconcile Regions to `desired`. If the structure (order + identity)
        // already matches, no-op â€” steady-state rebuilds (item-only updates
        // inside existing regions) don't churn the visual tree at all. If
        // anything structural differs, full Clear + Add: ItemsRepeater
        // releases every realized container on Reset and re-realizes from
        // scratch.
        //
        // Why not in-place Replace/Add/Remove: the previous approach mutated
        // mid-pass, which left ItemsRepeater with stale realized containers
        // (a HomeRegionView bound to LocalFiles when Regions=[LocalFiles]
        // would never get Unloaded after the rebuild expanded Regions to 5
        // entries â€” its data item was reused by reference and the layout
        // didn't request the now-out-of-realization index, so ItemsRepeater
        // skipped recycling it). Result: two LocalFiles bands painted at
        // different Y offsets. Clear+Add side-steps the desync at the cost
        // of Nâ‰¤5 fresh realizations on structural change â€” negligible.
        bool sameStructure = Regions.Count == desired.Count;
        if (sameStructure)
        {
            for (int i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(Regions[i], desired[i]))
                {
                    sameStructure = false;
                    break;
                }
            }
        }

        if (!sameStructure)
        {
            Regions.Clear();
            foreach (var r in desired) Regions.Add(r);
        }
    }

    private static void ReplaceSections(ObservableCollection<HomeSection> target, List<HomeSection> source)
    {
        // Replace contents in place. We don't try to be clever with diffing here â€”
        // section identity inside a region is stable (Recents has 1, MadeForYou
        // has â‰¤4, Discover has â‰¤8, Podcasts has â‰¤8) and HomeFeedCache.ApplyDiff
        // already keeps the per-section item lists current. A full replace just
        // resyncs the bucketing.
        target.Clear();
        foreach (var s in source) target.Add(s);
    }

    // â”€â”€ Hero slides â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void RebuildHeroSlides()
    {
        var slides = new List<HeroCarouselItem>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Local-only filter â€” the hero never shows Spotify content while the
        // user has the "Local files" chip selected. FeaturedItem is the most-
        // recently-played Spotify item, so skip the "Pick up where you left
        // off" slide entirely; the per-section walk below sticks to the
        // local-section's items via the same SectionUri prefix filter.
        bool localOnly = _host.IsLocalChipActive;

        // Slide 0 â€” FeaturedItem ("Pick up where you left off")
        if (!localOnly && _host.FeaturedItem is { } featured && HasResolvableImage(featured.ImageUrl))
        {
            slides.Add(BuildSlide(
                featured,
                eyebrow: "PICK UP WHERE YOU LEFT OFF",
                primaryCta: "Play",
                secondaryCta: "Open"));
            if (!string.IsNullOrEmpty(featured.Uri))
                seenUris.Add(featured.Uri!);
        }

        // Slides 1..MaxHeroSlidesâˆ’1 â€” first item of each section in feed order.
        // Sections are taken straight from the host's collection; no podcast
        // filtering, no facet-based gating, no shuffling. Skips:
        //   - Shorts (drive the side-rail shortcut cards exclusively),
        //   - RecentlyPlayed (no editorial value for a hero slide),
        //   - URI duplicates (the Featured slide above already covers some),
        //   - items whose image URL doesn't resolve to a usable https:// URI â€”
        //     LoadedImageSurface only fetches http(s); a slide built with
        //     ImageUri = null would render as a black rectangle and (worse)
        //     still occupy a carousel slot the user can pan into.
        foreach (var section in _host.Sections)
        {
            if (slides.Count >= MaxHeroSlides) break;
            if (section.SectionType is HomeSectionType.Shorts or HomeSectionType.RecentlyPlayed) continue;
            if (section.Items.Count == 0) continue;
            if (localOnly && (string.IsNullOrEmpty(section.SectionUri)
                              || !section.SectionUri.StartsWith("wavee:local:", StringComparison.Ordinal)))
                continue;

            var first = section.Items[0];
            if (!HasResolvableImage(first.ImageUrl)) continue;
            if (!string.IsNullOrEmpty(first.Uri) && !seenUris.Add(first.Uri!)) continue;

            var sectionTitle = section.Title ?? "";
            slides.Add(BuildSlide(
                first,
                eyebrow: sectionTitle.ToUpperInvariant(),
                primaryCta: "Play",
                secondaryCta: "Open"));
        }

        // Transient-empty guard. RebuildHeroSlides fires on every
        // Sections.CollectionChanged via RequestRebuild's coalesced dispatcher
        // post. Three paths can race the rebuild past a moment where
        // _host.Sections is empty:
        //   â€¢ Cold load â€” PopulateSectionsChunkedAsync calls Sections.Clear()
        //     before chunking adds back in. Coalescing usually folds the
        //     rebuild past the clear, but a yield between chunks can let the
        //     queued rebuild run while Sections is still empty.
        //   â€¢ Nav-back from hibernation â€” ResumeAndRehydrateâ†’ApplyBackgroundRefresh
        //     â†’ApplyDiff mutates Sections one item at a time. Page nav also
        //     re-fires Bindings.Update() which re-reads HeroSlides for the
        //     carousel's ItemsSource DP â€” an empty list reassigned at that
        //     moment leaves the hero blank for a frame to ~1 sec until the
        //     next rebuild lands.
        //   â€¢ Background refresh â€” same Extractâ†’Diffâ†’Restore pattern.
        // In every case the eventual final rebuild produces correct slides;
        // it's just the transient empty assignment that flickers. If we have
        // no source sections to walk AND we already have slides on screen,
        // keep them. The next rebuild with real data overwrites cleanly.
        //
        // Intentional empties (Local-only chip with no local sections, the
        // user explicitly hiding everything) still apply, because in those
        // cases Sections is non-empty â€” every entry just gets filtered out by
        // the localOnly guard inside the foreach.
        if (slides.Count == 0 && _host.Sections.Count == 0 && HeroSlides.Count > 0)
            return;

        // Identity guard. If the new list is structurally equal to the
        // current (same item URIs in the same order), skip the reassignment
        // entirely. HeroCarousel.ItemsSource is a DP â€” reassigning to a
        // logically-identical list still tears the carousel down and
        // re-realises every slide, which on a fast Bindings.Update() pass
        // costs a visible frame of blank carousel.
        if (SlidesAreEquivalent(HeroSlides, slides))
            return;

        // Reassign so HeroCarousel.ItemsSource DP-change fires RebuildSlides().
        // In-place mutation would not trigger the carousel rebuild.
        HeroSlides = slides;
    }

    /// <summary>
    /// Cheap structural-equality check over the two slide lists â€” same count
    /// + same Tag URI in the same order. The slides themselves are rebuilt
    /// fresh on every RebuildHeroSlides call (BuildSlide allocates new
    /// HeroCarouselItem instances), so ReferenceEquals on items always fails;
    /// the URI comparison is what tells us the visible content didn't change.
    /// </summary>
    private static bool SlidesAreEquivalent(IList<HeroCarouselItem> previous, IList<HeroCarouselItem> next)
    {
        if (previous.Count != next.Count) return false;
        for (var i = 0; i < previous.Count; i++)
        {
            var prevUri = (previous[i].Tag as HomeSectionItem)?.Uri;
            var nextUri = (next[i].Tag as HomeSectionItem)?.Uri;
            if (!string.Equals(prevUri, nextUri, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// True only when the supplied raw URL resolves through
    /// <c>SpotifyImageHelper.ToHttpsUrl</c> to an absolute http(s) URI â€” i.e.
    /// the exact same gate Klankhuis's <c>LoadedImageSurface</c> fetch needs
    /// to succeed. Filtering on the raw string alone (null/empty) lets
    /// <c>spotify:image:&lt;invalid&gt;</c> and other unresolved forms through;
    /// the slide is then built with <c>ImageUri = null</c> and renders as a
    /// black rectangle the user can pan into.
    /// </summary>
    private static bool HasResolvableImage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var resolved = SpotifyImageHelper.ToHttpsUrl(raw);
        if (string.IsNullOrEmpty(resolved)) return false;
        return Uri.TryCreate(resolved, UriKind.Absolute, out _);
    }

    private HeroCarouselItem BuildSlide(HomeSectionItem item, string eyebrow, string primaryCta, string secondaryCta)
    {
        // Phase 11c lifted slide construction into a shared factory so
        // BrowseViewModel can build slides through the same code without
        // duplicating Tag / accent / image-url wiring.
        return Services.HeroSlideFactory.BuildSlide(
            item, eyebrow, primaryCta, secondaryCta,
            HeroPrimaryCtaCommand, HeroSecondaryCtaCommand);
    }

    // â”€â”€ Side rail â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void RebuildSideCards()
    {
        // Shorts section stays in HomeViewModel.Sections (no parser refactor needed);
        // the classifier returns null for Shorts so it doesn't appear in any region â€”
        // we just pull it out here for the three side-rail slots.
        var shorts = _host.Sections.FirstOrDefault(s => s.SectionType == HomeSectionType.Shorts);
        var items = shorts?.Items ?? new ObservableCollection<HomeSectionItem>();

        SideCard0 = items.Count > 0 ? BuildSideCard(items[0]) : new SideCardItem();
        SideCard1 = items.Count > 1 ? BuildSideCard(items[1]) : new SideCardItem();
        SideCard2 = items.Count > 2 ? BuildSideCard(items[2]) : new SideCardItem();
    }

    private SideCardItem BuildSideCard(HomeSectionItem src) => new()
    {
        Label = src.Title ?? "",
        Eyebrow = string.IsNullOrEmpty(src.Subtitle) ? "SHORTCUT" : src.Subtitle!,
        Accent = ParseColorOrFallback(src.ColorHex),
        ImageUri = TryMakeUri(src.ImageUrl),
        NavigationUri = src.Uri
    };

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static Color ParseColorOrFallback(string? hex)
    {
        if (TintColorHelper.TryParseHex(hex, out var c))
            return c;
        return FallbackAccent;
    }

    private static Uri? TryMakeUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Convert Spotify-internal image identifiers (spotify:image:..., spotify:mosaic:...)
        // to https://i.scdn.co/... URLs. The Klankhuis HeroCarousel + SideCard load images
        // via LoadedImageSurface, which only accepts http(s) â€” feeding it a spotify: URI
        // succeeds at Uri.TryCreate (the scheme is valid) but the surface fetch silently
        // fails and the cover never paints. SpotifyImageHelper is what the existing
        // SpotifyImageConverter uses for the rest of the app.
        var resolved = SpotifyImageHelper.ToHttpsUrl(raw);
        if (string.IsNullOrEmpty(resolved)) return null;
        return Uri.TryCreate(resolved, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static void NavigateToItemSafe(HomeSectionItem? item)
    {
        if (item is null) return;
        HomeViewModel.NavigateToItem(item, openInNewTab: false);
    }

    private static void NavigateToUriSafe(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        HomeViewModel.NavigateToItem(new HomeSectionItem { Uri = uri }, openInNewTab: false);
    }

    /// <summary>
    /// Primary CTA action â€” start playback of the item's URI as a new context.
    /// Mirrors <see cref="ContentCard"/>'s click-to-play path: resolve
    /// <see cref="IPlaybackService"/> from the IoC container and call
    /// <c>PlayContextAsync</c> on a worker thread so the UI thread doesn't
    /// stall on the Connect command round-trip.
    /// </summary>
    private static void PlayItemSafe(HomeSectionItem? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Uri)) return;
        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null) return;
        var uri = item.Uri!;
        _ = Task.Run(() => playback.PlayContextAsync(uri));
    }

    // â”€â”€ Browse All lazy load â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Fetches the Pathfinder browseAll surface, parses + groups, and surfaces
    /// the result via <see cref="BrowseGroups"/> / <see cref="IsBrowseLoading"/>.
    /// One-shot per adapter lifetime â€” repeat calls are no-ops. Triggered by
    /// the BrowseAllSection control when it nears the viewport.
    /// </summary>
    public async Task LoadBrowseAsync()
    {
        if (_browseFetchTriggered || _disposed) return;
        _browseFetchTriggered = true;

        var response = await _host.FetchBrowseAllAsync(CancellationToken.None).ConfigureAwait(false);
        if (_disposed) return;

        var items = BrowseAllParser.Extract(response);
        var groups = BrowseAllGrouper.Group(items);

        Dispatch(() =>
        {
            BrowseGroups = groups;
            IsBrowseLoading = false;
        });
    }
}
