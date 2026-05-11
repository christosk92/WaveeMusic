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
using Wavee.UI.WinUI.Helpers;
using Windows.UI;

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
///   <item><see cref="HeroSlides"/> — feeds the <c>HeroCarousel</c>.</item>
///   <item><see cref="SideCard0"/>/<see cref="SideCard1"/>/<see cref="SideCard2"/>
///         — three fixed side-rail slots derived from the Shorts section.</item>
///   <item><see cref="Regions"/> — bucketed sections by
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

    // High cap rather than a tight one — the home feed never serves more than
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
    // ObservableCollection in place wouldn't trigger a slide rebuild — slides
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

    // ── Reactivity ─────────────────────────────────────────────────────

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        switch (e.PropertyName)
        {
            case nameof(HomeViewModel.Sections):
                AttachSectionsListener(_host.Sections);
                Dispatch(Rebuild);
                break;
            case nameof(HomeViewModel.FeaturedItem):
                Dispatch(RebuildHeroSlides);
                break;
        }
    }

    private void OnSectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        Dispatch(Rebuild);
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

    // ── Rebuild orchestration ─────────────────────────────────────────

    private void Rebuild()
    {
        if (_disposed) return;
        RebuildRegions();
        RebuildHeroSlides();
        RebuildSideCards();
    }

    // ── Regions ───────────────────────────────────────────────────────

    private void RebuildRegions()
    {
        // Bucket sections by classifier. Preserve identity of existing region
        // instances so the outer ItemsRepeater can recycle their visual trees
        // instead of throwing them away on every refresh.
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

        var ordered = new[]
        {
            HomeRegionKind.Recents,
            HomeRegionKind.MadeForYou,
            HomeRegionKind.Discover,
            HomeRegionKind.Podcasts
        };

        // Walk the desired order; mutate Regions in place to minimise
        // ItemsRepeater churn.
        int writeIndex = 0;
        foreach (var kind in ordered)
        {
            if (!bucket.TryGetValue(kind, out var sectionsForKind) || sectionsForKind.Count == 0)
                continue;

            HomeRegion region;
            int existingAt = IndexOfRegion(kind, writeIndex);
            if (existingAt >= 0)
            {
                region = Regions[existingAt];
                if (existingAt != writeIndex)
                    Regions.Move(existingAt, writeIndex);
            }
            else
            {
                region = HomeRegion.Create(kind);
                Regions.Insert(writeIndex, region);
            }

            ReplaceSections(region.Sections, sectionsForKind);
            writeIndex++;
        }

        // Drop any trailing regions that no longer have content.
        while (Regions.Count > writeIndex)
            Regions.RemoveAt(Regions.Count - 1);
    }

    private int IndexOfRegion(HomeRegionKind kind, int startIndex)
    {
        for (int i = startIndex; i < Regions.Count; i++)
            if (Regions[i].Kind == kind) return i;
        return -1;
    }

    private static void ReplaceSections(ObservableCollection<HomeSection> target, List<HomeSection> source)
    {
        // Replace contents in place. We don't try to be clever with diffing here —
        // section identity inside a region is stable (Recents has 1, MadeForYou
        // has ≤4, Discover has ≤8, Podcasts has ≤8) and HomeFeedCache.ApplyDiff
        // already keeps the per-section item lists current. A full replace just
        // resyncs the bucketing.
        target.Clear();
        foreach (var s in source) target.Add(s);
    }

    // ── Hero slides ───────────────────────────────────────────────────

    private void RebuildHeroSlides()
    {
        var slides = new List<HeroCarouselItem>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Slide 0 — FeaturedItem ("Pick up where you left off")
        if (_host.FeaturedItem is { } featured && HasResolvableImage(featured.ImageUrl))
        {
            slides.Add(BuildSlide(
                featured,
                eyebrow: "PICK UP WHERE YOU LEFT OFF",
                primaryCta: "Play",
                secondaryCta: "Open"));
            if (!string.IsNullOrEmpty(featured.Uri))
                seenUris.Add(featured.Uri!);
        }

        // Slides 1..MaxHeroSlides−1 — first item of each section in feed order.
        // Sections are taken straight from the host's collection; no podcast
        // filtering, no facet-based gating, no shuffling. Skips:
        //   - Shorts (drive the side-rail shortcut cards exclusively),
        //   - RecentlyPlayed (no editorial value for a hero slide),
        //   - URI duplicates (the Featured slide above already covers some),
        //   - items whose image URL doesn't resolve to a usable https:// URI —
        //     LoadedImageSurface only fetches http(s); a slide built with
        //     ImageUri = null would render as a black rectangle and (worse)
        //     still occupy a carousel slot the user can pan into.
        foreach (var section in _host.Sections)
        {
            if (slides.Count >= MaxHeroSlides) break;
            if (section.SectionType is HomeSectionType.Shorts or HomeSectionType.RecentlyPlayed) continue;
            if (section.Items.Count == 0) continue;

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

        // Reassign so HeroCarousel.ItemsSource DP-change fires RebuildSlides().
        // In-place mutation would not trigger the carousel rebuild.
        HeroSlides = slides;
    }

    /// <summary>
    /// True only when the supplied raw URL resolves through
    /// <c>SpotifyImageHelper.ToHttpsUrl</c> to an absolute http(s) URI — i.e.
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

    // ── Side rail ─────────────────────────────────────────────────────

    private void RebuildSideCards()
    {
        // Shorts section stays in HomeViewModel.Sections (no parser refactor needed);
        // the classifier returns null for Shorts so it doesn't appear in any region —
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

    // ── Helpers ───────────────────────────────────────────────────────

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
        // via LoadedImageSurface, which only accepts http(s) — feeding it a spotify: URI
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
    /// Primary CTA action — start playback of the item's URI as a new context.
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

    // ── Browse All lazy load ──────────────────────────────────────────

    /// <summary>
    /// Fetches the Pathfinder browseAll surface, parses + groups, and surfaces
    /// the result via <see cref="BrowseGroups"/> / <see cref="IsBrowseLoading"/>.
    /// One-shot per adapter lifetime — repeat calls are no-ops. Triggered by
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
